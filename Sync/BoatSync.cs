using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The player ship. The host's physics owns it: sails, buoyancy, collisions. The host sends the
    /// hull's pose in real space; a guest makes its own hull kinematic and moves it along the
    /// buffered poses, so both stand on the same ship at the same heel.
    ///
    /// The guest's copy of the game keeps running its ship code — a kinematic body ignores the
    /// forces it adds. The game itself switches the hull back to dynamic in several places (after a
    /// load, after an origin shift, after its own reset), so the guest re-asserts it every step.
    /// </summary>
    internal sealed class BoatSync
    {
        private struct Snap
        {
            public long Tick;
            public double X, Y, Z;
            public Quaternion Rot;
            public Vector3 Vel;
        }

        /// <summary>Further than this from the host's pose the hull is placed, not moved through the water.</summary>
        private const float TeleportDistance = 25f;
        private const float MaxExtrapolateMs = 250f;

        private readonly CoopNet _net;
        private readonly SnapshotBuffer<Snap> _buf = new SnapshotBuffer<Snap>(s => s.Tick, 32);
        private float _nextSendAt;

        // Diagnostics for the debug panel.
        public int Sent { get; private set; }
        public int Received { get; private set; }
        public int Teleports { get; private set; }
        public float LastError { get; private set; }
        public bool Driving { get; private set; }

        /// <summary>Guest: this copy's hull follows the host. Read by the patches.</summary>
        public static bool GuestSlave { get; private set; }
        /// <summary>Guest: how fast the host's hull moves at the moment this copy shows. Read by the patches.</summary>
        public static Vector3 GuestVelocity { get; private set; }

        public BoatSync(CoopNet net) { _net = net; }

        // Physics steps run in a burst at the start of a frame, so the wall clock read inside one says
        // when the code ran, not which moment the pose belongs to: stamping or sampling with it puts up
        // to a frame of jitter into the hull's motion. Poses are stamped and sampled on the physics
        // clock instead, mapped to the host clock once per frame.
        private double _physicsToHostMs;
        private bool _physicsClockSet;

        /// <summary>Every frame, from Update: refresh the mapping from game time to the host clock.</summary>
        public void FrameTick()
        {
            double offset = _net.Clock.ServerTick - Time.timeAsDouble * 1000.0;
            // A pause or a clock correction moves the mapping at once; otherwise it is only smoothed.
            if (!_physicsClockSet || System.Math.Abs(offset - _physicsToHostMs) > 50.0) _physicsToHostMs = offset;
            else _physicsToHostMs += (offset - _physicsToHostMs) * 0.05;
            _physicsClockSet = true;
        }

        internal long PhysicsTick => _physicsClockSet
            ? (long)(Time.fixedTimeAsDouble * 1000.0 + _physicsToHostMs)
            : _net.Clock.ServerTick;

        // -----------------------------------------------------------------
        // Host
        // -----------------------------------------------------------------

        /// <summary>Host, from FixedUpdate: send the hull's pose at the configured rate.</summary>
        public void HostFixedTick()
        {
            float now = Time.unscaledTime;
            if (now < _nextSendAt) return;
            _nextSendAt = now + 1f / Mathf.Clamp(Plugin.SendRateHz.Value, 1, 60);

            ShipHullMover ship = GameAccess.ActiveShip();
            if (ship == null) return;
            Rigidbody body = ship.GetComponent<Rigidbody>();
            if (body == null) return;

            var msg = new BoatStateMsg { Tick = PhysicsTick };
            CoordSpace.LocalToReal(body.position, out msg.X, out msg.Y, out msg.Z);
            Quaternion rot = body.rotation;
            msg.Qx = rot.x; msg.Qy = rot.y; msg.Qz = rot.z; msg.Qw = rot.w;
            if (!body.isKinematic)
            {
                Vector3 v = body.linearVelocity, w = body.angularVelocity;
                msg.Vx = v.x; msg.Vy = v.y; msg.Vz = v.z;
                msg.Wx = w.x; msg.Wy = w.y; msg.Wz = w.z;
            }
            _net.Broadcast(msg, DeliveryMethod.Unreliable);
            Sent++;
        }

        // -----------------------------------------------------------------
        // Guest
        // -----------------------------------------------------------------

        public void OnBoatState(BoatStateMsg msg)
        {
            if (!_net.IsClient) return;
            var rot = new Quaternion(msg.Qx, msg.Qy, msg.Qz, msg.Qw);
            // A zero or damaged quaternion would poison the transform.
            float norm = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
            if (norm < 0.5f || norm > 1.5f || float.IsNaN(norm)) return;
            _buf.Push(new Snap
            {
                Tick = msg.Tick, X = msg.X, Y = msg.Y, Z = msg.Z,
                Rot = rot.normalized, Vel = new Vector3(msg.Vx, msg.Vy, msg.Vz),
            });
            Received++;
        }

        /// <summary>Guest, from FixedUpdate: move the hull to where the host's hull was a moment ago.</summary>
        public void GuestFixedTick()
        {
            Driving = false;
            if (_buf.Count == 0) return;
            ShipHullMover ship = GameAccess.ActiveShip();
            if (ship == null) return;
            Rigidbody body = ship.GetComponent<Rigidbody>();
            if (body == null) return;
            if (!Sample(PhysicsTick, out double x, out double y, out double z, out Quaternion rot)) return;

            GuestSlave = true;
            Driving = true;
            if (!body.isKinematic) body.isKinematic = true;

            Vector3 target = CoordSpace.RealToLocal(x, y, z);
            LastError = Vector3.Distance(body.position, target);
            if (LastError > TeleportDistance)
            {
                // Far off (the first pose after a join, or a jump on the host): place the hull the way
                // the game does on a load, with the transform, not by sweeping the body through the sea.
                ship.transform.SetPositionAndRotation(target, rot);
                body.position = target;
                body.rotation = rot;
                Physics.SyncTransforms();
                Teleports++;
                return;
            }
            // MovePosition on a kinematic body keeps render interpolation and drags the jointed parts
            // (wheel, yards) along smoothly.
            body.MovePosition(target);
            body.MoveRotation(rot);
        }

        private bool Sample(long serverNow, out double x, out double y, out double z, out Quaternion rot)
        {
            x = y = z = 0.0;
            GuestVelocity = Vector3.zero;
            rot = Quaternion.identity;
            if (_buf.Count == 0) return false;
            long renderTick = serverNow - Plugin.InterpDelayMs.Value;

            Snap first = _buf[0];
            if (renderTick <= first.Tick) { x = first.X; y = first.Y; z = first.Z; rot = first.Rot; GuestVelocity = first.Vel; return true; }

            Snap last = _buf[_buf.Count - 1];
            if (renderTick >= last.Tick)
            {
                float ahead = Mathf.Min(renderTick - last.Tick, MaxExtrapolateMs) / 1000f;
                x = last.X + last.Vel.x * ahead; y = last.Y + last.Vel.y * ahead; z = last.Z + last.Vel.z * ahead;
                rot = last.Rot;
                // Past that the host has gone quiet (a pause): the hull stands still here.
                if (renderTick - last.Tick <= MaxExtrapolateMs) GuestVelocity = last.Vel;
                return true;
            }

            for (int i = 0; i < _buf.Count - 1; i++)
            {
                Snap a = _buf[i], b = _buf[i + 1];
                if (renderTick < a.Tick || renderTick > b.Tick) continue;
                float span = b.Tick - a.Tick;
                float f = span > 0 ? (renderTick - a.Tick) / span : 0f;
                x = a.X + (b.X - a.X) * f; y = a.Y + (b.Y - a.Y) * f; z = a.Z + (b.Z - a.Z) * f;
                rot = Quaternion.Slerp(a.Rot, b.Rot, f);
                GuestVelocity = Vector3.Lerp(a.Vel, b.Vel, f);
                return true;
            }
            x = last.X; y = last.Y; z = last.Z; rot = last.Rot;
            return true;
        }

        /// <summary>The session ended. A guest's world is unloaded next, so its hull is not restored.</summary>
        public void Clear()
        {
            _buf.Clear();
            GuestSlave = false;
            GuestVelocity = Vector3.zero;
            Driving = false;
            _physicsClockSet = false;
            Sent = Received = Teleports = 0;
            LastError = 0f;
        }

        public string Describe()
        {
            if (_net.IsHost) return "sent " + Sent;
            return "received " + Received + (Driving ? "  following, off by " + LastError.ToString("F2") + " m" : "  not following") +
                   "  jumps " + Teleports;
        }

        // -----------------------------------------------------------------
        // Patches
        // -----------------------------------------------------------------

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ShipHullMover), "Update")
                              ?? throw new System.MissingMethodException("ShipHullMover", "Update"),
                          prefix: new HarmonyMethod(typeof(BoatSync), nameof(ShipUpdatePrefix)));
            harmony.Patch(AccessTools.Method(typeof(PlayerHandler), "FixedUpdate")
                              ?? throw new System.MissingMethodException("PlayerHandler", "FixedUpdate"),
                          prefix: new HarmonyMethod(typeof(BoatSync), nameof(PlayerFixedPrefix)));
        }

        // The game's "Reset" key puts the ship into a mode where the player moves it by hand. On a
        // guest that would only move the guest's copy, against the host's pose. The frame in which
        // the key is pressed is skipped for the ship's Update, which is all that reads the key.
        private static bool ShipUpdatePrefix()
        {
            return PatchGuard.Prefix(() =>
            {
                if (!GuestSlave) return true;
                InteractionController controller = InteractionController.interactionInstance;
                if (controller == null) return true;
                var action = controller.GetInputController().actions.FindAction("Reset");
                return action == null || !action.WasPressedThisFrame();
            }, error => Plugin.Log.LogError("[BoatSync] " + error));
        }

        private static readonly System.Reflection.FieldInfo CrouchingField = AccessTools.Field(typeof(PlayerHandler), "isCrouching");

        // The game carries the player along with the ship by copying the hull's velocity to the
        // player's body every physics step — but only from a hull that is not kinematic, and a
        // guest's hull is. While the player stands on the game's level copy of the deck that does not
        // show; at a heel above 15 degrees the game takes the player off that copy, and the deck
        // would sail away from under a guest. This gives the guest's body the host's hull velocity,
        // by the game's own rule.
        private static void PlayerFixedPrefix(PlayerHandler __instance)
        {
            PatchGuard.Run(() =>
            {
                if (__instance.ship == null) return;
                Vector3 hull;
                // The same for a rowboat that another player's copy moves: it is kinematic here too.
                if (RowboatSync.Slave && __instance.ship == RowboatSync.Boat) hull = RowboatSync.SlaveVelocity;
                else
                {
                    if (!GuestSlave) return;
                    ShipHullMover ship = GameAccess.ActiveShip();
                    if (ship == null || __instance.ship != ship.gameObject) return;
                    hull = GuestVelocity;
                }
                Rigidbody body = __instance.GetRigidbody();
                if (body == null || body.isKinematic) return;
                Vector3 own = body.linearVelocity;
                bool crouching = CrouchingField != null && (bool)CrouchingField.GetValue(__instance);
                float y = crouching && hull.y < 0f ? hull.y * 2f : own.y;
                body.linearVelocity = new Vector3(hull.x, y, hull.z);
            }, error => Plugin.Log.LogError("[BoatSync] " + error));
        }
    }
}
