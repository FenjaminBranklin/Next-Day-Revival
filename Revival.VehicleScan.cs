// Next Day: Survival - Revival Toolkit
//
// VehicleScan / NpcScan - the scene's VehicleGameSystem and NPC_AI2 components,
// kept in a REGISTRY instead of being searched for.
//
// History. The F6 overlay first showed ConvoyRepair.Tick and a drone tick in
// the red because several modules each ran their own FindObjectsOfType(
// VehicleGameSystem); VehicleScan hoisted that into one shared scan with a
// 0.25 s TTL. That was enough on the old map. It is not enough with the east
// tile loaded: FindObjectsOfType(SomeMonoBehaviour) walks EVERY MonoBehaviour
// in the scene and allocates the result, and the east tile, the airfield and
// the military town multiplied that number. In 6.57.0 the one shared scan cost
// 10-25 ms, four times a second, and whichever module asked first after the
// TTL paid for it - which is why the peaks in Kevin's overlay moved between
// ConvoyRepair.Tick (16 ms), DroneGear.Tick (25 ms) and Drone.Tick (14 ms).
// The NPC side was worse: Flak, TowerRadar, GepardCrew, TechnicalCrew,
// ArtyBattery and others each scanned NPC_AI2 on their own 0.5-1.5 s clock,
// none of it inside a measured bracket.
//
// Now. Harmony postfixes on the game's own lifecycle methods (Awake/OnEnable
// adds, OnDestroy removes) keep a live set; All() returns a snapshot of the
// members whose GameObject is active - the same thing FindObjectsOfType
// returned - rebuilt from that small set at most every TTL. No scene walk, and
// no allocation unless the set changed. Neither type has a subclass in
// Assembly-CSharp (checked against 6.57.0), so the base-class hooks see every
// instance.
//
// Safety nets: the first call runs ONE FindObjectsOfType to seed objects that
// existed before the hooks (the plugin patches before the first game scene, so
// this is normally empty); one more scan a minute later compares the registry
// with the scene, logs the result and adopts anything the hooks missed. If a
// hook cannot be installed the registry falls back to the old TTL scan.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. UTF-8 (no BOM), compiled with /codepage:65001.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// A live set of one game component type, fed by Harmony hooks on the
    /// type's own Awake/OnEnable/OnDestroy. <see cref="All"/> is the drop-in
    /// replacement for FindObjectsOfType(type).
    /// </summary>
    public sealed class SceneRegistry
    {
        const float TTL = 0.25f;
        const float VerifyAfter = 60f;
        static readonly Component[] Empty = new Component[0];
        static readonly List<SceneRegistry> _all = new List<SceneRegistry>();

        readonly string _typeName;
        Type _type;
        bool _typeTried;
        bool _hooked;
        bool _seeded;
        float _verifyAt = -1f;
        int _verifyTries;
        readonly Dictionary<int, Component> _live = new Dictionary<int, Component>();
        readonly List<int> _dead = new List<int>();
        readonly List<Component> _scratch = new List<Component>();
        Component[] _snapshot = Empty;
        float _until;
        bool _dirty = true;

        public SceneRegistry(string typeName) { _typeName = typeName; _all.Add(this); }

        /// <summary>The resolved game type, or null.</summary>
        public Type Type
        {
            get
            {
                if (!_typeTried) { _typeTried = true; _type = RevivalPlugin.TypeByName(_typeName); }
                return _type;
            }
        }

        /// <summary>How many live members the set holds (active or not).</summary>
        public int Count { get { return _live.Count; } }

        /// <summary>Patch the type's lifecycle methods. Called once from Awake.</summary>
        public void Install(Harmony h)
        {
            try
            {
                Type t = Type;
                if (t == null) { RevivalPlugin.L.LogWarning("SceneRegistry: " + _typeName + " not found - scan fallback."); return; }
                MethodInfo add = typeof(SceneRegistry).GetMethod("AddHook", BindingFlags.Static | BindingFlags.Public);
                MethodInfo remove = typeof(SceneRegistry).GetMethod("RemoveHook", BindingFlags.Static | BindingFlags.Public);
                int n = 0;
                foreach (string name in new string[] { "Awake", "OnEnable" })
                {
                    MethodInfo m = AccessTools.DeclaredMethod(t, name, Type.EmptyTypes, null);
                    if (m == null) continue;
                    h.Patch(m, null, new HarmonyMethod(add), null, null, null);
                    n++;
                }
                MethodInfo destroy = AccessTools.DeclaredMethod(t, "OnDestroy", Type.EmptyTypes, null);
                if (destroy != null) h.Patch(destroy, new HarmonyMethod(remove), null, null, null, null);
                _hooked = n > 0;
                RevivalPlugin.L.LogInfo("SceneRegistry: " + _typeName + " hooked (" + n
                    + " add hook(s), OnDestroy " + (destroy != null ? "yes" : "no") + ").");
            }
            catch (Exception ex)
            {
                _hooked = false;
                RevivalPlugin.L.LogWarning("SceneRegistry: " + _typeName + " hook failed, scan fallback: " + ex.Message);
            }
        }

        public static void AddHook(object __instance)
        {
            Component c = __instance as Component;
            if (c == null) return;
            for (int i = 0; i < _all.Count; i++)
            {
                SceneRegistry r = _all[i];
                if (r._type != null && r._type.IsInstanceOfType(c)) { r.Add(c); return; }
            }
        }

        public static void RemoveHook(object __instance)
        {
            Component c = __instance as Component;
            if ((object)c == null) return;
            for (int i = 0; i < _all.Count; i++)
            {
                SceneRegistry r = _all[i];
                if (r._type != null && r._type.IsInstanceOfType(c))
                {
                    if (r._live.Remove(c.GetInstanceID())) r._dirty = true;
                    return;
                }
            }
        }

        void Add(Component c)
        {
            int id = c.GetInstanceID();
            if (_live.ContainsKey(id)) return;
            _live[id] = c;
            _dirty = true;
        }

        /// <summary>Forces the next <see cref="All"/> to rebuild (after a module
        /// knows it has just spawned or destroyed a member).</summary>
        public void Touch() { _dirty = true; _until = 0f; }

        /// <summary>
        /// The members whose GameObject is active, at most TTL old - the result
        /// FindObjectsOfType(type) would give, without the scene walk. Never
        /// null. The array is shared: read it, do not store or mutate it.
        /// </summary>
        public Component[] All()
        {
            float now = Time.time;
            if (now < _until && !_dirty) return _snapshot;
            _until = now + TTL;
            try
            {
                Type t = Type;
                if (t == null) { _snapshot = Empty; return _snapshot; }
                if (!_hooked) return _snapshot = Scan(t);

                if (!_seeded)
                {
                    _seeded = true;
                    _verifyAt = now + VerifyAfter;
                    Adopt(Scan(t), "seed");
                }
                else if (_verifyAt > 0f && now >= _verifyAt)
                {
                    Component[] found = Scan(t);
                    if (found.Length == 0 && ++_verifyTries < 10)
                        _verifyAt = now + VerifyAfter;     // nothing to compare yet (menu): later
                    else
                    {
                        _verifyAt = -1f;
                        if (Adopt(found, "verify") > 0)
                        {
                            // The hooks miss instances: correctness first, the
                            // old TTL scan from now on.
                            _hooked = false;
                            RevivalPlugin.L.LogWarning("SceneRegistry: " + _typeName
                                + " falls back to the scene scan for this session.");
                            return _snapshot = Scan(t);
                        }
                    }
                }
                Rebuild();
                return _snapshot;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("SceneRegistry " + _typeName + ": " + ex.Message);
                _snapshot = Empty;
                return _snapshot;
            }
        }

        int Adopt(Component[] found, string why)
        {
            int missed = 0;
            for (int i = 0; i < found.Length; i++)
            {
                int id = found[i].GetInstanceID();
                if (_live.ContainsKey(id)) continue;
                _live[id] = found[i];
                missed++;
            }
            _dirty = true;
            RevivalPlugin.L.LogInfo("SceneRegistry: " + _typeName + " " + why + " scan found "
                + found.Length + ", registry had " + (_live.Count - missed) + ", adopted " + missed
                + (why == "verify" && missed > 0 ? " - the hooks MISSED some, check the patch" : "") + ".");
            return missed;
        }

        void Rebuild()
        {
            _dirty = false;
            _scratch.Clear();
            _dead.Clear();
            foreach (KeyValuePair<int, Component> kv in _live)
            {
                Component c = kv.Value;
                if (c == null) { _dead.Add(kv.Key); continue; }   // destroyed without OnDestroy
                if (!c.gameObject.activeInHierarchy) continue;
                _scratch.Add(c);
            }
            for (int i = 0; i < _dead.Count; i++) _live.Remove(_dead[i]);

            // Allocate only when the membership changed; readers hold the old
            // array for the rest of the frame at most.
            bool same = _scratch.Count == _snapshot.Length;
            for (int i = 0; same && i < _scratch.Count; i++)
                if (!ReferenceEquals(_scratch[i], _snapshot[i])) same = false;
            if (!same) _snapshot = _scratch.ToArray();
        }

        static Component[] Scan(Type t)
        {
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
            Component[] buf = new Component[all.Length];
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component;
                if (c == null) continue;
                buf[n++] = c;
            }
            if (n == buf.Length) return buf;
            Component[] trimmed = new Component[n];
            Array.Copy(buf, trimmed, n);
            return trimmed;
        }
    }

    /// <summary>
    /// The scene's VehicleGameSystem components (see the file comment). Call
    /// <see cref="All"/> from any per-frame path that needs the vehicle list.
    /// </summary>
    public static class VehicleScan
    {
        public static readonly SceneRegistry Registry = new SceneRegistry("VehicleGameSystem");

        public static void Install(Harmony h)
        {
            Registry.Install(h);
            NpcScan.Registry.Install(h);
            InventoryScan.Registry.Install(h);
        }

        /// <summary>Active VehicleGameSystem components. Never null; shared array.</summary>
        public static Component[] All() { return Registry.All(); }
    }

    /// <summary>
    /// The scene's NPC_AI2 components - the replacement for every module's own
    /// FindObjectsOfType(NPC_AI2). Same contract as <see cref="VehicleScan"/>.
    /// </summary>
    public static class NpcScan
    {
        public static readonly SceneRegistry Registry = new SceneRegistry("NPC_AI2");

        /// <summary>Active NPC_AI2 components. Never null; shared array.</summary>
        public static Component[] All() { return Registry.All(); }
    }

    /// <summary>
    /// The scene's PlayerInventoryManager components (every player's, local
    /// and remote) - for Turret.PlayerInventories and HasItem.
    /// </summary>
    public static class InventoryScan
    {
        public static readonly SceneRegistry Registry = new SceneRegistry("PlayerInventoryManager");

        /// <summary>Active PlayerInventoryManager components. Never null; shared array.</summary>
        public static Component[] All() { return Registry.All(); }
    }
}
