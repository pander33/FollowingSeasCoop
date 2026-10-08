using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>
    /// The few places where the mod asks the game "where are we" and "who is the player". Everything
    /// here was read in the decompiled game code (see GAME_INTERACTIONS.md) and is kept in one file
    /// so a game update has one place to break.
    /// </summary>
    internal static class GameAccess
    {
        public const int MenuScene = 1;
        public const int IntroScene = 2;
        public const int WorldScene = 3;

        private static bool Loaded(int buildIndex)
        {
            Scene scene = SceneManager.GetSceneByBuildIndex(buildIndex);
            return scene.IsValid() && scene.isLoaded;
        }

        /// <summary>The title screen, with no world under it.</summary>
        public static bool AtMainMenu => Loaded(MenuScene) && !Loaded(WorldScene) && !Loaded(IntroScene);

        /// <summary>The world's save manager, or null outside the world (the menu has its own instance).</summary>
        public static DataPersistenceManager Dp
        {
            get
            {
                if (!Loaded(WorldScene)) return null;
                DataPersistenceManager dp = DataPersistenceManager.dpInstance;
                // While the world scene is loaded the instance is the world's: each scene's manager
                // takes the static over in its Awake. ignoreLoad marks one that only serves settings.
                if (dp == null || dp.ignoreLoad) return null;
                return dp;
            }
        }

        public static PlayerHandler Player
        {
            get
            {
                DataPersistenceManager dp = Dp;
                return dp != null ? dp.player : null;
            }
        }

        /// <summary>The world scene is loaded and its save data has been applied to a player.</summary>
        public static bool InWorld
        {
            get
            {
                DataPersistenceManager dp = Dp;
                return dp != null && dp.player != null && dp.gameData != null;
            }
        }

        private static ShipHullMover _ship;
        private static float _shipCheckedAt = -10f;

        /// <summary>
        /// The player's ship: the one active hull among the hulls of every type that the world scene
        /// holds. The same on every machine, because the host's save names it.
        /// </summary>
        public static ShipHullMover ActiveShip()
        {
            if (_ship != null && _ship.gameObject.activeInHierarchy && Time.unscaledTime - _shipCheckedAt < 1f) return _ship;
            _shipCheckedAt = Time.unscaledTime;
            _ship = null;
            DataPersistenceManager dp = Dp;
            if (dp == null) return null;

            string wanted = dp.gameData != null ? dp.gameData.currentShip : null;
            ShipHullMover fallback = null;
            foreach (GameObject root in SceneManager.GetSceneByBuildIndex(WorldScene).GetRootGameObjects())
            {
                if (!root.activeInHierarchy) continue;
                ShipHullMover mover = root.GetComponent<ShipHullMover>();
                if (mover == null || root.GetComponent<RowboatController>() != null) continue;
                if (!string.IsNullOrEmpty(wanted) && mover.id == wanted) { _ship = mover; return _ship; }
                if (fallback == null) fallback = mover;
            }
            _ship = fallback;
            return _ship;
        }

        /// <summary>
        /// The world as the game would save it right now, without writing a file: the same walk over
        /// the save participants as <c>DataPersistenceManager.SaveGame</c>, and the same serializer
        /// as <c>FileDataHandler.Save</c>.
        /// </summary>
        public static string CaptureWorldJson()
        {
            DataPersistenceManager dp = Dp;
            if (dp == null || dp.gameData == null) return null;
            var participants = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                                     .OfType<IDataPersistence>();
            foreach (IDataPersistence participant in participants)
            {
                if (participant.GetGameObject().activeSelf) participant.SaveData(ref dp.gameData);
            }
            return JsonConvert.SerializeObject(dp.gameData, Formatting.None,
                new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
        }

        /// <summary>Leaves the world for the title screen the way the game's own menu does.</summary>
        public static void ReturnToMainMenu()
        {
            // The game's InGameMenu.GoToMenu unhooks these before unloading the world.
            foreach (var rope in Object.FindObjectsByType<SimpleRopeController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Application.onBeforeRender -= rope.UpdateRopePositions;
            foreach (var line in Object.FindObjectsByType<CurvedLineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Application.onBeforeRender -= line.UpdatePoints;
            Time.timeScale = 1f;
            GlobalData.SaveFileToLoad = null;
            GlobalData.NewGame = false;
            _ship = null;
            GameManager.instance.LoadMainMenu();
        }
    }
}
