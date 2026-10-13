// Next Day: Survival - Revival Toolkit
//
// Power 5: sabotage points - the pure rules (no Unity, no game types), shared
// by the runtime (Revival.Sabotage.cs) and the headless test
// (tests/sabotage_test.cs via research/sabotage_check.py, csc 3.5).
//
// A sabotage point is one thing a C4 charge destroys at a power site: the
// generator, a fuel tank, the command post, the radar mast, an AA gun. Each
// is declared as data (Revival.SabotageData.cs) in a frame the runtime can
// resolve (the bunker, a depot tank, a gun, the tower): the target's box and
// one exact charge anchor on a face of that box - position, outward normal and
// the charge's long axis, the same pose C4.Orient gives a charge placed there
// by hand. Mercs use the anchors for the "Demolish" order later.
// Frame coordinates are metres; world offsets are game units (2.8 per metre).
//
// C# 3.0, ASCII.

using System;

namespace NextDayRevival
{
    internal sealed class SabotagePoint
    {
        internal string Key;                 // stable, unique: "<site>/<thing>"
        internal string Site;                // PowerSite.Id
        internal string Ref;                 // generator id, depot tank id, gun name ...
        internal string Label;               // admin overlay text
        internal int Kind, Frame;            // SabotageCore.Generator ... / SabotageCore.Bunker ...
        internal int Index = -1;             // gun index (Flak) or depot tank index; -1 none
        internal float Charges = 1f;         // C4 charges that destroy it
        internal float CX, CY, CZ;           // target box centre (frame metres)
        internal float HX, HY, HZ;           // target box half extents
        internal float AX, AY, AZ;           // charge anchor, on a face of the box
        internal float NX, NY, NZ;           // anchor normal, out of that face
        internal float TX, TY, TZ;           // charge long axis (C4.Orient: normal x up)
        internal float FloorY;               // where the person placing it stands (frame metres)
    }

    internal static class SabotageCore
    {
        internal const float K = 2.8f;

        internal const int Generator = 0, FuelTank = 1, CommandPost = 2, RadarMast = 3, AaGun = 4, KindCount = 5;
        internal static readonly string[] KindName = { "generator", "fuel_tank", "command_post", "radar_mast", "aa_gun" };

        /// <summary>Bunker: the site's bunker root (PowerSite X/Z/Yaw). Tank: a
        /// depot tank's base, world axes. Gun: the AA gun's root, its pit
        /// heading. Cab: tower x/z, y above the cab floor. Tower: the tower
        /// frame from its base (TowerRadar.TowerPoint).</summary>
        internal const int Bunker = 0, Tank = 1, Gun = 2, Cab = 3, Tower = 4, FrameCount = 5;
        internal static readonly string[] FrameName = { "bunker", "tank", "gun", "cab", "tower" };

        internal const float FaceTolerance = 0.02f;   // anchor to box face, metres
        internal const float MinReach = 0.05f, MaxReach = 2.0f;   // anchor above the floor, metres
        internal const float MaxCharges = 10f;

        static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }

        /// <summary>Is this point usable: known kind and frame, a real box, a
        /// unit normal along one box axis, the anchor on that face, a unit long
        /// axis equal to what C4.Orient builds, and in reach of a standing man.</summary>
        internal static bool Valid(SabotagePoint p, out string why)
        {
            why = null;
            if (p == null) { why = "null point"; return false; }
            if (string.IsNullOrEmpty(p.Key) || string.IsNullOrEmpty(p.Site) || string.IsNullOrEmpty(p.Label))
            { why = "key, site and label are required"; return false; }
            if (p.Kind < 0 || p.Kind >= KindCount) { why = "unknown kind"; return false; }
            if (p.Frame < 0 || p.Frame >= FrameCount) { why = "unknown frame"; return false; }
            float[] all = { p.Charges, p.CX, p.CY, p.CZ, p.HX, p.HY, p.HZ, p.AX, p.AY, p.AZ,
                            p.NX, p.NY, p.NZ, p.TX, p.TY, p.TZ, p.FloorY };
            for (int i = 0; i < all.Length; i++)
                if (!Finite(all[i])) { why = "non-finite value"; return false; }
            if (p.Charges <= 0f || p.Charges > MaxCharges) { why = "charges out of range"; return false; }
            if (p.HX <= 0f || p.HY <= 0f || p.HZ <= 0f) { why = "empty target box"; return false; }
            if (Math.Abs(Len(p.NX, p.NY, p.NZ) - 1f) > 1e-3f) { why = "normal is not a unit vector"; return false; }
            if (Math.Abs(Len(p.TX, p.TY, p.TZ) - 1f) > 1e-3f) { why = "long axis is not a unit vector"; return false; }
            int axis = Axis(p.NX, p.NY, p.NZ);
            if (axis < 0) { why = "normal is not along a box axis"; return false; }
            float sign = Comp(axis, p.NX, p.NY, p.NZ) > 0f ? 1f : -1f;
            float face = Comp(axis, p.CX, p.CY, p.CZ) + sign * Comp(axis, p.HX, p.HY, p.HZ);
            if (Math.Abs(Comp(axis, p.AX, p.AY, p.AZ) - face) > FaceTolerance) { why = "anchor is not on the face of its normal"; return false; }
            for (int a = 0; a < 3; a++)
            {
                if (a == axis) continue;
                if (Math.Abs(Comp(a, p.AX, p.AY, p.AZ) - Comp(a, p.CX, p.CY, p.CZ)) > Comp(a, p.HX, p.HY, p.HZ) + FaceTolerance)
                { why = "anchor is off the target's face"; return false; }
            }
            float tx, ty, tz;
            LongAxis(p.NX, p.NY, p.NZ, out tx, out ty, out tz);
            if (Math.Abs(tx - p.TX) > 1e-3f || Math.Abs(ty - p.TY) > 1e-3f || Math.Abs(tz - p.TZ) > 1e-3f)
            { why = "long axis differs from the hand-placed charge (C4.Orient)"; return false; }
            float above = p.AY - p.FloorY;
            if (above < MinReach || above > MaxReach) { why = "anchor is out of reach of a standing man"; return false; }
            if (p.FloorY > p.CY + p.HY) { why = "floor is above the target"; return false; }
            return true;
        }

        /// <summary>A whole table: every point valid, keys unique.</summary>
        internal static bool ValidAll(SabotagePoint[] points, out string why)
        {
            why = null;
            if (points == null) { why = "no table"; return false; }
            for (int i = 0; i < points.Length; i++)
            {
                if (!Valid(points[i], out why)) { why = (points[i] == null ? "#" + i : points[i].Key) + ": " + why; return false; }
                for (int j = 0; j < i; j++)
                    if (points[j].Key == points[i].Key) { why = points[i].Key + ": duplicate key"; return false; }
            }
            return true;
        }

        /// <summary>The long axis C4.Orient gives a charge on this normal:
        /// normal x up, or normal x forward on a floor/ceiling.</summary>
        internal static void LongAxis(float nx, float ny, float nz, out float tx, out float ty, out float tz)
        {
            // n x (0, 1, 0)
            tx = -nz; ty = 0f; tz = nx;
            if (tx * tx + tz * tz < 0.01f) { tx = ny; ty = -nx; tz = 0f; }   // n x (0, 0, 1)
            float l = Len(tx, ty, tz);
            if (l > 0f) { tx /= l; ty /= l; tz /= l; }
        }

        /// <summary>Distance (metres) from a frame point to the target box, 0 inside.</summary>
        internal static float BoxDistance(SabotagePoint p, float lx, float ly, float lz)
        {
            float dx = Math.Max(0f, Math.Abs(lx - p.CX) - p.HX);
            float dy = Math.Max(0f, Math.Abs(ly - p.CY) - p.HY);
            float dz = Math.Max(0f, Math.Abs(lz - p.CZ) - p.HZ);
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>World offset from the frame origin (units) to frame metres;
        /// Unity yaw turns +z toward +x.</summary>
        internal static void ToLocal(float yaw, float dx, float dy, float dz, out float lx, out float ly, out float lz)
        {
            double a = yaw * Math.PI / 180.0;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            lx = (dx * c - dz * s) / K;
            ly = dy / K;
            lz = (dx * s + dz * c) / K;
        }

        /// <summary>Frame metres to the world offset from the frame origin (units).</summary>
        internal static void ToWorld(float yaw, float lx, float ly, float lz, out float dx, out float dy, out float dz)
        {
            double a = yaw * Math.PI / 180.0;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            dx = (lx * c + lz * s) * K;
            dy = ly * K;
            dz = (-lx * s + lz * c) * K;
        }

        /// <summary>How many points of this kind the site declares.</summary>
        internal static int Count(SabotagePoint[] points, string site, int kind)
        {
            int n = 0;
            for (int i = 0; i < points.Length; i++)
                if (points[i] != null && points[i].Site == site && points[i].Kind == kind) n++;
            return n;
        }

        /// <summary>Demolition result for one chain: true when the summed
        /// charge shares reach the charges the point needs.</summary>
        internal static bool Demolished(float shares, float charges)
        {
            return Finite(shares) && shares >= Math.Max(0.1f, charges) - 1e-4f;
        }

        static float Len(float x, float y, float z) { return (float)Math.Sqrt(x * x + y * y + z * z); }

        static int Axis(float x, float y, float z)
        {
            float ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
            if (ax > 0.999f && ay < 1e-3f && az < 1e-3f) return 0;
            if (ay > 0.999f && ax < 1e-3f && az < 1e-3f) return 1;
            if (az > 0.999f && ax < 1e-3f && ay < 1e-3f) return 2;
            return -1;
        }

        static float Comp(int axis, float x, float y, float z) { return axis == 0 ? x : axis == 1 ? y : z; }
    }
}
