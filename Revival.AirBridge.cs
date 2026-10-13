// Air bridge AB2. Boarding is event-driven; the flight frame tests one box.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class AirBridge
    {
        static ConfigEntry<bool> _enabled;
        static readonly GameObject[] _vehicles = new GameObject[64];
        static readonly int[] _actors = new int[64], _seats = new int[64];
        static readonly float[] _until = new float[64];
        static string _sid, _jumped = "", _seated = "", _scene;
        static AirRoute _offer;
        static bool _fuelOk;
        static float _ground, _nextFloor, _noteUntil, _requestUntil;
        static GameObject _floorGo;
        static GameObject _requestGo;
        static GameObject _seatingGo;
        static bool _seating;
        static float _fuel;
        static bool _haveFloor;
        static string _note = "";
        static readonly string FuelHint = Loc.T("Недостаточно топлива для перелета и резерва.", "Not enough fuel for the flight and reserve.");
        static readonly string TransferHint = Loc.T("Подготовка перелета...", "Preparing the flight...");
        static readonly string PumpHint = Loc.T("F - заправить Ми-8 из топливного склада", "F - refuel the Mi-8 from the fuel depot");
        static readonly string SwampPadHint = Loc.T("Площадка Ми-8 - грузовой рейс на аэродром", "Mi-8 platform - cargo route to the airfield");
        static readonly string[] RouteHints = new string[AirTravel.Routes.Length];

        internal static bool On { get { return _enabled != null && _enabled.Value && AirRegistry.Owns("an2") && AirRegistry.CanTravel; } }
        internal static string LocalSid
        {
            get { if (string.IsNullOrEmpty(_sid)) _sid = Admin.LocalSteamId(); return _sid ?? ""; }
        }
        internal static void BindConfig(ConfigFile cfg)
        {
            _enabled = cfg.Bind("AirBridge", "Enabled", true,
                "Fly the registry aircraft between MAIN/Primorye/Toxic Swamp. Route fuel and reserves are checked by the master server.");
            for (int i = 0; i < RouteHints.Length; i++)
                RouteHints[i] = Loc.T("F - перелет: ", "F - fly to ") + AirTravel.Routes[i].Label;
        }
        internal static void Board(GameObject go, int actor, float[] data)
        {
            if (go == null || data == null || data.Length < 3) return;
            int free = -1;
            for (int i = 0; i < _vehicles.Length; i++)
            {
                if (_actors[i] == actor && _vehicles[i] == go)
                {
                    if (data[1] <= .5f) { _vehicles[i] = null; _until[i] = 0; return; }
                    _seats[i] = data[2] > .5f ? -1 : data.Length >= 4 ? (int)data[3] : 0;
                    _until[i] = Time.time + 6f; return;
                }
                if (free < 0 && (_vehicles[i] == null || _until[i] < Time.time)) free = i;
            }
            if (data[1] <= .5f || free < 0) return;
            _vehicles[free] = go; _actors[free] = actor;
            _seats[free] = data[2] > .5f ? -1 : data.Length >= 4 ? (int)data[3] : 0;
            _until[free] = Time.time + 6f;
        }
        internal static bool Pilot(GameObject go, int actor)
        {
            for (int i = 0; i < _vehicles.Length; i++)
                if (_vehicles[i] == go && _actors[i] == actor && _seats[i] == -1 && _until[i] >= Time.time) return true;
            return false;
        }
        internal static int FreeSeat(GameObject go, int count)
        {
            for (int seat = 0; seat < count; seat++)
            {
                bool used = false;
                for (int i = 0; i < _vehicles.Length; i++)
                    if (_vehicles[i] == go && _seats[i] == seat && _until[i] >= Time.time) { used = true; break; }
                if (!used) return seat;
            }
            return -2;
        }
        internal static string Manifest(GameObject go, int pilot)
        {
            StringBuilder b = new StringBuilder(); int n = 0; bool havePilot = false;
            for (int i = 0; i < _vehicles.Length; i++)
            {
                if (_vehicles[i] != go || _until[i] < Time.time) continue;
                string sid = _actors[i] == Crocodile.LocalActor() ? LocalSid : Mercs.SteamOf(Crocodile.PlayerByActor(_actors[i]));
                if (string.IsNullOrEmpty(sid) || ++n > AirTravel.MaxPeople) return null;
                if (_seats[i] == -1)
                { if (_actors[i] != pilot || havePilot) return null; havePilot = true; }
                if (b.Length > 0) b.Append(',');
                b.Append(sid).Append(':').Append(_seats[i].ToString(CultureInfo.InvariantCulture));
            }
            return havePilot ? b.ToString() : null;
        }
        internal static bool Floor(Vector3 at, out float y)
        {
            if (AirLanding.Deck(at, out y)) return true;
            float water;
            bool ground = RevivalTroopInsertion.TerrainHeight(at, out y) || AirBoundary.Height(at.x, at.z, out y);
            if (AirLanding.Water(at, out water) && (!ground || water > y)) { y = water; return true; }
            return ground;
        }
        internal static void Frame()
        {
            if (!On) { _offer = null; return; }
            GameObject go = PlayerAn2.Flying ? PlayerAn2.Plane : PlayerHeli.Flying ? PlayerHeli.Machine : null;
            if (go == null) { _offer = null; return; }
            if (_scene != MapScene.Current)
            { _scene = MapScene.Current; _nextFloor = 0; _floorGo = null; _requestUntil = 0; }
            Vector3 at = go.transform.position;
            if (_floorGo != go || Time.realtimeSinceStartup >= _nextFloor)
            {
                _floorGo = go; _nextFloor = Time.realtimeSinceStartup + .5f;
                _haveFloor = Floor(at, out _ground);
                if (!_haveFloor) { _offer = null; return; }
                if (PlayerAn2.Flying) _fuel = PlayerAn2.FuelOf(go) / PlayerAn2.Capacity;
                else { int parts; HeliCondition.Read(go, out parts, out _fuel); _fuel /= HeliCondition.Capacity; }
            }
            if (!_haveFloor) { _offer = null; return; }
            bool an2 = PlayerAn2.Flying;
            _offer = AirTravel.Gate(an2 ? "an2" : "mi8", AirPlan.RegionOf(_scene), at.x, at.z,
                go.transform.eulerAngles.y, (at.y - _ground) / 2.8f);
            _fuelOk = _offer != null && _fuel + .0001f >= _offer.Required;
            if (GameUi.WindowOpen || !Input.GetKeyDown(KeyCode.F) || Time.realtimeSinceStartup < _requestUntil) return;
            if (_offer != null)
            {
                if (!_fuelOk) { Note(FuelHint); return; }
                _requestGo = go; _requestUntil = Time.realtimeSinceStartup + 40f; Note(TransferHint);
                float speed = an2 ? PlayerAn2.Velocity.magnitude : PlayerHeli.TravelVelocity.magnitude;
                if (RevivalTroopInsertion.MasterClient()) AirRegistry.RequestDeparture(go, Crocodile.LocalActor(), speed);
                else if (an2) PlayerAn2.Net.Send(PlayerAn2.Net.SpawnRequest, new float[] { PlayerAn2.View(go), 1, speed }, true);
                else PlayerHeli.Net.Send(PlayerHeli.Net.SpawnRequest, new float[] { PlayerHeli.ViewOf(go), 1, speed }, true);
            }
            else if (!an2 && CanPump(go))
            {
                if (RevivalTroopInsertion.MasterClient()) Pump(go, Crocodile.LocalActor());
                else PlayerHeli.Net.Send(PlayerHeli.Net.SpawnRequest, new float[] { PlayerHeli.ViewOf(go), 2, 0 }, true);
                _requestUntil = Time.realtimeSinceStartup + 6f;
            }
        }
        internal static bool CorridorFlight { get { return On && _offer != null && _fuelOk; } }
        internal static bool Departing(GameObject go)
        { return On && go != null && go == _requestGo && Time.realtimeSinceStartup < _requestUntil; }
        internal static bool Seating(GameObject go) { return _seating && go != null && go == _seatingGo; }
        internal static bool UsesBoardKey(GameObject go)
        {
            return On && (Seating(go) || Departing(go) || (_offer != null && (PlayerAn2.Flown(go) || PlayerHeli.Flown(go)))
                || (PlayerHeli.Flown(go) && CanPump(go)));
        }
        internal static void Request(bool an2, float[] data, int actor)
        {
            if (!On || !RevivalTroopInsertion.MasterClient() || data.Length < 3) return;
            GameObject go = an2 ? PlayerAn2.ByViewId((int)data[0]) : PlayerHeli.MachineByView((int)data[0]);
            if (data[1] == 1) AirRegistry.RequestDeparture(go, actor, data[2]);
            else if (!an2 && data[1] == 2 && Pilot(go, actor)) Pump(go, actor);
        }
        static bool CanPump(GameObject go)
        {
            if (!MapScene.AtHome || go == null || PlayerHeli.InAir(go) || !FuelDepot.Active) return false;
            Vector3 p = go.transform.position - An2Repair.Depot;
            return p.x * p.x + p.z * p.z <= 150f * 150f;
        }
        static void Pump(GameObject go, int actor)
        {
            if (!CanPump(go) || !AirfieldHold.ActorMayUse(go, actor) || AirRegistry.CargoLocked(go)) return;
            int mask; float fuel; HeliCondition.Read(go, out mask, out fuel);
            float amount = FuelDepot.Draw(Mathf.Min(150f, HeliCondition.Capacity - fuel));
            if (amount <= 0) return;
            HeliCondition.Restore(go, mask, fuel + amount);
            // Existing condition channel distributes the refuel to the pilot.
            HeliCondition.TravelFill(go);
        }
        internal static void Note(string text) { _note = text; _noteUntil = Time.time + 5f; }
        internal static void Observe(AirBook book, string region, long now)
        {
            if (!On || region == null) return;
            string sid = LocalSid;
            for (int i = 0; i < book.Crafts.Length; i++)
            {
                AirCraft a = book.Crafts[i]; AirRoute r = AirTravel.Find(a.Route);
                if (book.Result == "err:fuel") Note(FuelHint);
                if (a.Transfer == _seated && a.Phase == "recovered")
                {
                    Transform body = PlayerAn2.Aboard ? PlayerAn2.Body : PlayerHeli.Body;
                    PlayerAn2.TravelLeave(); PlayerHeli.TravelLeave();
                    if (body != null)
                        body.position = a.Region == "PRIM" ? new Vector3(956.9f, 51.6f, 1041f)
                            : a.Region == "SWAMP" ? new Vector3(582.9f, 43.5f, 190.2f)
                            : new Vector3(a.X, a.Y + 2.8f, a.Z);
                    _seating = false; _seatingGo = null; _seated = "";
                }
                if (_requestGo != null && a.Id == (PlayerAn2.Contains(_requestGo) ? "an2" : "mi8")
                    && book.Result.StartsWith("err:", StringComparison.Ordinal)) { _requestGo = null; _requestUntil = 0; }
                if (a.Transfer == _seated) _seating = a.State == "in_transit" && a.Phase == "arriving";
                if (r == null || a.Transfer.Length == 0 || a.Transfer == _jumped || r.From != region
                    || (a.State != "in_transit" && a.Phase != "confirmed")) continue;
                for (int j = 0; j < a.PeopleCount; j++) if (a.People[j] == sid && !a.Seated[j])
                {
                    PlayerAn2.TravelLeave(); PlayerHeli.TravelLeave();
                    if (Jump(AirTravel.Scene(r.To))) _jumped = a.Transfer;
                    return;
                }
            }
        }
        internal static bool Seat(GameObject go, AirCraft craft, int person)
        {
            if (!On) return false;
            if (_seated == craft.Transfer)
                return craft.Id == "an2" ? PlayerAn2.Plane == go : PlayerHeli.Machine == go;
            bool ok = craft.Id == "an2" ? PlayerAn2.TravelBoard(go, craft.Seats[person], craft.Speed, craft.Power, craft.Running, craft.Throttle)
                : PlayerHeli.TravelBoard(go, craft.Seats[person], craft.Speed, craft.Power, craft.Running);
            if (ok) _seated = craft.Transfer;
            if (ok) { _seatingGo = go; _seating = craft.State == "in_transit"; }
            return ok;
        }
        static bool Jump(int scene)
        {
            try
            {
                Type triggerType = RevivalPlugin.TypeByName("LocationChangeTrigger");
                Type managerType = RevivalPlugin.TypeByName("GameLocationChangeManager");
                UnityEngine.Object manager = managerType == null ? null : UnityEngine.Object.FindObjectOfType(managerType);
                MethodInfo change = managerType == null ? null : AccessTools.Method(managerType, "ChangeGameLocation", null, null);
                if (scene < 0 || triggerType == null || manager == null || change == null) return false;
                GameObject triggerObject = new GameObject("NDR_AirBridge_RegionChange");
                Component trigger = triggerObject.AddComponent(triggerType);
                AccessTools.Field(triggerType, "Id").SetValue(trigger, 9000 + scene);
                FieldInfo kind = AccessTools.Field(triggerType, "LocationaChangeType");
                kind.SetValue(trigger, kind.FieldType.IsEnum ? Enum.ToObject(kind.FieldType, 2) : (object)2);
                FieldInfo sub = AccessTools.Field(triggerType, "SubLocation");
                sub.SetValue(trigger, sub.FieldType.IsEnum ? Enum.ToObject(sub.FieldType, scene) : (object)scene);
                AccessTools.Field(triggerType, "ShowOnMapUI").SetValue(trigger, false);
                change.Invoke(manager, new object[] { trigger });
                UnityEngine.Object.Destroy(triggerObject, 30f);
                return true;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirBridge region change: " + ex.Message); return false; }
        }
        internal static void Draw()
        {
            if (!On || Event.current == null || Event.current.type != EventType.Repaint) return;
            string hint = Time.time < _noteUntil ? _note : null;
            if (hint == null && _offer != null)
                hint = _fuelOk ? RouteHints[Array.IndexOf(AirTravel.Routes, _offer)] : FuelHint;
            if (hint == null && PlayerHeli.Flying && CanPump(PlayerHeli.Machine)) hint = PumpHint;
            if (hint != null) VanillaUi.InfoLabel(new Rect(Screen.width * .5f - 220, Screen.height * .5f + 95, 440, 25), hint);
            DrawMap();
        }
        static Texture _stamp;
        static Component _swampInkSource;
        static Vector2 _swampInkWorld, _swampInkMap, _swampPadInk;
        static float _swampInkAlpha;
        static void DrawMap()
        {
            MapInkLayer layer = null;
            try
            {
                Component manager, texture; Camera camera; Vector2 world, map;
                if (!MapTools.Context(out manager, out texture, out camera, out world, out map)) return;
                Rect full; if (!MapTools.MapScreenRect(texture, camera, out full)) return;
                if (_stamp == null) _stamp = MapInkLayer.SettlementStamp();
                if (_stamp == null) return;
                string region = AirPlan.RegionOf(MapScene.Current);
                bool keepSwamp = false;
                if (region != "SWAMP") _swampInkSource = null;
                if (region == "SWAMP")
                {
                    layer = MapInkLayer.Begin("air-bridge", texture);
                    if (layer == null) return;
                    keepSwamp = layer.CanKeep && _swampInkSource == texture && _swampInkWorld == world
                        && _swampInkMap == map && Mathf.Abs(layer.Alpha - _swampInkAlpha) < .001f;
                    DrawSwampPad(layer, texture, camera, world, map, full, keepSwamp);
                }
                for (int i = 0; i < AirTravel.Routes.Length; i++)
                {
                    AirRoute r = AirTravel.Routes[i]; if (r.From != region) continue;
                    Vector2 point;
                    if (!MapTools.WorldToGui(new Vector3((r.MinX + r.MaxX) * .5f, 0, (r.MinZ + r.MaxZ) * .5f),
                        texture, camera, world, map, out point)) continue;
                    point = MapProject.OnPicture(point, full);
                    if (layer == null) layer = MapInkLayer.Begin("air-bridge", texture);
                    if (layer == null) return;
                    float size = Mathf.Max(8, full.width * .014f);
                    if (!keepSwamp)
                        layer.Draw(new Rect((point.x - size - full.x) * 1024 / full.width,
                            (point.y - size - full.y) * 1024 / full.height, size * 2 * 1024 / full.width,
                            size * 2 * 1024 / full.height), _stamp, VanillaUi.Gold);
                    Vector2 cornerA, cornerB;
                    if (!keepSwamp && MapTools.WorldToGui(new Vector3(r.MinX, 0, r.MinZ), texture, camera, world, map, out cornerA)
                        && MapTools.WorldToGui(new Vector3(r.MaxX, 0, r.MaxZ), texture, camera, world, map, out cornerB))
                    {
                        cornerA = MapProject.OnPicture(cornerA, full); cornerB = MapProject.OnPicture(cornerB, full);
                        float x = (Mathf.Min(cornerA.x, cornerB.x) - full.x) * 1024 / full.width;
                        float y = (Mathf.Min(cornerA.y, cornerB.y) - full.y) * 1024 / full.height;
                        float w = Mathf.Abs(cornerB.x - cornerA.x) * 1024 / full.width;
                        float h = Mathf.Abs(cornerB.y - cornerA.y) * 1024 / full.height;
                        layer.Draw(new Rect(x, y, w, 2), Texture2D.whiteTexture, VanillaUi.Gold);
                        layer.Draw(new Rect(x, y + h - 2, w, 2), Texture2D.whiteTexture, VanillaUi.Gold);
                        layer.Draw(new Rect(x, y, 2, h), Texture2D.whiteTexture, VanillaUi.Gold);
                        layer.Draw(new Rect(x + w - 2, y, 2, h), Texture2D.whiteTexture, VanillaUi.Gold);
                    }
                    if (full.Contains(Event.current.mousePosition) && (Event.current.mousePosition - point).sqrMagnitude < size * size)
                        VanillaUi.InfoLabel(new Rect(point.x + 12, point.y, 260, 22), RouteHints[i]);
                }
                if (region == "SWAMP" && layer != null)
                {
                    if (keepSwamp) layer.Keep();
                    _swampInkSource = texture; _swampInkWorld = world; _swampInkMap = map; _swampInkAlpha = layer.Alpha;
                }
            }
            finally { if (layer != null) layer.End(); else MapInkLayer.Hide("air-bridge"); }
        }
        static void DrawSwampPad(MapInkLayer layer, Component texture, Camera camera, Vector2 world, Vector2 map, Rect full, bool keep)
        {
            if (keep)
            {
                SwampPadHover(new Vector2(full.x + _swampPadInk.x * full.width / 1024,
                    full.y + _swampPadInk.y * full.height / 1024), full);
                return;
            }
            Vector2 point;
            if (!MapTools.WorldToGui(new Vector3(AirLandingCore.DeckX, 0, AirLandingCore.DeckZ),
                texture, camera, world, map, out point)) return;
            point = MapProject.OnPicture(point, full);
            float x = (point.x - full.x) * 1024 / full.width;
            float y = (point.y - full.y) * 1024 / full.height;
            _swampPadInk = new Vector2(x, y);
            Color ink = VanillaUi.Gold;
            // Native map ink, with a legible H even at the whole-region zoom.
            layer.Draw(new Rect(x - 8, y - 8, 16, 2), Texture2D.whiteTexture, ink);
            layer.Draw(new Rect(x - 8, y + 6, 16, 2), Texture2D.whiteTexture, ink);
            layer.Draw(new Rect(x - 8, y - 6, 2, 12), Texture2D.whiteTexture, ink);
            layer.Draw(new Rect(x + 6, y - 6, 2, 12), Texture2D.whiteTexture, ink);
            layer.Draw(new Rect(x - 4, y - 4, 2, 8), Texture2D.whiteTexture, ink);
            layer.Draw(new Rect(x + 2, y - 4, 2, 8), Texture2D.whiteTexture, ink);
            layer.Draw(new Rect(x - 2, y - 1, 4, 2), Texture2D.whiteTexture, ink);
            AirRoute arrival = AirTravel.Find("mi8-main-swamp");
            for (int i = 1; i <= 12; i++)
            {
                Vector2 guide;
                if (!MapTools.WorldToGui(new Vector3(Mathf.Lerp(AirLandingCore.DeckX, arrival.X, i / 12f), 0,
                    AirLandingCore.DeckZ), texture, camera, world, map, out guide)) continue;
                guide = MapProject.OnPicture(guide, full);
                layer.Draw(new Rect((guide.x - full.x) * 1024 / full.width - 1,
                    (guide.y - full.y) * 1024 / full.height - 1, 2, 2), Texture2D.whiteTexture, ink);
            }
            SwampPadHover(point, full);
        }
        static void SwampPadHover(Vector2 point, Rect full)
        {
            if (full.Contains(Event.current.mousePosition) && (Event.current.mousePosition - point).sqrMagnitude < 144)
                VanillaUi.InfoLabel(new Rect(point.x + 12, point.y, 320, 22), SwampPadHint);
        }
    }

    // Deterministic local scenery and water index; aircraft remain Photon scene objects.
    internal static class AirLanding
    {
        struct Face
        {
            internal AirWaterFace Geometry;
            internal Renderer Drawn;
            internal GameObject Active;
            internal Bounds Footprint;
        }
        static readonly Dictionary<long, Face[]> _cells = new Dictionary<long, Face[]>();
        static readonly HashSet<GameObject> _waterColliders = new HashSet<GameObject>();
        static string _scene;
        static Terrain _terrain;
        static Terrain[] _grounds = new Terrain[0];
        static Vector3[] _origins = new Vector3[0], _sizes = new Vector3[0];
        static GameObject _platform, _deck, _ramp;
        static Material _deckMaterial, _markMaterial;
        static float _next, _waitUntil;
        static bool _indexed, _waiting, _platformComplete;
        static int _sceneCount = -1;
        static bool _eastPlaced;
        const float Cell = 128f;
        static long Key(int x, int z) { return ((long)x << 32) | (uint)z; }
        static int CellAt(float x) { return (int)Math.Floor(x / Cell); }
        internal static bool IsWater(GameObject go) { return go != null && _waterColliders.Contains(go); }
        internal static bool GroundReady { get { return _grounds.Length > 0 && _terrain != null; } }
        internal static bool PlatformReady { get { return _platformComplete && _platform != null && AirPlan.RegionOf(MapScene.Current) == "SWAMP"; } }
        internal static bool LandingSurface(GameObject go) { return go != null && (go == _deck || go == _ramp || An2WaterSites.Surface(go)); }
        internal static bool Ground(Vector3 at, out float y)
        {
            y = 0;
            for (int i = 0; i < _grounds.Length; i++)
            {
                Terrain t = _grounds[i]; Vector3 o = _origins[i], s = _sizes[i];
                if (t == null || at.x < o.x || at.x > o.x + s.x || at.z < o.z || at.z > o.z + s.z) continue;
                y = o.y + t.SampleHeight(at); return true;
            }
            return false;
        }
        internal static bool Deck(Vector3 at, out float y)
        {
            y = AirLandingCore.DeckY;
            return PlatformReady && AirLandingCore.Deck(at.x, at.z);
        }
        internal static bool Water(Vector3 at, out float y)
        {
            return Water(at, out y, 1.4f);
        }
        internal static bool Water(Vector3 at, out float y, float minimumDepth)
        {
            y = 0;
            Face[] faces;
            if (!_cells.TryGetValue(Key(CellAt(at.x), CellAt(at.z)), out faces)) return false;
            bool found = false;
            for (int i = 0; i < faces.Length; i++)
            {
                Face f = faces[i]; float h;
                if (f.Drawn == null || f.Active == null || !f.Drawn.enabled || !f.Active.activeInHierarchy
                    || at.x < f.Footprint.min.x || at.x > f.Footprint.max.x
                    || at.z < f.Footprint.min.z || at.z > f.Footprint.max.z
                    || !f.Geometry.Height(at.x, at.z, out h)) continue;
                if (!found || h > y) { y = h; found = true; }
            }
            float ground;
            return found && Ground(at, out ground) && AirLandingCore.Wet(y, ground, minimumDepth);
        }
        internal static bool An2Floor(Vector3 at, out float y)
        {
            if (Water(at, out y)) { y -= AirLandingCore.WaterlineMetres * PlayerAn2.K; return true; }
            return false;
        }
        internal static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 2f;
            string scene = MapScene.Current;
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            bool topology = _sceneCount != sceneCount || _eastPlaced != EastWorld.ContentReady;
            _sceneCount = sceneCount; _eastPlaced = EastWorld.ContentReady;
            if (_scene != scene || (_indexed && _terrain == null) || topology)
            {
                _scene = scene; _cells.Clear(); _waterColliders.Clear(); _indexed = false; _waiting = false;
                _grounds = new Terrain[0]; _origins = _sizes = new Vector3[0];
                if (_platform != null) UnityEngine.Object.Destroy(_platform);
                _platform = _deck = _ramp = null;
                _platformComplete = false;
                An2WaterSites.Reset();
            }
            if (AirPlan.RegionOf(scene) == null) return;
            if (_indexed)
            {
                CacheGround(Terrain.activeTerrains);
                if (AirPlan.RegionOf(scene) == "SWAMP" && !PlatformReady) BuildPlatform(_grounds);
                return;
            }
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains.Length == 0) return;
            CacheGround(terrains);
            // The deck must exist before registry materialisation, even if water
            // sources are still loading. BuildPlatform waits for the site's terrain.
            if (AirPlan.RegionOf(scene) == "SWAMP") BuildPlatform(terrains);
            if (!_waiting) { _waiting = true; _waitUntil = Time.realtimeSinceStartup + 30f; }
            Type waterType = RevivalPlugin.TypeByName("WaterObject");
            UnityEngine.Object[] sources = waterType == null ? new UnityEngine.Object[0] : UnityEngine.Object.FindObjectsOfType(waterType);
            if (sources.Length == 0 && Time.realtimeSinceStartup < _waitUntil) return;
            BuildWater(sources);
            _indexed = true;
            An2WaterSites.Build();
        }
        static void CacheGround(Terrain[] terrains)
        {
            bool same = terrains.Length == _grounds.Length;
            if (same) for (int i = 0; i < terrains.Length; i++) if (terrains[i] != _grounds[i]) { same = false; break; }
            if (same) return;
            _terrain = terrains.Length == 0 ? null : terrains[0];
            _grounds = terrains; _origins = new Vector3[terrains.Length]; _sizes = new Vector3[terrains.Length];
            for (int i = 0; i < terrains.Length; i++)
            {
                _origins[i] = terrains[i].transform.position;
                TerrainData data = terrains[i].terrainData;
                _sizes[i] = data == null ? Vector3.zero : data.size;
            }
        }
        static void Add(Dictionary<long, List<Face>> bins, Renderer r, Vector3 a, Vector3 b, Vector3 c)
        {
            Bounds bounds = r.bounds;
            float minX = Mathf.Max(bounds.min.x, Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
            float maxX = Mathf.Min(bounds.max.x, Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
            float minZ = Mathf.Max(bounds.min.z, Mathf.Min(a.z, Mathf.Min(b.z, c.z)));
            float maxZ = Mathf.Min(bounds.max.z, Mathf.Max(a.z, Mathf.Max(b.z, c.z)));
            if (minX > maxX || minZ > maxZ) return;
            Face f = new Face(); f.Drawn = r; f.Active = r.gameObject; f.Footprint = bounds;
            f.Geometry = new AirWaterFace(a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z);
            for (int x = CellAt(minX); x <= CellAt(maxX); x++)
                for (int z = CellAt(minZ); z <= CellAt(maxZ); z++)
                {
                    long key = Key(x, z); List<Face> list;
                    if (!bins.TryGetValue(key, out list)) { list = new List<Face>(); bins.Add(key, list); }
                    list.Add(f);
                }
        }
        static void BuildWater(UnityEngine.Object[] sources)
        {
            // Discover only when scene topology changes. Includes native sea
            // renderers and relief water even before EastWater attaches its script.
            List<UnityEngine.Object> water = new List<UnityEngine.Object>();
            HashSet<Renderer> added = new HashSet<Renderer>();
            for (int i = 0; i < sources.Length; i++)
            {
                Component c = sources[i] as Component;
                if (c == null) continue;
                Collider[] native = c.GetComponentsInChildren<Collider>(true);
                for (int k = 0; k < native.Length; k++) _waterColliders.Add(native[k].gameObject);
                Renderer[] children = c.GetComponentsInChildren<Renderer>(false);
                for (int k = 0; k < children.Length; k++)
                    if (added.Add(children[k])) water.Add(children[k]);
            }
            Renderer[] drawn = UnityEngine.Object.FindObjectsOfType<Renderer>();
            for (int i = 0; i < drawn.Length; i++)
            {
                Renderer r = drawn[i]; GameObject go = r.gameObject;
                if (go.name.StartsWith(EastWater.Prefix) || (go.layer == 4 && go.CompareTag("Water")))
                    if (added.Add(r)) water.Add(r);
            }
            sources = water.ToArray();
            Dictionary<long, List<Face>> bins = new Dictionary<long, List<Face>>();
            int planes = 0;
            for (int n = 0; n < sources.Length; n++)
            {
                Component source = sources[n] as Component;
                if (source == null || !source.gameObject.activeInHierarchy) continue;
                Collider[] colliders = source.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++) _waterColliders.Add(colliders[i].gameObject);
                Renderer r = source.GetComponent<Renderer>(); MeshFilter filter = source.GetComponent<MeshFilter>();
                if (r == null || !r.enabled || filter == null || filter.sharedMesh == null) continue;
                Mesh mesh = filter.sharedMesh;
                bool exact = false;
                try
                {
                    // A static batch stores world-space vertices and a renderer submesh range.
                    PropertyInfo firstProperty = AccessTools.Property(typeof(Renderer), "subMeshStartIndex");
                    bool batch = r.isPartOfStaticBatch;
                    if (mesh.isReadable && (!batch || firstProperty != null))
                    {
                        int first = batch ? (int)firstProperty.GetValue(r, null) : 0;
                        int count = batch ? r.sharedMaterials.Length : mesh.subMeshCount;
                        Vector3[] vertices = mesh.vertices;
                        for (int sub = first; sub < first + count && sub < mesh.subMeshCount; sub++)
                        {
                            int[] triangles = mesh.GetTriangles(sub);
                            for (int k = 0; k + 2 < triangles.Length; k += 3)
                            {
                                Vector3 a = vertices[triangles[k]], b = vertices[triangles[k + 1]], c = vertices[triangles[k + 2]];
                                if (!batch) { a = filter.transform.TransformPoint(a); b = filter.transform.TransformPoint(b); c = filter.transform.TransformPoint(c); }
                                Add(bins, r, a, b, c);
                            }
                        }
                        exact = count > 0 && first < mesh.subMeshCount;
                    }
                }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("AirBridge water mesh: " + ex.Message); }
                if (!exact)
                {
                    // Optimized native meshes may be unreadable. Flat WaterObjects retain
                    // their renderer footprint; terrain depth rejects dry shore/ground.
                    Bounds b = r.bounds;
                    if (b.size.y > .5f) continue;
                    Vector3 a = new Vector3(b.min.x, b.center.y, b.min.z), d = new Vector3(b.max.x, b.center.y, b.max.z);
                    Vector3 c = new Vector3(b.min.x, b.center.y, b.max.z), e = new Vector3(b.max.x, b.center.y, b.min.z);
                    Add(bins, r, a, c, d); Add(bins, r, a, d, e); planes++;
                }
            }
            _cells.Clear();
            foreach (KeyValuePair<long, List<Face>> pair in bins) _cells.Add(pair.Key, pair.Value.ToArray());
            RevivalPlugin.L.LogInfo("AirBridge water: " + sources.Length + " active sources, " + _cells.Count + " cells, " + planes + " flat bounds fallbacks.");
        }
        static GameObject Box(Transform parent, string name, Vector3 centre, Vector3 size, Material material, bool solid)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            go.transform.SetParent(parent, false); go.transform.position = centre; go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = material;
            if (!solid) { Collider collider = go.GetComponent<Collider>(); collider.enabled = false; UnityEngine.Object.Destroy(collider); }
            return go;
        }
        static void BuildPlatform(Terrain[] terrains)
        {
            if (_platform != null)
            {
                if (_platformComplete) return;
                // A failed scene build must not leave a partial pad marked ready.
                UnityEngine.Object.Destroy(_platform); _platform = _deck = _ramp = null;
            }
            _platformComplete = false;
            float siteGround;
            if (!Ground(new Vector3(AirLandingCore.DeckX, 0, AirLandingCore.DeckZ), out siteGround)) return;
            Shader shader = Shader.Find("Standard"); if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (_deckMaterial == null) _deckMaterial = AirSwampPlatform.Concrete;
            if (_markMaterial == null) { _markMaterial = new Material(shader); _markMaterial.color = new Color(.74f, .70f, .53f); }
            _platform = new GameObject("NDR_SwampAirBridgePlatform");
            Transform root = _platform.transform;
            root.position = new Vector3(AirLandingCore.DeckX, AirLandingCore.DeckY, AirLandingCore.DeckZ);
            _deck = Box(root, "Deck", new Vector3(AirLandingCore.DeckX, AirLandingCore.DeckY - .5f, AirLandingCore.DeckZ), new Vector3(70, 1, 70), _deckMaterial, true);
            for (int x = -1; x <= 1; x += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 p = new Vector3(AirLandingCore.DeckX + 30 * x, 0, AirLandingCore.DeckZ + 30 * z);
                    float ground; if (!RevivalTroopInsertion.TerrainHeight(p, out ground)) ground = 33.33f;
                    float height = Mathf.Max(1, AirLandingCore.DeckY - ground);
                    p.y = ground + height / 2;
                    GameObject leg = Box(root, "Stilt", p, new Vector3(2, height, 2), AirSwampPlatform.Wood, true);
                    UnityEngine.AI.NavMeshObstacle obstacle = leg.AddComponent<UnityEngine.AI.NavMeshObstacle>();
                    obstacle.shape = UnityEngine.AI.NavMeshObstacleShape.Box; obstacle.size = Vector3.one;
                    obstacle.carving = true; obstacle.carveOnlyStationary = true;
                }
            Vector3 start = new Vector3(AirLandingCore.DeckX, 0, AirLandingCore.DeckZ - 63);
            float y; if (!RevivalTroopInsertion.TerrainHeight(start, out y)) y = 35;
            Vector3 end = new Vector3(start.x, AirLandingCore.DeckY, AirLandingCore.DeckZ - 35);
            start.y = y; Vector3 delta = end - start;
            GameObject ramp = Box(root, "AccessRamp", (start + end) / 2 - Vector3.up * .25f,
                new Vector3(8, .5f, delta.magnitude), AirSwampPlatform.Wood, true);
            _ramp = ramp;
            ramp.transform.rotation = Quaternion.LookRotation(delta);
            float markY = AirLandingCore.DeckY + .012f;
            Box(root, "HLeft", new Vector3(AirLandingCore.DeckX - 6, markY, AirLandingCore.DeckZ), new Vector3(1, .02f, 18), _markMaterial, false);
            Box(root, "HRight", new Vector3(AirLandingCore.DeckX + 6, markY, AirLandingCore.DeckZ), new Vector3(1, .02f, 18), _markMaterial, false);
            Box(root, "HCross", new Vector3(AirLandingCore.DeckX, markY, AirLandingCore.DeckZ), new Vector3(12, .02f, 1), _markMaterial, false);
            AirSwampPlatform.Build(root);
            int removed = 0;
            for (int n = 0; n < terrains.Length; n++)
            {
                Terrain terrain = terrains[n]; TerrainData data = terrain.terrainData;
                if (data == null) continue;
                TreeInstance[] trees = data.treeInstances; List<TreeInstance> keep = new List<TreeInstance>(trees.Length);
                Vector3 origin = terrain.transform.position, size = data.size;
                for (int i = 0; i < trees.Length; i++)
                {
                    Vector3 p = origin + Vector3.Scale(trees[i].position, size);
                    if (AirLandingCore.ClearTrees(p.x, p.z)) removed++; else keep.Add(trees[i]);
                }
                if (keep.Count != trees.Length) data.treeInstances = keep.ToArray();
            }
            _platformComplete = true;
            RevivalPlugin.L.LogInfo("AirBridge SWAMP platform: deck y=38, 70x70, trees removed=" + removed + ", windsock/3 barrels/lamp ready.");
        }
    }
}
