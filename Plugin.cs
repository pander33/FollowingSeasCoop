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
        internal static ConfigEntry<int> DisconnectTimeoutMs;
        internal static ConfigEntry<int> SendRateHz;
        internal static ConfigEntry<int> InterpDelayMs;
        internal static ConfigEntry<string> MenuKey;
        internal static ConfigEntry<bool> ShowStatus;
        internal static ConfigEntry<string> SaveDir;

        private void Awake()
        {
            Log = Logger;

            PlayerName = Config.Bind("Player", "Name", System.Environment.UserName ?? "Player",
                "The name other players see in the session list.");
            Port = Config.Bind("Network", "Port", 7777, "UDP port the host listens on and a client connects to.");
            JoinIp = Config.Bind("Network", "JoinIp", "127.0.0.1", "Host address used by Join.");
            MaxClients = Config.Bind("Network", "MaxClients", 3, "How many guests the host accepts.");
            DisconnectTimeoutMs = Config.Bind("Network", "DisconnectTimeoutMs", 15000,
                "Silence after which a link is considered lost. A world load blocks the game for seconds, so keep it generous.");
            SendRateHz = Config.Bind("Sync", "SendRateHz", 20, "How many times a second a player's pose is sent.");
            InterpDelayMs = Config.Bind("Sync", "InterpDelayMs", 100,
                "How far in the past remote players are drawn. Larger is smoother and laggier.");
            MenuKey = Config.Bind("UI", "MenuKey", "F8", "Key that opens the co-op menu (a UnityEngine.InputSystem.Key name).");
            ShowStatus = Config.Bind("UI", "ShowStatus", true, "Show the status overlay while a session is active.");
            SaveDir = Config.Bind("Dev", "SaveDir", "",
                "Development: keep this copy's saves and settings in this folder instead of the game's own. " +
                "Empty = the game's folder. Lets a test copy run without touching real saves.");

            // The BepInEx manager object can be destroyed by the game; the loop must outlive scenes.
            var go = new GameObject("FollowingSeasCoop");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<CoopBehaviour>();

            Logger.LogInfo(Name + " " + Version + " loaded (protocol " + Net.Protocol.Version + ")");
        }
    }
}
