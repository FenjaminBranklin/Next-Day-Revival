// Shared bomb/rocket damage. Visual explosions carry zero damage. Only the
// master queues NPC/vehicle damage; the shooter queues player damage so native
// Photon sender attribution stays correct. NPC health runs on its owner,
// player health on the victim. Body/explosion avoids the head x3 multiplier
// and incorrect firearm parameters. Native player safety/group/armour rules
// still apply. No physics query or idle allocation.
using System;
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
            internal Component[] Npcs, Vehicles, Players;
            internal int Stage, Index;
            internal bool PlayersOnly;
            internal bool OwnerPeople, NativeProfile, FactionShield, Shelter;
            internal bool OwnPlayer;
            internal float NpcRadius, NpcCore;
            internal string FiringSide;
            internal Transform ExcludeHull;
            internal Vector3[] CrewPositions;
            internal int ReadyFrame;
        }

        static readonly Queue<Blast> Pending = new Queue<Blast>(64);
        static readonly Stack<Blast> Free = new Stack<Blast>(64);
        static Blast _current;
        static MethodInfo _getView, _rpc, _applyNpc, _alive, _applyVehicle;
        static PropertyInfo _owner, _mine;
        static object _messageInfo;
        static Func<bool> _master;
        static bool _ready;
        internal static bool Ready { get { return _ready; } }
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
                _ready = _getView != null && _rpc != null && _owner != null && _mine != null
                    && _applyNpc != null && _alive != null && _applyVehicle != null;
                if (!_ready) throw new Exception("native damage bindings incomplete");
                // Runs on every client, including a non-master NPC owner receiving
                // ApplyDamage by RPC. Anonymous settlement-clearing kills otherwise
                // throw after death while looking up PhotonPlayer.Find(0).
                harmony.Patch(_applyNpc, new HarmonyMethod(typeof(OrdnanceBlast).GetMethod("BeforeNpcDamage")),
                    null, null, null, null);
                CrewBlast.Install(harmony, _applyVehicle);
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

        internal static void EnqueueOwnPlayer(Vector3 point, float radiusU, float playerPeak)
        {
            if (!_ready) return;
            // NPC mortar impact packets reach every client. Each scans its
            // player registry once but applies damage only to its own victim.
            Blast b = Queue(point, radiusU, 0f, 0f, playerPeak, true);
            b.OwnPlayer = true;
        }

        // Only the explosion/projectile authority calls this. Unlike master
        // jobs, native blasts can originate on a non-master shooter. Health
        // still changes exclusively on each victim's Photon owner (W fix).
        internal static void EnqueuePeople(Vector3 point, float radiusU, float peak, bool nativeProfile)
        {
            if (!_ready || radiusU <= 0f || peak <= 0f) return;
            Blast b = Queue(point, radiusU, peak, 0f, peak, false);
            b.OwnerPeople = true; b.NativeProfile = nativeProfile;
        }

        internal static void EnqueueMortar(Vector3 point, bool shooter, float radiusU,
                                         float npcPeak, float vehiclePeak, float playerPeak, bool anyFaction)
        {
            if (!_ready) return;
            if (_master())
            {
                AirDefenceDamage.ReportBlast(point, radiusU, Mathf.Clamp(vehiclePeak / 700f, 0f, 2f));
                Blast b = Queue(point, radiusU, npcPeak, vehiclePeak, 0f, false);
                b.Shelter = true;
            }
            if (shooter)
            {
                Blast b = Queue(point, radiusU, 0f, 0f, playerPeak, true);
                b.Shelter = true; b.FactionShield = !anyFaction;
            }
        }

        internal static void EnqueuePatrolPeople(Vector3 point, string side, Transform own,
            float npcPeak, float npcRadius, float npcCore, float playerPeak, float playerRadius)
        {
            if (!_ready || !_master()) return;
            Blast b = Queue(point, playerRadius, npcPeak, 0f, playerPeak, false);
            b.NpcRadius = Mathf.Max(0.1f, npcRadius);
            b.NpcCore = Mathf.Clamp(npcCore, 0f, b.NpcRadius);
            b.FiringSide = side; b.ExcludeHull = own;
        }

        static Blast Queue(Vector3 point, float radiusU, float npcPeak, float vehiclePeak, float playerPeak, bool playersOnly)
        {
            Blast b = Free.Count > 0 ? Free.Pop() : new Blast();
            b.Point = point;
            b.Radius = Mathf.Max(0.5f, radiusU);
            b.NpcPeak = npcPeak; b.VehiclePeak = vehiclePeak; b.PlayerPeak = playerPeak;
            b.PlayersOnly = playersOnly;
            b.OwnerPeople = false; b.NativeProfile = false; b.FactionShield = false; b.Shelter = false;
            b.OwnPlayer = false;
            b.NpcRadius = 0f; b.NpcCore = 0f; b.FiringSide = null; b.ExcludeHull = null;
            b.CrewPositions = null; b.ReadyFrame = 0;
            b.Stage = playersOnly ? 2 : 0; b.Index = 0;
            // Shared hook-fed arrays: no FindObjects/OverlapSphere at impact.
            b.Npcs = playersOnly || npcPeak <= 0f ? null : NpcScan.BlastTargets();
            b.Vehicles = playersOnly || vehiclePeak <= 0f ? null : VehicleScan.All();
            b.Players = playerPeak <= 0f ? null : PlayerScan.BlastTargets();
            Pending.Enqueue(b);
            return b;
        }

        internal static void EnqueueCrew(Array men, Vector3 point, float radius, float peak,
            bool native, Vector3[] seats)
        {
            if (!_ready || !_master()) return;
            Blast b = Queue(point, radius, 0f, 0f, 0f, false);
            b.NpcPeak = peak; // explicit crew targets; do not read another world snapshot
            b.NativeProfile = native;
            b.Npcs = new Component[men.Length];
            b.CrewPositions = new Vector3[men.Length];
            for (int i = 0; i < men.Length; i++)
            {
                Component ai = men.GetValue(i) as Component;
                b.Npcs[i] = ai;
                // Seats were captured at the fatal hit. Neither wreck motion
                // nor the much wider dismount formation changes past exposure.
                // Do not test wreck shelter: the impulse reached its interior.
                b.CrewPositions[i] = seats[i % seats.Length];
            }
            b.ReadyFrame = Time.frameCount + 1; // NPC Unity Start must run first
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
                    if (Time.frameCount < _current.ReadyFrame) break;
                    // Keep shooter-only player jobs when authority changes; discard
                    // a former master's NPC/vehicle jobs to prevent duplicate hits.
                    if ((!master && !_current.PlayersOnly && !_current.OwnerPeople) || !Visit(_current))
                    {
                        _current.Npcs = null; _current.Vehicles = null; _current.Players = null;
                        _current.FiringSide = null; _current.ExcludeHull = null;
                        _current.CrewPositions = null;
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
                    : b.PlayerPeak <= 0f || b.Players == null ? 0 : b.Players.Length;
                if (b.Index >= count) { b.Stage++; b.Index = 0; continue; }
                int index = b.Index++;
                Component target = b.Stage == 0 ? b.Npcs[index] : b.Stage == 1 ? b.Vehicles[index] : b.Players[index];
                GameObject go = target == null ? null : target.gameObject;
                if (go == null) return true;
                float peak = b.Stage == 0 ? b.NpcPeak : b.Stage == 1 ? b.VehiclePeak : b.PlayerPeak;
                Vector3 victim = b.Stage == 0 && b.CrewPositions != null
                    ? b.CrewPositions[index] : go.transform.position;
                float distance = Vector3.Distance(victim, b.Point);
                float damage = b.NativeProfile ? peak * NativeModifier(distance, b.Radius)
                    : Damage(distance, b.Radius, peak);
                if (b.Stage == 0 && b.NpcRadius > 0f)
                    damage = distance <= b.NpcCore ? peak
                        : Damage(distance - b.NpcCore, b.NpcRadius - b.NpcCore, peak);
                if (damage < 1f) return true;
                if (b.FiringSide != null)
                {
                    if (b.ExcludeHull != null && go.transform.IsChildOf(b.ExcludeHull)) return true;
                    string side = b.Stage == 0 ? NpcWar.PatrolFaction(target) : Fraktion.Spielerseite(go);
                    if (!Fraktion.Feind(b.FiringSide, side)) return true;
                    Component carrier = GunnerAI.Carrier(go.transform);
                    if (carrier != null && GunnerAI.Armoured(carrier)) return true;
                }
                if (b.Shelter && b.Stage != 1 && AirKills.Sheltered(go.transform.position, b.Point)) return true;
                if (b.Stage == 2 && b.FactionShield && Mortar.FactionShield.SameFactionAsLocal(go)) return true;
                try
                {
                    if (b.OwnPlayer)
                    {
                        object view = _getView.Invoke(null, new object[] { go });
                        if (view == null || !(bool)_mine.GetValue(view, null)) return true;
                    }
                    if (b.Stage == 0)
                    {
                        if (!(bool)_alive.Invoke(target, null) || !Mortar.Hurtable(target)) return true;
                        NpcDamage(target, damage, b.Point);
                    }
                    else if (b.Stage == 1)
                    {
                        CrewBlast.Profile previous = CrewBlast.Begin(b.Point, b.Radius, b.NpcPeak, b.NativeProfile);
                        try { _applyVehicle.Invoke(target, new object[] { damage, Explosion }); }
                        finally { CrewBlast.End(previous); }
                    }
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

        // Preserve the shipped grenade/shell profile: full damage within r,
        // cosine falloff to zero at 2r. Bombs/mortars retain linear falloff.
        internal static float NativeModifier(float distance, float radius)
        {
            if (distance >= radius * 2f) return 0f;
            if (distance <= radius) return 1f;
            return 0.5f + 0.5f * Mathf.Cos((distance - radius) * Mathf.PI / radius);
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
