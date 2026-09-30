// Next Day: Survival - Revival Toolkit
//
// X PERF-BISECT: the admin panel's "Perf" tab (docs/ai/tasks/x-perf-bisect.md).
// Every heavy toolkit feature in one list with a live on/off switch, the
// current frame time beside it and, with F6 on, each feature's own share of
// our calls; "Auto test" switches the features off one after the other and
// prints frame ms with / without each of them to the tab and the log.
//
// How a feature goes off (local, this client only, back on at restart - no
// config entry is written):
//   ticks     RevivalPlugin asks Run(gate) before the bracketed Tick /
//             LateFrame / FixedTick of the feature and skips it while off
//             (the module keeps its state and resumes where it stood).
//   render    far forest: canopy layer + cards off (FarForest.BisectLook, the
//             bench's own switch); edge terrain: the skirt root inactive;
//             content: every enabled renderer of the loaded EastAf* / EastMt*
//             / EastTown scenes disabled and exactly those enabled again (the
//             P2 combine itself cannot be undone at runtime - this shows what
//             the whole combined content costs to draw).
//   NPC tier  every NPC back to the game's own handling (NpcDistance.BisectOff:
//             tier far, no forest hiding, no AI throttle) and the tier tick off.
// A master client switching a master-run feature (patrols, air events, convoy)
// off pauses it for everyone for those seconds; the tooltips say so.
//
// Auto test: per feature 1 s settle + 3 s sample with it ON, then 5 s OFF
// (1 s settle + 4 s sample), then back on; ~2.4 min for the list. A window's
// value is the mean of its middle 80 % of frames (a GC pause or a streaming
// hitch would otherwise move a 3 s mean by ~0.4 ms); its worst frame is kept
// beside it. The user's own switches are put back afterwards. Result: a table in the tab, one log block
// ("[PerfBisect] ..."), a toast naming the biggest saving.
//
// Per frame: Tick is one float smoothing and one int test while idle
// (F6 slot S_PerfBisectT); a running auto test adds one phase test and a
// double add. Strings: built at 1 Hz for the visible tab (RefreshLive) and once
// per auto-test result. The gates are one bool array read per bracket.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class PerfBisect
    {
        // ---- gates: call-site groups in RevivalPlugin (Update / LateUpdate / FixedUpdate).
        internal const int G_EdgeTerrain = 0, G_FarForest = 1, G_ContentPerf = 2, G_NpcDistance = 3,
            G_Flak = 4, G_Patrol = 5, G_Ambience = 6, G_Mercs = 7, G_AirEvents = 8, G_MilitaryTown = 9,
            G_Radar = 10, G_GroundNpcs = 11, G_Gepard = 12, G_Convoy = 13, G_SettlementGuns = 14,
            GateCount = 15;

        // ---- features: the rows of the tab.
        internal const int F_EdgeTerrain = 0, F_FarForest = 1, F_ContentOcclusion = 2, F_ContentRender = 3,
            F_NpcDistance = 4, F_Flak = 5, F_Patrol = 6, F_Ambience = 7, F_Mercs = 8, F_AirEvents = 9,
            F_MilitaryTown = 10, F_Radar = 11, F_GroundNpcs = 12, F_Gepard = 13, F_Convoy = 14,
            F_SettlementGuns = 15, Count = 16;

        internal static readonly string[] Names = new string[] {
            "Edge terrain (map skirt)",
            "Far forest (canopy + cards)",
            "Content occlusion (interiors)",
            "Content render (airfield + town)",
            "NPC distance tiers",
            "Flak 52-K / ZU-23",
            "Patrols (driver + guns)",
            "Airfield ambience",
            "Mercs (fight, cover, AA, ride)",
            "Air events + NPC aircraft",
            "Military town (guns, posts)",
            "Tower radar + air picture",
            "Ground NPC groups + NPC war",
            "Gepard (patrol AA + crew)",
            "Convoy + troop helicopter",
            "Settlement mortar + artillery",
        };

        internal static readonly string[] Tips = new string[] {
            "AirBoundary: the terrain skirt beyond the map edge hidden, its tick paused",
            "FarForest: canopy splat layer and impostor cards off (like the far forest bench), tick paused",
            "ContentPerf.Tick paused: interior occlusion linecasts and the per-scene job",
            "every drawn renderer of the airfield / military town scenes hidden (the P2 combine cannot be undone live; this is its whole draw cost); ContentPerf.Tick paused",
            "every NPC back to the game's own handling (no wake tier, no forest hiding, no AI throttle); tier tick paused",
            "Flak.Tick + Flak.LateFrame paused (guns, crews, fire control, ZU-23 short range)",
            "Patrol.Tick + Patrol.FixedTick paused; as host the patrols stop for everyone for those seconds",
            "AirfieldAmbience.Tick paused (sounds keep playing)",
            "Mercs, merc cover, merc AA ticks and the ride LateFrame paused",
            "AirEvents + NpcAircraft ticks paused; as host the planes hold for everyone for those seconds",
            "MilitaryTown.Tick + LateFrame paused (town guns, posted men, fight clock)",
            "TowerRadar Tick/LateFrame + AirPicture.Tick paused",
            "RevivalGroundEnemies + NpcWar ticks paused",
            "Gepard.Tick + GepardCrew.LateFrame paused",
            "RevivalConvoy + RevivalTroopInsertion ticks paused; as host for everyone",
            "Mortar + ArtyBattery ticks paused",
        };

        // Gates each feature closes. The content render row also closes the
        // occlusion tick: it would switch interior renderers back on.
        static readonly int[][] Gates = new int[][] {
            new int[] { G_EdgeTerrain }, new int[] { G_FarForest }, new int[] { G_ContentPerf },
            new int[] { G_ContentPerf }, new int[] { G_NpcDistance }, new int[] { G_Flak },
            new int[] { G_Patrol }, new int[] { G_Ambience }, new int[] { G_Mercs },
            new int[] { G_AirEvents }, new int[] { G_MilitaryTown }, new int[] { G_Radar },
            new int[] { G_GroundNpcs }, new int[] { G_Gepard }, new int[] { G_Convoy },
            new int[] { G_SettlementGuns },
        };

        // F6 slots whose smoothed ms are the feature's own share of our calls.
        static readonly int[][] Slots = new int[][] {
            new int[] { FrameProf.S_AirBoundaryT },
            new int[] { FrameProf.S_FarForestT },
            new int[] { FrameProf.S_ContentPerfT },
            new int[0],
            new int[] { FrameProf.S_NpcDistT },
            new int[] { FrameProf.S_FlakT, FrameProf.S_FlakL },
            new int[] { FrameProf.PatrolTick, FrameProf.PatrolFixed },
            new int[] { FrameProf.S_AirfieldAmbienceT },
            new int[] { FrameProf.S_MercsT, FrameProf.S_MercCoverT, FrameProf.S_MercAAT, FrameProf.S_MercsL, FrameProf.S_MercAAL },
            new int[] { FrameProf.S_AirEventsT, FrameProf.S_NpcAircraftT },
            new int[] { FrameProf.S_MilitaryTownT, FrameProf.S_MilitaryTownL },
            new int[] { FrameProf.S_TowerRadarT, FrameProf.S_TowerRadarL, FrameProf.S_AirPictureT },
            new int[] { FrameProf.S_GroundEnemiesT, FrameProf.S_NpcWarT },
            new int[] { FrameProf.S_GepardT, FrameProf.S_GepardCrewL },
            new int[] { FrameProf.ConvoyTick, FrameProf.S_TroopInsertionT },
            new int[] { FrameProf.S_MortarT, FrameProf.S_ArtyBatteryT },
        };

        static readonly bool[] _off = new bool[Count];
        static readonly bool[] _gateOff = new bool[GateCount];

        /// <summary>Read by RevivalPlugin before a gated bracket: false while
        /// a feature that owns the gate is switched off here.</summary>
        internal static bool Run(int gate) { return !_gateOff[gate]; }

        internal static bool IsOff(int f) { return _off[f]; }

        internal static int OffCount
        {
            get { int n = 0; for (int i = 0; i < Count; i++) if (_off[i]) n++; return n; }
        }

        /// <summary>The admin switch: a feature off (true) or back on.</summary>
        internal static void SetOff(int f, bool off)
        {
            if (f < 0 || f >= Count || _off[f] == off) return;
            try
            {
                if (f == F_EdgeTerrain) Skirt(off);
                else if (f == F_FarForest) FarForest.BisectLook(off);
                else if (f == F_ContentRender) Content(off);
                else if (f == F_NpcDistance && off) NpcDistance.BisectOff();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("[PerfBisect] " + Names[f] + ": " + ex.Message);
            }
            _off[f] = off;
            for (int g = 0; g < GateCount; g++) _gateOff[g] = false;
            for (int i = 0; i < Count; i++)
            {
                if (!_off[i]) continue;
                int[] gs = Gates[i];
                for (int k = 0; k < gs.Length; k++) _gateOff[gs[k]] = true;
            }
            if (_auto == AutoIdle)
                RevivalPlugin.L.LogInfo("[PerfBisect] " + Names[f] + (off ? " OFF" : " on") + " (admin, local).");
        }

        internal static void AllOn()
        {
            for (int i = 0; i < Count; i++) SetOff(i, false);
        }

        // ============================================================ render switches

        static GameObject _skirtHidden;

        static void Skirt(bool off)
        {
            if (off)
            {
                GameObject s = AirBoundary.SkirtRoot;
                if (s != null && s.activeSelf) { s.SetActive(false); _skirtHidden = s; }
            }
            else
            {
                if (_skirtHidden != null) _skirtHidden.SetActive(true);
                _skirtHidden = null;
            }
        }

        static readonly List<Renderer> _contentHidden = new List<Renderer>();

        static void Content(bool off)
        {
            if (!off)
            {
                for (int i = 0; i < _contentHidden.Count; i++)
                    if (_contentHidden[i] != null) _contentHidden[i].enabled = true;
                _contentHidden.Clear();
                return;
            }
            // Once per switch (an admin click), not per frame.
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded || !IsContent(scene.name)) continue;
                GameObject[] roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    Renderer[] rs = roots[r].GetComponentsInChildren<Renderer>(false);
                    for (int k = 0; k < rs.Length; k++)
                    {
                        if (!rs[k].enabled) continue;
                        rs[k].enabled = false;
                        _contentHidden.Add(rs[k]);
                    }
                }
            }
        }

        static bool IsContent(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return false;
            ContentPerf.Profile[] ps = ContentPerf.Profiles;
            for (int i = 0; i < ps.Length; i++)
                for (int k = 0; k < ps[i].Prefixes.Length; k++)
                    if (scene.StartsWith(ps[i].Prefixes[k], StringComparison.Ordinal)) return true;
            return false;
        }

        // ============================================================ frame time + tick

        static float _frameMs;

        /// <summary>Smoothed frame time (ms) of the last second or so.</summary>
        internal static float FrameMs { get { return _frameMs; } }

        /// <summary>Every frame (slot S_PerfBisectT): the smoothed frame time
        /// and, while running, one step of the auto test.</summary>
        internal static void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f)
            {
                float ms = dt * 1000f;
                _frameMs = _frameMs <= 0f ? ms : _frameMs + (ms - _frameMs) * 0.05f;
            }
            if (_auto != AutoIdle) AutoStep(Time.realtimeSinceStartup, dt * 1000.0);
        }

        /// <summary>The feature's own F6 ms (sum of its slots), -1 while F6 is off.</summary>
        internal static double OwnMs(int f)
        {
            if (!FrameProf.On) return -1.0;
            double sum = 0.0;
            int[] s = Slots[f];
            for (int i = 0; i < s.Length; i++) sum += FrameProf.SlotMs(s[i]);
            return sum;
        }

        // ============================================================ auto test

        internal const float OnSettle = 1f, OnSample = 3f, OffSettle = 1f, OffSample = 4f;
        internal const float Trim = 0.1f;             // share of frames dropped at each end of a window
        const int AutoIdle = 0, AutoOnSettle = 1, AutoOnSample = 2, AutoOffSettle = 3, AutoOffSample = 4;

        static int _auto = AutoIdle;
        static int _autoFeature;
        static float _phaseAt;
        static double _max;
        static int _frames;
        // One window's frame times (4 s at up to 500 FPS); sorted once at its end.
        static readonly float[] _window = new float[2048];
        static readonly bool[] _manual = new bool[Count];
        static readonly double[] _onMs = new double[Count], _offMs = new double[Count];
        static readonly double[] _onMax = new double[Count], _offMax = new double[Count];
        static readonly double[] _ownMs = new double[Count];
        static bool _haveResult;
        static string _resultTitle;
        static readonly string[] _resOn = new string[Count], _resOff = new string[Count], _resDelta = new string[Count];
        static readonly int[] _resTone = new int[Count];     // 0 dim, 1 text, 2 warn

        internal static bool AutoRunning { get { return _auto != AutoIdle; } }
        internal static bool HaveResult { get { return _haveResult; } }
        internal static string ResultTitle { get { return _resultTitle; } }
        internal static string ResultOn(int f) { return _resOn[f]; }
        internal static string ResultOff(int f) { return _resOff[f]; }
        internal static string ResultDelta(int f) { return _resDelta[f]; }
        internal static int ResultTone(int f) { return _resTone[f]; }

        /// <summary>Seconds a whole auto test takes.</summary>
        internal static float AutoSeconds { get { return Count * (OnSettle + OnSample + OffSettle + OffSample); } }

        /// <summary>Admin button: start the auto test, or stop a running one.</summary>
        internal static string AutoToggle()
        {
            if (_auto != AutoIdle)
            {
                SetOff(_autoFeature, false);
                RestoreManual();
                _auto = AutoIdle;
                RevivalPlugin.L.LogInfo("[PerfBisect] auto test stopped at " + Names[_autoFeature] + ".");
                return "Perf auto test stopped; your switches are back.";
            }
            for (int i = 0; i < Count; i++) _manual[i] = _off[i];
            _auto = AutoOnSettle;          // before AllOn: no per-switch log lines
            AllOn();
            _autoFeature = 0;
            _haveResult = false;
            Phase(AutoOnSettle, Time.realtimeSinceStartup);
            string m = "Perf auto test started: " + Count + " features, ~"
                + Mathf.CeilToInt(AutoSeconds).ToString(CultureInfo.InvariantCulture)
                + " s. Hold still; table here and in the log.";
            RevivalPlugin.L.LogInfo("[PerfBisect] " + m);
            return m;
        }

        /// <summary>One line of progress for the tab (built by RefreshLive, 1 Hz).</summary>
        internal static string AutoProgress()
        {
            if (_auto == AutoIdle) return null;
            bool off = _auto >= AutoOffSettle;
            return "Auto test " + (_autoFeature + 1) + "/" + Count + ": " + Names[_autoFeature]
                + (off ? " OFF" : " on (baseline)");
        }

        static void Phase(int phase, float now)
        {
            _auto = phase;
            _phaseAt = now;
            _max = 0.0;
            _frames = 0;
        }

        static void AutoStep(float now, double ms)
        {
            float age = now - _phaseAt;
            switch (_auto)
            {
                case AutoOnSettle:
                    if (age >= OnSettle) Phase(AutoOnSample, now);
                    return;
                case AutoOffSettle:
                    if (age >= OffSettle) Phase(AutoOffSample, now);
                    return;
                case AutoOnSample:
                case AutoOffSample:
                    if (ms > _max) _max = ms;
                    if (_frames < _window.Length) _window[_frames++] = (float)ms;
                    if (age < (_auto == AutoOnSample ? OnSample : OffSample)) return;
                    break;
                default:
                    return;
            }
            int f = _autoFeature;
            double avg = TrimmedMean(_window, _frames, Trim);
            if (_auto == AutoOnSample)
            {
                _onMs[f] = avg;
                _onMax[f] = _max;
                _ownMs[f] = OwnMs(f);
                SetOff(f, true);
                Phase(AutoOffSettle, now);
                return;
            }
            _offMs[f] = avg;
            _offMax[f] = _max;
            SetOff(f, false);
            if (++_autoFeature < Count) { Phase(AutoOnSettle, now); return; }
            _auto = AutoIdle;
            RestoreManual();
            Finish();
        }

        /// <summary>Mean of the middle (1 - 2 trim) of the first n values;
        /// sorts them in place. Once per window, not per frame.</summary>
        internal static double TrimmedMean(float[] v, int n, float trim)
        {
            if (n <= 0) return 0.0;
            Array.Sort(v, 0, n);
            int cut = (int)(n * trim);
            double sum = 0.0;
            for (int i = cut; i < n - cut; i++) sum += v[i];
            return sum / (n - 2 * cut);
        }

        static void RestoreManual()
        {
            for (int i = 0; i < Count; i++) SetOff(i, _manual[i]);
        }

        /// <summary>Per feature: saving (ms) = frame with it on - frame with it off.</summary>
        internal static double Saving(double onMs, double offMs) { return onMs - offMs; }

        /// <summary>Row tone: 2 = it costs more than NoiseMs (warn), 1 = measurable, 0 = noise.</summary>
        internal static int Tone(double saving)
        {
            if (saving >= WarnMs) return 2;
            if (saving >= NoiseMs) return 1;
            return 0;
        }

        internal const double NoiseMs = 0.5, WarnMs = 1.5;   // NoiseMs: ~2 sigma of a 3 s / 4 s window pair at 1.5 ms frame jitter

        static void Finish()
        {
            _haveResult = true;
            int best = 0;
            StringBuilder log = new StringBuilder(2048);
            log.Append("[PerfBisect] auto test result (frame ms with the feature on / off: mean of the middle 80 %, 1 s settle each):\n");
            log.Append(Pad("feature", 34)).Append(Pad("on ms", 9)).Append(Pad("off ms", 9))
               .Append(Pad("saved", 8)).Append(Pad("on max", 9)).Append(Pad("off max", 9)).Append("own F6\n");
            for (int f = 0; f < Count; f++)
            {
                double saved = Saving(_onMs[f], _offMs[f]);
                if (saved > Saving(_onMs[best], _offMs[best])) best = f;
                _resOn[f] = F2(_onMs[f]);
                _resOff[f] = F2(_offMs[f]);
                _resDelta[f] = (saved >= 0 ? "-" : "+") + F2(Math.Abs(saved));
                _resTone[f] = Tone(saved);
                log.Append(Pad(Names[f], 34)).Append(Pad(_resOn[f], 9)).Append(Pad(_resOff[f], 9))
                   .Append(Pad(F2(saved), 8)).Append(Pad(F2(_onMax[f]), 9)).Append(Pad(F2(_offMax[f]), 9))
                   .Append(_ownMs[f] < 0 ? "F6 off" : F2(_ownMs[f])).Append('\n');
            }
            double top = Saving(_onMs[best], _offMs[best]);
            _resultTitle = top >= NoiseMs
                ? "Biggest: " + Names[best] + " saves " + F2(top) + " ms/frame when off"
                : "No feature saves more than " + F2(NoiseMs) + " ms/frame here";
            log.Append(_resultTitle).Append('.');
            RevivalPlugin.L.LogInfo(log.ToString());
            UiKit.Toast("Perf auto test done. " + _resultTitle + ".", UiTone.Success);
        }

        static string Pad(string s, int n)
        {
            if (s == null) s = "";
            return s.Length >= n ? s + " " : s + new string(' ', n - s.Length);
        }

        internal static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        // ============================================================ live text (1 Hz, tab visible)

        static readonly string[] _liveOwn = new string[Count];
        static string _liveHead, _liveProgress;

        internal static string LiveHead { get { return _liveHead; } }
        internal static string LiveProgress { get { return _liveProgress; } }
        internal static string LiveOwn(int f) { return _liveOwn[f]; }

        /// <summary>1 Hz while the Perf tab is shown: the frame line and every
        /// feature's own F6 share.</summary>
        internal static void RefreshLive()
        {
            float ms = _frameMs;
            string head = "Frame " + F2(ms) + " ms  /  "
                + (ms > 0f ? (1000f / ms).ToString("0", CultureInfo.InvariantCulture) : "-") + " FPS";
            int off = OffCount;
            if (off > 0) head += "   (" + off + " off)";
            if (!FrameProf.On) head += "   - F6 for each feature's own ms";
            _liveHead = head;
            _liveProgress = AutoProgress();
            for (int f = 0; f < Count; f++)
            {
                double own = OwnMs(f);
                _liveOwn[f] = Slots[f].Length == 0 ? "render" : own < 0 ? "" : "own " + F2(own) + " ms";
            }
        }
    }
}
