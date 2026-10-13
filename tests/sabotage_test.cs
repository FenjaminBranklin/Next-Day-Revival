// Power 5: headless test of the sabotage points (Revival.SabotageCore.cs,
// Revival.SabotageData.cs) and the C4 rules they feed (PowerNetCore
// InsideHouse/Demolition, FuelDepotCore.Demolish). Compiled with csc 3.5 by
// research/sabotage_check.py together with the production cores. Prints one
// "POINT ..." line per point for the script's geometry cross-checks, then
// "N PASS, M FAIL"; exit code 0 only without FAIL.
// C# 3.0, ASCII.
using System;
using System.Globalization;
using NextDayRevival;

static class SabotageTest
{
    static int _pass, _fail;
    const float K = 2.8f;

    static void Ok(bool c, string what)
    {
        if (c) _pass++;
        else { _fail++; Console.WriteLine("FAIL " + what); }
    }

    static bool Near(float a, float b, float tol) { return Math.Abs(a - b) <= tol; }

    static SabotagePoint Copy(SabotagePoint p)
    {
        SabotagePoint q = new SabotagePoint();
        q.Key = p.Key; q.Site = p.Site; q.Ref = p.Ref; q.Label = p.Label; q.Kind = p.Kind; q.Frame = p.Frame;
        q.Index = p.Index; q.Charges = p.Charges; q.CX = p.CX; q.CY = p.CY; q.CZ = p.CZ; q.HX = p.HX; q.HY = p.HY; q.HZ = p.HZ;
        q.AX = p.AX; q.AY = p.AY; q.AZ = p.AZ; q.NX = p.NX; q.NY = p.NY; q.NZ = p.NZ; q.TX = p.TX; q.TY = p.TY; q.TZ = p.TZ;
        q.FloorY = p.FloorY;
        return q;
    }

    static string F(float f) { return f.ToString("0.####", CultureInfo.InvariantCulture); }

    static int Main()
    {
        SabotagePoint[] all = SabotageData.Points;
        string why;

        // ---------------------------------------------------- every anchor
        Ok(all.Length > 0, "the table declares points");
        Ok(SabotageCore.ValidAll(all, out why), "every sabotage point has a valid anchor: " + why);
        for (int i = 0; i < all.Length; i++)
        {
            SabotagePoint p = all[i];
            Ok(SabotageCore.Valid(p, out why), p.Key + " valid: " + why);
            Ok(p.Key == p.Site + "/" + p.Key.Substring(p.Key.IndexOf('/') + 1) && p.Key.IndexOf('/') > 0, p.Key + " key is <site>/<thing>");
            Ok(SabotageCore.BoxDistance(p, p.AX, p.AY, p.AZ) < 0.03f, p.Key + " anchor touches its target");
            // A charge on the anchor is a whole charge on the target.
            Ok(C4Core.ChargeShare(SabotageCore.BoxDistance(p, p.AX, p.AY, p.AZ) * K, 1.5f * K, 4f * K) == 1f, p.Key + " charge on the anchor counts in full");
            // Outward: 0.3 m along the normal is off the target, 0.3 m against it is inside.
            Ok(SabotageCore.BoxDistance(p, p.AX + p.NX * 0.3f, p.AY + p.NY * 0.3f, p.AZ + p.NZ * 0.3f) > 0.25f, p.Key + " normal points out of the target");
            Ok(SabotageCore.BoxDistance(p, p.AX - p.NX * 0.05f, p.AY - p.NY * 0.05f, p.AZ - p.NZ * 0.05f) == 0f, p.Key + " anchor sits on the surface");
            // Orientation: a unit long axis along the face, horizontal on a wall.
            Ok(Near(p.TX * p.NX + p.TY * p.NY + p.TZ * p.NZ, 0f, 1e-4f), p.Key + " long axis lies in the face");
            Ok(p.NY != 0f || Near(p.TY, 0f, 1e-4f), p.Key + " long axis horizontal on a wall");
            // One charge per standing man's reach and far from the far-away world origin.
            Ok(p.Charges >= 1f && p.Charges <= 3f, p.Key + " 1..3 charges");
            Console.WriteLine("POINT " + p.Key + " " + p.Kind + " " + p.Frame + " " + p.Index + " " + p.Ref + " " + F(p.Charges)
                + " " + F(p.CX) + " " + F(p.CY) + " " + F(p.CZ) + " " + F(p.HX) + " " + F(p.HY) + " " + F(p.HZ)
                + " " + F(p.AX) + " " + F(p.AY) + " " + F(p.AZ) + " " + F(p.NX) + " " + F(p.NY) + " " + F(p.NZ)
                + " " + F(p.TX) + " " + F(p.TY) + " " + F(p.TZ) + " " + F(p.FloorY));
        }

        // ------------------------------------------------ per site coverage
        for (int s = 0; s < PowerSitesData.Sites.Length; s++)
        {
            PowerSite site = PowerSitesData.Sites[s];
            SabotagePoint[] own = SabotageData.For(site.Id);
            Ok(own.Length > 0, site.Id + " declares sabotage points");
            Ok(SabotageCore.Count(all, site.Id, SabotageCore.Generator) == 1, site.Id + ": one generator point");
            Ok(SabotageCore.Count(all, site.Id, SabotageCore.CommandPost) == (PowerNetCore.Feeds(site, PowerNetCore.Terminal) ? 1 : 0),
               site.Id + ": a command post where it feeds the terminal");
            Ok(SabotageCore.Count(all, site.Id, SabotageCore.RadarMast) == (PowerNetCore.Feeds(site, PowerNetCore.Radar) ? 1 : 0),
               site.Id + ": a radar mast where it feeds the radar");
            Ok(SabotageCore.Count(all, site.Id, SabotageCore.AaGun) == site.Guns.Length, site.Id + ": one point per AA gun it feeds");
            for (int g = 0; g < site.Guns.Length; g++)
            {
                int found = 0;
                for (int i = 0; i < own.Length; i++)
                {
                    if (own[i].Kind != SabotageCore.AaGun || own[i].Index != site.Guns[g]) continue;
                    found++;
                    int pit = FlakPositionsCore.Position(site.Guns[g]);
                    Ok(pit >= 0 && own[i].Ref == FlakPositionsCore.Name[pit], own[i].Key + " is the gun of its pit");
                    Ok(own[i].Frame == SabotageCore.Gun, own[i].Key + " in the gun frame");
                }
                Ok(found == 1, site.Id + ": gun " + site.Guns[g] + " has exactly one point");
            }
            for (int i = 0; i < own.Length; i++)
            {
                SabotagePoint p = own[i];
                if (p.Kind != SabotageCore.Generator) continue;
                Ok(p.Frame == SabotageCore.Bunker && p.Ref == site.Generator, p.Key + " is " + site.Generator + " in the bunker frame");
                Ok(p.Charges == PowerNetCore.GenC4Charges, p.Key + " needs GenC4Charges");
                // The whole generator and its anchor are inside the house, in
                // world coordinates through the site's yaw.
                for (int c = 0; c < 9; c++)
                {
                    float lx = c == 8 ? p.AX : p.CX + ((c & 1) != 0 ? p.HX : -p.HX);
                    float ly = c == 8 ? p.AY : p.CY + ((c & 2) != 0 ? p.HY : -p.HY);
                    float lz = c == 8 ? p.AZ : p.CZ + ((c & 4) != 0 ? p.HZ : -p.HZ);
                    float dx, dy, dz;
                    SabotageCore.ToWorld(site.Yaw, lx, ly, lz, out dx, out dy, out dz);
                    Ok(PowerNetCore.InsideHouse(site, site.X + dx, 100f + dy, site.Z + dz, 100f),
                       p.Key + (c == 8 ? " anchor" : " box corner " + c) + " inside the house");
                }
                // Room to stand at it: 0.6 m in front of the anchor is still inside.
                float fx, fy, fz;
                SabotageCore.ToWorld(site.Yaw, p.AX + p.NX * 0.6f, p.AY, p.AZ + p.NZ * 0.6f, out fx, out fy, out fz);
                Ok(PowerNetCore.InsideHouse(site, site.X + fx, 100f + fy, site.Z + fz, 100f), p.Key + " reachable from inside");
            }
        }
        // Main airfield: the five depot tanks in FuelDepot's order.
        string[] tanks = { "V1", "V2", "H1", "H2", "H3" };
        Ok(SabotageCore.Count(all, "main_airfield", SabotageCore.FuelTank) == tanks.Length, "main airfield: five fuel tank points");
        for (int t = 0; t < tanks.Length; t++)
        {
            int found = 0;
            for (int i = 0; i < all.Length; i++)
                if (all[i].Kind == SabotageCore.FuelTank && all[i].Ref == tanks[t])
                { found++; Ok(all[i].Index == t && all[i].Frame == SabotageCore.Tank && all[i].Charges == 1f, all[i].Key + " index/frame/one charge"); }
            Ok(found == 1, "tank " + tanks[t] + " has exactly one point");
        }

        // ------------------------------------------------ validation itself
        SabotagePoint gen = null;
        for (int i = 0; i < all.Length; i++) if (all[i].Kind == SabotageCore.Generator) { gen = all[i]; break; }
        SabotagePoint b = Copy(gen); b.AZ -= 0.1f;
        Ok(!SabotageCore.Valid(b, out why), "anchor 0.1 m off its face is refused");
        b = Copy(gen); b.AX = gen.CX + gen.HX + 0.5f;
        Ok(!SabotageCore.Valid(b, out why), "anchor beside the face is refused");
        b = Copy(gen); b.NX = 0.6f; b.NZ = -0.8f;
        Ok(!SabotageCore.Valid(b, out why), "normal off the box axes is refused");
        b = Copy(gen); b.NZ = 1f;
        Ok(!SabotageCore.Valid(b, out why), "normal into the target is refused");
        b = Copy(gen); b.TX = -b.TX;
        Ok(!SabotageCore.Valid(b, out why), "a long axis C4.Orient would not give is refused");
        b = Copy(gen); b.AY = 2.5f; b.CY = 1.5f; b.HY = 1.2f;
        Ok(!SabotageCore.Valid(b, out why), "an anchor out of reach is refused");
        b = Copy(gen); b.Charges = 0f;
        Ok(!SabotageCore.Valid(b, out why), "zero charges is refused");
        b = Copy(gen); b.HX = 0f;
        Ok(!SabotageCore.Valid(b, out why), "an empty box is refused");
        b = Copy(gen); b.AX = float.NaN;
        Ok(!SabotageCore.Valid(b, out why), "NaN is refused");
        b = Copy(gen); b.Kind = 9;
        Ok(!SabotageCore.Valid(b, out why), "unknown kind is refused");
        Ok(!SabotageCore.ValidAll(new SabotagePoint[] { gen, Copy(gen) }, out why), "duplicate keys are refused");

        // ------------------------------------------------------- frame math
        float ox, oy, oz;
        SabotageCore.ToWorld(90f, 1f, 0f, 0f, out ox, out oy, out oz);
        Ok(Near(ox, 0f, 1e-4f) && Near(oz, -K, 1e-4f), "yaw 90: local +x is world -z (Unity yaw)");
        SabotageCore.ToWorld(90f, 0f, 0f, 1f, out ox, out oy, out oz);
        Ok(Near(ox, K, 1e-4f) && Near(oz, 0f, 1e-4f), "yaw 90: local +z is world +x");
        for (int y = -180; y <= 180; y += 15)
        {
            float lx, ly, lz;
            SabotageCore.ToWorld(y + 0.37f, 1.3f, 0.7f, -2.1f, out ox, out oy, out oz);
            SabotageCore.ToLocal(y + 0.37f, ox, oy, oz, out lx, out ly, out lz);
            Ok(Near(lx, 1.3f, 1e-3f) && Near(ly, 0.7f, 1e-3f) && Near(lz, -2.1f, 1e-3f), "frame round trip at yaw " + y);
        }
        float tx, ty, tz;
        SabotageCore.LongAxis(0f, 0f, -1f, out tx, out ty, out tz);
        Ok(tx == 1f && ty == 0f && tz == 0f, "long axis of a south face is east (normal x up)");
        SabotageCore.LongAxis(0f, 1f, 0f, out tx, out ty, out tz);
        Ok(tx == 1f && ty == 0f && tz == 0f, "long axis on a floor is normal x forward");

        // ------------------------------------------- house and the armour
        PowerSite main = PowerSitesData.Sites[0];
        PowerSite turned = new PowerSite();
        turned.Id = "t"; turned.X = 100f; turned.Z = 200f; turned.Yaw = 30f; turned.HalfX = main.HalfX; turned.HalfZ = main.HalfZ; turned.Height = main.Height;
        float wx, wy, wz;
        SabotageCore.ToWorld(turned.Yaw, turned.HalfX / K - 0.6f, 1f, 0f, out wx, out wy, out wz);
        Ok(PowerNetCore.InsideHouse(turned, turned.X + wx, 10f + wy, turned.Z + wz, 10f), "turned bunker: inside by the wall");
        Ok(PowerNetCore.OnBuilding(turned, PowerNetCore.HitHeavy, turned.X + wx, 10f + wy, turned.Z + wz, 10f), "turned bunker: same frame as the armour test");
        SabotageCore.ToWorld(turned.Yaw, turned.HalfX / K - 0.2f, 1f, 0f, out wx, out wy, out wz);
        Ok(!PowerNetCore.InsideHouse(turned, turned.X + wx, 10f + wy, turned.Z + wz, 10f), "a charge in the wall is not inside");
        SabotageCore.ToWorld(turned.Yaw, turned.HalfX / K + 0.3f, 1f, 0f, out wx, out wy, out wz);
        Ok(!PowerNetCore.InsideHouse(turned, turned.X + wx, 10f + wy, turned.Z + wz, 10f), "a charge on the outside wall is not inside");
        Ok(!PowerNetCore.InsideHouse(turned, turned.X, 10f + turned.Height + 0.1f, turned.Z, 10f), "a charge on the roof is not inside");
        Ok(!PowerNetCore.InsideHouse(turned, float.NaN, 0f, 0f, 0f), "NaN is not inside");

        // C4 on the generator: armour untouched, two charges destroy it.
        PowerState st = PowerNetCore.Fresh(main);
        Ok(PowerNetCore.Demolition(ref st, 1f, PowerNetCore.GenC4Charges, 10f), "one charge changes the state");
        Ok(st.Armour == main.Armour && Near(st.GenHp, 0.5f, 1e-4f) && PowerNetCore.Powered(st), "one charge: half the generator, armour intact, still on");
        Ok(st.Revision == 1 && st.Hits == 1, "revision and hit count bumped (sync)");
        Ok(PowerNetCore.Demolition(ref st, 1f, PowerNetCore.GenC4Charges, 12f), "second charge");
        Ok(st.GenHp == 0f && st.DownSince == 12f && PowerNetCore.Generator(st) == PowerNetCore.Destroyed && st.Armour == main.Armour,
           "two charges: generator destroyed, armour untouched");
        Ok(!PowerNetCore.Demolition(ref st, 1f, PowerNetCore.GenC4Charges, 13f), "a wreck takes no more");
        st = PowerNetCore.Fresh(main);
        Ok(PowerNetCore.Demolition(ref st, 2f, PowerNetCore.GenC4Charges, 5f) && PowerNetCore.Generator(st) == PowerNetCore.Destroyed,
           "a two-charge chain destroys it at once");
        st = PowerNetCore.Fresh(main);
        Ok(!PowerNetCore.Demolition(ref st, 0f, 2f, 5f) && !PowerNetCore.Demolition(ref st, float.NaN, 2f, 5f) && st.Revision == 0, "no share, no change");
        // The same charge outside, through the armour: the old rule holds.
        st = PowerNetCore.Fresh(main);
        bool genHit;
        PowerNetCore.Strike(ref st, main, PowerNetCore.HitHeavy, 1f, out genHit);
        Ok(st.Armour == 0 && st.GenHp == 1f && !genHit, "a charge outside spends armour first");

        // Demolished: the chain's shares against what the point needs.
        Ok(SabotageCore.Demolished(1f, 1f) && !SabotageCore.Demolished(0.99f, 1f) && SabotageCore.Demolished(2f, 2f)
           && !SabotageCore.Demolished(1f, 2f) && !SabotageCore.Demolished(float.NaN, 1f), "demolished at the needed charges");
        // Fuel tank: one charge on it destroys it, whatever hit points are left.
        float pts = 4f;
        Ok(FuelDepotCore.Demolish(ref pts) && pts == 0f, "C4 on a full tank destroys it");
        Ok(!FuelDepotCore.Demolish(ref pts), "a wreck is not destroyed twice");
        Ok(FuelDepotCore.DemolishCost > 2f, "the demolish report is above every hit class");

        // ------------------------------------------------------ allocation
        long before = GC.GetTotalMemory(true);
        int gc0 = GC.CollectionCount(0);
        float sink = 0f;
        for (int n = 0; n < 200000; n++)
        {
            SabotagePoint p = all[n % all.Length];
            float lx, ly, lz;
            SabotageCore.ToLocal(n * 0.01f, n * 0.1f, 1f, -n * 0.2f, out lx, out ly, out lz);
            sink += SabotageCore.BoxDistance(p, lx, ly, lz);
            SabotageCore.LongAxis(p.NX, p.NY, p.NZ, out tx, out ty, out tz);
            sink += PowerNetCore.InsideHouse(main, main.X + lx, ly, main.Z + lz, float.NaN) ? 1f : 0f;
            PowerState z = PowerNetCore.Fresh(main);
            PowerNetCore.Demolition(ref z, 0.5f, 2f, 1f);
            sink += SabotageCore.Demolished(z.GenHp, 1f) ? 1f : 0f;
        }
        long after = GC.GetTotalMemory(false);
        Ok(GC.CollectionCount(0) == gc0 && after - before < 4096, "C4 chain maths allocate nothing (" + (after - before) + " bytes)");
        Ok(!float.IsNaN(sink), "loop ran");

        Console.WriteLine(_pass + " PASS, " + _fail + " FAIL");
        return _fail == 0 ? 0 : 1;
    }
}
