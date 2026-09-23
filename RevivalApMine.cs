// Next Day: Survival - Revival Toolkit
//
// PMN-2 anti-personnel mine (item 1492) - the whole feature in one file.
//
// Equip in the grenade slot (slot 3), left click once: the mine is set into
// the ground in front of the player, arms after ApMine/ArmSeconds and goes off
// under the first person who steps on it - a player, an NPC, the placer too -
// or under a vehicle that rolls over it. The model, maps and icon come from
// Blender (apmine_build.py -> assets/src/apmine_blender.py).
//
// THE ID IS THE EQUIP CATEGORY, exactly as for the anti-tank mine 1490 and the
// gas launcher 1491 (RE "An item's slot category comes from its ID RANGE"):
// only 1401..1500 opens the grenade slot, and the band holds 1401..1406 in the
// game plus 1490/1491 from the toolkit. 1492 is free there, in
// research/items.tsv and in the master server's weapons_db.xml. The grenade
// record the equip path asks for is cloned from the frag grenade 1403 on every
// lookup (ApMineDataHook), so no server file has to know the id.
//
// WHY IT IS NETWORKED AND THE ANTI-TANK MINE IS NOT. The anti-tank mine is a
// local object of the placer that watches vehicles, which every client sees
// the same. A mine for people has to see the one thing only its own client
// knows exactly: where that client's player puts his feet. So placement is
// one Photon event (EventCode 196, "apmine-v1") and every client builds the
// same mine from it; each client tests ONLY its own local player, and the
// master client, who owns every NPC (RE 35: ApplyDamage needs
// photonView.isMine), tests the NPCs and the vehicles. Whoever finds a victim
// fires the mine once: targeted damage to that victim through the game's own
// gates, one networked explosion through RocketHook.Detonate (the frag
// grenade's ExplosionObject, which hurts NPCs through RagdollBone colliders,
// RE 26), and a "gone" event that removes every other copy. Two clients can
// catch the same mine within one round trip; then it goes off twice, which is
// the price of never trusting a remote position.
//
// KNOWN LIMIT: a player who joins after a mine was laid does not receive it
// (no room cache - removing cached events per mine is not worth the risk).
// The mine stays on every client that saw it placed, also after the placer
// leaves. A map change destroys the objects; the registry drops them.
//
// C# 3.0. UTF-8 without BOM: the Russian strings are real Cyrillic.
//
// SEAMS OUTSIDE THIS FILE (one-liners, marked "NDR anti-personnel mine"):
//   RevivalPlugin.cs BuildItemTable -> ApMine.AddItems(Items)
//   RevivalPlugin.cs BindConfig / Install / Update(Tick)
//   Revival.Admin.cs  "Clear AP mines" -> ApMine.ClearAll()

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static class ApMine
    {
        public const int DEF_MINE = 1492;
        const int DEF_DONOR = 1403;
        const byte EventCode = 196;
        const string Tag = "apmine-v1";
        const int OpPlace = 0;
        const int OpGone = 1;
        // Damage part Body and damage type Explosion (RE 35). Part 0 would be
        // the head and triple the damage on an NPC.
        const int PartBody = 1;
        const int TypeExplosion = 14;

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<float> CfgFrontOffset;
        public static ConfigEntry<float> CfgScale;
        public static ConfigEntry<float> CfgSink;
        public static ConfigEntry<float> CfgArmSeconds;
        public static ConfigEntry<float> CfgTriggerRadius;
        public static ConfigEntry<float> CfgStepDamage;
        public static ConfigEntry<float> CfgExplosionDamage;
        public static ConfigEntry<float> CfgExplosionRadius;
        public static ConfigEntry<bool> CfgPlacerSafe;
        public static ConfigEntry<bool> CfgVehicles;
        public static ConfigEntry<int> CfgMaxOwn;

        static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }
        public static int MineId { get { return DEF_MINE; } }

        /// <summary>One laid mine, the same on every client that saw it.</summary>
        sealed class Laid
        {
            public string Key;
            public int Owner;
            public int Serial;
            public Vector3 Position;
            public float ArmAt;
            public float Born;
            public GameObject Go;
        }

        static readonly Dictionary<string, Laid> _laid = new Dictionary<string, Laid>();
        static readonly List<Laid> _scratch = new List<Laid>();
        static int _serial;
        static int _lastPlaceFrame = -1;
        static float _nextScan;
        static Material _mat;
        static Mesh _mesh;

        // ------------------------------------------------------------- item
        public static void AddItems(List<ItemDef> items)
        {
            items.Add(new ItemDef(
                DEF_MINE, DEF_DONOR, true,
                "Противопехотная мина ПМН-2", "Anti-personnel mine PMN-2",
                "Взять в слот гранаты 3. Левый клик - установить мину в землю перед собой. "
                + "Через несколько секунд она взводится и срабатывает под любым, кто на неё "
                + "наступит - и под вами тоже.",
                "Equip in grenade slot 3. Left click sets the mine into the ground in front "
                + "of you. It arms after a few seconds and goes off under anyone who steps "
                + "on it - you included.",
                "apmine.ndmesh", "apmine_diffuse.png", "apmine_normal.png",
                "apmine_icon.png", null,
                1, 0, 0.4f));
        }

        // ------------------------------------------------------------- config
        public static void BindConfig(ConfigFile cfg)
        {
            const string S = "ApMine";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "The PMN-2 anti-personnel mine (item 1492).");
            CfgFrontOffset = cfg.Bind(S, "FrontOffset", 2.2f,
                "Distance in front of the player the mine is set, in world units "
                + "(the world is about 2.8 units per metre).");
            CfgScale = cfg.Bind(S, "Scale", 1.25f,
                "Scale of the laid mine. 1.1 is true size (12.5 cm across); a little "
                + "more keeps it findable for the one who laid it.");
            CfgSink = cfg.Bind(S, "Sink", 0.35f,
                "Share of the mine's height set into the ground (0 = lies on top).");
            CfgArmSeconds = cfg.Bind(S, "ArmSeconds", 6f,
                "Seconds after placement before the mine can go off.");
            CfgTriggerRadius = cfg.Bind(S, "TriggerRadius", 1.0f,
                "Horizontal distance from a person's feet to the mine that sets it off "
                + "(a human capsule has radius 0.75).");
            CfgStepDamage = cfg.Bind(S, "StepDamage", 160f,
                "Damage to the one who steps on it, through the game's own damage gate "
                + "(armour and god mode apply). An NPC has 150 health.");
            CfgExplosionDamage = cfg.Bind(S, "ExplosionDamage", 120f,
                "Damage of the networked blast for everyone around (frag grenade: 140).");
            CfgExplosionRadius = cfg.Bind(S, "ExplosionRadius", 9f,
                "Radius of the blast in world units (frag grenade: 25).");
            CfgPlacerSafe = cfg.Bind(S, "PlacerSafe", false,
                "true: the one who laid a mine cannot set it off himself.");
            CfgVehicles = cfg.Bind(S, "VehiclesTrigger", true,
                "A vehicle rolling over a mine sets it off (the blast does the damage).");
            CfgMaxOwn = cfg.Bind(S, "MaxPerPlayer", 30,
                "Mines one player may have laid at once; the oldest goes when exceeded.");
        }

        // ------------------------------------------------------------ install
        public static void Install(Harmony harmony)
        {
            if (!Enabled) { RevivalPlugin.L.LogInfo("AP mine: disabled (ApMine/Enabled)."); return; }
            ApMineThrowHook.Install(harmony);
            ApMineDataHook.Install(harmony);
            RevivalPlugin.L.LogInfo("AP mine: PMN-2 active (id " + DEF_MINE
                + ", slot 3, left click to place, event " + EventCode + ").");
        }

        // --------------------------------------------------------------- tick
        public static void Tick()
        {
            if (!Enabled) return;
            Network();
            if (_laid.Count == 0 || Time.time < _nextScan) return;
            _nextScan = Time.time + 0.1f;
            try { Scan(); }
            catch (Exception ex) { RevivalPlugin.L.LogError("AP mine scan: " + ex); }
        }

        static void Scan()
        {
            _scratch.Clear();
            foreach (Laid m in _laid.Values) _scratch.Add(m);
            GameObject local = MapTools.LocalPlayer();
            // Players() resolves the death-state and in-vehicle fields that
            // Huntable reads; without that first call Huntable says yes to a
            // dead man.
            Crocodile.Players();
            bool localOnFoot = local != null && Crocodile.Huntable(local);
            int me = Crocodile.LocalActor();
            bool master = Crocodile.IsMaster();
            float r = CfgTriggerRadius == null ? 1.0f : Mathf.Max(0.3f, CfgTriggerRadius.Value);
            bool placerSafe = CfgPlacerSafe != null && CfgPlacerSafe.Value;

            for (int i = 0; i < _scratch.Count; i++)
            {
                Laid m = _scratch[i];
                if (m.Go == null) { _laid.Remove(m.Key); continue; }   // map change
                if (Time.time < m.ArmAt) continue;

                if (localOnFoot && !(placerSafe && m.Owner == me)
                    && Under(local.transform.position, m.Position, r))
                {
                    Fire(m, "local player");
                    HurtLocalPlayer();
                    continue;
                }
                if (!master) continue;
                Component npc = NpcOn(m.Position, r);
                if (npc != null)
                {
                    Fire(m, "NPC " + npc.gameObject.name);
                    HurtNpc(npc);
                    continue;
                }
                if (CfgVehicles == null || CfgVehicles.Value)
                {
                    Component vehicle = VehicleOn(m.Position, r * 0.6f);
                    if (vehicle != null) Fire(m, "vehicle " + vehicle.gameObject.name);
                }
            }
        }

        /// <summary>A person over the mine: horizontal distance within the
        /// radius, and the character's root no lower than a slope's worth
        /// under the mine and no higher than a capsule's middle above it
        /// (whether a player's root sits at the feet or at the capsule centre
        /// is not measured; the capsule is 5 units tall, RE "2.8 times real
        /// size") - a man on a roof overhead is not standing on it.</summary>
        static bool Under(Vector3 feet, Vector3 mine, float r)
        {
            float dy = feet.y - mine.y;
            if (dy < -1.5f || dy > 3.5f) return false;
            float dx = feet.x - mine.x, dz = feet.z - mine.z;
            return dx * dx + dz * dz <= r * r;
        }

        static MethodInfo _isAlive;

        static Component NpcOn(Vector3 mine, float r)
        {
            Component[] npcs = GasLauncher.Npcs();
            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs[i];
                if (ai == null || !ai.gameObject.activeInHierarchy) continue;
                if (!Under(ai.transform.position, mine, r)) continue;
                try
                {
                    if (_isAlive == null) _isAlive = AccessTools.Method(ai.GetType(), "IsAlive", null, null);
                    if (_isAlive != null)
                    {
                        object alive = _isAlive.Invoke(ai, null);
                        if (alive is bool && !(bool)alive) continue;   // a corpse is no trigger
                    }
                }
                catch { continue; }
                return ai;
            }
            return null;
        }

        static Component VehicleOn(Vector3 mine, float r)
        {
            Component[] all = VehicleScan.All();
            float r2 = r * r;
            for (int i = 0; i < all.Length; i++)
            {
                Component vgs = all[i];
                if (vgs == null) continue;
                if ((vgs.transform.position - mine).sqrMagnitude > 900f) continue;
                Renderer[] rends = vgs.GetComponentsInChildren<Renderer>(false);
                for (int c = 0; c < rends.Length; c++)
                {
                    if (rends[c] == null || !rends[c].enabled) continue;
                    if ((rends[c].bounds.ClosestPoint(mine) - mine).sqrMagnitude <= r2) return vgs;
                }
            }
            return null;
        }

        // --------------------------------------------------------------- fire
        static void Fire(Laid m, string who)
        {
            if (!_laid.Remove(m.Key)) return;          // exactly once per client
            RevivalPlugin.L.LogInfo("AP mine " + m.Key + ": set off by " + who + " at " + m.Position + ".");
            Vector3 at = m.Position;
            if (m.Go != null) UnityEngine.Object.Destroy(m.Go);
            Send(OpGone, m.Owner, m.Serial, at, Vector3.up);
            try
            {
                float dmg = CfgExplosionDamage == null ? 120f : CfgExplosionDamage.Value;
                float rad = CfgExplosionRadius == null ? 9f : CfgExplosionRadius.Value;
                RocketHook.Detonate(at + Vector3.up * 0.15f, dmg, rad, 3f);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("AP mine: explosion failed: " + ex.Message);
            }
        }

        static float StepDamage() { return CfgStepDamage == null ? 160f : Mathf.Max(0f, CfgStepDamage.Value); }

        /// <summary>The stepping player through PlayerLifeDataManager's own
        /// PlayerApplyDamage, the gate god mode and armour live behind (RE 29.3).</summary>
        static void HurtLocalPlayer()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerLifeDataManager");
                GameObject player = MapTools.LocalPlayer();
                if (t == null || player == null) return;
                Component mgr = player.GetComponentInChildren(t);
                if (mgr == null) mgr = player.GetComponentInParent(t);
                if (mgr == null) return;
                MethodInfo m = FirstFloatMethod(t, "PlayerApplyDamage");
                if (m == null)
                {
                    RevivalPlugin.L.LogWarning("AP mine: PlayerApplyDamage missing - only the blast hurts.");
                    return;
                }
                m.Invoke(mgr, Arguments(m, StepDamage()));
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AP mine: player damage: " + ex.Message); }
        }

        /// <summary>The stepping NPC through NPC_AI2.ApplyDamage (master only,
        /// it owns the NPC). Owner 0 with the settlement's kill streak broken
        /// first, as every plugin round does (RE 35).</summary>
        static void HurtNpc(Component ai)
        {
            try
            {
                MethodInfo m = AccessTools.Method(ai.GetType(), "ApplyDamage", null, null);
                if (m == null) return;
                FieldInfo fs = AccessTools.Field(ai.GetType(), "MySettlement");
                object home = fs == null ? null : fs.GetValue(ai);
                FieldInfo fk = home == null ? null : AccessTools.Field(home.GetType(), "_lastKillerId");
                if (fk != null && fk.FieldType == typeof(int) && (int)fk.GetValue(home) == 0)
                    fk.SetValue(home, -1);
                m.Invoke(ai, Arguments(m, StepDamage()));
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AP mine: NPC damage: " + ex.Message); }
        }

        static MethodInfo FirstFloatMethod(Type t, string name)
        {
            MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo best = null;
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != name) continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType != typeof(float)) continue;
                if (best == null || ps.Length < best.GetParameters().Length) best = ms[i];
            }
            return best;
        }

        /// <summary>First float = damage; ints by parameter name (part = body,
        /// type = explosion, owner and the rest 0); everything else default.</summary>
        static object[] Arguments(MethodInfo m, float damage)
        {
            ParameterInfo[] ps = m.GetParameters();
            object[] args = new object[ps.Length];
            bool placed = false;
            for (int i = 0; i < ps.Length; i++)
            {
                Type pt = ps[i].ParameterType;
                string name = ps[i].Name == null ? "" : ps[i].Name.ToLowerInvariant();
                if (!placed && pt == typeof(float)) { args[i] = damage; placed = true; }
                else if (pt == typeof(int))
                {
                    if (name.Contains("part")) args[i] = PartBody;
                    else if (name.Contains("type")) args[i] = TypeExplosion;
                    else args[i] = 0;
                }
                else if (pt == typeof(Vector3)) args[i] = Vector3.up;
                else if (pt == typeof(bool)) args[i] = false;
                else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                else args[i] = null;
            }
            return args;
        }

        // ---------------------------------------------------------- placement
        internal static void PlaceFromController(Component ctrl)
        {
            if (!Enabled || _lastPlaceFrame == Time.frameCount || ctrl == null
                || !IsMine(ctrl) || EquippedGrenadeId(ctrl) != DEF_MINE
                || InVehicle() || MapTools.LocalPlayer() == null) return;
            _lastPlaceFrame = Time.frameCount;
            try { Place(ctrl); }
            catch (Exception ex) { RevivalPlugin.L.LogError("AP mine placement: " + ex); }
        }

        static void Place(Component ctrl)
        {
            Vector3 pos, normal;
            if (!GroundInFront(out pos, out normal))
            {
                Turret.Hinweis(Loc.T("Нет ровной земли", "No valid ground"), 2f);
                return;
            }
            Network();
            int me = Crocodile.LocalActor();
            int serial = ++_serial;
            Laid m = Lay(me, serial, pos, normal);
            if (m == null)
            {
                Turret.Hinweis(Loc.T("Не удалось установить мину", "Could not place the mine"), 2f);
                return;
            }
            bool consumed = false;
            try { consumed = ConsumeEquipped(ctrl); }
            finally
            {
                if (!consumed)
                {
                    _laid.Remove(m.Key);
                    if (m.Go != null) { m.Go.SetActive(false); UnityEngine.Object.Destroy(m.Go); }
                }
            }
            if (!consumed)
            {
                Turret.Hinweis(Loc.T("Мина не списана - не установлена", "Could not use up the mine - not placed"), 2f);
                return;
            }
            Send(OpPlace, me, serial, m.Position, normal);
            TrimOwn(me);
            float arm = CfgArmSeconds == null ? 6f : Mathf.Max(0f, CfgArmSeconds.Value);
            RevivalPlugin.L.LogInfo("AP mine " + m.Key + ": laid at " + m.Position + ", arms in " + arm + " s.");
            Turret.Hinweis(Loc.T("Мина установлена - взведение через " + Mathf.RoundToInt(arm) + " с",
                                 "Mine set - arms in " + Mathf.RoundToInt(arm) + " s"), 3f);
        }

        /// <summary>Builds one mine and enters it in the registry. Used for our
        /// own placement and for every placement event from another client.</summary>
        static Laid Lay(int owner, int serial, Vector3 ground, Vector3 normal)
        {
            string key = owner + ":" + serial;
            if (_laid.ContainsKey(key)) return _laid[key];
            GameObject go = Build(ground, normal, key);
            if (go == null) return null;
            Laid m = new Laid();
            m.Key = key;
            m.Owner = owner;
            m.Serial = serial;
            m.Position = ground;
            m.Born = Time.time;
            m.ArmAt = Time.time + (CfgArmSeconds == null ? 6f : Mathf.Max(0f, CfgArmSeconds.Value));
            m.Go = go;
            _laid[key] = m;
            return m;
        }

        static void TrimOwn(int owner)
        {
            int max = CfgMaxOwn == null ? 30 : Mathf.Max(1, CfgMaxOwn.Value);
            while (true)
            {
                Laid oldest = null;
                int count = 0;
                foreach (Laid m in _laid.Values)
                {
                    if (m.Owner != owner) continue;
                    count++;
                    if (oldest == null || m.Born < oldest.Born) oldest = m;
                }
                if (count <= max || oldest == null) return;
                Remove(oldest.Key);
                Send(OpGone, oldest.Owner, oldest.Serial, oldest.Position, Vector3.up);
            }
        }

        static void Remove(string key)
        {
            Laid m;
            if (!_laid.TryGetValue(key, out m)) return;
            _laid.Remove(key);
            if (m.Go != null) UnityEngine.Object.Destroy(m.Go);
        }

        /// <summary>Admin: take every mine this client knows off the map, on
        /// every client that knows it too.</summary>
        public static string ClearAll()
        {
            _scratch.Clear();
            foreach (Laid m in _laid.Values) _scratch.Add(m);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Remove(_scratch[i].Key);
                Send(OpGone, _scratch[i].Owner, _scratch[i].Serial, _scratch[i].Position, Vector3.up);
            }
            return Loc.T("Сняты мины: " + _scratch.Count, "AP mines cleared: " + _scratch.Count);
        }

        public static int Count { get { return _laid.Count; } }

        static bool GroundInFront(out Vector3 pos, out Vector3 normal)
        {
            pos = Vector3.zero;
            normal = Vector3.up;
            GameObject player = MapTools.LocalPlayer();
            if (player == null) return false;
            Camera cam = Camera.main;
            Vector3 fwd = cam != null ? cam.transform.forward : player.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            float off = CfgFrontOffset == null ? 2.2f : Mathf.Max(1.0f, CfgFrontOffset.Value);
            Vector3 point, n;
            if (!AntiTankMine.RaycastNormal(player.transform.position + fwd * off + Vector3.up * 2.0f,
                                            Vector3.down, 8.0f, out point, out n)) return false;
            if (n.y < 0.5f) return false;       // no wall, no steep bank
            pos = point;
            normal = n;
            return true;
        }

        // -------------------------------------------------------------- model
        static GameObject Build(Vector3 ground, Vector3 normal, string key)
        {
            try
            {
                if (_mesh == null) _mesh = Assets.Load("apmine.ndmesh");
                if (_mesh == null)
                {
                    RevivalPlugin.L.LogWarning("AP mine: apmine.ndmesh missing - nothing laid.");
                    return null;
                }
                float s = CfgScale == null ? 1.25f : Mathf.Max(0.1f, CfgScale.Value);
                float sink = CfgSink == null ? 0.35f : Mathf.Clamp01(CfgSink.Value);
                GameObject go = new GameObject("NDR AP mine " + key);
                // Lid normal to the ground normal, a yaw from the key so every
                // client turns the same mine the same way, then set into the soil.
                Quaternion lie = Quaternion.FromToRotation(Vector3.up, normal);
                float yaw = (key.GetHashCode() & 0xffff) * (360f / 65536f);
                go.transform.rotation = lie * Quaternion.Euler(0f, yaw, 0f);
                go.transform.localScale = new Vector3(s, s, s);
                go.transform.position = ground - normal * (_mesh.bounds.size.y * s * sink);
                go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                Material mat = MineMaterial();
                if (mat != null) mr.sharedMaterial = mat;
                return go;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("AP mine build: " + ex);
                return null;
            }
        }

        /// <summary>The Blender maps on the Standard shader, wired the way
        /// ItemFactory.MakeMaterial wires an item: albedo, tangent normal,
        /// metallic in R and smoothness in A of apmine_metal.png.</summary>
        static Material MineMaterial()
        {
            if (_mat != null) return _mat;
            try
            {
                Shader sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
                Material m = new Material(sh);
                m.name = "NDR_APMine_Material";
                Texture2D albedo = Assets.Texture("apmine_diffuse.png", false, true);
                m.mainTexture = albedo;
                if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
                Texture2D nrm = Assets.Texture("apmine_normal.png", true, true);
                if (nrm != null && m.HasProperty("_BumpMap"))
                {
                    m.SetTexture("_BumpMap", nrm);
                    m.EnableKeyword("_NORMALMAP");
                }
                Texture2D gloss = Assets.TextureIfPresent("apmine_metal.png");
                if (gloss != null && m.HasProperty("_MetallicGlossMap"))
                {
                    m.SetTexture("_MetallicGlossMap", gloss);
                    if (m.HasProperty("_GlossMapScale")) m.SetFloat("_GlossMapScale", 1f);
                    m.EnableKeyword("_METALLICGLOSSMAP");
                }
                else
                {
                    if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.3f);
                    if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                }
                // A roughness-setup shader reads roughness from this slot instead
                // (the ItemFactory.MakeMaterial rule); Standard has no such slot.
                Texture2D rough = Assets.TextureIfPresent("apmine_rough.png");
                if (rough != null && m.HasProperty("_SpecGlossMap"))
                {
                    m.SetTexture("_SpecGlossMap", rough);
                    m.EnableKeyword("_SPECGLOSSMAP");
                }
                m.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _mat = m;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AP mine material: " + ex.Message); }
            return _mat;
        }

        // ------------------------------------------------------------ network
        static bool _netReady, _netFailed;
        static MethodInfo _raise;
        static Type _options;

        static int V(ConfigEntry<int> e) { return e == null ? -1 : e.Value; }

        static bool Taken(int first, int count) { return first >= 0 && EventCode >= first && EventCode < first + count; }

        static int Configured(Type owner, string field)
        {
            try
            {
                FieldInfo f = AccessTools.Field(owner, field);
                ConfigEntry<int> e = f == null ? null : f.GetValue(null) as ConfigEntry<int>;
                return e == null ? -1 : e.Value;
            }
            catch { return -1; }
        }

        static void Network()
        {
            if (_netReady || _netFailed) return;
            try
            {
                if (Taken(V(RevivalPlugin.CfgDroneEventCode), 5)
                    || Taken(V(DroneGear.CfgSurvEventCode), 4)
                    || Taken(V(RevivalTroopInsertion.CfgEventCode), 3)
                    || Taken(V(PlayerHeli.CfgEventCode), 6)
                    || EventCode == V(RevivalPlugin.CfgTurretEventCode)
                    || EventCode == V(RevivalPlugin.CfgAdminEventCode)
                    || EventCode == V(RevivalPlugin.CfgPatrolCrewDroneEventCode)
                    || EventCode == Configured(typeof(Mortar), "_cfgEventCode")
                    || EventCode == Crocodile.EventCode()
                    || EventCode == V(Gepard.CfgEventCode)
                    || EventCode == 191)   // Stinger, a constant
                {
                    _netFailed = true;
                    RevivalPlugin.L.LogError("AP mine: event " + EventCode
                        + " overlaps another configured channel - mines stay on this client.");
                    return;
                }
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                FieldInfo ev = photon == null ? null : AccessTools.Field(photon, "OnEventCall");
                if (ev == null) return;                 // Photon not loaded yet: try again
                _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                _options = RevivalPlugin.TypeByName("RaiseEventOptions");
                if (_raise == null || _options == null)
                {
                    _netFailed = true;
                    RevivalPlugin.L.LogWarning("AP mine: RaiseEvent missing - mines stay on this client.");
                    return;
                }
                Delegate callback = Delegate.CreateDelegate(ev.FieldType, typeof(ApMine).GetMethod("OnEvent"));
                ev.SetValue(null, Delegate.Combine(ev.GetValue(null) as Delegate, callback));
                _netReady = true;
                RevivalPlugin.L.LogInfo("AP mine: event channel " + EventCode + " ready.");
            }
            catch (Exception ex)
            {
                _netFailed = true;
                RevivalPlugin.L.LogError("AP mine: network: " + ex);
            }
        }

        static void Send(int op, int owner, int serial, Vector3 p, Vector3 n)
        {
            if (!_netReady) return;
            try
            {
                _raise.Invoke(null, new object[] { EventCode,
                    new object[] { Tag, op, owner, serial, new float[] { p.x, p.y, p.z, n.x, n.y, n.z } },
                    true, Activator.CreateInstance(_options) });
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AP mine: send: " + ex.Message); }
        }

        public static void OnEvent(byte code, object content, int sender)
        {
            if (code != EventCode || !_netReady) return;
            try
            {
                object[] p = content as object[];
                if (p == null || p.Length != 5 || !(p[0] is string) || (string)p[0] != Tag
                    || !(p[1] is int) || !(p[2] is int) || !(p[3] is int)) return;
                float[] v = p[4] as float[];
                if (v == null || v.Length != 6) return;
                for (int i = 0; i < v.Length; i++)
                    if (float.IsNaN(v[i]) || float.IsInfinity(v[i])) return;
                int op = (int)p[1], owner = (int)p[2], serial = (int)p[3];
                if (serial <= 0) return;
                if (op == OpPlace)
                {
                    // Only the placer announces his own mine, and not without end.
                    if (owner != sender || _laid.Count >= 400) return;
                    Vector3 n = new Vector3(v[3], v[4], v[5]);
                    if (n.sqrMagnitude < 0.25f) n = Vector3.up;
                    Laid m = Lay(owner, serial, new Vector3(v[0], v[1], v[2]), n.normalized);
                    if (m != null) m.Position = new Vector3(v[0], v[1], v[2]);
                }
                else if (op == OpGone)
                {
                    Remove(owner + ":" + serial);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AP mine: event: " + ex.Message); }
        }

        // ------------------------------------------------ equipped grenade
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

        internal static int EquippedGrenadeId(Component ctrl)
        {
            try
            {
                FieldInfo fd = AccessTools.Field(ctrl.GetType(), "_weaponGrenadeData");
                object data = fd == null ? null : fd.GetValue(ctrl);
                if (data == null) return -1;
                FieldInfo fid = AccessTools.Field(data.GetType(), "ItemID");
                return fid == null ? -1 : ToInt(fid.GetValue(data));
            }
            catch { return -1; }
        }

        static bool IsMine(Component c)
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

        static bool InVehicle()
        {
            try
            {
                Component[] all = VehicleScan.All();
                for (int i = 0; i < all.Length; i++)
                {
                    FieldInfo f = AccessTools.Field(all[i].GetType(), "_localPlayerPassengerId");
                    if (f == null) continue;
                    object v = f.GetValue(all[i]);
                    if (v is int && (int)v >= 0) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Clear weapon slot 2 (UI slot 3) exactly where vanilla
        /// consumes a thrown grenade (RE "Mine grenade equipment").</summary>
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
            if (items == null || items.Length < 3 || ToInt(items.GetValue(2)) != DEF_MINE) return false;
            MethodInfo clear = AccessTools.Method(inv.GetType(), "ClearWeaponSlot",
                new Type[] { typeof(int), typeof(int), typeof(bool), typeof(bool) }, null);
            if (clear == null) return false;
            clear.Invoke(inv, new object[] { 2, DEF_MINE, true, false });
            items = ids.GetValue(data) as Array;
            return items != null && ToInt(items.GetValue(2)) != DEF_MINE;
        }
    }

    /// <summary>
    /// Postfix on PlayerGrenadeWeaponController::CantThrowGrenade (called only
    /// from ThrowGrenade, IL-confirmed): for the PMN-2 the throw is refused and
    /// the mine is set instead. The game's own UI/state refusals stay intact.
    /// </summary>
    public static class ApMineThrowHook
    {
        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerGrenadeWeaponController");
                MethodInfo m = t == null ? null : AccessTools.Method(t, "CantThrowGrenade", null, null);
                if (m == null || m.ReturnType != typeof(bool))
                {
                    RevivalPlugin.L.LogWarning("AP mine: CantThrowGrenade missing - left click inactive.");
                    return;
                }
                harmony.Patch(m, null, new HarmonyMethod(typeof(ApMineThrowHook).GetMethod("Postfix")),
                              null, null, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("AP mine: CantThrowGrenade patch: " + ex); }
        }

        public static void Postfix(object __instance, ref bool __result)
        {
            try
            {
                if (__result || __instance == null) return;
                if (ApMine.EquippedGrenadeId(__instance as Component) != ApMine.MineId) return;
                // Block first: even a failed placement must never throw the mine.
                __result = true;
                ApMine.PlaceFromController(__instance as Component);
            }
            catch { }
        }
    }

    /// <summary>
    /// The grenade record for 1492: the frag grenade 1403 cloned field by field
    /// with only ItemID replaced, on every lookup, so neither a server XML
    /// reload nor a server without the id can take it away. The donor entry
    /// itself is never touched.
    /// </summary>
    public static class ApMineDataHook
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
                    RevivalPlugin.L.LogWarning("AP mine: GetGrenadeWeaponData(int) missing - "
                        + "the mine cannot be equipped.");
                    return;
                }
                harmony.Patch(get, new HarmonyMethod(typeof(ApMineDataHook).GetMethod("Prefix")),
                              null, null, null, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("AP mine: GetGrenadeWeaponData patch: " + ex); }
        }

        public static void Prefix(object __instance, int __0)
        {
            if (__0 != ApMine.MineId || __instance == null) return;
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
                FieldInfo mineId = AccessTools.Field(copy.GetType(), "ItemID");
                object value = __0;
                if (mineId.FieldType != typeof(int))
                {
                    MethodInfo convert = mineId.FieldType.GetMethod("op_Implicit",
                        BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(int) }, null);
                    if (convert == null) return;
                    value = convert.Invoke(null, new object[] { __0 });
                }
                mineId.SetValue(copy, value);
                entries[__0] = copy;
                RevivalPlugin.L.LogInfo("AP mine: grenade equipment data registered for " + __0);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AP mine: grenade data: " + ex.Message); }
        }
    }
}
