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
        public const int Aufklaerer = 3;   // the reusable surveillance drone

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
            else if (_owner == Aufklaerer) SurvDrone.LateTick();
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

        /// <summary>
        /// Prefix on PlayerVehicleManager::GetOutFromVehicle. Hands the camera
        /// back to the game BEFORE the game switches it back on foot.
        ///
        /// BELEGT (IL von GetOutFromVehicle, 2026-09-03): fuer den lokalen
        /// Spieler (isMine) ruft das Aussteigen
        ///     _camSwitch.ChangeCameraToVehicleMode(false, CameraOptions)
        /// und schaltet danach den Fuss-Kameracontroller wieder ein. Genau
        /// dieser `_camSwitch` (CameraSwitch) ist aber das Skript, das die
        /// Geschuetz-Uebernahme ueber CameraOwner STILLGELEGT hat. Ein
        /// deaktiviertes Behaviour fuehrt einen direkten Methodenaufruf zwar
        /// aus, aber der Umschaltvorgang setzt sich nicht durch: HUD, Karte,
        /// Inventar und Menue des Spiels bleiben dunkel, bis das Spiel neu
        /// startet. Die alte Aufraeumung (Rescan -> Clear -> SetManning(false))
        /// laeuft erst ~0.4 s SPAETER, lange nachdem das Spiel schon umgeschaltet
        /// hat. Deshalb hier: erst die Kamera zurueckgeben (CameraSwitch wieder
        /// an), dann laesst das Original ChangeCameraToVehicleMode auf einem
        /// aktiven CameraSwitch laufen, und das HUD kommt zurueck.
        ///
        /// Feldbericht (2026-09-03), der den Fix ausgeloest hat: Geschuetz per G
        /// bedient, ausgestiegen, danach "gar nichts mehr angezeigt" - Karte,
        /// Inventar, Menue tot, waehrend die IMGUI-Overlays des Mods
        /// (Patrouillenrand, Admin/Teleport) weiter zeichneten.
        ///
        /// Nur fuer das EIGENE Fahrzeug: `_manning` ist ohnehin lokal, und der
        /// Instanzvergleich haelt das Aussteigen eines fremden Spielers heraus.
        /// </summary>
        public static void GetOutPrefix(object __instance)
        {
            try
            {
                if (!_manning) return;
                object myPvm = _vgs == null ? null : Field(_vgs, "_playerVehicleManager");
                if (myPvm == null || !ReferenceEquals(__instance, myPvm)) return;
                RevivalPlugin.L.LogInfo("Geschuetz: Fahrzeug wird verlassen - Kamera "
                    + "vor dem Umschalten zurueckgegeben (HUD-Fix).");
                SetManning(false);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Geschuetz GetOutPrefix: " + ex);
                SetManning(false);
            }
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

                // HUD-Fix: die Kamera zurueckgeben, BEVOR das Spiel beim
                // Aussteigen ueber den (vom Mod stillgelegten) CameraSwitch auf
                // die Fuss-Kamera umschaltet. Ohne das bleibt nach dem Bedienen
                // des Geschuetzes das gesamte Spiel-UI dunkel. Siehe GetOutPrefix.
                Type pvm = RevivalPlugin.TypeByName("PlayerVehicleManager");
                MethodInfo getOut = pvm == null ? null
                    : AccessTools.Method(pvm, "GetOutFromVehicle",
                                         new Type[] { typeof(int) }, null);
                if (getOut != null)
                {
                    harmony.Patch(getOut,
                        new HarmonyMethod(typeof(Turret).GetMethod("GetOutPrefix")),
                        null, null, null, null);
                    RevivalPlugin.L.LogInfo("Geschuetz: GetOutFromVehicle gepatcht "
                        + "(HUD-Fix beim Aussteigen).");
                }
                else
                {
                    RevivalPlugin.L.LogWarning("Geschuetz: PlayerVehicleManager."
                        + "GetOutFromVehicle(int) nicht gefunden - der HUD-Fix beim "
                        + "direkten Aussteigen ist inaktiv.");
                }

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
                Hinweis(Loc.T("Нет боеприпасов - предмет " + MunitionsId()
                        + " отсутствует в багажнике, рюкзаке и разгрузке",
                              "No ammunition - item " + MunitionsId()
                        + " missing from trunk, backpack and vest"), 2.5f);
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

            // BTR-Bordgeschuetz gegen ein Fahrzeug: frisst dessen Panzerung.
            // Ein Panzer-Schuetze (_tank) hat oben schon eine Granate gezuendet,
            // GunHit macht fuer ihn nichts und wir fallen durch.
            if (VehicleArmor.GunHit(struck, _tank)) return true;

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
        // One cache entry PER id, not a single slot. A single slot
        // (the old _hasId/_hasResult/_hasUntil) is defeated the instant two
        // callers alternate ids within one frame: each call sees a different
        // id than the last, misses, and runs a fresh FindObjectsOfType. The
        // surveillance drone polls SurveillanceId and BatteryId back to back
        // every frame (SurvDrone.TickCore), which turned this into TWO scene
        // scans plus a List allocation every frame on foot - the 6.0.0 frame
        // drop. A dictionary gives each id its own half-second answer, so the
        // number of scans is bounded by the distinct ids asked, not the frame
        // rate, no matter how many callers interleave.
        static readonly Dictionary<int, float> _hasUntil = new Dictionary<int, float>();
        static readonly Dictionary<int, bool> _hasResult = new Dictionary<int, bool>();

        /// <summary>
        /// Does item `wanted` lie in one of the local player's inventories?
        /// The same three containers TakeItem walks, but nothing is taken.
        ///
        /// The answer is kept for half a second per id. The jammer and the
        /// surveillance drone ask in every frame, and
        /// FindObjectsOfType(PlayerInventoryManager) per frame is the kind of
        /// cost that never shows up in a log and always shows up in the frame
        /// time.
        /// </summary>
        internal static bool HasItem(int wanted)
        {
            float until;
            if (_hasUntil.TryGetValue(wanted, out until) && Time.time < until)
                return _hasResult[wanted];
            bool found = false;
            List<object> invs = PlayerInventories();
            for (int i = 0; i < invs.Count && !found; i++)
                if (CountItem(invs[i], wanted) > 0) found = true;
            _hasResult[wanted] = found;
            _hasUntil[wanted] = Time.time + 0.5f;
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

        // ----------------------------------------------- NDR vehicle modules
        // Small internal seam for Revival.Modules.cs / Revival.GunnerOptics.cs,
        // kept here so that feature's own files never touch Turret's private
        // state. Three additions, nothing existing changed.

        /// <summary>The vehicle whose gunner seat the local player is manning,
        /// or null. Revival.Modules keys installed modules on it.</summary>
        internal static Transform MannedVehicle
        {
            get { return _manning ? _vehicleRoot : null; }
        }

        /// <summary>True while the manned vehicle is the T-72, false for the APC.</summary>
        internal static bool IsTankManned { get { return _tank; } }

        /// <summary>
        /// Puts `count` of item `itemId` back into the player's backpack through
        /// the game's own AddBackpackItemFromValues - the inverse of TakeItem,
        /// used when a vehicle module is uninstalled. Returns true on success.
        /// </summary>
        internal static bool GiveItem(int itemId, int count)
        {
            List<object> invs = PlayerInventories();
            for (int i = 0; i < invs.Count; i++)
            {
                object inv = invs[i];
                MethodInfo add = null;
                MethodInfo[] all = inv.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int k = 0; k < all.Length; k++)
                    if (all[k].Name == "AddBackpackItemFromValues") { add = all[k]; break; }
                if (add == null) continue;

                ParameterInfo[] ps = add.GetParameters();
                object[] args = new object[ps.Length];
                for (int k = 0; k < ps.Length; k++)
                    args[k] = ps[k].ParameterType.IsValueType
                        ? Activator.CreateInstance(ps[k].ParameterType) : null;
                args[0] = itemId;                                 // ItemID
                if (ps.Length >= 7) args[6] = count;              // Bullets/Menge
                if (ps.Length >= 1 && ps[ps.Length - 1].ParameterType == typeof(bool))
                    args[ps.Length - 1] = true;                   // onChangeInventory
                try { add.Invoke(inv, args); return true; }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("GiveItem(" + itemId + "): " + ex);
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// The trunk ContainerData of an ARBITRARY vehicle (not only the manned
        /// one) - the generalisation of TrunkContainer, used by Revival.Modules
        /// to stock a patrol trunk with loot and to read it back for the wreck
        /// despawn bonus. Returns the ContainerData or null.
        /// </summary>
        internal static object TrunkDataOf(Transform veh)
        {
            if (veh == null) return null;
            Type ic = RevivalPlugin.TypeByName("ItemsContainer");
            if (ic == null) return null;
            Component[] all = veh.GetComponentsInChildren(ic, true);
            if (all.Length == 0) return null;
            return Field(all[0], "_containerData");
        }

        /// <summary>How often item `itemId` lies in a container's data.</summary>
        internal static int CountInContainer(object data, int itemId)
        {
            return CountIn(data, itemId);
        }

        /// <summary>
        /// Writes `count` of item `itemId` into the first free slot of a
        /// container's parallel ObscuredInt arrays (ItemID/ItemBullets/SlotID),
        /// the same arrays TakeFrom reads. One call fills one slot; call it again
        /// for more items. Returns false when the container is full or unreadable.
        /// </summary>
        internal static bool AddToContainer(object data, int itemId, int count)
        {
            if (data == null) return false;
            Array ids = Field(data, "ItemID") as Array;
            Array bullets = Field(data, "ItemBullets") as Array;
            Array slots = Field(data, "SlotID") as Array;
            if (ids == null || bullets == null) return false;

            for (int i = 0; i < ids.Length && i < bullets.Length; i++)
            {
                object idBox = ids.GetValue(i);
                int cur = idBox == null ? 0 : Obscured(idBox);
                if (cur > 0) continue;                 // slot occupied

                ids.SetValue(MakeObscured(ids.GetType().GetElementType(), itemId), i);
                bullets.SetValue(MakeObscured(bullets.GetType().GetElementType(), count), i);
                if (slots != null && i < slots.Length)
                    slots.SetValue(MakeObscured(slots.GetType().GetElementType(), i), i);
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
                // NDR vehicle modules: a modern wide periscope replaces the old
                // round scope, with toggleable thermal / night vision when the
                // matching module is installed. When it takes over the optic we
                // skip the legacy scope entirely and only add the load bar.
                if (GunnerOptics.Draw(_tank, _vehicleRoot))
                {
                    DrawLadeanzeige();
                    return;
                }

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
                      Loc.T("Заряжание ", "Loading ") + rest.ToString("0.0") + " s");
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
}
