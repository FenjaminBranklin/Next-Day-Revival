// Next Day: Survival - Revival Toolkit
//
// NPC DISTANCE TIERS (N2, docs/ai/tasks/n02-npc-distance.md): NPCs visible,
// animated and hittable at aircraft ranges, a forest mask for far viewers.
//
// WHY THEY WERE T-POSED AND UNHITTABLE FROM THE AN-2 (IL, Assembly-CSharp):
//   1. NPC_Settlement.PlayersDistanceControll, every 2 s, measures each
//      network player's BODY (x/z) against CheckPlayersDistRadius through
//      HasBesideDistance. The local body outside it ->
//      CheckVisualizationForLocalPlayer -> NPC_AI2.SetPlayVisualizationValue
//      (false): Anim.enabled = false, RagdollController.SetPhysActive(false)
//      (detectCollisions off on every bone), _colliderMain off, rigidbody
//      asleep. Frozen, and nothing hits a bone any more.
//   2. No player in reach for 5 s -> AutoDisableControl -> SetEnableNpcAi
//      (false) -> NPC_AI2.SetActiveAI(false) -> Animation.Stop(): the legacy
//      Animation drops the skinned mesh into its bind pose, the T-pose.
//   3. CharacterLODController on the NPC model: once a second, from the
//      camera, LOD 0-3 at 25/50/100/350 u (5 prefabs 250 u); past LOD3 EVERY
//      mesh is switched off. 350 u is 125 m.
//   Not causes: ViewDistance (NPC meshes are layer 0 and its prop walk skips
//   Rigidbody subtrees; its layer culls are items/bushes/ragdoll-bone layers),
//   Photon CullArea/NetworkCullingHandler (PUN demo classes, on no NPC),
//   ObjectOptimizer (on no NPC prefab).
//
// WHAT THIS FILE DOES (units: config in real metres, x K = 2.8 u):
//   Wake   P1: only a local aircraft/elevated/optic/gunner viewer enables
//          the middle tier, and only inside that camera's view cone. The
//          per-NPC safety net wakes animation and colliders individually;
//          HasBesideDistance is no longer extended for whole settlements.
//          Inactive viewers drain restoration once, then skip NPC passes.
//   Tiers  every NPC_AI2 (settlement men, convoys, patrols, heli troops,
//          airfield/town defenders, anything spawned later) is sorted by its
//          distance to the camera, at most 16 per frame / 0.1 ms per slice:
//            near  < NearRange: untouched (the game as before).
//            mid   NearRange..wake range: legacy Animation culled by its
//                  renderers (no pose update off screen), skinning with two
//                  bones instead of four, AI Update (owner side) every
//                  AiEveryNthFrame frames while no player stands within
//                  NearRange of him; colliders and damage stay ON. Safety net:
//                  an alive NPC whose settlement did not wake (its centre
//                  outside the range) gets its visualization switched on and
//                  its animation restarted by this file, and off again when he
//                  leaves the tier.
//            far   beyond the wake range: the game as before.
//          CharacterLODController LOD3 is pushed out to the wake range, so
//          the coarsest mesh is drawn as far as the tier reaches.
//   Forest a mask per loaded terrain (main map and east tile) from its tree
//          instances, built once, time-sliced, rebuilt when the tree count
//          changes: a 4 m cell is forest with 2+ trees in its 12 m square and
//          10+ in its 28 m square - clearings, roads and the airfield have
//          neither. An NPC or remote player standing on forest and farther
//          from the camera than that terrain draws trees is not rendered
//          (his renderers off; particles, lines and trails stay). One array
//          lookup per man per pass, no raycasts.
//   Bench  admin panel "NPC tier bench": 4 phases of 2 s settle + 5 s sample
//          (tiered, all full, all mid, all frozen+hidden) -> per-NPC cost of
//          the near and mid tier and the total, in the log and the panel.
//
// Seams: RevivalPlugin.Awake (BindConfig, Install), RevivalPlugin.Update
// (Tick, slot S_NpcDistT), Admin panel (Bench, Status).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class NpcDistance
    {
        /// <summary>World units per real metre (PlayerAn2.K).</summary>
        const float K = 2.8f;

        const byte Near = 0, Mid = 1, Far = 2, None = 255;
        const int PerFrame = 16;
        const float CheckSeconds = 3f;          // safety-net recheck per NPC
        const float HideMarginM = 15f;          // forest: hide past trees + this
        const float BuildSliceMs = 0.15f;        // forest mask build budget

        // ============================================================ config

        static ConfigEntry<float> _cfgNear, _cfgAir, _cfgGround, _cfgElevated;
        static ConfigEntry<int> _cfgAiEvery;
        static ConfigEntry<bool> _cfgForest;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgNear = cfg.Bind("NpcDistance", "NearRange", 150f,
                "Metres. Closer NPCs are left exactly as the game runs them. Between "
                + "this and the wake range they are the middle tier: animated at a "
                + "lower cost, AI thinking less often, still hittable.");
            _cfgAir = cfg.Bind("NpcDistance", "AirRange", 900f,
                "Metres. Aircraft, elevated, scope/binocular and manned-gun viewers "
                + "wake NPCs inside their view cone out to this distance. "
                + "0 = the game's own distance shutdown.");
            _cfgGround = cfg.Bind("NpcDistance", "GroundRange", 400f,
                "Legacy setting, retained for config compatibility. Ground viewers now use "
                + "AirRange only with a scope, binoculars or a manned gun.");
            _cfgElevated = cfg.Bind("NpcDistance", "AirborneHeight", 30f,
                "Metres of camera height over the terrain from which AirRange applies.");
            _cfgAiEvery = cfg.Bind("NpcDistance", "AiEveryNthFrame", 4,
                "Middle tier: the NPC's AI update runs every Nth frame while no player "
                + "stands within NearRange of him. 1 = every frame.");
            _cfgForest = cfg.Bind("NpcDistance", "ForestMask", true,
                "Players and NPCs standing in forest are not drawn for a viewer "
                + "farther away than the trees are drawn (fairness: no man in the open "
                + "where the trees are simply not rendered).");
        }

        // ============================================================ state

        sealed class Hide
        {
            public readonly List<Renderer> Off = new List<Renderer>();
            public bool Active;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public float RefreshAt;
        }

        sealed class Npc
        {
            public Component Ai;
            public int Id;
            public Animation Anim;
            public AnimationCullingType AnimCull;
            public SkinnedMeshRenderer[] Skins;
            public SkinQuality[] Quality;
            public Component Lod;
            public float Lod3;
            public float LodSet = -1f;
            public byte Tier = None;
            public bool Forced;          // visualization switched on by this file
            public bool AnimFrozen;      // bench: Animation disabled by this file
            public float NextCheck;
            public readonly Hide Hide = new Hide();
        }

        sealed class Player
        {
            public GameObject Go;
            public readonly Hide Hide = new Hide();
        }

        static bool _installed;
        static readonly Dictionary<int, Npc> _npcs = new Dictionary<int, Npc>();
        static readonly Dictionary<int, Player> _remote = new Dictionary<int, Player>();
        static readonly HashSet<int> _throttled = new HashSet<int>();
        static readonly List<Vector3> _playerPos = new List<Vector3>();
        static readonly List<GameObject> _players = new List<GameObject>();
        static readonly List<int> _drop = new List<int>();

        static Vector3 _view;
        static bool _haveView;
        static bool _airborne;
        static Vector3 _forward;
        static float _coneCos2;
        static bool _viewerWasActive;
        static int _restoreIndex;
        static readonly List<Npc> _known = new List<Npc>();
        static FieldInfo _aimType, _aimCamera, _aimWeapons, _weaponCategory;
        static bool _scopeViewer;
        static int _scopeFrame = -10;
        static float _nextAirCheck;
        static float _wakeU;             // wake range in u, 0 = off
        static int _aiEvery = 4;

        static Component[] _pass;
        static int _passIndex;
        static float _nextPurge;

        // counters of the last completed pass
        static int _cNear, _cMid, _cFar, _cThrottled, _cForced, _cHiddenNpc, _cHiddenPlayer;
        static int _wNear, _wMid, _wFar, _wThrottled, _wForced, _wHiddenNpc, _wHiddenPlayer;
        static double _tickMs;
        static bool _warned;

        // ======================================================= reflection

        static Type _aiType, _lodType, _vgsType;
        static FieldInfo _fAnim, _fVis, _fMain, _fAdd, _fPose, _fLod3;
        static MethodInfo _mAlive, _mSetVis, _mSwitch;
        static PropertyInfo _ngsInstance;
        static FieldInfo _ngsPlayers;
        static readonly object[] _argTrue = new object[] { true };
        static readonly object[] _argFalse = new object[] { false };
        static readonly object[] _argSwitch = new object[3];

        internal static void Install(Harmony harmony)
        {
            try
            {
                _aiType = RevivalPlugin.TypeByName("NPC_AI2");
                _lodType = RevivalPlugin.TypeByName("CharacterLODController");
                _vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
                Type ngs = RevivalPlugin.TypeByName("NetworkGameServer");
                if (_aiType == null)
                {
                    RevivalPlugin.L.LogWarning("NpcDistance: NPC_AI2 not found - no distance tiers.");
                    return;
                }
                _fAnim = AccessTools.Field(_aiType, "Anim");
                _fVis = AccessTools.Field(_aiType, "IsPlayVisualizationEnabled");
                _fMain = AccessTools.Field(_aiType, "MainState");
                _fAdd = AccessTools.Field(_aiType, "AdditionalState");
                _fPose = AccessTools.Field(_aiType, "PoseState");
                _mAlive = AccessTools.Method(_aiType, "IsAlive", Type.EmptyTypes, null);
                _mSetVis = AccessTools.Method(_aiType, "SetPlayVisualizationValue", null, null);
                _mSwitch = AccessTools.Method(_aiType, "SwitchAnimationByStates", null, null);
                if (_mSwitch != null && _mSwitch.GetParameters().Length != 3) _mSwitch = null;
                if (_lodType != null) _fLod3 = AccessTools.Field(_lodType, "LOD3_Distance");
                if (ngs != null)
                {
                    _ngsInstance = AccessTools.Property(ngs, "Instance");
                    _ngsPlayers = AccessTools.Field(ngs, "NetworkPlayers");
                }

                // IL: AimType 2 is scope; type 1 plus weapon category 8 is binoculars.
                Type aiming = RevivalPlugin.TypeByName("CameraAimingSystem");
                Type weapons = RevivalPlugin.TypeByName("PlayerWeaponsManager");
                _aimType = aiming == null ? null : AccessTools.Field(aiming, "_AimType");
                _aimCamera = aiming == null ? null : AccessTools.Field(aiming, "MainCamera");
                _aimWeapons = aiming == null ? null : AccessTools.Field(aiming, "_plrWpnManager");
                _weaponCategory = weapons == null ? null : AccessTools.Field(weapons, "_WeaponCategoryEquiped");
                MethodInfo aim = aiming == null ? null : AccessTools.Method(aiming, "CameraAimController", Type.EmptyTypes, null);
                if (aim != null && _aimType != null && _aimCamera != null)
                    harmony.Patch(aim, null, new HarmonyMethod(typeof(NpcDistance).GetMethod("AimPostfix")), null, null, null);

                int hooks = 0;
                // Do not extend HasBesideDistance: it wakes the entire settlement,
                // including men behind the viewer. SafetyNet wakes individual men.

                MethodInfo update = AccessTools.DeclaredMethod(_aiType, "Update", Type.EmptyTypes, null);
                if (update != null)
                {
                    harmony.Patch(update,
                        new HarmonyMethod(typeof(NpcDistance).GetMethod("AiUpdatePrefix")),
                        null, null, null, null);
                    hooks++;
                }
                else RevivalPlugin.L.LogWarning("NpcDistance: NPC_AI2.Update not found - "
                    + "the middle tier's AI runs at full rate.");

                _installed = true;
                RevivalPlugin.L.LogInfo("NpcDistance: " + hooks + " AI hook(s); viewer-gated near < "
                    + _cfgNear.Value + " m, viewer wake "
                    + _cfgAir.Value + " m in view, AI every " + _cfgAiEvery.Value
                    + " frame(s) in the middle tier, forest mask " + (_cfgForest.Value ? "on" : "off")
                    + (_fLod3 == null ? "; CharacterLODController.LOD3_Distance missing" : "") + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("NpcDistance: not installed - " + ex);
            }
        }

        // ============================================================ hooks

        /// <summary>Middle tier: the AI update of a man no player is near runs
        /// every Nth frame (staggered by instance id). Bench "frozen": never.</summary>
        public static bool AiUpdatePrefix(object __instance)
        {
            if (_bench == BenchFrozen) return false;
            if (_throttled.Count == 0) return true;
            UnityEngine.Object o = __instance as UnityEngine.Object;
            if (o == null) return true;
            int id = o.GetInstanceID();
            if (!_throttled.Contains(id)) return true;
            int every = _aiEvery < 1 ? 1 : _aiEvery;
            return ((Time.frameCount + (id & 0x7fffffff)) % every) == 0;
        }

        // ============================================================ tick

        internal static void Tick()
        {
            if (!_installed) return;
            long t0 = Stopwatch.GetTimestamp();
            try { Step(); }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; RevivalPlugin.L.LogWarning("NpcDistance: " + ex); }
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _tickMs += (ms - _tickMs) * 0.05;
            if (_bench != BenchNone && _benchSampling) _benchOwn += ms;
        }

        public static void AimPostfix(object __instance)
        {
            FrameProf.S(FrameProf.S_NpcDistT);
            try
            {
                if (_aimCamera.GetValue(__instance) as Camera != CameraOwner.MainCamera()) return;
                int mode = FastField.GetInt(_aimType, __instance);
                object weapons = _aimWeapons == null ? null : _aimWeapons.GetValue(__instance);
                _scopeViewer = mode == 2 || (mode == 1 && weapons != null
                    && _weaponCategory != null && FastField.GetInt(_weaponCategory, weapons) == 8);
                _scopeFrame = Time.frameCount;
            }
            finally { FrameProf.E(FrameProf.S_NpcDistT); }
        }

        static bool InViewCone(Vector3 delta)
        {
            float dot = Vector3.Dot(delta, _forward);
            return dot > 0f && dot * dot >= delta.sqrMagnitude * _coneCos2;
        }

        static void Step()
        {
            Camera cam = CameraOwner.ViewCamera();
            _haveView = cam != null;
            if (_haveView)
            {
                _view = cam.transform.position;
                _forward = cam.transform.forward;
                float half = cam.fieldOfView * Mathf.Deg2Rad * 0.5f;
                float diagonal = Mathf.Atan(Mathf.Tan(half) * Mathf.Sqrt(1f + cam.aspect * cam.aspect));
                float cosine = Mathf.Cos(Mathf.Min(89f * Mathf.Deg2Rad, diagonal + 5f * Mathf.Deg2Rad));
                _coneCos2 = cosine * cosine;
            }
            _aiEvery = Mathf.Max(1, _cfgAiEvery.Value);
            if (_haveView && Time.time >= _nextAirCheck)
            {
                _nextAirCheck = Time.time + 0.5f;
                _airborne = HeightOverTerrain(_view) > Mathf.Max(0f, _cfgElevated.Value) * K;
            }
            int owner = CameraOwner.Owner;
            bool viewer = _haveView && (_airborne || PlayerAn2.Aboard || PlayerHeli.Aboard
                || Drone.Flying || (_scopeViewer && Time.frameCount - _scopeFrame <= 1)
                || owner == CameraOwner.Turm || owner == CameraOwner.GunTruck
                || owner == CameraOwner.Gepard || owner == CameraOwner.Flak || Katyusha.Aiming
                // P1b: the surveillance drone and the aircraft owners in their own
                // right (a camera handover can precede Aboard), and a manned gun
                // whose TakeCamera is off.
                || owner == CameraOwner.Aufklaerer || owner == CameraOwner.Drohne
                || owner == CameraOwner.An2 || owner == CameraOwner.Heli
                || TechnicalGun.Manning || GepardGun.Manning);
            float m = viewer ? _cfgAir.Value : 0f;
            _wakeU = m <= 0f ? 0f : Mathf.Max(m, _cfgNear.Value) * K;
            PumpMask();
            BenchStep();

            if (!viewer && _bench == BenchNone)
            {
                if (_viewerWasActive) { _restoreIndex = 0; _viewerWasActive = false; }
                // Restore only objects we changed. Once drained, idle is O(1).
                int stop = Mathf.Min(_known.Count, _restoreIndex + PerFrame);
                for (; _restoreIndex < stop; _restoreIndex++) Apply(_known[_restoreIndex], false);
                if (_restoreIndex == _known.Count && _pass != null)
                {
                    for (int i = 0; i < _players.Count; i++) ProcessPlayer(_players[i]);
                    _pass = null;
                }
                return;
            }
            _viewerWasActive = true;
            if (_pass == null || _passIndex >= _pass.Length)
            {
                EndPass();
                _pass = NpcScan.All();
                _passIndex = 0;
                GatherPlayers();
            }
            int end = Mathf.Min(_pass.Length, _passIndex + PerFrame);
            long start = Stopwatch.GetTimestamp();
            long budget = Stopwatch.Frequency / 10000; // 0.1 ms, cooperative between NPCs.
            for (; _passIndex < end; _passIndex++)
            {
                Process(_pass[_passIndex]);
                if (Stopwatch.GetTimestamp() - start >= budget) { _passIndex++; break; }
            }
        }

        static void EndPass()
        {
            if (_pass != null)
            {
                for (int i = 0; i < _players.Count; i++) ProcessPlayer(_players[i]);
                _cNear = _wNear; _cMid = _wMid; _cFar = _wFar; _cThrottled = _wThrottled;
                _cForced = _wForced; _cHiddenNpc = _wHiddenNpc; _cHiddenPlayer = _wHiddenPlayer;
            }
            _wNear = _wMid = _wFar = _wThrottled = _wForced = _wHiddenNpc = _wHiddenPlayer = 0;
            if (Time.time >= _nextPurge) { _nextPurge = Time.time + 5f; Purge(); }
        }

        static void Purge()
        {
            _drop.Clear();
            foreach (KeyValuePair<int, Npc> e in _npcs)
                if (e.Value.Ai == null) _drop.Add(e.Key);
            for (int i = 0; i < _drop.Count; i++) { _npcs.Remove(_drop[i]); _throttled.Remove(_drop[i]); }
            for (int i = _known.Count - 1; i >= 0; i--)
                if (_known[i].Ai == null) _known.RemoveAt(i);
            _drop.Clear();
            foreach (KeyValuePair<int, Player> e in _remote)
                if (e.Value.Go == null) _drop.Add(e.Key);
            for (int i = 0; i < _drop.Count; i++) _remote.Remove(_drop[i]);
        }

        /// <summary>Every network player's object (NetworkGameServer.
        /// NetworkPlayers) - AI throttling looks at all of them, the forest
        /// mask at the remote ones.</summary>
        static void GatherPlayers()
        {
            _players.Clear();
            _playerPos.Clear();
            try
            {
                object ngs = _ngsInstance == null ? null : _ngsInstance.GetValue(null, null);
                IList list = ngs == null || _ngsPlayers == null ? null : _ngsPlayers.GetValue(ngs) as IList;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    GameObject go = list[i] as GameObject;
                    if (go == null) continue;
                    _players.Add(go);
                    _playerPos.Add(go.transform.position);
                }
            }
            catch { }
        }

        // ============================================================ NPCs

        static Npc Make(Component ai, int id)
        {
            Npc n = new Npc();
            n.Ai = ai;
            n.Id = id;
            n.Anim = _fAnim == null ? null : _fAnim.GetValue(ai) as Animation;
            if (n.Anim == null) n.Anim = ai.GetComponentInChildren<Animation>();
            if (n.Anim != null) n.AnimCull = n.Anim.cullingType;
            n.Skins = ai.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            n.Quality = new SkinQuality[n.Skins.Length];
            for (int i = 0; i < n.Skins.Length; i++) n.Quality[i] = n.Skins[i].quality;
            if (_lodType != null && _fLod3 != null)
            {
                n.Lod = ai.GetComponentInChildren(_lodType);
                if (n.Lod != null) n.Lod3 = FastField.GetFloat(_fLod3, n.Lod);
            }
            return n;
        }

        static void Process(Component ai)
        {
            if (ai == null) return;
            int id = ai.GetInstanceID();
            Npc n;
            if (!_npcs.TryGetValue(id, out n)) { n = Make(ai, id); _npcs.Add(id, n); _known.Add(n); }
            Apply(n, true);
        }

        static void Apply(Npc n, bool count)
        {
            Component ai = n.Ai;
            if (ai == null) return;
            Vector3 p = ai.transform.position;
            float d = (p - _view).magnitude;
            float near = _cfgNear.Value * K;
            byte tier = d < near ? Near : _wakeU > 0f && d < _wakeU && InViewCone(p - _view) ? Mid : Far;
            if (_bench == BenchFull) tier = Near;
            else if (_bench == BenchMid) tier = Mid;
            else if (_bench == BenchFrozen) tier = Far;

            // The coarsest mesh as far as the tier reaches (vanilla: 350 u).
            if (n.Lod != null)
            {
                float want = tier == Mid ? Mathf.Max(n.Lod3, _wakeU) : n.Lod3;
                if (Mathf.Abs(want - n.LodSet) > 1f)
                {
                    FastField.SetFloat(_fLod3, n.Lod, want);
                    n.LodSet = want;
                }
            }

            if (tier != n.Tier)
            {
                if (tier == Mid) Cheap(n); else Restore(n);
                if (tier == Far && n.Forced) Unforce(n);
                n.Tier = tier;
                n.NextCheck = 0f;
            }

            bool throttle;
            if (_bench == BenchNone) throttle = tier == Mid && _aiEvery > 1 && NoPlayerNear(p, near);
            else throttle = _bench == BenchMid;
            if (throttle) _throttled.Add(n.Id); else _throttled.Remove(n.Id);

            if (tier == Mid && Time.time >= n.NextCheck)
            {
                n.NextCheck = Time.time + CheckSeconds;
                SafetyNet(n);
            }

            if (_bench == BenchFrozen)
            {
                if (n.Anim != null && n.Anim.enabled) { n.Anim.enabled = false; n.AnimFrozen = true; }
            }
            else if (n.AnimFrozen)
            {
                n.AnimFrozen = false;
                if (n.Anim != null) n.Anim.enabled = true;
            }

            bool hide = _bench == BenchFrozen || (_wakeU > 0f && InForest(p, d, n.Hide.Active));
            if (hide && !n.Hide.Active && _bench != BenchFrozen && InVehicle(ai)) hide = false;
            SetHidden(n.Hide, ai.gameObject, hide);

            if (!count) return;
            if (tier == Near) _wNear++; else if (tier == Mid) _wMid++; else _wFar++;
            if (throttle) _wThrottled++;
            if (n.Forced) _wForced++;
            if (n.Hide.Active) _wHiddenNpc++;
        }

        static void Cheap(Npc n)
        {
            if (n.Anim != null) n.Anim.cullingType = AnimationCullingType.BasedOnRenderers;
            for (int i = 0; i < n.Skins.Length; i++)
            {
                SkinnedMeshRenderer s = n.Skins[i];
                if (s == null) continue;
                SkinQuality q = n.Quality[i];
                if (q == SkinQuality.Auto || q == SkinQuality.Bone4) s.quality = SkinQuality.Bone2;
            }
        }

        static void Restore(Npc n)
        {
            if (n.Anim != null) n.Anim.cullingType = n.AnimCull;
            for (int i = 0; i < n.Skins.Length; i++)
                if (n.Skins[i] != null) n.Skins[i].quality = n.Quality[i];
        }

        /// <summary>An alive mid-tier man whose settlement did not wake (its
        /// centre outside the range, or no settlement on this client): collider,
        /// ragdoll and animation on - the game's own switch - and the animation
        /// restarted from his current state, never the bind pose.</summary>
        static void SafetyNet(Npc n)
        {
            if (_bench != BenchNone) return;
            Component ai = n.Ai;
            if (_mAlive == null || !FastCall.Bool(_mAlive, ai)) return;
            if (_fVis != null && _mSetVis != null && !FastField.GetBool(_fVis, ai))
            {
                _mSetVis.Invoke(ai, _argTrue);
                n.Forced = true;
            }
            Animation a = n.Anim;
            if (a != null && (!a.enabled || !a.isPlaying))
            {
                a.enabled = true;
                bool played = false;
                if (_mSwitch != null && _fMain != null && _fAdd != null && _fPose != null)
                {
                    try
                    {
                        // P1b: one argument array for every wake, not one per call.
                        _argSwitch[0] = _fMain.GetValue(ai);
                        _argSwitch[1] = _fAdd.GetValue(ai);
                        _argSwitch[2] = _fPose.GetValue(ai);
                        _mSwitch.Invoke(ai, _argSwitch);
                        played = a.isPlaying;
                    }
                    catch { }
                }
                if (!played && a.clip != null) a.Play();
            }
        }

        /// <summary>Hands the visualization back to the game's "not around"
        /// state when a man this file woke leaves for the far tier.</summary>
        static void Unforce(Npc n)
        {
            n.Forced = false;
            try
            {
                if (_mSetVis != null && n.Ai != null && _mAlive != null && FastCall.Bool(_mAlive, n.Ai))
                    _mSetVis.Invoke(n.Ai, _argFalse);
            }
            catch { }
        }

        static bool NoPlayerNear(Vector3 p, float near)
        {
            float n2 = near * near;
            if ((p - _view).sqrMagnitude < n2) return false;
            for (int i = 0; i < _playerPos.Count; i++)
                if ((p - _playerPos[i]).sqrMagnitude < n2) return false;
            return true;
        }

        static bool InVehicle(Component c)
        {
            return _vgsType != null && c.GetComponentInParent(_vgsType) != null;
        }

        // ========================================================== players

        static void ProcessPlayer(GameObject go)
        {
            if (go == null) return;
            GameObject me = MapTools.LocalPlayer();
            if (go == me) return;
            int id = go.GetInstanceID();
            Player pl;
            if (!_remote.TryGetValue(id, out pl)) { pl = new Player(); pl.Go = go; _remote.Add(id, pl); }
            Vector3 p = go.transform.position;
            bool hide = _wakeU > 0f && _bench == BenchNone && InForest(p, (p - _view).magnitude, pl.Hide.Active);
            SetHidden(pl.Hide, go, hide);
            if (pl.Hide.Active) _wHiddenPlayer++;
        }

        // ============================================================ hiding

        static bool InForest(Vector3 p, float d, bool hidden)
        {
            if (!_cfgForest.Value || _masks.Count == 0) return false;
            Mask m = MaskAt(p);
            if (m == null || m.Terrain == null) return false;
            float trees = m.Terrain.treeDistance;
            float edge = hidden ? trees : trees + HideMarginM * K;
            return d > edge && m.Forest(p);
        }

        static void SetHidden(Hide h, GameObject root, bool hide)
        {
            if (hide)
            {
                // Re-apply to cached renderers: character LOD can enable them again.
                // Refresh clothing/weapon membership at low frequency, not per pass.
                if (!h.Active || Time.time >= h.RefreshAt)
                {
                    h.RefreshAt = Time.time + 1f;
                    h.Renderers.Clear();
                    root.GetComponentsInChildren<Renderer>(true, h.Renderers);
                }
                for (int i = 0; i < h.Renderers.Count; i++)
                {
                    Renderer r = h.Renderers[i];
                    if (r == null || !r.enabled) continue;
                    if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                    r.enabled = false;
                    if (!h.Off.Contains(r)) h.Off.Add(r);
                }
                h.Active = true;
            }
            else if (h.Active)
            {
                for (int i = 0; i < h.Off.Count; i++)
                    if (h.Off[i] != null) h.Off[i].enabled = true;
                h.Off.Clear();
                h.Active = false;
            }
        }

        // ============================================================ forest mask

        internal sealed class Mask
        {
            public Terrain Terrain;
            public float X0, Z0, Cell;
            public int W, H, Cells;
            public byte[] Bits;
            /// <summary>Average top of the counted trees over the ground, u
            /// (0 = unknown). Was N2b's canopy height; P3 does not use it.</summary>
            public float TreeTop;

            public bool At(int x, int z)
            {
                if (x < 0 || z < 0 || x >= W || z >= H) return false;
                int i = z * W + x;
                return (Bits[i >> 3] & (1 << (i & 7))) != 0;
            }

            public bool Contains(Vector3 p)
            {
                return p.x >= X0 && p.z >= Z0 && p.x < X0 + W * Cell && p.z < Z0 + H * Cell;
            }

            public bool Forest(Vector3 p)
            {
                int x = (int)((p.x - X0) / Cell), z = (int)((p.z - Z0) / Cell);
                if (x < 0 || z < 0 || x >= W || z >= H) return false;
                int i = z * W + x;
                return (Bits[i >> 3] & (1 << (i & 7))) != 0;
            }
        }

        static List<Mask> _masks = new List<Mask>();
        static int _maskVersion, _pumpFrame = -1;
        static IEnumerator _build;
        static int _maskSig;
        static float _nextSigCheck;
        static readonly Stopwatch _sw = new Stopwatch();

        static Mask MaskAt(Vector3 p)
        {
            for (int i = 0; i < _masks.Count; i++)
                if (_masks[i].Terrain != null && _masks[i].Contains(p)) return _masks[i];
            return null;
        }

        /// <summary>The finished masks (swapped whole, never edited) and a
        /// counter that moves on every swap. Shared with FarForest (N2b), so
        /// the far canopy and the forest hiding use the same cells.</summary>
        internal static List<Mask> Masks { get { return _masks; } }
        internal static int MaskVersion { get { return _maskVersion; } }

        /// <summary>One mask build slice per frame, whoever asks first (this
        /// file's Tick, or FarForest when the tiers are not installed).</summary>
        internal static void PumpMask()
        {
            if (_pumpFrame == Time.frameCount) return;
            _pumpFrame = Time.frameCount;
            ForestStep();
        }

        static void ForestStep()
        {
            if ((_cfgForest == null || !_cfgForest.Value) && !FarForest.Wanted) return;
            if (_build == null && Time.time >= _nextSigCheck)
            {
                _nextSigCheck = Time.time + 10f;
                Terrain[] all = Terrain.activeTerrains;
                int sig = 17;
                for (int i = 0; all != null && i < all.Length; i++)
                {
                    if (all[i] == null || all[i].terrainData == null) continue;
                    sig = sig * 31 + all[i].terrainData.GetInstanceID();
                    sig = sig * 31 + all[i].terrainData.treeInstanceCount;
                }
                if (sig != _maskSig)
                {
                    _maskSig = sig;
                    _build = Build(all);
                }
            }
            if (_build == null) return;
            _sw.Reset();
            _sw.Start();
            bool more;
            try { more = _build.MoveNext(); }
            catch (Exception ex)
            {
                more = false;
                RevivalPlugin.L.LogWarning("NpcDistance: forest mask build failed - " + ex.Message);
            }
            _sw.Stop();
            if (!more) _build = null;
        }

        static bool OverBudget() { return _sw.Elapsed.TotalMilliseconds > BuildSliceMs; }

        /// <summary>The masks of every loaded terrain; swapped in when all are
        /// done. Yields whenever the frame's slice is used up.</summary>
        static IEnumerator Build(Terrain[] all)
        {
            List<Mask> made = new List<Mask>();
            float started = Time.realtimeSinceStartup;
            int frames = 0;
            for (int t = 0; all != null && t < all.Length; t++)
            {
                Terrain terrain = all[t];
                if (terrain == null || terrain.terrainData == null) continue;
                TerrainData data = terrain.terrainData;
                Vector3 org = terrain.GetPosition();
                Vector3 size = data.size;
                Mask m = new Mask();
                m.Terrain = terrain;
                m.X0 = org.x;
                m.Z0 = org.z;
                m.Cell = Mathf.Max(4f * K, Mathf.Max(size.x, size.z) / 1024f);
                m.W = Mathf.Max(1, Mathf.CeilToInt(size.x / m.Cell));
                m.H = Mathf.Max(1, Mathf.CeilToInt(size.z / m.Cell));
                byte[] count = new byte[m.W * m.H];

                // Which prototypes are trees: rocks, grass and bushes are not,
                // nor anything under 4 m.
                TreePrototype[] protos = data.treePrototypes;
                float[] height = new float[protos == null ? 0 : protos.Length];
                for (int p = 0; p < height.Length; p++) height[p] = TreeHeight(protos[p]);

                // One tree at a time: the treeInstances getter copies the whole
                // array (tens of thousands) inside one frame.
                int trees = data.treeInstanceCount;
                float minTall = 4f * K;
                double topSum = 0.0;
                int topN = 0;
                for (int i = 0; i < trees; i++)
                {
                    if ((i & 511) == 0 && OverBudget())
                    {
                        frames++;
                        yield return null;
                        if (terrain == null || data.treeInstanceCount != trees) break;
                    }
                    TreeInstance ti = data.GetTreeInstance(i);
                    int pi = ti.prototypeIndex;
                    if (pi < 0 || pi >= height.Length || height[pi] < 0f) continue;
                    if (height[pi] > 0f && height[pi] * ti.heightScale < minTall) continue;
                    if (height[pi] > 0f) { topSum += height[pi] * ti.heightScale; topN++; }
                    int x = (int)(ti.position.x * size.x / m.Cell);
                    int z = (int)(ti.position.z * size.z / m.Cell);
                    if (x < 0 || z < 0 || x >= m.W || z >= m.H) continue;
                    int k = z * m.W + x;
                    if (count[k] < 255) count[k]++;
                }

                if (terrain == null) continue;      // unloaded mid-build

                // Forest: 2+ trees in the ~12 m square, 10+ in the ~28 m square
                // (thresholds scaled when the cell is coarser than 4 m).
                int r1 = Mathf.Max(1, Mathf.RoundToInt(6f * K / m.Cell - 0.5f));
                int r2 = Mathf.Max(r1 + 1, Mathf.RoundToInt(14f * K / m.Cell - 0.5f));
                float a1 = (2 * r1 + 1) * m.Cell / (12f * K), a2 = (2 * r2 + 1) * m.Cell / (28f * K);
                int min1 = Mathf.Max(2, Mathf.RoundToInt(2f * a1 * a1));
                int min2 = Mathf.Max(min1 + 1, Mathf.RoundToInt(10f * a2 * a2));
                m.TreeTop = topN > 0 ? (float)(topSum / topN) : 0f;
                m.Bits = new byte[(m.W * m.H + 7) / 8];
                for (int z = 0; z < m.H; z++)
                {
                    if (OverBudget()) { frames++; yield return null; }
                    for (int x = 0; x < m.W; x++)
                    {
                        int s1 = Sum(count, m.W, m.H, x, z, r1);
                        if (s1 < min1) continue;
                        if (Sum(count, m.W, m.H, x, z, r2) < min2) continue;
                        int i = z * m.W + x;
                        m.Bits[i >> 3] |= (byte)(1 << (i & 7));
                        m.Cells++;
                    }
                }
                made.Add(m);
                RevivalPlugin.L.LogInfo("NpcDistance: forest mask " + terrain.name + " "
                    + m.W + "x" + m.H + " cells of " + (m.Cell / K).ToString("0.0", CultureInfo.InvariantCulture)
                    + " m, " + (100.0 * m.Cells / Math.Max(1, m.W * m.H)).ToString("0.0", CultureInfo.InvariantCulture)
                    + " % forest.");
            }
            _masks = made;
            _maskVersion++;
            RevivalPlugin.L.LogInfo("NpcDistance: " + made.Count + " forest mask(s) in "
                + (Time.realtimeSinceStartup - started).ToString("0.0", CultureInfo.InvariantCulture)
                + " s over " + (frames + 1) + " frame(s).");
        }

        static int Sum(byte[] c, int w, int h, int x, int z, int r)
        {
            int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
            int z0 = Math.Max(0, z - r), z1 = Math.Min(h - 1, z + r);
            int s = 0;
            for (int zz = z0; zz <= z1; zz++)
            {
                int row = zz * w;
                for (int xx = x0; xx <= x1; xx++) s += c[row + xx];
            }
            return s;
        }

        /// <summary>Prototype height in u; -1 = not a tree, 0 = unknown (counted).</summary>
        static float TreeHeight(TreePrototype proto)
        {
            GameObject prefab = proto == null ? null : proto.prefab;
            if (prefab == null) return 0f;
            string name = prefab.name.ToLowerInvariant();
            if (name.IndexOf("rock") >= 0 || name.IndexOf("stone") >= 0 || name.IndexOf("grass") >= 0
                || name.IndexOf("kamen") >= 0 || name.IndexOf("bush") >= 0 || name.IndexOf("kust") >= 0)
                return -1f;
            float top = 0f;
            MeshFilter[] meshes = prefab.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] == null || meshes[i].sharedMesh == null) continue;
                Bounds b = meshes[i].sharedMesh.bounds;
                top = Mathf.Max(top, b.max.y * meshes[i].transform.lossyScale.y);
            }
            return top;
        }

        static float HeightOverTerrain(Vector3 p)
        {
            Terrain[] all = Terrain.activeTerrains;
            for (int i = 0; all != null && i < all.Length; i++)
            {
                Terrain t = all[i];
                if (t == null || t.terrainData == null) continue;
                Vector3 o = t.GetPosition(), s = t.terrainData.size;
                if (p.x < o.x || p.z < o.z || p.x >= o.x + s.x || p.z >= o.z + s.z) continue;
                return p.y - (o.y + t.SampleHeight(p));
            }
            return 0f;
        }

        // ============================================================ bench

        const int BenchNone = 0, BenchTiered = 1, BenchFull = 2, BenchMid = 3, BenchFrozen = 4;
        static readonly string[] BenchNames = { "", "tiered", "all full", "all mid", "all frozen" };
        const float BenchSettle = 2f, BenchSample = 5f;

        static int _bench = BenchNone;
        static bool _benchSampling;
        static float _benchPhaseStart;
        static int _benchFrames;
        static double _benchMs, _benchOwn;
        static readonly double[] _benchAvg = new double[5];
        static int _benchNpcs, _benchNear, _benchMidC, _benchFar;
        static string _benchResult;

        /// <summary>Admin panel: runs the four phases here, ~28 s.</summary>
        internal static string Bench()
        {
            if (!_installed) return "NPC distance tiers are not installed (see log).";
            if (_bench != BenchNone) return "NPC tier bench already running (" + BenchNames[_bench] + ").";
            int alive = 0;
            foreach (KeyValuePair<int, Npc> e in _npcs) if (e.Value.Ai != null) alive++;
            if (alive == 0) return "No NPC loaded - fly or walk to NPCs first.";
            _benchNpcs = alive;
            _benchNear = _cNear; _benchMidC = _cMid; _benchFar = _cFar;
            _benchResult = null;
            SetBenchPhase(BenchTiered);
            RevivalPlugin.L.LogInfo("[NpcDistance] bench start: " + alive + " NPC(s), near "
                + _cNear + " / mid " + _cMid + " / far " + _cFar + ". Hold still ~28 s.");
            return "NPC tier bench started: " + alive + " NPCs, hold still ~28 s.";
        }

        static void SetBenchPhase(int phase)
        {
            _bench = phase;
            _benchSampling = false;
            _benchPhaseStart = Time.realtimeSinceStartup;
            _benchFrames = 0;
            _benchMs = 0.0;
            _benchOwn = 0.0;
            // Apply the phase to every NPC now instead of over a whole pass.
            foreach (KeyValuePair<int, Npc> e in _npcs) Apply(e.Value, false);
        }

        static void BenchStep()
        {
            if (_bench == BenchNone) return;
            float age = Time.realtimeSinceStartup - _benchPhaseStart;
            if (!_benchSampling)
            {
                if (age >= BenchSettle) _benchSampling = true;
                return;
            }
            _benchFrames++;
            _benchMs += Time.unscaledDeltaTime * 1000.0;
            if (age < BenchSettle + BenchSample) return;
            double avg = _benchMs / Math.Max(1, _benchFrames);
            _benchAvg[_bench] = avg;
            RevivalPlugin.L.LogInfo("[NpcDistance] bench " + BenchNames[_bench] + ": "
                + F2(avg) + " ms/frame (" + F1(1000.0 / Math.Max(0.01, avg)) + " FPS) over "
                + _benchFrames + " frames, this file " + F2(_benchOwn / Math.Max(1, _benchFrames)) + " ms.");
            if (_bench < BenchFrozen) { SetBenchPhase(_bench + 1); return; }
            SetBenchPhase(BenchNone);
            double baseMs = _benchAvg[BenchFrozen];
            double perNear = (_benchAvg[BenchFull] - baseMs) / _benchNpcs;
            double perMid = (_benchAvg[BenchMid] - baseMs) / _benchNpcs;
            double total = _benchAvg[BenchTiered] - baseMs;
            _benchResult = "NPC bench (" + _benchNpcs + " NPCs, near/mid/far " + _benchNear + "/"
                + _benchMidC + "/" + _benchFar + "): near " + F3(perNear) + " ms/NPC, mid "
                + F3(perMid) + " ms/NPC, far ~0; tiered total " + F2(total) + " ms (all full "
                + F2(_benchAvg[BenchFull] - baseMs) + " ms).";
            RevivalPlugin.L.LogInfo("[NpcDistance] " + _benchResult
                + " Frame ms: tiered " + F2(_benchAvg[BenchTiered]) + ", all full " + F2(_benchAvg[BenchFull])
                + ", all mid " + F2(_benchAvg[BenchMid]) + ", frozen " + F2(baseMs) + ".");
        }

        static string F1(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        static string F3(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        /// <summary>One line for the admin panel.</summary>
        internal static string Status()
        {
            if (!_installed) return "NPC tiers: not installed";
            if (_bench != BenchNone) return "NPC tier bench: " + BenchNames[_bench] + " ...";
            if (_benchResult != null) return _benchResult;
            StringBuilder sb = new StringBuilder();
            sb.Append("NPC tiers near/mid/far ").Append(_cNear).Append('/').Append(_cMid).Append('/').Append(_cFar)
              .Append(", AI slow ").Append(_cThrottled).Append(", woken ").Append(_cForced)
              .Append(", forest-hidden ").Append(_cHiddenNpc).Append(" NPC ").Append(_cHiddenPlayer)
              .Append(" pl, ").Append(F2(_tickMs)).Append(" ms, wake ")
              .Append((_wakeU / K).ToString("0", CultureInfo.InvariantCulture)).Append(" m");
            return sb.ToString();
        }
    }
}
