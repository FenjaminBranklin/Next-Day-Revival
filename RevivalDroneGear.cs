// Next Day: Survival - Revival Toolkit
//
// Antenna / battery / surveillance-drone rework - the whole system.
//
// A separate file on purpose. build.ps1 now compiles every top-level *.cs beside
// RevivalPlugin.cs into one DLL (it used to build the main file alone), so this
// large new system lives here instead of growing the 19k-line main file. The
// only edits in RevivalPlugin.cs are a handful of seams: the config bind and
// item-table calls, DroneGear.Tick in Update and DroneGear.Draw in OnGUI, a new
// CameraOwner.Aufklaerer slot routed to SurvDrone.LateTick, the FPV launch
// trigger routed through DroneGear.WantFpvLaunch (antenna gate + launch hold),
// and one added term in DroneInputHook that freezes the body while the antenna
// raises, a launch charges, or the surveillance view is active.
//
// What it contains:
//   DroneGear   config, the three new items, and the frame-loop coordination.
//   LaunchHold  the press-and-hold "loading bar" both drones launch through.
//   Antenna     the mast that must be up before ANY drone may launch.
//   SurvDrone   the reusable, battery-powered surveillance drone.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII-only comments and logs. Player-facing strings go through Loc.T, whose
// Russian half is real Cyrillic - this file is therefore UTF-8 (no BOM) and is
// compiled with /codepage:65001 like the main file.

using System;
using System.Globalization;
using System.Reflection;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// Config, item ids and the item-table entries for the drone rework. The
    /// three new items clone donor 2030 (a generic item box) and are gear, not
    /// weapons - the same shape the FPV drone (1163) and the jammer (2054) use.
    ///
    /// The meshes/icons reference EXISTING assets as placeholders (the antenna
    /// borrows the jammer's whip-antenna model, the battery an ammo box, the
    /// surveillance drone the FPV mesh scaled up at runtime). Dedicated
    /// generators can replace them later without touching this table.
    /// </summary>
    public static class DroneGear
    {
        // Fresh ids. items.tsv has nothing in 2055..2057; verify at build time.
        public const int AntennaId = 2055;
        public const int BatteryId = 2056;
        public const int SurveillanceId = 2057;

        // --- Antenna gate
        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<bool> CfgRequireAntenna;
        public static ConfigEntry<float> CfgDeploySeconds;
        public static ConfigEntry<float> CfgAntennaHeight;
        public static ConfigEntry<float> CfgAntennaBack;
        public static ConfigEntry<float> CfgAntennaUp;
        public static ConfigEntry<string> CfgAntennaKey;
        // --- Launch hold: the seconds a drone key is held before it lifts
        public static ConfigEntry<float> CfgLaunchHoldSeconds;
        // --- Surveillance drone
        public static ConfigEntry<bool> CfgSurvEnabled;
        public static ConfigEntry<float> CfgSurvModelScale;
        public static ConfigEntry<float> CfgSurvRange;
        public static ConfigEntry<float> CfgSurvFlightTime;
        public static ConfigEntry<int> CfgSurvHitpoints;
        public static ConfigEntry<bool> CfgSurvRequireBattery;
        public static ConfigEntry<string> CfgSurvKey;

        public static void BindConfig(ConfigFile cfg)
        {
            // .cfg descriptions stay German by project convention - only the
            // admin who opens the file reads them.
            CfgEnabled = cfg.Bind("DroneGear", "Enabled", true,
                "Das Antennen-/Akku-/Aufklaerungsdrohnen-System aktivieren.");
            CfgRequireAntenna = cfg.Bind("DroneGear", "RequireAntenna", true,
                "Eine ausgefahrene Antenne ist Voraussetzung, um IRGENDEINE Drohne "
                + "zu starten (FPV wie Aufklaerung). Aus: alte Sofortstart-Regel.");
            CfgDeploySeconds = cfg.Bind("DroneGear", "DeploySeconds", 20f,
                "Sekunden, die das Ausfahren der Antenne dauert. Waehrenddessen "
                + "steht der Spieler still (Ladebalken, wie beim Beerenpfluecken).");
            CfgAntennaHeight = cfg.Bind("DroneGear", "AntennaHeight", 3.0f,
                "Wie viele Meter die MastSPITZE ueber den KOPF des Spielers "
                + "hinausragt. 3 = die Antenne steht drei Meter ueber dem Kopf. "
                + "Der Mastfuss wird automatisch an der echten Kopfhoehe des "
                + "Spielermodells (Renderer-Bounds) verankert, damit der Mast nie "
                + "mehr im Koerper verschwindet.");
            CfgAntennaBack = cfg.Bind("DroneGear", "AntennaBack", 0.25f,
                "Wie weit hinter dem Spieler (Rucksackseite) der Mastfuss sitzt, in "
                + "Metern. Groesser = weiter hinten. Nur zum Feinjustieren der Optik.");
            CfgAntennaUp = cfg.Bind("DroneGear", "AntennaUp", 0.55f,
                "Wie viele Meter UNTER dem Kopf der Mastfuss ansetzt (Rucksack-/"
                + "Oberkoerperhoehe). 0.55 = knapp unter den Schultern. Nur zum "
                + "Feinjustieren, wo der Mast aus dem Rucksack tritt.");
            CfgAntennaKey = cfg.Bind("DroneGear", "AntennaKey", "H",
                "Taste, um die Mastantenne auszufahren bzw. wieder einzufahren. "
                + "Nur zu Fuss; im Fahrzeug faehrt sie automatisch ein. Ein Druck "
                + "startet das Ausfahren (Ladebalken, Spieler steht still), ein "
                + "weiterer Druck faehrt sie wieder ein.");
            CfgLaunchHoldSeconds = cfg.Bind("DroneGear", "LaunchHoldSeconds", 20f,
                "Sekunden, die die rechte Maustaste gehalten werden muss, bis die "
                + "Drohne in der Hand tatsaechlich abhebt.");

            CfgSurvEnabled = cfg.Bind("DroneGear", "SurveillanceEnabled", true,
                "Die wiederverwendbare Aufklaerungsdrohne bereitstellen.");
            CfgSurvModelScale = cfg.Bind("DroneGear", "SurveillanceModelScale", 12f,
                "Modellgroesse der Aufklaerungsdrohne (viel groesser als die FPV).");
            CfgSurvRange = cfg.Bind("DroneGear", "SurveillanceRange", 1400f,
                "Reichweite der Aufklaerungsdrohne in Metern (groesser als FPV).");
            CfgSurvFlightTime = cfg.Bind("DroneGear", "SurveillanceFlightTime", 240f,
                "Flugzeit der Aufklaerungsdrohne pro Akku in Sekunden.");
            CfgSurvHitpoints = cfg.Bind("DroneGear", "SurveillanceHitpoints", 4,
                "Treffer, die die Aufklaerungsdrohne aushaelt, bevor sie abstuerzt.");
            CfgSurvRequireBattery = cfg.Bind("DroneGear", "SurveillanceRequireBattery", true,
                "Der Start der Aufklaerungsdrohne verbraucht einen Akku aus dem "
                + "Inventar; die Akkuladung ist die Flugzeit.");
            CfgSurvKey = cfg.Bind("DroneGear", "SurveillanceKey", "B",
                "Taste fuer die Aufklaerungsdrohne. Gedrueckt halten startet sie "
                + "(Ladebalken); im Flug wechselt ein Tastendruck zwischen Drohnen- "
                + "und Koerpersicht, langes Halten holt sie zurueck; am Boden hebt "
                + "sie ein Tastendruck in der Naehe wieder auf.");
        }

        // --------------------------------------------------------- coordination

        /// <summary>
        /// The one seam the whole system hangs on the frame loop by. Called from
        /// RevivalPlugin.Update after Drone.Tick. The FPV launch HOLD is polled
        /// from inside Drone.Tick (see <see cref="WantFpvLaunch"/>); everything
        /// else - the antenna raise and the surveillance drone - ticks here.
        /// </summary>
        public static readonly LaunchHold FpvHold = new LaunchHold();

        public static void Tick()
        {
            if (CfgEnabled != null && !CfgEnabled.Value) return;
            Antenna.Tick();
            SurvDrone.Tick();
        }

        /// <summary>
        /// The one seam OnGUI hangs on. Draws the antenna raise bar, whichever
        /// launch hold is running, and the surveillance overlay/prompts.
        /// </summary>
        public static void Draw()
        {
            if (CfgEnabled != null && !CfgEnabled.Value) return;
            DrawGuide();
            Antenna.Draw();
            FpvHold.Draw(Loc.T("Запуск FPV", "FPV launch"));
            SurvDrone.Draw();
        }

        static Texture2D _guidePx;
        static Texture2D GuidePx()
        {
            if (_guidePx == null)
            {
                _guidePx = new Texture2D(1, 1);
                _guidePx.SetPixel(0, 0, Color.white);
                _guidePx.Apply();
            }
            return _guidePx;
        }

        /// <summary>
        /// A small always-on panel that WALKS THE PLAYER THROUGH the antenna ->
        /// launch flow with the ACTUAL configured keys and the live state. It is
        /// the answer to "there is simply no way to figure out how to start it":
        /// nothing about the antenna gate or the hold-to-launch is discoverable
        /// without it. Shown only on foot, only while NOTHING of ours is airborne
        /// (the flight OSDs take over then), and only while the player carries at
        /// least one relevant piece of gear - so it never clutters an unrelated
        /// session. Every line is bilingual (Loc.T) like the rest of the HUD.
        /// </summary>
        static void DrawGuide()
        {
            try
            {
                if (Drone.Flying || SurvDrone.Flying) return;

                bool haveAnt = Antenna.CarryingAntenna;
                int fpvId = RevivalPlugin.CfgDroneItemId == null
                    ? 1163 : RevivalPlugin.CfgDroneItemId.Value;
                bool haveFpv = Turret.HasItem(fpvId);
                bool haveSurv = CfgSurvEnabled != null && CfgSurvEnabled.Value
                    && Turret.HasItem(SurveillanceId);
                if (!haveAnt && !haveFpv && !haveSurv) return;

                bool inVeh = Antenna.PlayerInVehicle;
                bool up = Antenna.Up;
                bool deploying = Antenna.Deploying;

                string antKey = Antenna.KeyLabel;
                string fpvKey = RevivalPlugin.CfgDroneKey == null
                    ? "G" : RevivalPlugin.CfgDroneKey.Value;
                string survKey = CfgSurvKey == null ? "B" : CfgSurvKey.Value;

                // Build the lines. Each is {text, tint}.
                List<string> lines = new List<string>();
                List<Color> tints = new List<Color>();
                Color ok = new Color(0.55f, 0.95f, 0.6f, 0.97f);   // ready / done
                Color todo = new Color(1f, 0.85f, 0.35f, 0.97f);   // action needed
                Color dim = new Color(0.72f, 0.74f, 0.78f, 0.9f);  // not yet / info

                // Antenna line.
                if (inVeh)
                {
                    lines.Add(Loc.T("В машине антенну развернуть нельзя - выйди",
                                    "Antenna cannot deploy in a vehicle - get out"));
                    tints.Add(dim);
                }
                else if (!haveAnt)
                {
                    lines.Add(Loc.T("Нужна мачтовая антенна в рюкзаке",
                                    "Need the mast antenna in the backpack"));
                    tints.Add(todo);
                }
                else if (deploying)
                {
                    lines.Add(Loc.T("Антенна разворачивается...",
                                    "Antenna raising..."));
                    tints.Add(todo);
                }
                else if (up)
                {
                    lines.Add(Loc.T("[" + antKey + "] Антенна поднята - готова",
                                    "[" + antKey + "] Antenna up - ready"));
                    tints.Add(ok);
                }
                else
                {
                    lines.Add(Loc.T("[" + antKey + "] Поднять антенну (стой на месте)",
                                    "[" + antKey + "] Raise antenna (stand still)"));
                    tints.Add(todo);
                }

                // FPV launch line - only while carrying an FPV drone.
                if (haveFpv)
                {
                    if (up)
                    {
                        lines.Add(Loc.T("[" + fpvKey + "] держать - пуск FPV-дрона",
                                        "[" + fpvKey + "] hold - launch FPV drone"));
                        tints.Add(ok);
                    }
                    else
                    {
                        lines.Add(Loc.T("[" + fpvKey + "] FPV-дрон - нужна антенна",
                                        "[" + fpvKey + "] FPV drone - needs antenna"));
                        tints.Add(dim);
                    }
                }

                // Surveillance launch line - only while carrying the recon drone.
                if (haveSurv)
                {
                    if (up)
                    {
                        lines.Add(Loc.T("[" + survKey + "] держать - пуск разведдрона",
                                        "[" + survKey + "] hold - launch recon drone"));
                        tints.Add(ok);
                    }
                    else
                    {
                        lines.Add(Loc.T("[" + survKey + "] разведдрон - нужна антенна",
                                        "[" + survKey + "] recon drone - needs antenna"));
                        tints.Add(dim);
                    }
                }

                // Layout: a compact box on the left edge, below any top OSD.
                float pad = 8f;
                float lh = 20f;
                float w = 320f;
                float h = pad * 2f + lh * (lines.Count + 1);
                float x = 16f;
                float y = Screen.height * 0.32f;

                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(x - 4f, y - 4f, w + 8f, h + 8f), GuidePx());

                GUI.color = new Color(0.75f, 0.85f, 1f, 0.97f);
                GUI.Label(new Rect(x + pad, y + pad, w - pad * 2f, lh),
                          Loc.T("ДРОН - как запустить", "DRONE - how to launch"));

                for (int i = 0; i < lines.Count; i++)
                {
                    GUI.color = tints[i];
                    GUI.Label(new Rect(x + pad, y + pad + lh * (i + 1), w - pad * 2f, lh),
                              lines[i]);
                }
                GUI.color = old;
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Drone guide: " + ex); }
        }

        /// <summary>
        /// True while a launch hold is charging - the body must stand still then,
        /// exactly as it does while a drone is flown. Read by DroneInputHook.
        /// </summary>
        public static bool LaunchBusy
        {
            get { return FpvHold.Active || SurvDrone.LaunchBusy; }
        }

        /// <summary>
        /// The FPV drone's launch trigger, replacing the old instant key press.
        /// Called every frame from Drone.Tick while nothing of ours flies. It
        /// returns true on the single frame the hold completes; the antenna must
        /// be up first, or a hint says why nothing happened.
        /// </summary>
        public static bool WantFpvLaunch(KeyCode key)
        {
            // System switched off entirely: the FPV drone keeps its old instant
            // tap - no antenna, no hold.
            if (CfgEnabled != null && !CfgEnabled.Value)
                return Input.GetKeyDown(key);

            bool allowed = Antenna.LaunchAllowed();
            if (!allowed && Input.GetKeyDown(key))
            {
                Antenna.LaunchDeniedHint();
                return false;
            }
            return FpvHold.Poll(key, allowed);
        }

        /// <summary>
        /// Removes the collider a CreatePrimitive adds, without naming the
        /// UnityEngine.Collider TYPE - the plugin does not reference
        /// PhysicsModule.dll and reaches Physics/Collider by reflection
        /// everywhere (see Turret.RaycastObject). A colliderless model neither
        /// shoves the world in the air nor blocks the pilot on the ground.
        /// </summary>
        public static void StripCollider(GameObject go)
        {
            if (go == null) return;
            try
            {
                Type ct = RevivalPlugin.TypeByName("UnityEngine.Collider");
                if (ct == null) return;
                Component c = go.GetComponent(ct);
                if (c != null) UnityEngine.Object.Destroy(c);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("StripCollider: " + ex.Message);
            }
        }

        /// <summary>Appends the three new items to the shared item table.</summary>
        public static void AddItems(List<ItemDef> items)
        {
            // Antenna (2055) - placeholder art: the jammer's whip-antenna model.
            items.Add(new ItemDef(
                AntennaId, 2030, false,
                "Мачтовая антенна", "Mast antenna",
                "Складная мачтовая антенна на раме рюкзака. Разворачивается на "
                + "месте примерно за двадцать секунд и поднимается на несколько "
                + "метров вверх. Без поднятой антенны ни один дрон - ни FPV, ни "
                + "разведывательный - не взлетит. В машине развернуть нельзя.",
                "A folding mast antenna on a backpack frame. It deploys in place "
                + "over about twenty seconds and rises a few metres up. With no "
                + "raised antenna no drone - FPV or surveillance - will launch. "
                + "It cannot be deployed inside a vehicle.",
                "jammer.ndmesh", "jammer_diffuse.png", "jammer_normal.png",
                "jammer_icon.png", null,
                1, 0, 6.0f));

            // Battery (2056) - placeholder art: an ammo box.
            items.Add(new ItemDef(
                BatteryId, 2030, false,
                "Аккумулятор дрона", "Drone battery",
                "Тяговый литиевый аккумулятор для разведывательного дрона. Один "
                + "пуск - один аккумулятор; заряд определяет время полёта. FPV-дрон "
                + "питается от собственной встроенной батареи и в нём не нуждается.",
                "A lithium traction battery for the surveillance drone. One launch "
                + "spends one battery; the charge sets the flight time. The FPV "
                + "drone runs off its own built-in cell and does not need one.",
                "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png",
                "ammo50_icon.png", null,
                1, 0, 2.2f));

            // Surveillance drone (2057) - placeholder art: the FPV mesh (shown
            // large in the air via ModelScale; the ground item stays small).
            items.Add(new ItemDef(
                SurveillanceId, 2030, false,
                "Разведывательный дрон", "Surveillance drone",
                "Большой многовинтовой разведывательный дрон с увеличенной "
                + "дальностью. Многоразовый: можно ненадолго выйти из вида дрона и "
                + "вернуться, он не упадёт. При падении опускается на землю как "
                + "предмет - подойдите и подберите его. Питается от аккумуляторов.",
                "A large multirotor surveillance drone with extended range. "
                + "Reusable: you can briefly leave the drone view and return "
                + "without it crashing. When it does crash it settles to the "
                + "ground as an item - walk up and pick it back up. Battery "
                + "powered.",
                "drone.ndmesh", "drone_diffuse.png", "drone_normal.png",
                "drone_icon.png", null,
                1, 0, 3.5f));
        }
    }

    /// <summary>
    /// A press-and-hold that shows a loading bar and fires once when the hold
    /// completes - the "twenty seconds until it actually lifts" the antenna and
    /// both drones share. Only one instance charges at a time in practice: a
    /// player holds one key.
    /// </summary>
    public sealed class LaunchHold
    {
        bool _active;
        float _start;
        static Texture2D _px;

        public bool Active { get { return _active; } }

        /// <summary>
        /// Keep charging while the key is held and the gate allows it; return
        /// true the frame the configured hold time is reached, and reset so it
        /// fires only once per hold.
        /// </summary>
        public bool Poll(KeyCode key, bool gateOk)
        {
            if (!gateOk || !Input.GetKey(key)) { _active = false; return false; }

            float len = Len();
            if (!_active) { _active = true; _start = Time.time; }
            if (Time.time - _start >= len) { _active = false; return true; }
            return false;
        }

        public void Cancel() { _active = false; }

        static float Len()
        {
            return DroneGear.CfgLaunchHoldSeconds == null
                ? 20f : Mathf.Max(0.5f, DroneGear.CfgLaunchHoldSeconds.Value);
        }

        public void Draw(string label)
        {
            if (!_active) return;
            try
            {
                float len = Len();
                float t = Mathf.Clamp01((Time.time - _start) / len);
                float rest = Mathf.Max(0f, len - (Time.time - _start));

                float w = 300f, h = 22f;
                float x = (Screen.width - w) * 0.5f;
                float y = Screen.height * 0.66f;

                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(x - 2f, y - 2f, w + 4f, h + 4f), Px());
                GUI.color = new Color(0.12f, 0.12f, 0.12f, 0.9f);
                GUI.DrawTexture(new Rect(x, y, w, h), Px());
                GUI.color = new Color(0.30f, 0.72f, 0.95f, 0.95f);
                GUI.DrawTexture(new Rect(x, y, w * t, h), Px());
                GUI.color = Color.white;
                GUI.Label(new Rect(x, y - 22f, w, 20f),
                    label + "  " + Mathf.CeilToInt(rest) + " s");
                GUI.color = old;
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Launch bar: " + ex); }
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
    }

    /// <summary>
    /// The mast antenna: a mod-tracked deploy state that gates every drone
    /// launch. Pressing the antenna key (DroneGear/AntennaKey, default H) while
    /// carrying an antenna on foot raises it over ~20 seconds - the player is
    /// frozen (DroneInputHook reads <see cref="Deploying"/>) and a load bar
    /// counts down - after which <see cref="Up"/> is true and a telescopic grey
    /// mast stands up out of the backpack. Pressing the key again lowers it.
    /// Boarding a vehicle, or losing the antenna, retracts it automatically.
    ///
    /// Trigger note: the game has no item-use/right-click hook, so deploy is a
    /// dedicated key rather than a right-click on the item. The player asked for
    /// a deliberate deploy (no surprise freeze the moment the antenna is picked
    /// up), so raising is opt-in via the key.
    ///
    /// The mast is built from primitives at runtime (a telescopic stack of grey
    /// cylinders that slide up as it deploys), not a mesh asset - a dedicated
    /// .ndmesh is a later Codex asset job. It is PARENTED to the live player
    /// body (re-anchored every frame, so a respawn or a stale transform can
    /// never strand it at world origin - the cause of the "completely invisible"
    /// report) and forced upright in world space.
    ///
    /// PLACEMENT (fixes "the mast sits inside the body"): the foot is anchored
    /// off the player's REAL head height, read from the model's renderer bounds
    /// each frame (<see cref="PlayerHeadTop"/>), NOT from a fixed offset above an
    /// unknown transform origin - the earlier 1.1 m-above-origin guess left the
    /// short mast buried in the character silhouette. The foot sits AntennaUp
    /// metres below that head, on the backpack side (AntennaBack behind), and the
    /// telescope extends so the TIP clears the head by AntennaHeight metres - so
    /// the antenna always rises several metres clear above the head regardless of
    /// where the player transform's origin actually is.
    /// </summary>
    public static class Antenna
    {
        public static bool Up;         // antenna raised, drones may launch
        public static bool Deploying;  // raise in progress; freezes the player

        static float _start;
        static float _len;
        static float _end;

        // The telescopic mast: a root placed at the player's back and a stack of
        // grey cylinder segments (thinner towards the top) that slide up out of
        // one another as the antenna deploys. Segment 0 is the fat base tube.
        const int Segments = 4;
        static GameObject _root;
        static GameObject[] _seg;
        static Transform _pilot;
        static Material _grey;

        // Deploy key, parsed once from the config string.
        static KeyCode _key = KeyCode.H;
        static bool _keyParsed;

        // 0.5 s cache so we do not thrash Turret.HasItem's single-slot cache
        // (the jammer already polls it every frame).
        static float _haveUntil;
        static bool _haveResult;
        static float _vehUntil;
        static bool _vehResult;

        static Texture2D _px;

        static bool GateOn
        {
            get
            {
                return DroneGear.CfgEnabled != null && DroneGear.CfgEnabled.Value
                    && DroneGear.CfgRequireAntenna != null
                    && DroneGear.CfgRequireAntenna.Value;
            }
        }

        /// <summary>
        /// May a drone launch right now? With the gate off no antenna is needed
        /// (the old instant-launch rule); with it on the antenna must be fully
        /// up - deploying is not enough.
        /// </summary>
        public static bool LaunchAllowed()
        {
            if (!GateOn) return true;
            return Up;
        }

        /// <summary>Explains, once per press, why a launch did nothing.</summary>
        public static void LaunchDeniedHint()
        {
            if (Deploying)
                Turret.Hinweis(Loc.T("Антенна ещё разворачивается...",
                                     "Antenna still deploying..."), 2.5f);
            else if (!HaveAntenna())
                Turret.Hinweis(Loc.T("Нужна мачтовая антенна - без неё дрон не взлетит",
                                     "Need the mast antenna - no drone launches without it"), 3f);
            else if (InVehicle())
                Turret.Hinweis(Loc.T("В машине антенну развернуть нельзя",
                                     "The antenna cannot be deployed inside a vehicle"), 3f);
            else
                Turret.Hinweis(Loc.T("Сначала подними антенну", "Raise the antenna first"), 2.5f);
        }

        public static void Tick()
        {
            try { TickCore(); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Antenna tick: " + ex);
                Deploying = false;
            }
        }

        static void TickCore()
        {
            if (!GateOn)
            {
                // Gate disabled: no antenna needed, keep no state around.
                if (Deploying || Up) Retract("gate off");
                return;
            }

            bool press = Input.GetKeyDown(DeployKey());

            if (InVehicle())
            {
                if (Deploying || Up) Retract("boarded a vehicle");
                if (press)
                    Turret.Hinweis(Loc.T("В машине антенну развернуть нельзя",
                                         "The antenna cannot be deployed inside a vehicle"), 2.5f);
                return;
            }

            if (!HaveAntenna())
            {
                if (Deploying || Up) Retract("antenna gone");
                if (press)
                    Turret.Hinweis(Loc.T("Нужна мачтовая антенна в рюкзаке",
                                         "Need the mast antenna in the pack"), 2.5f);
                return;
            }

            // The key is the deliberate deploy: press to raise, press again to
            // lower (or to cancel a raise in progress).
            if (press)
            {
                if (Up || Deploying) Retract("key");
                else Begin();
            }

            if (Deploying)
            {
                float now = Time.time;
                if (now >= _end) Finish();
                else Grow((now - _start) / _len);
            }
            else if (Up)
            {
                Hold();
            }
        }

        static KeyCode DeployKey()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            string s = DroneGear.CfgAntennaKey == null ? "H" : DroneGear.CfgAntennaKey.Value;
            try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
            catch { _key = KeyCode.H; }
            return _key;
        }

        static void Begin()
        {
            Deploying = true;
            _start = Time.time;
            _len = Mathf.Max(0.5f, DroneGear.CfgDeploySeconds.Value);
            _end = _start + _len;
            BuildMast();
            Grow(0f);
            Turret.Hinweis(Loc.T("Разворачиваю антенну...", "Raising antenna..."), 3f);
            RevivalPlugin.L.LogInfo("Antenna: deploy started (" + _len + " s).");
        }

        static void Finish()
        {
            Deploying = false;
            Up = true;
            Grow(1f);
            Turret.Hinweis(Loc.T("Антенна поднята - дрон готов к пуску",
                                 "Antenna up - drone ready to launch"), 3f);
            Vector3 foot = _root == null ? Vector3.zero : _root.transform.position;
            float head; bool haveHead = PlayerHeadTop(out head);
            float tipY = foot.y + Mathf.Max(0.3f,
                (DroneGear.CfgAntennaUp == null ? 0.55f : DroneGear.CfgAntennaUp.Value)
                + (DroneGear.CfgAntennaHeight == null ? 3.0f : DroneGear.CfgAntennaHeight.Value));
            RevivalPlugin.L.LogInfo("Antenna: up - foot world " + foot.ToString("F1")
                + ", tip Y=" + tipY.ToString("F1")
                + ", head Y=" + (haveHead ? head.ToString("F1") : "n/a")
                + " (tip clears head by ~"
                + (haveHead ? (tipY - head).ToString("F1") : "?") + " m, segments="
                + (_seg == null ? 0 : _seg.Length) + ").");
        }

        static void Retract(string why)
        {
            bool was = Up || Deploying;
            Up = false;
            Deploying = false;
            DestroyMast();
            if (was)
                RevivalPlugin.L.LogInfo("Antenna: retracted (" + why + ").");
        }

        // ------------------------------------------------------------- visual

        // Segment radii, fat base to thin tip. A real telescopic whip tapers,
        // so thinner tubes appear to slide out of thicker ones.
        static readonly float[] SegRadius = new float[] { 0.060f, 0.046f, 0.034f, 0.024f };

        static void BuildMast()
        {
            DestroyMast();
            try
            {
                EnsurePilot();

                _root = new GameObject("NDR_Antenna");
                _seg = new GameObject[Segments];
                // Parent to the player body so the mast is guaranteed to sit
                // exactly where the body is - the way the old (visible) cylinder
                // did. A world-space object that only follows a cached transform
                // gets stranded the moment that transform goes stale (respawn),
                // which is how the mast ended up "completely invisible".
                if (_pilot != null) _root.transform.SetParent(_pilot, true);
                Material g = Grey();
                for (int i = 0; i < Segments; i++)
                {
                    GameObject c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    c.name = "NDR_AntennaSeg" + i;
                    DroneGear.StripCollider(c);
                    c.transform.SetParent(_root.transform, false);
                    if (g != null)
                    {
                        Renderer r = c.GetComponent<Renderer>();
                        if (r != null) r.sharedMaterial = g;
                    }
                    _seg[i] = c;
                }
                Layout(0f);
                RevivalPlugin.L.LogInfo("Antenna: mast built (pilot="
                    + (_pilot == null ? "NONE" : _pilot.name) + ").");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Antenna: mast build failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Refreshes <see cref="_pilot"/> to the LIVE local-player transform. A
        /// destroyed Unity object compares == null, so a respawn (or a late
        /// first frame where the player is not spawned yet) is caught here and
        /// the mast re-anchors instead of being left behind at its last spot.
        /// </summary>
        static void EnsurePilot()
        {
            if (_pilot != null) return;
            GameObject body = MapTools.LocalPlayer();
            _pilot = body == null ? null : body.transform;
            // Re-adopt an orphaned mast onto the fresh body.
            if (_pilot != null && _root != null && _root.transform.parent != _pilot)
                _root.transform.SetParent(_pilot, true);
        }

        /// <summary>
        /// Places the mast root at the player's back, forced upright, and lays
        /// the telescopic segments so that a fraction `t` of the configured
        /// height is extended. Segments fill from the bottom, so the thin upper
        /// tubes only rise once the fat lower ones are out - a telescope opening.
        /// </summary>
        static void Layout(float t)
        {
            if (_root == null) return;

            // Anchor: behind the player (backpack side) and forced upright, no
            // matter how the body leans or animates. Re-fetch the body every
            // frame; if there is no local player right now, hide the mast rather
            // than leave a stray one floating at world origin.
            EnsurePilot();
            if (_pilot == null) { DestroyMast(); return; }

            float back = DroneGear.CfgAntennaBack == null ? 0.25f : DroneGear.CfgAntennaBack.Value;
            float drop = DroneGear.CfgAntennaUp == null ? 0.55f : DroneGear.CfgAntennaUp.Value;
            float above = DroneGear.CfgAntennaHeight == null ? 3.0f : DroneGear.CfgAntennaHeight.Value;

            Vector3 root = _pilot.position;
            Vector3 fwd = _pilot.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward; else fwd.Normalize();

            // Real head height from the player's rendered bounds, so the mast is
            // placed relative to the head instead of a fixed guess above an
            // unknown origin (which buried it in the body). Clamp to a sane band
            // above the root so a raised weapon or an animation spike in the
            // bounds cannot fling the anchor.
            float headTop;
            if (!PlayerHeadTop(out headTop)) headTop = root.y + 1.75f;
            headTop = Mathf.Clamp(headTop, root.y + 1.2f, root.y + 2.2f);

            // Foot sits `drop` below the head on the backpack side; the fully
            // extended tip reaches `above` metres over the head.
            float footY = headTop - Mathf.Max(0f, drop);
            _root.transform.position = new Vector3(root.x, footY, root.z) - fwd * back;
            _root.transform.rotation = Quaternion.identity;

            // Physical mast length foot->tip: the drop below the head plus the
            // clearance above it, so at t=1 the tip is at headTop + above.
            float total = Mathf.Max(0.3f, Mathf.Max(0f, drop) + Mathf.Max(0.3f, above));
            float segMax = total / Segments;
            float h = Mathf.Clamp01(t) * total;

            float bottom = 0f;
            for (int i = 0; i < Segments; i++)
            {
                GameObject c = _seg == null ? null : _seg[i];
                if (c == null) continue;

                // How much of THIS segment is out: it starts extending only once
                // everything below it is fully out.
                float len = Mathf.Clamp(h - i * segMax, 0f, segMax);
                bool shown = len > 0.001f;
                // A collapsed segment shows a short stub at the current tip so
                // the nested tubes read as a stowed telescope, not a gap.
                if (!shown) len = 0.02f;

                float r = SegRadius[Mathf.Min(i, SegRadius.Length - 1)];
                // Unity cylinder: 2 m tall, 1 m across at scale 1, pivot centre.
                c.transform.localRotation = Quaternion.identity;
                c.transform.localScale = new Vector3(r * 2f, len * 0.5f, r * 2f);
                c.transform.localPosition = new Vector3(0f, bottom + len * 0.5f, 0f);

                bottom += shown ? len : 0f;
            }
        }

        static void Grow(float t) { Layout(t); }

        /// <summary>
        /// World-space Y of the top of the player model, from the union of its
        /// renderer bounds - i.e. the real head height, whatever the transform
        /// origin is. Our own mast segments (named "NDR_...") are skipped so the
        /// growing antenna cannot feed its own tip back into the anchor. Returns
        /// false if the body has no usable renderer yet (caller falls back).
        /// </summary>
        static bool PlayerHeadTop(out float y)
        {
            y = 0f;
            if (_pilot == null) return false;
            try
            {
                Renderer[] rs = _pilot.GetComponentsInChildren<Renderer>();
                if (rs == null) return false;
                bool any = false;
                float top = float.NegativeInfinity;
                for (int i = 0; i < rs.Length; i++)
                {
                    Renderer r = rs[i];
                    if (r == null || !r.enabled) continue;
                    if (r.gameObject != null && r.gameObject.name != null
                        && r.gameObject.name.StartsWith("NDR_")) continue;
                    Bounds b = r.bounds;
                    if (b.size.y <= 0.001f) continue;
                    if (b.max.y > top) top = b.max.y;
                    any = true;
                }
                if (!any) return false;
                y = top;
                return true;
            }
            catch { return false; }
        }

        static void Hold()
        {
            // Keep the mast alive and following the (moving) player each frame;
            // if the body was rebuilt (respawn) and the mast is gone, rebuild.
            if (_root == null) { BuildMast(); Layout(1f); }
            else Layout(1f);
        }

        static void DestroyMast()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; }
            _seg = null;
            _pilot = null;
        }

        /// <summary>
        /// A grey metal material that is guaranteed to draw. Shader.Find only
        /// returns shaders built into the game, so it falls back to a scene
        /// material's shader - a renderer with no material draws magenta.
        /// </summary>
        static Material Grey()
        {
            if (_grey != null) return _grey;
            Shader sh = Shader.Find("Standard");
            if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
            if (sh == null) sh = Shader.Find("Diffuse");
            if (sh == null)
            {
                UnityEngine.Object[] all =
                    UnityEngine.Object.FindObjectsOfType(typeof(Renderer));
                for (int i = 0; i < all.Length; i++)
                {
                    Renderer r = all[i] as Renderer;
                    if (r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null)
                    { sh = r.sharedMaterial.shader; break; }
                }
            }
            if (sh == null)
            {
                RevivalPlugin.L.LogWarning("Antenna: no shader found - mast may draw magenta.");
                return null;
            }
            _grey = new Material(sh);
            _grey.name = "NDR_Antenna_Material";
            _grey.color = new Color(0.55f, 0.57f, 0.60f, 1f); // brushed grey
            // Standard shader: nudge towards a metal read if the params exist.
            try { if (_grey.HasProperty("_Metallic")) _grey.SetFloat("_Metallic", 0.6f); } catch {}
            try { if (_grey.HasProperty("_Glossiness")) _grey.SetFloat("_Glossiness", 0.35f); } catch {}
            return _grey;
        }

        // -------------------------------------------------------------- bar

        public static void Draw()
        {
            if (!Deploying) return;
            try
            {
                float t = Mathf.Clamp01((Time.time - _start) / _len);
                float rest = Mathf.Max(0f, _end - Time.time);

                float w = 260f, h = 20f;
                float x = (Screen.width - w) * 0.5f;
                float y = Screen.height * 0.62f;

                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(x - 2f, y - 2f, w + 4f, h + 4f), Px());
                GUI.color = new Color(0.12f, 0.12f, 0.12f, 0.9f);
                GUI.DrawTexture(new Rect(x, y, w, h), Px());
                GUI.color = new Color(0.95f, 0.55f, 0.12f, 0.95f);
                GUI.DrawTexture(new Rect(x, y, w * t, h), Px());
                GUI.color = Color.white;
                GUI.Label(new Rect(x, y - 22f, w, 20f),
                    Loc.T("Антенна ", "Antenna ") + Mathf.CeilToInt(rest) + " s");
                GUI.color = old;
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Antenna draw: " + ex); }
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

        // ---------------------------------------------------------- helpers

        static bool HaveAntenna()
        {
            if (Time.time < _haveUntil) return _haveResult;
            _haveResult = Turret.HasItem(DroneGear.AntennaId);
            _haveUntil = Time.time + 0.5f;
            return _haveResult;
        }

        static bool InVehicle()
        {
            if (Time.time < _vehUntil) return _vehResult;
            bool inv = false;
            try
            {
                Component[] all = VehicleScan.All();   // shared cached scan
                for (int i = 0; i < all.Length; i++)
                {
                    FieldInfo f = AccessTools.Field(all[i].GetType(),
                                                    "_localPlayerPassengerId");
                    if (f == null) continue;
                    object v = f.GetValue(all[i]);
                    if (v is int && (int)v >= 0) { inv = true; break; }
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Antenna: vehicle check: " + ex.Message);
            }
            _vehResult = inv;
            _vehUntil = Time.time + 0.5f;
            return inv;
        }

        /// <summary>Reachable from SurvDrone, which shares the same gate.</summary>
        internal static bool GateActive { get { return GateOn; } }

        /// <summary>The on-screen guide reads these to describe the current
        /// state without duplicating the cached item/vehicle probes.</summary>
        internal static bool CarryingAntenna { get { return HaveAntenna(); } }
        internal static bool PlayerInVehicle { get { return InVehicle(); } }

        /// <summary>The deploy key as a short display string for the guide.</summary>
        internal static string KeyLabel { get { return DeployKey().ToString(); } }
    }

    /// <summary>
    /// The reusable surveillance drone: big, long-ranged, battery powered, and
    /// - unlike the FPV drone - it is not consumed by flying. Its whole point is
    /// a lifecycle the FPV drone deliberately does not have, so it is a separate
    /// self-contained system rather than a mode bolted onto Drone: keeping the
    /// two apart means this task cannot break the shipped FPV combat drone.
    ///
    ///   launch    hold the surveillance key (antenna up, a battery and the drone
    ///             in the pack). A battery and the drone item are spent; the drone
    ///             lifts into view. The battery charge is the flight time.
    ///   two views a tap toggles between the drone's eyes and the pilot's body.
    ///             OUT of the drone view it holds position and does NOT crash -
    ///             that is the "briefly step out and come back" the user asked
    ///             for. The body may walk while the drone hovers.
    ///   recall    holding the key in flight brings it straight down where it is.
    ///   crash     an empty battery drops it out of the sky; either way it does
    ///             not detonate - it settles to the ground AS AN ITEM.
    ///   pick up   walk up to the grounded drone and tap the key: it goes back
    ///             into the backpack, ready for another battery.
    ///
    /// Visibility is LOCAL, like the FPV drone's own picture of foreign drones:
    /// other players do not yet see this drone or its grounded wreck. A networked
    /// version is a piece of work of its own and is noted as deferred.
    /// </summary>
    public static class SurvDrone
    {
        // A steadier, heavier machine than the FPV racer. Fixed on purpose: the
        // user asked for range/size/battery to differ, not the handling, and
        // fewer knobs is fewer ways for an installed config to drift.
        const float Thrust = 22f;
        const float SideThrust = 15f;
        const float Lift = 18f;
        const float Drag = 1.05f;
        const float MaxSpeed = 26f;
        const float ArmSeconds = 0.5f;
        const float SafeRadius = 3.0f;
        const float PickupRange = 3.5f;

        static bool _flying;
        static bool _viewing;
        static Vector3 _pos;
        static Vector3 _vel;
        static float _yaw;
        static float _pitch;
        static float _start;
        static float _end;          // Time.time when the battery is empty
        static float _armed;
        static Transform _pilotRoot;
        static GameObject _model;
        static Renderer[] _modelRenderers;  // cached so visibility toggles allocate nothing
        static GameObject _wreck;
        static Vector3 _wreckAt;
        static float _nextHeight;
        static float _height = -1f;
        static float _holdKeyDown = -1f;    // realtime the key went down in flight
        static float _markFlash;            // Time.time until the "marked" flash fades
        static KeyCode _key = KeyCode.B;
        static bool _keyParsed;
        static Texture2D _px;

        public static readonly LaunchHold Hold = new LaunchHold();

        public static bool Flying { get { return _flying; } }
        public static bool Viewing { get { return _flying && _viewing; } }
        public static bool LaunchBusy { get { return Hold.Active; } }

        static bool Enabled
        {
            get
            {
                return DroneGear.CfgEnabled != null && DroneGear.CfgEnabled.Value
                    && DroneGear.CfgSurvEnabled != null && DroneGear.CfgSurvEnabled.Value;
            }
        }

        public static void Tick()
        {
            if (!Enabled) return;
            try { TickCore(); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("SurvDrone tick: " + ex);
                End("error", false);
            }
        }

        static void TickCore()
        {
            KeyCode k = Key();

            if (_flying)
            {
                Fly(k);
                return;
            }

            // On the ground as a grounded item: pick it back up.
            if (_wreck != null)
            {
                if (Input.GetKeyDown(k) && NearWreck()) PickUp();
                return;
            }

            // Idle: hold the key to launch. The antenna must be up and the drone
            // (and a battery) must be in the pack - checked before the bar even
            // starts, so a 20 s hold cannot end in a bare "nothing happened".
            bool antenna = Antenna.LaunchAllowed();
            bool needBat = DroneGear.CfgSurvRequireBattery == null
                        || DroneGear.CfgSurvRequireBattery.Value;
            bool haveDrone = Turret.HasItem(DroneGear.SurveillanceId);
            bool haveBat = !needBat || Turret.HasItem(DroneGear.BatteryId);
            if (Input.GetKeyDown(k))
            {
                if (!antenna) Antenna.LaunchDeniedHint();
                else if (!haveDrone)
                    Turret.Hinweis(Loc.T("Разведдрона нет в рюкзаке",
                                         "No surveillance drone in the backpack"), 3f);
                else if (!haveBat)
                    Turret.Hinweis(Loc.T("Нет аккумулятора для разведдрона",
                                         "No battery for the surveillance drone"), 3f);
            }
            if (Hold.Poll(k, antenna && haveDrone && haveBat)) Launch();
        }

        // ------------------------------------------------------------- launch

        static void Launch()
        {
            if (!CameraOwner.Free)
            {
                Turret.Hinweis(Loc.T("Обзор занят - сначала выйди из другого вида",
                                     "The view is taken - leave the other view first"), 2.5f);
                return;
            }

            bool needBat = DroneGear.CfgSurvRequireBattery == null
                        || DroneGear.CfgSurvRequireBattery.Value;
            if (needBat && !Turret.HasItem(DroneGear.BatteryId))
            {
                Turret.Hinweis(Loc.T("Нет аккумулятора для разведдрона",
                                     "No battery for the surveillance drone"), 3f);
                return;
            }
            if (!Turret.HasItem(DroneGear.SurveillanceId))
            {
                Turret.Hinweis(Loc.T("Разведдрона нет в рюкзаке",
                                     "No surveillance drone in the backpack"), 3f);
                return;
            }

            Camera cam = CameraOwner.ViewCamera();
            if (cam == null) { RevivalPlugin.L.LogWarning("SurvDrone: no camera."); return; }

            // Take the drone (and a battery) out FIRST, then the view - the same
            // order the FPV drone uses so an empty pack cannot flash the screen.
            if (!Turret.TakeItem(DroneGear.SurveillanceId, "Aufklaerungsdrohne")) return;
            if (needBat && !Turret.TakeItem(DroneGear.BatteryId, "Akku"))
            {
                // The drone is already out; put it back so nothing is lost.
                string back; Admin.GibItem(DroneGear.SurveillanceId, 1, out back);
                return;
            }

            if (!CameraOwner.Request(CameraOwner.Aufklaerer, true, "Aufklaerungsdrohne")) return;

            Vector3 f = cam.transform.forward;
            _pos = cam.transform.position + f * 2.0f + Vector3.up * 0.5f;
            _vel = f * 4f;
            _yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            _pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            _pilotRoot = PilotRoot();

            float charge = DroneGear.CfgSurvFlightTime == null
                ? 240f : Mathf.Max(5f, DroneGear.CfgSurvFlightTime.Value);
            _flying = true;
            _viewing = true;
            _start = Time.time;
            _end = _start + charge;
            _armed = _start + ArmSeconds;
            _holdKeyDown = -1f;
            BuildModel(false);

            int hp = DroneGear.CfgSurvHitpoints == null ? 4 : DroneGear.CfgSurvHitpoints.Value;
            RevivalPlugin.L.LogInfo("SurvDrone launched, charge " + charge + " s, range "
                + Range() + " m, hitpoints " + hp + " (reserved for a networked version).");
            Turret.Hinweis(Loc.T("Разведдрон в воздухе", "Surveillance drone airborne"), 2.5f);
        }

        // -------------------------------------------------------------- flight

        static void Fly(KeyCode k)
        {
            // Key: tap toggles the view, a long hold recalls the drone.
            if (Input.GetKeyDown(k)) _holdKeyDown = Time.time;
            if (Input.GetKey(k) && _holdKeyDown > 0f
                && Time.time - _holdKeyDown >= 0.6f)
            {
                _holdKeyDown = -1f;
                Recall();
                return;
            }
            if (Input.GetKeyUp(k) && _holdKeyDown > 0f)
            {
                _holdKeyDown = -1f;
                if (_viewing) LeaveView();
                else EnterView();
            }

            // Left-click while looking through the drone drops a marker on the
            // player's own map at the spot the drone is over. The recon drone
            // never fires, so the left button is free for this.
            if (_viewing && Input.GetMouseButtonDown(0)) MarkHere();

            if (_viewing) Steer();
            Move();
        }

        // ------------------------------------------------------------ map mark

        /// <summary>
        /// Mark the ground under the drone on the LOCAL player's map. Reuses the
        /// game's own single "player custom marker" (MapUIManager.PlayerCustomMarker,
        /// a MapMarkerObject): we set its transform to the terrain point below the
        /// drone and call ShowMarker(), exactly what MapUIManager.PlacePlayerCustomMarker
        /// does after converting a map click - only the world point comes from the
        /// drone instead of the mouse. The game's UpdateMapMarkers loop then keeps
        /// the map icon on that spot. A second click moves the same marker.
        /// </summary>
        static void MarkHere()
        {
            string msg;
            if (MarkOnMap(_pos, out msg))
            {
                _markFlash = Time.time + 1.6f;
                Turret.Hinweis(Loc.T("Место отмечено на карте",
                                     "Location marked on the map"), 2f);
            }
            else
            {
                RevivalPlugin.L.LogWarning("SurvDrone mark failed: " + msg);
                Turret.Hinweis(Loc.T("Не удалось отметить место",
                                     "Could not mark the location"), 2.5f);
            }
        }

        static bool MarkOnMap(Vector3 world, out string msg)
        {
            msg = "";
            try
            {
                Type mapType = RevivalPlugin.TypeByName("MapUIManager");
                if (mapType == null) { msg = "MapUIManager missing"; return false; }

                MethodInfo get = AccessTools.PropertyGetter(mapType, "Inst");
                object raw = get == null ? null : get.Invoke(null, null);
                if (raw == null)
                {
                    FieldInfo inst = AccessTools.Field(mapType, "_instance");
                    if (inst != null) raw = inst.GetValue(null);
                }
                Component mgr = raw as Component;
                if (mgr == null) { msg = "no MapUIManager instance"; return false; }

                FieldInfo pcmF = AccessTools.Field(mapType, "PlayerCustomMarker");
                Component pcm = pcmF == null ? null : pcmF.GetValue(mgr) as Component;
                if (pcm == null) { msg = "no PlayerCustomMarker"; return false; }

                MethodInfo show = AccessTools.Method(pcm.GetType(), "ShowMarker", null, null);
                if (show == null) { msg = "no ShowMarker"; return false; }

                // Drop the mark on the terrain under the drone, as the native
                // click path does (raycast down, offset up 1 m).
                Vector3 point = world;
                Vector3 hit;
                if (Turret.RaycastObject(world + Vector3.up * 2f, Vector3.down,
                                         3000f, out hit) != null)
                    point = hit;

                pcm.gameObject.SetActive(true);
                pcm.transform.position = point + Vector3.up * 1f;
                SetMarkerText(pcm, "_markerCustomName",
                    Loc.T("Метка разведдрона", "Recon mark"));
                SetMarkerText(pcm, "_markerCustomDescription",
                    Loc.T("Отмечено с разведдрона",
                          "Marked from the surveillance drone"));
                show.Invoke(pcm, null);
                return true;
            }
            catch (Exception ex) { msg = ex.Message; return false; }
        }

        static void SetMarkerText(Component marker, string field, string value)
        {
            try
            {
                FieldInfo f = AccessTools.Field(marker.GetType(), field);
                if (f != null && f.FieldType == typeof(string))
                    f.SetValue(marker, value);
            }
            catch { }
        }

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

        static void Move()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 fwd = Forward();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (right.sqrMagnitude < 0.000001f) right = Vector3.right;
            else right.Normalize();

            bool motorless = Time.time >= _end;

            Vector3 accel = Vector3.up * (motorless ? -9.81f
                                                    : RevivalPlugin.CfgDroneGravity.Value);
            if (!motorless && _viewing)
            {
                if (Input.GetKey(KeyCode.W)) accel += fwd * Thrust;
                if (Input.GetKey(KeyCode.S)) accel -= fwd * Thrust;
                if (Input.GetKey(KeyCode.D)) accel += right * SideThrust;
                if (Input.GetKey(KeyCode.A)) accel -= right * SideThrust;
                if (Input.GetKey(KeyCode.Space)) accel += Vector3.up * Lift;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C))
                    accel -= Vector3.up * Lift;
            }
            else if (!motorless && !_viewing)
            {
                // Unattended but powered: hold position. Cancel gravity and bleed
                // the speed off, so it hovers where it was left instead of
                // drifting or falling - this is what lets the pilot step out of
                // the view and come back to a drone that is still there.
                accel = -_vel * 4f;
            }

            _vel += accel * dt;
            _vel -= _vel * Mathf.Min(1f, Drag * dt);
            if (_vel.magnitude > MaxSpeed) _vel = _vel.normalized * MaxSpeed;

            Vector3 step = _vel * dt;
            float len = step.magnitude;
            if (len > 0.0001f && Time.time >= _armed)
            {
                Vector3 hit;
                GameObject go = Turret.RaycastObject(_pos, step / len, len + 0.20f, out hit);
                if (go != null && !IsPilot(go))
                {
                    // No detonation, ever: it drops as a recoverable item.
                    Ground(hit, motorless ? "battery empty, crashed" : "flew into something");
                    return;
                }
            }
            _pos += step;

            if (Range() > 0f && Vector3.Distance(_pos, HomePlanar()) >= Range() && _pilotRoot != null)
            {
                // Out of radio range: it holds where it is and the picture is
                // lost. Bring the view back to the body; the drone waits there.
                if (_viewing) LeaveView();
                Turret.Hinweis(Loc.T("Разведдрон вне зоны связи",
                                     "Surveillance drone out of radio range"), 2.5f);
            }

            PlaceModel();
        }

        static void Recall()
        {
            Vector3 ground;
            GameObject go = Turret.RaycastObject(_pos + Vector3.up * 0.2f,
                                                 Vector3.down, 400f, out ground);
            Vector3 at = go == null ? _pos : ground;
            Ground(at, "recalled by the pilot");
        }

        static void EnterView()
        {
            if (!CameraOwner.Free)
            {
                Turret.Hinweis(Loc.T("Обзор занят", "The view is taken"), 2f);
                return;
            }
            if (!CameraOwner.Request(CameraOwner.Aufklaerer, true, "Aufklaerungsdrohne")) return;
            _viewing = true;
        }

        static void LeaveView()
        {
            _viewing = false;
            CameraOwner.Release(CameraOwner.Aufklaerer);
            Turret.Hinweis(Loc.T("Разведдрон висит на месте",
                                 "Surveillance drone is holding position"), 2.5f);
        }

        // ------------------------------------------------------- ground / pickup

        /// <summary>Puts the drone on the ground as a recoverable item.</summary>
        static void Ground(Vector3 at, string why)
        {
            _wreckAt = at + Vector3.up * 0.25f;
            End(why, true);
            RevivalPlugin.L.LogInfo("SurvDrone grounded (" + why + ") at " + _wreckAt + ".");
            Turret.Hinweis(Loc.T("Разведдрон на земле - подойди и подбери",
                                 "Surveillance drone is down - walk up and pick it up"), 4f);
        }

        static void PickUp()
        {
            string msg;
            if (Admin.GibItem(DroneGear.SurveillanceId, 1, out msg))
            {
                if (_wreck != null) { UnityEngine.Object.Destroy(_wreck); _wreck = null; }
                Turret.Hinweis(Loc.T("Разведдрон подобран", "Surveillance drone collected"), 2.5f);
                RevivalPlugin.L.LogInfo("SurvDrone picked up.");
            }
            else
            {
                Turret.Hinweis(Loc.T("Не удалось подобрать: ", "Cannot pick up: ") + msg, 3f);
            }
        }

        static bool NearWreck()
        {
            if (_wreck == null) return false;
            GameObject body = MapTools.LocalPlayer();
            if (body == null) return false;
            return Vector3.Distance(body.transform.position, _wreckAt) <= PickupRange;
        }

        /// <summary>
        /// Ends the flight and gives the view back. If <paramref name="drop"/>
        /// the grounded item model is left standing at <see cref="_wreckAt"/>.
        /// </summary>
        static void End(string why, bool drop)
        {
            bool was = _flying;
            _flying = false;
            bool wasViewing = _viewing;
            _viewing = false;
            if (_model != null) { UnityEngine.Object.Destroy(_model); _model = null; }
            if (wasViewing) CameraOwner.Release(CameraOwner.Aufklaerer);

            if (drop)
            {
                BuildWreck();
            }
            if (was && !drop)
                RevivalPlugin.L.LogInfo("SurvDrone ended (" + why + ").");
        }

        // -------------------------------------------------------------- model

        static void BuildModel(bool wreck)
        {
            DestroyModel(wreck);
            float scale = DroneGear.CfgSurvModelScale == null
                ? 12f : Mathf.Max(1f, DroneGear.CfgSurvModelScale.Value);
            GameObject g = Shape(scale);
            g.name = wreck ? "NDR_SurvDroneWreck" : "NDR_SurvDrone";
            if (wreck) { _wreck = g; g.transform.position = _wreckAt; }
            else
            {
                _model = g;
                _modelRenderers = g.GetComponentsInChildren<Renderer>(true);
                PlaceModel();
            }
        }

        static void BuildWreck() { BuildModel(true); }

        static void DestroyModel(bool wreck)
        {
            if (wreck) { if (_wreck != null) { UnityEngine.Object.Destroy(_wreck); _wreck = null; } }
            else { if (_model != null) { UnityEngine.Object.Destroy(_model); _model = null; } _modelRenderers = null; }
        }

        static void PlaceModel()
        {
            if (_model == null) return;
            _model.transform.position = _pos;
            _model.transform.rotation = Quaternion.LookRotation(Forward(), Vector3.up);
            ApplyModelVisibility();
        }

        /// <summary>
        /// The recon camera sits at the drone's own centre (`_pos`), so while the
        /// pilot looks THROUGH the drone its large airframe (the FPV mesh scaled
        /// ~12x) fills the screen - a dark body straight ahead and the rotor frame
        /// across the top, with no way to see where it flies. So hide the model's
        /// renderers while viewing, and show them again the moment the pilot steps
        /// back to his body, where seeing the drone hover is the whole point of a
        /// reusable recon drone (the FPV drone dodges this by building no local
        /// model at all - only remote players see it). Runs every flight frame
        /// off cached renderers, so it costs nothing and cannot miss a transition.
        /// The wreck is a separate object and is never touched here.
        /// </summary>
        static void ApplyModelVisibility()
        {
            if (_modelRenderers == null) return;
            bool show = !_viewing;
            for (int i = 0; i < _modelRenderers.Length; i++)
                if (_modelRenderers[i] != null) _modelRenderers[i].enabled = show;
        }

        /// <summary>
        /// The recon drone is the SAME airframe as the FPV drone, only bigger:
        /// the detailed quadrotor from `drone.ndmesh` + `drone_diffuse.png` that
        /// `Drone.Modell.Bauen()` already builds for the FPV drone (real body,
        /// arms, four guard-ringed rotors, camera), scaled up by the recon
        /// SurveillanceModelScale (~12 vs the FPV's ModelScale ~4), so it reads
        /// as a large surveillance version of the same machine. The old
        /// primitive cube-and-four-discs placeholder is gone.
        ///
        /// Bauen() centres the mesh on its transform (child offset by
        /// -bounds.center) and applies the FPV ModelScale; we override the root
        /// localScale to the recon scale, which scales that offset with it, so
        /// the drone stays centred on the point it actually flies at. It builds
        /// a MeshFilter/MeshRenderer only - no collider - which is exactly what
        /// this reusable, non-detonating drone needs (our raycast does the
        /// flying; a big sky collider would shove the world around).
        /// </summary>
        static GameObject Shape(float scale)
        {
            GameObject root = Drone.Modell.Bauen();
            root.name = "NDR_SurvDroneShape";
            root.transform.localScale = new Vector3(scale, scale, scale);
            return root;
        }

        // ------------------------------------------------------------- helpers

        static Vector3 Forward()
        {
            return Quaternion.Euler(-_pitch, _yaw, 0f) * Vector3.forward;
        }

        static Vector3 HomePlanar()
        {
            return _pilotRoot != null ? _pilotRoot.position : _pos;
        }

        static float Range()
        {
            return DroneGear.CfgSurvRange == null ? 1400f
                 : Mathf.Max(50f, DroneGear.CfgSurvRange.Value);
        }

        static float Battery()
        {
            float charge = Mathf.Max(1f, _end - _start);
            return Mathf.Clamp01((_end - Time.time) / charge);
        }

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

        static bool IsPilot(GameObject go)
        {
            if (go == null) return false;
            if (_pilotRoot != null)
            {
                Transform t = go.transform;
                while (t != null) { if (t == _pilotRoot) return true; t = t.parent; }
            }
            return Vector3.Distance(go.transform.position, HomePlanar()) < SafeRadius;
        }

        static Transform PilotRoot()
        {
            GameObject body = MapTools.LocalPlayer();
            return body == null ? null : body.transform;
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try
            {
                _key = (KeyCode)Enum.Parse(typeof(KeyCode),
                    DroneGear.CfgSurvKey == null ? "B" : DroneGear.CfgSurvKey.Value, true);
            }
            catch
            {
                _key = KeyCode.B;
                RevivalPlugin.L.LogWarning("SurvDrone: key \""
                    + (DroneGear.CfgSurvKey == null ? "?" : DroneGear.CfgSurvKey.Value)
                    + "\" unknown, using B.");
            }
            return _key;
        }

        // --------------------------------------------------------------- camera

        /// <summary>Called from CameraOwner.LateTick while this drone owns the view.</summary>
        public static void LateTick()
        {
            if (!_flying || !_viewing) return;
            try
            {
                Camera cam = CameraOwner.ViewCamera();
                if (cam == null) return;
                cam.transform.position = _pos;
                cam.transform.rotation = Quaternion.LookRotation(Forward(), Vector3.up);
                if (RevivalPlugin.CfgDroneFov.Value > 1f)
                    cam.fieldOfView = RevivalPlugin.CfgDroneFov.Value;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("SurvDrone camera: " + ex);
                End("camera error", false);
            }
        }

        // ---------------------------------------------------------------- overlay

        public static void Draw()
        {
            Hold.Draw(Loc.T("Запуск разведдрона", "Surveillance launch"));

            // Ground prompt when the pilot is standing over the downed drone.
            if (!_flying && _wreck != null && NearWreck())
            {
                Prompt(Loc.T("[" + Key() + "] Подобрать разведдрон",
                             "[" + Key() + "] Pick up surveillance drone"));
            }

            if (!_flying || !_viewing) return;
            try
            {
                // The SAME video-feed HUD the FPV drone draws: noise on a weak
                // link, the camera corner brackets, the [REC] pilot line with
                // timer/voltage/RSSI/speeds, the heading tape and pitch ladder,
                // the crosshair and the battery bar - all driven by this drone's
                // own flight state instead of the FPV drone's.
                float w = Screen.width, h = Screen.height;
                Color old = GUI.color;
                float sig = Signal();

                if (sig < 1f) Rauschen(w, h, sig);
                Rahmen(w, h, sig);
                Technik(w, h, sig);
                Fadenkreuz(w, h);
                Zahlen(w, h, sig);
                MarkOverlay(w, h);

                GUI.color = old;
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("SurvDrone draw: " + ex); }
        }

        // ------------------------------------------------------- HUD (FPV look)

        /// <summary>Signal 1 (clean) .. 0 (break-up), from distance vs range -
        /// the same falloff the FPV drone uses, so the noise reads identically.</summary>
        static float Signal()
        {
            float weit = Range();
            float ruhig = weit * 0.7f;
            float d = _pilotRoot != null ? Vector3.Distance(_pos, HomePlanar()) : 0f;
            return d <= ruhig ? 1f
                : Mathf.Clamp01(1f - (d - ruhig) / Mathf.Max(1f, weit - ruhig));
        }

        static bool Motorless() { return Time.time >= _end; }

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
                GUI.DrawTexture(new Rect(0f, y, w, hh), Px());
            }
        }

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
                    GUI.DrawTexture(new Rect(xs[i], ys[k], l, t), Px());
                    float yv = k == 0 ? m : h - m - l;
                    float xv = i == 0 ? m : w - m - t;
                    GUI.DrawTexture(new Rect(xv, yv, t, l), Px());
                }
        }

        static void Technik(float w, float h, float sig)
        {
            Color ink = sig > 0.35f
                ? new Color(0.82f, 1f, 0.86f, 0.92f)
                : new Color(1f, 0.48f, 0.34f, 0.95f);
            float m = Mathf.Max(18f, h * 0.045f);
            int seconds = Mathf.Max(0, Mathf.FloorToInt(Time.time - _start));
            string timer = (seconds / 60).ToString("00") + ":"
                + (seconds % 60).ToString("00");
            string state = Time.time >= _armed ? "ARM" : "SAFE";
            OsdLabel(new Rect(m + 2f, m + 4f, 260f, 22f),
                "[REC]  " + state + "  " + timer + "  CH8", ink);

            float battery = Battery();
            float volts = 14.0f + 2.8f * battery;
            float ground = new Vector2(_vel.x, _vel.z).magnitude;
            OsdLabel(new Rect(w - m - 310f, m + 4f, 310f, 22f),
                "6S " + volts.ToString("0.0", CultureInfo.InvariantCulture)
                + "V   RSSI " + Mathf.RoundToInt(sig * 100f) + "%", ink);
            OsdLabel(new Rect(w - m - 310f, h - m - 28f, 310f, 22f),
                "GS " + ground.ToString("0.0", CultureInfo.InvariantCulture)
                + "m/s   VS " + _vel.y.ToString("+0.0;-0.0;0.0",
                    CultureInfo.InvariantCulture) + "m/s", ink);

            float tapeY = m + 31f;
            float cx = w * 0.5f;
            for (int off = -40; off <= 40; off += 10)
            {
                float x = cx + off * 3.0f;
                float tick = off % 20 == 0 ? 9f : 5f;
                GUI.color = ink;
                GUI.DrawTexture(new Rect(x - 0.75f, tapeY, 1.5f, tick), Px());
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
            GUI.DrawTexture(new Rect(cx - 4f, tapeY - 4f, 8f, 3f), Px());

            float baseY = h * 0.5f + _pitch * h / 85f;
            for (int pitch = -20; pitch <= 20; pitch += 10)
            {
                float y = baseY - pitch * h / 85f;
                if (y < h * 0.25f || y > h * 0.75f) continue;
                float arm = pitch == 0 ? 58f : 34f;
                GUI.color = new Color(0f, 0f, 0f, 0.45f);
                GUI.DrawTexture(new Rect(cx - arm - 23f + 1f, y + 1f, arm, 1.5f), Px());
                GUI.DrawTexture(new Rect(cx + 23f + 1f, y + 1f, arm, 1.5f), Px());
                GUI.color = ink;
                GUI.DrawTexture(new Rect(cx - arm - 23f, y, arm, 1.5f), Px());
                GUI.DrawTexture(new Rect(cx + 23f, y, arm, 1.5f), Px());
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
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text);
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
            GUI.DrawTexture(new Rect(cx - 1.5f, cy - 1.5f, 3f, 3f), Px());
        }

        static void Kreuz(float cx, float cy, float gap, float arm)
        {
            GUI.DrawTexture(new Rect(cx - gap - arm, cy - 1f, arm, 2f), Px());
            GUI.DrawTexture(new Rect(cx + gap, cy - 1f, arm, 2f), Px());
            GUI.DrawTexture(new Rect(cx - 1f, cy - gap - arm, 2f, arm), Px());
            GUI.DrawTexture(new Rect(cx - 1f, cy + gap, 2f, arm), Px());
        }

        static void Zahlen(float w, float h, float sig)
        {
            float m = Mathf.Max(18f, h * 0.045f);
            float akku = Battery();
            float bw = Mathf.Max(90f, w * 0.10f);
            float bh = Mathf.Max(9f, h * 0.014f);
            float bx = m + 4f, by = h - m - bh - 24f;

            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(bx - 1f, by - 1f, bw + 2f, bh + 2f), Px());
            GUI.color = akku > 0.2f
                ? new Color(0.55f, 1f, 0.6f, 0.85f)
                : new Color(1f, 0.35f, 0.25f, 0.9f);
            GUI.DrawTexture(new Rect(bx, by, bw * akku, bh), Px());

            float d = _pilotRoot != null ? Vector3.Distance(_pos, HomePlanar()) : 0f;
            float hoehe = Hoehe();
            string zeile = Loc.T("БАТ ", "BAT ") + Mathf.RoundToInt(akku * 100f) + "%"
                + Loc.T("   ДИСТ ", "   DIST ") + Mathf.RoundToInt(d) + " m"
                + Loc.T("   ВЫС ", "   ALT ") + (hoehe < 0f ? "--" : Mathf.RoundToInt(hoehe).ToString()) + " m"
                + "   SIG " + Mathf.RoundToInt(sig * 100f) + "%";
            if (Motorless()) zeile = Loc.T("БАТ РАЗРЯЖЕНА - ПАДЕНИЕ", "BATTERY EMPTY - FALLING") + "   " + zeile;
            else if (sig < 0.35f) zeile = Loc.T("СЛАБЫЙ СИГНАЛ", "WEAK SIGNAL") + "   " + zeile;

            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(bx + 1f, by + bh + 5f, w, 22f), zeile);
            GUI.color = sig > 0.35f && !Motorless()
                ? new Color(0.75f, 1f, 0.8f, 0.95f)
                : new Color(1f, 0.45f, 0.3f, 0.95f);
            GUI.Label(new Rect(bx, by + bh + 4f, w, 22f), zeile);
        }

        /// <summary>The always-on "left-click to mark" hint, and a brief flash
        /// at the crosshair right after a mark is placed.</summary>
        static void MarkOverlay(float w, float h)
        {
            float cx = w * 0.5f, cy = h * 0.5f;
            Color ink = new Color(0.7f, 1f, 0.75f, 0.85f);
            OsdLabel(new Rect(cx - 170f, h - Mathf.Max(18f, h * 0.045f) - 52f, 340f, 20f),
                Loc.T("[ЛКМ] отметить это место на карте",
                      "[LMB] mark this spot on the map"), ink);

            if (Time.time < _markFlash)
            {
                GUI.color = new Color(1f, 0.9f, 0.3f, 0.95f);
                GUI.DrawTexture(new Rect(cx - 22f, cy - 22f, 44f, 2f), Px());
                GUI.DrawTexture(new Rect(cx - 22f, cy + 20f, 44f, 2f), Px());
                GUI.DrawTexture(new Rect(cx - 22f, cy - 22f, 2f, 44f), Px());
                GUI.DrawTexture(new Rect(cx + 20f, cy - 22f, 2f, 44f), Px());
                OsdLabel(new Rect(cx - 90f, cy + 28f, 180f, 20f),
                    Loc.T("ОТМЕЧЕНО", "MARKED"),
                    new Color(1f, 0.9f, 0.3f, 0.95f));
            }
        }

        static void Prompt(string text)
        {
            try
            {
                Color old = GUI.color;
                float w = 360f, h = 26f;
                float x = (Screen.width - w) * 0.5f;
                float y = Screen.height * 0.72f;
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(x - 4f, y - 4f, w + 8f, h + 8f), Px());
                GUI.color = Color.white;
                GUI.Label(new Rect(x, y, w, h), text);
                GUI.color = old;
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("SurvDrone prompt: " + ex); }
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
    }
}
