using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using FullRig;
using HarmonyLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// More kinds of <see cref="ControlsSync"/>: the rowboat's launch, the reel of a trolling line,
    /// what the host's crew is doing at the helm and the pumps, and the length of a mooring line.
    /// </summary>
    internal sealed partial class ControlsSync
    {
        private static readonly FieldInfo StaticRowboatField = AccessTools.Field(typeof(ShipHullMover), "staticRowboat");
        private static readonly FieldInfo TrollOuterLineField = AccessTools.Field(typeof(TrollController), "outerFishingLine");
        private static readonly FieldInfo TrollInnerLineField = AccessTools.Field(typeof(TrollController), "innerFishingLine");
        private static readonly FieldInfo TrollHandField = AccessTools.Field(typeof(TrollController), "interacting");
        private static readonly FieldInfo MoorRopeField = AccessTools.Field(typeof(RopeInteractionHandler), "rope");
        private static readonly FieldInfo MoorCursorField = AccessTools.Field(typeof(RopeInteractionHandler), "cursor");
        private static readonly MethodInfo MoorJointMethod = AccessTools.Method(typeof(RopeInteractionHandler), "UpdateConfigJoint");

        private Slot _rowboat, _crewHelm;
        private Slot[] _reelSlots = new Slot[0];
        private PumpController[] _crewPumps = new PumpController[0];

        /// <summary>Guest: the host's crew is at the helm. Read by the crew panel's patch.</summary>
        public static bool HostCrewSteers { get; private set; }
        /// <summary>Guest: how many of the host's crew are at the pumps.</summary>
        public static int HostCrewPumping { get; private set; }
        private static float s_hostCourse;

        private void BindBoat()
        {
            // The boat on deck is what the game switches off when it launches the one in the water.
            if (StaticRowboatField?.GetValue(_ship) as GameObject != null && RowboatSync.Of(_ship) != null)
                _all.Add(_rowboat = new Slot { Kind = ControlKind.Rowboat });
            if (TrollOuterLineField != null && TrollInnerLineField != null && TrollHandField != null)
                _reelSlots = MakeSlots(ControlKind.TrollLine, _trolls.Length);
            if (_wheelController != null) _all.Add(_crewHelm = new Slot { Kind = ControlKind.CrewHelm });
            _crewPumps = _ship.GetComponentsInChildren<PumpController>(true);
        }

        // How many of this copy's crew are at the pumps, or on their way there.
        private int PumpingCrew()
        {
            int working = 0;
            foreach (PumpController pump in _crewPumps)
                if (pump != null && pump.isActiveAndEnabled && pump.crewTarget != null &&
                    (pump.crewTarget.interacting || pump.crewTarget.assigned)) working++;
            return working;
        }

        private void UnbindBoat()
        {
            _rowboat = _crewHelm = null;
            _reelSlots = new Slot[0];
            _crewPumps = new PumpController[0];
            HostCrewSteers = false;
            HostCrewPumping = 0;
        }

        private Slot FindBoat(ControlKind kind, int index)
        {
            switch (kind)
            {
                case ControlKind.Rowboat: return _rowboat;
                case ControlKind.TrollLine: return At(_reelSlots, index);
                case ControlKind.CrewHelm: return _crewHelm;
                default: return null;
            }
        }

        private string CountsBoat()
        {
            return ", rowboat " + (_rowboat != null ? "yes" : "no") + ", reels " + _reelSlots.Length;
        }

        private bool ReadBoat(Slot slot, ref Reading reading, out bool held)
        {
            held = false;
            switch (slot.Kind)
            {
                case ControlKind.Rowboat:
                    reading.A = _ship.rowboatDeployed ? 1f : 0f;
                    return true;
                case ControlKind.TrollLine:
                {
                    TrollController troll = _trolls[slot.Index];
                    if (troll == null) return false;
                    string text = ReelText(troll);
                    if (text == null) return false;   // the line has not made its hooks yet
                    reading.Text = text;
                    held = (bool)TrollHandField.GetValue(troll);
                    return true;
                }
                case ControlKind.CrewHelm:
                    if (s_guest)
                    {
                        // No crew works on a guest's copy: it reads what the host said.
                        reading.A = HostCrewSteers ? 1f : 0f;
                        reading.B = s_hostCourse;
                        reading.C = HostCrewPumping;
                        return true;
                    }
                    reading.A = _ship.crewPiloting ? 1f : 0f;
                    reading.B = _ship.crewPiloting ? _ship.setCourseDeg : 0f;
                    reading.C = PumpingCrew();
                    return true;
                default:
                    return false;
            }
        }

        private static bool DiffersBoat(Slot slot, Reading reading)
        {
            switch (slot.Kind)
            {
                case ControlKind.Rowboat:
                    return (reading.A > 0.5f) != (slot.A > 0.5f);
                case ControlKind.TrollLine:
                    return (reading.Text ?? "") != slot.Text;
                case ControlKind.CrewHelm:
                    return (reading.A > 0.5f) != (slot.A > 0.5f) || Mathf.Abs(reading.B - slot.B) > 0.5f ||
                           Mathf.RoundToInt(reading.C) != Mathf.RoundToInt(slot.C);
                default:
                    return false;
            }
        }

        private bool ApplyBoat(Slot slot)
        {
            switch (slot.Kind)
            {
                case ControlKind.Rowboat:
                    // The game's own switch: the boat on deck goes, the one in the water appears beside the ship.
                    if (_ship.rowboatDeployed != slot.A > 0.5f) _ship.DeployRowboat();
                    return true;
                case ControlKind.TrollLine:
                {
                    TrollController troll = _trolls[slot.Index];
                    if (troll != null && !(bool)TrollHandField.GetValue(troll)) ApplyReel(troll, slot.Text);
                    return true;
                }
                case ControlKind.CrewHelm:
                    if (!s_guest) return true;
                    HostCrewSteers = slot.A > 0.5f;
                    s_hostCourse = slot.B;
                    HostCrewPumping = Mathf.Max(0, Mathf.RoundToInt(slot.C));
                    // The game keeps the player's hand off a wheel the crew has.
                    if (_wheelController != null) _wheelController.crewUsing = HostCrewSteers;
                    return true;
                default:
                    return true;
            }
        }

        // --- The reel of a trolling line ---

        // Reeling moves the hooks along the two lines: each hook's place is a share of the line's
        // length, kept by the line itself. The state is those shares, outer line then inner.
        private static string ReelText(TrollController troll)
        {
            var outer = TrollOuterLineField.GetValue(troll) as Rope;
            var inner = TrollInnerLineField.GetValue(troll) as Rope;
            if (outer == null || inner == null || outer.attached == null || inner.attached == null) return null;
            var text = new StringBuilder();
            foreach (Rope line in new[] { outer, inner })
            {
                if (text.Length > 0) text.Append(';');
                for (int i = 0; i < line.attached.Length; i++)
                {
                    if (line.attached[i] == null) return null;
                    if (i > 0) text.Append('|');
                    text.Append(line.attached[i].alpha.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }
            return text.Length <= Limits.ControlText ? text.ToString() : null;
        }

        private static void ApplyReel(TrollController troll, string text)
        {
            var outer = TrollOuterLineField.GetValue(troll) as Rope;
            var inner = TrollInnerLineField.GetValue(troll) as Rope;
            string[] lines = (text ?? "").Split(';');
            if (outer == null || inner == null || lines.Length != 2) return;
            Rope[] ropes = { outer, inner };
            for (int l = 0; l < 2; l++)
            {
                if (ropes[l].attached == null) continue;
                string[] shares = lines[l].Split('|');
                for (int i = 0; i < shares.Length && i < ropes[l].attached.Length; i++)
                    if (ropes[l].attached[i] != null && TryNum(shares[i], out float share))
                        ropes[l].attached[i].alpha = Mathf.Clamp01(share);
            }
        }

        // --- The length of a mooring line ---

        // A mooring line is a rope of the Obi library; the player lets it out and takes it in with
        // the scroll wheel while holding it. Its rest length is read and set through the same two
        // calls the game makes. The library is reached by name, the mod has no reference to it.
        private static float MooringLength(RopeInteractionHandler line)
        {
            object rope = MoorRopeField?.GetValue(line);
            if (rope == null || rope.Equals(null)) return 0f;
            PropertyInfo length = rope.GetType().GetProperty("restLength");
            return length != null ? Convert.ToSingle(length.GetValue(rope)) : 0f;
        }

        private static void SetMooringLength(RopeInteractionHandler line, float length)
        {
            if (length <= 0f || Mathf.Abs(MooringLength(line) - length) < 0.05f) return;
            object cursor = MoorCursorField?.GetValue(line);
            if (cursor == null || cursor.Equals(null)) return;
            MethodInfo change = cursor.GetType().GetMethod("ChangeLength", new[] { typeof(float) });
            if (change == null) return;
            change.Invoke(cursor, new object[] { length });
            MoorJointMethod?.Invoke(line, new object[] { length });
        }
    }
}
