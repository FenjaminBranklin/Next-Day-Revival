"""Editor ground groups, the alive layer - offline check.

1. Compiles the PRODUCTION core, Revival.GroundAliveCore.cs, UNCHANGED with
   the .NET 3.5 csc into a deterministic simulation. The world is analytic:
   flat ground, walls and low walls as boxes (a ray at a given height hits a
   box that is taller), a walk is refused through a box or out of the leash.
   The men are points that walk (3.5 u/s) or run (9 u/s) to what the brain
   orders; a man sees the threat when the line from his eye (standing 4.8,
   crouched 3.1) to its chest is not blocked by a box taller than the line.
   Scenes:
     - SPREAD: eight men dropped in the NpcWar spawn ring (3 + 0.4 n units)
       with 'roam': they walk apart onto posts, never stacked;
     - ROAM: ten minutes of 'roam': rounds happen, a third of the men at most
       are out at once, every stop and every walk stays inside the radius,
       idle pauses and look-arounds at the posts;
     - HOLD: the 'guard' (hold position) posts NpcWar.GuardPosts lays out -
       its ring formula read from Revival.NpcCombat.cs - are not stacked;
     - CONTACT: a threat appears 40 m off a group between walls: men take
       cached cover (hidden from the threat), some flank wide, one calls: a
       friendly group 100 m away is alerted, a group 250 m away is not;
       against the stock behaviour (stand where they are) the hidden share is
       much higher; after the threat is gone they search, then walk back to
       their posts;
     - COST: 0, 16 and 63 sleeping groups beside one awake group: proximity
       tests, thinks and world calls per frame stay within the board caps
       and do NOT grow with the number of far groups, far groups never think,
       and the steady state allocates nothing (GC.GetTotalMemory).
2. Source wiring of the game adapter (Revival.GroundAlive.cs and seams):
   editor groups only, the F6 slot, far groups skipped, no allocation in
   the per-frame paths, the TSV/editor defaults.

It does not run Unity, PhysX, the NavMesh, the animator or Photon: how the
group looks in a fight is the in-game list's job.
"""
from pathlib import Path
import math
import os
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
NL = chr(10)


def read(name):
    return (ROOT / name).read_text(encoding='utf-8')


CORE = read('Revival.GroundAliveCore.cs')

HARNESS = r'''
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(0f, 0f, 0f); } }
        public static Vector3 up { get { return new Vector3(0f, 1f, 0f); } }
        public float sqrMagnitude { get { return x * x + y * y + z * z; } }
        public float magnitude { get { return (float)Math.Sqrt(x * x + y * y + z * z); } }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static Vector3 operator -(Vector3 a) { return new Vector3(-a.x, -a.y, -a.z); }
        public static Vector3 operator *(Vector3 a, float d) { return new Vector3(a.x * d, a.y * d, a.z * d); }
        public static Vector3 operator *(float d, Vector3 a) { return new Vector3(a.x * d, a.y * d, a.z * d); }
        public static Vector3 operator /(Vector3 a, float d) { return new Vector3(a.x / d, a.y / d, a.z / d); }
        public static float Dot(Vector3 a, Vector3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Deg2Rad = (float)(Math.PI / 180.0);
        public static float Sin(float f) { return (float)Math.Sin(f); }
        public static float Cos(float f) { return (float)Math.Cos(f); }
        public static float Sqrt(float f) { return (float)Math.Sqrt(f); }
        public static float Abs(float f) { return Math.Abs(f); }
        public static float Min(float a, float b) { return a < b ? a : b; }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static int Max(int a, int b) { return a > b ? a : b; }
        public static float Clamp(float v, float a, float b) { return v < a ? a : v > b ? b : v; }
    }
}

// PRODUCTION: Revival.GroundAliveCore.cs, unchanged
CORE_SOURCE

namespace NextDayRevival
{
    using UnityEngine;

    sealed class Box { public float X0, Z0, X1, Z1, H; }

    sealed class World : IGroundWorld
    {
        public readonly List<Box> Boxes = new List<Box>();
        public readonly List<Vector3> Players = new List<Vector3>();
        public long Calls, NearCalls;

        public void Wall(float cx, float cz, float hx, float hz, float h)
        {
            Box b = new Box(); b.X0 = cx - hx; b.X1 = cx + hx; b.Z0 = cz - hz; b.Z1 = cz + hz; b.H = h;
            Boxes.Add(b);
        }

        // Slab test of a flat segment against a box footprint; t in 0..1.
        static bool Hit(Box b, float ax, float az, float dx, float dz, out float t)
        {
            float t0 = 0f, t1 = 1f;
            t = 0f;
            if (!Slab(ax, dx, b.X0, b.X1, ref t0, ref t1)) return false;
            if (!Slab(az, dz, b.Z0, b.Z1, ref t0, ref t1)) return false;
            t = t0;
            return true;
        }

        static bool Slab(float a, float d, float lo, float hi, ref float t0, ref float t1)
        {
            if (Math.Abs(d) < 1e-6f) return a >= lo && a <= hi;
            float u0 = (lo - a) / d, u1 = (hi - a) / d;
            if (u0 > u1) { float s = u0; u0 = u1; u1 = s; }
            if (u0 > t0) t0 = u0;
            if (u1 < t1) t1 = u1;
            return t0 <= t1;
        }

        public bool Inside(float x, float z)
        {
            for (int i = 0; i < Boxes.Count; i++)
            {
                Box b = Boxes[i];
                if (x > b.X0 - 1f && x < b.X1 + 1f && z > b.Z0 - 1f && z < b.Z1 + 1f) return true;
            }
            return false;
        }

        public bool Cast(Vector3 from, Vector3 dir, float range, out float distance)
        {
            Calls++;
            distance = range;
            bool any = false;
            for (int i = 0; i < Boxes.Count; i++)
            {
                Box b = Boxes[i];
                if (b.H <= from.y) continue;
                float t;
                if (Hit(b, from.x, from.z, dir.x * range, dir.z * range, out t) && t * range < distance)
                { distance = t * range; any = true; }
            }
            return any;
        }

        public bool Stand(Vector3 near, float reach, out Vector3 at)
        {
            Calls++;
            at = new Vector3(near.x, 0f, near.z);
            return !Inside(near.x, near.z);
        }

        public bool Blocked(Vector3 a, Vector3 b, float hA, float hB)
        {
            for (int i = 0; i < Boxes.Count; i++)
            {
                Box x = Boxes[i];
                float t;
                if (!Hit(x, a.x, a.z, b.x - a.x, b.z - a.z, out t)) continue;
                float h = hA + (hB - hA) * t;
                if (x.H > h) return true;
            }
            return false;
        }

        public bool Walk(Vector3 from, Vector3 to, Vector3 home, float leash)
        {
            Calls++;
            if (Blocked(from, to, 0.5f, 0.5f)) return false;
            for (int k = 0; k <= 8; k++)
            {
                Vector3 p = from + (to - from) * (k / 8f);
                p.y = 0f;
                Vector3 d = p - home; d.y = 0f;
                if (d.magnitude > leash) return false;
            }
            return true;
        }

        public bool PlayerNear(Vector3 p, float range)
        {
            NearCalls++;
            for (int i = 0; i < Players.Count; i++)
            {
                Vector3 d = Players[i] - p; d.y = 0f;
                if (d.sqrMagnitude < range * range) return true;
            }
            return false;
        }
    }

    static class Program
    {
        const float Dt = 1f / 30f;
        static int Fails;

        static void Ok(bool c, string what)
        {
            Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + what);
            if (!c) Fails++;
        }

        static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        static GroundBrain Group(Vector3 home, int n, float radius, byte duty, int seed)
        {
            GroundBrain b = new GroundBrain(n, home, radius, duty, seed);
            for (int i = 0; i < n; i++)
            {
                // NpcWar's spawn ring (RevivalGroundEnemies.Spawn).
                float a = i * (float)Math.PI * 2f / n, r = n == 1 ? 0f : 3f + n * 0.4f;
                b.Men[i].Pos = home + new Vector3((float)Math.Cos(a) * r, 0f, (float)Math.Sin(a) * r);
                b.Men[i].Alive = true;
            }
            return b;
        }

        // The adapter's half of a frame for one awake group: move the men
        // toward their orders, read what they see.
        static void Step(World w, GroundBrain b, bool threatOn, Vector3 threat, float t)
        {
            for (int i = 0; i < b.Count; i++)
            {
                GroundMan m = b.Men[i];
                if (!m.Alive) continue;
                if (m.Order != m.Issued) m.Issued = m.Order;
                if (m.Moving)
                {
                    Vector3 d = m.Dest - m.Pos; d.y = 0f;
                    float len = d.magnitude, step = (m.Run ? 9f : 3.5f) * Dt;
                    if (len > 0.01f) m.Pos = len <= step ? m.Dest : m.Pos + d * (step / len);
                }
                bool sees = threatOn && Flat(threat - m.Pos) < 180f
                    && !w.Blocked(m.Pos, threat, m.Crouch && !m.Moving ? 3.1f : 4.8f, 4f);
                m.Sees = sees;
                m.HasThreat = sees;
                m.Threat = threat;
            }
        }

        static bool Hidden(World w, GroundMan m, Vector3 threat)
        {
            return w.Blocked(m.Pos, threat, m.Crouch && !m.Moving ? 3.1f : 4.8f, 4f);
        }

        static void Run(World w, GroundBoard board, float from, float to, bool threatOn, Vector3 threat)
        {
            for (float t = from; t < to; t += Dt)
            {
                board.Frame(w, t);
                for (int k = 0; k < board.Brains.Count; k++)
                    if (board.Brains[k].Active) Step(w, board.Brains[k], threatOn, threat, t);
            }
        }

        static float MinGap(GroundBrain b)
        {
            float gap = float.MaxValue;
            for (int i = 0; i < b.Count; i++)
                for (int j = i + 1; j < b.Count; j++)
                    gap = Math.Min(gap, Flat(b.Men[i].Pos - b.Men[j].Pos));
            return gap;
        }

        static void Spread()
        {
            Console.WriteLine("SPREAD (roam, 8 men dropped in the spawn ring, radius 200)");
            World w = new World();
            w.Players.Add(new Vector3(0f, 0f, 150f));
            GroundBoard board = new GroundBoard();
            GroundBrain b = Group(Vector3.zero, 8, 200f, GroundDuty.Roam, 3);
            board.Add(b);
            float before = MinGap(b);
            // Only the posts: no rounds in the first check.
            for (int i = 0; i < b.Count; i++) b.Men[i].NextRound = 1e9f;
            Run(w, board, 0f, 40f, false, Vector3.zero);
            float gap = MinGap(b);
            int atPost = 0;
            for (int i = 0; i < b.Count; i++) if (Flat(b.Men[i].Pos - b.Men[i].Post) <= 4.5f) atPost++;
            Console.WriteLine("    spawn gap " + before.ToString("0.0") + " u, after 40 s " + gap.ToString("0.0")
                + " u (" + (gap / 2.8f).ToString("0.0") + " m), " + atPost + "/8 at their posts, ready " + b.Ready);
            Ok(before < 5f, "the men start stacked (spawn ring gap under 2 m)");
            Ok(atPost == 8, "every man walked to his own post");
            Ok(gap >= 14f, "the posts are spread: no two men closer than 5 m");
            float far = 0f;
            for (int i = 0; i < b.Count; i++) far = Math.Max(far, Flat(b.Men[i].Post));
            Ok(far <= 0.6f * 200f + 1f, "every post lies inside the radius");
        }

        static void Roam()
        {
            Console.WriteLine("ROAM (8 men, radius 200, 10 minutes, a player near)");
            World w = new World();
            w.Wall(60f, 20f, 4f, 30f, 8f);
            w.Players.Add(new Vector3(0f, 0f, 150f));
            GroundBoard board = new GroundBoard();
            GroundBrain b = Group(Vector3.zero, 8, 200f, GroundDuty.Roam, 7);
            board.Add(b);
            float maxOut = 0f, maxOutCount = 0f, calmMoveSeconds = 0f, idleSeconds = 0f;
            int rounds = 0, looks = 0;
            int[] lastOrder = new int[8];
            Vector3[] lastLook = new Vector3[8];
            for (float t = 0f; t < 600f; t += Dt)
            {
                board.Frame(w, t);
                Step(w, b, false, Vector3.zero, t);
                int outNow = 0;
                for (int i = 0; i < 8; i++)
                {
                    GroundMan m = b.Men[i];
                    maxOut = Math.Max(maxOut, Flat(m.Pos));
                    if (m.Act == GroundAct.Round) outNow++;
                    if (m.Moving) { calmMoveSeconds += Dt; if (m.Run) maxOut = 1e9f; }
                    else idleSeconds += Dt;
                    if (m.Order != lastOrder[i] && m.Act == GroundAct.Round) rounds++;
                    lastOrder[i] = m.Order;
                    if (Flat(m.Look - lastLook[i]) > 0.01f) looks++;
                    lastLook[i] = m.Look;
                }
                maxOutCount = Math.Max(maxOutCount, outNow);
            }
            Console.WriteLine("    " + rounds + " round legs, " + looks + " look-arounds, at most " + maxOutCount
                + " men out at once, furthest " + maxOut.ToString("0.0") + " u from home, walking "
                + (100f * calmMoveSeconds / (calmMoveSeconds + idleSeconds)).ToString("0") + " % of the time");
            Ok(rounds >= 20, "the men walk rounds (at least 20 legs in ten minutes)");
            Ok(maxOutCount <= 2f, "at most a third of the group is out on a round at once");
            Ok(maxOut <= 200f, "every man stays inside the radius, no calm run");
            Ok(looks >= 100, "idle men look around at their posts and stops");
            float share = calmMoveSeconds / (calmMoveSeconds + idleSeconds);
            Ok(share > 0.1f && share < 0.6f, "slow rounds with idle pauses (walking 10..60 % of the time)");
        }

        static void Contact()
        {
            Console.WriteLine("CONTACT (8 men between walls, threat 40 m off; friends 100 m and 250 m away)");
            World w = new World();
            // Low walls and a house corner around the post.
            w.Wall(30f, 10f, 2f, 12f, 3.6f);
            w.Wall(-25f, 20f, 10f, 2f, 3.6f);
            w.Wall(10f, 40f, 12f, 1.5f, 9f);
            w.Wall(-5f, -30f, 1.5f, 10f, 3.6f);
            w.Wall(45f, -25f, 8f, 8f, 9f);
            w.Wall(-40f, -10f, 2f, 8f, 3.6f);
            w.Players.Add(new Vector3(0f, 0f, 60f));
            GroundBoard board = new GroundBoard();
            GroundBrain b = Group(Vector3.zero, 8, 120f, GroundDuty.Roam, 11);
            GroundBrain near = Group(new Vector3(280f, 0f, 0f), 4, 100f, GroundDuty.Hold, 12);
            GroundBrain far = Group(new Vector3(-700f, 0f, 0f), 4, 100f, GroundDuty.Hold, 13);
            // A group of another side (a traitor group, or another faction)
            // in reach is not called.
            GroundBrain foe = Group(new Vector3(-150f, 0f, 60f), 3, 60f, GroundDuty.Hold, 14);
            foe.Side = 7;
            board.Add(b); board.Add(near); board.Add(far); board.Add(foe);
            for (int i = 0; i < b.Count; i++) b.Men[i].NextRound = 1e9f;
            Run(w, board, 0f, 40f, false, Vector3.zero);
            Vector3 threat = new Vector3(112f, 0f, 0f);
            int stockHidden = 0;
            for (int i = 0; i < b.Count; i++) if (Hidden(w, b.Men[i], threat)) stockHidden++;
            Run(w, board, 40f, 52f, true, threat);
            bool called = b.Calls == 1;
            int hidden = 0, cover = 0, crouched = 0, flank = 0;
            for (int i = 0; i < b.Count; i++)
            {
                GroundMan m = b.Men[i];
                if (Hidden(w, m, threat)) hidden++;
                if (m.Act == GroundAct.Cover) cover++;
                if (m.Crouch) crouched++;
                if (m.Act == GroundAct.Flank) flank++;
            }
            Console.WriteLine("    phase " + GroundPhase.Name(b.Phase) + ", " + cover + " in cover, " + flank
                + " flanking, " + crouched + " down, hidden from the threat " + hidden + "/8 (stock: standing at "
                + "their posts " + stockHidden + "/8); cache " + b.Cover.Count + " spots from "
                + b.Cover.Calls + " world calls; near friend " + GroundPhase.Name(near.Phase)
                + ", far friend " + GroundPhase.Name(far.Phase));
            Ok(b.Phase == GroundPhase.Contact, "a sighting puts the group in contact");
            Ok(b.Cover.Done && b.Cover.Count >= 6, "the cover cache was built before the fight (6+ spots)");
            Ok(cover >= 3, "men take cached cover (3+ of 8)");
            Ok(b.Flanks >= 1 && flank >= 1, "some men flank");
            Ok(hidden >= 4 && hidden >= stockHidden + 3, "hidden from the threat: 4+ of 8 and 3+ more than stock");
            Ok(called, "one man calls the friends (once)");
            Ok(foe.Phase == GroundPhase.Calm, "a group of another side in reach is not called");
            Ok(near.Phase == GroundPhase.Contact && far.Phase == GroundPhase.Calm,
               "the friend 100 m away reacts, the one 250 m away does not");
            float flankGap = 0f;
            for (int i = 0; i < b.Count; i++)
                if (b.Men[i].Act == GroundAct.Flank)
                {
                    Vector3 r = b.Men[i].Pos - threat;
                    flankGap = Math.Max(flankGap, Math.Abs(r.z));
                }
            Ok(flankGap >= 25f, "a flanker stands wide of the threat's line (9+ m off it)");
            // The threat is gone: search, then back to the posts.
            float searchAt = -1f, calmAt = -1f, lastBack = -1f;
            bool[] reached = new bool[b.Count];
            for (float t = 52f; t < 200f; t += Dt)
            {
                board.Frame(w, t);
                for (int k = 0; k < board.Brains.Count; k++)
                    if (board.Brains[k].Active) Step(w, board.Brains[k], false, threat, t);
                if (searchAt < 0f && b.Phase == GroundPhase.Search) searchAt = t;
                if (searchAt > 0f && calmAt < 0f && b.Phase == GroundPhase.Calm) calmAt = t;
                if (calmAt > 0f)
                    for (int i = 0; i < b.Count; i++)
                        if (!reached[i] && Flat(b.Men[i].Pos - b.Men[i].Post) <= 4.5f) { reached[i] = true; lastBack = t; }
            }
            int searchers = b.Searches > 0 ? 1 : 0, home = 0;
            // Every man has been back at his own post since the search ended
            // (some are out on a new round again by now: that is roaming).
            for (int i = 0; i < b.Count; i++) if (reached[i]) home++;
            Console.WriteLine("    search from " + searchAt.ToString("0.0") + " s, calm again " + calmAt.ToString("0.0")
                + " s, back at their posts " + home + "/8 (the last at " + lastBack.ToString("0.0") + " s)");
            Ok(searchAt > 52f && searchAt < 70f, "the group searches 15 s after the last sighting");
            Ok(calmAt > searchAt && calmAt < searchAt + 35f, "and returns to calm after the search");
            Ok(home == 8 && lastBack - calmAt < 90f, "every man walks back to his post");
            Ok(near.ResumePending || near.Phase == GroundPhase.Calm, "the called hold group goes back to its duty");
        }

        static void Cost()
        {
            Console.WriteLine("COST (one awake group of 12 beside 0 / 16 / 63 sleeping groups of 12, and 63 of 1)");
            int[] counts = new int[] { 0, 16, 63, 63 };
            int[] size = new int[] { 12, 12, 12, 1 };
            float[] perFrame = new float[4];
            int[] maxNear = new int[4], maxThinks = new int[4], maxCalls = new int[4], farThinks = new int[4];
            long[] alloc = new long[4];
            double[] micro = new double[4];
            for (int c = 0; c < 4; c++)
            {
                World w = new World();
                w.Wall(20f, 0f, 2f, 10f, 3.6f);
                w.Players.Add(new Vector3(0f, 0f, 50f));
                GroundBoard board = new GroundBoard();
                GroundBrain awake = Group(Vector3.zero, 12, 200f, GroundDuty.Roam, 21);
                board.Add(awake);
                List<GroundBrain> sleepers = new List<GroundBrain>();
                for (int k = 0; k < counts[c]; k++)
                {
                    GroundBrain s = Group(new Vector3(1500f + 300f * (k % 8), 0f, 1500f + 300f * (k / 8)), size[c], 200f,
                        (byte)(k % 4), 100 + k);
                    board.Add(s); sleepers.Add(s);
                }
                // Warm: posts and cache built, every code path jitted once.
                Run(w, board, 0f, 180f, false, Vector3.zero);
                long calls0 = w.Calls, near0 = w.NearCalls;
                int thinks0 = 0;
                for (int k = 0; k < sleepers.Count; k++) thinks0 += sleepers[k].Thinks;
                Stopwatch sw = new Stopwatch();
                int frames = 0;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long mem0 = GC.GetTotalMemory(false);
                sw.Start();
                for (float t = 180f; t < 480f; t += Dt)
                {
                    board.Frame(w, t);
                    Step(w, awake, false, Vector3.zero, t);
                    frames++;
                    maxNear[c] = Math.Max(maxNear[c], board.FrameNear);
                    maxThinks[c] = Math.Max(maxThinks[c], board.FrameThinks);
                    maxCalls[c] = Math.Max(maxCalls[c], board.FrameCalls);
                }
                sw.Stop();
                alloc[c] = GC.GetTotalMemory(false) - mem0;
                int thinks1 = 0;
                for (int k = 0; k < sleepers.Count; k++) thinks1 += sleepers[k].Thinks;
                farThinks[c] = thinks1 - thinks0;
                perFrame[c] = (float)(w.NearCalls - near0 + (w.Calls - calls0)) / frames;
                micro[c] = sw.Elapsed.TotalMilliseconds * 1000.0 / frames;
                Console.WriteLine("    " + counts[c] + " x " + size[c] + " sleeping: " + perFrame[c].ToString("0.000")
                    + " world+proximity calls/frame, peak proximity " + maxNear[c] + ", thinks " + maxThinks[c]
                    + ", setup calls " + maxCalls[c] + " per frame; sleeping thinks " + farThinks[c]
                    + "; " + micro[c].ToString("0.00") + " us/frame (board + sim); managed growth "
                    + alloc[c] + " B");
            }
            Ok(maxNear[2] <= GroundBoard.ScanPerFrame && maxThinks[2] <= GroundBoard.ThinksPerFrame
               && maxCalls[2] <= GroundBoard.WorldBudget, "per-frame proximity tests, thinks and setup calls stay within the caps");
            Ok(farThinks[0] == 0 && farThinks[1] == 0 && farThinks[2] == 0 && farThinks[3] == 0, "sleeping groups never think");
            // A sleeping group costs one proximity test a second whatever its size,
            // and the board never tests more than ScanPerFrame groups in a frame.
            Ok(Math.Abs(perFrame[2] - perFrame[3]) < 0.01f, "the cost does not grow with the far NPC count (63 groups of 12 cost what 63 groups of 1 cost)");
            Ok(perFrame[2] - perFrame[0] <= GroundBoard.ScanPerFrame, "63 sleeping groups add at most the capped proximity tests per frame");
            Ok(alloc[0] <= 0 && alloc[1] <= 0 && alloc[2] <= 0 && alloc[3] <= 0, "no managed allocation in the steady state");
        }

        static int Main()
        {
            Spread();
            Roam();
            Contact();
            Cost();
            Console.WriteLine(Fails == 0 ? "ALL PASS" : Fails + " FAIL(S)");
            return Fails == 0 ? 0 : 1;
        }
    }
}
'''

FAILS = []


def ok(cond, what):
    print(('  PASS  ' if cond else '  FAIL  ') + what)
    if not cond:
        FAILS.append(what)


def method(src, signature):
    """The brace-balanced body that follows signature (empty: not found)."""
    at = src.find(signature)
    if at < 0:
        return ''
    start = src.find('{', at)
    depth = 0
    for i in range(start, len(src)):
        if src[i] == '{':
            depth += 1
        elif src[i] == '}':
            depth -= 1
            if depth == 0:
                return src[start:i + 1]
    return ''


def allocates(body):
    """new of a reference type, a string built, LINQ or a lambda."""
    news = re.findall(r'\bnew\s+([A-Za-z_][A-Za-z0-9_<>\[\]]*)', body)
    news = [n for n in news if n not in ('Vector3', 'GroundSpot')]
    return news or '+ "' in body or '" +' in body or '=>' in body or '.Select(' in body


def run_harness():
    compiler = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework/v3.5/csc.exe'
    if not compiler.exists():
        print('  FAIL  csc 3.5 not found at %s' % compiler)
        return 1
    lines = CORE.split(NL)
    body = NL.join(l for l in lines if not l.startswith('using '))
    src = HARNESS.replace('CORE_SOURCE', body)
    work = ROOT / 'build' / 'ground_alive_check'
    work.mkdir(parents=True, exist_ok=True)
    cs = work / 'check.cs'
    exe = work / 'check.exe'
    cs.write_bytes(src.encode('utf-8'))
    built = subprocess.run([str(compiler), '/nologo', '/warn:0', '/optimize+', '/codepage:65001',
                            '/out:' + str(exe), str(cs)],
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out = built.stdout.decode('utf-8', 'replace')
    if built.returncode != 0:
        print(out)
        print('  FAIL  the harness does not compile with csc 3.5')
        return 1
    ran = subprocess.run([str(exe)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    print(ran.stdout.decode('utf-8', 'replace').rstrip())
    return 0 if ran.returncode == 0 else 1


def hold_ring():
    """GuardPosts' ring (the 'guard' / hold position duty): read the formula
    from the source and check that no two men share a spot."""
    print('HOLD (guard: NpcWar.GuardPosts ring, read from Revival.NpcCombat.cs)')
    npc = read('Revival.NpcCombat.cs')
    m = re.search(r'float radius = Mathf\.Clamp\(([\d.]+)f \+ ([\d.]+)f \* ringN, ([\d.]+)f, ([\d.]+)f\);', npc)
    ok(m is not None, 'the ring formula is where it was')
    if not m:
        return
    a, b, lo, hi = (float(x) for x in m.groups())
    worst = 1e9
    for n in range(2, 13):
        r = min(max(a + b * n, lo), hi)
        gap = 2 * r * math.sin(math.pi / n)
        worst = min(worst, gap)
    print('    closest neighbours on the ring over 2..12 men: %.1f u (%.1f m)' % (worst, worst / 2.8))
    ok(worst >= 10.0, 'hold position spreads the men (no two closer than 3.5 m)')


def wiring():
    print('Source wiring')
    adapter = read('Revival.GroundAlive.cs')
    combat = read('Revival.NpcCombat.cs')
    ground = read('Revival.GroundEnemies.cs')
    prof = read('RevivalFrameProfiler.cs')
    gdef = read('grounddef.py')
    gjs = read('editor/ground.js')
    sync = read('sync_public.py')

    ok('internal sealed class PhysicsGroundWorld : IGroundWorld' in adapter
       and 'MercCoverService' not in method(adapter, 'public bool PlayerNear(')
       and '_cover.Cast(' in adapter,
       'the game world reuses the M1 cover world (non-alloc rays, people are no hit)')
    ok('GetCornersNonAlloc' in adapter and 'new NavMeshPath()' not in method(adapter, 'public bool Walk('),
       'the walk check reuses one NavMeshPath and a corner buffer')
    hot = [('Frame', 'static void GroundAliveFrame('), ('Gate', 'static bool GroundAliveGate('),
           ('Busy', 'static bool GroundAliveBusy('), ('Step', 'static bool GroundAliveStep('),
           ('Inputs', 'static void GroundAliveInputs('), ('Cast', 'public bool Cast('),
           ('Stand', 'public bool Stand('), ('Walk', 'public bool Walk('), ('PlayerNear', 'public bool PlayerNear(')]
    badhot = [n for n, sig in hot if not method(adapter, sig) or allocates(method(adapter, sig))]
    ok(not badhot, 'no allocation in the per-frame paths (%s)' % (', '.join(badhot) if badhot else
                                                                  ', '.join(n for n, _ in hot)))
    core_hot = [('Think', 'internal void Think('), ('Frame', 'internal void Frame('),
                ('Build', 'internal int Build('), ('Best', 'internal int Best(')]
    badcore = [n for n, sig in core_hot if not method(CORE, sig) or allocates(method(CORE, sig))]
    ok(not badcore, 'no allocation in the core think / frame / cover paths')
    run = method(combat, 'static void RunGround(Squad s, float now)')
    ok('if (s.Alive != null && !GroundAliveGate(s, now)) return;' in run,
       'RunGround skips a sleeping group before any per-man work')
    ok('if (s.Alive != null && GroundAliveBusy(f, s, i, now)) continue;' in run
       and 'if (s.Alive != null && GroundAliveStep(f, s, i, now)) continue;' in run,
       'the alive layer moves the men; the NpcWar fire code still shoots')
    ok('MainRun' not in run, 'RunGround itself still never runs (the runs to cover are in the adapter)')
    tick = method(combat, 'public static void Tick()')
    ok('GroundAliveFrame(now);' in tick and 'FrameProf.S(FrameProf.S_GroundAliveT)' in tick,
       'the board runs once per NpcWar tick inside its own F6 slot')
    ok('public const int S_GroundAliveT = ' in prof and '"GroundAlive.Tick"' in prof,
       'F6 FrameProfiler slot GroundAlive.Tick')
    ok('if (!g.Builtin) NpcWar.GroundAliveOn(g.Tag, g.Faction);' in ground,
       'only editor groups get the alive layer (airfield / military town pockets keep theirs)')
    ok('g.Behavior == "waiting") g.Behavior = "roam";' in ground,
       'a published legacy "waiting" group roams')
    ok("BEHAVIORS = [\"roam\", \"patrol\", \"guard\", \"walking\"]" in gdef
       and '("behavior", "roam")' in gdef and 'LEGACY_BEHAVIORS = {"waiting": "roam"}' in gdef,
       'the editor offers roam / patrol / hold / wander, roam by default, old data becomes roam')
    ok("roam: 'Guard with roam" in gjs and "behavior: 'roam'" in gjs,
       'the editor field names the behaviours and defaults to guard with roam')
    ok('"Revival.GroundAlive.cs"' in sync and '"Revival.GroundAliveCore.cs"' in sync,
       'both new sources go to the public repository (it must compile)')
    ascii_ok = all(ord(ch) < 127 for ch in adapter + CORE)
    ok(ascii_ok, 'the new sources are ASCII')


def main():
    code = run_harness()
    hold_ring()
    wiring()
    if code != 0 or FAILS:
        print('FAIL')
        return 1
    print('PASS: editor ground groups alive - spread, roam, hold, cover on contact, call, search, return, cost')
    return 0


if __name__ == '__main__':
    sys.exit(main())
