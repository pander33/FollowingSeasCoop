using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// Hunting. Animals wander on each copy by themselves and are not shared while alive; what a
    /// player does to one is: shot, it lies where the hunter's copy had it; harvested, it is gone
    /// and its yield is a thing in <see cref="ItemRegistry"/>, named after the animal.
    ///
    /// An animal is named by its place in its scene, noted at its <c>Start</c>. What became of it
    /// is kept for as long as the world is loaded — also outside a session, so a guest that joins
    /// later is told — and is applied whenever the animal's island is loaded on a copy.
    /// </summary>
    internal sealed class HuntSync
    {
        private static readonly FieldInfo DeadField = AccessTools.Field(typeof(WanderingGameController), "dead");
        private static readonly FieldInfo AgentField = AccessTools.Field(typeof(WanderingGameController), "nma");
        private static readonly FieldInfo AnimatorField = AccessTools.Field(typeof(WanderingGameController), "animator");
        private static readonly FieldInfo BodyField = AccessTools.Field(typeof(WanderingGameController), "heiarchicalParent");
        private static readonly FieldInfo YieldField = AccessTools.Field(typeof(WanderingGameController), "provisionPrefab");

        private static readonly ConditionalWeakTable<WanderingGameController, string> Keys =
            new ConditionalWeakTable<WanderingGameController, string>();
        // The yield of a harvest, by the object the game made for it.
        private static readonly ConditionalWeakTable<ObjectInteractionHandler, string> Yields =
            new ConditionalWeakTable<ObjectInteractionHandler, string>();
        // What an animal of a kind yields, kept from the first animal seen: a copy that has none
        // of them loaded can still show the yield another player carries.
        private static readonly Dictionary<string, GameObject> YieldPrefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        // Written by the patches, which are static.
        private static WanderingGameController s_harvesting;
        private static bool s_applying;

        private readonly CoopNet _net;
        private readonly Dictionary<string, AnimalEntry> _state = new Dictionary<string, AnimalEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, WanderingGameController> _animals =
            new Dictionary<string, WanderingGameController>(StringComparer.Ordinal);

        public int Sent { get; private set; }
        public int Received { get; private set; }

        public HuntSync(CoopNet net) { _net = net; }

        private static bool FieldsFound => DeadField != null && AgentField != null && AnimatorField != null &&
                                           BodyField != null && YieldField != null;

        private static bool Ready => FieldsFound && PatchHealth.StateOf("Hunt") == PatchHealthState.Ok;

        /// <summary>The world is gone, and what was done to its animals with it.</summary>
        public void WorldUnloaded(Scene scene)
        {
            if (scene.buildIndex != GameAccess.WorldScene) return;
            _state.Clear(); _animals.Clear();
            Sent = Received = 0;
        }

        /// <summary>The key of the thing if it is the yield of a harvest, otherwise null.</summary>
        internal static string YieldKey(ObjectInteractionHandler item) =>
            Yields.TryGetValue(item, out string key) ? key : null;

        /// <summary>What a harvested animal leaves, by the prefab's name; null when no such animal was ever seen here.</summary>
        internal static GameObject YieldPrefab(string name) =>
            YieldPrefabs.TryGetValue(name ?? "", out GameObject prefab) && prefab != null ? prefab : null;

        private bool InSession => _net.IsHost || (_net.IsClient && CoopBehaviour.Instance != null && CoopBehaviour.Instance.Participating);

        // -----------------------------------------------------------------
        // What the local game did
        // -----------------------------------------------------------------

        private void Started(WanderingGameController animal)
        {
            string key = Keys.GetValue(animal, value => ItemCatalog.SceneKey(value.transform));
            _animals[key] = animal;
            if (YieldField.GetValue(animal) is GameObject prefab && prefab != null) YieldPrefabs[prefab.name] = prefab;
            // An island loaded again brings its animals back alive.
            if (_state.TryGetValue(key, out AnimalEntry entry)) Apply(animal, entry);
        }

        private void Killed(WanderingGameController animal)
        {
            if (!Keys.TryGetValue(animal, out string key)) return;
            if (_state.ContainsKey(key)) return;
            var entry = new AnimalEntry { Key = key, Status = AnimalStatus.Dead };
            Transform t = Mover(animal);
            CoordSpace.LocalToReal(t.position, out entry.X, out entry.Y, out entry.Z);
            Quaternion q = t.rotation;
            entry.Qx = q.x; entry.Qy = q.y; entry.Qz = q.z; entry.Qw = q.w;
            Local(entry);
        }

        private void Harvested(WanderingGameController animal)
        {
            if (!Keys.TryGetValue(animal, out string key)) return;
            _state.TryGetValue(key, out AnimalEntry entry);
            entry.Key = key; entry.Status = AnimalStatus.Harvested;
            Local(entry);
        }

        private void Local(AnimalEntry entry)
        {
            _state[entry.Key] = entry;
            if (!InSession) return;
            var msg = new AnimalStateMsg { Entries = new[] { entry } };
            if (_net.IsHost) _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
            else _net.SendToHost(msg, DeliveryMethod.ReliableOrdered);
            Sent++;
        }

        // -----------------------------------------------------------------
        // Wire
        // -----------------------------------------------------------------

        /// <summary>Host: a guest came into the world, where every animal is alive.</summary>
        public void SendAll(uint player)
        {
            if (!_net.IsHost || _state.Count == 0) return;
            AnimalEntry[] all = _state.Values.ToArray();
            for (int i = 0; i < all.Length; i += AnimalStateMsg.MaxEntries)
                _net.SendTo(player, new AnimalStateMsg
                {
                    Entries = all.Skip(i).Take(AnimalStateMsg.MaxEntries).ToArray(),
                }, DeliveryMethod.ReliableOrdered);
        }

        public void OnState(AnimalStateMsg msg, NetPeer from)
        {
            if (!GameAccess.InWorld) return;
            Received++;
            foreach (AnimalEntry entry in msg.Entries)
            {
                if (string.IsNullOrEmpty(entry.Key)) continue;
                AnimalEntry next = entry;
                if (_state.TryGetValue(entry.Key, out AnimalEntry had))
                {
                    if (had.Status == AnimalStatus.Harvested) continue;
                    // Shot twice, from two copies: it lies where the first one had it.
                    if (entry.Status == AnimalStatus.Dead) continue;
                }
                _state[next.Key] = next;
                if (_animals.TryGetValue(next.Key, out WanderingGameController animal) && animal != null)
                {
                    try { Apply(animal, next); }
                    catch (Exception e) { Plugin.Log.LogError("[Hunt] " + e); }
                }
            }
            if (_net.IsHost)
                _net.Broadcast(msg, DeliveryMethod.ReliableOrdered, readyOnly: true, exceptNetId: _net.PlayerNetIdForPeer(from));
        }

        // -----------------------------------------------------------------
        // Applying
        // -----------------------------------------------------------------

        // The object the game's own walking moves.
        private static Transform Mover(WanderingGameController animal)
        {
            var agent = AgentField.GetValue(animal) as NavMeshAgent;
            return agent != null ? agent.transform : animal.transform;
        }

        private static void Apply(WanderingGameController animal, AnimalEntry entry)
        {
            if (!Ready) return;
            var body = BodyField.GetValue(animal) as Transform;
            if (entry.Status == AnimalStatus.Harvested)
            {
                // The yield is a thing of the registry; here only the carcass goes.
                if (body != null) UnityEngine.Object.Destroy(body.gameObject);
                else UnityEngine.Object.Destroy(animal.gameObject);
                return;
            }
            if ((bool)DeadField.GetValue(animal)) return;

            var agent = AgentField.GetValue(animal) as NavMeshAgent;
            Vector3 at = CoordSpace.RealToLocal(entry.X, entry.Y, entry.Z);
            var rot = new Quaternion(entry.Qx, entry.Qy, entry.Qz, entry.Qw);
            float norm = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
            Transform t = Mover(animal);
            bool walking = agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh;
            if (!walking || !agent.Warp(at)) t.position = at;
            if (norm > 0.5f && norm < 1.5f) t.rotation = rot.normalized;

            // What the game's own Kill does, without its call to an agent that may not be on the
            // ground yet on this copy: the body goes limp, the walking stops.
            s_applying = true;
            try
            {
                if (walking) animal.Kill();
                else
                {
                    foreach (Rigidbody limb in (body != null ? body : animal.transform).GetComponentsInChildren<Rigidbody>())
                        limb.isKinematic = false;
                    DeadField.SetValue(animal, true);
                    if (AnimatorField.GetValue(animal) is Animator animator && animator != null) animator.enabled = false;
                    if (agent != null) agent.enabled = false;
                }
            }
            finally { s_applying = false; }
        }

        public string Describe()
        {
            if (PatchHealth.StateOf("Hunt") != PatchHealthState.Ok) return "off: the Hunt patches failed";
            if (!FieldsFound) return "off: the game's animal fields were not found";
            int dead = 0, harvested = 0;
            foreach (AnimalEntry entry in _state.Values)
                if (entry.Status == AnimalStatus.Harvested) harvested++; else dead++;
            return "animals seen " + _animals.Count(pair => pair.Value != null) + "   shot " + dead + "   harvested " + harvested +
                   "   sent " + Sent + "   received " + Received;
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            harmony.Patch(Method("Start"), postfix: new HarmonyMethod(typeof(HuntSync), nameof(StartPostfix)));
            harmony.Patch(Method("Kill"), postfix: new HarmonyMethod(typeof(HuntSync), nameof(KillPostfix)));
            harmony.Patch(Method("Harvest"),
                          prefix: new HarmonyMethod(typeof(HuntSync), nameof(HarvestPrefix)),
                          finalizer: new HarmonyMethod(typeof(HuntSync), nameof(HarvestEnd)));
            harmony.Patch(AccessTools.Method(typeof(ObjectInteractionHandler), "SetType")
                              ?? throw new MissingMethodException(nameof(ObjectInteractionHandler), "SetType"),
                          postfix: new HarmonyMethod(typeof(HuntSync), nameof(SetTypePostfix)));
        }

        private static MethodInfo Method(string name) =>
            AccessTools.Method(typeof(WanderingGameController), name) ??
            throw new MissingMethodException(nameof(WanderingGameController), name);

        private static void PatchError(Exception error) => Plugin.Log.LogError("[Hunt] " + error);

        private static HuntSync Instance => CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Hunt : null;

        private static void StartPostfix(WanderingGameController __instance)
        {
            PatchGuard.Run(() => { if (FieldsFound) Instance?.Started(__instance); }, PatchError);
        }

        private static void KillPostfix(WanderingGameController __instance)
        {
            PatchGuard.Run(() => { if (!s_applying && FieldsFound) Instance?.Killed(__instance); }, PatchError);
        }

        // The game harvests only a dead animal: it makes the yield, names it and removes the carcass.
        private static void HarvestPrefix(WanderingGameController __instance)
        {
            PatchGuard.Run(() =>
            {
                s_harvesting = FieldsFound && (bool)DeadField.GetValue(__instance) ? __instance : null;
            }, PatchError);
        }

        // The one call the game makes on the yield while it is made, and makes nowhere else.
        private static void SetTypePostfix(ObjectInteractionHandler __instance)
        {
            if (s_harvesting == null) return;
            PatchGuard.Run(() =>
            {
                if (!Keys.TryGetValue(s_harvesting, out string key)) return;
                Yields.Remove(__instance);
                Yields.Add(__instance, "hunt:" + key);
            }, PatchError);
        }

        private static void HarvestEnd()
        {
            WanderingGameController animal = s_harvesting;
            s_harvesting = null;
            if (animal == null) return;
            PatchGuard.Run(() => Instance?.Harvested(animal), PatchError);
        }
    }
}
