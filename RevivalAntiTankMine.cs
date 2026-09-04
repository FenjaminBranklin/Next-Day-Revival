// Next Day: Survival - Revival Toolkit
//
// Equippable one-hit anti-tank mine - the whole feature in one file.
//
// WHAT IT IS. A grenade-category inventory item (id 2065, cloned from the frag
// grenade 1403 so the game files it in the grenade slot and equips a
// PlayerGrenadeWeaponController). LEFT click never throws or consumes it -
// PlayerGrenadeWeaponController::CantThrowGrenade is postfixed to return "can't"
// for this item (IL-confirmed: CantThrowGrenade is called only from
// ThrowGrenade, i.e. the left-click throw, so blocking it there stops the throw
// and the consume before either happens). RIGHT click (hold) runs a 20 s
// placement action with a progress bar and a movement/input lock, exactly the
// proven mechanism the FPV drone / antenna / convoy-repair use (their own
// postfix on the PlayerCant* set, gated on a state flag). On completion exactly
// one mine is consumed and a mine object is placed on the ground in front of the
// player, aligned to the surface.
//
// THE TRIGGER (MineObject). The placed mine watches the shared vehicle scan
// (VehicleScan.All() returns only VehicleGameSystem roots, so characters on foot
// - the placer included - are never in it and can never set it off). When any
// actual vehicle's collider (wheel, chassis or child) comes within the trigger
// radius it fires EXACTLY ONCE: it resolves the collider to the one authoritative
// vehicle root, destroys that vehicle in a single hit regardless of health,
// armour or modules by calling VehicleGameSystem::ApplyDamage with a large value
// that is NOT one of the recognised mod-weapon damages (so VehicleArmor's
// tank-only explosion re-balance leaves it untouched and even a tank dies), and
// spawns a networked explosion through the game's own ExplosionObject
// (RocketHook.Detonate) so every client sees one blast, hears one sound, and
// sees one wreck. The guaranteed kill is the TARGETED ApplyDamage, not the blast
// - the explosion is a normal, mine-sized boom, not an indiscriminate nuke that
// would kill the placer. The mine removes itself exactly once after it fires.
//
// CLEANUP DISCIPLINE. The placement lock is cleared on every exit path: success,
// cancel, released input, item switch, damage/death/downed, disconnect, vehicle
// entry, invalid ground and any exception - the same audit the antenna
// movement-lock fix needed, so this must not reintroduce that stuck-body bug.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII-only comments/logs; player-facing strings via Loc.T (real Cyrillic), so
// this file is UTF-8 (no BOM) and build.ps1 compiles it with /codepage:65001.
//
// SEAMS OUTSIDE THIS FILE (all one-liners, marked "NDR anti-tank mine"):
//   RevivalPlugin.cs BuildItemTable -> AntiTankMine.AddItems(Items)
//   RevivalPlugin.cs BindConfig / Install / Update(Tick) / OnGUI(Draw)

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// Config, the item, the placement state machine, the on-screen progress
    /// bar, and the reflection to read the equipped grenade. The placed mine
    /// itself is <see cref="MineObject"/>; the two hooks are below.
    /// </summary>
    public static class AntiTankMine
    {
        // Fresh id. The plugin's own items occupy 1160-1164, 2050-2064; 2065 is
        // free (verified by verify.py). Donor 1403 is the frag grenade, so the
        // clone inherits the grenade inventory category and equips through the
        // grenade path.
        public const int DEF_MINE = 2065;
        const int DEF_DONOR = 1403;

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<int> CfgMineId;
        public static ConfigEntry<float> CfgPlaceSeconds;
        public static ConfigEntry<float> CfgFrontOffset;
        public static ConfigEntry<float> CfgScale;
        public static ConfigEntry<float> CfgTriggerRadius;
        public static ConfigEntry<float> CfgExplosionRadius;
        public static ConfigEntry<float> CfgExplosionDamage;
        public static ConfigEntry<float> CfgKillDamage;
        public static ConfigEntry<bool> CfgConsume;
        public static ConfigEntry<string> CfgKey;

        static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }
        public static int MineId { get { return CfgMineId != null ? CfgMineId.Value : DEF_MINE; } }
        static float PlaceLen { get { return CfgPlaceSeconds == null ? 20f : Mathf.Max(0.5f, CfgPlaceSeconds.Value); } }

        // ------------------------------------------------------------- state
        static bool _placing;
        static float _placeStart;
        public static bool Placing { get { return _placing; } }

        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;

        // Cached local grenade controller (persists; re-found on a throttle).
        static Component _gren;
        static float _grenUntil;
        static FieldInfo _fGrenData;
        static Material _mineMat;
        static Texture2D _px;

        // ------------------------------------------------------------- item
        public static void AddItems(List<ItemDef> items)
        {
            items.Add(new ItemDef(
                DEF_MINE, DEF_DONOR, false,
                "Противотанковая мина ТМ-62", "Anti-tank mine TM-62",
                "Нажимная противотанковая мина. Левая кнопка её НЕ бросает. "
                + "Правую кнопку держать 20 секунд - мина встаёт на землю перед "
                + "вами, и вы в это время стоите неподвижно. Пехота, включая вас "
                + "самого, её не задевает; любая техника - от машины до танка и "
                + "нового Урала - подрывается с одного раза, сколько бы брони на "
                + "ней ни было.",
                "Pressure-fuzed anti-tank mine. Left click does NOT throw it. "
                + "Hold right click for 20 seconds to place it on the ground in "
                + "front of you - you stand still while it arms. Infantry, "
                + "including you, never set it off; any vehicle - a car, an APC, "
                + "a tank, the new Ural - is destroyed in a single hit no matter "
                + "how much armour it carries.",
                "mine.ndmesh", "mine_diffuse.png", "mine_normal.png",
                "mine_icon.png", null,
                1, 0, 9.0f));
        }

        // ------------------------------------------------------------- config
        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("AntiTankMine", "Enabled", true,
                "Die Panzerabwehrmine (Item 2065) aktivieren.");
            CfgMineId = cfg.Bind("AntiTankMine", "MineId", DEF_MINE,
                "Item-Id der Mine. Nur aendern, wenn 2065 mit etwas anderem "
                + "kollidiert.");
            CfgPlaceSeconds = cfg.Bind("AntiTankMine", "PlaceSeconds", 20f,
                "Sekunden, die die rechte Maustaste fuer eine Platzierung "
                + "gehalten werden muss. Loslassen bricht ab.");
            CfgFrontOffset = cfg.Bind("AntiTankMine", "FrontOffset", 2.2f,
                "Abstand vor dem Spieler, in dem die Mine abgelegt wird (m).");
            CfgScale = cfg.Bind("AntiTankMine", "Scale", 0.45f,
                "Skalierung des platzierten Minenobjekts (1 = Modellgroesse).");
            CfgTriggerRadius = cfg.Bind("AntiTankMine", "TriggerRadius", 1.6f,
                "Radius, in dem ein Fahrzeugkollider die Mine ausloest (m).");
            CfgExplosionRadius = cfg.Bind("AntiTankMine", "ExplosionRadius", 5f,
                "Radius der sichtbaren Explosion (m). Bewusst klein: der "
                + "garantierte Abschuss laeuft ueber gezielten Schaden am "
                + "getroffenen Fahrzeug, NICHT ueber einen riesigen Radius.");
            CfgExplosionDamage = cfg.Bind("AntiTankMine", "ExplosionDamage", 900f,
                "Schaden der sichtbaren Explosion (wie die LAW). Zerstoert jedes "
                + "weiche Fahrzeug ohnehin mit einem Schlag.");
            CfgKillDamage = cfg.Bind("AntiTankMine", "KillDamage", 1000000f,
                "Gezielter Schaden direkt am getroffenen Fahrzeug. Bewusst KEIN "
                + "erkannter Waffenwert, damit die Panzer-Explosionsdaempfung "
                + "(VehicleArmor) ihn nicht abschwaecht und auch ein Panzer mit "
                + "einem Treffer stirbt.");
            CfgConsume = cfg.Bind("AntiTankMine", "Consume", true,
                "Genau eine Mine bei erfolgreicher Platzierung verbrauchen.");
            CfgKey = cfg.Bind("AntiTankMine", "Key", "None",
                "Optionale Ersatztaste fuer die Platzierung, falls die rechte "
                + "Maustaste im Spiel nicht durchkommt. Standard None (aus).");
        }

        // ------------------------------------------------------------- install
        public static void Install(Harmony harmony)
        {
            if (!Enabled) { RevivalPlugin.L.LogInfo("Mine: abgeschaltet (AntiTankMine/Enabled)."); return; }
            MineGrenadeHook.Install(harmony);
            MineLockHook.Install(harmony);
            RevivalPlugin.L.LogInfo("Mine: Panzerabwehrmine aktiv (Id " + MineId
                + ", " + PlaceLen.ToString("0") + " s Platzierung).");
        }

        // ------------------------------------------------------------- tick
        public static void Tick()
        {
            try
            {
                if (!Enabled) { if (_placing) Cancel("disabled"); return; }

                if (_placing)
                {
                    string stop = WhyStop();
                    if (stop != null) { Cancel(stop); return; }
                    if (Time.time - _placeStart >= PlaceLen) Finish();
                    return;
                }

                if (!MineEquipped()) return;
                if (StartPressed()) Begin();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Mine-Tick: " + ex);
                Cancel("exception");   // never leave the body locked
            }
        }

        static void Begin()
        {
            // Refuse to start where a placement could not finish cleanly.
            if (InVehicle() || MapTools.LocalPlayer() == null) return;
            _placing = true;
            _placeStart = Time.time;
            RevivalPlugin.L.LogInfo("Mine: Platzierung gestartet.");
            Turret.Hinweis(Loc.T("Установка мины...", "Placing mine..."), 1.5f);
        }

        /// <summary>Reason to abort the running placement, or null to continue.
        /// One place lists every exit path so none is forgotten.</summary>
        static string WhyStop()
        {
            if (!Enabled) return "disabled";
            if (!Held()) return "released";              // released input
            if (!MineEquipped()) return "item switched"; // item switch
            if (MapTools.LocalPlayer() == null) return "no player (death/disconnect)";
            if (InVehicle()) return "entered vehicle";
            return null;
        }

        static void Cancel(string reason)
        {
            if (!_placing) return;
            _placing = false;
            RevivalPlugin.L.LogInfo("Mine: Platzierung abgebrochen (" + reason + ").");
            Turret.Hinweis(Loc.T("Установка прервана", "Placement cancelled"), 1.5f);
        }

        static void Finish()
        {
            _placing = false;
            Vector3 pos, normal;
            if (!GroundInFront(out pos, out normal))
            {
                RevivalPlugin.L.LogWarning("Mine: kein gueltiger Boden vor dem Spieler.");
                Turret.Hinweis(Loc.T("Нет ровной земли", "No valid ground"), 2f);
                return;
            }

            GameObject mine = MineObject.Place(pos, normal);
            if (mine == null)
            {
                Turret.Hinweis(Loc.T("Не удалось поставить мину", "Could not place the mine"), 2f);
                return;
            }

            // Consume exactly one - and only now, on success.
            if (CfgConsume == null || CfgConsume.Value)
                Turret.TakeItem(MineId, "anti-tank mine");

            RevivalPlugin.L.LogInfo("Mine: scharf bei " + pos + ".");
            Turret.Hinweis(Loc.T("Мина установлена", "Mine armed"), 2.5f);
        }

        // ------------------------------------------------------------- input
        static bool StartPressed()
        {
            if (Input.GetMouseButtonDown(1)) return true;
            KeyCode k = Key();
            return k != KeyCode.None && Input.GetKeyDown(k);
        }

        static bool Held()
        {
            if (Input.GetMouseButton(1)) return true;
            KeyCode k = Key();
            return k != KeyCode.None && Input.GetKey(k);
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            string s = CfgKey == null ? "None" : CfgKey.Value;
            try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
            catch { _key = KeyCode.None; }
            return _key;
        }

        // ------------------------------------------------- equipped detection
        static bool MineEquipped()
        {
            Component ctrl = GrenadeController();
            if (ctrl == null) return false;
            int id = EquippedGrenadeId(ctrl);
            return id == MineId;
        }

        static Component GrenadeController()
        {
            if (_gren != null && Time.time < _grenUntil) return _gren;
            _grenUntil = Time.time + 0.4f;
            _gren = null;
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerGrenadeWeaponController");
                if (t == null) return null;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
                Component fallback = null;
                for (int i = 0; i < all.Length; i++)
                {
                    Component c = all[i] as Component;
                    if (c == null) continue;
                    if (fallback == null) fallback = c;
                    if (IsMine(c)) { _gren = c; break; }
                }
                if (_gren == null) _gren = fallback;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mine: Granatencontroller-Suche: " + ex.Message);
            }
            return _gren;
        }

        static int EquippedGrenadeId(Component ctrl)
        {
            try
            {
                if (_fGrenData == null)
                    _fGrenData = AccessTools.Field(ctrl.GetType(), "_weaponGrenadeData");
                if (_fGrenData == null) return -1;
                object data = _fGrenData.GetValue(ctrl);
                if (data == null) return -1;
                FieldInfo fId = AccessTools.Field(data.GetType(), "ItemID");
                if (fId == null) return -1;
                object raw = fId.GetValue(data);
                if (raw == null) return -1;
                if (raw is int) return (int)raw;
                // ObscuredInt or similar: use its implicit int conversion.
                MethodInfo[] ms = raw.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != typeof(int)) continue;
                    ParameterInfo[] ps = ms[i].GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == raw.GetType())
                        return (int)ms[i].Invoke(null, new object[] { raw });
                }
                try { return Convert.ToInt32(raw); } catch { return -1; }
            }
            catch { return -1; }
        }

        static bool IsMine(Component c)
        {
            try
            {
                Type pv = RevivalPlugin.TypeByName("PhotonView");
                if (pv == null) return true;   // single player: treat as ours
                Component view = c.GetComponentInParent(pv);
                if (view == null) return true;
                MethodInfo getter = AccessTools.PropertyGetter(pv, "isMine");
                if (getter == null) return true;
                object r = getter.Invoke(view, null);
                return r is bool && (bool)r;
            }
            catch { return true; }
        }

        // ------------------------------------------------------------ seated
        static float _inVehUntil;
        static bool _inVehResult;

        static bool InVehicle()
        {
            if (Time.time < _inVehUntil) return _inVehResult;
            _inVehUntil = Time.time + 0.4f;
            bool inv = false;
            try
            {
                Component[] all = VehicleScan.All();
                for (int i = 0; i < all.Length; i++)
                {
                    FieldInfo f = AccessTools.Field(all[i].GetType(), "_localPlayerPassengerId");
                    if (f == null) continue;
                    object v = f.GetValue(all[i]);
                    if (v is int && (int)v >= 0) { inv = true; break; }
                }
            }
            catch { }
            _inVehResult = inv;
            return inv;
        }

        // ------------------------------------------------------------ ground
        static bool GroundInFront(out Vector3 pos, out Vector3 normal)
        {
            pos = Vector3.zero;
            normal = Vector3.up;
            GameObject player = MapTools.LocalPlayer();
            Camera cam = Camera.main;
            if (player == null) return false;

            Vector3 fwd = cam != null ? cam.transform.forward : player.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();

            float off = CfgFrontOffset == null ? 2.2f : Mathf.Max(1.0f, CfgFrontOffset.Value);
            Vector3 spot = player.transform.position + fwd * off + Vector3.up * 2.0f;

            Vector3 point, n;
            if (!RaycastNormal(spot, Vector3.down, 8.0f, out point, out n)) return false;
            // Do not place on a near-vertical wall or upside down.
            if (n.y < 0.4f) return false;
            pos = point;
            normal = n;
            return true;
        }

        // ------------------------------------------------------------ material
        internal static Material MineMaterial()
        {
            if (_mineMat != null) return _mineMat;
            try
            {
                Texture2D tex = Assets.Texture("mine_diffuse.png", false, true);
                Shader sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
                Material m = new Material(sh);
                m.name = "NDR_Mine_Material";
                m.mainTexture = tex;
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.2f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.1f);
                _mineMat = m;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Mine-Material: " + ex.Message); }
            return _mineMat;
        }

        // ------------------------------------------------------------- draw
        public static void Draw()
        {
            if (!_placing) return;
            try
            {
                float len = PlaceLen;
                float t = Mathf.Clamp01((Time.time - _placeStart) / len);
                float rest = Mathf.Max(0f, len - (Time.time - _placeStart));
                float w = 300f, h = 22f;
                float x = (Screen.width - w) * 0.5f;
                float y = Screen.height * 0.66f;

                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(x - 2f, y - 2f, w + 4f, h + 4f), Px());
                GUI.color = new Color(0.12f, 0.12f, 0.12f, 0.9f);
                GUI.DrawTexture(new Rect(x, y, w, h), Px());
                GUI.color = new Color(0.85f, 0.55f, 0.20f, 0.95f);
                GUI.DrawTexture(new Rect(x, y, w * t, h), Px());
                GUI.color = Color.white;
                GUI.Label(new Rect(x, y - 22f, w, 20f),
                    Loc.T("Установка мины", "Placing mine") + "  " + Mathf.CeilToInt(rest) + " s");
                GUI.color = old;
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Mine-Bar: " + ex); }
        }

        static Texture2D Px()
        {
            if (_px == null)
            {
                _px = new Texture2D(1, 1);
                _px.SetPixel(0, 0, Color.white);
                _px.Apply();
            }
            return _px;
        }

        // ------------------------------------------------------------ raycast
        /// <summary>Reflective Physics.Raycast returning point and normal - the
        /// plugin does not reference UnityEngine.PhysicsModule directly, so the
        /// method is resolved by name (the same pattern RocketHook uses).</summary>
        internal static bool RaycastNormal(Vector3 origin, Vector3 dir, float range,
                                           out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.up;
            try
            {
                Type physics = RevivalPlugin.TypeByName("UnityEngine.Physics");
                Type hitType = RevivalPlugin.TypeByName("UnityEngine.RaycastHit");
                if (physics == null || hitType == null) return false;
                MethodInfo chosen = null;
                MethodInfo[] ms = physics.GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "Raycast" || ms[i].ReturnType != typeof(bool)) continue;
                    ParameterInfo[] ps = ms[i].GetParameters();
                    if (ps.Length == 4 && ps[0].ParameterType == typeof(Vector3)
                        && ps[1].ParameterType == typeof(Vector3)
                        && ps[2].ParameterType.IsByRef
                        && ps[2].ParameterType.GetElementType() == hitType
                        && ps[3].ParameterType == typeof(float))
                    { chosen = ms[i]; break; }
                }
                if (chosen == null) return false;
                object boxed = Activator.CreateInstance(hitType);
                object[] args = new object[] { origin, dir, boxed, range };
                bool hit = (bool)chosen.Invoke(null, args);
                if (!hit) return false;
                boxed = args[2];
                PropertyInfo pp = hitType.GetProperty("point", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo pn = hitType.GetProperty("normal", BindingFlags.Public | BindingFlags.Instance);
                if (pp == null || pn == null) return false;
                point = (Vector3)pp.GetValue(boxed, null);
                normal = (Vector3)pn.GetValue(boxed, null);
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mine-Raycast: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// A placed, armed mine. Local to the placer (Object.Instantiate, not a
    /// networked object), but its DETONATION is networked through the game's own
    /// ExplosionObject, so a vehicle that rolls over it is destroyed for every
    /// client with one blast and one wreck. Vehicle-only by construction: it only
    /// ever tests the shared VehicleGameSystem scan, so infantry - the placer
    /// above all - can walk over it safely. Fires exactly once.
    /// </summary>
    public sealed class MineObject : MonoBehaviour
    {
        bool _fired;
        float _armed;          // ignore the first moment after placement
        float _nextScan;
        Type _vgsType;

        public static GameObject Place(Vector3 pos, Vector3 normal)
        {
            try
            {
                Mesh mesh = Assets.Load("mine.ndmesh");
                if (mesh == null)
                {
                    RevivalPlugin.L.LogWarning("Mine: mine.ndmesh fehlt - nichts platziert.");
                    return null;
                }
                GameObject go = new GameObject("NDR Anti-tank mine");
                float s = AntiTankMine.CfgScale == null ? 0.45f : Mathf.Max(0.1f, AntiTankMine.CfgScale.Value);
                go.transform.position = pos;
                // Lay the disc flat on the surface: its local +Y (the lid normal)
                // aligns to the ground normal, so it sits without intersecting a
                // slope. A small lift keeps it from z-fighting the ground.
                go.transform.up = normal;
                go.transform.position = pos + normal * 0.02f;
                go.transform.localScale = new Vector3(s, s, s);

                MeshFilter mf = go.AddComponent<MeshFilter>();
                mf.mesh = mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                Material mat = AntiTankMine.MineMaterial();
                if (mat != null) mr.material = mat;

                go.AddComponent<MineObject>();
                return go;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Mine.Place: " + ex);
                return null;
            }
        }

        void Start()
        {
            _armed = Time.time + 1.0f;    // do not fire on the placement frame
            _vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
        }

        void Update()
        {
            if (_fired) return;
            if (Time.time < _armed) return;
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + 0.08f;

            try
            {
                Component vehicle = OverlappingVehicle();
                if (vehicle != null) Fire(vehicle);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Mine.Update: " + ex);
            }
        }

        /// <summary>The first vehicle whose body (any wheel, chassis or child
        /// renderer) reaches within the trigger radius of the mine, resolved to
        /// its one authoritative VehicleGameSystem root. Null when no vehicle is
        /// close. Only VehicleGameSystem roots are ever tested, so a person on
        /// foot is never a candidate. Renderer world bounds are used instead of
        /// colliders because UnityEngine.PhysicsModule (Collider) is not among
        /// the referenced assemblies; the AABB of a wheel/chassis mesh over the
        /// mine is the right footprint test for a mine.</summary>
        Component OverlappingVehicle()
        {
            float r = AntiTankMine.CfgTriggerRadius == null ? 1.6f
                      : Mathf.Max(0.3f, AntiTankMine.CfgTriggerRadius.Value);
            float r2 = r * r;
            Vector3 me = transform.position;

            Component[] all = VehicleScan.All();
            for (int i = 0; i < all.Length; i++)
            {
                Component vgs = all[i];
                if (vgs == null) continue;
                // Cheap reject: skip vehicles whose root is far away before the
                // per-renderer test.
                if ((vgs.transform.position - me).sqrMagnitude > (r + 30f) * (r + 30f))
                    continue;
                Renderer[] rends = vgs.GetComponentsInChildren<Renderer>(true);
                for (int c = 0; c < rends.Length; c++)
                {
                    Renderer rend = rends[c];
                    if (rend == null || !rend.enabled) continue;
                    // Bounds.ClosestPoint returns the point itself when it is
                    // inside the box, so a vehicle sitting over the mine gives 0.
                    Vector3 cp = rend.bounds.ClosestPoint(me);
                    if ((cp - me).sqrMagnitude <= r2)
                        return vgs;
                }
            }
            return null;
        }

        void Fire(Component vgs)
        {
            if (_fired) return;
            _fired = true;

            Vector3 at = vgs.transform.position;
            RevivalPlugin.L.LogInfo("Mine: ausgeloest von " + vgs.gameObject.name + ".");

            // 1) Guaranteed one-hit kill of THIS vehicle, regardless of health,
            //    armour or modules. A large, non-weapon damage value on the
            //    explosion part type: VehicleArmor only re-balances RECOGNISED
            //    weapon damages for tanks, so this passes through unchanged and
            //    even a tank dies. Targeted (this vehicle only), never an AoE.
            KillVehicle(vgs);

            // 2) Networked explosion for the visible blast + sound on every
            //    client, through the game's own ExplosionObject. Mine-sized, not
            //    a player-nuke: the kill above already did the work.
            try
            {
                float dmg = AntiTankMine.CfgExplosionDamage == null ? 900f : AntiTankMine.CfgExplosionDamage.Value;
                float rad = AntiTankMine.CfgExplosionRadius == null ? 5f : AntiTankMine.CfgExplosionRadius.Value;
                RocketHook.Detonate(transform.position + Vector3.up * 0.1f, dmg, rad, 3f);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mine: Explosion konnte nicht ausgeloest werden: " + ex.Message);
            }

            // 3) Remove the mine exactly once.
            UnityEngine.Object.Destroy(gameObject);
        }

        void KillVehicle(Component vgs)
        {
            try
            {
                Type t = _vgsType != null ? _vgsType : RevivalPlugin.TypeByName("VehicleGameSystem");
                if (t == null) return;
                MethodInfo apply = AccessTools.Method(t, "ApplyDamage",
                    new Type[] { typeof(float), typeof(int) }, null);
                if (apply == null)
                {
                    RevivalPlugin.L.LogWarning("Mine: ApplyDamage(float,int) fehlt - "
                        + "gezielter Abschuss uebersprungen, Explosion bleibt.");
                    return;
                }
                float kill = AntiTankMine.CfgKillDamage == null ? 1000000f : AntiTankMine.CfgKillDamage.Value;
                apply.Invoke(vgs, new object[] { kill, 14 });   // 14 = explosion part
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mine: gezielter Abschuss: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Postfix on PlayerGrenadeWeaponController::CantThrowGrenade. Returns "can't"
    /// whenever the equipped grenade is the mine, so a LEFT click never throws or
    /// consumes it. CantThrowGrenade is called only from ThrowGrenade (the
    /// left-click path, IL-confirmed), so this blocks the throw before the item
    /// is spent, and does nothing to any other grenade.
    /// </summary>
    public static class MineGrenadeHook
    {
        static FieldInfo _fData;

        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerGrenadeWeaponController");
                if (t == null) { RevivalPlugin.L.LogWarning("Mine: PlayerGrenadeWeaponController fehlt."); return; }
                MethodInfo m = AccessTools.Method(t, "CantThrowGrenade", null, null);
                if (m == null || m.ReturnType != typeof(bool))
                {
                    RevivalPlugin.L.LogWarning("Mine: CantThrowGrenade fehlt - "
                        + "Linksklick-Sperre inaktiv.");
                    return;
                }
                harmony.Patch(m, null,
                    new HarmonyMethod(typeof(MineGrenadeHook).GetMethod("Postfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("Mine: Linksklick-Wurfsperre aktiv.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Mine: CantThrowGrenade-Patch: " + ex);
            }
        }

        public static void Postfix(object __instance, ref bool __result)
        {
            try
            {
                if (__result) return;   // already can't throw
                if (__instance == null) return;
                if (_fData == null)
                    _fData = AccessTools.Field(__instance.GetType(), "_weaponGrenadeData");
                if (_fData == null) return;
                object data = _fData.GetValue(__instance);
                if (data == null) return;
                FieldInfo fId = AccessTools.Field(data.GetType(), "ItemID");
                if (fId == null) return;
                object raw = fId.GetValue(data);
                int id = ToInt(raw);
                if (id == AntiTankMine.MineId) __result = true;   // the mine never throws
            }
            catch { }
        }

        static int ToInt(object raw)
        {
            if (raw == null) return -1;
            if (raw is int) return (int)raw;
            try
            {
                MethodInfo[] ms = raw.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != typeof(int)) continue;
                    ParameterInfo[] ps = ms[i].GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == raw.GetType())
                        return (int)ms[i].Invoke(null, new object[] { raw });
                }
                return Convert.ToInt32(raw);
            }
            catch { return -1; }
        }
    }

    /// <summary>
    /// The movement/input lock for the 20 s placement. Its own postfix on the
    /// same PlayerCant* set the FPV drone / antenna / convoy-repair patch, gated
    /// on <see cref="AntiTankMine.Placing"/> - so it is self-contained and does
    /// not touch those features. When Placing is false the body is free, which is
    /// why every placement exit path clears the flag.
    /// </summary>
    public static class MineLockHook
    {
        static readonly string[] Sperren = {
            "PlayerMovementController::PlayerCantMovement",
            "PlayerMovementController::PlayerCantRotate",
            "PlayerMovementController::PlayerCantRotateAxisX",
            "PlayerMovementController::PlayerCantJump",
            "PlayerMovementController::PlayerCantRun",
            "PlayerFirearmWeaponController::CantShoot",
            "PlayerMeleeWeaponController::MeleeCantAttack",
            "PlayerGrenadeWeaponController::CantThrowGrenade",
            "PlayerInteractingManager::CantInteractWithItem",
            "MouseOrbitController::PlayerCantOrbitRotate",
        };

        public static void Postfix(ref bool __result)
        {
            if (AntiTankMine.Placing) __result = true;
        }

        public static void Install(Harmony harmony)
        {
            int patched = 0;
            HarmonyMethod post = new HarmonyMethod(typeof(MineLockHook).GetMethod("Postfix"));
            for (int i = 0; i < Sperren.Length; i++)
            {
                string[] parts = Sperren[i].Split(new string[] { "::" }, StringSplitOptions.None);
                try
                {
                    Type t = RevivalPlugin.TypeByName(parts[0]);
                    MethodInfo m = t == null ? null : AccessTools.Method(t, parts[1], null, null);
                    if (m == null || m.ReturnType != typeof(bool)) continue;
                    harmony.Patch(m, null, post, null, null, null);
                    patched++;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Mine-Sperre " + Sperren[i] + ": " + ex.Message);
                }
            }
            RevivalPlugin.L.LogInfo("Mine: Bewegungssperre " + patched + "/" + Sperren.Length + " gepatcht.");
        }
    }
}
