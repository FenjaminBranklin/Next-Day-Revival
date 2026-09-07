// Next Day: Survival - Revival Toolkit
//
// Equippable one-hit anti-tank mine - the whole feature in one file.
//
// Equip in the grenade slot (slot 3), then left click once to place on the
// ground. CantThrowGrenade preserves the game's UI/state restrictions and
// replaces an allowed mine throw with placement before any throw coroutine.
// The mine supplies its own grenade data and hand model through existing item
// seams. No server weapons_db entry is required for client-side equipment.
//
// THE ITEM ID IS THE EQUIP CATEGORY - see DEF_MINE below. The game reads an
// item's inventory category from its id range alone, so the mine must carry an
// id in its donor's band (1401..1500) or no weapon slot will accept it. That is
// why it is 1490 and why there is no MineId config key any more.
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
// C# 3.0; ASCII source, localized strings use Unicode escapes.
//
// SEAMS OUTSIDE THIS FILE (all one-liners, marked "NDR anti-tank mine"):
//   RevivalPlugin.cs BuildItemTable -> AntiTankMine.AddItems(Items)
//   RevivalPlugin.cs BindConfig / Install / Update(Tick) / OnGUI(Draw)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// Config, the item, click placement and equipped-grenade lookup. The placed mine
    /// itself is <see cref="MineObject"/>; the two hooks are below.
    /// </summary>
    public static class AntiTankMine
    {
        // THE ID IS THE EQUIP CATEGORY. It is not a free choice and it is not a
        // setting. CONFIRMED from IL (research/ilq.py):
        // `ItemDataManager::GetItemCatData(itemId)` is a hard-coded id-RANGE
        // switch with no per-item data, and everything that decides where an
        // item may go reads its result - `ItemSlotUI::LoadSlotItemCategoryData`
        // fills `ItemCategoryData` from it, `ItemSlotUI::SlotDetecting` turns
        // `GeneralCategory` into the list of weapon slots it highlights, and
        // `PlayerInventoryUISystem::GetOnWeaponSlotDrag` accepts a drop only on
        // a highlighted slot. The bands are:
        //     1401..1500  "LootSpawn/Weapons/Usable/"  Exact 6, General 2
        //                 -> the grenade slot; this is where donor 1403 lives
        //     2001..3000  "LootSpawn/Ammunation/"      Exact 8, General 3
        //                 -> ammunition; NO weapon slot is ever highlighted
        // The mine shipped as 2065 and was therefore read as AMMUNITION: no
        // slot lit up, no drop was accepted, and it could not be equipped -
        // exactly the SWAT-gear bug of 2026-09-03 (docs/ai/TASKS.md), whose
        // accepted fix was the same one used here. 1490 puts the mine in its
        // donor's own band, so `SetWeaponInHands` files it under
        // `_WeaponCategoryEquiped` 9 (Exact 6, id != 1401) - the grenade path,
        // byte for byte what the frag grenade 1403 gets.
        // 1490 is free: nothing in research/items.tsv, in the plugin's id space
        // (1160-1164, 2050-2064) or in the master server's weapons_db.xml uses
        // it - the band holds only 1401-1406.
        public const int DEF_MINE = 1490;
        const int DEF_DONOR = 1403;

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<float> CfgFrontOffset;
        public static ConfigEntry<float> CfgScale;
        public static ConfigEntry<float> CfgTriggerRadius;
        public static ConfigEntry<float> CfgExplosionRadius;
        public static ConfigEntry<float> CfgExplosionDamage;
        public static ConfigEntry<float> CfgKillDamage;
        public static ConfigEntry<bool> CfgConsume;
        public static ConfigEntry<string> CfgKey;

        static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }
        /// <summary>The mine's item id. NOT configurable: the game derives the
        /// equip category from the id band alone (see DEF_MINE), so any other
        /// value outside 1401..1500 makes the mine unequippable again. The
        /// former "AntiTankMine/MineId" key is gone for that reason; an
        /// existing config file keeps the orphan line, which BepInEx ignores.</summary>
        public static int MineId { get { return DEF_MINE; } }

        // ------------------------------------------------------------- state
        static bool _placing;
        static int _lastPlaceFrame = -1;
        public static bool Placing { get { return _placing; } }

        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;

        // Cached local grenade controller (persists; re-found on a throttle).
        static Component _gren;
        static float _grenUntil;
        static FieldInfo _fGrenData;
        static Material _mineMat;

        // ------------------------------------------------------------- item
        public static void AddItems(List<ItemDef> items)
        {
            items.Add(new ItemDef(
                DEF_MINE, DEF_DONOR, true,
                "\u041f\u0440\u043e\u0442\u0438\u0432\u043e\u0442\u0430\u043d\u043a\u043e\u0432\u0430\u044f \u043c\u0438\u043d\u0430 \u0422\u041c-62", "Anti-tank mine TM-62",
                "Equip in grenade slot 3. Left click once to place on the ground. "
                + "Infantry cannot trigger it; vehicles trigger a lethal blast.",
                "Equip in grenade slot 3. Left click once to place on the ground. "
                + "Infantry cannot trigger it; vehicles trigger a lethal blast.",
                "mine.ndmesh", "mine_diffuse.png", "mine_normal.png",
                "mine_icon.png", null,
                1, 0, 9.0f));
        }

        // ------------------------------------------------------------- config
        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("AntiTankMine", "Enabled", true,
                "Die Panzerabwehrmine (Item 1490) aktivieren.");
            // Kein MineId-Schluessel mehr. Die Id bestimmt beim Spiel die
            // Ausruestungskategorie (ItemDataManager::GetItemCatData ist eine
            // fest verdrahtete Id-Bereichsliste); ein frei gesetzter Wert
            // ausserhalb 1401..1500 macht die Mine wieder unausruestbar.
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
                "Optionale Ersatztaste fuer die Platzierung, falls die linke "
                + "Maustaste im Spiel nicht durchkommt. Standard None (aus).");
        }

        // ------------------------------------------------------------- install
        public static void Install(Harmony harmony)
        {
            if (!Enabled) { RevivalPlugin.L.LogInfo("Mine: abgeschaltet (AntiTankMine/Enabled)."); return; }
            MineGrenadeHook.Install(harmony);
            MineGrenadeDataHook.Install(harmony);
            MineLockHook.Install(harmony);
            RevivalPlugin.L.LogInfo("Mine: Panzerabwehrmine aktiv (Id " + MineId
                + ", slot 3, left click to place).");
        }

        // ------------------------------------------------------------- tick
        public static void Tick()
        {
            // Optional key uses the same game action and all of its guards.
            if (!Enabled) return;
            KeyCode k = Key();
            if (k == KeyCode.None || !Input.GetKeyDown(k) || !MineEquipped()) return;
            Component ctrl = GrenadeController();
            MethodInfo use = AccessTools.Method(ctrl.GetType(), "ThrowGrenade", null, null);
            if (use != null) use.Invoke(ctrl, null);
        }

        internal static void PlaceFromController(Component ctrl)
        {
            if (!Enabled || _placing || _lastPlaceFrame == Time.frameCount
                || ctrl == null || !IsMine(ctrl) || EquippedGrenadeId(ctrl) != MineId
                || InVehicle() || MapTools.LocalPlayer() == null) return;
            _lastPlaceFrame = Time.frameCount;
            _placing = true;
            try { Finish(ctrl); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Mine placement: " + ex); }
            finally { _placing = false; }
        }

        static void Finish(Component ctrl)
        {
            Vector3 pos, normal;
            if (!GroundInFront(out pos, out normal))
            {
                RevivalPlugin.L.LogWarning("Mine: kein gueltiger Boden vor dem Spieler.");
                Turret.Hinweis(Loc.T("\u041d\u0435\u0442 \u0440\u043e\u0432\u043d\u043e\u0439 \u0437\u0435\u043c\u043b\u0438", "No valid ground"), 2f);
                return;
            }

            GameObject mine = MineObject.Place(pos, normal);
            if (mine == null)
            {
                Turret.Hinweis(Loc.T("\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u043f\u043e\u0441\u0442\u0430\u0432\u0438\u0442\u044c \u043c\u0438\u043d\u0443", "Could not place the mine"), 2f);
                return;
            }

            // Consume exactly one - and only now, on success.
            bool consumed = false;
            try
            {
                consumed = (CfgConsume != null && !CfgConsume.Value) || ConsumeEquipped(ctrl);
            }
            finally
            {
                if (!consumed)
                {
                    // This also runs if inventory reflection or its callback throws.
                    mine.SetActive(false);
                    UnityEngine.Object.Destroy(mine);
                }
            }
            if (!consumed)
            {
                Turret.Hinweis("Could not consume the equipped mine", 2f);
                return;
            }

            RevivalPlugin.L.LogInfo("Mine: scharf bei " + pos + ".");
            Turret.Hinweis(Loc.T("\u041c\u0438\u043d\u0430 \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d\u0430", "Mine armed"), 2.5f);
        }

        // Clear slot 2 (third UI slot), exactly where vanilla consumes a grenade.
        // Do not use TakeItem: it removes backpack copies before equipped items.
        static bool ConsumeEquipped(Component ctrl)
        {
            FieldInfo f = AccessTools.Field(ctrl.GetType(), "_plrInventoryManager");
            object inv = f == null ? null : f.GetValue(ctrl);
            if (inv == null) return false;
            FieldInfo fw = AccessTools.Field(inv.GetType(), "_weaponsData");
            object data = fw == null ? null : fw.GetValue(inv);
            if (data == null) return false;
            FieldInfo ids = AccessTools.Field(data.GetType(), "ItemID");
            Array items = ids == null ? null : ids.GetValue(data) as Array;
            if (items == null || items.Length < 3
                || MineGrenadeHook.ToInt(items.GetValue(2)) != MineId) return false;
            MethodInfo clear = AccessTools.Method(inv.GetType(), "ClearWeaponSlot",
                new Type[] { typeof(int), typeof(int), typeof(bool), typeof(bool) }, null);
            if (clear == null) return false;
            clear.Invoke(inv, new object[] { 2, MineId, true, false });
            items = ids.GetValue(data) as Array;
            return items != null && MineGrenadeHook.ToInt(items.GetValue(2)) != MineId;
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
                for (int i = 0; i < all.Length; i++)
                {
                    Component c = all[i] as Component;
                    if (c == null) continue;
                    if (IsMine(c)) { _gren = c; break; }
                }
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
            // Keep the existing OnGUI seam; placement no longer has a hold timer.
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
    /// for the mine, placing it instead of starting the throw coroutine.
    /// CantThrowGrenade is called only from ThrowGrenade (IL-confirmed).
    /// Original UI/state restrictions and ordinary grenades are preserved.
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
                if (id == AntiTankMine.MineId)
                {
                    // Set the block before placement: even failure must never throw.
                    __result = true;
                    AntiTankMine.PlaceFromController(__instance as Component);
                }
            }
            catch { }
        }

        internal static int ToInt(object raw)
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

    // Ensure the grenade lookup always has a distinct mine record, including
    // after a server XML reload. Never relabel or mutate the donor entry.
    public static class MineGrenadeDataHook
    {
        public static void Install(Harmony harmony)
        {
            Type t = RevivalPlugin.TypeByName("xmlItemsDataManager");
            MethodInfo get = t == null ? null : AccessTools.Method(t,
                "GetGrenadeWeaponData", new Type[] { typeof(int) }, null);
            if (get == null) throw new MissingMethodException("GetGrenadeWeaponData(int)");
            harmony.Patch(get, new HarmonyMethod(typeof(MineGrenadeDataHook)
                .GetMethod("Prefix")), null, null, null, null);
        }

        public static void Prefix(object __instance, int __0)
        {
            if (__0 != AntiTankMine.MineId) return;
            FieldInfo f = AccessTools.Field(__instance.GetType(), "WeaponsGrenadeData");
            IDictionary entries = f == null ? null : f.GetValue(__instance) as IDictionary;
            if (entries == null) return;
            object existing = entries.Contains(__0) ? entries[__0] : null;
            if (existing != null)
            {
                FieldInfo id = AccessTools.Field(existing.GetType(), "ItemID");
                if (id != null && MineGrenadeHook.ToInt(id.GetValue(existing)) == __0) return;
            }
            object donor = entries.Contains(1403) ? entries[1403] : null;
            if (donor == null) return;
            object copy = Activator.CreateInstance(donor.GetType());
            foreach (FieldInfo field in donor.GetType().GetFields(BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic))
                field.SetValue(copy, field.GetValue(donor));
            FieldInfo mineId = AccessTools.Field(copy.GetType(), "ItemID");
            object value = __0;
            if (mineId.FieldType != typeof(int))
            {
                MethodInfo convert = mineId.FieldType.GetMethod("op_Implicit",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { typeof(int) }, null);
                if (convert == null) throw new InvalidOperationException("Mine ItemID conversion missing");
                value = convert.Invoke(null, new object[] { __0 });
            }
            mineId.SetValue(copy, value);
            entries[__0] = copy;
            RevivalPlugin.L.LogInfo("Mine: grenade equipment data registered for " + __0);
        }
    }

    // Input lock covers the synchronous placement/consumption transaction only.
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
