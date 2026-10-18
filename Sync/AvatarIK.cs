using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// Bends an avatar's limbs after its animator has posed them. The crew controller's layers have
    /// no IK pass, so the bones are turned by hand in <c>LateUpdate</c>; the animator poses every
    /// bone anew each frame, so nothing adds up.
    /// </summary>
    internal static class AvatarIK
    {
        /// <summary>
        /// Turns <paramref name="root"/> and <paramref name="mid"/> so that <paramref name="tip"/>
        /// reaches <paramref name="target"/>, the middle joint towards <paramref name="hint"/>. The middle
        /// joint bends about its own hinge, so no bone is twisted. A foot keeps its rotation in the
        /// world (<paramref name="keepTip"/>), flat on the deck; a hand goes on in line with its
        /// forearm, as the animation had it. The two-bone solve of Unity's Animation Rigging.
        /// </summary>
        public static void Reach(Transform root, Transform mid, Transform tip, Vector3 target, Vector3 hint, bool keepTip)
        {
            Quaternion tipRotation = tip.rotation;
            Vector3 a = root.position, b = mid.position, c = tip.position;
            Vector3 ab = b - a, bc = c - b, ac = c - a, at = target - a;
            float abLength = ab.magnitude, bcLength = bc.magnitude;
            if (abLength < 0.001f || bcLength < 0.001f || at.sqrMagnitude < 0.000001f) return;

            float oldAngle = Angle(ac.magnitude, abLength, bcLength);
            float newAngle = Angle(at.magnitude, abLength, bcLength);
            Vector3 axis = Vector3.Cross(ab, bc);
            if (axis.sqrMagnitude < 0.000001f) axis = Vector3.Cross(hint - a, bc);
            if (axis.sqrMagnitude < 0.000001f) return;
            axis.Normalize();
            float half = 0.5f * (oldAngle - newAngle);
            float sin = Mathf.Sin(half);
            mid.rotation = new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, Mathf.Cos(half)) * mid.rotation;

            root.rotation = Quaternion.FromToRotation(tip.position - a, at) * root.rotation;

            ac = tip.position - a;
            if (ac.sqrMagnitude > 0.000001f)
            {
                Vector3 acNormal = ac.normalized;
                Vector3 abProjected = Vector3.ProjectOnPlane(mid.position - a, acNormal);
                Vector3 hintProjected = Vector3.ProjectOnPlane(hint - a, acNormal);
                float reach = abLength + bcLength;
                if (abProjected.sqrMagnitude > reach * reach * 0.001f && hintProjected.sqrMagnitude > 0f)
                    root.rotation = Quaternion.FromToRotation(abProjected, hintProjected) * root.rotation;
            }
            if (keepTip) tip.rotation = tipRotation;
        }

        /// <summary>The box around what is drawn of an object; false when nothing of it is drawn.</summary>
        public static bool Bounds(GameObject obj, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer renderer in obj.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                if (any) bounds.Encapsulate(renderer.bounds);
                else { bounds = renderer.bounds; any = true; }
            }
            return any;
        }

        /// <summary>How far a box reaches from its centre along a direction.</summary>
        public static float Reach(Bounds bounds, Vector3 direction)
        {
            Vector3 e = bounds.extents;
            return Mathf.Abs(e.x * direction.x) + Mathf.Abs(e.y * direction.y) + Mathf.Abs(e.z * direction.z);
        }

        // The angle at the joint between sides b and c of a triangle whose third side is a.
        private static float Angle(float a, float b, float c) =>
            Mathf.Acos(Mathf.Clamp((b * b + c * c - a * a) / (2f * b * c), -1f, 1f));
    }
}
