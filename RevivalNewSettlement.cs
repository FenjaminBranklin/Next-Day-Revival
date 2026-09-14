using System;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// Phase 1 of a new traitor settlement at the map's bottom-left corner.
    /// Plan and design notes: docs/ai/tasks/new-settlement-bottom-left.md.
    ///
    /// Spawns a small NATIVE NPC_Settlement through Crew.DropSquad - the same
    /// spawn-point/walk-point-ring machinery the game itself uses and that
    /// Crew.cs already reuses for wreck crews and heli troop drops - so the
    /// men patrol their own ring exactly like every other settlement. Draws a
    /// second, visible ORANGE ring on the in-game map at the settlement's own
    /// CheckPlayersDistRadius, so the location reads before a player walks
    /// there.
    ///
    /// Isolated on purpose: a new file, its own config section, its own
    /// hotkey (F12). RevivalPlugin.cs is touched only for the three one-line
    /// calls every such feature already adds (BindConfig, Tick, Draw); no
    /// other file is changed. Disabled side effects by default: BindConfig
    /// only registers the feature, spawning always needs the explicit key.
    /// </summary>
    public static class NewSettlement
    {
        static ConfigEntry<bool> _cfgEnabled;
        static ConfigEntry<string> _cfgKey;
        static ConfigEntry<int> _cfgCount;
        static ConfigEntry<float> _cfgX;
        static ConfigEntry<float> _cfgZ;
        static ConfigEntry<float> _cfgMapRadius;

        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static GameObject _settlement;
        static Texture2D _dot;

        const string Faction = "traitor";
        const int RingSegments = 40;
        static readonly Color RingColor = new Color(1f, 0.55f, 0f, 0.9f);   // orange

        public static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("NewSettlement", "Enabled", true,
                "Phase 1 bottom-left traitor settlement: a few NPCs on their "
                + "own patrol ring plus an orange map marker. See "
                + "docs/ai/tasks/new-settlement-bottom-left.md. Only ever "
                + "spawns on the Key press below - never automatic.");
            _cfgKey = cfg.Bind("NewSettlement", "Key", "F12",
                "Spawns the settlement once; pressed again, removes it.");
            _cfgCount = cfg.Bind("NewSettlement", "Count", 4,
                "How many NPCs stand this settlement up.");
            _cfgX = cfg.Bind("NewSettlement", "X", -1500f,
                "World X of the settlement centre. World runs -2500..2500; "
                + "this default sits in the map's bottom-left quadrant.");
            _cfgZ = cfg.Bind("NewSettlement", "Z", -1500f,
                "World Z of the settlement centre.");
            _cfgMapRadius = cfg.Bind("NewSettlement", "MapRingRadius", 120f,
                "World-metre radius of the orange map ring (matches the "
                + "settlement's own CheckPlayersDistRadius).");
        }

        public static void Tick()
        {
            if (_cfgEnabled == null || !_cfgEnabled.Value) return;
            try
            {
                if (!Input.GetKeyDown(Key())) return;
                if (MapTools.LocalPlayer() == null) return;
                if (_settlement != null) { Remove(); return; }
                Spawn();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("NewSettlement: " + ex);
            }
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try
            {
                _key = (KeyCode)Enum.Parse(typeof(KeyCode),
                    _cfgKey != null ? _cfgKey.Value : "F12", true);
            }
            catch { _key = KeyCode.F12; }
            return _key;
        }

        static void Spawn()
        {
            if (!RevivalTroopInsertion.MasterClient())
            {
                RevivalPlugin.L.LogInfo("NewSettlement: not master client - "
                    + "the host spawns this settlement.");
                return;
            }

            Vector3 above = new Vector3(_cfgX.Value, 400f, _cfgZ.Value);
            Vector3 ground;
            if (!FindGround(above, out ground))
            {
                RevivalPlugin.L.LogWarning("NewSettlement: no ground found "
                    + "near (" + _cfgX.Value + ", " + _cfgZ.Value + ") - not "
                    + "spawning. Adjust NewSettlement.X/Z in the config and "
                    + "try again.");
                return;
            }

            int count = Mathf.Clamp(_cfgCount.Value, 1, 8);
            GameObject settlement = Crew.DropSquad(ground, 0f, count, Faction, null);
            if (settlement == null)
            {
                RevivalPlugin.L.LogWarning("NewSettlement: DropSquad returned "
                    + "nothing.");
                return;
            }
            _settlement = settlement;
            RevivalPlugin.L.LogInfo("NewSettlement: " + count
                + " traitor(s) settled at " + ground + ".");
            Turret.Hinweis(Loc.T(
                "Новое поселение предателей в юго-западном углу карты.",
                "New traitor settlement in the map's south-west corner."), 4f);
        }

        static void Remove()
        {
            if (_settlement == null) return;
            Crew.Forget(_settlement);
            UnityEngine.Object.Destroy(_settlement);
            _settlement = null;
            RevivalPlugin.L.LogInfo("NewSettlement: removed.");
        }

        /// <summary>A straight raycast first, then a small outward search: the
        /// configured point may sit over water or a cliff edge, and nearby
        /// ground within 300 m is still "the map's bottom-left corner" for
        /// this purpose. Never guesses a height - only what a raycast hits.</summary>
        static bool FindGround(Vector3 above, out Vector3 ground)
        {
            Vector3 g;
            if (Turret.RaycastObject(above, Vector3.down, 800f, out g) != null)
            { ground = g; return true; }
            for (float r = 50f; r <= 300f; r += 50f)
            {
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI * 2f / 8f;
                    Vector3 probe = above + new Vector3(
                        Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (Turret.RaycastObject(probe, Vector3.down, 800f, out g) != null)
                    { ground = g; return true; }
                }
            }
            ground = Vector3.zero;
            return false;
        }

        public static void Draw()
        {
            if (_cfgEnabled == null || !_cfgEnabled.Value) return;
            if (_settlement == null) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            Component manager, texture;
            Camera camera;
            Vector2 world, map;
            if (!MapTools.Context(out manager, out texture, out camera,
                                  out world, out map)) return;

            Rect clip;
            if (!MapTools.MapScreenRect(texture, camera, out clip)) return;
            Rect view;
            if (MapTools.MapViewportRect(texture, camera, out view))
                clip = Intersect(clip, view);

            Color old = GUI.color;
            try
            {
                GUI.color = RingColor;
                Vector3 centre = _settlement.transform.position;
                float radius = _cfgMapRadius.Value;
                for (int i = 0; i < RingSegments; i++)
                {
                    float a = i * Mathf.PI * 2f / RingSegments;
                    Vector3 rim = centre + new Vector3(
                        Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                    Vector2 g;
                    if (!MapTools.WorldToGui(rim, texture, camera, world, map, out g))
                        continue;
                    if (!clip.Contains(g)) continue;
                    GUI.DrawTexture(new Rect(g.x - 1.5f, g.y - 1.5f, 3f, 3f), Dot());
                }
            }
            finally { GUI.color = old; }
        }

        static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.x, b.x), y0 = Mathf.Max(a.y, b.y);
            float x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
            return new Rect(x0, y0, Mathf.Max(0f, x1 - x0), Mathf.Max(0f, y1 - y0));
        }

        static Texture2D Dot()
        {
            if (_dot == null)
            {
                _dot = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _dot.SetPixel(0, 0, Color.white);
                _dot.Apply();
                _dot.hideFlags = HideFlags.HideAndDontSave;
            }
            return _dot;
        }
    }
}
