// Power 4: airfield lights - rules and declarations, engine-free (C# 3.0,
// ASCII). Compiled with the runtime and by research/airfield_lights_check.py.
//
// A site declares its lamps as data (Revival.AirfieldLightsData.cs): wall
// lamps over doors, roof beacons and pole floodlights, each in the frame of a
// named transform of a content scene, plus the runway edge lights of a props
// scene. A lamp burns while the lights are enabled, it is night and the power
// site feeding "lights" has power (PowerNet, master-authoritative and synced).
// Only lamps flagged Real carry a real-time light, and only while the local
// player is near the site; every other lamp is an emissive lens.
using System;

namespace NextDayRevival
{
    /// <summary>One declared lamp. Positions are metres in the anchor's
    /// frame (x right, y up, z forward; world units = metres * 2.8).</summary>
    internal sealed class LampDecl
    {
        internal string Name;
        internal int Kind;            // AirfieldLightsCore.Door / Flood / Beacon
        internal int Mount;           // AirfieldLightsCore.Wall / Roof / Pole
        internal string Scene;        // content scene that holds the anchor
        internal string Anchor;       // "<scene root>/<child>/..." path of the anchor transform
        internal float X, Y, Z;       // mount point; Pole: Y is the height above the pole foot
        internal float Yaw;           // Wall/Roof: facing in the anchor frame (0 = +z, 90 = +x)
        internal float AimX, AimZ;    // Pole: world point (u) the floodlight looks at
        internal bool Real;           // carries a real-time light
    }

    /// <summary>The lights of one power site.</summary>
    internal sealed class LightSite
    {
        internal string Id;           // PowerSite.Id of the site that feeds them
        internal float X, Z;          // world centre (u) for the real-light distance test
        internal string PropsScene;   // scene with the runway edge lights (null: none)
        internal string EdgeContainer; // "<root>/<path>" holding the edge light models
        internal string EdgePrefix;   // model name prefix of an edge light
        internal LampDecl[] Lamps;
    }

    internal static class AirfieldLightsCore
    {
        internal const float K = 2.8f;

        internal const int Door = 0, Flood = 1, Beacon = 2, Edge = 3, KindCount = 4;
        internal static readonly string[] KindName = { "door", "flood", "beacon", "edge" };
        internal const int Wall = 0, Roof = 1, Pole = 2;

        // Night with hysteresis on the sun's elevation (sine) and intensity:
        // on below 0.06 or under 0.2 intensity, off again above 0.10 and 0.3.
        internal const float OnElevation = 0.06f, OffElevation = 0.10f;
        internal const float OnIntensity = 0.2f, OffIntensity = 0.3f;

        internal const float NearMetres = 700f;       // real lights only near the site
        internal const float StepSeconds = 2f;        // slow tick (night, distance)
        internal const int MaxReal = 8;               // real-time light budget per site
        internal const float WallProbeM = 1.2f;       // wall ray starts this far out, reaches as far in
        internal const float RoofProbeM = 2f;         // roof ray starts this far up

        /// <summary>Night from the sun: sunDirY is the y of the sun light's
        /// forward vector (negative while it shines down from above the
        /// horizon). Without a sun it stays as it was.</summary>
        internal static bool Night(bool was, bool haveSun, float sunDirY, float intensity)
        {
            if (!haveSun || float.IsNaN(sunDirY) || float.IsNaN(intensity)) return was;
            float elevation = -sunDirY;
            if (was) return !(elevation > OffElevation && intensity > OffIntensity);
            return elevation < OnElevation || intensity < OnIntensity;
        }

        /// <summary>A lamp burns: enabled, night and the feeding site powered.</summary>
        internal static bool Lit(bool enabled, bool night, bool powered)
        {
            return enabled && night && powered;
        }

        /// <summary>A real-time light is switched on only for a burning lamp
        /// that has one, with real lights allowed and the player near.</summary>
        internal static bool RealOn(bool lit, bool real, bool allowed, bool near)
        {
            return lit && real && allowed && near;
        }

        /// <summary>One int for the applied state: change detection only.</summary>
        internal static int State(bool lit, bool realOn)
        {
            return (lit ? 1 : 0) | (realOn ? 2 : 0);
        }

        /// <summary>A snapped edge light (its mesh is the "broken" variant)
        /// never burns.</summary>
        internal static bool Intact(string mesh)
        {
            return mesh == null || mesh.IndexOf("broken", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>Unity yaw (degrees, 0 = +z, 90 = +x) from a point to an aim point.</summary>
        internal static float AimYaw(float x, float z, float aimX, float aimZ)
        {
            float dx = aimX - x, dz = aimZ - z;
            if (dx * dx + dz * dz < 1e-6f) return 0f;
            return (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
        }

        /// <summary>The forward unit vector of a Unity yaw, x and z.</summary>
        internal static void Facing(float yaw, out float fx, out float fz)
        {
            double r = yaw * Math.PI / 180.0;
            fx = (float)Math.Sin(r); fz = (float)Math.Cos(r);
        }

        /// <summary>The scene root and the child path of an anchor path.</summary>
        internal static bool SplitAnchor(string anchor, out string root, out string child)
        {
            root = child = null;
            if (string.IsNullOrEmpty(anchor)) return false;
            int i = anchor.IndexOf('/');
            if (i == 0) return false;
            if (i < 0) { root = anchor; child = ""; return true; }
            root = anchor.Substring(0, i);
            child = anchor.Substring(i + 1);
            return child.Length > 0;
        }

        /// <summary>Real-time lights declared for a site.</summary>
        internal static int RealCount(LightSite site)
        {
            int n = 0;
            if (site == null || site.Lamps == null) return 0;
            for (int i = 0; i < site.Lamps.Length; i++) if (site.Lamps[i] != null && site.Lamps[i].Real) n++;
            return n;
        }

        /// <summary>A declaration problem, or null when the site is usable.</summary>
        internal static string Problem(LightSite site)
        {
            if (site == null || string.IsNullOrEmpty(site.Id)) return "site without id";
            if (site.Lamps == null) return site.Id + ": no lamp list";
            if (RealCount(site) > MaxReal) return site.Id + ": more than " + MaxReal + " real-time lights";
            if (site.PropsScene != null && (string.IsNullOrEmpty(site.EdgeContainer) || string.IsNullOrEmpty(site.EdgePrefix)))
                return site.Id + ": edge lights without container or prefix";
            for (int i = 0; i < site.Lamps.Length; i++)
            {
                LampDecl l = site.Lamps[i];
                string root, child;
                if (l == null || string.IsNullOrEmpty(l.Name)) return site.Id + ": lamp " + i + " has no name";
                if (string.IsNullOrEmpty(l.Scene) || !SplitAnchor(l.Anchor, out root, out child) || child.Length == 0)
                    return l.Name + ": scene/anchor path missing";
                if (l.Kind < Door || l.Kind > Beacon) return l.Name + ": unknown kind";
                if (l.Mount < Wall || l.Mount > Pole) return l.Name + ": unknown mount";
                if ((l.Mount == Pole) != (l.Kind == Flood)) return l.Name + ": floodlights (only) hang on poles";
                if (l.Y <= 0f || l.Y > 40f) return l.Name + ": mount height out of range";
                for (int j = 0; j < i; j++) if (site.Lamps[j].Name == l.Name) return l.Name + ": duplicate name";
            }
            return null;
        }
    }
}
