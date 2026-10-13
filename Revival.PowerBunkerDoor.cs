// Power bunker 4: the heavy steel blast door at run time. Rules and model:
// Revival.PowerBunkerDoorCore.cs; report: docs/ai/tasks/power-bunker/pb-4-door.md.
//
//   BUILD   PowerNet.Bind hands over each bound bunker. A Sockets/BlastDoor
//           marker selects the pb-1 concrete bunker spec, Sockets/DoorHinge
//           the AF-GEN shed in the game today. The door is built once from
//           the Core parts table (frame, leaf, wheel, inner lever, dogs) with
//           the bunker's own vanilla kit materials (Bunker_Door sheet,
//           dk_metall, baremetal); AF-GEN's fixed-open leaf is cut out of its
//           combined mesh and its collider switched off.
//   USE     Like the breaker beside it: a 4 Hz reach scan, the key edge only
//           while in reach, [F] opens/closes (reverses a door in motion).
//   STATE   Master-authoritative: requests (FlakNet kind 35) are checked for
//           place and cooldown; the snapshot (kind 34) goes out on change and
//           every 5 s. Clients animate the same travel locally.
//   BLOCK   The leaf's box collider (kinematic body) stops players, NPCs and
//           bullets; while not passable a carving NavMeshObstacle closes the
//           opening for navigation. A local merc headed through asks to open.
//
// Per frame: the key edge in reach and, only while a door moves, its pose.
// C# 3.0 (csc from .NET 3.5). ASCII only; Loc.T keeps the Cyrillic.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal static class PowerBunkerDoor
    {
        internal const int StateMsg = 34, AskMsg = 35;
        const float K = PowerBunkerDoorCore.K;

        internal static ConfigEntry<bool> CfgEnabled, CfgStartOpen;
        internal static ConfigEntry<KeyCode> CfgKey;

        static readonly PowerSite[] Sites = PowerSitesData.Sites;
        static readonly DoorState[] States = Fresh();
        static readonly float[] LastAccepted = Filled(Sites.Length, -100f);
        static readonly float[] MercAskAt = Filled(Sites.Length, -100f);
        static readonly Door[] Doors = new Door[Sites.Length];
        static readonly float[] _state = new float[8], _ask = new float[8];
        static float _nextLocal, _nextBeat, _nextMerc;
        static bool _master, _moving, _pressed, _started;
        static int _near = -1, _promptKind = -1;
        static KeyCode _promptKey = KeyCode.None;
        static string _prompt, _promptText;

        sealed class Door
        {
            internal DoorSpec Spec;
            internal Transform Root, Leaf, Wheel, Lever;
            internal Transform[] Dogs;
            internal Vector3[] DogDirs;
            internal NavMeshObstacle Carve;
            internal float Posed = -1f;
        }

        static DoorState[] Fresh()
        {
            DoorState[] a = new DoorState[Sites.Length];
            for (int i = 0; i < a.Length; i++) a[i] = PowerBunkerDoorCore.Fresh();
            return a;
        }
        static float[] Filled(int n, float v) { float[] a = new float[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

        internal static void BindConfig(ConfigFile cfg)
        {
            const string G = "Power";
            CfgEnabled = cfg.Bind(G, "BlastDoor", true,
                "Generator bunker blast door: opens/closes with the wheel, blocks players, NPCs and bullets when shut; mercs open it on their way.");
            CfgStartOpen = cfg.Bind(G, "BlastDoorStartsOpen", false, "The master starts a session with the blast door open.");
            CfgKey = cfg.Bind(G, "BlastDoorKey", KeyCode.F, "Key pressed at the generator bunker's blast door.");
        }

        internal static bool On { get { return CfgEnabled == null || CfgEnabled.Value; } }

        /// <summary>Is the door of this site passable (true when no door is known)?</summary>
        internal static bool Passable(int s)
        {
            if (!On || s < 0 || s >= Sites.Length) return true;
            return PowerBunkerDoorCore.Passable(SpecFor(s), States[s].Progress);
        }

        static DoorSpec SpecFor(int s)
        {
            if (Doors[s] != null) return Doors[s].Spec;
            DoorSpec spec = PowerBunkerDoorCore.SpecById(Sites[s].Generator);
            return spec ?? PowerBunkerDoorCore.Specs[0];
        }

        static bool IsBound(int s) { return Doors[s] != null && Doors[s].Root != null; }

        // ------------------------------------------------------------ build

        /// <summary>PowerNet.Bind: a bunker of site s is in the scene.</summary>
        internal static void Bind(int s, Transform host)
        {
            if (!On || host == null || s < 0 || s >= Sites.Length) return;
            try
            {
                DoorSpec spec = host.Find("Sockets/BlastDoor") != null ? PowerBunkerDoorCore.SpecById("PB")
                    : host.Find("Sockets/DoorHinge") != null ? PowerBunkerDoorCore.SpecById("AF-GEN") : null;
                if (spec == null) { RevivalPlugin.L.LogInfo("PowerBunkerDoor: " + host.name + " has no door socket."); return; }
                Transform old = host.Find("PB4 BlastDoor");
                if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                int cut = spec.Id == "AF-GEN" ? CutStaticLeaf(host) : 0;
                Door d = Build(spec, host);
                Doors[s] = d;
                ApplyStart();
                Pose(s, true);
                RevivalPlugin.L.LogInfo("PowerBunkerDoor: " + Sites[s].Id + " door " + spec.Id + " built on " + host.name
                    + " (static leaf triangles cut " + cut + ", " + (States[s].Target ? "open" : "closed") + ").");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PowerBunkerDoor bind: " + ex.Message); }
        }

        /// <summary>The master's first word: closed unless configured open.</summary>
        static void ApplyStart()
        {
            if (_started || !Crocodile.IsMaster()) return;
            _started = true;
            if (CfgStartOpen == null || !CfgStartOpen.Value) return;
            for (int s = 0; s < Sites.Length; s++)
            {
                if (States[s].Revision != 0) continue;
                States[s].Target = true; States[s].Progress = 1f; States[s].Revision = 1;
            }
        }

        static Door Build(DoorSpec spec, Transform host)
        {
            Door d = new Door();
            d.Spec = spec;
            int layer = host.gameObject.layer;
            GameObject root = new GameObject("PB4 BlastDoor");
            root.layer = layer;
            root.transform.SetParent(host, false);
            root.transform.localPosition = new Vector3(spec.OriginX, spec.OriginY, spec.OriginZ) * K;
            root.transform.localRotation = Quaternion.Euler(0f, spec.Yaw, 0f);
            root.transform.localScale = Vector3.one * K;   // everything below is in metres
            d.Root = root.transform;

            Material[] mats = Materials(host);
            List<DoorPart> parts = PowerBunkerDoorCore.Parts(spec);
            List<Renderer> all = new List<Renderer>(8), near = new List<Renderer>(8);

            Renderer frame = MeshPart(root.transform, "Frame", Vector3.zero, parts, 0, mats, layer, null, Vector3.zero);
            if (frame != null) { all.Add(frame); near.Add(frame); }

            GameObject leaf = new GameObject("Leaf");
            leaf.layer = layer;
            leaf.tag = "Metal";
            leaf.transform.SetParent(root.transform, false);
            leaf.transform.localPosition = new Vector3(spec.HingeX, 0f, spec.HingeZ);
            d.Leaf = leaf.transform;
            Renderer leafR = MeshOn(leaf, parts, 1, mats);
            if (leafR != null) { all.Add(leafR); near.Add(leafR); }
            float cx = -spec.HingeX;   // the leaf is centred on the opening
            BoxCollider box = leaf.AddComponent<BoxCollider>();
            box.center = new Vector3(cx, spec.LeafY0 + spec.LeafH * 0.5f, spec.LeafZ0 + spec.LeafT * 0.5f - spec.HingeZ);
            box.size = new Vector3(spec.LeafW, spec.LeafH, spec.LeafT);
            Rigidbody body = leaf.AddComponent<Rigidbody>();
            body.isKinematic = true; body.useGravity = false;

            float hx, hy, zo, zi;
            PowerBunkerDoorCore.Hubs(spec, out hx, out hy, out zo, out zi);
            Renderer wheel = MeshPart(leaf.transform, "Wheel", new Vector3(hx, hy, zo), parts, 2, mats, layer, null, Vector3.zero);
            if (wheel != null) { d.Wheel = wheel.transform; all.Add(wheel); }
            Renderer lever = MeshPart(leaf.transform, "Lever", new Vector3(hx, hy, zi), parts, 4, mats, layer, null, Vector3.zero);
            if (lever != null) { d.Lever = lever.transform; all.Add(lever); }

            // dogs: one mesh per slide direction
            List<Vector3> dirs = new List<Vector3>(3);
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Group != 3) continue;
                float dx, dy;
                PowerBunkerDoorCore.DogDir(spec, parts[i], out dx, out dy);
                Vector3 v = new Vector3(dx, dy, 0f);
                if (!dirs.Contains(v)) dirs.Add(v);
            }
            d.Dogs = new Transform[dirs.Count];
            d.DogDirs = dirs.ToArray();
            for (int k = 0; k < dirs.Count; k++)
            {
                Renderer dog = MeshPart(leaf.transform, "Dogs" + k, Vector3.zero, parts, 3, mats, layer, spec, dirs[k]);
                if (dog != null) { d.Dogs[k] = dog.transform; all.Add(dog); }
            }

            // nav: the carve fills the opening and the wall depth behind it
            GameObject carve = new GameObject("Carve");
            carve.layer = layer;
            carve.transform.SetParent(root.transform, false);
            carve.transform.localPosition = new Vector3(0f, spec.OpenH * 0.5f, 0f);
            NavMeshObstacle ob = carve.AddComponent<NavMeshObstacle>();
            ob.shape = NavMeshObstacleShape.Box;
            ob.size = new Vector3(spec.OpenW + 0.2f, spec.OpenH, 0.8f);
            ob.carving = true; ob.carveOnlyStationary = true;
            d.Carve = ob;

            // LOD: the small moving parts drop first, then everything
            LODGroup lod = root.AddComponent<LODGroup>();
            lod.SetLODs(new LOD[] { new LOD(0.10f, all.ToArray()), new LOD(0.015f, near.ToArray()) });
            lod.RecalculateBounds();
            return d;
        }

        /// <summary>Kit materials from the host's own renderers: 0 door sheet,
        /// 1 painted steel, 2 bare steel (AF-GEN child names, else by the
        /// vanilla material name; the last fallback is a plain Standard).</summary>
        static Material[] Materials(Transform host)
        {
            Material[] m = new Material[3];
            m[0] = Find(host, "Equipment/door", "Bunker_Door");
            m[2] = Find(host, "Equipment/steel", "baremetal");
            m[1] = Find(host, "Equipment/paint", "dk_metall");
            if (m[1] == null) m[1] = m[2];
            if (m[2] == null) m[2] = m[1];
            if (m[0] == null) m[0] = m[1];
            for (int i = 0; i < 3; i++)
                if (m[i] == null)
                {
                    m[i] = new Material(Shader.Find("Standard"));
                    m[i].color = new Color(0.32f, 0.33f, 0.31f);
                }
            return m;
        }

        static Material Find(Transform host, string path, string vanilla)
        {
            Transform t = host.Find(path);
            Renderer r = t == null ? null : t.GetComponent<Renderer>();
            if (r != null && r.sharedMaterial != null) return r.sharedMaterial;
            Renderer[] rs = host.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                Material[] ms = rs[i].sharedMaterials;
                for (int j = 0; j < ms.Length; j++)
                    if (ms[j] != null && ms[j].name.StartsWith(vanilla, StringComparison.Ordinal)) return ms[j];
            }
            return null;
        }

        /// <summary>One moving piece; dogs (group 3) only of one slide direction.</summary>
        static Renderer MeshPart(Transform parent, string name, Vector3 at, List<DoorPart> parts, int group,
            Material[] mats, int layer, DoorSpec spec, Vector3 dogDir)
        {
            GameObject go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            if (group != 3) return MeshOn(go, parts, group, mats);
            List<DoorPart> pick = new List<DoorPart>(4);
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Group != 3) continue;
                float dx, dy;
                PowerBunkerDoorCore.DogDir(spec, parts[i], out dx, out dy);
                if (new Vector3(dx, dy, 0f) == dogDir) pick.Add(parts[i]);
            }
            return MeshOn(go, pick, 3, mats);
        }

        static Renderer MeshOn(GameObject go, List<DoorPart> parts, int group, Material[] mats)
        {
            DoorMesh b = new DoorMesh();
            for (int i = 0; i < parts.Count; i++) if (parts[i].Group == group) b.Add(parts[i]);
            Material[] used;
            Mesh mesh = b.Finish(go.name, mats, out used);
            if (mesh == null) return null;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = used;
            return r;
        }

        /// <summary>AF-GEN: cut the shipped fixed-open leaf and its handle out
        /// of the combined Equipment meshes and switch its collider off.</summary>
        static int CutStaticLeaf(Transform host)
        {
            int cut = 0;
            Transform eq = host.Find("Equipment");
            if (eq == null) return 0;
            Transform col = eq.Find("Door open leaf");
            if (col != null) col.gameObject.SetActive(false);
            string[] names = { "door", "steel" };
            for (int n = 0; n < names.Length; n++)
            {
                Transform t = eq.Find(names[n]);
                MeshFilter f = t == null ? null : t.GetComponent<MeshFilter>();
                if (f == null || f.sharedMesh == null || !f.sharedMesh.isReadable) continue;
                Mesh src = f.sharedMesh;
                Vector3[] v = src.vertices;
                Matrix4x4 toHost = host.worldToLocalMatrix * t.localToWorldMatrix;
                Mesh copy = null;
                for (int sub = 0; sub < src.subMeshCount; sub++)
                {
                    int[] tri = src.GetTriangles(sub);
                    List<int> keep = new List<int>(tri.Length);
                    for (int i = 0; i + 2 < tri.Length; i += 3)
                    {
                        Vector3 c = toHost.MultiplyPoint3x4((v[tri[i]] + v[tri[i + 1]] + v[tri[i + 2]]) / 3f) / K;
                        if (PowerBunkerDoorCore.StaticLeaf(c.x, c.y, c.z)) { cut++; continue; }
                        keep.Add(tri[i]); keep.Add(tri[i + 1]); keep.Add(tri[i + 2]);
                    }
                    if (keep.Count == tri.Length) continue;
                    if (copy == null) { copy = UnityEngine.Object.Instantiate(src); copy.name = src.name + " (door cut)"; }
                    copy.SetTriangles(keep, sub);
                }
                if (copy != null) f.sharedMesh = copy;
            }
            return cut;
        }

        // ------------------------------------------------------------ pose

        static void Pose(int s, bool force)
        {
            Door d = Doors[s];
            if (d == null || d.Root == null) return;
            float p = States[s].Progress;
            if (!force && p == d.Posed) return;
            d.Posed = p;
            DoorSpec spec = d.Spec;
            d.Leaf.localRotation = Quaternion.Euler(0f, PowerBunkerDoorCore.LeafYaw(spec, p), 0f);
            Quaternion turn = Quaternion.Euler(0f, 0f, PowerBunkerDoorCore.WheelDeg(p) * spec.HingeSign);
            if (d.Wheel != null) d.Wheel.localRotation = turn;
            if (d.Lever != null) d.Lever.localRotation = turn;
            float pull = PowerBunkerDoorCore.DogPull(p);
            for (int k = 0; k < d.Dogs.Length; k++)
                if (d.Dogs[k] != null) d.Dogs[k].localPosition = d.DogDirs[k] * pull;
            bool carve = PowerBunkerDoorCore.Carve(spec, p);
            if (d.Carve != null && d.Carve.enabled != carve) d.Carve.enabled = carve;
        }

        // ------------------------------------------------------------ network

        static void Send(int s)
        {
            if (!Crocodile.IsMaster()) return;
            float[] p = _state;
            p[0] = StateMsg; p[1] = s; p[2] = States[s].Target ? 1f : 0f; p[3] = States[s].Progress;
            p[4] = States[s].Revision; p[5] = p[6] = p[7] = 0f;
            FlakNet.SendPacket(p, true);
        }

        static void Ask(int s, bool open, int kind, Vector3 at)
        {
            if (Crocodile.IsMaster()) { OnAsk(s, open, kind, at, Crocodile.LocalActor()); return; }
            float[] p = _ask;
            p[0] = AskMsg; p[1] = s; p[2] = open ? 1f : 0f; p[3] = kind;
            p[4] = at.x; p[5] = at.y; p[6] = at.z; p[7] = 0f;
            FlakNet.SendPacket(p, true);
        }

        internal static void OnPacket(float[] f, int sender)
        {
            if (!On || f.Length < 8) return;
            int kind = Mathf.RoundToInt(f[0]);
            int s = Mathf.RoundToInt(f[1]);
            if (s < 0 || s >= Sites.Length) return;
            bool master = Crocodile.IsMaster();
            if (kind == StateMsg)
            {
                if (master || sender != MercAA.MasterActor()) return;
                bool was = States[s].Target;
                if (PowerBunkerDoorCore.Snapshot(ref States[s], f[2] > 0.5f, f[3], Mathf.RoundToInt(f[4])) && was != States[s].Target)
                    RevivalPlugin.L.LogInfo("PowerBunkerDoor: " + Sites[s].Id + " door " + (States[s].Target ? "opening" : "closing") + " (master).");
                _moving = true;
                Pose(s, false);
            }
            else if (kind == AskMsg && master)
                OnAsk(s, f[2] > 0.5f, Mathf.RoundToInt(f[3]), new Vector3(f[4], f[5], f[6]), sender);
        }

        /// <summary>Master: a player (checked by its own position) or a merc
        /// of that player's client (reported position) asks for the door.</summary>
        static void OnAsk(int s, bool open, int kind, Vector3 at, int actor)
        {
            if (s < 0 || s >= Sites.Length) return;
            GameObject player = Crocodile.PlayerByActor(actor);
            if (player == null) return;
            if (kind == PowerBunkerDoorCore.AskPlayer)
            {
                if (!Crocodile.PlayerUp(player)) return;
                at = player.transform.position;
            }
            else if (kind != PowerBunkerDoorCore.AskMerc || !open) return;
            Vector3 local;
            if (!Local(s, at, out local)) return;
            DoorSpec spec = SpecFor(s);
            if (!PowerBunkerDoorCore.InReach(spec, local.x, local.y, local.z, PowerBunkerDoorCore.MasterReachM)) return;
            if (!PowerBunkerDoorCore.Request(ref States[s], open, Time.time, ref LastAccepted[s])) return;
            RevivalPlugin.L.LogInfo("PowerBunkerDoor: " + Sites[s].Id + " door " + (open ? "opening" : "closing") + " by "
                + (kind == PowerBunkerDoorCore.AskMerc ? "a merc of " : "") + "actor " + actor + ".");
            _moving = true;
            Send(s);
        }

        /// <summary>World point -> door frame (metres); the master uses the
        /// declared site frame when its bunker scene is not loaded.</summary>
        static bool Local(int s, Vector3 world, out Vector3 local)
        {
            local = Vector3.zero;
            if (IsBound(s)) { local = Doors[s].Root.InverseTransformPoint(world); return true; }
            Vector3 origin; float yaw;
            if (!PowerNet.BunkerFrame(Sites[s].Id, out origin, out yaw)) return false;
            DoorSpec spec = SpecFor(s);
            Quaternion host = Quaternion.Euler(0f, yaw, 0f);
            Vector3 o = origin + host * (new Vector3(spec.OriginX, spec.OriginY, spec.OriginZ) * K);
            local = Quaternion.Inverse(host * Quaternion.Euler(0f, spec.Yaw, 0f)) * (world - o) / K;
            return true;
        }

        // ------------------------------------------------------------ tick

        internal static void Tick()
        {
            if (!On) return;
            try { TickInner(); }
            catch (Exception ex) { _moving = false; RevivalPlugin.L.LogWarning("PowerBunkerDoor: " + ex.Message); }
        }

        static void TickInner()
        {
            if (_near >= 0 && CfgKey != null && Input.GetKeyDown(CfgKey.Value)) _pressed = true;
            if (_moving) Animate(Time.deltaTime);
            if (Time.time < _nextLocal) return;
            _nextLocal = Time.time + 0.25f;
            bool master = Crocodile.IsMaster();
            if (master != _master) { _master = master; _nextBeat = 0f; if (master) ApplyStart(); }
            if (master && Time.time >= _nextBeat)
            {
                _nextBeat = Time.time + 5f;
                for (int s = 0; s < Sites.Length; s++) Send(s);
            }
            LocalPlayer();
            if (Time.time >= _nextMerc) { _nextMerc = Time.time + 0.5f; MercScan(); }
        }

        static void Animate(float dt)
        {
            bool any = false;
            for (int s = 0; s < Sites.Length; s++)
            {
                if (!PowerBunkerDoorCore.Step(ref States[s], dt)) continue;
                any = true;
                Pose(s, false);
            }
            _moving = any;
        }

        static void LocalPlayer()
        {
            GameObject me = MapTools.LocalPlayer();
            int near = -1;
            if (me != null && Crocodile.PlayerUp(me) && !GameUi.WindowOpen && !RadarScope.InView && Flak._manned == null
                && !PowerNet.Prompting)
            {
                Vector3 p = me.transform.position;
                for (int s = 0; s < Sites.Length && near < 0; s++)
                {
                    if (!IsBound(s)) continue;
                    if ((p - Doors[s].Root.position).sqrMagnitude > 30f * 30f) continue;
                    Vector3 l = Doors[s].Root.InverseTransformPoint(p);
                    if (PowerBunkerDoorCore.InReach(Doors[s].Spec, l.x, l.y, l.z, PowerBunkerDoorCore.ReachM)) near = s;
                }
            }
            _near = near;
            bool pressed = _pressed; _pressed = false;
            if (near < 0) { _prompt = null; return; }
            int kind = PowerBunkerDoorCore.PromptKind(States[near]);
            if (kind != _promptKind || CfgKey.Value != _promptKey || _promptText == null)
            {
                _promptKind = kind; _promptKey = CfgKey.Value;
                _promptText = "[" + CfgKey.Value + "] " + (kind == 0
                    ? Loc.T("Открыть гермодверь (повернуть штурвал)", "Open the blast door (turn the wheel)")
                    : Loc.T("Закрыть гермодверь", "Close the blast door"));
            }
            _prompt = _promptText;
            if (pressed) Ask(near, kind == 0, PowerBunkerDoorCore.AskPlayer, me.transform.position);
        }

        /// <summary>Owner side: a merc of this client at a closed door, on his
        /// way through, asks the master to open it (2 Hz, per door 1.5 s).</summary>
        static void MercScan()
        {
            if (!Mercs.Any) return;
            for (int s = 0; s < Sites.Length; s++)
            {
                if (!IsBound(s) || States[s].Target || Time.time - MercAskAt[s] < 1.5f) continue;
                Door d = Doors[s];
                if (PowerBunkerDoorCore.Passable(d.Spec, States[s].Progress)) continue;
                Vector3 o = d.Root.position;
                float reach = PowerBunkerDoorCore.MercApproachM * K + d.Spec.OpenW * 0.5f * K;
                foreach (MercUnit u in Mercs.AllUnits)
                {
                    if (u == null || u.Ai == null) continue;
                    Vector3 at = u.Ai.transform.position;
                    if ((at - o).sqrMagnitude > reach * reach) continue;
                    NavMeshAgent agent = CombatLoad.Agent(u.Ai);
                    if (agent == null) agent = u.Ai.GetComponent<NavMeshAgent>();
                    if (agent == null || !agent.enabled || !agent.isOnNavMesh) continue;
                    Vector3 l = d.Root.InverseTransformPoint(at);
                    float dx = Mathf.Max(0f, Mathf.Abs(l.x) - d.Spec.OpenW * 0.5f);
                    float dist = Mathf.Sqrt(dx * dx + l.z * l.z);
                    bool partial = agent.hasPath && agent.pathStatus == NavMeshPathStatus.PathPartial;
                    if (!PowerBunkerDoorCore.MercWantsPass(dist, PowerNet.InsideHouse(at), PowerNet.InsideHouse(agent.destination),
                        partial, false, States[s].Target)) continue;
                    MercAskAt[s] = Time.time;
                    Ask(s, true, PowerBunkerDoorCore.AskMerc, at);
                    break;
                }
            }
        }

        internal static void Draw()
        {
            if (_prompt == null) return;
            VanillaUi.Prompt(_prompt, Screen.height * 0.6f);
        }

        // ------------------------------------------------------------ mesh

        /// <summary>Box, cylinder and ring parts into one mesh with up to three
        /// submeshes (door sheet, painted steel, bare steel). Flat faces,
        /// kit-density box-projected UVs; the door sheet spans each face once.</summary>
        sealed class DoorMesh
        {
            static readonly float[] UnitsPerTile = { 5.902f, 0.825f, 2.082f };
            readonly List<Vector3> _v = new List<Vector3>(256), _n = new List<Vector3>(256);
            readonly List<Vector2> _uv = new List<Vector2>(256);
            readonly List<int>[] _t = { new List<int>(64), new List<int>(256), new List<int>(256) };

            internal void Add(DoorPart p)
            {
                if (p.Shape == 0) AddBox(p);
                else if (p.Shape == 1) AddCyl(p);
                else AddRing(p);
            }

            void Tri(int m, int a, int b, int c, Vector3 normal)
            {
                Vector3 g = Vector3.Cross(_v[b] - _v[a], _v[c] - _v[a]);
                if (Vector3.Dot(g, normal) < 0f) { int x = b; b = c; c = x; }
                _t[m].Add(a); _t[m].Add(b); _t[m].Add(c);
            }

            int V(Vector3 p, Vector3 n, Vector2 uv) { _v.Add(p); _n.Add(n); _uv.Add(uv); return _v.Count - 1; }

            void AddBox(DoorPart p)
            {
                Vector3 c = new Vector3(p.X, p.Y, p.Z), h = new Vector3(p.SX, p.SY, p.SZ) * 0.5f;
                for (int axis = 0; axis < 3; axis++)
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Vector3 n = Vector3.zero; n[axis] = side;
                        int ua = (axis + 1) % 3, va = (axis + 2) % 3;
                        int[] q = new int[4];
                        for (int k = 0; k < 4; k++)
                        {
                            float su = (k == 1 || k == 2) ? 1f : -1f, sv = k >= 2 ? 1f : -1f;
                            Vector3 o = c; o[axis] += side * h[axis]; o[ua] += su * h[ua]; o[va] += sv * h[va];
                            Vector2 uv;
                            if (p.Mat != 0) uv = new Vector2(o[ua], o[va]) * (K / UnitsPerTile[p.Mat]);
                            else if (axis == 2)   // the sheet reads unmirrored from either side
                                uv = new Vector2(side > 0 ? (c.x + h.x - o.x) / (2f * h.x) : (o.x - c.x + h.x) / (2f * h.x),
                                    (o.y - c.y + h.y) / (2f * h.y));
                            else uv = new Vector2((su + 1f) * 0.5f, (sv + 1f) * 0.5f);
                            q[k] = V(o, n, uv);
                        }
                        Tri(p.Mat, q[0], q[1], q[2], n);
                        Tri(p.Mat, q[0], q[2], q[3], n);
                    }
            }

            static Vector3 Ax(int axis, float a, float b, float along)
            {
                // a/b span the plane across the axis, along runs on it
                if (axis == 0) return new Vector3(along, a, b);
                if (axis == 1) return new Vector3(a, along, b);
                return new Vector3(a, b, along);
            }

            void AddCyl(DoorPart p)
            {
                int n = PowerBunkerDoorCore.CylSides;
                Vector3 c = new Vector3(p.X, p.Y, p.Z);
                float r = p.SX, half = p.SY * 0.5f, s = K / UnitsPerTile[p.Mat];
                Vector3 up = Ax(p.Axis, 0f, 0f, 1f);
                int[] lo = new int[n + 1], hi = new int[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    float a = i * Mathf.PI * 2f / n;
                    Vector3 radial = Ax(p.Axis, Mathf.Cos(a), Mathf.Sin(a), 0f);
                    lo[i] = V(c + radial * r - up * half, radial, new Vector2(a * r * s, -half * s));
                    hi[i] = V(c + radial * r + up * half, radial, new Vector2(a * r * s, half * s));
                }
                for (int i = 0; i < n; i++)
                {
                    Vector3 mid = (_n[lo[i]] + _n[lo[i + 1]]).normalized;
                    Tri(p.Mat, lo[i], lo[i + 1], hi[i + 1], mid);
                    Tri(p.Mat, lo[i], hi[i + 1], hi[i], mid);
                }
                for (int e = -1; e <= 1; e += 2)
                {
                    Vector3 cn = up * e;
                    int centre = V(c + up * (half * e), cn, Vector2.zero);
                    int[] ring = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        float a = i * Mathf.PI * 2f / n;
                        ring[i] = V(c + Ax(p.Axis, Mathf.Cos(a), Mathf.Sin(a), 0f) * r + up * (half * e), cn,
                            new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (r * s));
                    }
                    for (int i = 0; i < n; i++) Tri(p.Mat, centre, ring[i], ring[(i + 1) % n], cn);
                }
            }

            void AddRing(DoorPart p)
            {
                int seg = PowerBunkerDoorCore.RingSegments, sides = PowerBunkerDoorCore.RingSides;
                Vector3 c = new Vector3(p.X, p.Y, p.Z);
                float big = p.SX, tube = p.SY, s = K / UnitsPerTile[p.Mat];
                int row = sides + 1, start = _v.Count;
                for (int i = 0; i <= seg; i++)
                {
                    float a = i * Mathf.PI * 2f / seg;
                    Vector3 dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    for (int j = 0; j <= sides; j++)
                    {
                        float b = j * Mathf.PI * 2f / sides;
                        Vector3 n = dir * Mathf.Cos(b) + Vector3.forward * Mathf.Sin(b);
                        V(c + dir * big + n * tube, n, new Vector2(a * big * s, b * tube * s));
                    }
                }
                for (int i = 0; i < seg; i++)
                    for (int j = 0; j < sides; j++)
                    {
                        int a = start + i * row + j, b = a + row, cc = b + 1, d = a + 1;
                        Vector3 n = (_n[a] + _n[b] + _n[cc] + _n[d]).normalized;
                        Tri(p.Mat, a, b, cc, n);
                        Tri(p.Mat, a, cc, d, n);
                    }
            }

            internal Mesh Finish(string name, Material[] mats, out Material[] used)
            {
                used = null;
                if (_v.Count == 0) return null;
                List<int> subs = new List<int>(3);
                for (int m = 0; m < 3; m++) if (_t[m].Count > 0) subs.Add(m);
                Mesh mesh = new Mesh();
                mesh.name = "PB4 door " + name;
                mesh.SetVertices(_v);
                mesh.SetNormals(_n);
                mesh.SetUVs(0, _uv);
                mesh.subMeshCount = subs.Count;
                used = new Material[subs.Count];
                for (int i = 0; i < subs.Count; i++) { mesh.SetTriangles(_t[subs[i]], i); used[i] = mats[subs[i]]; }
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                return mesh;
            }
        }
    }
}
