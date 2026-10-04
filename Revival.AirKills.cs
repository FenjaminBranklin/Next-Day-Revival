// =====================================================================
// W AA4 - a kill must matter (docs/ai/tasks/w-aa4-kills-matter.md)
//
//   Hit points   an NPC aeroplane (Tu-95, paratroop An-2, test flyover)
//                keeps ONE damage ledger on the master (Revival.NpcAircraft.cs
//                Flight.Ledger): rifle rounds, flak, the Gepard and blasts
//                add up for the whole flight. The arithmetic is
//                Revival.AirKillCore.cs.
//   The run      at the release point the damage decides: whole - the
//                editor's line; damaged - the stick goes wide and scatters;
//                badly damaged - mostly aborts. Shot down before: nothing.
//   Troop Mi-8   the troop landing's helicopter is an air target of the
//                flak (GepardAir source "Mi-8 troops"): shot down, it falls,
//                the squad never gets out, the wreck burns with a hold.
//   Bounty       the master pays the player whose weapon (or merc crew)
//                brought it down: event 163 kind 7 to that actor, who books
//                it through the game's own money setter.
//   Revetments   a man inside a 52-K's sandbag ring is hurt by a blast only
//                when the bomb lands in the pit (Mortar.Sweep asks here).
//
// Wire (AirEvents.Net, code 163): kind 6 {6, view, level} damage smoke /
// 3 = aborted run; kind 7 {7, actor, amount, what} bounty; kind 8
// {8, view, x, y, z, phase} troop Mi-8: 0 hit (falling), 1 wreck on the
// ground, 2 a client's kill request to the master.
// =====================================================================
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    internal static class AirKills
    {
        internal static ConfigEntry<int> CfgBountyTu95, CfgBountyAn2, CfgBountyMi8;
        internal static ConfigEntry<bool> CfgRevetments, CfgTroopHelis, CfgSmoke;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string S = "AirKills";
            CfgBountyTu95 = cfg.Bind(S, "BountyTu95", 150000,
                "Money the host pays the player who shoots down an air event's Tu-95 (his own weapon, the "
                + "flak gun he mans, or his mercenaries at a gun). 0 = none.");
            CfgBountyAn2 = cfg.Bind(S, "BountyAn2", 60000, "The same for an air event's paratroop An-2.");
            CfgBountyMi8 = cfg.Bind(S, "BountyMi8", 80000, "The same for a troop landing's Mi-8.");
            CfgRevetments = cfg.Bind(S, "Revetments", true,
                "A man inside a 52-K's sandbag ring is hurt by a bomb or shell only when it lands in the pit.");
            CfgTroopHelis = cfg.Bind(S, "TroopHelicoptersAreTargets", true,
                "The flak engages a troop landing's Mi-8 of a hostile side; shot down, nobody gets out.");
            CfgSmoke = cfg.Bind(S, "DamageSmoke", true, "A damaged NPC aircraft trails smoke on every client.");
        }

        static bool On(ConfigEntry<bool> e) { return e == null || e.Value; }
        static bool Master() { return RevivalTroopInsertion.MasterClient(); }
        static float K { get { return PlayerAn2.K; } }

        // ============================================================ credit

        /// <summary>Set by a gun just before it fires (Revival.Flak.cs): the
        /// actor its shells are credited to. -1 = the round decides.</summary>
        internal static int NextShotCredit = -1;
        /// <summary>Set by the round loop around a hit; -1 = the local player.</summary>
        internal static int HitCredit = -1;

        /// <summary>The Photon actor the current hit is credited to (0 = an
        /// NPC crew, nobody is paid).</summary>
        internal static int Credit()
        {
            return HitCredit >= 0 ? HitCredit : Mathf.Max(0, Mercs.LocalActor);
        }

        // ============================================================ the run

        internal static ReleasePlan PlanRelease(float damage)
        {
            return AirKillCore.Plan(damage, UnityEngine.Random.value, UnityEngine.Random.value);
        }

        /// <summary>Master: a badly damaged bomber turns away at its release point.</summary>
        internal static void Aborted(GameObject go, string name, float damage)
        {
            int view = PlayerAn2.View(go);
            RevivalPlugin.L.LogInfo("AirKills: Tu-95 " + view + " (" + name + ") ABORTS its run, "
                + Mathf.RoundToInt(damage * 100f) + " % damaged - no bomb falls.");
            float[] msg = new float[] { 6f, view, 3f };
            AirEvents.Net.Send(msg, true);
            OnLevel(msg);
        }

        // ============================================================ damage

        /// <summary>Master, after a hit that did not bring it down.</summary>
        internal static void Damaged(NpcAircraft.Flight f)
        {
            int level = AirKillCore.Level(f.Ledger.Damage);
            if (level <= f.Level) return;
            f.Level = level;
            int view = PlayerAn2.View(f.Go);
            RevivalPlugin.L.LogInfo("AirKills: " + (f.Label ?? "flight") + " " + view + " damaged, "
                + Mathf.RoundToInt(f.Ledger.Damage * 100f) + " %.");
            if (view == 0) return;
            float[] msg = new float[] { 6f, view, level };
            AirEvents.Net.Send(msg, true);
            OnLevel(msg);
        }

        /// <summary>Master: the hit that brought an NPC aeroplane down.</summary>
        internal static void Downed(NpcAircraft.Flight f, Vector3 at)
        {
            string label = f.Label ?? "";
            if (label.StartsWith(AirEvents.BomberTag + RetakeRaids.NamePrefix, StringComparison.Ordinal))
                AirEvents.StopCarpet(PlayerAn2.View(f.Go));
            int what = label.StartsWith(AirEvents.BomberTag, StringComparison.Ordinal) ? 0
                : label.StartsWith(AirEvents.TransportTag, StringComparison.Ordinal) ? 1 : 3;
            Pay(f.Ledger.Winner(), what, at);
        }

        static void OnLevel(float[] f)
        {
            if (f.Length < 3) return;
            GameObject go = PlayerAn2.ByViewId((int)f[1]);
            int level = (int)f[2];
            if (level == 3)
            {
                if (go != null && Near(go.transform.position, 6000f * K))
                    Turret.Hinweis("A damaged Tu-95 turns away - it drops nothing.", 5f);
                return;
            }
            NpcAircraft.Flight flight = NpcAircraft.Find(go);
            if (flight != null && level > flight.Level) flight.Level = level;
            if (go != null) Smoke(go, level);
        }

        // ============================================================ bounty

        static readonly string[] What = { "Tu-95", "An-2", "Mi-8", "aircraft" };

        /// <summary>Master: the bounty for <paramref name="what"/> (0 Tu-95,
        /// 1 An-2, 2 troop Mi-8) to <paramref name="actor"/>.</summary>
        static void Pay(int actor, int what, Vector3 at)
        {
            int amount = AirKillCore.Bounty(what, CfgBountyTu95 == null ? 150000 : CfgBountyTu95.Value,
                CfgBountyAn2 == null ? 60000 : CfgBountyAn2.Value, CfgBountyMi8 == null ? 80000 : CfgBountyMi8.Value);
            string name = What[Mathf.Clamp(what, 0, What.Length - 1)];
            if (actor <= 0 || amount <= 0)
            {
                RevivalPlugin.L.LogInfo("AirKills: " + name + " down at " + at.ToString("0")
                    + " - no bounty (" + (actor <= 0 ? "an NPC crew's kill" : "none set") + ").");
                return;
            }
            RevivalPlugin.L.LogInfo("AirKills: " + name + " down at " + at.ToString("0") + " - bounty "
                + amount + " to player " + actor + ".");
            float[] msg = new float[] { 7f, actor, amount, what };
            if (actor == Mercs.LocalActor) OnBounty(msg, actor, true);
            else AirEvents.Net.Send(msg, true);
        }

        static void OnBounty(float[] f, int sender, bool local)
        {
            if (f.Length < 4) return;
            int actor = (int)f[1];
            if (actor <= 0 || actor != Mercs.LocalActor) return;
            // Only the master pays: a bounty from anybody else is ignored.
            if (!local && sender != MercAA.MasterActor()) return;
            int amount = Mathf.Clamp((int)f[2], 0, 10000000);
            if (amount <= 0) return;
            string name = What[Mathf.Clamp((int)f[3], 0, What.Length - 1)];
            int after = Mercs.AddMoney(amount);
            RevivalPlugin.L.LogInfo("AirKills: bounty " + amount + " for a " + name + (after < 0 ? " NOT booked." : " booked."));
            Turret.Hinweis(name + " shot down - bounty +" + Mercs.Money0(amount) + ".", 6f);
        }

        // ============================================================ troop Mi-8

        static bool _registered;
        static readonly List<GameObject> _downHelis = new List<GameObject>();

        static void Register()
        {
            if (_registered || !On(CfgTroopHelis)) return;
            _registered = true;
            GepardAir.Register("Mi-8 troops", ListHelis, KillHeli, HeliDown, 0);
        }

        static void ListHelis(List<GameObject> into) { RevivalTroopInsertion.TroopHelis(into); }

        static bool HeliDown(GameObject go) { return _downHelis.Contains(go); }

        /// <summary>A kill from GepardAir (the flak, a Stinger): the master
        /// brings it down, any other client asks the master.</summary>
        static void KillHeli(GameObject go, Vector3 at)
        {
            if (go == null || _downHelis.Contains(go)) return;
            if (Master()) { ShootDownHeli(go, at, Credit()); return; }
            int view = PlayerAn2.View(go);
            if (view != 0) AirEvents.Net.Send(new float[] { 8f, view, at.x, at.y, at.z, 2f }, true);
        }

        // Only Stinger impacts consult decoys; cannon rounds always remain live.
        internal static void StingerHit(GameObject go, Vector3 at)
        {
            if (go == null || Mi8Flares.Protects(go)) return;
            if (Master()) { KillHeli(go, at); return; }
            int view = PlayerAn2.View(go);
            if (view != 0) AirEvents.Net.Send(new float[] { 8f, view, at.x, at.y, at.z, 2f, 1f }, true);
        }

        static void ShootDownHeli(GameObject go, Vector3 at, int actor)
        {
            HeliFlight flight = RevivalTroopInsertion.FlightOf(go);
            if (flight == null || flight.Down) return;
            AircraftCrashFx.Hit(go, at);
            if (!flight.ShotDown()) return;
            int view = PlayerAn2.View(go);
            Vector3 site = AircraftCrashFx.Site(go);
            float[] msg = new float[] { 8f, view, at.x, at.y, at.z, 0f, site.x, site.y, site.z };
            AirEvents.Net.Send(msg, true);
            OnHeli(msg, go);
            Pay(actor, 2, at);
        }

        /// <summary>Master, from HeliFlight: the shot-down machine is on the ground.</summary>
        internal static void TroopHeliCrashed(GameObject go, Vector3 at)
        {
            int view = PlayerAn2.View(go);
            float[] msg = new float[] { 8f, view, at.x, at.y, at.z, 1f };
            AirEvents.Net.Send(msg, true);
            OnHeli(msg, go);
        }

        static GameObject HeliByView(int view)
        {
            _tmp.Clear();
            RevivalTroopInsertion.TroopHelis(_tmp);
            GameObject found = null;
            for (int i = 0; i < _tmp.Count && found == null; i++)
                if (PlayerAn2.View(_tmp[i]) == view) found = _tmp[i];
            _tmp.Clear();
            return found;
        }

        static readonly List<GameObject> _tmp = new List<GameObject>();

        static void OnHeli(float[] f, GameObject go)
        {
            if (f.Length < 6) return;
            if (go == null) go = HeliByView((int)f[1]);
            if (go == null) return;
            Vector3 at = new Vector3(f[2], f[3], f[4]);
            int phase = (int)f[5];
            if (phase == 0 || phase == 1) AircraftAudio.StopEngines(go);
            if (phase == 0)
            {
                if (!_downHelis.Contains(go)) _downHelis.Add(go);
                Vector3 site = f.Length >= 9 ? new Vector3(f[6], f[7], f[8]) : AircraftCrashFx.Site(go);
                AircraftCrashFx.Start(go, site, 0.7f * RevivalTroopInsertion.K);
                Unsmoke(go);
                if (Near(at, 3000f * K)) Turret.Hinweis("A troop helicopter is going down.", 4f);
                return;
            }
            if (phase != 1) return;
            if (!_downHelis.Contains(go)) _downHelis.Add(go);
            AircraftCrashFx.Stop(go);
            Unsmoke(go);
            try
            {
                HeliWreckModel.Apply(go);
                if (go.GetComponent<HeliWreckSettle>() == null)
                {
                    float floor;
                    if (!AircraftWreck.Surface(at, go.transform, out floor)) floor = at.y;
                    go.AddComponent<HeliWreckSettle>().Begin(floor);
                }
                FireEffect.SpawnHeliBlast(at + Vector3.up * (1.5f * K), 22f);
                if (!FireEffect.SpawnHeliFire(go)) FireEffect.SpawnWreck(go, false);
                HeliCrashSound.Play(at);
                HeliHold.Attach(go);
                if (Master()) StockHeli(go);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirKills: troop wreck - " + ex.Message); }
        }

        /// <summary>Master: what the squad that never got out leaves behind.</summary>
        static void StockHeli(GameObject go)
        {
            object data = Turret.TrunkDataOf(go.transform);
            if (data == null) { RevivalPlugin.L.LogWarning("AirKills: the Mi-8 wreck has no hold to stock."); return; }
            int placed = 0, n = UnityEngine.Random.Range(4, 8);
            for (int i = 0; i < n; i++)
            {
                int id = Airfield.LootItemId(i % 3 == 0 ? "military" : i % 3 == 1 ? "guard" : "medical");
                if (id > 0 && Turret.AddToContainer(data, id, 1)) placed++;
            }
            RevivalPlugin.L.LogInfo("AirKills: the Mi-8 wreck holds " + placed + " item(s).");
        }

        /// <summary>Master (Revival.Flak.cs): whose side a troop helicopter
        /// is on, or null for anything else.</summary>
        internal static string TroopSide(GameObject go)
        {
            if (!On(CfgTroopHelis) || !RevivalTroopInsertion.IsTroopHeli(go)) return null;
            return RevivalTroopInsertion.FactionOf(go);
        }

        // ============================================================ network

        internal static void OnEvent(int kind, float[] f, int sender)
        {
            if (kind == 6) OnLevel(f);
            else if (kind == 7) OnBounty(f, sender, false);
            else if (kind == 8 && f.Length >= 6)
            {
                if ((int)f[5] == 2)
                {
                    if (!Master()) return;
                    GameObject go = HeliByView((int)f[1]);
                    if (f.Length >= 7 && f[6] > 0.5f && Mi8Flares.Protects(go)) return;
                    if (go != null) ShootDownHeli(go, new Vector3(f[2], f[3], f[4]), sender);
                    return;
                }
                if (!Master() && sender == MercAA.MasterActor()) OnHeli(f, null);
            }
        }

        // ============================================================ revetments

        const int MaxPits = 8;
        static readonly Transform[] _pitHolder = new Transform[MaxPits];
        static readonly Vector3[] _pit = new Vector3[MaxPits];
        static int _pits;

        /// <summary>Revival.Flak.cs: a 52-K was built in this sandbag ring.</summary>
        internal static void AddPit(Transform holder)
        {
            if (holder == null) return;
            for (int i = 0; i < _pits; i++) if (_pitHolder[i] == holder) return;
            int slot = -1;
            for (int i = 0; i < _pits; i++) if (_pitHolder[i] == null) { slot = i; break; }
            if (slot < 0)
            {
                if (_pits >= MaxPits) return;
                slot = _pits++;
            }
            _pitHolder[slot] = holder;
            _pit[slot] = holder.position;
        }

        /// <summary>Mortar.Sweep: the man at <paramref name="man"/> is inside
        /// a gun pit and the blast at <paramref name="blast"/> is not in it.
        /// A few multiplications per pit; only men already inside the blast
        /// radius are asked.</summary>
        internal static bool Sheltered(Vector3 man, Vector3 blast)
        {
            if (_pits == 0 || !On(CfgRevetments)) return false;
            float k = K;
            for (int i = 0; i < _pits; i++)
            {
                if (_pitHolder[i] == null) continue;
                Vector3 c = _pit[i];
                if (AirKillCore.Sheltered(man.x - c.x, man.y - c.y, man.z - c.z, blast.x - c.x, blast.z - c.z, k))
                    return true;
            }
            return false;
        }

        // ============================================================ smoke

        const int MaxSmoke = 12;
        static readonly GameObject[] _smokeGo = new GameObject[MaxSmoke];
        static readonly int[] _smokeLevel = new int[MaxSmoke];
        static readonly Vector3[] _smokeLast = new Vector3[MaxSmoke];
        static int _smokes;
        static float _nextPuff, _nextPrune;

        static void Smoke(GameObject go, int level)
        {
            if (go == null || level <= 0 || !On(CfgSmoke)) return;
            for (int i = 0; i < _smokes; i++)
                if (_smokeGo[i] == go) { _smokeLevel[i] = Mathf.Max(_smokeLevel[i], level); return; }
            if (_smokes >= MaxSmoke) return;
            _smokeGo[_smokes] = go;
            _smokeLevel[_smokes] = level;
            _smokeLast[_smokes] = go.transform.position + Vector3.up * 1000f;
            _smokes++;
        }

        static void Unsmoke(GameObject go)
        {
            for (int i = _smokes - 1; i >= 0; i--)
                if (_smokeGo[i] == go) RemoveSmoke(i);
        }

        static void RemoveSmoke(int i)
        {
            _smokes--;
            _smokeGo[i] = _smokeGo[_smokes];
            _smokeLevel[i] = _smokeLevel[_smokes];
            _smokeLast[i] = _smokeLast[_smokes];
            _smokeGo[_smokes] = null;
        }

        static bool Near(Vector3 p, float reach)
        {
            Camera cam = CameraOwner.MainCamera();
            if (cam == null) return false;
            return (cam.transform.position - p).sqrMagnitude <= reach * reach;
        }

        /// <summary>
        /// Every frame, F6 slot AirKills.Tick. Idle (no damaged aircraft, no
        /// pits to prune) it is two comparisons. Smoke: 8 puffs a second per
        /// damaged aircraft within 2.5 km of the camera, through the Gepard's
        /// pooled particle systems (Emit with a struct, no allocation).
        /// </summary>
        internal static void Tick()
        {
            try
            {
                if (!_registered) Register();
                float now = Time.time;
                AircraftCrashFx.Tick(now);
                if (now >= _nextPrune)
                {
                    _nextPrune = now + 1f;
                    for (int i = _downHelis.Count - 1; i >= 0; i--)
                        if (_downHelis[i] == null) _downHelis.RemoveAt(i);
                    // A trail ends with the aircraft, or once it lies still
                    // (the wreck's own fire takes over).
                    for (int i = _smokes - 1; i >= 0; i--)
                    {
                        GameObject go = _smokeGo[i];
                        if (go == null) { RemoveSmoke(i); continue; }
                        Vector3 p = go.transform.position;
                        if ((p - _smokeLast[i]).sqrMagnitude < K * K) { RemoveSmoke(i); continue; }
                        _smokeLast[i] = p;
                    }
                }
                if (_smokes == 0 || now < _nextPuff) return;
                _nextPuff = now + 0.125f;
                Camera cam = CameraOwner.MainCamera();
                if (cam == null) return;
                Vector3 eye = cam.transform.position;
                float reach = 2500f * K;
                for (int i = 0; i < _smokes; i++)
                {
                    GameObject go = _smokeGo[i];
                    if (go == null) continue;
                    if (PlayerAn2.Down(go)) continue;
                    Transform tr = go.transform;
                    Vector3 p = tr.position;
                    if ((p - eye).sqrMagnitude > reach * reach) continue;
                    Vector3 back = -tr.forward;
                    float scale = _smokeLevel[i] >= 2 ? 9f : 5f;
                    GepardFx.Smoke(p + back * (4f * K) + Vector3.up * (1.5f * K), back, scale);
                }
            }
            catch (Exception ex)
            {
                if (++_errors <= 3) RevivalPlugin.L.LogError("AirKills: " + ex);
            }
        }

        static int _errors;
    }
}
