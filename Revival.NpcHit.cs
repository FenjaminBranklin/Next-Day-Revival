// B S2a: where an NpcWar round ends (docs/ai/tasks/b-s2a-longrange-damage.md).
// The round's ray uses the game's own bullet mask and ignores triggers; the
// intended man's body is arithmetic (NpcHitCore) only where the physics cannot
// see it on this client. One ray per round, as before; no allocation.
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        static FieldInfo _fVisualized;
        static bool _visualizedLooked;
        static int _bodyHitsShutdown, _bodyHitsLying;
        static float _nextBodyLog;

        /// <summary>One NpcWar round from `from` along the unit `dir`: the first
        /// solid thing the game's own bullet stops at, or the intended man when
        /// the round enters his body first and the physics cannot see that body
        /// here (his colliders are off - the vanilla distance shutdown - or he
        /// lies wounded). null: it flew on.</summary>
        static GameObject LandRound(Component targetAi, Vector3 from, Vector3 dir, float range,
                                    out Vector3 impact)
        {
            impact = Vector3.zero;
            GameObject struck = null;
            float solid = -1f;
            RaycastHit hit;
            if (Physics.Raycast(from, dir, out hit, range, NpcHitCore.BulletMask, QueryTriggerInteraction.Ignore)
                && hit.collider != null)
            {
                struck = hit.collider.gameObject;
                impact = hit.point;
                solid = hit.distance;
            }
            return BodyOrStruck(targetAi, from, dir, range, struck, solid, ref impact);
        }

        /// <summary>The intended man instead of what the ray struck, when the
        /// round enters his body before that and the body is arithmetic here.
        /// Shared by the merc vehicle guns (MercsRide.Shoot).</summary>
        static GameObject BodyOrStruck(Component targetAi, Vector3 from, Vector3 dir, float range,
                                       GameObject struck, float solid, ref Vector3 impact)
        {
            if (targetAi == null || !targetAi) return struck;
            if (struck != null && struck.transform.IsChildOf(targetAi.transform)) return struck;
            int state = IntField(targetAi, _fMainState, -1);
            bool lying = state == NpcHitCore.MainWounded;
            if (!NpcHitCore.UseBody(Visualized(targetAi), state)) return struck;
            float body = NpcHitCore.Body(from, dir, targetAi.transform.position, lying, range);
            if (!NpcHitCore.BodyFirst(body, solid)) return struck;
            impact = from + dir * body;
            if (lying) _bodyHitsLying++; else _bodyHitsShutdown++;
            BodyLog();
            return targetAi.gameObject;
        }

        /// <summary>M3 covering fire is a real round too: the native NPC shot
        /// has no NPC damage branch, so before B S2a a body such a round found
        /// was never hurt. Same ray and damage as an aimed round.</summary>
        static void CoverRound(Fighter f, Component weapon, Vector3 aim)
        {
            Vector3 from = Muzzle(weapon, f);
            if (from == Vector3.zero) return;
            Vector3 dir = aim - from;
            float dist = dir.magnitude;
            if (dist < 0.01f) return;
            dir /= dist;
            Component targetAi = f.Target != null && f.Target && !f.TargetIsPlayer
                ? f.Target.GetComponent(_npcType) : null;
            Vector3 impact;
            GameObject struck = LandRound(targetAi, from + dir * 1.0f, dir,
                Mathf.Max(dist + 5f, RangeOf(f) + 20f), out impact);
            if (struck != null) RoundHits(f, struck, impact, dist);
        }

        /// <summary>MercsRide's vehicle guns: the same body rule after their
        /// own ray (which skips the own hull and riders).</summary>
        internal static GameObject MercGunBody(Transform target, Vector3 from, Vector3 dir, float range,
                                               GameObject struck, float solid, ref Vector3 impact)
        {
            if (target == null || !target || !LookUp()) return struck;
            Component ai = target.GetComponent(_npcType);
            return ai == null ? struck : BodyOrStruck(ai, from, dir, range, struck, solid, ref impact);
        }

        /// <summary>Is this man lying in the vanilla wounded state? Then a
        /// shooter holds low (AimPoint): his chest is not 3.3 u up.</summary>
        static bool Lying(Transform target)
        {
            if (target == null || _npcType == null) return false;
            Component ai = target.GetComponent(_npcType);
            return ai != null && IntField(ai, _fMainState, -1) == NpcHitCore.MainWounded;
        }

        /// <summary>NPC_AI2.IsPlayVisualizationEnabled: false while the vanilla
        /// distance shutdown has every collider of his off on this client.
        /// Unreadable counts as visualized (the physics decides, as before).</summary>
        static bool Visualized(Component ai)
        {
            if (!_visualizedLooked)
            {
                _visualizedLooked = true;
                _fVisualized = AccessTools.Field(_npcType, "IsPlayVisualizationEnabled");
                if (_fVisualized != null && _fVisualized.FieldType != typeof(bool)) _fVisualized = null;
                if (_fVisualized == null)
                    RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2.IsPlayVisualizationEnabled not found - "
                        + "rounds at men switched off by the distance shutdown keep missing.");
            }
            if (_fVisualized == null) return true;
            try { return FastField.GetBool(_fVisualized, ai); }
            catch { return true; }
        }

        /// <summary>At most one line per 30 s, and only when it happened: the
        /// field evidence that these rounds land (Kevin's test list).</summary>
        static void BodyLog()
        {
            float now = Time.time;
            if (now < _nextBodyLog) return;
            _nextBodyLog = now + 30f;
            RevivalPlugin.L.LogInfo("NpcWar: rounds that found a body the physics could not see: "
                + _bodyHitsShutdown + " on men switched off by the distance shutdown, "
                + _bodyHitsLying + " on men lying wounded (since start).");
        }
    }
}
