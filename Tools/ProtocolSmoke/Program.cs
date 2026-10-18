using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using FollowingSeasCoop.Net;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.ProtocolSmoke
{
    /// <summary>
    /// The wire layer checked without the game. Messages are filled through reflection, so a new
    /// message or a new field is covered the day it is written. Exit code 1 when anything failed.
    /// </summary>
    internal static class Program
    {
        // Wire numbers as they were released. A number here must never change; a new one is added.
        private static readonly Dictionary<string, int> FrozenTypes = new Dictionary<string, int>
        {
            { "Hello", 1 }, { "HelloAck", 2 }, { "Reject", 3 }, { "Disconnect", 4 }, { "TimeSync", 5 },
            { "Roster", 6 }, { "WorldBegin", 7 }, { "WorldChunk", 8 }, { "WorldEnd", 9 },
            { "ClientWorldLoaded", 10 }, { "PlayerState", 11 }, { "BoatState", 12 }, { "EnvState", 13 },
            { "ControlRequest", 14 }, { "ControlState", 15 }, { "ItemManifest", 16 }, { "ItemAction", 17 },
            { "ItemState", 18 }, { "ItemMotion", 19 }, { "EconomyState", 20 }, { "EconomyDelta", 21 },
            { "TaskState", 22 }, { "JournalLine", 23 }, { "CargoOps", 24 }, { "CargoState", 25 },
            { "CargoLease", 26 }, { "AnimalState", 27 }, { "FishRecord", 28 }, { "RowboatState", 29 }, { "CrewOrder", 30 },
            { "CrewState", 31 }, { "CrewRoster", 32 }, { "ModData", 33 }, { "FastTravel", 34 },
            { "ShipPlans", 35 }, { "ShipLaunch", 36 }, { "MapChange", 37 }, { "AppearanceUpdate", 38 },
        };

        private static readonly Dictionary<string, int> FrozenControls = new Dictionary<string, int>
        {
            { "Steering", 1 }, { "Reef", 2 }, { "Rope", 3 }, { "Winch", 4 }, { "Door", 5 }, { "Lantern", 6 },
            { "Ballast", 7 }, { "Anchor", 8 }, { "Mooring", 9 }, { "Water", 10 }, { "Pump", 11 },
            { "Leak", 12 }, { "TrollWeight", 13 }, { "FishPoint", 14 }, { "Rowboat", 15 }, { "TrollLine", 16 }, { "CrewHelm", 17 },
        };

        // Lists a message does not shorten by itself: the code that sends them cuts them into
        // parts (ItemRegistry.SendManifest, WorldTransfer.HostSend).
        private static readonly HashSet<string> SenderCaps = new HashSet<string> { "ItemManifestMsg", "WorldChunkMsg" };

        private static int _failed, _checks;
        private static bool _verbose;
        private static readonly List<string> Warnings = new List<string>();

        private static int Main(string[] args)
        {
            _verbose = args.Contains("-v") || args.Contains("--verbose");
            Protocol.Warn = text => Warnings.Add(text);

            Section("numbers", Numbers);
            Section("registration", Registration);
            foreach (MsgType type in Enum.GetValues(typeof(MsgType)))
            {
                if (Protocol.Create(type) == null) continue;   // already reported by "registration"
                MsgType t = type;
                Section(t + ": there and back", () => RoundTrip(t));
                Section(t + ": as made", () => Defaults(t));
                Section(t + ": cut packets", () => Truncated(t));
                Section(t + ": long strings", () => LongStrings(t));
                Section(t + ": long lists", () => LongLists(t));
                Section(t + ": noise", () => Noise(t));
            }

            Section("LAN search: datagrams", LanDatagrams);
            Section("LAN search: one row per host", LanRows);
            Section("LAN search: broadcast addresses", LanBroadcast);
            Section("LAN search: a host on this PC is found", LanFound);

            Console.WriteLine();
            Console.WriteLine("Protocol " + Protocol.Version + ": " + _checks + " checks, " + _failed + " failed.");
            return _failed == 0 ? 0 : 1;
        }

        private static void Section(string name, Action body)
        {
            _checks++;
            try
            {
                body();
                if (_verbose) Console.WriteLine("ok    " + name);
            }
            catch (Exception error)
            {
                _failed++;
                string text = error is SmokeException ? error.Message : error.ToString();
                Console.WriteLine("FAIL  " + name + ": " + text);
            }
        }

        private sealed class SmokeException : Exception
        {
            public SmokeException(string message) : base(message) { }
        }

        private static void Fail(string message) => throw new SmokeException(message);

        // ---------------------------------------------------------------------------------------
        // Checks
        // ---------------------------------------------------------------------------------------

        private static NetDataReader Read(NetDataWriter w, int length = -1)
        {
            var bytes = new byte[length < 0 ? w.Length : length];
            Array.Copy(w.Data, bytes, bytes.Length);
            return new NetDataReader(bytes);
        }

        private static void LanDatagrams()
        {
            var query = new NetDataWriter();
            LanBeacon.WriteQuery(query);
            if (!LanBeacon.IsQuery(Read(query))) Fail("a query is not read as one");

            var reply = new NetDataWriter();
            LanBeacon.WriteReply(reply, new LanHost
            {
                SessionId = 77, ProtocolVersion = 10, ModVersion = "0.1.0", HostName = new string('n', 100),
                Players = 2, MaxPlayers = 4, Accepting = true,
            });
            if (LanBeacon.IsQuery(Read(reply))) Fail("a reply is read as a query");
            if (LanBeacon.TryReadReply(Read(query), out _)) Fail("a query is read as a reply");
            if (!LanBeacon.TryReadReply(Read(reply), out LanHost host)) Fail("a reply is not read");
            if (host.SessionId != 77 || host.ProtocolVersion != 10 || host.ModVersion != "0.1.0" || host.Players != 2 ||
                host.MaxPlayers != 4 || !host.Accepting) Fail("a reply's fields changed on the way");
            if (host.HostName.Length != Limits.Name) Fail("a long host name is not cut to " + Limits.Name);
            for (int length = 0; length < reply.Length; length++)
                if (LanBeacon.TryReadReply(Read(reply, length), out _)) Fail("a reply cut to " + length + " bytes is accepted");
            var noise = new Random(5);
            for (int i = 0; i < 2000; i++)
            {
                var bytes = new byte[noise.Next(0, 80)];
                noise.NextBytes(bytes);
                LanBeacon.TryReadReply(new NetDataReader(bytes), out _);   // must not throw
                LanBeacon.IsQuery(new NetDataReader(bytes));
            }
        }

        private static void LanRows()
        {
            var list = new LanHostList();
            list.Seen(new LanHost { SessionId = 1, HostName = "b" }, "192.168.0.40", 7777, 0f);
            list.Seen(new LanHost { SessionId = 1, HostName = "b" }, "172.18.0.1", 7777, 0.1f);
            list.Seen(new LanHost { SessionId = 2, HostName = "a" }, "192.168.0.41", 7777, 0.1f);
            LanHost[] rows = list.Snapshot();
            if (rows.Length != 2) Fail("two hosts give " + rows.Length + " rows");
            if (rows[0].HostName != "a") Fail("the rows are not in the order of names");
            if (rows[1].Address != "192.168.0.40") Fail("a host's address changed while the first one still answers");
            list.Seen(new LanHost { SessionId = 1, HostName = "b" }, "127.0.0.1", 7777, 0.2f);
            if (list.Snapshot()[1].Address != "127.0.0.1") Fail("a host on this PC is not shown by its loopback address");
            list.Expire(LanHostList.Lifetime + 0.15f);
            if (list.Count != 1) Fail("a host that stopped answering is still listed");
        }

        private static void LanBroadcast()
        {
            string Of(string address, string mask)
            {
                IPAddress directed = LanSearch.Directed(IPAddress.Parse(address), IPAddress.Parse(mask));
                return directed == null ? "none" : directed.ToString();
            }
            if (Of("192.168.0.40", "255.255.255.0") != "192.168.0.255") Fail("/24: " + Of("192.168.0.40", "255.255.255.0"));
            if (Of("172.18.0.1", "255.255.255.252") != "172.18.0.3") Fail("/30: " + Of("172.18.0.1", "255.255.255.252"));
            if (Of("10.1.2.3", "255.0.0.0") != "10.255.255.255") Fail("/8: " + Of("10.1.2.3", "255.0.0.0"));
            if (Of("10.1.2.3", "255.255.255.255") != "none") Fail("/32 has a broadcast address");
            if (Of("10.1.2.3", "0.0.0.0") != "none") Fail("an empty mask has a broadcast address");
        }

        // The real thing, over this PC's own network stack: a host and a search side by side.
        private static void LanFound()
        {
            const int port = 47811;
            bool world = false;
            var host = new CoopNet(null) { PlayerName = "Smoke host", MaxClients = 3, JoinBlocker = () => world ? null : "no world" };
            var search = new LanSearch(null);
            try
            {
                host.StartHost(port);
                if (!host.IsHost) Fail("the host did not start on port " + port + ": " + host.LastError);
                LanHost[] found = Look(host, search, port, rows => rows.Length > 0);
                if (found.Length != 1) Fail("found " + found.Length + " hosts, expected 1");
                if (found[0].HostName != "Smoke host" || found[0].Players != 1 || found[0].MaxPlayers != 4 ||
                    found[0].ProtocolVersion != Protocol.Version || found[0].Port != port)
                    Fail("the host is described wrongly: " + found[0].HostName + " " + found[0].Players + "/" + found[0].MaxPlayers +
                         " protocol " + found[0].ProtocolVersion + " port " + found[0].Port);
                if (found[0].Accepting) Fail("a host with no world says it lets guests in");
                world = true;
                found = Look(host, search, port, rows => rows.Length > 0 && rows[0].Accepting);
                if (found.Length != 1 || !found[0].Accepting) Fail("a host with a world does not say it lets guests in");

                host.AnnounceOnLan = false;
                host.StartHost(port);
                search.Stop();
                found = Look(host, search, port, rows => false);
                if (found.Length != 0) Fail("a host that does not announce itself was found");
            }
            finally
            {
                search.Stop();
                host.Stop();
            }
        }

        private static LanHost[] Look(CoopNet host, LanSearch search, int port, Func<LanHost[], bool> enough)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            LanHost[] found = new LanHost[0];
            while (clock.Elapsed.TotalSeconds < 2.5)
            {
                host.PollEvents();
                search.Tick(true, (float)clock.Elapsed.TotalSeconds, port);
                found = search.Hosts;
                if (enough(found)) break;
                System.Threading.Thread.Sleep(20);
            }
            return found;
        }

        private static void Numbers()
        {
            Frozen(typeof(MsgType), FrozenTypes);
            Frozen(typeof(ControlKind), FrozenControls);
        }

        private static void Frozen(Type type, Dictionary<string, int> frozen)
        {
            var problems = new List<string>();
            foreach (object value in Enum.GetValues(type))
            {
                string name = value.ToString();
                int number = Convert.ToInt32(value);
                if (!frozen.TryGetValue(name, out int was))
                    problems.Add(name + " = " + number + " is new: add it to the table in Tools/ProtocolSmoke/Program.cs");
                else if (was != number)
                    problems.Add(name + " was " + was + " and is " + number + ": a wire number must never change");
            }
            foreach (var pair in frozen)
                if (!Enum.GetNames(type).Contains(pair.Key))
                    problems.Add(pair.Key + " = " + pair.Value + " is gone");
            var seen = new HashSet<int>();
            foreach (object value in Enum.GetValues(type))
                if (!seen.Add(Convert.ToInt32(value))) problems.Add("the number " + Convert.ToInt32(value) + " is used twice");
            if (problems.Count > 0) Fail(type.Name + ": " + string.Join("; ", problems));
        }

        private static void Registration()
        {
            var problems = new List<string>();
            var made = new HashSet<Type>();
            foreach (MsgType type in Enum.GetValues(typeof(MsgType)))
            {
                INetMessage msg = Protocol.Create(type);
                if (msg == null) { problems.Add(type + " has no case in Protocol.Create"); continue; }
                if (msg.Type != type) problems.Add("Protocol.Create(" + type + ") gives a " + msg.GetType().Name + " whose Type is " + msg.Type);
                made.Add(msg.GetType());
                if (!MessageDirection.FromClient(type) && !MessageDirection.FromHost(type))
                    problems.Add(type + " is in neither list of MessageDirection: no side would take it");
            }
            foreach (Type type in typeof(Protocol).Assembly.GetTypes())
                if (typeof(INetMessage).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface && !made.Contains(type))
                    problems.Add(type.Name + " is made by no case of Protocol.Create");
            if (problems.Count > 0) Fail(string.Join("; ", problems));
        }

        private static void RoundTrip(MsgType type)
        {
            INetMessage sent = Sample(type);
            byte[] packet = Pack(sent);
            INetMessage got = Unpack(packet, packet.Length, out int left);
            if (got == null) Fail("its own packet is not read back" + LastWarning());
            if (left != 0) Fail(left + " byte(s) left unread: Serialize writes more than Deserialize reads");
            string difference = Difference(sent, got, sent.GetType().Name);
            if (difference != null) Fail(difference);
        }

        private static void Defaults(MsgType type)
        {
            INetMessage sent = Protocol.Create(type);
            byte[] packet = Pack(sent);
            INetMessage got = Unpack(packet, packet.Length, out int left);
            // A message nobody filled may be one the reader refuses (a manifest with no parts);
            // what it must not do is throw while it is written, or be read back differently.
            if (got == null) return;
            if (left != 0) Fail(left + " byte(s) left unread");
            string difference = Difference(sent, got, sent.GetType().Name);
            if (difference != null) Fail(difference);
        }

        private static void Truncated(MsgType type)
        {
            byte[] packet = Pack(Sample(type));
            for (int length = 0; length < packet.Length; length++)
            {
                // A datagram that is really this short...
                var exact = new byte[length];
                Array.Copy(packet, exact, length);
                if (Unpack(exact, length, out _) != null)
                    Fail("a packet cut to " + length + " of " + packet.Length + " bytes is taken as whole");
                // ...and one whose buffer still holds the bytes of an older, longer packet.
                if (Unpack(packet, length, out _) != null)
                    Fail("a packet cut to " + length + " of " + packet.Length + " bytes is read on into stale bytes");
            }
        }

        private static void LongStrings(MsgType type)
        {
            INetMessage sent = Sample(type);
            var strings = new List<string>();
            Walk(sent, sent.GetType().Name, (path, field, owner) =>
            {
                if (field.FieldType != typeof(string)) return;
                field.SetValue(owner, new string('x', 5000));
                strings.Add(path);
            });
            if (strings.Count == 0) return;
            byte[] packet = Pack(sent);
            INetMessage got = Unpack(packet, packet.Length, out int left);
            if (got == null) Fail("a long string makes the whole message unreadable" + LastWarning());
            if (left != 0) Fail(left + " byte(s) left unread with long strings");
            var lost = new List<string>();
            Walk(got, got.GetType().Name, (path, field, owner) =>
            {
                if (field.FieldType != typeof(string)) return;
                string value = (string)field.GetValue(owner);
                if (string.IsNullOrEmpty(value)) lost.Add(path);
                else if (_verbose && value.Length < 5000) Console.WriteLine("      " + path + " is cut at " + value.Length);
            });
            if (lost.Count > 0) Fail("a long string arrives empty instead of cut: " + string.Join(", ", lost.Distinct()));
        }

        private static void LongLists(MsgType type)
        {
            INetMessage sent = Sample(type);
            int n = 1;
            bool any = false;
            foreach (FieldInfo field in Fields(sent.GetType()))
            {
                if (!field.FieldType.IsArray) continue;
                Array was = (Array)field.GetValue(sent);
                // A list of a fixed length (the seven stores) is not a list of "how many".
                if (was != null && IsFixed(Protocol.Create(type), field)) continue;
                Type element = field.FieldType.GetElementType();
                Array big = Array.CreateInstance(element, 5000);
                for (int i = 0; i < big.Length; i++) big.SetValue(Fill(element, null, ref n), i);
                field.SetValue(sent, big);
                any = true;
            }
            if (!any) return;
            byte[] packet;
            try { packet = Pack(sent); }
            catch (Exception error)
            {
                if (SenderCaps.Contains(sent.GetType().Name)) return;
                Fail("a long list cannot be written: " + error.Message);
                return;
            }
            INetMessage got = Unpack(packet, packet.Length, out int left);
            if (got == null || left != 0)
            {
                if (SenderCaps.Contains(sent.GetType().Name)) return;
                Fail(got == null
                    ? "a long list makes the message unreadable: Serialize must cut it to what Deserialize takes" + LastWarning()
                    : left + " byte(s) left unread with a long list");
            }
            foreach (FieldInfo field in Fields(sent.GetType()))
            {
                if (!field.FieldType.IsArray) continue;
                Array a = (Array)field.GetValue(sent), b = (Array)field.GetValue(got);
                if (b == null) Fail(field.Name + " is null after a long list");
                if (b.Length > a.Length) Fail(field.Name + " grew on the way");
                for (int i = 0; i < b.Length; i++)
                {
                    string difference = Difference(a.GetValue(i), b.GetValue(i), field.Name + "[" + i + "]");
                    if (difference != null) Fail("a long list is not cut to its beginning: " + difference);
                }
                if (_verbose && b.Length < a.Length) Console.WriteLine("      " + field.Name + " is cut at " + b.Length);
            }
        }

        private static bool IsFixed(INetMessage fresh, FieldInfo field)
        {
            Array made = (Array)field.GetValue(fresh);
            return made != null && made.Length > 0;
        }

        private static void Noise(MsgType type)
        {
            var random = new Random(1000 + (int)type);
            for (int i = 0; i < 3000; i++)
            {
                var packet = new byte[1 + random.Next(0, 300)];
                random.NextBytes(packet);
                packet[0] = (byte)type;
                try { Unpack(packet, packet.Length, out _); }
                catch (Exception error) { Fail("noise is not dropped quietly: " + error.GetType().Name + ": " + error.Message); }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Packets
        // ---------------------------------------------------------------------------------------

        private static byte[] Pack(INetMessage msg)
        {
            NetDataWriter writer = Protocol.Write(msg);
            return writer.CopyData();
        }

        private static INetMessage Unpack(byte[] buffer, int length, out int left)
        {
            var reader = new NetDataReader(buffer, 0, length);
            INetMessage msg = Protocol.Read(reader, out _);
            left = reader.AvailableBytes;
            return msg;
        }

        private static string LastWarning() => Warnings.Count == 0 ? "" : " (" + Warnings[Warnings.Count - 1] + ")";

        // ---------------------------------------------------------------------------------------
        // Filling and comparing through reflection
        // ---------------------------------------------------------------------------------------

        private static INetMessage Sample(MsgType type)
        {
            INetMessage msg = Protocol.Create(type);
            int n = 1;
            foreach (FieldInfo field in Fields(msg.GetType()))
                field.SetValue(msg, Fill(field.FieldType, field.GetValue(msg), ref n));
            // The one message whose numbers must agree with each other to be read at all.
            if (msg is ItemManifestMsg manifest) { manifest.Part = 1; manifest.Parts = 3; }
            return msg;
        }

        private static IEnumerable<FieldInfo> Fields(Type type)
        {
            return type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                       .Where(field => !field.IsInitOnly && !field.IsLiteral);
        }

        private static object Fill(Type type, object existing, ref int n)
        {
            n++;
            if (type == typeof(string)) return "s" + n;
            if (type == typeof(bool)) return true;
            if (type == typeof(byte)) return (byte)(n % 200 + 1);
            if (type == typeof(ushort)) return (ushort)(n + 1000);
            if (type == typeof(short)) return (short)-(n + 1000);
            if (type == typeof(int)) return -(n + 100000);
            if (type == typeof(uint)) return (uint)(n + 3000000000u);
            if (type == typeof(long)) return (long)n + 5000000000L;
            if (type == typeof(float)) return n + 0.125f;
            if (type == typeof(double)) return n * 1000000.0 + 0.0625;
            if (type.IsEnum)
            {
                Array values = Enum.GetValues(type);
                return values.GetValue(values.Length - 1);
            }
            if (type.IsArray)
            {
                Type element = type.GetElementType();
                Array was = existing as Array;
                Array array = Array.CreateInstance(element, was != null && was.Length > 0 ? was.Length : 2);
                for (int i = 0; i < array.Length; i++) array.SetValue(Fill(element, null, ref n), i);
                return array;
            }
            if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                object value = existing ?? Activator.CreateInstance(type);
                foreach (FieldInfo field in Fields(type))
                    field.SetValue(value, Fill(field.FieldType, field.GetValue(value), ref n));
                return value;
            }
            throw new SmokeException("the smoke test cannot fill a " + type.Name + ": teach Fill about it");
        }

        private static void Walk(object value, string path, Action<string, FieldInfo, object> visit)
        {
            if (value == null) return;
            foreach (FieldInfo field in Fields(value.GetType()))
            {
                Type type = field.FieldType;
                string here = path + "." + field.Name;
                if (type == typeof(string)) { visit(here, field, value); continue; }
                if (type.IsArray)
                {
                    Array array = (Array)field.GetValue(value);
                    Type element = type.GetElementType();
                    if (array == null) continue;
                    if (element == typeof(string))
                    {
                        // No field to hand out for an element: lines are covered by their own check.
                        continue;
                    }
                    if (element.IsPrimitive || element.IsEnum) continue;
                    for (int i = 0; i < array.Length; i++)
                    {
                        object item = array.GetValue(i);
                        Walk(item, here + "[]", visit);
                        if (element.IsValueType) array.SetValue(item, i);   // a struct was changed in its box
                    }
                    continue;
                }
                if (type.IsPrimitive || type.IsEnum) continue;
                object inner = field.GetValue(value);
                Walk(inner, here, visit);
                if (type.IsValueType) field.SetValue(value, inner);
            }
        }

        /// <summary>Null when equal, otherwise where the first difference is.</summary>
        private static string Difference(object a, object b, string path)
        {
            if (a == null && b == null) return null;
            if (a == null || b == null) return path + ": " + Show(a) + " became " + Show(b);
            Type type = a.GetType();
            if (type != b.GetType()) return path + ": a " + type.Name + " became a " + b.GetType().Name;
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum)
                return a.Equals(b) ? null : path + ": " + Show(a) + " became " + Show(b);
            if (type.IsArray)
            {
                Array x = (Array)a, y = (Array)b;
                if (x.Length != y.Length) return path + ": " + x.Length + " entries became " + y.Length;
                for (int i = 0; i < x.Length; i++)
                {
                    string inner = Difference(x.GetValue(i), y.GetValue(i), path + "[" + i + "]");
                    if (inner != null) return inner;
                }
                return null;
            }
            foreach (FieldInfo field in Fields(type))
            {
                string inner = Difference(field.GetValue(a), field.GetValue(b), path + "." + field.Name);
                if (inner != null) return inner;
            }
            return null;
        }

        private static string Show(object value)
        {
            if (value == null) return "null";
            string text = value.ToString();
            return value is string ? "\"" + (text.Length > 40 ? text.Substring(0, 40) + "..." : text) + "\"" : text;
        }
    }
}
