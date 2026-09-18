// Next Day: Survival - Revival Toolkit
//
// Native action progress bridge. Custom long-running player actions use the
// same HUD and character-state entry points as the base game's item and world
// interactions instead of drawing a second, unrelated IMGUI progress bar.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments or modern syntax.

using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// Owns one native interaction presentation at a time. The gameplay timer
    /// remains in the feature that started the action; this class only starts
    /// and stops the base game's progress HUD and matching player animation.
    /// </summary>
    public static class NativeActionProgress
    {
        static string _owner;
        static float _deadline;
        static GameObject _player;
        static object _hud;
        static MethodInfo _show;
        static MethodInfo _hide;

        static MonoBehaviour _states;
        static Component _interactions;
        static Coroutine _animation;
        static MethodInfo _setGlobal;
        static bool _globalActive;

        // When the native animation routine that is currently playing restores
        // the pose by itself. The game never stops these routines; their own
        // tail puts the character back into an idle state and returns the
        // weapon. Cancelling an action therefore must NOT kill the routine - it
        // only releases the interaction lock and lets the clip play out. See
        // ReleaseAnimation.
        static float _animationEnds;

        static bool _hudWarning;
        static bool _animationWarning;

        /// <summary>
        /// Show the original interaction progress and play one of the original
        /// player interaction animations. Stationary actions prefer a real
        /// CharacterInteractState (repair, gathering, cooking); if this game
        /// build exposes no usable state, the configured use-item animation is used
        /// while the feature's existing movement lock keeps the body still.
        /// Returns false when another action owns the HUD or the requested
        /// native presentation cannot start. Callers must not start their timer
        /// after a failed acquisition.
        /// Pass false/null/null for HUD-only actions such as seated gun reloads.
        /// </summary>
        public static bool Begin(string owner, string label, float seconds,
                                 bool stationary, string interactionHint,
                                 string useItemAnimation)
        {
            if (string.IsNullOrEmpty(owner)) return false;
            // Never replace another action's presentation while its gameplay
            // timer is still running. Callers acquire before starting a timer.
            if (_owner != null && IsActive(_owner)) return false;
            _player = MapTools.LocalPlayer();
            if (_player == null || !_player.activeInHierarchy) return false;
            _owner = owner;
            _deadline = Time.time + Mathf.Max(0.05f, seconds) + 5f;

            if (!Show(label, Mathf.Max(0.05f, seconds)))
            {
                End(owner);
                return false;
            }
            if (!StartAnimation(stationary, interactionHint, useItemAnimation))
            {
                End(owner);
                return false;
            }
            RevivalPlugin.L.LogInfo("Native action progress: begin " + owner
                + ", HUD=" + _hud.GetType().Name + "." + _show.Name
                + ", seconds=" + seconds + ".");
            return true;
        }

        /// <summary>Reject completion after losing the local player or HUD, or
        /// once an action has outlived its own declared duration by five
        /// seconds. Every action here is time-bounded, so an owner still
        /// holding the presentation past that point has lost its own end
        /// condition; releasing it beats leaving the HUD and the interaction
        /// lock up for good.</summary>
        public static bool IsActive(string owner)
        {
            if (_owner == null || _owner != owner) return false;
            UnityEngine.Object hudObject = _hud as UnityEngine.Object;
            if (_player == null || !_player.activeInHierarchy
                || _player != MapTools.LocalPlayer()
                || Time.time > _deadline
                || (_hud is UnityEngine.Object && hudObject == null))
            {
                End(owner);
                return false;
            }
            return true;
        }

        /// <summary>End either a completed or cancelled custom action.</summary>
        public static void End(string owner)
        {
            if (_owner == null || _owner != owner) return;
            StopAnimation();
            Hide();
            ClearHud();
            _player = null;
            _owner = null;
            RevivalPlugin.L.LogInfo("Native action progress: end " + owner + ".");
        }

        static bool Show(string label, float seconds)
        {
            try
            {
                if (!FindHud()) return false;
                _show.Invoke(_hud, new object[] { label, seconds });
                return true;
            }
            catch (Exception ex)
            {
                WarnHud("show", ex);
                ClearHud();
            }
            return false;
        }

        static bool FindHud()
        {
            if (_hud != null && _show != null) return true;

            Type uiType = RevivalPlugin.TypeByName("UIController");
            if (uiType == null)
            {
                WarnHud("lookup", null);
                return false;
            }

            object ui = null;
            MethodInfo get = AccessTools.PropertyGetter(uiType, "Instance");
            if (get != null) ui = get.Invoke(null, null);

            FieldInfo field = AccessTools.Field(uiType, "HUD_InteractingProgress");
            if (ui != null && field != null) _hud = field.GetValue(ui);

            Type hudType = _hud == null
                ? RevivalPlugin.TypeByName("HUD_InteractingProgress")
                : _hud.GetType();
            if (_hud == null && hudType != null)
                _hud = UnityEngine.Object.FindObjectOfType(hudType);
            if (_hud == null)
            {
                WarnHud("instance", null);
                return false;
            }

            hudType = _hud.GetType();
            _show = AccessTools.Method(hudType, "ShowInteractingProgressByTime",
                new Type[] { typeof(string), typeof(float) }, null);
            _hide = ZeroArgumentMethod(hudType, new string[] {
                "HideInteractingProgress", "StopInteractingProgress",
                "HideInteractingProgressByTime", "HideProgress",
                "HideInteractingProgressBar", "CloseInteractingProgress",
                "StopProgress", "HideBar", "Hide"
            });
            if (_show == null)
            {
                WarnHud("method", null);
                ClearHud();
                return false;
            }
            return true;
        }

        static void Hide()
        {
            try
            {
                if (_hud == null || _show == null) return;
                if (_hide != null) _hide.Invoke(_hud, null);
                else _show.Invoke(_hud, new object[] { string.Empty, 0.01f });
            }
            catch (Exception ex)
            {
                WarnHud("hide", ex);
                ClearHud();
            }
        }

        static void ClearHud()
        {
            _hud = null;
            _show = null;
            _hide = null;
        }

        static MethodInfo ZeroArgumentMethod(Type type, string[] names)
        {
            for (int n = 0; n < names.Length; n++)
            {
                MethodInfo method = AccessTools.Method(type, names[n], Type.EmptyTypes, null);
                if (method != null) return method;
            }
            return null;
        }

        static void WarnHud(string stage, Exception ex)
        {
            if (_hudWarning) return;
            _hudWarning = true;
            RevivalPlugin.L.LogWarning("Native action progress unavailable at " + stage
                + (ex == null ? "." : ": " + ex.Message));
        }

        static bool StartAnimation(bool stationary, string interactionHint,
                                   string useItemAnimation)
        {
            try
            {
                // A seated weapon reload needs only the native HUD. It must
                // not change the player's seat animation or interaction state.
                if (!stationary && string.IsNullOrEmpty(useItemAnimation)) return true;
                GameObject player = _player;
                Type statesType = RevivalPlugin.TypeByName("PlayerStatesController");
                if (player == null || statesType == null)
                { WarnAnimation("player states type", null); return false; }
                _states = player.GetComponentInChildren(statesType) as MonoBehaviour;
                if (_states == null)
                { WarnAnimation("player states instance", null); return false; }

                // A routine from a just-cancelled action may still be playing
                // out its clip. Starting a second one on top of it would stack
                // two interaction animations on the same character, and only
                // the last one to finish would restore the pose. Reuse the
                // running one: the interaction lock below is what this action
                // actually needs.
                if (Time.time < _animationEnds)
                {
                    if (!FindInteractions()) return false;
                    if (!SetGlobalInteraction(stationary ? 1 : 2)) return false;
                    RevivalPlugin.L.LogInfo("Native action animation: " + _owner
                        + " -> reused the running interaction clip ("
                        + (_animationEnds - Time.time).ToString("F1") + " s left).");
                    return true;
                }

                IEnumerator routine = null;
                string selected = null;
                float length = 0f;
                if (stationary)
                {
                    string state = StationaryState(_states, interactionHint);
                    MethodInfo interact = AccessTools.Method(statesType,
                        "PlayerInteractingWithItem",
                        new Type[] { typeof(string) }, null);
                    if (interact != null && !string.IsNullOrEmpty(state))
                    {
                        routine = interact.Invoke(_states, new object[] { state }) as IEnumerator;
                        selected = "PlayerInteractingWithItem(" + state + ")";
                        length = AnimationLength(_states, state);
                    }
                }

                if (routine == null && !string.IsNullOrEmpty(useItemAnimation))
                {
                    MethodInfo use = AccessTools.Method(statesType, "PlayerUseItemAnim",
                        new Type[] { typeof(string) }, null);
                    if (use != null)
                    {
                        routine = use.Invoke(_states,
                            new object[] { useItemAnimation }) as IEnumerator;
                        selected = "PlayerUseItemAnim(" + useItemAnimation + ")";
                        length = AnimationLength(_states, useItemAnimation);
                    }
                }
                if (routine == null)
                { WarnAnimation("animation routine", null); return false; }

                if (!RegisterAnimation(routine)) return false;
                if (!SetGlobalInteraction(stationary ? 1 : 2)) return false;
                _animation = _states.StartCoroutine(routine);
                if (_animation == null)
                { WarnAnimation("animation coroutine", null); return false; }
                // getAnimationLenght is the game's own clip length. A build that
                // does not answer still needs a non-zero guard window, otherwise
                // a cancel/restart pair could stack two clips.
                if (length <= 0f) length = 5f;
                _animationEnds = Time.time + length;
                RevivalPlugin.L.LogInfo("Native action animation: " + _owner
                    + " -> " + selected + ", stationary=" + stationary
                    + ", clip=" + length.ToString("F1") + " s.");
                return true;
            }
            catch (Exception ex)
            {
                WarnAnimation("start", ex);
                return false;
            }
        }

        /// <summary>
        /// Pick an actual member of CharacterInteractState. getAnimationLenght
        /// validates names against this build; keyword scoring keeps repair on
        /// repair where available and gear deployment on gathering/cooking.
        /// </summary>
        static string StationaryState(MonoBehaviour states, string hint)
        {
            Type enumType = RevivalPlugin.TypeByName("CharacterInteractState");
            if (enumType == null || !enumType.IsEnum) return null;
            string[] names = Enum.GetNames(enumType);
            string best = null;
            float bestScore = -1f;
            for (int i = 0; i < names.Length; i++)
            {
                string lower = names[i].ToLowerInvariant();
                if (lower == "none" || lower.IndexOf("idle") >= 0) continue;

                // Never select an unrelated state just because its clip is
                // long. Only interaction names relevant to these tasks qualify.
                float score = 0f;
                if (!string.IsNullOrEmpty(hint)
                    && lower.IndexOf(hint.ToLowerInvariant()) >= 0) score += 1000f;
                if (lower.IndexOf("repair") >= 0) score += 150f;
                if (lower.IndexOf("berr") >= 0 || lower.IndexOf("branch") >= 0
                    || lower.IndexOf("gather") >= 0 || lower.IndexOf("collect") >= 0)
                    score += 100f;
                if (lower.IndexOf("cook") >= 0 || lower.IndexOf("meat") >= 0)
                    score += 50f;
                if (score <= 0f) continue;
                float length = AnimationLength(states, names[i]);
                if (length <= 0f) continue;
                score += Mathf.Min(length, 60f);
                if (score > bestScore) { bestScore = score; best = names[i]; }
            }
            return best;
        }

        static float AnimationLength(MonoBehaviour states, string state)
        {
            try
            {
                MethodInfo length = AccessTools.Method(states.GetType(),
                    "getAnimationLenght",
                    new Type[] { typeof(string), typeof(bool) }, null);
                if (length == null) return 0f;
                object raw = length.Invoke(states, new object[] { state, false });
                return raw == null ? 0f : Convert.ToSingle(raw);
            }
            catch { return 0f; }
        }

        /// <summary>Resolve the player's PlayerInteractingManager into
        /// <see cref="_interactions"/>.</summary>
        static bool FindInteractions()
        {
            Type managerType = RevivalPlugin.TypeByName("PlayerInteractingManager");
            if (managerType == null || _states == null)
            { WarnAnimation("interaction manager type", null); return false; }
            _interactions = _states.GetComponentInParent(managerType);
            if (_interactions == null)
                _interactions = _states.transform.root.GetComponentInChildren(managerType);
            if (_interactions == null)
            { WarnAnimation("interaction manager instance", null); return false; }
            return true;
        }

        static bool RegisterAnimation(IEnumerator routine)
        {
            if (!FindInteractions()) return false;
            Type managerType = _interactions.GetType();

            MethodInfo[] methods = managerType.GetMethods(BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "AddGlobalInteractingCoroutine") continue;
                ParameterInfo[] pars = methods[i].GetParameters();
                if (pars.Length != 2) continue;
                if (!pars[0].ParameterType.IsInstanceOfType(routine)
                    || !pars[1].ParameterType.IsInstanceOfType(_states)) continue;
                try
                {
                    methods[i].Invoke(_interactions, new object[] { routine, _states });
                    return true;
                }
                catch (Exception ex) { WarnAnimation("coroutine registration", ex); }
            }
            WarnAnimation("coroutine registration method", null);
            return false;
        }

        /// <summary>
        /// Adapter convention inherited from the initial migration: 1 for a
        /// stationary interaction, 2 for item use, 0 to release. The runtime
        /// acceptance must confirm these values against the shipped game.
        /// Support both integer and enum method parameters.
        /// </summary>
        static bool SetGlobalInteraction(int mode)
        {
            try
            {
                Type managerType = RevivalPlugin.TypeByName("PlayerInteractingManager");
                if (managerType == null)
                { WarnAnimation("global interaction type", null); return false; }
                if (_setGlobal == null)
                {
                    MethodInfo[] methods = managerType.GetMethods(BindingFlags.Static
                        | BindingFlags.Instance | BindingFlags.Public
                        | BindingFlags.NonPublic);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        if (methods[i].Name == "SetGlobalIntercatingState"
                            && methods[i].GetParameters().Length == 2)
                        { _setGlobal = methods[i]; break; }
                    }
                }
                if (_setGlobal == null)
                { WarnAnimation("global interaction method", null); return false; }
                object target = _setGlobal.IsStatic ? null : (object)_interactions;
                if (!_setGlobal.IsStatic && target == null)
                { WarnAnimation("global interaction target", null); return false; }
                ParameterInfo[] pars = _setGlobal.GetParameters();
                object state = NumericValue(pars[0].ParameterType, mode);
                object slot = NumericValue(pars[1].ParameterType, -1);
                _setGlobal.Invoke(target, new object[] { state, slot });
                _globalActive = mode != 0;
                return true;
            }
            catch (Exception ex) { WarnAnimation("interaction state", ex); }
            return false;
        }

        static object NumericValue(Type type, int value)
        {
            if (type.IsEnum) return Enum.ToObject(type, value);
            if (type == typeof(bool)) return value != 0;
            return Convert.ChangeType(value, type);
        }

        /// <summary>
        /// End the native presentation of a completed OR cancelled action.
        ///
        /// The interaction lock is released exactly as the game's own
        /// interaction coroutines release it: SetGlobalIntercatingState(0, -1).
        ///
        /// The player animation routine is deliberately NOT stopped. It is a
        /// native coroutine whose own tail restores the character state, the
        /// pose and the hidden weapon; the game never stops one either.
        /// Killing it mid-clip - which this bridge used to do on every cancel -
        /// skipped that tail, so aborting a drone launch or an antenna deploy
        /// left the player stuck in the interaction animation with no way out.
        /// Letting the clip finish on its own returns the body a moment later,
        /// which is what cancelling a native interaction looks like.
        ///
        /// If this build does expose an explicit native cancel, it is preferred
        /// over waiting for the clip.
        /// </summary>
        static void StopAnimation()
        {
            try { if (_globalActive) SetGlobalInteraction(0); }
            catch { }
            ReleaseAnimation();
            _animation = null;
            _states = null;
            _interactions = null;
            _globalActive = false;
        }

        // An explicit native "stop interacting" entry point, if this build has
        // one. Only ever called while one of our own actions owns the
        // presentation, and our movement hook blocks CantInteractWithItem for
        // that whole time, so no base-game interaction can be cut short by it.
        static readonly string[] StatesRelease = new string[] {
            "StopInteractingWithItem", "StopPlayerInteracting", "StopInteracting",
            "BreakInteracting", "CancelInteracting", "ResetInteractState"
        };
        static readonly string[] ManagerRelease = new string[] {
            "StopGlobalInteractingCoroutine", "StopGlobalInteractingCoroutines",
            "StopAllGlobalInteractingCoroutines", "ClearGlobalInteractingCoroutine",
            "BreakGlobalInteracting"
        };

        static void ReleaseAnimation()
        {
            bool released = false;
            released |= InvokeRelease(_states, StatesRelease);
            released |= InvokeRelease(_interactions, ManagerRelease);
            // Without a native cancel the running clip restores the pose by
            // itself; keep the guard window so a restart does not stack a
            // second clip on top of it.
            if (released) _animationEnds = 0f;
        }

        static bool InvokeRelease(Component target, string[] names)
        {
            if (target == null) return false;
            try
            {
                MethodInfo method = ZeroArgumentMethod(target.GetType(), names);
                if (method == null) return false;
                method.Invoke(target, null);
                RevivalPlugin.L.LogInfo("Native action animation: released via "
                    + target.GetType().Name + "." + method.Name + ".");
                return true;
            }
            catch (Exception ex) { WarnAnimation("release", ex); }
            return false;
        }

        static void WarnAnimation(string stage, Exception ex)
        {
            if (_animationWarning) return;
            _animationWarning = true;
            RevivalPlugin.L.LogWarning("Native action animation unavailable at " + stage
                + (ex == null ? "." : ": " + ex.Message));
        }
    }
}
