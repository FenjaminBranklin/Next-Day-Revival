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

                if (_flying)
                {
                    if (Input.GetKeyDown(Key())) TasteImFlug();
                }
                // Not a tap any more: the antenna must be up and the key held
                // through the launch bar. WantFpvLaunch returns true on the one
                // frame the hold completes.
                else if (DroneGear.WantFpvLaunch(Key()))
                {
                    Launch();
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
            // Insurance behind the launch-hold gate: no drone lifts without a
            // raised antenna (unless the whole antenna gate is switched off).
            if (!Antenna.LaunchAllowed()) { Antenna.LaunchDeniedHint(); return; }

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
                // NDR vehicle modules: a jamming module in the manned vehicle
                // broadcasts from the vehicle with the larger vehicle radius and
                // takes precedence over a carried jammer.
                float vr;
                bool vehicle = VehicleModules.LocalVehicleJammer(out vr);
                if (!_traegt && !vehicle)
                {
                    _gemeldet = false;
                    return;
                }
                float r;
                Vector3 p;
                if (vehicle)
                {
                    r = vr;
                    Transform veh = Turret.MannedVehicle;
                    if (veh == null) { _gemeldet = false; return; }
                    p = veh.position;
                }
                else
                {
                    r = Mathf.Max(1f, RevivalPlugin.CfgJammerRadius.Value);
                    if (!EigenePosition(out p)) return;
                }
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
            string zeile = Loc.T("БАТ ", "BAT ") + Mathf.RoundToInt(akku * 100f) + "%"
                + Loc.T("   ДИСТ ", "   DIST ") + Mathf.RoundToInt(Entfernung()) + " m"
                + Loc.T("   ВЫС ", "   ALT ") + (hoehe < 0f ? "--" : Mathf.RoundToInt(hoehe).ToString()) + " m"
                + "   SIG " + Mathf.RoundToInt(sig * 100f) + "%";
            if (Jammer.Warnt) zeile = Loc.T("ГЛУШИТЕЛЬ", "JAMMER") + "   " + zeile;
            else if (Motorlos()) zeile = Loc.T("БАТ РАЗРЯЖЕНА - ПАДЕНИЕ", "BATTERY EMPTY - FALLING") + "   " + zeile;
            else if (sig < 0.35f) zeile = Loc.T("СЛАБЫЙ СИГНАЛ", "WEAK SIGNAL") + "   " + zeile;

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
            // Frozen while the FPV drone flies, while the antenna is raising,
            // while any launch hold charges, and while the surveillance drone
            // is being viewed. Stepping OUT of the surveillance view frees the
            // body again (SurvDrone.Viewing is false then), which is the whole
            // point of the two-view drone. Antenna.Frozen (not the raw Deploying
            // flag) self-heals: a deploy whose per-frame tick stopped past its
            // deadline no longer freezes the body, so a death/scene-change/downed
            // event mid-deploy cannot leave the player stuck and unable to walk.
            if (Drone.Flying || Antenna.Frozen || DroneGear.LaunchBusy
                || SurvDrone.Viewing)
                __result = true;
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

    // ------------------------------------------------------------ DroneAlert

    /// <summary>
    /// A lightweight proximity warning for incoming FPV drones. When a hostile
    /// crew drone is in the air closer than `DroneAlert/AlertRange` metres, it
    /// pips - faster the nearer it is, like a parking sensor - and blinks a
    /// hint in the lower-left corner, where the game shows its own event notes.
    ///
    /// DELIBERATELY SELF-CONTAINED so several agents can work the same file at
    /// once and merge cleanly: everything the feature needs lives in this one
    /// class. Its only reach into the rest of the plugin is read-only -
    /// `CrewDrone.CollectThreats` for the drone positions, `Admin.LocalPlayer`
    /// for the body, `Drone.Flying` to fall silent while the player is piloting.
    /// It is driven by one `Tick()` in Update and one `Draw()` in OnGUI, sends
    /// nothing over the wire, and needs no Harmony patch of its own.
    /// </summary>
    public static class DroneAlert
    {
        static readonly List<Vector3> _threats = new List<Vector3>();
        static float _nextScan;
        static float _nextBeep;
        static float _shownUntil;
        static bool _active;
        static float _closest;
        static int _count;
        static bool _warned;

        static AudioSource _beeper;
        static AudioClip _tone;
        static Texture2D _dot;
        static GUIStyle _style;

        const float ScanInterval = 0.33f;

        static float Range()
        {
            return RevivalPlugin.CfgDroneAlertRange == null
                ? 500f : Mathf.Max(1f, RevivalPlugin.CfgDroneAlertRange.Value);
        }

        public static void Tick()
        {
            if (RevivalPlugin.CfgDroneAlert == null
                || !RevivalPlugin.CfgDroneAlert.Value) { _active = false; return; }
            try
            {
                // The pilot is already looking through his own drone; warning him
                // about the craft he is flying would only nag. Fall silent.
                if (Drone.Flying) { _active = false; return; }

                if (Time.time >= _nextScan)
                {
                    _nextScan = Time.time + ScanInterval;
                    Scan();
                }
                if (_active) Beep();
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    if (RevivalPlugin.L != null)
                        RevivalPlugin.L.LogWarning("Drohnenwarner: " + ex.Message);
                }
            }
        }

        static void Scan()
        {
            _threats.Clear();
            CrewDrone.CollectThreats(_threats);

            GameObject player = MapTools.LocalPlayer();
            if (player == null) { _active = false; _count = 0; return; }
            Vector3 me = player.transform.position;

            float range = Range();
            float r2 = range * range;
            float best = float.MaxValue;
            int n = 0;
            for (int i = 0; i < _threats.Count; i++)
            {
                float d2 = (_threats[i] - me).sqrMagnitude;
                if (d2 > r2) continue;
                n++;
                if (d2 < best) best = d2;
            }

            bool was = _active;
            _active = n > 0;
            _count = n;
            _closest = _active ? Mathf.Sqrt(best) : 0f;
            if (_active)
            {
                // Hold the hint a little past the next scan so it does not
                // flicker in the gap between scans.
                _shownUntil = Time.time + ScanInterval + 0.3f;
                if (!was)
                {
                    _nextBeep = 0f;   // sound the first pip at once
                    if (RevivalPlugin.L != null)
                        RevivalPlugin.L.LogInfo("Drone alert: " + n
                            + " hostile drone(s) within "
                            + Mathf.RoundToInt(_closest) + " m.");
                }
            }
        }

        static void Beep()
        {
            if (RevivalPlugin.CfgDroneAlertSound == null
                || !RevivalPlugin.CfgDroneAlertSound.Value) return;
            if (Time.time < _nextBeep) return;

            float frac = Mathf.Clamp01(_closest / Range());
            // Near -> fast pips and a higher pitch; far -> slow and lower.
            _nextBeep = Time.time + Mathf.Lerp(0.16f, 1.1f, frac);

            EnsureBeeper();
            if (_beeper == null || _tone == null) return;
            float vol = RevivalPlugin.CfgDroneAlertVolume == null
                ? 0.7f : Mathf.Clamp01(RevivalPlugin.CfgDroneAlertVolume.Value);
            _beeper.pitch = Mathf.Lerp(1.3f, 0.92f, frac);
            _beeper.PlayOneShot(_tone, vol);
        }

        static void EnsureBeeper()
        {
            if (_beeper != null) return;
            try
            {
                GameObject go = new GameObject("NDR_DroneAlertBeeper");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _beeper = go.AddComponent<AudioSource>();
                _beeper.loop = false;
                _beeper.playOnAwake = false;
                _beeper.spatialBlend = 0f;   // a 2D warning, not a world sound
                _beeper.volume = 1f;
                _tone = Tone();
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Drohnenwarner-Ton: " + ex.Message);
            }
        }

        /// <summary>
        /// The warning pip, computed rather than loaded - same reasoning as the
        /// drone hum. A single 80 ms sine at 1760 Hz under a raised-cosine
        /// envelope, so it opens and closes without a click.
        /// </summary>
        static AudioClip Tone()
        {
            const int rate = 22050;
            int len = rate * 8 / 100;   // 80 ms
            if (len < 2) len = 2;
            float[] d = new float[len];
            const float freq = 1760f;
            for (int i = 0; i < len; i++)
            {
                float t = (float)i / rate;
                float env = Mathf.Sin(Mathf.PI * i / (len - 1));
                d[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * env * 0.9f;
            }
            AudioClip clip = AudioClip.Create("NDR_DroneAlertTone", len, 1,
                                              rate, false);
            clip.SetData(d, 0);
            return clip;
        }

        public static void Draw()
        {
            if (RevivalPlugin.CfgDroneAlert == null
                || !RevivalPlugin.CfgDroneAlert.Value) return;
            if (RevivalPlugin.CfgDroneAlertHud == null
                || !RevivalPlugin.CfgDroneAlertHud.Value) return;
            if (Drone.Flying) return;
            if (!_active && Time.time > _shownUntil) return;
            try
            {
                if (_style == null)
                {
                    // Note: GUIStyle.fontStyle is avoided on purpose - the
                    // FontStyle enum lives in UnityEngine.TextRenderingModule,
                    // which build.ps1 does not reference. A larger fontSize plus
                    // the blink, colour and shadow make the hint prominent enough.
                    _style = new GUIStyle(GUI.skin.label);
                    _style.fontSize = 16;
                }

                bool blink = ((int)(Time.time * 3f) & 1) == 0;
                string msg = Loc.T("! ДРОН РЯДОМ", "! DRONE NEARBY")
                    + "  " + Mathf.RoundToInt(_closest) + " m"
                    + (_count > 1 ? "  x" + _count : "");

                // Lower-left, where the game shows its own event hints.
                float w = 360f;
                float h = 26f;
                float x = 20f;
                float y = Screen.height - 96f;

                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(x - 8f, y - 4f, w, h + 8f), Dot());
                // Shadow, so the text stays readable over a bright sky.
                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.Label(new Rect(x + 1f, y + 1f, w, h), msg, _style);
                GUI.color = blink
                    ? new Color(1f, 0.28f, 0.22f, 0.98f)
                    : new Color(1f, 0.62f, 0.28f, 0.85f);
                GUI.Label(new Rect(x, y, w, h), msg, _style);
                GUI.color = old;
            }
            catch { }
        }

        static Texture2D Dot()
        {
            if (_dot != null) return _dot;
            _dot = new Texture2D(1, 1);
            _dot.SetPixel(0, 0, Color.white);
            _dot.Apply();
            return _dot;
        }
    }
}
