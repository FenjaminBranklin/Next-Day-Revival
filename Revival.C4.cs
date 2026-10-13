// Next Day: Survival - Revival Toolkit
//
// C4 demolition charge (1493), remote detonator (1494) and defuse kit (2078).
// The rules live in Revival.C4Core.cs (tested headless); this file is the game
// side. Design of record: docs/ai/POWER.md, section "C4".
//
// THE HAND. C4 and the detonator sit in the grenade slot (only 1401..1500
// opens it, the AP mine's rule); their grenade record is the frag grenade 1403
// cloned on every lookup (C4DataHook). While C4 is in the hand a
// semi-transparent copy of the charge follows the surface the camera looks at,
// aligned to it - green when it may go there, red when not (too far, a person,
// the per-player limit). The mouse wheel walks the timer (remote only, 10 s ..
// 5 min). Left click (CantThrowGrenade, as the AP mine) places it. The
// detonator's left click fires every own charge within radio range.
//
// STICKING. A charge on a vehicle or an An-2 (anything with a PhotonView and
// a VehicleGameSystem, or a PlayerAn2 plane) is parented to that object by its
// view id, so it rides along on every client. A charge on a registered target
// (RegisterTarget: the POWER project's generator, fuel tanks, any later site)
// is parented to the target's root by its key. Anything else (AA guns, walls,
// ground) is static and kept in world space.
//
// AUTHORITY. The master owns every charge. A placement is an ask (PlaceAsk);
// the master checks the per-player limit, gives the charge its id and tells
// everyone (Spawn). Timers run on the master; the remote detonator and the
// defuse are asks too. Whatever goes off is announced (Gone) and the master
// alone deals the damage: people through OrdnanceBlast (lethal close, linear
// to zero at the radius), AA guns/radar/ammo depot through
// AirDefenceDamage.ReportBlast, vehicles, An-2s and registered targets by
// charge shares (C4Core.ChargeShare; a charge stuck on the object is one full
// charge). The visible explosion is the networked native one
// (RocketHook.Detonate with zero damage). Late joiners ask for a snapshot.
// No alarm is raised anywhere when a charge is placed.
//
// NETWORK. The packets ride the shared float channel of FlakNet (kinds
// 40..46, 15 floats), the same channel as the AA damage and the ammo depot -
// no new Photon event code, and sending reuses one array.
//
// PERFORMANCE. Nothing per frame allocates: charges live in a fixed array,
// the preview raycast uses a reused hit buffer, the equipped item is read
// four times a second, HUD strings change only with the timer step. With no
// charge placed and no C4 in the hand the tick is a few compares
// (F6: C4.Tick / C4.Draw).
//
// C# 3.0. UTF-8 without BOM: the Russian strings are real Cyrillic.
//
// SEAMS OUTSIDE THIS FILE (marked "NDR C4"):
//   RevivalPlugin.cs BindConfig / Install / BuildItemTable / Update / OnGUI
//   Revival.Items.cs ShopItemIds/ShopBuyPrices (1493, 1494, 2078)
//   Revival.Flak.cs  FlakNet.OnPhotonEvent dispatches kinds 40..46 here
//   Revival.PlayerAn2.cs PlaneCount/PlaneAt
//   RevivalFrameProfiler.cs S_C4T / S_C4D

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextDayRevival
{
    public static class C4
    {
        public const int DEF_C4 = 1493;
        public const int DEF_DETONATOR = 1494;
        public const int DEF_DEFUSE = 2078;
        const int DEF_GRENADE_DONOR = 1403;
        const int DEF_TOOL_DONOR = 2030;

        // FlakNet packet kinds (float[] of PacketLength).
        internal const int PlaceAsk = 40, Spawn = 41, Gone = 42, RemoteAsk = 43, DefuseAsk = 44,
                           SnapshotAsk = 45, Refused = 46;
        const int PacketLength = 15;
        const int HostWorld = 0, HostView = 1, HostTarget = 2;
        const int GoneDefused = 0, GoneExploded = 1, GoneLost = 2;
        const int Capacity = 128;
        const float K = C4Core.K;

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<int> CfgMaxPerPlayer;
        public static ConfigEntry<float> CfgChainRadius;
        public static ConfigEntry<float> CfgRadioRange;
        public static ConfigEntry<float> CfgPlaceReach;
        public static ConfigEntry<float> CfgPeopleRadius;
        public static ConfigEntry<float> CfgPeopleDamage;
        public static ConfigEntry<float> CfgLethalCore;
        public static ConfigEntry<float> CfgObjectFull;
        public static ConfigEntry<float> CfgObjectZero;
        public static ConfigEntry<float> CfgVehicleCharges;
        public static ConfigEntry<float> CfgAn2Charges;
        public static ConfigEntry<float> CfgAaCharges;
        public static ConfigEntry<float> CfgVehiclePartial;
        public static ConfigEntry<float> CfgDefuseSeconds;
        public static ConfigEntry<float> CfgMechanicFactor;
        public static ConfigEntry<float> CfgDefuseReach;
        public static ConfigEntry<string> CfgDefuseKey;
        public static ConfigEntry<float> CfgBeepVolume;

        static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        /// <summary>Mechanic buff of the merc buff system, once it exists: the
        /// defuse takes MechanicFactor of the time. Null = no buff.</summary>
        public static Func<bool> MechanicBuff;

        // ------------------------------------------------------------ state
        sealed class Charge
        {
            public int Id, Owner, HostKind, HostId;
            public Vector3 LocalPos, LocalNormal, World;
            public float FireAt;            // Time.time on this client, 0 = remote only
            public float NextBeep, LedOff;
            public bool LedLit;
            public Transform Host;
            public GameObject Go;
            public Renderer Led;
            public AudioSource Audio;
        }

        /// <summary>A sabotage point another feature declares (generator, fuel
        /// tank ...): charges stick to it, and blasts report to it.</summary>
        sealed class Target
        {
            public string Key;
            public int Id;
            public Transform Root;
            public float Charges;
            public Action<float, bool> Hit;
        }

        static readonly Charge[] _c = new Charge[Capacity];
        static int _n;
        static readonly float[] _xyz = new float[Capacity * 3];
        static readonly bool[] _live = new bool[Capacity];
        static readonly bool[] _seen = new bool[Capacity];
        static readonly int[] _order = new int[Capacity];
        static readonly int[] _owners = new int[Capacity];
        static readonly Charge[] _boom = new Charge[Capacity];
        static readonly Vector3[] _boomAt = new Vector3[Capacity];
        static readonly bool[] _inHouse = new bool[Capacity];
        static readonly float[] _pk = new float[PacketLength];
        static readonly List<Target> _targets = new List<Target>();
        static readonly RaycastHit[] _hits = new RaycastHit[12];
        static int _nextId;

        // ------------------------------------------------------------- items
        public static void AddItems(List<ItemDef> items)
        {
            items.Add(new ItemDef(
                DEF_C4, DEF_GRENADE_DONOR, true,
                "Заряд C4", "C4 demolition charge",
                "Взять в слот гранаты 3. Наведите на стену, землю или объект - зелёный силуэт "
                + "показывает, куда ляжет заряд. Колесо мыши - таймер (только радио, 10 с - 5 мин), "
                + "левый клик - установить. Заряд прилипает к технике и едет вместе с ней. "
                + "Взрывается от радиодетонатора или по таймеру и подрывает другие заряды рядом. "
                + "Не больше 5 зарядов на игрока.",
                "Equip in grenade slot 3. Aim at a wall, the ground or an object - the green "
                + "outline shows where the charge goes. Mouse wheel sets the timer (remote only, "
                + "10 s - 5 min), left click places it. It sticks to vehicles and rides along. "
                + "Fired by the remote detonator or its timer, it sets off other charges close by. "
                + "At most 5 charges per player.",
                "c4.ndmesh", "c4_diffuse.png", "c4_normal.png", "c4_icon.png", null,
                1, 0, 0.6f));
            items.Add(new ItemDef(
                DEF_DETONATOR, DEF_GRENADE_DONOR, true,
                "Радиодетонатор", "Remote detonator",
                "Взять в слот гранаты 3. Левый клик подрывает все ваши заряды C4 в радиусе связи.",
                "Equip in grenade slot 3. Left click fires all your C4 charges within radio range.",
                "detonator.ndmesh", "detonator_diffuse.png", "detonator_normal.png",
                "detonator_icon.png", null,
                1, 0, 0.3f));
            items.Add(new ItemDef(
                DEF_DEFUSE, DEF_TOOL_DONOR, false,
                "Набор сапёра", "Defuse kit",
                "Кусачки и щупы. Подойдите к заряду C4 и удерживайте клавишу (R), чтобы обезвредить его.",
                "Wire cutters and probes. Walk up to a C4 charge and hold the key (R) to defuse it.",
                "defusekit.ndmesh", "defusekit_diffuse.png", "defusekit_normal.png",
                "defusekit_icon.png", null,
                1, 0, 1.5f));
        }

        // ------------------------------------------------------------ config
        public static void BindConfig(ConfigFile cfg)
        {
            const string S = "C4";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "C4 demolition charge (1493), remote detonator (1494) and defuse kit (2078).");
            CfgMaxPerPlayer = cfg.Bind(S, "MaxPerPlayer", 5,
                "Charges one player may have placed at once; the master refuses more.");
            CfgChainRadius = cfg.Bind(S, "ChainRadius", 8f,
                "A charge going off sets off every other charge within this many metres.");
            CfgRadioRange = cfg.Bind(S, "RadioRange", 250f,
                "Metres the remote detonator reaches.");
            CfgPlaceReach = cfg.Bind(S, "PlaceReach", 2.5f,
                "Metres from the camera a charge can be placed.");
            CfgPeopleRadius = cfg.Bind(S, "PeopleRadius", 6f,
                "Metres at which the blast stops hurting people (linear falloff).");
            CfgPeopleDamage = cfg.Bind(S, "PeopleDamage", 400f,
                "Damage to a person at the charge (an NPC has 150 health).");
            CfgLethalCore = cfg.Bind(S, "LethalCore", 0.3f,
                "Share of PeopleRadius with full damage; inside half of it every NPC dies.");
            CfgObjectFull = cfg.Bind(S, "ObjectFullShare", 1.5f,
                "Metres from an object's centre within which a charge counts fully against it.");
            CfgObjectZero = cfg.Bind(S, "ObjectZeroShare", 4f,
                "Metres from an object's centre at which a charge no longer counts.");
            CfgVehicleCharges = cfg.Bind(S, "VehicleCharges", 1f,
                "Charges that destroy a vehicle (a charge stuck on it counts as one).");
            CfgAn2Charges = cfg.Bind(S, "An2Charges", 1f,
                "Charges that bring down an An-2.");
            CfgAaCharges = cfg.Bind(S, "AaGunCharges", 1f,
                "Charges that destroy an AA gun or the radar.");
            CfgVehiclePartial = cfg.Bind(S, "VehiclePartialDamage", 600f,
                "Vehicle damage per charge share below a kill.");
            CfgDefuseSeconds = cfg.Bind(S, "DefuseSeconds", 6f,
                "Seconds a defuse takes with the defuse kit.");
            CfgMechanicFactor = cfg.Bind(S, "MechanicFactor", 0.5f,
                "Defuse time factor with the Mechanic buff (when the merc buff system reports it).");
            CfgDefuseReach = cfg.Bind(S, "DefuseReach", 1.6f,
                "Metres from a charge a player can defuse it.");
            CfgDefuseKey = cfg.Bind(S, "DefuseKey", "R",
                "Key held to defuse (needs the defuse kit in the inventory).");
            CfgBeepVolume = cfg.Bind(S, "BeepVolume", 0.6f,
                "Volume of the timer beep (0 = silent).");
        }

        static float F(ConfigEntry<float> e, float d) { return e == null ? d : e.Value; }
        static int MaxPerPlayer() { return CfgMaxPerPlayer == null ? 5 : CfgMaxPerPlayer.Value; }

        // ----------------------------------------------------------- install
        public static void Install(Harmony harmony)
        {
            if (!Enabled) { RevivalPlugin.L.LogInfo("C4: disabled (C4/Enabled)."); return; }
            C4ThrowHook.Install(harmony);
            C4DataHook.Install(harmony);
            RevivalPlugin.L.LogInfo("C4: active (charge " + DEF_C4 + ", detonator " + DEF_DETONATOR
                + ", defuse kit " + DEF_DEFUSE + ", FlakNet kinds " + PlaceAsk + ".." + Refused + ").");
        }

        // -------------------------------------------------------- registry
        /// <summary>Declare a sabotage point (data-driven: any site may call
        /// this). charges = C4 needed to destroy it; hit gets the share of
        /// that (0..1) and whether it is destroyed, on the master.</summary>
        public static void RegisterTarget(string key, Transform root, float charges, Action<float, bool> hit)
        {
            if (string.IsNullOrEmpty(key) || root == null) return;
            UnregisterTarget(key);
            Target t = new Target();
            t.Key = key; t.Id = StableId(key); t.Root = root;
            t.Charges = Mathf.Max(0.1f, charges); t.Hit = hit;
            _targets.Add(t);
        }

        public static void UnregisterTarget(string key)
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
                if (_targets[i].Key == key) _targets.RemoveAt(i);
        }

        /// <summary>FNV-1a of the key in 24 bits, so it survives a float.</summary>
        static int StableId(string key)
        {
            uint h = 2166136261;
            for (int i = 0; i < key.Length; i++) { h ^= key[i]; h *= 16777619; }
            return (int)(h & 0xFFFFFF);
        }

        static Target TargetById(int id)
        {
            for (int i = 0; i < _targets.Count; i++)
                if (_targets[i].Id == id && _targets[i].Root != null) return _targets[i];
            return null;
        }

        public static int Count { get { return _n; } }

        // -------------------------------------------------------------- tick
        static float _masterUntil, _askAt = -1f;
        static bool _master;
        static int _askedActor = -2;

        static bool Master(float now)
        {
            if (now >= _masterUntil) { _masterUntil = now + 0.5f; _master = Crocodile.IsMaster(); }
            return _master;
        }

        public static void Tick()
        {
            if (!Enabled) return;
            float now = Time.time;
            try
            {
                FlakNet.EnsureHooked();
                Hand(now);
                Snapshot(now);
                if (_n == 0) { _defuseId = 0; return; }
                Charges(now);
                Defuse(now);
            }
            catch (Exception ex)
            {
                if (_errors++ < 5) RevivalPlugin.L.LogError("C4 tick: " + ex);
            }
        }
        static int _errors;

        /// <summary>Late joiner: ask the master once per room for what is placed.</summary>
        static void Snapshot(float now)
        {
            if (now < _askAt) return;
            _askAt = now + 2f;
            int me = Crocodile.LocalActor();
            if (me <= 0 || me == _askedActor || Master(now)) return;
            _askedActor = me;
            Clear(_pk); _pk[0] = SnapshotAsk; _pk[2] = me;
            FlakNet.SendPacket(_pk, true);
        }

        static void Charges(float now)
        {
            bool master = Master(now);
            int fire = -1;
            for (int i = 0; i < _n; i++)
            {
                Charge c = _c[i];
                if (c.Go == null)
                {
                    // Host gone (wreck despawned, map change): the charge went with it.
                    if (master) { Announce(c.Id, GoneLost, c.World); }
                    RemoveAt(i); i--;
                    continue;
                }
                if (c.LedLit && now >= c.LedOff) { c.LedLit = false; if (c.Led != null) c.Led.enabled = false; }
                if (now >= c.NextBeep)
                {
                    bool timed = c.FireAt > 0f;
                    c.NextBeep = now + (timed ? C4Core.BeepInterval(c.FireAt - now) : C4Core.RemoteBlink);
                    c.LedLit = true; c.LedOff = now + C4Core.FlashSeconds;
                    if (c.Led != null) c.Led.enabled = true;
                    if (timed && c.Audio != null && _beep != null && F(CfgBeepVolume, 0.6f) > 0f)
                        c.Audio.PlayOneShot(_beep, Mathf.Clamp01(F(CfgBeepVolume, 0.6f)));
                }
                if (master && fire < 0 && c.FireAt > 0f && now >= c.FireAt) fire = i;
            }
            if (fire >= 0) Explode(fire);
        }

        // -------------------------------------------------------- the hand
        static int _held = -1, _timerIndex = 3;
        static float _equipCheckAt;
        static Component _gren;
        static float _grenUntil;
        static string _timerLabel;
        static bool _aimValid, _aimHit;
        static string _aimWhy;
        static Vector3 _aimPoint, _aimNormal;
        static Transform _aimHost;
        static int _aimHostKind, _aimHostId;
        static int _lastPlaceFrame = -1, _lastFireFrame = -1, _limitFor;
        static string _limitWhy;

        static void Hand(float now)
        {
            if (now >= _equipCheckAt)
            {
                _equipCheckAt = now + 0.25f;
                _held = HeldId();
            }
            if (_held != DEF_C4 || MapTools.LocalPlayer() == null) { ShowPreview(false); return; }
            float w = GameUi.Axis("Mouse ScrollWheel");
            if (w != 0f && !GameplayCursor.Typing)
            {
                int was = _timerIndex;
                _timerIndex = C4Core.StepTimer(_timerIndex, w > 0f ? 1 : -1);
                if (was != _timerIndex) _timerLabel = null;
            }
            Aim(now);
            ShowPreview(_aimHit);
        }

        internal static string TimerLabel()
        {
            if (_timerLabel != null) return _timerLabel;
            int s = C4Core.TimerSteps[_timerIndex];
            string t = s <= 0 ? Loc.T("только радио", "remote only")
                : s < 60 ? s + Loc.T(" с", " s")
                : (s / 60) + Loc.T(" мин", " min") + (s % 60 == 0 ? "" : " " + (s % 60) + Loc.T(" с", " s"));
            _timerLabel = Loc.T("C4 - таймер: ", "C4 - timer: ") + t + Loc.T("  (колесо мыши)", "  (mouse wheel)");
            return _timerLabel;
        }

        static int HeldId()
        {
            Component ctrl = GrenadeController();
            Behaviour b = ctrl as Behaviour;
            if (b == null || !b.isActiveAndEnabled) return -1;
            return ApMine.EquippedGrenadeId(ctrl);
        }

        static Component GrenadeController()
        {
            float now = Time.time;
            if (_gren != null && now < _grenUntil) return _gren;
            if (_gren != null && IsLocal(_gren)) { _grenUntil = now + 2f; return _gren; }
            if (_gren == null && now < _grenUntil) return null;
            _grenUntil = now + 2f;
            _gren = null;
            Type t = RevivalPlugin.TypeByName("PlayerGrenadeWeaponController");
            if (t == null) return null;
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component;
                if (c != null && IsLocal(c)) { _gren = c; break; }
            }
            return _gren;
        }

        static bool IsLocal(Component c)
        {
            try
            {
                Type pv = RevivalPlugin.TypeByName("PhotonView");
                if (pv == null) return true;
                Component view = c.GetComponentInParent(pv);
                if (view == null) return true;
                MethodInfo getter = AccessTools.PropertyGetter(pv, "isMine");
                if (getter == null) return true;
                object r = getter.Invoke(view, null);
                return r is bool && (bool)r;
            }
            catch { return true; }
        }

        // ---------------------------------------------------------- aiming
        static Collider _lastCol;
        static int _lastColKind, _lastColId;
        static Transform _lastColHost;
        static bool _lastColOk;
        static Type _tView, _tVgs;
        static MethodInfo _mViewId, _mFind;

        /// <summary>The surface under the crosshair within reach, what it
        /// belongs to and whether a charge may go there.</summary>
        static void Aim(float now)
        {
            _aimHit = false; _aimValid = false; _aimWhy = null;
            Camera cam = CameraOwner.MainCamera();
            GameObject player = MapTools.LocalPlayer();
            if (cam == null || player == null) return;
            Transform me = player.transform;
            float reach = Mathf.Max(0.5f, F(CfgPlaceReach, 2.5f)) * K;
            Vector3 from = cam.transform.position, dir = cam.transform.forward;
            int n = Physics.RaycastNonAlloc(from, dir, _hits, reach + 3f, Physics.DefaultRaycastLayers,
                                            QueryTriggerInteraction.Ignore);
            int best = -1;
            for (int i = 0; i < n; i++)
            {
                Collider col = _hits[i].collider;
                if (col == null || col.transform.IsChildOf(me)) continue;
                if (best < 0 || _hits[i].distance < _hits[best].distance) best = i;
            }
            if (best < 0) return;
            RaycastHit hit = _hits[best];
            _aimHit = true;
            _aimPoint = hit.point;
            _aimNormal = hit.normal.sqrMagnitude > 0.5f ? hit.normal.normalized : Vector3.up;
            if (hit.distance > reach) { _aimWhy = Loc.T("Слишком далеко", "Too far"); return; }
            Classify(hit.collider);
            _aimHostKind = _lastColKind; _aimHostId = _lastColId; _aimHost = _lastColHost;
            if (!_lastColOk) { _aimWhy = Loc.T("Сюда нельзя", "Not here"); return; }
            if (!C4Core.MayPlace(OwnCount(Crocodile.LocalActor()), MaxPerPlayer()))
            {
                if (_limitWhy == null || _limitFor != MaxPerPlayer())
                {
                    _limitFor = MaxPerPlayer();
                    _limitWhy = Loc.T("Предел зарядов: ", "Charge limit: ") + _limitFor;
                }
                _aimWhy = _limitWhy;
                return;
            }
            _aimValid = true;
        }

        /// <summary>Host of a collider, worked out once per collider: a
        /// registered target, a vehicle or An-2 by its view, nothing networked
        /// else (people), or the static world.</summary>
        static void Classify(Collider col)
        {
            if (ReferenceEquals(col, _lastCol) && col != null) return;
            _lastCol = col; _lastColOk = true; _lastColKind = HostWorld; _lastColId = 0; _lastColHost = null;
            Transform t = col.transform;
            for (int i = 0; i < _targets.Count; i++)
            {
                Target g = _targets[i];
                if (g.Root != null && t.IsChildOf(g.Root))
                { _lastColKind = HostTarget; _lastColId = g.Id; _lastColHost = g.Root; return; }
            }
            try
            {
                if (_tView == null) _tView = RevivalPlugin.TypeByName("PhotonView");
                if (_tVgs == null) _tVgs = RevivalPlugin.TypeByName("VehicleGameSystem");
                Component view = _tView == null ? null : col.GetComponentInParent(_tView);
                if (view == null) return;
                bool vehicle = (_tVgs != null && col.GetComponentInParent(_tVgs) != null)
                               || PlayerAn2.Contains(view.gameObject);
                if (!vehicle) { _lastColOk = false; return; }
                if (_mViewId == null) _mViewId = AccessTools.PropertyGetter(_tView, "viewID");
                object id = _mViewId == null ? null : _mViewId.Invoke(view, null);
                if (!(id is int) || (int)id <= 0) { _lastColOk = false; return; }
                _lastColKind = HostView; _lastColId = (int)id; _lastColHost = view.transform;
            }
            catch { _lastColOk = false; }
        }

        static Transform ResolveHost(int kind, int id)
        {
            if (kind == HostTarget) { Target t = TargetById(id); return t == null ? null : t.Root; }
            if (kind != HostView) return null;
            try
            {
                if (_tView == null) _tView = RevivalPlugin.TypeByName("PhotonView");
                if (_mFind == null && _tView != null)
                    _mFind = AccessTools.Method(_tView, "Find", new Type[] { typeof(int) }, null);
                Component v = _mFind == null ? null : _mFind.Invoke(null, new object[] { id }) as Component;
                return v == null ? null : v.transform;
            }
            catch { return null; }
        }

        static int OwnCount(int owner)
        {
            for (int i = 0; i < _n; i++) _owners[i] = _c[i].Owner;
            return C4Core.CountOwn(_owners, _n, owner);
        }

        // ------------------------------------------------------- placement
        internal static void PlaceFromController(Component ctrl)
        {
            if (!Enabled || _lastPlaceFrame == Time.frameCount || ctrl == null || !IsLocal(ctrl)
                || ApMine.EquippedGrenadeId(ctrl) != DEF_C4 || MapTools.LocalPlayer() == null) return;
            _lastPlaceFrame = Time.frameCount;
            try { Place(ctrl); }
            catch (Exception ex) { RevivalPlugin.L.LogError("C4 placement: " + ex); }
        }

        static void Place(Component ctrl)
        {
            Aim(Time.time);
            if (!_aimValid)
            {
                Turret.Hinweis(_aimWhy ?? Loc.T("Нет поверхности", "No surface"), 2f);
                return;
            }
            if (!ConsumeEquipped(ctrl, DEF_C4))
            {
                Turret.Hinweis(Loc.T("Заряд не списан - не установлен", "Could not use up the charge - not placed"), 2f);
                return;
            }
            int me = Crocodile.LocalActor();
            Transform host = _aimHostKind == HostWorld ? null : _aimHost;
            Vector3 lp = host == null ? _aimPoint : host.InverseTransformPoint(_aimPoint);
            Vector3 ln = host == null ? _aimNormal : host.InverseTransformDirection(_aimNormal);
            int timer = C4Core.TimerSteps[_timerIndex];
            Clear(_pk);
            _pk[0] = PlaceAsk; _pk[1] = 0f; _pk[2] = me; _pk[3] = _aimHostKind; _pk[4] = _aimHostId;
            _pk[5] = lp.x; _pk[6] = lp.y; _pk[7] = lp.z; _pk[8] = ln.x; _pk[9] = ln.y; _pk[10] = ln.z;
            _pk[11] = timer; _pk[12] = _aimPoint.x; _pk[13] = _aimPoint.y; _pk[14] = _aimPoint.z;
            ToMaster(_pk);
            Turret.Hinweis(timer > 0
                ? Loc.T("C4 установлен - взрыв через " + timer + " с", "C4 placed - goes off in " + timer + " s")
                : Loc.T("C4 установлен - подрыв детонатором", "C4 placed - fire it with the detonator"), 3f);
        }

        internal static void FireFromController(Component ctrl)
        {
            if (!Enabled || _lastFireFrame == Time.frameCount || ctrl == null || !IsLocal(ctrl)
                || ApMine.EquippedGrenadeId(ctrl) != DEF_DETONATOR) return;
            _lastFireFrame = Time.frameCount;
            GameObject player = MapTools.LocalPlayer();
            if (player == null) return;
            int me = Crocodile.LocalActor();
            Vector3 at = player.transform.position;
            float range = Mathf.Max(1f, F(CfgRadioRange, 250f)) * K;
            int inRange = 0;
            for (int i = 0; i < _n; i++)
            {
                if (_c[i].Owner != me || _c[i].Go == null) continue;
                Vector3 d = _c[i].Go.transform.position - at;
                if (C4Core.InRange(d.x, d.y, d.z, range)) inRange++;
            }
            if (inRange == 0)
            {
                Turret.Hinweis(Loc.T("Нет ваших зарядов в радиусе связи", "None of your charges in radio range"), 2f);
                return;
            }
            Clear(_pk);
            _pk[0] = RemoteAsk; _pk[2] = me; _pk[12] = at.x; _pk[13] = at.y; _pk[14] = at.z;
            ToMaster(_pk);
            Turret.Hinweis(Loc.T("Подрыв: " + inRange, "Detonating: " + inRange), 2f);
        }

        /// <summary>Clear weapon slot 2 (UI slot 3) the way vanilla uses up a
        /// thrown grenade (the AP mine's path).</summary>
        static bool ConsumeEquipped(Component ctrl, int id)
        {
            FieldInfo f = AccessTools.Field(ctrl.GetType(), "_plrInventoryManager");
            object inv = f == null ? null : f.GetValue(ctrl);
            if (inv == null) return false;
            FieldInfo fw = AccessTools.Field(inv.GetType(), "_weaponsData");
            object data = fw == null ? null : fw.GetValue(inv);
            if (data == null) return false;
            FieldInfo ids = AccessTools.Field(data.GetType(), "ItemID");
            Array items = ids == null ? null : ids.GetValue(data) as Array;
            if (items == null || items.Length < 3 || ApMine.ToInt(items.GetValue(2)) != id) return false;
            MethodInfo clear = AccessTools.Method(inv.GetType(), "ClearWeaponSlot",
                new Type[] { typeof(int), typeof(int), typeof(bool), typeof(bool) }, null);
            if (clear == null) return false;
            clear.Invoke(inv, new object[] { 2, id, true, false });
            items = ids.GetValue(data) as Array;
            return items != null && ApMine.ToInt(items.GetValue(2)) != id;
        }

        // ---------------------------------------------------------- defuse
        static int _defuseId;
        static float _defuseDone, _defuseNeed;
        static bool _nearCharge, _hasKit;
        static float _kitCheckAt;

        static void Defuse(float now)
        {
            _nearCharge = false;
            GameObject player = MapTools.LocalPlayer();
            if (player == null) { _defuseId = 0; return; }
            Vector3 at = player.transform.position;
            float reach = Mathf.Max(0.5f, F(CfgDefuseReach, 1.6f)) * K;
            float best = reach * reach;
            Charge target = null;
            for (int i = 0; i < _n; i++)
            {
                if (_c[i].Go == null) continue;
                float d = (_c[i].Go.transform.position - at).sqrMagnitude;
                if (d <= best) { best = d; target = _c[i]; }
            }
            if (target == null) { _defuseId = 0; return; }
            _nearCharge = true;
            if (now >= _kitCheckAt) { _kitCheckAt = now + 0.5f; _hasKit = Turret.HasItem(DEF_DEFUSE); }
            KeyCode key = PlayerAn2.KeyOf(CfgDefuseKey, KeyCode.R);
            if (!_hasKit || GameUi.WindowOpen || !Input.GetKey(key) || GameplayCursor.Typing) { _defuseId = 0; return; }
            if (_defuseId != target.Id)
            {
                _defuseId = target.Id;
                _defuseDone = 0f;
                bool mech = false;
                try { mech = MechanicBuff != null && MechanicBuff(); } catch { }
                _defuseNeed = C4Core.DefuseSeconds(F(CfgDefuseSeconds, 6f), mech, F(CfgMechanicFactor, 0.5f));
            }
            _defuseDone += Time.deltaTime;
            if (_defuseDone < _defuseNeed) return;
            Clear(_pk);
            _pk[0] = DefuseAsk; _pk[1] = target.Id; _pk[2] = Crocodile.LocalActor();
            ToMaster(_pk);
            _defuseId = 0;
            Turret.Hinweis(Loc.T("Заряд обезврежен", "Charge defused"), 2f);
        }

        // ------------------------------------------------------------ HUD
        static GUIStyle _style;

        public static void Draw()
        {
            if (!Enabled || Event.current == null || Event.current.type != EventType.Repaint) return;
            bool hand = _held == DEF_C4;
            if (!hand && !_nearCharge) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.alignment = TextAnchor.MiddleCenter;
                _style.fontSize = 16;
                _style.normal.textColor = new Color(0.92f, 0.92f, 0.85f);
            }
            float w = 520f, x = (Screen.width - w) * 0.5f, y = Screen.height * 0.62f;
            if (hand)
            {
                GUI.Label(new Rect(x, y, w, 24f), TimerLabel(), _style);
                if (_aimWhy != null) GUI.Label(new Rect(x, y + 22f, w, 24f), _aimWhy, _style);
                y += 48f;
            }
            if (!_nearCharge) return;
            if (!_hasKit)
            {
                GUI.Label(new Rect(x, y, w, 24f), Loc.T("C4: нужен набор сапёра", "C4: a defuse kit is needed"), _style);
                return;
            }
            if (_defuseId == 0)
            {
                GUI.Label(new Rect(x, y, w, 24f), Loc.T("Удерживайте R - обезвредить C4", "Hold R to defuse the C4"), _style);
                return;
            }
            float p = _defuseNeed > 0f ? Mathf.Clamp01(_defuseDone / _defuseNeed) : 0f;
            Color was = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(x + 110f, y + 4f, 300f, 14f), Texture2D.whiteTexture);
            GUI.color = new Color(0.85f, 0.75f, 0.25f, 0.95f);
            GUI.DrawTexture(new Rect(x + 112f, y + 6f, 296f * p, 10f), Texture2D.whiteTexture);
            GUI.color = was;
        }

        // -------------------------------------------------------- network
        static void Clear(float[] p) { for (int i = 0; i < p.Length; i++) p[i] = 0f; }

        /// <summary>An ask: handled here on the master, sent to it elsewhere.</summary>
        static void ToMaster(float[] p)
        {
            if (Crocodile.IsMaster()) OnPacket(p, Crocodile.LocalActor());
            else FlakNet.SendPacket(p, true);
        }

        /// <summary>Master to everyone, itself included.</summary>
        static void Broadcast(float[] p)
        {
            FlakNet.SendPacket(p, true);
            Apply(p);
        }

        internal static void OnPacket(float[] f, int sender)
        {
            if (!Enabled || f == null || f.Length < PacketLength) return;
            try
            {
                int kind = Mathf.RoundToInt(f[0]);
                bool master = Crocodile.IsMaster();
                if (kind == PlaceAsk) { if (master) MasterPlace(sender, f); return; }
                if (kind == RemoteAsk)
                {
                    if (master) MasterRemote(sender, new Vector3(f[12], f[13], f[14]));
                    return;
                }
                if (kind == DefuseAsk) { if (master) MasterDefuse(Mathf.RoundToInt(f[1])); return; }
                if (kind == SnapshotAsk) { if (master) MasterSnapshot(); return; }
                // Results come from the master only.
                int host = MercAA.MasterActor();
                if (host > 0 && sender != host) return;
                Apply(f);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("C4: packet: " + ex.Message); }
        }

        static void Apply(float[] f)
        {
            int kind = Mathf.RoundToInt(f[0]);
            if (kind == Spawn) Build(f);
            else if (kind == Gone)
            {
                int i = IndexOf(Mathf.RoundToInt(f[1]));
                if (i >= 0) RemoveAt(i);
            }
            else if (kind == Refused && Mathf.RoundToInt(f[2]) == Crocodile.LocalActor())
            {
                Turret.Hinweis(Mathf.RoundToInt(f[3]) == 0
                    ? Loc.T("Предел зарядов - C4 не установлен", "Charge limit reached - C4 not placed")
                    : Loc.T("C4 не установлен", "C4 not placed"), 3f);
            }
        }

        // ----------------------------------------------------- master side
        static void MasterPlace(int owner, float[] f)
        {
            if (owner <= 0) owner = Mathf.RoundToInt(f[2]);
            if (!C4Core.MayPlace(OwnCount(owner), MaxPerPlayer()) || _n >= Capacity)
            {
                Clear(_pk); _pk[0] = Refused; _pk[2] = owner; _pk[3] = _n >= Capacity ? 1f : 0f;
                Broadcast(_pk);
                RevivalPlugin.L.LogInfo("C4: refused a charge of " + owner + " (limit " + MaxPerPlayer() + ").");
                return;
            }
            for (int i = 0; i < _n; i++) if (_c[i].Id > _nextId) _nextId = _c[i].Id;
            int id = ++_nextId;
            float timer = C4Core.ClampTimer(f[11]);
            float[] p = new float[PacketLength];    // f may be _pk itself
            Array.Copy(f, p, PacketLength);
            p[0] = Spawn; p[1] = id; p[2] = owner; p[11] = timer;
            Broadcast(p);
            RevivalPlugin.L.LogInfo("C4 " + id + ": placed by " + owner + " at "
                + new Vector3(f[12], f[13], f[14]).ToString("0") + (timer > 0f ? ", timer " + timer + " s." : ", remote."));
        }

        static void MasterRemote(int owner, Vector3 at)
        {
            float range = Mathf.Max(1f, F(CfgRadioRange, 250f)) * K;
            for (int guard = 0; guard < Capacity; guard++)
            {
                int start = -1;
                for (int i = 0; i < _n; i++)
                {
                    if (_c[i].Owner != owner) continue;
                    Vector3 d = Position(_c[i]) - at;
                    if (C4Core.InRange(d.x, d.y, d.z, range)) { start = i; break; }
                }
                if (start < 0) return;
                Explode(start);
            }
        }

        static void MasterDefuse(int id)
        {
            int i = IndexOf(id);
            if (i < 0) return;
            Vector3 at = Position(_c[i]);
            Announce(id, GoneDefused, at);
            RemoveAt(i);
            RevivalPlugin.L.LogInfo("C4 " + id + ": defused at " + at.ToString("0") + ".");
        }

        static void MasterSnapshot()
        {
            float now = Time.time;
            for (int i = 0; i < _n; i++)
            {
                Charge c = _c[i];
                Clear(_pk);
                _pk[0] = Spawn; _pk[1] = c.Id; _pk[2] = c.Owner; _pk[3] = c.HostKind; _pk[4] = c.HostId;
                _pk[5] = c.LocalPos.x; _pk[6] = c.LocalPos.y; _pk[7] = c.LocalPos.z;
                _pk[8] = c.LocalNormal.x; _pk[9] = c.LocalNormal.y; _pk[10] = c.LocalNormal.z;
                _pk[11] = c.FireAt > 0f ? Mathf.Max(0.1f, c.FireAt - now) : 0f;
                Vector3 w = Position(c);
                _pk[12] = w.x; _pk[13] = w.y; _pk[14] = w.z;
                FlakNet.SendPacket(_pk, true);
            }
        }

        static void Announce(int id, int reason, Vector3 at)
        {
            Clear(_pk);
            _pk[0] = Gone; _pk[1] = id; _pk[2] = reason; _pk[12] = at.x; _pk[13] = at.y; _pk[14] = at.z;
            FlakNet.SendPacket(_pk, true);
        }

        static Vector3 Position(Charge c) { return c.Go != null ? c.Go.transform.position : c.World; }

        /// <summary>Master: charge <paramref name="start"/> goes off and takes
        /// every charge the chain reaches with it, then the damage.</summary>
        static void Explode(int start)
        {
            for (int i = 0; i < _n; i++)
            {
                Vector3 p = Position(_c[i]);
                _xyz[i * 3] = p.x; _xyz[i * 3 + 1] = p.y; _xyz[i * 3 + 2] = p.z;
                _live[i] = true;
            }
            float chain = Mathf.Max(0f, CfgChainRadius == null ? 8f : CfgChainRadius.Value) * K;
            int count = C4Core.Chain(_xyz, _live, _n, start, chain, _seen, _order);
            for (int k = 0; k < count; k++)
            {
                int i = _order[k];
                _boom[k] = _c[i];
                _boomAt[k] = new Vector3(_xyz[i * 3], _xyz[i * 3 + 1], _xyz[i * 3 + 2]);
            }
            for (int k = 0; k < count; k++)
            {
                Announce(_boom[k].Id, GoneExploded, _boomAt[k]);
                int i = IndexOf(_boom[k].Id);
                if (i >= 0) RemoveAt(i);
            }
            RevivalPlugin.L.LogInfo("C4: " + count + " charge(s) went off at " + _boomAt[0].ToString("0") + ".");
            try { Damage(count); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("C4: damage: " + ex.Message); }
            for (int k = 0; k < count; k++) _boom[k] = null;
        }

        static void Damage(int count)
        {
            float people = Mathf.Max(0.5f, F(CfgPeopleRadius, 6f)) * K;
            float peak = Mathf.Max(0f, F(CfgPeopleDamage, 400f));
            float core = Mathf.Clamp(F(CfgLethalCore, 0.3f), 0f, 0.9f);
            float aaPeak = Mathf.Clamp(1f / Mathf.Max(0.1f, F(CfgAaCharges, 1f)), 0f, 2f);
            float aaRadius = 3f * K;
            // Power 5: the sabotage points first (generator inside its bunker,
            // fuel tanks, command post, radar mast, AA guns); a charge inside a
            // bunker went to the generator and costs no bunker armour.
            try { Sabotage.C4Chain(_boomAt, count, F(CfgObjectFull, 1.5f) * K, F(CfgObjectZero, 4f) * K, _inHouse); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("C4: sabotage: " + ex.Message); for (int k = 0; k < count; k++) _inHouse[k] = false; }
            for (int k = 0; k < count; k++)
            {
                Vector3 at = _boomAt[k];
                // AA first: the people pass bills AA with peak 0 at the same
                // point, and AirDefenceDamage keeps one bill per impact.
                AirDefenceDamage.ReportBlast(at, aaRadius, aaPeak, !_inHouse[k]);
                OrdnanceBlast.Enqueue(at, people, peak, 0f, peak, core, null, 0);
                try { RocketHook.Detonate(at + Vector3.up * 0.15f, 0f, aaRadius, 3f); }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("C4: no visible explosion - " + ex.Message); }
            }
            Vehicles(count);
            Planes(count);
            Targets(count);
        }

        /// <summary>Charges against one object: a charge stuck on it is one
        /// whole charge, the rest by distance (C4Core.ChargeShare).</summary>
        static float Shares(Transform obj, int count)
        {
            float full = F(CfgObjectFull, 1.5f) * K, zero = F(CfgObjectZero, 4f) * K, sum = 0f;
            Vector3 o = obj.position;
            for (int k = 0; k < count; k++)
            {
                Transform h = _boom[k].Host;
                if (h != null && (h == obj || h.IsChildOf(obj) || obj.IsChildOf(h))) { sum += 1f; continue; }
                sum += C4Core.ChargeShare(Vector3.Distance(o, _boomAt[k]), full, zero);
            }
            return sum;
        }

        static MethodInfo _apply;

        static void Vehicles(int count)
        {
            Component[] all = VehicleScan.All();
            float need = Mathf.Max(0.1f, F(CfgVehicleCharges, 1f));
            for (int i = 0; i < all.Length; i++)
            {
                Component v = all[i];
                if (v == null || PlayerAn2.Contains(v.gameObject)) continue;
                float s = Shares(v.transform, count);
                if (s <= 0f) continue;
                if (_apply == null)
                    _apply = AccessTools.Method(v.GetType(), "ApplyDamage", new Type[] { typeof(float), typeof(int) }, null);
                if (_apply == null) return;
                float dmg = C4Core.Destroyed(s, need) ? 1000000f : F(CfgVehiclePartial, 600f) * s / need;
                try { _apply.Invoke(v, new object[] { dmg, 14 }); }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("C4: vehicle damage: " + ex.Message); }
            }
        }

        static void Planes(int count)
        {
            float need = Mathf.Max(0.1f, F(CfgAn2Charges, 1f));
            for (int i = PlayerAn2.PlaneCount - 1; i >= 0; i--)
            {
                GameObject go = PlayerAn2.PlaneAt(i);
                if (go == null || PlayerAn2.Down(go)) continue;
                if (C4Core.Destroyed(Shares(go.transform, count), need)) PlayerAn2.ShotDown(go, go.transform.position);
            }
        }

        static void Targets(int count)
        {
            for (int i = 0; i < _targets.Count; i++)
            {
                Target t = _targets[i];
                if (t.Root == null || t.Hit == null) continue;
                float s = Shares(t.Root, count);
                if (s <= 0f) continue;
                try { t.Hit(Mathf.Clamp01(s / t.Charges), C4Core.Destroyed(s, t.Charges)); }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("C4: target " + t.Key + ": " + ex.Message); }
            }
        }

        // ------------------------------------------------------- registry
        static int IndexOf(int id)
        {
            for (int i = 0; i < _n; i++) if (_c[i].Id == id) return i;
            return -1;
        }

        static void RemoveAt(int i)
        {
            Charge c = _c[i];
            if (c.Go != null) UnityEngine.Object.Destroy(c.Go);
            _n--;
            _c[i] = _c[_n];
            _c[_n] = null;
        }

        /// <summary>Admin: every charge off the map, everywhere (master).</summary>
        public static string ClearAll()
        {
            int n = _n;
            while (_n > 0)
            {
                Charge c = _c[_n - 1];
                Announce(c.Id, GoneDefused, Position(c));
                RemoveAt(_n - 1);
            }
            return Loc.T("Сняты заряды C4: " + n, "C4 charges cleared: " + n);
        }

        // ---------------------------------------------------------- model
        static Mesh _mesh;
        static Material _mat, _ok, _bad, _led;
        static AudioClip _beep;
        static Quaternion _meshFrameInv = Quaternion.identity;
        static Vector3 _meshCenter;
        static float _meshThin;
        static bool _meshTried;
        static GameObject _preview;
        static MeshRenderer _previewR;
        static bool _previewValid;

        static bool LoadMesh()
        {
            if (_meshTried) return _mesh != null;
            _meshTried = true;
            _mesh = Assets.Load("c4.ndmesh");
            if (_mesh == null) { RevivalPlugin.L.LogWarning("C4: c4.ndmesh missing - charges are invisible."); return false; }
            // Thinnest axis onto the surface normal, longest along the surface.
            Vector3 s = _mesh.bounds.size;
            Vector3 thin = s.x <= s.y && s.x <= s.z ? Vector3.right : s.y <= s.z ? Vector3.up : Vector3.forward;
            Vector3 lng = s.x >= s.y && s.x >= s.z ? Vector3.right : s.y >= s.z ? Vector3.up : Vector3.forward;
            if (lng == thin) lng = thin == Vector3.right ? Vector3.up : Vector3.right;
            _meshFrameInv = Quaternion.Inverse(Quaternion.LookRotation(lng, thin));
            _meshThin = Vector3.Dot(s, thin);
            _meshCenter = _mesh.bounds.center;
            return true;
        }

        /// <summary>Pose of a charge on a surface: the same on every client
        /// (the long side follows the surface, chosen from the normal alone).</summary>
        static void Orient(Vector3 point, Vector3 normal, out Vector3 pos, out Quaternion rot)
        {
            Vector3 t = Vector3.Cross(normal, Vector3.up);
            if (t.sqrMagnitude < 0.01f) t = Vector3.Cross(normal, Vector3.forward);
            rot = Quaternion.LookRotation(t.normalized, normal) * _meshFrameInv;
            pos = point + normal * (_meshThin * 0.5f) - rot * _meshCenter;
        }

        static void ShowPreview(bool on)
        {
            if (!on)
            {
                if (_preview != null && _preview.activeSelf) _preview.SetActive(false);
                return;
            }
            if (_preview == null)
            {
                if (!LoadMesh()) return;
                _preview = new GameObject("NDR C4 preview");
                UnityEngine.Object.DontDestroyOnLoad(_preview);
                _preview.AddComponent<MeshFilter>().sharedMesh = _mesh;
                _previewR = _preview.AddComponent<MeshRenderer>();
                _previewR.shadowCastingMode = ShadowCastingMode.Off;
                _previewR.receiveShadows = false;
                _ok = Ghost(new Color(0.25f, 1f, 0.3f, 0.45f));
                _bad = Ghost(new Color(1f, 0.25f, 0.2f, 0.45f));
                _previewR.sharedMaterial = _ok;
                _previewValid = true;
            }
            if (!_preview.activeSelf) _preview.SetActive(true);
            if (_previewValid != _aimValid) { _previewValid = _aimValid; _previewR.sharedMaterial = _aimValid ? _ok : _bad; }
            Vector3 pos; Quaternion rot;
            Orient(_aimPoint, _aimNormal, out pos, out rot);
            _preview.transform.SetPositionAndRotation(pos, rot);
        }

        static Material Ghost(Color c)
        {
            Shader sh = Shader.Find("Legacy Shaders/Transparent/Diffuse");
            if (sh == null) sh = Shader.Find("Standard");
            Material m = new Material(sh);
            m.color = c;
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return m;
        }

        /// <summary>Builds one charge from a Spawn packet (every client).</summary>
        static void Build(float[] f)
        {
            int id = Mathf.RoundToInt(f[1]);
            if (id <= 0 || IndexOf(id) >= 0 || _n >= Capacity) return;
            Charge c = new Charge();
            c.Id = id; c.Owner = Mathf.RoundToInt(f[2]);
            c.HostKind = Mathf.RoundToInt(f[3]); c.HostId = Mathf.RoundToInt(f[4]);
            c.LocalPos = new Vector3(f[5], f[6], f[7]);
            c.LocalNormal = new Vector3(f[8], f[9], f[10]);
            c.World = new Vector3(f[12], f[13], f[14]);
            float timer = f[11] > 0f ? Mathf.Min(f[11], C4Core.MaxTimer) : 0f;
            float now = Time.time;
            c.FireAt = timer > 0f ? now + timer : 0f;
            c.NextBeep = now + 0.3f;
            c.Host = ResolveHost(c.HostKind, c.HostId);
            Vector3 point = c.Host != null ? c.Host.TransformPoint(c.LocalPos) : c.HostKind == HostWorld ? c.LocalPos : c.World;
            Vector3 normal = c.Host != null ? c.Host.TransformDirection(c.LocalNormal) : c.LocalNormal;
            if (normal.sqrMagnitude < 0.25f) normal = Vector3.up;
            normal.Normalize();
            if (c.Host == null) c.HostKind = HostWorld;
            GameObject go = new GameObject("NDR C4 " + id);
            if (LoadMesh())
            {
                go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                Material mat = ChargeMaterial();
                if (mat != null) mr.sharedMaterial = mat;
            }
            Vector3 pos; Quaternion rot;
            Orient(point, normal, out pos, out rot);
            go.transform.SetPositionAndRotation(pos, rot);
            if (c.Host != null) go.transform.SetParent(c.Host, true);
            c.Go = go;
            c.Led = Led(go.transform);
            c.Audio = go.AddComponent<AudioSource>();
            c.Audio.playOnAwake = false;
            c.Audio.spatialBlend = 1f;
            c.Audio.rolloffMode = AudioRolloffMode.Linear;
            c.Audio.minDistance = 2f;
            c.Audio.maxDistance = 25f * K;
            if (_beep == null) _beep = MakeBeep();
            _c[_n++] = c;
        }

        /// <summary>The red LED over the detonator box (c4_build.py puts its
        /// housing at x -0.06 on the top face); dark between flashes.</summary>
        static Renderer Led(Transform parent)
        {
            GameObject led = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Collider col = led.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
            led.name = "LED";
            led.transform.SetParent(parent, false);
            led.transform.localPosition = new Vector3(-0.06f, 0.19f, 0f);
            led.transform.localRotation = Quaternion.identity;
            led.transform.localScale = new Vector3(0.03f, 0.02f, 0.03f);
            Renderer r = led.GetComponent<Renderer>();
            if (_led == null)
            {
                Shader sh = Shader.Find("Unlit/Color");
                if (sh == null) sh = Shader.Find("Standard");
                _led = new Material(sh);
                _led.color = new Color(1f, 0.08f, 0.05f);
                if (_led.HasProperty("_EmissionColor"))
                {
                    _led.EnableKeyword("_EMISSION");
                    _led.SetColor("_EmissionColor", new Color(2f, 0.1f, 0.05f));
                }
                _led.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            r.sharedMaterial = _led;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.enabled = false;
            return r;
        }

        static AudioClip MakeBeep()
        {
            const int rate = 22050;
            int n = rate / 14;                     // ~70 ms
            float[] data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float env = Mathf.Min(1f, i / 60f) * Mathf.Min(1f, (n - i) / 200f);
                data[i] = Mathf.Sin(2f * Mathf.PI * 2900f * t) * 0.5f * env;
            }
            AudioClip clip = AudioClip.Create("NDR C4 beep", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static Material ChargeMaterial()
        {
            if (_mat != null) return _mat;
            try
            {
                Shader sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
                Material m = new Material(sh);
                m.name = "NDR_C4_Material";
                m.mainTexture = Assets.Texture("c4_diffuse.png", false, true);
                if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
                Texture2D nrm = Assets.Texture("c4_normal.png", true, true);
                if (nrm != null && m.HasProperty("_BumpMap"))
                {
                    m.SetTexture("_BumpMap", nrm);
                    m.EnableKeyword("_NORMALMAP");
                }
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.25f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                m.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _mat = m;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("C4 material: " + ex.Message); }
            return _mat;
        }
    }

    /// <summary>
    /// Postfix on PlayerGrenadeWeaponController::CantThrowGrenade: C4 and the
    /// detonator are never thrown - C4 is placed, the detonator fires.
    /// </summary>
    public static class C4ThrowHook
    {
        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerGrenadeWeaponController");
                MethodInfo m = t == null ? null : AccessTools.Method(t, "CantThrowGrenade", null, null);
                if (m == null || m.ReturnType != typeof(bool))
                {
                    RevivalPlugin.L.LogWarning("C4: CantThrowGrenade missing - left click inactive.");
                    return;
                }
                harmony.Patch(m, null, new HarmonyMethod(typeof(C4ThrowHook).GetMethod("Postfix")),
                              null, null, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("C4: CantThrowGrenade patch: " + ex); }
        }

        public static void Postfix(object __instance, ref bool __result)
        {
            try
            {
                if (__result || __instance == null) return;
                Component ctrl = __instance as Component;
                int id = ApMine.EquippedGrenadeId(ctrl);
                if (id == C4.DEF_C4) { __result = true; C4.PlaceFromController(ctrl); }
                else if (id == C4.DEF_DETONATOR) { __result = true; C4.FireFromController(ctrl); }
            }
            catch { }
        }
    }

    /// <summary>
    /// Grenade records for 1493 and 1494: the frag grenade 1403 cloned field by
    /// field with only ItemID replaced, on every lookup (the AP mine's way).
    /// </summary>
    public static class C4DataHook
    {
        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("xmlItemsDataManager");
                MethodInfo get = t == null ? null : AccessTools.Method(t,
                    "GetGrenadeWeaponData", new Type[] { typeof(int) }, null);
                if (get == null)
                {
                    RevivalPlugin.L.LogWarning("C4: GetGrenadeWeaponData(int) missing - C4 cannot be equipped.");
                    return;
                }
                harmony.Patch(get, new HarmonyMethod(typeof(C4DataHook).GetMethod("Prefix")),
                              null, null, null, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("C4: GetGrenadeWeaponData patch: " + ex); }
        }

        public static void Prefix(object __instance, int __0)
        {
            if ((__0 != C4.DEF_C4 && __0 != C4.DEF_DETONATOR) || __instance == null) return;
            try
            {
                FieldInfo f = AccessTools.Field(__instance.GetType(), "WeaponsGrenadeData");
                IDictionary entries = f == null ? null : f.GetValue(__instance) as IDictionary;
                if (entries == null) return;
                object existing = entries.Contains(__0) ? entries[__0] : null;
                if (existing != null)
                {
                    FieldInfo id = AccessTools.Field(existing.GetType(), "ItemID");
                    if (id != null && ApMine.ToInt(id.GetValue(existing)) == __0) return;
                }
                object donor = entries.Contains(1403) ? entries[1403] : null;
                if (donor == null) return;
                object copy = Activator.CreateInstance(donor.GetType());
                foreach (FieldInfo field in donor.GetType().GetFields(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic))
                    field.SetValue(copy, field.GetValue(donor));
                FieldInfo itemId = AccessTools.Field(copy.GetType(), "ItemID");
                object value = __0;
                if (itemId.FieldType != typeof(int))
                {
                    MethodInfo convert = itemId.FieldType.GetMethod("op_Implicit",
                        BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(int) }, null);
                    if (convert == null) return;
                    value = convert.Invoke(null, new object[] { __0 });
                }
                itemId.SetValue(copy, value);
                entries[__0] = copy;
                RevivalPlugin.L.LogInfo("C4: grenade equipment data registered for " + __0);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("C4: grenade data: " + ex.Message); }
        }
    }
}
