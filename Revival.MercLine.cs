// Z K1a: gate FireTo itself so native player shots and covering fire obey
// the same muzzle rule as NPC shots. No new network or authority path.
using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        static readonly RaycastHit[] _mercLineHits = new RaycastHit[32];
        static readonly Collider[] _mercLineOrigin = new Collider[32];
        static readonly MercLineBudget _mercLineBudget = new MercLineBudget();
        static FieldInfo _mercLineShotTimer;
        delegate void MercFireCall(object weapon, Vector3 at, bool apply);
        static MercFireCall _mercFireCall;

        static MercFireCall BindMercFire(MethodInfo fire)
        {
            DynamicMethod dm = new DynamicMethod("MercFireTo", typeof(void),
                new Type[] { typeof(object), typeof(Vector3), typeof(bool) }, typeof(NpcWar).Module, true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, fire.DeclaringType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, fire);
            il.Emit(OpCodes.Ret);
            return (MercFireCall)dm.CreateDelegate(typeof(MercFireCall));
        }

        static void InstallMercLine(Harmony harmony)
        {
            try
            {
                Type type = RevivalPlugin.TypeByName("NPC_FirearmWeaponController");
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                _mercLineShotTimer = npc == null ? null : AccessTools.Field(npc, "_shootingTimerDelay");
                if (_mercLineShotTimer != null && _mercLineShotTimer.FieldType != typeof(float))
                    _mercLineShotTimer = null;
                MethodInfo fire = type == null ? null : AccessTools.Method(type, "FireTo",
                    new Type[] { typeof(Vector3), typeof(bool) }, null);
                if (fire == null || fire.ReturnType != typeof(void))
                {
                    RevivalPlugin.L.LogError("MercLine: FireTo missing; muzzle guard unavailable.");
                    return;
                }
                harmony.Patch(fire, new HarmonyMethod(typeof(NpcWar).GetMethod("MercLinePrefix")),
                    null, null, null, null);
                _mercFireCall = BindMercFire(fire);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("MercLine: hook failed - " + ex); }
        }

        // IL: ShootToTarget selects the player's damaged-part hitbox and
        // passes its aim point to FireTo. VanillaShot and MercSuppress use
        // that same method for NPCs/last-seen points. Peers' RPC visuals are
        // unchanged; only the local merc owner's attempt is vetoed here.
        public static bool MercLinePrefix(Component __instance, Vector3 __0)
        {
            if (!Mercs.Any) return true;
            FrameProf.S(FrameProf.S_MercLineT);
            try
            {
                for (int q = 0; q < _squads.Count; q++)
                {
                    Squad s = _squads[q];
                    if (s.Merc == null || s.Men.Count == 0) continue;
                    Fighter f = s.Men[0];
                    if (WeaponOf(f) != __instance) continue;
                    return MercMuzzleClear(f, s.Merc.Fight.Line, __instance, __0, Time.time);
                }
                return true;
            }
            finally { FrameProf.E(FrameProf.S_MercLineT); }
        }

        static bool MercMuzzleClear(Fighter f, MercLineCache cache, Component weapon, Vector3 to, float now)
        {
            Vector3 from = Muzzle(weapon, f);
            if (from == Vector3.zero) return false; // no guessed eye-height muzzle
            int target = f.Target != null ? f.Target.GetInstanceID() : 0;
            int id = weapon.GetInstanceID();
            byte result = cache.Read(target, id, from, to, now);
            if (result == MercLineCache.Unknown && now >= cache.NextQuery && _mercLineBudget.Take(Time.frameCount))
            {
                cache.Store(target, id, from, to, now, MercLineCast(from, to, f.Tr, f.Target));
                result = cache.Result;
            }
            if (result == MercLineCache.Blocked)
            {
                if (f.MuzzleBlockedSince <= 0f) f.MuzzleBlockedSince = now;
                f.NextShot = now + MercLineCache.Every;
                return false;
            }
            if (result != MercLineCache.Clear)
            {
                // ShootToTarget resets its 0.2..0.3 s timer BEFORE FireTo.
                // Without this retry, synchronized native player shots could
                // lose the same frame budget forever. No round went out, so
                // retry next frame (or when this merc's 10 Hz throttle ends).
                if (f.TargetIsPlayer && f.Ai != null && _mercLineShotTimer != null)
                    FastField.SetFloat(_mercLineShotTimer, f.Ai,
                        cache.NextQuery > now ? cache.NextQuery - now : 0f);
                return false;
            }
            f.MuzzleBlockedSince = 0f;
            return !MercFriendInLine(f, from, to);
        }

        static bool MercLineCast(Vector3 from, Vector3 to, Transform self, Transform target)
        {
            // Raycast does not report an origin inside a collider. Check it
            // separately, without the old 1.2 u jump past a nearby wall.
            int n = Physics.OverlapSphereNonAlloc(from, 0.04f, _mercLineOrigin, ~0,
                QueryTriggerInteraction.Ignore);
            if (n == _mercLineOrigin.Length) return false;
            for (int i = 0; i < n; i++)
            {
                Collider c = _mercLineOrigin[i];
                if (c != null && !c.transform.IsChildOf(self)) return false;
            }
            Vector3 dir = to - from;
            float distance = dir.magnitude;
            if (distance < 0.001f) return false;
            n = Physics.RaycastNonAlloc(from, dir / distance, _mercLineHits, distance, ~0,
                QueryTriggerInteraction.Ignore);
            if (n == _mercLineHits.Length) return false; // unordered, possibly truncated
            float first = float.MaxValue;
            Collider hit = null;
            for (int i = 0; i < n; i++)
            {
                Collider c = _mercLineHits[i].collider;
                if (c == null || c.transform.IsChildOf(self) || _mercLineHits[i].distance >= first) continue;
                first = _mercLineHits[i].distance; hit = c;
            }
            // No proximity allowance: a slab/fence just before the hitbox
            // blocks. All solid layers, props, people and vehicles participate.
            return hit == null || (target != null && hit.transform.IsChildOf(target));
        }
    }
}
