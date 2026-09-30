// Stinger: LAW inventory/pose, continuous visual lock, guided physical flight.
// C# 3.0. Network authority follows LAW: shooter owns the blast; host owns helis.
//
// THREE THINGS THIS WEAPON IS NOT, EACH ONE A FIELD REPORT
// --------------------------------------------------------
// 1. NOT A HUD WEAPON. It used to be aimed over four pixels of GUI cross with
//    white squares floating on the open screen. A MANPADS is aimed through the
//    sight clamped to the tube. The sight is the game's own scope path now -
//    weapons_db.xml `Scope` -> Resources.Load -> ResourceHook -> the image
//    stinger_scope.py builds - and this file only draws the live part inside
//    its lens: which target the seeker has, and how far the tone has come.
//    `ScopeUp` says whether the server's weapon record really carries it; if
//    it does not, the old open reticle stays rather than the weapon becoming
//    unaimable, and the log says so once.
// 2. NOT A FIREWORK. "Neither vehicles nor the little drones die from one
//    missile." The blast alone was never the whole answer: a hit on the
//    ARTILLERY RECON DRONE went through ArtyBattery.Shoot, which removes ONE
//    of its three hit points, so three Stingers were needed for a quadcopter.
//    `Kill` no longer asks a shared damage path to be generous - it destroys
//    the bound target outright, one kind at a time, and the blast that follows
//    is only what is left over for anything standing next to it.
// 3. NOT SINGLE USE. That is the LAW (1162), whose tube is scrap after the
//    shot and stays that way. The Stinger is a gripstock: clip item 2068, one
//    missile in the tube, ReloadTime out of weapons_db.xml. Nothing in this
//    file implements the reload - the game does it, once the weapon record
//    names a magazine - which is why the change is three numbers in
//    mods/revival.json and one id in the item table.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static class Stinger
    {
        public const int ItemId = 1165;
        const byte EventCode = 191;
        static readonly string[] RequiredAssets = new string[] {
            "stinger.ndmesh", "stinger_diffuse.png", "stinger_normal.png",
            "stinger_metal.png", "stinger_rough.png", "stinger_icon.png", "stinger_weapon_icon.png",
            "stinger_missile.ndmesh", "stinger_missile_diffuse.png", "stinger_missile_normal.png",
            "stinger_missile_metal.png", "stinger_missile_rough.png",
            // The reload round's inventory icon and the gunner's sight. Listed
            // with the rest on purpose: an install that is missing one Stinger
            // file is a broken install, and one loud line at startup beats a
            // weapon that is quietly half there.
            "stinger_missile_icon.png", "stinger_scope.png" };
        public const int RoundId = 2068;     // the reload, item table in RevivalPlugin.cs
        static ConfigEntry<float> _lockSeconds, _range, _warhead;
        static readonly List<Target> _targets = new List<Target>();
        static readonly List<GameObject> _helis = new List<GameObject>();
        static readonly List<Flight> _flights = new List<Flight>();
        static readonly Dictionary<string, Ghost> _ghosts = new Dictionary<string, Ghost>();
        static readonly List<string> _expired = new List<string>();
        static readonly Dictionary<int, int> _lastLaunch = new Dictionary<int, int>();
        static Component _controller;
        static Component _candidate;
        static Camera _camera;
        static Target _locked;
        static float _held, _scan, _controllerScan;
        static int _serial, _shotFrame = -1;
        static bool _ready, _netReady;
        static MethodInfo _raise;
        static Type _options;
        static Material _missileMaterial;
        static Material _trailMaterial;
        static string _lastError;
        static object _room;
        static MethodInfo _roomGetter, _masterGetter, _sense, _weaponState, _ammoUi;
        static FieldInfo _shootState;
        static FieldInfo _aimField;
        static Type _controllerType;
        static PropertyInfo _controllerView;
        static MethodInfo _viewMine;
        static readonly RaycastHit[] CastHits = new RaycastHit[64];
        static bool _castSaturated;
        static float _visibilityAt;
        static int _visibilityCursor;
        static int _epoch, _loaded = -1;
        static bool _scope;
        static readonly Dictionary<Type, Func<object, int>> ItemReaders = new Dictionary<Type, Func<object, int>>();
        static readonly Dictionary<FieldInfo, Func<object, int>> IntReaders = new Dictionary<FieldInfo, Func<object, int>>();
        static Func<Array, int, int> _roundReader;
        static Type _roundArrayType;
        static readonly List<GepardGun.Contact> SeekerAir = new List<GepardGun.Contact>(32);
        static readonly List<GepardAir.Found> _planes = new List<GepardAir.Found>();

        sealed class Target
        {
            public GameObject Go;
            public Component Vehicle;
            public Vector3 LocalCentre;
            public int Kind, Actor;
            public int HeliView;
            public int Epoch;
            public bool Seen;
            public float CheckedAt = -10f;
            public Vector3 Point { get { return Go.transform.TransformPoint(LocalCentre); } }
        }
        sealed class Flight
        {
            public int Id, Heli, Sequence;
            public Target Target;
            public GameObject Model;
            public Vector3 Position, Direction;
            public float Age, NextSend, Born;
            public bool Diverted;
            public Vector3 DecoyPoint, CastFrom;
            public float NextCollision, NextSeeker;
        }
        sealed class Ghost
        {
            public GameObject Model;
            public Vector3 Position;
            public float Last, Born;
            public int Heli, Sequence;
        }

        public static void BindConfig(ConfigFile cfg)
        {
            _lockSeconds = cfg.Bind("Stinger", "LockSeconds", 1.5f,
                "Continuous time with the reticle inside the target square before firing.");
            _range = cfg.Bind("Stinger", "LockRange", 1200f,
                "Maximum acquisition distance in game world units.");
            _warhead = cfg.Bind("Stinger", "Warhead", 1400f,
                "Blast damage at the impact point. The LOCKED target does not "
                + "depend on this number - a missile that reaches what it was "
                + "aimed at destroys it outright - so this is what is left over "
                + "for whatever stands next to the impact. Deliberately not the "
                + "LAW's 900: VehicleArmor reads that value as a LAW hit and "
                + "gives a tank two of them.");
        }

        public static void Install(Harmony harmony)
        {
            try
            {
                Type ctrl = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                _controllerType = ctrl;
                _controllerView = AccessTools.Property(ctrl, "photonView");
                _aimField = AccessTools.Field(ctrl, "currentAimingState");
                MethodInfo fire = AccessTools.Method(ctrl, "Fire", null, null);
                MethodInfo shot = AccessTools.Method(ctrl, "FireOneShot", null, null);
                if (fire == null || shot == null) throw new MissingMethodException("Stinger fire hooks");
                _sense = AccessTools.Method(ctrl, "SenseOnFireShot", null, null);
                _weaponState = AccessTools.Method(ctrl, "NetworkWeaponState", new Type[] { typeof(int), typeof(int) }, null);
                _ammoUi = AccessTools.Method(ctrl, "UpdateAmmoInfoUI", new Type[] { typeof(bool) }, null);
                _shootState = AccessTools.Field(RevivalPlugin.TypeByName("WeaponState"), "Shoot");
                if (_sense == null || _weaponState == null || _shootState == null || _ammoUi == null)
                    throw new MissingMemberException("Stinger native shot effects");
                harmony.Patch(fire, new HarmonyMethod(typeof(Stinger).GetMethod("FirePrefix")), null, null, null, null);
                harmony.Patch(shot, new HarmonyMethod(typeof(Stinger).GetMethod("ShotPrefix")), null, null, null, null);
                for (int i = 0; i < RequiredAssets.Length; i++)
                    if (!File.Exists(Path.Combine(RevivalPlugin.AssetDir, RequiredAssets[i])))
                        throw new FileNotFoundException("Stinger asset missing", RequiredAssets[i]);
                _ready = true;
            }
            catch (Exception ex) { Warn(ex); }
        }

        static object Field(object obj, string name)
        {
            if (obj == null) return null;
            FieldInfo f = FastField.Find(obj is Type ? (Type)obj : obj.GetType(), name);
            return f == null ? null : f.GetValue(obj is Type ? null : obj);
        }
        static int Int(object value)
        {
            if (value == null) return 0;
            if (value is int) return (int)value;
            foreach (MethodInfo m in value.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "op_Implicit" && m.ReturnType == typeof(int))
                    return (int)m.Invoke(null, new object[] { value });
            return Convert.ToInt32(value);
        }
        static object FromInt(Type type, int value)
        {
            if (type == typeof(int)) return value;
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "op_Implicit" && m.ReturnType == type
                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int))
                    return m.Invoke(null, new object[] { value });
            throw new MissingMethodException(type.FullName, "op_Implicit(int)");
        }
        public static bool IsStinger(object ctrl)
        {
            object data = Field(ctrl, "_weaponFirearmData");
            if (data == null) return false;
            Type type = data.GetType(); Func<object, int> read;
            if (!ItemReaders.TryGetValue(type, out read))
            {
                FieldInfo item = AccessTools.Field(type, "ItemID");
                if (item == null || type.IsValueType) return false;
                MethodInfo convert = item.FieldType == typeof(int) ? null : item.FieldType.GetMethod("op_Implicit",
                    BindingFlags.Public | BindingFlags.Static, null, new Type[] { item.FieldType }, null);
                if (item.FieldType != typeof(int) && (convert == null || convert.ReturnType != typeof(int))) return false;
                DynamicMethod method = new DynamicMethod("StingerItem", typeof(int), new Type[] { typeof(object) }, typeof(Stinger), true);
                ILGenerator il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, type); il.Emit(OpCodes.Ldfld, item);
                if (convert != null) il.Emit(OpCodes.Call, convert);
                il.Emit(OpCodes.Ret);
                read = (Func<object, int>)method.CreateDelegate(typeof(Func<object, int>)); ItemReaders[type] = read;
            }
            return read(data) == ItemId;
        }
        static bool Local(object ctrl)
        {
            object view = _controllerView == null ? null : _controllerView.GetValue(ctrl, null);
            if (view == null) return false;
            if (_viewMine == null) _viewMine = AccessTools.PropertyGetter(view.GetType(), "isMine");
            return _viewMine != null && FastCall.Bool(_viewMine, view);
        }
        static bool Aiming(object ctrl)
        {
            Behaviour b = ctrl as Behaviour;
            if (b == null || !b.isActiveAndEnabled || PlayerHeli.Aboard || Drone.Flying || SurvDrone.Viewing) return false;
            return _aimField != null && FastField.GetBool(_aimField, ctrl) && !Cursor.visible;
        }
        public static bool FirePrefix(object __instance)
        {
            if (!IsStinger(__instance)) return true;
            try { return CanLaunch(__instance); }
            catch (Exception ex) { ResetLock(); Warn(ex); return false; }
        }
        static bool CanLaunch(object ctrl)
        {
            return _ready && _netReady && Local(ctrl) && Aiming(ctrl)
                && ReferenceEquals(ctrl, _controller) && _locked != null
                && _locked.Go != null && _held >= LockTime() && Inside(_locked) && Visible(_locked);
        }
        public static bool ShotPrefix(object __instance)
        {
            if (!IsStinger(__instance)) return true;
            try
            {
                if (!CanLaunch(__instance) || _shotFrame == Time.frameCount) return false;
                object inv = Field(__instance, "_plrInventoryManager");
                object weapons = Field(inv, "_weaponsData");
                Array bullets = Field(weapons, "Bullets") as Array;
                object slotValue = Field(weapons, "CurrentSlotID");
                if (slotValue == null) return false;
                int slot = Int(slotValue);
                if (bullets == null || slot < 0 || slot >= bullets.Length || Int(bullets.GetValue(slot)) <= 0) return false;
                Transform cam = Field(__instance, "MainCamera") as Transform;
                if (cam == null) return false;
                object remaining = FromInt(bullets.GetType().GetElementType(), Int(bullets.GetValue(slot)) - 1);
                Flight f = new Flight();
                f.Id = ++_serial; f.Target = _locked; f.Born = Time.time;
                f.Heli = _locked.Kind == 0 ? PlayerHeli.MissileView(_locked.Go) : 0;
                f.Position = cam.position + cam.forward * 1.2f - cam.up * 0.25f;
                f.Direction = cam.forward; f.Model = Model();
                f.CastFrom = f.Position;
                f.Model.transform.position = f.Position;
                f.Model.transform.rotation = Quaternion.LookRotation(f.Direction);
                bullets.SetValue(remaining, slot);
                _shotFrame = Time.frameCount;
                _flights.Add(f);
                Send(f, 0);
                MissileThreat(f.Target.Go, f.Id);
                _held = 0; _locked = null;
                // Fire() retains rate limiting and the normal firing animation.
                Array show = Field(weapons, "ShowInUI") as Array;
                if (show != null && slot < show.Length) show.SetValue(false, slot);
                _sense.Invoke(__instance, null);
                _weaponState.Invoke(__instance, new object[] { Int(_shootState.GetValue(null)), Int(Field(__instance, "_ShootMode")) });
                if (true.Equals(Field(__instance, "showedAmmoUI"))) _ammoUi.Invoke(__instance, new object[] { true });
                RevivalPlugin.L.LogInfo("Stinger launched: " + f.Id);
            }
            catch (Exception ex) { Warn(ex); }
            return false;
        }

        static float LockTime() { return Mathf.Clamp(_lockSeconds.Value, 0.2f, 10f); }
        internal static float LockSeconds { get { return LockTime(); } }
        static float Range() { return Mathf.Clamp(_range.Value, 20f, 1200f); }
        static float Warhead() { return Mathf.Clamp(_warhead == null ? 1400f : _warhead.Value, 1f, 100000f); }

        // ------------------------------------------------------------- sight
        // stinger_scope.py builds a 1920 square whose lens radius is 524, and
        // ScopeCameraEffect::OnGUI draws it with ScaleMode 1 (ScaleAndCrop):
        // the image is scaled by max(width, height) / 1920 and centred, so the
        // lens on screen is that same factor times 524. Both numbers live in
        // the generator; changing one without the other puts the target boxes
        // outside the glass.
        const float ScopeImage = 1920f;
        const float ScopeLens = 524f;
        static bool _noScopeLogged;

        /// <summary>
        /// True while the player is looking through the Stinger's own sight.
        ///
        /// `CameraSwitch::CantRenderScope` refuses the scope when
        /// `_weaponFirearmData.Scope` is null, and that field is whatever
        /// `Resources.Load` returned for the `Scope` attribute of this weapon's
        /// weapons_db.xml record. A server whose record predates the sight
        /// therefore hands out a Stinger with no glass - and the SERVER wins
        /// that argument, not the plugin. Rather than leave the weapon
        /// unaimable, the old open reticle is drawn instead and the log names
        /// the reason once.
        /// </summary>
        static bool ScopeUp(object ctrl)
        {
            object data = Field(ctrl, "_weaponFirearmData");
            if (data == null) return false;
            bool has = Field(data, "Scope") as Texture != null;
            if (!has && !_noScopeLogged)
            {
                _noScopeLogged = true;
                RevivalPlugin.L.LogWarning("Stinger: the weapon record on this server "
                    + "carries no Scope - the sight cannot be drawn and the plain "
                    + "reticle is used. weapons_db.xml 1165 needs Scope=\""
                    + RevivalPlugin.StingerScopePath + "\".");
            }
            return has;
        }

        static float LensRadius()
        { return Mathf.Max(Screen.width, Screen.height) / ScopeImage * ScopeLens; }

        public static void Tick()
        {
            try
            {
                Network();
                object room = _roomGetter == null ? null : _roomGetter.Invoke(null, null);
                if (!ReferenceEquals(room, _room)) { ClearFlights(); Mi8Flares.Clear(); _room = room; }
                TickFlights();
                Mi8Flares.Tick();
                if (MapTools.LocalPlayer() == null)
                { _controller = null; _candidate = null; _camera = null; ResetLock(); return; }
                if (Time.time >= _controllerScan)
                {
                    _controllerScan = Time.time + 0.3f;
                    Behaviour candidate = _candidate as Behaviour;
                    if (candidate == null || !candidate.isActiveAndEnabled)
                        _candidate = FindController(MapTools.LocalPlayer().transform);
                    Component previous = _controller;
                    _controller = _candidate != null && IsStinger(_candidate) && Local(_candidate) ? _candidate : null;
                    if (_controller != previous) _camera = null;
                    if (_controller != null) { _loaded = Loaded(); _scope = ScopeUp(_controller); }
                }
                if (_controller == null || !Aiming(_controller))
                { _camera = null; ResetLock(); return; }
                if (_camera == null)
                {
                    Transform aimCam = Field(_controller, "MainCamera") as Transform;
                    _camera = aimCam == null ? null : aimCam.GetComponent<Camera>();
                    if (_camera == null && aimCam != null) _camera = aimCam.GetComponentInChildren<Camera>();
                }
                if (_camera == null) { ResetLock(); return; }
                if (Time.time >= _scan) { _scan = Time.time + 0.3f; Scan(); }
                if (Time.time >= _visibilityAt)
                {
                    _visibilityAt = Time.time + 0.2f;
                    int budget = 8;
                    // Reticle candidates get fresh eye-directed LOS first.
                    for (int i = 0; i < _targets.Count && budget > 0; i++)
                        if (Inside(_targets[i])) { CheckVisible(_targets[i]); budget--; }
                    for (int n = 0; n < _targets.Count && budget > 0; n++)
                    {
                        int i = _visibilityCursor++ % _targets.Count;
                        if (_visibilityCursor >= _targets.Count) _visibilityCursor = 0;
                        if (_targets[i].CheckedAt == Time.time) continue;
                        CheckVisible(_targets[i]); budget--;
                    }
                }
                Target best = _locked != null && Inside(_locked) && Seen(_locked) ? _locked : null;
                bool keepTarget = best != null;
                float distance = float.MaxValue;
                for (int i = 0; !keepTarget && i < _targets.Count; i++)
                {
                    Target t = _targets[i];
                    if (t.Go == null || !Inside(t) || !Seen(t)) continue;
                    Vector3 screen = _camera.WorldToScreenPoint(t.Point);
                    float d = (new Vector2(screen.x - Screen.width * 0.5f, screen.y - Screen.height * 0.5f)).sqrMagnitude;
                    if (d < distance) { best = t; distance = d; }
                }
                if (best == null) { ResetLock(); return; }
                _held = AirDefenceCore.Lock(_held, Time.deltaTime,
                    _locked != null && _locked.Go == best.Go, Seen(best), LockTime());
                _locked = best;
            }
            catch (Exception ex) { ResetLock(); Warn(ex); }
        }
        static void ResetLock() { _locked = null; _held = 0f; }
        static bool Seen(Target t) { return t.Seen && Time.time - t.CheckedAt <= 0.5f; }
        static void CheckVisible(Target t) { t.Seen = Visible(t); t.CheckedAt = Time.time; }
        static Component FindController(Transform tr)
        {
            if (_controllerType == null) return null;
            Component c = tr.GetComponent(_controllerType);
            Behaviour b = c as Behaviour;
            if (b != null && b.isActiveAndEnabled) return c;
            for (int i = 0; i < tr.childCount; i++)
            { c = FindController(tr.GetChild(i)); if (c != null) return c; }
            return null;
        }
        static float HalfBox() { return Mathf.Clamp(Screen.height * 0.035f, 18f, 42f); }
        static bool Inside(Target t)
        {
            if (_camera == null || t.Go == null || !t.Go.activeInHierarchy) return false;
            Vector3 p = _camera.WorldToScreenPoint(t.Point);
            return p.z > 0f && Mathf.Abs(p.x - Screen.width * 0.5f) <= HalfBox()
                && Mathf.Abs(p.y - Screen.height * 0.5f) <= HalfBox();
        }
        static bool Visible(Target t)
        {
            if (_camera == null || t.Go == null || !t.Go.activeInHierarchy) return false;
            if (t.Kind == 0 && PlayerHeli.MissileTarget(t.HeliView) == null) return false;
            if (t.Vehicle != null)
            {
                FieldInfo health = FastField.Find(t.Vehicle.GetType(), "Durability");
                if (health != null && health.FieldType == typeof(float) && FastField.GetFloat(health, t.Vehicle) <= 0f) return false;
            }
            Vector3 origin = _camera.transform.position;
            Vector3 delta = t.Point - origin;
            if (delta.magnitude < 5f || delta.magnitude > Range()) return false;
            RaycastHit hit;
            if (!Cast(origin, delta.normalized, delta.magnitude, out hit)) return true;
            return !_castSaturated && (hit.transform == t.Go.transform || hit.transform.IsChildOf(t.Go.transform));
        }
        static void Add(GameObject go, int kind, int actor)
        {
            if (go == null || !go.activeInHierarchy) return;
            if (_camera != null && (go.transform.position - _camera.transform.position).sqrMagnitude > Range() * Range() * 1.3f) return;
            for (int i = 0; i < _targets.Count; i++)
                if (_targets[i].Go == go) { _targets[i].Epoch = _epoch; return; }
            if (_targets.Count >= 64) return;
            Target t = new Target(); t.Go = go; t.Kind = kind; t.Actor = actor;
            if (kind == 0) t.HeliView = PlayerHeli.MissileView(go);
            t.Epoch = _epoch;
            if (kind == 1) t.Vehicle = go.GetComponent(RevivalPlugin.TypeByName("VehicleGameSystem"));
            if (kind < 2 || kind == 6)
            {
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
                bool found = false; Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (!(renderers[i] is MeshRenderer) && !(renderers[i] is SkinnedMeshRenderer)) continue;
                    // The An-2's hidden Mi-8 carrier meshes are off: only the drawn model counts.
                    if (kind == 6 && !renderers[i].enabled) continue;
                    if (!found) { bounds = renderers[i].bounds; found = true; }
                    else bounds.Encapsulate(renderers[i].bounds);
                }
                t.LocalCentre = go.transform.InverseTransformPoint(bounds.center);
            }
            _targets.Add(t);
        }
        internal static void OfferTarget(GameObject go, int kind, int actor) { Add(go, kind, actor); }
        static void Scan()
        {
            _epoch++;
            for (int i = SeekerAir.Count - 1; i >= 0; i--)
                if (SeekerAir[i].Go == null || (SeekerAir[i].Go.transform.position - _camera.transform.position).sqrMagnitude > Range() * Range() * 1.3f)
                    SeekerAir.RemoveAt(i);
            FlakFire.Collect(SeekerAir, _camera.transform.position, Range());
            for (int i = 0; i < SeekerAir.Count; i++)
                if (SeekerAir[i].Kind != 6) Add(SeekerAir[i].Go, SeekerAir[i].Kind, SeekerAir[i].Actor);
            _planes.Clear(); GepardAir.Collect(_planes);
            for (int i = 0; i < _planes.Count; i++) Add(_planes[i].Go, 6, 0);
            foreach (Component c in VehicleScan.All()) if (c != null) Add(c.gameObject, 1, 0);
            CrewDrone.StingerTargets();
            ArtyBattery.StingerTargets();
            for (int i = _targets.Count - 1; i >= 0; i--)
                if (_targets[i].Go == null || _targets[i].Epoch != _epoch) _targets.RemoveAt(i);
            if (_locked != null && _locked.Epoch != _epoch) ResetLock();
        }

        /// <summary>
        /// The live half of the sight picture. The fixed half - lens, mount,
        /// aiming point, lead scale - is the texture the game itself draws, so
        /// nothing here repeats it: only the boxes over the targets the seeker
        /// can see, the tone bar under the one it has, and one line of text.
        ///
        /// Inside the lens, and nowhere else. A box painted over the mount is
        /// a box the gunner cannot be looking at.
        /// </summary>
        public static void Draw()
        {
            if (Event.current != null && Event.current.type != EventType.Repaint) return;
            if (_camera == null || _controller == null || !Aiming(_controller)) return;
            bool sight = _scope;
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            // Keep the whole box inside the glass, not just its centre.
            float lens = sight ? LensRadius() - HalfBox() - 6f : float.MaxValue;
            Color old = GUI.color;
            for (int i = 0; i < _targets.Count; i++)
            {
                Target t = _targets[i];
                if (t.Go == null || !Seen(t)) continue;
                Vector3 p = _camera.WorldToScreenPoint(t.Point);
                if (p.z <= 0 || p.x < 0 || p.x > Screen.width || p.y < 0 || p.y > Screen.height) continue;
                if (new Vector2(p.x - cx, p.y - cy).magnitude > lens) continue;
                bool selected = _locked != null && _locked.Go == t.Go;
                GUI.color = selected ? (_held >= LockTime() ? Color.green : Color.yellow) : Color.white;
                float h = HalfBox(), x = p.x - h, y = Screen.height - p.y - h;
                Line(x,y,h*2,2); Line(x,y+h*2,h*2,2); Line(x,y,2,h*2); Line(x+h*2,y,2,h*2);
                if (selected) Line(x, y + h * 2 + 5, h * 2 * _held / LockTime(), 4);
            }
            GUI.color = Color.white;
            // The sight brings its own aiming point; a second cross on top of
            // it is two crosses. Without a sight this is the only one there is.
            if (!sight) { Line(cx-7,cy,14,1); Line(cx,cy-7,1,14); }
            // == 0, not <= 0: Loaded returns -1 when the count could not be
            // read, and an unreadable count is not an empty tube.
            string text = _loaded == 0
                ? Loc.T("ТРУБА ПУСТА - ПЕРЕЗАРЯДИТЕ", "TUBE EMPTY - RELOAD")
                : _held >= LockTime() ? Loc.T("ЦЕЛЬ ЗАХВАЧЕНА", "TARGET LOCKED")
                : _held > 0 ? Loc.T("ЗАХВАТ ЦЕЛИ", "ACQUIRING")
                : Loc.T("Удерживайте точку прицеливания на цели",
                        "Hold the aiming point on a target");
            // Under the lens when there is one, so the line never sits across
            // the glass the gunner is searching through.
            float ty = sight ? cy + LensRadius() * 0.62f : cy + 75f;
            GUI.Label(new Rect(cx - 180f, Mathf.Min(ty, Screen.height - 40f), 360f, 30f), text);
            GUI.color = old;
        }

        /// <summary>Missiles in the tube, or -1 when that cannot be read. The
        /// weapon is reloadable now, so "empty" is a state the gunner stands
        /// in and has to act on, not the end of the weapon.</summary>
        static int Loaded()
        {
            try
            {
                object weapons = Field(Field(_controller, "_plrInventoryManager"), "_weaponsData");
                Array bullets = Field(weapons, "Bullets") as Array;
                if (bullets == null || weapons == null) return -1;
                FieldInfo slotField = FastField.Find(weapons.GetType(), "CurrentSlotID");
                if (slotField == null) return -1;
                Func<object, int> readSlot;
                if (!IntReaders.TryGetValue(slotField, out readSlot))
                {
                    DynamicMethod method = new DynamicMethod("StingerSlot", typeof(int), new Type[] { typeof(object) }, typeof(Stinger), true);
                    ILGenerator il = method.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, slotField.DeclaringType); il.Emit(OpCodes.Ldfld, slotField);
                    EmitInt(il, slotField.FieldType); il.Emit(OpCodes.Ret);
                    readSlot = (Func<object, int>)method.CreateDelegate(typeof(Func<object, int>)); IntReaders[slotField] = readSlot;
                }
                int slot = readSlot(weapons);
                if (slot < 0 || slot >= bullets.Length) return -1;
                if (_roundArrayType != bullets.GetType())
                {
                    _roundArrayType = bullets.GetType();
                    Type element = _roundArrayType.GetElementType();
                    DynamicMethod method = new DynamicMethod("StingerRound", typeof(int), new Type[] { typeof(Array), typeof(int) }, typeof(Stinger), true);
                    ILGenerator il = method.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, _roundArrayType); il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldelem, element); EmitInt(il, element); il.Emit(OpCodes.Ret);
                    _roundReader = (Func<Array, int, int>)method.CreateDelegate(typeof(Func<Array, int, int>));
                }
                return _roundReader(bullets, slot);
            }
            catch { return -1; }
        }
        static void EmitInt(ILGenerator il, Type type)
        {
            if (type == typeof(int)) return;
            MethodInfo convert = type.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { type }, null);
            if (convert == null || convert.ReturnType != typeof(int)) throw new MissingMethodException(type.FullName, "op_Implicit(int)");
            il.Emit(OpCodes.Call, convert);
        }
        static void Line(float x,float y,float w,float h)
        { GUI.DrawTexture(new Rect(x,y,w,h), Texture2D.whiteTexture); }

        static GameObject Model()
        {
            Mesh mesh = Assets.Load("stinger_missile.ndmesh");
            if (mesh == null) throw new InvalidOperationException("Stinger missile mesh unavailable");
            GameObject go = new GameObject("NDR_StingerMissile");
            if (mesh != null)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                if (_missileMaterial == null)
                {
                    Shader shader = Shader.Find("Standard");
                    if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                    if (shader == null) throw new InvalidOperationException("Stinger missile shader missing");
                    _missileMaterial = new Material(shader);
                    _missileMaterial.mainTexture = Assets.Texture("stinger_missile_diffuse.png", false, true);
                    _missileMaterial.SetTexture("_BumpMap", Assets.Texture("stinger_missile_normal.png", true, true));
                    _missileMaterial.EnableKeyword("_NORMALMAP");
                    if (_missileMaterial.HasProperty("_MetallicGlossMap"))
                    {
                        _missileMaterial.SetTexture("_MetallicGlossMap", Assets.Texture("stinger_missile_metal.png", true, true));
                        _missileMaterial.EnableKeyword("_METALLICGLOSSMAP");
                    }
                }
                go.AddComponent<MeshRenderer>().sharedMaterial = _missileMaterial;
            }
            if (_trailMaterial == null)
            {
                Shader shader = Shader.Find("Particles/Additive");
                if (shader != null) _trailMaterial = new Material(shader);
            }
            if (_trailMaterial != null)
            {
                TrailRenderer trail = go.AddComponent<TrailRenderer>();
                trail.sharedMaterial = _trailMaterial;
                trail.time = 0.3f; trail.startWidth = 0.18f; trail.endWidth = 0.02f;
                trail.startColor = new Color(1f, 0.65f, 0.15f, 0.9f);
                trail.endColor = new Color(0.7f, 0.25f, 0.05f, 0f);
            }
            return go;
        }
        // The local body must not block a ray launched from its own camera.
        static bool Cast(Vector3 origin, Vector3 direction, float distance, out RaycastHit nearest)
        { return CastIgnoring(origin, direction, distance, null, out nearest); }

        static bool CastIgnoring(Vector3 origin, Vector3 direction, float distance, Transform ignore, out RaycastHit nearest)
        {
            nearest = new RaycastHit();
            float best = float.MaxValue;
            GameObject body = MapTools.LocalPlayer();
            int count = Physics.RaycastNonAlloc(origin, direction, CastHits, distance, ~0, QueryTriggerInteraction.Ignore);
            _castSaturated = count == CastHits.Length;
            for (int i = 0; i < count; i++)
            {
                Transform tr = CastHits[i].transform;
                if (ignore != null && (tr == ignore || tr.IsChildOf(ignore))) continue;
                if (tr == null || (body != null && (tr == body.transform || tr.IsChildOf(body.transform)))) continue;
                if (CastHits[i].distance >= best) continue;
                nearest = CastHits[i]; best = CastHits[i].distance;
            }
            if (_castSaturated && best == float.MaxValue) { nearest = CastHits[0]; return true; }
            return best < float.MaxValue;
        }

        internal static bool MercVisible(Component merc, GepardGun.Contact c, Vector3 origin)
        {
            if (merc == null || c.Go == null || _flights.Count >= 32) return false;
            Vector3 d = c.Pos - origin;
            if (d.sqrMagnitude < 25f || d.sqrMagnitude > Range() * Range()) return false;
            RaycastHit hit;
            bool blocked = CastIgnoring(origin, d.normalized, d.magnitude, merc.transform, out hit);
            return !_castSaturated && (!blocked || hit.transform == c.Go.transform || hit.transform.IsChildOf(c.Go.transform));
        }

        internal static bool LaunchMerc(GepardGun.Contact c, Vector3 origin)
        {
            if (!_ready || !_netReady || c.Go == null || _flights.Count >= 32) return false;
            Target t = new Target(); t.Go = c.Go; t.Kind = c.Kind; t.Actor = c.Actor;
            t.LocalCentre = c.Go.transform.InverseTransformPoint(c.Pos);
            Flight f = new Flight(); f.Id = ++_serial; f.Target = t; f.Born = Time.time;
            f.Heli = c.Kind == 0 ? PlayerHeli.MissileView(c.Go) : 0;
            f.Direction = (c.Pos - origin).normalized;
            f.Position = origin + f.Direction * 2f; f.CastFrom = f.Position; f.Model = Model();
            f.Model.transform.position = f.Position; f.Model.transform.rotation = Quaternion.LookRotation(f.Direction);
            _flights.Add(f); Send(f, 0); MissileThreat(c.Go, f.Id); return true;
        }

        static void ClearFlights()
        {
            for (int i = 0; i < _flights.Count; i++)
                if (_flights[i].Model != null) UnityEngine.Object.Destroy(_flights[i].Model);
            foreach (Ghost g in _ghosts.Values)
                if (g.Model != null) UnityEngine.Object.Destroy(g.Model);
            _flights.Clear(); _ghosts.Clear(); _lastLaunch.Clear();
            _targets.Clear(); _controller = null; _camera = null; ResetLock();
            _candidate = null;
            SeekerAir.Clear();
        }

        static void TickFlights()
        {
            if (MapTools.LocalPlayer() == null) { ClearFlights(); return; }
            for (int i = _flights.Count - 1; i >= 0; i--)
            {
                Flight f = _flights[i];
                f.Age = Time.time - f.Born;
                if (f.Age > 12f || (!f.Diverted && (f.Target.Go == null || !f.Target.Go.activeInHierarchy
                    || (f.Heli != 0 && PlayerHeli.MissileTarget(f.Heli) == null))))
                { _flights.RemoveAt(i); Finish(f, false, false); continue; }
                if (!f.Diverted && Time.time >= f.NextSeeker)
                {
                    f.NextSeeker = Time.time + 0.1f;
                    f.Diverted = Mi8Flares.Decoy(f.Target.Go, f.Position, f.Direction, out f.DecoyPoint);
                }
                bool ended = false;
                float remaining = Mathf.Min(Time.deltaTime, 0.25f);
                // Short substeps give the same turn rate at ordinary frame rates.
                while (remaining > 0f)
                {
                    float dt = Mathf.Min(remaining, 0.025f); remaining -= dt;
                    Vector3 toward = (f.Diverted ? f.DecoyPoint : f.Target.Point) - f.Position;
                    f.Direction = Vector3.RotateTowards(f.Direction, toward.normalized, 1.8f * dt, 0f).normalized;
                    float step = 160f * dt;
                    Vector3 previous = f.Position;
                    float along = Mathf.Clamp(Vector3.Dot(toward, f.Direction), 0f, step);
                    bool nearTarget = (toward - f.Direction * along).sqrMagnitude < 1.44f;
                    RaycastHit hit;
                    // Test only up to the target when it lies within this segment:
                    // a wall BEHIND a drone must not swallow the drone impact.
                    Vector3 proposed = previous + f.Direction * (nearTarget ? along : step);
                    Vector3 swept = proposed - f.CastFrom;
                    if ((nearTarget || Time.time >= f.NextCollision)
                        && Cast(f.CastFrom, swept.normalized, swept.magnitude, out hit))
                    {
                        f.Position = hit.point;
                        bool targetHit = !f.Diverted && !_castSaturated && (hit.transform == f.Target.Go.transform || hit.transform.IsChildOf(f.Target.Go.transform));
                        _flights.RemoveAt(i); Finish(f, targetHit, !f.Diverted); ended = true; break;
                    }
                    if (nearTarget || Time.time >= f.NextCollision)
                    { f.NextCollision = Time.time + 0.1f; f.CastFrom = proposed; }
                    if (nearTarget)
                    {
                        f.Position = previous + f.Direction * along;
                        _flights.RemoveAt(i); Finish(f, !f.Diverted, !f.Diverted); ended = true; break;
                    }
                    f.Position += f.Direction * step;
                }
                if (ended) continue;
                f.Model.transform.position = f.Position;
                f.Model.transform.rotation = Quaternion.LookRotation(f.Direction);
                if (Time.time >= f.NextSend) { f.NextSend = Time.time + 0.1f; Send(f, 1); }
            }
            _expired.Clear();
            foreach (KeyValuePair<string, Ghost> pair in _ghosts)
            {
                Ghost g = pair.Value;
                if (Time.time - g.Last > 3f || Time.time - g.Born > 15f)
                { if (g.Model != null) UnityEngine.Object.Destroy(g.Model); _expired.Add(pair.Key); }
                else if (g.Model != null) g.Model.transform.position = Vector3.Lerp(g.Model.transform.position, g.Position, Mathf.Min(1f,Time.deltaTime*15f));
            }
            for (int i = 0; i < _expired.Count; i++) _ghosts.Remove(_expired[i]);
        }

        static void Finish(Flight f, bool targetHit, bool impact)
        {
            if (targetHit && Mi8Flares.Protects(f.Target.Go)) { targetHit = false; impact = false; }
            // Remove from the live list BEFORE any external damage/effect call.
            // A failed effect can never turn into another detonation next frame.
            try
            {
                Send(f, targetHit && f.Heli != 0 ? 3 : 2);
                if (targetHit && f.Target.Go != null) Kill(f);
                if (impact) RocketHook.Detonate(f.Position, Warhead(), 12f, 3f);
            }
            catch (Exception ex) { Warn(ex); }
            finally { if (f.Model != null) UnityEngine.Object.Destroy(f.Model); }
        }

        /// <summary>
        /// The missile reached what it was locked onto. That target dies here,
        /// with no help from the blast that follows.
        ///
        /// The old version handed every kind a number and hoped it was enough,
        /// and for two of the five it was not:
        ///
        ///   * A VEHICLE got nothing of its own at all - only the blast, at the
        ///     LAW's 900, which `VehicleArmor` reads back as a LAW hit and
        ///     halves against a tank. Two Stingers for one tank.
        ///   * The ARTILLERY RECON DRONE went through `ArtyBattery.Shoot`,
        ///     which is the entry point for a RIFLE ROUND: it takes one of the
        ///     three hit points that drone carries. Three Stingers for a
        ///     quadcopter.
        ///
        /// The FPV drone (3 hp), the recon drone (4) and a crew drone all died
        /// to the flat 100 already; they are raised to Lethal anyway so that no
        /// future hit-point change quietly makes the missile survivable again.
        /// </summary>
        static void Kill(Flight f)
        {
            // Whatever hit points a thing has, one missile is more than all of
            // them. Not float.MaxValue: these numbers travel over Photon as
            // floats and are subtracted from, and an infinity in a packet is
            // the kind of value a receiver rejects.
            const float Lethal = 100000f;
            if (f.Heli != 0)
            {
                // The helicopter path is unchanged and is already one shot:
                // MissileImpact burns the machine. Only the master may say so.
                if (RevivalTroopInsertion.MasterClient()) ApproveHeli(f.Heli, f.Position);
                return;
            }
            if (f.Target.Kind == 1) VehicleArmor.MissileKill(f.Target.Vehicle);
            else if (f.Target.Kind == 2)
                Drone.Net.Send(Drone.Net.Treffer, f.Target.Point,
                               new Vector3(f.Target.Actor, 0, 0), Lethal, true);
            else if (f.Target.Kind == 3)
                SurvNet.Send(SurvNet.Treffer, f.Target.Point,
                             new Vector3(f.Target.Actor, 0, 0), Lethal, true);
            else if (f.Target.Kind == 4)
                CrewDrone.Beschuss(f.Target.Point - f.Direction * 4f, f.Direction, 8f, Lethal);
            else if (f.Target.Kind == 6)
            {
                if (AirKills.TroopSide(f.Target.Go) != null) AirKills.StingerHit(f.Target.Go, f.Position);
                else GepardAir.Kill(f.Target.Go, f.Position);
            }
            else if (f.Target.Kind == 5)
            {
                // ArtyBattery.Shoot removes ONE of Post.DroneHits (3) and
                // ignores a drone already at zero, so the same shot line is
                // offered until there is nothing left to take. Four passes is
                // the three it has plus one that finds nothing - cheaper and
                // far more honest than a second entry point in a file this
                // feature does not own.
                for (int i = 0; i < 4; i++)
                    ArtyBattery.Shoot(f.Target.Point - f.Direction * 4f, f.Direction);
            }
        }

        static void ApproveHeli(int view, Vector3 point)
        {
            if (!RevivalTroopInsertion.MasterClient()) return;
            GameObject go = PlayerHeli.MissileTarget(view);
            if (go == null || Mi8Flares.Protects(go)) return;
            // The target was already bound to this shot at launch. Verify an
            // impact at the current fuselage (with a small network allowance).
            Collider[] hull = go.GetComponentsInChildren<Collider>();
            bool close = false;
            for (int i = 0; i < hull.Length; i++)
                if (hull[i].enabled && !hull[i].isTrigger && hull[i].bounds.SqrDistance(point) <= 100f)
                { close = true; break; }
            if (!close) return;
            SendPacket(0, 4, view, point, Vector3.forward, 0);
            PlayerHeli.MissileImpact(view, point);
        }

        static bool Reserved(int first, int count)
        { return EventCode >= first && EventCode < first + count; }

        static void Network()
        {
            if (_netReady) return;
            if (Reserved(RevivalPlugin.CfgDroneEventCode.Value, 5)
                || Reserved(DroneGear.CfgSurvEventCode.Value, 4)
                || Reserved(RevivalTroopInsertion.CfgEventCode.Value, 3)
                || Reserved(PlayerHeli.CfgEventCode.Value, 6)
                || EventCode == RevivalPlugin.CfgTurretEventCode.Value
                || EventCode == RevivalPlugin.CfgAdminEventCode.Value
                || EventCode == RevivalPlugin.CfgPatrolCrewDroneEventCode.Value)
                throw new InvalidOperationException("Stinger event 191 overlaps another configured channel");
            ConfigEntry<int> mortarCode = Field(typeof(Mortar), "_cfgEventCode") as ConfigEntry<int>;
            if (mortarCode != null && mortarCode.Value == EventCode)
                throw new InvalidOperationException("Stinger event 191 overlaps the mortar channel");
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            FieldInfo ev = photon == null ? null : AccessTools.Field(photon,"OnEventCall");
            if (ev == null) return;
            _raise = AccessTools.Method(photon,"RaiseEvent",null,null);
            _options = RevivalPlugin.TypeByName("RaiseEventOptions");
            _roomGetter = AccessTools.PropertyGetter(photon, "room");
            _masterGetter = AccessTools.PropertyGetter(photon, "masterClient");
            if (_raise == null || _options == null || _roomGetter == null || _masterGetter == null) return;
            Delegate callback = Delegate.CreateDelegate(ev.FieldType,typeof(Stinger).GetMethod("OnEvent"));
            ev.SetValue(null,Delegate.Combine(ev.GetValue(null) as Delegate,callback));
            _netReady = true;
        }

        static bool FromMaster(int sender)
        {
            object master = _masterGetter == null ? null : _masterGetter.Invoke(null, null);
            if (master == null) return false;
            PropertyInfo id = AccessTools.Property(master.GetType(), "ID");
            return id != null && Int(id.GetValue(master, null)) == sender;
        }

        static void Send(Flight f, int phase)
        {
            try { SendPacket(f.Id, phase, f.Heli, f.Position, f.Direction, ++f.Sequence); }
            catch (Exception ex) { Warn(ex); }
        }

        static void SendPacket(int id, int phase, int heli, Vector3 point, Vector3 direction, int sequence)
        {
            if (!_netReady) return;
            _raise.Invoke(null,new object[]{EventCode,new object[]{"stinger-v1",id,phase,heli,
                new float[]{point.x,point.y,point.z,direction.x,direction.y,direction.z},sequence},phase!=1,Activator.CreateInstance(_options)});
        }

        internal static void CountermeasurePacket(int phase, int view, int serial, Vector3 point, Vector3 state)
        { SendPacket(serial, phase, view, point, state, 0); }

        static void MissileThreat(GameObject go, int serial)
        {
            int view = PlayerAn2.View(go);
            if (MercAA.Authority) Mi8Flares.Threat(view);
            else CountermeasurePacket(8, view, serial, go.transform.position, Vector3.zero);
        }

        public static void OnEvent(byte code,object content,int sender)
        {
            if (code != EventCode || !_netReady) return;
            try
            {
                object[] p = content as object[];
                if (p == null || p.Length != 6 || !(p[0] is string) || (string)p[0] != "stinger-v1"
                    || !(p[1] is int) || !(p[2] is int) || !(p[3] is int) || !(p[5] is int)) return;
                float[] v = p[4] as float[];
                if (v == null || v.Length != 6) return;
                for (int i=0;i<v.Length;i++) if(float.IsNaN(v[i])||float.IsInfinity(v[i])) return;
                Vector3 point = new Vector3(v[0],v[1],v[2]);
                int phase=(int)p[2], heli=(int)p[3], serial=(int)p[1], sequence=(int)p[5];
                if (phase == 5) { Mi8Flares.RequestFrom(heli, sender); return; }
                if (phase == 6)
                {
                    if (FromMaster(sender)) Mi8Flares.Apply(heli, serial, point, new Vector3(v[3], v[4], v[5]));
                    return;
                }
                if (phase == 7) { Mi8Flares.Snapshot(); return; }
                if (phase == 8)
                {
                    Ghost shot;
                    if (serial > 0 && _ghosts.TryGetValue(sender + ":" + serial, out shot)
                        && Time.time - shot.Born < 1f && (shot.Position - point).sqrMagnitude <= Range() * Range())
                        Mi8Flares.Threat(heli);
                    return;
                }
                if (phase == 4)
                {
                    if (FromMaster(sender)) PlayerHeli.MissileImpact(heli, point);
                    return;
                }
                if (phase < 0 || phase > 3 || serial <= 0 || sequence <= 0 || sender <= 0) return;
                string key=sender+":"+serial; Ghost g;
                if (!_ghosts.TryGetValue(key,out g))
                {
                    int last; _lastLaunch.TryGetValue(sender, out last);
                    if (phase!=0 || serial<=last || _ghosts.Count>=64) return;
                    _lastLaunch[sender]=serial;
                    g=new Ghost();g.Model=Model();g.Model.transform.position=point;
                    g.Born=Time.time;g.Last=Time.time;g.Position=point;g.Heli=heli;
                    _ghosts.Add(key,g);
                }
                if (sequence <= g.Sequence || heli != g.Heli || Time.time-g.Born>15f) return;
                if (Vector3.Distance(g.Position,point)>160f*Mathf.Max(0.1f,Time.time-g.Last)+40f) return;
                g.Sequence=sequence;
                if (phase==2 || phase==3)
                {
                    _ghosts.Remove(key);
                    if (g.Model != null) UnityEngine.Object.Destroy(g.Model);
                    if (phase==3 && heli!=0) ApproveHeli(heli,point);
                    return;
                }
                g.Position=point;g.Last=Time.time;
                Vector3 dir=new Vector3(v[3],v[4],v[5]);
                if(dir.sqrMagnitude>0.01f)g.Model.transform.rotation=Quaternion.LookRotation(dir);
            }
            catch(Exception ex){Warn(ex);}
        }
        static void Warn(Exception ex)
        {
            string message=ex.GetType().Name+": "+ex.Message;
            if(_lastError==message)return;_lastError=message;
            RevivalPlugin.L.LogWarning("Stinger: "+message);
        }
    }
}
