using System;
using System.IO;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    internal static class CargoWire
    {
        /// <summary>More than any ship holds; a count above it is a broken packet.</summary>
        public const int MaxCrates = 4096;
    }

    /// <summary>
    /// A crate in the ship's hold: the cargo box by its number in the ship's own list, the place in
    /// that box, the cargo type and the task it belongs to.
    /// </summary>
    public struct CargoCrate
    {
        public byte Box;
        public float X, Y, Z;
        public string CargoId;
        public string TaskId;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Box); w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(CargoId ?? "", Limits.Text); w.Put(TaskId ?? "", Limits.Text);
        }

        public void Deserialize(NetDataReader r)
        {
            Box = r.GetByte(); X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat();
            CargoId = r.GetString(Limits.Text); TaskId = r.GetString(Limits.Text);
        }
    }

    public enum CargoOpKind : byte { Add, Remove, Move }

    /// <summary><c>Crate</c> is the crate where it was; <c>To*</c> is where a moved one went.</summary>
    public struct CargoOp
    {
        public CargoOpKind Kind;
        public CargoCrate Crate;
        public byte ToBox;
        public float ToX, ToY, ToZ;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Kind); Crate.Serialize(w);
            w.Put(ToBox); w.Put(ToX); w.Put(ToY); w.Put(ToZ);
        }

        public void Deserialize(NetDataReader r)
        {
            Kind = (CargoOpKind)r.GetByte(); Crate.Deserialize(r);
            ToBox = r.GetByte(); ToX = r.GetFloat(); ToY = r.GetFloat(); ToZ = r.GetFloat();
        }
    }

    /// <summary>
    /// What the sender's own game did to the cargo of a ship since its last report; the host relays
    /// a guest's to the others.
    /// </summary>
    public sealed class CargoOpsMsg : INetMessage
    {
        public MsgType Type => MsgType.CargoOps;
        public string ShipId = "";
        /// <summary>Guest → host: no operations; the guest is on this ship anew and asks for its whole hold.</summary>
        public bool Resync;
        public CargoOp[] Ops = Array.Empty<CargoOp>();

        public void Serialize(NetDataWriter w)
        {
            w.Put(ShipId ?? "", Limits.Text);
            w.Put(Resync);
            int count = Math.Min(Ops.Length, CargoWire.MaxCrates);
            w.Put((ushort)count);
            for (int i = 0; i < count; i++) Ops[i].Serialize(w);
        }

        public void Deserialize(NetDataReader r)
        {
            ShipId = r.GetString(Limits.Text);
            Resync = r.GetBool();
            int count = r.GetUShort();
            if (count > CargoWire.MaxCrates) throw new InvalidDataException("Too many cargo operations");
            Ops = new CargoOp[count];
            for (int i = 0; i < count; i++) Ops[i].Deserialize(r);
        }
    }

    /// <summary>Host → guest: every crate of the ship as the host has it.</summary>
    public sealed class CargoStateMsg : INetMessage
    {
        public MsgType Type => MsgType.CargoState;
        public string ShipId = "";
        public CargoCrate[] Crates = Array.Empty<CargoCrate>();

        public void Serialize(NetDataWriter w)
        {
            w.Put(ShipId ?? "", Limits.Text);
            int count = Math.Min(Crates.Length, CargoWire.MaxCrates);
            w.Put((ushort)count);
            for (int i = 0; i < count; i++) Crates[i].Serialize(w);
        }

        public void Deserialize(NetDataReader r)
        {
            ShipId = r.GetString(Limits.Text);
            int count = r.GetUShort();
            if (count > CargoWire.MaxCrates) throw new InvalidDataException("Too many crates");
            Crates = new CargoCrate[count];
            for (int i = 0; i < count; i++) Crates[i].Deserialize(r);
        }
    }

    /// <summary>
    /// One player at a time works the cargo panel. Guest → host: <c>Want</c> — it opened the panel
    /// or closed it. Host → guests: <c>Holder</c> — who has the panel now, 0 for nobody.
    /// </summary>
    public sealed class CargoLeaseMsg : INetMessage
    {
        public MsgType Type => MsgType.CargoLease;
        public bool Want;
        public uint Holder;
        public void Serialize(NetDataWriter w) { w.Put(Want); w.Put(Holder); }
        public void Deserialize(NetDataReader r) { Want = r.GetBool(); Holder = r.GetUInt(); }
    }
}
