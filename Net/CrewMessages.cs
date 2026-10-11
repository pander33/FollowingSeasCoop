using System;
using System.IO;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    [Flags]
    public enum RowboatFlags : byte
    {
        None = 0,
        /// <summary>The player who sends the pose is in the boat: it stays that player's to move.</summary>
        Held = 1,
        /// <summary>That player sits at the oars.</summary>
        Rowing = 2,
    }

    /// <summary>
    /// The launched rowboat's pose, in real space, sent unreliably by the copy whose physics moves
    /// it: the player who is in it, otherwise the host. A guest sends its own to the host, which
    /// passes it to the other guests.
    /// </summary>
    public sealed class RowboatStateMsg : INetMessage
    {
        public MsgType Type => MsgType.RowboatState;
        /// <summary>Whose physics moves the boat. The host writes it for a pose it relays.</summary>
        public uint Driver;
        public RowboatFlags Flags;
        /// <summary>Host clock, milliseconds, at capture.</summary>
        public long Tick;
        public double X, Y, Z;
        public float Qx, Qy, Qz, Qw;
        public float Vx, Vy, Vz;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Driver); w.Put((byte)Flags); w.Put(Tick);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Qx); w.Put(Qy); w.Put(Qz); w.Put(Qw);
            w.Put(Vx); w.Put(Vy); w.Put(Vz);
        }

        public void Deserialize(NetDataReader r)
        {
            Driver = r.GetUInt(); Flags = (RowboatFlags)r.GetByte(); Tick = r.GetLong();
            X = r.GetDouble(); Y = r.GetDouble(); Z = r.GetDouble();
            Qx = r.GetFloat(); Qy = r.GetFloat(); Qz = r.GetFloat(); Qw = r.GetFloat();
            Vx = r.GetFloat(); Vy = r.GetFloat(); Vz = r.GetFloat();
        }
    }

    [Flags]
    public enum CrewOrderFlags : byte
    {
        None = 0,
        /// <summary>Steer to <see cref="CrewOrderMsg.Heading"/>.</summary>
        Course = 1,
        /// <summary>Set the sails to <see cref="CrewOrderMsg.Reefs"/>.</summary>
        Sails = 2,
        /// <summary>Have <see cref="CrewOrderMsg.Pumps"/> of the crew at the pumps.</summary>
        Pumps = 4,
        /// <summary>Take the crew off the helm.</summary>
        LeaveHelm = 8,
    }

    /// <summary>
    /// Client → host: what a guest told the crew in the game's crew panel. The crew that works is
    /// the host's; what it does to the ship reaches the guests as the state of the controls.
    /// </summary>
    public sealed class CrewOrderMsg : INetMessage
    {
        public const int MaxMasts = 16;
        public MsgType Type => MsgType.CrewOrder;
        public CrewOrderFlags Flags;
        /// <summary>Compass course, degrees.</summary>
        public float Heading;
        /// <summary>For each mast of the ship, how much sail to set, 0..1; negative leaves it as it is.</summary>
        public float[] Reefs = Array.Empty<float>();
        public byte Pumps;

        public void Serialize(NetDataWriter w)
        {
            int count = Math.Min(Reefs?.Length ?? 0, MaxMasts);
            w.Put((byte)Flags); w.Put(Heading); w.Put(Pumps); w.Put((byte)count);
            for (int i = 0; i < count; i++) w.Put(Reefs[i]);
        }

        public void Deserialize(NetDataReader r)
        {
            Flags = (CrewOrderFlags)r.GetByte(); Heading = r.GetFloat(); Pumps = r.GetByte();
            int count = r.GetByte();
            if (count > MaxMasts) throw new InvalidDataException("Too many masts in a crew order");
            Reefs = new float[count];
            for (int i = 0; i < count; i++) Reefs[i] = r.GetFloat();
        }
    }

    /// <summary>
    /// Who is in the ship's crew. Host → clients: the crew as it is. Client → host: the crew as
    /// that player has just made it (a hand taken aboard, the crew changed at a dock); the host
    /// makes its own the same and tells everybody.
    /// </summary>
    public sealed class CrewRosterMsg : INetMessage
    {
        public const int MaxAboard = 16, MaxOwned = 32, MaxLine = 96;
        public MsgType Type => MsgType.CrewRoster;
        public string ShipId = "";
        /// <summary>The ids of the crew aboard, in the order of the ship's own list.</summary>
        public string[] Aboard = Array.Empty<string>();
        /// <summary>The game's own list of every crew member the players have: "id=ship id" or "id=NONE".</summary>
        public string[] Owned = Array.Empty<string>();

        public void Serialize(NetDataWriter w)
        {
            w.Put(ShipId ?? "", Limits.Text);
            int aboard = Math.Min(Aboard?.Length ?? 0, MaxAboard), owned = Math.Min(Owned?.Length ?? 0, MaxOwned);
            w.Put((byte)aboard);
            for (int i = 0; i < aboard; i++) w.Put(Aboard[i] ?? "", Limits.Name);
            w.Put((byte)owned);
            for (int i = 0; i < owned; i++) w.Put(Owned[i] ?? "", MaxLine);
        }

        public void Deserialize(NetDataReader r)
        {
            ShipId = r.GetString(Limits.Text);
            int aboard = r.GetByte();
            if (aboard > MaxAboard) throw new InvalidDataException("Too many crew aboard");
            Aboard = new string[aboard];
            for (int i = 0; i < aboard; i++) Aboard[i] = r.GetString(Limits.Name);
            int owned = r.GetByte();
            if (owned > MaxOwned) throw new InvalidDataException("Too many crew owned");
            Owned = new string[owned];
            for (int i = 0; i < owned; i++) Owned[i] = r.GetString(MaxLine);
        }
    }

    /// <summary>One crew member as the host's copy shows it.</summary>
    public struct CrewPose
    {
        /// <summary>Position and rotation (Euler, degrees) under the crew member's own parent on the ship.</summary>
        public float X, Y, Z, Rx, Ry, Rz;
        /// <summary>The animator's bool parameters, in the controller's order.</summary>
        public uint Bools;
        /// <summary>The animator's first four float parameters, in the controller's order.</summary>
        public float F0, F1, F2, F3;
    }

    /// <summary>
    /// Host → clients: where the ship's crew stands and what it is doing. A guest's own crew gets
    /// no orders and only shows this. The whole crew in every message, in the order of the ship's
    /// own list, so a lost one costs nothing.
    /// </summary>
    public sealed class CrewStateMsg : INetMessage
    {
        public const int MaxCrew = 16;
        public MsgType Type => MsgType.CrewState;
        public long Tick;
        public CrewPose[] Crew = Array.Empty<CrewPose>();

        public void Serialize(NetDataWriter w)
        {
            int count = Math.Min(Crew?.Length ?? 0, MaxCrew);
            w.Put(Tick); w.Put((byte)count);
            for (int i = 0; i < count; i++)
            {
                CrewPose p = Crew[i];
                w.Put(p.X); w.Put(p.Y); w.Put(p.Z); w.Put(p.Rx); w.Put(p.Ry); w.Put(p.Rz);
                w.Put(p.Bools); w.Put(p.F0); w.Put(p.F1); w.Put(p.F2); w.Put(p.F3);
            }
        }

        public void Deserialize(NetDataReader r)
        {
            Tick = r.GetLong();
            int count = r.GetByte();
            if (count > MaxCrew) throw new InvalidDataException("Too many crew members");
            Crew = new CrewPose[count];
            for (int i = 0; i < count; i++)
            {
                CrewPose p;
                p.X = r.GetFloat(); p.Y = r.GetFloat(); p.Z = r.GetFloat();
                p.Rx = r.GetFloat(); p.Ry = r.GetFloat(); p.Rz = r.GetFloat();
                p.Bools = r.GetUInt();
                p.F0 = r.GetFloat(); p.F1 = r.GetFloat(); p.F2 = r.GetFloat(); p.F3 = r.GetFloat();
                Crew[i] = p;
            }
        }
    }
}
