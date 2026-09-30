// Next Day: Survival - Revival Toolkit
//
// FrameProf - an in-game, toggleable frame-time overlay for the toolkit's own
// per-frame work. It exists to ANSWER a reported FPS drop with a measurement
// instead of a guess: it shows the overall frame rate (with a 1% low, so a
// steady drop is told apart from occasional hitches) and, broken out, how many
// milliseconds each of our Update/FixedUpdate/LateUpdate ticks and OnGUI draws
// costs THIS build. If our systems barely register, the cost is elsewhere
// (rendering, physics, the base game, drivers) and we look there; if one of
// ours stands out, it names itself.
//
// Q1 perf (6.57.0 stutter report) extended it to cover EVERY Revival call in
// RevivalPlugin's Update, FixedUpdate, LateUpdate and OnGUI (the east world,
// flak, radar, fuel depot, BuildingNav, ContentPerf, EastLadders ... were not
// bracketed before, which is why "measured calls" read 14% of the frame), and
// added what the frame time alone cannot tell:
//   - managed allocations per frame and per slot (GC.GetTotalMemory deltas -
//     block granular on Unity's Boehm GC, so read the smoothed figures), GC
//     collections, the managed heap size. Garbage is paid for in one
//     stop-the-world pause, so a steady KB/frame is a future long frame;
//   - peaks over the last ~4 s, not only since F6 was pressed;
//   - physics steps per frame (a slow frame runs several FixedUpdates, which
//     multiplies every FixedTick);
//   - the render settings that drive GPU cost (far clip, shadow distance, LOD
//     bias, terrain tree/billboard/grass distances), so a screenshot of the
//     overlay says which view settings were active;
//   - every frame over 50 ms is attributed in the log: how much of it was ours,
//     which slot, whether a GC ran, how many physics steps - "FrameSpike:" lines.
//
// Cost when OFF is a single bool test per bracket (S/E return immediately) plus
// one subtraction per frame for the FPS ring - deliberately negligible, so the
// overlay never distorts the number it is trying to measure. The overlay's text
// is rebuilt four times a second, not per OnGUI pass, so its own strings do not
// show up as allocation. It ships OFF and is toggled with a key at runtime
// (default F6). All player-visible strings go through Loc.T (bilingual) or are
// plain ASCII numbers; comments and logs stay ASCII.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// UTF-8 (no BOM), compiled with /codepage:65001 like the rest.

using System;
using System.Diagnostics;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// Per-slot frame-time accumulator and overlay. A "slot" is one bracketed
    /// call site (a module's Tick, LateFrame or Draw). <see cref="S"/> stamps the
    /// start, <see cref="E"/> adds the elapsed span (and the managed memory
    /// growth) to that slot's accumulator; both are no-ops while the overlay is
    /// off. <see cref="NewFrame"/>, called first in Update, folds the finished
    /// frame's accumulators into smoothed per-slot figures and clears them.
    /// </summary>
    public static class FrameProf
    {
        // ---- slot ids. Keep in sync with Names below.
        public const int NetWatch     = 0;
        public const int AdminTick    = 1;
        public const int MapTeleTick  = 2;
        public const int Cursor       = 3;
        public const int Regions      = 4;
        public const int Research     = 5;
        public const int TurretTick   = 6;
        public const int VehModTick   = 7;
        public const int DroneTick    = 8;
        public const int DroneGearT   = 9;
        public const int Arena        = 10;
        public const int CarSpawn     = 11;
        public const int PatrolTick   = 12;
        public const int ConvRepTick  = 13;
        public const int ConvoyTick   = 14;
        public const int CrewDrone    = 15;
        public const int DroneAlrtT   = 16;

        public const int TurretScope  = 17;
        public const int DroneDraw    = 18;
        public const int DroneGearD   = 19;
        public const int PatrolMap    = 20;
        public const int MapTeleDraw  = 21;
        public const int AdminDraw    = 22;
        public const int PatrolDraw   = 23;
        public const int ConvRepDraw  = 24;
        public const int ConvoyDraw   = 25;
        public const int DroneAlrtD   = 26;

        public const int PatrolFixed = 27;
        public const int CameraLate = 28;
        public const int PeerTick = 29;
        public const int OtherDraw = 30;
        // Q1 perf: every other call in RevivalPlugin's frame loop. T = Update,
        // L = LateUpdate, D = OnGUI.
        public const int S_LiveRoutesT = 31;
        public const int S_ClientIntegrityT = 32;
        public const int S_NativeActionProgressT = 33;
        public const int S_EastWorldT = 34;
        public const int S_EastCrossingsT = 35;
        public const int S_EastTileT = 36;
        public const int S_AirfieldAmbienceT = 37;
        public const int S_EastZonesT = 38;
        public const int S_BuildingNavT = 39;
        public const int S_ContentPerfT = 40;
        public const int S_EastLaddersT = 41;
        public const int S_FrameBenchT = 42;
        public const int S_BtrGunT = 43;
        public const int S_UralTruckT = 44;
        public const int S_TechnicalT = 45;
        public const int S_ArtyVehicleT = 46;
        public const int S_GepardT = 47;
        public const int S_FlakT = 48;
        public const int S_TowerRadarT = 49;
        public const int S_NoFlyT = 50;
        public const int S_AntiTankMineT = 51;
        public const int S_ApMineT = 52;
        public const int S_StingerT = 53;
        public const int S_GasLauncherT = 54;
        public const int S_TroopInsertionT = 55;
        public const int S_GroundEnemiesT = 56;
        public const int S_AirfieldT = 57;
        public const int S_MilitaryTownT = 58;
        public const int S_HelipadsT = 59;
        public const int S_PlayerHeliT = 60;
        public const int S_PlayerAn2T = 61;
        public const int S_An2RepairT = 62;
        public const int S_An2BombsT = 63;
        public const int S_FuelBalanceT = 64;
        public const int S_FuelStationsT = 65;
        public const int S_FuelDepotT = 66;
        public const int S_WindSoundT = 67;
        public const int S_NewSettlementT = 68;
        public const int S_CrocodileT = 69;
        public const int S_TraitorVendorT = 70;
        public const int S_MortarT = 71;
        public const int S_ArtyBatteryT = 72;
        public const int S_NpcWarT = 73;
        public const int S_TechnicalL = 74;
        public const int S_GepardCrewL = 75;
        public const int S_MilitaryTownL = 76;
        public const int S_FlakL = 77;
        public const int S_TowerRadarL = 78;
        public const int S_ViewDistanceL = 79;
        public const int S_PlayerHeliL = 80;
        public const int S_PlayerAn2L = 81;
        public const int S_TraitorVendorL = 82;
        public const int S_BtrGunL = 83;
        public const int S_TroopInsertionD = 84;
        public const int S_EastZonesD = 85;
        public const int S_HelipadsD = 86;
        public const int S_PlayerHeliD = 87;
        public const int S_PlayerAn2D = 88;
        public const int S_An2RepairD = 89;
        public const int S_An2BombsD = 90;
        public const int S_FuelDepotD = 91;
        public const int S_NewSettlementD = 92;
        public const int S_CrocodileD = 93;
        public const int S_MortarD = 94;
        public const int S_ArtyBatteryD = 95;
        public const int S_TechnicalD = 96;
        public const int S_GepardD = 97;
        public const int S_FlakD = 98;
        public const int S_TowerRadarD = 99;
        public const int S_NoFlyD = 100;
        public const int S_PeerCheckD = 101;
        public const int S_ClientIntegrityD = 102;
        public const int S_NpcWarD = 103;
        public const int S_SettingsD = 104;
        public const int S_NpcDistT = 105;
        public const int S_FarForestT = 106;
        public const int S_KatyushaT = 107;
        public const int S_KatyushaD = 108;
        public const int S_VehicleConditionT = 109;
        public const int S_VehicleConditionD = 110;
        public const int S_Tu95Visual_Update = 111;
        public const int S_CrewRemoteFix_Update = 112;
        public const int S_CrewAnimationKeeper_Update = 113;
        public const int S_CrewAlarm_Update = 114;
        public const int S_CrewLawSwap_Update = 115;
        public const int S_CrocodileSwimmer_Update = 116;
        public const int S_CrocodileSwimmer_LateUpdate = 117;
        public const int S_MapInkLayer_LateUpdate = 118;
        public const int S_MapLabels_LateUpdate = 119;
        public const int S_An2Visual_Update = 120;
        public const int S_An2Glide_Update = 121;
        public const int S_HeliCrashFall_Update = 122;
        public const int S_HeliWreckSettle_Update = 123;
        public const int S_HeliEngine_Update = 124;
        public const int S_RoadClear_Update = 125;
        public const int S_T72RunningGear_LateUpdate = 126;
        public const int S_NdrFlash_Update = 127;
        public const int S_MineObject_Update = 128;
        public const int S_ArtyDroneCrash_Update = 129;
        public const int S_ArtyRecoil_LateUpdate = 130;
        public const int S_GasRound_Update = 131;
        public const int S_GasCloud_Update = 132;
        public const int S_GepardRig_Update = 133;
        public const int S_TechnicalTracerStreak_Update = 134;
        public const int S_MercCoverT = 135;
        public const int S_MercCoverD = 136;
        public const int S_MercsT = 137;
        public const int S_MercsD = 138;
        public const int S_MercsL = 139;
        public const int S_UiKitT = 140;
        public const int S_UiKitD = 141;
        public const int S_MercNotifyT = 142;
        public const int S_TraderT = 143;
        public const int S_TraderD = 144;
        public const int S_MercPageT = 145;
        public const int S_MercPageD = 146;
        public const int S_MercAAT = 147;
        public const int S_MercAAL = 148;
        public const int S_AirKillsT = 149;
        public const int S_ShortRangeT = 150;
        public const int S_Mi8FlaresT = 151;
        public const int S_RadarShadowT = 152;
        public const int S_MercStingerT = 153;
        public const int S_AaDamageT = 154;
        public const int S_AaDamageD = 155;
        public const int S_RetakeRaidsT = 156;
        public const int S_OrdnanceBlastT = 157;
        public const int S_AirEventsT = 158;
        public const int S_NpcAircraftT = 159;
        public const int S_AirBoundaryT = 160;
        public const int Sub_PatrolGun = 161;
        public const int Sub_PatrolAuto = 162;
        public const int Sub_PatrolKeys = 163;
        public const int S_EnginePerfT = 164;
        public const int S_AirPictureT = 165;
        public const int S_AirPictureD = 166;
        public const int S_TowerSupportT = 167;
        public const int S_TowerSupportD = 168;
        public const int S_AirfieldHoldT = 169;
        public const int S_AirfieldHoldD = 170;
        // W edge terrain: the skirt's height query, called inside other slots.
        public const int S_AirBoundaryH = 171;
        public const int S_GroundAliveT = 172;
        public const int S_ParatroopersT = 173;
        public const int Count = 174;

        static readonly string[] Names = new string[]
        {
            "NetWatch.Tick", "Admin.Tick", "MapTeleport.Tick", "CursorGuard",
            "Regions.Tick", "Research.Tick", "Turret.Tick", "VehicleModules.Tick",
            "Drone.Tick", "DroneGear.Tick", "Arena.Tick", "CarSpawn.Tick",
            "Patrol.Tick", "ConvoyRepair.Tick", "Convoy.Tick", "CrewDrone.Tick",
            "DroneAlert.Tick",
            "Turret.DrawScope", "Drone.Draw", "DroneGear.Draw", "Patrol.DrawMap",
            "MapTeleport.Draw", "Admin.Draw", "Patrol.Draw", "ConvoyRepair.Draw",
            "Convoy.Draw", "DroneAlert.Draw",
            "Patrol.FixedTick", "Camera.LateTick", "PeerCheck.Tick", "Mines+Gas+Stinger.Draw",
            "LiveRoutes.Tick",
            "ClientIntegrity.Tick",
            "NativeActionProgress.Tick",
            "EastWorld.Tick",
            "EastCrossings.Tick",
            "EastTile.Tick",
            "AirfieldAmbience.Tick",
            "EastZones.Tick",
            "BuildingNav.Tick",
            "ContentPerf.Tick",
            "EastLadders.Tick",
            "FrameBench.Tick",
            "BtrGun.Tick",
            "UralTruck.Tick",
            "Technical.Tick",
            "ArtyVehicle.Tick",
            "Gepard.Tick",
            "Flak.Tick",
            "TowerRadar.Tick",
            "NoFly.Tick",
            "AntiTankMine.Tick",
            "ApMine.Tick",
            "Stinger.Tick",
            "GasLauncher.Tick",
            "TroopInsertion.Tick",
            "GroundEnemies.Tick",
            "Airfield.Tick",
            "MilitaryTown.Tick",
            "Helipads.Tick",
            "PlayerHeli.Tick",
            "PlayerAn2.Tick",
            "An2Repair.Tick",
            "An2Bombs.Tick",
            "FuelBalance.Tick",
            "FuelStations.Tick",
            "FuelDepot.Tick",
            "WindSound.Tick",
            "NewSettlement.Tick",
            "Crocodile.Tick",
            "TraitorVendor.Tick",
            "Mortar.Tick",
            "ArtyBattery.Tick",
            "NpcWar.Tick",
            "Technical.LateFrame",
            "GepardCrew.LateFrame",
            "MilitaryTown.LateFrame",
            "Flak.LateFrame",
            "TowerRadar.LateFrame",
            "ViewDistance.LateTick",
            "PlayerHeli.LateFrame",
            "PlayerAn2.LateFrame",
            "TraitorVendor.LateFrame",
            "BtrGun.LateFrame",
            "TroopInsertion.Draw",
            "EastZones.Draw",
            "Helipads.Draw",
            "PlayerHeli.Draw",
            "PlayerAn2.Draw",
            "An2Repair.Draw",
            "An2Bombs.Draw",
            "FuelDepot.Draw",
            "NewSettlement.Draw",
            "Crocodile.Draw",
            "Mortar.Draw",
            "ArtyBattery.Draw",
            "Technical.Draw",
            "Gepard.Draw",
            "Flak.Draw",
            "TowerRadar.Draw",
            "NoFly.Draw",
            "PeerCheck.Draw",
            "ClientIntegrity.Draw",
            "NpcWar.Draw",
            "Settings.Draw",
            "NpcDistance.Tick",
            "FarForest.Tick",
            "Katyusha.Tick",
            "Katyusha.Draw",
            "VehicleCondition.Tick",
            "VehicleCondition.Draw",
            "Tu95Visual.Update",
            "CrewRemoteFix.Update",
            "CrewAnimationKeeper.Update",
            "CrewAlarm.Update",
            "CrewLawSwap.Update",
            "CrocodileSwimmer.Update",
            "CrocodileSwimmer.LateUpdate",
            "MapInkLayer.LateUpdate",
            "MapLabels.LateUpdate",
            "An2Visual.Update",
            "An2Glide.Update",
            "HeliCrashFall.Update",
            "HeliWreckSettle.Update",
            "HeliEngine.Update",
            "RoadClear.Update",
            "T72RunningGear.LateUpdate",
            "NdrFlash.Update",
            "MineObject.Update",
            "ArtyDroneCrash.Update",
            "ArtyRecoil.LateUpdate",
            "GasRound.Update",
            "GasCloud.Update",
            "GepardRig.Update",
            "TechnicalTracerStreak.Update",
            "MercCover.Tick",
            "MercCover.Draw",
            "Mercs.Tick",
            "Mercs.Draw",
            "Mercs.LateFrame",
            "UiKit.Tick",
            "UiKit.Draw",
            "MercNotify.Tick",
            "TraderUi.Tick",
            "TraderUi.Draw",
            "MercPage.Tick",
            "MercPage.Draw",
            "MercAA.Tick",
            "MercAA.LateFrame",
            "AirKills.Tick",
            "ShortRange.Control",
            "Mi8Flares.Tick",
            "RadarShadow.Scan",
            "MercStinger.Control",
            "AirDefenceDamage.Tick",
            "AirDefenceDamage.Draw",
            "RetakeRaids.Tick",
            "OrdnanceBlast.Tick",
            "AirEvents.Tick",
            "NpcAircraft.Tick",
            "AirBoundary.Tick",
            "  Patrol.Gun.Sub",
            "  Patrol.Auto.Sub",
            "  Patrol.Keys.Sub",
            "EnginePerf.Tick",
            "AirPicture.Tick",
            "AirPicture.Draw",
            "TowerSupport.Tick",
            "TowerSupport.Draw",
            "AirfieldHold.Tick",
            "AirfieldHold.Draw",
            "  AirBoundary.Height.Sub",
            "GroundAlive.Tick",
            "Paratroopers.Tick",
        };

        // 0 = Update, 1 = FixedUpdate, 2 = LateUpdate, 3 = OnGUI, 4 = nested
        // sub-slot (inside another slot; shown, never summed).
        static readonly byte[] Kind = new byte[Count];

        static FrameProf()
        {
            for (int i = 0; i < Count; i++)
            {
                string n = Names[i];
                if (n.EndsWith(".Sub")) Kind[i] = 4;
                else if ((i >= TurretScope && i <= DroneAlrtD) || i == OtherDraw
                    || n.EndsWith(".Draw") || n.EndsWith(".DrawScope") || n.EndsWith(".DrawMap"))
                    Kind[i] = 3;
                else if (i == PatrolFixed || n.EndsWith(".FixedUpdate")) Kind[i] = 1;
                else if (i == CameraLate || n.EndsWith(".LateFrame") || n.EndsWith(".LateTick") || n.EndsWith(".LateUpdate"))
                    Kind[i] = 2;
            }
        }

        // Per-slot: start stamp and memory (this frame), accumulated ticks and
        // bytes (this frame).
        static readonly long[] _mark = new long[Count];
        static readonly long[] _acc  = new long[Count];
        static readonly long[] _memMark = new long[Count];
        static readonly long[] _memAcc = new long[Count];
        // Smoothed per-slot milliseconds and KB, shown in the overlay.
        static readonly double[] _ms = new double[Count];
        static readonly double[] _kb = new double[Count];
        static readonly double[] _peak = new double[Count];     // since F6
        static readonly double[] _winCur = new double[Count];   // this 2 s window
        static readonly double[] _winPrev = new double[Count];  // the one before
        static readonly int[] _order = new int[Count];
        static float _winEnd;

        // Stopwatch ticks -> milliseconds. Stopwatch, not DateTime: it is the
        // high-resolution timer and cheap to sample.
        static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
        // Exponential smoothing so the numbers are readable, not a blur of noise.
        const double Smooth = 0.1;

        // ---- overall frame rate, tracked always (one subtraction per frame).
        const int Ring = 240;                       // ~4 s at 60 fps
        static readonly float[] _dt = new float[Ring];
        static readonly float[] _sortedDt = new float[Ring];
        static float _worstMs;
        static int _slowFrames;
        static float _nextLog;
        static int _gcStart = GC.CollectionCount(0);
        static int _dtN;
        static int _dtCount;
        static double _fpsAvg;
        static float _low1;                         // 1% low fps
        static float _lowThrottle;

        // ---- memory, physics steps, spikes (only while On).
        static long _lastMem;
        static int _lastGc = -1;
        static double _allocKb;                     // smoothed KB per frame
        static double _heapMb;
        static int _fixedThis;
        static double _fixedAvg;
        static int _fixedLast;
        static string _lastSpike = "";
        static float _nextSpikeLog;
        static int _spikes;

        public static bool On;
        static ConfigEntry<string> _key;
        static ConfigEntry<bool> _start;
        static KeyCode _toggle = KeyCode.F6;
        static bool _keyResolved;

        public static void BindConfig(ConfigFile cfg)
        {
            _start = cfg.Bind("Diagnostics", "ShowFrameOverlay", false,
                "Blendet ein Messfenster ein, das die Bild-fuer-Bild-Kosten der "
                + "Toolkit-Systeme in Millisekunden zeigt (Gesamt-FPS, 1%-Low und "
                + "je Modul). Nur zur Fehlersuche gedacht, standardmaessig aus.");
            _key = cfg.Bind("Diagnostics", "FrameOverlayKey", "F6",
                "Taste, die das Messfenster im Spiel ein- und ausschaltet.");
            On = _start != null && _start.Value;
        }

        static KeyCode ToggleKey()
        {
            if (_keyResolved) return _toggle;
            _keyResolved = true;
            string s = _key != null ? _key.Value : "F6";
            try { _toggle = (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
            catch { _toggle = KeyCode.F6; }
            return _toggle;
        }

        /// <summary>Start of a bracket. No-op while the overlay is off.</summary>
        public static void S(int slot)
        {
            if (!On) return;
            if (slot < 0 || slot >= Count) return;
            _memMark[slot] = GC.GetTotalMemory(false);
            _mark[slot] = Stopwatch.GetTimestamp();
        }

        /// <summary>End of a bracket. No-op while the overlay is off.</summary>
        public static void E(int slot)
        {
            if (!On) return;
            if (slot < 0 || slot >= Count) return;
            _acc[slot] += Stopwatch.GetTimestamp() - _mark[slot];
            long grew = GC.GetTotalMemory(false) - _memMark[slot];
            if (grew > 0) _memAcc[slot] += grew;    // a GC inside reads negative: ignored
        }

        /// <summary>Called at the top of FixedUpdate: physics steps per frame.</summary>
        public static void FixedStep() { _fixedThis++; }

        /// <summary>
        /// Called once at the very top of Update. Handles the toggle key, folds
        /// the frame that just ended into the smoothed averages, tracks the
        /// overall frame rate, and clears the accumulators for the new frame.
        /// </summary>
        public static void NewFrame()
        {
            try
            {
                int fixedSteps = _fixedThis;
                _fixedThis = 0;

                if (Input.GetKeyDown(ToggleKey()))
                {
                    On = !On;
                    if (On)
                    {
                        // Entering: clear stale spans so the first shown frame is
                        // real, not a span measured across the paused interval.
                        for (int i = 0; i < Count; i++)
                        {
                            _acc[i] = 0; _ms[i] = 0; _peak[i] = 0; _memAcc[i] = 0; _kb[i] = 0;
                            _winCur[i] = 0; _winPrev[i] = 0;
                        }
                        _gcStart = GC.CollectionCount(0);
                        _lastGc = -1;
                        _allocKb = 0;
                        _spikes = 0;
                        _lastSpike = "";
                        _nextLog = Time.unscaledTime + 5f;
                        _textAt = 0f;
                    }
                }

                // Overall frame rate - tracked even while the per-slot overlay is
                // off, so toggling on shows a settled number immediately.
                float dt = Time.unscaledDeltaTime;
                if (dt > 0f)
                {
                    _dt[_dtN] = dt;
                    _dtN = (_dtN + 1) % Ring;
                    if (_dtCount < Ring) _dtCount++;
                    double fps = 1.0 / dt;
                    _fpsAvg = _dtCount == 1 ? fps : _fpsAvg + (fps - _fpsAvg) * 0.05;
                }

                if (!On) return;

                // Managed memory: growth since the last frame is what the frame
                // allocated (block granular); a collection resets the heap, so
                // that frame's figure is unknown and skipped.
                long mem = GC.GetTotalMemory(false);
                int gc = GC.CollectionCount(0);
                bool gcRan = _lastGc >= 0 && gc != _lastGc;
                if (_lastGc >= 0 && !gcRan && mem >= _lastMem)
                    _allocKb += ((mem - _lastMem) / 1024.0 - _allocKb) * Smooth;
                _lastMem = mem;
                _lastGc = gc;
                _heapMb = mem / (1024.0 * 1024.0);
                _fixedAvg += (fixedSteps - _fixedAvg) * Smooth;
                _fixedLast = fixedSteps;

                // A long frame: say in the log how much of it was ours.
                float frameMs = dt * 1000f;
                if (frameMs > 50f && _dtCount > 5)
                {
                    _spikes++;
                    double ours = 0; int top = 0;
                    for (int i = 0; i < Count; i++)
                    {
                        if (Kind[i] != 4) ours += _acc[i] * TickMs;
                        if (_acc[i] > _acc[top]) top = i;
                    }
                    _lastSpike = string.Format(
                        "{0:0} ms: ours {1:0.0} ms (top {2} {3:0.0}), GC {4}, physics steps {5}",
                        frameMs, ours, Names[top], _acc[top] * TickMs, gcRan ? "YES" : "no", fixedSteps);
                    if (Time.unscaledTime >= _nextSpikeLog)
                    {
                        _nextSpikeLog = Time.unscaledTime + 1f;
                        RevivalPlugin.L.LogInfo("FrameSpike: " + _lastSpike + ", heap "
                            + _heapMb.ToString("0") + " MB, spikes since F6 " + _spikes + ".");
                    }
                }

                // Rolling 2 + 2 s window for "peak lately".
                if (Time.unscaledTime >= _winEnd)
                {
                    _winEnd = Time.unscaledTime + 2f;
                    for (int i = 0; i < Count; i++) { _winPrev[i] = _winCur[i]; _winCur[i] = 0; }
                }

                // Fold the finished frame's per-slot spans into the averages.
                for (int i = 0; i < Count; i++)
                {
                    double ms = _acc[i] * TickMs;
                    if (ms > _peak[i]) _peak[i] = ms;
                    if (ms > _winCur[i]) _winCur[i] = ms;
                    _ms[i] = _ms[i] + (ms - _ms[i]) * Smooth;
                    _kb[i] = _kb[i] + (_memAcc[i] / 1024.0 - _kb[i]) * Smooth;
                    _acc[i] = 0;
                    _memAcc[i] = 0;
                }

                // 1% low, recomputed a few times a second (a small sort).
                if (Time.unscaledTime - _lowThrottle > 0.25f)
                {
                    _lowThrottle = Time.unscaledTime;
                    _low1 = OnePercentLow();
                }
                if (Time.unscaledTime >= _nextLog)
                {
                    _nextLog = Time.unscaledTime + 5f;
                    double measured = 0;
                    int top = 0, topPeak = 0, topKb = 0;
                    for (int i = 0; i < Count; i++)
                    {
                        if (Kind[i] != 4) measured += _ms[i];
                        if (_ms[i] > _ms[top]) top = i;
                        if (WinPeak(i) > WinPeak(topPeak)) topPeak = i;
                        if (_kb[i] > _kb[topKb]) topKb = i;
                    }
                    RevivalPlugin.L.LogInfo(string.Format(
                        "FramePerf: fps={0:0.0} p99fps={1:0.0} worstMs={2:0.0} "
                        + "over50ms={3}/{4} measuredMs={5:0.000} "
                        + "top={6}:{7:0.000} peak4s={8}:{9:0.0} "
                        + "allocKB/frame={10:0.0} topAlloc={11}:{12:0.0} heapMB={13:0} "
                        + "gc0={14} fixed/frame={15:0.0} | {16} | {17}",
                        _fpsAvg, _low1, _worstMs, _slowFrames, _dtCount,
                        measured, Names[top], _ms[top], Names[topPeak], WinPeak(topPeak),
                        _allocKb, Names[topKb], _kb[topKb], _heapMb,
                        GC.CollectionCount(0) - _gcStart, _fixedAvg, RenderLine(), EnginePerf.StatusLine()));
                }
            }
            catch { /* diagnostics must never throw into the frame loop */ }
        }

        static double WinPeak(int i) { return _winCur[i] > _winPrev[i] ? _winCur[i] : _winPrev[i]; }

        /// <summary>Worst frame time in the ring as an fps figure (the 1% low).</summary>
        static float OnePercentLow()
        {
            int n = _dtCount;
            if (n < 20) return 0f;
            Array.Copy(_dt, _sortedDt, n);
            Array.Sort(_sortedDt, 0, n);              // ascending frame time
            int idx = Mathf.Clamp((int)(n * 0.99f), 0, n - 1);
            float worst = _sortedDt[idx];
            _worstMs = _sortedDt[n - 1] * 1000f;
            _slowFrames = 0;
            for (int i = 0; i < n; i++)
                if (_sortedDt[i] > 0.05f) _slowFrames++;
            return worst > 0f ? 1f / worst : 0f;
        }

        /// <summary>The GPU-relevant view settings as one line: far clip, shadow
        /// distance, LOD bias, terrain tree/billboard/grass distances.</summary>
        static string RenderLine()
        {
            try
            {
                Camera cam = CameraOwner.MainCamera();
                Terrain t = Terrain.activeTerrain;
                Terrain[] all = Terrain.activeTerrains;
                return string.Format(
                    "far {0:0} m, shadows {1:0} m x{2}, LOD bias {3:0.00}, q{4}, "
                    + "terrains {5}: trees {6:0}/{7:0} m, grass {8:0} m x{9:0.00}, pixErr {10:0}",
                    cam != null ? cam.farClipPlane : 0f, QualitySettings.shadowDistance,
                    QualitySettings.shadowCascades, QualitySettings.lodBias,
                    QualitySettings.GetQualityLevel(), all != null ? all.Length : 0,
                    t != null ? t.treeDistance : 0f, t != null ? t.treeBillboardDistance : 0f,
                    t != null ? t.detailObjectDistance : 0f, t != null ? t.detailObjectDensity : 0f,
                    t != null ? t.heightmapPixelError : 0f);
            }
            catch { return "render settings unavailable"; }
        }

        // -------------------------------------------------------------- overlay

        static Texture2D _bg;
        const int Shown = 12;
        static readonly string[] _text = new string[Shown + 13];
        static readonly Color[] _tint = new Color[Shown + 13];
        static int _lines;
        static float _textAt;

        static Texture2D Bg()
        {
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, Color.white);
                _bg.Apply();
            }
            return _bg;
        }

        static void Add(string s, Color c)
        {
            if (_lines >= _text.Length) return;
            _text[_lines] = s; _tint[_lines] = c; _lines++;
        }

        /// <summary>Rebuilds the overlay text. Four times a second, so the
        /// overlay's own strings are not what it reports as allocation.</summary>
        static void BuildText()
        {
            _lines = 0;
            double upd = 0, fix = 0, late = 0, gui = 0;
            for (int i = 0; i < Count; i++)
            {
                switch (Kind[i])
                {
                    case 1: fix += _ms[i]; break;
                    case 2: late += _ms[i]; break;
                    case 3: gui += _ms[i]; break;
                    case 4: break;
                    default: upd += _ms[i]; break;
                }
            }
            double ours = upd + fix + late + gui;
            float frameMs = _fpsAvg > 0.01 ? (float)(1000.0 / _fpsAvg) : 0f;
            float pct = frameMs > 0.01f ? (float)(ours / frameMs * 100.0) : 0f;

            // Rank the slots by average.
            int[] order = _order;
            for (int i = 0; i < Count; i++) order[i] = i;
            Array.Sort(order, CompareAvg);

            Color head = new Color(0.7f, 0.9f, 1f, 1f);
            Color plain = Color.white;
            Color soft = new Color(0.8f, 0.85f, 0.9f, 1f);
            Add(Loc.T("NDR-Messfenster (" + ToggleKey() + " = aus)",
                      "NDR frame overlay (" + ToggleKey() + " = off)"), head);
            Add(string.Format(Loc.T("FPS {0:0}   Bild {1:0.0} ms   1%-Low {2:0}",
                                    "FPS {0:0}   frame {1:0.0} ms   1% low {2:0}"),
                _fpsAvg, frameMs, _low1), plain);
            Add(string.Format(Loc.T("Gemessene Aufrufe {0:0.00} ms ({1:0}% vom Bild)",
                                    "Measured calls {0:0.00} ms ({1:0}% of frame)"), ours, pct),
                pct > 25f ? new Color(1f, 0.55f, 0.4f, 1f) : new Color(0.6f, 0.95f, 0.6f, 1f));
            Add(string.Format("  Update {0:0.00}  Fixed {1:0.00} ({2:0.0} steps)  Late {3:0.00}  GUI {4:0.00} ms",
                upd, fix, _fixedAvg, late, gui), soft);
            Add(string.Format("Last {0} frames: max {1:0.0} ms, >50 ms: {2}, GC0 since F6: {3}",
                _dtCount, _worstMs, _slowFrames, GC.CollectionCount(0) - _gcStart), plain);
            Add(string.Format("Managed alloc {0:0.0} KB/frame ({1:0} KB/s), heap {2:0} MB",
                _allocKb, _allocKb * _fpsAvg, _heapMb),
                _allocKb > 8.0 ? new Color(1f, 0.6f, 0.45f, 1f) : plain);
            Add(RenderLine(), soft);
            Add(EnginePerf.StatusLine(), soft);
            Add("Spike: " + (_lastSpike.Length > 0 ? _lastSpike : "none since F6"),
                _lastSpike.Length > 0 ? new Color(1f, 0.8f, 0.5f, 1f) : soft);
            Add("Engine/GPU are the rest of the frame. Peak = last 4 s.", soft);
            Add(Loc.T("Groesste Posten: Mittel / Spitze ms, KB/Bild",
                      "Top consumers: avg / peak ms, KB/frame"), head);
            for (int i = 0; i < Shown && i < Count; i++)
            {
                int s = order[i];
                double v = _ms[s];
                Color c = v > 1.0 ? new Color(1f, 0.6f, 0.45f, 1f)
                        : v > 0.3 ? new Color(1f, 0.9f, 0.55f, 1f)
                                  : new Color(0.72f, 0.75f, 0.8f, 1f);
                Add(string.Format("  {0,-24} {1,6:0.000} / {2,6:0.0}  {3,5:0.0}",
                    Names[s], v, WinPeak(s), _kb[s]), c);
            }

            // The spikiest slots, whatever their average.
            int a = -1, b = -1, d = -1;
            for (int i = 0; i < Count; i++)
            {
                double p = WinPeak(i);
                if (a < 0 || p > WinPeak(a)) { d = b; b = a; a = i; }
                else if (b < 0 || p > WinPeak(b)) { d = b; b = i; }
                else if (d < 0 || p > WinPeak(d)) d = i;
            }
            StringBuilder sb = new StringBuilder("Peaks: ");
            int[] top3 = new int[] { a, b, d };
            for (int k = 0; k < 3; k++)
                if (top3[k] >= 0 && WinPeak(top3[k]) > 0.05)
                    sb.Append(Names[top3[k]]).Append(' ').Append(WinPeak(top3[k]).ToString("0.0")).Append("  ");
            Add(sb.ToString(), new Color(1f, 0.9f, 0.55f, 1f));
        }

        static int CompareAvg(int x, int y) { return _ms[y].CompareTo(_ms[x]); }

        /// <summary>Called last in OnGUI. Draws nothing while off.</summary>
        public static void DrawOverlay()
        {
            if (!On) return;
            try
            {
                if (Time.unscaledTime >= _textAt)
                {
                    _textAt = Time.unscaledTime + 0.25f;
                    BuildText();
                }
                float x = 12f, y = 12f, w = 620f;
                float lh = 16f;
                float h = lh * _lines + 16f;

                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.72f);
                GUI.DrawTexture(new Rect(x, y, w, h), Bg());
                GUI.color = old;

                float tx = x + 10f, ty = y + 8f;
                for (int i = 0; i < _lines; i++)
                    Line(tx, ref ty, w, lh, _tint[i], _text[i]);
            }
            catch { /* never throw into OnGUI */ }
        }

        static void Line(float x, ref float y, float w, float lh, Color c, string text)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.Label(new Rect(x, y, w - 20f, lh + 2f), text);
            GUI.color = old;
            y += lh;
        }
    }
}
