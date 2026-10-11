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
        private static readonly FieldInfo PumpAnimatorField = AccessTools.Field(typeof(PumpController), "animator");
        private static readonly FieldInfo PumpAudioField = AccessTools.Field(typeof(PumpController), "pumpAudio");
        private static readonly FieldInfo PumpPistonField = AccessTools.Field(typeof(PumpController), "isPistonType");
        private static readonly FieldInfo PumpWheelField = AccessTools.Field(typeof(PumpController), "isWheelType");
        private static readonly FieldInfo PumpJunkField = AccessTools.Field(typeof(PumpController), "isJunkType");
        private const int MaxPumpBits = 24;   // the mask travels as a float

        private Slot _rowboat, _crewHelm;
        private Slot[] _reelSlots = new Slot[0];
        private PumpController[] _crewPumps = new PumpController[0];
        private Animator[] _pumpAnimators = new Animator[0];
        private AudioSource[] _pumpSounds = new AudioSource[0];
        private bool[] _pumpShown = new bool[0];
        private float[] _pumpBeatAt = new float[0];

        /// <summary>Guest: the host's crew is at the helm. Read by the crew panel's patch.</summary>
        public static bool HostCrewSteers { get; private set; }
        /// <summary>Guest: how many of the host's crew are at the pumps.</summary>
        public static int HostCrewPumping
        {
            get
            {
                int count = 0;
                for (int mask = s_hostPumps; mask != 0; mask >>= 1) count += mask & 1;
                return count;
            }
        }
        private static int s_hostPumps;
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
            _pumpShown = new bool[_crewPumps.Length];
            _pumpBeatAt = new float[_crewPumps.Length];
            _pumpAnimators = new Animator[_crewPumps.Length];
            _pumpSounds = new AudioSource[_crewPumps.Length];
            for (int i = 0; i < _crewPumps.Length; i++)
            {
                _pumpAnimators[i] = PumpAnimatorField?.GetValue(_crewPumps[i]) as Animator;
                _pumpSounds[i] = PumpAudioField?.GetValue(_crewPumps[i]) as AudioSource;
            }
        }

        /// <summary>
        /// A pump whose crew member is gone from the crew goes on turning: the game's pumping
        /// routine runs on the crew member and ends with it, before it can stop the pump.
        /// </summary>
        public static void StopPump(ShipHullMover ship, CrewInteractionObject station)
        {
            foreach (PumpController pump in ship.GetComponentsInChildren<PumpController>(true))
            {
                if (pump.crewTarget != station) continue;
                if (pump.isContinuousType && PumpAnimatorField?.GetValue(pump) is Animator animator && animator != null &&
                    animator.isActiveAndEnabled && !pump.isInteracting) animator.SetFloat("mult", 0f);
                if (PumpAudioField?.GetValue(pump) is AudioSource sound && sound != null && sound.isPlaying) sound.Stop();
            }
        }

        // The pumps this copy's crew works, one bit per pump in the ship's own order. A hand that is
        // still walking to a pump that turns all the time is not at it yet: that pump's own
        // animation says when the work began.
        private int CrewPumps()
        {
            int mask = 0;
            for (int i = 0; i < _crewPumps.Length && i < MaxPumpBits; i++)
            {
                PumpController pump = _crewPumps[i];
                if (pump == null || !pump.isActiveAndEnabled || pump.crewTarget == null || !pump.crewTarget.interacting) continue;
                Animator animator = _pumpAnimators[i];
                if (pump.isContinuousType && animator != null && animator.isActiveAndEnabled && animator.GetFloat("mult") < 0.5f) continue;
                mask |= 1 << i;
            }
            return mask;
        }

        // A guest's crew does not pump, so its pumps would stand still under the hands the host
        // sent there. They are moved the way the game moves them — the turning of a pump that
        // turns all the time, a stroke a second of the others — and nothing is taken out of the
        // hull here: the water is the host's figure.
        private void ShowCrewPumps()
        {
            float now = Time.time;
            for (int i = 0; i < _crewPumps.Length && i < MaxPumpBits; i++)
            {
                PumpController pump = _crewPumps[i];
                if (pump == null || !pump.isActiveAndEnabled) continue;
                bool on = (s_hostPumps & (1 << i)) != 0 && !pump.isInteracting;
                Animator animator = _pumpAnimators[i];
                AudioSource sound = _pumpSounds[i];
                if (pump.isContinuousType)
                {
                    if (on == _pumpShown[i]) continue;
                    _pumpShown[i] = on;
                    // In the local player's hand the pump is the game's own again.
                    if (pump.isInteracting) continue;
                    if (animator != null && animator.isActiveAndEnabled) animator.SetFloat("mult", on ? 1f : 0f);
                    if (sound != null)
                    {
                        if (on && !sound.isPlaying) sound.Play();
                        else if (!on && sound.isPlaying) sound.Stop();
                    }
                    continue;
                }
                if (!on || now < _pumpBeatAt[i]) continue;
                _pumpBeatAt[i] = now + 1f;
                string stroke = PumpPistonField != null && (bool)PumpPistonField.GetValue(pump) ? "Base Layer.pistonpump_pump"
                    : PumpWheelField != null && (bool)PumpWheelField.GetValue(pump)
                        ? (PumpJunkField != null && (bool)PumpJunkField.GetValue(pump) ? "Base Layer.junk_pump" : "Base Layer.chain_pump")
                        : null;
                if (stroke != null && animator != null && animator.isActiveAndEnabled) animator.Play(stroke, 0, 0f);
                if (sound != null && !sound.isPlaying) sound.Play();
            }
        }

        private void UnbindBoat()
        {
            _rowboat = _crewHelm = null;
            _reelSlots = new Slot[0];
            _crewPumps = new PumpController[0];
            _pumpShown = new bool[0];
            _pumpBeatAt = new float[0];
            _pumpAnimators = new Animator[0];
            _pumpSounds = new AudioSource[0];
            HostCrewSteers = false;
            s_hostPumps = 0;
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
                        reading.C = s_hostPumps;
                        return true;
                    }
                    reading.A = _ship.crewPiloting ? 1f : 0f;
                    reading.B = _ship.crewPiloting ? _ship.setCourseDeg : 0f;
                    reading.C = CrewPumps();
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
                    s_hostPumps = Mathf.Clamp(Mathf.RoundToInt(slot.C), 0, (1 << MaxPumpBits) - 1);
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
