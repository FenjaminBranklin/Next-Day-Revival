// Next Day: Survival - Revival Toolkit
//
// EAST ZONES: names the placed-content piece the player stands in, so a walk
// through a greybox can be talked about by id (docs/ai/tasks/
// airfield-greybox.md). Acts only with [World] EastTile (EastWorld.On).
//
// The east content scenes (unity/EastTile BuildContent.cs; today the
// airfield greybox, AirfieldGreybox.cs) carry invisible markers under
// <root>/Markers: empty objects named "<id>|<name>", position = centre,
// rotation = yaw, localScale = size. They have no collider (a trigger box
// would stop bullets), so this polls: every 0.25 s the local player's
// position against every marker box. When the set of boxes around the player
// changes, one log line
//
//   EastZones: in H1 Repair hangar | N2 NPC zone: repair compound at (4251, 1102)
//
// (smallest box first), and while inside any a small label at the top left.
// Once per load, 5 s after a scene with markers is up: how many of its
// carving NavMeshObstacles have cut the NavMesh under their centre (the
// greybox's NavMesh: the tile's own, with the pieces cut out).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class EastZones
    {
        struct Box
        {
            public string id, name;
            public Vector3 centre, half;
            public Quaternion inv;
            public float area;
        }

        static readonly List<Box> _boxes = new List<Box>();
        static readonly List<string> _scenes = new List<string>();
        static int _sceneCount = -1;
        static float _next, _rescan, _navAt;
        static string _where = "", _label = "";
        static GUIStyle _st;

        internal static void Tick()
        {
            if (!EastWorld.On) return;
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 0.25f;
            try
            {
                if (SceneManager.sceneCount != _sceneCount || now > _rescan) Scan(now);
                if (_navAt > 0f && now > _navAt) { _navAt = 0f; NavCheck(); }
                if (_boxes.Count == 0) { Set("", Vector3.zero); return; }
                GameObject me = MapTools.LocalPlayer();
                if (me == null) return;
                Vector3 p = me.transform.position;
                List<Box> inside = new List<Box>();
                foreach (Box b in _boxes)
                {
                    Vector3 d = b.inv * (p - b.centre);
                    if (Mathf.Abs(d.x) <= b.half.x && Mathf.Abs(d.y) <= b.half.y && Mathf.Abs(d.z) <= b.half.z) inside.Add(b);
                }
                inside.Sort((a, b) => a.area.CompareTo(b.area));
                List<string> parts = new List<string>();
                foreach (Box b in inside)
                {
                    string s = b.id + " " + b.name;
                    if (!parts.Contains(s)) parts.Add(s);
                }
                Set(string.Join(" | ", parts.ToArray()), p);
            }
            catch (Exception ex)
            {
                _next = now + 10f;
                Log("tick failed: " + ex.Message);
            }
        }

        static void Set(string where, Vector3 p)
        {
            if (where == _where) return;
            _where = where;
            _label = where;
            if (where.Length > 0) Log("in " + where + " at (" + p.x.ToString("F0") + ", " + p.z.ToString("F0") + ")");
            else if (p != Vector3.zero) Log("left the marked zones at (" + p.x.ToString("F0") + ", " + p.z.ToString("F0") + ")");
        }

        static void Scan(float now)
        {
            _sceneCount = SceneManager.sceneCount;
            _rescan = now + 5f;
            _boxes.Clear();
            List<string> found = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded || !s.name.StartsWith("East") || s.name == EastWorld.SceneName) continue;
                int n = 0;
                foreach (GameObject r in s.GetRootGameObjects())
                {
                    Transform m = r.transform.Find("Markers");
                    if (m == null) continue;
                    foreach (Transform t in m)
                    {
                        int bar = t.name.IndexOf('|');
                        Box b = new Box();
                        b.id = bar > 0 ? t.name.Substring(0, bar) : t.name;
                        b.name = bar > 0 ? t.name.Substring(bar + 1) : "";
                        b.centre = t.position;
                        b.half = t.lossyScale * 0.5f;
                        b.inv = Quaternion.Inverse(t.rotation);
                        b.area = t.lossyScale.x * t.lossyScale.z;
                        _boxes.Add(b);
                        n++;
                    }
                }
                if (n > 0) found.Add(s.name + " (" + n + " markers)");
            }
            string key = string.Join(", ", found.ToArray());
            if (key != string.Join(", ", _scenes.ToArray()))
            {
                _scenes.Clear();
                _scenes.AddRange(found);
                Log(found.Count > 0 ? "markers: " + key + "." : "no content markers loaded.");
                if (found.Count > 0) _navAt = now + 5f;
            }
        }

        /// <summary>Every carving obstacle of at least 4 x 4 m in the content
        /// scenes: is the NavMesh gone under its centre?</summary>
        static void NavCheck()
        {
            int big = 0, cut = 0, all = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded || !s.name.StartsWith("East") || s.name == EastWorld.SceneName) continue;
                foreach (GameObject r in s.GetRootGameObjects())
                    foreach (NavMeshObstacle o in r.GetComponentsInChildren<NavMeshObstacle>())
                    {
                        all++;
                        Vector3 size = Vector3.Scale(o.size, o.transform.lossyScale);
                        if (size.x < 4f || size.z < 4f) continue;
                        big++;
                        Vector3 c = o.transform.TransformPoint(o.center);
                        NavMeshHit h;
                        if (!NavMesh.SamplePosition(c, out h, size.y * 0.5f + 1f, NavMesh.AllAreas)) { cut++; continue; }
                        Vector3 d = o.transform.InverseTransformPoint(h.position) - o.center;
                        d.y = 0f;                        // on the ground below the centre = not cut
                        if (d.sqrMagnitude > 1f) cut++;
                    }
            }
            Log("NavMesh: " + all + " carving obstacles; " + cut + " of " + big + " at least 4 x 4 m have no NavMesh at their centre.");
        }

        internal static void Draw()
        {
            if (!EastWorld.On || _label.Length == 0) return;
            if (_st == null) { _st = new GUIStyle(GUI.skin.label); _st.fontSize = 14; }
            GUIStyle st = _st;
            st.normal.textColor = Color.black;
            GUI.Label(new Rect(11f, 61f, 900f, 24f), _label, st);
            st.normal.textColor = new Color(1f, 0.92f, 0.55f);
            GUI.Label(new Rect(10f, 60f, 900f, 24f), _label, st);
        }

        static void Log(string s) { RevivalPlugin.L.LogInfo("EastZones: " + s); }
    }
}
