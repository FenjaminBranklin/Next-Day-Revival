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
// P1b script budget: a caller's budget is capped at MaxSliceMs (callers asked
// for up to 1.5 ms a frame, which F6 showed as their module's average for the
// whole sweep), the clock is read every 8 nodes instead of every 64, and a
// node's children are pushed one per step through a (node, next child)
// cursor, so a root with thousands of children never overruns the slice.
// Same nodes, same order, same first match - spread over more frames.
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

        /// <summary>Upper bound of any caller's per-frame budget (P1b).</summary>
        public const double MaxSliceMs = 0.15;

        readonly List<Transform> _stack = new List<Transform>();
        readonly List<int> _next = new List<int>();     // -1: visit the node; k: push its child k next
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
            _next.Clear();
            _visited = 0;
            // Pushed in reverse so they pop in scene order, roots in order.
            for (int s = SceneManager.sceneCount - 1; s >= 0; s--)
            {
                Scene sc = SceneManager.GetSceneAt(s);
                if (!sc.isLoaded || !sc.name.StartsWith(scenePrefix, StringComparison.Ordinal)) continue;
                GameObject[] roots = sc.GetRootGameObjects();
                for (int r = roots.Length - 1; r >= 0; r--) { _stack.Add(roots[r].transform); _next.Add(-1); }
            }
            _active = true;
        }

        /// <summary>Visit nodes until the budget is spent. Returns true when the
        /// sweep is complete (and then <see cref="Active"/> is false).</summary>
        public bool Step(double budgetMs, Visitor visit)
        {
            if (!_active) return true;
            long start = Stopwatch.GetTimestamp();
            if (budgetMs > MaxSliceMs) budgetMs = MaxSliceMs;
            long budget = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            int n = 0;
            while (_stack.Count > 0)
            {
                int last = _stack.Count - 1;
                Transform t = _stack[last];
                int next = _next[last];
                _stack.RemoveAt(last);
                _next.RemoveAt(last);
                if (t == null) continue;               // destroyed since it was queued
                if (next >= 0)
                {
                    // Child 'next' now, the rest of t's children after its subtree.
                    int cc = t.childCount;
                    if (next >= cc) continue;
                    if (next + 1 < cc) { _stack.Add(t); _next.Add(next + 1); }
                    _stack.Add(t.GetChild(next));
                    _next.Add(-1);
                    continue;
                }
                _visited++;
                if (visit(t, t.name) && t.childCount > 0) { _stack.Add(t); _next.Add(0); }
                if ((++n & 7) == 0 && Stopwatch.GetTimestamp() - start > budget) return false;
            }
            _active = false;
            return true;
        }

        /// <summary>P1b: a number that changes whenever a scene loads or
        /// unloads - for scans that only need repeating then. No allocation.</summary>
        public static int LoadedSignature()
        {
            int sig = 17;
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene sc = SceneManager.GetSceneAt(s);
                if (sc.isLoaded) sig = sig * 31 + sc.GetHashCode();
            }
            return sig;
        }

        /// <summary>Drop a sweep in progress.</summary>
        public void Cancel() { _stack.Clear(); _next.Clear(); _active = false; }
    }
}
