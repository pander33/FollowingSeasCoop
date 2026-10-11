using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>
    /// What the mod did, in the order it did it: a short memory per subsystem for the Debug panel,
    /// a count of everything that went wrong, and — with <c>Dev/Trace</c> — a file per copy whose
    /// lines carry the host's clock, so the files of two copies read as one story
    /// (<c>Tools/Merge-Trace.ps1</c>). Costs nothing worth naming: it is always on.
    /// </summary>
    internal static class CoopTrace
    {
        private const int PerArea = 40;
        private const int ProblemsKept = 8;

        private struct Entry
        {
            public long Local;
            public int Times;
            public string What, Key, Value;
        }

        private sealed class Area
        {
            public readonly Entry[] Ring = new Entry[PerArea];
            public int Next, Count;
        }

        private static readonly Dictionary<string, Area> Areas = new Dictionary<string, Area>();
        private static readonly List<string> Order = new List<string>();
        // Messages sent many times a second are only counted: "Players in", "Ship out".
        private static readonly SortedDictionary<string, int> Counters = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private static readonly string[] LastProblems = new string[ProblemsKept];
        private static int _problemNext;
        private static readonly System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();
        private static StreamWriter _file;
        private static string _who = "";

        /// <summary>The host's clock, milliseconds. Set by the runtime layer.</summary>
        public static Func<long> Clock = () => 0L;

        /// <summary>Warnings and errors of the mod since the game started.</summary>
        public static int Problems { get; private set; }
        /// <summary>Exceptions the game logged that have no frame of the mod in them.</summary>
        public static int GameErrors { get; private set; }
        public static string LastGameError { get; private set; } = "";
        public static string FilePath { get; private set; } = "";
        public static IReadOnlyList<string> AreaNames => Order;

        public static void Note(string area, string what, string key, string value = "")
        {
            try
            {
                long tick = Clock();
                if (!Areas.TryGetValue(area, out Area a))
                {
                    Areas[area] = a = new Area();
                    Order.Add(area);
                }
                // A rope being hauled reports ten times a second: in the panel that is one line
                // that counts, so it does not push out what happened before. The file has them all.
                // "got" and "sent" of one control alternate on the host, hence the few lines back.
                int last = -1;
                // A note without a key (a problem) is never the same as the one before it.
                for (int back = 1; !string.IsNullOrEmpty(key) && back <= 4 && back <= a.Count && last < 0; back++)
                {
                    int at = (a.Next - back + PerArea) % PerArea;
                    if (a.Ring[at].What == what && a.Ring[at].Key == (key ?? "")) last = at;
                }
                if (last >= 0)
                {
                    a.Ring[last].Times++;
                    a.Ring[last].Local = Watch.ElapsedMilliseconds;
                    a.Ring[last].Value = value ?? "";
                }
                else
                {
                    a.Ring[a.Next] = new Entry { Local = Watch.ElapsedMilliseconds, Times = 1, What = what, Key = key ?? "", Value = value ?? "" };
                    a.Next = (a.Next + 1) % PerArea;
                    if (a.Count < PerArea) a.Count++;
                }
                if (_file != null)
                    _file.WriteLine(tick + "\t" + _who + "\t" + area + "\t" + what + "\t" + Clean(key) + "\t" + Clean(value));
            }
            catch { }   // a diagnostic must never be the thing that breaks
        }

        public static void Count(string name)
        {
            Counters.TryGetValue(name, out int n);
            Counters[name] = n + 1;
        }

        public static void Problem(string area, string text)
        {
            Problems++;
            LastProblems[_problemNext] = DateTime.Now.ToString("HH:mm:ss") + "  " + Cut(text, 300);
            _problemNext = (_problemNext + 1) % ProblemsKept;
            Note(area, "PROBLEM", "", text);
        }

        public static void GameError(string text)
        {
            GameErrors++;
            LastGameError = DateTime.Now.ToString("HH:mm:ss") + "  " + Cut(text, 300);
            // A game object that throws does so every frame.
            if (GameErrors <= 20 || GameErrors % 200 == 0) Note("Game", "exception", "#" + GameErrors, text);
        }

        public static void ResetCounts()
        {
            Problems = 0;
            GameErrors = 0;
            LastGameError = "";
            Array.Clear(LastProblems, 0, LastProblems.Length);
            Counters.Clear();
        }

        /// <summary>The newest problems, newest first.</summary>
        public static void Recent(List<string> into, int max)
        {
            for (int i = 1; i <= ProblemsKept && into.Count < max; i++)
            {
                string text = LastProblems[((_problemNext - i) % ProblemsKept + ProblemsKept) % ProblemsKept];
                if (text != null) into.Add(text);
            }
        }

        /// <summary>The newest notes of one subsystem, newest first.</summary>
        public static void Last(string area, List<string> into, int max)
        {
            if (!Areas.TryGetValue(area, out Area a)) return;
            // How long ago is on this copy's own clock: the host's is only an estimate on a
            // guest and starts anew with every session.
            long now = Watch.ElapsedMilliseconds;
            for (int i = 1; i <= a.Count && into.Count < max; i++)
            {
                Entry e = a.Ring[((a.Next - i) % PerArea + PerArea) % PerArea];
                float ago = (now - e.Local) / 1000f;
                into.Add("-" + ago.ToString("F1") + "s  " + e.What + (e.Times > 1 ? " x" + e.Times : "") + "  " + e.Key +
                         (e.Value.Length > 0 ? "  " + Cut(e.Value, 90) : ""));
            }
        }

        public static string CountersLine()
        {
            var sb = new StringBuilder();
            foreach (var pair in Counters)
            {
                if (sb.Length > 0) sb.Append("   ");
                sb.Append(pair.Key).Append(' ').Append(pair.Value);
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        public static void Open(string path, string who)
        {
            Close();
            try
            {
                _who = who;
                _file = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
                FilePath = path;
                _file.WriteLine("# Following Seas Co-op trace of " + who + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                                ". Columns: host clock ms, who, subsystem, what, key, value.");
            }
            catch (Exception error)
            {
                _file = null;
                FilePath = "";
                Plugin.Log.LogWarning("[Trace] The trace file could not be opened: " + error.Message);
            }
        }

        public static void Close()
        {
            if (_file == null) return;
            try { _file.Dispose(); } catch { }
            _file = null;
            FilePath = "";
        }

        private static string Cut(string text, int max)
        {
            text = Clean(text);
            return text.Length > max ? text.Substring(0, max) + "..." : text;
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');
        }

        /// <summary>
        /// Counts every warning and error the mod writes to the BepInEx log, wherever in the code it
        /// is written, so the status line can say that something is wrong while the player looks
        /// at the game and not at the log.
        /// </summary>
        public sealed class LogWatch : ILogListener
        {
            private readonly string _source;
            public LogWatch(string source) { _source = source; }

            public void LogEvent(object sender, LogEventArgs e)
            {
                try
                {
                    if (e.Source == null || e.Source.SourceName != _source) return;
                    if ((e.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) == 0) return;
                    string text = e.Data?.ToString() ?? "";
                    // "[Controls] ..." names the subsystem.
                    string area = "Coop";
                    int close = text.IndexOf(']');
                    if (text.StartsWith("[") && close > 1 && close < 24) area = text.Substring(1, close - 1);
                    Problem(area, text);
                }
                catch { }
            }

            public void Dispose() { }
        }
    }
}
