using System;
using System.IO;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    /// <summary>
    /// The game's list of ship plans (<c>GameData.shipPlansKnown</c>): one line per kind of ship,
    /// "id=plan known=bought=building=paid=wood=linen=iron". Both directions; a receiver replaces
    /// its line of each kind and adds the kinds it lacks.
    /// </summary>
    public sealed class ShipPlansMsg : INetMessage
    {
        public const int MaxLines = 32, MaxLine = 96;
        public MsgType Type => MsgType.ShipPlans;
        public string[] Lines = Array.Empty<string>();

        public void Serialize(NetDataWriter w)
        {
            int count = Math.Min(Lines?.Length ?? 0, MaxLines);
            w.Put((byte)count);
            for (int i = 0; i < count; i++) w.Put(Lines[i] ?? "", MaxLine);
        }

        public void Deserialize(NetDataReader r)
        {
            int count = r.GetByte();
            if (count > MaxLines) throw new InvalidDataException("Too many ship plans");
            Lines = new string[count];
            for (int i = 0; i < count; i++) Lines[i] = r.GetString(MaxLine);
        }
    }

    /// <summary>
    /// A new ship launched at a shipyard. Client → host: this player launched it. Host → clients: the
    /// host's ship is now this one. The pose is the launch point in real space; the sails are the
    /// game's sail strings the shipyard gives the new hull.
    /// </summary>
    public sealed class ShipLaunchMsg : INetMessage
    {
        public const int MaxSails = 16;
        public MsgType Type => MsgType.ShipLaunch;
        public string Ship = "";
        public double X, Y, Z;
        public float Qx, Qy, Qz, Qw;
        public string[] Sails = Array.Empty<string>();

        public void Serialize(NetDataWriter w)
        {
            w.Put(Ship ?? "", Limits.Name);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Qx); w.Put(Qy); w.Put(Qz); w.Put(Qw);
            int count = Math.Min(Sails?.Length ?? 0, MaxSails);
            w.Put((byte)count);
            for (int i = 0; i < count; i++) w.Put(Sails[i] ?? "", Limits.Text);
        }

        public void Deserialize(NetDataReader r)
        {
            Ship = r.GetString(Limits.Name);
            X = r.GetDouble(); Y = r.GetDouble(); Z = r.GetDouble();
            Qx = r.GetFloat(); Qy = r.GetFloat(); Qz = r.GetFloat(); Qw = r.GetFloat();
            int count = r.GetByte();
            if (count > MaxSails) throw new InvalidDataException("Too many sails");
            Sails = new string[count];
            for (int i = 0; i < count; i++) Sails[i] = r.GetString(Limits.Text);
        }
    }
}
