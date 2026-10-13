// Next Day: Survival - Revival Toolkit. Air gunners: the pure rules.
//
// What a weapon is worth from an aircraft seat, where each gunner station
// looks, and how the An-2 flies a pylon turn around a marked point. No Unity
// here: research/air_gunners_check.py compiles this file with csc 3.5 next to
// tests/air_gunners_test.cs and runs the rating order and the orbit geometry.
// Revival.AirGunners.cs is the live side. docs/ai/AIR_BRIDGE.md, "Gunners".
//
// C# 3.0, ASCII only.
using System;

namespace NextDayRevival
{
    /// <summary>The weapons_db numbers of one firearm (WeaponFirearmData).</summary>
    internal struct AirWeaponStats
    {
        internal int ItemId;
        internal float Interval;        // rateOfFire: seconds between two shots
        internal float Spread;          // Spread
        internal float Damage;          // Damage
        internal float Reload;          // ReloadTime, seconds
        internal float EffectiveRange;  // EffectiveRange
        internal int Magazine;          // MaxBullets
        internal int Modes;             // ShootModes: 0 single, 1 single+auto, 2 auto
        internal bool Shotgun;          // WeaponType 1
        internal bool Scoped;           // a Scope texture / path
        internal bool Launcher;         // known rocket item (LAW, Stinger)
        internal string Cal;            // "7,62", "12", ".45 ACP", "66"
    }

    /// <summary>
    /// The air weapon rating, 0..1, or Forbidden for a rocket launcher (the
    /// backblast). Computed from the weapon's own numbers, no table per weapon:
    ///   fire   rounds per second while the trigger is held - the heaviest
    ///          factor - and, lightly, the share of the time not spent
    ///          reloading;
    ///   spread medium is best: a log-normal bell around 0.024, a pinpoint
    ///          rifle is as hard to walk onto a target from a moving door as a
    ///          scattergun is wasteful;
    ///   mag    magazine size, saturating;
    ///   punch  damage per round, and reach (EffectiveRange) - a pistol round
    ///          carries less from 150 m up.
    /// Single-shot/bolt/pump actions, shotguns and pistols take a heavy
    /// penalty, a scope a moderate one (it cannot be used from a door).
    /// On the real weapons_db: MG42/RPD/XM8 LMG/RPK, then the assault rifles,
    /// then the PP-19 SMG, then the DMRs, then everything near zero.
    /// </summary>
    internal static class AirWeaponRating
    {
        internal const float Forbidden = -1f;
        internal const float IdealSpread = 0.024f;
        internal const float SpreadWidth = 0.7f;      // ln units
        internal const float LauncherCalibre = 20f;   // mm: anything bigger is a rocket
        internal const float ActionPenalty = 0.15f;   // single shot / bolt / pump
        internal const float ShotgunPenalty = 0.15f;
        internal const float PistolPenalty = 0.15f;
        internal const float ScopePenalty = 0.45f;
        internal const int PistolFirst = 1200, PistolLast = 1299; // the game's "hg" band

        internal const int Useless = 0, Weak = 1, Medium = 2, Good = 3, Excellent = 4;

        /// <summary>The leading number of a calibre string: "7,62" 7.62,
        /// "12,7" 12.7, ".45 ACP" 0.45, "66" 66. 0 when there is none.</summary>
        internal static float Calibre(string cal)
        {
            if (string.IsNullOrEmpty(cal)) return 0f;
            int i = 0;
            while (i < cal.Length && cal[i] == ' ') i++;
            double whole = 0, frac = 0, scale = 0.1;
            bool any = false, dot = false;
            for (; i < cal.Length; i++)
            {
                char c = cal[i];
                if (c >= '0' && c <= '9')
                {
                    any = true;
                    if (dot) { frac += (c - '0') * scale; scale *= 0.1; }
                    else whole = whole * 10 + (c - '0');
                }
                else if ((c == ',' || c == '.') && !dot) dot = true;
                else break;
            }
            return any ? (float)(whole + frac) : 0f;
        }

        internal static bool IsLauncher(AirWeaponStats w)
        {
            return w.Launcher || Calibre(w.Cal) >= LauncherCalibre;
        }

        internal static bool IsPistol(int itemId) { return itemId >= PistolFirst && itemId <= PistolLast; }

        /// <summary>Rounds per second while the trigger is held: 1 - e^(-rps/6).</summary>
        internal static float FireTerm(AirWeaponStats w)
        {
            float rps = 1f / Math.Max(0.01f, w.Interval);
            return 1f - (float)Math.Exp(-rps / 6f);
        }

        /// <summary>The share of a magazine cycle spent firing, not reloading.</summary>
        internal static float Duty(AirWeaponStats w)
        {
            float firing = Math.Max(1, w.Magazine) * Math.Max(0.01f, w.Interval);
            return firing / (firing + Math.Max(0.5f, w.Reload));
        }

        internal static float SpreadTerm(float spread)
        {
            double l = Math.Log(Math.Max(0.00001f, spread) / IdealSpread);
            double bell = Math.Exp(-(l * l) / (2.0 * SpreadWidth * SpreadWidth));
            return (float)(0.3 + 0.7 * bell);
        }

        internal static float MagazineTerm(int magazine)
        {
            float m = Math.Max(1, magazine);
            return m / (m + 30f);
        }

        internal static float Rate(AirWeaponStats w)
        {
            if (IsLauncher(w)) return Forbidden;
            float fire = FireTerm(w);
            float duty = Duty(w);
            float mag = MagazineTerm(w.Magazine);
            float spread = SpreadTerm(w.Spread);
            float punch = Clamp01(w.Damage / 45f);
            float reach = Clamp01(w.EffectiveRange / 100f);
            if (fire <= 0f || punch <= 0f || reach <= 0f) return 0f;
            // Fire rate carries the biggest exponent: it weighs most.
            double score = Math.Pow(fire, 0.7) * Math.Pow(duty, 0.1) * Math.Pow(mag, 0.2)
                * Math.Pow(spread, 0.15) * Math.Pow(punch, 0.2) * Math.Pow(reach, 0.15);
            if (w.Modes == 0) score *= ActionPenalty;
            if (w.Shotgun) score *= ShotgunPenalty;
            if (IsPistol(w.ItemId)) score *= PistolPenalty;
            if (w.Scoped) score *= ScopePenalty;
            return Clamp01((float)score);
        }

        internal static int Tier(float rating)
        {
            if (rating >= 0.73f) return Excellent;
            if (rating >= 0.65f) return Good;
            if (rating >= 0.55f) return Medium;
            if (rating >= 0.15f) return Weak;
            return Useless;
        }

        /// <summary>The cone half-angle in radians a round leaves the door in:
        /// half the weapon's own spread plus the aircraft's shake, which a good
        /// air weapon rides out (bursts, a big magazine) and a bad one does not.</summary>
        internal static float ShotCone(float weaponSpread, float rating, float maxShake)
        {
            float r = Clamp01(rating);
            return Math.Max(0f, weaponSpread) * 0.5f + maxShake * (float)Math.Pow(1f - r, 1.5);
        }

        /// <summary>Seconds between two rounds a gunner may fire. A weapon
        /// without automatic fire is one round per click anyway; the floor
        /// keeps the shot events inside the Photon budget (E-051).</summary>
        internal static float Cadence(AirWeaponStats w, float floor)
        {
            return Math.Max(floor, Math.Max(0.01f, w.Interval));
        }

        static float Clamp01(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }
    }

    /// <summary>One gunner position, in metres of the aircraft's model frame
    /// (x right, y up, z nose) and degrees from the nose (yaw + right, pitch +
    /// up).</summary>
    internal struct AirStation
    {
        internal int Id;
        internal float X, Y, Z;               // the gunner's eye
        internal float CentreYaw, HalfYaw;    // the arc
        internal float PitchMin, PitchMax;
        internal bool Heavy;                  // the 12.7 mm tail gun, not the gunner's own weapon
    }

    internal static class AirStations
    {
        internal const int None = 0, An2Tail = 1, An2Door = 2, Mi8Left = 3, Mi8Right = 4;

        /// <summary>An-2F style: a 12.7 mm gun on the upper rear fuselage,
        /// ahead of the fin (z -8.76) and above the tailplane (y 2.62); its arc
        /// is the rear upper hemisphere, the fin and tailplane masked.</summary>
        internal static AirStation Get(int id)
        {
            AirStation s = new AirStation();
            s.Id = id;
            switch (id)
            {
                case An2Tail:
                    s.X = 0f; s.Y = 3.55f; s.Z = -6.0f;
                    s.CentreYaw = 180f; s.HalfYaw = 75f; s.PitchMin = -8f; s.PitchMax = 70f;
                    s.Heavy = true;
                    break;
                case An2Door:
                    // The port cargo door (an2_rig.txt door -0.877 1.282 -2.1),
                    // a kneeling man's eye in the opening.
                    s.X = -0.95f; s.Y = 2.3f; s.Z = -2.1f;
                    s.CentreYaw = -90f; s.HalfYaw = 70f; s.PitchMin = -75f; s.PitchMax = 20f;
                    break;
                case Mi8Left:
                    // Mi-8 model frame: centre line x 0.9, floor near y 1.2,
                    // the cabin seats z 2 .. -2.2 (PlayerHeli.Seat).
                    s.X = -0.4f; s.Y = 2.4f; s.Z = 3.0f;
                    s.CentreYaw = -90f; s.HalfYaw = 75f; s.PitchMin = -75f; s.PitchMax = 20f;
                    break;
                case Mi8Right:
                    s.X = 2.2f; s.Y = 2.4f; s.Z = 1.0f;
                    s.CentreYaw = 90f; s.HalfYaw = 75f; s.PitchMin = -75f; s.PitchMax = 20f;
                    break;
                default:
                    s.Id = None;
                    break;
            }
            return s;
        }

        /// <summary>The station after <paramref name="current"/> for a cabin
        /// passenger: An-2 seat, tail, door, seat; Mi-8 seat, left, right, seat.</summary>
        internal static int Next(int current, bool plane)
        {
            if (plane) return current == None ? An2Tail : current == An2Tail ? An2Door : None;
            return current == None ? Mi8Left : current == Mi8Left ? Mi8Right : None;
        }

        internal static float Wrap(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg <= -180f) deg += 360f;
            return deg;
        }

        /// <summary>Keep a look inside the station's arc.</summary>
        internal static void Clamp(AirStation s, ref float yaw, ref float pitch)
        {
            float d = Wrap(yaw - s.CentreYaw);
            if (d > s.HalfYaw) d = s.HalfYaw;
            if (d < -s.HalfYaw) d = -s.HalfYaw;
            yaw = Wrap(s.CentreYaw + d);
            if (pitch < s.PitchMin) pitch = s.PitchMin;
            if (pitch > s.PitchMax) pitch = s.PitchMax;
        }

        internal static bool InArc(AirStation s, float yaw, float pitch)
        {
            return Math.Abs(Wrap(yaw - s.CentreYaw)) <= s.HalfYaw && pitch >= s.PitchMin && pitch <= s.PitchMax;
        }

        /// <summary>The tail gun's own airframe in the way: the fin straight
        /// aft and low, the tailplane below the horizon aft.</summary>
        internal static bool Masked(AirStation s, float yaw, float pitch)
        {
            if (!s.Heavy) return false;
            float aft = 180f - Math.Abs(Wrap(yaw));
            return (aft < 14f && pitch < 30f) || (aft < 40f && pitch < 3f);
        }

        /// <summary>May this station fire along (yaw, pitch)?</summary>
        internal static bool Clear(AirStation s, float yaw, float pitch)
        {
            return InArc(s, yaw, pitch) && !Masked(s, yaw, pitch);
        }
    }

    /// <summary>
    /// The pylon turn around a marked point: a banked circle flown so the
    /// point stays off one wing - the port side for the An-2, whose cargo door
    /// is there. Positions in metres (x east, z north), headings in degrees
    /// clockwise from north, bank + right. Guide is a vector-field steering
    /// law: far away it heads for the circle, on it the bank of a level turn
    /// of that radius at that speed (feed-forward) plus a heading correction.
    /// </summary>
    internal static class OrbitCore
    {
        internal const float G = 9.81f;
        internal const int Left = -1, Right = 1;

        internal static float BankFor(float speed, float radius)
        {
            if (radius <= 1f) return 0f;
            return (float)(Math.Atan(speed * speed / (G * radius)) * 180.0 / Math.PI);
        }

        internal static float RadiusFor(float speed, float bankDeg)
        {
            double t = Math.Tan(Math.Max(1f, Math.Abs(bankDeg)) * Math.PI / 180.0);
            return (float)(speed * speed / (G * t));
        }

        /// <summary>Heading (deg, clockwise from north) from a to b.</summary>
        internal static float Bearing(float ax, float az, float bx, float bz)
        {
            return (float)(Math.Atan2(bx - ax, bz - az) * 180.0 / Math.PI);
        }

        /// <summary>The bank (deg, + right) the autopilot asks for.</summary>
        internal static float Guide(float px, float pz, float heading, float speed,
                                    float cx, float cz, float radius, int dir, float maxBank,
                                    out float wantHeading)
        {
            float dx = px - cx, dz = pz - cz;
            float d = (float)Math.Sqrt(dx * dx + dz * dz);
            float theta = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);   // centre -> plane
            float r = Math.Max(10f, radius);
            float off = (float)(Math.Atan((d - r) / (0.6f * r)) * 180.0 / Math.PI);
            if (off > 80f) off = 80f;
            if (off < -80f) off = -80f;
            wantHeading = AirStations.Wrap(theta + dir * 90f + dir * off);
            float err = AirStations.Wrap(wantHeading - heading);
            float near = 1f - Math.Abs(d - r) / r;
            if (near < 0f) near = 0f;
            float bank = dir * BankFor(speed, r) * near + 1.2f * err;
            if (bank > maxBank) bank = maxBank;
            if (bank < -maxBank) bank = -maxBank;
            return bank;
        }

        /// <summary>Where the centre is seen from the nose, deg (+ right).</summary>
        internal static float CentreYaw(float px, float pz, float heading, float cx, float cz)
        {
            return AirStations.Wrap(Bearing(px, pz, cx, cz) - heading);
        }

        /// <summary>The throttle lever the orbit asks for: a slow, steady
        /// circle (target speed in m/s), a gentle proportional step.</summary>
        internal static float Throttle(float lever, float speed, float target, float dt)
        {
            float want = lever + (target - speed) * 0.08f;
            if (want < 0.15f) want = 0.15f;
            if (want > 1f) want = 1f;
            float step = 0.35f * dt;
            if (want > lever + step) return lever + step;
            if (want < lever - step) return lever - step;
            return want;
        }

        /// <summary>Is a man at (x, z) close enough to the mark to be shot at?</summary>
        internal static bool NearMark(float x, float z, float cx, float cz, float radius)
        {
            float dx = x - cx, dz = z - cz;
            return dx * dx + dz * dz <= radius * radius;
        }
    }
}
