// Next Day: Survival - Revival Toolkit
//
// THE DRIVABLE HOWITZER - the same Bohdana that stands in every settlement,
// turned into a vehicle a player can get into and drive away.
//
// WHAT THIS IS. The settlement gun (RevivalMortar.cs) raises the imported
// Bohdana as scenery: a model on the ground with a turret, a barrel and a fire
// control, and nothing underneath it that drives. This file takes THE SAME
// MODEL - ArtyModel.Build, byte for byte the meshes and the material the
// settlement gun uses, no second import, no copy - and stands it on a complete,
// drivable, networked vanilla vehicle, exactly the way the 15-seat Ural and the
// technical are built:
//
//   1. THE DONOR. "ural-375(mod)_spawn" (research/resource_paths.tsv), the
//      game's own 6x6 truck: Rigidbody, RCCCarControllerV2, PhotonView,
//      VehicleNetworkController, VehicleGameSystem, six wheel colliders, a
//      "Colliders" node, storage and a full LODGroup. Driving, steering,
//      suspension, collision, damage and networking all belong to that prefab
//      and are never touched here. It is the six-wheel chassis of the game, and
//      the Bohdana is a six-wheel gun truck - the two have the same shape.
//   2. THE BODY. The donor's renderers are switched off and the Bohdana model
//      is hung into the donor's own frame, fitted to the donor's OWN measured
//      box: scaled by the ratio of the two hull lengths and stood on the
//      donor's underside. No number out of the model appears in this file, so a
//      re-import that changes the model's size cannot move the vehicle.
//   3. THE GUN. The turret and the barrel that ArtyModel already builds are
//      handed to the fire control that already exists - Mortar.AttachMobile.
//      Walk up to the gun deck, press F to aim and R to load, exactly as at a
//      settlement gun, because it IS the settlement gun's fire control: the
//      same reach, the same dispersion, the same flight time, the same
//      crosshair that may not outrun the turret, the same 122 mm shell (2066).
//      Nothing about the fire mission is written twice.
//   4. SEATS. The donor's six places are cut to the three of the Ural's cab.
//      VehicleGameSystem::InitCar sizes Passengers from SeatPoints.childCount
//      and SitToPassengerPlace reads SeatPoints.GetChild(i) once, so the seat
//      table IS the child list - the fact the T-72, the Ural and the technical
//      already stand on. The three survivors keep the positions the game gave
//      them: the model is fitted to the donor's box, so the donor's cab and the
//      Bohdana's cab are the same place.
//   5. NAME. The instance is marked "_ARTY". The marker deliberately contains
//      neither "btr-80a" (VehicleArmor.IstApc, Turret.IsBtr) nor "_T72"
//      (Tank.IstPanzer), so the BTR gunner never attaches itself to it and the
//      armour policy of the APC and the tank does not apply. Its toughness is
//      one config number instead, capped DOWNWARDS on every scan.
//
// WHAT THE PLAYER GETS. A howitzer he can drive into position, get out of, walk
// to the gun deck and fire off the map - which is the whole point of a
// self-propelled gun and the one thing the settlement version cannot do.
//
// WHAT IS LOCAL AND WHAT IS NOT. The vehicle is a networked scene object: every
// client sees it, drives it and rides in it. The GUN is local, in exactly the
// same sense the settlement gun's is: every client lays its own copy of the
// turret, and what travels over the wire is the shot report (Mortar.Net) and
// the impact - never the turret bearing. That is the existing artillery design
// and not a shortcut taken here; a client that is shown somebody else's gun
// sees it swing round on the shot.
//
// WHAT IS NOT PROVEN WITHOUT THE GAME. Where the model sits on the donor, how
// the three cab seats line up inside the Bohdana's cab, and how the truck
// drives with a nine-metre gun on its back are in-game acceptance items. Every
// placement number is derived from the two measured boxes, and the derived
// values are logged once per instance so the log answers "why does it sit like
// that" without a debugger.
//
// KNOWN AND DELIBERATE. The Bohdana's six wheels are part of its hull mesh (the
// import bakes the native BTR wheel into it, arty_import.append_wheels), so they
// do not turn or steer. Separating them would need a new mesh out of the asset
// pipeline, which is a build-time change and not a runtime one; the donor's own
// wheels are hidden because two sets of wheels in the same place is worse.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. This file is ASCII ONLY and has no BOM: it is machine-written and
// build.ps1 requires BOM-less sources, so every player-facing string with real
// Cyrillic lives in the UTF-8 file RevivalUralTruck.cs (class ArtyVehicleText),
// the same split RevivalTechnical.cs and RevivalArtyBattery.cs use.
//
// SEAMS OUTSIDE THIS FILE (all marked there):
//   RevivalPlugin.cs      BindConfig / Install / Update(Tick) - three lines.
//   RevivalUralTruck.cs   VehicleRegistry entry "arty" + ArtyVehicleText.
//   RevivalMortar.cs      Mortar.AttachMobile / ReleaseMobile - the fire
//                         control seam, and Tube.Mobile beside it.
//   Revival.Admin.cs      one button, because every F-key is already taken.
//   Revival.Tank.cs       CarSpawn.SpawnPrefab - used unchanged.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The vehicle: the donor, the body swap, the three seats, the toughness
    /// cap, the gun station and a structural self-check. Everything that
    /// drives, collides, burns and synchronizes is the untouched donor prefab;
    /// everything that is a howitzer is <see cref="ArtyModel"/> and
    /// <see cref="Mortar"/>.
    /// </summary>
    public static class ArtyVehicle
    {
        /// <summary>Spawnable prefab name (research/resource_paths.tsv:
        /// vehiclespawn/ural-375(mod)_spawn). Lower case, as the resource path
        /// stores it. The same donor the 15-seat Ural uses, for the same
        /// reason: it is the game's only six-wheel truck.</summary>
        public const string Prefab = "ural-375(mod)_spawn";

        /// <summary>Marker appended to a rebuilt instance's name. Contains
        /// neither "btr-80a" nor "_T72" on purpose - see the file header.</summary>
        public const string Marke = "_ARTY";

        /// <summary>The Bohdana model's root under the donor. Named, because it
        /// is how the body is found again on a later scan and how the measuring
        /// and hiding passes tell our own geometry from the donor's.</summary>
        public const string BodyName = "NDR_ArtyBody";

        /// <summary>The two children ArtyModel.Build puts under that root.
        /// Named here so a rename of either is a compile-time question.</summary>
        public const string TurretName = "Turret";
        public const string BarrelName = "Barrel";

        /// <summary>The Ural cab seats three abreast, and the crew of a
        /// self-propelled gun rides in the cab. Everything behind the cab on
        /// this vehicle is gun, so there is nowhere else to sit.</summary>
        public const int SeatTotal = 3;

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<string> CfgKey;
        public static ConfigEntry<float> CfgDistance;
        public static ConfigEntry<float> CfgDurability;
        public static ConfigEntry<float> CfgFit;
        public static ConfigEntry<float> CfgLift;
        public static ConfigEntry<bool> CfgGun;

        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static bool _gunRefused;

        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("ArtyVehicle", "Enabled", true,
                "The drivable howitzer: register it as a vehicle kind, put the "
                + "imported Bohdana on a Ural chassis and hand its gun to the "
                + "artillery fire control. Off leaves the settlement gun "
                + "(section Mortar) exactly as it is.");
            CfgKey = cfg.Bind("ArtyVehicle", "Key", "None",
                "Key that puts a howitzer down in front of the player (admin "
                + "tool, master client only). NONE by default, because F4..F12 "
                + "are all taken - editor, recording, overlay, vehicle, admin, "
                + "tank, arena/Ural, patrol and technical. The admin menu has a "
                + "button for it instead; set a key here to have one.");
            CfgDistance = cfg.Bind("ArtyVehicle", "Distance", 12f,
                "Metres in front of the camera the spawned howitzer is put "
                + "down. Larger than the other vehicles' because this one is "
                + "longer and would otherwise be dropped on the player.");
            CfgDurability = cfg.Bind("ArtyVehicle", "Durability", 900f,
                "Hit points. CarSpawn.Prepare gives every mod vehicle 2000, "
                + "which is BTR armour: this is a soft-skinned gun truck and "
                + "does not deserve it, but it is a big heavy vehicle and is "
                + "not a VAZ-1111 (150) either. Capped DOWNWARDS on every scan, "
                + "never raised, so repeating it can never undo damage.");
            CfgFit = cfg.Bind("ArtyVehicle", "Fit", 1.0f,
                "Extra factor on the model's size. The base size is DERIVED: "
                + "the model's hull is scaled so its length matches the donor's "
                + "measured length. This factor is only for nudging the result "
                + "in game; 1.0 means the model is exactly as long as the truck "
                + "under it.");
            CfgLift = cfg.Bind("ArtyVehicle", "Lift", 0.0f,
                "Vertical nudge of the model, as a fraction of the donor's "
                + "measured height. 0 stands the model's lowest point on the "
                + "donor's lowest point, which is where its wheels belong. "
                + "May be negative.");
            CfgGun = cfg.Bind("ArtyVehicle", "Gun", true,
                "Hand the vehicle's turret to the artillery fire control, so a "
                + "player standing at the gun deck can aim it off the map (F) "
                + "and load it (R) exactly as at a settlement gun. Off leaves a "
                + "drivable howitzer whose gun is scenery. The section Mortar "
                + "must be enabled either way - it owns the fire mission.");
        }

        public static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        public static bool IstArty(Transform root)
        {
            if (root == null) return false;
            return root.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------- rebuild

        /// <summary>
        /// The whole rebuild. Idempotent: the local spawn and the network
        /// postfix can both reach a given instance, and a second call only
        /// re-applies the toughness cap. Never throws out - a failure leaves a
        /// plain, drivable Ural, which is a complete vehicle.
        /// </summary>
        public static void Umbauen(GameObject car)
        {
            if (car == null) return;

            // The STRUCTURE is built once; the cap is not. On the master client
            // the DoInstantiate postfix rebuilds the vehicle BEFORE
            // CarSpawn.Prepare writes its Durability 2000, so the registry's own
            // Rebuild - which runs right after Prepare - is the first chance to
            // take those 2000 away again. Capping only ever goes downwards, so
            // repeating it can never heal a damaged gun.
            bool fertig = IstArty(car.transform);
            if (!fertig)
            {
                try { Aufbauen(car); }
                catch (Exception ex) { RevivalPlugin.L.LogError("ArtyVehicle, rebuild: " + ex); }

                if (car.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) < 0)
                    car.name = car.name + Marke;
            }

            try { Deckel(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("ArtyVehicle, hit points: " + ex); }

            if (fertig) return;
            try { Validate(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("ArtyVehicle, self-check: " + ex); }
        }

        static void Aufbauen(GameObject car)
        {
            Component vgs;
            Transform seats = FindSeatPoints(car, out vgs);
            if (seats == null)
                RevivalPlugin.L.LogWarning("ArtyVehicle: SeatPoints missing - the "
                    + "donor keeps its own seat table.");
            else Sitze(car, vgs, seats);

            Vector3 min, max;
            if (!Masse(car, out min, out max))
            {
                RevivalPlugin.L.LogWarning("ArtyVehicle: nothing measurable on the "
                    + "donor - without a box the model cannot be fitted, so this "
                    + "stays a plain Ural.");
                return;
            }

            Karosse(car, seats, min, max);
        }

        /// <summary>
        /// Three seats: the vanilla cab places stay exactly where the game put
        /// them, everything behind them is removed. Removing is Tank.Sitze's
        /// pattern - SetParent first, because Destroy only takes effect at the
        /// end of the frame and childCount would still count the seat until then.
        /// </summary>
        static void Sitze(GameObject car, Component vgs, Transform seats)
        {
            int before = seats.childCount;

            List<Transform> places = new List<Transform>();
            for (int i = 0; i < seats.childCount; i++)
            {
                Transform c = seats.GetChild(i);
                if (c != null) places.Add(c);
            }
            for (int i = places.Count - 1; i >= SeatTotal; i--)
            {
                places[i].SetParent(null, false);
                UnityEngine.Object.Destroy(places[i].gameObject);
                places.RemoveAt(i);
            }

            // InitCar may have run already (local spawn) or may still be coming
            // (network rebuild). Sizing Passengers here is correct in the first
            // case and harmless in the second, where InitCar sizes it again from
            // the same child count.
            if (vgs != null)
            {
                FieldInfo fPass = AccessTools.Field(vgs.GetType(), "Passengers");
                if (fPass != null) fPass.SetValue(vgs, new GameObject[seats.childCount]);
            }

            RevivalPlugin.L.LogInfo("ArtyVehicle: seats " + before + " -> "
                + seats.childCount + " (the Ural cab), Passengers re-sized.");
        }

        /// <summary>
        /// Put the imported Bohdana in the donor's place.
        ///
        /// The model is fitted into the donor's OWN measured box: scaled by the
        /// ratio of the two hull lengths, centred in x and z, and stood on the
        /// donor's underside in y. That is why no number out of the model
        /// appears in this file - a hull that measures 27 units and one that
        /// measures 270 both land on the same truck.
        ///
        /// WHY THE LODGroup IS SWITCHED OFF. The group owns Renderer.enabled and
        /// rewrites it whenever the camera distance crosses a threshold, so a
        /// donor body that is merely disabled comes back a second later. With
        /// the group off, the flags set here are the last word. The price is
        /// that this one vehicle has no distance LODs; the alternative - a
        /// howitzer that turns back into a Ural at thirty metres - is worse.
        /// It is the same trade the technical makes.
        ///
        /// COLLISION IS UNTOUCHED. Only renderers are switched; the donor's
        /// "Colliders" node, its wheel colliders and its damage volumes stay
        /// exactly as they are, so the body swap can never make the vehicle
        /// undrivable or unhittable.
        /// </summary>
        static void Karosse(GameObject car, Transform seats, Vector3 min, Vector3 max)
        {
            Transform root = car.transform;
            // Share the frame the seats live in (the chassis), so the gun and
            // the crew move together under suspension and body animation.
            Transform parent = seats != null && seats.parent != null ? seats.parent : root;
            if (Body(root) != null) return;                  // already swapped

            Transform turret, barrel;
            GameObject modell = ArtyModel.Build(out turret, out barrel);
            if (modell == null)
            {
                RevivalPlugin.L.LogWarning("ArtyVehicle: the Bohdana model did not "
                    + "load (see ArtyModel) - this stays a plain Ural. Repair the "
                    + "client package; the art is required, not optional.");
                return;
            }
            modell.name = BodyName;

            MeshFilter hullFilter = modell.GetComponent<MeshFilter>();
            Mesh hull = hullFilter == null ? null : hullFilter.sharedMesh;
            if (hull == null)
            {
                UnityEngine.Object.Destroy(modell);
                RevivalPlugin.L.LogWarning("ArtyVehicle: the built model has no hull "
                    + "mesh to measure - this stays a plain Ural.");
                return;
            }

            int groups = LodAus(car);
            int hidden = Verstecken(car);

            // The HULL is what is fitted, not the whole model: the barrel sticks
            // out well past the bow at low elevation and the turret stands above
            // the roof, and neither of them is the size of the vehicle.
            Bounds b = hull.bounds;
            float modelLength = Mathf.Max(0.0001f, b.size.z);
            if (b.size.z < b.size.x)
                RevivalPlugin.L.LogWarning("ArtyVehicle: the hull mesh is not "
                    + "longest along Z (" + b.size + ") - the model is probably "
                    + "turned. That belongs in arty_import.py (BODY_FRAME), not here.");
            float donorLength = Mathf.Max(0.0001f, max.z - min.z);
            float donorHeight = Mathf.Max(0.0001f, max.y - min.y);
            float scale = donorLength / modelLength
                * Mathf.Clamp(CfgFit == null ? 1f : CfgFit.Value, 0.2f, 5f);

            modell.transform.SetParent(parent, false);
            modell.transform.rotation = root.rotation;
            modell.transform.localScale = new Vector3(scale, scale, scale);
            // Centre on the donor's box in x and z, and stand the model on the
            // donor's underside in y - which is where its own wheels are, since
            // the box was measured WITH the donor's wheels in it (see Masse).
            float lift = (CfgLift == null ? 0f : CfgLift.Value) * donorHeight;
            Vector3 wanted = new Vector3(
                (min.x + max.x) * 0.5f - b.center.x * scale,
                min.y - b.min.y * scale + lift,
                (min.z + max.z) * 0.5f - b.center.z * scale);
            modell.transform.position = root.TransformPoint(wanted);

            RevivalPlugin.L.LogInfo("ArtyVehicle: donor box x "
                + min.x.ToString("0.00") + ".." + max.x.ToString("0.00") + ", y "
                + min.y.ToString("0.00") + ".." + max.y.ToString("0.00") + ", z "
                + min.z.ToString("0.00") + ".." + max.z.ToString("0.00") + " (L "
                + donorLength.ToString("0.00") + ", H " + donorHeight.ToString("0.00")
                + "); hull mesh " + b.size + ", factor " + scale.ToString("0.0000")
                + "; " + hidden + " donor renderers hidden, " + groups
                + " LODGroups switched off. Turret "
                + (turret == null ? "MISSING" : turret.name) + ", barrel "
                + (barrel == null ? "MISSING" : barrel.name) + ".");
        }

        /// <summary>Switch off every LODGroup on the donor, so the renderer
        /// flags written next are the last word. Returns how many.</summary>
        static int LodAus(GameObject car)
        {
            int groups = 0;
            Type lodType = RevivalPlugin.TypeByName("UnityEngine.LODGroup");
            if (lodType == null) lodType = RevivalPlugin.TypeByName("LODGroup");
            if (lodType == null) return 0;
            Component[] all = car.GetComponentsInChildren(lodType, true);
            PropertyInfo pEnabled = lodType.GetProperty("enabled",
                BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || pEnabled == null || !pEnabled.CanWrite) continue;
                pEnabled.SetValue(all[i], false, null);
                groups++;
            }
            return groups;
        }

        /// <summary>Hide every donor renderer, WHEELS INCLUDED. The Bohdana
        /// brings its own six wheels in its hull mesh, and two sets of wheels in
        /// the same place is worse than one set that does not turn.</summary>
        static int Verstecken(GameObject car)
        {
            int hidden = 0;
            Renderer[] all = car.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || !r.enabled || IstUnser(r.transform)) continue;
                r.enabled = false;
                hidden++;
            }
            return hidden;
        }

        /// <summary>Is this transform part of the model this file added?</summary>
        static bool IstUnser(Transform t)
        {
            while (t != null)
            {
                if (t.name == BodyName) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// The donor's bounds in the vehicle root's LOCAL space, from the meshes
        /// themselves, WITH the wheels.
        ///
        /// That is the one difference from Technical.Karosserie, which leaves
        /// the wheels out because it is looking for a deck to stand a gun on.
        /// Here a WHOLE VEHICLE is being replaced by another whole vehicle that
        /// carries its own wheels, so the box that matters is the one that
        /// includes them: its underside is the ground line, and standing the
        /// model's tyres on the donor's tyres is exactly right. Measured
        /// without them the model would float by a wheel radius.
        ///
        /// Mesh-local corners are transformed through the root, so a rotated or
        /// scaled instance measures the same as one standing at the origin - a
        /// world AABB would not.
        /// </summary>
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
                if (mf == null || mf.sharedMesh == null) continue;
                if (IstUnser(mf.transform)) continue;

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

        // --------------------------------------------------------- hit points

        /// <summary>
        /// Cap the howitzer's hit points. Only ever downwards, so a damaged
        /// vehicle is never healed and the call is safe to repeat on every scan
        /// - which it has to be, because Durability is written by
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
            RevivalPlugin.L.LogInfo("ArtyVehicle: hit points " + have.ToString("0")
                + " -> " + cap.ToString("0") + " (capped).");
        }

        // ------------------------------------------------------- gun stations

        /// <summary>One rebuilt howitzer whose gun is known to the fire
        /// control. The handle is the opaque token Mortar handed back.</summary>
        sealed class Station
        {
            public GameObject Car;
            public Transform Body;
            public object Handle;
        }

        static readonly List<Station> _stations = new List<Station>();

        /// <summary>
        /// Every rebuilt howitzer in the scene gets its turret handed to the
        /// artillery fire control once, and every one that has left the scene
        /// gives it back. Runs on the shared vehicle scan a few times a second:
        /// finding a body means walking a vehicle hierarchy, which is exactly
        /// the kind of work that must not happen once per frame per vehicle.
        /// </summary>
        static void Geschuetze(Component[] all)
        {
            for (int i = _stations.Count - 1; i >= 0; i--)
            {
                Station s = _stations[i];
                if (s.Car != null && s.Body != null) continue;
                Mortar.ReleaseMobile(s.Handle);
                _stations.RemoveAt(i);
                RevivalPlugin.L.LogInfo("ArtyVehicle: a howitzer left the scene, its "
                    + "gun was handed back to the fire control.");
            }

            if (CfgGun != null && !CfgGun.Value) return;

            for (int i = 0; i < all.Length; i++)
            {
                Component vgs = all[i];
                if (vgs == null || !IstArty(vgs.transform)) continue;
                if (Bekannt(vgs.gameObject)) continue;

                Transform body = Body(vgs.transform);
                if (body == null) continue;
                Transform turret = body.Find(TurretName);
                Transform barrel = turret == null ? null : turret.Find(BarrelName);
                if (turret == null || barrel == null)
                {
                    RevivalPlugin.L.LogWarning("ArtyVehicle: \"" + vgs.name + "\" carries "
                        + "a body without a turret or a barrel - no gun station on it.");
                    continue;
                }

                object handle = Mortar.AttachMobile(body.gameObject, turret, barrel);
                if (handle == null)
                {
                    if (_gunRefused) continue;
                    _gunRefused = true;
                    RevivalPlugin.L.LogWarning("ArtyVehicle: the fire control refused "
                        + "the gun - the section Mortar is switched off. The vehicle "
                        + "drives, but its howitzer is scenery.");
                    continue;
                }
                Station s = new Station();
                s.Car = vgs.gameObject;
                s.Body = body;
                s.Handle = handle;
                _stations.Add(s);
                RevivalPlugin.L.LogInfo("ArtyVehicle: gun station on \"" + vgs.name
                    + "\" - aim and load it on foot, exactly as at a settlement gun.");
            }
        }

        static bool Bekannt(GameObject car)
        {
            for (int i = 0; i < _stations.Count; i++)
                if (_stations[i].Car == car) return true;
            return false;
        }

        /// <summary>The model root of a rebuilt howitzer, or null.</summary>
        internal static Transform Body(Transform root)
        {
            if (root == null) return null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == BodyName) return all[i];
            return null;
        }

        // ------------------------------------------------------------- check

        /// <summary>
        /// Structural self-check: the seat table, the sized Passengers array,
        /// the model with its turret and barrel, the capped hit points, and the
        /// donor's own networking, physics and registry entry. Logs a PASS/FAIL
        /// summary. Everything that needs eyes - how the model sits, where the
        /// crew ends up, how it drives - is an in-game acceptance item.
        /// </summary>
        public static bool Validate(GameObject car)
        {
            if (car == null)
            {
                RevivalPlugin.L.LogWarning("ArtyVehicle check: no vehicle.");
                return false;
            }

            List<string> fail = new List<string>();

            Component vgs;
            Transform seats = FindSeatPoints(car, out vgs);
            int seatN = seats == null ? 0 : seats.childCount;
            if (seats == null) fail.Add("SeatPoints missing");
            else if (seatN != SeatTotal)
                fail.Add("seat count " + seatN + " instead of " + SeatTotal);

            if (vgs == null) fail.Add("VehicleGameSystem missing");
            else
            {
                FieldInfo fPass = AccessTools.Field(vgs.GetType(), "Passengers");
                Array pass = fPass == null ? null : fPass.GetValue(vgs) as Array;
                if (pass == null) fail.Add("Passengers array missing");
                else if (pass.Length != seatN)
                    fail.Add("Passengers length " + pass.Length + " != seats " + seatN);

                FieldInfo fDur = AccessTools.Field(vgs.GetType(), "Durability");
                if (fDur != null && fDur.FieldType == typeof(float)
                    && CfgDurability != null
                    && (float)fDur.GetValue(vgs) > CfgDurability.Value + 0.5f)
                    fail.Add("hit points above the cap");
            }

            Transform body = Body(car.transform);
            if (body == null) fail.Add(BodyName + " missing");
            else
            {
                Transform turret = body.Find(TurretName);
                if (turret == null) fail.Add(TurretName + " missing");
                else if (turret.Find(BarrelName) == null) fail.Add(BarrelName + " missing");
            }

            if (!HasComponent(car, "PhotonView")) fail.Add("PhotonView missing (network)");
            if (!HasComponent(car, "VehicleNetworkController"))
                fail.Add("VehicleNetworkController missing (network)");
            if (!HasComponent(car, "RCCCarControllerV2"))
                fail.Add("RCCCarControllerV2 missing (driving physics)");

            if (!VehicleRegistry.Contains("arty")) fail.Add("not in VehicleRegistry");

            if (fail.Count == 0)
            {
                RevivalPlugin.L.LogInfo("ArtyVehicle check: PASS - " + seatN
                    + " seats, the Bohdana on the donor, turret and barrel in "
                    + "place, hit points capped, network/physics/registry present.");
                return true;
            }
            RevivalPlugin.L.LogWarning("ArtyVehicle check: FAIL - "
                + string.Join("; ", fail.ToArray()));
            return false;
        }

        // ------------------------------------------------------------ access

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
            if (t == null) return false;
            return car.GetComponentInChildren(t, true) != null;
        }

        // ----------------------------------------------------------- runtime

        public static void Install(Harmony harmony)
        {
            if (!Enabled)
            {
                RevivalPlugin.L.LogInfo("ArtyVehicle: off (ArtyVehicle/Enabled).");
                return;
            }
            VehicleRegistry.EnsureBuilt();
            ArtyVehicleNetwork.Install(harmony);
            RevivalPlugin.L.LogInfo("ArtyVehicle: drivable howitzer registered (key "
                + (CfgKey == null ? "None" : CfgKey.Value) + ", prefab " + Prefab
                + ", " + SeatTotal + " seats, "
                + (CfgDurability == null ? 0f : CfgDurability.Value).ToString("0")
                + " hit points).");
        }

        public static void Tick()
        {
            try
            {
                if (!Enabled) return;
                KeyCode key = Key();
                if (key != KeyCode.None && Input.GetKeyDown(key)) SpawnInFront();
                Pflege();
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("ArtyVehicle tick: " + ex); }
        }

        static float _nextCare;

        /// <summary>Everything that has to hold for EVERY howitzer in the scene
        /// and not only for the one the local player spawned: the hit-point cap
        /// and the gun stations.</summary>
        static void Pflege()
        {
            if (Time.time < _nextCare) return;
            _nextCare = Time.time + 0.2f;

            Component[] all = VehicleScan.All();
            for (int i = 0; i < all.Length; i++)
            {
                Component vgs = all[i];
                if (vgs == null || !IstArty(vgs.transform)) continue;
                Deckel(vgs);
            }
            Geschuetze(all);
        }

        /// <summary>The admin spawn. Returns the line the admin menu shows;
        /// the key path throws it away.</summary>
        internal static string SpawnInFront()
        {
            if (!Enabled) return "The howitzer is disabled in the configuration.";
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
            if (under == null)
            {
                RevivalPlugin.L.LogWarning("ArtyVehicle: no ground under " + above + ".");
                return "No ground found ahead of the player.";
            }

            Vector3 pos = ground + Vector3.up * 1.6f;
            Quaternion rot = Quaternion.LookRotation(ahead, Vector3.up);
            bool isTank;
            GameObject car = VehicleRegistry.Spawn("arty", pos, rot, out isTank);
            if (car == null) return "Howitzer spawn failed; see the game log.";
            RevivalPlugin.L.LogInfo("ArtyVehicle: howitzer created at " + pos
                + ", ground \"" + under.name + "\".");
            Turret.Hinweis(ArtyVehicleText.Spawned(), 4f);
            return ArtyVehicleText.Spawned();
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            string wanted = CfgKey == null ? "None" : CfgKey.Value;
            try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), wanted, true); }
            catch
            {
                _key = KeyCode.None;
                RevivalPlugin.L.LogWarning("ArtyVehicle: key " + wanted
                    + " unknown, no spawn key bound.");
            }
            return _key;
        }
    }

    /// <summary>
    /// Carries the howitzer decision in Photon's cached scene-instantiation
    /// event (byte key 5), exactly like TankNetwork, UralNetwork and
    /// TechnicalNetwork. Element zero stays null -
    /// VehicleGameSystem.FindMySpawnPointAndSet unboxes it as an Int32
    /// spawn-point id and returns early - and element one is free mod data that
    /// Photon caches and replays to late joiners, so creator, remote players and
    /// late joiners all run the same rebuild.
    /// </summary>
    public static class ArtyVehicleNetwork
    {
        const string Marker = "NDR_ARTYVEH_V1";

        public static object[] SpawnData()
        {
            return new object[] { null, Marker };
        }

        static bool IsArtyData(object value)
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
                    RevivalPlugin.L.LogWarning("ArtyVehicle network: NetworkingPeer missing.");
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
                    RevivalPlugin.L.LogWarning("ArtyVehicle network: DoInstantiate missing.");
                    return;
                }

                harmony.Patch(target, null,
                    new HarmonyMethod(typeof(ArtyVehicleNetwork).GetMethod("Postfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("ArtyVehicle network: spawn marker active.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("ArtyVehicle network could not be enabled: " + ex);
            }
        }

        public static void Postfix(object __0, GameObject __result)
        {
            try
            {
                if (__result == null || ArtyVehicle.IstArty(__result.transform)) return;
                System.Collections.IDictionary eventData = __0 as System.Collections.IDictionary;
                if (eventData == null || !eventData.Contains((byte)5)) return;
                if (!IsArtyData(eventData[(byte)5])) return;

                ArtyVehicle.Umbauen(__result);
                RevivalPlugin.L.LogInfo("ArtyVehicle network: howitzer built on this "
                    + "client: " + __result.name + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("ArtyVehicle network, spawn marker: " + ex);
            }
        }
    }
}
