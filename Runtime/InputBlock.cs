using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>
    /// Takes the game's input away from the local player while something of the mod needs it: the
    /// open co-op menu, or the host's pause on a guest. Several reasons can hold it at once; the
    /// game gets its input back when the last one lets go.
    ///
    /// <c>InteractionController.blockInput</c> stops that controller's whole LateUpdate (camera and
    /// interactions; the game never sets it itself). <c>PlayerHandler.disablePlayerInput</c> stops
    /// walking and the game's menu key. Neither reaches the ship: with the wheel in hand its
    /// physics step reads the movement keys directly, so those two actions are switched off too —
    /// the game's code then reads "no key" and holds the wheel where it is.
    /// </summary>
    internal static class InputBlock
    {
        [Flags]
        public enum Reason
        {
            None = 0,
            Menu = 1,
            HostPause = 2,
        }

        private static readonly string[] HeldActions = { "Horizontal", "Vertical" };

        private static Reason _reasons;
        private static bool _applied;
        private static readonly List<InputAction> _switchedOff = new List<InputAction>();

        public static bool Active => _reasons != Reason.None;

        public static void Set(Reason reason, bool on)
        {
            if (on) _reasons |= reason;
            else _reasons &= ~reason;
        }

        /// <summary>Every frame, late: hold the block or give the input back.</summary>
        public static void Tick()
        {
            InteractionController controller = InteractionController.interactionInstance;
            if (Active)
            {
                if (controller == null) return;
                controller.blockInput = true;
                PlayerHandler player = GameAccess.Player;
                if (player != null) player.disablePlayerInput = true;
                if (!_applied)
                {
                    _applied = true;
                    SwitchActions(controller, off: true);
                }
                return;
            }
            if (!_applied) return;
            _applied = false;
            SwitchActions(controller, off: false);
            if (controller == null) return;
            controller.blockInput = false;
            PlayerHandler owner = GameAccess.Player;
            // The controller copies its own flag to the player every frame; put that value back.
            if (owner != null) owner.disablePlayerInput = controller.disablePlayerInput;
        }

        /// <summary>The session or the world ended: nothing of the mod holds the input any more.</summary>
        public static void Release()
        {
            _reasons = Reason.None;
            Tick();
        }

        private static void SwitchActions(InteractionController controller, bool off)
        {
            try
            {
                if (!off)
                {
                    // Only what was on before: an action the game had off stays off.
                    foreach (InputAction action in _switchedOff) action.Enable();
                    _switchedOff.Clear();
                    return;
                }
                _switchedOff.Clear();
                if (controller == null) return;
                PlayerInput input = controller.GetInputController();
                if (input == null || input.actions == null) return;
                foreach (string name in HeldActions)
                {
                    InputAction action = input.actions.FindAction(name);
                    if (action == null || !action.enabled) continue;
                    action.Disable();
                    _switchedOff.Add(action);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Input] " + e);
            }
        }
    }
}
