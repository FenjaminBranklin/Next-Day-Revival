// Next Day: Survival - Revival Toolkit
//
// A NEW WEAPON CLASS: area denial. RG-Kh "Tuman", a single-shot chemical
// launcher, and the toxic cloud its round leaves standing for half an hour.
// The whole weapon class is in this one file.
//
// WHAT IT DOES. Equip the tube in the grenade slot (slot 3), aim, left click
// once: the round arcs out on a ballistic path, bursts where it lands - one
// networked bang through the game's own ExplosionObject, so every client sees
// and hears the impact - and the burst leaves a GasCloud standing there for
// GasLauncher/Minutes (30 by default). Inside that cloud a bare face collects
// the game's own Toxicity, and NPCs take Toxicity damage until they drop. The
// tube is spent: exactly one launcher leaves the slot per shot.
//
// WHY IT IS A GRENADE-SLOT ITEM AND NOT A FIREARM. A firearm's numbers do not
// live in the plugin, they live in the master server's `staticdata\weapons_db.xml`
// (see tasks/m72-law-raketenwerfer.md: the LAW cost a whole session to that).
// An item in the grenade band needs NOTHING from the server - the anti-tank
// mine proved the path in 6.13: `ItemDataManager::GetItemCatData` derives the
// equip category from the id RANGE alone, 1401..1500 is the grenade slot, and
// `xmlItemsDataManager::GetGrenadeWeaponData` can be answered from the plugin
// by cloning the frag grenade's record (GasGrenadeDataHook below). So the id
// 1491 is not a free choice: outside 1401..1500 no weapon slot accepts the
// tube at all. 1491 is free - the band holds 1401..1406 in the game and 1490
// (the mine) in this plugin.
//
// THE GAS IS THE GAME'S OWN SURVIVAL MECHANIC, not a private damage counter.
// `PlayerLifeData.Toxicity` is one of the eleven life values the game already
// keeps (CONFIRMED IL, docs GAMEPLAY_CONTENT_EVIDENCE.txt; `ColdHook` writes
// its neighbours Cold/Temp the same way), it has its own HUD icon
// (gui/hud_lifestats/icons/toxicity_*), and anti-toxin TX-99 (7008) cures it
// through "ToxicityRegen". Standing in the cloud therefore reads in the HUD
// exactly like every other poisoning in this game, and the counter-play is the
// one the player already knows.
//
// PROTECTION - what "hazmat" means in item ids. Gear slot map (RE 14):
// 0 head, 1 mask, 2 upper body, 3 backpack, 4 hands, 5 vest/suit, 6 legs.
// Gas mask 4708 and the MAG-2 full-face respirator 4710 are the sealed masks;
// 4701..4704 are breathing masks that only help a little; the Protective L-1
// suits are 4203/4204/4205 (catalogue clothes/body/special). Mask AND suit is
// the full set and, by default, immunity - that is the point of carrying six
// kilos of rubber. Every id and every factor is a config key, so an admin can
// re-balance without a build.
//
// WHAT IS LOCAL AND WHAT IS NOT. The impact burst is networked (RocketHook.
// Detonate -> PhotonNetwork.Instantiate of the game's grenade prefab). The
// CLOUD is a local object on the firing client, like the placed mine: a
// networked, 30-minute scene object would need its own PhotonView, a prefab
// the other client can load and an owner that survives the shooter leaving.
// So a second player sees the explosion, not the fog. Noted, not hidden.
//
// ART. The tube reuses the LAW's mesh, textures and icon on purpose: a
// disposable shoulder tube is exactly what this weapon is, and new art needs
// `python make_assets.py`, which cannot run inside this task's sandbox. A
// dedicated gasgun_* asset set is a follow-up; nothing else depends on it.
//
// C# 3.0. Player-facing literals are bilingual (Loc.T) and carry real
// Cyrillic, as in RevivalPlugin.cs; comments, logs and config text stay as
// they are everywhere else in this repository.
//
// SEAMS OUTSIDE THIS FILE (five one-liners, marked "NDR gas launcher"):
//   RevivalPlugin.cs BuildItemTable -> GasLauncher.AddItems(Items)
//   RevivalPlugin.cs BindConfig / Awake(Install) / Update(Tick) / OnGUI(Draw)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The item, the shot, the protection rules and the shared lookups. The
    /// flying round is <see cref="GasRound"/>, the standing cloud is
    /// <see cref="GasCloud"/>, the two Harmony hooks are at the bottom.
    /// </summary>
    public static class GasLauncher
    {
        // THE ID IS THE EQUIP CATEGORY - see the file header. Not configurable
        // for the same reason the mine's id is not: a .cfg value outside
        // 1401..1500 would make the tube unequippable and look like a bug.
        public const int DEF_LAUNCHER = 1491;
        const int DEF_DONOR = 1403;              // frag grenade: the category donor

        public static int LauncherId { get { return DEF_LAUNCHER; } }

        // ------------------------------------------------------------- config
        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<float> CfgRadius;
        public static ConfigEntry<float> CfgMinutes;
        public static ConfigEntry<float> CfgSpeed;
        public static ConfigEntry<float> CfgGravity;
        public static ConfigEntry<float> CfgRange;
        public static ConfigEntry<float> CfgBurstDamage;
        public static ConfigEntry<float> CfgBurstRadius;
        public static ConfigEntry<float> CfgToxicityPerSecond;
        public static ConfigEntry<float> CfgHealthPerSecond;
        public static ConfigEntry<float> CfgDamageAbove;
        public static ConfigEntry<bool> CfgNpcDamage;
        public static ConfigEntry<float> CfgNpcDamagePerSecond;
        public static ConfigEntry<int> CfgMaxClouds;
        public static ConfigEntry<bool> CfgConsume;
        public static ConfigEntry<string> CfgMaskSealedIds;
        public static ConfigEntry<string> CfgMaskLightIds;
        public static ConfigEntry<string> CfgSuitIds;
        public static ConfigEntry<float> CfgMaskSealedFactor;
        public static ConfigEntry<float> CfgMaskLightFactor;
        public static ConfigEntry<float> CfgSuitFactor;
        public static ConfigEntry<bool> CfgFullSetImmune;
        public static ConfigEntry<string> CfgKey;
        public static ConfigEntry<string> CfgTestKey;

        public static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        // ------------------------------------------------------------- state
        static bool _firing;
        static int _lastShotFrame = -1;
        static KeyCode _key = KeyCode.None;
        static KeyCode _testKey = KeyCode.None;
        static bool _keysParsed;

        static Component _gren;                  // local grenade controller
        static float _grenUntil;
        static FieldInfo _fGrenData;

        static Component _life;                  // local PlayerLifeDataManager
        static float _lifeUntil;
        static Component _inv;                   // local PlayerInventoryManager
        static float _invUntil;

        static Component[] _npcs = new Component[0];
        static float _npcUntil;

        static Material _cloudMat;
        static Texture2D _blob;
        static bool _noShader;

        static bool _warnedToxField;
        static bool _warnedDamage;

        // HUD state: the cloud reports in, Draw paints while the report is fresh.
        static float _insideUntil;
        static float _insideProtection;
        static float _nextHint;

        static readonly List<GasCloud> _clouds = new List<GasCloud>();

        // --------------------------------------------------------------- item
        public static void AddItems(List<ItemDef> items)
        {
            items.Add(new ItemDef(
                DEF_LAUNCHER, DEF_DONOR, true,
                "Химический гранатомёт РГ-Х «Туман»", "RG-Kh 'Tuman' gas launcher",
                "Одноразовая труба с химической гранатой. Один выстрел - и участок "
                + "на полчаса затягивает ядовитым газом: в облаке растёт отравление, "
                + "и оно не разбирает, кто свой. Противогаз и костюм Л-1 спасают, "
                + "открытое лицо - нет. После выстрела труба пуста.",
                "Single-use tube with a chemical round. One shot leaves an area "
                + "under toxic gas for half an hour: everything inside the cloud "
                + "takes poisoning, and it does not care whose side you are on. A "
                + "gas mask and an L-1 suit keep you alive in there, a bare face "
                + "does not. The tube is empty after the shot.",
                "law.ndmesh", "law_diffuse.png", "law_normal.png",
                "law_icon.png", null,
                1, 0, 8.0f));
        }

        // ------------------------------------------------------------- config
        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("GasLauncher", "Enabled", true,
                "Den Chemie-Granatwerfer (Item 1491) aktivieren.");
            CfgRadius = cfg.Bind("GasLauncher", "Radius", 16f,
                "Radius der Giftgaswolke in Metern. 16 m entspricht einer Wolke "
                + "von gut dreissig Metern Durchmesser - das, was eine einzelne "
                + "Chemiegranate realistisch eindeckt, nicht ein halbes Dorf.");
            CfgMinutes = cfg.Bind("GasLauncher", "Minutes", 30f,
                "Standzeit der Wolke in Minuten. So lange bleibt das Gebiet "
                + "verseucht; die letzten drei Minuten duennt die Wolke aus.");
            CfgSpeed = cfg.Bind("GasLauncher", "LaunchSpeed", 80f,
                "Anfangsgeschwindigkeit der Granate (m/s).");
            CfgGravity = cfg.Bind("GasLauncher", "Gravity", 12f,
                "Fallbeschleunigung der Flugbahn. Groesser heisst kruemmerer "
                + "Bogen und kuerzere Schussweite.");
            CfgRange = cfg.Bind("GasLauncher", "Range", 400f,
                "Groesste Flugstrecke der Granate in Metern.");
            CfgBurstDamage = cfg.Bind("GasLauncher", "BurstDamage", 40f,
                "Schaden des Zerlegers am Einschlag. Bewusst klein: die Wirkung "
                + "dieser Waffe ist das Gas, nicht der Knall.");
            CfgBurstRadius = cfg.Bind("GasLauncher", "BurstRadius", 5f,
                "Radius des Zerlegers am Einschlag (m).");
            CfgToxicityPerSecond = cfg.Bind("GasLauncher", "ToxicityPerSecond", 3.0f,
                "Aufbau der Vergiftung je Sekunde in der Wolkenmitte, ohne "
                + "Schutz. Die Anzeige geht bis 100 - ohne Maske ist man also "
                + "in gut einer halben Minute voll.");
            CfgHealthPerSecond = cfg.Bind("GasLauncher", "HealthPerSecond", 1.2f,
                "Zusaetzlicher Lebenspunktschaden je Sekunde, sobald die "
                + "Vergiftung DamageAbove erreicht hat. 0 schaltet ihn ab und "
                + "ueberlaesst die Folgen ganz dem Spiel.");
            CfgDamageAbove = cfg.Bind("GasLauncher", "DamageAbove", 85f,
                "Ab diesem Vergiftungswert kostet die Wolke zusaetzlich Leben.");
            CfgNpcDamage = cfg.Bind("GasLauncher", "NpcDamage", true,
                "NPCs in der Wolke Schaden nehmen lassen.");
            CfgNpcDamagePerSecond = cfg.Bind("GasLauncher", "NpcDamagePerSecond", 3.0f,
                "Schaden je Sekunde an einem NPC in der Wolkenmitte. 3 toetet "
                + "einen Standard-NPC (150 Punkte) in knapp einer Minute.");
            CfgMaxClouds = cfg.Bind("GasLauncher", "MaxClouds", 6,
                "Hoechstzahl gleichzeitiger Wolken. Die aelteste weicht, damit "
                + "eine halbe Stunde Standzeit nicht die Karte zusetzt.");
            CfgConsume = cfg.Bind("GasLauncher", "Consume", true,
                "Genau eine Rohrwaffe je Schuss verbrauchen.");
            CfgMaskSealedIds = cfg.Bind("GasLauncher", "SealedMaskIds", "4708,4710",
                "Item-Ids dichter Masken (Gasmaske, Vollmaske MAG-2).");
            CfgMaskLightIds = cfg.Bind("GasLauncher", "LightMaskIds", "4701,4702,4703,4704",
                "Item-Ids einfacher Atemmasken - helfen nur wenig.");
            CfgSuitIds = cfg.Bind("GasLauncher", "SuitIds", "4203,4204,4205",
                "Item-Ids der Schutzanzuege (L-1) in Slot 5 oder 2.");
            CfgMaskSealedFactor = cfg.Bind("GasLauncher", "SealedMaskFactor", 0.10f,
                "Restwirkung des Gases mit dichter Maske (0 = nichts, 1 = voll).");
            CfgMaskLightFactor = cfg.Bind("GasLauncher", "LightMaskFactor", 0.55f,
                "Restwirkung des Gases mit einfacher Atemmaske.");
            CfgSuitFactor = cfg.Bind("GasLauncher", "SuitFactor", 0.35f,
                "Restwirkung des Gases mit Schutzanzug, zusaetzlich zur Maske.");
            CfgFullSetImmune = cfg.Bind("GasLauncher", "FullSetImmune", true,
                "Dichte Maske UND Schutzanzug machen vollstaendig immun. Das ist "
                + "der Lohn fuer sechs Kilo Gummi im Rucksack.");
            CfgKey = cfg.Bind("GasLauncher", "Key", "None",
                "Optionale Ersatztaste fuer den Schuss, falls die linke "
                + "Maustaste im Spiel nicht durchkommt. Standard None (aus).");
            CfgTestKey = cfg.Bind("GasLauncher", "TestKey", "None",
                "Nur zur Abnahme: setzt eine Wolke 15 m vor den Spieler, ohne "
                + "Waffe und ohne Schuss. Standard None (aus).");
        }

        static float Radius() { return CfgRadius == null ? 16f : Mathf.Clamp(CfgRadius.Value, 3f, 60f); }
        static float Seconds() { return (CfgMinutes == null ? 30f : Mathf.Clamp(CfgMinutes.Value, 0.2f, 180f)) * 60f; }
        static float Speed() { return CfgSpeed == null ? 80f : Mathf.Clamp(CfgSpeed.Value, 10f, 400f); }
        static float Gravity() { return CfgGravity == null ? 12f : Mathf.Clamp(CfgGravity.Value, 0f, 40f); }
        static float Range() { return CfgRange == null ? 400f : Mathf.Clamp(CfgRange.Value, 20f, 2000f); }

        internal static float ToxicityPerSecond() { return CfgToxicityPerSecond == null ? 3f : Mathf.Max(0f, CfgToxicityPerSecond.Value); }
        internal static float HealthPerSecond() { return CfgHealthPerSecond == null ? 1.2f : Mathf.Max(0f, CfgHealthPerSecond.Value); }
        internal static float DamageAbove() { return CfgDamageAbove == null ? 85f : Mathf.Clamp(CfgDamageAbove.Value, 1f, 100f); }
        internal static bool NpcDamage() { return CfgNpcDamage == null || CfgNpcDamage.Value; }
        internal static float NpcDamagePerSecond() { return CfgNpcDamagePerSecond == null ? 3f : Mathf.Max(0f, CfgNpcDamagePerSecond.Value); }

        // ------------------------------------------------------------ install
        public static void Install(Harmony harmony)
        {
            if (!Enabled)
            {
                RevivalPlugin.L.LogInfo("Gas launcher: abgeschaltet (GasLauncher/Enabled).");
                return;
            }
            // Nothing in here may take the rest of Awake down with it: each
            // hook logs its own failure, and this catch is the last net.
            try
            {
                GasGrenadeDataHook.Install(harmony);
                GasThrowHook.Install(harmony);
                RevivalPlugin.L.LogInfo("Gas launcher: RG-Kh aktiv (Id " + DEF_LAUNCHER
                    + ", slot 3, left click to fire, "
                    + (Seconds() / 60f).ToString("0.#", CultureInfo.InvariantCulture)
                    + " min Wolke, Radius " + Radius().ToString("0.#", CultureInfo.InvariantCulture) + " m).");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gas launcher konnte nicht eingehaengt werden: " + ex);
            }
        }

        // --------------------------------------------------------------- tick
        public static void Tick()
        {
            if (!Enabled) return;
            try
            {
                Keys();

                if (_key != KeyCode.None && Input.GetKeyDown(_key) && Equipped())
                {
                    // Same game action as the mouse, so every original guard runs.
                    Component ctrl = GrenadeController();
                    if (ctrl != null)
                    {
                        MethodInfo use = AccessTools.Method(ctrl.GetType(), "ThrowGrenade", null, null);
                        if (use != null) use.Invoke(ctrl, null);
                    }
                }

                if (_testKey != KeyCode.None && Input.GetKeyDown(_testKey)) TestCloud();

                // Keep the registry free of destroyed clouds.
                for (int i = _clouds.Count - 1; i >= 0; i--)
                    if (_clouds[i] == null) _clouds.RemoveAt(i);
            }
            catch (Exception ex)
            {
                // One frame's worth of trouble must not take the plugin's whole
                // Update with it.
                RevivalPlugin.L.LogWarning("Gas launcher tick: " + ex.Message);
            }
        }

        static void Keys()
        {
            if (_keysParsed) return;
            _keysParsed = true;
            _key = ParseKey(CfgKey == null ? "None" : CfgKey.Value);
            _testKey = ParseKey(CfgTestKey == null ? "None" : CfgTestKey.Value);
        }

        static KeyCode ParseKey(string s)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
            catch { return KeyCode.None; }
        }

        /// <summary>Abnahmehilfe: eine Wolke vor den Spieler, ohne Waffe.</summary>
        static void TestCloud()
        {
            GameObject player = MapTools.LocalPlayer();
            if (player == null) return;
            Camera cam = Camera.main;
            Vector3 fwd = cam != null ? cam.transform.forward : player.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 spot = player.transform.position + fwd * 15f;
            Vector3 point, normal;
            if (Ground(spot, out point, out normal)) spot = point;
            Impact(spot, Vector3.up, false);
            RevivalPlugin.L.LogInfo("Gas launcher: Testwolke bei " + spot + ".");
        }

        // --------------------------------------------------------------- shot
        /// <summary>
        /// Called from the CantThrowGrenade postfix for our id only. Runs the
        /// whole shot synchronously: trajectory, tube consumption, round.
        /// Never throws into the game's throw path.
        /// </summary>
        internal static void FireFromController(Component ctrl)
        {
            if (!Enabled || _firing || _lastShotFrame == Time.frameCount
                || ctrl == null || !IsMine(ctrl)
                || EquippedGrenadeId(ctrl) != DEF_LAUNCHER
                || MapTools.LocalPlayer() == null) return;
            _lastShotFrame = Time.frameCount;
            _firing = true;
            try { Shoot(ctrl); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Gas launcher shot: " + ex); }
            finally { _firing = false; }
        }

        static void Shoot(Component ctrl)
        {
            GameObject player = MapTools.LocalPlayer();
            Camera cam = Camera.main;
            if (cam == null && player == null) return;
            Vector3 origin;
            Vector3 dir;
            if (cam != null)
            {
                Transform t = cam.transform;
                origin = t.position + t.forward * 0.9f - t.up * 0.15f;
                dir = t.forward;
            }
            else
            {
                origin = player.transform.position + Vector3.up * 1.55f
                       + player.transform.forward * 0.9f;
                dir = player.transform.forward;
            }

            List<Vector3> path = new List<Vector3>();
            Vector3 point, normal;
            bool hit = Trace(origin, dir, path, out point, out normal);

            // Spend the tube first: a shot that cannot be paid for is not fired
            // at all, so the player never loses a round to a failed inventory
            // call and never fires two from one tube.
            bool consumed = (CfgConsume != null && !CfgConsume.Value) || ConsumeEquipped(ctrl);
            if (!consumed)
            {
                Turret.Hinweis(Loc.T("Не удалось израсходовать заряд",
                                     "Could not consume the equipped launcher"), 2f);
                return;
            }

            GasRound.Launch(path, Speed(), point, normal, hit);
            RevivalPlugin.L.LogInfo("Gas launcher: Schuss, Einschlag bei " + point
                + (hit ? "" : " (kein Treffer, Bahnende)") + ".");
        }

        /// <summary>
        /// Ballistic path in short straight segments, each one raycast. Same
        /// shape as the LAW's trajectory (RocketHook.TraceTrajectory), with the
        /// launcher's own speed and drop: this weapon is meant to be lobbed
        /// over a wall, so the arc is part of the weapon and not a detail.
        /// Returns true on a real impact; the path is filled either way.
        /// </summary>
        static bool Trace(Vector3 origin, Vector3 dir, List<Vector3> path,
                          out Vector3 point, out Vector3 normal)
        {
            const float step = 0.05f;
            float speed = Speed();
            float g = Gravity();
            float range = Range();

            dir.Normalize();
            point = origin;
            normal = Vector3.up;
            path.Add(origin);

            Vector3 prev = origin;
            int steps = Mathf.Max(1, Mathf.CeilToInt(range / Mathf.Max(1f, speed * step)));
            for (int i = 1; i <= steps; i++)
            {
                float t = i * step;
                Vector3 next = origin + dir * (speed * t)
                             + Vector3.down * (0.5f * g * t * t);
                Vector3 seg = next - prev;
                float len = seg.magnitude;
                RaycastHit h;
                if (len > 0.0001f
                    && Physics.Raycast(prev, seg / len, out h, len,
                                       Physics.DefaultRaycastLayers,
                                       QueryTriggerInteraction.Ignore))
                {
                    point = h.point;
                    normal = h.normal;
                    path.Add(point);
                    return true;
                }
                path.Add(next);
                prev = next;
            }
            point = prev;
            return false;
        }

        /// <summary>
        /// Impact: one networked burst so every client sees where it landed,
        /// then the cloud that is the actual weapon.
        /// </summary>
        internal static void Impact(Vector3 point, Vector3 normal, bool burst)
        {
            if (burst)
            {
                try
                {
                    float dmg = CfgBurstDamage == null ? 40f : Mathf.Max(0f, CfgBurstDamage.Value);
                    float rad = CfgBurstRadius == null ? 5f : Mathf.Max(0.5f, CfgBurstRadius.Value);
                    RocketHook.Detonate(point + normal * 0.15f, dmg, rad, 3f);
                }
                catch (Exception ex)
                {
                    // No burst is a cosmetic loss; the gas is the weapon.
                    RevivalPlugin.L.LogWarning("Gas launcher: Zerleger nicht gezuendet - "
                        + ex.Message);
                }
            }

            GasCloud cloud = GasCloud.Spawn(point + normal * 0.30f, Radius(), Seconds());
            if (cloud == null) return;
            Turret.Hinweis(Loc.T("Химический выстрел лёг", "Chemical round on target"), 2.5f);
        }

        // --------------------------------------------------------- cloud list
        internal static void Register(GasCloud cloud)
        {
            int max = CfgMaxClouds == null ? 6 : Mathf.Clamp(CfgMaxClouds.Value, 1, 32);
            for (int i = _clouds.Count - 1; i >= 0; i--)
                if (_clouds[i] == null) _clouds.RemoveAt(i);
            while (_clouds.Count >= max)
            {
                GasCloud oldest = _clouds[0];
                _clouds.RemoveAt(0);
                if (oldest != null) UnityEngine.Object.Destroy(oldest.gameObject);
            }
            _clouds.Add(cloud);
        }

        // ------------------------------------------------------- consumption
        /// <summary>
        /// Clear weapon slot 2 - exactly where the game consumes a thrown
        /// grenade, and the slot the mine uses. TakeItem is NOT used: it would
        /// take a spare tube out of the backpack and leave the equipped one.
        /// </summary>
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
                || ToInt(items.GetValue(2)) != DEF_LAUNCHER) return false;
            MethodInfo clear = AccessTools.Method(inv.GetType(), "ClearWeaponSlot",
                new Type[] { typeof(int), typeof(int), typeof(bool), typeof(bool) }, null);
            if (clear == null) return false;
            clear.Invoke(inv, new object[] { 2, DEF_LAUNCHER, true, false });
            items = ids.GetValue(data) as Array;
            return items != null && ToInt(items.GetValue(2)) != DEF_LAUNCHER;
        }

        // --------------------------------------------------- equipped tube
        static bool Equipped()
        {
            Component ctrl = GrenadeController();
            return ctrl != null && EquippedGrenadeId(ctrl) == DEF_LAUNCHER;
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
                RevivalPlugin.L.LogWarning("Gas launcher: Granatencontroller-Suche: " + ex.Message);
            }
            return _gren;
        }

        internal static int EquippedGrenadeId(Component ctrl)
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
                return ToInt(fId.GetValue(data));
            }
            catch { return -1; }
        }

        // -------------------------------------------------------- protection
        /// <summary>
        /// How much of the gas still reaches the player: 1 is a bare face, 0 is
        /// sealed. Mask and suit multiply, and the full set is immunity when
        /// FullSetImmune is on.
        /// </summary>
        internal static float Protection()
        {
            // Answered from a cache for a third of a second: every standing
            // cloud asks twice a second, and the answer cannot change faster
            // than a player can put on a mask.
            if (Time.time < _protUntil) return _protection;
            _protUntil = Time.time + 0.34f;
            _protection = ReadProtection();
            return _protection;
        }

        static float _protection = 1f;
        static float _protUntil;

        static float ReadProtection()
        {
            try
            {
                int mask = Gear(1);
                int vest = Gear(5);
                int body = Gear(2);
                bool sealedMask = InList(CfgMaskSealedIds, mask);
                bool lightMask = !sealedMask && InList(CfgMaskLightIds, mask);
                bool suit = InList(CfgSuitIds, vest) || InList(CfgSuitIds, body);

                if (sealedMask && suit && (CfgFullSetImmune == null || CfgFullSetImmune.Value))
                    return 0f;

                float f = 1f;
                if (sealedMask) f *= CfgMaskSealedFactor == null ? 0.10f : Mathf.Clamp01(CfgMaskSealedFactor.Value);
                else if (lightMask) f *= CfgMaskLightFactor == null ? 0.55f : Mathf.Clamp01(CfgMaskLightFactor.Value);
                if (suit) f *= CfgSuitFactor == null ? 0.35f : Mathf.Clamp01(CfgSuitFactor.Value);
                return Mathf.Clamp01(f);
            }
            catch { return 1f; }
        }

        /// <summary>Item id in one of the seven gear slots, 0 when empty.</summary>
        static int Gear(int slot)
        {
            Component inv = Inventory();
            if (inv == null) return 0;
            FieldInfo fg = AccessTools.Field(inv.GetType(), "_gearsData");
            object data = fg == null ? null : fg.GetValue(inv);
            if (data == null) return 0;
            FieldInfo ids = AccessTools.Field(data.GetType(), "ItemID");
            Array items = ids == null ? null : ids.GetValue(data) as Array;
            if (items == null || slot < 0 || slot >= items.Length) return 0;
            int id = ToInt(items.GetValue(slot));
            return id < 0 ? 0 : id;
        }

        static bool InList(ConfigEntry<string> entry, int id)
        {
            if (id <= 0 || entry == null) return false;
            string s = entry.Value;
            if (s == null || s.Length == 0) return false;
            string[] parts = s.Split(new char[] { ',', ';', ' ' });
            string want = id.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Trim() == want) return true;
            return false;
        }

        // --------------------------------------------------------- life data
        /// <summary>
        /// Raise the game's own Toxicity counter. Written the same way
        /// ColdHook writes Cold and Temp: the value is an ObscuredFloat, so
        /// the number goes in through the type's implicit conversion.
        /// </summary>
        internal static void AddToxicity(float delta)
        {
            if (delta <= 0f) return;
            try
            {
                object data = LifeData();
                if (data == null) return;
                FieldInfo f = AccessTools.Field(data.GetType(), "Toxicity");
                if (f == null)
                {
                    if (!_warnedToxField)
                    {
                        _warnedToxField = true;
                        RevivalPlugin.L.LogWarning("Gas launcher: PlayerLifeData.Toxicity "
                            + "fehlt - die Wolke vergiftet den Spieler nicht.");
                    }
                    return;
                }
                float now = ToFloat(f.GetValue(data));
                float next = Mathf.Clamp(now + delta, 0f, 100f);
                f.SetValue(data, FromFloat(f.FieldType, next));
            }
            catch (Exception ex)
            {
                if (!_warnedToxField)
                {
                    _warnedToxField = true;
                    RevivalPlugin.L.LogWarning("Gas launcher: Vergiftung nicht gesetzt: " + ex.Message);
                }
            }
        }

        internal static float Toxicity()
        {
            try
            {
                object data = LifeData();
                if (data == null) return 0f;
                FieldInfo f = AccessTools.Field(data.GetType(), "Toxicity");
                return f == null ? 0f : ToFloat(f.GetValue(data));
            }
            catch { return 0f; }
        }

        /// <summary>
        /// Health loss on top of the poisoning, through the game's OWN gate:
        /// PlayerLifeDataManager::PlayerApplyDamage consults CanApplyDamage
        /// (RE 29.3), so admin god mode and armour keep working and nothing
        /// here has to know how health is stored.
        /// </summary>
        internal static void HurtPlayer(float damage)
        {
            if (damage <= 0f || _warnedDamage) return;
            try
            {
                Component mgr = LifeManager();
                if (mgr == null) return;
                MethodInfo m = FirstFloatMethod(mgr.GetType(), "PlayerApplyDamage");
                if (m == null)
                {
                    _warnedDamage = true;
                    RevivalPlugin.L.LogWarning("Gas launcher: PlayerApplyDamage nicht "
                        + "gefunden - die Wolke vergiftet, kostet aber kein Leben.");
                    return;
                }
                m.Invoke(mgr, Arguments(m, damage));
            }
            catch (Exception ex)
            {
                _warnedDamage = true;
                RevivalPlugin.L.LogWarning("Gas launcher: Spielerschaden: " + ex.Message);
            }
        }

        static object LifeData()
        {
            Component mgr = LifeManager();
            if (mgr == null) return null;
            FieldInfo f = AccessTools.Field(mgr.GetType(), "_playerLifeData");
            return f == null ? null : f.GetValue(mgr);
        }

        static Component LifeManager()
        {
            if (_life != null && Time.time < _lifeUntil) return _life;
            _lifeUntil = Time.time + 2f;
            _life = LocalComponent("PlayerLifeDataManager");
            return _life;
        }

        static Component Inventory()
        {
            if (_inv != null && Time.time < _invUntil) return _inv;
            _invUntil = Time.time + 2f;
            _inv = LocalComponent("PlayerInventoryManager");
            return _inv;
        }

        /// <summary>The local player's instance of a game component.</summary>
        static Component LocalComponent(string typeName)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName(typeName);
                if (t == null) return null;
                GameObject player = MapTools.LocalPlayer();
                if (player != null)
                {
                    Component own = player.GetComponentInChildren(t);
                    if (own == null) own = player.GetComponentInParent(t);
                    if (own != null) return own;
                }
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
                for (int i = 0; i < all.Length; i++)
                {
                    Component c = all[i] as Component;
                    if (c != null && IsMine(c)) return c;
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Gas launcher: " + typeName + " nicht gefunden: "
                    + ex.Message);
            }
            return null;
        }

        // ---------------------------------------------------------- the NPCs
        /// <summary>
        /// Every NPC_AI2 in the scene, rebuilt at most every three seconds and
        /// only while a cloud is standing. FindObjectsOfType is the kind of
        /// cost that never shows in a log and always shows in the frame time.
        /// </summary>
        internal static Component[] Npcs()
        {
            if (Time.time < _npcUntil) return _npcs;
            _npcUntil = Time.time + 3f;
            try
            {
                Type t = RevivalPlugin.TypeByName("NPC_AI2");
                if (t == null) { _npcs = new Component[0]; return _npcs; }
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
                List<Component> list = new List<Component>(all.Length);
                for (int i = 0; i < all.Length; i++)
                {
                    Component c = all[i] as Component;
                    if (c != null) list.Add(c);
                }
                _npcs = list.ToArray();
            }
            catch { _npcs = new Component[0]; }
            return _npcs;
        }

        /// <summary>
        /// Toxicity damage on one NPC through its own ApplyDamage - body part,
        /// damage type 12 (Toxicity), owner 0, exactly like every other plugin
        /// round. BreakStreak first: NPC_Settlement.StatsOnNpcKilled reads
        /// PhotonPlayer.Find(_lastKillerId) on the kill that clears a
        /// settlement and throws for owner 0 (RE 35); resetting an id-0 streak
        /// is the same guard NpcWar uses.
        /// </summary>
        internal static void HurtNpc(Component ai, float damage)
        {
            if (ai == null || damage <= 0f) return;
            try
            {
                MethodInfo m = AccessTools.Method(ai.GetType(), "ApplyDamage", null, null);
                if (m == null) return;
                BreakStreak(ai);
                m.Invoke(ai, Arguments(m, damage));
            }
            catch { }
        }

        static void BreakStreak(Component ai)
        {
            try
            {
                FieldInfo fs = AccessTools.Field(ai.GetType(), "MySettlement");
                object home = fs == null ? null : fs.GetValue(ai);
                if (home == null) return;
                FieldInfo fk = AccessTools.Field(home.GetType(), "_lastKillerId");
                if (fk == null || fk.FieldType != typeof(int)) return;
                if ((int)fk.GetValue(home) == 0) fk.SetValue(home, -1);
            }
            catch { }
        }

        // -------------------------------------------------------- reflection
        /// <summary>
        /// Fills a damage call: the first float is the damage, ints are read
        /// from their parameter names (part = body, type = Toxicity, owner 0),
        /// everything else gets its default. Same idea as Turret.TryDamage,
        /// but with the two ints deliberately set instead of left at zero -
        /// part 0 is the HEAD and would triple every tick (RE 35).
        /// </summary>
        static object[] Arguments(MethodInfo m, float damage)
        {
            ParameterInfo[] ps = m.GetParameters();
            object[] args = new object[ps.Length];
            bool damagePlaced = false;
            for (int i = 0; i < ps.Length; i++)
            {
                Type pt = ps[i].ParameterType;
                string name = ps[i].Name == null ? "" : ps[i].Name.ToLowerInvariant();
                if (!damagePlaced && pt == typeof(float))
                {
                    args[i] = damage;
                    damagePlaced = true;
                }
                else if (pt == typeof(int))
                {
                    if (name.Contains("part")) args[i] = 1;          // body
                    else if (name.Contains("type")) args[i] = 12;    // Toxicity
                    else args[i] = 0;                                // owner, rest
                }
                else if (pt == typeof(Vector3)) args[i] = Vector3.zero;
                else if (pt == typeof(bool)) args[i] = false;
                else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                else args[i] = null;
            }
            return args;
        }

        static MethodInfo FirstFloatMethod(Type t, string name)
        {
            MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance);
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

        static float ToFloat(object raw)
        {
            if (raw == null) return 0f;
            if (raw is float) return (float)raw;
            try
            {
                MethodInfo[] ms = raw.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != typeof(float)) continue;
                    ParameterInfo[] ps = ms[i].GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == raw.GetType())
                        return (float)ms[i].Invoke(null, new object[] { raw });
                }
                return Convert.ToSingle(raw);
            }
            catch { return 0f; }
        }

        static object FromFloat(Type t, float value)
        {
            if (t == typeof(float)) return value;
            MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != t) continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(float))
                    return ms[i].Invoke(null, new object[] { value });
            }
            return Activator.CreateInstance(t);
        }

        internal static bool IsMine(Component c)
        {
            try
            {
                Type pv = RevivalPlugin.TypeByName("PhotonView");
                if (pv == null) return true;              // single player: ours
                Component view = c.GetComponentInParent(pv);
                if (view == null) return true;
                MethodInfo getter = AccessTools.PropertyGetter(pv, "isMine");
                if (getter == null) return true;
                object r = getter.Invoke(view, null);
                return r is bool && (bool)r;
            }
            catch { return true; }
        }

        internal static bool Ground(Vector3 near, out Vector3 point, out Vector3 normal)
        {
            point = near;
            normal = Vector3.up;
            RaycastHit h;
            if (!Physics.Raycast(near + Vector3.up * 4f, Vector3.down, out h, 40f,
                                 Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return false;
            point = h.point;
            normal = h.normal;
            return true;
        }

        // ---------------------------------------------------------- material
        /// <summary>
        /// One soft blob texture and one alpha-blended material for every
        /// cloud. Runtime material, not an asset: `Destroy(cloud)` does not
        /// take a material with it, and HideAndDontSave carries this one over
        /// a scene change (the lesson from the tracer and the wreck fire).
        /// </summary>
        internal static Material CloudMaterial()
        {
            if (_cloudMat != null) return _cloudMat;
            if (_noShader) return null;
            Shader s = null;
            string[] names = new string[] {
                "Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended",
                "Mobile/Particles/Alpha Blended", "Particles/Additive" };
            for (int i = 0; i < names.Length && s == null; i++) s = Shader.Find(names[i]);
            if (s == null)
            {
                _noShader = true;
                RevivalPlugin.L.LogWarning("Gas launcher: kein Partikelshader im Build - "
                    + "die Wolke wirkt, ist aber unsichtbar.");
                return null;
            }
            Material m = new Material(s);
            m.name = "NDR_Gas_Material";
            Texture2D t = Blob();
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", t);
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            m.hideFlags = HideFlags.HideAndDontSave;
            _cloudMat = m;
            return _cloudMat;
        }

        static Texture2D Blob()
        {
            if (_blob != null) return _blob;
            const int N = 64;
            Texture2D t = new Texture2D(N, N, TextureFormat.ARGB32, false);
            Color[] px = new Color[N * N];
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N * 2f - 1f;
                    float dy = (y + 0.5f) / N * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * (3f - 2f * a);
                    px[y * N + x] = new Color(1f, 1f, 1f, a);
                }
            }
            t.SetPixels(px);
            t.Apply();
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.hideFlags = HideFlags.HideAndDontSave;
            _blob = t;
            return _blob;
        }

        // --------------------------------------------------------------- HUD
        /// <summary>A standing cloud reports the local player's exposure.</summary>
        internal static void ReportInside(float dose, float protection)
        {
            _insideUntil = Time.time + 1.2f;
            _insideProtection = protection;

            // Do not nag at the fringe of a thinning cloud - only where the
            // gas is actually thick enough to matter.
            if (protection > 0f && dose > 0.15f && Time.time >= _nextHint)
            {
                _nextHint = Time.time + 8f;
                Turret.Hinweis(protection >= 0.9f
                    ? Loc.T("ГАЗ! Наденьте противогаз", "GAS! Put on a mask")
                    : Loc.T("Газ проникает через защиту", "Gas is getting through"), 3f);
            }
        }

        static GUIStyle _style;

        public static void Draw()
        {
            if (!Enabled || Time.time >= _insideUntil) return;
            try
            {
                if (_style == null)
                {
                    // No TextAnchor/FontStyle: both live in the unreferenced
                    // TextRenderingModule (see PeerCheck.DrawBadge), so the
                    // label is centred by hand below.
                    _style = new GUIStyle(GUI.skin.label);
                    _style.fontSize = 16;
                }
                float tox = Toxicity();
                string protection = _insideProtection <= 0f
                    ? Loc.T("защита держит", "sealed")
                    : Loc.T("защита " + Mathf.RoundToInt((1f - _insideProtection) * 100f) + "%",
                            "protection " + Mathf.RoundToInt((1f - _insideProtection) * 100f) + "%");
                _style.normal.textColor = _insideProtection <= 0f
                    ? new Color(0.65f, 0.95f, 0.55f, 0.95f)
                    : new Color(0.80f, 1.00f, 0.25f, 0.95f);
                string text = Loc.T("ЯДОВИТЫЙ ГАЗ", "TOXIC GAS")
                    + "   " + Mathf.RoundToInt(tox) + "/100   " + protection;
                float w = _style.CalcSize(new GUIContent(text)).x;
                GUI.Label(new Rect(Screen.width * 0.5f - w * 0.5f, Screen.height * 0.16f,
                                   w + 4f, 26f), text, _style);
            }
            catch { }
        }
    }

    /// <summary>
    /// The round in flight. The impact point is already known when it leaves
    /// the tube (the trajectory was traced in one go), so this object is only
    /// the visible part: it walks the same path at the launcher's speed with a
    /// short smoke trail behind it, and on arrival hands over to the cloud.
    /// Purely local and purely cosmetic - if it never gets there, the shot is
    /// still lost, which is why it always destroys itself on a timer too.
    /// </summary>
    public sealed class GasRound : MonoBehaviour
    {
        List<Vector3> _path;
        int _next;
        float _speed;
        Vector3 _point;
        Vector3 _normal;
        bool _burst;
        bool _done;
        float _deadline;

        internal static void Launch(List<Vector3> path, float speed,
                                    Vector3 point, Vector3 normal, bool burst)
        {
            if (path == null || path.Count < 2)
            {
                GasLauncher.Impact(point, normal, burst);
                return;
            }
            GameObject go = new GameObject("NDR Gas round");
            go.transform.position = path[0];
            GasRound r = go.AddComponent<GasRound>();
            r._path = path;
            r._next = 1;
            r._speed = Mathf.Max(5f, speed);
            r._point = point;
            r._normal = normal;
            r._burst = burst;
            r._deadline = Time.time + 20f;
            r.Trail();
        }

        void Trail()
        {
            Material mat = GasLauncher.CloudMaterial();
            if (mat == null) return;
            ParticleSystem ps = gameObject.AddComponent<ParticleSystem>();
            ps.Stop();                       // configure a stopped system, then Play
            ParticleSystemRenderer pr = gameObject.GetComponent<ParticleSystemRenderer>();
            if (pr != null)
            {
                pr.sharedMaterial = mat;
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                pr.receiveShadows = false;
            }
            ParticleSystem.MainModule main = ps.main;
            main.duration = 5f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.85f, 0.88f, 0.70f, 0.55f),
                new Color(0.70f, 0.78f, 0.55f, 0.40f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.03f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 90;

            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = true;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(45f);

            ParticleSystem.ShapeModule sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = 0.15f;
            ps.Play();
        }

        void Update()
        {
            if (_done) return;
            if (Time.time > _deadline) { Arrive(); return; }
            try
            {
                float budget = _speed * Time.deltaTime;
                while (budget > 0f && _next < _path.Count)
                {
                    Vector3 target = _path[_next];
                    Vector3 delta = target - transform.position;
                    float len = delta.magnitude;
                    if (len <= budget)
                    {
                        transform.position = target;
                        budget -= len;
                        _next++;
                    }
                    else
                    {
                        transform.position += delta * (budget / len);
                        budget = 0f;
                    }
                }
                if (_next >= _path.Count) Arrive();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gas round: " + ex);
                Arrive();
            }
        }

        void Arrive()
        {
            if (_done) return;
            _done = true;
            try { GasLauncher.Impact(_point, _normal, _burst); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Gas round impact: " + ex); }
            UnityEngine.Object.Destroy(gameObject);
        }
    }

    /// <summary>
    /// The standing cloud: the weapon itself. Two particle systems for the
    /// look, one slow tick for the effect.
    ///
    /// It grows to full density in the first fifteen seconds (the round has to
    /// evaporate), holds, and thins out over the last three minutes so that a
    /// player who watched it land can read from the outside when the ground is
    /// walkable again. Dose falls off towards the rim - the middle is the
    /// lethal part, the edge is a warning.
    ///
    /// Local to this client, like the placed mine. What it costs per tick is
    /// one distance check against the local player twice a second and one
    /// cached NPC list once a second; the list itself is shared between all
    /// standing clouds.
    /// </summary>
    public sealed class GasCloud : MonoBehaviour
    {
        const float TickPlayer = 0.5f;
        const float TickNpc = 1.0f;
        const float RampSeconds = 15f;
        const float FadeSeconds = 180f;

        float _radius = 16f;
        float _life = 1800f;
        float _born;
        float _nextPlayer;
        float _nextNpc;
        float _nextRate;
        bool _stopped;
        bool _warnedPlayer;
        bool _warnedNpc;
        ParticleSystem[] _systems = new ParticleSystem[0];
        float[] _rates = new float[0];

        internal static GasCloud Spawn(Vector3 pos, float radius, float seconds)
        {
            try
            {
                GameObject root = new GameObject("NDR Gas cloud");
                root.transform.position = pos;

                List<ParticleSystem> systems = new List<ParticleSystem>();
                List<float> rates = new List<float>();
                Material mat = GasLauncher.CloudMaterial();
                if (mat != null)
                {
                    Blanket(root, radius, mat, systems, rates);
                    Wisps(root, radius, mat, systems, rates);
                }

                GasCloud c = root.AddComponent<GasCloud>();
                c._radius = Mathf.Max(2f, radius);
                c._life = Mathf.Max(5f, seconds);
                c._born = Time.time;
                c._systems = systems.ToArray();
                c._rates = rates.ToArray();
                GasLauncher.Register(c);
                RevivalPlugin.L.LogInfo("Gas launcher: Wolke bei " + pos + ", Radius "
                    + c._radius.ToString("0.#", CultureInfo.InvariantCulture) + " m, "
                    + (c._life / 60f).ToString("0.#", CultureInfo.InvariantCulture) + " min.");
                return c;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gas cloud: " + ex);
                return null;
            }
        }

        /// <summary>The heavy part that lies on the ground and drifts.</summary>
        static void Blanket(GameObject root, float r, Material mat,
                            List<ParticleSystem> systems, List<float> rates)
        {
            ParticleSystem ps = New(root, "Gasschwaden", mat);
            ps.transform.localPosition = Vector3.up * 0.4f;

            ParticleSystem.MainModule main = ps.main;
            main.duration = 6f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(11f, 19f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.55f, r * 1.05f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.72f, 0.80f, 0.30f, 0.38f),
                new Color(0.55f, 0.72f, 0.28f, 0.30f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.004f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 130;

            ParticleSystem.ShapeModule sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = Mathf.Max(0.5f, r * 0.72f);

            // FEW AND HUGE, not many and small. A single particle is 9 to 17
            // metres across at the default radius, so seven a second fill the
            // whole cloud - and six standing clouds stay affordable. Raising
            // the rate buys nothing but overdraw.
            float rate = Mathf.Clamp(r * 0.45f, 3f, 10f);
            Rate(ps, rate);
            Fade(ps);
            Grow(ps, 0.55f, 1.35f);

            ParticleSystem.RotationOverLifetimeModule rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.12f, 0.12f);
            ps.Play();

            systems.Add(ps);
            rates.Add(rate);
        }

        /// <summary>Thin veils that rise and mark the cloud from a distance.</summary>
        static void Wisps(GameObject root, float r, Material mat,
                          List<ParticleSystem> systems, List<float> rates)
        {
            ParticleSystem ps = New(root, "Gasschleier", mat);
            ps.transform.localPosition = Vector3.up * 1.2f;

            ParticleSystem.MainModule main = ps.main;
            main.duration = 8f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(9f, 15f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.35f, r * 0.75f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.80f, 0.88f, 0.45f, 0.24f),
                new Color(0.66f, 0.78f, 0.36f, 0.18f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.02f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 70;

            ParticleSystem.ShapeModule sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.radius = Mathf.Max(0.5f, r * 0.5f);
            sh.angle = 12f;
            sh.rotation = new Vector3(-90f, 0f, 0f);

            float rate = Mathf.Clamp(r * 0.22f, 1.5f, 6f);
            Rate(ps, rate);
            Fade(ps);
            Grow(ps, 0.6f, 1.8f);
            ps.Play();

            systems.Add(ps);
            rates.Add(rate);
        }

        static ParticleSystem New(GameObject root, string name, Material mat)
        {
            GameObject go = new GameObject(name);
            go.transform.parent = root.transform;
            go.transform.localPosition = Vector3.zero;
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            // AddComponent starts it (playOnAwake). Stop first: the caller sets
            // duration and lifetime next, and those belong to a stopped system.
            ps.Stop();
            ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
            if (r != null)
            {
                // sharedMaterial: `material` would clone one copy per cloud and
                // leave it behind on Destroy.
                r.sharedMaterial = mat;
                r.renderMode = ParticleSystemRenderMode.Billboard;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            return ps;
        }

        static void Rate(ParticleSystem ps, float perSecond)
        {
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = true;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(perSecond);
        }

        static void Fade(ParticleSystem ps)
        {
            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(0.78f, 0.86f, 0.36f), 0.00f),
                    new GradientColorKey(new Color(0.62f, 0.76f, 0.30f), 0.45f),
                    new GradientColorKey(new Color(0.50f, 0.62f, 0.26f), 1.00f) },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(0.00f, 0.00f),
                    new GradientAlphaKey(1.00f, 0.12f),
                    new GradientAlphaKey(0.85f, 0.70f),
                    new GradientAlphaKey(0.00f, 1.00f) });
            col.color = new ParticleSystem.MinMaxGradient(g);
        }

        static void Grow(ParticleSystem ps, float start, float end)
        {
            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            AnimationCurve curve = new AnimationCurve();
            curve.AddKey(0.00f, start);
            curve.AddKey(0.35f, Mathf.Max(start, end) * 0.9f);
            curve.AddKey(1.00f, end);
            size.size = new ParticleSystem.MinMaxCurve(1f, curve);
        }

        /// <summary>
        /// 0 while the round is still evaporating, 1 at full density, back
        /// towards 0 in the last three minutes.
        /// </summary>
        float Strength(float age)
        {
            float up = Mathf.Clamp01(age / RampSeconds);
            float left = _life - age;
            float down = left >= FadeSeconds ? 1f : Mathf.Clamp01(left / FadeSeconds);
            return Mathf.Clamp01(up * Mathf.Max(0.15f, down));
        }

        void Update()
        {
            if (_stopped) return;
            float age = Time.time - _born;
            if (age >= _life) { Stop(); return; }

            float strength = Strength(age);

            if (Time.time >= _nextRate)
            {
                _nextRate = Time.time + 3f;
                for (int i = 0; i < _systems.Length; i++)
                {
                    if (_systems[i] == null) continue;
                    ParticleSystem.EmissionModule em = _systems[i].emission;
                    em.rateOverTime = new ParticleSystem.MinMaxCurve(_rates[i] * strength);
                }
            }

            // The two tick bodies are caught separately, and each says its
            // trouble once: this runs twice a second for half an hour.
            if (Time.time >= _nextPlayer)
            {
                _nextPlayer = Time.time + TickPlayer;
                try { Player(TickPlayer, strength); }
                catch (Exception ex)
                {
                    if (!_warnedPlayer)
                    {
                        _warnedPlayer = true;
                        RevivalPlugin.L.LogWarning("Gas cloud (player): " + ex.Message);
                    }
                }
            }

            if (Time.time >= _nextNpc)
            {
                _nextNpc = Time.time + TickNpc;
                try { Npcs(TickNpc, strength); }
                catch (Exception ex)
                {
                    if (!_warnedNpc)
                    {
                        _warnedNpc = true;
                        RevivalPlugin.L.LogWarning("Gas cloud (npc): " + ex.Message);
                    }
                }
            }
        }

        void Player(float dt, float strength)
        {
            GameObject player = MapTools.LocalPlayer();
            if (player == null) return;
            Vector3 head = player.transform.position + Vector3.up * 1.2f;
            float d = Vector3.Distance(head, transform.position);
            if (d > _radius) return;

            float dose = strength * Mathf.Clamp01(1f - 0.55f * (d / _radius));
            float protection = GasLauncher.Protection();
            GasLauncher.ReportInside(dose, protection);
            if (protection <= 0f || dose <= 0f) return;

            GasLauncher.AddToxicity(GasLauncher.ToxicityPerSecond() * dose * protection * dt);
            if (GasLauncher.Toxicity() >= GasLauncher.DamageAbove())
                GasLauncher.HurtPlayer(GasLauncher.HealthPerSecond() * dose * protection * dt);
        }

        void Npcs(float dt, float strength)
        {
            if (!GasLauncher.NpcDamage()) return;
            float perSecond = GasLauncher.NpcDamagePerSecond();
            if (perSecond <= 0f || strength <= 0f) return;

            Component[] npcs = GasLauncher.Npcs();
            Vector3 me = transform.position;
            float r2 = _radius * _radius;
            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs[i];
                if (ai == null) continue;
                Vector3 p = ai.transform.position + Vector3.up * 1.0f;
                float sq = (p - me).sqrMagnitude;
                if (sq > r2) continue;
                float dose = strength * Mathf.Clamp01(1f - 0.55f * (Mathf.Sqrt(sq) / _radius));
                if (dose <= 0f) continue;
                GasLauncher.HurtNpc(ai, perSecond * dose * dt);
            }
        }

        /// <summary>
        /// Time is up: stop emitting and let what is in the air run out before
        /// the object goes. Nothing takes damage from here on.
        /// </summary>
        void Stop()
        {
            _stopped = true;
            for (int i = 0; i < _systems.Length; i++)
            {
                if (_systems[i] == null) continue;
                ParticleSystem.EmissionModule em = _systems[i].emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
            }
            RevivalPlugin.L.LogInfo("Gas launcher: Wolke bei " + transform.position
                + " ist abgezogen.");
            UnityEngine.Object.Destroy(gameObject, 22f);
        }
    }

    /// <summary>
    /// Postfix on PlayerGrenadeWeaponController::CantThrowGrenade. For our tube
    /// it answers "you cannot throw this" and fires the launcher instead, so
    /// every original UI and state restriction of the throw path still decides
    /// whether anything happens at all - the mine's proven seam (it is called
    /// only from ThrowGrenade, IL-confirmed). Ordinary grenades and the mine
    /// pass through untouched.
    /// </summary>
    public static class GasThrowHook
    {
        static FieldInfo _fData;

        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerGrenadeWeaponController");
                if (t == null)
                {
                    RevivalPlugin.L.LogWarning("Gas launcher: PlayerGrenadeWeaponController fehlt.");
                    return;
                }
                MethodInfo m = AccessTools.Method(t, "CantThrowGrenade", null, null);
                if (m == null || m.ReturnType != typeof(bool))
                {
                    RevivalPlugin.L.LogWarning("Gas launcher: CantThrowGrenade fehlt - "
                        + "Linksklick-Schuss inaktiv.");
                    return;
                }
                harmony.Patch(m, null,
                    new HarmonyMethod(typeof(GasThrowHook).GetMethod("Postfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("Gas launcher: Linksklick-Schuss aktiv.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gas launcher: CantThrowGrenade-Patch: " + ex);
            }
        }

        public static void Postfix(object __instance, ref bool __result)
        {
            try
            {
                if (__result) return;              // the game already says no
                if (__instance == null) return;
                if (_fData == null)
                    _fData = AccessTools.Field(__instance.GetType(), "_weaponGrenadeData");
                if (_fData == null) return;
                object data = _fData.GetValue(__instance);
                if (data == null) return;
                FieldInfo fId = AccessTools.Field(data.GetType(), "ItemID");
                if (fId == null) return;
                if (GasLauncher.ToInt(fId.GetValue(data)) != GasLauncher.LauncherId) return;

                // Block first, fire second: a failure must never let the throw
                // coroutine run with our tube in hand.
                __result = true;
                GasLauncher.FireFromController(__instance as Component);
            }
            catch { }
        }
    }

    /// <summary>
    /// The grenade record for our id. `xmlItemsDataManager::GetGrenadeWeaponData`
    /// only knows what the server sent, and no server sends 1491 - without this
    /// the tube cannot be equipped at all. The frag grenade 1403 is cloned
    /// field by field and only its ItemID is replaced; the donor entry itself
    /// is never touched or relabelled. The prefix runs on every lookup, so a
    /// server XML reload cannot take the record away again.
    /// </summary>
    public static class GasGrenadeDataHook
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
                    RevivalPlugin.L.LogWarning("Gas launcher: GetGrenadeWeaponData(int) fehlt - "
                        + "die Rohrwaffe laesst sich nicht ausruesten.");
                    return;
                }
                harmony.Patch(get, new HarmonyMethod(typeof(GasGrenadeDataHook)
                    .GetMethod("Prefix")), null, null, null, null);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gas launcher: GetGrenadeWeaponData-Patch: " + ex);
            }
        }

        static bool _warned;

        public static void Prefix(object __instance, int __0)
        {
            try
            {
                if (__0 != GasLauncher.LauncherId || __instance == null) return;
                FieldInfo f = AccessTools.Field(__instance.GetType(), "WeaponsGrenadeData");
                System.Collections.IDictionary entries = f == null ? null
                    : f.GetValue(__instance) as System.Collections.IDictionary;
                if (entries == null) return;
                object existing = entries.Contains(__0) ? entries[__0] : null;
                if (existing != null)
                {
                    FieldInfo id = AccessTools.Field(existing.GetType(), "ItemID");
                    if (id != null && GasLauncher.ToInt(id.GetValue(existing)) == __0) return;
                }
                object donor = entries.Contains(1403) ? entries[1403] : null;
                if (donor == null) return;
                object copy = Activator.CreateInstance(donor.GetType());
                FieldInfo[] fields = donor.GetType().GetFields(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                    fields[i].SetValue(copy, fields[i].GetValue(donor));
                FieldInfo ownId = AccessTools.Field(copy.GetType(), "ItemID");
                if (ownId == null) return;
                object value = __0;
                if (ownId.FieldType != typeof(int))
                {
                    MethodInfo convert = ownId.FieldType.GetMethod("op_Implicit",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int) }, null);
                    if (convert == null) return;
                    value = convert.Invoke(null, new object[] { __0 });
                }
                ownId.SetValue(copy, value);
                entries[__0] = copy;
                RevivalPlugin.L.LogInfo("Gas launcher: grenade equipment data registered for " + __0);
            }
            catch (Exception ex)
            {
                // The lookup runs on every equip check, so this must never
                // become a log every frame: say it once.
                if (_warned) return;
                _warned = true;
                RevivalPlugin.L.LogWarning("Gas launcher: Granatendaten: " + ex.Message);
            }
        }
    }
}
