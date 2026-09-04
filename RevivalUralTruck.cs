// Next Day: Survival - Revival Toolkit
//
// The 15-seat Ural cargo truck - the whole feature in one file.
//
// WHAT THIS IS. The game already ships a complete, drivable, networked 6x6
// Ural-375: the prefab "ural-375(mod)_Spawn" (research/dump_prefab.py, RE 32)
// carries a Rigidbody, RCCCarControllerV2, PhotonView, VehicleNetworkController,
// VehicleInventoryManager, VehicleGameSystem, VehicleCharacterController, six
// WheelCollider+RCCWheelCollider pairs under "Wheel Colliders", six wheel
// meshes under "Wheel Transforms", a "Colliders" node, cargo storage, and a
// full LODGroup. It is registered as the spawnable "ural-375(mod)_spawn". So
// this feature does NOT build a truck - it REUSES that one and does exactly one
// thing to it: extend the six-seat "Chassis/SeatPoints" node to fifteen usable,
// synchronized seats (one driver + fourteen passengers) by adding nine more
// passenger transforms in the cargo bed, and reset the game's Passengers array
// to the new seat count.
//
// WHY THAT IS ENOUGH (CONFIRMED, Revival.Tank.cs / dump_prefab). The seating
// system is data-driven off SeatPoints: VehicleGameSystem::InitCar sets
// Passengers = new GameObject[SeatPoints.childCount], and SitToPassengerPlace
// reads SeatPoints.GetChild(i) once to place each passenger (the exact facts the
// T-72 reconstruction in Revival.Tank.cs already relies on). Adding SeatPoints
// children therefore adds seats; the seat index is the child order and is stable
// because the children are added with stable names. Driving, steering, braking,
// six wheel colliders, animated wheel transforms, front-wheel steering,
// suspension, storage, collision, damage, PhotonView networking and the LODGroup
// all belong to the untouched prefab and are preserved by construction - nothing
// here removes, renames, or re-parents any of them.
//
// NETWORK SYNC. Photon transmits the prefab PATH, not a locally changed object
// tree, so the added seats must be rebuilt on every client. CarSpawn writes a
// marker into InstantiateSceneObject's data block exactly like the T-72;
// UralNetwork's DoInstantiate postfix reads that marker on the creator, other
// players and late joiners and runs the same Umbauen there. Occupancy itself is
// authoritative through the prefab's own VehicleGameSystem/VehicleNetwork
// Controller (the same plumbing that already synchronizes the six vanilla
// seats); this feature only widens the seat table those systems iterate.
//
// REGISTRY. VehicleRegistry is the authoritative list of drivable vehicle kinds
// (btr, tank, ural). The admin key, the auto-patrol and the one-way convoy all
// spawn THROUGH it, so the later patrol/convoy route editor (Workstream A) can
// enumerate VehicleRegistry.Names() to let an admin pick the Ural, and a convoy
// of Urals drives the recorded route with the integrated one-way behaviour
// unchanged (the Unit flags that make a convoy one-way are set by Patrol
// regardless of which prefab was spawned).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII-only comments and logs; player-facing strings go through Loc.T (real
// Cyrillic), so this file is UTF-8 (no BOM) and build.ps1 compiles it with
// /codepage:65001.
//
// SEAMS OUTSIDE THIS FILE (all clearly marked):
//   Revival.Tank.cs  CarSpawn.SpawnPrefab + InstantiateSceneObjectData - the
//                    generic "spawn an arbitrary vehicle prefab and rebuild it
//                    on every client" seam this feature and the registry use.
//   RevivalPlugin.cs BindConfig / Install / Update(Tick) - three one-line calls.
//   Revival.Patrol.cs / RevivalConvoy.cs - route the vehicle KIND through the
//                    registry so patrol/convoy AI can drive the Ural (documented
//                    as a known overlap with the convoy one-way work).

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The Ural reconstruction: extend the vanilla six-seat cargo truck to
    /// fifteen synchronized seats, plus a structural self-check. Everything else
    /// about the vehicle is the untouched game prefab.
    /// </summary>
    public static class UralTruck
    {
        /// <summary>Spawnable prefab name (research/resource_paths.tsv:
        /// vehiclespawn/ural-375(mod)_spawn). Lower case, as the resource path
        /// stores it.</summary>
        public const string Prefab = "ural-375(mod)_spawn";

        /// <summary>Marker appended to a rebuilt instance's name. Chosen so it
        /// does NOT contain "btr-80a" (VehicleArmor.IstApc) or "_T72"
        /// (Tank.IstPanzer): the Ural is a soft truck and must keep vanilla
        /// one-hit explosion behaviour, so it must be recognised as neither an
        /// APC nor a tank.</summary>
        public const string Marke = "_URAL15";

        public const int SeatTotal = 15;   // 1 driver + 14 passengers

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<string> CfgKey;
        public static ConfigEntry<float> CfgDistance;

        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;

        // The nine passenger seats added to the cargo bed. Positions are in
        // Chassis-local metres (SeatPoints sits at the chassis origin with no
        // rotation or scale, so these ARE vehicle-local metres). They stay
        // inside the measured flatbed envelope the six vanilla seats already
        // occupy - x in [-2.6, +2.6], z in [-9.0, -3.7], y = 2.14 - so every
        // added seat is as physically valid as Passenger2 (which vanilla places
        // at z -8.99, the tail extreme). Rotations copy the two vanilla bench
        // conventions (right bench faces inward-left, left bench faces
        // inward-right) and the centre bench faces forward.
        static readonly Quaternion RotRight = new Quaternion(0f, -0.702f, 0f, 0.712f);
        static readonly Quaternion RotLeft = new Quaternion(0f, -0.707f, 0f, -0.707f);
        static readonly Quaternion RotFwd = Quaternion.identity;

        struct Seat
        {
            public string Name;
            public Vector3 Pos;
            public Quaternion Rot;
            public Seat(string n, float x, float y, float z, Quaternion r)
            { Name = n; Pos = new Vector3(x, y, z); Rot = r; }
        }

        static Seat[] _added;

        static Seat[] Added()
        {
            if (_added != null) return _added;
            _added = new Seat[] {
                new Seat("Passenger6",   2.60f, 2.14f, -3.70f, RotRight),
                new Seat("Passenger7",   2.60f, 2.14f, -6.70f, RotRight),
                new Seat("Passenger8",  -2.50f, 2.14f, -3.70f, RotLeft),
                new Seat("Passenger9",  -2.50f, 2.14f, -6.70f, RotLeft),
                new Seat("Passenger10",  0.00f, 2.14f, -3.90f, RotFwd),
                new Seat("Passenger11",  0.00f, 2.14f, -5.20f, RotFwd),
                new Seat("Passenger12",  0.00f, 2.14f, -6.50f, RotFwd),
                new Seat("Passenger13",  0.00f, 2.14f, -7.80f, RotFwd),
                new Seat("Passenger14",  0.00f, 2.14f, -9.00f, RotFwd),
            };
            return _added;
        }

        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("UralTruck", "Enabled", true,
                "Den 15-Sitzer-Ural aktivieren: Registrierung als fahrbares "
                + "Fahrzeug und die Spawn-Taste unten.");
            CfgKey = cfg.Bind("UralTruck", "Key", "F10",
                "Taste, die einen 15-Sitzer-Ural vor dem Spieler absetzt "
                + "(Adminwerkzeug, nur Masterclient).");
            CfgDistance = cfg.Bind("UralTruck", "Distance", 8f,
                "Abstand des gespawnten Urals vor der Kamera in Metern.");
        }

        public static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        public static bool IstUral(Transform root)
        {
            if (root == null) return false;
            return root.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The whole reconstruction. Idempotent: the local spawn and the network
        /// postfix can both reach a given instance, and a second call is a no-op
        /// (the marker and the seat count guard it). Never throws out - a failure
        /// leaves the vehicle a plain six-seat Ural, which still drives.
        /// </summary>
        public static void Umbauen(GameObject car)
        {
            if (car == null) return;
            if (IstUral(car.transform)) return;   // already rebuilt on this client

            try { AddSeats(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Ural, Sitze: " + ex); }

            if (car.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) < 0)
                car.name = car.name + Marke;

            try { Validate(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Ural, Validierung: " + ex); }
        }

        static Transform FindSeatPoints(GameObject car, out Component vgs)
        {
            vgs = null;
            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (vgsType == null) return null;
            vgs = car.GetComponent(vgsType);
            if (vgs == null) return null;
            FieldInfo fSeats = AccessTools.Field(vgsType, "SeatPoints");
            Transform seats = fSeats == null ? null : fSeats.GetValue(vgs) as Transform;
            // Fallback: the confirmed prefab layout is Chassis/SeatPoints.
            if (seats == null)
            {
                Transform chassis = car.transform.Find("Chassis");
                if (chassis != null) seats = chassis.Find("SeatPoints");
            }
            return seats;
        }

        static void AddSeats(GameObject car)
        {
            Component vgs;
            Transform seats = FindSeatPoints(car, out vgs);
            if (seats == null)
            {
                RevivalPlugin.L.LogWarning("Ural: SeatPoints fehlt - der Ural "
                    + "bleibt bei sechs Sitzen.");
                return;
            }

            int before = seats.childCount;
            HashSet<string> have = new HashSet<string>();
            for (int i = 0; i < seats.childCount; i++) have.Add(seats.GetChild(i).name);

            Seat[] add = Added();
            int made = 0;
            for (int i = 0; i < add.Length; i++)
            {
                if (have.Contains(add[i].Name)) continue;   // idempotent
                GameObject go = new GameObject(add[i].Name);
                go.transform.SetParent(seats, false);
                go.transform.localPosition = add[i].Pos;
                go.transform.localRotation = add[i].Rot;
                go.transform.localScale = Vector3.one;
                have.Add(add[i].Name);
                made++;
            }

            // VehicleGameSystem::InitCar has already run by the time a spawned
            // vehicle reaches here, so its Passengers array was sized to the old
            // seat count. Resize it to the NEW child count - exactly what the
            // T-72 reconstruction does - while no one is seated yet.
            Type vgsType = vgs.GetType();
            FieldInfo fPass = AccessTools.Field(vgsType, "Passengers");
            if (fPass != null) fPass.SetValue(vgs, new GameObject[seats.childCount]);

            RevivalPlugin.L.LogInfo("Ural: Sitze " + before + " -> "
                + seats.childCount + " (" + made + " neue Bett-Plaetze), "
                + "Passengers neu gesetzt.");
        }

        /// <summary>
        /// Structural self-check: the fifteen seat transforms with unique names,
        /// the six wheel colliders, the networking and damage components, and the
        /// LODGroup for remote visibility. Logs a PASS/FAIL summary and returns
        /// true only when every structural requirement holds. Runtime concerns
        /// (a real 15th passenger actually seating and syncing, driving feel) are
        /// in-game acceptance items, not structural ones.
        /// </summary>
        public static bool Validate(GameObject car)
        {
            if (car == null) { RevivalPlugin.L.LogWarning("Ural-Check: kein Fahrzeug."); return false; }

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
            }

            if (vgs == null) fail.Add("VehicleGameSystem fehlt");
            else
            {
                FieldInfo fPass = AccessTools.Field(vgs.GetType(), "Passengers");
                Array pass = fPass == null ? null : fPass.GetValue(vgs) as Array;
                if (pass == null) fail.Add("Passengers-Array fehlt");
                else if (pass.Length != seatN)
                    fail.Add("Passengers-Laenge " + pass.Length + " != Sitze " + seatN);
            }

            int wheels = CountWheelColliders(car);
            if (wheels < 6) fail.Add("nur " + wheels + " WheelCollider (erwartet 6)");

            if (!HasComponent(car, "PhotonView")) fail.Add("PhotonView fehlt (Netz)");
            if (!HasComponent(car, "VehicleNetworkController"))
                fail.Add("VehicleNetworkController fehlt (Netz)");
            if (!HasComponent(car, "RCCCarControllerV2"))
                fail.Add("RCCCarControllerV2 fehlt (Fahrphysik)");
            if (!HasComponent(car, "UnityEngine.LODGroup") && !HasComponent(car, "LODGroup"))
                fail.Add("LODGroup fehlt (Fernsicht)");

            bool registered = VehicleRegistry.Contains("ural");
            if (!registered) fail.Add("nicht in VehicleRegistry");

            if (fail.Count == 0)
            {
                RevivalPlugin.L.LogInfo("Ural-Check: PASS - " + seatN + " Sitze, "
                    + wheels + " Radkollider, Netz/Physik/LOD/Registry vorhanden.");
                return true;
            }
            RevivalPlugin.L.LogWarning("Ural-Check: FAIL - " + string.Join("; ", fail.ToArray()));
            return false;
        }

        static int CountWheelColliders(GameObject car)
        {
            // WheelCollider/LODGroup are not in the UnityEngine subset the plugin
            // references (it resolves game types by name), so count by reflection.
            Type wc = RevivalPlugin.TypeByName("UnityEngine.WheelCollider");
            if (wc == null) wc = RevivalPlugin.TypeByName("WheelCollider");
            if (wc != null)
            {
                Component[] all = car.GetComponentsInChildren(wc, true);
                if (all != null && all.Length > 0) return all.Length;
            }
            // Fallback: count the transforms under the "Wheel Colliders" node,
            // the confirmed prefab layout (six RCCWheelCollider children).
            Transform node = car.transform.Find("Wheel Colliders");
            return node == null ? 0 : node.childCount;
        }

        static bool HasComponent(GameObject car, string typeName)
        {
            Type t = RevivalPlugin.TypeByName(typeName);
            if (t == null) return false;
            return car.GetComponentInChildren(t, true) != null;
        }

        // ------------------------------------------------------------- admin key

        public static void Install(Harmony harmony)
        {
            if (!Enabled) { RevivalPlugin.L.LogInfo("Ural: abgeschaltet (UralTruck/Enabled)."); return; }
            VehicleRegistry.EnsureBuilt();
            UralNetwork.Install(harmony);
            RevivalPlugin.L.LogInfo("Ural: 15-Sitzer registriert (Taste "
                + CfgKey.Value + ", Prefab " + Prefab + ").");
        }

        public static void Tick()
        {
            try
            {
                if (!Enabled) return;
                if (!Input.GetKeyDown(Key())) return;
                SpawnInFront();
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Ural-Tick: " + ex); }
        }

        static void SpawnInFront()
        {
            Camera cam = Camera.main;
            if (cam == null) { RevivalPlugin.L.LogWarning("Ural: keine Kamera."); return; }

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
                RevivalPlugin.L.LogWarning("Ural: unter " + above + " ist kein Boden.");
                return;
            }

            Vector3 pos = ground + Vector3.up * 1.6f;
            Quaternion rot = Quaternion.LookRotation(ahead, Vector3.up);
            bool isTank;
            GameObject car = VehicleRegistry.Spawn("ural", pos, rot, out isTank);
            if (car == null) return;   // registry/CarSpawn already logged why
            RevivalPlugin.L.LogInfo("Ural: 15-Sitzer erzeugt bei " + pos
                + ", Boden \"" + under.name + "\".");
            Turret.Hinweis(Loc.T("Урал (15 мест) создан", "Ural (15 seats) spawned"), 4f);
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), CfgKey.Value, true); }
            catch
            {
                _key = KeyCode.F10;
                RevivalPlugin.L.LogWarning("Ural: Taste " + CfgKey.Value
                    + " unbekannt, benutze F10.");
            }
            return _key;
        }
    }

    /// <summary>
    /// Carries the Ural-15 decision in Photon's cached scene-instantiation event
    /// (event key 5), exactly like TankNetwork does for the T-72. Element zero
    /// stays null (VehicleGameSystem reads it as an Int32 spawn-point id and
    /// returns early); the marker string in element one is independent mod data
    /// that Photon caches and replays to late joiners, and the DoInstantiate
    /// postfix rebuilds the fifteen seats wherever the prefab is instantiated.
    /// </summary>
    public static class UralNetwork
    {
        const string Marker = "NDR_URAL15_V1";

        public static object[] SpawnData()
        {
            return new object[] { null, Marker };
        }

        static bool IsUralData(object value)
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
                    RevivalPlugin.L.LogWarning("Ural-Netzwerk: NetworkingPeer fehlt.");
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
                    RevivalPlugin.L.LogWarning("Ural-Netzwerk: DoInstantiate fehlt.");
                    return;
                }

                harmony.Patch(target, null,
                    new HarmonyMethod(typeof(UralNetwork).GetMethod("Postfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("Ural-Netzwerk: Spawnmarker aktiv.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Ural-Netzwerk konnte nicht aktiviert werden: " + ex);
            }
        }

        public static void Postfix(object __0, GameObject __result)
        {
            try
            {
                if (__result == null || UralTruck.IstUral(__result.transform)) return;
                System.Collections.IDictionary eventData = __0 as System.Collections.IDictionary;
                if (eventData == null || !eventData.Contains((byte)5)) return;
                if (!IsUralData(eventData[(byte)5])) return;

                UralTruck.Umbauen(__result);
                RevivalPlugin.L.LogInfo("Ural-Netzwerk: 15-Sitzer auf diesem Client "
                    + "aufgebaut: " + __result.name + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Ural-Netzwerk, Spawnmarker: " + ex);
            }
        }
    }

    /// <summary>
    /// The authoritative list of drivable vehicle kinds. The admin key, the
    /// auto-patrol and the one-way convoy spawn through <see cref="Spawn"/>, and
    /// the later route/composition editor (Workstream A) enumerates
    /// <see cref="Names"/> to let an admin choose one. A "btr"/"tank" entry keeps
    /// the vanilla BTR-donor path in CarSpawn (unchanged); a "custom" entry (the
    /// Ural) carries its own prefab, its own network marker and its own rebuild,
    /// spawned through CarSpawn.SpawnPrefab. One-way convoy behaviour is
    /// independent of the prefab, so any registered kind can be driven as a
    /// one-way convoy without change.
    /// </summary>
    public static class VehicleRegistry
    {
        public sealed class Entry
        {
            public string Kind;
            public string Label;          // player-facing (Loc.T already applied)
            public string Prefab;         // null for the BTR-donor kinds
            public bool IsTank;
            public bool BtrDonor;         // true = use CarSpawn's existing bool path
            public Action<GameObject> Rebuild;
            public Func<object[]> NetData;
        }

        static readonly Dictionary<string, Entry> _by =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> _order = new List<string>();
        static bool _built;

        public static void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            Add(Make("btr", Loc.T("БТР-80А", "BTR-80A"), null, false, true, null, null));
            Add(Make("tank", Loc.T("Танк Т-72", "T-72 tank"), null, true, true, null, null));
            Add(Make("ural", Loc.T("Урал (15 мест)", "Ural (15 seats)"),
                UralTruck.Prefab, false, false, UralTruck.Umbauen, UralNetwork.SpawnData));
        }

        static Entry Make(string kind, string label, string prefab, bool isTank,
                          bool btrDonor, Action<GameObject> rebuild, Func<object[]> net)
        {
            Entry e = new Entry();
            e.Kind = kind; e.Label = label; e.Prefab = prefab; e.IsTank = isTank;
            e.BtrDonor = btrDonor; e.Rebuild = rebuild; e.NetData = net;
            return e;
        }

        static void Add(Entry e)
        {
            if (_by.ContainsKey(e.Kind)) return;
            _by[e.Kind] = e;
            _order.Add(e.Kind);
        }

        public static bool Contains(string kind)
        {
            EnsureBuilt();
            return kind != null && _by.ContainsKey(kind);
        }

        public static Entry Get(string kind)
        {
            EnsureBuilt();
            Entry e;
            if (kind != null && _by.TryGetValue(kind, out e)) return e;
            return null;
        }

        /// <summary>The kind keys, in registration order, for the route editor to
        /// present. Always contains at least btr, tank and ural.</summary>
        public static string[] Names()
        {
            EnsureBuilt();
            return _order.ToArray();
        }

        /// <summary>
        /// Spawn a registered vehicle kind at a place. Unknown kinds fall back to
        /// "btr". <paramref name="isTank"/> reports whether the result should be
        /// treated as a tank for damage/behaviour, so callers set Unit.Tank
        /// correctly without hard-coding the mapping. Returns null on refusal.
        /// </summary>
        public static GameObject Spawn(string kind, Vector3 pos, Quaternion rot,
                                       out bool isTank)
        {
            EnsureBuilt();
            Entry e = Get(kind);
            if (e == null) e = Get("btr");
            isTank = e.IsTank;

            if (e.BtrDonor)
                return CarSpawn.SpawnAt(pos, rot, e.IsTank);

            object[] net = e.NetData == null ? null : e.NetData();
            return CarSpawn.SpawnPrefab(e.Prefab, pos, rot, net, e.Rebuild);
        }
    }
}
