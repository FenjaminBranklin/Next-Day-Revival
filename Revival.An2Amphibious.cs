// Air bridge 4a: permanent float kit on the shipped An-2, loaded once.
// No Update/FixedUpdate, network object, physics force or per-frame discovery.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace NextDayRevival
{
    internal static class An2Amphibious
    {
        internal sealed class Wheel
        {
            internal string Name, Kind;
            internal Vector3 Pivot;
            internal float Radius, CentreY, Retract;
        }

        static Mesh _body, _floats, _main, _rear;
        static Material _skin;
        static readonly List<Wheel> _wheels = new List<Wheel>();
        static readonly List<Bounds> _bottoms = new List<Bounds>();
        static readonly Vector3[] _contacts = new Vector3[3];
        internal static Vector3 Offset;
        internal static float Parked;
        internal static int FloatBottomCount { get { return _bottoms.Count; } }
        internal static Vector3 Contact(int index) { return _contacts[index]; }
        // Contact probes for the next water-physics task, in carrier metres.
        internal static Vector3 FloatBottom(int index)
        {
            Bounds b = _bottoms[index];
            return b.center - Vector3.up * (b.size.y * 0.5f) + Offset;
        }

        static float F(string value) { return float.Parse(value, CultureInfo.InvariantCulture); }
        static Vector3 V(string[] row, int start)
        {
            return new Vector3(F(row[start]), F(row[start + 1]), F(row[start + 2]));
        }

        internal static Mesh Load(Mesh original)
        {
            if (_body != null) return _body;
            _wheels.Clear(); _bottoms.Clear();
            List<int> starts = new List<int>(), counts = new List<int>();
            string digest = null;
            int vertices = 0, indices = 0, version = 0, contactBits = 0;
            bool scale = false, offset = false, parked = false;
            foreach (string raw in File.ReadAllLines(Path.Combine(RevivalPlugin.AssetDir, "an2_amphib_rig.txt")))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    case "version": version = int.Parse(p[1]); break;
                    case "scale":
                        scale = Mathf.Abs(F(p[1]) - PlayerAn2.K) < 0.001f && Mathf.Abs(F(p[2]) - 5f) < 0.001f;
                        break;
                    case "body": digest = p[1]; vertices = int.Parse(p[2]); indices = int.Parse(p[3]); break;
                    case "offset": Offset = V(p, 1); offset = true; break;
                    case "parked": Parked = F(p[1]); parked = true; break;
                    case "contact":
                        int at = p[1] == "l" ? 0 : (p[1] == "r" ? 1 : (p[1] == "tail" ? 2 : -1));
                        if (at < 0 || (contactBits & (1 << at)) != 0) throw new InvalidDataException("duplicate/unknown contact");
                        _contacts[at] = V(p, 2); contactBits |= 1 << at;
                        break;
                    case "remove":
                        if (p.Length != 4) throw new InvalidDataException("invalid land-gear mask range");
                        starts.Add(int.Parse(p[1])); counts.Add(int.Parse(p[2])); break;
                    case "wheel":
                        Wheel w = new Wheel();
                        w.Name = p[1]; w.Kind = p[2]; w.Pivot = V(p, 3);
                        w.Radius = F(p[6]); w.CentreY = F(p[7]); w.Retract = F(p[8]);
                        if ((w.Kind != "main" && w.Kind != "rear") || w.Radius <= 0f)
                            throw new InvalidDataException("invalid amphibious wheel");
                        _wheels.Add(w);
                        break;
                    case "floatbox":
                        Vector3 size = V(p, 5);
                        if (size.x <= 0f || size.y <= 0f || size.z <= 0f) throw new InvalidDataException("invalid float box");
                        _bottoms.Add(new Bounds(V(p, 2), size));
                        break;
                    default: throw new InvalidDataException("unknown amphibious rig record: " + p[0]);
                }
            }
            if (version != 1 || !scale || !offset || !parked || contactBits != 7
                || _wheels.Count != 4 || _bottoms.Count != 6 || starts.Count != 6)
                throw new InvalidDataException("incomplete amphibious rig");

            // Refuse a mask for another plane revision instead of hiding hull faces.
            byte[] file = File.ReadAllBytes(Path.Combine(RevivalPlugin.AssetDir, "an2_body.ndmesh"));
            string actual;
            using (SHA256 sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
            int[] source = original.triangles;
            if (actual != digest || original.vertexCount != vertices || source.Length != indices)
                throw new InvalidDataException("An-2 body changed; regenerate the amphibious kit");
            int previous = 0, removed = 0;
            for (int i = 0; i < starts.Count; i++)
            {
                if (starts[i] < previous || starts[i] % 3 != 0 || counts[i] <= 0 || counts[i] % 3 != 0
                    || starts[i] > source.Length - counts[i])
                    throw new InvalidDataException("invalid land-gear mask range");
                previous = starts[i] + counts[i]; removed += counts[i];
            }
            int[] retained = new int[source.Length - removed];
            int from = 0, to = 0;
            for (int i = 0; i < starts.Count; i++)
            {
                int length = starts[i] - from;
                Array.Copy(source, from, retained, to, length);
                to += length; from = starts[i] + counts[i];
            }
            Array.Copy(source, from, retained, to, source.Length - from);
            _floats = Assets.Load("an2_amphib_floats.ndmesh");
            _main = Assets.Load("an2_amphib_main.ndmesh");
            _rear = Assets.Load("an2_amphib_rear.ndmesh");
            Texture2D tex = Assets.Texture("an2_amphib_diffuse.png", false, true);
            Texture2D nrm = Assets.Texture("an2_amphib_normal.png", true, true);
            if (_floats == null || _main == null || _rear == null || tex == null || nrm == null)
                throw new InvalidDataException("missing amphibious kit assets");
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            _skin = new Material(shader);
            _skin.name = "NDR_An2_Amphibious";
            _skin.mainTexture = tex;
            _skin.color = Color.white;
            if (_skin.HasProperty("_Glossiness")) _skin.SetFloat("_Glossiness", 0.22f);
            if (_skin.HasProperty("_Metallic")) _skin.SetFloat("_Metallic", 0.1f);
            if (_skin.HasProperty("_BumpMap"))
            {
                _skin.SetTexture("_BumpMap", nrm); _skin.EnableKeyword("_NORMALMAP");
            }
            Mesh body = UnityEngine.Object.Instantiate(original) as Mesh;
            body.name = "NDR_An2_BodyWithoutLandGear";
            body.triangles = retained;
            body.RecalculateBounds();
            _body = body;
            RevivalPlugin.L.LogInfo("PlayerAn2: permanent amphibious kit loaded; " + removed / 3
                + " old gear triangles removed, four extended wheels, six float-bottom boxes. No frame callback.");
            return _body;
        }

        static GameObject Part(Transform parent, string name, Mesh mesh)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _skin;
            return go;
        }

        internal static void Attach(Transform model)
        {
            if (model.Find("Amphibious") != null) return;
            if (_body == null) throw new InvalidOperationException("amphibious kit not loaded");
            GameObject root = new GameObject("Amphibious");
            root.transform.SetParent(model, false);
            Part(root.transform, "PermanentFloats", _floats);
            for (int i = 0; i < _bottoms.Count; i++)
            {
                GameObject go = new GameObject("FloatBottom" + i);
                go.transform.SetParent(root.transform, false);
                BoxCollider box = go.AddComponent<BoxCollider>();
                box.center = _bottoms[i].center; box.size = _bottoms[i].size;
                box.isTrigger = false;
            }
            Transform[] pivots = new Transform[_wheels.Count];
            float[] angles = new float[_wheels.Count];
            for (int i = 0; i < _wheels.Count; i++)
            {
                Wheel w = _wheels[i];
                GameObject pivot = new GameObject("WheelPivot_" + w.Name);
                pivot.transform.SetParent(root.transform, false);
                pivot.transform.localPosition = w.Pivot;
                GameObject wheel = Part(pivot.transform, "Wheel_" + w.Name, w.Kind == "main" ? _main : _rear);
                SphereCollider tyre = wheel.AddComponent<SphereCollider>();
                tyre.center = new Vector3(0f, w.CentreY, 0f); tyre.radius = w.Radius;
                tyre.isTrigger = false;
                pivots[i] = pivot.transform; angles[i] = w.Retract;
            }
            root.AddComponent<An2AmphibiousGear>().Attach(pivots, angles);
        }
    }

    // The wheels are down and visible on every client. The next physics task
    // can call SetRetraction from authoritative state; floats never move/hide.
    // This component deliberately has no Unity frame callbacks.
    public sealed class An2AmphibiousGear : MonoBehaviour
    {
        Transform[] _pivots;
        float[] _angles;
        float _retraction = -1f;
        internal void Attach(Transform[] pivots, float[] angles)
        {
            _pivots = pivots; _angles = angles; SetRetraction(0f);
        }
        internal void SetRetraction(float fraction)
        {
            if (float.IsNaN(fraction) || float.IsInfinity(fraction)) return;
            fraction = Mathf.Clamp01(fraction);
            if (_pivots == null || fraction == _retraction) return;
            _retraction = fraction;
            for (int i = 0; i < _pivots.Length; i++)
                _pivots[i].localRotation = Quaternion.AngleAxis(_angles[i] * fraction, Vector3.forward);
        }
    }
}
