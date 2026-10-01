// Next Day: Survival - Revival Toolkit
// F6-only renderer census. isVisible includes any camera and shadow passes;
// this is not a draw-call count (batching/materials/terrain change that).
// Reused lists, 2 Hz samples, 5 s topology refresh, 0.06 ms total slice.
// List growth on first discovery / new topology is outside steady state.
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class RenderCounter
    {
        const double SliceMs = 0.06;
        static GameObject _host;
        static List<Renderer> _cache = new List<Renderer>(16384);
        static List<Renderer> _pending = new List<Renderer>(16384);
        static readonly List<Renderer> _components = new List<Renderer>(8);
        static readonly List<GameObject> _roots = new List<GameObject>(4096);
        static readonly List<Transform> _stack = new List<Transform>(256);
        static readonly List<int> _child = new List<int>(256);
        static int _scene, _root, _sample = -1, _active, _visible;
        static bool _walking, _persistent, _haveSample, _haveCensus;
        static float _discoverAt, _sampleAt, _finishedAt;
        static int _lastActive, _lastVisible;

        internal static void Bind(GameObject host) { _host = host; }

        internal static void Reset()
        {
            _cache.Clear(); _pending.Clear(); _roots.Clear();
            _stack.Clear(); _child.Clear();
            _walking = _haveSample = _haveCensus = false;
            _sample = -1; _discoverAt = _sampleAt = 0f;
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(SliceMs * Stopwatch.Frequency / 1000.0);
            if (_sample < 0 && now >= _sampleAt)
            {
                _sample = _active = _visible = 0;
            }
            // Finish a sample against one stable cache before swapping it.
            while (_sample >= 0 && Stopwatch.GetTimestamp() - start < budget)
            {
                if (_sample >= _cache.Count)
                {
                    _lastActive = _active; _lastVisible = _visible;
                    _haveSample = true; _finishedAt = now;
                    _sample = -1; _sampleAt = now + 0.5f;
                    break;
                }
                Renderer r = _cache[_sample++];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                _active++;
                if (r.isVisible) _visible++;
            }
            if (!_walking && now >= _discoverAt)
            {
                _pending.Clear(); _roots.Clear(); _stack.Clear(); _child.Clear();
                _scene = _root = 0; _persistent = false; _walking = true;
            }
            while (_walking && Stopwatch.GetTimestamp() - start < budget)
            {
                if (_stack.Count > 0) { Visit(); continue; }
                if (_root < _roots.Count)
                {
                    GameObject go = _roots[_root++];
                    if (go != null) { _stack.Add(go.transform); _child.Add(-1); }
                    continue;
                }
                if (NextScene()) continue;
                // No duplicate scene walk: persistent host scene is skipped
                // when SceneManager already included it.
                if (_sample >= 0) break;
                List<Renderer> old = _cache; _cache = _pending; _pending = old;
                _haveCensus = true;
                _walking = false; _discoverAt = now + 5f; _sampleAt = 0f;
            }
        }

        static bool NextScene()
        {
            _roots.Clear(); _root = 0;
            while (_scene < SceneManager.sceneCount)
            {
                Scene s = SceneManager.GetSceneAt(_scene++);
                if (_host != null && s == _host.scene) _persistent = true;
                if (!s.isLoaded) continue;
                Roots(s);
                return true;
            }
            if (!_persistent && _host != null)
            {
                _persistent = true;
                Scene s = _host.scene;
                if (s.IsValid() && s.isLoaded) { Roots(s); return true; }
            }
            return false;
        }

        static void Roots(Scene s)
        {
            // Unity's list overload requires spare capacity for rootCount.
            // Growth occurs only when topology exceeds the previous capacity.
            int count = s.rootCount;
            if (_roots.Capacity <= count) _roots.Capacity = count + 256;
            s.GetRootGameObjects(_roots);
        }

        static void Visit()
        {
            int last = _stack.Count - 1;
            Transform t = _stack[last];
            if (t == null) { _stack.RemoveAt(last); _child.RemoveAt(last); return; }
            int next = _child[last];
            if (next < 0)
            {
                _components.Clear();
                t.GetComponents<Renderer>(_components);
                for (int i = 0; i < _components.Count; i++) _pending.Add(_components[i]);
                _child[last] = 0;
                return;
            }
            if (next < t.childCount)
            {
                _child[last] = next + 1;
                _stack.Add(t.GetChild(next)); _child.Add(-1);
                return;
            }
            _stack.RemoveAt(last); _child.RemoveAt(last);
        }

        // Formatting is only requested by the existing 4 Hz F6 text builder.
        internal static string StatusLine()
        {
            if (!_haveSample || !_haveCensus) return "Renderers: census warming up (not draw calls)";
            return string.Format("Renderers visible {0} / active {1} / cached {2}, age {3:0.0}s (engine; not draws)",
                _lastVisible, _lastActive, _cache.Count, Time.unscaledTime - _finishedAt);
        }
    }
}
