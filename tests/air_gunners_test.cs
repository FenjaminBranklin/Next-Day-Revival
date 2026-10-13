// Offline test of Revival.AirGunnersCore.cs (research/air_gunners_check.py
// compiles both with csc 3.5 and runs this). The weapon rows are the live
// weapons_db.xml values (Stage64 staticdata, read 2026-10-07) - the fixture,
// not a rating table: the rating itself is computed from the numbers.
using System;
using System.Collections.Generic;
using NextDayRevival;

static class AirGunnersTest
{
    static int _fail;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _fail++;
    }

    static AirWeaponStats W(int id, string cal, int mag, float dmg, float spread, float rof,
                            float eff, float reload, int modes, int type, bool scope)
    {
        AirWeaponStats w = new AirWeaponStats();
        w.ItemId = id; w.Cal = cal; w.Magazine = mag; w.Damage = dmg; w.Spread = spread;
        w.Interval = rof; w.EffectiveRange = eff; w.Reload = reload; w.Modes = modes;
        w.Shotgun = type == 1; w.Scoped = scope;
        return w;
    }

    static readonly Dictionary<int, AirWeaponStats> Db = new Dictionary<int, AirWeaponStats>();
    static readonly Dictionary<int, string> Names = new Dictionary<int, string>();

    static void Add(string name, AirWeaponStats w) { Db[w.ItemId] = w; Names[w.ItemId] = name; }

    static void Fixture()
    {
        Add("AKMS", W(1001, "7,62", 30, 35, 0.022f, 0.08f, 80, 3.4f, 1, 0, false));
        Add("AKM", W(1002, "7,62", 30, 35, 0.02f, 0.08f, 80, 3.4f, 1, 0, false));
        Add("PP1901", W(1003, "9,18", 30, 30, 0.03f, 0.05f, 60, 3.4f, 1, 0, false));
        Add("AKS74U", W(1006, "7,62", 30, 30, 0.022f, 0.05f, 70, 3.4f, 1, 0, false));
        Add("AK12", W(1007, "5,45", 30, 40, 0.016f, 0.08f, 90, 3.4f, 1, 0, false));
        Add("VPO Vepr", W(1008, "12", 10, 15, 0.03f, 0.2f, 35, 3.4f, 0, 1, false));
        Add("VSS", W(1009, "9,39", 20, 55, 0.01f, 0.06f, 600, 3f, 1, 0, true));
        Add("SVD", W(1010, "7,62", 10, 100, 0.0015f, 0.5f, 1800, 3.4f, 0, 0, true));
        Add("VAL", W(1011, "9,39", 20, 32, 0.025f, 0.06f, 70, 3.4f, 1, 0, false));
        Add("M4A1", W(1012, "5,56", 30, 35, 0.03f, 0.06f, 70, 3.8f, 1, 0, false));
        Add("Saiga", W(1013, "12", 10, 15, 0.025f, 0.35f, 45, 3.4f, 1, 1, false));
        Add("Gauss", W(1014, "5,45", 30, 60, 0.02f, 0.5f, 150, 3.4f, 1, 0, false));
        Add("PSG-1", W(1015, "7,62", 5, 75, 0.0015f, 0.5f, 1800, 3.4f, 0, 0, true));
        Add("RPK", W(1016, "7,62", 75, 45, 0.025f, 0.08f, 55, 3.4f, 2, 0, false));
        Add("AKN", W(1017, "7,62", 30, 40, 0.015f, 0.08f, 90, 3.4f, 1, 0, false));
        Add("XM8 LMG", W(1018, "5,56", 100, 45, 0.02f, 0.08f, 60, 3.4f, 1, 0, false));
        Add("G36", W(1019, "5,56", 30, 40, 0.025f, 0.06f, 75, 3.4f, 1, 0, false));
        Add("G36 camo", W(1020, "5,56", 30, 45, 0.02f, 0.06f, 80, 3.4f, 1, 0, false));
        Add("USAS-12", W(1021, "12", 20, 20, 0.03f, 0.35f, 40, 3.4f, 1, 1, false));
        Add("Galil", W(1022, "5,56", 30, 30, 0.02f, 0.06f, 80, 3.4f, 1, 0, false));
        Add("RPD", W(1023, "7,62", 100, 50, 0.025f, 0.08f, 150, 3.4f, 2, 0, false));
        Add("Toxic AK", W(1024, "7,62", 26, 45, 0.01f, 0.08f, 180, 3.4f, 1, 0, false));
        Add("Gauss sniper", W(1025, "7,62", 8, 120, 0.0015f, 3.25f, 2000, 3.4f, 0, 0, true));
        Add("TKB-0111", W(1026, "5,45", 30, 47, 0.012f, 0.08f, 90, 3.4f, 1, 0, false));
        Add("VEPR 308", W(1027, "7,62", 10, 85, 0.01f, 0.5f, 600, 3.4f, 0, 0, true));
        Add("Samopal", W(1151, "9,18", 1, 45, 0.005f, 0.2f, 70, 3.35f, 0, 0, false));
        Add("Mosin", W(1152, "7,62", 5, 40, 0.0075f, 1.8f, 300, 3.4f, 0, 0, true));
        Add("TOZ-34", W(1153, "12", 2, 10, 0.03f, 0.2f, 80, 4.4f, 0, 1, false));
        Add("MP-133", W(1154, "12", 4, 15, 0.03f, 1.15f, 40, 5.5f, 0, 1, false));
        Add("MG42", W(1160, "7,62", 200, 45, 0.028f, 0.06f, 140, 5f, 2, 0, false));
        Add("TAC-50", W(1161, "12,7", 5, 2800, 0.0008f, 2.2f, 1800, 9f, 0, 0, true));
        Add("M72 LAW", W(1162, "66", 1, 25, 0.004f, 1.6f, 200, 99f, 0, 0, false));
        Add("M7", W(1164, "6,8", 100, 90, 0.0006f, 0.11f, 900, 3.5f, 2, 0, false));
        Add("Stinger", W(1165, "70", 1, 0, 0f, 1.6f, 1200, 8f, 0, 0, true));
        Add("PM", W(1201, "9,18", 8, 30, 0.022f, 0.2f, 40, 2f, 0, 0, false));
        Add("APS", W(1202, "9,18", 20, 30, 0.025f, 0.1f, 45, 2f, 1, 0, false));
        Add("TT", W(1203, "9,18", 7, 35, 0.017f, 0.2f, 50, 2f, 0, 0, false));
        Add("M1911", W(1204, ".45 ACP", 7, 27, 0.02f, 0.2f, 60, 2f, 0, 0, false));
        Add("Grach", W(1205, "9,18", 17, 40, 0.015f, 0.2f, 55, 2f, 0, 0, false));
    }

    static float R(int id) { return AirWeaponRating.Rate(Db[id]); }

    static float Min(int[] ids) { float m = 2f; foreach (int i in ids) m = Math.Min(m, R(i)); return m; }
    static float Max(int[] ids) { float m = -2f; foreach (int i in ids) m = Math.Max(m, R(i)); return m; }

    static void Ratings()
    {
        Fixture();
        int[] mgs = { 1160, 1016, 1023 };
        int[] rifles = { 1001, 1002, 1007, 1012, 1017, 1019, 1020, 1022, 1024, 1026 };
        int[] smgs = { 1003 };
        int[] dmrs = { 1009, 1014 };
        int[] useless = { 1010, 1015, 1025, 1027, 1152, 1161, 1008, 1013, 1021, 1153, 1154,
                          1201, 1202, 1203, 1204, 1205, 1151 };
        List<KeyValuePair<float, int>> all = new List<KeyValuePair<float, int>>();
        foreach (KeyValuePair<int, AirWeaponStats> p in Db) all.Add(new KeyValuePair<float, int>(R(p.Key), p.Key));
        all.Sort(delegate(KeyValuePair<float, int> p1, KeyValuePair<float, int> p2) { return p2.Key.CompareTo(p1.Key); });
        foreach (KeyValuePair<float, int> p in all)
            Console.WriteLine("  " + Names[p.Value].PadRight(14) + p.Key.ToString("0.000")
                + "  tier " + AirWeaponRating.Tier(p.Key));

        Check(R(1160) == Max(new int[] { 1160, 1016, 1023, 1018, 1001, 1012, 1003 }), "MG42 rates highest");
        Check(Min(mgs) > Max(rifles), "MG42/RPK/RPD above every assault rifle ("
            + Min(mgs).ToString("0.000") + " > " + Max(rifles).ToString("0.000") + ")");
        Check(R(1018) > Max(smgs), "XM8 above the SMG");
        Check(Min(rifles) > Max(smgs), "assault rifles above the SMG ("
            + Min(rifles).ToString("0.000") + " > " + Max(smgs).ToString("0.000") + ")");
        Check(Min(smgs) > Max(dmrs), "SMG above the DMRs");
        Check(Min(dmrs) > Max(useless), "DMRs above sniper/shotgun/pistol");
        Check(Max(useless) < 0.15f, "sniper rifles, shotguns, pistols and single shots are near useless ("
            + Max(useless).ToString("0.000") + ")");
        Check(Min(mgs) >= 0.73f && AirWeaponRating.Tier(R(1160)) == AirWeaponRating.Excellent, "MGs are excellent");
        Check(AirWeaponRating.Tier(R(1002)) == AirWeaponRating.Good && AirWeaponRating.Tier(R(1012)) == AirWeaponRating.Good,
              "AK and M4 are good");
        Check(AirWeaponRating.Tier(R(1003)) == AirWeaponRating.Medium, "the SMG is medium");
        Check(AirWeaponRating.Tier(R(1009)) == AirWeaponRating.Weak && AirWeaponRating.Tier(R(1014)) == AirWeaponRating.Weak,
              "the DMRs are weak");
        Check(AirWeaponRating.Tier(R(1010)) == AirWeaponRating.Useless
              && AirWeaponRating.Tier(R(1154)) == AirWeaponRating.Useless
              && AirWeaponRating.Tier(R(1201)) == AirWeaponRating.Useless, "SVD, pump gun and pistol are useless");
        Check(R(1162) == AirWeaponRating.Forbidden && R(1165) == AirWeaponRating.Forbidden,
              "rocket launchers are forbidden (backblast)");
        AirWeaponStats tagged = Db[1012]; tagged.Launcher = true;
        Check(AirWeaponRating.Rate(tagged) == AirWeaponRating.Forbidden, "a known rocket item is forbidden by id");
        Check(R(1161) >= 0f, "the 12,7 TAC-50 is a rifle, not a rocket");

        // Each factor moves the rating the asked-for way.
        AirWeaponStats a = Db[1002], b = a;
        b.Interval = 0.04f;
        Check(AirWeaponRating.Rate(b) > AirWeaponRating.Rate(a), "faster fire rates higher");
        b = a; b.Magazine = 100;
        Check(AirWeaponRating.Rate(b) > AirWeaponRating.Rate(a), "a bigger magazine helps");
        AirWeaponStats tight = a, wide = a, mid = a;
        tight.Spread = 0.002f; wide.Spread = 0.2f; mid.Spread = AirWeaponRating.IdealSpread;
        Check(AirWeaponRating.Rate(mid) > AirWeaponRating.Rate(tight) && AirWeaponRating.Rate(mid) > AirWeaponRating.Rate(wide),
              "medium spread beats pinpoint and scatter");
        b = a; b.Modes = 0;
        Check(AirWeaponRating.Rate(b) < 0.2f * AirWeaponRating.Rate(a), "single shot is a heavy penalty");
        // Weights: fire rate moves the rating more than the magazine does.
        AirWeaponStats slow = a, small = a;
        slow.Interval = a.Interval * 2f; small.Magazine = a.Magazine / 2;
        Check(AirWeaponRating.Rate(slow) < AirWeaponRating.Rate(small), "fire rate weighs more than magazine size");

        Check(Math.Abs(AirWeaponRating.Calibre("7,62") - 7.62f) < 0.001f
              && Math.Abs(AirWeaponRating.Calibre(".45 ACP") - 0.45f) < 0.001f
              && AirWeaponRating.Calibre("66") == 66f && AirWeaponRating.Calibre("") == 0f
              && Math.Abs(AirWeaponRating.Calibre("12,7") - 12.7f) < 0.001f, "calibre strings parse");
        float coneMg = AirWeaponRating.ShotCone(0.028f, R(1160), 0.06f);
        float coneSvd = AirWeaponRating.ShotCone(0.0015f, R(1010), 0.06f);
        Check(coneSvd > 2.5f * coneMg, "the SVD scatters far wider from a door than the MG42");
        Check(AirWeaponRating.Cadence(Db[1160], 0.07f) == 0.07f && AirWeaponRating.Cadence(Db[1010], 0.07f) == 0.5f,
              "cadence: the weapon's own, floored for the network");
    }

    static void Stations()
    {
        AirStation tail = AirStations.Get(AirStations.An2Tail);
        Check(tail.Heavy && !AirStations.Get(AirStations.An2Door).Heavy, "only the tail gun is the 12.7 mm");
        Check(AirStations.Clear(tail, 150f, 20f) && AirStations.Clear(tail, -150f, 45f)
              && AirStations.Clear(tail, 180f, 50f), "tail arc: rear quarters and high astern");
        Check(!AirStations.Clear(tail, 0f, 20f) && !AirStations.Clear(tail, 90f, 10f), "tail gun cannot fire forward or abeam");
        Check(!AirStations.Clear(tail, 180f, 10f) && !AirStations.Clear(tail, 170f, -2f), "the fin and tailplane are masked");
        Check(!AirStations.Clear(tail, 150f, -30f), "tail gun has an upper arc only");
        AirStation door = AirStations.Get(AirStations.An2Door);
        Check(AirStations.Clear(door, -90f, -40f) && !AirStations.Clear(door, 90f, -40f), "the An-2 door is on the port side");
        AirStation l = AirStations.Get(AirStations.Mi8Left), r = AirStations.Get(AirStations.Mi8Right);
        Check(AirStations.Clear(l, -80f, -30f) && AirStations.Clear(r, 80f, -30f)
              && !AirStations.Clear(l, 80f, -30f) && !AirStations.Clear(r, -80f, -30f), "Mi-8 left and right doors");
        float yaw = 10f, pitch = -89f;
        AirStations.Clamp(door, ref yaw, ref pitch);
        Check(AirStations.InArc(door, yaw, pitch) && Math.Abs(yaw - (-20f)) < 0.01f && pitch == door.PitchMin,
              "clamp keeps the look in the arc");
        yaw = -170f; pitch = 0f;
        AirStations.Clamp(tail, ref yaw, ref pitch);
        Check(Math.Abs(yaw + 170f) < 0.01f, "clamp across the 180 seam leaves an inside look alone");
        Check(AirStations.Next(AirStations.None, true) == AirStations.An2Tail
              && AirStations.Next(AirStations.An2Tail, true) == AirStations.An2Door
              && AirStations.Next(AirStations.An2Door, true) == AirStations.None
              && AirStations.Next(AirStations.None, false) == AirStations.Mi8Left
              && AirStations.Next(AirStations.Mi8Left, false) == AirStations.Mi8Right
              && AirStations.Next(AirStations.Mi8Right, false) == AirStations.None, "station cycle");
    }

    // A point-mass aeroplane: the bank follows the command with a lag, the
    // heading turns at g tan(bank) / v, constant speed and height.
    static void Orbit(float startX, float startZ, float heading, string label)
    {
        float v = 110f / 3.6f, R = 150f, maxBank = 55f, height = 120f;
        float x = startX, z = startZ, bank = 0f, dt = 0.05f;
        float worstR = 0f, worstArc = 0f;
        bool inArc = true, captured = false;
        AirStation door = AirStations.Get(AirStations.An2Door);
        for (int i = 0; i < (int)(240f / dt); i++)
        {
            float want;
            float cmd = OrbitCore.Guide(x, z, heading, v, 0f, 0f, R, OrbitCore.Left, maxBank, out want);
            bank += (cmd - bank) * Math.Min(1f, dt / 0.8f);
            heading = AirStations.Wrap(heading + (float)(OrbitCore.G * Math.Tan(bank * Math.PI / 180.0) / v * 180.0 / Math.PI) * dt);
            double h = heading * Math.PI / 180.0;
            x += (float)(Math.Sin(h) * v * dt);
            z += (float)(Math.Cos(h) * v * dt);
            if (i * dt < 120f) continue;     // capture time
            captured = true;
            float d = (float)Math.Sqrt(x * x + z * z);
            worstR = Math.Max(worstR, Math.Abs(d - R) / R);
            // The mark in the banked body frame: yaw from the nose, pitch
            // from the wings' plane (roll about the nose).
            float yawFlat = OrbitCore.CentreYaw(x, z, heading, 0f, 0f) * (float)Math.PI / 180f;
            double fx = Math.Sin(yawFlat) * d, fz = Math.Cos(yawFlat) * d, fy = -height;
            double phi = bank * Math.PI / 180.0;
            double bx = fx * Math.Cos(phi) + fy * Math.Sin(phi);
            double by = -fx * Math.Sin(phi) + fy * Math.Cos(phi);
            float yawB = (float)(Math.Atan2(bx, fz) * 180.0 / Math.PI);
            float pitchB = (float)(Math.Atan2(by, Math.Sqrt(bx * bx + fz * fz)) * 180.0 / Math.PI);
            if (!AirStations.InArc(door, yawB, pitchB)) inArc = false;
            worstArc = Math.Max(worstArc, Math.Abs(AirStations.Wrap(yawB - door.CentreYaw)));
        }
        Check(captured && worstR < 0.12f, label + ": holds the circle within 12 % (worst " + (worstR * 100f).ToString("0.0") + " %)");
        Check(inArc, label + ": the mark stays in the port door's arc (worst " + worstArc.ToString("0") + " deg off abeam)");
    }

    static void OrbitGeometry()
    {
        float bank = OrbitCore.BankFor(110f / 3.6f, 150f);
        Check(bank > 25f && bank < 40f, "a 150 m circle at 110 km/h is a " + bank.ToString("0") + " deg bank");
        Check(Math.Abs(OrbitCore.RadiusFor(110f / 3.6f, bank) - 150f) < 1f, "radius and bank invert");
        float want;
        float onCircle = OrbitCore.Guide(0f, 150f, -90f, 30f, 0f, 0f, 150f, OrbitCore.Left, 55f, out want);
        Check(onCircle < -20f && Math.Abs(AirStations.Wrap(want + 90f)) < 1f, "on the circle, tangent: a steady left bank");
        float far = OrbitCore.Guide(0f, 2000f, 180f, 30f, 0f, 0f, 150f, OrbitCore.Left, 55f, out want);
        Check(Math.Abs(far) < 15f && Math.Abs(AirStations.Wrap(want - 180f)) < 20f, "far away and pointing at it: nearly wings level, inbound to pass it on the left");
        float rightOrbit = OrbitCore.Guide(0f, 150f, 90f, 30f, 0f, 0f, 150f, OrbitCore.Right, 55f, out want);
        Check(rightOrbit > 20f, "a right-hand orbit banks right");
        Check(Math.Abs(OrbitCore.Guide(0f, 2000f, 0f, 30f, 0f, 0f, 150f, OrbitCore.Left, 40f, out want)) <= 40f, "bank is capped");
        Orbit(800f, -600f, 0f, "orbit from outside");
        Orbit(20f, 10f, 45f, "orbit from overhead");
        Orbit(-1500f, 1500f, 135f, "orbit from far away, inbound");
        Check(OrbitCore.CentreYaw(0f, 150f, -90f, 0f, 0f) < -80f, "on a left orbit the mark is off the port wing");
        float lever = 0.9f;
        for (int i = 0; i < 100; i++) lever = OrbitCore.Throttle(lever, 45f, 30f, 0.05f);
        Check(lever < 0.5f && lever >= 0.15f, "too fast: the orbit eases the throttle");
        lever = 0.2f;
        for (int i = 0; i < 100; i++) lever = OrbitCore.Throttle(lever, 22f, 30f, 0.05f);
        Check(lever > 0.6f, "too slow: the orbit opens the throttle");
        Check(OrbitCore.NearMark(30f, 0f, 0f, 0f, 35f) && !OrbitCore.NearMark(30f, 30f, 0f, 0f, 35f), "near-mark radius");
    }

    static int Main()
    {
        Ratings();
        Stations();
        OrbitGeometry();
        Console.WriteLine(_fail == 0 ? "ALL PASS" : _fail + " FAILED");
        return _fail == 0 ? 0 : 1;
    }
}
