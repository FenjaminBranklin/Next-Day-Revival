// C W4: ground access graph in world units (2.8 u = 1 m). C# 3.0.
// Shared by the load job and the offline geometry check; no Unity dependency.
using System;
using System.Collections.Generic;

namespace NextDayRevival
{
    internal static class AirfieldPathsCore
    {
        internal const float K = 2.8f, Cell = 0.9f, Width = 2.4f * K;
        internal const float Shoulder = 0.42f * K, DirtLift = 0.10f, SlabLift = 0.14f;
        internal const float SlabLength = 1.6f * K, Joint = 0.07f * K;
        internal const float TreeMargin = 3.5f * K, EntryBlend = 6f;
        internal const float FootX = 4219.48f, FootZ = 1362.02f;
        internal static readonly string[] Names = { "Apron", "AA-NW", "AA-NE", "AA-S", "ZU-W" };
        internal static readonly int[][] Routes = {
            new int[] { 0, 16, 1, 2, 3, 4 }, new int[] { 0, 16, 1, 5, 6 },
            new int[] { 0, 16, 1, 2, 7, 8 }, new int[] { 0, 16, 1, 2, 3, 4, 9, 10, 11 },
            new int[] { 0, 16, 1, 12, 13, 14, 15 }
        };
        internal static readonly int[][] Edges = {
            new int[] {0,16}, new int[] {16,1}, new int[] {1,2}, new int[] {2,3}, new int[] {3,4},
            new int[] {1,5}, new int[] {5,6}, new int[] {2,7}, new int[] {7,8},
            new int[] {4,9}, new int[] {9,10}, new int[] {10,11},
            new int[] {1,12}, new int[] {12,13}, new int[] {13,14}, new int[] {14,15}
        };
        internal static readonly Point[] Nodes = MakeNodes();
        // Existing pavement: A1, F1a and the P2 spine/branches crossed by this
        // graph. Exact footprints from east_airfield.json / shipped colliders.
        // Clip these out, rather than laying new slabs on old concrete.
        static readonly Point[][] Pavement = {
            Rect(4348,900,4496,1280), Rect(4289.6,1198,4348,1282),
            Rect(4171.6,-1460,4188.4,1570), Rect(4180,1431.6,4192,1448.4),
            Rect(4180,1326.6,4216,1343.4), Rect(4180,1091.6,4191,1108.4),
            Rect(4134,991.6,4180,1008.4)
        };

        internal struct Point
        {
            internal double X, Z;
            internal Point(double x, double z) { X = x; Z = z; }
        }

        static Point[] MakeNodes()
        {
            Point[] n = new Point[] {
                new Point(FootX,FootZ), new Point(4208,1376), new Point(4296,1376),
                new Point(4296,1302), new Point(4355,1272), new Point(), new Point(),
                new Point(), new Point(), new Point(4362,903), new Point(), new Point(),
                new Point(4180,1376), new Point(4180,1135), new Point(), new Point(), new Point(4208,FootZ)
            };
            int[] outer = { 5, 7, 10, 14 }, inner = { 6, 8, 11, 15 };
            for (int p = 0; p < outer.Length; p++)
            {
                double angle = FlakPositionsCore.Heading(p) * Math.PI / 180;
                double x = FlakPositionsCore.X[p], z = FlakPositionsCore.Z[p];
                n[outer[p]] = new Point(x + Math.Sin(angle) * 36, z + Math.Cos(angle) * 36);
                n[inner[p]] = new Point(x + Math.Sin(angle) * 5.9 * K, z + Math.Cos(angle) * 5.9 * K);
            }
            return n;
        }

        internal static double Length(int edge)
        {
            Point a = Nodes[Edges[edge][0]], b = Nodes[Edges[edge][1]];
            return Math.Sqrt((b.X-a.X)*(b.X-a.X) + (b.Z-a.Z)*(b.Z-a.Z));
        }
        internal static int Steps(int edge) { return (int)Math.Ceiling(Length(edge) / Cell); }
        static double Half(int edge, double t, bool dirt)
        {
            // A narrow westward throat fits between the actual stair rails
            // (0.94 m apart). Widen only after clearing the tower's west face.
            double w = edge == 0 ? 0.85*K/2 : Width/2;
            return w + (dirt && edge!=0 ? Shoulder : 0);
        }
        static bool Dead(int node) { return node == 0 || node == 6 || node == 8 || node == 11 || node == 15; }
        static Point[] Strip(int edge, double from, double to, bool dirt, bool caps)
        {
            Point a = Nodes[Edges[edge][0]], b = Nodes[Edges[edge][1]];
            double len = Length(edge), dx = (b.X-a.X)/len, dz = (b.Z-a.Z)/len;
            double wa = Half(edge, Math.Max(0,from/len),dirt), wb = Half(edge, Math.Min(1,to/len),dirt);
            if (caps && !Dead(Edges[edge][0])) from -= wa;
            if (caps && !Dead(Edges[edge][1])) to += wb;
            return new Point[] {
                new Point(a.X+dx*from-dz*wa,a.Z+dz*from+dx*wa),
                new Point(a.X+dx*to-dz*wb,a.Z+dz*to+dx*wb),
                new Point(a.X+dx*to+dz*wb,a.Z+dz*to-dx*wb),
                new Point(a.X+dx*from+dz*wa,a.Z+dz*from-dx*wa)
            };
        }
        static Point[] Rect(double x0, double z0, double x1, double z1)
        { return new Point[] { new Point(x0,z1), new Point(x1,z1), new Point(x1,z0), new Point(x0,z0) }; }

        static double Side(Point a, Point b, Point p)
        { return (b.X-a.X)*(p.Z-a.Z) - (b.Z-a.Z)*(p.X-a.X); }
        static List<Point> Clip(List<Point> p, Point a, Point b, bool inside)
        {
            List<Point> q = new List<Point>();
            if (p.Count == 0) return q;
            Point prev = p[p.Count-1]; double sp = Side(a,b,prev);
            bool ip = inside ? sp <= 0 : sp >= 0;
            foreach (Point next in p)
            {
                double sn = Side(a,b,next); bool it = inside ? sn <= 0 : sn >= 0;
                if (ip != it)
                {
                    double t = sp/(sp-sn);
                    q.Add(new Point(prev.X+(next.X-prev.X)*t,prev.Z+(next.Z-prev.Z)*t));
                }
                if (it) q.Add(next);
                prev=next; sp=sn; ip=it;
            }
            return q;
        }
        static double Area(List<Point> p)
        {
            double a=0;
            for (int i=0;i<p.Count;i++) { Point u=p[i],v=p[(i+1)%p.Count]; a+=u.X*v.Z-u.Z*v.X; }
            return Math.Abs(a)/2;
        }
        static bool Near(List<Point> p, Point[] cut)
        {
            double x0=double.MaxValue,z0=x0,x1=double.MinValue,z1=x1;
            foreach (Point v in p) { x0=Math.Min(x0,v.X); x1=Math.Max(x1,v.X); z0=Math.Min(z0,v.Z); z1=Math.Max(z1,v.Z); }
            double a0=double.MaxValue,b0=a0,a1=double.MinValue,b1=a1;
            foreach (Point v in cut) { a0=Math.Min(a0,v.X); a1=Math.Max(a1,v.X); b0=Math.Min(b0,v.Z); b1=Math.Max(b1,v.Z); }
            return x1>a0 && a1>x0 && z1>b0 && b1>z0;
        }
        static List<List<Point>> Subtract(List<List<Point>> polys, Point[] cut)
        {
            List<List<Point>> result = new List<List<Point>>();
            foreach (List<Point> p in polys)
            {
                if (!Near(p,cut)) { result.Add(p); continue; }
                List<Point> remainder=p;
                for (int i=0;i<cut.Length && remainder.Count>=3;i++)
                {
                    Point a=cut[i],b=cut[(i+1)%cut.Length];
                    List<Point> outside=Clip(remainder,a,b,false);
                    if (outside.Count>=3 && Area(outside)>0.000001) result.Add(outside);
                    remainder=Clip(remainder,a,b,true);
                }
            }
            return result;
        }

        // A finite panel, clipped against existing floors and earlier ribbons.
        // Dirt is continuous. Slab joints and occasional missing panels expose
        // the same kit earth beneath, while leaving the walking ground intact.
        internal static List<List<Point>> Panel(int edge, int step, int layer)
        {
            double len=Length(edge), from=len*step/Steps(edge), to=len*(step+1)/Steps(edge);
            bool dirt=layer==0;
            int slab=(int)(from/SlabLength), seed=(edge*71+slab*37)%29;
            List<List<Point>> polys=new List<List<Point>>();
            if (!dirt)
            {
                if (seed==7 || seed==19) return polys;
                from=Math.Max(from,slab*SlabLength+Joint/2);
                to=Math.Min(to,(slab+1)*SlabLength-Joint/2);
                if (to<=from) return polys;
            }
            Point[] strip=Strip(edge,from,to,dirt,false);
            // Only terminal panels extend into a junction, and later edges
            // subtract that extension. No coplanar overlays at shared nodes.
            if (step==0 && !Dead(Edges[edge][0]))
            {
                Point[] full=Strip(edge,0,len,dirt,true); strip[0]=full[0]; strip[3]=full[3];
            }
            if (step==Steps(edge)-1 && !Dead(Edges[edge][1]))
            {
                Point[] full=Strip(edge,0,len,dirt,true); strip[1]=full[1]; strip[2]=full[2];
            }
            polys.Add(new List<Point>(strip));
            foreach (Point[] cut in Pavement) polys=Subtract(polys,cut);
            for (int i=0;i<edge;i++) polys=Subtract(polys,Strip(i,0,Length(i),dirt,true));
            return polys;
        }

        internal static int Material(int edge, int step, int layer)
        {
            int slab=(int)(Length(edge)*step/Steps(edge)/SlabLength);
            return layer==0 ? 0 : ((edge*71+slab*37)%3==0 ? 2 : 1);
        }
        internal static List<Point[]> Triangles(int edge, int step, int layer)
        {
            List<Point[]> result=new List<Point[]>();
            foreach (List<Point> p in Panel(edge,step,layer))
            {
                double x0=double.MaxValue,z0=x0,x1=double.MinValue,z1=x1;
                foreach (Point v in p) { x0=Math.Min(x0,v.X); x1=Math.Max(x1,v.X); z0=Math.Min(z0,v.Z); z1=Math.Max(z1,v.Z); }
                // World-aligned cells avoid thin-triangle recursive explosion
                // on a narrow ribbon; each edge is bounded by sqrt(2)*Cell.
                for (int x=(int)Math.Floor(x0/Cell);x<=(int)Math.Floor(x1/Cell);x++)
                    for (int z=(int)Math.Floor(z0/Cell);z<=(int)Math.Floor(z1/Cell);z++)
                    {
                        Point[] box=Rect(x*(double)Cell,z*(double)Cell,(x+1)*(double)Cell,(z+1)*(double)Cell);
                        List<Point> cell=p;
                        for (int i=0;i<4 && cell.Count>=3;i++) cell=Clip(cell,box[i],box[(i+1)%4],true);
                        if (cell.Count<3 || Area(cell)<0.000001) continue;
                        for (int i=1;i+1<cell.Count;i++) result.Add(new Point[] {cell[0],cell[i],cell[i+1]});
                    }
            }
            return result;
        }

        internal static float Lift(double x, double z, float ground, float footY, float[] pitY)
        {
            double foot=Math.Sqrt((x-FootX)*(x-FootX)+(z-FootZ)*(z-FootZ));
            double lift=Math.Max(0,footY-ground)*(1-Math.Min(1,foot/EntryBlend));
            for (int p=0;p<pitY.Length;p++)
            {
                double dx=x-FlakPositionsCore.X[p],dz=z-FlakPositionsCore.Z[p];
                double r=Math.Sqrt(dx*dx+dz*dz), inner=FlakPositionsCore.Inner*K;
                double blend=1-Math.Min(1,Math.Max(0,r-inner)/EntryBlend);
                lift=Math.Max(lift,Math.Max(0,pitY[p]+FlakPositionsCore.PadLift-ground)*blend);
            }
            return (float)lift;
        }
        internal static bool ClearTree(float x, float z)
        {
            for (int e=0;e<Edges.Length;e++)
            {
                Point a=Nodes[Edges[e][0]],b=Nodes[Edges[e][1]];
                double dx=b.X-a.X,dz=b.Z-a.Z;
                double t=Math.Max(0,Math.Min(1,((x-a.X)*dx+(z-a.Z)*dz)/(dx*dx+dz*dz)));
                double ox=x-a.X-dx*t,oz=z-a.Z-dz*t,r=Half(e,t,true)+TreeMargin;
                if (ox*ox+oz*oz<=r*r) return true;
            }
            return false;
        }
    }
}
