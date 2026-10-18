using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// F4 for a carried thing: the poses its mover sent, drawn a moment in the past. A sample says
    /// which frame it is in (the hull's or real space); two samples of different frames are not
    /// interpolated, the later one is taken.
    /// </summary>
    internal sealed class ItemTrack
    {
        private struct Snap
        {
            public long Tick;
            public bool Aboard;
            public double X, Y, Z;
            public Quaternion Rot;
            public Vector3 Vel;
        }

        private const float MaxExtrapolateMs = 250f;
        private readonly SnapshotBuffer<Snap> _buf = new SnapshotBuffer<Snap>(s => s.Tick, 32);

        public void Clear() => _buf.Clear();

        public void Push(long tick, ItemMotionEntry e, Quaternion rot)
        {
            _buf.Push(new Snap
            {
                Tick = tick, Aboard = e.Aboard, X = e.X, Y = e.Y, Z = e.Z, Rot = rot,
                Vel = new Vector3(e.Vx, e.Vy, e.Vz),
            });
        }

        public bool Sample(long renderTick, out bool aboard, out double x, out double y, out double z, out Quaternion rot)
        {
            aboard = false; x = y = z = 0.0; rot = Quaternion.identity;
            if (_buf.Count == 0) return false;

            Snap first = _buf[0];
            Snap last = _buf[_buf.Count - 1];
            if (renderTick <= first.Tick) { Take(first, 0f, out aboard, out x, out y, out z, out rot); return true; }
            if (renderTick >= last.Tick)
            {
                Take(last, Mathf.Min(renderTick - last.Tick, MaxExtrapolateMs) / 1000f, out aboard, out x, out y, out z, out rot);
                return true;
            }
            for (int i = 0; i < _buf.Count - 1; i++)
            {
                Snap a = _buf[i], b = _buf[i + 1];
                if (renderTick < a.Tick || renderTick > b.Tick) continue;
                if (a.Aboard != b.Aboard) { Take(b, 0f, out aboard, out x, out y, out z, out rot); return true; }
                float span = b.Tick - a.Tick;
                float f = span > 0 ? (renderTick - a.Tick) / span : 0f;
                aboard = a.Aboard;
                x = a.X + (b.X - a.X) * f; y = a.Y + (b.Y - a.Y) * f; z = a.Z + (b.Z - a.Z) * f;
                rot = Quaternion.Slerp(a.Rot, b.Rot, f);
                return true;
            }
            Take(last, 0f, out aboard, out x, out y, out z, out rot);
            return true;
        }

        private static void Take(Snap s, float ahead, out bool aboard, out double x, out double y, out double z, out Quaternion rot)
        {
            aboard = s.Aboard; rot = s.Rot;
            x = s.X + s.Vel.x * ahead; y = s.Y + s.Vel.y * ahead; z = s.Z + s.Vel.z * ahead;
        }
    }

    /// <summary>
    /// Carrying. A thing a player has had in hand is moved by exactly one copy of the game at a
    /// time — its <b>mover</b>: the player who holds it, then the player who let it go, until it
    /// comes to rest. The mover runs the game's own code on it and sends its pose; every other
    /// copy holds it kinematic and draws it along those poses. At rest it is settled: every copy,
    /// the mover's too, keeps it still at the pose in its record until someone takes it again.
    ///
    /// A thing nobody has touched is left to each copy's own game.
    ///
    /// The host decides who holds a thing (the first to ask), keeps the records, and relays.
    /// </summary>
    internal sealed partial class ItemRegistry
    {
        /// <summary>A thing its mover saw within <see cref="MoveStep"/> of one spot for this long is at rest.</summary>
        private const float RestAfter = 1f;
        private const double MoveStep = 0.01;
        private const float MoveAngle = 1f;
        /// <summary>How long a record waits for its object after the world is entered before a stand-in is made.</summary>
        private const float StandInAfter = 10f;

        private static readonly FieldInfo AmountField = AccessTools.Field(typeof(ObjectInteractionHandler), "amount");
        private static readonly FieldInfo PurchasedField = AccessTools.Field(typeof(ObjectInteractionHandler), "purchased");
        private static readonly FieldInfo ParentField = AccessTools.Field(typeof(ObjectInteractionHandler), "parent");
        private static readonly FieldInfo ParentShipField = AccessTools.Field(typeof(ObjectInteractionHandler), "parentShip");
        private static readonly FieldInfo ObjectNameField = AccessTools.Field(typeof(ObjectInteractionHandler), "objectName");
        private static readonly FieldInfo StartNameField = AccessTools.Field(typeof(ObjectInteractionHandler), "startName");
        private static readonly FieldInfo ViewpointField = AccessTools.Field(typeof(ObjectInteractionHandler), "viewpoint");

        // In the local player's hand, as last told to the host.
        private readonly HashSet<ObjectInteractionHandler> _inHand = new HashSet<ObjectInteractionHandler>();
        // Held kinematic by the mod on this copy: the game's own handling of them is skipped.
        private readonly HashSet<ObjectInteractionHandler> _driven = new HashSet<ObjectInteractionHandler>();
        // Made by the mod for a record whose object this copy does not have.
        private readonly HashSet<ObjectInteractionHandler> _standIns = new HashSet<ObjectInteractionHandler>();
        // Records nothing here can stand in for yet, by key: when to look again.
        private readonly Dictionary<string, float> _noModel = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly List<ObjectInteractionHandler> _hands = new List<ObjectInteractionHandler>();
        private readonly List<ItemMotionEntry> _motion = new List<ItemMotionEntry>();
        private readonly List<Entry> _resting = new List<Entry>();
        // Things whose poses this copy has sent from this world: it really moves them.
        private readonly HashSet<uint> _ledHere = new HashSet<uint>();
        private uint _requestId;
        private float _nextMotionAt, _worldSince;
        private string _notice;

        public int Refused { get; private set; }

        private static bool CarryFields => FieldsFound && AmountField != null && PurchasedField != null &&
                                           ParentField != null && ParentShipField != null &&
                                           ObjectNameField != null && StartNameField != null;

        private static bool CarryReady => CarryFields && PatchHealth.StateOf("Items") == PatchHealthState.Ok;

        /// <summary>Something the local player should see: who has the thing it reached for.</summary>
        public string PopNotice()
        {
            string notice = _notice;
            _notice = null;
            return notice;
        }

        private void ClearCarry()
        {
            _inHand.Clear(); _driven.Clear(); _standIns.Clear(); _ledHere.Clear(); _noModel.Clear();
            _notice = null; Refused = 0;
        }

        private void ForgetCarry()
        {
            _inHand.RemoveWhere(obj => obj == null);
            _driven.RemoveWhere(obj => obj == null);
            _standIns.RemoveWhere(obj => obj == null);
        }

        // -----------------------------------------------------------------
        // Reading a thing
        // -----------------------------------------------------------------

        private static float AmountOf(ObjectInteractionHandler item) =>
            AmountField != null ? (float)AmountField.GetValue(item) : 0f;

        // "red_barrell_NEW (2)" and "red_barrell_NEW(Clone)" are both made from "red_barrell_NEW".
        private static string BaseName(string name)
        {
            name = (name ?? "").Trim();
            while (true)
            {
                if (name.EndsWith("(Clone)", StringComparison.Ordinal)) { name = name.Substring(0, name.Length - 7).TrimEnd(); continue; }
                int open = name.LastIndexOf(" (", StringComparison.Ordinal);
                if (open > 0 && name.EndsWith(")", StringComparison.Ordinal) &&
                    int.TryParse(name.Substring(open + 2, name.Length - open - 3), out _)) { name = name.Substring(0, open); continue; }
                return name;
            }
        }

        private static string ShipName(ShipHullMover ship) => string.IsNullOrEmpty(ship.id) ? "ship" : ship.id;

        private static bool Aboard(ObjectInteractionHandler item, ShipHullMover ship)
        {
            // In the hand the thing hangs under the camera, which is no part of the ship: it is
            // aboard when its holder is. Let go on deck, the game makes it a child of the hull —
            // one frame later, and until then it still hangs under the camera.
            Transform parent = item.transform.parent;
            if (item.isHeld || (parent != null && ViewpointField != null && ViewpointField.GetValue(item) as Transform == parent))
            {
                PlayerHandler player = GameAccess.Player;
                return player != null && player.ship == ship.gameObject;
            }
            return item.transform.IsChildOf(ship.transform);
        }

        /// <summary>Where the thing is now: hull-local aboard the player ship, real space elsewhere.</summary>
        private ItemPose PoseOf(ObjectInteractionHandler item)
        {
            var pose = new ItemPose { Tick = _net.Clock.ServerTick };
            Transform t = item.transform;
            ShipHullMover ship = GameAccess.ActiveShip();
            Quaternion q;
            if (ship != null && Aboard(item, ship))
            {
                pose.ShipId = ShipName(ship);
                Vector3 p = ship.transform.InverseTransformPoint(t.position);
                pose.X = p.x; pose.Y = p.y; pose.Z = p.z;
                q = Quaternion.Inverse(ship.transform.rotation) * t.rotation;
            }
            else
            {
                CoordSpace.LocalToReal(t.position, out pose.X, out pose.Y, out pose.Z);
                q = t.rotation;
            }
            pose.Qx = q.x; pose.Qy = q.y; pose.Qz = q.z; pose.Qw = q.w;
            return pose;
        }

        /// <summary>Whose copy moves the thing now; 0 when it is settled.</summary>
        private static uint Mover(ItemRecord r)
        {
            if (r.Status == ItemStatus.Held) return r.Holder;
            if (r.Status == ItemStatus.Free && (r.Flags & ItemFlags.Settled) == 0 && (r.Flags & ItemFlags.Touched) != 0) return r.Leader;
            return 0;
        }

        /// <summary>The thing another player holds, as this copy draws it; null when it holds none.</summary>
        internal Transform HeldBy(uint player)
        {
            foreach (Entry entry in _ids.Values)
                if (entry.Record.Status == ItemStatus.Held && entry.Record.Holder == player && entry.Object != null)
                    return entry.Object.transform;
            return null;
        }

        // The hand itself counts before the host has answered: the game has already put the thing there.
        private bool Leads(ItemRecord r, ObjectInteractionHandler item) => item.isHeld || Mover(r) == _net.MyNetId;

        // -----------------------------------------------------------------
        // The local hand
        // -----------------------------------------------------------------

        // The game's take, put-down, throw and its own letting go at four metres all end in the
        // isHeld flag, so the flag is what is watched — not the calls, which the game also makes
        // for things that are not in the hand.
        private void PollHands()
        {
            if (!CarryReady || (_net.IsClient && !_complete)) return;
            _hands.Clear();
            foreach (KeyValuePair<Component, string> pair in _objects)
                if (pair.Key is ObjectInteractionHandler item && item != null && item.isHeld != _inHand.Contains(item))
                    _hands.Add(item);

            uint me = _net.MyNetId;
            foreach (ObjectInteractionHandler item in _hands)
            {
                string key = _objects[item];
                Entry entry = Find(key);
                ItemRecord r = entry?.Record;
                if (item.isHeld)
                {
                    if (r != null && r.Status == ItemStatus.Held && r.Holder != me && _net.HasMember(r.Holder))
                    {
                        Refuse(item, r.Holder);
                        continue;
                    }
                    _inHand.Add(item);
                    // A guest does not wait for the host's answer to see its own hand.
                    if (r != null && !_net.IsHost)
                    {
                        r.Status = ItemStatus.Held; r.Holder = me; r.Leader = me;
                        r.Flags = (byte)((r.Flags | ItemFlags.Touched) & ~ItemFlags.Settled);
                        entry.Track.Clear();
                    }
                    Act(ItemActionKind.Take, item, key, entry);
                }
                else
                {
                    _inHand.Remove(item);
                    // Only the hand that paid can be refunded: a later hand on it bought nothing.
                    s_bought.Remove(item);
                    if (r != null && r.Status == ItemStatus.Deleted) continue;
                    if (r != null && !_net.IsHost)
                    {
                        r.Status = ItemStatus.Free; r.Holder = 0; r.Leader = me;
                        r.Flags = (byte)((r.Flags | ItemFlags.Touched) & ~ItemFlags.Settled);
                    }
                    if (entry != null) { entry.MovedAt = Time.unscaledTime; entry.Ref = null; entry.Prev = null; }
                    Act(ItemActionKind.Drop, item, key, entry);
                }
            }
        }

        private void Act(ItemActionKind action, ObjectInteractionHandler item, string key, Entry entry)
        {
            var msg = new ItemActionMsg
            {
                RequestId = ++_requestId, Action = action, Kind = ItemKind.Portable, Pose = PoseOf(item),
                Flags = (bool)PurchasedField.GetValue(item) ? ItemFlags.Purchased : (byte)0,
                Amount = AmountOf(item), ItemType = TypeField.GetValue(item).ToString(),
            };
            if (entry != null && entry.Record.NetId != 0) msg.NetId = entry.Record.NetId;
            else
            {
                // The host has no record of it: a thing of an island the host has not loaded.
                msg.Key = key;
                msg.Name = StartNameField.GetValue(item) as string ?? item.GetName() ?? "";
                msg.Prefab = BaseName(item.name);
            }
            if (_net.IsHost) HostAct(_net.MyNetId, msg);
            else _net.SendToHost(msg, DeliveryMethod.ReliableOrdered);
        }

        private struct Bought { public float At, Cost; }
        private const float RefundWithin = 10f;
        // Things this copy's hand paid for just now, until the host's answer.
        private static readonly Dictionary<ObjectInteractionHandler, Bought> s_bought = new Dictionary<ObjectInteractionHandler, Bought>();

        // Another player had it first: the local hand is opened again.
        private void Refuse(ObjectInteractionHandler item, uint holder)
        {
            // Bought by both hands at once: the one whose hand the host kept has paid.
            if (s_bought.TryGetValue(item, out Bought bought))
            {
                s_bought.Remove(item);
                DataPersistenceManager dp = GameAccess.Dp;
                if (dp != null && dp.gameData != null && Time.unscaledTime - bought.At < RefundWithin)
                {
                    dp.gameData.tearCount += bought.Cost;
                    CoopTrace.Note("Items", "refunded", item.name, bought.Cost.ToString("F0"));
                }
            }
            item.Hold(false);
            if (item.uiPrompt != null && item.uiPrompt.activeSelf) item.uiPrompt.SetActive(false);
            InteractionController controller = InteractionController.interactionInstance;
            if (controller != null) controller.ExternalStopInteractingAndUnlock();
            _inHand.Remove(item);
            Refused++;
            CoopTrace.Note("Items", "hand opened", item.name, "held by " + holder);
            _notice = _net.GetPlayerName(holder) + " is carrying this";
        }

        // The ship's hold took the thing on this copy (the game is about to destroy it).
        private void Consumed(ObjectInteractionHandler item)
        {
            if (!CarryReady || (_net.IsClient && !_complete)) return;
            if (!_objects.TryGetValue(item, out string key)) return;
            Entry entry = Find(key);
            if (entry != null && entry.Record.Status == ItemStatus.Deleted) return;
            _inHand.Remove(item);
            Act(ItemActionKind.Delete, item, key, entry);
            if (entry != null && !_net.IsHost) entry.Record.Status = ItemStatus.Deleted;
        }

        // A guest's world was loaded again with a thing in its hand, or with one it had let go and was
        // still moving: the host still has it so. The new world's copy of the thing lies where its
        // scene put it, so its poses must not be sent: the thing rests where the host last saw it.
        private void ReleaseStaleHolds()
        {
            if (!CarryReady) return;
            foreach (Entry entry in _ids.Values)
            {
                ItemRecord r = entry.Record;
                if (r.Kind != ItemKind.Portable || Mover(r) != _net.MyNetId) continue;
                var item = entry.Object as ObjectInteractionHandler;
                if (item != null && item.isHeld) { _inHand.Add(item); continue; }
                // A manifest asked for again in the same world: the thing is moved here for real.
                if (item != null && r.Status != ItemStatus.Held && _ledHere.Contains(r.NetId)) continue;
                bool held = r.Status == ItemStatus.Held;
                ItemPose pose = r.Pose;
                // Settled here too, at once: until the host answers this copy would lead the thing.
                r.Status = ItemStatus.Free; r.Holder = 0; r.Flags |= ItemFlags.Settled;
                foreach (ItemActionKind action in held ? new[] { ItemActionKind.Drop, ItemActionKind.Movement } : new[] { ItemActionKind.Movement })
                    _net.SendToHost(new ItemActionMsg
                    {
                        RequestId = ++_requestId, Action = action, Kind = ItemKind.Portable, NetId = r.NetId, Pose = pose,
                    }, DeliveryMethod.ReliableOrdered);
            }
        }

        // -----------------------------------------------------------------
        // Host: who holds what
        // -----------------------------------------------------------------

        public void OnAction(ItemActionMsg msg, NetPeer from)
        {
            if (!_net.IsHost) return;
            uint player = _net.PlayerNetIdForPeer(from);
            if (player == 0) return;
            if (msg.Action == ItemActionKind.Manifest) { SendManifest(player); return; }
            if (!EnsureWorld()) return;
            HostAct(player, msg);
        }

        private void HostAct(uint player, ItemActionMsg msg)
        {
            if (msg.Kind != ItemKind.Portable) return;
            Entry entry = msg.NetId != 0 ? Find(msg.NetId) : Find(msg.Key ?? "");
            if (entry == null)
            {
                if (msg.NetId != 0 || string.IsNullOrEmpty(msg.Key)) return;
                // Known to the guest only: its description becomes the record. This host gets the
                // object when it loads that island, or makes a stand-in when the thing comes aboard.
                entry = new Entry
                {
                    Record = new ItemRecord
                    {
                        NetId = _nextId++, Key = msg.Key, Kind = ItemKind.Portable, ItemType = msg.ItemType ?? "",
                        Name = msg.Name ?? "", Prefab = msg.Prefab ?? "", Amount = msg.Amount, Pose = msg.Pose,
                    }
                };
                _ids.Add(entry.Record.NetId, entry); _keys[msg.Key] = entry;
                if (_owners.TryGetValue(msg.Key, out Component owner) && owner != null) Bind(entry, owner);
            }

            ItemRecord r = entry.Record;
            if (r.Kind != ItemKind.Portable) return;
            uint mover = Mover(r);
            bool mine = r.Status == ItemStatus.Held && r.Holder == player;
            switch (msg.Action)
            {
                case ItemActionKind.Take:
                    if (r.Status == ItemStatus.Deleted ||
                        (r.Status == ItemStatus.Held && r.Holder != player && _net.HasMember(r.Holder)))
                    {
                        Refused++;
                        CoopTrace.Note("Items", "refused", "#" + r.NetId, "take by " + player + ", held by " + r.Holder);
                        Answer(player, entry, msg.RequestId);
                        return;
                    }
                    r.Status = ItemStatus.Held; r.Holder = player; r.Leader = player;
                    r.Flags = (byte)((r.Flags | ItemFlags.Touched | (msg.Flags & ItemFlags.Purchased)) & ~ItemFlags.Settled);
                    break;
                case ItemActionKind.Drop:
                case ItemActionKind.Throw:
                    if (!mine) { Answer(player, entry, msg.RequestId); return; }
                    r.Status = ItemStatus.Free; r.Holder = 0; r.Leader = player; r.Pose = msg.Pose;
                    r.Flags = (byte)(r.Flags & ~ItemFlags.Settled);
                    break;
                case ItemActionKind.Movement:
                    if (r.Status != ItemStatus.Free || mover != player) return;
                    r.Pose = msg.Pose;
                    r.Flags |= ItemFlags.Settled;
                    break;
                case ItemActionKind.Delete:
                    if (r.Status == ItemStatus.Deleted) return;
                    r.Status = ItemStatus.Deleted; r.Holder = 0;
                    break;
                default:
                    return;
            }
            r.Revision++;
            Publish(entry, msg.RequestId, mover);
        }

        private void Answer(uint player, Entry entry, uint requestId)
        {
            if (player != _net.MyNetId)
                _net.SendTo(player, new ItemStateMsg { RequestId = requestId, Record = entry.Record }, DeliveryMethod.ReliableOrdered);
        }

        private void Publish(Entry entry, uint requestId, uint moverBefore)
        {
            _net.Broadcast(new ItemStateMsg { RequestId = requestId, Record = entry.Record }, DeliveryMethod.ReliableOrdered);
            Changed(entry, moverBefore);
        }


        // A player left: what it held or had let go is nobody's now.
        private void HostPlayerGone(uint player)
        {
            if (!_net.IsHost || !_active) return;
            foreach (Entry entry in _ids.Values.ToArray())
            {
                ItemRecord r = entry.Record;
                if (r.Kind != ItemKind.Portable || Mover(r) != player) continue;
                r.Status = ItemStatus.Free; r.Holder = 0;
                // With the object here this copy's physics takes it on from where it was last seen;
                // without it the thing rests there.
                if (entry.Object != null) r.Leader = _net.MyNetId;
                else { r.Leader = 0; r.Flags |= ItemFlags.Settled; }
                r.Revision++;
                Publish(entry, 0, player);
            }
        }

        // -----------------------------------------------------------------
        // A record changed (every copy)
        // -----------------------------------------------------------------

        private void Changed(Entry entry, ItemRecord before) => Changed(entry, before != null ? Mover(before) : 0);

        private void Changed(Entry entry, uint moverBefore)
        {
            ItemRecord r = entry.Record;
            if (r.Kind != ItemKind.Portable) return;
            uint mover = Mover(r);
            // Poses of another mover, or of before a rest, say nothing about where it is now.
            if (mover != moverBefore) entry.Track.Clear();
            if (mover == _net.MyNetId && moverBefore != mover)
            {
                entry.MovedAt = Time.unscaledTime; entry.Ref = null; entry.Prev = null;
            }

            var item = entry.Object as ObjectInteractionHandler;
            if (item == null) return;
            if (r.Status == ItemStatus.Deleted)
            {
                _inHand.Remove(item);
                UnityEngine.Object.Destroy(item.gameObject);
                return;
            }
            Dress(r, item);
            if (item.isHeld && r.Status == ItemStatus.Held && r.Holder != _net.MyNetId && _net.HasMember(r.Holder) && CarryReady)
                Refuse(item, r.Holder);
        }

        // What a record says about the thing itself, not about where it is.
        private static void Dress(ItemRecord r, ObjectInteractionHandler item)
        {
            if (!CarryFields) return;
            if ((r.Flags & ItemFlags.Purchased) != 0 && !(bool)PurchasedField.GetValue(item))
                PurchasedField.SetValue(item, true);
            // A sack of food or drink names itself at random on every copy.
            if (!string.IsNullOrEmpty(r.Name) && StartNameField.GetValue(item) as string != r.Name)
            {
                StartNameField.SetValue(item, r.Name);
                ObjectNameField.SetValue(item, r.Name);
            }
        }

        // -----------------------------------------------------------------
        // Poses: sent by the mover, drawn by the others
        // -----------------------------------------------------------------

        /// <summary>
        /// Every frame from LateUpdate, after the avatars: by then the ship's transform and the
        /// local hand hold the frame's rendered pose.
        /// </summary>
        public void LateTick()
        {
            if (!_active || !CarryReady) return;
            float now = Time.unscaledTime;
            bool report = now >= _nextMotionAt;
            if (report) _nextMotionAt = now + 1f / Mathf.Clamp(Plugin.SendRateHz.Value, 1, 60);
            long tick = _net.Clock.ServerTick;
            long renderTick = tick - Plugin.InterpDelayMs.Value;
            ShipHullMover ship = GameAccess.ActiveShip();

            _motion.Clear(); _resting.Clear();
            foreach (Entry entry in _ids.Values)
            {
                ItemRecord r = entry.Record;
                if (r.Kind != ItemKind.Portable || r.Status == ItemStatus.Deleted) continue;
                var item = entry.Object as ObjectInteractionHandler;
                if (item == null) continue;
                if (Leads(r, item))
                {
                    Undrive(item);
                    if (report) Report(entry, item, now);
                }
                else if ((r.Flags & ItemFlags.Touched) == 0) Undrive(item);
                else Drive(entry, item, ship, renderTick);
            }

            for (int i = 0; i < _motion.Count; i += ItemMotionMsg.MaxEntries)
            {
                var msg = new ItemMotionMsg
                {
                    Tick = tick,
                    Entries = _motion.GetRange(i, Math.Min(ItemMotionMsg.MaxEntries, _motion.Count - i)).ToArray(),
                };
                if (_net.IsHost) _net.Broadcast(msg, DeliveryMethod.Unreliable);
                else _net.SendToHost(msg, DeliveryMethod.Unreliable);
            }
            foreach (Entry entry in _resting) Settle(entry);
        }

        private void Report(Entry entry, ObjectInteractionHandler item, float now)
        {
            ItemRecord r = entry.Record;
            ItemPose pose = PoseOf(item);
            bool aboard = pose.ShipId.Length > 0;

            ItemPose prev = entry.Prev;
            float dt = now - entry.PrevTime;
            if (prev != null && (prev.ShipId.Length > 0) == aboard && dt > 0.001f && dt < 0.5f)
            {
                var v = new Vector3((float)(pose.X - prev.X), (float)(pose.Y - prev.Y), (float)(pose.Z - prev.Z)) / dt;
                // Faster than a thrown barrel flies: a jump, not motion.
                if (v.sqrMagnitude < 60f * 60f) { pose.Vx = v.x; pose.Vy = v.y; pose.Vz = v.z; }
            }
            entry.Prev = pose; entry.PrevTime = now;

            ItemPose at = entry.Ref;
            // A paused world holds a thrown thing in the air: that is not rest.
            if (at == null || Time.timeScale == 0f || (at.ShipId.Length > 0) != aboard ||
                Math.Abs(pose.X - at.X) > MoveStep || Math.Abs(pose.Y - at.Y) > MoveStep || Math.Abs(pose.Z - at.Z) > MoveStep ||
                Quaternion.Angle(Rotation(pose), Rotation(at)) > MoveAngle)
            {
                entry.Ref = pose; entry.MovedAt = now;
            }

            r.Pose = pose;
            if (r.NetId != 0) _ledHere.Add(r.NetId);
            if (r.NetId != 0)
                _motion.Add(new ItemMotionEntry
                {
                    NetId = r.NetId, Aboard = aboard, X = pose.X, Y = pose.Y, Z = pose.Z,
                    Qx = pose.Qx, Qy = pose.Qy, Qz = pose.Qz, Qw = pose.Qw, Vx = pose.Vx, Vy = pose.Vy, Vz = pose.Vz,
                });

            // Let go and still: it rests. A thing fixed to the rowboat moves with a boat that is
            // not shared yet, and is left to the game.
            if (!item.isHeld && r.Status == ItemStatus.Free && r.Leader == _net.MyNetId && r.NetId != 0 &&
                now - entry.MovedAt > RestAfter && item.GetComponent<FixedJoint>() == null)
                _resting.Add(entry);
        }

        private static Quaternion Rotation(ItemPose pose) => new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);

        private void Settle(Entry entry)
        {
            ItemRecord r = entry.Record;
            if (_net.IsHost)
            {
                uint mover = Mover(r);
                r.Flags |= ItemFlags.Settled;
                r.Revision++;
                Publish(entry, 0, mover);
                return;
            }
            r.Flags |= ItemFlags.Settled;
            _net.SendToHost(new ItemActionMsg
            {
                RequestId = ++_requestId, Action = ItemActionKind.Movement, Kind = ItemKind.Portable, NetId = r.NetId, Pose = r.Pose,
            }, DeliveryMethod.ReliableOrdered);
        }

        public void OnMotion(ItemMotionMsg msg, NetPeer from)
        {
            if (!_active) return;
            uint sender = _net.IsHost ? _net.PlayerNetIdForPeer(from) : 0;
            if (_net.IsHost && sender == 0) return;
            List<ItemMotionEntry> relay = null;
            foreach (ItemMotionEntry e in msg.Entries)
            {
                Entry entry = Find(e.NetId);
                if (entry == null) continue;
                ItemRecord r = entry.Record;
                if (r.Kind != ItemKind.Portable || r.Status == ItemStatus.Deleted) continue;
                uint mover = Mover(r);
                // The host takes a thing's pose from its mover only; a guest never from the wire
                // for a thing it moves itself.
                if (_net.IsHost ? mover != sender : mover == _net.MyNetId) continue;

                var rot = new Quaternion(e.Qx, e.Qy, e.Qz, e.Qw);
                float norm = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
                if (norm < 0.5f || norm > 1.5f || float.IsNaN(norm)) continue;
                entry.Track.Push(msg.Tick, e, rot.normalized);
                if (!_net.IsHost) continue;
                // The record is what a release falls back on: never a pose older than the one it has.
                if (r.Pose == null || msg.Tick >= r.Pose.Tick)
                {
                    ShipHullMover ship = GameAccess.ActiveShip();
                    r.Pose = new ItemPose
                    {
                        ShipId = e.Aboard ? (ship != null ? ShipName(ship) : "ship") : "", Tick = msg.Tick,
                        X = e.X, Y = e.Y, Z = e.Z, Qx = e.Qx, Qy = e.Qy, Qz = e.Qz, Qw = e.Qw,
                    };
                }
                (relay ?? (relay = new List<ItemMotionEntry>())).Add(e);
            }
            if (relay != null)
                _net.Broadcast(new ItemMotionMsg { Tick = msg.Tick, Entries = relay.ToArray() },
                               DeliveryMethod.Unreliable, readyOnly: true, exceptNetId: sender);
        }

        // This copy does not move the thing: hold it still and put it where its mover has it.
        private void Drive(Entry entry, ObjectInteractionHandler item, ShipHullMover ship, long renderTick)
        {
            ItemRecord r = entry.Record;
            bool held = r.Status == ItemStatus.Held;
            if (_driven.Add(item))
            {
                // What the game does itself when a thing is taken in hand.
                FixedJoint joint = item.GetComponent<FixedJoint>();
                if (joint != null) UnityEngine.Object.Destroy(joint);
                if (item.cs != null) { item.cs.occupied = false; item.cs = null; }
            }
            Rigidbody body = item.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic) body.isKinematic = true;
            // In a hand the game makes the thing a trigger, so it does not push its carrier about.
            Collider collider = item.GetComponent<Collider>();
            if (collider != null && collider.isTrigger != held) collider.isTrigger = held;

            bool aboard;
            double x, y, z;
            Quaternion rot;
            bool settled = !held && (r.Flags & ItemFlags.Settled) != 0;
            if (settled || !entry.Track.Sample(renderTick, out aboard, out x, out y, out z, out rot))
            {
                // No pose of this hand has come yet: the thing stays where it is until one does.
                if (held) return;
                aboard = r.Pose.ShipId.Length > 0;
                x = r.Pose.X; y = r.Pose.Y; z = r.Pose.Z;
                rot = Rotation(r.Pose);
                float norm = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
                if (norm < 0.5f || norm > 1.5f || float.IsNaN(norm)) return;
            }

            Transform t = item.transform;
            Transform parent = null;
            Vector3 world;
            if (aboard)
            {
                if (ship == null) return;
                parent = ship.transform;
                world = parent.TransformPoint(new Vector3((float)x, (float)y, (float)z));
                rot = parent.rotation * rot;
            }
            else world = CoordSpace.RealToLocal(x, y, z);

            if (t.parent != parent)
            {
                t.SetParent(parent, true);
                // Where the game puts the thing back when the mod lets go of it.
                ParentField.SetValue(item, parent);
            }
            // Out of its island's scene: that scene is unloaded a kilometre away, on each copy by
            // itself, and would take a carried thing with it.
            if (parent == null && item.gameObject.scene.handle != _worldHandle)
                SceneManager.MoveGameObjectToScene(item.gameObject, SceneManager.GetSceneByBuildIndex(GameAccess.WorldScene));
            if ((t.position - world).sqrMagnitude > 1e-8f || Quaternion.Angle(t.rotation, rot) > 0.01f)
                t.SetPositionAndRotation(world, rot);
        }

        private void Undrive(ObjectInteractionHandler item)
        {
            if (!_driven.Remove(item) || item.isHeld) return;
            ParentField.SetValue(item, item.transform.parent);
        }

        // -----------------------------------------------------------------
        // Stand-ins
        // -----------------------------------------------------------------

        // A carried thing whose object this copy does not have: the game does not save such things,
        // so a late guest has none, and neither has a host that never loaded the thing's island. A
        // copy of a like thing stands in for it. When the island is loaded later, its own copy of
        // the thing is the duplicate and is removed (see Register).
        private void SpawnStandIns()
        {
            if (!CarryReady || (_net.IsClient && !_complete)) return;
            if (Time.unscaledTime - _worldSince < StandInAfter) return;
            Dictionary<string, ObjectInteractionHandler> models = null;
            foreach (Entry entry in _ids.Values)
            {
                ItemRecord r = entry.Record;
                if (entry.Object != null || r.Kind != ItemKind.Portable || r.Status == ItemStatus.Deleted ||
                    (r.Flags & ItemFlags.Touched) == 0 || string.IsNullOrEmpty(r.Prefab)) continue;
                if (_owners.TryGetValue(r.Key, out Component owner) && owner != null) continue;
                if (models == null)
                {
                    models = new Dictionary<string, ObjectInteractionHandler>(StringComparer.Ordinal);
                    foreach (Component obj in _objects.Keys)
                        if (obj is ObjectInteractionHandler model && model != null && !model.isHeld)
                        {
                            string name = BaseName(model.name);
                            if (!models.ContainsKey(name) || _standIns.Contains(models[name])) models[name] = model;
                        }
                }
                // The yield of a hunt has no like thing lying about: it is made from what the animal leaves.
                GameObject source = models.TryGetValue(r.Prefab, out ObjectInteractionHandler like) ? like.gameObject : HuntSync.YieldPrefab(r.Prefab);
                if (source == null) source = Unseen(r, models);
                if (source == null) continue;
                if (!Enum.TryParse(r.ItemType, out ProvisionsController.ProvisionType type)) continue;

                ObjectInteractionHandler item = Make(source, r.Prefab, type, r.Name, r.Amount);

                // Known to the registry before its Start is seen, or it would be taken for a new thing.
                _objects[item] = r.Key; _owners[r.Key] = item; _standIns.Add(item);
                entry.Object = item;
                Plugin.Log.LogInfo("[Items] Stand-in made for '" + r.Name + "' (" + r.Prefab + ", NetId " + r.NetId + ")");
            }
        }

        // A kind of thing this copy has not loaded, such as the yield of an animal of an island far
        // away: the game's own asset when it is in memory, else any thing of the same type, so that
        // the thing can at least be seen and taken.
        private GameObject Unseen(ItemRecord r, Dictionary<string, ObjectInteractionHandler> models)
        {
            if (_noModel.TryGetValue(r.Key, out float retryAt) && Time.unscaledTime < retryAt) return null;
            foreach (ObjectInteractionHandler asset in Resources.FindObjectsOfTypeAll<ObjectInteractionHandler>())
                if (asset != null && !asset.gameObject.scene.IsValid() && asset.transform.parent == null && asset.name == r.Prefab)
                    return asset.gameObject;
            foreach (ObjectInteractionHandler model in models.Values)
                if (TypeField.GetValue(model).ToString() == r.ItemType)
                {
                    Plugin.Log.LogInfo("[Items] No '" + r.Prefab + "' here: '" + model.name + "' stands in for '" + r.Name + "'");
                    return model.gameObject;
                }
            if (!_noModel.ContainsKey(r.Key))
                Plugin.Log.LogWarning("[Items] Nothing here can stand in for '" + r.Name + "' (" + r.Prefab + ", NetId " + r.NetId + "); it is not seen on this copy");
            _noModel[r.Key] = Time.unscaledTime + 5f;
            return null;
        }

        // A copy of a like thing, told what it is before its Start.
        private static ObjectInteractionHandler Make(GameObject source, string prefab, ProvisionsController.ProvisionType type, string name, float amount)
        {
            GameObject made = UnityEngine.Object.Instantiate(source);
            made.name = prefab;
            SceneManager.MoveGameObjectToScene(made, SceneManager.GetSceneByBuildIndex(GameAccess.WorldScene));
            foreach (FixedJoint joint in made.GetComponents<FixedJoint>()) UnityEngine.Object.Destroy(joint);
            ObjectInteractionHandler item = made.GetComponent<ObjectInteractionHandler>();
            // What the thing is comes from what it is made for, not from the model.
            item.isHeld = false; item.cs = null; item.uiPrompt = null;
            item.allowPurchase = false; item.namePreset = true;
            item.SetType(type); item.SetName(name);
            AmountField.SetValue(item, amount);
            PurchasedField.SetValue(item, true);
            return item;
        }

        // -----------------------------------------------------------------
        // Things made to be shared
        // -----------------------------------------------------------------

        // A thing made while the game runs has no scene path of its own to be named by. One made
        // through here is given a name when it is made, and from its Start on it is treated like
        // the yield of a hunt: a touched thing, which the other copies show as a stand-in.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ObjectInteractionHandler, string> MadeKeys =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ObjectInteractionHandler, string>();

        private static string MadeKey(ObjectInteractionHandler item) =>
            MadeKeys.TryGetValue(item, out string key) ? key : null;

        /// <summary>
        /// Dev: a new thing like the nearest one, at a point of the scene, for every copy. The others
        /// show it once they have a like thing loaded to copy.
        /// </summary>
        internal string DevSpawn(Vector3 at)
        {
            if (!_active || !CarryReady) return "Things are not shared now (a session must run)";
            if (_net.IsClient && !_complete) return "The host's list of things has not come yet";
            ObjectInteractionHandler model = null;
            float distance = float.MaxValue;
            foreach (Component obj in _objects.Keys)
            {
                if (!(obj is ObjectInteractionHandler like) || like == null || like.isHeld || !like.gameObject.activeInHierarchy) continue;
                float d = Vector3.Distance(like.transform.position, at);
                if (d < distance) { distance = d; model = like; }
            }
            if (model == null) return "No thing is loaded here to copy";

            string prefab = BaseName(model.name);
            string name = StartNameField.GetValue(model) as string ?? "";
            ObjectInteractionHandler item = Make(model.gameObject, prefab, (ProvisionsController.ProvisionType)TypeField.GetValue(model), name, AmountOf(model));
            item.transform.SetPositionAndRotation(at, Quaternion.identity);
            // The model may be one the mod holds still.
            Rigidbody body = item.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = false;
            // The same name must never be given twice, by any copy, in any session of this world.
            MadeKeys.Add(item, "made:" + Guid.NewGuid().ToString("N"));
            return "Made '" + name + "' (" + prefab + "), like the one " + distance.ToString("F0") + " m away";
        }

        private string DescribeCarry()
        {
            if (PatchHealth.StateOf("Items") != PatchHealthState.Ok) return "   carrying off: the Items patch failed";
            if (!CarryFields) return "   carrying off: the game's item fields were not found";
            int held = 0, moving = 0, settled = 0;
            foreach (Entry entry in _ids.Values)
            {
                ItemRecord r = entry.Record;
                if (r.Kind != ItemKind.Portable || r.Status == ItemStatus.Deleted || (r.Flags & ItemFlags.Touched) == 0) continue;
                if (r.Status == ItemStatus.Held) held++;
                else if ((r.Flags & ItemFlags.Settled) != 0) settled++;
                else moving++;
            }
            return "   | in hands " + held + "   moving " + moving + "   at rest " + settled +
                   (_standIns.Count > 0 ? "   stand-ins " + _standIns.Count : "") + (Refused > 0 ? "   refused " + Refused : "");
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            harmony.Patch(Method("ProcessHold"), prefix: new HarmonyMethod(typeof(ItemRegistry), nameof(HoldPrefix)));
            harmony.Patch(Method("OnTriggerEnter"), prefix: new HarmonyMethod(typeof(ItemRegistry), nameof(TriggerPrefix)));
            harmony.Patch(Method("OnCollisionEnter"), prefix: new HarmonyMethod(typeof(ItemRegistry), nameof(CollisionPrefix)));
            harmony.Patch(Method("Purchase"), prefix: new HarmonyMethod(typeof(ItemRegistry), nameof(PurchasePrefix)),
                          postfix: new HarmonyMethod(typeof(ItemRegistry), nameof(PurchasePostfix)));
        }

        // The game takes the price when the hand closes, before the host has said whose the thing is.
        private static void PurchasePrefix(ObjectInteractionHandler __instance, out bool __state)
        {
            bool before = true;
            PatchGuard.Run(() => before = (bool)PurchasedField.GetValue(__instance), PatchError);
            __state = before;
        }

        private static void PurchasePostfix(ObjectInteractionHandler __instance, bool __state)
        {
            PatchGuard.Run(() =>
            {
                if (__state || !(bool)PurchasedField.GetValue(__instance)) return;
                foreach (ObjectInteractionHandler old in s_bought.Where(p => p.Key == null || Time.unscaledTime - p.Value.At > RefundWithin).Select(p => p.Key).ToArray())
                    s_bought.Remove(old);
                s_bought[__instance] = new Bought { At = Time.unscaledTime, Cost = __instance.cost };
            }, PatchError);
        }

        private static MethodInfo Method(string name) =>
            AccessTools.Method(typeof(ObjectInteractionHandler), name) ??
            throw new MissingMethodException(nameof(ObjectInteractionHandler), name);

        private static void PatchError(Exception error) => Plugin.Log.LogError("[Items] " + error);

        private static bool DrivenHere(ObjectInteractionHandler item, out ItemRegistry items)
        {
            items = CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Items : null;
            return items != null && items._active && !item.isHeld && items._driven.Contains(item);
        }

        // Nothing is held still by the mod (always so outside a session): the game's code runs as it is.
        private static bool NothingDriven()
        {
            CoopBehaviour coop = CoopBehaviour.Instance;
            return coop == null || coop.Items == null || coop.Items._driven.Count == 0;
        }

        // Every frame the game puts a thing that is not in the local hand back under its parent and
        // gives it back to physics. On a copy that does not move the thing that would fight the
        // mover's poses.
        private static bool HoldPrefix(ObjectInteractionHandler __instance)
        {
            // Called for every thing every frame: no guard closure when there is nothing to decide.
            if (NothingDriven()) return true;
            return PatchGuard.Prefix(() => !DrivenHere(__instance, out _), PatchError);
        }

        // The ship's hold takes a thing that touches it: provisions are added and the thing is
        // destroyed. Only the mover's copy decides that; the others are told.
        private static bool TriggerPrefix(ObjectInteractionHandler __instance, Collider other)
        {
            return PatchGuard.Prefix(() =>
            {
                if (DrivenHere(__instance, out ItemRegistry items)) return false;
                if (items == null || !items._active || other == null) return true;
                if (other.gameObject.GetComponent<LootZone>() != null && ParentShipField != null &&
                    ParentShipField.GetValue(__instance) as ShipHullMover != null)
                    items.Consumed(__instance);
                return true;
            }, PatchError);
        }

        // A thing that touches the rowboat is fixed to a free place in it. Not on a copy that does
        // not move the thing: its rowboat would then hang on a body the mod holds still.
        private static bool CollisionPrefix(ObjectInteractionHandler __instance)
        {
            if (NothingDriven()) return true;
            return PatchGuard.Prefix(() => !DrivenHere(__instance, out _), PatchError);
        }
    }
}
