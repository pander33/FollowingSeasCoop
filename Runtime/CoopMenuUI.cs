using System;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Sync;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>
    /// The F8 co-op menu and the status overlay (IMGUI). The game reads input through the new Input
    /// System only — <c>UnityEngine.Input</c> throws here — so the hotkey comes from
    /// <c>Keyboard.current</c>.
    /// </summary>
    public sealed partial class CoopBehaviour
    {
        private bool _menuOpen;
        private Rect _menuRect = new Rect(40f, 40f, 380f, 0f);
        private string _ipField;
        private string _nameField;
        private Key _menuKey = Key.F8;
        private bool _menuKeyParsed;

        // What the menu changed in the game while open, to put back on close.
        private bool _tookCursor;
        private CursorLockMode _cursorLockBefore;
        private bool _cursorVisibleBefore;

        public bool MenuOpen => _menuOpen;

        private void TickMenuKey()
        {
            if (!_menuKeyParsed)
            {
                _menuKeyParsed = true;
                try { _menuKey = (Key)Enum.Parse(typeof(Key), Plugin.MenuKey.Value, true); }
                catch
                {
                    _menuKey = Key.F8;
                    Plugin.Log.LogWarning("[UI] Unknown key '" + Plugin.MenuKey.Value + "' in UI/MenuKey; using F8");
                }
            }
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard[_menuKey].wasPressedThisFrame) SetMenuOpen(!_menuOpen);
        }

        public void SetMenuOpen(bool open)
        {
            if (_menuOpen == open) return;
            _menuOpen = open;
            if (open)
            {
                _ipField = Plugin.JoinIp.Value;
                _nameField = Plugin.PlayerName.Value;
                return;
            }
            // Give the mouse back to the game the way it was.
            if (_tookCursor)
            {
                _tookCursor = false;
                Cursor.lockState = _cursorLockBefore;
                Cursor.visible = _cursorVisibleBefore;
            }
            InputBlock.Set(InputBlock.Reason.Menu, false);
        }

        // In the world the game locks the cursor, turns the camera with the mouse and acts on every
        // click and key. While the menu is open the cursor is freed and the game is told to ignore
        // input (InputBlock) — otherwise a click on a menu button also grabs the rope behind it and
        // typing a name walks the player or turns the wheel.
        private void HoldCursor()
        {
            if (!_menuOpen) return;
            if (!_tookCursor && Cursor.lockState == CursorLockMode.Locked)
            {
                _tookCursor = true;
                _cursorLockBefore = Cursor.lockState;
                _cursorVisibleBefore = Cursor.visible;
            }
            if (_tookCursor)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                InputBlock.Set(InputBlock.Reason.Menu, true);
            }
        }

        private void OnGUI()
        {
            try
            {
                if (Pause != null && Pause.Active) DrawBanner("Paused: a player is loading the world");
                else if (HostPaused) DrawBanner("The host has paused the game");
                else if (Controls != null && Controls.Contested)
                    DrawBanner(Net.GetPlayerName(Controls.ContestedBy) + " is working this control");
                else if (Time.unscaledTime < _flashUntil) DrawBanner(_flash);
                else if (World != null && World.State == GuestWorldState.Receiving)
                    DrawBanner("Receiving the host's world " + Mathf.RoundToInt(World.ReceiveProgress * 100f) + "%");

                if (Plugin.ShowStatus.Value && Net != null && Net.Role != Role.None) DrawStatus();
                if (_menuOpen) _menuRect = GUILayout.Window(0x46534350, _menuRect, DrawMenu, Plugin.Name + " " + Plugin.Version);
            }
            catch (Exception e)
            {
                if (_updateErrors++ < 20) Plugin.Log.LogError("[UI] OnGUI failed: " + e);
            }
        }

        private static void DrawBanner(string text)
        {
            var rect = new Rect(Screen.width * 0.5f - 220f, 60f, 440f, 30f);
            GUI.Box(rect, text);
        }

        private void DrawStatus()
        {
            string role = Net.IsHost ? "HOST" : "CLIENT";
            string line = role + "  " + Net.State;
            if (Net.IsClient) line += "  world " + World.State + "  rtt " + Net.RttMs.ToString("F0") + " ms";
            line += "  players " + Net.RosterSnapshot.Length;
            GUI.Label(new Rect(10f, Screen.height - 26f, 700f, 22f), "Co-op: " + line);
        }

        private void DrawMenu(int id)
        {
            GUILayout.Label("Status: " + StatusLine());
            if (!string.IsNullOrEmpty(Notice)) GUILayout.Label(Notice);
            GUILayout.Space(6f);

            if (Net.Role == Role.None)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Name", GUILayout.Width(60f));
                string name = GUILayout.TextField(_nameField ?? "", Limits.Name);
                if (name != _nameField) { _nameField = name; Plugin.PlayerName.Value = name; }
                GUILayout.EndHorizontal();

                string hostBlocker = HostBlocker();
                GUI.enabled = hostBlocker == null;
                if (GUILayout.Button("Host this world")) Host();
                GUI.enabled = true;
                if (hostBlocker != null) GUILayout.Label("  " + hostBlocker);

                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Host IP", GUILayout.Width(60f));
                string ip = GUILayout.TextField(_ipField ?? "", 64);
                if (ip != _ipField) { _ipField = ip; Plugin.JoinIp.Value = ip; }
                GUILayout.EndHorizontal();

                string joinBlocker = JoinBlocker();
                GUI.enabled = joinBlocker == null;
                if (GUILayout.Button("Join")) Join(_ipField);
                GUI.enabled = true;
                if (joinBlocker != null) GUILayout.Label("  " + joinBlocker);
            }
            else
            {
                GUILayout.Label("Players:");
                foreach (SessionMemberInfo member in Net.RosterSnapshot)
                {
                    string row = "  " + member.Name + (member.IsHost ? " (host)" : "") +
                                 (member.State == MemberJoinState.Ready ? "" : "  - " + member.State.ToString().ToLowerInvariant());
                    if (member.NetId == Net.MyNetId) row += "  <- you";
                    GUILayout.Label(row);
                }
                if (Participating)
                {
                    // For a player left behind in the water: the ship does not wait.
                    string rescueBlocker = Rescue.Blocker();
                    GUI.enabled = rescueBlocker == null;
                    if (GUILayout.Button("Return to the ship")) Flash(Rescue.ReturnToShip());
                    GUI.enabled = true;
                    if (rescueBlocker != null) GUILayout.Label("  " + rescueBlocker);
                }
                GUILayout.Space(6f);
                if (GUILayout.Button(Net.IsHost ? "Stop hosting" : "Disconnect")) Disconnect();
            }

            GUILayout.Space(6f);
            bool show = GUILayout.Toggle(Plugin.ShowStatus.Value, " Show status line");
            if (show != Plugin.ShowStatus.Value) Plugin.ShowStatus.Value = show;
            if (GUILayout.Button(_debugOpen ? "Debug  [-]" : "Debug  [+]"))
            {
                _debugOpen = !_debugOpen;
                _menuRect.height = 0f;   // a layout window grows by itself but never shrinks
            }
            if (_debugOpen) DrawDebug();

            if (GUILayout.Button("Close (" + _menuKey + ")")) SetMenuOpen(false);
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private bool _debugOpen;
        private string _saveName = "CoopSave";
        private string _saveNotice = "";
        private string _itemsNotice = "";

        private void DrawDebug()
        {
            // The game shows its own save button only when its menu is opened from an object in the
            // world, not with the menu key. This calls the same save.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Save as", GUILayout.Width(60f));
            _saveName = GUILayout.TextField(_saveName ?? "", 40);
            GUILayout.EndHorizontal();
            string saveBlocker = SaveBlocker();
            GUI.enabled = saveBlocker == null;
            if (GUILayout.Button("Save")) SaveWorld(_saveName);
            GUI.enabled = true;
            if (saveBlocker != null) GUILayout.Label("  " + saveBlocker);
            else if (!string.IsNullOrEmpty(_saveNotice)) GUILayout.Label("  " + _saveNotice);

            GUILayout.Space(4f);
            GUI.enabled = GameAccess.InWorld;
            if (GUILayout.Button("Dump items")) DumpItems();
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(_itemsNotice)) GUILayout.Label(_itemsNotice);
            GUILayout.Label("Patches: " + PatchHealth.Details);
            GUILayout.Label("Protocol " + Protocol.Version + "   NetId " + Net.MyNetId + "   world " + World.State);
            GUILayout.Label("Scene: " + (GameAccess.InWorld ? "world" : GameAccess.AtMainMenu ? "title screen" : "other"));
            if (GameAccess.InWorld)
            {
                ShipHullMover ship = GameAccess.ActiveShip();
                PlayerHandler player = GameAccess.Player;
                CoordSpace.Offset(out double ox, out double oy, out double oz);
                GUILayout.Label("Ship: " + (ship != null ? ship.id : "none") +
                                "   aboard: " + (ship != null && player.ship == ship.gameObject));
                GUILayout.Label("Player: " + player.transform.position.ToString("F1"));
                GUILayout.Label("Origin offset: " + ox.ToString("F0") + ", " + oy.ToString("F0") + ", " + oz.ToString("F0"));
                GUILayout.Label("Time scale: " + Time.timeScale.ToString("F2") + (Pause.Active ? "  (join pause)" : ""));
            }
            if (Net.Role != Role.None)
            {
                GUILayout.Label("Clock offset " + Net.Clock.OffsetMs.ToString("F0") + " ms   rtt " + Net.RttMs.ToString("F0") + " ms");
                GUILayout.Label("Remote players: " + Players.Describe());
                GUILayout.Label("Ship sync: " + Boats.Describe());
                GUILayout.Label("Sea and sky: " + Env.Describe());
                GUILayout.Label("Controls: " + Controls.Describe());
                GUILayout.Label("Items: " + Items.Describe());
                GUILayout.Label("Household: " + Economy.Describe());
                GUILayout.Label("Tasks: " + Tasks.Describe());
                if (Rescue.Rescues > 0) GUILayout.Label("Pulled back aboard: " + Rescue.Rescues);
            }
        }

        /// <summary>Writes the I0 catalog without saving or changing the world.</summary>
        public string DumpItems()
        {
            try
            {
                string path = ItemCatalog.Write(this);
                _itemsNotice = "Items written to " + path;
                Plugin.Log.LogInfo("[Items] " + _itemsNotice);
                return path;
            }
            catch (Exception e)
            {
                _itemsNotice = "Item dump failed: " + e.Message;
                Plugin.Log.LogError("[Items] " + e);
                return null;
            }
        }

        /// <summary>Null when this copy may save the world now, otherwise why not.</summary>
        public string SaveBlocker()
        {
            if (!GameAccess.InWorld) return "No world is loaded";
            if (WorldTransfer.GuestWorld) return "Only the host saves: this is the host's world";
            return null;
        }

        /// <summary>Saves the world under a name through the game's own save, as its save dialog does.</summary>
        public bool SaveWorld(string name)
        {
            string blocker = SaveBlocker();
            if (blocker != null) { _saveNotice = blocker; return false; }
            name = (name ?? "").Trim();
            // The name becomes a file name in the game's save folder; these three are the game's own files.
            if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                name == "Settings" || name == "Player.log" || name == "Player-prev.log")
            {
                _saveNotice = "This name cannot be a save name";
                return false;
            }
            try
            {
                GameAccess.Dp.SaveGame(name);
                _saveNotice = "Saved as '" + name + "' at " + DateTime.Now.ToString("HH:mm:ss");
                Plugin.Log.LogInfo("[Coop] World saved as '" + name + "' from the debug panel");
                return true;
            }
            catch (Exception e)
            {
                _saveNotice = "Save failed: " + e.Message;
                Plugin.Log.LogError("[Coop] Save failed: " + e);
                return false;
            }
        }

        /// <summary>One line that says where this copy stands. Also what the dev console reads.</summary>
        public string StatusLine()
        {
            switch (Net.Role)
            {
                case Role.Host:
                    return "hosting, " + (Net.RosterSnapshot.Length - 1) + " guest(s)" + (Pause.Active ? ", paused for a join" : "");
                case Role.Client:
                    return Net.State == LinkState.Connected ? "connected, world " + World.State : Net.State.ToString().ToLowerInvariant();
                default:
                    if (World.State == GuestWorldState.Leaving) return "leaving the host's world";
                    return Net.State == LinkState.Idle ? "not connected" : Net.State.ToString().ToLowerInvariant();
            }
        }
    }
}
