using System;
using System.Collections.Generic;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>Bounded, sorted timeline. A later result at the same tick replaces the sample.</summary>
    internal sealed class SnapshotBuffer<T>
    {
        private readonly List<T> _samples = new List<T>();
        private readonly Func<T, long> _tick;
        private readonly int _capacity;
        internal SnapshotBuffer(Func<T, long> tick, int capacity) { _tick = tick; _capacity = capacity; }
        internal int Count => _samples.Count;
        internal T this[int index] => _samples[index];
        internal void Push(T sample)
        {
            long tick = _tick(sample);
            if (_samples.Count > 0 && tick < _tick(_samples[0])) return;
            int i = _samples.Count - 1;
            while (i >= 0 && _tick(_samples[i]) > tick) i--;
            if (i >= 0 && _tick(_samples[i]) == tick) _samples[i] = sample;
            else _samples.Insert(i + 1, sample);
            while (_samples.Count > _capacity) _samples.RemoveAt(0);
        }
        internal void Clear() => _samples.Clear();
    }

    /// <summary>
    /// F4 — buffered interpolation/extrapolation for one remote pose.
    ///
    /// Snapshots arrive unreliably with a host tick; applying them raw would jitter with every late
    /// or lost packet. They are buffered and the remote object is drawn at
    /// <c>(now - interpDelay)</c>, between the two snapshots around that time. Past the newest
    /// snapshot (packet loss) the pose is extrapolated briefly from the last known velocity.
    ///
    /// The buffer holds positions in the frame they were sent in — hull-local or real space — and
    /// knows nothing about it: the caller converts the sampled pose to world space every frame,
    /// because both the ship and the floating origin keep moving. A change of frame must
    /// <see cref="Clear"/> the buffer: positions of two frames cannot be interpolated.
    /// </summary>
    internal sealed class NetTransform
    {
        private struct Snap
        {
            public long Tick;
            public double X, Y, Z;
            public float Yaw, Pitch;
            public float Vx, Vy, Vz;
        }

        private readonly SnapshotBuffer<Snap> _buf = new SnapshotBuffer<Snap>(s => s.Tick, 32);
        private const float MaxExtrapolateMs = 250f;

        /// <summary>Interpolation delay in ms (config). Larger = smoother but more lag.</summary>
        public float InterpDelayMs = 100f;

        public bool HasData => _buf.Count > 0;

        public void Push(long tick, double x, double y, double z, float yaw, float pitch, float vx, float vy, float vz)
        {
            _buf.Push(new Snap { Tick = tick, X = x, Y = y, Z = z, Yaw = yaw, Pitch = pitch, Vx = vx, Vy = vy, Vz = vz });
        }

        /// <summary>Samples the buffer at host time <paramref name="serverTickNow"/>. False until data exists.</summary>
        public bool Sample(long serverTickNow, out double x, out double y, out double z, out float yaw, out float pitch)
        {
            x = y = z = 0.0;
            yaw = pitch = 0f;
            if (_buf.Count == 0) return false;

            long renderTick = serverTickNow - (long)InterpDelayMs;

            Snap first = _buf[0];
            if (renderTick <= first.Tick)
            {
                x = first.X; y = first.Y; z = first.Z; yaw = first.Yaw; pitch = first.Pitch;
                return true;
            }

            Snap last = _buf[_buf.Count - 1];
            if (renderTick >= last.Tick)
            {
                float ahead = Mathf.Min(renderTick - last.Tick, MaxExtrapolateMs) / 1000f;
                x = last.X + last.Vx * ahead; y = last.Y + last.Vy * ahead; z = last.Z + last.Vz * ahead;
                yaw = last.Yaw; pitch = last.Pitch;
                return true;
            }

            for (int i = 0; i < _buf.Count - 1; i++)
            {
                Snap a = _buf[i];
                Snap b = _buf[i + 1];
                if (renderTick < a.Tick || renderTick > b.Tick) continue;
                float span = b.Tick - a.Tick;
                float f = span > 0 ? (renderTick - a.Tick) / span : 0f;
                x = a.X + (b.X - a.X) * f; y = a.Y + (b.Y - a.Y) * f; z = a.Z + (b.Z - a.Z) * f;
                yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, f);
                pitch = Mathf.LerpAngle(a.Pitch, b.Pitch, f);
                return true;
            }
            x = last.X; y = last.Y; z = last.Z; yaw = last.Yaw; pitch = last.Pitch;
            return true;
        }

        public void Clear() => _buf.Clear();
    }
}
