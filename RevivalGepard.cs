// Next Day: Survival - Revival Toolkit
//
// THE GEPARD - a self-propelled anti-aircraft gun a player can drive, man and
// fight with, doing the job the real one was built for: finding low flyers
// with its search radar, locking one with its tracking radar and putting the
// twin 35 mm cannons on it with the lead the fire control computes.
//
// WHAT THIS IS, and what it is built from:
//
//   1. THE DONOR. The game's BTR-80A ("btr-80a_spawn", the T-72's donor too):
//      Rigidbody, RCCCarControllerV2, PhotonView, VehicleNetworkController,
//      VehicleGameSystem, eight wheel colliders, damage volumes and storage.
//      Driving, collision, damage and networking all belong to that prefab and
//      are never touched. It is the armoured chassis of the game, and a Gepard
//      is an armoured vehicle of the same length.
//   2. THE BODY. Codex's original Blender model (gepard_build.py ->
//      assets/gepard/gepard.glb), cut into its moving parts by
//      gepard_import.py: hull, tracks, turret, two guns, two radars. The
//      donor's renderers are switched off and the model is fitted to the
//      donor's OWN measured length and stood on its wheels, exactly the way
//      the drivable howitzer is fitted. Every pivot comes from gepard_rig.txt,
//      written by the importer - no model number is typed into this file.
//   3. THE CREW. Three places: driver, gunner, commander (the real crew). All
//      three sit under the turret ring, the one space tall enough to hide a
//      seated body; the height is derived from the measured turret roof and
//      hull belly. Seat 1 is the gunner's.
//   4. THE GUN STATION (GepardGun). The gunner presses the turret key (G, the
//      same key as the BTR's) and looks through the sight on the turret roof.
//      Manual: the mouse lays the guns at the turret's real traverse and
//      elevation rates, -5 to +85 degrees, with a rangefinder and
//      superelevation. RADAR (R): the search radar reports every flyer and
//      vehicle it can see on the PPI scope, the right mouse button steps the
//      lock from one to the next, and from then on the tracking radar follows
//      that target and the fire control lays both guns on the INTERCEPT point
//      - the sight stays on the target, the guns lead it. The gunner only
//      decides when to pull the trigger.
//   5. THE ROUNDS (GepardShots). 1100 rounds a minute from two alternating
//      barrels, real projectiles with muzzle velocity, gravity and dispersion,
//      each with its tracer; proximity burst against aircraft and drones,
//      impact burst on everything else, self-destruct at the end of their
//      flight. Barrels recoil, muzzles flash, cases fly.
//   6. THE DAMAGE, balanced on purpose. A drone dies to one hit. A helicopter
//      takes HeliHits hits. A vehicle loses its hit points through the game's
//      own armour path (partType 10), which accepts one hit every 0.3 s, so a
//      tank needs TankSeconds of CONTINUOUS hits - the 35 mm is not a tank
//      gun and one burst does not kill one. Infantry takes the HE hit.
//   7. THE NAME. The instance becomes "Gepard_<donor name>_GEPARD". It does not
//      START with "BTR-80A", so the BTR's own gunner (Turret.IsBtr) never
//      attaches itself; it still CONTAINS "btr-80a", so VehicleArmor.IstApc
//      counts it as the armoured vehicle it is against the BTR autocannon.
//      Explosions are re-balanced for it here (GepardArmor) - an APC dies to
//      one of anything, a Gepard to FpvHits / LawHits / ShellHits.
//
// WHAT IS LOCAL AND WHAT IS NOT. The vehicle is a networked scene object.
// The turret pose, the trigger and the radar state travel on one Photon event
// (GepardNet, 10 per second) so every client sees the turret swing and the
// guns fire; every client draws the rounds for itself. The damage is the
// shooter's, through the same entry points the Stinger and the BTR turret use:
// ApplyDamage on vehicles, the drones' own hit messages, and for a helicopter
// the kill message every peer applies with PlayerHeli.MissileImpact. Both
// radars turn on every client all the time.
//
// WHAT IS NOT PROVEN WITHOUT THE GAME. How the model sits on the donor, where
// the crew ends up, the sight picture and the feel of the lead are in-game
// acceptance items. The track links do not move (the tracks are one static
// mesh with their wheels), and the BTR's colliders stay the collision hull,
// so the top of the turret and the radars can be shot through.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. UTF-8 without BOM; outside ASCII only the Cyrillic of the player
// lines in GepardText.
//
// SEAMS OUTSIDE THIS FILE (all marked there):
//   RevivalPlugin.cs          BindConfig / Install / Update / OnGUI.
//   RevivalUralTruck.cs       VehicleRegistry entry "gepard".
//   Revival.CameraTurret.cs   CameraOwner.Gepard and its LateTick dispatch.
//   Revival.Admin.cs          one button.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The vehicle: config, the donor rebuild, the crew places, the admin
    /// spawn and the structural self-check. The gun is GepardGun.
    /// </summary>
    public static class Gepard
    {
        /// <summary>The armoured donor. Lower case, as the resource path
        /// stores it (research/resource_paths.tsv: vehiclespawn/btr-80a_spawn).</summary>
        public const string Prefab = "btr-80a_spawn";

        /// <summary>Marker at the end of a rebuilt instance's name.</summary>
        public const string Marke = "_GEPARD";

        /// <summary>The model root under the donor.</summary>
        public const string BodyName = "NDR_GepardBody";

        public const int SeatTotal = 3;
        public const int DriverSeat = 0;
        public const int GunnerSeat = 1;

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<string> CfgKey;
        public static ConfigEntry<float> CfgDistance;
        public static ConfigEntry<float> CfgFit;
        public static ConfigEntry<float> CfgLift;
        internal static ConfigEntry<string> CfgRadarKey, CfgNextKey;
        internal static ConfigEntry<bool> CfgGroundTargets, CfgRequireAmmo;
        internal static ConfigEntry<float> CfgRadarRange, CfgRadarRpm, CfgTurnSpeed,
            CfgElevSpeed, CfgPitchMin, CfgPitchMax, CfgRpm, CfgVelocity, CfgDispersion,
            CfgMaxRange, CfgFuze, CfgInfantryDamage, CfgDroneDamage, CfgTankSeconds,
            CfgApcSeconds, CfgSoftSeconds, CfgSensitivity, CfgReloadSeconds, CfgAimLead;
        internal static ConfigEntry<int> CfgHeliHits, CfgRounds, CfgAmmoId,
            CfgRoundsPerBelt, CfgFpvHits, CfgLawHits, CfgShellHits, CfgEventCode;

        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;

        public static void BindConfig(ConfigFile cfg)
        {
            const string S = "Gepard";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "The Gepard anti-aircraft gun: register it as a vehicle kind, put "
                + "the Blender model on a BTR-80A chassis and give its gunner the "
                + "radar fire control.");
            CfgKey = cfg.Bind(S, "Key", "None",
                "Key that puts a Gepard down in front of the player (admin tool, "
                + "master client only). None by default, because F4..F12 are all "
                + "taken; the admin menu has a button for it.");
            CfgDistance = cfg.Bind(S, "Distance", 12f,
                "Distance in front of the camera the spawned Gepard is put down.");
            CfgFit = cfg.Bind(S, "Fit", 1.0f,
                "Extra factor on the model's size. The base size is derived: the "
                + "model's hull and tracks are scaled to the donor's measured "
                + "length. 1.0 = exactly as long as the BTR under it.");
            CfgLift = cfg.Bind(S, "Lift", 0.0f,
                "Vertical nudge of the model as a fraction of the donor's height. "
                + "0 stands the tracks on the donor's wheels.");
            CfgRadarKey = cfg.Bind(S, "RadarKey", "R",
                "At the gun: switch the radar fire control on and off.");
            CfgNextKey = cfg.Bind(S, "NextTargetKey", "Mouse1",
                "At the gun, radar on: lock the next target (Mouse1 = right button).");
            CfgGroundTargets = cfg.Bind(S, "GroundTargets", true,
                "The radar also reports and tracks vehicles, not only aircraft "
                + "and drones.");
            CfgRadarRange = cfg.Bind(S, "RadarRange", 1500f,
                "Search radar range in world units.");
            CfgRadarRpm = cfg.Bind(S, "RadarRpm", 60f,
                "Search radar revolutions per minute (the real one: about 60).");
            CfgTurnSpeed = cfg.Bind(S, "TurnSpeed", 90f,
                "Turret traverse, degrees per second.");
            CfgElevSpeed = cfg.Bind(S, "ElevationSpeed", 60f,
                "Gun elevation rate, degrees per second.");
            CfgPitchMin = cfg.Bind(S, "PitchMin", -5f, "Lowest gun elevation, degrees.");
            CfgPitchMax = cfg.Bind(S, "PitchMax", 85f,
                "Highest gun elevation, degrees. The real guns go to +85.");
            CfgAimLead = cfg.Bind(S, "AimLead", 8f,
                "Manual laying: how far the sight may run ahead of the guns, degrees.");
            CfgSensitivity = cfg.Bind(S, "Sensitivity", 2.0f,
                "Manual laying: degrees per unit of mouse movement.");
            CfgRpm = cfg.Bind(S, "RateOfFire", 1100f,
                "Rounds per minute from both barrels together (2 x 550, the real "
                + "cyclic rate). The barrels alternate.");
            CfgVelocity = cfg.Bind(S, "MuzzleVelocity", 1175f,
                "Muzzle velocity in world units per second (the real 1175 m/s).");
            CfgDispersion = cfg.Bind(S, "Dispersion", 1.5f,
                "Dispersion of a single round in milliradians.");
            CfgMaxRange = cfg.Bind(S, "MaxRange", 2600f,
                "A round self-destructs after this distance, world units.");
            CfgFuze = cfg.Bind(S, "ProximityRadius", 1.5f,
                "A round passing this close to an aircraft or drone (beyond its "
                + "own size) bursts and hits it, world units.");
            CfgInfantryDamage = cfg.Bind(S, "InfantryDamage", 180f,
                "Damage of a direct HE hit on a person or animal.");
            CfgDroneDamage = cfg.Bind(S, "DroneDamage", 6f,
                "Damage of one hit on a drone. Every drone in the game has fewer "
                + "hit points, so one 35 mm hit brings it down.");
            CfgHeliHits = cfg.Bind(S, "HeliHits", 10,
                "Hits a helicopter survives before it goes down. A Stinger takes "
                + "one missile; ten 35 mm hits are about half a second on target.");
            CfgTankSeconds = cfg.Bind(S, "TankSeconds", 12f,
                "Seconds of CONTINUOUS hits that destroy a tank. The game accepts "
                + "one hit on a vehicle every 0.3 s, so this is independent of "
                + "the rate of fire - a burst does not one-shot a tank.");
            CfgApcSeconds = cfg.Bind(S, "ApcSeconds", 4f,
                "Seconds of continuous hits that destroy an APC (BTR, Gepard).");
            CfgSoftSeconds = cfg.Bind(S, "SoftSeconds", 1.5f,
                "Seconds of continuous hits that destroy any other vehicle.");
            CfgRounds = cfg.Bind(S, "Rounds", 640,
                "Rounds on board (2 x 320). A new vehicle starts full.");
            CfgRequireAmmo = cfg.Bind(S, "RequireAmmo", true,
                "Reloading an empty gun takes one ammunition belt (AmmoItemId) "
                + "from the gunner's inventory. Off: reloading is free.");
            CfgAmmoId = cfg.Bind(S, "AmmoItemId", 2050,
                "Item that reloads the gun - the BTR turret's belt box by default.");
            CfgRoundsPerBelt = cfg.Bind(S, "RoundsPerBelt", 200,
                "Rounds one belt item puts back into the gun.");
            CfgReloadSeconds = cfg.Bind(S, "ReloadSeconds", 6f, "Reload time in seconds.");
            CfgFpvHits = cfg.Bind(S, "FpvHits", 2,
                "FPV drone strikes the Gepard survives before it is destroyed "
                + "(an APC takes one).");
            CfgLawHits = cfg.Bind(S, "LawHits", 2, "M72 LAW hits that destroy it.");
            CfgShellHits = cfg.Bind(S, "ShellHits", 1, "Tank shells that destroy it.");
            CfgEventCode = cfg.Bind(S, "NetworkEventCode", 193,
                "Photon event code for turret pose, trigger and helicopter kills. "
                + "Must not overlap any other channel (0..199).");
        }

        public static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        public static bool IstGepard(Transform root)
        {
            if (root == null) return false;
            return root.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------ rebuild

        /// <summary>
        /// The whole rebuild. Idempotent: the network postfix and the registry's
        /// own rebuild both reach a locally spawned instance. Never throws out -
        /// a failure leaves a plain BTR.
        /// </summary>
        public static void Umbauen(GameObject car)
        {
            if (car == null || IstGepard(car.transform)) return;
            // The name first: InitCar runs after the DoInstantiate postfix, and
            // Turret.InitCarPrefix would hang the BTR gunner's seat onto
            // anything whose name starts with "BTR-80A".
            car.name = "Gepard_" + car.name + Marke;
            try { Aufbauen(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Gepard, rebuild: " + ex); }
            try { Validate(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Gepard, self-check: " + ex); }
        }

        static void Aufbauen(GameObject car)
        {
            Component vgs;
            Transform seats = FindSeatPoints(car, out vgs);
            if (seats == null)
                RevivalPlugin.L.LogWarning("Gepard: SeatPoints missing - the donor "
                    + "keeps its own seat table.");
            else Sitze(vgs, seats);

            Vector3 min, max;
            if (!Masse(car, out min, out max))
            {
                RevivalPlugin.L.LogWarning("Gepard: nothing measurable on the donor - "
                    + "this stays a plain BTR.");
                return;
            }
            Karosse(car, seats, min, max);
        }

        /// <summary>Three seats; everything behind them removed, the BTR
        /// gunner's extra seat included should it already exist.</summary>
        static void Sitze(Component vgs, Transform seats)
        {
            int before = seats.childCount;
            List<Transform> places = new List<Transform>();
            for (int i = 0; i < seats.childCount; i++)
            {
                Transform c = seats.GetChild(i);
                if (c == null) continue;
                if (c.name == Turret.SeatName)
                {
                    c.SetParent(null, false);
                    UnityEngine.Object.Destroy(c.gameObject);
                    i--;
                    continue;
                }
                places.Add(c);
            }
            for (int i = places.Count - 1; i >= SeatTotal; i--)
            {
                places[i].SetParent(null, false);
                UnityEngine.Object.Destroy(places[i].gameObject);
            }
            if (vgs != null)
            {
                FieldInfo fPass = AccessTools.Field(vgs.GetType(), "Passengers");
                if (fPass != null) fPass.SetValue(vgs, new GameObject[seats.childCount]);
            }
            RevivalPlugin.L.LogInfo("Gepard: seats " + before + " -> " + seats.childCount
                + " (driver, gunner, commander), Passengers re-sized.");
        }

        /// <summary>Where the three sit, in model metres: x and z around the
        /// turret ring, y derived in Karosse from roof and belly.</summary>
        static readonly Vector3[] CrewAt = new Vector3[] {
            new Vector3(0.00f, 0f, 0.85f),     // driver
            new Vector3(0.62f, 0f, -0.15f),    // gunner
            new Vector3(-0.62f, 0f, -0.15f),   // commander
        };

        /// <summary>A seated body reaches from +1.85 to +5.95 above its seat
        /// root, in the seat table's own units - measured on vanilla cars
        /// (research/tank_crew_check.py, the numbers the T-72 stands on).</summary>
        const float BodyFeet = 1.85f;
        const float BodyHead = 5.95f;

        /// <summary>
        /// Put the Gepard in the donor's place, fitted to the donor's own box:
        /// scaled so hull and tracks are as long as the BTR with its wheels,
        /// centred, and stood on the wheels. Collision is untouched - only
        /// renderers are switched, and the LODGroups go off so the donor body
        /// does not come back at a distance (the howitzer's trade).
        /// </summary>
        static void Karosse(GameObject car, Transform seats, Vector3 min, Vector3 max)
        {
            Transform root = car.transform;
            Transform parent = seats != null && seats.parent != null ? seats.parent : root;
            if (Body(root) != null) return;

            GepardRig rig = GepardModel.Build();
            if (rig == null)
            {
                RevivalPlugin.L.LogWarning("Gepard: the model did not load (see "
                    + "GepardModel) - this stays a plain BTR. Repair the client package.");
                return;
            }
            GameObject modell = rig.gameObject;

            int groups = LodAus(car);
            int hidden = Verstecken(car);

            Bounds b = GepardModel.ChassisBounds;
            float modelLength = Mathf.Max(0.0001f, b.size.z);
            float donorLength = Mathf.Max(0.0001f, max.z - min.z);
            float donorHeight = Mathf.Max(0.0001f, max.y - min.y);
            float scale = donorLength / modelLength
                * Mathf.Clamp(CfgFit == null ? 1f : CfgFit.Value, 0.2f, 5f);

            modell.transform.SetParent(parent, false);
            modell.transform.rotation = root.rotation;
            modell.transform.localScale = new Vector3(scale, scale, scale);
            float lift = (CfgLift == null ? 0f : CfgLift.Value) * donorHeight;
            Vector3 wanted = new Vector3(
                (min.x + max.x) * 0.5f - b.center.x * scale,
                min.y - b.min.y * scale + lift,
                (min.z + max.z) * 0.5f - b.center.z * scale);
            // min/max are in the ROOT's frame; the body hangs under the chassis.
            modell.transform.position = root.TransformPoint(wanted);
            rig.Vehicle = root;

            string crew = seats == null ? "no seat table" : Crew(rig, seats);

            RevivalPlugin.L.LogInfo("Gepard: donor box x " + min.x.ToString("0.00") + ".."
                + max.x.ToString("0.00") + ", y " + min.y.ToString("0.00") + ".."
                + max.y.ToString("0.00") + ", z " + min.z.ToString("0.00") + ".."
                + max.z.ToString("0.00") + "; chassis mesh " + b.size + " m, factor "
                + scale.ToString("0.000") + "; " + hidden + " donor renderers hidden, "
                + groups + " LODGroups off; " + crew + ".");
        }

        /// <summary>The three crew places under the turret ring. The seat root
        /// is put where the whole seated body fits between the hull belly and
        /// the turret roof - both measured by gepard_import.py.</summary>
        static string Crew(GepardRig rig, Transform seats)
        {
            Transform body = rig.transform;
            // Seat-table units per model metre, measured through the transforms.
            float upm = seats.InverseTransformVector(body.TransformVector(Vector3.up)).magnitude;
            if (upm < 0.0001f) upm = 1f;
            float roof = GepardModel.TurretPivot.y + GepardModel.Roof - 0.08f;
            float belly = GepardModel.Belly + 0.05f;
            float highest = roof - BodyHead / upm;
            float lowest = belly - BodyFeet / upm;
            float y = lowest <= highest ? (lowest + highest) * 0.5f : highest;
            int n = Mathf.Min(seats.childCount, CrewAt.Length);
            for (int i = 0; i < n; i++)
            {
                Transform s = seats.GetChild(i);
                Vector3 at = CrewAt[i];
                at.y = y;
                s.position = body.TransformPoint(at);
                s.rotation = body.rotation;
            }
            return n + " crew places at y " + y.ToString("0.00") + " m (fits "
                + lowest.ToString("0.00") + ".." + highest.ToString("0.00") + ", "
                + upm.ToString("0.00") + " seat units per metre)";
        }

        static int LodAus(GameObject car)
        {
            int groups = 0;
            LODGroup[] all = car.GetComponentsInChildren<LODGroup>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                all[i].enabled = false;
                groups++;
            }
            return groups;
        }

        static int Verstecken(GameObject car)
        {
            int hidden = 0;
            Renderer[] all = car.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || !r.enabled || IstUnser(r.transform)) continue;
                if (r is ParticleSystemRenderer) continue;   // exhaust, dust
                r.enabled = false;
                hidden++;
            }
            return hidden;
        }

        static bool IstUnser(Transform t)
        {
            while (t != null)
            {
                if (t.name == BodyName) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>The donor's bounds in the root's LOCAL space, wheels
        /// included: the underside is the ground line the tracks stand on.</summary>
        static bool Masse(GameObject car, out Vector3 min, out Vector3 max)
        {
            min = Vector3.zero;
            max = Vector3.zero;
            bool any = false;
            Transform root = car.transform;
            MeshFilter[] all = car.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < all.Length; i++)
            {
                MeshFilter mf = all[i];
                if (mf == null || mf.sharedMesh == null || IstUnser(mf.transform)) continue;
                Bounds b = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = new Vector3(
                        (c & 1) == 0 ? b.min.x : b.max.x,
                        (c & 2) == 0 ? b.min.y : b.max.y,
                        (c & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 p = root.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!any) { min = p; max = p; any = true; continue; }
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            return any;
        }

        internal static Transform Body(Transform root)
        {
            if (root == null) return null;
            GepardRig rig = root.GetComponentInChildren<GepardRig>(true);
            return rig == null ? null : rig.transform;
        }

        internal static GepardRig Rig(Transform root)
        {
            return root == null ? null : root.GetComponentInChildren<GepardRig>(true);
        }

        // -------------------------------------------------------------- check

        public static bool Validate(GameObject car)
        {
            if (car == null) return false;
            List<string> fail = new List<string>();
            Component vgs;
            Transform seats = FindSeatPoints(car, out vgs);
            int seatN = seats == null ? 0 : seats.childCount;
            if (seats == null) fail.Add("SeatPoints missing");
            else if (seatN != SeatTotal) fail.Add("seat count " + seatN + " instead of " + SeatTotal);
            if (vgs == null) fail.Add("VehicleGameSystem missing");
            else
            {
                FieldInfo fPass = AccessTools.Field(vgs.GetType(), "Passengers");
                Array pass = fPass == null ? null : fPass.GetValue(vgs) as Array;
                if (pass == null) fail.Add("Passengers array missing");
                else if (pass.Length != seatN)
                    fail.Add("Passengers length " + pass.Length + " != seats " + seatN);
            }
            GepardRig rig = Rig(car.transform);
            if (rig == null) fail.Add(BodyName + " missing");
            else if (rig.Turret == null || rig.GunR == null || rig.GunL == null
                     || rig.RadarSearch == null || rig.RadarTrack == null || rig.Sight == null)
                fail.Add("a moving part is missing");
            if (car.name.StartsWith("BTR-80A", StringComparison.OrdinalIgnoreCase))
                fail.Add("name still starts with BTR-80A (the BTR gunner would attach)");
            if (!HasComponent(car, "PhotonView")) fail.Add("PhotonView missing");
            if (!HasComponent(car, "RCCCarControllerV2")) fail.Add("RCCCarControllerV2 missing");
            if (!VehicleRegistry.Contains("gepard")) fail.Add("not in VehicleRegistry");
            if (fail.Count == 0)
            {
                RevivalPlugin.L.LogInfo("Gepard check: PASS - " + seatN + " seats, model "
                    + "with turret, two guns and two radars on the donor, network and "
                    + "physics present.");
                return true;
            }
            RevivalPlugin.L.LogWarning("Gepard check: FAIL - " + string.Join("; ", fail.ToArray()));
            return false;
        }

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

        static bool HasComponent(GameObject car, string typeName)
        {
            Type t = RevivalPlugin.TypeByName(typeName);
            return t != null && car.GetComponentInChildren(t, true) != null;
        }

        // ------------------------------------------------------------ runtime

        public static void Install(Harmony harmony)
        {
            if (!Enabled)
            {
                RevivalPlugin.L.LogInfo("Gepard: off (Gepard/Enabled).");
                return;
            }
            VehicleRegistry.EnsureBuilt();
            GepardNet.Install(harmony);
            GepardArmor.Install(harmony);
            RevivalPlugin.L.LogInfo("Gepard: registered (key " + (CfgKey == null ? "None" : CfgKey.Value)
                + ", donor " + Prefab + ", " + SeatTotal + " seats, radar "
                + CfgRadarKey.Value + ", next target " + CfgNextKey.Value + ").");
        }

        public static void Tick()
        {
            if (!Enabled) return;
            try
            {
                KeyCode key = Key();
                if (key != KeyCode.None && Input.GetKeyDown(key)) SpawnInFront();
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Gepard spawn key: " + ex); }
            GepardGun.Tick();
            GepardShots.Tick();
        }

        public static void Draw()
        {
            if (!Enabled) return;
            GepardGun.Draw();
        }

        internal static string SpawnInFront()
        {
            if (!Enabled) return "The Gepard is disabled in the configuration.";
            Camera cam = Camera.main;
            if (cam == null) return "No player camera available.";
            Vector3 ahead = cam.transform.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.000001f) ahead = Vector3.forward;
            ahead.Normalize();
            float dist = Mathf.Max(8f, CfgDistance == null ? 12f : CfgDistance.Value);
            Vector3 above = cam.transform.position + ahead * dist + Vector3.up * 30f;
            Vector3 ground;
            GameObject under = Turret.RaycastObject(above, Vector3.down, 200f, out ground);
            if (under == null) return "No ground found ahead of the player.";
            Vector3 pos = ground + Vector3.up * 1.6f;
            Quaternion rot = Quaternion.LookRotation(ahead, Vector3.up);
            bool isTank;
            GameObject car = VehicleRegistry.Spawn("gepard", pos, rot, out isTank);
            if (car == null) return "Gepard spawn failed; see the game log.";
            RevivalPlugin.L.LogInfo("Gepard: created at " + pos + ", ground \"" + under.name + "\".");
            GepardGun.Hint(GepardText.Spawned(), 4f);
            return GepardText.Spawned();
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            _key = ParseKey(CfgKey == null ? "None" : CfgKey.Value, KeyCode.None);
            return _key;
        }

        internal static KeyCode ParseKey(string name, KeyCode fallback)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), name, true); }
            catch
            {
                RevivalPlugin.L.LogWarning("Gepard: key " + name + " unknown, using " + fallback + ".");
                return fallback;
            }
        }
    }

    // =====================================================================
    // Model

    /// <summary>
    /// Loads the seven meshes, the atlas, the metal map and the rig file once,
    /// and builds one model per vehicle out of them.
    /// </summary>
    internal static class GepardModel
    {
        static Mesh _hull, _tracks, _turret, _gunR, _gunL, _search, _track;
        static Material _material;
        static bool _loaded, _ok;
        static readonly Dictionary<string, Vector3> _pivots = new Dictionary<string, Vector3>();
        internal static Vector3 MuzzleR, MuzzleL;
        internal static float Roof = 1.5f, Belly = 0.38f;
        internal static Bounds ChassisBounds;
        internal static Vector3 TurretPivot { get { return Pivot("turret"); } }

        static Vector3 Pivot(string part)
        {
            Vector3 v;
            return _pivots.TryGetValue(part, out v) ? v : Vector3.zero;
        }

        internal static bool Load()
        {
            if (_loaded) return _ok;
            _loaded = true;
            try
            {
                ReadRig(Path.Combine(RevivalPlugin.AssetDir, "gepard_rig.txt"));
                _hull = Assets.Load("gepard_hull.ndmesh");
                _tracks = Assets.Load("gepard_tracks.ndmesh");
                _turret = Assets.Load("gepard_turret.ndmesh");
                _gunR = Assets.Load("gepard_gun_r.ndmesh");
                _gunL = Assets.Load("gepard_gun_l.ndmesh");
                _search = Assets.Load("gepard_radar_search.ndmesh");
                _track = Assets.Load("gepard_radar_track.ndmesh");
                Texture2D tex = Assets.Texture("gepard_diffuse.png", false, true);
                Texture2D metal = Assets.Texture("gepard_metal.png", true, true);
                if (_hull == null || _tracks == null || _turret == null || _gunR == null
                    || _gunL == null || _search == null || _track == null || tex == null)
                    throw new InvalidOperationException("Gepard assets missing; repair the client package");
                ChassisBounds = _hull.bounds;
                ChassisBounds.Encapsulate(_tracks.bounds);

                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                _material = new Material(shader);
                _material.name = "NDR_Gepard_Material";
                _material.mainTexture = tex;
                _material.color = Color.white;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.anisoLevel = 8;
                tex.filterMode = FilterMode.Trilinear;
                // Uploaded once, shared by every Gepard: compress and drop the
                // CPU copy, as the howitzer's 4K maps do.
                tex.Compress(true); tex.Apply(true, true);
                if (metal != null && _material.HasProperty("_MetallicGlossMap"))
                {
                    metal.Compress(true); metal.Apply(true, true);
                    _material.SetTexture("_MetallicGlossMap", metal);
                    if (_material.HasProperty("_GlossMapScale")) _material.SetFloat("_GlossMapScale", 1f);
                    if (_material.HasProperty("_SmoothnessTextureChannel"))
                        _material.SetFloat("_SmoothnessTextureChannel", 0f);
                    _material.EnableKeyword("_METALLICGLOSSMAP");
                }
                else
                {
                    if (_material.HasProperty("_Metallic")) _material.SetFloat("_Metallic", 0.2f);
                    if (_material.HasProperty("_Glossiness")) _material.SetFloat("_Glossiness", 0.3f);
                }
                if (_material.HasProperty("_SpecularHighlights")) _material.SetFloat("_SpecularHighlights", 1f);
                if (_material.HasProperty("_GlossyReflections")) _material.SetFloat("_GlossyReflections", 1f);
                if (_material.HasProperty("_EmissionColor")) _material.SetColor("_EmissionColor", Color.black);
                _material.DisableKeyword("_EMISSION");
                _ok = true;
                RevivalPlugin.L.LogInfo("GepardModel: loaded, chassis " + ChassisBounds.size
                    + " m, roof " + Roof.ToString("0.00") + ", belly " + Belly.ToString("0.00") + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("GepardModel: " + ex.Message);
                _ok = false;
            }
            return _ok;
        }

        static void ReadRig(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("gepard_rig.txt missing", path);
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p[0] == "part" && p.Length >= 6)
                    _pivots[p[1]] = new Vector3(F(p[3]), F(p[4]), F(p[5]));
                else if (p[0] == "muzzle" && p.Length >= 5)
                {
                    Vector3 m = new Vector3(F(p[2]), F(p[3]), F(p[4]));
                    if (p[1] == "gun_r") MuzzleR = m; else MuzzleL = m;
                }
                else if (p[0] == "roof" && p.Length >= 2) Roof = F(p[1]);
                else if (p[0] == "belly" && p.Length >= 2) Belly = F(p[1]);
            }
            string[] need = { "turret", "gun_r", "gun_l", "radar_search", "radar_track" };
            for (int i = 0; i < need.Length; i++)
                if (!_pivots.ContainsKey(need[i]))
                    throw new InvalidDataException("gepard_rig.txt lacks part " + need[i]);
            if (MuzzleR.z < 1f || MuzzleL.z < 1f)
                throw new InvalidDataException("gepard_rig.txt lacks the muzzles");
        }

        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }

        /// <summary>
        /// One model: Body (hull + tracks) / Turret (yaw) / GunR, GunL
        /// (elevation) / Slide (recoil, carries the gun mesh) / both radars /
        /// Sight (the gunner's eye). All in model metres; the caller scales
        /// the body root.
        /// </summary>
        internal static GepardRig Build()
        {
            if (!Load()) return null;
            GameObject root = new GameObject(Gepard.BodyName);
            try
            {
                Part(root, "Hull", _hull);
                Part(root, "Tracks", _tracks);
                GameObject turret = Node(root.transform, "Turret", Pivot("turret"));
                Part(turret, "TurretShell", _turret);
                GameObject gunR = Node(turret.transform, "GunR", Pivot("gun_r"));
                GameObject slideR = Node(gunR.transform, "SlideR", Vector3.zero);
                Part(slideR, "GunRMesh", _gunR);
                GameObject gunL = Node(turret.transform, "GunL", Pivot("gun_l"));
                GameObject slideL = Node(gunL.transform, "SlideL", Vector3.zero);
                Part(slideL, "GunLMesh", _gunL);
                GameObject search = Node(turret.transform, "SearchRadar", Pivot("radar_search"));
                Part(search, "SearchRadarMesh", _search);
                GameObject track = Node(turret.transform, "TrackingRadar", Pivot("radar_track"));
                Part(track, "TrackingRadarMesh", _track);
                // The sight: on the roof, in front of the search radar, between
                // the guns. Looking out from there the two barrels are at the
                // lower edges of the picture, as through the real periscope.
                GameObject sight = Node(turret.transform, "Sight",
                    new Vector3(0f, Roof + 0.30f, 0.85f));

                GepardRig rig = root.AddComponent<GepardRig>();
                rig.Turret = turret.transform;
                rig.GunR = gunR.transform;
                rig.GunL = gunL.transform;
                rig.SlideR = slideR.transform;
                rig.SlideL = slideL.transform;
                rig.RadarSearch = search.transform;
                rig.RadarTrack = track.transform;
                rig.Sight = sight.transform;
                rig.MuzzleR = MuzzleR;
                rig.MuzzleL = MuzzleL;
                rig.FlashR = Flash(gunR.transform, MuzzleR);
                rig.FlashL = Flash(gunL.transform, MuzzleL);
                rig.Apply(0f, 0f, 0f);
                return rig;
            }
            catch
            {
                UnityEngine.Object.Destroy(root);
                throw;
            }
        }

        static GameObject Node(Transform parent, string name, Vector3 at)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localRotation = Quaternion.identity;
            return go;
        }

        static void Part(GameObject parent, string name, Mesh mesh)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _material;
        }

        static Light Flash(Transform gun, Vector3 muzzle)
        {
            GameObject go = new GameObject("MuzzleLight");
            go.transform.SetParent(gun, false);
            go.transform.localPosition = muzzle + Vector3.forward * 0.3f;
            Light l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.78f, 0.45f);
            l.range = 14f;
            l.intensity = 0f;
            l.shadows = LightShadows.None;
            l.enabled = false;
            return l;
        }
    }

    /// <summary>
    /// The moving parts of one Gepard, on every client: the search radar that
    /// never stops turning, the tracking radar, the turret and guns as the
    /// gunner (local, or remote through GepardNet) lays them, barrel recoil
    /// and the muzzle light. A remote gunner's rounds are drawn from here.
    /// </summary>
    public sealed class GepardRig : MonoBehaviour
    {
        public Transform Turret, GunR, GunL, SlideR, SlideL, RadarSearch, RadarTrack, Sight;
        public Vector3 MuzzleR, MuzzleL;
        public Light FlashR, FlashL;
        public Transform Vehicle;
        public float Yaw, Pitch, TrackYaw;
        public bool LocalControl;
        public bool Alive = true;

        float _search;
        float _shotR = -10f, _shotL = -10f;
        float _rYaw, _rPitch, _rTrack, _rLast = -100f, _rNext;
        bool _rFiring;
        int _rGun;
        float _nextAlive;
        Component _vgs;

        /// <summary>Barrel stroke in model metres, and the cycle: 25 ms back,
        /// 80 ms home - inside the 109 ms between two rounds of one barrel.</summary>
        const float Stroke = 0.22f;

        void Start()
        {
            // Two Gepards side by side should not sweep in lockstep.
            _search = Mathf.Repeat(GetInstanceID() * 37.3f, 360f);
        }

        /// <summary>The search antenna's angle in the turret frame.</summary>
        internal float SearchAngle { get { return _search; } }

        void Update()
        {
            float dt = Time.deltaTime;
            CheckAlive();
            if (RadarSearch != null && Alive)
            {
                float rpm = Gepard.CfgRadarRpm == null ? 60f : Gepard.CfgRadarRpm.Value;
                _search = Mathf.Repeat(_search + dt * rpm * 6f, 360f);
                RadarSearch.localRotation = Quaternion.Euler(0f, _search, 0f);
            }
            if (!LocalControl) Remote(dt);
            if (SlideR != null) SlideR.localPosition = new Vector3(0f, 0f, -Recoil(Time.time - _shotR));
            if (SlideL != null) SlideL.localPosition = new Vector3(0f, 0f, -Recoil(Time.time - _shotL));
            Glow(FlashR, Time.time - _shotR);
            Glow(FlashL, Time.time - _shotL);
        }

        static float Recoil(float age)
        {
            if (age < 0f || age > 0.105f) return 0f;
            if (age < 0.025f) return Stroke * Mathf.Sin(age / 0.025f * Mathf.PI * 0.5f);
            float t = (age - 0.025f) / 0.08f;
            return Stroke * (1f - t * t * (3f - 2f * t));
        }

        static void Glow(Light l, float age)
        {
            if (l == null) return;
            bool on = age >= 0f && age < 0.05f;
            if (l.enabled != on) l.enabled = on;
            if (on) l.intensity = 5f * (1f - age / 0.05f);
        }

        void CheckAlive()
        {
            if (Time.time < _nextAlive) return;
            _nextAlive = Time.time + 1f;
            if (Vehicle == null) return;
            if (_vgs == null)
            {
                Type t = RevivalPlugin.TypeByName("VehicleGameSystem");
                if (t != null) _vgs = Vehicle.GetComponent(t);
            }
            if (_vgs == null) return;
            FieldInfo f = AccessTools.Field(_vgs.GetType(), "Durability");
            object v = f == null ? null : f.GetValue(_vgs);
            if (v is float) Alive = (float)v > 0f;
        }

        internal void Apply(float yaw, float pitch, float track)
        {
            Yaw = yaw;
            Pitch = pitch;
            TrackYaw = track;
            if (Turret != null) Turret.localRotation = Quaternion.Euler(0f, yaw, 0f);
            Quaternion p = Quaternion.Euler(-pitch, 0f, 0f);
            if (GunR != null) GunR.localRotation = p;
            if (GunL != null) GunL.localRotation = p;
            if (RadarTrack != null) RadarTrack.localRotation = Quaternion.Euler(0f, track, 0f);
        }

        internal Vector3 Muzzle(int gun)
        {
            return gun == 0 ? GunR.TransformPoint(MuzzleR) : GunL.TransformPoint(MuzzleL);
        }

        internal Vector3 Bore(int gun) { return (gun == 0 ? GunR : GunL).forward; }

        internal Vector3 GunCentre() { return (GunR.position + GunL.position) * 0.5f; }

        internal void Kick(int gun)
        {
            if (gun == 0) _shotR = Time.time; else _shotL = Time.time;
        }

        internal void RemoteState(float yaw, float pitch, float track, bool firing)
        {
            _rYaw = yaw;
            _rPitch = pitch;
            _rTrack = track;
            if (firing && !_rFiring) _rNext = Time.time;
            _rFiring = firing;
            _rLast = Time.time;
        }

        void Remote(float dt)
        {
            if (Time.time - _rLast > 2f) return;
            float k = 1f - Mathf.Exp(-dt * 12f);
            Apply(Mathf.LerpAngle(Yaw, _rYaw, k), Mathf.Lerp(Pitch, _rPitch, k),
                  Mathf.LerpAngle(TrackYaw, _rTrack, k));
            if (!_rFiring || !Alive || Time.time - _rLast > 0.4f) return;
            float interval = 60f / Mathf.Max(60f, Gepard.CfgRpm == null ? 1100f : Gepard.CfgRpm.Value);
            if (_rNext < Time.time - 0.25f) _rNext = Time.time;
            int n = 0;
            while (Time.time >= _rNext && n < 4)
            {
                GepardShots.Fire(this, _rGun, Bore(_rGun), false);
                _rGun = 1 - _rGun;
                _rNext += interval;
                n++;
            }
        }
    }

    // =====================================================================
    // The gunner

    /// <summary>
    /// The local gunner's station: manning, the sight camera, manual and radar
    /// laying, the contacts the radar holds, the trigger, the ammunition and
    /// the scope picture.
    /// </summary>
    internal static class GepardGun
    {
        internal sealed class Contact
        {
            public GameObject Go;
            public int Kind;             // 0 heli, 1 vehicle, 2 FPV, 3 recon (player), 4 crew drone, 5 battery drone
            public int Actor;            // drone owner for 2/3
            public int HeliView;
            public Component Vehicle;
            public Vector3 LocalCentre;
            public float Radius;
            public Vector3 Pos, Vel;
            public bool VelInit;
            public bool Visible;
            public float SeenAt, VisibleAt, PaintedAt, NextHit;
            public bool Air { get { return Kind != 1; } }
        }

        const string ReloadOwner = "gepard-reload";

        static Component _vgs;
        static Transform _root;
        static GepardRig _rig;
        static bool _manning, _radar;
        static float _nextRescan, _nextScan, _nextShot, _nextPublish, _seatWait;
        static float _cmdYaw, _cmdPitch, _superElev;
        static int _gun;
        static bool _firing, _sentFiring;
        static int _fov;
        static readonly float[] Fovs = new float[] { 55f, 26f, 11f };
        static Quaternion _camRot;
        static bool _camInit;
        static Contact _lock;
        static readonly List<Contact> _contacts = new List<Contact>();
        static readonly List<GameObject> _tmp = new List<GameObject>();
        static readonly Dictionary<int, int> _rounds = new Dictionary<int, int>();
        static readonly Dictionary<int, int> _heliHits = new Dictionary<int, int>();
        static readonly Dictionary<int, float> _vehicleNext = new Dictionary<int, float>();
        static bool _reloading;
        static float _reloadUntil, _noAmmoSaid;
        static int _reloadAdd;
        static Vector3 _aimPoint;
        static bool _aimValid, _outOfReach;
        static float _gunError = 180f;
        static float _lastSweep;
        static string _hint;
        static float _hintUntil;
        static KeyCode _manKey = KeyCode.None, _radarKey = KeyCode.None, _nextKey = KeyCode.None;
        static bool _keysParsed;
        static MethodInfo _applyDamage;
        static bool _applyLooked;

        internal static bool Manning { get { return _manning; } }

        internal static void Hint(string text, float seconds)
        {
            _hint = text;
            _hintUntil = Time.time + seconds;
        }

        static void Keys()
        {
            if (_keysParsed) return;
            _keysParsed = true;
            _manKey = Gepard.ParseKey(RevivalPlugin.CfgTurretKey == null ? "G"
                : RevivalPlugin.CfgTurretKey.Value, KeyCode.G);
            _radarKey = Gepard.ParseKey(Gepard.CfgRadarKey.Value, KeyCode.R);
            _nextKey = Gepard.ParseKey(Gepard.CfgNextKey.Value, KeyCode.Mouse1);
        }

        // ---------------------------------------------------------- the frame

        internal static void Tick()
        {
            try
            {
                GepardNet.EnsureHooked();
                Keys();
                if (Time.time >= _nextRescan)
                {
                    _nextRescan = Time.time + 0.4f;
                    Rescan();
                }
                if (_vgs == null || _rig == null) { SetManning(false); return; }
                if (Input.GetKeyDown(_manKey)) Toggle();
                if (!_manning) return;

                if (Time.time > _seatWait && IntField(_vgs, "_localPlayerPassengerId") != Gepard.GunnerSeat)
                {
                    SetManning(false);
                    return;
                }
                if (!_rig.Alive)
                {
                    Hint(GepardText.Destroyed(), 4f);
                    SetManning(false);
                    return;
                }

                if (Input.GetKeyDown(_radarKey)) ToggleRadar();
                if (_radar && Input.GetKeyDown(_nextKey)) NextTarget();
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (wheel > 0.01f && _fov < Fovs.Length - 1) _fov++;
                else if (wheel < -0.01f && _fov > 0) _fov--;

                if (Time.time >= _nextScan)
                {
                    _nextScan = Time.time + 0.25f;
                    Scan();
                }
                Follow(Time.deltaTime);
                if (_radar) { Paint(); KeepLock(); }
                Aim(Time.deltaTime);
                Trigger();
                TickReload();
                Publish(false);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gepard gun: " + ex);
                SetManning(false);
            }
        }

        static void Rescan()
        {
            if (_vgs != null)
            {
                MonoBehaviour cur = _vgs as MonoBehaviour;
                if (cur != null && IntField(_vgs, "_localPlayerPassengerId") >= 0) return;
                // Out of the vehicle: hand the camera back and the turret to
                // the network BEFORE the references go.
                SetManning(false);
                _vgs = null; _root = null; _rig = null;
            }
            Component[] all = VehicleScan.All();
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i];
                if (c == null || !Gepard.IstGepard(c.transform)) continue;
                if (IntField(c, "_localPlayerPassengerId") < 0) continue;
                _vgs = c;
                _root = c.transform;
                _rig = Gepard.Rig(_root);
                return;
            }
        }

        static void Toggle()
        {
            if (_manning)
            {
                SetManning(false);
                Hint(GepardText.Left(), 2.5f);
                return;
            }
            int here = IntField(_vgs, "_localPlayerPassengerId");
            if (here != Gepard.GunnerSeat)
            {
                if (!ChangeSeat(Gepard.GunnerSeat))
                {
                    Hint(GepardText.SeatBusy(), 3f);
                    return;
                }
                _seatWait = Time.time + 2.5f;
            }
            if (!SetManning(true)) return;
            Hint(GepardText.Controls(), 7f);
        }

        static bool SetManning(bool on)
        {
            if (!on)
            {
                if (!_manning) return true;
                _manning = false;
                _firing = false;
                Publish(true);
                if (_rig != null) _rig.LocalControl = false;
                NativeActionProgress.End(ReloadOwner);
                _reloading = false;
                CameraOwner.Release(CameraOwner.Gepard);
                return true;
            }
            if (_manning) return true;
            if (!CameraOwner.Request(CameraOwner.Gepard, true, "Gepard")) return false;
            _manning = true;
            _camInit = false;
            _rig.LocalControl = true;
            _cmdYaw = _rig.Yaw;
            _cmdPitch = _rig.Pitch;
            RevivalPlugin.L.LogInfo("Gepard: gun manned, " + Rounds() + " rounds on board.");
            return true;
        }

        static bool ChangeSeat(int index)
        {
            object pvm = Field(_vgs, "_playerVehicleManager");
            if (pvm == null) return false;
            MethodInfo m = AccessTools.Method(pvm.GetType(), "ChangeToPassengerPlace",
                                              new Type[] { typeof(int) }, null);
            Array passengers = Field(_vgs, "Passengers") as Array;
            if (m == null || passengers == null || index >= passengers.Length) return false;
            GameObject sitting = passengers.GetValue(index) as GameObject;
            if (sitting != null) return false;
            m.Invoke(pvm, new object[] { index });
            return true;
        }

        // ------------------------------------------------------------- radar

        static void ToggleRadar()
        {
            _radar = !_radar;
            if (_radar)
            {
                Scan();
                _lock = Best(null);
                Hint(_lock == null ? GepardText.RadarOnNothing() : GepardText.RadarOn(), 3f);
            }
            else
            {
                _lock = null;
                _cmdYaw = _rig.Yaw;
                _cmdPitch = _rig.Pitch;
                Hint(GepardText.RadarOff(), 2f);
            }
            Publish(true);
        }

        /// <summary>Every candidate the game has, in reach of the radar: the
        /// player helicopters, every vehicle but our own, and the five kinds
        /// of drone - the same lists the Stinger's seeker reads.</summary>
        static void Scan()
        {
            if (_rig == null) return;
            Vector3 radar = _rig.RadarSearch.position;
            float range = Mathf.Max(100f, Gepard.CfgRadarRange.Value);
            float reach = Mathf.Max(range, Gepard.CfgMaxRange.Value);
            for (int i = 0; i < _contacts.Count; i++) _contacts[i].Go = Alive(_contacts[i]) ? _contacts[i].Go : null;

            _tmp.Clear();
            PlayerHeli.MissileTargets(_tmp);
            for (int i = 0; i < _tmp.Count; i++) Offer(_tmp[i], 0, 0, radar, reach);
            if (Gepard.CfgGroundTargets.Value)
            {
                Component[] all = VehicleScan.All();
                for (int i = 0; i < all.Length; i++)
                {
                    Component c = all[i];
                    if (c == null || c.transform == _root) continue;
                    object d = Field(c, "Durability");
                    if (d is float && (float)d <= 0f) continue;
                    Offer(c.gameObject, 1, 0, radar, reach);
                }
            }
            Collection(typeof(Drone.Net), "_fremde", "Go", 2, radar, reach);
            Collection(typeof(SurvNet), "_ghosts", "Go", 3, radar, reach);
            Collection(typeof(CrewDrone), "_local", "Go", 4, radar, reach);
            Collection(typeof(CrewDrone), "_remote", "Go", 4, radar, reach);
            Collection(typeof(ArtyBattery), "_posts", "DroneModel", 5, radar, reach);
            Collection(typeof(ArtyBattery), "_ghosts", "DroneModel", 5, radar, reach);

            float now = Time.time;
            for (int i = _contacts.Count - 1; i >= 0; i--)
            {
                Contact c = _contacts[i];
                // Gone from every list the game keeps, or dead: dropped.
                if (c.Go == null || now - c.SeenAt > 0.6f)
                {
                    if (_lock == c) _lock = null;
                    _contacts.RemoveAt(i);
                    continue;
                }
                float dist = Vector3.Distance(radar, c.Pos);
                c.Visible = dist <= range && (c.Kind != 1 || (dist <= range * 0.8f && Clutter(c)))
                    && Sight(radar, c);
                if (c.Visible) c.VisibleAt = now;
            }
            _contacts.Sort(Order);
        }

        /// <summary>
        /// The moving-target filter a search radar has against ground clutter:
        /// a vehicle is reported when it moves, when somebody is in it, or
        /// when it is a tank. Every parked, empty car on the map would
        /// otherwise fill the scope. A locked vehicle stays reported when it
        /// stops - the tracking radar does not let go of a halted target.
        /// </summary>
        static bool Clutter(Contact c)
        {
            if (c == _lock || c.Vel.sqrMagnitude > 1f) return true;
            if (c.Vehicle == null) return false;
            if (Tank.IstPanzer(c.Vehicle.transform)) return true;
            Array pass = Field(c.Vehicle, "Passengers") as Array;
            if (pass == null) return false;
            for (int i = 0; i < pass.Length; i++)
                if (pass.GetValue(i) as GameObject != null) return true;
            return false;
        }

        static int Order(Contact a, Contact b)
        {
            if (a.Air != b.Air) return a.Air ? -1 : 1;
            Vector3 at = _rig == null ? Vector3.zero : _rig.transform.position;
            return (a.Pos - at).sqrMagnitude.CompareTo((b.Pos - at).sqrMagnitude);
        }

        static bool Alive(Contact c)
        {
            if (c.Go == null || !c.Go.activeInHierarchy) return false;
            if (c.Kind == 0) return PlayerHeli.MissileTarget(c.HeliView) != null;
            if (c.Kind == 1 && c.Vehicle != null)
            {
                object d = Field(c.Vehicle, "Durability");
                if (d is float && (float)d <= 0f) return false;
            }
            return true;
        }

        static void Offer(GameObject go, int kind, int actor, Vector3 radar, float reach)
        {
            if (go == null || !go.activeInHierarchy) return;
            Contact c = null;
            for (int i = 0; i < _contacts.Count; i++)
                if (_contacts[i].Go == go) { c = _contacts[i]; break; }
            if (c == null)
            {
                if ((go.transform.position - radar).sqrMagnitude > reach * reach) return;
                c = new Contact();
                c.Go = go;
                c.Kind = kind;
                c.Actor = actor;
                if (kind == 0) c.HeliView = PlayerHeli.MissileView(go);
                if (kind == 1) c.Vehicle = go.GetComponent(RevivalPlugin.TypeByName("VehicleGameSystem"));
                Measure(c);
                c.Pos = go.transform.TransformPoint(c.LocalCentre);
                c.PaintedAt = -100f;
                _contacts.Add(c);
            }
            if (kind == 1 && c.Vehicle != null && !Alive(c)) return;
            c.SeenAt = Time.time;
        }

        static void Measure(Contact c)
        {
            Renderer[] rs = c.Go.GetComponentsInChildren<Renderer>();
            bool found = false;
            Bounds b = new Bounds(c.Go.transform.position, Vector3.zero);
            for (int i = 0; i < rs.Length; i++)
            {
                if (!(rs[i] is MeshRenderer) && !(rs[i] is SkinnedMeshRenderer)) continue;
                if (!rs[i].enabled) continue;
                if (!found) { b = rs[i].bounds; found = true; }
                else b.Encapsulate(rs[i].bounds);
            }
            c.LocalCentre = c.Go.transform.InverseTransformPoint(b.center);
            c.Radius = Mathf.Clamp(b.extents.magnitude * 0.6f, 0.3f, 9f);
        }

        static void Collection(Type type, string name, string goField, int kind, Vector3 radar, float reach)
        {
            FieldInfo f = type == null ? null : AccessTools.Field(type, name);
            object collection = f == null ? null : f.GetValue(null);
            System.Collections.IDictionary dict = collection as System.Collections.IDictionary;
            if (dict != null)
            {
                foreach (System.Collections.DictionaryEntry pair in dict)
                    OfferItem(pair.Value, goField, kind, pair.Key is int ? (int)pair.Key : 0, radar, reach);
                return;
            }
            System.Collections.IEnumerable list = collection as System.Collections.IEnumerable;
            if (list == null) return;
            foreach (object item in list) OfferItem(item, goField, kind, 0, radar, reach);
        }

        static void OfferItem(object item, string goField, int kind, int actor, Vector3 radar, float reach)
        {
            if (item == null) return;
            // A battery post whose drone is down still names its model while
            // the wreck falls; it is not a target any more.
            object hits = Field(item, "DroneHits");
            if (hits is int && (int)hits <= 0) return;
            object up = Field(item, "DroneUp");
            if (up is bool && !(bool)up) return;
            Offer(Field(item, goField) as GameObject, kind, actor, radar, reach);
        }

        /// <summary>Line of sight from the antenna: terrain, buildings and
        /// anything solid between hide a target from the radar.</summary>
        static bool Sight(Vector3 from, Contact c)
        {
            Vector3 d = c.Pos - from;
            float dist = d.magnitude;
            if (dist < 1f) return true;
            RaycastHit hit;
            Vector3 dir = d / dist;
            Vector3 origin = from;
            float rest = dist;
            for (int i = 0; i < 4 && rest > 0.5f; i++)
            {
                if (!Physics.Raycast(origin, dir, out hit, rest - 0.5f,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    return true;
                Transform t = hit.transform;
                if (t == c.Go.transform || t.IsChildOf(c.Go.transform)) return true;
                if (_root != null && t.IsChildOf(_root))
                {
                    rest -= hit.distance + 0.2f;
                    origin = hit.point + dir * 0.2f;
                    continue;
                }
                return false;
            }
            return true;
        }

        /// <summary>Position and a smoothed velocity for every contact, every
        /// frame - the lead is only as good as the velocity.</summary>
        static void Follow(float dt)
        {
            if (dt <= 0f) return;
            float k = 1f - Mathf.Exp(-dt * 5f);
            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact c = _contacts[i];
                if (c.Go == null) continue;
                Vector3 p = c.Go.transform.TransformPoint(c.LocalCentre);
                Vector3 v = (p - c.Pos) / dt;
                if (v.sqrMagnitude > 250000f) v = c.Vel;   // a teleport, not a speed
                c.Vel = c.VelInit ? Vector3.Lerp(c.Vel, v, k) : v;
                c.VelInit = true;
                c.Pos = p;
            }
        }

        /// <summary>A blip is painted when the search antenna sweeps over its
        /// bearing: the scope shows what the radar saw on its last turn.</summary>
        static void Paint()
        {
            float sweep = Mathf.Repeat(_rig.Yaw + _rig.SearchAngle, 360f);
            float arc = Mathf.Repeat(sweep - _lastSweep, 360f);
            if (arc > 180f) arc = 0f;
            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact c = _contacts[i];
                if (!c.Visible) continue;
                float off = Mathf.Repeat(Bearing(c.Pos) - _lastSweep, 360f);
                if (off <= arc) c.PaintedAt = Time.time;
            }
            _lastSweep = sweep;
        }

        static float Bearing(Vector3 p)
        {
            Vector3 l = _rig.transform.InverseTransformPoint(p);
            return Mathf.Repeat(Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg, 360f);
        }

        static void KeepLock()
        {
            // Behind a hill for a moment the tracking radar coasts on its last
            // track; longer than that and the lock is gone.
            if (_lock != null && _lock.Go != null && Time.time - _lock.VisibleAt < 1.5f) return;
            bool had = _lock != null;
            _lock = Best(null);
            if (had) Hint(_lock == null ? GepardText.Lost() : GepardText.Locked(Describe(_lock)), 2f);
        }

        static Contact Best(Contact after)
        {
            List<Contact> vis = new List<Contact>();
            for (int i = 0; i < _contacts.Count; i++)
                if (_contacts[i].Visible && _contacts[i].Go != null) vis.Add(_contacts[i]);
            if (vis.Count == 0) return null;
            if (after == null) return vis[0];
            int at = vis.IndexOf(after);
            return vis[(at + 1) % vis.Count];
        }

        static void NextTarget()
        {
            Contact next = Best(_lock);
            if (next == null) { Hint(GepardText.RadarOnNothing(), 2f); return; }
            _lock = next;
            Hint(GepardText.Locked(Describe(next)), 2f);
            Publish(true);
        }

        // ------------------------------------------------------------ laying

        /// <summary>Yaw and pitch of a world direction in the hull's frame -
        /// the frame the turret turns in.</summary>
        static void Angles(Vector3 world, out float yaw, out float pitch)
        {
            Vector3 l = _rig.transform.InverseTransformDirection(world);
            yaw = Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg;
            pitch = Mathf.Atan2(l.y, Mathf.Sqrt(l.x * l.x + l.z * l.z)) * Mathf.Rad2Deg;
        }

        static Vector3 Direction(float yaw, float pitch)
        {
            return _rig.transform.TransformDirection(Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward);
        }

        static float Speed() { return Mathf.Max(100f, Gepard.CfgVelocity.Value); }
        static float Gravity() { return Mathf.Abs(Physics.gravity.y); }

        /// <summary>
        /// The fire control's one job: where the target WILL be when the round
        /// arrives, and how much higher to aim so gravity brings the round down
        /// onto it. Four fixed-point passes converge for anything slower than
        /// the round.
        /// </summary>
        internal static Vector3 Intercept(Vector3 from, Vector3 p, Vector3 v, out float tof)
        {
            float speed = Speed();
            tof = Vector3.Distance(from, p) / speed;
            Vector3 aim = p;
            for (int i = 0; i < 4; i++)
            {
                aim = p + v * tof;
                tof = Vector3.Distance(from, aim) / speed;
            }
            return aim + Vector3.up * (0.5f * Gravity() * tof * tof);
        }

        static void Aim(float dt)
        {
            float min = Gepard.CfgPitchMin.Value, max = Gepard.CfgPitchMax.Value;
            Vector3 gunMid = _rig.GunCentre();
            float wantYaw, wantPitch;
            bool auto = _radar && _lock != null && _lock.Go != null;
            _outOfReach = false;
            if (auto)
            {
                float tof;
                _aimPoint = Intercept(gunMid, _lock.Pos, _lock.Vel, out tof);
                _aimValid = true;
                Angles(_aimPoint - gunMid, out wantYaw, out wantPitch);
                if (wantPitch > max || wantPitch < min) _outOfReach = true;
                _cmdYaw = wantYaw;
                _cmdPitch = Mathf.Clamp(wantPitch, min, max);
            }
            else
            {
                // The mouse moves the line of sight; the sight may run only
                // AimLead degrees ahead of the guns, so the picture turns at
                // the turret's speed and the traverse rate still counts.
                float sens = Gepard.CfgSensitivity.Value;
                _cmdYaw += Input.GetAxis("Mouse X") * sens;
                _cmdPitch += Input.GetAxis("Mouse Y") * sens;
                float lead = Mathf.Max(0.5f, Gepard.CfgAimLead.Value);
                float losPitch = _rig.Pitch - _superElev;
                _cmdYaw = _rig.Yaw + Mathf.Clamp(Mathf.DeltaAngle(_rig.Yaw, _cmdYaw), -lead, lead);
                _cmdPitch = Mathf.Clamp(Mathf.Clamp(_cmdPitch, losPitch - lead, losPitch + lead), min, max);

                // Rangefinder along the line of sight, then superelevation for
                // that range: the ballistic half of the fire control.
                Vector3 eye = _rig.Sight.position;
                Vector3 los = Direction(_cmdYaw, _cmdPitch);
                float range = Mathf.Min(Gepard.CfgMaxRange.Value, 800f);
                RaycastHit hit;
                if (Physics.Raycast(eye, los, out hit, Gepard.CfgMaxRange.Value,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                    && !hit.transform.IsChildOf(_root))
                    range = hit.distance;
                float t = range / Speed();
                _aimPoint = eye + los * range + Vector3.up * (0.5f * Gravity() * t * t);
                _aimValid = true;
                Angles(_aimPoint - gunMid, out wantYaw, out wantPitch);
                float ly, lp;
                Angles(los, out ly, out lp);
                _superElev = Mathf.Lerp(_superElev, Mathf.Clamp(wantPitch - lp, 0f, 5f), 1f - Mathf.Exp(-dt * 6f));
            }
            wantPitch = Mathf.Clamp(wantPitch, min, max);
            float yaw = Mathf.MoveTowardsAngle(_rig.Yaw, wantYaw, Gepard.CfgTurnSpeed.Value * dt);
            float pitch = Mathf.MoveTowards(_rig.Pitch, wantPitch, Gepard.CfgElevSpeed.Value * dt);
            float track = 0f;
            if (auto)
            {
                float by, bp;
                Angles(_lock.Pos - _rig.RadarTrack.position, out by, out bp);
                track = Mathf.Clamp(Mathf.DeltaAngle(yaw, by), -75f, 75f);
            }
            track = Mathf.MoveTowardsAngle(_rig.TrackYaw, track, 240f * dt);
            if (yaw > 180f) yaw -= 360f;
            if (yaw < -180f) yaw += 360f;
            _rig.Apply(yaw, pitch, track);
            _gunError = _aimValid ? Vector3.Angle(_rig.Bore(0), _aimPoint - gunMid) : 180f;
        }

        // ----------------------------------------------------------- trigger

        static int Rounds()
        {
            if (_rig == null) return 0;
            int n;
            if (!_rounds.TryGetValue(_rig.GetInstanceID(), out n))
            {
                n = Mathf.Max(0, Gepard.CfgRounds.Value);
                _rounds[_rig.GetInstanceID()] = n;
            }
            return n;
        }

        static void Trigger()
        {
            bool held = Input.GetMouseButton(0);
            int rounds = Rounds();
            _firing = false;
            if (!held || _reloading) return;
            if (rounds <= 0) { StartReload(); return; }
            float interval = 60f / Mathf.Max(60f, Gepard.CfgRpm.Value);
            if (_nextShot < Time.time - 0.1f) _nextShot = Time.time;
            int n = 0;
            while (Time.time >= _nextShot && rounds > 0 && n < 4)
            {
                Shoot();
                rounds--;
                _nextShot += interval;
                n++;
            }
            _rounds[_rig.GetInstanceID()] = rounds;
            _firing = rounds > 0;
            if (rounds == 0) StartReload();
        }

        /// <summary>One round from the barrel whose turn it is. Along the
        /// barrel - and, when the barrel is laid within a degree of the aim
        /// point, converged onto it: both guns sit 1.3 m beside the sight and
        /// are harmonised at the range the fire control measured.</summary>
        static void Shoot()
        {
            int gun = _gun;
            _gun = 1 - _gun;
            Vector3 muzzle = _rig.Muzzle(gun);
            Vector3 dir = _rig.Bore(gun);
            if (_aimValid)
            {
                Vector3 want = (_aimPoint - muzzle).normalized;
                if (Vector3.Angle(dir, want) < 1f) dir = want;
            }
            GepardShots.Fire(_rig, gun, dir, true);
        }

        static void StartReload()
        {
            if (_reloading) return;
            int full = Mathf.Max(1, Gepard.CfgRounds.Value);
            int add = full;
            if (Gepard.CfgRequireAmmo.Value)
            {
                int id = Gepard.CfgAmmoId.Value;
                if (!Turret.TakeItem(id, "Gepard"))
                {
                    if (Time.time >= _noAmmoSaid)
                    {
                        _noAmmoSaid = Time.time + 3f;
                        Hint(GepardText.NoAmmo(id), 3f);
                    }
                    return;
                }
                add = Mathf.Max(1, Gepard.CfgRoundsPerBelt.Value);
            }
            _reloading = true;
            _reloadAdd = add;
            float seconds = Mathf.Max(0.5f, Gepard.CfgReloadSeconds.Value);
            _reloadUntil = Time.time + seconds;
            NativeActionProgress.Begin(ReloadOwner, GepardText.Loading(), seconds, false, null, null);
        }

        static void TickReload()
        {
            if (!_reloading || Time.time < _reloadUntil) return;
            _reloading = false;
            NativeActionProgress.End(ReloadOwner);
            int full = Mathf.Max(1, Gepard.CfgRounds.Value);
            _rounds[_rig.GetInstanceID()] = Mathf.Min(full, Rounds() + _reloadAdd);
        }

        static void Publish(bool now)
        {
            if (_rig == null || _root == null) return;
            bool firing = _manning && _firing;
            if (!now && firing == _sentFiring && Time.time < _nextPublish) return;
            _nextPublish = Time.time + 0.1f;
            _sentFiring = firing;
            GepardNet.SendPose(_root, _rig.Yaw, _rig.Pitch, _rig.TrackYaw, firing, _manning && _radar);
        }

        // ------------------------------------------------------------ damage

        /// <summary>A live round passes an aircraft or drone close enough for
        /// its proximity burst: the nearest along the segment wins.</summary>
        internal static bool Proximity(Vector3 a, Vector3 b, out Contact hit, out Vector3 at)
        {
            hit = null;
            at = b;
            Vector3 seg = b - a;
            float len2 = seg.sqrMagnitude;
            if (len2 < 1e-8f) return false;
            float fuze = Mathf.Max(0f, Gepard.CfgFuze.Value);
            float best = 2f;
            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact c = _contacts[i];
                if (!c.Air || c.Go == null) continue;
                float t = Mathf.Clamp01(Vector3.Dot(c.Pos - a, seg) / len2);
                Vector3 p = a + seg * t;
                float r = c.Radius + fuze;
                if ((c.Pos - p).sqrMagnitude > r * r || t >= best) continue;
                best = t;
                hit = c;
                at = p;
            }
            return hit != null;
        }

        /// <summary>A live round struck a collider.</summary>
        internal static void Struck(GameObject go, Vector3 point, Vector3 dir)
        {
            if (go == null) return;
            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact c = _contacts[i];
                if (c.Go == null || c.Kind == 1) continue;
                if (go.transform == c.Go.transform || go.transform.IsChildOf(c.Go.transform))
                {
                    Hit(c, point, dir);
                    return;
                }
            }
            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            Component vehicle = vgsType == null ? null : go.GetComponentInParent(vgsType);
            if (vehicle != null)
            {
                if (vehicle.transform != _root) VehicleHit(vehicle);
                return;
            }
            float dmg = Gepard.CfgInfantryDamage.Value;
            if (Turret.TryDamage(go, "NPC_AI2", "ApplyDamage", dmg)) return;
            if (Turret.TryDamage(go, "Animal_AI", "NetworkApplyDamage", dmg)) return;
            Turret.TryDamage(go, "PlayerNetworkController", "PlayerApplyDamage", dmg);
        }

        /// <summary>A hit on a known contact, through each kind's own entry
        /// point - the ones the Stinger uses, with a 35 mm-sized number.</summary>
        internal static void Hit(Contact c, Vector3 point, Vector3 dir)
        {
            if (c == null || c.Go == null) return;
            float dmg = Mathf.Max(1f, Gepard.CfgDroneDamage.Value);
            switch (c.Kind)
            {
                case 0: HeliHit(c, point); break;
                case 1: if (c.Vehicle != null) VehicleHit(c.Vehicle); break;
                case 2:
                    if (Time.time < c.NextHit) return;
                    c.NextHit = Time.time + 0.15f;
                    Drone.Net.Send(Drone.Net.Treffer, point, new Vector3(c.Actor, 0f, 0f), dmg, true);
                    break;
                case 3:
                    if (Time.time < c.NextHit) return;
                    c.NextHit = Time.time + 0.15f;
                    SurvNet.Send(SurvNet.Treffer, point, new Vector3(c.Actor, 0f, 0f), dmg, true);
                    break;
                case 4:
                    CrewDrone.Beschuss(point - dir * 4f, dir, 8f, dmg);
                    break;
                case 5:
                    if (Time.time < c.NextHit) return;
                    c.NextHit = Time.time + 0.15f;
                    // ArtyBattery.Shoot takes one of the drone's three hit
                    // points per call; a 35 mm round takes all of them.
                    for (int i = 0; i < 3; i++) ArtyBattery.Shoot(point - dir * 4f, dir);
                    break;
            }
        }

        static void HeliHit(Contact c, Vector3 point)
        {
            int view = c.HeliView;
            if (view <= 0 || PlayerHeli.MissileTarget(view) == null) return;
            int n;
            _heliHits.TryGetValue(view, out n);
            n++;
            int need = Mathf.Max(1, Gepard.CfgHeliHits.Value);
            if (n < need) { _heliHits[view] = n; return; }
            _heliHits.Remove(view);
            RevivalPlugin.L.LogInfo("Gepard: helicopter " + view + " shot down after " + n + " hits.");
            GepardNet.SendHeliKill(view, point);
            PlayerHeli.MissileImpact(view, point);
            Hint(GepardText.HeliDown(), 3f);
        }

        /// <summary>
        /// A vehicle's hit points through the game's own armour path
        /// (ApplyDamage partType 10: one to one, one accepted hit per 0.3 s).
        /// The class decides how many seconds of hits a kill takes, which is
        /// what keeps a tank from dying to one burst.
        /// </summary>
        static void VehicleHit(Component vehicle)
        {
            if (vehicle == null) return;
            int id = vehicle.GetInstanceID();
            float next;
            if (_vehicleNext.TryGetValue(id, out next) && Time.time < next) return;
            _vehicleNext[id] = Time.time + 0.3f;
            object d = Field(vehicle, "Durability");
            if (d is float && (float)d <= 0f) return;
            if (!_applyLooked)
            {
                _applyLooked = true;
                _applyDamage = AccessTools.Method(vehicle.GetType(), "ApplyDamage",
                    new Type[] { typeof(float), typeof(int) }, null);
            }
            if (_applyDamage == null) return;
            bool tank = Tank.IstPanzer(vehicle.transform);
            bool armour = !tank && vehicle.name.IndexOf("btr-80a", StringComparison.OrdinalIgnoreCase) >= 0;
            float seconds = tank ? Gepard.CfgTankSeconds.Value
                : armour ? Gepard.CfgApcSeconds.Value : Gepard.CfgSoftSeconds.Value;
            float pool = VehicleArmor.CfgPool == null ? 2000f : VehicleArmor.CfgPool.Value;
            float perHit = pool * 0.3f / Mathf.Max(0.3f, seconds);
            _applyDamage.Invoke(vehicle, new object[] { perHit, 10 });
        }

        // ------------------------------------------------------------ camera

        /// <summary>The sight camera, after the game has written its own.</summary>
        internal static void LateTick()
        {
            if (!_manning || _rig == null) return;
            try
            {
                Camera cam = CameraOwner.ViewCamera();
                if (cam == null) return;
                Vector3 eye = _rig.Sight.position;
                Vector3 los;
                if (_radar && _lock != null && _lock.Go != null) los = (_lock.Pos - eye).normalized;
                else los = Direction(_cmdYaw, _cmdPitch);
                if (los.sqrMagnitude < 0.5f) los = _rig.Turret.forward;
                Vector3 up = Vector3.Cross(los, _rig.Turret.right);
                if (up.sqrMagnitude < 0.0001f) up = _rig.Turret.up;
                Quaternion want = Quaternion.LookRotation(los, up);
                _camRot = _camInit ? Quaternion.Slerp(_camRot, want, 1f - Mathf.Exp(-Time.deltaTime * 18f)) : want;
                _camInit = true;
                cam.transform.position = eye;
                cam.transform.rotation = _camRot;
                cam.fieldOfView = Fovs[Mathf.Clamp(_fov, 0, Fovs.Length - 1)];
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gepard camera: " + ex);
                SetManning(false);
            }
        }

        // --------------------------------------------------------------- HUD

        static Texture2D _white;
        static GUIStyle _text, _small;
        static readonly Color Green = new Color(0.45f, 1f, 0.55f, 0.95f);
        static readonly Color Dim = new Color(0.45f, 1f, 0.55f, 0.45f);
        static readonly Color Red = new Color(1f, 0.3f, 0.2f, 0.95f);
        static readonly Color Amber = new Color(1f, 0.8f, 0.3f, 0.95f);

        internal static void Draw()
        {
            try
            {
                if (Event.current == null || Event.current.type != EventType.Repaint) return;
                bool hint = _hint != null && Time.time < _hintUntil;
                if (!_manning || _rig == null)
                {
                    if (hint) { Styles(); Shadowed(new Rect(0, Screen.height * 0.72f, Screen.width, 40), _hint, _text, Amber, Centre); }
                    return;
                }
                Styles();
                Camera cam = CameraOwner.ViewCamera();
                float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
                Reticle(cx, cy);
                if (cam != null && _aimValid)
                {
                    // Where the guns point, at the fire control's range: in
                    // radar mode the gap to the centre is the lead, by hand it
                    // shows the guns catching up with the sight.
                    float range = Vector3.Distance(_rig.GunCentre(), _aimPoint);
                    Vector3 p = cam.WorldToScreenPoint(_rig.GunCentre() + _rig.Bore(0) * range);
                    if (p.z > 0f) Ring(p.x, Screen.height - p.y, 7f, _gunError < 0.8f ? Green : Amber);
                }
                if (_radar && cam != null) Boxes(cam);
                if (_radar) Scope();
                Status();
                if (hint) Shadowed(new Rect(0, Screen.height * 0.72f, Screen.width, 40), _hint, _text, Amber, Centre);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gepard HUD: " + ex.Message);
            }
        }

        static void Styles()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
                _white.hideFlags = HideFlags.HideAndDontSave;
            }
            int size = Mathf.Clamp(Screen.height / 48, 12, 26);
            if (_text == null || _text.fontSize != size)
            {
                _text = new GUIStyle(GUI.skin.label);
                _text.fontSize = size;
                _small = new GUIStyle(GUI.skin.label);
                _small.fontSize = Mathf.Max(10, size - 4);
            }
        }

        static void Line(Vector2 a, Vector2 b, float w, Color c)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            Matrix4x4 m = GUI.matrix;
            GUI.color = c;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - w * 0.5f, len, w), _white);
            GUI.matrix = m;
            GUI.color = Color.white;
        }

        static void Ring(float x, float y, float r, Color c)
        {
            const int N = 24;
            for (int i = 0; i < N; i++)
            {
                float a0 = i * Mathf.PI * 2f / N, a1 = (i + 1) * Mathf.PI * 2f / N;
                Line(new Vector2(x + Mathf.Cos(a0) * r, y + Mathf.Sin(a0) * r),
                     new Vector2(x + Mathf.Cos(a1) * r, y + Mathf.Sin(a1) * r), 2f, c);
            }
        }

        static void Box(float x, float y, float h, Color c)
        {
            Line(new Vector2(x - h, y - h), new Vector2(x + h, y - h), 2f, c);
            Line(new Vector2(x + h, y - h), new Vector2(x + h, y + h), 2f, c);
            Line(new Vector2(x + h, y + h), new Vector2(x - h, y + h), 2f, c);
            Line(new Vector2(x - h, y + h), new Vector2(x - h, y - h), 2f, c);
        }

        const int Left = 0, Centre = 1, Right = 2;

        /// <summary>A line with a drop shadow, placed by measurement:
        /// GUIStyle.alignment and FontStyle live in
        /// UnityEngine.TextRenderingModule, which this build does not
        /// reference (the heli and the technical measure theirs too).</summary>
        static void Shadowed(Rect r, string s, GUIStyle st, Color c, int align)
        {
            GUIContent content = new GUIContent(s);
            Vector2 size = st.CalcSize(content);
            float x = align == Centre ? r.x + (r.width - size.x) * 0.5f
                : align == Right ? r.x + r.width - size.x : r.x;
            float y = align == Right ? r.y + r.height - size.y : r.y;
            Rect at = new Rect(x, y, size.x + 2f, size.y);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(at.x + 1, at.y + 1, at.width, at.height), content, st);
            GUI.color = c;
            GUI.Label(at, content, st);
            GUI.color = Color.white;
        }

        static void Reticle(float cx, float cy)
        {
            float r = Screen.height * 0.045f;
            Ring(cx, cy, r, Green);
            Line(new Vector2(cx - r * 2.2f, cy), new Vector2(cx - r * 1.15f, cy), 2f, Green);
            Line(new Vector2(cx + r * 1.15f, cy), new Vector2(cx + r * 2.2f, cy), 2f, Green);
            Line(new Vector2(cx, cy + r * 1.15f), new Vector2(cx, cy + r * 2.2f), 2f, Green);
            Line(new Vector2(cx - 2f, cy), new Vector2(cx + 2f, cy), 3f, Green);
        }

        static void Boxes(Camera cam)
        {
            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact c = _contacts[i];
                if (!c.Visible || c.Go == null) continue;
                Vector3 p = cam.WorldToScreenPoint(c.Pos);
                if (p.z <= 0f) continue;
                float y = Screen.height - p.y;
                bool locked = c == _lock;
                float h = Mathf.Clamp(Screen.height * 0.9f * c.Radius / Mathf.Max(1f, p.z), 8f, 90f);
                Box(p.x, y, h, locked ? Red : Dim);
                if (locked)
                {
                    Shadowed(new Rect(p.x + h + 6f, y - h, 300f, 60f),
                        Describe(c) + "\n" + Mathf.RoundToInt(Vector3.Distance(_rig.transform.position, c.Pos))
                        + "  " + Mathf.RoundToInt(c.Vel.magnitude) + "/s", _small, Red, Left);
                }
            }
        }

        /// <summary>The PPI scope: hull forward is up, the sweep is the search
        /// antenna's real angle, each blip fades until the antenna comes round
        /// again. The line is where the guns point.</summary>
        static void Scope()
        {
            float r = Mathf.Clamp(Screen.height * 0.13f, 70f, 180f);
            float cx = 24f + r, cy = Screen.height - 24f - r;
            GUI.color = new Color(0f, 0.08f, 0.02f, 0.72f);
            GUI.DrawTexture(new Rect(cx - r, cy - r, r * 2f, r * 2f), _white);
            GUI.color = Color.white;
            Ring(cx, cy, r, Dim);
            Ring(cx, cy, r * 0.5f, new Color(Dim.r, Dim.g, Dim.b, 0.25f));
            float sweep = (_rig.Yaw + _rig.SearchAngle) * Mathf.Deg2Rad;
            Line(new Vector2(cx, cy), new Vector2(cx + Mathf.Sin(sweep) * r, cy - Mathf.Cos(sweep) * r), 2f, Green);
            float gun = _rig.Yaw * Mathf.Deg2Rad;
            Line(new Vector2(cx, cy), new Vector2(cx + Mathf.Sin(gun) * r * 0.6f, cy - Mathf.Cos(gun) * r * 0.6f), 3f, Amber);
            float range = Mathf.Max(100f, Gepard.CfgRadarRange.Value);
            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact c = _contacts[i];
                if (!c.Visible || c.Go == null) continue;
                Vector3 l = _rig.transform.InverseTransformPoint(c.Pos);
                Vector2 flat = new Vector2(l.x, l.z) * _rig.transform.lossyScale.x;
                float d = flat.magnitude / range;
                if (d > 1f) continue;
                Vector2 at = new Vector2(cx, cy) + new Vector2(flat.x, -flat.y).normalized * d * r;
                bool locked = c == _lock;
                float fade = Mathf.Clamp01(1f - (Time.time - c.PaintedAt) / 1.2f);
                if (locked) fade = 1f;
                if (fade <= 0.02f) continue;
                Color col = locked ? Red : new Color(Green.r, Green.g, Green.b, fade);
                float s = c.Air ? 4f : 3f;
                if (c.Air)
                {
                    Line(new Vector2(at.x - s, at.y), new Vector2(at.x, at.y - s), 2f, col);
                    Line(new Vector2(at.x, at.y - s), new Vector2(at.x + s, at.y), 2f, col);
                    Line(new Vector2(at.x + s, at.y), new Vector2(at.x, at.y + s), 2f, col);
                    Line(new Vector2(at.x, at.y + s), new Vector2(at.x - s, at.y), 2f, col);
                }
                else
                {
                    GUI.color = col;
                    GUI.DrawTexture(new Rect(at.x - s, at.y - s, s * 2f, s * 2f), _white);
                    GUI.color = Color.white;
                }
                if (locked) Box(at.x, at.y, 7f, Red);
            }
        }

        static void Status()
        {
            float x = 24f, y = 24f, w = 460f, h = _text.fontSize + 8f;
            string mode = _radar ? GepardText.ModeRadar() : GepardText.ModeManual();
            Shadowed(new Rect(x, y, w, h), "GEPARD  " + mode, _text, _radar ? Green : Amber, Left);
            y += h;
            if (_radar)
            {
                string t = _lock == null ? GepardText.NoTarget()
                    : Describe(_lock) + "  " + Mathf.RoundToInt(Vector3.Distance(_rig.transform.position, _lock.Pos));
                Shadowed(new Rect(x, y, w, h), t, _small, _lock == null ? Amber : Red, Left);
                y += h;
                if (_lock != null)
                {
                    string lay = _outOfReach ? GepardText.OutOfReach()
                        : _gunError < 0.8f ? GepardText.OnTarget() : GepardText.Laying();
                    Shadowed(new Rect(x, y, w, h), lay, _small, _outOfReach ? Red : _gunError < 0.8f ? Green : Amber, Left);
                    y += h;
                }
            }
            int rounds = Rounds();
            string ammo = GepardText.Rounds() + " " + rounds + "/" + Mathf.Max(1, Gepard.CfgRounds.Value)
                + (_reloading ? "  " + GepardText.Loading() : "");
            Shadowed(new Rect(x, y, w, h), ammo, _small, rounds > 0 ? Green : Red, Left);
            y += h;
            Shadowed(new Rect(x, y, w, h), GepardText.Angles(Mathf.RoundToInt(Mathf.Repeat(_rig.Yaw, 360f)),
                Mathf.RoundToInt(_rig.Pitch), Mathf.RoundToInt(Fovs[_fov])), _small, Dim, Left);
            Shadowed(new Rect(0, Screen.height - 34f, Screen.width - 24f, 30f),
                GepardText.Keys(_radarKey.ToString(), _nextKey == KeyCode.Mouse1 ? GepardText.RightMouse() : _nextKey.ToString(),
                _manKey.ToString()), _small, Dim, Right);
        }

        static string Describe(Contact c)
        {
            switch (c.Kind)
            {
                case 0: return GepardText.KindHeli();
                case 1:
                    if (c.Vehicle != null && Tank.IstPanzer(c.Vehicle.transform)) return GepardText.KindTank();
                    return GepardText.KindVehicle();
                default: return GepardText.KindDrone();
            }
        }

        // ------------------------------------------------------------- access

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
    }

    // =====================================================================
    // Rounds

    /// <summary>
    /// Every 35 mm round in flight on this client: position, velocity,
    /// gravity, its tracer, and what happens at its end. Rounds of the local
    /// gunner are live and do damage through GepardGun; a remote gunner's are
    /// drawn, burst and are heard, but hurt nobody (his own client does that).
    /// </summary>
    internal static class GepardShots
    {
        sealed class Round
        {
            public Vector3 Pos, Vel;
            public float Age, Life, Flown;
            public bool Live;
            public Transform Owner;
            public LineRenderer Line;
        }

        static readonly List<Round> _rounds = new List<Round>();
        static readonly Stack<LineRenderer> _pool = new Stack<LineRenderer>();
        const float TracerLength = 14f;
        static readonly Color TracerHead = new Color(1f, 0.55f, 0.25f, 1f);
        static readonly Color TracerTail = new Color(1f, 0.25f, 0.08f, 0f);

        internal static void Fire(GepardRig rig, int gun, Vector3 dir, bool live)
        {
            if (rig == null) return;
            Vector3 muzzle = rig.Muzzle(gun);
            float mil = Mathf.Max(0f, Gepard.CfgDispersion == null ? 1.5f : Gepard.CfgDispersion.Value) * 0.001f;
            Vector2 spread = UnityEngine.Random.insideUnitCircle * mil;
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
            Vector3 up = Vector3.Cross(dir, right);
            dir = (dir + right * spread.x + up * spread.y).normalized;
            float speed = Mathf.Max(100f, Gepard.CfgVelocity == null ? 1175f : Gepard.CfgVelocity.Value);

            Round r = new Round();
            r.Pos = muzzle;
            r.Vel = dir * speed;
            r.Life = Mathf.Max(200f, Gepard.CfgMaxRange == null ? 2600f : Gepard.CfgMaxRange.Value) / speed;
            r.Live = live;
            r.Owner = rig.Vehicle;
            r.Line = Take();
            if (r.Line != null)
            {
                r.Line.SetPosition(0, muzzle);
                r.Line.SetPosition(1, muzzle);
            }
            _rounds.Add(r);

            rig.Kick(gun);
            GepardFx.Muzzle(muzzle, dir);
            GepardFx.Casing(gun == 0 ? rig.GunR : rig.GunL, gun == 0 ? 1f : -1f);
            VehicleShotSound.Play(muzzle, false);
        }

        internal static void Tick()
        {
            if (_rounds.Count == 0) return;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            float g = Physics.gravity.y;
            for (int i = _rounds.Count - 1; i >= 0; i--)
            {
                Round r = _rounds[i];
                bool done = false;
                try { done = Step(r, dt, g); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError("Gepard round: " + ex.Message);
                    done = true;
                }
                if (!done) continue;
                Give(r.Line);
                _rounds.RemoveAt(i);
            }
        }

        static bool Step(Round r, float dt, float g)
        {
            r.Age += dt;
            Vector3 next = r.Pos + r.Vel * dt + Vector3.up * (0.5f * g * dt * dt);
            r.Vel += Vector3.up * (g * dt);
            Vector3 seg = next - r.Pos;
            float len = seg.magnitude;
            if (len > 1e-4f)
            {
                Vector3 dir = seg / len;
                RaycastHit hit;
                bool struck = Cast(r.Pos, dir, len, r.Owner, out hit);
                Vector3 end = struck ? hit.point : next;
                if (r.Live)
                {
                    GepardGun.Contact c;
                    Vector3 at;
                    if (GepardGun.Proximity(r.Pos, end, out c, out at))
                    {
                        GepardFx.Burst(at, 1f);
                        GepardGun.Hit(c, at, dir);
                        return true;
                    }
                }
                if (struck)
                {
                    GepardFx.Impact(hit.point, hit.normal);
                    if (r.Live) GepardGun.Struck(hit.collider.gameObject, hit.point, dir);
                    return true;
                }
                r.Flown += len;
            }
            r.Pos = next;
            if (r.Age >= r.Life)
            {
                // The HEI round's self-destruct: a small black puff in the sky.
                GepardFx.Burst(r.Pos, 0.7f);
                return true;
            }
            if (r.Line != null)
            {
                Vector3 back = r.Vel.normalized * Mathf.Min(TracerLength, r.Flown);
                r.Line.SetPosition(0, r.Pos - back);
                r.Line.SetPosition(1, r.Pos);
            }
            return false;
        }

        /// <summary>A ray that steps past the firing vehicle (its own hull and
        /// crew) and ignores trigger volumes.</summary>
        static bool Cast(Vector3 from, Vector3 dir, float len, Transform owner, out RaycastHit hit)
        {
            Vector3 origin = from;
            float rest = len;
            for (int i = 0; i < 4 && rest > 0f; i++)
            {
                if (!Physics.Raycast(origin, dir, out hit, rest,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    return false;
                if (owner == null || !hit.transform.IsChildOf(owner)) return true;
                rest -= hit.distance + 0.05f;
                origin = hit.point + dir * 0.05f;
            }
            hit = new RaycastHit();
            return false;
        }

        static LineRenderer Take()
        {
            while (_pool.Count > 0)
            {
                LineRenderer l = _pool.Pop();
                if (l == null) continue;
                l.gameObject.SetActive(true);
                return l;
            }
            Material mat = RocketHook.TracerMaterial();
            if (mat == null) return null;
            GameObject go = new GameObject("NDR Gepard tracer");
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = mat;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = 0.16f;
            line.endWidth = 0.05f;
            line.startColor = TracerTail;
            line.endColor = TracerHead;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        static void Give(LineRenderer l)
        {
            if (l == null) return;
            l.gameObject.SetActive(false);
            if (_pool.Count < 128) _pool.Push(l);
            else UnityEngine.Object.Destroy(l.gameObject);
        }
    }

    /// <summary>
    /// Muzzle flash, smoke, flying cases, impact sparks and bursts - four
    /// shared world-space particle systems fed with Emit, so 18 rounds a
    /// second cost no new objects at all.
    /// </summary>
    internal static class GepardFx
    {
        static ParticleSystem _flash, _sparks, _smoke, _cases;
        static Texture2D _soft;

        static bool Ready()
        {
            if (_flash != null && _sparks != null && _smoke != null && _cases != null) return true;
            try
            {
                if (_soft == null) _soft = Soft();
                if (_flash == null) _flash = Make("NDR Gepard flash", true, 0f, false, 300);
                if (_sparks == null) _sparks = Make("NDR Gepard sparks", true, 0.9f, true, 800);
                if (_smoke == null) _smoke = Make("NDR Gepard smoke", false, -0.03f, false, 600);
                if (_cases == null) _cases = Make("NDR Gepard cases", false, 1.3f, false, 300);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gepard effects: " + ex.Message);
            }
            return _flash != null && _sparks != null && _smoke != null && _cases != null;
        }

        static Texture2D Soft()
        {
            Texture2D t = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            Color[] px = new Color[64 * 64];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float dx = (x - 31.5f) / 31.5f, dy = (y - 31.5f) / 31.5f;
                    float a = Mathf.Clamp01(1f - dx * dx - dy * dy);
                    px[y * 64 + x] = new Color(1f, 1f, 1f, a * a);
                }
            t.SetPixels(px);
            t.Apply();
            t.wrapMode = TextureWrapMode.Clamp;
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        static ParticleSystem Make(string name, bool additive, float gravity, bool stretch, int max)
        {
            string[] shaders = additive
                ? new string[] { "Particles/Additive", "Legacy Shaders/Particles/Additive" }
                : new string[] { "Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended" };
            Shader shader = null;
            for (int i = 0; i < shaders.Length && shader == null; i++) shader = Shader.Find(shaders[i]);
            if (shader == null) return null;
            Material m = new Material(shader);
            m.mainTexture = _soft;
            m.hideFlags = HideFlags.HideAndDontSave;

            GameObject go = new GameObject(name);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.duration = 1f;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = new ParticleSystem.MinMaxCurve(gravity);
            main.maxParticles = max;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f);
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = false;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = false;
            if (!additive)
            {
                ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
                col.enabled = true;
                Gradient fade = new Gradient();
                fade.SetKeys(new GradientColorKey[] { new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f) }, new GradientAlphaKey[] {
                    new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.7f, 0.5f),
                    new GradientAlphaKey(0f, 1f) });
                col.color = new ParticleSystem.MinMaxGradient(fade);
                ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
                size.enabled = gravity < 0.5f;   // smoke grows, cases do not
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 2.4f));
            }
            ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = m;
            r.renderMode = stretch ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            if (stretch) { r.velocityScale = 0.035f; r.lengthScale = 1f; }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play();
            return ps;
        }

        static void Emit(ParticleSystem ps, Vector3 p, Vector3 v, float size, float life, Color c)
        {
            ParticleSystem.EmitParams e = new ParticleSystem.EmitParams();
            e.position = p;
            e.velocity = v;
            e.startSize = size;
            e.startLifetime = life;
            e.startColor = c;
            ps.Emit(e, 1);
        }

        static Vector3 Rnd() { return UnityEngine.Random.onUnitSphere; }

        /// <summary>A short cone of fire along the bore and a puff of gun
        /// smoke that drifts off it.</summary>
        internal static void Muzzle(Vector3 at, Vector3 dir)
        {
            if (!Ready()) return;
            Color hot = new Color(1f, 0.82f, 0.5f, 1f);
            Emit(_flash, at + dir * 0.2f, dir * 2f, 1.5f, 0.05f, hot);
            Emit(_flash, at + dir * 0.9f, dir * 4f, 1.0f, 0.04f, hot);
            Emit(_flash, at + dir * 1.6f, dir * 6f, 0.6f, 0.035f, new Color(1f, 0.6f, 0.3f, 1f));
            Emit(_smoke, at + dir * 0.8f, dir * 4f + Rnd() * 0.6f, 0.9f, 0.9f, new Color(0.6f, 0.58f, 0.55f, 0.35f));
        }

        /// <summary>The empty case, thrown out of the side of the gun housing
        /// (outward for each gun) and falling.</summary>
        internal static void Casing(Transform gun, float side)
        {
            if (gun == null || !Ready()) return;
            Vector3 at = gun.position - gun.forward * 0.3f * gun.lossyScale.x;
            Vector3 v = gun.right * side * UnityEngine.Random.Range(3f, 5f) + Vector3.up * UnityEngine.Random.Range(1.5f, 3f);
            Emit(_cases, at, v, 0.1f, 1.4f, new Color(0.85f, 0.65f, 0.25f, 1f));
        }

        /// <summary>A 35 mm HE-I round on a surface: flash, sparks thrown off
        /// the surface, dust.</summary>
        internal static void Impact(Vector3 at, Vector3 normal)
        {
            if (!Ready()) return;
            Emit(_flash, at + normal * 0.2f, Vector3.zero, 2.0f, 0.06f, new Color(1f, 0.75f, 0.4f, 1f));
            for (int i = 0; i < 6; i++)
                Emit(_sparks, at, (normal + Rnd() * 0.8f).normalized * UnityEngine.Random.Range(6f, 16f),
                     0.07f, UnityEngine.Random.Range(0.15f, 0.4f), new Color(1f, 0.7f, 0.3f, 1f));
            for (int i = 0; i < 2; i++)
                Emit(_smoke, at + normal * 0.4f, normal * 1.5f + Rnd() * 0.5f, UnityEngine.Random.Range(1.2f, 2f),
                     UnityEngine.Random.Range(1f, 1.6f), new Color(0.35f, 0.32f, 0.28f, 0.55f));
        }

        /// <summary>A burst in the air (proximity or self-destruct): the dark
        /// puff anti-aircraft fire is known by.</summary>
        internal static void Burst(Vector3 at, float scale)
        {
            if (!Ready()) return;
            Emit(_flash, at, Vector3.zero, 3.2f * scale, 0.07f, new Color(1f, 0.7f, 0.35f, 1f));
            for (int i = 0; i < 4; i++)
                Emit(_smoke, at + Rnd() * 0.4f * scale, Rnd() * 1.5f, UnityEngine.Random.Range(2.2f, 3.4f) * scale,
                     UnityEngine.Random.Range(1.6f, 2.4f), new Color(0.1f, 0.1f, 0.1f, 0.8f));
            for (int i = 0; i < 8; i++)
                Emit(_sparks, at, Rnd() * UnityEngine.Random.Range(10f, 22f), 0.06f,
                     UnityEngine.Random.Range(0.15f, 0.35f), new Color(1f, 0.65f, 0.3f, 1f));
        }
    }

    // =====================================================================
    // Network

    /// <summary>
    /// Two things on the wire. The spawn marker in Photon's cached
    /// instantiation event (byte key 5), exactly like the T-72, the Ural, the
    /// technical and the howitzer - so creator, remote players and late
    /// joiners all run the same rebuild. And one event channel: the turret
    /// pose with trigger and radar state ten times a second (unreliable), and
    /// a helicopter kill (reliable) every peer applies itself.
    /// </summary>
    public static class GepardNet
    {
        const string Marker = "NDR_GEPARD_V1";
        const int Pose = 1;
        const int HeliKill = 2;

        static bool _hooked, _failed;
        static MethodInfo _raise, _getView, _getViewId, _findView;
        static Type _optType;
        static readonly Dictionary<int, int> _viewIds = new Dictionary<int, int>();

        public static object[] SpawnData() { return new object[] { null, Marker }; }

        static bool IsGepardData(object value)
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
                    RevivalPlugin.L.LogWarning("Gepard network: NetworkingPeer missing.");
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
                    RevivalPlugin.L.LogWarning("Gepard network: DoInstantiate missing.");
                    return;
                }
                harmony.Patch(target, null,
                    new HarmonyMethod(typeof(GepardNet).GetMethod("Postfix")), null, null, null);
                RevivalPlugin.L.LogInfo("Gepard network: spawn marker active.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gepard network could not be enabled: " + ex);
            }
        }

        public static void Postfix(object __0, GameObject __result)
        {
            try
            {
                if (__result == null || Gepard.IstGepard(__result.transform)) return;
                System.Collections.IDictionary eventData = __0 as System.Collections.IDictionary;
                if (eventData == null || !eventData.Contains((byte)5)) return;
                if (!IsGepardData(eventData[(byte)5])) return;
                Gepard.Umbauen(__result);
                RevivalPlugin.L.LogInfo("Gepard network: built on this client: " + __result.name + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Gepard network, spawn marker: " + ex);
            }
        }

        static int Code() { return Gepard.CfgEventCode == null ? 193 : Gepard.CfgEventCode.Value; }

        /// <summary>The other channels' codes, as configured on this client.</summary>
        static bool Overlaps(int code)
        {
            int drone = RevivalPlugin.CfgDroneEventCode.Value;
            if (code >= drone && code <= drone + 4) return true;
            if (code == RevivalPlugin.CfgTurretEventCode.Value) return true;
            if (RevivalPlugin.CfgAdminEventCode != null && code == RevivalPlugin.CfgAdminEventCode.Value) return true;
            if (RevivalPlugin.CfgPatrolCrewDroneEventCode != null
                && code == RevivalPlugin.CfgPatrolCrewDroneEventCode.Value) return true;
            if (DroneGear.CfgSurvEventCode != null && code >= DroneGear.CfgSurvEventCode.Value
                && code <= DroneGear.CfgSurvEventCode.Value + 3) return true;
            if (PlayerHeli.CfgEventCode != null && code >= PlayerHeli.CfgEventCode.Value
                && code <= PlayerHeli.CfgEventCode.Value + 5) return true;
            if (RevivalTroopInsertion.CfgEventCode != null && code >= RevivalTroopInsertion.CfgEventCode.Value
                && code <= RevivalTroopInsertion.CfgEventCode.Value + 2) return true;
            return code == 164 || code == 190 || code == 191;   // crocodile, mortar, Stinger
        }

        internal static void EnsureHooked()
        {
            if (_hooked || _failed) return;
            try
            {
                int code = Code();
                if (code < 0 || code > 199 || Overlaps(code))
                    throw new Exception("event code " + code + " is outside 0..199 or overlaps another channel");
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                Type viewType = RevivalPlugin.TypeByName("PhotonView");
                Type ext = RevivalPlugin.TypeByName("Extensions");
                if (photon == null || viewType == null || ext == null)
                    throw new Exception("PhotonNetwork, PhotonView or Extensions missing");
                _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                FieldInfo onEvent = AccessTools.Field(photon, "OnEventCall");
                _optType = RevivalPlugin.TypeByName("RaiseEventOptions");
                _getView = AccessTools.Method(ext, "GetPhotonView", new Type[] { typeof(GameObject) }, null);
                _getViewId = AccessTools.PropertyGetter(viewType, "viewID");
                _findView = AccessTools.Method(viewType, "Find", new Type[] { typeof(int) }, null);
                if (_raise == null || onEvent == null || _getView == null || _getViewId == null || _findView == null)
                    throw new Exception("reflection path incomplete");
                MethodInfo mine = typeof(GepardNet).GetMethod("OnPhotonEvent", BindingFlags.Public | BindingFlags.Static);
                Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                onEvent.SetValue(null, Delegate.Combine(onEvent.GetValue(null) as Delegate, handler));
                _hooked = true;
                RevivalPlugin.L.LogInfo("Gepard network: event code " + code + " hooked.");
            }
            catch (Exception ex)
            {
                _failed = true;
                RevivalPlugin.L.LogError("Gepard network not hooked - the turret is not shown to "
                    + "other players: " + ex.Message);
            }
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

        static void Send(float[] data, bool reliable)
        {
            object opts = _optType == null ? null : Activator.CreateInstance(_optType);
            _raise.Invoke(null, new object[] { (byte)Code(), data, reliable, opts });
        }

        internal static void SendPose(Transform root, float yaw, float pitch, float track, bool firing, bool radar)
        {
            if (!_hooked || root == null) return;
            try
            {
                int view = ViewId(root);
                if (view <= 0) return;
                Send(new float[] { Pose, view, yaw, pitch, track, firing ? 1f : 0f, radar ? 1f : 0f }, false);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Gepard network send: " + ex.Message); }
        }

        internal static void SendHeliKill(int heliView, Vector3 point)
        {
            if (!_hooked) return;
            try { Send(new float[] { HeliKill, heliView, point.x, point.y, point.z }, true); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Gepard network send: " + ex.Message); }
        }

        public static void OnPhotonEvent(byte code, object content, int sender)
        {
            if (code != (byte)Code()) return;
            try
            {
                float[] f = content as float[];
                if (f == null || f.Length < 2) return;
                for (int i = 0; i < f.Length; i++)
                    if (float.IsNaN(f[i]) || float.IsInfinity(f[i])) return;
                int kind = Mathf.RoundToInt(f[0]);
                if (kind == HeliKill && f.Length >= 5)
                {
                    PlayerHeli.MissileImpact(Mathf.RoundToInt(f[1]), new Vector3(f[2], f[3], f[4]));
                    return;
                }
                if (kind != Pose || f.Length < 7) return;
                Component view = _findView.Invoke(null, new object[] { Mathf.RoundToInt(f[1]) }) as Component;
                if (view == null) return;
                Transform root = view.transform;
                Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
                if (vgsType != null)
                {
                    Component vgs = view.gameObject.GetComponentInParent(vgsType);
                    if (vgs != null) root = vgs.transform;
                }
                GepardRig rig = Gepard.Rig(root);
                if (rig == null || rig.LocalControl) return;
                rig.RemoteState(f[2], Mathf.Clamp(f[3], -10f, 90f), f[4], f[5] > 0.5f);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Gepard network receive: " + ex.Message);
            }
        }
    }

    // =====================================================================
    // Armour

    /// <summary>
    /// Explosions against a Gepard. The game removes damage x 9 from an
    /// armoured vehicle's hit points for every explosion (partType 14), so any
    /// mod explosion destroys it at once - right for an APC, not for this
    /// vehicle. The same re-balance VehicleArmor gives the T-72, with its own
    /// hit counts; a Stinger kill (VehicleArmor.MissileKill) is not one of the
    /// recognised values and stays a kill.
    /// </summary>
    public static class GepardArmor
    {
        const float LawDamage = 900f;   // RocketHook's LAW blast, as in VehicleArmor

        public static void Install(Harmony harmony)
        {
            try
            {
                Type vgs = RevivalPlugin.TypeByName("VehicleGameSystem");
                MethodInfo apply = vgs == null ? null : AccessTools.Method(vgs, "ApplyDamage",
                    new Type[] { typeof(float), typeof(int) }, null);
                if (apply == null)
                {
                    RevivalPlugin.L.LogWarning("Gepard armour: ApplyDamage(float,int) missing.");
                    return;
                }
                harmony.Patch(apply, new HarmonyMethod(typeof(GepardArmor).GetMethod("Prefix")),
                    null, null, null, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Gepard armour: " + ex); }
        }

        public static bool Prefix(object __instance, ref float __0, int __1)
        {
            try
            {
                if (__1 != 14) return true;
                Component vgs = __instance as Component;
                if (vgs == null || !Gepard.IstGepard(vgs.transform)) return true;
                int hits = Hits(__0);
                if (hits <= 0) return true;
                float pool = VehicleArmor.CfgPool == null ? 2000f : VehicleArmor.CfgPool.Value;
                if (pool <= 0f) return true;
                __0 = pool / hits / 9f;
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null) RevivalPlugin.L.LogError("Gepard armour: " + ex.Message);
            }
            return true;
        }

        static int Hits(float incoming)
        {
            if (Near(incoming, RevivalPlugin.CfgDroneDamage.Value)) return Mathf.Max(1, Gepard.CfgFpvHits.Value);
            if (Near(incoming, LawDamage)) return Mathf.Max(1, Gepard.CfgLawHits.Value);
            if (Near(incoming, RevivalPlugin.CfgTankExplosionDamage.Value)) return Mathf.Max(1, Gepard.CfgShellHits.Value);
            if (RevivalPlugin.CfgPatrolShellDamage != null && Near(incoming, RevivalPlugin.CfgPatrolShellDamage.Value))
                return Mathf.Max(1, Gepard.CfgShellHits.Value);
            return 0;
        }

        static bool Near(float a, float b) { return a > 0f && Mathf.Abs(a - b) < 0.5f; }
    }

    // =====================================================================
    // Player lines

    internal static class GepardText
    {
        internal static string Label() { return Loc.T("ЗСУ \"Гепард\"", "Gepard anti-aircraft gun"); }
        internal static string Spawned() { return Loc.T("\"Гепард\" поставлен перед вами.", "Gepard placed in front of you."); }
        internal static string Left() { return Loc.T("Орудие оставлено.", "Left the gun."); }
        internal static string SeatBusy() { return Loc.T("Место наводчика занято.", "The gunner's seat is taken."); }
        internal static string Destroyed() { return Loc.T("Машина уничтожена.", "The vehicle is destroyed."); }
        internal static string Controls()
        {
            return Loc.T("Наводчик: мышь - наводка, ЛКМ - огонь, R - радар, ПКМ - следующая цель, колесо - увеличение",
                         "Gunner: mouse lays, LMB fires, R radar, RMB next target, wheel zoom");
        }
        internal static string RadarOn() { return Loc.T("Радар: автосопровождение цели.", "Radar: tracking the target."); }
        internal static string RadarOnNothing() { return Loc.T("Радар включён - целей нет.", "Radar on - no targets."); }
        internal static string RadarOff() { return Loc.T("Радар выключен - ручная наводка.", "Radar off - manual laying."); }
        internal static string Lost() { return Loc.T("Цель потеряна.", "Target lost."); }
        internal static string Locked(string what) { return Loc.T("Захват: ", "Locked: ") + what; }
        internal static string HeliDown() { return Loc.T("Вертолёт сбит!", "Helicopter down!"); }
        internal static string NoAmmo(int id)
        {
            return Loc.T("Нет ленты (предмет " + id + ") для перезарядки.",
                         "No ammunition belt (item " + id + ") to reload.");
        }
        internal static string Loading() { return Loc.T("Перезарядка", "Reloading"); }
        internal static string ModeRadar() { return Loc.T("РАДАР - АВТО", "RADAR - AUTO"); }
        internal static string ModeManual() { return Loc.T("РУЧНАЯ НАВОДКА", "MANUAL"); }
        internal static string NoTarget() { return Loc.T("нет цели", "no target"); }
        internal static string OnTarget() { return Loc.T("НА ЦЕЛИ", "ON TARGET"); }
        internal static string Laying() { return Loc.T("НАВЕДЕНИЕ", "LAYING"); }
        internal static string OutOfReach() { return Loc.T("ВНЕ СЕКТОРА", "OUT OF REACH"); }
        internal static string Rounds() { return Loc.T("Снаряды", "Rounds"); }
        internal static string KindHeli() { return Loc.T("ВЕРТОЛЁТ", "HELICOPTER"); }
        internal static string KindTank() { return Loc.T("ТАНК", "TANK"); }
        internal static string KindVehicle() { return Loc.T("ТЕХНИКА", "VEHICLE"); }
        internal static string KindDrone() { return Loc.T("ДРОН", "DRONE"); }
        internal static string RightMouse() { return Loc.T("ПКМ", "RMB"); }
        internal static string Angles(int az, int el, int fov)
        {
            return Loc.T("Азимут " + az + "\u00b0  Угол " + el + "\u00b0  Поле " + fov + "\u00b0",
                         "Azimuth " + az + "\u00b0  Elevation " + el + "\u00b0  FOV " + fov + "\u00b0");
        }
        internal static string Keys(string radar, string next, string man)
        {
            return Loc.T("[" + radar + "] радар   [" + next + "] цель   [ЛКМ] огонь   [колесо] зум   [" + man + "] выйти",
                         "[" + radar + "] radar   [" + next + "] target   [LMB] fire   [wheel] zoom   [" + man + "] leave");
        }
    }
}
