using System;
using System.Reflection;
using Crest;
using Enviro;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Runtime;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace FollowingSeasCoop.Sync
{
    /// <summary>
    /// One sea, one wind, one time of day. The host's weather runs as in single player; several
    /// times a second it sends the whole state. A guest switches its own random weather off and
    /// writes the host's values over its own.
    ///
    /// The waves are an FFT of a spectrum (Crest <c>ShapeFFT</c>): the same spectrum, wind and clock
    /// give the same surface. The clock is the part a single-player game never shares — Crest reads
    /// it from a time provider, and the guest gets one that runs on the host's time.
    /// </summary>
    internal sealed class EnvSync
    {
        private const float SendInterval = 0.25f;
        /// <summary>Game seconds the guest's time of day may differ before it is set. A day is 10 real minutes.</summary>
        private const int TimeToleranceSeconds = 90;

        private sealed class OceanClock : ITimeProvider
        {
            public float Offset;
            public float CurrentTime => Time.time + Offset;
            public float DeltaTime => Time.deltaTime;
            public float DeltaTimeDynamics => Time.deltaTime;
        }

        private static readonly FieldInfo BaseWindField = AccessTools.Field(typeof(WeatherController), "baseWindIntensity");
        private static readonly FieldInfo ThetaField = AccessTools.Field(typeof(WeatherController), "theta");
        private static readonly FieldInfo NewWindField = AccessTools.Field(typeof(WeatherController), "newWindIntensity");

        private readonly CoopNet _net;
        private float _nextSendAt;
        private EnvStateMsg _latest;
        private bool _latestApplied = true;
        private string _unknownWeather;
        private readonly OceanClock _clock = new OceanClock();
        private OceanRenderer _clockOwner;
        private bool _clockSet;
        private bool _warnedFields;

        public int Sent { get; private set; }
        public int Received { get; private set; }

        public EnvSync(CoopNet net) { _net = net; }

        // -----------------------------------------------------------------
        // Host
        // -----------------------------------------------------------------

        public void HostTick()
        {
            float now = Time.unscaledTime;
            if (now < _nextSendAt) return;
            _nextSendAt = now + SendInterval;

            DataPersistenceManager dp = GameAccess.Dp;
            WeatherController weather = dp != null ? dp.weatherController : null;
            if (weather == null || weather.waveSpectrum == null || weather.windDirection == null) return;

            var msg = new EnvStateMsg
            {
                Tick = _net.Clock.ServerTick,
                WeatherId = weather.currentWeatherType != null ? weather.currentWeatherType.id : "",
                BaseWind = ReadFloat(BaseWindField, weather),
                GustPhase = ReadFloat(ThetaField, weather),
                WaveWind = weather.waveSpectrum._windSpeed,
                TimeScale = Time.timeScale,
            };
            Vector3 wind = weather.windDirection.rotation.eulerAngles;
            msg.WindX = wind.x; msg.WindY = wind.y; msg.WindZ = wind.z;

            OceanWaveSpectrum spectrum = weather.waveSpectrum._spectrum;
            if (spectrum != null)
            {
                msg.Chop = spectrum._chop;
                msg.GravityScale = spectrum._gravityScale;
                if (spectrum._powerLog != null)
                {
                    int bands = Math.Min(spectrum._powerLog.Length, EnvStateMsg.SpectrumBands);
                    msg.PowerLog = new float[bands];
                    Array.Copy(spectrum._powerLog, msg.PowerLog, bands);
                }
            }

            OceanRenderer ocean = OceanRenderer.Instance;
            msg.OceanTime = ocean != null ? ocean.CurrentTime : Time.time;

            EnviroManager enviro = dp.enviro;
            if (enviro != null && enviro.Time != null)
            {
                msg.Second = enviro.Time.seconds; msg.Minute = enviro.Time.minutes; msg.Hour = enviro.Time.hours;
                msg.Day = enviro.Time.days; msg.Month = enviro.Time.months; msg.Year = enviro.Time.years;
            }

            _net.Broadcast(msg, DeliveryMethod.Unreliable);
            Sent++;
        }

        // -----------------------------------------------------------------
        // Guest
        // -----------------------------------------------------------------

        public void OnEnvState(EnvStateMsg msg)
        {
            if (!_net.IsClient) return;
            // Unreliable packets can overtake each other; an older state must not undo a newer one.
            if (_latest != null && msg.Tick <= _latest.Tick) return;
            _latest = msg;
            _latestApplied = false;
            Received++;
        }

        public void GuestTick()
        {
            DataPersistenceManager dp = GameAccess.Dp;
            WeatherController weather = dp != null ? dp.weatherController : null;
            if (weather == null) return;

            // The guest's own weather must not roll dice. ChangeWeather() returns at once with this set.
            if (!weather.disableRandomChange) weather.disableRandomChange = true;

            if (_latest == null) return;
            ApplyOceanClock(_latest);
            if (_latestApplied) return;
            _latestApplied = true;
            ApplyWeather(weather, _latest);
            ApplyTimeOfDay(dp.enviro, _latest);
        }

        private void ApplyWeather(WeatherController weather, EnvStateMsg msg)
        {
            if (weather.waveSpectrum == null || weather.windDirection == null) return;
            var wind = new Vector3(msg.WindX, msg.WindY, msg.WindZ);

            // A new weather type brings its rain, sound and sky: the game's own SetWeather does all of
            // that. "instant" also stops the game's 25-second blend, which would fight the values below.
            string current = weather.currentWeatherType != null ? weather.currentWeatherType.id : null;
            if (!string.IsNullOrEmpty(msg.WeatherId) && current != msg.WeatherId && _unknownWeather != msg.WeatherId)
            {
                weather.SetWeather(msg.WeatherId, wind, msg.BaseWind, msg.WaveWind, instant: true);
                if (weather.currentWeatherType == null || weather.currentWeatherType.id != msg.WeatherId)
                {
                    // The game fell back to its first type: this copy does not know the host's.
                    _unknownWeather = msg.WeatherId;
                    Plugin.Log.LogWarning("[Env] The host's weather type '" + msg.WeatherId + "' does not exist in this copy of the game");
                }
                else
                {
                    Plugin.Log.LogInfo("[Env] Weather: " + msg.WeatherId);
                }
            }

            weather.windDirection.rotation = Quaternion.Euler(wind);
            weather.waveSpectrum._windSpeed = msg.WaveWind;
            OceanWaveSpectrum spectrum = weather.waveSpectrum._spectrum;
            if (spectrum != null)
            {
                spectrum._chop = msg.Chop;
                spectrum._gravityScale = msg.GravityScale;
                if (spectrum._powerLog != null)
                {
                    int bands = Math.Min(spectrum._powerLog.Length, msg.PowerLog.Length);
                    for (int i = 0; i < bands; i++) spectrum._powerLog[i] = msg.PowerLog[i];
                }
            }

            // The wind strength itself: the base value, the gust phase, and no blend of the guest's own.
            WriteFloat(BaseWindField, weather, msg.BaseWind);
            WriteFloat(ThetaField, weather, msg.GustPhase);
            WriteFloat(NewWindField, weather, 0f);
        }

        private static void ApplyTimeOfDay(EnviroManager enviro, EnvStateMsg msg)
        {
            if (enviro == null || enviro.Time == null) return;
            // An all-zero date means the host had no sky to read.
            if (msg.Year == 0 && msg.Month == 0 && msg.Day == 0 && msg.Hour == 0 && msg.Minute == 0 && msg.Second == 0) return;
            EnviroTimeModule time = enviro.Time;
            bool sameDay = time.days == msg.Day && time.months == msg.Month && time.years == msg.Year;
            int mine = time.hours * 3600 + time.minutes * 60 + time.seconds;
            int theirs = msg.Hour * 3600 + msg.Minute * 60 + msg.Second;
            // Both skies run at the same speed, so the guest is only set when it has drifted: setting
            // it on every message would make the sun step back and forth.
            if (sameDay && Math.Abs(mine - theirs) <= TimeToleranceSeconds) return;
            time.SetDateTime(msg.Second, msg.Minute, msg.Hour, msg.Day, msg.Month, msg.Year);
        }

        private void ApplyOceanClock(EnvStateMsg msg)
        {
            OceanRenderer ocean = OceanRenderer.Instance;
            if (ocean == null) return;
            if (_clockOwner != ocean)
            {
                // A new ocean (a new world scene) starts with its own provider stack.
                ocean.PushTimeProvider(_clock);
                _clockOwner = ocean;
                _clockSet = false;
            }
            // The host's ocean time now: what it reported, plus the time since, at the host's speed.
            float age = (_net.Clock.ServerTick - msg.Tick) / 1000f;
            float target = msg.OceanTime + age * msg.TimeScale - Time.time;
            if (!_clockSet || Mathf.Abs(target - _clock.Offset) > 1f)
            {
                _clock.Offset = target;
                _clockSet = true;
            }
            else
            {
                // Network jitter of a few milliseconds would make the waves twitch; ease towards it.
                _clock.Offset = Mathf.Lerp(_clock.Offset, target, Mathf.Clamp01(Time.unscaledDeltaTime * 2f));
            }
        }

        public void Clear()
        {
            _latest = null;
            _latestApplied = true;
            _unknownWeather = null;
            if (_clockOwner != null)
            {
                try { _clockOwner.PopTimeProvider(_clock); } catch { }
            }
            _clockOwner = null;
            _clockSet = false;
            Sent = Received = 0;
        }

        public string Describe()
        {
            if (_net.IsHost) return "sent " + Sent;
            return "received " + Received + (_latest != null ? "  weather " + _latest.WeatherId + "  ocean clock " +
                   (_clockSet ? _clock.Offset.ToString("F1") + " s" : "not set") : "");
        }

        private float ReadFloat(FieldInfo field, object target)
        {
            if (field == null) { WarnFields(); return 0f; }
            return (float)field.GetValue(target);
        }

        private void WriteFloat(FieldInfo field, object target, float value)
        {
            if (field == null) { WarnFields(); return; }
            field.SetValue(target, value);
        }

        private void WarnFields()
        {
            if (_warnedFields) return;
            _warnedFields = true;
            Plugin.Log.LogWarning("[Env] The game's wind fields were not found (a game update?); wind strength is not synchronised");
        }
    }
}
