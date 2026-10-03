// Next Day: Survival - Revival Toolkit
//
// C M6: A HELD MAN THE GAME HAS WOUNDED IS LET GO
// (docs/ai/tasks/c-m6-immortal-arty-crews.md).
//
// Field report 6.67.0: some of the crew at the settlement artillery trucks
// could not be killed, and mercs hung on them for whole magazines. The cause
// is the vanilla wounded state meeting our own crew holds (CONFIRMED IL):
//
//   - NPC_AI2.ApplyDamage sends a man left with 0..25 health into the wounded
//     state half the time: OnWoundedAction sets MainState 7, _isWoundedAction
//     true and a 0.5 s pause. ApplyDamage returns at IL_0040 while
//     _isWoundedAction is set - no round, no blast, no knife hurts him.
//   - The ONLY code that clears the flag is NPC_AI2.WoundedActions, and only
//     when GetCalculatedPauseTime() has run down to 0. WoundedActions is
//     called from StateAction for MainState 7 and for nothing else.
//   - ArtyBattery.Posted, twice a second on every crewman IsAlive() calls
//     alive (Health > 0, so a wounded man is "alive"): SetPauseTime(1.4) -
//     the pause never runs down - and SetStateWithAnimAndSync back to the
//     working/standing state - MainState leaves 7 and WoundedActions is never
//     called again. Either alone pins the flag for good. He was stood back up
//     at his station, still a target to every merc, and immune to all of it.
//   - The technical's crew hold (every frame) and NpcWar's own state/pause
//     drive (defenders, ground squads) did the same to any man they held.
//
// The rule is the same for every holder: a man in MainState 7 belongs to the
// game. No pause, no state, no placement - WoundedActions clears the flag
// after its 0.5 s and the next round finishes him, exactly like any other NPC
// lying wounded. A man found STANDING with the flag still set was stood up by
// something before WoundedActions could run; his owner clears the flag, which
// is the one write WoundedActions would have made. A man with the flag set is
// not a target meanwhile (ApplyDamage would refuse him anyway).
//
// Pure, no UnityEngine: research/wounded_hold_check.py compiles this file into
// its offline model of the IL. C# 3.0, ASCII only.

namespace NextDayRevival
{
    internal static class WoundedHoldCore
    {
        /// <summary>NPCMainState.Wounded (NPC_AI2.OnWoundedAction).</summary>
        internal const int MainWounded = 7;
        /// <summary>NPCMainState.Dead.</summary>
        internal const int MainDead = 3;

        /// <summary>Hold him: pause, state, placement as the holder wants.</summary>
        internal const int Hold = 0;
        /// <summary>Let go: dead, or lying in the game's wounded state.</summary>
        internal const int Release = 1;
        /// <summary>Standing with the wounded flag still set: clear it, then hold.</summary>
        internal const int Repair = 2;

        /// <summary>What a crew hold may do with this man. <paramref name="alive"/>
        /// is NPC_AI2.IsAlive (MainState != 3 and Health > 0).</summary>
        internal static int Judge(bool alive, int mainState, bool woundFlag)
        {
            if (!alive || mainState == MainDead) return Release;
            if (mainState == MainWounded) return Release;
            return woundFlag ? Repair : Hold;
        }

        /// <summary>Does this man still stand at his post? A man lying wounded
        /// holds no gun, flies no drone and lays no sight.</summary>
        internal static bool Standing(bool alive, int mainState)
        {
            return alive && mainState != MainDead && mainState != MainWounded;
        }

        /// <summary>May a hold refresh his pause or change his state? Never on
        /// a man the game has put into the wounded state: the pause would stop
        /// WoundedActions from clearing _isWoundedAction, the state change
        /// would stop it from running at all.</summary>
        internal static bool MayDrive(int mainState)
        {
            return mainState != MainWounded;
        }

        /// <summary>Would NPC_AI2.ApplyDamage get past its wounded gate?</summary>
        internal static bool Hurtable(bool woundFlag)
        {
            return !woundFlag;
        }
    }
}
