// Airfield pockets acquire hostile players independently of vanilla's local
// player sensor. The master still drives native NPC shooting and Photon state.
// C# 3.0. No scene searches, argument arrays or enum boxes in the warm scan.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        sealed class AirfieldPlayer
        {
            internal Component Controller, Stats, States;
        }

        delegate int AirfieldFactionRead(object stats);
        delegate Transform AirfieldTargetRead(object ai);
        delegate void AirfieldTargetWrite(object ai, Transform target);
        static AirfieldFactionRead _airfieldFaction;
        static AirfieldTargetRead _airfieldTarget;
        static AirfieldTargetWrite _airfieldWriteTarget;
        static Type _airfieldStatsType, _airfieldStatesType;
        static FieldInfo _airfieldCharState;
        static bool _airfieldLooked;
        static int _airfieldScanFrame = -1, _airfieldScans;
        static Fighter _airfieldDeferred;
        static float _airfieldDeferredUntil;
        static readonly Dictionary<int, AirfieldPlayer> _airfieldPlayers =
            new Dictionary<int, AirfieldPlayer>();

        static bool AirfieldGarrisonTag(string tag)
        {
            return tag == "ground/airfield-N1-gate" || tag == "ground/airfield-N1-duty"
                || tag == "ground/airfield-N2-repair" || tag == "ground/airfield-N2-observer"
                || tag == "ground/airfield-N3-shelters" || tag == "ground/airfield-N4-ammo";
        }

        static bool AirfieldScanPermit(int frame)
        {
            if (_airfieldScanFrame != frame)
            { _airfieldScanFrame = frame; _airfieldScans = 0; }
            if (_airfieldScans >= 2) return false;
            _airfieldScans++;
            return true;
        }

        static void AirfieldAcquire(Fighter f, float now)
        {
            // Acquire owns the 0.30..0.45 s scan/LOS clocks. Bound expensive
            // work to two men per frame, including refreshing an existing LOS.
            if (now < f.NextScan && (f.Target == null || now < f.NextLos)) return;
            if (!CombatLoad.AnyNear(f.Tr.position, 600f)
                || !AirfieldScanTurn(f, now)) return;
            FrameProf.S(FrameProf.S_AirfieldAggro);
            try
            {
                Acquire(f, now);
                AirfieldBindPlayer(f);
            }
            finally { FrameProf.E(FrameProf.S_AirfieldAggro); }
        }

        static bool AirfieldScanTurn(Fighter f, float now)
        {
            // Give the first deferred man the next frame's first slot. Fixed
            // squad iteration otherwise starves the last pocket at low FPS.
            // A dead/removed/sleeping man may no longer get called: expire his
            // reservation, so he cannot block the surviving garrison forever.
            if (_airfieldDeferred != null && (_airfieldDeferred.Ai == null
                || _airfieldDeferred.Tr == null || now >= _airfieldDeferredUntil))
                _airfieldDeferred = null;
            if (_airfieldDeferred != null && _airfieldDeferred != f) return false;
            if (!AirfieldScanPermit(Time.frameCount))
            {
                if (_airfieldDeferred == null)
                { _airfieldDeferred = f; _airfieldDeferredUntil = now + 0.75f; }
                return false;
            }
            if (_airfieldDeferred == f) _airfieldDeferred = null;
            return true;
        }

        static void AirfieldPlayerCandidates(Fighter f, Vector3 p, float rangeSqr, ref int n)
        {
            if (_airfieldFaction == null || _airfieldWriteTarget == null) return;
            Component[] players = PlayerScan.All();
            for (int i = 0; i < players.Length; i++)
            {
                Component c = players[i];
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                float d = (c.transform.position - p).sqrMagnitude;
                if (d >= rangeSqr || (n == _cand.Length && d >= _candSqr[n - 1])) continue;
                AirfieldPlayer player = AirfieldPlayerOf(c);
                // Missing character state or faction is not permission to fire.
                if (player.Stats == null || player.States == null
                    || FastField.GetInt(_airfieldCharState, player.States) == 8) continue;
                int faction = _airfieldFaction(player.Stats);
                if (faction < 0 || !HatedValue(f.Hated, faction)) continue;
                Insert(ref n, c.transform, true, d);
            }
        }

        static AirfieldPlayer AirfieldPlayerOf(Component c)
        {
            AirfieldPlayer p;
            int id = c.GetInstanceID();
            if (!_airfieldPlayers.TryGetValue(id, out p) || p.Controller != c)
            {
                // Membership churn is cold work; stable connected players reuse
                // their component cache and the registry's immutable snapshot.
                if (_airfieldPlayers.Count > 512) _airfieldPlayers.Clear();
                p = new AirfieldPlayer(); p.Controller = c;
                _airfieldPlayers[id] = p;
            }
            if (p.Stats == null) p.Stats = c.GetComponentInChildren(_airfieldStatsType);
            if (p.States == null) p.States = c.GetComponentInChildren(_airfieldStatesType);
            return p;
        }

        static void AirfieldBindPlayer(Fighter f)
        {
            if (_airfieldTarget == null || _airfieldWriteTarget == null) return;
            Transform target = f.TargetIsPlayer && f.Sees ? f.Target : null;
            if (_airfieldTarget(f.Ai) == target) return;
            // Native SetKillTarget also sets the occupied-vehicle manager;
            // native ClearKillTarget releases aim IK. Never just write the field.
            _airfieldWriteTarget(f.Ai, target);
        }

        static void AirfieldAggroLookUp()
        {
            if (_airfieldLooked) return;
            _airfieldLooked = true;
            try
            {
                _airfieldStatsType = RevivalPlugin.TypeByName("PlayerStatisticsManager");
                _airfieldStatesType = RevivalPlugin.TypeByName("PlayerStatesController");
                FieldInfo info = AccessTools.Field(_airfieldStatsType, "_playerInfo");
                FieldInfo faction = info == null ? null : AccessTools.Field(info.FieldType, "fraction");
                _airfieldCharState = AccessTools.Field(_airfieldStatesType, "_characterState");
                MethodInfo set = AccessTools.Method(_npcType, "SetKillTarget", new Type[] { typeof(Transform) }, null);
                MethodInfo clear = AccessTools.Method(_npcType, "ClearKillTarget", Type.EmptyTypes, null);
                if (info == null || faction == null || info.FieldType.IsValueType
                    || _airfieldCharState == null || set == null || clear == null
                    || _fKillTarget == null || _fKillTarget.FieldType != typeof(Transform))
                    throw new InvalidOperationException("native player/kill-target bindings missing");
                Type ft = faction.FieldType;
                if (ft != typeof(int) && (!ft.IsEnum || Enum.GetUnderlyingType(ft) != typeof(int)))
                    throw new InvalidOperationException("native faction is not an int-backed enum");

                DynamicMethod read = new DynamicMethod("ndr_airfield_faction", typeof(int),
                    new Type[] { typeof(object) }, typeof(NpcWar), true);
                ILGenerator il = read.GetILGenerator();
                Label hasInfo = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, _airfieldStatsType);
                il.Emit(OpCodes.Ldfld, info); il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brtrue_S, hasInfo); il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldc_I4_M1); il.Emit(OpCodes.Ret);
                il.MarkLabel(hasInfo); il.Emit(OpCodes.Ldfld, faction); il.Emit(OpCodes.Ret);
                _airfieldFaction = (AirfieldFactionRead)read.CreateDelegate(typeof(AirfieldFactionRead));

                read = new DynamicMethod("ndr_airfield_target", typeof(Transform),
                    new Type[] { typeof(object) }, typeof(NpcWar), true);
                il = read.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, _npcType);
                il.Emit(OpCodes.Ldfld, _fKillTarget); il.Emit(OpCodes.Ret);
                _airfieldTarget = (AirfieldTargetRead)read.CreateDelegate(typeof(AirfieldTargetRead));

                DynamicMethod write = new DynamicMethod("ndr_airfield_bind_target", typeof(void),
                    new Type[] { typeof(object), typeof(Transform) }, typeof(NpcWar), true);
                il = write.GetILGenerator();
                Label noTarget = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Brfalse_S, noTarget);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, _npcType);
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Callvirt, set); il.Emit(OpCodes.Ret);
                il.MarkLabel(noTarget);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, _npcType);
                il.Emit(OpCodes.Callvirt, clear); il.Emit(OpCodes.Ret);
                _airfieldWriteTarget = (AirfieldTargetWrite)write.CreateDelegate(typeof(AirfieldTargetWrite));
            }
            catch (Exception ex)
            {
                _airfieldFaction = null; _airfieldWriteTarget = null;
                RevivalPlugin.L.LogError("AirfieldAggro: " + ex.Message);
            }
        }
    }
}
