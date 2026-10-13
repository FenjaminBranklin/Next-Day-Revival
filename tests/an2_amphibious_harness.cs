// Compile the real production loader/attachment against deterministic stubs.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public static Object Instantiate(Object original) { return ((Mesh)original).Copy(); }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform { get { return gameObject.transform; } }
    }
    public class MonoBehaviour : Component { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public static Vector3 up { get { return new Vector3(0, 1, 0); } }
        public static Vector3 forward { get { return new Vector3(0, 0, 1); } }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x+b.x, a.y+b.y, a.z+b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x, a.y-b.y, a.z-b.z); }
        public static Vector3 operator *(Vector3 a, float b) { return new Vector3(a.x*b, a.y*b, a.z*b); }
    }
    public struct Quaternion
    {
        public float Angle;
        public static int Writes;
        public static Quaternion AngleAxis(float angle, Vector3 axis)
        {
            Writes++; Quaternion q = new Quaternion(); q.Angle = angle; return q;
        }
    }
    public struct Bounds
    {
        public Vector3 center, size;
        public Bounds(Vector3 c, Vector3 s) { center = c; size = s; }
    }
    public static class Mathf
    {
        public static float Abs(float f) { return Math.Abs(f); }
        public static float Clamp01(float f) { return Math.Max(0f, Math.Min(1f, f)); }
    }
    public class Transform : Component
    {
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Transform parent;
        public readonly List<Transform> children = new List<Transform>();
        public void SetParent(Transform p, bool world) { parent = p; p.children.Add(this); }
        public Transform Find(string child) { return children.Find(delegate(Transform t) { return t.gameObject.name == child; }); }
    }
    public class GameObject : Object
    {
        public readonly Transform transform;
        public readonly List<Component> components = new List<Component>();
        public static int Created;
        public GameObject(string n) { name = n; transform = new Transform(); transform.gameObject = this; Created++; }
        public T AddComponent<T>() where T : Component, new()
        {
            T c = new T(); c.gameObject = this; components.Add(c); return c;
        }
        public T GetComponent<T>() where T : Component
        {
            foreach (Component c in components) if (c is T) return (T)c;
            return null;
        }
    }
    public class Mesh : Object
    {
        public int vertexCount;
        public int[] triangles;
        public Mesh Copy() { Mesh m = new Mesh(); m.vertexCount = vertexCount; m.triangles = (int[])triangles.Clone(); return m; }
        public void RecalculateBounds() { }
    }
    public class Texture2D : Object { }
    public class Shader : Object { public static Shader Find(string s) { return new Shader(); } }
    public struct Color { public static Color white { get { return new Color(); } } }
    public class Material : Object
    {
        public Material(Shader s) { }
        public Texture2D mainTexture;
        public Color color;
        public bool HasProperty(string s) { return true; }
        public void SetFloat(string s, float f) { }
        public void SetTexture(string s, Texture2D t) { }
        public void EnableKeyword(string s) { }
    }
    public class MeshFilter : Component { public Mesh sharedMesh; }
    public class MeshRenderer : Component { public Material sharedMaterial; }
    public class BoxCollider : Component { public Vector3 center, size; public bool isTrigger; }
    public class SphereCollider : Component { public Vector3 center; public float radius; public bool isTrigger; }
}

namespace NextDayRevival
{
    internal static class PlayerAn2 { internal const float K = 2.8f; }
    internal static class RevivalPlugin
    {
        internal static string AssetDir;
        internal static Logger L = new Logger();
    }
    internal class Logger { internal void LogInfo(string s) { } }
    internal static class Assets
    {
        internal static Mesh Load(string file)
        {
            string path = Path.Combine(RevivalPlugin.AssetDir, file);
            if (!File.Exists(path)) return null;
            using (BinaryReader r = new BinaryReader(File.OpenRead(path)))
            {
                r.ReadBytes(8); Mesh m = new Mesh(); m.vertexCount = r.ReadInt32();
                r.BaseStream.Position += m.vertexCount * 32;
                m.triangles = new int[r.ReadInt32()];
                for (int i = 0; i < m.triangles.Length; i++) m.triangles[i] = r.ReadInt32();
                return m;
            }
        }
        internal static Texture2D Texture(string file, bool linear, bool mipmaps)
        {
            return File.Exists(Path.Combine(RevivalPlugin.AssetDir, file)) ? new Texture2D() : null;
        }
    }
    internal static class Test
    {
        static int _checks;
        static void Need(bool ok, string label) { _checks++; if (!ok) throw new Exception(label); }
        static void Equal(float actual, float expected, string label) { Need(Math.Abs(actual-expected) < .0001f, label); }
        static int Count<T>(Transform root) where T : Component
        {
            int sum = root.gameObject.GetComponent<T>() != null ? 1 : 0;
            foreach (Transform t in root.children) sum += Count<T>(t);
            return sum;
        }
        static void RigAlignment()
        {
            // Production An2Model rig-adjustment block, injected by the runner.
            Dictionary<int, Vector3> _seats = new Dictionary<int, Vector3>();
            _seats[-1] = new Vector3(-.42f, 1.38f, .13f);
            _seats[0] = new Vector3(-.45f, 1.45f, -.6f);
            Vector3[] _contact = new Vector3[3];
            List<Bounds> Boxes = new List<Bounds>();
            Boxes.Add(new Bounds(new Vector3(0, 2.18f, -3.61f), new Vector3(1.83f, 2.27f, 11.17f)));
            Vector3[] Probes = new Vector3[] { new Vector3(9.09f, 4.5f, -.96f) };
            Vector3 Door = new Vector3(-.88f, 1.28f, -2.1f);
            Vector3 WingTip = new Vector3(9.09f, 4.8f, -.96f);
            Vector3 LowerTip = new Vector3(7f, 1.55f, -.65f);
            float Parked = 8.21f;
            /*RIG_ADJUSTMENT*/
            Equal(_seats[-1].y, 2.63f, "pilot follows raised airframe");
            Equal(_seats[0].y, 2.70f, "passenger follows raised airframe");
            Equal(Boxes[0].center.y, 3.43f, "hull follows model");
            Equal(Boxes[0].size.y, 2.27f, "hull size is unchanged");
            Equal(Probes[0].y, 5.75f, "scrape probe follows wing");
            Equal(Door.y, 2.53f, "door follows airframe");
            Equal(WingTip.y, 6.05f, "upper tip follows model");
            Equal(LowerTip.y, 2.8f, "lower tip follows model");
            Equal(_contact[0].x, -2.68f, "port contact follows wheel");
            Equal(_contact[1].x, 2.68f, "starboard contact follows wheel");
            Equal(_contact[2].z, -4.8f, "rear contact follows wheels");
            Equal(Parked, 0f, "four-wheel stance level");
        }
        public static int Main(string[] args)
        {
            RevivalPlugin.AssetDir = args[0];
            Mesh original = Assets.Load("an2_body.ndmesh");
            if (args.Length > 1)
            {
                try { An2Amphibious.Load(original); }
                catch (Exception ex)
                {
                    Need(ex.Message.Contains(args[1]), "expected failure: " + ex.Message);
                    Console.WriteLine("PASS rejects " + args[1]); return 0;
                }
                throw new Exception("bad kit accepted");
            }
            Mesh body = An2Amphibious.Load(original);
            Need(body != original, "original cached mesh retained");
            Need(body.triangles.Length == original.triangles.Length - 3312, "six gear ranges removed");
            Need(body.vertexCount == original.vertexCount, "original vertices retained");
            Need(ReferenceEquals(body, An2Amphibious.Load(original)), "loader cached");
            List<int> expected = new List<int>();
            HashSet<int> omitted = new HashSet<int>();
            foreach (string line in File.ReadAllLines(Path.Combine(args[0], "an2_amphib_rig.txt")))
            {
                string[] p = line.Split(' ');
                if (p[0] != "remove") continue;
                int at = int.Parse(p[1]), end = at + int.Parse(p[2]);
                for (; at < end; at++) omitted.Add(at);
            }
            for (int i = 0; i < original.triangles.Length; i++)
                if (!omitted.Contains(i)) expected.Add(original.triangles[i]);
            Need(expected.Count == body.triangles.Length, "retained indices count");
            for (int i = 0; i < expected.Count; i++) Need(expected[i] == body.triangles[i], "all retained indices unchanged");
            RigAlignment();
            GameObject model = new GameObject("NDR_An2");
            An2Amphibious.Attach(model.transform);
            int created = GameObject.Created;
            An2Amphibious.Attach(model.transform);
            Need(created == GameObject.Created, "attachment idempotent");
            Transform root = model.transform.Find("Amphibious");
            Need(root != null && model.transform.children.Count == 1, "one kit per plane");
            Need(root.Find("PermanentFloats") != null, "floats always present");
            Need(Count<BoxCollider>(root) == 6, "six nontrigger float-bottom boxes");
            Need(Count<SphereCollider>(root) == 4, "four physical tyres");
            foreach (Transform t in root.children)
            {
                if (!t.gameObject.name.StartsWith("WheelPivot_")) continue;
                Equal(t.localRotation.Angle, 0f, "visible wheels extended by default");
                SphereCollider tyre = t.children[0].gameObject.GetComponent<SphereCollider>();
                Equal(t.localPosition.y + tyre.center.y - tyre.radius + An2Amphibious.Offset.y, 0f, "ground contact matches tyre");
                Need(!tyre.isTrigger, "wheel physical contact");
            }
            Need(An2Amphibious.FloatBottomCount == 6, "water probes available without scans");
            Equal(An2Amphibious.FloatBottom(1).y, .39f, "fore float keel collider datum");
            An2AmphibiousGear gear = root.gameObject.GetComponent<An2AmphibiousGear>();
            gear.SetRetraction(1f);
            Equal(root.Find("WheelPivot_main_l").localRotation.Angle, -105f, "left swing");
            Equal(root.Find("WheelPivot_main_r").localRotation.Angle, 105f, "right swing");
            int writes = Quaternion.Writes;
            gear.SetRetraction(2f); gear.SetRetraction(float.NaN); gear.SetRetraction(float.PositiveInfinity);
            Need(writes == Quaternion.Writes, "unchanged/clamped/invalid gear avoids writes");
            gear.SetRetraction(-1f);
            Equal(root.Find("WheelPivot_main_l").localRotation.Angle, 0f, "extends again");
            gear.SetRetraction(.5f);
            Equal(root.Find("WheelPivot_rear_r").localRotation.Angle, 52.5f, "partial retraction");
            Need(root.Find("PermanentFloats") != null && Count<BoxCollider>(root) == 6, "retraction keeps floats and bottoms");
            Console.WriteLine("PASS " + _checks + " production loader/attachment/rig/gear checks");
            return 0;
        }
    }
}
