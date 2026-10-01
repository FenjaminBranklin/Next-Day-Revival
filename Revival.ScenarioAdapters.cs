// Z H0b: existing feature internals exposed only to the marked offline runner.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class Mercs
    {
        internal static void ScenarioPointHealth(Component point, float health) { SetNumber(point, "Health", health); }

        internal static Record ScenarioSpawn(ScenarioActorSpec spec, Vector3 position)
        {
            if (!OfflineStart.Active || !ScenarioRun.Active) throw new InvalidOperationException("Scenario mercs require offline mode.");
            GameObject owner = MapTools.LocalPlayer(); if (owner == null) return null;
            if (_profiles.Count == 0) LoadProfiles(DefaultProfiles, "scenario built-in");
            Profile p = ProfileById(spec.Profile); if (p == null) throw new ArgumentException("Unknown merc profile " + spec.Profile);
            Record r = new Record(); r.Id = --_sessionSerial; r.Session = true;
            r.ProfileId = p.Id; r.Name = spec.Id; r.PaidUntil = double.MaxValue;
            r.Order = new MercOrder(); r.Order.Mode = MercOrder.Stay;
            r.Order.Points = new Vector3[] { position }; r.Order.Scene = MapScene.Current;
            RevivalComposition.CrewMan man = p.Loadout(r.Name);
            GameObject settlement = Crew.DropOwnedSquad(position, new Vector3[] { position }, SideOf(FactionOf(owner)),
                new List<RevivalComposition.CrewMan>(new RevivalComposition.CrewMan[] { man }), KeyPrefix + LocalActor + "/" + r.Id,
                delegate(Component point, int index) { SetNumber(point, "Health", (float)spec.Health); SetNumber(point, "Level", p.Level); });
            Array men = settlement == null ? null : Crew.Men(settlement);
            Component ai = men == null || men.Length != 1 ? null : men.GetValue(0) as Component;
            if (ai == null) return null;
            Crew.Forget(settlement);
            MercUnit u = new MercUnit(); u.Id = r.Id; u.Name = r.Name; u.Settlement = settlement; u.Ai = ai;
            u.Owner = owner.transform; u.OwnerGo = owner; u.Order = r.Order;
            u.Precise = p.Precise; u.Fast = p.Fast; u.Tanky = p.Tanky; u.AAGunner = p.AAGunner;
            u.Grade = MercGrade.Of(p.Precise, p.Fast, p.Tanky, p.Level); u.MaxHealth = (float)spec.Health;
            u.Approach = position + owner.transform.forward * 280f;
            u.Medicine = r.Medicine; u.Medicine.MaxHealth = u.MaxHealth; r.Medicine.SessionLoadout();
            if (!NpcWar.StartMerc("merc-" + r.Id, settlement, ai, u, man)) return null;
            _units[ai.GetInstanceID()] = u; r.Unit = u; _roster.Add(r); Specs(ai, p);
            return r;
        }

        internal static MercOrder ScenarioOrder(Record r, ScenarioOrderSpec spec, object team)
        {
            if (!OfflineStart.Active || !ScenarioRun.Active || r == null || !r.Session || r.Unit == null) throw new InvalidOperationException("Not a scenario merc.");
            MercOrder o = new MercOrder();
            o.Mode = spec.Mode == "follow" ? MercOrder.Follow : spec.Mode == "stay" ? MercOrder.Stay
                : spec.Mode == "perimeter" ? MercOrder.Perimeter : spec.Mode == "attack" ? MercOrder.Attack
                : spec.Mode == "gun" ? MercOrder.ManGun : spec.Mode == "radar" ? MercOrder.ManRadar : MercOrder.Patrol;
            o.Points = new Vector3[spec.Points.Length];
            for (int i = 0; i < o.Points.Length; i++)
            {
                Vector3 requested = ScenarioRun.Position(spec.Points[i]);
                if (!ScenarioRun.Ground(requested, spec.Goal == "roof" ? 2f : 12f, out o.Points[i])) throw new ArgumentException("Order point has no ground.");
                if (spec.Goal == "roof" && !TowerRoof.GoalUp(o.Points[i])) throw new ArgumentException("Roof goal projected off roof.");
            }
            if (o.Mode == MercOrder.ManGun || o.Mode == MercOrder.ManRadar)
            {
                Vector3 seat; Quaternion rotation;
                if (!MercAA.Pose(spec.Post, out seat, out rotation) || !MercAA.AvailableForOrder(spec.Post))
                    throw new ArgumentException("Scenario station absent or occupied.");
                o.Points = new Vector3[] { seat }; o.Facing = new Vector3(spec.Post + 1, 0f, 0f);
            }
            if (o.Mode == MercOrder.Attack)
            {
                Vector3 from = r.Unit.Ai.transform.position;
                float distance = Flat(o.Centre - from);
                if (distance < MercOrder.AttackMinUnits || distance > MercOrder.AttackMaxUnits) throw new ArgumentException("Attack must be 10 to 600 metres away.");
                o.Points = new Vector3[] { o.Centre, from }; o.Facing = (o.Centre - from).normalized;
                o.Kind = MercOrder.AtPoint; o.Team = new MercAttackTeam(1);
            }
            Give(new List<Record>(new Record[] { r }), o);
            if (MercAA.IsOrder(r.Order)) NpcWar.MercStationWake(r.Unit);
            if (spec.Goal == "roof") r.Order.K = spec.Slot;
            if (team != null) { r.Order.Team = team; r.Order.K = spec.Slot; r.Order.N = spec.TeamSize; }
            return r.Order;
        }
    }

    public static partial class NpcWar
    {
        struct ScenarioShotContext { internal float NextShot; internal Component Previous; }
        internal static void ScenarioInstall(Harmony harmony)
        {
            if (!ScenarioRun.Active) return;
            ScenarioRun.PrepareStations();
            MethodInfo damage = AccessTools.Method(RevivalPlugin.TypeByName("NPC_AI2"), "ApplyDamage", null, null);
            if (damage == null) throw new MissingMethodException("NPC_AI2.ApplyDamage");
            harmony.Patch(damage, null, new HarmonyMethod(typeof(NpcWar).GetMethod("ScenarioDamage", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
            MethodInfo respawn = AccessTools.Method(RevivalPlugin.TypeByName("NPC_AI2"), "RespawnTimerActions", null, null);
            if (respawn == null) throw new MissingMethodException("NPC_AI2.RespawnTimerActions");
            harmony.Patch(respawn, new HarmonyMethod(typeof(NpcWar).GetMethod("ScenarioNoRespawn", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null, null);
            harmony.Patch(AccessTools.Method(typeof(NpcWar), "Shoot", null, null),
                new HarmonyMethod(typeof(NpcWar).GetMethod("ScenarioShotBefore", BindingFlags.Static | BindingFlags.NonPublic)),
                new HarmonyMethod(typeof(NpcWar).GetMethod("ScenarioShotAfter", BindingFlags.Static | BindingFlags.NonPublic)), null,
                new HarmonyMethod(typeof(NpcWar).GetMethod("ScenarioShotFinal", BindingFlags.Static | BindingFlags.NonPublic)), null);
        }
        static void ScenarioDamage(object __instance) { ScenarioRun.ObserveDamage(__instance as Component); }
        static bool ScenarioNoRespawn(object __instance) { return !ScenarioRun.OwnsNpc(__instance as Component); }
        static void ScenarioShotBefore(Fighter f, out ScenarioShotContext __state)
        {
            __state = new ScenarioShotContext(); __state.NextShot = f.NextShot;
            __state.Previous = ScenarioRun.Shooter;
            if (ScenarioRun.Measuring) ScenarioRun.Shooter = f.Ai;
        }
        static void ScenarioShotAfter(Fighter f, bool __result, ScenarioShotContext __state)
        {
            if (!__result && f.NextShot != __state.NextShot && ((f.MuzzleBlockedSince > 0f && f.NextShot == Time.time + 0.2f)
                || (f.MateBlockedSince > 0f && f.NextShot == Time.time + 0.15f))) ScenarioRun.BlockedShot(f.Ai);
        }
        static Exception ScenarioShotFinal(Exception __exception, ScenarioShotContext __state)
        { ScenarioRun.Shooter = __state.Previous; return __exception; }
        internal static bool ScenarioMoving(Component ai)
        {
            Fighter f = FighterOf(ai);
            return f != null && f.HasOrder && (f.WantMain == MainRun || f.WantMain == MainWalk)
                && (f.Ordered - f.Tr.position).sqrMagnitude > 2.8f * 2.8f;
        }
    }
}
