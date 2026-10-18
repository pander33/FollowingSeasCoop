using BepInEx.Configuration;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Sync;
using Newtonsoft.Json.Linq;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>
    /// The local player's own look, kept in the config by names (the model's crew id, each part's
    /// object and the crew id it takes its materials from), so a reordered catalog still reads it.
    /// Nobody else's look is ever written here.
    /// </summary>
    internal sealed class AppearanceProfile
    {
        private const int Schema = 1;
        private readonly ConfigEntry<string> _config;

        internal PlayerAppearance Committed { get; private set; }

        internal AppearanceProfile(ConfigEntry<string> config)
        {
            _config = config;
            Committed = new PlayerAppearance { Model = AppearanceCatalog.Unchosen };
            if (string.IsNullOrWhiteSpace(config.Value)) return;
            // A profile that cannot be read stays in the config as it is until the player applies another.
            try { Committed = Read(JObject.Parse(config.Value)); }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Appearance] The profile in the config cannot be read, " +
                                      "a model by your place in the crew is used: " + e.Message);
            }
        }

        internal void Commit(PlayerAppearance appearance)
        {
            Committed = AppearanceCatalog.Normalize(appearance);
            _config.Value = Committed.Model == AppearanceCatalog.Unchosen ? "" : Write(Committed).ToString(Newtonsoft.Json.Formatting.None);
        }

        private static PlayerAppearance Read(JObject data)
        {
            if ((int?)data["Schema"] != Schema) throw new System.FormatException("schema " + data["Schema"]);
            // A number is how the first builds of this profile wrote the model.
            JToken token = data["Model"];
            int model = token?.Type == JTokenType.Integer ? (int)token : AppearanceCatalog.IndexOf((string)token);
            if (model >= AppearanceCatalog.ModelCount) model = -1;
            if (model < 0) throw new System.FormatException("model " + data["Model"]);
            var appearance = new PlayerAppearance { Model = (byte)model, Parts = new byte[AppearanceCatalog.PartCount((byte)model)] };
            if (data["Parts"] is JObject parts)
                for (int i = 0; i < appearance.Parts.Length; i++)
                {
                    int donor = AppearanceCatalog.IndexOf((string)parts[AppearanceCatalog.PartObject(appearance.Model, i)]);
                    if (donor >= 0) appearance.Parts[i] = AppearanceCatalog.ChoiceOf(appearance.Model, (byte)donor);
                }
            return AppearanceCatalog.Normalize(appearance);
        }

        private static JObject Write(PlayerAppearance appearance)
        {
            var data = new JObject { ["Schema"] = Schema, ["Model"] = AppearanceCatalog.Id(appearance.Model) };
            if (appearance.Parts == null) return data;
            var parts = new JObject();
            for (int i = 0; i < appearance.Parts.Length; i++)
                if (appearance.Parts[i] != 0)
                    parts[AppearanceCatalog.PartObject(appearance.Model, i)] =
                        AppearanceCatalog.Id(AppearanceCatalog.Donor(appearance.Model, appearance.Parts[i]));
            data["Parts"] = parts;
            return data;
        }
    }
}
