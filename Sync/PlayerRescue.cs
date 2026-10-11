using System.Reflection;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// A drowned player comes back aboard instead of ending the session.
    ///
    /// The game has one death: 30 seconds under water. It fades the screen out and five seconds
    /// later opens a dialog with two ways on — load a save or leave to the title screen. In a
    /// session neither is acceptable: a guest cannot load a save in the host's world, so it would
    /// have to leave, and the host loading a save unloads the world for everyone. So a little before
    /// that dialog the player is put back on the deck, at the last spot it stood on.
    ///
    /// With no ship to return to (it is sinking) the game's dialog is left to appear: that session
    /// is over anyway.
    ///
    /// The same move serves a player left behind in the water: the co-op menu has a button for it.
    /// </summary>
    internal sealed class PlayerRescue
    {
        /// <summary>Seconds after the game's death moment. Its own dialog opens at 5.</summary>
        private const float RescueAfter = 3f;
        private const float NoteInterval = 0.5f;

        private static readonly FieldInfo UnderwaterField = AccessTools.Field(typeof(PlayerHandler), "currentUnderwaterTime");
        private static readonly FieldInfo MaxUnderwaterField = AccessTools.Field(typeof(PlayerHandler), "maxUnderwaterTime");
        private static readonly FieldInfo DeathScreenField = AccessTools.Field(typeof(PlayerHandler), "deathScreen");
        private static readonly FieldInfo WheelField = AccessTools.Field(typeof(ShipHullMover), "shipsWheel");

        private ShipHullMover _deckShip;
        private Vector3 _deckLocal;
        private float _nextNoteAt;
        private bool _warned;

        public int Rescues { get; private set; }

        /// <summary>Every frame for a participant. Returns what to tell the player, or null.</summary>
        public string Tick()
        {
            PlayerHandler player = GameAccess.Player;
            if (player == null) return null;
            ShipHullMover ship = GameAccess.ActiveShip();
            NoteDeck(player, ship);

            if (UnderwaterField == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning("[Rescue] The game's drowning timer was not found (a game update?); a drowned player is not brought back");
                }
                return null;
            }
            float under = (float)UnderwaterField.GetValue(player);
            float max = MaxUnderwaterField != null ? (float)MaxUnderwaterField.GetValue(player) : 30f;
            if (under <= max + RescueAfter) return null;
            if (ShipBlocker(ship) != null) return null;

            PutAboard(player, ship);
            Plugin.Log.LogInfo("[Rescue] The local player drowned and was put back aboard");
            return "You were pulled back aboard";
        }

        /// <summary>Null when the local player can be put on the ship now, otherwise why not.</summary>
        public string Blocker()
        {
            PlayerHandler player = GameAccess.Player;
            if (player == null) return "No world is loaded";
            string blocker = ShipBlocker(GameAccess.ActiveShip());
            if (blocker != null) return blocker;
            if (player.isInteracting) return "Let go of what you are holding first";
            return null;
        }

        /// <summary>The menu's button: puts the local player on the ship. Returns what to tell the player.</summary>
        public string ReturnToShip()
        {
            string blocker = Blocker();
            if (blocker != null) return blocker;
            PutAboard(GameAccess.Player, GameAccess.ActiveShip());
            return "You are back aboard";
        }

        private static string ShipBlocker(ShipHullMover ship)
        {
            if (ship == null) return "There is no ship";
            if (ship.sinking) return "The ship is sinking";
            return null;
        }

        // Where the player last stood on the deck, in the hull's frame.
        private void NoteDeck(PlayerHandler player, ShipHullMover ship)
        {
            float now = Time.unscaledTime;
            if (now < _nextNoteAt) return;
            _nextNoteAt = now + NoteInterval;
            if (ship == null || ship.sinking || player.ship != ship.gameObject) return;
            if (!player.grounded || player.state == PlayerHandler.MovementState.swimming) return;
            _deckShip = ship;
            _deckLocal = ship.transform.InverseTransformPoint(player.transform.position);
        }

        private void PutAboard(PlayerHandler player, ShipHullMover ship)
        {
            Vector3 point;
            if (_deckShip == ship)
            {
                point = ship.transform.TransformPoint(_deckLocal) + Vector3.up * 0.3f;
            }
            else
            {
                // Never stood on this ship in this session: above the wheel, which every ship has on deck.
                GameObject wheel = WheelField?.GetValue(ship) as GameObject;
                point = (wheel != null ? wheel.transform.position : ship.transform.position + Vector3.up * 2f) + Vector3.up * 1.5f;
            }

            Move(player, point);

            UnderwaterField?.SetValue(player, 0f);
            // What the game does when a player surfaces in time: the fade runs backwards.
            if (DeathScreenField?.GetValue(player) is GameObject screen && screen.activeSelf)
            {
                Fade fade = screen.GetComponent<Fade>();
                if (fade != null) fade.Reset();
            }
            Rescues++;
        }

        /// <summary>Puts the local player at a point of the scene, the way the game's own ladders do.</summary>
        internal static void Move(PlayerHandler player, Vector3 point)
        {
            Rigidbody body = player.GetRigidbody();
            PlayerCloneController clone = player.playerClone;
            if (clone != null && clone.gameObject.activeSelf)
            {
                // The game's own teleport of a player that walks on the deck copy (LadderController.Teleport).
                body.isKinematic = true;
                clone.GetBody().isKinematic = true;
                player.transform.position = point;
                clone.transform.localPosition = body.transform.localPosition;
                body.isKinematic = false;
                clone.GetBody().isKinematic = false;
            }
            else
            {
                body.linearVelocity = Vector3.zero;
                player.transform.position = point;
                body.position = point;
            }
            Physics.SyncTransforms();
        }

        public void Clear()
        {
            _deckShip = null;
            Rescues = 0;
        }
    }
}
