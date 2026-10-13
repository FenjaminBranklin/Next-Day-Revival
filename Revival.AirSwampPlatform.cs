// Aged concrete pad, timber supports and simple utility props in vanilla colours.
// Built once per loaded swamp scene; deliberately has no per-frame behavior.
using UnityEngine;

namespace NextDayRevival
{
    internal static class AirSwampPlatform
    {
        static Material _concrete, _wood, _metal, _rust, _cloth, _stripe, _glass;
        static Mesh _sock;
        internal static Material Concrete { get { Materials(); return _concrete; } }
        internal static Material Wood { get { Materials(); return _wood; } }
        static Material Finish(Shader shader, Color color)
        {
            Material m = new Material(shader); m.color = color;
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", .05f);
            return m;
        }
        static Texture2D Weather(bool timber)
        {
            const int size = 128;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGB24, false);
            texture.name = timber ? "SwampWeatheredTimber" : "SwampWeatheredConcrete";
            Color[] pixels = new Color[size * size];
            System.Random random = new System.Random(timber ? 830 : 829);
            for (int z = 0; z < size; z++) for (int x = 0; x < size; x++)
            {
                float noise = (float)random.NextDouble() * .12f;
                bool seam = timber ? x % 32 < 2 : x < 2 || z < 2;
                float shade = seam ? .40f : .83f + noise;
                if (timber) shade -= .05f * Mathf.Sin(z * .6f + x * .1f);
                pixels[z * size + x] = new Color(shade, shade, shade);
            }
            texture.SetPixels(pixels); texture.Apply(false, true);
            texture.wrapMode = TextureWrapMode.Repeat;
            return texture;
        }
        static void Materials()
        {
            if (_concrete != null) return;
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            _concrete = Finish(shader, new Color(.43f, .43f, .36f));
            _concrete.mainTexture = Weather(false); _concrete.mainTextureScale = new Vector2(8, 8);
            _wood = Finish(shader, new Color(.32f, .27f, .19f));
            _wood.mainTexture = Weather(true);
            _metal = Finish(shader, new Color(.18f, .20f, .17f));
            _rust = Finish(shader, new Color(.36f, .24f, .15f));
            _cloth = Finish(shader, new Color(.48f, .24f, .12f));
            _stripe = Finish(shader, new Color(.73f, .68f, .51f));
            _glass = Finish(shader, new Color(.95f, .80f, .46f));
            if (_glass.HasProperty("_EmissionColor"))
            {
                _glass.EnableKeyword("_EMISSION");
                _glass.SetColor("_EmissionColor", new Color(.45f, .31f, .11f));
            }
        }
        internal static void Build(Transform root)
        {
            Materials();
            for (int i = 0; i < AirSwampCore.Parts.Length; i++)
            {
                SwampPart p = AirSwampCore.Parts[i];
                GameObject go = GameObject.CreatePrimitive(p.Round ? PrimitiveType.Cylinder : PrimitiveType.Cube);
                go.name = p.Name; go.transform.SetParent(root, false);
                go.transform.localPosition = new Vector3(p.X, p.Y, p.Z);
                // Unity's cylinder has native height 2; cube dimensions are 1.
                go.transform.localScale = new Vector3(p.Width, p.Round ? p.Height * .5f : p.Height, p.Depth);
                go.GetComponent<Renderer>().sharedMaterial = p.Finish == "wood" ? _wood
                    : p.Finish == "rust" ? _rust : p.Finish == "glass" ? _glass : _metal;
                if (!p.Solid)
                {
                    Collider c = go.GetComponent<Collider>(); c.enabled = false;
                    Object.Destroy(c);
                }
                if (p.Name == "LampGlass")
                {
                    Light lamp = go.AddComponent<Light>(); lamp.type = LightType.Point;
                    lamp.color = new Color(1, .78f, .48f); lamp.range = 22; lamp.intensity = .65f;
                    lamp.shadows = LightShadows.None;
                }
            }
            GameObject sock = new GameObject("Windsock"); sock.transform.SetParent(root, false);
            sock.AddComponent<MeshFilter>().sharedMesh = Sock();
            MeshRenderer renderer = sock.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new Material[] { _cloth, _stripe };
        }
        static Mesh Sock()
        {
            if (_sock != null) return _sock;
            int sides = AirSwampCore.SockSides, rings = AirSwampCore.SockRings;
            int faceCount = rings * sides;
            Vector3[] points = new Vector3[faceCount * 2], normals = new Vector3[faceCount * 2];
            for (int r = 0; r < rings; r++) for (int s = 0; s < sides; s++)
            {
                float x, y, z; AirSwampCore.SockPoint(r, s, out x, out y, out z);
                int i = r * sides + s;
                points[i] = points[i + faceCount] = new Vector3(x, y, z);
                float angle = 2 * Mathf.PI * s / sides;
                normals[i] = new Vector3(0, Mathf.Sin(angle), Mathf.Cos(angle));
                normals[i + faceCount] = -normals[i];
            }
            // Alternating faded cloth bands; both faces of the open tube are visible.
            _sock = new Mesh(); _sock.name = "SwampWindsock"; _sock.vertices = points;
            _sock.subMeshCount = 2;
            _sock.SetTriangles(AirSwampCore.SockTriangles(0), 0); _sock.SetTriangles(AirSwampCore.SockTriangles(1), 1);
            _sock.normals = normals; _sock.RecalculateBounds();
            return _sock;
        }
    }
}
