using System;
using System.IO;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    public enum AnimalStatus : byte { Dead = 1, Harvested = 2 }

    /// <summary>
    /// An animal that was shot or harvested, named by its place in its scene. A dead one carries
    /// where it fell, in real space: animals wander on each copy by themselves.
    /// </summary>
    public struct AnimalEntry
    {
        public string Key;
        public AnimalStatus Status;
        public double X, Y, Z;
        public float Qx, Qy, Qz, Qw;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Key ?? "", AnimalStateMsg.MaxKey); w.Put((byte)Status);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Qx); w.Put(Qy); w.Put(Qz); w.Put(Qw);
        }

        public void Deserialize(NetDataReader r)
        {
            Key = r.GetString(AnimalStateMsg.MaxKey); Status = (AnimalStatus)r.GetByte();
            X = r.GetDouble(); Y = r.GetDouble(); Z = r.GetDouble();
            Qx = r.GetFloat(); Qy = r.GetFloat(); Qz = r.GetFloat(); Qw = r.GetFloat();
        }
    }

    /// <summary>
    /// What became of animals, from the copy whose player did it; the host relays a guest's and
    /// tells a newcomer about every one.
    /// </summary>
    public sealed class AnimalStateMsg : INetMessage
    {
        /// <summary>A scene path with every name escaped.</summary>
        public const int MaxKey = 1000;
        public const int MaxEntries = 32;

        public MsgType Type => MsgType.AnimalState;
        public AnimalEntry[] Entries = Array.Empty<AnimalEntry>();

        public void Serialize(NetDataWriter w)
        {
            int count = Math.Min(Entries.Length, MaxEntries);
            w.Put((byte)count);
            for (int i = 0; i < count; i++) Entries[i].Serialize(w);
        }

        public void Deserialize(NetDataReader r)
        {
            int count = r.GetByte();
            if (count > MaxEntries) throw new InvalidDataException("Too many animals");
            Entries = new AnimalEntry[count];
            for (int i = 0; i < count; i++) Entries[i].Deserialize(r);
        }
    }

    /// <summary>The heaviest catch of a kind of fish, as the game keeps it: <c>id=weight</c>.</summary>
    public sealed class FishRecordMsg : INetMessage
    {
        public MsgType Type => MsgType.FishRecord;
        public string Line = "";
        public void Serialize(NetDataWriter w) => w.Put(Line ?? "", Limits.Text);
        public void Deserialize(NetDataReader r) => Line = r.GetString(Limits.Text);
    }
}
