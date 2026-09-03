using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// Makes "are we all on the same build" visible, because nothing else did.
    ///
    /// THE PROBLEM THIS SOLVES. Almost everything this plugin draws on a REMOTE
    /// player's screen - a wreck crew's appearance and animation, a patrol
    /// vehicle, the admin panel itself - is supplied by the plugin running on
    /// THAT player's machine, not by the game. The host always looks right,
    /// because a spawn the host owns is dressed locally; a viewer only looks
    /// right if the viewer's plugin runs the same repair code. A crew is the
    /// clearest case: the vanilla remote path leaves the men in the bind pose
    /// (T-pose) with collision off - "T-posed unkillable objects" - and only
    /// Crew.NpcStartPostfix on the viewer restores them (see Revival.Crew.cs).
    ///
    /// So a tester on an old, half-installed, or not-loaded plugin sees broken
    /// visuals while the host sees nothing wrong, and until now there was no
    /// signal anywhere: no version on screen, and no exchange of versions
    /// between clients. A whole test session could be spent before anyone
    /// realised the two machines were not running the same code.
    ///
    /// TWO THINGS, BOTH READ-ONLY TO THE GAME:
    ///
    ///   A version badge         drawn in a corner whenever the plugin is
    ///                           loaded. A tester can read it, or screenshot
    ///                           it, and say "mine says 6.5.1" - the thing you
    ///                           could never ask before.
    ///   A mismatch banner       each client publishes its plugin version into
    ///                           its own Photon player property ("ndrv"); every
    ///                           client reads the others' and, when one differs
    ///                           or is absent, draws a named warning. The host
    ///                           finally SEES "Bob is on 6.0.0" instead of
    ///                           discovering it from broken crew an hour in.
    ///
    /// Photon here is PUN classic (NetWatch reads isMasterClient and
    /// connectionStateDetailed, both lower-cased), so the members are player,
    /// playerList, customProperties, SetCustomProperties. Everything is by
    /// reflection - the plugin links no game assembly - and every step is
    /// wrapped: a naming difference costs the warning, never a crash, and never
    /// the badge, which needs no networking at all.
    /// </summary>
    public static class PeerCheck
    {
        /// <summary>The Photon player property our version is published under.
        /// Short, unique to us, and merged in - SetCustomProperties only writes
        /// the keys it is given and never disturbs the game's own.</summary>
        const string Key = "ndrv";

        internal static ConfigEntry<bool> CfgBadge;
        internal static ConfigEntry<bool> CfgWarn;

        // Resolved once. PhotonNetwork is static; the player type is learned
        // from a live instance the first time we hold one.
        static bool _looked;
        static Type _photon;
        static PropertyInfo _inRoom;
        static PropertyInfo _playerList;
        static PropertyInfo _localPlayer;

        static bool _playerResolved;
        static PropertyInfo _pProps;      // customProperties
        static PropertyInfo _pIsLocal;    // isLocal
        static PropertyInfo _pNick;       // NickName / name
        static PropertyInfo _pId;         // ID
        static MethodInfo _setProps;      // SetCustomProperties(Hashtable)
        static Type _hashType;            // the parameter type of _setProps

        // Recomputed on a slow clock in Tick, read every frame by Draw.
        static float _nextTick;
        static string _ownPublished = "";       // what we last pushed to Photon
        static readonly List<string> _mismatch = new List<string>();

        static GUIStyle _badgeStyle;
        static GUIStyle _titleStyle;
        static GUIStyle _lineStyle;
        static Texture2D _bg;

        public static void BindConfig(ConfigFile config)
        {
            CfgBadge = config.Bind("Diagnostics", "ShowVersionBadge", true,
                "Zeigt die Plugin-Version klein in der Bildschirmecke, sobald das "
                + "Plugin geladen ist. Damit kann ein Mitspieler seine Version "
                + "ablesen oder abfotografieren - der schnellste Weg zu pruefen, "
                + "ob alle denselben Stand fahren.");
            CfgWarn = config.Bind("Diagnostics", "WarnVersionMismatch", true,
                "Vergleicht im Photon-Raum die Plugin-Version jedes Mitspielers und "
                + "warnt sichtbar, wenn einer eine andere oder keine Version meldet. "
                + "Ein solcher Client sieht Crew und Technik kaputt (T-Pose), obwohl "
                + "beim Host alles richtig aussieht.");
        }

        // ------------------------------------------------------------- Tick

        /// <summary>
        /// Every two seconds: publish our own version into our Photon player
        /// property if it is not already there, then read every other player's
        /// and note who does not match. Cheap, and it makes no network traffic
        /// beyond the single property write on join.
        /// </summary>
        public static void Tick()
        {
            if (CfgWarn == null || !CfgWarn.Value) return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextTick) return;
            _nextTick = now + 2f;

            _mismatch.Clear();
            try
            {
                Look();
                if (_photon == null || _inRoom == null) return;
                object inRoom = _inRoom.GetValue(null, null);
                if (!(inRoom is bool) || !(bool)inRoom) { _ownPublished = ""; return; }

                // Publish ours. Read-back is optimistic in PUN, so once it has
                // taken we stop writing; a rejoin clears the property and we
                // publish again on the next tick.
                object local = _localPlayer == null ? null : _localPlayer.GetValue(null, null);
                if (local != null)
                {
                    ResolvePlayer(local);
                    string mine = ReadVersion(local);
                    if (mine != RevivalPlugin.VERSION)
                    {
                        if (Publish(local)) _ownPublished = RevivalPlugin.VERSION;
                    }
                    else _ownPublished = RevivalPlugin.VERSION;
                }

                // Read the others.
                object listObj = _playerList == null ? null : _playerList.GetValue(null, null);
                IEnumerable list = listObj as IEnumerable;
                if (list == null) return;
                foreach (object p in list)
                {
                    if (p == null) continue;
                    if (IsLocal(p)) continue;
                    string ver = ReadVersion(p);
                    if (ver == RevivalPlugin.VERSION) continue;   // match, nothing to say
                    string who = Name(p);
                    if (string.IsNullOrEmpty(ver))
                        _mismatch.Add(who + " - " + Loc.T(
                            "мод не подтверждён (старый или не загружен)",
                            "mod unconfirmed (old or not loaded)"));
                    else
                        _mismatch.Add(who + " - " + ver);
                }
            }
            catch (Exception ex)
            {
                // One line, once in a while - this runs on a slow clock, so it
                // cannot flood the log even if it fails every time.
                RevivalPlugin.L.LogWarning("PeerCheck: " + ex.Message);
                _mismatch.Clear();
            }
        }

        static bool Publish(object player)
        {
            try
            {
                if (_setProps == null || _hashType == null) return false;
                object table = Activator.CreateInstance(_hashType);
                IDictionary dict = table as IDictionary;
                if (dict == null) return false;
                dict[Key] = RevivalPlugin.VERSION;
                _setProps.Invoke(player, new object[] { table });
                RevivalPlugin.L.LogInfo("PeerCheck: published version "
                    + RevivalPlugin.VERSION + " to the room.");
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PeerCheck: could not publish version - "
                    + ex.Message);
                return false;
            }
        }

        static string ReadVersion(object player)
        {
            try
            {
                if (_pProps == null) return "";
                object props = _pProps.GetValue(player, null);
                IDictionary dict = props as IDictionary;
                if (dict == null) return "";
                object v = dict[Key];
                return v == null ? "" : v.ToString();
            }
            catch { return ""; }
        }

        static bool IsLocal(object player)
        {
            try
            {
                if (_pIsLocal == null) return false;
                object v = _pIsLocal.GetValue(player, null);
                return v is bool && (bool)v;
            }
            catch { return false; }
        }

        static string Name(object player)
        {
            try
            {
                if (_pNick != null)
                {
                    object v = _pNick.GetValue(player, null);
                    string s = v == null ? "" : v.ToString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                if (_pId != null)
                {
                    object v = _pId.GetValue(player, null);
                    if (v != null) return Loc.T("игрок ", "player ") + v;
                }
            }
            catch { }
            return Loc.T("игрок", "a player");
        }

        static void Look()
        {
            if (_looked) return;
            _looked = true;
            _photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (_photon == null)
            {
                RevivalPlugin.L.LogWarning("PeerCheck: PhotonNetwork not found - "
                    + "the version badge still shows, but peers are not compared.");
                return;
            }
            _inRoom = AccessTools.Property(_photon, "inRoom");
            _playerList = AccessTools.Property(_photon, "playerList");
            if (_playerList == null) _playerList = AccessTools.Property(_photon, "PlayerList");
            _localPlayer = AccessTools.Property(_photon, "player");
            if (_localPlayer == null) _localPlayer = AccessTools.Property(_photon, "LocalPlayer");
        }

        /// <summary>Learn the PhotonPlayer members from a live instance, once.
        /// The parameter type of SetCustomProperties is the ExitGames Hashtable
        /// we must hand it, taken straight from the method so no type name has
        /// to be guessed.</summary>
        static void ResolvePlayer(object player)
        {
            if (_playerResolved || player == null) return;
            _playerResolved = true;
            Type t = player.GetType();
            _pProps = AccessTools.Property(t, "customProperties");
            if (_pProps == null) _pProps = AccessTools.Property(t, "CustomProperties");
            _pIsLocal = AccessTools.Property(t, "isLocal");
            if (_pIsLocal == null) _pIsLocal = AccessTools.Property(t, "IsLocal");
            _pNick = AccessTools.Property(t, "NickName");
            if (_pNick == null) _pNick = AccessTools.Property(t, "name");
            _pId = AccessTools.Property(t, "ID");

            _setProps = FindSetProps(t);
            if (_setProps != null)
            {
                ParameterInfo[] ps = _setProps.GetParameters();
                if (ps.Length >= 1) _hashType = ps[0].ParameterType;
            }
            if (_pProps == null || _setProps == null || _hashType == null)
                RevivalPlugin.L.LogWarning("PeerCheck: PhotonPlayer shape not fully "
                    + "resolved - version exchange may be limited.");
        }

        static MethodInfo FindSetProps(Type t)
        {
            while (t != null)
            {
                MethodInfo[] ms = t.GetMethods(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (MethodInfo m in ms)
                    if (m.Name == "SetCustomProperties" && m.GetParameters().Length == 1)
                        return m;
                t = t.BaseType;
            }
            return null;
        }

        // ------------------------------------------------------------- Draw

        public static void Draw()
        {
            try
            {
                if (CfgBadge != null && CfgBadge.Value) DrawBadge();
                if (CfgWarn != null && CfgWarn.Value && _mismatch.Count > 0) DrawBanner();
            }
            catch { }
        }

        static void DrawBadge()
        {
            // Copied from the skin label so it inherits a working font. This
            // module never touches TextAnchor or FontStyle: both live in
            // UnityEngine.TextRenderingModule, which the plugin does not
            // reference, so alignment is done by hand and weight by size.
            if (_badgeStyle == null)
            {
                _badgeStyle = new GUIStyle(GUI.skin.label);
                _badgeStyle.fontSize = 11;
            }
            string text = "Revival " + RevivalPlugin.VERSION;
            Rect r = new Rect(6f, 3f, 260f, 18f);
            // Drawn twice so it reads on any background: a black shadow, then
            // the label a pixel up-left. No box, so it stays out of the way.
            _badgeStyle.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _badgeStyle);
            _badgeStyle.normal.textColor = new Color(0.85f, 0.9f, 0.85f, 0.9f);
            GUI.Label(r, text, _badgeStyle);
        }

        static void DrawBanner()
        {
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0.5f, 0.05f, 0.05f, 0.92f));
                _bg.Apply();
            }
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label);
                _titleStyle.fontSize = 15;
                _titleStyle.normal.textColor = Color.white;
            }
            if (_lineStyle == null)
            {
                _lineStyle = new GUIStyle(GUI.skin.label);
                _lineStyle.fontSize = 12;
                _lineStyle.normal.textColor = new Color(1f, 0.92f, 0.85f, 1f);
                _lineStyle.wordWrap = true;
            }

            string title = Loc.T(
                "РАЗНЫЕ ВЕРСИИ МОДА",
                "MOD VERSION MISMATCH");
            string hint = Loc.T(
                "У них ломается крю и техника (T-поза). Обновите через лаунчер.",
                "They see broken crew and vehicles (T-pose). Update them via the launcher.");
            string you = Loc.T("Ты: ", "You: ") + RevivalPlugin.VERSION;

            float w = Mathf.Min(Screen.width - 20f, 760f);
            float x = (Screen.width - w) * 0.5f;
            float lineH = 17f;
            // title + "you" + one line per mismatched player + hint, plus padding
            float h = 30f + lineH * (_mismatch.Count + 2) + 12f;
            float y = 34f;

            GUI.DrawTexture(new Rect(x, y, w, h), _bg);

            float cy = y + 8f;
            Centered(title, _titleStyle, x, cy, w, 22f);
            cy += 24f;
            Centered(you, _lineStyle, x, cy, w, lineH);
            cy += lineH;
            foreach (string m in _mismatch)
            {
                Centered("- " + m, _lineStyle, x, cy, w, lineH);
                cy += lineH;
            }
            cy += 4f;
            // The hint may wrap on a narrow screen, so it keeps a left margin
            // instead of being centre-measured as one line.
            GUI.Label(new Rect(x + 12f, cy, w - 24f, lineH * 2f), hint, _lineStyle);
        }

        /// <summary>Draws one line horizontally centred in the given band,
        /// measuring its own width - the stand-in for TextAnchor.MiddleCenter,
        /// which is in an assembly this plugin does not reference.</summary>
        static void Centered(string text, GUIStyle style, float x, float y,
                             float w, float lineH)
        {
            Vector2 size = style.CalcSize(new GUIContent(text));
            float tx = x + Mathf.Max(0f, (w - size.x) * 0.5f);
            GUI.Label(new Rect(tx, y, Mathf.Min(size.x, w), lineH), text, style);
        }
    }
}
