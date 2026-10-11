using System;
using System.Collections;
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
    /// Shared ship controls. Each is synchronised as a <b>state</b>, not as input. The player who
    /// works a control does it with the game's own code on its own copy of the ship and reports what
    /// the control reads; the host applies that to its ship — the one whose physics sails — and passes
    /// it to the other players.
    ///
    /// Who speaks for a control:
    /// <list type="bullet">
    /// <item>the player who has it in hand (wheel, reef, rope, winch, ballast, capstan);</item>
    /// <item>the player who just switched it (doors, lanterns: the game's own call is intercepted);</item>
    /// <item>a guest whose copy changed without a hand on it, for the kinds where only the local
    /// player can cause that (the anchor, mooring lines);</item>
    /// <item>otherwise the host, for what its own world did (its crew reefs and steers too).</item>
    /// </list>
    /// One hand at a time on the controls that are held for a while: the host's own hand first, then
    /// the guest who reported first and keeps reporting. A later hand's reports are dropped and it is
    /// told what the control reads.
    ///
    /// A control in the local hand is never overwritten: a guest keeps the incoming state and applies
    /// it after letting go. When guests stop reporting a control the host says once more what it
    /// reads, so everyone ends on the host's value whatever order the reports arrived in.
    ///
    /// This file is the mechanism plus the wheel, the reefs and the rig ropes; the other kinds are in
    /// <c>ControlsSync.Deck.cs</c>.
    /// </summary>
    internal sealed partial class ControlsSync
    {
        private const float ScanInterval = 0.1f;
        /// <summary>A control counts as "in hand" for this long after it is let go: tying a rope and the
        /// wheel settling on its spring happen just after the release.</summary>
        private const float HoldGrace = 1.5f;
        /// <summary>The host keeps its own readings to itself while a guest worked the control this recently.</summary>
        private const float RemoteHold = 1.0f;
        /// <summary>A rope the player looks at takes the scroll wheel without being picked up.</summary>
        private const float TouchWindow = 0.3f;
        /// <summary>Scans a state may wait for something it needs (a prefab, a dock) before it is dropped.</summary>
        private const int MaxApplyTries = 50;
        /// <summary>A guest with a control in hand repeats its reading this often, so the host knows it is still held.</summary>
        private const float LeaseBeat = 0.4f;

        private struct Reading
        {
            public float A, B, C;
            public string Text;
        }

        private sealed class Slot
        {
            public ControlKind Kind;
            public int Index;
            public float A, B, C;
            public string Text = "";
            public bool Known;
            public bool Dirty;
            public float HeldAt = -100f;
            /// <summary>When a hand was last on it — not merely a look at a rope.</summary>
            public float HandAt = -100f;
            public float RemoteAt = -100f;
            public float SentAt = -100f;
            /// <summary>Host: the guest whose hand the control is in, and when it last said so.</summary>
            public uint Holder;
            public float HolderAt = -100f;
            /// <summary>Host: guests reported this control; say what it reads once they stop.</summary>
            public bool Settle;
            /// <summary>A state that arrived and is not applied yet: the control is in the local hand,
            /// or something it needs is not there yet.</summary>
            public bool HasPending;
            public Reading Pending;
            public int Tries;
        }

        private static readonly FieldInfo WheelField = AccessTools.Field(typeof(ShipHullMover), "shipsWheel");
        private static readonly FieldInfo WheelControllerField = AccessTools.Field(typeof(ShipHullMover), "wheelController");
        private static readonly FieldInfo RudderField = AccessTools.Field(typeof(ShipHullMover), "rudderRoot");
        private static readonly FieldInfo VisibleRudderField = AccessTools.Field(typeof(ShipHullMover), "visibleRudderRoot");
        private static readonly FieldInfo ChildRudderField = AccessTools.Field(typeof(ShipHullMover), "childRudder");
        private static readonly FieldInfo RopeJointField = AccessTools.Field(typeof(RopeInteractionHandler), "ropeJoint");

        // Written by the patches, which are static.
        private static RopeInteractionHandler s_touchedRope;
        private static float s_touchedAt = -100f;

        private readonly CoopNet _net;
        private float _nextScanAt;

        // The ship the slots are bound to, and its parts.
        private ShipHullMover _ship;
        private readonly List<Slot> _all = new List<Slot>();
        private GameObject _wheel, _rudder, _visibleRudder, _childRudder;
        private SteeringController _wheelController;
        private readonly List<MastController> _masts = new List<MastController>();
        private readonly List<RopeInteractionHandler> _ropes = new List<RopeInteractionHandler>();
        // For each rope, the ropes whose handling also changes it (the opposite brace of a pair).
        private int[][] _ropeDrivers = new int[0][];
        private Slot _steer;
        private Slot[] _reefs = new Slot[0];
        private Slot[] _ropeSlots = new Slot[0];
        // A reef that is not in the local hand is turned by the game's own routine, stepped from here.
        private IEnumerator[] _reefDrivers = new IEnumerator[0];

        public int Sent { get; private set; }
        public int Applied { get; private set; }
        /// <summary>Host: reports dropped because another player had the control first.</summary>
        public int Refused { get; private set; }
        private float _contestedAt = -100f;
        private uint _contestedBy;

        /// <summary>The local player has a control in hand that another player took first.</summary>
        public bool Contested => Time.unscaledTime - _contestedAt < 1.5f;
        public uint ContestedBy => _contestedBy;
        private int _errors;

        public ControlsSync(CoopNet net) { _net = net; }

        // -----------------------------------------------------------------
        // Binding
        // -----------------------------------------------------------------

        private bool Bind()
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            if (ship == _ship && ship != null) return true;
            Unbind();
            _ship = ship;
            if (ship == null) return false;

            _wheel = WheelField?.GetValue(ship) as GameObject;
            _wheelController = WheelControllerField?.GetValue(ship) as SteeringController;
            _rudder = RudderField?.GetValue(ship) as GameObject;
            _visibleRudder = VisibleRudderField?.GetValue(ship) as GameObject;
            _childRudder = ChildRudderField?.GetValue(ship) as GameObject;
            if (_wheel != null && _wheelController != null && _rudder != null)
                _all.Add(_steer = new Slot { Kind = ControlKind.Steering });
            else
                Plugin.Log.LogWarning("[Controls] The ship's wheel was not found; steering is not shared");

            // The lists come from the ship prefab, so their order is the same on every machine.
            foreach (MastController mast in ship.GetMastControllers()) _masts.Add(mast);
            if (ship.persistentRopes != null) _ropes.AddRange(ship.persistentRopes);

            _reefs = MakeSlots(ControlKind.Reef, _masts.Count);
            _reefDrivers = new IEnumerator[_reefs.Length];
            _ropeSlots = MakeSlots(ControlKind.Rope, _ropes.Count);
            _ropeDrivers = new int[_ropeSlots.Length][];
            var drivers = new List<int>();
            for (int i = 0; i < _ropeSlots.Length; i++)
            {
                drivers.Clear();
                for (int j = 0; j < _ropeSlots.Length; j++)
                    if (j != i && _ropes[j] != null && _ropes[i] != null && _ropes[j].linkedRope == _ropes[i]) drivers.Add(j);
                _ropeDrivers[i] = drivers.ToArray();
            }

            Guard(BindDeck);
            Guard(BindHull);
            Guard(BindBoat);

            Plugin.Log.LogInfo("[Controls] Bound to ship '" + ship.id + "': " + Counts());
            return true;
        }

        private Slot[] MakeSlots(ControlKind kind, int count)
        {
            // The index travels as one byte.
            var slots = new Slot[Math.Min(count, 255)];
            for (int i = 0; i < slots.Length; i++) _all.Add(slots[i] = new Slot { Kind = kind, Index = i });
            return slots;
        }

        private void Unbind()
        {
            for (int i = 0; i < _reefDrivers.Length; i++) DropReefDriver(i);
            _ship = null;
            _all.Clear();
            _steer = null;
            _masts.Clear();
            _ropes.Clear();
            _reefs = new Slot[0];
            _ropeSlots = new Slot[0];
            _ropeDrivers = new int[0][];
            _reefDrivers = new IEnumerator[0];
            UnbindDeck();
            UnbindHull();
            UnbindBoat();
        }

        // -----------------------------------------------------------------
        // Per-frame
        // -----------------------------------------------------------------

        public void Tick()
        {
            s_guest = _net.IsClient;
            if (!Bind()) return;
            StepReefDrivers();
            if (_net.IsClient) Guard(ShowCrewPumps);

            float now = Time.unscaledTime;
            if (now < _nextScanAt) return;
            _nextScanAt = now + ScanInterval;

            if (_net.IsClient) SendPumped();
            Guard(BindHooks);
            for (int i = 0; i < _all.Count; i++)
            {
                Slot slot = _all[i];
                Guard(() => Scan(slot, now));
            }
        }

        /// <summary>Host: a player has just entered the world; tell it where every control stands.</summary>
        public void MarkAllDirty()
        {
            foreach (Slot slot in _all) slot.Dirty = true;
        }

        private void Scan(Slot slot, float now)
        {
            if (!Read(slot, out Reading reading, out bool held)) return;
            bool hand = held;
            if (slot.Kind == ControlKind.Rope && held && !RopeInHand(slot.Index))
            {
                // Only looked at. The scroll wheel trims such a rope, so it counts as the local
                // player's when it actually moved — otherwise a glance at a rope would keep it
                // from the player who hauls it.
                hand = false;
                held = slot.Known && Differs(slot, reading);
            }
            if (held) slot.HeldAt = now;
            if (hand) slot.HandAt = now;
            bool local = now - slot.HeldAt < HoldGrace;

            if (slot.HasPending)
            {
                // A guest that just let go takes what arrived meanwhile; a state that could not be
                // applied is tried again. Nothing is reported about a control in that condition.
                if (local && _net.IsClient) { /* still in hand: keep reporting below */ }
                else { Flush(slot, true); return; }
            }

            if (_net.IsHost)
            {
                if (slot.Settle && !local && now - slot.RemoteAt > RemoteHold)
                {
                    slot.Settle = false;
                    slot.Dirty = true;
                }
            }
            else if (slot.Kind == ControlKind.Water)
            {
                // The guest's own leaks keep adding water to its copy; the host's figure is the truth.
                if (slot.Known) Apply(slot);
                return;
            }

            // A reef being turned towards a reported point reads everything in between; the point is
            // the state, and that is what a newcomer is told.
            bool driven = slot.Kind == ControlKind.Reef && !local && _reefDrivers[slot.Index] != null;
            if (driven && !slot.Dirty) return;

            bool changed = !slot.Known || slot.Dirty || Differs(slot, reading);
            // Silence from a guest would read as letting go, and the control would pass to the next hand.
            bool beat = _net.IsClient && hand && Leased(slot.Kind) && now - slot.SentAt > LeaseBeat;
            if (!changed && !beat) return;

            if (_net.IsHost)
            {
                if (!local && !slot.Dirty)
                {
                    // A guest is working it: the host is only catching up with that guest's readings.
                    if (now - slot.RemoteAt <= RemoteHold) return;
                    if (now - slot.SentAt < MinInterval(slot.Kind)) return;
                }
            }
            else if (!local)
            {
                if (!GuestSpeaksOnChange(slot.Kind)) return;
                // The first look is only a note of where things stand; the host tells the rest.
                if (!slot.Known) { Store(slot, reading); slot.Known = true; return; }
            }

            // The local player has just done this. A state the host sent before it heard of it must
            // not undo it here: it waits, like a state for a control in hand, and by then the host
            // has said what it reads after this change. Every further change starts the wait anew.
            if (_net.IsClient && GuestSpeaksOnChange(slot.Kind)) slot.HeldAt = now;

            if (!driven) Store(slot, reading);
            Publish(slot, now, local);
        }

        private void Publish(Slot slot, float now, bool local)
        {
            slot.Known = true;
            slot.Dirty = false;
            slot.SentAt = now;
            if (_net.IsHost)
            {
                var msg = new ControlStateMsg();
                Fill(msg, slot, local ? _net.MyNetId : 0u);
                _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                var msg = new ControlRequestMsg();
                Fill(msg, slot, _net.MyNetId);
                _net.SendToHost(msg, DeliveryMethod.ReliableOrdered);
            }
            Sent++;
        }

        private static void Fill(ControlMsg msg, Slot slot, uint holder)
        {
            msg.Kind = slot.Kind;
            msg.Index = (byte)slot.Index;
            msg.Holder = holder;
            msg.A = slot.A; msg.B = slot.B; msg.C = slot.C;
            msg.Text = slot.Text;
        }

        private static void Store(Slot slot, Reading reading)
        {
            slot.A = reading.A; slot.B = reading.B; slot.C = reading.C;
            slot.Text = reading.Text ?? "";
        }

        // -----------------------------------------------------------------
        // Incoming
        // -----------------------------------------------------------------

        /// <summary>Host: a guest reports a control it works.</summary>
        public void OnRequest(ControlRequestMsg msg, NetPeer from)
        {
            if (!_net.IsHost || !Bind()) return;
            uint sender = _net.PlayerNetIdForPeer(from);
            if (sender == 0) return;
            if (msg.Kind == ControlKind.Pump) { Guard(() => HostPump(msg.A)); return; }
            // The host's own figure and the host's own crew, never a guest's.
            if (msg.Kind == ControlKind.Water || msg.Kind == ControlKind.CrewHelm) return;
            Slot slot = Find(msg.Kind, msg.Index);
            if (slot == null) return;

            float now = Time.unscaledTime;
            slot.RemoteAt = now;
            slot.Settle = true;
            // The first hand keeps a control: the host's own on the host's ship, otherwise the guest
            // who reported it first and has not been silent since. A later hand is told what it reads.
            if (now - slot.HeldAt < HoldGrace) { Refuse(slot, sender, _net.MyNetId); return; }
            if (Leased(msg.Kind))
            {
                if (slot.Holder != 0 && slot.Holder != sender && now - slot.HolderAt <= RemoteHold)
                {
                    Refuse(slot, sender, slot.Holder);
                    return;
                }
                slot.Holder = sender;
                slot.HolderAt = now;
            }
            Take(slot, msg, now);

            var state = new ControlStateMsg();
            Fill(state, slot, sender);
            _net.Broadcast(state, DeliveryMethod.ReliableOrdered, readyOnly: true, exceptNetId: sender);
        }

        /// <summary>Guest: the host says what a control reads.</summary>
        public void OnState(ControlStateMsg msg)
        {
            if (!_net.IsClient || !Bind()) return;
            Slot slot = Find(msg.Kind, msg.Index);
            if (slot == null) return;
            float now = Time.unscaledTime;
            // Only a hand that is on the control right now: the grace after a release does not count.
            if (msg.Holder != 0 && msg.Holder != _net.MyNetId && Leased(msg.Kind) && now - slot.HandAt < ScanInterval * 2.5f)
            {
                _contestedAt = now;
                _contestedBy = msg.Holder;
            }
            Take(slot, msg, now);
        }

        private void Refuse(Slot slot, uint sender, uint holder)
        {
            Refused++;
            CoopTrace.Note("Controls", "refused", Name(slot), "report of " + sender + ", in the hand of " + holder);
            if (!slot.Known) return;
            var state = new ControlStateMsg();
            Fill(state, slot, holder);
            _net.SendTo(sender, state, DeliveryMethod.ReliableOrdered);
        }

        private void Take(Slot slot, ControlMsg msg, float now)
        {
            slot.Pending = new Reading { A = msg.A, B = msg.B, C = msg.C, Text = msg.Text ?? "" };
            slot.HasPending = true;
            slot.Tries = 0;
            // What is in the local hand stays the local player's until it is let go.
            if (now - slot.HeldAt < HoldGrace)
            {
                if (Traced(slot.Kind)) CoopTrace.Note("Controls", "waits", Name(slot), "in the local hand");
                return;
            }
            Flush(slot);
        }

        private void Flush(Slot slot, bool waited = false)
        {
            Store(slot, slot.Pending);
            slot.Known = true;
            bool done = true;   // a state that throws is dropped, not retried
            Guard(() => done = Apply(slot));
            if (!done && ++slot.Tries < MaxApplyTries)
            {
                if (slot.Tries == 1) CoopTrace.Note("Controls", "not yet", Name(slot), "will be tried again");
                return;
            }
            slot.HasPending = false;
            if (!done)
            {
                // It never became possible here. The host tells everyone what it has instead.
                if (_net.IsHost) slot.Dirty = true;
                if (_errors++ < 10) Plugin.Log.LogWarning("[Controls] The state of " + Name(slot) + " could not be put into this world");
            }
            else
            {
                Applied++;
                // Applied the moment it came is what "got" already says.
                if (waited && Traced(slot.Kind)) CoopTrace.Note("Controls", "applied", Name(slot), Show(slot));
            }
            // Where a guest reports its own changes, a state that did not take here must not look
            // like one: the note of where things stand is what the control actually reads.
            if (GuestSpeaksOnChange(slot.Kind))
                Guard(() => { if (Read(slot, out Reading actual, out _)) Store(slot, actual); });
        }

        private static string Name(Slot slot) => slot.Kind + "#" + slot.Index;

        private static string Show(Slot slot) =>
            slot.A.ToString("0.###") + " " + slot.B.ToString("0.###") + " " + slot.C.ToString("0.###") +
            (string.IsNullOrEmpty(slot.Text) ? "" : " '" + slot.Text + "'");

        // The wheel while it is turned and the hull's water arrive many times a second.
        private static bool Traced(ControlKind kind) =>
            kind != ControlKind.Steering && kind != ControlKind.Water && kind != ControlKind.TrollLine;

        private Slot Find(ControlKind kind, int index)
        {
            switch (kind)
            {
                case ControlKind.Steering: return _steer;
                case ControlKind.Reef: return At(_reefs, index);
                case ControlKind.Rope: return At(_ropeSlots, index);
                default: return FindDeck(kind, index);
            }
        }

        private static Slot At(Slot[] slots, int index) => index >= 0 && index < slots.Length ? slots[index] : null;

        // -----------------------------------------------------------------
        // Kinds
        // -----------------------------------------------------------------

        /// <summary>Kinds where a change on a guest's copy can only be the local player's doing.</summary>
        private static bool GuestSpeaksOnChange(ControlKind kind)
        {
            // Not lanterns: a guest's own oil store runs out by itself and would put the host's lights out.
            return kind == ControlKind.Anchor || kind == ControlKind.Mooring ||
                   kind == ControlKind.Leak || kind == ControlKind.TrollWeight || kind == ControlKind.FishPoint ||
                   kind == ControlKind.Rowboat;
        }

        /// <summary>Kinds worked with a hand on them for a while: one player at a time.</summary>
        private static bool Leased(ControlKind kind)
        {
            return kind == ControlKind.Steering || kind == ControlKind.Reef || kind == ControlKind.Rope ||
                   kind == ControlKind.Winch || kind == ControlKind.Ballast || kind == ControlKind.TrollLine;
        }

        /// <summary>The least time between two reports of a state that changes by itself all the time.</summary>
        private static float MinInterval(ControlKind kind) => kind == ControlKind.Water ? 0.5f : 0f;

        /// <summary>What the control reads now. False when it cannot be read (the part is missing).</summary>
        private bool Read(Slot slot, out Reading reading, out bool held)
        {
            switch (slot.Kind)
            {
                case ControlKind.Steering: return ReadSteering(out reading, out held);
                case ControlKind.Reef: return ReadReef(slot.Index, out reading, out held);
                case ControlKind.Rope: return ReadRope(slot.Index, out reading, out held);
                default: return ReadDeck(slot, out reading, out held);
            }
        }

        private bool Differs(Slot slot, Reading reading)
        {
            switch (slot.Kind)
            {
                case ControlKind.Steering:
                    return Mathf.Abs(Mathf.DeltaAngle(reading.A, slot.A)) > 0.3f ||
                           Mathf.Abs(Mathf.DeltaAngle(reading.B, slot.B)) > 0.2f ||
                           Mathf.Abs(Mathf.DeltaAngle(reading.C, slot.C)) > 0.2f;
                case ControlKind.Reef:
                    return Mathf.Abs(reading.A - slot.A) > 0.005f;
                case ControlKind.Rope:
                    // The handle swings on its cleat all the time; only the cleat and the lengths are the state.
                    return Tail(reading.Text) != Tail(slot.Text);
                default:
                    return DiffersDeck(slot, reading);
            }
        }

        /// <summary>Puts the slot's state into the world. False: not possible yet, try again.</summary>
        private bool Apply(Slot slot)
        {
            switch (slot.Kind)
            {
                case ControlKind.Steering: ApplySteering(slot); return true;
                case ControlKind.Reef: ApplyReef(slot); return true;
                case ControlKind.Rope: ApplyRope(slot); return true;
                default: return ApplyDeck(slot);
            }
        }

        // --- Wheel ---

        private bool ReadSteering(out Reading reading, out bool held)
        {
            reading = default;
            reading.A = Signed(_wheel.transform.localEulerAngles.z);
            reading.B = Signed(_rudder.transform.localEulerAngles.y);
            reading.C = _visibleRudder != null ? Signed(_visibleRudder.transform.localEulerAngles.y) : reading.B;
            held = _wheelController.isInteracting;
            return true;
        }

        // The wheel is a physics body on a hinge. Its spring is what holds it where the player left it;
        // pointing the spring at the reported angle turns it the same way here. The rudder is a plain
        // transform that the game only turns while someone steers, so it is set directly — and it is
        // the rudder's angle that the host's physics reads.
        private void ApplySteering(Slot slot)
        {
            HingeJoint hinge = _wheelController.GetHingeJoint();
            if (hinge != null)
            {
                JointSpring spring = hinge.spring;
                // The game itself aims a whipstaff's spring at the negated angle.
                spring.targetPosition = _wheelController.isWheelType ? slot.A : -slot.A;
                hinge.spring = spring;
                hinge.useSpring = true;
            }
            SetYaw(_rudder.transform, slot.B);
            if (_visibleRudder != null)
            {
                SetYaw(_visibleRudder.transform, slot.C);
                if (_childRudder != null) _childRudder.transform.localRotation = _visibleRudder.transform.localRotation;
            }
        }

        // --- Reefs ---

        private bool ReadReef(int index, out Reading reading, out bool held)
        {
            reading = default;
            held = false;
            MastController mast = _masts[index];
            if (mast == null) return false;
            reading.A = mast.GetCurrentPosPercent();
            held = mast.isInteracting;
            return true;
        }

        private void ApplyReef(Slot slot)
        {
            MastController mast = _masts[slot.Index];
            if (mast == null || mast.isInteracting) return;
            // The routine below moves in steps of a frame, so it stops within a step of the point.
            if (Mathf.Abs(mast.GetCurrentPosPercent() - slot.A) < 0.01f) { DropReefDriver(slot.Index); return; }
            // The game's routine for a crew member reefing to a set point: it turns the winch and runs
            // the sail's fold stages on the way. Stepped by hand so a newer target can replace it.
            _reefDrivers[slot.Index] = mast.ReefToPoint(Mathf.Clamp01(slot.A));
        }

        private void StepReefDrivers()
        {
            for (int i = 0; i < _reefDrivers.Length; i++)
            {
                IEnumerator driver = _reefDrivers[i];
                if (driver == null) continue;
                MastController mast = _masts[i];
                if (mast == null || mast.isInteracting) { DropReefDriver(i); continue; }
                bool more = false;
                try { more = driver.MoveNext(); }
                catch (Exception e) { Report(e); }
                if (!more) _reefDrivers[i] = null;
            }
        }

        // The routine marks the mast's crew station busy at its start and free at its end. One that
        // is abandoned half-way never reaches the end, so the station is freed here.
        private void DropReefDriver(int index)
        {
            if (_reefDrivers[index] == null) return;
            _reefDrivers[index] = null;
            try
            {
                MastController mast = _masts[index];
                if (mast != null) mast.GetCrewInteractionObject()?.SetDone();
            }
            catch (Exception e) { Report(e); }
        }

        // --- Rig ropes ---

        private bool ReadRope(int index, out Reading reading, out bool held)
        {
            reading = default;
            held = false;
            RopeInteractionHandler rope = _ropes[index];
            if (rope == null) return false;
            reading.Text = rope.GetSaveString();
            if (string.IsNullOrEmpty(reading.Text)) return false;   // a mooring line or an Obi rope: not a rig rope
            float now = Time.unscaledTime;
            held = InLocalHand(rope, now);
            // Hauling one brace of a pair pays out the other.
            int[] drivers = _ropeDrivers[index];
            for (int i = 0; i < drivers.Length && !held; i++) held = InLocalHand(_ropes[drivers[i]], now);
            return true;
        }

        private bool RopeInHand(int index)
        {
            RopeInteractionHandler rope = _ropes[index];
            if (rope != null && rope.isHeld) return true;
            foreach (int driver in _ropeDrivers[index])
                if (_ropes[driver] != null && _ropes[driver].isHeld) return true;
            return false;
        }

        private static bool InLocalHand(RopeInteractionHandler rope, float now)
        {
            if (rope == null) return false;
            return rope.isHeld || (s_touchedRope == rope && now - s_touchedAt < TouchWindow);
        }

        // The state string is the game's own (RopeInteractionHandler.GetSaveString):
        //   shipId = x|y|z of the handle = cleat index = joint limit = length = second length = spar forces
        // and the game's own SetDataFromString puts it back. A rope in somebody's hand has no cleat
        // (index -1), which that method cannot take: then the handle and the cleat stay as they are
        // here and only the lengths move. The game sets the thread's culture to en-US itself, so the
        // numbers in the string read the same on every machine.
        private void ApplyRope(Slot slot)
        {
            RopeInteractionHandler rope = _ropes[slot.Index];
            if (rope == null || rope.isHeld) return;
            string[] theirs = slot.Text.Split('=');
            if (theirs.Length < 7) return;
            if (ParseInt(theirs[2]) >= 0)
            {
                rope.SetDataFromString(slot.Text);
                return;
            }

            string[] mine = rope.GetSaveString().Split('=');
            if (mine.Length >= 7 && ParseInt(mine[2]) >= 0)
            {
                theirs[1] = mine[1];
                theirs[2] = mine[2];
                rope.SetDataFromString(string.Join("=", theirs));
                return;
            }

            // No cleat on either side: set what can be set without one.
            if (RopeJointField?.GetValue(rope) is ConfigurableJoint joint && float.TryParse(theirs[3], out float limit))
            {
                SoftJointLimit linear = joint.linearLimit;
                linear.limit = limit;
                joint.linearLimit = linear;
            }
            if (rope.rigRope != null && float.TryParse(theirs[4], out float length)) rope.rigRope.length = length;
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static float Signed(float degrees) => Mathf.DeltaAngle(0f, degrees);

        private static void SetYaw(Transform t, float yaw)
        {
            Vector3 euler = t.localEulerAngles;
            euler.y = yaw;
            t.localEulerAngles = euler;
        }

        private static int ParseInt(string text) => int.TryParse(text, out int value) ? value : -1;

        /// <summary>The rope state without the ship id and the handle position.</summary>
        private static string Tail(string state)
        {
            if (string.IsNullOrEmpty(state)) return "";
            int first = state.IndexOf('=');
            int second = first < 0 ? -1 : state.IndexOf('=', first + 1);
            return second < 0 ? state : state.Substring(second + 1);
        }

        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception e) { Report(e); }
        }

        private void Report(Exception e)
        {
            if (_errors++ < 10) Plugin.Log.LogError("[Controls] " + e);
        }

        public void Clear()
        {
            Unbind();
            Sent = Applied = Refused = 0;
            _contestedAt = -100f;
            s_guest = false;
            s_pumped = 0f;
            s_touchedRope = null;
        }

        private string Counts()
        {
            return "steering " + (_steer != null ? "yes" : "no") + ", masts " + _reefs.Length + ", ropes " + _ropeSlots.Length +
                   CountsDeck() + CountsHull() + CountsBoat();
        }

        /// <summary>Diagnostics: what every control reads on this copy right now.</summary>
        public void Dump(StateDump d)
        {
            if (_ship == null) { d.Info("Controls", "ship", "not bound"); return; }
            d.Line("Controls", "ship", _ship.id ?? "");
            foreach (Slot slot in _all)
            {
                Slot s = slot;
                Reading reading = default;
                bool read = false;
                Guard(() => { read = Read(s, out Reading now, out _); reading = now; });
                if (!read) { d.Line("Controls", Name(s), "cannot be read"); continue; }
                string text = s.Kind == ControlKind.Rope ? Tail(reading.Text) : reading.Text ?? "";
                // The wheel swings on its spring and the water rises between two looks.
                float tolerance = s.Kind == ControlKind.Steering ? 3f : s.Kind == ControlKind.Water ? 100f : 0.05f;
                d.Line("Controls", Name(s), StateDump.F(reading.A) + " " + StateDump.F(reading.B) + " " + StateDump.F(reading.C) +
                       (text.Length > 0 ? " '" + text + "'" : ""), tolerance);
                if (s.HasPending) d.Info("Controls", Name(s) + " waits", "a state that is not applied yet, tries " + s.Tries);
            }
        }

        public string Describe()
        {
            if (_ship == null) return "not bound";
            int driving = 0, pending = 0;
            foreach (IEnumerator driver in _reefDrivers) if (driver != null) driving++;
            foreach (Slot slot in _all) if (slot.HasPending) pending++;
            return Counts() + "\n   sent " + Sent + "  applied " + Applied +
                   (Refused > 0 ? "  refused " + Refused : "") + (driving > 0 ? "  reefing " + driving : "") + (pending > 0 ? "  waiting " + pending : "");
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            harmony.Patch(Method(typeof(RopeInteractionHandler), "ExtendRopeRig"),
                          prefix: new HarmonyMethod(typeof(ControlsSync), nameof(RopeTouchedPrefix)));
            InstallPump(harmony);
        }

        private static MethodInfo Method(Type type, string name)
            => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.Name, name);

        // The game calls this every frame for the rope the player holds and for the rope the player
        // looks at — both take the scroll wheel. Nothing else calls it, so it marks the local hand.
        private static void RopeTouchedPrefix(RopeInteractionHandler __instance)
        {
            PatchGuard.Run(() =>
            {
                s_touchedRope = __instance;
                s_touchedAt = Time.unscaledTime;
            }, PatchError);
        }

        private static void PatchError(Exception error) => Plugin.Log.LogError("[Controls] " + error);
    }
}
