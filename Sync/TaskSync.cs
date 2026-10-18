using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// One list of accepted contracts for the crew. The game keeps a contract as an object in the
    /// task panel (<c>TaskPanelHandler.acceptedTaskObjects</c>) and can write it as, and build it
    /// from, a save string — that string is what travels.
    ///
    /// Twice a second each copy compares its contracts with what it last knew: a new one, one whose
    /// numbers changed (loaded, delivered, quantity), one that was completed, one that is gone
    /// (abandoned). The copy whose game did it sends the string; the host relays a guest's. The
    /// receiver builds, updates, completes or removes its own object — it does not run the game's
    /// completion, so the reward, the debts and the journal line are counted once, on the copy
    /// that delivered, and reach the others as money, debts and a journal line.
    ///
    /// Offers on an island are each copy's own: a contract a guest accepts is unknown to the host
    /// until its string arrives. The three product tasks (wood, linen, iron) are fixed objects and
    /// carry numbers only.
    /// </summary>
    internal sealed class TaskSync
    {
        private const float Period = 0.5f;
        // Places in the game's save string.
        private const int IdAt = 12, CompleteAt = 13;

        private static readonly MethodInfo CreateTaskMethod = AccessTools.Method(typeof(TaskPanelHandler), "CreateTask");
        private static readonly FieldInfo AssignmentRunField = AccessTools.Field(typeof(TaskPanelHandler), "assignmentRun");

        private readonly CoopNet _net;
        // Contract id → its numbers as last seen here or as last written from the wire.
        private readonly Dictionary<string, string> _known = new Dictionary<string, string>(StringComparer.Ordinal);
        // Completed or abandoned: a late message must not bring one back.
        private readonly HashSet<string> _gone = new HashSet<string>(StringComparer.Ordinal);
        private readonly string[] _products = new string[4];
        private readonly List<TaskStateMsg> _waiting = new List<TaskStateMsg>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _lost = new List<string>();
        // Guest: the last strings it sent of each contract or product, and the ones whose latest
        // change here was its own — the host's copy of such a string is only its own coming back.
        private readonly Dictionary<string, List<string>> _sentData = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> _mine = new HashSet<string>(StringComparer.Ordinal);
        private TaskPanelHandler _panel;
        private float _nextPollAt;
        private bool _warned;

        public int Sent { get; private set; }
        public int Applied { get; private set; }

        public TaskSync(CoopNet net) { _net = net; }

        public void Clear()
        {
            _known.Clear(); _gone.Clear(); _waiting.Clear();
            _sentData.Clear(); _mine.Clear();
            Array.Clear(_products, 0, _products.Length);
            _panel = null;
            Sent = Applied = 0;
        }

        private static bool Usable => CreateTaskMethod != null && AssignmentRunField != null;

        // The panel builds its objects from the loaded strings in its first Update; before that
        // there is nothing to compare and nothing to add to.
        private static TaskPanelHandler Panel()
        {
            TaskPanelHandler panel = TaskPanelHandler.Instance;
            if (panel == null || panel.acceptedTaskObjects == null || panel.acceptedTasks == null) return null;
            return (bool)AssignmentRunField.GetValue(panel) ? panel : null;
        }

        // -----------------------------------------------------------------
        // Reading the game's tasks
        // -----------------------------------------------------------------

        private static string Numbers(TaskHandler task)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return task.quantity.ToString("R", c) + "|" + task.loaded.ToString("R", c) + "|" +
                   task.progress.ToString("R", c) + "|" + task.complete;
        }

        private static string IdOf(string data)
        {
            string[] parts = (data ?? "").Split('=');
            return parts.Length > IdAt ? parts[IdAt] : "";
        }

        private static bool CompleteIn(string data)
        {
            string[] parts = (data ?? "").Split('=');
            return parts.Length > CompleteAt && bool.TryParse(parts[CompleteAt], out bool complete) && complete;
        }

        private static GameObject Find(TaskPanelHandler panel, string id)
        {
            foreach (GameObject obj in panel.acceptedTaskObjects)
            {
                if (obj == null) continue;
                TaskHandler task = obj.GetComponent<TaskHandler>();
                if (task != null && !task.isProductType && task.id == id) return obj;
            }
            return null;
        }

        private static void DropString(List<string> list, string id)
        {
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
                if (IdOf(list[i]) == id) list.RemoveAt(i);
        }

        private static TaskHandler Product(TaskPanelHandler panel, TaskKind kind)
        {
            GameObject obj = kind == TaskKind.WoodProduct ? panel.woodProductTask
                           : kind == TaskKind.LinenProduct ? panel.linenProductTask
                           : kind == TaskKind.IronProduct ? panel.ironProductTask : null;
            return obj != null ? obj.GetComponent<TaskHandler>() : null;
        }

        // The game's own writers look up the task's destinations and ship; a contract whose
        // destination is not in the world makes them throw.
        private string Write(TaskHandler task, bool product)
        {
            try { return product ? task.GetSaveProductString() : task.GetSaveString(); }
            catch (Exception e)
            {
                if (!_warned) Plugin.Log.LogWarning("[Tasks] The game could not write task '" + task.id + "': " + e.Message);
                _warned = true;
                return null;
            }
        }

        // -----------------------------------------------------------------
        // Loop
        // -----------------------------------------------------------------

        /// <summary>Every frame for a participant.</summary>
        public void Tick()
        {
            if (!Usable || Time.unscaledTime < _nextPollAt) return;
            _nextPollAt = Time.unscaledTime + Period;
            TaskPanelHandler panel = Panel();
            if (panel == null) return;
            if (panel != _panel) { Base(panel); return; }
            Poll(panel);
        }

        // A new world: what it holds is the starting point, nothing is sent.
        private void Base(TaskPanelHandler panel)
        {
            _panel = panel;
            _known.Clear(); _gone.Clear();
            _sentData.Clear(); _mine.Clear();
            foreach (GameObject obj in panel.acceptedTaskObjects)
            {
                TaskHandler task = obj != null ? obj.GetComponent<TaskHandler>() : null;
                if (task != null && !task.isProductType) _known[task.id] = Numbers(task);
            }
            for (int k = 1; k < _products.Length; k++)
            {
                TaskHandler product = Product(panel, (TaskKind)k);
                _products[k] = product != null ? Numbers(product) : null;
            }
            foreach (TaskStateMsg msg in _waiting) Apply(panel, msg);
            _waiting.Clear();
        }

        private void Poll(TaskPanelHandler panel)
        {
            _seen.Clear();
            foreach (GameObject obj in panel.acceptedTaskObjects.ToArray())
            {
                TaskHandler task = obj != null ? obj.GetComponent<TaskHandler>() : null;
                if (task == null || task.isProductType) continue;
                string id = task.id;
                _seen.Add(id);
                if (_gone.Contains(id)) continue;
                string numbers = Numbers(task);
                if (_known.TryGetValue(id, out string before) && before == numbers) continue;
                string data = Write(task, false);
                // Completed here: the game removes the object in its next frame.
                if (task.complete) { _known.Remove(id); _gone.Add(id); }
                else _known[id] = numbers;
                if (data != null) Send(new TaskStateMsg { Kind = TaskKind.Contract, Id = id, Data = data });
            }

            _lost.Clear();
            foreach (string id in _known.Keys)
                if (!_seen.Contains(id)) _lost.Add(id);
            foreach (string id in _lost)
            {
                _known.Remove(id); _gone.Add(id);
                // Completed between two looks, or abandoned.
                string done = panel.completedTasks != null ? panel.completedTasks.FindLast(s => IdOf(s) == id) : null;
                Send(new TaskStateMsg { Kind = TaskKind.Contract, Id = id, Data = done ?? "", Deleted = done == null });
            }

            for (int k = 1; k < _products.Length; k++)
            {
                TaskHandler product = Product(panel, (TaskKind)k);
                if (product == null) continue;
                string numbers = Numbers(product);
                if (_products[k] == numbers) continue;
                _products[k] = numbers;
                string data = Write(product, true);
                if (data != null) Send(new TaskStateMsg { Kind = (TaskKind)k, Id = product.id ?? "", Data = data });
            }
        }

        private void Send(TaskStateMsg msg)
        {
            if (_net.IsHost) _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
            else
            {
                _net.SendToHost(msg, DeliveryMethod.ReliableOrdered);
                string key = Key(msg);
                if (!_sentData.TryGetValue(key, out List<string> sent)) _sentData[key] = sent = new List<string>();
                sent.Add(msg.Data ?? "");
                if (sent.Count > 8) sent.RemoveAt(0);
                _mine.Add(key);
            }
            Sent++;
        }

        private static string Key(TaskStateMsg msg) =>
            msg.Kind == TaskKind.Contract ? "c" + msg.Id : "p" + (int)msg.Kind;

        // Guest: the host passes every change on to its sender too. One of this copy's own strings
        // coming back while its latest change is its own is older than what it has, or the same.
        private bool OwnEcho(TaskStateMsg msg)
        {
            if (msg.Kind == TaskKind.Roster || msg.Deleted || (msg.Kind == TaskKind.Contract && CompleteIn(msg.Data))) return false;
            string key = Key(msg);
            return _mine.Contains(key) && _sentData.TryGetValue(key, out List<string> sent) && sent.Contains(msg.Data ?? "");
        }

        /// <summary>The local changes now, not at the next look.</summary>
        public void Flush()
        {
            if (!Usable) return;
            TaskPanelHandler panel = Panel();
            if (panel != null && panel == _panel) Poll(panel);
        }

        /// <summary>Host: a guest came into the world; contracts changed while it was loading.</summary>
        public void SendAll(uint player)
        {
            if (!_net.IsHost || !Usable) return;
            TaskPanelHandler panel = Panel();
            if (panel == null) return;
            // First what there is: the guest drops a contract it holds that the host no longer has.
            var ids = new List<string>();
            foreach (GameObject obj in panel.acceptedTaskObjects)
            {
                TaskHandler task = obj != null ? obj.GetComponent<TaskHandler>() : null;
                if (task != null && !task.isProductType && !task.complete) ids.Add(task.id);
            }
            _net.SendTo(player, new TaskStateMsg { Kind = TaskKind.Roster, Data = string.Join("\n", ids) }, DeliveryMethod.ReliableOrdered);
            foreach (GameObject obj in panel.acceptedTaskObjects)
            {
                TaskHandler task = obj != null ? obj.GetComponent<TaskHandler>() : null;
                if (task == null || task.isProductType || task.complete) continue;
                string data = Write(task, false);
                if (data != null)
                    _net.SendTo(player, new TaskStateMsg { Kind = TaskKind.Contract, Id = task.id, Data = data }, DeliveryMethod.ReliableOrdered);
            }
            for (int k = 1; k < _products.Length; k++)
            {
                TaskHandler product = Product(panel, (TaskKind)k);
                string data = product != null ? Write(product, true) : null;
                if (data != null)
                    _net.SendTo(player, new TaskStateMsg { Kind = (TaskKind)k, Id = product.id ?? "", Data = data }, DeliveryMethod.ReliableOrdered);
            }
        }

        // -----------------------------------------------------------------
        // From the wire
        // -----------------------------------------------------------------

        public void OnTask(TaskStateMsg msg, NetPeer from)
        {
            if (!Usable) return;
            TaskPanelHandler panel = Panel();
            if (panel == null || panel != _panel)
            {
                if (_net.IsHost)
                    _net.Broadcast(msg, DeliveryMethod.ReliableOrdered, readyOnly: true, exceptNetId: _net.PlayerNetIdForPeer(from));
                _waiting.Add(msg);
                return;
            }
            // The local changes first, so the incoming one is not sent back as a local change.
            Poll(panel);
            if (_net.IsClient && OwnEcho(msg)) return;
            Apply(panel, msg);
            // Passed on after the host's own changes, and to its sender too: every copy then ends on
            // the host's order of changes, whichever of two changes of a contract came first.
            if (_net.IsHost) _net.Broadcast(msg, DeliveryMethod.ReliableOrdered, readyOnly: true);
        }

        private void Apply(TaskPanelHandler panel, TaskStateMsg msg)
        {
            Applied++;
            _mine.Remove(Key(msg));
            if (msg.Kind == TaskKind.Roster) { ApplyRoster(panel, msg); return; }
            if (msg.Kind != TaskKind.Contract) { ApplyProduct(panel, msg); return; }
            string id = msg.Id ?? "";
            if (id.Length == 0) return;
            GameObject obj = Find(panel, id);

            if (msg.Deleted || CompleteIn(msg.Data))
            {
                _known.Remove(id); _gone.Add(id);
                DropString(panel.acceptedTasks, id);
                if (!msg.Deleted && panel.completedTasks != null && !panel.completedTasks.Exists(s => IdOf(s) == id))
                    panel.completedTasks.Add(msg.Data);
                if (obj == null) return;
                TaskHandler gone = obj.GetComponent<TaskHandler>();
                if (msg.Deleted)
                {
                    // What the game's abandon does with the crates of the contract.
                    ShipHullMover ship = GameAccess.ActiveShip();
                    try { if (ship != null) ship.RemoveCargoOfTask(gone); }
                    catch (Exception e) { Plugin.Log.LogWarning("[Tasks] Cargo of an abandoned contract was not removed: " + e.Message); }
                }
                panel.acceptedTaskObjects.Remove(obj);
                UnityEngine.Object.Destroy(obj);
                return;
            }

            if (_gone.Contains(id)) return;
            if (obj == null)
            {
                // Built the way the game builds a contract of a loaded world.
                CreateTaskMethod.Invoke(panel, new object[] { msg.Data });
                panel.acceptedTasks.Add(msg.Data);
                obj = Find(panel, id);
                if (obj == null) return;
                // The panel shows one contract at a time and switches the others off when opened.
                obj.SetActive(false);
            }
            else
            {
                obj.GetComponent<TaskHandler>().SetTaskData(msg.Data);
                for (int i = 0; i < panel.acceptedTasks.Count; i++)
                    if (IdOf(panel.acceptedTasks[i]) == id) panel.acceptedTasks[i] = msg.Data;
            }
            _known[id] = Numbers(obj.GetComponent<TaskHandler>());
        }

        // The host's whole list, sent to a guest that came in or loaded its world again: a contract
        // this copy holds that is not in it was completed or abandoned meanwhile.
        private void ApplyRoster(TaskPanelHandler panel, TaskStateMsg msg)
        {
            if (!_net.IsClient) return;
            var ids = new HashSet<string>((msg.Data ?? "").Split('\n'), StringComparer.Ordinal);
            foreach (GameObject obj in panel.acceptedTaskObjects.ToArray())
            {
                TaskHandler task = obj != null ? obj.GetComponent<TaskHandler>() : null;
                if (task == null || task.isProductType || ids.Contains(task.id)) continue;
                _known.Remove(task.id); _gone.Add(task.id);
                DropString(panel.acceptedTasks, task.id);
                panel.acceptedTaskObjects.Remove(obj);
                UnityEngine.Object.Destroy(obj);
            }
        }

        // What the game itself reads back from a product task's string when a world is loaded.
        private void ApplyProduct(TaskPanelHandler panel, TaskStateMsg msg)
        {
            TaskHandler product = Product(panel, msg.Kind);
            string[] parts = (msg.Data ?? "").Split('=');
            if (product == null || parts.Length <= 11) return;
            CultureInfo c = CultureInfo.CurrentCulture;
            if (float.TryParse(parts[1], NumberStyles.Float, c, out float quantity)) product.quantity = quantity;
            if (float.TryParse(parts[3], NumberStyles.Float, c, out float progress)) product.progress = progress;
            if (float.TryParse(parts[5], NumberStyles.Float, c, out float loaded)) product.loaded = loaded;
            if (parts[10] != "NA" && float.TryParse(parts[11], NumberStyles.Float, c, out float distance) && distance > 0f)
            {
                string[] v = parts[10].Replace("(", "").Replace(")", "").Split(new[] { ", " }, StringSplitOptions.None);
                if (v.Length == 3 && float.TryParse(v[0], NumberStyles.Float, c, out float x) &&
                    float.TryParse(v[1], NumberStyles.Float, c, out float y) && float.TryParse(v[2], NumberStyles.Float, c, out float z))
                {
                    product.originDirection = new Vector3(x, y, z);
                    product.originDistance = distance;
                }
            }
            if (msg.Kind == TaskKind.WoodProduct) panel.woodProductTaskString = msg.Data;
            else if (msg.Kind == TaskKind.LinenProduct) panel.linenProductTaskString = msg.Data;
            else panel.ironProductTaskString = msg.Data;
            _products[(int)msg.Kind] = Numbers(product);
        }

        /// <summary>Diagnostics: the contracts as this copy's game holds them right now.</summary>
        public void Dump(StateDump d)
        {
            TaskPanelHandler panel = Usable ? Panel() : null;
            if (panel == null) { d.Info("Tasks", "panel", "not there"); return; }
            int count = 0;
            foreach (GameObject obj in panel.acceptedTaskObjects)
            {
                TaskHandler task = obj != null ? obj.GetComponent<TaskHandler>() : null;
                if (task == null || task.isProductType) continue;
                count++;
                d.Line("Tasks", "contract " + task.id, Numbers(task));
            }
            d.Line("Tasks", "contracts", count.ToString());
            for (int k = 1; k < _products.Length; k++)
            {
                TaskHandler product = Product(panel, (TaskKind)k);
                d.Line("Tasks", ((TaskKind)k).ToString(), product != null ? Numbers(product) : "none");
            }
            d.Info("Tasks", "waiting for the panel", _waiting.Count.ToString());
        }

        public string Describe()
        {
            if (!Usable) return "off: the game's task panel was not found";
            return "contracts " + _known.Count + "   sent " + Sent + "   applied " + Applied +
                   (_waiting.Count > 0 ? "   waiting " + _waiting.Count : "");
        }
    }
}
