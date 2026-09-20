// Stinger: LAW inventory/pose, continuous visual lock, guided physical flight.
// C# 3.0. Network authority follows LAW: shooter owns the blast; host owns helis.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
            "stinger_missile_metal.png", "stinger_missile_rough.png" };
        static ConfigEntry<float> _lockSeconds, _range;
        static readonly List<Target> _targets = new List<Target>();
        static readonly List<GameObject> _helis = new List<GameObject>();
        static readonly List<Flight> _flights = new List<Flight>();
        static readonly Dictionary<string, Ghost> _ghosts = new Dictionary<string, Ghost>();
        static readonly List<string> _expired = new List<string>();
        static readonly Dictionary<int, int> _lastLaunch = new Dictionary<int, int>();
        static Component _controller;
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

        sealed class Target
        {
            public GameObject Go;
            public Component Vehicle;
            public Vector3 LocalCentre;
            public int Kind, Actor;
            public Vector3 Point { get { return Go.transform.TransformPoint(LocalCentre); } }
        }
        sealed class Flight
        {
            public int Id, Heli, Sequence;
            public Target Target;
            public GameObject Model;
            public Vector3 Position, Direction;
            public float Age, NextSend, Born;
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
        }

        public static void Install(Harmony harmony)
        {
            try
            {
                Type ctrl = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
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
            FieldInfo f = AccessTools.Field(obj is Type ? (Type)obj : obj.GetType(), name);
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
        { return Int(Field(Field(ctrl, "_weaponFirearmData"), "ItemID")) == ItemId; }
        static bool Local(object ctrl)
        {
            PropertyInfo p = AccessTools.Property(ctrl.GetType(), "photonView");
            object view = p == null ? null : p.GetValue(ctrl, null);
            PropertyInfo mine = view == null ? null : AccessTools.Property(view.GetType(), "isMine");
            return mine != null && (bool)mine.GetValue(view, null);
        }
        static bool Aiming(object ctrl)
        {
            Behaviour b = ctrl as Behaviour;
            if (b == null || !b.isActiveAndEnabled || PlayerHeli.Aboard || Drone.Flying || SurvDrone.Viewing) return false;
            object aim = Field(ctrl, "currentAimingState");
            return aim is bool && (bool)aim && !Cursor.visible;
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
                f.Model.transform.position = f.Position;
                f.Model.transform.rotation = Quaternion.LookRotation(f.Direction);
                bullets.SetValue(remaining, slot);
                _shotFrame = Time.frameCount;
                _flights.Add(f);
                Send(f, 0);
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
        static float Range() { return Mathf.Clamp(_range.Value, 20f, 1200f); }
        public static void Tick()
        {
            try
            {
                Network();
                object room = _roomGetter == null ? null : _roomGetter.Invoke(null, null);
                if (!ReferenceEquals(room, _room)) { ClearFlights(); _room = room; }
                TickFlights();
                if (MapTools.LocalPlayer() == null)
                { _controller = null; _camera = null; ResetLock(); return; }
                if (Time.time >= _controllerScan)
                {
                    _controllerScan = Time.time + 0.3f;
                    _controller = null;
                    Type type = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                    if (type != null)
                        foreach (Component obj in MapTools.LocalPlayer().GetComponentsInChildren(type, true))
                        {
                            Component c = obj as Component;
                            if (c != null && IsStinger(c) && Local(c)) { _controller = c; break; }
                        }
                }
                if (_controller == null || !IsStinger(_controller) || !Aiming(_controller))
                { _camera = null; ResetLock(); return; }
                Transform aimCam = Field(_controller, "MainCamera") as Transform;
                _camera = aimCam == null ? null : aimCam.GetComponent<Camera>();
                if (_camera == null && aimCam != null) _camera = aimCam.GetComponentInChildren<Camera>();
                if (_camera == null) { ResetLock(); return; }
                if (Time.time >= _scan) { _scan = Time.time + 0.3f; Scan(); }
                Target best = _locked != null && Inside(_locked) && Visible(_locked) ? _locked : null;
                bool keepTarget = best != null;
                float distance = float.MaxValue;
                for (int i = 0; !keepTarget && i < _targets.Count; i++)
                {
                    Target t = _targets[i];
                    if (t.Go == null || !Inside(t) || !Visible(t)) continue;
                    Vector3 screen = _camera.WorldToScreenPoint(t.Point);
                    float d = (new Vector2(screen.x - Screen.width * 0.5f, screen.y - Screen.height * 0.5f)).sqrMagnitude;
                    if (d < distance) { best = t; distance = d; }
                }
                if (best == null) { ResetLock(); return; }
                if (_locked == null || _locked.Go != best.Go) { _held = 0f; _locked = best; }
                else _locked = best;
                _held = Mathf.Min(LockTime(), _held + Mathf.Min(Time.deltaTime, 0.1f));
            }
            catch (Exception ex) { ResetLock(); Warn(ex); }
        }
        static void ResetLock() { _locked = null; _held = 0f; }
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
            if (t.Kind == 0 && PlayerHeli.MissileTarget(PlayerHeli.MissileView(t.Go)) == null) return false;
            object health = t.Vehicle == null ? null : Field(t.Vehicle, "Durability");
            if (health is float && (float)health <= 0f) return false;
            Vector3 origin = _camera.transform.position;
            Vector3 delta = t.Point - origin;
            if (delta.magnitude < 5f || delta.magnitude > Range()) return false;
            RaycastHit hit;
            if (!Cast(origin, delta.normalized, delta.magnitude, out hit)) return true;
            return hit.transform == t.Go.transform || hit.transform.IsChildOf(t.Go.transform);
        }
        static void Add(GameObject go, int kind, int actor)
        {
            if (go == null || !go.activeInHierarchy) return;
            for (int i = 0; i < _targets.Count; i++) if (_targets[i].Go == go) return;
            Target t = new Target(); t.Go = go; t.Kind = kind; t.Actor = actor;
            if (kind == 1) t.Vehicle = go.GetComponent(RevivalPlugin.TypeByName("VehicleGameSystem"));
            if (kind < 2)
            {
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
                bool found = false; Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (!(renderers[i] is MeshRenderer) && !(renderers[i] is SkinnedMeshRenderer)) continue;
                    if (!found) { bounds = renderers[i].bounds; found = true; }
                    else bounds.Encapsulate(renderers[i].bounds);
                }
                t.LocalCentre = go.transform.InverseTransformPoint(bounds.center);
            }
            _targets.Add(t);
        }
        static void Collection(Type type, string name, string goField, int kind)
        {
            object collection = Field(type, name);
            IDictionary dict = collection as IDictionary;
            if (dict != null)
            {
                foreach (DictionaryEntry pair in dict)
                    Add(Field(pair.Value, goField) as GameObject, kind, pair.Key is int ? (int)pair.Key : 0);
                return;
            }
            IEnumerable list = collection as IEnumerable;
            if (list == null) return;
            foreach (object item in list) Add(item as GameObject ?? Field(item, goField) as GameObject, kind, 0);
        }
        static void Scan()
        {
            _targets.Clear();
            _helis.Clear(); PlayerHeli.MissileTargets(_helis);
            for (int i = 0; i < _helis.Count; i++) Add(_helis[i], 0, 0);
            foreach (Component c in VehicleScan.All()) if (c != null) Add(c.gameObject, 1, 0);
            Collection(typeof(Drone.Net), "_fremde", "Go", 2);
            Collection(typeof(SurvNet), "_ghosts", "Go", 3);
            Collection(typeof(CrewDrone), "_local", "Go", 4);
            Collection(typeof(CrewDrone), "_remote", "Go", 4);
            Collection(typeof(ArtyBattery), "_posts", "DroneModel", 5);
            Collection(typeof(ArtyBattery), "_ghosts", "DroneModel", 5);
        }

        public static void Draw()
        {
            if (Event.current != null && Event.current.type != EventType.Repaint) return;
            if (_camera == null || _controller == null || !Aiming(_controller)) return;
            Color old = GUI.color;
            for (int i = 0; i < _targets.Count; i++)
            {
                Target t = _targets[i];
                if (t.Go == null || !Visible(t)) continue;
                Vector3 p = _camera.WorldToScreenPoint(t.Point);
                if (p.z <= 0 || p.x < 0 || p.x > Screen.width || p.y < 0 || p.y > Screen.height) continue;
                bool selected = _locked != null && _locked.Go == t.Go;
                GUI.color = selected ? (_held >= LockTime() ? Color.green : Color.yellow) : Color.white;
                float h = HalfBox(), x = p.x - h, y = Screen.height - p.y - h;
                Line(x,y,h*2,2); Line(x,y+h*2,h*2,2); Line(x,y,2,h*2); Line(x+h*2,y,2,h*2);
                if (selected) Line(x, y + h * 2 + 5, h * 2 * _held / LockTime(), 4);
            }
            GUI.color = Color.white;
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            Line(cx-7,cy,14,1); Line(cx,cy-7,1,14);
            string text = _held >= LockTime() ? Loc.T("ЦЕЛЬ ЗАХВАЧЕНА", "TARGET LOCKED")
                : _held > 0 ? Loc.T("ЗАХВАТ ЦЕЛИ", "ACQUIRING")
                : Loc.T("Удерживайте прицел в квадрате", "Hold the reticle inside a target square");
            GUI.Label(new Rect(cx-160,cy+75,360,30), text);
            GUI.color = old;
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
        {
            nearest = new RaycastHit();
            float best = float.MaxValue;
            GameObject body = MapTools.LocalPlayer();
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, distance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Transform tr = hits[i].transform;
                if (tr == null || (body != null && (tr == body.transform || tr.IsChildOf(body.transform)))) continue;
                if (hits[i].distance >= best) continue;
                nearest = hits[i]; best = hits[i].distance;
            }
            return best < float.MaxValue;
        }

        static void ClearFlights()
        {
            for (int i = 0; i < _flights.Count; i++)
                if (_flights[i].Model != null) UnityEngine.Object.Destroy(_flights[i].Model);
            foreach (Ghost g in _ghosts.Values)
                if (g.Model != null) UnityEngine.Object.Destroy(g.Model);
            _flights.Clear(); _ghosts.Clear(); _lastLaunch.Clear();
            _targets.Clear(); _controller = null; _camera = null; ResetLock();
        }

        static void TickFlights()
        {
            if (MapTools.LocalPlayer() == null) { ClearFlights(); return; }
            for (int i = _flights.Count - 1; i >= 0; i--)
            {
                Flight f = _flights[i];
                f.Age = Time.time - f.Born;
                if (f.Target.Go == null || !f.Target.Go.activeInHierarchy || f.Age > 12f
                    || (f.Heli != 0 && PlayerHeli.MissileTarget(f.Heli) == null))
                { _flights.RemoveAt(i); Finish(f, false, false); continue; }
                bool ended = false;
                float remaining = Mathf.Min(Time.deltaTime, 0.25f);
                // Short substeps give the same turn rate at ordinary frame rates.
                while (remaining > 0f)
                {
                    float dt = Mathf.Min(remaining, 0.025f); remaining -= dt;
                    Vector3 toward = f.Target.Point - f.Position;
                    f.Direction = Vector3.RotateTowards(f.Direction, toward.normalized, 1.8f * dt, 0f).normalized;
                    float step = 160f * dt;
                    Vector3 previous = f.Position;
                    float along = Mathf.Clamp(Vector3.Dot(toward, f.Direction), 0f, step);
                    bool nearTarget = (toward - f.Direction * along).sqrMagnitude < 1.44f;
                    RaycastHit hit;
                    // Test only up to the target when it lies within this segment:
                    // a wall BEHIND a drone must not swallow the drone impact.
                    if (Cast(previous, f.Direction, nearTarget ? along : step, out hit))
                    {
                        f.Position = hit.point;
                        bool targetHit = hit.transform == f.Target.Go.transform || hit.transform.IsChildOf(f.Target.Go.transform);
                        _flights.RemoveAt(i); Finish(f, targetHit, true); ended = true; break;
                    }
                    if (nearTarget)
                    {
                        f.Position = previous + f.Direction * along;
                        _flights.RemoveAt(i); Finish(f, true, true); ended = true; break;
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
            // Remove from the live list BEFORE any external damage/effect call.
            // A failed effect can never turn into another detonation next frame.
            try
            {
                Send(f, targetHit && f.Heli != 0 ? 3 : 2);
                if (targetHit && f.Target.Go != null)
                {
                    if (f.Heli != 0 && RevivalTroopInsertion.MasterClient()) ApproveHeli(f.Heli, f.Position);
                    else if (f.Target.Kind == 2) Drone.Net.Send(Drone.Net.Treffer,f.Target.Point,new Vector3(f.Target.Actor,0,0),100f,true);
                    else if (f.Target.Kind == 3) SurvNet.Send(SurvNet.Treffer,f.Target.Point,new Vector3(f.Target.Actor,0,0),100f,true);
                    else if (f.Target.Kind == 4) CrewDrone.Beschuss(f.Target.Point-f.Direction*4f,f.Direction,8f,100f);
                    else if (f.Target.Kind == 5) ArtyBattery.Shoot(f.Target.Point-f.Direction*4f,f.Direction);
                }
                if (impact) RocketHook.Detonate(f.Position,900f,12f,3f);
            }
            catch (Exception ex) { Warn(ex); }
            finally { if (f.Model != null) UnityEngine.Object.Destroy(f.Model); }
        }

        static void ApproveHeli(int view, Vector3 point)
        {
            if (!RevivalTroopInsertion.MasterClient()) return;
            GameObject go = PlayerHeli.MissileTarget(view);
            if (go == null) return;
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
