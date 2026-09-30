// W Tower 4, the retake raids - the pure core (docs/ai/tasks/w-tower4-retake-raids.md).
// It knows nothing of the game or of Unity, so research/retake_raids_check.py
// compiles this file UNCHANGED with the .NET 3.5 csc into a deterministic
// simulation. Revival.RetakeRaids.cs feeds it (who holds the airfield, the
// clock) and carries its raids out through AirEvents and the heli landings.
//
//   SETTINGS  the map editor's "Retake raids" panel (airdef.py key
//             "retakeRaids"), one comment row "#retake" in the air event
//             table - an older plugin skips '#' rows. No row: the defaults
//             (on). A row that does not parse changes nothing.
//   CLOCK     the master's hold clock. The garrison holds: nothing. A side
//             takes the airfield: the first raid FirstMinutes later, then
//             every GapMinutes, the gap shrinking the longer the side holds
//             (1 + Ramp per hour held), never below MinGapMinutes; all
//             divided by the editor's frequency. A raid that is still in the
//             air when the next falls due pushes it back by a minute. A new
//             holder (or the garrison back) starts over.
//   COMPOSE   one raid: its counts grow with the raids already flown in this
//             hold (+Growth per raid up to MaxGrowthLevel), times the
//             editor's strength. The bomb target rotates over the targets the
//             editor allows (tower, guns, fuel depot), from a random start.
//             While troops of an earlier raid still fight, a raid brings no
//             new ones (bombs and escort only), and one raid brings at most
//             MaxTroops: the NPC count stays bounded.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no LINQ. ASCII only.

using System;
using System.Globalization;

namespace NextDayRevival
{
    /// <summary>The editor's retake raid settings (one "#retake" row).</summary>
    internal sealed class RetakeSettings
    {
        public const string RowTag = "#retake";

        public bool Enabled = true;
        /// <summary>Raid rhythm multiplier, 0.25..4 (2 = twice as often).</summary>
        public float Frequency = 1f;
        /// <summary>Aircraft/heli count multiplier, 0.5..3.</summary>
        public float Strength = 1f;
        public int Bombers = 1;              // Tu-95 per raid at the start, 0..4
        public int Bombs = 36;               // FAB-250 per Tu-95, 20..60
        public bool HitTower = true, HitGuns = true, HitFuel = true;
        public int Transports = 2;           // An-2 with paratroopers, 0..4
        public int Paratroopers = 8;         // per An-2, 1..12
        public int Helis = 1;                // Mi-8 troop landings, 0..3
        public int HeliTroops = 8;           // per Mi-8, 1..12
        public bool Escort = true;           // An-2 that bomb the AA guns first
        public int Escorts = 2;              // 1..4
        /// <summary>"auto" = the airfield's garrison ([Airfield] DefenderFaction),
        /// else traitor / looter / civilian / neutral.</summary>
        public string Faction = "auto";

        public const int Columns = 14;

        /// <summary>The row "#retake\t..." of the air event table into a new
        /// settings object; null and a reason when a cell is bad.</summary>
        public static RetakeSettings Parse(string row, out string error)
        {
            error = null;
            if (row == null) { error = "no row"; return null; }
            string[] c = row.TrimEnd('\r').Split('\t');
            if (c.Length < Columns || c[0].Trim() != RowTag)
            {
                error = "retake row needs " + Columns + " columns, has " + c.Length;
                return null;
            }
            try
            {
                RetakeSettings s = new RetakeSettings();
                s.Enabled = c[1].Trim() != "0";
                s.Frequency = Clamp(F(c[2]), 0.25f, 4f);
                s.Strength = Clamp(F(c[3]), 0.5f, 3f);
                s.Bombers = ClampI(F(c[4]), 0, 4);
                s.Bombs = ClampI(F(c[5]), 20, 60);
                string t = c[6].Trim().ToLowerInvariant();
                s.HitTower = t.IndexOf('t') >= 0;
                s.HitGuns = t.IndexOf('g') >= 0;
                s.HitFuel = t.IndexOf('f') >= 0;
                s.Transports = ClampI(F(c[7]), 0, 4);
                s.Paratroopers = ClampI(F(c[8]), 1, 12);
                s.Helis = ClampI(F(c[9]), 0, 3);
                s.HeliTroops = ClampI(F(c[10]), 1, 12);
                s.Escort = c[11].Trim() != "0";
                s.Escorts = ClampI(F(c[12]), 1, 4);
                string f = c[13].Trim().ToLowerInvariant();
                if (f != "auto" && f != "traitor" && f != "looter" && f != "civilian" && f != "neutral")
                    throw new FormatException("unknown faction '" + f + "'");
                s.Faction = f;
                return s;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>The first "#retake" row of a table, or the defaults when
        /// the table has none. <paramref name="error"/> is set (and the
        /// defaults are NOT returned: null) when the row is there but bad.</summary>
        public static RetakeSettings FromTable(string[] lines, out string error)
        {
            error = null;
            if (lines != null)
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = lines[i];
                    if (l == null || !l.StartsWith(RowTag + "\t", StringComparison.Ordinal)) continue;
                    return Parse(l, out error);
                }
            return new RetakeSettings();
        }

        /// <summary>Targets the editor allows, in rotation order (tower, guns,
        /// fuel). An editor that allows none gets the tower.</summary>
        public int AllowedTargets(int[] into)
        {
            int n = 0;
            if (HitTower) into[n++] = RetakePlan.TargetTower;
            if (HitGuns) into[n++] = RetakePlan.TargetGuns;
            if (HitFuel) into[n++] = RetakePlan.TargetFuel;
            if (n == 0) into[n++] = RetakePlan.TargetTower;
            return n;
        }

        static float F(string s)
        {
            float v;
            if (!float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                || float.IsNaN(v) || float.IsInfinity(v))
                throw new FormatException("not a number: '" + s + "'");
            return v;
        }

        internal static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
        internal static int ClampI(float v, int lo, int hi)
        {
            int i = (int)Math.Round(v, MidpointRounding.AwayFromZero);
            return i < lo ? lo : i > hi ? hi : i;
        }
    }

    /// <summary>One raid as the core composed it.</summary>
    internal struct RetakeRaid
    {
        public int Level;            // raids already flown in this hold
        public int Target;           // RetakePlan.Target*
        public int Bombers, Bombs;
        public int Transports, Paratroopers;
        public int Helis, HeliTroops;
        public int Escorts;
        public bool TroopsHeld;      // no troops: an earlier raid's still fight

        public int Aircraft { get { return Bombers + Transports + Escorts; } }
        public int Troops { get { return Transports * Paratroopers + Helis * HeliTroops; } }
    }

    internal static class RetakePlan
    {
        public const int TargetTower = 0, TargetGuns = 1, TargetFuel = 2;
        public static readonly string[] TargetNames = { "tower", "guns", "fuel depot" };

        // Rhythm at frequency 1, minutes.
        public const float FirstMinutes = 8f;
        public const float GapMinutes = 30f;
        public const float MinGapMinutes = 10f;
        /// <summary>The gap shrinks by 1 + Ramp per hour held.</summary>
        public const float Ramp = 0.6f;
        /// <summary>Never two raids closer than this, whatever the frequency.</summary>
        public const float FloorMinutes = 3f;
        /// <summary>A due raid waits this long while the previous one still flies.</summary>
        public const float BusyRetrySeconds = 60f;

        // Growth of the counts per raid flown in this hold.
        public const float Growth = 0.35f;
        public const int MaxGrowthLevel = 6;

        public const int MaxBombers = 6, MaxTransports = 6, MaxHelis = 3, MaxEscorts = 4;
        /// <summary>At most this many troops in one raid (CPU: NPCs are the
        /// expensive part); transports go first, then helis, one of each kept.</summary>
        public const int MaxTroops = 36;

        public static float FirstDelaySeconds(RetakeSettings s)
        {
            float f = s == null ? 1f : RetakeSettings.Clamp(s.Frequency, 0.25f, 4f);
            return Max(FloorMinutes, FirstMinutes / f) * 60f;
        }

        /// <summary>Seconds from one raid to the next after
        /// <paramref name="heldHours"/> of holding.</summary>
        public static float GapSeconds(RetakeSettings s, float heldHours)
        {
            float f = s == null ? 1f : RetakeSettings.Clamp(s.Frequency, 0.25f, 4f);
            float h = heldHours < 0f ? 0f : heldHours;
            float gap = Max(MinGapMinutes, GapMinutes / (1f + Ramp * h)) / f;
            return Max(FloorMinutes, gap) * 60f;
        }

        public static float Scale(RetakeSettings s, int level)
        {
            int l = level < 0 ? 0 : level > MaxGrowthLevel ? MaxGrowthLevel : level;
            float st = s == null ? 1f : RetakeSettings.Clamp(s.Strength, 0.5f, 3f);
            return st * (1f + Growth * l);
        }

        /// <summary>
        /// Raid number <paramref name="level"/> of a hold. <paramref name="start"/>
        /// is the hold's random start in the target rotation;
        /// <paramref name="troopsBusy"/>: an earlier raid's troops still fight.
        /// <paramref name="scratch"/> holds at least 3 ints (no allocation).
        /// </summary>
        public static RetakeRaid Compose(RetakeSettings s, int level, int start, bool troopsBusy, int[] scratch)
        {
            RetakeRaid r = new RetakeRaid();
            r.Level = level;
            float k = Scale(s, level);
            int n = s.AllowedTargets(scratch);
            int idx = (start + level) % n;
            if (idx < 0) idx += n;
            r.Target = scratch[idx];
            r.Bombers = Count(s.Bombers, k, MaxBombers);
            r.Bombs = s.Bombs;
            r.TroopsHeld = troopsBusy;
            if (!troopsBusy)
            {
                r.Transports = Count(s.Transports, k, MaxTransports);
                r.Helis = Count(s.Helis, k, MaxHelis);
            }
            r.Paratroopers = s.Paratroopers;
            r.HeliTroops = s.HeliTroops;
            while (r.Troops > MaxTroops)
            {
                if (r.Transports > 1 && (r.Transports * r.Paratroopers >= r.Helis * r.HeliTroops || r.Helis <= 1))
                    r.Transports--;
                else if (r.Helis > 1) r.Helis--;
                else break;
            }
            // The escort goes along whenever there is anything to cover; it
            // grows at most to twice the editor's count.
            if (s.Escort && (r.Bombers + r.Transports + r.Helis) > 0)
                r.Escorts = Count(s.Escorts, Min(k, 2f), MaxEscorts);
            // Nothing at all (every count 0 in the editor, or all troops held
            // with no bombers): one bomber so a due raid is never silent.
            if (r.Bombers + r.Transports + r.Helis + r.Escorts == 0)
            {
                r.Bombers = 1;
                if (s.Escort) r.Escorts = RetakeSettings.ClampI(s.Escorts, 1, MaxEscorts);
            }
            return r;
        }

        static int Count(int baseCount, float k, int max)
        {
            if (baseCount <= 0) return 0;
            int n = (int)Math.Round(baseCount * k, MidpointRounding.AwayFromZero);
            return n < 1 ? 1 : n > max ? max : n;
        }

        static float Max(float a, float b) { return a > b ? a : b; }
        static float Min(float a, float b) { return a < b ? a : b; }
    }

    /// <summary>
    /// The master's hold clock: fed once a second with who holds the airfield
    /// (-1 = its garrison) and the time; says when a raid is due and which.
    /// </summary>
    internal sealed class RetakeClock
    {
        public int Holder = -1;          // Fraction value of the holding side, -1 the garrison
        public float HeldSince;
        public float Next = -1f;         // time of the next raid, -1 none
        public int Level;                // raids flown in this hold
        public int Start;                // the hold's start in the target rotation

        public float HeldHours(float now) { return Holder < 0 ? 0f : (now - HeldSince) / 3600f; }

        /// <summary>
        /// One step. True when a raid is due now: the caller composes it with
        /// <see cref="Level"/> and then calls <see cref="Flown"/>.
        /// <paramref name="busy"/>: the previous raid is still in the air.
        /// <paramref name="startRoll"/> seeds the target rotation of a new hold.
        /// </summary>
        public bool Step(float now, int holder, RetakeSettings s, bool enabled, bool busy, int startRoll)
        {
            if (holder != Holder)
            {
                Holder = holder;
                Level = 0;
                HeldSince = now;
                Start = startRoll < 0 ? -startRoll : startRoll;
                Next = holder < 0 ? -1f : now + RetakePlan.FirstDelaySeconds(s);
            }
            if (Holder < 0 || !enabled || s == null || !s.Enabled)
            {
                // Switched off while held: nothing is due; switched on again,
                // the first raid comes a first delay later.
                if (Holder >= 0) Next = -1f;
                return false;
            }
            if (Next < 0f) { Next = now + RetakePlan.FirstDelaySeconds(s); return false; }
            if (now < Next) return false;
            if (busy) { Next = now + RetakePlan.BusyRetrySeconds; return false; }
            return true;
        }

        /// <summary>The raid composed at <see cref="Level"/> went out.</summary>
        public void Flown(float now, RetakeSettings s)
        {
            Level++;
            Next = now + RetakePlan.GapSeconds(s, HeldHours(now));
        }
    }
}
