using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using FullRig;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The deck kinds of <see cref="ControlsSync"/>: winches, doors, lanterns, ballast, the kedge
    /// anchor, mooring lines, the water in the hull and the pump.
    /// </summary>
    internal sealed partial class ControlsSync
    {
        private static readonly FieldInfo WinchJointField = AccessTools.Field(typeof(ProgressiveInteractionPoint), "joint");
        private static readonly FieldInfo WinchTargetsField = AccessTools.Field(typeof(ProgressiveInteractionPoint), "targets");
        private static readonly FieldInfo WinchAngleField = AccessTools.Field(typeof(ProgressiveInteractionPoint), "curAngle");
        private static readonly FieldInfo WinchPercentField = AccessTools.Field(typeof(ProgressiveInteractionPoint), "curPercent");
        private static readonly FieldInfo DoorChildField = AccessTools.Field(typeof(DoorController), "childDoor");
        private static readonly FieldInfo DoorAudioField = AccessTools.Field(typeof(DoorController), "sam");
        private static readonly FieldInfo MooringsField = AccessTools.Field(typeof(ShipHullMover), "mooringRopes");
        private static readonly FieldInfo MoorSpringField = AccessTools.Field(typeof(RopeInteractionHandler), "springjoint");
        private static readonly FieldInfo MoorAttachedField = AccessTools.Field(typeof(RopeInteractionHandler), "attachedRope");
        private static readonly FieldInfo MassHolderField = AccessTools.Field(typeof(ShipHullMover), "vhcm");
        private static readonly FieldInfo KedgePrefabField = AccessTools.Field(typeof(KedgingController), "kedgePoint");
        private static readonly FieldInfo KedgeCurrentField = AccessTools.Field(typeof(KedgingController), "currentKedgePoint");
        private static readonly FieldInfo KedgeRopeInitField = AccessTools.Field(typeof(KedgingController), "ropeInitPoint");
        private static readonly FieldInfo KedgeShipField = AccessTools.Field(typeof(KedgingController), "parentShip");
        private static readonly FieldInfo KedgeAttachField = AccessTools.Field(typeof(KedgingController), "attachPoint");

        // Written by the patches, which are static.
        private static bool s_guest;
        private static bool s_applying;
        private static bool s_playerPump;
        private static float s_pumped;
        private static readonly HashSet<UnityEngine.Object> s_switched = new HashSet<UnityEngine.Object>();

        private readonly List<ProgressiveInteractionPoint> _winches = new List<ProgressiveInteractionPoint>();
        private readonly List<DoorController> _doors = new List<DoorController>();
        private readonly List<LightController> _lanterns = new List<LightController>();
        private readonly List<RopeInteractionHandler> _moorings = new List<RopeInteractionHandler>();
        private AnchorController[] _capstans = new AnchorController[0];
        private KedgeAnchorParts _kedgeParts;
        private BallastController _ballastUi;
        private Rigidbody _hull;
        private object _massHolder;
        private FieldInfo _massField;
        private PropertyInfo _massProperty;
        private Slot[] _winchSlots = new Slot[0];
        private Slot[] _doorSlots = new Slot[0];
        private Slot[] _lanternSlots = new Slot[0];
        private Slot[] _mooringSlots = new Slot[0];
        private Slot _ballast, _anchor, _water;

        // -----------------------------------------------------------------
        // Binding
        // -----------------------------------------------------------------

        private void BindDeck()
        {
            ShipHullMover ship = _ship;
            // Components of the ship prefab in hierarchy order: the same on every machine.
            _winches.AddRange(ship.GetComponentsInChildren<ProgressiveInteractionPoint>(true));
            _winchSlots = MakeSlots(ControlKind.Winch, _winches.Count);

            // The second leaf of a double door is swung by the first one's own code.
            DoorController[] doors = ship.GetComponentsInChildren<DoorController>(true);
            var leaves = new HashSet<DoorController>();
            foreach (DoorController door in doors)
                if (DoorChildField?.GetValue(door) is DoorController child && child != null) leaves.Add(child);
            foreach (DoorController door in doors)
                if (!leaves.Contains(door)) _doors.Add(door);
            _doorSlots = MakeSlots(ControlKind.Door, _doors.Count);

            if (ship.lanterns != null) _lanterns.AddRange(ship.lanterns);
            _lanternSlots = MakeSlots(ControlKind.Lantern, _lanterns.Count);

            if (MooringsField?.GetValue(ship) is List<RopeInteractionHandler> moorings) _moorings.AddRange(moorings);
            _mooringSlots = MakeSlots(ControlKind.Mooring, _moorings.Count);

            _ballastUi = ship.GetComponentInChildren<BallastController>(true);
            _all.Add(_ballast = new Slot { Kind = ControlKind.Ballast });

            _capstans = ship.GetComponentsInChildren<AnchorController>(true);
            _kedgeParts = ship.GetComponentInChildren<KedgeAnchorParts>(true);
            if (ship.GetKedgeAnchorPoint() != null && ship.GetKedgeAnchorRope() != null)
                _all.Add(_anchor = new Slot { Kind = ControlKind.Anchor });

            _hull = ship.GetComponent<Rigidbody>();
            _massHolder = MassHolderField?.GetValue(ship);
            if (_massHolder != null && !_massHolder.Equals(null))
            {
                _massField = AccessTools.Field(_massHolder.GetType(), "baseMass");
                _massProperty = _massField == null ? AccessTools.Property(_massHolder.GetType(), "baseMass") : null;
            }
            if (_hull != null && (_massField != null || _massProperty != null))
                _all.Add(_water = new Slot { Kind = ControlKind.Water });
            else
                Plugin.Log.LogWarning("[Controls] The hull's mass was not found; water in the hull is not shared");
        }

        private void UnbindDeck()
        {
            _winches.Clear();
            _doors.Clear();
            _lanterns.Clear();
            _moorings.Clear();
            _capstans = new AnchorController[0];
            _kedgeParts = null;
            _ballastUi = null;
            _hull = null;
            _massHolder = null;
            _massField = null;
            _massProperty = null;
            _winchSlots = new Slot[0];
            _doorSlots = new Slot[0];
            _lanternSlots = new Slot[0];
            _mooringSlots = new Slot[0];
            _ballast = _anchor = _water = null;
            s_switched.Clear();
        }

        private Slot FindDeck(ControlKind kind, int index)
        {
            switch (kind)
            {
                case ControlKind.Winch: return At(_winchSlots, index);
                case ControlKind.Door: return At(_doorSlots, index);
                case ControlKind.Lantern: return At(_lanternSlots, index);
                case ControlKind.Mooring: return At(_mooringSlots, index);
                case ControlKind.Ballast: return _ballast;
                case ControlKind.Anchor: return _anchor;
                case ControlKind.Water: return _water;
                default: return FindHull(kind, index);
            }
        }

        private string CountsDeck()
        {
            return ", winches " + _winchSlots.Length + ", doors " + _doorSlots.Length + ", lanterns " + _lanternSlots.Length +
                   ", moorings " + _mooringSlots.Length + ", anchor " + (_anchor != null ? "yes" : "no") +
                   ", water " + (_water != null ? "yes" : "no");
        }

        private bool ReadDeck(Slot slot, out Reading reading, out bool held)
        {
            reading = default;
            held = false;
            switch (slot.Kind)
            {
                case ControlKind.Winch: return ReadWinch(slot.Index, ref reading, out held);
                case ControlKind.Door:
                {
                    DoorController door = _doors[slot.Index];
                    if (door == null) return false;
                    reading.A = door.open ? 1f : 0f;
                    held = s_switched.Remove(door);
                    return true;
                }
                case ControlKind.Lantern:
                {
                    LightController light = _lanterns[slot.Index];
                    if (light == null) return false;
                    reading.A = light.isOn ? 1f : 0f;
                    held = s_switched.Remove(light);
                    return true;
                }
                case ControlKind.Ballast:
                    reading.A = _ship.GetCurrentBallast();
                    reading.B = _ship.GetBallastBowSternVal();
                    reading.C = _ship.GetBallastPortStarboardVal();
                    held = _ballastUi != null && _ballastUi.interacting;
                    return true;
                case ControlKind.Anchor: return ReadAnchor(ref reading, out held);
                case ControlKind.Mooring: return ReadMooring(slot.Index, ref reading, out held);
                case ControlKind.Water:
                    reading.A = HullMass;
                    return reading.A > 0f;
                default: return ReadHull(slot, ref reading);
            }
        }

        private bool DiffersDeck(Slot slot, Reading reading)
        {
            switch (slot.Kind)
            {
                case ControlKind.Winch:
                    return Mathf.Abs(reading.A - slot.A) > 0.05f || Mathf.Abs(reading.B - slot.B) > 0.4f ||
                           (reading.Text ?? "") != slot.Text;
                case ControlKind.Door:
                case ControlKind.Lantern:
                    return (reading.A > 0.5f) != (slot.A > 0.5f);
                case ControlKind.Ballast:
                    return Mathf.Abs(reading.A - slot.A) > 0.5f || Mathf.Abs(reading.B - slot.B) > 0.002f ||
                           Mathf.Abs(reading.C - slot.C) > 0.002f;
                case ControlKind.Anchor:
                    if ((reading.B > 0.5f) != (slot.B > 0.5f)) return true;
                    if (reading.B < 0.5f) return false;
                    if (Mathf.Abs(reading.A - slot.A) > 0.02f) return true;
                    return !TryParsePoint(reading.Text, out double x, out double y, out double z) ||
                           !TryParsePoint(slot.Text, out double sx, out double sy, out double sz) ||
                           Math.Abs(x - sx) + Math.Abs(y - sy) + Math.Abs(z - sz) > 0.5;
                case ControlKind.Mooring:
                    return Mathf.RoundToInt(reading.A) != Mathf.RoundToInt(slot.A);
                case ControlKind.Water:
                    return Mathf.Abs(reading.A - slot.A) > 1f;
                default:
                    return DiffersHull(slot, reading);
            }
        }

        private bool ApplyDeck(Slot slot)
        {
            switch (slot.Kind)
            {
                case ControlKind.Winch: ApplyWinch(slot); return true;
                case ControlKind.Door: return ApplyDoor(slot);
                case ControlKind.Lantern:
                {
                    LightController light = _lanterns[slot.Index];
                    if (light == null || light.isOn == slot.A > 0.5f) return true;
                    // Not ToggleLight: that one asks the local oil store, and the host's is what counts.
                    if (slot.A > 0.5f) light.SetLightOn(); else light.DisableLight();
                    return true;
                }
                case ControlKind.Ballast:
                    _ship.UpdateCurrentBallast(slot.A, slot.B, slot.C);
                    return true;
                case ControlKind.Anchor: return ApplyAnchor(slot);
                case ControlKind.Mooring: return ApplyMooring(slot);
                case ControlKind.Water:
                    if (slot.A > 0f) HullMass = slot.A;
                    return true;
                default: return ApplyHull(slot);
            }
        }

        // -----------------------------------------------------------------
        // Winches (ProgressiveInteractionPoint): the gaff peak, the fishing poles
        // -----------------------------------------------------------------

        private bool ReadWinch(int index, ref Reading reading, out bool held)
        {
            held = false;
            ProgressiveInteractionPoint winch = _winches[index];
            if (winch == null) return false;
            held = winch.isInteracting;
            reading.B = winch.GetPercent();
            if (winch.isJointType && WinchJointField?.GetValue(winch) is ConfigurableJoint joint && joint != null)
            {
                reading.A = joint.lowAngularXLimit.limit;
                reading.Text = "";
                return true;
            }
            reading.A = WinchAngleField != null ? (float)WinchAngleField.GetValue(winch) : 0f;
            var text = new StringBuilder();
            if (WinchTargetsField?.GetValue(winch) is List<Transform> targets)
            {
                foreach (Transform target in targets)
                {
                    if (text.Length > 0) text.Append(';');
                    Vector3 euler = target != null ? target.localEulerAngles : Vector3.zero;
                    text.Append(Num(euler.x)).Append('|').Append(Num(euler.y)).Append('|').Append(Num(euler.z));
                }
            }
            reading.Text = text.ToString();
            return true;
        }

        private void ApplyWinch(Slot slot)
        {
            ProgressiveInteractionPoint winch = _winches[slot.Index];
            if (winch == null || winch.isInteracting) return;
            if (winch.isJointType && WinchJointField?.GetValue(winch) is ConfigurableJoint joint && joint != null)
            {
                SoftJointLimit limit = joint.lowAngularXLimit;
                limit.limit = slot.A;
                joint.lowAngularXLimit = limit;
            }
            else if (WinchTargetsField?.GetValue(winch) is List<Transform> targets && slot.Text.Length > 0)
            {
                string[] parts = slot.Text.Split(';');
                for (int i = 0; i < targets.Count && i < parts.Length; i++)
                {
                    string[] xyz = parts[i].Split('|');
                    if (targets[i] == null || xyz.Length != 3) continue;
                    if (TryNum(xyz[0], out float x) && TryNum(xyz[1], out float y) && TryNum(xyz[2], out float z))
                        targets[i].localRotation = Quaternion.Euler(x, y, z);
                }
            }
            // The game derives "deployed" and the sail's share of power from these two.
            WinchAngleField?.SetValue(winch, slot.A);
            WinchPercentField?.SetValue(winch, slot.B);
        }

        // -----------------------------------------------------------------
        // Doors
        // -----------------------------------------------------------------

        private bool ApplyDoor(Slot slot)
        {
            DoorController door = _doors[slot.Index];
            if (door == null || door.open == slot.A > 0.5f) return true;
            // SwapState plays its sound through a manager the door finds a moment after it wakes up.
            if (!door.isAnimOnly && (DoorAudioField?.GetValue(door) as UnityEngine.Object) == null) return false;
            s_applying = true;
            try { door.SwapState(); }
            finally { s_applying = false; }
            return true;
        }

        // -----------------------------------------------------------------
        // Kedge anchor
        // -----------------------------------------------------------------

        // The game lays the anchor from a component on the local player's camera: it instantiates a
        // point with a spring joint to the ship and hands the joint to the ship, whose physics then
        // holds it. On the host that joint is what stops the ship, so a guest's anchor has to exist
        // there; everywhere else it is the visible cable and the game's own "retract" prompt.
        private bool ReadAnchor(ref Reading reading, out bool held)
        {
            held = false;
            foreach (AnchorController capstan in _capstans)
                if (capstan != null && capstan.isInteracting) held = true;
            SpringJoint joint = _ship.GetKedgeAnchorJoint();
            if (joint == null) { reading.Text = ""; return true; }
            reading.B = 1f;
            reading.A = joint.maxDistance;
            CoordSpace.LocalToReal(joint.transform.position, out double x, out double y, out double z);
            reading.Text = Num(x) + "|" + Num(y) + "|" + Num(z);
            return true;
        }

        private static KedgingController LocalKedging()
        {
            PlayerHandler player = GameAccess.Player;
            GameObject camera = player != null ? player.GetCameraObject() : null;
            return camera != null ? camera.GetComponent<KedgingController>() : null;
        }

        private bool ApplyAnchor(Slot slot)
        {
            SpringJoint joint = _ship.GetKedgeAnchorJoint();
            KedgingController kedging = LocalKedging();
            if (slot.B < 0.5f)
            {
                if (joint != null) ClearKedge(kedging, joint);
                return true;
            }
            if (!TryParsePoint(slot.Text, out double x, out double y, out double z)) return true;
            Vector3 position = CoordSpace.RealToLocal(x, y, z);
            if (joint != null && Vector3.Distance(joint.transform.position, position) < 0.5f)
            {
                joint.maxDistance = slot.A;   // the capstan
                return true;
            }

            GameObject prefab = kedging != null ? KedgePrefabField?.GetValue(kedging) as GameObject : null;
            GameObject attach = _ship.GetKedgeAnchorPoint();
            if (prefab == null || attach == null) return false;
            if (joint != null) ClearKedge(kedging, joint);

            GameObject point = UnityEngine.Object.Instantiate(prefab);
            point.transform.position = position;
            SpringJoint spring = point.GetComponent<SpringJoint>();
            if (spring == null) { UnityEngine.Object.Destroy(point); return true; }
            spring.connectedBody = attach.GetComponent<Rigidbody>();
            spring.maxDistance = slot.A;
            spring.minDistance = 0f;
            _ship.SetKedgeAnchorJoint(spring);

            GameObject cable = _ship.GetKedgeAnchorRope();
            Rope rope = cable != null ? cable.GetComponent<Rope>() : null;
            if (rope != null)
            {
                if ((KedgeRopeInitField?.GetValue(kedging) as Transform) == null) KedgeRopeInitField?.SetValue(kedging, rope.endAttach);
                rope.endAttach = point.transform;
            }
            // So the game's own "Retract Kedge Anchor" works for the local player afterwards: its code
            // reads these, and fills them itself only once the player has laid an anchor personally.
            KedgeCurrentField?.SetValue(kedging, point);
            if ((KedgeShipField?.GetValue(kedging) as UnityEngine.Object) == null) KedgeShipField?.SetValue(kedging, _ship);
            if ((KedgeAttachField?.GetValue(kedging) as UnityEngine.Object) == null) KedgeAttachField?.SetValue(kedging, attach);
            ShowKedgeLaid(true);
            return true;
        }

        private void ClearKedge(KedgingController kedging, SpringJoint joint)
        {
            GameObject point = joint.gameObject;
            GameObject cable = _ship.GetKedgeAnchorRope();
            Rope rope = cable != null ? cable.GetComponent<Rope>() : null;
            Transform rest = kedging != null ? KedgeRopeInitField?.GetValue(kedging) as Transform : null;
            if (rope != null && rest != null) rope.endAttach = rest;
            _ship.SetKedgeAnchorJoint(null);
            if (kedging != null) KedgeCurrentField?.SetValue(kedging, null);
            UnityEngine.Object.Destroy(point);
            ShowKedgeLaid(false);
        }

        private void ShowKedgeLaid(bool laid)
        {
            if (_kedgeParts != null) _kedgeParts.SetDeployed(laid);
            if (_ship.kedgeHideObject != null) _ship.kedgeHideObject.SetActive(!laid);
        }

        // -----------------------------------------------------------------
        // Mooring lines
        // -----------------------------------------------------------------

        // A moored line hangs under the dock's cleat instead of under the ship, and the cleat's spring
        // joint holds the ship. The cleat is named by its index in the world's own list, the one the
        // game's save uses.
        private bool ReadMooring(int index, ref Reading reading, out bool held)
        {
            held = false;
            RopeInteractionHandler line = _moorings[index];
            if (line == null) return false;
            held = line.isHeld;
            reading.A = -1f;
            if (held || line.transform.root == _ship.transform) return true;
            int cleat = CleatIndex(line.transform);
            if (cleat < 0) return false;   // tied to something the list does not know
            reading.A = cleat;
            return true;
        }

        private static int CleatIndex(Transform line)
        {
            List<GameObject> cleats = GameAccess.Dp?.cleats;
            if (cleats == null) return -1;
            // The line hangs under the cleat's mooring point, which is a child of the listed object.
            Transform t = line.parent;
            for (int depth = 0; depth < 4 && t != null; depth++, t = t.parent)
            {
                int index = cleats.IndexOf(t.gameObject);
                if (index >= 0) return index;
            }
            return -1;
        }

        private bool ApplyMooring(Slot slot)
        {
            RopeInteractionHandler line = _moorings[slot.Index];
            if (line == null || line.isHeld) return true;
            int want = Mathf.RoundToInt(slot.A);
            int have = line.transform.root == _ship.transform ? -1 : CleatIndex(line.transform);
            bool moored = line.transform.root != _ship.transform;
            if (want < 0)
            {
                if (moored) Unmoor(line);
                return true;
            }
            if (moored && have == want) return true;

            List<GameObject> cleats = GameAccess.Dp?.cleats;
            if (cleats == null || want >= cleats.Count) return true;
            GameObject cleat = cleats[want];
            if (cleat == null) return false;   // that dock is not loaded here yet
            if (moored) Unmoor(line);

            GameObject target = cleat;
            foreach (Transform child in cleat.GetComponentsInChildren<Transform>(true))
                if (child.CompareTag("MoorRoot")) { target = child.gameObject; break; }
            // As the game's own load does.
            Rigidbody body = line.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = false;
            line.MoorRope(target);
            return true;
        }

        // What the game does over two steps when the player takes a moored line off its cleat and
        // lets go of it: free the cleat's spring, hang the line back on the ship, send it to its rest.
        private void Unmoor(RopeInteractionHandler line)
        {
            if (MoorSpringField?.GetValue(line) is SpringJoint spring && spring != null) spring.connectedBody = null;
            MoorSpringField?.SetValue(line, null);
            line.allowRecenter = true;
            Transform home = MoorAttachedField?.GetValue(line) is GameObject attached && attached != null
                ? attached.transform : _ship.transform;
            line.transform.parent = home;
            line.lastParent = null;
            line.mainTarget = line.restPointForMoorRope;
            Rigidbody body = line.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = false;
        }

        // -----------------------------------------------------------------
        // Water in the hull and the pump
        // -----------------------------------------------------------------

        // The game keeps the water as extra mass of the hull. Only the host's hull floats, so its
        // mass is the truth: guests show it, and what a guest pumps is taken out of the host's hull.
        private float HullMass
        {
            get
            {
                if (_massHolder == null) return 0f;
                object value = _massField != null ? _massField.GetValue(_massHolder) : _massProperty?.GetValue(_massHolder);
                return value != null ? Convert.ToSingle(value) : 0f;
            }
            set
            {
                if (_massHolder == null) return;
                if (_massField != null) _massField.SetValue(_massHolder, value);
                else _massProperty?.SetValue(_massHolder, value);
                if (_hull != null) _hull.mass = value;
            }
        }

        private void SendPumped()
        {
            if (s_pumped <= 0f) return;
            var msg = new ControlRequestMsg { Kind = ControlKind.Pump, Holder = _net.MyNetId, A = s_pumped };
            s_pumped = 0f;
            _net.SendToHost(msg, DeliveryMethod.ReliableOrdered);
            Sent++;
        }

        private void HostPump(float amount)
        {
            if (float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;
            _ship.Pump(amount);
            Applied++;
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static bool TryNum(string text, out float value)
            => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static bool TryParsePoint(string text, out double x, out double y, out double z)
        {
            x = y = z = 0.0;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('|');
            return parts.Length == 3 &&
                   double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                   double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                   double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        private static void InstallPump(Harmony harmony)
        {
            // Doors and lanterns are switched by a call, and only the local player makes that call.
            harmony.Patch(Method(typeof(DoorController), "SwapState"),
                          postfix: new HarmonyMethod(typeof(ControlsSync), nameof(SwitchedPostfix)));
            harmony.Patch(Method(typeof(LightController), "ToggleLight"),
                          postfix: new HarmonyMethod(typeof(ControlsSync), nameof(SwitchedPostfix)));

            // The player's pumping is told apart from the crew's by where the call comes from.
            var end = new HarmonyMethod(typeof(ControlsSync), nameof(PlayerPumpEnd));
            harmony.Patch(Method(typeof(PumpController), "Update"),
                          prefix: new HarmonyMethod(typeof(ControlsSync), nameof(PumpUpdatePrefix)), finalizer: end);
            harmony.Patch(Method(typeof(InteractionController), "HandlePumping"),
                          prefix: new HarmonyMethod(typeof(ControlsSync), nameof(PlayerPumpBegin)), finalizer: end);
            harmony.Patch(AccessTools.Method(typeof(ShipHullMover), "Pump", new[] { typeof(float) })
                              ?? throw new MissingMethodException("ShipHullMover", "Pump"),
                          postfix: new HarmonyMethod(typeof(ControlsSync), nameof(ShipPumpPostfix)));
        }

        private static void SwitchedPostfix(UnityEngine.Object __instance)
        {
            PatchGuard.Run(() => { if (!s_applying) s_switched.Add(__instance); }, PatchError);
        }

        private static void PumpUpdatePrefix(PumpController __instance)
        {
            PatchGuard.Run(() => s_playerPump = __instance.isInteracting, PatchError);
        }

        private static void PlayerPumpBegin() => s_playerPump = true;

        private static void PlayerPumpEnd() => s_playerPump = false;

        private static void ShipPumpPostfix(float amount)
        {
            PatchGuard.Run(() => { if (s_playerPump && s_guest && amount > 0f) s_pumped += amount; }, PatchError);
        }
    }
}
