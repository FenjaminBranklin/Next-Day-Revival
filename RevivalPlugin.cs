// Next Day: Survival - Revival Toolkit
//
//   1. Cursor- und Fensterfix (Zwei-Monitor-Bug, Zeiger laeuft aus dem Fenster)
//   2. Neue Items: MG42, TAC-50, MG-Gurt, .50-BMG-Kiste
//      Mesh, Textur, Material und Icons entstehen zur Laufzeit aus Dateien
//      neben der DLL - kein AssetBundle, kein Unity-Editor noetig.
//   3. Eigenes Zielfernrohr-Overlay fuer die TAC-50
//   4. Diagnose
//
// Referenziert bewusst KEIN Assembly-CSharp.dll - alle Spieltypen werden ueber
// AccessTools per Namen aufgeloest. Kompiliert mit csc aus .NET 3.5, also
// C# 3.0: keine optionalen Argumente, keine Lambdas mit Ausdrucksbaeumen.
//
// WAS SICH GEGENUEBER 0.2.0 GEAENDERT HAT (und warum)
// ---------------------------------------------------
// a) Beschreibung: der Schluessel heisst "$<id>_Descr", nicht "$<id>_Description".
//    Belegt in PlayerInventoryManager::AddWeaponItemFromValues und im
//    Localization_DB-TextAsset. Darum stand im Spiel die rohe "$1160_..."-Zeile.
// b) Material: keine Metallic/Gloss-Map mehr. Von 1488 Materialien mit
//    _Metallic in resources.assets benutzt KEIN einziges eine solche Map; die
//    Waffen stehen auf _Metallic 0.0 und _Glossiness 0.6. Genau das wird jetzt
//    gesetzt.
// c) Skalierung: WeaponTranformManager::ApplyLocalTransformData ueberschreibt
//    beim Anlegen localPosition, localEulerAngles UND localScale der Wurzel.
//    Die Wurzelskalierung des Prefabs war damit wirkungslos. Der Konfigwert
//    wird jetzt in die kopierte Komponente geschrieben, wo er auch wirkt.
// d) Icons: das Spiel benutzt ZWEI Bilder je Waffe - ItemIcon 300x300 und
//    WeaponIcon 317x183. Bisher ging ein einziges 256er-Quadrat in beide.
// e) Aus der einen fest verdrahteten Waffe ist eine Tabelle geworden.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    // ------------------------------------------------------------------ Daten

    /// <summary>Ein neues Item. Alles, was den Bau steuert, steht hier.</summary>
    public class ItemDef
    {
        public int Id;
        public int DonorId;            // vorhandenes Item, von dem geklont wird
        public bool IsWeapon;          // braucht PlayerDataPrefabs/Weapons/<id>_Weapon
        public string Name;
        public string Descr;
        public string Mesh;            // Datei im assets-Verzeichnis
        public string Diffuse;
        public string Normal;
        public string Icon;            // ItemIcon, 300x300
        public string WeaponIcon;      // WeaponIcon, 317x183; null bei Munition
        public int Bullets;            // Waffe: Gurtlaenge. Munition: Kapazitaet.
        public int ClipItemId;
        public float Weight;
        public ItemFactory Factory;

        public ItemDef(int id, int donorId, bool isWeapon, string name, string descr,
                       string mesh, string diffuse, string normal,
                       string icon, string weaponIcon,
                       int bullets, int clipItemId, float weight)
        {
            Id = id; DonorId = donorId; IsWeapon = isWeapon;
            Name = name; Descr = descr;
            Mesh = mesh; Diffuse = diffuse; Normal = normal;
            Icon = icon; WeaponIcon = weaponIcon;
            Bullets = bullets; ClipItemId = clipItemId; Weight = weight;
            Factory = new ItemFactory(this);
        }
    }

    // ----------------------------------------------------------------- Plugin

    [BepInPlugin(GUID, NAME, VERSION)]
    public class RevivalPlugin : BaseUnityPlugin
    {
        public const string GUID = "nextday.revival.toolkit";
        public const string NAME = "Next Day Revival Toolkit";
        public const string VERSION = "0.3.0";

        internal static ManualLogSource L;
        internal static string AssetDir;

        internal static ConfigEntry<bool> CfgCursorFix;
        internal static ConfigEntry<bool> CfgConfine;
        internal static ConfigEntry<bool> CfgCustomItems;
        internal static ConfigEntry<bool> CfgLootTables;
        internal static ConfigEntry<float> CfgScale;
        internal static ConfigEntry<float> CfgMetallic;
        internal static ConfigEntry<float> CfgGlossiness;
        internal static ConfigEntry<int> CfgRenderQueue;
        internal static ConfigEntry<bool> CfgVerbose;
        internal static ConfigEntry<string> CfgExtraScenes;
        internal static ConfigEntry<bool> CfgSceneJump;
        internal static ConfigEntry<int> CfgJumpScene;
        internal static ConfigEntry<string> CfgJumpKey;

        internal static List<ItemDef> Items = new List<ItemDef>();
        internal const string ScopePath = "WeaponElements/Scopes/NDR_Scope50";
        internal static bool SetupDone;

        private Harmony _harmony;

        void Awake()
        {
            L = Logger;
            L.LogInfo("=========================================================");
            L.LogInfo("  NEXT DAY REVIVAL TOOLKIT " + VERSION);
            L.LogInfo("=========================================================");

            AssetDir = Path.Combine(Path.GetDirectoryName(Info.Location), "assets");
            L.LogInfo("Asset-Verzeichnis: " + AssetDir);

            BindConfig();
            BuildItemTable();

            _harmony = new Harmony(GUID);
            PatchCursor();
            PatchResourcesLoad();
            PatchLocalization();
            PatchBackpackDiagnostics();
            PatchReloadDiagnostics();

            StartCoroutine(LateSetup());
        }

        void BindConfig()
        {
            CfgCursorFix = Config.Bind("Fixes", "CursorLockFix", true,
                "Cursor-Lock nach Fokuswechsel wiederherstellen und jeden Frame "
                + "nachziehen. Behebt, dass der Zeiger auf den zweiten Monitor wandert.");
            CfgConfine = Config.Bind("Fixes", "ConfineCursorToWindow", true,
                "Solange gespielt wird (Zeiger versteckt) und das Fenster den Fokus hat, "
                + "wird der Systemzeiger per ClipCursor im Fensterbereich gehalten. Das "
                + "ist der eigentliche Fensterfix: Unity kennt den Zeiger nur, solange er "
                + "ueber dem Fenster steht, also gehen Klicks am Bildschirmrand verloren.");
            CfgCustomItems = Config.Bind("CustomItems", "Enabled", true,
                "Die selbstgebauten Items bereitstellen.");
            CfgLootTables = Config.Bind("CustomItems", "AddToLootTables", true,
                "Die neuen IDs zusaetzlich in die Loot-Kategorien der Spende-Items "
                + "eintragen, damit sie in der Welt gefunden werden koennen.");
            CfgScale = Config.Bind("CustomItems", "WeaponScale", 0.01f,
                "Skalierung der Waffe in der Hand. Wird in die kopierten "
                + "Transform-Komponenten geschrieben, weil ApplyLocalTransformData die "
                + "Wurzelskalierung sonst ueberschreibt. Die Spielwaffen benutzen 0.01.");
            CfgMetallic = Config.Bind("CustomItems", "Metallic", 0.0f,
                "Metallic-Anteil des Materials. Die Waffenmaterialien des Spiels "
                + "(z. B. osnova am RPD) stehen auf 0.0.");
            CfgGlossiness = Config.Bind("CustomItems", "Glossiness", 0.6f,
                "Smoothness des Materials. osnova steht auf 0.6.");
            CfgRenderQueue = Config.Bind("CustomItems", "RenderQueueOffset", 60,
                "Verschiebt die Zeichenreihenfolge nach hinten. 0 = wie die "
                + "Spielmaterialien (Geometry, 2000).");
            CfgVerbose = Config.Bind("Debug", "Verbose", false,
                "Ausfuehrliche Feld- und Ladeprotokolle. Fuer die Fehlersuche.");

            // Der Build enthaelt zehn fertige Szenen, die die Release-Regionliste
            // nicht benutzt - darunter Bunker_A65 (3), GW_Scene_2 (4) und
            // Underground_Lab (18). Beides hier ist ein Erkundungswerkzeug und
            // steht deshalb standardmaessig aus.
            CfgExtraScenes = Config.Bind("Research", "ExtraScenes", "",
                "Zusaetzliche Szenen-Buildindizes fuer Region 0, mit Komma getrennt, "
                + "z. B. \"3,4,18\". Ohne Eintrag in der Szenenliste liefert "
                + "GetRegionDataAtScene null und der Beitritt zum Raum scheitert. "
                + "Leer lassen heisst: nichts aendern.");
            CfgSceneJump = Config.Bind("Research", "EnableSceneJump", false,
                "Auf Tastendruck in die unter JumpScene angegebene Szene wechseln. "
                + "UNGETESTET - die ungenutzten Szenen koennen ohne Spawnpunkte oder "
                + "ohne Loot dastehen. Vorher speichern.");
            CfgJumpScene = Config.Bind("Research", "JumpScene", 3,
                "Buildindex der Zielszene. 3 Bunker_A65, 4 GW_Scene_2, "
                + "5 GW_Scene_3, 6 Catacombs, 7 GW_Scene_1, 8..17 Chunks 0..9, "
                + "18 Underground_Lab.");
            CfgJumpKey = Config.Bind("Research", "JumpKey", "F9",
                "Taste fuer den Szenenwechsel, Name aus UnityEngine.KeyCode.");
        }

        // Die Tabelle. Spende-IDs sind bewusst artverwandt gewaehlt: das RPD ist
        // ein gurtgefuettertes MG, die SVD ein Repetierer-artiges Scharfschuetzen-
        // gewehr, und 2030 ist die 7,62-Kiste, an der sich beide neuen
        // Munitionsitems orientieren.
        void BuildItemTable()
        {
            Items.Clear();
            Items.Add(new ItemDef(
                1160, 1023, true,
                "MG42",
                "Maschinengewehr 42. Gurtzufuehrung, 7,62 mm, offener Verschluss. "
                + "Hoechste Feuerrate im Feld - ein voller Gurt ist in zehn Sekunden durch. "
                + "Frisst MG-Gurte, notfalls auch Kisten und Magazine.",
                "mg42.ndmesh", "mg42_diffuse.png", "mg42_normal.png",
                "mg42_icon.png", "mg42_weapon_icon.png",
                200, 2050, 12.0f));

            Items.Add(new ItemDef(
                1161, 1010, true,
                "TAC-50",
                "Repetierbuechse im Kaliber .50 BMG. Fuenf Schuss, schwerer Lauf, "
                + "grosse Muendungsbremse. Zwischen zwei Schuessen muss reproduziert "
                + "werden, und ein Magazinwechsel dauert eine halbe Ewigkeit. "
                + "Dafuer haelt kaum etwas einen Treffer aus.",
                "sniper50.ndmesh", "sniper50_diffuse.png", "sniper50_normal.png",
                "sniper50_icon.png", "sniper50_weapon_icon.png",
                5, 2051, 14.5f));

            Items.Add(new ItemDef(
                2050, 2030, false,
                "MG-Gurt 7,62 (200)",
                "Gurtkasten mit 200 Schuss 7,62 fuer das MG42. Doppelt so viel wie "
                + "eine Kiste - und doppelt so schwer.",
                "mgbelt.ndmesh", "mgbelt_diffuse.png", "mgbelt_normal.png",
                "mgbelt_icon.png", null,
                200, 0, 4.0f));

            Items.Add(new ItemDef(
                2051, 2030, false,
                ".50 BMG Kiste (10)",
                "Zehn Patronen .50 BMG in der Blechkiste. Passt in nichts ausser "
                + "die TAC-50, wiegt entsprechend.",
                "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png",
                "ammo50_icon.png", null,
                10, 0, 3.2f));

            L.LogInfo("Item-Tabelle: " + Items.Count + " Eintraege");
            for (int i = 0; i < Items.Count; i++)
            {
                ItemDef d = Items[i];
                L.LogInfo("  " + d.Id + "  " + d.Name + "  (Spende " + d.DonorId
                          + ", " + (d.IsWeapon ? "Waffe" : "Munition")
                          + ", Bullets " + d.Bullets + ")");
            }
        }

        // ------------------------------------------------------------ Harmony

        void PatchCursor()
        {
            try
            {
                MethodInfo setLock = AccessTools.PropertySetter(typeof(Cursor), "lockState");
                MethodInfo setVis = AccessTools.PropertySetter(typeof(Cursor), "visible");
                if (setLock != null)
                    _harmony.Patch(setLock, null,
                        new HarmonyMethod(typeof(CursorTracker).GetMethod("AfterLock")), null, null, null);
                if (setVis != null)
                    _harmony.Patch(setVis, null,
                        new HarmonyMethod(typeof(CursorTracker).GetMethod("AfterVisible")), null, null, null);
                L.LogInfo("Cursor-Patches: lockState=" + (setLock != null)
                          + " visible=" + (setVis != null));
            }
            catch (Exception ex) { L.LogError("Cursor-Patch fehlgeschlagen: " + ex); }
        }

        void PatchResourcesLoad()
        {
            try
            {
                // AccessTools.Method wirft hier AmbiguousMatchException, weil
                // Resources.Load(string) und Resources.Load<T>(string) dieselbe
                // Parameterliste haben. Also von Hand auswaehlen.
                MethodInfo target = null;
                foreach (MethodInfo m in typeof(Resources).GetMethods(
                             BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Load") continue;
                    if (m.IsGenericMethod || m.IsGenericMethodDefinition) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) target = m;
                }
                if (target == null) { L.LogError("Resources.Load(string) nicht gefunden."); return; }

                _harmony.Patch(target,
                    new HarmonyMethod(typeof(ResourceHook).GetMethod("Prefix")),
                    null, null, null, null);
                L.LogInfo("Resources.Load(string) gepatcht.");
            }
            catch (Exception ex) { L.LogError("Resources.Load-Patch fehlgeschlagen: " + ex); }
        }

        // Der Inventarname kommt aus der Localization ueber "$<ItemID>_Name",
        // die Beschreibung ueber "$<ItemID>_Descr". Fuer neue IDs gibt es beides
        // nicht, also wird es hier nachgereicht.
        void PatchLocalization()
        {
            try
            {
                Type t = AccessTools.TypeByName("LocalizationManager");
                if (t == null) { L.LogWarning("LocalizationManager nicht gefunden."); return; }
                MethodInfo m = null;
                foreach (MethodInfo cand in t.GetMethods(BindingFlags.Instance | BindingFlags.Public
                                                         | BindingFlags.NonPublic))
                {
                    if (cand.Name != "GetLocalizationText") continue;
                    ParameterInfo[] ps = cand.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType == typeof(string)
                        && cand.ReturnType == typeof(string)) { m = cand; break; }
                }
                if (m == null) { L.LogWarning("GetLocalizationText(string, ...) nicht gefunden."); return; }
                _harmony.Patch(m, null,
                    new HarmonyMethod(typeof(LocalizationHook).GetMethod("Postfix")), null, null, null);
                L.LogInfo("LocalizationManager." + m.Name + " gepatcht ("
                          + m.GetParameters().Length + " Parameter).");
            }
            catch (Exception ex) { L.LogError("Localization-Patch fehlgeschlagen: " + ex); }
        }

        void PatchReloadDiagnostics()
        {
            try
            {
                Type t = AccessTools.TypeByName("PlayerFirearmWeaponController");
                if (t == null) { L.LogWarning("PlayerFirearmWeaponController nicht gefunden."); return; }
                MethodInfo m = AccessTools.Method(t, "ReloadWeapon", null, null);
                if (m == null) { L.LogWarning("ReloadWeapon nicht gefunden."); return; }
                _harmony.Patch(m,
                    new HarmonyMethod(typeof(Diag).GetMethod("ReloadPrefix")), null, null, null, null);
                L.LogInfo("ReloadWeapon-Diagnose aktiv.");
            }
            catch (Exception ex) { L.LogError("Reload-Patch fehlgeschlagen: " + ex.Message); }
        }

        void PatchBackpackDiagnostics()
        {
            try
            {
                Type t = AccessTools.TypeByName("PlayerInventoryManager");
                if (t == null) { L.LogWarning("PlayerInventoryManager nicht gefunden."); return; }
                MethodInfo m = null;
                foreach (MethodInfo cand in t.GetMethods(BindingFlags.Instance | BindingFlags.Public
                                                         | BindingFlags.NonPublic))
                    if (cand.Name == "AddBackpackItemFromValues") { m = cand; break; }
                if (m == null) { L.LogWarning("AddBackpackItemFromValues nicht gefunden."); return; }
                _harmony.Patch(m,
                    new HarmonyMethod(typeof(Diag).GetMethod("BackpackPrefix")), null, null, null, null);
                L.LogInfo("Rucksack-Diagnose aktiv (" + m.GetParameters().Length + " Parameter).");
            }
            catch (Exception ex) { L.LogError("Rucksack-Patch fehlgeschlagen: " + ex.Message); }
        }

        // -------------------------------------------------------------- Setup

        IEnumerator LateSetup()
        {
            // Die Item-Datenbank ist ein ScriptableObject, das erst beim ersten
            // Zugriff aus Resources geladen wird. Vorher gibt es nichts zu ergaenzen.
            yield return new WaitForSeconds(8f);
            if (!CfgCustomItems.Value) yield break;

            L.LogInfo("--- Selbsttest ---");
            for (int i = 0; i < Items.Count; i++)
            {
                ItemDef d = Items[i];
                if (d.IsWeapon)
                {
                    GameObject model = d.Factory.GetModelPrefab();
                    L.LogInfo(d.Id + " Modell-Prefab   : "
                              + (model == null ? "FEHLGESCHLAGEN" : model.name));
                }
                GameObject spawn = d.Factory.GetSpawnPrefab(null);
                L.LogInfo(d.Id + " Inventar-Prefab : "
                          + (spawn == null ? "FEHLGESCHLAGEN" : spawn.name));
            }

            Registry.RegisterAll();
            if (CfgLootTables.Value) Registry.AddToLootTables();
            Research.ReportRegions();
            Research.ApplyExtraScenes();
            L.LogInfo("--- Selbsttest Ende ---");
            SetupDone = true;

            // Zweiter Anlauf: bis hierher kann der Client die Datenbank neu
            // geladen haben, etwa beim Wechsel ins Spiel.
            yield return new WaitForSeconds(12f);
            Registry.RegisterAll();
            if (CfgLootTables.Value) Registry.AddToLootTables();
            StartCoroutine(WaitForWeaponDb());
        }

        // Pollt, bis der Client weapons_db.xml verarbeitet hat, und schreibt dann
        // einmal die geparsten Daten der neuen Waffen ins Log.
        IEnumerator WaitForWeaponDb()
        {
            int tries = 0;
            while (tries < 120)
            {
                tries++;
                yield return new WaitForSeconds(5f);
                if (!WeaponData.Available(1023)) continue;

                L.LogInfo("--- weapons_db geparst nach " + (tries * 5) + "s ---");
                for (int i = 0; i < Items.Count; i++)
                    if (Items[i].IsWeapon) WeaponData.Summary(Items[i].Id);
                WeaponData.Summary(1023);
                L.LogInfo("--- Waffendaten Ende ---");
                yield break;
            }
            L.LogWarning("weapons_db wurde nicht rechtzeitig geparst.");
        }

        // ------------------------------------------------------------- Cursor

        void Update()
        {
            CursorGuard.Tick();
            Research.Tick();
        }

        void OnApplicationFocus(bool hasFocus)
        {
            CursorGuard.OnFocus(hasFocus);
        }

        void OnDestroy()
        {
            CursorGuard.Release();
        }
    }

    // ------------------------------------------------------------ Cursor-Fix

    public static class CursorTracker
    {
        public static bool Restoring;
        public static CursorLockMode DesiredLock = CursorLockMode.None;
        public static bool DesiredVisible = true;
        public static bool SawCall;

        public static void AfterLock(CursorLockMode value)
        {
            if (Restoring) return;
            DesiredLock = value;
            SawCall = true;
        }

        public static void AfterVisible(bool value)
        {
            if (Restoring) return;
            DesiredVisible = value;
            SawCall = true;
        }
    }

    /// <summary>
    /// Haelt den Zeiger dort, wo das Spiel ihn haben will.
    ///
    /// Zwei getrennte Probleme, zwei getrennte Mittel:
    ///
    /// 1. Unity verwirft Cursor.lockState beim Fokusverlust und erwartet, dass
    ///    die Anwendung ihn wiederherstellt. In der gesamten Assembly-CSharp
    ///    gibt es genau ein OnApplicationFocus, und das sitzt auf AudioRpc -
    ///    nichts stellt den Lock wieder her. Deshalb wird der zuletzt
    ///    gewuenschte Zustand hier jeden Frame nachgezogen, nicht nur einmal
    ///    beim Fokuswechsel: der Lock geht auch verloren, wenn Windows
    ///    zwischendurch ein anderes Fenster aktiviert.
    ///
    /// 2. Der Lock allein reicht im Fenstermodus nicht. Steht der Zeiger
    ///    ausserhalb des Fensters, bekommt Unity die Klicks gar nicht erst -
    ///    sie gehen an das Fenster darunter. Genau das ist der Effekt "weit
    ///    nach links geschaut, Zeiger klebt am linken Rand, Klick passiert
    ///    nichts". Dagegen hilft nur ClipCursor aus user32: der Systemzeiger
    ///    wird auf das Client-Rechteck begrenzt.
    ///
    /// Begrenzt wird nur, solange gespielt wird (Zeiger versteckt) UND das
    /// Fenster den Fokus hat. Im Menue und bei Fokusverlust wird sofort wieder
    /// freigegeben, sonst haette man die Maus im Fenster gefangen.
    /// </summary>
    public static class CursorGuard
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref RECT lpRect);

        [DllImport("user32.dll", EntryPoint = "ClipCursor")]
        private static extern bool ClipCursorNull(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        private static bool _clipped;
        private static bool _focused = true;
        private static int _failures;
        private static int _frame;

        // Windows hebt die Begrenzung bei jedem Fokuswechsel von selbst auf, sie
        // muss also nachgezogen werden. Jeden Frame waere Verschwendung; alle 15
        // Frames sind bei 60 fps eine Viertelsekunde und damit nicht spuerbar.
        private const int CLIP_EVERY = 15;

        public static void OnFocus(bool hasFocus)
        {
            _focused = hasFocus;
            if (!hasFocus) { Release(); return; }
            Restore();
        }

        public static void Tick()
        {
            if (!_focused) return;
            if (RevivalPlugin.CfgCursorFix != null && RevivalPlugin.CfgCursorFix.Value)
                Restore();

            if (RevivalPlugin.CfgConfine == null || !RevivalPlugin.CfgConfine.Value)
            {
                if (_clipped) Release();
                return;
            }

            // Versteckter Zeiger heisst: es wird gespielt, nicht geklickt.
            bool wantClip = !CursorTracker.DesiredVisible && CursorTracker.SawCall;
            if (!wantClip)
            {
                if (_clipped) Release();
                return;
            }
            _frame++;
            if (_clipped && (_frame % CLIP_EVERY) != 0) return;
            Clip();
        }

        private static void Restore()
        {
            if (!CursorTracker.SawCall) return;
            try
            {
                if (Cursor.lockState == CursorTracker.DesiredLock
                    && Cursor.visible == CursorTracker.DesiredVisible) return;
                CursorTracker.Restoring = true;
                Cursor.lockState = CursorTracker.DesiredLock;
                Cursor.visible = CursorTracker.DesiredVisible;
                CursorTracker.Restoring = false;
            }
            catch (Exception ex)
            {
                CursorTracker.Restoring = false;
                Warn("Cursor-Wiederherstellung: " + ex.Message);
            }
        }

        private static void Clip()
        {
            try
            {
                IntPtr hwnd = GetActiveWindow();
                if (hwnd == IntPtr.Zero) return;
                RECT c;
                if (!GetClientRect(hwnd, out c)) return;

                POINT tl; tl.X = c.Left; tl.Y = c.Top;
                POINT br; br.X = c.Right; br.Y = c.Bottom;
                if (!ClientToScreen(hwnd, ref tl)) return;
                if (!ClientToScreen(hwnd, ref br)) return;

                // Einen Pixel nach innen: liegt der Rand genau auf der
                // Bildschirmkante, laesst Windows den Zeiger sonst haengen.
                RECT r;
                r.Left = tl.X + 1; r.Top = tl.Y + 1;
                r.Right = br.X - 1; r.Bottom = br.Y - 1;
                if (r.Right <= r.Left || r.Bottom <= r.Top) return;

                ClipCursor(ref r);
                if (!_clipped)
                {
                    _clipped = true;
                    RevivalPlugin.L.LogInfo("Zeiger auf das Fenster begrenzt: "
                        + r.Left + "," + r.Top + " bis " + r.Right + "," + r.Bottom);
                }
            }
            catch (Exception ex) { Warn("ClipCursor: " + ex.Message); }
        }

        public static void Release()
        {
            if (!_clipped) return;
            try { ClipCursorNull(IntPtr.Zero); }
            catch (Exception ex) { Warn("ClipCursor freigeben: " + ex.Message); }
            _clipped = false;
        }

        private static void Warn(string msg)
        {
            // Ein Fehler pro Frame waere eine Logdatei im Gigabyte-Bereich.
            if (_failures >= 5) return;
            _failures++;
            if (RevivalPlugin.L != null) RevivalPlugin.L.LogWarning(msg);
        }
    }

    // ------------------------------------------------------------ Hooks

    public static class LocalizationHook
    {
        public static void Postfix(string __0, ref string __result)
        {
            if (__0 == null || __0.Length < 3 || __0[0] != '$') return;
            List<ItemDef> items = RevivalPlugin.Items;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDef d = items[i];
                if (__0 == "$" + d.Id + "_Name") { __result = d.Name; return; }
                if (__0 == "$" + d.Id + "_Descr") { __result = d.Descr; return; }
            }
        }
    }

    /// <summary>
    /// Faengt Resources.Load ab und liefert fuer die neuen IDs das selbst
    /// gebaute Prefab statt null.
    ///
    /// Das Spiel laedt fuer eine Waffe ZWEI verschiedene Prefabs:
    ///   PlayerDataPrefabs/Weapons/&lt;id&gt;_Weapon   Modell in Hand und am Ruecken
    ///   &lt;ItemKategorie.PrefabPatch&gt;&lt;id&gt;_Spawn   Inventareintrag
    /// Der Praefix der zweiten Form haengt an der Kategorie, die fuer eine neue
    /// ID nicht stimmen muss - deshalb wird auf die Endung gematcht.
    ///
    /// Dazu kommt die Zielfernrohr-Textur: xmlItemsDataManager liest das
    /// Attribut Scope aus weapons_db.xml und ruft damit Resources.Load(string)
    /// mit anschliessendem isinst Texture2D auf. Der Pfad muss also nur hier
    /// bedient werden, damit die TAC-50 ein eigenes Fadenkreuz bekommt.
    /// </summary>
    public static class ResourceHook
    {
        const string WEAPON_PREFIX = "PlayerDataPrefabs/Weapons/";
        internal static bool Reentry;
        static int _served;

        public static bool Prefix(string path, ref UnityEngine.Object __result)
        {
            if (Reentry || path == null) return true;
            if (RevivalPlugin.CfgCustomItems == null || !RevivalPlugin.CfgCustomItems.Value)
                return true;

            if (path.Equals(RevivalPlugin.ScopePath, StringComparison.OrdinalIgnoreCase))
            {
                Texture2D scope = Assets.Texture("scope50.png", false, false);
                if (scope == null) return true;
                Log(path, "Zielfernrohr");
                __result = scope;
                return false;
            }

            List<ItemDef> items = RevivalPlugin.Items;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDef d = items[i];

                if (d.IsWeapon
                    && path.Equals(WEAPON_PREFIX + d.Id + "_Weapon", StringComparison.OrdinalIgnoreCase))
                {
                    GameObject go = d.Factory.GetModelPrefab();
                    if (go == null) return true;
                    Log(path, "Modell");
                    __result = go;
                    return false;
                }

                // Auf die Endung matchen, weil der Praefix aus der Kategorie kommt
                // und fuer eine neue ID nicht stimmen muss. Das Zeichen davor muss
                // aber ein Trennzeichen sein - sonst wuerde "2050_Spawn" auch auf
                // "12050_Spawn" passen.
                string tail = d.Id + "_Spawn";
                if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
                    && (path.Length == tail.Length || path[path.Length - tail.Length - 1] == '/'))
                {
                    GameObject go = d.Factory.GetSpawnPrefab(
                        path.Substring(0, path.Length - tail.Length));
                    if (go == null) return true;
                    Log(path, "Inventareintrag");
                    __result = go;
                    return false;
                }
            }
            return true;
        }

        static void Log(string path, string what)
        {
            if (_served >= 24) return;
            _served++;
            RevivalPlugin.L.LogInfo("Ausgeliefert #" + _served + " " + what + ": " + path);
        }
    }

    // -------------------------------------------------------------- Assets

    /// <summary>Laedt Mesh und Texturen aus dem assets-Verzeichnis, mit Cache.</summary>
    public static class Assets
    {
        static Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();
        static Dictionary<string, Mesh> _mesh = new Dictionary<string, Mesh>();

        public static Texture2D Texture(string fileName, bool linear, bool mipmaps)
        {
            if (fileName == null) return null;
            if (_tex.ContainsKey(fileName)) return _tex[fileName];
            Texture2D t = null;
            try
            {
                string path = Path.Combine(RevivalPlugin.AssetDir, fileName);
                if (!File.Exists(path))
                {
                    RevivalPlugin.L.LogWarning("Textur fehlt: " + path);
                }
                else
                {
                    t = new Texture2D(4, 4, TextureFormat.RGBA32, mipmaps, linear);
                    t.LoadImage(File.ReadAllBytes(path));
                    t.name = fileName;
                    t.wrapMode = TextureWrapMode.Clamp;
                    t.Apply(mipmaps);
                    RevivalPlugin.L.LogInfo("Textur geladen: " + fileName + " "
                                            + t.width + "x" + t.height);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning(fileName + ": " + ex.Message); }
            _tex[fileName] = t;
            return t;
        }

        /// <summary>Liest eine .ndmesh. Prueft die Normalen, bevor sie ins Spiel geht.</summary>
        public static Mesh Load(string fileName)
        {
            if (fileName == null) return null;
            if (_mesh.ContainsKey(fileName)) return _mesh[fileName];
            Mesh mesh = null;
            try
            {
                string path = Path.Combine(RevivalPlugin.AssetDir, fileName);
                if (!File.Exists(path)) throw new FileNotFoundException("fehlt: " + path);
                mesh = Read(path, fileName);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Mesh " + fileName + ": " + ex); }
            _mesh[fileName] = mesh;
            return mesh;
        }

        static Mesh Read(string path, string name)
        {
            FileStream fs = File.OpenRead(path);
            BinaryReader r = new BinaryReader(fs);
            try
            {
                byte[] magic = r.ReadBytes(4);
                if (magic[0] != (byte)'N' || magic[1] != (byte)'D'
                    || magic[2] != (byte)'M' || magic[3] != (byte)'S')
                    throw new Exception("falsche Magic in " + path);
                r.ReadInt32();                                  // Version
                int n = r.ReadInt32();

                Vector3[] verts = new Vector3[n];
                for (int i = 0; i < n; i++)
                    verts[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Vector3[] norms = new Vector3[n];
                int bad = 0;
                for (int i = 0; i < n; i++)
                {
                    Vector3 v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    // Eine Normale der Laenge null wird im Shader zu normalize(0)
                    // und damit zu NaN - im Bild ein Pixel ohne Obergrenze, mit
                    // Bloom ein weisser Fleck. Lieber nach oben zeigen lassen.
                    if (v.sqrMagnitude < 1e-8f) { v = Vector3.up; bad++; }
                    norms[i] = v.normalized;
                }
                Vector2[] uvs = new Vector2[n];
                for (int i = 0; i < n; i++)
                    uvs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());

                int m = r.ReadInt32();
                int[] tris = new int[m];
                for (int i = 0; i < m; i++) tris[i] = r.ReadInt32();

                Mesh mesh = new Mesh();
                mesh.name = name;
                mesh.vertices = verts;
                mesh.normals = norms;
                mesh.uv = uvs;
                mesh.triangles = tris;
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                RevivalPlugin.L.LogInfo("Mesh geladen: " + name + "  " + n + " Vertices, "
                                        + (m / 3) + " Dreiecke, bounds=" + mesh.bounds.size
                                        + (bad > 0 ? ("  ACHTUNG " + bad + " Null-Normalen ersetzt") : ""));
                if (bad > 0)
                    RevivalPlugin.L.LogWarning("Null-Normalen in " + name
                        + " - das Mesh-Skript neu laufen lassen, ndmesh.validate faengt das ab.");
                return mesh;
            }
            finally { r.Close(); fs.Close(); }
        }
    }

    // ------------------------------------------------------------- Factory

    /// <summary>Baut Modell- und Inventarprefab fuer genau ein ItemDef.</summary>
    public class ItemFactory
    {
        readonly ItemDef _def;

        GameObject _model;
        GameObject _spawn;
        Component _mySpawned;
        Mesh _mesh;
        Material _mat;
        bool _modelFailed, _spawnFailed;

        public ItemFactory(ItemDef def) { _def = def; }

        public Component MySpawned { get { return _mySpawned; } }

        // Unterordner von LootSpawn laut ResourceManager. Die Spende kann in
        // jedem davon liegen, und die vom Spiel angefragte Kategorie muss nicht
        // stimmen - alle Schusswaffen liegen unter ASR, Munition unter
        // Ammunation, gefragt wird aber nach Rifles.
        static readonly string[] SPAWN_FOLDERS = {
            "LootSpawn/Ammunation/",
            "LootSpawn/Weapons/ASR/", "LootSpawn/Weapons/Rifles/",
            "LootSpawn/Weapons/Handguns/", "LootSpawn/Weapons/Usable/",
            "LootSpawn/Weapons/OneHandMelees/", "LootSpawn/Weapons/TwoHandMelees/",
        };

        // ------------------------------------------------------------ Modell

        public GameObject GetModelPrefab()
        {
            if (_model != null) return _model;
            if (_modelFailed || !_def.IsWeapon) return null;
            try
            {
                _model = BuildModel();
                RevivalPlugin.L.LogInfo(_def.Id + ": Modell-Prefab gebaut.");
            }
            catch (Exception ex)
            {
                _modelFailed = true;
                RevivalPlugin.L.LogError(_def.Id + ": Modellbau fehlgeschlagen: " + ex);
            }
            return _model;
        }

        GameObject LoadDonorWeapon()
        {
            try
            {
                ResourceHook.Reentry = true;
                return Resources.Load("PlayerDataPrefabs/Weapons/" + _def.DonorId + "_Weapon")
                       as GameObject;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Spende-Waffe " + _def.DonorId + ": " + ex.Message);
                return null;
            }
            finally { ResourceHook.Reentry = false; }
        }

        GameObject BuildModel()
        {
            GameObject donor = LoadDonorWeapon();
            _mesh = Assets.Load(_def.Mesh);
            if (_mesh == null) throw new Exception("kein Mesh");
            _mat = MakeMaterial(donor);

            // Aufbau exakt wie beim RPD: Wurzel, darunter das Mesh-Objekt, daran
            // haengen die leeren Anker-Transforms.
            GameObject root = new GameObject(_def.Id + "_Weapon");

            GameObject body = new GameObject(_def.Name.Replace(" ", "_") + "_LOD0_W");
            body.transform.SetParent(root.transform, false);
            MeshFilter mf = body.AddComponent<MeshFilter>();
            mf.sharedMesh = _mesh;
            MeshRenderer mr = body.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;

            // Die Anker liegen an der Geometrie des eigenen Meshes. Y-Minimum ist
            // die Muendung, weil alle Meshes des Toolkits entlang -Y nach vorn
            // zeigen - so wie das RPD-Mesh, an dem sie ausgerichtet sind.
            Bounds b = _mesh.bounds;
            float muzzleY = b.min.y;
            MakeAnchor(body.transform, "Muzzle", new Vector3(0f, muzzleY, 0.096f));
            MakeAnchor(body.transform, "MuzzleShoot", new Vector3(0f, muzzleY + 0.074f, 0.097f));
            MakeAnchor(body.transform, "CapsuleSpawner", new Vector3(0.05f, 0.118f, 0.143f));
            MakeAnchor(body.transform, "LHandIKTarget", new Vector3(0f, muzzleY * 0.45f, 0.0f));
            MakeAnchor(body.transform, "LHandIKTargetAiming", new Vector3(0f, muzzleY * 0.45f, 0.0f));

            // ERST jetzt die Komponenten uebernehmen. Vorher gaebe es weder das
            // Mesh-Objekt noch die Anker, und die Verdrahtung legte fuer jeden
            // Verweis einen Platzhalter im Ursprung an statt die echten Anker zu
            // finden - die Waffe zeigte dann auf leere Objekte an falscher Stelle.
            CopyDonorComponents(donor, root);
            ApplyScale(root);

            // Die Originale aus Resources haben m_IsActive = true. Wird die
            // Vorlage selbst deaktiviert, ist auch jede Instantiate-Kopie
            // deaktiviert - die Waffe existiert dann, wird aber nie eingeschaltet.
            // Also bleibt die Wurzel aktiv und haengt unter einem deaktivierten
            // Halter: activeSelf true, activeInHierarchy false.
            GameObject holder = new GameObject("NextDayRevival_Holder_" + _def.Id);
            holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(holder);
            root.transform.SetParent(holder.transform, false);
            root.SetActive(true);

            RevivalPlugin.L.LogInfo(_def.Id + ": activeSelf=" + root.activeSelf
                                    + " activeInHierarchy=" + root.activeInHierarchy);
            return root;
        }

        static void MakeAnchor(Transform parent, string name, Vector3 pos)
        {
            GameObject a = new GameObject(name);
            a.transform.SetParent(parent, false);
            a.transform.localPosition = pos;
            a.transform.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// Nimmt den Shader einer vorhandenen Waffe und setzt genau die
        /// Eigenschaften, die die Spielmaterialien auch setzen.
        ///
        /// Frueher standen hier _Metallic 0.55 plus eine eigene
        /// Metallic/Gloss-Map samt Keyword. Von 1488 Materialien mit _Metallic
        /// in resources.assets hat kein einziges eine solche Map, und 78 Prozent
        /// stehen auf Metallic 0.0 (osnova am RPD: 0.0 / Glossiness 0.6). Ein
        /// fast spiegelndes Metall nimmt seine Farbe vollstaendig aus der
        /// Umgebungsspiegelung - im Menue also aus der Skybox.
        /// </summary>
        Material MakeMaterial(GameObject donor)
        {
            Shader shader = null;
            try
            {
                if (donor != null)
                {
                    MeshRenderer[] rs = donor.GetComponentsInChildren<MeshRenderer>(true);
                    for (int i = 0; i < rs.Length && shader == null; i++)
                        if (rs[i] != null && rs[i].sharedMaterial != null)
                            shader = rs[i].sharedMaterial.shader;
                    if (shader != null)
                        RevivalPlugin.L.LogInfo(_def.Id + ": Shader von Spende-Waffe: " + shader.name);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Spende-Shader: " + ex.Message); }

            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new Exception("kein brauchbarer Shader gefunden");

            Texture2D diffuse = Assets.Texture(_def.Diffuse, false, true);
            Material mat = new Material(shader);
            mat.name = _def.Name + "_Material";
            mat.mainTexture = diffuse;
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", diffuse);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_Glossiness"))
                mat.SetFloat("_Glossiness", RevivalPlugin.CfgGlossiness.Value);
            if (mat.HasProperty("_Metallic"))
                mat.SetFloat("_Metallic", RevivalPlugin.CfgMetallic.Value);

            // Keine Metallic/Gloss-Map. Falls der geerbte Zustand eine hat oder
            // das Keyword traegt, hier abraeumen.
            if (mat.HasProperty("_MetallicGlossMap")) mat.SetTexture("_MetallicGlossMap", null);
            mat.DisableKeyword("_METALLICGLOSSMAP");

            // Blend-Modus hart auf Opaque: liefert das Spende-Material einen
            // Transparenzmodus mit, wird der sonst mitgeerbt und die Waffe wird
            // stellenweise durchsichtig.
            if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", 1f);   // One
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", 0f);   // Zero
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.black);
            mat.DisableKeyword("_EMISSION");

            int q = 2000 + RevivalPlugin.CfgRenderQueue.Value;   // 2000 = Geometry
            mat.renderQueue = q;

            Texture2D nrm = Assets.Texture(_def.Normal, true, true);
            if (nrm != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", nrm);
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1.0f);
                // Ohne das Keyword wird die Textur zwar gesetzt, aber nicht
                // ausgewertet - die haeufigste Stolperfalle beim Standard-Shader.
                mat.EnableKeyword("_NORMALMAP");
            }

            RevivalPlugin.L.LogInfo(_def.Id + ": Material Metallic="
                + RevivalPlugin.CfgMetallic.Value + " Glossiness="
                + RevivalPlugin.CfgGlossiness.Value + " renderQueue=" + q
                + " Normal=" + (nrm != null));
            return mat;
        }

        /// <summary>
        /// PlayerMenuCustomizationManager.WeaponSpineInstanceManager macht am Ende
        /// Instantiate(prefab).GetComponent&lt;ItemTransformManager&gt;().ApplyLocalTransformData().
        /// Ohne diese Komponente ist GetComponent null und der Aufruf wirft eine
        /// NullReferenceException, die den Charakterbildschirm haengen laesst.
        ///
        /// Kopiert werden nur Wertefelder. UnityEngine.Object-Verweise bleiben
        /// aussen vor, sonst zeigt die neue Waffe auf Kindobjekte der Spende.
        /// </summary>
        void CopyDonorComponents(GameObject donor, GameObject target)
        {
            if (donor == null)
            {
                RevivalPlugin.L.LogWarning(_def.Id + ": keine Spende-Waffe - Komponenten fehlen.");
                return;
            }
            foreach (Component src in donor.GetComponents<Component>())
            {
                if (src == null) continue;
                Type t = src.GetType();
                if (t == typeof(Transform)) continue;
                if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;

                Component dst = target.GetComponent(t);
                if (dst == null) dst = target.AddComponent(t);
                if (dst == null)
                {
                    RevivalPlugin.L.LogWarning("AddComponent fehlgeschlagen: " + t.Name);
                    continue;
                }

                int n = 0;
                Type walk = t;
                while (walk != null && walk != typeof(MonoBehaviour) && walk != typeof(object))
                {
                    FieldInfo[] fs = walk.GetFields(BindingFlags.Instance | BindingFlags.Public
                                                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    foreach (FieldInfo f in fs)
                    {
                        Type ft = f.FieldType;
                        if (typeof(UnityEngine.Object).IsAssignableFrom(ft)) continue;
                        if (!ft.IsValueType && ft != typeof(string)) continue;
                        try { f.SetValue(dst, f.GetValue(src)); n++; }
                        catch { }
                    }
                    walk = walk.BaseType;
                }
                RevivalPlugin.L.LogInfo(_def.Id + ": Komponente uebernommen: " + t.Name
                                        + " (" + n + " Wertefelder)");
                WireReferences(src, dst, t, target, 0);
            }
        }

        /// <summary>
        /// Schreibt die gewuenschte Skalierung in die kopierten
        /// Transform-Komponenten.
        ///
        /// ChangeWeaponHelper ruft WeaponTranformManager::ApplyLocalTransformData,
        /// und das setzt localPosition, localEulerAngles UND localScale der
        /// Wurzel aus den Feldern der Komponente. Was am Prefab steht, ist danach
        /// egal. Deshalb muss der Wert dorthin, nicht an transform.localScale.
        /// </summary>
        void ApplyScale(GameObject root)
        {
            float s = RevivalPlugin.CfgScale.Value;
            if (s <= 0f) s = 0.01f;
            root.transform.localScale = new Vector3(s, s, s);

            string[] names = { "WeaponTranformManager", "ItemTransformManager" };
            for (int i = 0; i < names.Length; i++)
            {
                Type t = AccessTools.TypeByName(names[i]);
                if (t == null) continue;
                Component c = root.GetComponent(t);
                if (c == null) continue;
                FieldInfo f = AccessTools.Field(t, "localScale");
                if (f == null || f.FieldType != typeof(Vector3)) continue;
                try
                {
                    f.SetValue(c, new Vector3(s, s, s));
                    RevivalPlugin.L.LogInfo(_def.Id + ": " + names[i] + ".localScale -> " + s);
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning(_def.Id + ": localScale nicht setzbar: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Zieht Objektverweise vom Spende-Prefab auf die eigenen Kindobjekte um:
        /// der Verweis der Spende zeigt auf ein Kindobjekt mit einem bestimmten
        /// Namen, im eigenen Prefab wird ein Kind gleichen Namens gesucht. Fehlt
        /// es - etwa magaz_l oder Zatvor_l - wird ein leeres Objekt angelegt,
        /// damit der Verweis nicht null bleibt.
        /// </summary>
        void WireReferences(object src, object dst, Type t, GameObject root, int depth)
        {
            if (src == null || dst == null || depth > 2) return;
            Type walk = t;
            while (walk != null && walk != typeof(MonoBehaviour) && walk != typeof(object))
            {
                FieldInfo[] fs = walk.GetFields(BindingFlags.Instance | BindingFlags.Public
                                                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (FieldInfo f in fs)
                {
                    Type ft = f.FieldType;
                    object sv;
                    try { sv = f.GetValue(src); } catch { continue; }

                    if (typeof(Transform).IsAssignableFrom(ft) || typeof(GameObject).IsAssignableFrom(ft))
                    {
                        string wanted = null;
                        Transform st = sv as Transform;
                        GameObject sg = sv as GameObject;
                        if (st != null) wanted = st.name;
                        else if (sg != null) wanted = sg.name;
                        if (wanted == null) continue;

                        Transform mine = FindOrCreate(root, wanted);
                        try
                        {
                            if (typeof(Transform).IsAssignableFrom(ft)) f.SetValue(dst, mine);
                            else f.SetValue(dst, mine.gameObject);
                            if (RevivalPlugin.CfgVerbose.Value)
                                RevivalPlugin.L.LogInfo("      " + f.Name + " -> " + wanted);
                        }
                        catch (Exception ex)
                        {
                            RevivalPlugin.L.LogWarning("      " + f.Name + " nicht setzbar: " + ex.Message);
                        }
                    }
                    else if (sv != null && !ft.IsPrimitive && !ft.IsEnum && ft != typeof(string)
                             && !typeof(UnityEngine.Object).IsAssignableFrom(ft)
                             && (ft.Namespace == null || (!ft.Namespace.StartsWith("System")
                                                          && !ft.Namespace.StartsWith("UnityEngine"))))
                    {
                        // Verschachtelte serialisierbare Klasse, etwa WeaponTransforms
                        object dv;
                        try { dv = f.GetValue(dst); } catch { continue; }
                        if (dv == null)
                        {
                            try { dv = Activator.CreateInstance(ft); f.SetValue(dst, dv); }
                            catch { continue; }
                        }
                        WireReferences(sv, dv, ft, root, depth + 1);
                    }
                }
                walk = walk.BaseType;
            }
        }

        Transform FindOrCreate(GameObject root, string name)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform tr in all)
                if (tr.name == name) return tr;
            if (root.name == name) return root.transform;

            Transform parent = root.transform;
            foreach (Transform tr in all)
                if (tr.name.EndsWith("_LOD0_W")) { parent = tr; break; }

            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            if (RevivalPlugin.CfgVerbose.Value)
                RevivalPlugin.L.LogInfo("      (Platzhalter angelegt: " + name + ")");
            return go.transform;
        }

        // -------------------------------------------------------- Inventar

        /// <summary>
        /// Der Inventareintrag. Geklont vom Spende-Prefab, dann wird in jeder
        /// Komponente das Feld ItemID auf die neue ID gezogen - sonst legt
        /// AddWeaponItemFromValues das Item unter der Spende-ID ab.
        /// </summary>
        public GameObject GetSpawnPrefab(string requestedPrefix)
        {
            if (_spawn != null) return _spawn;
            if (_spawnFailed) return null;
            try
            {
                GameObject donor = LoadDonorSpawn(requestedPrefix);
                if (donor == null)
                {
                    _spawnFailed = true;
                    RevivalPlugin.L.LogError(_def.Id
                        + ": Spende-Inventarprefab in keinem Kandidatenpfad gefunden.");
                    return null;
                }

                GameObject clone = UnityEngine.Object.Instantiate(donor);
                clone.name = _def.Id + "_Spawn";

                Texture2D icon = Assets.Texture(_def.Icon, false, false);
                Texture2D wicon = Assets.Texture(_def.WeaponIcon, false, false);

                int patched = 0;
                Component[] all = clone.GetComponentsInChildren<Component>(true);
                foreach (Component c in all)
                {
                    if (c == null) continue;
                    Type t = c.GetType();
                    while (t != null && t != typeof(object))
                    {
                        FieldInfo fid = t.GetField("ItemID", BindingFlags.Instance
                                                   | BindingFlags.Public | BindingFlags.NonPublic
                                                   | BindingFlags.DeclaredOnly);
                        if (fid == null) { t = t.BaseType; continue; }

                        if (RevivalPlugin.CfgVerbose.Value) DumpFieldNames(t);

                        try
                        {
                            if (fid.FieldType == typeof(int)) fid.SetValue(c, _def.Id);
                            else fid.SetValue(c, Convert.ChangeType(_def.Id, fid.FieldType));
                            patched++;
                        }
                        catch (Exception ex)
                        {
                            RevivalPlugin.L.LogWarning("  ItemID nicht setzbar: " + ex.Message);
                        }

                        SetIcon(c, t, "ItemIcon", icon);
                        SetIcon(c, t, "WeaponIcon", wicon != null ? wicon : icon);
                        if (_mySpawned == null && t.Name == "ItemSpawned") _mySpawned = c;
                        SetInt(c, t, "Bullets", _def.Bullets);
                        if (_def.ClipItemId > 0) SetInt(c, t, "ClipItemID", _def.ClipItemId);
                        SetFloat(c, t, "ItemWeight", _def.Weight);
                        break;
                    }
                }
                if (patched == 0)
                    RevivalPlugin.L.LogWarning(_def.Id
                        + ": kein ItemID-Feld im Inventarprefab gefunden.");

                SwapGeometry(clone);

                GameObject holder = new GameObject("NextDayRevival_SpawnHolder_" + _def.Id);
                holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(holder);
                clone.transform.SetParent(holder.transform, false);
                clone.SetActive(true);

                _spawn = clone;
                RevivalPlugin.L.LogInfo(_def.Id + ": Inventarprefab gebaut ("
                    + all.Length + " Komponenten, " + patched + "x ItemID gesetzt).");
                return _spawn;
            }
            catch (Exception ex)
            {
                _spawnFailed = true;
                RevivalPlugin.L.LogError(_def.Id + ": Inventarprefab fehlgeschlagen: " + ex);
                return null;
            }
        }

        GameObject LoadDonorSpawn(string requestedPrefix)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(requestedPrefix))
                candidates.Add(requestedPrefix + _def.DonorId + "_Spawn");
            for (int i = 0; i < SPAWN_FOLDERS.Length; i++)
                candidates.Add(SPAWN_FOLDERS[i] + _def.DonorId + "_Spawn");

            try
            {
                ResourceHook.Reentry = true;
                for (int i = 0; i < candidates.Count; i++)
                {
                    GameObject go = Resources.Load(candidates[i]) as GameObject;
                    if (go != null)
                    {
                        RevivalPlugin.L.LogInfo(_def.Id + ": Spende-Inventarprefab "
                                                + candidates[i]);
                        return go;
                    }
                    if (RevivalPlugin.CfgVerbose.Value)
                        RevivalPlugin.L.LogInfo("  Kandidat leer: " + candidates[i]);
                }
            }
            finally { ResourceHook.Reentry = false; }
            return null;
        }

        /// <summary>
        /// Der Klon traegt bis hier die Geometrie der Spende. Ohne Austausch liegt
        /// im Rucksack sichtbar das Spende-Item. Also alle MeshFilter des Klons
        /// auf das eigene Mesh umhaengen - Hierarchie, Skalierung und Ausrichtung
        /// des Original-Prefabs bleiben dabei erhalten. Genau deshalb sind die
        /// Meshes der Munitionsitems in ammo_mesh.py auf die Ausdehnung UND den
        /// Mittelpunkt von magaz_l gezogen: der Collider des Originals bleibt.
        /// </summary>
        void SwapGeometry(GameObject clone)
        {
            Mesh mesh = _mesh;
            if (mesh == null) mesh = Assets.Load(_def.Mesh);
            if (mesh == null) return;

            Material mat = _mat;
            if (mat == null)
            {
                mat = MakeMaterial(clone);
                _mat = mat;
            }

            int swapped = 0;
            MeshFilter[] mfs = clone.GetComponentsInChildren<MeshFilter>(true);
            foreach (MeshFilter mf in mfs)
            {
                if (mf == null) continue;
                mf.sharedMesh = mesh;
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mr != null && mat != null)
                {
                    Material[] mats = new Material[Math.Max(1, mr.sharedMaterials.Length)];
                    for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                    mr.sharedMaterials = mats;
                }
                swapped++;
            }
            SkinnedMeshRenderer[] sks = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer sk in sks)
                if (sk != null) sk.sharedMesh = mesh;

            RevivalPlugin.L.LogInfo(_def.Id + ": Inventar-Geometrie ersetzt ("
                + swapped + " MeshFilter, Skalierung " + clone.transform.localScale + ")");
        }

        static void DumpFieldNames(Type t)
        {
            RevivalPlugin.L.LogInfo("Inventarkomponente " + t.Name + ":");
            FieldInfo[] every = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (FieldInfo any in every)
                RevivalPlugin.L.LogInfo("    " + any.FieldType.Name + " " + any.Name);
        }

        static void SetInt(Component c, Type t, string name, int value)
        {
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f == null) return;
                if (f.FieldType == typeof(int)) f.SetValue(c, value);
                else f.SetValue(c, Convert.ChangeType(value, f.FieldType));
                RevivalPlugin.L.LogInfo("  " + name + " -> " + value);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("  " + name + ": " + ex.Message); }
        }

        static void SetFloat(Component c, Type t, string name, float value)
        {
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f == null) return;
                if (f.FieldType == typeof(float)) f.SetValue(c, value);
                else f.SetValue(c, Convert.ChangeType(value, f.FieldType));
                RevivalPlugin.L.LogInfo("  " + name + " -> " + value);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("  " + name + ": " + ex.Message); }
        }

        /// <summary>
        /// Das Feld heisst ItemIcon oder WeaponIcon; ob es ein Sprite oder eine
        /// Texture haelt, entscheidet sich erst zur Laufzeit. Beides wird bedient.
        /// </summary>
        static void SetIcon(Component c, Type t, string name, Texture2D tex)
        {
            if (tex == null) return;
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f == null) return;

                if (typeof(Sprite).IsAssignableFrom(f.FieldType))
                {
                    Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                              new Vector2(0.5f, 0.5f), 100f);
                    sp.name = tex.name;
                    f.SetValue(c, sp);
                    RevivalPlugin.L.LogInfo("  " + name + " -> Sprite " + tex.name);
                }
                else if (typeof(Texture).IsAssignableFrom(f.FieldType))
                {
                    f.SetValue(c, tex);
                    RevivalPlugin.L.LogInfo("  " + name + " -> Texture " + tex.name);
                }
                else
                {
                    RevivalPlugin.L.LogWarning("  " + name + " hat unerwarteten Typ "
                                               + f.FieldType.FullName);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("  " + name + ": " + ex.Message); }
        }
    }

    // ------------------------------------------------------------ Registry

    /// <summary>
    /// Traegt die neuen IDs in die Item-Datenbank ein.
    ///
    /// Es gibt MEHRERE Nachschlagewerke, nicht nur eines:
    ///   GetItemByID              -> DeserealizedSpawnDictionary
    ///   GetItemSpawnedScriptByID -> ItemSpawnedDictionary
    ///   GetRandomItemByCategory  -> SpawnCategoriesDictionary
    /// Der Waffenslot benutzt das erste, der Rucksack das zweite, der Loot das
    /// dritte. Wer nur eines befuellt, bekommt "ItemSpawned is null!" beim
    /// Umlegen und das Item verschwindet.
    /// </summary>
    public static class Registry
    {
        public static void RegisterAll()
        {
            try
            {
                object db = GetDb();
                if (db == null) return;
                Type t = db.GetType();

                FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                                 | BindingFlags.NonPublic);
                int filled = 0, total = 0;
                foreach (FieldInfo f in fields)
                {
                    IDictionary dic = GetDic(db, f);
                    if (dic == null) continue;
                    if (f.Name == "SpawnCategoriesDictionary") continue;   // andere Schluessel
                    total++;

                    List<ItemDef> items = RevivalPlugin.Items;
                    int done = 0;
                    for (int i = 0; i < items.Count; i++)
                    {
                        ItemDef d = items[i];
                        if (dic.Contains(d.Id)) { done++; continue; }
                        if (!dic.Contains(d.DonorId)) continue;

                        object value = dic[d.DonorId];
                        string via = "Spende-Verweis";
                        Component mine = d.Factory.MySpawned;
                        if (mine != null)
                        {
                            Type want = value == null ? null : value.GetType();
                            if (want == null || want.IsInstanceOfType(mine))
                            {
                                value = mine;
                                via = "eigenes ItemSpawned";
                            }
                        }
                        try
                        {
                            dic[d.Id] = value;
                            done++;
                            RevivalPlugin.L.LogInfo("Item-DB " + f.Name + ": " + d.Id
                                                    + " angelegt (" + via + ").");
                        }
                        catch (Exception ex)
                        {
                            RevivalPlugin.L.LogWarning("Item-DB " + f.Name + ": " + d.Id
                                                       + " nicht eintragbar: " + ex.Message);
                        }
                    }
                    if (done == items.Count) filled++;
                    RevivalPlugin.L.LogInfo("Item-DB " + f.Name + ": " + dic.Count
                                            + " Eintraege, " + done + "/" + items.Count
                                            + " eigene vorhanden.");
                }
                RevivalPlugin.L.LogInfo("Item-DB: " + filled + " von " + total
                                        + " Woerterbuechern vollstaendig.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Item-DB-Registrierung: " + ex); }
        }

        /// <summary>
        /// Haengt die neuen IDs in die Loot-Kategorien, in denen auch das
        /// Spende-Item steht. GetRandomItemByCategory zieht aus
        /// SpawnCategoriesDictionary[kategorie].SpawnPrefabs - liegt die neue ID
        /// dort nicht drin, wird sie in der Welt nie gespawnt.
        /// </summary>
        public static void AddToLootTables()
        {
            try
            {
                object db = GetDb();
                if (db == null) return;
                FieldInfo f = AccessTools.Field(db.GetType(), "SpawnCategoriesDictionary");
                IDictionary dic = f == null ? null : GetDic(db, f);
                if (dic == null) { RevivalPlugin.L.LogWarning("Loot: keine Kategorietabelle."); return; }

                List<ItemDef> items = RevivalPlugin.Items;
                int added = 0;
                foreach (object key in dic.Keys)
                {
                    object table = dic[key];
                    if (table == null) continue;
                    FieldInfo fp = AccessTools.Field(table.GetType(), "SpawnPrefabs");
                    IList list = fp == null ? null : fp.GetValue(table) as IList;
                    if (list == null) continue;

                    for (int i = 0; i < items.Count; i++)
                    {
                        ItemDef d = items[i];
                        object donorEntry = null;
                        bool already = false;
                        for (int k = 0; k < list.Count; k++)
                        {
                            int id = EntryId(list[k]);
                            if (id == d.Id) { already = true; break; }
                            if (id == d.DonorId) donorEntry = list[k];
                        }
                        if (already || donorEntry == null) continue;

                        object copy = CloneEntry(donorEntry, d.Id);
                        if (copy == null) continue;
                        try { list.Add(copy); added++; }
                        catch (Exception ex)
                        {
                            RevivalPlugin.L.LogWarning("Loot: " + d.Id + " nicht eintragbar: "
                                                       + ex.Message);
                        }
                    }
                }
                RevivalPlugin.L.LogInfo("Loot-Kategorien: " + added + " Eintraege ergaenzt ("
                                        + dic.Count + " Kategorien geprueft).");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Loot-Kategorien: " + ex); }
        }

        static int EntryId(object entry)
        {
            if (entry == null) return -1;
            FieldInfo f = AccessTools.Field(entry.GetType(), "ItemID");
            if (f == null) return -1;
            try { return Convert.ToInt32(f.GetValue(entry)); }
            catch { return -1; }
        }

        static object CloneEntry(object src, int newId)
        {
            try
            {
                Type t = src.GetType();
                object copy = Activator.CreateInstance(t);
                FieldInfo[] fs = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                             | BindingFlags.NonPublic);
                foreach (FieldInfo f in fs)
                {
                    try { f.SetValue(copy, f.GetValue(src)); }
                    catch { }
                }
                FieldInfo fid = AccessTools.Field(t, "ItemID");
                if (fid != null)
                {
                    if (fid.FieldType == typeof(int)) fid.SetValue(copy, newId);
                    else fid.SetValue(copy, Convert.ChangeType(newId, fid.FieldType));
                }
                // Der Pfad zeigt weiter auf das Spende-Prefab. Unser
                // Resources.Load-Prefix matcht aber auf die Endung <id>_Spawn,
                // also muss die ID auch im Pfad stehen.
                FieldInfo fp = AccessTools.Field(t, "Path");
                if (fp != null && fp.FieldType == typeof(string))
                {
                    string p = fp.GetValue(src) as string;
                    if (!string.IsNullOrEmpty(p))
                        fp.SetValue(copy, p.Replace(EntryIdString(src), newId.ToString()));
                }
                return copy;
            }
            catch { return null; }
        }

        static string EntryIdString(object entry)
        {
            int id = EntryId(entry);
            return id < 0 ? "" : id.ToString();
        }

        static IDictionary GetDic(object db, FieldInfo f)
        {
            object holder;
            try { holder = f.GetValue(db); } catch { return null; }
            if (holder == null) return null;
            PropertyInfo pDic = AccessTools.Property(holder.GetType(), "Dic");
            if (pDic == null) return null;
            try { return pDic.GetValue(holder, null) as IDictionary; }
            catch { return null; }
        }

        static object GetDb()
        {
            Type t = AccessTools.TypeByName("ItemSpawnCategoriesDB");
            if (t == null) { RevivalPlugin.L.LogWarning("ItemSpawnCategoriesDB nicht gefunden."); return null; }
            MethodInfo cur = AccessTools.PropertyGetter(t, "current");
            if (cur == null) { RevivalPlugin.L.LogWarning("ItemSpawnCategoriesDB.current fehlt."); return null; }
            ResourceHook.Reentry = true;
            try { return cur.Invoke(null, null); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("current: " + ex.Message); return null; }
            finally { ResourceHook.Reentry = false; }
        }
    }

    // -------------------------------------------------------- Waffendaten

    /// <summary>Liest die aus weapons_db.xml geparsten Werte zurueck.</summary>
    public static class WeaponData
    {
        public static bool Available(int itemId)
        {
            try { return Get(itemId) != null; }
            catch { return false; }
        }

        public static object Get(int itemId)
        {
            Type t = AccessTools.TypeByName("xmlItemsDataManager");
            if (t == null) return null;
            MethodInfo inst = AccessTools.PropertyGetter(t, "Instance");
            object mgr = inst == null ? null : inst.Invoke(null, null);
            if (mgr == null) return null;

            MethodInfo get = null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public
                                                  | BindingFlags.NonPublic))
            {
                if (m.Name != "GetFirearmWeaponData") continue;
                if (m.GetParameters().Length == 1) { get = m; break; }
            }
            if (get == null) return null;

            ParameterInfo pi = get.GetParameters()[0];
            object[] args = new object[1];
            args[0] = pi.ParameterType == typeof(int)
                    ? (object)itemId
                    : Convert.ChangeType(itemId, pi.ParameterType);
            return get.Invoke(mgr, args);
        }

        /// <summary>Die Handvoll Werte, auf die es beim Einbau ankommt.</summary>
        public static void Summary(int itemId)
        {
            try
            {
                object data = Get(itemId);
                if (data == null)
                {
                    RevivalPlugin.L.LogWarning("WAFFENDATEN " + itemId
                        + ": NULL - Eintrag fehlt in weapons_db.xml!");
                    return;
                }
                RevivalPlugin.L.LogInfo("WAFFENDATEN " + itemId
                    + "  MaxBullets=" + F(data, "MaxBullets")
                    + "  Damage=" + F(data, "Damage")
                    + "  rateOfFire=" + F(data, "rateOfFire")
                    + "  ReloadTime=" + F(data, "ReloadTime")
                    + "  Modes=" + F(data, "_shootModes")
                    + "  Cal=" + F(data, "Cal")
                    + "  Scope=" + F(data, "Scope")
                    + "  ScopeFOV=" + F(data, "ScopeFOV")
                    + "  Clips=" + Arr(Field(Field(data, "WeaponAmmo"), "Clips"), 8));
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Waffendaten " + itemId + ": " + ex.Message); }
        }

        static object Field(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            while (t != null && t != typeof(object))
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) { try { return f.GetValue(o); } catch { return null; } }
                t = t.BaseType;
            }
            return null;
        }

        static string F(object o, string name)
        {
            object v = Field(o, name);
            return v == null ? "null" : v.ToString();
        }

        internal static string Arr(object arr, int max)
        {
            IEnumerable e = arr as IEnumerable;
            if (e == null) return arr == null ? "null" : arr.ToString();
            System.Text.StringBuilder sb = new System.Text.StringBuilder("[");
            int n = 0;
            foreach (object x in e)
            {
                if (n > 0) sb.Append(", ");
                sb.Append(x == null ? "null" : x.ToString());
                if (++n >= max) { sb.Append(", ..."); break; }
            }
            return sb.Append("]").ToString();
        }
    }

    // ---------------------------------------------------------- Diagnose

    /// <summary>
    /// Diagnose statt Vermutung. ReloadWeapon prueft der Reihe nach mehrere
    /// Bedingungen und kehrt still zurueck, sobald eine zutrifft; der Rucksack
    /// gibt beim Einlegen zurueck, was die Datenbank fuer eine ID hergibt.
    /// Beide Stellen protokollieren hier, was sie sehen.
    /// </summary>
    public static class Diag
    {
        static int _reload, _backpack;

        public static void ReloadPrefix(object __instance)
        {
            if (_reload >= 6) return;
            _reload++;
            ManualLogSource L = RevivalPlugin.L;
            try
            {
                object wd = F(__instance, "_weaponFirearmData");
                object inv = F(__instance, "_plrInventoryManager");
                L.LogInfo("=== ReloadWeapon (#" + _reload + ")");
                if (wd != null)
                    L.LogInfo("    ItemID=" + F(wd, "ItemID") + " MaxBullets=" + F(wd, "MaxBullets")
                              + " Clips=" + WeaponData.Arr(F(F(wd, "WeaponAmmo"), "Clips"), 8));
                if (inv != null)
                {
                    object wpn = F(inv, "_weaponsData");
                    object bp = F(inv, "_backpackData");
                    L.LogInfo("    Slot=" + F(wpn, "CurrentSlotID")
                              + " WaffenIDs=" + WeaponData.Arr(F(wpn, "ItemID"), 8)
                              + " Bullets=" + WeaponData.Arr(F(wpn, "Bullets"), 8));
                    L.LogInfo("    Rucksack=" + WeaponData.Arr(F(bp, "ItemID"), 20));
                }
            }
            catch (Exception ex) { L.LogError("Reload-Diagnose: " + ex.Message); }
        }

        // __args faengt ALLE Parameter ab, unabhaengig von Position und Namen.
        public static void BackpackPrefix(object[] __args)
        {
            if (_backpack >= 12) return;
            ManualLogSource L = RevivalPlugin.L;
            try
            {
                int itemId = -1;
                if (__args.Length > 0 && __args[0] is int) itemId = (int)__args[0];
                ItemDef mine = null;
                List<ItemDef> items = RevivalPlugin.Items;
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Id == itemId) { mine = items[i]; break; }
                if (mine == null && !RevivalPlugin.CfgVerbose.Value) return;

                _backpack++;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < __args.Length; i++)
                    sb.Append(i > 0 ? ", " : "").Append(__args[i] == null ? "null" : __args[i].ToString());
                L.LogInfo("=== AddBackpackItemFromValues(" + sb + ")");
                if (mine == null) return;

                Type dm = AccessTools.TypeByName("ItemSpawnCategoriesDB");
                MethodInfo cur = dm == null ? null : AccessTools.PropertyGetter(dm, "current");
                object db = cur == null ? null : cur.Invoke(null, null);
                if (db == null) { L.LogWarning("  DB NULL"); return; }

                MethodInfo get = AccessTools.Method(db.GetType(), "GetItemSpawnedScriptByID", null, null);
                object res = get == null ? null : get.Invoke(db, new object[] { itemId });
                if (res == null) { L.LogWarning("  GetItemSpawnedScriptByID liefert NULL"); return; }

                Component comp = res as Component;
                L.LogInfo("  liefert " + res.GetType().Name + " auf '"
                          + (comp == null ? "?" : comp.gameObject.name) + "', ist es meins? "
                          + (ReferenceEquals(res, mine.Factory.MySpawned) ? "JA" : "NEIN"));
                if (comp != null)
                {
                    MeshFilter mf = comp.GetComponentInChildren<MeshFilter>(true);
                    L.LogInfo("  erstes Mesh = " + (mf == null || mf.sharedMesh == null
                                                    ? "keins" : mf.sharedMesh.name));
                }
            }
            catch (Exception ex) { L.LogError("Rucksack-Diagnose: " + ex.Message); }
        }

        static object F(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            while (t != null && t != typeof(object))
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) { try { return f.GetValue(o); } catch { return null; } }
                t = t.BaseType;
            }
            return null;
        }
    }
    // ------------------------------------------------------------ Research

    /// <summary>
    /// Erkundungswerkzeug fuer die Frage, was mit Regionen und Szenen geht.
    ///
    /// WAS IM CLIENT STEHT
    /// -------------------
    /// GameRegionsManager laedt ScriptableObjects/GameRegions, ein
    /// GameRegionsData mit einem Array GameRegionData[] RegionsList. Jeder
    /// Eintrag hat region, startScene und List&lt;int&gt; scenes - alles reine
    /// Buildindizes aus den BuildSettings.
    ///
    /// Der Release-Datensatz enthaelt genau EINE Region:
    ///     region 0 Severoufimsk, startScene 5, scenes [5, 6, 9, 7, 13, 14]
    ///
    /// Die BuildSettings kennen aber 19 Szenen. Nicht benutzt und trotzdem
    /// vollstaendig im Build: 3 Bunker_A65, 4 GW_Scene_2, 18 Underground_Lab
    /// sowie die Chunks 0, 2, 3, 4, 7, 8, 9.
    ///
    /// Gereist wird ueber GameLocationChangeManager::ChangeGameLocation, das
    /// einen LocationChangeTrigger erwartet. In GenerateServerOptions steht die
    /// entscheidende Zeile: bei LocationaChangeType == 2 wird _gameScene direkt
    /// aus trigger.SubLocation gesetzt. SubLocation IST also der Buildindex.
    ///
    /// Beides zusammen heisst: eine Szene laesst sich anspringen, sobald sie in
    /// der Szenenliste ihrer Region steht - denn GetRegionDataAtScene liefert
    /// sonst null, und JoinRoom laeuft in eine NullReference.
    ///
    /// Standardmaessig ist hier alles aus. Es ist ein Werkzeug zum Nachsehen,
    /// keine Spielfunktion.
    /// </summary>
    public static class Research
    {
        static bool _armed;
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;

        public static void ReportRegions()
        {
            try
            {
                object data = GetRegionsData();
                if (data == null) { RevivalPlugin.L.LogInfo("Regionen: kein GameRegionsData."); return; }
                FieldInfo fl = AccessTools.Field(data.GetType(), "RegionsList");
                IEnumerable list = fl == null ? null : fl.GetValue(data) as IEnumerable;
                if (list == null) { RevivalPlugin.L.LogInfo("Regionen: keine RegionsList."); return; }

                RevivalPlugin.L.LogInfo("--- Regionen ---");
                foreach (object r in list)
                {
                    if (r == null) continue;
                    Type rt = r.GetType();
                    object region = Val(rt, r, "region");
                    object start = Val(rt, r, "startScene");
                    object scenes = Val(rt, r, "scenes");
                    RevivalPlugin.L.LogInfo("  region=" + region + " startScene=" + start
                                            + " scenes=" + WeaponData.Arr(scenes, 32));
                }
                RevivalPlugin.L.LogInfo("  Buildindizes: 3 Bunker_A65, 4 GW_Scene_2, "
                    + "5 GW_Scene_3, 6 Catacombs, 7 GW_Scene_1, 8..17 Chunk 0..9, "
                    + "18 Underground_Lab");
                RevivalPlugin.L.LogInfo("--- Regionen Ende ---");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Regionen: " + ex.Message); }
        }

        /// <summary>Haengt die konfigurierten Buildindizes an die Szenenliste von Region 0.</summary>
        public static void ApplyExtraScenes()
        {
            string cfg = RevivalPlugin.CfgExtraScenes.Value;
            if (string.IsNullOrEmpty(cfg)) return;
            try
            {
                object data = GetRegionsData();
                FieldInfo fl = data == null ? null : AccessTools.Field(data.GetType(), "RegionsList");
                IEnumerable list = fl == null ? null : fl.GetValue(data) as IEnumerable;
                if (list == null) { RevivalPlugin.L.LogWarning("ExtraScenes: keine RegionsList."); return; }

                object first = null;
                foreach (object r in list) { first = r; break; }
                if (first == null) { RevivalPlugin.L.LogWarning("ExtraScenes: Region 0 fehlt."); return; }

                IList scenes = Val(first.GetType(), first, "scenes") as IList;
                if (scenes == null) { RevivalPlugin.L.LogWarning("ExtraScenes: keine Szenenliste."); return; }

                string[] parts = cfg.Split(',');
                int added = 0;
                for (int i = 0; i < parts.Length; i++)
                {
                    string p = parts[i].Trim();
                    if (p.Length == 0) continue;
                    int idx;
                    try { idx = Convert.ToInt32(p); }
                    catch { RevivalPlugin.L.LogWarning("ExtraScenes: kein Zahlenwert: " + p); continue; }
                    if (scenes.Contains(idx)) continue;
                    scenes.Add(idx);
                    added++;
                    RevivalPlugin.L.LogInfo("ExtraScenes: Szene " + idx + " zu Region 0 ergaenzt.");
                }
                RevivalPlugin.L.LogInfo("ExtraScenes: " + added + " Szenen ergaenzt, Liste jetzt "
                                        + WeaponData.Arr(scenes, 32));
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("ExtraScenes: " + ex.Message); }
        }

        public static void Tick()
        {
            if (RevivalPlugin.CfgSceneJump == null || !RevivalPlugin.CfgSceneJump.Value) return;
            if (!_keyParsed)
            {
                _keyParsed = true;
                try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), RevivalPlugin.CfgJumpKey.Value, true); }
                catch { _key = KeyCode.None; }
                if (_key == KeyCode.None)
                    RevivalPlugin.L.LogWarning("JumpKey ist kein KeyCode: "
                                               + RevivalPlugin.CfgJumpKey.Value);
                else if (!_armed)
                {
                    _armed = true;
                    RevivalPlugin.L.LogInfo("Szenenwechsel scharf: " + _key + " -> Szene "
                                            + RevivalPlugin.CfgJumpScene.Value);
                }
            }
            if (_key == KeyCode.None) return;
            if (!Input.GetKeyDown(_key)) return;
            Jump(RevivalPlugin.CfgJumpScene.Value);
        }

        /// <summary>
        /// Baut einen LocationChangeTrigger zusammen und uebergibt ihn an
        /// ChangeGameLocation. Typ 2 heisst Unterort, und dann ist SubLocation
        /// direkt der Buildindex der Zielszene.
        /// </summary>
        public static void Jump(int buildIndex)
        {
            try
            {
                Type tTrig = AccessTools.TypeByName("LocationChangeTrigger");
                Type tMgr = AccessTools.TypeByName("GameLocationChangeManager");
                if (tTrig == null || tMgr == null)
                {
                    RevivalPlugin.L.LogWarning("Szenenwechsel: Typen nicht gefunden.");
                    return;
                }

                UnityEngine.Object mgrObj = UnityEngine.Object.FindObjectOfType(tMgr);
                if (mgrObj == null)
                {
                    RevivalPlugin.L.LogWarning("Szenenwechsel: kein GameLocationChangeManager in der Szene.");
                    return;
                }

                GameObject go = new GameObject("NextDayRevival_Jump");
                Component trig = go.AddComponent(tTrig);
                if (trig == null)
                {
                    UnityEngine.Object.Destroy(go);
                    RevivalPlugin.L.LogWarning("Szenenwechsel: Trigger nicht anlegbar.");
                    return;
                }
                SetField(tTrig, trig, "Id", 9000 + buildIndex);
                SetField(tTrig, trig, "LocationaChangeType", 2);
                SetField(tTrig, trig, "SubLocation", buildIndex);
                SetField(tTrig, trig, "ShowOnMapUI", false);

                MethodInfo m = AccessTools.Method(tMgr, "ChangeGameLocation", null, null);
                if (m == null)
                {
                    UnityEngine.Object.Destroy(go);
                    RevivalPlugin.L.LogWarning("Szenenwechsel: ChangeGameLocation nicht gefunden.");
                    return;
                }
                RevivalPlugin.L.LogInfo("Szenenwechsel nach Buildindex " + buildIndex + " ...");
                m.Invoke(mgrObj, new object[] { trig });
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Szenenwechsel: " + ex); }
        }

        static void SetField(Type t, object o, string name, object value)
        {
            FieldInfo f = AccessTools.Field(t, name);
            if (f == null) return;
            try
            {
                if (f.FieldType.IsEnum) f.SetValue(o, Enum.ToObject(f.FieldType, value));
                else f.SetValue(o, Convert.ChangeType(value, f.FieldType));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("  Trigger." + name + ": " + ex.Message);
            }
        }

        static object Val(Type t, object o, string name)
        {
            FieldInfo f = AccessTools.Field(t, name);
            if (f == null) return null;
            try { return f.GetValue(o); } catch { return null; }
        }

        static object GetRegionsData()
        {
            Type t = AccessTools.TypeByName("GameRegionsManager");
            if (t == null) return null;
            UnityEngine.Object mgr = UnityEngine.Object.FindObjectOfType(t);
            if (mgr == null) return null;
            FieldInfo f = AccessTools.Field(t, "_gameRegionsData");
            object data = f == null ? null : f.GetValue(mgr);
            if (data == null)
            {
                MethodInfo setup = AccessTools.Method(t, "SetupGameRegionsData", null, null);
                if (setup != null)
                {
                    ResourceHook.Reentry = true;
                    try { setup.Invoke(mgr, null); }
                    catch { }
                    finally { ResourceHook.Reentry = false; }
                    data = f == null ? null : f.GetValue(mgr);
                }
            }
            return data;
        }
    }
}
