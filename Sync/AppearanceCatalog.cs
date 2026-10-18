using System.Collections.Generic;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The looks a player can choose: the game's six crew models, and for the models that share
    /// their meshes with others, the materials of a part taken from another model of the family.
    /// Nothing is made or tinted: a part is given materials the game already puts on the same mesh.
    /// </summary>
    internal static class AppearanceCatalog
    {
        internal const byte DefaultModel = 0;
        /// <summary>No look chosen: each copy gives the player a model by their NetId, so a crew differs.</summary>
        internal const byte Unchosen = 255;

        // The order is the wire's model number: only append.
        internal static readonly string[] Ids =
        {
            "crew_male_01", "crew_female_01", "crew_male_02", "crew_female_02", "crew_male_03", "crew_female_03",
        };
        private static readonly string[] Names = { "Male 1", "Female 1", "Male 2", "Female 2", "Male 3", "Female 3" };

        // Models whose bodies are the same meshes on the same bones (runtime audit 2026-10-18);
        // female 2 is a rig of its own.
        private static readonly byte[][] Families = { new byte[] { 0, 2, 4 }, new byte[] { 1, 5 }, new byte[] { 3 } };

        // The renderers whose materials differ inside a family, by object name, with what the
        // player reads. The order is the wire's part number: only append.
        private static readonly string[,] MaleParts =
        {
            { "Body:001.001", "Body" }, { "Braslet.001", "Bracelet" }, { "Clothe__top.001", "Top" },
            { "figurehead", "Figurehead" }, { "jewelry", "Jewelry 1" }, { "jewelry.001", "Jewelry 2" },
            { "jewelry.002", "Jewelry 3" }, { "lrg_Armor", "Armour" }, { "Shoue", "Shoes" },
            { "Sleve.001", "Sleeve 1" }, { "Sleve.002", "Sleeve 2" }, { "Sleve.003", "Sleeve 3" },
        };
        private static readonly string[,] FemaleParts =
        {
            { "Body:001.002", "Body" }, { "epitaph.001", "Epitaph" }, { "epitaph_pants", "Trousers" },
            { "epitaph_wrapping", "Wrapping" }, { "mask_clean.001", "Mask" },
        };

        private static readonly HashSet<string> Warned = new HashSet<string>();

        internal static int ModelCount => Ids.Length;
        internal static byte Valid(byte model) => model < Ids.Length ? model : DefaultModel;
        internal static string Id(byte model) => model == Unchosen ? "unchosen" : Ids[Valid(model)];
        internal static string Name(byte model) => model == Unchosen ? "Automatic" : Names[Valid(model)];

        internal static int IndexOf(string id) => System.Array.IndexOf(Ids, id);

        /// <summary>The look a player's body is built with on this copy.</summary>
        internal static PlayerAppearance ForPlayer(PlayerAppearance appearance, uint netId)
        {
            appearance = Normalize(appearance);
            return appearance.Model == Unchosen ? new PlayerAppearance { Model = (byte)(netId % (uint)Ids.Length) } : appearance;
        }

        private static string[,] Parts(byte model)
        {
            byte[] family = Family(model);
            return family == Families[0] ? MaleParts : family == Families[1] ? FemaleParts : null;
        }

        private static byte[] Family(byte model)
        {
            foreach (byte[] family in Families)
                if (System.Array.IndexOf(family, model) >= 0) return family;
            return Families[0];
        }

        internal static int PartCount(byte model) => model == Unchosen ? 0 : Parts(Valid(model))?.GetLength(0) ?? 0;
        internal static string PartLabel(byte model, int part) => Parts(Valid(model))[part, 1];
        internal static string PartObject(byte model, int part) => Parts(Valid(model))[part, 0];

        /// <summary>How many looks a part has: its own model's, then each other model of the family.</summary>
        internal static int ChoiceCount(byte model) => PartCount(model) == 0 ? 1 : Family(Valid(model)).Length;

        /// <summary>The model a part's choice takes its materials from; 0 is the model's own.</summary>
        internal static byte Donor(byte model, byte choice)
        {
            model = Valid(model);
            if (choice == 0 || choice >= ChoiceCount(model)) return model;
            int seen = 0;
            foreach (byte other in Family(model))
                if (other != model && ++seen == choice) return other;
            return model;
        }

        internal static byte ChoiceOf(byte model, byte donor)
        {
            for (byte choice = 0; choice < ChoiceCount(model); choice++)
                if (Donor(model, choice) == donor) return choice;
            return 0;
        }

        /// <summary>
        /// One form for one look: a known model, a choice for each of its parts and none past them,
        /// and no parts at all when every one is the model's own. Two copies compare looks this way.
        /// </summary>
        internal static PlayerAppearance Normalize(PlayerAppearance appearance)
        {
            if (appearance.Model == Unchosen) return new PlayerAppearance { Model = Unchosen };
            byte model = Valid(appearance.Model);
            int count = PartCount(model), choices = ChoiceCount(model);
            var parts = new byte[count];
            bool any = false;
            for (int i = 0; i < count && appearance.Parts != null && i < appearance.Parts.Length; i++)
            {
                byte choice = appearance.Parts[i];
                parts[i] = choice < choices ? choice : (byte)0;
                any |= parts[i] != 0;
            }
            return new PlayerAppearance { Model = model, Parts = any ? parts : null };
        }

        /// <summary>
        /// Gives the parts of a fresh, still inactive copy of the model the materials of the chosen
        /// models. The donors are the game's prefabs themselves: read, never instantiated or changed.
        /// </summary>
        internal static void Apply(GameObject body, PlayerAppearance appearance)
        {
            appearance = Normalize(appearance);
            if (appearance.Parts == null) return;
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null) return;
            var donors = new Dictionary<byte, Dictionary<(string, Mesh), Material[]>>();
            for (int i = 0; i < appearance.Parts.Length; i++)
            {
                byte donor = Donor(appearance.Model, appearance.Parts[i]);
                if (donor == appearance.Model) continue;
                if (!donors.TryGetValue(donor, out Dictionary<(string, Mesh), Material[]> materials))
                    donors[donor] = materials = MaterialsOf(dp.InstantiateCrewByID(Ids[donor]));
                if (materials == null) continue;

                string name = PartObject(appearance.Model, i);
                Transform part = FindPart(body.transform, name);
                if (part == null) continue;
                int applied = 0;
                foreach (Renderer renderer in PartRenderers(body, part))
                {
                    Mesh mesh = MeshOf(renderer);
                    if (mesh == null || !materials.TryGetValue((renderer.name, mesh), out Material[] shared)) continue;
                    renderer.sharedMaterials = shared;
                    applied++;
                }
                if (applied == 0) WarnOnce(Ids[donor] + "/" + name, "has no renderer on the same mesh");
            }
        }

        // The main body's renderers of a prefab by object name and mesh. The second body has the
        // same names and is left out.
        private static Dictionary<(string, Mesh), Material[]> MaterialsOf(GameObject prefab)
        {
            if (prefab == null) return null;
            var second = new List<Transform>();
            foreach (GameObject part in AvatarFactory.SecondBody(prefab)) second.Add(part.transform);
            var materials = new Dictionary<(string, Mesh), Material[]>();
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = MeshOf(renderer);
                if (mesh == null || second.Exists(renderer.transform.IsChildOf)) continue;
                var key = (renderer.name, mesh);
                if (!materials.ContainsKey(key)) materials.Add(key, renderer.sharedMaterials);
            }
            return materials;
        }

        // The part must be one object of the body: after a game update a name that is missing or
        // found twice changes nothing rather than a guess.
        private static Transform FindPart(Transform root, string name)
        {
            Transform found = null;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name != name) continue;
                if (found != null) { WarnOnce(name, "is found more than once"); return null; }
                found = child;
            }
            if (found == null) WarnOnce(name, "is missing");
            return found;
        }

        // A part's renderers and the other levels of detail of the same part, wherever they hang:
        // a level left out would bring the old look back at a distance.
        private static IEnumerable<Renderer> PartRenderers(GameObject body, Transform part)
        {
            var renderers = new HashSet<Renderer>(part.GetComponentsInChildren<Renderer>(true));
            foreach (LODGroup group in body.GetComponentsInChildren<LODGroup>(true))
            {
                LOD[] lods = group.GetLODs();
                bool ours = false;
                foreach (LOD lod in lods)
                    foreach (Renderer renderer in lod.renderers)
                        ours |= renderer != null && renderers.Contains(renderer);
                if (!ours) continue;
                foreach (LOD lod in lods)
                    foreach (Renderer renderer in lod.renderers)
                        if (renderer != null) renderers.Add(renderer);
            }
            return renderers;
        }

        private static Mesh MeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        private static void WarnOnce(string what, string problem)
        {
            if (Warned.Add(what + problem)) Plugin.Log.LogWarning("[Appearance] Part '" + what + "' " + problem + "; it keeps the model's own look");
        }
    }
}
