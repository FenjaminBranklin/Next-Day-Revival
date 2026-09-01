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
using System.Globalization;
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
        // Muss mit der Datei VERSION im Wurzelverzeichnis uebereinstimmen -
        // verify.py prueft das. Zwei Staende, die sich beide "0.3.0" nennen,
        // machen jeden Versionsabgleich wertlos, und genau das war zwischen
        // dem Release 0.3.0 und dem Stand vom 2026-08-28 der Fall.
        public const string VERSION = "0.5.13";

        internal static ManualLogSource L;
        internal static string AssetDir;

        /// <summary>
        /// Memoized AccessTools.TypeByName.
        ///
        /// AccessTools.TypeByName has NO cache of its own (HarmonyX 2.9:
        /// AccessTools carries exactly one static dictionary, and it is for
        /// event handlers). For any type that Type.GetType cannot resolve -
        /// which is every game type and every Unity type outside the four
        /// modules build.ps1 references - it falls through to
        /// AllTypes().FirstOrDefault(t => t.FullName == name). That walks
        /// Assembly.GetTypes() of all 93 loaded assemblies, about 16,700
        /// types, materializing a Type[] per assembly and a FullName string
        /// per type. It costs milliseconds, every single call.
        ///
        /// Harmless where it is what it looks like - a lookup during setup.
        /// Fatal in a loop that runs per frame or per physics step: three of
        /// these per FixedUpdate is enough to push a physics step past its own
        /// budget, and then Unity's catch-up pins the whole game at
        /// 1 / Time.maximumDeltaTime = 3 FPS. That is what the first patrol
        /// did (EXPERIMENTS.md, 2026-08-30).
        ///
        /// Only hits are cached. A miss stays slow, deliberately: a type that
        /// is absent now may belong to an assembly that loads later, and a
        /// cached null would make that absence permanent.
        /// </summary>
        static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        internal static Type TypeByName(string name)
        {
            Type t;
            if (_typeCache.TryGetValue(name, out t)) return t;
            t = AccessTools.TypeByName(name);
            if (t != null) _typeCache[name] = t;
            return t;
        }

        internal static ConfigEntry<bool> CfgCursorFix;
        internal static ConfigEntry<bool> CfgConfine;
        internal static ConfigEntry<bool> CfgCustomItems;
        internal static ConfigEntry<bool> CfgLootTables;
        internal static ConfigEntry<float> CfgScale;
        internal static ConfigEntry<float> CfgMetallic;
        internal static ConfigEntry<float> CfgGlossiness;
        internal static ConfigEntry<int> CfgRenderQueue;
        internal static ConfigEntry<bool> CfgNoCold;
        internal static ConfigEntry<bool> CfgVerbose;
        internal static ConfigEntry<bool> CfgNetWatch;
        internal static ConfigEntry<float> CfgNetWatchHitch;
        internal static ConfigEntry<float> CfgNetWatchEvery;
        internal static ConfigEntry<int> CfgPhotonTimeout;
        internal static ConfigEntry<bool> CfgSceneJump;
        internal static ConfigEntry<int> CfgJumpScene;
        internal static ConfigEntry<int> CfgJumpRegion;
        internal static ConfigEntry<string> CfgJumpKey;
        internal static ConfigEntry<bool> CfgNewRegion;
        internal static ConfigEntry<int> CfgNewRegionId;
        internal static ConfigEntry<string> CfgNewRegionName;
        internal static ConfigEntry<int> CfgNewRegionStart;
        internal static ConfigEntry<string> CfgNewRegionScenes;
        internal static ConfigEntry<bool> CfgNewRegionExclusive;
        internal static ConfigEntry<bool> CfgTurret;
        internal static ConfigEntry<string> CfgTurretKey;
        internal static ConfigEntry<float> CfgTurretDamage;
        internal static ConfigEntry<float> CfgTurretRange;
        internal static ConfigEntry<float> CfgTurretDelay;
        internal static ConfigEntry<float> CfgTurretTurnSpeed;
        internal static ConfigEntry<float> CfgTurretPitchMin;
        internal static ConfigEntry<float> CfgTurretPitchMax;
        internal static ConfigEntry<bool> CfgTurretAmmo;
        internal static ConfigEntry<int> CfgTurretAmmoId;
        internal static ConfigEntry<int> CfgTurretSpawnAmmo;
        internal static ConfigEntry<bool> CfgTurretAmmoBackpack;
        internal static ConfigEntry<int> CfgTurretEventCode;
        internal static ConfigEntry<bool> CfgTurretSound;
        internal static ConfigEntry<float> CfgTurretSoundVolume;
        internal static ConfigEntry<float> CfgTurretSoundRange;
        internal static ConfigEntry<float> CfgTurretSensitivity;
        internal static ConfigEntry<float> CfgTurretRecoil;
        internal static ConfigEntry<float> CfgTurretEyeForward;
        internal static ConfigEntry<float> CfgTurretEyeUp;
        internal static ConfigEntry<float> CfgTurretEyeSide;
        internal static ConfigEntry<bool> CfgTurretCrosshair;
        internal static ConfigEntry<bool> CfgTurretTakeCamera;
        internal static ConfigEntry<float> CfgTurretFov;
        internal static ConfigEntry<bool> CfgTurretInvertX;
        internal static ConfigEntry<bool> CfgTurretScope;
        internal static ConfigEntry<bool> CfgTurretScopeOverlay;
        internal static ConfigEntry<float> CfgTurretAimLead;
        internal static ConfigEntry<float> CfgTurretSeatX;
        internal static ConfigEntry<float> CfgTurretSeatY;
        internal static ConfigEntry<float> CfgTurretSeatZ;
        internal static ConfigEntry<bool> CfgTank;
        internal static ConfigEntry<string> CfgTankKey;
        internal static ConfigEntry<bool> CfgTankSwapMesh;
        internal static ConfigEntry<int> CfgTankSeats;
        internal static ConfigEntry<float> CfgTankDamage;
        internal static ConfigEntry<float> CfgTankExplosionDamage;
        internal static ConfigEntry<float> CfgTankExplosionRadius;
        internal static ConfigEntry<float> CfgTankDelay;
        internal static ConfigEntry<float> CfgTankRange;
        internal static ConfigEntry<float> CfgTankTurnSpeed;
        internal static ConfigEntry<float> CfgTankPitchMin;
        internal static ConfigEntry<float> CfgTankPitchMax;
        internal static ConfigEntry<float> CfgTankFov;
        internal static ConfigEntry<int> CfgTankAmmoId;
        internal static ConfigEntry<int> CfgTankSpawnAmmo;
        internal static ConfigEntry<bool> CfgTankScope;
        internal static ConfigEntry<bool> CfgTankExplosion;
        internal static ConfigEntry<bool> CfgSpawnCar;
        internal static ConfigEntry<string> CfgSpawnCarKey;
        internal static ConfigEntry<string> CfgSpawnCarName;
        internal static ConfigEntry<float> CfgSpawnCarDistance;
        internal static ConfigEntry<bool> CfgPatrol;
        internal static ConfigEntry<string> CfgPatrolKey;
        internal static ConfigEntry<string> CfgPatrolRecordKey;
        internal static ConfigEntry<string> CfgPatrolAutoKey;
        internal static ConfigEntry<string> CfgPatrolFile;
        internal static ConfigEntry<string> CfgPatrolRoute;
        internal static ConfigEntry<string> CfgPatrolVehicle;
        internal static ConfigEntry<int> CfgPatrolMax;
        internal static ConfigEntry<bool> CfgPatrolAuto;
        internal static ConfigEntry<float> CfgPatrolRespawn;
        internal static ConfigEntry<bool> CfgPatrolGun;
        internal static ConfigEntry<float> CfgPatrolGunRange;
        internal static ConfigEntry<float> CfgPatrolGunEffective;
        internal static ConfigEntry<float> CfgPatrolGunNotice;
        internal static ConfigEntry<float> CfgPatrolGunForget;
        internal static ConfigEntry<float> CfgPatrolGunAccuracy;
        internal static ConfigEntry<float> CfgPatrolGunDamage;
        internal static ConfigEntry<float> CfgPatrolGunTankDamage;
        internal static ConfigEntry<int> CfgPatrolGunBurst;
        internal static ConfigEntry<float> CfgPatrolGunBurstPause;
        internal static ConfigEntry<bool> CfgPatrolCrew;
        internal static ConfigEntry<int> CfgPatrolCrewMax;
        internal static ConfigEntry<float> CfgPatrolCrewHealth;
        internal static ConfigEntry<int> CfgPatrolCrewLevel;
        internal static ConfigEntry<float> CfgPatrolWreck;
        internal static ConfigEntry<float> CfgPatrolSpeed;
        internal static ConfigEntry<float> CfgPatrolRecordSeconds;
        internal static ConfigEntry<float> CfgPatrolPassRadius;
        internal static ConfigEntry<float> CfgPatrolStuck;
        internal static ConfigEntry<float> CfgPatrolRam;
        internal static ConfigEntry<float> CfgPatrolFree;
        internal static ConfigEntry<string> CfgPatrolFraction;
        internal static ConfigEntry<string> CfgPatrolEditorKey;
        internal static ConfigEntry<float> CfgPatrolGunPointBlank;
        internal static ConfigEntry<float> CfgPatrolShellDamage;
        internal static ConfigEntry<float> CfgPatrolShellRadius;
        internal static ConfigEntry<bool> CfgPatrolCrush;
        internal static ConfigEntry<float> CfgPatrolCrushHeight;
        internal static ConfigEntry<float> CfgPatrolCrushWidth;
        internal static ConfigEntry<float> CfgPatrolCrewSensor;
        internal static ConfigEntry<float> CfgPatrolRouteMapWidth;
        internal static ConfigEntry<bool> CfgPatrolCrewDrone;
        internal static ConfigEntry<float> CfgPatrolCrewDroneDelay;
        internal static ConfigEntry<float> CfgPatrolCrewDroneSpeed;
        internal static ConfigEntry<float> CfgPatrolCrewDroneMiss;
        internal static ConfigEntry<float> CfgPatrolCrewDroneDamage;
        internal static ConfigEntry<float> CfgPatrolCrewDroneRadius;
        internal static ConfigEntry<int> CfgPatrolCrewDroneHitpoints;
        internal static ConfigEntry<float> CfgPatrolCrewDroneHitRadius;
        internal static ConfigEntry<int> CfgPatrolCrewDroneEventCode;
        internal static ConfigEntry<bool> CfgAdmin;
        internal static ConfigEntry<string> CfgAdminKey;
        internal static ConfigEntry<string> CfgAdminIds;
        internal static ConfigEntry<int> CfgAdminEventCode;
        internal static ConfigEntry<bool> CfgDrone;
        internal static ConfigEntry<string> CfgDroneKey;
        internal static ConfigEntry<float> CfgDroneThrust;
        internal static ConfigEntry<float> CfgDroneSideThrust;
        internal static ConfigEntry<float> CfgDroneLift;
        internal static ConfigEntry<float> CfgDroneGravity;
        internal static ConfigEntry<float> CfgDroneDrag;
        internal static ConfigEntry<float> CfgDroneMaxSpeed;
        internal static ConfigEntry<float> CfgDroneSensitivity;
        internal static ConfigEntry<bool> CfgDroneInvertX;
        internal static ConfigEntry<bool> CfgDroneInvertY;
        internal static ConfigEntry<float> CfgDroneFov;
        internal static ConfigEntry<float> CfgDroneDamage;
        internal static ConfigEntry<float> CfgDroneRadius;
        internal static ConfigEntry<float> CfgDroneLaunchForward;
        internal static ConfigEntry<float> CfgDroneLaunchUp;
        internal static ConfigEntry<float> CfgDroneLaunchSpeed;
        internal static ConfigEntry<float> CfgDroneArmDelay;
        internal static ConfigEntry<float> CfgDroneSafeRadius;
        internal static ConfigEntry<int> CfgDroneEventCode;
        internal static ConfigEntry<float> CfgDroneNetHz;
        internal static ConfigEntry<float> CfgDroneModelScale;
        internal static ConfigEntry<bool> CfgDroneSound;
        internal static ConfigEntry<float> CfgDroneSoundVolume;
        internal static ConfigEntry<float> CfgDroneSoundRange;
        internal static ConfigEntry<float> CfgDroneFlightTime;
        internal static ConfigEntry<float> CfgDroneRange;
        internal static ConfigEntry<float> CfgDroneNoiseFrom;
        internal static ConfigEntry<bool> CfgDroneOverlay;
        internal static ConfigEntry<bool> CfgDroneWake;
        internal static ConfigEntry<bool> CfgDroneRequireItem;
        internal static ConfigEntry<int> CfgDroneItemId;
        internal static ConfigEntry<bool> CfgWatchTemplates;
        internal static ConfigEntry<bool> CfgJammer;
        internal static ConfigEntry<int> CfgJammerItemId;
        internal static ConfigEntry<float> CfgJammerRadius;
        internal static ConfigEntry<float> CfgJammerWarnRadius;
        internal static ConfigEntry<bool> CfgJammerDetonate;
        internal static ConfigEntry<float> CfgJammerDelay;
        internal static ConfigEntry<bool> CfgJammerAffectsOwn;
        internal static ConfigEntry<bool> CfgDroneSelfDestruct;
        internal static ConfigEntry<bool> CfgDroneShootable;
        internal static ConfigEntry<int> CfgDroneHitpoints;
        internal static ConfigEntry<float> CfgDroneShootRange;
        internal static ConfigEntry<bool> CfgDroneNpcFire;
        internal static ConfigEntry<float> CfgDroneNpcFireRange;
        internal static ConfigEntry<float> CfgDroneNpcAccuracy;
        internal static ConfigEntry<float> CfgDroneNpcShotSeconds;
        internal static ConfigEntry<int> CfgDroneNpcShooters;
        internal static ConfigEntry<bool> CfgFire;
        internal static ConfigEntry<float> CfgFireScale;
        internal static ConfigEntry<bool> CfgNetDrop;
        internal static ConfigEntry<bool> CfgArena;
        internal static ConfigEntry<string> CfgArenaKey;
        internal static ConfigEntry<float> CfgArenaSize;
        internal static ConfigEntry<float> CfgArenaDistance;

        internal static List<ItemDef> Items = new List<ItemDef>();

        /// <summary>The ItemDef for an id, or null if the id is the game's own.</summary>
        internal static ItemDef FindItem(int id)
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Id == id) return Items[i];
            return null;
        }
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
            PatchWeaponSpine();
            PatchSpawnLookup();
            PatchDestroyWatch();
            PatchReloadDiagnostics();
            PatchRocketImpact();
            PatchDroneShot();
            PatchCustomDrop();
            PatchFire();
            Turret.Install(_harmony);
            ColdHook.Install(_harmony);
            DroneInputHook.Install(_harmony);
            DroneNpcHook.Install(_harmony);
            Crew.Install(_harmony);
            Admin.Install(_harmony);
            TankNetwork.Install(_harmony);
            Patrol.Install(_harmony);

            StartCoroutine(Tank.Prewarm());
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
            // Die Erkaeltung entsteht zur Laufzeit, nicht im Profil:
            // PlayerLifeDataManager::PlayerColdController zaehlt _playerLifeData.Cold
            // im Takt von Cold_Delay hoch, sobald die Stunde am TOD_Sky ueber 22
            // oder unter 7 liegt. Ein geheiltes Profil ist deshalb nach der
            // naechsten Nacht wieder krank - das Feld im Profil zu setzen reicht
            // nicht, der Zaehler muss aus.
            CfgNoCold = Config.Bind("Fixes", "NoCold", true,
                "Die naechtliche Erkaeltung abschalten: haelt Cold UND Temp auf 0. "
                + "Temp ist der Fieberzaehler, nicht die Koerpertemperatur - "
                + "gesund ist 0, jeder Wert darueber hustet, macht Schaden und "
                + "waechst von selbst weiter.");
            CfgVerbose = Config.Bind("Debug", "Verbose", false,
                "Ausfuehrliche Feld- und Ladeprotokolle. Fuer die Fehlersuche.");

            // The scene jump assembles a LocationChangeTrigger and hands it to
            // ChangeGameLocation. The number in it is a value of the GameScene
            // enum, NOT a build index - see docs/ai/tasks/new-regions.md,
            // section 1. The key ExtraScenes that used to be bound here is gone
            // without replacement: it appended build indices to a list of enum
            // values, and the four scenes it was meant to reach are in region 0
            // already.
            CfgSceneJump = Config.Bind("Research", "EnableSceneJump", false,
                "Change into the scene given by JumpScene on a key press. An "
                + "exploration tool, therefore off by default. Save first.");
            CfgJumpScene = Config.Bind("Research", "JumpScene", 9,
                "GameScene value of the target scene, not a build index. "
                + "Present are 5 GW_Scene_1, 6 GW_Scene_2, 7 GW_Scene_3, "
                + "9 Bunker_A65, 13 Catacombs, 14 Underground_Lab, 11 GL_Scene. "
                + "All six world scenes are furnished: player spawns, loot, NPCs "
                + "and exits are all present in the scene files.");
            CfgJumpRegion = Config.Bind("Research", "JumpRegion", -1,
                "Jump into a whole REGION instead of a scene: number of the "
                + "GameRegion (0 Severoufimsk, 2 Uralsk). The target is then "
                + "that region's startScene and JumpScene is not used. "
                + "-1 means: scene jump as before.");
            CfgJumpKey = Config.Bind("Research", "JumpKey", "F9",
                "Key for the scene change, a name from UnityEngine.KeyCode.");

            // The second region. GameRegion.Uralsk = 2 is in the game's enum and
            // has no data row in GameRegions - this section creates one at
            // runtime. Reasoning, measurements and the four possible routes are
            // in docs/ai/tasks/new-regions.md.
            CfgNewRegion = Config.Bind("Regions", "Enabled", true,
                "Register a second region in the game's RegionsList. It appears "
                + "in the region drop-down of the room settings by itself.");
            CfgNewRegionId = Config.Bind("Regions", "RegionId", 2,
                "Number of the new region, from the GameRegion enum. 2 is "
                + "Uralsk: the only free value that is neither DEV nor Test, and "
                + "the only one the game already has a translated name for.");
            CfgNewRegionName = Config.Bind("Regions", "RegionName", "",
                "A name of your own. LEAVING THIS EMPTY IS THE RULE: the game "
                + "has had a translated name for Uralsk all along - "
                + "$GameRegion_Uralsk is in Localization_DB in five languages "
                + "(ru Cyrillic, en/de Uralsk, fr Ouralsk). Only fill this in if "
                + "you want to read something else; LocalizationHook then "
                + "overrides the game's own text.");
            CfgNewRegionStart = Config.Bind("Regions", "StartScene", 9,
                "GameScene value of the new region's start scene. 9 Bunker_A65.");
            CfgNewRegionScenes = Config.Bind("Regions", "Scenes", "9,14",
                "Scenes of the new region, GameScene values separated by commas. "
                + "Default: 9 Bunker_A65 and 14 Underground_Lab.");
            CfgNewRegionExclusive = Config.Bind("Regions", "TakeFromRegion0", true,
                "Remove those scenes from the list of region 0. NEEDED, because "
                + "GetRegionDataAtScene returns the FIRST region that contains a "
                + "scene - without the removal region 0 would always win and the "
                + "new region would be a label and nothing else. Region 0 keeps "
                + "GW_Scene_1, GW_Scene_2, GW_Scene_3 and Catacombs.");

            // Das Turmgeschuetz des BTR-80A. Der Sitz entsteht im Prefix auf
            // InitCar, weil das Spiel dort
            //     Passengers = new GameObject[SeatPoints.childCount]
            // setzt - ein zusaetzliches Kind an SeatPoints genuegt also, und
            // das Array bekommt von selbst die richtige Laenge.
            CfgTurret = Config.Bind("Turret", "Enabled", true,
                "Dem BTR-80A einen siebten Sitz im Turm geben und das Geschuetz "
                + "bedienbar machen. Im Spiel noch UNGEPRUEFT, Stand 2026-08-28.");
            CfgTurretKey = Config.Bind("Turret", "TurretKey", "G",
                "Taste zum Aufsitzen und Verlassen des Geschuetzes, waehrend man "
                + "im Fahrzeug sitzt. Name aus UnityEngine.KeyCode.");
            CfgTurretDamage = Config.Bind("Turret", "Damage", 120f,
                "Schaden je Schuss, und der EINZIGE Schaden - es gibt keine "
                + "Flaechenwirkung mehr, getroffen wird nur, worauf die Bildmitte "
                + "steht. Bis 0.5.1 waren es 750 je Schuss plus Sprengwirkung; "
                + "das war ein Panzer im Kleinen. Jetzt ist es eine Waffe mit "
                + "grossem Kaliber: ein einzelner Treffer tut weh, toetet aber "
                + "nicht von selbst - das macht erst die Garbe. Zum Vergleich: "
                + "die Granate des M72 macht 900 im Radius 12, der Panzer 1500.");
            CfgTurretRange = Config.Bind("Turret", "Range", 900f,
                "Reichweite des Schusses in Welteinheiten.");
            CfgTurretDelay = Config.Bind("Turret", "FireDelay", 0.12f,
                "Sekunden zwischen zwei Schuessen - rund acht je Sekunde, also "
                + "eine Maschinenkanone. Das Geschuetz lebt von der Kadenz, nicht "
                + "von der Wucht des einzelnen Treffers. ACHTUNG: jeder Schuss "
                + "nimmt eine Patrone. Deshalb ist die Vorgabe fuer AmmoItemId "
                + "seit 0.5.2 der Gurt mit 200 Schuss und nicht mehr die Kiste "
                + "mit 10 - die waere in gut einer Sekunde durch.");
            CfgTurretTurnSpeed = Config.Bind("Turret", "TurnSpeed", 140f,
                "Grad je Sekunde, mit denen das Rohr der Blickrichtung nachdreht. "
                + "Der BLICK folgt der Maus sofort - nur das Rohr hat Traegheit. "
                + "Bis 2026-08-28 hing die Kamera am Rohr und damit an diesen "
                + "55 Grad je Sekunde; das war der Grund, warum sich das Zielen "
                + "zaeh anfuehlte.");
            CfgTurretPitchMin = Config.Bind("Turret", "PitchMin", -20f,
                "Tiefster Rohrwinkel in Grad. Weiter als das echte Vorbild "
                + "(-5 Grad): seit die Kamera an der Zielrichtung haengt, ist "
                + "der Rohrwinkel zugleich der Blickwinkel, und ein Blick, der "
                + "nicht nach unten kann, macht das Zielen unbrauchbar.");
            CfgTurretPitchMax = Config.Bind("Turret", "PitchMax", 60f,
                "Hoechster Rohrwinkel in Grad.");
            CfgTurretAmmo = Config.Bind("Turret", "RequireAmmo", true,
                "Je Schuss eine Patrone aus dem Kofferraum nehmen. Der Kofferraum "
                + "ist InteractColliders/BagaggeContainer, ein ItemsContainer.");
            CfgTurretAmmoId = Config.Bind("Turret", "AmmoItemId", 2050,
                "Item-ID der Munition. 2050 ist der Gurtkasten mit 200 Schuss, "
                + "2051 die .50-BMG-Kiste mit 10. Bei acht Schuss je Sekunde ist "
                + "die Kiste in gut einer Sekunde leer, deshalb der Gurt. Dass "
                + "eine Maschinenkanone an einem 7,62er Gurt haengt, ist ein "
                + "Zugestaendnis an den vorhandenen Itembestand - ein eigener "
                + "30-mm-Gurt waere ein eigenes Item mit eigener Id, eigenem "
                + "Modell und einem Eintrag am Server.");
            CfgTurretSpawnAmmo = Config.Bind("Turret", "SpawnAmmo", 200,
                "So viele Schuss wandern beim manuellen Hinstellen eines BTR in "
                + "den Kofferraum. Damit ist das Geschuetz sofort benutzbar. 0 "
                + "schaltet die Beigabe ab.");
            CfgTurretAmmoBackpack = Config.Bind("Turret", "AmmoFromBackpack", true,
                "Ist der Kofferraum leer, auch aus dem Rucksack des Spielers "
                + "nehmen. Der Kofferraum gehoert dem Fahrzeug und ist nach dem "
                + "naechsten Spielstart mitsamt Inhalt weg.");
            CfgTurretEventCode = Config.Bind("Turret", "NetworkEventCode", 181,
                "Photon event code for turret rotation. The default follows the "
                + "five drone codes 176 through 180 and must not overlap them.");
            CfgTurretSound = Config.Bind("Turret", "Sound", true,
                "Spatial firing sound for player and patrol vehicle guns. The "
                + "sound is generated by the plugin and synchronized with the shot.");
            CfgTurretSoundVolume = Config.Bind("Turret", "SoundVolume", 1f,
                "Volume of vehicle gun shots, 0 to 1.");
            CfgTurretSoundRange = Config.Bind("Turret", "SoundRange", 650f,
                "Maximum distance in metres at which a tank shot is audible. "
                + "The BTR uses half this range.");
            // Gezielt wird mit der Maus, nicht mit dem Kopf: die Kamera sitzt
            // waehrend des Schiessens im Rohr und koennte den Turm sonst nicht
            // mehr steuern - sie zeigt ja immer schon dorthin, wo er steht.
            CfgTurretSensitivity = Config.Bind("Turret", "Sensitivity", 2.2f,
                "Grad Turmschwenk je Einheit Mausbewegung. Die Traegheit aus "
                + "TurnSpeed bleibt davon unberuehrt.");
            CfgTurretRecoil = Config.Bind("Turret", "Recoil", 0.05f,
                "Grad, die das Rohr je Schuss hochschlaegt. Bewusst klein - ein "
                + "Turmgeschuetz sitzt auf zwoelf Tonnen Fahrzeug. Der Wert haengt "
                + "an der Kadenz, nicht am Kaliber: bei acht Schuss je Sekunde "
                + "sind auch 0,05 Grad noch 0,4 Grad Wanderung in der Sekunde. "
                + "Die alten 0,30 Grad haetten das Rohr in einer Garbe in den "
                + "Himmel geschoben.");
            // Die Kamera sass bis 2026-08-28 auf der Rohrachse, kurz vor der
            // Muendung - und damit MITTEN IM BUG. Nachgemessen am Prefab
            // BTR-80A_Spawn: das Turmmesh reicht in Rohrrichtung bis z 8.9,
            // die Wanne (hull, Halbmasse 11.47 um z 0) aber bis z 11.47. Ein
            // Auge knapp vor dem Rohr steckt also im Bugblech. Deshalb sitzt
            // es jetzt UEBER dem Turm: Ankerpunkt ist die Turmachse, nicht die
            // Muendung, und EyeUp hebt es ueber das Turmdach (lokal z bis
            // 2.878 ueber dem Drehpunkt).
            CfgTurretEyeForward = Config.Bind("Turret", "CamForward", 2.0f,
                "Wie weit vor dem Turmdrehpunkt die Kamera sitzt, laengs des "
                + "Rohres. Heisst nicht mehr EyeForward, weil sich der Bezugs"
                + "punkt geaendert hat - EyeForward und EyeUp in dieser Datei "
                + "sind Reste und ohne Wirkung.");
            CfgTurretEyeUp = Config.Bind("Turret", "CamUp", 3.6f,
                "Wie hoch die Kamera ueber dem Turmdrehpunkt sitzt. Das Turmdach "
                + "liegt 2.9 Einheiten darueber - weniger, und der Turm steht "
                + "im Bild.");
            CfgTurretEyeSide = Config.Bind("Turret", "CamSide", 0.0f,
                "Seitenversatz der Kamera. 0 heisst mittig ueber dem Rohr.");
            CfgTurretInvertX = Config.Bind("Turret", "InvertX", false,
                "Seitenrichtung der Maus umkehren.");
            CfgTurretCrosshair = Config.Bind("Turret", "Crosshair", true,
                "Fadenkreuz in der Bildmitte. Der Schuss laeuft seit 2026-08-28 "
                + "auf der BLICKACHSE der Kamera, nicht mehr auf der Rohrachse - "
                + "die Mitte ist damit immer der Treffpunkt, egal wo die Kamera "
                + "sitzt. Der alte Schluessel CrosshairRange ist wirkungslos. "
                + "Zu sehen nur, wenn keine Optik im Bild ist - Turret/Scope "
                + "und Tank/Scope bringen ihr eigenes Kreuz mit.");
            CfgTurretTakeCamera = Config.Bind("Turret", "TakeCamera", true,
                "Die Kamera des Spiels waehrend des Zielens stilllegen. Ohne das "
                + "zieht MouseOrbitController sie jeden Frame wieder um das "
                + "Fahrzeug herum - der Blick zeigt dann auf den eigenen BTR "
                + "statt durch das Rohr. Auf false, falls die Kamera nach dem "
                + "Aussteigen haengt.");
            CfgTurretFov = Config.Bind("Turret", "FOV", 32f,
                "Bildwinkel im Geschuetz, in Grad. Klein heisst nah heran wie im "
                + "Zielfernrohr; das Spiel selbst benutzt 60. 0 laesst den "
                + "Bildwinkel unveraendert. Weiter als im Panzer (20): das BTR "
                + "schiesst schnell auf bewegliche Ziele, und wer nur durch ein "
                + "Rohr sieht, findet sie nicht wieder.");
            CfgTurretScope = Config.Bind("Turret", "Scope", true,
                "Richtoptik statt vier Striche im freien Bild: schwarze Fassung, "
                + "runde Linse, offenes Kreuz mit Vorhaltemarken (apc_scope.png). "
                + "Die Linse ist weiter als die des Panzers und die Abschattung "
                + "zum Rand schwaecher. Auf false bleibt es beim einfachen "
                + "Fadenkreuz.");
            // KEIN Explosionsschalter mehr fuer das BTR, und das ist Absicht.
            // Ein Schalter mit der Vorgabe false haette nichts geholfen: eine
            // vorhandene nextday.revival.toolkit.cfg gewinnt gegen jede neue
            // Vorgabe, und genau so schoss das Geschuetz nach 0.5.1 weiter mit
            // Sprenggranaten. Was ganz weg ist, kann keine alte Datei wieder
            // anschalten. Die Sprengwerte des Panzers stehen unter [Tank].
            // Die drei alten Zeilen Explosion/ExplosionDamage/ExplosionRadius
            // bleiben in vorhandenen Dateien stehen und sind wirkungslos.
            CfgTurretScopeOverlay = Config.Bind("Turret", "ScopeOverlay", false,
                "Zusaetzlich das Zielfernrohrbild ueberblenden. Aus, seit die "
                + "Kamera im Rohr sitzt - das Bild verdeckte nur die Sicht.");
            CfgTurretAimLead = Config.Bind("Turret", "AimLead", 5f,
                "Wie weit die Zielrichtung der Rohrstellung vorauslaufen darf, "
                + "in Grad. Der Blick haengt seit 0.4.9 am ROHR, nicht mehr an "
                + "der Maus: das Bild schwenkt genau so schnell wie der Turm. "
                + "Ohne diese Grenze liefe die Maus beliebig weit voraus und der "
                + "Turm drehte nach dem Loslassen noch sekundenlang weiter. "
                + "0 klebt die Maus starr ans Rohr.");
            CfgTurretSeatX = Config.Bind("Turret", "SeatX", 0.0f,
                "Lage des Geschuetzsitzes, relativ zu SeatPoints. Die vorhandenen "
                + "Sitze liegen bei x plus/minus 1.35, y 0.15, z -4.62 bis 5.77.");
            CfgTurretSeatY = Config.Bind("Turret", "SeatY", 0.95f,
                "Hoehe des Geschuetzsitzes. Hoeher als die Bank, weil der Kopf in "
                + "den Turm gehoert. UNGEPRUEFT - hier wird im Spiel nachgemessen.");
            CfgTurretSeatZ = Config.Bind("Turret", "SeatZ", 2.6f,
                "Laengslage des Geschuetzsitzes. Der Turm sitzt vor der Mitte.");

            // Eine ebene Testflaeche zur Laufzeit. Keine Region im Sinne des
            // Spiels - eine Region ist ein GameRegionData mit Buildindizes,
            // und neue Buildszenen gibt es ohne Neubau des Spiels nicht.
            CfgAdmin = Config.Bind("Admin", "Enabled", true,
                "Menue im Spiel: Items geben, Werkzeuge schalten.");
            CfgAdminKey = Config.Bind("Admin", "Key", "F8",
                "Taste, die das Adminmenue auf- und zumacht.");
            // Steam-Ids, die das Menue oeffnen duerfen, mit Komma getrennt.
            // Leer laesst es fuer jeden auf - so bleibt ein Rechner ohne
            // laufendes Steam pruefbar. Die Vorgabe sind die beiden Konten,
            // die bisher am Masterserver waren.
            CfgAdminIds = Config.Bind("Admin", "AllowedSteamIds",
                "76561198376412662,76561198035776744",
                "Steam-Ids mit Zugang zum Menue, mit Komma getrennt. Leer = alle.");
            CfgAdminEventCode = Config.Bind("Admin", "NetworkEventCode", 182,
                "Photon event code for temporary admin grants, remote loadouts "
                + "and map teleport. It must not overlap drone or turret events.");

            // ------------------------------------------------------- Drohne
            //
            // Die Werte stehen im Verhaeltnis zu dem, was es schon gibt:
            // die LAW detoniert mit 900 auf 12 m, das BTR-Geschuetz mit 350
            // auf 5 m. Die Drohne ist eine Wegwerfwaffe, keine Artillerie -
            // ihr Reiz ist die Zustellung, nicht die Wucht.
            CfgDrone = Config.Bind("Drone", "Enabled", true,
                "FPV-Drohne: auf Tastendruck starten, durch ihre Kamera fliegen, "
                + "beim ersten Treffer detoniert sie. Der Koerper bleibt stehen "
                + "und ist waehrenddessen angreifbar. Notausgang bei Problemen: "
                + "hier false eintragen.");
            CfgDroneKey = Config.Bind("Drone", "Key", "V",
                "Taste zum Starten und Abbrechen, Name aus UnityEngine.KeyCode.");
            // WARUM 30 UND NICHT 16 (2026-08-30). Die Endgeschwindigkeit einer
            // Drohne ist Thrust/Drag, nicht MaxSpeed - MaxSpeed ist nur eine
            // Kappe darueber. Mit 16/1.4 waren das 11,4 m/s = 41 km/h, und die
            // Patrouille faehrt 45. Die Drohne konnte ein Fahrzeug also nie
            // einholen, egal wie lange man drueckte. Jetzt: 30/0.95 = 31,6 m/s
            // = 114 km/h, gekappt bei 38 m/s. Damit ist sie schneller als
            // alles, was auf der Strasse faehrt, und das ist der Sinn: eine
            // Patrouille ist nur dann konterbar, wenn man sie einholt.
            CfgDroneThrust = Config.Bind("Drone", "Thrust", 30f,
                "Schub vor und zurueck (W/S) in Metern je Sekundenquadrat. "
                + "Zusammen mit Drag bestimmt das die Endgeschwindigkeit: "
                + "Thrust/Drag, hier 31,6 m/s = 114 km/h.");
            CfgDroneSideThrust = Config.Bind("Drone", "SideThrust", 20f,
                "Schub seitwaerts (A/D).");
            CfgDroneLift = Config.Bind("Drone", "Lift", 22f,
                "Schub nach oben (Leertaste) und unten (Strg oder C).");
            CfgDroneGravity = Config.Bind("Drone", "Gravity", -5.5f,
                "Schwerkraft auf die Drohne. Absichtlich schwaecher als die "
                + "echten -9.81: eine Drohne haengt in der Luft, sie faellt nicht.");
            CfgDroneDrag = Config.Bind("Drone", "Drag", 0.95f,
                "Luftwiderstand je Sekunde, geschwindigkeitsproportional. Groesser "
                + "heisst traeger und stabiler, kleiner heisst schwebender. Zusammen "
                + "mit Thrust bestimmt das die Endgeschwindigkeit (Thrust/Drag).");
            CfgDroneMaxSpeed = Config.Bind("Drone", "MaxSpeed", 38f,
                "Harte Obergrenze der Geschwindigkeit in m/s.");
            CfgDroneSensitivity = Config.Bind("Drone", "Sensitivity", 2.4f,
                "Mausempfindlichkeit fuer Nick und Gier.");
            CfgDroneInvertX = Config.Bind("Drone", "InvertX", false,
                "Seitenrichtung der Maus umkehren.");
            CfgDroneInvertY = Config.Bind("Drone", "InvertY", false,
                "Hoehenrichtung der Maus umkehren.");
            CfgDroneFov = Config.Bind("Drone", "FOV", 92f,
                "Bildwinkel der Drohnenkamera. FPV-Kameras sind extrem weitwinklig; "
                + "0 laesst den Bildwinkel des Spiels stehen.");
            CfgDroneDamage = Config.Bind("Drone", "Damage", 550f,
                "Sprengschaden beim Einschlag. LAW 900, BTR-Geschuetz 350.");
            CfgDroneRadius = Config.Bind("Drone", "Radius", 7f,
                "Wirkungsradius der Detonation in Metern. LAW 12, BTR 5.");
            CfgDroneLaunchForward = Config.Bind("Drone", "LaunchForward", 2.2f,
                "Wie weit vor der Kamera die Drohne entsteht. Zu klein, und sie "
                + "startet im eigenen Koerper.");
            CfgDroneLaunchUp = Config.Bind("Drone", "LaunchUp", 0.5f,
                "Wie weit ueber der Kamera die Drohne entsteht.");
            CfgDroneLaunchSpeed = Config.Bind("Drone", "LaunchSpeed", 5f,
                "Anfangsgeschwindigkeit in Blickrichtung.");
            CfgDroneArmDelay = Config.Bind("Drone", "ArmDelay", 0.35f,
                "Sekunden nach dem Start, in denen keine Kollision zaehlt. Faengt "
                + "alles ab, was im ersten Moment im Weg steht - Rucksack, Waffe, "
                + "Fahrzeug.");
            CfgDroneSafeRadius = Config.Bind("Drone", "SafeRadius", 2.5f,
                "Treffer naeher als das am Startpunkt gelten als eigener Koerper.");
            CfgDroneEventCode = Config.Bind("Drone", "EventCode", 176,
                "First of five Photon event codes (Start, Lauf, Ende, Jam, "
                + "Treffer). Photon drops everything from 200 up; the game "
                + "itself uses only 1 and 2.");
            CfgDroneNetHz = Config.Bind("Drone", "NetHz", 15f,
                "Wie oft je Sekunde Lage und Blickrichtung an die anderen gehen. "
                + "Dazwischen wird bei ihnen interpoliert.");
            CfgDroneModelScale = Config.Bind("Drone", "ModelScale", 4f,
                "Size of the model the others see, as a multiple of the mesh "
                + "itself. The mesh is the INVENTORY model and is 36 cm across - "
                + "at that size a drone is two pixels at thirty metres, and a "
                + "weapon nobody can see is a weapon nobody can answer. Four "
                + "makes it about 1.5 m and clearly visible against the sky. It "
                + "is also the hitbox: Shootable measures against the model, so "
                + "there is no second number to keep in step with this one.");
            CfgDroneSound = Config.Bind("Drone", "Sound", true,
                "Surren. NICHT nur Verzierung: wer von einer Drohne getroffen wird, "
                + "muss vorher die Gelegenheit gehabt haben, sie zu hoeren.");
            CfgDroneSoundVolume = Config.Bind("Drone", "SoundVolume", 0.8f,
                "Lautstaerke des Surrens, 0 bis 1.");
            CfgDroneSoundRange = Config.Bind("Drone", "SoundRange", 140f,
                "Ab dieser Entfernung in Metern ist die Drohne nicht mehr zu hoeren.");
            CfgDroneFlightTime = Config.Bind("Drone", "FlightTime", 90f,
                "Flugzeit in Sekunden. Danach gehen die Motoren aus: sie faellt, "
                + "und beim Aufschlag detoniert sie NICHT.");
            CfgDroneRange = Config.Bind("Drone", "Range", 650f,
                "Funkreichweite in Metern. Darueber reisst die Verbindung ab, der "
                + "Blick faellt zum Koerper zurueck und die Drohne ist weg. Der "
                + "Wert ist GERATEN und gehoert nachgemessen - er ist zugleich die "
                + "billigste Absicherung dagegen, dass die Drohne aus dem geladenen "
                + "Teil der Welt fliegt und durch den Boden faellt. Seit "
                + "2026-08-30 650 statt 300: eine Patrouille faehrt bei 45 km/h "
                + "in den 20 Sekunden, die der Anflug dauert, 250 m weit, und "
                + "bei 300 m Leine ist sie draussen, bevor man sie hat.");
            CfgDroneNoiseFrom = Config.Bind("Drone", "NoiseFrom", 250f,
                "Ab dieser Entfernung rauscht das Bild. Das ist die Vorwarnung vor "
                + "dem Abriss.");
            CfgDroneOverlay = Config.Bind("Drone", "Overlay", true,
                "Videoeinblendung: Akku, Entfernung, Hoehe, Fadenkreuz, Rauschen.");
            CfgDroneWake = Config.Bind("Drone", "WakeNpc", true,
                "Die Drohne weckt die NPCs, ueber die sie fliegt. Ohne das "
                + "stehen sie in T-Pose ueber dem Boden: eine Siedlung schaltet "
                + "die Animation und die KI ab, sobald KEIN SPIELER in "
                + "CheckPlayersDistRadius steht, und der Pilot steht "
                + "hunderte Meter weit weg. Siehe RE 22.");
            CfgDroneRequireItem = Config.Bind("Drone", "RequireItem", true,
                "Der Start verbraucht eine Drohne aus dem Rucksack. Auf false "
                + "startet sie aus dem Nichts - zum Ausprobieren, nicht zum Spielen.");
            CfgDroneItemId = Config.Bind("Drone", "ItemId", 1163,
                "Item, das ein Start verbraucht.");

            // -------------------------------------------------- Stoersender
            //
            // The counter to the drone, and the reason it is a CARRIED item:
            // a drone is a weapon that costs its owner nothing but a backpack
            // slot, so the answer has to cost a backpack - 26 kg of it.
            //
            // Who decides that a drone dies is the whole design. A drone only
            // exists on the client flying it; only there can the camera be
            // given back and the warhead be fired. The jammer therefore kills
            // nothing itself. It says "jammer here, radius R" and every pilot
            // checks their own drone against that. A modified client could
            // ignore it - among friends that is fine, as a security measure it
            // is worthless, and it is written down so nobody mistakes it for
            // one.
            //
            // Traffic stays at zero until it matters: the event goes out only
            // while a foreign drone is actually close, and then five times a
            // second, unreliable.
            CfgWatchTemplates = Config.Bind("Fixes", "WatchSpawnTemplates", true,
                "Schreibt ins Log, WER eine Inventarvorlage eines eigenen Items "
                + "zerstoert - mitsamt Aufrufer. Kostet einen Vergleich je "
                + "Object.Destroy und beantwortet die Frage, warum ein Item "
                + "mitten in der Sitzung aufhoert zu existieren (E-029).");
            CfgJammer = Config.Bind("Jammer", "Enabled", true,
                "Stoersender: wer einen traegt, laesst jede Drohne hochgehen, "
                + "die ihm zu nahe kommt.");
            CfgJammerItemId = Config.Bind("Jammer", "ItemId", 2054,
                "Item, das den Stoersender ausmacht. Es muss nur im Rucksack "
                + "oder in der Weste liegen - verbraucht wird nichts. Zum "
                + "Ausprobieren laesst sich hier auch eine vorhandene Spiel-Id "
                + "eintragen.");
            CfgJammerRadius = Config.Bind("Jammer", "Radius", 50f,
                "Ab dieser Entfernung in Metern ist die Drohne verloren.");
            CfgJammerWarnRadius = Config.Bind("Jammer", "WarnRadius", 85f,
                "Ab hier rauscht das Bild - die Vorwarnung. Wer sie sieht, "
                + "kann noch abdrehen. Ein Wert kleiner als Radius schaltet "
                + "die Vorwarnung ab.");
            CfgJammerDetonate = Config.Bind("Jammer", "Detonate", true,
                "true: die Drohne zuendet dort, wo sie fliegt. false: die "
                + "Motoren stehen, sie faellt und geht am Boden hoch.");
            CfgJammerDelay = Config.Bind("Jammer", "Delay", 0.4f,
                "Sekunden zwischen dem Betreten des Radius und dem Knall. "
                + "Nicht null, damit es nach Wirkung aussieht und nicht nach "
                + "einem Fehler. Entkommen kann die Drohne in dieser Zeit "
                + "nicht - wer drin war, ist erledigt.");
            CfgJammerAffectsOwn = Config.Bind("Jammer", "AffectsOwn", false,
                "Stoert der eigene Sender die eigene Drohne? Vorgabe false, "
                + "sonst kann niemand beides tragen. Auf true ist der "
                + "Stoersender allein zu pruefen: starten, wegfliegen, "
                + "umdrehen - beim Radius muss es knallen.");
            CfgDroneSelfDestruct = Config.Bind("Drone", "SelfDestruct", true,
                "Pressing the drone key during a flight blows the drone up where it "
                + "is, instead of quietly ending the flight. Nearer to the pilot "
                + "than SafeRadius it still only lands - a mis-press must not be "
                + "able to kill you.");

            // Shooting a drone down. The cheap answer to the jammer's
            // expensive one: no item, no 26 kg, just the gun already in your
            // hands - and it needs a drone you can SEE, which is why
            // ModelScale moved up in the same step.
            //
            // Who decides is what it always is in this file: the PILOT's
            // client. The shooter reports "I hit the drone of player X", the
            // pilot subtracts it from his own and fires the warhead when
            // nothing is left. A modified client could throw that report
            // away. Among friends that is no problem, as protection against
            // cheating it is worth nothing, and it is written here so nobody
            // takes it for that.
            //
            // The shot is measured from the camera through the drone's
            // middle - no spread, no bullet drop, no travel time. The game
            // keeps all of that to itself inside FireOneShot, and guessing at
            // it would mean a second ballistics model that disagrees with the
            // first. What the player sees instead is honest: the crosshair
            // was on the drone, so it was a hit.
            CfgDroneShootable = Config.Bind("Drone", "Shootable", true,
                "A drone can be shot down. Every shot that goes through the model "
                + "takes one hit point; at zero the drone detonates where it flies. "
                + "The blast is the normal one, so whoever shot it dies only if he "
                + "was inside Radius metres of it anyway.");
            CfgDroneHitpoints = Config.Bind("Drone", "Hitpoints", 3,
                "Shots a drone survives. Every firearm counts the same - a drone is "
                + "a plastic frame and four motors, and the difference between a "
                + "pistol and a rifle is the chance of hitting it at all, not what "
                + "happens when you do.");
            CfgDroneShootRange = Config.Bind("Drone", "ShootRange", 250f,
                "How far a shot at a drone reaches, in metres. Not a weapon value: "
                + "it is the distance at which aiming at a 1.5 m object from the "
                + "hip of a hitscan test stops being fair.");
            CfgDroneNpcFire = Config.Bind("Drone", "NpcFire", true,
                "Hostile NPCs fire at the drone while it keeps their settlement "
                + "awake. The real NPC firearm makes the shot and tracer; this "
                + "setting only adds the drone as a possible target.");
            CfgDroneNpcFireRange = Config.Bind("Drone", "NpcFireRange", 110f,
                "Metres. Also extends the drone's NPC wake radius so the shooter "
                + "is active before it fires.");
            CfgDroneNpcAccuracy = Config.Bind("Drone", "NpcAccuracy", 0.25f,
                "Chance per NPC shot at point blank. It falls to half this "
                + "value at maximum range. A drone over a camp is the most "
                + "dangerous thing in it and everyone shoots at it first, so "
                + "this is not a token defence: at 0.25, three men with line "
                + "of sight take a three-hit drone down in about four seconds "
                + "of hovering. Flying fast, high and behind cover is the "
                + "answer, not a lower number here.");
            CfgDroneNpcShotSeconds = Config.Bind("Drone", "NpcShotSeconds", 0.8f,
                "Average seconds between NPC volleys at the drone.");
            CfgDroneNpcShooters = Config.Bind("Drone", "NpcShooters", 8,
                "How many hostile NPCs fire at the drone in one volley, "
                + "closest first. Everyone in range with a clear line would "
                + "shoot at it, so this is a ceiling against a whole camp "
                + "firing at once, not a squad size.");

            // ------------------------------------------------------- Effects
            CfgFire = Config.Bind("Effects", "Fire", true,
                "Fire on top of every explosion: fireball, tongues, sparks, smoke "
                + "and a short flash of light. The game's own explosion stays as it "
                + "is underneath - this is added, nothing is replaced.");
            CfgFireScale = Config.Bind("Effects", "FireScale", 1.0f,
                "Size of that fire, relative to the blast radius of the explosion "
                + "it belongs to. 0 is not allowed, use Fire = false.");

            CfgNetDrop = Config.Bind("CustomItems", "NetworkedDrop", true,
                "Drop our own ids through the game's own path, so what lands on "
                + "the ground is a scene object with a PhotonView and an "
                + "ItemSpawned: others see it, and it can be picked up. Needs the "
                + "Resources.Load(string, Type) patch (0.5.3). On false the plugin "
                + "falls back to the local piece of scenery of 0.5.1 - visible to "
                + "nobody else and not pickable.");

            // -------------------------------------------------- Kampfpanzer
            //
            // Der Panzer ist ein umgebautes BTR-80A und benutzt dasselbe
            // Geschuetz - nur mit diesen Werten statt der Turret-Werte. Sie
            // stehen bewusst im Verhaeltnis zu dem, was es schon gibt:
            //
            //   BTR-Geschuetz   750 direkt,  350 auf  5 m, alle 0,9 s
            //   M72 LAW                      900 auf 12 m, Einwegwaffe
            //   T-72           1500 direkt, 1600 auf 16 m, alle 12 s
            //
            // Das eigentliche Gegengewicht zur Feuerkraft ist nicht die
            // Ladezeit, sondern die Turmdrehung: 22 Grad je Sekunde gegen 140
            // beim BTR. Wer von der Seite kommt, ist vorbei, bevor das Rohr
            // steht. Alle Werte sind Vorschlaege und gehoeren im Spiel
            // gedreht, bis es sich richtig anfuehlt.
            CfgTank = Config.Bind("Tank", "Enabled", true,
                "Kampfpanzer T-72: auf Tastendruck einen umgebauten BTR-80A "
                + "hinstellen - anderes Mesh, vier Sitze, schweres Geschuetz. "
                + "Notausgang bei Problemen: hier false eintragen.");
            CfgTankKey = Config.Bind("Tank", "Key", "F9",
                "Taste, die den Panzer hinstellt, Name aus UnityEngine.KeyCode. "
                + "F7 stellt weiter den unveraenderten BTR hin.");
            CfgTankSwapMesh = Config.Bind("Tank", "SwapMesh", true,
                "Das sichtbare Mesh gegen das Panzermodell tauschen. Auf false "
                + "bleibt ein BTR stehen, der sich wie ein Panzer verhaelt - "
                + "zum Trennen der Fehlersuche.");
            CfgTankSeats = Config.Bind("Tank", "Seats", 3,
                "Mitfahrplaetze ohne den Geschuetzsitz. 3 ergibt zusammen mit "
                + "dem Geschuetz die geforderten vier Sitze.");
            CfgTankDamage = Config.Bind("Tank", "Damage", 1500f,
                "Direkter Trefferschaden. Doppelt so hart wie das BTR-Geschuetz.");
            CfgTankExplosionDamage = Config.Bind("Tank", "ExplosionDamage", 1600f,
                "Sprengschaden am Einschlag. Knapp doppelt so viel wie die LAW - "
                + "die ist eine Handwaffe, das hier ist Artillerie.");
            CfgTankExplosionRadius = Config.Bind("Tank", "ExplosionRadius", 16f,
                "Wirkungsradius der Sprengwirkung in Metern.");
            CfgTankDelay = Config.Bind("Tank", "FireDelay", 12f,
                "Ladezeit in Sekunden. Ein echter T-72 laedt mit Lademaschine in "
                + "sieben bis acht Sekunden, von Hand in ueber zwanzig. Waehrend "
                + "der Ladezeit steht ein Balken unter dem Fadenkreuz.");
            CfgTankRange = Config.Bind("Tank", "Range", 1600f,
                "Reichweite des Schusses in Metern.");
            CfgTankTurnSpeed = Config.Bind("Tank", "TurnSpeed", 22f,
                "Grad je Sekunde, mit denen das Rohr der Blickrichtung nachdreht. "
                + "Ein Turm dieser Groesse dreht langsam, und genau das ist das "
                + "Gegengewicht zur Feuerkraft.");
            CfgTankPitchMin = Config.Bind("Tank", "PitchMin", -6f,
                "Lowest barrel angle in degrees. The T-72 cannot use the BTR's "
                + "-20 degree camera range without driving the barrel through "
                + "its own hull.");
            CfgTankPitchMax = Config.Bind("Tank", "PitchMax", 14f,
                "Highest barrel angle in degrees. The real 2A46 installation "
                + "is limited to roughly 14 degrees; the old shared 60 degree "
                + "limit made the tank point into the sky.");
            CfgTankFov = Config.Bind("Tank", "FOV", 20f,
                "Bildwinkel im Geschuetz. Enger als beim BTR (26).");
            CfgTankAmmoId = Config.Bind("Tank", "AmmoItemId", 2053,
                "Item, das ein Schuss verbraucht. 2053 ist die 125-mm-Granate.");
            CfgTankSpawnAmmo = Config.Bind("Tank", "SpawnAmmo", 5,
                "So viele 125-mm-Granaten wandern beim Hinstellen des Panzers in "
                + "den Rucksack. Ohne das steht ein Panzer da, der nicht "
                + "schiesst - und im Log stand nur eine Zeile darueber, im Spiel "
                + "gar nichts. 0 schaltet die Beigabe ab.");
            CfgTankScope = Config.Bind("Tank", "Scope", true,
                "Panzerzielfernrohr statt freier Sicht: schwarze Fassung, runde "
                + "Linse, Winkelmarke mit Entfernungsskala (t72_scope.png). "
                + "Ersetzt im Panzer das einfache Fadenkreuz.");
            // Eigener Schalter, seit das BTR keine Sprenggranaten mehr schiesst:
            // vorher hing beides an Turret/Explosion, und ein Panzer ohne
            // Flaechenwirkung waere ein sehr langsames Gewehr.
            CfgTankExplosion = Config.Bind("Tank", "Explosion", true,
                "Am Einschlag eine Sprenggranate zuenden. Beim Panzer der Sinn "
                + "der Sache - 125 mm sind Artillerie. Das BTR hat dafuer seinen "
                + "eigenen Schalter unter Turret/Explosion, der seit 0.5.1 aus "
                + "ist.");

            CfgArena = Config.Bind("Research", "Arena", false,
                "Auf Tastendruck eine ebene Testflaeche vor dem Spieler bauen, "
                + "aus dem Material des Bodens darunter. Nochmal druecken raeumt "
                + "sie weg. Erkundungswerkzeug, deshalb standardmaessig aus.");
            CfgArenaKey = Config.Bind("Research", "ArenaKey", "F10",
                "Taste fuer die Testflaeche, Name aus UnityEngine.KeyCode.");
            CfgArenaSize = Config.Bind("Research", "ArenaSize", 60f,
                "Kantenlaenge der Testflaeche in Welteinheiten.");
            CfgArenaDistance = Config.Bind("Research", "ArenaDistance", 45f,
                "Wie weit vor dem Spieler die Flaeche entsteht.");

            // Fahrzeuge entstehen sonst nur an den VehicleSpawnPoints der Szene.
            // VehicleSpawnPoint::InstantiateCar macht nichts weiter als
            //     PhotonNetwork.InstantiateSceneObject("VehicleSpawn\\" + name, ...)
            // und setzt danach Kraftstoff, Zustand und die drei Teile. Genau das
            // steht hier nochmal, damit ein BTR dort steht, wo geprueft wird.
            CfgSpawnCar = Config.Bind("Research", "SpawnCar", false,
                "Auf Tastendruck ein Fahrzeug vor dem Spieler erzeugen. "
                + "Erkundungswerkzeug, deshalb standardmaessig aus. Braucht den "
                + "Masterclient - im eigenen Raum ist man das.");
            CfgSpawnCarKey = Config.Bind("Research", "SpawnCarKey", "F7",
                "Taste fuer den Fahrzeugspawn, Name aus UnityEngine.KeyCode.");
            CfgSpawnCarName = Config.Bind("Research", "SpawnCarName", "btr-80a_spawn",
                "Prefabname unter Resources/VehicleSpawn. Moeglich sind "
                + "btr-80a_spawn, paz-672_spawn, uaz-3151_military_spawn, "
                + "uaz-3151_police_spawn, ural-375(mod)_spawn, vaz_1111_spawn, "
                + "zaz-968_spawn.");
            CfgSpawnCarDistance = Config.Bind("Research", "SpawnCarDistance", 12f,
                "Wie weit vor dem Spieler das Fahrzeug entsteht. Weniger als "
                + "die halbe Fahrzeuglaenge setzt es in den Spieler.");

            // ------------------------------------------------------- Patrols
            //
            // Only the keys phase 2 actually uses are bound here. Every key
            // bound today is written into the installed
            // nextday.revival.toolkit.cfg and a later change to its default
            // never reaches that installation (CLAUDE.md, point 4) - so the
            // numbers for detection, convoy and loot are NOT reserved in
            // advance. They arrive with the phase that uses them.
            CfgPatrol = Config.Bind("Patrol", "Enabled", true,
                "NPC vehicle patrols: vehicles that drive a recorded road on "
                + "their own, and their route drawn on the map. ON by default "
                + "since 0.5.13: it was off \"until it has been seen once\", "
                + "and the effect was that the second machine installed every "
                + "asset correctly and still saw an empty map, because this one "
                + "line returns before anything is drawn. The driver, the gun "
                + "and the crew - no convoy yet.");
            CfgPatrolKey = Config.Bind("Patrol", "Key", "F11",
                "Puts one more patrol vehicle on the active route. Hold Shift "
                + "to take every patrol off the road again. Name out of "
                + "UnityEngine.KeyCode.");
            CfgPatrolAutoKey = Config.Bind("Patrol", "RecordAutoKey", "F5",
                "Toggles route recording: a waypoint every RecordSeconds while "
                + "you walk or drive. This is how a route is made.");
            CfgPatrolRecordKey = Config.Bind("Patrol", "RecordKey", "F6",
                "Appends a single waypoint where you stand, recording or not.");
            CfgPatrolFile = Config.Bind("Patrol", "RouteFile", "ndr_routes.tsv",
                "The route file, next to the DLL in BepInEx\\plugins\\assets. "
                + "The recorder writes it, python routecheck.py --pull fetches "
                + "it back into the repository.");
            CfgPatrolRoute = Config.Bind("Patrol", "RouteName", "R1",
                "Which route is driven, and which one the recorder appends to.");
            // The old boolean "Tank" is gone from the code on purpose. A key
            // that is bound writes its value into the installed
            // nextday.revival.toolkit.cfg and that value wins forever
            // (CLAUDE.md point 4) - the way to retire a setting is to stop
            // binding it. An orphaned Tank=false line in an old config file
            // is now ignored.
            CfgPatrolVehicle = Config.Bind("Patrol", "Vehicle", "mixed",
                "What drives the route: btr, tank, or mixed. Mixed alternates "
                + "- the first patrol is a BTR-80A, the second a T-72, and so "
                + "on, so both kinds are on the road at once.");
            CfgPatrolMax = Config.Bind("Patrol", "MaxVehicles", 4,
                "How many patrol vehicles may be out at once. Each press of "
                + "Key puts one more down; Shift plus Key takes them all off "
                + "the road again.");
            CfgPatrolAuto = Config.Bind("Patrol", "AutoStart", true,
                "Patrols without a key press: MaxVehicles of them go out by "
                + "themselves once the world is up, and a lost one is replaced "
                + "after RespawnSeconds. They start on the far side of the "
                + "route, not in front of the player. Shift plus the patrol key "
                + "takes them all off AND switches the automatic off until the "
                + "key is pressed again.");
            CfgPatrolRespawn = Config.Bind("Patrol", "RespawnSeconds", 300f,
                "Seconds between a patrol being destroyed and the next one "
                + "taking its place. Counted from the destruction, not from the "
                + "moment the wreck disappears, so a value under WreckSeconds "
                + "means the replacement is already driving while the wreck "
                + "still burns. 0 fills the road once and never again.");
            CfgPatrolGun = Config.Bind("Patrol", "Gun", true,
                "The gun on a patrol vehicle looks for players by itself and "
                + "shoots at them. Off leaves the turret pointing forward.");
            CfgPatrolGunRange = Config.Bind("Patrol", "GunRange", 120f,
                "Metres. Beyond this a player is not a target at all. Also "
                + "the range at which the hit chance has fallen to zero. It "
                + "was 220 until 2026-08-30, which is further than a man can "
                + "make out a BTR against a treeline - being shot from there "
                + "is being shot by nothing.");
            CfgPatrolGunEffective = Config.Bind("Patrol", "GunEffectiveRange", 30f,
                "Metres. Inside this the gun shoots as well as it can; between "
                + "here and GunRange the hit chance falls off along a cosine, "
                + "the same shape NPC_FirearmWeaponController uses. At 30 m the "
                + "whole middle distance becomes a falling chance instead of a "
                + "certainty.");
            CfgPatrolGunPointBlank = Config.Bind("Patrol", "GunPointBlank", 12f,
                "Metres under which a shot is displaced by a hand's width "
                + "whatever the roll says. The game's own NPCs use 30 m for "
                + "this, and that ring is the single reason a patrol was an "
                + "80 percent death sentence: inside it accuracy meant "
                + "nothing. 0 switches the free ring off and rolls every "
                + "shot.");
            CfgPatrolGunNotice = Config.Bind("Patrol", "GunNotice", 2.5f,
                "Seconds a target has to be visible before the first shot. "
                + "Without it you are hit in the frame you step out of cover.");
            CfgPatrolGunForget = Config.Bind("Patrol", "GunForget", 8f,
                "Seconds out of sight before the gun gives the target up.");
            CfgPatrolGunAccuracy = Config.Bind("Patrol", "GunAccuracy", 0.45f,
                "Multiplier on the hit chance. 1 is the game's own NPC "
                + "behaviour, 0.5 makes every second aimed shot a miss, 0 "
                + "makes the gun harmless. It applies at every distance now, "
                + "point blank included - see GunPointBlank.");
            CfgPatrolGunDamage = Config.Bind("Patrol", "GunDamage", 22f,
                "Damage of ONE hit from a patrol gun. 0 takes the vehicle's "
                + "own value out of [Turret] or [Tank] instead - and those are "
                + "meant for a vehicle, not for a man: 120 a shot at eight "
                + "shots a second is not a fight, it is a verdict. The tank's "
                + "shell explodes on top of this, and that is where a tank "
                + "gets its lethality from.");
            CfgPatrolGunTankDamage = Config.Bind("Patrol", "GunTankDamage", 180f,
                "Durability removed from a T-72 by one APC burst. VehicleGameSystem "
                + "accepts damage only once per 0.3 seconds, so the three rapid "
                + "shots form one armour hit. This does not increase damage to "
                + "players on foot.");
            CfgPatrolGunBurst = Config.Bind("Patrol", "GunBurst", 3,
                "Shots in one burst before the gun pauses. Only for a fast "
                + "gun - anything that reloads for a second or more, which "
                + "means the tank, fires single shots whatever stands here.");
            CfgPatrolGunBurstPause = Config.Bind("Patrol", "GunBurstPause", 3.5f,
                "Seconds between two bursts. This, not the damage, is what "
                + "gives a man time to get behind something.");
            CfgPatrolCrew = Config.Bind("Patrol", "Crew", true,
                "Every seat carries a man. They are counted while the vehicle "
                + "drives and they climb out when it is destroyed - marauders "
                + "who then hunt whoever killed it. No player can take a seat "
                + "on a patrol vehicle while its crew is aboard.");
            CfgPatrolCrewMax = Config.Bind("Patrol", "CrewMax", 4,
                "Upper limit on the men who climb out of one wreck, whatever "
                + "the vehicle has seats for. Six marauders at once out of one "
                + "BTR is a firefight nobody survives.");
            CfgPatrolCrewHealth = Config.Bind("Patrol", "CrewHealth", 120f,
                "Hit points of one crewman.");
            CfgPatrolCrewLevel = Config.Bind("Patrol", "CrewLevel", 2,
                "NPC level of the crew. Feeds the game's own level table.");
            CfgPatrolWreck = Config.Bind("Patrol", "WreckSeconds", 240f,
                "Seconds a destroyed patrol vehicle stays on the road before "
                + "it is removed. 0 leaves the wreck where it is.");
            CfgPatrolSpeed = Config.Bind("Patrol", "Speed", 45f,
                "Target speed in km/h on a straight leg. A waypoint may name "
                + "its own speed in the file; this is what 0 there means. "
                + "A BTR with a 12 ton centre of mass rolls over in a corner "
                + "long before it runs out of engine.");
            CfgPatrolRecordSeconds = Config.Bind("Patrol", "RecordSeconds", 3f,
                "Seconds between two recorded waypoints. At 45 km/h three "
                + "seconds are about 37 m, which is the spacing routecheck.py "
                + "asks for (25 to 40). Recording on foot writes a much denser "
                + "route - that is allowed, it only means routecheck.py will "
                + "warn about the spacing. A waypoint is never written twice "
                + "at the same spot: three metres of movement are required "
                + "however long the wait was.");
            CfgPatrolPassRadius = Config.Bind("Patrol", "PassRadius", 12f,
                "How near a waypoint counts as reached. Too large and the "
                + "vehicle skips whole corners.");
            CfgPatrolStuck = Config.Bind("Patrol", "StuckSeconds", 3f,
                "Seconds with the throttle down and under 3 km/h before the "
                + "vehicle counts as stuck and starts backing up.");
            CfgPatrolRam = Config.Bind("Patrol", "RamAfter", 8f,
                "Seconds stuck before it stops avoiding and drives through "
                + "whatever is there.");
            CfgPatrolFree = Config.Bind("Patrol", "FreeAfter", 25f,
                "Seconds stuck before the vehicle is lifted onto the next "
                + "waypoint. Every one of those is logged, because it names a "
                + "waypoint that wants fixing.");

            // ------------------------------------------- the AI tank shell
            //
            // [Tank] holds what a PLAYER fires: 1600 damage in a 16 m radius,
            // which is artillery and is meant to be. An AI landing that shell
            // anywhere near a man kills him with no counterplay at all, and no
            // accuracy setting helps - the blast does not care where the shell
            // went. So the AI gets its own two numbers.
            CfgPatrolShellDamage = Config.Bind("Patrol", "ShellDamage", 260f,
                "Blast damage of a shell fired by an AI tank. A player in his "
                + "own tank keeps [Tank] ExplosionDamage (1600). 0 takes the "
                + "player value, which is very nearly a guaranteed kill.");
            CfgPatrolShellRadius = Config.Bind("Patrol", "ShellRadius", 6f,
                "Blast radius of a shell fired by an AI tank, in metres. A "
                + "player keeps [Tank] ExplosionRadius (16). 0 takes the "
                + "player value.");

            // ------------------------------------------ routes and factions
            CfgPatrolFraction = Config.Bind("Patrol", "Fraction", "looter",
                "Which side a patrol is on when its route does not say. One "
                + "of civilian, looter, traitor, neutral. civilian attacks "
                + "everyone but civilians, looter everyone but looters, "
                + "traitor attacks EVERYONE including other traitors, and "
                + "neutral attacks traitors only. A route carries its own "
                + "choice in the flags of its first waypoint "
                + "(fraction=looter); the route editor writes that for you.");
            CfgPatrolEditorKey = Config.Bind("Patrol", "EditorKey", "F4",
                "Opens the route editor: record a route, drop single "
                + "waypoints, pick the faction, the vehicle and the number of "
                + "patrols per route, and put one on the road now. Name out "
                + "of UnityEngine.KeyCode. NOT F12: that is Steam's screenshot "
                + "key by default, and every press of it would leave a "
                + "picture behind.");

            // ------------------------------------------------ the obstacles
            CfgPatrolCrush = Config.Bind("Patrol", "Crush", true,
                "Drive THROUGH the small stuff. The map is full of knee-high "
                + "fences, posts and road junk that stop twelve tons dead, and "
                + "a driver that steers around them ends up in the ditch. "
                + "Anything small enough by CrushHeight and CrushWidth loses "
                + "its collider the moment a patrol pushes into it. Only on "
                + "this machine, and only for what a patrol actually hits.");
            CfgPatrolCrushHeight = Config.Bind("Patrol", "CrushHeight", 2.2f,
                "Metres. Taller than this and it is a wall, not a fence - the "
                + "driver steers around it instead of through it.");
            CfgPatrolCrushWidth = Config.Bind("Patrol", "CrushWidth", 9f,
                "Metres. Wider than this and it is a building, whatever its "
                + "height.");

            CfgPatrolCrewSensor = Config.Bind("Patrol", "CrewSensor", 70f,
                "Metres a crewman out of a wreck can see. It is the "
                + "settlement's SensorVisibleDist, and an AddComponent leaves "
                + "that at 0 - a crew that sees nothing stands around its "
                + "wreck and does nothing.");
            CfgPatrolRouteMapWidth = Config.Bind("Patrol", "RouteMapWidth", 60f,
                "Half-width in metres of the orange dashed patrol area drawn "
                + "around every recorded route on the map (minimum 55).");
            CfgPatrolCrewDrone = Config.Bind("Patrol", "CrewDrone", true,
                "A dismounted crew may launch one FPV drone after it has acquired "
                + "a real enemy through the game's own faction logic.");
            CfgPatrolCrewDroneDelay = Config.Bind("Patrol", "CrewDroneDelay", 18f,
                "Minimum seconds between dismounting and launching the crew drone.");
            CfgPatrolCrewDroneSpeed = Config.Bind("Patrol", "CrewDroneSpeed", 16f,
                "Speed of the crew FPV drone in metres per second.");
            CfgPatrolCrewDroneMiss = Config.Bind("Patrol", "CrewDroneMiss", 10f,
                "Deliberate horizontal miss distance in metres. The drone follows "
                + "this offset beside the target instead of being perfectly accurate. "
                + "The generated miss is 45 to 100 percent of this value, so some "
                + "drones explode dramatically beside the player.");
            CfgPatrolCrewDroneDamage = Config.Bind("Patrol", "CrewDroneDamage", 110f,
                "Explosion damage of the crew FPV drone.");
            CfgPatrolCrewDroneRadius = Config.Bind("Patrol", "CrewDroneRadius", 4.5f,
                "Explosion radius of the crew FPV drone in metres.");
            CfgPatrolCrewDroneHitpoints = Config.Bind("Patrol", "CrewDroneHitpoints", 1,
                "Hits needed to intercept a crew FPV drone.");
            CfgPatrolCrewDroneHitRadius = Config.Bind("Patrol", "CrewDroneHitRadius", 2f,
                "Radius in metres accepted around the visible crew FPV for firearm "
                + "interception. The network model has no physics collider, so this "
                + "is its practical hit box.");
            CfgPatrolCrewDroneEventCode = Config.Bind("Patrol", "CrewDroneEventCode", 183,
                "Photon event code for crew FPV drones. It must not overlap the "
                + "player drone, turret or admin channels.");

            // ------------------------------------------------- Diagnostics
            CfgPhotonTimeout = Config.Bind("Diagnostics", "PhotonTimeoutMs", 60000,
                "Milliseconds without a Photon response before the room link is "
                + "declared dead. The game's 15000 ms default drops both clients "
                + "during a short shared UDP interruption; 60000 lets reliable "
                + "commands recover when the link returns.");
            CfgNetWatch = Config.Bind("Diagnostics", "NetWatch", true,
                "Write a wall clock time, every long frame and the Photon "
                + "peer's own counters into the log. It is the only thing that "
                + "tells a busy client apart from a bad link when the game "
                + "reports DisconnectByClientTimeout. This switch controls "
                + "logging only; PhotonTimeoutMs remains active independently.");
            CfgNetWatchHitch = Config.Bind("Diagnostics", "NetWatchHitch", 0.5f,
                "Seconds. A frame longer than this is written to the log with "
                + "the peer counters of that moment.");
            CfgNetWatchEvery = Config.Bind("Diagnostics", "NetWatchEvery", 5f,
                "Seconds between the regular peer reports.");
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

            Items.Add(new ItemDef(
                1162, 1010, true,
                "M72 LAW",
                "Leichter 66-mm-Einweg-Raketenwerfer mit fest geladener "
                + "Hohlladungsrakete. Nach dem einzigen Schuss bleibt ein leeres, "
                + "nicht nachladbares Rohr zurueck. Der Gefahrenbereich hinter dem "
                + "Rohr ist kein Ort fuer Freunde.",
                "law.ndmesh", "law_diffuse.png", "law_normal.png",
                "law_icon.png", "law_weapon_icon.png",
                1, 0, 2.5f));

            Items.Add(new ItemDef(
                2052, 2030, false,
                "M72 Rakete (1)",
                "Ausstellungsstueck einer 66-mm-Hohlladungsrakete. Die M72 wird "
                + "ab Werk geladen und kann im Feld nicht nachgeladen werden.",
                "rocket.ndmesh", "rocket_diffuse.png", "rocket_normal.png",
                "rocket_icon.png", null,
                1, 0, 6.0f));

            Items.Add(new ItemDef(
                2053, 2030, false,
                "125-mm-Granate (1)",
                "Eine Granate fuer die 2A46 des T-72. Getrennt geladen: Geschoss "
                + "mit Aufschlagzuender, dahinter die Treibladung. Ein Einschlag "
                + "reisst mehr weg als die LAW - dafuer dauert das Nachladen im "
                + "Turm zwoelf Sekunden.",
                "shell125.ndmesh", "shell125_diffuse.png", "shell125_normal.png",
                "shell125_icon.png", null,
                1, 0, 9.0f));

            Items.Add(new ItemDef(
                1163, 2030, false,
                "FPV-Drohne",
                "Kleiner Quadrokopter mit Kamera und fest verbautem Sprengkopf. "
                + "Wird auf Tastendruck gestartet und aus der Ferne geflogen; "
                + "waehrenddessen steht man selbst unbeweglich in der Gegend "
                + "herum. Kommt nicht zurueck.",
                "drone.ndmesh", "drone_diffuse.png", "drone_normal.png",
                "drone_icon.png", null,
                1, 0, 1.4f));

            Items.Add(new ItemDef(
                2054, 2030, false,
                "Stoersender R-330 (tragbar)",
                "Breitbandstoerer im Traggestell: Batteriekasten, Verstaerker, "
                + "vier Peitschenantennen. Wer ihn traegt, nimmt jeder Drohne "
                + "im Umkreis von 50 m die Funkstrecke - und weil der Zuender "
                + "davon nichts weiss, geht sie dort hoch, wo sie gerade "
                + "fliegt. Der Preis steht auf der Waage: 26 kg, die sonst "
                + "Munition waeren.",
                "jammer.ndmesh", "jammer_diffuse.png", "jammer_normal.png",
                "jammer_icon.png", null,
                1, 0, 26.0f));

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
                //
                // BEIDE Ueberladungen, seit 0.5.3. Das Spiel selbst ruft
                // Resources.Load(string); Photon aber ruft in
                // NetworkingPeer::DoInstantiate und in
                // PhotonNetwork::InstantiateSceneObject die Form
                // Resources.Load(string, Type) auf. Genau daran ist der Drop
                // eigener Ids bisher gescheitert (E-025): der Hook beantwortete
                // die erste Ladung, Photons zweite lief daran vorbei, kam mit
                // null zurueck, und das Spiel schrieb "DropItem null!".
                MethodInfo byPath = null;
                MethodInfo byPathAndType = null;
                foreach (MethodInfo m in typeof(Resources).GetMethods(
                             BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Load") continue;
                    if (m.IsGenericMethod || m.IsGenericMethodDefinition) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        byPath = m;
                    else if (ps.Length == 2 && ps[0].ParameterType == typeof(string)
                             && ps[1].ParameterType == typeof(Type))
                        byPathAndType = m;
                }
                if (byPath == null) { L.LogError("Resources.Load(string) nicht gefunden."); return; }

                _harmony.Patch(byPath,
                    new HarmonyMethod(typeof(ResourceHook).GetMethod("Prefix")),
                    null, null, null, null);
                if (byPathAndType != null)
                    _harmony.Patch(byPathAndType,
                        new HarmonyMethod(typeof(ResourceHook).GetMethod("Prefix")),
                        null, null, null, null);
                else
                    L.LogWarning("Resources.Load(string, Type) nicht gefunden - "
                                 + "der Drop eigener Ids bleibt oertlich.");
                L.LogInfo("Resources.Load gepatcht (string"
                          + (byPathAndType != null ? " und string,Type" : "") + ").");
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
                Type t = RevivalPlugin.TypeByName("LocalizationManager");
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
                Type t = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                if (t == null) { L.LogWarning("PlayerFirearmWeaponController nicht gefunden."); return; }
                MethodInfo m = AccessTools.Method(t, "ReloadWeapon", null, null);
                if (m == null) { L.LogWarning("ReloadWeapon nicht gefunden."); return; }
                _harmony.Patch(m,
                    new HarmonyMethod(typeof(Diag).GetMethod("ReloadPrefix")), null, null, null, null);
                L.LogInfo("ReloadWeapon-Diagnose aktiv.");
            }
            catch (Exception ex) { L.LogError("Reload-Patch fehlgeschlagen: " + ex.Message); }
        }

        void PatchRocketImpact()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                if (t == null)
                {
                    L.LogWarning("PlayerFirearmWeaponController fuer LAW nicht gefunden.");
                    return;
                }
                MethodInfo m = AccessTools.Method(t, "FireOneShot", null, null);
                if (m == null) { L.LogWarning("FireOneShot fuer LAW nicht gefunden."); return; }
                _harmony.Patch(m, null,
                    new HarmonyMethod(typeof(RocketHook).GetMethod("Postfix")), null, null, null);
                L.LogInfo("M72-LAW-Einschlagexplosion aktiv.");
            }
            catch (Exception ex) { L.LogError("LAW-Patch fehlgeschlagen: " + ex); }
        }

        /// <summary>
        /// The second postfix on `FireOneShot`, and the whole of "a drone can
        /// be shot down". The game's shot is not touched; the hook only reads
        /// the camera the controller aims with and offers that line to the
        /// drones in the sky.
        ///
        /// Two postfixes on one method is deliberate: RocketHook answers for
        /// the LAW and returns immediately for every other weapon, and mixing
        /// the drone into it would tie two features that have nothing to do
        /// with each other to one another's mistakes.
        /// </summary>
        void PatchDroneShot()
        {
            try
            {
                bool playerDrone = CfgDrone.Value && CfgDroneShootable.Value;
                bool crewDrone = CfgPatrolCrewDrone != null
                    && CfgPatrolCrewDrone.Value;
                if (!playerDrone && !crewDrone)
                {
                    L.LogInfo("Drohnen lassen sich nicht abschiessen (abgeschaltet).");
                    return;
                }
                Type t = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                if (t == null)
                {
                    L.LogWarning("PlayerFirearmWeaponController fuer den "
                        + "Drohnenbeschuss nicht gefunden.");
                    return;
                }
                MethodInfo m = AccessTools.Method(t, "FireOneShot", null, null);
                if (m == null)
                {
                    L.LogWarning("FireOneShot fuer den Drohnenbeschuss nicht gefunden.");
                    return;
                }
                _harmony.Patch(m, null,
                    new HarmonyMethod(typeof(DroneShotHook).GetMethod("Postfix")),
                    null, null, null);
                L.LogInfo("Drohnenbeschuss aktiv bis "
                    + CfgDroneShootRange.Value + " m (Spieler- und Crew-FPV).");
            }
            catch (Exception ex) { L.LogError("Drohnenbeschuss-Patch fehlgeschlagen: " + ex); }
        }

        void PatchCustomDrop()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerInventoryManager");
                if (t == null) { L.LogWarning("PlayerInventoryManager fuer LAW-Drop fehlt."); return; }
                MethodInfo m = AccessTools.Method(t, "DropWeaponFromHand",
                    new Type[] { typeof(int), typeof(int), typeof(int), typeof(Vector3),
                                 typeof(Quaternion), typeof(Vector3) }, null);
                if (m == null) { L.LogWarning("DropWeaponFromHand fuer LAW fehlt."); return; }
                _harmony.Patch(m,
                    new HarmonyMethod(typeof(DropHook).GetMethod("Prefix")),
                    null, null,
                    new HarmonyMethod(typeof(DropHook).GetMethod("HandFinalizer")),
                    null);

                MethodInfo inventory = AccessTools.Method(t, "DropInventoryItem",
                    new Type[] { typeof(int), typeof(int), typeof(string), typeof(bool) }, null);
                if (inventory == null) { L.LogWarning("DropInventoryItem fuer LAW fehlt."); return; }
                _harmony.Patch(inventory,
                    new HarmonyMethod(typeof(DropHook).GetMethod("InventoryPrefix")),
                    null, null, null, null);
                L.LogInfo("Netzwerk- und Todes-Drop fuer alle eigenen Items aktiv.");
            }
            catch (Exception ex) { L.LogError("LAW-Drop-Patch fehlgeschlagen: " + ex); }
        }

        /// <summary>
        /// Fire for every explosion. The hook sits on
        /// `ExplosionObject::NetworkVisualizeExplode` on purpose: that method is
        /// the RPC the game sends to everyone in range, so it runs once on the
        /// client that blew something up AND once on every other client. One
        /// postfix, and the fire is there for all of them - our own explosions
        /// (drone, LAW, both guns) as well as the game's grenades.
        ///
        /// A postfix, never a prefix: whatever the game does with its own dust
        /// stays untouched, the fire is put on top of it.
        /// </summary>
        void PatchFire()
        {
            try
            {
                if (!CfgFire.Value) { L.LogInfo("Fire effect switched off."); return; }
                Type t = RevivalPlugin.TypeByName("ExplosionObject");
                if (t == null) { L.LogWarning("ExplosionObject fehlt - kein Feuer."); return; }
                MethodInfo m = null;
                foreach (MethodInfo cand in t.GetMethods(BindingFlags.Instance
                             | BindingFlags.Public | BindingFlags.NonPublic))
                    if (cand.Name == "NetworkVisualizeExplode") { m = cand; break; }
                if (m == null)
                {
                    L.LogWarning("NetworkVisualizeExplode fehlt - kein Feuer.");
                    return;
                }
                _harmony.Patch(m, null,
                    new HarmonyMethod(typeof(FireHook).GetMethod("Postfix")),
                    null, null, null);
                L.LogInfo("Feuer auf jeder Explosion aktiv ("
                          + m.GetParameters().Length + " Parameter).");
            }
            catch (Exception ex) { L.LogError("Feuer-Patch fehlgeschlagen: " + ex); }
        }

        /// <summary>
        /// Haengt den Finalizer an das Rueckenmodell. Begruendung bei
        /// <see cref="SpineGuard"/>.
        /// </summary>
        void PatchWeaponSpine()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerMenuCustomizationManager");
                if (t == null)
                {
                    L.LogWarning("PlayerMenuCustomizationManager nicht gefunden.");
                    return;
                }
                MethodInfo m = AccessTools.Method(t, "WeaponSpineInstanceManager", null, null);
                if (m == null)
                {
                    L.LogWarning("WeaponSpineInstanceManager nicht gefunden.");
                    return;
                }
                _harmony.Patch(m, null, null, null,
                    new HarmonyMethod(typeof(SpineGuard).GetMethod("Finalizer")), null);
                L.LogInfo("Rueckenmodell abgesichert (WeaponSpineInstanceManager).");
            }
            catch (Exception ex) { L.LogError("Spine-Patch fehlgeschlagen: " + ex.Message); }
        }

        /// <summary>
        /// Haengt die Vorlagenpruefung vor jede Item-Abfrage des Spiels.
        /// Begruendung bei <see cref="Registry.RepairIfDead"/>.
        /// </summary>
        void PatchSpawnLookup()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("ItemSpawnCategoriesDB");
                if (t == null) { L.LogWarning("ItemSpawnCategoriesDB nicht gefunden."); return; }
                MethodInfo m = AccessTools.Method(t, "GetItemSpawnedScriptByID", null, null);
                if (m == null) { L.LogWarning("GetItemSpawnedScriptByID nicht gefunden."); return; }
                _harmony.Patch(m,
                    new HarmonyMethod(typeof(Registry).GetMethod("LookupPrefix")),
                    null, null, null, null);
                L.LogInfo("Vorlagenpruefung aktiv (GetItemSpawnedScriptByID).");
            }
            catch (Exception ex) { L.LogError("Vorlagen-Patch fehlgeschlagen: " + ex.Message); }
        }

        /// <summary>
        /// Haengt den Wachhund an Object.Destroy. Begruendung bei
        /// <see cref="SpawnWatch"/>.
        /// </summary>
        void PatchDestroyWatch()
        {
            if (!CfgWatchTemplates.Value) return;
            try
            {
                int n = 0;
                foreach (MethodInfo m in typeof(UnityEngine.Object).GetMethods(
                             BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Destroy" && m.Name != "DestroyImmediate") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 1 || ps[0].ParameterType != typeof(UnityEngine.Object)) continue;
                    _harmony.Patch(m,
                        new HarmonyMethod(typeof(SpawnWatch).GetMethod("DestroyPrefix")),
                        null, null, null, null);
                    n++;
                }
                L.LogInfo("Vorlagenwache aktiv (" + n + " Destroy-Ueberladungen).");
            }
            catch (Exception ex) { L.LogError("Destroy-Patch fehlgeschlagen: " + ex.Message); }
        }

        void PatchBackpackDiagnostics()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerInventoryManager");
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

            // The region list has nothing to do with our own items, so it is
            // handled before the bail-out below.
            Regions.Apply(true);

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
            // First in the frame: it measures the gap to the previous frame,
            // and everything below is part of that gap.
            NetWatch.Tick();
            Admin.Tick();
            // Solange das Menue offen ist, gehoert der Zeiger dem Menue -
            // sonst zieht CursorGuard ihn jeden Frame zurueck ins Fenster und
            // man kann keinen Knopf treffen.
            if (Admin.IsOpen || Patrol.EditorOpen) CursorGuard.Release();
            else CursorGuard.Tick();
            Regions.Tick();
            Research.Tick();
            Turret.Tick();
            Drone.Tick();
            Arena.Tick();
            CarSpawn.Tick();
            Patrol.Tick();
            CrewDrone.Tick();
        }

        void FixedUpdate()
        {
            Patrol.FixedTick();
        }

        void LateUpdate()
        {
            CameraOwner.LateTick();
        }

        void OnGUI()
        {
            Turret.DrawScope();
            Drone.Draw();
            Patrol.DrawMap();
            Admin.Draw();
            Patrol.Draw();
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
            if (Regions.Label(__0, ref __result)) return;
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
    /// Haengt die vorhandene Granatenexplosion an den Hitscan-Treffer der LAW.
    /// Alle Spieltypen bleiben absichtlich ueber Reflection angebunden.
    /// </summary>
    public static class RocketHook
    {
        const int LAW_ID = 1162;
        const string EXPLOSION_PREFAB = "PlayerDataPrefabs/Throw/1403_Throw";
        static bool _loggedId;

        public static void Postfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                object weaponData = GetField(__instance, "_weaponFirearmData");
                if (weaponData == null) return;

                int itemId = ObscuredInt(GetField(weaponData, "ItemID"));
                if (itemId != LAW_ID) return;
                if (!_loggedId)
                {
                    _loggedId = true;
                    RevivalPlugin.L.LogInfo("M72 LAW erkannt: ItemID " + itemId);
                }

                Transform cameraTransform = GetField(__instance, "MainCamera") as Transform;
                if (cameraTransform == null)
                    throw new InvalidOperationException("MainCamera ist null oder kein Transform.");

                float maximumRange = ObscuredFloat(GetField(weaponData, "MaximumRange"));
                Vector3 hitPoint, hitNormal;
                List<Vector3> trajectory;
                Vector3 launchPoint = cameraTransform.position
                    + cameraTransform.forward * 0.85f - cameraTransform.up * 0.34f;
                bool didHit = TraceTrajectory(launchPoint, cameraTransform.forward,
                                              maximumRange, out hitPoint, out hitNormal,
                                              out trajectory);
                SpawnTracer(trajectory);
                if (!didHit) return;

                Detonate(hitPoint + hitNormal * 0.15f, 900f, 12f, 3f);
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("M72-LAW-Einschlagexplosion fehlgeschlagen: " + ex);
            }
        }

        /// <summary>
        /// Zuendet eine Sprenggranate an einem Punkt. Herausgeloest, weil das
        /// Geschuetz des BTR dieselben sechs Schritte braucht - Prefab ueber
        /// PhotonNetwork erzeugen, Physik ruhigstellen, Collider auf
        /// IgnoreLocalPlayer, dann SetExplosionData und StartExplosion.
        ///
        /// `ExplosionObject::Explode` prueft `photonView.isMine`; ein blosses
        /// Object.Instantiate reicht deshalb nicht.
        /// </summary>
        internal static void Detonate(Vector3 point, float damage, float radius, float lifeTime)
        {
            GameObject spawned = PhotonInstantiate(EXPLOSION_PREFAB, point,
                                                   Quaternion.identity, (byte)0);
            if (spawned == null)
                throw new InvalidOperationException("PhotonNetwork.Instantiate lieferte null.");

            Type bodyType = RevivalPlugin.TypeByName("UnityEngine.Rigidbody");
            Component body = bodyType == null ? null : spawned.GetComponent(bodyType);
            if (body == null && bodyType != null)
                body = spawned.GetComponentInChildren(bodyType);
            if (body != null)
            {
                SetProperty(body, "velocity", Vector3.zero);
                SetProperty(body, "angularVelocity", Vector3.zero);
                SetProperty(body, "useGravity", false);
                SetProperty(body, "isKinematic", true);
            }

            int ignoreLayer = LayerMask.NameToLayer("IgnoreLocalPlayer");
            Type colliderType = RevivalPlugin.TypeByName("UnityEngine.Collider");
            Component[] colliders = colliderType == null
                ? new Component[0] : spawned.GetComponentsInChildren(colliderType, true);
            for (int i = 0; i < colliders.Length; i++)
                colliders[i].gameObject.layer = ignoreLayer;

            Type explosionType = RevivalPlugin.TypeByName("ExplosionObject");
            if (explosionType == null)
                throw new MissingMemberException("ExplosionObject nicht gefunden.");
            Component explosion = spawned.GetComponent(explosionType);
            if (explosion == null)
                throw new MissingMemberException("ExplosionObject fehlt am Granaten-Prefab.");

            MethodInfo setData = AccessTools.Method(explosionType, "SetExplosionData",
                new Type[] { typeof(float), typeof(float), typeof(float), typeof(float) }, null);
            MethodInfo start = AccessTools.Method(explosionType, "StartExplosion",
                new Type[] { typeof(float) }, null);
            if (setData == null || start == null)
                throw new MissingMethodException("ExplosionObject-Methoden nicht gefunden.");

            setData.Invoke(explosion, new object[] { damage, radius, lifeTime, 0f });
            IEnumerator routine = start.Invoke(explosion, new object[] { 0.02f }) as IEnumerator;
            MonoBehaviour behaviour = explosion as MonoBehaviour;
            if (routine == null || behaviour == null)
                throw new InvalidOperationException("StartExplosion lieferte keine Coroutine.");
            behaviour.StartCoroutine(routine);
        }

        static bool TraceTrajectory(Vector3 origin, Vector3 direction, float range,
                                    out Vector3 point, out Vector3 normal,
                                    out List<Vector3> path)
        {
            const float speed = 95.0f;
            const float gravity = 14.0f;
            const float stepTime = 0.08f;

            direction.Normalize();
            point = Vector3.zero;
            normal = Vector3.zero;
            path = new List<Vector3>();
            path.Add(origin);

            Vector3 previous = origin;
            int steps = Math.Max(1, (int)Math.Ceiling(range / (speed * stepTime)));
            for (int i = 1; i <= steps; i++)
            {
                float time = i * stepTime;
                Vector3 next = origin + direction * (speed * time)
                    + Vector3.down * (0.5f * gravity * time * time);
                Vector3 segment = next - previous;
                float length = segment.magnitude;
                if (length > 0.0001f
                    && Raycast(previous, segment / length, length, out point, out normal))
                {
                    path.Add(point);
                    return true;
                }
                path.Add(next);
                previous = next;
            }
            point = previous;
            return false;
        }

        // EIN Material fuer alle Leuchtspuren, nicht eines je Schuss.
        //
        // Bis 0.5.1 legte jeder Schuss ein eigenes `new Material(shader)` an.
        // Bei der LAW war das gleichgueltig - eine Rakete alle paar Sekunden.
        // Das BTR-Geschuetz schiesst seit 0.5.2 acht mal je Sekunde, und ein
        // zur Laufzeit erzeugtes Material wird von `Destroy(tracer)` NICHT
        // mitgenommen: das waeren knapp fuenfhundert liegengebliebene
        // Materialien je Minute Dauerfeuer. `HideAndDontSave` haelt das eine
        // ueber den Szenenwechsel; wird es doch einmal abgeraeumt, ist der
        // Unity-Vergleich mit null true und es entsteht ein neues.
        static Material _tracerMat;
        static readonly Color TracerFarbe = new Color(1.0f, 0.88f, 0.42f, 1.0f);

        static Material TracerMaterial()
        {
            if (_tracerMat != null) return _tracerMat;

            Shader shader = Shader.Find("Particles/Additive");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) return null;

            Material m = new Material(shader);
            if (m.HasProperty("_Color")) m.SetColor("_Color", TracerFarbe);
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", TracerFarbe);
            m.hideFlags = HideFlags.HideAndDontSave;
            _tracerMat = m;
            return _tracerMat;
        }

        internal static void SpawnTracer(List<Vector3> points)
        {
            SpawnTracer(points, 0.035f, 0.012f, Color.white, TracerFarbe, 0.22f);
        }

        /// <summary>
        /// Leuchtspur mit vorgegebener Breite, Farbe und Standzeit.
        ///
        /// Die Rakete braucht einen duennen Faden, das Panzergeschuetz einen
        /// Balken - dieselbe Bahn, aber um mehr als eine Zehnerpotenz andere
        /// Masse. Bis 0.5.2 hatten beide dieselben festen Werte, und die waren
        /// an der Rakete ausgerichtet: 0.035 Spieleinheiten sind bei rund drei
        /// Einheiten je Meter gut ein Zentimeter Breite. Fuer ein
        /// 125-mm-Geschoss auf dreihundert Meter ist das nichts.
        ///
        /// Die Farbe kommt NICHT ueber das Material. Das ist seit 0.5.2 eines
        /// fuer alle Spuren und wird geteilt (`sharedMaterial`) - wer es faerbt,
        /// faerbt jede laufende Spur mit. Der LineRenderer hat dafuer eigene
        /// Eckfarben, und `Particles/Additive` multipliziert sie mit der
        /// Materialfarbe. Deshalb zwei Farben je Aufruf statt einer im Material.
        ///
        /// C# 3.0 kennt keine optionalen Argumente, deshalb eine Ueberladung
        /// statt Standardwerten.
        /// </summary>
        internal static void SpawnTracer(List<Vector3> points, float startWidth,
                                         float endWidth, Color vorn, Color hinten,
                                         float standzeit)
        {
            if (points == null || points.Count < 2) return;
            GameObject tracer = new GameObject("NDR Leuchtspur");
            LineRenderer line = tracer.AddComponent(typeof(LineRenderer)) as LineRenderer;
            if (line == null)
            {
                UnityEngine.Object.Destroy(tracer);
                throw new MissingMemberException("LineRenderer konnte nicht erzeugt werden.");
            }

            Material material = TracerMaterial();
            if (material == null)
            {
                UnityEngine.Object.Destroy(tracer);
                throw new MissingMemberException("Shader fuer LAW-Leuchtspur nicht gefunden.");
            }

            line.sharedMaterial = material;
            line.useWorldSpace = true;
            line.positionCount = points.Count;
            line.startWidth = startWidth;
            line.endWidth = endWidth;
            line.startColor = vorn;
            line.endColor = hinten;
            for (int i = 0; i < points.Count; i++) line.SetPosition(i, points[i]);
            UnityEngine.Object.Destroy(tracer, standzeit);
        }

        static object GetField(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo f = AccessTools.Field(instance.GetType(), name);
            if (f == null) throw new MissingFieldException(instance.GetType().FullName, name);
            return f.GetValue(instance);
        }

        static int ObscuredInt(object value)
        {
            object result = InvokeImplicit(value, typeof(int));
            return (int)result;
        }

        static float ObscuredFloat(object value)
        {
            object result = InvokeImplicit(value, typeof(float));
            return (float)result;
        }

        static object InvokeImplicit(object value, Type returnType)
        {
            if (value == null) throw new ArgumentNullException("value");
            Type valueType = value.GetType();
            MethodInfo[] methods = valueType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "op_Implicit" || m.ReturnType != returnType) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == valueType)
                    return m.Invoke(null, new object[] { value });
            }
            throw new MissingMethodException(valueType.FullName,
                                             "op_Implicit -> " + returnType.FullName);
        }

        static bool Raycast(Vector3 origin, Vector3 direction, float range,
                            out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            Type physicsType = RevivalPlugin.TypeByName("UnityEngine.Physics");
            Type hitType = RevivalPlugin.TypeByName("UnityEngine.RaycastHit");
            if (physicsType == null || hitType == null)
                throw new MissingMemberException("UnityEngine.Physics oder RaycastHit nicht gefunden.");

            MethodInfo chosen = null;
            MethodInfo[] methods = physicsType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "Raycast" || m.ReturnType != typeof(bool)) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 4 && ps[0].ParameterType == typeof(Vector3)
                    && ps[1].ParameterType == typeof(Vector3)
                    && ps[2].ParameterType.IsByRef
                    && ps[2].ParameterType.GetElementType() == hitType
                    && ps[3].ParameterType == typeof(float))
                {
                    chosen = m;
                    break;
                }
            }
            if (chosen == null)
                throw new MissingMethodException("Physics.Raycast(Vector3,Vector3,out RaycastHit,float)");

            object boxedHit = Activator.CreateInstance(hitType);
            object[] args = new object[] { origin, direction, boxedHit, range };
            bool didHit = (bool)chosen.Invoke(null, args);
            if (!didHit) return false;
            boxedHit = args[2];

            PropertyInfo pointProperty = hitType.GetProperty("point", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo normalProperty = hitType.GetProperty("normal", BindingFlags.Public | BindingFlags.Instance);
            if (pointProperty == null || normalProperty == null)
                throw new MissingMemberException("RaycastHit.point oder normal nicht gefunden.");
            point = (Vector3)pointProperty.GetValue(boxedHit, null);
            normal = (Vector3)normalProperty.GetValue(boxedHit, null);
            return true;
        }

        static void SetProperty(object instance, string name, object value)
        {
            MethodInfo setter = AccessTools.PropertySetter(instance.GetType(), name);
            if (setter == null)
                throw new MissingMethodException(instance.GetType().FullName, "set_" + name);
            setter.Invoke(instance, new object[] { value });
        }

        static GameObject PhotonInstantiate(string path, Vector3 position,
                                            Quaternion rotation, byte group)
        {
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (photon == null) throw new MissingMemberException("PhotonNetwork nicht gefunden.");
            MethodInfo chosen = null;
            MethodInfo[] methods = photon.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "Instantiate") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 4 && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType == typeof(Vector3)
                    && ps[2].ParameterType == typeof(Quaternion)
                    && ps[3].ParameterType == typeof(byte))
                {
                    chosen = m;
                    break;
                }
            }
            if (chosen == null)
                throw new MissingMethodException("PhotonNetwork.Instantiate(string,Vector3,Quaternion,byte)");
            return chosen.Invoke(null, new object[] { path, position, rotation, group }) as GameObject;
        }
    }

    /// <summary>
    /// Puts an item of ours on the ground. The game cannot do it for our ids.
    ///
    /// WHY (CONFIRMED, IL, 2026-08-29) - PlayerInventoryManager::
    /// DropInventoryItem builds the path
    /// `GetItemCatData(id).PrefabPatch + id + "_Spawn"`, loads it through
    /// Resources.Load(string) - which ResourceHook answers for our ids - and
    /// then hands the same PATH to PhotonNetwork::InstantiateSceneObject.
    /// Photon loads the prefab a SECOND time, through its own cache and its
    /// own overload, and wants a PhotonView on the result. That second load
    /// is not ours, so the drop ends in "DropItem null!" and nothing lies on
    /// the ground. Same story in DropWeaponFromHand, only there the null
    /// prefab is dereferenced and throws - which used to take PlayerDeath
    /// down with it, before the respawn screen.
    ///
    /// So we drop the item ourselves, locally: mesh, material, collider,
    /// rigidbody. It falls, it lies there, it is gone after five minutes.
    /// What it is NOT: pickupable, and nobody else sees it. A real drop needs
    /// a networked ItemSpawned and is a piece of work of its own.
    /// </summary>
    public static class DropHook
    {
        const int LAW_ID = 1162;

        /// <summary>How long a dropped item stays, in seconds.</summary>
        const float Liegezeit = 300f;

        /// <summary>
        /// The drop out of the inventory, for EVERY id of ours - since 0.5.1
        /// not the LAW alone. The drone hangs on exactly this: it is not a
        /// weapon, it only ever lies in the backpack, and the game's own path
        /// silently did nothing for it.
        /// </summary>
        public static bool InventoryPrefix(object __instance, int __0, int __1,
                                           string __2, bool __3)
        {
            ItemDef def = RevivalPlugin.FindItem(__1);
            if (def == null) return true;
            if (__2 != "WeaponSlot" && __2 != "BackpackSlot") return true;

            // Since 0.5.3 the game's own path can carry our ids, so this hook
            // steps aside. What lands on the ground is then a real scene object
            // with a PhotonView and an ItemSpawned - everybody sees it and it can
            // be picked up. See E-025: the only thing that was missing was the
            // second Resources.Load overload, the one Photon uses.
            //
            // If Photon is not in a room, InstantiateSceneObject returns null
            // without touching the inventory - the item would silently stay in
            // the backpack. In that case the local piece of scenery of 0.5.1 is
            // still better than nothing.
            if (RevivalPlugin.CfgNetDrop != null && RevivalPlugin.CfgNetDrop.Value
                && InRoom())
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo(def.Name + ": drop through the game's own "
                                            + "path (networked scene object).");
                return true;
            }

            try
            {
                FieldInfo spawnerField = AccessTools.Field(__instance.GetType(), "ObjectSpawner");
                Transform spawner = spawnerField == null
                    ? null : spawnerField.GetValue(__instance) as Transform;
                Vector3 position = spawner == null
                    ? Vector3.zero : spawner.position;
                Ablegen(def, position, Vector3.zero);

                if (__2 == "WeaponSlot")
                {
                    MethodInfo clear = AccessTools.Method(__instance.GetType(), "ClearWeaponSlot",
                        new Type[] { typeof(int), typeof(int), typeof(bool), typeof(bool) }, null);
                    if (clear == null) throw new MissingMethodException("ClearWeaponSlot fehlt.");
                    clear.Invoke(__instance, new object[] { __0, def.Id, true, false });
                }
                else
                {
                    MethodInfo clear = AccessTools.Method(__instance.GetType(), "ClearBackpackSlot",
                        new Type[] { typeof(int), typeof(int), typeof(bool) }, null);
                    if (clear == null) throw new MissingMethodException("ClearBackpackSlot fehlt.");
                    clear.Invoke(__instance, new object[] { __0, def.Id, true });
                }

                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo(def.Name + " aus " + __2 + " " + __0
                                            + " entfernt und lokal abgelegt.");
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("Ablegen aus dem Inventar fehlgeschlagen ("
                                             + def.Id + "): " + ex);
            }
            return false;
        }

        /// <summary>
        /// The drop out of the HAND, for every custom weapon. In a room the
        /// game's networked path is kept; outside a room the local visible
        /// fallback is the only path that can exist.
        /// </summary>
        public static bool Prefix(int __0, int __1, int __2, Vector3 __3,
                                  Quaternion __4, Vector3 __5)
        {
            ItemDef def = RevivalPlugin.FindItem(__0);
            if (def == null) return true;

            if (RevivalPlugin.CfgNetDrop != null && RevivalPlugin.CfgNetDrop.Value
                && InRoom())
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo(def.Name + ": hand/death drop through "
                                            + "the networked scene-object path.");
                return true;
            }

            Ablegen(def, __3, __5);
            return false;
        }

        /// <summary>
        /// A failed hand drop must never abort PlayerDeath. The 2026-08-31 log
        /// proves this exact exception prevented the crew-FPV victim from
        /// reaching the respawn UI. The inactive-template fix below makes the
        /// network path succeed; this finalizer is the last-resort guarantee
        /// that a future prefab failure still leaves one visible object and
        /// lets death finish.
        /// </summary>
        public static Exception HandFinalizer(Exception __exception, int __0,
                                              Vector3 __3, Vector3 __5)
        {
            if (__exception == null) return null;
            ItemDef def = RevivalPlugin.FindItem(__0);
            if (def == null) return __exception;
            Ablegen(def, __3, __5);
            if (RevivalPlugin.L != null)
                RevivalPlugin.L.LogWarning(def.Name + ": hand/death network drop "
                    + "failed (" + __exception.GetType().Name + "); local fallback "
                    + "created and PlayerDeath allowed to continue.");
            return null;
        }

        /// <summary>
        /// Is Photon in a room right now? Only then does
        /// PhotonNetwork.InstantiateSceneObject do anything at all - outside a
        /// room it logs and returns null.
        /// </summary>
        static bool InRoom()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PhotonNetwork");
                if (t == null) return false;
                MethodInfo getter = AccessTools.PropertyGetter(t, "inRoom");
                if (getter == null) return false;
                object v = getter.Invoke(null, null);
                return v is bool && (bool)v;
            }
            catch { return false; }
        }

        /// <summary>
        /// Builds the thing that lies on the ground: one MeshFilter, one
        /// MeshRenderer, a box, a rigidbody. Nothing else.
        ///
        /// Deliberately NOT a clone of the spawn prefab: that one is full of
        /// MeshFilters, ItemSpawned and Photon components. The LAW attempt
        /// with it produced several tubes and a pickup prompt that could never
        /// succeed. Geometry comes from the model prefab (weapons) or from the
        /// inventory prefab (everything else - the drone).
        /// </summary>
        static void Ablegen(ItemDef def, Vector3 position, Vector3 velocity)
        {
            try
            {
                Mesh mesh;
                Material[] mats;
                if (!Quelle(def, out mesh, out mats))
                    throw new MissingMemberException(def.Id + ": keine Geometrie zum Ablegen.");

                GameObject drop = new GameObject(def.Name + " (abgelegt)");
                MeshFilter filter = drop.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = drop.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = mats;
                drop.transform.position = position + Vector3.up * 0.35f;
                drop.transform.rotation = def.Id == LAW_ID
                    ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.identity;

                Type colliderType = RevivalPlugin.TypeByName("UnityEngine.BoxCollider");
                Component collider = colliderType == null ? null : drop.AddComponent(colliderType);
                if (collider == null)
                    throw new MissingMemberException("BoxCollider fuer die Ablage fehlt.");
                SetProperty(collider, "center", mesh.bounds.center);
                SetProperty(collider, "size", mesh.bounds.size);

                Type bodyType = RevivalPlugin.TypeByName("UnityEngine.Rigidbody");
                Component body = bodyType == null ? null : drop.GetComponent(bodyType);
                if (body == null && bodyType != null) body = drop.AddComponent(bodyType);
                if (body != null)
                {
                    SetProperty(body, "useGravity", true);
                    SetProperty(body, "isKinematic", false);
                    Vector3 v = velocity;
                    if (v.magnitude > 15f) v = v.normalized * 15f;
                    SetProperty(body, "velocity", v);
                    SetProperty(body, "angularVelocity", new Vector3(0f, 0.8f, 0f));
                }

                UnityEngine.Object.Destroy(drop, Liegezeit);
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo(def.Name + " liegt bei " + drop.transform.position
                                            + ", " + (int)Liegezeit + " s lang.");
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("Ablage fehlgeschlagen (" + def.Id + "): " + ex);
            }
        }

        /// <summary>
        /// Mesh and materials for the dropped object. Weapons have a model
        /// prefab for the hand; everything else has only the inventory prefab,
        /// and its first MeshFilter carries our own geometry (SwapGeometry put
        /// it there). Materials come from the SAME object as the mesh -
        /// otherwise a drop ends up wearing the donor's material.
        /// </summary>
        static bool Quelle(ItemDef def, out Mesh mesh, out Material[] mats)
        {
            mesh = null;
            mats = null;
            GameObject source = null;
            if (def.IsWeapon) source = def.Factory.GetModelPrefab();
            if (source == null) source = def.Factory.GetSpawnPrefab(null);
            if (source == null) return false;

            MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i] == null || filters[i].sharedMesh == null) continue;
                MeshRenderer r = filters[i].GetComponent<MeshRenderer>();
                if (r == null) r = source.GetComponentInChildren<MeshRenderer>(true);
                if (r == null) continue;
                mesh = filters[i].sharedMesh;
                mats = r.sharedMaterials;
                return true;
            }
            return false;
        }

        static void SetProperty(object instance, string name, object value)
        {
            MethodInfo setter = AccessTools.PropertySetter(instance.GetType(), name);
            if (setter == null)
                throw new MissingMethodException(instance.GetType().FullName, "set_" + name);
            setter.Invoke(instance, new object[] { value });
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

        /// <summary>
        /// Wie <see cref="Texture"/>, aber still, wenn die Datei nicht da ist.
        /// Fuer Karten, die nur MANCHE Stuecke haben - die Metallic/Gloss-Map
        /// liegt neben MG42 und LAW, nicht neben Gurt, Munition und TAC-50.
        /// `Texture` wuerde dort bei jedem Bau eine Warnung ins Log schreiben,
        /// die nichts meldet ausser "ist so gedacht".
        /// </summary>
        public static Texture2D TextureIfPresent(string fileName)
        {
            if (fileName == null) return null;
            if (_tex.ContainsKey(fileName)) return _tex[fileName];
            if (!File.Exists(Path.Combine(RevivalPlugin.AssetDir, fileName))) return null;
            return Texture(fileName, true, true);
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

        /// <summary>
        /// Is the built inventory template still there? Uses Unity's own null
        /// operator on purpose: a destroyed object is not `null` in the CLR
        /// sense, it only compares equal to null - and exactly that state is
        /// what turns GetItemSpawnedScriptByID into a liar.
        /// </summary>
        public bool SpawnAlive
        {
            get { return _spawn != null && _mySpawned != null; }
        }

        /// <summary>Drops the cache so the next call builds a fresh template.</summary>
        public void ForgetSpawn()
        {
            _spawn = null;
            _mySpawned = null;
            _spawnFailed = false;
        }

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
            Shader spende = null;
            try
            {
                if (donor != null)
                {
                    MeshRenderer[] rs = donor.GetComponentsInChildren<MeshRenderer>(true);
                    for (int i = 0; i < rs.Length && spende == null; i++)
                        if (rs[i] != null && rs[i].sharedMaterial != null)
                            spende = rs[i].sharedMaterial.shader;
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Spende-Shader: " + ex.Message); }

            // DER SHADER DES PANZERS, NICHT DER DER SPENDE-WAFFE (2026-08-30)
            //
            // Bis hierher wurde der Shader der Spende uebernommen, und genau
            // das war der Grund, warum die Waffen matt blieben, obwohl neben
            // ihnen eine Metallic-Map lag. `research/dump_material.py` ueber
            // alle 1708 Materialien in resources.assets, nach Texturslots
            // sortiert:
            //
            //   Shader 56  Standard                   nur _MetallicGlossMap
            //   Shader 55  Standard (Specular setup)  nur _SpecGlossMap
            //   Shader 57  Standard (Roughness setup) BEIDE
            //
            // Der T-72 erbt vom MTW und landet auf 56. Dort ist die Smoothness
            // der Alphakanal der Metallic-Map - der Panzer sieht deshalb
            // metallisch aus. Die Waffen erben von ihrer Spende und landen auf
            // 57, und dort kommt die Smoothness NICHT aus dem Alpha, sondern
            // als Roughness aus `_SpecGlossMap`. Der Slot war leer, sein
            // Vorgabewert ist "white" - Roughness 1.0, also Smoothness 0 und
            // kein einziges Glanzlicht auf der ganzen Waffe. Das Log sagte die
            // ganze Zeit "Metall-Map=mg42_metal.png", und sie wurde auch
            // benutzt: nur die Haelfte davon, die das Metallic traegt.
            //
            // Also derselbe Shader wie beim Panzer. Die Spende bleibt der
            // Rueckfall, und darunter steht `_SpecGlossMap`, damit auch dieser
            // Rueckfall nicht wieder matt ist.
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = spende;
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new Exception("kein brauchbarer Shader gefunden");
            RevivalPlugin.L.LogInfo(_def.Id + ": Shader " + shader.name
                + " (Spende-Waffe: "
                + (spende == null ? "unbekannt" : spende.name) + ")");

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

            // Metallic/Gloss-Map, wenn neben der Diffuse eine liegt:
            // <stamm>_metal.png. Der Standardshader liest dann Metallic aus
            // deren ROTEM Kanal und Smoothness aus dem ALPHAKANAL; die beiden
            // Skalarwerte darueber sind damit wirkungslos. So haengt die Karte
            // am MTW des Spiels und seit 0.5.2 am T-72 - und erst sie hat den
            // Panzer aufhoeren lassen, neben dem MTW wie bemaltes Papier
            // auszusehen. Bis 0.5.4 stand hier, es gebe keine solche Karte;
            // das war vor der Messung an btr-80a_alb richtig und ist es nicht
            // mehr. Waffen ohne eigene Karte - TAC-50, Gurt, Munition - gehen
            // weiter durch den unteren Zweig und bleiben, wie sie waren.
            string stamm = null;
            if (_def.Diffuse != null && _def.Diffuse.EndsWith("_diffuse.png"))
                stamm = _def.Diffuse.Substring(
                    0, _def.Diffuse.Length - "_diffuse.png".Length);
            string metalName = stamm == null ? null : stamm + "_metal.png";
            string roughName = stamm == null ? null : stamm + "_rough.png";
            Texture2D metal = Assets.TextureIfPresent(metalName);
            if (metal != null && mat.HasProperty("_MetallicGlossMap"))
            {
                mat.SetTexture("_MetallicGlossMap", metal);
                // 1.0, weil die Daempfung schon im Alphakanal der Karte steckt.
                if (mat.HasProperty("_GlossMapScale")) mat.SetFloat("_GlossMapScale", 1.0f);
                // 0 = Smoothness aus dem Alphakanal der Karte, so wie am MTW.
                if (mat.HasProperty("_SmoothnessTextureChannel"))
                    mat.SetFloat("_SmoothnessTextureChannel", 0f);
                mat.EnableKeyword("_METALLICGLOSSMAP");
                // Ohne diese beiden bleibt das Metall stumpf: Glanzlicht und
                // Spiegelung rechnet der Standardshader nur, wenn beide
                // anstehen. Am Panzer stehen sie aus demselben Grund.
                if (mat.HasProperty("_SpecularHighlights")) mat.SetFloat("_SpecularHighlights", 1f);
                if (mat.HasProperty("_GlossyReflections")) mat.SetFloat("_GlossyReflections", 1f);
                mat.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
                mat.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
            }
            else
            {
                // Keine Karte. Falls der geerbte Zustand eine hat oder das
                // Keyword traegt, hier abraeumen.
                if (mat.HasProperty("_MetallicGlossMap")) mat.SetTexture("_MetallicGlossMap", null);
                mat.DisableKeyword("_METALLICGLOSSMAP");
            }

            // `Standard (Roughness setup)` liest die Smoothness NICHT aus dem
            // Alphakanal oben, sondern als Roughness aus diesem Slot - siehe
            // den Block ueber der Shaderwahl. Hat das Material den Slot, ist es
            // dieser Shader, und dann entscheidet allein diese Datei, ob die
            // Waffe glaenzt. `<stamm>_rough.png` ist der umgekehrte Alphakanal
            // derselben Karte, geschrieben von texlib.save_rough_atlas.
            Texture2D rauh = Assets.TextureIfPresent(roughName);
            if (mat.HasProperty("_SpecGlossMap"))
            {
                if (rauh != null)
                {
                    mat.SetTexture("_SpecGlossMap", rauh);
                    mat.EnableKeyword("_SPECGLOSSMAP");
                }
                else
                {
                    // Ohne eigene Karte waere der Vorgabewert "white", also
                    // Roughness 1 und ein vollstaendig mattes Stueck. Den Slot
                    // leeren heisst hier: der Skalarwert entscheidet wieder.
                    mat.SetTexture("_SpecGlossMap", null);
                    mat.DisableKeyword("_SPECGLOSSMAP");
                }
            }

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
                + " Normal=" + (nrm != null)
                + " Metall-Map=" + (metal != null ? metalName : "keine")
                + " Rauheits-Map=" + (rauh != null ? roughName : "keine")
                + " SpecGlossSlot=" + mat.HasProperty("_SpecGlossMap"));
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
                Type t = RevivalPlugin.TypeByName(names[i]);
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

                // A Resources prefab is dormant. Our runtime clone used to be
                // active for one frame before it was parented below the hidden
                // holder. Its PhotonView.Awake registered that TEMPLATE as a
                // scene object. When Photon later cloned it for a real drop,
                // both objects carried the same view id; PUN destroyed the
                // template and cached the dead object. The next item then did
                // not drop, and a death drop threw before the respawn UI.
                //
                // Clone while the donor is inactive, then make activeSelf true
                // only below an inactive parent. The template never becomes
                // activeInHierarchy; the real world clone is the first object
                // whose PhotonView wakes up.
                bool donorActive = donor.activeSelf;
                if (donorActive) donor.SetActive(false);
                GameObject clone = null;
                try { clone = UnityEngine.Object.Instantiate(donor); }
                finally { if (donorActive) donor.SetActive(true); }
                if (clone == null) throw new Exception("Spende konnte nicht geklont werden");
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
                SpawnWatch.Remember(_def.Id, clone, holder);
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
            int hidden = 0;
            MeshFilter[] mfs = clone.GetComponentsInChildren<MeshFilter>(true);
            MeshFilter primary = null;
            foreach (MeshFilter mf in mfs)
            {
                if (mf == null) continue;
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (primary != null)
                {
                    mf.sharedMesh = null;
                    if (mr != null) mr.enabled = false;
                    hidden++;
                    continue;
                }
                primary = mf;
                mf.sharedMesh = mesh;
                if (mr != null && mat != null)
                {
                    Material[] mats = new Material[Math.Max(1, mr.sharedMaterials.Length)];
                    for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                    mr.sharedMaterials = mats;
                    mr.enabled = true;
                }
                swapped++;
            }
            SkinnedMeshRenderer[] sks = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer sk in sks)
                if (sk != null) { sk.sharedMesh = null; sk.enabled = false; hidden++; }

            // A donor with three LOD renderers was the visible "three copies"
            // report. Keep one complete custom mesh and prevent LODGroup from
            // re-enabling the now empty donor renderers at distance.
            LODGroup[] lods = clone.GetComponentsInChildren<LODGroup>(true);
            for (int i = 0; i < lods.Length; i++)
                if (lods[i] != null) lods[i].enabled = false;

            RevivalPlugin.L.LogInfo(_def.Id + ": Inventar-Geometrie ersetzt ("
                + swapped + " sichtbarer MeshFilter, " + hidden
                + " Spender-Renderer stillgelegt, Skalierung "
                + clone.transform.localScale + ")");
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
    /// <summary>
    /// Says who destroys one of our inventory templates.
    ///
    /// The entry the game reads out of ItemSpawnedDictionary is a COMPONENT on
    /// an object this plugin built. Destroy that object and the entry becomes
    /// Unity's "fake null": the id still has a key, the value still looks like
    /// an object, and every lookup answers null. The item is then unavailable
    /// for the rest of the session - "ItemSpawned is null!".
    ///
    /// A prefix on Object.Destroy is the only place where the CALLER is still
    /// on the stack; in OnDestroy it is long gone, because Unity destroys at
    /// the end of the frame.
    /// </summary>
    public static class SpawnWatch
    {
        class Vorlage
        {
            public int Id;
            public GameObject Go;
            public GameObject Holder;
        }

        static readonly List<Vorlage> _vorlagen = new List<Vorlage>();

        public static void Remember(int id, GameObject go, GameObject holder)
        {
            for (int i = 0; i < _vorlagen.Count; i++)
                if (_vorlagen[i].Id == id) { _vorlagen.RemoveAt(i); break; }
            Vorlage v = new Vorlage();
            v.Id = id; v.Go = go; v.Holder = holder;
            _vorlagen.Add(v);
        }

        public static void DestroyPrefix(UnityEngine.Object __0)
        {
            if (_vorlagen.Count == 0 || __0 == null) return;
            try
            {
                GameObject go = __0 as GameObject;
                if (go == null)
                {
                    Component c = __0 as Component;
                    if (c == null) return;
                    go = c.gameObject;
                }
                for (int i = 0; i < _vorlagen.Count; i++)
                {
                    Vorlage v = _vorlagen[i];
                    if (!Betrifft(go, v)) continue;
                    RevivalPlugin.L.LogWarning("VORLAGE " + v.Id + " wird zerstoert ("
                        + __0.GetType().Name + " \"" + __0.name + "\"). Ohne sie ist das "
                        + "Item bis zum Neustart weg. Aufrufer:");
                    RevivalPlugin.L.LogWarning(Environment.StackTrace);
                    return;
                }
            }
            catch { }
        }

        /// <summary>Is `go` the template itself, its holder, or a child of it?</summary>
        static bool Betrifft(GameObject go, Vorlage v)
        {
            Transform t = go.transform;
            while (t != null)
            {
                if (ReferenceEquals(t.gameObject, v.Go)
                    || ReferenceEquals(t.gameObject, v.Holder)) return true;
                t = t.parent;
            }
            return false;
        }
    }

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

        static readonly Dictionary<int, int> _repairs = new Dictionary<int, int>();

        /// <summary>
        /// Puts an entry back that has died under us.
        ///
        /// Called right before the game looks an id up. If the template behind
        /// that id is gone, it is rebuilt and written into the dictionaries
        /// again - the game then gets a living object instead of the fake null
        /// that made it say "ItemSpawned is null!" (E-029).
        ///
        /// Three attempts per id, then it gives up: a template that cannot be
        /// built will not become buildable by trying harder, and a repair loop
        /// inside a lookup would be worse than the missing item.
        /// </summary>
        public static void RepairIfDead(int id)
        {
            ItemDef d = null;
            List<ItemDef> items = RevivalPlugin.Items;
            for (int i = 0; i < items.Count; i++)
                if (items[i].Id == id) { d = items[i]; break; }
            if (d == null || d.Factory.SpawnAlive) return;

            int mal = 0;
            if (_repairs.TryGetValue(id, out mal) && mal >= 3) return;
            _repairs[id] = mal + 1;

            RevivalPlugin.L.LogWarning("Item " + id + ": Inventarvorlage ist weg - "
                + "wird neu gebaut (Versuch " + (mal + 1) + " von 3).");
            d.Factory.ForgetSpawn();
            if (d.Factory.GetSpawnPrefab(null) == null)
            {
                RevivalPlugin.L.LogError("Item " + id + ": Vorlage laesst sich nicht "
                    + "neu bauen.");
                return;
            }

            object db = GetDb();
            if (db == null) return;
            Component mine = d.Factory.MySpawned;
            int n = 0;
            FieldInfo[] fields = db.GetType().GetFields(BindingFlags.Instance
                                     | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo f in fields)
            {
                if (f.Name == "SpawnCategoriesDictionary") continue;
                IDictionary dic = GetDic(db, f);
                if (dic == null) continue;
                object value = dic.Contains(d.DonorId) ? dic[d.DonorId] : null;
                if (mine != null && (value == null || value.GetType().IsInstanceOfType(mine)))
                    value = mine;
                if (value == null) continue;
                try { dic[d.Id] = value; n++; }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("  " + f.Name + ": " + ex.Message);
                }
            }
            RevivalPlugin.L.LogInfo("Item " + id + ": Vorlage steht wieder, "
                + n + " Woerterbuecher nachgetragen.");
        }

        /// <summary>
        /// Prefix on ItemSpawnCategoriesDB::GetItemSpawnedScriptByID - the one
        /// place every path goes through: giving, picking up, spawning loot.
        /// </summary>
        public static void LookupPrefix(int __0)
        {
            try { RepairIfDead(__0); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Vorlagenpruefung: " + ex.Message);
            }
        }

        static object GetDb()
        {
            Type t = RevivalPlugin.TypeByName("ItemSpawnCategoriesDB");
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
            Type t = RevivalPlugin.TypeByName("xmlItemsDataManager");
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
    /// <summary>
    /// Keeps a missing back model from taking the whole character screen with
    /// it.
    ///
    /// `PlayerMenuCustomizationManager::WeaponSpineInstanceManager` hangs the
    /// weapon on the character's back, and its IL reads:
    ///
    ///     Instantiate(Resources.Load("PlayerDataPrefabs/Weapons/" + id + "_Weapon"))
    ///
    /// The game DOES check that result against null - two instructions too
    /// late, after the Instantiate ("NetworkShowWeaponSpine: not loaded!").
    /// For an id without such a prefab - every item of ours that is not a
    /// weapon, the drone 1163 in a weapon slot for instance -
    /// Instantiate(null) throws ArgumentException,
    /// CharacterOptionsUI.ShowCharacterData never returns, and the loading
    /// screen stands at "Spielcharaktere laden" until the process is killed.
    /// That cost several game starts on 2026-08-29; the stack trace is in
    /// E-028.
    ///
    /// The finalizer swallows exactly that. By the time it runs, the method
    /// has already cleared the old model off the back; what is missing
    /// afterwards is the new one - which is right, the item is not a weapon.
    /// </summary>
    public static class SpineGuard
    {
        static bool _gemeldet;

        public static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!_gemeldet)
            {
                _gemeldet = true;
                RevivalPlugin.L.LogWarning("Rueckenmodell: "
                    + __exception.GetType().Name + " geschluckt - ein Item im "
                    + "Waffenslot hat kein _Weapon-Prefab. Ohne das haengt der "
                    + "Charakterbildschirm beim Laden des Profils (E-028).");
            }
            return null;
        }
    }

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

                Type dm = RevivalPlugin.TypeByName("ItemSpawnCategoriesDB");
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
    // ------------------------------------------------------------- Netzwacht

    /// <summary>
    /// The one measurement E-043 and E-044 were missing.
    ///
    /// Both read `DisconnectByClientTimeout` as "the main thread was busy",
    /// and both ruled out the other side by checking the LOCAL master server.
    /// That check proves nothing about this message. The local master only
    /// serves login, profile, static data and the server list. The room the
    /// timeout belongs to is a PHOTON CLOUD room - own app id, region 0, see
    /// MASTERSERVER.md section 2 - so the peer that timed out is a UDP link
    /// over the internet, not a socket to 127.0.0.1. A healthy master log is
    /// consistent with every one of the causes below.
    ///
    /// Three of them produce the same line and need different fixes:
    ///
    ///   the main thread stalled   Update stops for longer than the peer
    ///                             tolerates. Shows up here as a frame gap.
    ///   the link degraded         packets stop arriving. Shows up as a
    ///                             rising round trip time and resent reliable
    ///                             commands while the frame gaps stay small.
    ///   the room is flooded       more traffic than the plan carries. Shows
    ///                             up as a growing outgoing queue.
    ///
    /// The symptoms reported on 2026-08-30 - after F9, in the loading screen,
    /// and at no particular moment - cannot all be the tank construction. The
    /// 2026-09-01 two-client failure finally measured the degraded-link case:
    /// 5052 reliable resends with a small outgoing queue and no long frame
    /// before Photon's 15 second default disconnected both players. The guard
    /// below raises every current or replacement peer to the configured 60
    /// seconds. The optional reports remain read-only apart from enabling
    /// Photon's own traffic counters.
    /// </summary>
    public static class NetWatch
    {
        static bool _first = true;
        static float _lastFrame;
        static float _nextReport;
        static float _worstSinceReport;
        static float _worstEver;
        static string _lastState = "";

        static object _peer;
        static float _nextPeerLook;
        static Type _network;
        static PropertyInfo _stateDetailed;
        static bool _looked;

        static double _lastIn, _lastOut, _lastMs;
        static bool _statsOn;
        static float _nextTimeoutCheck;

        public static void Tick()
        {
            GuardTimeout();
            if (RevivalPlugin.CfgNetWatch == null || !RevivalPlugin.CfgNetWatch.Value) return;

            float now = Time.realtimeSinceStartup;
            if (_first)
            {
                _first = false;
                _lastFrame = now;
                _nextReport = now;
                RevivalPlugin.L.LogInfo("Netzwacht " + Uhr()
                    + " active - frame gaps above "
                    + RevivalPlugin.CfgNetWatchHitch.Value.ToString("0.00")
                    + " s and a Photon report every "
                    + RevivalPlugin.CfgNetWatchEvery.Value.ToString("0") + " s.");
                return;
            }

            float gap = now - _lastFrame;
            _lastFrame = now;
            if (gap > _worstSinceReport) _worstSinceReport = gap;

            // The gap is measured on the frame AFTER the pause, so a
            // synchronous scene load or a first-time asset load lands here
            // whole. Time.realtimeSinceStartup keeps running while Unity does
            // not, which is exactly why it and not Time.deltaTime is used.
            float grenze = Mathf.Max(0.05f, RevivalPlugin.CfgNetWatchHitch.Value);
            if (gap > grenze)
            {
                bool schlimmster = gap > _worstEver;
                if (schlimmster) _worstEver = gap;
                RevivalPlugin.L.LogWarning("Netzwacht " + Uhr() + " frame gap "
                    + gap.ToString("0.00") + " s"
                    + (schlimmster ? " (worst so far)" : "")
                    + " - " + PeerText() + ".");
                // A gap this long has already moved the report clock past due.
                // Report right after it as well, so the counters either show
                // the damage or show that the link was never the problem.
                _nextReport = now;
            }

            if (now < _nextReport) return;
            _nextReport = now + Mathf.Max(1f, RevivalPlugin.CfgNetWatchEvery.Value);

            string state = StateText();
            bool gewechselt = state != _lastState;
            _lastState = state;
            RevivalPlugin.L.LogInfo("Netzwacht " + Uhr() + " state=" + state
                + (gewechselt ? " (changed)" : "")
                + " " + PeerText() + " " + VerkehrText()
                + " worstframe=" + _worstSinceReport.ToString("0.00") + " s.");
            _worstSinceReport = 0f;
        }

        /// <summary>
        /// Photon creates or resets its networking peer on every reconnect.
        /// The old tank guard changed only the peer that happened to exist when
        /// F9 was pressed, so ordinary play and every later peer kept the game's
        /// 15 second default. Check once a second and raise any current peer
        /// whose value is lower than the configured tolerance. A higher value
        /// is preserved.
        /// </summary>
        static void GuardTimeout()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextTimeoutCheck) return;
            _nextTimeoutCheck = now + 1f;

            object peer = Peer();
            if (peer == null || RevivalPlugin.CfgPhotonTimeout == null) return;
            int wanted = Mathf.Clamp(RevivalPlugin.CfgPhotonTimeout.Value,
                                     15000, 120000);
            try
            {
                Type type = peer.GetType();
                PropertyInfo property = FindProperty(type, "DisconnectTimeout");
                FieldInfo field = property == null
                    ? FindField(type, "DisconnectTimeout") : null;
                object old = property != null ? property.GetValue(peer, null)
                    : field != null ? field.GetValue(peer) : null;
                if (old == null) return;
                int previous = Convert.ToInt32(old);
                if (previous >= wanted) return;

                if (property != null && property.CanWrite)
                    property.SetValue(peer, wanted, null);
                else if (field != null)
                    field.SetValue(peer, wanted);
                else
                    return;

                RevivalPlugin.L.LogInfo("Photon guard: timeout " + previous
                    + " -> " + wanted + " ms on the current room peer.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Photon guard: timeout could not be set - "
                    + ex.Message);
            }
        }

        static PropertyInfo FindProperty(Type type, string name)
        {
            while (type != null)
            {
                PropertyInfo property = type.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null) return property;
                type = type.BaseType;
            }
            return null;
        }

        static FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        static string Uhr()
        {
            return DateTime.Now.ToString("HH:mm:ss.fff");
        }

        /// <summary>The peer's own counters. `RoundTripTime` and
        /// `ResentReliableCommands` separate a dead link from a busy client;
        /// `QueuedOutgoingCommands` separates both from us sending more than
        /// the room carries. `DisconnectTimeout` is here because the F9 guard
        /// raises it and nothing has ever confirmed that it held.</summary>
        static string PeerText()
        {
            object peer = Peer();
            if (peer == null) return "no peer";
            return "rtt=" + Zahl(peer, "RoundTripTime")
                + "/" + Zahl(peer, "RoundTripTimeVariance") + " ms"
                + " out=" + Zahl(peer, "QueuedOutgoingCommands")
                + " in=" + Zahl(peer, "QueuedIncomingCommands")
                + " resent=" + Zahl(peer, "ResentReliableCommands")
                + " losscrc=" + Zahl(peer, "PacketLossByCrc")
                + " timeout=" + Zahl(peer, "DisconnectTimeout");
        }

        static string StateText()
        {
            Look();
            if (_stateDetailed == null) return "?";
            try
            {
                object v = _stateDetailed.GetValue(null, null);
                return v == null ? "?" : v.ToString();
            }
            catch { return "?"; }
        }

        /// <summary>The peer is replaced on every reconnect, so it is looked
        /// up again every second instead of cached for the session.</summary>
        static object Peer()
        {
            float now = Time.realtimeSinceStartup;
            if (_peer != null && now < _nextPeerLook) return _peer;
            _nextPeerLook = now + 1f;
            _peer = null;
            try
            {
                Look();
                if (_network == null) return null;
                FieldInfo f = AccessTools.Field(_network, "networkingPeer");
                if (f != null) _peer = f.GetValue(null);
                if (_peer == null)
                {
                    PropertyInfo p = AccessTools.Property(_network, "networkingPeer");
                    if (p != null) _peer = p.GetValue(null, null);
                }
            }
            catch { _peer = null; }
            return _peer;
        }

        static void Look()
        {
            if (_looked) return;
            _looked = true;
            _network = RevivalPlugin.TypeByName("PhotonNetwork");
            if (_network == null)
            {
                RevivalPlugin.L.LogWarning("Netzwacht: PhotonNetwork not found - "
                    + "only the frame gaps are measured.");
                return;
            }
            _stateDetailed = AccessTools.Property(_network, "connectionStateDetailed");
        }

        /// <summary>
        /// Bytes per second in each direction, from the peer's own traffic
        /// counters. This is the only reading that separates "we send more
        /// than the room carries" from the other two causes: a queue length is
        /// drained every frame and is therefore almost always zero, even while
        /// the client is well over the plan's message rate.
        ///
        /// Switching the counters on is the one write this class makes. It is
        /// a diagnostic flag on the peer, it changes no traffic, and Photon
        /// ships it for exactly this purpose.
        /// </summary>
        static string VerkehrText()
        {
            object peer = Peer();
            if (peer == null) return "";
            try
            {
                if (!_statsOn)
                {
                    PropertyInfo on = AccessTools.Property(peer.GetType(),
                                                           "TrafficStatsEnabled");
                    if (on == null || !on.CanWrite) return "";
                    on.SetValue(peer, true, null);
                    _statsOn = true;
                    _lastMs = 0d;
                    return "traffic=on";
                }

                double ms = Wert(peer, "TrafficStatsElapsedMs");
                double ein = Bytes(peer, "TrafficStatsIncoming");
                double aus = Bytes(peer, "TrafficStatsOutgoing");
                double spanne = ms - _lastMs;
                string text = "traffic=?";
                if (_lastMs > 0d && spanne > 0d)
                    text = "in=" + ((ein - _lastIn) * 1000d / spanne).ToString("0")
                         + " out=" + ((aus - _lastOut) * 1000d / spanne).ToString("0")
                         + " B/s";
                _lastMs = ms;
                _lastIn = ein;
                _lastOut = aus;
                return text;
            }
            catch { return ""; }
        }

        static double Bytes(object peer, string name)
        {
            PropertyInfo p = AccessTools.Property(peer.GetType(), name);
            object stats = p == null ? null : p.GetValue(peer, null);
            if (stats == null)
            {
                FieldInfo f = AccessTools.Field(peer.GetType(), name);
                if (f != null) stats = f.GetValue(peer);
            }
            return stats == null ? 0d : Wert(stats, "TotalPacketBytes");
        }

        static double Wert(object o, string name)
        {
            if (o == null) return 0d;
            try
            {
                object v = null;
                PropertyInfo p = AccessTools.Property(o.GetType(), name);
                if (p != null) v = p.GetValue(o, null);
                else
                {
                    FieldInfo f = AccessTools.Field(o.GetType(), name);
                    if (f != null) v = f.GetValue(o);
                }
                return v == null ? 0d : Convert.ToDouble(v);
            }
            catch { return 0d; }
        }

        static string Zahl(object o, string name)
        {
            if (o == null) return "?";
            try
            {
                object v = null;
                PropertyInfo p = AccessTools.Property(o.GetType(), name);
                if (p != null) v = p.GetValue(o, null);
                else
                {
                    FieldInfo f = AccessTools.Field(o.GetType(), name);
                    if (f != null) v = f.GetValue(o);
                }
                if (v == null) return "?";
                return Convert.ToDouble(v).ToString("0");
            }
            catch { return "?"; }
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
                    IEnumerable scenes = Val(rt, r, "scenes") as IEnumerable;
                    RevivalPlugin.L.LogInfo("  region=" + Regions.RegionName(region)
                                            + " startScene=" + Regions.SceneName(start)
                                            + " scenes=" + Regions.SceneNames(scenes));
                }
                RevivalPlugin.L.LogInfo("--- Regionen Ende ---");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Regionen: " + ex.Message); }
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
            int region = RevivalPlugin.CfgJumpRegion.Value;
            if (region >= 0) JumpToRegion(region);
            else Jump(RevivalPlugin.CfgJumpScene.Value);
        }

        /// <summary>
        /// Assembles a LocationChangeTrigger and hands it to
        /// ChangeGameLocation. Type 2 means sub-location, and then SubLocation
        /// is the GameScene VALUE of the target scene - not a build index.
        ///
        /// Evidence from GenerateServerOptions: for type 2 it writes
        /// trigger.SubLocation and for type 1
        /// GetGameRegionData(trigger.Region).startScene into the same field
        /// _gameScene. Both numbers therefore have the same type, and that
        /// type is the GameScene enum. Cross-check against the scene files:
        /// the door trigger in Bunker_A65 carries SubLocation 6, which is
        /// GW_Scene_2, and Bunker_A65 itself reports CurrentScene 9.
        /// </summary>
        public static void Jump(int gameScene)
        {
            try
            {
                Type tTrig = RevivalPlugin.TypeByName("LocationChangeTrigger");
                Type tMgr = RevivalPlugin.TypeByName("GameLocationChangeManager");
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
                SetField(tTrig, trig, "Id", 9000 + gameScene);
                SetField(tTrig, trig, "LocationaChangeType", 2);
                SetField(tTrig, trig, "SubLocation", gameScene);
                SetField(tTrig, trig, "ShowOnMapUI", false);

                MethodInfo m = AccessTools.Method(tMgr, "ChangeGameLocation", null, null);
                if (m == null)
                {
                    UnityEngine.Object.Destroy(go);
                    RevivalPlugin.L.LogWarning("Szenenwechsel: ChangeGameLocation nicht gefunden.");
                    return;
                }
                RevivalPlugin.L.LogInfo("Szenenwechsel nach "
                                        + Regions.SceneName(gameScene) + " ...");
                m.Invoke(mgrObj, new object[] { trig });
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Szenenwechsel: " + ex); }
        }

        /// <summary>
        /// Changes into the start scene of a whole region. That is change
        /// type 1: GenerateServerOptions then fetches
        /// GetGameRegionData(Region).startScene by itself. It is exactly how
        /// the three bunker doors in the game send the player back up to the
        /// surface - type 1, Region 0, SubLocation 0.
        /// </summary>
        public static void JumpToRegion(int region)
        {
            try
            {
                Type tTrig = RevivalPlugin.TypeByName("LocationChangeTrigger");
                Type tMgr = RevivalPlugin.TypeByName("GameLocationChangeManager");
                if (tTrig == null || tMgr == null)
                {
                    RevivalPlugin.L.LogWarning("Region change: types not found.");
                    return;
                }
                UnityEngine.Object mgrObj = UnityEngine.Object.FindObjectOfType(tMgr);
                if (mgrObj == null)
                {
                    RevivalPlugin.L.LogWarning("Region change: no "
                        + "GameLocationChangeManager in the scene.");
                    return;
                }

                GameObject go = new GameObject("NextDayRevival_RegionJump");
                Component trig = go.AddComponent(tTrig);
                if (trig == null)
                {
                    UnityEngine.Object.Destroy(go);
                    RevivalPlugin.L.LogWarning("Region change: cannot add the trigger.");
                    return;
                }
                SetField(tTrig, trig, "Id", 9500 + region);
                SetField(tTrig, trig, "LocationaChangeType", 1);
                SetField(tTrig, trig, "Region", region);
                SetField(tTrig, trig, "SubLocation", 0);
                SetField(tTrig, trig, "ShowOnMapUI", false);

                MethodInfo m = AccessTools.Method(tMgr, "ChangeGameLocation", null, null);
                if (m == null)
                {
                    UnityEngine.Object.Destroy(go);
                    RevivalPlugin.L.LogWarning("Region change: ChangeGameLocation "
                        + "not found.");
                    return;
                }
                RevivalPlugin.L.LogInfo("Region change to "
                                        + Regions.RegionName(region) + " ...");
                m.Invoke(mgrObj, new object[] { trig });
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Region change: " + ex); }
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

        /// <summary>
        /// Gets hold of the GameRegionsData.
        ///
        /// FindObjectOfType CANNOT find this manager, and that is not a
        /// timing problem - it never could. Measured 2026-08-29 from the IL:
        ///
        ///     GameRegionsManager::.cctor
        ///         newobj GameRegionsManager::.ctor
        ///         stsfld Instance
        ///
        /// The class derives from MonoBehaviour but its instance is built by
        /// the **static constructor** with `new`, and it is never attached to
        /// a GameObject. There is no such component in any scene, so
        /// FindObjectOfType returns null for the whole session. That is why
        /// ReportRegions had been printing "kein GameRegionsData" since it
        /// was written, and why the second region was never registered on the
        /// first try in the game.
        ///
        /// The static field `Instance` is the way in - it is what the game
        /// itself uses (`ldsfld Instance` in UseTransition). Reading it also
        /// runs the static constructor, so the manager exists from the first
        /// attempt on.
        ///
        /// `mgr` is deliberately typed `object`: an instance created with
        /// `new` has no native peer, and UnityEngine.Object's overloaded
        /// `==` would report it as null. With the static type `object` the
        /// comparison is an ordinary reference check.
        /// </summary>
        internal static object GetRegionsData()
        {
            Type t = RevivalPlugin.TypeByName("GameRegionsManager");
            if (t == null) return null;

            object mgr = null;
            FieldInfo inst = AccessTools.Field(t, "Instance");
            if (inst != null && inst.IsStatic)
            {
                try { mgr = inst.GetValue(null); }
                catch { }
            }
            if (mgr == null) mgr = UnityEngine.Object.FindObjectOfType(t);
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

    // -------------------------------------------------------------- Regions

    /// <summary>
    /// Adds a second region to the game's region list.
    ///
    /// WHY THIS WORKS AT ALL
    /// ---------------------
    /// Until 2026-08-29 REVERSE_ENGINEERING.md said that a region's
    /// `startScene` and `scenes` were build indices - so a new region would
    /// need a new scene in the build, and that would need a rebuild of the
    /// game. That was a misreading. They are values of the `GameScene` enum.
    ///
    /// EVIDENCE - `GameLocationChangeManager::GenerateServerOptions` writes
    /// into the one field `_gameScene` either
    ///     GetGameRegionData(trigger.Region).startScene   (change type 1)
    ///     trigger.SubLocation                            (change type 2)
    /// so both numbers have the same type. Cross-check against the scene
    /// files, measured 2026-08-29:
    ///     level3  Bunker_A65       SceneGamePlayDataObjects.CurrentScene  9
    ///     level4  GW_Scene_2                                              6
    ///     level6  Catacombs                                              13
    ///     level18 Underground_Lab                                        14
    /// Those are the enum values, not the build indices 3, 4, 6 and 18.
    ///
    /// WHAT THIS CLASS DOES
    /// --------------------
    /// `GameRegionsManager.gameRegionsList` is a plain array of
    /// `GameRegionData` with the three fields `region`, `startScene`,
    /// `scenes`. This class appends one element to that array - the same kind
    /// of work `Registry` does for the item databases.
    /// `GameModeOptionsUI::UpdateGameRegionsPopUp` walks the same array to
    /// fill the region drop-down of the room settings, so the new region
    /// shows up there by itself.
    ///
    /// WHY THE SCENES HAVE TO LEAVE REGION 0
    /// -------------------------------------
    /// `GetRegionDataAtScene` walks the list from index 0 and returns the
    /// FIRST region whose `scenes` contains the value. Region 0 comes first.
    /// If Bunker_A65 and Underground_Lab stayed in its list, region 0 would
    /// win every lookup and the new region would be a label and nothing else.
    /// That is what `TakeFromRegion0` is for. Region 0 keeps GW_Scene_1,
    /// GW_Scene_2, GW_Scene_3 and Catacombs - the whole surface world.
    ///
    /// NOT A SERVER MATTER. `Stage64Server.cs` knows neither game scenes nor
    /// game regions; both travel between clients in the Photon room options.
    /// Both players do need the same plugin, or one of them has a region the
    /// other cannot resolve.
    ///
    /// NOT YET ACCEPTED IN THE GAME - state 2026-08-29. The plan and the open
    /// points are in docs/ai/tasks/new-regions.md.
    /// </summary>
    public static class Regions
    {
        static float _next;
        static bool _loggedFail;

        /// <summary>Name of a GameScene value, from the game's own enum.</summary>
        public static string SceneName(object value)
        {
            return EnumName("GameScene", value);
        }

        /// <summary>Name of a GameRegion value, from the game's own enum.</summary>
        public static string RegionName(object value)
        {
            return EnumName("GameRegion", value);
        }

        /// <summary>A scene list as "[9 Bunker_A65, 14 Underground_Lab]".</summary>
        public static string SceneNames(IEnumerable scenes)
        {
            if (scenes == null) return "[]";
            System.Text.StringBuilder sb = new System.Text.StringBuilder("[");
            bool first = true;
            foreach (object o in scenes)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(SceneName(o));
            }
            return sb.Append("]").ToString();
        }

        static string EnumName(string typeName, object value)
        {
            if (value == null) return "null";
            int n;
            try { n = Convert.ToInt32(value); }
            catch { return value.ToString(); }
            try
            {
                Type t = RevivalPlugin.TypeByName(typeName);
                if (t != null && t.IsEnum)
                {
                    string name = Enum.GetName(t, Enum.ToObject(t, n));
                    if (!string.IsNullOrEmpty(name)) return n + " " + name;
                }
            }
            catch { }
            return n + " ?";
        }

        /// <summary>
        /// Called every frame, does work at most every two seconds. The
        /// GameRegionsManager only exists once the menu is up, and its data
        /// can be reloaded on a scene change - so this does not register once
        /// and forget, it checks whether the region is still in the list.
        ///
        /// While the list is out of reach the interval grows to ten seconds.
        /// `GetRegionsData` calls `SetupGameRegionsData`, and that reads a
        /// ScriptableObject out of Resources - not something to do every two
        /// seconds for a whole session if the data never turns up.
        /// </summary>
        public static void Tick()
        {
            if (RevivalPlugin.CfgNewRegion == null || !RevivalPlugin.CfgNewRegion.Value) return;
            if (Time.realtimeSinceStartup < _next) return;
            bool reached = false;
            try { reached = Apply(false); }
            catch (Exception ex)
            {
                if (!_loggedFail)
                {
                    _loggedFail = true;
                    RevivalPlugin.L.LogWarning("Region: " + ex.Message);
                }
            }
            _next = Time.realtimeSinceStartup + (reached ? 2f : 10f);
        }

        /// <summary>
        /// Registers the region if it is missing. `loud` also logs when there
        /// was nothing to do. Returns whether the region list could be
        /// reached at all - not whether anything was changed.
        /// </summary>
        public static bool Apply(bool loud)
        {
            object data = Research.GetRegionsData();
            if (data == null)
            {
                if (loud) RevivalPlugin.L.LogInfo("Region: no GameRegionsData yet.");
                return false;
            }
            FieldInfo fList = AccessTools.Field(data.GetType(), "RegionsList");
            Array arr = fList == null ? null : fList.GetValue(data) as Array;
            if (arr == null)
            {
                if (loud) RevivalPlugin.L.LogWarning("Region: no RegionsList.");
                return false;
            }

            int id = RevivalPlugin.CfgNewRegionId.Value;
            for (int i = 0; i < arr.Length; i++)
            {
                object row = arr.GetValue(i);
                if (row == null) continue;
                if (IntField(row, "region") == id)
                {
                    if (loud)
                        RevivalPlugin.L.LogInfo("Region " + RegionName(id)
                            + " is already in the list.");
                    return true;
                }
            }

            int start = RevivalPlugin.CfgNewRegionStart.Value;
            List<int> scenes = Parse(RevivalPlugin.CfgNewRegionScenes.Value);
            if (!scenes.Contains(start))
            {
                // Otherwise GetRegionDataAtScene would not find our own start
                // scene in our own region, and the room switch would end up in
                // the wrong region or in none at all.
                scenes.Insert(0, start);
                RevivalPlugin.L.LogInfo("Region: startScene " + SceneName(start)
                    + " was missing from the scene list and has been prepended.");
            }

            Type tRow = arr.GetType().GetElementType();
            object fresh = NewRow(tRow, id, start, scenes);
            if (fresh == null) return true;   // reached, but unusable - do not hammer it

            if (RevivalPlugin.CfgNewRegionExclusive.Value) TakeFromOthers(arr, scenes);

            Array longer = Array.CreateInstance(tRow, arr.Length + 1);
            Array.Copy(arr, longer, arr.Length);
            longer.SetValue(fresh, arr.Length);
            fList.SetValue(data, longer);

            RevivalPlugin.L.LogInfo("Region " + RegionName(id) + " registered: startScene "
                + SceneName(start) + ", scenes " + SceneNames(scenes)
                + (RevivalPlugin.CfgNewRegionExclusive.Value
                   ? ", taken out of the other regions." : ", other regions unchanged."));
            Research.ReportRegions();
            return true;
        }

        /// <summary>Fills a fresh GameRegionData with the three fields.</summary>
        static object NewRow(Type tRow, int region, int startScene, List<int> scenes)
        {
            if (tRow == null)
            {
                RevivalPlugin.L.LogWarning("Region: no GameRegionData type.");
                return null;
            }
            object row;
            try { row = Activator.CreateInstance(tRow); }
            catch
            {
                // No public default constructor: allocate uninitialised. All
                // three fields are written right below anyway.
                row = System.Runtime.Serialization.FormatterServices
                          .GetUninitializedObject(tRow);
            }
            if (row == null) return null;

            FieldInfo fRegion = AccessTools.Field(tRow, "region");
            FieldInfo fStart = AccessTools.Field(tRow, "startScene");
            FieldInfo fScenes = AccessTools.Field(tRow, "scenes");
            if (fRegion == null || fStart == null || fScenes == null)
            {
                RevivalPlugin.L.LogWarning("Region: GameRegionData does not have the "
                    + "three expected fields.");
                return null;
            }

            fRegion.SetValue(row, Enum.ToObject(fRegion.FieldType, region));
            fStart.SetValue(row, Enum.ToObject(fStart.FieldType, startScene));

            IList list = Activator.CreateInstance(fScenes.FieldType) as IList;
            if (list == null)
            {
                RevivalPlugin.L.LogWarning("Region: scenes is not a list.");
                return null;
            }
            Type element = ElementType(fScenes.FieldType);
            for (int i = 0; i < scenes.Count; i++)
                list.Add(element != null && element.IsEnum
                         ? Enum.ToObject(element, scenes[i]) : (object)scenes[i]);
            fScenes.SetValue(row, list);
            return row;
        }

        static Type ElementType(Type listType)
        {
            try
            {
                Type[] args = listType.GetGenericArguments();
                if (args != null && args.Length == 1) return args[0];
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Takes the given scenes away from every region that already exists.
        ///
        /// Not just region 0, even though only region 0 is in question today:
        /// the new row is APPENDED, and GetRegionDataAtScene returns the first
        /// region that contains the scene. Any row in front of ours would win,
        /// whatever number it carries.
        /// </summary>
        static void TakeFromOthers(Array arr, List<int> scenes)
        {
            for (int r = 0; r < arr.Length; r++)
            {
                object row = arr.GetValue(r);
                if (row == null) continue;
                FieldInfo f = AccessTools.Field(row.GetType(), "scenes");
                IList list = f == null ? null : f.GetValue(row) as IList;
                if (list == null) continue;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    int value;
                    try { value = Convert.ToInt32(list[i]); }
                    catch { continue; }
                    if (!scenes.Contains(value)) continue;
                    list.RemoveAt(i);
                    RevivalPlugin.L.LogInfo("  " + SceneName(value) + " removed from region "
                        + RegionName(IntField(row, "region")) + ".");
                }
            }
        }

        static int IntField(object o, string name)
        {
            FieldInfo f = AccessTools.Field(o.GetType(), name);
            if (f == null) return int.MinValue;
            try { return Convert.ToInt32(f.GetValue(o)); }
            catch { return int.MinValue; }
        }

        static List<int> Parse(string csv)
        {
            List<int> values = new List<int>();
            if (string.IsNullOrEmpty(csv)) return values;
            string[] parts = csv.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i].Trim();
                if (t.Length == 0) continue;
                try { values.Add(Convert.ToInt32(t)); }
                catch { RevivalPlugin.L.LogWarning("Region: not a number: " + t); }
            }
            return values;
        }

        /// <summary>
        /// Label for the drop-down - only when the config asks for a name of
        /// its own.
        ///
        /// CORRECTION TO new-regions.md section 3: the key is NOT
        /// "$GameRegion_2". `UpdateGameRegionsPopUp` puts the `region` field
        /// into `String.Concat` with `box GameRegion`, so the call ends up in
        /// `Enum.ToString()` and the key carries the NAME. For region 2 that
        /// is "$GameRegion_Uralsk" - and that entry is already in the game's
        /// Localization_DB, translated into five languages. The new region
        /// names itself; the plugin has nothing to do. Only a value with no
        /// name in the enum falls back to the number, which is why both forms
        /// are answered here.
        /// </summary>
        public static bool Label(string key, ref string result)
        {
            if (RevivalPlugin.CfgNewRegion == null || !RevivalPlugin.CfgNewRegion.Value)
                return false;
            string own = RevivalPlugin.CfgNewRegionName.Value;
            if (string.IsNullOrEmpty(own)) return false;

            int id = RevivalPlugin.CfgNewRegionId.Value;
            if (key == "$GameRegion_" + id) { result = own; return true; }

            Type t = RevivalPlugin.TypeByName("GameRegion");
            if (t != null && t.IsEnum)
            {
                string name = Enum.GetName(t, Enum.ToObject(t, id));
                if (!string.IsNullOrEmpty(name) && key == "$GameRegion_" + name)
                {
                    result = own;
                    return true;
                }
            }
            return false;
        }
    }

    // -------------------------------------------------- Kameraschiedsrichter

    /// <summary>
    /// Haelt fest, WER gerade durch die Kamera des Spiels sieht.
    ///
    /// Bis 0.4.5 gab es genau einen Bewerber - das Geschuetz - und deshalb
    /// stand die Uebernahme mitten in `Turret`: `TakeCamera` legte die
    /// Kameraskripte still, und `CameraHook.Postfix` rief fest
    /// `Turret.LateTick()` auf. Mit der Drohne gibt es zwei Bewerber um
    /// dieselbe Transform, und zwei Skripte, die im selben Frame dieselbe
    /// Transform schreiben, ergeben genau das zuckende Bild, gegen das
    /// `TakeCamera` ueberhaupt gebaut wurde.
    ///
    /// Deshalb haelt jetzt diese Klasse den Blick: wer sehen will, meldet sich
    /// mit `Request` an und bekommt den Zuschlag oder eine Absage. Nur der
    /// eingetragene Halter wird von `LateTick` aufgerufen. Der Zweite bekommt
    /// nichts - bewusst kein Verdraengen, denn dann muesste geklaert werden,
    /// was mit dem Ersten passiert, und ein Geschuetz ohne Bild ist schlimmer
    /// als eine Drohne, die nicht startet.
    ///
    /// Uebernommen aus `Turret` und dort geloescht. Verhalten unveraendert:
    /// dieselbe Liste `CamDrivers`, dieselbe Suche vom Kameraobjekt nach OBEN
    /// durch alle Elternteile, dieselbe Wiederherstellung des Bildwinkels.
    /// </summary>
    public static class CameraOwner
    {
        public const int None = 0;
        public const int Turm = 1;
        public const int Drohne = 2;

        /// <summary>
        /// Skripte, die die Kamera bewegen und deshalb waehrend einer
        /// Uebernahme stillstehen muessen.
        ///
        /// Der wichtige davon ist `MouseOrbitController`: eine Umlaufkamera mit
        /// `Target`, die im LateUpdate jeden Frame neu um ihr Ziel herum
        /// gerechnet wird. Genau das war der Grund, warum drei Anlaeufe mit
        /// Postfix und LateUpdate nichts genutzt haben - egal wohin das Plugin
        /// die Kamera setzte, sie stand einen Wimpernschlag spaeter wieder
        /// hinter dem BTR und blickte darauf. Gegen ein Skript, das jeden Frame
        /// schreibt, hilft kein zweites Skript, das auch jeden Frame schreibt,
        /// sondern nur Abschalten.
        ///
        /// Bildeffekte (`CrosshairCameraEffect`, `ScopeCameraEffect`, ...)
        /// stehen absichtlich NICHT auf der Liste: sie bewegen nichts, und ohne
        /// sie saehe das Bild anders aus als im Rest des Spiels.
        /// </summary>
        static readonly string[] CamDrivers = {
            "MouseOrbitController", "CameraController", "CameraControllerFPS",
            "CameraFPSController", "CameraTPSController", "CameraAimingSystem",
            "CameraFollow", "CameraWork", "CameraAnimationController",
            "CameraSpectratorController", "CameraSwitch", "CameraPathAnimator",
            "CameraRotateAroundTrailer", "CameraRotateWhenLoaded",
        };

        static int _owner;
        static string _label = "";
        static Camera _cam;                  // die uebernommene Kamera
        static float _fovBack = -1f;         // Bildwinkel vor der Uebernahme
        static readonly List<Behaviour> _paused = new List<Behaviour>();

        public static int Owner { get { return _owner; } }
        public static bool Has(int who) { return _owner == who; }
        public static bool Free { get { return _owner == None; } }

        /// <summary>
        /// Die Kamera, die wirklich rendert. Camera.main ist der Normalfall;
        /// findet sich keine mit dem Tag MainCamera, wird die aktivste
        /// genommen.
        ///
        /// Waehrend einer Uebernahme immer dieselbe wie beim Anmelden. Sonst
        /// koennten Kameralage, Fadenkreuz und Schuss auf drei verschiedene
        /// Kameras zeigen.
        /// </summary>
        public static Camera ViewCamera()
        {
            if (_cam != null) return _cam;
            Camera cam = Camera.main;
            if (cam != null) return cam;
            Camera[] all = Camera.allCameras;
            Camera best = null;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].enabled
                    && (best == null || all[i].depth > best.depth))
                    best = all[i];
            return best;
        }

        /// <summary>
        /// Meldet einen Halter an. Liefert false, wenn schon jemand sieht oder
        /// keine Kamera zu finden ist - der Aufrufer darf dann NICHT so tun,
        /// als haette er den Blick.
        ///
        /// `pauseDrivers` bildet den alten Schalter `Turret/TakeCamera` ab:
        /// Halter wird man auch ohne Stilllegen, dann bewegt das Spiel die
        /// Kamera aber weiter mit.
        /// </summary>
        public static bool Request(int who, bool pauseDrivers, string label)
        {
            if (who == None) return false;
            if (_owner == who) return true;
            if (_owner != None)
            {
                RevivalPlugin.L.LogInfo(label + ": die Kamera haelt gerade \""
                    + _label + "\" - Anfrage abgelehnt.");
                return false;
            }

            Camera cam = ViewCamera();
            if (cam == null)
            {
                RevivalPlugin.L.LogWarning(label + ": keine Kamera gefunden.");
                return false;
            }

            _owner = who;
            _label = label;
            _cam = cam;
            if (!pauseDrivers)
            {
                RevivalPlugin.L.LogInfo(label + ": Kamera uebernommen, ohne die "
                    + "Kameraskripte stillzulegen - das Bild wird wandern.");
                return true;
            }

            try
            {
                _paused.Clear();
                _fovBack = _cam.fieldOfView;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();

                // Vom Kameraobjekt aus nach OBEN durch alle Elternteile: die
                // Umlaufkamera sitzt am Rig, nicht an der Kamera selbst.
                Transform t = _cam.transform;
                while (t != null)
                {
                    Component[] comps = t.gameObject.GetComponents(typeof(Behaviour));
                    for (int i = 0; i < comps.Length; i++)
                    {
                        Behaviour b = comps[i] as Behaviour;
                        if (b == null || !b.enabled) continue;
                        string n = b.GetType().Name;
                        bool drives = false;
                        for (int k = 0; k < CamDrivers.Length; k++)
                            if (CamDrivers[k] == n) { drives = true; break; }
                        if (!drives) continue;
                        b.enabled = false;
                        _paused.Add(b);
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(n).Append(" an \"").Append(t.name).Append("\"");
                    }
                    t = t.parent;
                }
                RevivalPlugin.L.LogInfo(label + ": Kamera uebernommen: \"" + _cam.name
                    + "\", stillgelegt: " + (sb.Length == 0 ? "nichts" : sb.ToString())
                    + ". Aktive Kameras: " + Kameraliste() + ".");
                if (_paused.Count == 0)
                    RevivalPlugin.L.LogWarning(label + ": kein einziges "
                        + "Kameraskript gefunden - dann bewegt etwas anderes die "
                        + "Kamera, und der Blick wird wieder wandern.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError(label + ": Kamera uebernehmen: " + ex);
            }
            return true;
        }

        /// <summary>
        /// Gibt die Kamera dem Spiel zurueck. Muss immer laufen, auch bei
        /// Ausnahme. Ein fremder Halter wird nicht angetastet - wer nicht
        /// angemeldet ist, kann auch nicht abmelden.
        /// </summary>
        public static void Release(int who)
        {
            if (_owner != who || who == None) return;
            try
            {
                for (int i = 0; i < _paused.Count; i++)
                    if (_paused[i] != null) _paused[i].enabled = true;
                if (_paused.Count > 0)
                    RevivalPlugin.L.LogInfo(_label + ": Kamera zurueckgegeben ("
                        + _paused.Count + " Skripte wieder an).");
                _paused.Clear();
                if (_cam != null && _fovBack > 0f) _cam.fieldOfView = _fovBack;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError(_label + ": Kamera zurueckgeben: " + ex);
            }
            finally
            {
                _fovBack = -1f;
                _cam = null;
                _owner = None;
                _label = "";
            }
        }

        /// <summary>
        /// Setzt die Kamera fuer den aktuellen Halter. Gehoert in LateUpdate:
        /// das Spiel zieht seine eigene Kamera im selben Frame nach, und wer
        /// zuerst schreibt, verliert.
        /// </summary>
        public static void LateTick()
        {
            if (_owner == Turm) Turret.LateTick();
            else if (_owner == Drohne) Drone.LateTick();
        }

        /// <summary>
        /// Alle eingeschalteten Kameras mit Tiefe und Bildausschnitt, einmal
        /// fuer das Log. Ohne das ist im Nachhinein nicht zu unterscheiden, ob
        /// die Kamera falsch stand oder ob eine ganz andere gerendert hat.
        /// </summary>
        public static string Kameraliste()
        {
            Camera[] all = Camera.allCameras;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(all[i].name).Append(" (Tiefe ").Append(all[i].depth)
                  .Append(", FOV ").Append(all[i].fieldOfView)
                  .Append(all[i].targetTexture == null ? "" : ", in Textur")
                  .Append(")");
            }
            return sb.Length == 0 ? "keine" : sb.ToString();
        }
    }

    // ------------------------------------------------------- BTR-Geschuetz

    /// <summary>
    /// Macht das Turmgeschuetz des BTR-80A bemannbar.
    ///
    /// BELEGT (IL von Assembly-CSharp.dll, gelesen am 2026-08-28):
    ///
    ///   VehicleGameSystem::InitCar setzt
    ///       Passengers = new GameObject[SeatPoints.childCount]
    ///   Ein siebter Sitz ist deshalb KEIN Arraypatch, sondern ein
    ///   zusaetzliches Kind an SeatPoints. Das Spiel dimensioniert selbst,
    ///   solange das Kind vor InitCar existiert.
    ///
    ///   VehicleGameSystem::SitToPassengerPlace(playerGO, placeId, isLocal, ...)
    ///       Passengers[placeId] = playerGO
    ///       playerGO.transform.SetParent(_passengersRootTr)
    ///       Position und Rotation von SeatPoints.GetChild(placeId)
    ///
    ///   PlayerVehicleManager::SomePlayerWantToSit(viewId)
    ///       _passengerPlaceId = vgs.GetFreePassengerPlaceId()
    ///       -1 heisst voll, dann passiert nichts.
    ///   Deshalb haelt FreeSeatPostfix den Geschuetzplatz aus der
    ///   automatischen Vergabe heraus: wer einsteigt, landet auf einem
    ///   normalen Sitz, nicht ueberraschend am Geschuetz.
    ///
    ///   PlayerVehicleManager::ChangeToPassengerPlace(int newPlaceId)
    ///       prueft selbst Passengers[newPlaceId] == null und schickt
    ///       ChangeToPassengerPlaceRPC ueber Photon. Genau ein int-Argument.
    ///
    ///   Am BTR-Prefab traegt InteractColliders/BagaggeContainer die
    ///   Komponenten PhotonView und ItemsContainer.
    ///   ItemsContainer._containerData ist eine ContainerData mit den
    ///   Parallelarrays SlotID, ItemID (ObscuredInt[]) und ItemBullets
    ///   (ObscuredInt[]). Daraus kommt die Munition.
    ///
    ///   Schaden verteilt das Spiel in PlayerFirearmWeaponController::FireOneShot
    ///   ueber PhotonView.RPC: ApplyDamage an NPC_AI2, PlayerApplyDamage an
    ///   Spieler, NetworkApplyDamage an Animal_AI.
    ///
    /// UNGEPRUEFT: alles, was Augen braucht - Sitzhoehe im Turm, Blickrichtung,
    /// Drehsinn, Muendungspunkt, ob der Schaden ankommt. Steht in TASKS.md
    /// unter "Abnahme im Spiel".
    /// </summary>
    public static class Turret
    {
        public const string SeatName = "NDR_GunnerSeat";
        const string BtrPrefix = "BTR-80A";

        static object _vgs;                  // VehicleGameSystem des eigenen Fahrzeugs
        static Transform _vehicleRoot;
        static Transform[] _turrets = new Transform[0];
        static Renderer _turretRenderer;
        static int _gunnerIndex = -1;
        static float _nextScan;
        static float _nextShot;
        static bool _manning;
        static KeyCode _manKey = KeyCode.None;
        static bool _keyParsed;
        static Texture2D _scope;
        static bool _scopeTried;
        static bool _warnedNoSeat;
        static float _yaw;                   // Sollrichtung des Rohrs, Grad
        static float _pitch;
        static bool _aimInit;
        static Texture2D _dot;               // 1x1 weiss, fuer das Fadenkreuz
        static bool _camLogged;
        // Kamera, Bildwinkel und die stillgelegten Skripte liegen seit 0.4.6
        // in CameraOwner - der Turm ist nur noch einer von zwei Bewerbern.
        static string _ammoFrom;             // zuletzt benutzte Munitionsquelle
        static float _nextTry;               // Wiederholsperre nach Fehlschuss
        static string _hinweis;              // Einblendung ueber dem Fadenkreuz
        static float _hinweisBis;
        static float _leerGemeldet;          // Log-Bremse fuer "keine Munition"
        static Texture2D _tankScope;         // Panzerzielfernrohr
        static bool _tankScopeTried;
        static Texture2D _apcScope;          // Richtoptik des BTR
        static bool _apcScopeTried;

        public static bool Manning { get { return _manning; } }

        // --------------------------------------------------- Zwei Werteprofile
        //
        // Das Geschuetz des Panzers ist DASSELBE Geschuetz - nur langsamer,
        // seltener und haerter. Deshalb steht hier kein zweiter Bauplan,
        // sondern nur eine zweite Wertetabelle. Alles andere - Sitz, Kamera,
        // Zielen, Munition aus dem Rucksack, Leuchtspur, Explosion - bleibt
        // Zeile fuer Zeile dieselbe.
        //
        // `_tank` gilt fuer das Fahrzeug, in dem der Spieler gerade sitzt, und
        // wird ausschliesslich in Rescan gesetzt und in Clear zurueckgenommen.
        static bool _tank;

        static float Damage()
        { return _tank ? RevivalPlugin.CfgTankDamage.Value : RevivalPlugin.CfgTurretDamage.Value; }

        static float Reichweite()
        { return _tank ? RevivalPlugin.CfgTankRange.Value : RevivalPlugin.CfgTurretRange.Value; }

        static float Ladezeit()
        { return _tank ? RevivalPlugin.CfgTankDelay.Value : RevivalPlugin.CfgTurretDelay.Value; }

        static float Drehgeschwindigkeit()
        { return _tank ? RevivalPlugin.CfgTankTurnSpeed.Value : RevivalPlugin.CfgTurretTurnSpeed.Value; }

        static float PitchMin()
        { return PitchMin(_tank); }

        static float PitchMax()
        { return PitchMax(_tank); }

        internal static float PitchMin(bool tank)
        { return tank ? RevivalPlugin.CfgTankPitchMin.Value : RevivalPlugin.CfgTurretPitchMin.Value; }

        internal static float PitchMax(bool tank)
        { return tank ? RevivalPlugin.CfgTankPitchMax.Value : RevivalPlugin.CfgTurretPitchMax.Value; }

        static float Bildwinkel()
        { return _tank ? RevivalPlugin.CfgTankFov.Value : RevivalPlugin.CfgTurretFov.Value; }

        // Sprengwerte gibt es nur noch beim Panzer. Das BTR schiesst durch.
        static float Sprengschaden()
        { return RevivalPlugin.CfgTankExplosionDamage.Value; }

        static float Sprengradius()
        { return RevivalPlugin.CfgTankExplosionRadius.Value; }

        static int MunitionsId()
        { return _tank ? RevivalPlugin.CfgTankAmmoId.Value : RevivalPlugin.CfgTurretAmmoId.Value; }

        // Der Unterschied, der die beiden Geschuetze trennt: der Panzer wirft
        // eine Sprenggranate, das BTR schiesst durch - ohne Schalter, ohne
        // Ausnahme. Beim BTR ist das keine Einstellung mehr, sondern die
        // Bauart der Waffe.
        static bool Sprengt()
        { return _tank && RevivalPlugin.CfgTankExplosion.Value; }

        /// <summary>
        /// Der EINZIGE Weg, `_manning` zu aendern. Vorher stand das an sechs
        /// Stellen verstreut, und jede haette die Kamera zurueckgeben muessen.
        ///
        /// Liefert seit 0.4.6 zurueck, ob es geklappt hat: haelt jemand anders
        /// den Blick - die Drohne fliegt -, wird das Geschuetz NICHT besetzt.
        /// Ohne diese Rueckmeldung saesse der Spieler im Turm und saehe durch
        /// die Drohne.
        /// </summary>
        static bool SetManning(bool on)
        {
            if (_manning == on) return true;
            if (on)
            {
                if (!CameraOwner.Request(CameraOwner.Turm,
                                         RevivalPlugin.CfgTurretTakeCamera.Value,
                                         "Geschuetz"))
                    return false;
                _manning = true;
                return true;
            }
            _manning = false;
            CameraOwner.Release(CameraOwner.Turm);
            return true;
        }

        // --------------------------------------------------------- Einhaengen

        public static void Install(Harmony harmony)
        {
            if (!RevivalPlugin.CfgTurret.Value) return;
            try
            {
                Type vgs = RevivalPlugin.TypeByName("VehicleGameSystem");
                if (vgs == null)
                {
                    RevivalPlugin.L.LogWarning("Geschuetz: VehicleGameSystem nicht gefunden.");
                    return;
                }

                MethodInfo initCar = AccessTools.Method(vgs, "InitCar", null, null);
                if (initCar == null)
                    throw new MissingMethodException("VehicleGameSystem.InitCar");
                harmony.Patch(initCar,
                    new HarmonyMethod(typeof(Turret).GetMethod("InitCarPrefix")),
                    null, null, null, null);

                MethodInfo freeSeat = AccessTools.Method(vgs, "GetFreePassengerPlaceId", null, null);
                if (freeSeat == null)
                    throw new MissingMethodException("VehicleGameSystem.GetFreePassengerPlaceId");
                harmony.Patch(freeSeat, null,
                    new HarmonyMethod(typeof(Turret).GetMethod("FreeSeatPostfix")),
                    null, null, null);

                RevivalPlugin.L.LogInfo("Geschuetz: InitCar und GetFreePassengerPlaceId gepatcht.");
                CameraHook.Install(harmony);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Geschuetz konnte nicht eingehaengt werden: " + ex);
            }
        }

        /// <summary>
        /// Haengt den Geschuetzsitz an SeatPoints, BEVOR das Original
        /// Passengers dimensioniert. Nur am BTR-80A, nur einmal je Fahrzeug.
        /// </summary>
        public static void InitCarPrefix(object __instance)
        {
            try
            {
                MonoBehaviour mb = __instance as MonoBehaviour;
                if (mb == null) return;
                if (!IsBtr(mb.transform)) return;

                Transform seatPoints = Field(__instance, "SeatPoints") as Transform;
                if (seatPoints == null)
                {
                    RevivalPlugin.L.LogWarning("Geschuetz: SeatPoints fehlt an " + mb.name + ".");
                    return;
                }
                if (seatPoints.Find(SeatName) != null) return;

                GameObject seat = new GameObject(SeatName);
                seat.transform.SetParent(seatPoints, false);
                seat.transform.localPosition = new Vector3(
                    RevivalPlugin.CfgTurretSeatX.Value,
                    RevivalPlugin.CfgTurretSeatY.Value,
                    RevivalPlugin.CfgTurretSeatZ.Value);
                seat.transform.localRotation = Quaternion.identity;

                RevivalPlugin.L.LogInfo("Geschuetzsitz an " + mb.name
                    + " angehaengt, Index " + (seatPoints.childCount - 1)
                    + " von " + seatPoints.childCount + " Sitzen.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Geschuetzsitz anhaengen fehlgeschlagen: " + ex);
            }
        }

        /// <summary>
        /// Der Geschuetzplatz wird nicht automatisch vergeben. Sonst landet
        /// irgendein Mitfahrer ohne Vorwarnung im Turm.
        ///
        /// Seit den Patrouillen haengt hier ein zweiter Riegel: auf einem
        /// Patrouillenfahrzeug sitzt auf JEDEM Platz ein Mann, also gibt es
        /// keinen freien Platz. Ohne das koennte man ein Fahrzeug, das gerade
        /// auf einen schiesst, einfach besteigen und wegfahren.
        /// </summary>
        public static void FreeSeatPostfix(object __instance, ref int __result)
        {
            try
            {
                if (__result < 0) return;
                if (Patrol.Besetzt(__instance)) { __result = -1; return; }
                MonoBehaviour mb = __instance as MonoBehaviour;
                if (mb == null || !IsBtr(mb.transform)) return;
                Transform seatPoints = Field(__instance, "SeatPoints") as Transform;
                if (seatPoints == null) return;
                int gunner = GunnerIndexOf(seatPoints);
                if (gunner >= 0 && __result == gunner) __result = -1;
            }
            catch { }
        }

        // -------------------------------------------------------------- Frame

        public static void Tick()
        {
            if (!RevivalPlugin.CfgTurret.Value) return;
            try
            {
                Net.EnsureHooked();
                Net.TickRemotes();
                if (Time.time >= _nextScan)
                {
                    _nextScan = Time.time + 0.4f;
                    Rescan();
                }
                if (_vgs == null) { SetManning(false); return; }

                if (Input.GetKeyDown(ManKey())) ToggleManning();
                if (!_manning) return;

                Aim();
                // Die Ladezeit laeuft erst, wenn wirklich eine Granate im Rohr
                // war. Bis 0.4.8 stand sie eine Zeile zu frueh: wer keine
                // Munition hatte, bekam zwoelf Sekunden Ladebalken und nie
                // einen Schuss - im Spiel sah das aus wie ein kaputtes
                // Geschuetz, im Log stand die Begruendung.
                if (Input.GetMouseButton(0) && Time.time >= _nextShot
                    && Time.time >= _nextTry)
                {
                    if (Fire()) _nextShot = Time.time + Ladezeit();
                    else _nextTry = Time.time + 1f;
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Geschuetz-Tick: " + ex);
                SetManning(false);
            }
        }

        /// <summary>Sucht das Fahrzeug, in dem der lokale Spieler sitzt.</summary>
        static void Rescan()
        {
            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (vgsType == null) { _vgs = null; return; }

            if (_vgs != null)
            {
                MonoBehaviour cur = _vgs as MonoBehaviour;
                if (cur != null && IntField(_vgs, "_localPlayerPassengerId") >= 0) return;
                Clear();
            }

            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(vgsType);
            for (int i = 0; i < all.Length; i++)
            {
                MonoBehaviour mb = all[i] as MonoBehaviour;
                if (mb == null) continue;
                if (IntField(all[i], "_localPlayerPassengerId") < 0) continue;
                if (!IsBtr(mb.transform)) continue;

                _vgs = all[i];
                _vehicleRoot = mb.transform;
                _tank = Tank.IstPanzer(mb.transform);
                Transform seatPoints = Field(_vgs, "SeatPoints") as Transform;
                _gunnerIndex = seatPoints == null ? -1 : GunnerIndexOf(seatPoints);
                CollectTurrets(mb.transform);
                if (_gunnerIndex < 0 && !_warnedNoSeat)
                {
                    _warnedNoSeat = true;
                    RevivalPlugin.L.LogWarning("Geschuetz: kein " + SeatName
                        + " an diesem BTR. InitCar lief vermutlich vor dem Plugin.");
                }
                return;
            }
            Clear();
        }

        static void Clear()
        {
            _vgs = null;
            _vehicleRoot = null;
            _tank = false;
            _gunnerIndex = -1;
            _turrets = new Transform[0];
            _turretRenderer = null;
            SetManning(false);
        }

        /// <summary>Alle vier LOD-Tuerme einsammeln - die LODGroup blendet um.</summary>
        static void CollectTurrets(Transform root)
        {
            _turrets = FindTurrets(root);
            _turretRenderer = null;
            for (int i = 0; i < _turrets.Length; i++)
            {
                Renderer r = _turrets[i].GetComponent<Renderer>();
                if (r != null) { _turretRenderer = r; break; }
            }
            RevivalPlugin.L.LogInfo("Geschuetz: " + _turrets.Length
                + " Turmobjekte gefunden, Sitzindex " + _gunnerIndex
                + ", Werteprofil " + (_tank ? "T-72" : "BTR-80A") + ".");
        }

        internal static Transform[] FindTurrets(Transform root)
        {
            List<Transform> found = new List<Transform>();
            if (root == null) return found.ToArray();
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == "turret") found.Add(all[i]);
            return found.ToArray();
        }

        // ------------------------------------------------------- Platzwechsel

        static void ToggleManning()
        {
            if (_gunnerIndex < 0)
            {
                RevivalPlugin.L.LogInfo("Geschuetz: dieses Fahrzeug hat keinen Geschuetzplatz.");
                return;
            }
            int here = IntField(_vgs, "_localPlayerPassengerId");

            if (_manning)
            {
                SetManning(false);
                // Koerperlich zurueck auf einen normalen Platz. Ohne das bleibt
                // man im Turm sitzen, und beim naechsten Druck gaebe es nichts
                // mehr zu schalten.
                int back = FirstNormalSeat();
                if (back >= 0) ChangeSeat(back);
                RevivalPlugin.L.LogInfo("Geschuetz verlassen"
                    + (back >= 0 ? ", zurueck auf Platz " + back + "." : "."));
                return;
            }

            // Wer schon auf dem Geschuetzplatz sitzt, will es bedienen.
            //
            // Bis 2026-08-28 stand hier das Gegenteil: derselbe Zweig, der das
            // Verlassen meldet, fing auch den Fall "sitzt bereits auf Platz 6"
            // ab - und bewegte dabei niemanden. Wer vom Spiel auf den Turmplatz
            // gesetzt wurde, bekam auf jeden Tastendruck "Geschuetz verlassen"
            // und kam nie hinein. Genau so sah es im Log vom 2026-08-28 aus.
            if (here == _gunnerIndex)
            {
                if (!SetManning(true)) return;   // CameraOwner begruendet selbst
                _aimInit = false;
                _camLogged = false;
                RevivalPlugin.L.LogInfo("Geschuetz besetzt (Sitz " + here
                    + ", der Spieler sass schon dort).");
                return;
            }

            if (!ChangeSeat(_gunnerIndex)) return;   // ChangeSeat begruendet selbst
            if (!SetManning(true)) return;           // CameraOwner begruendet selbst
            _aimInit = false;
            _camLogged = false;
            RevivalPlugin.L.LogInfo("Geschuetz besetzt (Sitz " + _gunnerIndex
                + ", vorher " + here + ").");
        }

        /// <summary>Erster freier Platz, der nicht der Geschuetzplatz ist.</summary>
        static int FirstNormalSeat()
        {
            Array passengers = Field(_vgs, "Passengers") as Array;
            if (passengers == null) return -1;
            for (int i = 0; i < passengers.Length; i++)
            {
                if (i == _gunnerIndex) continue;
                GameObject go = passengers.GetValue(i) as GameObject;
                if (go == null) return i;
            }
            return -1;
        }

        /// <summary>PlayerVehicleManager.ChangeToPassengerPlace(int) - ein Argument.</summary>
        static bool ChangeSeat(int index)
        {
            object pvm = Field(_vgs, "_playerVehicleManager");
            if (pvm == null)
            {
                RevivalPlugin.L.LogWarning("Geschuetz: _playerVehicleManager ist null - "
                    + "der lokale Spieler ist an diesem Fahrzeug nicht eingetragen.");
                return false;
            }
            MethodInfo m = AccessTools.Method(pvm.GetType(), "ChangeToPassengerPlace",
                                              new Type[] { typeof(int) }, null);
            if (m == null)
            {
                RevivalPlugin.L.LogWarning("Geschuetz: ChangeToPassengerPlace(int) fehlt.");
                return false;
            }
            Array passengers = Field(_vgs, "Passengers") as Array;
            if (passengers == null)
            {
                RevivalPlugin.L.LogWarning("Geschuetz: Passengers ist null.");
                return false;
            }
            if (index >= passengers.Length)
            {
                RevivalPlugin.L.LogWarning("Geschuetz: Passengers hat nur "
                    + passengers.Length + " Plaetze, der Turm waere Nummer " + index
                    + ". InitCar lief vor dem Plugin.");
                return false;
            }
            // Ueber GameObject vergleichen, nicht ueber object: Unity haelt eine
            // zerstoerte Instanz als Verweis fest, meldet sie aber ueber den
            // eigenen ==-Operator als null. Ein roher object-Vergleich haelt so
            // einen Platz fuer belegt, den das Spiel selbst als frei ansieht -
            // VehicleGameSystem prueft mit Object::op_Inequality.
            GameObject sitting = passengers.GetValue(index) as GameObject;
            if (sitting != null)
            {
                RevivalPlugin.L.LogWarning("Geschuetz: Platz " + index
                    + " ist belegt von \"" + sitting.name + "\".");
                return false;
            }
            m.Invoke(pvm, new object[] { index });
            return true;
        }

        // ------------------------------------------------------------- Zielen

        /// <summary>
        /// Der Turm haengt unter Meshes, und Meshes ist um -90 Grad um X
        /// gedreht. Im Turmraum gilt damit: lokales +Z zeigt in der Welt nach
        /// oben, lokales -Y ist die Rohrrichtung.
        ///
        /// Quaternion.LookRotation baut dagegen eine Drehung, deren +Z nach
        /// vorn und deren +Y nach oben zeigt. Die Differenz ist eine feste
        /// Korrekturdrehung - damit stimmt der Drehsinn, ohne Vorzeichen zu
        /// raten.
        /// </summary>
        static void Aim()
        {
            if (_turrets.Length == 0) return;
            Transform parent = _turrets[0].parent;
            if (parent == null) return;

            if (!_aimInit) InitAim();

            // Seitenrichtung: HERGELEITET, nicht geraten. Bei Seitenwinkel a
            // ist die Rohrrichtung im Turmraum (-sin a, -cos a, sin e); der
            // Turmraum liegt unter Meshes, dessen -90 Grad um X das lokale X
            // unveraendert auf das Welt-X des Fahrzeugs abbilden, und +X ist
            // am BTR-Prefab rechts (Rad FR bei x +3.57). Ein groesseres a
            // schiebt das Rohr also nach -X, das heisst nach LINKS. Bis
            // 2026-08-28 stand hier "+=": Maus nach rechts drehte den Turm
            // nach links.
            float sens = RevivalPlugin.CfgTurretSensitivity.Value;
            float mx = Input.GetAxis("Mouse X") * sens;
            if (RevivalPlugin.CfgTurretInvertX.Value) mx = -mx;
            _yaw -= mx;
            _pitch += Input.GetAxis("Mouse Y") * sens;
            _pitch = Mathf.Clamp(_pitch, PitchMin(), PitchMax());
            if (_yaw > 180f) _yaw -= 360f;
            if (_yaw < -180f) _yaw += 360f;

            // Vorlauf begrenzen. Seit 0.4.9 haengt der BLICK am Rohr, nicht an
            // der Maus - das Bild schwenkt also nur so schnell wie der Turm.
            // Ohne diese Grenze liefe die Sollrichtung beliebig weit voraus:
            // eine schnelle Mausbewegung, und der Turm dreht danach noch
            // Sekunden weiter, ohne dass der Spieler es aufhalten kann.
            float lead = RevivalPlugin.CfgTurretAimLead.Value;
            if (lead >= 0f)
            {
                float iy, ip;
                if (IstStellung(out iy, out ip))
                {
                    _yaw = iy + Mathf.Clamp(Mathf.DeltaAngle(iy, _yaw), -lead, lead);
                    _pitch = ip + Mathf.Clamp(_pitch - ip, -lead, lead);
                    _pitch = Mathf.Clamp(_pitch, PitchMin(), PitchMax());
                    if (_yaw > 180f) _yaw -= 360f;
                    if (_yaw < -180f) _yaw += 360f;
                }
            }

            Quaternion want = LocalRotationFor(_yaw, _pitch);
            float step = Drehgeschwindigkeit() * Time.deltaTime;
            for (int i = 0; i < _turrets.Length; i++)
                _turrets[i].localRotation =
                    Quaternion.RotateTowards(_turrets[i].localRotation, want, step);

            float actualYaw, actualPitch;
            if (IstStellung(out actualYaw, out actualPitch))
                Net.Publish(_vehicleRoot, actualYaw, actualPitch);
        }

        /// <summary>
        /// Uebersetzt Seiten- und Hoehenwinkel in die lokale Drehung des Turms.
        ///
        /// Der Turm haengt unter Meshes, und Meshes ist um -90 Grad um X
        /// gedreht. Im Turmraum gilt damit: lokales +Z zeigt in der Welt nach
        /// oben, lokales -Y ist die Rohrrichtung. Bei Seitenwinkel 0 ist die
        /// Rohrrichtung also (0, -1, 0), rechts davon liegt (-1, 0, 0).
        ///
        /// Quaternion.LookRotation baut dagegen eine Drehung, deren +Z nach
        /// vorn und deren +Y nach oben zeigt. Die Differenz ist eine feste
        /// Korrekturdrehung - damit stimmt der Drehsinn, ohne Vorzeichen zu
        /// raten.
        /// </summary>
        internal static Quaternion LocalRotationFor(float yaw, float pitch)
        {
            float a = yaw * Mathf.Deg2Rad;
            float e = pitch * Mathf.Deg2Rad;
            float ce = Mathf.Cos(e);
            Vector3 dirLocal = new Vector3(-Mathf.Sin(a) * ce,
                                           -Mathf.Cos(a) * ce,
                                           Mathf.Sin(e));
            Quaternion correction = Quaternion.Inverse(
                Quaternion.LookRotation(new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, 1f)));
            return Quaternion.LookRotation(dirLocal, new Vector3(0f, 0f, 1f)) * correction;
        }

        /// <summary>
        /// Seiten- und Hoehenwinkel, die das Rohr WIRKLICH hat - die Umkehrung
        /// von LocalRotationFor. Zwei Aufgaben haengen daran: der Sollwinkel
        /// beim Aufsitzen (damit der Turm nicht springt) und die Vorlaufgrenze
        /// beim Zielen.
        /// </summary>
        static bool IstStellung(out float yaw, out float pitch)
        {
            if (_turrets.Length == 0)
            {
                yaw = 0f;
                pitch = 0f;
                return false;
            }
            return AnglesFor(_turrets[0], out yaw, out pitch);
        }

        internal static bool AnglesFor(Transform turret, out float yaw, out float pitch)
        {
            yaw = 0f;
            pitch = 0f;
            if (turret == null) return false;
            Vector3 d = turret.localRotation * new Vector3(0f, -1f, 0f);
            if (d.sqrMagnitude < 0.000001f) return false;
            d.Normalize();
            pitch = Mathf.Asin(Mathf.Clamp(d.z, -1f, 1f)) * Mathf.Rad2Deg;
            yaw = Mathf.Atan2(-d.x, -d.y) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>
        /// Sollwinkel aus der aktuellen Stellung uebernehmen, damit der Turm
        /// beim Aufsitzen nicht springt.
        /// </summary>
        static void InitAim()
        {
            _aimInit = true;
            _yaw = 0f;
            _pitch = 0f;
            IstStellung(out _yaw, out _pitch);
        }

        /// <summary>
        /// Setzt die Kamera in die Rohrachse. Gehoert in LateUpdate: das Spiel
        /// zieht seine Fahrzeugkamera im selben Frame nach, und wer zuerst
        /// schreibt, verliert.
        /// </summary>
        public static void LateTick()
        {
            if (!RevivalPlugin.CfgTurret.Value || !_manning) return;
            try
            {
                if (_turrets.Length == 0) return;
                Camera cam = ViewCamera();
                if (cam == null) return;

                // Der Blick folgt der ROHRSTELLUNG, nicht der Sollrichtung.
                //
                // Bis 0.4.8 war es umgekehrt, mit der Begruendung, ein Bild,
                // das der Maus hinterherkriecht, fuehle sich an wie Sirup. Beim
                // BTR mit 140 Grad je Sekunde stimmt das auch. Beim Panzer mit
                // 22 Grad je Sekunde war es dagegen ein Balanceloch: das
                // Fadenkreuz - und damit der Schuss, siehe AimRay - sprang
                // sofort ueberall hin, waehrend das Rohr noch schwenkte. Die
                // langsame Turmdrehung, das erklaerte Gegengewicht zur
                // Feuerkraft, kostete nichts.
                //
                // Jetzt schwenkt das Bild genau so schnell wie der Turm. Damit
                // das nicht zaeh wird, begrenzt Aim() den Vorlauf der Maus auf
                // AimLead Grad.
                Vector3 dir, up;
                AimAxes(out dir, out up);
                Vector3 side = Vector3.Cross(up, dir).normalized;

                // Ankerpunkt ist der TURMDREHPUNKT, nicht die Muendung. Ein
                // Auge kurz vor der Muendung liegt beim BTR im Bugblech: das
                // Turmmesh reicht laengs bis 8.9, die Wanne bis 11.47.
                Vector3 eye = _turrets[0].position
                    + dir * RevivalPlugin.CfgTurretEyeForward.Value
                    + up * RevivalPlugin.CfgTurretEyeUp.Value
                    + side * RevivalPlugin.CfgTurretEyeSide.Value;

                cam.transform.position = eye;
                cam.transform.rotation = Quaternion.LookRotation(dir, up);

                // Bildwinkel jeden Frame nachziehen, nicht einmal beim
                // Aufsitzen: Zielfernrohr- und Sprinteffekte des Spiels
                // schreiben ihn sonst wieder um.
                if (Bildwinkel() > 1f)
                    cam.fieldOfView = Bildwinkel();

                if (!_camLogged)
                {
                    _camLogged = true;
                    RevivalPlugin.L.LogInfo("Geschuetzkamera: \"" + cam.name
                        + "\" (Elternteil \""
                        + (cam.transform.parent == null ? "-" : cam.transform.parent.name)
                        + "\"), Auge " + eye + ", Turmmitte "
                        + (_turretRenderer == null ? Vector3.zero : _turretRenderer.bounds.center)
                        + ", Muendung " + Muzzle() + ", Rohrrichtung " + dir + ".");
                    // Gezielt wird ueber genau diese Kamera. Steht hier mehr
                    // als eine, ist im Nachhinein zu klaeren, ob wirklich die
                    // oben genannte das Bild macht.
                    RevivalPlugin.L.LogInfo("Geschuetzkamera: aktive Kameras "
                        + CameraOwner.Kameraliste() + ".");
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Geschuetzkamera: " + ex);
                SetManning(false);
            }
        }

        /// <summary>
        /// Die Kamera, die wirklich rendert - jetzt aus einer Hand, damit
        /// Turm und Drohne nicht auf verschiedene Kameras zeigen koennen.
        /// </summary>
        static Camera ViewCamera()
        {
            return CameraOwner.ViewCamera();
        }

        /// <summary>
        /// Blickachse des Geschuetzes: die tatsaechliche Rohrachse mitsamt
        /// ihrer Oben-Richtung. Im Turmraum ist -Y die Rohrrichtung und +Z
        /// oben (siehe LocalRotationFor).
        /// </summary>
        static void AimAxes(out Vector3 dir, out Vector3 up)
        {
            dir = BarrelDirection();
            up = Vector3.up;
            if (_turrets.Length == 0) return;
            Vector3 u = _turrets[0].TransformDirection(new Vector3(0f, 0f, 1f));
            if (u.sqrMagnitude < 0.000001f) return;
            up = u.normalized;
        }

        /// <summary>Weltrichtung, in die das Rohr gerade zeigt.</summary>
        static Vector3 BarrelDirection()
        {
            if (_turrets.Length == 0) return Vector3.forward;
            return _turrets[0].TransformDirection(new Vector3(0f, -1f, 0f)).normalized;
        }

        /// <summary>
        /// Muendung aus den WELT-Bounds des Turmrenderers, nicht aus
        /// Modellzahlen. Die Einheiten des BTR-Modells sind nicht Meter
        /// (Radstand plus/minus 3.47 bei 2.9 m Spurweite in echt), ein hart
        /// eingetragener Versatz waere also geraten.
        /// </summary>
        static Vector3 Muzzle()
        {
            Vector3 dir = BarrelDirection();
            if (_turretRenderer == null)
                return (_turrets.Length > 0 ? _turrets[0].position : Vector3.zero) + dir;
            Bounds b = _turretRenderer.bounds;
            float reach = Vector3.Dot(b.extents, new Vector3(
                Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z)));
            return b.center + dir * (reach + 0.5f);
        }

        // ----------------------------------------------------------- Schiessen

        /// <summary>
        /// Ein Schuss. Liefert false, wenn KEINER gefallen ist - dann darf auch
        /// die Ladezeit nicht anlaufen.
        /// </summary>
        static bool Fire()
        {
            if (RevivalPlugin.CfgTurretAmmo.Value && !TakeRound())
            {
                Hinweis("Keine Munition - Item " + MunitionsId()
                        + " fehlt in Kofferraum, Rucksack und Weste", 2.5f);
                // Der Mausknopf bleibt gedrueckt; ohne Bremse stuende diese
                // Zeile jede Sekunde im Log.
                if (Time.time >= _leerGemeldet)
                {
                    _leerGemeldet = Time.time + 10f;
                    RevivalPlugin.L.LogInfo("Geschuetz: keine Munition (Item "
                        + MunitionsId()
                        + ") im Kofferraum und im Rucksack.");
                }
                return false;
            }

            // Rueckstoss: das Rohr schlaegt hoch, der Sollwinkel wandert mit.
            // Bewusst klein - der Turm sitzt auf zwoelf Tonnen Fahrzeug, und
            // ein Geschuetz, das nach jedem Schuss neu gesucht werden muss,
            // ist auf diese Entfernung unbrauchbar.
            _pitch = Mathf.Clamp(_pitch + RevivalPlugin.CfgTurretRecoil.Value,
                                 PitchMin(), PitchMax());

            Vector3 origin, dir;
            AimRay(out origin, out dir);
            VehicleShotSound.Play(origin, _tank);
            Net.PublishShot(origin, _tank);
            Vector3 impact;
            GameObject struck = RaycastPastVehicle(origin, dir,
                                                   Reichweite(), out impact);

            // Leuchtspur IMMER, auch wenn nichts getroffen wurde. Bis heute war
            // ein Schuss weder zu sehen noch zu hoeren - im Spiel sah das aus,
            // als ginge das Geschuetz gar nicht los.
            Vector3 ende = struck == null
                ? origin + dir * Reichweite() : impact;
            Tracer(origin + dir * 3f, ende);
            if (struck == null) return true;

            // Sprenggranate am Einschlag - beim Panzer. Das BTR schiesst seit
            // 0.5.1 durch: ein einzelner, flacher Schuss mit Leuchtspur, der
            // nur trifft, worauf die Bildmitte steht. Zu sehen ist er auch so,
            // die Leuchtspur oben laeuft in jedem Fall.
            if (Sprengt())
            {
                try
                {
                    RocketHook.Detonate(impact - dir * 0.15f,
                        Sprengschaden(), Sprengradius(), 3f);
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError("Geschuetz: Einschlag ohne Explosion - " + ex.Message);
                }
            }

            float damage = Damage();

            // Reihenfolge und Methodennamen wie in FireOneShot.
            if (TryDamage(struck, "NPC_AI2", "ApplyDamage", damage)) return true;
            if (TryDamage(struck, "Animal_AI", "NetworkApplyDamage", damage)) return true;
            TryDamage(struck, "PlayerNetworkController", "PlayerApplyDamage", damage);
            return true;
        }

        /// <summary>Kurze Einblendung ueber dem Fadenkreuz.</summary>
        internal static void Hinweis(string text, float sekunden)
        {
            _hinweis = text;
            _hinweisBis = Time.time + sekunden;
        }

        // Farben der Leuchtspur. Weissglut im Kern, Orange aussen und am Ende -
        // ein Leuchtspurgeschoss brennt vorn heisser als hinten.
        static readonly Color SpurKern = new Color(1.00f, 0.96f, 0.78f, 1.0f);
        static readonly Color SpurEnde = new Color(1.00f, 0.62f, 0.20f, 1.0f);
        static readonly Color SpurHof = new Color(1.00f, 0.38f, 0.10f, 1.0f);

        /// <summary>
        /// Leuchtspur von der Muendung zum Einschlag.
        ///
        /// WARUM DREI LINIEN UND NICHT EINE (0.5.2)
        /// Eine einzelne Linie hat genau eine Breite und eine Farbe. Damit ist
        /// sie entweder duenn und hell oder dick und flau - was fehlt, ist der
        /// Uebergang, an dem das Auge "gluehend" liest. Der Shader ist additiv,
        /// also addiert sich, was uebereinander liegt: ein breiter, dunkler Hof
        /// zuerst, darauf der schmale helle Kern, und in der Summe ein Balken
        /// mit weissem Zentrum und orangem Rand. Dazu ein kurzes, sehr breites
        /// Stueck am Anfang - das Muendungsfeuer.
        ///
        /// KEIN LICHT AM ANFANG DER BAHN
        /// Naheliegend waere eine Punktlichtquelle als Muendungsfeuer. Sie
        /// saesse aber im Auge des Spielers: `von` ist nicht die Muendung,
        /// sondern die Kameraposition plus drei Einheiten, also gut einen Meter
        /// vor dem Gesicht (siehe AimRay). Ein Licht mit Reichweite dort
        /// ueberstrahlt das halbe Bild. Das Muendungsfeuer ist deshalb
        /// Geometrie und kein Licht.
        ///
        /// Der Panzer bekommt den vollen Balken, das BTR eine schlankere Spur:
        /// dessen Geschuetz schiesst acht mal je Sekunde, und acht Balken
        /// gleichzeitig im Bild waeren kein Feuerstoss, sondern eine Wand.
        /// </summary>
        static void Tracer(Vector3 von, Vector3 bis)
        {
            try
            {
                List<Vector3> bahn = new List<Vector3>();
                bahn.Add(von);
                bahn.Add(bis);

                if (_tank)
                {
                    RocketHook.SpawnTracer(bahn, 1.20f, 0.50f, SpurHof, SpurHof, 0.30f);
                    RocketHook.SpawnTracer(bahn, 0.44f, 0.17f, SpurKern, SpurEnde, 0.55f);
                }
                else
                {
                    RocketHook.SpawnTracer(bahn, 0.34f, 0.14f, SpurHof, SpurHof, 0.10f);
                    RocketHook.SpawnTracer(bahn, 0.13f, 0.05f, SpurKern, SpurEnde, 0.18f);
                }

                // Muendungsfeuer: die ersten Meter der Bahn, sehr breit und
                // sehr kurz. Es sitzt auf der Bahn und braucht deshalb weder
                // die Muendungsposition noch die Rohrrichtung.
                Vector3 achse = bis - von;
                float laenge = achse.magnitude;
                if (laenge > 0.01f)
                {
                    float feuer = _tank ? 7.0f : 3.0f;
                    if (feuer > laenge) feuer = laenge;
                    List<Vector3> muendung = new List<Vector3>();
                    muendung.Add(von);
                    muendung.Add(von + achse / laenge * feuer);
                    RocketHook.SpawnTracer(muendung,
                                           _tank ? 2.60f : 0.90f, 0.05f,
                                           Color.white, SpurHof,
                                           _tank ? 0.09f : 0.05f);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Geschuetz: Leuchtspur - " + ex.Message);
            }
        }

        /// <summary>
        /// Die Achse, auf der der Schuss laeuft: Standort und Blickrichtung der
        /// KAMERA, nicht Muendung und Rohrrichtung.
        ///
        /// Begruendung (2026-08-28, dritter Anlauf): solange gezielt wurde,
        /// indem das Fadenkreuz auf die Rohrachse projiziert wurde, hing das
        /// Fadenkreuz irgendwo im Bild und wanderte beim Schwenken sogar an den
        /// Rand - zielen war damit Glueckssache. Umgekehrt ist es eindeutig:
        /// getroffen wird, worauf die Bildmitte zeigt. Das gilt auch dann noch,
        /// wenn das Spiel die Kamera doch woanders hinsetzt als LateTick sie
        /// gestellt hat; das Fadenkreuz kann dann gar nicht mehr luegen.
        ///
        /// Der Rueckstoss wirkt weiter ueber den Turm: die Kamera haengt an der
        /// Rohrachse, also wandert mit dem Rohr auch der Blick.
        /// </summary>
        static void AimRay(out Vector3 origin, out Vector3 direction)
        {
            Camera cam = ViewCamera();
            if (cam == null)
            {
                origin = Muzzle();
                direction = BarrelDirection();
                return;
            }
            origin = cam.transform.position;
            direction = cam.transform.forward;
        }

        /// <summary>
        /// Wie RaycastObject, ueberspringt aber Treffer am eigenen Fahrzeug.
        ///
        /// Noetig, weil der Schuss auf der Rohrachse beginnt und die Muendung
        /// beim BTR NOCH IN DER WANNE steckt: das Turmmesh reicht laengs bis
        /// z 8.9, die Wanne bis z 11.47 (gemessen am Prefab BTR-80A_Spawn).
        /// Ohne das schoesse das Geschuetz in das Auto, in dem man sitzt.
        /// </summary>
        static GameObject RaycastPastVehicle(Vector3 origin, Vector3 direction,
                                             float range, out Vector3 point)
        {
            point = Vector3.zero;
            Vector3 from = origin;
            float rest = range;
            for (int versuch = 0; versuch < 4 && rest > 0f; versuch++)
            {
                Vector3 hit;
                GameObject go = RaycastObject(from, direction, rest, out hit);
                if (go == null) return null;
                if (!IsOwnVehicle(go)) { point = hit; return go; }
                rest -= Vector3.Distance(from, hit) + 0.25f;
                from = hit + direction * 0.25f;
            }
            return null;
        }

        /// <summary>Gehoert das Objekt zu dem Fahrzeug, in dem wir sitzen?</summary>
        static bool IsOwnVehicle(GameObject go)
        {
            if (go == null || _vehicleRoot == null) return false;
            Transform t = go.transform;
            while (t != null)
            {
                if (t == _vehicleRoot) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Raycast ueber Reflexion. build.ps1 referenziert bewusst nur
        /// UnityEngine.dll, CoreModule, ImageConversionModule und IMGUIModule -
        /// UnityEngine.PhysicsModule ist nicht dabei, also sind Physics,
        /// RaycastHit und Collider als Typen nicht uebersetzbar. Derselbe Weg
        /// wie in RocketHook.Raycast.
        /// </summary>
        internal static GameObject RaycastObject(Vector3 origin, Vector3 direction,
                                                 float range, out Vector3 point)
        {
            point = Vector3.zero;
            if (!LookUpRaycast()) return null;

            object[] args = new object[] {
                origin, direction, Activator.CreateInstance(_hitType), range };
            if (!(bool)_raycast.Invoke(null, args)) return null;

            if (_hitPoint != null) point = (Vector3)_hitPoint.GetValue(args[2], null);

            Component hitCollider = _hitCollider.GetValue(args[2], null) as Component;
            return hitCollider == null ? null : hitCollider.gameObject;
        }

        static Type _hitType;
        static MethodInfo _raycast;
        static PropertyInfo _hitPoint, _hitCollider;
        static bool _raycastLookedUp;

        /// <summary>
        /// Resolves Physics.Raycast and the two RaycastHit properties once.
        ///
        /// This used to happen inside RaycastObject, per cast. Two type
        /// lookups, a GetMethods over all ~120 public statics of
        /// UnityEngine.Physics with a linear scan for the right overload, and
        /// two GetProperty calls - milliseconds of reflection for a cast that
        /// takes microseconds. The turret survived it because it casts when a
        /// shell leaves the barrel; the patrol driver casts three obstacle
        /// rays every FixedUpdate and put the game on the floor at 3 FPS.
        ///
        /// A failure is remembered, unlike a missing type in
        /// RevivalPlugin.TypeByName: if UnityEngine.PhysicsModule is not
        /// there on the first cast it will not appear later, and repeating
        /// the full scan per frame is the very thing this exists to stop.
        /// </summary>
        static bool LookUpRaycast()
        {
            if (_raycastLookedUp) return _raycast != null;
            _raycastLookedUp = true;

            Type physicsType = RevivalPlugin.TypeByName("UnityEngine.Physics");
            _hitType = RevivalPlugin.TypeByName("UnityEngine.RaycastHit");
            if (physicsType == null || _hitType == null)
            {
                RevivalPlugin.L.LogWarning("Geschuetz: UnityEngine.Physics oder "
                    + "RaycastHit nicht gefunden - kein Raycast.");
                return false;
            }

            MethodInfo[] methods = physicsType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "Raycast" || m.ReturnType != typeof(bool)) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 4 && ps[0].ParameterType == typeof(Vector3)
                    && ps[1].ParameterType == typeof(Vector3)
                    && ps[2].ParameterType.IsByRef
                    && ps[2].ParameterType.GetElementType() == _hitType
                    && ps[3].ParameterType == typeof(float))
                {
                    _raycast = m;
                    break;
                }
            }
            if (_raycast == null)
            {
                RevivalPlugin.L.LogWarning("Geschuetz: Physics.Raycast nicht gefunden.");
                return false;
            }

            _hitPoint = _hitType.GetProperty("point",
                BindingFlags.Public | BindingFlags.Instance);
            _hitCollider = _hitType.GetProperty("collider",
                BindingFlags.Public | BindingFlags.Instance);
            if (_hitCollider == null)
            {
                RevivalPlugin.L.LogWarning("Geschuetz: RaycastHit.collider nicht gefunden.");
                _raycast = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Sucht am getroffenen Objekt die genannte Komponente und ruft ihre
        /// Schadensmethode. Die Argumentliste ist nicht belegt, deshalb wird
        /// sie aus der Methode selbst gelesen: der erste float bekommt den
        /// Schaden, alles andere den Vorgabewert seines Typs. Passt keine
        /// Signatur, wird das protokolliert statt geraten.
        /// </summary>
        internal static bool TryDamage(GameObject struck, string typeName, string rpc, float damage)
        {
            Type t = RevivalPlugin.TypeByName(typeName);
            if (t == null) return false;
            Component target = struck.GetComponentInParent(t);
            if (target == null) return false;

            MethodInfo m = AccessTools.Method(t, rpc, null, null);
            if (m == null)
            {
                RevivalPlugin.L.LogWarning("Geschuetz: " + typeName + "." + rpc + " fehlt.");
                return false;
            }
            ParameterInfo[] ps = m.GetParameters();
            object[] args = new object[ps.Length];
            bool damagePlaced = false;
            for (int i = 0; i < ps.Length; i++)
            {
                Type pt = ps[i].ParameterType;
                if (!damagePlaced && pt == typeof(float))
                {
                    args[i] = damage;
                    damagePlaced = true;
                }
                else if (pt == typeof(string)) args[i] = string.Empty;
                else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                else args[i] = null;
            }
            if (!damagePlaced)
            {
                RevivalPlugin.L.LogWarning("Geschuetz: " + typeName + "." + rpc
                    + " hat keinen float-Parameter - Schaden nicht zuzuordnen.");
                return false;
            }
            m.Invoke(target, args);
            RevivalPlugin.L.LogInfo("Geschuetztreffer: " + typeName + ", "
                + damage + " Schaden.");
            return true;
        }

        // ----------------------------------------------------------- Munition

        /// <summary>
        /// Nimmt eine Patrone aus dem Kofferraum. Der Kofferraum ist der
        /// ItemsContainer an InteractColliders/BagaggeContainer; die Menge
        /// steht in ContainerData.ItemBullets an derselben Stelle, an der
        /// ItemID die Munitions-ID traegt.
        /// </summary>
        static bool TakeRound()
        {
            object trunk = TrunkContainer();
            if (trunk != null && TakeFrom(trunk, Field(trunk, "_containerData"), "Kofferraum"))
                return true;

            if (!RevivalPlugin.CfgTurretAmmoBackpack.Value) return false;

            // Alle eigenen Inventare, Rucksack UND Weste. Die Weste bleibt als
            // Quelle drin, obwohl dort nach dem 2026-08-28 keine Munition mehr
            // liegt: Westenplatz 7 gab es nie, und ein Eintrag darauf hat das
            // Laden des ganzen Profils abgebrochen (Beleg im Kopf von
            // invtool.py). Ein einziger,
            // ueber PlayerInventory() geratener Kandidat reicht also nicht: das
            // Spiel legt fuer Fahrzeuge Spielerkopien an
            // (VehicleGameSystem::CheckAndRemovePlayerCopys), und die erste
            // gefundene Instanz muss nicht die mit dem gefuellten Rucksack sein.
            List<object> invs = PlayerInventories();
            for (int i = 0; i < invs.Count; i++)
            {
                if (TakeFrom(invs[i], Field(invs[i], "_backpackData"), "Rucksack")) return true;
                if (TakeFrom(invs[i], Field(invs[i], "_gearsData"), "Weste")) return true;
            }
            BerichteLeer(trunk, invs);
            return false;
        }

        /// <summary>
        /// Removes ONE whole item `wanted` from the player's own inventory -
        /// backpack, vest or weapon slots. Used by the drone.
        ///
        /// WHY NOT TakeFrom: TakeFrom decrements ItemBullets, which is the
        /// number of rounds INSIDE one item (a belt of 200, a box of 10) - the
        /// right thing for turret ammunition, the wrong thing for an item that
        /// is consumed as a whole. A drone with Bullets 1 ended up at
        /// "Bullets 0" and stayed in the slot, because the slot was never
        /// cleared: TakeFrom looked for NetworkClearContainerSlot(int) or
        /// ClearBackpackSlot(int) on the PlayerInventoryManager, and neither
        /// exists there (CONFIRMED, IL: the manager has
        /// ClearBackpackSlot(int SlotID, int ItemID, bool onChangeInventory)).
        /// The reflection lookup returned null and nothing happened.
        ///
        /// The game's own RemoveItem(int ItemID, int Count) does all of it:
        /// it counts the item across backpack, vest and weapon slots, refuses
        /// if there are fewer than Count, and clears whole slots through
        /// ClearBackpackSlot / ClearGearSlot / ClearWeaponSlot with
        /// onChangeInventory set - so UI and server data are updated too.
        /// It returns void, hence the count before and after.
        /// </summary>
        static int _hasId = -1;
        static bool _hasResult;
        static float _hasUntil;

        /// <summary>
        /// Does item `wanted` lie in one of the local player's inventories?
        /// The same three containers TakeItem walks, but nothing is taken.
        ///
        /// The answer is kept for half a second. The jammer asks in every
        /// frame, and FindObjectsOfType(PlayerInventoryManager) per frame is
        /// the kind of cost that never shows up in a log and always shows up
        /// in the frame time.
        /// </summary>
        internal static bool HasItem(int wanted)
        {
            if (wanted == _hasId && Time.time < _hasUntil) return _hasResult;
            bool found = false;
            List<object> invs = PlayerInventories();
            for (int i = 0; i < invs.Count && !found; i++)
                if (CountItem(invs[i], wanted) > 0) found = true;
            _hasId = wanted;
            _hasResult = found;
            _hasUntil = Time.time + 0.5f;
            return found;
        }

        internal static bool TakeItem(int wanted, string wer)
        {
            List<object> invs = PlayerInventories();
            for (int i = 0; i < invs.Count; i++)
            {
                object inv = invs[i];
                int vorher = CountItem(inv, wanted);
                if (vorher <= 0) continue;

                MethodInfo remove = AccessTools.Method(inv.GetType(), "RemoveItem",
                    new Type[] { typeof(int), typeof(int) }, null);
                if (remove == null)
                {
                    RevivalPlugin.L.LogWarning(wer + ": PlayerInventoryManager."
                        + "RemoveItem(int,int) fehlt - Item " + wanted
                        + " kann nicht verbraucht werden.");
                    return false;
                }

                try { remove.Invoke(inv, new object[] { wanted, 1 }); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError(wer + ": RemoveItem(" + wanted
                        + ", 1) fehlgeschlagen: " + ex);
                    return false;
                }

                int nachher = CountItem(inv, wanted);
                if (nachher >= vorher)
                {
                    RevivalPlugin.L.LogWarning(wer + ": Item " + wanted
                        + " liegt noch " + nachher + " mal im Inventar - "
                        + "RemoveItem hat nichts weggenommen.");
                    return false;
                }
                RevivalPlugin.L.LogInfo(wer + ": Item " + wanted
                    + " verbraucht, " + nachher + " uebrig.");
                return true;
            }
            return false;
        }

        /// <summary>
        /// How often item `wanted` lies in one PlayerInventoryManager -
        /// backpack, vest and weapon slots, the same three containers
        /// RemoveItem walks. Slot counts come from the arrays themselves, not
        /// from the fixed 7 and 3 the game hardcodes.
        /// </summary>
        static int CountItem(object inv, int wanted)
        {
            return CountIn(Field(inv, "_backpackData"), wanted)
                 + CountIn(Field(inv, "_gearsData"), wanted)
                 + CountIn(Field(inv, "_weaponsData"), wanted);
        }

        static int CountIn(object data, int wanted)
        {
            if (data == null) return 0;
            Array ids = Field(data, "ItemID") as Array;
            if (ids == null) return 0;
            int n = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                object box = ids.GetValue(i);
                if (box != null && Obscured(box) == wanted) n++;
            }
            return n;
        }

        static bool _leerBerichtet;

        /// <summary>
        /// Schreibt EINMAL auf, was in den durchsuchten Behaeltern wirklich
        /// steht. Ohne das ist "keine Munition" nicht zu unterscheiden von
        /// "Behaelter nicht gefunden" oder "IDs nicht lesbar" - genau daran ist
        /// der erste Anlauf haengengeblieben.
        /// </summary>
        static void BerichteLeer(object trunk, List<object> invs)
        {
            if (_leerBerichtet) return;
            _leerBerichtet = true;
            RevivalPlugin.L.LogInfo("Geschuetz: keine Munition (Item "
                + MunitionsId() + ") gefunden.");
            RevivalPlugin.L.LogInfo("  Kofferraum: "
                + (trunk == null ? "nicht gefunden" : Inhalt(Field(trunk, "_containerData"))));
            RevivalPlugin.L.LogInfo("  eigene Inventare: " + invs.Count);
            for (int i = 0; i < invs.Count; i++)
            {
                RevivalPlugin.L.LogInfo("  #" + i + " Rucksack: "
                    + Inhalt(Field(invs[i], "_backpackData")));
                RevivalPlugin.L.LogInfo("  #" + i + " Weste:    "
                    + Inhalt(Field(invs[i], "_gearsData")));
            }
        }

        /// <summary>ItemID mal ItemBullets eines Datenblocks als Text.</summary>
        static string Inhalt(object data)
        {
            if (data == null) return "kein Datenblock";
            Array ids = Field(data, "ItemID") as Array;
            Array bullets = Field(data, "ItemBullets") as Array;
            if (ids == null) return "kein ItemID-Feld";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < ids.Length; i++)
            {
                object box = ids.GetValue(i);
                int id = box == null ? -1 : Obscured(box);
                if (id <= 0) continue;
                int b = 0;
                if (bullets != null && i < bullets.Length && bullets.GetValue(i) != null)
                    b = Obscured(bullets.GetValue(i));
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(id).Append("x").Append(b);
            }
            return sb.Length == 0 ? "leer (" + ids.Length + " Plaetze)" : sb.ToString();
        }

        /// <summary>
        /// Nimmt eine Patrone aus einem ContainerData. Kofferraum und Rucksack
        /// haben denselben Aufbau - Parallelarrays SlotID, ItemID, ItemBullets,
        /// alle drei mit ObscuredInt-Elementen.
        ///
        /// Der Rucksack ist bewusst als zweite Quelle dabei: der Kofferraum
        /// gehoert dem Fahrzeug, und ein Fahrzeug ist nach dem naechsten
        /// Spielstart weg - mitsamt der Munition, die darin lag.
        /// </summary>
        static bool TakeFrom(object owner, object data, string woher)
        {
            return TakeFrom(owner, data, woher, MunitionsId(), "Geschuetz");
        }

        /// <summary>
        /// Takes ONE ROUND out of a container: ItemBullets minus one, and the
        /// slot is cleared once it hits zero. Only the turret calls this - the
        /// drone used to, and that was the bug: an item that is consumed whole
        /// is not a stack of rounds. It now goes through TakeItem above.
        /// The wanted-id and caller parameters stay, they cost nothing and
        /// keep the log readable.
        /// </summary>
        internal static bool TakeFrom(object owner, object data, string woher,
                                      int wanted, string wer)
        {
            if (owner == null || data == null) return false;

            Array ids = Field(data, "ItemID") as Array;
            Array bullets = Field(data, "ItemBullets") as Array;
            Array slots = Field(data, "SlotID") as Array;
            if (ids == null || bullets == null) return false;

            for (int i = 0; i < ids.Length && i < bullets.Length; i++)
            {
                object idBox = ids.GetValue(i);
                if (idBox == null) continue;
                if (Obscured(idBox) != wanted) continue;

                object countBox = bullets.GetValue(i);
                int count = countBox == null ? 0 : Obscured(countBox);
                if (count <= 0) continue;

                count--;
                bullets.SetValue(MakeObscured(bullets.GetType().GetElementType(), count), i);
                if (count > 0)
                {
                    if (_ammoFrom != woher)
                    {
                        _ammoFrom = woher;
                        RevivalPlugin.L.LogInfo(wer + ": Nachschub aus dem " + woher + ".");
                    }
                    return true;
                }

                int slot = i;
                if (slots != null && i < slots.Length && slots.GetValue(i) != null)
                    slot = Obscured(slots.GetValue(i));
                ClearSlot(owner, woher, slot, wanted, wer);
                _ammoFrom = woher;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Empties one slot after its last round was used. Three containers,
        /// three different methods - and NONE of them is the one-int
        /// NetworkClearContainerSlot that this code called for all of them
        /// until 0.4.9. On a PlayerInventoryManager that lookup returned null,
        /// so an empty ammunition item kept sitting in the backpack with
        /// "0 rounds" (CONFIRMED, IL, signatures below).
        ///
        /// The onChangeInventory flag is set on purpose: it makes the game
        /// call OnChangedInventoryData(true), which refreshes the UI and the
        /// server-side copy. Without it the slot would only look empty until
        /// the next reload.
        /// </summary>
        static void ClearSlot(object owner, string woher, int slot, int wanted, string wer)
        {
            MethodInfo m;
            object[] args;

            if (woher == "Weste")
            {
                // ClearGearSlot(int SlotID, int ItemID, bool animate,
                //               bool onChangeInventory, bool onlyValuesClear)
                m = AccessTools.Method(owner.GetType(), "ClearGearSlot",
                    new Type[] { typeof(int), typeof(int), typeof(bool),
                                 typeof(bool), typeof(bool) }, null);
                args = new object[] { slot, wanted, false, true, false };
            }
            else if (woher == "Rucksack")
            {
                // ClearBackpackSlot(int SlotID, int ItemID, bool onChangeInventory)
                m = AccessTools.Method(owner.GetType(), "ClearBackpackSlot",
                    new Type[] { typeof(int), typeof(int), typeof(bool) }, null);
                args = new object[] { slot, wanted, true };
            }
            else
            {
                // The trunk is an ItemsContainer and has this one.
                m = AccessTools.Method(owner.GetType(), "NetworkClearContainerSlot",
                    new Type[] { typeof(int) }, null);
                args = new object[] { slot };
            }

            if (m == null)
            {
                RevivalPlugin.L.LogWarning(wer + ": Platz " + slot + " im " + woher
                    + " laesst sich nicht leeren - passende Methode an "
                    + owner.GetType().Name + " nicht gefunden.");
                return;
            }
            try { m.Invoke(owner, args); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError(wer + ": " + m.Name + " auf Platz " + slot
                    + " (" + woher + ") fehlgeschlagen: " + ex);
            }
        }

        /// <summary>
        /// Alle PlayerInventoryManager, die dem lokalen Spieler gehoeren.
        ///
        /// Bewusst eine Liste statt eines einzelnen Treffers: fremde Inventare
        /// bleiben ueber photonView.isMine draussen, aber unter den eigenen
        /// wird nicht mehr geraten, welches das richtige ist.
        /// </summary>
        internal static List<object> PlayerInventories()
        {
            List<object> found = new List<object>();
            Type t = RevivalPlugin.TypeByName("PlayerInventoryManager");
            if (t == null) return found;
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
            for (int i = 0; i < all.Length; i++)
            {
                MonoBehaviour mb = all[i] as MonoBehaviour;
                if (mb == null) continue;
                MethodInfo get = AccessTools.Method(mb.GetType(), "get_photonView", null, null);
                object view = null;
                try { if (get != null) view = get.Invoke(mb, null); }
                catch { view = null; }
                if (view != null)
                {
                    MethodInfo isMine = AccessTools.PropertyGetter(view.GetType(), "isMine");
                    try
                    {
                        if (isMine != null && !(bool)isMine.Invoke(view, null)) continue;
                    }
                    catch { }
                }
                found.Add(all[i]);
            }
            return found;
        }

        /// <summary>
        /// Sends the actual turret rotation, not the mouse target. Vehicle
        /// movement already belongs to Photon; only the child named `turret`
        /// is custom and therefore needs this small side channel.
        /// </summary>
        public static class Net
        {
            class Remote
            {
                public Transform Root;
                public Transform[] Turrets;
                public Quaternion From;
                public Quaternion To;
                public float T;
                public float Duration;
                public float Last;
            }

            static bool _hooked;
            static bool _failed;
            static MethodInfo _raise;
            static MethodInfo _getView;
            static MethodInfo _getViewId;
            static MethodInfo _findView;
            static Type _optType;
            static FieldInfo _onEventCall;
            static readonly Dictionary<int, int> _viewIds = new Dictionary<int, int>();
            static readonly Dictionary<int, float> _nextSend = new Dictionary<int, float>();
            static readonly Dictionary<int, Remote> _remote = new Dictionary<int, Remote>();
            static readonly List<int> _remove = new List<int>();

            public static void EnsureHooked()
            {
                if (_hooked || _failed) return;
                try
                {
                    int code = RevivalPlugin.CfgTurretEventCode.Value;
                    int drone = RevivalPlugin.CfgDroneEventCode.Value;
                    if (code < 0 || code > 199 || (code >= drone && code <= drone + 4)
                        || (RevivalPlugin.CfgAdminEventCode != null
                            && code == RevivalPlugin.CfgAdminEventCode.Value)
                        || (RevivalPlugin.CfgPatrolCrewDroneEventCode != null
                            && code == RevivalPlugin.CfgPatrolCrewDroneEventCode.Value))
                        throw new Exception("event code " + code
                            + " is outside 0..199 or overlaps another channel");

                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    Type viewType = RevivalPlugin.TypeByName("PhotonView");
                    Type ext = RevivalPlugin.TypeByName("Extensions");
                    if (photon == null || viewType == null || ext == null)
                        throw new Exception("PhotonNetwork, PhotonView or Extensions missing");

                    _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                    _onEventCall = AccessTools.Field(photon, "OnEventCall");
                    _optType = RevivalPlugin.TypeByName("RaiseEventOptions");
                    _getView = AccessTools.Method(ext, "GetPhotonView",
                        new Type[] { typeof(GameObject) }, null);
                    _getViewId = AccessTools.PropertyGetter(viewType, "viewID");
                    _findView = AccessTools.Method(viewType, "Find",
                        new Type[] { typeof(int) }, null);
                    if (_raise == null || _onEventCall == null || _getView == null
                        || _getViewId == null || _findView == null)
                        throw new Exception("turret network reflection path incomplete");

                    MethodInfo mine = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static);
                    Delegate handler = Delegate.CreateDelegate(_onEventCall.FieldType, mine);
                    Delegate current = _onEventCall.GetValue(null) as Delegate;
                    _onEventCall.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("Geschuetz-Netzwerk eingehaengt: Ereigniscode "
                        + code + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("Geschuetz-Netzwerk nicht eingehaengt: " + ex);
                }
            }

            internal static void Publish(Transform root, float yaw, float pitch)
            {
                if (!_hooked || root == null) return;
                try
                {
                    int viewId = ViewId(root);
                    if (viewId <= 0) return;
                    float next;
                    if (_nextSend.TryGetValue(viewId, out next) && Time.time < next) return;
                    _nextSend[viewId] = Time.time + 0.08f;

                    float[] data = new float[] { viewId, yaw, pitch };
                    object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                    _raise.Invoke(null, new object[] {
                        (byte)RevivalPlugin.CfgTurretEventCode.Value, data, false, opts });
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Geschuetz-Netzwerk senden: " + ex.Message);
                }
            }

            internal static void PublishWreck(Transform root, bool tank)
            {
                if (!_hooked || root == null) return;
                try
                {
                    int viewId = ViewId(root);
                    if (viewId <= 0) return;
                    Send(new float[] { -1f, viewId, tank ? 1f : 0f }, true);
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Wreck effect network send: "
                        + ex.Message);
                }
            }

            internal static void PublishShot(Vector3 point, bool tank)
            {
                if (!_hooked) return;
                try
                {
                    Send(new float[] { -2f, tank ? 1f : 0f,
                                       point.x, point.y, point.z }, tank);
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Vehicle shot sound network send: "
                        + ex.Message);
                }
            }

            static void Send(float[] data, bool reliable)
            {
                object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                _raise.Invoke(null, new object[] {
                    (byte)RevivalPlugin.CfgTurretEventCode.Value, data, reliable, opts });
            }

            static int ViewId(Transform root)
            {
                int instance = root.GetInstanceID();
                int id;
                if (_viewIds.TryGetValue(instance, out id)) return id;
                object view = _getView.Invoke(null, new object[] { root.gameObject });
                if (view == null) return -1;
                id = Convert.ToInt32(_getViewId.Invoke(view, null));
                if (id > 0) _viewIds[instance] = id;
                return id;
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                if (code != (byte)RevivalPlugin.CfgTurretEventCode.Value) return;
                try
                {
                    float[] data = content as float[];
                    if (data == null || data.Length < 3) return;
                    int action = Mathf.RoundToInt(data[0]);
                    if (action == -2 && data.Length >= 5)
                    {
                        VehicleShotSound.Play(new Vector3(data[2], data[3], data[4]),
                                              data[1] > 0.5f);
                        return;
                    }
                    int viewId = Mathf.RoundToInt(data[0]);
                    object view = _findView.Invoke(null, new object[] { viewId });
                    Component component = view as Component;
                    if (component == null) return;

                    Transform root = component.transform;
                    Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
                    if (vgsType != null)
                    {
                        Component vgs = component.gameObject.GetComponentInParent(vgsType);
                        if (vgs == null) vgs = component.gameObject.GetComponentInChildren(vgsType, true);
                        if (vgs != null) root = vgs.transform;
                    }

                    if (action == -1)
                    {
                        FireEffect.SpawnWreck(root.gameObject, data[2] > 0.5f);
                        return;
                    }

                    Transform[] turrets = FindTurrets(root);
                    if (turrets.Length == 0) return;

                    Remote state;
                    bool first = !_remote.TryGetValue(viewId, out state);
                    if (first)
                    {
                        state = new Remote();
                        _remote[viewId] = state;
                    }
                    state.Root = root;
                    state.Turrets = turrets;
                    state.From = turrets[0].localRotation;
                    state.To = LocalRotationFor(data[1], data[2]);
                    state.Duration = first ? 0.08f
                        : Mathf.Clamp(Time.time - state.Last, 0.02f, 0.25f);
                    state.Last = Time.time;
                    state.T = 0f;
                    if (first)
                        RevivalPlugin.L.LogInfo("Geschuetz-Netzwerk: Fahrzeug " + viewId
                            + " von Spieler " + sender + " wird synchronisiert.");
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Geschuetz-Netzwerk empfangen: " + ex.Message);
                }
            }

            public static void TickRemotes()
            {
                if (_remote.Count == 0) return;
                _remove.Clear();
                foreach (KeyValuePair<int, Remote> pair in _remote)
                {
                    Remote state = pair.Value;
                    if (state.Root == null || Time.time - state.Last > 3f)
                    {
                        _remove.Add(pair.Key);
                        continue;
                    }
                    if (_vehicleRoot == state.Root && _manning) continue;
                    state.T = Mathf.Min(1f, state.T
                        + Time.deltaTime / Mathf.Max(0.02f, state.Duration));
                    Quaternion rotation = Quaternion.Slerp(state.From, state.To, state.T);
                    for (int i = 0; i < state.Turrets.Length; i++)
                        if (state.Turrets[i] != null)
                            state.Turrets[i].localRotation = rotation;
                }
                for (int i = 0; i < _remove.Count; i++) _remote.Remove(_remove[i]);
            }
        }

        /// <summary>
        /// Der Kofferraum. Ueber die KOMPONENTE gesucht, nicht ueber den Pfad:
        /// "InteractColliders/BagaggeContainer" haengt am BTR nicht an der
        /// Wurzel, sondern unter Chassis (gemessen am Prefab BTR-80A_Spawn) -
        /// Transform.Find lieferte deshalb immer null, und der Kofferraum wurde
        /// nie durchsucht.
        /// </summary>
        static object TrunkContainer()
        {
            if (_vehicleRoot == null) return null;
            Type ic = RevivalPlugin.TypeByName("ItemsContainer");
            if (ic == null) return null;
            Component[] all = _vehicleRoot.GetComponentsInChildren(ic, true);
            return all.Length == 0 ? null : all[0];
        }

        // ------------------------------------------------------ Zielfernrohr

        public static void DrawScope()
        {
            // Der Hinweis gilt auch ausserhalb des Geschuetzes: die
            // Munitionsbeigabe beim Panzerspawn meldet sich, waehrend der
            // Spieler noch daneben steht.
            try { DrawHinweis(); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Hinweis: " + ex); }
            if (!_manning) return;
            try
            {
                // In beiden Fahrzeugen sieht man durch eine Optik, nicht ueber
                // vier Striche im freien Bild - seit 0.5.1 auch im BTR. Die
                // Bilder sind aber nicht dasselbe: der Panzer bekommt Winkel-
                // marke und Entfernungsskala fuer eine Granate, die faellt, das
                // BTR ein offenes Kreuz mit Vorhaltemarken fuer einen flachen
                // Schuss auf ein bewegliches Ziel. Beide bringen ihr Fadenkreuz
                // selbst mit - deshalb bleibt daneben keines stehen.
                Texture2D glas = Blende();
                if (glas != null) Vollbild(glas);
                else if (RevivalPlugin.CfgTurretScopeOverlay.Value) DrawOverlay();
                if (RevivalPlugin.CfgTurretCrosshair.Value && glas == null) DrawCrosshair();
                DrawLadeanzeige();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Geschuetzanzeige: " + ex);
            }
        }

        static void DrawOverlay()
        {
            if (!_scopeTried)
            {
                _scopeTried = true;
                _scope = Assets.Texture("scope50.png", false, true);
            }
            if (_scope == null) return;
            Vollbild(_scope);
        }

        /// <summary>
        /// Die Optik des Fahrzeugs, in dem gerade gesessen wird, oder null -
        /// abgeschaltet oder Bild fehlt. Null heisst: einfaches Fadenkreuz.
        /// </summary>
        static Texture2D Blende()
        {
            if (_tank)
                return RevivalPlugin.CfgTankScope.Value ? PanzerScope() : null;
            return RevivalPlugin.CfgTurretScope.Value ? ApcScope() : null;
        }

        static Texture2D ApcScope()
        {
            if (!_apcScopeTried)
            {
                _apcScopeTried = true;
                _apcScope = Assets.Texture("apc_scope.png", false, true);
                if (_apcScope == null)
                    RevivalPlugin.L.LogWarning("Geschuetz: apc_scope.png fehlt neben "
                        + "der DLL - es bleibt beim einfachen Fadenkreuz.");
            }
            return _apcScope;
        }

        static Texture2D PanzerScope()
        {
            if (!_tankScopeTried)
            {
                _tankScopeTried = true;
                _tankScope = Assets.Texture("t72_scope.png", false, true);
                if (_tankScope == null)
                    RevivalPlugin.L.LogWarning("Panzer: t72_scope.png fehlt neben "
                        + "der DLL - es bleibt beim einfachen Fadenkreuz.");
            }
            return _tankScope;
        }

        /// <summary>
        /// Quadratische Blende ueber das ganze Bild, wie ScaleMode.ScaleAndCrop:
        /// die Breite passt, oben und unten wird abgeschnitten. StretchToFill
        /// auf ein Rechteck wuerde den Linsenkreis zum Ei ziehen.
        /// </summary>
        static void Vollbild(Texture2D tex)
        {
            float side = Mathf.Max(Screen.width, Screen.height);
            Rect r = new Rect((Screen.width - side) * 0.5f,
                              (Screen.height - side) * 0.5f, side, side);
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
        }

        /// <summary>Einblendung ueber dem Fadenkreuz - "keine Munition" und aehnliches.</summary>
        static void DrawHinweis()
        {
            if (_hinweis == null || Time.time > _hinweisBis) return;
            float w = Mathf.Max(280f, Screen.width * 0.36f);
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.5f - Mathf.Max(60f, Screen.height * 0.10f);

            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x, y, w, 24f), Punkt());
            GUI.color = new Color(1f, 0.72f, 0.35f, 0.95f);
            GUI.Label(new Rect(x + 8f, y + 3f, w - 16f, 22f), _hinweis);
            GUI.color = old;
        }

        /// <summary>
        /// Fadenkreuz in der BILDMITTE.
        ///
        /// Bis 2026-08-28 wurde der Punkt, den das Rohr auf CrosshairRange
        /// erreicht, in den Bildschirm projiziert. Das war im Spiel unbrauchbar:
        /// das Fadenkreuz wanderte beim Schwenken aus der Mitte und stand
        /// zeitweise am Bildrand, weil Kamera und Rohrachse eben doch nicht
        /// dieselbe Achse sind. Jetzt umgekehrt herum gedacht - der Schuss
        /// laeuft auf der BLICKACHSE (siehe AimRay), damit ist die Bildmitte
        /// per Konstruktion der Treffpunkt und das Fadenkreuz steht still.
        ///
        /// Gezeichnet aus einer 1x1-Textur: das Spiel bringt kein Fadenkreuz
        /// mit, das zum Geschuetz passt, und eine eigene Textur waere fuer vier
        /// Striche zu viel Aufwand.
        /// </summary>
        static void DrawCrosshair()
        {
            Punkt();

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float gap = Mathf.Max(6f, Screen.height * 0.012f);
            float arm = Mathf.Max(14f, Screen.height * 0.030f);
            float th = 2f;

            Color old = GUI.color;

            // Erst ein dunkler Schatten, einen Pixel versetzt: sonst
            // verschwindet ein weisses Fadenkreuz vor hellem Himmel.
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            Bars(cx + 1f, cy + 1f, gap, arm, th);
            GUI.color = new Color(0.85f, 1f, 0.85f, 0.95f);
            Bars(cx, cy, gap, arm, th);

            // Mittelpunkt: der eigentliche Treffpunkt.
            GUI.color = new Color(1f, 0.35f, 0.2f, 0.95f);
            GUI.DrawTexture(new Rect(cx - 1.5f, cy - 1.5f, 3f, 3f), _dot);

            // Entfernungsstriche unter der Mitte, alle 25 Bildpunkte einer.
            GUI.color = new Color(0.85f, 1f, 0.85f, 0.55f);
            for (int i = 1; i <= 3; i++)
            {
                float y = cy + arm + gap + i * Mathf.Max(10f, Screen.height * 0.022f);
                float w = 10f - i * 2f;
                GUI.DrawTexture(new Rect(cx - w, y, w * 2f, 1.5f), _dot);
            }

            GUI.color = old;
        }

        /// <summary>Die 1x1-Textur, aus der alle Anzeigen gezeichnet werden.</summary>
        static Texture2D Punkt()
        {
            if (_dot == null)
            {
                _dot = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _dot.SetPixel(0, 0, Color.white);
                _dot.Apply();
                _dot.hideFlags = HideFlags.HideAndDontSave;
            }
            return _dot;
        }

        /// <summary>
        /// Ladebalken unter dem Fadenkreuz, solange nachgeladen wird.
        ///
        /// Er erscheint erst ab zwei Sekunden Ladezeit: beim BTR mit 0,9 s
        /// waere er ein Flackern und im Weg. Beim Panzer ist er dagegen kein
        /// Schmuck, sondern notwendig - zwoelf Sekunden lang passiert auf
        /// Mausklick nichts, und ohne Rueckmeldung haelt der Spieler das
        /// Geschuetz fuer kaputt.
        /// </summary>
        static void DrawLadeanzeige()
        {
            float ladezeit = Ladezeit();
            if (ladezeit < 2f) return;
            float rest = _nextShot - Time.time;
            if (rest <= 0f || rest > ladezeit) return;

            float w = Mathf.Max(160f, Screen.width * 0.16f);
            float h = Mathf.Max(5f, Screen.height * 0.007f);
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.5f + Mathf.Max(44f, Screen.height * 0.08f);
            float voll = 1f - Mathf.Clamp01(rest / ladezeit);

            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x - 1f, y - 1f, w + 2f, h + 2f), Punkt());
            GUI.color = new Color(0.10f, 0.10f, 0.09f, 0.80f);
            GUI.DrawTexture(new Rect(x, y, w, h), Punkt());
            GUI.color = new Color(0.85f, 0.55f, 0.15f, 0.95f);
            GUI.DrawTexture(new Rect(x, y, w * voll, h), Punkt());
            GUI.color = new Color(0.88f, 1f, 0.88f, 0.90f);
            GUI.Label(new Rect(x, y + h + 3f, w, 22f),
                      "Laedt " + rest.ToString("0.0") + " s");
            GUI.color = old;
        }

        static void Bars(float cx, float cy, float gap, float arm, float th)
        {
            GUI.DrawTexture(new Rect(cx - gap - arm, cy - th * 0.5f, arm, th), _dot);
            GUI.DrawTexture(new Rect(cx + gap, cy - th * 0.5f, arm, th), _dot);
            GUI.DrawTexture(new Rect(cx - th * 0.5f, cy - gap - arm, th, arm), _dot);
            GUI.DrawTexture(new Rect(cx - th * 0.5f, cy + gap, th, arm), _dot);
        }

        // ------------------------------------------------------------- Helfer

        static bool IsBtr(Transform root)
        {
            if (root == null) return false;
            return root.name.StartsWith(BtrPrefix, StringComparison.OrdinalIgnoreCase);
        }

        static int GunnerIndexOf(Transform seatPoints)
        {
            for (int i = 0; i < seatPoints.childCount; i++)
                if (seatPoints.GetChild(i).name == SeatName) return i;
            return -1;
        }

        static KeyCode ManKey()
        {
            if (_keyParsed) return _manKey;
            _keyParsed = true;
            try
            {
                _manKey = (KeyCode)Enum.Parse(typeof(KeyCode),
                                              RevivalPlugin.CfgTurretKey.Value, true);
            }
            catch
            {
                _manKey = KeyCode.G;
                RevivalPlugin.L.LogWarning("Geschuetz: TurretKey "
                    + RevivalPlugin.CfgTurretKey.Value + " unbekannt, benutze G.");
            }
            return _manKey;
        }

        static object Field(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo f = AccessTools.Field(instance.GetType(), name);
            return f == null ? null : f.GetValue(instance);
        }

        static int IntField(object instance, string name)
        {
            object v = Field(instance, name);
            return v is int ? (int)v : -1;
        }

        /// <summary>ObscuredInt zu int ueber den impliziten Operator.</summary>
        static int Obscured(object value)
        {
            if (value is int) return (int)value;
            Type t = value.GetType();
            MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != typeof(int)) continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == t)
                    return (int)ms[i].Invoke(null, new object[] { value });
            }
            return -1;
        }

        static object MakeObscured(Type t, int value)
        {
            if (t == typeof(int)) return value;
            MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != t) continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
                    return ms[i].Invoke(null, new object[] { value });
            }
            return value;
        }
    }

    // ----------------------------------------------------------- Testflaeche

    /// <summary>
    /// Legt zur Laufzeit eine kleine ebene Testflaeche vor den Spieler und
    /// setzt ihn darauf. Gedacht als Ort, an dem sich etwas ausprobieren
    /// laesst, ohne die Welt anzufassen.
    ///
    /// WARUM DAS KEINE ECHTE REGION IST - und keine sein kann
    /// ------------------------------------------------------
    /// Eine Region des Spiels ist ein GameRegionData mit den Feldern region,
    /// startScene und scenes; die Szenen sind Buildindizes. Eine neue Region
    /// braucht also entweder
    ///
    ///   a) eine neue Szene im Build - die laesst sich ohne Neubau des Spiels
    ///      nicht anlegen, oder
    ///   b) ein zurueckgeschriebenes resources.assets - das ist in
    ///      docs/ai/TASKS.md unter NEXT als offene Voraussetzung vermerkt und
    ///      bis heute UNKNOWN.
    ///
    /// Was ohne beides geht, ist genau das hier: Geometrie zur Laufzeit, in
    /// der bereits geladenen Szene. Der Szenensprung in die zehn ungenutzten
    /// Buildszenen (Research.Jump, Bunker_A65, GW_Scene_2, Underground_Lab)
    /// ist der andere Weg zu "neuem" Gelaende und schon vorhanden.
    ///
    /// WARUM HIER KEINE GEGNER STEHEN
    /// ------------------------------
    /// Belegt aus NPC_Settlement::InitSpawnNpc: das Spiel erzeugt einen NPC
    /// mit PhotonNetwork.InstantiateSceneObject unter dem Pfad
    /// "NPCSpawn\Marauder_NPC_01" und ruft danach der Reihe nach
    /// SetCustomization, SetMaxHealth, ResetHealth, SetBehaviorPattern,
    /// CalculateMaxEnemiesCount, SetGodMode, SetIsSafeSettlement,
    /// SetMainWeaponId, NPC_SpawnPoint::Init, NPC_AI2::InitSpawnPoint und
    /// SetSpawnData. Die Daten dafuer kommen aus einer Customization-Datenbank
    /// und einer Waffentabelle.
    ///
    /// Diese Kette halb nachzubauen ergibt Gegner ohne Waffe, ohne Leben und
    /// ohne Verhalten - und InstantiateSceneObject setzt ausserdem voraus,
    /// dass man Masterclient ist. Dazu kommt: NPC_AI2 haelt einen
    /// NavMeshAgent, und ein Navigationsnetz laesst sich zur Laufzeit nicht
    /// backen. Deshalb liegt die Flaeche bewusst nur wenige Zentimeter ueber
    /// dem Boden - dann traegt das vorhandene Navigationsnetz darunter noch.
    ///
    /// Der vollstaendige Bauplan steht in
    /// docs/ai/tasks/testregion-arena.md. Ausprobiert wird er in einem Zug,
    /// wenn das Spiel ohnehin laeuft.
    /// </summary>
    public static class Arena
    {
        const string RootName = "NDR_TestArena";
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static GameObject _arena;

        public static void Tick()
        {
            if (!RevivalPlugin.CfgArena.Value) return;
            try
            {
                if (!Input.GetKeyDown(Key())) return;
                if (_arena != null)
                {
                    UnityEngine.Object.Destroy(_arena);
                    _arena = null;
                    RevivalPlugin.L.LogInfo("Testflaeche entfernt.");
                    return;
                }
                Build();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Testflaeche: " + ex);
            }
        }

        static void Build()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                RevivalPlugin.L.LogWarning("Testflaeche: keine Kamera gefunden.");
                return;
            }

            Vector3 eye = cam.transform.position;
            Vector3 ahead = cam.transform.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.000001f) ahead = Vector3.forward;
            ahead.Normalize();

            float distance = RevivalPlugin.CfgArenaDistance.Value;
            Vector3 above = eye + ahead * distance + Vector3.up * 60f;

            Vector3 ground;
            GameObject under = Turret.RaycastObject(above, Vector3.down, 400f, out ground);
            if (under == null)
            {
                RevivalPlugin.L.LogWarning("Testflaeche: unter " + above
                    + " ist kein Boden - naeher an festen Grund stellen.");
                return;
            }

            string quelle;
            Material material = GroundMaterial(under, out quelle);
            if (material == null)
            {
                // Lieber nichts bauen als etwas Magentafarbenes hinstellen: ein
                // Renderer ohne Material sieht im Spiel nach kaputtem Modell aus
                // und schickt die Fehlersuche in die falsche Richtung.
                RevivalPlugin.L.LogError("Testflaeche: kein brauchbares Material "
                    + "gefunden, die Flaeche wird nicht gebaut. Ohne Material "
                    + "zeichnet Unity sie magenta.");
                return;
            }

            float size = Mathf.Max(8f, RevivalPlugin.CfgArenaSize.Value);

            _arena = new GameObject(RootName);
            _arena.transform.position = ground + Vector3.up * 0.06f;

            GameObject floor = new GameObject("Flaeche");
            floor.transform.SetParent(_arena.transform, false);
            MeshFilter mf = floor.AddComponent<MeshFilter>();
            mf.sharedMesh = Grid(size, 16);
            MeshRenderer mr = floor.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;

            Posts(size, material);

            RevivalPlugin.L.LogInfo("Testflaeche gebaut: " + size + " x " + size
                + " Einheiten bei " + _arena.transform.position
                + ", Material \"" + material.name + "\" aus Quelle: " + quelle + ".");
            RevivalPlugin.L.LogInfo("Testflaeche: Gegner stehen hier absichtlich "
                + "keine - Begruendung in docs/ai/tasks/testregion-arena.md.");
        }

        /// <summary>
        /// Besorgt ein Material, das garantiert zeichnet.
        ///
        /// Der erste Anlauf am 2026-08-28 nahm nur den Renderer des getroffenen
        /// Bodens. In der Overworld steht der Spieler aber auf Unity-Terrain,
        /// und Terrain zeichnet ueber die Komponente `Terrain`, nicht ueber
        /// einen `MeshRenderer` - der Griff ging ins Leere, das Material blieb
        /// null, und Unity malt einen Renderer ohne Material magenta. Genau die
        /// pinke Flaeche, die im Spiel zu sehen war.
        ///
        /// Deshalb jetzt vier Quellen der Reihe nach. Die letzte traegt immer,
        /// solange das Spiel ueberhaupt etwas zeichnet.
        /// </summary>
        static Material GroundMaterial(GameObject under, out string quelle)
        {
            quelle = "keine";

            string terrainQuelle;
            Material vorlage = TerrainMaterial(out terrainQuelle);
            if (vorlage != null) quelle = terrainQuelle;

            if (vorlage == null)
            {
                Renderer r = under.GetComponent<Renderer>();
                if (r == null) r = under.GetComponentInParent<Renderer>();
                if (r != null && r.sharedMaterial != null)
                {
                    vorlage = r.sharedMaterial;
                    quelle = "Boden unter dem Spieler";
                }
            }

            if (vorlage == null)
            {
                Renderer r = NearbyRenderer(under.transform.position);
                if (r != null)
                {
                    vorlage = r.sharedMaterial;
                    quelle = "naechster Renderer: " + r.gameObject.name;
                }
            }

            if (vorlage != null)
            {
                Material copy = new Material(vorlage);
                copy.name = "NDR_ArenaGround";
                return copy;
            }

            // Letzter Ausweg: eigenes Material auf einem Shader, den das Spiel
            // selbst benutzt. Dieselbe Kette wie in ItemFactory.MakeMaterial.
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) shader = Shader.Find("Diffuse");
            if (shader == null) return null;

            Material eigen = new Material(shader);
            eigen.name = "NDR_ArenaGround";
            if (eigen.HasProperty("_Color"))
                eigen.color = new Color(0.42f, 0.40f, 0.36f);
            quelle = "eigener Shader " + shader.name;
            return eigen;
        }

        /// <summary>
        /// Terrain.activeTerrain.materialTemplate ueber Reflexion.
        ///
        /// `Terrain` liegt in UnityEngine.TerrainModule, und build.ps1
        /// referenziert die Assembly nicht. Ueber AccessTools zu gehen ist
        /// derselbe Weg, den das Plugin fuer alle Spieltypen nimmt, und spart
        /// einen Verweis, der auf einem anderen Rechner fehlen koennte.
        ///
        /// `materialTemplate` ist im Spiel LEER - gemessen am 2026-08-28: das
        /// Gelaende benutzt das eingebaute Standardmaterial, und das gibt Unity
        /// nur bei materialType == Custom heraus. Deshalb zweiter Griff auf die
        /// **Splat-Textur** des Gelaendes: das ist die Textur, die man beim
        /// Spielen tatsaechlich unter den Fuessen sieht.
        ///
        /// Ohne diesen zweiten Griff fiel die Flaeche auf "naechster Renderer"
        /// zurueck und trug die Rinde von "dead_trunk_01_LOD0" - nicht mehr
        /// magenta, aber Boden aus Baumstamm.
        /// </summary>
        static Material TerrainMaterial(out string quelle)
        {
            quelle = null;
            try
            {
                Type t = RevivalPlugin.TypeByName("UnityEngine.Terrain");
                if (t == null) return null;
                MethodInfo aktiv = AccessTools.PropertyGetter(t, "activeTerrain");
                if (aktiv == null) return null;
                object terrain = aktiv.Invoke(null, null);
                if (terrain == null) return null;

                MethodInfo mat = AccessTools.PropertyGetter(t, "materialTemplate");
                if (mat != null)
                {
                    Material vorlage = mat.Invoke(terrain, null) as Material;
                    if (vorlage != null)
                    {
                        quelle = "Terrain.materialTemplate";
                        return vorlage;
                    }
                }

                Material ausSplat = SplatMaterial(t, terrain);
                if (ausSplat != null)
                {
                    quelle = "Terrain-Splattextur";
                    return ausSplat;
                }
                return null;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Testflaeche: Terrain-Material nicht "
                    + "lesbar (" + ex.Message + ")");
                return null;
            }
        }

        /// <summary>
        /// Erste Splat-Textur des Gelaendes auf einem Standard-Shader.
        ///
        /// Unity 2018.1 fuehrt sie als `TerrainData.splatPrototypes`, ein Feld
        /// von `SplatPrototype` mit `texture` und `tileSize`. Ab 2018.3 heisst
        /// dasselbe `terrainLayers`/`diffuseTexture` - beide Namen werden
        /// probiert, damit ein Spielupdate das hier nicht stumm ausknipst.
        /// </summary>
        static Material SplatMaterial(Type terrainTyp, object terrain)
        {
            MethodInfo daten = AccessTools.PropertyGetter(terrainTyp, "terrainData");
            if (daten == null) return null;
            object td = daten.Invoke(terrain, null);
            if (td == null) return null;

            object[] schichten = null;
            string[] namen = new string[] { "splatPrototypes", "terrainLayers" };
            for (int i = 0; i < namen.Length && schichten == null; i++)
            {
                MethodInfo g = AccessTools.PropertyGetter(td.GetType(), namen[i]);
                if (g == null) continue;
                schichten = g.Invoke(td, null) as object[];
            }
            if (schichten == null || schichten.Length == 0) return null;

            Texture textur = null;
            for (int i = 0; i < schichten.Length && textur == null; i++)
            {
                if (schichten[i] == null) continue;
                string[] felder = new string[] { "texture", "diffuseTexture" };
                for (int k = 0; k < felder.Length && textur == null; k++)
                {
                    MethodInfo g = AccessTools.PropertyGetter(schichten[i].GetType(), felder[k]);
                    if (g == null) continue;
                    textur = g.Invoke(schichten[i], null) as Texture;
                }
            }
            if (textur == null) return null;

            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) return null;

            Material m = new Material(shader);
            m.mainTexture = textur;
            return m;
        }

        /// <summary>Naechstgelegener Renderer mit brauchbarem Material.</summary>
        static Renderer NearbyRenderer(Vector3 nahe)
        {
            MeshRenderer[] alle = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
            Renderer beste = null;
            float abstand = float.MaxValue;
            for (int i = 0; i < alle.Length; i++)
            {
                MeshRenderer r = alle[i];
                if (r == null || !r.enabled) continue;
                if (r.sharedMaterial == null || r.sharedMaterial.shader == null) continue;
                float d = (r.transform.position - nahe).sqrMagnitude;
                if (d < abstand) { abstand = d; beste = r; }
            }
            return beste;
        }

        /// <summary>Ebenes Gitter, damit die Beleuchtung nicht auf zwei Dreiecke faellt.</summary>
        static Mesh Grid(float size, int cells)
        {
            int line = cells + 1;
            Vector3[] verts = new Vector3[line * line];
            Vector2[] uvs = new Vector2[line * line];
            Vector3[] normals = new Vector3[line * line];
            float half = size * 0.5f;
            float step = size / cells;

            for (int z = 0; z < line; z++)
            {
                for (int x = 0; x < line; x++)
                {
                    int i = z * line + x;
                    verts[i] = new Vector3(-half + x * step, 0f, -half + z * step);
                    // Eine Kachel je zwei Meter, damit die Bodentextur nicht
                    // ueber die ganze Flaeche gezogen wird.
                    uvs[i] = new Vector2(verts[i].x * 0.5f, verts[i].z * 0.5f);
                    normals[i] = Vector3.up;
                }
            }

            int[] tris = new int[cells * cells * 6];
            int t = 0;
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int i = z * line + x;
                    tris[t++] = i;
                    tris[t++] = i + line;
                    tris[t++] = i + line + 1;
                    tris[t++] = i;
                    tris[t++] = i + line + 1;
                    tris[t++] = i + 1;
                }
            }

            Mesh mesh = new Mesh();
            mesh.name = "NDR_ArenaFloor";
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Vier Ecken markieren, sonst findet man die Flaeche im Gelaende
        /// nicht wieder. Bewusst Wuerfel aus demselben Mesh statt
        /// GameObject.CreatePrimitive - das haengt einen Collider an, und der
        /// wuerde dem Navigationsnetz darunter im Weg stehen.
        /// </summary>
        static void Posts(float size, Material boden)
        {
            float half = size * 0.5f;
            Mesh post = Grid(1.2f, 1);

            // Eigenes Material, sonst faerbt das Abdunkeln auch die Flaeche -
            // und vor allem: ein MeshRenderer ohne Material ist magenta. Der
            // erste Anlauf hatte hier gar keins gesetzt.
            Material mark = new Material(boden);
            mark.name = "NDR_ArenaMarker";
            if (mark.HasProperty("_Color"))
                mark.color = mark.color * 0.45f;

            for (int i = 0; i < 4; i++)
            {
                float sx = (i == 0 || i == 3) ? -1f : 1f;
                float sz = (i < 2) ? -1f : 1f;
                GameObject marker = new GameObject("Ecke" + i);
                marker.transform.SetParent(_arena.transform, false);
                marker.transform.localPosition =
                    new Vector3(sx * half, 1.4f, sz * half);
                MeshFilter mf = marker.AddComponent<MeshFilter>();
                mf.sharedMesh = post;
                MeshRenderer mr = marker.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mark;
            }
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try
            {
                _key = (KeyCode)Enum.Parse(typeof(KeyCode),
                                           RevivalPlugin.CfgArenaKey.Value, true);
            }
            catch
            {
                _key = KeyCode.F10;
                RevivalPlugin.L.LogWarning("Testflaeche: ArenaKey "
                    + RevivalPlugin.CfgArenaKey.Value + " unbekannt, benutze F10.");
            }
            return _key;
        }
    }

    /// <summary>
    /// A procedural spatial report for both vehicle guns. There is no donor
    /// AudioClip in the runtime-built weapon, so the same deterministic clip
    /// is generated on every client and only the tiny shot event is sent.
    /// </summary>
    public static class VehicleShotSound
    {
        static AudioClip _tank;
        static AudioClip _btr;

        public static void Play(Vector3 point, bool tank)
        {
            if (RevivalPlugin.CfgTurretSound == null
                || !RevivalPlugin.CfgTurretSound.Value) return;
            try
            {
                GameObject go = new GameObject(tank
                    ? "NDR Tank Shot Sound" : "NDR BTR Shot Sound");
                go.transform.position = point;
                AudioSource source = go.AddComponent<AudioSource>();
                source.clip = Clip(tank);
                source.loop = false;
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.dopplerLevel = 0f;
                source.minDistance = tank ? 30f : 10f;
                source.maxDistance = RevivalPlugin.CfgTurretSoundRange == null
                    ? 650f : Mathf.Max(50f,
                        RevivalPlugin.CfgTurretSoundRange.Value);
                source.volume = RevivalPlugin.CfgTurretSoundVolume == null
                    ? 1f : Mathf.Clamp01(
                        RevivalPlugin.CfgTurretSoundVolume.Value);
                source.Play();
                UnityEngine.Object.Destroy(go, source.clip.length + 1f);
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Vehicle shot sound: " + ex.Message);
            }
        }

        static AudioClip Clip(bool tank)
        {
            if (tank && _tank != null) return _tank;
            if (!tank && _btr != null) return _btr;

            const int rate = 44100;
            float seconds = tank ? 3.4f : 0.48f;
            float[] data = new float[Mathf.RoundToInt(rate * seconds)];
            int seed = tank ? 125 : 30;
            float filteredNoise = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float t = (float)i / rate;
                seed = seed * 1103515245 + 12345;
                float noise = (((seed >> 16) & 0x7fff) / 16383.5f) - 1f;
                filteredNoise = filteredNoise * (tank ? 0.965f : 0.90f)
                    + noise * (tank ? 0.035f : 0.10f);
                if (tank)
                {
                    float body = (Mathf.Sin(2f * Mathf.PI * 54f * t) * 0.90f
                        + Mathf.Sin(2f * Mathf.PI * 82f * t) * 0.48f
                        + Mathf.Sin(2f * Mathf.PI * 117f * t) * 0.24f)
                        * Mathf.Exp(-t * 1.75f);
                    float blast = filteredNoise * 3.2f * Mathf.Exp(-t * 2.4f);
                    float crack = noise * 1.7f * Mathf.Exp(-t * 75f);
                    float echo1 = t > 0.24f
                        ? (Mathf.Sin(2f * Mathf.PI * 47f * (t - 0.24f)) * 0.62f
                           + filteredNoise * 0.75f)
                          * Mathf.Exp(-(t - 0.24f) * 2.3f) : 0f;
                    float echo2 = t > 0.66f
                        ? (Mathf.Sin(2f * Mathf.PI * 39f * (t - 0.66f)) * 0.42f
                           + filteredNoise * 0.45f)
                          * Mathf.Exp(-(t - 0.66f) * 1.8f) : 0f;
                    data[i] = Mathf.Clamp(body * 1.15f + blast + crack
                        + echo1 + echo2, -1f, 1f);
                }
                else
                {
                    float envelope = Mathf.Exp(-t * 10f);
                    float body = Mathf.Sin(2f * Mathf.PI * 82f * t) * 0.60f;
                    float crack = Mathf.Exp(-t * 45f) * noise;
                    float echo = t > 0.09f
                        ? Mathf.Sin(2f * Mathf.PI * 57f * t)
                          * Mathf.Exp(-(t - 0.09f) * 4f) : 0f;
                    data[i] = Mathf.Clamp((body + noise * 0.24f) * envelope
                        + crack * 0.85f + echo * 0.18f, -1f, 1f);
                }
            }
            AudioClip clip = AudioClip.Create(tank ? "NDR_TankShot"
                : "NDR_BTRShot", data.Length, 1, rate, false);
            clip.SetData(data, 0);
            if (tank) _tank = clip; else _btr = clip;
            return clip;
        }
    }

    /// <summary>
    /// Small reflection bridge to the game's NGUI map. The map already owns
    /// the authoritative world size, texture size and UI camera; using those
    /// values keeps route lines and clicks correct while the map is zoomed or
    /// moved and avoids a second, guessed coordinate system.
    /// </summary>
    public static class MapTools
    {
        public static bool Context(out Component manager, out Component texture,
                                   out Camera uiCamera, out Vector2 worldSize,
                                   out Vector2 mapSize)
        {
            manager = null;
            texture = null;
            uiCamera = null;
            worldSize = Vector2.zero;
            mapSize = Vector2.zero;
            try
            {
                Type mapType = RevivalPlugin.TypeByName("MapUIManager");
                if (mapType == null) return false;
                MethodInfo get = AccessTools.PropertyGetter(mapType, "Inst");
                object raw = get == null ? null : get.Invoke(null, null);
                if (raw == null)
                {
                    FieldInfo instance = AccessTools.Field(mapType, "_instance");
                    if (instance != null) raw = instance.GetValue(null);
                }
                manager = raw as Component;
                if (manager == null || manager.gameObject == null
                    || !manager.gameObject.activeInHierarchy) return false;

                FieldInfo panelField = AccessTools.Field(mapType, "MapPanel");
                object panelRaw = panelField == null ? null : panelField.GetValue(manager);
                Component panel = panelRaw as Component;
                GameObject panelGo = panelRaw as GameObject;
                if (panel != null && !panel.gameObject.activeInHierarchy) return false;
                if (panelGo != null && !panelGo.activeInHierarchy) return false;

                FieldInfo textureField = AccessTools.Field(mapType, "MapTextureUI");
                texture = textureField == null ? null
                    : textureField.GetValue(manager) as Component;
                if (texture == null || !texture.gameObject.activeInHierarchy) return false;

                FieldInfo world = AccessTools.Field(mapType, "WORLD_SIZE");
                FieldInfo map = AccessTools.Field(mapType, "MAP_SIZE");
                if (world == null || map == null) return false;
                worldSize = (Vector2)world.GetValue(null);
                mapSize = (Vector2)map.GetValue(null);
                if (worldSize.x <= 0f || worldSize.y <= 0f
                    || mapSize.x <= 0f || mapSize.y <= 0f) return false;

                Type uiType = RevivalPlugin.TypeByName("UIController");
                if (uiType != null)
                {
                    MethodInfo uiGet = AccessTools.PropertyGetter(uiType, "Instance");
                    object ui = uiGet == null ? null : uiGet.Invoke(null, null);
                    FieldInfo general = AccessTools.Field(uiType, "_UI_General");
                    if (ui == null || general == null
                        || Convert.ToInt32(general.GetValue(ui)) != 8) return false;
                    FieldInfo cam = AccessTools.Field(uiType, "UICam");
                    if (ui != null && cam != null) uiCamera = cam.GetValue(ui) as Camera;
                }
                return uiCamera != null;
            }
            catch { return false; }
        }

        public static bool WorldToGui(Vector3 point, out Vector2 gui)
        {
            gui = Vector2.zero;
            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            if (!Context(out manager, out texture, out cam, out world, out map))
                return false;
            return WorldToGui(point, texture, cam, world, map, out gui);
        }

        public static bool WorldToGui(Vector3 point, Component texture, Camera cam,
                                      Vector2 world, Vector2 map, out Vector2 gui)
        {
            gui = Vector2.zero;
            if (texture == null || cam == null || world.x <= 0f || world.y <= 0f)
                return false;
            Vector3 local = new Vector3(point.x / world.x * map.x,
                                        point.z / world.y * map.y, 0f);
            Vector3 screen = cam.WorldToScreenPoint(texture.transform.TransformPoint(local));
            if (screen.z < 0f) return false;
            gui = new Vector2(screen.x, Screen.height - screen.y);
            return true;
        }

        /// <summary>
        /// The map texture's rectangle in GUI coordinates. Everything drawn
        /// over the map must be clipped to this - a route waypoint north of the
        /// visible map still projects to a screen point, and without this rect
        /// its dashes would be painted over the 3D scene above the map panel
        /// (the "civ" line leaking into the sky). Built from the same NGUI
        /// widget bounds that MouseWorld uses, so it is exactly the drawn map.
        /// </summary>
        public static bool MapScreenRect(Component texture, Camera cam,
                                         out Rect rect)
        {
            rect = new Rect();
            try
            {
                if (texture == null || cam == null) return false;
                Type math = RevivalPlugin.TypeByName("NGUIMath");
                MethodInfo boundsMethod = math == null ? null
                    : AccessTools.Method(math, "CalculateAbsoluteWidgetBounds",
                        new Type[] { typeof(Transform) }, null);
                if (boundsMethod == null) return false;
                Bounds b = (Bounds)boundsMethod.Invoke(null,
                    new object[] { texture.transform });
                if (b.size == Vector3.zero) return false;
                Vector3 s0 = cam.WorldToScreenPoint(b.min);
                Vector3 s1 = cam.WorldToScreenPoint(b.max);
                float x0 = Mathf.Min(s0.x, s1.x);
                float x1 = Mathf.Max(s0.x, s1.x);
                float g0 = Screen.height - s0.y;
                float g1 = Screen.height - s1.y;
                float y0 = Mathf.Min(g0, g1);
                float y1 = Mathf.Max(g0, g1);
                rect = new Rect(x0, y0, x1 - x0, y1 - y0);
                return rect.width > 1f && rect.height > 1f;
            }
            catch { return false; }
        }

        /// <summary>The same click conversion used by
        /// MapUIManager.PlacePlayerCustomMarker, including its terrain ray.</summary>
        public static bool MouseWorld(out Vector3 point)
        {
            point = Vector3.zero;
            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            if (!Context(out manager, out texture, out cam, out world, out map))
                return false;
            try
            {
                Type math = RevivalPlugin.TypeByName("NGUIMath");
                MethodInfo boundsMethod = math == null ? null
                    : AccessTools.Method(math, "CalculateAbsoluteWidgetBounds",
                        new Type[] { typeof(Transform) }, null);
                if (boundsMethod == null) return false;
                Bounds bounds = (Bounds)boundsMethod.Invoke(null,
                    new object[] { texture.transform });
                if (bounds.size == Vector3.zero) return false;

                Vector3 mouse = Input.mousePosition;
                mouse.z = 10f;
                Vector3 inUi = cam.ScreenToWorldPoint(mouse);
                float nx = (inUi.x - bounds.min.x) / bounds.size.x;
                float ny = (inUi.y - bounds.min.y) / bounds.size.y;
                if (nx < 0f || nx > 1f || ny < 0f || ny > 1f) return false;

                Vector3 above = new Vector3((nx - 0.5f) * world.x, 1000f,
                                            (ny - 0.5f) * world.y);
                Vector3 hit;
                if (Turret.RaycastObject(above, Vector3.down, 2500f, out hit) == null)
                    return false;
                point = hit + Vector3.up * 1f;
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Map click conversion: " + ex.Message);
                return false;
            }
        }

        public static GameObject LocalPlayer()
        {
            try
            {
                Type ngsType = RevivalPlugin.TypeByName("NetworkGameServer");
                if (ngsType != null)
                {
                    MethodInfo get = AccessTools.PropertyGetter(ngsType, "Instance");
                    object ngs = get == null ? null : get.Invoke(null, null);
                    FieldInfo local = AccessTools.Field(ngsType, "localPlayer");
                    GameObject go = ngs == null || local == null ? null
                        : local.GetValue(ngs) as GameObject;
                    if (go != null) return go;
                }

                Type movement = RevivalPlugin.TypeByName("PlayerMovementController");
                if (movement == null) return null;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(movement);
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i] as MonoBehaviour;
                    if (mb != null && IsMine(mb)) return mb.gameObject;
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Local player lookup: " + ex.Message);
            }
            return null;
        }

        static bool IsMine(MonoBehaviour mb)
        {
            MethodInfo get = AccessTools.Method(mb.GetType(), "get_photonView", null, null);
            object view = get == null ? null : get.Invoke(mb, null);
            if (view == null) return true;
            MethodInfo mine = AccessTools.PropertyGetter(view.GetType(), "isMine");
            return mine == null || (bool)mine.Invoke(view, null);
        }

        public static bool TeleportLocal(Vector3 point, out string message)
        {
            GameObject player = LocalPlayer();
            if (player == null)
            {
                message = "local player not found";
                return false;
            }
            try
            {
                Type controllerType = RevivalPlugin.TypeByName("CharacterController");
                Component[] controllers = controllerType == null
                    ? new Component[0]
                    : player.GetComponentsInChildren(controllerType, true);
                bool[] enabled = new bool[controllers.Length];
                for (int i = 0; i < controllers.Length; i++)
                {
                    PropertyInfo property = AccessTools.Property(
                        controllers[i].GetType(), "enabled");
                    enabled[i] = property != null
                        && (bool)property.GetValue(controllers[i], null);
                    if (property != null) property.SetValue(controllers[i], false, null);
                }
                player.transform.position = point;
                Type bodyType = RevivalPlugin.TypeByName("Rigidbody");
                Component body = bodyType == null ? null : player.GetComponent(bodyType);
                if (body != null)
                {
                    SetVector(body, "position", point);
                    SetVector(body, "velocity", Vector3.zero);
                    SetVector(body, "angularVelocity", Vector3.zero);
                }
                for (int i = 0; i < controllers.Length; i++)
                {
                    PropertyInfo property = AccessTools.Property(
                        controllers[i].GetType(), "enabled");
                    if (property != null)
                        property.SetValue(controllers[i], enabled[i], null);
                }
                message = "teleported to " + point;
                RevivalPlugin.L.LogInfo("Admin teleport: local player -> " + point + ".");
                return true;
            }
            catch (Exception ex)
            {
                message = "teleport failed: " + ex.Message;
                return false;
            }
        }

        static void SetVector(Component component, string name, Vector3 value)
        {
            PropertyInfo property = AccessTools.Property(component.GetType(), name);
            if (property != null && property.CanWrite)
                property.SetValue(component, value, null);
        }
    }

    /// <summary>
    /// Kleines Menue im Spiel: Items geben, Werkzeuge an- und ausschalten.
    ///
    /// Der Grund ist nicht Bequemlichkeit, sondern Zeit. Bisher fuellte
    /// `invtool.py` das Inventar - und das geht nur bei geschlossenem Spiel,
    /// kostet also je Versuch einen Neustart. Wer eine Waffe dreimal
    /// hintereinander in der Hand sehen will, startet dreimal.
    ///
    /// Gegeben wird ueber `PlayerInventoryManager::AddBackpackItemFromValues` -
    /// dieselbe Methode, die das Spiel beim Laden des Profils benutzt. Ihre
    /// Argumentliste ist am 2026-08-28 aus dem eigenen Diagnoseprotokoll
    /// abgelesen worden:
    ///
    ///     AddBackpackItemFromValues(2051, 0, 0, 0, 0, 0, 5, 0, False)
    ///     AddBackpackItemFromValues(2050, 0, 0, 0, 0, 0, 200, 0, False)
    ///
    /// Argument 0 ist die Item-Id, Argument 6 die Menge. IL shows that the
    /// final bool controls OnChangedInventoryData: true refreshes the UI and
    /// sends the recalculated inventory data. The method silently returns
    /// without adding anything when no backpack slot is free, so both facts
    /// have to be checked here instead of reporting success unconditionally.
    /// </summary>
    public static class Admin
    {
        const int FensterId = 0x4E445241;

        static bool _offen;
        static bool _fokusLoesen;
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static Rect _fenster = new Rect(40f, 40f, 560f, 0f);
        static Vector2 _rollen;
        static string _menge = "";
        static string _status = "Bereit.";
        static bool _sessionGranted;
        static bool _godMode;
        static bool _teleportArmed;
        static int _targetActor = -1;
        static float _nextPlayers;

        class PlayerRow
        {
            public int Actor;
            public string Name;
            public bool Mine;
        }

        static readonly List<PlayerRow> _players = new List<PlayerRow>();

        public static bool IsOpen { get { return _offen; } }

        // -1 noch nicht geprueft, 0 nein, 1 ja. Einmal entschieden bleibt es
        // so: die Steam-Id aendert sich waehrend einer Sitzung nicht.
        static int _zutritt = -1;

        // Wer das Menue oeffnen darf. Es setzt Panzer, Drohne und Items in die
        // Welt - das gehoert nicht in jede Hand, die das Paket herunterlaedt.
        //
        // Das ist KEIN Schutz im Sinne von Sicherheit. Die Liste steht in einer
        // Konfigurationsdatei auf dem Rechner des Spielers, und wer sie aendert,
        // aendert sie. Es haelt das Menue von Haenden fern, die es nicht suchen.
        // Was wirklich zaehlt, prueft ohnehin der Server gegen weapons_db.xml.
        static bool Zutritt()
        {
            if (_sessionGranted) return true;
            if (_zutritt >= 0) return _zutritt == 1;
            _zutritt = 0;
            try
            {
                string liste = RevivalPlugin.CfgAdminIds.Value;
                if (liste == null) liste = "";
                liste = liste.Trim();
                if (liste.Length == 0) { _zutritt = 1; return true; }

                string ich = SteamId();
                if (ich == null)
                {
                    RevivalPlugin.L.LogInfo(
                        "Adminmenue: eigene Steam-Id nicht lesbar, Menue bleibt zu.");
                    return false;
                }
                string[] teile = liste.Split(',');
                for (int i = 0; i < teile.Length; i++)
                {
                    if (teile[i].Trim() == ich) { _zutritt = 1; break; }
                }
                RevivalPlugin.L.LogInfo("Adminmenue: Steam-Id " + ich +
                    (_zutritt == 1 ? " steht auf der Liste." : " steht nicht auf der Liste."));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Adminmenue Zutritt: " + ex.Message);
            }
            return _zutritt == 1;
        }

        internal static bool HasAccess { get { return Zutritt(); } }

        public static void Install(Harmony harmony)
        {
            Net.EnsureHooked();
            try
            {
                Type life = RevivalPlugin.TypeByName("PlayerLifeDataManager");
                MethodInfo canDamage = life == null ? null
                    : AccessTools.Method(life, "CanApplyDamage", null, null);
                MethodInfo godPostfix = typeof(Admin).GetMethod("DamageAllowedPostfix",
                    BindingFlags.Public | BindingFlags.Static);
                if (canDamage == null || godPostfix == null)
                    RevivalPlugin.L.LogWarning("Admin god mode: damage gate not found.");
                else
                {
                    harmony.Patch(canDamage, null, new HarmonyMethod(godPostfix),
                                  null, null, null);
                    RevivalPlugin.L.LogInfo("Admin god mode attached to the local damage gate.");
                }

                Type click = RevivalPlugin.TypeByName("MapClickHandler");
                MethodInfo onClick = click == null ? null
                    : AccessTools.Method(click, "OnClick", null, null);
                MethodInfo postfix = typeof(Admin).GetMethod("MapClickPostfix",
                    BindingFlags.Public | BindingFlags.Static);
                if (onClick == null || postfix == null)
                {
                    RevivalPlugin.L.LogWarning("Admin map teleport: MapClickHandler.OnClick "
                        + "not found.");
                    return;
                }
                harmony.Patch(onClick, null, new HarmonyMethod(postfix), null, null, null);
                RevivalPlugin.L.LogInfo("Admin map teleport attached to map clicks.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Admin map teleport hook: " + ex);
            }
        }

        public static void DamageAllowedPostfix(ref bool __result)
        {
            if (_godMode) __result = false;
        }

        public static void MapClickPostfix()
        {
            if (!_teleportArmed || !Zutritt()) return;
            Vector3 point;
            if (!MapTools.MouseWorld(out point))
            {
                Melde("map click could not be converted to a world position");
                return;
            }
            _teleportArmed = false;
            string message;
            Net.Teleport(_targetActor, point, out message);
            Melde(message);
        }

        // SteamInterface::GetSteamID ist der Umweg des Spiels ueber
        // Steamworks.SteamUser (liegt in Assembly-CSharp-firstpass, nicht in
        // Assembly-CSharp). Zurueck kommt ein CSteamID; die Zahl steht in
        // dessen Feld m_SteamID - belegt mit ildasm gegen beide Assemblies.
        static string SteamId()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("SteamInterface");
                if (t == null) return null;
                MethodInfo m = AccessTools.Method(t, "GetSteamID", null, null);
                if (m == null) return null;
                object id = m.Invoke(null, null);
                if (id == null) return null;
                FieldInfo f = AccessTools.Field(id.GetType(), "m_SteamID");
                if (f == null) return null;
                object roh = f.GetValue(id);
                if (roh == null) return null;
                string s = roh.ToString();
                return s == "0" ? null : s;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Steam-Id nicht lesbar: " + ex.Message);
                return null;
            }
        }

        public static void Tick()
        {
            Net.EnsureHooked();
            if (Time.time >= _nextPlayers)
            {
                _nextPlayers = Time.time + 1f;
                RefreshPlayers();
            }
            if (!RevivalPlugin.CfgAdmin.Value || !Zutritt()) return;
            try
            {
                if (!Input.GetKeyDown(Key())) return;
                _offen = !_offen;
                if (!_offen)
                {
                    _fokusLoesen = true;
                    CursorZurueck();
                }
                RevivalPlugin.L.LogInfo("Adminmenue " + (_offen ? "auf" : "zu") + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Adminmenue: " + ex);
            }
        }

        static void CursorZurueck()
        {
            if (!CursorTracker.SawCall) return;
            CursorTracker.Restoring = true;
            try
            {
                Cursor.lockState = CursorTracker.DesiredLock;
                Cursor.visible = CursorTracker.DesiredVisible;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Adminmenue Cursor zurueck: " + ex.Message);
            }
            finally { CursorTracker.Restoring = false; }
        }

        public static void Draw()
        {
            // Vermutung: Das Mengenfeld haelt sonst den IMGUI-Tastaturfokus.
            if (_fokusLoesen)
            {
                _fokusLoesen = false;
                GUIUtility.keyboardControl = 0;
                GUIUtility.hotControl = 0;
            }
            if (!_offen || !RevivalPlugin.CfgAdmin.Value || !Zutritt()) return;
            // Der Cursor gehoert waehrenddessen dem Menue. CursorGuard wird in
            // RevivalPlugin.Update ausgesetzt, solange offen ist. Restoring
            // verhindert, dass diese Zugriffe als Spielwunsch gespeichert werden.
            CursorTracker.Restoring = true;
            try
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            finally { CursorTracker.Restoring = false; }
            _fenster = GUILayout.Window(FensterId, _fenster, Inhalt,
                                        "Revival - Admin");
        }

        static void Inhalt(int id)
        {
            GUILayout.Label("Target player");
            GUILayout.BeginHorizontal();
            if (_players.Count == 0) GUILayout.Label("No player in the world yet.");
            for (int i = 0; i < _players.Count; i++)
            {
                PlayerRow p = _players[i];
                string text = (p.Mine ? "me: " : "") + p.Name;
                if (GUILayout.Toggle(_targetActor == p.Actor, text, GUI.skin.button,
                                     GUILayout.Width(125f)))
                    _targetActor = p.Actor;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("grant admin", GUILayout.Width(120f)))
            {
                string message;
                Net.Grant(_targetActor, out message);
                Melde(message);
            }
            if (GUILayout.Button("teleport on map", GUILayout.Width(145f)))
            {
                if (_targetActor < 0) Melde("select a player first");
                else
                {
                    _teleportArmed = true;
                    _offen = false;
                    CursorZurueck();
                    Melde("open the map and click the destination");
                }
            }
            if (_teleportArmed && GUILayout.Button("cancel teleport", GUILayout.Width(125f)))
            {
                _teleportArmed = false;
                Melde("map teleport cancelled");
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("god mode ON", GUILayout.Width(120f)))
            {
                string message;
                Net.GodMode(_targetActor, true, out message);
                Melde(message);
            }
            if (GUILayout.Button("god mode OFF", GUILayout.Width(120f)))
            {
                string message;
                Net.GodMode(_targetActor, false, out message);
                Melde(message);
            }
            GUILayout.Label(_targetActor == Net.OwnActor()
                ? (_godMode ? "local: protected" : "local: vulnerable")
                : "applies to selected player");
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label("Complete loadout");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("full loadout", GUILayout.Width(150f)))
            {
                string message;
                Net.Loadout(_targetActor, false, out message);
                Melde(message);
            }
            if (GUILayout.Button("full loadout UKB armor", GUILayout.Width(190f)))
            {
                string message;
                Net.Loadout(_targetActor, true, out message);
                Melde(message);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label("Items in den Rucksack legen");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Menge (leer = Standard):", GUILayout.Width(170f));
            _menge = GUILayout.TextField(_menge, 6, GUILayout.Width(60f));
            GUILayout.EndHorizontal();

            _rollen = GUILayout.BeginScrollView(_rollen, GUILayout.Height(175f));
            List<ItemDef> items = RevivalPlugin.Items;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDef d = items[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(d.Id + "  " + d.Name, GUILayout.Width(250f));
                if (GUILayout.Button("geben", GUILayout.Width(90f)))
                    Geben(d);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            GUILayout.Label("Werkzeuge");
            RevivalPlugin.CfgTurret.Value =
                GUILayout.Toggle(RevivalPlugin.CfgTurret.Value,
                                 "Geschuetz (Taste " + RevivalPlugin.CfgTurretKey.Value + ")");
            RevivalPlugin.CfgArena.Value =
                GUILayout.Toggle(RevivalPlugin.CfgArena.Value,
                                 "Testflaeche (Taste " + RevivalPlugin.CfgArenaKey.Value + ")");
            RevivalPlugin.CfgSpawnCar.Value =
                GUILayout.Toggle(RevivalPlugin.CfgSpawnCar.Value,
                                 "Fahrzeugspawn (Taste " + RevivalPlugin.CfgSpawnCarKey.Value + ")");
            RevivalPlugin.CfgTank.Value =
                GUILayout.Toggle(RevivalPlugin.CfgTank.Value,
                                 "Panzer T-72 (Taste " + RevivalPlugin.CfgTankKey.Value + ")");
            RevivalPlugin.CfgSceneJump.Value =
                GUILayout.Toggle(RevivalPlugin.CfgSceneJump.Value,
                                 "Szenensprung (Taste " + RevivalPlugin.CfgJumpKey.Value + ")");

            GUILayout.Space(6f);
            GUILayout.Label(_status);
            GUILayout.Label("Alles hier landet auch im BepInEx-Log. "
                            + "Auswerten mit: python playlog.py");

            if (GUILayout.Button("schliessen")) _offen = false;
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        static void Geben(ItemDef d)
        {
            int menge = d.Bullets > 0 ? d.Bullets : 1;
            if (_menge.Length > 0)
            {
                int gewuenscht;
                if (int.TryParse(_menge, out gewuenscht) && gewuenscht > 0)
                    menge = gewuenscht;
            }
            string meldung;
            Net.Item(_targetActor, d.Id, menge, out meldung);
            Melde(meldung);
        }

        /// <summary>
        /// Ein Item in den Rucksack legen. Seit 0.4.9 nicht mehr nur fuer das
        /// Menue: der Panzerspawn legt hierueber seine Granaten dazu, damit
        /// nicht wieder jemand vor einem Panzer steht, der nicht schiesst.
        /// </summary>
        internal static bool GibItem(int id, int menge, out string meldung)
        {
            try
            {
                object pim = InventarManager();
                if (pim == null)
                {
                    meldung = "PlayerInventoryManager nicht gefunden - im Hauptmenue "
                              + "gibt es keinen. Erst ins Spiel gehen.";
                    return false;
                }

                MethodInfo m = null;
                MethodInfo[] alle = pim.GetType().GetMethods();
                for (int i = 0; i < alle.Length; i++)
                    if (alle[i].Name == "AddBackpackItemFromValues") { m = alle[i]; break; }
                if (m == null)
                {
                    meldung = "AddBackpackItemFromValues fehlt - Spielversion anders?";
                    return false;
                }

                int freieVorher = FreeBackpackSlots(pim);
                if (freieVorher < 0)
                {
                    meldung = "Rucksackdaten nicht lesbar - nichts wurde gegeben.";
                    return false;
                }
                if (freieVorher == 0)
                {
                    meldung = "Rucksack voll - kein freier Platz fuer " + id + ".";
                    return false;
                }

                ParameterInfo[] ps = m.GetParameters();
                object[] args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    if (pt == typeof(bool)) args[i] = false;
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null;
                }
                args[0] = id;
                // Argument 6 ist in allen beobachteten Aufrufen die Menge. Hat
                // die Methode weniger Argumente, wird NICHT geraten.
                bool mengeGesetzt = ps.Length > 6 && ps[6].ParameterType == typeof(int);
                if (mengeGesetzt) args[6] = menge;
                // IL: the last bool is onChangeInventory. Without true the
                // item data changes, but UI and server copy stay stale.
                bool aktualisiert = ps.Length > 0
                    && ps[ps.Length - 1].ParameterType == typeof(bool);
                if (aktualisiert) args[ps.Length - 1] = true;

                m.Invoke(pim, args);
                int freieNachher = FreeBackpackSlots(pim);
                if (freieNachher >= freieVorher)
                {
                    meldung = "nicht gegeben: " + id + " wurde vom Inventar abgewiesen.";
                    return false;
                }
                meldung = "gegeben: " + id + " x" + menge
                          + (mengeGesetzt ? "" : " (Argument 6 ist kein int - "
                                                 + "Menge nicht gesetzt)")
                          + (aktualisiert ? "" : " (Inventar-Refresh fehlt)");
                return true;
            }
            catch (Exception ex)
            {
                meldung = "fehlgeschlagen: " + ex.Message;
                RevivalPlugin.L.LogError("Item geben: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Pick only a manager owned by this client. FindObjectOfType returned
        /// an arbitrary player's manager as soon as a second player joined.
        /// </summary>
        static object InventarManager()
        {
            Type t = RevivalPlugin.TypeByName("PlayerInventoryManager");
            if (t == null) return null;
            List<object> own = Turret.PlayerInventories();
            if (own.Count == 0) return null;

            string[] namen = new string[] { "current", "Instance", "instance" };
            for (int i = 0; i < namen.Length; i++)
            {
                MethodInfo g = AccessTools.PropertyGetter(t, namen[i]);
                if (g == null || !g.IsStatic) continue;
                object o = g.Invoke(null, null);
                if (o == null) continue;
                for (int j = 0; j < own.Count; j++)
                    if (System.Object.ReferenceEquals(o, own[j])) return o;
            }
            return own[0];
        }

        static int FreeBackpackSlots(object pim)
        {
            if (pim == null) return -1;
            FieldInfo f = AccessTools.Field(pim.GetType(), "_backpackData");
            object data = f == null ? null : f.GetValue(pim);
            if (data == null) return -1;
            FieldInfo idsField = AccessTools.Field(data.GetType(), "ItemID");
            Array ids = idsField == null ? null : idsField.GetValue(data) as Array;
            if (ids == null) return -1;
            int free = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                object value = ids.GetValue(i);
                if (value == null || ReadInt(value) == 0) free++;
            }
            return free;
        }

        static int ReadInt(object value)
        {
            if (value == null) return 0;
            if (value is int) return (int)value;
            Type t = value.GetType();
            MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "op_Implicit"
                    || methods[i].ReturnType != typeof(int)) continue;
                ParameterInfo[] ps = methods[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == t)
                    return (int)methods[i].Invoke(null, new object[] { value });
            }
            return -1;
        }

        static float ReadFloat(object value)
        {
            if (value == null) return 0f;
            if (value is float) return (float)value;
            if (value is double) return (float)(double)value;
            Type t = value.GetType();
            MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "op_Implicit"
                    || methods[i].ReturnType != typeof(float)) continue;
                ParameterInfo[] ps = methods[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == t)
                    return (float)methods[i].Invoke(null, new object[] { value });
            }
            try { return Convert.ToSingle(value); }
            catch { return 0f; }
        }

        static object Field(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo f = AccessTools.Field(instance.GetType(), name);
            return f == null ? null : f.GetValue(instance);
        }

        static object ArrayValue(object instance, string name, int index)
        {
            Array values = Field(instance, name) as Array;
            if (values == null || index < 0 || index >= values.Length) return null;
            return values.GetValue(index);
        }

        static object[] DefaultArgs(MethodInfo method)
        {
            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                if (t == typeof(bool)) args[i] = false;
                else if (t.IsValueType) args[i] = Activator.CreateInstance(t);
                else args[i] = null;
            }
            return args;
        }

        static MethodInfo NamedMethod(object instance, string name, int parameterCount)
        {
            if (instance == null) return null;
            MethodInfo[] all = instance.GetType().GetMethods(BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < all.Length; i++)
                if (all[i].Name == name
                    && all[i].GetParameters().Length == parameterCount) return all[i];
            return null;
        }

        static void RefreshPlayers()
        {
            _players.Clear();
            try
            {
                Type ngsType = RevivalPlugin.TypeByName("NetworkGameServer");
                MethodInfo get = ngsType == null ? null
                    : AccessTools.PropertyGetter(ngsType, "Instance");
                object ngs = get == null ? null : get.Invoke(null, null);
                FieldInfo field = ngsType == null ? null
                    : AccessTools.Field(ngsType, "NetworkPlayers");
                IEnumerable rows = ngs == null || field == null ? null
                    : field.GetValue(ngs) as IEnumerable;
                if (rows == null) return;

                int own = Net.OwnActor();
                foreach (object raw in rows)
                {
                    GameObject go = raw as GameObject;
                    if (go == null) continue;
                    object photonPlayer;
                    int actor = ActorFor(go, out photonPlayer);
                    if (actor <= 0) continue;
                    PlayerRow row = new PlayerRow();
                    row.Actor = actor;
                    row.Mine = actor == own;
                    row.Name = PlayerName(go, photonPlayer, actor);
                    _players.Add(row);
                }
                bool targetExists = false;
                for (int i = 0; i < _players.Count; i++)
                    if (_players[i].Actor == _targetActor) targetExists = true;
                if (!targetExists)
                    _targetActor = own > 0 ? own
                        : (_players.Count == 0 ? -1 : _players[0].Actor);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Admin player list: " + ex.Message);
            }
        }

        static int ActorFor(GameObject go, out object photonPlayer)
        {
            photonPlayer = null;
            Type pncType = RevivalPlugin.TypeByName("PlayerNetworkController");
            if (pncType == null || go == null) return -1;
            Component pnc = go.GetComponentInChildren(pncType, true);
            if (pnc == null) pnc = go.GetComponentInParent(pncType);
            if (pnc == null) return -1;
            MethodInfo get = AccessTools.PropertyGetter(pncType, "GetPhotonPlayer");
            if (get == null) return -1;
            photonPlayer = get.Invoke(pnc, null);
            return PhotonActor(photonPlayer);
        }

        static int PhotonActor(object player)
        {
            if (player == null) return -1;
            string[] names = new string[] { "ID", "ActorNumber", "ActorNr" };
            for (int i = 0; i < names.Length; i++)
            {
                MethodInfo get = AccessTools.PropertyGetter(player.GetType(), names[i]);
                if (get == null) continue;
                try { return Convert.ToInt32(get.Invoke(player, null)); }
                catch { }
            }
            return -1;
        }

        static string PlayerName(GameObject go, object player, int actor)
        {
            if (player != null)
            {
                string[] names = new string[] { "NickName", "name", "Name" };
                for (int i = 0; i < names.Length; i++)
                {
                    MethodInfo get = AccessTools.PropertyGetter(player.GetType(), names[i]);
                    if (get == null) continue;
                    try
                    {
                        object value = get.Invoke(player, null);
                        if (value != null && value.ToString().Length > 0)
                            return value.ToString();
                    }
                    catch { }
                }
            }
            return (go == null ? "player" : go.name) + " #" + actor;
        }

        class GearDef
        {
            public int Slot, Id;
            public float Energy, Regenerate;
            public GearDef(int slot, int id, float energy, float regenerate)
            { Slot = slot; Id = id; Energy = energy; Regenerate = regenerate; }
        }

        class BackpackItem
        {
            public int Id, Bullets, Clip;
            public float Food, Water, Energy, Regenerate, Condition;
        }

        static readonly GearDef[] FullGear = new GearDef[] {
            new GearDef(0, 4128, 0f, 0f), new GearDef(1, 4710, 50f, 0f),
            new GearDef(2, 4323, 0f, 50f), new GearDef(4, 4603, 0f, 0f),
            new GearDef(5, 4205, 50f, 0f), new GearDef(6, 4517, 0f, 8f) };

        static readonly GearDef[] UkbGear = new GearDef[] {
            new GearDef(0, 4017, 45f, 48.5f), new GearDef(1, 4710, 50f, 0f),
            new GearDef(2, 4316, 30f, 48.5f), new GearDef(4, 4603, 0f, 0f),
            new GearDef(5, 4205, 50f, 0f), new GearDef(6, 4509, 25f, 40f) };

        static bool ApplyLoadout(bool ukb, out string message)
        {
            object pim = InventarManager();
            if (pim == null)
            {
                message = "inventory not found - enter the world first";
                return false;
            }
            try
            {
                int backpack = ukb ? 6019 : 6028;
                int capacity = ukb ? 22 : 40;
                List<BackpackItem> saved = SnapshotBackpack(pim);
                if (saved.Count > capacity)
                {
                    message = "current backpack has " + saved.Count + " items, but "
                        + backpack + " has only " + capacity + " slots; nothing changed";
                    return false;
                }
                if (!EquipBackpack(pim, backpack, saved, out message)) return false;

                GearDef[] gear = ukb ? UkbGear : FullGear;
                for (int i = 0; i < gear.Length; i++) EquipGear(pim, gear[i]);

                EquipWeapon(pim, 0, 1160, 200, 2050);
                EquipWeapon(pim, 1, 1161, 5, 2051);
                EquipWeapon(pim, 2, 1162, 1, 0);

                int[,] supplies = new int[,] {
                    {2050,200},{2050,200},{2051,10},{2051,10},{2051,10},{2051,10},
                    {2053,1},{2053,1},{2053,1},{2053,1},{2053,1},
                    {2052,1},{2052,1},{1163,1},{1163,1},{1163,1},
                    {2054,1},{7001,1},{7001,1},{7001,1} };
                int added = 0;
                for (int i = 0; i < supplies.GetLength(0); i++)
                {
                    string one;
                    if (GibItem(supplies[i, 0], supplies[i, 1], out one)) added++;
                    else break;
                }
                message = (ukb ? "full UKB loadout" : "full loadout")
                    + " equipped; " + added + " supply slots added";
                RevivalPlugin.L.LogInfo("Admin: " + message + ".");
                return true;
            }
            catch (Exception ex)
            {
                message = "loadout failed: " + ex.Message;
                RevivalPlugin.L.LogError("Admin loadout: " + ex);
                return false;
            }
        }

        static List<BackpackItem> SnapshotBackpack(object pim)
        {
            List<BackpackItem> result = new List<BackpackItem>();
            object data = Field(pim, "_backpackData");
            Array ids = Field(data, "ItemID") as Array;
            if (ids == null) return result;
            for (int i = 0; i < ids.Length; i++)
            {
                int item = ReadInt(ids.GetValue(i));
                if (item <= 0) continue;
                BackpackItem value = new BackpackItem();
                value.Id = item;
                value.Food = ReadFloat(ArrayValue(data, "ItemFood", i));
                value.Water = ReadFloat(ArrayValue(data, "ItemWater", i));
                value.Energy = ReadFloat(ArrayValue(data, "ItemEnergy", i));
                value.Regenerate = ReadFloat(ArrayValue(data, "ItemRegenerate", i));
                value.Condition = ReadFloat(ArrayValue(data, "ItemCondition", i));
                value.Bullets = ReadInt(ArrayValue(data, "ItemBullets", i));
                value.Clip = ReadInt(ArrayValue(data, "ClipItemID", i));
                result.Add(value);
            }
            return result;
        }

        static bool EquipBackpack(object pim, int wanted,
                                  List<BackpackItem> saved, out string message)
        {
            object data = Field(pim, "_backpackData");
            int current = ReadInt(Field(data, "BackpackID"));
            if (current == wanted)
            {
                message = "backpack already equipped";
                return true;
            }

            if (current > 0) ClearSlot(pim, "ClearGearSlot", 3, current);
            FieldInfo backpackId = data == null ? null
                : AccessTools.Field(data.GetType(), "BackpackID");
            if (backpackId == null)
            {
                message = "BackpackID field not found";
                return false;
            }
            backpackId.SetValue(data, MakeNumber(backpackId.FieldType, 0));

            MethodInfo give = NamedMethod(pim, "GiveItem", 3);
            if (give == null || give.GetParameters().Length != 3)
            {
                message = "GiveItem signature changed";
                return false;
            }
            object[] args = DefaultArgs(give);
            args[0] = wanted;
            Type sourceType = give.GetParameters()[1].ParameterType;
            args[1] = sourceType.IsEnum ? Enum.ToObject(sourceType, 2)
                                        : Convert.ChangeType(2, sourceType);
            args[2] = true;
            give.Invoke(pim, args);

            data = Field(pim, "_backpackData");
            if (ReadInt(Field(data, "BackpackID")) != wanted)
            {
                message = "backpack " + wanted + " was refused";
                return false;
            }
            for (int i = 0; i < saved.Count; i++) AddBackpackValues(pim, saved[i]);
            message = "backpack " + wanted + " equipped and " + saved.Count
                + " existing item(s) restored";
            return true;
        }

        static void AddBackpackValues(object pim, BackpackItem value)
        {
            MethodInfo add = NamedMethod(pim, "AddBackpackItemFromValues", 9);
            if (add == null)
                throw new MissingMethodException("AddBackpackItemFromValues signature changed");
            object[] args = DefaultArgs(add);
            args[0] = value.Id;
            args[1] = value.Food;
            args[2] = value.Water;
            args[3] = value.Energy;
            args[4] = value.Regenerate;
            args[5] = value.Condition;
            args[6] = value.Bullets;
            args[7] = value.Clip;
            args[args.Length - 1] = true;
            add.Invoke(pim, args);
        }

        static void EquipGear(object pim, GearDef gear)
        {
            object data = Field(pim, "_gearsData");
            int current = ReadInt(ArrayValue(data, "ItemID", gear.Slot));
            if (current > 0 && current != gear.Id)
                ClearSlot(pim, "ClearGearSlot", gear.Slot, current);
            if (current == gear.Id) return;

            MethodInfo add = NamedMethod(pim, "AddGearItemFromValues", 7);
            if (add == null)
                throw new MissingMethodException("AddGearItemFromValues signature changed");
            object[] args = DefaultArgs(add);
            args[0] = gear.Slot;
            args[1] = gear.Id;
            args[2] = gear.Energy;
            args[3] = gear.Regenerate;
            args[4] = 0f;
            args[5] = 0f;
            args[args.Length - 1] = true;
            add.Invoke(pim, args);
            data = Field(pim, "_gearsData");
            if (ReadInt(ArrayValue(data, "ItemID", gear.Slot)) != gear.Id)
                throw new InvalidOperationException("gear " + gear.Id
                    + " was refused in slot " + gear.Slot);
        }

        static void EquipWeapon(object pim, int slot, int item, int bullets, int clip)
        {
            object data = Field(pim, "_weaponsData");
            int current = ReadInt(ArrayValue(data, "ItemID", slot));
            if (current > 0) ClearSlot(pim, "ClearWeaponSlot", slot, current);

            MethodInfo add = NamedMethod(pim, "AddWeaponItemFromValues", 6);
            if (add == null)
                throw new MissingMethodException("AddWeaponItemFromValues signature changed");
            object[] args = DefaultArgs(add);
            args[0] = slot;
            args[1] = item;
            args[2] = bullets;
            args[3] = clip;
            args[4] = 0f;
            args[args.Length - 1] = true;
            add.Invoke(pim, args);
            data = Field(pim, "_weaponsData");
            if (ReadInt(ArrayValue(data, "ItemID", slot)) != item)
                throw new InvalidOperationException("weapon " + item
                    + " was refused in slot " + slot);
        }

        static void ClearSlot(object pim, string methodName, int slot, int item)
        {
            int count = methodName == "ClearGearSlot" ? 5 : 4;
            MethodInfo clear = NamedMethod(pim, methodName, count);
            if (clear == null) throw new MissingMethodException(methodName);
            object[] args = DefaultArgs(clear);
            if (args.Length < 2) throw new MissingMethodException(methodName + " signature");
            args[0] = slot;
            args[1] = item;
            ParameterInfo[] ps = clear.GetParameters();
            for (int i = 2; i < ps.Length; i++)
            {
                if (ps[i].ParameterType != typeof(bool)) continue;
                string name = ps[i].Name == null ? "" : ps[i].Name.ToLowerInvariant();
                args[i] = name.IndexOf("onchange") >= 0;
            }
            clear.Invoke(pim, args);
        }

        static object MakeNumber(Type type, int value)
        {
            if (type == typeof(int)) return value;
            MethodInfo[] all = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Name != "op_Implicit" || all[i].ReturnType != type) continue;
                ParameterInfo[] ps = all[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
                    return all[i].Invoke(null, new object[] { value });
            }
            return Convert.ChangeType(value, type);
        }

        public static class Net
        {
            const int GrantAction = 1;
            const int ItemAction = 2;
            const int LoadoutAction = 3;
            const int TeleportAction = 4;
            const int GodModeAction = 5;

            static bool _hooked, _failed;
            static MethodInfo _raise;
            static Type _optionsType;
            static FieldInfo _onEvent;

            public static void EnsureHooked()
            {
                if (_hooked || _failed || RevivalPlugin.CfgAdminEventCode == null) return;
                try
                {
                    int code = RevivalPlugin.CfgAdminEventCode.Value;
                    int drone = RevivalPlugin.CfgDroneEventCode.Value;
                    if (code < 0 || code > 199
                        || (code >= drone && code <= drone + 4)
                        || code == RevivalPlugin.CfgTurretEventCode.Value
                        || (RevivalPlugin.CfgPatrolCrewDroneEventCode != null
                            && code == RevivalPlugin.CfgPatrolCrewDroneEventCode.Value))
                        throw new Exception("event code " + code + " overlaps another channel");

                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null) throw new Exception("PhotonNetwork missing");
                    _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                    _onEvent = AccessTools.Field(photon, "OnEventCall");
                    _optionsType = RevivalPlugin.TypeByName("RaiseEventOptions");
                    if (_raise == null || _onEvent == null)
                        throw new Exception("Photon event reflection path incomplete");
                    MethodInfo own = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static);
                    Delegate handler = Delegate.CreateDelegate(_onEvent.FieldType, own);
                    Delegate current = _onEvent.GetValue(null) as Delegate;
                    _onEvent.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("Admin network attached: event " + code + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("Admin network not attached: " + ex);
                }
            }

            internal static int OwnActor()
            {
                try
                {
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null) return -1;
                    MethodInfo get = AccessTools.PropertyGetter(photon, "player");
                    if (get == null) get = AccessTools.PropertyGetter(photon, "LocalPlayer");
                    return PhotonActor(get == null ? null : get.Invoke(null, null));
                }
                catch { return -1; }
            }

            static bool Send(float[] data, bool reliable)
            {
                EnsureHooked();
                if (!_hooked) return false;
                object options = _optionsType == null ? null
                    : Activator.CreateInstance(_optionsType);
                _raise.Invoke(null, new object[] {
                    (byte)RevivalPlugin.CfgAdminEventCode.Value, data, reliable, options });
                return true;
            }

            public static void Grant(int target, out string message)
            {
                if (target <= 0) { message = "select a player first"; return; }
                if (target == OwnActor())
                {
                    _sessionGranted = true;
                    message = "admin access already active for this session";
                    return;
                }
                message = Send(new float[] { GrantAction, target }, true)
                    ? "temporary admin access sent to player #" + target
                    : "admin grant could not be sent";
            }

            public static void Item(int target, int item, int amount, out string message)
            {
                if (target <= 0) { message = "select a player first"; return; }
                if (target == OwnActor())
                {
                    GibItem(item, amount, out message);
                    return;
                }
                message = Send(new float[] { ItemAction, target, item, amount }, true)
                    ? "item " + item + " sent to player #" + target
                    : "item command could not be sent";
            }

            public static void Loadout(int target, bool ukb, out string message)
            {
                if (target <= 0) { message = "select a player first"; return; }
                if (target == OwnActor())
                {
                    ApplyLoadout(ukb, out message);
                    return;
                }
                message = Send(new float[] { LoadoutAction, target, ukb ? 1f : 0f }, true)
                    ? (ukb ? "UKB loadout" : "loadout") + " sent to player #" + target
                    : "loadout command could not be sent";
            }

            public static void Teleport(int target, Vector3 point, out string message)
            {
                if (target <= 0) { message = "select a player first"; return; }
                if (target == OwnActor())
                {
                    MapTools.TeleportLocal(point, out message);
                    return;
                }
                message = Send(new float[] {
                    TeleportAction, target, point.x, point.y, point.z }, true)
                    ? "teleport sent to player #" + target
                    : "teleport command could not be sent";
            }

            public static void GodMode(int target, bool enabled, out string message)
            {
                if (target <= 0) { message = "select a player first"; return; }
                if (target == OwnActor())
                {
                    SetGodMode(enabled, out message);
                    return;
                }
                message = Send(new float[] {
                    GodModeAction, target, enabled ? 1f : 0f }, true)
                    ? "god mode " + (enabled ? "ON" : "OFF")
                        + " sent to player #" + target
                    : "god mode command could not be sent";
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                if (RevivalPlugin.CfgAdminEventCode == null
                    || code != (byte)RevivalPlugin.CfgAdminEventCode.Value) return;
                try
                {
                    float[] data = content as float[];
                    if (data == null || data.Length < 2) return;
                    int target = Mathf.RoundToInt(data[1]);
                    if (target != OwnActor()) return;
                    int action = Mathf.RoundToInt(data[0]);
                    string message = "";
                    if (action == GrantAction)
                    {
                        _sessionGranted = true;
                        _zutritt = 1;
                        message = "player #" + sender
                            + " granted temporary admin access for this session";
                    }
                    else if (action == ItemAction && data.Length >= 4)
                        GibItem(Mathf.RoundToInt(data[2]), Mathf.RoundToInt(data[3]),
                                out message);
                    else if (action == LoadoutAction && data.Length >= 3)
                        ApplyLoadout(data[2] > 0.5f, out message);
                    else if (action == TeleportAction && data.Length >= 5)
                        MapTools.TeleportLocal(new Vector3(data[2], data[3], data[4]),
                                               out message);
                    else if (action == GodModeAction && data.Length >= 3)
                        SetGodMode(data[2] > 0.5f, out message);
                    if (message.Length > 0)
                    {
                        Melde(message);
                        Turret.Hinweis(message, 5f);
                    }
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Admin network receive: " + ex.Message);
                }
            }
        }

        static void SetGodMode(bool enabled, out string message)
        {
            _godMode = enabled;
            message = "god mode " + (enabled ? "ON" : "OFF")
                + " for this session";
            RevivalPlugin.L.LogInfo("Admin: " + message + ".");
        }

        static void Melde(string s)
        {
            _status = s;
            RevivalPlugin.L.LogInfo("Adminmenue: " + s);
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try
            {
                _key = (KeyCode)Enum.Parse(typeof(KeyCode),
                                           RevivalPlugin.CfgAdminKey.Value, true);
            }
            catch
            {
                _key = KeyCode.F8;
                RevivalPlugin.L.LogWarning("Adminmenue: Taste "
                    + RevivalPlugin.CfgAdminKey.Value + " unbekannt, benutze F8.");
            }
            return _key;
        }
    }

    /// <summary>
    /// Setzt die Kamera NACH der Kamera des Spiels.
    ///
    /// Der erste Anlauf am 2026-08-28 schrieb die Kameralage im LateUpdate des
    /// Plugins. Das reicht nicht: `CameraFPSController::LateUpdate` setzt sie
    /// im selben Frame ebenfalls, und welche der beiden Komponenten zuerst
    /// laeuft, entscheidet Unity nach Skriptreihenfolge - im Spiel gewann die
    /// des Spiels, der Blick blieb hinter dem Turm. Ein Postfix auf genau diese
    /// Methode laeuft dagegen garantiert danach.
    /// </summary>
    [HarmonyPatch]
    public static class CameraHook
    {
        public static void Postfix()
        {
            // Nicht mehr fest der Turm: wer den Blick haelt, entscheidet
            // CameraOwner. Sonst schrieben Turm und Drohne im selben Frame
            // dieselbe Transform.
            CameraOwner.LateTick();
        }

        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("CameraFPSController");
                if (t == null)
                {
                    RevivalPlugin.L.LogWarning("Geschuetzkamera: CameraFPSController "
                        + "nicht gefunden - der Blick bleibt beim Spiel.");
                    return;
                }
                MethodInfo m = AccessTools.Method(t, "LateUpdate", null, null);
                if (m == null)
                {
                    RevivalPlugin.L.LogWarning("Geschuetzkamera: CameraFPSController.LateUpdate "
                        + "nicht gefunden.");
                    return;
                }
                harmony.Patch(m, null,
                              new HarmonyMethod(typeof(CameraHook).GetMethod("Postfix")),
                              null, null, null);
                RevivalPlugin.L.LogInfo("Geschuetzkamera: CameraFPSController.LateUpdate gepatcht.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Geschuetzkamera nicht eingehaengt: " + ex);
            }
        }
    }

    /// <summary>
    /// Haelt die Erkaeltung auf null.
    ///
    /// `PlayerLifeDataManager::PlayerColdController` laeuft im Takt von
    /// `Cold_Delay` und zaehlt `_playerLifeData.Cold` hoch, sobald die Stunde
    /// am `TOD_Sky` ueber 22 oder unter 7 liegt. Steigt Cold auf 100 und ist
    /// Temp 0, startet `ApplyPlayerSick("Temp", 50)`.
    ///
    /// Deshalb genuegt es NICHT, `cold` im gespeicherten Profil auf 0 zu
    /// setzen - nach der naechsten Nacht steht es wieder da. Der Prefix setzt
    /// den Wert und laesst das Original aus.
    /// </summary>
    [HarmonyPatch]
    public static class ColdHook
    {
        static FieldInfo _lifeData;
        static FieldInfo _cold;
        static FieldInfo _temp;
        static bool _looked;
        static bool _warned;

        public static bool Prefix(object __instance)
        {
            return Null(__instance, "Cold", ref _cold);
        }

        /// <summary>
        /// Das Fieber ist ein ZWEITER, eigener Zaehler - und der Grund, warum
        /// die Erkaeltung nach dem Heilen wiederkam.
        ///
        /// GEMESSEN (IL, 2026-08-28):
        /// ApplyPlayerLifeParamsConsequences ruft PlayerTempController auf,
        /// sobald _playerLifeData.Temp GROESSER NULL ist. PlayerTempController
        /// macht dann je Takt 1 Schaden, spielt den Hustenzustand und zaehlt
        /// Temp um 1 HOCH. Temp ist also kein Grad Celsius, sondern ein
        /// Fieberzaehler - gesund heisst Temp == 0.
        ///
        /// Beleg aus dem Profil: invtool.py hat Temp auf 36.6 gesetzt
        /// ("normale Koerpertemperatur"), nach der Sitzung stand 39.6 darin -
        /// genau drei Takte a plus eins. Die Heilung hat die Krankheit selbst
        /// am Leben gehalten.
        ///
        /// Der zweite Weg hinein bleibt trotzdem zu: PlayerColdController setzt
        /// bei Cold >= 100 ueber ApplyPlayerSick("Temp", 50) neues Fieber an -
        /// deshalb bleibt auch der Prefix auf Cold.
        /// </summary>
        public static bool TempPrefix(object __instance)
        {
            return Null(__instance, "Temp", ref _temp);
        }

        static bool Null(object __instance, string feld, ref FieldInfo cache)
        {
            if (!RevivalPlugin.CfgNoCold.Value) return true;
            try
            {
                if (!_looked)
                {
                    _looked = true;
                    _lifeData = AccessTools.Field(__instance.GetType(), "_playerLifeData");
                }
                if (_lifeData == null) return true;
                object data = _lifeData.GetValue(__instance);
                if (data == null) return true;

                if (cache == null) cache = AccessTools.Field(data.GetType(), feld);
                if (cache == null) return true;

                cache.SetValue(data, ZeroLike(cache.FieldType));
                return false;                       // Original ueberspringen
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    RevivalPlugin.L.LogWarning("Erkaeltung: " + ex.Message
                        + " - der Zaehler laeuft weiter.");
                }
                return true;
            }
        }

        /// <summary>
        /// Cold ist ein ObscuredFloat. Der Wert 0 muss deshalb ueber die
        /// implizite Umwandlung des Typs entstehen, nicht als blanke Null.
        /// </summary>
        static object ZeroLike(Type t)
        {
            if (t == typeof(float)) return 0f;
            MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != t) continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(float))
                    return ms[i].Invoke(null, new object[] { 0f });
            }
            return Activator.CreateInstance(t);
        }

        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerLifeDataManager");
                if (t == null)
                {
                    RevivalPlugin.L.LogWarning("Erkaeltung: PlayerLifeDataManager nicht gefunden.");
                    return;
                }
                MethodInfo m = AccessTools.Method(t, "PlayerColdController", null, null);
                if (m == null)
                {
                    RevivalPlugin.L.LogWarning("Erkaeltung: PlayerColdController nicht gefunden.");
                    return;
                }
                harmony.Patch(m, new HarmonyMethod(typeof(ColdHook).GetMethod("Prefix")),
                              null, null, null, null);

                MethodInfo mt = AccessTools.Method(t, "PlayerTempController", null, null);
                if (mt == null)
                    RevivalPlugin.L.LogWarning("Fieber: PlayerTempController nicht gefunden - "
                        + "eine bestehende Erkaeltung heilt dann nicht von selbst aus.");
                else
                    harmony.Patch(mt, new HarmonyMethod(typeof(ColdHook).GetMethod("TempPrefix")),
                                  null, null, null, null);

                RevivalPlugin.L.LogInfo("Erkaeltung: PlayerColdController"
                    + (mt == null ? "" : " und PlayerTempController")
                    + " gepatcht (NoCold=" + RevivalPlugin.CfgNoCold.Value
                    + "), Cold und Temp werden auf 0 gehalten.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Erkaeltung konnte nicht abgeschaltet werden: " + ex);
            }
        }
    }

    /// <summary>
    /// Erzeugt ein Fahrzeug vor dem Spieler. Ohne das steht ein BTR nur dort,
    /// wo die Szene einen VehicleSpawnPoint hat - zum Pruefen des Turmgeschuetzes
    /// zu wenig.
    ///
    /// Nachgebaut aus VehicleSpawnPoint::InstantiateCar (IL, 2026-08-28):
    ///
    ///     PhotonNetwork.InstantiateSceneObject("VehicleSpawn\" + name, pos, rot, 0, data)
    ///     VehicleGameSystem.Fuel, .Durability
    ///     VehicleInventoryManager.SetPartSpawn(10002, 10003, 10004)
    ///
    /// Zwei bewusste Abweichungen:
    ///
    /// 1. The data block keeps element zero null. VehicleGameSystem reads that
    ///    element as a spawn point id and returns immediately when it is null.
    ///    Element one carries only our T-72 marker, so the vehicle stays at the
    ///    requested position while every client can build the same appearance.
    /// 2. Die drei Teile setzt LocalSetVehicleComponent direkt statt ueber
    ///    SetPartSpawn - das wuerfelt bei Modus 3 mit 50 Prozent, und ein
    ///    Fahrzeug ohne Zuendkerze faehrt nicht.
    /// </summary>
    // ------------------------------------------------------------ Kampfpanzer

    /// <summary>
    /// Macht aus einem frisch gespawnten BTR-80A einen T-72.
    ///
    /// Der Panzer ist kein eigenes Fahrzeug, sondern ein umgebautes. Dasselbe
    /// Prefab, dieselben Kollider, dieselbe Fahrphysik, dasselbe PhotonView.
    /// Ausgetauscht werden drei Dinge, und nur diese drei:
    ///
    ///     das sichtbare Mesh   hull -> t72_hull, turret -> t72_turret
    ///     die Zahl der Sitze   6 + Geschuetz  ->  3 + Geschuetz
    ///     das Werteprofil      Turret rechnet mit den Tank-Werten
    ///
    /// Der Panzer faehrt sich damit wie ein BTR. Das ist kein Versehen,
    /// sondern der Preis, zu dem es ueberhaupt geht: ein Fahrzeug ist nicht
    /// ein Modell, sondern Modell plus Radkollider, Fahrphysik, Netzabgleich,
    /// Sitzpunkte, Schadensmodell, Treibstoff, Bauteile und Spawnsystem.
    ///
    /// BELEGT (REVERSE_ENGINEERING.md 18, am 2026-08-29 ohne Spielstart):
    ///
    ///   Das Prefab hat sieben BoxCollider und acht Radkollider, aber KEINEN
    ///   MeshCollider. Ein Meshtausch kann die Fahrphysik nicht treffen - das
    ///   war die riskanteste Annahme des ganzen Vorhabens.
    ///
    ///   VehicleGameSystem::InitCar setzt
    ///       Passengers = new GameObject[SeatPoints.childCount]
    ///   Die Sitzzahl steht also NUR an SeatPoints. InitCar ist gelaufen, wenn
    ///   dieser Umbau greift - deshalb wird das Array hier neu gesetzt. Zu
    ///   diesem Zeitpunkt sitzt noch niemand darin.
    ///
    ///   Der Knoten `Meshes` dreht -90 Grad um X. Im Meshraum ist damit +Z
    ///   oben und -Y vorn, der Massstab betraegt 3 Einheiten je Meter.
    ///   `t72_mesh.py` baut genau so - deshalb wird hier nichts skaliert und
    ///   nichts gedreht.
    ///
    /// NETWORK APPEARANCE (CONFIRMED IL, 2026-08-31): Photon transmits the
    /// prefab path, not a locally changed mesh. CarSpawn therefore writes a
    /// marker into InstantiateSceneObject's object[] data. NetworkingPeer::
    /// DoInstantiate receives that data on the creator, other players and late
    /// joiners; TankNetwork's postfix applies this same conversion there.
    ///
    /// UNGEPRUEFT: alles, was Augen braucht. Steht in TASKS.md unter
    /// "Abnahme im Spiel".
    /// </summary>
    public static class Tank
    {
        /// <summary>
        /// Kennzeichnung im Namen der Instanz. Sie MUSS weiter mit "BTR-80A"
        /// beginnen: `Turret.IsBtr` prueft diesen Praefix, und der Panzer soll
        /// vom vorhandenen Geschuetzcode ohne jede Aenderung erkannt werden.
        /// </summary>
        public const string Marke = "_T72";
        const string InstanzName = "BTR-80A_Spawn" + Marke;

        /// <summary>
        /// Turmring des T-72 in der Wanne, in Spieleinheiten. Der BTR hat den
        /// Turm hoeher und weiter vorn; bliebe die Transform stehen, schwebte
        /// der Panzerturm.
        ///
        /// Der Wert ist seit 0.5.3 nicht mehr gesetzt, sondern GEMESSEN:
        /// `t72_import.py` liest die Baugruppe `t-72_wrecked_LOD0` aus dem
        /// Spiel und gibt am Ende die Stellung aus, die die Turmtransform
        /// bekommen muss. Wer den Import erneut laufen laesst, vergleicht die
        /// letzte Zeile seiner Ausgabe mit dieser Zeile hier.
        /// </summary>
        static readonly Vector3 Turmring = new Vector3(0f, -0.922f, 4.144f);

        /// <summary>
        /// Wo die Mitfahrer im Panzer sitzen, in Fahrzeugeinheiten relativ zu
        /// SeatPoints. Fahrer vorn links, dahinter zwei Plaetze im Kampfraum -
        /// alle drei tief genug, dass kein Kopf durch das Dach stoesst.
        /// </summary>
        static readonly Vector3[] Mitfahrerplaetze = new Vector3[] {
            new Vector3(-1.10f, -1.0f, 3.60f),
            new Vector3( 1.35f, -1.0f, 0.20f),
            new Vector3(-1.35f, -1.0f, -2.60f),
        };

        /// <summary>
        /// The BTR gunner seat is at y 0.95. In the lower T-72 body that puts
        /// the seated player's head through the roof. The sight camera follows
        /// the turret independently, so only the physical body moves down.
        /// </summary>
        static readonly Vector3 Gunnerplatz = new Vector3(0f, -1.15f, 1.10f);

        static Material _mat;

        /// <summary>
        /// The first F9 spawn used to load two meshes and three 2K textures in
        /// one Update call. Unity could not service Photon during that work,
        /// and the combined pause exceeded its 15 second disconnect timeout.
        /// Fill the existing Assets caches near startup instead, yielding one
        /// frame between every expensive file/decode step. The actual tank
        /// construction remains synchronous but has no disk work left.
        /// </summary>
        public static IEnumerator Prewarm()
        {
            if (RevivalPlugin.CfgTank == null || !RevivalPlugin.CfgTank.Value
                || RevivalPlugin.CfgTankSwapMesh == null
                || !RevivalPlugin.CfgTankSwapMesh.Value)
                yield break;

            yield return null;
            Assets.Load("t72_hull.ndmesh");
            yield return null;
            Assets.Load("t72_turret.ndmesh");
            yield return null;
            Assets.Texture("t72_diffuse.png", false, true);
            yield return null;
            Assets.Texture("t72_metal.png", true, true);
            yield return null;
            Assets.Texture("t72_normal.png", true, true);
            yield return null;
            RevivalPlugin.L.LogInfo("Panzer: resources prewarmed for F9.");
        }

        public static bool IstPanzer(Transform root)
        {
            if (root == null) return false;
            return root.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Der ganze Umbau. Aufgerufen direkt nach dem Spawn.</summary>
        public static void Umbauen(GameObject car)
        {
            if (car == null) return;
            car.name = InstanzName;
            try { Sitze(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Panzer, Sitze: " + ex); }
            if (!RevivalPlugin.CfgTankSwapMesh.Value)
            {
                RevivalPlugin.L.LogInfo("Panzer: Meshtausch ist abgeschaltet, "
                    + "es bleibt ein BTR mit Panzerwerten stehen.");
                return;
            }
            try { Meshes(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Panzer, Mesh: " + ex); }
        }

        // ------------------------------------------------------------- Sitze

        static void Sitze(GameObject car)
        {
            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (vgsType == null) return;
            Component vgs = car.GetComponent(vgsType);
            if (vgs == null) return;

            FieldInfo fSeats = AccessTools.Field(vgsType, "SeatPoints");
            Transform seats = fSeats == null ? null : fSeats.GetValue(vgs) as Transform;
            if (seats == null)
            {
                RevivalPlugin.L.LogWarning("Panzer: SeatPoints fehlt, Sitzzahl bleibt.");
                return;
            }

            // Der Geschuetzsitz haengt schon dran (Turret.InitCarPrefix lief
            // beim Erzeugen) und bleibt in jedem Fall. Weggenommen werden nur
            // Mitfahrerplaetze, und zwar von hinten.
            int behalten = Mathf.Max(1, RevivalPlugin.CfgTankSeats.Value);
            List<Transform> mitfahrer = new List<Transform>();
            Transform gunner = null;
            for (int i = 0; i < seats.childCount; i++)
            {
                Transform c = seats.GetChild(i);
                if (c.name == Turret.SeatName) { gunner = c; continue; }
                mitfahrer.Add(c);
            }
            for (int i = mitfahrer.Count - 1; i >= behalten; i--)
            {
                // SetParent wirkt sofort, Destroy erst am Frameende - und bis
                // dahin zaehlte childCount den Sitz noch mit.
                mitfahrer[i].SetParent(null, false);
                UnityEngine.Object.Destroy(mitfahrer[i].gameObject);
            }

            // Die verbliebenen Plaetze IN die Wanne legen.
            //
            // Die Sitzpunkte des BTR stehen fuer eine Wanne, die hoeher ist als
            // die des Panzers: Driver bei z 5.47 und Passenger1 bei z 5.77
            // liegen unter der abfallenden Bugplatte, und dort ragten am
            // 2026-08-29 im Spiel zwei Koepfe aus dem Panzerdach heraus.
            //
            // VehicleGameSystem::SitToPassengerPlace setzt Position und Drehung
            // des Mitfahrers EINMAL aus SeatPoints.GetChild(i) (im IL gelesen,
            // 2026-08-29) - wer die Punkte vor dem Aufsitzen verschiebt,
            // verschiebt damit den Mitfahrer, und sonst nichts. The gunner is
            // moved too: its BTR height is above the T-72 roof.
            //
            // Hoehe -1.0 statt 0.15: das Wannendach liegt bei 4.5 Einheiten,
            // ein sitzender Koerper ist rund 3.9 hoch. Damit bleibt gut eine
            // Einheit Luft, und der Platz liegt trotzdem nicht so tief, dass
            // die Kamera durch den Boden faellt.
            for (int i = 0; i < mitfahrer.Count && i < Mitfahrerplaetze.Length; i++)
            {
                if (mitfahrer[i] == null) continue;
                mitfahrer[i].localPosition = Mitfahrerplaetze[i];
            }
            if (gunner != null)
            {
                gunner.localPosition = Gunnerplatz;
                gunner.localRotation = Quaternion.identity;
            }

            FieldInfo fPass = AccessTools.Field(vgsType, "Passengers");
            if (fPass != null) fPass.SetValue(vgs, new GameObject[seats.childCount]);

            RevivalPlugin.L.LogInfo("Panzer: " + seats.childCount + " Sitze ("
                + behalten + " Mitfahrer plus Geschuetz), Passengers neu gesetzt, "
                + "Mitfahrer und Richtschuetze in die Wanne gesetzt.");
        }

        // ------------------------------------------------------------- Modell

        static void Meshes(GameObject car)
        {
            Mesh wanne = Assets.Load("t72_hull.ndmesh");
            Mesh turm = Assets.Load("t72_turret.ndmesh");
            if (wanne == null || turm == null)
            {
                RevivalPlugin.L.LogWarning("Panzer: t72_hull.ndmesh oder "
                    + "t72_turret.ndmesh fehlt neben der DLL - es bleibt ein BTR.");
                return;
            }

            int wannen = 0, tuerme = 0, glas = 0;
            Transform[] all = car.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                // Vier LOD-Stufen tragen dieselben Namen. Alle vier werden
                // umgehaengt: die LODGroup blendet beim Wegfahren um, und ein
                // Panzer, der ab dreissig Metern zum BTR wird, ist schlimmer
                // als gar keiner.
                if (t.name == "hull") { if (Setzen(t, wanne)) wannen++; }
                else if (t.name == "turret")
                {
                    if (Setzen(t, turm)) { t.localPosition = Turmring; tuerme++; }
                }
                else if (t.name == "glass")
                {
                    Renderer r = t.GetComponent<Renderer>();
                    if (r != null) { r.enabled = false; glas++; }
                }
            }

            // Die acht Raeder des BTR liegen innerhalb der Ketten und waeren
            // von aussen kaum zu sehen - aber sie drehen sich beim Fahren, und
            // ein drehendes LKW-Rad in einer Panzerkette faellt dann doch auf.
            // Nur die Anzeige geht aus; die Radkollider bleiben, sonst faehrt
            // nichts mehr.
            int raeder = 0;
            Transform naben = car.transform.Find("Wheel Transforms");
            if (naben != null)
            {
                Renderer[] rs = naben.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++) { rs[i].enabled = false; raeder++; }
            }

            RevivalPlugin.L.LogInfo("Panzer: " + wannen + " Wannen, " + tuerme
                + " Tuerme getauscht, " + glas + " Scheiben und " + raeder
                + " Raeder ausgeblendet.");
        }

        static bool Setzen(Transform t, Mesh mesh)
        {
            MeshFilter mf = t.GetComponent<MeshFilter>();
            if (mf == null) return false;
            // .mesh und nicht .sharedMesh: sonst traegt jedes BTR im Spiel ab
            // sofort das Panzermodell.
            mf.mesh = mesh;
            Renderer r = t.GetComponent<Renderer>();
            if (r != null) r.material = Panzermaterial(r.sharedMaterial);
            return true;
        }

        /// <summary>
        /// Material aus dem Shader des Fahrzeugs und den eigenen Texturen.
        ///
        /// Dieselbe Kette wie `ItemFactory.MakeMaterial` - dort steht
        /// ausfuehrlich, warum jede einzelne Zeile noetig ist. Kurz: Shader vom
        /// Original uebernehmen, Metallic-Map abraeumen, Blend-Modus hart auf
        /// Opaque, und die Normal Map ohne das Keyword `_NORMALMAP` wird zwar
        /// gesetzt, aber nicht ausgewertet.
        ///
        /// Einmal gebaut und gemerkt: alle Panzer teilen sich ein Material.
        /// </summary>
        static Material Panzermaterial(Material vorlage)
        {
            if (_mat != null) return _mat;

            Shader shader = vorlage == null ? null : vorlage.shader;
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new Exception("kein brauchbarer Shader gefunden");

            Texture2D diffuse = Assets.Texture("t72_diffuse.png", false, true);
            Material m = new Material(shader);
            m.name = "T72_Material";
            m.mainTexture = diffuse;
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", diffuse);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);

            // OBERFLAECHE WIE AM MTW - UND ZWAR AUS DESSEN EIGENEM MATERIAL
            //
            // Bis 0.5.1 stand hier Metallic 0.04 und Glossiness 0.10, mit der
            // Begruendung "Panzerlack ist matt". Das war eine Annahme, und im
            // Spiel sah der Panzer damit aus wie bemaltes Papier, waehrend der
            // BTR direkt daneben metallisch glaenzte. Der Auftrag lautet
            // ausdruecklich: dieselbe Oberflaeche wie der MTW.
            //
            // `research/dump_material.py btr-80a_alb` (2026-08-29) sagt, was
            // der MTW wirklich benutzt: Standardshader mit den Keywords
            // _METALLICGLOSSMAP und _NORMALMAP, dazu `_GlossMapScale` 0.4. Die
            // Skalarwerte `_Metallic` 0.30 und `_Glossiness` 0.5 sind dabei
            // WIRKUNGSLOS - liegt eine Map an, liest der Shader Metallic aus
            // deren rotem Kanal und Smoothness aus deren Alpha. Gemessen an
            // btr-80a_met: Rot im Mittel 0.15, Alpha ueberall 1.0 (DXT1 hat
            // keinen Alphakanal), wirksame Smoothness also 0.40.
            //
            // t72_metal.png traegt genau diese Werte auf Wanne und Turm und
            // hoehere am Laufwerk. `_GlossMapScale` bleibt deshalb auf 1.0 -
            // die 0.4 stecken schon im Alphakanal. Die Skalarwerte werden
            // trotzdem gesetzt, damit der Panzer auch dann vernuenftig
            // aussieht, wenn die Map einmal fehlt.
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.40f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.15f);
            Texture2D metal = Assets.Texture("t72_metal.png", true, true);
            if (metal != null && m.HasProperty("_MetallicGlossMap"))
            {
                m.SetTexture("_MetallicGlossMap", metal);
                if (m.HasProperty("_GlossMapScale")) m.SetFloat("_GlossMapScale", 1.0f);
                // 0 = Smoothness aus dem Alphakanal der Metallic-Map. Genau so
                // steht es am MTW.
                if (m.HasProperty("_SmoothnessTextureChannel"))
                    m.SetFloat("_SmoothnessTextureChannel", 0f);
                m.EnableKeyword("_METALLICGLOSSMAP");
            }
            else
            {
                if (m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", null);
                m.DisableKeyword("_METALLICGLOSSMAP");
            }
            // Ohne diese beiden bleibt das Metall stumpf: der Standardshader
            // rechnet Glanzlicht und Spiegelung nur, wenn beide anstehen.
            if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 1f);
            if (m.HasProperty("_GlossyReflections")) m.SetFloat("_GlossyReflections", 1f);
            m.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
            m.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
            if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 0f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", 1f);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", 0f);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            m.DisableKeyword("_EMISSION");

            Texture2D nrm = Assets.Texture("t72_normal.png", true, true);
            if (nrm != null && m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", nrm);
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 1.0f);
                m.EnableKeyword("_NORMALMAP");
            }

            RevivalPlugin.L.LogInfo("Panzer: Material auf Shader " + shader.name
                + ", Normal Map " + (nrm != null)
                + ", Metallic Map " + (metal != null) + ".");
            _mat = m;
            return m;
        }
    }

    /// <summary>
    /// Carries the T-72 decision in Photon's cached scene-instantiation event.
    /// Element zero deliberately remains null because VehicleGameSystem treats
    /// it as an Int32 spawn point id. The string in element one is independent
    /// mod data and is safe for Photon to cache and replay to late joiners.
    /// </summary>
    public static class TankNetwork
    {
        const string Marker = "NDR_T72_V1";

        public static object[] SpawnData(bool tank)
        {
            if (!tank) return null;
            return new object[] { null, Marker };
        }

        static bool IsTankData(object value)
        {
            object[] data = value as object[];
            return data != null && data.Length > 1 && data[0] == null
                && string.Equals(data[1] as string, Marker, StringComparison.Ordinal);
        }

        public static void Install(Harmony harmony)
        {
            try
            {
                Type peer = RevivalPlugin.TypeByName("NetworkingPeer");
                if (peer == null)
                {
                    RevivalPlugin.L.LogWarning("Panzer-Netzwerk: NetworkingPeer fehlt.");
                    return;
                }

                MethodInfo target = null;
                MethodInfo[] methods = peer.GetMethods(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo candidate = methods[i];
                    if (candidate.Name == "DoInstantiate"
                        && candidate.ReturnType == typeof(GameObject)
                        && candidate.GetParameters().Length == 3)
                    {
                        target = candidate;
                        break;
                    }
                }
                if (target == null)
                {
                    RevivalPlugin.L.LogWarning("Panzer-Netzwerk: DoInstantiate fehlt.");
                    return;
                }

                harmony.Patch(target, null,
                    new HarmonyMethod(typeof(TankNetwork).GetMethod("Postfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("Panzer-Netzwerk: Spawnmarker aktiv.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Panzer-Netzwerk konnte nicht aktiviert werden: " + ex);
            }
        }

        public static void Postfix(object __0, GameObject __result)
        {
            try
            {
                if (__result == null || Tank.IstPanzer(__result.transform)) return;
                IDictionary eventData = __0 as IDictionary;
                if (eventData == null || !eventData.Contains((byte)5)) return;
                if (!IsTankData(eventData[(byte)5])) return;

                Tank.Umbauen(__result);
                RevivalPlugin.L.LogInfo("Panzer-Netzwerk: T-72 auf diesem Client aufgebaut: "
                    + __result.name + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Panzer-Netzwerk, Spawnmarker: " + ex);
            }
        }
    }

    public static class CarSpawn
    {
        // Aus VehicleSpawnPoint::InstantiateCar: Batterie, Schluessel, Kerze.
        static readonly int[] Parts = new int[] { 10002, 10003, 10004 };

        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static KeyCode _tankKey = KeyCode.None;
        static bool _tankKeyParsed;
        static bool _tankTimeoutRaised;

        public static void Tick()
        {
            try
            {
                // Der Panzer haengt NICHT an Research/SpawnCar: der Fahrzeug-
                // spawn ist ein Werkzeug und standardmaessig aus, der Panzer
                // ist Spielinhalt und standardmaessig an.
                if (RevivalPlugin.CfgTank.Value && Input.GetKeyDown(TankKey()))
                {
                    GuardTankTimeout();
                    Spawn(true);
                    return;
                }
                if (!RevivalPlugin.CfgSpawnCar.Value) return;
                if (!Input.GetKeyDown(Key())) return;
                Spawn(false);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Fahrzeugspawn: " + ex);
            }
        }

        /// <summary>
        /// Put a vehicle down at a given place. Same path as the F7/F9 keys -
        /// same prefab, same three parts, same tank and durability - only the
        /// position comes from the caller instead of from the camera. The
        /// patrol driver spawns through here so there is exactly one place
        /// that knows how a vehicle is built.
        /// </summary>
        internal static GameObject SpawnAt(Vector3 pos, Quaternion rot, bool panzer)
        {
            if (!IsMasterClient())
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: dieser Client ist nicht "
                    + "Masterclient. InstantiateSceneObject wird von Photon abgewiesen.");
                return null;
            }

            string name = RevivalPlugin.CfgSpawnCarName.Value;
            GameObject car = InstantiateSceneObject("VehicleSpawn\\" + name, pos, rot,
                panzer);
            if (car == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: Photon lieferte null fuer \""
                    + name + "\". Ist der Prefabname richtig?");
                return null;
            }

            Prepare(car);
            if (panzer && !Tank.IstPanzer(car.transform)) Tank.Umbauen(car);
            return car;
        }

        static void Spawn(bool panzer)
        {
            float started = Time.realtimeSinceStartup;
            Camera cam = Camera.main;
            if (cam == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: keine Kamera gefunden.");
                return;
            }

            Vector3 ahead = cam.transform.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.000001f) ahead = Vector3.forward;
            ahead.Normalize();

            float distance = Mathf.Max(4f, RevivalPlugin.CfgSpawnCarDistance.Value);
            Vector3 above = cam.transform.position + ahead * distance + Vector3.up * 30f;

            Vector3 ground;
            GameObject under = Turret.RaycastObject(above, Vector3.down, 200f, out ground);
            if (under == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: unter " + above
                    + " ist kein Boden - naeher an festen Grund stellen.");
                return;
            }

            if (!IsMasterClient())
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: dieser Client ist nicht "
                    + "Masterclient. InstantiateSceneObject wird von Photon abgewiesen.");
                return;
            }

            string name = RevivalPlugin.CfgSpawnCarName.Value;
            string path = "VehicleSpawn\\" + name;
            Vector3 pos = ground + Vector3.up * 1.6f;
            Quaternion rot = Quaternion.LookRotation(ahead, Vector3.up);

            GameObject car = InstantiateSceneObject(path, pos, rot, panzer);
            if (car == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: Photon lieferte null fuer \""
                    + path + "\". Ist der Prefabname richtig?");
                return;
            }

            Prepare(car);
            if (panzer)
            {
                if (!Tank.IstPanzer(car.transform)) Tank.Umbauen(car);
            }
            Munitionsbeigabe(car, panzer);

            RevivalPlugin.L.LogInfo((panzer ? "Panzer aus \"" : "Fahrzeug \"")
                + name + "\" erzeugt bei " + pos
                + ", Boden \"" + under.name + "\" in "
                + (Time.realtimeSinceStartup - started).ToString("0.00") + " s.");
        }

        /// <summary>
        /// Photon 15 seconds is too aggressive for the synchronous first tank
        /// construction on this Unity version. Raise the existing peer value
        /// immediately before F9 and keep it for the session. This does not
        /// affect normal traffic; it only delays declaring a genuinely dead
        /// connection while the main thread is occupied.
        /// </summary>
        static void GuardTankTimeout()
        {
            if (_tankTimeoutRaised) return;
            try
            {
                Type network = RevivalPlugin.TypeByName("PhotonNetwork");
                if (network == null) throw new Exception("PhotonNetwork missing");

                object peer = null;
                FieldInfo peerField = AccessTools.Field(network, "networkingPeer");
                if (peerField != null) peer = peerField.GetValue(null);
                if (peer == null)
                {
                    PropertyInfo peerProperty = AccessTools.Property(network,
                                                                    "networkingPeer");
                    if (peerProperty != null)
                        peer = peerProperty.GetValue(null, null);
                }
                if (peer == null) throw new Exception("networkingPeer missing");

                const int wanted = 60000;
                int previous = -1;
                PropertyInfo property = AccessTools.Property(peer.GetType(),
                                                              "DisconnectTimeout");
                if (property != null && property.CanWrite)
                {
                    object old = property.GetValue(peer, null);
                    if (old != null) previous = Convert.ToInt32(old);
                    property.SetValue(peer, wanted, null);
                }
                else
                {
                    FieldInfo field = AccessTools.Field(peer.GetType(),
                                                        "DisconnectTimeout");
                    if (field == null) throw new Exception("DisconnectTimeout missing");
                    object old = field.GetValue(peer);
                    if (old != null) previous = Convert.ToInt32(old);
                    field.SetValue(peer, wanted);
                }

                _tankTimeoutRaised = true;
                RevivalPlugin.L.LogInfo("Panzer: Photon timeout " + previous
                    + " -> " + wanted + " ms for F9 construction.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Panzer: Photon timeout guard failed - "
                    + ex.Message);
            }
        }

        /// <summary>
        /// Granaten zum Panzer dazulegen.
        ///
        /// Am 2026-08-29 stand im Spiel ein fertiger Panzer, dessen Geschuetz
        /// auf jeden Mausklick schwieg - im Rucksack lag keine einzige 2053.
        /// Die Begruendung stand nur im Log. Wer den Panzer per Taste
        /// hinstellt, bekommt seine erste Ladung jetzt mit dazu; alles Weitere
        /// kommt aus Loot oder dem Adminmenue.
        /// </summary>
        static void Munitionsbeigabe(GameObject car, bool panzer)
        {
            int menge = panzer ? RevivalPlugin.CfgTankSpawnAmmo.Value
                               : RevivalPlugin.CfgTurretSpawnAmmo.Value;
            if (menge <= 0) return;
            int id = panzer ? RevivalPlugin.CfgTankAmmoId.Value
                            : RevivalPlugin.CfgTurretAmmoId.Value;

            if (!panzer)
            {
                int boxes = 1;
                ItemDef def = RevivalPlugin.FindItem(id);
                if (def != null && def.Bullets > 0)
                    boxes = Mathf.Max(1, Mathf.CeilToInt((float)menge / def.Bullets));
                int added = AddAmmoToTrunk(car, id, boxes);
                if (added > 0)
                {
                    int rounds = def == null ? menge : added * def.Bullets;
                    RevivalPlugin.L.LogInfo("MTW: Munitionsbeigabe " + added
                        + " Behaelter Item " + id + " im Kofferraum ("
                        + rounds + " Schuss).");
                    Turret.Hinweis(rounds + " Schuss im MTW-Kofferraum", 4f);
                    return;
                }
                RevivalPlugin.L.LogWarning("MTW: Kofferraum nahm Item " + id
                    + " nicht an - versuche den lokalen Rucksack.");
            }

            string meldung;
            bool ok = Admin.GibItem(id, menge, out meldung);
            RevivalPlugin.L.LogInfo((panzer ? "Panzer" : "MTW")
                + ": Munitionsbeigabe " + menge + "x " + id + " - " + meldung);
            if (ok) Turret.Hinweis(menge
                + (panzer ? " Granaten" : " Schuss") + " im Rucksack", 4f);
            else Turret.Hinweis("Munition fehlgeschlagen: " + meldung, 6f);
        }

        static int AddAmmoToTrunk(GameObject car, int id, int boxes)
        {
            if (car == null || boxes <= 0) return 0;
            try
            {
                Type type = RevivalPlugin.TypeByName("ItemsContainer");
                if (type == null) return 0;
                Component[] containers = car.GetComponentsInChildren(type, true);
                if (containers.Length == 0) return 0;
                object trunk = containers[0];
                MethodInfo free = AccessTools.Method(type, "GetContainerFreeSlot",
                    Type.EmptyTypes, null);
                MethodInfo add = AccessTools.Method(type,
                    "AddNewContainerItemFromResources",
                    new Type[] { typeof(int) }, null);
                if (free == null || add == null) return 0;

                int added = 0;
                for (int i = 0; i < boxes; i++)
                {
                    int before = Convert.ToInt32(free.Invoke(trunk, null));
                    if (before < 0) break;
                    add.Invoke(trunk, new object[] { id });
                    int after = Convert.ToInt32(free.Invoke(trunk, null));
                    if (after == before) break;
                    added++;
                }
                return added;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("MTW: Munition in Kofferraum: " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Tank, Zustand und die drei Teile, sonst springt der Motor nicht an.
        /// </summary>
        static void Prepare(GameObject car)
        {
            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (vgsType == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: VehicleGameSystem nicht gefunden.");
                return;
            }

            Component vgs = car.GetComponent(vgsType);
            if (vgs == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: kein VehicleGameSystem am Fahrzeug.");
                return;
            }

            SetFloatField(vgs, "Fuel", 4000f);
            SetFloatField(vgs, "Durability", 2000f);

            MethodInfo set = AccessTools.Method(vgsType, "LocalSetVehicleComponent", null, null);
            if (set == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: LocalSetVehicleComponent fehlt - "
                    + "das Fahrzeug steht ohne Batterie, Kerze und Schluessel da.");
                return;
            }

            ParameterInfo[] ps = set.GetParameters();
            bool secondIsBool = ps.Length > 1 && ps[1].ParameterType == typeof(bool);
            for (int i = 0; i < Parts.Length; i++)
            {
                object second = secondIsBool ? (object)true : (object)1;
                try { set.Invoke(vgs, new object[] { Parts[i], second }); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Fahrzeugspawn: Teil " + Parts[i]
                        + " liess sich nicht setzen: " + ex.Message);
                }
            }
        }

        static void SetFloatField(object instance, string name, float value)
        {
            try
            {
                FieldInfo fi = AccessTools.Field(instance.GetType(), name);
                if (fi == null) return;
                if (fi.FieldType == typeof(float)) fi.SetValue(instance, value);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: Feld " + name
                    + " nicht gesetzt: " + ex.Message);
            }
        }

        static bool IsMasterClient()
        {
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (photon == null) return false;
            MethodInfo getter = AccessTools.PropertyGetter(photon, "isMasterClient");
            if (getter == null) getter = AccessTools.PropertyGetter(photon, "IsMasterClient");
            // Findet sich die Eigenschaft nicht, wird nicht geraten: dann laesst
            // Photon den Aufruf entweder zu oder meldet es selbst.
            if (getter == null) return true;
            return (bool)getter.Invoke(null, null);
        }

        static GameObject InstantiateSceneObject(string path, Vector3 position,
                                                 Quaternion rotation, bool panzer)
        {
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (photon == null) throw new MissingMemberException("PhotonNetwork nicht gefunden.");

            MethodInfo chosen = null;
            MethodInfo[] methods = photon.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "InstantiateSceneObject") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 5 && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType == typeof(Vector3)
                    && ps[2].ParameterType == typeof(Quaternion)
                    && ps[3].ParameterType == typeof(byte))
                {
                    chosen = m;
                    break;
                }
            }
            if (chosen == null)
                throw new MissingMethodException(
                    "PhotonNetwork.InstantiateSceneObject(string,Vector3,Quaternion,byte,object[])");

            return chosen.Invoke(null, new object[] {
                path, position, rotation, (byte)0, TankNetwork.SpawnData(panzer) })
                as GameObject;
        }

        static KeyCode TankKey()
        {
            if (_tankKeyParsed) return _tankKey;
            _tankKeyParsed = true;
            try
            {
                _tankKey = (KeyCode)Enum.Parse(typeof(KeyCode),
                                               RevivalPlugin.CfgTankKey.Value, true);
            }
            catch
            {
                _tankKey = KeyCode.F9;
                RevivalPlugin.L.LogWarning("Panzer: Key "
                    + RevivalPlugin.CfgTankKey.Value + " unbekannt, benutze F9.");
            }
            return _tankKey;
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try
            {
                _key = (KeyCode)Enum.Parse(typeof(KeyCode),
                                           RevivalPlugin.CfgSpawnCarKey.Value, true);
            }
            catch
            {
                _key = KeyCode.F7;
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: SpawnCarKey "
                    + RevivalPlugin.CfgSpawnCarKey.Value + " unbekannt, benutze F7.");
            }
            return _key;
        }
    }

    // ------------------------------------------------------ NPC vehicle patrols

    /// <summary>
    /// Vehicles that drive the road on their own.
    ///
    /// This is **phase 2** of `docs/ai/tasks/npc-vehicle-patrols.md`: the
    /// driver, and nothing else. One vehicle, one route, no gun, no convoy,
    /// no loot. Its acceptance needs no eyes - switch it on, let it run ten
    /// minutes, and read the lap counter out of `BepInEx\LogOutput.log`.
    ///
    /// HOW A VEHICLE WITH NOBODY IN IT DRIVES (REVERSE_ENGINEERING.md 20)
    ///
    ///   The game's own way in is closed. `VehicleGameSystem::InputAxis`
    ///   returns at once while `_playersCount &lt;= 0`, and it would not help
    ///   anyway: it writes `VerticalAxis` / `HorizontalAxis`, which are only
    ///   the wish. `RCCCarControllerV2::KeyboardControlling` - which reads no
    ///   keyboard - turns that wish into `gasInput` by lerping towards it at
    ///   10 per second. Write `gasInput` yourself and that lerp erases it.
    ///
    ///   The door is one bool. `RCCCarControllerV2::Update` skips
    ///   `KeyboardControlling` when `AIController` is true, and skips the
    ///   branch that zeroes the inputs when `canControl` is false as well.
    ///   So: set `AIController = true`, and the four input fields are ours
    ///   alone. `canControl` gates nothing we need - `Engine`, `Braking` and
    ///   `ApplySteering` sit after that gate in `FixedUpdate`.
    ///
    ///   What DOES stop the vehicle is sleep mode, and it is two paths, not
    ///   one. The timer puts an empty vehicle to sleep 25 s after it is left
    ///   alone; `ForceSleepModeController` disables its physics after about
    ///   ONE second of standing still with an empty driver's seat - which is
    ///   what a patrol looks like in the moment it spawns. Both end in
    ///   `DisablePhys`, which sets `RCCCarControllerV2.IsMine = false`, and
    ///   RCC's `FixedUpdate` returns on its first line when that is false.
    ///
    ///   Faking `_playersCount` is the obvious idea and it is wrong:
    ///   `RefreshPlayersCount` recomputes it from the seats, and while it
    ///   holds, `VehicleGameSystem::Update` runs the HOST's own
    ///   `KeyboardControlling` on the patrol vehicle - his keys would drive
    ///   it and his engine key would switch it off. So the sleep is stopped
    ///   with two Harmony prefixes instead, and only for vehicles this class
    ///   owns. Every other vehicle in the world keeps its sleep mode.
    ///
    /// REVERSE IS THE BRAKE. `RCCCarControllerV2::GearBox` shifts into
    /// reverse when `brakeInput > 0.1` while the local forward velocity is
    /// under 1 m/s. With `autoReverse` on, `canGoReverseNow` is always true,
    /// so **any** braking below walking pace flips the vehicle into reverse.
    /// That is why <see cref="Drive"/> never brakes under
    /// <see cref="CoastBelow"/> km/h unless it means to reverse.
    ///
    /// UNTESTED: all of it. Nothing in this class has been seen in the game.
    /// </summary>
    public static class Patrol
    {
        /// <summary>Below this speed the driver coasts instead of braking,
        /// because braking here means reverse gear. See the class comment.</summary>
        const float CoastBelow = 8f;

        /// <summary>Steering angle in degrees that means full lock.</summary>
        const float FullLockAt = 25f;

        /// <summary>How far ahead of the hull the obstacle rays start. The
        /// BTR-80A is about 7.6 m long, so 4.2 m clears its own collider.</summary>
        const float NoseOffset = 4.2f;

        // ------------------------------------------------------------- the file

        class Point
        {
            public Vector3 Pos;
            public float Speed;          // km/h on the leg starting here, 0 = config
            public string Flags;
        }

        /// <summary>
        /// One route, and everything about the patrol that drives it.
        ///
        /// WHERE THE SETTINGS LIVE. In the FLAGS of waypoint 0, as
        /// `spawn,fraction=looter,vehicle=btr,count=2`. Not in a second file
        /// and not in a new column: the recorder writes this file from inside
        /// the game, `routecheck.py` reads it, `build.ps1` refuses to
        /// overwrite it, and every one of those three would have needed
        /// teaching. A flag on the first waypoint needed none of them - the
        /// loader already splits flags on commas and the checker already
        /// ignores what it does not know.
        /// </summary>
        class Route
        {
            public string Name;
            public List<Point> P = new List<Point>();

            /// <summary>civilian, looter, traitor or neutral - see
            /// <see cref="Fraktion"/>. Empty means the one in the config.</summary>
            public string Fraction = "";

            /// <summary>btr, tank or mixed. Empty means the one in the
            /// config.</summary>
            public string Vehicle = "";

            /// <summary>How many patrols this route should carry. The
            /// automatic keeps that many alive on it, inside the global
            /// MaxVehicles.</summary>
            public int Count = 1;

            /// <summary>Does the automatic use this route at all? A route
            /// being recorded, or one whose waypoints are wrong, is switched
            /// off without being deleted.</summary>
            public bool Enabled = true;

            public string Seite
            {
                get
                {
                    string f = Fraktion.Sauber(Fraction);
                    if (f.Length > 0) return f;
                    f = Fraktion.Sauber(RevivalPlugin.CfgPatrolFraction.Value);
                    return f.Length > 0 ? f : "neutral";
                }
            }

            public string Wagen
            {
                get
                {
                    string v = Vehicle == null ? "" : Vehicle.Trim().ToLowerInvariant();
                    if (v == "btr" || v == "tank" || v == "mixed") return v;
                    v = RevivalPlugin.CfgPatrolVehicle.Value;
                    if (v == null) return "mixed";
                    v = v.Trim().ToLowerInvariant();
                    return (v == "btr" || v == "tank") ? v : "mixed";
                }
            }
        }

        static Dictionary<string, Route> _routes = new Dictionary<string, Route>();
        static List<string> _order = new List<string>();
        static bool _loaded;

        // ------------------------------------------------------------ one patrol

        class Unit
        {
            public GameObject Car;
            public Component Vgs;        // VehicleGameSystem
            public Component Rcc;        // RCCCarControllerV2
            public object Body;          // UnityEngine.Rigidbody, by reflection
            public Route Route;
            public int Next;             // waypoint being driven to
            public int Lap;
            public bool Armed;
            public float Wait;           // seconds waited for IsInitialized
            public float Stuck;          // seconds with throttle and no speed
            public int Stage;            // 0 drive 1 reverse 2 ram
            public float StageTime;
            public int Frees;
            public int Reported;         // last lap written to the log

            // ---------------------------------------------------- the gun
            public bool Tank;            // which of the two value profiles
            public Transform[] Turrets = new Transform[0];
            public Renderer TurretRend;  // for the muzzle, see Muendung
            public float Yaw, Pitch;     // where the barrel is being sent
            public Transform Target;     // the player being engaged
            public float Held;           // seconds this target has been held
            public float Lost;           // seconds since it was last seen
            public float NextShot;       // Time.time the gun may fire again
            public float NextLook;       // Time.time of the next target scan
            public int Burst;            // shots fired in the running burst
            public int Shots, Hits;

            // --------------------------------------------------- the crew
            public string Seite;         // which side climbs out of the wreck
            public int CrewSize;         // men aboard, one per seat
            public bool CrewOut;         // they have climbed out
            public float Died;           // Time.time the vehicle was killed
        }

        static List<Unit> _units = new List<Unit>();

        // --------------------------------------------------- the automatic

        /// <summary>Seconds between two vehicles while the road is being
        /// FILLED. A replacement waits `RespawnSeconds` instead; this is only
        /// so the first MaxVehicles do not all appear in the same second.</summary>
        const float FillEvery = 12f;

        /// <summary>Seconds after the world comes up before the first patrol
        /// goes out. The scene is still settling in the first few seconds -
        /// terrain, colliders, the player's own body - and a vehicle dropped
        /// into that lands on nothing.</summary>
        const float SettleFirst = 25f;

        /// <summary>Metres an automatic patrol keeps away from the player when
        /// it is put down. A patrol appearing at 150 m is a vehicle coming down
        /// the road; one appearing at 30 m is a bug report.</summary>
        const float AutoAway = 150f;

        /// <summary>Is the automatic allowed to act? Shift plus the patrol key
        /// switches it off, the key alone switches it back on - otherwise a
        /// road cleared by hand would refill by itself within seconds.</summary>
        static bool _auto = true;

        /// <summary>Does the automatic still replace losses? `RespawnSeconds`
        /// at 0 fills the road once and never again.</summary>
        static bool _refill = true;

        /// <summary>Was the world up on the last tick? The change from false
        /// to true is what starts the first patrol.</summary>
        static bool _welt;

        /// <summary>`Time.time` the automatic may put the next vehicle down.</summary>
        static float _nextAuto;

        /// <summary>Instance ids of the VehicleGameSystem components this class
        /// owns. The sleep prefixes consult it, so it must be filled BEFORE the
        /// vehicle is woken and cleared when it is given up.</summary>
        static Dictionary<int, bool> _owned = new Dictionary<int, bool>();

        // ------------------------------------------------------------ recording

        static bool _recording;
        static Vector3 _lastRecorded;
        static bool _haveLastRecorded;
        static float _nextRecord;

        /// <summary>Metres the recorder must have moved before it writes
        /// another waypoint, however long it waited. Not a setting: it is a
        /// floor against a file full of the same point, not a taste.</summary>
        const float MinStep = 3f;

        // ------------------------------------------------------------------ keys

        static KeyCode _key, _recKey, _autoKey, _editKey;
        static bool _keysParsed;

        // =====================================================================
        //  Harmony: keep our own vehicles out of sleep mode
        // =====================================================================

        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("VehicleGameSystem");
                if (t == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol: VehicleGameSystem not found - "
                        + "patrol vehicles would fall asleep, the class stays off.");
                    return;
                }

                MethodInfo disable = AccessTools.Method(t, "DisablePhys", null, null);
                if (disable != null)
                    harmony.Patch(disable,
                        new HarmonyMethod(typeof(Patrol).GetMethod("DisablePhysPrefix")),
                        null, null, null, null);
                else
                    RevivalPlugin.L.LogWarning("Patrol: DisablePhys not found.");

                MethodInfo sleep = AccessTools.Method(t, "SetSleepModeEnabled",
                    new Type[] { typeof(bool) }, null);
                if (sleep != null)
                    harmony.Patch(sleep,
                        new HarmonyMethod(typeof(Patrol).GetMethod("SleepPrefix")),
                        null, null, null, null);
                else
                    RevivalPlugin.L.LogWarning("Patrol: SetSleepModeEnabled not found.");

                RevivalPlugin.L.LogInfo("Patrol: sleep mode suppressed for own vehicles.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol install: " + ex); }
        }

        /// <summary>False = swallow the call. Only for vehicles we own.</summary>
        public static bool DisablePhysPrefix(object __instance)
        {
            return !Owned(__instance);
        }

        /// <summary>Let a wake-up through, swallow a sleep. Sleeping would also
        /// switch the net sync off, and then a second player sees a vehicle
        /// standing where it no longer is.</summary>
        public static bool SleepPrefix(object __instance, bool __0)
        {
            if (!__0) return true;
            return !Owned(__instance);
        }

        static bool Owned(object instance)
        {
            if (_owned.Count == 0) return false;
            UnityEngine.Object o = instance as UnityEngine.Object;
            if (o == null) return false;
            return _owned.ContainsKey(o.GetInstanceID());
        }

        // =====================================================================
        //  Per frame
        // =====================================================================

        public static void Tick()
        {
            if (!RevivalPlugin.CfgPatrol.Value) return;
            try
            {
                ParseKeys();
                if (Input.GetKeyDown(_key))
                {
                    if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                        StopAll();
                    else
                        Toggle();
                }
                if (Input.GetKeyDown(_autoKey)) ToggleRecording();
                if (Input.GetKeyDown(_recKey)) RecordHere(true);
                if (_recording) RecordWhileWalking();
                Editor.Tick();
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol tick: " + ex); }

            try { Nachschub(); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol auto: " + ex); }

            // The gun runs at frame rate, not in the physics step: the turret
            // is turned by RotateTowards and a turret that steps 50 times a
            // second while the picture is drawn 120 times stutters visibly.
            // The driving stays in FixedTick, where it belongs.
            try { Gun.Tick(_units); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol gun: " + ex); }
        }

        /// <summary>The driving itself. Belongs in FixedUpdate: it writes the
        /// same fields RCC reads in ITS FixedUpdate, and a driver running at
        /// frame rate would fight the physics step.</summary>
        /// <summary>The editor window. Belongs in OnGUI, like every other
        /// piece of IMGUI in this plugin.</summary>
        public static void Draw()
        {
            if (!RevivalPlugin.CfgPatrol.Value) return;
            try { Editor.Draw(); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol editor: " + ex); }
        }

        /// <summary>Is the editor window open? RevivalPlugin.Update asks,
        /// because the cursor belongs to the window while it is.</summary>
        public static bool EditorOpen
        {
            get { return RevivalPlugin.CfgPatrol.Value && Editor.IsOpen; }
        }

        public static void FixedTick()
        {
            if (!RevivalPlugin.CfgPatrol.Value) return;
            if (_units.Count == 0) return;
            try
            {
                for (int i = _units.Count - 1; i >= 0; i--)
                {
                    Unit u = _units[i];
                    if (u.Car == null)
                    {
                        Forget(u);
                        _units.RemoveAt(i);
                        RevivalPlugin.L.LogInfo("Patrol: vehicle on " + u.Route.Name
                            + " is gone after " + u.Lap + " lap(s).");
                        Verloren();
                        continue;
                    }
                    if (!u.Armed) { Arm(u); continue; }
                    if (Gefallen(u)) continue;
                    Keep(u);
                    Drive(u);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol drive: " + ex); }
        }

        // =====================================================================
        //  Start and stop
        // =====================================================================

        static void Toggle()
        {
            // The key is also the way back: whoever presses it wants patrols,
            // so the automatic that Shift switched off is switched on again.
            if (!_auto)
            {
                _auto = true;
                _refill = true;
                _nextAuto = Time.time + FillEvery;
                RevivalPlugin.L.LogInfo("Patrol: the automatic is on again.");
            }

            int max = Mathf.Max(1, RevivalPlugin.CfgPatrolMax.Value);
            if (_units.Count >= max)
            {
                RevivalPlugin.L.LogInfo("Patrol: " + _units.Count + " vehicle(s) are "
                    + "already out, MaxVehicles is " + max
                    + ". Shift plus the key takes them off the road.");
                Turret.Hinweis(_units.Count + " patrols out - Shift+key stops them", 3f);
                return;
            }

            Load(false);
            // The named route first, because the key has always meant "one
            // more on the route I am working on". Since every route is a
            // patrol of its own, a name that is not in the file is no longer a
            // dead end: the route most in need gets the vehicle instead.
            Route r = Active();
            if (r == null) r = Duenn();
            if (r == null)
            {
                RevivalPlugin.L.LogWarning("Patrol: route \""
                    + RevivalPlugin.CfgPatrolRoute.Value + "\" is not in "
                    + RevivalPlugin.CfgPatrolFile.Value
                    + " and no other route wants a patrol. Record one - the "
                    + "editor key is " + RevivalPlugin.CfgPatrolEditorKey.Value + ".");
                Turret.Hinweis("No route \"" + RevivalPlugin.CfgPatrolRoute.Value + "\"", 4f);
                return;
            }
            if (r.P.Count < 3)
            {
                RevivalPlugin.L.LogWarning("Patrol: route " + r.Name + " has "
                    + r.P.Count + " waypoints. A loop needs at least three.");
                return;
            }
            Spawn(r, false);
        }

        /// <summary>
        /// The patrols nobody pressed a key for.
        ///
        /// Until 2026-08-30 a patrol existed only between one F11 and the end
        /// of the session, and the user asked for what the game's own NPCs do:
        /// be there when the world comes up, and come back some time after
        /// they are killed. This is that, and it is deliberately made of the
        /// pieces that were already there - `Spawn` puts one down, `Active`
        /// finds the route, `MaxVehicles` bounds the road.
        ///
        /// EVERY ROUTE IS A PATROL. Since 2026-08-30 the automatic does not
        /// serve one route out of the config but every route in the file:
        /// each says how many patrols it wants (`count=` in its first
        /// waypoint's flags) and which side drives it, `Duenn` picks the one
        /// furthest short of its number, and `MaxVehicles` is the ceiling over
        /// all of them together. That is what makes a looter patrol outside
        /// the looter base and a civilian one around the civilian base a
        /// setting rather than a rebuild.
        ///
        /// TWO CLOCKS, and they mean different things. The road is FILLED at
        /// `FillEvery` - a short interval, so the first MaxVehicles are out
        /// within a minute of the world coming up. A LOSS is replaced after
        /// `RespawnSeconds`, and that clock starts when the vehicle is
        /// destroyed (see <see cref="Verloren"/>), not when its wreck is
        /// cleared away - otherwise the two waits would add up and the road
        /// would stay empty for nine minutes after a kill.
        ///
        /// It waits for the world. `Gun.WeltLaeuft` asks the game's own player
        /// list, which is empty in the menu and in the loading screen; the
        /// first patrol goes out `SettleFirst` seconds after it fills, because
        /// a vehicle dropped into a scene that is still building lands on
        /// nothing.
        /// </summary>
        static void Nachschub()
        {
            if (!RevivalPlugin.CfgPatrolAuto.Value || !_auto || !_refill) return;

            bool welt = Gun.WeltLaeuft();
            if (welt != _welt)
            {
                _welt = welt;
                if (welt)
                {
                    _nextAuto = Time.time + SettleFirst;
                    RevivalPlugin.L.LogInfo("Patrol: the world is up - the first "
                        + "automatic patrol goes out in " + SettleFirst.ToString("0")
                        + " s.");
                }
                else
                {
                    // The scene is gone and so is everything that stood in it.
                    // The units would be a list of destroyed GameObjects, and
                    // the next world would inherit them.
                    _units.Clear();
                    _owned.Clear();
                    _refill = true;
                }
                return;
            }
            if (!welt) return;

            int max = Mathf.Max(1, RevivalPlugin.CfgPatrolMax.Value);
            if (_units.Count >= max) return;
            if (Time.time < _nextAuto) return;

            Load(false);
            Route r = Duenn();
            if (r == null)
            {
                // Said once a minute, not once a frame. Both reasons for
                // landing here are normal states, not errors: no route
                // recorded yet, or every route already carrying the patrols
                // it asked for.
                _nextAuto = Time.time + 60f;
                if (_order.Count == 0)
                    RevivalPlugin.L.LogWarning("Patrol: AutoStart is on and no "
                        + "route is recorded - press the editor key ("
                        + RevivalPlugin.CfgPatrolEditorKey.Value + ") and drive "
                        + "one.");
                return;
            }

            int before = _units.Count;
            Spawn(r, true);
            if (_units.Count == before)
            {
                // CarSpawn has already said why. Trying again next frame would
                // say it sixty times a second.
                _nextAuto = Time.time + 30f;
                return;
            }
            _nextAuto = Time.time + FillEvery;
        }

        /// <summary>
        /// The route most in need of a vehicle, or null when every one of them
        /// has what it asked for.
        ///
        /// EVERY route is a patrol now, not just the one named in the config.
        /// The user's plan is patrols at many places on the map - a looter
        /// patrol outside the looter base, a civilian one around the civilian
        /// base - and that means the automatic has to keep several routes
        /// stocked at once instead of one. Each route says how many it wants
        /// (`count=` in its first waypoint's flags, 1 when it does not say),
        /// and `MaxVehicles` is the ceiling over the whole map.
        ///
        /// "Most in need" is the largest shortfall, so a route that wants
        /// three and has none is filled before one that wants one and has
        /// none. A tie goes to the route that comes first in the file, which
        /// makes the order predictable while a route is being tuned.
        /// </summary>
        static Route Duenn()
        {
            Route best = null;
            int bestFehlt = 0;
            for (int i = 0; i < _order.Count; i++)
            {
                Route r = _routes[_order[i]];
                if (!r.Enabled || r.Count <= 0 || r.P.Count < 3) continue;
                int fehlt = r.Count - Fahren(r.Name);
                if (fehlt <= 0) continue;
                if (best == null || fehlt > bestFehlt) { best = r; bestFehlt = fehlt; }
            }
            return best;
        }

        /// <summary>How many patrols are on this route right now, wrecks
        /// included - a burning BTR still counts as that route's vehicle
        /// until `RespawnSeconds` says otherwise.</summary>
        static int Fahren(string name)
        {
            int n = 0;
            for (int i = 0; i < _units.Count; i++)
                if (_units[i].Car != null && _units[i].Route != null
                    && _units[i].Route.Name == name) n++;
            return n;
        }

        /// <summary>
        /// A patrol is gone, and the clock for its replacement starts here.
        /// Called the moment a vehicle is destroyed, not when the wreck is
        /// removed - `WreckSeconds` and `RespawnSeconds` are two waits that
        /// must not add up.
        /// </summary>
        static void Verloren()
        {
            if (!RevivalPlugin.CfgPatrolAuto.Value || !_auto) return;
            float wait = RevivalPlugin.CfgPatrolRespawn.Value;
            if (wait <= 0f)
            {
                if (_refill)
                    RevivalPlugin.L.LogInfo("Patrol: RespawnSeconds is 0 - this one "
                        + "is not replaced.");
                _refill = false;
                return;
            }
            float when = Time.time + wait;
            if (when > _nextAuto) _nextAuto = when;
            RevivalPlugin.L.LogInfo("Patrol: the next patrol goes out in "
                + wait.ToString("0") + " s.");
        }

        public static void StopAll()
        {
            // Shift takes them off AND keeps them off. A road cleared by hand
            // that fills itself again within a minute is not a stop button.
            _auto = false;

            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                Forget(u);
                Weg(u.Car);
            }
            RevivalPlugin.L.LogInfo("Patrol: " + _units.Count + " vehicle(s) taken off the road.");
            _units.Clear();
            Crew.StopAll();
        }

        /// <summary>
        /// Does this VehicleGameSystem belong to a patrol whose crew is still
        /// aboard? `Turret.FreeSeatPostfix` asks, and the answer decides
        /// whether a player may climb in. Cheap: an empty dictionary is the
        /// normal case and returns on the first line.
        /// </summary>
        internal static bool Besetzt(object vgs)
        {
            if (_units.Count == 0) return false;
            UnityEngine.Object o = vgs as UnityEngine.Object;
            if (o == null) return false;
            int id = o.GetInstanceID();
            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                if (u.Vgs == null || u.Vgs.GetInstanceID() != id) continue;
                return RevivalPlugin.CfgPatrolCrew.Value && !u.CrewOut && u.CrewSize > 0;
            }
            return false;
        }

        static MethodInfo _photonDestroy;
        static bool _photonDestroyLookedUp;

        /// <summary>
        /// Take a patrol vehicle out of the world - on EVERY client.
        ///
        /// It was put there with `PhotonNetwork.InstantiateSceneObject`
        /// (CarSpawn), so a plain `Object.Destroy` removes it on this machine
        /// and leaves a BTR standing on the road of every other one. Photon's
        /// own Destroy is only allowed to the master client, which is the only
        /// machine this class runs on anyway; if it is not to be had, the
        /// local Destroy is still better than a vehicle that stays.
        /// </summary>
        static void Weg(GameObject car)
        {
            if (car == null) return;
            if (!_photonDestroyLookedUp)
            {
                _photonDestroyLookedUp = true;
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon != null)
                    _photonDestroy = AccessTools.Method(photon, "Destroy",
                        new Type[] { typeof(GameObject) }, null);
                if (_photonDestroy == null)
                    RevivalPlugin.L.LogWarning("Patrol: PhotonNetwork.Destroy(GameObject) "
                        + "not found - a removed patrol vehicle stays standing on the "
                        + "other clients.");
            }
            if (_photonDestroy != null)
            {
                try
                {
                    _photonDestroy.Invoke(null, new object[] { car });
                    return;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Patrol: PhotonNetwork.Destroy refused ("
                        + ex.Message + ") - removing it locally instead.");
                }
            }
            UnityEngine.Object.Destroy(car);
        }

        /// <summary>Give a vehicle back to the game: out of the owner list, so
        /// its sleep mode works again the moment we stop steering it.</summary>
        static void Forget(Unit u)
        {
            if (u.Vgs == null) return;
            int id = u.Vgs.GetInstanceID();
            if (_owned.ContainsKey(id)) _owned.Remove(id);
        }

        /// <summary>
        /// Put one patrol vehicle on the route.
        ///
        /// `auto` decides WHERE, and the two answers are opposites on purpose.
        /// A patrol asked for with the key should be visible at once, so it
        /// starts at the nearest waypoint that is not on top of the man who
        /// pressed it. A patrol the automatic puts out should NOT appear in
        /// front of anybody - it starts as far from the player and from every
        /// other patrol as the route allows, and drives to him.
        /// </summary>
        static void Spawn(Route src, bool auto)
        {
            Route r = OutAndBack(src);

            int start = 0;
            for (int i = 0; i < r.P.Count; i++)
                if (HasFlag(r.P[i], "spawn")) { start = i; break; }

            Vector3 me = Where();
            float away = -1f;

            int pick = auto ? Verteilt(r, me) : -1;
            if (pick >= 0)
            {
                start = pick;
                if (me != Vector3.zero) away = Flat(r.P[start].Pos - me);
            }
            // The spawn flag sits where the RECORDING began, which can be a
            // kilometre from where the key is pressed - and a patrol nobody
            // ever sees is indistinguishable from one that never started. So
            // it starts at the nearest waypoint instead, but not one so close
            // that the vehicle lands on the man watching.
            else if (me != Vector3.zero)
            {
                int near = -1;
                float best = 0f;
                for (int i = 0; i < r.P.Count; i++)
                {
                    float d = Flat(r.P[i].Pos - me);
                    if (d < 20f) continue;
                    if (near < 0 || d < best) { near = i; best = d; }
                }
                if (near >= 0 && best <= 400f) { start = near; away = best; }
                else
                    RevivalPlugin.L.LogInfo("Patrol: no waypoint of " + r.Name
                        + " within 400 m - starting at waypoint " + start
                        + ", where the recording began.");
            }

            // The waypoints come from the CAMERA, which sits above the driver
            // and, in a vehicle, well above the road. Dropping the patrol from
            // that height is survivable but ugly - the ground under the
            // waypoint is the honest place, found the same way the F7 spawn
            // finds it.
            Vector3 pos = r.P[start].Pos + Vector3.up * 1.6f;
            Vector3 ground;
            GameObject under = Turret.RaycastObject(r.P[start].Pos + Vector3.up * 30f,
                                                    Vector3.down, 200f, out ground);
            if (under != null) pos = ground + Vector3.up * 1.6f;

            Vector3 ahead = r.P[(start + 1) % r.P.Count].Pos - r.P[start].Pos;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.0001f) ahead = Vector3.forward;
            ahead.Normalize();

            bool tank = TankThisTime(r);
            GameObject car = CarSpawn.SpawnAt(pos, Quaternion.LookRotation(ahead, Vector3.up),
                                              tank);
            if (car == null) return;      // CarSpawn has already said why

            Unit u = new Unit();
            u.Car = car;
            u.Route = r;
            u.Tank = tank;
            u.Seite = r.Seite;
            u.Next = (start + 1) % r.P.Count;
            _units.Add(u);
            _spawned++;

            RevivalPlugin.L.LogInfo("Patrol: "
                + (auto ? "automatic " : "")
                + (tank ? "tank" : "BTR")
                + " (" + u.Seite + ") put down on " + r.Name
                + " at waypoint " + start
                + " " + pos + ", driving to " + u.Next
                + (away >= 0f ? ", " + away.ToString("0") + " m from the player" : "")
                + ".");
            Turret.Hinweis(away >= 0f
                ? "Patrol " + r.Name + " (" + u.Seite + "), "
                  + away.ToString("0") + " m away"
                : "Patrol " + r.Name + " (" + u.Seite + ") started", 4f);
        }

        /// <summary>
        /// The waypoint an AUTOMATIC patrol starts at: the one whose nearest
        /// neighbour - the player, or another patrol already on the road - is
        /// as far away as the route allows.
        ///
        /// One number decides it, the smallest of those distances, and taking
        /// the largest of THOSE spreads the vehicles over the route by itself:
        /// with an empty road it is the far end, with one patrol out it is the
        /// opposite side, with three it is whatever gap is left. No slots, no
        /// division of the route into sectors, and nothing to keep in sync
        /// when a vehicle is lost.
        ///
        /// Returns -1 when the whole route lies inside `AutoAway` of the
        /// player. Then the caller falls back to the near rule - a patrol on a
        /// short route in front of the player is still better than none.
        /// </summary>
        static int Verteilt(Route r, Vector3 me)
        {
            int best = -1;
            float bestScore = 0f;
            for (int i = 0; i < r.P.Count; i++)
            {
                Vector3 pos = r.P[i].Pos;

                float score = 100000f;
                if (me != Vector3.zero)
                {
                    float d = Flat(pos - me);
                    if (d < AutoAway) continue;
                    score = d;
                }
                for (int k = 0; k < _units.Count; k++)
                {
                    if (_units[k].Car == null) continue;
                    float d = Flat(pos - _units[k].Car.transform.position);
                    if (d < score) score = d;
                }
                if (best < 0 || score > bestScore) { best = i; bestScore = score; }
            }
            return best;
        }

        /// <summary>How many patrols have been put down since the game
        /// started. Only "mixed" reads it, and only to alternate.</summary>
        static int _spawned;

        /// <summary>
        /// btr, tank, or mixed - and mixed ALTERNATES instead of rolling a
        /// die. Two dice throws in a row give two BTRs often enough that a
        /// player would report "the tank patrol does not work"; alternating
        /// means the second key press is always the other kind.
        /// </summary>
        static bool TankThisTime(Route r)
        {
            string want = r.Wagen;
            if (want == "tank") return true;
            if (want == "btr") return false;
            return (_spawned % 2) == 1;
        }

        /// <summary>
        /// A recorded route is OPEN: it ends where the driver stopped, and the
        /// leg from the last waypoint back to the first is a line across
        /// country that nobody drove. On R1, recorded 2026-08-30, that line is
        /// 1998 m long - driving it is the difference between a patrol and a
        /// vehicle disappearing into the woods.
        ///
        /// An open route is therefore mirrored: out along the road, and back
        /// the same way. The driver stays a pure loop driver, needs no second
        /// code path, and the two ends become u-turns - which is what the
        /// stuck escalation is there for. A route whose ends already meet is
        /// left alone.
        ///
        /// The copy belongs to the patrol. What stands in `_routes` stays as
        /// recorded, because the recorder writes THAT back to the file.
        /// </summary>
        static Route OutAndBack(Route r)
        {
            int n = r.P.Count;
            if (n < 3) return r;

            float sum = 0f;
            for (int i = 0; i < n - 1; i++)
                sum += Flat(r.P[i + 1].Pos - r.P[i].Pos);
            float avg = sum / (n - 1);
            float closing = Flat(r.P[0].Pos - r.P[n - 1].Pos);
            if (closing <= Mathf.Max(80f, 3f * avg)) return r;

            Route back = new Route();
            back.Name = r.Name;
            back.Fraction = r.Fraction;
            back.Vehicle = r.Vehicle;
            back.Count = r.Count;
            back.Enabled = r.Enabled;
            for (int i = 0; i < n; i++) back.P.Add(r.P[i]);
            for (int i = n - 2; i >= 1; i--) back.P.Add(r.P[i]);

            RevivalPlugin.L.LogInfo("Patrol: " + r.Name + " is open - the two ends "
                + "are " + closing.ToString("0") + " m apart while the legs average "
                + avg.ToString("0") + " m. Driving it out and back: "
                + back.P.Count + " waypoints, a u-turn at each end.");
            return back;
        }

        /// <summary>Length in the ground plane. Height is never a distance here:
        /// the waypoints carry camera height, the vehicle carries its own.</summary>
        static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }

        // =====================================================================
        //  Arming: the four settings that make an empty vehicle drivable
        // =====================================================================

        static void Arm(Unit u)
        {
            u.Wait += Time.fixedDeltaTime;

            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (vgsType == null) { Drop(u, "VehicleGameSystem not found"); return; }

            if (u.Vgs == null) u.Vgs = u.Car.GetComponent(vgsType);
            if (u.Vgs == null) { Drop(u, "the spawned object has no VehicleGameSystem"); return; }

            // The vehicle needs a few frames. Until IsInitialized is true,
            // SetSleepModeEnabled returns without doing anything and the
            // component references are not filled in yet.
            if (!GetBool(u.Vgs, "IsInitialized", false))
            {
                if (u.Wait > 15f)
                    Drop(u, "IsInitialized stayed false for 15 s");
                return;
            }

            u.Rcc = GetField(u.Vgs, "_carController") as Component;
            if (u.Rcc == null) { Drop(u, "_carController is empty"); return; }
            u.Body = GetField(u.Vgs, "_rigidbody");

            // From here on the sleep prefixes protect this vehicle. Entering it
            // in the owner list BEFORE waking it is deliberate: EnablePhys is
            // safe, but anything that runs in between must not put it back to
            // sleep.
            _owned[u.Vgs.GetInstanceID()] = true;

            // The one bool that takes the car out of every input path the game
            // has (REVERSE_ENGINEERING.md 20.1). The two next to it are what
            // RCCAICarController::Awake sets on a car it is going to drive.
            SetBool(u.Rcc, "AIController", true);
            SetBool(u.Rcc, "autoReverse", true);
            SetBool(u.Rcc, "canEngineStall", false);
            SetBool(u.Rcc, "automaticGear", true);

            // Physics on. NOT SetSleepModeEnabled(false) - EnablePhys is the
            // call that puts the wheels back and sets IsMine, and our own
            // prefix would swallow nothing of it (20.5).
            Invoke(u.Vgs, "EnablePhys");

            SetFloat(u.Vgs, "Fuel", 4000f);
            SetBool(u.Rcc, "engineRunning", true);

            Gun.Collect(u);
            u.CrewSize = Besatzung(u);

            u.Armed = true;
            RevivalPlugin.L.LogInfo("Patrol: vehicle armed on " + u.Route.Name
                + " - AIController set, physics on, engine running, "
                + u.Turrets.Length + " turret object(s), " + u.CrewSize
                + " man crew.");
        }

        /// <summary>
        /// One man per seat, minus the gunner's seat, which is not a seat the
        /// game hands out (Turret.FreeSeatPostfix) - the gunner is the turret
        /// code itself. Capped by CrewMax, because six marauders climbing out
        /// of one BTR is not a fight, it is a verdict.
        /// </summary>
        static int Besatzung(Unit u)
        {
            if (!RevivalPlugin.CfgPatrolCrew.Value) return 0;
            Transform seats = GetField(u.Vgs, "SeatPoints") as Transform;
            if (seats == null) return 0;
            int n = 0;
            for (int i = 0; i < seats.childCount; i++)
                if (seats.GetChild(i).name != Turret.SeatName) n++;
            return Mathf.Clamp(n, 0, Mathf.Max(0, RevivalPlugin.CfgPatrolCrewMax.Value));
        }

        static void Drop(Unit u, string why)
        {
            RevivalPlugin.L.LogWarning("Patrol: giving up on this vehicle - " + why + ".");
            Forget(u);
            Weg(u.Car);
            _units.Remove(u);
            Verloren();
        }

        /// <summary>
        /// Is this vehicle dead, and what happens then.
        ///
        /// `VehicleGameSystem::SetDurabilityValue` kills the engine at
        /// `Durability &lt;= 0`, turns the damage smoke on and starts a respawn
        /// timer (RE 20.9). It does NOT destroy the object - the wreck stands
        /// there. That is the moment the crew climbs out: whoever killed the
        /// vehicle is standing within a few dozen metres, and now there are
        /// men on the ground who want a word.
        ///
        /// The wreck is removed after WreckSeconds so a long session does not
        /// leave the road lined with burnt out BTRs. The crew stays - they are
        /// ordinary NPCs from that moment on, and they die like ordinary NPCs.
        /// </summary>
        static bool Gefallen(Unit u)
        {
            if (u.Died <= 0f)
            {
                if (GetFloat(u.Vgs, "Durability", 1f) > 0f) return false;

                u.Died = Time.time;
                u.Target = null;
                SetFloat(u.Rcc, "gasInput", 0f);
                SetFloat(u.Rcc, "brakeInput", 1f);
                SetFloat(u.Rcc, "steerInput", 0f);
                RevivalPlugin.L.LogInfo("Patrol: the " + (u.Tank ? "tank" : "BTR")
                    + " on " + u.Route.Name + " is destroyed after " + u.Lap
                    + " lap(s), " + u.Shots + " shot(s), " + u.Hits + " hit(s).");

                // The game's DamageSmoke is a dense cloud close to the hull.
                // Keep it, and add the part visible from down the road: fire on
                // the deck and a tall column that lives exactly as long as the
                // wreck. The effect is a child, so Weg(u.Car) removes it too.
                FireEffect.SpawnWreck(u.Car, u.Tank);
                Turret.Net.PublishWreck(u.Car.transform, u.Tank);

                if (!u.CrewOut && u.CrewSize > 0)
                {
                    u.CrewOut = true;
                    Crew.Aussteigen(u.Car, u.Vgs, u.CrewSize, u.Tank, u.Seite);
                }

                Verloren();
            }

            float bleibt = RevivalPlugin.CfgPatrolWreck.Value;
            if (bleibt > 0f && Time.time - u.Died >= bleibt)
            {
                RevivalPlugin.L.LogInfo("Patrol: wreck on " + u.Route.Name
                    + " removed after " + bleibt.ToString("0") + " s.");
                Forget(u);
                Weg(u.Car);
                _units.Remove(u);
            }
            return true;
        }

        /// <summary>Everything that has to be held every step, because the game
        /// keeps undoing it.</summary>
        static void Keep(Unit u)
        {
            // ExpendFuel drains an unmanned vehicle exactly as fast as a driven
            // one and kills the engine at zero (20.6). A patrol meant to run
            // for hours needs its tank held up.
            if (GetFloat(u.Vgs, "Fuel", 1f) < 500f) SetFloat(u.Vgs, "Fuel", 4000f);

            // StartEngine is a TOGGLE, not a start - calling it on a running
            // engine switches it off. Write the field.
            if (!GetBool(u.Rcc, "engineRunning", true)) SetBool(u.Rcc, "engineRunning", true);

            // Belt and braces: if some path we have not read got the physics
            // off anyway, the vehicle is a statue until this puts it back.
            if (!GetBool(u.Vgs, "EnabledPhys", true)) Invoke(u.Vgs, "EnablePhys");
        }

        // =====================================================================
        //  The driver
        // =====================================================================

        static void Drive(Unit u)
        {
            Transform t = u.Car.transform;
            Vector3 pos = t.position;
            Route r = u.Route;
            int n = r.P.Count;
            float dt = Time.fixedDeltaTime;

            Vector3 vel = Velocity(u.Body);
            float kmh = vel.magnitude * 3.6f;
            float forward = t.InverseTransformDirection(vel).z;

            Advance(u, pos);

            // --- where to aim ------------------------------------------------
            float look = Mathf.Clamp(vel.magnitude * 1.1f, 10f, 35f);
            Vector3 aim = LookAhead(r, u.Next, pos, look);

            Vector3 local = t.InverseTransformPoint(aim);
            local.y = 0f;
            float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;

            // --- how fast ----------------------------------------------------
            float want = r.P[u.Next].Speed;
            if (want <= 0f) want = RevivalPlugin.CfgPatrolSpeed.Value;
            want *= CornerFactor(r, u.Next);
            want = Mathf.Max(want, 12f);

            float steer = Mathf.Clamp(angle / FullLockAt, -1f, 1f);
            float gas, brake;
            Throttle(want, kmh, out gas, out brake);

            // --- what is in the way ------------------------------------------
            float dodge = Avoid(u, t, vel.magnitude);
            if (dodge != 0f)
            {
                steer = Mathf.Clamp(steer + dodge, -1f, 1f);
                if (want > 25f) { gas *= 0.5f; }
            }

            // --- stuck? -------------------------------------------------------
            if (gas > 0.3f && kmh < 3f) u.Stuck += dt; else u.Stuck = 0f;
            Escalate(u, ref gas, ref brake, ref steer, kmh, forward, pos);

            // Braking under walking pace is a gear change, not a brake
            // (see the class comment). Coast instead.
            if (u.Stage == 0 && kmh < CoastBelow && brake > 0f) { brake = 0f; }

            SetFloat(u.Rcc, "gasInput", Mathf.Clamp01(gas));
            SetFloat(u.Rcc, "brakeInput", Mathf.Clamp01(brake));
            SetFloat(u.Rcc, "steerInput", Mathf.Clamp(steer, -1f, 1f));
            SetFloat(u.Rcc, "handbrakeInput", 0f);

            if (u.Lap != u.Reported)
            {
                u.Reported = u.Lap;
                RevivalPlugin.L.LogInfo("Patrol: " + r.Name + " lap " + u.Lap
                    + " done, " + u.Frees + " free event(s) so far.");
            }
        }

        /// <summary>Walk the waypoint index forward past everything we have
        /// already reached or driven past.</summary>
        static void Advance(Unit u, Vector3 pos)
        {
            Route r = u.Route;
            int n = r.P.Count;
            float pass = RevivalPlugin.CfgPatrolPassRadius.Value;
            int moved = 0;

            while (moved < n)
            {
                Vector3 w = r.P[u.Next].Pos;
                Vector3 to = w - pos; to.y = 0f;
                bool close = to.sqrMagnitude < pass * pass;

                bool past = false;
                Vector3 prev = r.P[(u.Next - 1 + n) % n].Pos;
                Vector3 leg = w - prev; leg.y = 0f;
                if (leg.sqrMagnitude > 0.01f)
                {
                    Vector3 back = pos - w; back.y = 0f;
                    past = Vector3.Dot(back, leg.normalized) > 0f;
                }

                if (!close && !past) break;

                u.Next++;
                moved++;
                if (u.Next >= n) { u.Next = 0; u.Lap++; }
            }

            if (moved >= n)
                RevivalPlugin.L.LogWarning("Patrol: " + r.Name + " skipped a whole "
                    + "lap in one step - the vehicle is nowhere near its route. "
                    + "PassRadius too large, or the route has duplicate points.");
        }

        /// <summary>A point <paramref name="dist"/> metres along the route,
        /// measured from the vehicle. Pure pursuit aims at this, not at the
        /// waypoint: aiming straight at a waypoint makes a vehicle hunt.</summary>
        static Vector3 LookAhead(Route r, int next, Vector3 pos, float dist)
        {
            int n = r.P.Count;
            Vector3 cur = pos;
            Vector3 target = r.P[next].Pos;
            float rest = dist;
            int i = next;

            for (int step = 0; step < n; step++)
            {
                Vector3 w = r.P[i].Pos;
                Vector3 seg = w - cur;
                float len = seg.magnitude;
                if (len > 0.001f)
                {
                    if (len >= rest) return cur + seg * (rest / len);
                    rest -= len;
                }
                cur = w;
                target = w;
                i = (i + 1) % n;
            }
            return target;
        }

        /// <summary>1 on a straight, less the sharper the next corner is.</summary>
        static float CornerFactor(Route r, int next)
        {
            int n = r.P.Count;
            Vector3 a = r.P[(next - 1 + n) % n].Pos;
            Vector3 b = r.P[next].Pos;
            Vector3 c = r.P[(next + 1) % n].Pos;
            Vector3 u = b - a; u.y = 0f;
            Vector3 v = c - b; v.y = 0f;
            if (u.sqrMagnitude < 0.01f || v.sqrMagnitude < 0.01f) return 1f;
            float deg = Vector3.Angle(u, v);
            return Mathf.Clamp(1f - deg / 120f, 0.25f, 1f);
        }

        static void Throttle(float wantKmh, float isKmh, out float gas, out float brake)
        {
            float err = wantKmh - isKmh;
            gas = Mathf.Clamp01(err / 8f);
            brake = Mathf.Clamp01(-err / 8f);
        }

        // =====================================================================
        //  Obstacles
        // =====================================================================

        /// <summary>Three rays as RCC casts them. Returns a steering correction,
        /// 0 when the road ahead is clear.</summary>
        static float Avoid(Unit u, Transform t, float speed)
        {
            float range = Mathf.Clamp(speed * 1.5f, 8f, 25f);
            Vector3 nose = t.position + t.forward * NoseOffset + Vector3.up * 1.2f;

            // Ramming steers by nothing - but it is exactly the moment the
            // hull is pressed against whatever stopped it, so the one ray
            // that can still help is cast: if that thing is small, it goes.
            if (u.Stage == 2)
            {
                Hit(u, nose, t.forward, CrushReach);
                return 0f;
            }

            float wide = Hit(u, nose, t.forward, range);
            float left = Hit(u, nose, Quaternion.AngleAxis(-25f, t.up) * t.forward, range * 0.7f);
            float right = Hit(u, nose, Quaternion.AngleAxis(25f, t.up) * t.forward, range * 0.7f);

            if (wide < 0f && left < 0f && right < 0f) return 0f;

            // Steer towards whichever side has more room. Both blocked and the
            // escalation takes over on its own, because we will stop moving.
            float freeLeft = left < 0f ? range : left;
            float freeRight = right < 0f ? range : right;
            float push = freeLeft > freeRight ? -0.6f : 0.6f;
            if (wide >= 0f) push *= Mathf.Clamp01(1f - wide / range) + 0.4f;
            return push;
        }

        /// <summary>Distance to the first thing that is not this vehicle and
        /// not small enough to drive through, or -1 for a clear ray.</summary>
        static float Hit(Unit u, Vector3 origin, Vector3 dir, float range)
        {
            Vector3 point;
            GameObject go = Turret.RaycastObject(origin, dir, range, out point);
            if (go == null) return -1f;
            if (u.Car != null && go.transform.IsChildOf(u.Car.transform)) return -1f;
            float d = (point - origin).magnitude;

            Transform klein = Kleinkram(go);
            if (klein == null) return d;
            // Small enough to drive through: never steer around it, and take
            // it out of the way once the hull is actually against it.
            if (d <= CrushReach) Zerbrechen(u, klein);
            return -1f;
        }

        // =====================================================================
        //  The small stuff on the road
        // =====================================================================

        /// <summary>Metres in front of the nose at which a crushable thing is
        /// actually crushed. Anything further off is only ignored - a fence
        /// that vanishes twenty metres before the vehicle reaches it is a
        /// bug report.</summary>
        const float CrushReach = 6f;

        /// <summary>The answer for one hit object, so the same fence is not
        /// measured again on every ray of every physics step. Value null means
        /// "not crushable", which is the expensive answer and the common
        /// one.</summary>
        static Dictionary<int, Transform> _klein = new Dictionary<int, Transform>();

        /// <summary>Ids of things already crushed. Keeps the log to one line
        /// each and the work to one pass.</summary>
        static Dictionary<int, bool> _zerbrochen = new Dictionary<int, bool>();

        static Type _colliderType;
        static PropertyInfo _colliderEnabled;
        static bool _physLookedUp;

        /// <summary>
        /// Is this hit a knee-high fence, a post, a bit of road junk - the kind
        /// of thing twelve tons goes through rather than around?
        ///
        /// WHY THIS EXISTS. The map is full of small obstacles, and a driver
        /// that treats every one of them as a wall does two wrong things at
        /// once: it steers off the road for a fence it would have flattened,
        /// and having steered off the road it gets stuck for real. Every FREE
        /// event in the run of 2026-08-30 - waypoints 67, 68, 71, 72, 73, 74 -
        /// began that way.
        ///
        /// The test is SIZE and nothing else, because size is the one thing
        /// that means the same for every prop on the map. It walks up from the
        /// hit object while the whole candidate still fits inside CrushHeight
        /// and CrushWidth, so a fence hit on one plank gives up the whole
        /// fence and not just that plank. A candidate that grows too large
        /// ends the walk, which is what keeps a house from being crushed
        /// because a doorstep was hit.
        ///
        /// What is NEVER crushed, whatever its size: anything belonging to a
        /// player, an NPC, an animal or a vehicle. Those are small and would
        /// pass the size test easily, and taking a man's collider away is not
        /// a driving aid, it is a hole in the game.
        /// </summary>
        static Transform Kleinkram(GameObject go)
        {
            if (!RevivalPlugin.CfgPatrolCrush.Value || go == null) return null;

            int id = go.GetInstanceID();
            Transform found;
            if (_klein.TryGetValue(id, out found)) return found;
            if (_klein.Count > 4096) _klein.Clear();

            found = Suchen(go);
            _klein[id] = found;
            return found;
        }

        /// <summary>
        /// The largest thing around this hit that is still small enough to
        /// drive through, or null.
        ///
        /// THE WALK UP STOPS AT A CONTAINER. Every step up is an
        /// `Ausmasse` over a bigger subtree, and a map has objects whose
        /// parent is a bin holding a thousand props. Two things keep that
        /// from being the next E-032: a parent with more than
        /// `ContainerAb` children is taken as a bin and ends the walk before
        /// it is measured, and `Ausmasse` stops counting the moment the box
        /// is already too big. Nothing here runs twice for the same object -
        /// the answer is cached in `_klein`.
        /// </summary>
        static Transform Suchen(GameObject go)
        {
            if (Lebendig(go.transform)) return null;

            float hoch = Mathf.Max(0.1f, RevivalPlugin.CfgPatrolCrushHeight.Value);
            float breit = Mathf.Max(0.1f, RevivalPlugin.CfgPatrolCrushWidth.Value);

            Transform best = null;
            Transform t = go.transform;
            for (int i = 0; i < 4 && t != null; i++)
            {
                if (!Passt(t, hoch, breit)) break;
                best = t;
                Transform hoeher = t.parent;
                if (hoeher == null) break;
                if (hoeher.childCount > ContainerAb) break;
                t = hoeher;
            }
            return best;
        }

        /// <summary>Direct children from which a transform is taken for a bin
        /// of props rather than one prop. A fence has planks, a car has
        /// wheels; nothing that is ONE thing has two dozen children.</summary>
        const int ContainerAb = 24;

        /// <summary>
        /// Does everything under this transform fit inside the two limits?
        /// False also when there is nothing measurable - a bare collider with
        /// no mesh is a thing we cannot size up, and a thing we cannot size up
        /// is a thing we do not crush.
        /// </summary>
        static bool Passt(Transform t, float hoch, float breit)
        {
            Renderer[] rs = t.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            Bounds b = new Bounds();
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null || !rs[i].enabled) continue;
                if (!any) { b = rs[i].bounds; any = true; }
                else b.Encapsulate(rs[i].bounds);
                Vector3 s = b.size;
                if (s.y > hoch || s.x > breit || s.z > breit) return false;
            }
            return any;
        }

        /// <summary>
        /// Is this part of something alive or something driven?
        ///
        /// Walked UP a few levels with `GetComponent`, not down from the root
        /// with `GetComponentInChildren`. The root of a scene prop can be a
        /// container holding half the map, and searching that five times per
        /// obstacle is exactly the shape of mistake that put the driver at
        /// 3 FPS once (E-032). A vehicle, an NPC and a player all carry their
        /// marker component within a few levels of any collider they own.
        /// </summary>
        static bool Lebendig(Transform t)
        {
            Type[] typen = Marker();
            for (int hoehe = 0; hoehe < 6 && t != null; hoehe++)
            {
                for (int i = 0; i < typen.Length; i++)
                {
                    if (typen[i] == null) continue;
                    if (t.GetComponent(typen[i]) != null) return true;
                }
                t = t.parent;
            }
            return false;
        }

        static Type[] _marker;

        static Type[] Marker()
        {
            if (_marker != null) return _marker;
            _marker = new Type[Unantastbar.Length];
            for (int i = 0; i < Unantastbar.Length; i++)
                _marker[i] = RevivalPlugin.TypeByName(Unantastbar[i]);
            return _marker;
        }

        /// <summary>Types whose objects are never crushed. Names, not types:
        /// the plugin references no Assembly-CSharp.</summary>
        static readonly string[] Unantastbar = new string[] {
            "NPC_AI2", "PlayerNetworkController", "VehicleGameSystem",
            "Animal_AI", "ItemSpawned",
        };

        /// <summary>
        /// Take the colliders off, and only the colliders. The prop stays
        /// where it is and stays visible - a fence that disappears is a
        /// glitch, a fence a BTR drives through is a BTR driving through a
        /// fence. Local only: the patrol runs on the master client, the other
        /// machines never had a reason to steer around it.
        /// </summary>
        static void Zerbrechen(Unit u, Transform was)
        {
            if (was == null) return;
            int id = was.GetInstanceID();
            if (_zerbrochen.ContainsKey(id)) return;
            _zerbrochen[id] = true;

            if (!PhysLookUp()) return;
            try
            {
                Component[] cs = was.GetComponentsInChildren(_colliderType, true);
                int n = 0;
                for (int i = 0; i < cs.Length; i++)
                {
                    if (cs[i] == null) continue;
                    object on = _colliderEnabled.GetValue(cs[i], null);
                    if (on is bool && !(bool)on) continue;
                    _colliderEnabled.SetValue(cs[i], false, null);
                    n++;
                }
                if (n > 0)
                    RevivalPlugin.L.LogInfo("Patrol: drove through \"" + was.name
                        + "\" on " + u.Route.Name + " - " + n + " collider(s) off.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Patrol: \"" + was.name + "\" not "
                    + "crushed - " + ex.Message);
            }
        }

        static bool PhysLookUp()
        {
            if (_physLookedUp) return _colliderEnabled != null;
            _physLookedUp = true;
            _colliderType = RevivalPlugin.TypeByName("UnityEngine.Collider");
            if (_colliderType != null)
                _colliderEnabled = _colliderType.GetProperty("enabled",
                    BindingFlags.Public | BindingFlags.Instance);
            if (_colliderEnabled == null)
                RevivalPlugin.L.LogWarning("Patrol: UnityEngine.Collider.enabled not "
                    + "found - patrols cannot drive through anything and will "
                    + "steer around every fence on the map.");
            return _colliderEnabled != null;
        }

        // =====================================================================
        //  The escalation, stage by stage
        // =====================================================================

        static void Escalate(Unit u, ref float gas, ref float brake, ref float steer,
                             float kmh, float forward, Vector3 pos)
        {
            float stuckFor = RevivalPlugin.CfgPatrolStuck.Value;
            float ramAfter = RevivalPlugin.CfgPatrolRam.Value;
            float freeAfter = RevivalPlugin.CfgPatrolFree.Value;

            if (u.Stuck <= 0f)
            {
                if (u.Stage != 0)
                {
                    RevivalPlugin.L.LogInfo("Patrol: " + u.Route.Name
                        + " is moving again, back to driving.");
                    u.Stage = 0;
                    u.StageTime = 0f;
                }
                return;
            }

            if (u.Stuck < stuckFor) return;         // still just avoiding

            if (u.Stuck >= freeAfter)
            {
                Free(u, pos);
                return;
            }

            int want = u.Stuck >= ramAfter ? 2 : 1;
            if (want != u.Stage)
            {
                u.Stage = want;
                u.StageTime = 0f;
                RevivalPlugin.L.LogInfo("Patrol: " + u.Route.Name + " stuck for "
                    + u.Stuck.ToString("0.0") + " s at waypoint " + u.Next
                    + " - " + (want == 1 ? "backing up" : "going through it") + ".");
            }
            u.StageTime += Time.fixedDeltaTime;

            if (u.Stage == 1)
            {
                // Reverse IS the brake: GearBox shifts back when brakeInput is
                // over 0.1 below 1 m/s forward. Steer the other way so the nose
                // comes off whatever it is against.
                gas = 0f;
                brake = 1f;
                steer = -steer;
                // Once actually rolling backwards, ease off so it does not
                // reverse into the ditch on the other side.
                if (forward < -1.5f) brake = 0.45f;
            }
            else
            {
                // Full throttle, straight ahead, avoidance off. A BTR against a
                // fence is the fence's problem.
                gas = 1f;
                brake = 0f;
                steer *= 0.3f;
            }
        }

        /// <summary>Last resort: put the vehicle back on the route. Ugly, and
        /// it is the reason a patrol never dies in a ditch. Every one of these
        /// names a waypoint that wants fixing.</summary>
        static void Free(Unit u, Vector3 pos)
        {
            Route r = u.Route;
            int n = r.P.Count;
            int to = (u.Next + 1) % n;

            Vector3 target = r.P[to].Pos + Vector3.up * 1.5f;
            Vector3 ahead = r.P[(to + 1) % n].Pos - r.P[to].Pos;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.0001f) ahead = Vector3.forward;

            Stop(u.Body);
            u.Car.transform.position = target;
            u.Car.transform.rotation = Quaternion.LookRotation(ahead.normalized, Vector3.up);

            u.Frees++;
            u.Next = to;
            u.Stuck = 0f;
            u.Stage = 0;
            u.StageTime = 0f;

            RevivalPlugin.L.LogWarning("Patrol: FREE on " + r.Name + " - stuck at "
                + pos + " near waypoint " + u.Next + ", lifted onto waypoint " + to
                + ". That waypoint wants fixing. (" + u.Frees + " so far)");
        }

        // =====================================================================
        //  The gunner
        // =====================================================================

        /// <summary>
        /// The gun of a patrol vehicle, aimed by a state machine instead of by
        /// a mouse. It is the SAME gun the player mans - the same turret
        /// transforms, the same two value profiles out of [Turret] and [Tank],
        /// the same tracer - and this class adds only the three things a human
        /// brings: it looks for a target, it turns the barrel, and it misses.
        ///
        /// HOW IT MISSES, AND WHY EXACTLY LIKE THIS (read out of the game,
        /// 2026-08-30: NPC_AI2::ShootToTarget, NPC_AI2::CalcChancesToHit,
        /// NPC_FirearmWeaponController::GetSqrDistanceModifier)
        ///
        ///   The game's own NPCs do not spray a cone. They roll ONE chance per
        ///   shot and then displace the aim point by whole metres:
        ///
        ///       chance = base by target stance (0.5 crouched to 1.0 standing)
        ///                minus the distance loss, clamped to 0..1
        ///       loss   = 0 inside the weapon's effective range, rising along
        ///                a cosine to 1 at maximum range, and a loss over 0.95
        ///                sets the chance to zero outright
        ///       offset = 0.2 m under 30 m - practically a hit
        ///                a rolled hit:  up to 2.5 m
        ///                a rolled miss: 3 m
        ///
        ///   That is the "factor common among the NPCs" this gun uses. THREE
        ///   things are deliberately different. The offset here goes in a
        ///   random DIRECTION around the aim point instead of into +x and +y
        ///   together, because the game's version puts every miss of every NPC
        ///   on the same diagonal. A rolled hit is displaced by 0.25 m, not by
        ///   2.5: the game gets away with the large number because
        ///   NPC_FirearmWeaponController::FireTo decides the damage itself,
        ///   while this gun is a raycast - at 150 m a 2.5 m offset would miss
        ///   a man every time and the chance above would mean nothing.
        ///
        ///   And the third, added on 2026-08-30 after the user reported a
        ///   patrol as "an 80 percent death sentence": the free ring under
        ///   30 m is GONE as a constant. It is `GunPointBlank` now, 12 m by
        ///   default, and the chance is rolled inside it like anywhere else.
        ///   That one line was the whole difficulty problem - a road vehicle
        ///   and a man on foot end up inside 30 m of each other in every
        ///   fight, and inside that ring the gun was perfect no matter what
        ///   GunAccuracy said.
        ///
        /// COST. A patrol with no player within GunRange does nothing at all
        /// beyond one square distance per player per half second, and that
        /// player list is fetched once for every vehicle on the road. The line
        /// of sight ray is cast for ONE candidate, not for all of them, and
        /// only twice a second. The turret is turned only while there is a
        /// target or the barrel is not yet back at rest.
        /// </summary>
        static class Gun
        {
            /// <summary>Degrees between barrel and target inside which the
            /// gun considers itself laid and pulls the trigger.</summary>
            const float FireWithin = 2.5f;

            /// <summary>Seconds between two target scans of one vehicle. The
            /// scan is the expensive half - it casts a ray.</summary>
            const float ScanEvery = 0.5f;

            // ------------------------------------------------------- targets

            class Spieler
            {
                public Transform Tr;
                public Component States;     // PlayerStatesController, may be null
            }

            static List<Spieler> _players = new List<Spieler>();
            static float _nextRefresh;

            static Type _ngs, _statesType;
            static PropertyInfo _instance;
            static FieldInfo _networkPlayers, _stateField;
            static object _death;
            static bool _lookedUp;

            /// <summary>
            /// The list of players, refreshed at most twice a second and
            /// shared by every patrol vehicle.
            ///
            /// NetworkGameServer.Instance.NetworkPlayers is the game's own
            /// list of player GameObjects - the same one
            /// NPC_Settlement::PlayersDistanceControll walks. Reading it costs
            /// two field accesses; FindObjectsOfType, which would be the
            /// obvious way, walks every object in the scene and is the kind of
            /// call that put the driver at 3 FPS once already (E-032).
            /// </summary>
            static void Refresh()
            {
                if (Time.time < _nextRefresh) return;
                _nextRefresh = Time.time + ScanEvery;
                _players.Clear();

                if (!LookUp()) return;
                object server = _instance.GetValue(null, null);
                if (server == null) return;
                IList list = _networkPlayers.GetValue(server) as IList;
                if (list == null) return;

                for (int i = 0; i < list.Count; i++)
                {
                    GameObject go = list[i] as GameObject;
                    if (go == null) continue;
                    Spieler s = new Spieler();
                    s.Tr = go.transform;
                    if (_statesType != null) s.States = go.GetComponent(_statesType);
                    _players.Add(s);
                }
            }

            static bool LookUp()
            {
                if (_lookedUp) return _networkPlayers != null;
                _lookedUp = true;

                _ngs = RevivalPlugin.TypeByName("NetworkGameServer");
                if (_ngs == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol gun: NetworkGameServer not found - "
                        + "the gun has no way to see a player and stays quiet.");
                    return false;
                }
                _instance = _ngs.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static);
                _networkPlayers = AccessTools.Field(_ngs, "NetworkPlayers");
                if (_instance == null || _networkPlayers == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol gun: NetworkGameServer.Instance or "
                        + ".NetworkPlayers is missing - the gun stays quiet.");
                    _networkPlayers = null;
                    return false;
                }

                // Dead players are not targets. The state lives on
                // PlayerStatesController; a missing type is not fatal, it only
                // means a corpse is shot at once more.
                _statesType = RevivalPlugin.TypeByName("PlayerStatesController");
                if (_statesType != null)
                {
                    _stateField = AccessTools.Field(_statesType, "_characterState");
                    if (_stateField != null && _stateField.FieldType.IsEnum)
                    {
                        try { _death = Enum.Parse(_stateField.FieldType, "Death"); }
                        catch { _death = null; }
                    }
                }
                return true;
            }

            /// <summary>
            /// Is the world up - is there a player in the game's own list?
            /// Empty in the menu, empty in the loading screen, one entry the
            /// moment the local player exists. The automatic patrol start asks
            /// this before it puts anything down; the answer is at most half a
            /// second old, because `Refresh` is what limits it.
            /// </summary>
            internal static bool WeltLaeuft()
            {
                Refresh();
                for (int i = 0; i < _players.Count; i++)
                    if (_players[i].Tr != null) return true;
                return false;
            }

            static bool Lebt(Spieler s)
            {
                if (s.States == null || _stateField == null || _death == null) return true;
                return !_death.Equals(_stateField.GetValue(s.States));
            }

            // --------------------------------------------------------- frame

            public static void Tick(List<Unit> units)
            {
                if (!RevivalPlugin.CfgPatrolGun.Value) return;
                if (units.Count == 0) return;
                Refresh();

                for (int i = 0; i < units.Count; i++)
                {
                    Unit u = units[i];
                    if (!u.Armed || u.Died > 0f || u.Car == null) continue;
                    if (u.Turrets.Length == 0) continue;
                    Einer(u);
                }
            }

            /// <summary>Turret objects of one vehicle. Four of them - the
            /// LODGroup swaps between them, and a barrel that only turns on
            /// LOD0 stands still as soon as the player steps back.</summary>
            public static void Collect(Unit u)
            {
                List<Transform> found = new List<Transform>();
                Transform[] all = u.Car.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                    if (all[i].name == "turret") found.Add(all[i]);
                u.Turrets = found.ToArray();
                u.TurretRend = null;
                for (int i = 0; i < u.Turrets.Length; i++)
                {
                    Renderer r = u.Turrets[i].GetComponent<Renderer>();
                    if (r != null) { u.TurretRend = r; break; }
                }
            }

            static void Einer(Unit u)
            {
                float dt = Time.deltaTime;

                if (Time.time >= u.NextLook)
                {
                    u.NextLook = Time.time + ScanEvery;
                    Suchen(u);
                }

                if (u.Target == null)
                {
                    Ruhen(u, dt);
                    return;
                }

                u.Held += dt;

                Vector3 ziel = Zielpunkt(u.Target);
                if (!Winkel(u, ziel, out u.Yaw, out u.Pitch)) return;
                Drehen(u, dt);

                if (u.Held < RevivalPlugin.CfgPatrolGunNotice.Value) return;
                if (Time.time < u.NextShot) return;
                if (Vector3.Angle(Rohrrichtung(u), ziel - Muendung(u)) > FireWithin) return;

                Nachladen(u);
                Schiessen(u, ziel);
            }

            /// <summary>
            /// When the gun may fire again. A fast gun fires a BURST and then
            /// pauses; without that a BTR at 0.12 s a shot empties a man in a
            /// tenth of a second and there is nothing to react to. A gun that
            /// reloads for a second or more - the tank - is its own pause and
            /// is left alone.
            /// </summary>
            static void Nachladen(Unit u)
            {
                float delay = Ladezeit(u);
                int burst = delay >= 1f ? 1 : Mathf.Max(1, RevivalPlugin.CfgPatrolGunBurst.Value);

                u.Burst++;
                if (u.Burst < burst)
                {
                    u.NextShot = Time.time + delay;
                    return;
                }
                u.Burst = 0;
                u.NextShot = Time.time + Mathf.Max(delay,
                    RevivalPlugin.CfgPatrolGunBurstPause.Value);
            }

            /// <summary>Barrel back to straight ahead. Costs nothing once it
            /// is there, which is the normal case.</summary>
            static void Ruhen(Unit u, float dt)
            {
                if (Mathf.Abs(u.Yaw) < 0.5f && Mathf.Abs(u.Pitch) < 0.5f) return;
                float step = Drehgeschwindigkeit(u) * dt;
                u.Yaw = Mathf.MoveTowards(u.Yaw, 0f, step);
                u.Pitch = Mathf.MoveTowards(u.Pitch, 0f, step);
                Drehen(u, dt);
            }

            // -------------------------------------------------------- target

            /// <summary>
            /// Pick a target, or keep the one we have. Cheap test first:
            /// distance, then the line of sight, and the ray is cast for one
            /// candidate only.
            /// </summary>
            static void Suchen(Unit u)
            {
                float range = RevivalPlugin.CfgPatrolGunRange.Value;
                Vector3 from = Muendung(u);

                // Keep the current target while it is alive, near and visible.
                if (u.Target != null)
                {
                    bool weg = Flat(u.Target.position - from) > range * 1.15f;
                    if (!weg && Sicht(u, u.Target))
                    {
                        u.Lost = 0f;
                        return;
                    }
                    u.Lost += ScanEvery;
                    if (!weg && u.Lost < RevivalPlugin.CfgPatrolGunForget.Value) return;

                    RevivalPlugin.L.LogInfo("Patrol gun: target lost on " + u.Route.Name
                        + " after " + u.Held.ToString("0.0") + " s.");
                    u.Target = null;
                    u.Held = 0f;
                    u.Lost = 0f;
                    u.Burst = 0;
                }

                Transform best = null;
                float bestDist = 0f;
                for (int i = 0; i < _players.Count; i++)
                {
                    Spieler s = _players[i];
                    if (s.Tr == null) continue;
                    float d = Flat(s.Tr.position - from);
                    if (d > range) continue;
                    if (!Lebt(s)) continue;
                    if (best != null && d >= bestDist) continue;
                    best = s.Tr;
                    bestDist = d;
                }
                if (best == null) return;
                if (!Sicht(u, best)) return;

                u.Target = best;
                u.Held = 0f;
                u.Lost = 0f;
                RevivalPlugin.L.LogInfo("Patrol gun: " + (u.Tank ? "tank" : "BTR")
                    + " on " + u.Route.Name + " has a target at "
                    + bestDist.ToString("0") + " m.");
            }

            /// <summary>One ray from the muzzle to the target's chest. Hits on
            /// our own vehicle are stepped over - the BTR's muzzle sits inside
            /// its own bow plate (RE 18).</summary>
            static bool Sicht(Unit u, Transform ziel)
            {
                Vector3 from = Muendung(u);
                Vector3 to = Zielpunkt(ziel);
                Vector3 dir = to - from;
                float dist = dir.magnitude;
                if (dist < 0.5f) return true;
                dir /= dist;

                Vector3 point;
                GameObject hit = Strahl(u, from, dir, dist, out point);
                if (hit == null) return true;           // nothing in between
                // The target itself is allowed to be in the way of itself.
                if (hit.transform.IsChildOf(ziel)) return true;
                return (to - point).sqrMagnitude < 2.25f;
            }

            /// <summary>Chest height. The transform of a player sits at his
            /// feet, and a gun that aims there shoots the ground in front of
            /// him at any distance.</summary>
            static Vector3 Zielpunkt(Transform t)
            {
                return t.position + Vector3.up * 1.1f;
            }

            // --------------------------------------------------------- aiming

            /// <summary>
            /// World point to the turret's own two angles. The inverse of
            /// Turret.LocalRotationFor: in turret space -Y is the barrel and
            /// +Z is up, so a local direction d means pitch = asin(d.z) and
            /// yaw = atan2(-d.x, -d.y).
            /// </summary>
            static bool Winkel(Unit u, Vector3 world, out float yaw, out float pitch)
            {
                yaw = u.Yaw;
                pitch = u.Pitch;
                Transform turm = u.Turrets[0];
                if (turm == null) return false;
                Transform parent = turm.parent;
                if (parent == null) return false;

                Vector3 d = parent.InverseTransformPoint(world) - turm.localPosition;
                if (d.sqrMagnitude < 0.0001f) return false;
                d.Normalize();

                pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(d.z, -1f, 1f)) * Mathf.Rad2Deg,
                                    Turret.PitchMin(u.Tank),
                                    Turret.PitchMax(u.Tank));
                yaw = Mathf.Atan2(-d.x, -d.y) * Mathf.Rad2Deg;
                return true;
            }

            static void Drehen(Unit u, float dt)
            {
                Quaternion want = Turret.LocalRotationFor(u.Yaw, u.Pitch);
                float step = Drehgeschwindigkeit(u) * dt;
                for (int i = 0; i < u.Turrets.Length; i++)
                {
                    if (u.Turrets[i] == null) continue;
                    u.Turrets[i].localRotation =
                        Quaternion.RotateTowards(u.Turrets[i].localRotation, want, step);
                }
                float actualYaw, actualPitch;
                if (Turret.AnglesFor(u.Turrets[0], out actualYaw, out actualPitch))
                    Turret.Net.Publish(u.Car.transform, actualYaw, actualPitch);
            }

            static Vector3 Rohrrichtung(Unit u)
            {
                return u.Turrets[0].TransformDirection(new Vector3(0f, -1f, 0f)).normalized;
            }

            /// <summary>Muzzle out of the WORLD bounds of the turret renderer,
            /// exactly as the manned gun does it (Turret.Muzzle).</summary>
            static Vector3 Muendung(Unit u)
            {
                Vector3 dir = Rohrrichtung(u);
                if (u.TurretRend == null) return u.Turrets[0].position + dir;
                Bounds b = u.TurretRend.bounds;
                float reach = Vector3.Dot(b.extents, new Vector3(
                    Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z)));
                return b.center + dir * (reach + 0.5f);
            }

            // -------------------------------------------------------- firing

            static void Schiessen(Unit u, Vector3 ziel)
            {
                Vector3 from = Muendung(u);
                float dist = Vector3.Distance(from, ziel);
                Vector3 aim = ziel + Streuung(u, dist);

                Vector3 dir = aim - from;
                if (dir.sqrMagnitude < 0.0001f) return;
                dir.Normalize();

                VehicleShotSound.Play(from, u.Tank);
                Turret.Net.PublishShot(from, u.Tank);

                float range = Reichweite(u);
                Vector3 impact;
                GameObject struck = Strahl(u, from, dir, range, out impact);
                Vector3 ende = struck == null ? from + dir * range : impact;

                Spur(u, from + dir * 2f, ende);
                u.Shots++;
                if (struck == null) return;

                if (u.Tank && RevivalPlugin.CfgTankExplosion.Value)
                {
                    try
                    {
                        // NOT the player's shell. [Tank] is 1600 damage in a
                        // 16 m radius, which is artillery and is meant to be -
                        // but a blast does not care how well the gun was
                        // pointed, so an AI firing it kills with no counter
                        // and GunAccuracy cannot soften it. The AI gets its
                        // own two numbers; a player in the same tank keeps the
                        // artillery.
                        float scha = RevivalPlugin.CfgPatrolShellDamage.Value;
                        float rad = RevivalPlugin.CfgPatrolShellRadius.Value;
                        if (scha <= 0f) scha = RevivalPlugin.CfgTankExplosionDamage.Value;
                        if (rad <= 0f) rad = RevivalPlugin.CfgTankExplosionRadius.Value;
                        RocketHook.Detonate(impact - dir * 0.15f, scha, rad, 3f);
                    }
                    catch (Exception ex)
                    {
                        RevivalPlugin.L.LogError("Patrol gun: impact without explosion - "
                            + ex.Message);
                    }
                }

                if (Schaden(u, struck, Schadenswert(u), impact, from))
                {
                    u.Hits++;
                    RevivalPlugin.L.LogInfo("Patrol gun: hit at "
                        + dist.ToString("0") + " m (" + u.Hits + " of " + u.Shots + ").");
                }
            }

            /// <summary>
            /// The miss. See the class comment for where the numbers come
            /// from - this is the game's own model with the direction made
            /// random, the hit case tightened, and ONE deliberate departure.
            ///
            /// THE DEPARTURE (2026-08-30). The game's model gives every shot
            /// under 30 m a free pass: offset 0.2 m, no roll, accuracy not
            /// consulted. That single line is why a patrol read as an 80
            /// percent death sentence - every fight with a road vehicle
            /// happens inside 30 m sooner or later, and inside it the gun was
            /// perfect no matter what GunAccuracy said. So the free ring is a
            /// setting now (`GunPointBlank`, 12 m) and the roll happens at
            /// EVERY distance. Point blank a rolled miss still lands close,
            /// because a man standing at arm's length from a BTR should not be
            /// safe either.
            /// </summary>
            static Vector3 Streuung(Unit u, float dist)
            {
                float weit = Mathf.Max(1f, RevivalPlugin.CfgPatrolGunRange.Value);
                float nah = Mathf.Clamp(RevivalPlugin.CfgPatrolGunEffective.Value, 1f, weit);

                float loss;
                if (dist <= nah) loss = 0f;
                else if (dist >= weit) loss = 1f;
                else loss = 0.5f * (1f - Mathf.Cos(Mathf.PI * (dist - nah) / (weit - nah)));

                float chance = loss > 0.95f
                    ? 0f
                    : Mathf.Clamp01((1f - loss) * RevivalPlugin.CfgPatrolGunAccuracy.Value);

                bool nahdran = dist < RevivalPlugin.CfgPatrolGunPointBlank.Value;
                float betrag;
                if (UnityEngine.Random.value <= chance) betrag = nahdran ? 0.2f : 0.25f;
                else betrag = nahdran ? 1.1f : 3f;

                // A direction perpendicular to the shot, so a miss goes past
                // the man or over him and never falls short and hits anyway.
                Vector3 achse = Rohrrichtung(u);
                Vector3 seite = Vector3.Cross(Vector3.up, achse);
                if (seite.sqrMagnitude < 0.0001f) seite = Vector3.right;
                seite.Normalize();
                Vector3 hoch = Vector3.Cross(achse, seite).normalized;
                float a = UnityEngine.Random.value * Mathf.PI * 2f;
                return (seite * Mathf.Cos(a) + hoch * Mathf.Sin(a)) * betrag;
            }

            /// <summary>Ray that steps over our own vehicle, up to four
            /// times. Same reason as Turret.RaycastPastVehicle.</summary>
            static GameObject Strahl(Unit u, Vector3 from, Vector3 dir, float range,
                                     out Vector3 point)
            {
                point = Vector3.zero;
                Vector3 start = from;
                float rest = range;
                for (int i = 0; i < 4 && rest > 0f; i++)
                {
                    Vector3 hit;
                    GameObject go = Turret.RaycastObject(start, dir, rest, out hit);
                    if (go == null) return null;
                    if (u.Car == null || !go.transform.IsChildOf(u.Car.transform))
                    {
                        point = hit;
                        return go;
                    }
                    rest -= Vector3.Distance(start, hit) + 0.25f;
                    start = hit + dir * 0.25f;
                }
                return null;
            }

            static void Spur(Unit u, Vector3 von, Vector3 bis)
            {
                try
                {
                    List<Vector3> bahn = new List<Vector3>();
                    bahn.Add(von);
                    bahn.Add(bis);
                    if (u.Tank)
                    {
                        RocketHook.SpawnTracer(bahn, 1.20f, 0.50f, SpurHof, SpurHof, 0.30f);
                        RocketHook.SpawnTracer(bahn, 0.44f, 0.17f, SpurKern, SpurEnde, 0.55f);
                    }
                    else
                    {
                        RocketHook.SpawnTracer(bahn, 0.34f, 0.14f, SpurHof, SpurHof, 0.10f);
                        RocketHook.SpawnTracer(bahn, 0.13f, 0.05f, SpurKern, SpurEnde, 0.18f);
                    }
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError("Patrol gun: tracer - " + ex.Message);
                }
            }

            static readonly Color SpurKern = new Color(1.00f, 0.96f, 0.78f, 1.0f);
            static readonly Color SpurEnde = new Color(1.00f, 0.62f, 0.20f, 1.0f);
            static readonly Color SpurHof = new Color(1.00f, 0.38f, 0.10f, 1.0f);

            // -------------------------------------------------------- damage

            static Type _pncType;
            static PropertyInfo _photonPlayer;
            static MethodInfo _getView, _rpc;
            static bool _damageLookedUp;

            /// <summary>
            /// Damage on the struck object, by the game's own three roads.
            ///
            /// A PLAYER is hit over the wire, not by hand: FireOneShot does
            /// Extensions.GetPhotonView(go).RPC("PlayerApplyDamage", victim,
            /// damage, partType, 4, hitPoint, shooterPosition), and health
            /// lives on the victim's own client. Calling the method directly
            /// on our copy of a remote player would change a number nobody
            /// reads.
            /// </summary>
            static bool Schaden(Unit shooter, GameObject struck, float damage,
                                Vector3 point, Vector3 from)
            {
                if (PanzerSchaden(shooter, struck)) return true;
                if (SpielerSchaden(struck, damage, point, from)) return true;
                if (Turret.TryDamage(struck, "NPC_AI2", "ApplyDamage", damage)) return true;
                if (Turret.TryDamage(struck, "Animal_AI", "NetworkApplyDamage", damage)) return true;
                return false;
            }

            /// <summary>
            /// APC armour damage is separate from anti-personnel damage. The
            /// T-72 has 2000 durability and VehicleGameSystem accepts one hit
            /// every 0.3 seconds; a three-shot 0.12 second burst therefore
            /// becomes one configured armour hit. Calling the game's own
            /// ApplyDamage keeps durability, smoke, destruction and Photon
            /// synchronization on the established vehicle path.
            /// </summary>
            static bool PanzerSchaden(Unit shooter, GameObject struck)
            {
                if (shooter == null || shooter.Tank || struck == null) return false;
                float damage = RevivalPlugin.CfgPatrolGunTankDamage.Value;
                if (damage <= 0f) return false;

                Type type = RevivalPlugin.TypeByName("VehicleGameSystem");
                Component vehicle = type == null ? null : struck.GetComponentInParent(type);
                if (vehicle == null || !Tank.IstPanzer(vehicle.transform)) return false;

                MethodInfo apply = AccessTools.Method(type, "ApplyDamage",
                    new Type[] { typeof(float), typeof(int) }, null);
                if (apply == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol gun: VehicleGameSystem.ApplyDamage "
                        + "is missing - APC fire cannot damage a tank.");
                    return true;
                }

                float before = GetFloat(vehicle, "Durability", -1f);
                apply.Invoke(vehicle, new object[] { damage, 10 });
                float after = GetFloat(vehicle, "Durability", before);
                if (after < before)
                    RevivalPlugin.L.LogInfo("Patrol gun: APC armour hit "
                        + before.ToString("0") + " -> " + after.ToString("0") + ".");
                return true;
            }

            static bool SpielerSchaden(GameObject struck, float damage, Vector3 point,
                                       Vector3 from)
            {
                if (!DamageLookUp()) return false;
                Component pnc = struck.GetComponentInParent(_pncType);
                if (pnc == null) return false;

                object victim = _photonPlayer.GetValue(pnc, null);
                if (victim == null) return false;
                object view = _getView.Invoke(null, new object[] { pnc.gameObject });
                if (view == null) return false;

                // partType 0 is the body, 4 is the damage kind FireOneShot
                // passes for a bullet. Both were read out of its IL.
                _rpc.Invoke(view, new object[] {
                    "PlayerApplyDamage", victim,
                    new object[] { damage, 0, 4, point, from } });
                return true;
            }

            static bool DamageLookUp()
            {
                if (_damageLookedUp) return _rpc != null;
                _damageLookedUp = true;

                _pncType = RevivalPlugin.TypeByName("PlayerNetworkController");
                Type ext = RevivalPlugin.TypeByName("Extensions");
                Type viewType = RevivalPlugin.TypeByName("PhotonView");
                if (_pncType == null || ext == null || viewType == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol gun: PlayerNetworkController, "
                        + "Extensions or PhotonView not found - the gun cannot hurt "
                        + "a player.");
                    return false;
                }

                _photonPlayer = _pncType.GetProperty("GetPhotonPlayer",
                    BindingFlags.Public | BindingFlags.Instance);
                _getView = AccessTools.Method(ext, "GetPhotonView",
                    new Type[] { typeof(GameObject) }, null);

                MethodInfo[] ms = viewType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "RPC") continue;
                    ParameterInfo[] ps = ms[i].GetParameters();
                    if (ps.Length != 3) continue;
                    if (ps[0].ParameterType != typeof(string)) continue;
                    if (ps[2].ParameterType != typeof(object[])) continue;
                    if (ps[1].ParameterType.Name != "PhotonPlayer") continue;
                    _rpc = ms[i];
                    break;
                }

                if (_photonPlayer == null || _getView == null || _rpc == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol gun: the player damage road is "
                        + "incomplete (GetPhotonPlayer " + (_photonPlayer != null)
                        + ", GetPhotonView " + (_getView != null)
                        + ", RPC " + (_rpc != null) + ") - the gun stays harmless "
                        + "to players.");
                    _rpc = null;
                    return false;
                }
                return true;
            }

            // ------------------------------------------------- value profile

            static float Schadenswert(Unit u)
            {
                float own = RevivalPlugin.CfgPatrolGunDamage.Value;
                if (own > 0f) return own;
                return u.Tank ? RevivalPlugin.CfgTankDamage.Value
                              : RevivalPlugin.CfgTurretDamage.Value;
            }

            static float Reichweite(Unit u)
            {
                return u.Tank ? RevivalPlugin.CfgTankRange.Value
                              : RevivalPlugin.CfgTurretRange.Value;
            }

            static float Ladezeit(Unit u)
            {
                return u.Tank ? RevivalPlugin.CfgTankDelay.Value
                              : RevivalPlugin.CfgTurretDelay.Value;
            }

            static float Drehgeschwindigkeit(Unit u)
            {
                return u.Tank ? RevivalPlugin.CfgTankTurnSpeed.Value
                              : RevivalPlugin.CfgTurretTurnSpeed.Value;
            }
        }

        // =====================================================================
        //  The recorder
        // =====================================================================

        static void ToggleRecording()
        {
            _recording = !_recording;
            _haveLastRecorded = false;
            _nextRecord = 0f;
            if (_recording)
            {
                Load(false);
                RevivalPlugin.L.LogInfo("Patrol: recording route \""
                    + RevivalPlugin.CfgPatrolRoute.Value + "\" - a waypoint every "
                    + RevivalPlugin.CfgPatrolRecordSeconds.Value.ToString("0.#")
                    + " s. F5 again to stop.");
                Turret.Hinweis("Recording " + RevivalPlugin.CfgPatrolRoute.Value, 3f);
            }
            else
            {
                Route r = Active();
                int count = r == null ? 0 : r.P.Count;
                RevivalPlugin.L.LogInfo("Patrol: recording stopped, "
                    + RevivalPlugin.CfgPatrolRoute.Value + " has " + count + " waypoints.");
                Turret.Hinweis("Recorded " + count + " waypoints", 3f);
            }
        }

        /// <summary>
        /// A waypoint every `RecordSeconds`, which is the clock and not the
        /// tape measure. The recorder counted METRES until 2026-08-30 and the
        /// user asked for the clock: a route is recorded by driving it, and at
        /// a steady speed the two are the same thing - three seconds at
        /// 45 km/h is 37 m. Where they differ is the corner, where the driver
        /// slows down, and there the clock puts the waypoints closer
        /// together - which is exactly where a route wants them.
        ///
        /// `MinStep` is the one thing left of the metres: without it a
        /// recorder left running while its owner stands still writes the same
        /// point every three seconds until the file is full.
        /// </summary>
        static void RecordWhileWalking()
        {
            if (Time.time < _nextRecord) return;
            Vector3 p = Where();
            if (p == Vector3.zero) return;
            if (_haveLastRecorded)
            {
                Vector3 d = p - _lastRecorded; d.y = 0f;
                if (d.sqrMagnitude < MinStep * MinStep) return;
            }
            _nextRecord = Time.time
                        + Mathf.Max(0.2f, RevivalPlugin.CfgPatrolRecordSeconds.Value);
            RecordHere(false);
        }

        static void RecordHere(bool loud)
        {
            Vector3 p = Where();
            if (p == Vector3.zero)
            {
                RevivalPlugin.L.LogWarning("Patrol: no camera - nothing to record.");
                return;
            }

            Load(false);
            string name = RevivalPlugin.CfgPatrolRoute.Value;
            Route r;
            if (!_routes.TryGetValue(name, out r))
            {
                r = new Route();
                r.Name = name;
                _routes[name] = r;
                _order.Add(name);
            }

            Point pt = new Point();
            pt.Pos = p;
            pt.Speed = 0f;
            pt.Flags = r.P.Count == 0 ? "spawn" : "";
            r.P.Add(pt);

            _lastRecorded = p;
            _haveLastRecorded = true;

            Save();
            if (loud)
            {
                RevivalPlugin.L.LogInfo("Patrol: " + name + " waypoint "
                    + (r.P.Count - 1) + " at " + p);
                Turret.Hinweis(name + " #" + (r.P.Count - 1), 2f);
            }
        }

        /// <summary>Where the player stands. The camera sits at his head, which
        /// is close enough for a road.</summary>
        static Vector3 Where()
        {
            Camera cam = Camera.main;
            if (cam == null) return Vector3.zero;
            return cam.transform.position;
        }

        // =====================================================================
        //  File
        // =====================================================================

        public static void Load(bool force)
        {
            if (_loaded && !force) return;
            _loaded = true;
            _routes.Clear();
            _order.Clear();

            string path = Path.Combine(RevivalPlugin.AssetDir,
                                       RevivalPlugin.CfgPatrolFile.Value);
            if (!File.Exists(path))
            {
                RevivalPlugin.L.LogWarning("Patrol: " + path + " does not exist. "
                    + "No route until one is recorded.");
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
                int bad = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string raw = lines[i];
                    if (raw == null) continue;
                    string trimmed = raw.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                    string[] c = raw.Split('\t');
                    if (c.Length < 5) { bad++; continue; }

                    float x, y, z;
                    int index;
                    if (!int.TryParse(c[1].Trim(), out index)) { bad++; continue; }
                    if (!float.TryParse(c[2].Trim(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out x)
                        || !float.TryParse(c[3].Trim(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out y)
                        || !float.TryParse(c[4].Trim(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out z))
                    { bad++; continue; }

                    float speed = 0f;
                    if (c.Length > 5 && c[5].Trim().Length > 0)
                        float.TryParse(c[5].Trim(), NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out speed);

                    string name = c[0].Trim();
                    if (name.Length == 0) { bad++; continue; }

                    Route r;
                    if (!_routes.TryGetValue(name, out r))
                    {
                        r = new Route();
                        r.Name = name;
                        _routes[name] = r;
                        _order.Add(name);
                    }
                    Point p = new Point();
                    p.Pos = new Vector3(x, y, z);
                    p.Speed = speed;
                    p.Flags = c.Length > 6 ? c[6].Trim() : "";
                    r.P.Add(p);
                }

                for (int i = 0; i < _order.Count; i++)
                {
                    Route r = _routes[_order[i]];
                    MetaLesen(r);
                    RevivalPlugin.L.LogInfo("Patrol: route " + r.Name + ", "
                        + r.P.Count + " waypoints, " + r.Seite + " ("
                        + Fraktion.Erklaerung(r.Seite) + "), " + r.Wagen + ", "
                        + r.Count + " patrol(s)"
                        + (r.Enabled ? "" : ", SWITCHED OFF") + ".");
                }
                if (bad > 0)
                    RevivalPlugin.L.LogWarning("Patrol: " + bad + " line(s) in "
                        + RevivalPlugin.CfgPatrolFile.Value + " were not readable.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol: reading routes: " + ex); }
        }

        static void Save()
        {
            string path = Path.Combine(RevivalPlugin.AssetDir,
                                       RevivalPlugin.CfgPatrolFile.Value);
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# ndr_routes.tsv - written by the in-game recorder.");
                lines.Add("# route\tindex\tx\ty\tz\tspeed\tflags");
                lines.Add("# Pull it into the repository with: python routecheck.py --pull");
                for (int i = 0; i < _order.Count; i++)
                {
                    Route r = _routes[_order[i]];
                    MetaSchreiben(r);
                    for (int k = 0; k < r.P.Count; k++)
                    {
                        Point p = r.P[k];
                        lines.Add(r.Name + "\t" + k.ToString(CultureInfo.InvariantCulture)
                            + "\t" + p.Pos.x.ToString("0.00", CultureInfo.InvariantCulture)
                            + "\t" + p.Pos.y.ToString("0.00", CultureInfo.InvariantCulture)
                            + "\t" + p.Pos.z.ToString("0.00", CultureInfo.InvariantCulture)
                            + "\t" + p.Speed.ToString("0.#", CultureInfo.InvariantCulture)
                            + "\t" + (p.Flags == null ? "" : p.Flags));
                    }
                }
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol: writing routes: " + ex); }
        }

        static Route Active()
        {
            Route r;
            return _routes.TryGetValue(RevivalPlugin.CfgPatrolRoute.Value, out r) ? r : null;
        }

        static bool HasFlag(Point p, string flag)
        {
            if (p.Flags == null || p.Flags.Length == 0) return false;
            string[] parts = p.Flags.Split(',');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Trim() == flag) return true;
            return false;
        }

        /// <summary>The value of a `key=value` flag, empty when it is not
        /// there.</summary>
        static string FlagValue(Point p, string key)
        {
            if (p.Flags == null || p.Flags.Length == 0) return "";
            string[] parts = p.Flags.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string one = parts[i].Trim();
                if (one.Length <= key.Length + 1) continue;
                if (!one.StartsWith(key + "=")) continue;
                return one.Substring(key.Length + 1).Trim();
            }
            return "";
        }

        /// <summary>
        /// The route's own settings, out of the flags of its first waypoint.
        /// A route recorded before 2026-08-30 has none of them and gets the
        /// values from the config - which is what it did all along.
        /// </summary>
        static void MetaLesen(Route r)
        {
            if (r.P.Count == 0) return;
            Point p = r.P[0];
            r.Fraction = Fraktion.Sauber(FlagValue(p, "fraction"));
            r.Vehicle = FlagValue(p, "vehicle");
            r.Enabled = !HasFlag(p, "off");
            int n;
            if (int.TryParse(FlagValue(p, "count"), out n)) r.Count = Mathf.Clamp(n, 0, 16);
            else r.Count = 1;
        }

        /// <summary>
        /// The other direction: the settings back into the flags, replacing
        /// whatever stood there. `spawn` is kept because the driver reads it,
        /// and any flag this class does not know is kept too - somebody may
        /// have written it by hand.
        /// </summary>
        static void MetaSchreiben(Route r)
        {
            if (r.P.Count == 0) return;
            Point p = r.P[0];
            List<string> keep = new List<string>();
            keep.Add("spawn");
            if (p.Flags != null)
            {
                string[] parts = p.Flags.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    string one = parts[i].Trim();
                    if (one.Length == 0 || one == "spawn" || one == "off") continue;
                    if (one.StartsWith("fraction=") || one.StartsWith("vehicle=")
                        || one.StartsWith("count=")) continue;
                    keep.Add(one);
                }
            }
            string f = Fraktion.Sauber(r.Fraction);
            if (f.Length > 0) keep.Add("fraction=" + f);
            string v = r.Vehicle == null ? "" : r.Vehicle.Trim().ToLowerInvariant();
            if (v == "btr" || v == "tank" || v == "mixed") keep.Add("vehicle=" + v);
            keep.Add("count=" + r.Count.ToString(CultureInfo.InvariantCulture));
            if (!r.Enabled) keep.Add("off");
            p.Flags = string.Join(",", keep.ToArray());
        }


        // =====================================================================
        //  Map overlay
        // =====================================================================

        static Texture2D _routeMapBrush;

        /// <summary>
        /// Draws every recorded route as one faction-coloured dashed line along
        /// its waypoints. Editing and deletion stay in the existing F4 route
        /// editor, whose confirmation protects the file.
        /// </summary>
        public static void DrawMap()
        {
            if (RevivalPlugin.CfgPatrol == null || !RevivalPlugin.CfgPatrol.Value)
                return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            Component manager, texture;
            Camera camera;
            Vector2 world, map;
            if (!MapTools.Context(out manager, out texture, out camera,
                                  out world, out map)) return;
            Load(false);

            // The visible map rectangle. Dashes are clipped to it so a route
            // that runs off the shown map does not paint over the game scene
            // around the panel. If the bounds are unavailable, fall back to the
            // whole screen rather than draw nothing.
            Rect clip;
            if (!MapTools.MapScreenRect(texture, camera, out clip))
                clip = new Rect(0f, 0f, Screen.width, Screen.height);

            Color old = GUI.color;
            Matrix4x4 oldMatrix = GUI.matrix;
            try
            {
                for (int routeIndex = 0; routeIndex < _order.Count; routeIndex++)
                {
                    Route route;
                    if (!_routes.TryGetValue(_order[routeIndex], out route)
                        || route == null || route.P.Count < 2) continue;

                    // The waypoints, CONNECTED, are one line. The line is then
                    // cut into evenly spaced Locator-style dashes by walking its
                    // arc length - the individual waypoints are never dashes of
                    // their own. Colour is the patrol's faction: looter and
                    // traitor red, civilian green, neutral white.
                    GUI.color = RouteColor(route.Seite, route.Enabled);

                    // Project the waypoints to the map, splitting into runs
                    // wherever a point cannot be projected.
                    List<Vector2> run = new List<Vector2>(route.P.Count);
                    float phase = 0f;
                    for (int i = 0; i < route.P.Count; i++)
                    {
                        Vector2 g;
                        if (MapTools.WorldToGui(route.P[i].Pos, texture, camera,
                                                world, map, out g))
                            run.Add(g);
                        else
                        {
                            DashRun(run, ref phase, clip);
                            run.Clear();
                        }
                    }
                    DashRun(run, ref phase, clip);

                }

                // Labels belong above the lines, including lines from routes
                // later in the file.
                for (int routeIndex = 0; routeIndex < _order.Count; routeIndex++)
                {
                    Route route;
                    if (!_routes.TryGetValue(_order[routeIndex], out route)
                        || route == null || route.P.Count < 1) continue;
                    Vector2 label;
                    if (!MapTools.WorldToGui(route.P[0].Pos, texture, camera,
                                             world, map, out label)
                        || !clip.Contains(label)) continue;
                    GUI.color = RouteColor(route.Seite, route.Enabled);
                    GUI.Label(new Rect(label.x + 7f, label.y - 12f, 230f, 22f),
                              route.Name + (route.Enabled ? "" : " (disabled)"));
                }

                GUI.color = new Color(1f, 0.65f, 0.22f, 0.95f);
                GUI.Label(new Rect(18f, Screen.height - 48f, 310f, 25f),
                          "F4: edit or delete patrol routes");
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Patrol map overlay: " + ex.Message);
            }
            finally
            {
                GUI.matrix = oldMatrix;
                GUI.color = old;
            }
        }

        /// <summary>
        /// The dash colour for a patrol route, by its faction: looter and
        /// traitor draw the game's own target-ring red (measured RGB
        /// 183,33,32), civilian green, neutral white. A disabled route keeps
        /// its hue but drops to a faint alpha.
        /// </summary>
        static Color RouteColor(string faction, bool enabled)
        {
            Color c;
            switch (faction)
            {
                case "civilian":
                    c = new Color(0.22f, 0.78f, 0.30f); break;   // green
                case "neutral":
                    c = new Color(0.95f, 0.95f, 0.95f); break;   // white
                case "looter":
                case "traitor":
                default:
                    c = new Color(0.72f, 0.13f, 0.125f); break;  // Locator red
            }
            c.a = enabled ? 0.95f : 0.42f;
            return c;
        }

        // Measured from the game's Locator ring at its native map size. The
        // 10 px value is only the cadence gap ALONG each waypoint route. Camp
        // circles, other routes and bends never remove a route dash.
        const float RouteDash = 55f;
        const float RouteGap = 10f;
        const float RouteStroke = 4.5f;

        /// <summary>
        /// Cuts one connected run of screen points into evenly spaced dashes by
        /// walking its arc length. The projected waypoints are smoothed first,
        /// then each dash is stamped along that curve. It therefore follows
        /// bends instead of cutting them with one rigid chord, without reviving
        /// the waypoint-to-waypoint jitter of the old implementation. The dash
        /// phase carries across runs so the pattern stays regular.
        /// </summary>
        static void DashRun(List<Vector2> pts, ref float phase, Rect clip)
        {
            if (pts == null || pts.Count < 2) return;
            pts = SmoothMapRun(pts);
            int n = pts.Count;
            float[] cum = new float[n];
            for (int i = 1; i < n; i++)
                cum[i] = cum[i - 1] + (pts[i] - pts[i - 1]).magnitude;
            float total = cum[n - 1];
            if (total < 1f) { phase = 0f; return; }

            Texture2D brush = RouteMapBrush();
            float period = RouteDash + RouteGap;
            // Begin at the dash boundary preceding this run, so a dash that was
            // mid-stroke at the previous run's end continues seamlessly here.
            for (float ds = -(phase % period); ds < total; ds += period)
            {
                float start = Mathf.Max(ds, 0f);
                float end = Mathf.Min(ds + RouteDash, total);
                if (end <= start) continue;
                DrawCurvedDash(pts, cum, start, end, clip, brush);
            }
            phase = (phase + total) % period;
        }

        /// <summary>A short symmetric low-pass pass over dense screen points.
        /// Three passes suppress recorder jitter while keeping the route's broad
        /// direction changes. Endpoints remain exactly where they were.</summary>
        static List<Vector2> SmoothMapRun(List<Vector2> pts)
        {
            if (pts.Count < 3) return pts;
            List<Vector2> current = new List<Vector2>(pts);
            for (int pass = 0; pass < 3; pass++)
            {
                List<Vector2> next = new List<Vector2>(current.Count);
                next.Add(current[0]);
                for (int i = 1; i < current.Count - 1; i++)
                    next.Add((current[i - 1] + current[i] * 2f
                              + current[i + 1]) * 0.25f);
                next.Add(current[current.Count - 1]);
                current = next;
            }
            return current;
        }

        /// <summary>Draw one dash as overlapping antialiased circles following
        /// the smoothed arc. The first and last brush centres are inset by the
        /// brush radius, so the rounded caps stay inside the measured 55 px dash
        /// and do not steal 4.5 px from the visible 10 px gap.</summary>
        static void DrawCurvedDash(List<Vector2> pts, float[] cum,
                                   float start, float end, Rect clip,
                                   Texture2D brush)
        {
            float radius = RouteStroke * 0.5f;
            float centerStart = start + radius;
            float centerEnd = end - radius;
            if (centerEnd < centerStart)
                centerStart = centerEnd = (start + end) * 0.5f;

            const float step = 1.5f;
            for (float d = centerStart; d < centerEnd; d += step)
                DrawMapBrush(PointAtArc(pts, cum, d), clip, brush);
            DrawMapBrush(PointAtArc(pts, cum, centerEnd), clip, brush);
        }

        static void DrawMapBrush(Vector2 p, Rect clip, Texture2D brush)
        {
            if (!clip.Contains(p)) return;
            float radius = RouteStroke * 0.5f;
            GUI.DrawTexture(new Rect(p.x - radius, p.y - radius,
                                     RouteStroke, RouteStroke), brush);
        }

        /// <summary>The point at arc length <paramref name="d"/> along the
        /// screen polyline, interpolated within the segment it falls in.</summary>
        static Vector2 PointAtArc(List<Vector2> pts, float[] cum, float d)
        {
            int n = pts.Count;
            if (d <= 0f) return pts[0];
            if (d >= cum[n - 1]) return pts[n - 1];
            int i = 1;
            while (i < n - 1 && cum[i] < d) i++;
            float segLen = cum[i] - cum[i - 1];
            float t = segLen > 1e-4f ? (d - cum[i - 1]) / segLen : 0f;
            return Vector2.Lerp(pts[i - 1], pts[i], t);
        }

        /// <summary>A softly antialiased circular brush. Stamping it along a
        /// smoothed arc makes curved dashes with genuinely round end caps.</summary>
        static Texture2D RouteMapBrush()
        {
            if (_routeMapBrush != null) return _routeMapBrush;
            const int size = 16;
            float radius = (size - 1) * 0.5f;
            _routeMapBrush = new Texture2D(size, size, TextureFormat.ARGB32, false);
            _routeMapBrush.name = "NDR Patrol Route Brush";
            _routeMapBrush.hideFlags = HideFlags.HideAndDontSave;
            _routeMapBrush.wrapMode = TextureWrapMode.Clamp;
            _routeMapBrush.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - radius;
                    float dy = y - radius;
                    float alpha = Mathf.Clamp01(radius + 0.5f
                        - Mathf.Sqrt(dx * dx + dy * dy));
                    _routeMapBrush.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            _routeMapBrush.Apply(false, true);
            return _routeMapBrush;
        }

        // =====================================================================
        //  The route editor
        // =====================================================================

        /// <summary>
        /// The window that makes a route without a text file.
        ///
        /// Everything it does was already possible from `nextday.revival.toolkit.cfg`
        /// plus three keys, and that is exactly the problem it solves: the
        /// settings that matter are PER ROUTE - which side patrols it, what
        /// drives it, how many - and a config file has one of each. The user's
        /// plan is patrols at many places on the map, a looter patrol outside
        /// the looter base and a civilian one around the civilian base, and
        /// that plan needs a place to say so per route. This is that place,
        /// and it writes what it is told straight into the flags of each
        /// route's first waypoint (see <see cref="Route"/>).
        ///
        /// It is nested inside Patrol on purpose: it works on `Route`, which
        /// is Patrol's own type, and a second class outside would have needed
        /// a translation layer of strings for no gain.
        /// </summary>
        internal static class Editor
        {
            const int FensterId = 0x4E445242;

            static bool _offen;
            static bool _fokusLoesen;
            static Rect _fenster = new Rect(60f, 60f, 470f, 0f);
            static Vector2 _rollen;
            static string _neu = "";
            static string _status = "";
            static string _loeschFrage = "";

            public static bool IsOpen { get { return _offen; } }

            public static void Tick()
            {
                if (!Input.GetKeyDown(_editKey)) return;
                _offen = !_offen;
                if (_offen) Load(false);
                else { _fokusLoesen = true; CursorZurueck(); }
                RevivalPlugin.L.LogInfo("Patrol editor " + (_offen ? "open" : "closed") + ".");
            }

            static void CursorZurueck()
            {
                if (!CursorTracker.SawCall) return;
                CursorTracker.Restoring = true;
                try
                {
                    Cursor.lockState = CursorTracker.DesiredLock;
                    Cursor.visible = CursorTracker.DesiredVisible;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Patrol editor cursor: " + ex.Message);
                }
                finally { CursorTracker.Restoring = false; }
            }

            public static void Draw()
            {
                if (_fokusLoesen)
                {
                    _fokusLoesen = false;
                    GUIUtility.keyboardControl = 0;
                    GUIUtility.hotControl = 0;
                }
                if (!_offen || !RevivalPlugin.CfgPatrol.Value) return;

                CursorTracker.Restoring = true;
                try
                {
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                }
                finally { CursorTracker.Restoring = false; }

                _fenster = GUILayout.Window(FensterId, _fenster, Inhalt,
                                            "Revival - Patrol routes");
            }

            static void Inhalt(int id)
            {
                // ------------------------------------------------ recording
                GUILayout.Label(_recording
                    ? "RECORDING into \"" + RevivalPlugin.CfgPatrolRoute.Value
                      + "\" - a waypoint every "
                      + RevivalPlugin.CfgPatrolRecordSeconds.Value.ToString("0.#")
                      + " s while you drive."
                    : "Not recording. Drive the road you want patrolled, then stop.");

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_recording ? "stop recording" : "record",
                                     GUILayout.Width(130f)))
                {
                    ToggleRecording();
                    Melde(_recording ? "recording " + RevivalPlugin.CfgPatrolRoute.Value
                                     : "recording stopped");
                }
                if (GUILayout.Button("waypoint here", GUILayout.Width(120f)))
                {
                    RecordHere(true);
                    Melde("waypoint added to " + RevivalPlugin.CfgPatrolRoute.Value);
                }
                if (GUILayout.Button("undo last", GUILayout.Width(90f))) Zurueck();
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("New route:", GUILayout.Width(70f));
                _neu = GUILayout.TextField(_neu, 24, GUILayout.Width(140f));
                if (GUILayout.Button("create and record", GUILayout.Width(150f))) Anlegen();
                GUILayout.EndHorizontal();

                GUILayout.Space(6f);

                // --------------------------------------------------- routes
                GUILayout.Label("Routes - each one is its own patrol");
                _rollen = GUILayout.BeginScrollView(_rollen, GUILayout.Height(260f));
                for (int i = 0; i < _order.Count; i++) Zeile(_routes[_order[i]]);
                if (_order.Count == 0)
                    GUILayout.Label("None yet. Type a name above, press "
                        + "\"create and record\", and drive the road.");
                GUILayout.EndScrollView();

                GUILayout.Space(6f);

                // ------------------------------------------------ the road
                int max = Mathf.Max(1, RevivalPlugin.CfgPatrolMax.Value);
                GUILayout.Label("On the road: " + _units.Count + " of " + max
                    + " (MaxVehicles). Automatic: " + (_auto ? "on" : "OFF"));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_auto ? "automatic off" : "automatic on",
                                     GUILayout.Width(130f)))
                {
                    if (_auto) { StopAll(); Melde("all patrols off the road"); }
                    else { Toggle(); Melde("automatic on"); }
                }
                if (GUILayout.Button("clear the road", GUILayout.Width(120f)))
                {
                    StopAll();
                    Melde("all patrols off the road");
                }
                if (GUILayout.Button("save file", GUILayout.Width(90f)))
                {
                    Save();
                    Melde("written to " + RevivalPlugin.CfgPatrolFile.Value);
                }
                if (GUILayout.Button("reload", GUILayout.Width(70f)))
                {
                    Load(true);
                    Melde("read back from " + RevivalPlugin.CfgPatrolFile.Value);
                }
                GUILayout.EndHorizontal();

                if (_status.Length > 0) GUILayout.Label(_status);
                GUILayout.Label("civilian attacks everyone but civilians. looter "
                    + "everyone but looters. traitor attacks EVERYONE. neutral "
                    + "attacks traitors only.");

                if (GUILayout.Button("close")) { _offen = false; CursorZurueck(); }
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
            }

            /// <summary>
            /// One route. Every change here is written to the file at once:
            /// the alternative is a window whose state is lost when the world
            /// ends, and a route editor that loses routes is worse than no
            /// route editor. `Save` is a few dozen lines of text - it can be
            /// afforded on a button press.
            /// </summary>
            static void Zeile(Route r)
            {
                bool aktiv = RevivalPlugin.CfgPatrolRoute.Value == r.Name;
                GUILayout.BeginVertical(GUI.skin.box);

                GUILayout.BeginHorizontal();
                GUILayout.Label((aktiv ? "> " : "  ") + r.Name + "  "
                    + r.P.Count + " wp  " + Fahren(r.Name) + "/" + r.Count + " out",
                    GUILayout.Width(190f));
                bool an = GUILayout.Toggle(r.Enabled, "on", GUILayout.Width(45f));
                if (an != r.Enabled)
                {
                    r.Enabled = an;
                    Sichern(r.Name + (an ? " is on" : " is off - the automatic "
                                            + "leaves it alone"));
                }
                if (GUILayout.Button("-", GUILayout.Width(24f)) && r.Count > 0)
                {
                    r.Count--;
                    Sichern(r.Name + " carries " + r.Count + " patrol(s)");
                }
                GUILayout.Label(r.Count.ToString(), GUILayout.Width(20f));
                if (GUILayout.Button("+", GUILayout.Width(24f)) && r.Count < 16)
                {
                    r.Count++;
                    Sichern(r.Name + " carries " + r.Count + " patrol(s)");
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("side", GUILayout.Width(34f));
                for (int i = 0; i < Fraktion.Namen.Length; i++)
                {
                    string n = Fraktion.Namen[i];
                    // `Seite` is the EFFECTIVE side, so a route that never
                    // chose one shows the config's choice as selected. Pressing
                    // that same button is therefore not a no-op: it pins the
                    // choice to the route, which is what the user meant by
                    // pressing it.
                    if (GUILayout.Toggle(r.Seite == n, n, GUI.skin.button,
                                         GUILayout.Width(72f))
                        && r.Fraction != n)
                    {
                        r.Fraction = n;
                        Sichern(r.Name + " is " + n + " - " + Fraktion.Erklaerung(n));
                    }
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("car", GUILayout.Width(34f));
                Wagenknopf(r, "btr");
                Wagenknopf(r, "tank");
                Wagenknopf(r, "mixed");
                if (GUILayout.Button("record into", GUILayout.Width(90f)))
                {
                    RevivalPlugin.CfgPatrolRoute.Value = r.Name;
                    if (!_recording) ToggleRecording();
                    Melde("recording into " + r.Name);
                }
                if (GUILayout.Button("patrol now", GUILayout.Width(85f))) Jetzt(r);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (_loeschFrage == r.Name)
                {
                    GUILayout.Label("Delete " + r.Name + " and its " + r.P.Count
                        + " waypoints?", GUILayout.Width(250f));
                    if (GUILayout.Button("yes, delete", GUILayout.Width(90f))) Loeschen(r);
                    if (GUILayout.Button("no", GUILayout.Width(40f))) _loeschFrage = "";
                }
                else
                {
                    if (GUILayout.Button("delete route", GUILayout.Width(100f)))
                        _loeschFrage = r.Name;
                }
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
            }

            static void Wagenknopf(Route r, string was)
            {
                if (GUILayout.Toggle(r.Wagen == was, was, GUI.skin.button,
                                     GUILayout.Width(54f))
                    && r.Vehicle != was)
                {
                    r.Vehicle = was;
                    Sichern(r.Name + " drives " + was);
                }
            }

            /// <summary>Say it and write it. Every setting in this window goes
            /// through here.</summary>
            static void Sichern(string text)
            {
                Save();
                Melde(text);
            }

            static void Anlegen()
            {
                string name = _neu == null ? "" : _neu.Trim();
                if (name.Length == 0) { Melde("a route needs a name"); return; }
                if (name.IndexOf('\t') >= 0 || name[0] == '#')
                {
                    Melde("no tabs and no leading # - the file is a TSV");
                    return;
                }
                Load(false);
                if (_routes.ContainsKey(name))
                {
                    Melde("\"" + name + "\" is already there - use \"record into\"");
                    return;
                }
                Route r = new Route();
                r.Name = name;
                _routes[name] = r;
                _order.Add(name);
                RevivalPlugin.CfgPatrolRoute.Value = name;
                _neu = "";
                if (!_recording) ToggleRecording();
                Melde("recording " + name + " - drive the road, then press stop");
            }

            static void Zurueck()
            {
                Route r = Active();
                if (r == null || r.P.Count == 0) { Melde("nothing to undo"); return; }
                r.P.RemoveAt(r.P.Count - 1);
                Save();
                Melde(r.Name + " now has " + r.P.Count + " waypoints");
            }

            static void Loeschen(Route r)
            {
                _loeschFrage = "";
                _routes.Remove(r.Name);
                _order.Remove(r.Name);
                Save();
                Melde(r.Name + " deleted");
                RevivalPlugin.L.LogInfo("Patrol: route " + r.Name + " deleted from "
                    + RevivalPlugin.CfgPatrolFile.Value + ".");
            }

            /// <summary>One patrol on this route now, wherever the player is
            /// standing. It goes through the same Spawn the automatic uses, so
            /// a route that works here works there.</summary>
            static void Jetzt(Route r)
            {
                if (r.P.Count < 3) { Melde(r.Name + " needs at least three waypoints"); return; }
                int max = Mathf.Max(1, RevivalPlugin.CfgPatrolMax.Value);
                if (_units.Count >= max)
                {
                    Melde("MaxVehicles is " + max + " and " + _units.Count
                          + " are out - clear the road first");
                    return;
                }
                int before = _units.Count;
                Spawn(r, false);
                Melde(_units.Count > before
                    ? r.Name + ": one " + r.Seite + " patrol on the road"
                    : "the vehicle could not be put down - see the log");
            }

            static void Melde(string text)
            {
                _status = text;
                RevivalPlugin.L.LogInfo("Patrol editor: " + text);
            }
        }

        // =====================================================================
        //  Reflection, cached. The plugin references no Assembly-CSharp and no
        //  PhysicsModule, so every field here goes through AccessTools - and a
        //  fresh lookup per physics step for six vehicles is not free.
        // =====================================================================

        static Dictionary<string, FieldInfo> _fieldCache = new Dictionary<string, FieldInfo>();
        static Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>();
        static PropertyInfo _velocity, _angular;
        static bool _bodyLookedUp;

        static FieldInfo Field(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            string key = t.FullName + "|" + name;
            FieldInfo fi;
            if (_fieldCache.TryGetValue(key, out fi)) return fi;
            fi = AccessTools.Field(t, name);
            _fieldCache[key] = fi;
            if (fi == null)
                RevivalPlugin.L.LogWarning("Patrol: field " + name + " not on " + t.Name + ".");
            return fi;
        }

        static object GetField(object o, string name)
        {
            FieldInfo fi = Field(o, name);
            return fi == null ? null : fi.GetValue(o);
        }

        static float GetFloat(object o, string name, float fallback)
        {
            FieldInfo fi = Field(o, name);
            if (fi == null || fi.FieldType != typeof(float)) return fallback;
            return (float)fi.GetValue(o);
        }

        static void SetFloat(object o, string name, float value)
        {
            FieldInfo fi = Field(o, name);
            if (fi != null && fi.FieldType == typeof(float)) fi.SetValue(o, value);
        }

        static bool GetBool(object o, string name, bool fallback)
        {
            FieldInfo fi = Field(o, name);
            if (fi == null || fi.FieldType != typeof(bool)) return fallback;
            return (bool)fi.GetValue(o);
        }

        static void SetBool(object o, string name, bool value)
        {
            FieldInfo fi = Field(o, name);
            if (fi != null && fi.FieldType == typeof(bool)) fi.SetValue(o, value);
        }

        static void Invoke(object o, string name)
        {
            if (o == null) return;
            Type t = o.GetType();
            string key = t.FullName + "|()" + name;
            MethodInfo mi;
            if (!_methodCache.TryGetValue(key, out mi))
            {
                mi = AccessTools.Method(t, name, null, null);
                _methodCache[key] = mi;
                if (mi == null)
                    RevivalPlugin.L.LogWarning("Patrol: method " + name + " not on " + t.Name + ".");
            }
            if (mi != null) mi.Invoke(o, null);
        }

        static void LookUpBody()
        {
            if (_bodyLookedUp) return;
            _bodyLookedUp = true;
            Type rb = RevivalPlugin.TypeByName("UnityEngine.Rigidbody");
            if (rb == null)
            {
                RevivalPlugin.L.LogWarning("Patrol: UnityEngine.Rigidbody not found - "
                    + "speed will be read as zero and the driver will floor it.");
                return;
            }
            _velocity = rb.GetProperty("velocity", BindingFlags.Public | BindingFlags.Instance);
            _angular = rb.GetProperty("angularVelocity", BindingFlags.Public | BindingFlags.Instance);
        }

        static Vector3 Velocity(object body)
        {
            LookUpBody();
            if (body == null || _velocity == null) return Vector3.zero;
            return (Vector3)_velocity.GetValue(body, null);
        }

        static void Stop(object body)
        {
            LookUpBody();
            if (body == null) return;
            if (_velocity != null) _velocity.SetValue(body, Vector3.zero, null);
            if (_angular != null) _angular.SetValue(body, Vector3.zero, null);
        }

        // =====================================================================
        //  Keys
        // =====================================================================

        static void ParseKeys()
        {
            if (_keysParsed) return;
            _keysParsed = true;
            _key = ParseKey(RevivalPlugin.CfgPatrolKey.Value, KeyCode.F11, "Key");
            _autoKey = ParseKey(RevivalPlugin.CfgPatrolAutoKey.Value, KeyCode.F5, "RecordAutoKey");
            _recKey = ParseKey(RevivalPlugin.CfgPatrolRecordKey.Value, KeyCode.F6, "RecordKey");
            _editKey = ParseKey(RevivalPlugin.CfgPatrolEditorKey.Value, KeyCode.F4, "EditorKey");
        }

        static KeyCode ParseKey(string text, KeyCode fallback, string which)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), text, true); }
            catch
            {
                RevivalPlugin.L.LogWarning("Patrol: " + which + " \"" + text
                    + "\" is not a KeyCode, using " + fallback + ".");
                return fallback;
            }
        }
    }

    // ------------------------------------------------------- who shoots whom

    /// <summary>
    /// The four sides a patrol can be on, and what each of them attacks.
    ///
    /// The game already has the enum - `Fraction`, eight values, Neutral 0,
    /// Marauder 1, Peace 2, Hermit 3, Wildman 4, Military 5, Traitor 6,
    /// MilitaryNeutral 7 - and it already has the one method that decides a
    /// fight (read out of Assembly-CSharp.dll on 2026-08-30, RE 23):
    ///
    ///     NPC_AI2::IsEnemyFraction(player)
    ///         fraction = PlayerStatisticsManager.GetPlayerInfo(player).fraction
    ///         walk THIS NPC's MainOptions.HatedFractions
    ///         return true if the player's fraction is in it
    ///
    /// So enmity is not a table somewhere: it is an ARRAY on the NPC, and
    /// whoever fills that array decides who is shot at. `NPC_Settlement::
    /// SetNpcParams` copies it from the settlement's `FractionOptions` (or the
    /// spawn point's `IndividualFraction` where `UseIndividualFraction` is
    /// set), and both of those are ours to write.
    ///
    /// The game's own hardcoded relations (PlayerStatisticsManager::
    /// GetHatedFractionsOfNPC) are only used for the map markers and are NOT
    /// what an NPC fights by. They are listed here because they are the reason
    /// the four names below map onto the enum values they do - a patrol whose
    /// marker colour disagreed with its trigger finger would be a bug report:
    ///
    ///     Neutral   hates Marauder
    ///     Marauder  hates everyone except Marauder and MilitaryNeutral
    ///     Peace     hates Marauder, Military, Traitor
    ///     Hermit    hates Traitor
    ///     Wildman   hates Marauder, Traitor
    ///     Military  hates everyone except Military and MilitaryNeutral
    ///     Traitor   hates EVERYONE, itself included
    ///     MilitaryNeutral returns null - never use it, IsHatedFraction does
    ///                     ldlen on the result and would throw.
    ///
    /// The four names the user asked for, and what each becomes:
    ///
    ///     civilian  Peace     hates all seven others - everyone but civilians
    ///     looter    Marauder  hates all seven others - everyone but looters
    ///     traitor   Traitor   hates all EIGHT, itself included
    ///     neutral   Neutral   hates Traitor only
    ///
    /// The hated arrays are written out in full rather than taken from the
    /// game's own table, because the user's rule and the game's table differ
    /// in one place that matters: the game's Peace tolerates Hermits and
    /// Wildmen, and "attacks everyone but civilians" does not.
    /// </summary>
    public static class Fraktion
    {
        /// <summary>The enum values, by name, so nothing here depends on a
        /// number staying where it is.</summary>
        const string Neutral = "Neutral";
        const string Marauder = "Marauder";
        const string Peace = "Peace";
        const string Hermit = "Hermit";
        const string Wildman = "Wildman";
        const string Military = "Military";
        const string Traitor = "Traitor";
        const string MilitaryNeutral = "MilitaryNeutral";

        /// <summary>The four names a route may carry, in the order the editor
        /// shows them.</summary>
        public static readonly string[] Namen =
            new string[] { "civilian", "looter", "traitor", "neutral" };

        /// <summary>One line each, for the editor and the log.</summary>
        public static string Erklaerung(string name)
        {
            switch (Sauber(name))
            {
                case "civilian": return "attacks everyone but civilians";
                case "looter": return "attacks everyone but looters";
                case "traitor": return "attacks EVERYONE, traitors included";
                default: return "attacks traitors only";
            }
        }

        /// <summary>A name the rest of the code can rely on. Anything
        /// unreadable becomes the configured default, and that is said once
        /// where it happens, not once a frame.</summary>
        public static string Sauber(string name)
        {
            if (name == null) return "neutral";
            string n = name.Trim().ToLowerInvariant();
            for (int i = 0; i < Namen.Length; i++)
                if (Namen[i] == n) return n;
            return "";
        }

        /// <summary>The game's own enum value this side is.</summary>
        static string Eigene(string name)
        {
            switch (name)
            {
                case "civilian": return Peace;
                case "looter": return Marauder;
                case "traitor": return Traitor;
                default: return Neutral;
            }
        }

        /// <summary>Everything this side shoots at.</summary>
        static string[] Gehasste(string name)
        {
            switch (name)
            {
                case "civilian":
                    return new string[] { Neutral, Marauder, Hermit, Wildman,
                                          Military, Traitor, MilitaryNeutral };
                case "looter":
                    return new string[] { Neutral, Peace, Hermit, Wildman,
                                          Military, Traitor, MilitaryNeutral };
                case "traitor":
                    return new string[] { Neutral, Marauder, Peace, Hermit,
                                          Wildman, Military, Traitor,
                                          MilitaryNeutral };
                default:
                    return new string[] { Traitor };
            }
        }

        /// <summary>
        /// Build one `NPCMainOptions` for this side: MyFraction, the hated
        /// array, and an EMPTY friendly array.
        ///
        /// The empty array is not tidiness. `FriendlyFractions` is never read
        /// by the AI - only `HatedFractions` is - but a null array in a field
        /// the game may one day walk is a crash waiting for a patch, and an
        /// empty one costs nothing. `HatedFractions` itself MUST be an array:
        /// `IsEnemyFraction` does `ldlen` on it with no null check.
        /// </summary>
        public static object Optionen(string name)
        {
            Type t = RevivalPlugin.TypeByName("NPCMainOptions");
            Type f = RevivalPlugin.TypeByName("Fraction");
            if (t == null || f == null || !f.IsEnum)
            {
                RevivalPlugin.L.LogWarning("Fraktion: NPCMainOptions or the "
                    + "Fraction enum is missing - a patrol crew will use "
                    + "whatever the game gives it.");
                return null;
            }

            string wer = Sauber(name);
            if (wer.Length == 0) wer = "neutral";

            object o;
            try { o = Activator.CreateInstance(t); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Fraktion: NPCMainOptions could not "
                    + "be built - " + ex.Message);
                return null;
            }

            Feld(o, "MyFraction", Wert(f, Eigene(wer)));
            Feld(o, "HatedFractions", Liste(f, Gehasste(wer)));
            Feld(o, "FriendlyFractions", Array.CreateInstance(f, 0));
            Feld(o, "ID", "");
            Feld(o, "GroupID", "");
            return o;
        }

        static object Wert(Type enumType, string name)
        {
            try { return Enum.Parse(enumType, name, true); }
            catch
            {
                RevivalPlugin.L.LogWarning("Fraktion: the Fraction enum has no "
                    + name + " - falling back to the first value.");
                return Enum.ToObject(enumType, 0);
            }
        }

        static Array Liste(Type enumType, string[] namen)
        {
            Array a = Array.CreateInstance(enumType, namen.Length);
            for (int i = 0; i < namen.Length; i++)
                a.SetValue(Wert(enumType, namen[i]), i);
            return a;
        }

        static void Feld(object o, string name, object value)
        {
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null)
            {
                RevivalPlugin.L.LogWarning("Fraktion: NPCMainOptions has no "
                    + name + ".");
                return;
            }
            try { fi.SetValue(o, value); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Fraktion: " + name + " not set - "
                    + ex.Message);
            }
        }
    }

    // ------------------------------------------------- the crew of a patrol

    /// <summary>
    /// The men in the vehicle, and what happens when it burns.
    ///
    /// WHILE IT DRIVES the crew is a NUMBER, not bodies. One man per seat is
    /// counted in <see cref="Patrol"/>, the seats are closed to players
    /// (Turret.FreeSeatPostfix asks Patrol.Besetzt), and that is all. Putting
    /// real NPCs into the seats was tried on paper first and dropped, for two
    /// reasons that are both read out of the game, not guessed:
    ///
    ///   1  `VehicleGameSystem::SetDamageToAllPassengers` and
    ///      `GetPassengersPlayerIds` walk `Passengers` and call
    ///      `GetComponent&lt;PlayerNetworkController&gt;().GetPhotonPlayer` on
    ///      every entry. On an NPC GameObject that GetComponent returns null
    ///      and the call throws - and SetDamageToAllPassengers runs exactly
    ///      when the vehicle is destroyed, which is the one moment that must
    ///      not fail.
    ///   2  A body parented to the seat is parented on the HOST only. The
    ///      other clients get the NPC's own position sync and would see the
    ///      crew standing in the road where the vehicle once spawned. The
    ///      hull is closed - nobody can see the crew inside it anyway.
    ///
    /// WHEN IT IS DESTROYED the number becomes bodies. That is the moment
    /// they are worth their cost: the man who killed the vehicle is standing
    /// within a few dozen metres, and now there are marauders on the ground
    /// looking for him.
    ///
    /// HOW THE BODIES ARE MADE. Not by hand. The game builds an NPC out of a
    /// **settlement** plus one **spawn point** per man, and every value an NPC
    /// needs - appearance, weapon, level, behaviour - is derived from those
    /// two by the game's own code (RE 10, `NPC_Settlement::InitSpawnNpc`). So
    /// this class builds a settlement the size of one wreck: a GameObject at
    /// the wreck, a spawn point at each of the vehicle's own GetOutPoints, a
    /// ring of walk points around it, and then `StartMainInit` - the game's
    /// own entry point - does the rest, including
    /// `PhotonNetwork.InstantiateSceneObject` so every client sees them.
    ///
    /// The settlement is what makes them hunt: its `Update` runs
    /// `PlayersDistanceControll` and the sensors that hand an NPC a target.
    /// Behaviour Aggressive, respawn switched off - they die once and stay
    /// dead. WHICH SIDE they are on comes from the route the vehicle was
    /// driving (`fraction=` in its first waypoint's flags) and is written into
    /// the settlement's `FractionOptions` by <see cref="Fraktion"/>; that one
    /// object is what `NPC_AI2::IsEnemyFraction` reads, so it is the whole
    /// answer to "who do these men shoot at" (RE 23).
    ///
    /// TWO THINGS AN AddComponent DOES NOT GIVE, and both of them stopped this
    /// class dead once each:
    ///
    ///   the lists and the little classes   `Listen`, and the reasons are at
    ///       that method. The second run died on `NPC_SpawnPoint.Quests` being
    ///       null, after the men had already been built.
    ///   the designers' numbers             `Abschreiben`. A settlement built
    ///       at runtime has SensorVisibleDist 0 - a crew that cannot see. The
    ///       values are copied off a settlement the map already has instead of
    ///       being invented here.
    ///
    /// THE PHOTONVIEW WITH ID 0 IS DELIBERATE. `InitSpawnNpc` puts
    /// `photonView.viewID` into the instantiation data as element 0, and on
    /// the other clients `NPC_AI2::FindMySpawnPointAndSet` looks that id up in
    /// `LocalSettlementsDictionary`. A settlement built at runtime is not in
    /// that dictionary on any machine, so the lookup misses and the method
    /// returns - after it has read the weapon id out of element 2, but BEFORE
    /// it applies element 1, the appearance. An unregistered PhotonView
    /// (viewID 0) gives a clean lookup miss and no id collision, while the
    /// remote Start postfix below supplies the missing visual half.
    ///
    /// UNTESTED. Every line of this is read IL. Nothing here has run.
    /// </summary>
    public static class Crew
    {
        /// <summary>Metres between the walk points the crew wanders over.
        /// Wide enough that they spread out around the wreck, tight enough
        /// that they stay a group.</summary>
        const float RingRadius = 9f;

        /// <summary>Tactical points used by the game's real alarm state. They
        /// sit between the ordinary patrol points so an alarm makes the crew
        /// spread out instead of walking the same ring a little faster.</summary>
        const float TacticalRingRadius = 12f;

        /// <summary>The name every crew settlement carries. It is how the
        /// template search tells the map's settlements from our own.</summary>
        internal const string Name = "NDR_PatrolCrew";

        static List<GameObject> _settlements = new List<GameObject>();

        /// <summary>Wreck crews are permanent combatants, not settlements
        /// waiting for a nearby player. The vanilla distance shutdown calls
        /// SetActiveAI(false), which stops the legacy Animation and leaves its
        /// skinned mesh in the bind pose. Skip only that shutdown for the
        /// generated crew settlement; every map settlement keeps the vanilla
        /// distance optimization.</summary>
        public static bool AutoDisablePrefix(object __instance)
        {
            Component settlement = __instance as Component;
            return settlement == null || settlement.gameObject == null
                || settlement.gameObject.name != Name;
        }

        /// <summary>
        /// The switch that froze them, and the same switch that made them
        /// unkillable. One call, four effects (`NPC_AI2::SetPlayVisualizationValue`):
        ///
        ///     Anim.enabled = false            the legacy Animation stops
        ///                                     where it stands - the frozen
        ///                                     figure with the frozen weapon
        ///     RagdollController.SetPhysActive(false)
        ///                                     detectCollisions = false on
        ///                                     EVERY ragdoll rigidbody
        ///     _colliderMain.enabled = false
        ///     _rigidbody.Sleep()
        ///
        /// The second line is why a drone detonating beside them does nothing.
        /// `ExplosionObject::ExplosionPhysicsEffect` damages an NPC only
        /// through a collider tagged `RagdollBone` that `Physics.OverlapSphere`
        /// reports, and a rigidbody with collision detection switched off is
        /// not reported by that query. 550 damage in 7 m never reaches them,
        /// and no damage value would have.
        ///
        /// It is switched off whenever the local player BODY is not in
        /// `PlayersAround`: `CheckVisualizationForLocalPlayer` and
        /// `StartAutoDisableTimer` both do it, neither looks at the camera, and
        /// a drone is not a body. So a wreck crew that is flown to instead of
        /// walked to is frozen and invulnerable at once, from one line.
        ///
        /// E-042 found the neighbouring switch - `SetActiveAI(false)`, which
        /// calls `Animation.Stop()` - and this one was left standing. Same rule
        /// as there, and same reason: only the generated crew settlement
        /// refuses it, every settlement the map brought keeps the vanilla
        /// distance optimization. Enabling is never refused.
        /// </summary>
        public static bool PlayVisualizationPrefix(object __instance, bool __0)
        {
            if (__0) return true;
            Component settlement = __instance as Component;
            return settlement == null || settlement.gameObject == null
                || settlement.gameObject.name != Name;
        }

        internal static void Install(Harmony harmony)
        {
            Type type = RevivalPlugin.TypeByName("NPC_Settlement");
            if (type == null)
            {
                RevivalPlugin.L.LogWarning("Crew: NPC_Settlement not found - distant "
                    + "wreck crews stay frozen and cannot be hit.");
            }
            else
            {
                // Two switches, two patches, and one must not cost the other.
                Haengen(harmony, type, "AutoDisableControl",
                    typeof(Crew).GetMethod("AutoDisablePrefix"),
                    "Crew: wreck crews stay animated at any distance.",
                    "distant wreck crews may enter the T-pose");
                Haengen(harmony, type, "SetPlayVisualizationValue",
                    typeof(Crew).GetMethod("PlayVisualizationPrefix"),
                    "Crew: wreck crews keep animation, collision and ragdoll at any distance.",
                    "a wreck crew reached by drone stays frozen and cannot be hit");
            }

            Type npc = RevivalPlugin.TypeByName("NPC_AI2");
            if (npc == null)
            {
                RevivalPlugin.L.LogWarning("Crew: NPC_AI2 not found - remote crew "
                    + "appearance cannot be restored.");
                return;
            }
            try
            {
                MethodInfo start = AccessTools.Method(npc, "Start", null, null);
                MethodInfo visualization = AccessTools.Method(npc,
                    "SetPlayVisualizationValue", null, null);
                if (start != null)
                    harmony.Patch(start, null,
                        new HarmonyMethod(typeof(Crew).GetMethod("NpcStartPostfix")),
                        null, null, null);
                if (visualization != null)
                    harmony.Patch(visualization,
                        new HarmonyMethod(typeof(Crew).GetMethod(
                            "NpcVisualizationPrefix")), null, null, null, null);
                RevivalPlugin.L.LogInfo("Crew: remote NPC appearance and animation "
                    + "repair hooks installed.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Crew: remote NPC hooks failed - " + ex);
            }
        }

        public static void NpcStartPostfix(object __instance)
        {
            try
            {
                Component ai = __instance as Component;
                object[] data;
                bool isMine;
                if (ai == null || !SpawnData(ai, out data, out isMine) || isMine) return;
                int[] appearance = data[1] as int[];
                int weapon = Convert.ToInt32(data[2]);
                if (appearance == null) return;
                CrewRemoteFix fix = ai.gameObject.GetComponent<CrewRemoteFix>();
                if (fix == null) fix = ai.gameObject.AddComponent<CrewRemoteFix>();
                fix.Begin(ai, appearance, weapon);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: remote NPC start repair - "
                    + ex.Message);
            }
        }

        public static bool NpcVisualizationPrefix(object __instance, bool __0)
        {
            if (__0) return true;
            Component ai = __instance as Component;
            object[] data;
            bool isMine;
            return ai == null || !SpawnData(ai, out data, out isMine);
        }

        static bool SpawnData(Component ai, out object[] data, out bool isMine)
        {
            data = null;
            isMine = true;
            try
            {
                MethodInfo pv = AccessTools.Method(ai.GetType(), "get_photonView",
                                                   null, null);
                object view = pv == null ? null : pv.Invoke(ai, null);
                if (view == null) return false;
                MethodInfo mine = AccessTools.PropertyGetter(view.GetType(), "isMine");
                if (mine != null) isMine = (bool)mine.Invoke(view, null);
                MethodInfo inst = AccessTools.PropertyGetter(view.GetType(),
                                                              "instantiationData");
                data = inst == null ? null : inst.Invoke(view, null) as object[];
                return data != null && data.Length >= 5 && data[0] != null
                    && Convert.ToInt32(data[0]) == 0 && data[1] is int[];
            }
            catch { return false; }
        }

        internal static bool ApplyRemoteAppearance(Component ai, int[] appearance,
                                                   int weapon, out string problem)
        {
            problem = "";
            if (ai == null) { problem = "NPC component disappeared"; return false; }
            try
            {
                MethodInfo customize = AccessTools.Method(ai.GetType(),
                    "SetCustomization", new Type[] { typeof(int[]) }, null);
                MethodInfo arm = AccessTools.Method(ai.GetType(), "SetMainWeaponId",
                    new Type[] { typeof(int), typeof(bool) }, null);
                MethodInfo visualize = AccessTools.Method(ai.GetType(),
                    "SetPlayVisualizationValue", new Type[] { typeof(bool) }, null);
                if (customize == null || arm == null)
                {
                    problem = "customization or weapon method missing";
                    return false;
                }
                customize.Invoke(ai, new object[] { appearance });
                arm.Invoke(ai, new object[] { weapon, false });
                if (visualize != null) visualize.Invoke(ai, new object[] { true });

                FieldInfo animationField = AccessTools.Field(ai.GetType(), "Anim");
                Component animation = animationField == null ? null
                    : animationField.GetValue(ai) as Component;
                if (animation == null)
                {
                    problem = "animation component not ready";
                    return false;
                }
                PropertyInfo enabled = AccessTools.Property(animation.GetType(), "enabled");
                if (enabled != null) enabled.SetValue(animation, true, null);
                MethodInfo play = AccessTools.Method(animation.GetType(), "Play",
                                                      Type.EmptyTypes, null);
                if (play != null) play.Invoke(animation, null);
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.InnerException == null
                    ? ex.Message : ex.InnerException.Message;
                return false;
            }
        }

        static void Haengen(Harmony harmony, Type type, string spielMethode,
                            MethodInfo eigene, string erfolg, string folge)
        {
            try
            {
                MethodInfo method = AccessTools.Method(type, spielMethode, null, null);
                if (method == null || eigene == null)
                {
                    RevivalPlugin.L.LogWarning("Crew: NPC_Settlement." + spielMethode
                        + " not found - " + folge + ".");
                    return;
                }
                harmony.Patch(method, new HarmonyMethod(eigene), null, null, null, null);
                RevivalPlugin.L.LogInfo(erfolg);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Crew: " + spielMethode + " hook failed - " + ex);
            }
        }

        /// <summary>
        /// The crew of a destroyed vehicle climbs out.
        /// </summary>
        public static void Aussteigen(GameObject car, Component vgs, int count,
                                      bool tank, string fraktion)
        {
            if (!RevivalPlugin.CfgPatrolCrew.Value || count <= 0) return;
            if (car == null) return;

            GameObject settlement = null;
            try
            {
                Type sType = RevivalPlugin.TypeByName("NPC_Settlement");
                Type pType = RevivalPlugin.TypeByName("NPC_SpawnPoint");
                Type wType = RevivalPlugin.TypeByName("NPC_WP");
                if (sType == null || pType == null || wType == null)
                {
                    RevivalPlugin.L.LogWarning("Crew: NPC_Settlement, NPC_SpawnPoint "
                        + "or NPC_WP not found - the wreck stays empty.");
                    return;
                }

                Vector3[] wo = Ausstiege(car, vgs, count);

                settlement = new GameObject(Name);
                settlement.transform.position = car.transform.position;

                GameObject leute = new GameObject("People");
                leute.transform.SetParent(settlement.transform, false);
                GameObject wege = new GameObject("WalkPoints");
                wege.transform.SetParent(settlement.transform, false);
                for (int i = 0; i < 8; i++)
                {
                    GameObject wp = new GameObject("WP" + i);
                    wp.transform.SetParent(wege.transform, false);
                    float a = i * Mathf.PI * 2f / 8f;
                    wp.transform.localPosition = new Vector3(
                        Mathf.Cos(a) * RingRadius, 0f, Mathf.Sin(a) * RingRadius);

                    // StartMainInit does not collect transforms here. It asks
                    // AllWalkPointsTr for NPC_WP components, then filters them
                    // by Type. Without this component every NPC receives an
                    // empty _mainWalkPoints list and IdleStateAction indexes
                    // element zero forever. NPC_WP::.ctor sets Type to 1,
                    // which is the settlement's ordinary patrol-point type.
                    wp.AddComponent(wType);

                    // An aggressive NPC switches to TemporaryTask Tactical
                    // when the settlement alarm is raised. That task reads
                    // NPC_WP type 5, not the patrol list above. With no type 5
                    // points RepairTemporaryWalkPointsList indexes an empty
                    // list on every state change and the otherwise complete
                    // AI freezes. Real tactical points carry default values;
                    // their point type is the complete behavioural contract.
                    GameObject tactical = new GameObject("Tactical" + i);
                    tactical.transform.SetParent(wege.transform, false);
                    float ta = a + Mathf.PI / 8f;
                    tactical.transform.localPosition = new Vector3(
                        Mathf.Cos(ta) * TacticalRingRadius, 0f,
                        Mathf.Sin(ta) * TacticalRingRadius);
                    Component tacticalPoint = tactical.AddComponent(wType);
                    SetEnum(tacticalPoint, "Type", "Tactical");
                }

                // The spawn points, one per man. They must exist BEFORE the
                // settlement component: StartMainInit collects them with
                // GetComponentsInChildren when _npcSpawnPoints is null.
                for (int i = 0; i < count; i++)
                {
                    GameObject sp = new GameObject("Crew" + i);
                    sp.transform.SetParent(settlement.transform, true);
                    sp.transform.position = wo[i];
                    Component punkt = sp.AddComponent(pType);
                    Listen(punkt, 0);
                    Abschreiben(punkt, VorlagePunkt(pType));
                    Component military = VorlageMilitaer(pType);
                    Abschreiben(punkt, military);
                    Punkt(punkt, military != null);
                }

                // An unregistered PhotonView - see the class comment.
                Type viewType = RevivalPlugin.TypeByName("PhotonView");
                if (viewType != null) settlement.AddComponent(viewType);

                Component sied = settlement.AddComponent(sType);
                Listen(sied, 0);
                Abschreiben(sied, VorlageSiedlung(sType, settlement));
                Siedlung(sied, leute.transform, wege.transform, fraktion);

                // The game's own entry point. On the master client it runs
                // InitSpawnNpc and InitSetupNpc, and those two build the men.
                Invoke(sied, "StartMainInit");
                Set(sied, "AllInitializationDone", true);
                Absichern(sied);

                string wer = Fraktion.Sauber(fraktion);
                if (wer.Length == 0) wer = "neutral";
                _settlements.Add(settlement);
                RevivalPlugin.L.LogInfo("Crew: " + count + " " + wer
                    + " out of the "
                    + (tank ? "tank" : "BTR") + " at " + car.transform.position
                    + " - " + _settlements.Count + " crew(s) on the ground.");
                Turret.Hinweis(count + " " + wer + " out of the wreck", 4f);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Crew: nobody climbed out - " + ex);
                if (settlement != null) UnityEngine.Object.Destroy(settlement);
            }
        }

        /// <summary>
        /// Runtime truth after the game's complete initialization. The spawn
        /// point already requests GodMode=false and InitSetupNpc normally sets
        /// IsInitialized=true. Repeat those two safety-critical facts on the
        /// resulting NPC and log them: damage returns immediately when either
        /// is wrong, so a future report no longer has to infer killability from
        /// a model on screen.
        /// </summary>
        static void Absichern(Component settlement)
        {
            FieldInfo nf = AccessTools.Field(settlement.GetType(), "NpcAI");
            Array npcs = nf == null ? null : nf.GetValue(settlement) as Array;
            if (npcs == null)
            {
                RevivalPlugin.L.LogWarning("Crew: NpcAI array missing after initialization.");
                return;
            }

            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs.GetValue(i) as Component;
                if (ai == null)
                {
                    RevivalPlugin.L.LogWarning("Crew: NPC " + i
                        + " missing after initialization.");
                    continue;
                }

                MethodInfo god = AccessTools.Method(ai.GetType(), "SetGodMode",
                    new Type[] { typeof(bool) }, null);
                if (god != null) god.Invoke(ai, new object[] { false });
                if (!Bool(ai, "IsInitialized")) Set(ai, "IsInitialized", true);
            }

            // Photon-spawned NPCs run Unity Start on the next frame. Until
            // then their game data is complete but _aimIk is still null.
            // SetGeneralAlarm calls ClearKillTarget, which dereferences that
            // component without a null check. Arm the real alarm only after
            // Unity has completed every NPC component; a delayed failure must
            // never escape into Aussteigen's spawn-cleanup catch.
            CrewAlarm delayed = settlement.gameObject.AddComponent<CrewAlarm>();
            delayed.Begin(settlement, npcs);
            CrewDrone.Begin(settlement.transform, npcs);
        }

        /// <summary>Enter the game's own settlement alarm once Unity Start has
        /// populated the components SetGeneralAlarm assumes. False with an
        /// empty problem means "not ready yet"; false with text is a real
        /// reflection/runtime failure for CrewAlarm to report once.</summary>
        internal static bool TryStartAlarm(Component settlement, Array npcs,
                                           out string problem)
        {
            problem = "";
            if (settlement == null || npcs == null) return false;

            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs.GetValue(i) as Component;
                if (ai == null) return false;
                FieldInfo af = AccessTools.Field(ai.GetType(), "_aimIk");
                object aim = af == null ? null : af.GetValue(ai);
                if (aim == null) return false;
                FieldInfo solver = AccessTools.Field(aim.GetType(), "solver");
                if (solver != null && solver.GetValue(aim) == null) return false;
            }

            // EnabledAI is copied as true before the NPCs exist. That field
            // alone does not play their legacy Animation: OnPlayerEnterZone
            // calls SetEnableNpcAi(true) only after a previous disable made
            // the settlement field false. Invoke the same local game method
            // once after Unity Start so every NPC enters its current
            // walk/combat animation even when the crew spawned far from the
            // player's body (for example while piloting the drone).
            MethodInfo enable = AccessTools.Method(settlement.GetType(),
                "SetEnableNpcAi", new Type[] { typeof(bool) }, null);
            if (enable == null)
            {
                problem = "NPC_Settlement.SetEnableNpcAi(bool) is missing";
                return false;
            }
            try { enable.Invoke(settlement, new object[] { true }); }
            catch (Exception ex)
            {
                Exception cause = ex.InnerException == null ? ex : ex.InnerException;
                problem = "crew animation activation failed - " + cause.Message;
                return false;
            }

            // THE SECOND SWITCH. `SetEnableNpcAi` alone leaves the crew frozen
            // and unhittable whenever the player body is not in PlayersAround
            // - the reasons are at PlayVisualizationPrefix. The prefix refuses
            // every later attempt to switch it off again; this is the one call
            // that switches it ON, and it has to be a real call because the
            // game does the work in a coroutine, not in the field.
            MethodInfo visual = AccessTools.Method(settlement.GetType(),
                "SetPlayVisualizationValue", new Type[] { typeof(bool) }, null);
            if (visual == null)
            {
                problem = "NPC_Settlement.SetPlayVisualizationValue(bool) is missing";
                return false;
            }
            try { visual.Invoke(settlement, new object[] { true }); }
            catch (Exception ex)
            {
                Exception cause = ex.InnerException == null ? ex : ex.InnerException;
                problem = "crew visualization activation failed - " + cause.Message;
                return false;
            }

            // The runtime settlement has no registered Photon view. Mark the
            // persistent local alarm before any NPC sensor can ask the
            // settlement to broadcast it through illegal view id 0.
            Set(settlement, "AlarmEnabled", true);
            SetNumber(settlement, "_alarmTimer", float.MaxValue);

            for (int i = 0; i < npcs.Length; i++)
            {
                Component ai = npcs.GetValue(i) as Component;
                MethodInfo alarm = AccessTools.Method(ai.GetType(),
                    "SetGeneralAlarm", new Type[] { typeof(bool) }, null);
                if (alarm == null)
                {
                    problem = "NPC_AI2.SetGeneralAlarm(bool) is missing";
                    Set(settlement, "AlarmEnabled", false);
                    return false;
                }
                try { alarm.Invoke(ai, new object[] { true }); }
                catch (Exception ex)
                {
                    Exception cause = ex.InnerException == null ? ex : ex.InnerException;
                    problem = "NPC " + i + " alarm failed - " + cause.Message;
                    Set(settlement, "AlarmEnabled", false);
                    return false;
                }
            }

            for (int i = 0; i < npcs.Length; i++)
                LogNpc(npcs.GetValue(i) as Component, i);
            return true;
        }

        static void LogNpc(Component ai, int index)
        {
            if (ai == null) return;
            FieldInfo sf = AccessTools.Field(ai.GetType(), "Specifications");
            object specs = sf == null ? null : sf.GetValue(ai);
            RevivalPlugin.L.LogInfo("Crew: NPC " + index
                + " initialized=" + Bool(ai, "IsInitialized")
                + ", enabled=" + Bool(ai, "EnabledAI")
                + ", sensor=" + Bool(ai, "SensorIsActive")
                + ", godmode=" + Bool(ai, "GodModeEnabled")
                + ", alarm=" + Bool(ai, "AlarmIsEnabled")
                + ", task=" + GetNumber(ai, "TemporaryTask").ToString("0")
                + ", tactical=" + Count(ai, "_temporaryWalkPoints")
                // The two that decide whether he moves and whether he can be
                // hit. `visual` false means Anim.enabled false and every
                // ragdoll rigidbody with detectCollisions off - frozen and
                // immune at the same time, see PlayVisualizationPrefix.
                + ", visual=" + Bool(ai, "IsPlayVisualizationEnabled")
                + ", safe=" + Bool(ai, "_isSafeSettlement")
                + ", health=" + GetNumber(specs, "Health").ToString("0.#")
                + "/" + GetNumber(specs, "HealthMax").ToString("0.#") + ".");
        }

        static bool Bool(object o, string name)
        {
            if (o == null) return false;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null || fi.FieldType != typeof(bool)) return false;
            try { return (bool)fi.GetValue(o); }
            catch { return false; }
        }

        static int Count(object o, string name)
        {
            if (o == null) return -1;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null) return -1;
            try
            {
                ICollection values = fi.GetValue(o) as ICollection;
                return values == null ? -1 : values.Count;
            }
            catch { return -1; }
        }

        /// <summary>Take every crew off the map again. Shift plus the patrol
        /// key does this, so a test run can be reset without a restart.</summary>
        public static void StopAll()
        {
            if (_settlements.Count == 0) return;
            int n = _settlements.Count;
            for (int i = 0; i < _settlements.Count; i++)
                if (_settlements[i] != null)
                    UnityEngine.Object.Destroy(_settlements[i]);
            _settlements.Clear();
            RevivalPlugin.L.LogInfo("Crew: " + n + " crew(s) removed.");
        }

        /// <summary>
        /// Where the men appear. The vehicle carries its own answer:
        /// `GetOutPoints` is a single Transform whose children Point0..Point9
        /// are the places the game itself puts a player who leaves the vehicle
        /// (RE 10). A ring around the hull is only the fallback.
        /// </summary>
        static Vector3[] Ausstiege(GameObject car, Component vgs, int count)
        {
            Vector3[] wo = new Vector3[count];
            Transform points = null;
            if (vgs != null)
            {
                FieldInfo fi = AccessTools.Field(vgs.GetType(), "GetOutPoints");
                if (fi != null) points = fi.GetValue(vgs) as Transform;
            }

            for (int i = 0; i < count; i++)
            {
                if (points != null && points.childCount > 0)
                {
                    wo[i] = points.GetChild(i % points.childCount).position;
                }
                else
                {
                    float a = i * Mathf.PI * 2f / Mathf.Max(1, count);
                    wo[i] = car.transform.position
                          + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 4.5f;
                }

                // Onto the ground. A man dropped at hatch height falls, and a
                // NavMeshAgent that starts in the air never finds the mesh.
                Vector3 boden;
                GameObject unter = Turret.RaycastObject(wo[i] + Vector3.up * 6f,
                                                        Vector3.down, 30f, out boden);
                if (unter != null) wo[i] = boden + Vector3.up * 0.1f;
            }
            return wo;
        }

        // ----------------------------------------------------- the template

        static Component _vorlageSiedlung, _vorlagePunkt, _vorlageMilitaer;
        static bool _vorlageGesucht;
        static bool _militaerGesucht;

        /// <summary>
        /// Fields a template must NOT hand over: they are running state, not
        /// a designer's choice, and a settlement that starts life already
        /// initialised never runs its own init.
        /// </summary>
        static readonly string[] Tabu = new string[] {
            "IsInitialized", "InitializationState", "AllInitializationDone",
            "AlarmEnabled", "IsPlayVisualizationEnabled",
            "IsMain", "SettlementType", "MyNPC", "Active",
        };

        /// <summary>
        /// The one thing an `AddComponent` cannot give us: the numbers the
        /// game's own level designers put on a settlement.
        ///
        /// A component built at runtime has whatever its constructor writes,
        /// and for a MonoBehaviour that is almost nothing - every float in the
        /// inspector is 0. `SensorVisibleDist` at 0 means a crew that cannot
        /// see; `UnitsPerEnemy` at 0, `AlarmTimeDelay` at 0, `FloorDelta*` at
        /// 0 are all the same kind of quiet wrong. Rather than invent a value
        /// for each, this copies them off a settlement THE MAP ALREADY HAS -
        /// found once per session, and then the handful that matter are
        /// overwritten by `Siedlung` on top.
        ///
        /// Three limits, each for its own reason:
        ///   value types and strings only   an object reference belongs to the
        ///       template's own scene objects and would point a wreck's crew at
        ///       some village's walk points. Lists are `Listen`'s job.
        ///   PUBLIC fields only             the designer's knobs are public and
        ///       the running state is private with an underscore -
        ///       `_aliveNPCsCount`, `_killedCount`, `_respawnQueueIndex`. A
        ///       fresh settlement that starts life believing twelve of its men
        ///       are already alive is worse than one with a zero in it.
        ///   the Tabu list                  the handful of PUBLIC fields that
        ///       are running state anyway. A settlement copied as already
        ///       initialised never initialises.
        /// </summary>
        static void Abschreiben(Component ziel, Component vorlage)
        {
            if (ziel == null || vorlage == null) return;
            if (vorlage.GetType() != ziel.GetType()) return;
            int n = 0;
            for (Type t = ziel.GetType(); t != null && t != typeof(MonoBehaviour);
                 t = t.BaseType)
            {
                FieldInfo[] fs = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                           | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fs.Length; i++)
                {
                    Type ft = fs[i].FieldType;
                    if (!ft.IsValueType && ft != typeof(string)) continue;
                    if (Verboten(fs[i].Name)) continue;
                    try { fs[i].SetValue(ziel, fs[i].GetValue(vorlage)); n++; }
                    catch { }
                }
            }
            if (n > 0)
                RevivalPlugin.L.LogInfo("Crew: " + n + " value(s) copied off an "
                    + "existing " + ziel.GetType().Name + ".");
        }

        static bool Verboten(string name)
        {
            for (int i = 0; i < Tabu.Length; i++)
                if (Tabu[i] == name) return true;
            return false;
        }

        /// <summary>A settlement of the map's own, never one of ours. The
        /// search costs a FindObjectsOfType and happens at most once a
        /// session - a vehicle has to be destroyed first.</summary>
        static Component VorlageSiedlung(Type sType, GameObject eigen)
        {
            Suchen(sType, null);
            if (_vorlageSiedlung != null && _vorlageSiedlung.gameObject == eigen)
                return null;
            return _vorlageSiedlung;
        }

        static Component VorlagePunkt(Type pType)
        {
            Suchen(null, pType);
            return _vorlagePunkt;
        }

        static Component VorlageMilitaer(Type pType)
        {
            if (_militaerGesucht) return _vorlageMilitaer;
            _militaerGesucht = true;
            try
            {
                UnityEngine.Object asset = Resources.Load(
                    "npcspawn/npc_premade/military_1_heavy");
                GameObject go = asset as GameObject;
                Component direct = asset as Component;
                _vorlageMilitaer = go == null ? direct : go.GetComponent(pType);
                RevivalPlugin.L.LogInfo("Crew: uniform military preset "
                    + (_vorlageMilitaer == null ? "NOT found"
                       : "military_1_heavy loaded") + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: military uniform preset - "
                    + ex.Message);
            }
            return _vorlageMilitaer;
        }

        static void Suchen(Type sType, Type pType)
        {
            if (_vorlageGesucht) return;
            _vorlageGesucht = true;
            try
            {
                if (sType == null) sType = RevivalPlugin.TypeByName("NPC_Settlement");
                if (pType == null) pType = RevivalPlugin.TypeByName("NPC_SpawnPoint");
                if (sType != null)
                {
                    UnityEngine.Object[] alle = UnityEngine.Object.FindObjectsOfType(sType);
                    for (int i = 0; i < alle.Length; i++)
                    {
                        Component c = alle[i] as Component;
                        if (c == null || c.gameObject.name == Name) continue;
                        _vorlageSiedlung = c;
                        break;
                    }
                }
                if (pType != null)
                {
                    UnityEngine.Object[] alle = UnityEngine.Object.FindObjectsOfType(pType);
                    for (int i = 0; i < alle.Length; i++)
                    {
                        Component c = alle[i] as Component;
                        if (c == null) continue;
                        if (c.transform.parent != null
                            && c.transform.parent.name == Name) continue;
                        _vorlagePunkt = c;
                        break;
                    }
                }
                RevivalPlugin.L.LogInfo("Crew: template settlement "
                    + (_vorlageSiedlung == null ? "NOT found - the crew will "
                       + "run on hand written numbers"
                       : "\"" + _vorlageSiedlung.gameObject.name + "\"")
                    + ", template spawn point "
                    + (_vorlagePunkt == null ? "NOT found"
                       : "\"" + _vorlagePunkt.gameObject.name + "\"") + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: looking for a template - "
                    + ex.Message);
            }
        }

        // ------------------------------------------------------- the values

        /// <summary>
        /// One spawn point. Only the fields whose meaning is CONFIRMED are
        /// written; everything else keeps the value the component ships with.
        /// The appearance comes from the shipped military_1_heavy preset. All
        /// spawn points receive the same seven item ids, so a group is a unit
        /// rather than a random collection. GrantWeaponType 1 is the confirmed
        /// fixed-id path in `NPC_Settlement::GetWeaponId`.
        /// </summary>
        static void Punkt(Component sp, bool militaryPreset)
        {
            Set(sp, "Active", true);
            SetNumber(sp, "Health", RevivalPlugin.CfgPatrolCrewHealth.Value);
            SetNumber(sp, "Level", RevivalPlugin.CfgPatrolCrewLevel.Value);
            SetEnum(sp, "BehaviorPattern", "Aggressive");
            SetNumber(sp, "NPCType", 0);              // -> prefab Marauder_NPC_01
            SetNumber(sp, "GrantWeaponType", 1);      // fixed WeaponId below
            SetNumber(sp, "WeaponId", 1160);          // NDR MG42
            if (!militaryPreset)
                SetNumber(sp, "AppearanceType", 0);   // safe vanilla fallback
            Set(sp, "UseCustomAppearance", militaryPreset);
            Set(sp, "UseIndividualFraction", false);  // the settlement decides
            Set(sp, "UseIndividualGodMode", true);
            Set(sp, "GodModeEnabled", false);
            Set(sp, "UseIndividualWalkPoints", false);
            Set(sp, "RandomWalkPoint", true);

            // PlayerInteractingManager always localizes this key before it
            // draws the NPC name plate. A runtime NPCQuestData has a null key,
            // and Dictionary.ContainsKey(null) aborts the whole hover path.
            // A non-localized key longer than five characters is deliberately
            // returned verbatim by LocalizationManager; the faction is drawn
            // separately from MainOptions.MyFraction.
            FieldInfo qf = AccessTools.Field(sp.GetType(), "Quests");
            object quests = qf == null ? null : qf.GetValue(sp);
            if (quests != null) Set(quests, "NameKey", "Patrol Crew");

            // Loot on the body is phase 5 and needs SpawnCategories, which is
            // a list this class has no business inventing. At 0 the game's own
            // GenerateOtherItems returns an empty array without ever reading
            // that list - the weapon in his hands still drops.
            SetNumber(sp, "RandomItemsCount", 0);
        }

        static void Siedlung(Component s, Transform leute, Transform wege,
                             string fraktion)
        {
            Set(s, "AllPeopleTr", leute);
            Set(s, "AllWalkPointsTr", wege);
            Set(s, "IsSafeSettlement", false);
            Set(s, "SensorIsEnabled", true);
            Set(s, "EnabledAI", true);
            Set(s, "VisibleNpc", true);
            Set(s, "IsIndoors", false);
            Set(s, "UseFloorBounds", false);
            Set(s, "UseCustomFractionOptions", true);
            SetNumber(s, "SettlementType", 0);
            SetNumber(s, "CheckPlayersDistRadius", 120f);
            SetNumber(s, "DisableAiTimer", 600f);

            // What a crewman can SEE. SetupNpcSettlements hands this straight
            // to NPC_AI2::SetSensorVisibleDist, and an AddComponent leaves it
            // at 0 - men who stand around their own wreck and never look up.
            // A template settlement will already have overwritten it with the
            // map's own number; this is the floor under that.
            if (GetNumber(s, "SensorVisibleDist") < 5f)
                SetNumber(s, "SensorVisibleDist",
                          RevivalPlugin.CfgPatrolCrewSensor.Value);
            if (GetNumber(s, "UnitsPerEnemy") < 1f) SetNumber(s, "UnitsPerEnemy", 1f);
            if (GetNumber(s, "AlarmTimeDelay") < 0.1f) SetNumber(s, "AlarmTimeDelay", 20f);

            // No second wave. RespawnQueueActions would put the crew back on
            // its feet at RespawnTimeInSec, and a wreck that keeps producing
            // gunmen is a bug report, not a feature.
            SetNumber(s, "RespawnTimeInSec", 1000000f);

            // WHO THEY SHOOT AT. `SetNpcParams` copies this object straight
            // into every NPC's `MainOptions`, and `IsEnemyFraction` walks its
            // `HatedFractions` - so this one field IS the answer to "which
            // side is this patrol on" (RE 23). An AddComponent leaves it null,
            // which is worse than wrong: `MainOptions` would be null on every
            // crewman and the first sensor hit would throw.
            object opts = Fraktion.Optionen(fraktion);
            if (opts != null)
            {
                Set(s, "FractionOptions", opts);
                RevivalPlugin.L.LogInfo("Crew: the crew is " + Fraktion.Sauber(fraktion)
                    + " - " + Fraktion.Erklaerung(fraktion) + ".");
            }
            Notausgang(s);
        }

        /// <summary>
        /// Whatever went wrong above, `HatedFractions` must not be null.
        ///
        /// `IsEnemyFraction` does `ldlen` on it with no null check (RE 23), so
        /// a crew whose fraction could not be built would not merely be
        /// harmless - it would throw on its first sensor hit, once per NPC per
        /// scan. `Listen` has already put an empty `NPCMainOptions` in the
        /// field; this fills its array with nothing, and a crew that attacks
        /// nobody is a bug you can walk away from.
        /// </summary>
        static void Notausgang(Component s)
        {
            try
            {
                FieldInfo fo = AccessTools.Field(s.GetType(), "FractionOptions");
                if (fo == null) return;
                object opts = fo.GetValue(s);
                if (opts == null)
                {
                    if (fo.FieldType.GetConstructor(Type.EmptyTypes) == null) return;
                    opts = Activator.CreateInstance(fo.FieldType);
                    fo.SetValue(s, opts);
                }
                FieldInfo hf = AccessTools.Field(opts.GetType(), "HatedFractions");
                if (hf == null || !hf.FieldType.IsArray) return;
                if (hf.GetValue(opts) != null) return;
                hf.SetValue(opts, Array.CreateInstance(
                    hf.FieldType.GetElementType(), 0));
                RevivalPlugin.L.LogWarning("Crew: the fraction could not be set - "
                    + "HatedFractions is an empty array, so this crew attacks "
                    + "nobody. It would otherwise have thrown on its first "
                    + "sensor scan.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: HatedFractions - " + ex.Message);
            }
        }

        // -------------------------------------------------------- reflection

        /// <summary>
        /// Every empty list the component would have brought with it out of a
        /// prefab, and does not bring out of `AddComponent`.
        ///
        /// This is what stopped the crew on the first run (E-033). A Unity
        /// component built at runtime gets exactly the fields its CONSTRUCTOR
        /// writes; a `List&lt;T&gt;` that the game only ever fills in the
        /// inspector is `null`. `NPC_Settlement::.ctor` builds `_enemysDic`
        /// and `_enemysTargets` and NOT `_enemysTr`, so the third line of
        /// `ClearEnemys` - the third call in `StartMainInit` - threw a
        /// NullReference before a single man was built. Seven more of the
        /// settlement's lists are in the same state, `TacticalPoints` and
        /// `enemyeOwnerId` among them, and those are read from `Update` while
        /// the crew is fighting.
        ///
        /// So: every instance field that is a plain data type with a
        /// parameterless constructor and is null gets an empty instance - which
        /// is precisely what a component placed in the editor has. That means
        /// `List&lt;T&gt;` and `Dictionary&lt;K,V&gt;`, and it ALSO means the
        /// serialisable little classes: `NPC_SpawnPoint.Quests` is an
        /// `NPCQuestData`, and its being null is what stopped the crew the
        /// SECOND time (2026-08-30, run two of E-033):
        ///
        ///     QuestManager.ConfigNPC   point.Quests.StartQuest.Count
        ///     NPC_Settlement.SetupNpcSettlements
        ///     NPC_Settlement.InitSetupNpc
        ///     NPC_Settlement.StartMainInit
        ///
        /// The men were already built by then - `InitSpawnNpc` runs first and
        /// had done its work - and this one null threw them all away again.
        /// Hence the RECURSION: an `NPCQuestData` built by `Activator` has the
        /// same problem one level down, its `StartQuest` list being null, and
        /// `ConfigNPC` reads exactly that.
        ///
        /// WHAT IS LEFT ALONE, and why each one:
        ///   ARRAYS      `StartMainInit` fills `_npcSpawnPoints` itself, but
        ///               only while it is null, and a helpful empty array here
        ///               would leave the settlement without any men. The one
        ///               array that MUST be filled, `HatedFractions`, is
        ///               written by `Fraktion.Optionen` by name.
        ///   UnityEngine.Object   a null Texture is a missing icon; a NEW
        ///               Texture is a leak, and `AddComponent` on a type we
        ///               guessed is worse.
        ///   string      null and "" behave the same everywhere the game
        ///               reads these, and `Abschreiben` copies strings anyway.
        /// </summary>
        static void Listen(object o, int tiefe)
        {
            if (o == null || tiefe > 3) return;
            int n = 0;
            for (Type t = o.GetType(); t != null && t != typeof(MonoBehaviour)
                 && t != typeof(object); t = t.BaseType)
            {
                FieldInfo[] fs = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                           | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fs.Length; i++)
                {
                    Type ft = fs[i].FieldType;
                    if (IstCache(fs[i].Name)) continue;
                    if (!Fuellbar(ft)) continue;
                    try
                    {
                        object hat = fs[i].GetValue(o);
                        if (hat != null)
                        {
                            if (!ft.IsGenericType) Listen(hat, tiefe + 1);
                            continue;
                        }
                        object neu = Activator.CreateInstance(ft);
                        fs[i].SetValue(o, neu);
                        n++;
                        if (!ft.IsGenericType) Listen(neu, tiefe + 1);
                    }
                    catch (Exception ex)
                    {
                        RevivalPlugin.L.LogWarning("Crew: field " + fs[i].Name
                            + " not filled - " + ex.Message);
                    }
                }
            }
            if (n > 0)
                RevivalPlugin.L.LogInfo("Crew: " + n + " empty value(s) put into "
                    + o.GetType().Name + ".");
        }

        /// <summary>Is this a type the Unity serialiser would have handed us an
        /// instance of? See the rules in <see cref="Listen"/>.</summary>
        static bool Fuellbar(Type ft)
        {
            if (ft.IsValueType || ft.IsArray) return false;
            if (ft == typeof(string)) return false;
            if (ft.IsAbstract || ft.IsInterface) return false;
            if (typeof(UnityEngine.Object).IsAssignableFrom(ft)) return false;
            if (typeof(Delegate).IsAssignableFrom(ft)) return false;
            return ft.GetConstructor(Type.EmptyTypes) != null;
        }

        /// <summary>
        /// Null is meaningful for these three lists: their getters use it as
        /// "not built yet" and then filter the NPC_WP components collected by
        /// StartMainInit. An empty list is not equivalent - PatrolPoints in
        /// particular is returned unchanged, every NPC receives it, and
        /// IdleStateAction indexes element zero forever. These are caches, not
        /// inspector data for Listen to recreate.
        /// </summary>
        static bool IstCache(string name)
        {
            return name == "PatrolPoints" || name == "EscapePoints"
                || name == "SleepPoints";
        }

        static void Set(object o, string name, object value)
        {
            if (o == null) return;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null)
            {
                RevivalPlugin.L.LogWarning("Crew: field " + name + " not on "
                    + o.GetType().Name + ".");
                return;
            }
            try { fi.SetValue(o, value); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: " + name + " not set - " + ex.Message);
            }
        }

        /// <summary>Reads a numeric field as a float, 0 when it is not
        /// there. Used to tell a value a template handed over from the 0 an
        /// AddComponent leaves behind.</summary>
        static float GetNumber(object o, string name)
        {
            if (o == null) return 0f;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null) return 0f;
            try
            {
                object v = fi.GetValue(o);
                if (v == null) return 0f;
                return Convert.ToSingle(v);
            }
            catch { return 0f; }
        }

        /// <summary>Writes a number into whatever numeric type the field
        /// happens to be. Health is an int in the spawn point and a float
        /// everywhere it is used, and guessing wrong throws.</summary>
        static void SetNumber(object o, string name, float value)
        {
            if (o == null) return;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null)
            {
                RevivalPlugin.L.LogWarning("Crew: field " + name + " not on "
                    + o.GetType().Name + ".");
                return;
            }
            try
            {
                Type t = fi.FieldType;
                if (t.IsEnum) fi.SetValue(o, Enum.ToObject(t, (int)value));
                else if (t == typeof(float)) fi.SetValue(o, value);
                else if (t == typeof(int)) fi.SetValue(o, (int)value);
                else if (t == typeof(double)) fi.SetValue(o, (double)value);
                else fi.SetValue(o, Convert.ChangeType(value, t));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: " + name + " not set - " + ex.Message);
            }
        }

        /// <summary>An enum field by NAME. The numbers behind
        /// NPCBehaviorPattern and Fraction are known (RE 10), but a name that
        /// no longer exists says so in the log instead of quietly picking the
        /// wrong behaviour.</summary>
        static void SetEnum(object o, string name, string value)
        {
            if (o == null) return;
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null || !fi.FieldType.IsEnum)
            {
                RevivalPlugin.L.LogWarning("Crew: enum field " + name + " not on "
                    + o.GetType().Name + ".");
                return;
            }
            try { fi.SetValue(o, Enum.Parse(fi.FieldType, value, true)); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crew: " + name + " = " + value
                    + " not set - " + ex.Message);
            }
        }

        static void Invoke(object o, string name)
        {
            if (o == null) return;
            MethodInfo mi = AccessTools.Method(o.GetType(), name, null, null);
            if (mi == null)
                throw new MissingMethodException(o.GetType().Name + "." + name);
            mi.Invoke(o, null);
        }
    }

    /// <summary>
    /// Repairs the visual half of a runtime crew on non-master clients. The
    /// scene settlement lookup cannot succeed for view id zero, so vanilla
    /// applies the weapon but never the seven customization items. Waiting a
    /// few frames also covers the legacy Animation component created by the
    /// NPC prefab after its network spawn.
    /// </summary>
    public sealed class CrewRemoteFix : MonoBehaviour
    {
        Component _ai;
        int[] _appearance;
        int _weapon;
        float _next;
        float _deadline;
        string _problem = "";

        public void Begin(Component ai, int[] appearance, int weapon)
        {
            _ai = ai;
            _appearance = appearance;
            _weapon = weapon;
            _next = Time.time + 0.1f;
            _deadline = Time.time + 6f;
        }

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.25f;
            if (Crew.ApplyRemoteAppearance(_ai, _appearance, _weapon,
                                           out _problem))
            {
                RevivalPlugin.L.LogInfo("Crew: remote uniform, MG42 and animation "
                    + "restored.");
                UnityEngine.Object.Destroy(this);
                return;
            }
            if (Time.time < _deadline) return;
            RevivalPlugin.L.LogWarning("Crew: remote appearance repair timed out"
                + (_problem.Length == 0 ? "." : " - " + _problem + "."));
            UnityEngine.Object.Destroy(this);
        }
    }

    /// <summary>
    /// The crew NPCs are created inside NPC_Settlement.StartMainInit. Their
    /// Unity Start methods, including FinalIK discovery, run after that call
    /// returns. This tiny one-shot waits for those components and then enters
    /// the real alarm state. It never owns or replaces NPC behaviour.
    /// </summary>
    public sealed class CrewAlarm : MonoBehaviour
    {
        Component _settlement;
        Array _npcs;
        float _next;
        float _deadline;
        string _lastProblem = "";

        public void Begin(Component settlement, Array npcs)
        {
            _settlement = settlement;
            _npcs = npcs;
            _next = Time.time + 0.25f;
            _deadline = Time.time + 10f;
        }

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.25f;

            string problem;
            if (Crew.TryStartAlarm(_settlement, _npcs, out problem))
            {
                RevivalPlugin.L.LogInfo("Crew: aggravated settlement AI is active.");
                UnityEngine.Object.Destroy(this);
                return;
            }
            if (problem.Length > 0) _lastProblem = problem;
            if (Time.time < _deadline) return;

            RevivalPlugin.L.LogWarning("Crew: spawned normally, but aggravated "
                + "state did not start within 10 s"
                + (_lastProblem.Length == 0 ? "." : " - " + _lastProblem + "."));
            UnityEngine.Object.Destroy(this);
        }
    }

    /// <summary>
    /// One disposable FPV drone per dismounted crew. The master client uses
    /// the crew's real kill target, which preserves the game's faction rules.
    /// A fixed lateral error keeps it dangerous without making it a perfect
    /// homing missile. Other clients receive only its transform and may report
    /// a firearm hit back to the owner.
    /// </summary>
    public static class CrewDrone
    {
        class Pending
        {
            public Transform Root;
            public Array Npcs;
            public float At;
        }

        class Local
        {
            public int Id;
            public GameObject Go;
            public GameObject Target;
            public Vector3 Pos;
            public Vector3 Dir;
            public Vector3 Error;
            public float Hp;
            public float Armed;
            public float Deadline;
            public float NextSend;
        }

        class Remote
        {
            public int Owner;
            public int Id;
            public GameObject Go;
            public Vector3 From;
            public Vector3 To;
            public Vector3 Dir;
            public float T;
            public float Duration;
            public float Last;
        }

        static readonly List<Pending> _pending = new List<Pending>();
        static readonly List<Local> _local = new List<Local>();
        static readonly Dictionary<string, Remote> _remote =
            new Dictionary<string, Remote>();
        static readonly List<string> _remove = new List<string>();
        static int _nextId = 1;

        public static void Begin(Transform root, Array npcs)
        {
            if (RevivalPlugin.CfgPatrolCrewDrone == null
                || !RevivalPlugin.CfgPatrolCrewDrone.Value
                || root == null || npcs == null || npcs.Length == 0) return;
            Pending p = new Pending();
            p.Root = root;
            p.Npcs = npcs;
            p.At = Time.time + Mathf.Max(1f,
                RevivalPlugin.CfgPatrolCrewDroneDelay.Value);
            _pending.Add(p);
        }

        public static void Tick()
        {
            if (RevivalPlugin.CfgPatrolCrewDrone == null
                || !RevivalPlugin.CfgPatrolCrewDrone.Value) return;
            Net.EnsureHooked();
            Net.TickRemotes();

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                if (p.Root == null) { _pending.RemoveAt(i); continue; }
                if (Time.time < p.At) continue;
                GameObject target = FindTarget(p.Npcs);
                if (target == null) continue;
                Launch(p.Root, target);
                _pending.RemoveAt(i);
            }

            for (int i = _local.Count - 1; i >= 0; i--)
                Move(_local[i]);
        }

        static GameObject FindTarget(Array npcs)
        {
            for (int i = 0; i < npcs.Length; i++)
            {
                object ai = npcs.GetValue(i);
                if (ai == null) continue;
                FieldInfo targetField = AccessTools.Field(ai.GetType(), "_killTarget");
                object raw = targetField == null ? null : targetField.GetValue(ai);
                GameObject go = raw as GameObject;
                Component component = raw as Component;
                if (go == null && component != null) go = component.gameObject;
                if (go != null && go.activeInHierarchy) return go;
            }
            return null;
        }

        static void Launch(Transform root, GameObject target)
        {
            Local d = new Local();
            d.Id = _nextId++;
            d.Target = target;
            d.Pos = root.position + Vector3.up * 3.2f;
            Vector3 to = AimPoint(target) - d.Pos;
            d.Dir = to.sqrMagnitude < 0.01f ? root.forward : to.normalized;

            float miss = Mathf.Max(0f, RevivalPlugin.CfgPatrolCrewDroneMiss.Value);
            float angle = Mathf.Abs(Mathf.Sin(d.Id * 12.9898f)) * Mathf.PI * 2f;
            float radius = miss * (0.45f + 0.55f
                * Mathf.Abs(Mathf.Sin(d.Id * 78.233f)));
            d.Error = new Vector3(Mathf.Cos(angle) * radius,
                                  radius * 0.12f,
                                  Mathf.Sin(angle) * radius);
            d.Hp = Mathf.Max(1, RevivalPlugin.CfgPatrolCrewDroneHitpoints.Value);
            d.Armed = Time.time + 1.2f;
            d.Deadline = Time.time + 55f;
            d.Go = Drone.Modell.Bauen();
            d.Go.name = "NDR Crew FPV " + d.Id;
            d.Go.transform.localScale *= 1.15f;
            d.Go.transform.position = d.Pos;
            d.Go.transform.rotation = Quaternion.LookRotation(d.Dir, Vector3.up);
            Drone.Sound.Anhaengen(d.Go);
            _local.Add(d);
            Net.Send(Net.Start, d.Id, d.Pos, d.Dir, 0f, true);
            RevivalPlugin.L.LogInfo("Crew FPV " + d.Id + " launched at player "
                + target.name + " with " + radius.ToString("0.0")
                + " m deliberate miss.");
        }

        static Vector3 AimPoint(GameObject target)
        {
            return target == null ? Vector3.zero
                : target.transform.position + Vector3.up * 1.1f;
        }

        static void Move(Local d)
        {
            if (d == null || d.Go == null)
            {
                if (d != null) _local.Remove(d);
                return;
            }
            if (Time.time >= d.Deadline)
            {
                Finish(d, d.Pos, false, "flight timeout");
                return;
            }

            Vector3 aim = d.Target == null ? d.Pos + d.Dir * 20f
                : AimPoint(d.Target) + d.Error;
            Vector3 wanted = aim - d.Pos;
            if (wanted.sqrMagnitude > 0.001f) wanted.Normalize();
            else wanted = d.Dir;

            Vector3 right = Vector3.Cross(Vector3.up, wanted);
            if (right.sqrMagnitude > 0.001f) right.Normalize();
            wanted += right * Mathf.Sin(Time.time * 2.1f + d.Id) * 0.055f;
            wanted += Vector3.up * Mathf.Sin(Time.time * 1.4f + d.Id * 0.7f) * 0.025f;
            wanted.Normalize();
            d.Dir = Vector3.Slerp(d.Dir, wanted,
                Mathf.Clamp01(Time.deltaTime * 2.4f)).normalized;

            float speed = Mathf.Max(3f, RevivalPlugin.CfgPatrolCrewDroneSpeed.Value);
            Vector3 step = d.Dir * speed * Time.deltaTime;
            float length = step.magnitude;
            if (Time.time >= d.Armed && length > 0.001f)
            {
                Vector3 hit;
                GameObject struck = Turret.RaycastObject(d.Pos, d.Dir,
                                                         length + 0.25f, out hit);
                if (struck != null)
                {
                    Finish(d, hit, true, "impact");
                    return;
                }
            }

            d.Pos += step;
            d.Go.transform.position = d.Pos;
            d.Go.transform.rotation = Quaternion.LookRotation(d.Dir, Vector3.up);
            if (Time.time >= d.Armed && Vector3.Distance(d.Pos, aim) < 1.1f)
            {
                Finish(d, d.Pos, true, "target area");
                return;
            }
            if (Time.time >= d.NextSend)
            {
                d.NextSend = Time.time + 0.08f;
                Net.Send(Net.Move, d.Id, d.Pos, d.Dir, 0f, false);
            }
        }

        static void Finish(Local d, Vector3 point, bool explode, string why)
        {
            if (!_local.Remove(d)) return;
            Net.Send(Net.End, d.Id, point, d.Dir, explode ? 1f : 0f, true);
            if (d.Go != null) UnityEngine.Object.Destroy(d.Go);
            if (explode)
            {
                try
                {
                    RocketHook.Detonate(point,
                        RevivalPlugin.CfgPatrolCrewDroneDamage.Value,
                        RevivalPlugin.CfgPatrolCrewDroneRadius.Value, 3f);
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Crew FPV explosion: " + ex.Message);
                }
            }
            RevivalPlugin.L.LogInfo("Crew FPV " + d.Id + " ended: " + why + ".");
        }

        public static bool Beschuss(Vector3 origin, Vector3 dir, float range,
                                    float damage)
        {
            if (dir.sqrMagnitude < 0.000001f) return false;
            dir.Normalize();
            float radius = RevivalPlugin.CfgPatrolCrewDroneHitRadius == null
                ? 2f : Mathf.Max(0.25f,
                    RevivalPlugin.CfgPatrolCrewDroneHitRadius.Value);
            float nearest = float.MaxValue;
            Local localHit = null;
            Remote remoteHit = null;

            for (int i = 0; i < _local.Count; i++)
            {
                Local d = _local[i];
                if (d.Go == null) continue;
                float t = Along(origin, dir, d.Pos, range, radius);
                if (t > 0f && t < nearest)
                { nearest = t; localHit = d; remoteHit = null; }
            }
            foreach (KeyValuePair<string, Remote> pair in _remote)
            {
                Remote d = pair.Value;
                if (d.Go == null) continue;
                float t = Along(origin, dir, d.Go.transform.position, range, radius);
                if (t > 0f && t < nearest)
                { nearest = t; localHit = null; remoteHit = d; }
            }
            if (localHit == null && remoteHit == null) return false;

            Vector3 obstruction;
            if (nearest > 2.2f && Turret.RaycastObject(origin + dir, dir,
                    nearest - 1.2f, out obstruction) != null) return false;

            if (localHit != null)
            {
                localHit.Hp -= Mathf.Max(0.1f, damage);
                if (localHit.Hp <= 0f)
                    Finish(localHit, localHit.Pos, Time.time >= localHit.Armed,
                           "shot down");
            }
            else
            {
                Net.Send(Net.Hit, remoteHit.Id, remoteHit.Go.transform.position,
                         Vector3.zero, remoteHit.Owner, true);
            }
            Turret.Hinweis("Crew drone hit", 0.6f);
            return true;
        }

        static float Along(Vector3 origin, Vector3 dir, Vector3 point,
                           float range, float radius)
        {
            Vector3 to = point - origin;
            float t = Vector3.Dot(to, dir);
            if (t < 1f || t > range) return -1f;
            return (to - dir * t).sqrMagnitude <= radius * radius ? t : -1f;
        }

        public static class Net
        {
            public const int Start = 1;
            public const int Move = 2;
            public const int End = 3;
            public const int Hit = 4;

            static bool _hooked;
            static bool _failed;
            static MethodInfo _raise;
            static Type _optionsType;
            static FieldInfo _eventField;

            public static void EnsureHooked()
            {
                if (_hooked || _failed) return;
                try
                {
                    int code = RevivalPlugin.CfgPatrolCrewDroneEventCode.Value;
                    int playerDrone = RevivalPlugin.CfgDroneEventCode.Value;
                    if (code < 0 || code > 199
                        || (code >= playerDrone && code <= playerDrone + 4)
                        || code == RevivalPlugin.CfgTurretEventCode.Value
                        || code == RevivalPlugin.CfgAdminEventCode.Value)
                        throw new Exception("event code overlaps another feature");
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null) throw new Exception("PhotonNetwork missing");
                    _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                    _eventField = AccessTools.Field(photon, "OnEventCall");
                    _optionsType = RevivalPlugin.TypeByName("RaiseEventOptions");
                    if (_raise == null || _eventField == null)
                        throw new Exception("event reflection path incomplete");
                    MethodInfo own = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static);
                    Delegate handler = Delegate.CreateDelegate(_eventField.FieldType, own);
                    Delegate current = _eventField.GetValue(null) as Delegate;
                    _eventField.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("Crew FPV network hooked on event "
                        + code + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("Crew FPV network hook: " + ex);
                }
            }

            public static void Send(int action, int id, Vector3 point, Vector3 dir,
                                    float extra, bool reliable)
            {
                EnsureHooked();
                if (!_hooked) return;
                try
                {
                    float[] data = new float[] { action, id,
                        point.x, point.y, point.z, dir.x, dir.y, dir.z, extra };
                    object options = _optionsType == null ? null
                        : Activator.CreateInstance(_optionsType);
                    _raise.Invoke(null, new object[] {
                        (byte)RevivalPlugin.CfgPatrolCrewDroneEventCode.Value,
                        data, reliable, options });
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Crew FPV network send: " + ex.Message);
                }
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                if (RevivalPlugin.CfgPatrolCrewDroneEventCode == null
                    || code != (byte)RevivalPlugin.CfgPatrolCrewDroneEventCode.Value)
                    return;
                try
                {
                    float[] data = content as float[];
                    if (data == null || data.Length < 9) return;
                    int action = Mathf.RoundToInt(data[0]);
                    int id = Mathf.RoundToInt(data[1]);
                    Vector3 point = new Vector3(data[2], data[3], data[4]);
                    Vector3 dir = new Vector3(data[5], data[6], data[7]);
                    if (action == Hit)
                    {
                        if (Mathf.RoundToInt(data[8]) != Admin.Net.OwnActor()) return;
                        for (int i = 0; i < _local.Count; i++)
                        {
                            Local own = _local[i];
                            if (own.Id != id) continue;
                            own.Hp -= 1f;
                            if (own.Hp <= 0f)
                                Finish(own, own.Pos, Time.time >= own.Armed,
                                       "shot down by player #" + sender);
                            return;
                        }
                        return;
                    }
                    if (sender == Admin.Net.OwnActor()) return;
                    string key = sender + ":" + id;
                    if (action == End)
                    {
                        RemoveRemote(key);
                        return;
                    }

                    Remote remote;
                    if (!_remote.TryGetValue(key, out remote))
                    {
                        remote = new Remote();
                        remote.Owner = sender;
                        remote.Id = id;
                        remote.Go = Drone.Modell.Bauen();
                        remote.Go.name = "NDR Remote Crew FPV " + key;
                        remote.Go.transform.localScale *= 1.15f;
                        remote.Go.transform.position = point;
                        Drone.Sound.Anhaengen(remote.Go);
                        remote.From = point;
                        _remote[key] = remote;
                    }
                    else remote.From = remote.Go == null
                        ? point : remote.Go.transform.position;
                    remote.To = point;
                    remote.Dir = dir.sqrMagnitude < 0.001f
                        ? Vector3.forward : dir.normalized;
                    remote.Duration = remote.Last <= 0f ? 0.08f
                        : Mathf.Clamp(Time.time - remote.Last, 0.02f, 0.3f);
                    remote.Last = Time.time;
                    remote.T = 0f;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Crew FPV network receive: "
                        + ex.Message);
                }
            }

            public static void TickRemotes()
            {
                _remove.Clear();
                foreach (KeyValuePair<string, Remote> pair in _remote)
                {
                    Remote r = pair.Value;
                    if (r.Go == null || Time.time - r.Last > 4f)
                    { _remove.Add(pair.Key); continue; }
                    r.T = Mathf.Min(1f, r.T + Time.deltaTime
                        / Mathf.Max(0.02f, r.Duration));
                    r.Go.transform.position = Vector3.Lerp(r.From, r.To, r.T);
                    r.Go.transform.rotation = Quaternion.LookRotation(r.Dir,
                                                                       Vector3.up);
                }
                for (int i = 0; i < _remove.Count; i++) RemoveRemote(_remove[i]);
            }

            static void RemoveRemote(string key)
            {
                Remote r;
                if (!_remote.TryGetValue(key, out r)) return;
                if (r.Go != null) UnityEngine.Object.Destroy(r.Go);
                _remote.Remove(key);
            }
        }
    }

    // ------------------------------------------------------------ FPV-Drohne

    /// <summary>
    /// Eine Wegwerfdrohne: Taste druecken, durch ihre Kamera fliegen, beim
    /// ersten Treffer detoniert sie. Der Koerper des Piloten bleibt stehen,
    /// wo er steht, und ist waehrenddessen angreifbar.
    ///
    /// Technisch ist sie **eine Turmkamera, die sich bewegen darf**. Die zwei
    /// schwierigen Teile waren vorher da: `CameraOwner` haelt den Blick, und
    /// `RocketHook.Detonate` zuendet eine Granatenexplosion, die alle sehen
    /// und spueren (belegt an der M72 LAW). Neu ist nur der Integrator hier
    /// und eine Kollisionsabfrage per Strahl.
    ///
    /// KEIN Rigidbody und keine Unity-Physik. Ein Rigidbody wuerde mit Photon
    /// und den Collidern der Welt Dinge tun, die wir nicht kontrollieren, und
    /// wir brauchen keine Aerodynamik - wir brauchen ein Flugbild, das sich
    /// wie eine Drohne anfuehlt, und das entsteht aus Traegheit und Daempfung.
    ///
    /// BELEGT (IL von Assembly-CSharp.dll, gelesen am 2026-08-28):
    ///
    ///   Das Spiel hat eine ganze Familie von Sperr-Praedikaten, alle mit
    ///   Rueckgabe bool und der Bedeutung "jetzt gerade nicht":
    ///       PlayerMovementController::PlayerCantMovement / PlayerCantRotate /
    ///       PlayerCantRotateAxisX / PlayerCantJump / PlayerCantRun
    ///       PlayerFirearmWeaponController::CantShoot
    ///       PlayerMeleeWeaponController::MeleeCantAttack
    ///       PlayerGrenadeWeaponController::CantThrowGrenade
    ///       PlayerInteractingManager::CantInteractWithItem
    ///       MouseOrbitController::PlayerCantOrbitRotate
    ///   `PlayerCantMovement` liefert unter anderem dann true, wenn
    ///   `_uiController.CantPlayerControlWhenOpenedUI()` true ist oder der
    ///   Spieler auf einem Sitz sitzt. Genau dieselbe Tuer benutzt die
    ///   Drohne (DroneInputHook): ein Postfix, der waehrend des Fluges true
    ///   erzwingt. Das ist der Weg des Spiels, nicht ein Abschalten von
    ///   Skripten - laeuft die Drohne aus, ist der Spieler ohne Aufraeumen
    ///   sofort wieder normal steuerbar.
    ///
    ///   PhotonNetwork::RaiseEvent(byte code, object inhalt, bool zuverlaessig,
    ///   RaiseEventOptions) verwirft alles ab Code 200 - 0..199 sind frei.
    ///   Im ganzen Spiel ruft das genau EINE Stelle auf (PunTurnManager::
    ///   SendMove, mit den Codes 1 und 2), unsere 176-178 sind also frei.
    ///   NetworkingPeer::OnEvent ruft fuer Code &lt; 200 das statische FELD
    ///   PhotonNetwork.OnEventCall mit (Code, EventData[245], senderId) auf.
    ///   Ein Feld, kein C#-Event: es laesst sich lesen, per Delegate.Combine
    ///   erweitern und zurueckschreiben - ohne die Photon-DLL zu
    ///   referenzieren und ohne einen eigenen RPC anzumelden.
    ///
    /// UNGEPRUEFT: alles, was Augen braucht. Steht in TASKS.md unter
    /// "Abnahme im Spiel".
    /// </summary>
    public static class Drone
    {
        static bool _flying;
        static Vector3 _pos;
        static Vector3 _vel;
        static float _yaw;                   // Grad, 0 = Welt-Z
        static float _pitch;                 // Grad, positiv = Nase hoch
        static float _armed;                 // ab wann eine Kollision zaehlt
        static float _start;                 // Time.time beim Start
        static Vector3 _home;                // wo der Pilot steht
        static Transform _pilotRoot;
        static float _nextNet;
        static float _nextHeight;
        static float _height = -1f;
        static Texture2D _dot;               // 1x1 weiss, fuer die Einblendung
        static AudioSource _ownHum;
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static float _hp;                    // Trefferpunkte, was davon uebrig ist
        static float _markeBis;              // Trefferzeichen fuer den SCHUETZEN
        static bool _markeTot;
        static MethodInfo _spielerGet;       // PhotonNetwork.player
        static MethodInfo _nummerGet;        // PhotonPlayer.ID
        static bool _nummerGesucht;

        public static bool Flying { get { return _flying; } }
        public static Vector3 Position { get { return _pos; } }
        public static Vector3 Home { get { return _home; } }

        /// <summary>
        /// Wo der Koerper des Piloten steht, waehrend die Drohne fliegt.
        /// `DroneNpcHook` braucht das, um in der Spielerliste des Spiels die
        /// eine Zeile zu erkennen, die der Pilot selbst ist. `_pilotRoot` wird
        /// beim Start gesetzt; fehlt es, ist `_home` dieselbe Stelle - da hat
        /// er beim Start gestanden, und wer eine Drohne fliegt, laeuft nicht.
        /// </summary>
        internal static bool PilotAt(out Vector3 p)
        {
            p = Vector3.zero;
            if (!_flying) return false;

            // THE BODY, NOT THE LAUNCH CAMERA (2026-08-30).
            //
            // `DroneNpcHook` uses this to recognise the one row in the game's
            // own player list that is the pilot, and it allows two metres. On
            // foot that holds - the camera stands over the body. In a vehicle
            // it does not: the camera sits at the turret, several metres from
            // the body, no row matches, and the settlement is never told that
            // the drone is there. The crew then stays frozen while the drone
            // hovers over it, which is exactly the reported case.
            //
            // The reason the launch camera was used at all - transform.root
            // being the shared "ServerObjects" container - was fixed in
            // `LocalPlayerRoot` itself (E-039). It returns the transform that
            // IS the listed player object, so the match is now exact.
            if (_pilotRoot == null) _pilotRoot = LocalPlayerRoot();
            if (_pilotRoot != null) { p = _pilotRoot.position; return true; }
            p = _home;
            return true;
        }
        public static float FlightTime { get { return Time.time - _start; } }

        static float ArmDelay() { return RevivalPlugin.CfgDroneArmDelay.Value; }

        // ------------------------------------------------------------- Takt

        public static void Tick()
        {
            if (!RevivalPlugin.CfgDrone.Value) return;
            try
            {
                Net.EnsureHooked();
                Net.TickRemotes();

                if (Input.GetKeyDown(Key()))
                {
                    if (_flying) TasteImFlug();
                    else Launch();
                }
                if (_flying)
                {
                    Steer();
                    Move();
                    DroneNpcFire.Tick();
                }
                // Last, and after Move: the jammer may end the flight, and it
                // measures against the position of THIS frame. It also runs
                // while nothing flies - carrying a jammer is what makes a
                // player dangerous to other people's drones.
                Jammer.Tick();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Drohne: " + ex);
                Land(Grund.Abbruch);
            }
        }

        /// <summary>
        /// Setzt die Kamera in die Drohne. Wird von CameraOwner.LateTick
        /// gerufen, und nur dann, wenn die Drohne den Blick haelt.
        /// </summary>
        public static void LateTick()
        {
            if (!_flying) return;
            try
            {
                Camera cam = CameraOwner.ViewCamera();
                if (cam == null) return;
                cam.transform.position = _pos;
                cam.transform.rotation = Quaternion.LookRotation(Forward(), Vector3.up);
                // Bildwinkel jeden Frame nachziehen, nicht einmal beim Start:
                // Zielfernrohr- und Sprinteffekte des Spiels schreiben ihn
                // sonst wieder um. Derselbe Grund wie beim Geschuetz.
                if (RevivalPlugin.CfgDroneFov.Value > 1f)
                    cam.fieldOfView = RevivalPlugin.CfgDroneFov.Value;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Drohnenkamera: " + ex);
                Land(Grund.Abbruch);
            }
        }

        // ------------------------------------------------------ Start, Ende

        /// <summary>Warum der Flug endet. Geht als Zahl ueber das Netz.</summary>
        public static class Grund
        {
            public const int Detonation = 1;
            public const int Absturz = 2;
            public const int Abriss = 3;
            public const int Abbruch = 4;
        }

        static void Launch()
        {
            Camera cam = CameraOwner.ViewCamera();
            if (cam == null)
            {
                RevivalPlugin.L.LogWarning("Drohne: keine Kamera gefunden.");
                return;
            }
            if (!CameraOwner.Free)
            {
                RevivalPlugin.L.LogInfo("Drohne: der Blick ist gerade vergeben - "
                    + "erst das Geschuetz verlassen.");
                return;
            }

            Vector3 f = cam.transform.forward;
            _home = cam.transform.position;
            _pos = _home + f * RevivalPlugin.CfgDroneLaunchForward.Value
                 + Vector3.up * RevivalPlugin.CfgDroneLaunchUp.Value;
            _vel = f * RevivalPlugin.CfgDroneLaunchSpeed.Value;
            _yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            _pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            _pilotRoot = LocalPlayerRoot();

            // Erst die Drohne aus dem Rucksack nehmen, DANN die Kamera holen.
            // Andersherum haette ein leerer Rucksack den Blick uebernommen und
            // sofort wieder zurueckgegeben - ein Zucken im Bild fuer nichts.
            if (RevivalPlugin.CfgDroneRequireItem.Value
                && !Turret.TakeItem(RevivalPlugin.CfgDroneItemId.Value, "Drohne"))
            {
                RevivalPlugin.L.LogInfo("Drohne: keine im Rucksack (Item "
                    + RevivalPlugin.CfgDroneItemId.Value + ").");
                return;
            }

            if (!CameraOwner.Request(CameraOwner.Drohne, true, "Drohne")) return;

            _flying = true;
            _start = Time.time;
            _armed = Time.time + ArmDelay();
            _hp = Mathf.Max(1, RevivalPlugin.CfgDroneHitpoints.Value);
            Jammer.Reset();
            DroneNpcFire.Reset();
            _nextNet = 0f;
            Net.Send(Net.Start, _pos, Forward(), 0f, true);
            StartOwnHum();

            RevivalPlugin.L.LogInfo("Drohne gestartet bei " + _pos
                + ", Blickrichtung " + Forward()
                + ", Pilot \"" + (_pilotRoot == null ? "unbekannt" : _pilotRoot.name)
                + "\". Scharf ab " + ArmDelay() + " s.");
        }

        /// <summary>
        /// Beendet den Flug. Muss IMMER laufen - sonst behaelt die Drohne den
        /// Blick, und der Spieler sieht bis zum Neustart durch eine Kamera,
        /// die niemand mehr bewegt.
        /// </summary>
        static void Land(int grund)
        {
            if (!_flying) return;
            _flying = false;
            try
            {
                if (grund != Grund.Detonation)
                    Net.Send(Net.Ende, _pos, Forward(), (float)grund, true);
                StopOwnHum();
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Drohne beenden: " + ex); }
            finally
            {
                DroneNpcFire.Reset();
                CameraOwner.Release(CameraOwner.Drohne);
            }
            RevivalPlugin.L.LogInfo("Drohne beendet (" + GrundText(grund)
                + "), Blick zurueck beim Koerper.");
        }

        /// <summary>
        /// The drone key, pressed while flying. Since 0.5.3 that is the
        /// detonator and no longer an exit: whoever takes his hand off the drone
        /// blows it up. It cannot come back anyway - the flight always ended
        /// with the drone gone, only until now it ended without a bang.
        ///
        /// Two exceptions, and both matter more than the rule:
        ///
        ///   too close      Nearer to the pilot than SafeRadius the drone only
        ///                  lands. A hand on the wrong key must not be able to
        ///                  kill the pilot, and right after the start the drone
        ///                  is by definition next to him.
        ///   not armed yet  Before ArmDelay has passed nothing detonates - the
        ///                  same rule that keeps the first frame from blowing up
        ///                  in the pilot's face.
        ///
        /// Every other way out (empty battery, signal lost, an error in Tick)
        /// keeps going through Land, unchanged.
        /// </summary>
        static void TasteImFlug()
        {
            if (RevivalPlugin.CfgDroneSelfDestruct == null
                || !RevivalPlugin.CfgDroneSelfDestruct.Value)
            {
                Land(Grund.Abbruch);
                return;
            }

            float abstand = Vector3.Distance(_pos, _home);
            if (Time.time < _armed
                || abstand < RevivalPlugin.CfgDroneSafeRadius.Value)
            {
                RevivalPlugin.L.LogInfo("Drohne: zu nah am Piloten ("
                    + abstand.ToString("0.0") + " m, Sicherheitsabstand "
                    + RevivalPlugin.CfgDroneSafeRadius.Value
                    + " m) - sie wird abgesetzt statt gezuendet.");
                Land(Grund.Abbruch);
                return;
            }

            Impact(_pos);
        }

        // ---------------------------------------------------------- Abschuss

        /// <summary>
        /// A hit somebody else's client reported. Only the pilot acts on it,
        /// and only for his own drone.
        ///
        /// Which drone is meant is the actor number, with the distance as a
        /// fallback: `PhotonNetwork.player` is read through reflection, and a
        /// drone that cannot be shot down because a property was renamed in a
        /// game update would be a silent hole. Eight metres around the
        /// reported point is close enough: the point comes from the shooter's
        /// interpolated picture and may lag a few metres at 32 m/s, and two
        /// drones that near each other are one bang either way.
        ///
        /// Shot down before ArmDelay has passed, it falls instead of
        /// detonating. That is the same rule that keeps the first frame from
        /// going off in the pilot's face, and it is the only case in which a
        /// destroyed drone does not explode.
        /// </summary>
        internal static void Beschossen(int schuetze, Vector3 punkt, int ziel, float schaden)
        {
            if (!_flying) return;
            if (!RevivalPlugin.CfgDroneShootable.Value) return;

            int ich = MeineNummer();
            if (ich >= 0) { if (ziel != ich) return; }
            else if (Vector3.Distance(punkt, _pos) > 8f) return;

            _hp -= Mathf.Max(0.1f, schaden);
            RevivalPlugin.L.LogInfo("Drohne getroffen von Spieler " + schuetze
                + " - noch " + _hp.ToString("0.#") + " von "
                + RevivalPlugin.CfgDroneHitpoints.Value + ".");
            if (_hp > 0f) return;

            if (Time.time < _armed) { Land(Grund.Absturz); return; }
            RevivalPlugin.L.LogInfo("Drohne abgeschossen von Spieler " + schuetze + ".");
            Impact(_pos);
        }

        /// <summary>A real NPC firearm shot that the local pilot resolved as
        /// a hit. NPCs are not Photon players, so this stays on the pilot's
        /// client just like the hit points themselves.</summary>
        internal static void NpcTreffer(Vector3 schuetzenPos)
        {
            if (!_flying || !RevivalPlugin.CfgDroneShootable.Value) return;
            _hp -= 1f;
            float dist = Vector3.Distance(schuetzenPos, _pos);
            RevivalPlugin.L.LogInfo("Drohne von NPC auf " + dist.ToString("0")
                + " m getroffen - noch " + _hp.ToString("0.#") + " von "
                + RevivalPlugin.CfgDroneHitpoints.Value + ".");
            if (_hp > 0f) return;

            if (Time.time < _armed) { Land(Grund.Absturz); return; }
            RevivalPlugin.L.LogInfo("Drohne von NPC abgeschossen.");
            Impact(_pos);
        }

        /// <summary>
        /// The own actor number in the Photon room. Read fresh every time and
        /// never cached: it is handed out per room, so a value kept over a
        /// rejoin would point at somebody else. Only the two reflection
        /// handles are kept.
        ///
        /// -1 means "not known", and the caller falls back on the distance.
        /// </summary>
        static int MeineNummer()
        {
            try
            {
                if (!_nummerGesucht)
                {
                    _nummerGesucht = true;
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon != null)
                    {
                        _spielerGet = AccessTools.PropertyGetter(photon, "player");
                        if (_spielerGet == null)
                            _spielerGet = AccessTools.PropertyGetter(photon, "LocalPlayer");
                    }
                    if (_spielerGet == null)
                        RevivalPlugin.L.LogWarning("Drohne: eigene Spielernummer nicht "
                            + "gefunden - ein Abschuss wird ueber die Entfernung "
                            + "zugeordnet.");
                }
                if (_spielerGet == null) return -1;
                object spieler = _spielerGet.Invoke(null, null);
                if (spieler == null) return -1;
                if (_nummerGet == null)
                {
                    _nummerGet = AccessTools.PropertyGetter(spieler.GetType(), "ID");
                    if (_nummerGet == null)
                        _nummerGet = AccessTools.PropertyGetter(spieler.GetType(),
                                                                "ActorNumber");
                }
                if (_nummerGet == null) return -1;
                return (int)_nummerGet.Invoke(spieler, null);
            }
            catch { return -1; }
        }

        /// <summary>
        /// The shooter's only feedback, and it is worth the twelve lines: a
        /// drone is small, fast and far away, and without a mark on the
        /// screen nobody can tell a hit from a miss. Red and wider means the
        /// drone actually went up - that one is set when the detonation comes
        /// back over the wire, not when the shot leaves, so it never promises
        /// a kill that did not happen.
        /// </summary>
        internal static void Marke(bool tot)
        {
            _markeTot = tot;
            _markeBis = Time.time + (tot ? 0.55f : 0.25f);
        }

        static string GrundText(int grund)
        {
            if (grund == Grund.Detonation) return "Detonation";
            if (grund == Grund.Absturz) return "Absturz";
            if (grund == Grund.Abriss) return "Signalabriss";
            return "abgebrochen";
        }

        /// <summary>
        /// Einschlag: erst allen sagen, WO es knallt, dann zuenden, dann den
        /// Blick zurueckgeben. Die Explosion selbst laeuft ueber
        /// PhotonNetwork.Instantiate und ist damit ohnehin fuer alle da - das
        /// Ereignis daneben braucht es nur, damit die anderen ihr Modell und
        /// ihren Ton loswerden.
        /// </summary>
        static void Impact(Vector3 point)
        {
            Vector3 p = point - Forward() * 0.25f;
            Net.Send(Net.Ende, p, Forward(), (float)Grund.Detonation, true);
            try
            {
                RocketHook.Detonate(p, RevivalPlugin.CfgDroneDamage.Value,
                                    RevivalPlugin.CfgDroneRadius.Value, 3f);
                RevivalPlugin.L.LogInfo("Drohne detoniert bei " + p + ", "
                    + RevivalPlugin.CfgDroneDamage.Value + " Schaden auf "
                    + RevivalPlugin.CfgDroneRadius.Value + " m.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Drohnendetonation fehlgeschlagen: " + ex);
            }
            Land(Grund.Detonation);
        }

        // -------------------------------------------------------- Steuerung

        static void Steer()
        {
            float sens = RevivalPlugin.CfgDroneSensitivity.Value;
            float mx = Input.GetAxis("Mouse X") * sens;
            if (RevivalPlugin.CfgDroneInvertX.Value) mx = -mx;
            _yaw += mx;
            float my = Input.GetAxis("Mouse Y") * sens;
            if (RevivalPlugin.CfgDroneInvertY.Value) my = -my;
            _pitch = Mathf.Clamp(_pitch + my, -85f, 85f);
            if (_yaw > 180f) _yaw -= 360f;
            if (_yaw < -180f) _yaw += 360f;
        }

        /// <summary>
        /// Der ganze Flug, drei Zeilen Mathematik plus ein Strahl.
        ///
        /// Der Strahl geht von der ALTEN zur NEUEN Lage, nicht einfach nach
        /// vorn: bei 30 m/s und 60 Bildern liegt zwischen zwei Frames ein
        /// halber Meter, und eine Wand, die duenner ist als dieser Schritt,
        /// wuerde sonst durchflogen.
        /// </summary>
        static void Move()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 fwd = Forward();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (right.sqrMagnitude < 0.000001f) right = Vector3.right;
            else right.Normalize();

            // Leerer Akku heisst nicht Detonation, sondern Motoren aus. Sie
            // faellt dann wie ein Stein mit Fluegeln: volle Schwerkraft, kein
            // Schub, und beim Aufschlag knallt sie NICHT.
            bool motorlos = Motorlos();

            float thrust = RevivalPlugin.CfgDroneThrust.Value;
            float side = RevivalPlugin.CfgDroneSideThrust.Value;
            float lift = RevivalPlugin.CfgDroneLift.Value;

            Vector3 accel = Vector3.up * (motorlos ? -9.81f
                                                   : RevivalPlugin.CfgDroneGravity.Value);
            if (!motorlos)
            {
                if (Input.GetKey(KeyCode.W)) accel += fwd * thrust;
                if (Input.GetKey(KeyCode.S)) accel -= fwd * thrust;
                if (Input.GetKey(KeyCode.D)) accel += right * side;
                if (Input.GetKey(KeyCode.A)) accel -= right * side;
                if (Input.GetKey(KeyCode.Space)) accel += Vector3.up * lift;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C))
                    accel -= Vector3.up * lift;
            }

            _vel += accel * dt;

            // Luftwiderstand. Bewusst geschwindigkeitsproportional und ueber
            // dt gerechnet - ein fester Faktor je Frame haengt sonst an der
            // Bildrate, und die Drohne floege auf einem schnellen Rechner
            // anders als auf einem langsamen.
            _vel -= _vel * Mathf.Min(1f, RevivalPlugin.CfgDroneDrag.Value * dt);

            float max = RevivalPlugin.CfgDroneMaxSpeed.Value;
            if (_vel.magnitude > max) _vel = _vel.normalized * max;

            Vector3 step = _vel * dt;
            float len = step.magnitude;
            if (len > 0.0001f && Time.time >= _armed)
            {
                Vector3 hit;
                GameObject go = Turret.RaycastObject(_pos, step / len, len + 0.20f, out hit);
                if (go != null && !IsPilot(go))
                {
                    if (motorlos) Land(Grund.Absturz);
                    else Impact(hit);
                    return;
                }
            }
            _pos += step;

            // Ab hier ist die Funkstrecke die Grenze, nicht der Akku. Die
            // Drohne verschwindet dabei - kein Wrack: es gibt kein Objekt in
            // der Welt, das liegenbleiben koennte, die anderen sehen nur ein
            // lokal gebautes Modell, und ein Wrack muesste als eigenes Ding
            // erfunden und wieder aufgeraeumt werden.
            if (Entfernung() >= RevivalPlugin.CfgDroneRange.Value)
            {
                Land(Grund.Abriss);
                return;
            }

            if (Time.time >= _nextNet)
            {
                float hz = Mathf.Max(2f, RevivalPlugin.CfgDroneNetHz.Value);
                _nextNet = Time.time + 1f / hz;
                Net.Send(Net.Lauf, _pos, fwd, 0f, false);
            }
        }

        static Vector3 Forward()
        {
            return Quaternion.Euler(-_pitch, _yaw, 0f) * Vector3.forward;
        }

        // -------------------------------------- Akku, Reichweite, Bildstoerung

        /// <summary>Entfernung zum Piloten - die Funkstrecke.</summary>
        public static float Entfernung()
        {
            return Vector3.Distance(_pos, _home);
        }

        /// <summary>Akkustand von 1 (voll) bis 0 (leer).</summary>
        public static float Akku()
        {
            float ganz = Mathf.Max(1f, RevivalPlugin.CfgDroneFlightTime.Value);
            return Mathf.Clamp01(1f - FlightTime / ganz);
        }

        static bool Motorlos()
        {
            return FlightTime >= RevivalPlugin.CfgDroneFlightTime.Value
                || Jammer.Motorstop;
        }

        /// <summary>
        /// Signalguete von 1 (sauber) bis 0 (Abriss). Bis `NoiseFrom` ist das
        /// Bild ruhig, danach faellt es linear ab. Das ist die Vorwarnung: wer
        /// das Rauschen sieht, weiss, dass er umkehren muss.
        /// </summary>
        public static float Signal()
        {
            float weit = RevivalPlugin.CfgDroneRange.Value;
            float ruhig = Mathf.Min(RevivalPlugin.CfgDroneNoiseFrom.Value, weit - 1f);
            float d = Entfernung();
            float funk = d <= ruhig
                ? 1f
                : Mathf.Clamp01(1f - (d - ruhig) / Mathf.Max(1f, weit - ruhig));
            // A jammer eats the picture the same way distance does. On
            // purpose: the pilot already knows this noise and does not need to
            // learn a second warning.
            return Mathf.Min(funk, 1f - Jammer.Grad);
        }

        /// <summary>
        /// Hoehe ueber Grund, per Strahl nach unten. Nur fuenfmal je Sekunde -
        /// jeden Frame waere es ein Raycast mehr fuer eine Zahl, die sich in
        /// einer Fuenftelsekunde kaum aendert.
        /// </summary>
        static float Hoehe()
        {
            if (Time.time >= _nextHeight)
            {
                _nextHeight = Time.time + 0.2f;
                Vector3 boden;
                GameObject go = Turret.RaycastObject(_pos, Vector3.down, 400f, out boden);
                _height = go == null ? -1f : _pos.y - boden.y;
            }
            return _height;
        }

        /// <summary>
        /// Gehoert das Getroffene zum eigenen Koerper? Ohne das detoniert die
        /// Drohne im ersten Frame am Piloten. Muster ist Turret.IsOwnVehicle.
        /// Die Scharfzeit (`ArmDelay`) faengt zusaetzlich alles ab, was in den
        /// ersten Zehntelsekunden im Weg steht - Ruecksack, Waffe, Fahrzeug.
        /// </summary>
        static bool IsPilot(GameObject go)
        {
            if (go == null) return false;
            if (_pilotRoot != null)
            {
                Transform t = go.transform;
                while (t != null)
                {
                    if (t == _pilotRoot) return true;
                    t = t.parent;
                }
            }
            return Vector3.Distance(go.transform.position, _home)
                   < RevivalPlugin.CfgDroneSafeRadius.Value;
        }

        /// <summary>
        /// Die Wurzel des eigenen Spielerobjekts. Fremde bleiben ueber
        /// photonView.isMine draussen - dasselbe Muster wie in
        /// Turret.PlayerInventories.
        /// </summary>
        static Transform LocalPlayerRoot()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerMovementController");
                if (t == null) return null;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i] as MonoBehaviour;
                    if (mb == null) continue;
                    if (!IsMine(mb)) continue;
                    // transform.root is the common "ServerObjects" scene
                    // container, not this player. The controller itself sits
                    // on the player object and is the ancestor needed by the
                    // launch-collision guard.
                    return mb.transform;
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Drohne: Pilot nicht gefunden: " + ex.Message); }
            return null;
        }

        static bool IsMine(MonoBehaviour mb)
        {
            MethodInfo get = AccessTools.Method(mb.GetType(), "get_photonView", null, null);
            object view = null;
            try { if (get != null) view = get.Invoke(mb, null); }
            catch { view = null; }
            if (view == null) return true;          // ohne PhotonView: Einzelspieler
            MethodInfo isMine = AccessTools.PropertyGetter(view.GetType(), "isMine");
            try { return isMine == null || (bool)isMine.Invoke(view, null); }
            catch { return true; }
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try
            {
                _key = (KeyCode)Enum.Parse(typeof(KeyCode),
                                           RevivalPlugin.CfgDroneKey.Value, true);
            }
            catch
            {
                _key = KeyCode.V;
                RevivalPlugin.L.LogWarning("Drohne: Taste \""
                    + RevivalPlugin.CfgDroneKey.Value + "\" unbekannt, benutze V.");
            }
            return _key;
        }

        // -------------------------------------------------------------- Ton

        /// <summary>
        /// Der Pilot hoert seine eigene Drohne nicht raeumlich - er sitzt in
        /// ihr. Ein leiser Motorton ohne Raumanteil, damit Schub und Stille
        /// unterscheidbar sind.
        /// </summary>
        static void StartOwnHum()
        {
            if (!RevivalPlugin.CfgDroneSound.Value) return;
            try
            {
                if (_ownHum == null)
                {
                    GameObject go = new GameObject("NDR_DroneOwnHum");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _ownHum = go.AddComponent<AudioSource>();
                    _ownHum.clip = Sound.Hum();
                    _ownHum.loop = true;
                    _ownHum.spatialBlend = 0f;
                    _ownHum.playOnAwake = false;
                }
                _ownHum.volume = RevivalPlugin.CfgDroneSoundVolume.Value * 0.35f;
                _ownHum.Play();
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Drohnenton: " + ex.Message); }
        }

        static void StopOwnHum()
        {
            if (_ownHum != null) _ownHum.Stop();
        }

        // ------------------------------------------------------------- Netz

        /// <summary>
        /// Sichtbarkeit fuer die anderen - Weg B aus `tasks/fpv-drohne.md`:
        /// uebertragen werden nur Zahlen, das Modell baut jeder Client selbst.
        /// Kein registriertes Prefab, keine Abhaengigkeit vom Masterclient.
        ///
        /// Warum ein Photon-EREIGNIS und kein eigener RPC: ein RPC muesste als
        /// Methode an einer Komponente eines vom Spiel registrierten
        /// PhotonView haengen und traegt in PUN das Attribut `[PunRPC]` - ein
        /// Attribut steht in den Metadaten und liesse sich nur mit einer
        /// Referenz auf die Photon-DLL setzen. `RaiseEvent` braucht davon
        /// nichts: Code, Zahlenfeld, fertig.
        ///
        /// Warum drei Ereigniscodes statt einem mit Typfeld: dann ist der
        /// Inhalt immer ein schlichtes float[], und ueber die Serialisierung
        /// von object[] oder Hashtable muss nichts vermutet werden.
        /// </summary>
        public static class Net
        {
            public const int Start = 0;
            public const int Lauf = 1;
            public const int Ende = 2;
            // The jammer rides on the drone's own channel: one event more, no
            // second hook, and it is gone the moment the drone code is.
            public const int Jam = 3;
            // A shot that went through a foreign drone. Travels the other way
            // round to everything else here: not from the pilot outwards, but
            // from a shooter to the one client that owns the drone.
            public const int Treffer = 4;

            static bool _hooked;
            static bool _failed;
            static MethodInfo _raise;
            static Type _optType;
            static FieldInfo _onEventCall;
            static readonly Dictionary<int, Fremd> _fremde = new Dictionary<int, Fremd>();
            static readonly List<int> _weg = new List<int>();

            /// <summary>Eine fremde Drohne, wie dieser Client sie sieht.</summary>
            class Fremd
            {
                public GameObject Go;
                public AudioSource Src;
                public Vector3 Von;
                public Vector3 Nach;
                public Vector3 Blick;
                public float T;              // 0..1 zwischen Von und Nach
                public float Dauer;          // Sekunden zwischen zwei Meldungen
                public float Zuletzt;        // Time.time der letzten Meldung
                public float Getroffen;      // Time.time des letzten eigenen Treffers
            }

            public static void EnsureHooked()
            {
                if (_hooked || _failed) return;
                try
                {
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null)
                    {
                        _failed = true;
                        RevivalPlugin.L.LogWarning("Drohnennetz: PhotonNetwork nicht "
                            + "gefunden - die Drohne fliegt, aber niemand sonst sieht sie.");
                        return;
                    }
                    _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                    _onEventCall = AccessTools.Field(photon, "OnEventCall");
                    _optType = RevivalPlugin.TypeByName("RaiseEventOptions");
                    if (_raise == null || _onEventCall == null)
                    {
                        _failed = true;
                        RevivalPlugin.L.LogWarning("Drohnennetz: RaiseEvent oder "
                            + "OnEventCall fehlt - die Drohne bleibt fuer andere unsichtbar.");
                        return;
                    }

                    MethodInfo mine = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static);
                    Delegate handler = Delegate.CreateDelegate(_onEventCall.FieldType, mine);
                    Delegate current = _onEventCall.GetValue(null) as Delegate;
                    _onEventCall.SetValue(null, Delegate.Combine(current, handler));

                    _hooked = true;
                    RevivalPlugin.L.LogInfo("Drohnennetz eingehaengt: Ereigniscodes "
                        + Code(Start) + "-" + Code(Treffer) + ", Empfang ueber "
                        + "PhotonNetwork.OnEventCall.");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("Drohnennetz nicht eingehaengt: " + ex);
                }
            }

            static int Code(int art) { return RevivalPlugin.CfgDroneEventCode.Value + art; }

            public static void Send(int art, Vector3 pos, Vector3 blick, float zusatz,
                                    bool zuverlaessig)
            {
                if (!_hooked) return;
                try
                {
                    float[] daten = new float[] {
                        pos.x, pos.y, pos.z, blick.x, blick.y, blick.z, zusatz };
                    object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                    _raise.Invoke(null, new object[] {
                        (byte)Code(art), daten, zuverlaessig, opts });
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Drohnennetz senden: " + ex.Message);
                }
            }

            /// <summary>
            /// Empfaenger. Signatur MUSS zu PhotonNetwork.EventCallback passen
            /// (byte, object, int) - Delegate.CreateDelegate prueft das, und ein
            /// Fehler hier faellt beim Einhaengen auf, nicht erst im Flug.
            /// </summary>
            public static void OnPhotonEvent(byte code, object inhalt, int absender)
            {
                try
                {
                    int art = code - RevivalPlugin.CfgDroneEventCode.Value;
                    if (art < Start || art > Treffer) return;
                    float[] d = inhalt as float[];
                    if (d == null || d.Length < 7) return;

                    Vector3 pos = new Vector3(d[0], d[1], d[2]);
                    Vector3 blick = new Vector3(d[3], d[4], d[5]);

                    if (art == Jam) { Jammer.Fremdmeldung(absender, pos, d[6]); return; }
                    if (art == Ende) { Entferne(absender, (int)d[6]); return; }
                    // Treffer: d[3] carries the pilot's actor number where the
                    // other events carry a view direction - see SendTreffer.
                    if (art == Treffer)
                    {
                        Beschossen(absender, pos, (int)d[3], d[6]);
                        return;
                    }

                    Fremd f;
                    if (!_fremde.TryGetValue(absender, out f))
                    {
                        f = new Fremd();
                        f.Go = Modell.Bauen();
                        f.Src = Sound.Anhaengen(f.Go);
                        f.Von = pos;
                        _fremde[absender] = f;
                        RevivalPlugin.L.LogInfo("Fremde Drohne von Spieler "
                            + absender + " bei " + pos + ".");
                    }
                    else
                    {
                        f.Von = f.Go == null ? pos : f.Go.transform.position;
                    }
                    f.Nach = pos;
                    f.Blick = blick.sqrMagnitude < 0.000001f ? Vector3.forward : blick;
                    f.Dauer = Mathf.Max(0.02f, Time.time - f.Zuletzt);
                    f.Zuletzt = Time.time;
                    f.T = 0f;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Drohnennetz empfangen: " + ex.Message);
                }
            }

            /// <summary>
            /// Zwischen zwei Meldungen wird lokal weitergeschoben. Ohne das
            /// ruckelt die fremde Drohne im Takt der Meldungen - bei 15 Hz
            /// waere das gut sichtbar.
            /// </summary>
            public static void TickRemotes()
            {
                if (_fremde.Count == 0) return;
                _weg.Clear();
                foreach (KeyValuePair<int, Fremd> kv in _fremde)
                {
                    Fremd f = kv.Value;
                    if (f.Go == null || Time.time - f.Zuletzt > 4f) { _weg.Add(kv.Key); continue; }
                    f.T = Mathf.Min(1f, f.T + Time.deltaTime / Mathf.Max(0.02f, f.Dauer));
                    f.Go.transform.position = Vector3.Lerp(f.Von, f.Nach, f.T);
                    f.Go.transform.rotation = Quaternion.LookRotation(f.Blick, Vector3.up);
                }
                for (int i = 0; i < _weg.Count; i++) Entferne(_weg[i], Grund.Abriss);
            }

            /// <summary>
            /// Is a foreign drone within `r` metres of `p`? The jammer asks
            /// before it says anything at all: no drone nearby, no event, no
            /// traffic. The position is the interpolated one - the same the
            /// player sees.
            /// </summary>
            public static bool RemoteNear(Vector3 p, float r)
            {
                float rr = r * r;
                foreach (KeyValuePair<int, Fremd> kv in _fremde)
                {
                    Fremd f = kv.Value;
                    if (f.Go == null) continue;
                    if ((f.Go.transform.position - p).sqrMagnitude <= rr) return true;
                }
                return false;
            }

            /// <summary>
            /// One shot of the local player, measured against every foreign
            /// drone. Called from the postfix on the game's own FireOneShot,
            /// so it runs exactly as often as a bullet leaves the barrel.
            ///
            /// Geometry, not physics: the closest approach of the aiming line
            /// to the drone's middle, against the radius of the model.
            /// Deliberate - `UnityEngine.PhysicsModule` is not referenced (see
            /// Turret.RaycastObject), so there is no SphereCast to be had, and
            /// a collider on the drone would put an obstacle into the game's
            /// own ray casts for a test we can do in six lines.
            ///
            /// The one ray that IS cast is the line of sight, and it starts a
            /// metre out: the camera sits inside the player's own head, and a
            /// ray from there hits his backpack before anything else. A
            /// vehicle the shooter sits in blocks a shot it should not - the
            /// price of not knowing where the game puts its muzzle.
            /// </summary>
            public static bool Beschuss(Vector3 origin, Vector3 dir, float reichweite,
                                        float schaden)
            {
                if (_fremde.Count == 0) return false;
                if (dir.sqrMagnitude < 0.000001f) return false;
                dir.Normalize();
                float r = Trefferradius();

                int wer = 0;
                Fremd getroffen = null;
                float nah = float.MaxValue;
                Vector3 punkt = Vector3.zero;
                foreach (KeyValuePair<int, Fremd> kv in _fremde)
                {
                    Fremd f = kv.Value;
                    if (f.Go == null) continue;
                    Vector3 hin = f.Go.transform.position - origin;
                    float t = Vector3.Dot(hin, dir);
                    if (t < 1f || t > reichweite || t >= nah) continue;
                    if ((hin - dir * t).sqrMagnitude > r * r) continue;
                    nah = t;
                    wer = kv.Key;
                    getroffen = f;
                    punkt = f.Go.transform.position;
                }
                if (getroffen == null) return false;

                Vector3 egal;
                if (nah > 2.2f
                    && Turret.RaycastObject(origin + dir, dir, nah - 1.2f, out egal) != null)
                    return false;

                getroffen.Getroffen = Time.time;
                SendTreffer(wer, punkt, schaden);
                Drone.Marke(false);
                RevivalPlugin.L.LogInfo("Drohne von Spieler " + wer + " getroffen auf "
                    + Mathf.RoundToInt(nah) + " m.");
                return true;
            }

            /// <summary>
            /// How wide a drone is as a target: the model, nothing else. The
            /// mesh is 36 cm across before ModelScale, so half of it is 0.18.
            /// One number for size and hitbox both - a second knob would be a
            /// knob that can fall out of step with what the shooter sees.
            /// </summary>
            static float Trefferradius()
            {
                return Mathf.Max(0.30f, 0.18f * RevivalPlugin.CfgDroneModelScale.Value);
            }

            /// <summary>
            /// The only event whose seven floats mean something else: d[0..2]
            /// is where the drone was hit, d[3] the actor number of its pilot,
            /// d[4] and d[5] stay empty, d[6] is the damage. It rides on
            /// `Send` on purpose - the array keeps its shape, so the length
            /// check and the serialization on the other end stay exactly as
            /// they were.
            /// </summary>
            static void SendTreffer(int ziel, Vector3 punkt, float schaden)
            {
                Send(Treffer, punkt, new Vector3(ziel, 0f, 0f), schaden, true);
            }

            static void Entferne(int absender, int grund)
            {
                Fremd f;
                if (!_fremde.TryGetValue(absender, out f)) return;
                _fremde.Remove(absender);
                if (f.Src != null) f.Src.Stop();
                if (f.Go != null) UnityEngine.Object.Destroy(f.Go);
                // Blown up right after we hit it: that was our kill, and the
                // shooter gets to see it. Tied to the detonation coming back
                // over the wire and not to the shot going out, so the mark
                // never promises something that did not happen.
                if (grund == Grund.Detonation && Time.time - f.Getroffen < 1.5f)
                    Drone.Marke(true);
                RevivalPlugin.L.LogInfo("Fremde Drohne von Spieler " + absender
                    + " ist weg (" + GrundText(grund) + ").");
            }
        }

        // ----------------------------------------------------- Stoersender

        /// <summary>
        /// The counter to the drone: a carried jammer that ends every flight
        /// inside its radius.
        ///
        /// Two halves, and they never run on the same machine at the same
        /// time:
        ///
        ///   Sender   the local player carries a jammer. While a foreign drone
        ///            is near, position and radius go out on the drone's own
        ///            event channel (Net.Jam), five times a second.
        ///   Pilot    the local drone measures itself against every field it
        ///            knows about - the foreign ones from those events, the
        ///            own one only if AffectsOwn says so.
        ///
        /// The pilot's client decides, because only there is anything to
        /// decide: camera, warhead and flight all live on that machine. The
        /// price is that a modified client could ignore a jammer. Among
        /// friends that is no problem; as protection against cheating it is
        /// worth nothing, and it is written down here so nobody takes it for
        /// that.
        /// </summary>
        public static class Jammer
        {
            /// <summary>A jammer somebody else carries, as last reported.</summary>
            class Quelle
            {
                public Vector3 Pos;
                public float Radius;
                public float Zuletzt;    // Time.time of the last message
            }

            static readonly Dictionary<int, Quelle> _quellen = new Dictionary<int, Quelle>();
            static readonly List<int> _weg = new List<int>();
            static bool _traegt;             // does the local player carry one
            static bool _gemeldet;           // are we sending right now
            static float _naechsteMeldung;
            static float _zuendung;          // Time.time of the bang, 0 = nothing
            static bool _motorstop;
            static bool _draussen;           // own drone has left the own field once
            static float _grad;              // 0 = clean picture, 1 = inside

            /// <summary>Are the motors dead because of a jammer?</summary>
            public static bool Motorstop { get { return _motorstop; } }

            /// <summary>How badly the picture is jammed, 0 to 1.</summary>
            public static float Grad { get { return _grad; } }

            /// <summary>Is there anything to say on the overlay?</summary>
            public static bool Warnt { get { return _grad > 0.02f; } }

            /// <summary>A new flight starts with a clean slate.</summary>
            public static void Reset()
            {
                _zuendung = 0f;
                _motorstop = false;
                _draussen = false;
                _grad = 0f;
            }

            public static void Tick()
            {
                if (!RevivalPlugin.CfgJammer.Value) { _grad = 0f; return; }
                try
                {
                    Aufraeumen();
                    Tragen();
                    Melden();
                    if (_flying) Fliegen();
                    else _grad = 0f;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Stoersender: " + ex.Message);
                }
            }

            /// <summary>
            /// A jammer reported by somebody else. Nothing is trusted further
            /// than it must be: the radius is clamped, and a sender that goes
            /// quiet is forgotten after a second and a half - which is also
            /// what happens when he drops the thing.
            /// </summary>
            public static void Fremdmeldung(int absender, Vector3 pos, float radius)
            {
                if (!RevivalPlugin.CfgJammer.Value) return;
                Quelle q;
                if (!_quellen.TryGetValue(absender, out q))
                {
                    q = new Quelle();
                    _quellen[absender] = q;
                    RevivalPlugin.L.LogInfo("Stoersender von Spieler " + absender
                        + " bei " + pos + ", Radius " + radius + " m.");
                }
                q.Pos = pos;
                q.Radius = Mathf.Clamp(radius, 1f, 500f);
                q.Zuletzt = Time.time;
            }

            static void Aufraeumen()
            {
                if (_quellen.Count == 0) return;
                _weg.Clear();
                foreach (KeyValuePair<int, Quelle> kv in _quellen)
                    if (Time.time - kv.Value.Zuletzt > 1.5f) _weg.Add(kv.Key);
                for (int i = 0; i < _weg.Count; i++)
                {
                    _quellen.Remove(_weg[i]);
                    RevivalPlugin.L.LogInfo("Stoersender von Spieler " + _weg[i]
                        + " meldet sich nicht mehr.");
                }
            }

            static void Tragen()
            {
                bool jetzt = Turret.HasItem(RevivalPlugin.CfgJammerItemId.Value);
                if (jetzt != _traegt)
                    RevivalPlugin.L.LogInfo("Stoersender (Item "
                        + RevivalPlugin.CfgJammerItemId.Value + ") "
                        + (jetzt ? "getragen." : "abgelegt."));
                _traegt = jetzt;
            }

            /// <summary>
            /// Where the jammer stands: at the player, never at the camera.
            /// While a drone is up the camera IS the drone, and a jammer that
            /// flew along with it would jam whatever it approaches - the exact
            /// opposite of a counter.
            /// </summary>
            static bool EigenePosition(out Vector3 p)
            {
                p = Vector3.zero;
                Transform root = LocalPlayerRoot();
                if (root != null) { p = root.position; return true; }
                if (_flying) { p = _home; return true; }
                Camera cam = CameraOwner.ViewCamera();
                if (cam == null) return false;
                p = cam.transform.position;
                return true;
            }

            /// <summary>
            /// Tell the others - but only while there is something to jam.
            /// Without a foreign drone in sight this costs one walk through a
            /// dictionary per frame and not a single packet.
            /// </summary>
            static void Melden()
            {
                if (!_traegt)
                {
                    _gemeldet = false;
                    return;
                }
                float r = Mathf.Max(1f, RevivalPlugin.CfgJammerRadius.Value);
                Vector3 p;
                if (!EigenePosition(out p)) return;
                if (!Net.RemoteNear(p, Reichweite(r) + 40f))
                {
                    _gemeldet = false;
                    return;
                }
                if (!_gemeldet)
                {
                    _gemeldet = true;
                    RevivalPlugin.L.LogInfo("Stoersender: fremde Drohne in "
                        + "Reichweite, Stoerung geht raus (Radius " + r + " m).");
                }
                if (Time.time < _naechsteMeldung) return;
                _naechsteMeldung = Time.time + 0.2f;
                Net.Send(Net.Jam, p, Vector3.zero, r, false);
            }

            /// <summary>
            /// Warn distance of a field with radius r. The ratio comes from
            /// the own configuration: a foreign jammer sends one number, and
            /// one number is enough.
            /// </summary>
            static float Reichweite(float r)
            {
                float eigen = Mathf.Max(1f, RevivalPlugin.CfgJammerRadius.Value);
                float warn = RevivalPlugin.CfgJammerWarnRadius.Value;
                return warn <= eigen ? r : r * (warn / eigen);
            }

            /// <summary>
            /// The pilot's half: measure the own drone against every known
            /// field, then act. Once the fuse is lit there is no way out - at
            /// 32 m/s and four tenths of a second nobody leaves a fifty metre
            /// bubble anyway, and a drone flickering in and out of its doom
            /// would only look broken.
            /// </summary>
            static void Fliegen()
            {
                float grad = 0f;
                bool drin = false;

                foreach (KeyValuePair<int, Quelle> kv in _quellen)
                    Messen(kv.Value.Pos, kv.Value.Radius, ref grad, ref drin);

                // The own jammer, if it counts at all. It arms only after the
                // drone has been outside once - otherwise every launch would
                // end two metres in front of the pilot.
                if (_traegt && RevivalPlugin.CfgJammerAffectsOwn.Value)
                {
                    Vector3 p;
                    if (EigenePosition(out p))
                    {
                        float r = Mathf.Max(1f, RevivalPlugin.CfgJammerRadius.Value);
                        if (!_draussen)
                        {
                            if (Vector3.Distance(_pos, p) > r)
                            {
                                _draussen = true;
                                RevivalPlugin.L.LogInfo("Eigener Stoersender ist "
                                    + "scharf - die Drohne ist draussen.");
                            }
                        }
                        else Messen(p, r, ref grad, ref drin);
                    }
                }

                _grad = _zuendung > 0f ? 1f : grad;

                if (drin && _zuendung <= 0f && Time.time >= _armed)
                {
                    float t = Mathf.Max(0f, RevivalPlugin.CfgJammerDelay.Value);
                    _zuendung = Time.time + t;
                    RevivalPlugin.L.LogInfo("Drohne im Stoerfeld bei " + _pos
                        + " - " + (RevivalPlugin.CfgJammerDetonate.Value
                                   ? "sie zuendet" : "die Motoren gehen aus")
                        + " in " + t + " s.");
                }

                if (_zuendung > 0f && Time.time >= _zuendung)
                {
                    _zuendung = 0f;
                    if (RevivalPlugin.CfgJammerDetonate.Value) Impact(_pos);
                    else _motorstop = true;
                }
            }

            /// <summary>
            /// One field against the drone. `drin` says the flight is over,
            /// `grad` is what the picture shows on the way there.
            /// </summary>
            static void Messen(Vector3 mitte, float r, ref float grad, ref bool drin)
            {
                float d = Vector3.Distance(_pos, mitte);
                if (d <= r) { drin = true; grad = 1f; return; }
                float warn = Reichweite(r);
                if (d >= warn) return;
                float g = Mathf.Clamp01(1f - (d - r) / Mathf.Max(1f, warn - r));
                if (g > grad) grad = g;
            }
        }

        // ----------------------------------------------------------- Modell

        /// <summary>
        /// Das Modell, das die anderen sehen. Wird lokal gebaut, nicht ueber
        /// das Netz erzeugt - deshalb braucht es kein registriertes Prefab.
        /// </summary>
        public static class Modell
        {
            static Material _mat;
            static Mesh _notnagel;

            /// <summary>
            /// Baut das Modell. Seit 0.4.8 aus drone.ndmesh und
            /// drone_diffuse.png; fehlt eine der beiden Dateien, faellt es auf
            /// den eingebauten Notnagel zurueck, statt still gar nichts
            /// anzuzeigen - eine unsichtbare Drohne waere schlimmer als eine
            /// haessliche.
            ///
            /// Zwei Objekte statt einem, seit 0.5.5: `drone.ndmesh` ist das
            /// INVENTARMODELL und wurde auf Lage und Groesse eines Magazins
            /// eingepasst (`drone_mesh.py`, `fit_box`) - seine Mitte liegt
            /// deshalb rund 20 cm hinter seinem Nullpunkt. In der Hand ist das
            /// genau richtig; am Himmel bedeutet es, dass die Drohne hinter
            /// dem Punkt gezeichnet wird, an dem sie wirklich fliegt. Bei
            /// ModelScale 4 sind das 80 cm - und dieselben 80 cm daneben fuer
            /// jeden, der auf sie schiesst. Ein Kind, um die eigenen Bounds
            /// verschoben, legt die Mitte der Drohne auf den Transform.
            /// </summary>
            public static GameObject Bauen()
            {
                GameObject go = new GameObject("NDR_Drone");
                float s = RevivalPlugin.CfgDroneModelScale.Value;
                go.transform.localScale = new Vector3(s, s, s);

                GameObject koerper = new GameObject("Body");
                koerper.transform.parent = go.transform;
                koerper.transform.localRotation = Quaternion.identity;
                koerper.transform.localScale = Vector3.one;

                MeshFilter mf = koerper.AddComponent<MeshFilter>();
                MeshRenderer mr = koerper.AddComponent<MeshRenderer>();
                Mesh mesh = Assets.Load("drone.ndmesh");
                if (mesh == null) mesh = Notnagel();
                mf.sharedMesh = mesh;
                mr.sharedMaterial = Werkstoff();
                koerper.transform.localPosition = -mesh.bounds.center;
                return go;
            }

            /// <summary>
            /// Rueckfall, wenn drone.ndmesh fehlt - fuenf Quader: Rumpf und
            /// vier Rotorscheiben. Sieht nach nichts aus, ist aber sichtbar,
            /// und Sichtbarkeit ist bei dieser Waffe kein Schmuck: wer von ihr
            /// getroffen wird, muss die Gelegenheit gehabt haben, sie zu sehen.
            /// </summary>
            static Mesh Notnagel()
            {
                if (_notnagel != null) return _notnagel;
                List<Vector3> v = new List<Vector3>();
                List<int> t = new List<int>();
                Quader(v, t, new Vector3(0f, 0f, 0.02f), new Vector3(0.16f, 0.05f, 0.22f));
                float a = 0.15f;
                Quader(v, t, new Vector3(a, 0.03f, a), new Vector3(0.14f, 0.012f, 0.14f));
                Quader(v, t, new Vector3(-a, 0.03f, a), new Vector3(0.14f, 0.012f, 0.14f));
                Quader(v, t, new Vector3(a, 0.03f, -a), new Vector3(0.14f, 0.012f, 0.14f));
                Quader(v, t, new Vector3(-a, 0.03f, -a), new Vector3(0.14f, 0.012f, 0.14f));
                Mesh m = new Mesh();
                m.name = "NDR_DroneFallback";
                m.vertices = v.ToArray();
                m.triangles = t.ToArray();
                m.RecalculateNormals();
                m.RecalculateBounds();
                _notnagel = m;
                return m;
            }

            static void Quader(List<Vector3> v, List<int> t, Vector3 c, Vector3 size)
            {
                float x = size.x * 0.5f, y = size.y * 0.5f, z = size.z * 0.5f;
                int b = v.Count;
                v.Add(c + new Vector3(-x, -y, -z)); v.Add(c + new Vector3(x, -y, -z));
                v.Add(c + new Vector3(x, y, -z)); v.Add(c + new Vector3(-x, y, -z));
                v.Add(c + new Vector3(-x, -y, z)); v.Add(c + new Vector3(x, -y, z));
                v.Add(c + new Vector3(x, y, z)); v.Add(c + new Vector3(-x, y, z));
                int[] f = new int[] {
                    0,2,1, 0,3,2,   5,6,4, 4,6,7,
                    4,7,0, 0,7,3,   1,2,5, 5,2,6,
                    3,7,2, 2,7,6,   0,1,4, 4,1,5 };
                for (int i = 0; i < f.Length; i++) t.Add(b + f[i]);
            }

            /// <summary>
            /// Ein Material, das garantiert zeichnet. Shader.Find findet in
            /// einem gebauten Spiel nur, was auch eingebaut ist; deshalb der
            /// Rueckfall auf ein vorhandenes Material aus der Szene. Ein
            /// Renderer ohne Material malt Unity magenta - genau der Fehler,
            /// der bei der Testflaeche schon einmal Zeit gekostet hat.
            /// </summary>
            static Material Werkstoff()
            {
                if (_mat != null) return _mat;
                Shader sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
                if (sh == null)
                {
                    UnityEngine.Object[] all =
                        UnityEngine.Object.FindObjectsOfType(typeof(Renderer));
                    for (int i = 0; i < all.Length; i++)
                    {
                        Renderer r = all[i] as Renderer;
                        if (r == null || r.sharedMaterial == null) continue;
                        if (r.sharedMaterial.shader == null) continue;
                        sh = r.sharedMaterial.shader;
                        break;
                    }
                }
                if (sh == null)
                {
                    RevivalPlugin.L.LogWarning("Drohnenmodell: kein Shader gefunden - "
                        + "die anderen sehen sie nicht.");
                    return null;
                }
                _mat = new Material(sh);
                _mat.name = "NDR_Drone_Material";
                Texture2D tex = Assets.Texture("drone_diffuse.png", false, true);
                if (tex != null) _mat.mainTexture = tex;
                else _mat.color = new Color(0.16f, 0.17f, 0.19f, 1f);
                return _mat;
            }
        }

        // ------------------------------------------------- Videoeinblendung

        /// <summary>
        /// Das Bild eines Videosenders: Akku, Entfernung, Hoehe, Fadenkreuz,
        /// und bei schlechtem Signal Rauschen. Das ist der Teil, der aus einer
        /// fliegenden Kiste eine FPV-Drohne macht, und er kostet am wenigsten.
        ///
        /// Gehoert in OnGUI, nicht in Update: IMGUI zeichnet nur dort.
        /// </summary>
        public static void Draw()
        {
            if (!RevivalPlugin.CfgDrone.Value) return;
            try
            {
                Punkt();

                // Before the flying check, and that is the point: the mark
                // belongs to the SHOOTER, who is standing on the ground with
                // a rifle and no overlay of his own.
                if (Time.time < _markeBis) Treffermarke();

                if (!_flying || !RevivalPlugin.CfgDroneOverlay.Value) return;

                float w = Screen.width, h = Screen.height;
                float sig = Signal();
                Color alt = GUI.color;

                if (sig < 1f) Rauschen(w, h, sig);
                Rahmen(w, h, sig);
                Technik(w, h, sig);
                Fadenkreuz(w, h);
                Zahlen(w, h, sig);

                GUI.color = alt;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Drohnenanzeige: " + ex);
            }
        }

        /// <summary>Die 1x1-Textur, aus der alles hier gezeichnet wird.</summary>
        static Texture2D Punkt()
        {
            if (_dot == null)
            {
                _dot = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _dot.SetPixel(0, 0, Color.white);
                _dot.Apply();
                _dot.hideFlags = HideFlags.HideAndDontSave;
            }
            return _dot;
        }

        /// <summary>
        /// Four short bars around the middle of the screen, turned by 45
        /// degrees: the mark a shooter gets when his bullet went through a
        /// drone. Wider and red when the drone actually blew up.
        /// </summary>
        static void Treffermarke()
        {
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            float gap = Mathf.Max(7f, Screen.height * 0.013f);
            float arm = Mathf.Max(7f, Screen.height * (_markeTot ? 0.021f : 0.013f));
            float th = _markeTot ? 3f : 2f;

            Color altFarbe = GUI.color;
            Matrix4x4 altMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, new Vector2(cx, cy));
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            MarkeBalken(cx + 1f, cy + 1f, gap, arm, th);
            GUI.color = _markeTot
                ? new Color(1f, 0.35f, 0.25f, 0.95f)
                : new Color(1f, 1f, 1f, 0.9f);
            MarkeBalken(cx, cy, gap, arm, th);
            GUI.matrix = altMatrix;
            GUI.color = altFarbe;
        }

        static void MarkeBalken(float cx, float cy, float gap, float arm, float th)
        {
            GUI.DrawTexture(new Rect(cx - gap - arm, cy - th * 0.5f, arm, th), _dot);
            GUI.DrawTexture(new Rect(cx + gap, cy - th * 0.5f, arm, th), _dot);
            GUI.DrawTexture(new Rect(cx - th * 0.5f, cy - gap - arm, th, arm), _dot);
            GUI.DrawTexture(new Rect(cx - th * 0.5f, cy + gap, th, arm), _dot);
        }

        /// <summary>
        /// Waagerechte Streifen mit wechselnder Deckkraft - das Bild eines
        /// Analogsenders am Rand der Reichweite. Bewusst grob und billig:
        /// gezeichnet wird jeden Frame, und eine echte Stoerung ueber eine
        /// Textur waere Aufwand fuer denselben Eindruck.
        /// </summary>
        static void Rauschen(float w, float h, float sig)
        {
            float staerke = 1f - sig;
            int streifen = (int)(staerke * 26f);
            int seed = (int)(Time.time * 37f) * 40503 + 12345;
            for (int i = 0; i < streifen; i++)
            {
                seed = seed * 1103515245 + 12345;
                float y = (((seed >> 16) & 0x7fff) / 32767f) * h;
                seed = seed * 1103515245 + 12345;
                float hh = 1f + (((seed >> 16) & 0x7fff) / 32767f) * 9f;
                GUI.color = new Color(0.75f, 0.85f, 0.8f, 0.05f + staerke * 0.16f);
                GUI.DrawTexture(new Rect(0f, y, w, hh), _dot);
            }
        }

        /// <summary>
        /// Vier Eckwinkel wie im Sucher einer Kamera. Faerbt sich mit
        /// schlechtem Signal rot - man soll die Warnung sehen, ohne die Zahlen
        /// zu lesen.
        /// </summary>
        static void Rahmen(float w, float h, float sig)
        {
            float m = Mathf.Max(18f, h * 0.045f);
            float l = Mathf.Max(24f, h * 0.055f);
            float t = 2f;
            GUI.color = sig > 0.35f
                ? new Color(0.6f, 1f, 0.7f, 0.55f)
                : new Color(1f, 0.35f, 0.25f, 0.75f);
            float[] xs = new float[] { m, w - m - l };
            float[] ys = new float[] { m, h - m - t };
            for (int i = 0; i < 2; i++)
                for (int k = 0; k < 2; k++)
                {
                    GUI.DrawTexture(new Rect(xs[i], ys[k], l, t), _dot);
                    float yv = k == 0 ? m : h - m - l;
                    float xv = i == 0 ? m : w - m - t;
                    GUI.DrawTexture(new Rect(xv, yv, t, l), _dot);
                }
        }

        /// <summary>
        /// A compact analogue-FPV OSD: armed state and timer, heading tape,
        /// pitch ladder, ground/vertical speed, battery voltage and link
        /// quality. Every number comes from the actual flight state; the old
        /// decorative corner brackets remain, but the picture now reads like
        /// a pilot display instead of a generic game HUD.
        /// </summary>
        static void Technik(float w, float h, float sig)
        {
            Color ink = sig > 0.35f
                ? new Color(0.82f, 1f, 0.86f, 0.92f)
                : new Color(1f, 0.48f, 0.34f, 0.95f);
            float m = Mathf.Max(18f, h * 0.045f);
            int seconds = Mathf.Max(0, Mathf.FloorToInt(FlightTime));
            string timer = (seconds / 60).ToString("00") + ":"
                + (seconds % 60).ToString("00");
            string state = Time.time >= _armed ? "ARM" : "SAFE";
            OsdLabel(new Rect(m + 2f, m + 4f, 260f, 22f),
                "[REC]  " + state + "  " + timer + "  CH8", ink);

            float battery = Akku();
            float volts = 14.0f + 2.8f * battery;
            float ground = new Vector2(_vel.x, _vel.z).magnitude;
            OsdLabel(new Rect(w - m - 310f, m + 4f, 310f, 22f),
                "4S " + volts.ToString("0.0", CultureInfo.InvariantCulture)
                + "V   RSSI " + Mathf.RoundToInt(sig * 100f) + "%", ink);
            OsdLabel(new Rect(w - m - 310f, h - m - 28f, 310f, 22f),
                "GS " + ground.ToString("0.0", CultureInfo.InvariantCulture)
                + "m/s   VS " + _vel.y.ToString("+0.0;-0.0;0.0",
                    CultureInfo.InvariantCulture) + "m/s", ink);

            // Heading tape: 80 degrees around the current nose direction.
            float tapeY = m + 31f;
            float cx = w * 0.5f;
            for (int off = -40; off <= 40; off += 10)
            {
                float x = cx + off * 3.0f;
                float tick = off % 20 == 0 ? 9f : 5f;
                GUI.color = ink;
                GUI.DrawTexture(new Rect(x - 0.75f, tapeY, 1.5f, tick), _dot);
                if (off % 20 == 0)
                {
                    int heading = Mathf.RoundToInt(_yaw + off);
                    while (heading < 0) heading += 360;
                    while (heading >= 360) heading -= 360;
                    OsdLabel(new Rect(x - 18f, tapeY + 9f, 40f, 20f),
                        heading.ToString("000"), ink);
                }
            }
            GUI.color = new Color(1f, 0.42f, 0.24f, 0.95f);
            GUI.DrawTexture(new Rect(cx - 4f, tapeY - 4f, 8f, 3f), _dot);

            // Pitch ladder. Positive nose angle moves the horizon down in the
            // camera image, as it does in a real attitude display.
            float baseY = h * 0.5f + _pitch * h / 85f;
            for (int pitch = -20; pitch <= 20; pitch += 10)
            {
                float y = baseY - pitch * h / 85f;
                if (y < h * 0.25f || y > h * 0.75f) continue;
                float arm = pitch == 0 ? 58f : 34f;
                GUI.color = new Color(0f, 0f, 0f, 0.45f);
                GUI.DrawTexture(new Rect(cx - arm - 23f + 1f, y + 1f,
                                         arm, 1.5f), _dot);
                GUI.DrawTexture(new Rect(cx + 23f + 1f, y + 1f,
                                         arm, 1.5f), _dot);
                GUI.color = ink;
                GUI.DrawTexture(new Rect(cx - arm - 23f, y, arm, 1.5f), _dot);
                GUI.DrawTexture(new Rect(cx + 23f, y, arm, 1.5f), _dot);
                if (pitch != 0)
                {
                    OsdLabel(new Rect(cx - arm - 50f, y - 9f, 26f, 18f),
                        Mathf.Abs(pitch).ToString(), ink);
                    OsdLabel(new Rect(cx + arm + 26f, y - 9f, 26f, 18f),
                        Mathf.Abs(pitch).ToString(), ink);
                }
            }
        }

        static void OsdLabel(Rect rect, string text, Color ink)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.86f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f,
                               rect.width, rect.height), text);
            GUI.color = ink;
            GUI.Label(rect, text);
        }

        static void Fadenkreuz(float w, float h)
        {
            float cx = w * 0.5f, cy = h * 0.5f;
            float gap = Mathf.Max(5f, h * 0.010f);
            float arm = Mathf.Max(10f, h * 0.022f);
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            Kreuz(cx + 1f, cy + 1f, gap, arm);
            GUI.color = new Color(0.7f, 1f, 0.75f, 0.9f);
            Kreuz(cx, cy, gap, arm);
            GUI.color = new Color(1f, 0.4f, 0.25f, 0.9f);
            GUI.DrawTexture(new Rect(cx - 1.5f, cy - 1.5f, 3f, 3f), _dot);
        }

        static void Kreuz(float cx, float cy, float gap, float arm)
        {
            GUI.DrawTexture(new Rect(cx - gap - arm, cy - 1f, arm, 2f), _dot);
            GUI.DrawTexture(new Rect(cx + gap, cy - 1f, arm, 2f), _dot);
            GUI.DrawTexture(new Rect(cx - 1f, cy - gap - arm, 2f, arm), _dot);
            GUI.DrawTexture(new Rect(cx - 1f, cy + gap, 2f, arm), _dot);
        }

        static void Zahlen(float w, float h, float sig)
        {
            float m = Mathf.Max(18f, h * 0.045f);
            float akku = Akku();
            float bw = Mathf.Max(90f, w * 0.10f);
            float bh = Mathf.Max(9f, h * 0.014f);
            float bx = m + 4f, by = h - m - bh - 24f;

            // Akkubalken. Unter einem Fuenftel rot - ab da ist der Rueckweg
            // ohnehin keine Frage mehr, die Drohne kommt nicht zurueck.
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(bx - 1f, by - 1f, bw + 2f, bh + 2f), _dot);
            GUI.color = akku > 0.2f
                ? new Color(0.55f, 1f, 0.6f, 0.85f)
                : new Color(1f, 0.35f, 0.25f, 0.9f);
            GUI.DrawTexture(new Rect(bx, by, bw * akku, bh), _dot);

            float hoehe = Hoehe();
            string zeile = "AKKU " + Mathf.RoundToInt(akku * 100f) + "%"
                + "   ENTF " + Mathf.RoundToInt(Entfernung()) + " m"
                + "   HOEHE " + (hoehe < 0f ? "--" : Mathf.RoundToInt(hoehe).ToString()) + " m"
                + "   SIG " + Mathf.RoundToInt(sig * 100f) + "%";
            if (Jammer.Warnt) zeile = "STOERSENDER   " + zeile;
            else if (Motorlos()) zeile = "AKKU LEER - SIE FAELLT   " + zeile;
            else if (sig < 0.35f) zeile = "SIGNAL SCHWACH   " + zeile;

            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(bx + 1f, by + bh + 5f, w, 22f), zeile);
            GUI.color = sig > 0.35f && !Motorlos()
                ? new Color(0.75f, 1f, 0.8f, 0.95f)
                : new Color(1f, 0.45f, 0.3f, 0.95f);
            GUI.Label(new Rect(bx, by + bh + 4f, w, 22f), zeile);
        }

        // -------------------------------------------------------------- Ton

        /// <summary>
        /// Der Klang wird gerechnet, nicht geladen.
        ///
        /// Begruendung: ob und wie sich eigene Klaenge ueber Resources.Load ins
        /// Spiel bringen lassen, ist offen (REVERSE_ENGINEERING.md). Vier
        /// Sinusroehren mit Oberwelle und etwas Rauschen klingen nach
        /// Quadrokopter, kosten nichts und haengen von nichts ab. Alle
        /// Frequenzen sind ganze Vielfache von 1 Hz, damit die Sekunde
        /// nahtlos in sich selbst uebergeht - sonst knackt die Schleife.
        /// </summary>
        public static class Sound
        {
            static AudioClip _hum;

            public static AudioClip Hum()
            {
                if (_hum != null) return _hum;
                const int rate = 22050;
                float[] d = new float[rate];
                int[] rotor = new int[] { 187, 193, 199, 211 };
                int seed = 1163;
                for (int i = 0; i < d.Length; i++)
                {
                    float t = (float)i / rate;
                    float s = 0f;
                    for (int k = 0; k < rotor.Length; k++)
                    {
                        float w = 2f * Mathf.PI * rotor[k] * t;
                        s += Mathf.Sin(w) * 0.22f + Mathf.Sin(w * 2f) * 0.07f;
                    }
                    // Billiges, deterministisches Rauschen - kein System.Random,
                    // damit jeder Client denselben Klang bekommt.
                    seed = seed * 1103515245 + 12345;
                    float r = (((seed >> 16) & 0x7fff) / 16383.5f) - 1f;
                    s += r * 0.05f;
                    d[i] = Mathf.Clamp(s * 0.5f, -1f, 1f);
                }
                _hum = AudioClip.Create("NDR_DroneHum", d.Length, 1, rate, false);
                _hum.SetData(d, 0);
                return _hum;
            }

            public static AudioSource Anhaengen(GameObject go)
            {
                if (!RevivalPlugin.CfgDroneSound.Value || go == null) return null;
                try
                {
                    AudioSource src = go.AddComponent<AudioSource>();
                    src.clip = Hum();
                    src.loop = true;
                    src.playOnAwake = false;
                    src.spatialBlend = 1f;              // voll raeumlich
                    src.rolloffMode = AudioRolloffMode.Logarithmic;
                    src.minDistance = 5f;
                    src.maxDistance = RevivalPlugin.CfgDroneSoundRange.Value;
                    src.volume = RevivalPlugin.CfgDroneSoundVolume.Value;
                    src.Play();
                    return src;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Drohnenton: " + ex.Message);
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// Waehrend die Drohne fliegt, steht der Koerper still.
    ///
    /// Nicht durch Abschalten von Skripten, sondern durch die eigenen
    /// Sperren des Spiels: eine Reihe von `Cant...`-Praedikaten entscheidet
    /// ohnehin schon jeden Frame, ob der Spieler laufen, drehen, springen,
    /// schiessen oder etwas aufheben darf. Ein Postfix, der waehrend des
    /// Fluges true erzwingt, ist deshalb weder ein Eingriff noch etwas, das
    /// aufgeraeumt werden muesste: hoert die Drohne auf zu fliegen, gilt
    /// wieder das Urteil des Spiels.
    ///
    /// ANGREIFBAR BLEIBT ER. Das ist Absicht und der Kern des Spielgefuehls -
    /// wer eine Drohne startet, sucht sich vorher Deckung.
    /// </summary>
    [HarmonyPatch]
    /// <summary>
    /// Die Drohne weckt, worueber sie fliegt.
    ///
    /// DER BEFUND (Benutzer, 2026-08-30): weit weg vom eigenen Koerper
    /// geflogen, und alle NPCs standen in T-Pose ueber dem Boden, nicht
    /// bekaempfbar. Das ist kein Fehler des Spiels, sondern seine
    /// Entfernungsabschaltung - siehe RE 22:
    ///
    ///     NPC_Settlement.Update
    ///       -> PlayersDistanceControll   alle 2 s, laeuft
    ///          NetworkPlayers ab und misst gegen CheckPlayersDistRadius
    ///       -> AutoDisableControl        kein Spieler in Reichweite, 5 s
    ///          Nachlauf, dann DisableForLocalPlayer
    ///            -> SetEnableNpcAi(false) -> NPC_AI2.SetActiveAI(false)
    ///               -> Anim.Stop()
    ///
    /// `Anim` ist eine LEGACY-`Animation`. Angehalten faellt das Skinned Mesh
    /// in seine Bindepose zurueck, und die Bindepose ist genau die T-Pose, die
    /// der Benutzer gesehen hat.
    ///
    /// Die Kamera ist waehrend des Fluges die Drohne, und `DynamicObjectsManager`
    /// misst von der KAMERA - Tueren, Items und Spielerphysik folgen der Drohne
    /// also schon. Nur die Siedlung misst von SPIELERN, und der Pilot steht
    /// hunderte Meter weit weg.
    ///
    /// DER EINGRIFF ist eine Zeile Antwort auf eine Frage, die das Spiel sich
    /// selbst stellt: `HasBesideDistance(_settlementPos, spielerFlach, r2)`.
    /// Sie wird NUR aus `PlayersDistanceControll` gerufen (mit
    /// `scan_call.py` geprueft), also faelscht dieser Postfix nichts anderes.
    /// Steht die Drohne im Radius der Siedlung, ist die Antwort "ja" - und
    /// danach laeuft der eigene Weg des Spiels: OnPlayerEnterZone, KI an,
    /// Animation an, und beim Wegfliegen OnPlayerExitZone von selbst wieder
    /// aus.
    ///
    /// NUR DIE ZEILE DES PILOTEN wird gefaelscht. `OnPlayerEnterZone` schaltet
    /// die sichtbare Darstellung nur fuer den lokalen Spieler ein; eine fremde
    /// Zeile mitzufaelschen wuerde einem anderen Spieler NPCs an den Hals
    /// haengen, die nicht bei ihm sind. Erkannt wird sie an der Position: die
    /// uebergebene ist die des Spielerobjekts mit y = 0, und der Pilot steht
    /// still.
    /// </summary>
    public static class DroneNpcHook
    {
        /// <summary>Meter, innerhalb derer eine Position als "der Pilot" gilt.
        /// Der Koerper steht still, das Spielerobjekt wackelt um Zentimeter -
        /// zwei Meter sind grosszuegig und trotzdem eindeutig, solange nicht
        /// zwei Spieler aufeinander stehen.</summary>
        const float Selbst = 2f;

        /// <summary>__0 = _settlementPos (y = 0), __1 = Spielerposition
        /// (y = 0), __2 = Radius zum Quadrat.</summary>
        public static void Postfix(Vector3 __0, Vector3 __1, float __2,
                                   ref bool __result)
        {
            if (__result) return;
            if (!RevivalPlugin.CfgDroneWake.Value) return;
            if (!Drone.Flying) return;

            Vector3 pilot;
            if (!Drone.PilotAt(out pilot)) return;
            pilot.y = 0f;
            if ((pilot - __1).sqrMagnitude > Selbst * Selbst) return;

            Vector3 drohne = Drone.Position;
            drohne.y = 0f;
            float radius2 = __2;
            if (RevivalPlugin.CfgDroneNpcFire != null
                && RevivalPlugin.CfgDroneNpcFire.Value)
            {
                float r = Mathf.Max(0f, RevivalPlugin.CfgDroneNpcFireRange.Value);
                radius2 = Mathf.Max(radius2, r * r);
            }
            if ((drohne - __0).sqrMagnitude < radius2) __result = true;
        }

        public static void Install(Harmony harmony)
        {
            if (!RevivalPlugin.CfgDrone.Value) return;
            try
            {
                Type t = RevivalPlugin.TypeByName("NPC_Settlement");
                MethodInfo m = t == null ? null
                             : AccessTools.Method(t, "HasBesideDistance", null, null);
                if (m == null || m.ReturnType != typeof(bool))
                {
                    RevivalPlugin.L.LogWarning("Drohne: NPC_Settlement.HasBesideDistance "
                        + "nicht gefunden - NPCs bleiben in T-Pose, wenn die Drohne "
                        + "allein bei ihnen ist.");
                    return;
                }
                harmony.Patch(m, null,
                    new HarmonyMethod(typeof(DroneNpcHook).GetMethod("Postfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("Drohne: NPCs unter der Drohne bleiben wach.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Drohne: NPC-Weckruf nicht eingehaengt - " + ex);
            }
        }
    }

    /// <summary>
    /// Hostile NPCs answer a drone with their REAL firearm controller. The
    /// game's target field cannot be used: `NPC_AI2.ShootToTarget` assumes the
    /// target has a PlayerNetworkController and dereferences it before
    /// `FireTo`. A drone is deliberately not a fake player. Instead this class
    /// does the missing target choice, then calls
    /// `NPC_FirearmWeaponController.FireTo` with a world point. That preserves
    /// the weapon's own muzzle flash, sound, spread, raycast and tracer.
    ///
    /// Only the local drone is considered. Its pilot owns its hit points, so
    /// the same client decides the deliberately low hit chance and subtracts a
    /// hit. Remote clients receive the real firearm effect through the game's
    /// weapon network path. At most three closest hostile NPCs fire per volley;
    /// no drone, no scan and no cost.
    /// </summary>
    public static class DroneNpcFire
    {
        /// <summary>How much of the point blank chance is left at maximum
        /// range. A rifle at 110 m against a moving 40 cm target is a worse
        /// shot than at 10 m, but not a hopeless one.</summary>
        const float Abfall = 0.5f;

        /// <summary>Seconds before the same man is asked to reload again.
        /// `NPC_AI2.OnBulletsEnded` starts `ReloadingCor(3f)`, so anything
        /// above that is enough; five leaves room for the animation.</summary>
        const float NachladeWarten = 5f;

        class Shooter
        {
            public Component Ai;
            public Component Gun;
            public Vector3 Muzzle;
            public float Distance2;
        }

        static float _next;
        static GameObject _pilot;
        static object _pilotTarget;
        static bool _announced;
        static bool _lookedUp, _failed;

        /// <summary>Instance id of an NPC -> when it may be asked to reload
        /// again. Without this a man who empties his magazine at the drone
        /// never fires again: the game reloads inside its own shooting
        /// actions, and this class deliberately does not go through them.</summary>
        static Dictionary<int, float> _nachladen = new Dictionary<int, float>();

        static Type _aiType, _ngsType, _enemyTargetType;
        static FieldInfo _gunField, _sensorField, _initializedField;
        static FieldInfo _networkPlayers, _rateDelay;
        static PropertyInfo _instance;
        static MethodInfo _isEnemy, _isAlive, _fireTo, _getMuzzle;
        static MethodInfo _hasBullets, _cantWork, _bulletsEnded;

        public static void Reset()
        {
            _next = Time.time + 0.5f;
            _pilot = null;
            _pilotTarget = null;
            _announced = false;
            _nachladen.Clear();
        }

        public static void Tick()
        {
            if (!Drone.Flying) return;
            if (RevivalPlugin.CfgDroneNpcFire == null
                || !RevivalPlugin.CfgDroneNpcFire.Value) return;
            if (Time.time < _next) return;

            float seconds = Mathf.Max(0.35f,
                RevivalPlugin.CfgDroneNpcShotSeconds.Value);
            _next = Time.time + seconds * UnityEngine.Random.Range(0.8f, 1.2f);

            try
            {
                if (!LookUp()) return;
                if (_pilot == null)
                {
                    _pilot = FindPilot();
                    if (_pilot != null) _pilotTarget = FindEnemyTarget(_pilot);
                }
                if (_pilot == null) return;
                if (_pilotTarget == null)
                {
                    Fail("pilot has no " + _enemyTargetType.Name + " component");
                    return;
                }

                float range = Mathf.Max(1f,
                    RevivalPlugin.CfgDroneNpcFireRange.Value);
                float range2 = range * range;
                List<Shooter> closest = new List<Shooter>();
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_aiType);
                for (int i = 0; i < all.Length; i++)
                {
                    Component ai = all[i] as Component;
                    if (ai == null || !ai.gameObject.activeInHierarchy) continue;
                    if (!Bool(_initializedField, ai, true)) continue;
                    if (!Bool(_sensorField, ai, true)) continue;
                    if (!(bool)_isAlive.Invoke(ai, null)) continue;
                    if (!(bool)_isEnemy.Invoke(ai,
                                               new object[] { _pilotTarget })) continue;

                    Component gun = _gunField.GetValue(ai) as Component;
                    if (gun == null || !Ready(ai, gun)) continue;
                    Vector3 muzzle = (Vector3)_getMuzzle.Invoke(gun, null);
                    if (muzzle == Vector3.zero) muzzle = ai.transform.position + Vector3.up * 1.4f;
                    float d2 = (Drone.Position - muzzle).sqrMagnitude;
                    if (d2 < 4f || d2 > range2) continue;
                    if (!Visible(ai, muzzle, Mathf.Sqrt(d2))) continue;

                    Shooter s = new Shooter();
                    s.Ai = ai;
                    s.Gun = gun;
                    s.Muzzle = muzzle;
                    s.Distance2 = d2;
                    Insert(closest, s);
                }

                if (closest.Count == 0) return;
                if (!_announced)
                {
                    _announced = true;
                    RevivalPlugin.L.LogInfo("Drohne: " + closest.Count
                        + " hostile NPC(s) open fire, accuracy "
                        + RevivalPlugin.CfgDroneNpcAccuracy.Value.ToString("0.00")
                        + ", range " + range.ToString("0") + " m.");
                }

                for (int i = 0; i < closest.Count && Drone.Flying; i++)
                    Fire(closest[i], range);
            }
            catch (Exception ex)
            {
                // NPC fire is an optional counter to the drone. A reflection
                // mismatch must disable this volley, never abort the flight.
                RevivalPlugin.L.LogWarning("Drohne: NPC-Beschuss - " + ex.Message);
            }
        }

        static void Insert(List<Shooter> list, Shooter s)
        {
            int at = 0;
            while (at < list.Count && list[at].Distance2 <= s.Distance2) at++;
            list.Insert(at, s);
            int max = Mathf.Max(1, RevivalPlugin.CfgDroneNpcShooters.Value);
            while (list.Count > max) list.RemoveAt(list.Count - 1);
        }

        static void Fire(Shooter s, float range)
        {
            Vector3 to = Drone.Position - s.Muzzle;
            float dist = to.magnitude;
            if (dist < 0.1f) return;
            Vector3 dir = to / dist;

            float chance = Mathf.Clamp01(RevivalPlugin.CfgDroneNpcAccuracy.Value);
            chance *= 1f - (1f - Abfall) * Mathf.Clamp01(dist / range);
            bool hit = UnityEngine.Random.value < chance;
            Vector3 aim = Drone.Position;
            if (!hit)
            {
                Vector3 side = Vector3.Cross(dir, Vector3.up);
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                side.Normalize();
                Vector3 up = Vector3.Cross(side, dir).normalized;
                float spread = Mathf.Max(2.5f, dist * 0.08f);
                float x = UnityEngine.Random.Range(-spread, spread);
                float y = UnityEngine.Random.Range(-spread, spread);
                if (Mathf.Abs(x) + Mathf.Abs(y) < spread * 0.5f)
                    x += x < 0f ? -spread : spread;
                aim += side * x + up * y;
            }

            _fireTo.Invoke(s.Gun, new object[] { aim, true });
            if (hit) Drone.NpcTreffer(s.Ai.transform.position);
        }

        static bool Visible(Component ai, Vector3 muzzle, float distance)
        {
            Vector3 dir = (Drone.Position - muzzle) / distance;
            Vector3 from = muzzle + dir * 0.35f;
            float left = distance - 1.0f;
            for (int i = 0; i < 3 && left > 0.1f; i++)
            {
                Vector3 point;
                GameObject block = Turret.RaycastObject(from, dir, left, out point);
                if (block == null) return true;
                if (block.transform.IsChildOf(ai.transform)
                    || ai.transform.IsChildOf(block.transform))
                {
                    float step = Vector3.Distance(from, point) + 0.35f;
                    left -= step;
                    from = point + dir * 0.35f;
                    continue;
                }
                return false;
            }
            return left <= 0.1f;
        }

        static bool Ready(Component ai, Component gun)
        {
            if (!(bool)_hasBullets.Invoke(gun, null)) { Nachladen(ai); return false; }
            if ((bool)_cantWork.Invoke(gun, null)) return false;
            if (_rateDelay != null)
            {
                object v = _rateDelay.GetValue(gun);
                if (v is float && (float)v >= Time.time) return false;
            }
            return true;
        }

        /// <summary>
        /// Let the man reload, with the game's own method and its own
        /// animation. `NPC_AI2.OnBulletsEnded` tells the other clients, stops
        /// his rotation, puts him into the reload state and starts
        /// `ReloadingCor(3f)`, which refills the magazine at the end. Calling
        /// it again while that runs would restart the animation, hence the
        /// wait.
        /// </summary>
        static void Nachladen(Component ai)
        {
            if (_bulletsEnded == null || ai == null) return;
            int id = ai.GetInstanceID();
            float frei;
            if (_nachladen.TryGetValue(id, out frei) && Time.time < frei) return;
            _nachladen[id] = Time.time + NachladeWarten;
            try { _bulletsEnded.Invoke(ai, null); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Drohne: NPC-Nachladen - " + ex.Message);
                _bulletsEnded = null;
            }
        }

        static bool Bool(FieldInfo field, object instance, bool fallback)
        {
            if (field == null) return fallback;
            try { return (bool)field.GetValue(instance); }
            catch { return fallback; }
        }

        static GameObject FindPilot()
        {
            object server = _instance.GetValue(null, null);
            if (server == null) return null;
            IList players = _networkPlayers.GetValue(server) as IList;
            if (players == null) return null;

            GameObject best = null;
            float best2 = 0f;
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i] as GameObject;
                if (go == null) continue;
                float d2 = (go.transform.position - Drone.Home).sqrMagnitude;
                if (best == null || d2 < best2) { best = go; best2 = d2; }
            }
            return best;
        }

        static object FindEnemyTarget(GameObject pilot)
        {
            if (pilot == null || _enemyTargetType == null) return null;
            if (_enemyTargetType == typeof(GameObject)) return pilot;
            if (_enemyTargetType.IsInstanceOfType(pilot)) return pilot;
            if (typeof(Component).IsAssignableFrom(_enemyTargetType))
                return pilot.GetComponent(_enemyTargetType);
            return null;
        }

        static bool LookUp()
        {
            if (_failed) return false;
            if (_lookedUp) return true;
            _lookedUp = true;

            _aiType = RevivalPlugin.TypeByName("NPC_AI2");
            _ngsType = RevivalPlugin.TypeByName("NetworkGameServer");
            if (_aiType == null || _ngsType == null) return Fail("NPC_AI2 or NetworkGameServer missing");

            _gunField = AccessTools.Field(_aiType, "_firearmWeaponController");
            _sensorField = AccessTools.Field(_aiType, "SensorIsActive");
            _initializedField = AccessTools.Field(_aiType, "IsInitialized");
            _isEnemy = AccessTools.Method(_aiType, "IsEnemyFraction", null, null);
            _isAlive = AccessTools.Method(_aiType, "IsAlive", Type.EmptyTypes, null);
            _bulletsEnded = AccessTools.Method(_aiType, "OnBulletsEnded",
                                               Type.EmptyTypes, null);
            _instance = _ngsType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            _networkPlayers = AccessTools.Field(_ngsType, "NetworkPlayers");
            if (_gunField == null || _isEnemy == null || _isAlive == null
                || _instance == null || _networkPlayers == null)
                return Fail("NPC target fields or player list missing");
            ParameterInfo[] enemyParams = _isEnemy.GetParameters();
            if (enemyParams.Length != 1)
                return Fail("NPC enemy test has " + enemyParams.Length + " parameters");
            _enemyTargetType = enemyParams[0].ParameterType;

            Type gunType = _gunField.FieldType;
            _fireTo = AccessTools.Method(gunType, "FireTo",
                new Type[] { typeof(Vector3), typeof(bool) }, null);
            _getMuzzle = AccessTools.Method(gunType, "GetMuzzlePos", Type.EmptyTypes, null);
            _hasBullets = AccessTools.Method(gunType, "HasBullets", Type.EmptyTypes, null);
            _cantWork = AccessTools.Method(gunType, "CantWorkWeapon", Type.EmptyTypes, null);
            _rateDelay = AccessTools.Field(gunType, "CurrentRateOfFireDelay");
            if (_fireTo == null || _getMuzzle == null || _hasBullets == null
                || _cantWork == null)
                return Fail("NPC firearm methods missing");
            return true;
        }

        static bool Fail(string why)
        {
            _failed = true;
            RevivalPlugin.L.LogWarning("Drohne: NPC-Beschuss abgeschaltet - " + why + ".");
            return false;
        }
    }

    public static class DroneInputHook
    {
        /// <summary>Typ::Methode. Alle liefern bool und heissen "geht nicht".</summary>
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
            if (Drone.Flying) __result = true;
        }

        public static void Install(Harmony harmony)
        {
            if (!RevivalPlugin.CfgDrone.Value) return;
            int gepatcht = 0;
            System.Text.StringBuilder fehlt = new System.Text.StringBuilder();
            HarmonyMethod post = new HarmonyMethod(
                typeof(DroneInputHook).GetMethod("Postfix"));

            for (int i = 0; i < Sperren.Length; i++)
            {
                string[] teile = Sperren[i].Split(new string[] { "::" },
                                                  StringSplitOptions.None);
                try
                {
                    Type t = RevivalPlugin.TypeByName(teile[0]);
                    MethodInfo m = t == null ? null
                                 : AccessTools.Method(t, teile[1], null, null);
                    if (m == null || m.ReturnType != typeof(bool))
                    {
                        if (fehlt.Length > 0) fehlt.Append(", ");
                        fehlt.Append(Sperren[i]);
                        continue;
                    }
                    harmony.Patch(m, null, post, null, null, null);
                    gepatcht++;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Drohnensperre " + Sperren[i]
                        + " nicht gepatcht: " + ex.Message);
                }
            }

            RevivalPlugin.L.LogInfo("Drohnensperren: " + gepatcht + " von "
                + Sperren.Length + " gepatcht"
                + (fehlt.Length == 0 ? "." : (", nicht gefunden: " + fehlt + ".")));
            if (gepatcht == 0)
                RevivalPlugin.L.LogWarning("Drohne: KEINE Sperre gepatcht - der "
                    + "Koerper laeuft mit, waehrend geflogen wird.");
        }
    }

    /// <summary>
    /// Every shot the local player fires, offered to the drones in the sky.
    ///
    /// It sits on `PlayerFirearmWeaponController::FireOneShot` - the method
    /// the reverse engineering notes name as the place where the game hands
    /// out its own damage, and the same one RocketHook already watches for
    /// the LAW. A postfix, so the game's shot is over and done with before
    /// anything of ours happens; nothing is changed, only read.
    ///
    /// The aiming line comes from the controller's own `MainCamera` field,
    /// which is how the LAW finds its direction as well. That means no
    /// spread, no drop and no travel time: what counted is where the
    /// crosshair was. Anything else would be a second ballistics model
    /// disagreeing with the game's own.
    /// </summary>
    public static class DroneShotHook
    {
        static FieldInfo _kamera;
        static bool _gesucht;

        public static void Postfix(object __instance)
        {
            try
            {
                bool playerDrone = RevivalPlugin.CfgDroneShootable != null
                    && RevivalPlugin.CfgDroneShootable.Value
                    && RevivalPlugin.CfgDrone != null
                    && RevivalPlugin.CfgDrone.Value;
                bool crewDrone = RevivalPlugin.CfgPatrolCrewDrone != null
                    && RevivalPlugin.CfgPatrolCrewDrone.Value;
                if (!playerDrone && !crewDrone) return;
                if (__instance == null) return;
                // While flying, the body cannot shoot at all (DroneInputHook).
                // If it ever could, a pilot must not be able to shoot his own
                // drone down through his own camera.
                if (Drone.Flying) return;

                if (!_gesucht)
                {
                    _gesucht = true;
                    _kamera = AccessTools.Field(__instance.GetType(), "MainCamera");
                    if (_kamera == null)
                        RevivalPlugin.L.LogWarning("Drohnenbeschuss: MainCamera am "
                            + "Waffencontroller nicht gefunden - Drohnen lassen sich "
                            + "nicht abschiessen.");
                }
                if (_kamera == null) return;

                Transform t = _kamera.GetValue(__instance) as Transform;
                if (t == null) return;
                float range = RevivalPlugin.CfgDroneShootRange == null
                    ? 400f : RevivalPlugin.CfgDroneShootRange.Value;
                if (playerDrone)
                    Drone.Net.Beschuss(t.position, t.forward, range, 1f);
                if (crewDrone)
                    CrewDrone.Beschuss(t.position, t.forward, range, 1f);
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Drohnenbeschuss: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Puts fire on the game's explosion. Sits as a postfix on
    /// `ExplosionObject::NetworkVisualizeExplode`, which is the RPC the game
    /// sends to everyone nearby - so this runs once on the client that fired
    /// and once on every other client with the plugin. Nothing of ours has to
    /// go over the wire.
    /// </summary>
    public static class FireHook
    {
        static bool _logged;

        public static void Postfix(object __instance)
        {
            try
            {
                Component c = __instance as Component;
                if (c == null) return;
                float radius = Radius(c);
                FireEffect.Spawn(c.transform.position, radius);
                if (!_logged && RevivalPlugin.L != null)
                {
                    _logged = true;
                    RevivalPlugin.L.LogInfo("Feuer: erste Explosion mit Radius "
                                            + radius + " m ausgeschmueckt.");
                }
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("Feuer fehlgeschlagen: " + ex);
            }
        }

        /// <summary>
        /// The blast radius of that explosion, so a grenade does not look like a
        /// 125 mm shell. The field may be an Obscured type - then the implicit
        /// conversion to float is asked for by name, the same way WeaponData
        /// reads the game's weapon values.
        /// </summary>
        static float Radius(Component c)
        {
            try
            {
                FieldInfo f = AccessTools.Field(c.GetType(), "ExplodeDamageRadius");
                if (f == null) return 6f;
                object v = f.GetValue(c);
                if (v == null) return 6f;
                if (v is float) return (float)v;
                MethodInfo[] ms = v.GetType().GetMethods(BindingFlags.Public
                                                         | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                    if (ms[i].Name == "op_Implicit" && ms[i].ReturnType == typeof(float))
                        return (float)ms[i].Invoke(null, new object[] { v });
                return 6f;
            }
            catch { return 6f; }
        }
    }

    /// <summary>
    /// The fire itself: four particle systems and one light, all built at
    /// runtime. No asset and no Blender - a flame is a bright blob that grows,
    /// turns red and then goes transparent, and that is a texture of 64 by 64
    /// pixels and a gradient.
    ///
    /// Two materials for all of it, not one per explosion. The lesson is the
    /// one from the tracer (0.5.2): `Destroy(go)` does NOT take a material
    /// created at runtime with it, and explosions happen more often than LAW
    /// shots. `HideAndDontSave` carries them over a scene change.
    ///
    /// UNGEPRUEFT im Spiel - `Shader.Find` liefert nur, was auch im Build ist.
    /// "Particles/Additive" ist dieselbe Wahl wie bei der Leuchtspur.
    /// </summary>
    public static class FireEffect
    {
        static Material _additive;
        static Material _blended;
        static Texture2D _blob;
        static bool _noShader;

        public static void Spawn(Vector3 point, float radius)
        {
            if (RevivalPlugin.CfgFire == null || !RevivalPlugin.CfgFire.Value) return;
            if (_noShader) return;

            float scale = RevivalPlugin.CfgFireScale == null
                ? 1f : Mathf.Max(0.1f, RevivalPlugin.CfgFireScale.Value);
            float r = Mathf.Clamp(radius, 1.5f, 20f) * scale;

            Material add = Additive();
            Material blend = Blended();
            if (add == null || blend == null)
            {
                _noShader = true;
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Feuer: kein Partikelshader im Build "
                        + "gefunden - die Explosion bleibt, wie sie war.");
                return;
            }

            GameObject root = new GameObject("NDR Feuerball");
            root.transform.position = point;

            Ball(root, r, add);
            Zungen(root, r, add);
            Funken(root, r, add);
            Rauch(root, r, blend);
            Blitz(root, r);

            UnityEngine.Object.Destroy(root, 8f);
        }

        /// <summary>
        /// Long-lived fire for a destroyed patrol vehicle. The game's own
        /// DamageSmoke remains untouched near the hull; this adds the missing
        /// flames and the high column above it. The root is parented to the
        /// vehicle and has no timer, so the patrol's normal wreck cleanup is
        /// also the only cleanup this effect needs.
        /// </summary>
        public static void SpawnWreck(GameObject vehicle, bool tank)
        {
            if (vehicle == null || _noShader) return;
            if (vehicle.transform.Find("NDR Wrackfeuer") != null) return;

            Material add = Additive();
            Material blend = Blended();
            if (add == null || blend == null)
            {
                _noShader = true;
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Wrackfeuer: kein Partikelshader im "
                        + "Build gefunden - es bleibt beim DamageSmoke des Spiels.");
                return;
            }

            GameObject root = new GameObject("NDR Wrackfeuer");
            root.transform.position = vehicle.transform.position
                                    + Vector3.up * (tank ? 1.9f : 2.1f);
            root.transform.rotation = Quaternion.identity;
            root.transform.parent = vehicle.transform;

            WrackFlammen(root, add, -0.75f, tank ? 1.15f : 1.25f);
            WrackFlammen(root, add,  0.75f, tank ? 1.15f : 1.25f);
            WrackFeuerkrone(root, add, tank ? 1.35f : 1.50f);
            WrackRauch(root, blend, tank ? 1.15f : 1.30f);
            WrackLicht(root, tank ? 34f : 38f);

            if (RevivalPlugin.L != null)
                RevivalPlugin.L.LogInfo("Patrol: tall smoke and fire attached to "
                    + (tank ? "tank" : "BTR") + " wreck.");
        }

        /// <summary>One of two continuously burning patches on the deck.</summary>
        static void WrackFlammen(GameObject root, Material mat, float x, float r)
        {
            ParticleSystem ps = Neu(root, "Wrackflammen", mat, true);
            ps.transform.localPosition = new Vector3(x, 0f, 0f);

            ParticleSystem.MainModule main = ps.main;
            main.duration = 2f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.65f, 1.45f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.7f, 2.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.75f, r * 1.65f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.88f, 0.38f, 1f),
                new Color(1.00f, 0.30f, 0.03f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.10f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 110;

            Kegel(ps, r * 0.55f, 24f);
            Dauer(ps, 28f);
            Farbverlauf(ps, false);
            Groesse(ps, 0.80f, 0.08f);
            ps.Play();
        }

        /// <summary>
        /// The high part of the fire. The two deck patches give the wreck a
        /// burning base; this narrower and faster system throws visible flame
        /// tongues well above the turret or troop compartment.
        /// </summary>
        static void WrackFeuerkrone(GameObject root, Material mat, float r)
        {
            ParticleSystem ps = Neu(root, "Wrackfeuerkrone", mat, true);
            ps.transform.localPosition = Vector3.up * 0.35f;

            ParticleSystem.MainModule main = ps.main;
            main.duration = 3f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 4.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.8f, 6.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 1.2f, r * 2.4f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.96f, 0.60f, 1f),
                new Color(1.00f, 0.24f, 0.02f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.18f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 190;

            Kegel(ps, r * 0.75f, 17f);
            Dauer(ps, 38f);
            Farbverlauf(ps, false);
            Groesse(ps, 0.85f, 0.06f);
            ps.Play();
        }

        /// <summary>
        /// The landmark: dark smoke rises roughly 140 to 300 metres before it
        /// fades, widening into a column that can be read from kilometres away.
        /// Ten particles per second keep that scale affordable even when all
        /// four patrol vehicles are burning at once.
        /// </summary>
        static void WrackRauch(GameObject root, Material mat, float r)
        {
            ParticleSystem ps = Neu(root, "Wrackrauchsaule", mat, false);
            ParticleSystem.MainModule main = ps.main;
            main.duration = 16f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(18f, 28f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(7.5f, 11f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 2.2f, r * 4.5f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.06f, 0.055f, 0.05f, 0.98f),
                new Color(0.18f, 0.17f, 0.16f, 0.92f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.02f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 320;

            Kegel(ps, r * 1.15f, 7f);
            Dauer(ps, 10f);
            WrackRauchFarbe(ps);
            Groesse(ps, 0.55f, 3.10f);

            ParticleSystem.RotationOverLifetimeModule rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.18f, 0.18f);
            ps.Play();
        }

        /// <summary>Opaque low down, slowly greyer, transparent only at the top.</summary>
        static void WrackRauchFarbe(ParticleSystem ps)
        {
            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(0.07f, 0.06f, 0.055f), 0.00f),
                    new GradientColorKey(new Color(0.10f, 0.09f, 0.08f), 0.18f),
                    new GradientColorKey(new Color(0.17f, 0.16f, 0.15f), 0.62f),
                    new GradientColorKey(new Color(0.30f, 0.29f, 0.28f), 1.00f) },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(0.00f, 0.00f),
                    new GradientAlphaKey(0.86f, 0.06f),
                    new GradientAlphaKey(0.72f, 0.68f),
                    new GradientAlphaKey(0.00f, 1.00f) });
            col.color = new ParticleSystem.MinMaxGradient(g);
        }

        /// <summary>Warm light under the smoke; the particles provide motion.</summary>
        static void WrackLicht(GameObject root, float range)
        {
            GameObject go = new GameObject("Wrackglut");
            go.transform.parent = root.transform;
            go.transform.localPosition = Vector3.up * 0.6f;

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.40f, 0.10f, 1f);
            light.range = range;
            light.intensity = 4.0f;
            light.shadows = LightShadows.None;
        }

        // -------------------------------------------------- die fuenf Teile

        /// <summary>The bang itself: bright, fast, gone in half a second.</summary>
        static void Ball(GameObject root, float r, Material mat)
        {
            ParticleSystem ps = Neu(root, "Ball", mat, true);
            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.5f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.30f, 0.65f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.6f, r * 1.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.55f, r * 1.05f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.92f, 0.62f, 1f), new Color(1.00f, 0.62f, 0.16f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.12f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;

            Kugel(ps, r * 0.22f);
            Ausbruch(ps, 34);
            Farbverlauf(ps, false);
            Groesse(ps, 0.45f, 1.25f);
        }

        /// <summary>Tongues that stand and climb after the bang.</summary>
        static void Zungen(GameObject root, float r, Material mat)
        {
            ParticleSystem ps = Neu(root, "Zungen", mat, true);
            ParticleSystem.MainModule main = ps.main;
            main.duration = 1.2f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.15f, r * 0.55f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.35f, r * 0.80f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.75f, 0.30f, 1f), new Color(1.00f, 0.40f, 0.08f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.28f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 120;

            Kegel(ps, r * 0.35f, 22f);
            Ausbruch(ps, 18);
            Farbverlauf(ps, false);
            Groesse(ps, 0.70f, 0.15f);
        }

        /// <summary>Sparks. Small, fast, and the only part that falls.</summary>
        static void Funken(GameObject root, float r, Material mat)
        {
            ParticleSystem ps = Neu(root, "Funken", mat, true);
            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.4f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(r * 1.2f, r * 3.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.03f, r * 0.08f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.95f, 0.70f, 1f), new Color(1.00f, 0.55f, 0.15f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(1.1f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 160;

            Kugel(ps, r * 0.15f);
            Ausbruch(ps, 46);
            Farbverlauf(ps, false);
            Groesse(ps, 1.00f, 0.25f);
        }

        /// <summary>What is left over and stands in the air for a while.</summary>
        static void Rauch(GameObject root, float r, Material mat)
        {
            ParticleSystem ps = Neu(root, "Rauch", mat, false);
            ParticleSystem.MainModule main = ps.main;
            main.duration = 1.5f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.15f, r * 0.45f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.90f, r * 1.80f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.18f, 0.16f, 0.15f, 1f), new Color(0.42f, 0.39f, 0.36f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.06f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 80;

            Kegel(ps, r * 0.5f, 28f);
            Ausbruch(ps, 22);
            Farbverlauf(ps, true);
            Groesse(ps, 0.60f, 1.80f);

            ParticleSystem.RotationOverLifetimeModule rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.7f, 0.7f);
        }

        /// <summary>
        /// A short flash of light. Its own child object, because NdrFlash
        /// destroys what it sits on when it is done - on the root that would
        /// take the fire with it.
        /// </summary>
        static void Blitz(GameObject root, float r)
        {
            GameObject go = new GameObject("Blitz");
            go.transform.parent = root.transform;
            go.transform.localPosition = Vector3.up * (r * 0.25f);

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.72f, 0.38f, 1f);
            light.range = Mathf.Clamp(r * 5f, 8f, 90f);
            light.intensity = 7f;
            light.shadows = LightShadows.None;

            NdrFlash flash = go.AddComponent<NdrFlash>();
            flash.Life = 0.45f;
        }

        // ------------------------------------------------------- Bausteine

        static ParticleSystem Neu(GameObject root, string name, Material mat, bool vorn)
        {
            GameObject go = new GameObject(name);
            go.transform.parent = root.transform;
            go.transform.localPosition = Vector3.zero;

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
            if (r != null)
            {
                // sharedMaterial, nicht material: `material` legt fuer JEDES
                // System eine eigene Kopie an, und die bleibt beim Destroy liegen.
                r.sharedMaterial = mat;
                r.renderMode = ParticleSystemRenderMode.Billboard;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.sortingFudge = vorn ? -2f : 0f;
            }
            return ps;
        }

        static void Ausbruch(ParticleSystem ps, int anzahl)
        {
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = true;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
            ParticleSystem.Burst[] bursts = new ParticleSystem.Burst[1];
            bursts[0] = new ParticleSystem.Burst(0f, (short)anzahl);
            em.SetBursts(bursts);
        }

        static void Dauer(ParticleSystem ps, float proSekunde)
        {
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = true;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(proSekunde);
        }

        static void Kugel(ParticleSystem ps, float radius)
        {
            ParticleSystem.ShapeModule sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = Mathf.Max(0.05f, radius);
        }

        static void Kegel(ParticleSystem ps, float radius, float winkel)
        {
            ParticleSystem.ShapeModule sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.radius = Mathf.Max(0.05f, radius);
            sh.angle = winkel;
            sh.rotation = new Vector3(-90f, 0f, 0f);   // Kegel nach oben
        }

        /// <summary>
        /// White to yellow to orange to dark red, and out. That order is what
        /// makes a fire look like a fire and not like coloured dust.
        /// </summary>
        static void Farbverlauf(ParticleSystem ps, bool rauch)
        {
            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;

            Gradient g = new Gradient();
            if (rauch)
            {
                g.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(new Color(0.55f, 0.42f, 0.30f), 0.00f),
                        new GradientColorKey(new Color(0.32f, 0.30f, 0.28f), 0.35f),
                        new GradientColorKey(new Color(0.22f, 0.21f, 0.20f), 1.00f) },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(0.00f, 0.00f),
                        new GradientAlphaKey(0.55f, 0.18f),
                        new GradientAlphaKey(0.35f, 0.60f),
                        new GradientAlphaKey(0.00f, 1.00f) });
            }
            else
            {
                g.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(new Color(1.00f, 0.98f, 0.85f), 0.00f),
                        new GradientColorKey(new Color(1.00f, 0.80f, 0.30f), 0.22f),
                        new GradientColorKey(new Color(1.00f, 0.42f, 0.08f), 0.60f),
                        new GradientColorKey(new Color(0.45f, 0.09f, 0.02f), 1.00f) },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(1.00f, 0.00f),
                        new GradientAlphaKey(0.95f, 0.45f),
                        new GradientAlphaKey(0.00f, 1.00f) });
            }
            col.color = new ParticleSystem.MinMaxGradient(g);
        }

        static void Groesse(ParticleSystem ps, float anfang, float ende)
        {
            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            AnimationCurve curve = new AnimationCurve();
            curve.AddKey(0.00f, anfang);
            curve.AddKey(0.30f, Mathf.Max(anfang, ende) * 0.95f);
            curve.AddKey(1.00f, ende);
            size.size = new ParticleSystem.MinMaxCurve(1f, curve);
        }

        // ------------------------------------------------------ Werkstoffe

        static Material Additive()
        {
            if (_additive != null) return _additive;
            Shader s = Finde(new string[] {
                "Particles/Additive", "Legacy Shaders/Particles/Additive",
                "Mobile/Particles/Additive" });
            if (s == null) return null;
            _additive = Werkstoff(s);
            return _additive;
        }

        static Material Blended()
        {
            if (_blended != null) return _blended;
            Shader s = Finde(new string[] {
                "Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended",
                "Mobile/Particles/Alpha Blended", "Particles/Additive" });
            if (s == null) return null;
            _blended = Werkstoff(s);
            return _blended;
        }

        static Shader Finde(string[] namen)
        {
            for (int i = 0; i < namen.Length; i++)
            {
                Shader s = Shader.Find(namen[i]);
                if (s != null) return s;
            }
            return null;
        }

        static Material Werkstoff(Shader s)
        {
            Material m = new Material(s);
            Texture2D t = Blob();
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", t);
            if (m.HasProperty("_TintColor"))
                m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            m.hideFlags = HideFlags.HideAndDontSave;
            return m;
        }

        /// <summary>
        /// One soft blob, 64 by 64. Everything else - flame, spark, smoke - is
        /// this same blob in a different colour and a different size. A texture
        /// that lives in the plugin needs no asset file, no generator and no
        /// entry in verify.py.
        /// </summary>
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
                    a = a * a * (3f - 2f * a);              // weiche Kante
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
    }

    /// <summary>
    /// Fades a light out and then removes the object it sits on. A
    /// MonoBehaviour and not a coroutine: the fire has no MonoBehaviour of its
    /// own to hang one on, and the plugin's own one would keep the coroutine
    /// alive across a scene change.
    /// </summary>
    public class NdrFlash : MonoBehaviour
    {
        public float Life = 0.45f;

        float _t;
        float _start;
        Light _light;

        void Awake()
        {
            _light = GetComponent<Light>();
            if (_light != null) _start = _light.intensity;
        }

        void Update()
        {
            if (_light == null) { UnityEngine.Object.Destroy(gameObject); return; }
            _t += Time.deltaTime;
            if (_t >= Life) { UnityEngine.Object.Destroy(gameObject); return; }
            float k = 1f - _t / Life;
            _light.intensity = _start * k * k * (0.78f + 0.22f * Mathf.Sin(_t * 70f));
        }
    }
}
