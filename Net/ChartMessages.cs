using System;
using System.IO;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    /// <summary>
    /// The chart's courses and markers (<c>GameData.mapPlots</c>, <c>mapMarkers</c>), as the game's own
    /// lines. A change says which lines went and which came; <see cref="Full"/> gives every line, for
    /// a guest whose world was loaded again. Both directions; the host passes a guest's on.
    /// </summary>
    public sealed class MapChangeMsg : INetMessage
    {
        public const int MaxLines = 64, MaxLine = 4000;
        public MsgType Type => MsgType.MapChange;
        public bool Full;
        public string[] AddPlots = Array.Empty<string>();
        public string[] RemovePlots = Array.Empty<string>();
        public string[] AddMarkers = Array.Empty<string>();
        public string[] RemoveMarkers = Array.Empty<string>();

        public void Serialize(NetDataWriter w)
        {
            w.Put(Full);
            Put(w, AddPlots); Put(w, RemovePlots); Put(w, AddMarkers); Put(w, RemoveMarkers);
        }

        public void Deserialize(NetDataReader r)
        {
            Full = r.GetBool();
            AddPlots = Get(r); RemovePlots = Get(r); AddMarkers = Get(r); RemoveMarkers = Get(r);
        }

        private static void Put(NetDataWriter w, string[] lines)
        {
            int count = Math.Min(lines?.Length ?? 0, MaxLines);
            w.Put((byte)count);
            for (int i = 0; i < count; i++) w.Put(lines[i] ?? "", MaxLine);
        }

        private static string[] Get(NetDataReader r)
        {
            int count = r.GetByte();
            if (count > MaxLines) throw new InvalidDataException("Too many chart lines");
            var lines = new string[count];
            for (int i = 0; i < count; i++) lines[i] = r.GetString(MaxLine);
            return lines;
        }
    }
}
