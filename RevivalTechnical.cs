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
//      children; UralTruck.AddSeats for adding them. The BENCH those removed
//      points belonged to leaves the truck with them (Ruecksitze): a seat nobody
//      can take is furniture, and this piece of furniture was carrying the
//      machine gun.
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
// WHERE THE STATION STANDS (twice corrected from the game, both times from a
// picture; this is the second pass, field report technical.png of 2026-09-18:
// "das geschuetz ist viel zu niedrig, geht durch die scheibe, ist zwischen den
// sitzen anstatt solide hinten mit space um die stuetze drum rum").
//
// Pass one stood the station on a fraction of the measured bounding box, with
// the height fraction at 1.00 - the TOP of that box, which is the highest point
// of the whole vehicle, aerial and roof rack included, so gun and gunner hung in
// the air over the cabin. Pass two took the donor's own REAR SEAT instead. That
// fixed the floating and broke the position: a jeep's rear seat is INSIDE the
// cabin. Its height is the cabin floor, so the pintle - 1.10 m tall - reached
// exactly windscreen height, and the mount, standing one arm's length in FRONT
// of the man, landed between the two front seats. Both sentences of the report
// are that one derivation.
//
// A seat is where the game sits a man, not where a weapon can stand. The station
// is therefore derived from the two things that do answer the question:
//
//   * ALONG the vehicle, from the REAR EDGE of the measured body (MountRear).
//     The pintle stands in the rear quarter, and it is pushed forward only as
//     far as it must be for the gunner - who walks AROUND it, see below - to
//     keep body under his feet. It never passes the middle.
//   * UPWARDS, from the donor's own TOP SURFACE at that point: a ray is dropped
//     from above the measured box onto the donor's own colliders and the highest
//     hit is the deck (Oberflaeche). That is a surface that really exists, which
//     is what neither the bounding box nor the seat was. On a closed cabin it is
//     the roof, and gun and gunner stand on it with clear space around the
//     pintle - which is what was asked for.
//
// Under that measurement stand three fallbacks, in the order of how much each
// one knows: the top of the wide MESH parts over the rear (Dachkante - a box
// top, so an aerial welded into the body mesh can still push it up), then the
// donor's rear seat (its height plus the sit pose's measured foot offset,
// FeetAboveSeat, is the floor the game itself stands a man on), then the
// configured fraction. Which one was used is logged once per instance, together
// with everything else that was derived.
//
// AND OVER ALL FOUR OF THEM, A CEILING (third correction, field report of
// 2026-09-19: "aktuell steht das mg AUF der sitzreihe drauf, und der spieler
// steht fast wie auf einem ausguck auf dem ding. Die hintere sitzreihe muss weg
// [...] dann den mg stand runter moven und dann ans mg"). A measured surface is
// the highest thing that is REALLY there, and on this donor the highest thing
// over the rear was the rear BENCH: the pintle was bolted to its backrest and
// the gunner stood on top of it. Two things follow from that report, and
// together they are one fix:
//
//   * THE BENCH GOES (Ruecksitze). Nobody can sit on it anymore - the technical
//     has the two front seats and the standing place at the gun - so it is
//     furniture in the load area, and it is the furniture the station was
//     standing on. Its parts are found through the seat points this rebuild is
//     about to delete, never by name, and they are deactivated together with
//     their own colliders, so the ray that measures the deck reaches the floor.
//   * THE DECK IS A FLOOR, NOT A PERCH (DeckStep). Where the floor is, is
//     derivable, and the game itself says it: the rearmost seat point plus
//     FeetAboveSeat is where the game stands that passenger's feet. A load bed
//     is that floor or a step above it; a roof, a bonnet or a backrest is half a
//     man above it. So the measured surface is still used - it is the sharper
//     number, and on a donor with a real bed it IS the bed - but never more than
//     one step above the floor the donor's own passengers stand on. That is what
//     keeps gun and gunner on the bed even on a donor whose body is one closed
//     collider that no ray can see into.
//
// THE BENCH IS CUT OUT WHEN IT CANNOT BE SWITCHED OFF, AND THE STEP IS EARNED
// (fifth pass, docs/ai/tasks/technical-elevation-fix.md, field report of
// 2026-09-20, screenshots anothertechnicalbug and anothertechnicalbug2: the
// gunner still standing above the last row of seats). Two things follow:
//
//   * A donor whose cabin is ONE mesh - dashboard, front seats and rear bench
//     together - fails both guards above on purpose, and until this pass that
//     meant the bench simply stayed. It is now cut OUT of that shared mesh at
//     triangle level (BankSchnitt): every triangle whose centre stands in the
//     bench zone goes, every triangle that also stands in the same zone around
//     a FRONT seat point stays, and the result is a NEW mesh on that one
//     MeshFilter - the donor's own shared asset is never written to, or every
//     UAZ in the world would lose its back seat. Each LOD stage is cut the
//     same way, so the bench cannot come back with camera distance.
//   * Only a VISIBLE removal counts as confirmed - a whole part deactivated or
//     a mesh cut, never a collider on its own. When even the cut cannot run
//     (a mesh the engine refuses to read out), the surface DeckStep measures
//     is most likely the still-standing bench, not a genuine raised bed, so
//     the knee-height tolerance is not granted: the station is pulled all the
//     way down to the floor instead of one step above it. A confirmed removal
//     keeps the normal tolerance unchanged.
//
// Distances that measure a PERSON (the arm, the pintle height) are metres and
// are converted with the vehicle's own units-per-metre; distances that measure
// the VEHICLE are fractions of its own measured size, because the vehicle models
// are not metric (the BTR's 2.9 m track measures +-3.47 model units).
//
// THE STATION IS A CIRCLE, NOT A POINT. A man at a pintle mount walks around it;
// he does not stand on one spot and reach after the grips. The gunner is
// therefore placed one arm's length behind the mount ON THE MOUNT'S OWN BEARING
// every frame (TechnicalGun.Stellung), so the grips are in front of his chest at
// every angle. That is the rest of the same report - "sobald er dreht sieht es
// glitchy und falsch aus": at zero degrees the old fixed place looked right,
// and at ninety the grips stood beside the man while the arm solver dragged his
// arms across his own chest after them.
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

        /// <summary>
        /// How wide and deep a rear bench is, in METRES around its own seat
        /// point. These are the horizontal bounds within which a mesh part or a
        /// collider counts as part of the bench the rebuild takes away (see
        /// Ruecksitze) - furniture is the same size on a jeep and on a lorry, so
        /// it is metres here and not a fraction of the vehicle. The vertical
        /// bounds start from the implied floor below.
        /// </summary>
        const float BenchSide = 0.60f;
        const float BenchDepth = 0.70f;

        /// <summary>
        /// The bench once more, but measured from the FLOOR the seat point
        /// implies instead of from the seat point itself - metres above
        /// (seat point + FeetAboveSeat). This is the band BankSchnitt cuts.
        ///
        /// WHY A SECOND PAIR OF NUMBERS. A seat point is NOT the cushion: the
        /// game's sit clip floats the whole body above the root it is given
        /// (research/tank_crew_check.py, "the root is not the seat cushion"),
        /// and on the UAZ-3151 it measures root 0, body underside +0.90, feet
        /// +1.85, seated head +5.95, cabin roof +5.92 units. So the bench
        /// stands ABOVE the seat point by more than the height of a seated
        /// man's knees - the old zone over the seat point ended below the floor
        /// and could not hold a
        /// single triangle of the bench. That is why the whole-part test never
        /// found one on this donor and why the bench was still standing in the
        /// field report of 2026-09-20.
        ///
        /// Above the floor the same bench is plain: the cushion is about 0.30 m
        /// up, the backrest and its headrest reach about 1 m, and the cabin
        /// roof is a good 0.6 m higher still. The band starts just clear of the
        /// floor so the load floor itself is never cut.
        /// </summary>
        const float BenchFloorClear = 0.06f;
        const float BenchTopAboveFloor = 0.95f;

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<string> CfgKey;
        public static ConfigEntry<float> CfgDistance;
        public static ConfigEntry<float> CfgDurability;
        public static ConfigEntry<float> CfgMountRear;
        public static ConfigEntry<float> CfgDeckHeight;
        public static ConfigEntry<float> CfgDeckStep;
        public static ConfigEntry<bool> CfgRearBench;
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
            // Renamed from "MountBack" on purpose, and no longer a fallback: the
            // Lafette is placed from the rear edge on EVERY donor. The old key
            // was only used when the donor had no rear seat, and the seat it
            // gave way to is the reported bug - on a jeep it sits between the
            // cabin walls, so the gun stood between the front seats. A changed
            // default would not have reached anybody, because BepInEx keeps the
            // value already written in the config file; a changed KEY does.
            CfgMountRear = cfg.Bind("Technical", "MountRear", 0.26f,
                "Wo die Lafette auf dem Fahrzeug steht, als Anteil der "
                + "Fahrzeuglaenge von der HECKKANTE aus gemessen. 0 ist die "
                + "Heckkante, 1 die Bugkante. Der Wert wird nur nach vorn "
                + "korrigiert, und zwar so weit, dass der Schuetze hinter der "
                + "Lafette noch auf dem Fahrzeug steht; ueber die Mitte hinaus "
                + "kommt die Lafette nie. Alle Masse dieser Sektion sind "
                + "Anteile der gemessenen Modellgroesse und keine Meter: die "
                + "Fahrzeugmodelle des Spiels sind nicht metrisch.");
            // Renamed from "MountUpFallback" for the same reason: its default
            // was the vehicle floor (0.20), and the floor is exactly what put
            // the weapon into the windscreen. The deck height is measured on the
            // vehicle now (Oberflaeche); this value is only the last resort.
            CfgDeckHeight = cfg.Bind("Technical", "DeckFallback", 0.90f,
                "RUECKFALL: Hoehe der Standflaeche als Anteil der Fahrzeug"
                + "hoehe, vom tiefsten Punkt der Karosserie aus. Wird nur "
                + "benutzt, wenn weder eine Oberflaeche getroffen wird noch der "
                + "Spender einen Ruecksitz hat. 0.90 liegt knapp unter der "
                + "Oberkante des Aufbaus, also auf dem Dach - dort steht die "
                + "Lafette frei, waehrend sie auf dem Wagenboden durch die "
                + "Scheiben ragt. Auch dieser Wert wird von DeckStep wieder auf "
                + "Fussbodenhoehe heruntergezogen.");
            CfgDeckStep = cfg.Bind("Technical", "DeckStep", 0.15f,
                "OBERGRENZE der Standflaeche: wie weit sie hoechstens ueber dem "
                + "Fussboden des Spenders liegen darf, als Anteil der Fahrzeug"
                + "hoehe. Der Fussboden ist der Sitzpunkt des hintersten Sitzes "
                + "plus die gemessene Hoehe, in der das Spiel einen sitzenden "
                + "Koerper darueber schweben laesst - also der Boden, auf dem "
                + "die Insassen selbst stehen. Alles darueber ist eine Ladeflaeche "
                + "ueber dem Radkasten, und alles viel darueber ist ein Dach. "
                + "0.15 laesst eine knappe Stufe zu und verhindert, dass MG und "
                + "Schuetze auf der Sitzbank oder auf dem Dach landen "
                + "(Feldbericht 2026-09-19: 'das mg steht AUF der sitzreihe "
                + "drauf, der spieler wie auf einem ausguck'). 0 oder weniger "
                + "schaltet die Grenze ab.");
            CfgRearBench = cfg.Bind("Technical", "HideRearBench", true,
                "Die hintere Sitzbank des Spenders ausblenden. Auf der "
                + "Technischen gibt es nur noch zwei Sitze vorn und den "
                + "Stehplatz am MG - die Bank hinten ist also nur noch Mobiliar, "
                + "und zwar genau das, auf dem bisher das MG stand und in dem "
                + "der Schuetze steckte. Ausgeblendet "
                + "werden nur Teile, die an einem entfernten Sitzpunkt haengen "
                + "und zu klein fuer die Karosserie sind; ihre Kollisionskoerper "
                + "werden mit abgeschaltet, damit die gemessene Standflaeche der "
                + "Ladeboden ist und nicht die Lehne.");
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
            // NDR technical crew: the men who ride one on a convoy or patrol
            // route. Bound from here and not from RevivalPlugin, because the
            // crew is part of the technical and the plugin already calls this.
            TechnicalCrew.BindConfig(cfg);
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

            // FIRST take the donor's rear bench away, because the station is
            // measured on what is left. Nobody sits on that bench anymore - the
            // technical has the two front seats and the standing place at the
            // gun - and as long as it is there, it is the highest thing over the
            // rear: the pintle gets bolted to the backrest and the gunner ends up
            // standing on it (field report 2026-09-19). Its seat POINTS are still
            // in the list at this moment; Sitze removes them a few lines below.
            // The RETURN VALUE matters further down (DeckStep): a removal that
            // found nothing did not confirm the measured surface is a floor and
            // not the bench itself, so the tolerance built for a genuine raised
            // load bed must not be granted to it (field report
            // anothertechnicalbug/anothertechnicalbug2, 2026-09-20).
            int ruecksitzeEntfernt = Ruecksitze(car, seats, min, max, unitsPerMetre);

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
            // ALONG THE VEHICLE the pintle is placed from the REAR EDGE of the
            // measured body and pushed forward only as far as the gunner needs:
            // he stands one arm's length behind it and walks around it, so the
            // whole circle has to have body under it. It never passes the middle.
            // The donor's rear SEAT, which pass two used, is not that place - on
            // a jeep it sits between the cabin walls, which is how the gun came
            // to stand between the front seats (field report technical.png).
            float x = centreX + CfgMountSide.Value * width;
            float mountZ = min.z + Mathf.Max(CfgMountRear.Value * length,
                                             abstand + 0.06f * length);
            mountZ = Mathf.Min(mountZ, min.z + 0.50f * length);
            float standZ = mountZ - abstand;

            // UPWARDS from the donor's own TOP SURFACE at that point - a surface
            // that really exists, which is what neither the bounding box (pass
            // one: the station hung over the cabin) nor the rear seat (pass two:
            // the cabin floor, so the weapon reached into the windscreen) was.
            // The seat survives as the fallback for a donor whose colliders
            // cannot be hit, and the configured fraction under that.
            float deckY;
            string herkunft;
            Vector3 seatPoint;
            if (Oberflaeche(car, min, max, x, mountZ, out deckY))
                herkunft = "von der gemessenen Oberflaeche ueber dem Heck";
            else if (Dachkante(car, min, max, out deckY))
                herkunft = "von der Oberkante der Aufbauteile ueber dem Heck "
                    + "(kein Kollisionstreffer)";
            else if (Stehplatz(car.transform, seats, out seatPoint))
            {
                deckY = seatPoint.y + FeetAboveSeat / Hoehenmass(car.transform);
                herkunft = "aus dem hintersten Sitz des Spenders (keine "
                    + "Oberflaeche getroffen)";
            }
            else
            {
                deckY = min.y + CfgDeckHeight.Value * height;
                herkunft = "aus dem Anteil DeckFallback (weder Oberflaeche noch "
                    + "Ruecksitz)";
            }

            // THE DECK IS A FLOOR, NOT A PERCH. Field report 2026-09-19: "das mg
            // steht AUF der sitzreihe drauf, und der spieler steht fast wie auf
            // einem ausguck auf dem ding". A measured surface is the highest
            // thing that is REALLY there - and on this donor the highest thing
            // over the rear was the bench (now gone) and, above that, whatever
            // collider closes the body. Neither is a floor.
            //
            // The floor is derivable, and the game itself says where it is: the
            // rearmost seat point plus the sit pose's own offset is the height
            // the game stands that passenger's feet at. A cargo bed is that floor
            // or a step above it; a roof is a metre above it. So the measured
            // surface may still be used - it is the sharper number, and on a
            // donor with a real bed it IS the bed - but only up to one step over
            // the floor the donor's own passengers stand on.
            //
            // THAT STEP IS EARNED, NOT ASSUMED. A follow-up field report (screen-
            // shots anothertechnicalbug/anothertechnicalbug2, 2026-09-20) showed
            // the same symptom the fourth pass was meant to close: the gunner
            // still standing above the rear seats. Ruecksitze already tells the
            // caller when it could not confirm the bench was actually taken off
            // (a donor whose interior is one mesh shared with the front seats
            // fails both the size cap and the front-seat guard on purpose - see
            // its own header). In that case the "surface" just measured is most
            // likely the bench itself, not a genuine raised load bed, so the
            // knee-height tolerance a real bed would deserve is not granted: the
            // station is pulled all the way down to the floor instead, exactly
            // "das mg mit standfuss auf den boden des fahrzeuges" from the
            // report. A confirmed removal still keeps the normal tolerance.
            float boden;
            if (CfgDeckStep != null && CfgDeckStep.Value > 0f
                && Sitzboden(car.transform, seats, out boden))
            {
                float step = ruecksitzeEntfernt > 0 ? CfgDeckStep.Value * height : 0f;
                float grenze = boden + step;
                if (deckY > grenze)
                {
                    RevivalPlugin.L.LogInfo("Technical: die Standflaeche lag "
                        + (deckY - boden).ToString("0.00") + " Einheiten ueber dem "
                        + "Fussboden des Spenders (" + boden.ToString("0.00") + ") - "
                        + (ruecksitzeEntfernt > 0
                            ? "das ist ein Ausguck und keine Ladeflaeche."
                            : "die Ruecksitzbank konnte nicht bestaetigt entfernt "
                              + "werden, also KEIN Toleranzschritt.")
                        + " Heruntergezogen auf " + grenze.ToString("0.00")
                        + " (DeckStep " + CfgDeckStep.Value.ToString("0.00")
                        + (ruecksitzeEntfernt > 0 ? "" : ", hier 0 wirksam") + ").");
                    deckY = grenze;
                    herkunft = herkunft + ", auf Fussbodenhoehe heruntergezogen";
                }
            }

            float meshFloor;
            if (HullFloor(car, x, mountZ, out meshFloor))
            {
                deckY = meshFloor;
                herkunft = "from the UAZ hull floor mesh";
            }

            // Two clamps, so a donor that measures surprisingly can still only be
            // wrong by a little: the deck stays inside the measured body - up to
            // its very top, because that is where a deck belongs on a closed
            // cabin - and the man cannot hang off the tail.
            float deckClamped = Mathf.Clamp(deckY, min.y + 0.02f * height, max.y);
            float hinten = min.z + 0.02f * length;
            float standClamped = Mathf.Max(standZ, hinten);
            if (Mathf.Abs(deckClamped - deckY) > 0.001f
                || Mathf.Abs(standClamped - standZ) > 0.001f)
                RevivalPlugin.L.LogWarning("Technical: der abgeleitete Standplatz "
                    + "lag ausserhalb der Karosserie (y " + deckY.ToString("0.00")
                    + " -> " + deckClamped.ToString("0.00") + ", z "
                    + standZ.ToString("0.00") + " -> " + standClamped.ToString("0.00")
                    + ") und wurde hineingezogen.");
            deckY = deckClamped;
            standZ = standClamped;

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

            // The deck is normally the topmost surface of the donor, and then
            // there is nothing above it at all. If something IS above it and
            // there is less room than the pintle is tall, the weapon rises
            // through that part - which is worth one line in the log, because it
            // is the answer to a report of "the barrel goes through the roof".
            float kopfraum = max.y - deckY;
            float braucht = TechnicalModel.PivotHeight * unitsPerMetre;
            if (kopfraum > 0.05f * height && kopfraum < braucht)
                RevivalPlugin.L.LogInfo("Technical: ueber der Standflaeche liegen "
                    + kopfraum.ToString("0.00") + " Einheiten Aufbau, die Lafette "
                    + "ist " + braucht.ToString("0.00") + " hoch - das MG ragt "
                    + "also hindurch. Die Standflaeche ist die gemessene "
                    + "Oberflaeche des Spenders; ein Aufbau DARUEBER (Dachtraeger, "
                    + "Antenne) bleibt stehen, weil er nicht getragen wird.");
            // The other way round, and the price of standing the gun on the load
            // floor instead of on the roof: the cabin is then TALLER than the
            // pintle, so the barrel cannot look over it and a shot straight
            // ahead goes into the back of the cab. That is what the order "den
            // mg stand runter moven" buys, it is what a machine gun on a pickup
            // bed does in reality, and this line is the answer to the field
            // report it may produce. DeckStep raises the station again, MountRear
            // moves it back off the cabin.
            else if (kopfraum >= braucht)
                RevivalPlugin.L.LogInfo("Technical: die Lafette ist "
                    + braucht.ToString("0.00") + " Einheiten hoch, ueber der "
                    + "Standflaeche stehen aber " + kopfraum.ToString("0.00")
                    + " Einheiten Fahrzeug (Kabine). Das MG steht damit auf der "
                    + "Ladeflaeche UNTER der Kabinenoberkante und schiesst nicht "
                    + "ueber sie hinweg - so gewollt (Feldbericht 2026-09-19). "
                    + "Hoeher stellen: Technical/DeckStep.");
        }

        /// <summary>
        /// The donor's own TOP SURFACE at a point, in the ROOT's local space: a
        /// ray dropped from above the measured box onto the vehicle's own
        /// colliders, taking the highest hit that belongs to this vehicle.
        ///
        /// This is the one question the two earlier derivations both got wrong.
        /// A bounding box gives the highest POINT of the whole vehicle - an
        /// aerial, a roof rack - and a seat gives a place inside the cabin; a
        /// collider is the thing a pintle could actually be bolted to. Hits on
        /// anything that is not part of this vehicle are ignored, so a truck
        /// parked under a bridge measures the same as one in the open, and the
        /// wheels are ignored for the same reason the measured box ignores them.
        ///
        /// Returns false when nothing was hit - a donor whose colliders are not
        /// up yet at rebuild time, or sit on a layer the physics query does not
        /// see. The caller then falls back to the seat and to the fraction, and
        /// says in the log which of the three it used.
        /// </summary>
        static bool Oberflaeche(GameObject car, Vector3 min, Vector3 max,
                                float x, float z, out float y)
        {
            y = 0f;
            if (car == null) return false;
            Transform root = car.transform;
            float height = Mathf.Max(0.001f, max.y - min.y);

            Vector3 start = root.TransformPoint(
                new Vector3(x, max.y + 0.5f * height, z));
            Vector3 ziel = root.TransformPoint(new Vector3(x, min.y, z));
            Vector3 weg = ziel - start;
            float weite = weg.magnitude;
            if (weite < 0.0001f) return false;

            RaycastHit[] treffer = Physics.RaycastAll(start, weg / weite, weite);
            if (treffer == null) return false;

            bool any = false;
            for (int i = 0; i < treffer.Length; i++)
            {
                Collider col = treffer[i].collider;
                if (col == null || col.isTrigger) continue;
                if (!Gehoert(root, col.transform)) continue;
                if (IstRad(col.transform)) continue;
                float local = root.InverseTransformPoint(treffer[i].point).y;
                if (!any || local > y) { y = local; any = true; }
            }
            return any;
        }

        /// <summary>
        /// The rougher of the two surface measurements, and the fallback for the
        /// sharper one: the highest MESH part standing over the rear of the body,
        /// counting only parts wide enough to be a roof, a hardtop or a bed. An
        /// aerial, a mirror or a lamp is narrow and drops out, and that filter is
        /// the whole difference to the bounding box of the first pass, whose top
        /// was whatever stood highest anywhere on the vehicle.
        ///
        /// It is still a box top and not a surface - a donor whose whole body is
        /// ONE mesh measures that mesh, aerial included - which is why the
        /// collider ray is the measurement and this is only its fallback. It
        /// stands above the seat in that order all the same: too high by a
        /// fitting is a pintle standing proud, too low by a cabin is a pintle in
        /// the windscreen, and the second one is the reported bug.
        /// </summary>
        static bool Dachkante(GameObject car, Vector3 min, Vector3 max, out float y)
        {
            y = 0f;
            if (car == null) return false;
            Transform root = car.transform;
            float width = Mathf.Max(0.001f, max.x - min.x);
            float mitte = (min.z + max.z) * 0.5f;

            bool any = false;
            MeshFilter[] all = car.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < all.Length; i++)
            {
                MeshFilter mf = all[i];
                if (mf == null || mf.sharedMesh == null) continue;
                if (IstRad(mf.transform) || IstUnser(mf.transform)) continue;
                // A deck is something you can SEE. This drops the distance LODs
                // (their renderers are off while LOD0 is drawn) and the rear
                // bench, which Ruecksitze deactivated a moment ago - nothing may
                // be bolted to a part that is not on the truck anymore.
                Renderer rr = mf.GetComponent<Renderer>();
                if (rr == null || !rr.enabled) continue;
                if (!mf.gameObject.activeInHierarchy) continue;

                Vector3 lo, hi;
                if (!Huelle(root, mf, out lo, out hi)) continue;
                if (lo.z > mitte) continue;                  // nothing over the rear
                if (hi.x - lo.x < 0.40f * width) continue;   // an aerial, not a deck
                if (!any || hi.y > y) { y = hi.y; any = true; }
            }
            return any;
        }

        /// <summary>
        /// One mesh part's own box in the ROOT's local space. Mesh-local corners
        /// are transformed through the root, so a rotated or scaled instance
        /// measures the same as one standing at the origin - a world AABB would
        /// not.
        /// </summary>
        static bool Huelle(Transform root, MeshFilter mf, out Vector3 lo,
                           out Vector3 hi)
        {
            lo = Vector3.zero;
            hi = Vector3.zero;
            if (root == null || mf == null || mf.sharedMesh == null) return false;

            Bounds b = mf.sharedMesh.bounds;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new Vector3(
                    (c & 1) == 0 ? b.min.x : b.max.x,
                    (c & 2) == 0 ? b.min.y : b.max.y,
                    (c & 4) == 0 ? b.min.z : b.max.z);
                Vector3 p = root.InverseTransformPoint(
                    mf.transform.TransformPoint(corner));
                if (c == 0) { lo = p; hi = p; continue; }
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            return true;
        }

        /// <summary>Is this transform a part of that vehicle?</summary>
        static bool Gehoert(Transform root, Transform t)
        {
            while (t != null)
            {
                if (t == root) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// The donor's own FLOOR: the rear-most seat point this rebuild is about
        /// to remove, in the ROOT's local space. Returns false for a donor with
        /// nothing but the two front places, and then the caller falls back to
        /// the configured fraction.
        ///
        /// Only the HEIGHT of it is used, and only when no surface could be
        /// measured (see Oberflaeche): a seat is the one point on the vehicle the
        /// GAME itself guarantees a man fits on, so its height plus the sit
        /// pose's own offset (FeetAboveSeat) is a floor that certainly exists.
        /// Its POSITION along the vehicle is not the station - it is a place
        /// between the cabin walls, and using it stood the pintle between the
        /// front seats (field report technical.png, 2026-09-18).
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
        /// The floor the donor's OWN passengers stand on, in the root's local
        /// space: the rearmost seat point plus the sit pose's measured offset.
        ///
        /// This is not where the station goes - a seat answers no question about
        /// where a weapon can stand, which is the whole lesson of the second pass
        /// - but it is the one height on the vehicle the GAME itself guarantees a
        /// man's feet come to rest at. A cargo bed is that floor or a step above
        /// it, and a roof is a metre above it, so it is what the measured surface
        /// is held against (DeckStep). Taken from the rearmost seat because that
        /// is the one nearest the station: on a donor whose bench has already
        /// been hidden the POINT is still in the list, and the floor under a
        /// bench is the floor of the load area.
        /// </summary>
        static bool Sitzboden(Transform root, Transform seats, out float y)
        {
            y = 0f;
            if (root == null || seats == null) return false;

            bool any = false;
            float hinten = 0f;
            float sitz = 0f;
            for (int i = 0; i < seats.childCount; i++)
            {
                Transform c = seats.GetChild(i);
                if (c == null || c.name == SeatName) continue;
                Vector3 p = root.InverseTransformPoint(c.position);
                if (!any || p.z < hinten) { hinten = p.z; sitz = p.y; any = true; }
            }
            if (!any) return false;
            y = sitz + FeetAboveSeat / Hoehenmass(root);
            return true;
        }

        /// <summary>
        /// The donor's seat points, split into the places that STAY (driver and
        /// co-driver, by the same rule <see cref="Sitze"/> keeps them) and the
        /// ones this rebuild throws away. Root-local, and read while the points
        /// are all still there.
        /// </summary>
        static void Sitzpunkte(Transform root, Transform seats,
                               List<Vector3> vorn, List<Vector3> hinten)
        {
            if (root == null || seats == null) return;
            int keep = SeatTotal - 1;                 // driver + co-driver
            int seen = 0;
            for (int i = 0; i < seats.childCount; i++)
            {
                Transform c = seats.GetChild(i);
                if (c == null || c.name == SeatName) continue;
                seen++;
                Vector3 p = root.InverseTransformPoint(c.position);
                if (seen <= keep) vorn.Add(p); else hinten.Add(p);
            }
        }

        /// <summary>
        /// Take the donor's REAR BENCH off the truck: its parts are deactivated
        /// and its own colliders are switched off with them.
        ///
        /// Returns how many VISIBLE bench parts were actually removed - whole
        /// parts deactivated, plus meshes the bench was cut out of
        /// (BankSchnitt). Colliders deliberately do NOT count: a collider
        /// switched off behind a bench that is still drawn removes nothing a
        /// player can see. 0 therefore means removal could not be CONFIRMED
        /// (see the "nothing found" branch below), and the caller (Aufbauen)
        /// reads that to decide whether the DeckStep tolerance is earned: a
        /// surface measured over a bench that might still be standing there is
        /// not a raised load bed and gets no step at all.
        ///
        /// Field report 2026-09-19: "aktuell steht das mg AUF der sitzreihe
        /// drauf, und der spieler steht fast wie auf einem ausguck auf dem ding.
        /// Die hintere sitzreihe muss weg, gibt eh nur die 2 sitze vorne zum
        /// auswaehlen und der mg spot hinten." Both halves of that are this
        /// method and DeckStep: nobody can sit on the bench anymore - Sitze
        /// deletes its seat points a few lines further down - so it is furniture
        /// standing in the load area, and while it stands there it is the highest
        /// surface over the rear, which is exactly what the measured deck finds.
        ///
        /// WHICH PARTS. The bench is not identified by name - the donor's mesh
        /// names are the donor's business - but by the seat points that are about
        /// to be removed: a part counts when its centre is within a bench of one
        /// of them (BenchSide/Depth and the implied-floor height, in metres). Two guards keep the
        /// truck itself: nothing longer than 0.45 of the vehicle or wider than
        /// 0.95 of it can be furniture, and nothing that holds a FRONT seat point
        /// as well is touched - that is the shared interior, and hiding it would
        /// take the driver's seat with it.
        ///
        /// AND WHEN THERE IS NO SUCH PART. On a donor whose cabin is ONE mesh,
        /// those two guards refuse everything and the bench stays standing -
        /// which is what the field report of 2026-09-20 came back with. The
        /// bench is then cut out of the shared mesh at triangle level instead
        /// (BankSchnitt), so "die letzte sitzreihe MUSS WEG" holds on that donor
        /// too while the front seats stay whole.
        ///
        /// The COLLIDERS matter as much as the renderers: the deck is measured
        /// with a ray (Oberflaeche), and a bench whose backrest is invisible but
        /// still solid would still catch that ray. The same size and zone test
        /// decides, so the body shell and the wheels are never switched off - a
        /// technical that cannot be driven or hit would be a far worse bug than a
        /// visible bench.
        /// </summary>
        static int Ruecksitze(GameObject car, Transform seats, Vector3 min,
                              Vector3 max, float unitsPerMetre)
        {
            if (CfgRearBench != null && !CfgRearBench.Value) return 0;
            if (car == null || seats == null) return 0;

            Transform root = car.transform;
            List<Vector3> vorn = new List<Vector3>();
            List<Vector3> hinten = new List<Vector3>();
            Sitzpunkte(root, seats, vorn, hinten);
            if (hinten.Count == 0) return 0;          // a donor with two seats

            float length = Mathf.Max(0.001f, max.z - min.z);
            float width = Mathf.Max(0.001f, max.x - min.x);

            int weg = 0;
            int fest = 0;
            List<string> namen = new List<string>();

            // The colliders FIRST, while everything is still switched on: a
            // collider on a deactivated object reports no bounds at all, so the
            // other order would measure nothing.
            Collider[] cols = car.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider col = cols[i];
                if (col == null || !col.enabled || col.isTrigger) continue;
                if (IstRad(col.transform) || IstUnser(col.transform)) continue;
                MeshFilter furniture = col.GetComponent<MeshFilter>();
                if (furniture == null || furniture.name != "interior") continue;

                Vector3 lo, hi;
                if (!Kasten(root, col, out lo, out hi)) continue;
                if (!IstBank(root, lo, hi, vorn, hinten, length, width, unitsPerMetre))
                    continue;

                col.enabled = false;
                fest++;
            }

            // The parts themselves are DEACTIVATED and not merely hidden. A
            // LODGroup owns Renderer.enabled and rewrites it at every distance
            // threshold (the same fact Karosse stands on), so a bench whose
            // renderer is switched off is back as soon as the camera walks away
            // - and the bench exists once per LOD stage. An inactive object is
            // the last word for all of them, and it takes what hangs under it
            // with it.
            MeshFilter[] all = car.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < all.Length; i++)
            {
                MeshFilter mf = all[i];
                if (mf == null || mf.sharedMesh == null) continue;
                // The UAZ seats share interior; doors and hull are not furniture.
                if (mf.name != "interior") continue;
                if (IstRad(mf.transform) || IstUnser(mf.transform)) continue;
                if (mf.GetComponent<Renderer>() == null) continue;
                if (!mf.gameObject.activeSelf) continue;

                Vector3 lo, hi;
                if (!Huelle(root, mf, out lo, out hi)) continue;
                if (!IstBank(root, lo, hi, vorn, hinten, length, width, unitsPerMetre))
                    continue;

                mf.gameObject.SetActive(false);
                weg++;
                if (namen.Count < 6) namen.Add(mf.name);
            }

            // NOTHING TO HIDE IS NOT NOTHING TO REMOVE. If no whole part could
            // be taken off, the bench is not a part: on this donor it is
            // geometry inside a mesh it SHARES with the rest of the interior,
            // which fails the size cap and the front-seat guard above on
            // purpose - hiding that whole mesh would take the driver's seat and
            // the dashboard with it. The bench is then cut OUT of that mesh,
            // triangle by triangle, and only the triangles standing in the
            // bench zone go (BankSchnitt). "Die letzte sitzreihe MUSS WEG"
            // (field report 2026-09-20) is not satisfied by a lower gun alone.
            int geschnitten = 0;
            if (weg == 0)
                geschnitten = BankSchnitt(car, root, vorn, hinten, unitsPerMetre, namen);
            weg += geschnitten;

            if (weg == 0)
            {
                // Diagnostic only, so a repeat report does not need another blind
                // pass: how many mesh parts sit in the bench ZONE at all, before
                // the size cap and the front-seat guard get to reject any of
                // them. Zero here means the zone itself missed the bench (retune
                // BenchSide/Depth/FloorClear/TopAboveFloor); a positive count here means a
                // candidate existed but was too big or shared with a front seat
                // - the donor's interior is most likely one merged mesh, and
                // then not even the cut could read it (see BankSchnitt's own
                // warning), which is the case DeckStep trusts with zero
                // tolerance (see the caller).
                int inZoneUngeprueft = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    MeshFilter mf = all[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    // The UAZ seats share interior; doors and hull are not furniture.
                    if (mf.name != "interior") continue;
                    if (IstRad(mf.transform) || IstUnser(mf.transform)) continue;
                    Vector3 lo, hi;
                    if (!Huelle(root, mf, out lo, out hi)) continue;
                    if (ZoneBeruehrt(root, lo, hi, hinten, unitsPerMetre))
                        inZoneUngeprueft++;
                }

                RevivalPlugin.L.LogWarning("Technical: an den " + hinten.Count
                    + " entfernten Sitzpunkten haengt kein eigenes Teil (" + inZoneUngeprueft
                    + " Teile liegen in der Bankzone, wurden aber von der Groessen- "
                    + "oder Vordersitz-Pruefung verworfen), und auch aus dem "
                    + "gemeinsamen Innenraum-Mesh konnte nichts herausgeschnitten "
                    + "werden - die Sitzreihe bleibt SICHTBAR. " + fest
                    + " Kollisionskoerper sind trotzdem abgeschaltet. Die "
                    + "Standflaeche wird deshalb HART auf Fussbodenhoehe begrenzt, "
                    + "ohne DeckStep-Toleranz.");
                return 0;
            }

            RevivalPlugin.L.LogInfo("Technical: Ruecksitzbank entfernt - "
                + (weg - geschnitten) + " Teile ausgeblendet, " + geschnitten
                + " Meshes beschnitten" + (namen.Count > 0
                    ? " (" + string.Join(", ", namen.ToArray()) + ")" : "")
                + ", " + fest + " Kollisionskoerper abgeschaltet. Darauf stand "
                + "sonst die Lafette.");
            // ONLY the visible bench counts as a confirmed removal. A collider
            // switched off behind a bench that is still drawn is not the thing
            // the report asked for, and it must not buy the DeckStep tolerance
            // in the caller either.
            return weg;
        }

        /// <summary>
        /// Cut the rear bench OUT OF THE MESH it shares with the rest of the
        /// interior. Returns the number of meshes that lost geometry.
        ///
        /// This is the answer to the donor the whole-part pass above cannot
        /// serve: a cabin modelled as ONE mesh - dashboard, front seats and
        /// rear bench together - is both too big for the size cap and holds a
        /// front seat point, so it is refused there on purpose, and refusing it
        /// leaves the bench standing. Geometry does not care about object
        /// boundaries, so the bench is taken out at TRIANGLE level instead: a
        /// triangle goes when its centre stands in the bench zone of a seat
        /// point this rebuild is about to delete (BenchSide/Depth and the
        /// implied-floor height, the same zone the whole-part test uses), and it stays when it also
        /// stands in the same zone around a FRONT seat point - between two rows
        /// the front seat wins, because a driver's seat with a hole in it is a
        /// worse bug than a bench remnant.
        ///
        /// THE DONOR'S OWN MESH IS NEVER TOUCHED. A Mesh asset is shared by
        /// every vehicle of that kind in the scene (and by the next one the
        /// game loads), so writing into it would cut the bench out of every UAZ
        /// in the world. A NEW mesh is built with the same vertices and the
        /// surviving triangles, and only this MeshFilter is pointed at it.
        ///
        /// Every LOD stage is its own mesh and is cut with the same test, so
        /// the bench cannot come back when the camera walks away.
        ///
        /// THE FRONT-SEAT GUARD. A triangle in the zone of a front seat stays,
        /// even when it also reaches the rear zone. A mesh the engine will not
        /// read out (model importers commonly ship with Read/Write disabled) is
        /// skipped rather than guessed at - the caller then reports the bench as
        /// NOT removed, which is what keeps the DeckStep tolerance at zero.
        /// </summary>
        static int BankSchnitt(GameObject car, Transform root, List<Vector3> vorn,
                               List<Vector3> hinten, float unitsPerMetre,
                               List<string> namen)
        {
            if (car == null || root == null || hinten == null || hinten.Count == 0)
                return 0;

            int geschnitten = 0;
            bool lesefehler = false;
            MeshFilter[] all = car.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < all.Length; i++)
            {
                MeshFilter mf = all[i];
                if (mf == null || mf.sharedMesh == null) continue;
                // The UAZ seats share interior; doors and hull are not furniture.
                if (mf.name != "interior") continue;
                if (IstRad(mf.transform) || IstUnser(mf.transform)) continue;
                if (mf.GetComponent<Renderer>() == null) continue;
                if (!mf.gameObject.activeSelf) continue;

                Vector3 lo, hi;
                if (!Huelle(root, mf, out lo, out hi)) continue;
                if (!ZoneBeruehrt(root, lo, hi, hinten, unitsPerMetre)) continue;

                try
                {
                    if (Schneiden(mf, root, vorn, hinten, unitsPerMetre))
                    {
                        geschnitten++;
                        if (namen.Count < 6) namen.Add(mf.name + " (Schnitt)");
                    }
                }
                catch (Exception ex)
                {
                    if (!lesefehler)
                    {
                        lesefehler = true;
                        RevivalPlugin.L.LogWarning("Technical: das Mesh \""
                            + mf.name + "\" laesst sich nicht auslesen ("
                            + ex.Message + ") - aus einem solchen Mesh kann die "
                            + "Ruecksitzbank nicht herausgeschnitten werden.");
                    }
                }
            }
            return geschnitten;
        }

        /// <summary>
        /// One mesh: build the same mesh without the triangles standing in the
        /// bench zone, and point this MeshFilter at it. Returns false when
        /// nothing was in the zone after all.
        /// </summary>
        static bool Schneiden(MeshFilter mf, Transform root, List<Vector3> vorn,
                              List<Vector3> hinten, float unitsPerMetre)
        {
            Mesh alt = mf.sharedMesh;
            int sub = alt.subMeshCount;
            if (sub <= 0) return false;

            Vector3[] ecken = alt.vertices;      // throws on an unreadable mesh
            if (ecken == null || ecken.Length == 0) return false;

            // Mesh-local to ROOT-local in one matrix, so a rotated or scaled
            // part measures the same as one standing at the origin - the same
            // reason Huelle transforms its corners through the root.
            Matrix4x4 m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            Vector3[] lokal = new Vector3[ecken.Length];
            for (int v = 0; v < ecken.Length; v++)
                lokal[v] = m.MultiplyPoint3x4(ecken[v]);

            int[][] behalten = new int[sub][];
            int raus = 0;
            int gesamt = 0;
            for (int s = 0; s < sub; s++)
            {
                int[] tri = alt.GetTriangles(s);
                if (tri == null) { behalten[s] = new int[0]; continue; }

                List<int> keep = new List<int>(tri.Length);
                for (int k = 0; k + 2 < tri.Length; k += 3)
                {
                    gesamt++;
                    // Only the interior mesh reaches here. The midpoint between
                    // the two seat rows separates its rear bench from front seats.
                    if (RearInteriorTriangle(lokal[tri[k]], lokal[tri[k + 1]],
                                             lokal[tri[k + 2]], vorn, hinten))
                    {
                        raus++;
                        continue;
                    }
                    keep.Add(tri[k]);
                    keep.Add(tri[k + 1]);
                    keep.Add(tri[k + 2]);
                }
                behalten[s] = keep.ToArray();
            }

            if (raus == 0 || gesamt == 0) return false;

            Mesh neu = new Mesh();
            neu.name = alt.name + "_NDR_ohneBank";
            neu.vertices = ecken;

            Vector3[] normalen = alt.normals;
            if (normalen != null && normalen.Length == ecken.Length) neu.normals = normalen;
            Vector4[] tangenten = alt.tangents;
            if (tangenten != null && tangenten.Length == ecken.Length) neu.tangents = tangenten;
            Vector2[] uv = alt.uv;
            if (uv != null && uv.Length == ecken.Length) neu.uv = uv;
            Vector2[] uv2 = alt.uv2;
            if (uv2 != null && uv2.Length == ecken.Length) neu.uv2 = uv2;
            Color[] farben = alt.colors;
            if (farben != null && farben.Length == ecken.Length) neu.colors = farben;

            neu.subMeshCount = sub;
            for (int s = 0; s < sub; s++)
                neu.SetTriangles(behalten[s] == null ? new int[0] : behalten[s], s);
            neu.RecalculateBounds();

            // sharedMesh and not mesh: this points THIS filter at the new mesh.
            // The donor's own asset is not written to at all - see the header.
            mf.sharedMesh = neu;
            RevivalPlugin.L.LogInfo("Technical: aus \"" + mf.name + "\" "
                + raus + " von " + gesamt + " Dreiecken herausgeschnitten - das "
                + "ist die Ruecksitzbank im gemeinsamen Innenraum-Mesh.");
            return true;
        }

        // The open UAZ hull has a double-sided rear floor. Use its upper
        // surface, not the bounding collider, seat root, or seat cushions.
        static bool HullFloor(GameObject car, float x, float z, out float y)
        {
            y = float.MinValue;
            MeshFilter[] filters = car.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null
                    || mf.sharedMesh.name != "hull") continue;
                try
                {
                    Vector3[] vertices = mf.sharedMesh.vertices;
                    int[] triangles = mf.sharedMesh.triangles;
                    for (int k = 0; k < vertices.Length; k++)
                        vertices[k] = car.transform.InverseTransformPoint(
                            mf.transform.TransformPoint(vertices[k]));
                    for (int k = 0; k + 2 < triangles.Length; k += 3)
                    {
                        Vector3 a = vertices[triangles[k]];
                        Vector3 b = vertices[triangles[k + 1]] - a;
                        Vector3 c = vertices[triangles[k + 2]] - a;
                        float det = b.x * c.z - c.x * b.z;
                        if (Mathf.Abs(det) < 0.000001f) continue;
                        float u = ((x - a.x) * c.z - (z - a.z) * c.x) / det;
                        float v = (b.x * (z - a.z) - b.z * (x - a.x)) / det;
                        if (u < 0f || v < 0f || u + v > 1f) continue;
                        y = Mathf.Max(y, a.y + u * b.y + v * c.y);
                    }
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Technical: hull floor unavailable: " + ex.Message);
                }
            }
            return y != float.MinValue;
        }

        static bool RearInteriorTriangle(Vector3 a, Vector3 b, Vector3 c,
                                         List<Vector3> front, List<Vector3> rear)
        {
            if (front.Count == 0 || rear.Count == 0) return false;
            float backOfFront = float.MaxValue;
            float frontOfRear = float.MinValue;
            for (int i = 0; i < front.Count; i++)
                backOfFront = Mathf.Min(backOfFront, front[i].z);
            for (int i = 0; i < rear.Count; i++)
                frontOfRear = Mathf.Max(frontOfRear, rear[i].z);
            if (frontOfRear >= backOfFront) return false;
            float split = (backOfFront + frontOfRear) * 0.5f;
            return a.z < split && b.z < split && c.z < split;
        }

        /// <summary>Does this point stand in the bench zone of one of those
        /// seat points? The same box the whole-part test uses, but around a
        /// POINT instead of around a part's centre.</summary>
        static bool InBankZone(Transform root, Vector3 p, List<Vector3> punkte,
                               float unitsPerMetre)
        {
            if (root == null || punkte == null) return false;
            float floor = FeetAboveSeat / Hoehenmass(root);
            for (int i = 0; i < punkte.Count; i++)
            {
                Vector3 d = p - punkte[i];
                if (Mathf.Abs(d.x) <= BenchSide * unitsPerMetre
                    && Mathf.Abs(d.z) <= BenchDepth * unitsPerMetre
                    && d.y >= floor + BenchFloorClear * unitsPerMetre
                    && d.y <= floor + BenchTopAboveFloor * unitsPerMetre)
                    return true;
            }
            return false;
        }

        /// <summary>Does this box reach into the bench zone of one of those
        /// seat points at all? The cheap test that keeps the triangle walk off
        /// every mesh on the vehicle.</summary>
        static bool ZoneBeruehrt(Transform root, Vector3 lo, Vector3 hi,
                                 List<Vector3> punkte, float unitsPerMetre)
        {
            if (root == null || punkte == null) return false;
            float floor = FeetAboveSeat / Hoehenmass(root);
            for (int i = 0; i < punkte.Count; i++)
            {
                Vector3 p = punkte[i];
                if (hi.x < p.x - BenchSide * unitsPerMetre) continue;
                if (lo.x > p.x + BenchSide * unitsPerMetre) continue;
                if (hi.z < p.z - BenchDepth * unitsPerMetre) continue;
                if (lo.z > p.z + BenchDepth * unitsPerMetre) continue;
                if (hi.y < p.y + floor + BenchFloorClear * unitsPerMetre) continue;
                if (lo.y > p.y + floor + BenchTopAboveFloor * unitsPerMetre) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Is this box a piece of the rear bench? Size first (a part of the
        /// vehicle itself can never be), then the front seats (the shared
        /// interior is not the bench), then the zone around a removed seat point.
        /// </summary>
        static bool IstBank(Transform root, Vector3 lo, Vector3 hi, List<Vector3> vorn,
                            List<Vector3> hinten, float length, float width,
                            float unitsPerMetre)
        {
            if (root == null) return false;
            if (hi.z - lo.z > 0.45f * length) return false;   // the body, not a seat
            if (hi.x - lo.x > 0.95f * width) return false;    // spans the vehicle

            for (int i = 0; i < vorn.Count; i++)
                if (Drin(lo, hi, vorn[i], 0.05f * length)) return false;
                else if (lo.x <= vorn[i].x + BenchSide * unitsPerMetre
                    && hi.x >= vorn[i].x - BenchSide * unitsPerMetre
                    && lo.z <= vorn[i].z + BenchDepth * unitsPerMetre
                    && hi.z >= vorn[i].z - BenchDepth * unitsPerMetre)
                    return false; // Seat roots lie BELOW the cushions.

            float floor = FeetAboveSeat / Hoehenmass(root);
            Vector3 mitte = (lo + hi) * 0.5f;
            for (int i = 0; i < hinten.Count; i++)
            {
                Vector3 d = mitte - hinten[i];
                if (Mathf.Abs(d.x) <= BenchSide * unitsPerMetre
                    && Mathf.Abs(d.z) <= BenchDepth * unitsPerMetre
                    && d.y >= floor + BenchFloorClear * unitsPerMetre
                    && d.y <= floor + BenchTopAboveFloor * unitsPerMetre)
                    return true;
            }
            return false;
        }

        /// <summary>Is the point inside the box, with a margin?</summary>
        static bool Drin(Vector3 lo, Vector3 hi, Vector3 p, float m)
        {
            return p.x >= lo.x - m && p.x <= hi.x + m
                && p.y >= lo.y - m && p.y <= hi.y + m
                && p.z >= lo.z - m && p.z <= hi.z + m;
        }

        /// <summary>
        /// A collider's box in the ROOT's local space. Collider.bounds is a world
        /// AABB, so its eight corners are transformed back through the root the
        /// same way <see cref="Huelle"/> transforms a mesh's - which makes the
        /// measurement independent of where the vehicle stands and how it is
        /// turned. It is a box around a box and therefore never too small, which
        /// is the safe direction here: a part that measures too big fails the
        /// size guard and simply stays.
        /// </summary>
        static bool Kasten(Transform root, Collider col, out Vector3 lo,
                           out Vector3 hi)
        {
            lo = Vector3.zero;
            hi = Vector3.zero;
            if (root == null || col == null) return false;

            Bounds b = col.bounds;
            if (b.size.sqrMagnitude <= 0f) return false;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new Vector3(
                    (c & 1) == 0 ? b.min.x : b.max.x,
                    (c & 2) == 0 ? b.min.y : b.max.y,
                    (c & 4) == 0 ? b.min.z : b.max.z);
                Vector3 p = root.InverseTransformPoint(corner);
                if (c == 0) { lo = p; hi = p; continue; }
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            return true;
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
        /// height down by a wheel radius. Each part is measured by
        /// <see cref="Huelle"/>, which is why a rotated or scaled instance
        /// measures the same as one standing at the origin.
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

                Vector3 lo, hi;
                if (!Huelle(root, mf, out lo, out hi)) continue;
                if (!any) { min = lo; max = hi; any = true; continue; }
                min = Vector3.Min(min, lo);
                max = Vector3.Max(max, hi);
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
                // The riding crew FIRST: it puts the men where the truck is and
                // lays the gun, and TechnicalGun.LateAll below then walks the
                // gunner around the pintle it has just aimed and solves his
                // hands onto the grips. The other order leaves one of the three
                // a frame behind the other two, which is what a turning gunner
                // looks wrong from.
                TechnicalCrew.LateFrame();
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
            // NDR technical crew: which technicals are manned, who their men
            // are and - on the master - putting them there. Same 5 Hz pass and
            // the same shared scan, for the same reason.
            TechnicalCrew.Scan(all);
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
        public static ConfigEntry<float> CfgTracerSpeed;
        public static ConfigEntry<float> CfgTracerLength;
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
        public static ConfigEntry<bool> CfgOrbit;
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
            CfgTracerSpeed = cfg.Bind("TechnicalGun", "TracerSpeed", 700f,
                "Welteinheiten je Sekunde, mit denen die Leuchtspur sichtbar "
                + "vom Lauf zum Ziel FLIEGT, statt die ganze Strecke in einem "
                + "Bild zu zeichnen. Feldbericht 2026-09-20 "
                + "(anothertechnicalbug/anothertechnicalbug2): \"aktuell sind "
                + "die schuesse diese langweiligen raytraces, ich will das die "
                + "schuesse aussehen so wie die schuesse aus der dragunov aber "
                + "halt mit hohem frequenz\". Die Feuerrate selbst ist schon "
                + "hoch (FireDelay, neun Schuss je Sekunde); dieser Wert macht "
                + "aus jedem einzelnen Schuss ein fliegendes Geschoss statt "
                + "einer sofort erscheinenden Linie. Bei mehreren Schuss "
                + "gleichzeitig in der Luft entsteht so der Strom aus "
                + "Leuchtspurgeschossen, den ein MG im Dauerfeuer zeigt.");
            CfgTracerLength = cfg.Bind("TechnicalGun", "TracerLength", 6f,
                "Laenge des fliegenden Leuchtspur-Streifens in Welteinheiten. "
                + "Kuerzer als die Reichweite: es ist ein Streifen, der ueber "
                + "die Strecke wandert, keine durchgezogene Linie vom Lauf bis "
                + "zum Ziel.");
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
                "Sample the native standing idle pose before arm IK for every gunner. "
                + "Vehicle controls and passenger state remain owned by the game.");
            CfgStandPoseValue = cfg.Bind("TechnicalGun", "StandPoseValue", 0,
                "Legacy setting, retained for compatibility; no longer used. "
                + "Standing now samples idle_alert instead of changing vehicle state.");
            CfgHands = cfg.Bind("TechnicalGun", "HandsOnGun", true,
                "Die Haende des Schuetzen auf die Griffe des MG legen. Das "
                + "Spiel hat fuer eine Lafette keine eigene Animation, deshalb "
                + "werden die Armknochen NACH der Animation (LateUpdate) auf "
                + "die Griffe gerechnet - fuer den eigenen Schuetzen und fuer "
                + "den jedes Mitspielers. Findet sich das Skelett nicht, "
                + "passiert nichts und es steht einmal im Log.");
            CfgOrbit = cfg.Bind("TechnicalGun", "GunnerOrbit", true,
                "Den Schuetzen bei jedem Schwenk HINTER seine Waffe stellen, "
                + "statt ihn auf einem festen Punkt der Ladeflaeche stehen zu "
                + "lassen. Ein Mann an einer Lafette geht um sie herum; steht er "
                + "fest, liegen die Griffe beim Geradeausschauen vor ihm und bei "
                + "90 Grad neben ihm, und der Armrechner zieht ihm die Arme vor "
                + "die Brust - genau das meldete das Feldbild technical.png "
                + "(\"sobald er dreht sieht es glitchy und falsch aus\"). Der "
                + "Standpunkt kommt aus der Lafette selbst, also ohne neue "
                + "Netzwerknachricht. Abschalten stellt den Schuetzen wieder "
                + "fest auf seinen Sitzpunkt.");
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

        /// <summary>
        /// The body at this vehicle's gun: the player in the gunner's place if
        /// there is one, and otherwise the NPC riding there
        /// (RevivalTechnicalCrew.cs). Everything in this class that used to ask
        /// PassengerAt for the gunner asks here instead, so the standing place,
        /// the walk around the pintle, the hands on the grips and the remote
        /// slew all work on a crew NPC without knowing it is one.
        ///
        /// A PLAYER ALWAYS WINS. He is the one the seat system knows about, and
        /// two bodies claiming one station would fight over it every frame.
        /// </summary>
        static GameObject GunnerBody(Component vgs)
        {
            GameObject body = PassengerAt(vgs, Technical.GunnerSeat);
            if (body != null) return body;
            return TechnicalCrew.GunnerBody(vgs);
        }

        /// <summary>True when this vehicle's gun is laid by its own crew and not
        /// by a player. The NPC gunner's own code writes the mount, so the slew
        /// that follows a body's bearing must keep its hands off it: the two
        /// would take turns overwriting each other in the same frame.</summary>
        static bool NpcManned(Component vgs)
        {
            return PassengerAt(vgs, Technical.GunnerSeat) == null
                && TechnicalCrew.GunnerBody(vgs) != null;
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
                if (!_atGun)
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
            // Before the "nothing changes" exit, and not after it: whoever is
            // NOT on the gun must not have a reload bar, no matter what the
            // flag already said. The gun is let go on more paths than it is
            // taken (camera lost, vehicle gone, an exception in Tick), and one
            // of them arriving with _manning already false used to leave the
            // bar standing on the HUD. Same order as Turret.SetManning.
            if (!on) Ladeabbruch();
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
            GameObject passenger = PassengerAt(_vgs, IntField(_vgs, "_localPlayerPassengerId"));
            Type managerType = RevivalPlugin.TypeByName("PlayerVehicleManager");
            object pvm = passenger == null || managerType == null ? null
                : passenger.GetComponent(managerType);
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

                GameObject body = GunnerBody(vgs);
                if (body == null) return;
                // A gun the riding crew is laying itself: its own aim is the
                // truth on this machine, and reading the bearing back off the
                // gunner would only undo it. On every OTHER machine the man's
                // replicated rotation is the only bearing there is, so the slew
                // below is exactly what carries it - see the header of
                // RevivalTechnicalCrew.cs.
                if (NpcManned(vgs) && TechnicalCrew.Aiming(vgs)) return;
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
            public GameObject PoseBody, AnimationObject;
            public object StandClip;
            public MethodInfo SampleStand;
            public bool PoseTried;
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
        /// of a vehicle somebody else is shooting from, where its gunner stands,
        /// and the arms of every gunner - the local one and each remote one.
        ///
        /// This has to be LateUpdate. The game's animator writes the character's
        /// bones every frame between Update and LateUpdate; a hand put on a grip
        /// in Update is back at the man's side before anything is drawn. The
        /// standing place belongs in the same pass for the same reason: the man
        /// has to be where he will be DRAWN before his arms are solved.
        /// </summary>
        internal static void LateAll()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.Vgs == null) continue;
                // In this order, and all three in the same late frame: the mount
                // is final before the man is placed behind it, and the man is
                // final before his arms are solved onto the grips. Any other
                // order leaves one of the three a frame behind the other two,
                // which is what a turning gunner looks wrong from.
                SlewRemote(st.Vgs);                       // returns at once for our own
                GameObject body = GunnerBody(st.Vgs);

                // Who owns the bearing here? We do while WE man this gun - the
                // mouse turns the mount and the man is turned with it. Otherwise
                // the man owns it: SlewRemote has just read the mount's bearing
                // off his body, so writing his rotation back would be a loop.
                bool selbst = _manning && ReferenceEquals(st.Vgs, _vgs);
                // A local gunner who let go on purpose (G) is left alone
                // altogether: he is standing in the place, not working the gun,
                // and it is HIS bearing the mount is following.
                bool losgelassen = !selbst && _atGun
                                   && ReferenceEquals(st.Vgs, _vgs);
                StandingPose(st, body);
                if (!losgelassen) Stellung(st, body, selbst);

                Hands(st, body, dt);
            }
        }

        /// <summary>
        /// Put the gunner BEHIND his gun - on the far side of the pintle from the
        /// muzzle, at every bearing. A man at a pintle mount walks around it; he
        /// does not stand on one spot and reach after the grips.
        ///
        /// This is the second half of the field report technical.png: "der
        /// spieler hat zwar die haende am mg beim grade ausgucken, aber sobald er
        /// dreht sieht es glitchy und falsch aus". The place was one fixed point
        /// on the deck. Straight ahead the grips were in front of his chest and
        /// everything solved cleanly; at ninety degrees they stood beside him,
        /// and <see cref="Arm"/> - which always reaches, because that is what an
        /// IK solver does - dragged his arms across his own body after them. The
        /// station is a CIRCLE, and the radius is the same StandOff the seat
        /// point was built from, so the picture at zero degrees is unchanged.
        ///
        /// The standing point is asked of the MOUNT'S OWN space, so it carries
        /// the vehicle's units-per-metre, the chassis frame and the suspension
        /// with it and there is no scale arithmetic here at all. No network
        /// message either: every client computes the same circle from the same
        /// mount, exactly as <see cref="SlewRemote"/> computes the same bearing.
        /// </summary>
        static void Stellung(Station st, GameObject body, bool drehen)
        {
            try
            {
                if (CfgOrbit == null || !CfgOrbit.Value) return;
                if (body == null || st.Mount == null) return;

                Vector3 mitte = st.Mount.position;
                Vector3 stand = st.Mount.TransformPoint(
                    new Vector3(0f, 0f, -TechnicalModel.StandOff()));
                float radius = Vector3.Distance(stand, mitte);
                if (radius < 0.0001f) return;

                // Never DRAG a body that is not at this station anymore: one
                // that died, got out, or was moved by the game belongs to the
                // game. Three radii is far more than the place is wide, and a
                // man who has just been seated is at its centre-most point.
                //
                // A RIDING CREW'S GUNNER IS EXEMPT. He was named as this gun's
                // gunner by the key he carries, not by where he happens to
                // stand, and on a machine that is not the master his replicated
                // position trails the moving truck by whatever the last sync
                // cost. Disowning him for that is precisely the lag this whole
                // arrangement exists to remove.
                if (!TechnicalCrew.IsRider(st.Vgs, body)
                    && Vector3.Distance(body.transform.position, mitte) > 3f * radius)
                    return;

                body.transform.position = stand;
                if (!drehen) return;

                Vector3 dir = st.Mount.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.000001f) return;
                body.transform.rotation = Quaternion.LookRotation(dir.normalized,
                                                                  Vector3.up);
            }
            catch { }
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
        /// Find anatomical arm joints once per body. This rig has four twist
        /// helpers between elbow and hand; the hand's parents are not its elbow
        /// and shoulder. PlayerObjectsManager names the real joints.
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
            Transform lu, lf, ru, rf;
            bool leftArm = ArmJoints(body, lh, true, out lu, out lf);
            bool rightArm = ArmJoints(body, rh, false, out ru, out rf);
            if (!leftArm || !rightArm) why = "anatomical arm joints missing";

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

            st.LHand = lh; st.LFore = lf; st.LUpper = lu;
            st.RHand = rh; st.RFore = rf; st.RUpper = ru;
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
        static bool ArmJoints(GameObject body, Transform hand, bool left,
                              out Transform upper, out Transform fore)
        {
            upper = null; fore = null;
            Type type = RevivalPlugin.TypeByName("PlayerObjectsManager");
            Component pom = type == null ? null : body.GetComponentInChildren(type, true);
            if (pom == null || hand == null) return false;
            upper = AsTransform(Field(pom, left ? "MainChar_Upper_armL" : "MainChar_Upper_armR"));
            fore = AsTransform(Field(pom, left ? "MainChar_ForearmL1" : "MainChar_ForearmR1"));
            return upper != null && fore != null && Under(upper, fore) && Under(fore, hand)
                && Vector3.Distance(upper.position, fore.position) > 0.0001f
                && Vector3.Distance(fore.position, hand.position) > 0.0001f;
        }

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

        // _inVehiclePose is gameplay state, not an animation selector. In
        // SetPlayerVehicleState every value except driver (1) selects sitting.
        // Sample the native standing clip after animation and before arm IK.
        static void StandingPose(Station st, GameObject body)
        {
            if (CfgStandPose == null || !CfgStandPose.Value || body == null) return;
            try
            {
                if (st.PoseBody != body)
                {
                    st.PoseBody = body;
                    st.PoseTried = false;
                    st.StandClip = null;
                    st.SampleStand = null;
                }
                if (!st.PoseTried)
                {
                    st.PoseTried = true;
                    Type statesType = RevivalPlugin.TypeByName("PlayerStatesController");
                    Component states = statesType == null ? null
                        : body.GetComponentInChildren(statesType, true);
                    Component animation = Field(states, "_anim") as Component;
                    if (animation == null) throw new InvalidOperationException("Player animation missing");
                    PropertyInfo item = animation.GetType().GetProperty("Item", new Type[] { typeof(string) });
                    object idle = item == null ? null : item.GetValue(animation, new object[] { "idle_alert" });
                    if (idle == null) throw new InvalidOperationException("idle_alert clip missing");
                    st.StandClip = idle.GetType().GetProperty("clip").GetValue(idle, null);
                    st.SampleStand = st.StandClip.GetType().GetMethod("SampleAnimation",
                        new Type[] { typeof(GameObject), typeof(float) });
                    st.AnimationObject = animation.gameObject;
                    if (st.SampleStand == null) throw new InvalidOperationException("SampleAnimation missing");
                    RevivalPlugin.L.LogInfo("Technical-MG: standing idle_alert pose for " + body.name);
                }
                if (st.SampleStand != null)
                    st.SampleStand.Invoke(st.StandClip, new object[] { st.AnimationObject, 0f });
            }
            catch (Exception ex)
            {
                st.SampleStand = null;
                RevivalPlugin.L.LogWarning("Technical-MG: standing pose unavailable: " + ex.Message);
            }
        }

        /// <summary>Is the local player in this place of our technical?</summary>
        static bool InSeat(int index)
        {
            return _vgs != null
                   && IntField(_vgs, "_localPlayerPassengerId") == index;
        }

        static void ReleasePose()
        {
            // Native animation resumes when the body leaves the gunner slot.
            // Release cached references too, without rewriting vehicle state.
            Station st = Find(_vgs);
            if (st == null) return;
            st.PoseBody = null;
            st.AnimationObject = null;
            st.StandClip = null;
            st.SampleStand = null;
            st.PoseTried = false;
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

            // Keep the MG cadence, but give every report the game's own
            // TAC-50/L96 sniper character instead of the synthetic BTR crack.
            VehicleShotSound.PlayTechnical(muzzle);
            Turret.Net.PublishTechnicalShot(muzzle);

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
        /// mid-air beside him reads as a bug.
        ///
        /// UNTIL the field report of 2026-09-20 (screenshots
        /// anothertechnicalbug/anothertechnicalbug2) this drew the halo and the
        /// core as ONE static line spanning the whole muzzle-to-impact distance,
        /// visible for a fixed lifetime - correct for an instant
        /// Physics.RaycastAll, but "diese langweiligen raytraces" (these boring
        /// raytraces) is exactly what a line that snaps into existence looks
        /// like. Both now travel instead: <see cref="TechnicalTracerStreak"/>
        /// is a short glowing segment whose head advances from the muzzle at
        /// TracerSpeed and whose tail follows TracerLength behind it, the way a
        /// rifle's own tracer round is drawn - the report asked for the
        /// Dragunov's look. The rate of fire was already high (FireDelay, nine
        /// rounds a second); several streaks in flight together at once is what
        /// "mit hohem frequenz" asked for on top of that, not a faster streak by
        /// itself. Thinner than the BTR's - nine rounds a second must not fill
        /// the screen.
        /// </summary>
        static void Tracer(Vector3 von, Vector3 bis)
        {
            try
            {
                float tempo = CfgTracerSpeed == null ? 700f : CfgTracerSpeed.Value;
                float laenge = CfgTracerLength == null ? 6f : CfgTracerLength.Value;
                TechnicalTracerStreak.Spawn(von, bis, tempo, laenge * 1.6f,
                                            0.24f, 0.10f, SpurHof, SpurHof);
                TechnicalTracerStreak.Spawn(von, bis, tempo, laenge,
                                            0.11f, 0.04f, SpurKern, SpurEnde);

                Vector3 achse = bis - von;
                float distanz = achse.magnitude;
                if (distanz > 0.01f)
                {
                    float feuer = Mathf.Min(2.5f, distanz);
                    List<Vector3> muendung = new List<Vector3>();
                    muendung.Add(von);
                    muendung.Add(von + achse / distanz * feuer);
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
    /// A short glowing segment that visibly FLIES from the muzzle to the
    /// impact point instead of the whole line materialising in one frame -
    /// the fix for "diese langweiligen raytraces" (field report of
    /// 2026-09-20, screenshots anothertechnicalbug/anothertechnicalbug2). The
    /// head advances at TracerGeschwindigkeit; the tail follows
    /// TracerLaenge behind it, so the visible part is a moving streak and not
    /// a static line - the way the game's own rifle tracers, the Dragunov's
    /// included, read as a round in flight rather than a drawn line.
    ///
    /// One instance per line per shot (TechnicalGun.Tracer spawns two, halo
    /// and core). At the MG's own high rate of fire several are in the air
    /// together, which is the "hohe Frequenz" half of the same report - the
    /// travel speed makes ONE shot look right, the existing FireDelay makes
    /// MANY of them look like a stream.
    ///
    /// A HARD TIMEOUT guards a misconfigured TracerSpeed: without it, a value
    /// of 0 or a very small one would leave the segment stuck at the muzzle
    /// forever - a leaked GameObject that never reaches its target and never
    /// self-destroys.
    /// </summary>
    internal sealed class TechnicalTracerStreak : MonoBehaviour
    {
        const float Timeout = 3f;
        const float Nachleuchten = 0.04f;

        Vector3 _von, _bis;
        float _tempo;
        float _laenge;
        float _t;
        bool _fertig;
        LineRenderer _line;

        /// <summary>Creates the streak and starts it flying. Silently does
        /// nothing if the shared tracer material cannot be found - the same
        /// fail-open RocketHook.SpawnTracer already uses, so a missing shader
        /// costs a visual and not a shot.</summary>
        internal static void Spawn(Vector3 von, Vector3 bis, float tempo,
                                   float laenge, float startWidth, float endWidth,
                                   Color vorn, Color hinten)
        {
            try
            {
                Material mat = RocketHook.TracerMaterial();
                if (mat == null) return;

                GameObject go = new GameObject("NDR MG Leuchtspur");
                LineRenderer line = go.AddComponent<LineRenderer>();
                line.sharedMaterial = mat;
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.startWidth = startWidth;
                line.endWidth = endWidth;
                line.startColor = vorn;
                line.endColor = hinten;
                line.SetPosition(0, von);
                line.SetPosition(1, von);

                TechnicalTracerStreak s = go.AddComponent<TechnicalTracerStreak>();
                s._von = von;
                s._bis = bis;
                s._tempo = Mathf.Max(50f, tempo);
                s._laenge = Mathf.Max(0.05f, laenge);
                s._line = line;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Technical-MG: fliegende Leuchtspur - " + ex.Message);
            }
        }

        void Update()
        {
            if (_fertig) return;
            _t += Time.deltaTime;
            if (_t > Timeout) { Destroy(gameObject); return; }
            if (_line == null) { Destroy(gameObject); return; }

            Vector3 achse = _bis - _von;
            float distanz = achse.magnitude;
            if (distanz < 0.001f) { Destroy(gameObject); return; }

            Vector3 richtung = achse / distanz;
            float kopf = Mathf.Min(distanz, _t * _tempo);
            Vector3 spitze = _von + richtung * kopf;
            Vector3 ende = _von + richtung * Mathf.Max(0f, kopf - _laenge);
            _line.SetPosition(0, ende);
            _line.SetPosition(1, spitze);

            if (kopf >= distanz)
            {
                _fertig = true;
                Destroy(gameObject, Nachleuchten);
            }
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
        /// small offset sets grip height. The sampled idle_alert skeleton needs
        /// 1.40 m to reach the grips across the full -12..55 degree pitch range;
        /// at 1.10 m the lowered rear grips exceed the standing arm length.
        /// </summary>
        internal const float PivotHeight = 1.40f;

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
        // The sampled idle_alert rig reaches 1.42 world units with these joints.
        // At the 1.40 m pintle, 0.25 m forward keeps both elbows bent at every
        // supported elevation (research/technical_standing_check.py).
        const float ArmForward = 0.25f;

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
