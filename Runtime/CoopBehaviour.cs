using System;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>
    /// The single MonoBehaviour that owns the loop: the session (<see cref="CoopNet"/>), every
    /// <c>*Sync</c> instance, the Harmony instance and the IMGUI surfaces. Lives for the whole
    /// process (DontDestroyOnLoad).
    /// </summary>
    public sealed partial class CoopBehaviour : MonoBehaviour
    {
        public static CoopBehaviour Instance { get; private set; }

        public CoopNet Net { get; private set; }
        internal WorldTransfer World { get; private set; }
        internal JoinPause Pause { get; private set; }
        internal PlayerSync Players { get; private set; }
        internal BoatSync Boats { get; private set; }
        internal EnvSync Env { get; private set; }
        internal ControlsSync Controls { get; private set; }
        internal PlayerRescue Rescue { get; private set; }
        internal ItemRegistry Items { get; private set; }
        internal EconomySync Economy { get; private set; }
        internal TaskSync Tasks { get; private set; }
        internal CargoSync Cargo { get; private set; }
        internal HuntSync Hunt { get; private set; }
        internal RowboatSync Rowboat { get; private set; }
        internal CrewSync Crew { get; private set; }
        internal ShipyardSync Shipyard { get; private set; }
        internal ChartSync Chart { get; private set; }
        internal AppearanceProfile Appearance { get; private set; }

        private Harmony _harmony;
        /// <summary>What the menu shows about the last thing that happened ("Connected", an error...).</summary>
        public string Notice { get; private set; } = "";

        /// <summary>Looks for hosts on the local network while the join dialog wants the list.</summary>
        public readonly LanSearch LanSearch = new LanSearch(message => Plugin.Log.LogInfo(message));
        private int _lanSearchErrors;
        private const int DefaultPort = 7777;

        private void Awake()
        {
            Instance = this;
            Protocol.Warn = message => Plugin.Log.LogWarning(message);
            SteamLink.Log = message => Plugin.Log.LogInfo(message);
            SteamLink.Warn = message => Plugin.Log.LogWarning(message);

            Net = new CoopNet(message => Plugin.Log.LogInfo(message))
            {
                ModVersion = Plugin.Version,
                GameVersion = Application.version ?? "",
                JoinBlocker = HostJoinBlocker,
            };
            Appearance = new AppearanceProfile(Plugin.PlayerAppearance);
            Net.PlayerAppearance = Appearance.Committed;
            World = new WorldTransfer(Net, message => Plugin.Log.LogInfo(message));
            Pause = new JoinPause();
            Players = new PlayerSync(Net);
            Boats = new BoatSync(Net);
            Env = new EnvSync(Net);
            Controls = new ControlsSync(Net);
            Rescue = new PlayerRescue();
            Items = new ItemRegistry(Net);
            Economy = new EconomySync(Net);
            Tasks = new TaskSync(Net);
            Cargo = new CargoSync(Net, Tasks);
            Hunt = new HuntSync(Net);
            Rowboat = new RowboatSync(Net, Boats);
            Crew = new CrewSync(Net);
            Shipyard = new ShipyardSync(Net);
            Chart = new ChartSync(Net);
            SceneManager.sceneUnloaded += Items.WorldUnloaded;
            SceneManager.sceneUnloaded += Hunt.WorldUnloaded;

            Net.OnClientReady += HostOnClientReady;
            Net.OnPlayerLeft += HostOnPlayerLeft;
            Net.OnAccepted += ack => Notice = "Connected to " + ack.HostName + ", receiving the world";
            Net.OnLinkLost += ClientOnLinkLost;
            Net.OnGameMessage += OnGameMessage;
            Net.OnRosterChanged += Players.OnRosterChanged;
            World.OnGuestAbort += reason => EndClientSession(reason);
            InitDev();

            _harmony = new Harmony(Plugin.Guid);
            Action<string> log = message => Plugin.Log.LogError(message);
            PatchHealth.Install("Save folder", () => SavePatches.InstallSaveDir(_harmony), log);
            // Without these two a guest would load its own save, or overwrite it with the host's world.
            PatchHealth.Install("Guest world", () => SavePatches.InstallGuestWorld(_harmony), log, required: true);
            PatchHealth.Install("Guest save block", () => SavePatches.InstallGuestSaveBlock(_harmony), log, required: true);
            PatchHealth.Install("Ship", () => BoatSync.Install(_harmony), log);
            PatchHealth.Install("Fast travel", () => BoatSync.InstallTravel(_harmony), log);
            PatchHealth.Install("Shipyard", () => ShipyardSync.Install(_harmony), log);
            // Without it: no rope trimming by looking at a rope, no door or lantern from a guest, no guest pumping.
            PatchHealth.Install("Controls", () => ControlsSync.Install(_harmony), log);
            PatchHealth.Install("Item catalog", () => ItemCatalog.Install(_harmony), log);
            // Without it things are still told apart, but carrying one is not shown to the others.
            PatchHealth.Install("Items", () => ItemRegistry.Install(_harmony), log);
            // Without it a tool is still shown in the other player's hand, but not its blows.
            PatchHealth.Install("Tools", () => ToolVisuals.Install(_harmony), log);
            // Without it a guest's own timer would eat from the shared stores a second time.
            PatchHealth.Install("Economy", () => EconomySync.Install(_harmony), log);
            PatchHealth.Install("Trade dialogs", () => EconomySync.InstallDialogs(_harmony), log);
            PatchHealth.Install("Fish sale", () => EconomySync.InstallFishSale(_harmony), log);
            // Without it the hold is not shared: two cargo panels at once would lose a task's counts.
            PatchHealth.Install("Cargo", () => CargoSync.Install(_harmony), log);
            // Without it leaks and trolling lines are not shared: a guest's copy would roll its own.
            PatchHealth.Install("Hull", () => ControlsSync.InstallHull(_harmony), log);
            // Without it a shot animal and its yield stay on the hunter's copy.
            PatchHealth.Install("Hunt", () => HuntSync.Install(_harmony), log);
            // Without it a guest's orders go to its own crew, whose work the host's ship never sees.
            PatchHealth.Install("Crew", () => CrewSync.Install(_harmony), log);
            PatchHealth.Install("Co-op UI", () => CoopGameUI.Install(_harmony), log);
            Plugin.Log.LogInfo("Patches: " + PatchHealth.Details);
        }

        // -----------------------------------------------------------------
        // Session control (the menu and the dev console call these)
        // -----------------------------------------------------------------

        /// <summary>Null when hosting may start now, otherwise why not.</summary>
        public string HostBlocker()
        {
            if (Net.Role != Role.None) return "A session is already active";
            if (Net.Closing) return "Finishing the previous connection";
            if (PatchHealth.Blocker != null) return "The mod does not fit this game version: " + PatchHealth.Blocker;
            if (WorldTransfer.GuestWorld) return "Still leaving the previous session";
            if (!GameAccess.InWorld) return "Load your world first, then host";
            return null;
        }

        /// <summary>Null when a join may start now, otherwise why not.</summary>
        public string JoinBlocker()
        {
            if (Net.Role != Role.None) return "A connection is already active";
            if (Net.Closing) return "Finishing the previous connection";
            if (PatchHealth.Blocker != null) return "The mod does not fit this game version: " + PatchHealth.Blocker;
            if (WorldTransfer.GuestWorld) return "Still leaving the previous session";
            if (!GameAccess.AtMainMenu) return "Join from the title screen";
            return null;
        }

        public bool Host()
        {
            string blocker = HostBlocker();
            if (blocker != null) { Notice = blocker; return false; }
            ApplyConfig();
            bool steam = Plugin.SteamEnabled.Value;
            if (steam) Net.StartSteamHost(Plugin.Port.Value, Plugin.SteamFriendsOnly.Value);
            else Net.StartHost(Plugin.Port.Value);
            // A host that asked for Steam and did not get it is up all the same, by address.
            Notice = !Net.IsHost || steam != Net.OverSteam ? Net.LastError
                : steam ? "Hosting over Steam and on port " + Plugin.Port.Value
                : "Hosting on port " + Plugin.Port.Value;
            return Net.IsHost;
        }

        /// <summary>Joins a host. The address is a name or an IP, with ":port" when it is not the configured one.</summary>
        public bool Join(string address)
        {
            string blocker = JoinBlocker();
            if (blocker != null) { Notice = blocker; return false; }
            ApplyConfig();
            string host = string.IsNullOrWhiteSpace(address) ? Plugin.JoinIp.Value.Trim() : address.Trim();
            // Seventeen digits are no address: it is the Steam ID a host copies from its Co-op page.
            ulong steamId = SteamJoinLink.ParseId(host);
            if (steamId != 0UL) return JoinSteam(steamId);
            int port = Plugin.Port.Value;
            // One colon only: more of them is an IPv6 address, not a port.
            int colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host.Substring(colon + 1), out int given) &&
                given >= 1 && given <= 65535)
            {
                port = given;
                host = host.Substring(0, colon);
            }
            Net.StartClient(host, port);
            Notice = Net.IsClient ? "Connecting to " + Net.HostLabel + " ..." : Net.LastError;
            return Net.IsClient;
        }

        /// <summary>Joins a host through Steam, by its SteamID64.</summary>
        public bool JoinSteam(ulong hostSteamId)
        {
            string blocker = JoinBlocker();
            if (blocker != null) { Notice = blocker; return false; }
            if (!SteamLink.EnsureReady()) { Notice = SteamLink.Error; return false; }
            // Steam would carry the packets back to this copy, and the attempt would end in half a minute of nothing.
            if (hostSteamId == SteamLink.MyId) { Notice = "That is your own Steam ID: ask the host for theirs"; return false; }
            AdoptSteamName();
            ApplyConfig();
            Net.StartSteamClient(hostSteamId);
            Notice = Net.IsClient ? "Connecting to " + Net.HostLabel + " over Steam ..." : Net.LastError;
            if (Net.IsClient) _steamHost = hostSteamId;
            return Net.IsClient;
        }

        // The Steam host of the last join that was started, to tell a repeated request from a new one.
        private ulong _steamHost;

        /// <summary>
        /// Starts Steam if the player wants it and it is not started yet. Asked again whenever the
        /// Co-op menu opens, so a player who started Steam after the game needs no restart.
        /// </summary>
        public bool SteamReady()
        {
            if (!Plugin.SteamEnabled.Value) return false;
            if (SteamLink.Ready) return true;
            if (!SteamLink.EnsureReady()) return false;
            AdoptSteamName();
            return true;
        }

        // A name the player never set is the Windows account's; the Steam name is the one friends know.
        private static void AdoptSteamName()
        {
            string persona = (SteamLink.MyName ?? "").Trim();
            if (persona.Length == 0 || Plugin.PlayerName.Value != (string)Plugin.PlayerName.DefaultValue) return;
            Plugin.PlayerName.Value = persona.Length > Limits.Name ? persona.Substring(0, Limits.Name) : persona;
        }

        // "Join Game" on a friend in Steam, or an accepted invite. Steam gives the host's id to a
        // running game through a callback, and to a game it starts on the command line.
        private const float SteamStartDelay = 3f;      // the game's own start comes first
        private const float SteamJoinPatience = 120f;  // how long a request waits for the title screen
        private bool _steamStartChecked, _steamJoinTold;
        private ulong _steamJoinHost;
        private float _steamJoinSince;

        private void TickSteamJoin()
        {
            float now = Time.realtimeSinceStartup;
            if (!_steamStartChecked && now >= SteamStartDelay)
            {
                _steamStartChecked = true;
                ulong launched = SteamJoinLink.Parse(Environment.GetCommandLineArgs());
                if (launched != 0UL)
                {
                    Plugin.Log.LogInfo("[Coop] Started by Steam to join " + launched);
                    _steamJoinHost = launched;
                    _steamJoinSince = now;
                }
                // Steam hands a join request only to a game that talks to it, so it is started
                // with the game and not with the first opening of the menu.
                SteamReady();
            }

            ulong asked = SteamLink.TakeJoinRequest();
            if (asked != 0UL)
            {
                _steamJoinHost = asked;
                _steamJoinSince = now;
                _steamJoinTold = false;
            }
            if (_steamJoinHost == 0UL) return;

            if (Net.Role != Role.None)
            {
                ulong again = _steamJoinHost;
                _steamJoinHost = 0UL;
                // The same host again is a second click in Steam, or Steam repeating itself: nothing to say.
                if (Net.IsClient && Net.OverSteam && again == _steamHost) return;
                Flash(Net.IsHost ? "You are hosting: stop hosting before you join a friend through Steam"
                                 : "You are in a crew: leave it before you join another through Steam");
                return;
            }
            if (JoinBlocker() != null)
            {
                // A game Steam has just started is on its way to the title screen; a player in a
                // world of their own has to go there, since a guest brings no world along.
                if (GameAccess.InWorld && !_steamJoinTold)
                {
                    _steamJoinTold = true;
                    Flash("To join your friend, go to the title screen");
                }
                if (now - _steamJoinSince > SteamJoinPatience)
                {
                    _steamJoinHost = 0UL;
                    Notice = "The request to join through Steam has lapsed. Join from the Co-op menu.";
                }
                return;
            }
            ulong host = _steamJoinHost;
            _steamJoinHost = 0UL;
            Plugin.Log.LogInfo("[Coop] Joining " + host + " on a request from Steam");
            // Said on screen as well: the dialog may not be there to say it (not found yet, or switched off).
            if (!JoinSteam(host)) Flash(Notice);
            CoopGameUI.ShowJoin(Notice);
        }

        public void Disconnect()
        {
            if (Net.IsHost)
            {
                Net.Stop("The host ended the session");
                Pause.Reset();
                ClearWorldState();
                Notice = "Session ended";
            }
            else if (Net.IsClient)
            {
                EndClientSession("Disconnected");
            }
        }

        private void ApplyConfig()
        {
            Net.PlayerName = string.IsNullOrEmpty(Plugin.PlayerName.Value) ? "Player" : Plugin.PlayerName.Value;
            Net.MaxClients = Mathf.Clamp(Plugin.MaxClients.Value, 1, Limits.RosterEntries - 1);
            Net.DisconnectTimeoutMs = Plugin.DisconnectTimeoutMs.Value;
            Net.AnnounceOnLan = Plugin.AnnounceOnLan.Value;
        }

        // -----------------------------------------------------------------
        // Host side
        // -----------------------------------------------------------------

        // Asked by the handshake: a guest can only be given a world that is loaded.
        private string HostJoinBlocker() => GameAccess.InWorld ? null : "the host has no world loaded";

        private void HostOnClientReady(PeerSession session)
        {
            if (!World.HostSend(session))
            {
                Net.DisconnectPlayer(session.PlayerNetId, "The host could not send its world");
                return;
            }
            Pause.Begin(session.PlayerNetId);
            Notice = session.PlayerName + " is loading the world";
        }

        private void HostOnPlayerLeft(uint netId)
        {
            Pause.End(netId);
            Players.Remove(netId);
            Items.RemovePlayer(netId);
            Economy.RemovePlayer(netId);
            Cargo.RemovePlayer(netId);
            Rowboat.RemovePlayer(netId);
            Notice = "A player left";
        }

        // -----------------------------------------------------------------
        // Client side
        // -----------------------------------------------------------------

        private void ClientOnLinkLost(string reason)
        {
            Notice = reason;
            ClearWorldState();
            World.EndSession();
        }

        private void EndClientSession(string reason)
        {
            if (Net.IsClient) Net.Stop(reason);
            Notice = reason;
            ClearWorldState();
            World.EndSession();
        }

        // Everything a session put on top of the world: avatars, the slaved hull, the host's sea.
        private void ClearWorldState()
        {
            Players.Clear();
            Boats.Clear();
            Env.Clear();
            Controls.Clear();
            Rescue.Clear();
            Items.Clear();
            Economy.Clear();
            Tasks.Clear();
            Cargo.Clear();
            Rowboat.Clear();
            Crew.Clear();
            Shipyard.Clear();
            Chart.Clear();
            InputBlock.Set(InputBlock.Reason.HostPause, false);
        }

        // -----------------------------------------------------------------
        // Messages
        // -----------------------------------------------------------------

        private void OnGameMessage(MsgType type, INetMessage msg, NetPeer from)
        {
            try
            {
                switch (type)
                {
                    case MsgType.WorldBegin: World.OnBegin((WorldBeginMsg)msg); break;
                    case MsgType.WorldChunk: World.OnChunk((WorldChunkMsg)msg); break;
                    case MsgType.WorldEnd: World.OnEnd((WorldEndMsg)msg); break;
                    case MsgType.ClientWorldLoaded: HostOnWorldLoaded((ClientWorldLoadedMsg)msg, from); break;
                    case MsgType.PlayerState: Players.OnPlayerState((PlayerStateMsg)msg, from); break;
                    case MsgType.BoatState: Boats.OnBoatState((BoatStateMsg)msg); break;
                    case MsgType.FastTravel: Boats.OnFastTravel((FastTravelMsg)msg, from); break;
                    case MsgType.ShipPlans: Shipyard.OnPlans((ShipPlansMsg)msg, from); break;
                    case MsgType.ShipLaunch: Shipyard.OnLaunch((ShipLaunchMsg)msg, from); break;
                    case MsgType.MapChange: Chart.OnChange((MapChangeMsg)msg, from); break;
                    case MsgType.EnvState: Env.OnEnvState((EnvStateMsg)msg); break;
                    case MsgType.ControlRequest: Controls.OnRequest((ControlRequestMsg)msg, from); break;
                    case MsgType.ControlState: Controls.OnState((ControlStateMsg)msg); break;
                    case MsgType.ItemManifest: Items.OnManifest((ItemManifestMsg)msg); break;
                    case MsgType.ItemState: Items.OnState((ItemStateMsg)msg); break;
                    case MsgType.ItemAction: Items.OnAction((ItemActionMsg)msg, from); break;
                    case MsgType.ItemMotion: Items.OnMotion((ItemMotionMsg)msg, from); break;
                    case MsgType.EconomyState: Economy.OnState((EconomyStateMsg)msg); break;
                    case MsgType.EconomyDelta:
                        Economy.OnDelta((EconomyDeltaMsg)msg, from);
                        // A guest whose world was loaded again holds the contracts it joined with.
                        if (((EconomyDeltaMsg)msg).Resync && Net.IsHost)
                        {
                            Tasks.SendAll(Net.PlayerNetIdForPeer(from));
                            Hunt.SendAll(Net.PlayerNetIdForPeer(from));
                            Crew.SendRoster(Net.PlayerNetIdForPeer(from));
                            Shipyard.SendAll(Net.PlayerNetIdForPeer(from));
                            Chart.SendAll(Net.PlayerNetIdForPeer(from));
                            // Its ship is the one of the save again: every control as it stands now.
                            Controls.MarkAllDirty();
                        }
                        break;
                    case MsgType.TaskState: Tasks.OnTask((TaskStateMsg)msg, from); break;
                    case MsgType.JournalLine: Economy.OnJournal((JournalLineMsg)msg, from); break;
                    case MsgType.CargoOps: Cargo.OnOps((CargoOpsMsg)msg, from); break;
                    case MsgType.CargoState: Cargo.OnState((CargoStateMsg)msg); break;
                    case MsgType.CargoLease: Cargo.OnLease((CargoLeaseMsg)msg, from); break;
                    case MsgType.AnimalState: Hunt.OnState((AnimalStateMsg)msg, from); break;
                    case MsgType.FishRecord: Economy.OnRecord((FishRecordMsg)msg, from); break;
                    case MsgType.RowboatState: Rowboat.OnState((RowboatStateMsg)msg, from); break;
                    case MsgType.CrewOrder: Crew.OnOrder((CrewOrderMsg)msg, from); break;
                    case MsgType.CrewState: Crew.OnState((CrewStateMsg)msg); break;
                    case MsgType.CrewRoster: Crew.OnRoster((CrewRosterMsg)msg, from); break;
                    case MsgType.ModData:
                        ModChannel.DeliverPeer((ModDataMsg)msg, from);
                        ModChannel.Deliver((ModDataMsg)msg);
                        break;
                }
            }
            catch (Exception e)
            {
                // One bad message must not take the frame's remaining messages with it.
                Plugin.Log.LogError("[Coop] " + type + " handler failed: " + e);
            }
        }

        private void HostOnWorldLoaded(ClientWorldLoadedMsg msg, NetPeer from)
        {
            uint netId = Net.PlayerNetIdForPeer(from);
            if (netId == 0) return;
            Pause.End(netId);
            string name = Net.GetPlayerName(netId);
            if (msg.Ok)
            {
                Net.SetMemberState(netId, MemberJoinState.Ready);
                // The world it loaded is a few seconds old and holds no wheel angle at all.
                Controls.MarkAllDirty();
                Items.SendManifest(netId);
                Economy.SendAll(netId);
                Tasks.SendAll(netId);
                Hunt.SendAll(netId);
                Crew.SendRoster(netId);
                Shipyard.SendAll(netId);
                Notice = name + " joined";
                Plugin.Log.LogInfo("[Coop] " + name + " is in the world");
            }
            else
            {
                Notice = name + " could not load the world";
                Plugin.Log.LogWarning("[Coop] " + name + " could not load the world: " + msg.Detail);
            }
        }

        // -----------------------------------------------------------------
        // Loop
        // -----------------------------------------------------------------

        /// <summary>This copy is in the shared world and exchanges gameplay state.</summary>
        public bool Participating =>
            GameAccess.InWorld && (Net.IsHost || (Net.IsClient && World.State == GuestWorldState.InWorld));

        // Order matters: the network is pumped first so this frame's state is in; the world state
        // machine and the pause run before anything that reads the world; then the sea, then players.
        private void Update()
        {
            try
            {
                Players.RestoreHandAnimation();
                TickMenuKey();
                // Each guards itself: the menu must not take the network's frame with it.
                CoopGameUI.TickInput(this);
                CoopGameUI.TickPanel(this);
                TickLanSearch();
                Net.PollEvents();
                try { TickSteamJoin(); }
                catch (Exception e)
                {
                    if (_updateErrors++ < 20) Plugin.Log.LogError("[Coop] Steam join request failed: " + e);
                }
                try { TickDev(); }
                catch (Exception e)
                {
                    if (_updateErrors++ < 20) Plugin.Log.LogError("[Dev] Tick failed: " + e);
                }

                // The host left its world through the game's menu: there is nothing left to host.
                if (Net.IsHost && !GameAccess.InWorld)
                {
                    Plugin.Log.LogInfo("[Coop] The host's world is gone; ending the session");
                    Disconnect();
                }

                World.Tick();
                Pause.Tick();
                if (Participating)
                {
                    Boats.FrameTick();
                    Rowboat.FrameTick();
                    // The sea before the players: a guest's wind and waves are the host's from this frame on.
                    if (Net.IsHost) Env.HostTick(); else Env.GuestTick();
                    Controls.Tick();
                    // On its own: a failure in the item registry must not take the players' frame with it.
                    try { Items.Tick(); }
                    catch (Exception e)
                    {
                        if (_updateErrors++ < 20) Plugin.Log.LogError("[Items] Tick failed: " + e);
                    }
                    // On their own, like the items: a failure in the household must not stop the players.
                    try { Economy.Tick(); Tasks.Tick(); }
                    catch (Exception e)
                    {
                        if (_updateErrors++ < 20) Plugin.Log.LogError("[Economy] Tick failed: " + e);
                    }
                    try { Cargo.Tick(); }
                    catch (Exception e)
                    {
                        if (_updateErrors++ < 20) Plugin.Log.LogError("[Cargo] Tick failed: " + e);
                    }
                    try { Crew.Tick(); }
                    catch (Exception e)
                    {
                        if (_updateErrors++ < 20) Plugin.Log.LogError("[Crew] Tick failed: " + e);
                    }
                    try { Shipyard.Tick(Boats); }
                    catch (Exception e)
                    {
                        if (_updateErrors++ < 20) Plugin.Log.LogError("[Shipyard] Tick failed: " + e);
                    }
                    try { Chart.Tick(); }
                    catch (Exception e)
                    {
                        if (_updateErrors++ < 20) Plugin.Log.LogError("[Chart] Tick failed: " + e);
                    }
                    string carried = Items.PopNotice();
                    if (carried != null) Flash(carried);
                    string fish = Controls.PopNotice();
                    if (fish != null) Flash(fish);
                    string loading = Cargo.PopNotice();
                    if (loading != null) Flash(loading);
                    string rescued = Rescue.Tick();
                    if (rescued != null) Flash(rescued);
                    Players.TickLocal();
                }
                // A guest cannot walk about a world that stands still.
                InputBlock.Set(InputBlock.Reason.HostPause, HostPaused);
            }
            catch (Exception e)
            {
                if (_updateErrors++ < 20) Plugin.Log.LogError("[Coop] Update failed: " + e);
            }
        }

        // The hull is a physics body: the host reads it and the guest moves it on the physics step.
        private void FixedUpdate()
        {
            try
            {
                if (!Participating) return;
                if (Net.IsHost) Boats.HostFixedTick(); else Boats.GuestFixedTick();
                Rowboat.FixedTick();
            }
            catch (Exception e)
            {
                if (_updateErrors++ < 20) Plugin.Log.LogError("[Coop] FixedUpdate failed: " + e);
            }
        }

        private void LateUpdate()
        {
            try
            {
                Players.ApplyRemotes();
                // After the avatars and the ship: a carried thing is drawn against both.
                if (Participating)
                {
                    Items.LateTick();
                    Crew.LateTick();
                }
                Players.PoseHands(Participating ? Items : null);
            }
            catch (Exception e)
            {
                if (_updateErrors++ < 20) Plugin.Log.LogError("[Coop] LateUpdate failed: " + e);
            }
            HoldCursor();
            try { InputBlock.Tick(); }
            catch (Exception e)
            {
                if (_updateErrors++ < 20) Plugin.Log.LogError("[Coop] Input block failed: " + e);
            }
        }

        private int _updateErrors;

        /// <summary>Guest: the host's world stands still, so this player's input is taken away.</summary>
        public bool HostPaused => Net.IsClient && Participating && Env.HostPaused;

        private string _flash = "";
        private float _flashUntil;

        /// <summary>Something the player should see without opening the menu.</summary>
        private void Flash(string text)
        {
            Notice = text;
            _flash = text;
            _flashUntil = Time.unscaledTime + 4f;
        }

        // A host may listen on the port of this copy's settings or on the usual one; both are asked.
        private void TickLanSearch()
        {
            try
            {
                bool wanted = CoopGameUI.WantsLanSearch && Net.Role == Role.None && !Net.Closing;
                LanSearch.Tick(wanted, Time.unscaledTime, Plugin.Port.Value, DefaultPort);
            }
            catch (Exception e)
            {
                if (_lanSearchErrors++ < 3) Plugin.Log.LogWarning("[Coop] The search of the local network failed: " + e);
            }
        }

        private void OnApplicationQuit()
        {
            try { LanSearch.Stop(); } catch { }
            try { Net.Stop("The game was closed"); Net.FinishClosing(); } catch { }
            // After the session: Steam is given a moment to send the goodbye that is still queued.
            SteamLink.Shutdown();
        }

        private void OnDestroy()
        {
            try { LanSearch.Stop(); } catch { }
            CoopGameUI.Dispose();
            if (Items != null) SceneManager.sceneUnloaded -= Items.WorldUnloaded;
            if (Hunt != null) SceneManager.sceneUnloaded -= Hunt.WorldUnloaded;
        }
    }
}
