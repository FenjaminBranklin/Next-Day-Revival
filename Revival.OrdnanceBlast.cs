// Shared bomb/rocket damage. Visual explosions carry zero damage. Only the
// master queues NPC/vehicle damage; the shooter queues player damage so native
// Photon sender attribution stays correct. NPC health runs on its owner,
// player health on the victim. Body/explosion avoids the head x3 multiplier
// and incorrect firearm parameters. Native player safety/group/armour rules
// still apply. No physics query or idle allocation.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class OrdnanceBlast
    {
        internal const float UnitsPerMetre = 2.8f;
        internal const float ReferenceRadiusM = 32f;
        internal const float ReferencePersonPeak = 240f;
        internal const int Body = 1, Explosion = 14;

        internal static float RadiusForMass(float kilograms)
        {
            return ReferenceRadiusM * Mathf.Pow(Mathf.Max(1f, kilograms) / 100f, 1f / 3f);
        }

        internal static float Damage(float distanceU, float radiusU, float peak)
        {
            return Mathf.Max(0f, peak) * Mathf.Clamp01(1f - distanceU / Mathf.Max(0.001f, radiusU));
        }

        sealed class Blast
        {
            internal Vector3 Point;
            internal float Radius, NpcPeak, VehiclePeak, PlayerPeak;
            internal Component[] Npcs, Vehicles;
            internal IList Players;
            internal int Stage, Index;
            internal bool PlayersOnly;
        }

        static readonly Queue<Blast> Pending = new Queue<Blast>(64);
        static readonly Stack<Blast> Free = new Stack<Blast>(64);
        static Blast _current;
        static MethodInfo _getView, _rpc, _applyNpc, _alive, _applyVehicle, _server;
        static PropertyInfo _owner, _mine;
        static FieldInfo _players;
        static object _messageInfo;
        static Func<bool> _master;
        static bool _ready;
        static int _errors;

        internal static void Install(Harmony harmony)
        {
            try
            {
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                Type ext = RevivalPlugin.TypeByName("Extensions");
                Type view = RevivalPlugin.TypeByName("PhotonView");
                Type info = RevivalPlugin.TypeByName("PhotonMessageInfo");
                Type vehicle = RevivalPlugin.TypeByName("VehicleGameSystem");
                Type server = RevivalPlugin.TypeByName("NetworkGameServer");
                MethodInfo masterGetter = AccessTools.PropertyGetter(RevivalPlugin.TypeByName("PhotonNetwork"), "isMasterClient");
                _master = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), masterGetter);
                _getView = AccessTools.Method(ext, "GetPhotonView", new Type[] { typeof(GameObject) }, null);
                _rpc = AccessTools.Method(view, "RPC", new Type[] { typeof(string),
                    RevivalPlugin.TypeByName("PhotonPlayer"), typeof(object[]) }, null);
                _owner = view.GetProperty("owner");
                _mine = view.GetProperty("isMine");
                _applyNpc = AccessTools.Method(npc, "ApplyDamage", new Type[] {
                    typeof(float), typeof(int), typeof(int), typeof(int), typeof(Vector3), info }, null);
                _alive = AccessTools.Method(npc, "IsAlive", null, null);
                _messageInfo = Activator.CreateInstance(info);
                _applyVehicle = AccessTools.Method(vehicle, "ApplyDamage", new Type[] { typeof(float), typeof(int) }, null);
                _server = AccessTools.PropertyGetter(server, "Instance");
                _players = AccessTools.Field(server, "NetworkPlayers");
                _ready = _getView != null && _rpc != null && _owner != null && _mine != null
                    && _applyNpc != null && _alive != null && _applyVehicle != null
                    && _server != null && _players != null;
                if (!_ready) throw new Exception("native damage bindings incomplete");
                // Runs on every client, including a non-master NPC owner receiving
                // ApplyDamage by RPC. Anonymous settlement-clearing kills otherwise
                // throw after death while looking up PhotonPlayer.Find(0).
                harmony.Patch(_applyNpc, new HarmonyMethod(typeof(OrdnanceBlast).GetMethod("BeforeNpcDamage")),
                    null, null, null, null);
            }
            catch (Exception ex)
            {
                _ready = false;
                RevivalPlugin.L.LogError("OrdnanceBlast: " + ex.Message);
            }
        }

        public static void BeforeNpcDamage(object __instance, int __2, int __3)
        {
            if (__2 == Explosion && __3 == 0) Mortar.BreakKillStreak(__instance as Component);
        }

        internal static void Enqueue(Vector3 point, float radiusU, float npcPeak, float vehiclePeak, float playerPeak)
        {
            if (!_ready || !_master()) return;
            // W AA7: mod bombs and rockets also damage fixed AA (as Mortar.Sweep does).
            AirDefenceDamage.ReportBlast(point, radiusU, Mathf.Clamp(vehiclePeak / 700f, 0f, 2f));
            Queue(point, radiusU, npcPeak, vehiclePeak, playerPeak, false);
        }

        internal static void EnqueuePlayers(Vector3 point, float radiusU, float playerPeak)
        {
            if (!_ready) return;
            Queue(point, radiusU, 0f, 0f, playerPeak, true);
        }

        static void Queue(Vector3 point, float radiusU, float npcPeak, float vehiclePeak, float playerPeak, bool playersOnly)
        {
            Blast b = Free.Count > 0 ? Free.Pop() : new Blast();
            b.Point = point;
            b.Radius = Mathf.Max(0.5f, radiusU);
            b.NpcPeak = npcPeak; b.VehiclePeak = vehiclePeak; b.PlayerPeak = playerPeak;
            b.PlayersOnly = playersOnly;
            b.Stage = playersOnly ? 2 : 0; b.Index = 0;
            // Shared hook-fed arrays: no FindObjects/OverlapSphere at impact.
            b.Npcs = playersOnly ? null : NpcScan.All();
            b.Vehicles = playersOnly ? null : VehicleScan.All();
            object server = _server.Invoke(null, null);
            b.Players = server == null ? null : _players.GetValue(server) as IList;
            Pending.Enqueue(b);
        }

        internal static void Tick()
        {
            if (_current == null && Pending.Count == 0) return;
            bool master = _master();
            FrameProf.S(FrameProf.S_OrdnanceBlastT);
            try
            {
                long start = Stopwatch.GetTimestamp();
                long budget = Math.Max(1L, Stopwatch.Frequency / 12500L); // 0.08 ms, between native calls
                int visits = 0;
                do
                {
                    if (_current == null) _current = Pending.Dequeue();
                    // Keep shooter-only player jobs when authority changes; discard
                    // a former master's NPC/vehicle jobs to prevent duplicate hits.
                    if ((!master && !_current.PlayersOnly) || !Visit(_current))
                    {
                        _current.Npcs = null; _current.Vehicles = null; _current.Players = null;
                        Free.Push(_current); _current = null;
                    }
                    visits++;
                }
                while ((_current != null || Pending.Count > 0) && visits < 64
                    && Stopwatch.GetTimestamp() - start < budget);
            }
            finally { FrameProf.E(FrameProf.S_OrdnanceBlastT); }
        }

        static bool Visit(Blast b)
        {
            while (b.Stage < 3)
            {
                int count = b.Stage == 0 ? b.NpcPeak <= 0f ? 0 : b.Npcs.Length
                    : b.Stage == 1 ? b.VehiclePeak <= 0f ? 0 : b.Vehicles.Length
                    : b.PlayerPeak <= 0f || b.Players == null ? 0 : b.Players.Count;
                if (b.Index >= count) { b.Stage++; b.Index = 0; continue; }
                int index = b.Index++;
                Component target = b.Stage == 0 ? b.Npcs[index] : b.Stage == 1 ? b.Vehicles[index] : null;
                GameObject go = b.Stage == 2 ? b.Players[index] as GameObject : target == null ? null : target.gameObject;
                if (go == null) return true;
                float peak = b.Stage == 0 ? b.NpcPeak : b.Stage == 1 ? b.VehiclePeak : b.PlayerPeak;
                float damage = Damage(Vector3.Distance(go.transform.position, b.Point), b.Radius, peak);
                if (damage < 1f) return true;
                try
                {
                    if (b.Stage == 0)
                    {
                        if (!(bool)_alive.Invoke(target, null) || !Mortar.Hurtable(target)) return true;
                        NpcDamage(target, damage, b.Point);
                    }
                    else if (b.Stage == 1) _applyVehicle.Invoke(target, new object[] { damage, Explosion });
                    else PlayerDamage(go, damage, b.Point);
                }
                catch (Exception ex)
                {
                    if (_errors++ < 3) RevivalPlugin.L.LogWarning("OrdnanceBlast damage: " + ex.Message);
                }
                return true;
            }
            return false;
        }

        static void NpcDamage(Component ai, float damage, Vector3 point)
        {
            object view = _getView.Invoke(null, new object[] { ai.gameObject });
            if (view == null) return;
            Vector3 direction = ai.transform.position - point;
            if ((bool)_mine.GetValue(view, null))
            {
                _applyNpc.Invoke(ai, new object[] { damage, Body, Explosion, 0, direction, _messageInfo });
            }
            else
            {
                object owner = _owner.GetValue(view, null);
                if (owner == null) return;
                _rpc.Invoke(view, new object[] { "ApplyDamage", owner,
                    new object[] { damage, Body, Explosion, 0, direction } });
            }
        }

        static void PlayerDamage(GameObject go, float damage, Vector3 point)
        {
            object view = _getView.Invoke(null, new object[] { go });
            if (view == null) return;
            object victim = _owner.GetValue(view, null);
            if (victim == null) return;
            // Body, Explosion: physical blasts also hit the firing faction and
            // the bomber himself. Native safe-zone/god-mode rules still apply.
            _rpc.Invoke(view, new object[] { "PlayerApplyDamage", victim,
                new object[] { damage, Body, Explosion, point, point } });
        }
    }
}
