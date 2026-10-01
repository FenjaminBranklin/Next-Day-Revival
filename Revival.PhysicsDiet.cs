// Z P3b: dormant plugin ragdolls have hitboxes, but no per-bone physics actors.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class PhysicsDiet
    {
        sealed class Bone
        {
            internal GameObject Go;
            internal Rigidbody Body;
            internal float Mass, Drag, AngularDrag, MaxAngular, MaxDepenetration, SleepThreshold;
            internal bool Gravity, Detect;
            internal RigidbodyConstraints Constraints;
            internal RigidbodyInterpolation Interpolation;
            internal CollisionDetectionMode Collision;
            internal Vector3 Centre, Inertia;
            internal Quaternion InertiaRotation;
            internal int Solver, SolverVelocity;
            internal Bone(Rigidbody b)
            {
                Go = b.gameObject; Body = b; Mass = b.mass; Drag = b.drag;
                AngularDrag = b.angularDrag; Gravity = b.useGravity;
                Detect = b.detectCollisions; Constraints = b.constraints;
                Interpolation = b.interpolation; Collision = b.collisionDetectionMode;
                Centre = b.centerOfMass; Inertia = b.inertiaTensor;
                InertiaRotation = b.inertiaTensorRotation; MaxAngular = b.maxAngularVelocity;
                MaxDepenetration = b.maxDepenetrationVelocity;
                SleepThreshold = b.sleepThreshold; Solver = b.solverIterations;
                SolverVelocity = b.solverVelocityIterations;
            }
            internal void Create()
            {
                Body = Go.AddComponent<Rigidbody>(); Body.isKinematic = true;
                Body.mass = Mass; Body.drag = Drag; Body.angularDrag = AngularDrag;
                Body.useGravity = Gravity; Body.detectCollisions = Detect;
                Body.constraints = Constraints; Body.interpolation = Interpolation;
                Body.collisionDetectionMode = Collision; Body.centerOfMass = Centre;
                Body.inertiaTensor = Inertia; Body.inertiaTensorRotation = InertiaRotation;
                Body.maxAngularVelocity = MaxAngular; Body.sleepThreshold = SleepThreshold;
                Body.maxDepenetrationVelocity = MaxDepenetration;
                Body.solverIterations = Solver; Body.solverVelocityIterations = SolverVelocity;
                Body.Sleep(); Track(Body, 0, true);
            }
        }

        sealed class Link
        {
            internal CharacterJoint Joint;
            internal GameObject Go;
            internal int Connected;
            internal Rigidbody External;
            internal Vector3 Anchor, Axis, SwingAxis, ConnectedAnchor;
            internal SoftJointLimit Low, High, Swing1, Swing2;
            internal SoftJointLimitSpring TwistSpring, SwingSpring;
            internal float BreakForce, BreakTorque, MassScale, ConnectedMassScale;
            internal float ProjectionDistance, ProjectionAngle;
            internal bool AutoAnchor, Collision, Preprocessing, Projection;
            internal Link(CharacterJoint j, Bone[] bones)
            {
                Joint = j; Go = j.gameObject; Connected = -1; External = j.connectedBody;
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i].Body == External) { Connected = i; External = null; break; }
                Anchor = j.anchor; Axis = j.axis; SwingAxis = j.swingAxis;
                ConnectedAnchor = j.connectedAnchor; AutoAnchor = j.autoConfigureConnectedAnchor;
                Low = j.lowTwistLimit; High = j.highTwistLimit;
                Swing1 = j.swing1Limit; Swing2 = j.swing2Limit;
                TwistSpring = j.twistLimitSpring; SwingSpring = j.swingLimitSpring;
                BreakForce = j.breakForce; BreakTorque = j.breakTorque;
                Collision = j.enableCollision; Preprocessing = j.enablePreprocessing;
                MassScale = j.massScale; ConnectedMassScale = j.connectedMassScale;
                Projection = j.enableProjection; ProjectionDistance = j.projectionDistance;
                ProjectionAngle = j.projectionAngle;
            }
            internal void Create(Bone[] bones)
            {
                Joint = Go.AddComponent<CharacterJoint>();
                Joint.autoConfigureConnectedAnchor = false;
                Joint.connectedBody = Connected < 0 ? External : bones[Connected].Body;
                Joint.anchor = Anchor; Joint.axis = Axis; Joint.swingAxis = SwingAxis;
                Joint.connectedAnchor = ConnectedAnchor;
                Joint.autoConfigureConnectedAnchor = AutoAnchor;
                Joint.lowTwistLimit = Low; Joint.highTwistLimit = High;
                Joint.swing1Limit = Swing1; Joint.swing2Limit = Swing2;
                Joint.twistLimitSpring = TwistSpring; Joint.swingLimitSpring = SwingSpring;
                Joint.breakForce = BreakForce; Joint.breakTorque = BreakTorque;
                Joint.enableCollision = Collision; Joint.enablePreprocessing = Preprocessing;
                Joint.massScale = MassScale; Joint.connectedMassScale = ConnectedMassScale;
                Joint.enableProjection = Projection; Joint.projectionDistance = ProjectionDistance;
                Joint.projectionAngle = ProjectionAngle;
            }
        }

        sealed class Doll
        {
            internal Component Controller;
            internal Bone[] Bones;
            internal Link[] Links;
            internal Rigidbody[] Native, Tracked;
            internal Component[] Parts;
            internal int[] PartBones, TrackBones;
            internal float Next;
            internal bool Stored;
            internal int Id;
            internal int[] PartIds;
        }
        static readonly List<Doll> _dolls = new List<Doll>(512);
        static readonly Dictionary<int, Doll> _controllers = new Dictionary<int, Doll>(512);
        static readonly Dictionary<int, Doll> _parts = new Dictionary<int, Doll>(8192);
        static FieldInfo _native, _state, _partBody;
        static Type _controllerType, _partType;
        static int _cursor, _stored, _saved, _restores;
        static bool _ready;

        internal static void Install(Harmony h)
        {
            _controllerType = RevivalPlugin.TypeByName("PlayerRagdollController");
            _partType = RevivalPlugin.TypeByName("DamagedPart");
            _native = _controllerType == null ? null : AccessTools.Field(_controllerType, "RigidBodies");
            _state = _controllerType == null ? null : AccessTools.Field(_controllerType, "_RagdollState");
            _partBody = _partType == null ? null : AccessTools.Field(_partType, "body");
            MethodInfo active = _controllerType == null ? null : AccessTools.Method(_controllerType, "SetRagdollActive", new Type[] { typeof(bool) }, null);
            MethodInfo force = _partType == null ? null : AccessTools.Method(_partType, "ApplyForce", null, null);
            if (_native != null && _state != null && _partBody != null && active != null && force != null)
            {
                h.Patch(active, new HarmonyMethod(typeof(PhysicsDiet).GetMethod("BeforeRagdoll")), null, null, null, null);
                h.Patch(force, new HarmonyMethod(typeof(PhysicsDiet).GetMethod("BeforeForce")), null, null, null, null);
                _ready = true;
            }
            Feed(h, "NPC_AI2", "NpcSpawned");
            Feed(h, "VehicleGameSystem", "VehicleSpawned");
            Feed(h, "ItemSpawned", "ItemSpawned");
            SceneManager.sceneLoaded += Loaded;
            for (int i = 0; i < SceneManager.sceneCount; i++) Seed(SceneManager.GetSceneAt(i));
        }

        internal static void RegisterNpc(Component ai, Rigidbody[] tracked)
        {
            Track(tracked, 0);
            if (!_ready || ai == null) return;
            // The retained root actor carries the animated compound hitboxes.
            // Without it, trimming would turn moving bones into static shapes;
            // disabled root collision detection would make the NPC unhittable.
            Rigidbody root = ai.GetComponent<Rigidbody>();
            if (root == null || !root.isKinematic || !root.detectCollisions) return;
            Component c = ai.GetComponent(_controllerType);
            if (c == null) c = ai.GetComponentInChildren(_controllerType);
            if (c == null || _controllers.ContainsKey(c.GetInstanceID())) return;
            Rigidbody[] native = _native.GetValue(c) as Rigidbody[];
            if (native == null || native.Length == 0) return;
            // Native caches have finished by NPC Start. Delay trimming until all
            // DamagedPart.Start methods and the first visualization pass ran.
            Doll d = new Doll(); d.Controller = c; d.Native = native; d.Tracked = tracked; d.Id = c.GetInstanceID();
            d.Next = Time.unscaledTime + 2f;
            _dolls.Add(d); _controllers[c.GetInstanceID()] = d;
        }

        static bool Capture(Doll d)
        {
            List<Bone> bones = new List<Bone>();
            for (int i = 0; i < d.Native.Length; i++)
            {
                Rigidbody b = d.Native[i];
                if (b == null || !b.isKinematic || b.transform == d.Controller.transform
                    || !b.transform.IsChildOf(d.Controller.transform)) return false;
                bones.Add(new Bone(b));
            }
            d.Bones = bones.ToArray();
            // Only the known native CharacterJoint layout is supported. Never
            // delete a body with an unmodelled joint or an external connection.
            Joint[] joints = d.Controller.GetComponentsInChildren<Joint>(true);
            List<Link> links = new List<Link>();
            for (int i = 0; i < joints.Length; i++)
            {
                CharacterJoint j = joints[i] as CharacterJoint;
                if (j == null || Index(d.Bones, j.GetComponent<Rigidbody>()) < 0) return false;
                if (j.connectedBody != null && Index(d.Bones, j.connectedBody) < 0) return false;
                links.Add(new Link(j, d.Bones));
            }
            d.Links = links.ToArray();
            d.Parts = d.Controller.GetComponentsInChildren(_partType, true);
            d.PartBones = new int[d.Parts.Length];
            d.PartIds = new int[d.Parts.Length];
            for (int i = 0; i < d.Parts.Length; i++)
            {
                d.PartBones[i] = Index(d.Bones, _partBody.GetValue(d.Parts[i]) as Rigidbody);
                d.PartIds[i] = d.Parts[i].GetInstanceID();
                if (d.PartBones[i] < 0) return false; // preserve unknown damage/body wiring
            }
            d.TrackBones = new int[d.Tracked.Length];
            for (int i = 0; i < d.Tracked.Length; i++) d.TrackBones[i] = Index(d.Bones, d.Tracked[i]);
            for (int i = 0; i < d.Parts.Length; i++) _parts[d.Parts[i].GetInstanceID()] = d;
            return true;
        }

        static int Index(Bone[] bones, Rigidbody b)
        {
            if (b == null) return -1;
            for (int i = 0; i < bones.Length; i++) if (bones[i].Body == b) return i;
            return -1;
        }

        static void Store(Doll d)
        {
            if (d.Stored || FastField.GetInt(_state, d.Controller) != 0) return;
            if (d.Bones == null && !Capture(d)) { d.Next = float.PositiveInfinity; return; }
            for (int i = 0; i < d.Bones.Length; i++)
                if (d.Bones[i].Body == null || !d.Bones[i].Body.isKinematic) return;
            // Complete the topology transaction synchronously. Deferred Destroy
            // can delete a body that a same-frame death just tried to restore.
            // Runtime instances only; colliders/GameObjects are never destroyed.
            for (int i = 0; i < d.Links.Length; i++) UnityEngine.Object.DestroyImmediate(d.Links[i].Joint);
            for (int i = 0; i < d.Bones.Length; i++) UnityEngine.Object.DestroyImmediate(d.Bones[i].Body);
            for (int i = 0; i < d.Native.Length; i++) d.Native[i] = null;
            for (int i = 0; i < d.Tracked.Length; i++) if (d.TrackBones[i] >= 0) d.Tracked[i] = null;
            for (int i = 0; i < d.Parts.Length; i++) _partBody.SetValue(d.Parts[i], null);
            d.Stored = true; _stored++; _saved += d.Bones.Length;
        }

        static void Restore(Doll d)
        {
            if (!d.Stored || d.Controller == null) return;
            // Bodies first, then joints, then every native/cache reference.
            for (int i = 0; i < d.Bones.Length; i++) d.Bones[i].Create();
            for (int i = 0; i < d.Links.Length; i++) d.Links[i].Create(d.Bones);
            for (int i = 0; i < d.Native.Length; i++) d.Native[i] = d.Bones[i].Body;
            for (int i = 0; i < d.Tracked.Length; i++)
                if (d.TrackBones[i] >= 0) d.Tracked[i] = d.Bones[d.TrackBones[i]].Body;
            for (int i = 0; i < d.Parts.Length; i++)
                if (d.Parts[i] != null) _partBody.SetValue(d.Parts[i], d.Bones[d.PartBones[i]].Body);
            d.Stored = false; _stored--; _saved -= d.Bones.Length; _restores++;
            d.Next = Time.unscaledTime + 2f;
        }

        public static void BeforeRagdoll(object __instance, bool __0)
        {
            Component c = __instance as Component; Doll d;
            if (__0 && c != null && _controllers.TryGetValue(c.GetInstanceID(), out d)) Restore(d);
        }
        public static void BeforeForce(object __instance)
        {
            Component c = __instance as Component; Doll d;
            if (c != null && _parts.TryGetValue(c.GetInstanceID(), out d)) Restore(d);
        }
        static void Feed(Harmony h, string typeName, string hook)
        {
            Type t = RevivalPlugin.TypeByName(typeName);
            MethodInfo m = t == null ? null : AccessTools.Method(t, "Start", Type.EmptyTypes, null);
            if (m != null) h.Patch(m, null, new HarmonyMethod(typeof(PhysicsDiet).GetMethod(hook)), null, null, null);
        }
        public static void NpcSpawned(object __instance) { Enqueue(__instance, 0); }
        public static void VehicleSpawned(object __instance) { Enqueue(__instance, 7); }
        public static void ItemSpawned(object __instance) { Enqueue(__instance, 6); }
        static void Enqueue(object instance, int category)
        {
            Component c = instance as Component;
            if (c != null) _walk.Push(new Visit(c.transform, category));
        }

        // Census: one cold hierarchy walk per scene load, plus known spawn feeds.
        // No recurring FindObjectsOfType/GetComponents/scene array allocation.
        sealed class Entry { internal Rigidbody Body; internal int Id, Category; internal bool Dynamic; internal float Next; }
        static readonly List<Entry> _bodies = new List<Entry>(4096);
        static readonly Dictionary<int, Entry> _ids = new Dictionary<int, Entry>(4096);
        struct Visit
        {
            internal Transform Node;
            internal int Category;
            internal Visit(Transform node, int category) { Node = node; Category = category; }
        }
        static readonly Stack<Visit> _walk = new Stack<Visit>(4096);
        static readonly int[] _counts = new int[9];
        static readonly string[] _names = { "npc", "af", "town", "bag", "crate", "wreck", "prop", "vehicle", "other" };
        static int _bodyCursor, _dynamic, _staticRemoved;
        static void Loaded(Scene s, LoadSceneMode mode) { Seed(s); }
        static void Seed(Scene s)
        {
            if (!s.isLoaded) return;
            GameObject[] roots = s.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++) _walk.Push(new Visit(roots[i].transform, -1));
        }
        internal static void Track(Rigidbody[] bodies, int category)
        { for (int i = 0; i < bodies.Length; i++) Track(bodies[i], category, true); }
        static void Track(Rigidbody b, int category, bool creator)
        {
            if (b == null) return;
            Entry known;
            if (_ids.TryGetValue(b.GetInstanceID(), out known))
            {
                if (known.Category != category && (creator || known.Category == 8))
                { _counts[known.Category]--; known.Category = category; _counts[category]++; }
                return;
            }
            Entry e = new Entry(); e.Body = b; e.Id = b.GetInstanceID(); e.Category = category;
            e.Dynamic = b.gameObject.activeInHierarchy && !b.isKinematic && !b.IsSleeping();
            _ids[e.Id] = e; _bodies.Add(e); _counts[category]++; if (e.Dynamic) _dynamic++;
        }
        static int Category(Rigidbody b)
        {
            if (b.CompareTag("RagdollBone")) return 0;
            string name = b.name.ToLowerInvariant();
            if (name.IndexOf("sandbag") >= 0) return 3;
            if (name.IndexOf("crate") >= 0 || name.IndexOf("box_wood") >= 0) return 4;
            if (name.IndexOf("wreck") >= 0) return 5;
            string scene = b.gameObject.scene.name;
            if (scene.StartsWith("EastAf", StringComparison.Ordinal)) return 1;
            if (scene.StartsWith("EastMt", StringComparison.Ordinal) || scene.StartsWith("EastTown", StringComparison.Ordinal)) return 2;
            return 8;
        }
        static bool PlainStatic(Rigidbody b)
        {
            string s = b.gameObject.scene.name;
            if (!(s.StartsWith("EastAf", StringComparison.Ordinal) || s.StartsWith("EastMt", StringComparison.Ordinal)
                || s.StartsWith("EastTown", StringComparison.Ordinal))) return false;
            if (!b.isKinematic || !b.gameObject.isStatic) return false;
            // Scripted/destructible/animated/networked content always keeps its
            // actor. This whitelist is deliberately stricter than name matching.
            Component[] all = b.transform.root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i];
                Collider collider = c as Collider;
                if (collider != null && collider.isTrigger) return false;
                if (c == null || c is Transform || c is Renderer || c is MeshFilter || c is Collider
                    || c is Rigidbody || c is LODGroup || c is UnityEngine.AI.NavMeshObstacle) continue;
                return false;
            }
            return true;
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20000; // 0.05 ms cooperative census
            for (int i = 0; i < 12 && _bodies.Count > 0; i++)
            {
                if (_bodyCursor >= _bodies.Count) _bodyCursor = 0;
                Entry e = _bodies[_bodyCursor]; Rigidbody b = e.Body;
                if (b == null)
                {
                    _counts[e.Category]--; if (e.Dynamic) _dynamic--;
                    _ids.Remove(e.Id);
                    _bodies[_bodyCursor] = _bodies[_bodies.Count - 1]; _bodies.RemoveAt(_bodies.Count - 1);
                }
                else
                {
                    _bodyCursor++;
                    if (now >= e.Next)
                    {
                        e.Next = now + 0.5f;
                        bool active = b.gameObject.activeInHierarchy && !b.isKinematic && !b.IsSleeping();
                        if (active != e.Dynamic) { _dynamic += active ? 1 : -1; e.Dynamic = active; }
                    }
                }
                if (Stopwatch.GetTimestamp() >= until) break;
            }
            if (_dolls.Count > 0)
            {
                if (_cursor >= _dolls.Count) _cursor = 0;
                Doll d = _dolls[_cursor++];
                if (d.Controller == null)
                {
                    if (d.Stored) { _stored--; _saved -= d.Bones.Length; }
                    if (d.PartIds != null) for (int i = 0; i < d.PartIds.Length; i++) _parts.Remove(d.PartIds[i]);
                    _controllers.Remove(d.Id);
                    _dolls.RemoveAt(--_cursor);
                }
                else if (now >= d.Next)
                {
                    d.Next = now + 0.5f; Store(d); // at most one cold ragdoll per frame
                }
            }
            for (int n = 0; n < 12 && _walk.Count > 0 && Stopwatch.GetTimestamp() < until; n++)
            {
                Visit visit = _walk.Pop(); Transform t = visit.Node;
                if (t != null)
                {
                    for (int i = 0; i < t.childCount; i++) _walk.Push(new Visit(t.GetChild(i), visit.Category));
                    Rigidbody b = t.GetComponent<Rigidbody>();
                    if (b != null)
                    {
                        if (PlainStatic(b)) { UnityEngine.Object.Destroy(b); _staticRemoved++; }
                        else Track(b, visit.Category < 0 ? Category(b) : visit.Category, visit.Category >= 0);
                    }
                }
            }
        }

        static string _line;
        static int _lastTotal = -1, _lastDynamic, _lastSaved, _lastRemoved, _lastRestores;
        static bool _lastSeeding;
        static readonly int[] _lastCounts = new int[9];
        internal static string StatusLine()
        {
            bool changed = _line == null || _lastTotal != _bodies.Count || _lastDynamic != _dynamic
                || _lastSaved != _saved || _lastRemoved != _staticRemoved || _lastRestores != _restores
                || _lastSeeding != (_walk.Count > 0);
            for (int i = 0; i < _counts.Length; i++) if (_counts[i] != _lastCounts[i]) changed = true;
            if (!changed) return _line;
            _lastTotal = _bodies.Count; _lastDynamic = _dynamic; _lastSaved = _saved;
            _lastRemoved = _staticRemoved; _lastRestores = _restores;
            _lastSeeding = _walk.Count > 0;
            _line = "RB census " + _bodies.Count + " awake " + _dynamic + " stored " + _saved + " static- " + _staticRemoved;
            for (int i = 0; i < _counts.Length; i++) { _lastCounts[i] = _counts[i]; _line += " " + _names[i] + ":" + _counts[i]; }
            if (_walk.Count > 0) _line += " (seeding)";
            return _line;
        }

        internal static void Shutdown()
        {
            SceneManager.sceneLoaded -= Loaded;
            for (int i = 0; i < _dolls.Count; i++) Restore(_dolls[i]);
            _dolls.Clear(); _controllers.Clear(); _parts.Clear(); _walk.Clear();
            _bodies.Clear(); _ids.Clear(); Array.Clear(_counts, 0, _counts.Length);
            _stored = 0; _saved = 0; _dynamic = 0;
        }
    }
}
