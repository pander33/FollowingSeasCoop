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
        private static readonly FieldInfo FishBodyField = AccessTools.Field(typeof(ObjectInteractionHandler), "interactionRB");
        private static readonly FieldInfo FishColliderField = AccessTools.Field(typeof(ObjectInteractionHandler), "collider");
        private static readonly FieldInfo FishParentField = AccessTools.Field(typeof(ObjectInteractionHandler), "parent");
        private static readonly FieldInfo FishNullTimeField = AccessTools.Field(typeof(ObjectInteractionHandler), "nullTime");

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
        // Guest: whether each hook's fish is in the local hand, and since when.
        private bool[] _fishHeld = new bool[0];
        private float[] _fishHeldAt = new float[0];
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
            _fishHeld = new bool[0];
            _fishHeldAt = new float[0];
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
            _fishHeld = new bool[_hooks.Count];
            _fishHeldAt = new float[_hooks.Count];
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
                    // The number is the host's, as is the bite: a guest's own reading would argue with it.
                    reading.Text = _net.IsClient ? slot.Text
                        : hook.fishOn && hook.fish != null ? ModChannel.CatchUnitsText(hook.fish) : "";
                    if (_net.IsClient)
                    {
                        // The game also takes a bite off a hook whose fish is already in a hand; only
                        // a fish picked up just now is a take the host must decide on.
                        bool held = Fish(hook)?.isHeld == true;
                        if (held && !_fishHeld[slot.Index]) _fishHeldAt[slot.Index] = Time.unscaledTime;
                        _fishHeld[slot.Index] = held;
                        reading.C = held && Time.unscaledTime - _fishHeldAt[slot.Index] < 0.5f ? 1f : 0f;
                    }
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
                    return reading.A > 0.5f && (Mathf.Abs(reading.B - slot.B) > 0.01f || (reading.Text ?? "") != slot.Text);
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
                    if (on && hook.fish != null) ModChannel.GiveCatchUnits(hook.fish, slot.Text);
                    // As the game's bite does: a fish still on its way back from the hold is put on
                    // the hook, or its reset would take the new bite off. One in the hand stays there.
                    if (on && hook.fish != null && hook.fish.transform.parent != hook.transform && Fish(hook)?.isHeld != true)
                        hook.fish.transform.parent = hook.transform;
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

        // Host: a guest's blows of the hammer are a step from the strength its copy knew, so blows of
        // two players at once both count and a growth meanwhile is not undone.
        private void StepLeak(Slot slot, ControlMsg msg)
        {
            var here = default(Reading);
            if (msg.A < 0.5f || !ReadHull(slot, ref here)) return;
            if (here.A < 0.5f) msg.A = msg.B = 0f;
            else msg.B = Mathf.Max(0f, here.B + msg.B - msg.C);
            msg.C = 0f;
        }

        private static ObjectInteractionHandler Fish(FishPoint hook) =>
            hook.fish != null ? hook.fish.GetComponent<ObjectInteractionHandler>() : null;

        // Host: each copy lets its own player take the fish off a hook; the first take the host
        // hears of while the fish is still on here gets it, a later one is told who has it (C = 1
        // marks that answer). A hook already empty here with no hand on its fish: nothing to do.
        private bool FishTaken(Slot slot, ControlMsg msg, uint sender)
        {
            if (msg.A > 0.5f) return false;
            FishPoint hook = _hooks[slot.Index];
            if (hook == null) return false;
            bool hand = msg.C > 0.5f;
            msg.C = 0f;
            if (hook.fishOn)
            {
                if (hand) slot.Holder = sender;
                return false;
            }
            if (!hand) return true;
            uint holder = Fish(hook)?.isHeld == true ? _net.MyNetId : slot.Holder != 0 ? slot.Holder : _net.MyNetId;
            Refused++;
            CoopTrace.Note("Controls", "refused", Name(slot), "fish of " + sender + ", taken by " + holder);
            var state = new ControlStateMsg();
            Fill(state, slot, holder);
            state.A = state.B = 0f;
            state.C = 1f;
            _net.SendTo(sender, state, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // Guest: the host refused this player's take of this hook's fish. The fish it took meanwhile
        // goes back to its hook the way the game resets it, or it would reach the hold twice.
        private void LoseFish(Slot slot, uint holder)
        {
            FishPoint hook = _hooks[slot.Index];
            ObjectInteractionHandler fish = hook != null ? Fish(hook) : null;
            if (fish == null || !fish.isHeld) return;
            fish.Hold(false);
            if (fish.uiPrompt != null && fish.uiPrompt.activeSelf) fish.uiPrompt.SetActive(false);
            InteractionController controller = InteractionController.interactionInstance;
            if (controller != null) controller.ExternalStopInteractingAndUnlock();
            if (FishColliderField?.GetValue(fish) is Collider collider) collider.isTrigger = true;
            if (FishBodyField?.GetValue(fish) is Rigidbody body)
            {
                body.mass = 0f;
                body.useGravity = false;
                body.isKinematic = true;
            }
            fish.transform.parent = hook.transform;
            FishParentField?.SetValue(fish, hook.transform);
            FishNullTimeField?.SetValue(fish, 0f);
            fish.transform.localPosition = Vector3.zero;
            hook.fishOn = false;
            CoopTrace.Note("Controls", "fish lost", Name(slot), "taken by " + holder);
            _notice = _net.GetPlayerName(holder) + " took this fish first";
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
