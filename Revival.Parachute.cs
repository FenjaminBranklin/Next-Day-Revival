// Next Day: Survival - Revival Toolkit
//
// THE PARACHUTE. An item in the pack (2067) and, when a man jumps out of a
// helicopter high enough with one on his back, a canopy over his head and a
// landing he walks away from.
//
// THIS FILE BUILDS NO PARACHUTE. The game already has a complete one, and it is
// the SAME canopy the humanitarian aid crates hang from:
// "PlayerDataPrefabs/Other/Parachute_Pref", instantiated by
// PlayerObjectsManager.InstanceParachute, parented to the player, animated
// through CharacterParashuteState and hung from the neck bone (AnchorStraps =
// MainChar_Neck). Everything below is one call into that.
//
// WHAT WAS CONFIRMED BEFORE THIS FILE (IL of Assembly-CSharp, research/ilq.py):
//   PlayerMovementController::SetPlayerParashuteFlyState(bool, Vector3)
//     true  - character state type 5, parachute state 1, the player put at the
//             given position (a zero vector means "ground + 1200", which is the
//             game's own drop-in), the backpack swapped for mesh 6099, the
//             weapon holstered and Particles/parachute_vfx started
//     false - all of it undone, the backpack back, speed zeroed
//   PlayerMovementController::PlayerParashuteUncover(bool)
//     opens the canopy: NetworkShowParashute, which is an RPC, so every other
//     client sees the canopy too - this is the ONLY networking the feature
//     needs, and the game does it
//   PlayerMovementController::FixedUpdate, state 5
//     with the canopy open: sinks at 25 * SpeedMovement.Parashute_idle_fly,
//     plays parashute_falling_landing under 2 units and ends the state itself
//     with the canopy CLOSED: 1000 damage on arrival. That is the whole reason
//     the canopy is opened in the same breath as the state is entered here -
//     a state entered without a canopy is a death sentence with an animation.
//
// SO THE RULE IS SHORT: jump high enough with a parachute in the pack and the
// game flies you down. Jump without one and you fall, which is what jumping out
// of a helicopter without a parachute is.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII-only comments and logs; player-facing strings go through
// Loc.T (real Cyrillic), so this file is UTF-8 without BOM.
//
// SEAMS OUTSIDE THIS FILE (two one-line calls):
//   RevivalPlugin.cs  BindConfig / BuildItemTable
// and one caller: Revival.PlayerHeli.cs Jump().

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The parachute item and the one move that turns it into a descent: the
    /// game's own parachute state, entered with the canopy already open.
    /// </summary>
    public static class Parachute
    {
        /// <summary>Item id. 2067 is the first free number after the gas
        /// launcher's shell (2066); the band 2001..3000 is the game's
        /// ammunition band, which is right for something that is CARRIED and
        /// never dragged onto a weapon slot - the same band the mast antenna
        /// (2055) and the drone battery (2056) live in. Anything that has to be
        /// equipped in a slot would have to sit in 1401..1500 instead; that is
        /// the anti-tank mine's lesson and it does not apply here.</summary>
        public const int ItemId = 2067;

        public static ConfigEntry<bool> CfgEnabled, CfgConsume;
        public static ConfigEntry<float> CfgMinHeight;

        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("Parachute", "Enabled", true,
                "Der Fallschirm (Item 2067). Aus: der Sprung aus dem Hubschrauber "
                + "bleibt ein Sturz, und das Item taucht nicht mehr auf.");
            CfgMinHeight = cfg.Bind("Parachute", "MinHeight", 100f,
                "Mindesthoehe ueber Grund in METERN, ab der sich der Schirm "
                + "oeffnen laesst. Darunter reicht die Zeit nicht: der Schirm "
                + "braucht die Hoehe, um zu stehen. Der Wert ist derselbe, den "
                + "die Hoehenanzeige im Hubschrauber zeigt.");
            CfgConsume = cfg.Bind("Parachute", "Consume", true,
                "Ein Sprung verbraucht den Fallschirm. Aus: derselbe Schirm "
                + "laesst sich beliebig oft benutzen.");
        }

        public static bool Enabled
        {
            get { return CfgEnabled == null || CfgEnabled.Value; }
        }

        /// <summary>The height in metres from which a canopy still has room to
        /// open. Read by the helicopter for its hint as well.</summary>
        public static float MinHeight
        {
            get
            {
                return CfgMinHeight == null ? 100f : Mathf.Max(10f, CfgMinHeight.Value);
            }
        }

        /// <summary>Is a parachute in the pack? Turret.HasItem walks the same
        /// three containers the game's own RemoveItem walks and caches for half
        /// a second, which is what the antenna uses too.</summary>
        public static bool Have()
        {
            return Turret.HasItem(ItemId);
        }

        // --------------------------------------------------------------- item

        /// <summary>Appends the parachute to the shared item table. Donor 2030
        /// is the 7.62 box every carried, non-weapon item of this plugin clones:
        /// it puts the item in the pack, gives it a prefab with a collider to
        /// lie on the ground in, and asks nothing of a weapon slot.</summary>
        public static void AddItems(List<ItemDef> items)
        {
            if (!Enabled) return;
            items.Add(new ItemDef(
                ItemId, 2030, false,
                "Парашют Д-6", "Parachute D-6",
                "Десантный парашют в ранце. Сам по себе он ничего не делает - "
                + "он нужен в рюкзаке в тот момент, когда ты прыгаешь из "
                + "вертолёта. С высоты от ста метров купол раскрывается сам, "
                + "и ты спускаешься на землю целым. Один прыжок - один парашют.",
                "A paratrooper's parachute in its pack. It does nothing by "
                + "itself - it has to be in your backpack at the moment you jump "
                + "out of a helicopter. From a hundred metres up the canopy opens "
                + "by itself and sets you down unhurt. One jump, one parachute.",
                "parachute.ndmesh", "parachute_diffuse.png",
                "parachute_normal.png", "parachute_icon.png", null,
                1, 0, 11.0f));
        }

        // --------------------------------------------------------------- jump

        /// <summary>
        /// A man has just left a helicopter at <paramref name="height"/> metres
        /// over the ground. Put a canopy over him if he has earned one.
        ///
        /// Returns true when the canopy is open and the game is flying him down.
        /// False is an ordinary fall - which is a legitimate outcome, not an
        /// error - and <paramref name="say"/> then holds the line telling him
        /// why, ready for the helicopter's own hint.
        /// </summary>
        public static bool Jump(Vector3 at, float height, out string say)
        {
            say = "";
            if (!Enabled) return false;

            if (height < MinHeight)
            {
                say = Text.TooLow(Mathf.RoundToInt(MinHeight));
                return false;
            }
            if (!Have())
            {
                say = Text.NoChute();
                return false;
            }
            if (!Open(at))
            {
                say = Text.Failed();
                return false;
            }
            if (CfgConsume == null || CfgConsume.Value)
                Turret.TakeItem(ItemId, "Parachute");
            say = Text.Open();
            RevivalPlugin.L.LogInfo("Parachute: canopy open at "
                + Mathf.RoundToInt(height) + " m.");
            return true;
        }

        static Type _tMove;
        static MethodInfo _mFly, _mUncover;
        static bool _looked, _warned;

        /// <summary>
        /// The one move. Both calls are the game's own and they belong together:
        /// the state alone is a free fall that ends in 1000 damage, the canopy
        /// alone is a model with nothing under it.
        /// </summary>
        static bool Open(Vector3 at)
        {
            try
            {
                Component man = Controller();
                if (man == null || !LookUp())
                {
                    if (!_warned)
                    {
                        _warned = true;
                        RevivalPlugin.L.LogWarning("Parachute: the game's own "
                            + "parachute state was not found - a jump stays a "
                            + "fall. Looked for PlayerMovementController."
                            + "SetPlayerParashuteFlyState(bool,Vector3) and "
                            + ".PlayerParashuteUncover(bool).");
                    }
                    return false;
                }
                _mFly.Invoke(man, new object[] { true, at });
                _mUncover.Invoke(man, new object[] { true });
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Parachute: " + ex.Message);
                return false;
            }
        }

        static bool LookUp()
        {
            if (_looked) return _mFly != null && _mUncover != null;
            _looked = true;
            _tMove = RevivalPlugin.TypeByName("PlayerMovementController");
            if (_tMove == null) return false;
            _mFly = AccessTools.Method(_tMove, "SetPlayerParashuteFlyState",
                new Type[] { typeof(bool), typeof(Vector3) }, null);
            _mUncover = AccessTools.Method(_tMove, "PlayerParashuteUncover",
                new Type[] { typeof(bool) }, null);
            return _mFly != null && _mUncover != null;
        }

        static Component _man;
        static float _manUntil;

        /// <summary>The LOCAL player's movement controller - the one whose
        /// PhotonView is mine. Cached for two seconds; a respawn hands out a new
        /// object and the cache has to notice.</summary>
        static Component Controller()
        {
            if (_man != null && Time.time < _manUntil) return _man;
            _manUntil = Time.time + 2f;
            _man = null;
            try
            {
                if (_tMove == null)
                    _tMove = RevivalPlugin.TypeByName("PlayerMovementController");
                if (_tMove == null) return null;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_tMove);
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i] as MonoBehaviour;
                    if (mb == null || !IsMine(mb)) continue;
                    _man = mb;
                    break;
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Parachute: local player not found: "
                    + ex.Message);
            }
            return _man;
        }

        static bool IsMine(MonoBehaviour mb)
        {
            MethodInfo get = AccessTools.Method(mb.GetType(), "get_photonView", null, null);
            object view = null;
            try { if (get != null) view = get.Invoke(mb, null); }
            catch { view = null; }
            if (view == null) return true;            // no PhotonView: single player
            MethodInfo isMine = AccessTools.PropertyGetter(view.GetType(), "isMine");
            try { return isMine == null || (bool)isMine.Invoke(view, null); }
            catch { return true; }
        }

        // -------------------------------------------------------------- lines

        /// <summary>Everything the player reads. Russian for a Russian client,
        /// English otherwise.</summary>
        public static class Text
        {
            public static string Open()
            {
                return Loc.T("Купол раскрыт", "Canopy open");
            }

            public static string NoChute()
            {
                return Loc.T("Парашюта в рюкзаке нет",
                             "No parachute in your pack");
            }

            public static string TooLow(int metres)
            {
                return Loc.T("Слишком низко - куполу нужно от " + metres + " м",
                             "Too low - the canopy needs " + metres + " m");
            }

            public static string Failed()
            {
                return Loc.T("Парашют не раскрылся - смотри лог",
                             "The parachute did not open - see the log");
            }
        }
    }
}
