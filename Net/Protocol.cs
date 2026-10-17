using System;
using LiteNetLib;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    /// <summary>
    /// Single (de)serialization entry point (F1). Every packet on the wire is:
    ///   [ byte msgType ][ message payload ]
    /// Bump <see cref="Version"/> whenever the wire format of any message changes in a
    /// non-backward-compatible way; the handshake compares versions and a mismatch is rejected
    /// with a clear reason.
    /// </summary>
    public static class Protocol
    {
        /// <summary>Wire protocol version. Increment on any breaking format change.</summary>
        public const int Version = 14;

        /// <summary>Where the net layer reports a dropped packet. Set by the runtime layer.</summary>
        public static Action<string> Warn = _ => { };

        /// <summary>Writes [msgType][payload] into a fresh writer ready to send.</summary>
        public static NetDataWriter Write(INetMessage msg)
        {
            var w = new NetDataWriter();
            w.Put((byte)msg.Type);
            msg.Serialize(w);
            return w;
        }

        /// <summary>
        /// Reads one whole message. Returns null for an unknown type and for a truncated or corrupt
        /// payload, which would otherwise throw out of the receive callback and take down the whole
        /// frame's tick — the caller drops the packet instead.
        /// </summary>
        public static INetMessage Read(NetDataReader r, out MsgType type)
        {
            type = 0;
            if (r.AvailableBytes < 1) return null;
            type = (MsgType)r.GetByte();
            INetMessage msg = Create(type);
            if (msg == null) return null;
            try
            {
                msg.Deserialize(r);
                // LiteNetLib getters can read beyond the logical packet length when the pooled
                // backing array is larger; do not accept those stale buffer bytes.
                if (r.AvailableBytes < 0) throw new System.IO.InvalidDataException("Read past packet boundary");
            }
            catch (Exception e)
            {
                // Throttled: a corrupt or spoofed UDP stream on the port would otherwise write a
                // line per datagram.
                _malformedCount++;
                if (_malformedCount <= 5 || _malformedCount % 500 == 0)
                    Warn("[Protocol] Malformed " + type + " payload dropped (occurrence #" + _malformedCount + "): " + e.Message);
                return null;
            }
            return msg;
        }

        private static int _malformedCount;
        public static int MalformedCount => _malformedCount;

        public static INetMessage Create(MsgType type)
        {
            switch (type)
            {
                case MsgType.Hello: return new HelloMsg();
                case MsgType.HelloAck: return new HelloAckMsg();
                case MsgType.Reject: return new RejectMsg();
                case MsgType.Disconnect: return new DisconnectMsg();
                case MsgType.TimeSync: return new TimeSyncMsg();
                case MsgType.Roster: return new RosterMsg();
                case MsgType.WorldBegin: return new WorldBeginMsg();
                case MsgType.WorldChunk: return new WorldChunkMsg();
                case MsgType.WorldEnd: return new WorldEndMsg();
                case MsgType.ClientWorldLoaded: return new ClientWorldLoadedMsg();
                case MsgType.PlayerState: return new PlayerStateMsg();
                case MsgType.BoatState: return new BoatStateMsg();
                case MsgType.EnvState: return new EnvStateMsg();
                case MsgType.ControlRequest: return new ControlRequestMsg();
                case MsgType.ControlState: return new ControlStateMsg();
                case MsgType.ItemManifest: return new ItemManifestMsg();
                case MsgType.ItemAction: return new ItemActionMsg();
                case MsgType.ItemState: return new ItemStateMsg();
                case MsgType.ItemMotion: return new ItemMotionMsg();
                case MsgType.EconomyState: return new EconomyStateMsg();
                case MsgType.EconomyDelta: return new EconomyDeltaMsg();
                case MsgType.TaskState: return new TaskStateMsg();
                case MsgType.JournalLine: return new JournalLineMsg();
                case MsgType.CargoOps: return new CargoOpsMsg();
                case MsgType.CargoState: return new CargoStateMsg();
                case MsgType.CargoLease: return new CargoLeaseMsg();
                case MsgType.AnimalState: return new AnimalStateMsg();
                case MsgType.FishRecord: return new FishRecordMsg();
                case MsgType.RowboatState: return new RowboatStateMsg();
                case MsgType.CrewOrder: return new CrewOrderMsg();
                case MsgType.CrewState: return new CrewStateMsg();
                case MsgType.CrewRoster: return new CrewRosterMsg();
                case MsgType.ModData: return new ModDataMsg();
                case MsgType.FastTravel: return new FastTravelMsg();
                // New message bodies are registered here as they land.
                default: return null;
            }
        }
    }

    /// <summary>Convenience send helpers over a LiteNetLib peer.</summary>
    public static class PeerExt
    {
        public static void Send(this NetPeer peer, INetMessage msg, DeliveryMethod method)
        {
            var writer = Protocol.Write(msg);
            // LiteNetLib throws on an Unreliable payload above the peer MTU and only fragments
            // reliable channels. A snapshot that outgrew one datagram is still delivered.
            if (method == DeliveryMethod.Unreliable && writer.Length > peer.GetMaxSinglePacketSize(method))
                method = DeliveryMethod.ReliableOrdered;
            peer.Send(writer, method);
        }
    }
}
