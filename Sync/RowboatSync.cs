using System.Reflection;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The ship's rowboat once it is in the water (launching and stowing it is a control,
    /// <see cref="ControlKind.Rowboat"/>). It is a second vessel with a body of its own, and it is
    /// moved by one copy at a time: the copy of the player who is in it, otherwise the host's.
    /// That copy runs the game's own physics — oars, waves, the player's weight — and sends the
    /// pose in real space; the other copies hold their boat kinematic and move it along the
    /// buffered poses, as a guest does with the ship's hull.
    ///
    /// The first player aboard keeps the boat while it stays aboard. A guest that steps in simply
    /// starts sending; the host takes that as a claim unless the boat is already somebody's, and a
    /// guest that sees the boat held by another follows instead.
    /// </summary>
    internal sealed class RowboatSync
    {
        private struct Snap
        {
            public long Tick;
            public double X, Y, Z;
            public Quaternion Rot;
            public Vector3 Vel;
        }

        private const float TeleportDistance = 10f;
        private const float MaxExtrapolateMs = 250f;
        /// <summary>A driving guest that said nothing for this long has left the boat (or the session).</summary>
        private const float ClaimTimeout = 0.6f;

        private static readonly FieldInfo DeployedField = AccessTools.Field(typeof(ShipHullMover), "deployedRowboat");
        private static readonly FieldInfo StowedOarsField = AccessTools.Field(typeof(RowboatController), "stowedOars");
        private static readonly FieldInfo DeployedOarsField = AccessTools.Field(typeof(RowboatController), "deployedOars");

        private readonly CoopNet _net;
        private readonly BoatSync _clock;
        private readonly SnapshotBuffer<Snap> _buf = new SnapshotBuffer<Snap>(s => s.Tick, 32);
        private float _nextSendAt;

        // Host: the guest whose copy moves the boat, 0 when it is the host's own.
        private uint _guestDriver;
        private float _guestHeardAt = -100f;
        // Guest: what the host last said about whose boat it is.
        private uint _statedDriver;
        private bool _statedHeld;
        private float _statedAt = -100f;
        private bool _remoteRowing;
        private Rigidbody _slaved;

        public int Sent { get; private set; }
        public int Received { get; private set; }
        public float LastError { get; private set; }
        /// <summary>This copy's physics moves the boat.</summary>
        public bool Driving { get; private set; }

        /// <summary>The launched rowboat of this copy; null while it is stowed.</summary>
        public static GameObject Boat { get; private set; }
        /// <summary>This copy's boat follows another copy's. Read by the player patch.</summary>
        public static bool Slave { get; private set; }
        public static Vector3 SlaveVelocity { get; private set; }

        public RowboatSync(CoopNet net, BoatSync clock) { _net = net; _clock = clock; }

        /// <summary>The rowboat that belongs to a ship, launched or not.</summary>
        public static GameObject Of(ShipHullMover ship)
        {
            if (ship == null) return null;
            var boat = DeployedField?.GetValue(ship) as GameObject;
            if (boat != null) return boat;
            DataPersistenceManager dp = GameAccess.Dp;
            if (dp == null) return null;
            return ship.isCaelean ? dp.caeleanRowboat : dp.imperialRowboat;
        }

        private static bool LocalAboard()
        {
            PlayerHandler player = GameAccess.Player;
            return Boat != null && player != null && player.ship == Boat;
        }

        /// <summary>Every frame, from Update: is the boat in the water on this copy.</summary>
        public void FrameTick()
        {
            ShipHullMover ship = GameAccess.ActiveShip();
            GameObject boat = ship != null && ship.rowboatDeployed ? Of(ship) : null;
            if (boat != null && !boat.activeInHierarchy) boat = null;
            if (boat == Boat) return;
            Release();
            Boat = boat;
            _buf.Clear();
            _guestDriver = 0;
            _statedDriver = 0;
            _statedHeld = false;
        }

        /// <summary>From FixedUpdate, on every copy: move the boat, or say where it is.</summary>
        public void FixedTick()
        {
            Driving = false;
            if (Boat == null) { Slave = false; return; }
            Rigidbody body = Boat.GetComponent<Rigidbody>();
            if (body == null) return;
            float now = Time.unscaledTime;
            bool aboard = LocalAboard();

            bool drive;
            if (_net.IsHost)
            {
                if (_guestDriver != 0 && (now - _guestHeardAt > ClaimTimeout || !_net.HasMember(_guestDriver)))
                {
                    CoopTrace.Note("Rowboat", "back to the host", _guestDriver.ToString());
                    _guestDriver = 0;
                    _buf.Clear();
                }
                drive = _guestDriver == 0;
            }
            else
            {
                // The host's word is a second old at most; with none, the boat is nobody's.
                bool held = _statedHeld && now - _statedAt < 1f;
                drive = aboard && (!held || _statedDriver == _net.MyNetId);
            }

            if (drive)
            {
                if (Slave) { _buf.Clear(); ShowOars(false); }
                Release();
                Driving = true;
                if (now < _nextSendAt) return;
                _nextSendAt = now + 1f / Mathf.Clamp(Plugin.SendRateHz.Value, 1, 60);
                Send(body, aboard);
                return;
            }
            Follow(body);
        }

        private void Send(Rigidbody body, bool aboard)
        {
            var msg = new RowboatStateMsg { Driver = _net.MyNetId, Tick = _clock.PhysicsTick };
            if (aboard) msg.Flags |= RowboatFlags.Held;
            RowboatController rowing = Boat.GetComponent<RowboatController>();
            if (rowing != null && rowing.piloting) msg.Flags |= RowboatFlags.Rowing;
            CoordSpace.LocalToReal(body.position, out msg.X, out msg.Y, out msg.Z);
            Quaternion rot = body.rotation;
            msg.Qx = rot.x; msg.Qy = rot.y; msg.Qz = rot.z; msg.Qw = rot.w;
            Vector3 v = body.linearVelocity;
            msg.Vx = v.x; msg.Vy = v.y; msg.Vz = v.z;
            if (_net.IsHost) _net.Broadcast(msg, DeliveryMethod.Unreliable);
            else _net.SendToHost(msg, DeliveryMethod.Unreliable);
            Sent++;
        }

        private void Follow(Rigidbody body)
        {
            if (!Sample(_clock.PhysicsTick, out double x, out double y, out double z, out Quaternion rot)) return;
            Slave = true;
            _slaved = body;
            if (!body.isKinematic) body.isKinematic = true;
            // The game interpolates a vessel only while the local player stands on it; a boat that
            // is watched from the ship would move in physics steps.
            if (body.interpolation != RigidbodyInterpolation.Interpolate) body.interpolation = RigidbodyInterpolation.Interpolate;
            ShowOars(_remoteRowing);

            Vector3 target = CoordSpace.RealToLocal(x, y, z);
            LastError = Vector3.Distance(body.position, target);
            if (LastError > TeleportDistance)
            {
                Boat.transform.SetPositionAndRotation(target, rot);
                body.position = target;
                body.rotation = rot;
                Physics.SyncTransforms();
                return;
            }
            body.MovePosition(target);
            body.MoveRotation(rot);
        }

        // -----------------------------------------------------------------
        // Incoming
        // -----------------------------------------------------------------

        public void OnState(RowboatStateMsg msg, NetPeer from)
        {
            var rot = new Quaternion(msg.Qx, msg.Qy, msg.Qz, msg.Qw);
            float norm = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
            if (norm < 0.5f || norm > 1.5f || float.IsNaN(norm)) return;
            float now = Time.unscaledTime;

            if (_net.IsHost)
            {
                uint sender = _net.PlayerNetIdForPeer(from);
                if (sender == 0 || Boat == null) return;
                if (_guestDriver != sender)
                {
                    // The boat is somebody's already: the host's own player is in it, or another guest.
                    if (_guestDriver != 0 || LocalAboard()) return;
                    _guestDriver = sender;
                    _buf.Clear();
                    CoopTrace.Note("Rowboat", "taken", sender.ToString());
                }
                _guestHeardAt = now;
                msg.Driver = sender;
                msg.Flags |= RowboatFlags.Held;
                _net.Broadcast(msg, DeliveryMethod.Unreliable, readyOnly: true, exceptNetId: sender);
            }
            else
            {
                if (_statedDriver != msg.Driver) _buf.Clear();
                _statedDriver = msg.Driver;
                _statedHeld = (msg.Flags & RowboatFlags.Held) != 0;
                _statedAt = now;
                if (msg.Driver == _net.MyNetId) return;
            }

            _remoteRowing = (msg.Flags & RowboatFlags.Rowing) != 0;
            _buf.Push(new Snap
            {
                Tick = msg.Tick, X = msg.X, Y = msg.Y, Z = msg.Z,
                Rot = rot.normalized, Vel = new Vector3(msg.Vx, msg.Vy, msg.Vz),
            });
            Received++;
        }

        /// <summary>Host: a player left the session.</summary>
        public void RemovePlayer(uint netId)
        {
            if (_guestDriver == netId) _guestHeardAt = -100f;
        }

        private bool Sample(long serverNow, out double x, out double y, out double z, out Quaternion rot)
        {
            x = y = z = 0.0;
            SlaveVelocity = Vector3.zero;
            rot = Quaternion.identity;
            if (_buf.Count == 0) return false;
            long renderTick = serverNow - Plugin.InterpDelayMs.Value;

            Snap first = _buf[0];
            if (renderTick <= first.Tick) { x = first.X; y = first.Y; z = first.Z; rot = first.Rot; SlaveVelocity = first.Vel; return true; }

            Snap last = _buf[_buf.Count - 1];
            if (renderTick >= last.Tick)
            {
                float ahead = Mathf.Min(renderTick - last.Tick, MaxExtrapolateMs) / 1000f;
                x = last.X + last.Vel.x * ahead; y = last.Y + last.Vel.y * ahead; z = last.Z + last.Vel.z * ahead;
                rot = last.Rot;
                if (renderTick - last.Tick <= MaxExtrapolateMs) SlaveVelocity = last.Vel;
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
                SlaveVelocity = Vector3.Lerp(a.Vel, b.Vel, f);
                return true;
            }
            x = last.X; y = last.Y; z = last.Z; rot = last.Rot;
            return true;
        }

        // The oars lie in the boat until somebody rows. On a copy that only follows, the game's own
        // switch is never thrown, so the two sets are shown by hand — unless the local player sits
        // at the oars here too, and the game shows them itself.
        private void ShowOars(bool rowing)
        {
            if (Boat == null) return;
            RowboatController controller = Boat.GetComponent<RowboatController>();
            if (controller == null || controller.piloting) return;
            var stowed = StowedOarsField?.GetValue(controller) as GameObject;
            var deployed = DeployedOarsField?.GetValue(controller) as GameObject;
            if (stowed != null && stowed.activeSelf == rowing) stowed.SetActive(!rowing);
            if (deployed != null && deployed.activeSelf != rowing) deployed.SetActive(rowing);
        }

        // The boat's body is the game's again: it floats by itself.
        private void Release()
        {
            Slave = false;
            SlaveVelocity = Vector3.zero;
            if (_slaved != null)
            {
                if (_slaved.isKinematic) _slaved.isKinematic = false;
                // Back to the game's own setting for a vessel the player is not on.
                if (!LocalAboard()) _slaved.interpolation = RigidbodyInterpolation.None;
            }
            _slaved = null;
        }

        public void Clear()
        {
            ShowOars(false);
            Release();
            Boat = null;
            _buf.Clear();
            _guestDriver = 0;
            _statedDriver = 0;
            _statedHeld = false;
            _remoteRowing = false;
            Driving = false;
            Sent = Received = 0;
            LastError = 0f;
        }

        public string Describe()
        {
            if (Boat == null) return "stowed";
            string who = Driving ? "moved here" : Slave ? "following, off by " + LastError.ToString("F2") + " m" : "waiting for a pose";
            if (_net.IsHost && _guestDriver != 0) who += "  in the hands of " + _guestDriver;
            return "in the water, " + who + "  sent " + Sent + "  received " + Received;
        }

        public void Dump(StateDump d)
        {
            if (Boat == null) { d.Line("Rowboat", "state", "stowed"); return; }
            d.Line("Rowboat", "state", "in the water");
            CoordSpace.LocalToReal(Boat.transform.position, out double x, out double y, out double z);
            d.Line("Rowboat", "place", StateDump.F(x) + " " + StateDump.F(y) + " " + StateDump.F(z), 3f);
            d.Info("Rowboat", "moved", Driving ? "here" : "elsewhere");
        }
    }
}
