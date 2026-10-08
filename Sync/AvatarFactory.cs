using System;
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
        private static readonly string[] CrewIds =
        {
            "crew_male_01", "crew_female_01", "crew_male_02", "crew_female_02", "crew_male_03", "crew_female_03",
        };

        public static GameObject Create(uint netId, string label, out Animator animator)
        {
            animator = null;
            GameObject avatar = null;
            try { avatar = FromCrewPrefab(netId); }
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

        private static GameObject FromCrewPrefab(uint netId)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null) return null;
            GameObject prefab = dp.InstantiateCrewByID(CrewIds[netId % (uint)CrewIds.Length]);
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
            NewCrewController crew = copy.GetComponentInChildren<NewCrewController>(true);
            if (crew == null) return;
            foreach (string field in new[] { "crewClone", "targetClone" })
            {
                try
                {
                    var part = HarmonyLib.AccessTools.Field(typeof(NewCrewController), field)?.GetValue(crew) as GameObject;
                    if (part != null && part != copy && part.transform.IsChildOf(copy.transform))
                        UnityEngine.Object.DestroyImmediate(part);
                }
                catch (Exception e) { Plugin.Log.LogWarning("[Avatar] Could not remove '" + field + "': " + e.Message); }
            }
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
