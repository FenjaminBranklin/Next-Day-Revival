// Next Day: Survival - Revival Toolkit
//
// AIR GUNNERS. A cabin passenger takes a gun station; the An-2 pilot marks a
// ground point and flies an automatic pylon turn around it; merc riders at
// the stations fire at enemies near the mark. Design of record:
// docs/ai/AIR_BRIDGE.md, "Gunners". The rules (weapon rating, arcs, orbit
// guidance) are pure and tested: Revival.AirGunnersCore.cs,
// tests/air_gunners_test.cs, research/air_gunners_check.py.
//
// STATIONS ([AirGunners] StationKey, default T, cycles from the cabin seat):
//   An-2  seat -> tail gun -> cargo door -> seat
//   Mi-8  seat -> left door -> right door -> seat
// The TAIL GUN is an An-2F style 12.7 mm machine gun on the upper rear
// fuselage, rear upper arc, fin and tailplane masked. It is the technical's
// gun in another mount: TechnicalGun's ammunition item and RequireAmmo, the
// belt taken through Turret.TakeFrom out of the player's own inventories,
// the game's own progress bar for the reload, the DShK report
// (VehicleShotSound.PlayTechnical) and the technical's damage path.
// The DOOR stations fire the weapon the gunner carries: its weapons_db
// numbers (WeaponFirearmData on the player's PlayerFirearmWeaponController),
// its own report, its own loaded rounds (Bullets[CurrentSlotID]) and the
// game's own reload (ReloadWeapon). The air weapon rating decides how much the
// aircraft's shake spreads the rounds - an MG walks onto a target, a sniper
// rifle from a door is near useless. Rocket launchers are refused with a
// vanilla toast: the backblast.
//
// ORBIT (An-2 pilot): the merc command key (mouse wheel click) marks a point
// under the view's centre, [AirGunners] OrbitKey (default O) flies a left
// pylon turn around it - the port cargo door faces the mark - at OrbitSpeed;
// any control key (W A S D Q E, throttle) gives the aeroplane back. The
// autopilot is PlayerAn2.Fly's bank seam (the bombsight's), the throttle a
// gentle lever step. The Mi-8 pilot can mark too (no orbit).
//
// MULTIPLAYER: the turret shot channel (Turret.Net, the BTR/technical rounds)
// carries three more actions - -6 a gunner's rounds (tracer, report, the tail
// gun's pose on every copy), -7 a station claim (one man per station), -8 the
// pilot's mark. Damage goes through the existing paths: an NPC this client
// owns takes Turret.TryDamage, one owned elsewhere the game's ApplyDamage RPC
// (NpcWar.RemoteHit), a merc's round NpcWar's own RoundHits.
//
// PERFORMANCE: Tick and LateTick allocate nothing and scan nothing; the merc
// target search walks the NpcScan registry snapshot twice a second, only
// while a merc rides at a station and a mark is set. F6: AirGunners.Tick /
// AirGunners.Draw.
//
// C# 3.0, ASCII only (Loc.T keeps the Cyrillic).

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class AirGunners
    {
        // ============================================================= config

        internal static ConfigEntry<bool> CfgEnabled, CfgMercs;
        internal static ConfigEntry<string> CfgStationKey, CfgOrbitKey, CfgReloadKey;
        internal static ConfigEntry<float> CfgOrbitRadius, CfgOrbitSpeed, CfgMarkRadius, CfgShake,
            CfgSensitivity, CfgTailDamage, CfgTailInterval, CfgTailReload, CfgRange;
        internal static ConfigEntry<int> CfgTailBelt;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string S = "AirGunners";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "Gun stations for cabin passengers (An-2 tail gun and cargo door, Mi-8 doors), "
                + "the pilot's ground mark and the An-2's orbit on it. Every client should agree.");
            CfgMercs = cfg.Bind(S, "MercGunners", true,
                "Mercs riding in the aircraft man the free stations and fire at enemies near the mark.");
            CfgStationKey = cfg.Bind(S, "StationKey", "T",
                "In a cabin seat: next gun station (An-2 tail, door, seat; Mi-8 left, right, seat).");
            CfgOrbitKey = cfg.Bind(S, "OrbitKey", "O",
                "An-2 pilot: fly a pylon turn around the marked point (mouse wheel click marks it).");
            CfgReloadKey = cfg.Bind(S, "ReloadKey", "R", "At a station: reload.");
            CfgOrbitRadius = cfg.Bind(S, "OrbitRadius", 150f, "Pylon turn radius, metres.");
            CfgOrbitSpeed = cfg.Bind(S, "OrbitSpeed", 110f,
                "Pylon turn airspeed, km/h (the An-2 stalls near 60).");
            CfgMarkRadius = cfg.Bind(S, "MarkRadius", 35f,
                "Merc gunners fire at enemies within this many metres of the mark.");
            CfgShake = cfg.Bind(S, "AirShake", 0.06f,
                "Radians the aircraft's shake adds to a round from a weapon rated 0 (an MG adds almost nothing).");
            CfgSensitivity = cfg.Bind(S, "MouseSensitivity", 2.0f, "Station aim, degrees per mouse unit.");
            CfgTailDamage = cfg.Bind(S, "TailGunDamage", 85f,
                "Damage per 12.7 mm round (the technical's DShK: [TechnicalGun] Damage 85).");
            CfgTailInterval = cfg.Bind(S, "TailGunInterval", 0.1f, "Seconds between two tail gun rounds.");
            CfgTailBelt = cfg.Bind(S, "TailGunBelt", 100, "Rounds per tail gun belt.");
            CfgTailReload = cfg.Bind(S, "TailGunReload", 5f, "Seconds to put a belt in.");
            CfgRange = cfg.Bind(S, "Range", 600f, "Range of a station's round, metres.");
        }

        internal static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        static float F(ConfigEntry<float> e, float fallback) { return e == null ? fallback : e.Value; }

        static KeyCode Key(ConfigEntry<string> e, KeyCode fallback)
        {
            return PlayerAn2.KeyOf(e, fallback);
        }

        // ============================================================== state

        const string TailReloadOwner = "air-tailgun-reload";
        const float Floor = 0.07f;          // shot cadence floor: E-051's Photon budget
        static readonly Color AttackRed = new Color(0.72f, 0.13f, 0.125f, 1f);

        // ---- the local gunner
        static int _station;                // AirStations id; None in the cabin seat
        static GameObject _craft;           // the aircraft of that station
        static bool _plane;                 // An-2 (true) or Mi-8
        static float _yaw, _pitch;
        static float _nextShot, _nextClaim, _nextWeapon, _toastAt, _seatedAt = -100f;
        static bool _wasPassenger;
        static int _tracerCount;

        // ---- the tail gun's belt (the technical's ammunition rules)
        static int _tailBelt = -1, _tailPending;
        static float _tailReloadDone, _tailNextTry;

        // ---- the carried weapon
        static Type _ctrlType;
        static Component _ctrl;
        static object _ctrlData;
        static WeaponCard _card;
        static FieldInfo _fData, _fInv, _fWeapons, _fBullets, _fSlot;
        static MethodInfo _mReload;

        // ---- the mark and the orbit (local pilot)
        static bool _markOn, _orbitOn, _markPlane;
        static Vector3 _mark;
        static GameObject _markCraft;
        static float _nextMarkSend;
        static bool _pilotWas;

        // ---- shot events, aggregated to <= 10 per second
        static int _pendRounds;
        static Vector3 _pendEnd;
        static bool _pendHit;
        static float _nextPublish;

        // ---- what other clients said
        sealed class Claim { internal int Actor; internal float Heard; }
        sealed class Mark { internal Vector3 At; internal bool On; internal float Heard; }
        static readonly Dictionary<int, Claim> _claims = new Dictionary<int, Claim>();
        static readonly Dictionary<int, Mark> _marks = new Dictionary<int, Mark>();

        // ---- merc gunners (owner side)
        sealed class MercGun
        {
            internal MercUnit Unit;
            internal GameObject Craft;
            internal bool Plane;
            internal int Station;
            internal Component Target;
            internal WeaponCard Card;
            internal float NextShot;
            internal int Burst;
            internal float Pend; internal int PendRounds; internal Vector3 PendEnd;
        }
        static readonly MercGun[] _mercGuns = { new MercGun(), new MercGun(), new MercGun(), new MercGun() };
        static int _mercCount;
        static float _nextMercScan, _nextMercClaim;
        static readonly List<MercUnit> _riders = new List<MercUnit>();

        // ---- weapons_db cards, by item id
        internal sealed class WeaponCard
        {
            internal int Id;
            internal AirWeaponStats Stats;
            internal float Rating;
            internal AudioClip Clip;
            internal string Text;           // the HUD line, built once
        }
        static readonly Dictionary<int, WeaponCard> _cards = new Dictionary<int, WeaponCard>();
        static readonly List<Vector3> _line = new List<Vector3>(2);
        static readonly List<Vector3> _flash = new List<Vector3>(2);

        // ---- tail gun models, one per An-2 on every client
        static readonly Dictionary<GameObject, Transform> _tailGuns = new Dictionary<GameObject, Transform>();
        static readonly List<GameObject> _tailGone = new List<GameObject>();
        static float _nextModels;
        static Material _gunMat;

        // ============================================================ queries

        /// <summary>The pilot's mouse wheel click belongs to the mark while he
        /// flies (MercQuick.Available asks).</summary>
        internal static bool PilotOwnsMark
        {
            get { return Enabled && (PlayerAn2.Flying || PlayerHeli.Flying); }
        }

        internal static bool AtStation { get { return _station != AirStations.None && _craft != null; } }

        static float Scale(bool plane) { return plane ? PlayerAn2.K : PlayerHeli.K; }

        static int ViewOf(GameObject craft, bool plane)
        {
            if (craft == null) return 0;
            return plane ? PlayerAn2.View(craft) : PlayerHeli.ViewOf(craft);
        }

        static Vector3 Eye(GameObject craft, bool plane, AirStation s)
        {
            Transform tr = craft.transform;
            return tr.position + tr.rotation * (new Vector3(s.X, s.Y, s.Z) * Scale(plane));
        }

        // =============================================================== frame

        internal static void Tick()
        {
            if (!Enabled) return;
            try
            {
                Turret.Net.EnsureHooked();
                float now = Time.time;
                TailGunModels(now);
                Pilot(now);
                Passenger(now);
                Flush(now);
                if (CfgMercs == null || CfgMercs.Value) MercGunners(now);
            }
            catch (Exception ex)
            {
                if (Time.time >= _toastAt)
                {
                    _toastAt = Time.time + 10f;
                    RevivalPlugin.L.LogWarning("AirGunners: " + ex);
                }
                Exit();
            }
        }

        /// <summary>After the seats are bound (RevivalPlugin.LateUpdate): the
        /// body carries the camera rig, so the station view goes on last.</summary>
        internal static void LateFrame()
        {
            if (!Enabled) return;
            if (CameraOwner.Has(CameraOwner.AirGun)) LateTick();
        }

        /// <summary>The station view (CameraOwner.AirGun).</summary>
        internal static void LateTick()
        {
            if (_station == AirStations.None || _craft == null) return;
            Camera cam = CameraOwner.ViewCamera();
            if (cam == null) return;
            AirStation s = AirStations.Get(_station);
            Transform tr = _craft.transform;
            Quaternion look = tr.rotation * Quaternion.Euler(-_pitch, _yaw, 0f);
            cam.transform.position = Eye(_craft, _plane, s);
            cam.transform.rotation = look;
            if (s.Heavy) PoseTailGun(_craft, look * Vector3.forward);
        }

        // ============================================================== pilot

        static void Pilot(float now)
        {
            bool an2 = PlayerAn2.Flying, heli = PlayerHeli.Flying;
            GameObject craft = an2 ? PlayerAn2.Plane : heli ? PlayerHeli.Machine : null;
            if (craft == null)
            {
                if (_pilotWas) { _pilotWas = false; ClearMark(); }
                return;
            }
            _pilotWas = true;
            bool free = !Cursor.visible && !GameUi.WindowOpen;
            MercQuick.Keys();
            KeyCode mark = MercQuick.CommandKey == KeyCode.None ? KeyCode.Mouse2 : MercQuick.CommandKey;
            if (free && Input.GetKeyDown(mark)) MarkUnderView(craft, an2);
            if (an2 && free && Input.GetKeyDown(Key(CfgOrbitKey, KeyCode.O)))
            {
                if (_orbitOn) { _orbitOn = false; Toast(Text.OrbitOff(), UiTone.Info); }
                else if (!_markOn) Toast(Text.NeedMark(), UiTone.Warning);
                else if (PlayerAn2.OnGround) Toast(Text.Airborne(), UiTone.Warning);
                else
                {
                    _orbitOn = true;
                    Toast(Text.OrbitOn(Mathf.RoundToInt(F(CfgOrbitRadius, 150f))), UiTone.Info);
                }
            }
            if (_markOn && now >= _nextMarkSend) SendMark(craft, an2);
        }

        static void MarkUnderView(GameObject craft, bool plane)
        {
            Camera cam = CameraOwner.ViewCamera();
            if (cam == null) return;
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector3 hit;
            GameObject struck = CastPast(ray.origin, ray.direction, 1500f * Scale(plane), craft, null, out hit);
            if (struck == null) { Toast(Text.NoGround(), UiTone.Warning); return; }
            _mark = hit;
            _markOn = true;
            _markCraft = craft;
            _markPlane = plane;
            _nextMarkSend = 0f;
            SendMark(craft, plane);
            Toast(plane ? Text.Marked(Key(CfgOrbitKey, KeyCode.O).ToString()) : Text.MarkedHeli(), UiTone.Info);
        }

        static void ClearMark()
        {
            int view = ViewOf(_markCraft, _markPlane);
            if (_markOn && view > 0) Publish(new float[] { -8f, view, 0f, 0f, 0f, 0f }, true);
            _markOn = false;
            _orbitOn = false;
            _markCraft = null;
        }

        static void SendMark(GameObject craft, bool plane)
        {
            _nextMarkSend = Time.time + 4f;
            int view = ViewOf(craft, plane);
            if (view <= 0) return;
            Publish(new float[] { -8f, view, _mark.x, _mark.y, _mark.z, 1f }, true);
        }

        /// <summary>PlayerAn2.Fly's autopilot seam: the bank of the pylon turn,
        /// or false when no orbit flies (any control key ends it).</summary>
        internal static bool OrbitSteer(GameObject plane, Vector3 pos, Vector3 vel, float maxBank, out float bank)
        {
            bank = 0f;
            if (!_orbitOn) return false;
            if (!Enabled || !_markOn || !PlayerAn2.Flying || !ReferenceEquals(plane, PlayerAn2.Plane)
                || PlayerAn2.OnGround)
            {
                _orbitOn = false;
                return false;
            }
            if (PilotInput())
            {
                _orbitOn = false;
                Toast(Text.OrbitTaken(), UiTone.Info);
                return false;
            }
            float k = PlayerAn2.K;
            float heading = Mathf.Atan2(vel.x, vel.z) * Mathf.Rad2Deg;
            float want;
            bank = OrbitCore.Guide(pos.x / k, pos.z / k, heading, vel.magnitude, _mark.x / k, _mark.z / k,
                                   Mathf.Max(40f, F(CfgOrbitRadius, 150f)), OrbitCore.Left, maxBank, out want);
            return true;
        }

        /// <summary>PlayerAn2.Fly's throttle seam: a slow, steady circle.</summary>
        internal static float OrbitThrottle(float lever, float speed, float dt)
        {
            if (!_orbitOn) return lever;
            return OrbitCore.Throttle(lever, speed, Mathf.Max(70f, F(CfgOrbitSpeed, 110f)) / 3.6f, dt);
        }

        internal static bool Orbiting { get { return _orbitOn; } }

        static bool PilotInput()
        {
            return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S)
                || Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.E)
                || Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C);
        }

        // ========================================================== passenger

        static void Passenger(float now)
        {
            bool an2 = PlayerAn2.Aboard && !PlayerAn2.Flying && PlayerAn2.CabinSeatTaken >= 0;
            bool heli = !an2 && PlayerHeli.Aboard && !PlayerHeli.Flying && PlayerHeli.CabinSeatTaken >= 0;
            GameObject craft = an2 ? PlayerAn2.Plane : heli ? PlayerHeli.Machine : null;
            if (craft == null)
            {
                _wasPassenger = false;
                if (_station != AirStations.None) Exit();
                return;
            }
            if (!_wasPassenger) { _wasPassenger = true; _seatedAt = now; }
            if (_station != AirStations.None && !ReferenceEquals(craft, _craft)) Exit();

            bool free = !Cursor.visible && !GameUi.WindowOpen;
            if (free && Input.GetKeyDown(Key(CfgStationKey, KeyCode.T))) Cycle(craft, an2);
            if (_station == AirStations.None) return;

            AirStation s = AirStations.Get(_station);
            if (free)
            {
                float sens = F(CfgSensitivity, 2f);
                _yaw += Input.GetAxis("Mouse X") * sens;
                _pitch += Input.GetAxis("Mouse Y") * sens;
                AirStations.Clamp(s, ref _yaw, ref _pitch);
            }
            if (now >= _nextClaim) SendClaim(_craft, _plane, _station, true);

            if (s.Heavy) TailGun(now, s, free);
            else DoorGun(now, s, free);
        }

        static void Cycle(GameObject craft, bool plane)
        {
            int next = _station;
            for (int i = 0; i < 3; i++)
            {
                next = AirStations.Next(next, plane);
                if (next == AirStations.None || !Taken(craft, plane, next)) break;
            }
            Exit();
            if (next == AirStations.None) { Toast(Text.BackInSeat(), UiTone.Info); return; }
            if (!CameraOwner.Request(CameraOwner.AirGun, true, "AirGunners"))
            {
                Toast(Text.CamBusy(), UiTone.Warning);
                return;
            }
            _station = next;
            _craft = craft;
            _plane = plane;
            AirStation s = AirStations.Get(next);
            _yaw = s.CentreYaw;
            _pitch = s.Heavy ? 15f : -20f;
            _nextShot = Time.time + 0.3f;
            SendClaim(craft, plane, next, true);
            if (s.Heavy)
            {
                if (_tailBelt < 0) _tailBelt = 0;
                if (_tailBelt == 0) TailLoad(false);
                Toast(Text.TailTaken(), UiTone.Info);
            }
            else
            {
                ReadWeapon(true);
                if (_card != null && _card.Rating == AirWeaponRating.Forbidden) Toast(Text.Backblast(), UiTone.Warning);
                else Toast(Text.DoorTaken(_card == null ? null : _card.Text), UiTone.Info);
            }
            RevivalPlugin.L.LogInfo("AirGunners: station " + next + " on view " + ViewOf(craft, plane) + ".");
        }

        static void Exit()
        {
            if (_station != AirStations.None && _craft != null) SendClaim(_craft, _plane, _station, false);
            if (CameraOwner.Has(CameraOwner.AirGun)) CameraOwner.Release(CameraOwner.AirGun);
            if (_tailReloadDone > 0f) NativeActionProgress.End(TailReloadOwner);
            _station = AirStations.None;
            _craft = null;
        }

        static bool Taken(GameObject craft, bool plane, int station)
        {
            Claim c;
            // Another client's man (or merc) holds it. This client's own mercs
            // give way: the next assignment skips the player's station.
            return _claims.TryGetValue(ClaimKey(ViewOf(craft, plane), station), out c) && Time.time - c.Heard < 5f;
        }

        static int ClaimKey(int view, int station) { return view * 8 + station; }

        static void SendClaim(GameObject craft, bool plane, int station, bool on)
        {
            _nextClaim = Time.time + 2f;
            int view = ViewOf(craft, plane);
            if (view > 0) Publish(new float[] { -7f, view, station, on ? 1f : 0f }, false);
        }

        // ----------------------------------------------------------- tail gun

        static void TailGun(float now, AirStation s, bool free)
        {
            if (_tailReloadDone > 0f)
            {
                float rest = _tailReloadDone - now;
                if (rest > 0.02f)
                {
                    if (!NativeActionProgress.IsActive(TailReloadOwner))
                        NativeActionProgress.Begin(TailReloadOwner, Text.Reloading(), rest, false, null, null);
                    return;
                }
                _tailReloadDone = 0f;
                _tailBelt = _tailPending;
                _tailPending = 0;
                NativeActionProgress.End(TailReloadOwner);
            }
            if (free && Input.GetKeyDown(Key(CfgReloadKey, KeyCode.R))) { TailLoad(true); return; }
            if (!free || !Input.GetMouseButton(0) || now < _nextShot) return;
            if (_tailBelt <= 0) { TailLoad(false); return; }
            if (!AirStations.Clear(s, _yaw, _pitch)) { _nextShot = now + 0.15f; return; }
            Quaternion look = _craft.transform.rotation * Quaternion.Euler(-_pitch, _yaw, 0f);
            float cone = AirWeaponRating.ShotCone(0.006f, 0.9f, F(CfgShake, 0.06f));
            Fire(_craft, _plane, s, look * Vector3.forward, cone, F(CfgTailDamage, 85f), null, null, 0);
            _tailBelt--;
            _nextShot = now + Mathf.Max(Floor, F(CfgTailInterval, 0.1f));
            if (_tailBelt <= 0) TailLoad(false);
        }

        /// <summary>A belt out of the player's own backpack and vest - the
        /// technical's ammunition item, its RequireAmmo, taken at the start.</summary>
        static void TailLoad(bool byHand)
        {
            if (_tailReloadDone > 0f) return;
            int belt = Mathf.Max(1, CfgTailBelt == null ? 100 : CfgTailBelt.Value);
            int room = belt - Mathf.Max(0, _tailBelt);
            if (room <= 0 || Time.time < _tailNextTry) return;
            int got = room;
            if (TechnicalGun.CfgRequireAmmo == null || TechnicalGun.CfgRequireAmmo.Value)
            {
                got = 0;
                int id = TechnicalGun.CfgAmmoId == null ? 2050 : TechnicalGun.CfgAmmoId.Value;
                List<object> invs = Turret.PlayerInventories();
                for (int i = 0; i < invs.Count && got < room; i++)
                {
                    object pack = FieldOf(invs[i], "_backpackData");
                    while (got < room && Turret.TakeFrom(invs[i], pack, "Rucksack", id, "AirGunners")) got++;
                    object vest = FieldOf(invs[i], "_gearsData");
                    while (got < room && Turret.TakeFrom(invs[i], vest, "Weste", id, "AirGunners")) got++;
                }
                if (got <= 0)
                {
                    _tailNextTry = Time.time + 1f;
                    Toast(TechnicalText.NoAmmo(id), UiTone.Warning);
                    return;
                }
            }
            _tailPending = Mathf.Max(0, _tailBelt) + got;
            _tailBelt = 0;
            _tailReloadDone = Time.time + Mathf.Max(0.5f, F(CfgTailReload, 5f));
            NativeActionProgress.End(TailReloadOwner);
            NativeActionProgress.Begin(TailReloadOwner, Text.Reloading(), _tailReloadDone - Time.time, false, null, null);
            RevivalPlugin.L.LogInfo("AirGunners: tail gun belt of " + got + (byHand ? " (by hand)." : "."));
        }

        // ----------------------------------------------------------- door gun

        static void DoorGun(float now, AirStation s, bool free)
        {
            if (now >= _nextWeapon) ReadWeapon(false);
            if (!free) return;
            if (Input.GetKeyDown(Key(CfgReloadKey, KeyCode.R))) { NativeReload(); return; }
            WeaponCard card = _card;
            bool auto = card != null && card.Stats.Modes != 0;
            bool trigger = auto ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0);
            if (!trigger || now < _nextShot) return;
            if (card == null) { _nextShot = now + 0.5f; Toast(Text.NoWeapon(), UiTone.Warning); return; }
            if (card.Rating == AirWeaponRating.Forbidden) { _nextShot = now + 1f; Toast(Text.Backblast(), UiTone.Warning); return; }
            int loaded = Loaded();
            if (loaded == 0)
            {
                _nextShot = now + 0.4f;
                Toast(Text.Empty(Key(CfgReloadKey, KeyCode.R).ToString()), UiTone.Warning);
                return;
            }
            if (!AirStations.Clear(s, _yaw, _pitch)) { _nextShot = now + 0.15f; return; }
            if (loaded > 0) SetLoaded(loaded - 1);
            Quaternion look = _craft.transform.rotation * Quaternion.Euler(-_pitch, _yaw, 0f);
            float cone = AirWeaponRating.ShotCone(card.Stats.Spread, card.Rating, F(CfgShake, 0.06f));
            Fire(_craft, _plane, s, look * Vector3.forward, cone, card.Stats.Damage, card, null, card.Id);
            _nextShot = now + AirWeaponRating.Cadence(card.Stats, Floor);
        }

        /// <summary>The player's own firearm controller and its weapons_db
        /// card. Twice a second while at a door: a weapon switch shows.</summary>
        static void ReadWeapon(bool force)
        {
            _nextWeapon = Time.time + 0.5f;
            if (_ctrlType == null) _ctrlType = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
            if (_ctrlType == null) return;
            Behaviour live = _ctrl as Behaviour;
            if (force || live == null || !live.isActiveAndEnabled)
            {
                GameObject me = MapTools.LocalPlayer();
                _ctrl = me == null ? null : me.GetComponentInChildren(_ctrlType, false);
                _ctrlData = null;
            }
            if (_ctrl == null) { _card = null; return; }
            if (_fData == null) _fData = AccessTools.Field(_ctrlType, "_weaponFirearmData");
            object data = _fData == null ? null : _fData.GetValue(_ctrl);
            if (ReferenceEquals(data, _ctrlData) && !force) return;
            _ctrlData = data;
            _card = data == null ? null : CardOf(data);
        }

        static int Loaded()
        {
            Array bullets; int slot;
            if (!Bullets(out bullets, out slot)) return -1;
            return ToInt(bullets.GetValue(slot));
        }

        static void SetLoaded(int rounds)
        {
            Array bullets; int slot;
            if (!Bullets(out bullets, out slot)) return;
            Type t = bullets.GetType().GetElementType();
            object v = FromInt(t, rounds);
            if (v != null) bullets.SetValue(v, slot);
        }

        static bool Bullets(out Array bullets, out int slot)
        {
            bullets = null; slot = -1;
            if (_ctrl == null) return false;
            if (_fInv == null) _fInv = AccessTools.Field(_ctrlType, "_plrInventoryManager");
            object inv = _fInv == null ? null : _fInv.GetValue(_ctrl);
            if (inv == null) return false;
            if (_fWeapons == null) _fWeapons = AccessTools.Field(inv.GetType(), "_weaponsData");
            object weapons = _fWeapons == null ? null : _fWeapons.GetValue(inv);
            if (weapons == null) return false;
            if (_fBullets == null) _fBullets = AccessTools.Field(weapons.GetType(), "Bullets");
            if (_fSlot == null) _fSlot = AccessTools.Field(weapons.GetType(), "CurrentSlotID");
            if (_fBullets == null || _fSlot == null) return false;
            bullets = _fBullets.GetValue(weapons) as Array;
            slot = ToInt(_fSlot.GetValue(weapons));
            return bullets != null && slot >= 0 && slot < bullets.Length;
        }

        /// <summary>The game's own reload (ReloadWeapon): its magazine rule,
        /// its timing, its sound and its bar.</summary>
        static void NativeReload()
        {
            if (_ctrl == null) return;
            try
            {
                if (_mReload == null) _mReload = AccessTools.Method(_ctrlType, "ReloadWeapon", Type.EmptyTypes, null);
                if (_mReload != null) _mReload.Invoke(_ctrl, null);
                _nextShot = Time.time + 0.5f;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirGunners reload: " + ex.Message); }
        }

        // ============================================================== fire

        /// <summary>One round from a station along <paramref name="aim"/>.
        /// merc: a merc gunner's round (NpcWar's own hit rules), else the
        /// local player's.</summary>
        static void Fire(GameObject craft, bool plane, AirStation s, Vector3 aim, float cone, float damage,
                         WeaponCard card, MercGun merc, int weaponId)
        {
            Vector3 from = Eye(craft, plane, s);
            Vector3 dir = Scatter(aim, cone);
            float range = Mathf.Max(50f, F(CfgRange, 600f)) * Scale(plane);
            Vector3 hit;
            GameObject struck = CastPast(from + dir * 0.6f * Scale(plane), dir, range, craft, merc == null ? LocalBody() : null, out hit);
            Vector3 end = struck == null ? from + dir * range : hit;
            Vector3 muzzle = Muzzle(craft, plane, s, dir);
            Show(craft, plane, s, muzzle, end, struck != null, card, ++_tracerCount);
            if (merc == null) { _pendRounds++; _pendEnd = end; _pendHit = struck != null; }
            else { merc.PendRounds++; merc.PendEnd = end; }
            if (struck == null) return;
            float dist = Vector3.Distance(from, hit);
            if (merc != null) NpcWar.AirGunnerMercRound(merc.Unit.Ai, struck, hit, dist);
            else NpcWar.AirGunnerHit(struck, damage, hit);
        }

        static Vector3 Muzzle(GameObject craft, bool plane, AirStation s, Vector3 dir)
        {
            float k = Scale(plane);
            if (s.Heavy)
            {
                Transform gun;
                if (_tailGuns.TryGetValue(craft, out gun) && gun != null)
                    return gun.position + gun.forward * 1.45f * k;
            }
            return Eye(craft, plane, s) + dir * 0.9f * k - craft.transform.up * 0.25f * k;
        }

        static Vector3 Scatter(Vector3 aim, float cone)
        {
            if (cone <= 0f) return aim;
            Vector3 side = Vector3.Cross(aim, Vector3.up);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.Cross(aim, Vector3.right);
            side.Normalize();
            Vector3 up = Vector3.Cross(side, aim);
            Vector2 r = UnityEngine.Random.insideUnitCircle * Mathf.Tan(cone);
            return (aim + side * r.x + up * r.y).normalized;
        }

        /// <summary>Turret.RaycastObject past the aircraft itself and the
        /// shooter's own body (up to four times).</summary>
        static GameObject CastPast(Vector3 from, Vector3 dir, float range, GameObject craft, Transform body, out Vector3 point)
        {
            point = Vector3.zero;
            float rest = range;
            Transform root = craft == null ? null : craft.transform;
            for (int i = 0; i < 4 && rest > 0f; i++)
            {
                Vector3 hit;
                GameObject go = Turret.RaycastObject(from, dir, rest, out hit);
                if (go == null) return null;
                Transform t = go.transform;
                bool own = (root != null && t.IsChildOf(root)) || (body != null && t.IsChildOf(body));
                if (!own) { point = hit; return go; }
                rest -= Vector3.Distance(from, hit) + 0.25f;
                from = hit + dir * 0.25f;
            }
            return null;
        }

        static Transform LocalBody()
        {
            GameObject me = MapTools.LocalPlayer();
            return me == null ? null : me.transform;
        }

        /// <summary>Tracer, muzzle flash and report - here and on every copy.</summary>
        static void Show(GameObject craft, bool plane, AirStation s, Vector3 muzzle, Vector3 end, bool hit,
                         WeaponCard card, int round)
        {
            try
            {
                if (s.Heavy) PoseTailGun(craft, end - muzzle);
                bool tracer = s.Heavy || round % 3 == 0;
                if (tracer && Anim.Tracers)
                {
                    _line.Clear(); _line.Add(muzzle); _line.Add(end);
                    float w = s.Heavy ? 0.3f : 0.12f;
                    RocketHook.SpawnTracer(_line, w, w * 0.4f, new Color(1f, 0.96f, 0.78f, 1f),
                                           new Color(1f, 0.62f, 0.2f, 1f), 0.12f);
                }
                if (Anim.MuzzleFlash)
                {
                    Vector3 axis = end - muzzle;
                    float len = axis.magnitude;
                    if (len > 0.01f)
                    {
                        _flash.Clear(); _flash.Add(muzzle);
                        _flash.Add(muzzle + axis / len * Mathf.Min(len, s.Heavy ? 3f : 1.6f));
                        RocketHook.SpawnTracer(_flash, s.Heavy ? 0.9f : 0.5f, 0.05f, Color.white,
                                               new Color(1f, 0.38f, 0.1f, 1f), 0.05f);
                    }
                }
                if (s.Heavy || card == null || card.Clip == null) VehicleShotSound.PlayTechnical(muzzle);
                else VehicleShotSound.PlayClip(muzzle, card.Clip);
            }
            catch (Exception ex)
            {
                if (Time.time >= _toastAt)
                {
                    _toastAt = Time.time + 10f;
                    RevivalPlugin.L.LogWarning("AirGunners shot effect: " + ex.Message);
                }
            }
        }

        /// <summary>The local gunner's rounds go out at most ten times a
        /// second: the last end point and how many rounds since.</summary>
        static void Flush(float now)
        {
            if (_pendRounds <= 0 || now < _nextPublish || _craft == null) return;
            _nextPublish = now + 0.1f;
            int view = ViewOf(_craft, _plane);
            int weapon = _station == AirStations.An2Tail || _card == null ? 0 : _card.Id;
            if (view > 0)
                Publish(new float[] { -6f, view, _station, _pendEnd.x, _pendEnd.y, _pendEnd.z,
                                      _pendHit ? 1f : 0f, weapon, Mathf.Min(_pendRounds, 30) }, false);
            _pendRounds = 0;
        }

        static void Publish(float[] data, bool reliable)
        {
            Turret.Net.PublishAir(data, reliable);
        }

        // ============================================================ network

        /// <summary>Turret.Net.OnPhotonEvent hands over -6, -7 and -8.</summary>
        internal static void OnNet(float[] data, int sender)
        {
            if (!Enabled || data == null || data.Length < 4) return;
            int action = Mathf.RoundToInt(data[0]);
            int view = Mathf.RoundToInt(data[1]);
            if (view <= 0) return;
            if (action == -7)
            {
                int key = ClaimKey(view, Mathf.RoundToInt(data[2]));
                if (data[3] > 0.5f)
                {
                    Claim c;
                    if (!_claims.TryGetValue(key, out c)) { c = new Claim(); _claims[key] = c; }
                    c.Actor = sender; c.Heard = Time.time;
                }
                else _claims.Remove(key);
                return;
            }
            if (action == -8 && data.Length >= 6)
            {
                Mark m;
                if (!_marks.TryGetValue(view, out m)) { m = new Mark(); _marks[view] = m; }
                m.At = new Vector3(data[2], data[3], data[4]);
                m.On = data[5] > 0.5f;
                m.Heard = Time.time;
                return;
            }
            if (action == -6 && data.Length >= 9)
            {
                bool plane = true;
                GameObject craft = PlayerAn2.ByViewId(view);
                if (craft == null) { craft = PlayerHeli.MachineByView(view); plane = false; }
                if (craft == null) return;
                AirStation s = AirStations.Get(Mathf.RoundToInt(data[2]));
                if (s.Id == AirStations.None) return;
                Vector3 end = new Vector3(data[3], data[4], data[5]);
                int weapon = Mathf.RoundToInt(data[7]);
                WeaponCard card = weapon > 0 ? CardById(weapon) : null;
                Vector3 dir = end - Eye(craft, plane, s);
                if (dir.sqrMagnitude < 0.0001f) return;
                dir.Normalize();
                Show(craft, plane, s, Muzzle(craft, plane, s, dir), end, data[6] > 0.5f, card, ++_tracerCount);
            }
        }

        // ======================================================= merc gunners

        /// <summary>Mercs of this client riding an aircraft with a mark man
        /// the free stations; twice a second the seats and targets are
        /// re-read (the NpcScan snapshot), every frame only due rounds fire.</summary>
        static void MercGunners(float now)
        {
            if (now >= _nextMercScan)
            {
                _nextMercScan = now + 0.5f;
                AssignMercs(now);
            }
            for (int i = 0; i < _mercCount; i++)
            {
                MercGun g = _mercGuns[i];
                if (g.Target == null || g.Craft == null || g.Unit == null || g.Unit.Ai == null) continue;
                if (g.PendRounds > 0 && now >= g.Pend)
                {
                    g.Pend = now + 0.1f;
                    int view = ViewOf(g.Craft, g.Plane);
                    if (view > 0)
                        Publish(new float[] { -6f, view, g.Station, g.PendEnd.x, g.PendEnd.y, g.PendEnd.z,
                                              0f, g.Card == null ? 0 : g.Card.Id, g.PendRounds }, false);
                    g.PendRounds = 0;
                }
                if (now < g.NextShot) continue;
                if (!NpcWar.AirGunnerEnemy(g.Unit, g.Target)) { g.Target = null; continue; }
                AirStation s = AirStations.Get(g.Station);
                Vector3 eye = Eye(g.Craft, g.Plane, s);
                Vector3 aim = Chest(g.Target) - eye;
                if (aim.sqrMagnitude < 1f) { g.Target = null; continue; }
                aim.Normalize();
                float yaw, pitch;
                LocalAngles(g.Craft, aim, out yaw, out pitch);
                if (!AirStations.Clear(s, yaw, pitch)) { g.NextShot = now + 0.3f; continue; }
                float rating = g.Card == null ? 0.3f : Mathf.Max(0f, g.Card.Rating);
                float spread = g.Card == null ? 0.02f : g.Card.Stats.Spread;
                Fire(g.Craft, g.Plane, s, aim, AirWeaponRating.ShotCone(spread, rating, F(CfgShake, 0.06f)),
                     0f, g.Card, g, g.Card == null ? 0 : g.Card.Id);
                float cadence = g.Card == null ? 0.15f : AirWeaponRating.Cadence(g.Card.Stats, Floor);
                if (g.Card != null && g.Card.Stats.Modes == 0) cadence = Mathf.Max(cadence, 0.6f);
                if (++g.Burst >= 6) { g.Burst = 0; cadence += 0.9f; }
                g.NextShot = now + cadence;
            }
            if (_mercCount > 0 && now >= _nextMercClaim)
            {
                _nextMercClaim = now + 2f;
                for (int i = 0; i < _mercCount; i++)
                {
                    int view = ViewOf(_mercGuns[i].Craft, _mercGuns[i].Plane);
                    if (view > 0) Publish(new float[] { -7f, view, _mercGuns[i].Station, 1f }, false);
                }
            }
        }

        static void AssignMercs(float now)
        {
            int was = _mercCount;
            _mercCount = 0;
            MercRide.AirRiders(_riders);
            for (int r = 0; r < _riders.Count && _mercCount < _mercGuns.Length; r++)
            {
                MercUnit u = _riders[r];
                MercCarrier c = u.Ride.Carrier;
                bool plane = c.Kind == MercCarrier.Plane;
                Vector3 mark;
                if (!MarkFor(c.Go, plane, out mark)) continue;
                int station = FreeStation(c.Go, plane);
                if (station == AirStations.None) continue;
                MercGun g = _mercGuns[_mercCount++];
                bool same = ReferenceEquals(g.Unit, u) && ReferenceEquals(g.Craft, c.Go) && g.Station == station;
                g.Unit = u; g.Craft = c.Go; g.Plane = plane; g.Station = station;
                if (!same) { g.Target = null; g.Burst = 0; g.PendRounds = 0; }
                int weapon = NpcWar.AirGunnerWeapon(u.Ai);
                g.Card = weapon > 0 ? CardById(weapon) : null;
                if (g.Card != null && g.Card.Rating == AirWeaponRating.Forbidden) { _mercCount--; continue; }
                g.Target = MercTarget(g, mark);
            }
            for (int i = _mercCount; i < was; i++)
            {
                MercGun g = _mercGuns[i];
                if (g.Craft != null)
                {
                    int view = ViewOf(g.Craft, g.Plane);
                    if (view > 0) Publish(new float[] { -7f, view, g.Station, 0f }, false);
                }
                g.Unit = null; g.Craft = null; g.Target = null; g.Card = null;
            }
        }

        static int FreeStation(GameObject craft, bool plane)
        {
            int[] order = plane ? An2Order : Mi8Order;
            for (int i = 0; i < order.Length; i++)
            {
                int st = order[i];
                if (_station == st && ReferenceEquals(_craft, craft)) continue;
                Claim c;
                if (_claims.TryGetValue(ClaimKey(ViewOf(craft, plane), st), out c) && Time.time - c.Heard < 5f) continue;
                bool used = false;
                for (int k = 0; k < _mercCount; k++)
                    if (ReferenceEquals(_mercGuns[k].Craft, craft) && _mercGuns[k].Station == st) used = true;
                if (!used) return st;
            }
            return AirStations.None;
        }

        // The door first: the orbit holds the mark off the port side.
        static readonly int[] An2Order = { AirStations.An2Door, AirStations.An2Tail };
        static readonly int[] Mi8Order = { AirStations.Mi8Left, AirStations.Mi8Right };

        static bool MarkFor(GameObject craft, bool plane, out Vector3 mark)
        {
            mark = Vector3.zero;
            if (craft == null) return false;
            if (_markOn && ReferenceEquals(craft, _markCraft))
            {
                mark = _mark;
                return true;
            }
            Mark m;
            if (_marks.TryGetValue(ViewOf(craft, plane), out m) && m.On && Time.time - m.Heard < 12f)
            {
                mark = m.At;
                return true;
            }
            return false;
        }

        /// <summary>The nearest hostile man to the mark, inside the station's
        /// arc. Walks the NpcScan registry snapshot (twice a second).</summary>
        static Component MercTarget(MercGun g, Vector3 mark)
        {
            float k = Scale(g.Plane);
            float reach = Mathf.Max(5f, F(CfgMarkRadius, 35f)) * k;
            AirStation s = AirStations.Get(g.Station);
            Vector3 eye = Eye(g.Craft, g.Plane, s);
            float range = Mathf.Max(50f, F(CfgRange, 600f)) * k;
            if (g.Target != null && Keep(g, g.Target, mark, reach, eye, range, s)) return g.Target;
            Component[] all = NpcScan.All();
            Component best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < all.Length; i++)
            {
                Component npc = all[i];
                if (npc == null) continue;
                Vector3 p = npc.transform.position;
                if (!OrbitCore.NearMark(p.x, p.z, mark.x, mark.z, reach)) continue;
                if (!Keep(g, npc, mark, reach, eye, range, s)) continue;
                float d = (p - mark).sqrMagnitude;
                if (d < bestD) { bestD = d; best = npc; }
            }
            return best;
        }

        static bool Keep(MercGun g, Component npc, Vector3 mark, float reach, Vector3 eye, float range, AirStation s)
        {
            if (npc == null || !npc.gameObject.activeInHierarchy) return false;
            Vector3 p = npc.transform.position;
            if (!OrbitCore.NearMark(p.x, p.z, mark.x, mark.z, reach * 1.3f)) return false;
            Vector3 aim = Chest(npc) - eye;
            if (aim.sqrMagnitude > range * range || aim.sqrMagnitude < 1f) return false;
            float yaw, pitch;
            LocalAngles(g.Craft, aim.normalized, out yaw, out pitch);
            if (!AirStations.InArc(s, yaw, pitch)) return false;
            return NpcWar.AirGunnerEnemy(g.Unit, npc);
        }

        static Vector3 Chest(Component npc)
        {
            return npc.transform.position + Vector3.up * 3.4f;
        }

        static void LocalAngles(GameObject craft, Vector3 worldDir, out float yaw, out float pitch)
        {
            Vector3 l = Quaternion.Inverse(craft.transform.rotation) * worldDir;
            yaw = Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg;
            pitch = Mathf.Atan2(l.y, Mathf.Sqrt(l.x * l.x + l.z * l.z)) * Mathf.Rad2Deg;
        }

        // ===================================================== weapons_db cards

        static WeaponCard CardById(int id)
        {
            WeaponCard c;
            if (_cards.TryGetValue(id, out c)) return c;
            object data = null;
            try { data = WeaponData.Get(id); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirGunners weapon " + id + ": " + ex.Message); }
            c = data == null ? null : CardOf(data);
            _cards[id] = c;
            return c;
        }

        static WeaponCard CardOf(object data)
        {
            int id = ToInt(FieldOf(data, "ItemID"));
            WeaponCard c;
            if (id > 0 && _cards.TryGetValue(id, out c) && c != null) return c;
            c = new WeaponCard();
            c.Id = id;
            AirWeaponStats w = new AirWeaponStats();
            w.ItemId = id;
            w.Interval = ToFloat(FieldOf(data, "rateOfFire"));
            w.Spread = ToFloat(FieldOf(data, "Spread"));
            w.Damage = ToFloat(FieldOf(data, "Damage"));
            w.Reload = ToFloat(FieldOf(data, "ReloadTime"));
            w.EffectiveRange = ToFloat(FieldOf(data, "EffectiveRange"));
            w.Magazine = ToInt(FieldOf(data, "MaxBullets"));
            w.Modes = ToInt(FieldOf(data, "_shootModes"));
            w.Shotgun = ToInt(FieldOf(data, "_weaponType")) == 1;
            w.Scoped = FieldOf(data, "Scope") as UnityEngine.Object != null;
            w.Cal = FieldOf(data, "Cal") as string;
            w.Launcher = id == Stinger.ItemId || id == Crew.LAW_ID;
            c.Stats = w;
            c.Rating = AirWeaponRating.Rate(w);
            try
            {
                AudioClip[] sounds = FieldOf(data, "Weapon_Sounds") as AudioClip[];
                int idx = ToInt(FieldOf(data, "ShootSND_Index"));
                if (sounds != null && idx >= 0 && idx < sounds.Length) c.Clip = sounds[idx];
            }
            catch { c.Clip = null; }
            c.Text = Text.Rating(c.Rating);
            if (id > 0) _cards[id] = c;
            RevivalPlugin.L.LogInfo("AirGunners: weapon " + id + " air rating " + c.Rating.ToString("0.00")
                + " (rof " + w.Interval + ", spread " + w.Spread + ", mag " + w.Magazine + ", modes " + w.Modes
                + (w.Shotgun ? ", shotgun" : "") + (w.Scoped ? ", scope" : "") + ", cal " + w.Cal + ").");
            return c;
        }

        static object FieldOf(object o, string name)
        {
            if (o == null) return null;
            FieldInfo f = FastField.Find(o.GetType(), name);
            return f == null ? null : f.GetValue(o);
        }

        static readonly Dictionary<Type, MethodInfo> _toInt = new Dictionary<Type, MethodInfo>();
        static readonly Dictionary<Type, MethodInfo> _toFloat = new Dictionary<Type, MethodInfo>();
        static readonly Dictionary<Type, MethodInfo> _fromInt = new Dictionary<Type, MethodInfo>();

        static int ToInt(object v)
        {
            if (v == null) return 0;
            if (v is int) return (int)v;
            if (v is Enum) return Convert.ToInt32(v);
            MethodInfo m = Implicit(_toInt, v.GetType(), v.GetType(), typeof(int));
            return m == null ? 0 : (int)m.Invoke(null, new object[] { v });
        }

        static float ToFloat(object v)
        {
            if (v == null) return 0f;
            if (v is float) return (float)v;
            MethodInfo m = Implicit(_toFloat, v.GetType(), v.GetType(), typeof(float));
            return m == null ? 0f : (float)m.Invoke(null, new object[] { v });
        }

        static object FromInt(Type t, int value)
        {
            if (t == typeof(int)) return value;
            MethodInfo m = Implicit(_fromInt, t, typeof(int), t);
            return m == null ? null : m.Invoke(null, new object[] { value });
        }

        /// <summary>op_Implicit picked by both ends: ObscuredInt declares several
        /// that differ only in the return type (Stinger.ToInt's lesson).</summary>
        static MethodInfo Implicit(Dictionary<Type, MethodInfo> cache, Type owner, Type from, Type to)
        {
            MethodInfo m;
            if (cache.TryGetValue(owner, out m)) return m;
            MethodInfo[] ms = owner.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length && m == null; i++)
            {
                if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != to) continue;
                ParameterInfo[] p = ms[i].GetParameters();
                if (p.Length == 1 && p[0].ParameterType == from) m = ms[i];
            }
            cache[owner] = m;
            return m;
        }

        // ======================================================== tail gun model

        /// <summary>An-2F style: a ring mount, receiver, barrel and muzzle
        /// brake on the upper rear fuselage of every An-2, once per aircraft
        /// (the list is checked once a second).</summary>
        static void TailGunModels(float now)
        {
            if (now < _nextModels) return;
            _nextModels = now + 1f;
            List<GameObject> all = PlayerAn2.AllPlanes;
            for (int i = 0; i < all.Count; i++)
            {
                GameObject go = all[i];
                if (go == null || _tailGuns.ContainsKey(go)) continue;
                try { _tailGuns[go] = BuildTailGun(go); }
                catch (Exception ex)
                {
                    _tailGuns[go] = null;
                    RevivalPlugin.L.LogWarning("AirGunners tail gun model: " + ex.Message);
                }
            }
            _tailGone.Clear();
            foreach (KeyValuePair<GameObject, Transform> p in _tailGuns)
                if (p.Key == null) _tailGone.Add(p.Key);
            for (int i = 0; i < _tailGone.Count; i++) _tailGuns.Remove(_tailGone[i]);
        }

        static Transform BuildTailGun(GameObject plane)
        {
            float k = PlayerAn2.K;
            AirStation s = AirStations.Get(AirStations.An2Tail);
            if (_gunMat == null)
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                _gunMat = new Material(shader);
                _gunMat.name = "NDR An-2 tail gun";
                _gunMat.color = new Color(0.16f, 0.17f, 0.15f, 1f);
                _gunMat.hideFlags = HideFlags.HideAndDontSave;
            }
            Transform tr = plane.transform;
            Transform mount = new GameObject("NDR An-2 tail gun mount").transform;
            mount.SetParent(tr, false);
            mount.position = tr.position + tr.rotation * (new Vector3(s.X, s.Y - 0.35f, s.Z) * k);
            mount.rotation = tr.rotation;
            Part(mount, PrimitiveType.Cylinder, new Vector3(0f, 0f, 0f), new Vector3(0.9f, 0.04f, 0.9f), Quaternion.identity, k);
            Transform gun = new GameObject("NDR An-2 tail gun").transform;
            gun.SetParent(mount, false);
            gun.localPosition = new Vector3(0f, 0.3f, 0f) * k;
            gun.localRotation = Quaternion.LookRotation(Vector3.back);
            Part(gun, PrimitiveType.Cube, new Vector3(0f, 0f, 0.1f), new Vector3(0.16f, 0.18f, 0.7f), Quaternion.identity, k);
            Quaternion along = Quaternion.Euler(90f, 0f, 0f);
            Part(gun, PrimitiveType.Cylinder, new Vector3(0f, 0.02f, 0.85f), new Vector3(0.045f, 0.55f, 0.045f), along, k);
            Part(gun, PrimitiveType.Cylinder, new Vector3(0f, 0.02f, 1.42f), new Vector3(0.08f, 0.05f, 0.08f), along, k);
            Part(gun, PrimitiveType.Cube, new Vector3(0f, -0.14f, -0.05f), new Vector3(0.05f, 0.14f, 0.05f), Quaternion.identity, k);
            return gun;
        }

        static void Part(Transform parent, PrimitiveType type, Vector3 at, Vector3 size, Quaternion rot, float k)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = "NDR tail gun part";
            Collider c = go.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.DestroyImmediate(c);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at * k;
            go.transform.localRotation = rot;
            go.transform.localScale = size * k;
            Renderer r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = _gunMat;
        }

        static void PoseTailGun(GameObject plane, Vector3 dir)
        {
            Transform gun;
            if (plane == null || !_tailGuns.TryGetValue(plane, out gun) || gun == null) return;
            if (dir.sqrMagnitude < 0.0001f) return;
            gun.rotation = Quaternion.LookRotation(dir, plane.transform.up);
        }

        // ================================================================= draw

        static string _hudText;
        static int _hudAmmo = int.MinValue, _hudStation = -1, _hudLoaded = -1;
        static float _hudAmmoAt;
        static WeaponCard _hudCard;

        internal static void Draw()
        {
            if (!Enabled || Event.current == null || Event.current.type != EventType.Repaint) return;
            if (GameUi.WindowOpen) return;
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            GameObject craft = _craft != null ? _craft : PlayerAn2.Aboard ? PlayerAn2.Plane : PlayerHeli.Machine;
            if (craft == null) return;
            Vector3 mark;
            bool plane = _craft != null ? _plane : PlayerAn2.Aboard;
            if (MarkFor(craft, plane, out mark)) DrawMark(mark);
            KeyText();

            if (_station != AirStations.None)
            {
                AirStation s = AirStations.Get(_station);
                bool clear = AirStations.Clear(s, _yaw, _pitch);
                Color c = clear ? VanillaUi.White : VanillaUi.Red;
                Box(new Rect(cx - 9f, cy - 1f, 6f, 2f), c);
                Box(new Rect(cx + 3f, cy - 1f, 6f, 2f), c);
                Box(new Rect(cx - 1f, cy - 9f, 2f, 6f), c);
                Box(new Rect(cx - 1f, cy + 3f, 2f, 6f), c);
                // The loaded rounds are reflected (boxed) values: read 4x a second.
                if (s.Heavy) _hudLoaded = _tailBelt;
                else if (Time.time >= _hudAmmoAt) { _hudAmmoAt = Time.time + 0.25f; _hudLoaded = Loaded(); }
                int ammo = _hudLoaded;
                if (_hudText == null || ammo != _hudAmmo || _station != _hudStation || !ReferenceEquals(_card, _hudCard))
                {
                    _hudAmmo = ammo; _hudStation = _station; _hudCard = _card;
                    _hudText = Text.Hud(_station, ammo, s.Heavy ? null : _card == null ? null : _card.Text);
                }
                VanillaUi.Readout(_hudText, cx, cy + 150f, VanillaUi.White, 15);
                if (Time.time - _seatedAt < 12f || !clear)
                    VanillaUi.Readout(_keysText, cx, cy + 172f, VanillaUi.Grey, 13);
                return;
            }
            if (_wasPassenger && Time.time - _seatedAt < 10f)
                VanillaUi.Readout(_takeText, cx, cy + 172f, VanillaUi.Grey, 13);
            if (_orbitOn && PlayerAn2.Flying)
                VanillaUi.Readout(_orbitText, cx, cy + 246f, VanillaUi.Gold, 14);
        }

        static string _keysText, _takeText, _orbitText;
        static KeyCode _textStation = KeyCode.None, _textReload = KeyCode.None;

        /// <summary>The key prompts, rebuilt only when a key or the language
        /// could have changed (no strings per frame).</summary>
        static void KeyText()
        {
            KeyCode st = Key(CfgStationKey, KeyCode.T), re = Key(CfgReloadKey, KeyCode.R);
            if (_keysText != null && st == _textStation && re == _textReload) return;
            _textStation = st; _textReload = re;
            _keysText = Text.StationKeys(st.ToString(), re.ToString());
            _takeText = Text.TakeGun(st.ToString());
            _orbitText = Text.Orbiting();
        }

        static void DrawMark(Vector3 at)
        {
            Camera cam = CameraOwner.ViewCamera();
            if (cam == null) return;
            Vector3 sp = cam.WorldToScreenPoint(at + Vector3.up * 2f);
            if (sp.z <= 0f) return;
            float x = sp.x, y = Screen.height - sp.y;
            Box(new Rect(x - 10f, y - 10f, 20f, 2f), AttackRed);
            Box(new Rect(x - 10f, y + 8f, 20f, 2f), AttackRed);
            Box(new Rect(x - 10f, y - 8f, 2f, 16f), AttackRed);
            Box(new Rect(x + 8f, y - 8f, 2f, 16f), AttackRed);
        }

        static void Box(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            VanillaUi.Texture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        static void Toast(string text, int tone)
        {
            UiKit.Toast(text, tone);
        }

        // ================================================================= text

        internal static class Text
        {
            internal static string Backblast()
            {
                return Loc.T("Гранатомёт нельзя применять с борта - реактивная струя ударит в салон.",
                    "A rocket launcher cannot be fired from an aircraft seat - the backblast.");
            }
            internal static string NeedMark()
            {
                return Loc.T("Сначала отметьте точку на земле колёсиком мыши.",
                    "Mark a ground point with the mouse wheel first.");
            }
            internal static string Airborne() { return Loc.T("Облёт - только в воздухе.", "Orbit only in the air."); }
            internal static string NoGround() { return Loc.T("Под прицелом нет земли.", "No ground under the view's centre."); }
            internal static string Marked(string key)
            {
                return Loc.T("Точка отмечена. [" + key + "] - облёт вокруг неё.",
                    "Point marked. [" + key + "] orbits it.");
            }
            internal static string MarkedHeli() { return Loc.T("Точка отмечена для стрелков.", "Point marked for the gunners."); }
            internal static string OrbitOn(int metres)
            {
                return Loc.T("Облёт: вираж радиусом " + metres + " м, левый борт к цели. Любое управление - отмена.",
                    "Orbit: a " + metres + " m pylon turn, port side to the mark. Any control cancels.");
            }
            internal static string OrbitOff() { return Loc.T("Облёт выключен.", "Orbit off."); }
            internal static string OrbitTaken() { return Loc.T("Управление у пилота - облёт снят.", "Controls taken - orbit off."); }
            internal static string Orbiting() { return Loc.T("ОБЛЁТ ЦЕЛИ", "ORBITING THE MARK"); }
            internal static string CamBusy() { return Loc.T("Обзор сейчас занят.", "The view is busy."); }
            internal static string BackInSeat() { return Loc.T("Вы снова на сиденье.", "Back in the seat."); }
            internal static string TailTaken()
            {
                return Loc.T("Хвостовая установка, 12,7 мм. Сектор - назад и вверх.",
                    "Tail gun, 12.7 mm. Arc: rear and upper.");
            }
            internal static string DoorTaken(string rating)
            {
                return rating == null ? Loc.T("У двери. Оружия в руках нет.", "At the door. No weapon in hand.")
                    : Loc.T("У двери. ", "At the door. ") + rating;
            }
            internal static string NoWeapon() { return Loc.T("Нет оружия в руках.", "No weapon in hand."); }
            internal static string Empty(string key)
            {
                return Loc.T("Магазин пуст - [" + key + "] перезарядка.", "Magazine empty - [" + key + "] reload.");
            }
            internal static string Reloading() { return Loc.T("Заряжаю ленту", "Loading a belt"); }
            internal static string Rating(float r)
            {
                if (r == AirWeaponRating.Forbidden) return Loc.T("С борта нельзя.", "Not from an aircraft.");
                int pct = Mathf.RoundToInt(r * 100f);
                switch (AirWeaponRating.Tier(r))
                {
                    case AirWeaponRating.Excellent: return Loc.T("Для стрельбы с борта: отлично (", "From the air: excellent (") + pct + "%)";
                    case AirWeaponRating.Good: return Loc.T("Для стрельбы с борта: хорошо (", "From the air: good (") + pct + "%)";
                    case AirWeaponRating.Medium: return Loc.T("Для стрельбы с борта: средне (", "From the air: medium (") + pct + "%)";
                    case AirWeaponRating.Weak: return Loc.T("Для стрельбы с борта: слабо (", "From the air: weak (") + pct + "%)";
                    default: return Loc.T("Для стрельбы с борта: почти бесполезно (", "From the air: near useless (") + pct + "%)";
                }
            }
            internal static string Hud(int station, int ammo, string rating)
            {
                string name;
                switch (station)
                {
                    case AirStations.An2Tail: name = Loc.T("ХВОСТ 12,7", "TAIL 12.7"); break;
                    case AirStations.An2Door: name = Loc.T("ДВЕРЬ", "DOOR"); break;
                    case AirStations.Mi8Left: name = Loc.T("ЛЕВАЯ ДВЕРЬ", "LEFT DOOR"); break;
                    default: name = Loc.T("ПРАВАЯ ДВЕРЬ", "RIGHT DOOR"); break;
                }
                string rounds = ammo < 0 ? "" : "   " + ammo;
                return rating == null ? name + rounds : name + rounds + "   " + rating;
            }
            internal static string StationKeys(string station, string reload)
            {
                return Loc.T("[ЛКМ] огонь   [" + reload + "] перезарядка   [" + station + "] следующее место",
                    "[LMB] fire   [" + reload + "] reload   [" + station + "] next station");
            }
            internal static string TakeGun(string key)
            {
                return Loc.T("[" + key + "] занять огневую точку", "[" + key + "] take a gun station");
            }
        }
    }

    // ---- seams into existing systems, kept beside their callers

    internal static partial class MercRide
    {
        /// <summary>This client's mercs seated in an aircraft (Mi-8 or An-2).</summary>
        internal static void AirRiders(List<MercUnit> into)
        {
            into.Clear();
            for (int i = 0; i < _riders.Count; i++)
            {
                MercUnit u = _riders[i];
                if (u == null || u.Ai == null) continue;
                MercCarrier c = u.Ride.Carrier;
                if (c == null || c.Go == null || u.Ride.Seat < 0 || u.Ride.Dying) continue;
                if (c.Kind != MercCarrier.Heli && c.Kind != MercCarrier.Plane) continue;
                into.Add(u);
            }
        }
    }

    public static partial class NpcWar
    {
        /// <summary>The weapon item a merc carries (0 unknown).</summary>
        internal static int AirGunnerWeapon(Component ai)
        {
            Fighter f = ai == null ? null : FighterOf(ai);
            return f == null ? 0 : f.WeaponId;
        }

        /// <summary>The merc's own enemy rule (quick orders): never friends,
        /// peaceful men or the whitelist.</summary>
        internal static bool AirGunnerEnemy(MercUnit u, Component npc)
        {
            return MercQuickEnemy(u, npc, null);
        }

        /// <summary>A merc gunner's round: NpcWar's own hit rules and damage.</summary>
        internal static void AirGunnerMercRound(Component mercAi, GameObject struck, Vector3 impact, float dist)
        {
            Fighter f = mercAi == null ? null : FighterOf(mercAi);
            if (f != null && struck != null) RoundHits(f, struck, impact, dist);
        }

        /// <summary>A player gunner's round: an NPC this client owns takes the
        /// local damage call, one owned elsewhere the game's ApplyDamage RPC
        /// to its owner; animals and players as the technical's gun.</summary>
        internal static bool AirGunnerHit(GameObject struck, float damage, Vector3 impact)
        {
            if (struck == null || damage <= 0f) return false;
            Type npc = _npcType != null ? _npcType : RevivalPlugin.TypeByName("NPC_AI2");
            Component ai = npc == null ? null : struck.GetComponentInParent(npc);
            if (ai != null)
            {
                if (_mIsAlive != null && !Alive(ai)) return false;
                return IsMine(ai) ? Turret.TryDamage(struck, "NPC_AI2", "ApplyDamage", damage)
                    : RemoteHit(ai, damage, impact);
            }
            if (Turret.TryDamage(struck, "Animal_AI", "NetworkApplyDamage", damage)) return true;
            return Turret.TryDamage(struck, "PlayerNetworkController", "PlayerApplyDamage", damage);
        }
    }
}
