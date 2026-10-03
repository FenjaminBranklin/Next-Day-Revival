// G R2 (was Z TC1): the C1 cab as a Soviet air defence command post.
// Deterministic recipe in real metres, tower frame (x east, z north, y up
// from the cab floor). Pure C# 3.0: the game's runtime build
// (Revival.TowerCommandRoom.cs), the kit build (unity/EastTile/Tools/
// c1_room.py) and the offline proof (research/tower_command_room_check.py)
// all compile this exact recipe, not a copy.
using System.Collections.Generic;

namespace NextDayRevival
{
    internal static class TowerCommandRoomCore
    {
        // Surfaces. Kit build: vanilla sheets (c1_room.py KIT_OF); runtime: the
        // game's materials found on the tower (TowerCommandRoom.Look). Paper,
        // Screen and Lamp are the room's own atlases (assets/c1_room_*.png).
        internal const int Grey = 0, Olive = 1, Steel = 2, Wood = 3, Black = 4,
            Paper = 5, Lamp = 6, Screen = 7, Glass = 8, Materials = 9;
        // Compatibility with the existing runtime builder until its kit/atlas
        // integration lands. The new recipe never emits this legacy surface.
        internal const int Canvas = 9;
        internal const int Box = 0, CylY = 1, CylZ = 2, CylX = 3;

        // Atlas regions: x0, y0, x1, y1 as image fractions, y down. Paper
        // regions live in c1_room_papers.png, RPpi.. in c1_room_screens.png.
        internal const int RNone = 0, RMap = 1, RWallMap = 2, RFreq = 3, RRoster = 4, RNotes = 5,
            RClock = 6, RGauge = 7, RFolder = 8, RPanel = 9, RLog = 10, RRadio = 11, RLabel = 12,
            RTape = 13, RPlot = 14, RSign = 15, RCalendar = 16, RBook = 17, RPack = 18, RDial = 19,
            RPpi = 20, RAScope = 21, RRepeater = 22, Regions = 23;
        internal static readonly float[] Region = {
            0f, 0f, 1f, 1f,               // none
            0f, 0f, .5f, .41f,            // plotting map
            .5f, 0f, 1f, .36f,            // wall map
            .5f, .36f, .75f, .66f,        // frequency table
            .75f, .36f, 1f, .66f,         // duty roster
            0f, .41f, .25f, .66f,         // operator memo
            .25f, .41f, .375f, .535f,     // clock face
            .375f, .41f, .5f, .535f,      // gauge face
            .25f, .535f, .375f, .66f,     // folder cover
            .375f, .535f, .5f, .66f,      // switch panel
            0f, .66f, .125f, .83f,        // typed log sheet
            .125f, .66f, .375f, .79f,     // radio front
            .125f, .79f, .375f, .83f,     // label strip
            .375f, .66f, .5f, .83f,       // teleprinter tape
            .5f, .66f, .84f, 1f,          // plotting board grid (cut out)
            .84f, .66f, 1f, .72f,         // supply terminal plate
            .84f, .72f, 1f, .92f,         // calendar
            0f, .83f, .125f, 1f,          // journal cover
            .125f, .83f, .25f, .91f,      // cigarette pack
            .25f, .83f, .375f, .955f,     // telephone dial
            0f, 0f, .5f, 1f,              // PPI (screens atlas)
            .5f, 0f, .75f, .5f,           // A-scope
            .75f, 0f, 1f, .5f };          // repeater PPI

        // Z M4 integration seam: the vanilla east desk is retained. Approach
        // from the west; do not put furniture/chairs in this reservation.
        internal const float OrderX = 10.65f, OrderZ = 0f;
        internal const float OrderApproachX = 9.35f;
        // G R3: the supply order desk (TowerDelivery.Candidates[0]) between the
        // radio desk (x <= 6.125) and the radar console (x >= 7.25), its
        // front in line with theirs; its collider is W x H x D metres.
        internal const float SupplyX = 6.65f, SupplyZ = -2.85f;
        internal const float SupplyW = .90f, SupplyH = 1.1f, SupplyD = .50f;
        // The radar console (= TowerRoofCore.ConsoleX/Z; its desk collider,
        // [G] and damage box stay TowerRadar's). Its operator sits 0.85 m north.
        internal const float RadarX = 7.9f, RadarZ = -2.95f, SeatDZ = .85f, SeatH = .45f;
        // The second scope position (height finder) by the south window.
        internal const float ScopeX = 9.65f, ScopeZ = -3.0f;

        internal struct Piece
        {
            internal string Name;
            internal float X, Y, Z, SX, SY, SZ, Tilt, Yaw;
            internal int Material, Shape, Region;
            internal bool Solid;
            internal bool RadarPart;
            // The old runtime builder still reads this flag. No bags remain;
            // every piece keeps the default false value.
            internal bool Bag;
        }

        static void Add(List<Piece> p, string n, int shape, float x, float y, float z,
            float sx, float sy, float sz, int m, int region, float tilt, float yaw)
        {
            Piece q = new Piece();
            q.Name = n; q.Shape = shape; q.X = x; q.Y = y; q.Z = z; q.SX = sx; q.SY = sy; q.SZ = sz;
            q.Material = m; q.Region = region; q.Tilt = tilt; q.Yaw = yaw;
            p.Add(q);
        }

        static void B(List<Piece> p, string n, float x, float y, float z, float sx, float sy, float sz, int m)
        { Add(p, n, Box, x, y, z, sx, sy, sz, m, RNone, 0f, 0f); }

        static void BR(List<Piece> p, string n, float x, float y, float z, float sx, float sy, float sz,
            int m, float tilt, float yaw)
        { Add(p, n, Box, x, y, z, sx, sy, sz, m, RNone, tilt, yaw); }

        static void Sheet(List<Piece> p, string n, float x, float y, float z, float sx, float sy, float sz,
            int region, float yaw)
        { Add(p, n, Box, x, y, z, sx, sy, sz, Paper, region, 0f, yaw); }

        static void Cy(List<Piece> p, string n, float x, float y, float z, float d, float h, int m)
        { Add(p, n, CylY, x, y, z, d, h, d, m, RNone, 0f, 0f); }

        static void Cz(List<Piece> p, string n, float x, float y, float z, float d, float len, int m, int region)
        { Add(p, n, CylZ, x, y, z, d, d, len, m, region, 0f, 0f); }

        static void Cx(List<Piece> p, string n, float x, float y, float z, float d, float len, int m, int region)
        { Add(p, n, CylX, x, y, z, len, d, d, m, region, 0f, 0f); }

        static void Collider(List<Piece> p, string n, float x, float y, float z, float sx, float sy, float sz)
        {
            Piece q = new Piece();
            q.Name = n; q.X = x; q.Y = y; q.Z = z; q.SX = sx; q.SY = sy; q.SZ = sz;
            q.Material = Black; q.Solid = true;
            p.Add(q);
        }

        /// <summary>Sides of a cylinder piece: the round screens and the
        /// clock get 16, knobs 6. Both builders use this rule.</summary>
        internal static int Sides(Piece q)
        {
            float d = q.Shape == CylX ? q.SY : q.SX;
            return d >= .2f ? 16 : d >= .06f ? 10 : 6;
        }

        // ------------------------------------------------------------ furniture

        /// <summary>A Soviet office desk: wooden top and aprons, steel legs,
        /// a drawer pedestal on the right of the man at side f (+1 north).</summary>
        static void Desk(List<Piece> p, string n, float x, float z, float sx, float sz, float f, bool pedestal)
        {
            B(p, n + " top", x, .7625f, z, sx, .035f, sz, Wood);
            for (int a = -1; a <= 1; a += 2)
            {
                B(p, n + " apron", x, .70f, z + a * (sz * .5f - .04f), sx - .1f, .08f, .02f, Wood);
                for (int b = -1; b <= 1; b += 2)
                    B(p, n + " leg", x + a * (sx * .5f - .05f), .3725f, z + b * (sz * .5f - .05f), .045f, .745f, .045f, Steel);
            }
            if (pedestal)
            {
                float px = x - f * (sx * .5f - .24f);
                B(p, n + " drawer pedestal", px, .38f, z, .40f, .66f, sz - .08f, Wood);
                for (int i = 0; i < 3; i++)
                    B(p, n + " drawer pull", px, .58f - i * .20f, z + f * (sz * .5f - .03f), .10f, .015f, .02f, Steel);
            }
            Collider(p, n, x, .39f, z, sx, .78f, sz);
        }

        /// <summary>A wooden office chair; the man faces south when the
        /// back is north (facesNorth false).</summary>
        static void Chair(List<Piece> p, string n, float x, float z, bool facesNorth, bool solid)
        {
            float back = facesNorth ? -.19f : .19f;
            B(p, n + " seat", x, SeatH, z, .42f, .04f, .42f, Wood);
            for (int a = -1; a <= 1; a += 2)
            {
                for (int b = -1; b <= 1; b += 2)
                    B(p, n + " leg", x + a * .17f, .215f, z + b * .17f, .035f, .43f, .035f, Wood);
                B(p, n + " back post", x + a * .17f, .69f, z + back, .035f, .45f, .03f, Wood);
                B(p, n + " stretcher", x + a * .17f, .14f, z, .025f, .025f, .34f, Wood);
            }
            B(p, n + " backrest", x, .80f, z + back, .40f, .15f, .025f, Wood);
            B(p, n + " back rail", x, .60f, z + back, .36f, .04f, .02f, Wood);
            if (solid) Collider(p, n, x, .49f, z, .43f, .98f, .48f);
        }

        /// <summary>TA-57 field telephone: olive case, black handset, cord.</summary>
        static void FieldPhone(List<Piece> p, float x, float y, float z, float yaw)
        {
            BR(p, "TA-57 field telephone", x, y + .045f, z, .23f, .09f, .17f, Olive, 0f, yaw);
            BR(p, "TA-57 handset", x, y + .11f, z, .25f, .035f, .045f, Black, 0f, yaw);
            BR(p, "TA-57 handset grip", x, y + .10f, z, .10f, .03f, .03f, Black, 0f, yaw);
            BR(p, "TA-57 crank", x + .125f, y + .05f, z + .05f, .02f, .02f, .05f, Steel, 0f, yaw);
            for (int i = 0; i < 4; i++)
                B(p, "telephone cord", x - .14f, y + .006f, z - .06f + i * .035f, .03f, .012f, .025f, Black);
        }

        /// <summary>Black desk telephone with a rotary dial.</summary>
        static void DeskPhone(List<Piece> p, float x, float y, float z)
        {
            B(p, "desk telephone", x, y + .04f, z, .19f, .08f, .21f, Black);
            BR(p, "desk telephone front", x, y + .07f, z - .07f, .17f, .02f, .09f, Black, -18f, 0f);
            Add(p, "rotary dial", CylY, x, y + .087f, z - .065f, .085f, .006f, .085f, Paper, RDial, -18f, 0f);
            B(p, "desk telephone handset", x, y + .115f, z + .02f, .22f, .035f, .05f, Black);
            for (int i = 0; i < 3; i++)
                B(p, "telephone cord", x + .12f, y + .006f, z + .05f + i * .03f, .02f, .012f, .025f, Black);
        }

        /// <summary>Soviet desk lamp: weighted foot, stem, arm, enamel shade
        /// with a warm diffuser under it.</summary>
        static void DeskLamp(List<Piece> p, float x, float y, float z, float dir)
        {
            Cy(p, "lamp foot", x, y + .015f, z, .14f, .03f, Black);
            B(p, "lamp stem", x, y + .21f, z, .022f, .38f, .022f, Steel);
            B(p, "lamp arm", x + dir * .07f, y + .40f, z, .16f, .02f, .02f, Steel);
            Cy(p, "enamel shade", x + dir * .15f, y + .37f, z, .17f, .09f, Olive);
            Cy(p, "warm diffuser", x + dir * .15f, y + .323f, z, .15f, .004f, Lamp);
        }

        /// <summary>Tea glass in a metal holder, a spoon in it.</summary>
        static void TeaGlass(List<Piece> p, float x, float y, float z)
        {
            Cy(p, "glass holder", x, y + .035f, z, .074f, .07f, Steel);
            B(p, "glass holder handle", x + .05f, y + .045f, z, .025f, .05f, .012f, Steel);
            Cy(p, "tea glass", x, y + .065f, z, .066f, .10f, Glass);
            Cy(p, "strong tea", x, y + .055f, z, .060f, .07f, Wood);
            BR(p, "spoon", x - .01f, y + .10f, z, .006f, .13f, .01f, Steel, 0f, 0f);
        }

        static void Ashtray(List<Piece> p, float x, float y, float z)
        {
            Cy(p, "ashtray", x, y + .0125f, z, .11f, .025f, Steel);
            Cy(p, "ashes", x, y + .026f, z, .085f, .003f, Black);
            Add(p, "papirosa butt", Box, x + .02f, y + .03f, z, .045f, .009f, .009f, Paper, RLog, 0f, 30f);
            Add(p, "papirosa butt", Box, x - .015f, y + .03f, z + .02f, .04f, .009f, .009f, Paper, RLog, 0f, -50f);
        }

        static void Pencil(List<Piece> p, float x, float y, float z, float yaw)
        { BR(p, "pencil", x, y + .004f, z, .16f, .008f, .008f, Wood, 0f, yaw); }

        static void Headphones(List<Piece> p, float x, float y, float z)
        {
            for (int a = -1; a <= 1; a += 2)
                Cy(p, "headphone cup", x + a * .085f, y + .0175f, z, .085f, .035f, Black);
            B(p, "headphone band", x, y + .03f, z + .055f, .19f, .012f, .02f, Steel);
            for (int i = 0; i < 4; i++)
                B(p, "headphone cord", x - .085f, y + .006f, z - .06f - i * .045f, .015f, .012f, .05f, Black);
        }

        // ------------------------------------------------------------ positions

        /// <summary>The PPI console: grey cabinet, writing ledge, sloped
        /// control shelf, the round screen in its hood (renderer "Screen"),
        /// gauges, knobs, switches, lamps, cables to the wall; the operator's
        /// chair north of it. Front = north.</summary>
        static void RadarConsole(List<Piece> p)
        {
            const float x = RadarX, z = RadarZ;
            B(p, "console plinth", x, .03f, z - .05f, 1.26f, .06f, .60f, Steel);
            B(p, "console cabinet", x, .39f, z - .06f, 1.26f, .66f, .58f, Grey);
            for (int a = -1; a <= 1; a += 2)
            {
                B(p, "cabinet door", x + a * .31f, .39f, z + .236f, .58f, .58f, .012f, Grey);
                B(p, "door handle", x + a * .06f, .47f, z + .248f, .02f, .10f, .015f, Steel);
                for (int i = 0; i < 4; i++)
                    B(p, "louvre", x + a * .31f, .14f + i * .03f, z + .244f, .40f, .008f, .006f, Black);
            }
            B(p, "writing ledge", x, .735f, z - .03f, 1.30f, .03f, .68f, Grey);
            B(p, "indicator cabinet", x, 1.15f, z - .24f, 1.20f, .80f, .26f, Grey);
            B(p, "cabinet cap", x, 1.565f, z - .24f, 1.24f, .03f, .30f, Steel);
            BR(p, "control shelf", x, .805f, z - .03f, 1.16f, .025f, .19f, Grey, 34f, 0f);
            for (int i = 0; i < 6; i++)
                Add(p, "shelf knob", CylY, x - .45f + i * .18f, .825f, z - .016f, .04f, .025f, .04f, Black, RNone, 34f, 0f);
            // the PPI: hood, black bezel, the screen (the runtime swaps its
            // material when the radar is off or the console destroyed)
            Cz(p, "CRT hood", x - .12f, 1.17f, z - .06f, .50f, .10f, Grey, RNone);
            Cz(p, "CRT bezel", x - .12f, 1.17f, z - .007f, .45f, .006f, Black, RNone);
            Cz(p, "PPI screen", x - .12f, 1.17f, z - .002f, .41f, .004f, Screen, RPpi);
            Cz(p, "range selector", x - .51f, 1.25f, z - .09f, .08f, .04f, Black, RNone);
            Cz(p, "gain knob", x - .51f, 1.05f, z - .095f, .05f, .03f, Black, RNone);
            for (int i = 0; i < 3; i++)
                Cz(p, "panel knob", x - .32f + i * .20f, .95f, z - .095f, .05f, .03f, Black, RNone);
            Sheet(p, "label strip", x - .12f, 1.465f, z - .108f, .40f, .05f, .004f, RLabel, 0f);
            Sheet(p, "switch panel", x + .37f, 1.15f, z - .108f, .34f, .34f, .004f, RPanel, 0f);
            for (int i = 0; i < 4; i++)
                B(p, "toggle switch", x + .25f + i * .08f, 1.06f, z - .095f, .012f, .035f, .03f, Steel);
            for (int i = 0; i < 3; i++)
                Cz(p, "indicator lamp", x + .26f + i * .1f, 1.29f, z - .1f, .025f, .015f, Lamp, RNone);
            for (int i = 0; i < 2; i++)
            {
                Cz(p, "gauge case", x + .29f + i * .18f, 1.43f, z - .095f, .11f, .03f, Grey, RNone);
                Cz(p, "gauge face", x + .29f + i * .18f, 1.43f, z - .079f, .09f, .003f, Paper, RGauge);
            }
            FieldPhone(p, x + .44f, .75f, z + .13f, -8f);
            Sheet(p, "operator journal", x - .44f, .757f, z + .12f, .17f, .014f, .24f, RBook, 6f);
            Pencil(p, x - .28f, .75f, z + .17f, 20f);
            // cable runs: along the south sill to the wall box, and east to the
            // second scope; one riser into the ceiling
            for (int i = 0; i < 3; i++)
                B(p, "console cable", (4.6f + x - .63f) * .5f, .011f, -3.33f + i * .025f, x - .63f - 4.6f, .02f, .02f, Black);
            B(p, "scope cable", (x + .63f + ScopeX - .53f) * .5f, .011f, -3.33f, ScopeX - .53f - x - .63f, .02f, .02f, Black);
            B(p, "cable riser", 4.575f, .33f, -3.305f, .02f, .62f, .02f, Black);
            B(p, "junction box", 4.62f, .78f, -3.20f, .12f, .30f, .28f, Grey);
            B(p, "junction box lid", 4.684f, .78f, -3.20f, .008f, .26f, .24f, Steel);
            B(p, "ceiling cable", 4.575f, 1.96f, -3.25f, .02f, 2.06f, .02f, Black);
            Chair(p, "operator chair", x, z + SeatDZ, false, false);
        }

        /// <summary>The height finder position: a smaller grey console with an
        /// A-scope, gauges and a plotting pad. Front = north.</summary>
        static void ScopeConsole(List<Piece> p)
        {
            const float x = ScopeX, z = ScopeZ;
            B(p, "scope plinth", x, .03f, z, 1.06f, .06f, .54f, Steel);
            B(p, "scope cabinet", x, .38f, z, 1.06f, .64f, .54f, Grey);
            B(p, "scope door", x, .38f, z + .276f, .90f, .52f, .012f, Grey);
            B(p, "scope door handle", x + .38f, .45f, z + .287f, .02f, .10f, .015f, Steel);
            B(p, "scope ledge", x, .715f, z + .01f, 1.10f, .03f, .58f, Grey);
            B(p, "scope cabinet top", x, .99f, z - .15f, 1.0f, .52f, .26f, Grey);
            B(p, "scope cap", x, 1.265f, z - .15f, 1.04f, .03f, .30f, Steel);
            Cz(p, "A-scope hood", x - .22f, 1.0f, z + .015f, .30f, .07f, Grey, RNone);
            Cz(p, "A-scope bezel", x - .22f, 1.0f, z + .053f, .27f, .006f, Black, RNone);
            Cz(p, "A-scope screen", x - .22f, 1.0f, z + .058f, .24f, .004f, Screen, RAScope);
            Sheet(p, "scope switch panel", x + .25f, 1.0f, z - .018f, .40f, .40f, .004f, RPanel, 0f);
            Sheet(p, "scope label strip", x - .22f, 1.20f, z - .018f, .30f, .04f, .004f, RLabel, 0f);
            for (int i = 0; i < 4; i++)
                Cz(p, "scope knob", x - .40f + i * .12f, .80f, z - .005f, .04f, .03f, Black, RNone);
            for (int i = 0; i < 2; i++)
            {
                Cz(p, "scope gauge case", x + .16f + i * .18f, 1.15f, z - .005f, .10f, .03f, Grey, RNone);
                Cz(p, "scope gauge face", x + .16f + i * .18f, 1.15f, z + .011f, .08f, .003f, Paper, RGauge);
            }
            Sheet(p, "height plot pad", x + .25f, .735f, z + .14f, .21f, .006f, .28f, RLog, -7f);
            Pencil(p, x + .05f, .73f, z + .2f, -30f);
            TeaGlass(p, x - .42f, .73f, z + .16f);
            Collider(p, "height finder console", x, .62f, z, 1.1f, 1.24f, .6f);
            Chair(p, "height finder chair", x, z + SeatDZ, false, false);
        }

        static void RadioDesk(List<Piece> p)
        {
            const float x = 5.5f, z = -2.95f, top = .78f;
            Desk(p, "radio desk", x, z, 1.25f, .64f, 1f, true);
            B(p, "R-105 radio", x - .17f, top + .185f, z - .11f, .62f, .37f, .32f, Olive);
            Sheet(p, "radio front", x - .17f, top + .195f, z + .052f, .58f, .31f, .004f, RRadio, 0f);
            Cz(p, "tuning knob", x + .02f, top + .24f, z + .064f, .07f, .03f, Black, RNone);
            for (int i = 0; i < 3; i++)
                Cz(p, "radio knob", x - .38f + i * .12f, top + .08f, z + .064f, .045f, .03f, Black, RNone);
            B(p, "radio carry handle", x - .17f, top + .38f, z - .11f, .30f, .02f, .02f, Steel);
            B(p, "power supply", x - .17f, top + .45f, z - .13f, .56f, .16f, .28f, Olive);
            Sheet(p, "power supply front", x - .17f, top + .45f, z + .012f, .50f, .12f, .004f, RPanel, 0f);
            B(p, "antenna lead", x - .44f, 2.075f, z - .25f, .015f, 1.83f, .015f, Black);
            Headphones(p, x + .41f, top, z + .07f);
            BR(p, "radio handset", x + .25f, top + .02f, z - .17f, .06f, .04f, .20f, Black, 0f, 10f);
            B(p, "telegraph key base", x + .43f, top + .01f, z - .17f, .10f, .02f, .07f, Steel);
            Cy(p, "telegraph key knob", x + .43f, top + .035f, z - .17f, .03f, .02f, Black);
            Sheet(p, "radio journal", x - .45f, top + .008f, z + .16f, .17f, .016f, .24f, RBook, -5f);
            Sheet(p, "call sign card", x + .16f, top + .002f, z + .19f, .14f, .004f, .18f, RFreq, 12f);
            Pencil(p, x - .25f, top, z + .22f, 70f);
            DeskLamp(p, x + .54f, top, z - .22f, -1f);
            Chair(p, "radio chair", x, -1.93f, false, true);
        }

        static void CommandDesk(List<Piece> p)
        {
            const float x = 5.5f, z = 2.9f, top = .78f;
            Desk(p, "duty officer desk", x, z, 1.2f, .60f, -1f, true);
            // the repeater indicator faces the officer (south)
            B(p, "repeater cabinet", x - .45f, top + .175f, z + .13f, .36f, .35f, .32f, Grey);
            Cz(p, "repeater hood", x - .45f, top + .19f, z - .055f, .26f, .05f, Grey, RNone);
            Cz(p, "repeater bezel", x - .45f, top + .19f, z - .0825f, .23f, .005f, Black, RNone);
            Cz(p, "repeater screen", x - .45f, top + .19f, z - .0865f, .20f, .004f, Screen, RRepeater);
            for (int a = -1; a <= 1; a += 2)
                Cz(p, "repeater knob", x - .45f + a * .1f, top + .04f, z - .04f, .03f, .02f, Black, RNone);
            B(p, "command intercom", x + .12f, top + .11f, z + .16f, .50f, .22f, .26f, Olive);
            Sheet(p, "intercom keys", x + .12f, top + .11f, z + .028f, .46f, .18f, .004f, RPanel, 0f);
            for (int i = 0; i < 6; i++)
                B(p, "intercom key", x - .08f + i * .08f, top + .235f, z + .10f, .015f, .03f, .015f, Steel);
            DeskPhone(p, x + .48f, top, z + .05f);
            Sheet(p, "orders", x - .12f, top + .002f, z - .13f, .21f, .003f, .30f, RLog, -8f);
            Sheet(p, "duty folder", x + .2f, top + .007f, z - .15f, .24f, .014f, .32f, RFolder, 94f);
            Ashtray(p, x - .47f, top, z - .2f);
            TeaGlass(p, x + .52f, top, z - .2f);
            Pencil(p, x - .12f, top + .003f, z - .02f, -15f);
            Chair(p, "duty officer chair", x, 1.9f, true, true);
        }

        static void PlottingTable(List<Piece> p)
        {
            const float x = 7.4f, z = 1.65f, top = .78f;
            Desk(p, "plotting table", x, z, 1.2f, 1.0f, 1f, false);
            Sheet(p, "plotting map", x, top + .0025f, z, 1.0f, .005f, .82f, RMap, 0f);
            BR(p, "plotting ruler", x + .15f, top + .009f, z - .23f, .5f, .008f, .035f, Wood, 0f, 25f);
            BR(p, "plotting triangle", x - .25f, top + .0075f, z + .2f, .2f, .005f, .2f, Glass, 0f, 15f);
            Pencil(p, x + .3f, top + .005f, z + .15f, -40f);
            Pencil(p, x - .05f, top + .005f, z - .3f, 10f);
            FieldPhone(p, x + .47f, top, z - .38f, -10f);
            DeskLamp(p, x - .40f, top, z + .37f, 1f);
            TeaGlass(p, x + .45f, top, z + .35f);
            Ashtray(p, x - .35f, top, z - .35f);
            Sheet(p, "papirosy pack", x - .18f, top + .01f, z - .40f, .055f, .02f, .085f, RPack, 30f);
            Chair(p, "plotting chair", x, 2.7f, false, true);
        }

        /// <summary>Vertical perspex plotting board (planshet) in a steel
        /// frame by the north window; a grease pencil tray.</summary>
        static void PlotBoard(List<Piece> p)
        {
            const float x = 9.0f, z = 2.98f;
            for (int a = -1; a <= 1; a += 2)
            {
                B(p, "board upright", x + a * .80f, .97f, z, .05f, 1.94f, .05f, Steel);
                B(p, "board foot", x + a * .80f, .02f, z, .06f, .04f, .50f, Steel);
            }
            B(p, "board top rail", x, 1.905f, z, 1.55f, .05f, .04f, Grey);
            B(p, "board bottom rail", x, .315f, z, 1.55f, .05f, .04f, Grey);
            B(p, "perspex sheet", x, 1.11f, z, 1.55f, 1.54f, .012f, Glass);
            Sheet(p, "grease pencil plot", x, 1.11f, z - .0085f, 1.50f, 1.50f, .003f, RPlot, 0f);
            B(p, "pencil tray", x, .36f, z - .06f, .50f, .02f, .08f, Steel);
            Pencil(p, x - .1f, .37f, z - .06f, 3f);
            Pencil(p, x + .12f, .37f, z - .05f, -4f);
            Collider(p, "plotting board", x, .95f, z, 1.64f, 1.9f, .52f);
        }

        static void Storage(List<Piece> p)
        {
            // document safe in the south-east corner
            B(p, "document safe", 10.5f, .43f, -3.06f, .5f, .86f, .44f, Grey);
            B(p, "safe handle", 10.62f, .55f, -2.835f, .02f, .12f, .02f, Steel);
            B(p, "safe keyhole", 10.62f, .66f, -2.838f, .03f, .04f, .01f, Black);
            Sheet(p, "secret folder", 10.48f, .867f, -3.06f, .24f, .014f, .32f, RFolder, -6f);
            Sheet(p, "secret folder", 10.50f, .881f, -3.04f, .24f, .014f, .32f, RFolder, 3f);
            Collider(p, "document safe", 10.5f, .43f, -3.06f, .5f, .86f, .44f);
            // filing cabinet in the north-east corner, north of the vanilla east
            // counter (x 10.2..11.15, z -2.72..2.68, top 0.78 m), drawers south
            B(p, "filing cabinet", 10.55f, .66f, 3.03f, .47f, 1.32f, .56f, Grey);
            for (int i = 0; i < 4; i++)
            {
                B(p, "drawer pull", 10.55f, 1.12f - i * .3f, 2.74f, .12f, .02f, .02f, Steel);
                Sheet(p, "drawer label", 10.55f, 1.18f - i * .3f, 2.748f, .08f, .03f, .004f, RLabel, 0f);
            }
            Sheet(p, "folder stack", 10.55f, 1.33f, 3.03f, .24f, .02f, .32f, RFolder, 8f);
            Collider(p, "filing cabinet", 10.55f, .66f, 3.03f, .47f, 1.32f, .56f);
        }

        /// <summary>The solid west wall (door at z -1..0.45): wall map,
        /// frequency table, duty roster, memo, calendar, clock over the door.</summary>
        static void Walls(List<Piece> p)
        {
            const float w = 4.555f;
            // A narrow picture rail connects the clock and map frames to the
            // wall battens; it clears the door and uses the same timber sheet.
            B(p, "wall picture rail", w + .015f, 2.74f, -.04f, .025f, .04f, 6.34f, Wood);
            for (int i = 0; i < 2; i++)
                B(p, "map mounting batten", w + .01f, 1.37f, -2.96f + i * 1.13f,
                    .02f, 2.74f, .025f, Wood);
            Sheet(p, "air situation map", w, 1.80f, -2.50f, .006f, 1.0f, 1.40f, RWallMap, 0f);
            for (int a = -1; a <= 1; a += 2)
                B(p, "map batten", w + .01f, 1.80f + a * .515f, -2.50f, .02f, .03f, 1.44f, Wood);
            Cx(p, "wall clock", w + .02f, 2.55f, -.25f, .34f, .04f, Black, RNone);
            Cx(p, "clock face", w + .042f, 2.55f, -.25f, .30f, .004f, Paper, RClock);
            // Timber wall battens carry the duty board down to the skirting;
            // notices sit flush on its backing, rather than floating in air.
            B(p, "noticeboard backing", w + .02f, 1.75f, 1.945f, .02f, .90f, 2.39f, Wood);
            for (int i = 0; i < 2; i++)
                B(p, "noticeboard wall batten", w + .015f, 1.37f, .77f + i * 2.35f,
                    .025f, 2.74f, .035f, Wood);
            Sheet(p, "frequency table", w + .032f, 1.75f, 1.05f, .004f, .60f, .50f, RFreq, 0f);
            Sheet(p, "duty roster", w + .032f, 1.75f, 1.65f, .004f, .60f, .50f, RRoster, 0f);
            Sheet(p, "operator memo", w + .032f, 1.68f, 2.25f, .004f, .45f, .45f, RNotes, 0f);
            Sheet(p, "calendar", w + .032f, 1.78f, 2.88f, .004f, .38f, .30f, RCalendar, 0f);
        }

        internal static List<Piece> Pieces()
        {
            List<Piece> p = new List<Piece>(512);
            RadarConsole(p);
            for (int i = 0; i < p.Count; i++) {
                Piece part = p[i]; part.RadarPart = true; p[i] = part;
            }
            ScopeConsole(p);
            RadioDesk(p);
            CommandDesk(p);
            PlottingTable(p);
            PlotBoard(p);
            Storage(p);
            Walls(p);
            return p;
        }

        /// <summary>G R3: the Z M4 supply order terminal in its own frame
        /// (floor, front +z, inside its SupplyW x SupplyH x SupplyD desk
        /// collider): a field requisition desk like the radio desk beside it,
        /// an olive order teleprinter, a TA-57 line to the depot, the
        /// requisition journal and a stencilled tin plate on the apron.</summary>
        internal static List<Piece> Terminal()
        {
            List<Piece> p = new List<Piece>(48);
            const float top = .78f, w = SupplyW, d = SupplyD;
            B(p, "requisition desk top", 0f, .7625f, 0f, w, .035f, d, Wood);
            for (int a = -1; a <= 1; a += 2)
            {
                B(p, "requisition desk apron", 0f, .70f, a * (d * .5f - .04f), w - .1f, .08f, .02f, Wood);
                for (int b = -1; b <= 1; b += 2)
                    B(p, "requisition desk leg", a * (w * .5f - .05f), .3725f, b * (d * .5f - .05f), .045f, .745f, .045f, Steel);
            }
            B(p, "requisition desk shelf", 0f, .13f, -.02f, w - .06f, .02f, d - .14f, Wood);
            B(p, "forms box", -.20f, .225f, -.04f, .30f, .17f, .24f, Olive);
            B(p, "forms box lid handle", -.20f, .315f, -.04f, .12f, .012f, .02f, Steel);
            for (int a = -1; a <= 1; a += 2)
                Cx(p, "cable reel flange", .20f + a * .065f, .24f, -.04f, .20f, .015f, Steel, RNone);
            Cx(p, "field cable on the reel", .20f, .24f, -.04f, .16f, .115f, Black, RNone);
            // ST-2M style order teleprinter: case, sloped keyboard, platen, form
            B(p, "order teleprinter", -.13f, top + .07f, -.06f, .46f, .14f, .30f, Olive);
            B(p, "teleprinter hood", -.13f, top + .165f, -.12f, .40f, .05f, .16f, Olive);
            BR(p, "teleprinter keyboard", -.13f, top + .025f, .135f, .42f, .03f, .09f, Olive, 12f, 0f);
            for (int i = -1; i <= 1; i++)   // three key rows on the slope (12 deg: sin .21, cos .98)
                BR(p, "teleprinter key row", -.13f, top + .0455f - i * .025f * .21f, .135f + i * .025f * .98f,
                    .38f - .03f * i, .012f, .016f, Black, 12f, 0f);
            Cx(p, "teleprinter platen", -.13f, top + .20f, -.12f, .05f, .38f, Black, RNone);
            Add(p, "order form", Box, -.13f, top + .25f, -.13f, .26f, .10f, .003f, Paper, RLog, -8f, 0f);
            Cx(p, "tape roll", -.13f, top + .20f, -.19f, .07f, .24f, Paper, RTape);
            FieldPhone(p, .28f, top, -.10f, -8f);
            Sheet(p, "requisition journal", .27f, top + .007f, .12f, .19f, .014f, .22f, RBook, 4f);
            Sheet(p, "order slip", .10f, top + .002f, .19f, .14f, .004f, .10f, RLog, -9f);
            Pencil(p, .26f, top + .014f, .14f, 62f);
            Sheet(p, "supply plate", 0f, .62f, d * .5f - .0285f, .40f, .15f, .003f, RSign, 0f);
            // the depot line: down the back leg into the cable run by the sill
            B(p, "depot line", w * .5f - .08f, .39f, -d * .5f + .015f, .015f, .78f, .015f, Black);
            B(p, "depot line run", w * .5f - .08f, .011f, -d * .5f - .08f, .02f, .02f, .16f, Black);
            return p;
        }
    }
}
