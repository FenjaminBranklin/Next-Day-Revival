// Next Day: Survival - Revival Toolkit
//
// Surveillance-drone RELEVANCE, COMBAT and NETWORKING - the pieces the recon
// drone (SurvDrone, in RevivalDroneGear.cs) needs to be a real object in the
// world instead of a private flying camera.
//
// A separate feature file on purpose. SurvDrone's own state, lifecycle and hit
// points stay in RevivalDroneGear.cs; the Harmony hooks and the Photon plumbing
// that reach OUTSIDE the drone live here, so the shipped FPV combat drone
// (Drone, DroneNpcHook, DroneNpcFire, Drone.Net in Revival.FpvDrone.cs) is not
// touched and cannot regress. The two drones deliberately run parallel systems:
// no shared mutable state, no refactor of the FPV file.
//
// What it contains:
//   SurvNet       Photon-event networking: remote clients see the drone move and
//                 see it destroyed, and a player can shoot a foreign one.
//   SurvNpcFire   hostile NPCs in a bounded radius fire their real weapon at the
//                 drone (the missing target choice the game will not make for a
//                 non-player).
//   SurvCombat    the Harmony hooks and their one install seam: the NPC-relevance
//                 bubble on NPC_Settlement.HasBesideDistance, and the shot probe
//                 on PlayerFirearmWeaponController.FireOneShot.
//   SurvDiag      diagnostics that separate the five failure modes an in-game
//                 pass must tell apart: rendering/animation, AI activation,
//                 target selection, damage, and destruction.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII-only comments and logs; player-facing strings go through Loc.T.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The surveillance drone's presence on other clients, built exactly like
    /// the FPV drone's Drone.Net: nothing but seven floats go over the wire and
    /// each client builds the model itself, so there is no registered prefab and
    /// no dependency on the master client. A SEPARATE event-code base
    /// (DroneGear/SurveillanceEventCode, default 182) keeps it clear of the FPV
    /// drone's codes (176..180). Several handlers may sit on
    /// PhotonNetwork.OnEventCall at once; this one ignores codes outside its
    /// range, and so does the FPV one.
    /// </summary>
    public static class SurvNet
    {
        public const int Start = 0;   // a drone launched: build the ghost
        public const int Lauf = 1;    // a position update: interpolate the ghost
        public const int Ende = 2;    // the drone is gone: drop the ghost
        public const int Treffer = 3; // a shot went through a foreign drone

        static bool _hooked;
        static bool _failed;
        static MethodInfo _raise;
        static Type _optType;
        static FieldInfo _onEventCall;

        static MethodInfo _playerGet;   // PhotonNetwork.player / LocalPlayer
        static MethodInfo _actorGet;    // PhotonPlayer.ID / ActorNumber
        static bool _actorLookedUp;

        static readonly Dictionary<int, Ghost> _ghosts = new Dictionary<int, Ghost>();

        internal static void AirContacts(List<GepardGun.Contact> air, Vector3 eye, float range)
        {
            if (SurvDrone.Flying) FlakFire.Offer(air, SurvDrone.AirTarget, 3, Mercs.LocalActor, null, eye, range);
            foreach (KeyValuePair<int, Ghost> pair in _ghosts)
                FlakFire.Offer(air, pair.Value.Go, 3, pair.Key, null, eye, range);
        }
        static readonly List<int> _gone = new List<int>();

        /// <summary>A foreign surveillance drone as this client sees it.</summary>
        class Ghost
        {
            public GameObject Go;
            public Vector3 From;
            public Vector3 To;
            public Vector3 Look;
            public float T;         // 0..1 between From and To
            public float Span;      // seconds between two updates
            public float Last;      // Time.time of the last update
        }

        static int Base()
        {
            return DroneGear.CfgSurvEventCode == null ? 182
                 : DroneGear.CfgSurvEventCode.Value;
        }

        static int Code(int art) { return Base() + art; }

        public static void EnsureHooked()
        {
            if (_hooked || _failed) return;
            try
            {
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon == null)
                {
                    _failed = true;
                    RevivalPlugin.L.LogWarning("SurvNet: PhotonNetwork not found - the "
                        + "surveillance drone flies, but no one else sees it.");
                    return;
                }
                _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                _onEventCall = AccessTools.Field(photon, "OnEventCall");
                _optType = RevivalPlugin.TypeByName("RaiseEventOptions");
                if (_raise == null || _onEventCall == null)
                {
                    _failed = true;
                    RevivalPlugin.L.LogWarning("SurvNet: RaiseEvent or OnEventCall "
                        + "missing - the surveillance drone stays invisible to others.");
                    return;
                }

                MethodInfo mine = typeof(SurvNet).GetMethod("OnPhotonEvent",
                    BindingFlags.Public | BindingFlags.Static);
                Delegate handler = Delegate.CreateDelegate(_onEventCall.FieldType, mine);
                Delegate current = _onEventCall.GetValue(null) as Delegate;
                _onEventCall.SetValue(null, Delegate.Combine(current, handler));

                _hooked = true;
                RevivalPlugin.L.LogInfo("SurvNet hooked: event codes " + Code(Start)
                    + "-" + Code(Treffer) + ", received via PhotonNetwork.OnEventCall.");
            }
            catch (Exception ex)
            {
                _failed = true;
                RevivalPlugin.L.LogError("SurvNet not hooked: " + ex);
            }
        }

        public static void Send(int art, Vector3 pos, Vector3 look, float extra,
                                bool reliable)
        {
            if (!_hooked) return;
            try
            {
                float[] data = new float[] {
                    pos.x, pos.y, pos.z, look.x, look.y, look.z, extra };
                object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                _raise.Invoke(null, new object[] {
                    (byte)Code(art), data, reliable, opts });
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("SurvNet send: " + ex.Message);
            }
        }

        /// <summary>Receiver. The signature MUST match PhotonNetwork.EventCallback
        /// (byte, object, int) - Delegate.CreateDelegate checks it at hook time,
        /// so a mismatch shows up then, not in flight.</summary>
        public static void OnPhotonEvent(byte code, object content, int sender)
        {
            try
            {
                int art = code - Base();
                if (art < Start || art > Treffer) return;
                float[] d = content as float[];
                if (d == null || d.Length < 7) return;

                Vector3 pos = new Vector3(d[0], d[1], d[2]);
                Vector3 look = new Vector3(d[3], d[4], d[5]);

                if (art == Ende) { Remove(sender); return; }
                if (art == Treffer)
                {
                    // d[3] carries the owner's actor number where the others carry
                    // a look direction - see SendTreffer.
                    SurvDrone.RemoteHit(sender, pos, (int)d[3], d[6]);
                    return;
                }

                Ghost g;
                if (!_ghosts.TryGetValue(sender, out g))
                {
                    g = new Ghost();
                    g.Go = BuildGhost();
                    g.From = pos;
                    _ghosts[sender] = g;
                    RevivalPlugin.L.LogInfo("SurvNet: foreign surveillance drone from "
                        + "player " + sender + " at " + pos + ".");
                }
                else
                {
                    g.From = g.Go == null ? pos : g.Go.transform.position;
                }
                g.To = pos;
                g.Look = look.sqrMagnitude < 0.000001f ? Vector3.forward : look;
                g.Span = Mathf.Max(0.02f, Time.time - g.Last);
                g.Last = Time.time;
                g.T = 0f;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("SurvNet receive: " + ex.Message);
            }
        }

        /// <summary>Slides each foreign drone between updates and drops any that
        /// have gone quiet - a disconnect leaves no End, so the 4 s timeout is
        /// what removes a ghost whose owner vanished.</summary>
        public static void TickRemotes()
        {
            if (_ghosts.Count == 0) return;
            _gone.Clear();
            foreach (KeyValuePair<int, Ghost> kv in _ghosts)
            {
                Ghost g = kv.Value;
                if (g.Go == null || Time.time - g.Last > 4f) { _gone.Add(kv.Key); continue; }
                g.T = Mathf.Min(1f, g.T + Time.deltaTime / Mathf.Max(0.02f, g.Span));
                g.Go.transform.position = Vector3.Lerp(g.From, g.To, g.T);
                g.Go.transform.rotation = Quaternion.LookRotation(g.Look, Vector3.up);
            }
            for (int i = 0; i < _gone.Count; i++) Remove(_gone[i]);
        }

        /// <summary>
        /// One shot of the local player, measured against every foreign
        /// surveillance drone - the same geometry Drone.Net.Beschuss uses:
        /// closest approach of the aim line to the ghost centre against the
        /// model radius, plus one line-of-sight ray so a wall in between saves
        /// it. On a hit the owner is told (SurvNet.Treffer); the owner applies
        /// the damage, so there is one authority and no double counting.
        /// </summary>
        public static bool Beschuss(Vector3 origin, Vector3 dir, float range, float damage)
        {
            if (_ghosts.Count == 0) return false;
            if (dir.sqrMagnitude < 0.000001f) return false;
            dir.Normalize();
            float r = SurvDrone.HitRadius;

            int who = 0;
            Ghost hitGhost = null;
            float near = float.MaxValue;
            Vector3 point = Vector3.zero;
            foreach (KeyValuePair<int, Ghost> kv in _ghosts)
            {
                Ghost g = kv.Value;
                if (g.Go == null) continue;
                Vector3 to = g.Go.transform.position - origin;
                float t = Vector3.Dot(to, dir);
                if (t < 1f || t > range || t >= near) continue;
                if ((to - dir * t).sqrMagnitude > r * r) continue;
                near = t;
                who = kv.Key;
                hitGhost = g;
                point = g.Go.transform.position;
            }
            if (hitGhost == null) return false;

            Vector3 ignore;
            if (near > 2.2f
                && Turret.RaycastObject(origin + dir, dir, near - 1.2f, out ignore) != null)
                return false;

            SendTreffer(who, point, damage);
            RevivalPlugin.L.LogInfo("SurvNet: hit player " + who
                + "'s surveillance drone at " + Mathf.RoundToInt(near) + " m.");
            return true;
        }

        static void SendTreffer(int target, Vector3 point, float damage)
        {
            Send(Treffer, point, new Vector3(target, 0f, 0f), damage, true);
        }

        static void Remove(int sender)
        {
            Ghost g;
            if (!_ghosts.TryGetValue(sender, out g)) return;
            _ghosts.Remove(sender);
            if (g.Go != null) UnityEngine.Object.Destroy(g.Go);
            RevivalPlugin.L.LogInfo("SurvNet: foreign surveillance drone from player "
                + sender + " is gone.");
        }

        static GameObject BuildGhost()
        {
            GameObject g = Drone.Modell.Bauen();
            g.name = "NDR_SurvDroneRemote";
            float s = DroneGear.CfgSurvModelScale == null
                ? 12f : Mathf.Max(1f, DroneGear.CfgSurvModelScale.Value);
            g.transform.localScale = new Vector3(s, s, s);
            return g;
        }

        /// <summary>The own Photon actor number, read FRESH every time and never
        /// cached: it is handed out per room, so a value kept across a rejoin
        /// would point at another player. -1 means "unknown", and the caller
        /// falls back on distance. Mirrors Drone.MeineNummer so ownership after a
        /// reconnect is always the current one.</summary>
        public static int MyActor()
        {
            try
            {
                if (!_actorLookedUp)
                {
                    _actorLookedUp = true;
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon != null)
                    {
                        _playerGet = AccessTools.PropertyGetter(photon, "player");
                        if (_playerGet == null)
                            _playerGet = AccessTools.PropertyGetter(photon, "LocalPlayer");
                    }
                }
                if (_playerGet == null) return -1;
                object player = _playerGet.Invoke(null, null);
                if (player == null) return -1;
                if (_actorGet == null)
                {
                    _actorGet = AccessTools.PropertyGetter(player.GetType(), "ID");
                    if (_actorGet == null)
                        _actorGet = AccessTools.PropertyGetter(player.GetType(), "ActorNumber");
                }
                if (_actorGet == null) return -1;
                return (int)_actorGet.Invoke(player, null);
            }
            catch { return -1; }
        }
    }

    /// <summary>
    /// Hostile NPCs within a bounded radius answer the surveillance drone with
    /// their real firearm, the same way DroneNpcFire does for the FPV drone: the
    /// game's own target field cannot be used because a drone is not a player, so
    /// this makes the missing target choice and calls the weapon's FireTo with a
    /// world point, preserving muzzle flash, sound, spread, raycast and tracer.
    ///
    /// Only the local drone is considered; its owner holds the hit points, so the
    /// same client rolls the deliberately low hit chance and subtracts a hit
    /// (SurvDrone.NpcHit). Discovered NPCs share SurveillanceNpcShooters and the
    /// same bounded think/LOS budget as the FPV drone.
    /// </summary>
    public static class SurvNpcFire
    {
        static readonly DroneNpcEngagement _engagement = new DroneNpcEngagement(Hit);
        static float _nextPilot;
        static GameObject _pilot;
        static object _pilotTarget;
        static bool _lookedUp, _failed;
        static Type _aiType, _ngsType, _enemyTargetType;
        static FieldInfo _gunField, _sensorField, _initializedField, _networkPlayers;
        static FieldInfo _rateDelay;
        static PropertyInfo _instance;
        static MethodInfo _isEnemy, _isAlive, _fireTo, _getMuzzle;
        static MethodInfo _hasBullets, _cantWork, _bulletsEnded;

        public static void Reset()
        {
            _engagement.Reset();
            _pilot = null; _pilotTarget = null;
            _nextPilot = Time.time + 1f;
            try
            {
                // Resolve native delegates at launch, never in each think.
                if (LookUp())
                {
                    _pilot = FindPilot();
                    if (_pilot != null) _pilotTarget = FindEnemyTarget(_pilot);
                }
            }
            catch (Exception ex) { Fail("initialization: " + ex.Message); }
        }

        static bool Hit(Vector3 shooter)
        {
            SurvDrone.NpcHit(shooter);
            return SurvDrone.Flying;
        }

        public static void Tick()
        {
            if (!SurvDrone.Flying || _failed || Admin.GodModeActive) return;
            if (DroneGear.CfgSurvNpcFire == null || !DroneGear.CfgSurvNpcFire.Value) return;
            try
            {
                if (_pilot == null)
                {
                    if (Time.time < _nextPilot) return;
                    _nextPilot = Time.time + 1f;
                    _pilot = FindPilot();
                    if (_pilot != null) _pilotTarget = FindEnemyTarget(_pilot);
                }
                if (_pilotTarget == null) return;
                _engagement.Tick(_pilotTarget, SurvDrone.Position,
                    DroneGear.CfgSurvNpcFireRange == null ? 140f : Mathf.Max(1f, DroneGear.CfgSurvNpcFireRange.Value),
                    DroneGear.CfgSurvNpcAccuracy == null ? 0.22f : Mathf.Clamp01(DroneGear.CfgSurvNpcAccuracy.Value),
                    DroneGear.CfgSurvNpcShotSeconds == null ? 1.1f : Mathf.Max(0.35f, DroneGear.CfgSurvNpcShotSeconds.Value),
                    DroneGear.CfgSurvNpcShooters == null ? 3 : Mathf.Max(1, DroneGear.CfgSurvNpcShooters.Value));
                SurvDiag.Combat(_engagement.LastInRange, _engagement.LastActive, _engagement.LastLocated);
            }
            catch (Exception ex) { Fail("NPC fire: " + ex.Message); }
        }

        static GameObject FindPilot()
        {
            object server = _instance.GetValue(null, null);
            if (server == null) return null;
            IList players = _networkPlayers.GetValue(server) as IList;
            if (players == null) return null;

            Vector3 home;
            if (!SurvDrone.PilotPos(out home)) home = SurvDrone.Position;

            GameObject best = null;
            float best2 = 0f;
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i] as GameObject;
                if (go == null) continue;
                float d2 = (go.transform.position - home).sqrMagnitude;
                if (best == null || d2 < best2) { best = go; best2 = d2; }
            }
            return best;
        }

        static object FindEnemyTarget(GameObject pilot)
        {
            if (pilot == null || _enemyTargetType == null) return null;
            if (_enemyTargetType == typeof(GameObject)) return pilot;
            if (_enemyTargetType.IsInstanceOfType(pilot)) return pilot;
            if (typeof(Component).IsAssignableFrom(_enemyTargetType))
                return pilot.GetComponent(_enemyTargetType);
            return null;
        }

        static bool LookUp()
        {
            if (_failed) return false;
            if (_lookedUp) return true;
            _lookedUp = true;

            _aiType = RevivalPlugin.TypeByName("NPC_AI2");
            _ngsType = RevivalPlugin.TypeByName("NetworkGameServer");
            if (_aiType == null || _ngsType == null)
                return Fail("NPC_AI2 or NetworkGameServer missing");

            _gunField = AccessTools.Field(_aiType, "_firearmWeaponController");
            _sensorField = AccessTools.Field(_aiType, "SensorIsActive");
            _initializedField = AccessTools.Field(_aiType, "IsInitialized");
            _isEnemy = AccessTools.Method(_aiType, "IsEnemyFraction", null, null);
            _isAlive = AccessTools.Method(_aiType, "IsAlive", Type.EmptyTypes, null);
            _bulletsEnded = AccessTools.Method(_aiType, "OnBulletsEnded", Type.EmptyTypes, null);
            _instance = _ngsType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            _networkPlayers = AccessTools.Field(_ngsType, "NetworkPlayers");
            if (_gunField == null || _isEnemy == null || _isAlive == null
                || _instance == null || _networkPlayers == null)
                return Fail("NPC target fields or player list missing");
            ParameterInfo[] enemyParams = _isEnemy.GetParameters();
            if (enemyParams.Length != 1)
                return Fail("NPC enemy test has " + enemyParams.Length + " parameters");
            _enemyTargetType = enemyParams[0].ParameterType;

            Type gunType = _gunField.FieldType;
            _fireTo = AccessTools.Method(gunType, "FireTo",
                new Type[] { typeof(Vector3), typeof(bool) }, null);
            _getMuzzle = AccessTools.Method(gunType, "GetMuzzlePos", Type.EmptyTypes, null);
            _hasBullets = AccessTools.Method(gunType, "HasBullets", Type.EmptyTypes, null);
            _cantWork = AccessTools.Method(gunType, "CantWorkWeapon", Type.EmptyTypes, null);
            _rateDelay = AccessTools.Field(gunType, "CurrentRateOfFireDelay");
            if (_fireTo == null || _getMuzzle == null || _hasBullets == null
                || _cantWork == null)
                return Fail("NPC firearm methods missing");
            _engagement.Bind(_gunField, _initializedField, _sensorField,
                AccessTools.Field(_aiType, "_mainWeaponId") ?? AccessTools.Field(_aiType, "_weaponId"),
                _rateDelay, _isAlive, _isEnemy, _hasBullets, _cantWork, _getMuzzle, _fireTo, _bulletsEnded);
            return true;
        }

        static bool Fail(string why)
        {
            _failed = true;
            RevivalPlugin.L.LogWarning("SurvDrone: NPC fire disabled - " + why + ".");
            return false;
        }
    }

    /// <summary>
    /// The two Harmony hooks the surveillance drone needs on game code, and their
    /// one install seam.
    ///
    ///   RelevancePostfix  on NPC_Settlement.HasBesideDistance: keeps the NPCs in
    ///                     a BOUNDED bubble around a flying drone awake, so they
    ///                     render, animate and can be hit instead of dropping into
    ///                     the distance T-pose. This is the exact seam the FPV
    ///                     drone uses (RE section 22); saying "the pilot is beside
    ///                     this settlement" lets the game's own OnPlayerEnterZone
    ///                     path re-enable the NPCs and turn them off again by
    ///                     itself when the drone leaves - no standing wake state.
    ///   ShotPostfix       on PlayerFirearmWeaponController.FireOneShot: offers
    ///                     each local shot to the foreign surveillance drones, so
    ///                     a player can shoot one down. A second postfix beside
    ///                     the FPV drone's DroneShotHook - Harmony runs both.
    /// </summary>
    public static class SurvCombat
    {
        const float Self = 2f;   // metres within which a player row IS the pilot
        static FieldInfo _shotCamera;
        static bool _shotCameraLookedUp;

        public static void Install(Harmony harmony)
        {
            if (DroneGear.CfgSurvEnabled != null && !DroneGear.CfgSurvEnabled.Value)
            {
                RevivalPlugin.L.LogInfo("SurvCombat: surveillance drone disabled - "
                    + "relevance and shot hooks not installed.");
                return;
            }
            InstallRelevance(harmony);
            InstallShot(harmony);
        }

        static void InstallRelevance(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("NPC_Settlement");
                MethodInfo m = t == null ? null
                    : AccessTools.Method(t, "HasBesideDistance", null, null);
                if (m == null || m.ReturnType != typeof(bool))
                {
                    RevivalPlugin.L.LogWarning("SurvDrone: NPC_Settlement.HasBesideDistance "
                        + "not found - NPCs stay in T-pose when only the drone is near them.");
                    return;
                }
                harmony.Patch(m, null,
                    new HarmonyMethod(typeof(SurvCombat).GetMethod("RelevancePostfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("SurvDrone: NPCs under the drone stay awake "
                    + "(bounded relevance bubble).");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("SurvDrone: relevance hook not installed - " + ex);
            }
        }

        static void InstallShot(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                MethodInfo m = t == null ? null
                    : AccessTools.Method(t, "FireOneShot", null, null);
                if (m == null)
                {
                    RevivalPlugin.L.LogWarning("SurvDrone: FireOneShot not found - a "
                        + "surveillance drone cannot be shot down by a player.");
                    return;
                }
                harmony.Patch(m, null,
                    new HarmonyMethod(typeof(SurvCombat).GetMethod("ShotPostfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("SurvDrone: player shots probe foreign "
                    + "surveillance drones.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("SurvDrone: shot hook not installed - " + ex);
            }
        }

        /// <summary>__0 = settlement position (y = 0), __1 = player position
        /// (y = 0), __2 = radius squared.</summary>
        public static void RelevancePostfix(Vector3 __0, Vector3 __1, float __2,
                                            ref bool __result)
        {
            if (__result) return;   // already relevant; nothing to do
            if (DroneGear.CfgSurvRelevance == null || !DroneGear.CfgSurvRelevance.Value) return;
            if (!SurvDrone.Flying) return;

            // Only fake the PILOT's own row: OnPlayerEnterZone enables the local
            // visualization for the matched player, and forging a foreign row
            // would give another player NPCs that are not beside him. The recon
            // pilot may WALK, so this uses the live body position, not a cached
            // launch spot.
            Vector3 pilot;
            if (!SurvDrone.PilotPos(out pilot)) return;
            pilot.y = 0f;
            if ((pilot - __1).sqrMagnitude > Self * Self) return;

            Vector3 drone = SurvDrone.Position;
            drone.y = 0f;

            // Bounded bubble: the settlement's own radius, widened to the NPC
            // fire range so NPCs that can shoot the drone are also kept awake,
            // but never past the configured relevance cap - so a far settlement
            // is never woken and the frame rate is not spent on distant NPCs.
            float radius2 = __2;
            if (DroneGear.CfgSurvNpcFire != null && DroneGear.CfgSurvNpcFire.Value
                && DroneGear.CfgSurvNpcFireRange != null)
            {
                float fr = Mathf.Max(0f, DroneGear.CfgSurvNpcFireRange.Value) * DroneNpcAimCore.Metre;
                radius2 = Mathf.Max(radius2, fr * fr);
            }
            float cap = DroneGear.CfgSurvRelevanceRadius == null
                ? 160f : Mathf.Max(10f, DroneGear.CfgSurvRelevanceRadius.Value);
            cap *= DroneNpcAimCore.Metre;
            radius2 = Mathf.Min(radius2, cap * cap);

            if ((drone - __0).sqrMagnitude < radius2)
            {
                __result = true;
                SurvDiag.Relevance();
            }
        }

        public static void ShotPostfix(object __instance)
        {
            try
            {
                if (DroneGear.CfgSurvEnabled != null && !DroneGear.CfgSurvEnabled.Value) return;
                if (__instance == null) return;
                if (Stinger.IsStinger(__instance)) return; // Damage arrives with the missile.
                // The pilot cannot shoot his own drone: while viewing, the body
                // cannot fire at all (DroneInputHook); this is only for shooting
                // OTHER players' surveillance drones, which are the ghosts.
                if (SurvDrone.Viewing) return;

                if (!_shotCameraLookedUp)
                {
                    _shotCameraLookedUp = true;
                    _shotCamera = AccessTools.Field(__instance.GetType(), "MainCamera");
                    if (_shotCamera == null)
                        RevivalPlugin.L.LogWarning("SurvDrone: MainCamera on the weapon "
                            + "controller not found - surveillance drones cannot be shot down.");
                }
                if (_shotCamera == null) return;

                Transform cam = _shotCamera.GetValue(__instance) as Transform;
                if (cam == null) return;
                float range = RevivalPlugin.CfgDroneShootRange == null
                    ? 400f : RevivalPlugin.CfgDroneShootRange.Value;
                SurvNet.Beschuss(cam.position, cam.forward, range, 1f);
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("SurvDrone: shot probe - " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Diagnostics that separate the failure modes an in-game acceptance pass
    /// must tell apart, so the fix can be judged one layer at a time instead of
    /// as one "it does not work":
    ///
    ///   relevance    settlements the bubble kept awake (rendering + animation +
    ///                AI all come back through the game's re-enable path).
    ///   in range     NPCs physically near the drone (rendered/animated presence).
    ///   AI active    of those, how many have a running sensor and are alive
    ///                (AI activation, as opposed to a body standing in T-pose).
    ///   shooters     of those, how many were chosen to fire (target selection).
    ///   damage       each resolved hit and the remaining hit points.
    ///   destroyed    the single destruction event.
    ///
    /// One throttled line per second while a drone flies, plus the per-event
    /// logs in SurvDrone/SurvNpcFire, is enough to point at exactly one layer.
    /// </summary>
    public static class SurvDiag
    {
        static int _wakes;
        static int _inRange, _aiActive, _shooters;
        static int _hits;
        static float _nextLog;

        static bool On
        {
            get
            {
                return DroneGear.CfgSurvDiag == null || DroneGear.CfgSurvDiag.Value;
            }
        }

        public static void Reset()
        {
            _wakes = 0; _inRange = 0; _aiActive = 0; _shooters = 0; _hits = 0;
            _nextLog = 0f;
        }

        public static void Relevance() { _wakes++; }

        public static void Combat(int inRange, int aiActive, int shooters)
        {
            _inRange = inRange; _aiActive = aiActive; _shooters = shooters;
        }

        public static void Damage(float distance, float hpLeft)
        {
            _hits++;
            if (On)
                RevivalPlugin.L.LogInfo("SurvDiag[damage]: hit at "
                    + distance.ToString("0") + " m, hp left " + hpLeft.ToString("0.#")
                    + ", hits total " + _hits + ".");
        }

        public static void Destroyed(string why)
        {
            if (On)
                RevivalPlugin.L.LogInfo("SurvDiag[destroyed]: " + why
                    + " after " + _hits + " hit(s).");
        }

        /// <summary>Called once per flying frame; prints one throttled summary
        /// line per second so the relevance/AI/target-selection split is visible
        /// even when NPC fire is off.</summary>
        public static void Flush()
        {
            if (!On) return;
            if (Time.time < _nextLog) return;
            _nextLog = Time.time + 1f;
            RevivalPlugin.L.LogInfo("SurvDiag: relevance wakes " + _wakes
                + " | NPCs in range " + _inRange
                + " | AI active " + _aiActive
                + " | shooters " + _shooters
                + " | hits " + _hits + ".");
            _wakes = 0;   // per-second rate for the wake counter
        }
    }
}
