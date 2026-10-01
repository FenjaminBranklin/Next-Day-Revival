// Z F3: exact static rules, C# 3.0; shared by runtime and offline harness.
using System;

namespace NextDayRevival
{
    internal static class AirfieldObjectsCore
    {
        internal const float RadarScale = 0.24f, RadarLiftM = 3f;
        internal static readonly string[] RemovedBundles = {
            "b1", "d3", "f1", "g1", "h2", "scrapyard", "wrecks"
        };
        internal static readonly string[] Props = {
            "Windsock 0", "Windsock 1", "Maintenance ladder", "Ground power cart",
            "Sandbags S1", "Sandbags D2a"
        };
        internal static readonly string[] Fuel = {
            "pol_tank_horizontal 1", "pol_tank_horizontal 2",
            "pol_loading_stand 1", "fuel_bowser_trailer 1"
        };
        internal static readonly string[] Shelters = {
            "S1 shelter_ubs (south leaf jammed)", "S2 shelter_ubs_open", "S4 shelter_ubs_open",
            "AA position north", "AA position S2-S3", "AA position V3 (empty)"
        };
        internal static readonly string[] RemovedIds = {
            "H2", "F1", "B1", "D3", "G1", "G1a", "G1b", "G1c", "G1d", "G1e", "G1f", "G1g",
            "D1a", "D1b", "D1d", "S3", "V1", "V2", "V3", "M1", "M1a", "M1b", "M1c",
            "W1", "W1a", "W1b", "W1c", "W1d", "W1e", "W1f", "W1g", "W1h", "W1i",
            "W2a", "W2b", "W2c", "W2d", "P1", "G1p",
            "W1p1", "W1p2", "W1p3", "W1p4", "W1p5", "W1p6", "W1p7", "W1p8", "W1p9"
        };
        // Original baked tile holes; removed scene navigation is never loaded.
        internal static readonly float[][] Holes = {
            new float[] {4053.04f,1394.8f,4130.6f,1585.2f},
            new float[] {4177.4f,1405f,4342.6f,1475f},
            new float[] {4196.6f,1184.42f,4283.4f,1300.1f},
            new float[] {4052.4f,1608.8f,4175.6f,1743.2f},
            new float[] {4180.2f,783.8f,4339.8f,876.2f}
        };

        internal static bool SkipBundle(string file)
        {
            for (int i = 0; i < RemovedBundles.Length; i++)
                if (file == "east_af_" + RemovedBundles[i] + ".bundle") return true;
            return false;
        }

        internal static bool RemoveBase(string name)
        {
            for (int i = 0; i < RemovedIds.Length; i++)
                if (name == RemovedIds[i] || name.StartsWith(RemovedIds[i] + " ", StringComparison.Ordinal)
                    || name.StartsWith(RemovedIds[i] + "|", StringComparison.Ordinal)) return true;
            return name.StartsWith("D1 Fuel compound bund", StringComparison.Ordinal)
                || name == "Fences" || name == "L1|Loot: technical stores"
                || name == "L2|Loot: workshop" || name.StartsWith("L5|", StringComparison.Ordinal)
                || name.StartsWith("L6|", StringComparison.Ordinal)
                || name == "east_af_fuel_water";
        }

        // An assembly model is the direct root named in its source recipe.
        // Other category containers/mesh descendants are traversed normally.
        internal static bool RemoveModel(string scene, string name, bool model)
        {
            if (!model) return false;
            string[] keep = scene == "EastAfProps" ? Props : scene == "EastAfFuelWater" ? Fuel
                : scene == "EastAfShelters" ? Shelters : null;
            if (keep == null) return false;
            for (int i = 0; i < keep.Length; i++) if (name == keep[i]) return false;
            return true;
        }

        internal static bool Move(string name, out float x, out float z)
        {
            x = z = 0f;
            switch (name) {
                case "Windsock 0": x=4510f; z=-1240f; return true;
                case "Windsock 1": x=4510f; z=1200f; return true;
                case "Maintenance ladder": x=4360f; z=1065f; return true;
                case "Ground power cart": x=4370f; z=1065f; return true;
                case "pol_tank_horizontal 1": x=4100f; z=560f; return true;
                case "pol_tank_horizontal 2": x=4100f; z=578f; return true;
                case "pol_loading_stand 1": x=4137f; z=550f; return true;
                case "fuel_bowser_trailer 1": x=4150f; z=550f; return true;
                case "AA position S2-S3": x=4190f; z=-250f; return true;
                case "AA position V3 (empty)": x=4420f; z=-1180f; return true;
            }
            return false;
        }
    }
}
