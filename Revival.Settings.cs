// Next Day: Survival - Revival Toolkit
//
// SETTINGS: the player-facing switches that cut across every module, in one
// place (queue task P9, docs/ai/tasks/settings-cleanup.md).
//
//   [Effects] ParticleDensity   Off / Low / Medium / High - one level for all
//                               fire, smoke, explosions, flak bursts, gas
//                               clouds and muzzle flashes the mod spawns (Fx).
//   [Effects] Animations        master switch for every custom animation and
//                               effect; the keys below switch single ones (Anim).
//   [Hints]   Mode              Auto / Always / Off - on-screen help (Hints).
//   [Settings] MenuKey          the in-game settings window (SettingsMenu).
//
// A module that spawns particles calls Fx.Apply(ps) after configuring the
// system (or Fx.Count / Fx.Keep for hand-emitted particles); an animation
// asks Anim.Recoil, Anim.Radar, Anim.Tracers ...; a help text draws only with
// Hints.Alpha(id, state) > 0. Nothing here changes damage, aim or any other
// gameplay value - only what is drawn. One exception, a loadout choice: the
// An-2 bomb load (Y B4, [An2Bombs] BombLoad, 8 x FAB-100 or 12 x FAB-50).
//
// Migration: every key is new, read with a tolerant parser (any case, unknown
// text falls back to the default without rewriting the file), so an existing
// nextday.revival.toolkit.cfg keeps every value it already has.
// [Settings] Version records the settings layout the file was last written by.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>One particle density for everything the mod emits.</summary>
    internal static class Fx
    {
        static ConfigEntry<string> _density;

        internal static void BindConfig(ConfigFile cfg)
        {
            _density = cfg.Bind("Effects", "ParticleDensity", "High",
                "One particle level for every effect the mod spawns: fire and smoke "
                + "on explosions and wrecks, flak and Gepard bursts, impact puffs, "
                + "gas clouds, the crocodile's fumes and muzzle flashes. "
                + "Off = none at all, Low = about a quarter, Medium = about half, "
                + "High = full (the look before this setting existed). Takes effect "
                + "for every effect spawned after the change. Values: Off, Low, Medium, High.");
        }

        /// <summary>0 Off, 1 Low, 2 Medium, 3 High.</summary>
        internal static int Level
        {
            get { return ParseLevel(_density == null ? null : _density.Value); }
        }

        internal static readonly string[] Names = { "Off", "Low", "Medium", "High" };

        internal static int ParseLevel(string s)
        {
            if (string.IsNullOrEmpty(s)) return 3;
            string t = s.Trim().ToLowerInvariant();
            if (t == "off" || t == "none" || t == "0" || t == "false") return 0;
            if (t == "low" || t == "1") return 1;
            if (t == "medium" || t == "mid" || t == "2") return 2;
            return 3;
        }

        internal static void SetLevel(int level)
        {
            if (_density == null) return;
            _density.Value = Names[Mathf.Clamp(level, 0, 3)];
        }

        /// <summary>The share of particles kept: 0, 0.25, 0.55 or 1.</summary>
        internal static float Factor
        {
            get
            {
                switch (Level)
                {
                    case 0: return 0f;
                    case 1: return 0.25f;
                    case 2: return 0.55f;
                    default: return 1f;
                }
            }
        }

        /// <summary>False at Off: nothing particle-like should be spawned.</summary>
        internal static bool On { get { return Level > 0; } }

        /// <summary>A particle count under the level; at least one while on.</summary>
        internal static int Count(int n)
        {
            if (n <= 0) return 0;
            float f = Factor;
            if (f <= 0f) return 0;
            if (f >= 1f) return n;
            return Mathf.Max(1, Mathf.RoundToInt(n * f));
        }

        /// <summary>For particles emitted one at a time: keep this one?</summary>
        internal static bool Keep()
        {
            float f = Factor;
            if (f >= 1f) return true;
            if (f <= 0f) return false;
            return UnityEngine.Random.value < f;
        }

        /// <summary>
        /// Scales a configured system to the level: max particles, the emission
        /// rates and every burst. At Off the emission is switched off and the
        /// system stopped. Call it after the system is set up, before or right
        /// after Play() (bursts at time 0 fire on the next update).
        /// </summary>
        internal static void Apply(ParticleSystem ps)
        {
            Scale(ps, Factor);
        }

        static void Scale(ParticleSystem ps, float f)
        {
            if (ps == null) return;
            if (f >= 1f) return;
            try
            {
                ParticleSystem.EmissionModule em = ps.emission;
                if (f <= 0f)
                {
                    em.enabled = false;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    return;
                }
                ParticleSystem.MainModule main = ps.main;
                main.maxParticles = Mathf.Max(1, Mathf.RoundToInt(main.maxParticles * f));
                em.rateOverTimeMultiplier = em.rateOverTimeMultiplier * f;
                em.rateOverDistanceMultiplier = em.rateOverDistanceMultiplier * f;
                int n = em.burstCount;
                if (n > 0)
                {
                    ParticleSystem.Burst[] bursts = new ParticleSystem.Burst[n];
                    em.GetBursts(bursts);
                    for (int i = 0; i < n; i++)
                    {
                        bursts[i].minCount = (short)Mathf.Max(1, Mathf.RoundToInt(bursts[i].minCount * f));
                        bursts[i].maxCount = (short)Mathf.Max(1, Mathf.RoundToInt(bursts[i].maxCount * f));
                    }
                    em.SetBursts(bursts);
                }
            }
            catch (Exception ex) { Settings.Warn("Fx.Apply: " + ex.Message); }
        }

        /// <summary>
        /// For particles that MARK A DANGER (a toxic gas cloud, the crocodile's
        /// fumes): scaled like the rest, but never below Low, so a harmful zone
        /// never turns invisible because effects are off.
        /// </summary>
        internal static float VitalFactor { get { return Mathf.Max(0.25f, Factor); } }

        internal static void ApplyVital(ParticleSystem ps)
        {
            if (Level == 0) Scale(ps, 0.25f);
            else Apply(ps);
        }

        /// <summary>Apply to a system and all its children.</summary>
        internal static void ApplyAll(GameObject root)
        {
            if (root == null || Factor >= 1f) return;
            ParticleSystem[] all = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < all.Length; i++) Apply(all[i]);
        }
    }

    /// <summary>On/off switches for the custom animations and effects.</summary>
    internal static class Anim
    {
        static ConfigEntry<bool> _all, _recoil, _radar, _tracers, _muzzle, _lights, _propeller;

        internal static void BindConfig(ConfigFile cfg)
        {
            _all = cfg.Bind("Effects", "Animations", true,
                "Master switch for every custom animation and effect below. Off = "
                + "all of them off at once (recoil, rotating radars, tracers, muzzle "
                + "flashes, explosion lights, propeller). Only the look changes: "
                + "shots, damage, aim and radar detection work the same.");
            _recoil = cfg.Bind("Effects", "Recoil", true,
                "Barrel recoil animations: the Gepard's sliding barrels, the "
                + "howitzer's recoiling tube, later mounted guns (ZU-23, MTW). "
                + "Does NOT touch the aim climb ([Turret] Recoil, [TechnicalGun] "
                + "Recoil), which is part of the shooting.");
            _radar = cfg.Bind("Effects", "RadarRotation", true,
                "Rotating radar antennas (Gepard search radar, airfield tower "
                + "radar). Off = the dish stands still; the radar still works.");
            _tracers = cfg.Bind("Effects", "Tracers", true,
                "Tracer lines of the mod's guns: vehicle turret, technical MG, "
                + "Gepard, rocket/NPC tracers. Off = the rounds fly unseen.");
            _muzzle = cfg.Bind("Effects", "MuzzleFlashes", true,
                "Muzzle flash light and flame of the mod's guns. The flame is also "
                + "a particle and follows [Effects] ParticleDensity.");
            _lights = cfg.Bind("Effects", "ExplosionLights", true,
                "The short light flash of explosions and burning wrecks.");
            _propeller = cfg.Bind("Effects", "Propeller", true,
                "The An-2's spinning propeller.");
        }

        static bool On(ConfigEntry<bool> e)
        {
            if (_all != null && !_all.Value) return false;
            return e == null || e.Value;
        }

        internal static bool All { get { return _all == null || _all.Value; } }
        internal static bool Recoil { get { return On(_recoil); } }
        internal static bool Radar { get { return On(_radar); } }
        internal static bool Tracers { get { return On(_tracers); } }
        /// <summary>Muzzle flashes: the switch AND a particle level above Off.</summary>
        internal static bool MuzzleFlash { get { return On(_muzzle) && Fx.On; } }
        internal static bool Lights { get { return On(_lights); } }
        internal static bool Propeller { get { return On(_propeller); } }

        internal static ConfigEntry<bool>[] Entries()
        {
            return new ConfigEntry<bool>[] { _all, _recoil, _radar, _tracers, _muzzle, _lights, _propeller };
        }
    }

    /// <summary>
    /// On-screen help. Auto (default): a hint shows when it becomes relevant -
    /// first drawn, or its state changed - stays a few seconds and fades; it
    /// comes back when it disappears for a moment and becomes relevant again,
    /// or for everything on the hint key. Always: the old permanent boxes.
    /// Off: hidden; the hint key still shows them for a few seconds.
    /// </summary>
    internal static class Hints
    {
        static ConfigEntry<string> _mode, _key;
        static ConfigEntry<float> _seconds;
        static ConfigEntry<bool> _prompts;
        static KeyCode _keyCode = KeyCode.None;
        static string _keyName;
        static float _recall = -100f;
        const float Fade = 1.2f;
        const float Gone = 2f;

        sealed class State { public string Sig; public float Since, Seen; }
        static readonly Dictionary<string, State> _states = new Dictionary<string, State>();

        internal static readonly string[] Names = { "Off", "Auto", "Always" };

        internal static void BindConfig(ConfigFile cfg)
        {
            _mode = cfg.Bind("Hints", "Mode", "Auto",
                "On-screen help of the mod: the DRONE launch box, the NPC/zone "
                + "label of the east world, key help lines in gun sights, the F4 "
                + "line on the map. Auto = show a hint when it becomes relevant for "
                + "[Hints] Seconds, then fade; Always = keep them on screen (the "
                + "behaviour before this setting); Off = hidden, only the hint key "
                + "shows them. Values: Off, Auto, Always.");
            _seconds = cfg.Bind("Hints", "Seconds", 6f,
                "How long a hint stays up in Auto mode (and after the hint key) "
                + "before it fades, in seconds.");
            _key = cfg.Bind("Hints", "Key", "F1",
                "Shows every hint that applies right now again for [Hints] Seconds. "
                + "None = no key.");
            _prompts = cfg.Bind("Hints", "InteractionPrompts", true,
                "The key prompts beside objects (\"[G] repair\", mortar, An-2 "
                + "repair). They appear only in reach anyway, so Auto keeps them. "
                + "False, or [Hints] Mode = Off, hides them too (hint key shows them).");
        }

        /// <summary>0 Off, 1 Auto, 2 Always.</summary>
        internal static int Mode
        {
            get
            {
                string s = _mode == null ? null : _mode.Value;
                if (string.IsNullOrEmpty(s)) return 1;
                string t = s.Trim().ToLowerInvariant();
                if (t == "off" || t == "none" || t == "false" || t == "hidden") return 0;
                if (t == "always" || t == "on" || t == "true" || t == "permanent") return 2;
                return 1;
            }
        }

        internal static void SetMode(int mode)
        {
            if (_mode != null) _mode.Value = Names[Mathf.Clamp(mode, 0, 2)];
        }

        internal static float Seconds
        {
            get { return _seconds == null ? 6f : Mathf.Clamp(_seconds.Value, 1f, 120f); }
        }

        internal static string KeyLabel
        {
            get { Parse(); return _keyCode == KeyCode.None ? "-" : _keyCode.ToString(); }
        }

        static void Parse()
        {
            string name = _key == null ? "F1" : _key.Value;
            if (name == _keyName) return;
            _keyName = name;
            _keyCode = Settings.ParseKey(name, KeyCode.None);
        }

        internal static void Tick()
        {
            Parse();
            if (_keyCode != KeyCode.None && Input.GetKeyDown(_keyCode)) Recall();
        }

        /// <summary>Shows every current hint again, as if it had just appeared.</summary>
        internal static void Recall() { _recall = Time.realtimeSinceStartup; }

        static float Curve(float age)
        {
            float s = Seconds;
            if (age < 0f) return 0f;
            if (age <= s) return 1f;
            if (age >= s + Fade) return 0f;
            return 1f - (age - s) / Fade;
        }

        /// <summary>
        /// The opacity (0..1) of the hint `id` this frame. `state` describes what
        /// the hint currently says in a way that changes only when something
        /// relevant changes (not with a running distance): a new state shows it
        /// again. Call it every OnGUI pass the hint would be drawn; 0 = skip.
        /// </summary>
        internal static float Alpha(string id, string state)
        {
            float now = Time.realtimeSinceStartup;
            float recall = Curve(now - _recall);
            int mode = Mode;
            if (mode == 2) return 1f;
            if (mode == 0) return recall;
            if (state == null) state = "";
            State st;
            if (!_states.TryGetValue(id, out st))
            {
                st = new State();
                st.Sig = state; st.Since = now; st.Seen = now;
                _states[id] = st;
            }
            else if (st.Sig != state || now - st.Seen > Gone)
            {
                st.Sig = state;
                st.Since = now;
            }
            st.Seen = now;
            return Mathf.Max(recall, Curve(now - st.Since));
        }

        /// <summary>Interaction prompts: shown unless switched off (then the hint key).</summary>
        internal static bool Prompts
        {
            get
            {
                if (Mode != 0 && (_prompts == null || _prompts.Value)) return true;
                return Curve(Time.realtimeSinceStartup - _recall) > 0f;
            }
        }

        /// <summary>The colour with its alpha multiplied by `alpha`.</summary>
        internal static Color Tint(Color c, float alpha)
        {
            c.a *= alpha;
            return c;
        }

        internal static ConfigEntry<bool> PromptsEntry { get { return _prompts; } }
    }

    /// <summary>Binding, migration and the in-game settings window.</summary>
    internal static class Settings
    {
        // 1 (P9): [Effects], [Hints], [Settings] added.
        // 2 (6.57.0): [World] EastTile on by default - a file from before is
        //    switched on once (EastWorld.BindConfig asks FileLayout).
        // 3 (Q4): every gameplay feature on by default - [PlayerAn2] Enabled
        //    and [An2Repair] Enabled from a file before are switched on once
        //    (Settings.GameplayOn, asked by their BindConfig).
        const int Layout = 4;
        /// <summary>The layout this config file had when the plugin started,
        /// before Migrate stamps the current one: -1 until first read.</summary>
        static int _fileLayout = -1;
        const int WindowId = 0x4E445253;
        static ConfigEntry<int> _version;
        static ConfigEntry<string> _menuKey;
        static KeyCode _menu = KeyCode.None;
        static string _menuName;
        static bool _open, _clearFocus;
        static Rect _window = new Rect(60f, 80f, 440f, 420f);
        static bool _warned;

        internal static bool IsOpen { get { return _open; } }

        internal static void BindConfig(ConfigFile cfg)
        {
            Fx.BindConfig(cfg);
            Anim.BindConfig(cfg);
            Hints.BindConfig(cfg);
            _menuKey = cfg.Bind("Settings", "MenuKey", "F2",
                "Opens the Revival settings window: particle density, every "
                + "animation switch and the hint mode. Changes are saved at once. "
                + "None = no window (edit this file instead).");
            FileLayout(cfg);
            Migrate();
        }

        /// <summary>
        /// The settings layout the config file carried at start (0 = a file
        /// from before P9, or a new one). Read once and kept, so a module that
        /// migrates its own key can ask before or after Migrate has stamped
        /// the file - the bind order in RevivalPlugin does not matter.
        /// </summary>
        internal static int FileLayout(ConfigFile cfg)
        {
            if (_version == null)
                _version = cfg.Bind("Settings", "Version", 0,
                    "Settings layout this file was last updated to. Written by the "
                    + "plugin; do not edit.");
            if (_fileLayout < 0) _fileLayout = _version.Value;
            return _fileLayout;
        }

        /// <summary>Layout 2 turned the east tile on by default.</summary>
        internal const int EastTileOnLayout = 2;

        /// <summary>Layout 3 turned every gameplay feature on by default.</summary>
        internal const int GameplayOnLayout = 3;

        /// <summary>Layout 4 gave the 52-K its full-crew cadence key and the
        /// 30 rpm defaults (Flak.MigrateCadence).</summary>
        internal const int FlakCadenceLayout = 4;

        /// <summary>
        /// Rule since Q4: a gameplay feature is on by default; settings only
        /// tune graphics and performance. A key that was off by default in an
        /// older file holds that old default, not a choice: switch it on once.
        /// Migrate then stamps the file, so false set afterwards stays false.
        /// </summary>
        internal static void GameplayOn(ConfigFile cfg, ConfigEntry<bool> e)
        {
            if (e == null || e.Value || FileLayout(cfg) >= GameplayOnLayout) return;
            e.Value = true;
            if (RevivalPlugin.L != null)
                RevivalPlugin.L.LogInfo("Settings: [" + e.Definition.Section + "] " + e.Definition.Key
                    + " false in a config from before layout " + GameplayOnLayout
                    + " - switched on once (gameplay features are on by default).");
        }

        /// <summary>
        /// Layout 1 (P9) only adds keys, so nothing a player set is rewritten:
        /// BepInEx keeps every existing value, the new keys start at their
        /// defaults. Layout 2 rewrites one value, [World] EastTile, and does
        /// that in EastWorld.BindConfig. Layout 3 switches the gameplay
        /// features that used to be off ([PlayerAn2] Enabled, [An2Repair]
        /// Enabled) on once, through GameplayOn. Layout 4 moves the 52-K's shipped
        /// cadence defaults (RateOfFire 15, ManualRateOfFire 18) to the new numbers
        /// in Flak.MigrateCadence and keeps any other value's effective cadence.
        /// The stamp lets a later layout migrate from a known state.
        /// </summary>
        static void Migrate()
        {
            if (_version == null || _version.Value >= Layout) return;
            int from = _version.Value;
            _version.Value = Layout;
            if (RevivalPlugin.L != null)
                RevivalPlugin.L.LogInfo("Settings: config layout " + from + " -> " + Layout
                    + " ([Effects], [Hints], [Settings] added; [World] EastTile, [PlayerAn2]/[An2Repair] Enabled on once; [Flak52K] shipped cadence defaults moved to 30 rpm; other values kept). "
                    + "Particles " + Fx.Names[Fx.Level] + ", hints " + Hints.Names[Hints.Mode] + ".");
        }

        internal static KeyCode ParseKey(string name, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            string t = name.Trim();
            if (t.Length == 0) return fallback;
            if (t.Equals("None", StringComparison.OrdinalIgnoreCase)) return KeyCode.None;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), t, true); }
            catch (Exception) { return fallback; }
        }

        internal static void Warn(string s)
        {
            if (_warned || RevivalPlugin.L == null) return;
            _warned = true;
            RevivalPlugin.L.LogWarning("Settings: " + s);
        }

        internal static void Tick()
        {
            Hints.Tick();
            string name = _menuKey == null ? "F2" : _menuKey.Value;
            if (name != _menuName)
            {
                _menuName = name;
                _menu = ParseKey(name, KeyCode.None);
            }
            if (_menu == KeyCode.None || !Input.GetKeyDown(_menu)) return;
            _open = !_open;
            if (!_open) Close();
            if (RevivalPlugin.L != null) RevivalPlugin.L.LogInfo("Settings window " + (_open ? "open" : "closed") + ".");
        }

        static void Close()
        {
            _open = false;
            _clearFocus = true;
            if (!CursorTracker.SawCall) return;
            CursorTracker.Restoring = true;
            try
            {
                Cursor.lockState = CursorTracker.DesiredLock;
                Cursor.visible = CursorTracker.DesiredVisible;
            }
            catch (Exception ex) { Warn("cursor back: " + ex.Message); }
            finally { CursorTracker.Restoring = false; }
        }

        internal static void Draw()
        {
            if (_clearFocus)
            {
                _clearFocus = false;
                GUIUtility.keyboardControl = 0;
                GUIUtility.hotControl = 0;
            }
            if (!_open) return;
            CursorTracker.Restoring = true;
            try
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            finally { CursorTracker.Restoring = false; }
            _window = GUILayout.Window(WindowId, _window, Content,
                Loc.T("Revival - Настройки", "Revival - Settings"));
        }

        static readonly string[] LevelRu = { "Выкл", "Низко", "Средне", "Высоко" };
        static readonly string[] LevelEn = { "Off", "Low", "Medium", "High" };
        static readonly string[] FarRu = { "Выкл", "Низко", "Норма" };
        static readonly string[] HintRu = { "Выкл", "Авто", "Всегда" };
        static readonly string[] HintEn = { "Off", "Auto", "Always" };
        static readonly string[] BombRu = { "8 x ФАБ-100", "12 x ФАБ-50" };
        static readonly string[] BombEn = { "8 x FAB-100", "12 x FAB-50" };

        static void Content(int id)
        {
            try
            {
                GUILayout.Label(Loc.T("Частицы (огонь, дым, взрывы, разрывы, вспышки)",
                                      "Particles (fire, smoke, explosions, flak, muzzle flashes)"));
                int lv = Fx.Level;
                int nlv = GUILayout.Toolbar(lv, Loc.Lang() == 0 ? LevelRu : LevelEn);
                if (nlv != lv) Fx.SetLevel(nlv);

                GUILayout.Space(6f);
                GUILayout.Label(Loc.T("Дальний лес (лес за дальностью прорисовки деревьев)",
                                      "Far forest (forest past the tree draw distance)"));
                int ff = FarForest.Level;
                int nff = GUILayout.Toolbar(ff, Loc.Lang() == 0 ? FarRu : FarForest.Names);
                if (nff != ff) FarForest.SetLevel(nff);

                GUILayout.Space(6f);
                GUILayout.Label(Loc.T("Анимации и эффекты", "Animations and effects"));
                ConfigEntry<bool>[] e = Anim.Entries();
                string[] ru = { "Все анимации и эффекты", "Отдача стволов", "Вращение радаров",
                                "Трассеры", "Дульные вспышки", "Свет взрывов", "Винт Ан-2" };
                string[] en = { "All animations and effects", "Barrel recoil", "Rotating radars",
                                "Tracers", "Muzzle flashes", "Explosion lights", "An-2 propeller" };
                for (int i = 0; i < e.Length; i++)
                {
                    if (e[i] == null) continue;
                    bool enabled = i == 0 || Anim.All;
                    GUI.enabled = enabled;
                    bool v = GUILayout.Toggle(e[i].Value, (i == 0 ? "" : "   ") + Loc.T(ru[i], en[i]));
                    if (v != e[i].Value) e[i].Value = v;
                }
                GUI.enabled = true;

                GUILayout.Space(6f);
                GUILayout.Label(Loc.T("Подсказки", "Hints"));
                int hm = Hints.Mode;
                int nhm = GUILayout.Toolbar(hm, Loc.Lang() == 0 ? HintRu : HintEn);
                if (nhm != hm) Hints.SetMode(nhm);
                ConfigEntry<bool> p = Hints.PromptsEntry;
                if (p != null)
                {
                    bool v = GUILayout.Toggle(p.Value, Loc.T("Подсказки клавиш у объектов", "Key prompts at objects"));
                    if (v != p.Value) p.Value = v;
                }
                GUILayout.Label(Loc.T("Авто: подсказка видна " + Hints.Seconds.ToString("0") + " с, затем гаснет. ["
                                      + Hints.KeyLabel + "] - показать снова.",
                                      "Auto: a hint shows for " + Hints.Seconds.ToString("0") + " s, then fades. ["
                                      + Hints.KeyLabel + "] shows them again."));

                GUILayout.Space(6f);
                GUILayout.Label(Loc.T("Бомбовая загрузка Ан-2 (для пустых бомбодержателей)",
                                      "An-2 bomb load (for empty racks)"));
                int bl = An2Bombs.LoadIndex;
                int nbl = GUILayout.Toolbar(bl, Loc.Lang() == 0 ? BombRu : BombEn);
                if (nbl != bl) An2Bombs.SetLoad(nbl);

                GUILayout.Space(6f);
                GUILayout.Label(Loc.T("Всё сохраняется в BepInEx/config/nextday.revival.toolkit.cfg.",
                                      "Saved at once to BepInEx/config/nextday.revival.toolkit.cfg."));
                if (GUILayout.Button(Loc.T("закрыть", "close"))) Close();
            }
            catch (Exception ex) { Warn("window: " + ex.Message); }
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }
    }
}
