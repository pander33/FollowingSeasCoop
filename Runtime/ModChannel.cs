using System;
using System.Collections.Generic;
using FollowingSeasCoop.Net;
using LiteNetLib;

namespace FollowingSeasCoop.Runtime
{
    /// <summary>
    /// The door for other mods: a line of text carried between the copies of a session under a
    /// channel name (<see cref="ModDataMsg"/>). The co-op mod does not read the text. Another mod
    /// reaches this class by reflection, so it needs no reference to this one and still loads
    /// without it:
    /// <code>
    /// Type t = coopAssembly.GetType("FollowingSeasCoop.Runtime.ModChannel");
    /// t.GetMethod("Register").Invoke(null, new object[] { "my.mod", (Action&lt;string&gt;)OnText });
    /// t.GetMethod("Send").Invoke(null, new object[] { "my.mod", "text" });
    /// int role = (int)t.GetProperty("Role").GetValue(null);
    /// </code>
    /// Everything here runs on Unity's main thread: <see cref="Send"/> is called from a mod's
    /// <c>Update</c>, and a handler is called from the co-op mod's.
    /// </summary>
    public static class ModChannel
    {
        /// <summary>Raised when this class changes in a way a caller has to know of.</summary>
        public const int Api = 2;

        private static readonly Dictionary<string, Action<string>> Handlers = new Dictionary<string, Action<string>>();
        private static readonly Dictionary<string, Action<uint, string>> PeerHandlers = new Dictionary<string, Action<uint, string>>();

        /// <summary>A new object for each session, null outside one: compare it by reference.</summary>
        public static object SessionIdentity => CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Net?.Session : null;

        /// <summary>The host's number for a fish on a trolling hook; travels with the hook's state.</summary>
        public static Func<UnityEngine.GameObject, int> CatchUnits { get; set; }

        /// <summary>A guest is given the host's number for a fish on a trolling hook.</summary>
        public static Action<UnityEngine.GameObject, int> ReceiveCatchUnits { get; set; }

        /// <summary>NetIds of everyone in the session, in ascending order. Guests still loading the
        /// world are listed but are not sent anything by <see cref="Send"/> until they are in it.</summary>
        public static uint[] Members
        {
            get
            {
                var net = CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Net : null;
                if (net == null) return new uint[0];
                var roster = net.RosterSnapshot;
                var ids = new uint[roster.Length];
                for (int i = 0; i < ids.Length; i++) ids[i] = roster[i].NetId;
                Array.Sort(ids);
                return ids;
            }
        }

        /// <summary>As <see cref="Register"/>, and the handler is also given the sender's NetId (on a
        /// guest always the host's).</summary>
        public static void RegisterPeer(string channel, Action<uint, string> onReceive)
        {
            if (string.IsNullOrEmpty(channel)) return;
            if (onReceive == null) PeerHandlers.Remove(channel);
            else PeerHandlers[channel] = onReceive;
        }

        internal static void DeliverPeer(ModDataMsg msg, NetPeer from)
        {
            var net = CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Net : null;
            if (net == null || !PeerHandlers.TryGetValue(msg.Channel, out var handler)) return;
            uint sender = net.IsHost ? net.PlayerNetIdForPeer(from) : 1u;
            if (sender == 0) return;
            try { handler(sender, msg.Text); }
            catch (Exception e) { Plugin.Log.LogWarning("[ModChannel] Peer handler failed: " + e); }
        }

        /// <summary><see cref="CatchUnits"/> as text, "" without it or when it fails.</summary>
        internal static string CatchUnitsText(UnityEngine.GameObject fish)
        {
            var read = CatchUnits;
            if (read == null) return "";
            try { return read(fish).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception e) { Plugin.Log.LogWarning("[ModChannel] CatchUnits failed: " + e); return ""; }
        }

        internal static void GiveCatchUnits(UnityEngine.GameObject fish, string text)
        {
            var give = ReceiveCatchUnits;
            if (give == null || !int.TryParse(text, out int units)) return;
            try { give(fish, units); }
            catch (Exception e) { Plugin.Log.LogWarning("[ModChannel] ReceiveCatchUnits failed: " + e); }
        }

        /// <summary>0 outside a session, 1 the host, 2 a guest (<see cref="Net.Role"/>).</summary>
        public static int Role
        {
            get
            {
                CoopNet net = CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Net : null;
                return net != null ? (int)net.Role : 0;
            }
        }

        /// <summary>Where text that arrives on <paramref name="channel"/> goes. A second call replaces
        /// the first; null removes it.</summary>
        public static void Register(string channel, Action<string> onReceive)
        {
            if (string.IsNullOrEmpty(channel)) return;
            if (onReceive == null) Handlers.Remove(channel);
            else Handlers[channel] = onReceive;
        }

        /// <summary>
        /// The host sends to every guest who is in the world, a guest to the host; reliable and in
        /// order. False outside a session, or when the channel name or the text is too long.
        /// </summary>
        public static bool Send(string channel, string text)
        {
            CoopNet net = CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Net : null;
            if (net == null || string.IsNullOrEmpty(channel)) return false;
            if (channel.Length > ModDataMsg.MaxChannel || (text ?? "").Length > ModDataMsg.MaxText)
            {
                Plugin.Log.LogWarning("[ModChannel] Not sent on '" + channel + "': too long");
                return false;
            }
            var msg = new ModDataMsg { Channel = channel, Text = text ?? "" };
            if (net.IsHost) net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
            else if (net.IsClient) net.SendToHost(msg, DeliveryMethod.ReliableOrdered);
            else return false;
            return true;
        }

        /// <summary>A message for a channel nobody here listens on is dropped: the other copy may have
        /// a mod this one lacks.</summary>
        internal static void Deliver(ModDataMsg msg)
        {
            if (!Handlers.TryGetValue(msg.Channel, out var handler)) return;
            try { handler(msg.Text); }
            catch (Exception e) { Plugin.Log.LogWarning("[ModChannel] The handler of '" + msg.Channel + "' failed: " + e); }
        }
    }
}
