using System;
using System.Collections.Generic;
using System.Globalization;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// One household for the crew: money (<c>GameData.tearCount</c>), the ship's seven stores
    /// (<c>ProvisionsController</c>), the same seven ashore (<c>GameData.storedProvisions</c>), debts
    /// and the journal. The host's numbers are the truth.
    ///
    /// The game writes these from many places — a trade dialog assigns the stores directly, a
    /// payment dialog assigns the money — so nothing is intercepted: four times a second each copy
    /// compares the numbers with what it last knew. What a guest's own game changed goes to the
    /// host as a difference, numbered; the host adds it to its own numbers and sends everybody the
    /// result, telling each guest the number of its last difference that is in it. A guest shows
    /// the host's numbers plus its own differences the host has not counted yet, so a purchase is
    /// seen at once and is counted once.
    ///
    /// The stores are eaten by the host's clock only: a guest's own timer is switched off.
    /// </summary>
    internal sealed partial class EconomySync
    {
        private const float Period = 0.25f;
        private const float MoneyStep = 0.005f;
        private const float StoreStep = 0.005f;

        private sealed class Pending
        {
            public uint Seq;
            public float Money;
            public float[] Stores;
            public float[] Shore;
            public Dictionary<string, float> Debts;
            public int Fish;
        }

        private readonly CoopNet _net;
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly Dictionary<uint, uint> _acked = new Dictionary<uint, uint>();

        // The numbers as last seen here: a difference from them is the local game's doing.
        private float _money;
        private readonly float[] _stores = new float[EconomyWire.Stores];
        private bool _hasStores;
        private readonly float[] _shore = new float[EconomyWire.Stores];
        private Dictionary<string, float> _debts = new Dictionary<string, float>(StringComparer.Ordinal);
        private int _logs, _worldLogs;
        private bool _warnedLong;
        private DataPersistenceManager _dp;
        private float _nextPollAt;
        private uint _seq;
        private bool _synced;
        private ProvisionsController _controller;
        // The ship's fish hold, counted like a store; the catch records as last seen, by kind of fish.
        private FishCargoManager _fishHold;
        private int _fish;
        private bool _hasFish;
        private readonly Dictionary<string, string> _records = new Dictionary<string, string>(StringComparer.Ordinal);

        public int Sent { get; private set; }
        public int Received { get; private set; }

        /// <summary>This copy is a guest in a session: the patch reads it.</summary>
        private static bool s_guest;

        public EconomySync(CoopNet net) { _net = net; }

        public void Clear()
        {
            _pending.Clear(); _acked.Clear();
            _dp = null; _hasStores = false; _seq = 0; _synced = false; _controller = null;
            _fishHold = null; _hasFish = false; _fish = 0; _records.Clear();
            Sent = Received = 0;
            s_guest = false;
        }

        public void RemovePlayer(uint player) => _acked.Remove(player);

        // -----------------------------------------------------------------
        // Reading and writing the game's numbers
        // -----------------------------------------------------------------

        private static ProvisionsController Controller()
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            return ship != null ? ship.provisionController : null;
        }

        private static void Read(ProvisionsController c, float[] stores)
        {
            stores[0] = c.currentFood; stores[1] = c.currentDrink; stores[2] = c.currentOakum;
            stores[3] = c.currentTimber; stores[4] = c.currentCanvas; stores[5] = c.currentOil;
            stores[6] = c.currentAburae;
        }

        private static void Write(ProvisionsController c, float[] stores)
        {
            c.currentFood = stores[0]; c.currentDrink = stores[1]; c.currentOakum = stores[2];
            c.currentTimber = stores[3]; c.currentCanvas = stores[4]; c.currentOil = stores[5];
            c.currentAburae = stores[6];
            // The game keeps the sum in a field of its own and checks it before adding.
            float sum = 0f;
            for (int i = 0; i < EconomyWire.Stores; i++) sum += stores[i];
            c.currentProvisions = sum;
        }

        private static FishCargoManager FishHold()
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            return ship != null ? ship.GetComponent<FishCargoManager>() : null;
        }

        // The game can only add fish one by one, or count them all anew.
        private static void WriteFish(FishCargoManager hold, int count)
        {
            if (hold.currentFishCount == count) return;
            hold.RecalculateFish(count);
            ShipHullMover ship = hold.GetComponent<ShipHullMover>();
            if (ship != null) ship.UpdateCargoMass();
        }

        // The game writes a record as "id=weight". It sets the thread's culture to en-US itself, so
        // the weight has a point; a comma is read too, should that ever change.
        private static bool ParseRecord(string line, out string id, out float weight)
        {
            id = null; weight = 0f;
            int cut = line == null ? -1 : line.IndexOf('=');
            if (cut <= 0) return false;
            id = line.Substring(0, cut);
            string text = line.Substring(cut + 1);
            return float.TryParse(text, out weight) ||
                   float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out weight);
        }

        private static FishRecordMsg RecordMsg(string id, float weight) =>
            new FishRecordMsg { Line = id + "=" + weight.ToString("R", CultureInfo.InvariantCulture) };

        // The game's own names in GameData.storedProvisions, in the order of the stores.
        private static readonly string[] ShoreNames = { "Food", "Drink", "Oakum", "Timber", "Canvas", "Oil", "Aburae" };

        private static bool ParseNumber(string text, out float value) =>
            float.TryParse(text, out value) ||
            float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);

        // The store ashore: "Food=120" and so on; a store the list does not name is empty.
        private static void ReadShore(GameData data, float[] shore)
        {
            Array.Clear(shore, 0, shore.Length);
            if (data.storedProvisions == null) return;
            foreach (string line in data.storedProvisions)
            {
                int cut = line == null ? -1 : line.IndexOf('=');
                if (cut <= 0) continue;
                int i = Array.IndexOf(ShoreNames, line.Substring(0, cut));
                if (i >= 0 && ParseNumber(line.Substring(cut + 1), out float value)) shore[i] = value;
            }
        }

        private static void WriteShore(GameData data, float[] shore)
        {
            var now = new float[EconomyWire.Stores];
            ReadShore(data, now);
            bool same = true;
            for (int i = 0; i < now.Length; i++) same &= Mathf.Abs(now[i] - shore[i]) <= StoreStep;
            if (same) return;
            var lines = new List<string>();
            for (int i = 0; i < ShoreNames.Length; i++) lines.Add(ShoreNames[i] + "=" + Number(shore[i]));
            data.storedProvisions = lines;
        }

        // Debts: "creditor=amount", one line per creditor.
        private static Dictionary<string, float> ReadDebts(IEnumerable<string> lines)
        {
            var debts = new Dictionary<string, float>(StringComparer.Ordinal);
            if (lines == null) return debts;
            foreach (string line in lines)
            {
                int cut = line == null ? -1 : line.LastIndexOf('=');
                if (cut <= 0 || !ParseNumber(line.Substring(cut + 1), out float amount)) continue;
                string creditor = line.Substring(0, cut);
                debts[creditor] = debts.TryGetValue(creditor, out float more) ? more + amount : amount;
            }
            return debts;
        }

        // The game's lines in their order, changed where the amounts differ; a paid debt goes.
        private static void WriteDebts(GameData data, Dictionary<string, float> debts)
        {
            if (data.debts == null) data.debts = new List<string>();
            Dictionary<string, float> now = ReadDebts(data.debts);
            bool same = now.Count == debts.Count;
            foreach (KeyValuePair<string, float> debt in debts)
                same &= now.TryGetValue(debt.Key, out float amount) && Mathf.Abs(amount - debt.Value) <= MoneyStep;
            if (same) return;
            var lines = new List<string>();
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in data.debts)
            {
                int cut = line == null ? -1 : line.LastIndexOf('=');
                string creditor = cut > 0 ? line.Substring(0, cut) : null;
                if (creditor == null || !debts.ContainsKey(creditor) || !written.Add(creditor)) continue;
                lines.Add(creditor + "=" + Number(debts[creditor]));
            }
            foreach (KeyValuePair<string, float> debt in debts)
                if (written.Add(debt.Key)) lines.Add(debt.Key + "=" + Number(debt.Value));
            data.debts.Clear();
            data.debts.AddRange(lines);
        }

        private static void AddDebts(Dictionary<string, float> debts, Dictionary<string, float> change)
        {
            foreach (KeyValuePair<string, float> c in change)
            {
                float amount = (debts.TryGetValue(c.Key, out float known) ? known : 0f) + c.Value;
                if (amount > MoneyStep) debts[c.Key] = amount; else debts.Remove(c.Key);
            }
        }

        private static string[] DebtLines(Dictionary<string, float> debts)
        {
            var lines = new List<string>();
            foreach (KeyValuePair<string, float> debt in debts) lines.Add(debt.Key + "=" + Number(debt.Value));
            return lines.ToArray();
        }

        // -----------------------------------------------------------------
        // Loop
        // -----------------------------------------------------------------

        /// <summary>Every frame for a participant.</summary>
        public void Tick()
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null || dp.gameData == null) return;
            s_guest = _net.IsClient;
            if (dp != _dp) { Rebase(dp); return; }
            if (Time.unscaledTime < _nextPollAt) return;
            _nextPollAt = Time.unscaledTime + Period;
            Look(dp);
        }

        // A new world: what it holds is the starting point. A guest's world loaded again is the copy
        // it joined with, so it asks the host for the household as it is now.
        private void Rebase(DataPersistenceManager dp)
        {
            // A guest reports nothing until the host's numbers have come: what a world shows while it
            // is still being put together must never reach the host as this player's doing.
            _synced = !_net.IsClient;
            _dp = dp;
            _pending.Clear();
            Base(dp);
            if (_net.IsClient)
                _net.SendToHost(ResyncMsg(dp.gameData), DeliveryMethod.ReliableOrdered);
        }

        private void Base(DataPersistenceManager dp)
        {
            GameData data = dp.gameData;
            if (data.debts == null) data.debts = new List<string>();
            _money = data.tearCount;
            ProvisionsController c = Controller();
            _hasStores = c != null;
            _controller = c;
            if (c != null) Read(c, _stores);
            ReadShore(data, _shore);
            _debts = ReadDebts(data.debts);
            _logs = data.logs != null ? data.logs.Count : 0;
            _worldLogs = data.worldLogs != null ? data.worldLogs.Count : 0;
            FishCargoManager hold = FishHold();
            _hasFish = hold != null;
            _fishHold = hold;
            if (hold != null) _fish = hold.currentFishCount;
            _records.Clear();
            if (data.fishRecords != null)
                foreach (string line in data.fishRecords)
                    if (ParseRecord(line, out string id, out _)) _records[id] = line;
        }

        // Before anything from the wire is written: the local game's changes are taken first, so they
        // are neither lost under the incoming numbers nor taken for them.
        private void Look(DataPersistenceManager dp)
        {
            if (dp == _dp) Poll(dp); else Rebase(dp);
        }

        // What the local game changed since the last look.
        private void Poll(DataPersistenceManager dp)
        {
            GameData data = dp.gameData;
            if (data.debts == null) data.debts = new List<string>();
            if (!_synced) { Base(dp); return; }
            bool changed = false;
            var delta = new EconomyDeltaMsg();

            float money = data.tearCount;
            if (Mathf.Abs(money - _money) > MoneyStep) { delta.Money = money - _money; _money = money; changed = true; }

            ProvisionsController c = Controller();
            if (c != null)
            {
                var now = new float[EconomyWire.Stores];
                Read(c, now);
                // Another ship (or the first one): its stores are a new starting point, not a change.
                if (!_hasStores || c != _controller)
                {
                    Array.Copy(now, _stores, now.Length);
                    changed |= _hasStores && _net.IsHost;
                    _hasStores = true; _controller = c;
                }
                for (int i = 0; i < now.Length; i++)
                    if (Mathf.Abs(now[i] - _stores[i]) > StoreStep)
                    {
                        delta.Stores[i] = now[i] - _stores[i];
                        _stores[i] = now[i];
                        changed = true;
                    }
            }

            FishCargoManager hold = FishHold();
            if (hold != null)
            {
                int fish = hold.currentFishCount;
                if (!_hasFish || hold != _fishHold)
                {
                    _fish = fish;
                    changed |= _hasFish && _net.IsHost;
                    _hasFish = true; _fishHold = hold;
                }
                if (fish != _fish) { delta.Fish = fish - _fish; _fish = fish; changed = true; }
            }

            var shore = new float[EconomyWire.Stores];
            ReadShore(data, shore);
            for (int i = 0; i < shore.Length; i++)
                if (Mathf.Abs(shore[i] - _shore[i]) > StoreStep)
                {
                    delta.Shore[i] = shore[i] - _shore[i];
                    _shore[i] = shore[i];
                    changed = true;
                }

            Dictionary<string, float> debts = ReadDebts(data.debts);
            var debtChange = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, float> debt in debts)
            {
                float d = debt.Value - (_debts.TryGetValue(debt.Key, out float known) ? known : 0f);
                if (Mathf.Abs(d) > MoneyStep) debtChange[debt.Key] = d;
            }
            foreach (KeyValuePair<string, float> debt in _debts)
                if (!debts.ContainsKey(debt.Key)) debtChange[debt.Key] = -debt.Value;
            if (debtChange.Count > 0)
            {
                _debts = debts;
                delta.Debts = DebtLines(debtChange);
                changed = true;
            }

            SendNewLines(data.logs, ref _logs, false);
            SendNewLines(data.worldLogs, ref _worldLogs, true);

            if (data.fishRecords != null)
                foreach (string line in data.fishRecords)
                {
                    if (!ParseRecord(line, out string id, out float weight)) continue;
                    if (_records.TryGetValue(id, out string known) && known == line) continue;
                    _records[id] = line;
                    if (_net.IsHost) _net.Broadcast(RecordMsg(id, weight), DeliveryMethod.ReliableOrdered);
                    else _net.SendToHost(RecordMsg(id, weight), DeliveryMethod.ReliableOrdered);
                }

            if (!changed) return;
            if (_net.IsHost) { SendState(); return; }
            delta.Seq = ++_seq;
            _pending.Add(new Pending { Seq = delta.Seq, Money = delta.Money, Stores = delta.Stores, Shore = delta.Shore, Debts = debtChange, Fish = delta.Fish });
            _net.SendToHost(delta, DeliveryMethod.ReliableOrdered);
            Sent++;
        }

        // -----------------------------------------------------------------
        // Host
        // -----------------------------------------------------------------

        private EconomyStateMsg State(uint player)
        {
            _acked.TryGetValue(player, out uint acked);
            var msg = new EconomyStateMsg { Acked = acked, Money = _money, HasStores = _hasStores };
            Array.Copy(_stores, msg.Stores, _stores.Length);
            Array.Copy(_shore, msg.Shore, _shore.Length);
            msg.HasFish = _hasFish; msg.Fish = _fish;
            msg.Debts = DebtLines(_debts);
            return msg;
        }

        private void SendState()
        {
            foreach (SessionMemberInfo member in _net.RosterSnapshot)
                if (member.NetId != _net.MyNetId && member.State == MemberJoinState.Ready)
                    _net.SendTo(member.NetId, State(member.NetId), DeliveryMethod.ReliableOrdered);
            Sent++;
        }

        /// <summary>Host: a guest came into the world; its copy of it is a few seconds old.</summary>
        public void SendAll(uint player)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (!_net.IsHost || dp == null || dp.gameData == null) return;
            Look(dp);
            _net.SendTo(player, State(player), DeliveryMethod.ReliableOrdered);
            if (dp.gameData.fishRecords == null) return;
            foreach (string line in dp.gameData.fishRecords)
                if (ParseRecord(line, out string id, out float weight))
                    _net.SendTo(player, RecordMsg(id, weight), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Guest: asks the host for the household again, and with it the contracts, the animals and
        /// every control (the host answers a resync with all of them).
        /// </summary>
        public void AskAgain()
        {
            if (!_net.IsClient || _dp == null || _dp.gameData == null) return;
            _net.SendToHost(ResyncMsg(_dp.gameData), DeliveryMethod.ReliableOrdered);
        }

        private EconomyDeltaMsg ResyncMsg(GameData data) => new EconomyDeltaMsg
        {
            Seq = ++_seq, Resync = true,
            Logs = data.logs != null ? data.logs.Count : 0, WorldLogs = data.worldLogs != null ? data.worldLogs.Count : 0,
        };

        public void OnDelta(EconomyDeltaMsg msg, NetPeer from)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            uint player = _net.PlayerNetIdForPeer(from);
            if (!_net.IsHost || player == 0 || dp == null || dp.gameData == null) return;
            // The host's own changes first, so the guest's are not taken for the host's.
            Look(dp);
            Received++;

            GameData data = dp.gameData;
            if (data.debts == null) data.debts = new List<string>();
            data.tearCount += msg.Money;
            _money = data.tearCount;

            ProvisionsController c = Controller();
            if (c != null && _hasStores)
            {
                for (int i = 0; i < _stores.Length; i++) _stores[i] = Mathf.Max(0f, _stores[i] + msg.Stores[i]);
                Write(c, _stores);
            }
            FishCargoManager hold = FishHold();
            if (hold != null && _hasFish && msg.Fish != 0)
            {
                _fish = Math.Max(0, _fish + msg.Fish);
                WriteFish(hold, _fish);
            }
            bool shore = false;
            for (int i = 0; i < _shore.Length; i++)
                if (msg.Shore[i] != 0f) { _shore[i] = Mathf.Max(0f, _shore[i] + msg.Shore[i]); shore = true; }
            if (shore) WriteShore(data, _shore);
            if (msg.Debts.Length > 0)
            {
                AddDebts(_debts, ReadDebts(msg.Debts));
                WriteDebts(data, _debts);
            }
            _acked[player] = msg.Seq;
            SendState();
            // A world loaded again is the one the guest joined with: the lines written since are not in it.
            if (msg.Resync) SendJournal(player, data, msg.Logs, msg.WorldLogs);
        }

        // -----------------------------------------------------------------
        // Guest
        // -----------------------------------------------------------------

        public void OnState(EconomyStateMsg msg)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (!_net.IsClient || dp == null || dp.gameData == null) return;
            // What the local game did since the last look becomes a difference before the host's
            // numbers are written over it.
            Look(dp);
            Received++;
            _synced = true;
            _pending.RemoveAll(p => p.Seq <= msg.Acked);

            float money = msg.Money;
            var stores = new float[EconomyWire.Stores];
            Array.Copy(msg.Stores, stores, stores.Length);
            var shore = new float[EconomyWire.Stores];
            Array.Copy(msg.Shore, shore, shore.Length);
            Dictionary<string, float> debts = ReadDebts(msg.Debts);
            int fish = msg.Fish;
            foreach (Pending p in _pending)
            {
                money += p.Money;
                for (int i = 0; i < stores.Length; i++) stores[i] = Mathf.Max(0f, stores[i] + p.Stores[i]);
                for (int i = 0; i < shore.Length; i++) shore[i] = Mathf.Max(0f, shore[i] + p.Shore[i]);
                AddDebts(debts, p.Debts);
                fish = Math.Max(0, fish + p.Fish);
            }

            GameData data = dp.gameData;
            if (data.debts == null) data.debts = new List<string>();
            data.tearCount = money;
            _money = money;

            ProvisionsController c = Controller();
            if (c != null && msg.HasStores)
            {
                Write(c, stores);
                Array.Copy(stores, _stores, stores.Length);
                _hasStores = true;
            }
            FishCargoManager hold = FishHold();
            if (hold != null && msg.HasFish)
            {
                WriteFish(hold, fish);
                _fish = fish; _hasFish = true; _fishHold = hold;
            }
            WriteShore(data, shore);
            Array.Copy(shore, _shore, shore.Length);
            WriteDebts(data, debts);
            _debts = debts;
        }

        // -----------------------------------------------------------------
        // Journal
        // -----------------------------------------------------------------

        // The lines the local game added since the last look.
        private void SendNewLines(List<string> lines, ref int known, bool world)
        {
            if (lines == null) return;
            if (lines.Count < known) known = lines.Count;
            for (; known < lines.Count; known++)
            {
                if (!Fits(lines[known])) continue;
                var line = new JournalLineMsg { World = world, Line = lines[known] };
                if (_net.IsHost) _net.Broadcast(line, DeliveryMethod.ReliableOrdered);
                else _net.SendToHost(line, DeliveryMethod.ReliableOrdered);
            }
        }

        // A line cut short would be another line to the receiver: it is not sent at all.
        private bool Fits(string line)
        {
            if (line != null && line.Length <= JournalLineMsg.MaxLine) return true;
            if (!_warnedLong) Plugin.Log.LogWarning("[Economy] A journal line is longer than " + JournalLineMsg.MaxLine + " characters and is not shared");
            _warnedLong = true;
            return false;
        }

        // Host: a world loaded again is the one the guest joined with, whose lines begin the host's
        // lists too (lines are only ever added); the guest is sent the ones after them.
        private void SendJournal(uint player, GameData data, int logs, int worldLogs)
        {
            SendFrom(player, data.logs, logs, false);
            SendFrom(player, data.worldLogs, worldLogs, true);
        }

        private void SendFrom(uint player, List<string> lines, int from, bool world)
        {
            if (lines == null) return;
            for (int i = Math.Max(0, from); i < lines.Count; i++)
                if (Fits(lines[i])) _net.SendTo(player, new JournalLineMsg { World = world, Line = lines[i] }, DeliveryMethod.ReliableOrdered);
        }

        // The game itself knows a page of the world by its title: a note read twice is one page.
        private static bool HasLine(List<string> lines, string line, bool world)
        {
            if (!world) return lines.Contains(line);
            string title = Title(line);
            return lines.Exists(known => known != null && Title(known) == title);
        }

        private static string Title(string line)
        {
            int cut = line.IndexOf('=');
            return cut < 0 ? line : line.Substring(0, cut);
        }

        public void OnJournal(JournalLineMsg msg, NetPeer from)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null || dp.gameData == null) return;
            List<string> lines = msg.World ? dp.gameData.worldLogs : dp.gameData.logs;
            if (lines == null) return;
            // The local lines first, so this one is not sent back as a local line.
            Look(dp);
            if (HasLine(lines, msg.Line, msg.World)) return;
            lines.Add(msg.Line);
            if (msg.World) _worldLogs = lines.Count; else _logs = lines.Count;
            if (_net.IsHost)
                _net.Broadcast(msg, DeliveryMethod.ReliableOrdered, readyOnly: true, exceptNetId: _net.PlayerNetIdForPeer(from));
        }

        // -----------------------------------------------------------------
        // Fish records
        // -----------------------------------------------------------------

        public void OnRecord(FishRecordMsg msg, NetPeer from)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null || dp.gameData == null) return;
            if (!ParseRecord(msg.Line, out string id, out float weight)) return;
            // The local records first, so this one is not sent back as a local one.
            Look(dp);
            GameData data = dp.gameData;
            if (data.fishRecords == null) data.fishRecords = new List<string>();
            // As the game writes it.
            string line = id + "=" + weight;
            // The game itself replaces the line of a kind whenever it calls a catch a record, with
            // a lighter one too: the latest line is the record.
            int at = data.fishRecords.FindIndex(known => ParseRecord(known, out string knownId, out _) && knownId == id);
            if (at < 0) data.fishRecords.Add(line); else data.fishRecords[at] = line;
            _records[id] = line;
            if (_net.IsHost)
                _net.Broadcast(msg, DeliveryMethod.ReliableOrdered, readyOnly: true, exceptNetId: _net.PlayerNetIdForPeer(from));
        }

        /// <summary>Diagnostics: the household as this copy's game holds it right now.</summary>
        public void Dump(StateDump d)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null || dp.gameData == null) { d.Info("Household", "world", "none"); return; }
            GameData data = dp.gameData;
            d.Line("Household", "money", StateDump.F(data.tearCount), 0.01f);
            ProvisionsController c = Controller();
            if (c != null)
            {
                var stores = new float[EconomyWire.Stores];
                Read(c, stores);
                string[] names = { "food", "drink", "oakum", "timber", "canvas", "oil", "aburae" };
                for (int i = 0; i < stores.Length; i++) d.Line("Household", "store " + names[i], StateDump.F(stores[i]), 0.05f);
            }
            else d.Line("Household", "stores", "no ship");
            var shore = new float[EconomyWire.Stores];
            ReadShore(data, shore);
            for (int i = 0; i < shore.Length; i++) d.Line("Household", "ashore " + ShoreNames[i].ToLowerInvariant(), StateDump.F(shore[i]), 0.05f);
            FishCargoManager hold = FishHold();
            d.Line("Household", "fish in the hold", hold != null ? hold.currentFishCount.ToString() : "no hold");
            var debts = new List<string>(data.debts ?? new List<string>());
            debts.Sort(StringComparer.Ordinal);
            d.Line("Household", "debts", debts.Count.ToString());
            for (int i = 0; i < debts.Count; i++) d.Line("Household", "debt " + i, debts[i]);
            if (data.fishRecords != null)
                foreach (string line in data.fishRecords)
                    if (ParseRecord(line, out string id, out float weight)) d.Line("Household", "record " + id, StateDump.F(weight), 0.01f);
            d.Line("Household", "journal lines", (data.logs != null ? data.logs.Count : 0).ToString());
            d.Line("Household", "journal lines of the world", (data.worldLogs != null ? data.worldLogs.Count : 0).ToString());
            d.Info("Household", "differences not counted by the host yet", _pending.Count.ToString());
        }

        public string Describe()
        {
            return "money " + _money.ToString("F0") + "   sent " + Sent + "   received " + Received +
                   (_pending.Count > 0 ? "   waiting " + _pending.Count : "");
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ProvisionsController), "ConsumeProvisions")
                              ?? throw new MissingMethodException(nameof(ProvisionsController), "ConsumeProvisions"),
                          prefix: new HarmonyMethod(typeof(EconomySync), nameof(ConsumePrefix)));
        }

        // Every half hour of play the game takes food, drink and lamp oil from the stores, on each
        // copy by its own timer. A guest's stores are the host's, so a guest's timer takes nothing.
        private static bool ConsumePrefix(ProvisionsController __instance)
        {
            return PatchGuard.Prefix(() =>
            {
                if (!s_guest) return true;
                __instance.currentConsumptionInterval = 0f;
                return false;
            }, error => Plugin.Log.LogError("[Economy] " + error));
        }
    }
}
