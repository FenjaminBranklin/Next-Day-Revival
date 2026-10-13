// Power 4: declared airfield lights per power site (data only, C# 3.0, ASCII).
// Frames: the building's own model/kit transform in its content scene
// (unity/EastTile/Content/east_af_*.json: holder at the recipe x/z/yaw, floor
// at y 0), the bunker root (east_af_generator.json) and the power poles of
// east_af_power (pole foot at the ground). Sources of the positions:
//   AF-GEN    pb-5 bunker (assets/src/power_bunker_blender.py), root on the
//             interior floor: south wall face z -3.5, blast door x 1.7..3.3,
//             door top 2.2, canopy 2.35..2.6, roof overhang from 3.2 m; the
//             lamp hangs over the canopy (pb-3's own door lamp beside the
//             frame stays an unlit fixture).
//   B1        Tools/b1_barracks.py: front wall z 9.0, door top 2.4, canopy
//             underside 2.87 m.
//   F1        east_af_f1.json colliders: portal header z 11.8 +- 0.2, portals
//             at x -14 / -6 / 2 (6.4 m), header 4.8..7.4 m.
//   G1        Tools/g1_gate.py: guard hut centre (17.86, -20.71), west wall face
//             x -4.0 of it, door s 0.55..1.55 north, door top 2.1 m.
//   H1        Tools/h1_hangar.py: gate wall z 20, 24 m gate 8 m high, eave 10 m.
//   C1        Revival.TowerRoofCore.cs: north stair foot (-10.9, 0, 9.65) m,
//             cab roof 15.142 m over x 4.3..11.6, z -3.6..3.6.
//   Poles     research/airfield_power_layout.json; collider 14 u, arms 16.6 u.
// Wall lamps are pressed onto the real wall by a short ray at bind time, roof
// beacons onto the real roof; a lamp whose ray finds no surface of its own
// scene is left out (logged), never left floating.
// A new site (Primorye later) is one more LightSite with its own lamps.
namespace NextDayRevival
{
    internal static class AirfieldLightsData
    {
        internal static readonly LightSite[] Sites = Build();

        static LampDecl Wall(string name, int kind, string scene, string anchor, float x, float y, float z, float yaw, bool real)
        {
            LampDecl l = new LampDecl();
            l.Name = name; l.Kind = kind; l.Mount = AirfieldLightsCore.Wall;
            l.Scene = scene; l.Anchor = anchor; l.X = x; l.Y = y; l.Z = z; l.Yaw = yaw; l.Real = real;
            return l;
        }

        static LampDecl Roof(string name, string scene, string anchor, float x, float y, float z)
        {
            LampDecl l = Wall(name, AirfieldLightsCore.Beacon, scene, anchor, x, y, z, 0f, false);
            l.Mount = AirfieldLightsCore.Roof;
            return l;
        }

        static LampDecl Pole(string pole, float height, float aimX, float aimZ)
        {
            LampDecl l = new LampDecl();
            l.Name = "Floodlight " + pole; l.Kind = AirfieldLightsCore.Flood; l.Mount = AirfieldLightsCore.Pole;
            l.Scene = "EastAfPower"; l.Anchor = "EastAfPowerRoot/main_airfield/" + pole;
            l.Y = height; l.AimX = aimX; l.AimZ = aimZ; l.Real = true;
            return l;
        }

        static LightSite[] Build()
        {
            const int D = AirfieldLightsCore.Door;
            LightSite main = new LightSite();
            main.Id = "main_airfield";
            main.X = 4220f; main.Z = 1330f;                 // the lit north cluster (bunker, B1, F1, tower)
            main.PropsScene = "EastAfProps";
            main.EdgeContainer = "EastAfPropsRoot/Runway/EdgeLights";
            main.EdgePrefix = "Edge light ";
            main.Lamps = new LampDecl[]
            {
                Wall("AF-GEN door", D, "EastAfGenerator", "EastAfGeneratorRoot/AF-GEN", 2.5f, 2.85f, -3.5f, 180f, true),
                Wall("B1 entrance", D, "EastAfB1", "EastAfB1Root/B1", 0f, 2.62f, 9.0f, 0f, true),
                Wall("F1 bay 1", D, "EastAfF1", "EastAfF1Root/F1", -14f, 5.3f, 12.0f, 0f, false),
                Wall("F1 bay 2", D, "EastAfF1", "EastAfF1Root/F1", -6f, 5.3f, 12.0f, 0f, false),
                Wall("F1 bay 3", D, "EastAfF1", "EastAfF1Root/F1", 2f, 5.3f, 12.0f, 0f, false),
                Wall("G1 guard hut", D, "EastAfG1", "EastAfG1Root/G1", 13.86f, 2.45f, -19.66f, -90f, false),
                Wall("H1 gate", D, "EastAfH1", "EastAfH1Root/H1", 0f, 8.6f, 20.0f, 0f, false),
                Wall("C1 stair foot", D, "EastAfC1", "EastAfC1Root/C1", -11.0f, 2.6f, 9.65f, 0f, true),
                Roof("C1 cab beacon", "EastAfC1", "EastAfC1Root/C1", 11.0f, 15.142f, 3.2f),
                Pole("P00", 4.6f, 4088f, 1222f),            // generator bunker door
                Pole("P03", 4.6f, 4116f, 1490f),            // B1 entrance
                Pole("PT1", 4.6f, 4320f, 1430f),            // D3 east end, AA-NE route
                Pole("PT2", 4.6f, 4290f, 1300f),            // between the tower and F1
            };
            return new LightSite[] { main };
        }
    }
}
