// Power bunker 4: the heavy steel blast door, engine-free rules. Runtime:
// Revival.PowerBunkerDoor.cs; check: research/power_bunker_door_check.py;
// report: docs/ai/tasks/power-bunker/pb-4-door.md.
//
//   SPEC    One DoorSpec per host: the AF-GEN shed's east opening (in the
//           game today) and the pb-1 concrete bunker's south opening (its
//           integration adds a Sockets/BlastDoor marker). Metres, in a door
//           frame: origin = opening centre on the outer wall face at floor
//           level, +x along the wall, +y up, +z outward.
//   MOTION  One scalar per door: Progress 0 = closed and dogged, 1 = fully
//           open, moving toward Target. The first UnlockShare of the travel
//           turns the wheel and pulls the dogs; the rest swings the leaf
//           (ease in/out). Closing runs the same path backward.
//   STATE   The master owns Target/Progress/Revision; clients mirror its
//           snapshot (newer revisions only) and animate locally.
//   NAV     Closed (leaf under PassDeg) carves the opening: enemies stay
//           out; a merc whose way runs through it asks the master to open.
//
// C# 3.0 (csc from .NET 3.5). ASCII only.
using System;
using System.Collections.Generic;

namespace NextDayRevival
{
    internal sealed class DoorSpec
    {
        internal string Id, Socket;
        internal float OriginX, OriginY, OriginZ;   // host frame, metres
        internal float Yaw;                         // outward normal: 0 = host +z, 90 = host +x
        internal float OpenW, OpenH;                // clear opening
        internal int HingeSign;                     // hinge on door-local +x (1) or -x (-1)
        internal float HingeX, HingeZ;              // hinge axis in the door frame
        internal float LeafW, LeafH, LeafT, LeafZ0, LeafY0;   // leaf size, inner face, bottom
        internal float OpenDeg;
        internal bool Frame;                        // the host has no steel frame: build one
        internal float WheelY;                      // wheel centre height
    }

    internal struct DoorState
    {
        internal bool Target;      // true = open
        internal float Progress;   // 0 closed .. 1 open
        internal int Revision;
    }

    /// <summary>One box or cylinder of the door model (door frame, metres).
    /// Group: 0 fixed frame, 1 leaf, 2 wheel (turns on the leaf), 3 dogs
    /// (slide on the leaf), 4 inner lever (turns on the leaf). Leaf parts are
    /// relative to the hinge axis; wheel/lever parts to their own hub.</summary>
    internal struct DoorPart
    {
        internal int Group, Mat, Shape;            // Mat: 0 door sheet, 1 painted steel, 2 bare steel; Shape: 0 box, 1 cylinder (axis), 2 ring
        internal float X, Y, Z, SX, SY, SZ;        // box: centre + size; cylinder: centre, radius in SX, length in SY
        internal int Axis;                         // cylinder axis 0 x, 1 y, 2 z
        internal string Name;
    }

    internal static class PowerBunkerDoorCore
    {
        internal const float K = 2.8f;                 // game units per metre
        internal const float UnlockShare = 0.3f;       // share of the travel spent on the wheel/dogs
        internal const float TravelSeconds = 4.2f;     // closed -> open (wheel 1.26 s, swing 2.94 s)
        internal const float WheelTurns = 1.25f;       // wheel revolutions from dogged to free
        internal const float DogTravelM = 0.13f;       // dogs pull back this far (fully into the leaf)
        internal const float PassDeg = 55f;            // leaf open at least this far: the opening is passable
        internal const float ReachM = 2.4f;            // player reach to the opening centre (either side)
        internal const float ReachUpM = 2.2f;          // vertical reach
        internal const float MasterReachM = 4.8f;      // master check (lag margin)
        internal const float MercApproachM = 3.5f;     // a merc this close asks for the door
        internal const float AskCooldown = 0.6f;       // one accepted request per door per this many seconds
        internal const float ResyncShare = 0.2f;       // a snapshot this far off the local animation snaps it
        internal const int Closed = 0, Opening = 1, Open = 2, Closing = 3;
        internal const int AskPlayer = 0, AskMerc = 1;

        internal static readonly DoorSpec[] Specs = BuildSpecs();

        static DoorSpec[] BuildSpecs()
        {
            // AF-GEN (unity/EastTile/Tools/airfield_generator.py): east wall
            // outer face x 6.0, opening z -0.3..2.3, lintel underside 2.74;
            // the hinge socket DoorHinge sits at the north jamb.
            DoorSpec gen = new DoorSpec();
            gen.Id = "AF-GEN"; gen.Socket = "DoorHinge";
            gen.OriginX = 6.0f; gen.OriginY = 0f; gen.OriginZ = 1.0f; gen.Yaw = 90f;
            gen.OpenW = 2.6f; gen.OpenH = 2.74f;
            gen.HingeSign = -1; gen.HingeX = -1.48f; gen.HingeZ = 0.27f;
            gen.LeafW = 2.84f; gen.LeafH = 2.86f; gen.LeafT = 0.16f; gen.LeafZ0 = 0.07f; gen.LeafY0 = 0.02f;
            gen.OpenDeg = 92f; gen.Frame = true; gen.WheelY = 1.25f;

            // pb-1 bunker (assets/src/power_bunker_blender.py, Unity axes):
            // south face z -3.5, opening x 1.7..3.3, 2.2 high; its frame
            // stands 0.14 proud, the hinge knuckles at x 1.64, 0.2 out. The
            // pb-5 site root (east_af_generator.json) is the interior floor
            // (the model's -1.5 m), so the opening starts at host y 0.
            DoorSpec pb = new DoorSpec();
            pb.Id = "PB"; pb.Socket = "BlastDoor";
            pb.OriginX = 2.5f; pb.OriginY = 0f; pb.OriginZ = -3.5f; pb.Yaw = 180f;
            pb.OpenW = 1.6f; pb.OpenH = 2.2f;
            pb.HingeSign = 1; pb.HingeX = 0.86f; pb.HingeZ = 0.2f;
            pb.LeafW = 1.76f; pb.LeafH = 2.33f; pb.LeafT = 0.12f; pb.LeafZ0 = 0.15f; pb.LeafY0 = 0.03f;
            pb.OpenDeg = 95f; pb.Frame = false; pb.WheelY = 1.15f;
            return new DoorSpec[] { gen, pb };
        }

        internal static DoorSpec SpecById(string id)
        {
            for (int i = 0; i < Specs.Length; i++) if (Specs[i].Id == id) return Specs[i];
            return null;
        }

        // ------------------------------------------------------------ motion

        internal static DoorState Fresh() { DoorState s = new DoorState(); return s; }

        internal static int Phase(DoorState s)
        {
            if (s.Target) return s.Progress >= 1f ? Open : Opening;
            return s.Progress <= 0f ? Closed : Closing;
        }

        internal static bool Moving(DoorState s) { return s.Target ? s.Progress < 1f : s.Progress > 0f; }

        /// <summary>Advance toward the target; true while something moved.</summary>
        internal static bool Step(ref DoorState s, float dt)
        {
            if (!Finite(dt) || dt <= 0f || !Moving(s)) return false;
            float d = Math.Min(dt, 1f) / TravelSeconds;
            s.Progress = s.Target ? Math.Min(1f, s.Progress + d) : Math.Max(0f, s.Progress - d);
            return true;
        }

        /// <summary>Leaf angle (degrees, 0 closed) at this progress.</summary>
        internal static float LeafDeg(DoorSpec spec, float progress)
        {
            float t = Clamp01((progress - UnlockShare) / (1f - UnlockShare));
            return spec.OpenDeg * t * t * (3f - 2f * t);
        }

        /// <summary>Signed leaf yaw in the door frame: the free edge swings out.</summary>
        internal static float LeafYaw(DoorSpec spec, float progress) { return LeafDeg(spec, progress) * spec.HingeSign; }

        /// <summary>Wheel (and inner lever) turn in degrees: all of it in the unlock share.</summary>
        internal static float WheelDeg(float progress)
        {
            return Clamp01(progress / UnlockShare) * WheelTurns * 360f;
        }

        /// <summary>How far the dogs are pulled back (metres).</summary>
        internal static float DogPull(float progress) { return Clamp01(progress / UnlockShare) * DogTravelM; }

        internal static bool Dogged(float progress) { return progress <= 0f; }

        /// <summary>A person or an NPC fits through: the leaf is open far enough.</summary>
        internal static bool Passable(DoorSpec spec, float progress) { return LeafDeg(spec, progress) >= PassDeg; }

        /// <summary>The nav carve is on whenever the opening is not passable.</summary>
        internal static bool Carve(DoorSpec spec, float progress) { return !Passable(spec, progress); }

        // ------------------------------------------------------------ requests

        /// <summary>Is a requester at (x, y, z) (door frame, metres) in reach
        /// of the opening? Either side of the wall counts.</summary>
        internal static bool InReach(DoorSpec spec, float x, float y, float z, float reach)
        {
            if (!Finite(x) || !Finite(y) || !Finite(z)) return false;
            float halfW = spec.OpenW * 0.5f;
            float dx = Math.Max(0f, Math.Abs(x) - halfW);
            float dy = y < 0f ? -y : Math.Max(0f, y - spec.OpenH);
            return dx * dx + z * z <= reach * reach && dy <= ReachUpM;
        }

        /// <summary>Master: apply an open/close request. A request to the
        /// current target or one inside the cooldown changes nothing; a
        /// reversal mid-travel turns the door around where it is.</summary>
        internal static bool Request(ref DoorState s, bool open, float now, ref float lastAccepted)
        {
            if (s.Target == open) return false;
            if (!Finite(now) || now - lastAccepted < AskCooldown) return false;
            s.Target = open;
            s.Revision++;
            lastAccepted = now;
            return true;
        }

        /// <summary>Client: take the master's snapshot if newer. Progress is
        /// taken over only when the local animation is off by ResyncShare or
        /// more, or the door is at rest there (no visible jumps otherwise).</summary>
        internal static bool Snapshot(ref DoorState s, bool target, float progress, int revision)
        {
            if (!Finite(progress) || progress < -0.01f || progress > 1.01f) return false;
            if (revision < s.Revision) return false;
            progress = Clamp01(progress);
            bool changed = s.Target != target || revision != s.Revision;
            s.Target = target;
            s.Revision = revision;
            bool rest = target ? progress >= 1f : progress <= 0f;
            if (Math.Abs(s.Progress - progress) >= ResyncShare || (rest && !Moving(s)) || (rest && s.Progress == progress))
                s.Progress = progress;
            return changed;
        }

        /// <summary>Owner client: does this merc need the door opened?
        /// It is close, the door bars the way, and it is headed for the other
        /// side (or its path ends short at the door).</summary>
        internal static bool MercWantsPass(float distM, bool insideNow, bool destInside, bool pathPartial,
            bool passable, bool targetOpen)
        {
            if (passable || targetOpen || !Finite(distM) || distM > MercApproachM) return false;
            return insideNow != destInside || pathPartial;
        }

        /// <summary>The prompt the local player sees (0 open, 1 close, 2 wait).</summary>
        internal static int PromptKind(DoorState s)
        {
            int p = Phase(s);
            if (p == Closed) return 0;
            if (p == Open) return 1;
            return s.Target ? 1 : 0;   // reverse what it is doing
        }

        // ------------------------------------------------------------ frames

        /// <summary>Host frame (metres) -> door frame.</summary>
        internal static void ToDoor(DoorSpec spec, float hx, float hy, float hz, out float x, out float y, out float z)
        {
            double r = spec.Yaw * Math.PI / 180.0;
            float c = (float)Math.Cos(r), sn = (float)Math.Sin(r);
            float dx = hx - spec.OriginX, dz = hz - spec.OriginZ;
            // door +x = host (cos, -sin), door +z = host (sin, cos)
            x = dx * c - dz * sn;
            z = dx * sn + dz * c;
            y = hy - spec.OriginY;
        }

        /// <summary>Door frame -> host frame (metres).</summary>
        internal static void ToHost(DoorSpec spec, float x, float y, float z, out float hx, out float hy, out float hz)
        {
            double r = spec.Yaw * Math.PI / 180.0;
            float c = (float)Math.Cos(r), sn = (float)Math.Sin(r);
            hx = spec.OriginX + x * c + z * sn;
            hz = spec.OriginZ - x * sn + z * c;
            hy = spec.OriginY + y;
        }

        /// <summary>Leaf-local point (relative to the hinge axis, closed pose)
        /// rotated to the given leaf yaw, in the door frame.</summary>
        internal static void LeafPoint(DoorSpec spec, float yawDeg, float lx, float ly, float lz,
            out float x, out float y, out float z)
        {
            double r = yawDeg * Math.PI / 180.0;
            float c = (float)Math.Cos(r), sn = (float)Math.Sin(r);
            // Unity yaw: x' = x cos + z sin, z' = -x sin + z cos
            x = spec.HingeX + lx * c + lz * sn;
            z = spec.HingeZ - lx * sn + lz * c;
            y = ly;
        }

        /// <summary>AF-GEN only: the shipped fixed-open leaf and its handle
        /// (airfield_generator.py "Door open leaf"/"Door handle", host metres)
        /// that the working door replaces. True for a triangle centroid inside.</summary>
        internal static bool StaticLeaf(float x, float y, float z)
        {
            return In(x, y, z, 6.7f, 1.35f, 2.2f, 1.8f, 2.65f, 0.12f) || In(x, y, z, 7.2f, 1.15f, 2.10f, 0.28f, 0.09f, 0.15f);
        }

        static bool In(float x, float y, float z, float cx, float cy, float cz, float sx, float sy, float sz)
        {
            const float e = 0.01f;
            return Math.Abs(x - cx) <= sx * 0.5f + e && Math.Abs(y - cy) <= sy * 0.5f + e && Math.Abs(z - cz) <= sz * 0.5f + e;
        }

        // ------------------------------------------------------------ model

        /// <summary>The door model as parts (door frame, metres). The runtime
        /// builds its meshes from these; the Blender preview reads the same
        /// list (harness --dump-parts).</summary>
        internal static List<DoorPart> Parts(DoorSpec spec)
        {
            List<DoorPart> p = new List<DoorPart>(64);
            float hx = spec.HingeX, hz = spec.HingeZ;
            float d = -spec.HingeSign;                     // hinge -> free edge along door x
            float w = spec.LeafW, h = spec.LeafH, t = spec.LeafT;
            float y0 = spec.LeafY0, z0 = spec.LeafZ0;
            // leaf-local (hinge axis origin): x from the hinge edge offset to the free edge
            float edge = Math.Abs(hx) - w * 0.5f;          // axis beyond the leaf's hinge edge
            float cx = d * (edge + w * 0.5f);              // leaf centre x relative to the axis
            float cz = z0 + t * 0.5f - hz;                 // leaf centre z relative to the axis
            float outer = z0 + t - hz, inner = z0 - hz;

            if (spec.Frame)
            {
                float fw = 0.16f, fd = 0.06f, ow = spec.OpenW * 0.5f;
                Box(p, 0, 1, "frame jamb hinge", -d * (ow + fw * 0.5f), spec.OpenH * 0.5f + fw * 0.5f, fd * 0.5f, fw, spec.OpenH + fw, fd);
                Box(p, 0, 1, "frame jamb lock", d * (ow + fw * 0.5f), spec.OpenH * 0.5f + fw * 0.5f, fd * 0.5f, fw, spec.OpenH + fw, fd);
                Box(p, 0, 1, "frame head", 0f, spec.OpenH + fw * 0.5f, fd * 0.5f, spec.OpenW, fw, fd);
                Box(p, 0, 2, "threshold", 0f, 0.01f, fd * 0.5f, spec.OpenW, 0.02f, fd + 0.3f);
                Box(p, 0, 1, "strike plate", d * (ow + 0.08f), spec.WheelY, fd + 0.02f, 0.12f, 0.36f, 0.04f);
                for (int i = 0; i < 3; i++)
                {
                    float ky = y0 + 0.28f + i * (h - 0.56f) * 0.5f;
                    Cyl(p, 0, 2, "hinge knuckle", hx, ky + 0.07f, hz, 0.055f, 0.14f, 1);
                    Box(p, 0, 1, "hinge strap fixed", hx - d * 0.02f, ky + 0.07f, (hz + fd) * 0.5f, 0.1f, 0.14f, hz - fd);
                }
            }

            // leaf slab: the vanilla bunker door sheet on both broad faces
            Box(p, 1, 0, "leaf", cx, y0 + h * 0.5f, cz, w, h, t);
            // outer face: perimeter band and two horizontal stiffeners
            float band = 0.09f, bz = outer + 0.015f;
            Box(p, 1, 1, "band top", cx, y0 + h - band * 0.5f, bz, w, band, 0.03f);
            Box(p, 1, 1, "band bottom", cx, y0 + band * 0.5f, bz, w, band, 0.03f);
            Box(p, 1, 1, "band hinge", d * (edge + band * 0.5f), y0 + h * 0.5f, bz, band, h - 2f * band, 0.03f);
            Box(p, 1, 1, "band lock", d * (edge + w - band * 0.5f), y0 + h * 0.5f, bz, band, h - 2f * band, 0.03f);
            Box(p, 1, 1, "stiffener low", cx, y0 + h * 0.22f, bz, w - 2f * band, 0.07f, 0.03f);
            Box(p, 1, 1, "stiffener high", cx, y0 + h * 0.80f, bz, w - 2f * band, 0.07f, 0.03f);
            // inner face: ribs (painted), kept inside the clear opening so the
            // closed leaf never cuts the frame; ox = opening centre, leaf-local
            float rz = inner - 0.025f, ox = -hx, rw = spec.OpenW - 0.12f, rh = spec.OpenH - 0.12f;
            Box(p, 1, 1, "rib inner low", ox, rh * 0.25f + 0.06f, rz, rw, 0.08f, 0.05f);
            Box(p, 1, 1, "rib inner high", ox, rh * 0.8f + 0.06f, rz, rw, 0.08f, 0.05f);
            Box(p, 1, 1, "rib inner mid", ox, rh * 0.5f + 0.06f, rz, 0.08f, rh, 0.05f);
            // hinge straps on the leaf (outer face) around each knuckle
            for (int i = 0; i < 3; i++)
            {
                float ky = y0 + 0.28f + i * (h - 0.56f) * 0.5f;
                Box(p, 1, 2, "hinge strap", d * (edge + 0.2f), ky - 0.07f, bz + 0.02f, 0.5f, 0.12f, 0.02f);
                Cyl(p, 1, 2, "hinge knuckle leaf", 0f, ky - 0.07f, 0f, 0.05f, 0.14f, 1);
                Box(p, 1, 2, "hinge arm", d * 0.5f * (edge + 0.02f), ky - 0.07f, (outer + 0f) * 0.5f, edge + 0.04f, 0.12f, Math.Abs(outer) + 0.04f);
            }
            // wheel boss and the outer pull handle
            float wx = d * (edge + w * 0.5f), wy = spec.WheelY;
            Cyl(p, 1, 2, "wheel boss", wx, wy, outer + 0.04f, 0.09f, 0.08f, 2);
            Box(p, 1, 2, "pull handle", d * (edge + w - 0.22f), wy, outer + 0.07f, 0.04f, 0.42f, 0.04f);
            Box(p, 1, 2, "pull handle foot", d * (edge + w - 0.22f), wy + 0.19f, outer + 0.035f, 0.05f, 0.05f, 0.07f);
            Box(p, 1, 2, "pull handle foot", d * (edge + w - 0.22f), wy - 0.19f, outer + 0.035f, 0.05f, 0.05f, 0.07f);
            // dog linkage bars (painted) from the boss to the dogs
            float lockX = ox + d * (spec.OpenW * 0.5f - 0.04f), barTop = spec.OpenH - 0.04f;
            Box(p, 1, 1, "dog bar lock", (wx + lockX) * 0.5f, wy, inner - 0.07f, Math.Abs(lockX - wx), 0.05f, 0.03f);
            Box(p, 1, 1, "dog bar up", wx, (wy + barTop) * 0.5f, inner - 0.07f, 0.05f, barTop - wy, 0.03f);
            Box(p, 1, 1, "dog bar down", wx, (wy + 0.04f) * 0.5f, inner - 0.07f, 0.05f, wy - 0.04f, 0.03f);

            // dogs (group 3): bolts in the leaf's thickness that shoot past its
            // edges into the frame keepers; they pull back by DogPull (Axis: 0 x, 1 y)
            Dog(p, d * (edge + w + 0.03f), wy + 0.35f, cz, 0.16f, 0.06f, 0.05f, 0);
            Dog(p, d * (edge + w + 0.03f), wy - 0.35f, cz, 0.16f, 0.06f, 0.05f, 0);
            Dog(p, wx, y0 + h + 0.03f, cz, 0.06f, 0.16f, 0.05f, 1);
            Dog(p, wx, y0 + 0.03f, cz, 0.06f, 0.16f, 0.05f, 1);

            // wheel (group 2) about its hub on the outer face: rim ring, four spokes, hub
            float rimR = Math.Min(0.3f, w * 0.17f), tube = 0.022f;
            DoorPart r = new DoorPart();
            r.Group = 2; r.Mat = 2; r.Shape = 2; r.Name = "wheel rim";
            r.X = 0f; r.Y = 0f; r.Z = 0.11f; r.SX = rimR; r.SY = tube; r.SZ = 0f; r.Axis = 2;
            p.Add(r);
            Box(p, 2, 2, "wheel spoke", 0f, 0f, 0.09f, rimR * 2f, 0.03f, 0.025f);
            Box(p, 2, 2, "wheel spoke", 0f, 0f, 0.09f, 0.03f, rimR * 2f, 0.025f);
            Cyl(p, 2, 2, "wheel hub", 0f, 0f, 0.06f, 0.055f, 0.1f, 2);
            Box(p, 2, 1, "wheel knob", rimR, 0f, 0.15f, 0.04f, 0.04f, 0.06f);
            // inner lever (group 4) about its hub on the inner face
            Cyl(p, 4, 2, "lever hub", 0f, 0f, -0.05f, 0.06f, 0.1f, 2);
            Box(p, 4, 1, "lever", 0.22f, 0f, -0.12f, 0.48f, 0.05f, 0.04f);
            Box(p, 4, 2, "lever grip", 0.45f, 0f, -0.17f, 0.05f, 0.05f, 0.1f);
            return p;
        }

        /// <summary>Hub of the wheel (outer face) and of the lever (inner face), leaf-local.</summary>
        internal static void Hubs(DoorSpec spec, out float x, out float y, out float zOut, out float zIn)
        {
            float d = -spec.HingeSign, edge = Math.Abs(spec.HingeX) - spec.LeafW * 0.5f;
            x = d * (edge + spec.LeafW * 0.5f);
            y = spec.WheelY;
            zOut = spec.LeafZ0 + spec.LeafT - spec.HingeZ;
            zIn = spec.LeafZ0 - spec.HingeZ;
        }

        static void Box(List<DoorPart> p, int g, int m, string n, float x, float y, float z, float sx, float sy, float sz)
        {
            DoorPart b = new DoorPart();
            b.Group = g; b.Mat = m; b.Shape = 0; b.Name = n;
            b.X = x; b.Y = y; b.Z = z; b.SX = Math.Abs(sx); b.SY = Math.Abs(sy); b.SZ = Math.Abs(sz);
            p.Add(b);
        }

        static void Cyl(List<DoorPart> p, int g, int m, string n, float x, float y, float z, float radius, float length, int axis)
        {
            DoorPart b = new DoorPart();
            b.Group = g; b.Mat = m; b.Shape = 1; b.Name = n;
            b.X = x; b.Y = y; b.Z = z; b.SX = radius; b.SY = length; b.SZ = 0f; b.Axis = axis;
            p.Add(b);
        }

        static void Dog(List<DoorPart> p, float x, float y, float z, float sx, float sy, float sz, int axis)
        {
            DoorPart b = new DoorPart();
            b.Group = 3; b.Mat = 2; b.Shape = 0; b.Name = "dog";
            b.X = x; b.Y = y; b.Z = z; b.SX = sx; b.SY = sy; b.SZ = sz; b.Axis = axis;
            p.Add(b);
        }

        /// <summary>Slide direction of a dog (unit, leaf-local x or y) when it
        /// pulls back toward the wheel.</summary>
        internal static void DogDir(DoorSpec spec, DoorPart dog, out float dx, out float dy)
        {
            float hubX, hubY, a, b;
            Hubs(spec, out hubX, out hubY, out a, out b);
            dx = 0f; dy = 0f;
            if (dog.Axis == 0) dx = dog.X > hubX ? -1f : 1f;
            else dy = dog.Y > hubY ? -1f : 1f;
        }

        /// <summary>Triangles of one part (the runtime's mesh builder emits
        /// exactly these: box 12, cylinder 4*sides, ring 2*seg*sides).</summary>
        internal static int Triangles(DoorPart part, int sides, int ringSegments)
        {
            if (part.Shape == 0) return 12;
            if (part.Shape == 1) return 4 * sides;
            return 2 * ringSegments * sides;
        }

        internal const int CylSides = 10, RingSegments = 20, RingSides = 6;

        // ------------------------------------------------------------ helpers

        internal static float Clamp01(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }
        internal static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
    }
}
