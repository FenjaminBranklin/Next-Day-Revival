// Next Day: Survival - Revival Toolkit
//
// W TOWER 4: RETAKE RAIDS. The east airfield (tower radar HQ + 52-K guns) is a
// strategic objective. When a side takes it from its garrison, the garrison
// strikes back on its own - not only when an admin schedules an air event:
// bombers on the tower, the guns or the fuel depot, An-2 paratroopers and Mi-8
// troop landings that retake the tower, optionally an An-2 escort that bombs
// the AA guns first. K5b2 adds close sticks, a second air wave and quiet windows.
// docs/ai/tasks/w-tower4-retake-raids.md has the design, numbers and checks.
//
// PIECES
//   Core      Revival.RetakeRaidsCore.cs (pure, csc-tested offline by
//             research/retake_raids_check.py): the editor settings, the hold
//             clock (first raid, interval/window), the raid composition (growth
//             per raid, target rotation, no new troops while old ones fight).
//   Holder    who holds the airfield (master, 1 Hz). On this base: the
//             garrison while its HQ operator (radar/c1/op) lives at the
//             console; once he is down, the side whose orders stand on the
//             guns (TowerRadar.ControlSide) if it is not the garrison's, else a
//             living player of another side on the ground within 150 m of the
//             C1 tower - sticky until the operator is back (he respawns only
//             when no player is near the console, TowerRadar). W Tower 3's
//             AirfieldHold, when merged, replaces Holder() alone.
//   Raid      master: an AirEvents.Event built in code (escort An-2 over
//             each airfield 52-K, Tu-95 stick over the chosen target, An-2
//             paratroopers dropped by the runway who attack the tower) flown
//             by AirEvents exactly like an editor event - warning, siren,
//             banner, radar log, bombs, canopies, NpcWar squads; and Mi-8
//             landings on the runway (RevivalTroopInsertion.LaunchExternal)
//             whose squads attack the tower. Every aircraft is an NpcAircraft
//             or the troop heli, so every client sees them (Photon) and every
//             AA gun, the radar, the Stinger and rifles treat them as targets.
//   Settings  the map editor's Air events -> "Retake raids" panel (airdef.py
//             "retakeRaids"), the row "#retake" of the air event table (live
//             /runtime/air or assets/ndr_airevents.tsv; an older plugin skips
//             '#' rows). No row: the defaults - ON. Host kill switch
//             [RetakeRaids] Enabled.
//   Admin     Air events rows: "Retake raid now" (a non-host admin asks the
//             host through AirEvents request kind 3, template -2) and the
//             status (holder, raid number, next raid).
//
// PERFORMANCE. Tick: one bool and one float compare per frame; the master's
// 1 Hz step reads TowerRadar's mirrored state and the clock (no allocation);
// only while the HQ operator is down and nobody holds yet, every 2 s one pass
// over the cached player list (flat distance, a faction read for the few in
// range). The raid itself (lists, strings, Flak.Guns) is built once per raid.
// F6 slot "RetakeRaids.Tick".
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    internal static class RetakeRaids
    {
        /// <summary>AirEvents request kind 3 with this template = "retake raid now".</summary>
        internal const int RequestTemplate = -2;
        internal const string NamePrefix = "NDR-retake-";
        const string HeliPrefix = "NDR-retake-heli-";
        /// <summary>Paratroop squads of a retake event: AirEvents names them
        /// "air-" + event name + ...</summary>
        const string ParaPrefix = "air-NDR-retake-";

        /// <summary>A player on the ground this close to the C1 tower holds the
        /// airfield once the HQ operator is down (150 m).</summary>
        const float PresenceRadiusU = 150f * PlayerAn2.K;
        const float PresenceMaxHeightU = 30f * PlayerAn2.K;
        const int PatrolMinutes = 25;
        const int EscortBombs = 4;
        // Seconds after the raid's start: escort first, the bombers behind it,
        // the paratroopers after the bombs, the Mi-8 last.
        const float BomberDelay = 25f, TransportDelay = 70f, HeliDelay = 75f, HeliStagger = 20f;

        /// <summary>The runway centre line (x 4578..4686, Revival.Airfield.cs):
        /// open ground next to the tower for the drop zone and the Mi-8.</summary>
        const float RunwayX = 4632f;
        static readonly float[] HeliZ = { 1150f, 800f, 1500f };

        internal static ConfigEntry<bool> CfgEnabled;

        static RetakeSettings _settings = new RetakeSettings();
        static readonly RetakeClock _clock = new RetakeClock();
        static readonly int[] _scratch = new int[3];
        static float _nextStep, _nextPresence;
        static int _serial, _sticky = -1, _errors;
        static bool _wasMaster;
        static bool _askedClock, _remoteClock;
        static readonly RetakeClock _remote = new RetakeClock();
        internal const int ClockMessage = 9, ClockRequest = 10; // AirEvents channel; 6..8 = AirKills
        static readonly float[] _clockRequest = { ClockRequest };
        static AirEvents.Event _flying;

        sealed class PendingHeli
        {
            public float At;
            public RevivalTroopInsertion.Landing Landing;
        }
        static readonly List<PendingHeli> _helis = new List<PendingHeli>();
        static readonly List<string> _heliNames = new List<string>();

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("RetakeRaids", "Enabled", true,
                "Host: the airfield's garrison strikes back when a side takes the airfield (bombers on the "
                + "tower/guns/fuel depot, paratroopers, Mi-8 landings, An-2 escort on the guns). "
                + "Interval, random window, quiet floor, strength and composition are in the map editor (Air "
                + "events -> Retake raids). Off: no automatic retake raids (the admin button still works).");
        }

        // ============================================================ settings

        /// <summary>The air event table was (re)loaded (AirEvents.Load): take
        /// its "#retake" row, or the defaults when it has none. A bad row
        /// keeps the settings in force.</summary>
        internal static void Table(string[] lines, string label)
        {
            string error;
            RetakeSettings s = RetakeSettings.FromTable(lines, out error);
            if (s == null)
            {
                RevivalPlugin.L.LogError("RetakeRaids: " + label + " retake row rejected - " + error);
                return;
            }
            _settings = s;
            RevivalPlugin.L.LogInfo("RetakeRaids: " + Describe(s) + " (" + label + ").");
        }

        static string Describe(RetakeSettings s)
        {
            return (s.Enabled ? "on" : "OFF") + ", frequency x" + s.Frequency.ToString("0.##") + ", strength x"
                + s.Strength.ToString("0.##") + ", " + s.Bombers + " Tu-95 x " + s.Bombs + " on "
                + (s.HitTower ? "T" : "") + (s.HitGuns ? "G" : "") + (s.HitFuel ? "F" : "") + ", "
                + s.Transports + " An-2 x " + s.Paratroopers + ", " + s.Helis + " Mi-8 x " + s.HeliTroops
                + ", escort " + (s.Escort ? s.Escorts.ToString() : "off") + ", side " + s.Faction
                + ", close drops " + s.CloseDrops + ", second air wave " + s.SecondWave
                + " (+" + s.SecondWaveDelaySeconds + " s), interval " + s.IntervalMinutes
                + " +0.." + s.RandomWindowMinutes + " min, quiet floor " + s.QuietMinutes + " min"                + ", approach " + s.ApproachMode + ", flight heading " + s.ApproachHeading.ToString("0.##");
        }

        // =============================================================== frame

        internal static void Tick()
        {
            if (CfgEnabled != null && !CfgEnabled.Value) return;
            float now = Time.time;
            if (now < _nextStep) return;
            _nextStep = now + 1f;
            try
            {
                bool master = Crocodile.IsMaster();
                if (!master)
                {
                    // Only the master flies raids; a later master starts its own clock.
                    if (_wasMaster) { _clock.Step(now, -1, _settings, false, false, 0); _helis.Clear(); _sticky = -1; }
                    _wasMaster = false;
                    if (!_askedClock) _askedClock = AirEvents.Net.Send(_clockRequest, true);
                    return;
                }
                _wasMaster = true;
                _askedClock = false;
                TickHelis(now);
                int before = _clock.Holder;
                float beforeWindow = _clock.Earliest;
                bool beforeClear = _clock.AwaitClear;
                int holder = TowerRadar.On && TowerRadar.Built ? Holder(now) : -1;
                bool busy = AirBusy();
                bool due = _clock.Step(now, holder, _settings, true, busy, UnityEngine.Random.Range(0, int.MaxValue));
                if (_clock.Holder != before) HolderChanged(before, now);
                if (due)
                {
                    RevivalPlugin.L.LogInfo("RetakeRaids: raid " + (_clock.Level + 1) + " - " + Fly(now, false));
                    _clock.Flown(now, _settings);
                    RevivalPlugin.L.LogInfo("RetakeRaids: next quiet window starts when this air operation clears; "
                        + _settings.QuietMinutes + " min minimum.");
                }
                if (due || before != _clock.Holder || beforeWindow != _clock.Earliest || beforeClear != _clock.AwaitClear)
                    PublishClock();
                _errors = 0;
            }
            catch (Exception ex)
            {
                if (++_errors <= 3) RevivalPlugin.L.LogError("RetakeRaids: " + ex);
            }
        }

        static void HolderChanged(int before, float now)
        {
            if (_clock.Holder < 0)
            {
                RevivalPlugin.L.LogInfo("RetakeRaids: the garrison holds the airfield again - retake raids stop.");
                try { RadarScope.Note("GARRISON back at the HQ - retake raids stand down."); } catch { }
                return;
            }
            string who = TowerRadar.SideLabel(_clock.Holder);
            RevivalPlugin.L.LogInfo("RetakeRaids: " + who + " hold the airfield (was "
                + (before < 0 ? "the garrison" : TowerRadar.SideLabel(before)) + ") - first retake raid in "
                + Mathf.RoundToInt((_clock.Next - now) / 60f) + " min" + (_settings.Enabled ? "." : " (off in the editor)."));
            try { RadarScope.Note("AIRFIELD HELD BY " + who + " - expect the garrison to strike back."); } catch { }
        }

        // ============================================================== holder

        /// <summary>
        /// Who holds the airfield: -1 its garrison, else the game's Fraction
        /// value of the holding side (TowerRadar.UnreadSide for a player whose
        /// faction cannot be read). The one function W Tower 3's AirfieldHold
        /// replaces when it is merged.
        /// </summary>
        static int Holder(float now)
        {
            // W Tower 3 merged: AirfieldHold decides who holds it (garrison = -1).
            HoldState h = AirfieldHold.State;
            return h != null && h.ByPlayers && h.Holder >= 0 ? h.Holder : -1;
        }

        // ================================================================ raid

        static bool TroopsBusy()
        {
            if (NpcWar.ActiveWithPrefix(ParaPrefix) > 0 || NpcWar.ActiveWithPrefix(HeliPrefix) > 0) return true;
            for (int i = 0; i < _heliNames.Count; i++)
                if (RevivalTroopInsertion.Flying(_heliNames[i])) return true;
            return false;
        }

        static bool AirBusy()
        {
            if (AirEvents.Flying(_flying) || _helis.Count > 0) return true;
            for (int i = 0; i < _heliNames.Count; i++)
                if (RevivalTroopInsertion.Flying(_heliNames[i])) return true;
            return false;
        }

        // Transition-only reliable snapshots, plus one request when a client joins.
        // Relative time is conservative by the network transit time on receivers.
        internal static void PublishClock()
        {
            if (!Crocodile.IsMaster()) return;
            float now = Time.time;
            float[] msg = new float[] { ClockMessage, _clock.Holder, _clock.Level,
                !_settings.Enabled || (CfgEnabled != null && !CfgEnabled.Value) || _clock.Next < 0f
                    ? -1f : _clock.AwaitClear ? 1f : 0f,
                Mathf.Max(0f, _clock.Earliest - now), Mathf.Max(0f, _clock.Latest - now) };
            AirEvents.Net.Send(msg, true);
            ReceiveClock(msg);
        }

        internal static void ReceiveClock(float[] f)
        {
            if (f == null || f.Length != 6) return;
            for (int i = 1; i < 6; i++) if (float.IsNaN(f[i]) || float.IsInfinity(f[i])) return;
            _remote.Holder = (int)f[1]; _remote.Level = (int)f[2];
            _remote.AwaitClear = f[3] > 0f;
            _remote.Next = f[3] < 0f ? -1f : 0f;
            _remote.Earliest = Time.time + Mathf.Max(0f, f[4]);
            _remote.Latest = Time.time + Mathf.Max(0f, f[5]);
            _remoteClock = true;
            if (_remote.Holder >= 0 && _remote.Next >= 0f)
                RadarScope.Note(_remote.AwaitClear ? "RETAKE: air active; quiet window follows."
                    : "RETAKE: quiet at least " + Mathf.CeilToInt(f[4] / 60f)
                        + " min, next raid window ends in " + Mathf.CeilToInt(f[5] / 60f) + " min.");
        }

        /// <summary>Master: compose and launch the raid for the clock's level.</summary>
        static string Fly(float now, bool quick)
        {
            RetakeRaid plan = RetakePlan.Compose(_settings, _clock.Level, _clock.Start, TroopsBusy(), _scratch);
            string faction = _settings.Faction == "auto" ? Airfield.Faction() : _settings.Faction;
            int serial = ++_serial;
            Vector3 target;
            AirEvents.Event e = Build(plan, faction, serial, out target);
            string result = "";
            if (e != null)
            {
                result = AirEvents.Launch(e, quick);
                _flying = e;
            }
            _heliNames.Clear();
            for (int k = 0; k < plan.Helis; k++)
            {
                RevivalTroopInsertion.Landing d = new RevivalTroopInsertion.Landing();
                d.Name = HeliPrefix + serial + "-" + k;
                d.Enabled = true;
                d.X = RunwayX; d.Z = HeliZ[k % HeliZ.Length];
                d.TailX = d.X; d.TailZ = d.Z;
                d.HeadX = TowerRadar.TowerSpot.x; d.HeadZ = TowerRadar.TowerSpot.y;
                d.Faction = faction;
                d.Count = plan.HeliTroops;
                d.PatrolMinutes = PatrolMinutes;
                PendingHeli h = new PendingHeli();
                // Do not let a short heli route arrive before the AA lead from a far edge.
                h.At = now + (e != null ? e.ArrivalLead : quick ? AirEvents.QuickWarnSeconds : AirEvents.WarnSeconds)
                    + HeliDelay + k * HeliStagger;
                h.Landing = d;
                _helis.Add(h);
                _heliNames.Add(d.Name);
            }
            if (e == null && plan.Helis > 0)
            {
                // From behind the landing's attack arrow, as TroopInsertion flies it.
                float from = AirPicturePolicy.Bearing(RunwayX - TowerRadar.TowerSpot.x,
                    HeliZ[0] - TowerRadar.TowerSpot.y);
                AirEvents.WarnRetakeHelis(new Vector3(RunwayX, 0f, HeliZ[0]), from,
                    plan.Helis, (quick ? AirEvents.QuickWarnSeconds : 180f) + HeliDelay);
            }
            string summary = RetakePlan.TargetNames[plan.Target] + " (" + target.x.ToString("0") + ", "
                + target.z.ToString("0") + "): " + plan.Escorts + " escort An-2, " + plan.Bombers + " Tu-95 x "
                + plan.Bombs + ", " + plan.Transports + " An-2 x " + plan.Paratroopers + ", " + plan.Helis
                + " Mi-8 x " + plan.HeliTroops + ", side " + faction
                + (plan.TroopsHeld ? " (no new troops: the last ones still fight)" : "");
            RevivalPlugin.L.LogInfo("RetakeRaids: " + NamePrefix + serial + " level " + plan.Level + " on " + summary);
            return result.Length > 0 ? result : "Retake raid: " + summary;
        }

        /// <summary>The raid's air event: escort, bombers, transports. Null
        /// when it has no aircraft (helis only).</summary>
        static AirEvents.Event Build(RetakeRaid plan, string faction, int serial, out Vector3 target)
        {
            Vector3 tower = new Vector3(TowerRadar.TowerSpot.x, 0f, TowerRadar.TowerSpot.y);
            List<Vector3> guns = AirfieldGuns();
            float heading = RetakeTactics.Heading(_settings, serial);
            float length = 350f, width = 80f;
            target = new Vector3((TowerRadar.TowerSpot.x + TowerRadar.MastSpot.x) * 0.5f, 0f,
                                 (TowerRadar.TowerSpot.y + TowerRadar.MastSpot.y) * 0.5f);
            if (plan.Target == RetakePlan.TargetGuns && guns.Count > 0)
            {
                // Aim at one position; do not deliberately join two guns with a carpet.
                target = guns[serial % guns.Count];
                length = 300f;
            }
            else if (plan.Target == RetakePlan.TargetFuel)
            {
                target = FuelDepot.Centre;
                length = 300f; width = 100f;
            }

            AirEvents.Event e = new AirEvents.Event();
            e.Name = NamePrefix + serial;
            e.WarningLeadSeconds = 180f;
            e.Enabled = true;
            e.Coordinated = true;
            e.SpeedFactor = AARaidBalanceCore.RaidSpeedFactor;
            e.X = target.x; e.Z = target.z;
            e.Heading = heading;
            e.Length = length; e.Width = width;
            e.Edge = "auto";
            // Legacy event arrow stays the tower; raid sticks override it below.
            e.DropX = RunwayX; e.DropZ = tower.z;
            e.AttackX = tower.x; e.AttackZ = tower.z;
            e.Faction = faction;
            e.PatrolMinutes = PatrolMinutes;
            if (plan.Escorts > 0)
            {
                AirEvents.Wave w = new AirEvents.Wave();
                w.Escort = true;
                w.Count = plan.Escorts;
                w.Load = EscortBombs;
                // No 52-K found (guns off): the escort goes for the raid's target.
                w.Aims = guns.Count > 0 ? guns.ToArray() : new Vector3[] { target };
                e.Waves.Add(w);
            }
            if (plan.Bombers > 0)
            {
                AirEvents.Wave w = new AirEvents.Wave();
                w.Bomber = true;
                w.Count = plan.Bombers;
                w.Delay = plan.Escorts > 0 ? BomberDelay : 0f;
                w.Load = plan.Bombs;
                e.Waves.Add(w);
            }
            if (plan.Transports > 0)
            {
                Vector3 dropCentre = _settings.CloseDrops ? CloseDropCentre(tower) : tower;
                int zones = RetakeTactics.Zones(plan.Transports, _settings.DropZones);
                for (int k = 0; k < plan.Transports; k++)
                {
                    int zone = RetakeTactics.Zone(k, zones);
                    float bearing = RetakeTactics.Bearing(heading, zone, zones);
                    float rad = bearing * Mathf.Deg2Rad;
                    Vector3 outward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
                    AirEvents.Wave w = new AirEvents.Wave();
                    w.Count = 1;
                    w.Delay = TransportDelay + k * 3f;
                    w.Load = plan.Paratroopers;
                    w.OwnDrop = true;
                    w.CloseDrop = _settings.CloseDrops;
                    // Inward flight; the canopy's forward throw is corrected at launch.
                    w.Direction = Mathf.Repeat(bearing + 180f, 360f);
                    w.Drop = dropCentre + outward * ((_settings.CloseDrops ? RetakeTactics.CloseRadiusM
                        : _settings.DropRadiusM) * PlayerAn2.K + (k / zones) * 30f);
                    w.Attack = guns.Count > 0 && RetakeTactics.AttackCrew(k, plan.Transports, _settings.CrewAttackPercent)
                        ? guns[(serial + k) % guns.Count] : tower;
                    e.Waves.Add(w);
                }
            }
            // A second air strike during the ground fight, never another troop batch.
            // Reuse the raid's selected target; no silent change to bomb/fire choices.
            if (_settings.SecondWave && _settings.Bombers > 0 && plan.Bombers > 0)
            {
                AirEvents.Wave w = new AirEvents.Wave();
                w.Bomber = true; w.Count = plan.Bombers; w.Load = plan.Bombs;
                w.Delay = TransportDelay + _settings.SecondWaveDelaySeconds;
                e.Waves.Add(w);
            }
            return e.Waves.Count > 0 ? e : null;
        }

        // Nearest live airfield ZU, so future layout changes move close DZs too.
        // No AA immunity or fixed world coordinates; absent ZU falls back to C1.
        static Vector3 CloseDropCentre(Vector3 tower)
        {
            Vector3 centre = tower;
            float best = float.MaxValue;
            List<FlakGunInfo> guns = Flak.Guns();
            for (int i = 0; i < guns.Count; i++)
            {
                FlakGunInfo g = guns[i];
                if (g.Health <= 0f || g.Id == null || !g.Id.StartsWith("ZU-", StringComparison.Ordinal)) continue;
                float x = g.Position.x - tower.x, z = g.Position.z - tower.z;
                float dist = x * x + z * z;
                if (dist >= best) continue;
                best = dist; centre = new Vector3(g.Position.x, 0f, g.Position.z);
            }
            return centre;
        }

        static List<Vector3> AirfieldGuns()
        {
            List<Vector3> list = new List<Vector3>();
            try
            {
                List<FlakGunInfo> guns = Flak.Guns();
                for (int i = 0; i < guns.Count; i++)
                    if (guns[i].Health > 0f && guns[i].Id != null
                        && (guns[i].Id.StartsWith("AA-", StringComparison.Ordinal)
                            || guns[i].Id.StartsWith("ZU-", StringComparison.Ordinal)))
                        list.Add(new Vector3(guns[i].Position.x, 0f, guns[i].Position.z));
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("RetakeRaids: gun list - " + ex.Message); }
            return list;
        }

        static void TickHelis(float now)
        {
            for (int i = _helis.Count - 1; i >= 0; i--)
            {
                PendingHeli h = _helis[i];
                if (now < h.At) continue;
                _helis.RemoveAt(i);
                bool ok = RevivalTroopInsertion.LaunchExternal(h.Landing);
                RevivalPlugin.L.LogInfo("RetakeRaids: Mi-8 " + h.Landing.Name + (ok ? " inbound to the runway, "
                    + h.Landing.Count + " " + h.Landing.Faction + " to retake the tower." : " could not start (troop landings off?)."));
            }
        }

        // =============================================================== admin

        /// <summary>The admin's "Retake raid now": the master flies one at the
        /// clock's level and restarts its quiet window, anyone else asks.</summary>
        internal static string Ask(bool quick)
        {
            if (Crocodile.IsMaster()) return Now(quick);
            if (!AirEvents.Net.Send(new float[] { 3f, 0f, 0f, 0f, RequestTemplate, quick ? 1f : 0f }, true))
                return "Retake raid: the network channel is not up - see the log.";
            return "Retake raid asked of the host.";
        }

        internal static string Now(bool quick)
        {
            if (!Crocodile.IsMaster()) return "Retake raid: only the host can launch.";
            if (!TowerRadar.On || !TowerRadar.Built) return "Retake raid: the east airfield is not loaded here.";
            if (AirBusy()) return "Retake raid: the previous air operation is still active.";
            string result = Fly(Time.time, quick);
            if (_clock.Holder >= 0) _clock.Flown(Time.time, _settings);
            PublishClock();
            return result;
        }

        /// <summary>One line for the admin panel.</summary>
        internal static string Status()
        {
            if (CfgEnabled != null && !CfgEnabled.Value) return "Retake raids: off ([RetakeRaids] Enabled).";
            if (!_wasMaster && !_remoteClock) return "Retake raids: waiting for host clock.";
            if (!_settings.Enabled) return "Retake raids: off in the editor.";
            RetakeClock c = _wasMaster ? _clock : _remote;
            if (c.Holder < 0) return "Retake raids: the garrison holds the airfield.";
            if (c.Next < 0f) return "Retake raids: off at the host.";
            float now = Time.time;
            if (c.AwaitClear) return "Retake raids: air operation active; quiet countdown starts after it clears.";
            return "Retake raids: " + TowerRadar.SideLabel(c.Holder) + " hold it, " + c.Level + " raid(s), quiet for "
                + Mathf.Max(0, Mathf.CeilToInt((c.Earliest - now) / 60f)) + " min, raid window ends in "
                + Mathf.Max(0, Mathf.CeilToInt((c.Latest - now) / 60f)) + " min";
        }
    }
}
