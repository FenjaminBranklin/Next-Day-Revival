// Z L1b: static field-airfield earthworks. No Update/LateUpdate or scene scan.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    public struct FlakSweepState
    {
        public bool Active;
        public Vector3 Point;
    }

    internal static class FlakPositions
    {
        static Material _earth;
        static MethodInfo _rpc;
        static Type _npc;
        static FieldInfo _specs, _health;
        static bool _sweepActive;
        static Vector3 _sweepPoint;

        internal static Transform Build(int index, Scene scene)
        {
            int pit = FlakPositionsCore.Position(index);
            if (pit < 0) return null;
            Vector3 at = new Vector3(FlakPositionsCore.X[pit], 0f, FlakPositionsCore.Z[pit]);
            float y;
            if (!EastWorld.TerrainHeight(at, out y)) return null;
            // L1a measures <=0.32 u relief. A 0.4 u raised pad clears the
            // terrain without editing TerrainData or requiring Unity bundles.
            at.y = y + FlakPositionsCore.PadLift;
            GameObject root = new GameObject("NDR earthwork " + FlakPositionsCore.Name[pit]);
            SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.position = at;
            root.transform.rotation = Quaternion.Euler(0f, FlakPositionsCore.Heading(pit), 0f);
            root.AddComponent<FlakEarthwork>();
            return root.transform;
        }

        internal static void Attach(Flak.Gun gun, Transform pit)
        {
            gun.Earthwork = pit;
            gun.Root.SetParent(pit, true);
            gun.Root.position = pit.position;
            // Both rigs share the 1.8 m firing axis specified by L1a. The ZU
            // otherwise has a 0.95 m axis hidden behind a 1.2 m earth wall.
            float lift = FlakPositionsCore.Pivot * Flak.K - gun.Mount.localPosition.y - gun.Cradle.localPosition.y;
            if (lift > 0f) gun.Mount.localPosition += Vector3.up * lift;
            GameObject column = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            column.name = "field gun pedestal";
            column.transform.SetParent(gun.Root, false);
            float columnHeight = gun.Mount.localPosition.y;
            column.transform.localPosition = Vector3.up * columnHeight * 0.5f;
            column.transform.localScale = new Vector3(0.65f * Flak.K, columnHeight * 0.5f, 0.65f * Flak.K);
            column.GetComponent<Renderer>().sharedMaterial = Earth;
            gun.Owner = gun.Root; // A gun's own shots must collide with its berm.
            gun.Holder = null;   // The legacy sandbag swap/AddPit immunity is unused.
            Flak.Log(gun.Id + ": field position " + pit.name + " at " + pit.position + ".");
        }

        internal static bool Active(Flak.Gun gun, bool master)
        {
            if (gun == Flak._manned) { gun.Awake = true; return true; }
            if (Time.time < gun.NextWake) return gun.Awake;
            gun.NextWake = Time.time + 0.5f;
            if (MercAA.Gun(gun.Index) != null || MercAA.Operator != null)
            { gun.Awake = true; return true; }
            float radius = Flak.GunRange(gun) + 600f * Flak.K;
            Vector3 centre = gun.Root.position;
            GameObject me = MapTools.LocalPlayer();
            gun.Awake = me != null && (me.transform.position - centre).sqrMagnitude < radius * radius;
            if (!gun.Awake && master)
            {
                List<GameObject> players = GepardCrew.Spieler();
                for (int i = 0; i < players.Count; i++)
                    if (players[i] != null && (players[i].transform.position - centre).sqrMagnitude < radius * radius)
                    { gun.Awake = true; break; }
            }
            return gun.Awake;
        }

        internal static Material Earth
        {
            get
            {
                if (_earth != null) return _earth;
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                _earth = new Material(shader);
                _earth.name = "NDR earth revetment";
                _earth.color = new Color(0.29f, 0.25f, 0.17f, 1f);
                if (_earth.HasProperty("_Glossiness")) _earth.SetFloat("_Glossiness", 0f);
                return _earth;
            }
        }

        internal static bool Centre(Vector3 man, out Vector3 centre)
        {
            for (int i = 0; i < FlakPositionsCore.GunIndex.Length; i++)
            {
                Flak.Gun g = Flak.ByIndex(FlakPositionsCore.GunIndex[i]);
                if (g == null || g.Earthwork == null) continue;
                centre = g.Earthwork.position;
                Vector3 d = man - centre;
                if (FlakPositionsCore.Inside(d.x, d.y, d.z)) return true;
            }
            centre = Vector3.zero;
            return false;
        }

        internal static void Protect(Component ai, ref float damage, int body, ref Vector3 direction)
        {
            if (ai == null || damage <= 0f) return;
            Vector3 centre;
            if (!Centre(ai.transform.position, out centre)) return;
            Vector3 blast = ai.transform.position - direction - centre;
            object specs = _specs == null ? null : _specs.GetValue(ai);
            if (specs != null)
            {
                if (_health == null) _health = AccessTools.Field(specs.GetType(), "Health");
                if (_health != null)
                {
                    float health = Convert.ToSingle(_health.GetValue(specs));
                    float multiplier = body == 0 ? 3f : body == 1 ? 1f : 0.5f;
                    damage = FlakPositionsCore.Damage(damage * multiplier, health, blast.x, blast.z) / multiplier;
                }
            }
            // Restore native unit force direction before ragdoll animations.
            direction.y = 0f;
            direction.Normalize();
        }

        public static void BeforeDamage(object __instance, ref float __0, int __1, int __2, ref Vector3 __4)
        {
            if (__2 == OrdnanceBlast.Explosion) Protect(__instance as Component, ref __0, __1, ref __4);
            else if (_sweepActive && __2 == 0)
            {
                // Mortar.Sweep's old anonymous TryDamage path sends body/type
                // zero and no displacement. Its synchronous call needs the
                // real point too; rifle hits outside the scope stay unchanged.
                Component ai = __instance as Component;
                Vector3 centre;
                if (ai != null && Centre(ai.transform.position, out centre))
                {
                    __4 = ai.transform.position - _sweepPoint;
                    Protect(ai, ref __0, __1, ref __4);
                }
            }
        }

        public static void BeginSweep(Vector3 __0, out FlakSweepState __state)
        {
            __state = new FlakSweepState();
            __state.Active = _sweepActive; __state.Point = _sweepPoint;
            _sweepActive = true; _sweepPoint = __0;
        }

        public static Exception EndSweep(Exception __exception, FlakSweepState __state)
        {
            _sweepActive = __state.Active; _sweepPoint = __state.Point;
            return __exception;
        }

        internal static void Install(Harmony harmony)
        {
            _rpc = AccessTools.Method(RevivalPlugin.TypeByName("PhotonView"), "RPC", new Type[] {
                typeof(string), RevivalPlugin.TypeByName("PhotonPlayer"), typeof(object[]) }, null);
            _npc = RevivalPlugin.TypeByName("NPC_AI2");
            _specs = AccessTools.Field(_npc, "Specifications");
            MethodInfo physics = AccessTools.Method(RevivalPlugin.TypeByName("ExplosionObject"), "ExplosionPhysicsEffect", null, null);
            if (physics == null || _rpc == null || _specs == null)
                throw new InvalidOperationException("Flak earthwork native damage bindings missing");
            harmony.Patch(physics, null, null,
                new HarmonyMethod(typeof(FlakPositions).GetMethod("NativeDispatch")), null, null);
            MethodInfo damage = AccessTools.Method(_npc, "ApplyDamage", new Type[] {
                typeof(float), typeof(int), typeof(int), typeof(int), typeof(Vector3),
                RevivalPlugin.TypeByName("PhotonMessageInfo") }, null);
            harmony.Patch(damage, new HarmonyMethod(typeof(FlakPositions).GetMethod("BeforeDamage")),
                null, null, null, null);
            Type byref = typeof(int).MakeByRefType();
            MethodInfo sweep = AccessTools.Method(typeof(Mortar), "Sweep", new Type[] {
                typeof(Vector3), typeof(bool), typeof(float), typeof(float), typeof(float), typeof(float),
                byref, byref, byref }, null);
            harmony.Patch(sweep, new HarmonyMethod(typeof(FlakPositions).GetMethod("BeginSweep")),
                null, null, new HarmonyMethod(typeof(FlakPositions).GetMethod("EndSweep")), null);
        }

        // Native ExplosionPhysicsEffect discards distance when normalizing its
        // force vector. Carry full displacement to the existing owner-side
        // ApplyDamage prefix, then normalize there. Other native RPCs pass on.
        public static IEnumerable<CodeInstruction> NativeDispatch(IEnumerable<CodeInstruction> source)
        {
            bool npcPacket = false;
            int patched = 0;
            foreach (CodeInstruction c in source)
            {
                if (c.opcode == OpCodes.Ldstr) npcPacket = (string)c.operand == "ApplyDamage";
                if (npcPacket && (c.opcode == OpCodes.Callvirt || c.opcode == OpCodes.Call) && Equals(c.operand, _rpc))
                {
                    CodeInstruction explosion = new CodeInstruction(OpCodes.Ldarg_0, null);
                    explosion.labels.AddRange(c.labels); c.labels.Clear();
                    yield return explosion;
                    c.opcode = OpCodes.Call;
                    c.operand = typeof(FlakPositions).GetMethod("SendNative");
                    npcPacket = false; patched++;
                }
                yield return c;
            }
            if (patched == 0) throw new InvalidOperationException("Flak earthwork native NPC dispatch not found");
        }

        public static void SendNative(Component view, string method, object owner, object[] packet, Component explosion)
        {
            if (method == "ApplyDamage" && packet.Length == 5 && (int)packet[2] == OrdnanceBlast.Explosion)
            {
                Component ai = view.GetComponent(_npc);
                Vector3 centre;
                if (ai != null && Centre(ai.transform.position, out centre))
                    packet[4] = ai.transform.position - explosion.transform.position;
            }
            _rpc.Invoke(view, new object[] { method, owner, packet });
        }
    }

    internal sealed class FlakEarthwork : MonoBehaviour
    {
        readonly List<Mesh> meshes = new List<Mesh>();
        // Construction is spread across frames; after Start finishes this
        // component has no tick. Static carving also stops when stationary.
        System.Collections.IEnumerator Start()
        {
            FrameProf.S(FrameProf.S_FlakEarthworkBuild);
            Vector3[] pad = new Vector3[FlakPositionsCore.Segments + 1];
            int[] tris = new int[FlakPositionsCore.Segments * 3];
            for (int i = 0; i < FlakPositionsCore.Segments; i++)
            {
                float angle = i * Mathf.PI * 2 / FlakPositionsCore.Segments;
                pad[i + 1] = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * FlakPositionsCore.Inner * FlakPositionsCore.K;
                tris[i * 3] = 0; tris[i * 3 + 1] = i + 1; tris[i * 3 + 2] = (i + 1) % FlakPositionsCore.Segments + 1;
            }
            Part("level earth pad", pad, tris);
            FrameProf.E(FrameProf.S_FlakEarthworkBuild);
            Vector3[] wall = new Vector3[FlakPositionsCore.Segments * 6];
            int[] wallTriangles = new int[FlakPositionsCore.Segments * 24];
            int[] sectorTriangles = new int[] { 0,1,3, 1,4,3, 1,2,4, 2,5,4, 0,2,1, 3,4,5, 0,3,2, 2,3,5 };
            for (int i = 0; i < FlakPositionsCore.Segments; i++)
            {
                yield return null;
                FrameProf.S(FrameProf.S_FlakEarthworkBuild);
                float[] points = FlakPositionsCore.Sector(i);
                for (int j = 0; j < 6; j++) wall[i * 6 + j] = new Vector3(points[j*3], points[j*3+1], points[j*3+2]);
                for (int j = 0; j < sectorTriangles.Length; j++) wallTriangles[i * 24 + j] = sectorTriangles[j] + i * 6;
                float[] box = FlakPositionsCore.Carve(i);
                float angle = (FlakPositionsCore.Angle(i) + FlakPositionsCore.Angle(i+1)) * 0.5f;
                GameObject carve = new GameObject("berm navigation exclusion");
                carve.transform.SetParent(transform, false);
                carve.transform.localPosition = new Vector3(Mathf.Sin(angle) * box[0], FlakPositionsCore.Height * FlakPositionsCore.K / 2, Mathf.Cos(angle) * box[0]);
                carve.transform.localRotation = Quaternion.Euler(0f, angle * Mathf.Rad2Deg, 0f);
                NavMeshObstacle obstacle = carve.AddComponent<NavMeshObstacle>();
                obstacle.shape = NavMeshObstacleShape.Box;
                obstacle.size = new Vector3(box[2], (FlakPositionsCore.Height + 0.5f) * FlakPositionsCore.K, box[1]);
                obstacle.carveOnlyStationary = true;
                obstacle.carving = true;
                FrameProf.E(FrameProf.S_FlakEarthworkBuild);
            }
            yield return null;
            FrameProf.S(FrameProf.S_FlakEarthworkBuild);
            // One renderer/collider for the entire wall, rather than 24 draw
            // calls and shadow casters. Still only 192 triangles per position.
            Part("earth berm", wall, wallTriangles);
            FrameProf.E(FrameProf.S_FlakEarthworkBuild);
        }

        GameObject Part(string name, Vector3[] vertices, int[] triangles)
        {
            Mesh mesh = new Mesh(); mesh.name = name;
            mesh.vertices = vertices; mesh.triangles = triangles;
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); meshes.Add(mesh);
            GameObject part = new GameObject(name);
            part.transform.SetParent(transform, false);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            part.AddComponent<MeshRenderer>().sharedMaterial = FlakPositions.Earth;
            part.AddComponent<MeshCollider>().sharedMesh = mesh;
            return part;
        }

        void OnDestroy()
        {
            for (int i = 0; i < meshes.Count; i++) if (meshes[i] != null) UnityEngine.Object.Destroy(meshes[i]);
        }
    }
}
