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
    /// The chart (<c>Map2Controller</c>): the courses drawn on it and its markers are the crew's.
    ///
    /// The chart keeps them as objects while it is open and writes them into
    /// <c>GameData.mapPlots</c> / <c>mapMarkers</c> when it closes; it reads those lines only the first
    /// time it opens. So twice a second each copy compares the lines with what it last knew and sends
    /// which went and which came; a receiver changes its lines the same way and has its chart build
    /// itself anew from them the next time it opens. While the local chart is open a change waits:
    /// the chart would write its own objects over it when it closes.
    /// </summary>
    internal sealed class ChartSync
    {
        private const float Period = 0.5f;

        private static readonly FieldInfo PlotsField = AccessTools.Field(typeof(Map2Controller), "plots");
        private static readonly FieldInfo PlotControlsField = AccessTools.Field(typeof(Map2Controller), "plotControls");
        private static readonly FieldInfo MarkersField = AccessTools.Field(typeof(Map2Controller), "mapMarkers");
        private static readonly FieldInfo LoadedField = AccessTools.Field(typeof(Map2Controller), "saveLoaded");

        private readonly CoopNet _net;
        private readonly List<string> _plots = new List<string>();
        private readonly List<string> _markers = new List<string>();
        private readonly List<MapChangeMsg> _waiting = new List<MapChangeMsg>();
        private DataPersistenceManager _dp;
        private float _nextPollAt;
        private bool _warned, _warnedLong;

        public ChartSync(CoopNet net) { _net = net; }

        public void Clear()
        {
            _plots.Clear(); _markers.Clear(); _waiting.Clear();
            _dp = null;
        }

        private static bool Ready(DataPersistenceManager dp) =>
            dp != null && dp.gameData != null && dp.gameData.mapPlots != null && dp.gameData.mapMarkers != null;

        /// <summary>Every frame for a participant.</summary>
        public void Tick()
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (!Ready(dp)) return;
            if (dp != _dp)
            {
                _dp = dp;
                _waiting.Clear();
                Remember(dp.gameData);
                return;
            }
            if (Time.unscaledTime < _nextPollAt) return;
            _nextPollAt = Time.unscaledTime + Period;
            Look(dp);
            if (_waiting.Count == 0 || AnyOpen()) return;
            foreach (MapChangeMsg msg in _waiting) Write(dp.gameData, msg);
            _waiting.Clear();
            Rebuild();
        }

        private void Remember(GameData data)
        {
            _plots.Clear(); _plots.AddRange(data.mapPlots);
            _markers.Clear(); _markers.AddRange(data.mapMarkers);
        }

        // The lines the local chart wrote since the last look.
        private void Look(DataPersistenceManager dp)
        {
            GameData data = dp.gameData;
            var msg = new MapChangeMsg
            {
                AddPlots = Missing(data.mapPlots, _plots), RemovePlots = Missing(_plots, data.mapPlots),
                AddMarkers = Missing(data.mapMarkers, _markers), RemoveMarkers = Missing(_markers, data.mapMarkers),
            };
            if (msg.AddPlots.Length + msg.RemovePlots.Length + msg.AddMarkers.Length + msg.RemoveMarkers.Length == 0) return;
            Remember(data);
            foreach (MapChangeMsg part in Parts(msg))
                if (_net.IsHost) _net.Broadcast(part, DeliveryMethod.ReliableOrdered);
                else _net.SendToHost(part, DeliveryMethod.ReliableOrdered);
            CoopTrace.Note("Chart", "sent", "courses +" + msg.AddPlots.Length + " -" + msg.RemovePlots.Length,
                           "markers +" + msg.AddMarkers.Length + " -" + msg.RemoveMarkers.Length);
        }

        // The lines of one list the other lacks, counting repeats.
        private static string[] Missing(List<string> from, List<string> other)
        {
            var left = new List<string>(other);
            var missing = new List<string>();
            foreach (string line in from)
                if (!left.Remove(line)) missing.Add(line);
            return missing.ToArray();
        }

        // A message holds so many lines of so many characters; a longer line is not sent cut short,
        // which the receiver's chart could not read.
        private IEnumerable<MapChangeMsg> Parts(MapChangeMsg msg)
        {
            string[] addPlots = Fit(msg.AddPlots), removePlots = Fit(msg.RemovePlots);
            string[] addMarkers = Fit(msg.AddMarkers), removeMarkers = Fit(msg.RemoveMarkers);
            int most = Math.Max(Math.Max(addPlots.Length, removePlots.Length), Math.Max(addMarkers.Length, removeMarkers.Length));
            for (int at = 0; at == 0 || at < most; at += MapChangeMsg.MaxLines)
                yield return new MapChangeMsg
                {
                    Full = msg.Full && at == 0,
                    AddPlots = Slice(addPlots, at), RemovePlots = Slice(removePlots, at),
                    AddMarkers = Slice(addMarkers, at), RemoveMarkers = Slice(removeMarkers, at),
                };
        }

        private string[] Fit(string[] lines)
        {
            if (Array.TrueForAll(lines, line => line.Length <= MapChangeMsg.MaxLine)) return lines;
            if (!_warnedLong) Plugin.Log.LogWarning("[Chart] A course or marker is longer than " + MapChangeMsg.MaxLine + " characters and is not shared");
            _warnedLong = true;
            return Array.FindAll(lines, line => line.Length <= MapChangeMsg.MaxLine);
        }

        private static string[] Slice(string[] lines, int at)
        {
            if (at >= lines.Length) return Array.Empty<string>();
            var part = new string[Math.Min(MapChangeMsg.MaxLines, lines.Length - at)];
            Array.Copy(lines, at, part, 0, part.Length);
            return part;
        }

        public void OnChange(MapChangeMsg msg, NetPeer from)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (!Ready(dp)) return;
            if (dp == _dp) Look(dp);
            if (msg.Full) _waiting.Clear();
            if (AnyOpen()) _waiting.Add(msg);
            else
            {
                Write(dp.gameData, msg);
                Rebuild();
            }
            if (_net.IsHost)
                _net.Broadcast(msg, DeliveryMethod.ReliableOrdered, exceptNetId: _net.PlayerNetIdForPeer(from));
        }

        private void Write(GameData data, MapChangeMsg msg)
        {
            Apply(data.mapPlots, data.mapMarkers, msg);
            if (_dp != null && _dp.gameData == data) Remember(data);
        }

        private static void Apply(List<string> plots, List<string> markers, MapChangeMsg msg)
        {
            if (msg.Full)
            {
                plots.Clear(); markers.Clear();
            }
            else
            {
                foreach (string line in msg.RemovePlots) plots.Remove(line);
                foreach (string line in msg.RemoveMarkers) markers.Remove(line);
            }
            foreach (string line in msg.AddPlots) if (!plots.Contains(line)) plots.Add(line);
            foreach (string line in msg.AddMarkers) if (!markers.Contains(line)) markers.Add(line);
        }

        private static IEnumerable<Map2Controller> Charts() =>
            UnityEngine.Object.FindObjectsByType<Map2Controller>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        private static bool AnyOpen()
        {
            foreach (Map2Controller chart in Charts())
                if (chart.gameObject.activeInHierarchy) return true;
            return false;
        }

        // A closed chart drops what it drew and reads the lines again when it next opens.
        private void Rebuild()
        {
            if (PlotsField == null || PlotControlsField == null || MarkersField == null || LoadedField == null)
            {
                if (!_warned) Plugin.Log.LogWarning("[Chart] The chart's fields were not found (a game update?); a shared course shows after the world is loaded again");
                _warned = true;
                return;
            }
            foreach (Map2Controller chart in Charts())
            {
                if (chart.gameObject.activeInHierarchy || !(bool)LoadedField.GetValue(chart)) continue;
                if (PlotsField.GetValue(chart) is List<GameObject> plots)
                {
                    foreach (GameObject plot in plots)
                    {
                        if (plot == null) continue;
                        MapPlot drawn = plot.GetComponent<MapPlot>();
                        if (drawn != null) { drawn.ClearPins(); drawn.Delete(); }
                        else UnityEngine.Object.Destroy(plot);
                    }
                    plots.Clear();
                }
                if (PlotControlsField.GetValue(chart) is List<GameObject> controls)
                {
                    foreach (GameObject control in controls) if (control != null) UnityEngine.Object.Destroy(control);
                    controls.Clear();
                }
                chart.plotControlCount = 0;
                if (MarkersField.GetValue(chart) is List<GameObject> markers)
                {
                    foreach (GameObject marker in markers) if (marker != null) UnityEngine.Object.Destroy(marker);
                    markers.Clear();
                }
                LoadedField.SetValue(chart, false);
            }
        }

        /// <summary>Host: a guest whose world was loaded again holds the chart it joined with.</summary>
        public void SendAll(uint player)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (!_net.IsHost || !Ready(dp)) return;
            Look(dp);
            // With what waits for the host's own chart to close: the guest was sent it already, and
            // a whole chart without it would take it away there.
            var plots = new List<string>(dp.gameData.mapPlots);
            var markers = new List<string>(dp.gameData.mapMarkers);
            foreach (MapChangeMsg waiting in _waiting) Apply(plots, markers, waiting);
            var all = new MapChangeMsg { Full = true, AddPlots = plots.ToArray(), AddMarkers = markers.ToArray() };
            foreach (MapChangeMsg part in Parts(all)) _net.SendTo(player, part, DeliveryMethod.ReliableOrdered);
        }

        public void Dump(StateDump d)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (!Ready(dp)) { d.Info("Chart", "lines", "none"); return; }
            var plots = new List<string>(dp.gameData.mapPlots);
            plots.Sort(StringComparer.Ordinal);
            d.Line("Chart", "courses", plots.Count.ToString());
            for (int i = 0; i < plots.Count; i++) d.Line("Chart", "course " + i, plots[i]);
            var markers = new List<string>(dp.gameData.mapMarkers);
            markers.Sort(StringComparer.Ordinal);
            d.Line("Chart", "markers", markers.Count.ToString());
            for (int i = 0; i < markers.Count; i++) d.Line("Chart", "marker " + i, markers[i]);
            d.Info("Chart", "changes waiting for the chart to close", _waiting.Count.ToString());
        }
    }
}
