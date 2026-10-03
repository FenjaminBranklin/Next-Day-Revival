// Z TC1. Deterministic static furnishing recipe, real metres in the C1 frame.
// Pure C# 3.0: the offline proof compiles this exact recipe, not a copy.
using System.Collections.Generic;

namespace NextDayRevival
{
    internal static class TowerCommandRoomCore
    {
        internal const int Olive = 0, Steel = 1, Wood = 2, Black = 3,
            Paper = 4, Canvas = 5, Lamp = 6, Map = 7;
        // Z M4 integration seam: the vanilla east desk is retained. Approach
        // from the west; do not put furniture/chairs in this reservation.
        internal const float OrderX = 10.65f, OrderZ = 0f;
        internal const float OrderApproachX = 9.35f;

        internal struct Piece
        {
            internal string Name;
            internal float X, Y, Z, SX, SY, SZ;
            internal int Material;
            internal bool Solid, Bag;
            internal Piece(string n, float x, float y, float z, float sx,
                float sy, float sz, int m, bool solid, bool bag)
            { Name=n; X=x; Y=y; Z=z; SX=sx; SY=sy; SZ=sz;
              Material=m; Solid=solid; Bag=bag; }
        }

        static void Box(List<Piece> p, string n, float x, float y, float z,
            float sx, float sy, float sz, int m)
        { p.Add(new Piece(n,x,y,z,sx,sy,sz,m,false,false)); }

        static void Collider(List<Piece> p, string n, float x, float y, float z,
            float sx, float sy, float sz)
        { p.Add(new Piece(n,x,y,z,sx,sy,sz,Black,true,false)); }

        static void Table(List<Piece> p, string n, float x, float z, float sx, float sz)
        {
            Box(p,n+" top",x,.78f,z,sx,.07f,sz,Wood);
            for (int a=-1;a<=1;a+=2) for (int b=-1;b<=1;b+=2)
                Box(p,n+" leg",x+a*(sx*.5f-.09f),.37f,z+b*(sz*.5f-.09f),.07f,.74f,.07f,Steel);
            Collider(p,n,x,.4f,z,sx,.8f,sz);
        }

        static void Chair(List<Piece> p, string n, float x, float z, bool facesNorth)
        {
            Box(p,n+" seat",x,.45f,z,.43f,.06f,.43f,Wood);
            Box(p,n+" back",x,.73f,z+(facesNorth?-.21f:.21f),.43f,.48f,.055f,Wood);
            for (int a=-1;a<=1;a+=2) for (int b=-1;b<=1;b+=2)
                Box(p,n+" leg",x+a*.17f,.22f,z+b*.17f,.035f,.44f,.035f,Steel);
            Collider(p,n,x,.49f,z,.43f,.98f,.48f);
        }

        static void Phone(List<Piece> p, float x, float z)
        {
            Box(p,"TA-57 field telephone",x,.865f,z,.28f,.12f,.20f,Olive);
            Box(p,"handset",x,.95f,z,.30f,.045f,.045f,Black);
            for (int s=-1;s<=1;s+=2)
                Box(p,"earpiece",x+s*.12f,.92f,z,.065f,.07f,.07f,Black);
            for (int i=0;i<5;i++)
                Box(p,"telephone cord",x+.20f,.84f,z-.07f+i*.028f,.075f,.012f,.012f,Black);
        }

        static void DeskLamp(List<Piece> p, float x, float z)
        {
            Box(p,"lamp foot",x,.84f,z,.18f,.035f,.15f,Black);
            Box(p,"lamp stem",x,1.04f,z,.024f,.4f,.024f,Steel);
            Box(p,"lamp arm",x+.065f,1.23f,z,.16f,.025f,.025f,Steel);
            Box(p,"enamel shade",x+.13f,1.21f,z,.22f,.08f,.16f,Olive);
            Box(p,"warm diffuser",x+.13f,1.168f,z,.18f,.006f,.12f,Lamp);
        }

        static void Bags(List<Piece> p, string n, float x, float z, bool east)
        {
            // Three staggered courses, below the 1 m window sill; rounded
            // low-poly bags share one mesh/material and one physical box.
            int count=east?1:4;
            for (int row=0;row<3;row++) for (int i=0;i<count;i++) {
                float d=(i-(count-1)*.5f)*.43f+(row==1?.10f:0f);
                p.Add(new Piece(n,east?x:x+d,.15f+row*.25f,east?z+d:z,
                    east?.38f:.46f,.27f,east?.46f:.38f,Canvas,false,true));
            }
            Collider(p,n+" cover",x,.40f,z,east?.40f:1.95f,.80f,east?.60f:.40f);
        }

        // C W3: the radar console stands on the MAIN roof (TowerRoofCore.
        // ConsoleX/ConsoleZ, its desk collider is the console's own); its
        // post is a sandbag horseshoe W/S/E, open north to the stair, and a
        // field cable from a junction box behind the desk to the cab.
        internal const float RoofConsoleX = 1.5f, RoofConsoleZ = 4.2f;

        static void Wall(List<Piece> p, string n, float x, float z, float len, bool alongX)
        {
            // Three courses of rounded bags, the middle one staggered by half
            // a bag; one physical box for the whole wall.
            int count=(int)(len/.46f+.5f);
            float bag=len/count;
            for (int row=0;row<3;row++) {
                int k=row==1?count-1:count;
                for (int i=0;i<k;i++) {
                    float d=(i-(k-1)*.5f)*bag;
                    p.Add(new Piece(n,alongX?x+d:x,.15f+row*.3f,alongX?z:z+d,
                        alongX?bag*.98f:.45f,.3f,alongX?.45f:bag*.98f,Canvas,false,true));
                }
            }
            Collider(p,n+" cover",x,.45f,z,alongX?len:.45f,.9f,alongX?.45f:len);
        }

        /// <summary>The console's post on the main roof, tower metres, y up
        /// from the roof (TowerRoofCore.ConsoleBox holds the same walls).</summary>
        internal static List<Piece> RoofPieces()
        {
            List<Piece> p=new List<Piece>(64);
            float x=RoofConsoleX, z=RoofConsoleZ;
            Wall(p,"console south bags",x,z-.95f,3.0f,true);
            Wall(p,"console west bags",x-1.275f,z+.225f,1.85f,false);
            Wall(p,"console east bags",x+1.275f,z+.225f,1.85f,false);
            // junction box behind the desk, a cable flat on the roof under the
            // east wall to the cab's north-west corner
            Box(p,"junction box",x+.85f,.16f,z-.56f,.26f,.32f,.2f,Olive);
            Box(p,"junction lid",x+.85f,.335f,z-.56f,.28f,.03f,.22f,Steel);
            Box(p,"field cable",(x+.98f+4.75f)*.5f,.012f,z-.56f,4.75f-x-.98f+.035f,.024f,.035f,Black);
            Box(p,"field cable",4.75f,.012f,(z-.56f+3.3f)*.5f,.035f,.024f,z-.56f-3.3f,Black);
            return p;
        }

        internal static List<Piece> Pieces()
        {
            List<Piece> p=new List<Piece>(256);
            Table(p,"map table",7.4f,1.65f,1.2f,1f);
            Box(p,"map sheet",7.4f,.819f,1.65f,1.0f,.006f,.82f,Map);
            // Folded orders and map weights, in place of text/UI clutter.
            Box(p,"orders",7.8f,.836f,1.96f,.22f,.02f,.15f,Paper);
            Box(p,"map weight",7.05f,.839f,1.3f,.06f,.04f,.06f,Black);
            Phone(p,7.70f,1.3f);
            DeskLamp(p,7.05f,1.94f);
            Chair(p,"map chair",7.4f,2.7f,false);

            Table(p,"radio bench",5.5f,-2.95f,1.25f,.64f);
            Box(p,"R-123 radio",5.45f,1.04f,-3.04f,.68f,.44f,.30f,Olive);
            Box(p,"radio dial",5.31f,1.08f,-2.883f,.21f,.17f,.009f,Paper);
            for (int i=0;i<4;i++)
                Box(p,"radio selector",5.62f,1.17f-i*.075f,-2.86f,.035f,.035f,.035f,Black);
            for (int i=0;i<7;i++)
                Box(p,"radio grille",5.19f+i*.025f,.93f,-2.878f,.012f,.07f,.01f,Black);
            Phone(p,5.92f,-2.94f);
            Chair(p,"radio chair",5.5f,-1.93f,false);

            Table(p,"dispatch desk",5.5f,2.9f,1.2f,.60f);
            Box(p,"dispatch panel",5.5f,1.04f,3.03f,1.05f,.44f,.23f,Olive);
            for (int i=0;i<5;i++) {
                Box(p,"analogue meter",5.10f+i*.20f,1.14f,2.908f,.13f,.12f,.009f,Paper);
                Box(p,"meter needle",5.10f+i*.20f,1.14f,2.901f,.008f,.075f,.007f,Black);
                Box(p,"dispatch switch",5.10f+i*.20f,.97f,2.885f,.025f,.06f,.025f,Black);
            }
            DeskLamp(p,5.05f,2.7f);
            Chair(p,"dispatch chair",5.5f,1.9f,true);
            Bags(p,"north window bags",9.5f,3.10f,false);
            Bags(p,"south window bags",10.15f,-3.10f,false);
            // East window flank only; center stays reserved for Z M4.
            Bags(p,"east window bags",10.98f,3.0f,true);
            return p;
        }
    }
}
