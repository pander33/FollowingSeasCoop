using System;
using System.Collections.Generic;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// Patches around the game's save system. Two jobs:
    ///
    /// <b>The guest loads the host's world from memory.</b> The game loads a world by reading a save
    /// file (<c>FileDataHandler.Load</c>). While a transferred world is pending, that read returns
    /// the transferred data instead, so nothing of the host's ever lands in the guest's save folder.
    ///
    /// <b>The guest never saves.</b> It stands in the host's world; a save would overwrite its own
    /// save files with the host's progress (the autosave is a fixed name, "AutoSave").
    /// </summary>
    internal static class SavePatches
    {
        private static void Report(Exception error) => Plugin.Log.LogError("[SavePatches] " + error);

        public static void InstallSaveDir(Harmony harmony)
        {
            harmony.Patch(AccessTools.Constructor(typeof(FileDataHandler), new[] { typeof(string), typeof(string) })
                              ?? throw new MissingMethodException("FileDataHandler(string, string)"),
                          prefix: new HarmonyMethod(typeof(SavePatches), nameof(CtorPrefix)));
        }

        public static void InstallGuestWorld(Harmony harmony)
        {
            harmony.Patch(Method(typeof(FileDataHandler), "Load"),
                          prefix: new HarmonyMethod(typeof(SavePatches), nameof(LoadPrefix)));
            harmony.Patch(Method(typeof(FileDataHandler), "GetFileSaveNames"),
                          postfix: new HarmonyMethod(typeof(SavePatches), nameof(SaveNamesPostfix)));
        }

        public static void InstallGuestSaveBlock(Harmony harmony)
        {
            harmony.Patch(Method(typeof(DataPersistenceManager), "SaveGame"),
                          prefix: new HarmonyMethod(typeof(SavePatches), nameof(SaveGamePrefix)));
            harmony.Patch(Method(typeof(FileDataHandler), "Save"),
                          prefix: new HarmonyMethod(typeof(SavePatches), nameof(FileSavePrefix)));
            harmony.Patch(Method(typeof(GameManager), "LoadGameHot"),
                          prefix: new HarmonyMethod(typeof(SavePatches), nameof(LoadGameHotPrefix)));
        }

        private static System.Reflection.MethodInfo Method(Type type, string name)
            => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.Name, name);

        // Dev/SaveDir: every save and settings file of this copy goes to another folder.
        private static void CtorPrefix(ref string dataDirPath)
        {
            try
            {
                string dir = Plugin.SaveDir.Value;
                if (string.IsNullOrEmpty(dir)) return;
                System.IO.Directory.CreateDirectory(dir);
                dataDirPath = dir;
            }
            catch (Exception error) { Report(error); }
        }

        private static bool LoadPrefix(ref GameData __result)
        {
            GameData world = null;
            bool handled = PatchGuard.Prefix(() =>
            {
                string json = WorldTransfer.PendingJson;
                if (json == null) return true;
                world = JsonUtility.FromJson<GameData>(json);
                Plugin.Log.LogInfo("[SavePatches] The world is loaded from the host's transfer, not from a file");
                return false;
            }, Report);
            if (!handled) __result = world;
            return handled;
        }

        // GameManager.LoadGame opens the world scene only when the save folder has at least one file;
        // with none it starts the intro. A guest with no saves of its own still has a world to load.
        private static void SaveNamesPostfix(List<string> __result)
        {
            PatchGuard.Run(() =>
            {
                if (WorldTransfer.PendingJson != null && __result != null && __result.Count == 0)
                    __result.Add(WorldTransfer.SessionSaveName);
            }, Report);
        }

        private static bool SaveGamePrefix(DataPersistenceManager __instance, string saveName)
        {
            return PatchGuard.Prefix(() =>
            {
                if (!WorldTransfer.GuestWorld) return true;
                Plugin.Log.LogInfo("[SavePatches] Save '" + saveName + "' skipped: this is the host's world");
                // The game's SaveGame also stores the settings; keep that half.
                __instance.SaveSettings();
                return false;
            }, Report);
        }

        private static bool FileSavePrefix(string fileName)
        {
            return PatchGuard.Prefix(() =>
            {
                if (!WorldTransfer.GuestWorld) return true;
                Plugin.Log.LogWarning("[SavePatches] File save '" + fileName + "' blocked: this is the host's world");
                return false;
            }, Report);
        }

        // The in-game load dialog would replace the host's world with one of the guest's own saves.
        private static bool LoadGameHotPrefix()
        {
            return PatchGuard.Prefix(() =>
            {
                if (!WorldTransfer.GuestWorld) return true;
                Plugin.Log.LogWarning("[SavePatches] Loading a save is not available in the host's world; leave the session first");
                return false;
            }, Report);
        }
    }
}
