using System.Globalization;
using System.Text;
using FollowingSeasCoop.Net;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>A message in a line of the trace: which subsystem it belongs to, what it is about, what it says.</summary>
    internal static class TraceText
    {
        /// <summary>Null for the messages sent many times a second: those are only counted.</summary>
        public static string Area(MsgType type)
        {
            switch (type)
            {
                case MsgType.WorldBegin:
                case MsgType.WorldEnd:
                case MsgType.ClientWorldLoaded: return "World";
                case MsgType.ControlRequest:
                case MsgType.ControlState: return "Controls";
                case MsgType.ItemManifest:
                case MsgType.ItemAction:
                case MsgType.ItemState: return "Items";
                case MsgType.EconomyState:
                case MsgType.EconomyDelta:
                case MsgType.JournalLine:
                case MsgType.FishRecord: return "Household";
                case MsgType.TaskState: return "Tasks";
                case MsgType.CargoOps:
                case MsgType.CargoState:
                case MsgType.CargoLease: return "Cargo";
                case MsgType.AnimalState: return "Hunt";
                case MsgType.CrewOrder:
                case MsgType.CrewRoster: return "Crew";
                case MsgType.ShipPlans:
                case MsgType.ShipLaunch: return "Shipyard";
                default: return null;
            }
        }

        public static string CounterName(MsgType type)
        {
            switch (type)
            {
                case MsgType.PlayerState: return "Players";
                case MsgType.BoatState: return "Ship";
                case MsgType.EnvState: return "Sea";
                case MsgType.ItemMotion: return "Item poses";
                case MsgType.WorldChunk: return "World chunks";
                case MsgType.RowboatState: return "Rowboat";
                case MsgType.CrewState: return "Crew poses";
                default: return type.ToString();
            }
        }

        /// <summary>The wheel while it is turned, the hull's water and a line being reeled: too many to keep one by one.</summary>
        public static bool Quiet(INetMessage msg) =>
            msg is ControlMsg control && (control.Kind == ControlKind.Steering || control.Kind == ControlKind.Water ||
                                          control.Kind == ControlKind.TrollLine);

        public static void Describe(INetMessage msg, out string key, out string value)
        {
            key = "";
            value = "";
            switch (msg)
            {
                case ControlMsg m:
                    key = m.Kind + "#" + m.Index;
                    value = F(m.A) + " " + F(m.B) + " " + F(m.C) + (string.IsNullOrEmpty(m.Text) ? "" : " '" + m.Text + "'") +
                            (m.Holder != 0 ? "  hand " + m.Holder : "");
                    break;
                case ItemManifestMsg m:
                    key = "manifest " + m.Generation;
                    value = "part " + (m.Part + 1) + "/" + m.Parts + ", " + m.Entries.Length + " records";
                    break;
                case ItemActionMsg m:
                    key = m.NetId != 0 ? "#" + m.NetId : Tail(m.Key, 60);
                    value = m.Action + "  request " + m.RequestId + "  seen rev " + m.SeenRevision + Place(m.Pose);
                    break;
                case ItemStateMsg m:
                    key = "#" + m.Record.NetId;
                    value = m.Record.Status + "  holder " + m.Record.Holder + "  mover " + m.Record.Leader + "  flags " + m.Record.Flags +
                            "  rev " + m.Record.Revision + (m.RequestId != 0 ? "  answers " + m.RequestId : "") + Place(m.Record.Pose) +
                            "  " + Tail(m.Record.Key, 60);
                    break;
                case EconomyStateMsg m:
                    key = "counted up to " + m.Acked;
                    value = "money " + F(m.Money) + (m.HasStores ? "  stores " + Stores(m.Stores) : "  no ship") +
                            (m.HasFish ? "  fish " + m.Fish : "") + "  debts " + m.Debts.Length;
                    break;
                case EconomyDeltaMsg m:
                    key = "difference " + m.Seq;
                    value = m.Resync ? "asks for everything again"
                          : "money " + F(m.Money) + "  stores " + Stores(m.Stores) + "  fish " + m.Fish + (m.HasDebts ? "  debts " + m.Debts.Length : "");
                    break;
                case TaskStateMsg m:
                    key = m.Kind + " " + m.Id;
                    value = m.Deleted ? "gone" : m.Data;
                    break;
                case JournalLineMsg m:
                    key = "journal";
                    value = m.Line;
                    break;
                case FishRecordMsg m:
                    key = "record";
                    value = m.Line;
                    break;
                case CrewRosterMsg m:
                    key = "roster";
                    value = "aboard " + string.Join(",", m.Aboard) + "  owned " + m.Owned.Length;
                    break;
                case ShipPlansMsg m:
                    key = "plans";
                    value = string.Join(" ", m.Lines);
                    break;
                case ShipLaunchMsg m:
                    key = m.Ship;
                    value = "at " + F(m.X) + " " + F(m.Z) + "  sails " + m.Sails.Length;
                    break;
                case CrewOrderMsg m:
                    key = "order";
                    value = m.Flags + "  course " + F(m.Heading) + "  pumps " + m.Pumps + "  masts " + m.Reefs.Length;
                    break;
                case CargoOpsMsg m:
                    key = "ship " + m.ShipId;
                    if (m.Resync) { value = "asks for the whole hold"; break; }
                    var sb = new StringBuilder();
                    for (int i = 0; i < m.Ops.Length && i < 4; i++)
                    {
                        CargoOp op = m.Ops[i];
                        if (i > 0) sb.Append("; ");
                        sb.Append(op.Kind).Append(" box ").Append(op.Crate.Box).Append(" (").Append(F(op.Crate.X)).Append(' ')
                          .Append(F(op.Crate.Y)).Append(' ').Append(F(op.Crate.Z)).Append(") ").Append(op.Crate.CargoId)
                          .Append(" / ").Append(op.Crate.TaskId);
                        if (op.Kind == CargoOpKind.Move)
                            sb.Append(" -> box ").Append(op.ToBox).Append(" (").Append(F(op.ToX)).Append(' ').Append(F(op.ToY)).Append(' ').Append(F(op.ToZ)).Append(')');
                    }
                    if (m.Ops.Length > 4) sb.Append("; and ").Append(m.Ops.Length - 4).Append(" more");
                    value = sb.ToString();
                    break;
                case CargoStateMsg m:
                    key = "ship " + m.ShipId;
                    value = "the whole hold, " + m.Crates.Length + " crates";
                    break;
                case CargoLeaseMsg m:
                    key = "panel";
                    value = (m.Want ? "wanted" : "given up") + "  holder " + m.Holder;
                    break;
                case AnimalStateMsg m:
                    key = m.Entries.Length == 1 ? Tail(m.Entries[0].Key, 60) : m.Entries.Length + " animals";
                    var animals = new StringBuilder();
                    for (int i = 0; i < m.Entries.Length && i < 4; i++)
                    {
                        if (i > 0) animals.Append("; ");
                        animals.Append(m.Entries[i].Status).Append(" at ").Append(F(m.Entries[i].X)).Append(' ')
                               .Append(F(m.Entries[i].Y)).Append(' ').Append(F(m.Entries[i].Z));
                    }
                    value = animals.ToString();
                    break;
                case WorldBeginMsg m:
                    key = "transfer " + m.TransferId;
                    value = m.PackedBytes + " bytes packed, " + m.RawBytes + " raw, " + m.ChunkCount + " chunks";
                    break;
                case WorldEndMsg m:
                    key = "transfer " + m.TransferId;
                    value = "end";
                    break;
                case ClientWorldLoadedMsg m:
                    key = "world";
                    value = m.Ok ? "loaded" : "failed: " + m.Detail;
                    break;
            }
        }

        private static string Place(ItemPose pose)
        {
            if (pose == null) return "";
            return "  " + (string.IsNullOrEmpty(pose.ShipId) ? "world" : "aboard") + " " + F(pose.X) + " " + F(pose.Y) + " " + F(pose.Z);
        }

        private static string Stores(float[] stores)
        {
            if (stores == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < stores.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(F(stores[i]));
            }
            return sb.ToString();
        }

        private static string Tail(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length > max ? "..." + text.Substring(text.Length - max) : text;
        }

        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
