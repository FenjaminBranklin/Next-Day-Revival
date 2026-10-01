// Z K1b: owner-local search, all-layer route clearance, existing M1/M2 fight.
using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        sealed class MercPositionWorld : IMercPositionWorld
        {
            readonly NavMeshPath _path = new NavMeshPath();
            readonly Vector3[] _corners = new Vector3[5];
            readonly RaycastHit[] _hits = new RaycastHit[32];
            readonly Collider[] _overlaps = new Collider[32];
            internal Fighter Self;

            public bool Reach(Vector3 from, Vector3 near, out Vector3 at)
            {
                at = near;
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(near, out hit, 1.2f, NavMesh.AllAreas)) return false;
                at = hit.position;
                if (Mathf.Abs(at.y - from.y) > 4.2f || MercFriendInLine(Self, from, at)) return false;
                if (!NavMesh.CalculatePath(from, at, NavMesh.AllAreas, _path)
                    || _path.status != NavMeshPathStatus.PathComplete) return false;
                int count = _path.GetCornersNonAlloc(_corners);
                if (count < 2 || count >= _corners.Length) return false;
                int n = Physics.OverlapCapsuleNonAlloc(at + Vector3.up * 0.85f,
                    at + Vector3.up * 4.1f, 0.75f, _overlaps, ~0, QueryTriggerInteraction.Ignore);
                if (n >= _overlaps.Length) return false;
                for (int i = 0; i < n; i++)
                    if (_overlaps[i] != null && !_overlaps[i].transform.IsChildOf(Self.Tr)) return false;
                float travel = 0f;
                for (int i = 1; i < count; i++)
                {
                    Vector3 dir = _corners[i] - _corners[i - 1];
                    float distance = dir.magnitude; travel += distance;
                    if (travel > MercPosition.Max * 1.5f) return false;
                    if (distance < 0.01f) continue;
                    // Baked navigation can miss props added at runtime.
                    n = Physics.CapsuleCastNonAlloc(_corners[i - 1] + Vector3.up * 0.85f,
                        _corners[i - 1] + Vector3.up * 4.1f, 0.75f, dir / distance, _hits,
                        distance, ~0, QueryTriggerInteraction.Ignore);
                    if (n >= _hits.Length) return false;
                    for (int j = 0; j < n; j++)
                        if (_hits[j].collider != null && !_hits[j].collider.transform.IsChildOf(Self.Tr)) return false;
                }
                return true;
            }

            public bool Lane(Vector3 muzzle, Vector3 target)
            { return !MercFriendInLine(Self, muzzle, target) && MercLineCast(muzzle, target, Self.Tr, Self.Target); }
        }

        static readonly MercPositionWorld _mercPositionWorld = new MercPositionWorld();
        static readonly MercPositionBudget _mercPositionBudget = new MercPositionBudget();

        delegate void MercPositionShot(object ai, Transform target);
        static MercPositionShot _mercPositionShot;

        // ShootingActions requires MainState == Idle (installed game IL).
        // ShootToTarget itself is state-independent and retains the native
        // player hit-region, accuracy, ammunition, rate timer and Photon shot.
        static void InstallMercPositionShot()
        {
            try
            {
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                MethodInfo method = npc == null ? null : AccessTools.Method(npc, "ShootToTarget", new Type[] { typeof(Transform) }, null);
                if (method == null || method.ReturnType != typeof(void)) return;
                DynamicMethod dm = new DynamicMethod("MercPositionShot", typeof(void),
                    new Type[] { typeof(object), typeof(Transform) }, typeof(NpcWar).Module, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, method.DeclaringType);
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Callvirt, method); il.Emit(OpCodes.Ret);
                _mercPositionShot = (MercPositionShot)dm.CreateDelegate(typeof(MercPositionShot));
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("MercPosition: moving player-shot binding failed - " + ex); }
        }

        static bool MercPositionAiming(Fighter f)
        {
            MercUnit u = f.Squad == null ? null : f.Squad.Merc;
            return u != null && u.Fight.Out.FireOnMove && u.Fight.Position.Moving
                && f.WantMain == MainRun;
        }

        static void MercPositionFire(Fighter f, MercFight ft, float now)
        {
            FrameProf.S(FrameProf.S_MercPositionT);
            try
            {
                if (!ft.Position.Moving || !f.Sees || f.Target == null || Reloading(f)
                    || MercFriendInLine(f, f.Tr.position, f.Target.position)) return;
                Drive(f, MainRun, AddFire, PoseStand, now, false);
                if (IntField(f.Ai, _fMainState, -1) != MainRun) return;
                Aim(f, now);
                if (now < f.ReactUntil || !f.IkDriven || f.AimWeight < 0.6f || now < f.NextShot) return;
                if (f.TargetIsPlayer)
                {
                    if (_mercPositionShot == null) return;
                    // Arrival can let native ShootingActions run in this frame.
                    // Share its timer so the bridge never emits a second round.
                    if (_mercLineShotTimer != null && FastField.GetFloat(_mercLineShotTimer, f.Ai) > 0f) return;
                    _mercPositionShot(f.Ai, f.Target);
                    // K1a resets the unused native timer on a pending check.
                    f.NextShot = now + (_mercLineShotTimer == null ? ShotDelay(f)
                        : Mathf.Max(0.05f, FastField.GetFloat(_mercLineShotTimer, f.Ai)));
                }
                else if (Shoot(f)) f.NextShot = now + ShotDelay(f);
            }
            finally { FrameProf.E(FrameProf.S_MercPositionT); }
        }

        static void MercPositionIn(Fighter f, MercUnit u, MercFight ft, float now)
        {
            FrameProf.S(FrameProf.S_MercPositionT);
            try
            {
                MercPosition p = ft.Position;
                ft.In.PositionMove = ft.In.PositionFire = false;
                ft.In.Me = f.Tr.position;
                // Orders and survival retain priority; no stale movement across commands.
                if (ft.PositionOrder != u.Order || ft.PositionIssued != u.Order.IssuedAt || ft.PositionTarget != f.Target)
                {
                    p.Reset(); ft.PositionOrder = u.Order; ft.PositionIssued = u.Order.IssuedAt;
                    ft.PositionTarget = f.Target;
                }
                bool allowed = ft.In.MayFight && ft.In.Target && f.Target != null && ft.In.Count > 0 && !ft.In.Survive
                    && ft.In.Lanes && !ft.In.Danger && !ft.In.Reloading && ft.In.Health >= MercBrain.RetreatBelow
                    && ft.In.Rounds != 0 && ft.Brain.Mode == MercBrain.Normal
                    && ft.Brain.State != MercBrain.Healing && ft.Brain.State != MercBrain.Reloading;
                if (!allowed) { p.Reset(); return; }
                // Hold the opening until the next M2 Think consumes it.
                if (p.Opened) { ft.In.PositionFire = true; return; }
                bool blocked = (f.MuzzleBlockedSince > 0f && now - f.MuzzleBlockedSince > 0.2f)
                    || !f.Sees;
                if (!blocked && !p.Moving && !p.Searching) return;
                Vector3 leash; float radius;
                MercLeash(f, u, out leash, out radius);
                if (p.Due(now) && _mercPositionBudget.Take(Time.frameCount))
                {
                    Vector3 muzzle = Muzzle(ft.Weapon, f);
                    if (muzzle != Vector3.zero && ft.Weapon != null)
                    {
                        Vector3 endpoint = AimWorld(f);
                        if (!p.Moving && !p.Searching)
                        {
                            CoverPick held = ft.Brain.Cover;
                            p.Begin(ft.In.Me, endpoint, ref u.Sense.Pick, ref held, leash, radius, now);
                        }
                        _mercPositionWorld.Self = f;
                        p.Probe(_mercPositionWorld, ft.In.Me, muzzle - ft.In.Me, endpoint, leash, radius, now);
                        if (p.Opened) f.NextLos = now;
                    }
                }
                ft.In.PositionMove = p.Moving;
                ft.In.PositionDest = p.Dest;
                ft.In.PositionFire = p.Opened || (p.Moving && p.Clear);
            }
            finally { FrameProf.E(FrameProf.S_MercPositionT); }
        }
    }
}
