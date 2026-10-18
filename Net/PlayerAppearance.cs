using LiteNetLib.Utils;

namespace FollowingSeasCoop.Net
{
    /// <summary>
    /// A player's look: a crew model and, for each of its parts, which model of the same family the
    /// part takes its materials from. The numbers mean something only to the runtime's catalog, which
    /// reads anything unknown as the model's own look.
    /// </summary>
    public struct PlayerAppearance
    {
        public byte Model;
        public byte[] Parts;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Model);
            int count = Parts == null ? 0 : System.Math.Min(Parts.Length, Limits.AppearanceParts);
            writer.Put((byte)count);
            for (int i = 0; i < count; i++) writer.Put(Parts[i]);
        }

        public void Deserialize(NetDataReader reader)
        {
            Model = reader.GetByte();
            int count = reader.GetByte();
            if (count > Limits.AppearanceParts) throw new System.IO.InvalidDataException("Appearance parts " + count);
            Parts = count == 0 ? null : new byte[count];
            for (int i = 0; i < count; i++) Parts[i] = reader.GetByte();
        }

        public bool Same(PlayerAppearance other)
        {
            if (Model != other.Model) return false;
            int length = Parts == null ? 0 : Parts.Length, otherLength = other.Parts == null ? 0 : other.Parts.Length;
            if (length != otherLength) return false;
            for (int i = 0; i < length; i++)
                if (Parts[i] != other.Parts[i]) return false;
            return true;
        }
    }
}
