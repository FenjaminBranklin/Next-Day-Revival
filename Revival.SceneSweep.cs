// Next Day: Survival - Revival Toolkit
//
// SceneSweep - a time-sliced walk over every transform of the loaded east
// scenes.
//
// Several east modules look for their objects by name (Flak's AA positions,
// the tower radar kit, the runway edge lights, the content ladders). They did
// it with one recursive walk of the WHOLE scene hierarchy per name, inside one
// frame, and repeated every few seconds until the object turned up. Reading
// Transform.name allocates a new string on every node, so each walk was both
// a long frame and a heap of garbage - with the east tile, the airfield and
// the military town loaded that is tens of thousands of nodes (Q1 perf).
//
// A sweep visits every node ONCE, hands the name to the caller's visitor (so
// one pass answers every name the caller wants), and stops after a time budget
// to continue next frame. The visit order is the same depth-first pre-order a
// recursive walk over the scenes' roots gives, so "the first match" is the same
// object as before.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. UTF-8 (no BOM), compiled with /codepage:65001.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    public sealed class SceneSweep
    {
        /// <summary>Called for every node; return false to skip its children.</summary>
        public delegate bool Visitor(Transform t, string name);

        readonly List<Transform> _stack = new List<Transform>();
        bool _active;
        int _visited;

        /// <summary>True between <see cref="Begin"/> and the Step that finishes.</summary>
        public bool Active { get { return _active; } }

        /// <summary>Nodes visited by the current (or last) sweep.</summary>
        public int Visited { get { return _visited; } }

        /// <summary>Queue the roots of every loaded scene whose name starts with
        /// <paramref name="scenePrefix"/> (ordinal).</summary>
        public void Begin(string scenePrefix)
        {
            _stack.Clear();
            _visited = 0;
            // Pushed in reverse so they pop in scene order, roots in order.
            for (int s = SceneManager.sceneCount - 1; s >= 0; s--)
            {
                Scene sc = SceneManager.GetSceneAt(s);
                if (!sc.isLoaded || !sc.name.StartsWith(scenePrefix, StringComparison.Ordinal)) continue;
                GameObject[] roots = sc.GetRootGameObjects();
                for (int r = roots.Length - 1; r >= 0; r--) _stack.Add(roots[r].transform);
            }
            _active = true;
        }

        /// <summary>Visit nodes until the budget is spent. Returns true when the
        /// sweep is complete (and then <see cref="Active"/> is false).</summary>
        public bool Step(double budgetMs, Visitor visit)
        {
            if (!_active) return true;
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            int n = 0;
            while (_stack.Count > 0)
            {
                int last = _stack.Count - 1;
                Transform t = _stack[last];
                _stack.RemoveAt(last);
                if (t == null) continue;               // destroyed since it was queued
                _visited++;
                if (visit(t, t.name))
                    for (int c = t.childCount - 1; c >= 0; c--) _stack.Add(t.GetChild(c));
                if ((++n & 63) == 0 && Stopwatch.GetTimestamp() - start > budget) return false;
            }
            _active = false;
            return true;
        }

        /// <summary>Drop a sweep in progress.</summary>
        public void Cancel() { _stack.Clear(); _active = false; }
    }
}
