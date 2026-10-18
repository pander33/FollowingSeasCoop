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
    /// that machine. The launched rowboat is a frame of the same kind. Anywhere else the pose is in
    /// real space (<see cref="CoordSpace"/>).
    /// </summary>
    internal sealed class PlayerSync
    {
        private const byte FrameReal = 0, FrameShip = 1, FrameRowboat = 2;

        // The game's crouch sets state to crouching and its StateHandler sets it back in the same
        // Update; only this field lasts.
        private static readonly System.Reflection.FieldInfo CrouchingField =
            HarmonyLib.AccessTools.Field(typeof(PlayerHandler), "isCrouching");

        internal static bool IsCrouching(PlayerHandler player) =>
            CrouchingField != null ? (bool)CrouchingField.GetValue(player) : player.state == PlayerHandler.MovementState.crouching;

        private sealed class Remote
        {
            public uint NetId;
            public readonly NetTransform Pose = new NetTransform();
            public byte Frame;
            public bool HasFrame;
            public GameObject Avatar;
            public Animator Animator;
            public bool HasWalking, HasVertical, HasHorizontal, HasStand, HasLadder, HasSpeed;
            public Transform[] Spine;
            // Hips, spine, then upper leg, lower leg and foot of the left and the right leg.
            public Transform[] Legs;
            public float Low;
            // Upper arm, forearm and hand of the left and the right arm.
            public Transform[] Arms;
            // The left and the right middle finger's first bone: the line the hand points along.
            public Transform[] Fingers;
            public float LeftHold, RightHold;
            public float LeftCurl, RightCurl;
            public Vector3 LeftAt, RightAt;
            public Vector3 LastWorld;
            public bool HasLastWorld;
            public float Speed, Climb;
            public Vector3 Step;
            public PlayerFlags Flags;
            public bool Crouching;
            public byte Tool, ToolUse;
            public bool ToolRaised;
            public long ToolTick;
            // Hands on a control: from the feet of the pose they came with, in its frame; then in
            // this copy's scene for this frame.
            public bool HasHands;
            public double HandsX, HandsY, HandsZ;
            public Vector3 RightOffset, LeftOffset, RightControl, LeftControl;
            public readonly ToolVisuals Tools = new ToolVisuals();
            public HandPose Hands;
            public PalmGrip RightGrip, LeftGrip;
            public Quaternion RightGripOffset, LeftGripOffset;
        }

        private readonly CoopNet _net;
        private readonly Dictionary<uint, Remote> _remotes = new Dictionary<uint, Remote>();
        private readonly List<uint> _gone = new List<uint>();

        // Local capture.
        private float _nextSendAt;
        private byte _lastFrame;
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

            // "On the ship" means on the player ship itself, the hull every machine has; the launched
            // rowboat is the other deck every machine has. Any other vessel is not a shared frame,
            // so a player there is sent in real space.
            ShipHullMover ship = GameAccess.ActiveShip();
            byte frame = FrameReal;
            Transform deck = null;
            if (ship != null && player.ship != null && player.ship == ship.gameObject) { frame = FrameShip; deck = ship.transform; }
            else if (RowboatSync.Boat != null && player.ship == RowboatSync.Boat) { frame = FrameRowboat; deck = RowboatSync.Boat.transform; }

            var state = new PlayerStateMsg { NetId = _net.MyNetId, Tick = _net.Clock.ServerTick };
            Vector3 framePos;
            Vector3 forward;
            if (deck != null)
            {
                state.Flags |= frame == FrameShip ? PlayerFlags.OnShip : PlayerFlags.OnRowboat;
                framePos = deck.InverseTransformPoint(feet);
                forward = deck.InverseTransformDirection(orientation.forward);
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
            if (_hasLastFramePos && frame == _lastFrame && dt > 0.001f && dt < 0.5f)
            {
                Vector3 velocity = (framePos - _lastFramePos) / dt;
                // Faster than a person moves: a teleport or an origin shift, not motion.
                if (velocity.sqrMagnitude < 30f * 30f) { state.Vx = velocity.x; state.Vy = velocity.y; state.Vz = velocity.z; }
            }
            _lastFramePos = framePos;
            _hasLastFramePos = true;
            _lastFrame = frame;
            _lastCaptureAt = now;

            if (IsCrouching(player)) state.Flags |= PlayerFlags.Crouching;
            if (player.state == PlayerHandler.MovementState.swimming) state.Flags |= PlayerFlags.Swimming;
            if (player.state == PlayerHandler.MovementState.sprinting) state.Flags |= PlayerFlags.Sprinting;
            if (player.onLadder) state.Flags |= PlayerFlags.OnLadder;

            state.Tool = ToolVisuals.LocalKind();
            state.ToolRaised = ToolVisuals.LocalRaised(state.Tool);
            state.ToolUse = ToolVisuals.LocalUseCount;
            ToolVisuals.Measure(player, feet, deck != null ? deck.up : Vector3.up);

            if (HandsOnControl(player, camera, orientation, out Vector3 right, out Vector3 left))
            {
                Vector3 r = deck != null ? deck.InverseTransformPoint(right) - framePos : right - feet;
                Vector3 l = deck != null ? deck.InverseTransformPoint(left) - framePos : left - feet;
                state.HasHands = true;
                state.RightX = r.x; state.RightY = r.y; state.RightZ = r.z;
                state.LeftX = l.x; state.LeftY = l.y; state.LeftZ = l.z;
                ControlPalms(player.heldObject.transform, orientation, right, left, out Quaternion rq, out Quaternion lq);
                if (deck != null)
                {
                    rq = Quaternion.Inverse(deck.rotation) * rq;
                    lq = Quaternion.Inverse(deck.rotation) * lq;
                }
                state.RightQx = rq.x; state.RightQy = rq.y; state.RightQz = rq.z; state.RightQw = rq.w;
                state.LeftQx = lq.x; state.LeftQy = lq.y; state.LeftQz = lq.z; state.LeftQw = lq.w;
            }

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

        // How far apart the hands are on a bar or a rope, and a wheel's grips from its centre, as
        // parts of its size: ten and two o'clock.
        private const float HandSpread = 0.2f, WheelSide = 0.35f, WheelUp = 0.25f;

        /// <summary>
        /// Where the local player's hands are on the control it works: the game's held object while
        /// it interacts — the wheel or whipstaff, a rope, a reef or winch handle, a pump, a capstan.
        /// The hands close where its collider comes nearest the chest; on a long part (a bar, a rope)
        /// one above the other along it, else side by side, and on a wheel at its rim.
        /// A carried thing is the registry's, a ladder the climb's.
        /// </summary>
        private static bool HandsOnControl(PlayerHandler player, GameObject camera, Transform orientation, out Vector3 right, out Vector3 left)
        {
            right = left = default;
            GameObject held = player.isInteracting ? player.heldObject : null;
            if (held == null || camera == null || player.onLadder) return false;
            if (held.GetComponent<ObjectInteractionHandler>() != null || held.GetComponent<LadderController>() != null) return false;
            Collider collider = held.GetComponent<Collider>();
            if (collider == null) collider = held.GetComponentInChildren<Collider>();
            if (collider == null) return false;

            Transform ship = player.ship != null ? player.ship.transform : null;
            Vector3 up = ship != null ? ship.up : Vector3.up, side = orientation.right;
            Vector3 chest = camera.transform.position - up * 0.35f;
            SteeringController steering = held.GetComponentInParent<SteeringController>();
            if (steering != null && steering.isWheelType)
            {
                Bounds wheel = ControlBounds(collider);
                Vector3 size = Vector3.Scale(wheel.size, collider.transform.lossyScale);
                Vector3 normal = ThinAxis(collider.transform, size);
                Vector3 wheelUp = Vector3.ProjectOnPlane(up, normal).normalized;
                if (wheelUp.sqrMagnitude < 0.0001f)
                    wheelUp = Vector3.ProjectOnPlane(Mathf.Abs(Vector3.Dot(collider.transform.up, normal)) < 0.9f
                        ? collider.transform.up : collider.transform.right, normal).normalized;
                Vector3 wheelSide = Vector3.Cross(wheelUp, normal).normalized;
                if (Vector3.Dot(wheelSide, side) < 0f) wheelSide = -wheelSide;
                float radius = Mathf.Max(Mathf.Abs(size.x), Mathf.Max(Mathf.Abs(size.y), Mathf.Abs(size.z))) * 0.5f;
                Vector3 center = collider.transform.TransformPoint(wheel.center);
                right = center + (wheelSide * WheelSide + wheelUp * WheelUp).normalized * radius;
                left = center + (-wheelSide * WheelSide + wheelUp * WheelUp).normalized * radius;
                return true;
            }

            Vector3 grip = collider is MeshCollider mesh && !mesh.convex
                ? collider.bounds.ClosestPoint(chest) : collider.ClosestPoint(chest);
            // Not a box, sphere, capsule or convex mesh: the point comes back as it went in.
            if (grip == chest) grip = collider.bounds.ClosestPoint(chest);
            Vector3 along = side;
            if (collider is BoxCollider box)
            {
                Vector3 s = Vector3.Scale(box.size, box.transform.lossyScale);
                Vector3 axis = s.x >= s.y && s.x >= s.z ? box.transform.right : s.y >= s.z ? box.transform.up : box.transform.forward;
                float longest = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                float rest = s.x + s.y + s.z - longest - Mathf.Min(s.x, Mathf.Min(s.y, s.z));
                if (longest > rest * 3f) along = axis;
            }
            else if (collider is CapsuleCollider capsule)
            {
                along = capsule.transform.TransformDirection(capsule.direction == 0 ? Vector3.right
                    : capsule.direction == 1 ? Vector3.up : Vector3.forward);
            }
            else
            {
                Vector3 s = collider.bounds.size;
                float longest = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                if (longest == s.y && s.y > 3f * Mathf.Max(s.x, s.z)) along = up;
            }
            // A bar upright: the right hand above. Lying: the right hand to the right.
            if (Vector3.Dot(along, up + side) < 0f) along = -along;
            right = grip + along * (HandSpread * 0.5f);
            left = grip - along * (HandSpread * 0.5f);
            return true;
        }

        private static Bounds ControlBounds(Collider collider)
        {
            Transform space = collider.transform;
            Bounds local;
            if (collider is BoxCollider box) local = new Bounds(box.center, box.size);
            else if (collider is MeshCollider mesh && mesh.sharedMesh != null) local = mesh.sharedMesh.bounds;
            else if (collider is SphereCollider sphere) local = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
            else if (collider is CapsuleCollider capsule)
            {
                Vector3 size = Vector3.one * (capsule.radius * 2f);
                size[capsule.direction] = capsule.height;
                local = new Bounds(capsule.center, size);
            }
            else
            {
                Bounds world = collider.bounds;
                local = new Bounds(space.InverseTransformPoint(world.center), Vector3.zero);
                for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                        for (int z = -1; z <= 1; z += 2)
                            local.Encapsulate(space.InverseTransformPoint(world.center +
                                Vector3.Scale(world.extents, new Vector3(x, y, z))));
            }
            return local;
        }

        private static Vector3 ThinAxis(Transform space, Vector3 size) =>
            Mathf.Abs(size.x) <= Mathf.Abs(size.y) && Mathf.Abs(size.x) <= Mathf.Abs(size.z)
                ? space.right : Mathf.Abs(size.y) <= Mathf.Abs(size.z) ? space.up : space.forward;

        private static void ControlPalms(Transform control, Transform body, Vector3 right, Vector3 left,
            out Quaternion rightRotation, out Quaternion leftRotation)
        {
            Collider collider = control.GetComponent<Collider>();
            if (collider == null) collider = control.GetComponentInChildren<Collider>();
            Transform space = collider.transform;
            Bounds local = ControlBounds(collider);
            Vector3 along = (right - left).normalized;
            SteeringController steering = control.GetComponentInParent<SteeringController>();
            bool wheel = steering != null && steering.isWheelType;
            Vector3 size = Vector3.Scale(local.size, space.lossyScale);
            Vector3 thin = ThinAxis(space, size);
            Quaternion Frame(Vector3 point, bool isRight)
            {
                Vector3 normal;
                Vector3 axis = along;
                if (wheel)
                {
                    Vector3 radial = Vector3.ProjectOnPlane(point - space.TransformPoint(local.center), thin).normalized;
                    axis = Vector3.Cross(thin, radial).normalized;
                    normal = thin;
                    if (Vector3.Dot(normal, point - body.position) < 0f) normal = -normal;
                }
                else
                {
                    Vector3 outward;
                    if (collider is SphereCollider sphere)
                        outward = point - space.TransformPoint(sphere.center);
                    else if (collider is CapsuleCollider capsule)
                    {
                        Vector3 center = space.TransformPoint(capsule.center);
                        Vector3 capsuleAxis = space.TransformDirection(capsule.direction == 0 ? Vector3.right
                            : capsule.direction == 1 ? Vector3.up : Vector3.forward);
                        Vector3 scale = space.lossyScale;
                        float axialScale = Mathf.Abs(scale[capsule.direction]);
                        float radialScale = Mathf.Max(Mathf.Abs(scale[(capsule.direction + 1) % 3]),
                            Mathf.Abs(scale[(capsule.direction + 2) % 3]));
                        float half = Mathf.Max(0f, capsule.height * axialScale * 0.5f - capsule.radius * radialScale);
                        Vector3 nearest = center + capsuleAxis * Mathf.Clamp(Vector3.Dot(point - center, capsuleAxis), -half, half);
                        outward = point - nearest;
                    }
                    else
                    {
                        Vector3 p = space.InverseTransformPoint(point) - local.center;
                        Vector3 e = local.extents;
                        Vector3 relative = new Vector3(e.x > 0f ? p.x / e.x : 0f,
                            e.y > 0f ? p.y / e.y : 0f, e.z > 0f ? p.z / e.z : 0f);
                        outward = Mathf.Abs(relative.x) >= Mathf.Abs(relative.y) && Mathf.Abs(relative.x) >= Mathf.Abs(relative.z)
                            ? space.right * Mathf.Sign(relative.x) : Mathf.Abs(relative.y) >= Mathf.Abs(relative.z)
                                ? space.up * Mathf.Sign(relative.y) : space.forward * Mathf.Sign(relative.z);
                    }
                    normal = Vector3.ProjectOnPlane(-outward, along);
                    if (normal.sqrMagnitude < 0.0001f) normal = Vector3.ProjectOnPlane(thin, along);
                    if (normal.sqrMagnitude < 0.0001f) normal = Vector3.ProjectOnPlane(space.up, along);
                    if (normal.sqrMagnitude < 0.0001f) normal = Vector3.ProjectOnPlane(space.right, along);
                    normal.Normalize();
                }
                Vector3 fingers = Vector3.Cross(axis, normal).normalized * (isRight ? 1f : -1f);
                return Quaternion.LookRotation(fingers, normal);
            }
            rightRotation = Frame(right, true);
            leftRotation = Frame(left, false);
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
            byte frame = (msg.Flags & PlayerFlags.OnShip) != 0 ? FrameShip
                : (msg.Flags & PlayerFlags.OnRowboat) != 0 ? FrameRowboat : FrameReal;
            // ToolTick is the newest tick taken: a late pose of the frame before must not clear the new one's.
            if (remote.HasFrame && remote.Frame != frame && msg.Tick < remote.ToolTick) return;
            if (!remote.HasFrame || remote.Frame != frame)
            {
                // Positions of two frames cannot be interpolated.
                remote.Pose.Clear();
                remote.Frame = frame;
                remote.HasFrame = true;
                remote.HasLastWorld = false;
            }
            // Poses are interpolated; these are taken as they are, so a late packet must not bring an old tool back.
            if (msg.Tick >= remote.ToolTick)
            {
                remote.ToolTick = msg.Tick;
                remote.Flags = msg.Flags;
                remote.Crouching = (msg.Flags & PlayerFlags.Crouching) != 0;
                remote.Tool = msg.Tool;
                remote.ToolUse = msg.ToolUse;
                remote.ToolRaised = msg.ToolRaised;
                remote.HasHands = msg.HasHands;
                remote.HandsX = msg.X; remote.HandsY = msg.Y; remote.HandsZ = msg.Z;
                remote.RightOffset = new Vector3(msg.RightX, msg.RightY, msg.RightZ);
                remote.LeftOffset = new Vector3(msg.LeftX, msg.LeftY, msg.LeftZ);
                remote.RightGripOffset = new Quaternion(msg.RightQx, msg.RightQy, msg.RightQz, msg.RightQw);
                remote.LeftGripOffset = new Quaternion(msg.LeftQx, msg.LeftQy, msg.LeftQz, msg.LeftQw);
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
                Transform deck = remote.Frame == FrameShip ? (ship != null ? ship.transform : null)
                    : remote.Frame == FrameRowboat ? (RowboatSync.Boat != null ? RowboatSync.Boat.transform : null) : null;
                if (remote.Frame != FrameReal && deck == null)
                {
                    // Its deck is not here (yet): a pose on it cannot be placed.
                    if (remote.Avatar != null && remote.Avatar.activeSelf) remote.Avatar.SetActive(false);
                    continue;
                }

                if (remote.Avatar == null) Spawn(remote);
                if (!remote.Avatar.activeSelf) remote.Avatar.SetActive(true);

                Vector3 world;
                Quaternion rotation;
                if (deck != null)
                {
                    world = deck.TransformPoint(new Vector3((float)x, (float)y, (float)z));
                    rotation = deck.rotation * Quaternion.Euler(0f, yaw, 0f);
                }
                else
                {
                    world = CoordSpace.RealToLocal(x, y, z);
                    rotation = Quaternion.Euler(0f, yaw, 0f);
                }
                remote.Avatar.transform.SetPositionAndRotation(world, rotation);
                Animate(remote, deck);
                Crouch(remote);
                Look(remote, pitch);
                // After the body: the tool hangs at its eye.
                remote.Tools.Apply(remote.Avatar, remote.Spine?[3], pitch, remote.Crouching, remote.Tool, remote.ToolUse, remote.ToolRaised);
                if (remote.HasHands)
                {
                    Quaternion frameRotation = deck != null ? deck.rotation : Quaternion.identity;
                    remote.RightGrip.Rotation = frameRotation * remote.RightGripOffset;
                    remote.LeftGrip.Rotation = frameRotation * remote.LeftGripOffset;
                    // From the pose the hands came with, not the interpolated one: on the ship this is
                    // the hull-local point itself, where this copy's wheel or rope is too.
                    if (deck != null)
                    {
                        var feet = new Vector3((float)remote.HandsX, (float)remote.HandsY, (float)remote.HandsZ);
                        remote.RightControl = deck.TransformPoint(feet + remote.RightOffset);
                        remote.LeftControl = deck.TransformPoint(feet + remote.LeftOffset);
                    }
                    else
                    {
                        Vector3 feet = CoordSpace.RealToLocal(remote.HandsX, remote.HandsY, remote.HandsZ);
                        remote.RightControl = feet + remote.RightOffset;
                        remote.LeftControl = feet + remote.LeftOffset;
                    }
                }
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
                    if (parameter.name == "onladder" && parameter.type == AnimatorControllerParameterType.Bool) remote.HasLadder = true;
                    if (parameter.name == "speed" && parameter.type == AnimatorControllerParameterType.Float) remote.HasSpeed = true;
                }
                if (remote.Animator.isHuman)
                {
                    remote.Spine = new[]
                    {
                        remote.Animator.GetBoneTransform(HumanBodyBones.Chest),
                        remote.Animator.GetBoneTransform(HumanBodyBones.UpperChest),
                        remote.Animator.GetBoneTransform(HumanBodyBones.Neck),
                        remote.Animator.GetBoneTransform(HumanBodyBones.Head),
                    };
                    remote.Legs = new[]
                    {
                        remote.Animator.GetBoneTransform(HumanBodyBones.Hips),
                        remote.Animator.GetBoneTransform(HumanBodyBones.Spine),
                        remote.Animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg),
                        remote.Animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg),
                        remote.Animator.GetBoneTransform(HumanBodyBones.LeftFoot),
                        remote.Animator.GetBoneTransform(HumanBodyBones.RightUpperLeg),
                        remote.Animator.GetBoneTransform(HumanBodyBones.RightLowerLeg),
                        remote.Animator.GetBoneTransform(HumanBodyBones.RightFoot),
                    };
                    if (System.Array.IndexOf(remote.Legs, null) >= 0) remote.Legs = null;
                    remote.Arms = new[]
                    {
                        remote.Animator.GetBoneTransform(HumanBodyBones.LeftUpperArm),
                        remote.Animator.GetBoneTransform(HumanBodyBones.LeftLowerArm),
                        remote.Animator.GetBoneTransform(HumanBodyBones.LeftHand),
                        remote.Animator.GetBoneTransform(HumanBodyBones.RightUpperArm),
                        remote.Animator.GetBoneTransform(HumanBodyBones.RightLowerArm),
                        remote.Animator.GetBoneTransform(HumanBodyBones.RightHand),
                    };
                    if (System.Array.IndexOf(remote.Arms, null) >= 0) remote.Arms = null;
                    remote.Fingers = new[]
                    {
                        remote.Animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal),
                        remote.Animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal),
                    };
                    remote.Hands = new HandPose(remote.Animator);
                }
            }
            Plugin.Log.LogInfo("[Players] Avatar created for " + _net.GetPlayerName(remote.NetId) + " (NetId " + remote.NetId + ")");
        }

        // How much of the look's pitch each of chest, upper chest, neck and head takes.
        private static readonly float[] SpineShare = { 0.15f, 0.2f, 0.25f, 0.4f };

        /// <summary>
        /// Drives the crew model from how the avatar moves over the deck or the ground. The crew
        /// controller holds more than the game's crew use: one blend tree of idle (0, 0), walking
        /// (0.5) and running (1) in every direction, a kneel (<c>stand</c> false) and a ladder climb.
        /// </summary>
        private static void Animate(Remote remote, Transform deck)
        {
            // Speed is measured in the frame the player walks in: on a sailing ship the world position
            // changes even when the player stands still.
            Transform body = remote.Avatar.transform;
            Vector3 framePos = deck != null ? deck.InverseTransformPoint(body.position) : body.position;
            float dt = Time.unscaledDeltaTime;
            if (remote.HasLastWorld && dt > 0.0001f)
            {
                Vector3 step = (framePos - remote.LastWorld) / dt;
                if (step.sqrMagnitude < 30f * 30f)
                {
                    float k = Mathf.Clamp01(dt * 8f);
                    remote.Climb = Mathf.Lerp(remote.Climb, step.y, k);
                    step.y = 0f;
                    remote.Speed = Mathf.Lerp(remote.Speed, step.magnitude, k);
                    remote.Step = Vector3.Lerp(remote.Step, step, k);
                }
            }
            remote.LastWorld = framePos;
            remote.HasLastWorld = true;

            if (remote.Animator == null) return;
            Animator animator = remote.Animator;
            bool ladder = (remote.Flags & PlayerFlags.OnLadder) != 0;
            bool walking = !ladder && remote.Speed > 0.25f;
            // The controller's kneel is not used for a crouch: it has no walk, and going into it again
            // at every stop plays a whole sitting down. A crouch is the standing body bent low (Crouch).
            if (remote.HasStand) animator.SetBool("stand", true);
            if (remote.HasLadder) animator.SetBool("onladder", ladder);
            if (remote.HasSpeed) animator.SetFloat("speed", ladder ? (Mathf.Abs(remote.Climb) < 0.1f ? 0f : Mathf.Sign(remote.Climb)) : 1f);
            if (remote.HasWalking) animator.SetBool("walking", walking);

            Vector2 blend = Vector2.zero;
            if (walking)
            {
                Vector3 frameStep = deck != null ? deck.TransformDirection(remote.Step) : remote.Step;
                Vector3 local = body.InverseTransformDirection(frameStep);
                var dir = new Vector2(local.x, local.z);
                if (dir.sqrMagnitude > 0.0001f)
                    blend = dir.normalized * ((remote.Flags & PlayerFlags.Sprinting) != 0 ? 1f : 0.5f);
            }
            if (remote.HasHorizontal) animator.SetFloat("horizontal", blend.x, 0.15f, dt);
            if (remote.HasVertical) animator.SetFloat("vertical", blend.y, 0.15f, dt);

            // A crouching player moves slowly: its walk is the walk at a slower pace.
            bool low = remote.Crouching && !ladder && (remote.Flags & PlayerFlags.Swimming) == 0;
            remote.Low = Mathf.MoveTowards(remote.Low, low ? 1f : 0f, dt * 4f);
            animator.speed = low && walking ? Mathf.Clamp(remote.Speed / CrouchWalkPace, 0.4f, 1f) : 1f;
        }

        // How far the hips go down in a crouching walk, how far the body leans forward, and the
        // speed, metres a second, at which the walk is played at its own pace.
        private const float CrouchDrop = 0.55f, CrouchLean = 30f, CrouchWalkPace = 1.4f;

        /// <summary>
        /// A crouch, which the crew controller has none of: the body as the animator posed it, standing
        /// or walking, with the hips lowered, the feet kept where the pose puts them and the knees
        /// bent forward to reach them.
        /// </summary>
        private static void Crouch(Remote remote)
        {
            if (remote.Legs == null || remote.Low <= 0f) return;
            Transform body = remote.Avatar.transform;
            Transform[] legs = remote.Legs;
            Vector3 leftFoot = legs[4].position, rightFoot = legs[7].position;
            legs[0].position -= body.up * (CrouchDrop * remote.Low);
            legs[1].rotation = Quaternion.AngleAxis(CrouchLean * remote.Low, body.right) * legs[1].rotation;
            Vector3 ahead = body.forward * 0.5f;
            AvatarIK.Reach(legs[2], legs[3], legs[4], leftFoot, legs[3].position + ahead, true);
            AvatarIK.Reach(legs[5], legs[6], legs[7], rightFoot, legs[6].position + ahead, true);
        }

        /// <summary>
        /// After the animator has posed the body: chest, neck and head bend with the look. The animator
        /// poses every bone anew each frame, so this does not add up.
        /// </summary>
        private static void Look(Remote remote, float pitch)
        {
            if (remote.Spine == null || (remote.Flags & PlayerFlags.OnLadder) != 0) return;
            Vector3 right = remote.Avatar.transform.right;
            pitch = Mathf.Clamp(pitch, -70f, 70f);
            for (int i = 0; i < remote.Spine.Length; i++)
            {
                Transform bone = remote.Spine[i];
                if (bone != null) bone.rotation = Quaternion.AngleAxis(pitch * SpineShare[i], right) * bone.rotation;
            }
        }

        // The wrist is this far short of where the hand grips.
        private const float WristToGrip = 0.07f;

        /// <summary>
        /// The hands of every avatar on what it holds: a carried thing in both, held at its sides, the
        /// control it works, or the tool it has out. After the things are drawn (<see cref="ItemRegistry.LateTick"/>): a
        /// carried thing's pose of this frame is known only then.
        /// </summary>
        public void PoseHands(ItemRegistry items)
        {
            float dt = Time.unscaledDeltaTime;
            foreach (Remote remote in _remotes.Values)
            {
                if (remote.Avatar == null || !remote.Avatar.activeSelf || remote.Arms == null) continue;
                Transform body = remote.Avatar.transform;
                bool right = false, left = false, tool = false;
                Vector3 r = default, l = default;
                PalmGrip rg = remote.RightGrip, lg = remote.LeftGrip;
                // The climb moves the arms itself.
                if ((remote.Flags & PlayerFlags.OnLadder) == 0)
                {
                    Transform held = items != null ? items.HeldBy(remote.NetId) : null;
                    if (held != null && AvatarIK.Bounds(held.gameObject, out Bounds box))
                    {
                        float half = AvatarIK.Reach(box, body.right) + WristToGrip;
                        r = box.center + body.right * half;
                        l = box.center - body.right * half;
                        right = left = true;
                        rg = new PalmGrip(r - body.right * WristToGrip, body.up, -body.right, FingerGrip.Side);
                        lg = new PalmGrip(l + body.right * WristToGrip, body.up, body.right, FingerGrip.Side);
                    }
                    else if (remote.HasHands)
                    {
                        r = Wrist(remote.RightControl, remote.Arms[3]);
                        l = Wrist(remote.LeftControl, remote.Arms[0]);
                        right = left = true;
                        rg.Position = remote.RightControl;
                        lg.Position = remote.LeftControl;
                        rg.Fingers = lg.Fingers = FingerGrip.Cylinder;
                    }
                    else if (remote.Tools.Grips(out r, out l, out bool both))
                    {
                        right = tool = true; left = both;
                        remote.Tools.PalmGrips(out rg, out PalmGrip toolLeft, out _);
                        if (both) lg = toolLeft;
                    }
                }
                remote.RightHold = Mathf.MoveTowards(remote.RightHold, right ? 1f : 0f, dt * 5f);
                remote.LeftHold = Mathf.MoveTowards(remote.LeftHold, left ? 1f : 0f, dt * 5f);
                bool ladder = (remote.Flags & PlayerFlags.OnLadder) != 0;
                if (remote.Hands != null && !ladder)
                {
                    if (right && remote.Hands.Available(true)) r = remote.Hands.Wrist(true, rg);
                    else if (tool) r = Wrist(r, remote.Arms[3]);
                    if (left && remote.Hands.Available(false)) l = remote.Hands.Wrist(false, lg);
                    else if (tool) l = Wrist(l, remote.Arms[0]);
                    if (tool && !remote.Tools.AtEye && right && remote.Hands.Available(true))
                    {
                        for (int pass = 0; pass < 4; pass++)
                        {
                            Vector3 shift = ReachCorrection(remote.Arms[3], remote.Arms[4], remote.Arms[5], r);
                            if (left && remote.Hands.Available(false))
                            {
                                Vector3 leftShift = ReachCorrection(remote.Arms[0], remote.Arms[1], remote.Arms[2], l);
                                if (leftShift.sqrMagnitude > shift.sqrMagnitude) shift = leftShift;
                            }
                            if (shift.sqrMagnitude < 0.000001f) break;
                            remote.Tools.Shift(shift);
                            rg.Position += shift; lg.Position += shift;
                            r += shift; l += shift;
                        }
                        // Incompatible two-hand targets must not pull the primary hand off the tool.
                        Vector3 remaining = ReachCorrection(remote.Arms[3], remote.Arms[4], remote.Arms[5], r);
                        remote.Tools.Shift(remaining);
                        rg.Position += remaining; lg.Position += remaining;
                        r += remaining; l += remaining;
                    }
                    if (right) remote.RightAt = r;
                    if (left) remote.LeftAt = l;
                    Hold(remote.Arms[3], remote.Arms[4], remote.Arms[5], remote.Fingers[1], remote.RightAt, remote.RightHold, body, 1f);
                    Hold(remote.Arms[0], remote.Arms[1], remote.Arms[2], remote.Fingers[0], remote.LeftAt, remote.LeftHold, body, -1f);
                    remote.Hands.ApplyPalm(true, rg, remote.RightHold);
                    remote.Hands.ApplyPalm(false, lg, remote.LeftHold);
                    float rightContact = remote.Hands.Contact(true, rg);
                    float leftContact = remote.Hands.Contact(false, lg);
                    remote.RightCurl = Mathf.MoveTowards(remote.RightCurl, remote.RightHold * rightContact, dt * 5f);
                    remote.LeftCurl = Mathf.MoveTowards(remote.LeftCurl, remote.LeftHold * leftContact, dt * 5f);
                    remote.Hands.ApplyFingers(lg.Fingers, remote.LeftCurl, rg.Fingers, remote.RightCurl);
                    remote.RightGrip = rg; remote.LeftGrip = lg;
                    continue;
                }
                if (ladder)
                {
                    remote.RightCurl = remote.LeftCurl = 0f;
                    continue;
                }
                if (right) remote.RightAt = tool ? Wrist(r, remote.Arms[3]) : r;
                Hold(remote.Arms[3], remote.Arms[4], remote.Arms[5], remote.Fingers[1], remote.RightAt, remote.RightHold, body, 1f);
                // The tool goes into the right hand where the arm could take it, then the left takes
                // it there. One held to the eye stays at the eye.
                if (tool && !remote.Tools.AtEye && remote.RightHold >= 1f)
                {
                    Transform hand = remote.Arms[5];
                    Vector3 palm = hand.position + (hand.position - remote.Arms[4].position).normalized * WristToGrip;
                    remote.Tools.Shift(Vector3.ClampMagnitude(palm - r, 0.15f));
                    remote.Tools.Grips(out _, out l, out _);
                }
                if (left) remote.LeftAt = tool ? Wrist(l, remote.Arms[0]) : l;
                Hold(remote.Arms[0], remote.Arms[1], remote.Arms[2], remote.Fingers[0], remote.LeftAt, remote.LeftHold, body, -1f);
            }
        }

        public void RestoreHandAnimation()
        {
            foreach (Remote remote in _remotes.Values) remote.Hands?.RestoreFingers();
        }

        private static Vector3 ReachCorrection(Transform shoulder, Transform elbow, Transform wrist, Vector3 target)
        {
            float reach = Vector3.Distance(shoulder.position, elbow.position) + Vector3.Distance(elbow.position, wrist.position);
            Vector3 delta = target - shoulder.position;
            return shoulder.position + Vector3.ClampMagnitude(delta, reach * 0.99f) - target;
        }

        // The wrist for a hand that closes on a point: the palm goes on past the wrist, along the arm.
        private static Vector3 Wrist(Vector3 grip, Transform shoulder) =>
            grip - (grip - shoulder.position).normalized * WristToGrip;

        private static void Hold(Transform upper, Transform lower, Transform hand, Transform finger, Vector3 at, float weight, Transform body, float side)
        {
            if (weight <= 0f) return;
            // The elbow down, out to its side and a little back.
            Vector3 elbow = upper.position - body.up * 0.4f + body.right * (side * 0.25f) - body.forward * 0.1f;
            AvatarIK.Reach(upper, lower, hand, Vector3.Lerp(hand.position, at, weight), elbow, false);
            // A hand at rest hangs from the wrist; one that holds something is in line with the forearm.
            if (finger == null) return;
            Quaternion straight = Quaternion.FromToRotation(finger.position - hand.position, hand.position - lower.position);
            hand.rotation = Quaternion.Slerp(Quaternion.identity, straight, weight) * hand.rotation;
        }

        public void Remove(uint netId)
        {
            if (!_remotes.TryGetValue(netId, out Remote remote)) return;
            remote.Hands?.Dispose();
            if (remote.Avatar != null) Object.Destroy(remote.Avatar);
            _remotes.Remove(netId);
        }

        public void Clear()
        {
            foreach (Remote remote in _remotes.Values)
            {
                remote.Hands?.Dispose();
                if (remote.Avatar != null) Object.Destroy(remote.Avatar);
            }
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
                sb.Append(remote.NetId).Append(remote.Frame == FrameShip ? " ship" : remote.Frame == FrameRowboat ? " rowboat" : " real");
                if (remote.Avatar != null) sb.Append(' ').Append(remote.Avatar.transform.position.ToString("F1"));
                else sb.Append(" (no avatar)");
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }
    }
}
