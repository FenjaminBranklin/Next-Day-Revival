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
        // Muss mit der Datei VERSION im Wurzelverzeichnis uebereinstimmen -
        // verify.py prueft das. Zwei Staende, die sich beide "0.3.0" nennen,
        // machen jeden Versionsabgleich wertlos, und genau das war zwischen
        // dem Release 0.3.0 und dem Stand vom 2026-08-28 der Fall.
        public const string VERSION = "0.5.0";

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
        internal static ConfigEntry<bool> CfgNoCold;
        internal static ConfigEntry<bool> CfgVerbose;
        internal static ConfigEntry<string> CfgExtraScenes;
        internal static ConfigEntry<bool> CfgSceneJump;
        internal static ConfigEntry<int> CfgJumpScene;
        internal static ConfigEntry<string> CfgJumpKey;
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
        internal static ConfigEntry<bool> CfgTurretAmmoBackpack;
        internal static ConfigEntry<float> CfgTurretSensitivity;
        internal static ConfigEntry<float> CfgTurretRecoil;
        internal static ConfigEntry<float> CfgTurretEyeForward;
        internal static ConfigEntry<float> CfgTurretEyeUp;
        internal static ConfigEntry<float> CfgTurretEyeSide;
        internal static ConfigEntry<bool> CfgTurretCrosshair;
        internal static ConfigEntry<bool> CfgTurretTakeCamera;
        internal static ConfigEntry<float> CfgTurretFov;
        internal static ConfigEntry<bool> CfgTurretExplosion;
        internal static ConfigEntry<float> CfgTurretExplosionDamage;
        internal static ConfigEntry<float> CfgTurretExplosionRadius;
        internal static ConfigEntry<bool> CfgTurretInvertX;
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
        internal static ConfigEntry<float> CfgTankFov;
        internal static ConfigEntry<int> CfgTankAmmoId;
        internal static ConfigEntry<int> CfgTankSpawnAmmo;
        internal static ConfigEntry<bool> CfgTankScope;
        internal static ConfigEntry<bool> CfgSpawnCar;
        internal static ConfigEntry<string> CfgSpawnCarKey;
        internal static ConfigEntry<string> CfgSpawnCarName;
        internal static ConfigEntry<float> CfgSpawnCarDistance;
        internal static ConfigEntry<bool> CfgAdmin;
        internal static ConfigEntry<string> CfgAdminKey;
        internal static ConfigEntry<string> CfgAdminIds;
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
        internal static ConfigEntry<bool> CfgDroneRequireItem;
        internal static ConfigEntry<int> CfgDroneItemId;
        internal static ConfigEntry<bool> CfgArena;
        internal static ConfigEntry<string> CfgArenaKey;
        internal static ConfigEntry<float> CfgArenaSize;
        internal static ConfigEntry<float> CfgArenaDistance;

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
            PatchRocketImpact();
            PatchLawDrop();
            Turret.Install(_harmony);
            ColdHook.Install(_harmony);
            DroneInputHook.Install(_harmony);

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
            CfgTurretDamage = Config.Bind("Turret", "Damage", 750f,
                "Schaden je Schuss. Zum Vergleich: die Granate des M72 macht 900 "
                + "im Radius 12.");
            CfgTurretRange = Config.Bind("Turret", "Range", 900f,
                "Reichweite des Schusses in Welteinheiten.");
            CfgTurretDelay = Config.Bind("Turret", "FireDelay", 0.9f,
                "Sekunden zwischen zwei Schuessen.");
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
            CfgTurretAmmoId = Config.Bind("Turret", "AmmoItemId", 2051,
                "Item-ID der Munition. 2051 ist die .50-BMG-Kiste des Toolkits.");
            CfgTurretAmmoBackpack = Config.Bind("Turret", "AmmoFromBackpack", true,
                "Ist der Kofferraum leer, auch aus dem Rucksack des Spielers "
                + "nehmen. Der Kofferraum gehoert dem Fahrzeug und ist nach dem "
                + "naechsten Spielstart mitsamt Inhalt weg.");
            // Gezielt wird mit der Maus, nicht mit dem Kopf: die Kamera sitzt
            // waehrend des Schiessens im Rohr und koennte den Turm sonst nicht
            // mehr steuern - sie zeigt ja immer schon dorthin, wo er steht.
            CfgTurretSensitivity = Config.Bind("Turret", "Sensitivity", 2.2f,
                "Grad Turmschwenk je Einheit Mausbewegung. Die Traegheit aus "
                + "TurnSpeed bleibt davon unberuehrt.");
            CfgTurretRecoil = Config.Bind("Turret", "Recoil", 0.30f,
                "Grad, die das Rohr je Schuss hochschlaegt. Bewusst klein - "
                + "ein Turmgeschuetz sitzt auf zwoelf Tonnen Fahrzeug.");
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
                + "sitzt. Der alte Schluessel CrosshairRange ist wirkungslos.");
            CfgTurretTakeCamera = Config.Bind("Turret", "TakeCamera", true,
                "Die Kamera des Spiels waehrend des Zielens stilllegen. Ohne das "
                + "zieht MouseOrbitController sie jeden Frame wieder um das "
                + "Fahrzeug herum - der Blick zeigt dann auf den eigenen BTR "
                + "statt durch das Rohr. Auf false, falls die Kamera nach dem "
                + "Aussteigen haengt.");
            CfgTurretFov = Config.Bind("Turret", "FOV", 26f,
                "Bildwinkel im Geschuetz, in Grad. Klein heisst nah heran wie im "
                + "Zielfernrohr; das Spiel selbst benutzt 60. 0 laesst den "
                + "Bildwinkel unveraendert.");
            CfgTurretExplosion = Config.Bind("Turret", "Explosion", true,
                "Am Einschlag eine Sprenggranate zuenden. Ohne das ist ein "
                + "Schuss nicht zu sehen und nicht zu hoeren - die 30 mm des "
                + "BTR-80A sind Sprengmunition, kein Gewehrschuss.");
            CfgTurretExplosionDamage = Config.Bind("Turret", "ExplosionDamage", 350f,
                "Schaden der Sprenggranate im Umkreis.");
            CfgTurretExplosionRadius = Config.Bind("Turret", "ExplosionRadius", 5f,
                "Wirkungsradius der Sprenggranate in Welteinheiten.");
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
            CfgDroneThrust = Config.Bind("Drone", "Thrust", 16f,
                "Schub vor und zurueck (W/S) in Metern je Sekundenquadrat.");
            CfgDroneSideThrust = Config.Bind("Drone", "SideThrust", 11f,
                "Schub seitwaerts (A/D).");
            CfgDroneLift = Config.Bind("Drone", "Lift", 14f,
                "Schub nach oben (Leertaste) und unten (Strg oder C).");
            CfgDroneGravity = Config.Bind("Drone", "Gravity", -5.5f,
                "Schwerkraft auf die Drohne. Absichtlich schwaecher als die "
                + "echten -9.81: eine Drohne haengt in der Luft, sie faellt nicht.");
            CfgDroneDrag = Config.Bind("Drone", "Drag", 1.4f,
                "Luftwiderstand je Sekunde, geschwindigkeitsproportional. Groesser "
                + "heisst traeger und stabiler, kleiner heisst schwebender. Zusammen "
                + "mit Thrust bestimmt das die Endgeschwindigkeit (Thrust/Drag).");
            CfgDroneMaxSpeed = Config.Bind("Drone", "MaxSpeed", 32f,
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
                "Erster von drei Photon-Ereigniscodes (Start, Lauf, Ende). Photon "
                + "verwirft alles ab 200; das Spiel selbst benutzt nur 1 und 2.");
            CfgDroneNetHz = Config.Bind("Drone", "NetHz", 15f,
                "Wie oft je Sekunde Lage und Blickrichtung an die anderen gehen. "
                + "Dazwischen wird bei ihnen interpoliert.");
            CfgDroneModelScale = Config.Bind("Drone", "ModelScale", 1f,
                "Groesse des Modells, das die anderen sehen.");
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
            CfgDroneRange = Config.Bind("Drone", "Range", 300f,
                "Funkreichweite in Metern. Darueber reisst die Verbindung ab, der "
                + "Blick faellt zum Koerper zurueck und die Drohne ist weg. Der "
                + "Wert ist GERATEN und gehoert nachgemessen - er ist zugleich die "
                + "billigste Absicherung dagegen, dass die Drohne aus dem geladenen "
                + "Teil der Welt fliegt und durch den Boden faellt.");
            CfgDroneNoiseFrom = Config.Bind("Drone", "NoiseFrom", 250f,
                "Ab dieser Entfernung rauscht das Bild. Das ist die Vorwarnung vor "
                + "dem Abriss.");
            CfgDroneOverlay = Config.Bind("Drone", "Overlay", true,
                "Videoeinblendung: Akku, Entfernung, Hoehe, Fadenkreuz, Rauschen.");
            CfgDroneRequireItem = Config.Bind("Drone", "RequireItem", true,
                "Der Start verbraucht eine Drohne aus dem Rucksack. Auf false "
                + "startet sie aus dem Nichts - zum Ausprobieren, nicht zum Spielen.");
            CfgDroneItemId = Config.Bind("Drone", "ItemId", 1163,
                "Item, das ein Start verbraucht.");

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

        void PatchRocketImpact()
        {
            try
            {
                Type t = AccessTools.TypeByName("PlayerFirearmWeaponController");
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

        void PatchLawDrop()
        {
            try
            {
                Type t = AccessTools.TypeByName("PlayerInventoryManager");
                if (t == null) { L.LogWarning("PlayerInventoryManager fuer LAW-Drop fehlt."); return; }
                MethodInfo m = AccessTools.Method(t, "DropWeaponFromHand",
                    new Type[] { typeof(int), typeof(int), typeof(int), typeof(Vector3),
                                 typeof(Quaternion), typeof(Vector3) }, null);
                if (m == null) { L.LogWarning("DropWeaponFromHand fuer LAW fehlt."); return; }
                _harmony.Patch(m,
                    new HarmonyMethod(typeof(LawDropHook).GetMethod("Prefix")),
                    null, null, null, null);

                MethodInfo inventory = AccessTools.Method(t, "DropInventoryItem",
                    new Type[] { typeof(int), typeof(int), typeof(string), typeof(bool) }, null);
                if (inventory == null) { L.LogWarning("DropInventoryItem fuer LAW fehlt."); return; }
                _harmony.Patch(inventory,
                    new HarmonyMethod(typeof(LawDropHook).GetMethod("InventoryPrefix")),
                    null, null, null, null);
                L.LogInfo("M72-LAW-Weltablage, Slotfreigabe und Todes-Drop aktiv.");
            }
            catch (Exception ex) { L.LogError("LAW-Drop-Patch fehlgeschlagen: " + ex); }
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
            Admin.Tick();
            // Solange das Menue offen ist, gehoert der Zeiger dem Menue -
            // sonst zieht CursorGuard ihn jeden Frame zurueck ins Fenster und
            // man kann keinen Knopf treffen.
            if (Admin.IsOpen) CursorGuard.Release();
            else CursorGuard.Tick();
            Research.Tick();
            Turret.Tick();
            Drone.Tick();
            Arena.Tick();
            CarSpawn.Tick();
        }

        void LateUpdate()
        {
            CameraOwner.LateTick();
        }

        void OnGUI()
        {
            Turret.DrawScope();
            Drone.Draw();
            Admin.Draw();
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

            Type bodyType = AccessTools.TypeByName("UnityEngine.Rigidbody");
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
            Type colliderType = AccessTools.TypeByName("UnityEngine.Collider");
            Component[] colliders = colliderType == null
                ? new Component[0] : spawned.GetComponentsInChildren(colliderType, true);
            for (int i = 0; i < colliders.Length; i++)
                colliders[i].gameObject.layer = ignoreLayer;

            Type explosionType = AccessTools.TypeByName("ExplosionObject");
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

        internal static void SpawnTracer(List<Vector3> points)
        {
            if (points == null || points.Count < 2) return;
            GameObject tracer = new GameObject("NDR Leuchtspur");
            LineRenderer line = tracer.AddComponent(typeof(LineRenderer)) as LineRenderer;
            if (line == null)
            {
                UnityEngine.Object.Destroy(tracer);
                throw new MissingMemberException("LineRenderer konnte nicht erzeugt werden.");
            }

            Shader shader = Shader.Find("Particles/Additive");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null)
            {
                UnityEngine.Object.Destroy(tracer);
                throw new MissingMemberException("Shader fuer LAW-Leuchtspur nicht gefunden.");
            }

            Material material = new Material(shader);
            Color bright = new Color(1.0f, 0.88f, 0.42f, 1.0f);
            if (material.HasProperty("_Color")) material.SetColor("_Color", bright);
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", bright);

            line.material = material;
            line.useWorldSpace = true;
            line.positionCount = points.Count;
            line.startWidth = 0.035f;
            line.endWidth = 0.012f;
            line.startColor = Color.white;
            line.endColor = bright;
            for (int i = 0; i < points.Count; i++) line.SetPosition(i, points[i]);
            UnityEngine.Object.Destroy(tracer, 0.22f);
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
            Type physicsType = AccessTools.TypeByName("UnityEngine.Physics");
            Type hitType = AccessTools.TypeByName("UnityEngine.RaycastHit");
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
            Type photon = AccessTools.TypeByName("PhotonNetwork");
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
    /// Das Spiel kennt fuer die neue ID kein Photon-Drop-Prefab. Ohne diesen
    /// Ersatz wirft DropWeaponFromHand eine NullReferenceException; beim Tod
    /// bricht dadurch auch PlayerDeath vor dem Respawn-Bildschirm ab.
    /// </summary>
    public static class LawDropHook
    {
        const int LAW_ID = 1162;

        public static bool InventoryPrefix(object __instance, int __0, int __1,
                                           string __2, bool __3)
        {
            if (__1 != LAW_ID) return true;
            if (__2 != "WeaponSlot" && __2 != "BackpackSlot") return true;
            try
            {
                FieldInfo spawnerField = AccessTools.Field(__instance.GetType(), "ObjectSpawner");
                Transform spawner = spawnerField == null
                    ? null : spawnerField.GetValue(__instance) as Transform;
                Vector3 position = spawner == null
                    ? Vector3.zero : spawner.position;
                Prefix(LAW_ID, 0, 0, position, Quaternion.identity, Vector3.zero);

                if (__2 == "WeaponSlot")
                {
                    MethodInfo clear = AccessTools.Method(__instance.GetType(), "ClearWeaponSlot",
                        new Type[] { typeof(int), typeof(int), typeof(bool), typeof(bool) }, null);
                    if (clear == null) throw new MissingMethodException("ClearWeaponSlot fehlt.");
                    clear.Invoke(__instance, new object[] { __0, LAW_ID, true, false });
                }
                else
                {
                    MethodInfo clear = AccessTools.Method(__instance.GetType(), "ClearBackpackSlot",
                        new Type[] { typeof(int), typeof(int), typeof(bool) }, null);
                    if (clear == null) throw new MissingMethodException("ClearBackpackSlot fehlt.");
                    clear.Invoke(__instance, new object[] { __0, LAW_ID, true });
                }

                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo("M72 LAW aus " + __2 + " " + __0
                                            + " entfernt und lokal abgelegt.");
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("M72-LAW-Slotfreigabe fehlgeschlagen: " + ex);
            }
            return false;
        }

        public static bool Prefix(int __0, int __1, int __2, Vector3 __3,
                                  Quaternion __4, Vector3 __5)
        {
            if (__0 != LAW_ID) return true;
            try
            {
                ItemDef law = null;
                for (int i = 0; i < RevivalPlugin.Items.Count; i++)
                    if (RevivalPlugin.Items[i].Id == LAW_ID)
                    {
                        law = RevivalPlugin.Items[i];
                        break;
                    }
                if (law == null) throw new MissingMemberException("LAW-ItemDef fehlt.");

                GameObject model = law.Factory.GetModelPrefab();
                if (model == null) throw new MissingMemberException("LAW-Modell-Prefab fehlt.");
                MeshFilter sourceFilter = model.GetComponentInChildren<MeshFilter>(true);
                MeshRenderer sourceRenderer = model.GetComponentInChildren<MeshRenderer>(true);
                if (sourceFilter == null || sourceFilter.sharedMesh == null
                    || sourceRenderer == null)
                    throw new MissingMemberException("LAW-Modellgeometrie fehlt.");

                // Kein Spawn-Prefab klonen: Es enthaelt viele MeshFilter und
                // ItemSpawned/Photon-Komponenten. Das erzeugte mehrere Rohre
                // und einen Pickup-Prompt, der niemals erfolgreich sein konnte.
                GameObject drop = new GameObject("M72 LAW verbrauchtes Rohr");
                MeshFilter filter = drop.AddComponent<MeshFilter>();
                filter.sharedMesh = sourceFilter.sharedMesh;
                MeshRenderer renderer = drop.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                drop.transform.position = __3 + Vector3.up * 0.35f;
                drop.transform.rotation = Quaternion.Euler(0f, 0f, 90f);

                Type colliderType = AccessTools.TypeByName("UnityEngine.BoxCollider");
                Component collider = colliderType == null ? null : drop.AddComponent(colliderType);
                if (collider == null)
                    throw new MissingMemberException("BoxCollider fuer LAW-Drop fehlt.");
                SetProperty(collider, "center", sourceFilter.sharedMesh.bounds.center);
                SetProperty(collider, "size", sourceFilter.sharedMesh.bounds.size);

                Type bodyType = AccessTools.TypeByName("UnityEngine.Rigidbody");
                Component body = bodyType == null ? null : drop.GetComponent(bodyType);
                if (body == null && bodyType != null) body = drop.AddComponent(bodyType);
                if (body != null)
                {
                    SetProperty(body, "useGravity", true);
                    SetProperty(body, "isKinematic", false);
                    Vector3 velocity = __5;
                    if (velocity.magnitude > 15f) velocity = velocity.normalized * 15f;
                    SetProperty(body, "velocity", velocity);
                    SetProperty(body, "angularVelocity", new Vector3(0f, 0.8f, 0f));
                }

                UnityEngine.Object.Destroy(drop, 300f);
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo("M72 LAW lokal abgelegt (Bullets=" + __1 + ").");
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("M72-LAW-Ablage fehlgeschlagen: " + ex);
            }

            // Das Original wuerde fuer ID 1162 immer ein null-Drop-Prefab
            // dereferenzieren. Der aufrufende Inventarcode entfernt den Slot.
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

        static float Bildwinkel()
        { return _tank ? RevivalPlugin.CfgTankFov.Value : RevivalPlugin.CfgTurretFov.Value; }

        static float Sprengschaden()
        { return _tank ? RevivalPlugin.CfgTankExplosionDamage.Value : RevivalPlugin.CfgTurretExplosionDamage.Value; }

        static float Sprengradius()
        { return _tank ? RevivalPlugin.CfgTankExplosionRadius.Value : RevivalPlugin.CfgTurretExplosionRadius.Value; }

        static int MunitionsId()
        { return _tank ? RevivalPlugin.CfgTankAmmoId.Value : RevivalPlugin.CfgTurretAmmoId.Value; }

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
                Type vgs = AccessTools.TypeByName("VehicleGameSystem");
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
        /// </summary>
        public static void FreeSeatPostfix(object __instance, ref int __result)
        {
            try
            {
                if (__result < 0) return;
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
            Type vgsType = AccessTools.TypeByName("VehicleGameSystem");
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
            List<Transform> found = new List<Transform>();
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == "turret") found.Add(all[i]);
            _turrets = found.ToArray();
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
            _pitch = Mathf.Clamp(_pitch,
                                 RevivalPlugin.CfgTurretPitchMin.Value,
                                 RevivalPlugin.CfgTurretPitchMax.Value);
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
                    _pitch = Mathf.Clamp(_pitch,
                                         RevivalPlugin.CfgTurretPitchMin.Value,
                                         RevivalPlugin.CfgTurretPitchMax.Value);
                    if (_yaw > 180f) _yaw -= 360f;
                    if (_yaw < -180f) _yaw += 360f;
                }
            }

            Quaternion want = LocalRotationFor(_yaw, _pitch);
            float step = Drehgeschwindigkeit() * Time.deltaTime;
            for (int i = 0; i < _turrets.Length; i++)
                _turrets[i].localRotation =
                    Quaternion.RotateTowards(_turrets[i].localRotation, want, step);
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
        static Quaternion LocalRotationFor(float yaw, float pitch)
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
            yaw = 0f;
            pitch = 0f;
            if (_turrets.Length == 0) return false;
            Vector3 d = _turrets[0].localRotation * new Vector3(0f, -1f, 0f);
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
                                 RevivalPlugin.CfgTurretPitchMin.Value,
                                 RevivalPlugin.CfgTurretPitchMax.Value);

            Vector3 origin, dir;
            AimRay(out origin, out dir);
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

            // Sprenggranate am Einschlag. Das BTR-80A schiesst 30 mm
            // Sprengmunition, keine Gewehrkugeln: der Einschlag gehoert
            // gesehen, und Flaechenwirkung gehoert dazu.
            if (RevivalPlugin.CfgTurretExplosion.Value)
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

        /// <summary>Leuchtspur von der Muendung zum Einschlag.</summary>
        static void Tracer(Vector3 von, Vector3 bis)
        {
            try
            {
                List<Vector3> bahn = new List<Vector3>();
                bahn.Add(von);
                bahn.Add(bis);
                RocketHook.SpawnTracer(bahn);
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
            Type physicsType = AccessTools.TypeByName("UnityEngine.Physics");
            Type hitType = AccessTools.TypeByName("UnityEngine.RaycastHit");
            if (physicsType == null || hitType == null) return null;

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
            {
                RevivalPlugin.L.LogWarning("Geschuetz: Physics.Raycast nicht gefunden.");
                return null;
            }

            object[] args = new object[] {
                origin, direction, Activator.CreateInstance(hitType), range };
            if (!(bool)chosen.Invoke(null, args)) return null;

            PropertyInfo pointProperty = hitType.GetProperty("point",
                BindingFlags.Public | BindingFlags.Instance);
            if (pointProperty != null) point = (Vector3)pointProperty.GetValue(args[2], null);

            PropertyInfo colliderProperty = hitType.GetProperty("collider",
                BindingFlags.Public | BindingFlags.Instance);
            if (colliderProperty == null) return null;
            Component hitCollider = colliderProperty.GetValue(args[2], null) as Component;
            return hitCollider == null ? null : hitCollider.gameObject;
        }

        /// <summary>
        /// Sucht am getroffenen Objekt die genannte Komponente und ruft ihre
        /// Schadensmethode. Die Argumentliste ist nicht belegt, deshalb wird
        /// sie aus der Methode selbst gelesen: der erste float bekommt den
        /// Schaden, alles andere den Vorgabewert seines Typs. Passt keine
        /// Signatur, wird das protokolliert statt geraten.
        /// </summary>
        static bool TryDamage(GameObject struck, string typeName, string rpc, float damage)
        {
            Type t = AccessTools.TypeByName(typeName);
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
        static List<object> PlayerInventories()
        {
            List<object> found = new List<object>();
            Type t = AccessTools.TypeByName("PlayerInventoryManager");
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
        /// Der Kofferraum. Ueber die KOMPONENTE gesucht, nicht ueber den Pfad:
        /// "InteractColliders/BagaggeContainer" haengt am BTR nicht an der
        /// Wurzel, sondern unter Chassis (gemessen am Prefab BTR-80A_Spawn) -
        /// Transform.Find lieferte deshalb immer null, und der Kofferraum wurde
        /// nie durchsucht.
        /// </summary>
        static object TrunkContainer()
        {
            if (_vehicleRoot == null) return null;
            Type ic = AccessTools.TypeByName("ItemsContainer");
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
                // Im Panzer sieht man durch ein Zielfernrohr, nicht ueber vier
                // Striche im freien Bild: schwarze Fassung, runde Linse,
                // Winkelmarke mit Entfernungsskala. Das Bild bringt sein
                // Fadenkreuz selbst mit - deshalb bleibt daneben keines stehen.
                bool panzerglas = _tank && RevivalPlugin.CfgTankScope.Value
                                  && PanzerScope() != null;
                if (panzerglas) Vollbild(PanzerScope());
                else if (RevivalPlugin.CfgTurretScopeOverlay.Value) DrawOverlay();
                if (RevivalPlugin.CfgTurretCrosshair.Value && !panzerglas) DrawCrosshair();
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
                Type t = AccessTools.TypeByName("UnityEngine.Terrain");
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
    /// Argument 0 ist die Item-Id, Argument 6 die Menge - belegt daran, dass
    /// dort genau die Bullets-Werte der Item-Tabelle stehen. Die uebrigen
    /// Argumente sind in allen beobachteten Aufrufen 0 beziehungsweise False;
    /// was sie bedeuten, ist **Hypothese** und wird hier nicht geraten,
    /// sondern auf den beobachteten Wert gesetzt.
    /// </summary>
    public static class Admin
    {
        const int FensterId = 0x4E445241;

        static bool _offen;
        static bool _fokusLoesen;
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static Rect _fenster = new Rect(40f, 40f, 430f, 0f);
        static Vector2 _rollen;
        static string _menge = "";
        static string _status = "Bereit.";

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

        // SteamInterface::GetSteamID ist der Umweg des Spiels ueber
        // Steamworks.SteamUser (liegt in Assembly-CSharp-firstpass, nicht in
        // Assembly-CSharp). Zurueck kommt ein CSteamID; die Zahl steht in
        // dessen Feld m_SteamID - belegt mit ildasm gegen beide Assemblies.
        static string SteamId()
        {
            try
            {
                Type t = AccessTools.TypeByName("SteamInterface");
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
            GUILayout.Label("Items in den Rucksack legen");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Menge (leer = Standard):", GUILayout.Width(170f));
            _menge = GUILayout.TextField(_menge, 6, GUILayout.Width(60f));
            GUILayout.EndHorizontal();

            _rollen = GUILayout.BeginScrollView(_rollen, GUILayout.Height(190f));
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
            GibItem(d.Id, menge, out meldung);
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

                m.Invoke(pim, args);
                meldung = "gegeben: " + id + " x" + menge
                          + (mengeGesetzt ? "" : " (Argument 6 ist kein int - "
                                                 + "Menge nicht gesetzt)");
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
        /// Der Manager ist eine Komponente in der Szene. Erst die ueblichen
        /// Singleton-Namen probieren, dann in der Szene suchen - FindObjectOfType
        /// ist teuer, aber das hier passiert nur auf Knopfdruck.
        /// </summary>
        static object InventarManager()
        {
            Type t = AccessTools.TypeByName("PlayerInventoryManager");
            if (t == null) return null;
            string[] namen = new string[] { "current", "Instance", "instance" };
            for (int i = 0; i < namen.Length; i++)
            {
                MethodInfo g = AccessTools.PropertyGetter(t, namen[i]);
                if (g == null || !g.IsStatic) continue;
                object o = g.Invoke(null, null);
                if (o != null) return o;
            }
            return UnityEngine.Object.FindObjectOfType(t);
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
                Type t = AccessTools.TypeByName("CameraFPSController");
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
                Type t = AccessTools.TypeByName("PlayerLifeDataManager");
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
    /// 1. Der Datenblock bleibt null. Sein erster Eintrag ist die PointId, und
    ///    VehicleGameSystem::FindMySpawnPointAndSet setzt das Fahrzeug damit auf
    ///    die Position des zugehoerigen Spawnpunkts zurueck - es stuende dann
    ///    nicht mehr dort, wo es geprueft werden soll. Bei null kehrt die
    ///    Methode gleich am Anfang zurueck (IL_000C..IL_0044).
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
    /// WAS ANDERE SPIELER SEHEN: einen BTR. Uebertragen wird der Resources-
    /// Pfad, nicht das Ergebnis; jeder Client baut sein eigenes Exemplar aus
    /// demselben Prefab, und dieser Umbau ist rein oertlich. Der Weg dahin
    /// stuende im fuenften Parameter von InstantiateSceneObject (object[]
    /// data, heute null) - gebaut ist er nicht.
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
        /// Turmring des T-72 in der Wanne, in Spieleinheiten. Aus t72_mesh.py
        /// (RING_Y und RING_Z mal U). Der BTR hat den Turm hoeher und weiter
        /// vorn; bliebe die Transform stehen, schwebte der Panzerturm.
        /// </summary>
        static readonly Vector3 Turmring = new Vector3(0f, -1.2f, 4.5f);

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

        static Material _mat;

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
            Type vgsType = AccessTools.TypeByName("VehicleGameSystem");
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
            for (int i = 0; i < seats.childCount; i++)
            {
                Transform c = seats.GetChild(i);
                if (c.name == Turret.SeatName) continue;
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
            // verschiebt damit den Mitfahrer, und sonst nichts. Der
            // Geschuetzsitz bleibt, wo Turret.InitCarPrefix ihn hingesetzt hat.
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

            FieldInfo fPass = AccessTools.Field(vgsType, "Passengers");
            if (fPass != null) fPass.SetValue(vgs, new GameObject[seats.childCount]);

            RevivalPlugin.L.LogInfo("Panzer: " + seats.childCount + " Sitze ("
                + behalten + " Mitfahrer plus Geschuetz), Passengers neu gesetzt, "
                + "Mitfahrer in die Wanne gesetzt.");
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
            // Panzerlack ist matt und nicht metallisch. Die Werte der Waffen
            // waeren hier zu glaenzend.
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.10f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.04f);
            if (m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", null);
            m.DisableKeyword("_METALLICGLOSSMAP");
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
                + ", Normal Map " + (nrm != null) + ".");
            _mat = m;
            return m;
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

        public static void Tick()
        {
            try
            {
                // Der Panzer haengt NICHT an Research/SpawnCar: der Fahrzeug-
                // spawn ist ein Werkzeug und standardmaessig aus, der Panzer
                // ist Spielinhalt und standardmaessig an.
                if (RevivalPlugin.CfgTank.Value && Input.GetKeyDown(TankKey()))
                {
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

        static void Spawn(bool panzer)
        {
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

            GameObject car = InstantiateSceneObject(path, pos, rot);
            if (car == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: Photon lieferte null fuer \""
                    + path + "\". Ist der Prefabname richtig?");
                return;
            }

            Prepare(car);
            if (panzer)
            {
                Tank.Umbauen(car);
                Munitionsbeigabe();
            }

            RevivalPlugin.L.LogInfo((panzer ? "Panzer aus \"" : "Fahrzeug \"")
                + name + "\" erzeugt bei " + pos
                + ", Boden \"" + under.name + "\".");
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
        static void Munitionsbeigabe()
        {
            int menge = RevivalPlugin.CfgTankSpawnAmmo.Value;
            if (menge <= 0) return;
            int id = RevivalPlugin.CfgTankAmmoId.Value;
            string meldung;
            bool ok = Admin.GibItem(id, menge, out meldung);
            RevivalPlugin.L.LogInfo("Panzer: Munitionsbeigabe " + menge + "x "
                + id + " - " + meldung);
            if (ok) Turret.Hinweis(menge + " Granaten im Rucksack", 4f);
        }

        /// <summary>
        /// Tank, Zustand und die drei Teile, sonst springt der Motor nicht an.
        /// </summary>
        static void Prepare(GameObject car)
        {
            Type vgsType = AccessTools.TypeByName("VehicleGameSystem");
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
            Type photon = AccessTools.TypeByName("PhotonNetwork");
            if (photon == null) return false;
            MethodInfo getter = AccessTools.PropertyGetter(photon, "isMasterClient");
            if (getter == null) getter = AccessTools.PropertyGetter(photon, "IsMasterClient");
            // Findet sich die Eigenschaft nicht, wird nicht geraten: dann laesst
            // Photon den Aufruf entweder zu oder meldet es selbst.
            if (getter == null) return true;
            return (bool)getter.Invoke(null, null);
        }

        static GameObject InstantiateSceneObject(string path, Vector3 position,
                                                 Quaternion rotation)
        {
            Type photon = AccessTools.TypeByName("PhotonNetwork");
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
                path, position, rotation, (byte)0, null }) as GameObject;
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

        public static bool Flying { get { return _flying; } }
        public static Vector3 Position { get { return _pos; } }
        public static Vector3 Home { get { return _home; } }
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
                    if (_flying) Land(Grund.Abbruch);
                    else Launch();
                }
                if (!_flying) return;

                Steer();
                Move();
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
                CameraOwner.Release(CameraOwner.Drohne);
            }
            RevivalPlugin.L.LogInfo("Drohne beendet (" + GrundText(grund)
                + "), Blick zurueck beim Koerper.");
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
            return FlightTime >= RevivalPlugin.CfgDroneFlightTime.Value;
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
            if (d <= ruhig) return 1f;
            return Mathf.Clamp01(1f - (d - ruhig) / Mathf.Max(1f, weit - ruhig));
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
                Type t = AccessTools.TypeByName("PlayerMovementController");
                if (t == null) return null;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i] as MonoBehaviour;
                    if (mb == null) continue;
                    if (!IsMine(mb)) continue;
                    return mb.transform.root;
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
            }

            public static void EnsureHooked()
            {
                if (_hooked || _failed) return;
                try
                {
                    Type photon = AccessTools.TypeByName("PhotonNetwork");
                    if (photon == null)
                    {
                        _failed = true;
                        RevivalPlugin.L.LogWarning("Drohnennetz: PhotonNetwork nicht "
                            + "gefunden - die Drohne fliegt, aber niemand sonst sieht sie.");
                        return;
                    }
                    _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                    _onEventCall = AccessTools.Field(photon, "OnEventCall");
                    _optType = AccessTools.TypeByName("RaiseEventOptions");
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
                        + Code(Start) + "-" + Code(Ende) + ", Empfang ueber "
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
                    if (art < Start || art > Ende) return;
                    float[] d = inhalt as float[];
                    if (d == null || d.Length < 7) return;

                    Vector3 pos = new Vector3(d[0], d[1], d[2]);
                    Vector3 blick = new Vector3(d[3], d[4], d[5]);

                    if (art == Ende) { Entferne(absender, (int)d[6]); return; }

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

            static void Entferne(int absender, int grund)
            {
                Fremd f;
                if (!_fremde.TryGetValue(absender, out f)) return;
                _fremde.Remove(absender);
                if (f.Src != null) f.Src.Stop();
                if (f.Go != null) UnityEngine.Object.Destroy(f.Go);
                RevivalPlugin.L.LogInfo("Fremde Drohne von Spieler " + absender
                    + " ist weg (" + GrundText(grund) + ").");
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
            /// </summary>
            public static GameObject Bauen()
            {
                GameObject go = new GameObject("NDR_Drone");
                MeshFilter mf = go.AddComponent<MeshFilter>();
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                Mesh mesh = Assets.Load("drone.ndmesh");
                mf.sharedMesh = mesh != null ? mesh : Notnagel();
                mr.sharedMaterial = Werkstoff();
                float s = RevivalPlugin.CfgDroneModelScale.Value;
                go.transform.localScale = new Vector3(s, s, s);
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
            if (!_flying || !RevivalPlugin.CfgDroneOverlay.Value) return;
            try
            {
                if (_dot == null)
                {
                    _dot = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    _dot.SetPixel(0, 0, Color.white);
                    _dot.Apply();
                    _dot.hideFlags = HideFlags.HideAndDontSave;
                }

                float w = Screen.width, h = Screen.height;
                float sig = Signal();
                Color alt = GUI.color;

                if (sig < 1f) Rauschen(w, h, sig);
                Rahmen(w, h, sig);
                Fadenkreuz(w, h);
                Zahlen(w, h, sig);

                GUI.color = alt;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Drohnenanzeige: " + ex);
            }
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
            if (Motorlos()) zeile = "AKKU LEER - SIE FAELLT   " + zeile;
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
                    Type t = AccessTools.TypeByName(teile[0]);
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
}
