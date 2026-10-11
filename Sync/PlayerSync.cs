using System.Collections.Generic;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using LiteNetLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// Players see each other. Every participant that is in the world sends its own pose; the host
    /// relays a client's pose to the other clients.
    ///
    /// On the ship the pose is hull-local: each player stands on its own copy of the same ship, and
    /// hull-local coordinates map identically whatever the floating origin or the ship's position on
    /// that machine. Off the ship the pose is in real space (<see cref="CoordSpace"/>).
    /// </summary>
    internal sealed class PlayerSync
    {
        private sealed class Remote
        {
            public uint NetId;
            public readonly NetTransform Pose = new NetTransform();
            public bool OnShip;
            public bool HasFrame;
            public GameObject Avatar;
            public Animator Animator;
            public bool HasWalking, HasVertical, HasHorizontal, HasStand;
            public Vector3 LastWorld;
            public bool HasLastWorld;
            public float Speed;
            public bool Crouching;
            public byte Tool, ToolUse;
            public long ToolTick;
            public readonly ToolVisuals Tools = new ToolVisuals();
        }

        private readonly CoopNet _net;
        private readonly Dictionary<uint, Remote> _remotes = new Dictionary<uint, Remote>();
        private readonly List<uint> _gone = new List<uint>();

        // Local capture.
        private float _nextSendAt;
        private bool _lastOnShip;
        private Vector3 _lastFramePos;
        private bool _hasLastFramePos;
        private float _lastCaptureAt;

        public int RemoteCount => _remotes.Count;

        public PlayerSync(CoopNet net) { _net = net; }

        /// <summary>Where another player's avatar stands in this copy's scene; false when it has none.</summary>
        public bool AvatarAt(uint netId, out Vector3 position)
        {
            position = default;
            if (!_remotes.TryGetValue(netId, out Remote remote) || remote.Avatar == null) return false;
            position = remote.Avatar.transform.position;
            return true;
        }

        // -----------------------------------------------------------------
        // Local player → wire
        // -----------------------------------------------------------------

        /// <summary>Sends the local player's pose at the configured rate. Call only for a participant in the world.</summary>
        public void TickLocal()
        {
            float now = Time.unscaledTime;
            if (now < _nextSendAt) return;
            _nextSendAt = now + 1f / Mathf.Clamp(Plugin.SendRateHz.Value, 1, 60);

            PlayerHandler player = GameAccess.Player;
            if (player == null) return;
            if (!Capture(player, now, out PlayerStateMsg msg)) return;

            if (_net.IsHost) _net.Broadcast(msg, DeliveryMethod.Unreliable);
            else _net.SendToHost(msg, DeliveryMethod.Unreliable);
        }

        private bool Capture(PlayerHandler player, float now, out PlayerStateMsg msg)
        {
            msg = null;
            Transform orientation = player.orientation != null ? player.orientation : player.transform;
            Vector3 feet = Feet(player);

            // "On the ship" means on the player ship itself, the hull every machine has. A rowboat or
            // another vessel is not a shared frame yet, so a player there is sent in real space.
            ShipHullMover ship = GameAccess.ActiveShip();
            bool onShip = ship != null && player.ship != null && player.ship == ship.gameObject;

            var state = new PlayerStateMsg { NetId = _net.MyNetId, Tick = _net.Clock.ServerTick };
            Vector3 framePos;
            Vector3 forward;
            if (onShip)
            {
                state.Flags |= PlayerFlags.OnShip;
                framePos = ship.transform.InverseTransformPoint(feet);
                forward = ship.transform.InverseTransformDirection(orientation.forward);
                state.X = framePos.x; state.Y = framePos.y; state.Z = framePos.z;
            }
            else
            {
                CoordSpace.LocalToReal(feet, out state.X, out state.Y, out state.Z);
                // Velocity is taken in scene space; an origin shift between two captures would show
                // as a jump, so such a sample simply carries no velocity (see below).
                framePos = feet;
                forward = orientation.forward;
            }
            state.Yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

            GameObject camera = null;
            try { camera = player.GetCameraObject(); } catch { }
            if (camera != null) state.Pitch = Mathf.DeltaAngle(0f, camera.transform.eulerAngles.x);

            float dt = now - _lastCaptureAt;
            if (_hasLastFramePos && onShip == _lastOnShip && dt > 0.001f && dt < 0.5f)
            {
                Vector3 velocity = (framePos - _lastFramePos) / dt;
                // Faster than a person moves: a teleport or an origin shift, not motion.
                if (velocity.sqrMagnitude < 30f * 30f) { state.Vx = velocity.x; state.Vy = velocity.y; state.Vz = velocity.z; }
            }
            _lastFramePos = framePos;
            _hasLastFramePos = true;
            _lastOnShip = onShip;
            _lastCaptureAt = now;

            if (player.state == PlayerHandler.MovementState.crouching) state.Flags |= PlayerFlags.Crouching;
            if (player.state == PlayerHandler.MovementState.swimming) state.Flags |= PlayerFlags.Swimming;

            state.Tool = ToolVisuals.LocalKind();
            state.ToolUse = ToolVisuals.LocalUseCount;
            ToolVisuals.Measure(player, feet, onShip ? ship.transform.up : Vector3.up);

            msg = state;
            return true;
        }

        /// <summary>The point under the player's feet. The player object's own position is the capsule centre.</summary>
        private static Vector3 Feet(PlayerHandler player)
        {
            CapsuleCollider capsule = null;
            try { capsule = player.GetPlayerCollider(); } catch { }
            if (capsule == null) return player.transform.position;
            return capsule.transform.TransformPoint(capsule.center - Vector3.up * (capsule.height * 0.5f));
        }

        // -----------------------------------------------------------------
        // Wire → remote players
        // -----------------------------------------------------------------

        public void OnPlayerState(PlayerStateMsg msg, NetPeer from)
        {
            if (_net.IsHost)
            {
                // A client speaks for itself only.
                if (msg.NetId != _net.PlayerNetIdForPeer(from)) return;
                _net.Broadcast(msg, DeliveryMethod.Unreliable, readyOnly: true, exceptNetId: msg.NetId);
            }
            if (msg.NetId == _net.MyNetId) return;

            if (!_remotes.TryGetValue(msg.NetId, out Remote remote))
            {
                remote = new Remote { NetId = msg.NetId };
                _remotes[msg.NetId] = remote;
            }
            bool onShip = (msg.Flags & PlayerFlags.OnShip) != 0;
            if (!remote.HasFrame || remote.OnShip != onShip)
            {
                // Positions of two frames cannot be interpolated.
                remote.Pose.Clear();
                remote.OnShip = onShip;
                remote.HasFrame = true;
                remote.HasLastWorld = false;
            }
            // Poses are interpolated; these are taken as they are, so a late packet must not bring an old tool back.
            if (msg.Tick >= remote.ToolTick)
            {
                remote.ToolTick = msg.Tick;
                remote.Crouching = (msg.Flags & PlayerFlags.Crouching) != 0;
                remote.Tool = msg.Tool;
                remote.ToolUse = msg.ToolUse;
            }
            remote.Pose.InterpDelayMs = Plugin.InterpDelayMs.Value;
            remote.Pose.Push(msg.Tick, msg.X, msg.Y, msg.Z, msg.Yaw, msg.Pitch, msg.Vx, msg.Vy, msg.Vz);
        }

        /// <summary>
        /// Places every remote avatar. Runs in LateUpdate: by then the ship's transform holds this
        /// frame's rendered (interpolated) pose, so an avatar on deck does not shake against it.
        /// </summary>
        public void ApplyRemotes()
        {
            if (_remotes.Count == 0) return;
            if (!GameAccess.InWorld) { Clear(); return; }

            long serverNow = _net.Clock.ServerTick;
            ShipHullMover ship = GameAccess.ActiveShip();
            _gone.Clear();
            foreach (Remote remote in _remotes.Values)
            {
                if (!_net.HasMember(remote.NetId)) { _gone.Add(remote.NetId); continue; }
                if (!remote.Pose.Sample(serverNow, out double x, out double y, out double z, out float yaw, out float pitch)) continue;
                if (remote.OnShip && ship == null)
                {
                    if (remote.Avatar != null && remote.Avatar.activeSelf) remote.Avatar.SetActive(false);
                    continue;
                }

                if (remote.Avatar == null) Spawn(remote);
                if (!remote.Avatar.activeSelf) remote.Avatar.SetActive(true);

                Vector3 world;
                Quaternion rotation;
                if (remote.OnShip)
                {
                    world = ship.transform.TransformPoint(new Vector3((float)x, (float)y, (float)z));
                    rotation = ship.transform.rotation * Quaternion.Euler(0f, yaw, 0f);
                }
                else
                {
                    world = CoordSpace.RealToLocal(x, y, z);
                    rotation = Quaternion.Euler(0f, yaw, 0f);
                }
                remote.Avatar.transform.SetPositionAndRotation(world, rotation);
                Animate(remote, ship);
                // After the body: the tool hangs at its eye.
                remote.Tools.Apply(remote.Avatar, pitch, remote.Crouching, remote.Tool, remote.ToolUse);
            }
            foreach (uint netId in _gone) Remove(netId);
        }

        private void Spawn(Remote remote)
        {
            remote.Avatar = AvatarFactory.Create(remote.NetId, _net.GetPlayerName(remote.NetId), out remote.Animator);
            if (remote.Animator != null)
            {
                foreach (AnimatorControllerParameter parameter in remote.Animator.parameters)
                {
                    if (parameter.name == "walking" && parameter.type == AnimatorControllerParameterType.Bool) remote.HasWalking = true;
                    if (parameter.name == "stand" && parameter.type == AnimatorControllerParameterType.Bool) remote.HasStand = true;
                    if (parameter.name == "vertical" && parameter.type == AnimatorControllerParameterType.Float) remote.HasVertical = true;
                    if (parameter.name == "horizontal" && parameter.type == AnimatorControllerParameterType.Float) remote.HasHorizontal = true;
                }
            }
            Plugin.Log.LogInfo("[Players] Avatar created for " + _net.GetPlayerName(remote.NetId) + " (NetId " + remote.NetId + ")");
        }

        /// <summary>Drives the crew model's own walk parameters from how fast the avatar moves over the deck or the ground.</summary>
        private static void Animate(Remote remote, ShipHullMover ship)
        {
            // Speed is measured in the frame the player walks in: on a sailing ship the world position
            // changes even when the player stands still.
            Vector3 framePos = remote.OnShip && ship != null
                ? ship.transform.InverseTransformPoint(remote.Avatar.transform.position)
                : remote.Avatar.transform.position;
            float dt = Time.unscaledDeltaTime;
            if (remote.HasLastWorld && dt > 0.0001f)
            {
                Vector3 step = framePos - remote.LastWorld;
                step.y = 0f;
                float speed = step.magnitude / dt;
                if (speed < 30f) remote.Speed = Mathf.Lerp(remote.Speed, speed, Mathf.Clamp01(dt * 8f));
            }
            remote.LastWorld = framePos;
            remote.HasLastWorld = true;

            if (remote.Animator == null) return;
            // The crew controller rests seated: "stand" is false until a crew member gets a task.
            if (remote.HasStand) remote.Animator.SetBool("stand", true);
            bool walking = remote.Speed > 0.25f;
            if (remote.HasWalking) remote.Animator.SetBool("walking", walking);
            if (remote.HasVertical) remote.Animator.SetFloat("vertical", walking ? 0.5f : 0f, 0.15f, dt);
            if (remote.HasHorizontal) remote.Animator.SetFloat("horizontal", 0f, 0.15f, dt);
        }

        public void Remove(uint netId)
        {
            if (!_remotes.TryGetValue(netId, out Remote remote)) return;
            if (remote.Avatar != null) Object.Destroy(remote.Avatar);
            _remotes.Remove(netId);
        }

        public void Clear()
        {
            foreach (Remote remote in _remotes.Values)
                if (remote.Avatar != null) Object.Destroy(remote.Avatar);
            _remotes.Clear();
            _hasLastFramePos = false;
        }

        /// <summary>Diagnostics: where a remote avatar is, for the overlay and the dev console.</summary>
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            foreach (Remote remote in _remotes.Values)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(remote.NetId).Append(remote.OnShip ? " ship" : " real");
                if (remote.Avatar != null) sb.Append(' ').Append(remote.Avatar.transform.position.ToString("F1"));
                else sb.Append(" (no avatar)");
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }
    }
}
