using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    /// <summary>
    /// Wire message ids. <b>Never renumber an existing value — only append.</b> A change of any
    /// message's layout needs a new <see cref="Protocol.Version"/>.
    /// </summary>
    public enum MsgType : byte
    {
        Hello = 1,
        HelloAck = 2,
        Reject = 3,
        Disconnect = 4,
        TimeSync = 5,
        Roster = 6,
        WorldBegin = 7,
        WorldChunk = 8,
        WorldEnd = 9,
        ClientWorldLoaded = 10,
        PlayerState = 11,
    }

    public interface INetMessage
    {
        MsgType Type { get; }
        void Serialize(NetDataWriter w);
        void Deserialize(NetDataReader r);
    }

    public enum RejectReason : byte
    {
        Unknown = 0,
        ProtocolMismatch = 1,
        HostNotInWorld = 2,
        SessionFull = 3,
    }

    /// <summary>Where a member is on its way into the host's world.</summary>
    public enum MemberJoinState : byte
    {
        Handshaking = 0,
        /// <summary>Receiving the world or loading it. The host stays paused meanwhile.</summary>
        Loading = 1,
        /// <summary>In the world: sends and receives gameplay state.</summary>
        Ready = 2,
    }

    internal static class Limits
    {
        public const int Name = 32;
        public const int Text = 200;
        public const int Version = 32;
        public const int RosterEntries = 16;
        /// <summary>Payload bytes of one world chunk. Reliable channels fragment it over the MTU.</summary>
        public const int WorldChunk = 16000;
        /// <summary>Largest packed world a client accepts.</summary>
        public const int WorldBytes = 32 * 1024 * 1024;
    }

    // -------------------------------------------------------------------------------------------
    // Session
    // -------------------------------------------------------------------------------------------

    /// <summary>Client → host, first message after the connection opens.</summary>
    public sealed class HelloMsg : INetMessage
    {
        public MsgType Type => MsgType.Hello;
        public int ProtocolVersion;
        public string ModVersion = "";
        /// <summary>The game's own version string; logged, never compared.</summary>
        public string GameVersion = "";
        public string PlayerName = "";

        public void Serialize(NetDataWriter w)
        {
            w.Put(ProtocolVersion);
            w.Put(ModVersion ?? "", Limits.Version);
            w.Put(GameVersion ?? "", Limits.Version);
            w.Put(PlayerName ?? "", Limits.Name);
        }

        public void Deserialize(NetDataReader r)
        {
            ProtocolVersion = r.GetInt();
            ModVersion = r.GetString(Limits.Version);
            GameVersion = r.GetString(Limits.Version);
            PlayerName = r.GetString(Limits.Name);
        }
    }

    /// <summary>Host → client: the handshake is accepted. The world transfer follows.</summary>
    public sealed class HelloAckMsg : INetMessage
    {
        public MsgType Type => MsgType.HelloAck;
        public uint YourNetId;
        public long HostTick;
        public string HostName = "";
        public string ModVersion = "";
        public string GameVersion = "";

        public void Serialize(NetDataWriter w)
        {
            w.Put(YourNetId);
            w.Put(HostTick);
            w.Put(HostName ?? "", Limits.Name);
            w.Put(ModVersion ?? "", Limits.Version);
            w.Put(GameVersion ?? "", Limits.Version);
        }

        public void Deserialize(NetDataReader r)
        {
            YourNetId = r.GetUInt();
            HostTick = r.GetLong();
            HostName = r.GetString(Limits.Name);
            ModVersion = r.GetString(Limits.Version);
            GameVersion = r.GetString(Limits.Version);
        }
    }

    /// <summary>Host → client, carried in the disconnect packet: why the handshake was refused.</summary>
    public sealed class RejectMsg : INetMessage
    {
        public MsgType Type => MsgType.Reject;
        public RejectReason Reason;
        public string Detail = "";

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Reason);
            w.Put(Detail ?? "", Limits.Text);
        }

        public void Deserialize(NetDataReader r)
        {
            Reason = (RejectReason)r.GetByte();
            Detail = r.GetString(Limits.Text);
        }
    }

    /// <summary>Either direction, carried in the disconnect packet: an orderly end with a reason.</summary>
    public sealed class DisconnectMsg : INetMessage
    {
        public MsgType Type => MsgType.Disconnect;
        public string Reason = "";

        public void Serialize(NetDataWriter w) => w.Put(Reason ?? "", Limits.Text);
        public void Deserialize(NetDataReader r) => Reason = r.GetString(Limits.Text);
    }

    /// <summary>Client → host ping, host → client reply with the host clock.</summary>
    public sealed class TimeSyncMsg : INetMessage
    {
        public MsgType Type => MsgType.TimeSync;
        public bool IsReply;
        public long ClientSendTick;
        public long ServerTick;

        public void Serialize(NetDataWriter w)
        {
            w.Put(IsReply);
            w.Put(ClientSendTick);
            w.Put(ServerTick);
        }

        public void Deserialize(NetDataReader r)
        {
            IsReply = r.GetBool();
            ClientSendTick = r.GetLong();
            ServerTick = r.GetLong();
        }
    }

    public struct RosterEntry
    {
        public uint NetId;
        public string Name;
        public MemberJoinState State;
    }

    /// <summary>Host → clients: everyone in the session. A player missing from it has left.</summary>
    public sealed class RosterMsg : INetMessage
    {
        public MsgType Type => MsgType.Roster;
        public int Revision;
        public RosterEntry[] Entries = new RosterEntry[0];

        public void Serialize(NetDataWriter w)
        {
            w.Put(Revision);
            int count = Entries == null ? 0 : System.Math.Min(Entries.Length, Limits.RosterEntries);
            w.Put((byte)count);
            for (int i = 0; i < count; i++)
            {
                w.Put(Entries[i].NetId);
                w.Put(Entries[i].Name ?? "", Limits.Name);
                w.Put((byte)Entries[i].State);
            }
        }

        public void Deserialize(NetDataReader r)
        {
            Revision = r.GetInt();
            int count = r.GetByte();
            if (count > Limits.RosterEntries) throw new System.IO.InvalidDataException("Roster too long");
            Entries = new RosterEntry[count];
            for (int i = 0; i < count; i++)
            {
                Entries[i].NetId = r.GetUInt();
                Entries[i].Name = r.GetString(Limits.Name);
                Entries[i].State = (MemberJoinState)r.GetByte();
            }
        }
    }

    // -------------------------------------------------------------------------------------------
    // World transfer (host → client)
    // -------------------------------------------------------------------------------------------

    /// <summary>Announces the host's world: the game's save object as JSON, gzip-packed, in chunks.</summary>
    public sealed class WorldBeginMsg : INetMessage
    {
        public MsgType Type => MsgType.WorldBegin;
        public int TransferId;
        public int PackedBytes;
        public int RawBytes;
        public int ChunkCount;
        /// <summary>CRC-32 of the unpacked JSON bytes.</summary>
        public uint Crc;

        public void Serialize(NetDataWriter w)
        {
            w.Put(TransferId);
            w.Put(PackedBytes);
            w.Put(RawBytes);
            w.Put(ChunkCount);
            w.Put(Crc);
        }

        public void Deserialize(NetDataReader r)
        {
            TransferId = r.GetInt();
            PackedBytes = r.GetInt();
            RawBytes = r.GetInt();
            ChunkCount = r.GetInt();
            Crc = r.GetUInt();
        }
    }

    public sealed class WorldChunkMsg : INetMessage
    {
        public MsgType Type => MsgType.WorldChunk;
        public int TransferId;
        public int Index;
        public byte[] Data = new byte[0];

        public void Serialize(NetDataWriter w)
        {
            w.Put(TransferId);
            w.Put(Index);
            int length = Data == null ? 0 : Data.Length;
            w.Put((ushort)length);
            if (length > 0) w.Put(Data, 0, length);
        }

        public void Deserialize(NetDataReader r)
        {
            TransferId = r.GetInt();
            Index = r.GetInt();
            int length = r.GetUShort();
            if (length > Limits.WorldChunk || length > r.AvailableBytes)
                throw new System.IO.InvalidDataException("World chunk length " + length);
            Data = new byte[length];
            if (length > 0) r.GetBytes(Data, length);
        }
    }

    public sealed class WorldEndMsg : INetMessage
    {
        public MsgType Type => MsgType.WorldEnd;
        public int TransferId;

        public void Serialize(NetDataWriter w) => w.Put(TransferId);
        public void Deserialize(NetDataReader r) => TransferId = r.GetInt();
    }

    /// <summary>Client → host: the transferred world is loaded (or failed to), the host may resume.</summary>
    public sealed class ClientWorldLoadedMsg : INetMessage
    {
        public MsgType Type => MsgType.ClientWorldLoaded;
        public bool Ok;
        public string Detail = "";

        public void Serialize(NetDataWriter w)
        {
            w.Put(Ok);
            w.Put(Detail ?? "", Limits.Text);
        }

        public void Deserialize(NetDataReader r)
        {
            Ok = r.GetBool();
            Detail = r.GetString(Limits.Text);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Players
    // -------------------------------------------------------------------------------------------

    [System.Flags]
    public enum PlayerFlags : byte
    {
        None = 0,
        /// <summary>The pose is in the frame of the player ship's hull; otherwise in real space.</summary>
        OnShip = 1,
        Crouching = 2,
        Swimming = 4,
    }

    /// <summary>
    /// One player's pose, sent unreliably several times a second by its owner and relayed by the host.
    /// The position is the feet point. On the ship it is hull-local; elsewhere it is real space
    /// (scene position plus the floating-origin offset), which is why it travels as doubles.
    /// </summary>
    public sealed class PlayerStateMsg : INetMessage
    {
        public MsgType Type => MsgType.PlayerState;
        public uint NetId;
        /// <summary>Host clock, milliseconds, at capture.</summary>
        public long Tick;
        public PlayerFlags Flags;
        public double X, Y, Z;
        /// <summary>Facing around the frame's up axis, degrees.</summary>
        public float Yaw;
        /// <summary>Look pitch, degrees, positive down.</summary>
        public float Pitch;
        public float Vx, Vy, Vz;

        public void Serialize(NetDataWriter w)
        {
            w.Put(NetId);
            w.Put(Tick);
            w.Put((byte)Flags);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Yaw);
            w.Put(Pitch);
            w.Put(Vx); w.Put(Vy); w.Put(Vz);
        }

        public void Deserialize(NetDataReader r)
        {
            NetId = r.GetUInt();
            Tick = r.GetLong();
            Flags = (PlayerFlags)r.GetByte();
            X = r.GetDouble(); Y = r.GetDouble(); Z = r.GetDouble();
            Yaw = r.GetFloat();
            Pitch = r.GetFloat();
            Vx = r.GetFloat(); Vy = r.GetFloat(); Vz = r.GetFloat();
        }
    }
}
