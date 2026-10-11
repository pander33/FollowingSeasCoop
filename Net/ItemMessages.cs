using System;
using System.IO;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    public enum ItemKind : byte { Portable, Cargo }
    public enum ItemStatus : byte { Free, Held, Secured, Deleted }

    /// <summary>
    /// What a player did with a thing on its own copy of the game. <c>Take</c>: it is in my hand.
    /// <c>Drop</c> and <c>Throw</c>: I let go, my physics carries it from here. <c>Movement</c>: the
    /// thing I let go has come to rest at this pose. <c>Delete</c>: it was used up on my copy (the
    /// ship's hold took it). The rest are later stages.
    /// </summary>
    public enum ItemActionKind : byte
    {
        Manifest, Take, Drop, Throw, Create, Delete, PlaceCargo, MoveCargo, Movement, Scenes
    }

    public static class ItemFlags
    {
        /// <summary>A player has had it in hand: from then on its place is shared, not each copy's own.</summary>
        public const byte Touched = 1;
        /// <summary>Bought from a shop: the game holds an unbought thing still and shows its price.</summary>
        public const byte Purchased = 2;
        /// <summary>At rest at the record's pose; no copy runs physics on it.</summary>
        public const byte Settled = 4;
    }

    // No Unity types in the wire layer.
    public sealed class ItemPose
    {
        /// <summary>Not empty: the pose is in the frame of this ship's hull. Empty: real space.</summary>
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
        /// <summary><c>Leader</c>: whose physics moves a free thing that is not settled.</summary>
        public uint NetId, Revision, Holder, Leader;
        public ItemKind Kind;
        public string Key = "", ItemType = "", Name = "", TaskId = "";
        /// <summary>The object's name without its copy number: what a stand-in is cloned from.</summary>
        public string Prefab = "";
        /// <summary>Kilograms of provisions in it.</summary>
        public float Amount;
        public int Box = -1;
        public ItemStatus Status;
        public byte Flags;
        public ItemPose Pose = new ItemPose();

        public void Serialize(NetDataWriter w)
        {
            w.Put(NetId); w.Put((byte)Kind); w.Put(Key); w.Put(ItemType); w.Put(Name);
            w.Put(TaskId); w.Put(Box); w.Put(Revision); w.Put((byte)Status);
            w.Put(Holder); w.Put(Leader); w.Put(Flags); w.Put(Prefab); w.Put(Amount); Pose.Serialize(w);
        }

        public void Deserialize(NetDataReader r)
        {
            NetId = r.GetUInt(); Kind = (ItemKind)r.GetByte(); Key = r.GetString();
            ItemType = r.GetString(); Name = r.GetString(); TaskId = r.GetString();
            Box = r.GetInt(); Revision = r.GetUInt(); Status = (ItemStatus)r.GetByte();
            Holder = r.GetUInt(); Leader = r.GetUInt(); Flags = r.GetByte();
            Prefab = r.GetString(); Amount = r.GetFloat(); Pose.Deserialize(r);
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
        /// <summary>
        /// Names the thing when it has no <c>NetId</c> yet: a thing of an island the host has not
        /// loaded. The description fields then tell the host what it is.
        /// </summary>
        public string Key = "";
        public string ItemType = "", Name = "", TaskId = "", Scenes = "", Prefab = "";
        public float Amount;
        public int Box = -1;
        public byte Flags;
        public ItemPose Pose = new ItemPose();
        public void Serialize(NetDataWriter w)
        {
            w.Put(RequestId); w.Put((byte)Action); w.Put(NetId); w.Put(SeenRevision);
            w.Put((byte)Kind); w.Put(Key); w.Put(ItemType); w.Put(Name); w.Put(TaskId); w.Put(Box);
            w.Put(Scenes); w.Put(Prefab); w.Put(Amount); w.Put(Flags); Pose.Serialize(w);
        }
        public void Deserialize(NetDataReader r)
        {
            RequestId = r.GetUInt(); Action = (ItemActionKind)r.GetByte(); NetId = r.GetUInt();
            SeenRevision = r.GetUInt(); Kind = (ItemKind)r.GetByte(); Key = r.GetString();
            ItemType = r.GetString(); Name = r.GetString(); TaskId = r.GetString(); Box = r.GetInt();
            Scenes = r.GetString(); Prefab = r.GetString(); Amount = r.GetFloat();
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

    public struct ItemMotionEntry
    {
        public uint NetId;
        /// <summary>The pose is in the frame of the player ship's hull; otherwise in real space.</summary>
        public bool Aboard;
        public double X, Y, Z;
        public float Qx, Qy, Qz, Qw;
        public float Vx, Vy, Vz;
    }

    /// <summary>
    /// Poses of the things one player moves: the thing in its hand and the things it let go that
    /// have not come to rest. Sent unreliably many times a second by that player; the host relays
    /// a guest's poses to the other guests.
    /// </summary>
    public sealed class ItemMotionMsg : INetMessage
    {
        /// <summary>Keeps one message inside the smallest datagram LiteNetLib starts with.</summary>
        public const int MaxEntries = 8;
        public MsgType Type => MsgType.ItemMotion;
        /// <summary>Host clock, milliseconds, at capture.</summary>
        public long Tick;
        public ItemMotionEntry[] Entries = Array.Empty<ItemMotionEntry>();

        public void Serialize(NetDataWriter w)
        {
            int count = Math.Min(Entries.Length, MaxEntries);
            w.Put(Tick); w.Put((byte)count);
            for (int i = 0; i < count; i++)
            {
                ItemMotionEntry e = Entries[i];
                w.Put(e.NetId); w.Put(e.Aboard);
                w.Put(e.X); w.Put(e.Y); w.Put(e.Z);
                w.Put(e.Qx); w.Put(e.Qy); w.Put(e.Qz); w.Put(e.Qw);
                w.Put(e.Vx); w.Put(e.Vy); w.Put(e.Vz);
            }
        }

        public void Deserialize(NetDataReader r)
        {
            Tick = r.GetLong();
            int count = r.GetByte();
            if (count > MaxEntries) throw new InvalidDataException("Too many item poses");
            Entries = new ItemMotionEntry[count];
            for (int i = 0; i < count; i++)
            {
                ItemMotionEntry e;
                e.NetId = r.GetUInt(); e.Aboard = r.GetBool();
                e.X = r.GetDouble(); e.Y = r.GetDouble(); e.Z = r.GetDouble();
                e.Qx = r.GetFloat(); e.Qy = r.GetFloat(); e.Qz = r.GetFloat(); e.Qw = r.GetFloat();
                e.Vx = r.GetFloat(); e.Vy = r.GetFloat(); e.Vz = r.GetFloat();
                Entries[i] = e;
            }
        }
    }
}
