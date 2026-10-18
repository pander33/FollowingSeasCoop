using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using LiteNetLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>Where a guest is on its way into (or out of) the host's world.</summary>
    internal enum GuestWorldState
    {
        None,
        /// <summary>Chunks of the host's world are arriving; the guest is still on the title screen.</summary>
        Receiving,
        /// <summary>The game is loading the world scene from the transferred data.</summary>
        Loading,
        InWorld,
        /// <summary>The session ended; the guest is going back to the title screen.</summary>
        Leaving,
    }

    /// <summary>
    /// Brings a guest into the host's world. The whole world is the game's one save object
    /// (<c>GameData</c>): the host captures it without writing a file, sends it packed, and the
    /// guest hands it to the game's own loader in place of a save file (<see cref="SavePatches"/>).
    /// A guest joins from the title screen only.
    /// </summary>
    internal sealed class WorldTransfer
    {
        /// <summary>The name the game sees as the save being loaded. No file of this name is written.</summary>
        public const string SessionSaveName = "coop-session";

        /// <summary>
        /// Guest: the transferred world as JSON, kept for the whole session. The game reloads the world
        /// on its own in places (coming back from a side scene); with the data gone it would read a save
        /// file that does not exist and start a new game instead.
        /// </summary>
        public static string PendingJson { get; private set; }

        /// <summary>Guest: this copy holds (or is loading) the host's world. Saving is blocked.</summary>
        public static bool GuestWorld { get; private set; }

        private const float LoadTimeoutSeconds = 180f;
        /// <summary>The game keeps its loading screen up for 2 s after the scene is in.</summary>
        private const float SettleSeconds = 3f;

        private readonly CoopNet _net;
        private readonly Action<string> _log;
        private int _nextTransferId = 1;

        // Guest side.
        private int _transferId;
        private byte[] _packed;
        private int _received;
        private int _nextChunk;
        private WorldBeginMsg _begin;
        private float _loadStartedAt;
        private float _inWorldSince = -1f;
        private bool _leaveIssued;
        // A failed return to the title screen is tried again a few times, not left half done.
        private int _leaveTries;
        private float _leaveRetryAt;

        public GuestWorldState State { get; private set; } = GuestWorldState.None;
        public string LastError { get; private set; } = "";
        public float ReceiveProgress => _begin == null || _begin.PackedBytes <= 0 ? 0f : (float)_received / _begin.PackedBytes;

        /// <summary>Guest: the session must end (the world could not be loaded, or the player left it).</summary>
        public event Action<string> OnGuestAbort;

        public WorldTransfer(CoopNet net, Action<string> log)
        {
            _net = net;
            _log = log;
        }

        // -----------------------------------------------------------------
        // Host
        // -----------------------------------------------------------------

        /// <summary>Host: capture the world as it is now and send it to one client.</summary>
        public bool HostSend(PeerSession session)
        {
            string json;
            try { json = GameAccess.CaptureWorldJson(); }
            catch (Exception e)
            {
                _log("[World] Capture failed: " + e);
                return false;
            }
            if (string.IsNullOrEmpty(json)) { _log("[World] Nothing to send: no world is loaded"); return false; }

            byte[] raw = Encoding.UTF8.GetBytes(json);
            byte[] packed = Pack(raw);
            int chunkCount = (packed.Length + Limits.WorldChunk - 1) / Limits.WorldChunk;
            int id = _nextTransferId++;

            session.Peer.Send(new WorldBeginMsg
            {
                TransferId = id,
                PackedBytes = packed.Length,
                RawBytes = raw.Length,
                ChunkCount = chunkCount,
                Crc = Crc32(raw),
            }, DeliveryMethod.ReliableOrdered);
            for (int i = 0; i < chunkCount; i++)
            {
                int offset = i * Limits.WorldChunk;
                int length = Math.Min(Limits.WorldChunk, packed.Length - offset);
                var data = new byte[length];
                Buffer.BlockCopy(packed, offset, data, 0, length);
                session.Peer.Send(new WorldChunkMsg { TransferId = id, Index = i, Data = data }, DeliveryMethod.ReliableOrdered);
            }
            session.Peer.Send(new WorldEndMsg { TransferId = id }, DeliveryMethod.ReliableOrdered);
            _log("[World] Sent the world to " + session.PlayerName + ": " + raw.Length + " bytes of JSON, " +
                 packed.Length + " packed, " + chunkCount + " chunk(s)");
            return true;
        }

        // -----------------------------------------------------------------
        // Guest: receiving
        // -----------------------------------------------------------------

        public void OnBegin(WorldBeginMsg msg)
        {
            if (State != GuestWorldState.None && State != GuestWorldState.Receiving) return;
            if (msg.PackedBytes <= 0 || msg.PackedBytes > Limits.WorldBytes || msg.RawBytes <= 0 || msg.ChunkCount <= 0)
            {
                Abort("The host sent a world of an impossible size");
                return;
            }
            _begin = msg;
            _transferId = msg.TransferId;
            _packed = new byte[msg.PackedBytes];
            _received = 0;
            _nextChunk = 0;
            LastError = "";
            State = GuestWorldState.Receiving;
        }

        public void OnChunk(WorldChunkMsg msg)
        {
            if (State != GuestWorldState.Receiving || msg.TransferId != _transferId) return;
            // The channel is reliable and ordered, so a gap means a broken sender, not a lost packet.
            if (msg.Index != _nextChunk || _received + msg.Data.Length > _packed.Length)
            {
                Abort("The world transfer is out of order");
                return;
            }
            Buffer.BlockCopy(msg.Data, 0, _packed, _received, msg.Data.Length);
            _received += msg.Data.Length;
            _nextChunk++;
        }

        public void OnEnd(WorldEndMsg msg)
        {
            if (State != GuestWorldState.Receiving || msg.TransferId != _transferId) return;
            string json;
            try
            {
                if (_received != _packed.Length) throw new InvalidDataException("got " + _received + " of " + _packed.Length + " bytes");
                byte[] raw = Unpack(_packed, _begin.RawBytes);
                if (Crc32(raw) != _begin.Crc) throw new InvalidDataException("checksum mismatch");
                json = Encoding.UTF8.GetString(raw);
                if (JsonUtility.FromJson<GameData>(json) == null) throw new InvalidDataException("not a game save");
            }
            catch (Exception e)
            {
                Abort("The host's world arrived damaged: " + e.Message);
                return;
            }
            finally { _packed = null; }

            if (!GameAccess.AtMainMenu || GameManager.instance == null)
            {
                Abort("The world can be joined from the title screen only");
                return;
            }

            _log("[World] Received the host's world (" + json.Length + " chars), loading");
            PendingJson = json;
            GuestWorld = true;
            _loadStartedAt = Time.realtimeSinceStartup;
            _inWorldSince = -1f;
            _leaveIssued = false;
            _leaveTries = 0;
            _leaveRetryAt = 0f;
            State = GuestWorldState.Loading;
            try
            {
                GlobalData.NewGame = false;
                GlobalData.SaveFileToLoad = SessionSaveName;
                GameManager.instance.LoadGame();
            }
            catch (Exception e)
            {
                PendingJson = null;
                GuestWorld = false;
                State = GuestWorldState.None;
                Abort("The game refused to load the world: " + e.Message);
            }
        }

        // -----------------------------------------------------------------
        // Guest: per-frame
        // -----------------------------------------------------------------

        public void Tick()
        {
            switch (State)
            {
                case GuestWorldState.Loading:
                    if (GameAccess.InWorld)
                    {
                        if (_inWorldSince < 0f) _inWorldSince = Time.realtimeSinceStartup;
                        if (Time.realtimeSinceStartup - _inWorldSince < SettleSeconds) return;
                        State = GuestWorldState.InWorld;
                        _log("[World] In the host's world");
                        _net.SendToHost(new ClientWorldLoadedMsg { Ok = true }, DeliveryMethod.ReliableOrdered);
                    }
                    else if (Time.realtimeSinceStartup - _loadStartedAt > LoadTimeoutSeconds)
                    {
                        Abort("The world did not load in " + (int)LoadTimeoutSeconds + " s");
                    }
                    break;

                case GuestWorldState.InWorld:
                    // The player used the game's own menu to go back to the title screen.
                    if (GameAccess.AtMainMenu) Abort("Left the host's world", loadFailed: false);
                    break;

                case GuestWorldState.Leaving:
                    TickLeaving();
                    break;
            }
        }

        private void TickLeaving()
        {
            if (GameAccess.AtMainMenu)
            {
                PendingJson = null;
                GuestWorld = false;
                State = GuestWorldState.None;
                _log("[World] Back on the title screen");
                return;
            }
            if (_leaveIssued) return;
            // A scene load in progress cannot be cancelled; wait for the world, then leave it.
            if (!GameAccess.InWorld)
            {
                if (Time.realtimeSinceStartup - _loadStartedAt > LoadTimeoutSeconds * 2f)
                {
                    _log("[World] The world never finished loading; giving up on leaving it");
                    PendingJson = null;
                    GuestWorld = false;
                    State = GuestWorldState.None;
                }
                return;
            }
            if (Time.realtimeSinceStartup < _leaveRetryAt) return;
            _leaveIssued = true;
            try { GameAccess.ReturnToMainMenu(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[World] Return to the title screen failed: " + e);
                if (++_leaveTries < 3)
                {
                    _leaveIssued = false;
                    _leaveRetryAt = Time.realtimeSinceStartup + 2f;
                }
            }
        }

        /// <summary>Guest: the session is over (link lost, kicked, or the player pressed Disconnect).</summary>
        public void EndSession()
        {
            _packed = null;
            switch (State)
            {
                case GuestWorldState.None:
                case GuestWorldState.Leaving:
                    return;
                case GuestWorldState.Receiving:
                    PendingJson = null;
                    GuestWorld = false;
                    State = GuestWorldState.None;
                    return;
                default:
                    State = GuestWorldState.Leaving;
                    return;
            }
        }

        private void Abort(string reason, bool loadFailed = true)
        {
            LastError = reason;
            _log("[World] " + reason);
            if (loadFailed && _net.IsClient) _net.SendToHost(new ClientWorldLoadedMsg { Ok = false, Detail = reason }, DeliveryMethod.ReliableOrdered);
            OnGuestAbort?.Invoke(reason);
        }

        // -----------------------------------------------------------------
        // Packing
        // -----------------------------------------------------------------

        private static byte[] Pack(byte[] raw)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
                    gzip.Write(raw, 0, raw.Length);
                return output.ToArray();
            }
        }

        private static byte[] Unpack(byte[] packed, int rawLength)
        {
            var raw = new byte[rawLength];
            using (var input = new MemoryStream(packed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                int total = 0;
                while (total < rawLength)
                {
                    int read = gzip.Read(raw, total, rawLength - total);
                    if (read <= 0) break;
                    total += read;
                }
                // The announced length is a promise: shorter or longer data is not the announced world.
                if (total != rawLength || gzip.ReadByte() != -1) throw new InvalidDataException("unpacked length mismatch");
            }
            return raw;
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] data)
        {
            if (_crcTable == null)
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                _crcTable = table;
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++) crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
