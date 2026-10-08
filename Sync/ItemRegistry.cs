using System;
using System.Collections.Generic;
using System.Linq;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FollowingSeasCoop.Sync
{
    internal sealed class ItemRegistry
    {
        internal sealed class Entry
        {
            public ItemRecord Record;
            public Component Object;
        }

        private readonly CoopNet _net;
        private readonly Dictionary<uint, Entry> _ids = new Dictionary<uint, Entry>();
        private readonly Dictionary<string, Entry> _keys = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<Component, string> _objects = new Dictionary<Component, string>();
        private readonly List<ItemStateMsg> _pending = new List<ItemStateMsg>();
        private readonly Dictionary<uint, Dictionary<uint, string>> _cargoKeys =
            new Dictionary<uint, Dictionary<uint, string>>();
        private uint _nextId = 1, _generation, _incomingGeneration;
        private int _nextPart;
        private bool _complete, _active;
        private int _worldHandle;
        private float _scanAt;

        public ItemRegistry(CoopNet net) { _net = net; }
        public Entry Find(uint id) => _ids.TryGetValue(id, out Entry entry) ? entry : null;
        public Entry Find(string key) => _keys.TryGetValue(key, out Entry entry) ? entry : null;
        public Entry Find(Component obj) => _objects.TryGetValue(obj, out string key) ? Find(key) : null;

        public void Clear()
        {
            _ids.Clear(); _keys.Clear(); _objects.Clear(); _pending.Clear();
            _cargoKeys.Clear();
            _nextId = 1; _nextPart = 0; _complete = false; _active = false; _worldHandle = 0;
        }

        public void WorldUnloaded(Scene scene)
        {
            if (scene.buildIndex == GameAccess.WorldScene) Clear();
        }

        public void RemovePlayer(uint player) => _cargoKeys.Remove(player);

        public void Tick()
        {
            if (!GameAccess.InWorld) return;
            int handle = SceneManager.GetSceneByBuildIndex(GameAccess.WorldScene).handle;
            if (!_active || handle != _worldHandle)
            {
                Clear(); _active = true; _worldHandle = handle;
                Scan();
                if (_net.IsClient)
                    _net.SendToHost(new ItemActionMsg { Action = ItemActionKind.Manifest }, DeliveryMethod.ReliableOrdered);
            }
            if (Time.unscaledTime < _scanAt) return;
            _scanAt = Time.unscaledTime + 1f;
            foreach (Component obj in _objects.Keys.Where(obj => obj == null).ToArray()) _objects.Remove(obj);
            Scan();
        }

        private void Scan()
        {
            foreach (ObjectInteractionHandler item in Resources.FindObjectsOfTypeAll<ObjectInteractionHandler>())
                if (item != null && item.gameObject.scene.IsValid() && item.gameObject.scene.isLoaded)
                {
                    string key = ItemCatalog.KeyOf(item);
                    if (key != null) PortableStarted(item, key);
                }
            ShipHullMover ship = GameAccess.ActiveShip();
            if (ship == null) return;
            var boxes = (List<CargoPlacer>)AccessTools.Field(typeof(ShipHullMover), "cargoBoxes").GetValue(ship);
            int row = 0;
            for (int i = 0; i < boxes.Count; i++)
                foreach (Transform child in boxes[i].transform.parent)
                {
                    CargoDefinition cargo = child.GetComponent<CargoDefinition>();
                    if (cargo == null) continue;
                    string key = "cargo:" + ship.id + ":" + row++;
                    if (_objects.ContainsKey(cargo)) continue;
                    Register(cargo, new ItemRecord
                    {
                        Key = key, Kind = ItemKind.Cargo, ItemType = cargo.id,
                        Name = cargo.name, TaskId = cargo.taskID, Box = i, Status = ItemStatus.Secured
                    });
                }
        }

        public void CaptureCargoKeys(uint player)
        {
            Tick(); Scan();
            var keys = new Dictionary<uint, string>();
            ShipHullMover ship = GameAccess.ActiveShip();
            if (ship != null)
            {
                var boxes = (List<CargoPlacer>)AccessTools.Field(typeof(ShipHullMover), "cargoBoxes").GetValue(ship);
                int row = 0;
                foreach (CargoPlacer box in boxes)
                    foreach (Transform child in box.transform.parent)
                    {
                        CargoDefinition cargo = child.GetComponent<CargoDefinition>();
                        if (cargo == null) continue;
                        Entry entry = Find(cargo);
                        if (entry != null) keys[entry.Record.NetId] = "cargo:" + ship.id + ":" + row;
                        row++;
                    }
            }
            _cargoKeys[player] = keys;
        }

        public void PortableStarted(ObjectInteractionHandler item, string key)
        {
            if (!_active || _objects.ContainsKey(item) ||
                (bool)AccessTools.Field(typeof(ObjectInteractionHandler), "isFauna").GetValue(item)) return;
            Register(item, new ItemRecord
            {
                Key = key, Kind = ItemKind.Portable, Name = item.GetName(),
                ItemType = AccessTools.Field(typeof(ObjectInteractionHandler), "type").GetValue(item).ToString(),
                Status = item.isHeld ? ItemStatus.Held : ItemStatus.Free,
                Holder = item.isHeld ? _net.MyNetId : 0
            });
        }

        private void Register(Component obj, ItemRecord record)
        {
            _objects[obj] = record.Key;
            Entry existing = Find(record.Key);
            if (existing != null) { Bind(existing, obj); return; }
            if (!_net.IsHost) return;
            record.NetId = _nextId++; record.Revision = 1; record.Leader = _net.MyNetId;
            Capture(record, obj);
            var entry = new Entry { Record = record, Object = obj };
            _ids.Add(record.NetId, entry); _keys.Add(record.Key, entry);
            if (record.Kind == ItemKind.Portable)
                _net.Broadcast(new ItemStateMsg { Record = record }, DeliveryMethod.ReliableOrdered);
        }

        private static void Capture(ItemRecord record, Component obj)
        {
            CoordSpace.LocalToReal(obj.transform.position, out record.Pose.X, out record.Pose.Y, out record.Pose.Z);
            Quaternion q = obj.transform.rotation;
            record.Pose.Qx = q.x; record.Pose.Qy = q.y; record.Pose.Qz = q.z; record.Pose.Qw = q.w;
        }

        private static void Bind(Entry entry, Component obj)
        {
            entry.Object = obj;
            if (entry.Record.Status == ItemStatus.Deleted) UnityEngine.Object.Destroy(obj.gameObject);
        }

        public void SendManifest(uint player)
        {
            if (!_net.IsHost) return;
            Tick(); Scan();
            _cargoKeys.TryGetValue(player, out Dictionary<uint, string> cargoKeys);
            ItemRecord[] records = _ids.Values.Where(entry => entry.Record.Kind != ItemKind.Cargo ||
                (cargoKeys != null && cargoKeys.ContainsKey(entry.Record.NetId)))
                .OrderBy(entry => entry.Record.NetId).Select(entry =>
            {
                if (entry.Object != null) Capture(entry.Record, entry.Object);
                ItemRecord record = entry.Record;
                if (record.Kind != ItemKind.Cargo) return record;
                return new ItemRecord
                {
                    NetId = record.NetId, Key = cargoKeys[record.NetId], Kind = record.Kind,
                    ItemType = record.ItemType, Name = record.Name, TaskId = record.TaskId, Box = record.Box,
                    Revision = record.Revision, Status = record.Status, Holder = record.Holder,
                    Leader = record.Leader, Flags = record.Flags, Pose = record.Pose
                };
            }).ToArray();
            int parts = Math.Max(1, (records.Length + ItemManifestMsg.EntriesPerPart - 1) / ItemManifestMsg.EntriesPerPart);
            uint generation = ++_generation;
            for (int part = 0; part < parts; part++)
                _net.SendTo(player, new ItemManifestMsg
                {
                    Generation = generation, Part = part, Parts = parts,
                    Entries = records.Skip(part * ItemManifestMsg.EntriesPerPart).Take(ItemManifestMsg.EntriesPerPart).ToArray()
                }, DeliveryMethod.ReliableOrdered);
        }

        public void OnManifest(ItemManifestMsg msg)
        {
            if (!_net.IsClient) return;
            Tick();
            if (msg.Part == 0)
            {
                _ids.Clear(); _keys.Clear(); _complete = false;
                _incomingGeneration = msg.Generation; _nextPart = 0;
            }
            if (msg.Generation != _incomingGeneration || msg.Part != _nextPart)
                throw new InvalidOperationException("Item manifest out of order");
            foreach (ItemRecord record in msg.Entries) Apply(record);
            _nextPart++;
            if (_nextPart != msg.Parts) return;
            _complete = true;
            foreach (ItemStateMsg state in _pending) Apply(state.Record);
            _pending.Clear();
            Plugin.Log.LogInfo("[Items] Manifest bound: " + Describe());
        }

        public void OnState(ItemStateMsg msg)
        {
            if (!_net.IsClient) return;
            Tick();
            if (!_complete) { _pending.Add(msg); return; }
            Apply(msg.Record);
        }

        private void Apply(ItemRecord record)
        {
            Entry entry = Find(record.NetId);
            if (entry != null && entry.Record.Revision > record.Revision) return;
            if (entry == null) { entry = new Entry(); _ids.Add(record.NetId, entry); }
            entry.Record = record; _keys[record.Key] = entry;
            foreach (var pair in _objects)
                if (pair.Key != null && pair.Value == record.Key) { Bind(entry, pair.Key); break; }
        }

        public string Describe()
        {
            int deleted = _ids.Values.Count(entry => entry.Record.Status == ItemStatus.Deleted);
            int bound = _ids.Values.Count(entry => entry.Record.Status != ItemStatus.Deleted && entry.Object != null);
            int unmatched = _objects.Count(pair => pair.Key != null && !_keys.ContainsKey(pair.Value));
            return "bound " + bound + "   waiting " + (_ids.Count - bound - deleted) +
                   "   unmatched " + unmatched + "   deleted " + deleted +
                   (_net.IsClient && !_complete ? "   receiving manifest" : "");
        }
    }
}
