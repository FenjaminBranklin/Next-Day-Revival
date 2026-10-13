// Headless test of Revival.C4Core.cs (C4 chain radius, per-player limit,
// timer). Compiled together with the production core by
// research/c4_core_check.py (csc 3.5); prints "N PASS, M FAIL".
using System;

namespace NextDayRevival
{
    static class C4CoreTest
    {
        static int Pass, Fail;
        static void Ok(bool c, string what) { if (c) Pass++; else { Fail++; Console.WriteLine("FAIL " + what); } }
        static bool Near(float a, float b) { return Math.Abs(a - b) < 1e-4f; }

        static int Main()
        {
            const float K = C4Core.K;
            float chain = 8f * K;

            // ------------------------------------------------ chain radius
            // A line of charges 7 m apart: each relays to the next, so all go.
            float[] xyz = new float[] { 0f, 0f, 0f, 7f * K, 0f, 0f, 14f * K, 0f, 0f, 21f * K, 0f, 0f };
            bool[] live = new bool[] { true, true, true, true };
            bool[] seen = new bool[4];
            int[] order = new int[4];
            int n = C4Core.Chain(xyz, live, 4, 0, chain, seen, order);
            Ok(n == 4 && order[0] == 0 && order[1] == 1 && order[2] == 2 && order[3] == 3,
               "a 7 m line chains end to end in order");

            // 9 m apart: out of reach, only the first goes.
            float[] far = new float[] { 0f, 0f, 0f, 9f * K, 0f, 0f };
            n = C4Core.Chain(far, new bool[] { true, true }, 2, 0, chain, new bool[2], new int[2]);
            Ok(n == 1, "a charge 9 m away does not go off");

            // Exactly at 8 m: inside (radius inclusive); 3D distance counts.
            float[] edge = new float[] { 0f, 0f, 0f, 0f, 8f * K, 0f };
            n = C4Core.Chain(edge, new bool[] { true, true }, 2, 0, chain, new bool[2], new int[2]);
            Ok(n == 2, "a charge exactly 8 m above goes off");

            // A gap (a defused / removed charge in the middle) breaks the line.
            live = new bool[] { true, false, true, true };
            n = C4Core.Chain(xyz, live, 4, 0, chain, seen, order);
            Ok(n == 1, "a removed charge never relays the chain");

            // Start from the middle: both directions, start first.
            live = new bool[] { true, true, true, true };
            n = C4Core.Chain(xyz, live, 4, 2, chain, seen, order);
            Ok(n == 4 && order[0] == 2, "the chain spreads both ways from the first charge");

            // Not-live start or bad index: nothing.
            Ok(C4Core.Chain(xyz, new bool[] { false, true, true, true }, 4, 0, chain, seen, order) == 0,
               "a charge that is gone does not start a chain");
            Ok(C4Core.Chain(xyz, live, 4, 9, chain, seen, order) == 0, "an index out of range is refused");
            Ok(C4Core.Chain(xyz, live, 4, 0, float.NaN, seen, order) == 1, "a NaN radius chains nothing");

            // ------------------------------------------------------- limit
            int[] owners = new int[] { 1, 1, 2, 1, 1, 1, 3 };
            int own = C4Core.CountOwn(owners, owners.Length, 1);
            Ok(own == 5, "five own charges counted");
            Ok(!C4Core.MayPlace(own, 5), "the sixth charge is refused at limit 5");
            Ok(C4Core.MayPlace(C4Core.CountOwn(owners, owners.Length, 2), 5), "another player may still place");
            Ok(C4Core.MayPlace(4, 5), "the fifth charge is allowed");
            Ok(C4Core.MayPlace(0, 0) && !C4Core.MayPlace(1, 0), "a limit of 0 still means one charge");

            // ------------------------------------------------------- timer
            Ok(C4Core.TimerSteps[0] == 0, "step 0 is remote only");
            Ok(C4Core.TimerSteps[1] == 10 && C4Core.TimerSteps[C4Core.TimerSteps.Length - 1] == 300,
               "timer steps run from 10 s to 5 min");
            bool rising = true;
            for (int i = 1; i < C4Core.TimerSteps.Length; i++) rising &= C4Core.TimerSteps[i] > C4Core.TimerSteps[i - 1];
            Ok(rising, "timer steps strictly rise");
            Ok(C4Core.StepTimer(0, -1) == 0 && C4Core.StepTimer(C4Core.TimerSteps.Length - 1, 1) == C4Core.TimerSteps.Length - 1,
               "the wheel stops at both ends");
            Ok(C4Core.StepTimer(2, 1) == 3 && C4Core.StepTimer(2, -1) == 1, "the wheel steps one at a time");
            Ok(C4Core.ClampTimer(3f) == 10f && C4Core.ClampTimer(9999f) == 300f && C4Core.ClampTimer(45f) == 45f,
               "a packet timer is held inside 10 s .. 5 min");
            Ok(C4Core.ClampTimer(0f) == 0f && C4Core.ClampTimer(-5f) == 0f && C4Core.ClampTimer(float.NaN) == 0f,
               "zero, negative and NaN mean remote only");

            // Beep speeds up as the time runs out, within 0.12 .. 2 s.
            Ok(Near(C4Core.BeepInterval(300f), 2f) && Near(C4Core.BeepInterval(60f), 2f), "slow beep with a minute left");
            Ok(C4Core.BeepInterval(30f) < C4Core.BeepInterval(60f)
               && C4Core.BeepInterval(5f) < C4Core.BeepInterval(30f), "the beep speeds up");
            Ok(Near(C4Core.BeepInterval(1f), 0.12f) && Near(C4Core.BeepInterval(-1f), 0.12f), "fastest beep at the end");
            Ok(Near(C4Core.BeepInterval(float.NaN), 2f), "a NaN timer beeps slowly");

            // ---------------------------------------------- object damage
            Ok(C4Core.ChargeShare(0f, 1.5f * K, 4f * K) == 1f, "a charge on the object counts in full");
            Ok(C4Core.ChargeShare(5f * K, 1.5f * K, 4f * K) == 0f, "a charge 5 m off does nothing");
            float mid = C4Core.ChargeShare(2.75f * K, 1.5f * K, 4f * K);
            Ok(Near(mid, 0.5f), "half way between full and zero is half a charge");
            Ok(!C4Core.Destroyed(1f, 2f) && C4Core.Destroyed(2f, 2f), "a tank takes two charges");
            Ok(C4Core.Destroyed(1f, 1f) && C4Core.Destroyed(0.5f + 0.5f, 1f), "shares add up");
            Ok(!C4Core.Destroyed(float.NaN, 1f), "a NaN share never destroys");

            // ---------------------------------------------------- defuse
            Ok(C4Core.DefuseSeconds(6f, false, 0.5f) == 6f, "defuse takes its base time");
            Ok(C4Core.DefuseSeconds(6f, true, 0.5f) == 3f, "the Mechanic buff halves it");
            Ok(C4Core.DefuseSeconds(6f, true, 2f) == 6f, "the buff never slows the defuse");
            Ok(C4Core.DefuseSeconds(0.2f, true, 0.5f) == 1f, "never under a second");

            // --------------------------------------------------- radio range
            Ok(C4Core.InRange(100f, 0f, 0f, 100f) && !C4Core.InRange(100f, 1f, 0f, 100f),
               "radio range is a 3D sphere, inclusive");
            Ok(!C4Core.InRange(0f, 0f, 0f, 0f), "no range, no radio");

            Console.WriteLine(Pass + " PASS, " + Fail + " FAIL");
            return Fail == 0 ? 0 : 1;
        }
    }
}
