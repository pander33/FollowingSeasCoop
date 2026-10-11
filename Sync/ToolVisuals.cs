using System;
using System.Collections.Generic;
using System.Reflection;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The tool another player has out: quadrant, spyglass, chronometer, lantern, compass, caulking
    /// hammer, log, gun. Which one is the index of the active button in the game's
    /// <c>InventoryManager.buttons</c>, plus one; the list is the same on every copy.
    ///
    /// What the others see is a copy of this copy's own tool of that slot with everything but its
    /// meshes taken off: no scripts (they read input, move the camera, mend leaks, change the sky),
    /// no cameras, sounds, bodies or colliders, and none of the first-person arms. The game builds
    /// a tool in the space of its owner's camera, so the copy is put at the other player's eye,
    /// turned the way that player looks — where its owner sees it.
    /// </summary>
    internal sealed class ToolVisuals
    {
        private const float SwingSeconds = 0.3f;
        private const float SwingDegrees = 40f;
        /// <summary>
        /// A tool sits a hand's breadth from its owner's eye — inside the face of a body seen from
        /// outside. The copy is held this much further along the look.
        /// </summary>
        private const float FaceClearance = 0.2f;

        private static readonly FieldInfo ButtonsField = AccessTools.Field(typeof(InventoryManager), "buttons");
        private static readonly FieldInfo ItemField = AccessTools.Field(typeof(InventoryButton), "item");
        private static readonly FieldInfo HammerReadyField = AccessTools.Field(typeof(CaulkHammerController), "animComplete");

        // The local eye above the feet, as last seen standing and crouching. Every player has the
        // same body, so this copy's own numbers place the other players' tools.
        private static float s_eyeStanding = 1.6f, s_eyeCrouching = 1f;
        private static bool s_crouchMeasured;

        private static InventoryManager s_manager;
        private static float s_managerSoughtAt = -10f;
        private static byte s_useCount;

        // -----------------------------------------------------------------
        // The local player's tool
        // -----------------------------------------------------------------

        private static List<InventoryButton> Buttons()
        {
            if (ButtonsField == null || ItemField == null) return null;
            if (s_manager == null && Time.unscaledTime - s_managerSoughtAt > 1f)
            {
                s_managerSoughtAt = Time.unscaledTime;
                foreach (InventoryManager manager in UnityEngine.Object.FindObjectsByType<InventoryManager>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (!manager.disabledInScene && manager.gameObject.scene.buildIndex == GameAccess.WorldScene)
                    {
                        s_manager = manager;
                        break;
                    }
            }
            return s_manager != null ? ButtonsField.GetValue(s_manager) as List<InventoryButton> : null;
        }

        /// <summary>The tool the local player has out: its slot plus one, 0 for none.</summary>
        public static byte LocalKind()
        {
            List<InventoryButton> buttons = Buttons();
            if (buttons == null) return 0;
            for (int i = 0; i < buttons.Count && i < 254; i++)
                if (buttons[i] != null && buttons[i].isActive) return (byte)(i + 1);
            return 0;
        }

        /// <summary>Grows by one with every blow of the local caulking hammer.</summary>
        public static byte LocalUseCount => s_useCount;

        /// <summary>Called with every local pose: remembers how high the eye is above the feet.</summary>
        public static void Measure(PlayerHandler player, Vector3 feet, Vector3 up)
        {
            GameObject camera = null;
            try { camera = player.GetCameraObject(); } catch { }
            if (camera == null) return;
            float height = Vector3.Dot(camera.transform.position - feet, up);
            if (height < 0.2f || height > 3f) return;
            if (player.state == PlayerHandler.MovementState.crouching) { s_eyeCrouching = height; s_crouchMeasured = true; }
            else if (player.state != PlayerHandler.MovementState.swimming)
            {
                s_eyeStanding = height;
                if (!s_crouchMeasured) s_eyeCrouching = height * 0.6f;
            }
        }

        // -----------------------------------------------------------------
        // Another player's tool
        // -----------------------------------------------------------------

        private GameObject _tool;
        private byte _kind, _use;
        private bool _hasUse;
        private float _swingAt = -10f, _retryAt;
        private byte _warnedKind;

        /// <summary>Every frame after the avatar is placed.</summary>
        public void Apply(GameObject avatar, float pitch, bool crouching, byte kind, byte use)
        {
            float now = Time.unscaledTime;
            if (kind != _kind || (kind != 0 && _tool == null && now >= _retryAt))
            {
                Drop();
                _kind = kind;
                if (kind != 0)
                {
                    try { _tool = Build(kind, avatar); }
                    catch (Exception e)
                    {
                        // Tried again every two seconds: said once per tool.
                        if (_warnedKind != kind) Plugin.Log.LogWarning("[Tools] No copy of the tool in slot " + (kind - 1) + ": " + e.Message);
                        _warnedKind = kind;
                    }
                    // This copy's own tool of that slot may not exist yet (the gun is made anew each time).
                    if (_tool == null) _retryAt = now + 2f;
                }
            }
            if (_tool == null) return;

            if (_hasUse && use != _use) _swingAt = now;
            _use = use; _hasUse = true;

            Transform body = avatar.transform;
            Quaternion look = body.rotation * Quaternion.Euler(pitch, 0f, 0f);
            float swing = now - _swingAt;
            if (swing < SwingSeconds) look *= Quaternion.Euler(SwingDegrees * Mathf.Sin(Mathf.PI * swing / SwingSeconds), 0f, 0f);
            Vector3 eye = body.position + body.up * (crouching ? s_eyeCrouching : s_eyeStanding);
            _tool.transform.SetPositionAndRotation(eye + look * Vector3.forward * FaceClearance, look);
        }

        public void Drop()
        {
            if (_tool != null) UnityEngine.Object.Destroy(_tool);
            _tool = null; _kind = 0; _hasUse = false; _swingAt = -10f;
        }

        private static GameObject Build(byte kind, GameObject avatar)
        {
            List<InventoryButton> buttons = Buttons();
            if (buttons == null || kind > buttons.Count || buttons[kind - 1] == null) return null;
            var item = ItemField.GetValue(buttons[kind - 1]) as GameObject;
            if (item == null) return null;

            Renderer skin = avatar.GetComponentInChildren<Renderer>(true);
            int layer = skin != null ? skin.gameObject.layer : 0;
            // The game draws a tool with its own overlay camera on this layer; a mesh that is on
            // another one is not part of the tool's body (the spyglass's picture of the far view).
            int toolLayer = LayerMask.NameToLayer("UI_Physical");

            // Under an inactive parent the copy never wakes: no Awake or OnEnable of the tool's
            // scripts runs before they are removed.
            var holder = new GameObject("CoopToolHolder");
            holder.SetActive(false);
            GameObject copy;
            try
            {
                copy = UnityEngine.Object.Instantiate(item, holder.transform);
                foreach (MeshRenderer renderer in copy.GetComponentsInChildren<MeshRenderer>(true))
                {
                    int own = renderer.gameObject.layer;
                    if (own != 0 && own != toolLayer) UnityEngine.Object.DestroyImmediate(renderer);
                }
                // A component cannot be removed while another one requires it (Unity logs an error
                // and leaves it). The first pass takes what requires — scripts, joints, filters —
                // and leaves what is required; the next ones take the rest.
                for (int pass = 0; pass < 5; pass++)
                {
                    bool removed = false;
                    foreach (Component component in copy.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null || Keep(component) || (pass == 0 && Required(component))) continue;
                        // Goes with its particle system.
                        if (component.GetType().Name == "ParticleSystemRenderer" && component.GetComponent("ParticleSystem") != null) continue;
                        UnityEngine.Object.DestroyImmediate(component);
                        removed = true;
                    }
                    if (!removed && pass > 0) break;
                }
                foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.tag = "Untagged";
                    t.gameObject.layer = layer;
                }
                copy.name = "CoopTool " + item.name;
                copy.transform.SetParent(null, false);
                // The size it has in front of its owner's eye, whatever the avatar's own scale is.
                copy.transform.localScale = item.transform.lossyScale;
                copy.transform.SetParent(avatar.transform, true);
                copy.SetActive(true);
            }
            finally { UnityEngine.Object.Destroy(holder); }
            return copy;
        }

        // Named, not typed: the project does not reference the engine's UI, audio and particle modules.
        private static readonly HashSet<string> RequiredNames = new HashSet<string>
        {
            "Canvas", "CanvasRenderer", "AudioSource", "ParticleSystem", "ParticleSystemRenderer",
        };

        private static bool Required(Component component)
        {
            return component is Rigidbody || component is Collider || component is Camera || component is Light ||
                   component is Animator || RequiredNames.Contains(component.GetType().Name);
        }

        // Skinned meshes are the first-person arms; an avatar has its own.
        private static bool Keep(Component component)
        {
            return component is Transform || component is MeshRenderer || component is MeshFilter || component is LODGroup;
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(CaulkHammerController), "Hammer")
                              ?? throw new MissingMethodException(nameof(CaulkHammerController), "Hammer"),
                          prefix: new HarmonyMethod(typeof(ToolVisuals), nameof(HammerPrefix)));
        }

        // The game ignores a blow while the last one is still playing; only a blow that lands counts.
        private static void HammerPrefix(CaulkHammerController __instance)
        {
            PatchGuard.Run(() =>
            {
                if (HammerReadyField == null || (bool)HammerReadyField.GetValue(__instance)) s_useCount++;
            }, error => Plugin.Log.LogError("[Tools] " + error));
        }
    }
}
