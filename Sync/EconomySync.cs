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
    /// (<c>ProvisionsController</c>), debts and the journal. The host's numbers are the truth.
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
    internal sealed class EconomySync
    {
        private const float Period = 0.25f;
        private const float MoneyStep = 0.005f;
        private const float StoreStep = 0.005f;

        private sealed class Pending
        {
            public uint Seq;
            public float Money;
            public float[] Stores;
            public bool HasDebts;
            public int Fish;
        }

        private readonly CoopNet _net;
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly Dictionary<uint, uint> _acked = new Dictionary<uint, uint>();

        // The numbers as last seen here: a difference from them is the local game's doing.
        private float _money;
        private readonly float[] _stores = new float[EconomyWire.Stores];
        private bool _hasStores;
        private string _debts = "";
        private int _logs;
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

        private static string Join(List<string> lines) => lines == null ? "" : string.Join("\n", lines);

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
                _net.SendToHost(new EconomyDeltaMsg { Seq = ++_seq, Resync = true }, DeliveryMethod.ReliableOrdered);
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
            _debts = Join(data.debts);
            _logs = data.logs != null ? data.logs.Count : 0;
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

            string debts = Join(data.debts);
            if (debts != _debts)
            {
                _debts = debts;
                delta.HasDebts = true;
                delta.Debts = data.debts.ToArray();
                changed = true;
            }

            if (data.logs != null)
            {
                if (data.logs.Count < _logs) _logs = data.logs.Count;
                for (; _logs < data.logs.Count; _logs++)
                {
                    var line = new JournalLineMsg { Line = data.logs[_logs] };
                    if (_net.IsHost) _net.Broadcast(line, DeliveryMethod.ReliableOrdered);
                    else _net.SendToHost(line, DeliveryMethod.ReliableOrdered);
                }
            }

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
            _pending.Add(new Pending { Seq = delta.Seq, Money = delta.Money, Stores = delta.Stores, HasDebts = delta.HasDebts, Fish = delta.Fish });
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
            msg.HasFish = _hasFish; msg.Fish = _fish;
            msg.Debts = _debts.Length == 0 ? Array.Empty<string>() : _debts.Split('\n');
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
            if (!_net.IsClient || _dp == null) return;
            _net.SendToHost(new EconomyDeltaMsg { Seq = ++_seq, Resync = true }, DeliveryMethod.ReliableOrdered);
        }

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
            if (msg.HasDebts)
            {
                data.debts.Clear();
                data.debts.AddRange(msg.Debts);
                _debts = Join(data.debts);
            }
            _acked[player] = msg.Seq;
            SendState();
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
            bool ownDebts = false;
            int fish = msg.Fish;
            foreach (Pending p in _pending)
            {
                money += p.Money;
                for (int i = 0; i < stores.Length; i++) stores[i] = Mathf.Max(0f, stores[i] + p.Stores[i]);
                ownDebts |= p.HasDebts;
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
            if (!ownDebts)
            {
                data.debts.Clear();
                data.debts.AddRange(msg.Debts);
                _debts = Join(data.debts);
            }
        }

        // -----------------------------------------------------------------
        // Journal
        // -----------------------------------------------------------------

        public void OnJournal(JournalLineMsg msg, NetPeer from)
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null || dp.gameData == null || dp.gameData.logs == null) return;
            // The local lines first, so this one is not sent back as a local line.
            Look(dp);
            dp.gameData.logs.Add(msg.Line);
            _logs = dp.gameData.logs.Count;
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
