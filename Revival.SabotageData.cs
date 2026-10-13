// Power 5: the precomputed sabotage points of every power site (POWER.md
// "Power 5"). Boxes and anchors are measured from the shipped geometry, in
// metres in each point's frame (SabotageCore.Bunker ...):
// - generator: the pb-2 diesel set in the pb-5 bunker (assets/power_bunker/
//   genset/pb_genset_colliders.json PB_GS_COL_00_frame_engine_alt: frame,
//   engine and alternator, on the pad 0.15 m over the floor at plan
//   (-0.5, 0.3); the radiator is in share reach); the anchor is the
//   alternator end of its south face, east of the control panel, towards
//   the aisle, inside the bunker;
// - fuel tanks: FuelDepot's tank table and shell sizes (Revival.Fuel.cs,
//   BigR/BigH, SmallHalfX/SmallHalfZ/SmallH), the face towards the depot;
// - command post: the radar console cabinet of the command room
//   (TowerCommandRoomCore.RadarConsole), its left cabinet door;
// - radar mast: the field support under the antenna (AirfieldObjects.
//   CompactRadar, 0.24 m square, 3 m from the cab roof), its south face;
// - AA guns: the mount's pedestal at the gun root, the face along the pit
//   heading (FlakPositionsCore.Heading, towards the gate).
// research/sabotage_check.py compiles this table with the core and checks
// every anchor against those sources. A new site adds its own block here.
// C# 3.0, ASCII.
namespace NextDayRevival
{
    internal static class SabotageData
    {
        internal static readonly SabotagePoint[] Points = Build();

        static SabotagePoint[] Build()
        {
            const string S = "main_airfield";
            return new SabotagePoint[]
            {
                P(S, "generator", "AF-GEN", "AF-GEN generator", SabotageCore.Generator, SabotageCore.Bunker, -1, 2f,
                  -0.41f, 0.795f, 0.3f, 1.63f, 0.645f, 0.45f, 0.9f, 0.85f, -0.15f, 0f, 0f, -1f, 0f),
                Tank(S, "V1", 0, true, 1f), Tank(S, "V2", 1, true, 1f),
                Tank(S, "H1", 2, false, 1f), Tank(S, "H2", 3, false, -1f), Tank(S, "H3", 4, false, 1f),
                P(S, "command_post", "C1-console", "Command post (radar console)", SabotageCore.CommandPost, SabotageCore.Cab, -1, 1f,
                  7.9f, 0.39f, -3.01f, 0.63f, 0.33f, 0.29f, 7.59f, 0.30f, -2.72f, 0f, 0f, 1f, 0f),
                P(S, "radar_mast", "C1-radar", "Radar mast", SabotageCore.RadarMast, SabotageCore.Tower, -1, 1f,
                  7.9f, 15.142f + 1.5f, 0f, 0.12f, 1.5f, 0.12f, 7.9f, 15.142f + 1.0f, -0.12f, 0f, 0f, -1f, 15.142f),
                Gun(S, "AA-NW", 0, false), Gun(S, "AA-NE", 6, false), Gun(S, "AA-S", 1, false), Gun(S, "ZU-W", 5, true),
            };
        }

        /// <summary>The points of one site (a new array; call once at load).</summary>
        internal static SabotagePoint[] For(string site)
        {
            int n = 0;
            for (int i = 0; i < Points.Length; i++) if (Points[i].Site == site) n++;
            SabotagePoint[] a = new SabotagePoint[n];
            n = 0;
            for (int i = 0; i < Points.Length; i++) if (Points[i].Site == site) a[n++] = Points[i];
            return a;
        }

        // Depot tank shells (Revival.Fuel.cs): big vertical r 11.5 u, 26 u high;
        // small horizontal along world X, half 14.4 x 4.5 u, 11.4 u high.
        const float BigR = 11.5f / 2.8f, BigH = 26f / 2.8f;
        const float SmallX = 14.4f / 2.8f, SmallZ = 4.5f / 2.8f, SmallH = 11.4f / 2.8f;

        /// <summary>side: big tanks use their east side (+x, towards the depot
        /// yard); small ones +1 north / -1 south face.</summary>
        static SabotagePoint Tank(string site, string id, int index, bool big, float side)
        {
            if (big)
                return P(site, "tank_" + id, id, "Fuel tank " + id, SabotageCore.FuelTank, SabotageCore.Tank, index, 1f,
                         0f, BigH * 0.5f, 0f, BigR, BigH * 0.5f, BigR, BigR, 1.2f, 0f, 1f, 0f, 0f, 0f);
            return P(site, "tank_" + id, id, "Fuel tank " + id, SabotageCore.FuelTank, SabotageCore.Tank, index, 1f,
                     0f, SmallH * 0.5f, 0f, SmallX, SmallH * 0.5f, SmallZ, 0f, 1.5f, side * SmallZ, 0f, 0f, side, 0f);
        }

        /// <summary>The 52-K mount's pedestal (ZU-23: the smaller ZU mount).</summary>
        static SabotagePoint Gun(string site, string name, int gun, bool zu)
        {
            float h = zu ? 0.35f : 0.45f, y = zu ? 0.45f : 0.6f;
            return P(site, "gun_" + name, name, "AA gun " + name, SabotageCore.AaGun, SabotageCore.Gun, gun, 1f,
                     0f, y, 0f, h, y, h, 0f, y, h, 0f, 0f, 1f, 0f);
        }

        static SabotagePoint P(string site, string thing, string reference, string label, int kind, int frame, int index,
                               float charges, float cx, float cy, float cz, float hx, float hy, float hz,
                               float ax, float ay, float az, float nx, float ny, float nz, float floorY)
        {
            SabotagePoint p = new SabotagePoint();
            p.Key = site + "/" + thing; p.Site = site; p.Ref = reference; p.Label = label;
            p.Kind = kind; p.Frame = frame; p.Index = index; p.Charges = charges;
            p.CX = cx; p.CY = cy; p.CZ = cz; p.HX = hx; p.HY = hy; p.HZ = hz;
            p.AX = ax; p.AY = ay; p.AZ = az; p.NX = nx; p.NY = ny; p.NZ = nz;
            SabotageCore.LongAxis(nx, ny, nz, out p.TX, out p.TY, out p.TZ);
            p.FloorY = floorY;
            return p;
        }
    }
}
