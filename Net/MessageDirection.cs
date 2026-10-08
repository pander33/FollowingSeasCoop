namespace FollowingSeasCoop.Net
{
    /// <summary>
    /// Which side may send which message. A packet that arrives from the wrong side is dropped.
    /// This is a format rule, not a gameplay one: the host does not judge what a client asks for.
    /// </summary>
    public static class MessageDirection
    {
        /// <summary>True when a client may send this type to the host.</summary>
        public static bool FromClient(MsgType type)
        {
            switch (type)
            {
                case MsgType.Hello:
                case MsgType.Disconnect:
                case MsgType.TimeSync:
                case MsgType.ClientWorldLoaded:
                case MsgType.ControlRequest:
                case MsgType.ItemAction:
                case MsgType.PlayerState:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>True when the host may send this type to a client.</summary>
        public static bool FromHost(MsgType type)
        {
            switch (type)
            {
                case MsgType.HelloAck:
                case MsgType.Reject:
                case MsgType.Disconnect:
                case MsgType.TimeSync:
                case MsgType.Roster:
                case MsgType.WorldBegin:
                case MsgType.WorldChunk:
                case MsgType.WorldEnd:
                case MsgType.BoatState:
                case MsgType.EnvState:
                case MsgType.ControlState:
                case MsgType.ItemManifest:
                case MsgType.ItemState:
                case MsgType.PlayerState:
                    return true;
                default:
                    return false;
            }
        }
    }
}
