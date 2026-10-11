using System;
using System.Collections.Generic;
using System.Reflection;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// One hold for the crew: the crates of the active ship. A crate has no identity of its own —
    /// the game puts it on a half-metre grid inside a cargo box and never two in one place — so a
    /// crate is named by its box (the number in the ship's own list) and its place in it.
    ///
    /// Nothing the game does to the cargo is intercepted: four times a second each copy compares
    /// the crates with what it last knew and sends what its own game did — placed, removed, moved.
    /// The host applies a guest's report and relays what changed its hold. The counters of a task
    /// (<c>loaded</c>, <c>progress</c>) and a delivery's reward travel with <see cref="TaskSync"/>
    /// and <see cref="EconomySync"/>; here only the crates and the ship's mass.
    ///
    /// One player at a time works the cargo panel: the game counts crates in the task's own
    /// numbers, and two panels at once would lose counts. The host gives the panel to the first
    /// who opens it; until then, and for anybody else, the panel's clicks are skipped.
    ///
    /// A guest reports nothing until the host's whole hold has come (<see cref="CargoStateMsg"/>),
    /// which it asks for whenever it finds itself on a ship anew.
    /// </summary>
    internal sealed class CargoSync
    {
        private const float Period = 0.25f;
        private const float AskRetry = 5f;

        private static readonly FieldInfo BoxesField = AccessTools.Field(typeof(ShipHullMover), "cargoBoxes");
        // The label on a crate; the task's own text object is of a library this project does not reference.
        private static readonly FieldInfo DestinationField = AccessTools.Field(typeof(TaskHandler), "destinationName");

        private struct Crate
        {
            public CargoDefinition Object;
            public CargoCrate Wire;
        }

        private enum Outcome { Done, Nothing, Failed }

        private readonly CoopNet _net;
        private readonly TaskSync _tasks;
        // Place → the crate there, as last seen here or as last written from the wire.
        private Dictionary<long, Crate> _known = new Dictionary<long, Crate>();
        private Dictionary<long, Crate> _scan = new Dictionary<long, Crate>();
        // The parent of each cargo box's crates, by the box's number; null for a box without one.
        private readonly List<Transform> _boxes = new List<Transform>();
        private readonly List<Crate> _came = new List<Crate>();
        private readonly List<Crate> _left = new List<Crate>();
        private readonly List<CargoOp> _ops = new List<CargoOp>();
        private ShipHullMover _ship;
        private CargoPanelHandler[] _panels;
        private bool _synced, _asked;
        private uint _holder;
        private float _nextPollAt, _askAt;
        private int _massFrame = -1;
        private string _notice;
        private bool _warned;

        public int Sent { get; private set; }
        public int Applied { get; private set; }
        public int Failed { get; private set; }

        /// <summary>The panel is not this player's to work: the patches read it.</summary>
        private static bool s_blocked;

        public CargoSync(CoopNet net, TaskSync tasks) { _net = net; _tasks = tasks; }

        private static bool Usable => BoxesField != null && PatchHealth.StateOf("Cargo") == PatchHealthState.Ok;

        public void Clear()
        {
            _known.Clear(); _scan.Clear(); _boxes.Clear();
            _ship = null; _panels = null;
            _synced = false; _asked = false; _holder = 0; _massFrame = -1; _notice = null;
            Sent = Applied = Failed = 0;
            s_blocked = false;
        }

        /// <summary>What the player should be told, once.</summary>
        public string PopNotice()
        {
            string notice = _notice;
            _notice = null;
            return notice;
        }

        // -----------------------------------------------------------------
        // Reading the hold
        // -----------------------------------------------------------------

        private static int Half(float v) => Mathf.RoundToInt(v * 2f);

        // The game saves a crate's place with two decimals, so a guest's loaded crate and the
        // host's placed one differ in the last digits; the half-metre cell is the same.
        private static long Key(int box, float x, float y, float z) =>
            ((long)(box & 0xFF) << 48) | ((long)(Half(x) & 0xFFFF) << 32) |
            ((long)(Half(y) & 0xFFFF) << 16) | (long)(Half(z) & 0xFFFF);

        private static long Key(CargoCrate c) => Key(c.Box, c.X, c.Y, c.Z);

        private static bool Same(CargoCrate a, CargoCrate b) => a.CargoId == b.CargoId && a.TaskId == b.TaskId;

        private void Bind(ShipHullMover ship)
        {
            _ship = ship;
            _boxes.Clear();
            var seen = new HashSet<Transform>();
            if (BoxesField.GetValue(ship) is List<CargoPlacer> placers)
                foreach (CargoPlacer placer in placers)
                {
                    Transform parent = placer != null ? placer.transform.parent : null;
                    // Two boxes under one parent would count its crates twice: the first has them.
                    _boxes.Add(parent != null && seen.Add(parent) ? parent : null);
                }
            _panels = ship.GetComponentsInChildren<CargoPanelHandler>(true);
            Scan();
            Swap();
            // A guest's hold is the one it joined with, or older: the host's comes first.
            _synced = !_net.IsClient;
            _askAt = 0f;
            _massFrame = -1;
        }

        private void Unbind()
        {
            _ship = null; _panels = null;
            _known.Clear(); _boxes.Clear();
            _synced = false; _massFrame = -1;
        }

        private void Scan()
        {
            _scan.Clear();
            for (int i = 0; i < _boxes.Count && i < 256; i++)
            {
                Transform parent = _boxes[i];
                if (parent == null) continue;
                foreach (Transform child in parent)
                {
                    CargoDefinition cargo = child.GetComponent<CargoDefinition>();
                    if (cargo == null) continue;
                    Vector3 p = child.localPosition;
                    long key = Key(i, p.x, p.y, p.z);
                    if (_scan.ContainsKey(key)) continue;
                    _scan.Add(key, new Crate
                    {
                        Object = cargo,
                        Wire = new CargoCrate { Box = (byte)i, X = p.x, Y = p.y, Z = p.z, CargoId = cargo.id ?? "", TaskId = cargo.taskID ?? "" }
                    });
                }
            }
        }

        private void Swap()
        {
            Dictionary<long, Crate> known = _known;
            _known = _scan;
            _scan = known;
        }

        // -----------------------------------------------------------------
        // Loop
        // -----------------------------------------------------------------

        /// <summary>Every frame for a participant.</summary>
        public void Tick()
        {
            if (!Usable) return;
            ShipHullMover ship = GameAccess.ActiveShip();
            if (ship == null)
            {
                if (!ReferenceEquals(_ship, null))
                {
                    // The ship went with the world: a panel that was open on it is closed.
                    _panels = null;
                    TickLease();
                    Unbind();
                }
                return;
            }
            if (ship != _ship) Bind(ship);
            TickLease();
            // The game destroys a crate at the end of its frame, and the mass is the sum of the
            // crates that are there: it is counted a frame after the change.
            if (_massFrame >= 0 && Time.frameCount > _massFrame)
            {
                _massFrame = -1;
                _ship.UpdateCargoMass();
            }
            float now = Time.unscaledTime;
            if (_net.IsClient && !_synced && now >= _askAt)
            {
                _askAt = now + AskRetry;
                _net.SendToHost(new CargoOpsMsg { ShipId = _ship.id ?? "", Resync = true }, DeliveryMethod.ReliableOrdered);
            }
            if (now < _nextPollAt) return;
            _nextPollAt = now + Period;
            Poll();
        }

        // What the local game did to the hold since the last look.
        private void Poll()
        {
            if (_ship == null) return;
            Scan();
            if (!_synced) { Swap(); return; }
            _came.Clear(); _left.Clear(); _ops.Clear();
            foreach (KeyValuePair<long, Crate> pair in _known)
                if (!_scan.TryGetValue(pair.Key, out Crate now) || !ReferenceEquals(now.Object, pair.Value.Object))
                    _left.Add(pair.Value);
            foreach (KeyValuePair<long, Crate> pair in _scan)
                if (!_known.TryGetValue(pair.Key, out Crate before) || !ReferenceEquals(before.Object, pair.Value.Object))
                    _came.Add(pair.Value);
            Swap();
            if (_came.Count == 0 && _left.Count == 0) return;

            // Removals first: a crate may go where another just stood.
            foreach (Crate left in _left)
                if (!_came.Exists(c => ReferenceEquals(c.Object, left.Object)))
                    _ops.Add(new CargoOp { Kind = CargoOpKind.Remove, Crate = left.Wire });
            foreach (Crate came in _came)
            {
                int from = _left.FindIndex(c => ReferenceEquals(c.Object, came.Object));
                if (from < 0) continue;
                _ops.Add(new CargoOp
                {
                    Kind = CargoOpKind.Move, Crate = _left[from].Wire,
                    ToBox = came.Wire.Box, ToX = came.Wire.X, ToY = came.Wire.Y, ToZ = came.Wire.Z
                });
            }
            foreach (Crate came in _came)
                if (!_left.Exists(c => ReferenceEquals(c.Object, came.Object)))
                    _ops.Add(new CargoOp { Kind = CargoOpKind.Add, Crate = came.Wire });

            _massFrame = Time.frameCount;
            var msg = new CargoOpsMsg { ShipId = _ship.id ?? "", Ops = _ops.ToArray() };
            if (_net.IsHost) _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
            else _net.SendToHost(msg, DeliveryMethod.ReliableOrdered);
            Sent++;
        }

        // -----------------------------------------------------------------
        // Writing the hold
        // -----------------------------------------------------------------

        private static TaskHandler TaskIn(GameObject obj, string id)
        {
            TaskHandler task = obj != null ? obj.GetComponent<TaskHandler>() : null;
            return task != null && task.id == id ? task : null;
        }

        private static TaskHandler TaskOf(string id)
        {
            TaskPanelHandler panel = TaskPanelHandler.Instance;
            if (panel == null || string.IsNullOrEmpty(id)) return null;
            if (panel.acceptedTaskObjects != null)
                foreach (GameObject obj in panel.acceptedTaskObjects)
                {
                    TaskHandler task = TaskIn(obj, id);
                    if (task != null) return task;
                }
            return TaskIn(panel.woodProductTask, id) ?? TaskIn(panel.linenProductTask, id) ?? TaskIn(panel.ironProductTask, id);
        }

        private static string IdOf(GameObject prefab)
        {
            CargoDefinition cargo = prefab != null ? prefab.GetComponent<CargoDefinition>() : null;
            return cargo != null ? cargo.id : null;
        }

        private static GameObject PrefabOf(string id, TaskHandler task)
        {
            if (task != null && IdOf(task.cargo) == id) return task.cargo;
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null || dp.availableCargoTypes == null) return null;
            foreach (GameObject type in dp.availableCargoTypes)
                if (IdOf(type) == id) return type;
            return null;
        }

        // The steps the game takes for a crate of a loaded world.
        private CargoDefinition Create(CargoCrate crate)
        {
            if (crate.Box >= _boxes.Count || _boxes[crate.Box] == null) return null;
            TaskHandler task = TaskOf(crate.TaskId);
            GameObject prefab = PrefabOf(crate.CargoId, task);
            if (prefab == null) return null;
            Transform parent = _boxes[crate.Box];
            GameObject obj = UnityEngine.Object.Instantiate(prefab);
            CargoDefinition cargo = obj.GetComponent<CargoDefinition>();
            if (cargo == null) { UnityEngine.Object.Destroy(obj); return null; }
            obj.transform.parent = parent;
            obj.transform.localPosition = new Vector3(crate.X, crate.Y, crate.Z);
            obj.transform.rotation = parent.rotation;
            FixedJoint joint = obj.GetComponent<FixedJoint>();
            if (joint != null) joint.connectedBody = _ship.GetComponent<Rigidbody>();
            cargo.taskID = crate.TaskId ?? "";
            try
            {
                cargo.SetReflectionProbe(_ship.cargoReflectionProbe);
                if (task != null && DestinationField != null && DestinationField.GetValue(task) is string destination && destination.Length > 0)
                    cargo.SetDestination(destination);
            }
            catch (Exception e)
            {
                if (!_warned) Plugin.Log.LogWarning("[Cargo] A crate was placed without its label: " + e.Message);
                _warned = true;
            }
            return cargo;
        }

        private static void Remove(CargoDefinition cargo)
        {
            if (cargo == null) return;
            // Out of the box at once: the game destroys at the end of the frame, and until then
            // the crate would still be counted and still be found in its place.
            cargo.transform.SetParent(null, true);
            UnityEngine.Object.Destroy(cargo.gameObject);
        }

        private void Move(CargoDefinition cargo, CargoOp op)
        {
            Transform parent = _boxes[op.ToBox];
            FixedJoint joint = cargo.GetComponent<FixedJoint>();
            if (joint != null) joint.connectedBody = null;
            cargo.transform.parent = parent;
            cargo.transform.localPosition = new Vector3(op.ToX, op.ToY, op.ToZ);
            cargo.transform.rotation = parent.rotation;
            if (joint != null) joint.connectedBody = _ship.GetComponent<Rigidbody>();
            try { cargo.SetReflectionProbe(_ship.cargoReflectionProbe); } catch { }
        }

        private Outcome Apply(CargoOp op)
        {
            long key = Key(op.Crate);
            bool there = _known.TryGetValue(key, out Crate crate) && crate.Object != null;
            switch (op.Kind)
            {
                case CargoOpKind.Add:
                {
                    if (there) return Same(crate.Wire, op.Crate) ? Outcome.Nothing : Outcome.Failed;
                    CargoDefinition made = Create(op.Crate);
                    if (made == null) return Outcome.Failed;
                    _known[key] = new Crate { Object = made, Wire = op.Crate };
                    return Outcome.Done;
                }
                case CargoOpKind.Remove:
                {
                    if (!there) { _known.Remove(key); return Outcome.Nothing; }
                    Remove(crate.Object);
                    _known.Remove(key);
                    return Outcome.Done;
                }
                case CargoOpKind.Move:
                {
                    long to = Key(op.ToBox, op.ToX, op.ToY, op.ToZ);
                    bool taken = _known.TryGetValue(to, out Crate other) && other.Object != null;
                    if (!there) return taken && Same(other.Wire, op.Crate) ? Outcome.Nothing : Outcome.Failed;
                    if (taken || op.ToBox >= _boxes.Count || _boxes[op.ToBox] == null) return Outcome.Failed;
                    Move(crate.Object, op);
                    _known.Remove(key);
                    crate.Wire.Box = op.ToBox; crate.Wire.X = op.ToX; crate.Wire.Y = op.ToY; crate.Wire.Z = op.ToZ;
                    _known[to] = crate;
                    return Outcome.Done;
                }
            }
            return Outcome.Failed;
        }

        // -----------------------------------------------------------------
        // From the wire
        // -----------------------------------------------------------------

        public void OnOps(CargoOpsMsg msg, NetPeer from)
        {
            if (!Usable) return;
            uint player = _net.IsHost ? _net.PlayerNetIdForPeer(from) : 0;
            if (_net.IsHost && player == 0) return;
            if (_ship == null || (msg.ShipId ?? "") != (_ship.id ?? "")) return;
            if (msg.Resync)
            {
                if (!_net.IsHost) return;
                SendState(player);
                _net.SendTo(player, new CargoLeaseMsg { Holder = _holder }, DeliveryMethod.ReliableOrdered);
                return;
            }
            // The host's whole hold is on its way to this guest; these are already in it.
            if (_net.IsClient && !_synced) return;
            // The local changes first, so the incoming ones are not sent back as local changes.
            Poll();
            var done = new List<CargoOp>();
            bool failed = false;
            foreach (CargoOp op in msg.Ops)
            {
                Outcome outcome = Apply(op);
                if (outcome == Outcome.Done) done.Add(op);
                else if (outcome == Outcome.Failed) failed = true;
            }
            if (done.Count > 0) { _massFrame = Time.frameCount; Applied++; }
            if (failed) Failed++;
            if (!_net.IsHost) return;
            if (done.Count > 0)
                _net.Broadcast(new CargoOpsMsg { ShipId = msg.ShipId, Ops = done.ToArray() },
                               DeliveryMethod.ReliableOrdered, readyOnly: true, exceptNetId: player);
            // What could not be done here stands on the guest's copy: it gets the hold as it is.
            if (failed) SendState(player);
        }

        private CargoStateMsg State()
        {
            Poll();
            var crates = new CargoCrate[_known.Count];
            int i = 0;
            foreach (Crate crate in _known.Values) crates[i++] = crate.Wire;
            return new CargoStateMsg { ShipId = _ship.id ?? "", Crates = crates };
        }

        private void SendState(uint player)
        {
            if (!_net.IsHost || _ship == null) return;
            _net.SendTo(player, State(), DeliveryMethod.ReliableOrdered);
            Sent++;
        }

        /// <summary>Guest: the host's hold. This copy's is made the same, whatever it held.</summary>
        public void OnState(CargoStateMsg msg)
        {
            if (!_net.IsClient || !Usable) return;
            // With no ship there is nothing to make the same; once on one, this copy asks again.
            if (_ship == null || (msg.ShipId ?? "") != (_ship.id ?? "")) return;
            Scan();
            Swap();
            var wanted = new Dictionary<long, CargoCrate>();
            foreach (CargoCrate crate in msg.Crates) wanted[Key(crate)] = crate;

            var extra = new List<long>();
            foreach (KeyValuePair<long, Crate> pair in _known)
                if (!wanted.TryGetValue(pair.Key, out CargoCrate crate) || !Same(crate, pair.Value.Wire))
                    extra.Add(pair.Key);
            foreach (long key in extra)
            {
                Remove(_known[key].Object);
                _known.Remove(key);
            }
            foreach (KeyValuePair<long, CargoCrate> pair in wanted)
            {
                if (_known.ContainsKey(pair.Key)) continue;
                CargoDefinition made = Create(pair.Value);
                if (made != null) _known[pair.Key] = new Crate { Object = made, Wire = pair.Value };
                else Failed++;
            }
            _synced = true;
            _massFrame = Time.frameCount;
            Applied++;
        }

        // -----------------------------------------------------------------
        // The panel: one player at a time
        // -----------------------------------------------------------------

        private void TickLease()
        {
            CargoPanelHandler open = null;
            if (_panels != null)
                foreach (CargoPanelHandler panel in _panels)
                    if (panel != null && panel.interacting) open = panel;
            bool mine = open != null;
            uint me = _net.MyNetId;
            if (mine && _holder != 0 && _holder != me)
            {
                open.SetInteracting(false);
                _notice = _net.GetPlayerName(_holder) + " is loading cargo";
                mine = false;
            }
            if (_net.IsHost)
            {
                if (mine && _holder == 0) SetHolder(me);
                else if (!mine && _holder == me) { Flush(); SetHolder(0); }
            }
            else if (mine && !_asked)
            {
                _asked = true;
                if (!_synced) _notice = "Waiting for the host's cargo";
                _net.SendToHost(new CargoLeaseMsg { Want = true }, DeliveryMethod.ReliableOrdered);
            }
            else if (!mine && _asked)
            {
                _asked = false;
                // The last crates and counts go before the panel is given up, so the next player
                // starts from them.
                Flush();
                if (_holder == me) _holder = 0;
                _net.SendToHost(new CargoLeaseMsg { Want = false }, DeliveryMethod.ReliableOrdered);
            }
            // Not only while the panel is open: the game opens it late in a frame, and its first
            // frame of clicks may run before this is looked at again.
            s_blocked = _holder != me || !_synced;
        }

        private void Flush()
        {
            _tasks.Flush();
            Poll();
        }

        private void SetHolder(uint holder)
        {
            _holder = holder;
            _net.Broadcast(new CargoLeaseMsg { Holder = holder }, DeliveryMethod.ReliableOrdered);
            // Between two players every copy is brought to the host's hold.
            if (holder != 0 || _ship == null) return;
            _net.Broadcast(State(), DeliveryMethod.ReliableOrdered);
            Sent++;
        }

        public void OnLease(CargoLeaseMsg msg, NetPeer from)
        {
            if (!_net.IsHost) { _holder = msg.Holder; return; }
            uint player = _net.PlayerNetIdForPeer(from);
            if (player == 0) return;
            if (!msg.Want) { if (_holder == player) SetHolder(0); return; }
            if (_holder == 0) SetHolder(player);
            else if (_holder != player)
                _net.SendTo(player, new CargoLeaseMsg { Holder = _holder }, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host: a player left; the panel it had is free.</summary>
        public void RemovePlayer(uint player)
        {
            if (_net.IsHost && _holder == player) SetHolder(0);
        }

        public string Describe()
        {
            if (BoxesField == null) return "off: the game's cargo boxes were not found";
            if (PatchHealth.StateOf("Cargo") != PatchHealthState.Ok) return "off: the Cargo patch failed";
            return "crates " + _known.Count + "   sent " + Sent + "   applied " + Applied +
                   (Failed > 0 ? "   failed " + Failed : "") +
                   (_holder != 0 ? "   panel: " + _net.GetPlayerName(_holder) : "") +
                   (_net.IsClient && !_synced ? "   waiting for the host's hold" : "");
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(CargoSync), nameof(PanelPrefix));
            foreach (string name in new[] { "HandlePlacement", "HandleMovement", "HandleRemoval", "HandleSelection" })
                harmony.Patch(AccessTools.Method(typeof(CargoPanelHandler), name)
                                  ?? throw new MissingMethodException(nameof(CargoPanelHandler), name),
                              prefix: prefix);
        }

        // The panel's four modes — place, move, remove, pick — run every frame while it is open.
        // They are skipped while the panel is not this player's: before the host has answered, and
        // when another player has it.
        private static bool PanelPrefix()
        {
            if (!s_blocked) return true;
            return PatchGuard.Prefix(() => !s_blocked, error => Plugin.Log.LogError("[Cargo] " + error));
        }
    }
}
