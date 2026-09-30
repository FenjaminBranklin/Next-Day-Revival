// M2 mercenaries, the fight loop - the game side of MercBrain
// (Revival.MercFightCore.cs, docs/ai/tasks/m2-merc-peek-fight.md).
//
//   WHERE     RunGround (Revival.NpcCombat.cs) hands every merc on foot to
//             NpcWar.MercFight after his target scan and his M1 sense tick.
//             While MercBrain says None (no fight, or his order does not let
//             him fight where he is) his order runs as before (MercStep).
//   IN        the brain's view, filled from NpcWar and the sense: threats and
//             exposure (MercSense), the M1 pick (FindCover's samples only
//             while the service has none), line of fire and planted feet
//             (Acquire, Planted), health, the magazine (_bulletsCount of the
//             NPC weapon), the game's reload, hits (Mercs.DamagePrefix) and
//             blasts / live grenades near him (MercDanger).
//   OUT       Run: a sprint (MainRun, 15 % over his run speed). Step: the
//             short run to a peek spot and back. Hold low: crouched, facing
//             the threat. Fire: the NpcWar planted shot (Fire), standing - the
//             game has no crouched or prone aiming clip. Reload: the game's
//             own reload (StartReload) while he crouches in cover. Heal: a
//             field dressing, HealAmount of his full health (Mercs.Dress).
//   NETWORK   nothing new: moves, poses and shots go through the NPC's own
//             SetStateWithAnimAndSync, NavMeshAgent and weapon replication,
//             like every NpcWar order. Health is the owner's (ApplyDamage is
//             isMine only); the roster report carries it to the server, which
//             accepts +0.1 a report (a dressing is 0.10, 20 s apart).
//   COST      per merc in a fight: one Think every 0.1 s (arithmetic, at most
//             two rays when a peek starts), the per-frame part is NpcWar's
//             own (Drive / Fire / aim). Move orders are re-issued only when
//             the goal changes (at most four a second). A fallback FindCover
//             search (16 rays) at most every 2 s per merc and one a quarter
//             second for all, only while the service has no pick. The danger
//             feed is one GetComponent per network spawn while mercs exist.
//             Nothing here allocates per frame or per Think.
//
// M3 (docs/ai/tasks/m3-merc-tactics.md): every merc of this client posts to
// one team board (MercTeam.Board, Revival.MercSquadCore.cs) and gets his
// grade (MercUnit.Grade) and the owner. What a merc fights, his mates are told
// (MercTeam.Share notes it into their sense twice a second). No round leaves
// while the owner or another merc is near the line of fire: the brain's
// NoShot, the live check below (MercFriendInLine) every frame he is up, and
// NpcWar.Shoot for every round at an NPC. Covering fire (Suppress) is the
// NPC's own shot at the threat's last spot while it is out of sight; an
// NpcWar fighter there feels it as suppression. Cost per merc: the board
// is a scan of at most 8 rows per Think; the live line check is arithmetic
// over the owner and the other mercs (no ray) while he fires.
//
// merc-combat-response (docs/ai/tasks/merc-combat-response.md): the fire
// decision of a merc who is up is MercFireGate's (Revival.MercCombatResponseCore.cs,
// run unchanged by research/merc_combat_response_check.py) - the live friendly
// check right before it every frame, NpcWar.Shoot's for every round. A
// player target's round is the game's own: when the gate holds while the NPC
// is in Shooting, the state change to Aiming is not left to wait for the
// 0.28 s state throttle. The brain is told the owner's held aim and whether
// he may step out of lines of fire (not deserting, boarding or on a gun or
// radar post). Each merc keeps a reaction trace (MercReaction: contact ->
// decision -> first real round, what held a late one) for F8 and Debug.
//
// Mercenaries only: normal NPCs, squads and defenders never reach this file.
// Units: game units (NpcWar SCALE note, ~2.8 per metre). C# 3.0, ASCII only.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    /// <summary>One merc's fight state on his owner's client: the brain and
    /// what the adapter needs between two Thinks. Made once per merc.</summary>
    internal sealed class MercFight
    {
        internal MercBrain Brain;
        internal FightOut Out;
        internal FightIn In;
        internal byte LastAct;
        internal Vector3 MoveTo;
        internal float NextMove, NextSpeed;
        internal bool Sprinting;
        internal float Health = 1f, NextHealth;
        internal Component Weapon;
        internal float NextWeapon;
        internal CoverPick Fallback;         // FindCover's spot while the service has none
        internal float FallbackAt;
        internal int Hits;                   // hits taken (Mercs.DamagePrefix)
        internal float LastTick;
        // M3: what he fights, for his mates' call-outs (MercTeam.Share).
        internal Transform Target;
        internal bool TargetIsPlayer, Sees;
        internal Vector3 At;
        // merc-combat-response: the reaction trace and the last fire decision.
        internal readonly MercReaction React = new MercReaction();
        internal byte Gate;
        internal float UnseenSince;
        internal Transform TraceTarget;

        internal byte State { get { return Brain == null ? MercBrain.Off : Brain.State; } }
        internal bool Holding { get { return Brain != null && Brain.Holding; } }
    }

    /// <summary>M3: the team of this client's mercs (all of them belong to
    /// the local player): the board their brains post to, and the call-outs.</summary>
    internal static class MercTeam
    {
        internal static readonly MercSquad Board = new MercSquad();
        internal static int Shared;

        /// <summary>His mates' targets they see now become his threats too
        /// (MercSense.Note): he takes cover against them and peeks at them,
        /// and a call-out brings him into the fight. Twice a second per merc.</summary>
        internal static void Share(MercUnit u, Vector3 me, float now)
        {
            List<MercUnit> units = MercCoverService.Units;
            for (int i = 0; i < units.Count; i++)
            {
                MercUnit v = units[i];
                if (v == u) continue;
                MercFight ft = v.Fight;
                if (ft.Brain == null || !ft.Brain.Fighting || !ft.Sees || ft.Target == null || !ft.Target) continue;
                Vector3 d = ft.At - me;
                d.y = 0f;
                if (d.sqrMagnitude > MercSquad.CallReach * MercSquad.CallReach) continue;
                u.Sense.Note(ft.Target, ft.TargetIsPlayer, now);
                Shared++;
            }
        }

        internal static void Drop(MercUnit u) { if (u != null) Board.Drop(u.Id); }
    }

    /// <summary>Blasts and live grenades, for mercs to get away from. Fed by
    /// Harmony postfixes on the owner's client; costs nothing without mercs.</summary>
    internal static class MercDanger
    {
        const int Blasts = 16, Live = 8;
        const float BlastKeep = 1.5f;        // a blast beside his cover counts this long
        const float LiveKeep = 8f;           // a grenade is watched this long at most
        static readonly Vector3[] _blastAt = new Vector3[Blasts];
        static readonly float[] _blastTime = new float[Blasts];
        static int _nextBlast;
        static readonly Transform[] _live = new Transform[Live];
        static readonly float[] _liveSince = new float[Live];
        static Type _explType;
        internal static int Seen, Grenades;

        internal static void Install(Harmony harmony)
        {
            try
            {
                _explType = RevivalPlugin.TypeByName("ExplosionObject");
                int n = 0;
                if (_explType != null)
                {
                    MethodInfo post = typeof(MercDanger).GetMethod("BlastPostfix", BindingFlags.Static | BindingFlags.Public);
                    foreach (string name in new string[] { "Explode", "NetworkVisualizeExplode" })
                    {
                        MethodInfo m = AccessTools.DeclaredMethod(_explType, name, null, null);
                        if (m == null) continue;
                        harmony.Patch(m, null, new HarmonyMethod(post), null, null, null);
                        n++;
                    }
                }
                // A grenade is a network object with an ExplosionObject: every
                // client (the thrower too) builds it in NetworkingPeer.DoInstantiate.
                bool spawn = false;
                Type peer = RevivalPlugin.TypeByName("NetworkingPeer");
                if (peer != null && _explType != null)
                {
                    MethodInfo[] ms = peer.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int i = 0; i < ms.Length; i++)
                    {
                        if (ms[i].Name != "DoInstantiate" || ms[i].ReturnType != typeof(GameObject)
                            || ms[i].GetParameters().Length != 3) continue;
                        harmony.Patch(ms[i], null, new HarmonyMethod(typeof(MercDanger).GetMethod("SpawnPostfix")),
                            null, null, null);
                        spawn = true;
                        break;
                    }
                }
                RevivalPlugin.L.LogInfo("Mercs: danger feed - " + n + " blast hook(s), grenade spawn hook " + spawn + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mercs: danger feed not installed - " + ex.Message);
            }
        }

        /// <summary>Postfix on ExplosionObject.Explode (its owner) and
        /// NetworkVisualizeExplode (everyone else).</summary>
        public static void BlastPostfix(object __instance)
        {
            if (!Mercs.Any) return;
            Component c = __instance as Component;
            if (c == null) return;
            _blastAt[_nextBlast] = c.transform.position;
            _blastTime[_nextBlast] = Time.time;
            _nextBlast = (_nextBlast + 1) % Blasts;
            Seen++;
        }

        /// <summary>Postfix on NetworkingPeer.DoInstantiate: a thrown grenade
        /// (or rocket) is watched until it is gone.</summary>
        public static void SpawnPostfix(GameObject __result)
        {
            if (__result == null || _explType == null || !Mercs.Any) return;
            if (__result.GetComponent(_explType) == null) return;
            float now = Time.time;
            int slot = 0;
            for (int i = 0; i < Live; i++)
            {
                if (_live[i] == null || now - _liveSince[i] > LiveKeep) { slot = i; break; }
                if (_liveSince[i] < _liveSince[slot]) slot = i;
            }
            _live[slot] = __result.transform;
            _liveSince[slot] = now;
            Grenades++;
        }

        /// <summary>The nearest danger within radius of p: a grenade in the
        /// air or on the ground, or a blast in the last BlastKeep seconds.</summary>
        internal static bool Near(Vector3 p, float radius, float now, out Vector3 at)
        {
            at = Vector3.zero;
            float best = radius * radius;
            bool found = false;
            for (int i = 0; i < Live; i++)
            {
                Transform t = _live[i];
                if (t == null) continue;
                if (!t || now - _liveSince[i] > LiveKeep) { _live[i] = null; continue; }
                Vector3 d = t.position - p;
                d.y = 0f;
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; at = t.position; found = true; }
            }
            for (int i = 0; i < Blasts; i++)
            {
                if (_blastTime[i] <= 0f || now - _blastTime[i] > BlastKeep) continue;
                Vector3 d = _blastAt[i] - p;
                d.y = 0f;
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; at = _blastAt[i]; found = true; }
            }
            return found;
        }
    }

    public static partial class NpcWar
    {
        static FieldInfo _fBullets, _fBulletsMax;
        static Type _bulletsType;
        static float _fallbackAt;

        /// <summary>M2: a merc on foot fights like a player (the file head).
        /// False: no fight - his order runs (MercStep).</summary>
        static bool MercFight(Fighter f, MercUnit u, float now)
        {
            if (f.Manpads != null) return MercStingerFight(f, u, now);
            MercFight ft = u.Fight;
            if (ft.Brain == null || ft.Brain.Id != u.Id) ft.Brain = new MercBrain(u.Id);
            MercBrain b = ft.Brain;
            b.Squad = MercTeam.Board;
            b.Grade = u.Grade;
            float dt = ft.LastTick > 0f ? Mathf.Min(now - ft.LastTick, 0.25f) : 0f;
            // Not stepped for a while (a seat, a warp): the old fight is gone.
            if (b.Fighting && now - ft.LastTick > 1f) { b.Leave(MercCoverService.Field); ft.Out = new FightOut(); }
            ft.LastTick = now;
            // NpcWar's suppression is only raised by shooters this client
            // runs; it wears off as it does for a defender.
            f.Suppression = Mathf.Max(0f, f.Suppression - dt * 0.28f);

            if (b.Due(now))
            {
                MercFightIn(f, u, ft, now);
                FightOut o;
                b.Think(ref ft.In, MercCoverService.Field, out o);
                if (o.Repick)
                {
                    MercSense s = u.Sense;
                    s.NextSense = 0f; s.NextPick = 0f; s.PickAt = -1000f;
                    ft.FallbackAt = 0f;
                }
                if (o.Kick) StartReload(f);
                if (o.HealNow) Mercs.Dress(u, MercBrain.HealAmount);
                // Once rallied safely beside the living owner, the chosen
                // FOLLOW/VEHICLE order resumes (including boarding at a stop).
                if (u.Rally && b.Down && b.Cover.Found && !b.SurvivalFire &&
                    ft.In.HasOwner && Flat(ft.In.Me - ft.In.Owner) <= 22f)
                {
                    u.Rally = false;
                    b.Leave(MercCoverService.Field);
                    o = new FightOut();
                    u.NextOrder = 0f;
                }
                ft.Out = o;
                MercNotice(f, u, ft, now);           // W: the owner's toasts (Revival.MercNotify.cs)
            }
            FightOut act = ft.Out;
            MercTrace(f, ft, act, now);
            if (act.Act == FightAct.None)
            {
                if (ft.LastAct != FightAct.None) MercFightEnd(f, ft);
                ft.LastAct = FightAct.None;
                // The game's reload holds him as it did before M2.
                if (Reloading(f)) { Quiet(f, true); return true; }
                return false;
            }
            Vector3 me = f.Tr.position;
            switch (act.Act)
            {
                case FightAct.Run:
                case FightAct.Step:
                    f.Crouched = false;
                    f.InCover = false;
                    f.MuzzleBlockedSince = 0f;           // another spot: the barrel is tried again
                    MercRun(f, u, ft, act.Dest, act.Act == FightAct.Run, now);
                    break;
                case FightAct.Fire:
                {
                    f.Crouched = false;
                    f.InCover = false;
                    // Up: look for the target at once, not at the next line check.
                    if (ft.LastAct != FightAct.Fire) f.NextLos = now;
                    bool target = f.Target != null && f.Target;
                    // M3: never through the owner or a mate - the brain's
                    // view (NoShot) and the live bodies, every frame.
                    bool friend = MercFireGate.NeedFriendCheck(target, act.NoShot, ft.In.Survive, b.SurvivalFire)
                        && MercFriendInLine(f, me, f.Target.position);
                    byte gate = MercFireGate.Decide(target, f.Sees, act.NoShot, ft.In.Survive, b.SurvivalFire, friend,
                        !ft.In.Survive && act.Suppress);
                    ft.Gate = gate;
                    if (gate == MercFireGate.Shoot)
                    {
                        int shots = f.Squad.Shots;
                        Fire(f, now);
                        ft.React.Decided(now);
                        // A round at an NPC is NpcWar.Shoot's; at a player the game's own, in Shooting.
                        if (f.Squad.Shots != shots || (f.TargetIsPlayer && IntField(f.Ai, _fAddState, -1) == AddFire))
                        {
                            bool first = ft.React.Open;
                            ft.React.Fired(now);
                            if (first && CfgDebug.Value) MercTraceLog(u, ft.React);
                        }
                        else ft.React.Held((f.PlantedSince <= 0f || now - f.PlantedSince < PlantSeconds ? MercReaction.Planting : 0)
                            | (f.MuzzleBlockedSince > 0f ? MercReaction.Muzzle : 0) | (Reloading(f) ? MercReaction.Reload : 0));
                    }
                    else if (gate == MercFireGate.Suppress && MercSuppress(f, act.Face, now)) { }
                    else
                    {
                        if (gate == MercFireGate.HoldFriend) ft.React.Held(MercReaction.Friend);
                        else if (gate == MercFireGate.HoldUnseen) ft.React.Held(MercReaction.Unseen);
                        f.Stance = Stance.Hold;
                        f.HasOrder = false;
                        // The game fires at a player target by itself while it is
                        // in Shooting: out of it now, not after the state throttle.
                        if (f.TargetIsPlayer && IntField(f.Ai, _fAddState, -1) == AddFire) f.NextState = 0f;
                        Drive(f, MainIdle, AddAim, PoseStand, now, true);
                        if (f.Target != null && f.Target) Face(f); else FaceDir(f, act.Face - me);
                    }
                    break;
                }
                default:
                    // Hold (low or not), reload, heal: still, facing the threat.
                    f.Crouched = act.Low;
                    f.InCover = b.Down;
                    if (act.Low) MercCrouch(f, now);
                    else Hold(f, null, now);
                    FaceDir(f, act.Face - me);
                    break;
            }
            ft.LastAct = act.Act;
            return true;
        }

        /// <summary>The brain's view this Think (no allocation).</summary>
        static void MercFightIn(Fighter f, MercUnit u, MercFight ft, float now)
        {
            MercSense s = u.Sense;
            ft.In.Now = now;
            ft.In.Me = f.Tr.position;
            ft.In.Count = s.Count;
            ft.In.Threats = s.At;
            ft.In.Exposed = s.Exposed;
            ft.In.SensedAt = s.ExposedAt;
            bool target = f.Target != null && f.Target;
            ft.In.Target = target;
            // merc-combat-response: eyes over a wall the barrel does not clear
            // (NpcWar.Shoot, 0.8 s as for a squad man) is no line of fire -
            // the brain moves on instead of standing there aiming.
            ft.In.Sees = target && f.Sees && !(f.MuzzleBlockedSince > 0f && now - f.MuzzleBlockedSince > 0.8f);
            ft.In.LastSeen = f.LastSeen;
            ft.In.Planted = f.PlantedSince > 0f && now - f.PlantedSince >= PlantSeconds;
            if (now >= ft.NextHealth) { ft.NextHealth = now + 0.5f; ft.Health = HealthFraction(f); }
            ft.In.Health = ft.Health;
            MercRounds(f, ft, now, out ft.In.Rounds, out ft.In.MaxRounds);
            ft.In.Reloading = Reloading(f);
            ft.In.Hits = ft.Hits;
            ft.In.Suppression = f.Suppression;
            ft.In.Danger = MercDanger.Near(ft.In.Me, MercBrain.DangerRadius, now, out ft.In.DangerAt);
            ft.In.Survive = !u.Deserting && (u.Order.Survive || u.Rally);
            ft.In.Rally = u.Rally;
            ft.In.Watch = u.Approach;
            ft.In.MayFight = MercMayEngage(u, now) && MercMayStand(f, u);
            if (ft.In.Survive) ft.In.MayFight = true;
            // A perimeter guard gives up the cover sooner: his B3b pursuit
            // takes over a target that went out of sight.
            // merc-attack-orders: an attacker too - the advance picks up sooner.
            ft.In.Disengage = u.Order.Mode == MercOrder.Perimeter || u.Order.Mode == MercOrder.Attack ? 6f : 8f;
            ft.In.Pick = s.Pick;
            ft.In.PickFresh = s.Pick.Found && s.Count > 0 && s.PickFor == s.Who[0] && now - s.PickAt < 5f;
            if (ft.In.Survive) ft.In.PickFresh = s.Pick.Found && now - s.PickAt < 5f;
            ft.In.PickFrom = s.PickFrom;
            // M3: the owner - never fired through; fallen back on under a
            // FOLLOW / VEHICLE order only (the others keep their post).
            Transform owner = u.Owner;
            ft.In.HasOwner = owner != null && owner;
            ft.In.Owner = ft.In.HasOwner ? owner.position : Vector3.zero;
            ft.In.Regroup = !ft.In.Survive && ft.In.HasOwner && (u.Order.Mode == MercOrder.Follow || u.Order.Mode == MercOrder.Vehicle)
                && Flat(ft.In.Owner - ft.In.Me) < MercBreakOffUnits;
            // merc-combat-response: lanes - the owner's held aim, and whether
            // he may step out of a line (not a deserter, a boarder, a gun or
            // radar crewman).
            ft.In.Lanes = MercLanesFree(u);
            Vector3 aimFrom = ft.In.Me, aimTo = ft.In.Me;
            ft.In.OwnerAims = ft.In.Lanes && ft.In.HasOwner && !ft.In.Survive && MercOwnerAim(now, out aimFrom, out aimTo);
            ft.In.AimFrom = ft.In.OwnerAims ? aimFrom : ft.In.Me;
            ft.In.AimTo = ft.In.OwnerAims ? aimTo : ft.In.Me;
            // What he fights, for his mates.
            ft.Target = target ? f.Target : null;
            ft.TargetIsPlayer = f.TargetIsPlayer;
            ft.Sees = ft.In.Sees;
            ft.At = ft.In.Me;
            if (!ft.In.PickFresh && ft.In.MayFight && s.Count > 0 && ft.Brain.Fighting && MercCover(f, u, now))
            {
                ft.In.Pick = ft.Fallback;
                ft.In.PickFresh = true;
            }
        }

        /// <summary>Cover while the service has none (the cells around him
        /// are still being mapped, or it found nothing): FindCover's samples
        /// against his primary threat, inside his order's leash - one search
        /// a quarter second for all mercs, one every two seconds for him.
        /// Kept in u.Fight.Fallback as an OVER point (stand up to fire).</summary>
        static bool MercCover(Fighter f, MercUnit u, float now)
        {
            MercFight ft = u.Fight;
            MercSense s = u.Sense;
            if (ft.Fallback.Found && now - ft.FallbackAt < 5f) return true;
            if (now < u.NextCover || now - _fallbackAt < 0.25f) return false;
            u.NextCover = now + 2f;
            _fallbackAt = now;
            ft.Fallback = new CoverPick();
            Vector3 spot;
            if (!FindCover(f, s.At[0], out spot)) return false;
            Vector3 leash;
            float radius;
            MercLeash(f, u, out leash, out radius);
            if (radius > 0f && Flat(spot - leash) > radius) return false;
            ft.Fallback.Found = true;
            ft.Fallback.Confirmed = true;
            ft.Fallback.Ground = true;
            ft.Fallback.Point.Pos = spot;
            ft.Fallback.PeekSide = CoverPeek.Over;
            ft.Fallback.PeekPos = spot;
            ft.Fallback.Covered = 1;
            ft.FallbackAt = now;
            return true;
        }

        /// <summary>The magazine of his weapon (NPC_FirearmWeaponController
        /// _bulletsCount / _bulletsMaxCount, IL); -1 when it cannot be read.</summary>
        static void MercRounds(Fighter f, MercFight ft, float now, out int rounds, out int max)
        {
            rounds = -1; max = -1;
            if (ft.Weapon == null || now >= ft.NextWeapon)
            {
                ft.NextWeapon = now + 2f;
                ft.Weapon = WeaponOf(f);
            }
            Component w = ft.Weapon;
            if (w == null) return;
            Type t = w.GetType();
            if (t != _bulletsType)
            {
                _bulletsType = t;
                _fBullets = AccessTools.Field(t, "_bulletsCount");
                _fBulletsMax = AccessTools.Field(t, "_bulletsMaxCount");
                if (_fBullets != null && _fBullets.FieldType != typeof(int)) _fBullets = null;
                if (_fBulletsMax != null && _fBulletsMax.FieldType != typeof(int)) _fBulletsMax = null;
            }
            if (_fBullets == null || _fBulletsMax == null) return;
            try
            {
                max = FastField.GetInt(_fBulletsMax, w);
                rounds = FastField.GetInt(_fBullets, w);
                if (max <= 0) { rounds = -1; max = -1; }
            }
            catch { rounds = -1; max = -1; }
        }

        /// <summary>A run to a point (Run: a sprint, Step: the short move of
        /// a peek). Re-issued only when the goal moved or the order lapsed.</summary>
        static void MercRun(Fighter f, MercUnit u, MercFight ft, Vector3 goal, bool sprint, float now)
        {
            bool reorder = !f.HasOrder || f.WantMain != MainRun || now >= f.MoveDeadline
                || Flat(ft.MoveTo - goal) > 1f;
            if (reorder && now >= ft.NextMove)
            {
                ft.NextMove = now + 0.25f;
                ft.MoveTo = goal;
                Vector3 dest = goal;
                // Cover and peek spots are NavMesh spots already; a strafe or
                // a flight point is put on the mesh here.
                NavMeshHit hit;
                if (sprint && NavMesh.SamplePosition(goal, out hit, 6f, NavMesh.AllAreas)) dest = hit.position;
                Go(f, dest, MainRun, PoseStand, now, Stance.Reposition);
                ft.Sprinting = sprint;
                ft.NextSpeed = 0f;
            }
            else if (f.HasOrder) Drive(f, MainRun, AddNone, PoseStand, now, false);
            if (now >= ft.NextSpeed)
            {
                ft.NextSpeed = now + 0.5f;
                NavMeshAgent agent = Agent(f);
                if (agent != null && agent.isActiveAndEnabled)
                {
                    float want = u.BaseSpeed(agent.speed) * (1f + Mathf.Max(0, u.Fast) / 100f)
                        * (ft.Sprinting ? SprintScale : 1f);
                    if (Mathf.Abs(agent.speed - want) > 0.05f) agent.speed = want;
                    u.LastSpeedSet = want;
                }
            }
        }

        const float SprintScale = 1.15f;

        /// <summary>M3: the owner or another merc of this client near the
        /// line from -> to (MercSquad.Near: flat, LineClear wide). Live
        /// positions, no ray.</summary>
        static bool MercFriendInLine(Fighter f, Vector3 from, Vector3 to)
        {
            MercUnit self = f.Squad == null ? null : f.Squad.Merc;
            if (self == null) return false;
            Transform owner = self.Owner;
            if (owner != null && owner && MercSquad.Near(from, to, owner.position)) return true;
            List<MercUnit> units = MercCoverService.Units;
            for (int i = 0; i < units.Count; i++)
            {
                MercUnit v = units[i];
                if (v == self || v.Ai == null || !v.Ai) continue;
                if (MercSquad.Near(from, to, v.Ai.transform.position)) return true;
            }
            return false;
        }

        /// <summary>M3: covering fire - the NPC's own round at the threat's
        /// last spot while it is out of sight (standing, planted, at his rate
        /// of fire). An NpcWar fighter there feels it as suppression; nobody
        /// is hurt by it unless the round finds him (the game's own shot).
        /// False: not now (not planted, no weapon) - he aims instead.</summary>
        static bool MercSuppress(Fighter f, Vector3 at, float now)
        {
            f.Stance = Stance.Fire;
            f.HasOrder = false;
            if (!Planted(f, now))
            {
                Drive(f, MainIdle, AddAim, PoseStand, now, true);
                FaceDir(f, at - f.Tr.position);
                return true;
            }
            Drive(f, MainIdle, AddFire, PoseStand, now, true);
            FaceDir(f, at - f.Tr.position);
            if (now < f.NextShot || !ReadArmed(f)) return true;
            Component weapon = WeaponOf(f);
            Vector3 aim = at + Vector3.up * ChestHeight
                + new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), UnityEngine.Random.Range(-0.5f, 1.0f),
                              UnityEngine.Random.Range(-1.5f, 1.5f));
            int fired = VanillaShot(f, weapon, aim);
            if (fired < 0) return false;
            f.NextShot = now + (fired > 0 ? ShotDelay(f) : 0.05f);
            if (fired > 0 && f.Target != null && f.Target && CfgSuppression.Value && _npcType != null)
            {
                Component c = f.Target.GetComponent(_npcType);
                Fighter v = c == null ? null : FighterOf(c);
                if (v != null) v.Suppression = Mathf.Min(1f, v.Suppression + 0.2f / Mathf.Max(0.4f, v.Nerve));
            }
            return true;
        }

        /// <summary>merc-combat-response: the reaction trace, every frame
        /// (arithmetic; a string only for the Debug line of a late round).</summary>
        static void MercTrace(Fighter f, MercFight ft, FightOut act, float now)
        {
            MercReaction r = ft.React;
            bool target = f.Target != null && f.Target;
            if (target && f.Sees)
            {
                ft.UnseenSince = 0f;
                // Another target is another contact.
                if (f.Target != ft.TraceTarget) { r.Lost(); ft.TraceTarget = f.Target; }
                if (r.ContactAt < 0f) r.Sight(now, Flat(f.Target.position - f.Tr.position));
            }
            else if (r.ContactAt >= 0f)
            {
                if (ft.UnseenSince <= 0f) ft.UnseenSince = now;
                else if (now - ft.UnseenSince > 1.5f) r.Lost();
            }
            if (act.Act == FightAct.None)
            {
                // Not a contact he may answer (his order bars the fight): no reaction to measure.
                if (!target || !f.Sees || !ft.In.MayFight) r.Quiet();
                else r.Held(MercReaction.Brain);
                return;
            }
            if (!r.Open) return;
            if (act.Act == FightAct.Run || act.Act == FightAct.Step) r.Held(MercReaction.Moving);
            else if (act.Act == FightAct.Reload) r.Held(MercReaction.Reload);
            else if (act.Act != FightAct.Fire || act.NoShot) r.Held(MercReaction.Brain);
        }

        /// <summary>Debug: one line per answered contact, distances in
        /// metres and game units.</summary>
        static void MercTraceLog(MercUnit u, MercReaction r)
        {
            RevivalPlugin.L.LogInfo("Mercs: " + u.Name + " contact at " + (r.ContactUnits / MercWeaponReach.Metre).ToString("0")
                + " m (" + r.ContactUnits.ToString("0") + " units): decision +"
                + (r.DecideAt >= 0f ? (r.DecideAt - r.ContactAt).ToString("0.00") : "-") + " s, first round +"
                + r.Last.ToString("0.00") + " s" + (r.Last > MercReaction.Prompt ? " (late, held:"
                + ((r.Why & MercReaction.Moving) != 0 ? " moving" : "") + ((r.Why & MercReaction.Reload) != 0 ? " reload" : "")
                + ((r.Why & MercReaction.Friend) != 0 ? " friend" : "") + ((r.Why & MercReaction.Muzzle) != 0 ? " muzzle" : "")
                + ((r.Why & MercReaction.Planting) != 0 ? " planting" : "") + ((r.Why & MercReaction.Brain) != 0 ? " brain" : "")
                + ((r.Why & MercReaction.Unseen) != 0 ? " unseen" : "") + ")" : "") + ".");
        }

        /// <summary>The fight is over: out of cover, his order next.</summary>
        static void MercFightEnd(Fighter f, MercFight ft)
        {
            f.InCover = false;
            f.Crouched = false;
            f.Cover = Vector3.zero;
            f.HasOrder = false;
            ft.Sprinting = false;
            ft.Fallback = new CoverPick();
        }
    }

    /// <summary>F8: what the mercs of this client are doing in their fights.</summary>
    internal static class MercFightStats
    {
        static string _status = "";
        static float _at;

        internal static string Status()
        {
            if (Time.time < _at) return _status;
            _at = Time.time + 1f;
            List<MercUnit> units = MercCoverService.Units;
            if (units.Count == 0) { _status = "no merc"; return _status; }
            int peeks = 0, bursts = 0, moves = 0, reloads = 0, heals = 0, flees = 0, strafes = 0;
            int covers = 0, planned = 0, covered = 0, flanks = 0, falls = 0, retreats = 0, held = 0, joined = 0;
            float fight = 0f, still = 0f;
            string each = "";
            for (int i = 0; i < units.Count; i++)
            {
                MercBrain b = units[i].Fight.Brain;
                if (b == null) continue;
                peeks += b.Peeks; bursts += b.Bursts; moves += b.Relocations; reloads += b.Reloads;
                heals += b.Heals; flees += b.Flees; strafes += b.Strafes;
                covers += b.Covers; planned += b.PlannedMoves; covered += b.CoveredMoves; flanks += b.FlankRuns;
                falls += b.Falls; retreats += b.Retreats; held += b.HeldFire; joined += b.Joined;
                fight += b.FightTime; still += b.SeenStill;
                if (each.Length < 120)
                    each += (each.Length > 0 ? " " : "") + units[i].Name + "=" + MercBrain.Name(b.State)
                        + (b.Mode != MercBrain.Normal ? "/" + MercBrain.ModeName(b.Mode) : "")
                        + " T" + MercGrade.Tier(units[i].Grade);
            }
            int contacts = 0, answered = 0, late = 0, lMove = 0, lReload = 0, lFriend = 0, lMuzzle = 0, lBrain = 0;
            int snaps = 0, laneSteps = 0, laneCleared = 0, laneStuck = 0, selfMoves = 0;
            float react = 0f, reactMax = 0f, laneTime = 0f, laneMax = 0f;
            for (int i = 0; i < units.Count; i++)
            {
                MercReaction r = units[i].Fight.React;
                contacts += r.Contacts; answered += r.Answered; late += r.Late; react += r.Sum;
                if (r.Max > reactMax) reactMax = r.Max;
                lMove += r.LateMoving; lReload += r.LateReload; lFriend += r.LateFriend; lMuzzle += r.LateMuzzle; lBrain += r.LateBrain;
                MercBrain b = units[i].Fight.Brain;
                if (b == null) continue;
                snaps += b.SnapBursts; laneSteps += b.LaneSteps; laneCleared += b.LaneCleared; laneStuck += b.LaneStuck;
                selfMoves += b.SelfMoves; laneTime += b.LaneTime;
                if (b.LaneLongest > laneMax) laneMax = b.LaneLongest;
            }
            _status = each + " | peeks " + peeks + ", bursts " + bursts + ", relocations " + moves
                + ", reloads " + reloads + ", heals " + heals + ", flees " + flees + ", strafes " + strafes
                + " | team: covering peeks " + covers + ", moves " + planned + " (" + covered + " covered), flanks " + flanks
                + ", fall-backs " + falls + ", retreats " + retreats + ", held fire " + held + ", call-outs " + joined
                + " (" + MercTeam.Shared + " shared)"
                + " | seen holding still " + still.ToString("0.0") + " s of " + fight.ToString("0") + " s in fights"
                + " | blasts " + MercDanger.Seen + ", grenades " + MercDanger.Grenades
                + " | reaction " + answered + "/" + contacts + " contacts answered, mean "
                + (answered > 0 ? react / answered : 0f).ToString("0.00") + " s, max " + reactMax.ToString("0.00")
                + " s, late " + late + " (moving " + lMove + ", reload " + lReload + ", friend " + lFriend
                + ", muzzle " + lMuzzle + ", brain " + lBrain + ") | stand bursts " + snaps
                + " | lanes " + laneCleared + "/" + laneSteps + " cleared (mean "
                + (laneCleared > 0 ? laneTime / laneCleared : 0f).ToString("0.00") + " s, max " + laneMax.ToString("0.00")
                + " s), blocked by geometry " + laneStuck + ", own steps " + selfMoves;
            return _status;
        }
    }
}
