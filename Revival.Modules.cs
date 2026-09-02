// Next Day: Survival - Revival Toolkit
// Vehicle modules: lootable hardware pulled from a destroyed patrol APC/tank
// and INSTALLED into a vehicle the player mans. Three kinds:
//
//   Thermal module     - the gunner periscope gains a thermal mode
//   Night-vision module - the gunner periscope gains a night mode
//   Large jamming module - a vehicle-mounted jammer, stronger than the carried
//                          one, that detonates drones that close in
//
// WHY A SEPARATE FILE. RevivalPlugin.cs is one 18k-line file. Keeping this
// feature in its own file (its own top-level classes, no partial of the giant
// class) means several agents can work in parallel and the integration agent
// merges by concatenation instead of by untangling one file - see AGENTS.md,
// the same-file exception. The ONLY edits this feature makes to RevivalPlugin.cs
// are a handful of clearly marked one-line calls (BindConfig, RegisterItems,
// Tick) plus tiny internal accessors on Turret; everything else lives here and
// in Revival.GunnerOptics.cs.
//
// ASCII-only in code and comments; the player-facing item names/descriptions
// are bilingual Russian/English through Loc.T, exactly as the other items are.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>Which optic the gunner periscope is showing.</summary>
    internal enum VisionMode { Normal = 0, Night = 1, Thermal = 2 }

    /// <summary>
    /// The looted-and-installed vehicle modules. Installation is LOCAL and keyed
    /// on the vehicle root transform: the periscope modes are something only the
    /// player sitting in that gunner seat sees, so there is nothing to
    /// synchronise for them. The jammer effect reuses the existing carried-jammer
    /// machinery (Jammer), which already runs on the local client.
    /// </summary>
    internal static class VehicleModules
    {
        // Default item ids. The block 2060..2062 sits next to the portable
        // jammer (2054) and is not otherwise used (grep of the item table). All
        // three clone donor 2030, the same carryable non-weapon the drone and
        // the portable jammer clone from.
        const int DEF_DONOR   = 2030;
        const int DEF_THERMAL = 2060;
        const int DEF_NIGHT   = 2061;
        const int DEF_JAMMER  = 2062;

        internal static ConfigEntry<bool>    CfgEnabled;
        internal static ConfigEntry<bool>    CfgPeriscope;
        internal static ConfigEntry<int>     CfgThermalId;
        internal static ConfigEntry<int>     CfgNightId;
        internal static ConfigEntry<int>     CfgJammerId;
        internal static ConfigEntry<KeyCode> CfgInstallKey;
        internal static ConfigEntry<KeyCode> CfgVisionKey;
        internal static ConfigEntry<float>   CfgJammerDetonate;
        internal static ConfigEntry<int>     CfgTrunkMin;
        internal static ConfigEntry<int>     CfgTrunkMax;
        internal static ConfigEntry<float>   CfgWreckBonus;

        internal static int ThermalId { get { return CfgThermalId != null ? CfgThermalId.Value : DEF_THERMAL; } }
        internal static int NightId   { get { return CfgNightId   != null ? CfgNightId.Value   : DEF_NIGHT; } }
        internal static int JammerId  { get { return CfgJammerId  != null ? CfgJammerId.Value  : DEF_JAMMER; } }
        internal static bool Enabled  { get { return CfgEnabled == null || CfgEnabled.Value; } }

        /// <summary>What is bolted into one vehicle. All three are independent.</summary>
        internal sealed class Slot
        {
            internal bool Thermal;
            internal bool Night;
            internal bool Jammer;
            internal bool AnyVision { get { return Thermal || Night; } }
            internal bool Any       { get { return Thermal || Night || Jammer; } }
        }

        static readonly Dictionary<Transform, Slot> _installed =
            new Dictionary<Transform, Slot>();

        // The gunner's currently selected mode. Kept per session, clamped to
        // what is actually installed every time it is read.
        static VisionMode _mode = VisionMode.Normal;

        // ------------------------------------------------------------ Config

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("VehicleModules", "Enabled", true,
                "Fahrzeugmodule aus Patrouillen-Kofferraeumen (Thermal, Nachtsicht, "
                + "grosser Stoersender). Aus: die Items existieren nicht und der "
                + "Kofferraum-Loot entfaellt.");
            CfgPeriscope = cfg.Bind("VehicleModules", "Periscope", true,
                "Ersetzt im bemannten Fahrzeug die alte runde Zieloptik durch ein "
                + "modernes, weites Periskop. Aus: das Spiel bleibt bei der alten "
                + "Optik (t72_scope/apc_scope), Thermal/Nachtsicht sind dann ohne "
                + "Wirkung.");
            CfgThermalId = cfg.Bind("VehicleModules", "ThermalItemId", DEF_THERMAL,
                "Item-Id des Thermalmoduls.");
            CfgNightId = cfg.Bind("VehicleModules", "NightVisionItemId", DEF_NIGHT,
                "Item-Id des Nachtsichtmoduls.");
            CfgJammerId = cfg.Bind("VehicleModules", "JammerItemId", DEF_JAMMER,
                "Item-Id des grossen Stoersendermoduls.");
            CfgInstallKey = cfg.Bind("VehicleModules", "InstallKey", KeyCode.G,
                "Taste, um ein getragenes Modul in das bemannte Fahrzeug einzubauen. "
                + "Mit gehaltenem Shift: Modul wieder ausbauen und in den Rucksack "
                + "zurueckgeben.");
            CfgVisionKey = cfg.Bind("VehicleModules", "VisionKey", KeyCode.N,
                "Taste im Geschuetz, die zwischen Normal, Nachtsicht und Thermal "
                + "umschaltet - nur die Modi, deren Modul eingebaut ist.");
            CfgJammerDetonate = cfg.Bind("VehicleModules", "JammerDetonateRange", 100f,
                "Reichweite des eingebauten Stoersenders in Metern: eine fremde Drohne, "
                + "die dem Fahrzeug bis auf diese Distanz naeher kommt, wird gesprengt. "
                + "Deutlich groesser als der tragbare Stoersender (50 m); weiter draussen "
                + "wird das Bild wie beim tragbaren zunehmend gestoert.");
            CfgTrunkMin = cfg.Bind("VehicleModules", "TrunkMin", 1,
                "Mindestzahl Module im Kofferraum einer Patrouille (APC/Panzer).");
            CfgTrunkMax = cfg.Bind("VehicleModules", "TrunkMax", 3,
                "Hoechstzahl Module im Kofferraum einer Patrouille.");
            CfgWreckBonus = cfg.Bind("VehicleModules", "WreckLootSeconds", 300f,
                "Zusatzzeit, die ein zerstoertes Patrouillenfahrzeug mit Modul-Loot "
                + "im Kofferraum stehen bleibt, damit der Loot geborgen werden kann. "
                + "Wirkt ADDITIV auf Patrol/WreckSeconds und wird vom Integrations-"
                + "Agenten mit der Wrack-Brand-Logik abgestimmt (bekannte "
                + "Ueberschneidung).");
        }

        // --------------------------------------------------------- Item table

        /// <summary>
        /// Adds the three module items to the shared item table. Called once from
        /// Awake after BuildItemTable(). Placeholder art for now: the three reuse
        /// the portable jammer's mesh/textures/icon so the build and the runtime
        /// self-test are green; a Codex asset job produces distinct meshes,
        /// textures and icons later (that is a generator/asset task, not ours).
        /// </summary>
        internal static void RegisterItems()
        {
            if (!Enabled) return;

            RevivalPlugin.Items.Add(new ItemDef(
                ThermalId, DEF_DONOR, false,
                "Тепловизионный модуль",
                "Thermal imaging module",
                "Тепловизор с башни бронемашины: матрица в бронекоробе, разъём "
                + "питания, кабель к прицелу. Установленный в машину, он даёт "
                + "наводчику перекючаемый тепловой режим в новом перископе - тёплые "
                + "тела светятся сквозь дым и темноту. 18 кг: тяжело, но влезает в "
                + "рюкзак.",
                "A vehicle-turret thermal imager: the sensor in an armoured box, a "
                + "power connector, a cable to the sight. Installed into a vehicle it "
                + "gives the gunner a toggleable thermal mode in the new periscope - "
                + "warm bodies glow through smoke and darkness. 18 kg: heavy, but it "
                + "fits in a backpack.",
                "jammer.ndmesh", "jammer_diffuse.png", "jammer_normal.png",
                "jammer_icon.png", null,
                1, 0, 18.0f));

            RevivalPlugin.Items.Add(new ItemDef(
                NightId, DEF_DONOR, false,
                "Модуль ночного видения",
                "Night-vision module",
                "Низкоуровневый усилитель яркости для башенного перископа: труба ЭОП, "
                + "блок питания, крепление. Установленный в машину, он даёт наводчику "
                + "переключаемый ночной режим - зелёная картина, видно в темноте. "
                + "14 кг, переносится в рюкзаке.",
                "A low-light image intensifier for the turret periscope: the tube, a "
                + "power pack, a mount. Installed into a vehicle it gives the gunner a "
                + "toggleable night mode - a green picture that sees in the dark. "
                + "14 kg, carried in a backpack.",
                "jammer.ndmesh", "jammer_diffuse.png", "jammer_normal.png",
                "jammer_icon.png", null,
                1, 0, 14.0f));

            RevivalPlugin.Items.Add(new ItemDef(
                JammerId, DEF_DONOR, false,
                "Модуль РЭБ (большой)",
                "Large jamming module",
                "Башенный комплекс РЭБ: мощный передатчик, антенное поле, охлаждение. "
                + "Установленный в машину, он глушит дроны на большем радиусе, чем "
                + "носимый, а подлетевший слишком близко дрон подрывает на месте. "
                + "34 кг - тяжелее носимого, но всё ещё в рюкзаке.",
                "A turret electronic-warfare set: a high-power transmitter, an antenna "
                + "array, cooling. Installed into a vehicle it jams drones over a "
                + "larger radius than the carried one, and detonates any drone that "
                + "closes in. 34 kg - heavier than the portable, but still backpack-"
                + "portable.",
                "jammer.ndmesh", "jammer_diffuse.png", "jammer_normal.png",
                "jammer_icon.png", null,
                1, 0, 34.0f));
        }

        // ------------------------------------------------------------- Runtime

        /// <summary>Called every frame from RevivalPlugin.Update, after Turret.Tick.</summary>
        internal static void Tick()
        {
            if (!Enabled) return;
            try
            {
                Transform veh = Turret.MannedVehicle;
                if (veh == null) return;

                if (Input.GetKeyDown(CfgInstallKey.Value))
                {
                    if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                        TryUninstall(veh);
                    else
                        TryInstall(veh);
                }

                if (Input.GetKeyDown(CfgVisionKey.Value))
                    CycleVision(veh);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("VehicleModules.Tick: " + ex);
            }
        }

        static void TryInstall(Transform veh)
        {
            Slot s = GetOrAdd(veh);
            // Priority order if the player carries several: thermal, night, jammer.
            if (!s.Thermal && Turret.HasItem(ThermalId) && Turret.TakeItem(ThermalId, "Thermalmodul"))
            { s.Thermal = true; Turret.Hinweis(Loc.T("Тепловизор установлен", "Thermal module installed"), 2.5f); return; }
            if (!s.Night && Turret.HasItem(NightId) && Turret.TakeItem(NightId, "Nachtsicht"))
            { s.Night = true; Turret.Hinweis(Loc.T("Ночной модуль установлен", "Night-vision module installed"), 2.5f); return; }
            if (!s.Jammer && Turret.HasItem(JammerId) && Turret.TakeItem(JammerId, "Stoersendermodul"))
            { s.Jammer = true; Turret.Hinweis(Loc.T("Модуль РЭБ установлен", "Jamming module installed"), 2.5f); return; }

            Turret.Hinweis(Loc.T("Нет модуля для установки", "No module to install"), 2.0f);
        }

        static void TryUninstall(Transform veh)
        {
            Slot s = Get(veh);
            if (s == null || !s.Any)
            { Turret.Hinweis(Loc.T("Нечего снимать", "Nothing installed"), 2.0f); return; }
            // Give one back, most-recently-useful first.
            if (s.Jammer)  { s.Jammer  = false; GiveBack(JammerId);  Turret.Hinweis(Loc.T("Модуль РЭБ снят", "Jamming module removed"), 2.5f); return; }
            if (s.Thermal) { s.Thermal = false; GiveBack(ThermalId); Turret.Hinweis(Loc.T("Тепловизор снят", "Thermal module removed"), 2.5f); return; }
            if (s.Night)   { s.Night   = false; GiveBack(NightId);   Turret.Hinweis(Loc.T("Ночной модуль снят", "Night-vision module removed"), 2.5f); return; }
        }

        static void GiveBack(int itemId)
        {
            // The item is put back through the game's own give path so it shows up
            // in the backpack. Delegated to Turret, which already owns the
            // inventory reflection; a no-op fallback keeps this from throwing if
            // the give method is missing on some build.
            try { Turret.GiveItem(itemId, 1); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Modul zurueckgeben: " + ex); }
        }

        static void CycleVision(Transform veh)
        {
            Slot s = Get(veh);
            if (s == null || !s.AnyVision)
            { Turret.Hinweis(Loc.T("Нет оптического модуля", "No optics module installed"), 2.0f); return; }

            // Build the cycle out of Normal + whatever is installed.
            List<VisionMode> ring = new List<VisionMode>();
            ring.Add(VisionMode.Normal);
            if (s.Night) ring.Add(VisionMode.Night);
            if (s.Thermal) ring.Add(VisionMode.Thermal);

            int idx = ring.IndexOf(_mode);
            if (idx < 0) idx = 0;
            _mode = ring[(idx + 1) % ring.Count];

            string label = _mode == VisionMode.Thermal
                ? Loc.T("Тепловизор", "Thermal")
                : _mode == VisionMode.Night
                    ? Loc.T("Ночное видение", "Night vision")
                    : Loc.T("Обычный", "Normal");
            Turret.Hinweis(label, 1.5f);
        }

        // ------------------------------------------------------- Trunk loot

        /// <summary>
        /// Puts TrunkMin..TrunkMax module items into a freshly armed patrol
        /// vehicle's trunk. Called once per vehicle from Patrol.Arm. Runs on the
        /// master client (the patrol owner); the trunk is a networked
        /// ItemsContainer, so a second player may need the container to sync -
        /// noted as an acceptance point.
        /// </summary>
        internal static void StockTrunk(Transform veh, bool tank)
        {
            if (!Enabled || veh == null) return;
            try
            {
                object data = Turret.TrunkDataOf(veh);
                if (data == null)
                {
                    RevivalPlugin.L.LogWarning("VehicleModules: kein Kofferraum zum Bestuecken gefunden.");
                    return;
                }

                int min = Mathf.Clamp(CfgTrunkMin.Value, 0, 6);
                int max = Mathf.Clamp(CfgTrunkMax.Value, min, 6);
                int n = UnityEngine.Random.Range(min, max + 1);

                int[] pool = new int[] { ThermalId, NightId, JammerId };
                int placed = 0;
                for (int k = 0; k < n; k++)
                {
                    int id = pool[UnityEngine.Random.Range(0, pool.Length)];
                    if (Turret.AddToContainer(data, id, 1)) placed++;
                    else break; // trunk full
                }
                RevivalPlugin.L.LogInfo("VehicleModules: " + placed + " Modul(e) in den "
                    + (tank ? "Panzer" : "APC") + "-Kofferraum gelegt.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("VehicleModules.StockTrunk: " + ex);
            }
        }

        /// <summary>
        /// Extra despawn seconds for a wreck whose trunk still holds any module,
        /// so the loot can be recovered. Zero once the trunk is emptied.
        /// </summary>
        internal static float WreckBonus(Transform veh)
        {
            if (!Enabled || veh == null || CfgWreckBonus == null) return 0f;
            try
            {
                object data = Turret.TrunkDataOf(veh);
                if (data == null) return 0f;
                if (Turret.CountInContainer(data, ThermalId) > 0
                    || Turret.CountInContainer(data, NightId) > 0
                    || Turret.CountInContainer(data, JammerId) > 0)
                    return Mathf.Max(0f, CfgWreckBonus.Value);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("VehicleModules.WreckBonus: " + ex);
            }
            return 0f;
        }

        // ------------------------------------------------------------- Queries

        internal static Slot Get(Transform veh)
        {
            if (veh == null) return null;
            Slot s;
            return _installed.TryGetValue(veh, out s) ? s : null;
        }

        static Slot GetOrAdd(Transform veh)
        {
            Slot s = Get(veh);
            if (s == null) { s = new Slot(); _installed[veh] = s; }
            return s;
        }

        /// <summary>The vision mode the gunner should actually see, clamped to
        /// installed hardware. Read by Revival.GunnerOptics.</summary>
        internal static VisionMode CurrentMode(Transform veh)
        {
            Slot s = Get(veh);
            if (s == null) return VisionMode.Normal;
            if (_mode == VisionMode.Thermal && s.Thermal) return VisionMode.Thermal;
            if (_mode == VisionMode.Night && s.Night) return VisionMode.Night;
            return VisionMode.Normal;
        }

        internal static bool HasJammer(Transform veh)
        {
            Slot s = Get(veh);
            return s != null && s.Jammer;
        }

        /// <summary>
        /// Is the local player manning a vehicle with an installed jamming
        /// module, and if so at what detonation radius? Read by the Jammer class
        /// so the vehicle jammer reuses the existing broadcast/detonation path
        /// with a larger radius. (v1 ties the source to the gunner seat; a
        /// driver/passenger jammer is a possible extension.)
        /// </summary>
        internal static bool LocalVehicleJammer(out float radius)
        {
            radius = 0f;
            if (!Enabled) return false;
            Transform veh = Turret.MannedVehicle;
            if (veh == null || !HasJammer(veh)) return false;
            radius = CfgJammerDetonate != null ? Mathf.Max(1f, CfgJammerDetonate.Value) : 100f;
            return true;
        }

        /// <summary>
        /// Drop stale entries: destroyed vehicles leave a null key behind.
        /// Cheap enough to run occasionally; called from GunnerOptics on a timer.
        /// </summary>
        internal static void Sweep()
        {
            if (_installed.Count == 0) return;
            List<Transform> dead = null;
            foreach (KeyValuePair<Transform, Slot> kv in _installed)
                if (kv.Key == null)
                {
                    if (dead == null) dead = new List<Transform>();
                    dead.Add(kv.Key);
                }
            if (dead != null)
                for (int i = 0; i < dead.Count; i++) _installed.Remove(dead[i]);
        }
    }
}
