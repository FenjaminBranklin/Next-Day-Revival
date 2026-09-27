// Next Day: Survival - Revival Toolkit
//
// BUILDING NAV: the runtime side of the walkable east content buildings
// (docs/ai/tasks/airfield-npc-nav.md; the editor side is unity/EastTile
// Assets/Editor/BuildingNav.cs). Acts only with [World] EastTile.
//
// A walkable building's content scene brings its own baked NavMesh (the
// tile's NavMesh has a hole there) and, under <root>/BuildingNav/<name>:
//   Links/*   OffMeshLinks: across the hole's edge to the tile and over the
//             steps at the doors - they join the building to the tile
//   Probes/*  interior points (every floor)
//   Posts/*   defender posts near the outer walls, rotation = looking out
// position = the hole's centre, localScale = its size.
//
// This file
// - re-registers the links once a new content scene is up (a link joins the
//   NavMesh that is loaded when it is enabled; the tile loads first, this
//   makes the order not matter),
// - logs once per load, 5 s later, per building: probes on the NavMesh and
//   reached from outside the hole, posts and links -
//     BuildingNav: H1 3/3 probes reached from outside, 12 posts, 140 links
// - hands a guard group whose home lies in or near a building (hole + 30 u)
//   that building's posts (NpcWar.GuardPosts) instead of a ring in the open.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class BuildingNav
    {
        const string RootName = "BuildingNav";
        const float Near = 30f;                 // a guard home this far outside the hole still takes the posts

        class Bld
        {
            public string name;
            public Transform tr;
            public Vector3 centre, half;
            public readonly List<Transform> posts = new List<Transform>();
            public readonly List<Transform> probes = new List<Transform>();
            public readonly List<OffMeshLink> links = new List<OffMeshLink>();
        }

        static readonly List<Bld> _b = new List<Bld>();
        static int _sceneCount = -1;
        static float _next, _checkAt;

        internal static void Tick()
        {
            if (!EastWorld.On) return;
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 1f;
            try
            {
                if (SceneManager.sceneCount != _sceneCount) Scan(now);
                if (_checkAt > 0f && now > _checkAt) { _checkAt = 0f; SelfCheck(); }
            }
            catch (System.Exception ex)
            {
                RevivalPlugin.L.LogWarning("BuildingNav: " + ex.Message);
                _next = now + 30f;
            }
        }

        static void Scan(float now)
        {
            _sceneCount = SceneManager.sceneCount;
            int before = _b.Count;
            _b.RemoveAll(delegate(Bld b) { return b.tr == null; });
            bool fresh = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                foreach (GameObject r in s.GetRootGameObjects())
                {
                    Transform nav = r.transform.Find(RootName);
                    if (nav == null) continue;
                    foreach (Transform t in nav)
                    {
                        if (t.Find("Posts") == null && t.Find("Links") == null) continue;
                        bool known = false;
                        foreach (Bld k in _b) if (k.tr == t) known = true;
                        if (known) continue;
                        _b.Add(Read(t));
                        fresh = true;
                    }
                }
            }
            if (fresh)
            {
                foreach (Bld b in _b)
                    foreach (OffMeshLink l in b.links)
                    {
                        if (l == null) continue;
                        l.enabled = false;          // re-registers on the NavMeshes loaded now
                        l.enabled = true;
                    }
                _checkAt = now + 5f;
            }
            else if (_b.Count != before) _checkAt = 0f;
        }

        static Bld Read(Transform t)
        {
            Bld b = new Bld();
            b.name = t.name;
            b.tr = t;
            b.centre = t.position;
            b.half = t.localScale * 0.5f;
            Transform p = t.Find("Posts");
            if (p != null) foreach (Transform c in p) b.posts.Add(c);
            Transform q = t.Find("Probes");
            if (q != null) foreach (Transform c in q) b.probes.Add(c);
            Transform l = t.Find("Links");
            if (l != null) b.links.AddRange(l.GetComponentsInChildren<OffMeshLink>(true));
            return b;
        }

        /// <summary>Per building: are its probes on the NavMesh and reached
        /// from the tile outside its hole (one of the four sides)?</summary>
        static void SelfCheck()
        {
            foreach (Bld b in _b)
            {
                if (b.tr == null) continue;
                int on = 0, reached = 0;
                foreach (Transform p in b.probes)
                {
                    if (p == null) continue;
                    NavMeshHit ph;
                    if (!NavMesh.SamplePosition(p.position, out ph, 1.5f, NavMesh.AllAreas)) continue;
                    on++;
                    if (FromOutside(b, ph.position)) reached++;
                }
                int active = 0;
                foreach (OffMeshLink l in b.links) if (l != null && l.activated) active++;
                RevivalPlugin.L.LogInfo("BuildingNav: " + b.name + " " + reached + "/" + b.probes.Count + " probes reached from outside ("
                    + on + " on the NavMesh), " + b.posts.Count + " posts, " + active + " links.");
            }
        }

        static bool FromOutside(Bld b, Vector3 goal)
        {
            Vector3[] dirs = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
            foreach (Vector3 d in dirs)
            {
                float reach = (d.x != 0f ? b.half.x : b.half.z) + 10f;
                Vector3 o = b.centre + d * reach;
                float y;
                if (!RevivalTroopInsertion.TerrainHeight(o, out y)) continue;
                o.y = y;
                NavMeshHit oh;
                if (!NavMesh.SamplePosition(o, out oh, 8f, NavMesh.AllAreas)) continue;
                NavMeshPath path = new NavMeshPath();
                if (!NavMesh.CalculatePath(oh.position, goal, NavMesh.AllAreas, path) || path.corners.Length == 0) continue;
                if (Vector3.Distance(path.corners[path.corners.Length - 1], goal) < 1.5f) return true;
            }
            return false;
        }

        /// <summary>Up to n posts of the building nearest to home when home
        /// lies in its hole or within 30 u of it: positions on the NavMesh
        /// and the direction each looks out. Spread over the building (every
        /// k-th post by distance from home), nearest building first. False
        /// when no building is near.</summary>
        internal static bool Posts(Vector3 home, int n, List<Vector3> pos, List<Vector3> look)
        {
            pos.Clear();
            look.Clear();
            if (!EastWorld.On || n <= 0) return false;
            Bld best = null;
            float bestD = float.MaxValue;
            foreach (Bld b in _b)
            {
                if (b.tr == null || b.posts.Count == 0) continue;
                float dx = Mathf.Max(0f, Mathf.Abs(home.x - b.centre.x) - b.half.x);
                float dz = Mathf.Max(0f, Mathf.Abs(home.z - b.centre.z) - b.half.z);
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d > Near) continue;
                float c = (home - b.centre).sqrMagnitude;
                if (c < bestD) { bestD = c; best = b; }
            }
            if (best == null) return false;
            List<Transform> ps = new List<Transform>();
            foreach (Transform t in best.posts) if (t != null) ps.Add(t);
            ps.Sort(delegate(Transform a, Transform c)
            {
                return (a.position - home).sqrMagnitude.CompareTo((c.position - home).sqrMagnitude);
            });
            int take = Mathf.Min(n, ps.Count);
            float stride = ps.Count / (float)take;
            for (int i = 0; i < take; i++)
            {
                Transform t = ps[Mathf.Min(ps.Count - 1, Mathf.FloorToInt(i * stride))];
                NavMeshHit h;
                Vector3 p = NavMesh.SamplePosition(t.position, out h, 1.5f, NavMesh.AllAreas) ? h.position : t.position;
                pos.Add(p);
                look.Add(t.forward);
            }
            return pos.Count > 0;
        }
    }
}
