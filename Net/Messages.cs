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
        BoatState = 12,
        EnvState = 13,
        ControlRequest = 14,
        ControlState = 15,
        ItemManifest = 16,
        ItemAction = 17,
        ItemState = 18,
        ItemMotion = 19,
        EconomyState = 20,
        EconomyDelta = 21,
        TaskState = 22,
        JournalLine = 23,
        CargoOps = 24,
        CargoState = 25,
        CargoLease = 26,
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
        /// <summary>A control's state string (the game's own rope save string).</summary>
        public const int ControlText = 400;
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
        /// <summary>The tool in hand: its slot in the game's inventory plus one, 0 for none.</summary>
        public byte Tool;
        /// <summary>Grows by one each time the tool is used (a blow of the caulking hammer).</summary>
        public byte ToolUse;

        public void Serialize(NetDataWriter w)
        {
            w.Put(NetId);
            w.Put(Tick);
            w.Put((byte)Flags);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Yaw);
            w.Put(Pitch);
            w.Put(Vx); w.Put(Vy); w.Put(Vz);
            w.Put(Tool); w.Put(ToolUse);
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
            Tool = r.GetByte(); ToolUse = r.GetByte();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Ship and environment (host → clients)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The player ship's hull as the host's physics has it: position in real space, rotation, and
    /// the velocities used to extrapolate over a lost packet. Sent unreliably many times a second.
    /// </summary>
    public sealed class BoatStateMsg : INetMessage
    {
        public MsgType Type => MsgType.BoatState;
        /// <summary>Host clock, milliseconds, at capture.</summary>
        public long Tick;
        public double X, Y, Z;
        public float Qx, Qy, Qz, Qw;
        public float Vx, Vy, Vz;
        /// <summary>Angular velocity, radians per second.</summary>
        public float Wx, Wy, Wz;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Tick);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Qx); w.Put(Qy); w.Put(Qz); w.Put(Qw);
            w.Put(Vx); w.Put(Vy); w.Put(Vz);
            w.Put(Wx); w.Put(Wy); w.Put(Wz);
        }

        public void Deserialize(NetDataReader r)
        {
            Tick = r.GetLong();
            X = r.GetDouble(); Y = r.GetDouble(); Z = r.GetDouble();
            Qx = r.GetFloat(); Qy = r.GetFloat(); Qz = r.GetFloat(); Qw = r.GetFloat();
            Vx = r.GetFloat(); Vy = r.GetFloat(); Vz = r.GetFloat();
            Wx = r.GetFloat(); Wy = r.GetFloat(); Wz = r.GetFloat();
        }
    }

    /// <summary>
    /// Everything that makes the sea and the sky the same on every machine: the weather type, the
    /// wind, the wave spectrum, the ocean's clock and the time of day. Sent a few times a second;
    /// each message is the whole state, so a lost one costs nothing.
    /// </summary>
    public sealed class EnvStateMsg : INetMessage
    {
        public const int SpectrumBands = 14;

        public MsgType Type => MsgType.EnvState;
        public long Tick;
        /// <summary>The game's weather type id.</summary>
        public string WeatherId = "";
        /// <summary>Wind direction object's rotation, Euler degrees.</summary>
        public float WindX, WindY, WindZ;
        /// <summary>Wind strength before gusts, and the gust phase.</summary>
        public float BaseWind, GustPhase;
        /// <summary>Wave spectrum: wind speed, chop, gravity scale and the power of each band.</summary>
        public float WaveWind, Chop, GravityScale;
        public float[] PowerLog = new float[0];
        /// <summary>The ocean renderer's clock on the host at <see cref="Tick"/>, and how fast it runs.</summary>
        public float OceanTime, TimeScale;
        public int Second, Minute, Hour, Day, Month, Year;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Tick);
            w.Put(WeatherId ?? "", Limits.Name);
            w.Put(WindX); w.Put(WindY); w.Put(WindZ);
            w.Put(BaseWind); w.Put(GustPhase);
            w.Put(WaveWind); w.Put(Chop); w.Put(GravityScale);
            int bands = PowerLog == null ? 0 : System.Math.Min(PowerLog.Length, SpectrumBands);
            w.Put((byte)bands);
            for (int i = 0; i < bands; i++) w.Put(PowerLog[i]);
            w.Put(OceanTime); w.Put(TimeScale);
            w.Put(Second); w.Put(Minute); w.Put(Hour); w.Put(Day); w.Put(Month); w.Put(Year);
        }

        public void Deserialize(NetDataReader r)
        {
            Tick = r.GetLong();
            WeatherId = r.GetString(Limits.Name);
            WindX = r.GetFloat(); WindY = r.GetFloat(); WindZ = r.GetFloat();
            BaseWind = r.GetFloat(); GustPhase = r.GetFloat();
            WaveWind = r.GetFloat(); Chop = r.GetFloat(); GravityScale = r.GetFloat();
            int bands = r.GetByte();
            if (bands > SpectrumBands) throw new System.IO.InvalidDataException("Spectrum bands " + bands);
            PowerLog = new float[bands];
            for (int i = 0; i < bands; i++) PowerLog[i] = r.GetFloat();
            OceanTime = r.GetFloat(); TimeScale = r.GetFloat();
            Second = r.GetInt(); Minute = r.GetInt(); Hour = r.GetInt(); Day = r.GetInt(); Month = r.GetInt(); Year = r.GetInt();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Ship controls
    // -------------------------------------------------------------------------------------------

    /// <summary>What a control message is about. Wire values: append only.</summary>
    public enum ControlKind : byte
    {
        /// <summary>The wheel or whipstaff. A = wheel angle, B = rudder angle, C = visible rudder angle, degrees.</summary>
        Steering = 1,
        /// <summary>A mast's reef. Index = mast, A = how much sail is set, 0..1.</summary>
        Reef = 2,
        /// <summary>A rig rope (sheet, brace, halyard). Index = rope, Text = the game's own state string.</summary>
        Rope = 3,
        /// <summary>A deck winch (gaff peak, fishing poles). A = angle or joint limit, B = percent, Text = target rotations.</summary>
        Winch = 4,
        /// <summary>A door or hatch. A = 1 when the game calls it open.</summary>
        Door = 5,
        /// <summary>A lantern. A = 1 when lit.</summary>
        Lantern = 6,
        /// <summary>Ballast. A = amount, B = bow/stern balance, C = port/starboard balance.</summary>
        Ballast = 7,
        /// <summary>The kedge anchor. B = 1 when laid, A = cable length, Text = the anchor's point in real space.</summary>
        Anchor = 8,
        /// <summary>A mooring line. Index = line, A = index of the dock cleat in the world's list, -1 when not moored.</summary>
        Mooring = 9,
        /// <summary>Host → clients only: the hull's mass with the water in it. A = kilograms.</summary>
        Water = 10,
        /// <summary>Client → host only, a command rather than a state: A = kilograms of water this player pumped out.</summary>
        Pump = 11,
    }

    /// <summary>
    /// The state of one ship control. Controls are addressed by kind and by their index in the
    /// ship's own lists, which are the same on every machine because the ship is the same prefab.
    /// </summary>
    public abstract class ControlMsg : INetMessage
    {
        public abstract MsgType Type { get; }
        public ControlKind Kind;
        public byte Index;
        /// <summary>Who has the control in hand; 0 when nobody (the value comes from the host's world).</summary>
        public uint Holder;
        public float A, B, C;
        public string Text = "";

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Kind);
            w.Put(Index);
            w.Put(Holder);
            w.Put(A); w.Put(B); w.Put(C);
            w.Put(Text ?? "", Limits.ControlText);
        }

        public void Deserialize(NetDataReader r)
        {
            Kind = (ControlKind)r.GetByte();
            Index = r.GetByte();
            Holder = r.GetUInt();
            A = r.GetFloat(); B = r.GetFloat(); C = r.GetFloat();
            Text = r.GetString(Limits.ControlText);
        }
    }

    /// <summary>Client → host: a control this client has in hand now reads this.</summary>
    public sealed class ControlRequestMsg : ControlMsg
    {
        public override MsgType Type => MsgType.ControlRequest;
    }

    /// <summary>Host → clients: a control now reads this.</summary>
    public sealed class ControlStateMsg : ControlMsg
    {
        public override MsgType Type => MsgType.ControlState;
    }
}
