using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Sync;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>
    /// The player's co-op menu (uGUI), in the game's own look: a <b>Co-op</b> button among the title
    /// screen's buttons opens a dialog to join a host, and a <b>Co-op</b> button in the game's ESC
    /// menu opens a page — where the game shows its options — to host the loaded world and to see
    /// and end a session. Buttons are copies of the game's buttons with the game's listeners
    /// removed; texts use the game's fonts.
    ///
    /// Two halves, driven separately from <see cref="CoopBehaviour"/> so a failure of the panel
    /// cannot take the first with it: <see cref="TickInput"/> keeps a guest's world running under
    /// the game's pause menu and answers the menu key while the mod holds the input;
    /// <see cref="TickPanel"/> is everything that is drawn. What is shown is worked out from the
    /// session's state every time, never remembered.
    /// </summary>
    internal static class CoopGameUI
    {
        private enum Place { Closed, Title, World }

        private const float RefWidth = 1600f, RefHeight = 900f;   // the game's canvases
        private const int MaxFailures = 3;
        private const int FoundRows = 4;   // games of the local network shown in the join dialog

        private static readonly Color Slate = new Color(0.129f, 0.131f, 0.142f, 0.97f);
        private static readonly Color Ink = new Color(0.196f, 0.196f, 0.196f, 1f);
        private static readonly Color Soft = new Color(1f, 1f, 1f, 0.8f);
        private static readonly Color Warm = new Color(1f, 0.86f, 0.6f, 1f);

        // What the panel is built from, taken from the first game menu seen.
        private static GameObject _kit;
        private static GameObject _buttonTemplate;
        private static TMP_FontAsset _scriptFont, _bodyFont, _plainFont;
        private static Sprite _fieldSprite;

        // The buttons put into the game's menus; they go with their scene.
        private static Button _titleEntry, _worldEntry;
        private static InGameMenu _menu;
        private static GameObject _optionButton;
        private static RectTransform _menuArea;
        private static float _bindAt;

        private static Canvas _canvas;
        private static GameObject _backdrop;
        private static RectTransform _window;
        private static Image _windowImage;
        private static TextMeshProUGUI _heading, _status, _address, _crew, _message, _hint, _slotsText;
        private static RectTransform _barFill;
        private static GameObject _bar, _nameRow, _hostRow, _slotsRow, _portRow, _addressRow;
        private static TMP_InputField _name, _host, _port;
        private static Button _join, _back, _hostButton, _rescue, _end, _yes, _no, _copy, _minus, _plus;
        private static TextMeshProUGUI _foundTitle;
        private static readonly GameObject[] _foundRows = new GameObject[FoundRows];
        private static readonly TextMeshProUGUI[] _foundTexts = new TextMeshProUGUI[FoundRows];
        private static readonly Button[] _foundJoins = new Button[FoundRows];
        private static LanHost[] _found = new LanHost[0];
        // Steam: friends who are in the game (join dialog), and who a host lets in (host page).
        private static TextMeshProUGUI _steamTitle;
        private static readonly GameObject[] _friendRows = new GameObject[FoundRows];
        private static readonly TextMeshProUGUI[] _friendTexts = new TextMeshProUGUI[FoundRows];
        private static readonly Button[] _friendJoins = new Button[FoundRows];
        private static readonly SteamFriendInfo[] NoFriends = new SteamFriendInfo[0];
        private static SteamFriendInfo[] _friends = NoFriends;
        private static float _friendsAt;
        private static GameObject _steamRow;
        private static Button _steamMode, _copyId;

        private static Place _place = Place.Closed;
        private static string _note = "";
        private static bool _confirm;
        private static int _slots;
        private static string _lanAddress = "";
        private static float _refreshAt;
        private static bool _opening;

        // How the last connection ended, for a player who did not end it.
        private static bool _wasClient, _report, _userEnded;
        private static string _pending;

        private static bool _typing, _typingBefore;
        private static int _failures, _inputErrors;

        /// <summary>The panel could not be built or drawn; the debug window offers the session buttons.</summary>
        public static bool Broken { get; private set; }

        public static bool Open => _place != Place.Closed;

        /// <summary>The join dialog is showing: the games of the local network are wanted in it.</summary>
        public static bool WantsLanSearch => _place == Place.Title;

        /// <summary>The game's ESC menu of the loaded world is showing.</summary>
        public static bool GameMenuOpen
        {
            get
            {
                DataPersistenceManager dp = GameAccess.Dp;
                return dp != null && dp.mainMenu != null && dp.mainMenu.activeInHierarchy;
            }
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(InGameMenu), "FreezeAll"),
                prefix: new HarmonyMethod(typeof(CoopGameUI), nameof(FreezePrefix)));
            harmony.Patch(AccessTools.Method(typeof(InGameMenu), "ClearMenuChildren"),
                postfix: new HarmonyMethod(typeof(CoopGameUI), nameof(PageChanged)));
            harmony.Patch(AccessTools.Method(typeof(GameManager), "LoadGame"),
                prefix: new HarmonyMethod(typeof(CoopGameUI), nameof(TitleStartPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GameManager), "NewGame"),
                prefix: new HarmonyMethod(typeof(CoopGameUI), nameof(TitleStartPrefix)));
        }

        private static void Report(Exception error) => Plugin.Log.LogError("[Co-op UI] " + error);

        // The game's menu makes every body kinematic. A guest's world is the host's and goes on.
        private static bool FreezePrefix() => PatchGuard.Prefix(
            () => CoopBehaviour.Instance == null || !CoopBehaviour.Instance.Net.IsClient, Report);

        // The game put another page (options, tasks, the log) where the co-op page is shown.
        private static void PageChanged() => PatchGuard.Run(() =>
        {
            if (_place == Place.World && !_opening) Close();
        }, Report);

        // While a join is under way the title screen must not start a world of its own. The load
        // of the host's world is the one start that passes.
        private static bool TitleStartPrefix() => PatchGuard.Prefix(() =>
        {
            CoopBehaviour coop = CoopBehaviour.Instance;
            if (coop == null || !GameAccess.AtMainMenu) return true;
            if (coop.World.State == GuestWorldState.Loading) return true;
            return !coop.Net.IsClient && !coop.Net.Closing && coop.World.State != GuestWorldState.Leaving;
        }, Report);

        // -----------------------------------------------------------------
        // Input: runs whatever becomes of the panel
        // -----------------------------------------------------------------

        public static void TickInput(CoopBehaviour coop)
        {
            try
            {
                bool world = GameAccess.InWorld;
                bool guestMenu = world && coop.Net.IsClient && GameMenuOpen;
                if (guestMenu)
                {
                    // The game's menu stops time. Not on a guest: the host's world does not wait.
                    if (Time.timeScale == 0f) Time.timeScale = 1f;
                    GameAccess.Dp.paused = false;
                }
                // A guest's player would walk on under the open menu; typed letters are not for the game.
                InputBlock.Set(InputBlock.Reason.GameMenu, guestMenu || (world && _place == Place.World && _typing));
                if (world && InputBlock.Applied && !coop.MenuOpen && !_typingBefore) MenuKey();
                _typingBefore = _typing;
            }
            catch (Exception e)
            {
                if (_inputErrors++ < 5) Plugin.Log.LogError("[Co-op UI] Input failed: " + e);
            }
        }

        // The game reads its menu key in PlayerHandler.MyInput, which does not run while the mod
        // holds the player's input (the menu on a guest, the host's pause). Then the same is done
        // here, so a guest can always reach the menu — and Leave session in it.
        private static void MenuKey()
        {
            InteractionController controller = InteractionController.interactionInstance;
            PlayerInput input = controller != null ? controller.GetInputController() : null;
            InputAction action = input != null && input.actions != null ? input.actions.FindAction("Menu") : null;
            if (action == null || !action.WasPressedThisFrame()) return;

            List<IMenuItem> menus = controller.FindAllUIMenus();
            if (menus != null && menus.Count > 0)
            {
                foreach (IMenuItem item in menus) item.CloseMenu();
                return;
            }
            GameObject dialog = GameAccess.Dp.mainMenu;
            InGameMenu menu = dialog != null ? dialog.GetComponent<InGameMenu>() : null;
            if (menu == null) return;
            dialog.SetActive(true);
            menu.openedFromMenu = false;
            menu.HideForDialog();
        }

        // -----------------------------------------------------------------
        // Panel
        // -----------------------------------------------------------------

        public static void TickPanel(CoopBehaviour coop)
        {
            if (Broken) return;
            try
            {
                Bind(coop);
                Watch(coop);
                if (_worldEntry != null && _optionButton != null)
                {
                    // The game hides its buttons on the tasks page; the co-op one goes with them.
                    bool shown = _optionButton.activeSelf;
                    if (_worldEntry.gameObject.activeSelf != shown) _worldEntry.gameObject.SetActive(shown);
                }

                if (_place == Place.Title && (!GameAccess.AtMainMenu || coop.World.State == GuestWorldState.Loading ||
                                              coop.World.State == GuestWorldState.InWorld)) Close();
                if (_place == Place.World && (!GameAccess.InWorld || !GameMenuOpen)) Close();
                if (_place == Place.Closed) { _typing = false; return; }

                _typing = _name.isFocused || _host.isFocused || _port.isFocused;
                Keyboard keyboard = Keyboard.current;
                if (_place == Place.Title && keyboard != null && keyboard.escapeKey.wasPressedThisFrame &&
                    !_typingBefore && !coop.MenuOpen) BackOrCancel();
                if (_place != Place.Closed && Time.unscaledTime >= _refreshAt) Refresh(coop);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // One failure takes the panel down to be built anew; a panel that keeps failing is given up.
        private static void Fail(Exception error)
        {
            Plugin.Log.LogError("[Co-op UI] " + error);
            _failures++;
            try { DestroyPanel(); } catch { }
            if (_failures < MaxFailures) return;
            Broken = true;
            try
            {
                if (_titleEntry != null) _titleEntry.gameObject.SetActive(false);
                if (_worldEntry != null) _worldEntry.gameObject.SetActive(false);
            }
            catch { }
            Plugin.Log.LogWarning("[Co-op UI] The co-op menu is switched off after " + _failures +
                                  " failures; host and join from the debug window (" + Plugin.MenuKey.Value + ")");
        }

        // The menus are looked for, not waited for: twice a second until the scene's one is found.
        private static void Bind(CoopBehaviour coop)
        {
            if (Time.unscaledTime < _bindAt) return;
            _bindAt = Time.unscaledTime + 0.5f;
            if (GameAccess.AtMainMenu)
            {
                if (_titleEntry != null) return;
                NewGame start = UnityEngine.Object.FindFirstObjectByType<NewGame>();
                Button source = start != null ? start.GetComponent<Button>() : null;
                if (source == null || !EnsureKit(coop, source)) return;
                _titleEntry = Entry(source, () => Show(Place.Title));
                PlaceTitleEntry((RectTransform)_titleEntry.transform, (RectTransform)source.transform);
                Plugin.Log.LogInfo("[Co-op UI] Title screen button added");
            }
            else if (GameAccess.InWorld)
            {
                if (_worldEntry != null) return;
                GameObject dialog = GameAccess.Dp.mainMenu;
                InGameMenu menu = dialog != null ? dialog.GetComponent<InGameMenu>() : null;
                if (menu == null) return;
                var source = (Button)AccessTools.Field(typeof(InGameMenu), "optionButton").GetValue(menu);
                var area = (GameObject)AccessTools.Field(typeof(InGameMenu), "menuSidebar").GetValue(menu);
                if (source == null || area == null || !EnsureKit(coop, source)) return;
                _menu = menu;
                _optionButton = source.gameObject;
                _menuArea = area.transform as RectTransform;
                _worldEntry = Entry(source, OpenWorldPage);
                // The buttons are a layout group: the place in it is the place on screen.
                _worldEntry.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
                Plugin.Log.LogInfo("[Co-op UI] Game menu button added");
            }
        }

        private static bool _kitWarned;

        private static bool EnsureKit(CoopBehaviour coop, Button source)
        {
            if (_kit != null) return true;
            TMP_Text label = source.GetComponentInChildren<TMP_Text>(true);
            var image = source.targetGraphic as Image;
            if (label == null || image == null)
            {
                if (!_kitWarned) Plugin.Log.LogWarning("[Co-op UI] The game's button '" + source.name + "' is not built as expected; no co-op button here");
                _kitWarned = true;
                return false;
            }
            _kit = new GameObject("Coop UI kit");
            _kit.SetActive(false);
            _kit.transform.SetParent(coop.transform, false);

            _scriptFont = label.font;
            _bodyFont = FindFont("PlaywriteAUSA-VariableFont_wght SDF") ?? TMP_Settings.defaultFontAsset ?? _scriptFont;
            _plainFont = FindFont("LiberationSans SDF") ?? TMP_Settings.defaultFontAsset ?? _bodyFont;
            _fieldSprite = FindSprite("InputFieldBackground") ?? image.sprite;

            _buttonTemplate = Strip(source);
            _buttonTemplate.name = "Coop button";
            TMP_Text caption = _buttonTemplate.GetComponentInChildren<TMP_Text>(true);
            caption.fontSize = 24f;
            caption.fontStyle = FontStyles.Bold;
            return true;
        }

        private static TMP_FontAsset FindFont(string name)
        {
            foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (font.name == name) return font;
            return null;
        }

        private static Sprite FindSprite(string name)
        {
            foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                if (sprite.name == name) return sprite;
            return null;
        }

        // A copy of a game button that does nothing of the game's: made under an inactive parent so
        // none of its scripts wakes up, then left with the picture, the caption and an empty click.
        private static GameObject Strip(Button source)
        {
            GameObject copy = UnityEngine.Object.Instantiate(source.gameObject, _kit.transform);
            foreach (MonoBehaviour script in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (script == null || script is Button || script is Image || script is TMP_Text) continue;
                UnityEngine.Object.DestroyImmediate(script);
            }
            copy.GetComponent<Button>().onClick = new Button.ButtonClickedEvent();
            copy.SetActive(true);
            return copy;
        }

        private static Button Entry(Button source, Action click)
        {
            GameObject copy = Strip(source);
            copy.name = "Coop entry";
            copy.GetComponentInChildren<TMP_Text>(true).text = "Co-op";
            copy.transform.SetParent(source.transform.parent, false);
            var button = copy.GetComponent<Button>();
            button.onClick.AddListener(() => Guarded(click));
            return button;
        }

        // The title's buttons stand at fixed heights. Co-op takes the place under "New"; what was
        // below moves one step down.
        private static void PlaceTitleEntry(RectTransform entry, RectTransform source)
        {
            float y = source.anchoredPosition.y;
            float step = 0f;
            var below = new List<RectTransform>();
            foreach (Transform sibling in source.parent)
            {
                var rect = sibling as RectTransform;
                if (rect == null || rect == entry || rect == source || sibling.GetComponent<Button>() == null) continue;
                float gap = Mathf.Abs(rect.anchoredPosition.y - y);
                if (gap > 1f && (step == 0f || gap < step)) step = gap;
                if (rect.anchoredPosition.y < y - 1f) below.Add(rect);
            }
            if (step == 0f) step = source.rect.height + 10f;
            foreach (RectTransform rect in below) rect.anchoredPosition -= new Vector2(0f, step);
            entry.anchoredPosition = source.anchoredPosition - new Vector2(0f, step);
            entry.SetSiblingIndex(source.GetSiblingIndex() + 1);
        }

        private static void OpenWorldPage()
        {
            if (_menu == null) return;
            // As the game's own buttons do: whatever page is showing makes room first.
            _opening = true;
            try
            {
                AccessTools.Method(typeof(InGameMenu), "ClearMenuChildren").Invoke(_menu, null);
                object tasks = AccessTools.Field(typeof(InGameMenu), "tph").GetValue(_menu);
                if (tasks != null) AccessTools.Method(tasks.GetType(), "ToggleMenuShowing").Invoke(tasks, new object[] { false });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Co-op UI] Could not clear the game menu's page: " + (e.InnerException ?? e).Message);
            }
            finally { _opening = false; }
            Show(Place.World);
        }

        // A player who did not end the connection is told how it ended.
        private static void Watch(CoopBehaviour coop)
        {
            bool client = coop.Net.IsClient;
            if (client && coop.Participating) _report = true;
            if (_wasClient && !client)
            {
                if (_report && !_userEnded && !string.IsNullOrEmpty(coop.Notice)) _pending = coop.Notice;
                _report = false;
                _userEnded = false;
            }
            _wasClient = client;

            if (_pending == null || !GameAccess.AtMainMenu || _titleEntry == null ||
                coop.World.State != GuestWorldState.None) return;
            string text = _pending;
            _pending = null;
            if (_place != Place.Title) Show(Place.Title);
            Say(text);
        }

        /// <summary>A join started outside the dialog ("Join Game" in Steam) is shown as the dialog's own.</summary>
        public static void ShowJoin(string note)
        {
            if (Broken) return;
            try
            {
                _userEnded = false;
                _report = true;
                if (_titleEntry == null) return;   // the title screen is not found yet; Watch reports a failure
                if (_place != Place.Title) Show(Place.Title);
                Say(CoopBehaviour.Instance.Net.IsClient ? "" : note);
            }
            catch (Exception e) { Fail(e); }
        }

        private static void Show(Place place)
        {
            CoopBehaviour coop = CoopBehaviour.Instance;
            if (_canvas == null) Build(coop);
            // Before the name is shown: a name never set becomes the Steam one.
            coop.SteamReady();
            _friendsAt = 0f;
            _place = place;
            _confirm = false;
            _note = "";
            _name.text = Plugin.PlayerName.Value;
            _host.text = Plugin.JoinIp.Value;
            _port.text = Plugin.Port.Value.ToString();
            _slots = Mathf.Clamp(Plugin.MaxClients.Value, 1, Limits.RosterEntries - 1);
            _lanAddress = LanAddress();

            bool title = place == Place.Title;
            _backdrop.SetActive(title);
            _windowImage.enabled = title;
            _canvas.gameObject.SetActive(true);
            Refresh(coop);
            Select(title ? (Selectable)_host : coop.Net.Role == Role.None ? _hostButton : _rescue);
        }

        private static void Close()
        {
            _place = Place.Closed;
            _confirm = false;
            _typing = false;
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        private static void DestroyPanel()
        {
            _place = Place.Closed;
            _typing = false;
            if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject);
            _canvas = null;
        }

        public static void Dispose()
        {
            DestroyPanel();
            if (_titleEntry != null) UnityEngine.Object.Destroy(_titleEntry.gameObject);
            if (_worldEntry != null) UnityEngine.Object.Destroy(_worldEntry.gameObject);
            if (_kit != null) UnityEngine.Object.Destroy(_kit);
            _titleEntry = _worldEntry = null;
            _kit = null;
        }

        // -----------------------------------------------------------------
        // What the buttons do
        // -----------------------------------------------------------------

        // A click must not throw into the game's event system.
        private static void Guarded(Action action)
        {
            if (Broken) return;
            try { action(); _refreshAt = 0f; }
            catch (Exception e) { Fail(e); }
        }

        private static void Say(string text)
        {
            _note = text ?? "";
            _refreshAt = 0f;
        }

        private static void KeepName()
        {
            string name = _name.text.Trim();
            if (name.Length > 0) Plugin.PlayerName.Value = name;
            else _name.text = Plugin.PlayerName.Value;
        }

        private static void Join()
        {
            CoopBehaviour coop = CoopBehaviour.Instance;
            string address = _host.text.Trim();
            if (address.Length == 0) { Say("Enter the host's address"); Select(_host); return; }
            KeepName();
            Plugin.JoinIp.Value = address;
            _userEnded = false;
            _report = true;
            Say(coop.Join(address) ? "" : coop.Notice);
        }

        private static void Host()
        {
            CoopBehaviour coop = CoopBehaviour.Instance;
            if (!int.TryParse(_port.text.Trim(), out int port) || port < 1 || port > 65535)
            {
                Say("The port is a number from 1 to 65535");
                Select(_port);
                return;
            }
            KeepName();
            Plugin.Port.Value = port;
            Plugin.MaxClients.Value = _slots;
            // Steam asked for and not there: the world is hosted by address, and the player is told why.
            bool hosted = coop.Host();
            Say(hosted && coop.Net.OverSteam == Plugin.SteamEnabled.Value ? "" : coop.Notice);
            _lanAddress = LanAddress();
        }

        // A Steam friend who hosts: joined by the Steam id, with no address at all.
        private static void JoinFriend(int row)
        {
            if (row >= _friends.Length) return;
            CoopBehaviour coop = CoopBehaviour.Instance;
            KeepName();
            _userEnded = false;
            _report = true;
            Say(coop.JoinSteam(_friends[row].Id) ? "" : coop.Notice);
        }

        // Who may come through Steam. Before hosting: off, anyone, friends only. A session that
        // runs over Steam can change between the last two; switching Steam off needs a new session.
        private static void SteamMode()
        {
            CoopBehaviour coop = CoopBehaviour.Instance;
            if (coop.Net.IsHost)
            {
                if (!coop.Net.OverSteam) return;
                Plugin.SteamFriendsOnly.Value = !coop.Net.SteamFriendsOnly;
                coop.Net.SetSteamFriendsOnly(Plugin.SteamFriendsOnly.Value);
                _lanAddress = LanAddress();
            }
            else if (!Plugin.SteamEnabled.Value)
            {
                Plugin.SteamEnabled.Value = true;
                Plugin.SteamFriendsOnly.Value = false;
                coop.SteamReady();
            }
            else if (!Plugin.SteamFriendsOnly.Value) Plugin.SteamFriendsOnly.Value = true;
            else Plugin.SteamEnabled.Value = Plugin.SteamFriendsOnly.Value = false;
            Say("");
        }

        // A game found on the local network: its address goes into the field, as if typed.
        private static void JoinFound(int row)
        {
            if (row >= _found.Length) return;
            LanHost found = _found[row];
            _host.text = found.Port == Plugin.Port.Value ? found.Address : found.Address + ":" + found.Port;
            Join();
        }

        private static void Step(int by)
        {
            _slots = Mathf.Clamp(_slots + by, 1, Limits.RosterEntries - 1);
        }

        private static void End(bool confirmed)
        {
            CoopBehaviour coop = CoopBehaviour.Instance;
            _confirm = false;
            if (!confirmed) { Say(""); Select(_rescue); return; }
            bool guest = coop.Net.IsClient;
            _userEnded = true;
            coop.Disconnect();
            // A guest's world goes with the session; the host's stays, and so does the page.
            if (guest) Close(); else Say("The session has ended");
        }

        private static void BackOrCancel()
        {
            CoopBehaviour coop = CoopBehaviour.Instance;
            if (_place == Place.Title && coop.Net.IsClient)
            {
                _userEnded = true;
                coop.Disconnect();
                Say("");
                return;
            }
            KeepName();
            Close();
        }

        private static void Select(Selectable target)
        {
            EventSystem events = EventSystem.current;
            if (events == null || target == null || !target.gameObject.activeInHierarchy) return;
            events.SetSelectedGameObject(target.gameObject);
        }

        // -----------------------------------------------------------------
        // What is shown
        // -----------------------------------------------------------------

        private static void Refresh(CoopBehaviour coop)
        {
            _refreshAt = Time.unscaledTime + 0.2f;
            CoopNet net = coop.Net;
            bool title = _place == Place.Title;
            bool idle = net.Role == Role.None;
            bool linking = title && !idle;
            bool session = !title && !idle;
            bool hosting = session && net.IsHost;
            if (!session) _confirm = false;
            string blocker = !idle ? null : title ? coop.JoinBlocker() : coop.HostBlocker();

            _heading.text = title ? "Join a crew" : hosting ? "Hosting" : session ? "In a crew" : "Co-op";

            float progress = -1f;
            string status = "";
            if (linking)
            {
                if (coop.World.State == GuestWorldState.Receiving)
                {
                    progress = Mathf.Clamp01(coop.World.ReceiveProgress);
                    status = "Receiving the world... " + Mathf.RoundToInt(progress * 100f) + "%";
                }
                else if (net.State == LinkState.Connected) status = "Connected. The host is packing the world...";
                else status = "Connecting to " + Plain(net.HostLabel) + (net.OverSteam ? " over Steam..." : "...");
            }
            else if (hosting)
            {
                SessionMemberInfo[] roster = net.RosterSnapshot;
                status = (roster.Length - 1) + " of " + net.MaxClients + " guests aboard";
                if (coop.Pause.Active) status += "\nPaused while a player loads the world.";
            }
            else if (session)
            {
                status = "You sail in " + HostName(net) + "'s world.";
                if (coop.HostPaused) status += "\nThe host has paused the game.";
            }
            _status.text = status;
            _status.gameObject.SetActive(status.Length > 0);
            _bar.SetActive(progress >= 0f);
            if (progress >= 0f) _barFill.anchorMax = new Vector2(progress, 1f);

            _nameRow.SetActive(idle);
            _hostRow.SetActive(idle && title);
            Found(coop, idle && title, blocker == null);
            Friends(idle && title, blocker == null);
            _slotsRow.SetActive(idle && !title);
            _portRow.SetActive(idle && !title);
            _slotsText.text = _slots.ToString();
            _minus.interactable = _slots > 1;
            _plus.interactable = _slots < Limits.RosterEntries - 1;

            bool overSteam = hosting && net.OverSteam;
            bool steamOn = hosting ? overSteam : Plugin.SteamEnabled.Value;
            bool friendsOnly = hosting ? net.SteamFriendsOnly : steamOn && Plugin.SteamFriendsOnly.Value;
            _steamRow.SetActive((idle && !title) || overSteam);
            Caption(_steamMode, !steamOn ? "Off" : friendsOnly ? "Friends only" : "Anyone");
            _copyId.gameObject.SetActive(overSteam);

            // Friends only: nobody is let in by address, so none is offered.
            _addressRow.SetActive(hosting && !friendsOnly);
            if (hosting) _address.text = _lanAddress.Length > 0 ? _lanAddress + ":" + Plugin.Port.Value : "port " + Plugin.Port.Value;
            _copy.gameObject.SetActive(hosting && _lanAddress.Length > 0);

            _crew.gameObject.SetActive(session);
            if (session) _crew.text = Crew(net);

            string message = _note;
            if (_confirm)
                message = hosting ? "Stop hosting? Your guests go back to their title screen. Your world stays loaded."
                                  : "Leave the crew? You go back to the title screen.";
            else if (message.Length == 0 && blocker != null) message = blocker;
            _message.text = message;
            _message.gameObject.SetActive(message.Length > 0);

            _join.gameObject.SetActive(title && idle);
            _join.interactable = blocker == null;
            _back.gameObject.SetActive(title);
            Caption(_back, linking ? "Cancel" : "Back");
            _hostButton.gameObject.SetActive(!title && idle);
            _hostButton.interactable = blocker == null;
            _rescue.gameObject.SetActive(session && !_confirm);
            _rescue.interactable = session && coop.Rescue.Blocker() == null;
            _end.gameObject.SetActive(session && !_confirm);
            Caption(_end, hosting ? "Stop hosting" : "Leave the crew");
            _yes.gameObject.SetActive(_confirm);
            _no.gameObject.SetActive(_confirm);
            Caption(_yes, hosting ? "Stop hosting" : "Leave");
            Caption(_no, hosting ? "Keep hosting" : "Stay");

            _hint.text = title
                ? (idle ? "Steam friends who host, and games on your network, are listed by themselves. For any other, type the host's address or Steam ID. You sail in the host's world; your own saves are not touched."
                        : "")
                : hosting ? (friendsOnly ? "Only your Steam friends can join: from their Co-op dialog, or with Join Game in the Steam friends list."
                    : overSteam ? "Steam friends join from their Co-op dialog, or with Join Game in the Steam friends list. Anyone else needs your Steam ID."
                    : "On another network a guest needs your public address, and UDP port " + Plugin.Port.Value + " forwarded to this PC.")
                : session ? "Only the host saves the world."
                : !steamOn ? "Friends join from their title screen: Co-op, then your address."
                : !SteamLink.Ready ? SteamLink.Error + " You can still host by address."
                : friendsOnly ? "Only your Steam friends will be able to join, and nobody by address."
                : "Steam friends will see your game in their Co-op dialog. Anyone else joins by your Steam ID or your address.";
            _hint.gameObject.SetActive(_hint.text.Length > 0);

            Arrange(title);
            Navigate(title);
        }

        // The games heard on the local network, each with a Join of its own. A game that cannot be
        // joined is listed too, with the reason: that it is seen at all tells the player the
        // network is fine.
        private static void Found(CoopBehaviour coop, bool shown, bool mayJoin)
        {
            _found = shown ? coop.LanSearch.Hosts : new LanHost[0];
            _foundTitle.gameObject.SetActive(shown);
            if (shown) _foundTitle.text = _found.Length == 0 ? "Looking for games on your network..." : "On your network";
            for (int i = 0; i < FoundRows; i++)
            {
                bool used = i < _found.Length;
                _foundRows[i].SetActive(used);
                if (!used) continue;
                LanHost found = _found[i];
                string why = found.ProtocolVersion != Protocol.Version ? "another mod version (" + Plain(found.ModVersion) + ")"
                    : found.Players >= found.MaxPlayers ? "full"
                    : !found.Accepting ? "no world loaded yet"
                    : "";
                string name = found.HostName.Length > 0 ? Plain(found.HostName) : "Host";
                _foundTexts[i].text = name + "   <alpha=#B0>" + found.Players + "/" + found.MaxPlayers + "   " +
                                      (why.Length > 0 ? why : found.Address) + "<alpha=#FF>";
                _foundJoins[i].interactable = mayJoin && why.Length == 0;
            }
        }

        // Steam friends who are in the game, the hosts first. One who does not host is listed too:
        // that the friend is seen at all tells the player Steam is fine.
        private static void Friends(bool shown, bool mayJoin)
        {
            shown = shown && Plugin.SteamEnabled.Value;
            if (!shown) _friends = NoFriends;
            else if (Time.unscaledTime >= _friendsAt)
            {
                _friendsAt = Time.unscaledTime + 1f;
                _friends = SteamLink.FriendsInGame();
            }
            _steamTitle.gameObject.SetActive(shown);
            if (shown)
                _steamTitle.text = !SteamLink.Ready ? SteamLink.Error
                    : _friends.Length == 0 ? "No Steam friends are in the game right now"
                    : "Steam friends in the game";
            for (int i = 0; i < FoundRows; i++)
            {
                bool used = i < _friends.Length;
                _friendRows[i].SetActive(used);
                if (!used) continue;
                SteamFriendInfo friend = _friends[i];
                string what = !friend.Hosting ? "not hosting" : friend.SameVersion ? "hosting" : "another mod version";
                _friendTexts[i].text = (friend.Name.Length > 0 ? Plain(friend.Name) : "Friend") + "   <alpha=#B0>" + what + "<alpha=#FF>";
                _friendJoins[i].interactable = mayJoin && friend.Hosting && friend.SameVersion;
            }
        }

        private static string HostName(CoopNet net)
        {
            foreach (SessionMemberInfo member in net.RosterSnapshot)
                if (member.IsHost) return Plain(member.Name);
            return "the host";
        }

        private static string Crew(CoopNet net)
        {
            var rows = new StringBuilder();
            foreach (SessionMemberInfo member in net.RosterSnapshot)
            {
                if (rows.Length > 0) rows.Append('\n');
                rows.Append(Plain(member.Name));
                string tag = member.IsHost ? "host" : "";
                if (member.NetId == net.MyNetId) tag += (tag.Length > 0 ? ", " : "") + "you";
                if (member.State != MemberJoinState.Ready) tag += (tag.Length > 0 ? ", " : "") + "joining";
                if (tag.Length > 0) rows.Append("   <alpha=#B0>").Append(tag).Append("<alpha=#FF>");
            }
            return rows.ToString();
        }

        // A name comes from another player; it must not be read as text markup.
        private static string Plain(string name) => (name ?? "").Replace('<', '‹').Replace('>', '›');

        private static void Caption(Button button, string text) => button.GetComponentInChildren<TMP_Text>(true).text = text;

        // On the title screen the window is a dialog in the middle. In the world it lies on the
        // game menu's own page area, wherever the game has put that at this resolution.
        private static void Arrange(bool title)
        {
            if (title)
            {
                _window.pivot = new Vector2(0.5f, 0.5f);
                _window.sizeDelta = new Vector2(480f, _window.sizeDelta.y);
                _window.anchoredPosition = Vector2.zero;
                return;
            }
            float scale = _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
            var corners = new Vector3[4];
            Vector3 topLeft = new Vector3(Screen.width * 0.19f, Screen.height, 0f);
            float width = Screen.width * 0.8f;
            if (_menuArea != null)
            {
                _menuArea.GetWorldCorners(corners);
                if (corners[2].x - corners[1].x > 300f * scale)
                {
                    topLeft = corners[1];
                    width = corners[2].x - corners[1].x;
                }
            }
            _window.pivot = new Vector2(0f, 1f);
            _window.sizeDelta = new Vector2(Mathf.Min(520f, width / scale - 40f), _window.sizeDelta.y);
            _window.position = topLeft + new Vector3(20f * scale, -20f * scale, 0f);
        }

        // The title dialog is modal: its controls lead only to each other, so the keys cannot walk
        // off to the title's buttons under it. In the world the page is one of the menu's own and
        // the game's buttons beside it stay in reach.
        private static void Navigate(bool title)
        {
            var all = new List<Selectable> { _name, _host };
            all.AddRange(_foundJoins);
            all.AddRange(_friendJoins);
            all.AddRange(new Selectable[] { _port, _minus, _plus, _copy, _steamMode, _copyId, _hostButton, _join, _back, _rescue, _end, _yes, _no });
            EventSystem events = EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            if (!title)
            {
                foreach (Selectable one in all) one.navigation = Navigation.defaultNavigation;
                // The selected button has just been replaced by another (Leave by Stay): go with it.
                if (selected != null && !selected.activeInHierarchy && selected.transform.IsChildOf(_window))
                    Select(_confirm ? _no : _hostButton.gameObject.activeSelf ? _hostButton : _rescue);
                return;
            }
            var open = new List<Selectable>();
            foreach (Selectable one in all)
                if (one.gameObject.activeInHierarchy && one.IsInteractable()) open.Add(one);
            for (int i = 0; i < open.Count; i++)
            {
                Selectable before = open[(i + open.Count - 1) % open.Count], after = open[(i + 1) % open.Count];
                // In a text field the side arrows move the caret.
                bool field = open[i] is TMP_InputField;
                open[i].navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = before,
                    selectOnDown = after,
                    selectOnLeft = field ? null : before,
                    selectOnRight = field ? null : after,
                };
            }
            if (events == null || open.Count == 0) return;
            if (selected != null && selected.activeInHierarchy && selected.transform.IsChildOf(_window)) return;
            events.SetSelectedGameObject((_join.gameObject.activeInHierarchy && _join.IsInteractable() ? _join : _back).gameObject);
        }

        // The address a guest on the same network types. A PC has many adapters, and a VPN tunnel
        // or a virtual switch often holds the default route, so the route is no guide: the real
        // network card (Wi-Fi or Ethernet) that has a gateway is the one, and a private address
        // on it. What the system itself would pick was tried and gave the tunnel's address.
        private static string LanAddress()
        {
            try
            {
                string best = "";
                int bestScore = int.MinValue;
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    int score = 0;
                    switch (adapter.NetworkInterfaceType)
                    {
                        case NetworkInterfaceType.Ethernet:
                        case NetworkInterfaceType.GigabitEthernet:
                        case NetworkInterfaceType.FastEthernetT:
                        case NetworkInterfaceType.FastEthernetFx:
                        case NetworkInterfaceType.Wireless80211:
                            score += 4;
                            break;
                    }
                    foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                    {
                        if (gateway.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        score += 2;
                        break;
                    }
                    string label = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
                    foreach (string word in VirtualAdapterWords)
                    {
                        if (!label.Contains(word)) continue;
                        score -= 8;
                        break;
                    }
                    foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                    {
                        IPAddress address = unicast.Address;
                        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                        byte[] b = address.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue;   // no address was given to this adapter
                        bool home = b[0] == 192 && b[1] == 168 || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
                        int total = score + (home ? 1 : 0);
                        if (total <= bestScore) continue;
                        bestScore = total;
                        best = address.ToString();
                    }
                }
                return best;
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo("[Co-op UI] This PC's address could not be read: " + e.Message);
                return "";
            }
        }

        private static readonly string[] VirtualAdapterWords =
        {
            "virtual", "hyper-v", "vethernet", "vmware", "vpn", "tun", "tap", "wintun", "wireguard", "tailscale",
            "zerotier", "hamachi", "docker", "wsl", "loopback", "bluetooth",
        };
        // -----------------------------------------------------------------
        // Building
        // -----------------------------------------------------------------

        private static void Build(CoopBehaviour coop)
        {
            var root = new GameObject("Coop UI", typeof(RectTransform));
            root.transform.SetParent(coop.transform, false);
            _canvas = root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 50;   // over the game's menus (0 and 1)
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefWidth, RefHeight);
            scaler.matchWidthOrHeight = 1f;
            root.AddComponent<GraphicRaycaster>();

            // Behind the title dialog: dims the screen and takes the clicks meant for what is under it.
            RectTransform backdrop = Node(root.transform, "Backdrop");
            Stretch(backdrop);
            backdrop.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);
            _backdrop = backdrop.gameObject;

            _window = Node(root.transform, "Window");
            _window.anchorMin = _window.anchorMax = new Vector2(0.5f, 0.5f);
            _window.sizeDelta = new Vector2(480f, 100f);
            _windowImage = _window.gameObject.AddComponent<Image>();
            _windowImage.color = Slate;
            var column = _window.gameObject.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset(28, 28, 22, 24);
            column.spacing = 12f;
            column.childControlWidth = column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            _window.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _heading = Text(_window, "Heading", _scriptFont, 44f, Color.white);
            _heading.fontStyle = FontStyles.Bold;
            _status = Text(_window, "Status", _bodyFont, 16f, Color.white);

            RectTransform bar = Node(_window, "Progress");
            bar.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.45f);
            Size(bar.gameObject, -1f, 8f);
            _barFill = Node(bar, "Fill");
            _barFill.anchorMin = Vector2.zero;
            _barFill.anchorMax = new Vector2(0f, 1f);
            _barFill.offsetMin = _barFill.offsetMax = Vector2.zero;
            _barFill.gameObject.AddComponent<Image>().color = new Color(0.62f, 0.69f, 0.78f, 1f);
            _bar = bar.gameObject;

            _nameRow = Row(_window, "Your name");
            _name = Field(_nameRow.transform, Limits.Name, TMP_InputField.ContentType.Standard, -1f);
            _hostRow = Row(_window, "Host address");
            _host = Field(_hostRow.transform, 128, TMP_InputField.ContentType.Standard, -1f);
            _host.onSubmit.AddListener(_ => Guarded(Join));

            _foundTitle = Text(_window, "Found", _bodyFont, 13f, Soft);
            for (int i = 0; i < FoundRows; i++)
            {
                int row = i;
                RectTransform found = Node(_window, "Found " + i);
                var across = found.gameObject.AddComponent<HorizontalLayoutGroup>();
                across.spacing = 10f;
                across.childAlignment = TextAnchor.MiddleLeft;
                across.childControlWidth = across.childControlHeight = true;
                across.childForceExpandWidth = across.childForceExpandHeight = false;
                Size(found.gameObject, -1f, 34f);
                _foundTexts[i] = Text(found, "Game", _plainFont, 17f, Color.white);
                _foundTexts[i].textWrappingMode = TextWrappingModes.NoWrap;
                _foundTexts[i].overflowMode = TextOverflowModes.Ellipsis;
                Size(_foundTexts[i].gameObject, -1f, -1f).flexibleWidth = 1f;
                _foundJoins[i] = MakeButton(found, "Join", () => JoinFound(row), 90f);
                _foundRows[i] = found.gameObject;
            }

            _steamTitle = Text(_window, "Steam", _bodyFont, 13f, Soft);
            for (int i = 0; i < FoundRows; i++)
            {
                int row = i;
                RectTransform friend = Node(_window, "Friend " + i);
                var across = friend.gameObject.AddComponent<HorizontalLayoutGroup>();
                across.spacing = 10f;
                across.childAlignment = TextAnchor.MiddleLeft;
                across.childControlWidth = across.childControlHeight = true;
                across.childForceExpandWidth = across.childForceExpandHeight = false;
                Size(friend.gameObject, -1f, 34f);
                _friendTexts[i] = Text(friend, "Friend", _plainFont, 17f, Color.white);
                _friendTexts[i].textWrappingMode = TextWrappingModes.NoWrap;
                _friendTexts[i].overflowMode = TextOverflowModes.Ellipsis;
                Size(_friendTexts[i].gameObject, -1f, -1f).flexibleWidth = 1f;
                _friendJoins[i] = MakeButton(friend, "Join", () => JoinFriend(row), 90f);
                _friendRows[i] = friend.gameObject;
            }

            _slotsRow = Row(_window, "Guests");
            _minus = MakeButton(_slotsRow.transform, "-", () => Step(-1), 40f);
            _slotsText = Text(_slotsRow.transform, "Count", _plainFont, 20f, Color.white);
            _slotsText.alignment = TextAlignmentOptions.Center;
            Size(_slotsText.gameObject, 40f, -1f);
            _plus = MakeButton(_slotsRow.transform, "+", () => Step(1), 40f);

            _portRow = Row(_window, "UDP port");
            _port = Field(_portRow.transform, 5, TMP_InputField.ContentType.IntegerNumber, 100f);

            _addressRow = Row(_window, "Your address");
            _address = Text(_addressRow.transform, "Address", _plainFont, 20f, Color.white);
            Size(_address.gameObject, -1f, -1f).flexibleWidth = 1f;
            _copy = MakeButton(_addressRow.transform, "Copy", () =>
            {
                GUIUtility.systemCopyBuffer = _lanAddress + ":" + Plugin.Port.Value;
                Say("Address copied");
            }, 90f);

            _steamRow = Row(_window, "Steam");
            _steamMode = MakeButton(_steamRow.transform, "Anyone", SteamMode, 150f);
            _copyId = MakeButton(_steamRow.transform, "Copy ID", () =>
            {
                GUIUtility.systemCopyBuffer = SteamLink.MyId.ToString();
                Say("Steam ID copied");
            }, 100f);

            _crew = Text(_window, "Crew", _bodyFont, 16f, Color.white);
            _crew.lineSpacing = 12f;
            _message = Text(_window, "Message", _bodyFont, 15f, Warm);

            RectTransform buttons = Node(_window, "Buttons");
            var line = buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
            line.spacing = 12f;
            line.childControlWidth = line.childControlHeight = true;
            line.childForceExpandWidth = true;
            line.childForceExpandHeight = false;
            _join = MakeButton(buttons, "Join", Join, -1f);
            _hostButton = MakeButton(buttons, "Host this world", Host, -1f);
            _rescue = MakeButton(buttons, "Return to the ship", () => Say(CoopBehaviour.Instance.Rescue.ReturnToShip()), -1f);
            _end = MakeButton(buttons, "Leave the crew", () => _confirm = true, -1f);
            _yes = MakeButton(buttons, "Leave", () => End(true), -1f);
            _no = MakeButton(buttons, "Stay", () => End(false), -1f);
            _back = MakeButton(buttons, "Back", BackOrCancel, -1f);

            _hint = Text(_window, "Hint", _bodyFont, 13f, Soft);
            root.SetActive(false);
        }

        private static RectTransform Node(Transform parent, string name)
        {
            var go = new GameObject("Coop " + name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private static LayoutElement Size(GameObject go, float width, float height)
        {
            LayoutElement layout = go.GetComponent<LayoutElement>();
            if (layout == null) layout = go.AddComponent<LayoutElement>();
            if (width >= 0f) layout.minWidth = layout.preferredWidth = width;
            if (height >= 0f) layout.minHeight = layout.preferredHeight = height;
            return layout;
        }

        private static TextMeshProUGUI Text(Transform parent, string name, TMP_FontAsset font, float size, Color color)
        {
            var text = Node(parent, name).gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.Left;
            text.raycastTarget = false;
            text.text = "";
            return text;
        }

        // A line of the form: what it is on the left, the control (added by the caller) on the right.
        private static GameObject Row(Transform parent, string caption)
        {
            RectTransform row = Node(parent, caption);
            var line = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            line.spacing = 10f;
            line.childAlignment = TextAnchor.MiddleLeft;
            line.childControlWidth = line.childControlHeight = true;
            line.childForceExpandWidth = line.childForceExpandHeight = false;
            Size(row.gameObject, -1f, 34f);
            TextMeshProUGUI label = Text(row, "Label", _bodyFont, 15f, Color.white);
            label.text = caption;
            Size(label.gameObject, 140f, -1f);
            return row.gameObject;
        }

        private static Button MakeButton(Transform parent, string caption, Action click, float width)
        {
            GameObject copy = UnityEngine.Object.Instantiate(_buttonTemplate, parent);
            copy.name = "Coop " + caption;
            copy.GetComponentInChildren<TMP_Text>(true).text = caption;
            LayoutElement layout = Size(copy, width, 34f);
            if (width < 0f) layout.flexibleWidth = 1f;
            var button = copy.GetComponent<Button>();
            button.onClick.AddListener(() => Guarded(click));
            return button;
        }

        private static TMP_InputField Field(Transform parent, int limit, TMP_InputField.ContentType content, float width)
        {
            RectTransform box = Node(parent, "Field");
            // Put together while switched off: the field looks for its parts when it wakes up.
            box.gameObject.SetActive(false);
            var image = box.gameObject.AddComponent<Image>();
            image.sprite = _fieldSprite;
            image.type = _fieldSprite != null ? Image.Type.Sliced : Image.Type.Simple;
            LayoutElement layout = Size(box.gameObject, width, 32f);
            if (width < 0f) layout.flexibleWidth = 1f;

            RectTransform area = Node(box, "Text area");
            Stretch(area);
            area.offsetMin = new Vector2(10f, 4f);
            area.offsetMax = new Vector2(-10f, -4f);
            area.gameObject.AddComponent<RectMask2D>();
            TextMeshProUGUI text = Text(area, "Text", _plainFont, 18f, Ink);
            Stretch((RectTransform)text.transform);
            text.alignment = TextAlignmentOptions.MidlineLeft;

            var field = box.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = image;
            field.textViewport = area;
            field.textComponent = text;
            field.fontAsset = _plainFont;
            field.pointSize = 18f;
            field.characterLimit = limit;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.contentType = content;
            field.selectionColor = new Color(0.55f, 0.66f, 0.82f, 0.75f);
            field.caretColor = Ink;
            field.customCaretColor = true;
            box.gameObject.SetActive(true);
            return field;
        }
    }
}
