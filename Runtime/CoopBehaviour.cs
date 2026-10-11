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

        private Harmony _harmony;
        /// <summary>What the menu shows about the last thing that happened ("Connected", an error...).</summary>
        public string Notice { get; private set; } = "";

        private void Awake()
        {
            Instance = this;
            Protocol.Warn = message => Plugin.Log.LogWarning(message);

            Net = new CoopNet(message => Plugin.Log.LogInfo(message))
            {
                ModVersion = Plugin.Version,
                GameVersion = Application.version ?? "",
                JoinBlocker = HostJoinBlocker,
            };
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
            SceneManager.sceneUnloaded += Items.WorldUnloaded;

            Net.OnClientReady += HostOnClientReady;
            Net.OnPlayerLeft += HostOnPlayerLeft;
            Net.OnAccepted += ack => Notice = "Connected to " + ack.HostName + ", receiving the world";
            Net.OnLinkLost += ClientOnLinkLost;
            Net.OnGameMessage += OnGameMessage;
            World.OnGuestAbort += reason => EndClientSession(reason);

            _harmony = new Harmony(Plugin.Guid);
            Action<string> log = message => Plugin.Log.LogError(message);
            PatchHealth.Install("Save folder", () => SavePatches.InstallSaveDir(_harmony), log);
            // Without these two a guest would load its own save, or overwrite it with the host's world.
            PatchHealth.Install("Guest world", () => SavePatches.InstallGuestWorld(_harmony), log, required: true);
            PatchHealth.Install("Guest save block", () => SavePatches.InstallGuestSaveBlock(_harmony), log, required: true);
            PatchHealth.Install("Ship", () => BoatSync.Install(_harmony), log);
            // Without it: no rope trimming by looking at a rope, no door or lantern from a guest, no guest pumping.
            PatchHealth.Install("Controls", () => ControlsSync.Install(_harmony), log);
            PatchHealth.Install("Item catalog", () => ItemCatalog.Install(_harmony), log);
            // Without it things are still told apart, but carrying one is not shown to the others.
            PatchHealth.Install("Items", () => ItemRegistry.Install(_harmony), log);
            // Without it a tool is still shown in the other player's hand, but not its blows.
            PatchHealth.Install("Tools", () => ToolVisuals.Install(_harmony), log);
            // Without it a guest's own timer would eat from the shared stores a second time.
            PatchHealth.Install("Economy", () => EconomySync.Install(_harmony), log);
            Plugin.Log.LogInfo("Patches: " + PatchHealth.Details);
        }

        // -----------------------------------------------------------------
        // Session control (the menu and the dev console call these)
        // -----------------------------------------------------------------

        /// <summary>Null when hosting may start now, otherwise why not.</summary>
        public string HostBlocker()
        {
            if (PatchHealth.Blocker != null) return "The mod does not fit this game version: " + PatchHealth.Blocker;
            if (WorldTransfer.GuestWorld) return "Still leaving the previous session";
            if (!GameAccess.InWorld) return "Load your world first, then host";
            return null;
        }

        /// <summary>Null when a join may start now, otherwise why not.</summary>
        public string JoinBlocker()
        {
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
            Net.StartHost(Plugin.Port.Value);
            Notice = Net.IsHost ? "Hosting on port " + Plugin.Port.Value : Net.LastError;
            return Net.IsHost;
        }

        public bool Join(string ip)
        {
            string blocker = JoinBlocker();
            if (blocker != null) { Notice = blocker; return false; }
            ApplyConfig();
            Net.StartClient(string.IsNullOrEmpty(ip) ? Plugin.JoinIp.Value : ip.Trim(), Plugin.Port.Value);
            Notice = Net.IsClient ? "Connecting to " + Net.HostLabel + " ..." : Net.LastError;
            return Net.IsClient;
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
        }

        // -----------------------------------------------------------------
        // Host side
        // -----------------------------------------------------------------

        // Asked by the handshake: a guest can only be given a world that is loaded.
        private string HostJoinBlocker() => GameAccess.InWorld ? null : "the host has no world loaded";

        private void HostOnClientReady(PeerSession session)
        {
            Items.CaptureCargoKeys(session.PlayerNetId);
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
                        if (((EconomyDeltaMsg)msg).Resync && Net.IsHost) Tasks.SendAll(Net.PlayerNetIdForPeer(from));
                        break;
                    case MsgType.TaskState: Tasks.OnTask((TaskStateMsg)msg, from); break;
                    case MsgType.JournalLine: Economy.OnJournal((JournalLineMsg)msg, from); break;
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
                TickMenuKey();
                Net.PollEvents();

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
                    string carried = Items.PopNotice();
                    if (carried != null) Flash(carried);
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
                if (Participating) Items.LateTick();
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

        private void OnApplicationQuit()
        {
            try { Net.Stop("The game was closed"); Net.FinishClosing(); } catch { }
        }

        private void OnDestroy()
        {
            if (Items != null) SceneManager.sceneUnloaded -= Items.WorldUnloaded;
        }
    }
}
