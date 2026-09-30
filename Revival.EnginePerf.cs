// Next Day: Survival - Revival Toolkit
//
// W PERF2 - THE ENGINE SHARE OF THE FRAME (tests/w_perf2_engine_check.py).
//
// Kevin's F6 after W Perf1: 27 ms frame, ~20 ms of it outside our measured
// calls (rendering, physics, vanilla scripts, NPC animation), "3 physics
// steps" seen. What the game files say (research/w_perf2_engine_scan.py):
//   - fixed timestep 0.02 s (RCCCarControllerV2.Start writes 0.02 again);
//     maximum allowed timestep 0.333 s: after a hitch Unity can run 16-17
//     physics steps (PhysX + every FixedUpdate, ours included) in ONE frame,
//     and that frame then becomes the next hitch. A 150 ms GC pause is
//     followed by 7-8 steps. This is a possible source of post-hitch spikes;
//     F6 alone does not identify the cost of the individual engine systems.
//   - shadows: 200 u with 4 (or 2) cascades from the game's menu; the static
//     GW_Scene_1 casters average ~343 draws across eight busy spots, the
//     terrains cast none, DynamicShadowSettings is placed in no scene - no
//     estimated cascade saving is only 0.12 ms (see the offline scan).
//   - animation: human NPC prefabs already cull their legacy Animation by
//     renderer (and NpcDistance thins the mid tier); every ANIMAL prefab
//     (wolf, deer, moose, boar, fox, rat, beaver, snake, calf) is
//     AlwaysAnimate - sampled and posed every frame behind the player. No
//     clip of the game has an AnimationEvent, so a culled pose changes
//     nothing but the pose nobody sees.
//   - left as the game has them, with the reason in the report: parked cars
//     (VehicleGameSystem.DisablePhys), the 693 barrels/crates
//     (DynamicObjectsManager), reflection probes (Custom), the Last
//     Survivor fog camera (inactive), NGUI, fixed timestep, autoSyncTransforms.
//
// WHAT THIS FILE DOES (each a [EnginePerf] key, on by default, live):
//   StepCap        Time.maximumDeltaTime = MaxPhysicsSteps x fixedDeltaTime
//                  (4 -> 0.08 s). A frame never runs more than that many
//                  physics steps; the game time a longer hitch loses is not
//                  simulated (the game runs 20 ms "slow" for a 100 ms hitch).
//                  Never raises the value, adopts another writer's value as
//                  its base, restores it at 0.
//   AnimalCulling  Animal_AI's Animation AlwaysAnimate -> BasedOnRenderers
//                  once per animal (lifecycle cache, no recurring scene scan); the
//                  crocodile boss is never touched; restored when switched off.
//   F6             slot EnginePerf.Tick and the "Engine:" line (step cap,
//                  animals culled, live NPCs).
//
// Cost: Tick is a clock compare per frame; the work runs once a second -
// a few float compares and cached animal passes, at most four new animation
// lookups per pass. Expected F6 cost <0.01 ms average, <0.2 ms warm peak for
// <=64 registered animals; estimates require F6 confirmation on Kevin's CPU.
// No steady-state Tick allocations. Startup seeding, spawns,
// config changes and F6 text refresh allocate outside the steady-state path.
//
// Seams: RevivalPlugin.Awake (BindConfig, Install), RevivalPlugin.Update
// (Tick, slot S_EnginePerfT), FrameProf overlay/log (StatusLine).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>Animal_AI lifecycle cache. Unlike the general SceneRegistry,
    /// there is no delayed verification scan or recurring scan fallback.</summary>
    public static class AnimalScan
    {
        static readonly List<Component> _live = new List<Component>();
        static Component[] _snapshot = new Component[0];
        static bool _dirty;

        public static int Count { get { return _live.Count; } }

        internal static void Install(Harmony h)
        {
            Type t = RevivalPlugin.TypeByName("Animal_AI");
            MethodInfo awake = t == null ? null : AccessTools.DeclaredMethod(t, "Awake", Type.EmptyTypes, null);
            MethodInfo destroy = t == null ? null : AccessTools.DeclaredMethod(t, "OnDestroy", Type.EmptyTypes, null);
            if (awake == null || destroy == null)
            {
                RevivalPlugin.L.LogWarning("EnginePerf: Animal_AI lifecycle missing; animal culling unavailable.");
                return;
            }
            h.Patch(awake, null, new HarmonyMethod(typeof(AnimalScan).GetMethod("AddHook")), null, null, null);
            h.Patch(destroy, new HarmonyMethod(typeof(AnimalScan).GetMethod("RemoveHook")), null, null, null, null);
            // Seed only during plugin Awake, before the world loads. All later
            // animals arrive through Awake; a hook mismatch never starts scans.
            UnityEngine.Object[] seed = UnityEngine.Object.FindObjectsOfType(t);
            for (int i = 0; i < seed.Length; i++) AddHook(seed[i]);
            RevivalPlugin.L.LogInfo("EnginePerf: Animal_AI lifecycle cached, startup seed " + _live.Count + ".");
        }

        public static void AddHook(object __instance)
        {
            Component c = __instance as Component;
            if (c != null && !_live.Contains(c)) { _live.Add(c); _dirty = true; }
        }

        public static void RemoveHook(object __instance)
        {
            Component c = __instance as Component;
            if ((object)c != null && _live.Remove(c)) _dirty = true;
        }

        /// <summary>Registered Animal_AI components. Never null; shared array.</summary>
        public static Component[] All()
        {
            if (_dirty)
            {
                _snapshot = _live.ToArray(); // membership changes only
                _dirty = false;
            }
            return _snapshot;
        }
    }

    internal static class EnginePerf
    {
        static ConfigEntry<int> _cfgSteps;
        static ConfigEntry<bool> _cfgAnimals;

        internal const int DefaultSteps = 2;
        internal const int MaxSteps = 16;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgSteps = cfg.Bind("EnginePerf", "MaxPhysicsSteps", DefaultSteps,
                "Most physics steps one frame may run to catch up after a hitch "
                + "(nonzero values are capped at 2; >=40 ms frames use 1). "
                + "Time.maximumDeltaTime = steps x fixed timestep (2 = 0.04 s). The game's "
                + "0.333 s lets a GC pause be followed by 16-17 steps, which makes the next "
                + "frame the next hitch. A frame longer than the cap loses the rest of its game "
                + "time instead. 0 = the game's value.");
            _cfgAnimals = cfg.Bind("EnginePerf", "AnimalCulling", true,
                "Animals (wolf, deer, moose, boar, fox, rat, beaver, snake) stop posing their "
                + "skeleton while no camera sees them, like the game's human NPCs already do. "
                + "Off = every animal animates every frame (the game's setting).");
        }

        internal static void Install(Harmony h)
        {
            try { AnimalScan.Install(h); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("EnginePerf: animal lifecycle hook failed: " + ex.Message); }
        }

        static float _next;

        /// <summary>Step cap each frame; animal work once a second.</summary>
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            StepCap();
            if (now < _next) return;
            _next = now + 1f;
            try
            {
                if (_cfgAnimals == null || _cfgAnimals.Value) CullAnimals();
                else RestoreAnimals();
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    RevivalPlugin.L.LogWarning("EnginePerf: " + ex.Message);
                }
            }
        }

        static bool _warned;

        /// <summary>Restore only settings still owned by this module.</summary>
        internal static void Shutdown()
        {
            if (_capped && Mathf.Abs(Time.maximumDeltaTime - _written) < 1e-5f)
                Time.maximumDeltaTime = _base;
            _capped = false;
            RestoreAnimals();
        }

        // ------------------------------------------------------------ step cap

        static bool _capped, _saidCap;
        static float _base = -1f;       // the value before ours (the game's 0.333)
        static float _written = -1f;    // what we wrote last

        /// <summary>The capped maximum delta time: steps x fixed step, never
        /// below one fixed step and never above the base value.</summary>
        internal static float CapFor(int steps, float fixedDt, float baseValue)
        {
            if (steps <= 0 || fixedDt <= 0f) return baseValue;
            if (steps > MaxSteps) steps = MaxSteps;
            float want = steps * fixedDt;
            if (want > baseValue) want = baseValue;
            if (want < fixedDt) want = fixedDt;
            return want;
        }

        static void StepCap()
        {
            int steps = _cfgSteps != null ? _cfgSteps.Value : DefaultSteps;
            steps = CombatLoadPolicy.Steps(steps, Time.unscaledDeltaTime);
            float cur = Time.maximumDeltaTime;
            if (steps <= 0)
            {
                if (_capped)
                {
                    if (Mathf.Abs(cur - _written) < 1e-5f) Time.maximumDeltaTime = _base;
                    _capped = false;
                    RevivalPlugin.L.LogInfo("EnginePerf: physics step cap off, maximumDeltaTime "
                        + Time.maximumDeltaTime.ToString("0.000", CultureInfo.InvariantCulture) + " s.");
                }
                return;
            }
            // First time, or somebody else wrote since: that value is the base.
            if (!_capped || Mathf.Abs(cur - _written) > 1e-5f) _base = cur;
            float want = CapFor(steps, Time.fixedDeltaTime, _base);
            if (Mathf.Abs(cur - want) > 1e-5f) Time.maximumDeltaTime = want;
            bool first = !_capped || Mathf.Abs(want - _written) > 1e-5f;
            _written = want;
            _capped = true;
            if (first && !_saidCap)
            {
                _saidCap = true;
                RevivalPlugin.L.LogInfo("EnginePerf: physics step cap " + steps + " (maximumDeltaTime "
                    + want.ToString("0.000", CultureInfo.InvariantCulture) + " s, game "
                    + _base.ToString("0.000", CultureInfo.InvariantCulture) + " s, fixed step "
                    + Time.fixedDeltaTime.ToString("0.000", CultureInfo.InvariantCulture) + " s).");
            }
        }

        // ------------------------------------------------------------ animals

        // Every Animal_AI seen once (instance id), and the Animations we
        // switched with their owner, so a switch-off restores exactly those.
        static readonly HashSet<int> _seen = new HashSet<int>();
        static readonly List<Animation> _culled = new List<Animation>();
        static readonly List<Component> _owner = new List<Component>();
        static FieldInfo _animField;
        static Type _animType;
        static int _loggedCulls;
        const int AnimalSetupsPerPass = 4;
        static int _animalCursor;

        static void CullAnimals()
        {
            // Dead entries out; a crocodile (attached after its Awake) back.
            for (int i = _culled.Count - 1; i >= 0; i--)
            {
                Animation a = _culled[i];
                Component o = _owner[i];
                if (a == null || o == null) { _culled.RemoveAt(i); _owner.RemoveAt(i); continue; }
                if (Crocodile.IsCrocAnimal(o))
                {
                    if (a.cullingType == AnimationCullingType.BasedOnRenderers)
                        a.cullingType = AnimationCullingType.AlwaysAnimate;
                    _culled.RemoveAt(i);
                    _owner.RemoveAt(i);
                }
            }
            if (_seen.Count > 4096) _seen.Clear();        // a very long session; ids are re-checked, harmless
            Component[] all = AnimalScan.All();
            int setups = 0;
            // Bound native component lookups after a wave of spawns. The cursor
            // also prevents uninitialized animals from starving later entries.
            for (int n = 0; n < all.Length && setups < AnimalSetupsPerPass; n++)
            {
                if (_animalCursor >= all.Length) _animalCursor = 0;
                int i = _animalCursor++;
                Component c = all[i];
                if (c == null) continue;
                if (_seen.Contains(c.GetInstanceID())) continue;
                if (Crocodile.IsCrocAnimal(c)) continue;
                setups++;
                Animation a = AnimOf(c);
                // Awake registration may precede the native animation setup.
                // Retry on the next pass instead of marking a null reference seen.
                if (a == null) continue;
                _seen.Add(c.GetInstanceID());
                if (a.cullingType != AnimationCullingType.AlwaysAnimate) continue;
                a.cullingType = AnimationCullingType.BasedOnRenderers;
                _culled.Add(a);
                _owner.Add(c);
                if (_loggedCulls < 3)
                {
                    _loggedCulls++;
                    RevivalPlugin.L.LogInfo("EnginePerf: animal '" + c.gameObject.name
                        + "' animates only while seen (was AlwaysAnimate).");
                }
            }
        }

        /// <summary>Animal_AI._anim (the game's own reference), else the first
        /// Animation under the animal. Once per animal.</summary>
        static Animation AnimOf(Component c)
        {
            Type t = c.GetType();
            if (t != _animType)
            {
                _animType = t;
                _animField = FastField.Find(t, "_anim");
            }
            Animation a = null;
            if (_animField != null)
            {
                try { a = _animField.GetValue(c) as Animation; } catch { a = null; }
            }
            if (a == null) a = c.GetComponentInChildren<Animation>();
            return a;
        }

        static void RestoreAnimals()
        {
            if (_culled.Count == 0 && _seen.Count == 0) return;
            for (int i = 0; i < _culled.Count; i++)
            {
                Animation a = _culled[i];
                if (a != null && a.cullingType == AnimationCullingType.BasedOnRenderers)
                    a.cullingType = AnimationCullingType.AlwaysAnimate;
            }
            RevivalPlugin.L.LogInfo("EnginePerf: animal culling off, " + _culled.Count + " restored.");
            _culled.Clear();
            _owner.Clear();
            _seen.Clear();
            _animalCursor = 0;
        }

        // ------------------------------------------------------------ F6

        /// <summary>One overlay line (built only while F6 is on, 4x/s).</summary>
        internal static string StatusLine()
        {
            int steps = _cfgSteps != null ? _cfgSteps.Value : DefaultSteps;
            steps = CombatLoadPolicy.Steps(steps, Time.unscaledDeltaTime);
            string cap = steps > 0 && _capped
                ? "step cap " + steps + " (max dt " + Time.maximumDeltaTime.ToString("0.000", CultureInfo.InvariantCulture)
                  + " s, game " + _base.ToString("0.000", CultureInfo.InvariantCulture) + ")"
                : "step cap off (max dt " + Time.maximumDeltaTime.ToString("0.000", CultureInfo.InvariantCulture) + " s)";
            return "Engine: " + cap + ", registered animals " + AnimalScan.Count + " (" + _culled.Count
                + " renderer-cull enabled), registered NPCs " + NpcScan.Registry.Count;
        }
    }
}
