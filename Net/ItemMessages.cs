using System;
using System.IO;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    public enum ItemKind : byte { Portable, Cargo }
    public enum ItemStatus : byte { Free, Held, Secured, Deleted }
    public enum ItemActionKind : byte
    {
        Manifest, Take, Drop, Throw, Create, Delete, PlaceCargo, MoveCargo, Movement, Scenes
    }

    // No Unity types in the wire layer. Held poses are derived from the holder, not streamed.
    public sealed class ItemPose
    {
        public string ShipId = "";
        public long Tick;
        public double X, Y, Z;
        public float Qx, Qy, Qz, Qw = 1f;
        public float Vx, Vy, Vz, Wx, Wy, Wz;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ShipId); w.Put(Tick);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Qx); w.Put(Qy); w.Put(Qz); w.Put(Qw);
            w.Put(Vx); w.Put(Vy); w.Put(Vz); w.Put(Wx); w.Put(Wy); w.Put(Wz);
        }

        public void Deserialize(NetDataReader r)
        {
            ShipId = r.GetString(); Tick = r.GetLong();
            X = r.GetDouble(); Y = r.GetDouble(); Z = r.GetDouble();
            Qx = r.GetFloat(); Qy = r.GetFloat(); Qz = r.GetFloat(); Qw = r.GetFloat();
            Vx = r.GetFloat(); Vy = r.GetFloat(); Vz = r.GetFloat();
            Wx = r.GetFloat(); Wy = r.GetFloat(); Wz = r.GetFloat();
        }
    }

    public sealed class ItemRecord
    {
        public uint NetId, Revision, Holder, Leader;
        public ItemKind Kind;
        public string Key = "", ItemType = "", Name = "", TaskId = "";
        public int Box = -1;
        public ItemStatus Status;
        public byte Flags;
        public ItemPose Pose = new ItemPose();

        public void Serialize(NetDataWriter w)
        {
            w.Put(NetId); w.Put((byte)Kind); w.Put(Key); w.Put(ItemType); w.Put(Name);
            w.Put(TaskId); w.Put(Box); w.Put(Revision); w.Put((byte)Status);
            w.Put(Holder); w.Put(Leader); w.Put(Flags); Pose.Serialize(w);
        }

        public void Deserialize(NetDataReader r)
        {
            NetId = r.GetUInt(); Kind = (ItemKind)r.GetByte(); Key = r.GetString();
            ItemType = r.GetString(); Name = r.GetString(); TaskId = r.GetString();
            Box = r.GetInt(); Revision = r.GetUInt(); Status = (ItemStatus)r.GetByte();
            Holder = r.GetUInt(); Leader = r.GetUInt(); Flags = r.GetByte(); Pose.Deserialize(r);
        }
    }

    public sealed class ItemManifestMsg : INetMessage
    {
        public const int EntriesPerPart = 32;
        public MsgType Type => MsgType.ItemManifest;
        public uint Generation;
        public int Part, Parts;
        public ItemRecord[] Entries = Array.Empty<ItemRecord>();
        public void Serialize(NetDataWriter w)
        {
            w.Put(Generation); w.Put(Part); w.Put(Parts); w.Put(Entries.Length);
            foreach (ItemRecord entry in Entries) entry.Serialize(w);
        }
        public void Deserialize(NetDataReader r)
        {
            Generation = r.GetUInt(); Part = r.GetInt(); Parts = r.GetInt();
            int count = r.GetInt();
            if (Parts < 1 || Part < 0 || Part >= Parts || count < 0 || count > EntriesPerPart)
                throw new InvalidDataException("Invalid item manifest part");
            Entries = new ItemRecord[count];
            for (int i = 0; i < count; i++) { Entries[i] = new ItemRecord(); Entries[i].Deserialize(r); }
        }
    }

    public sealed class ItemActionMsg : INetMessage
    {
        public MsgType Type => MsgType.ItemAction;
        public uint RequestId, NetId, SeenRevision;
        public ItemActionKind Action;
        public ItemKind Kind;
        public string ItemType = "", Name = "", TaskId = "", Scenes = "";
        public int Box = -1;
        public byte Flags;
        public ItemPose Pose = new ItemPose();
        public void Serialize(NetDataWriter w)
        {
            w.Put(RequestId); w.Put((byte)Action); w.Put(NetId); w.Put(SeenRevision);
            w.Put((byte)Kind); w.Put(ItemType); w.Put(Name); w.Put(TaskId); w.Put(Box);
            w.Put(Scenes); w.Put(Flags); Pose.Serialize(w);
        }
        public void Deserialize(NetDataReader r)
        {
            RequestId = r.GetUInt(); Action = (ItemActionKind)r.GetByte(); NetId = r.GetUInt();
            SeenRevision = r.GetUInt(); Kind = (ItemKind)r.GetByte(); ItemType = r.GetString();
            Name = r.GetString(); TaskId = r.GetString(); Box = r.GetInt(); Scenes = r.GetString();
            Flags = r.GetByte(); Pose.Deserialize(r);
        }
    }

    public sealed class ItemStateMsg : INetMessage
    {
        public MsgType Type => MsgType.ItemState;
        public uint RequestId;
        public ItemRecord Record = new ItemRecord();
        public void Serialize(NetDataWriter w) { w.Put(RequestId); Record.Serialize(w); }
        public void Deserialize(NetDataReader r) { RequestId = r.GetUInt(); Record.Deserialize(r); }
    }
}
