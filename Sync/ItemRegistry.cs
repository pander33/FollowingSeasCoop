using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        /// <summary>A guest with no manifest asks for one this long after it entered the world.</summary>
        private const float RequestAfter = 3f;
        private const float RequestRetry = 5f;

        private static readonly FieldInfo CargoBoxesField = AccessTools.Field(typeof(ShipHullMover), "cargoBoxes");
        private static readonly FieldInfo FaunaField = AccessTools.Field(typeof(ObjectInteractionHandler), "isFauna");
        private static readonly FieldInfo TypeField = AccessTools.Field(typeof(ObjectInteractionHandler), "type");

        private readonly CoopNet _net;
        private readonly Dictionary<uint, Entry> _ids = new Dictionary<uint, Entry>();
        private readonly Dictionary<string, Entry> _keys = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<Component, string> _objects = new Dictionary<Component, string>();
        // The one object that stands for a key. A second object with the same key stays local.
        private readonly Dictionary<string, Component> _owners = new Dictionary<string, Component>(StringComparer.Ordinal);
        private readonly HashSet<Component> _duplicates = new HashSet<Component>();
        private readonly List<ItemStateMsg> _pending = new List<ItemStateMsg>();
        private readonly Dictionary<uint, Dictionary<uint, string>> _cargoKeys =
            new Dictionary<uint, Dictionary<uint, string>>();
        private uint _nextId = 1, _generation, _incomingGeneration;
        private int _nextPart, _lateCargo;
        private bool _complete, _active, _receiving, _warnedFields, _warnedOrder;
        private int _worldHandle;
        private float _scanAt, _requestAt;
        // The ship whose cargo got its keys by save order. Crates that appear later get none.
        private ShipHullMover _cargoShip;

        public ItemRegistry(CoopNet net) { _net = net; }
        public Entry Find(uint id) => _ids.TryGetValue(id, out Entry entry) ? entry : null;
        public Entry Find(string key) => _keys.TryGetValue(key, out Entry entry) ? entry : null;
        public Entry Find(Component obj) => _objects.TryGetValue(obj, out string key) ? Find(key) : null;

        public void Clear()
        {
            _ids.Clear(); _keys.Clear(); _objects.Clear(); _owners.Clear(); _duplicates.Clear(); _pending.Clear();
            _cargoKeys.Clear();
            _nextId = 1; _nextPart = 0; _lateCargo = 0;
            _complete = false; _active = false; _receiving = false; _worldHandle = 0;
            _cargoShip = null;
        }

        public void WorldUnloaded(Scene scene)
        {
            if (scene.buildIndex == GameAccess.WorldScene) Clear();
        }

        public void RemovePlayer(uint player) => _cargoKeys.Remove(player);

        /// <summary>Every frame for a participant.</summary>
        public void Tick()
        {
            if (!EnsureWorld()) return;
            float now = Time.unscaledTime;
            // The host sends a manifest by itself when a guest becomes ready; this covers a guest
            // that loaded its world again, and a manifest that was cut short.
            if (_net.IsClient && !_complete && !_receiving && now >= _requestAt)
            {
                _requestAt = now + RequestRetry;
                _net.SendToHost(new ItemActionMsg { Action = ItemActionKind.Manifest }, DeliveryMethod.ReliableOrdered);
            }
            if (now < _scanAt) return;
            _scanAt = now + 1f;
            Scan();
        }

        // Binds the registry to the loaded world; a new world starts an empty registry.
        private bool EnsureWorld()
        {
            if (!GameAccess.InWorld) return false;
            int handle = SceneManager.GetSceneByBuildIndex(GameAccess.WorldScene).handle;
            if (_active && handle == _worldHandle) return true;
            Clear(); _active = true; _worldHandle = handle;
            _requestAt = Time.unscaledTime + RequestAfter;
            _scanAt = Time.unscaledTime + 1f;
            Scan();
            return true;
        }

        private static bool FieldsFound => CargoBoxesField != null && FaunaField != null && TypeField != null;

        private void Scan()
        {
            if (!FieldsFound)
            {
                if (!_warnedFields)
                {
                    _warnedFields = true;
                    Plugin.Log.LogWarning("[Items] The game's item fields were not found (a game update?); items are not registered");
                }
                return;
            }
            Forget();
            foreach (ObjectInteractionHandler item in Resources.FindObjectsOfTypeAll<ObjectInteractionHandler>())
                if (item != null && item.gameObject.scene.IsValid() && item.gameObject.scene.isLoaded)
                {
                    string key = ItemCatalog.KeyOf(item);
                    if (key != null) PortableStarted(item, key);
                }
            ScanCargo();
        }

        // Destroyed objects: an unloaded island, a used-up barrel, a removed crate.
        private void Forget()
        {
            foreach (Component obj in _objects.Keys.Where(obj => obj == null).ToArray()) _objects.Remove(obj);
            foreach (string key in _owners.Where(pair => pair.Value == null).Select(pair => pair.Key).ToArray())
                _owners.Remove(key);
            _duplicates.RemoveWhere(obj => obj == null);
        }

        private static List<CargoPlacer> BoxesOf(ShipHullMover ship) =>
            ship != null ? CargoBoxesField.GetValue(ship) as List<CargoPlacer> : null;

        // Crates in the order the game saves them: by box, then by place among the box's children.
        private static IEnumerable<KeyValuePair<int, CargoDefinition>> CargoOf(List<CargoPlacer> boxes)
        {
            for (int i = 0; i < boxes.Count; i++)
            {
                Transform parent = boxes[i] != null ? boxes[i].transform.parent : null;
                if (parent == null) continue;
                foreach (Transform child in parent)
                {
                    CargoDefinition cargo = child.GetComponent<CargoDefinition>();
                    if (cargo != null) yield return new KeyValuePair<int, CargoDefinition>(i, cargo);
                }
            }
        }

        private void ScanCargo()
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            List<CargoPlacer> boxes = BoxesOf(ship);
            if (boxes == null) return;
            // The row number is an identity only for the cargo the world was loaded with: both
            // copies built it from the same save. A row counted later would repeat a used key.
            bool seed = _cargoShip != ship;
            _cargoShip = ship;
            int row = 0;
            foreach (KeyValuePair<int, CargoDefinition> pair in CargoOf(boxes))
            {
                CargoDefinition cargo = pair.Value;
                int index = row++;
                if (_objects.ContainsKey(cargo) || _duplicates.Contains(cargo)) continue;
                if (!seed)
                {
                    // Placed during the session: shared cargo actions are a later stage.
                    _objects[cargo] = "cargo-late:" + _lateCargo++;
                    continue;
                }
                Register(cargo, new ItemRecord
                {
                    Key = "cargo:" + ship.id + ":" + index, Kind = ItemKind.Cargo, ItemType = cargo.id,
                    Name = cargo.name, TaskId = cargo.taskID, Box = pair.Key, Status = ItemStatus.Secured
                });
            }
        }

        public void CaptureCargoKeys(uint player)
        {
            if (!EnsureWorld()) return;
            Scan();
            var keys = new Dictionary<uint, string>();
            ShipHullMover ship = GameAccess.ActiveShip();
            List<CargoPlacer> boxes = FieldsFound ? BoxesOf(ship) : null;
            if (boxes != null)
            {
                int row = 0;
                foreach (KeyValuePair<int, CargoDefinition> pair in CargoOf(boxes))
                {
                    Entry entry = Find(pair.Value);
                    if (entry != null && entry.Object == pair.Value)
                        keys[entry.Record.NetId] = "cargo:" + ship.id + ":" + row;
                    row++;
                }
            }
            _cargoKeys[player] = keys;
        }

        public void PortableStarted(ObjectInteractionHandler item, string key)
        {
            if (!_active || !FieldsFound || _objects.ContainsKey(item) || _duplicates.Contains(item) ||
                (bool)FaunaField.GetValue(item)) return;
            Register(item, new ItemRecord
            {
                Key = key, Kind = ItemKind.Portable, Name = item.GetName(),
                ItemType = TypeField.GetValue(item).ToString(),
                Status = item.isHeld ? ItemStatus.Held : ItemStatus.Free,
                Holder = item.isHeld ? _net.MyNetId : 0
            });
        }

        private void Register(Component obj, ItemRecord record)
        {
            if (_owners.TryGetValue(record.Key, out Component owner) && owner != null && owner != obj)
            {
                // Two objects, one key: the first keeps the identity. Seen when an island is loaded
                // again while a thing taken from it is still on the ship.
                _duplicates.Add(obj);
                Plugin.Log.LogWarning("[Items] A second object has the key of '" + owner.name + "': " + record.Key);
                return;
            }
            _owners[record.Key] = obj;
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
            if (!_net.IsHost || !EnsureWorld()) return;
            Scan();
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
            if (!_net.IsClient || !EnsureWorld()) return;
            if (msg.Part == 0)
            {
                // Everything sent before a manifest is already in it.
                _ids.Clear(); _keys.Clear(); _pending.Clear(); _complete = false;
                _incomingGeneration = msg.Generation; _nextPart = 0; _receiving = true;
            }
            if (!_receiving || msg.Generation != _incomingGeneration || msg.Part != _nextPart)
            {
                // The rest of a manifest whose start belonged to a world this copy has left.
                _receiving = false;
                if (!_warnedOrder)
                {
                    _warnedOrder = true;
                    Plugin.Log.LogWarning("[Items] A manifest part came without its start; asking for the manifest again");
                }
                return;
            }
            foreach (ItemRecord record in msg.Entries) Apply(record);
            _nextPart++;
            if (_nextPart != msg.Parts) return;
            _complete = true; _receiving = false;
            foreach (ItemStateMsg state in _pending) Apply(state.Record);
            _pending.Clear();
            Plugin.Log.LogInfo("[Items] Manifest bound: " + Describe());
        }

        public void OnState(ItemStateMsg msg)
        {
            if (!_net.IsClient || !EnsureWorld()) return;
            if (!_complete) { _pending.Add(msg); return; }
            Apply(msg.Record);
        }

        private void Apply(ItemRecord record)
        {
            Entry entry = Find(record.NetId);
            if (entry != null && entry.Record.Revision > record.Revision) return;
            if (entry == null) { entry = new Entry(); _ids.Add(record.NetId, entry); }
            else if (entry.Record.Key != record.Key && Find(entry.Record.Key) == entry) _keys.Remove(entry.Record.Key);
            entry.Record = record; _keys[record.Key] = entry;
            if (_owners.TryGetValue(record.Key, out Component owner) && owner != null) Bind(entry, owner);
        }

        public string Describe()
        {
            if (PatchHealth.StateOf("Item catalog") != PatchHealthState.Ok) return "off: the Item catalog patch failed";
            if (!FieldsFound) return "off: the game's item fields were not found";
            int deleted = _ids.Values.Count(entry => entry.Record.Status == ItemStatus.Deleted);
            int bound = _ids.Values.Count(entry => entry.Record.Status != ItemStatus.Deleted && entry.Object != null);
            int unmatched = _objects.Count(pair => pair.Key != null && !_keys.ContainsKey(pair.Value));
            int duplicate = _duplicates.Count(obj => obj != null);
            return "bound " + bound + "   waiting " + (_ids.Count - bound - deleted) +
                   "   unmatched " + unmatched + "   duplicate " + duplicate + "   deleted " + deleted +
                   (_net.IsClient && !_complete ? "   receiving manifest" : "");
        }
    }
}
