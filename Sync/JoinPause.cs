using System.Collections.Generic;
using FollowingSeasCoop.Runtime;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// Host: the world stands still (<c>Time.timeScale = 0</c>) while a client loads the transferred
    /// world, so the ship does not sail away from the state the client was sent.
    /// </summary>
    internal sealed class JoinPause
    {
        private const float TimeoutSeconds = 180f;

        private readonly Dictionary<uint, float> _loading = new Dictionary<uint, float>();
        private readonly List<uint> _expired = new List<uint>();
        private bool _pausing;

        public bool Active => _pausing;
        public int LoadingCount => _loading.Count;

        public void Begin(uint netId) => _loading[netId] = Time.realtimeSinceStartup;
        public void End(uint netId) => _loading.Remove(netId);

        public void Tick()
        {
            if (_loading.Count > 0)
            {
                _expired.Clear();
                foreach (var pair in _loading)
                    if (Time.realtimeSinceStartup - pair.Value > TimeoutSeconds) _expired.Add(pair.Key);
                foreach (uint netId in _expired)
                {
                    _loading.Remove(netId);
                    Plugin.Log.LogWarning("[JoinPause] Player " + netId + " is still loading after " + (int)TimeoutSeconds + " s; the host resumes");
                }
            }

            if (_loading.Count > 0 && GameAccess.InWorld)
            {
                // The game's own menu sets the scale back to 1 when it closes, so hold it every frame.
                if (Time.timeScale != 0f) Time.timeScale = 0f;
                _pausing = true;
            }
            else if (_pausing)
            {
                Release();
            }
        }

        public void Reset()
        {
            _loading.Clear();
            if (_pausing) Release();
        }

        private void Release()
        {
            _pausing = false;
            // The game's pause menu keeps its own pause: timeScale 0 together with dp.paused.
            DataPersistenceManager dp = GameAccess.Dp;
            Time.timeScale = dp != null && dp.paused ? 0f : 1f;
        }
    }
}
