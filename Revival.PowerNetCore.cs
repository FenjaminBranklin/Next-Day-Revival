// Power 2: the power network's rules, independent of Unity. A site declares
// one generator in its own bunker and the consumers it feeds
// (Revival.PowerSitesData.cs); the master owns one PowerState per site.
// research/power_network_check.py compiles this file and the site data
// unchanged with the .NET 3.5 compiler. docs/ai/POWER.md is the design.
using System;

namespace NextDayRevival
{
    /// <summary>One site's authoritative state (master truth, mirrored on clients).</summary>
    internal struct PowerState
    {
        internal int Armour;      // bunker armour points left (site.Armour = intact)
        internal float GenHp;     // generator 0..1; 0 = destroyed
        internal float Tank;      // day tank 0..1
        internal bool Breaker;    // true = closed (generator feeds the network)
        internal int Hits;        // counted hits on the building (scratches excluded)
        internal int Revision;    // bumped on every change the clients must see
        internal float DownSince; // master clock when destroyed, -1 intact
    }

    /// <summary>A site's declaration: where its bunker stands, what it feeds,
    /// how much it takes. World units (2.8 u = 1 m), yaw in Unity degrees.</summary>
    internal sealed class PowerSite
    {
        internal string Id, Generator;
        internal float X, Z, Yaw;
        internal float HalfX, HalfZ, Height;          // bunker shell, local axes; Height floor to roof top
        internal float WallM = PowerNetCore.WallM;    // wall (and roof slab) thickness, metres
        internal float BreakerX, BreakerY, BreakerZ;  // sockets, bunker-local
        internal float TankX, TankY, TankZ;
        internal float SignX, SignY, SignZ;
        internal float SignYaw;                       // plate facing: 0 = bunker +x, 90 = bunker -z
        internal int Armour = PowerNetCore.ArmourPoints;
        internal float TankMinutes = 45f;
        internal int[] Consumers;                     // PowerNetCore.Radar ...
        internal int[] Guns;                          // Flak gun indices fed (radar-guided AA)
    }

    internal static class PowerNetCore
    {
        internal const float K = 2.8f;

        // Consumers.
        internal const int Radar = 0, AirDefence = 1, Lights = 2, Terminal = 3, Screens = 4, ConsumerCount = 5;
        internal static readonly string[] ConsumerName = { "tower_radar", "radar_aa", "lights", "supply_order_terminal", "command_room_screens" };

        // Weapon classes of a hit on a bunker.
        // Light: small arms, grenades, mines, LAW - scratches only, never counted.
        // Medium: tank shells, FPV drones. Heavy: bombs, artillery, Katyusha.
        internal const int HitLight = 0, HitMedium = 1, HitHeavy = 2;

        // Bunker armour in points: a heavy hit takes two, a medium one. The
        // generator takes damage only from a hit that lands on spent armour, so
        // the 2nd heavy and the 3rd medium hit are the first to reach it.
        internal const int ArmourPoints = 2, HeavyCost = 2, MediumCost = 1;
        internal const float GenHeavy = 0.6f, GenMedium = 0.35f;
        // A hit counts on the building within this margin of its shell, metres.
        internal const float HeavyMarginM = 4f, MediumMarginM = 1.5f;
        // Power 5: a C4 charge inside the house (this far inside the shell's
        // outer faces: the wall) bypasses the armour; GenC4Charges on the
        // generator destroy it.
        internal const float WallM = 0.4f, GenC4Charges = 2f;

        // Generator states.
        internal const int Off = 0, On = 1, Destroyed = 2;
        // Damage stages = the asset's sibling shells.
        internal const int Intact = 0, Cracked = 1, Breached = 2;
        internal static readonly string[] StageName = { "Intact", "Cracked", "Breached" };

        // Breaker: pulling it is quick (sabotage), closing it takes a little longer.
        internal const float BreakerOffSeconds = 3f, BreakerOnSeconds = 5f;
        // Latency the master tolerates on a finished breaker action, seconds.
        internal const float BreakerSlack = 0.75f;
        internal const float BreakerReachM = 2.2f;

        // Day tank: refilled from the depot while it has fuel, or by hand from
        // a canister (100 units, one canister per RefuelSeconds at the filler).
        internal const float RefillPerMinute = 0.2f, TankUnits = 100f;
        internal const float RefuelSeconds = 4f, CanUnits = 100f;
        // A destroyed generator and its bunker are rebuilt after this long.
        internal const float RestoreMinutes = 30f;

        // Without power every fed AA gun lays by eye, close in, badly.
        internal const float DarkRangeM = 1000f, DarkError = 2.5f, DarkWalk = 0.5f,
            DarkFuze = 2f, DarkReaction = 1.5f;

        internal static PowerState Fresh(PowerSite site)
        {
            PowerState s = new PowerState();
            s.Armour = Math.Max(1, site.Armour);
            s.GenHp = 1f; s.Tank = 1f; s.Breaker = true;
            s.DownSince = -1f;
            return s;
        }

        internal static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }

        internal static int Generator(PowerState s)
        {
            if (s.GenHp <= 0f) return Destroyed;
            return s.Breaker && s.Tank > 0f ? On : Off;
        }

        internal static bool Powered(PowerState s) { return Generator(s) == On; }

        internal static int Stage(PowerState s, PowerSite site)
        {
            int full = Math.Max(1, site.Armour);
            if (s.Armour >= full) return Intact;
            return s.Armour > 0 ? Cracked : Breached;
        }

        /// <summary>Building condition in percent (armour left).</summary>
        internal static int BuildingPercent(PowerState s, PowerSite site)
        {
            return Percent((float)Math.Max(0, s.Armour) / Math.Max(1, site.Armour));
        }

        internal static int Percent(float f)
        {
            if (!Finite(f)) return 0;
            return (int)Math.Round(Math.Max(0f, Math.Min(1f, f)) * 100f);
        }

        internal static bool Feeds(PowerSite site, int consumer)
        {
            if (site == null || site.Consumers == null) return false;
            for (int i = 0; i < site.Consumers.Length; i++) if (site.Consumers[i] == consumer) return true;
            return false;
        }

        internal static bool FeedsGun(PowerSite site, int gun)
        {
            if (site == null || site.Guns == null || !Feeds(site, AirDefence)) return false;
            for (int i = 0; i < site.Guns.Length; i++) if (site.Guns[i] == gun) return true;
            return false;
        }

        /// <summary>Margin within which a hit of this class counts on the
        /// building, world units; 0 for scratches.</summary>
        internal static float Margin(int cls)
        {
            return cls == HitHeavy ? HeavyMarginM * K : cls == HitMedium ? MediumMarginM * K : 0f;
        }

        /// <summary>Is a hit at (x, y, z) on this site's bunker? groundY is the
        /// floor's world height (NaN: unknown, height not checked).</summary>
        internal static bool OnBuilding(PowerSite site, int cls, float x, float y, float z, float groundY)
        {
            float m = Margin(cls);
            if (site == null || m <= 0f || !Finite(x) || !Finite(z)) return false;
            double a = -site.Yaw * Math.PI / 180.0;
            float dx = x - site.X, dz = z - site.Z;
            // Unity yaw turns +z toward +x; undo it to read bunker-local axes.
            float lx = (float)(dx * Math.Cos(a) + dz * Math.Sin(a));
            float lz = (float)(-dx * Math.Sin(a) + dz * Math.Cos(a));
            if (Math.Abs(lx) > site.HalfX + m || Math.Abs(lz) > site.HalfZ + m) return false;
            if (!Finite(groundY) || !Finite(y)) return true;
            return y >= groundY - m && y <= groundY + site.Height + m;
        }

        /// <summary>Power 5: is (x, y, z) inside the bunker, within its walls,
        /// floor and roof? groundY NaN: height not checked.</summary>
        internal static bool InsideHouse(PowerSite site, float x, float y, float z, float groundY)
        {
            if (site == null || !Finite(x) || !Finite(z)) return false;
            double a = -site.Yaw * Math.PI / 180.0;
            float dx = x - site.X, dz = z - site.Z;
            float lx = (float)(dx * Math.Cos(a) + dz * Math.Sin(a));
            float lz = (float)(-dx * Math.Sin(a) + dz * Math.Cos(a));
            float w = site.WallM * K;
            if (Math.Abs(lx) > site.HalfX - w || Math.Abs(lz) > site.HalfZ - w) return false;
            if (!Finite(groundY) || !Finite(y)) return true;
            return y >= groundY - w && y <= groundY + site.Height - w;
        }

        /// <summary>Power 5: C4 inside the house on the generator - straight
        /// to the generator, the armour is not touched. charges = the chain's
        /// summed shares on it; need = charges that destroy it. Returns true
        /// when the state changed.</summary>
        internal static bool Demolition(ref PowerState s, float charges, float need, float now)
        {
            if (!Finite(charges) || charges <= 0f || s.GenHp <= 0f) return false;
            s.Hits++;
            s.GenHp = Math.Max(0f, s.GenHp - charges / Math.Max(0.1f, need));
            if (s.GenHp < 0.001f) s.GenHp = 0f;
            if (s.GenHp <= 0f) s.DownSince = now;
            s.Revision++;
            return true;
        }

        /// <summary>A counted hit on the bunker. Scratches change nothing.
        /// Returns true when the state changed; genHit when it reached the generator.</summary>
        internal static bool Strike(ref PowerState s, PowerSite site, int cls, float now, out bool genHit)
        {
            genHit = false;
            if (cls != HitHeavy && cls != HitMedium) return false;
            s.Hits++;
            if (s.Armour > 0)
                s.Armour = Math.Max(0, s.Armour - (cls == HitHeavy ? HeavyCost : MediumCost));
            else if (s.GenHp > 0f)
            {
                s.GenHp = Math.Max(0f, s.GenHp - (cls == HitHeavy ? GenHeavy : GenMedium));
                if (s.GenHp < 0.001f) s.GenHp = 0f;
                if (s.GenHp <= 0f) s.DownSince = now;
                genHit = true;
            }
            s.Revision++;
            return true;
        }

        /// <summary>The running generator drinks its day tank. Returns true when
        /// it ran dry this step (the network goes down).</summary>
        internal static bool Burn(ref PowerState s, float seconds, float tankMinutes)
        {
            if (!Finite(seconds) || seconds <= 0f || !Powered(s)) return false;
            float full = Math.Max(0.1f, tankMinutes) * 60f;
            s.Tank = Math.Max(0f, s.Tank - seconds / full);
            if (s.Tank > 0f) return false;
            s.Revision++;
            return true;
        }

        /// <summary>Fraction of a full day tank the depot pump may add now.</summary>
        internal static float RefillWant(PowerState s, float seconds, float perMinute)
        {
            if (!Finite(seconds) || seconds <= 0f || s.GenHp <= 0f || s.Tank >= 1f) return 0f;
            return Math.Min(1f - s.Tank, Math.Max(0f, perMinute) * seconds / 60f);
        }

        /// <summary>Pour a tank fraction in. A refill of a dry tank is a change
        /// every client must see (the network comes back).</summary>
        internal static bool Refill(ref PowerState s, float fraction)
        {
            if (!Finite(fraction) || fraction <= 0f || s.GenHp <= 0f) return false;
            bool dry = s.Tank <= 0f;
            s.Tank = Math.Min(1f, s.Tank + fraction);
            if (dry) s.Revision++;
            return dry;
        }

        /// <summary>Master: a hand refuel of `units` from a canister, at most one
        /// canister per RefuelSeconds. Returns the tank fraction added.</summary>
        internal static float HandRefuel(ref PowerState s, float units, float sinceLast, out bool changed)
        {
            changed = false;
            if (!Finite(units) || units <= 0f || units > CanUnits + 0.01f || s.GenHp <= 0f || s.Tank >= 1f
                || !Finite(sinceLast) || sinceLast < RefuelSeconds - BreakerSlack) return 0f;
            float before = s.Tank;
            changed = Refill(ref s, units / TankUnits);
            return s.Tank - before;
        }

        internal static float BreakerSeconds(bool close) { return close ? BreakerOnSeconds : BreakerOffSeconds; }

        /// <summary>Master: a finished breaker action is valid after its full
        /// duration (less network slack) and only if it changes something.</summary>
        internal static bool BreakerDone(PowerState s, bool close, float started, float now)
        {
            if (!Finite(started) || !Finite(now) || started < 0f || s.Breaker == close) return false;
            return now - started >= BreakerSeconds(close) - BreakerSlack;
        }

        internal static bool SetBreaker(ref PowerState s, bool close)
        {
            if (s.Breaker == close) return false;
            s.Breaker = close; s.Revision++;
            return true;
        }

        /// <summary>Master: rebuild a generator destroyed long enough ago.</summary>
        internal static bool Restore(ref PowerState s, PowerSite site, float now, float minutes)
        {
            if (s.GenHp > 0f || s.DownSince < 0f || !Finite(now) || now - s.DownSince < Math.Max(0.1f, minutes) * 60f) return false;
            int revision = s.Revision, hits = s.Hits;
            float tank = s.Tank;
            s = Fresh(site);
            s.Tank = Math.Max(0.25f, tank);
            s.Revision = revision + 1; s.Hits = hits;
            return true;
        }

        /// <summary>Client: accept the master's snapshot if it is sane.</summary>
        internal static bool Snapshot(ref PowerState s, PowerSite site, float armour, float hp, float tank,
                                      float flags, float revision, float hits)
        {
            if (!Finite(armour) || !Finite(hp) || !Finite(tank) || !Finite(flags) || !Finite(revision) || !Finite(hits)
                || armour < 0f || armour > Math.Max(1, site.Armour) || hp < 0f || hp > 1f || tank < 0f || tank > 1f
                || revision < 0f || hits < 0f) return false;
            s.Armour = (int)Math.Round(armour); s.GenHp = hp; s.Tank = tank;
            s.Breaker = ((int)Math.Round(flags) & 1) != 0;
            s.Revision = (int)Math.Round(revision); s.Hits = (int)Math.Round(hits);
            // Master clocks differ: a new master stamps a wreck when it first sees it.
            s.DownSince = -1f;
            return true;
        }

        internal static float Flags(PowerState s) { return s.Breaker ? 1f : 0f; }

        /// <summary>The fed AA gun without power: by eye only, short range,
        /// larger errors, slower correction and reaction.</summary>
        internal static AACalibration Dark(AACalibration c)
        {
            c.Range = Math.Min(c.Range, DarkRangeM);
            c.InitialMil *= DarkError;
            c.FloorMil *= DarkError;
            c.Walk = Math.Min(1f, 1f - (1f - c.Walk) * DarkWalk);
            c.FuzeError *= DarkFuze;
            c.Reaction *= DarkReaction;
            return c;
        }

        /// <summary>ZU-23 by eye without power: first-burst error.</summary>
        internal static float DarkInitial(float initial) { return initial * DarkError; }

        /// <summary>ZU-23 by eye without power: burst-to-burst correction.</summary>
        internal static float DarkCorrect(float error, float distance, float floorMil)
        {
            float floor = Math.Max(0f, distance) * floorMil * 0.001f * DarkError;
            return Math.Max(floor, error * (1f - 0.5f * DarkWalk));
        }

        internal static float DarkRange(float rangeUnits) { return Math.Min(rangeUnits, DarkRangeM * K); }
    }
}
