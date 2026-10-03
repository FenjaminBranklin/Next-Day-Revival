// Next Day: Survival - Revival Toolkit
//
// C M6: the live side of WoundedHoldCore (why: see Revival.WoundedHoldCore.cs
// and docs/ai/tasks/c-m6-immortal-arty-crews.md).
//
//   WoundedLying(ai)     MainState 7 - every crew hold lets him go
//                        (ArtyBattery.Posted/StationMan, TechnicalCrew.Lebt,
//                        NpcWar.SetState/Quiet).
//   WoundFlag(ai)        NPC_AI2._isWoundedAction - Hurtable refuses him.
//   RepairStuckWound(ai) owner only: a man STANDING with the flag set gets
//                        the one write WoundedActions would have made.
//
// No tick of its own: every call rides a holder's existing cadence (Posted
// 2 Hz per crewman, Targetable at the target scan). One field read each,
// FastField, no allocation. C# 3.0, ASCII only.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        static FieldInfo _fWoundFlag;
        static bool _woundLooked;
        static int _woundRepairs;

        static FieldInfo WoundFlagField()
        {
            if (_woundLooked) return _fWoundFlag;
            _woundLooked = true;
            if (!LookUp() || _npcType == null) return null;
            _fWoundFlag = AccessTools.Field(_npcType, "_isWoundedAction");
            if (_fWoundFlag != null && _fWoundFlag.FieldType != typeof(bool)) _fWoundFlag = null;
            if (_fWoundFlag == null)
                RevivalPlugin.L.LogWarning("NpcWar: NPC_AI2._isWoundedAction missing - a held man "
                    + "the game wounded cannot be checked for a stuck wound.");
            return _fWoundFlag;
        }

        /// <summary>Is he lying in the game's wounded state (MainState 7)?
        /// The same test as <see cref="GroundDowned"/>, under the name the
        /// crew holds ask it by.</summary>
        internal static bool WoundedLying(Component ai)
        {
            return ai != null && LookUp()
                && !WoundedHoldCore.MayDrive(IntField(ai, _fMainState, -1));
        }

        /// <summary>NPC_AI2._isWoundedAction: ApplyDamage returns while it is
        /// set. False when it cannot be read.</summary>
        internal static bool WoundFlag(Component ai)
        {
            FieldInfo f = WoundFlagField();
            if (ai == null || f == null) return false;
            try { return FastField.GetBool(f, ai); }
            catch { return false; }
        }

        /// <summary>What a crew hold may do with this man (WoundedHoldCore
        /// Hold / Release / Repair). <paramref name="alive"/> is the holder's
        /// own IsAlive answer, so no second reflective call is paid here.</summary>
        internal static int WoundJudge(Component ai, bool alive)
        {
            if (ai == null) return WoundedHoldCore.Release;
            // Without the game's NPC type there is no state to read: the
            // holder keeps its own answer rather than letting everybody go.
            if (!LookUp()) return alive ? WoundedHoldCore.Hold : WoundedHoldCore.Release;
            int main = IntField(ai, _fMainState, -1);
            // The flag is only worth a read on a man who is standing.
            bool flag = alive && WoundedHoldCore.MayDrive(main) && WoundFlag(ai);
            return WoundedHoldCore.Judge(alive, main, flag);
        }

        /// <summary>OWNER ONLY (ApplyDamage runs on the Photon owner, and so
        /// does the flag it reads). A man standing with _isWoundedAction still
        /// set was stood up before NPC_AI2.WoundedActions could clear it, and
        /// would refuse every round for the rest of the level. Clear it - the
        /// write WoundedActions itself makes. Returns true when it did.</summary>
        internal static bool RepairStuckWound(Component ai, string who)
        {
            FieldInfo f = WoundFlagField();
            if (ai == null || f == null) return false;
            try
            {
                // The flag first: it is a plain field read, and it is false on
                // every man who matters to the frame budget.
                if (!FastField.GetBool(f, ai)) return false;
                if (!WoundedHoldCore.MayDrive(IntField(ai, _fMainState, -1))) return false;
                if (!IsMine(ai)) return false;
                FastField.SetBool(f, ai, false);
                if (_woundRepairs++ < 5)
                    RevivalPlugin.L.LogInfo("NpcWar: " + who + " " + ai.name + " stood with the "
                        + "wounded flag still set (immune to damage) - cleared.");
                return true;
            }
            catch { return false; }
        }
    }
}
