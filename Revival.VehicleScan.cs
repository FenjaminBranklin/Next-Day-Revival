// Next Day: Survival - Revival Toolkit
//
// VehicleScan - one shared, short-lived cache of the scene's VehicleGameSystem
// components. It exists to ANSWER a measured FPS drop: the F6 overlay showed
// ConvoyRepair.Tick and a drone tick in the red (>1 ms/frame). The cause was
// that several modules each ran their OWN UnityEngine.Object.FindObjectsOfType(
// VehicleGameSystem) on their own throttle - FindObjectsOfType walks the whole
// scene and allocates, so a handful of independent scans per second is a string
// of little hitches. Each module already threw its result away and rescanned;
// none shared. This hoists that one expensive call to a single cache the modules
// read from, so the whole-scene scan runs at most a few times a second in total
// instead of once per module.
//
// The cache holds the live VehicleGameSystem components for a fraction of a
// second (TTL). The first caller in a window pays for the scan; every other
// caller in that window reuses the array for free. TTL is deliberately as short
// as the tightest previous per-module throttle (ConvoyRepair's 0.25 s wreck
// scan), so no reader sees data staler than it did before. Destroyed objects are
// filtered out when the array is built; the rare object destroyed mid-window is
// caught by the callers' existing Unity null checks.
//
// Self-contained: no shared state beyond this class, so a merge folds it in
// beside the other branches. C# 3.0 (csc from .NET 3.5): no optional arguments,
// no expression-tree lambdas. UTF-8 (no BOM), compiled with /codepage:65001.

using System;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// A shared, TTL-cached snapshot of the scene's VehicleGameSystem components.
    /// Call <see cref="All"/> from any per-frame path that needs the vehicle list;
    /// the underlying whole-scene FindObjectsOfType runs at most once per TTL no
    /// matter how many modules ask. Never throws into the frame loop.
    /// </summary>
    public static class VehicleScan
    {
        // As short as the tightest previous per-module throttle (0.25 s), so no
        // reader gets a staler list than it did before the cache existed.
        const float TTL = 0.25f;

        static readonly Component[] Empty = new Component[0];
        static Component[] _cache = Empty;
        static float _until;            // Time.time at which _cache goes stale
        static Type _type;              // resolved VehicleGameSystem type, once
        static bool _typeTried;

        /// <summary>
        /// The scene's VehicleGameSystem components, at most <c>TTL</c> old.
        /// Returns an empty array (never null) if the type is unknown or the scan
        /// fails. The array is shared - read it, do not store or mutate it.
        /// </summary>
        public static Component[] All()
        {
            if (Time.time < _until) return _cache;
            _until = Time.time + TTL;

            try
            {
                if (!_typeTried)
                {
                    _typeTried = true;
                    _type = RevivalPlugin.TypeByName("VehicleGameSystem");
                }
                if (_type == null) { _cache = Empty; return _cache; }

                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_type);
                Component[] buf = new Component[all.Length];
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    // Unity's overloaded == treats a destroyed object as null, so
                    // this drops any that died since the last scan.
                    Component c = all[i] as Component;
                    if (c == null) continue;
                    buf[n] = c;
                    n++;
                }
                if (n == buf.Length) { _cache = buf; return _cache; }
                Component[] trimmed = new Component[n];
                Array.Copy(buf, trimmed, n);
                _cache = trimmed;
                return _cache;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("VehicleScan: " + ex.Message);
                _cache = Empty;
                return _cache;
            }
        }
    }
}
