// Next Day: Survival - Revival Toolkit
//
// THE HIGH-ALTITUDE WIND. The game already whistles when a man stands on a
// tall roof - resources.tsv lists the clip as
// sounds/ambient/nature/ambience_sounds/wind_heavy. This file finds that
// same clip among the loaded AudioClips once and loops it under two more
// circumstances the game's own trigger does not cover: aboard a
// player-flown helicopter (Revival.PlayerHeli.cs, Aboard) and in the air
// under an open parachute canopy (Revival.Parachute.cs, Falling). If the
// clip cannot be found on a given client (a build that has not pulled it
// into memory yet) a procedural loop is synthesized instead - the same
// noise-filter technique RevivalMortar.cs uses for its report, because
// there is no donor AudioClip guaranteed to be loaded.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII-only comments and logs.
//
// SEAMS OUTSIDE THIS FILE (two lines):
//   RevivalPlugin.cs  BindConfig / Update
// and two reads, no writes, of state owned elsewhere:
//   Revival.PlayerHeli.cs  Aboard
//   Revival.Parachute.cs   Falling (also cleared here, on landing)

using System;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>Loops the game's own high-altitude wind while the local
    /// player is aboard a player-flown helicopter or descending under an
    /// open parachute canopy - the same whistle the game already plays on a
    /// tall roof, kept going under two more circumstances.</summary>
    internal static class WindSound
    {
        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<float> CfgVolume;

        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("WindSound", "Enabled", true,
                "Der Hoehenwind (derselbe Sound wie auf hohen Gebaeuden) laeuft "
                + "auch im Hubschrauber und am offenen Fallschirm.");
            CfgVolume = cfg.Bind("WindSound", "Volume", 0.6f,
                "Lautstaerke des Windgeraeuschs, 0..1.");
        }

        static bool Enabled
        {
            get { return CfgEnabled == null || CfgEnabled.Value; }
        }

        static float Volume
        {
            get { return CfgVolume == null ? 0.6f : Mathf.Clamp01(CfgVolume.Value); }
        }

        static AudioSource _source;
        static float _fade;                  // current volume multiplier, 0..1
        const float FadeSeconds = 1.5f;
        const float LandHeight = 3f;         // metres over terrain: counts as landed

        internal static void Tick()
        {
            if (!Enabled) { StopIfPlaying(); return; }
            try
            {
                if (Parachute.Falling) CheckLanded();
                bool want = PlayerHeli.Aboard || Parachute.Falling;

                if (want && _source == null) Start();
                if (_source == null) return;

                _fade = Mathf.MoveTowards(_fade, want ? 1f : 0f,
                    Time.deltaTime / FadeSeconds);
                _source.volume = _fade * Volume;

                if (!want && _fade <= 0.001f) StopIfPlaying();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("WindSound: " + ex.Message);
            }
        }

        /// <summary>A canopy back down near the ground has landed - the
        /// game's own parachute state ends itself there (see
        /// Revival.Parachute.cs), this just notices in time to fade the wind
        /// out with it instead of leaving it running over an empty sky.</summary>
        static void CheckLanded()
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null) return;
            float ground;
            if (!RevivalTroopInsertion.TerrainHeight(me.transform.position, out ground)) return;
            if (me.transform.position.y - ground < LandHeight) Parachute.Falling = false;
        }

        static void Start()
        {
            AudioClip clip = Clip();
            if (clip == null) return;
            GameObject go = new GameObject("NDR Wind Sound");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _source = go.AddComponent<AudioSource>();
            _source.clip = clip;
            _source.loop = true;
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;          // personal ambience, not a world sound
            _source.volume = 0f;
            _fade = 0f;
            _source.Play();
        }

        static void StopIfPlaying()
        {
            if (_source == null) return;
            UnityEngine.Object.Destroy(_source.gameObject);
            _source = null;
            _fade = 0f;
        }

        // -------------------------------------------------------------- clip

        static AudioClip _clip;
        static bool _lookedUpClip;

        /// <summary>The game's own high-altitude wind clip if it can be found
        /// among the loaded AudioClips (the one "wind_heavy" names in the
        /// asset list), else a procedural loop built once.</summary>
        static AudioClip Clip()
        {
            if (_clip != null) return _clip;
            if (!_lookedUpClip)
            {
                _lookedUpClip = true;
                try
                {
                    UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(AudioClip));
                    for (int i = 0; i < all.Length; i++)
                    {
                        AudioClip c = all[i] as AudioClip;
                        if (c == null || string.IsNullOrEmpty(c.name)) continue;
                        string n = c.name.ToLowerInvariant();
                        if (n.IndexOf("wind_heavy") >= 0)
                        {
                            _clip = c;
                            break;
                        }
                        if (_clip == null && n.IndexOf("wind") >= 0 && n.IndexOf("window") < 0)
                            _clip = c;             // second choice, keep looking for wind_heavy
                    }
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("WindSound: clip lookup: " + ex.Message);
                }
            }
            if (_clip != null) return _clip;
            return Synthesize();
        }

        static AudioClip _synth;

        /// <summary>Filtered noise, looped: a fallback for a client whose
        /// loaded scene has not pulled the game's own wind clip into memory
        /// (or a build without it). Two poles of low-passed white noise plus
        /// a slow double-sine swell so a straight loop does not sound like a
        /// machine - the layered noise-filter approach RevivalMortar.cs uses
        /// for its report, at ambient loudness instead of a gun's.</summary>
        static AudioClip Synthesize()
        {
            if (_synth != null) return _synth;
            try
            {
                const int rate = 44100;
                const float seconds = 4f;
                int n = (int)(rate * seconds);
                float[] data = new float[n];
                int seed = 1337;
                float low = 0f, hiss = 0f;
                for (int i = 0; i < n; i++)
                {
                    float t = (float)i / rate;
                    seed = seed * 1103515245 + 12345;
                    float noise = (((seed >> 16) & 0x7fff) / 16383.5f) - 1f;
                    low = low * 0.985f + noise * 0.015f;
                    hiss = hiss * 0.85f + noise * 0.15f;
                    float swell = 0.75f
                        + 0.15f * Mathf.Sin(2f * Mathf.PI * 0.09f * t)
                        + 0.10f * Mathf.Sin(2f * Mathf.PI * 0.031f * t + 1.7f);
                    data[i] = Mathf.Clamp((low * 2.2f + hiss * 0.35f) * swell, -1f, 1f);
                }
                // A loop has to meet itself: cross-fade the tail into the head
                // over a quarter second so the seam does not click.
                int fade = (int)(rate * 0.25f);
                for (int i = 0; i < fade; i++)
                {
                    float k = (float)i / fade;
                    data[n - fade + i] = data[n - fade + i] * (1f - k) + data[i] * k;
                }
                AudioClip clip = AudioClip.Create("NDR_WindHeavy", n, 1, rate, false);
                clip.SetData(data, 0);
                _synth = clip;
                return clip;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("WindSound: synth: " + ex.Message);
                return null;
            }
        }
    }
}
