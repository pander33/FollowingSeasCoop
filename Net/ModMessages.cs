using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    /// <summary>
    /// A message of another mod, carried for it (<c>Runtime/ModChannel</c>). The co-op mod reads the
    /// channel name only, to find who it is for; the text is the other mod's own.
    /// </summary>
    public sealed class ModDataMsg : INetMessage
    {
        public const int MaxChannel = 64;
        public const int MaxText = 4000;

        public MsgType Type => MsgType.ModData;
        public string Channel = "";
        public string Text = "";

        public void Serialize(NetDataWriter w)
        {
            w.Put(Channel ?? "", MaxChannel);
            w.Put(Text ?? "", MaxText);
        }

        public void Deserialize(NetDataReader r)
        {
            Channel = r.GetString(MaxChannel);
            Text = r.GetString(MaxText);
        }
    }
}
