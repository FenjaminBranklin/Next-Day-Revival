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
        // Name und Beschreibung tragen beide Spielersprachen. Das Spiel liest
        // sie ueber LocalizationHook mit der aktiven Sprache; die Properties
        // Name/Descr liefern die richtige, sodass jede Anzeige (Inventar,
        // Adminmenue, Log) automatisch stimmt. Russisch ist die Sprache der
        // Mehrheit, Englisch der Rueckfall fuer alle anderen (Loc.T).
        public string NameRu;
        public string NameEn;
        public string DescrRu;
        public string DescrEn;
        public string Name  { get { return Loc.T(NameRu, NameEn); } }
        public string Descr { get { return Loc.T(DescrRu, DescrEn); } }
        public string Mesh;            // Datei im assets-Verzeichnis
        public string Diffuse;
        public string Normal;
        public string Icon;            // ItemIcon, 300x300
        public string WeaponIcon;      // WeaponIcon, 317x183; null bei Munition
        public int Bullets;            // Waffe: Gurtlaenge. Munition: Kapazitaet.
        public int ClipItemId;
        public float Weight;
        public ItemFactory Factory;

        public ItemDef(int id, int donorId, bool isWeapon,
                       string nameRu, string nameEn,
                       string descrRu, string descrEn,
                       string mesh, string diffuse, string normal,
                       string icon, string weaponIcon,
                       int bullets, int clipItemId, float weight)
        {
            Id = id; DonorId = donorId; IsWeapon = isWeapon;
            NameRu = nameRu; NameEn = nameEn;
            DescrRu = descrRu; DescrEn = descrEn;
            Mesh = mesh; Diffuse = diffuse; Normal = normal;
            Icon = icon; WeaponIcon = weaponIcon;
            Bullets = bullets; ClipItemId = clipItemId; Weight = weight;
            Factory = new ItemFactory(this);
        }
    }

    /// <summary>
    /// The one place that answers "which language is the player in".
    ///
    /// The game keeps its whole UI in five languages and resolves them in
    /// `LocalizationManager.GetLocalizationText`: it calls
    /// `SteamInterface.GetCurrentGameLanguage` ("russian"/"english"/"german"/
    /// "french"), maps it to an index (ru=0, en=1, de=2, fr=3) and picks that
    /// slot of the string array. Everything the toolkit puts on screen goes
    /// through <see cref="T"/> so it follows the SAME setting - the player
    /// never has to configure the mod's language separately.
    ///
    /// Ninety percent of the players are Russian, the rest English-speaking,
    /// so the toolkit carries Russian and English. Any other setting (a
    /// German or French client) falls back to English, which is the intended
    /// lingua franca of the second audience. German text is gone from every
    /// player-facing surface on purpose.
    /// </summary>
    public static class Loc
    {
        static MethodInfo _getLang;
        static bool _resolved;
        static int _lang = 1;      // english until proven otherwise
        static float _next;        // realtime of the next re-read

        /// <summary>Language index the game uses: ru=0, en=1, de=2, fr=3.</summary>
        public static int Lang()
        {
            // A player can switch language in the options mid-session, so this
            // is not read once. The reflection call is cheap, but the OSD asks
            // several times per frame, so cache it for a second.
            float now = Time.realtimeSinceStartup;
            if (now >= _next)
            {
                _next = now + 1f;
                _lang = Read();
            }
            return _lang;
        }

        public static bool Ru { get { return Lang() == 0; } }

        /// <summary>Russian when the client is Russian, English otherwise.</summary>
        public static string T(string ru, string en)
        {
            return Lang() == 0 ? ru : en;
        }

        static int Read()
        {
            try
            {
                if (!_resolved)
                {
                    _resolved = true;
                    Type t = RevivalPlugin.TypeByName("SteamInterface");
                    if (t != null)
                        _getLang = AccessTools.Method(t, "GetCurrentGameLanguage", null, null);
                }
                if (_getLang != null)
                {
                    string s = _getLang.Invoke(null, null) as string;
                    if (s == "russian") return 0;
                    if (s == "german") return 2;
                    if (s == "french") return 3;
                    return 1;   // english and anything unknown
                }
            }
            catch { }
            return 1;
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
        public const string VERSION = "6.5.1";

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
        internal static ConfigEntry<int> CfgPhotonResendLimit;
        internal static ConfigEntry<int> CfgPhotonQuickResend;
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
        internal static ConfigEntry<bool> CfgTankAnimate;
        internal static ConfigEntry<float> CfgTankTrackScroll;
        internal static ConfigEntry<bool> CfgTankSpinInvert;
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
        internal static ConfigEntry<int> CfgPatrolCrewLawCount;
        internal static ConfigEntry<float> CfgPatrolCrewMgShotDelay;
        internal static ConfigEntry<float> CfgPatrolCrewLawDamage;
        internal static ConfigEntry<float> CfgPatrolCrewLawRadius;
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
        internal static ConfigEntry<bool> CfgMapTeleport;
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
        // DroneAlert (self-contained proximity warning, see class DroneAlert)
        internal static ConfigEntry<bool> CfgDroneAlert;
        internal static ConfigEntry<float> CfgDroneAlertRange;
        internal static ConfigEntry<bool> CfgDroneAlertSound;
        internal static ConfigEntry<float> CfgDroneAlertVolume;
        internal static ConfigEntry<bool> CfgDroneAlertHud;

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
            DroneGear.BindConfig(Config);
            VehicleModules.BindConfig(Config);   // NDR vehicle modules
            ConvoyRepair.BindConfig(Config);     // NDR convoy vehicle repair
            VehicleArmor.BindConfig(Config);     // NDR vehicle armour balance
            RevivalConvoy.BindConfig(Config);    // NDR convoy event
            FrameProf.BindConfig(Config);        // NDR frame-time overlay (F6)
            BuildItemTable();
            VehicleModules.RegisterItems();      // NDR vehicle modules

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
            VehicleWreck.Install(_harmony);
            Turret.Install(_harmony);
            ColdHook.Install(_harmony);
            DroneInputHook.Install(_harmony);
            DroneNpcHook.Install(_harmony);
            Crew.Install(_harmony);
            Admin.Install(_harmony);
            TankNetwork.Install(_harmony);
            Patrol.Install(_harmony);
            ConvoyRepair.Install(_harmony);      // NDR convoy vehicle repair
            VehicleArmor.Install(_harmony);      // NDR vehicle armour balance
            SwatGear.Install(_harmony);          // NDR SWAT gear worn-mesh (donor mesh)

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
            CfgMapTeleport = Config.Bind("Admin", "MapTeleport", true,
                "With the map open, right-click a spot and press the Teleport "
                + "button that appears there to warp yourself to it. Uses the "
                + "same admin access as the menu; false turns it off.");

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
            // KEY MOVED OFF V (2026-09-03, user). V is the game's own "sit down"
            // bind, so holding V to launch only made the player sit - the drone
            // never lifted ("V startet nichts"). G is free on foot; the vehicle
            // module InstallKey is also G but only acts in the gunner seat, and a
            // drone only launches on foot (the antenna retracts on boarding), so
            // the two contexts never overlap. Changing this default alone does not
            // reach an installed config (CLAUDE.md point 4) - retune.py carries
            // Drone/Key = G so existing machines get it too.
            CfgDroneKey = Config.Bind("Drone", "Key", "G",
                "Taste zum Starten (gedrueckt halten) und Abbrechen, Name aus "
                + "UnityEngine.KeyCode. NICHT V - das ist im Spiel 'Hinsetzen'.");
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

            // ----------------------------------------------------- DroneAlert
            // Leichter Warnmelder: schlaegt an, wenn eine feindliche Crew-FPV in
            // der Luft naeher als AlertRange steht. Rein lokal, kein Netz.
            CfgDroneAlert = Config.Bind("DroneAlert", "Enabled", true,
                "Warnt den Spieler, wenn eine feindliche Drohne in der Luft naeher "
                + "als AlertRange kommt. Rein lokal, verschickt nichts.");
            CfgDroneAlertRange = Config.Bind("DroneAlert", "AlertRange", 500f,
                "Reichweite des Melders in Metern. Ab hier zaehlt eine Drohne als "
                + "nah und der Alarm geht an.");
            CfgDroneAlertSound = Config.Bind("DroneAlert", "Sound", true,
                "Akustisches Warnsignal. Piept schneller, je naeher die Drohne ist "
                + "- wie ein Einparkhilfe- oder Radarwarner.");
            CfgDroneAlertVolume = Config.Bind("DroneAlert", "Volume", 0.7f,
                "Lautstaerke des Warntons, 0 bis 1.");
            CfgDroneAlertHud = Config.Bind("DroneAlert", "Hud", true,
                "Blinkender Texthinweis unten links, solange eine Drohne in "
                + "Reichweite ist. Unabhaengig vom Ton nutzbar.");

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
            CfgTankAnimate = Config.Bind("Tank", "Animate", true,
                "Laufwerk beleben: die Laufraeder, das Leit- und das Triebrad "
                + "drehen sich mit der Fahrt, und die Kette laeuft mit - wie die "
                + "Raeder des BTR. false laesst das Laufwerk starr stehen.");
            CfgTankTrackScroll = Config.Bind("Tank", "TrackScroll", 1.0f,
                "Feinjustage, wie schnell die Kettenglieder relativ zur "
                + "Fahrgeschwindigkeit wandern. 1 = an der Radgeschwindigkeit "
                + "ausgerichtet; groesser laesst die Kette schneller laufen.");
            CfgTankSpinInvert = Config.Bind("Tank", "SpinInvert", false,
                "Drehrichtung von Raedern und Kette umkehren, falls sie im Spiel "
                + "verkehrt herum laufen. Reine Sichtkorrektur.");
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
            CfgPatrolCrewLawCount = Config.Bind("Patrol", "CrewLawCount", 1,
                "How many of a crew carry an M72 LAW instead of the MG42. A LAW "
                + "man fires a real rocket - a networked explosion on impact - "
                + "and re-arms in about three seconds, so one rocketeer keeps the "
                + "pressure on a vehicle or on a man behind thin cover. The rest "
                + "carry the MG42. 0 gives the whole crew the machine gun; a "
                + "number at or above the crew size gives them all the LAW.");
            CfgPatrolCrewMgShotDelay = Config.Bind("Patrol", "CrewMgShotDelay", 0.06f,
                "Seconds between two MG42 rounds for a crewman. The game paces "
                + "every NPC at a fixed ~0.25 s whatever the weapon, which is why "
                + "the belt-fed gun used to sound no faster than a rifle. 0.06 "
                + "lets it rattle at roughly the weapon's own rate; larger is "
                + "slower, 0.25 is the vanilla pace. The LAW is untouched - it "
                + "reloads between every rocket regardless.");
            CfgPatrolCrewLawDamage = Config.Bind("Patrol", "CrewLawDamage", 600f,
                "Blast damage of a crew LAW rocket on impact. The player LAW does "
                + "900; the crew's is deliberately a little softer so a near miss "
                + "is survivable and a rocket every few seconds is not an instant "
                + "death sentence.");
            CfgPatrolCrewLawRadius = Config.Bind("Patrol", "CrewLawRadius", 8f,
                "Blast radius in metres of a crew LAW rocket. The player LAW is 12.");
            CfgPatrolWreck = Config.Bind("Patrol", "WreckSeconds", 240f,
                "Seconds a destroyed patrol vehicle stays on the road before "
                + "it is removed. 0 leaves the wreck where it is.");
            CfgPatrolSpeed = Config.Bind("Patrol", "Speed", 45f,
                "Target speed in km/h on a straight leg. A waypoint may name "
                + "its own speed in the file; this is what 0 there means. "
                + "A BTR with a 12 ton centre of mass rolls over in a corner "
                + "long before it runs out of engine.");
            CfgPatrolRecordSeconds = Config.Bind("Patrol", "RecordSeconds", 0.2f,
                "Seconds between two recorded waypoints. Defaults to 0.2, i.e. "
                + "five waypoints a second, so a driven route is captured densely "
                + "enough that the map line follows the road the driver took. "
                + "The old 3 m minimum-movement rule is gone; only a 5 cm hair "
                + "remains, so a parked recorder writes nothing while any real "
                + "driving records every step. A dense route makes routecheck.py "
                + "warn about spacing - that is expected and harmless.");
            CfgPatrolPassRadius = Config.Bind("Patrol", "PassRadius", 12f,
                "How near a waypoint counts as reached. Too large and the "
                + "vehicle skips whole corners.");
            CfgPatrolStuck = Config.Bind("Patrol", "StuckSeconds", 3f,
                "Seconds under 3 km/h before the vehicle counts as stuck. It is "
                + "then moved at least 5 m forward along the route immediately; "
                + "throttle and obstacle avoidance do not reset this timer.");
            CfgPatrolRam = Config.Bind("Patrol", "RamAfter", 8f,
                "Compatibility setting from the old staged recovery. No longer "
                + "used: a stuck patrol is moved along the route immediately.");
            CfgPatrolFree = Config.Bind("Patrol", "FreeAfter", 25f,
                "Compatibility setting from the old staged recovery. No longer "
                + "used: StuckSeconds is the complete recovery delay.");

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
            CfgPhotonResendLimit = Config.Bind("Diagnostics", "PhotonResendLimit", 300,
                "Photon SentCountAllowance: how many times one reliable command "
                + "may be resent without an ACK before the client declares the "
                + "link dead (DisconnectByClientTimeout). The game's low default "
                + "is blown by a multi-second loading stall - the Photon service "
                + "loop starves, ACKs go unread, and the resends pile up until "
                + "the count trips. PhotonTimeoutMs governs a silent link; this "
                + "governs a stalled-but-alive one, and a map load is exactly a "
                + "stalled-but-alive link, so the value must be high enough to "
                + "ride the whole load out and let the 60 s silence timer be the "
                + "real death detector. The first fix at 30 was measured too low "
                + "against a real join (resent hit 13468, still dropped); the "
                + "guard also floors this to 300 so an installed cfg still "
                + "holding the old 30 gets the higher tolerance. 0 disables it.");
            CfgPhotonQuickResend = Config.Bind("Diagnostics", "PhotonQuickResend", 2,
                "Photon QuickResendAttempts: how many fast resends a reliable "
                + "command gets before falling back to RTT-timed resends. A small "
                + "value recovers quicker right after a loading stall. -1 leaves "
                + "the default.");
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
                "MG42", "MG42",
                "Пулемёт MG42. Ленточное питание, 7,62 мм, открытый затвор. "
                + "Самая высокая скорострельность в поле - полная лента уходит за десять секунд. "
                + "Ест пулемётные ленты, при нужде и коробки с магазинами.",
                "Maschinengewehr 42. Belt-fed, 7.62 mm, open bolt. "
                + "Highest rate of fire in the field - a full belt is gone in ten seconds. "
                + "Eats MG belts, and boxes or magazines at a pinch.",
                "mg42.ndmesh", "mg42_diffuse.png", "mg42_normal.png",
                "mg42_icon.png", "mg42_weapon_icon.png",
                200, 2050, 12.0f));

            Items.Add(new ItemDef(
                1161, 1010, true,
                "TAC-50", "TAC-50",
                "Магазинная винтовка под .50 BMG. Пять патронов, тяжёлый ствол, "
                + "крупный дульный тормоз. Между выстрелами нужно передёрнуть затвор, "
                + "а смена магазина длится целую вечность. "
                + "Зато попадание почти никто не переживёт.",
                "Bolt-action rifle in .50 BMG. Five rounds, heavy barrel, "
                + "large muzzle brake. You must cycle the bolt between shots, "
                + "and a magazine change takes half an eternity. "
                + "In return, almost nothing survives a hit.",
                "sniper50.ndmesh", "sniper50_diffuse.png", "sniper50_normal.png",
                "sniper50_icon.png", "sniper50_weapon_icon.png",
                5, 2051, 14.5f));

            Items.Add(new ItemDef(
                2050, 2030, false,
                "Лента 7,62 (200)", "7.62 MG belt (200)",
                "Короб с лентой на 200 патронов 7,62 для MG42. Вдвое больше коробки "
                + "- и вдвое тяжелее.",
                "Belt box with 200 rounds of 7.62 for the MG42. Twice as much as "
                + "a box - and twice the weight.",
                "mgbelt.ndmesh", "mgbelt_diffuse.png", "mgbelt_normal.png",
                "mgbelt_icon.png", null,
                200, 0, 4.0f));

            Items.Add(new ItemDef(
                2051, 2030, false,
                "Ящик .50 BMG (10)", ".50 BMG box (10)",
                "Десять патронов .50 BMG в жестяном ящике. Подходит только к TAC-50 "
                + "и весит соответственно.",
                "Ten rounds of .50 BMG in a tin box. Fits nothing but the TAC-50, "
                + "and weighs accordingly.",
                "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png",
                "ammo50_icon.png", null,
                10, 0, 3.2f));

            Items.Add(new ItemDef(
                1162, 1010, true,
                "M72 LAW", "M72 LAW",
                "Лёгкий одноразовый 66-мм гранатомёт с уже снаряжённой "
                + "кумулятивной ракетой. После единственного выстрела остаётся "
                + "пустая труба, перезарядить её нельзя. Позади трубы - опасная "
                + "зона, друзьям там не место.",
                "Light 66 mm single-use rocket launcher with a pre-loaded "
                + "shaped-charge rocket. After the one shot an empty, "
                + "non-reloadable tube is all that is left. The backblast area "
                + "behind the tube is no place for friends.",
                "law.ndmesh", "law_diffuse.png", "law_normal.png",
                "law_icon.png", "law_weapon_icon.png",
                1, 0, 2.5f));

            Items.Add(new ItemDef(
                2052, 2030, false,
                "Ракета M72 (1)", "M72 rocket (1)",
                "Выставочный образец 66-мм кумулятивной ракеты. M72 снаряжается "
                + "на заводе и в поле не перезаряжается.",
                "A display piece of a 66 mm shaped-charge rocket. The M72 is "
                + "loaded at the factory and cannot be reloaded in the field.",
                "rocket.ndmesh", "rocket_diffuse.png", "rocket_normal.png",
                "rocket_icon.png", null,
                1, 0, 6.0f));

            Items.Add(new ItemDef(
                2053, 2030, false,
                "125-мм снаряд (1)", "125 mm shell (1)",
                "Снаряд для пушки 2А46 танка Т-72. Раздельное заряжание: снаряд "
                + "с ударным взрывателем, за ним метательный заряд. Разрушений от "
                + "попадания больше, чем у LAW, но перезарядка в башне занимает "
                + "двенадцать секунд.",
                "A round for the T-72's 2A46 gun. Separately loaded: projectile "
                + "with impact fuze, propellant charge behind it. An impact tears "
                + "away more than the LAW - but reloading in the turret takes "
                + "twelve seconds.",
                "shell125.ndmesh", "shell125_diffuse.png", "shell125_normal.png",
                "shell125_icon.png", null,
                1, 0, 9.0f));

            Items.Add(new ItemDef(
                1163, 2030, false,
                "FPV-дрон", "FPV drone",
                "Небольшой квадрокоптер с камерой и встроенной боевой частью. "
                + "Запускается по нажатию клавиши и управляется дистанционно; "
                + "пока вы им управляете, вы стоите неподвижно на открытом "
                + "месте. Обратно не возвращается.",
                "Small quadcopter with a camera and a built-in warhead. "
                + "Launched at the press of a key and flown remotely; while you "
                + "fly it you stand motionless out in the open. It does not come "
                + "back.",
                "drone.ndmesh", "drone_diffuse.png", "drone_normal.png",
                "drone_icon.png", null,
                1, 0, 1.4f));

            Items.Add(new ItemDef(
                2054, 2030, false,
                "Постановщик помех Р-330 (носимый)", "R-330 jammer (portable)",
                "Широкополосный постановщик помех на носимой раме: аккумуляторный "
                + "ящик, усилитель, четыре штыревые антенны. Тот, кто его несёт, "
                + "обрывает радиоканал любого дрона в радиусе 50 м - а поскольку "
                + "взрыватель об этом не знает, дрон подрывается там, где летит. "
                + "Цена видна на весах: 26 кг, которые иначе были бы боезапасом.",
                "Broadband jammer on a carry frame: battery box, amplifier, four "
                + "whip antennas. Whoever carries it cuts the radio link of every "
                + "drone within 50 m - and since the fuze knows nothing of that, "
                + "the drone goes off wherever it happens to be flying. The price "
                + "is on the scale: 26 kg that would otherwise be ammunition.",
                "jammer.ndmesh", "jammer_diffuse.png", "jammer_normal.png",
                "jammer_icon.png", null,
                1, 0, 26.0f));

            // The antenna, drone battery and surveillance drone (own file).
            DroneGear.AddItems(Items);
            // Fire extinguisher and heavy tool kit for convoy repair (own file).
            ConvoyRepair.AddItems(Items);

            // The M7 (XM7) rifle and its 6.8x51mm magazines (own file).
            M7Rifle.AddItems(Items);
            // The SWAT uniform gear: helmet, body armour, trousers, backpack
            // (own file).
            SwatGear.AddItems(Items);

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
            Registry.RegisterMarketplace();
            if (CfgLootTables.Value) Registry.AddToLootTables();
            Research.ReportRegions();
            L.LogInfo("--- Selbsttest Ende ---");
            SetupDone = true;

            // Zweiter Anlauf: bis hierher kann der Client die Datenbank neu
            // geladen haben, etwa beim Wechsel ins Spiel.
            yield return new WaitForSeconds(12f);
            Registry.RegisterAll();
            Registry.RegisterMarketplace();
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
            // First in the frame: FrameProf.NewFrame folds the previous frame's
            // measured spans into the overlay averages and tracks the frame rate;
            // then everything below is measured against this frame's gap. The S/E
            // pairs are no-ops unless the F6 diagnostics overlay is toggled on.
            FrameProf.NewFrame();
            FrameProf.S(FrameProf.NetWatch);    NetWatch.Tick();          FrameProf.E(FrameProf.NetWatch);
            FrameProf.S(FrameProf.AdminTick);   Admin.Tick();            FrameProf.E(FrameProf.AdminTick);
            FrameProf.S(FrameProf.MapTeleTick); MapTeleport.Tick();      FrameProf.E(FrameProf.MapTeleTick);
            // Solange das Menue offen ist, gehoert der Zeiger dem Menue -
            // sonst zieht CursorGuard ihn jeden Frame zurueck ins Fenster und
            // man kann keinen Knopf treffen.
            FrameProf.S(FrameProf.Cursor);
            if (Admin.IsOpen || Patrol.EditorOpen) CursorGuard.Release();
            else CursorGuard.Tick();
            FrameProf.E(FrameProf.Cursor);
            FrameProf.S(FrameProf.Regions);     Regions.Tick();          FrameProf.E(FrameProf.Regions);
            FrameProf.S(FrameProf.Research);    Research.Tick();         FrameProf.E(FrameProf.Research);
            FrameProf.S(FrameProf.TurretTick);  Turret.Tick();           FrameProf.E(FrameProf.TurretTick);
            FrameProf.S(FrameProf.VehModTick);  VehicleModules.Tick();   FrameProf.E(FrameProf.VehModTick);   // NDR vehicle modules
            FrameProf.S(FrameProf.DroneTick);   Drone.Tick();            FrameProf.E(FrameProf.DroneTick);
            FrameProf.S(FrameProf.DroneGearT);  DroneGear.Tick();        FrameProf.E(FrameProf.DroneGearT);
            FrameProf.S(FrameProf.Arena);       Arena.Tick();            FrameProf.E(FrameProf.Arena);
            FrameProf.S(FrameProf.CarSpawn);    CarSpawn.Tick();         FrameProf.E(FrameProf.CarSpawn);
            FrameProf.S(FrameProf.PatrolTick);  Patrol.Tick();           FrameProf.E(FrameProf.PatrolTick);
            FrameProf.S(FrameProf.ConvRepTick); ConvoyRepair.Tick();     FrameProf.E(FrameProf.ConvRepTick);  // NDR convoy vehicle repair
            FrameProf.S(FrameProf.ConvoyTick);  RevivalConvoy.Tick();    FrameProf.E(FrameProf.ConvoyTick);   // NDR convoy event
            FrameProf.S(FrameProf.CrewDrone);   CrewDrone.Tick();        FrameProf.E(FrameProf.CrewDrone);
            FrameProf.S(FrameProf.DroneAlrtT);  DroneAlert.Tick();       FrameProf.E(FrameProf.DroneAlrtT);
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
            FrameProf.S(FrameProf.TurretScope); Turret.DrawScope();      FrameProf.E(FrameProf.TurretScope);
            FrameProf.S(FrameProf.DroneDraw);   Drone.Draw();            FrameProf.E(FrameProf.DroneDraw);
            FrameProf.S(FrameProf.DroneGearD);  DroneGear.Draw();        FrameProf.E(FrameProf.DroneGearD);
            FrameProf.S(FrameProf.PatrolMap);   Patrol.DrawMap();        FrameProf.E(FrameProf.PatrolMap);
            FrameProf.S(FrameProf.MapTeleDraw); MapTeleport.Draw();      FrameProf.E(FrameProf.MapTeleDraw);
            FrameProf.S(FrameProf.AdminDraw);   Admin.Draw();            FrameProf.E(FrameProf.AdminDraw);
            FrameProf.S(FrameProf.PatrolDraw);  Patrol.Draw();           FrameProf.E(FrameProf.PatrolDraw);
            FrameProf.S(FrameProf.ConvRepDraw); ConvoyRepair.Draw();     FrameProf.E(FrameProf.ConvRepDraw);  // NDR convoy vehicle repair
            FrameProf.S(FrameProf.ConvoyDraw);  RevivalConvoy.Draw();    FrameProf.E(FrameProf.ConvoyDraw);   // NDR convoy event
            FrameProf.S(FrameProf.DroneAlrtD);  DroneAlert.Draw();       FrameProf.E(FrameProf.DroneAlrtD);
            FrameProf.DrawOverlay();
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
}
