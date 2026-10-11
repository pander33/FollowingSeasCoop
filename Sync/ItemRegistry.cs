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
    internal sealed partial class ItemRegistry
    {
        internal sealed class Entry
        {
            public ItemRecord Record;
            public Component Object;
            // Where the thing is on a copy that does not move it itself.
            public readonly ItemTrack Track = new ItemTrack();
            // While this copy moves the thing: the pose of the last report, and the pose it last
            // moved away from (a thing that stays within a centimetre of it is at rest).
            public ItemPose Prev, Ref;
            public float PrevTime, MovedAt;
        }

        /// <summary>A guest with no manifest asks for one this long after it entered the world.</summary>
        private const float RequestAfter = 3f;
        private const float RequestRetry = 5f;
        /// <summary>A thing this far from where its scene put it was carried there, not nudged.</summary>
        private const float MovedBefore = 1f;

        private static readonly FieldInfo FaunaField = AccessTools.Field(typeof(ObjectInteractionHandler), "isFauna");
        private static readonly FieldInfo TypeField = AccessTools.Field(typeof(ObjectInteractionHandler), "type");

        private readonly CoopNet _net;
        private readonly Dictionary<uint, Entry> _ids = new Dictionary<uint, Entry>();
        private readonly Dictionary<string, Entry> _keys = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<Component, string> _objects = new Dictionary<Component, string>();
        // The one object that stands for a key. A second object with the same key stays local.
        private readonly Dictionary<string, Component> _owners = new Dictionary<string, Component>(StringComparer.Ordinal);
        private readonly HashSet<Component> _duplicates = new HashSet<Component>();
        private readonly List<ItemStateMsg> _pending = new List<ItemStateMsg>();
        private uint _nextId = 1, _generation, _incomingGeneration;
        private int _nextPart;
        private bool _complete, _active, _receiving, _warnedFields, _warnedOrder;
        private int _worldHandle;
        private float _scanAt, _requestAt;

        public ItemRegistry(CoopNet net) { _net = net; }
        public Entry Find(uint id) => _ids.TryGetValue(id, out Entry entry) ? entry : null;
        public Entry Find(string key) => _keys.TryGetValue(key, out Entry entry) ? entry : null;
        public Entry Find(Component obj) => _objects.TryGetValue(obj, out string key) ? Find(key) : null;

        public void Clear()
        {
            _ids.Clear(); _keys.Clear(); _objects.Clear(); _owners.Clear(); _duplicates.Clear(); _pending.Clear();
            _nextId = 1; _nextPart = 0;
            _complete = false; _active = false; _receiving = false; _worldHandle = 0;
            ClearCarry();
        }

        public void WorldUnloaded(Scene scene)
        {
            if (scene.buildIndex == GameAccess.WorldScene) Clear();
        }

        public void RemovePlayer(uint player) => HostPlayerGone(player);

        /// <summary>Every frame for a participant.</summary>
        public void Tick()
        {
            if (!EnsureWorld()) return;
            float now = Time.unscaledTime;
            // The host sends a manifest by itself when a guest becomes ready; this covers a guest
            // that loaded its world again, and a manifest that was cut short.
            if (_net.IsClient && !_complete && !_receiving && now >= _requestAt)
            {
                _requestAt = now + RequestRetry;
                _net.SendToHost(new ItemActionMsg { Action = ItemActionKind.Manifest }, DeliveryMethod.ReliableOrdered);
            }
            PollHands();
            if (now < _scanAt) return;
            _scanAt = now + 1f;
            Scan();
            SpawnStandIns();
        }

        // Binds the registry to the loaded world; a new world starts an empty registry.
        private bool EnsureWorld()
        {
            if (!GameAccess.InWorld) return false;
            int handle = SceneManager.GetSceneByBuildIndex(GameAccess.WorldScene).handle;
            if (_active && handle == _worldHandle) return true;
            Clear(); _active = true; _worldHandle = handle;
            _worldSince = Time.unscaledTime;
            _requestAt = Time.unscaledTime + RequestAfter;
            _scanAt = Time.unscaledTime + 1f;
            Scan();
            return true;
        }

        private static bool FieldsFound => FaunaField != null && TypeField != null;

        private void Scan()
        {
            if (!FieldsFound)
            {
                if (!_warnedFields)
                {
                    _warnedFields = true;
                    Plugin.Log.LogWarning("[Items] The game's item fields were not found (a game update?); items are not registered");
                }
                return;
            }
            Forget();
            foreach (ObjectInteractionHandler item in Resources.FindObjectsOfTypeAll<ObjectInteractionHandler>())
                if (item != null && item.gameObject.scene.IsValid() && item.gameObject.scene.isLoaded)
                {
                    string key = ItemCatalog.KeyOf(item);
                    if (key != null) PortableStarted(item, key);
                }
        }

        // Destroyed objects: an unloaded island, a used-up barrel.
        private void Forget()
        {
            foreach (Component obj in _objects.Keys.Where(obj => obj == null).ToArray()) _objects.Remove(obj);
            foreach (string key in _owners.Where(pair => pair.Value == null).Select(pair => pair.Key).ToArray())
                _owners.Remove(key);
            _duplicates.RemoveWhere(obj => obj == null);
            ForgetCarry();
        }

        public void PortableStarted(ObjectInteractionHandler item, string key)
        {
            if (!_active || !FieldsFound || _objects.ContainsKey(item) || _duplicates.Contains(item) ||
                (bool)FaunaField.GetValue(item)) return;
            // Made while the game runs: its path is not an identity, the next one made gets the same.
            // The yield of a hunt is named after its animal; anything else stays this copy's own.
            string yield = HuntSync.YieldKey(item);
            if (yield != null) key = yield;
            else if (item.name.EndsWith("(Clone)", StringComparison.Ordinal)) return;
            var record = new ItemRecord
            {
                Key = key, Kind = ItemKind.Portable, Name = item.GetName(),
                ItemType = TypeField.GetValue(item).ToString(),
                Prefab = BaseName(item.name), Amount = AmountOf(item),
                Status = item.isHeld ? ItemStatus.Held : ItemStatus.Free,
                Holder = item.isHeld ? _net.MyNetId : 0
            };
            // Carried off before this session began: a guest's world has it where its scene put it, so
            // it is shared from the start. This copy moves it like a thing just let go: a second
            // later it rests where it lies, or where it lands if it was in the air.
            // The yield of a hunt is on no other copy at all.
            if (_net.IsHost && CarryFields && !item.isHeld &&
                item.GetComponent<FixedJoint>() == null && (yield != null || ItemCatalog.MovedFromStart(item, MovedBefore)))
            {
                record.Flags |= ItemFlags.Touched;
                if ((bool)PurchasedField.GetValue(item)) record.Flags |= ItemFlags.Purchased;
            }
            Register(item, record);
            // A guest's yield: the host learns of it as of a thing taken up and let go at once, so
            // the others see it lie there before anyone carries it.
            if (yield != null && _net.IsClient && _complete && CarryReady && !item.isHeld && Find(key) == null)
            {
                Act(ItemActionKind.Take, item, key, null);
                Act(ItemActionKind.Drop, item, key, null);
            }
        }

        private void Register(Component obj, ItemRecord record)
        {
            if (_owners.TryGetValue(record.Key, out Component owner) && owner != null && owner != obj)
            {
                Entry taken = Find(record.Key);
                if (taken != null && record.Kind == ItemKind.Portable &&
                    ((taken.Record.Flags & ItemFlags.Touched) != 0 || taken.Record.Status == ItemStatus.Deleted))
                {
                    // An island loaded again brings back a thing that was carried away from it: the
                    // carried one is the thing, this is its empty place.
                    UnityEngine.Object.Destroy(obj.gameObject);
                    return;
                }
                // Two objects, one key: the first keeps the identity.
                _duplicates.Add(obj);
                Plugin.Log.LogWarning("[Items] A second object has the key of '" + owner.name + "': " + record.Key);
                return;
            }
            _owners[record.Key] = obj;
            _objects[obj] = record.Key;
            Entry existing = Find(record.Key);
            if (existing != null) { Bind(existing, obj); return; }
            if (!_net.IsHost) return;
            record.NetId = _nextId++; record.Revision = 1; record.Leader = _net.MyNetId;
            Capture(record, obj);
            var entry = new Entry { Record = record, Object = obj };
            _ids.Add(record.NetId, entry); _keys.Add(record.Key, entry);
            if (record.Kind == ItemKind.Portable)
                _net.Broadcast(new ItemStateMsg { Record = record }, DeliveryMethod.ReliableOrdered);
        }

        private void Capture(ItemRecord record, Component obj)
        {
            // A settled thing is where its record says, to the last digit, on every copy.
            if ((record.Flags & ItemFlags.Settled) != 0) return;
            if (obj is ObjectInteractionHandler item) { record.Pose = PoseOf(item); return; }
            CoordSpace.LocalToReal(obj.transform.position, out record.Pose.X, out record.Pose.Y, out record.Pose.Z);
            Quaternion q = obj.transform.rotation;
            record.Pose.Qx = q.x; record.Pose.Qy = q.y; record.Pose.Qz = q.z; record.Pose.Qw = q.w;
        }

        private void Bind(Entry entry, Component obj)
        {
            entry.Object = obj;
            if (entry.Record.Status == ItemStatus.Deleted) { UnityEngine.Object.Destroy(obj.gameObject); return; }
            if (obj is ObjectInteractionHandler item) Dress(entry.Record, item);
        }

        public void SendManifest(uint player)
        {
            if (!_net.IsHost || !EnsureWorld()) return;
            Scan();
            // Crates in the hold are not here: CargoSync names them by their place.
            ItemRecord[] records = _ids.Values.OrderBy(entry => entry.Record.NetId).Select(entry =>
            {
                if (entry.Object != null) Capture(entry.Record, entry.Object);
                return entry.Record;
            }).ToArray();
            int parts = Math.Max(1, (records.Length + ItemManifestMsg.EntriesPerPart - 1) / ItemManifestMsg.EntriesPerPart);
            uint generation = ++_generation;
            for (int part = 0; part < parts; part++)
                _net.SendTo(player, new ItemManifestMsg
                {
                    Generation = generation, Part = part, Parts = parts,
                    Entries = records.Skip(part * ItemManifestMsg.EntriesPerPart).Take(ItemManifestMsg.EntriesPerPart).ToArray()
                }, DeliveryMethod.ReliableOrdered);
        }

        public void OnManifest(ItemManifestMsg msg)
        {
            if (!_net.IsClient || !EnsureWorld()) return;
            if (msg.Part == 0)
            {
                // Everything sent before a manifest is already in it.
                _ids.Clear(); _keys.Clear(); _pending.Clear(); _complete = false;
                _driven.Clear();
                _incomingGeneration = msg.Generation; _nextPart = 0; _receiving = true;
            }
            if (!_receiving || msg.Generation != _incomingGeneration || msg.Part != _nextPart)
            {
                // The rest of a manifest whose start belonged to a world this copy has left.
                _receiving = false;
                if (!_warnedOrder)
                {
                    _warnedOrder = true;
                    Plugin.Log.LogWarning("[Items] A manifest part came without its start; asking for the manifest again");
                }
                return;
            }
            foreach (ItemRecord record in msg.Entries) Apply(record);
            _nextPart++;
            if (_nextPart != msg.Parts) return;
            _complete = true; _receiving = false;
            foreach (ItemStateMsg state in _pending) Apply(state.Record);
            _pending.Clear();
            Plugin.Log.LogInfo("[Items] Manifest bound: " + Describe());
            ReleaseStaleHolds();
        }

        public void OnState(ItemStateMsg msg)
        {
            if (!_net.IsClient || !EnsureWorld()) return;
            if (!_complete) { _pending.Add(msg); return; }
            Apply(msg.Record);
        }

        private void Apply(ItemRecord record)
        {
            Entry entry = Find(record.NetId);
            if (entry != null && entry.Record.Revision > record.Revision) return;
            if (entry == null) { entry = new Entry(); _ids.Add(record.NetId, entry); }
            else if (entry.Record.Key != record.Key && Find(entry.Record.Key) == entry) _keys.Remove(entry.Record.Key);
            ItemRecord before = entry.Record;
            entry.Record = record; _keys[record.Key] = entry;
            if (_owners.TryGetValue(record.Key, out Component owner) && owner != null) Bind(entry, owner);
            Changed(entry, before);
        }

        /// <summary>Diagnostics: every thing somebody has touched, and where it is on this copy.</summary>
        public void Dump(StateDump d)
        {
            int untouched = 0, touched = 0;
            foreach (Entry entry in _ids.Values)
            {
                ItemRecord r = entry.Record;
                if (r.Kind != ItemKind.Portable) continue;
                if (r.Status != ItemStatus.Deleted && (r.Flags & ItemFlags.Touched) == 0) { untouched++; continue; }
                touched++;
                string name = "thing #" + r.NetId + " " + r.Key;
                if (r.Status == ItemStatus.Deleted) { d.Line("Items", name, "gone"); continue; }
                d.Line("Items", name, r.Status + " holder " + r.Holder + " mover " + r.Leader +
                       ((r.Flags & ItemFlags.Settled) != 0 ? " at rest" : " moving"));
                // A thing of an island this copy has not loaded has a record and no object.
                if (entry.Object == null) { d.Info("Items", name + " object", "none on this copy"); continue; }
                // A thing in a hand or on its way is drawn a moment behind on the copies that follow it.
                if (r.Status != ItemStatus.Free || (r.Flags & ItemFlags.Settled) == 0 || !(entry.Object is ObjectInteractionHandler item))
                    continue;
                ItemPose p = PoseOf(item);
                d.Line("Items", name + " place", (string.IsNullOrEmpty(p.ShipId) ? "world" : "aboard " + p.ShipId) + " " +
                       StateDump.F(p.X) + " " + StateDump.F(p.Y) + " " + StateDump.F(p.Z), 0.1f);
            }
            d.Line("Items", "touched", touched.ToString());
            d.Info("Items", "untouched records", untouched.ToString());
            d.Info("Items", "registry", Describe());
        }

        public string Describe()
        {
            if (PatchHealth.StateOf("Item catalog") != PatchHealthState.Ok) return "off: the Item catalog patch failed";
            if (!FieldsFound) return "off: the game's item fields were not found";
            int deleted = _ids.Values.Count(entry => entry.Record.Status == ItemStatus.Deleted);
            int bound = _ids.Values.Count(entry => entry.Record.Status != ItemStatus.Deleted && entry.Object != null);
            int unmatched = _objects.Count(pair => pair.Key != null && !_keys.ContainsKey(pair.Value));
            int duplicate = _duplicates.Count(obj => obj != null);
            return "bound " + bound + "   waiting " + (_ids.Count - bound - deleted) +
                   "   unmatched " + unmatched + "   duplicate " + duplicate + "   deleted " + deleted +
                   (_net.IsClient && !_complete ? "   receiving manifest" : "") + DescribeCarry();
        }
    }
}
