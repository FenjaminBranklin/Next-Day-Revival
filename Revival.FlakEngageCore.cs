// A L2: the east airfield's engagement zone and target discipline for the
// heavy guns. Engine-free; compiled unchanged by research/flak_engage_early_sim.py.
using System;

namespace NextDayRevival
{
    internal static class FlakEngageCore
    {
        // The C1 tower (TowerRadar.MastSpot), world units.
        internal const float CentreX = 4272.12f, CentreZ = 1335.05f;
        // A manned airfield 52-K fires on a hostile inside this circle around the
        // tower, by eye too; the radar adds range beyond it and accuracy.
        internal const float ZoneMetres = 4500f;
        // Slant reach past the rim: the guns stand up to ~400 m from the tower
        // and a bomber flies 550 m up.
        internal const float RimMetres = 1000f;
        // A target that vanishes for a moment (a terrain/LOS sample, a radar
        // shadow tick) and returns keeps the crew's solution and reaction.
        internal const float ResumeSeconds = 4f;
        // Radar threat points a new aircraft must beat the current one by
        // before a crew abandons its bracketing (~37 s of ETA, or a heavier class).
        internal const int SwitchMargin = 150;

        internal static bool InZone(float x, float z, float zoneMetres, float k)
        {
            float dx = x - CentreX, dz = z - CentreZ, r = Math.Max(0f, zoneMetres) * k;
            return dx * dx + dz * dz <= r * r;
        }

        /// <summary>Engagement range in metres: the crew's own calibration, or
        /// the zone's reach for an aircraft inside the airfield zone.</summary>
        internal static float Reach(float calibrationMetres, bool inZone, float zoneMetres, float maxFuzeMetres)
        {
            if (!inZone) return calibrationMetres;
            return Math.Max(calibrationMetres, Math.Min(zoneMetres + RimMetres, maxFuzeMetres));
        }

        /// <summary>Collection radius in metres: never smaller than what a zone
        /// target may need.</summary>
        internal static float CollectMetres(float calibrationMetres, bool airfieldHeavy, float zoneMetres, float maxFuzeMetres)
        {
            return airfieldHeavy ? Reach(calibrationMetres, true, zoneMetres, maxFuzeMetres) : calibrationMetres;
        }

        /// <summary>Stay on the aircraft being bracketed unless the radar shows
        /// a clearly more urgent one. By eye the crew keeps its target.</summary>
        internal static bool Keep(int currentThreat, int bestThreat, bool radar)
        {
            return !radar || currentThreat + SwitchMargin > bestThreat;
        }

        /// <summary>The same aircraft found again within ResumeSeconds of its
        /// loss: the bracketing continues, no new reaction or first-shot offset.</summary>
        internal static bool Resume(bool engaged, bool sameAircraft, float lostSeconds)
        {
            return engaged && sameAircraft && lostSeconds >= 0f && lostSeconds <= ResumeSeconds;
        }

        /// <summary>E L1: an engaged aircraft still hostile and in reach whose
        /// line of sight failed for a moment (a tree, a ridge sample, the
        /// tower) stays the crew's target - laid and fired on its track - for
        /// ResumeSeconds after the last clear ray. One failed ray no longer
        /// drops it, stops the fire and restarts the reaction.</summary>
        internal static bool Hold(bool engaged, bool inReach, float sinceSight)
        {
            return engaged && inReach && sinceSight >= 0f && sinceSight <= ResumeSeconds;
        }

        // E L1: with the radar manned for its side a gun with nothing in reach
        // lays on the nearest hostile contact out to this slant range, so it
        // is on bearing (and its crew past its reaction) when the target
        // enters range. Laying only: a cue never fires.
        internal const float CueMetres = 12000f;

        /// <summary>Collection radius in metres for a gun that may be cued.</summary>
        internal static float CueCollectMetres(float collectMetres, bool direction)
        {
            return direction ? Math.Max(collectMetres, CueMetres) : collectMetres;
        }

        /// <summary>Reaction already spent when a target enters reach: the
        /// seconds the crew laid on it as a radar cue (same aircraft only).</summary>
        internal static float CueHeld(bool sameAircraft, float cueSeconds)
        {
            return sameAircraft ? Math.Max(0f, cueSeconds) : 0f;
        }
    }
}
