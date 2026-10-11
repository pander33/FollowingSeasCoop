using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FollowingSeasCoop.Runtime;
using UnityEngine;

namespace FollowingSeasCoop
{
    /// <summary>BepInEx entry point: reads the config and attaches the one behaviour that owns the mod.</summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.followingseas.coop";
        public const string Name = "Following Seas Co-op";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<string> PlayerName;
        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<string> JoinIp;
        internal static ConfigEntry<int> MaxClients;
        internal static ConfigEntry<bool> AnnounceOnLan;
        internal static ConfigEntry<bool> SteamEnabled;
        internal static ConfigEntry<bool> SteamFriendsOnly;
        internal static ConfigEntry<int> DisconnectTimeoutMs;
        internal static ConfigEntry<int> SendRateHz;
        internal static ConfigEntry<int> InterpDelayMs;
        internal static ConfigEntry<string> MenuKey;
        internal static ConfigEntry<bool> ShowStatus;
        internal static ConfigEntry<string> SaveDir;
        internal static ConfigEntry<bool> DevTools;
        internal static ConfigEntry<bool> DevTrace;
        internal static ConfigEntry<int> DevDumpEverySeconds;
        internal static ConfigEntry<int> DevLagMs;
        internal static ConfigEntry<int> DevJitterMs;
        internal static ConfigEntry<bool> DevAutoHost;
        internal static ConfigEntry<bool> DevAutoJoin;
        internal static ConfigEntry<bool> DevRunInBackground;
        internal static ConfigEntry<string> DevWindow;

        private void Awake()
        {
            Log = Logger;

            PlayerName = Config.Bind("Player", "Name", System.Environment.UserName ?? "Player",
                "The name other players see in the session list.");
            Port = Config.Bind("Network", "Port", 7777, "UDP port the host listens on and a client connects to.");
            JoinIp = Config.Bind("Network", "JoinIp", "127.0.0.1", "Host address used by Join.");
            MaxClients = Config.Bind("Network", "MaxClients", 3, "How many guests the host accepts.");
            AnnounceOnLan = Config.Bind("Network", "AnnounceOnLan", true,
                "The host answers players of the local network who look for a game, so it appears in their Co-op dialog. Off = the game is joined by address only. Applied when hosting starts.");
            SteamEnabled = Config.Bind("Steam", "Enabled", true,
                "Host and join through Steam as well as by address: Steam friends see a hosted game in the Co-op dialog and in their friends list, with no address and no port forwarding. Steam must be running. Set on the Co-op page.");
            SteamFriendsOnly = Config.Bind("Steam", "FriendsOnly", false,
                "A host over Steam lets in its Steam friends only, and nobody by address. Off = anyone who has the host's Steam ID or address. Set on the Co-op page.");
            DisconnectTimeoutMs = Config.Bind("Network", "DisconnectTimeoutMs", 15000,
                "Silence after which a link is considered lost. A world load blocks the game for seconds, so keep it generous.");
            SendRateHz = Config.Bind("Sync", "SendRateHz", 20, "How many times a second a player's pose is sent.");
            InterpDelayMs = Config.Bind("Sync", "InterpDelayMs", 100,
                "How far in the past remote players are drawn. Larger is smoother and laggier.");
            MenuKey = Config.Bind("UI", "MenuKey", "F8", "Key that opens the debug menu (a UnityEngine.InputSystem.Key name).");
            ShowStatus = Config.Bind("UI", "ShowStatus", true, "Show the status overlay while a session is active.");
            SaveDir = Config.Bind("Dev", "SaveDir", "",
                "Development: keep this copy's saves and settings in this folder instead of the game's own. " +
                "Empty = the game's folder. Lets a test copy run without touching real saves.");

            DevTools = Config.Bind("Dev", "Tools", false,
                "Development: show the Dev section of the menu (teleports, leaks, fish, a resync).");
            DevTrace = Config.Bind("Dev", "Trace", false,
                "Development: write every message and what became of it to BepInEx/coop-trace-<who>.log during a session.");
            DevDumpEverySeconds = Config.Bind("Dev", "DumpEverySeconds", 0,
                "Development: write this copy's shared state to BepInEx/coop-state-<who>.txt this often, at the same moment " +
                "of the host's clock on every copy. 0 = only with the Dump state button.");
            DevLagMs = Config.Bind("Dev", "LagMs", 0,
                "Development: hold every arriving message this many milliseconds before acting on it.");
            DevJitterMs = Config.Bind("Dev", "JitterMs", 0,
                "Development: hold arriving messages up to this many milliseconds more, at random.");
            DevAutoHost = Config.Bind("Dev", "AutoHost", false,
                "Development: host as soon as a world is loaded.");
            DevAutoJoin = Config.Bind("Dev", "AutoJoin", false,
                "Development: join Network/JoinIp from the title screen, trying again until the host answers.");
            DevRunInBackground = Config.Bind("Dev", "RunInBackground", false,
                "Development: keep the game running while its window is not the active one.");
            DevWindow = Config.Bind("Dev", "Window", "",
                "Development: keep the game in a window at this place and size, as x,y,width,height (0,0,1280,720). " +
                "The game's options switch to full screen by themselves. Empty = left to the game.");
            ApplyCommandLine();

            // The BepInEx manager object can be destroyed by the game; the loop must outlive scenes.
            var go = new GameObject("FollowingSeasCoop");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<CoopBehaviour>();

            Logger.LogInfo(Name + " " + Version + " loaded (protocol " + Net.Protocol.Version + ")");
        }

        // Two copies started from one folder read one config file. A copy is told apart on its
        // command line: --coop:Section.Key=value, for any setting above. What is set this way is
        // never written back to the file.
        private void ApplyCommandLine()
        {
            const string prefix = "--coop:";
            foreach (string arg in System.Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) continue;
                string body = arg.Substring(prefix.Length);
                int dot = body.IndexOf('.');
                int equals = body.IndexOf('=');
                if (dot <= 0 || equals <= dot + 1)
                {
                    Logger.LogWarning("Command line: '" + arg + "' is not --coop:Section.Key=value");
                    continue;
                }
                var definition = new ConfigDefinition(body.Substring(0, dot), body.Substring(dot + 1, equals - dot - 1));
                if (!Config.ContainsKey(definition))
                {
                    Logger.LogWarning("Command line: there is no setting " + definition.Section + "/" + definition.Key);
                    continue;
                }
                Config.SaveOnConfigSet = false;
                Config[definition].SetSerializedValue(body.Substring(equals + 1));
                Logger.LogInfo("Command line: " + definition.Section + "/" + definition.Key + " = " + Config[definition].GetSerializedValue());
            }
        }
    }
}
