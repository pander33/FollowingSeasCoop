using System;
using System.Collections.Generic;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// Builds the body other players see. The game has no player model (it is first person), so the
    /// avatar is one of the game's crew prefabs with everything that makes it a crew member taken
    /// off: only the transforms, the renderers and the animator stay. The game counts on a single
    /// <c>PlayerHandler</c> and a single object tagged <c>Player</c>; an avatar carries neither.
    /// </summary>
    internal static class AvatarFactory
    {
        public static GameObject Create(uint netId, string label, PlayerAppearance appearance, out Animator animator)
        {
            animator = null;
            GameObject avatar = null;
            try { avatar = FromCrewPrefab(appearance); }
            catch (Exception e) { Plugin.Log.LogWarning("[Avatar] Crew model unavailable, using a placeholder: " + e.Message); }
            if (avatar == null) avatar = Placeholder();

            avatar.name = "CoopAvatar " + netId + " (" + label + ")";
            UnityEngine.Object.DontDestroyOnLoad(avatar);
            animator = avatar.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            return avatar;
        }

        private static GameObject FromCrewPrefab(PlayerAppearance appearance)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null) return null;
            GameObject prefab = dp.InstantiateCrewByID(AppearanceCatalog.Id(appearance.Model));
            if (prefab == null) return null;

            // Instantiated under an inactive parent the copy never wakes: no Awake or Start of the
            // crew scripts runs before they are removed.
            var holder = new GameObject("CoopAvatarHolder");
            holder.SetActive(false);
            GameObject copy;
            try
            {
                copy = UnityEngine.Object.Instantiate(prefab, holder.transform);
                RemoveSecondBody(copy);
                try { AppearanceCatalog.Apply(copy, appearance); }
                catch (Exception e) { Plugin.Log.LogWarning("[Avatar] Parts keep the model's own look: " + e.Message); }
                // A component cannot be removed while another one requires it, so take several passes.
                for (int pass = 0; pass < 4; pass++)
                {
                    bool removed = false;
                    foreach (Component component in copy.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null || Keep(component)) continue;
                        UnityEngine.Object.DestroyImmediate(component);
                        removed = true;
                    }
                    if (!removed) break;
                }
                foreach (Transform t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = "Untagged";
                copy.transform.SetParent(null, false);
                copy.transform.localScale = prefab.transform.localScale;
                copy.SetActive(true);
            }
            finally { UnityEngine.Object.Destroy(holder); }
            return copy;
        }

        // A crew prefab holds two bodies. The one with the Animator walks on the static copy of the
        // deck; "crewClone" is a second, bone-for-bone copy that NewCrewController moves to the real
        // ship and poses from the first every frame. Without that script the clone is a frozen
        // duplicate, so it goes, together with the navigation target marker.
        private static void RemoveSecondBody(GameObject copy)
        {
            foreach (GameObject part in SecondBody(copy)) UnityEngine.Object.DestroyImmediate(part);
        }

        /// <summary>The second body and the target marker inside a crew model, or its prefab.</summary>
        internal static List<GameObject> SecondBody(GameObject root)
        {
            var parts = new List<GameObject>();
            NewCrewController crew = root.GetComponentInChildren<NewCrewController>(true);
            if (crew == null) return parts;
            foreach (string field in new[] { "crewClone", "targetClone" })
            {
                try
                {
                    var part = HarmonyLib.AccessTools.Field(typeof(NewCrewController), field)?.GetValue(crew) as GameObject;
                    if (part != null && part != root && part.transform.IsChildOf(root.transform)) parts.Add(part);
                }
                catch (Exception e) { Plugin.Log.LogWarning("[Avatar] Could not read '" + field + "': " + e.Message); }
            }
            return parts;
        }

        private static bool Keep(Component component)
        {
            return component is Transform || component is Renderer || component is MeshFilter ||
                   component is Animator || component is LODGroup;
        }

        private static GameObject Placeholder()
        {
            var root = new GameObject("CoopAvatarPlaceholder");
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            UnityEngine.Object.Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Unlit") ??
                            Shader.Find("Sprites/Default");
            if (shader != null) body.GetComponent<Renderer>().material = new Material(shader) { color = new Color(0.8f, 0.55f, 0.2f) };
            return root;
        }
    }
}
