using System;
using System.Collections.Generic;
using System.Text;

namespace FollowingSeasCoop.Runtime
{
    public enum PatchHealthState
    {
        Unknown,
        Ok,
        Failed
    }

    /// <summary>
    /// Every Harmony patch set is installed through <see cref="Install"/>: a set that fails (the
    /// game changed a method) turns off its own feature instead of the whole mod, and a required
    /// one blocks sessions with a readable reason.
    /// </summary>
    public static class PatchHealth
    {
        private sealed class Entry
        {
            public PatchHealthState State;
            public string Detail;
        }

        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();

        /// <summary>Why a session must not start: a patch set everything depends on is absent.</summary>
        public static string Blocker { get; private set; }

        internal static bool Install(string domain, Action apply, Action<string> log, bool required = false)
        {
            try
            {
                apply();
                Entries[domain] = new Entry { State = PatchHealthState.Ok, Detail = "" };
                return true;
            }
            catch (Exception error)
            {
                string reason = error.GetType().Name + ": " + error.Message;
                Entries[domain] = new Entry { State = PatchHealthState.Failed, Detail = reason };
                if (required && Blocker == null) Blocker = domain + " (" + reason + ")";
                try { log?.Invoke("patch set '" + domain + "' failed" + (required ? " (required)" : "") + ": " + error); }
                catch { } // Diagnostics must never interrupt the remaining sets.
                return false;
            }
        }

        internal static PatchHealthState StateOf(string domain)
            => Entries.TryGetValue(domain, out var entry) ? entry.State : PatchHealthState.Unknown;

        public static string Summary
        {
            get
            {
                if (Entries.Count == 0) return "not initialized";
                int ok = 0, failed = 0;
                foreach (var entry in Entries.Values)
                {
                    if (entry.State == PatchHealthState.Ok) ok++; else failed++;
                }
                return failed == 0 ? "ok (" + ok + ")" : "ok " + ok + ", failed " + failed;
            }
        }

        public static string Details
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var pair in Entries)
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(pair.Key).Append('=').Append(pair.Value.State);
                    if (!string.IsNullOrEmpty(pair.Value.Detail)) sb.Append('(').Append(pair.Value.Detail).Append(')');
                }
                return sb.Length == 0 ? "not initialized" : sb.ToString();
            }
        }
    }
}
