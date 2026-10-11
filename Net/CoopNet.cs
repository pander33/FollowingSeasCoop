using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    public enum Role { None, Host, Client }

    public enum LinkState { Idle, Connecting, Handshaking, Connected, Rejected, Failed }

    /// <summary>Per-peer session info the host keeps for each connected client.</summary>
    public sealed class PeerSession
    {
        public NetPeer Peer;
        public bool HandshakeDone;
        public uint PlayerNetId;
        public string PlayerName = "";
        public MemberJoinState JoinState = MemberJoinState.Handshaking;
    }

    public sealed class SessionMemberInfo
    {
        public uint NetId;
        public string Name = "";
        public bool IsHost;
        public MemberJoinState State;
        public int PingMs = -1;
    }

    /// <summary>
    /// Transport + session layer (F1/F5). Wraps LiteNetLib, drives the Hello handshake and
    /// TimeSync, keeps the roster, and routes decoded gameplay messages to a callback.
    /// Pure transport/session here — no game-object access (that lives in Sync/).
    ///
    /// All public methods are expected to be called from the Unity main thread;
    /// <see cref="PollEvents"/> must run every frame (F5).
    /// </summary>
    public sealed class CoopNet
    {
        // The connection key gates wrong builds out at the LiteNetLib layer too.
        private static string ConnKey => "FollowingSeasCoop:" + Protocol.Version;

        private readonly Action<string> _log;
        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private NetManager _net;
        // A session that was ended with a reason: kept alive a moment so the reason arrives.
        private NetManager _closing;
        private DateTime _closingUntil;
        private const double LingerSeconds = 1.5;

        public Role Role { get; private set; } = Role.None;
        public LinkState State { get; private set; } = LinkState.Idle;
        public string LastError { get; private set; } = "";
        public string LastDisconnectReason { get; private set; } = "";
        public string HostLabel { get; private set; } = "";

        /// <summary>This machine's own player NetId (host = 1, client = assigned by the host).</summary>
        public uint MyNetId { get; private set; } = NetRegistry.HostPlayerNetId;

        public readonly NetClock Clock = new NetClock();
        public readonly NetRegistry Registry = new NetRegistry();

        // Host: connected client sessions, keyed by peer id. Client: single host peer.
        private readonly Dictionary<int, PeerSession> _sessions = new Dictionary<int, PeerSession>();
        private NetPeer _hostPeer;
        private long _lastTimeSyncTick;
        private SessionMemberInfo[] _roster = new SessionMemberInfo[0];
        private int _rosterRevision;

        // Identity supplied by the runtime layer.
        public string ModVersion = "0.0.0";
        public string GameVersion = "";
        public string PlayerName = "Player";

        /// <summary>Host: null when a client may join now, otherwise the reason it may not.</summary>
        public Func<string> JoinBlocker = () => null;

        // Tuning, supplied by the runtime layer from config.
        public int MaxClients = 3;
        public int DisconnectTimeoutMs = 5000;
        public int UpdateTimeMs = 15;
        public int ConnectAttempts = 10;
        public int ReconnectDelayMs = 500;

        /// <summary>Raised for every message that is not session bookkeeping. (type, message, sender)</summary>
        public event Action<MsgType, INetMessage, NetPeer> OnGameMessage;
        /// <summary>Host: a client finished the handshake.</summary>
        public event Action<PeerSession> OnClientReady;
        /// <summary>Client: its own handshake was accepted.</summary>
        public event Action<HelloAckMsg> OnAccepted;
        /// <summary>Host: a client that had finished the handshake left.</summary>
        public event Action<uint> OnPlayerLeft;
        /// <summary>Client: the link to the host ended, for any reason.</summary>
        public event Action<string> OnLinkLost;
        public event Action OnRosterChanged;

        /// <summary>
        /// Diagnostics: every gameplay message that leaves or arrives. (sent, type, message, the
        /// other player's NetId — 0 for a broadcast.) Must not throw.
        /// </summary>
        public Action<bool, MsgType, INetMessage, uint> Tap;

        /// <summary>
        /// Development: hold every arriving message this long before it is acted on, plus up to
        /// <see cref="JitterMs"/> more. Two copies on one machine have no delay of their own, so
        /// mistakes of ordering never show without it. The order of messages is kept.
        /// </summary>
        public int LagMs;
        public int JitterMs;

        private struct Held
        {
            public long Due;
            public NetPeer Peer;
            public MsgType Type;
            public INetMessage Msg;
        }

        private readonly Queue<Held> _held = new Queue<Held>();
        private long _lastDue;
        private readonly Random _jitter = new Random();
        public int HeldCount => _held.Count;

        public CoopNet(Action<string> log)
        {
            _log = log ?? (_ => { });
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
            _listener.NetworkErrorEvent += (endPoint, error) => _log("[CoopNet] Socket error " + error + " at " + endPoint);
        }

        public int PeerCount => _sessions.Count;
        public double RttMs => Clock.RttMs;
        public bool IsHost => Role == Role.Host;
        public bool IsClient => Role == Role.Client;
        public bool Closing => _closing != null;
        public SessionMemberInfo[] RosterSnapshot => (SessionMemberInfo[])_roster.Clone();

        public string GetPlayerName(uint netId)
        {
            foreach (var member in _roster)
                if (member.NetId == netId && !string.IsNullOrEmpty(member.Name)) return member.Name;
            return netId == MyNetId && !string.IsNullOrEmpty(PlayerName) ? PlayerName : "Player " + netId;
        }

        public bool HasMember(uint netId)
        {
            foreach (var member in _roster)
                if (member.NetId == netId) return true;
            return false;
        }

        public uint PlayerNetIdForPeer(NetPeer peer)
        {
            if (peer != null && _sessions.TryGetValue(peer.Id, out var session) && session.HandshakeDone)
                return session.PlayerNetId;
            return 0;
        }

        public PeerSession SessionOf(uint netId)
        {
            foreach (var session in _sessions.Values)
                if (session.HandshakeDone && session.PlayerNetId == netId) return session;
            return null;
        }

        public void SetMemberState(uint netId, MemberJoinState state)
        {
            if (Role != Role.Host) return;
            var session = SessionOf(netId);
            if (session == null || session.JoinState == state) return;
            session.JoinState = state;
            BroadcastRoster();
        }

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        public void StartHost(int port)
        {
            Stop();
            _net = NewManager();
            if (!_net.Start(port))
            {
                _net = null;
                State = LinkState.Failed;
                LastError = "Failed to open UDP port " + port;
                _log("[CoopNet] " + LastError);
                return;
            }
            Role = Role.Host;
            State = LinkState.Connected;   // the host is "up" immediately; clients join later
            LastError = "";
            LastDisconnectReason = "";
            MyNetId = NetRegistry.HostPlayerNetId;
            Registry.Register(NetRegistry.HostPlayerNetId, NetObjKind.Player, NetRegistry.HostPlayerNetId);
            BroadcastRoster();
            _log("[CoopNet] Host listening on port " + port + " (protocol " + Protocol.Version + ")");
        }

        public void StartClient(string ip, int port)
        {
            Stop();
            HostLabel = ip ?? "";
            _net = NewManager();
            if (!_net.Start())
            {
                _net = null;
                State = LinkState.Failed;
                LastError = "Failed to start the network manager";
                _log("[CoopNet] " + LastError);
                return;
            }
            Role = Role.Client;
            State = LinkState.Connecting;
            LastError = "";
            LastDisconnectReason = "";
            _log("[CoopNet] Connecting to " + ip + ":" + port + " ...");
            try { _net.Connect(ip, port, ConnKey); }   // Hello is sent once the peer connects
            catch (Exception e)
            {
                Stop();
                State = LinkState.Failed;
                LastError = "Bad address '" + ip + "': " + e.Message;
                _log("[CoopNet] " + LastError);
            }
        }

        /// <summary>Ends the session. The outcome fields (<see cref="LastError"/> and so on) are kept.</summary>
        public void Stop(string reason = null)
        {
            FinishClosing();
            if (_net != null)
            {
                bool linger = false;
                if (!string.IsNullOrEmpty(reason) && _net.ConnectedPeersCount > 0)
                {
                    try
                    {
                        var bye = Protocol.Write(new DisconnectMsg { Reason = reason });
                        _net.DisconnectAll(bye.Data, 0, bye.Length);
                        linger = true;
                    }
                    catch { }
                }
                // The reason travels in the disconnect packet, which LiteNetLib repeats until the
                // other side confirms it — but only while the manager runs. Stopping it at once
                // sends the packet a single time, and a lost one leaves the other side with no reason.
                if (linger)
                {
                    _closing = _net;
                    _closingUntil = DateTime.UtcNow.AddSeconds(LingerSeconds);
                }
                else _net.Stop();
                _net = null;
            }
            _sessions.Clear();
            _held.Clear();
            _lastDue = 0;
            _hostPeer = null;
            _roster = new SessionMemberInfo[0];
            _rosterRevision = 0;
            MyNetId = NetRegistry.HostPlayerNetId;
            _lastTimeSyncTick = 0;
            Registry.Clear();
            Clock.Reset();
            Role = Role.None;
            State = LinkState.Idle;
            OnRosterChanged?.Invoke();
        }

        /// <summary>Stops a session that was only waiting for its goodbye to arrive.</summary>
        public void FinishClosing()
        {
            if (_closing == null) return;
            try { _closing.Stop(); } catch { }
            _closing = null;
        }

        private NetManager NewManager()
        {
            return new NetManager(_listener)
            {
                AutoRecycle = true,
                UpdateTime = Math.Max(1, UpdateTimeMs),
                DisconnectTimeout = Math.Max(1000, DisconnectTimeoutMs),
                MaxConnectAttempts = Math.Max(1, ConnectAttempts),
                ReconnectDelay = Math.Max(100, ReconnectDelayMs),
                UnconnectedMessagesEnabled = false,
                IPv6Enabled = false,
            };
        }

        // -----------------------------------------------------------------
        // Per-frame pump (F5: main thread only)
        // -----------------------------------------------------------------

        public void PollEvents()
        {
            if (_closing != null)
            {
                // Its events arrive with no role set and are ignored by the handlers below.
                try { _closing.PollEvents(); } catch { }
                if (_closing != null && DateTime.UtcNow >= _closingUntil) FinishClosing();
            }
            if (_net == null) return;
            _net.PollEvents();
            if (_net == null) return;   // a callback ended the session
            while (_held.Count > 0 && _held.Peek().Due <= Clock.LocalTick)
            {
                Held held = _held.Dequeue();
                Dispatch(held.Peer, held.Type, held.Msg);
                if (_net == null) return;
            }

            // The client drives TimeSync at ~3 Hz once connected.
            if (Role == Role.Client && _hostPeer != null && State == LinkState.Connected)
            {
                long now = Clock.LocalTick;
                if (now - _lastTimeSyncTick >= 333)
                {
                    _lastTimeSyncTick = now;
                    _hostPeer.Send(new TimeSyncMsg { IsReply = false, ClientSendTick = now }, DeliveryMethod.Unreliable);
                }
            }
        }

        // -----------------------------------------------------------------
        // Sending
        // -----------------------------------------------------------------

        /// <summary>Client: send to the host. No-op unless the handshake is done.</summary>
        public void SendToHost(INetMessage msg, DeliveryMethod method)
        {
            if (Role == Role.Client && _hostPeer != null && State == LinkState.Connected)
            {
                _hostPeer.Send(msg, method);
                Tap?.Invoke(true, msg.Type, msg, NetRegistry.HostPlayerNetId);
            }
        }

        /// <summary>Host: send to one player.</summary>
        public void SendTo(uint netId, INetMessage msg, DeliveryMethod method)
        {
            if (Role != Role.Host) return;
            var session = SessionOf(netId);
            if (session == null || session.Peer == null) return;
            session.Peer.Send(msg, method);
            Tap?.Invoke(true, msg.Type, msg, netId);
        }

        /// <summary>Host: send to every client that finished the handshake.</summary>
        /// <param name="readyOnly">Only clients that are in the world.</param>
        /// <param name="exceptNetId">A player to skip (the one the message came from).</param>
        public void Broadcast(INetMessage msg, DeliveryMethod method, bool readyOnly = true, uint exceptNetId = 0)
        {
            if (Role != Role.Host) return;
            bool any = false;
            foreach (var session in _sessions.Values)
            {
                if (!session.HandshakeDone || session.Peer == null) continue;
                if (readyOnly && session.JoinState != MemberJoinState.Ready) continue;
                if (exceptNetId != 0 && session.PlayerNetId == exceptNetId) continue;
                session.Peer.Send(msg, method);
                any = true;
            }
            if (any) Tap?.Invoke(true, msg.Type, msg, 0u);
        }

        // -----------------------------------------------------------------
        // LiteNetLib callbacks
        // -----------------------------------------------------------------

        private void OnConnectionRequest(ConnectionRequest request)
        {
            if (Role != Role.Host) { request.Reject(); return; }
            if (_sessions.Count >= Math.Max(1, MaxClients))
            {
                request.Reject(Protocol.Write(new RejectMsg { Reason = RejectReason.SessionFull, Detail = "the session is full" }));
                return;
            }
            request.AcceptIfKey(ConnKey);
        }

        private void OnPeerConnected(NetPeer peer)
        {
            if (Role == Role.Client)
            {
                _hostPeer = peer;
                State = LinkState.Handshaking;
                _log("[CoopNet] Connected, sending Hello");
                peer.Send(new HelloMsg
                {
                    ProtocolVersion = Protocol.Version,
                    ModVersion = ModVersion,
                    GameVersion = GameVersion,
                    PlayerName = PlayerName,
                }, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                _sessions[peer.Id] = new PeerSession { Peer = peer };
                _log("[CoopNet] Client connected (peer " + peer.Id + "), waiting for Hello");
            }
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (Role == Role.Host)
            {
                if (!_sessions.TryGetValue(peer.Id, out var session)) return;
                _sessions.Remove(peer.Id);
                if (!session.HandshakeDone) return;
                Registry.Remove(session.PlayerNetId);
                _log("[CoopNet] " + session.PlayerName + " (NetId " + session.PlayerNetId + ") left: " + info.Reason);
                BroadcastRoster();
                OnPlayerLeft?.Invoke(session.PlayerNetId);
                return;
            }
            if (Role != Role.Client) return;

            // The host explains an orderly end in the disconnect packet itself.
            LinkState end = State == LinkState.Connected ? LinkState.Idle : LinkState.Failed;
            string reason = Describe(info.Reason);
            if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
            {
                INetMessage carried = Protocol.Read(info.AdditionalData, out _);
                if (carried is RejectMsg reject)
                {
                    end = LinkState.Rejected;
                    reason = "Rejected: " + (string.IsNullOrEmpty(reject.Detail) ? reject.Reason.ToString() : reject.Detail);
                }
                else if (carried is DisconnectMsg bye && !string.IsNullOrEmpty(bye.Reason))
                {
                    reason = bye.Reason;
                }
            }
            else if (info.Reason == DisconnectReason.ConnectionRejected)
            {
                end = LinkState.Rejected;
                reason = "Rejected by the host (a different mod protocol?)";
            }
            _log("[CoopNet] Link to the host ended: " + reason);
            Stop();
            State = end;
            LastDisconnectReason = reason;
            if (end != LinkState.Idle) LastError = reason;
            OnLinkLost?.Invoke(reason);
        }

        private static string Describe(DisconnectReason reason)
        {
            switch (reason)
            {
                case DisconnectReason.ConnectionFailed: return "The host did not answer";
                case DisconnectReason.Timeout: return "The connection timed out";
                case DisconnectReason.RemoteConnectionClose: return "The host closed the session";
                case DisconnectReason.DisconnectPeerCalled: return "Disconnected";
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable: return "The host is unreachable";
                default: return reason.ToString();
            }
        }

        private int _wrongSide;

        // A message this side may not receive is a mistake in the mod, not in the session: without
        // a line in the log the feature it belongs to would only look dead.
        private void WrongSide(MsgType type)
        {
            _wrongSide++;
            if (_wrongSide <= 5 || _wrongSide % 500 == 0)
                Protocol.Warn("[CoopNet] " + type + " dropped: this side does not take it (occurrence #" + _wrongSide + ")");
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            INetMessage msg = Protocol.Read(reader, out MsgType type);
            if (msg == null) return;

            if (LagMs <= 0 && JitterMs <= 0 && _held.Count == 0) { Dispatch(peer, type, msg); return; }
            // Never before the message that arrived ahead of it: reliable channels promise an order.
            long due = Clock.LocalTick + Math.Max(0, LagMs) + (JitterMs > 0 ? _jitter.Next(JitterMs + 1) : 0);
            if (due < _lastDue) due = _lastDue;
            _lastDue = due;
            _held.Enqueue(new Held { Due = due, Peer = peer, Type = type, Msg = msg });
        }

        private void Dispatch(NetPeer peer, MsgType type, INetMessage msg)
        {
            if (Role == Role.Host)
            {
                if (!MessageDirection.FromClient(type)) { WrongSide(type); return; }
                if (!_sessions.TryGetValue(peer.Id, out var session)) return;
                if (type == MsgType.Hello) { HandleHello(session, (HelloMsg)msg); return; }
                if (!session.HandshakeDone) return;   // nothing else is accepted before the handshake
                if (type == MsgType.TimeSync)
                {
                    var ping = (TimeSyncMsg)msg;
                    if (!ping.IsReply)
                        peer.Send(new TimeSyncMsg { IsReply = true, ClientSendTick = ping.ClientSendTick, ServerTick = Clock.LocalTick },
                                  DeliveryMethod.Unreliable);
                    return;
                }
                Tap?.Invoke(false, type, msg, session.PlayerNetId);
                OnGameMessage?.Invoke(type, msg, peer);
                return;
            }

            if (Role != Role.Client || peer != _hostPeer) return;
            if (!MessageDirection.FromHost(type)) { WrongSide(type); return; }
            switch (type)
            {
                case MsgType.HelloAck:
                {
                    var ack = (HelloAckMsg)msg;
                    MyNetId = ack.YourNetId;
                    State = LinkState.Connected;
                    // A first clock estimate before the first ping completes.
                    Clock.OnReply(Clock.LocalTick, ack.HostTick);
                    _log("[CoopNet] Accepted by " + ack.HostName + " as NetId " + ack.YourNetId +
                         " (host mod " + ack.ModVersion + ", game " + ack.GameVersion + ")");
                    if (!string.IsNullOrEmpty(ack.GameVersion) && ack.GameVersion != GameVersion)
                        _log("[CoopNet] WARNING: the host runs game version " + ack.GameVersion + ", this copy is " + GameVersion);
                    OnAccepted?.Invoke(ack);
                    return;
                }
                case MsgType.TimeSync:
                {
                    var reply = (TimeSyncMsg)msg;
                    if (reply.IsReply) Clock.OnReply(reply.ClientSendTick, reply.ServerTick);
                    return;
                }
                case MsgType.Roster:
                    ApplyRoster((RosterMsg)msg);
                    return;
            }
            if (State != LinkState.Connected) return;
            Tap?.Invoke(false, type, msg, NetRegistry.HostPlayerNetId);
            OnGameMessage?.Invoke(type, msg, peer);
        }

        // -----------------------------------------------------------------
        // Handshake and roster (host)
        // -----------------------------------------------------------------

        private void HandleHello(PeerSession session, HelloMsg hello)
        {
            if (session.HandshakeDone) return;
            if (hello.ProtocolVersion != Protocol.Version)
            {
                Refuse(session, RejectReason.ProtocolMismatch,
                       "the host uses mod protocol " + Protocol.Version + ", you have " + hello.ProtocolVersion);
                return;
            }
            string blocker = null;
            try { blocker = JoinBlocker(); } catch (Exception e) { blocker = "host error: " + e.Message; }
            if (!string.IsNullOrEmpty(blocker))
            {
                Refuse(session, RejectReason.HostNotInWorld, blocker);
                return;
            }

            session.PlayerNetId = Registry.AllocateId();
            session.PlayerName = string.IsNullOrEmpty(hello.PlayerName) ? "Player " + session.PlayerNetId : hello.PlayerName;
            session.HandshakeDone = true;
            session.JoinState = MemberJoinState.Loading;
            Registry.Register(session.PlayerNetId, NetObjKind.Player, session.PlayerNetId);
            _log("[CoopNet] " + session.PlayerName + " joined as NetId " + session.PlayerNetId +
                 " (mod " + hello.ModVersion + ", game " + hello.GameVersion + ")");
            if (!string.IsNullOrEmpty(hello.GameVersion) && hello.GameVersion != GameVersion)
                _log("[CoopNet] WARNING: " + session.PlayerName + " runs game version " + hello.GameVersion + ", the host is " + GameVersion);

            session.Peer.Send(new HelloAckMsg
            {
                YourNetId = session.PlayerNetId,
                HostTick = Clock.LocalTick,
                HostName = PlayerName,
                ModVersion = ModVersion,
                GameVersion = GameVersion,
            }, DeliveryMethod.ReliableOrdered);
            BroadcastRoster();
            OnClientReady?.Invoke(session);
        }

        private void Refuse(PeerSession session, RejectReason reason, string detail)
        {
            _log("[CoopNet] Refused peer " + session.Peer.Id + ": " + detail);
            _sessions.Remove(session.Peer.Id);
            session.Peer.Disconnect(Protocol.Write(new RejectMsg { Reason = reason, Detail = detail }));
        }

        /// <summary>Host: remove a player from the session.</summary>
        public bool DisconnectPlayer(uint netId, string reason)
        {
            if (Role != Role.Host || netId == NetRegistry.HostPlayerNetId) return false;
            var session = SessionOf(netId);
            if (session == null || session.Peer == null) return false;
            session.Peer.Disconnect(Protocol.Write(new DisconnectMsg { Reason = string.IsNullOrEmpty(reason) ? "Removed by the host" : reason }));
            return true;
        }

        private void BroadcastRoster()
        {
            if (Role != Role.Host) return;
            var members = new List<SessionMemberInfo>
            {
                new SessionMemberInfo { NetId = NetRegistry.HostPlayerNetId, Name = PlayerName, IsHost = true, State = MemberJoinState.Ready },
            };
            foreach (var session in _sessions.Values)
            {
                if (!session.HandshakeDone) continue;
                members.Add(new SessionMemberInfo
                {
                    NetId = session.PlayerNetId,
                    Name = session.PlayerName,
                    State = session.JoinState,
                    PingMs = session.Peer != null ? session.Peer.Ping : -1,
                });
            }
            _roster = members.ToArray();

            var msg = new RosterMsg { Revision = ++_rosterRevision, Entries = new RosterEntry[_roster.Length] };
            for (int i = 0; i < _roster.Length; i++)
                msg.Entries[i] = new RosterEntry { NetId = _roster[i].NetId, Name = _roster[i].Name, State = _roster[i].State };
            Broadcast(msg, DeliveryMethod.ReliableOrdered, readyOnly: false);
            OnRosterChanged?.Invoke();
        }

        private void ApplyRoster(RosterMsg msg)
        {
            if (msg.Revision <= _rosterRevision) return;
            _rosterRevision = msg.Revision;
            var members = new SessionMemberInfo[msg.Entries.Length];
            for (int i = 0; i < members.Length; i++)
            {
                members[i] = new SessionMemberInfo
                {
                    NetId = msg.Entries[i].NetId,
                    Name = msg.Entries[i].Name,
                    State = msg.Entries[i].State,
                    IsHost = msg.Entries[i].NetId == NetRegistry.HostPlayerNetId,
                };
            }
            _roster = members;
            OnRosterChanged?.Invoke();
        }
    }
}
