using UnityEngine;
using twoloop;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// The game uses a floating origin (<c>twoloop.OriginShift</c>): when the player is far from the
    /// scene origin, every root object of the world scene is moved back and the distance is added to
    /// <c>OriginShift.LocalOffset</c>. The same spot therefore has a different
    /// <c>transform.position</c> on each machine.
    ///
    /// "Real" space is the position the scene was authored in: <c>local + LocalOffset</c>. It is the
    /// same on every machine and is what travels on the wire for anything not standing on the ship.
    /// The origin only translates, never rotates — rotations need no conversion.
    /// </summary>
    internal static class CoordSpace
    {
        /// <summary>The current origin offset as doubles, whatever precision mode the scene uses.</summary>
        public static void Offset(out double x, out double y, out double z)
        {
            x = y = z = 0.0;
            // The getter itself logs when there is no OriginShift, so ask first.
            if (OriginShift.singleton == null) return;
            OriginShift.Offset offset = OriginShift.LocalOffset;
            // Only the fields of the active precision mode are non-zero.
            x = offset.vector.x + offset.xDouble + (double)offset.xDecimal;
            y = offset.vector.y + offset.yDouble + (double)offset.yDecimal;
            z = offset.vector.z + offset.zDouble + (double)offset.zDecimal;
        }

        public static void LocalToReal(Vector3 local, out double x, out double y, out double z)
        {
            Offset(out double ox, out double oy, out double oz);
            x = local.x + ox;
            y = local.y + oy;
            z = local.z + oz;
        }

        public static Vector3 RealToLocal(double x, double y, double z)
        {
            Offset(out double ox, out double oy, out double oz);
            return new Vector3((float)(x - ox), (float)(y - oy), (float)(z - oz));
        }
    }
}
