using System;
using System.IO;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    internal static class EconomyWire
    {
        /// <summary>Food, drink, oakum, timber, canvas, oil, aburae: the game's seven stores.</summary>
        public const int Stores = 7;
        public const int MaxDebts = 64;
        /// <summary>A task's save string holds its description; a journal line is short.</summary>
        public const int MaxText = 4000;

        public static void Put(NetDataWriter w, float[] stores)
        {
            for (int i = 0; i < Stores; i++) w.Put(stores != null && i < stores.Length ? stores[i] : 0f);
        }

        public static float[] GetStores(NetDataReader r)
        {
            var stores = new float[Stores];
            for (int i = 0; i < Stores; i++) stores[i] = r.GetFloat();
            return stores;
        }

        public static void Put(NetDataWriter w, string[] lines)
        {
            int count = lines == null ? 0 : Math.Min(lines.Length, MaxDebts);
            w.Put((byte)count);
            for (int i = 0; i < count; i++) w.Put(lines[i] ?? "", MaxText);
        }

        public static string[] GetLines(NetDataReader r)
        {
            int count = r.GetByte();
            if (count > MaxDebts) throw new InvalidDataException("Too many lines");
            var lines = new string[count];
            for (int i = 0; i < count; i++) lines[i] = r.GetString(MaxText);
            return lines;
        }
    }

    /// <summary>
    /// Host → one guest: the household as the host has it — money, the ship's stores, the stores
    /// ashore (<c>GameData.storedProvisions</c>), debts — and
    /// the number of the last change of this guest that is already in it.
    /// </summary>
    public sealed class EconomyStateMsg : INetMessage
    {
        public MsgType Type => MsgType.EconomyState;
        public uint Acked;
        public float Money;
        /// <summary>False while the host has no ship to read the stores from.</summary>
        public bool HasStores;
        public float[] Stores = new float[EconomyWire.Stores];
        public float[] Shore = new float[EconomyWire.Stores];
        /// <summary>The game's debt lines, "creditor=amount".</summary>
        public string[] Debts = Array.Empty<string>();
        /// <summary>Fish in the ship's fish hold; false while the host's ship has none.</summary>
        public bool HasFish;
        public int Fish;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Acked); w.Put(Money); w.Put(HasStores);
            EconomyWire.Put(w, Stores); EconomyWire.Put(w, Shore); EconomyWire.Put(w, Debts);
            w.Put(HasFish); w.Put(Fish);
        }

        public void Deserialize(NetDataReader r)
        {
            Acked = r.GetUInt(); Money = r.GetFloat(); HasStores = r.GetBool();
            Stores = EconomyWire.GetStores(r); Shore = EconomyWire.GetStores(r); Debts = EconomyWire.GetLines(r);
            HasFish = r.GetBool(); Fish = r.GetInt();
        }
    }

    /// <summary>
    /// Guest → host: what the guest's own game changed in the household since its last report —
    /// by how much the money, each store aboard and ashore and each debt moved. Differences, not
    /// figures: two players' changes made at once both count.
    /// </summary>
    public sealed class EconomyDeltaMsg : INetMessage
    {
        public MsgType Type => MsgType.EconomyDelta;
        public uint Seq;
        public float Money;
        public float[] Stores = new float[EconomyWire.Stores];
        public float[] Shore = new float[EconomyWire.Stores];
        /// <summary>"creditor=change" for each debt that changed.</summary>
        public string[] Debts = Array.Empty<string>();
        /// <summary>The guest's world was loaded again from the copy it joined with: it asks for everything anew.</summary>
        public bool Resync;
        /// <summary>Fish put into the fish hold (or sold out of it, negative).</summary>
        public int Fish;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Seq); w.Put(Money); EconomyWire.Put(w, Stores); EconomyWire.Put(w, Shore);
            EconomyWire.Put(w, Debts); w.Put(Resync); w.Put(Fish);
        }

        public void Deserialize(NetDataReader r)
        {
            Seq = r.GetUInt(); Money = r.GetFloat(); Stores = EconomyWire.GetStores(r); Shore = EconomyWire.GetStores(r);
            Debts = EconomyWire.GetLines(r); Resync = r.GetBool(); Fish = r.GetInt();
        }
    }

    /// <summary><c>Roster</c>: <c>Data</c> lists the ids of every contract the host has, one a line.</summary>
    public enum TaskKind : byte { Contract, WoodProduct, LinenProduct, IronProduct, Roster }

    /// <summary>
    /// A task as its save string, from the copy whose game changed it; the host relays a guest's.
    /// A contract is named by its id; the three product tasks are fixed objects, named by kind.
    /// </summary>
    public sealed class TaskStateMsg : INetMessage
    {
        public MsgType Type => MsgType.TaskState;
        public TaskKind Kind;
        public string Id = "";
        public string Data = "";
        /// <summary>The contract was abandoned.</summary>
        public bool Deleted;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Kind); w.Put(Id ?? "", Limits.Text); w.Put(Data ?? "", EconomyWire.MaxText); w.Put(Deleted);
        }

        public void Deserialize(NetDataReader r)
        {
            Kind = (TaskKind)r.GetByte(); Id = r.GetString(Limits.Text); Data = r.GetString(EconomyWire.MaxText); Deleted = r.GetBool();
        }
    }

    /// <summary>A new line of the ship's journal, from the copy whose game wrote it.</summary>
    public sealed class JournalLineMsg : INetMessage
    {
        public MsgType Type => MsgType.JournalLine;
        public string Line = "";
        public void Serialize(NetDataWriter w) => w.Put(Line ?? "", EconomyWire.MaxText);
        public void Deserialize(NetDataReader r) => Line = r.GetString(EconomyWire.MaxText);
    }
}
