// Next Day: Survival - Revival Toolkit
//
// The technical - a light gun truck with three places: driver, co-driver and a
// STANDING gunner behind a pintle-mounted machine gun on the rear deck. The
// whole feature is this file plus four one-line seams in RevivalPlugin.cs, one
// registry entry and the bilingual strings in RevivalUralTruck.cs, and one
// camera-owner id in Revival.CameraTurret.cs.
//
// WHAT THIS IS. Like the 15-seat Ural, this does NOT build a car. It REUSES a
// complete, drivable, networked vanilla vehicle - "uaz-3151_military_spawn"
// (research/resource_paths.tsv; FINDINGS "Fahrzeuge": VehicleGameSystem,
// RCCCarControllerV2, VehicleInventoryManager, VehicleCharacterController,
// VehicleNetworkController, four wheel colliders, LODGroup) - and does exactly
// four things to it:
//
//   1. SEATS. Cut "SeatPoints" down to THREE children: the vanilla driver, the
//      vanilla co-driver, and one added NDR_TechnicalGunner on the rear deck.
//      VehicleGameSystem::InitCar sizes Passengers from SeatPoints.childCount
//      and SitToPassengerPlace reads SeatPoints.GetChild(i) once (RE 18.1 and
//      18.7), so the seat table IS the child list - the same fact the T-72 and
//      the Ural already stand on. Tank.Sitze is the precedent for removing
//      children; UralTruck.AddSeats for adding them.
//   2. THE GUN. A pintle mount is built on the rear deck as three nested
//      transforms - base (frame correction and model scale), mount (yaw) and
//      gun (elevation). The geometry is generated, and a shipped model wins
//      whenever one is dropped into assets\ (see TechnicalModel).
//   3. FRAGILITY. CarSpawn.Prepare hands every mod vehicle Durability 2000,
//      which is BTR armour. The technical is capped at 150 - the vanilla
//      VAZ-1111, the weakest car in the game (FINDINGS "Fahrzeuge"). The cap is
//      re-applied on every scan, because Durability can be written after a
//      rebuild (InitCar runs after the network rebuild but before the local
//      one), and clamping DOWNWARDS can never undo damage.
//   4. NAME. The instance is marked "_TECHNICAL". The marker deliberately
//      contains neither "btr-80a" (VehicleArmor.IstApc) nor "_T72"
//      (Tank.IstPanzer), so the technical keeps vanilla one-hit explosion
//      behaviour and the BTR gunner never attaches itself to it.
//
// THE GUNNER. Seat 2 is a standing place, not a bench. Whoever stands in it:
//
//   * Stands. The seat point is measured for a man on his feet, and the game's
//     seated pose is replaced by its on-foot one for as long as he is in that
//     place - not only while he holds the gun, because the place is where he
//     stands and the gun is only what he does there.
//   * Has his HANDS ON THE SPADE GRIPS. The game has no animation for a man at
//     a pintle mount, so the arms are solved instead: two bones a side, aimed
//     at the grips the gun model actually has, once a frame in LateUpdate,
//     after the animator wrote the character and before anything is drawn. The
//     same trick the game plays on its own NPCs, whose rifles are aimed by an
//     IK solver on MainChar_HandR and not by a clip. It runs for the local
//     gunner and for every remote one, so the man on the truck you are looking
//     at holds his gun too.
//
// THE PLACE IS THE GUN. Whoever is in seat 2 mans it by himself - the same
// field report of 2026-09-18 said "man kann auf dem gunner sitz weder aimen
// noch schiessen noch nachladen", and that is what a station you have to know
// an undocumented key for looks like from the inside. G still lets go of it
// (and moves back to a front seat), a deliberate stand-down is not overridden,
// and the screen says so either way. TechnicalGun/AutoMan turns it off.
//
// AMMUNITION IS A BELT, NOT A TAP ON THE INVENTORY. A reload pulls BeltRounds
// rounds out of the trunk, backpack and vest at once, takes ReloadSeconds with
// the game's own progress bar, and only then does the gun fire - R reloads by
// hand, an empty belt reloads itself on the trigger, and the rounds left are
// under the crosshair.
//
// While the gun is MANNED on top of that:
//
//   * The view is third person, over the gunner's shoulder, along the barrel -
//     an ordinary non-scoped aim, only further back and higher, sitting on the
//     gun instead of on the player. The field of view is NOT touched, so it
//     looks like the rest of the game and not like an optic.
//   * The gun follows the gunner, not a network message. The local gunner turns
//     the mount with the mouse AND his own body is turned onto the same bearing;
//     on every other client the mount is slewed onto the bearing of whoever sits
//     in seat 2. The body rotation of a seated player is already synchronized by
//     the game, so the machine gun turns with the man for everybody without a
//     single new Photon event. Elevation rides on the same bearing: whatever
//     tilt the body carries is put on the barrel, and a seated body normally
//     carries none - so a remote gun usually reads level. That is the one thing
//     this simplification costs, and it costs nothing at all on a client whose
//     body tilt the game does carry.
//   * Recoil is deliberately tiny and the damage is deliberately BELOW the BTR
//     autocannon: 85 against 120 per shot, and no anti-vehicle path at all
//     (VehicleArmor.GunHit is NOT called). It is an anti-personnel machine gun,
//     so armoured vehicles shrug it off exactly as they shrug off rifle fire.
//
// WHERE THE STATION STANDS (changed 2026-09-18 after the field report
// technicalbug.png: "das mg schwebt, der spieler steht nicht richtig dran").
// It used to be a fraction of the measured bounding box, with the height
// fraction at 1.00 - the TOP of that box. A bounding box says nothing about
// what is actually there: its top is the highest point of the whole vehicle,
// aerial and roof rack included, so gun and gunner ended up standing in mid-air
// above the cabin. The station is now derived from the donor's OWN REAR SEAT
// instead - the rear-most seat point this rebuild removes anyway:
//
//   * its height plus the sit pose's measured foot offset (FeetAboveSeat) is
//     the vehicle FLOOR, because the game itself seats a man there;
//   * the man stands on that floor at that seat's position;
//   * the pintle stands on the same floor, one arm's length in front of him
//     (TechnicalModel.StandOff, in metres, measured on the gun that is really
//     mounted), so his hands are AT the grips by construction.
//
// The old fractions remain as the fallback for a donor with no rear seat, and
// everything derived is logged once per instance. Distances that measure a
// PERSON (the arm, the pintle height) are metres and are converted with the
// vehicle's own units-per-metre; distances that measure the VEHICLE are
// fractions of its own measured size, because the vehicle models are not metric
// (the BTR's 2.9 m track measures +-3.47 model units).
//
// The exact standing position, the pose and the camera distance stay in-game
// acceptance items.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. This file is ASCII ONLY and has no BOM: it is machine-written and
// build.ps1 requires BOM-less sources, so every player-facing string with real
// Cyrillic lives in the UTF-8 file RevivalUralTruck.cs (class TechnicalText),
// the same split RevivalArtyBattery.cs and RevivalMortar.cs use.
//
// SEAMS OUTSIDE THIS FILE (all marked there):
//   RevivalPlugin.cs         BindConfig / Install / Update(Tick) / OnGUI(Draw)
//                            / LateUpdate(LateFrame - the arms and every other
//                            client's gun, which must be written AFTER the
//                            game's animator and therefore cannot live in Tick).
//   RevivalUralTruck.cs      VehicleRegistry entry "technical" + TechnicalText.
//   Revival.CameraTurret.cs  CameraOwner.GunTruck and its LateTick dispatch.
//   Revival.Tank.cs          CarSpawn.SpawnPrefab - used unchanged.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The vehicle itself: three seats, the pintle mount, the durability cap,
    /// the admin spawn key and a structural self-check. Everything that drives,
    /// collides, burns and synchronizes is the untouched donor prefab.
    /// </summary>
    public static class Technical
    {
        /// <summary>Spawnable prefab name (research/resource_paths.tsv:
        /// vehiclespawn/uaz-3151_military_spawn). Lower case, as the resource
        /// path stores it.</summary>
        public const string Prefab = "uaz-3151_military_spawn";

        /// <summary>Marker appended to a rebuilt instance's name. Contains
        /// neither "btr-80a" nor "_T72" on purpose - see the file header.</summary>
        public const string Marke = "_TECHNICAL";

        public const string SeatName = "NDR_TechnicalGunner";
        public const string BaseName = "NDR_TechnicalMountBase";
        public const string MountName = "NDR_TechnicalMount";
        public const string GunName = "NDR_TechnicalGun";

        /// <summary>Driver, co-driver, gunner. The gunner is always last, so
        /// its index is the seat count minus one.</summary>
        public const int SeatTotal = 3;
        public const int GunnerSeat = 2;

        /// <summary>Length of the donor in metres (UAZ-3151, 4.03 m). The ONLY
        /// real-world number in this file: everything else is expressed as a
        /// fraction of the donor's own measured bounds, and this one turns those
        /// model units into metres so the gun and the camera have a human
        /// size.</summary>
        const float DonorLengthMetres = 4.03f;

        /// <summary>
        /// The one measured constant of the game's own sit pose: how far ABOVE
        /// a seat point the seated man's feet come to rest, in the game's world
        /// units. VehicleGameSystem::SitToPassengerPlace only copies position
        /// and rotation onto the player root; the animation then floats the
        /// whole body above it (research/tank_crew_check.py, read off three
        /// vanilla cabins: VAZ-1111, ZAZ-968, UAZ-3151). It is a property of the
        /// CHARACTER and not of any vehicle, which is why it may be a constant
        /// here while every vehicle number is measured - and it is the reason a
        /// seat point is NOT the floor: a man stood at his seat point sinks to
        /// the knees, a man stood this much higher stands ON it.
        /// </summary>
        const float FeetAboveSeat = 1.85f;

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<string> CfgKey;
        public static ConfigEntry<float> CfgDistance;
        public static ConfigEntry<float> CfgDurability;
        public static ConfigEntry<float> CfgMountBack;
        public static ConfigEntry<float> CfgMountUp;
        public static ConfigEntry<float> CfgMountSide;
        public static ConfigEntry<float> CfgSeatBack;
        public static ConfigEntry<float> CfgSeatDrop;
        public static ConfigEntry<float> CfgStandDrop;
        public static ConfigEntry<float> CfgModelScale;
        public static ConfigEntry<bool> CfgShield;

        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;

        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("Technical", "Enabled", true,
                "Die Technische (Gun Truck) aktivieren: Registrierung als "
                + "fahrbares Fahrzeug, das MG auf der Ladeflaeche und die "
                + "Spawn-Taste unten.");
            CfgKey = cfg.Bind("Technical", "Key", "F12",
                "Taste, die eine Technische vor dem Spieler absetzt "
                + "(Adminwerkzeug, nur Masterclient). F12 ist die letzte freie "
                + "F-Taste: F4..F11 gehoeren Editor, Aufnahme, Overlay, "
                + "Fahrzeug, Admin, Panzer, Arena/Ural und Patrouille.");
            CfgDistance = cfg.Bind("Technical", "Distance", 8f,
                "Abstand der gespawnten Technischen vor der Kamera in Metern.");
            CfgDurability = cfg.Bind("Technical", "Durability", 150f,
                "Trefferpunkte der Technischen. 150 ist der Wert des VAZ-1111, "
                + "des schwaechsten Autos im Spiel (FINDINGS: Fahrzeuge). "
                + "CarSpawn.Prepare setzt jedem Mod-Fahrzeug 2000 - das ist "
                + "BTR-Panzerung und waere fuer eine Technische voellig "
                + "falsch. Der Wert wird bei jedem Durchlauf nach UNTEN "
                + "gedeckelt, nie erhoeht, kann also keinen Schaden ruecknehmen.");
            CfgMountBack = cfg.Bind("Technical", "MountBack", 0.42f,
                "RUECKFALL: wo die Lafette auf dem Fahrzeug steht, als Anteil "
                + "der Fahrzeuglaenge vom HECK aus gemessen. 0 ist die "
                + "Heckkante, 1 die Bugkante. Normalerweise wird der Standplatz "
                + "aus dem hintersten Sitz des Spenders abgeleitet (siehe "
                + "Stehplatz im Quelltext) und dieser Wert gar nicht benutzt - "
                + "er greift nur bei einem Spender ohne Ruecksitz. Alle Masse "
                + "dieser Sektion sind Anteile der gemessenen Modellgroesse und "
                + "keine Meter: die Fahrzeugmodelle des Spiels sind nicht "
                + "metrisch.");
            // Renamed from "MountUp" on purpose. The old key defaulted to 1.00 -
            // the top of the measured box, i.e. the roof edge or whatever aerial
            // stands above it - and that is exactly the reported bug: MG and
            // Schuetze standing in mid-air over the cabin. A changed default
            // would not have reached anybody, because BepInEx keeps the value
            // already written in the config file; a changed KEY does.
            CfgMountUp = cfg.Bind("Technical", "MountUpFallback", 0.20f,
                "RUECKFALL: Hoehe der Standflaeche als Anteil der Fahrzeug"
                + "hoehe, vom tiefsten Punkt der Karosserie aus. Wird nur "
                + "benutzt, wenn der Spender keinen Sitz hat, aus dem sich der "
                + "Boden ableiten laesst. 1.0 waere die Dachkante - dort steht "
                + "nichts, deshalb ist die Vorgabe der Wagenboden.");
            CfgMountSide = cfg.Bind("Technical", "MountSide", 0.00f,
                "Seitenversatz der Lafette als Anteil der Fahrzeugbreite. "
                + "0 heisst mittig.");
            CfgSeatBack = cfg.Bind("Technical", "GunnerBack", 0.10f,
                "MINDESTABSTAND: wie weit der Schuetze hinter der Lafette "
                + "steht, als Anteil der Fahrzeuglaenge. Der wirkliche Abstand "
                + "ist der GROESSERE aus diesem Anteil und der Armlaenge, mit "
                + "der der Mann die Griffe des aufgebauten MG erreicht "
                + "(TechnicalModel.StandOff, in Metern am gemessenen Modell). "
                + "So steht er nie IM Geschuetz, egal wie gross das Fahrzeug "
                + "ist und welches MG-Modell darauf sitzt.");
            CfgSeatDrop = cfg.Bind("Technical", "GunnerDrop", 0.37f,
                "Wie weit der Sitzpunkt des Schuetzen UNTER der Standflaeche "
                + "liegt, als Anteil der Fahrzeughoehe. Der Grund steht in "
                + "REVERSE_ENGINEERING: die Haltung des Spiels laesst den "
                + "Koerper ueber dem Sitzpunkt schweben - am UAZ-3151 liegen "
                + "die Fuesse rund 1.85 Modelleinheiten ueber dem Punkt, bei "
                + "5.02 Einheiten Fahrzeughoehe also 0.37 davon. Gilt NUR fuer "
                + "den sitzenden Schuetzen, also wenn TechnicalGun/StandPose "
                + "aus ist; steht er, ist GunnerStandDrop der Wert.");
            CfgStandDrop = cfg.Bind("Technical", "GunnerStandDrop", 0.00f,
                "Dasselbe wie GunnerDrop, aber fuer den STEHENDEN Schuetzen "
                + "(TechnicalGun/StandPose an, die Vorgabe). Eine Figur auf "
                + "den Beinen hat ihren Nullpunkt an den Fuessen und braucht "
                + "deshalb keine Absenkung - wer sie trotzdem absenkt, steckt "
                + "bis zu den Knien in der Ladeflaeche. Steht der Schuetze im "
                + "Spiel zu hoch oder zu tief, ist das der Wert dafuer; er "
                + "darf auch negativ sein.");
            CfgModelScale = cfg.Bind("Technical", "ModelScale", 1.0f,
                "Zusatzfaktor auf die Groesse des erzeugten MG. Die Grund"
                + "groesse kommt aus der gemessenen Fahrzeuglaenge, dieser "
                + "Faktor ist nur zum Nachjustieren da.");
            CfgShield = cfg.Bind("Technical", "GunShield", true,
                "Ein Schutzschild vor dem MG. Rein optisch - es haelt nichts "
                + "auf, die Technische ist absichtlich ungepanzert.");
        }

        public static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        /// <summary>The drop for a gunner on his feet. Its own key, because it
        /// measures from a different place than the seated one - see the caller.</summary>
        static float StandDrop()
        {
            return CfgStandDrop == null ? 0f : CfgStandDrop.Value;
        }

        public static bool IstTechnical(Transform root)
        {
            if (root == null) return false;
            return root.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------- Umbau

        /// <summary>
        /// The whole rebuild. Idempotent: the local spawn and the network
        /// postfix can both reach a given instance, and a second call is a
        /// no-op. Never throws out - a failure leaves a plain, drivable UAZ.
        /// </summary>
        public static void Umbauen(GameObject car)
        {
            if (car == null) return;

            // The STRUCTURE is built once; the durability cap is not. On the
            // master client the DoInstantiate postfix rebuilds the vehicle
            // BEFORE CarSpawn.Prepare writes its Durability 2000, so this
            // second call - the registry's own Rebuild, which runs right after
            // Prepare - is the first chance to take those 2000 away again.
            // Capping only ever goes downwards (see Deckel), so repeating it
            // can never heal a damaged truck.
            bool fertig = IstTechnical(car.transform);
            if (!fertig)
            {
                try { Aufbauen(car); }
                catch (Exception ex) { RevivalPlugin.L.LogError("Technical, Umbau: " + ex); }

                if (car.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) < 0)
                    car.name = car.name + Marke;
            }

            try { Deckel(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Technical, Trefferpunkte: " + ex); }

            if (fertig) return;
            try { Validate(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Technical, Validierung: " + ex); }
        }

        static void Aufbauen(GameObject car)
        {
            Component vgs;
            Transform seats = FindSeatPoints(car, out vgs);
            if (seats == null)
            {
                RevivalPlugin.L.LogWarning("Technical: SeatPoints fehlt - es bleibt "
                    + "ein gewoehnlicher UAZ ohne MG.");
                return;
            }

            Vector3 min, max;
            if (!Karosserie(car, out min, out max))
            {
                RevivalPlugin.L.LogWarning("Technical: keine Karosserie messbar - "
                    + "ohne Masse kann die Lafette nicht gesetzt werden.");
                return;
            }

            float length = Mathf.Max(0.001f, max.z - min.z);
            float width = Mathf.Max(0.001f, max.x - min.x);
            float height = Mathf.Max(0.001f, max.y - min.y);
            float unitsPerMetre = length / DonorLengthMetres;
            float centreX = (min.x + max.x) * 0.5f;

            // How far the man has to stand behind the pintle axis so that the
            // grips of the mounted gun land in his hands: the grips' own offset
            // (measured on a delivered model, built in on the generated one)
            // plus the length of an arm. In metres, converted with the
            // vehicle's own scale - never a fraction of the vehicle, because a
            // man is the same size on a jeep and on a lorry. The configured
            // fraction stays as a MINIMUM, so it can still push him further
            // back but never into the weapon.
            float abstand = Mathf.Max(CfgSeatBack.Value * length,
                                      TechnicalModel.StandOff() * unitsPerMetre);

            // Everything below is in the ROOT's local space and then converted
            // into whatever frame the target parent happens to be, so neither a
            // rotated chassis node nor a scaled prefab can move the gun.
            //
            // THE STANDING PLACE IS THE DONOR'S OWN REAR SEAT, not a fraction of
            // the bounding box. The box says nothing about what is actually
            // THERE: its top is the highest point of the whole vehicle, aerial
            // and roof rack included, and standing the station on it put gun and
            // gunner in mid-air over the cabin (field report technicalbug.png,
            // 2026-09-18). A seat the game itself puts a passenger on is by
            // construction inside the body, above the floor and clear of the
            // wheels. The mount then stands one arm's length IN FRONT of it, so
            // the man and his weapon are placed by the same measurement and
            // cannot drift apart.
            float deckY, standZ;
            string herkunft;
            Vector3 seatPoint;
            if (Stehplatz(car.transform, seats, out seatPoint))
            {
                deckY = seatPoint.y + FeetAboveSeat / Hoehenmass(car.transform);
                standZ = seatPoint.z;
                herkunft = "aus dem hintersten Sitz des Spenders";
            }
            else
            {
                deckY = min.y + CfgMountUp.Value * height;
                standZ = min.z + CfgMountBack.Value * length - abstand;
                herkunft = "aus den Anteilen MountUpFallback/MountBack";
            }

            // Two clamps, so a donor with a surprising seat can still only be
            // wrong by a little: the deck stays inside the measured body, and
            // neither the man nor the gun can hang off either end of it.
            float deckClamped = Mathf.Clamp(deckY, min.y + 0.02f * height,
                                            min.y + 0.95f * height);
            float hinten = min.z + 0.04f * length;
            float vorn = Mathf.Max(hinten, max.z - 0.04f * length - abstand);
            float standClamped = Mathf.Clamp(standZ, hinten, vorn);
            if (Mathf.Abs(deckClamped - deckY) > 0.001f
                || Mathf.Abs(standClamped - standZ) > 0.001f)
                RevivalPlugin.L.LogWarning("Technical: der abgeleitete Standplatz "
                    + "lag ausserhalb der Karosserie (y " + deckY.ToString("0.00")
                    + " -> " + deckClamped.ToString("0.00") + ", z "
                    + standZ.ToString("0.00") + " -> " + standClamped.ToString("0.00")
                    + ") und wurde hineingezogen.");
            deckY = deckClamped;
            standZ = standClamped;

            float x = centreX + CfgMountSide.Value * width;
            Vector3 mountInRoot = new Vector3(x, deckY, standZ + abstand);
            // TWO drops, because there are two poses and they do not measure
            // from the same place. The game's SIT clip floats the body high
            // above the seat root (FeetAboveSeat), so a seated gunner has to be
            // dropped by that much to stand on the deck. A man on his FEET has
            // his root AT his feet, so he needs no drop at all - dropping him
            // anyway would sink him into the truck bed to the knees. Which one
            // applies is decided by the same switch that decides the pose.
            bool steht = TechnicalGun.CfgStandPose == null
                         || TechnicalGun.CfgStandPose.Value;
            float drop = steht ? StandDrop() : CfgSeatDrop.Value;
            Vector3 seatInRoot = new Vector3(x, deckY - drop * height, standZ);

            Sitze(car, vgs, seats, seatInRoot);
            Karosse(car, seats, min, max);
            Lafette(car, seats, mountInRoot, unitsPerMetre);

            RevivalPlugin.L.LogInfo("Technical: Karosserie x " + min.x.ToString("0.00")
                + ".." + max.x.ToString("0.00") + ", y " + min.y.ToString("0.00")
                + ".." + max.y.ToString("0.00") + ", z " + min.z.ToString("0.00")
                + ".." + max.z.ToString("0.00") + " (L " + length.ToString("0.00")
                + ", B " + width.ToString("0.00") + ", H " + height.ToString("0.00")
                + ", " + unitsPerMetre.ToString("0.000") + " Einheiten je Meter). "
                + "Standflaeche y " + deckY.ToString("0.00") + " " + herkunft
                + ", Armabstand " + abstand.ToString("0.00") + " Einheiten ("
                + TechnicalModel.StandOff().ToString("0.00") + " m). Lafette "
                + mountInRoot + ", Schuetze " + seatInRoot
                + " (Absenkung " + drop.ToString("0.00") + ", "
                + (steht ? "stehend" : "sitzend") + ").");

            // A donor with a roof over its rear places has less room above the
            // floor than a pintle needs. That is a property of the DONOR, not a
            // fault here, and the alternative - standing the station on the roof
            // - is the bug this rebuild was changed to fix. It is logged so that
            // a report of "the barrel goes through the roof" has its answer in
            // the file already.
            float kopfraum = max.y - deckY;
            float braucht = TechnicalModel.PivotHeight * unitsPerMetre;
            if (kopfraum < braucht)
                RevivalPlugin.L.LogInfo("Technical: ueber der Standflaeche liegen "
                    + kopfraum.ToString("0.00") + " Einheiten, die Lafette ist "
                    + braucht.ToString("0.00") + " hoch - das MG ragt also durch "
                    + "das Dach des Spenders. Das ist gewollt: es steht damit auf "
                    + "dem Wagenboden wie der Schuetze, statt ueber dem Dach zu "
                    + "schweben.");
        }

        /// <summary>
        /// The donor's own standing place: the rear-most seat point this rebuild
        /// is about to remove, in the ROOT's local space. Returns false for a
        /// donor with nothing but the two front places, and then the caller
        /// falls back to the configured fractions.
        ///
        /// Why a seat and not the mesh: the seat is the one point on the vehicle
        /// the GAME itself guarantees a man fits on. Its height is the floor
        /// (plus the sit pose's own offset, see FeetAboveSeat), its length
        /// position is behind the cabin, and both follow a different donor
        /// without a single number changing here.
        /// </summary>
        static bool Stehplatz(Transform root, Transform seats, out Vector3 point)
        {
            point = Vector3.zero;
            if (root == null || seats == null) return false;

            int keep = SeatTotal - 1;                 // driver + co-driver stay
            int seen = 0;
            bool any = false;
            for (int i = 0; i < seats.childCount; i++)
            {
                Transform c = seats.GetChild(i);
                if (c == null || c.name == SeatName) continue;
                seen++;
                if (seen <= keep) continue;           // a front place, not ours
                Vector3 p = root.InverseTransformPoint(c.position);
                if (!any || p.z < point.z) { point = p; any = true; }
            }
            return any;
        }

        /// <summary>
        /// World units per ROOT-LOCAL unit along y. Everything measured in this
        /// class is root-local, while <see cref="FeetAboveSeat"/> is a world
        /// measurement of the character - so it has to be divided by this before
        /// the two can be added. It is 1 for every unscaled prefab and exists
        /// for the one that is not.
        /// </summary>
        static float Hoehenmass(Transform root)
        {
            if (root == null) return 1f;
            float s = Mathf.Abs(root.lossyScale.y);
            return s < 0.0001f ? 1f : s;
        }

        /// <summary>
        /// Three seats: the two vanilla front places stay exactly where the game
        /// put them, everything behind them is removed, and one standing place
        /// is appended at the gun. Removing is Tank.Sitze's pattern - SetParent
        /// first, because Destroy only takes effect at the end of the frame and
        /// childCount would still count the seat until then.
        /// </summary>
        static void Sitze(GameObject car, Component vgs, Transform seats,
                          Vector3 seatInRoot)
        {
            int before = seats.childCount;

            List<Transform> vorn = new List<Transform>();
            Transform gunner = null;
            for (int i = 0; i < seats.childCount; i++)
            {
                Transform c = seats.GetChild(i);
                if (c == null) continue;
                if (c.name == SeatName) { gunner = c; continue; }
                vorn.Add(c);
            }

            int keep = SeatTotal - 1;                 // driver + co-driver
            for (int i = vorn.Count - 1; i >= keep; i--)
            {
                vorn[i].SetParent(null, false);
                UnityEngine.Object.Destroy(vorn[i].gameObject);
                vorn.RemoveAt(i);
            }

            if (gunner == null)
            {
                GameObject go = new GameObject(SeatName);
                go.transform.SetParent(seats, false);
                gunner = go.transform;
            }
            gunner.SetAsLastSibling();
            gunner.position = car.transform.TransformPoint(seatInRoot);
            gunner.rotation = car.transform.rotation;
            gunner.localScale = Vector3.one;

            // InitCar may have run already (local spawn) or may still be coming
            // (network rebuild). Sizing Passengers here is correct in the first
            // case and harmless in the second, where InitCar sizes it again from
            // the same child count.
            FieldInfo fPass = AccessTools.Field(vgs.GetType(), "Passengers");
            if (fPass != null) fPass.SetValue(vgs, new GameObject[seats.childCount]);

            RevivalPlugin.L.LogInfo("Technical: Sitze " + before + " -> "
                + seats.childCount + " (Fahrer, Beifahrer, Stehplatz am MG), "
                + "Passengers neu gesetzt.");
        }

        /// <summary>
        /// The pintle: base (frame correction plus model scale), mount (yaw) and
        /// gun (elevation). The base absorbs any rotation between the parent
        /// node and the vehicle root, so the mount's yaw is a pure angle around
        /// the vehicle's own up axis and zero really is straight ahead.
        /// </summary>
        static void Lafette(GameObject car, Transform seats, Vector3 mountInRoot,
                            float unitsPerMetre)
        {
            Transform root = car.transform;
            // Share the frame the seats live in (the chassis), so gun and gunner
            // move together under suspension and body animation.
            Transform parent = seats.parent != null ? seats.parent : root;

            Transform basis = parent.Find(BaseName);
            if (basis == null)
            {
                GameObject go = new GameObject(BaseName);
                go.transform.SetParent(parent, false);
                basis = go.transform;
            }
            basis.position = root.TransformPoint(mountInRoot);
            basis.rotation = root.rotation;
            float s = Mathf.Max(0.01f, unitsPerMetre
                * (CfgModelScale == null ? 1f : CfgModelScale.Value));
            // localScale is relative to the parent, which already carries the
            // prefab's own scale - so this is exactly "metres of this vehicle".
            basis.localScale = new Vector3(s, s, s);

            if (basis.Find(MountName) != null) return;    // already built

            Transform gun;
            GameObject mount = TechnicalModel.Build(CfgShield == null
                                                    || CfgShield.Value, out gun);
            if (mount == null) return;
            mount.name = MountName;
            gun.name = GunName;
            mount.transform.SetParent(basis, false);
            mount.transform.localPosition = Vector3.zero;
            mount.transform.localRotation = Quaternion.identity;
            mount.transform.localScale = Vector3.one;
        }

        public const string BodyName = "NDR_TechnicalBody";

        /// <summary>
        /// Put a DELIVERED body model in the donor's place, when one has been
        /// built into assets\technical_body.ndmesh. Without that file this does
        /// nothing at all and the technical stays a UAZ with a machine gun -
        /// which is a complete, playable vehicle, so the model is an upgrade and
        /// never a requirement.
        ///
        /// The model is fitted into the donor's OWN measured box: scaled by the
        /// ratio of the two lengths and stood on the donor's underside. That is
        /// why no numbers from the model appear anywhere in this file - a body
        /// that is 4 m long in Blender and one that is 400 units long both land
        /// on the same truck.
        ///
        /// WHY THE LODGroup IS SWITCHED OFF. The group owns Renderer.enabled and
        /// rewrites it whenever the camera distance crosses a threshold, so a
        /// body that is merely disabled comes back a second later. With the
        /// group off, the flags set here are the last word. The price is that
        /// this one vehicle has no distance LODs; the alternative - a technical
        /// that turns back into a UAZ at thirty metres - is worse.
        ///
        /// COLLISION IS UNTOUCHED. Only renderers are switched; the donor's
        /// "Colliders" node, its wheel colliders and its damage volumes stay
        /// exactly as they are, so a delivered model can never make the vehicle
        /// undrivable or unhittable.
        /// </summary>
        static void Karosse(GameObject car, Transform seats, Vector3 min, Vector3 max)
        {
            Mesh body = TechnicalModel.Body();
            if (body == null) return;

            Transform root = car.transform;
            Transform parent = seats.parent != null ? seats.parent : root;
            if (parent.Find(BodyName) != null) return;    // already swapped

            int groups = 0;
            Type lodType = RevivalPlugin.TypeByName("UnityEngine.LODGroup");
            if (lodType == null) lodType = RevivalPlugin.TypeByName("LODGroup");
            if (lodType != null)
            {
                Component[] all = car.GetComponentsInChildren(lodType, true);
                PropertyInfo pEnabled = lodType.GetProperty("enabled",
                    BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null || pEnabled == null || !pEnabled.CanWrite) continue;
                    pEnabled.SetValue(all[i], false, null);
                    groups++;
                }
            }

            int hidden = 0;
            MeshFilter[] filters = car.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || IstRad(mf.transform) || IstUnser(mf.transform)) continue;
                Renderer r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                hidden++;
            }

            Bounds b = body.bounds;
            float modelLength = Mathf.Max(0.0001f, b.size.z);
            if (b.size.x > b.size.z || b.size.y > b.size.z)
                RevivalPlugin.L.LogWarning("Technical: technical_body.ndmesh ist "
                    + "laengs Z NICHT am groessten (" + b.size
                    + ") - das Modell steht vermutlich quer. Die Achsen gehoeren "
                    + "in technical_build.py (AXES) geradegerueckt, nicht hier.");
            float scale = (max.z - min.z) / modelLength;

            GameObject go = new GameObject(BodyName);
            go.transform.SetParent(parent, false);
            MeshFilter nmf = go.AddComponent<MeshFilter>();
            nmf.mesh = body;
            MeshRenderer nmr = go.AddComponent<MeshRenderer>();
            Material skin = TechnicalModel.BodySkin();
            if (skin != null) nmr.material = skin;

            go.transform.rotation = root.rotation;
            go.transform.localScale = new Vector3(scale, scale, scale);
            // Centre on the donor's box in x and z, and stand the model on the
            // donor's underside in y.
            Vector3 wanted = new Vector3(
                (min.x + max.x) * 0.5f - b.center.x * scale,
                min.y - b.min.y * scale,
                (min.z + max.z) * 0.5f - b.center.z * scale);
            go.transform.position = root.TransformPoint(wanted);

            RevivalPlugin.L.LogInfo("Technical: geliefertes Modell eingesetzt - "
                + hidden + " Karosserieteile ausgeblendet, " + groups
                + " LODGroups abgeschaltet, Modellmasse " + b.size
                + ", Faktor " + scale.ToString("0.0000") + ".");

            if (TechnicalModel.GunIsShipped()) return;
            RevivalPlugin.L.LogWarning("Technical: es liegt eine Karosserie, aber "
                + "kein technical_mg.ndmesh. Wenn das Modell sein MG selbst "
                + "mitbringt, steht es jetzt FEST auf der Karosserie und das "
                + "schwenkbare MG wird zusaetzlich darueber gebaut. Abhilfe: in "
                + "technical_build.py GUN_MATERIALS setzen und neu bauen, dann "
                + "wird das MG des Modells selbst geschwenkt.");
        }

        /// <summary>Is this transform something the rebuild itself added?</summary>
        static bool IstUnser(Transform t)
        {
            while (t != null)
            {
                if (t.name == BaseName || t.name == BodyName) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// The body's bounds in the vehicle root's LOCAL space, from the meshes
        /// themselves. Wheels are left out: they are the only part that sits
        /// below the body and turns, and including them would push the deck
        /// height down by a wheel radius. Mesh-local corners are transformed
        /// through the root, so a rotated or scaled instance measures the same
        /// as one standing at the origin - a world AABB would not.
        /// </summary>
        static bool Karosserie(GameObject car, out Vector3 min, out Vector3 max)
        {
            min = Vector3.zero;
            max = Vector3.zero;
            bool any = false;
            Transform root = car.transform;

            MeshFilter[] all = car.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < all.Length; i++)
            {
                MeshFilter mf = all[i];
                if (mf == null || mf.sharedMesh == null) continue;
                if (IstRad(mf.transform) || IstUnser(mf.transform)) continue;

                Bounds b = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = new Vector3(
                        (c & 1) == 0 ? b.min.x : b.max.x,
                        (c & 2) == 0 ? b.min.y : b.max.y,
                        (c & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 p = root.InverseTransformPoint(
                        mf.transform.TransformPoint(corner));
                    if (!any) { min = p; max = p; any = true; continue; }
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            return any;
        }

        static bool IstRad(Transform t)
        {
            while (t != null)
            {
                string n = t.name;
                if (n.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("tire", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("tyre", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                t = t.parent;
            }
            return false;
        }

        // ------------------------------------------------------ Trefferpunkte

        /// <summary>
        /// Cap the technical at the weakest vanilla car. Only ever downwards, so
        /// a damaged vehicle is never healed and the call is safe to repeat on
        /// every scan - which it has to be, because Durability is written by
        /// CarSpawn.Prepare, by InitCar and by the network controller, and the
        /// rebuild does not run last on every path.
        /// </summary>
        internal static void Deckel(GameObject car)
        {
            if (car == null) return;
            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (vgsType == null) return;
            Component vgs = car.GetComponent(vgsType);
            if (vgs == null) return;
            Deckel(vgs);
        }

        internal static void Deckel(Component vgs)
        {
            if (vgs == null || CfgDurability == null) return;
            float cap = CfgDurability.Value;
            if (cap <= 0f) return;
            FieldInfo f = AccessTools.Field(vgs.GetType(), "Durability");
            if (f == null || f.FieldType != typeof(float)) return;
            float have = (float)f.GetValue(vgs);
            if (have <= cap) return;
            f.SetValue(vgs, cap);
            RevivalPlugin.L.LogInfo("Technical: Trefferpunkte " + have.ToString("0")
                + " -> " + cap.ToString("0") + " (gedeckelt auf den VAZ-1111).");
        }

        // ------------------------------------------------------------- Pruefung

        /// <summary>
        /// Structural self-check: three uniquely named seats, a sized Passengers
        /// array, the mount and gun transforms, the capped durability and the
        /// donor's own networking, physics and LOD. Logs a PASS/FAIL summary.
        /// Everything that needs eyes - where the gunner stands, how the gun
        /// sits, how it drives - is an in-game acceptance item.
        /// </summary>
        public static bool Validate(GameObject car)
        {
            if (car == null) { RevivalPlugin.L.LogWarning("Technical-Check: kein Fahrzeug."); return false; }

            List<string> fail = new List<string>();

            Component vgs;
            Transform seats = FindSeatPoints(car, out vgs);
            int seatN = seats == null ? 0 : seats.childCount;
            if (seats == null) fail.Add("SeatPoints fehlt");
            else
            {
                if (seatN != SeatTotal)
                    fail.Add("Sitzzahl " + seatN + " statt " + SeatTotal);
                HashSet<string> names = new HashSet<string>();
                for (int i = 0; i < seatN; i++)
                {
                    Transform c = seats.GetChild(i);
                    if (c == null) { fail.Add("Sitz " + i + " ist null"); continue; }
                    if (!names.Add(c.name)) fail.Add("doppelter Sitzname: " + c.name);
                }
                if (seatN > 0 && seats.GetChild(seatN - 1) != null
                    && seats.GetChild(seatN - 1).name != SeatName)
                    fail.Add("der letzte Sitz ist nicht " + SeatName);
            }

            if (vgs == null) fail.Add("VehicleGameSystem fehlt");
            else
            {
                FieldInfo fPass = AccessTools.Field(vgs.GetType(), "Passengers");
                Array pass = fPass == null ? null : fPass.GetValue(vgs) as Array;
                if (pass == null) fail.Add("Passengers-Array fehlt");
                else if (pass.Length != seatN)
                    fail.Add("Passengers-Laenge " + pass.Length + " != Sitze " + seatN);

                FieldInfo fDur = AccessTools.Field(vgs.GetType(), "Durability");
                if (fDur != null && fDur.FieldType == typeof(float)
                    && CfgDurability != null
                    && (float)fDur.GetValue(vgs) > CfgDurability.Value + 0.5f)
                    fail.Add("Trefferpunkte ueber dem Deckel");
            }

            if (GunOf(car.transform) == null) fail.Add(GunName + " fehlt");
            if (MountOf(car.transform) == null) fail.Add(MountName + " fehlt");

            if (!HasComponent(car, "PhotonView")) fail.Add("PhotonView fehlt (Netz)");
            if (!HasComponent(car, "VehicleNetworkController"))
                fail.Add("VehicleNetworkController fehlt (Netz)");
            if (!HasComponent(car, "RCCCarControllerV2"))
                fail.Add("RCCCarControllerV2 fehlt (Fahrphysik)");

            if (!VehicleRegistry.Contains("technical")) fail.Add("nicht in VehicleRegistry");

            if (fail.Count == 0)
            {
                RevivalPlugin.L.LogInfo("Technical-Check: PASS - " + seatN
                    + " Sitze, MG aufgebaut, Trefferpunkte gedeckelt, "
                    + "Netz/Physik/Registry vorhanden.");
                return true;
            }
            RevivalPlugin.L.LogWarning("Technical-Check: FAIL - "
                + string.Join("; ", fail.ToArray()));
            return false;
        }

        // ------------------------------------------------------------- Zugriff

        internal static Transform FindSeatPoints(GameObject car, out Component vgs)
        {
            vgs = null;
            if (car == null) return null;
            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (vgsType == null) return null;
            vgs = car.GetComponent(vgsType);
            if (vgs == null) return null;
            FieldInfo fSeats = AccessTools.Field(vgsType, "SeatPoints");
            Transform seats = fSeats == null ? null : fSeats.GetValue(vgs) as Transform;
            if (seats == null)
            {
                Transform chassis = car.transform.Find("Chassis");
                if (chassis != null) seats = chassis.Find("SeatPoints");
            }
            return seats;
        }

        /// <summary>The yaw pivot of a rebuilt technical, or null.</summary>
        internal static Transform MountOf(Transform root)
        {
            if (root == null) return null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == MountName) return all[i];
            return null;
        }

        /// <summary>The elevation pivot of a rebuilt technical, or null.</summary>
        internal static Transform GunOf(Transform root)
        {
            Transform mount = MountOf(root);
            return mount == null ? null : mount.Find(GunName);
        }

        static bool HasComponent(GameObject car, string typeName)
        {
            Type t = RevivalPlugin.TypeByName(typeName);
            if (t == null) return false;
            return car.GetComponentInChildren(t, true) != null;
        }

        // ------------------------------------------------------------- Laufzeit

        public static void Install(Harmony harmony)
        {
            if (!Enabled) { RevivalPlugin.L.LogInfo("Technical: abgeschaltet (Technical/Enabled)."); return; }
            VehicleRegistry.EnsureBuilt();
            TechnicalNetwork.Install(harmony);
            RevivalPlugin.L.LogInfo("Technical: Gun Truck registriert (Spawntaste "
                + CfgKey.Value + ", Prefab " + Prefab + ", " + SeatTotal
                + " Plaetze, " + CfgDurability.Value.ToString("0")
                + " Trefferpunkte).");
        }

        public static void Tick()
        {
            try
            {
                if (!Enabled) return;
                if (Input.GetKeyDown(Key())) SpawnInFront();
                Pflege();
                TechnicalGun.Tick();
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Technical-Tick: " + ex); }
        }

        public static void Draw()
        {
            try
            {
                if (!Enabled) return;
                TechnicalGun.Draw();
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Technical-Draw: " + ex.Message); }
        }

        /// <summary>
        /// Everything that has to be written AFTER the game's own animation ran:
        /// the machine guns of the whole scene and the gunners' arms. Bone
        /// rotations written in Update are overwritten by the animator in the
        /// same frame; written in LateUpdate they are what the camera sees.
        /// Seam: RevivalPlugin.LateUpdate. Separate from
        /// <see cref="TechnicalGun.LateTick"/>, which CameraOwner calls for the
        /// LOCAL gunner's view only.
        /// </summary>
        public static void LateFrame()
        {
            try
            {
                if (!Enabled) return;
                TechnicalGun.LateAll();
                // The gunner's view a SECOND time, from the plugin's own
                // LateUpdate. CameraOwner.LateTick drives it from a postfix on
                // CameraFPSController.LateUpdate, and that hook is installed by
                // Turret.Install - which returns at its first line when the BTR
                // gun is switched off. The technical must not lose its aim
                // because a different feature is disabled. Writing the same
                // transform twice in a frame costs one matrix and is harmless:
                // the value is computed from the gun, not accumulated.
                TechnicalGun.LateTick();
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Technical-LateFrame: " + ex.Message); }
        }

        static float _nextCare;

        /// <summary>
        /// Everything that has to hold for EVERY technical in the scene and not
        /// only for the one the local player sits in: the durability cap, and
        /// the list of gun stations the per-frame work runs on. Runs on the
        /// shared vehicle scan, a few times a second - finding a mount means
        /// walking a whole vehicle hierarchy, which is exactly the kind of work
        /// that must not happen once per frame per vehicle.
        /// </summary>
        static void Pflege()
        {
            if (Time.time < _nextCare) return;
            _nextCare = Time.time + 0.2f;

            Component[] all = VehicleScan.All();
            for (int i = 0; i < all.Length; i++)
            {
                Component vgs = all[i];
                if (vgs == null || !IstTechnical(vgs.transform)) continue;
                Deckel(vgs);
            }
            TechnicalGun.Stations(all);
        }

        internal static string SpawnInFront()
        {
            if (!Enabled) return "Technical is disabled in the configuration.";
            Camera cam = Camera.main;
            if (cam == null) return "No player camera available.";

            Vector3 ahead = cam.transform.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.000001f) ahead = Vector3.forward;
            ahead.Normalize();

            float dist = Mathf.Max(5f, CfgDistance == null ? 8f : CfgDistance.Value);
            Vector3 above = cam.transform.position + ahead * dist + Vector3.up * 30f;
            Vector3 ground;
            GameObject under = Turret.RaycastObject(above, Vector3.down, 200f, out ground);
            if (under == null)
            {
                RevivalPlugin.L.LogWarning("Technical: unter " + above + " ist kein Boden.");
                return "No ground found ahead of the player.";
            }

            Vector3 pos = ground + Vector3.up * 1.6f;
            Quaternion rot = Quaternion.LookRotation(ahead, Vector3.up);
            bool isTank;
            GameObject car = VehicleRegistry.Spawn("technical", pos, rot, out isTank);
            if (car == null) return "Technical spawn failed; see the game log.";
            RevivalPlugin.L.LogInfo("Technical: Gun Truck erzeugt bei " + pos
                + ", Boden \"" + under.name + "\".");
            TechnicalGun.Hinweis(TechnicalText.Spawned(), 4f);
            return TechnicalText.Spawned();
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), CfgKey.Value, true); }
            catch
            {
                _key = KeyCode.F12;
                RevivalPlugin.L.LogWarning("Technical: Taste " + CfgKey.Value
                    + " unbekannt, benutze F12.");
            }
            return _key;
        }
    }

    /// <summary>
    /// Carries the technical decision in Photon's cached scene-instantiation
    /// event (byte key 5), exactly like TankNetwork and UralNetwork. Element
    /// zero stays null - VehicleGameSystem.FindMySpawnPointAndSet unboxes it as
    /// an Int32 spawn-point id and returns early - and element one is free mod
    /// data that Photon caches and replays to late joiners, so creator, remote
    /// players and late joiners all run the same rebuild.
    /// </summary>
    public static class TechnicalNetwork
    {
        const string Marker = "NDR_TECHNICAL_V1";

        public static object[] SpawnData()
        {
            return new object[] { null, Marker };
        }

        static bool IsTechnicalData(object value)
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
                    RevivalPlugin.L.LogWarning("Technical-Netzwerk: NetworkingPeer fehlt.");
                    return;
                }

                MethodInfo target = null;
                MethodInfo[] methods = peer.GetMethods(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo c = methods[i];
                    if (c.Name == "DoInstantiate" && c.ReturnType == typeof(GameObject)
                        && c.GetParameters().Length == 3)
                    { target = c; break; }
                }
                if (target == null)
                {
                    RevivalPlugin.L.LogWarning("Technical-Netzwerk: DoInstantiate fehlt.");
                    return;
                }

                harmony.Patch(target, null,
                    new HarmonyMethod(typeof(TechnicalNetwork).GetMethod("Postfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("Technical-Netzwerk: Spawnmarker aktiv.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Technical-Netzwerk konnte nicht aktiviert werden: " + ex);
            }
        }

        public static void Postfix(object __0, GameObject __result)
        {
            try
            {
                if (__result == null || Technical.IstTechnical(__result.transform)) return;
                System.Collections.IDictionary eventData = __0 as System.Collections.IDictionary;
                if (eventData == null || !eventData.Contains((byte)5)) return;
                if (!IsTechnicalData(eventData[(byte)5])) return;

                Technical.Umbauen(__result);
                RevivalPlugin.L.LogInfo("Technical-Netzwerk: Gun Truck auf diesem "
                    + "Client aufgebaut: " + __result.name + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Technical-Netzwerk, Spawnmarker: " + ex);
            }
        }
    }

    /// <summary>
    /// The man at the gun: seat change, aiming, the third-person view, firing
    /// and the pose. Local to the player who mans it; what the rest of the
    /// server sees comes out of the gunner's own synchronized body bearing (see
    /// <see cref="SlewRemote"/>), not out of a new network message.
    /// </summary>
    public static class TechnicalGun
    {
        public static ConfigEntry<string> CfgKey;
        public static ConfigEntry<float> CfgDamage;
        public static ConfigEntry<float> CfgDelay;
        public static ConfigEntry<float> CfgRange;
        public static ConfigEntry<float> CfgRecoil;
        public static ConfigEntry<float> CfgSensitivity;
        public static ConfigEntry<float> CfgTurnSpeed;
        public static ConfigEntry<float> CfgPitchMin;
        public static ConfigEntry<float> CfgPitchMax;
        public static ConfigEntry<bool> CfgRequireAmmo;
        public static ConfigEntry<int> CfgAmmoId;
        public static ConfigEntry<float> CfgCamBack;
        public static ConfigEntry<float> CfgCamUp;
        public static ConfigEntry<float> CfgCamSide;
        public static ConfigEntry<bool> CfgCrosshair;
        public static ConfigEntry<bool> CfgStandPose;
        public static ConfigEntry<int> CfgStandPoseValue;
        public static ConfigEntry<bool> CfgHands;
        public static ConfigEntry<float> CfgHandFade;
        public static ConfigEntry<float> CfgElbowOut;
        public static ConfigEntry<float> CfgGripLift;
        public static ConfigEntry<bool> CfgAutoMan;
        public static ConfigEntry<int> CfgBelt;
        public static ConfigEntry<float> CfgReload;
        public static ConfigEntry<string> CfgReloadKey;

        public static void BindConfig(ConfigFile cfg)
        {
            CfgKey = cfg.Bind("TechnicalGun", "Key", "G",
                "Taste zum Aufsitzen auf das MG und zum Verlassen. Bewusst "
                + "dieselbe Taste wie am BTR-Geschuetz: es ist derselbe "
                + "Handgriff. Der Zweig hier reagiert nur, wenn der Spieler "
                + "wirklich in einer Technischen sitzt.");
            CfgDamage = cfg.Bind("TechnicalGun", "Damage", 85f,
                "Schaden je Schuss. Bewusst UNTER dem BTR-Bordgeschuetz "
                + "(Turret/Damage 120): das hier ist ein schweres MG und keine "
                + "Maschinenkanone. Auch die Dauerleistung bleibt darunter - "
                + "85 alle 0.11 s gegen 120 alle 0.12 s.");
            CfgDelay = cfg.Bind("TechnicalGun", "FireDelay", 0.11f,
                "Sekunden zwischen zwei Schuessen, rund neun je Sekunde. Jeder "
                + "Schuss nimmt eine Patrone, deshalb ist die Vorgabe fuer "
                + "AmmoItemId der Gurt mit 200 Schuss.");
            CfgRange = cfg.Bind("TechnicalGun", "Range", 600f,
                "Reichweite des Schusses in Welteinheiten. Kuerzer als das "
                + "Bordgeschuetz (900).");
            CfgRecoil = cfg.Bind("TechnicalGun", "Recoil", 0.04f,
                "Grad, die das Rohr je Schuss hochschlaegt. Niedrig, wie "
                + "gefordert: die Waffe haengt in einer Lafette und nicht in "
                + "den Haenden. Bei neun Schuss je Sekunde sind auch 0.04 Grad "
                + "noch gut ein Drittel Grad Wanderung in der Sekunde.");
            CfgSensitivity = cfg.Bind("TechnicalGun", "Sensitivity", 2.2f,
                "Grad Schwenk je Einheit Mausbewegung.");
            CfgTurnSpeed = cfg.Bind("TechnicalGun", "TurnSpeed", 260f,
                "Grad je Sekunde, mit denen die Lafette der Blickrichtung "
                + "nachdreht. Deutlich schneller als ein Turm: ein Mann "
                + "schwenkt ein MG mit den Armen.");
            CfgPitchMin = cfg.Bind("TechnicalGun", "PitchMin", -12f,
                "Tiefster Rohrwinkel in Grad.");
            CfgPitchMax = cfg.Bind("TechnicalGun", "PitchMax", 55f,
                "Hoechster Rohrwinkel in Grad.");
            CfgRequireAmmo = cfg.Bind("TechnicalGun", "RequireAmmo", true,
                "Je Schuss eine Patrone aus dem Kofferraum, sonst aus Rucksack "
                + "und Weste.");
            CfgAmmoId = cfg.Bind("TechnicalGun", "AmmoItemId", 2050,
                "Item-ID der Munition. 2050 ist der Gurtkasten mit 200 Schuss - "
                + "fuer ein MG genau das richtige Item.");
            CfgCamBack = cfg.Bind("TechnicalGun", "CamBack", 2.6f,
                "Wie weit hinter der Waffe die Kamera steht, in Metern. Das "
                + "Bild soll aussehen wie normales Zielen in der dritten "
                + "Person, nur etwas weiter weg.");
            CfgCamUp = cfg.Bind("TechnicalGun", "CamUp", 0.75f,
                "Wie hoch ueber der Waffe die Kamera steht, in Metern.");
            CfgCamSide = cfg.Bind("TechnicalGun", "CamSide", 0.55f,
                "Seitenversatz der Kamera in Metern. Positiv ist rechts - ueber "
                + "die rechte Schulter, wie beim Zielen zu Fuss.");
            CfgCrosshair = cfg.Bind("TechnicalGun", "Crosshair", true,
                "Fadenkreuz in der Bildmitte. Getroffen wird, worauf die "
                + "Bildmitte steht.");
            CfgStandPose = cfg.Bind("TechnicalGun", "StandPose", true,
                "Den Schuetzen STEHEN lassen statt sitzen. "
                + "PlayerVehicleManager::SitToVehicle setzt _inVehiclePose auf "
                + "1 fuer den Fahrer und 2 fuer jeden Mitfahrer; solange das MG "
                + "bedient wird, wird stattdessen StandPoseValue gesetzt. Wenn "
                + "die Figur im Spiel dadurch seltsam steht, hier abschalten - "
                + "dann sitzt der Schuetze wieder, das MG dreht sich aber "
                + "genauso.");
            CfgStandPoseValue = cfg.Bind("TechnicalGun", "StandPoseValue", 0,
                "Der Wert, den _inVehiclePose waehrend des Schiessens bekommt. "
                + "0 ist die Haltung ausserhalb eines Fahrzeugs, also die "
                + "normale Fusshaltung.");
            CfgHands = cfg.Bind("TechnicalGun", "HandsOnGun", true,
                "Die Haende des Schuetzen auf die Griffe des MG legen. Das "
                + "Spiel hat fuer eine Lafette keine eigene Animation, deshalb "
                + "werden die Armknochen NACH der Animation (LateUpdate) auf "
                + "die Griffe gerechnet - fuer den eigenen Schuetzen und fuer "
                + "den jedes Mitspielers. Findet sich das Skelett nicht, "
                + "passiert nichts und es steht einmal im Log.");
            CfgHandFade = cfg.Bind("TechnicalGun", "HandFade", 0.25f,
                "Sekunden, in denen die Haende an die Griffe wandern und "
                + "wieder los. 0 heisst sofort.");
            CfgElbowOut = cfg.Bind("TechnicalGun", "ElbowOut", 0.55f,
                "Wie weit die Ellenbogen nach AUSSEN zeigen, 0 = nur nach "
                + "unten, 1 = waagerecht zur Seite. Nur die Optik.");
            CfgGripLift = cfg.Bind("TechnicalGun", "GripLift", 0f,
                "Zusaetzliche Hoehe der Griffpunkte in Metern, falls die "
                + "Haende im Spiel zu tief oder zu hoch am MG liegen.");
            CfgAutoMan = cfg.Bind("TechnicalGun", "AutoMan", true,
                "Wer auf dem Stehplatz steht, greift das MG von selbst. Der "
                + "Platz IST das Geschuetz: bis 6.25.0 musste man wissen, dass "
                + "dafuer noch eine Taste gedrueckt werden will, und wer das "
                + "nicht wusste, stand an einem MG, das nichts tat (Feldbericht "
                + "technicalbug.png). Mit G laesst man es trotzdem wieder los "
                + "und wechselt nach vorn; wer losgelassen hat, wird nicht "
                + "erneut aufgeschaltet, solange er auf dem Platz bleibt.");
            CfgBelt = cfg.Bind("TechnicalGun", "BeltRounds", 50,
                "Schuss je Gurt. So viele Patronen werden beim Nachladen aus "
                + "Kofferraum, Rucksack und Weste in die Waffe gelegt; danach "
                + "muss neu geladen werden. Das ist die eigentliche Grenze der "
                + "Feuerkraft - der Schaden je Schuss liegt fest unter dem "
                + "BTR-Bordgeschuetz.");
            CfgReload = cfg.Bind("TechnicalGun", "ReloadSeconds", 4.5f,
                "Wie lange das Einlegen eines Gurtes dauert. In dieser Zeit "
                + "schiesst das MG nicht, und das Spiel zeigt seinen eigenen "
                + "Fortschrittsbalken.");
            CfgReloadKey = cfg.Bind("TechnicalGun", "ReloadKey", "R",
                "Taste zum Nachladen von Hand, auch wenn der Gurt noch nicht "
                + "leer ist. Dieselbe Taste wie beim Nachladen zu Fuss.");
        }

        // ------------------------------------------------------------- Zustand

        /// <summary>Owner name of the native reload bar, like the BTR's.</summary>
        const string ReloadOwner = "technical-reload";

        static Component _vgs;              // VehicleGameSystem of our technical
        static Transform _root;
        static Transform _mount, _gun;
        static bool _manning;
        static float _yaw, _pitch;
        static float _nextScan, _nextShot, _nextTry;
        static float _leerGemeldet;
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static KeyCode _reloadKey = KeyCode.None;
        static bool _reloadKeyParsed;
        static bool _atGun;                 // the local player is IN the place
        static bool _standDown;             // ... and let go of the gun on purpose
        static float _nextOffer;
        static int _belt;                   // rounds in the gun
        static int _pendingBelt;            // rounds going in while it reloads
        static float _reloadDone;           // Time.time the belt is in, 0 = idle
        static Texture2D _dot;
        static string _hinweis;
        static float _hinweisBis;
        static bool _camLogged;
        static bool _poseHeld;
        static object _poseOwner;
        static FieldInfo _poseField;
        static object _poseBack;
        static bool _poseWarned;

        public static bool Manning { get { return _manning; } }

        internal static void Hinweis(string text, float sekunden)
        {
            _hinweis = text;
            _hinweisBis = Time.time + sekunden;
        }

        // --------------------------------------------------------------- Frame

        internal static void Tick()
        {
            try
            {
                if (Time.time >= _nextScan)
                {
                    _nextScan = Time.time + 0.4f;
                    Rescan();
                }
                if (_vgs == null)
                {
                    SetManning(false);
                    ReleasePose();
                    _atGun = false;
                    _standDown = false;
                    return;
                }

                // Somebody else may have taken the view back in the meantime -
                // Turret.GetOutPrefix hands the camera to the game the moment
                // ANY vehicle is left, whoever was holding it, so that the HUD
                // survives the switch. Without this the gun would keep swinging
                // and firing for up to one scan interval after that.
                if (_manning && !CameraOwner.Has(CameraOwner.GunTruck))
                {
                    // Through SetManning, not by writing the flag: that is the
                    // one path that also gives the pose and a running reload
                    // back. Releasing a camera we no longer own is a no-op
                    // (CameraOwner.Release checks the holder).
                    SetManning(false);
                    RevivalPlugin.L.LogInfo("Technical-MG: die Kamera wurde "
                        + "anderweitig zurueckgegeben - das MG ist nicht mehr besetzt.");
                    return;
                }

                if (Input.GetKeyDown(Key())) Toggle();

                // The pose belongs to the PLACE, not to the trigger: seat 2 is
                // a standing place on the bed, so whoever is in it stands -
                // whether or not he has taken hold of the gun. The seat point
                // is measured for exactly that (Technical.Aufbauen), so a
                // gunner who only stands there must not sit down and float.
                _atGun = InSeat(Technical.GunnerSeat);
                if (_atGun) HoldPose();
                else
                {
                    ReleasePose();
                    _standDown = false;
                    _offerWarned = false;
                    // Keep the automatic takeover one second in the future for
                    // as long as he is NOT in the place. Getting into a vehicle
                    // switches the game's own camera, and CameraOwner remembers
                    // whichever camera was current when it was asked - taking
                    // the view in the middle of that switch would hold the wrong
                    // one. One second after he is seated, the switch is done.
                    _nextOffer = Time.time + 1f;
                }

                // The place IS the gun: whoever stands in it takes hold of it
                // without being told a key first. See Anbieten.
                if (_atGun && !_manning) Anbieten();

                if (!_manning) return;

                Aim();
                Nachladen();

                if (Input.GetMouseButton(0) && Time.time >= _nextShot
                    && Time.time >= _nextTry)
                    Feuern();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Technical-MG: " + ex);
                SetManning(false);
            }
        }

        /// <summary>Finds the technical the local player is sitting in.</summary>
        static void Rescan()
        {
            // Unity's overloaded == reports a destroyed component as null, so a
            // vehicle that blew up under the gunner drops out here.
            if (_vgs != null)
            {
                if (IntField(_vgs, "_localPlayerPassengerId") >= 0
                    && Technical.IstTechnical(_vgs.transform)) return;
                Clear();
            }

            Component[] all = VehicleScan.All();
            for (int i = 0; i < all.Length; i++)
            {
                Component vgs = all[i];
                if (vgs == null) continue;
                if (IntField(vgs, "_localPlayerPassengerId") < 0) continue;
                if (!Technical.IstTechnical(vgs.transform)) continue;

                _vgs = vgs;
                _root = vgs.transform;
                _mount = Technical.MountOf(_root);
                _gun = Technical.GunOf(_root);
                if (_mount == null || _gun == null)
                    RevivalPlugin.L.LogWarning("Technical-MG: an \"" + _root.name
                        + "\" fehlt die Lafette - der Umbau ist auf diesem "
                        + "Client nicht gelaufen.");
                return;
            }
            Clear();
        }

        static void Clear()
        {
            SetManning(false);
            Ladeabbruch();
            _vgs = null;
            _root = null;
            _mount = null;
            _gun = null;
            _atGun = false;
            _standDown = false;
            _offerWarned = false;
            // The belt belongs to the gun that was left behind, not to the next
            // one: a fresh technical starts empty and is loaded once.
            _belt = 0;
            _pendingBelt = 0;
            _reloadDone = 0f;
        }

        static void Toggle()
        {
            if (_vgs == null) return;
            if (_mount == null || _gun == null)
            {
                Hinweis(TechnicalText.NoGun(), 3f);
                return;
            }

            int here = IntField(_vgs, "_localPlayerPassengerId");

            if (_manning)
            {
                SetManning(false);
                // Let go ON PURPOSE. Without this flag the automatic takeover
                // below would put him straight back on the gun in the same
                // second, and the key would look broken.
                _standDown = true;
                int back = FirstNormalSeat();
                if (back >= 0) ChangeSeat(back);
                RevivalPlugin.L.LogInfo("Technical-MG verlassen"
                    + (back >= 0 ? ", zurueck auf Platz " + back + "." : "."));
                return;
            }

            if (here == Technical.GunnerSeat)
            {
                _standDown = false;
                if (!SetManning(true)) { Hinweis(TechnicalText.CamBusy(), 3f); return; }
                Hinweis(TechnicalText.AtGun(), 5f);
                RevivalPlugin.L.LogInfo("Technical-MG besetzt (Sitz " + here
                    + ", der Spieler stand schon dort).");
                return;
            }

            _standDown = false;
            if (!ChangeSeat(Technical.GunnerSeat)) return;
            if (!SetManning(true)) { Hinweis(TechnicalText.CamBusy(), 3f); return; }
            Hinweis(TechnicalText.AtGun(), 5f);
            RevivalPlugin.L.LogInfo("Technical-MG besetzt (Sitz "
                + Technical.GunnerSeat + ", vorher " + here + ").");
        }

        static bool _offerWarned;

        /// <summary>
        /// Take hold of the gun for a player who is already standing at it.
        ///
        /// THE PLACE IS THE GUN. It was reachable only through the G key, and
        /// the field report of 2026-09-18 is what that costs: a player standing
        /// at the machine gun of his own truck, unable to aim, fire or reload,
        /// because nothing on the screen said that a key was missing. Anyone put
        /// into the place - by G, by the game's own seat assignment, or by
        /// another player leaving - now mans it by himself.
        ///
        /// Three things keep it from becoming a trap: it never runs after a
        /// deliberate stand-down (G), it asks at most once a second, and it
        /// reports a refused camera ONCE instead of every second. G always
        /// remains the way out, and to a front seat with it.
        /// </summary>
        static void Anbieten()
        {
            if (_standDown) return;
            if (CfgAutoMan != null && !CfgAutoMan.Value) return;
            if (Time.time < _nextOffer) return;
            _nextOffer = Time.time + 1f;

            if (_mount == null || _gun == null)
            {
                if (!_offerWarned)
                {
                    _offerWarned = true;
                    Hinweis(TechnicalText.NoGun(), 4f);
                }
                return;
            }
            if (SetManning(true))
            {
                _offerWarned = false;
                Hinweis(TechnicalText.AtGun(), 5f);
                RevivalPlugin.L.LogInfo("Technical-MG: der Stehplatz hat das MG "
                    + "selbst besetzt (kein Tastendruck noetig).");
                return;
            }
            if (_offerWarned) return;
            _offerWarned = true;
            Hinweis(TechnicalText.CamBusy(), 4f);
        }

        /// <summary>
        /// The ONLY place _manning changes, so the camera and the pose can never
        /// be left held. Returns false when somebody else - a drone, the BTR
        /// turret - is looking through the camera.
        /// </summary>
        static bool SetManning(bool on)
        {
            if (_manning == on) return true;
            if (on)
            {
                if (!CameraOwner.Request(CameraOwner.GunTruck, true, "Technical-MG"))
                    return false;
                _manning = true;
                _camLogged = false;
                InitAim();
                return true;
            }
            _manning = false;
            ReleasePose();
            // A reload that is still running has to give the game's progress bar
            // back with the gun, or it hangs on the HUD for good.
            Ladeabbruch();
            CameraOwner.Release(CameraOwner.GunTruck);
            return true;
        }

        static int FirstNormalSeat()
        {
            Array passengers = Field(_vgs, "Passengers") as Array;
            if (passengers == null) return -1;
            for (int i = 0; i < passengers.Length; i++)
            {
                if (i == Technical.GunnerSeat) continue;
                GameObject go = passengers.GetValue(i) as GameObject;
                if (go == null) return i;
            }
            return -1;
        }

        static bool ChangeSeat(int index)
        {
            object pvm = Field(_vgs, "_playerVehicleManager");
            if (pvm == null)
            {
                RevivalPlugin.L.LogWarning("Technical-MG: _playerVehicleManager ist "
                    + "null - der lokale Spieler ist an diesem Fahrzeug nicht "
                    + "eingetragen.");
                return false;
            }
            MethodInfo m = AccessTools.Method(pvm.GetType(), "ChangeToPassengerPlace",
                                              new Type[] { typeof(int) }, null);
            if (m == null)
            {
                RevivalPlugin.L.LogWarning("Technical-MG: ChangeToPassengerPlace(int) fehlt.");
                return false;
            }
            Array passengers = Field(_vgs, "Passengers") as Array;
            if (passengers == null || index >= passengers.Length)
            {
                RevivalPlugin.L.LogWarning("Technical-MG: Passengers hat nur "
                    + (passengers == null ? 0 : passengers.Length)
                    + " Plaetze, das MG waere Nummer " + index + ".");
                return false;
            }
            GameObject sitting = passengers.GetValue(index) as GameObject;
            if (sitting != null)
            {
                Hinweis(TechnicalText.GunTaken(), 3f);
                RevivalPlugin.L.LogWarning("Technical-MG: Platz " + index
                    + " ist belegt von \"" + sitting.name + "\".");
                return false;
            }
            m.Invoke(pvm, new object[] { index });
            return true;
        }

        // ------------------------------------------------------------- Zielen

        static void InitAim()
        {
            _yaw = 0f;
            _pitch = 0f;
            if (_mount != null)
            {
                Vector3 e = _mount.localEulerAngles;
                _yaw = Mathf.DeltaAngle(0f, e.y);
            }
            if (_gun != null)
            {
                Vector3 e = _gun.localEulerAngles;
                _pitch = -Mathf.DeltaAngle(0f, e.x);
            }
        }

        /// <summary>
        /// The mouse turns the mount, the mount turns the man. Both pivots are
        /// built by this plugin with identity rotation inside a base node that
        /// already absorbed the chassis frame, so yaw is a plain rotation about
        /// the vehicle's up axis and pitch a plain rotation about its right axis
        /// - no correction quaternion and no guessed sign, unlike the BTR turret
        /// which has to live inside the game's own rotated "Meshes" node.
        /// </summary>
        static void Aim()
        {
            if (_mount == null || _gun == null) return;

            float sens = CfgSensitivity.Value;
            _yaw += Input.GetAxis("Mouse X") * sens;
            _pitch += Input.GetAxis("Mouse Y") * sens;
            _pitch = Mathf.Clamp(_pitch, CfgPitchMin.Value, CfgPitchMax.Value);
            if (_yaw > 180f) _yaw -= 360f;
            if (_yaw < -180f) _yaw += 360f;

            float step = Mathf.Max(1f, CfgTurnSpeed.Value) * Time.deltaTime;
            _mount.localRotation = Quaternion.RotateTowards(_mount.localRotation,
                Quaternion.Euler(0f, _yaw, 0f), step);
            _gun.localRotation = Quaternion.RotateTowards(_gun.localRotation,
                Quaternion.Euler(-_pitch, 0f, 0f), step);

            // The body goes where the gun goes. This is also the whole network
            // story: a seated player's bearing is synchronized by the game, and
            // SlewRemote reads it back on every other client.
            Koerper();
        }

        static void Koerper()
        {
            GameObject body = PassengerAt(_vgs, Technical.GunnerSeat);
            if (body == null || _mount == null) return;
            Vector3 dir = _mount.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.000001f) return;
            body.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        /// <summary>
        /// The machine gun of a technical whose gunner is SOMEBODY ELSE (or
        /// nobody). The mount is slewed onto the bearing of the man standing in
        /// seat 2, which the game already synchronizes for us; an empty gun is
        /// left exactly where it was. Called once per frame per technical from
        /// <see cref="LateAll"/>, so it must stay cheap and must never throw -
        /// the two pivots come out of the station cache and are NOT searched
        /// for here.
        ///
        /// Elevation comes out of the same bearing: whatever tilt the body
        /// carries is put on the gun. A seated body is normally level, so a
        /// remote gun usually reads level - that is the price of using the
        /// game's own synchronization instead of a private Photon channel, and
        /// it costs nothing when the game does carry a tilt.
        /// </summary>
        internal static void SlewRemote(Component vgs)
        {
            try
            {
                if (vgs == null) return;
                if (_manning && ReferenceEquals(vgs, _vgs)) return;   // ours

                GameObject body = PassengerAt(vgs, Technical.GunnerSeat);
                if (body == null) return;
                Station st = Find(vgs);
                Transform mount = st != null ? st.Mount : Technical.MountOf(vgs.transform);
                if (mount == null || mount.parent == null) return;
                Transform gun = st != null ? st.Gun : mount.Find(Technical.GunName);

                Vector3 dir = mount.parent.InverseTransformDirection(
                    body.transform.forward);
                if (dir.sqrMagnitude < 0.000001f) return;
                dir.Normalize();
                float pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.000001f) return;
                float yaw = Quaternion.LookRotation(dir.normalized, Vector3.up)
                            .eulerAngles.y;
                float step = Mathf.Max(1f, CfgTurnSpeed.Value) * Time.deltaTime;
                mount.localRotation = Quaternion.RotateTowards(mount.localRotation,
                    Quaternion.Euler(0f, yaw, 0f), step);
                if (gun == null) return;
                pitch = Mathf.Clamp(pitch, CfgPitchMin.Value, CfgPitchMax.Value);
                gun.localRotation = Quaternion.RotateTowards(gun.localRotation,
                    Quaternion.Euler(-pitch, 0f, 0f), step);
            }
            catch { }
        }

        // ------------------------------------------------------ Gun stations

        /// <summary>
        /// One technical in the scene: its two pivots and the arm bones of
        /// whoever stands at its gun. Everything in here is expensive to find -
        /// finding the mount walks a whole vehicle hierarchy and finding the
        /// bones walks a character - and cheap to keep, so the list is rebuilt
        /// on the shared 5 Hz vehicle scan and only READ in LateUpdate.
        /// </summary>
        sealed class Station
        {
            public Component Vgs;
            public Transform Mount;
            public Transform Gun;
            public GameObject Body;          // the gunner the bones belong to
            public Transform LUpper, LFore, LHand;
            public Transform RUpper, RFore, RHand;
            public bool BonesTried;
            public float Blend;              // 0 = the game's animation, 1 = at the grips
        }

        static readonly List<Station> _stations = new List<Station>();
        static bool _handsWarned;

        static Station Find(Component vgs)
        {
            for (int i = 0; i < _stations.Count; i++)
                if (ReferenceEquals(_stations[i].Vgs, vgs)) return _stations[i];
            return null;
        }

        /// <summary>
        /// Rebuild the station list from the shared vehicle scan. Entries are
        /// kept by identity, so a gunner's bones are looked up once and not
        /// five times a second; a destroyed vehicle drops out through Unity's
        /// overloaded == on the next pass.
        /// </summary>
        internal static void Stations(Component[] all)
        {
            try
            {
                for (int i = _stations.Count - 1; i >= 0; i--)
                    if (_stations[i].Vgs == null) _stations.RemoveAt(i);

                if (all == null) return;
                for (int i = 0; i < all.Length; i++)
                {
                    Component vgs = all[i];
                    if (vgs == null || !Technical.IstTechnical(vgs.transform)) continue;
                    Station st = Find(vgs);
                    if (st == null)
                    {
                        st = new Station();
                        st.Vgs = vgs;
                        _stations.Add(st);
                    }
                    if (st.Mount == null) st.Mount = Technical.MountOf(vgs.transform);
                    if (st.Gun == null && st.Mount != null)
                        st.Gun = st.Mount.Find(Technical.GunName);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Technical-MG, Stationen: " + ex.Message);
            }
        }

        /// <summary>
        /// The per-frame work for EVERY technical in the scene: the machine gun
        /// of a vehicle somebody else is shooting from, and the arms of every
        /// gunner - the local one and each remote one.
        ///
        /// This has to be LateUpdate. The game's animator writes the character's
        /// bones every frame between Update and LateUpdate; a hand put on a grip
        /// in Update is back at the man's side before anything is drawn.
        /// </summary>
        internal static void LateAll()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.Vgs == null) continue;
                SlewRemote(st.Vgs);                       // returns at once for our own
                Hands(st, PassengerAt(st.Vgs, Technical.GunnerSeat), dt);
            }
        }

        // --------------------------------------------------- Hands on the gun

        /// <summary>
        /// Put the gunner's hands on the spade grips. The game has no animation
        /// for a man at a pintle mount - it has a sit pose and a walk pose - so
        /// the arms are SOLVED here instead: two bones per side, aimed at the
        /// grip the model actually has, once per frame after the animation ran.
        /// It is the same trick the game's own aim IK plays (RE: NPC_AI2 aims
        /// its rifle with an IK solver on MainChar_HandR, not with a clip).
        ///
        /// Everything about it is optional. Without the bones nothing happens,
        /// the reason is logged once and the gunner keeps the pose the game gave
        /// him; the gun still turns with him either way.
        /// </summary>
        static void Hands(Station st, GameObject body, float dt)
        {
            try
            {
                if (CfgHands == null || !CfgHands.Value) { st.Blend = 0f; return; }

                if (!ReferenceEquals(st.Body, body))
                {
                    st.Body = body;
                    st.BonesTried = false;
                    st.LUpper = null; st.LFore = null; st.LHand = null;
                    st.RUpper = null; st.RFore = null; st.RHand = null;
                    st.Blend = 0f;
                }
                if (body == null || st.Gun == null) { st.Blend = 0f; return; }
                if (!st.BonesTried) Bones(st);
                if (st.LHand == null || st.RHand == null) return;

                float fade = CfgHandFade == null ? 0.25f : Mathf.Max(0f, CfgHandFade.Value);
                st.Blend = fade < 0.0001f ? 1f
                                          : Mathf.MoveTowards(st.Blend, 1f, dt / fade);

                float lift = CfgGripLift == null ? 0f : CfgGripLift.Value;
                float outward = CfgElbowOut == null ? 0.55f
                                                    : Mathf.Clamp01(CfgElbowOut.Value);
                Vector3 up = body.transform.up;
                Vector3 side = body.transform.right;

                for (int s = 0; s < 2; s++)
                {
                    bool left = s == 0;
                    Vector3 local = TechnicalModel.GripLocal(left);
                    local.y += lift;
                    // The grip is asked of the GUN transform, so it carries the
                    // vehicle's units-per-metre with it and the hands land in
                    // the right place on a model of any size.
                    Vector3 target = st.Gun.TransformPoint(local);
                    Vector3 pole = -up * (1f - outward)
                                   + side * (left ? -outward : outward);
                    if (pole.sqrMagnitude < 0.000001f) pole = -up;
                    Arm(left ? st.LUpper : st.RUpper,
                        left ? st.LFore : st.RFore,
                        left ? st.LHand : st.RHand,
                        target, pole.normalized, st.Blend);
                }
            }
            catch (Exception ex)
            {
                st.Blend = 0f;
                st.LHand = null;
                st.RHand = null;
                if (!_handsWarned)
                {
                    _handsWarned = true;
                    RevivalPlugin.L.LogWarning("Technical-MG: Haende am MG nicht "
                        + "moeglich (" + ex.Message + ") - der Schuetze behaelt "
                        + "die Haltung des Spiels.");
                }
            }
        }

        /// <summary>
        /// Two-bone IK, world space, no Animator and no IK package: aim the
        /// whole arm at the grip, bend the elbow by the law of cosines so the
        /// hand lands ON it, then point the forearm at it. The HAND itself is
        /// never touched - it is a child of the forearm and keeps the local
        /// rotation the animation gave it, which is what keeps the fingers in
        /// the shape the game posed them.
        ///
        /// A grip further away than the arm is long gives an elbow angle of
        /// zero, i.e. a straight arm reaching for it. That is the right picture
        /// for a gunner standing too far back, and it is why nothing here
        /// divides by a length that could be zero.
        /// </summary>
        static void Arm(Transform upper, Transform fore, Transform hand,
                        Vector3 target, Vector3 pole, float weight)
        {
            if (upper == null || fore == null || hand == null) return;
            if (weight <= 0.0001f) return;

            Quaternion upperBack = upper.rotation;
            Quaternion foreBack = fore.rotation;

            Vector3 shoulder = upper.position;
            float upperLen = Vector3.Distance(shoulder, fore.position);
            float foreLen = Vector3.Distance(fore.position, hand.position);
            if (upperLen < 0.0001f || foreLen < 0.0001f) return;

            Vector3 toGrip = target - shoulder;
            float dist = toGrip.magnitude;
            if (dist < 0.0001f) return;
            Vector3 dir = toGrip / dist;

            // 1: the whole arm points at the grip.
            upper.rotation = Quaternion.FromToRotation(fore.position - shoulder, dir)
                             * upper.rotation;

            // 2: the elbow swings out of that line by the shoulder angle of the
            // triangle (upper arm, forearm, line to the grip), toward the pole.
            float reach = Mathf.Clamp(dist,
                Mathf.Abs(upperLen - foreLen) + 0.0001f, upperLen + foreLen);
            float cos = Mathf.Clamp((upperLen * upperLen + reach * reach
                                     - foreLen * foreLen)
                                    / (2f * upperLen * reach), -1f, 1f);
            float bend = Mathf.Acos(cos) * Mathf.Rad2Deg;
            Vector3 axis = Vector3.Cross(dir, pole);
            if (axis.sqrMagnitude < 0.000001f) axis = Vector3.Cross(dir, Vector3.up);
            if (bend > 0.01f && axis.sqrMagnitude > 0.000001f)
                upper.rotation = Quaternion.AngleAxis(bend, axis.normalized)
                                 * upper.rotation;

            // 3: the forearm points at the grip, which puts the hand on it.
            Vector3 elbow = fore.position;
            fore.rotation = Quaternion.FromToRotation(hand.position - elbow,
                                                      target - elbow) * fore.rotation;

            if (weight < 0.999f)
            {
                Quaternion upperNow = upper.rotation;
                Quaternion foreNow = fore.rotation;
                upper.rotation = Quaternion.Slerp(upperBack, upperNow, weight);
                fore.rotation = Quaternion.Slerp(foreBack, foreNow, weight);
            }
        }

        /// <summary>
        /// Find the gunner's arm bones ONCE per body. The hand is the anchor:
        /// in every humanoid rig the forearm is its parent and the upper arm
        /// its grandparent, so finding one transform per side is enough and no
        /// guess about the game's bone names beyond "hand" is needed.
        /// </summary>
        static void Bones(Station st)
        {
            st.BonesTried = true;
            GameObject body = st.Body;
            if (body == null) return;

            Transform lh = HandBone(body, true);
            Transform rh = HandBone(body, false);
            string why = null;
            if (lh == null || rh == null) why = "keine Handknochen gefunden";
            else if (ReferenceEquals(lh, rh)) why = "links und rechts sind derselbe Knochen";
            else if (!Chain(lh) || !Chain(rh)) why = "ueber der Hand fehlen Unterarm und Oberarm";

            if (why != null)
            {
                if (!_handsWarned)
                {
                    _handsWarned = true;
                    RevivalPlugin.L.LogWarning("Technical-MG: Haende bleiben, wo "
                        + "die Animation sie hat - " + why + " an \"" + body.name
                        + "\". Das MG dreht sich trotzdem mit dem Schuetzen; "
                        + "abschalten laesst sich der Versuch mit "
                        + "TechnicalGun/HandsOnGun.");
                }
                return;
            }

            st.LHand = lh; st.LFore = lh.parent; st.LUpper = lh.parent.parent;
            st.RHand = rh; st.RFore = rh.parent; st.RUpper = rh.parent.parent;
            RevivalPlugin.L.LogInfo("Technical-MG: Arme des Schuetzen gefunden - "
                + st.LUpper.name + " / " + st.LFore.name + " / " + lh.name
                + " und " + st.RUpper.name + " / " + st.RFore.name + " / "
                + rh.name + ".");
            Vermessung(st, body);
        }

        /// <summary>
        /// One line per gunner that answers the two questions a screenshot of a
        /// wrong-looking gun station always raises: does the man stand ON the
        /// deck, and can his arms reach the grips at all? Both are read off the
        /// live scene once per body - the pintle foot IS the deck, the man's
        /// root is at his feet while he stands, and the arm length is the sum of
        /// the two bones that were just found. Written because the report of
        /// 2026-09-18 had to be answered from a picture instead of from the log.
        /// </summary>
        static void Vermessung(Station st, GameObject body)
        {
            try
            {
                Transform basis = st.Mount == null ? null : st.Mount.parent;
                if (basis == null || st.Gun == null || st.RHand == null) return;

                float deck = basis.position.y;
                float root = body.transform.position.y;
                Vector3 griff = st.Gun.TransformPoint(TechnicalModel.GripLocal(false));
                float arm = Vector3.Distance(st.RUpper.position, st.RFore.position)
                            + Vector3.Distance(st.RFore.position, st.RHand.position);
                float weit = Vector3.Distance(st.RUpper.position, griff);

                RevivalPlugin.L.LogInfo("Technical-MG: Schuetze \"" + body.name
                    + "\" - Wurzel " + root.ToString("0.00") + ", Standflaeche "
                    + deck.ToString("0.00") + " (Abstand "
                    + (root - deck).ToString("0.00")
                    + "; 0 heisst er steht darauf, deutlich mehr heisst er "
                    + "schwebt oder sitzt). Griff " + griff.y.ToString("0.00")
                    + ", also " + (griff.y - root).ToString("0.00")
                    + " ueber seiner Wurzel. Armlaenge " + arm.ToString("0.00")
                    + ", Schulter zum Griff " + weit.ToString("0.00")
                    + (weit > arm ? " - ZU WEIT, die Arme sind gestreckt." : "."));
            }
            catch { }
        }

        /// <summary>Is there a forearm and an upper arm above this hand, and are
        /// the two bones long enough and similar enough to be an arm?</summary>
        static bool Chain(Transform hand)
        {
            Transform fore = hand.parent;
            if (fore == null) return false;
            Transform upper = fore.parent;
            if (upper == null) return false;
            float a = Vector3.Distance(upper.position, fore.position);
            float b = Vector3.Distance(fore.position, hand.position);
            if (a < 0.0001f || b < 0.0001f) return false;
            float ratio = a / b;
            return ratio > 0.2f && ratio < 5f;
        }

        /// <summary>
        /// The hand of one side. First the game's own answer: NPC_AI2 aims
        /// through _playerObjectsManager.MainChar_HandR (RE), so
        /// PlayerObjectsManager is where a character's hands are named. A
        /// character without that component falls back to the shallowest
        /// transform whose name says "hand" and says which side it is.
        /// </summary>
        static Transform HandBone(GameObject body, bool left)
        {
            Type pomType = RevivalPlugin.TypeByName("PlayerObjectsManager");
            if (pomType != null)
            {
                Component pom = body.GetComponentInChildren(pomType, true);
                if (pom != null)
                {
                    Transform t = AsTransform(Field(pom,
                        left ? "MainChar_HandL" : "MainChar_HandR"));
                    if (t != null && Under(body.transform, t)) return t;
                }
            }

            Transform best = null;
            int bestDepth = int.MaxValue;
            Transform[] all = body.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                string low = t.name.ToLowerInvariant();
                if (low.IndexOf("hand") < 0) continue;
                if (low.IndexOf("handle") >= 0) continue;
                if (low.IndexOf("ik") >= 0 || low.IndexOf("target") >= 0
                    || low.IndexOf("weapon") >= 0 || low.IndexOf("attach") >= 0
                    || low.IndexOf("socket") >= 0 || low.IndexOf("grip") >= 0
                    || low.IndexOf("hold") >= 0 || low.IndexOf("point") >= 0)
                    continue;
                if (Side(low) != (left ? -1 : 1)) continue;
                int depth = Depth(body.transform, t);
                if (depth < 0 || depth >= bestDepth) continue;
                best = t;
                bestDepth = depth;
            }
            return best;
        }

        /// <summary>-1 left, +1 right, 0 no side in the name. Reads the side
        /// letter next to the word "hand" (MainChar_HandR, Bip01 L Hand) as well
        /// as the spelled-out words.</summary>
        static int Side(string low)
        {
            if (low.IndexOf("left") >= 0) return -1;
            if (low.IndexOf("right") >= 0) return 1;
            int i = low.IndexOf("hand");
            if (i < 0) return 0;
            for (int k = i + 4; k < low.Length; k++)
            {
                char c = low[k];
                if (c == '_' || c == '.' || c == ' ' || c == '-') continue;
                if (c == 'l') return -1;
                if (c == 'r') return 1;
                break;
            }
            for (int k = i - 1; k >= 0; k--)
            {
                char c = low[k];
                if (c == '_' || c == '.' || c == ' ' || c == '-') continue;
                if (c == 'l') return -1;
                if (c == 'r') return 1;
                break;
            }
            return 0;
        }

        static Transform AsTransform(object value)
        {
            Transform t = value as Transform;
            if (t != null) return t;
            GameObject go = value as GameObject;
            return go == null ? null : go.transform;
        }

        static bool Under(Transform root, Transform t)
        {
            return Depth(root, t) >= 0;
        }

        /// <summary>How many parents lie between the character root and this
        /// transform, or -1 when it is not below it at all.</summary>
        static int Depth(Transform root, Transform t)
        {
            int n = 0;
            while (t != null)
            {
                if (t == root) return n;
                t = t.parent;
                n++;
            }
            return -1;
        }

        // -------------------------------------------------------------- Haltung

        /// <summary>
        /// Stand the gunner up. `_inVehiclePose` is the field
        /// PlayerVehicleManager::SitToVehicle writes (1 for the driver, 2 for a
        /// passenger; RE "The T-72 crew"); holding it at the on-foot value for
        /// as long as the local player is IN the gunner's place is what turns
        /// the bench pose into a man on his feet - the place is what he stands
        /// in, the gun is only what he does there, so this does not wait for the
        /// trigger. It is written every frame because the game writes it too,
        /// and it is restored exactly once on release. Everything here is
        /// optional: without the field the gunner simply sits, and the gun still
        /// turns with him.
        /// </summary>
        static void HoldPose()
        {
            if (CfgStandPose == null || !CfgStandPose.Value) return;
            try
            {
                if (!_poseHeld)
                {
                    object pvm = Field(_vgs, "_playerVehicleManager");
                    if (pvm == null) return;
                    FieldInfo f = AccessTools.Field(pvm.GetType(), "_inVehiclePose");
                    if (f == null)
                    {
                        if (!_poseWarned)
                        {
                            _poseWarned = true;
                            RevivalPlugin.L.LogWarning("Technical-MG: "
                                + "PlayerVehicleManager._inVehiclePose fehlt - der "
                                + "Schuetze bleibt in der Sitzhaltung.");
                        }
                        return;
                    }
                    _poseOwner = pvm;
                    _poseField = f;
                    _poseBack = f.GetValue(pvm);
                    _poseHeld = true;
                    RevivalPlugin.L.LogInfo("Technical-MG: Haltung " + _poseBack
                        + " -> " + CfgStandPoseValue.Value + " (Schuetze steht).");
                }
                if (_poseField == null || _poseOwner == null) return;
                _poseField.SetValue(_poseOwner, PoseValue(_poseField.FieldType,
                                                          CfgStandPoseValue.Value));
            }
            catch (Exception ex)
            {
                _poseHeld = false;
                _poseField = null;
                _poseOwner = null;
                if (!_poseWarned)
                {
                    _poseWarned = true;
                    RevivalPlugin.L.LogWarning("Technical-MG: Stehhaltung nicht "
                        + "moeglich (" + ex.Message + ") - der Schuetze sitzt.");
                }
            }
        }

        /// <summary>The configured pose number in whatever type the field is -
        /// the game may declare it as an int or as a small enum, and boxing the
        /// wrong one throws inside SetValue.</summary>
        static object PoseValue(Type fieldType, int value)
        {
            if (fieldType == null) return value;
            if (fieldType.IsEnum) return Enum.ToObject(fieldType, value);
            return Convert.ChangeType(value, fieldType);
        }

        /// <summary>Is the local player in this place of our technical?</summary>
        static bool InSeat(int index)
        {
            return _vgs != null
                   && IntField(_vgs, "_localPlayerPassengerId") == index;
        }

        static void ReleasePose()
        {
            if (!_poseHeld) return;
            try
            {
                // Give the pose back only while the man is still IN the
                // vehicle. Once he has climbed out, PlayerVehicleManager has
                // written the on-foot pose itself, and putting the seated value
                // we remembered back over it would sit him down in the road.
                if (_poseField != null && _poseOwner != null && _poseBack != null
                    && _vgs != null
                    && IntField(_vgs, "_localPlayerPassengerId") >= 0)
                    _poseField.SetValue(_poseOwner, _poseBack);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Technical-MG: Haltung nicht "
                    + "zurueckgesetzt: " + ex.Message);
            }
            finally
            {
                _poseHeld = false;
                _poseField = null;
                _poseOwner = null;
                _poseBack = null;
            }
        }

        // --------------------------------------------------------------- Bild

        /// <summary>
        /// The view: third person, over the right shoulder, along the barrel.
        /// Deliberately NOT the turret's eye-in-the-bore treatment - the request
        /// was that it look like ordinary non-scoped aiming on foot, only
        /// further out and fitted to a man standing on a truck bed. The field of
        /// view is left alone for the same reason.
        ///
        /// Belongs in LateUpdate: the game pulls its own vehicle camera along in
        /// the same frame, and whoever writes first loses.
        /// </summary>
        public static void LateTick()
        {
            if (!_manning) return;
            try
            {
                if (_gun == null || _root == null) return;
                Camera cam = CameraOwner.ViewCamera();
                if (cam == null) return;

                Vector3 dir = _gun.forward;
                if (dir.sqrMagnitude < 0.000001f) return;
                dir.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, dir);
                if (right.sqrMagnitude < 0.000001f) right = _gun.right;
                right.Normalize();

                // Pull back along a FLATTENED aim axis: at 55 degrees elevation
                // a camera on the true axis would be under the truck bed.
                Vector3 back = new Vector3(dir.x, dir.y * 0.35f, dir.z);
                if (back.sqrMagnitude < 0.000001f) back = dir;
                back.Normalize();

                float u = UnitsPerMetre();
                Vector3 eye = _gun.position
                    - back * (CfgCamBack.Value * u)
                    + Vector3.up * (CfgCamUp.Value * u)
                    + right * (CfgCamSide.Value * u);

                cam.transform.position = eye;
                cam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

                if (!_camLogged)
                {
                    _camLogged = true;
                    RevivalPlugin.L.LogInfo("Technical-MG: Kamera \"" + cam.name
                        + "\", Auge " + eye + ", Waffe " + _gun.position
                        + ", Richtung " + dir + ", " + u.ToString("0.000")
                        + " Einheiten je Meter.");
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Technical-MG, Kamera: " + ex);
                SetManning(false);
            }
        }

        /// <summary>
        /// Model units per metre, read back from the scale the rebuild put on
        /// the mount's base node. That keeps every distance in this class in
        /// metres even though the vehicle models are not metric.
        /// </summary>
        static float UnitsPerMetre()
        {
            if (_mount != null && _mount.parent != null)
            {
                float s = _mount.parent.localScale.x;
                if (s > 0.0001f) return s;
            }
            return 1f;
        }

        // ---------------------------------------------------------- Schiessen

        static readonly Color SpurKern = new Color(1.00f, 0.96f, 0.78f, 1.0f);
        static readonly Color SpurEnde = new Color(1.00f, 0.62f, 0.20f, 1.0f);
        static readonly Color SpurHof = new Color(1.00f, 0.38f, 0.10f, 1.0f);

        // ------------------------------------------------------- Gurt und Laden

        /// <summary>
        /// The trigger. The gun fires out of the BELT that is in it; an empty
        /// belt starts a reload instead of a shot, which is what makes the
        /// weapon behave like a weapon and not like a tap on an inventory.
        /// </summary>
        static void Feuern()
        {
            if (_gun == null) return;
            if (_reloadDone > 0f) return;             // a belt is going in
            if (_belt <= 0) { Ladebeginn(false); return; }
            if (Fire())
            {
                _belt--;
                _nextShot = Time.time + CfgDelay.Value;
                if (_belt <= 0) Ladebeginn(false);
            }
            else _nextTry = Time.time + 1f;
        }

        /// <summary>
        /// The reload clock, and the reload key. Both belong in Update: the
        /// native progress bar is started once and then only waited on.
        /// </summary>
        static void Nachladen()
        {
            if (_reloadDone > 0f)
            {
                float rest = _reloadDone - Time.time;
                if (rest > 0.02f)
                {
                    // Also the way back in: a gunner who let go mid-reload and
                    // took hold again gets the bar for the REMAINING time. The
                    // rounds were taken out of the box when the belt was pulled,
                    // so the wait cannot be skipped by letting go of the gun.
                    if (!NativeActionProgress.IsActive(ReloadOwner))
                        NativeActionProgress.Begin(ReloadOwner,
                            TechnicalText.Reloading(), rest, false, null, null);
                    return;
                }
                _reloadDone = 0f;
                _belt = _pendingBelt;
                _pendingBelt = 0;
                NativeActionProgress.End(ReloadOwner);
                _nextShot = Time.time + CfgDelay.Value;
                RevivalPlugin.L.LogInfo("Technical-MG: Gurt eingelegt, " + _belt
                    + " Schuss in der Waffe.");
                return;
            }
            if (Input.GetKeyDown(ReloadKey())) Ladebeginn(true);
        }

        /// <summary>
        /// Put a belt in. The rounds are taken out of the trunk, the backpack
        /// and the vest AT ONCE and at the START - a belt that is being fed into
        /// the gun has left the box, so a gunner who dies halfway through loses
        /// it, and a single shot can never cost a whole 200-round box.
        ///
        /// Returns silently when there is nothing to load; the player is told
        /// once through the hint line and the log says where it looked.
        /// </summary>
        static void Ladebeginn(bool byHand)
        {
            if (_reloadDone > 0f) return;
            int room = BeltRounds() - _belt;
            if (room <= 0) return;
            if (byHand && Time.time < _nextTry) return;

            int got = Gurt(room);
            if (got <= 0)
            {
                _nextTry = Time.time + 1f;
                Hinweis(TechnicalText.NoAmmo(CfgAmmoId.Value), 3f);
                if (Time.time >= _leerGemeldet)
                {
                    _leerGemeldet = Time.time + 10f;
                    RevivalPlugin.L.LogInfo("Technical-MG: keine Munition (Item "
                        + CfgAmmoId.Value + ") im Kofferraum, Rucksack und in der Weste.");
                }
                return;
            }

            float dauer = Mathf.Max(0.1f, CfgReload == null ? 4.5f : CfgReload.Value);
            _pendingBelt = _belt + got;
            _belt = 0;
            _reloadDone = Time.time + dauer;
            NativeActionProgress.End(ReloadOwner);
            NativeActionProgress.Begin(ReloadOwner, TechnicalText.Reloading(),
                                       dauer, false, null, null);
            // No extra hint line: the game's own bar and the counter under the
            // crosshair already say it, and three copies of one word is noise.
            RevivalPlugin.L.LogInfo("Technical-MG: Gurt mit " + got
                + " Schuss wird eingelegt (" + dauer.ToString("0.0") + " s"
                + (byHand ? ", von Hand" : ", Gurt leer") + ").");
        }

        /// <summary>
        /// Give the game's progress bar back when the gun is let go mid-reload,
        /// otherwise it stands on the HUD for good. The CLOCK keeps running and
        /// the pulled rounds stay pulled: the belt is half in the gun, letting
        /// go of the grips does not put it back in the box, and it cannot be
        /// used to skip the wait either. Nachladen picks the bar up again.
        /// </summary>
        static void Ladeabbruch()
        {
            NativeActionProgress.End(ReloadOwner);
        }

        /// <summary>
        /// Pull up to <paramref name="want"/> rounds out of the vehicle's trunk,
        /// then out of the backpack and the vest. The containers are found ONCE
        /// for the whole belt: Turret.PlayerInventories walks the scene, and
        /// doing that per round would turn a reload into a visible stall.
        /// </summary>
        static int Gurt(int want)
        {
            if (want <= 0) return 0;
            if (CfgRequireAmmo == null || !CfgRequireAmmo.Value) return want;

            int id = CfgAmmoId.Value;
            int got = 0;

            Component trunk = TrunkContainer(_root);
            object trunkData = trunk == null ? null : Field(trunk, "_containerData");
            while (got < want && trunk != null
                   && Turret.TakeFrom(trunk, trunkData, "Kofferraum", id, "Technical-MG"))
                got++;

            if (got >= want) return got;

            List<object> invs = Turret.PlayerInventories();
            for (int i = 0; i < invs.Count && got < want; i++)
            {
                object pack = Field(invs[i], "_backpackData");
                while (got < want
                       && Turret.TakeFrom(invs[i], pack, "Rucksack", id, "Technical-MG"))
                    got++;
                object vest = Field(invs[i], "_gearsData");
                while (got < want
                       && Turret.TakeFrom(invs[i], vest, "Weste", id, "Technical-MG"))
                    got++;
            }
            return got;
        }

        static int BeltRounds()
        {
            return CfgBelt == null ? 50 : Mathf.Max(1, CfgBelt.Value);
        }

        /// <summary>
        /// One shot. Returns false when NONE was fired - then the reload delay
        /// must not start either. The ammunition was paid for when the belt was
        /// loaded, so this only puts the round on its way.
        /// </summary>
        static bool Fire()
        {
            _pitch = Mathf.Clamp(_pitch + CfgRecoil.Value,
                                 CfgPitchMin.Value, CfgPitchMax.Value);

            Vector3 muzzle = Muzzle();

            // Hit what the crosshair covers. The eye is behind the gunner, so
            // the ray comes from the camera - the same decision the BTR turret
            // made for the same reason: a crosshair that can lie is worse than
            // an origin a few metres behind the barrel.
            Camera cam = CameraOwner.ViewCamera();
            Vector3 origin = cam == null ? muzzle : cam.transform.position;
            Vector3 dir = cam == null ? _gun.forward : cam.transform.forward;

            VehicleShotSound.Play(muzzle, false);
            Turret.Net.PublishShot(muzzle, false);

            Vector3 impact;
            GameObject struck = RaycastPastVehicle(origin, dir,
                                                   CfgRange.Value, out impact);
            Vector3 ende = struck == null ? origin + dir * CfgRange.Value : impact;
            Tracer(muzzle, ende);
            if (struck == null) return true;

            // No VehicleArmor.GunHit on purpose: this is an anti-personnel
            // machine gun, so armour is unaffected exactly as it is by rifle
            // fire. That also keeps it strictly weaker than the BTR autocannon,
            // which is what was asked for.
            float damage = CfgDamage.Value;
            if (Turret.TryDamage(struck, "NPC_AI2", "ApplyDamage", damage)) return true;
            if (Turret.TryDamage(struck, "Animal_AI", "NetworkApplyDamage", damage)) return true;
            Turret.TryDamage(struck, "PlayerNetworkController", "PlayerApplyDamage", damage);
            return true;
        }

        static Vector3 Muzzle()
        {
            if (_gun == null) return Vector3.zero;
            return _gun.TransformPoint(TechnicalModel.MuzzleLocal);
        }

        /// <summary>Like Turret's, but for our own vehicle: the ray starts
        /// behind the gunner and would otherwise hit the truck he stands on.</summary>
        static GameObject RaycastPastVehicle(Vector3 origin, Vector3 direction,
                                             float range, out Vector3 point)
        {
            point = Vector3.zero;
            Vector3 from = origin;
            float rest = range;
            for (int versuch = 0; versuch < 4 && rest > 0f; versuch++)
            {
                Vector3 hit;
                GameObject go = Turret.RaycastObject(from, direction, rest, out hit);
                if (go == null) return null;
                if (!IsOwnVehicle(go)) { point = hit; return go; }
                rest -= Vector3.Distance(from, hit) + 0.25f;
                from = hit + direction * 0.25f;
            }
            return null;
        }

        static bool IsOwnVehicle(GameObject go)
        {
            if (go == null || _root == null) return false;
            Transform t = go.transform;
            while (t != null)
            {
                if (t == _root) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Tracer from the MUZZLE to the impact, not from the eye: in third
        /// person the gunner watches his own weapon, and a burst that starts in
        /// mid-air beside him reads as a bug. Thinner than the BTR's - nine
        /// rounds a second must not fill the screen.
        /// </summary>
        static void Tracer(Vector3 von, Vector3 bis)
        {
            try
            {
                List<Vector3> bahn = new List<Vector3>();
                bahn.Add(von);
                bahn.Add(bis);
                RocketHook.SpawnTracer(bahn, 0.30f, 0.12f, SpurHof, SpurHof, 0.09f);
                RocketHook.SpawnTracer(bahn, 0.11f, 0.04f, SpurKern, SpurEnde, 0.16f);

                Vector3 achse = bis - von;
                float laenge = achse.magnitude;
                if (laenge > 0.01f)
                {
                    float feuer = Mathf.Min(2.5f, laenge);
                    List<Vector3> muendung = new List<Vector3>();
                    muendung.Add(von);
                    muendung.Add(von + achse / laenge * feuer);
                    RocketHook.SpawnTracer(muendung, 0.80f, 0.05f,
                                           Color.white, SpurHof, 0.05f);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Technical-MG: Leuchtspur - " + ex.Message);
            }
        }

        // ---------------------------------------------------------- Munition

        static Component TrunkContainer(Transform veh)
        {
            if (veh == null) return null;
            Type ic = RevivalPlugin.TypeByName("ItemsContainer");
            if (ic == null) return null;
            Component[] all = veh.GetComponentsInChildren(ic, true);
            return all.Length == 0 ? null : all[0];
        }

        // -------------------------------------------------------------- IMGUI

        public static void Draw()
        {
            bool hint = _hinweis != null && Time.time < _hinweisBis;
            // The standing hint is not a notice with a lifetime: as long as a
            // player is in the gunner's place with his hands off the gun, the
            // screen has to say how he takes hold of it again.
            bool offer = _atGun && !_manning;
            if (!_manning && !hint && !offer) return;
            if (Event.current != null && Event.current.type != EventType.Repaint) return;

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            if (_manning && CfgCrosshair != null && CfgCrosshair.Value)
            {
                if (_dot == null)
                {
                    _dot = new Texture2D(1, 1);
                    _dot.SetPixel(0, 0, Color.white);
                    _dot.Apply();
                }
                Color old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.85f);
                // Four strokes with a gap in the middle, like the game's own.
                GUI.DrawTexture(new Rect(cx - 14f, cy - 1f, 9f, 2f), _dot);
                GUI.DrawTexture(new Rect(cx + 5f, cy - 1f, 9f, 2f), _dot);
                GUI.DrawTexture(new Rect(cx - 1f, cy - 14f, 2f, 9f), _dot);
                GUI.DrawTexture(new Rect(cx - 1f, cy + 5f, 2f, 9f), _dot);
                GUI.color = old;
            }

            // The ammunition state, where a shooter looks for it: under the
            // crosshair, and only while he is actually at the gun.
            if (_manning)
            {
                string text = _reloadDone > 0f
                    ? TechnicalText.Reloading()
                    : TechnicalText.Belt(_belt, BeltRounds());
                Zeile(text, cx, cy + 72f,
                      _belt <= 0 && _reloadDone <= 0f
                          ? new Color(1f, 0.55f, 0.40f, 1f)
                          : new Color(0.92f, 0.92f, 0.86f, 1f), 15);
            }

            if (hint)
                Zeile(_hinweis, cx, cy + 40f, new Color(1f, 0.92f, 0.70f, 1f), 16);
            else if (offer)
                Zeile(TechnicalText.ManHint(Key().ToString()), cx, cy + 40f,
                      new Color(1f, 0.92f, 0.70f, 1f), 16);
        }

        /// <summary>One centred line of text. Centred by measurement, because
        /// GUIStyle.alignment would pull in TextAnchor from
        /// UnityEngine.TextRenderingModule, which this build does not
        /// reference.</summary>
        static void Zeile(string text, float cx, float y, Color colour, int size)
        {
            if (string.IsNullOrEmpty(text)) return;
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = size;
            style.normal.textColor = colour;
            GUIContent content = new GUIContent(text);
            Vector2 measured = style.CalcSize(content);
            GUI.Label(new Rect(cx - measured.x * 0.5f,
                               y + (26f - measured.y) * 0.5f,
                               measured.x, measured.y), content, style);
        }

        // ------------------------------------------------------------ Helfer

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), CfgKey.Value, true); }
            catch
            {
                _key = KeyCode.G;
                RevivalPlugin.L.LogWarning("Technical-MG: Taste " + CfgKey.Value
                    + " unbekannt, benutze G.");
            }
            return _key;
        }

        static KeyCode ReloadKey()
        {
            if (_reloadKeyParsed) return _reloadKey;
            _reloadKeyParsed = true;
            try
            {
                _reloadKey = (KeyCode)Enum.Parse(typeof(KeyCode),
                                                 CfgReloadKey.Value, true);
            }
            catch
            {
                _reloadKey = KeyCode.R;
                RevivalPlugin.L.LogWarning("Technical-MG: Nachladetaste "
                    + CfgReloadKey.Value + " unbekannt, benutze R.");
            }
            return _reloadKey;
        }

        static FieldInfo _passField;
        static Type _passType;

        /// <summary>
        /// Who sits in this place. The FieldInfo is kept, because this runs for
        /// every technical in the scene in every LateUpdate and a fresh
        /// AccessTools.Field lookup per call would be reflection in the frame
        /// loop for no gain - the type never changes.
        /// </summary>
        static GameObject PassengerAt(Component vgs, int index)
        {
            if (vgs == null) return null;
            Type t = vgs.GetType();
            if (!ReferenceEquals(t, _passType))
            {
                _passType = t;
                _passField = AccessTools.Field(t, "Passengers");
            }
            Array passengers = _passField == null ? null
                                                  : _passField.GetValue(vgs) as Array;
            if (passengers == null || index < 0 || index >= passengers.Length) return null;
            return passengers.GetValue(index) as GameObject;
        }

        static object Field(object owner, string name)
        {
            if (owner == null) return null;
            FieldInfo f = AccessTools.Field(owner.GetType(), name);
            return f == null ? null : f.GetValue(owner);
        }

        static int IntField(object owner, string name)
        {
            object v = Field(owner, name);
            return v is int ? (int)v : -1;
        }
    }

    /// <summary>
    /// The machine gun on its pintle, generated.
    ///
    /// WHY GENERATED. The same reason the settlement gun's vehicle is: a
    /// generated mesh costs no asset file, no entry in the launch receipt
    /// (ClientIntegrity rejects any file under plugins\assets the receipt does
    /// not know) and no make_assets.py run to install.
    ///
    /// IF A REAL MODEL IS DROPPED IN, IT WINS. Whenever assets\technical_mg.ndmesh
    /// (and optionally technical_mount.ndmesh and technical_diffuse.png) exist,
    /// they are loaded instead of the generated parts, so a proper model
    /// replaces this one without a code change. technical_build.py converts a
    /// source .glb into exactly those names. The generated shape is the
    /// fallback, not the intention.
    ///
    /// Everything here is modelled in METRES around the pintle foot. The caller
    /// scales the whole rig by the donor's own units-per-metre, so the gun is
    /// the right size on a vehicle whose model is not metric.
    ///
    /// Winding follows the rule verify.py enforces on the shipped meshes: the
    /// right-hand normal of each triangle's winding points the same way as its
    /// stored normal, so nothing is culled while it is lit.
    /// </summary>
    internal static class TechnicalModel
    {
        /// <summary>
        /// Height of the elevation pivot above the pintle foot, in metres. It
        /// is the ERGONOMIC number of the whole feature: the pintle stands on
        /// the same deck the gunner stands on, so this plus the grips' own
        /// small offset is exactly how high his hands end up. 1.10 m puts them
        /// where a standing man holds a spade grip; at the old 0.95 m he
        /// stooped for it.
        /// </summary>
        internal const float PivotHeight = 1.10f;

        /// <summary>The barrel axis height in the GUN's own space, and the two
        /// lengths the muzzle is built from.</summary>
        const float BoreY = 0.06f;
        const float BarrelEnd = 1.02f;
        const float MuzzleEnd = 1.14f;

        /// <summary>The spade grips: half the distance between them, and how
        /// far behind the pivot they stand. The generated gun puts its grip
        /// posts exactly here, and the gunner's hands are asked for the same
        /// two points.</summary>
        const float GripSide = 0.12f;
        const float GripBack = -0.30f;

        /// <summary>
        /// Where a hand belongs, in the GUN's own space. For the generated gun
        /// that is the spade grips it was built with. For a DELIVERED gun mesh
        /// nobody here has seen, it is derived from that mesh's own bounds - the
        /// back of the weapon, left and right of its axis - exactly as the
        /// muzzle is, and for the same reason: a number typed in for a model
        /// that was never measured is a guess.
        /// </summary>
        internal static Vector3 GripLocal(bool left)
        {
            Mesh g = GunMesh();
            if (g != null && _gunIsShipped)
            {
                Bounds b = g.bounds;
                return new Vector3(
                    b.center.x + (left ? -0.30f : 0.30f) * b.size.x,
                    b.center.y,
                    b.min.z + 0.12f * b.size.z);
            }
            return new Vector3(left ? -GripSide : GripSide, BoreY - 0.05f, GripBack);
        }

        /// <summary>
        /// How far a man's hands reach in front of his own root when he holds
        /// something at chest height, in metres. Together with the grips' own
        /// position it says where the gunner has to stand, which is why it is
        /// here beside them and not in the vehicle: it is a measurement of the
        /// PERSON.
        /// </summary>
        const float ArmForward = 0.42f;

        /// <summary>
        /// How far BEHIND the pintle axis the gunner's own root belongs, in
        /// metres: the grips sit behind the axis (see GripLocal - built in on
        /// the generated gun, measured on a delivered one), and his hands reach
        /// ArmForward in front of him. So the man is placed by the weapon that
        /// is actually mounted: a longer gun moves him back by itself, and
        /// nothing in the vehicle code has to know what it looks like.
        /// The floor of 0.30 m keeps a gun whose grips sit in FRONT of its pivot
        /// from pulling the gunner into the breech.
        /// </summary>
        internal static float StandOff()
        {
            return Mathf.Max(0.30f, ArmForward - GripLocal(true).z);
        }

        /// <summary>
        /// The muzzle in the GUN's space. The barrel points along its own +Z, so
        /// for the generated gun this is simply its length. For a DELIVERED gun
        /// mesh it is read back out of that mesh's own bounds instead - a model
        /// nobody here has seen cannot be assumed to be 1.14 m long, and a
        /// tracer that starts in the wrong place is the first thing a player
        /// notices.
        /// </summary>
        internal static Vector3 MuzzleLocal
        {
            get
            {
                Mesh g = GunMesh();
                if (g != null && _gunIsShipped)
                {
                    Bounds b = g.bounds;
                    return new Vector3(b.center.x, b.center.y, b.max.z);
                }
                return new Vector3(0f, BoreY, MuzzleEnd);
            }
        }

        static bool _gunIsShipped;

        /// <summary>Did a real gun model replace the generated one? Answering
        /// this builds the mesh if it has not been built yet, which is what the
        /// caller wants: the question is only ever asked once, at rebuild.</summary>
        internal static bool GunIsShipped()
        {
            GunMesh();
            return _gunIsShipped;
        }

        static Mesh _mount, _gun, _shield, _body;
        static bool _bodyTried;
        static Material _material, _bodyMaterial;

        /// <summary>
        /// The delivered vehicle body, or null when none was built. There is no
        /// generated fallback on purpose: without a model the technical is the
        /// donor's own body, which is a finished, textured, collision-correct
        /// vehicle - a box on wheels would be worse than what the game already
        /// has.
        /// </summary>
        internal static Mesh Body()
        {
            if (_bodyTried) return _body;
            _bodyTried = true;
            _body = Shipped("technical_body.ndmesh");
            return _body;
        }

        /// <summary>
        /// Material for a delivered body: the same imported atlas the gun uses,
        /// but a body is painted metal and not blued steel, so it gets its own
        /// instance with vehicle-like gloss instead of the gun's.
        /// </summary>
        internal static Material BodySkin()
        {
            if (_bodyMaterial != null) return _bodyMaterial;
            try
            {
                Shader sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
                Material m = new Material(sh);
                m.name = "NDR_Technical_Body_Material";
                Texture2D tex = null;
                try { tex = Assets.TextureIfPresent("technical_diffuse.png"); }
                catch { }
                if (tex != null) m.mainTexture = tex;
                Texture2D nrm = null;
                try { nrm = Assets.TextureIfPresent("technical_normal.png"); }
                catch { }
                if (nrm != null && m.HasProperty("_BumpMap"))
                {
                    m.SetTexture("_BumpMap", nrm);
                    m.EnableKeyword("_NORMALMAP");
                }
                Color sand = new Color(0.45f, 0.42f, 0.33f, 1f);
                if (m.HasProperty("_Color")) m.SetColor("_Color", tex == null ? sand : Color.white);
                if (tex == null) m.color = sand;
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.35f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.20f);
                _bodyMaterial = m;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TechnicalModel body material: " + ex.Message);
            }
            return _bodyMaterial;
        }

        /// <summary>
        /// Stand one pintle up. The caller owns the returned root (the YAW
        /// pivot) and receives the elevation pivot it has to drive.
        /// </summary>
        internal static GameObject Build(bool shield, out Transform gun)
        {
            gun = null;
            GameObject mount = new GameObject("NDR Technical Mount");
            Material m = Skin();

            Part(mount, MountMesh(), m);

            GameObject g = new GameObject("NDR Technical Gun");
            g.transform.SetParent(mount.transform, false);
            g.transform.localPosition = new Vector3(0f, PivotHeight, 0f);
            g.transform.localRotation = Quaternion.identity;
            Part(g, GunMesh(), m);

            if (shield)
            {
                GameObject s = new GameObject("NDR Technical Shield");
                s.transform.SetParent(g.transform, false);
                s.transform.localPosition = Vector3.zero;
                s.transform.localRotation = Quaternion.identity;
                Part(s, ShieldMesh(), m);
            }

            gun = g.transform;
            return mount;
        }

        static void Part(GameObject go, Mesh mesh, Material m)
        {
            if (mesh == null) return;
            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            if (m != null) mr.material = m;
        }

        static Material Skin()
        {
            if (_material != null) return _material;
            try
            {
                Shader sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
                Material m = new Material(sh);
                m.name = "NDR_Technical_Material";
                // A delivered gun brings its OWN atlas (technical_build.py packs
                // the gun's materials separately from the body's, because two
                // imports cannot share one UV layout). The body atlas is the
                // fallback for a model that was not split.
                Texture2D tex = null;
                try { tex = Assets.TextureIfPresent("technical_mg_diffuse.png"); }
                catch { }
                if (tex == null)
                {
                    try { tex = Assets.TextureIfPresent("technical_diffuse.png"); }
                    catch { }
                }
                if (tex != null) m.mainTexture = tex;
                // The atlas the importer writes beside the diffuse one. Without
                // it a delivered gun is flat-lit next to a body that has one.
                Texture2D nrm = null;
                try { nrm = Assets.TextureIfPresent("technical_mg_normal.png"); }
                catch { }
                if (nrm == null)
                {
                    try { nrm = Assets.TextureIfPresent("technical_normal.png"); }
                    catch { }
                }
                if (nrm != null && m.HasProperty("_BumpMap"))
                {
                    m.SetTexture("_BumpMap", nrm);
                    m.EnableKeyword("_NORMALMAP");
                }
                // Gun-metal, a shade darker than the olive of the settlement
                // gun: this is a weapon, not a vehicle body.
                Color gunmetal = new Color(0.16f, 0.16f, 0.15f, 1f);
                if (m.HasProperty("_Color")) m.SetColor("_Color", tex == null ? gunmetal : Color.white);
                if (tex == null) m.color = gunmetal;
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.30f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.45f);
                _material = m;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TechnicalModel material: " + ex.Message);
            }
            return _material;
        }

        /// <summary>A shipped mesh of this name, or null when there is none.
        /// Silent on purpose: no file is the normal case.</summary>
        static Mesh Shipped(string file)
        {
            try
            {
                string path = Path.Combine(RevivalPlugin.AssetDir, file);
                if (!File.Exists(path)) return null;
                Mesh m = Assets.Load(file);
                if (m != null)
                    RevivalPlugin.L.LogInfo("TechnicalModel: " + file + " geladen - "
                        + "das gelieferte Modell ersetzt das erzeugte Teil.");
                return m;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TechnicalModel: " + file + ": " + ex.Message);
                return null;
            }
        }

        // ------------------------------------------------------------- meshes

        /// <summary>Foot plate, pintle post and the traverse ring on top.</summary>
        static Mesh MountMesh()
        {
            if (_mount != null) return _mount;
            _mount = Shipped("technical_mount.ndmesh");
            if (_mount != null) return _mount;

            List<Vector3> v = new List<Vector3>();
            List<Vector3> n = new List<Vector3>();
            List<Vector2> uv = new List<Vector2>();
            List<int> tri = new List<int>();

            Box(v, n, uv, tri, new Vector3(0f, 0.03f, 0f),
                new Vector3(0.20f, 0.03f, 0.20f));
            Cyl(v, n, uv, tri, new Vector3(0f, 0.06f, 0f),
                new Vector3(0f, PivotHeight - 0.06f, 0f), 0.055f, 0.045f, 12);
            // Three legs, so the post is not a lonely pipe on a plate.
            for (int i = 0; i < 3; i++)
            {
                float a = i * Mathf.PI * 2f / 3f;
                Vector3 foot = new Vector3(Mathf.Cos(a) * 0.17f, 0.06f,
                                           Mathf.Sin(a) * 0.17f);
                Cyl(v, n, uv, tri, foot, new Vector3(0f, 0.42f, 0f), 0.018f, 0.014f, 6);
            }
            Cyl(v, n, uv, tri, new Vector3(0f, PivotHeight - 0.08f, 0f),
                new Vector3(0f, PivotHeight, 0f), 0.085f, 0.075f, 14);

            _mount = Finish(v, n, uv, tri, "NDR_Technical_Mount");
            return _mount;
        }

        /// <summary>
        /// Cradle, receiver, barrel, muzzle device, spade grips and the belt box
        /// on the right. Six shapes are what make a gun read as a gun rather
        /// than as a pipe on a stick.
        /// </summary>
        static Mesh GunMesh()
        {
            if (_gun != null) return _gun;
            _gun = Shipped("technical_mg.ndmesh");
            if (_gun != null) { _gunIsShipped = true; return _gun; }

            List<Vector3> v = new List<Vector3>();
            List<Vector3> n = new List<Vector3>();
            List<Vector2> uv = new List<Vector2>();
            List<int> tri = new List<int>();

            // Cradle: the yoke the gun swings in, straddling the pivot.
            Box(v, n, uv, tri, new Vector3(0f, 0f, 0f),
                new Vector3(0.07f, 0.05f, 0.20f));
            // Receiver.
            Box(v, n, uv, tri, new Vector3(0f, BoreY - 0.02f, 0.04f),
                new Vector3(0.065f, 0.085f, 0.34f));
            // Feed cover, a touch narrower and on top.
            Box(v, n, uv, tri, new Vector3(0f, BoreY + 0.08f, 0.02f),
                new Vector3(0.055f, 0.022f, 0.24f));
            // Barrel and its jacket.
            Cyl(v, n, uv, tri, new Vector3(0f, BoreY, 0.36f),
                new Vector3(0f, BoreY, 0.52f), 0.048f, 0.042f, 12);
            Cyl(v, n, uv, tri, new Vector3(0f, BoreY, 0.50f),
                new Vector3(0f, BoreY, BarrelEnd), 0.033f, 0.026f, 12);
            // Muzzle device.
            Cyl(v, n, uv, tri, new Vector3(0f, BoreY, BarrelEnd),
                new Vector3(0f, BoreY, MuzzleEnd), 0.046f, 0.042f, 12);
            // Front sight.
            Box(v, n, uv, tri, new Vector3(0f, BoreY + 0.06f, 0.94f),
                new Vector3(0.008f, 0.030f, 0.014f));
            // Rear sight.
            Box(v, n, uv, tri, new Vector3(0f, BoreY + 0.11f, -0.10f),
                new Vector3(0.020f, 0.022f, 0.012f));
            // Spade grips and the crosspiece between them - where the hands go.
            // Same two numbers as GripLocal, so the posts and the hands can
            // never drift apart.
            for (int s = -1; s <= 1; s += 2)
            {
                Box(v, n, uv, tri,
                    new Vector3(s * GripSide, BoreY - 0.09f, GripBack),
                    new Vector3(0.022f, 0.095f, 0.022f));
            }
            Box(v, n, uv, tri, new Vector3(0f, BoreY + 0.00f, GripBack),
                new Vector3(GripSide, 0.020f, 0.020f));
            // Belt box on the right, and the belt stub running into the feed.
            Box(v, n, uv, tri, new Vector3(0.155f, BoreY - 0.07f, 0.02f),
                new Vector3(0.075f, 0.075f, 0.13f));
            Box(v, n, uv, tri, new Vector3(0.10f, BoreY + 0.01f, 0.02f),
                new Vector3(0.030f, 0.010f, 0.030f));

            _gun = Finish(v, n, uv, tri, "NDR_Technical_Gun");
            return _gun;
        }

        /// <summary>A plain gun shield. Cosmetic - it stops nothing.</summary>
        static Mesh ShieldMesh()
        {
            if (_shield != null) return _shield;
            _shield = Shipped("technical_shield.ndmesh");
            if (_shield != null) return _shield;

            List<Vector3> v = new List<Vector3>();
            List<Vector3> n = new List<Vector3>();
            List<Vector2> uv = new List<Vector2>();
            List<int> tri = new List<int>();

            // Two side panels and a top strip, with the barrel port between them.
            for (int s = -1; s <= 1; s += 2)
            {
                Box(v, n, uv, tri, new Vector3(s * 0.22f, BoreY + 0.04f, 0.30f),
                    new Vector3(0.14f, 0.20f, 0.012f));
            }
            Box(v, n, uv, tri, new Vector3(0f, BoreY + 0.19f, 0.30f),
                new Vector3(0.36f, 0.05f, 0.012f));

            _shield = Finish(v, n, uv, tri, "NDR_Technical_Shield");
            return _shield;
        }

        static Mesh Finish(List<Vector3> v, List<Vector3> n, List<Vector2> uv,
                           List<int> tri, string name)
        {
            Mesh mesh = new Mesh();
            mesh.name = name;
            mesh.vertices = v.ToArray();
            mesh.normals = n.ToArray();
            mesh.uv = uv.ToArray();
            mesh.triangles = tri.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        // --------------------------------------------------- geometry helpers

        /// <summary>An axis-aligned box around <paramref name="c"/> with the
        /// half-sizes <paramref name="h"/>. Each face keeps its own four
        /// vertices so the normals stay flat, and every face is wound so its
        /// right-hand normal points out of the body.</summary>
        static void Box(List<Vector3> v, List<Vector3> n, List<Vector2> uv,
                        List<int> tri, Vector3 c, Vector3 h)
        {
            Face(v, n, uv, tri, c + new Vector3(0f, 0f, h.z), Vector3.forward,
                 new Vector3(h.x, 0f, 0f), new Vector3(0f, h.y, 0f));
            Face(v, n, uv, tri, c - new Vector3(0f, 0f, h.z), Vector3.back,
                 new Vector3(-h.x, 0f, 0f), new Vector3(0f, h.y, 0f));
            Face(v, n, uv, tri, c + new Vector3(h.x, 0f, 0f), Vector3.right,
                 new Vector3(0f, 0f, -h.z), new Vector3(0f, h.y, 0f));
            Face(v, n, uv, tri, c - new Vector3(h.x, 0f, 0f), Vector3.left,
                 new Vector3(0f, 0f, h.z), new Vector3(0f, h.y, 0f));
            Face(v, n, uv, tri, c + new Vector3(0f, h.y, 0f), Vector3.up,
                 new Vector3(-h.x, 0f, 0f), new Vector3(0f, 0f, h.z));
            Face(v, n, uv, tri, c - new Vector3(0f, h.y, 0f), Vector3.down,
                 new Vector3(h.x, 0f, 0f), new Vector3(0f, 0f, h.z));
        }

        /// <summary>One quad at <paramref name="centre"/> spanned by
        /// <paramref name="right"/> and <paramref name="up"/>, wound
        /// (0,1,2),(0,2,3). The right-hand normal of that winding is
        /// right x up, so the caller picks the two vectors in the order that
        /// makes it the OUTWARD normal.</summary>
        static void Face(List<Vector3> v, List<Vector3> n, List<Vector2> uv,
                         List<int> tri, Vector3 centre, Vector3 normal,
                         Vector3 right, Vector3 up)
        {
            int b = v.Count;
            v.Add(centre - right - up); n.Add(normal); uv.Add(new Vector2(0f, 0f));
            v.Add(centre + right - up); n.Add(normal); uv.Add(new Vector2(1f, 0f));
            v.Add(centre + right + up); n.Add(normal); uv.Add(new Vector2(1f, 1f));
            v.Add(centre - right + up); n.Add(normal); uv.Add(new Vector2(0f, 1f));
            tri.Add(b); tri.Add(b + 1); tri.Add(b + 2);
            tri.Add(b); tri.Add(b + 2); tri.Add(b + 3);
        }

        /// <summary>One closed, capped, possibly tapered cylinder.</summary>
        static void Cyl(List<Vector3> v, List<Vector3> n, List<Vector2> uv,
                        List<int> tri, Vector3 a, Vector3 b,
                        float ra, float rb, int sides)
        {
            Vector3 w = b - a;
            float h = w.magnitude;
            if (h < 1e-4f || sides < 3) return;
            w /= h;

            Vector3 helper = Mathf.Abs(w.y) > 0.9f ? Vector3.forward : Vector3.up;
            Vector3 u = Vector3.Cross(helper, w).normalized;
            Vector3 vv = Vector3.Cross(w, u);

            int sideBase = v.Count;
            for (int i = 0; i <= sides; i++)
            {
                float t = i * Mathf.PI * 2f / sides;
                Vector3 dir = u * Mathf.Cos(t) + vv * Mathf.Sin(t);
                Vector3 sn = (dir * h + w * (ra - rb)).normalized;
                float uu = (float)i / sides;
                v.Add(a + dir * ra); n.Add(sn); uv.Add(new Vector2(uu, 0f));
                v.Add(b + dir * rb); n.Add(sn); uv.Add(new Vector2(uu, 1f));
            }
            for (int i = 0; i < sides; i++)
            {
                int b0 = sideBase + i * 2;
                int t0 = b0 + 1;
                int b1 = b0 + 2;
                int t1 = b0 + 3;
                tri.Add(b0); tri.Add(t1); tri.Add(t0);
                tri.Add(b0); tri.Add(b1); tri.Add(t1);
            }

            int capTop = v.Count;
            v.Add(b); n.Add(w); uv.Add(new Vector2(0.5f, 0.5f));
            for (int i = 0; i <= sides; i++)
            {
                float t = i * Mathf.PI * 2f / sides;
                Vector3 dir = u * Mathf.Cos(t) + vv * Mathf.Sin(t);
                v.Add(b + dir * rb); n.Add(w);
                uv.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(t), 0.5f + 0.5f * Mathf.Sin(t)));
            }
            for (int i = 0; i < sides; i++)
            {
                tri.Add(capTop);
                tri.Add(capTop + 1 + i);
                tri.Add(capTop + 2 + i);
            }

            int capBottom = v.Count;
            v.Add(a); n.Add(-w); uv.Add(new Vector2(0.5f, 0.5f));
            for (int i = 0; i <= sides; i++)
            {
                float t = i * Mathf.PI * 2f / sides;
                Vector3 dir = u * Mathf.Cos(t) + vv * Mathf.Sin(t);
                v.Add(a + dir * ra); n.Add(-w);
                uv.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(t), 0.5f + 0.5f * Mathf.Sin(t)));
            }
            for (int i = 0; i < sides; i++)
            {
                tri.Add(capBottom);
                tri.Add(capBottom + 2 + i);
                tri.Add(capBottom + 1 + i);
            }
        }
    }
}
