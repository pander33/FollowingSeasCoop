using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Sync;
using HarmonyLib;
using UnityEngine;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>
    /// The debugging half of <see cref="CoopBehaviour"/>: the trace of messages, the count of what
    /// went wrong, the dump of this copy's shared state, and — behind <c>Dev/Tools</c> — buttons
    /// that bring about a situation without sailing to it. A button does what the game itself
    /// would have done on this copy and never calls the synchronisation: that is what is tested.
    /// </summary>
    public sealed partial class CoopBehaviour
    {
        private static readonly MethodInfo LeakDegradeMethod = AccessTools.Method(typeof(LeakController), "Degrade");
        private static readonly FieldInfo AnimalDeadField = AccessTools.Field(typeof(WanderingGameController), "dead");
        private static readonly FieldInfo WeatherTypesField = AccessTools.Field(typeof(WeatherController), "weatherTypes");
        private static readonly FieldInfo IssuerSpotField = AccessTools.Field(typeof(TaskMenuController), "cameraLockPos");
        private static readonly FieldInfo TaskPrefabField = AccessTools.Field(typeof(TaskPanelHandler), "taskPrefab");
        private static readonly FieldInfo TaskContainerField = AccessTools.Field(typeof(TaskPanelHandler), "taskContainer");
        private static readonly MethodInfo CreateTaskMethod = AccessTools.Method(typeof(TaskPanelHandler), "CreateTask");
        private static readonly FieldInfo TaskDestinationField = AccessTools.Field(typeof(TaskHandler), "destinationName");
        private static readonly FieldInfo TaskAssignmentRunField = AccessTools.Field(typeof(TaskPanelHandler), "assignmentRun");
        private static readonly FieldInfo TaskRewardTypeField = AccessTools.Field(typeof(TaskHandler), "rewardType");
        private static readonly FieldInfo TaskRewardAmountField = AccessTools.Field(typeof(TaskHandler), "rewardAmount");
        private static readonly FieldInfo TaskRewardKindField = AccessTools.Field(typeof(TaskHandler), "pType");
        private static readonly MethodInfo TaskAddProvisionsMethod = AccessTools.Method(typeof(TaskHandler), "AddProvisions");
        private static readonly FieldInfo CargoBoxesField =AccessTools.Field(typeof(ShipHullMover), "cargoBoxes");

        private string _traceWho = "";
        private long _dumpSlot = -1;
        private bool _autoDone;
        private float _autoAt;
        private float _worldSince = -1f;
        private string _devNotice = "";

        private void InitDev()
        {
            CoopTrace.Clock = () => Net.Clock.ServerTick;
            Net.Tap = OnTap;
            BepInEx.Logging.Logger.Listeners.Add(new CoopTrace.LogWatch(Plugin.Log.SourceName));
            Application.logMessageReceived += OnUnityLog;
            if (Plugin.DevRunInBackground.Value) Application.runInBackground = true;
            Plugin.Log.LogInfo("[Dev] The game runs in the background: " + Application.runInBackground);
        }

        // Every gameplay message, as it leaves and as it is about to be acted on.
        private static void OnTap(bool sent, MsgType type, INetMessage msg, uint other)
        {
            try
            {
                string area = TraceText.Area(type);
                if (area == null || TraceText.Quiet(msg))
                {
                    CoopTrace.Count((area == null ? TraceText.CounterName(type) : "Wheel and water") + (sent ? " out" : " in"));
                    return;
                }
                TraceText.Describe(msg, out string key, out string value);
                string who = sent ? (other == 0 ? "to all" : "to " + other) : "from " + other;
                CoopTrace.Note(area, (sent ? "sent " : "got ") + type, key, value + "  [" + who + "]");
            }
            catch { }
        }

        // An exception that got past the game's own code is in Player.log only; nobody reads that
        // while testing. One with a frame of the mod in it is the mod's own.
        private static void OnUnityLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception) return;
            try
            {
                string first = "";
                if (!string.IsNullOrEmpty(stack))
                {
                    int end = stack.IndexOf('\n');
                    first = end > 0 ? stack.Substring(0, end) : stack;
                }
                string text = condition + (first.Length > 0 ? "  at " + first.Trim() : "");
                if ((stack ?? "").Contains("FollowingSeasCoop")) CoopTrace.Problem("Coop", "Exception through the mod: " + text);
                else CoopTrace.GameError(text);
            }
            catch { }
        }

        private string Who => Net.IsHost ? "host" : Net.IsClient && Net.State == LinkState.Connected ? "guest" + Net.MyNetId : "";

        private int[] _window;
        private bool _windowRead;
        private float _windowRetryAt;
        private float _windowPlaceAt;

        // Dev/Window. The game's options panels call Screen.SetResolution(..., fullscreen: true)
        // when they are built (title screen, world) and when they are confirmed, so a window asked
        // for on the command line lasts until the title screen. Polled: the calls are in listeners.
        private void TickWindow()
        {
            if (!_windowRead)
            {
                _windowRead = true;
                string text = Plugin.DevWindow.Value ?? "";
                if (text.Trim().Length == 0) return;
                string[] parts = text.Split(',');
                var numbers = new int[4];
                bool good = parts.Length == 4;
                for (int i = 0; good && i < 4; i++) good = int.TryParse(parts[i].Trim(), out numbers[i]);
                if (!good || numbers[2] < 320 || numbers[3] < 200)
                {
                    Plugin.Log.LogWarning("[Dev] Dev/Window '" + text + "' is not x,y,width,height");
                    return;
                }
                _window = numbers;
            }
            if (_window == null) return;
            float now = Time.unscaledTime;
            if (Screen.fullScreen)
            {
                // The change takes a frame or more to show in Screen.fullScreen.
                if (now < _windowRetryAt) return;
                _windowRetryAt = now + 0.5f;
                _windowPlaceAt = now + 0.5f;
                Screen.SetResolution(_window[2], _window[3], FullScreenMode.Windowed);
                return;
            }
            if (_windowPlaceAt < 0f || now < _windowPlaceAt) return;
            _windowPlaceAt = -1f;
            if (!GameWindow.MoveTo(_window[0], _window[1])) Plugin.Log.LogInfo("[Dev] Dev/Window: the game's window was not found");
        }

        private static class GameWindow
        {
            [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            private static extern uint GetCurrentProcessId();

            // Two copies run at once: the window is found by its process, not by its title.
            public static bool MoveTo(int x, int y)
            {
                try
                {
                    uint me = GetCurrentProcessId();
                    IntPtr window = IntPtr.Zero;
                    for (int i = 0; i < 64; i++)
                    {
                        window = FindWindowEx(IntPtr.Zero, window, "UnityWndClass", null);
                        if (window == IntPtr.Zero) return false;
                        GetWindowThreadProcessId(window, out uint owner);
                        if (owner != me) continue;
                        // No size, no order, no activation: only the place.
                        return SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
                    }
                }
                catch { }
                return false;
            }
        }

        private void TickDev()
        {
            TickWindow();
            Net.LagMs = Plugin.DevLagMs.Value;
            Net.JitterMs = Plugin.DevJitterMs.Value;

            // The trace file lives as long as the session and is named after who this copy is in it.
            string who = Plugin.DevTrace.Value ? Who : "";
            if (who != _traceWho)
            {
                _traceWho = who;
                if (who.Length == 0) CoopTrace.Close();
                else CoopTrace.Open(Path.Combine(BepInEx.Paths.BepInExRootPath, "coop-trace-" + who + ".log"), who);
            }

            // On the host's clock, so every copy writes its state at the same moment.
            int every = Plugin.DevDumpEverySeconds.Value;
            if (every > 0 && Participating)
            {
                long slot = Net.Clock.ServerTick / (every * 1000L);
                if (slot != _dumpSlot)
                {
                    bool first = _dumpSlot < 0;
                    _dumpSlot = slot;
                    if (!first) DumpState();
                }
            }
            else _dumpSlot = -1;

            if (Net.Role != Role.None) { if (Net.State == LinkState.Connected) _autoDone = true; return; }
            if (_autoDone || Time.unscaledTime < _autoAt) return;
            // A world that has only just been loaded is still being put together by the game.
            _worldSince = GameAccess.InWorld ? (_worldSince < 0f ? Time.unscaledTime : _worldSince) : -1f;
            if (Plugin.DevAutoHost.Value && HostBlocker() == null)
            {
                if (Time.unscaledTime - _worldSince < 5f) return;
                _autoDone = true;
                Plugin.Log.LogInfo("[Dev] AutoHost: hosting");
                Host();
            }
            else if (Plugin.DevAutoJoin.Value && JoinBlocker() == null)
            {
                _autoAt = Time.unscaledTime + 5f;
                Plugin.Log.LogInfo("[Dev] AutoJoin: joining " + Plugin.JoinIp.Value);
                Join(Plugin.JoinIp.Value);
            }
        }

        // -----------------------------------------------------------------
        // The dump of this copy's shared state
        // -----------------------------------------------------------------

        /// <summary>Writes what this copy holds of the shared world to a file. Returns its path, or null.</summary>
        public string DumpState()
        {
            try
            {
                string who = Who.Length > 0 ? Who : "solo";
                var d = new StateDump();
                d.Info("Session", "who", who);
                d.Info("Session", "protocol", Protocol.Version.ToString());
                d.Info("Session", "problems", CoopTrace.Problems + ", game exceptions " + CoopTrace.GameErrors);
                d.Info("Session", "patches", PatchHealth.Details);
                if (GameAccess.InWorld)
                {
                    ShipHullMover ship = GameAccess.ActiveShip();
                    if (ship != null)
                    {
                        CoordSpace.LocalToReal(ship.transform.position, out double x, out double y, out double z);
                        d.Line("Ship", "id", ship.id ?? "");
                        // A guest draws the hull a moment behind the host.
                        d.Line("Ship", "place", StateDump.F(x) + " " + StateDump.F(y) + " " + StateDump.F(z), 5f);
                        Vector3 e = ship.transform.eulerAngles;
                        d.Info("Ship", "heading", StateDump.F(e.y));
                    }
                    else d.Line("Ship", "id", "none");
                    d.Info("Players", "remote", Players.Describe());
                    Section(d, "Controls", () => Controls.Dump(d));
                    Section(d, "Items", () => Items.Dump(d));
                    Section(d, "Household", () => Economy.Dump(d));
                    Section(d, "Tasks", () => Tasks.Dump(d));
                    Section(d, "Cargo", () => Cargo.Dump(d));
                    Section(d, "Hunt", () => Hunt.Dump(d));
                    Section(d, "Rowboat", () => Rowboat.Dump(d));
                    Section(d, "Crew", () => Crew.Dump(d));
                }
                else d.Info("Session", "world", "none");

                string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "coop-state-" + who + ".txt");
                d.Write(path, "Following Seas Co-op state of " + who + ", host clock " + Net.Clock.ServerTick + " ms, " +
                              DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                return path;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Dev] The state was not written: " + e);
                return null;
            }
        }

        private static void Section(StateDump d, string name, Action body)
        {
            try { body(); }
            catch (Exception e) { d.Line(name, "FAILED", e.GetType().Name + ": " + e.Message); }
        }

        // -----------------------------------------------------------------
        // Situations
        // -----------------------------------------------------------------

        private string Dev(Func<string> action)
        {
            try { return action(); }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Dev] " + e);
                return "Failed: " + e.Message;
            }
        }

        /// <summary>Opens the next closed leak, the way the game's own dice do.</summary>
        public string DevOpenLeak() => Dev(() =>
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            if (ship == null) return "There is no ship";
            LeakController[] leaks = ship.GetComponentsInChildren<LeakController>(true);
            for (int i = 0; i < leaks.Length; i++)
            {
                if (leaks[i] == null || leaks[i].gameObject.activeSelf) continue;
                leaks[i].gameObject.SetActive(true);
                return "Leak " + i + " of " + leaks.Length + " is open";
            }
            return leaks.Length == 0 ? "This ship has no leaks" : "Every leak is open already";
        });

        /// <summary>Makes every open leak one step stronger, the way the game's own timer does.</summary>
        public string DevWidenLeaks() => Dev(() =>
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            if (ship == null) return "There is no ship";
            if (LeakDegradeMethod == null) return "The game's leak method was not found";
            int n = 0;
            foreach (LeakController leak in ship.GetComponentsInChildren<LeakController>(false))
            {
                LeakDegradeMethod.Invoke(leak, null);
                n++;
            }
            return n == 0 ? "No leak is open" : "Widened " + n + " leak(s)";
        });

        /// <summary>Puts a fish on the next free hook, the way a bite does.</summary>
        public string DevHookFish() => Dev(() =>
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            if (ship == null) return "There is no ship";
            // The hooks hang on the lines, not under the hull: the list the controls are shared by.
            IReadOnlyList<FishPoint> hooks = Controls.Hooks;
            foreach (FishPoint hook in hooks)
            {
                if (hook == null || !hook.gameObject.activeInHierarchy || hook.fishOn || hook.fish == null) continue;
                hook.fishOn = true;
                if (hook.fish.transform.parent != hook.transform) hook.fish.transform.parent = hook.transform;
                float t = UnityEngine.Random.Range(0f, 1f);
                hook.SetScale(t * t * (hook.maxScale - hook.minScale) + hook.minScale);
                return "A fish is on a hook (" + hooks.Count + " hooks)";
            }
            return hooks.Count == 0 ? "No trolling hooks are bound (a session must run, and the ship must have lines)"
                                    : "Every hook has a fish already";
        });

        public string DevAddFish() => Dev(() =>
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            FishCargoManager hold = ship != null ? ship.GetComponent<FishCargoManager>() : null;
            if (hold == null) return "This ship has no fish hold";
            hold.AddFish();
            ship.UpdateCargoMass();
            return "Fish in the hold: " + hold.currentFishCount;
        });

        // The nearest one that is still alive: a second press must find the next animal.
        private static WanderingGameController NearestAnimal(PlayerHandler player, out float distance)
        {
            WanderingGameController nearest = null;
            distance = float.MaxValue;
            foreach (WanderingGameController animal in
                     UnityEngine.Object.FindObjectsByType<WanderingGameController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (AnimalDeadField != null && (bool)AnimalDeadField.GetValue(animal)) continue;
                float d = Vector3.Distance(animal.transform.position, player.transform.position);
                if (d < distance) { distance = d; nearest = animal; }
            }
            return nearest;
        }

        public string DevGoToAnimal() => Dev(() =>
        {
            PlayerHandler player = GameAccess.Player;
            if (player == null) return "No world is loaded";
            WanderingGameController animal = NearestAnimal(player, out float distance);
            if (animal == null) return "No living animal is loaded here";
            PlayerRescue.Move(player, animal.transform.position + Vector3.up * 1.5f + Vector3.forward * 3f);
            return "At '" + animal.name + "', which was " + distance.ToString("F0") + " m away";
        });

        public string DevKillAnimal() => Dev(() =>
        {
            PlayerHandler player = GameAccess.Player;
            if (player == null) return "No world is loaded";
            WanderingGameController animal = NearestAnimal(player, out float distance);
            if (animal == null) return "No living animal is loaded here";
            animal.Kill();
            return "Killed '" + animal.name + "', " + distance.ToString("F0") + " m away";
        });

        public string DevGoToPlayer(uint netId) => Dev(() =>
        {
            PlayerHandler player = GameAccess.Player;
            if (player == null) return "No world is loaded";
            if (!Players.AvatarAt(netId, out Vector3 position)) return "That player has no avatar here";
            PlayerRescue.Move(player, position + Vector3.up * 0.5f);
            return "At " + Net.GetPlayerName(netId);
        });

        public string DevAddMoney(float amount) => Dev(() =>
        {
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null || dp.gameData == null) return "No world is loaded";
            dp.gameData.tearCount += amount;
            return "Money: " + dp.gameData.tearCount.ToString("F0");
        });

        public string DevAddStores() => Dev(() =>
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            ProvisionsController stores = ship != null ? ship.provisionController : null;
            if (stores == null) return "There is no ship";
            stores.AddProvisions(5f, ProvisionsController.ProvisionType.Oakum);
            stores.AddProvisions(5f, ProvisionsController.ProvisionType.Food);
            return "Oakum " + stores.currentOakum.ToString("F0") + ", food " + stores.currentFood.ToString("F0");
        });

        public string DevGoToRowboat() => Dev(() =>
        {
            PlayerHandler player = GameAccess.Player;
            ShipHullMover ship = GameAccess.ActiveShip();
            if (player == null || ship == null) return "There is no ship";
            GameObject boat = ship.rowboatDeployed ? RowboatSync.Of(ship) : null;
            if (boat == null) return "The rowboat is stowed";
            PlayerRescue.Move(player, boat.transform.position + Vector3.up * 1.5f);
            return "In the rowboat";
        });

        /// <summary>To the nearest loaded contract board, where the game puts the view of a player who reads it.</summary>
        public string DevGoToDock() => Dev(() =>
        {
            PlayerHandler player = GameAccess.Player;
            if (player == null) return "No world is loaded";
            TaskIssuer nearest = null;
            float distance = float.MaxValue;
            foreach (TaskIssuer issuer in
                     UnityEngine.Object.FindObjectsByType<TaskIssuer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                float d = Vector3.Distance(issuer.transform.position, player.transform.position);
                if (d < distance) { distance = d; nearest = issuer; }
            }
            if (nearest == null) return "No dock is loaded here";
            TaskMenuController menu = nearest.GetComponent<TaskMenuController>();
            Transform spot = menu != null ? IssuerSpotField?.GetValue(menu) as Transform : null;
            PlayerRescue.Move(player, spot != null ? spot.position : nearest.transform.position + Vector3.up * 1.5f);
            return "At the contracts of '" + nearest.transform.root.name + "', which were " + distance.ToString("F0") + " m away";
        });

        /// <summary>Off the deck, 20 m abeam: what a fall over the side leaves.</summary>
        public string DevOverboard() => Dev(() =>
        {
            PlayerHandler player = GameAccess.Player;
            ShipHullMover ship = GameAccess.ActiveShip();
            if (player == null || ship == null) return "There is no ship";
            Vector3 abeam = ship.transform.right;
            abeam.y = 0f;
            PlayerRescue.Move(player, ship.transform.position + abeam.normalized * 20f + Vector3.up * 2f);
            return "Overboard";
        });

        /// <summary>The next weather type of the game's list, brought in the way the game changes weather.</summary>
        public string DevNextWeather() => Dev(() =>
        {
            DataPersistenceManager dp = GameAccess.Dp;
            WeatherController weather = dp != null ? dp.weatherController : null;
            if (weather == null) return "No world is loaded";
            if (!(WeatherTypesField?.GetValue(weather) is List<WeatherType> types) || types.Count == 0) return "The game's weather list was not found";
            int next = (types.IndexOf(weather.currentWeatherType) + 1) % types.Count;
            weather.ChangeWeather(types[next].id);
            return "Weather: " + types[next].id + " (" + (next + 1) + " of " + types.Count + "), blending in";
        });

        public string DevAddHours(int hours) => Dev(() =>
        {
            DataPersistenceManager dp = GameAccess.Dp;
            var time = dp != null && dp.enviro != null ? dp.enviro.Time : null;
            if (time == null) return "No world is loaded";
            // Through the calendar: past midnight the day, and with it the month, must stay real.
            DateTime then = new DateTime(time.years, time.months, time.days, time.hours, time.minutes, time.seconds).AddHours(hours);
            time.SetDateTime(then.Second, then.Minute, then.Hour, then.Day, then.Month, then.Year);
            return "Time: " + time.hours.ToString("00") + ":" + time.minutes.ToString("00");
        });

        /// <summary>
        /// A contract from the nearest place that gives cargo: generated by the game's own task
        /// object, then built as the game builds an accepted contract of a loaded world.
        /// </summary>
        public string DevTakeContract() => Dev(() =>
        {
            PlayerHandler player = GameAccess.Player;
            ShipHullMover ship = GameAccess.ActiveShip();
            TaskPanelHandler panel = TaskPanelHandler.Instance;
            if (player == null || ship == null || panel == null) return "There is no ship";
            var prefab = TaskPrefabField?.GetValue(panel) as GameObject;
            var container = TaskContainerField?.GetValue(panel) as GameObject;
            if (prefab == null || container == null || CreateTaskMethod == null) return "The game's task panel was not found";
            // Until then the panel builds every string of its list itself: this one would be built twice.
            if (!(TaskAssignmentRunField?.GetValue(panel) is bool built) || !built) return "The task panel has not built its contracts yet";

            Destination origin = null;
            float distance = float.MaxValue;
            foreach (Destination place in
                     UnityEngine.Object.FindObjectsByType<Destination>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (place.providedCargoes == null || place.providedCargoes.Count == 0) continue;
                float d = Vector3.Distance(place.transform.position, player.transform.position);
                if (d < distance) { distance = d; origin = place; }
            }
            if (origin == null) return "No place that gives cargo was found";

            // Under the panel's container: the task looks for its ship four parents up.
            GameObject draft = UnityEngine.Object.Instantiate(prefab);
            string data, given = "";
            try
            {
                draft.transform.SetParent(container.transform, false);
                TaskHandler task = draft.GetComponent<TaskHandler>();
                task.targetShip = ship;
                task.SetOrigin(origin);
                task.GenNewTaskData();
                data = task.GetSaveString();
                // What the game's Accept button does besides: a reward in provisions is given at once.
                if (TaskRewardTypeField?.GetValue(task) as string == "Provisions" && TaskAddProvisionsMethod != null)
                {
                    TaskAddProvisionsMethod.Invoke(task, new[] { TaskRewardAmountField.GetValue(task), TaskRewardKindField.GetValue(task) });
                    given = ", its reward in provisions given";
                }
            }
            finally { UnityEngine.Object.Destroy(draft); }

            panel.acceptedTasks.Add(data);
            CreateTaskMethod.Invoke(panel, new object[] { data });
            string[] fields = data.Split('=');
            return "Contract " + fields[12] + ": " + fields[1] + " x " + fields[6] + " from " + origin.name + " to " + fields[15] + given;
        });

        /// <summary>
        /// One crate of the first contract that still has cargo to load, in the first free place of
        /// the hold: the steps of the game's cargo panel, without the panel (and without its
        /// rule of 400 m from the origin).
        /// </summary>
        public string DevLoadCrate() => Dev(() =>
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            TaskPanelHandler panel = TaskPanelHandler.Instance;
            if (ship == null || panel == null) return "There is no ship";
            TaskHandler task = null;
            foreach (GameObject obj in panel.acceptedTaskObjects)
            {
                TaskHandler t = obj != null ? obj.GetComponent<TaskHandler>() : null;
                if (t == null || t.complete || t.cargo == null || t.loaded >= t.quantity) continue;
                task = t;
                break;
            }
            if (task == null) return "No contract has cargo left to load";
            if (!(CargoBoxesField?.GetValue(ship) is List<CargoPlacer> placers)) return "The ship's cargo boxes were not found";

            int boxes = 0;
            foreach (CargoPlacer placer in placers)
            {
                Transform box = placer != null ? placer.transform.parent : null;
                BoxCollider bounds = box != null ? box.GetComponent<BoxCollider>() : null;
                if (bounds == null) continue;
                boxes++;
                var taken = new HashSet<Vector3Int>();
                foreach (Transform child in box)
                    if (child.GetComponent<CargoDefinition>() != null) taken.Add(Vector3Int.RoundToInt(child.localPosition * 2f));
                // The panel's grid: whole metres, shifted by half where the box is an even number wide.
                float shift = bounds.size.x % 2f == 0f ? 0.5f : 0f;
                float halfX = bounds.size.x * 0.5f - 0.5f, halfZ = bounds.size.z * 0.5f - 0.5f;
                Vector3 centre = bounds.center;
                for (float z = Mathf.Ceil(centre.z - halfZ); z <= centre.z + halfZ; z += 1f)
                    for (float x = Mathf.Ceil(centre.x - halfX - shift) + shift; x <= centre.x + halfX; x += 1f)
                    {
                        var place = new Vector3(x, -0.5f, z);
                        if (taken.Contains(Vector3Int.RoundToInt(place * 2f))) continue;
                        GameObject crate = UnityEngine.Object.Instantiate(task.cargo, box);
                        crate.transform.localPosition = place;
                        crate.transform.rotation = box.rotation;
                        FixedJoint joint = crate.GetComponent<FixedJoint>();
                        if (joint != null) joint.connectedBody = ship.GetComponent<Rigidbody>();
                        CargoDefinition cargo = crate.GetComponent<CargoDefinition>();
                        cargo.SetReflectionProbe(ship.cargoReflectionProbe);
                        cargo.taskID = task.id;
                        if (TaskDestinationField?.GetValue(task) is string destination && destination.Length > 0) cargo.SetDestination(destination);
                        task.loaded += 1f;
                        ship.UpdateCargoMass();
                        return "Crate of " + task.id + " loaded: " + task.loaded.ToString("F0") + " of " + task.quantity.ToString("F0");
                    }
            }
            return boxes == 0 ? "No cargo box with bounds was found on this ship" : "The hold has no free place";
        });

        /// <summary>
        /// What the host tells a player who has just come in, told again: the household, the
        /// contracts, the hold, the animals and every control. Not the things: a manifest makes a
        /// guest forget its records and bind them anew, which is a test of its own.
        /// </summary>
        public string DevResync() => Dev(() =>
        {
            if (!Participating) return "Not in a session";
            if (Net.IsClient)
            {
                Economy.AskAgain();
                Cargo.AskAgain();
                return "Asked the host for everything again";
            }
            Controls.MarkAllDirty();
            int guests = 0;
            foreach (SessionMemberInfo member in Net.RosterSnapshot)
            {
                if (member.IsHost || member.State != MemberJoinState.Ready) continue;
                Economy.SendAll(member.NetId);
                Tasks.SendAll(member.NetId);
                Hunt.SendAll(member.NetId);
                Cargo.SendAll(member.NetId);
                guests++;
            }
            return "Sent everything again to " + guests + " guest(s)";
        });

        // -----------------------------------------------------------------
        // Menu
        // -----------------------------------------------------------------

        private bool _traceOpen, _devOpen;
        private string _traceArea = "";
        private string _lagField;
        private readonly List<string> _lines = new List<string>();

        private void DrawProblems()
        {
            if (CoopTrace.Problems == 0 && CoopTrace.GameErrors == 0)
            {
                GUILayout.Label("Problems: none");
                return;
            }
            Color was = GUI.contentColor;
            if (CoopTrace.Problems > 0) GUI.contentColor = new Color(1f, 0.45f, 0.4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Problems: " + CoopTrace.Problems + "   game exceptions: " + CoopTrace.GameErrors);
            GUI.contentColor = was;
            if (GUILayout.Button("Reset", GUILayout.Width(60f))) CoopTrace.ResetCounts();
            GUILayout.EndHorizontal();
            _lines.Clear();
            CoopTrace.Recent(_lines, 5);
            foreach (string line in _lines) GUILayout.Label("  " + line);
            if (CoopTrace.LastGameError.Length > 0) GUILayout.Label("  game: " + CoopTrace.LastGameError);
        }

        private void DrawTrace()
        {
            if (GUILayout.Button(_traceOpen ? "Trace  [-]" : "Trace  [+]"))
            {
                _traceOpen = !_traceOpen;
                _menuRect.height = 0f;
                _menuRect.width = _traceOpen ? 620f : 380f;
            }
            if (!_traceOpen) return;
            GUILayout.Label("Counted only: " + CoopTrace.CountersLine());
            if (CoopTrace.FilePath.Length > 0) GUILayout.Label("File: " + CoopTrace.FilePath);
            else GUILayout.Label("No file is written (Dev/Trace is off, or there is no session)");
            GUILayout.BeginHorizontal();
            int inRow = 0;
            foreach (string area in CoopTrace.AreaNames)
            {
                // Every subsystem that ever logged a problem has a name here too.
                if (inRow++ == 6) { inRow = 1; GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                bool on = area == _traceArea;
                if (GUILayout.Toggle(on, area, GUI.skin.button) != on)
                {
                    _traceArea = on ? "" : area;
                    _menuRect.height = 0f;
                }
            }
            GUILayout.EndHorizontal();
            if (_traceArea.Length == 0) return;
            _lines.Clear();
            CoopTrace.Last(_traceArea, _lines, 12);
            foreach (string line in _lines) GUILayout.Label(line);
        }

        private void DrawDev()
        {
            if (GUILayout.Button("Dump state")) _devNotice = "State written to " + (DumpState() ?? "nothing: see the log");
            if (!Plugin.DevTools.Value)
            {
                if (!string.IsNullOrEmpty(_devNotice)) GUILayout.Label(_devNotice);
                return;
            }
            if (GUILayout.Button(_devOpen ? "Dev  [-]" : "Dev  [+]"))
            {
                _devOpen = !_devOpen;
                _menuRect.height = 0f;
            }
            if (!string.IsNullOrEmpty(_devNotice)) GUILayout.Label(_devNotice);
            if (!_devOpen) return;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Lag ms", GUILayout.Width(60f));
            if (_lagField == null) _lagField = Plugin.DevLagMs.Value.ToString();
            string lag = GUILayout.TextField(_lagField, 5);
            if (lag != _lagField)
            {
                _lagField = lag;
                if (int.TryParse(lag, out int ms) && ms >= 0) Plugin.DevLagMs.Value = ms;
            }
            GUILayout.Label("held " + Net.HeldCount, GUILayout.Width(80f));
            GUILayout.EndHorizontal();

            GUI.enabled = Participating;
            if (GUILayout.Button(Net.IsClient ? "Ask the host for everything again" : "Send everything again")) _devNotice = DevResync();
            GUI.enabled = GameAccess.InWorld;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open a leak")) _devNotice = DevOpenLeak();
            // A guest's leaks do not widen by themselves: its copy of the game's timer is switched off.
            GUI.enabled = GameAccess.InWorld && !Net.IsClient;
            if (GUILayout.Button("Widen leaks")) _devNotice = DevWidenLeaks();
            GUI.enabled = GameAccess.InWorld;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Fish on a hook")) _devNotice = DevHookFish();
            if (GUILayout.Button("Fish into the hold")) _devNotice = DevAddFish();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Go to an animal")) _devNotice = DevGoToAnimal();
            if (GUILayout.Button("Kill it")) _devNotice = DevKillAnimal();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Money +100")) _devNotice = DevAddMoney(100f);
            if (GUILayout.Button("Oakum and food +5")) _devNotice = DevAddStores();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Take a contract")) _devNotice = DevTakeContract();
            if (GUILayout.Button("Load a crate")) _devNotice = DevLoadCrate();
            GUILayout.EndHorizontal();

            // The sea and the sky are the host's: a guest's are written over four times a second.
            GUI.enabled = GameAccess.InWorld && !Net.IsClient;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Next weather")) _devNotice = DevNextWeather();
            if (GUILayout.Button("Time +3 h")) _devNotice = DevAddHours(3);
            GUILayout.EndHorizontal();
            GUI.enabled = GameAccess.InWorld;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Go to a dock")) _devNotice = DevGoToDock();
            if (GUILayout.Button("Go to the rowboat")) _devNotice = DevGoToRowboat();
            if (GUILayout.Button("Overboard")) _devNotice = DevOverboard();
            GUILayout.EndHorizontal();

            foreach (SessionMemberInfo member in Net.RosterSnapshot)
            {
                if (member.NetId == Net.MyNetId) continue;
                if (GUILayout.Button("Go to " + member.Name)) _devNotice = DevGoToPlayer(member.NetId);
            }
            GUI.enabled = true;
        }

        private void DrawProblemLine()
        {
            if (CoopTrace.Problems == 0) return;
            Color was = GUI.contentColor;
            GUI.contentColor = new Color(1f, 0.45f, 0.4f);
            GUI.Label(new Rect(10f, Screen.height - 46f, 700f, 22f),
                      "Co-op: " + CoopTrace.Problems + " problem(s) - open the menu (" + _menuKey + "), Debug");
            GUI.contentColor = was;
        }
    }
}
