using System;
using System.Collections.Generic;
using System.Reflection;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The kinds of <see cref="ControlsSync"/> that are not worked by a hand on them: the leaks of
    /// the hull, the weights of the trolling lines and the fish on their hooks.
    ///
    /// The game opens a leak, lets it grow and puts a fish on a hook by chance, on each copy by
    /// itself. A guest's copy does none of that (the <c>Hull</c> patches): its leaks and hooks are
    /// what the host says. What a guest's own player does — a blow of the caulking hammer, a fish
    /// taken off the hook, a weight thrown out — changes the guest's copy, and the guest reports it
    /// like any other control.
    /// </summary>
    internal sealed partial class ControlsSync
    {
        private static readonly FieldInfo LeakIntensityField = AccessTools.Field(typeof(LeakController), "intensity");
        private static readonly FieldInfo LeakRateField = AccessTools.Field(typeof(LeakController), "leakRate");
        private static readonly FieldInfo LeakFlowField = AccessTools.Field(typeof(LeakController), "currentParticleFlowRate");
        private static readonly FieldInfo LeakMaxFlowField = AccessTools.Field(typeof(LeakController), "maxParticleFlowRate");
        private static readonly FieldInfo LeakStartIntensityField = AccessTools.Field(typeof(LeakController), "startIntensity");
        private static readonly FieldInfo LeakStartRateField = AccessTools.Field(typeof(LeakController), "startLeakRate");
        private static readonly FieldInfo LeakStartFlowField = AccessTools.Field(typeof(LeakController), "startParticleFlowRate");
        private static readonly FieldInfo LeakVolumeStepField = AccessTools.Field(typeof(LeakController), "volumeStep");
        private static readonly FieldInfo LeakEffectField = AccessTools.Field(typeof(LeakController), "leakEffect");
        private static readonly FieldInfo LeakAudioField = AccessTools.Field(typeof(LeakController), "leakAudio");
        private static readonly FieldInfo TrollOuterField = AccessTools.Field(typeof(TrollController), "fishPointPool");
        private static readonly FieldInfo TrollInnerField = AccessTools.Field(typeof(TrollController), "fishPointPoolInner");
        private static readonly FieldInfo HelperWeightsField = AccessTools.Field(typeof(TrollWeightHelper), "weights");

        /// <summary>The hooks are made by the lines' own Start; they are waited for this long.</summary>
        private const float HooksWait = 20f;

        private readonly List<LeakController> _leaks = new List<LeakController>();
        private readonly List<TrollWeight> _weights = new List<TrollWeight>();
        private readonly List<FishPoint> _hooks = new List<FishPoint>();
        private TrollWeightHelper[] _weightHelpers = new TrollWeightHelper[0];
        private TrollController[] _trolls = new TrollController[0];
        private Slot[] _leakSlots = new Slot[0];
        private Slot[] _weightSlots = new Slot[0];
        private Slot[] _hookSlots = new Slot[0];
        private bool _hooksBound;
        private float _hooksUntil;
        private static bool s_warnedHull;

        /// <summary>Diagnostics: the hooks of the ship's trolling lines, in the order they are shared by.</summary>
        internal IReadOnlyList<FishPoint> Hooks => _hooks;

        private static bool HullReady =>
            LeakIntensityField != null && LeakRateField != null && LeakFlowField != null && LeakMaxFlowField != null &&
            LeakStartIntensityField != null && LeakStartRateField != null && LeakStartFlowField != null &&
            LeakVolumeStepField != null && LeakEffectField != null && LeakAudioField != null &&
            TrollOuterField != null && TrollInnerField != null && HelperWeightsField != null &&
            PatchHealth.StateOf("Hull") == PatchHealthState.Ok;

        // -----------------------------------------------------------------
        // Binding
        // -----------------------------------------------------------------

        private void BindHull()
        {
            if (!HullReady)
            {
                // Without the patches a guest's copy would open and widen leaks of its own.
                if (!s_warnedHull)
                {
                    s_warnedHull = true;
                    Plugin.Log.LogWarning("[Controls] Leaks and trolling lines are not shared: the Hull patches or the game's fields are missing");
                }
                return;
            }
            ShipHullMover ship = _ship;
            _leaks.AddRange(ship.GetComponentsInChildren<LeakController>(true));
            _leakSlots = MakeSlots(ControlKind.Leak, _leaks.Count);
            _weights.AddRange(ship.GetComponentsInChildren<TrollWeight>(true));
            _weightSlots = MakeSlots(ControlKind.TrollWeight, _weights.Count);
            _weightHelpers = ship.GetComponentsInChildren<TrollWeightHelper>(true);
            _trolls = ship.GetComponentsInChildren<TrollController>(true);
            _hooksBound = _trolls.Length == 0;
            _hooksUntil = Time.unscaledTime + HooksWait;
        }

        private void UnbindHull()
        {
            _leaks.Clear();
            _weights.Clear();
            _hooks.Clear();
            _weightHelpers = new TrollWeightHelper[0];
            _trolls = new TrollController[0];
            _leakSlots = new Slot[0];
            _weightSlots = new Slot[0];
            _hookSlots = new Slot[0];
            _hooksBound = true;
        }

        // A line makes its hooks in its Start, outer and inner in turn, so their order is the same
        // on every copy — but they are not there yet when the ship is first seen.
        private void BindHooks()
        {
            if (_hooksBound) return;
            bool late = Time.unscaledTime > _hooksUntil;
            var hooks = new List<FishPoint>();
            foreach (TrollController troll in _trolls)
            {
                if (troll == null) continue;
                var outer = TrollOuterField.GetValue(troll) as List<GameObject>;
                var inner = TrollInnerField.GetValue(troll) as List<GameObject>;
                if (outer == null || inner == null || outer.Count == 0 || inner.Count == 0)
                {
                    if (!late) return;
                    continue;
                }
                foreach (List<GameObject> pool in new[] { outer, inner })
                    foreach (GameObject point in pool)
                        hooks.Add(point != null ? point.GetComponent<FishPoint>() : null);
            }
            _hooksBound = true;
            _hooks.AddRange(hooks);
            _hookSlots = MakeSlots(ControlKind.FishPoint, _hooks.Count);
            if (_hooks.Count > 0) Plugin.Log.LogInfo("[Controls] Trolling lines bound: hooks " + _hookSlots.Length);
        }

        private Slot FindHull(ControlKind kind, int index)
        {
            switch (kind)
            {
                case ControlKind.Leak: return At(_leakSlots, index);
                case ControlKind.TrollWeight: return At(_weightSlots, index);
                case ControlKind.FishPoint: 
                    // A state may come before this copy has looked at its lines.
                    if (!_hooksBound) Guard(BindHooks);
                    return At(_hookSlots, index);
                default: return null;
            }
        }

        private string CountsHull()
        {
            return ", leaks " + _leakSlots.Length + ", troll weights " + _weightSlots.Length + ", hooks " + _hookSlots.Length;
        }

        // -----------------------------------------------------------------
        // Kinds
        // -----------------------------------------------------------------

        private bool ReadHull(Slot slot, ref Reading reading)
        {
            switch (slot.Kind)
            {
                case ControlKind.Leak:
                {
                    LeakController leak = _leaks[slot.Index];
                    if (leak == null) return false;
                    bool open = leak.gameObject.activeSelf;
                    reading.A = open ? 1f : 0f;
                    reading.B = open ? (float)LeakIntensityField.GetValue(leak) : 0f;
                    return true;
                }
                case ControlKind.TrollWeight:
                {
                    TrollWeight weight = _weights[slot.Index];
                    if (weight == null) return false;
                    reading.A = weight.deployed ? 1f : 0f;
                    return true;
                }
                case ControlKind.FishPoint:
                {
                    FishPoint hook = _hooks[slot.Index];
                    if (hook == null) return false;
                    reading.A = hook.fishOn ? 1f : 0f;
                    reading.B = hook.fishOn && hook.fish != null ? hook.fish.transform.localScale.x : 0f;
                    return true;
                }
                default: return false;
            }
        }

        private static bool DiffersHull(Slot slot, Reading reading)
        {
            if ((reading.A > 0.5f) != (slot.A > 0.5f)) return true;
            switch (slot.Kind)
            {
                case ControlKind.Leak:
                case ControlKind.FishPoint:
                    return reading.A > 0.5f && Mathf.Abs(reading.B - slot.B) > 0.01f;
                default:
                    return false;
            }
        }

        private bool ApplyHull(Slot slot)
        {
            switch (slot.Kind)
            {
                case ControlKind.Leak: ApplyLeak(slot); return true;
                case ControlKind.TrollWeight: ApplyWeight(slot); return true;
                case ControlKind.FishPoint:
                {
                    FishPoint hook = _hooks[slot.Index];
                    if (hook == null) return true;
                    bool on = slot.A > 0.5f;
                    if (on && slot.B > 0f) hook.SetScale(slot.B);
                    hook.fishOn = on;
                    return true;
                }
                default: return true;
            }
        }

        // A leak is an object that is switched on, with a strength that the game steps up by one
        // every so often and a blow of the hammer steps down by one; the spray and the sound follow
        // the strength by the same steps.
        private void ApplyLeak(Slot slot)
        {
            LeakController leak = _leaks[slot.Index];
            if (leak == null) return;
            GameObject obj = leak.gameObject;
            if (slot.A < 0.5f)
            {
                if (obj.activeSelf) obj.SetActive(false);
                return;
            }
            // Switching it on makes the game start it from its first strength.
            if (!obj.activeSelf) obj.SetActive(true);
            float steps = slot.B - (float)LeakStartIntensityField.GetValue(leak);
            LeakIntensityField.SetValue(leak, slot.B);
            LeakRateField.SetValue(leak, (float)LeakStartRateField.GetValue(leak) + steps);
            float most = (float)LeakMaxFlowField.GetValue(leak);
            float flow = Mathf.Clamp((float)LeakStartFlowField.GetValue(leak) + steps * most / 10f, 0f, most);
            LeakFlowField.SetValue(leak, flow);
            if (LeakEffectField.GetValue(leak) is ParticleSystem spray && spray != null)
            {
                ParticleSystem.EmissionModule emission = spray.emission;
                emission.rateOverTime = flow;
            }
            if (LeakAudioField.GetValue(leak) is AudioSource sound && sound != null)
                sound.volume = Mathf.Clamp01((float)LeakVolumeStepField.GetValue(leak) * (1f + steps));
        }

        private void ApplyWeight(Slot slot)
        {
            TrollWeight weight = _weights[slot.Index];
            if (weight == null) return;
            bool want = slot.A > 0.5f;
            if (weight.deployed == want) return;
            // The game's own throw: the weight is let loose and pushed overboard.
            if (want) { weight.PerformAction(); return; }
            // Weights are hauled back all at once by the game's own routine, which also moves them
            // to their places; without it the weight would freeze where it hangs.
            foreach (TrollWeightHelper helper in _weightHelpers)
                if (helper != null && HelperWeightsField.GetValue(helper) is List<TrollWeight> hauled && hauled.Contains(weight))
                {
                    helper.PerformAction();
                    return;
                }
            weight.deployed = false;
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void InstallHull(Harmony harmony)
        {
            harmony.Patch(Method(typeof(ConditionEventManager), "RollForEffect"),
                          prefix: new HarmonyMethod(typeof(ControlsSync), nameof(ShipEventPrefix)));
            harmony.Patch(Method(typeof(LeakController), "Degrade"),
                          prefix: new HarmonyMethod(typeof(ControlsSync), nameof(HostOnlyPrefix)));
            harmony.Patch(AccessTools.Method(typeof(TrollController), "RollForFish", new[] { typeof(List<GameObject>) })
                              ?? throw new MissingMethodException(nameof(TrollController), "RollForFish"),
                          prefix: new HarmonyMethod(typeof(ControlsSync), nameof(HostOnlyPrefix)));
        }

        // A leak grows and a fish bites by the host's dice only.
        private static bool HostOnlyPrefix()
        {
            return PatchGuard.Prefix(() => !s_guest, PatchError);
        }

        // The game opens leaks through this; only a ship's are the host's to open.
        private static bool ShipEventPrefix(ConditionEventManager __instance)
        {
            return PatchGuard.Prefix(() => !s_guest || __instance.GetComponentInParent<ShipHullMover>() == null, PatchError);
        }
    }
}
