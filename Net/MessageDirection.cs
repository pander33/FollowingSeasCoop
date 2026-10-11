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
                case MsgType.ItemMotion:
                case MsgType.EconomyDelta:
                case MsgType.TaskState:
                case MsgType.JournalLine:
                case MsgType.CargoOps:
                case MsgType.CargoLease:
                case MsgType.AnimalState:
                case MsgType.FishRecord:
                case MsgType.PlayerState:
                case MsgType.RowboatState:
                case MsgType.CrewOrder:
                case MsgType.CrewRoster:
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
                case MsgType.ItemMotion:
                case MsgType.EconomyState:
                case MsgType.CargoOps:
                case MsgType.CargoState:
                case MsgType.CargoLease:
                case MsgType.AnimalState:
                case MsgType.FishRecord:
                case MsgType.TaskState:
                case MsgType.JournalLine:
                case MsgType.PlayerState:
                case MsgType.RowboatState:
                case MsgType.CrewState:
                case MsgType.CrewRoster:
                    return true;
                default:
                    return false;
            }
        }
    }
}
