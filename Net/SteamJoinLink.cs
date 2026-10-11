using System;
using System.Globalization;

namespace FollowingSeasCoop.Net
{
    /// <summary>
    /// The text a host publishes in the Steam rich presence key <c>connect</c>. With it Steam shows
    /// "Join Game" next to the host in the friends list and lets the host send "Invite to Game".
    /// Steam hands the text back in one of two ways: to a running game through a callback, and to a
    /// game it has to start as command line arguments. Both are parsed here. No Steamworks types.
    /// </summary>
    public static class SteamJoinLink
    {
        public const string Token = "+followingseas_coop_join";

        public static string Format(ulong hostSteamId)
        {
            return Token + " " + hostSteamId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The host's SteamID64 from a connect string, or 0 when it is not ours.</summary>
        public static ulong Parse(string connect)
        {
            if (string.IsNullOrEmpty(connect)) return 0UL;
            return Parse(connect.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>A SteamID64 a player typed or pasted, or 0 when the text is not one.</summary>
        public static ulong ParseId(string text)
        {
            // An individual account id is 17 digits; anything else is not a host we can reach.
            text = (text ?? "").Trim();
            if (text.Length != 17) return 0UL;
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0UL;
        }

        /// <summary>The host's SteamID64 from command line arguments, or 0 when there is none.</summary>
        public static ulong Parse(string[] args)
        {
            if (args == null) return 0UL;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], Token, StringComparison.OrdinalIgnoreCase)) continue;
                return ParseId(args[i + 1]);
            }
            return 0UL;
        }
    }
}
