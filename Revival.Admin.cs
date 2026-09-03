using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{

    // ----------------------------------------------------------- Testflaeche

    /// <summary>
    /// Legt zur Laufzeit eine kleine ebene Testflaeche vor den Spieler und
    /// setzt ihn darauf. Gedacht als Ort, an dem sich etwas ausprobieren
    /// laesst, ohne die Welt anzufassen.
    ///
    /// WARUM DAS KEINE ECHTE REGION IST - und keine sein kann
    /// ------------------------------------------------------
    /// Eine Region des Spiels ist ein GameRegionData mit den Feldern region,
    /// startScene und scenes; die Szenen sind Buildindizes. Eine neue Region
    /// braucht also entweder
    ///
    ///   a) eine neue Szene im Build - die laesst sich ohne Neubau des Spiels
    ///      nicht anlegen, oder
    ///   b) ein zurueckgeschriebenes resources.assets - das ist in
    ///      docs/ai/TASKS.md unter NEXT als offene Voraussetzung vermerkt und
    ///      bis heute UNKNOWN.
    ///
    /// Was ohne beides geht, ist genau das hier: Geometrie zur Laufzeit, in
    /// der bereits geladenen Szene. Der Szenensprung in die zehn ungenutzten
    /// Buildszenen (Research.Jump, Bunker_A65, GW_Scene_2, Underground_Lab)
    /// ist der andere Weg zu "neuem" Gelaende und schon vorhanden.
    ///
    /// WARUM HIER KEINE GEGNER STEHEN
    /// ------------------------------
    /// Belegt aus NPC_Settlement::InitSpawnNpc: das Spiel erzeugt einen NPC
    /// mit PhotonNetwork.InstantiateSceneObject unter dem Pfad
    /// "NPCSpawn\Marauder_NPC_01" und ruft danach der Reihe nach
    /// SetCustomization, SetMaxHealth, ResetHealth, SetBehaviorPattern,
    /// CalculateMaxEnemiesCount, SetGodMode, SetIsSafeSettlement,
    /// SetMainWeaponId, NPC_SpawnPoint::Init, NPC_AI2::InitSpawnPoint und
    /// SetSpawnData. Die Daten dafuer kommen aus einer Customization-Datenbank
    /// und einer Waffentabelle.
    ///
    /// Diese Kette halb nachzubauen ergibt Gegner ohne Waffe, ohne Leben und
    /// ohne Verhalten - und InstantiateSceneObject setzt ausserdem voraus,
    /// dass man Masterclient ist. Dazu kommt: NPC_AI2 haelt einen
    /// NavMeshAgent, und ein Navigationsnetz laesst sich zur Laufzeit nicht
    /// backen. Deshalb liegt die Flaeche bewusst nur wenige Zentimeter ueber
    /// dem Boden - dann traegt das vorhandene Navigationsnetz darunter noch.
    ///
    /// Der vollstaendige Bauplan steht in
    /// docs/ai/tasks/testregion-arena.md. Ausprobiert wird er in einem Zug,
    /// wenn das Spiel ohnehin laeuft.
    /// </summary>
    public static class Arena
    {
        const string RootName = "NDR_TestArena";
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static GameObject _arena;

        public static void Tick()
        {
            if (!RevivalPlugin.CfgArena.Value) return;
            try
            {
                if (!Input.GetKeyDown(Key())) return;
                if (_arena != null)
                {
                    UnityEngine.Object.Destroy(_arena);
                    _arena = null;
                    RevivalPlugin.L.LogInfo("Testflaeche entfernt.");
                    return;
                }
                Build();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Testflaeche: " + ex);
            }
        }

        static void Build()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                RevivalPlugin.L.LogWarning("Testflaeche: keine Kamera gefunden.");
                return;
            }

            Vector3 eye = cam.transform.position;
            Vector3 ahead = cam.transform.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.000001f) ahead = Vector3.forward;
            ahead.Normalize();

            float distance = RevivalPlugin.CfgArenaDistance.Value;
            Vector3 above = eye + ahead * distance + Vector3.up * 60f;

            Vector3 ground;
            GameObject under = Turret.RaycastObject(above, Vector3.down, 400f, out ground);
            if (under == null)
            {
                RevivalPlugin.L.LogWarning("Testflaeche: unter " + above
                    + " ist kein Boden - naeher an festen Grund stellen.");
                return;
            }

            string quelle;
            Material material = GroundMaterial(under, out quelle);
            if (material == null)
            {
                // Lieber nichts bauen als etwas Magentafarbenes hinstellen: ein
                // Renderer ohne Material sieht im Spiel nach kaputtem Modell aus
                // und schickt die Fehlersuche in die falsche Richtung.
                RevivalPlugin.L.LogError("Testflaeche: kein brauchbares Material "
                    + "gefunden, die Flaeche wird nicht gebaut. Ohne Material "
                    + "zeichnet Unity sie magenta.");
                return;
            }

            float size = Mathf.Max(8f, RevivalPlugin.CfgArenaSize.Value);

            _arena = new GameObject(RootName);
            _arena.transform.position = ground + Vector3.up * 0.06f;

            GameObject floor = new GameObject("Flaeche");
            floor.transform.SetParent(_arena.transform, false);
            MeshFilter mf = floor.AddComponent<MeshFilter>();
            mf.sharedMesh = Grid(size, 16);
            MeshRenderer mr = floor.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;

            Posts(size, material);

            RevivalPlugin.L.LogInfo("Testflaeche gebaut: " + size + " x " + size
                + " Einheiten bei " + _arena.transform.position
                + ", Material \"" + material.name + "\" aus Quelle: " + quelle + ".");
            RevivalPlugin.L.LogInfo("Testflaeche: Gegner stehen hier absichtlich "
                + "keine - Begruendung in docs/ai/tasks/testregion-arena.md.");
        }

        /// <summary>
        /// Besorgt ein Material, das garantiert zeichnet.
        ///
        /// Der erste Anlauf am 2026-08-28 nahm nur den Renderer des getroffenen
        /// Bodens. In der Overworld steht der Spieler aber auf Unity-Terrain,
        /// und Terrain zeichnet ueber die Komponente `Terrain`, nicht ueber
        /// einen `MeshRenderer` - der Griff ging ins Leere, das Material blieb
        /// null, und Unity malt einen Renderer ohne Material magenta. Genau die
        /// pinke Flaeche, die im Spiel zu sehen war.
        ///
        /// Deshalb jetzt vier Quellen der Reihe nach. Die letzte traegt immer,
        /// solange das Spiel ueberhaupt etwas zeichnet.
        /// </summary>
        static Material GroundMaterial(GameObject under, out string quelle)
        {
            quelle = "keine";

            string terrainQuelle;
            Material vorlage = TerrainMaterial(out terrainQuelle);
            if (vorlage != null) quelle = terrainQuelle;

            if (vorlage == null)
            {
                Renderer r = under.GetComponent<Renderer>();
                if (r == null) r = under.GetComponentInParent<Renderer>();
                if (r != null && r.sharedMaterial != null)
                {
                    vorlage = r.sharedMaterial;
                    quelle = "Boden unter dem Spieler";
                }
            }

            if (vorlage == null)
            {
                Renderer r = NearbyRenderer(under.transform.position);
                if (r != null)
                {
                    vorlage = r.sharedMaterial;
                    quelle = "naechster Renderer: " + r.gameObject.name;
                }
            }

            if (vorlage != null)
            {
                Material copy = new Material(vorlage);
                copy.name = "NDR_ArenaGround";
                return copy;
            }

            // Letzter Ausweg: eigenes Material auf einem Shader, den das Spiel
            // selbst benutzt. Dieselbe Kette wie in ItemFactory.MakeMaterial.
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) shader = Shader.Find("Diffuse");
            if (shader == null) return null;

            Material eigen = new Material(shader);
            eigen.name = "NDR_ArenaGround";
            if (eigen.HasProperty("_Color"))
                eigen.color = new Color(0.42f, 0.40f, 0.36f);
            quelle = "eigener Shader " + shader.name;
            return eigen;
        }

        /// <summary>
        /// Terrain.activeTerrain.materialTemplate ueber Reflexion.
        ///
        /// `Terrain` liegt in UnityEngine.TerrainModule, und build.ps1
        /// referenziert die Assembly nicht. Ueber AccessTools zu gehen ist
        /// derselbe Weg, den das Plugin fuer alle Spieltypen nimmt, und spart
        /// einen Verweis, der auf einem anderen Rechner fehlen koennte.
        ///
        /// `materialTemplate` ist im Spiel LEER - gemessen am 2026-08-28: das
        /// Gelaende benutzt das eingebaute Standardmaterial, und das gibt Unity
        /// nur bei materialType == Custom heraus. Deshalb zweiter Griff auf die
        /// **Splat-Textur** des Gelaendes: das ist die Textur, die man beim
        /// Spielen tatsaechlich unter den Fuessen sieht.
        ///
        /// Ohne diesen zweiten Griff fiel die Flaeche auf "naechster Renderer"
        /// zurueck und trug die Rinde von "dead_trunk_01_LOD0" - nicht mehr
        /// magenta, aber Boden aus Baumstamm.
        /// </summary>
        static Material TerrainMaterial(out string quelle)
        {
            quelle = null;
            try
            {
                Type t = RevivalPlugin.TypeByName("UnityEngine.Terrain");
                if (t == null) return null;
                MethodInfo aktiv = AccessTools.PropertyGetter(t, "activeTerrain");
                if (aktiv == null) return null;
                object terrain = aktiv.Invoke(null, null);
                if (terrain == null) return null;

                MethodInfo mat = AccessTools.PropertyGetter(t, "materialTemplate");
                if (mat != null)
                {
                    Material vorlage = mat.Invoke(terrain, null) as Material;
                    if (vorlage != null)
                    {
                        quelle = "Terrain.materialTemplate";
                        return vorlage;
                    }
                }

                Material ausSplat = SplatMaterial(t, terrain);
                if (ausSplat != null)
                {
                    quelle = "Terrain-Splattextur";
                    return ausSplat;
                }
                return null;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Testflaeche: Terrain-Material nicht "
                    + "lesbar (" + ex.Message + ")");
                return null;
            }
        }

        /// <summary>
        /// Erste Splat-Textur des Gelaendes auf einem Standard-Shader.
        ///
        /// Unity 2018.1 fuehrt sie als `TerrainData.splatPrototypes`, ein Feld
        /// von `SplatPrototype` mit `texture` und `tileSize`. Ab 2018.3 heisst
        /// dasselbe `terrainLayers`/`diffuseTexture` - beide Namen werden
        /// probiert, damit ein Spielupdate das hier nicht stumm ausknipst.
        /// </summary>
        static Material SplatMaterial(Type terrainTyp, object terrain)
        {
            MethodInfo daten = AccessTools.PropertyGetter(terrainTyp, "terrainData");
            if (daten == null) return null;
            object td = daten.Invoke(terrain, null);
            if (td == null) return null;

            object[] schichten = null;
            string[] namen = new string[] { "splatPrototypes", "terrainLayers" };
            for (int i = 0; i < namen.Length && schichten == null; i++)
            {
                MethodInfo g = AccessTools.PropertyGetter(td.GetType(), namen[i]);
                if (g == null) continue;
                schichten = g.Invoke(td, null) as object[];
            }
            if (schichten == null || schichten.Length == 0) return null;

            Texture textur = null;
            for (int i = 0; i < schichten.Length && textur == null; i++)
            {
                if (schichten[i] == null) continue;
                string[] felder = new string[] { "texture", "diffuseTexture" };
                for (int k = 0; k < felder.Length && textur == null; k++)
                {
                    MethodInfo g = AccessTools.PropertyGetter(schichten[i].GetType(), felder[k]);
                    if (g == null) continue;
                    textur = g.Invoke(schichten[i], null) as Texture;
                }
            }
            if (textur == null) return null;

            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) return null;

            Material m = new Material(shader);
            m.mainTexture = textur;
            return m;
        }

        /// <summary>Naechstgelegener Renderer mit brauchbarem Material.</summary>
        static Renderer NearbyRenderer(Vector3 nahe)
        {
            MeshRenderer[] alle = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
            Renderer beste = null;
            float abstand = float.MaxValue;
            for (int i = 0; i < alle.Length; i++)
            {
                MeshRenderer r = alle[i];
                if (r == null || !r.enabled) continue;
                if (r.sharedMaterial == null || r.sharedMaterial.shader == null) continue;
                float d = (r.transform.position - nahe).sqrMagnitude;
                if (d < abstand) { abstand = d; beste = r; }
            }
            return beste;
        }

        /// <summary>Ebenes Gitter, damit die Beleuchtung nicht auf zwei Dreiecke faellt.</summary>
        static Mesh Grid(float size, int cells)
        {
            int line = cells + 1;
            Vector3[] verts = new Vector3[line * line];
            Vector2[] uvs = new Vector2[line * line];
            Vector3[] normals = new Vector3[line * line];
            float half = size * 0.5f;
            float step = size / cells;

            for (int z = 0; z < line; z++)
            {
                for (int x = 0; x < line; x++)
                {
                    int i = z * line + x;
                    verts[i] = new Vector3(-half + x * step, 0f, -half + z * step);
                    // Eine Kachel je zwei Meter, damit die Bodentextur nicht
                    // ueber die ganze Flaeche gezogen wird.
                    uvs[i] = new Vector2(verts[i].x * 0.5f, verts[i].z * 0.5f);
                    normals[i] = Vector3.up;
                }
            }

            int[] tris = new int[cells * cells * 6];
            int t = 0;
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int i = z * line + x;
                    tris[t++] = i;
                    tris[t++] = i + line;
                    tris[t++] = i + line + 1;
                    tris[t++] = i;
                    tris[t++] = i + line + 1;
                    tris[t++] = i + 1;
                }
            }

            Mesh mesh = new Mesh();
            mesh.name = "NDR_ArenaFloor";
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Vier Ecken markieren, sonst findet man die Flaeche im Gelaende
        /// nicht wieder. Bewusst Wuerfel aus demselben Mesh statt
        /// GameObject.CreatePrimitive - das haengt einen Collider an, und der
        /// wuerde dem Navigationsnetz darunter im Weg stehen.
        /// </summary>
        static void Posts(float size, Material boden)
        {
            float half = size * 0.5f;
            Mesh post = Grid(1.2f, 1);

            // Eigenes Material, sonst faerbt das Abdunkeln auch die Flaeche -
            // und vor allem: ein MeshRenderer ohne Material ist magenta. Der
            // erste Anlauf hatte hier gar keins gesetzt.
            Material mark = new Material(boden);
            mark.name = "NDR_ArenaMarker";
            if (mark.HasProperty("_Color"))
                mark.color = mark.color * 0.45f;

            for (int i = 0; i < 4; i++)
            {
                float sx = (i == 0 || i == 3) ? -1f : 1f;
                float sz = (i < 2) ? -1f : 1f;
                GameObject marker = new GameObject("Ecke" + i);
                marker.transform.SetParent(_arena.transform, false);
                marker.transform.localPosition =
                    new Vector3(sx * half, 1.4f, sz * half);
                MeshFilter mf = marker.AddComponent<MeshFilter>();
                mf.sharedMesh = post;
                MeshRenderer mr = marker.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mark;
            }
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try
            {
                _key = (KeyCode)Enum.Parse(typeof(KeyCode),
                                           RevivalPlugin.CfgArenaKey.Value, true);
            }
            catch
            {
                _key = KeyCode.F10;
                RevivalPlugin.L.LogWarning("Testflaeche: ArenaKey "
                    + RevivalPlugin.CfgArenaKey.Value + " unbekannt, benutze F10.");
            }
            return _key;
        }
    }

    /// <summary>
    /// Small reflection bridge to the game's NGUI map. The map already owns
    /// the authoritative world size, texture size and UI camera; using those
    /// values keeps route lines and clicks correct while the map is zoomed or
    /// moved and avoids a second, guessed coordinate system.
    /// </summary>
    public static class MapTools
    {
        public static bool Context(out Component manager, out Component texture,
                                   out Camera uiCamera, out Vector2 worldSize,
                                   out Vector2 mapSize)
        {
            manager = null;
            texture = null;
            uiCamera = null;
            worldSize = Vector2.zero;
            mapSize = Vector2.zero;
            try
            {
                Type mapType = RevivalPlugin.TypeByName("MapUIManager");
                if (mapType == null) return false;
                MethodInfo get = AccessTools.PropertyGetter(mapType, "Inst");
                object raw = get == null ? null : get.Invoke(null, null);
                if (raw == null)
                {
                    FieldInfo instance = AccessTools.Field(mapType, "_instance");
                    if (instance != null) raw = instance.GetValue(null);
                }
                manager = raw as Component;
                if (manager == null || manager.gameObject == null
                    || !manager.gameObject.activeInHierarchy) return false;

                FieldInfo panelField = AccessTools.Field(mapType, "MapPanel");
                object panelRaw = panelField == null ? null : panelField.GetValue(manager);
                Component panel = panelRaw as Component;
                GameObject panelGo = panelRaw as GameObject;
                if (panel != null && !panel.gameObject.activeInHierarchy) return false;
                if (panelGo != null && !panelGo.activeInHierarchy) return false;

                FieldInfo textureField = AccessTools.Field(mapType, "MapTextureUI");
                texture = textureField == null ? null
                    : textureField.GetValue(manager) as Component;
                if (texture == null || !texture.gameObject.activeInHierarchy) return false;

                FieldInfo world = AccessTools.Field(mapType, "WORLD_SIZE");
                FieldInfo map = AccessTools.Field(mapType, "MAP_SIZE");
                if (world == null || map == null) return false;
                worldSize = (Vector2)world.GetValue(null);
                mapSize = (Vector2)map.GetValue(null);
                if (worldSize.x <= 0f || worldSize.y <= 0f
                    || mapSize.x <= 0f || mapSize.y <= 0f) return false;

                Type uiType = RevivalPlugin.TypeByName("UIController");
                if (uiType != null)
                {
                    MethodInfo uiGet = AccessTools.PropertyGetter(uiType, "Instance");
                    object ui = uiGet == null ? null : uiGet.Invoke(null, null);
                    FieldInfo general = AccessTools.Field(uiType, "_UI_General");
                    if (ui == null || general == null
                        || Convert.ToInt32(general.GetValue(ui)) != 8) return false;
                    FieldInfo cam = AccessTools.Field(uiType, "UICam");
                    if (ui != null && cam != null) uiCamera = cam.GetValue(ui) as Camera;
                }
                return uiCamera != null;
            }
            catch { return false; }
        }

        public static bool WorldToGui(Vector3 point, out Vector2 gui)
        {
            gui = Vector2.zero;
            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            if (!Context(out manager, out texture, out cam, out world, out map))
                return false;
            return WorldToGui(point, texture, cam, world, map, out gui);
        }

        public static bool WorldToGui(Vector3 point, Component texture, Camera cam,
                                      Vector2 world, Vector2 map, out Vector2 gui)
        {
            gui = Vector2.zero;
            if (texture == null || cam == null || world.x <= 0f || world.y <= 0f)
                return false;
            Vector3 local = new Vector3(point.x / world.x * map.x,
                                        point.z / world.y * map.y, 0f);
            Vector3 screen = cam.WorldToScreenPoint(texture.transform.TransformPoint(local));
            if (screen.z < 0f) return false;
            gui = new Vector2(screen.x, Screen.height - screen.y);
            return true;
        }

        /// <summary>
        /// The map texture's rectangle in GUI coordinates. Everything drawn
        /// over the map must be clipped to this - a route waypoint north of the
        /// visible map still projects to a screen point, and without this rect
        /// its dashes would be painted over the 3D scene above the map panel
        /// (the "civ" line leaking into the sky). Built from the same NGUI
        /// widget bounds that MouseWorld uses, so it is exactly the drawn map.
        /// </summary>
        public static bool MapScreenRect(Component texture, Camera cam,
                                         out Rect rect)
        {
            rect = new Rect();
            try
            {
                if (texture == null || cam == null) return false;
                Type math = RevivalPlugin.TypeByName("NGUIMath");
                MethodInfo boundsMethod = math == null ? null
                    : AccessTools.Method(math, "CalculateAbsoluteWidgetBounds",
                        new Type[] { typeof(Transform) }, null);
                if (boundsMethod == null) return false;
                Bounds b = (Bounds)boundsMethod.Invoke(null,
                    new object[] { texture.transform });
                if (b.size == Vector3.zero) return false;
                Vector3 s0 = cam.WorldToScreenPoint(b.min);
                Vector3 s1 = cam.WorldToScreenPoint(b.max);
                float x0 = Mathf.Min(s0.x, s1.x);
                float x1 = Mathf.Max(s0.x, s1.x);
                float g0 = Screen.height - s0.y;
                float g1 = Screen.height - s1.y;
                float y0 = Mathf.Min(g0, g1);
                float y1 = Mathf.Max(g0, g1);
                rect = new Rect(x0, y0, x1 - x0, y1 - y0);
                return rect.width > 1f && rect.height > 1f;
            }
            catch { return false; }
        }

        /// <summary>
        /// The VISIBLE map window in GUI coordinates: the clip region of the
        /// nearest enclosing NGUI UIPanel (a UIScrollView) that actually clips.
        /// The map texture scrolls inside this panel, so when it is panned the
        /// texture's own bounds (MapScreenRect) run far past the window; this is
        /// the window itself. Read from UIPanel.finalClipRegion (centre x,y plus
        /// width,height in the panel's LOCAL space) and converted to screen via
        /// the panel transform and the UI camera. Returns false when there is no
        /// clipping panel, so the caller keeps the texture rect as before.
        /// </summary>
        public static bool MapViewportRect(Component texture, Camera cam,
                                           out Rect rect)
        {
            rect = new Rect();
            try
            {
                if (texture == null || cam == null) return false;
                Type panelType = RevivalPlugin.TypeByName("UIPanel");
                if (panelType == null) return false;
                FieldInfo clipField = AccessTools.Field(panelType, "mClipping");
                MethodInfo getRegion = AccessTools.PropertyGetter(panelType, "finalClipRegion");
                if (getRegion == null) return false;

                Component panel = null;
                for (Transform t = texture.transform; t != null; t = t.parent)
                {
                    Component p = t.GetComponent(panelType) as Component;
                    if (p == null) continue;
                    int mode = clipField == null ? 1
                        : Convert.ToInt32(clipField.GetValue(p));
                    if (mode != 0) { panel = p; break; }   // 0 = None
                }
                if (panel == null) return false;

                Vector4 r = (Vector4)getRegion.Invoke(panel, null);
                float hw = r.z * 0.5f, hh = r.w * 0.5f;
                if (hw <= 0f || hh <= 0f) return false;
                Transform pt = panel.transform;
                Vector3 c0 = pt.TransformPoint(new Vector3(r.x - hw, r.y - hh, 0f));
                Vector3 c1 = pt.TransformPoint(new Vector3(r.x + hw, r.y + hh, 0f));
                Vector3 s0 = cam.WorldToScreenPoint(c0);
                Vector3 s1 = cam.WorldToScreenPoint(c1);
                float x0 = Mathf.Min(s0.x, s1.x);
                float x1 = Mathf.Max(s0.x, s1.x);
                float g0 = Screen.height - s0.y;
                float g1 = Screen.height - s1.y;
                float y0 = Mathf.Min(g0, g1);
                float y1 = Mathf.Max(g0, g1);
                rect = new Rect(x0, y0, x1 - x0, y1 - y0);
                return rect.width > 1f && rect.height > 1f;
            }
            catch { return false; }
        }

        /// <summary>The same click conversion used by
        /// MapUIManager.PlacePlayerCustomMarker, including its terrain ray.</summary>
        public static bool MouseWorld(out Vector3 point)
        {
            point = Vector3.zero;
            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            if (!Context(out manager, out texture, out cam, out world, out map))
                return false;
            try
            {
                Type math = RevivalPlugin.TypeByName("NGUIMath");
                MethodInfo boundsMethod = math == null ? null
                    : AccessTools.Method(math, "CalculateAbsoluteWidgetBounds",
                        new Type[] { typeof(Transform) }, null);
                if (boundsMethod == null) return false;
                Bounds bounds = (Bounds)boundsMethod.Invoke(null,
                    new object[] { texture.transform });
                if (bounds.size == Vector3.zero) return false;

                Vector3 mouse = Input.mousePosition;
                mouse.z = 10f;
                Vector3 inUi = cam.ScreenToWorldPoint(mouse);
                float nx = (inUi.x - bounds.min.x) / bounds.size.x;
                float ny = (inUi.y - bounds.min.y) / bounds.size.y;
                if (nx < 0f || nx > 1f || ny < 0f || ny > 1f) return false;

                Vector3 above = new Vector3((nx - 0.5f) * world.x, 1000f,
                                            (ny - 0.5f) * world.y);
                Vector3 hit;
                if (Turret.RaycastObject(above, Vector3.down, 2500f, out hit) == null)
                    return false;
                point = hit + Vector3.up * 1f;
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Map click conversion: " + ex.Message);
                return false;
            }
        }

        public static GameObject LocalPlayer()
        {
            try
            {
                Type ngsType = RevivalPlugin.TypeByName("NetworkGameServer");
                if (ngsType != null)
                {
                    MethodInfo get = AccessTools.PropertyGetter(ngsType, "Instance");
                    object ngs = get == null ? null : get.Invoke(null, null);
                    FieldInfo local = AccessTools.Field(ngsType, "localPlayer");
                    GameObject go = ngs == null || local == null ? null
                        : local.GetValue(ngs) as GameObject;
                    if (go != null) return go;
                }

                Type movement = RevivalPlugin.TypeByName("PlayerMovementController");
                if (movement == null) return null;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(movement);
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i] as MonoBehaviour;
                    if (mb != null && IsMine(mb)) return mb.gameObject;
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Local player lookup: " + ex.Message);
            }
            return null;
        }

        static bool IsMine(MonoBehaviour mb)
        {
            MethodInfo get = AccessTools.Method(mb.GetType(), "get_photonView", null, null);
            object view = get == null ? null : get.Invoke(mb, null);
            if (view == null) return true;
            MethodInfo mine = AccessTools.PropertyGetter(view.GetType(), "isMine");
            return mine == null || (bool)mine.Invoke(view, null);
        }

        public static bool TeleportLocal(Vector3 point, out string message)
        {
            GameObject player = LocalPlayer();
            if (player == null)
            {
                message = Loc.T("локальный игрок не найден", "local player not found");
                return false;
            }
            try
            {
                Type controllerType = RevivalPlugin.TypeByName("CharacterController");
                Component[] controllers = controllerType == null
                    ? new Component[0]
                    : player.GetComponentsInChildren(controllerType, true);
                bool[] enabled = new bool[controllers.Length];
                for (int i = 0; i < controllers.Length; i++)
                {
                    PropertyInfo property = AccessTools.Property(
                        controllers[i].GetType(), "enabled");
                    enabled[i] = property != null
                        && (bool)property.GetValue(controllers[i], null);
                    if (property != null) property.SetValue(controllers[i], false, null);
                }
                player.transform.position = point;
                Type bodyType = RevivalPlugin.TypeByName("Rigidbody");
                Component body = bodyType == null ? null : player.GetComponent(bodyType);
                if (body != null)
                {
                    SetVector(body, "position", point);
                    SetVector(body, "velocity", Vector3.zero);
                    SetVector(body, "angularVelocity", Vector3.zero);
                }
                for (int i = 0; i < controllers.Length; i++)
                {
                    PropertyInfo property = AccessTools.Property(
                        controllers[i].GetType(), "enabled");
                    if (property != null)
                        property.SetValue(controllers[i], enabled[i], null);
                }
                message = Loc.T("телепортирован в ", "teleported to ") + point;
                RevivalPlugin.L.LogInfo("Admin teleport: local player -> " + point + ".");
                return true;
            }
            catch (Exception ex)
            {
                message = Loc.T("телепорт не удался: ", "teleport failed: ") + ex.Message;
                return false;
            }
        }

        static void SetVector(Component component, string name, Vector3 value)
        {
            PropertyInfo property = AccessTools.Property(component.GetType(), name);
            if (property != null && property.CanWrite)
                property.SetValue(component, value, null);
        }
    }

    /// <summary>
    /// Self-service map teleport. With the map open, right-click a spot on it:
    /// a small "Teleport" button appears at the click, and pressing it warps the
    /// LOCAL player to that world position. Right-clicking again moves the
    /// pending target; Escape or closing the map clears it.
    ///
    /// This is the F8 admin "teleport on map" without the menu detour. It only
    /// ever teleports the caller - teleporting OTHER players stays in the Admin
    /// menu. It reuses MapTools.MouseWorld (the terrain-ray click conversion the
    /// game itself uses for map markers) and MapTools.TeleportLocal, and is
    /// gated by the same admin access as the menu, so an ordinary package
    /// download does not get free teleport.
    ///
    /// Tick() reads the right-click in Update; Draw() paints the button in
    /// OnGUI. The click point is stored in screen pixels (y up, as Unity's
    /// Input.mousePosition reports it) and flipped to GUI space (y down) only
    /// when the button is laid out.
    /// </summary>
    public static class MapTeleport
    {
        static bool _pending;
        static Vector3 _target;
        static Vector2 _clickScreen;
        static string _status;
        static float _statusUntil;

        static bool Enabled
        {
            get
            {
                return RevivalPlugin.CfgMapTeleport != null
                    && RevivalPlugin.CfgMapTeleport.Value
                    && Admin.HasAccess;
            }
        }

        public static void Tick()
        {
            if (!Enabled) { _pending = false; return; }
            // Only act while the map screen is actually up.
            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            if (!MapTools.Context(out manager, out texture, out cam, out world, out map))
            {
                _pending = false;
                return;
            }
            try
            {
                if (_pending && Input.GetKeyDown(KeyCode.Escape))
                {
                    _pending = false;
                    return;
                }
                if (Input.GetMouseButtonDown(1))
                {
                    Vector3 point;
                    if (MapTools.MouseWorld(out point))
                    {
                        _target = point;
                        _clickScreen = Input.mousePosition;
                        _pending = true;
                    }
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Map teleport tick: " + ex.Message);
                _pending = false;
            }
        }

        public static void Draw()
        {
            if (!_pending || !Enabled) return;
            // Confirm the map is still open before painting over it.
            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            if (!MapTools.Context(out manager, out texture, out cam, out world, out map))
            {
                _pending = false;
                return;
            }

            const float w = 90f;
            const float h = 26f;
            float mx = _clickScreen.x;
            float my = Screen.height - _clickScreen.y;

            // A small marker where the click landed.
            GUI.Label(new Rect(mx - 5f, my - 12f, 16f, 20f), "x");

            // Button just off the cursor, flipped back onto the screen if it
            // would run off an edge.
            float x = mx + 8f;
            float y = my + 8f;
            if (x + w > Screen.width) x = mx - w - 8f;
            if (y + h > Screen.height) y = my - h - 8f;
            if (x < 0f) x = 0f;
            if (y < 0f) y = 0f;

            if (GUI.Button(new Rect(x, y, w, h), Loc.T("Телепорт", "Teleport")))
            {
                string message;
                MapTools.TeleportLocal(_target, out message);
                _pending = false;
                _status = message;
                _statusUntil = Time.time + 4f;
                RevivalPlugin.L.LogInfo("Map teleport: " + message);
            }

            if (!string.IsNullOrEmpty(_status) && Time.time < _statusUntil)
                GUI.Label(new Rect(x, y + h + 2f, 280f, 22f), _status);
        }
    }

    /// <summary>
    /// Kleines Menue im Spiel: Items geben, Werkzeuge an- und ausschalten.
    ///
    /// Der Grund ist nicht Bequemlichkeit, sondern Zeit. Bisher fuellte
    /// `invtool.py` das Inventar - und das geht nur bei geschlossenem Spiel,
    /// kostet also je Versuch einen Neustart. Wer eine Waffe dreimal
    /// hintereinander in der Hand sehen will, startet dreimal.
    ///
    /// Gegeben wird ueber `PlayerInventoryManager::AddBackpackItemFromValues` -
    /// dieselbe Methode, die das Spiel beim Laden des Profils benutzt. Ihre
    /// Argumentliste ist am 2026-08-28 aus dem eigenen Diagnoseprotokoll
    /// abgelesen worden:
    ///
    ///     AddBackpackItemFromValues(2051, 0, 0, 0, 0, 0, 5, 0, False)
    ///     AddBackpackItemFromValues(2050, 0, 0, 0, 0, 0, 200, 0, False)
    ///
    /// Argument 0 ist die Item-Id, Argument 6 die Menge. IL shows that the
    /// final bool controls OnChangedInventoryData: true refreshes the UI and
    /// sends the recalculated inventory data. The method silently returns
    /// without adding anything when no backpack slot is free, so both facts
    /// have to be checked here instead of reporting success unconditionally.
    /// </summary>
    public static class Admin
    {
        const int FensterId = 0x4E445241;

        static bool _offen;
        static bool _fokusLoesen;
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static Rect _fenster = new Rect(40f, 40f, 560f, 0f);
        static Vector2 _rollen;
        static string _menge = "";
        static string _status = "Bereit.";
        static bool _sessionGranted;
        static bool _godMode;
        static bool _teleportArmed;
        static int _targetActor = -1;
        static float _nextPlayers;

        // Server-defined items can be granted before their full client-side
        // ItemDef is integrated. Registered definitions take precedence below,
        // so parallel item work cannot create duplicate rows in this menu.
        static readonly int[] ExtraItemIds = new int[] { 2055, 2056, 2057 };
        static readonly string[] ExtraItemNames = new string[] {
            "Mast Antenna", "Drone Battery", "Surveillance Drone"
        };

        class PlayerRow
        {
            public int Actor;
            public string Name;
            public bool Mine;
        }

        static readonly List<PlayerRow> _players = new List<PlayerRow>();

        public static bool IsOpen { get { return _offen; } }

        // -1 noch nicht geprueft, 0 nein, 1 ja. Einmal entschieden bleibt es
        // so: die Steam-Id aendert sich waehrend einer Sitzung nicht.
        static int _zutritt = -1;

        // Wer das Menue oeffnen darf. Es setzt Panzer, Drohne und Items in die
        // Welt - das gehoert nicht in jede Hand, die das Paket herunterlaedt.
        //
        // Das ist KEIN Schutz im Sinne von Sicherheit. Die Liste steht in einer
        // Konfigurationsdatei auf dem Rechner des Spielers, und wer sie aendert,
        // aendert sie. Es haelt das Menue von Haenden fern, die es nicht suchen.
        // Was wirklich zaehlt, prueft ohnehin der Server gegen weapons_db.xml.
        static bool Zutritt()
        {
            if (_sessionGranted) return true;
            if (_zutritt >= 0) return _zutritt == 1;
            _zutritt = 0;
            try
            {
                string liste = RevivalPlugin.CfgAdminIds.Value;
                if (liste == null) liste = "";
                liste = liste.Trim();
                if (liste.Length == 0) { _zutritt = 1; return true; }

                string ich = SteamId();
                if (ich == null)
                {
                    RevivalPlugin.L.LogInfo(
                        "Adminmenue: eigene Steam-Id nicht lesbar, Menue bleibt zu.");
                    return false;
                }
                string[] teile = liste.Split(',');
                for (int i = 0; i < teile.Length; i++)
                {
                    if (teile[i].Trim() == ich) { _zutritt = 1; break; }
                }
                RevivalPlugin.L.LogInfo("Adminmenue: Steam-Id " + ich +
                    (_zutritt == 1 ? " steht auf der Liste." : " steht nicht auf der Liste."));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Adminmenue Zutritt: " + ex.Message);
            }
            return _zutritt == 1;
        }

        internal static bool HasAccess { get { return Zutritt(); } }

        public static void Install(Harmony harmony)
        {
            Net.EnsureHooked();
            try
            {
                Type life = RevivalPlugin.TypeByName("PlayerLifeDataManager");
                MethodInfo canDamage = life == null ? null
                    : AccessTools.Method(life, "CanApplyDamage", null, null);
                MethodInfo godPostfix = typeof(Admin).GetMethod("DamageAllowedPostfix",
                    BindingFlags.Public | BindingFlags.Static);
                if (canDamage == null || godPostfix == null)
                    RevivalPlugin.L.LogWarning("Admin god mode: damage gate not found.");
                else
                {
                    harmony.Patch(canDamage, null, new HarmonyMethod(godPostfix),
                                  null, null, null);
                    RevivalPlugin.L.LogInfo("Admin god mode attached to the local damage gate.");
                }

                Type click = RevivalPlugin.TypeByName("MapClickHandler");
                MethodInfo onClick = click == null ? null
                    : AccessTools.Method(click, "OnClick", null, null);
                MethodInfo postfix = typeof(Admin).GetMethod("MapClickPostfix",
                    BindingFlags.Public | BindingFlags.Static);
                if (onClick == null || postfix == null)
                {
                    RevivalPlugin.L.LogWarning("Admin map teleport: MapClickHandler.OnClick "
                        + "not found.");
                    return;
                }
                harmony.Patch(onClick, null, new HarmonyMethod(postfix), null, null, null);
                RevivalPlugin.L.LogInfo("Admin map teleport attached to map clicks.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Admin map teleport hook: " + ex);
            }
        }

        public static void DamageAllowedPostfix(ref bool __result)
        {
            if (_godMode) __result = false;
        }

        public static void MapClickPostfix()
        {
            if (!_teleportArmed || !Zutritt()) return;
            Vector3 point;
            if (!MapTools.MouseWorld(out point))
            {
                Melde(Loc.T("клик по карте не удалось перевести в мировую позицию",
                            "map click could not be converted to a world position"));
                return;
            }
            _teleportArmed = false;
            string message;
            Net.Teleport(_targetActor, point, out message);
            Melde(message);
        }

        // SteamInterface::GetSteamID ist der Umweg des Spiels ueber
        // Steamworks.SteamUser (liegt in Assembly-CSharp-firstpass, nicht in
        // Assembly-CSharp). Zurueck kommt ein CSteamID; die Zahl steht in
        // dessen Feld m_SteamID - belegt mit ildasm gegen beide Assemblies.
        static string SteamId()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("SteamInterface");
                if (t == null) return null;
                MethodInfo m = AccessTools.Method(t, "GetSteamID", null, null);
                if (m == null) return null;
                object id = m.Invoke(null, null);
                if (id == null) return null;
                FieldInfo f = AccessTools.Field(id.GetType(), "m_SteamID");
                if (f == null) return null;
                object roh = f.GetValue(id);
                if (roh == null) return null;
                string s = roh.ToString();
                return s == "0" ? null : s;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Steam-Id nicht lesbar: " + ex.Message);
                return null;
            }
        }

        public static void Tick()
        {
            Net.EnsureHooked();
            if (Time.time >= _nextPlayers)
            {
                _nextPlayers = Time.time + 1f;
                RefreshPlayers();
            }
            if (!RevivalPlugin.CfgAdmin.Value || !Zutritt()) return;
            try
            {
                if (!Input.GetKeyDown(Key())) return;
                _offen = !_offen;
                if (!_offen)
                {
                    _fokusLoesen = true;
                    CursorZurueck();
                }
                RevivalPlugin.L.LogInfo("Adminmenue " + (_offen ? "auf" : "zu") + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Adminmenue: " + ex);
            }
        }

        static void CursorZurueck()
        {
            if (!CursorTracker.SawCall) return;
            CursorTracker.Restoring = true;
            try
            {
                Cursor.lockState = CursorTracker.DesiredLock;
                Cursor.visible = CursorTracker.DesiredVisible;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Adminmenue Cursor zurueck: " + ex.Message);
            }
            finally { CursorTracker.Restoring = false; }
        }

        public static void Draw()
        {
            // Vermutung: Das Mengenfeld haelt sonst den IMGUI-Tastaturfokus.
            if (_fokusLoesen)
            {
                _fokusLoesen = false;
                GUIUtility.keyboardControl = 0;
                GUIUtility.hotControl = 0;
            }
            if (!_offen || !RevivalPlugin.CfgAdmin.Value || !Zutritt()) return;
            // Der Cursor gehoert waehrenddessen dem Menue. CursorGuard wird in
            // RevivalPlugin.Update ausgesetzt, solange offen ist. Restoring
            // verhindert, dass diese Zugriffe als Spielwunsch gespeichert werden.
            CursorTracker.Restoring = true;
            try
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            finally { CursorTracker.Restoring = false; }
            _fenster = GUILayout.Window(FensterId, _fenster, Inhalt,
                                        Loc.T("Revival - Админ",
                                              "Revival - Admin"));
        }

        static void Inhalt(int id)
        {
            GUILayout.Label(Loc.T("Игрок-цель", "Target player"));
            GUILayout.BeginHorizontal();
            if (_players.Count == 0)
                GUILayout.Label(Loc.T("В мире пока нет игроков.", "No player in the world yet."));
            for (int i = 0; i < _players.Count; i++)
            {
                PlayerRow p = _players[i];
                string text = (p.Mine ? Loc.T("я: ", "me: ") : "") + p.Name;
                if (GUILayout.Toggle(_targetActor == p.Actor, text, GUI.skin.button,
                                     GUILayout.Width(125f)))
                    _targetActor = p.Actor;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("выдать админа", "grant admin"), GUILayout.Width(120f)))
            {
                string message;
                Net.Grant(_targetActor, out message);
                Melde(message);
            }
            if (GUILayout.Button(Loc.T("телепорт по карте", "teleport on map"), GUILayout.Width(145f)))
            {
                if (_targetActor < 0) Melde(Loc.T("сначала выберите игрока", "select a player first"));
                else
                {
                    _teleportArmed = true;
                    _offen = false;
                    CursorZurueck();
                    Melde(Loc.T("откройте карту и щёлкните по месту назначения",
                                "open the map and click the destination"));
                }
            }
            if (_teleportArmed && GUILayout.Button(Loc.T("отменить телепорт", "cancel teleport"), GUILayout.Width(125f)))
            {
                _teleportArmed = false;
                Melde(Loc.T("телепорт по карте отменён", "map teleport cancelled"));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("бессмертие ВКЛ", "god mode ON"), GUILayout.Width(120f)))
            {
                string message;
                Net.GodMode(_targetActor, true, out message);
                Melde(message);
            }
            if (GUILayout.Button(Loc.T("бессмертие ВЫКЛ", "god mode OFF"), GUILayout.Width(120f)))
            {
                string message;
                Net.GodMode(_targetActor, false, out message);
                Melde(message);
            }
            GUILayout.Label(_targetActor == Net.OwnActor()
                ? (_godMode ? Loc.T("локально: защищён", "local: protected")
                            : Loc.T("локально: уязвим", "local: vulnerable"))
                : Loc.T("применяется к выбранному игроку", "applies to selected player"));
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label(Loc.T("Полное снаряжение", "Complete loadout"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("полный набор", "full loadout"), GUILayout.Width(150f)))
            {
                string message;
                Net.Loadout(_targetActor, false, out message);
                Melde(message);
            }
            if (GUILayout.Button(Loc.T("полный набор + броня УКБ", "full loadout UKB armor"), GUILayout.Width(190f)))
            {
                string message;
                Net.Loadout(_targetActor, true, out message);
                Melde(message);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label(Loc.T("Конвой", "Convoy"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("отправить конвой сейчас", "spawn convoy now"),
                                 GUILayout.Width(190f)))
                Melde(RevivalConvoy.SpawnNow());
            GUILayout.Label(Loc.T("тест: нужен маршрут с меткой \"конвой\" (F4)",
                                  "test: needs a route marked \"convoy\" (F4)"));
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label(Loc.T("Выдать предметы в рюкзак", "Put items in the backpack"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Кол-во (пусто = станд.):", "Amount (empty = default):"), GUILayout.Width(170f));
            _menge = GUILayout.TextField(_menge, 6, GUILayout.Width(60f));
            GUILayout.EndHorizontal();

            _rollen = GUILayout.BeginScrollView(_rollen, GUILayout.Height(175f));
            List<ItemDef> items = RevivalPlugin.Items;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDef d = items[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(d.Id + "  " + d.Name, GUILayout.Width(250f));
                if (GUILayout.Button(Loc.T("выдать", "give"), GUILayout.Width(90f)))
                    Geben(d);
                GUILayout.EndHorizontal();
            }
            for (int i = 0; i < ExtraItemIds.Length; i++)
            {
                int itemId = ExtraItemIds[i];
                if (RevivalPlugin.FindItem(itemId) != null) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(itemId + "  " + ExtraItemNames[i],
                                GUILayout.Width(250f));
                if (GUILayout.Button(Loc.T("выдать", "give"), GUILayout.Width(90f)))
                    GebenExtra(itemId);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            GUILayout.Label(Loc.T("Инструменты", "Tools"));
            RevivalPlugin.CfgTurret.Value =
                GUILayout.Toggle(RevivalPlugin.CfgTurret.Value,
                                 Loc.T("Пушка (клавиша ", "Gun (key ") + RevivalPlugin.CfgTurretKey.Value + ")");
            RevivalPlugin.CfgArena.Value =
                GUILayout.Toggle(RevivalPlugin.CfgArena.Value,
                                 Loc.T("Полигон (клавиша ", "Test area (key ") + RevivalPlugin.CfgArenaKey.Value + ")");
            RevivalPlugin.CfgSpawnCar.Value =
                GUILayout.Toggle(RevivalPlugin.CfgSpawnCar.Value,
                                 Loc.T("Спавн техники (клавиша ", "Vehicle spawn (key ") + RevivalPlugin.CfgSpawnCarKey.Value + ")");
            RevivalPlugin.CfgTank.Value =
                GUILayout.Toggle(RevivalPlugin.CfgTank.Value,
                                 Loc.T("Танк Т-72 (клавиша ", "T-72 tank (key ") + RevivalPlugin.CfgTankKey.Value + ")");
            RevivalPlugin.CfgSceneJump.Value =
                GUILayout.Toggle(RevivalPlugin.CfgSceneJump.Value,
                                 Loc.T("Переход сцены (клавиша ", "Scene jump (key ") + RevivalPlugin.CfgJumpKey.Value + ")");

            GUILayout.Space(6f);
            GUILayout.Label(_status);
            GUILayout.Label(Loc.T("Всё это также попадает в лог BepInEx. Разбор: python playlog.py",
                                  "Everything here also goes to the BepInEx log. Read it with: python playlog.py"));

            if (GUILayout.Button(Loc.T("закрыть", "close"))) _offen = false;
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        static void Geben(ItemDef d)
        {
            int menge = d.Bullets > 0 ? d.Bullets : 1;
            if (_menge.Length > 0)
            {
                int gewuenscht;
                if (int.TryParse(_menge, out gewuenscht) && gewuenscht > 0)
                    menge = gewuenscht;
            }
            string meldung;
            Net.Item(_targetActor, d.Id, menge, out meldung);
            Melde(meldung);
        }

        static void GebenExtra(int itemId)
        {
            int menge = 1;
            if (_menge.Length > 0)
            {
                int gewuenscht;
                if (int.TryParse(_menge, out gewuenscht) && gewuenscht > 0)
                    menge = gewuenscht;
            }
            string meldung;
            Net.Item(_targetActor, itemId, menge, out meldung);
            Melde(meldung);
        }

        /// <summary>
        /// Ein Item in den Rucksack legen. Seit 0.4.9 nicht mehr nur fuer das
        /// Menue: der Panzerspawn legt hierueber seine Granaten dazu, damit
        /// nicht wieder jemand vor einem Panzer steht, der nicht schiesst.
        /// </summary>
        internal static bool GibItem(int id, int menge, out string meldung)
        {
            try
            {
                object pim = InventarManager();
                if (pim == null)
                {
                    meldung = Loc.T("PlayerInventoryManager не найден - в главном меню "
                              + "его нет. Сначала зайдите в игру.",
                                    "PlayerInventoryManager not found - there is none "
                              + "in the main menu. Enter the world first.");
                    return false;
                }

                MethodInfo m = null;
                MethodInfo[] alle = pim.GetType().GetMethods();
                for (int i = 0; i < alle.Length; i++)
                    if (alle[i].Name == "AddBackpackItemFromValues") { m = alle[i]; break; }
                if (m == null)
                {
                    meldung = Loc.T("AddBackpackItemFromValues отсутствует - другая версия игры?",
                                    "AddBackpackItemFromValues missing - different game version?");
                    return false;
                }

                int freieVorher = FreeBackpackSlots(pim);
                if (freieVorher < 0)
                {
                    meldung = Loc.T("Данные рюкзака не читаются - ничего не выдано.",
                                    "Backpack data unreadable - nothing was given.");
                    return false;
                }
                if (freieVorher == 0)
                {
                    meldung = Loc.T("Рюкзак полон - нет места для " + id + ".",
                                    "Backpack full - no free slot for " + id + ".");
                    return false;
                }

                ParameterInfo[] ps = m.GetParameters();
                object[] args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    if (pt == typeof(bool)) args[i] = false;
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null;
                }
                args[0] = id;
                // Argument 6 ist in allen beobachteten Aufrufen die Menge. Hat
                // die Methode weniger Argumente, wird NICHT geraten.
                bool mengeGesetzt = ps.Length > 6 && ps[6].ParameterType == typeof(int);
                if (mengeGesetzt) args[6] = menge;
                // IL: the last bool is onChangeInventory. Without true the
                // item data changes, but UI and server copy stay stale.
                bool aktualisiert = ps.Length > 0
                    && ps[ps.Length - 1].ParameterType == typeof(bool);
                if (aktualisiert) args[ps.Length - 1] = true;

                m.Invoke(pim, args);
                int freieNachher = FreeBackpackSlots(pim);
                if (freieNachher >= freieVorher)
                {
                    meldung = Loc.T("не выдано: " + id + " отклонён инвентарём.",
                                    "not given: " + id + " was refused by the inventory.");
                    return false;
                }
                meldung = Loc.T("выдано: ", "given: ") + id + " x" + menge
                          + (mengeGesetzt ? "" : " (Argument 6 ist kein int - "
                                                 + "Menge nicht gesetzt)")
                          + (aktualisiert ? "" : " (Inventar-Refresh fehlt)");
                return true;
            }
            catch (Exception ex)
            {
                meldung = Loc.T("ошибка: ", "failed: ") + ex.Message;
                RevivalPlugin.L.LogError("Item geben: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Pick only a manager owned by this client. FindObjectOfType returned
        /// an arbitrary player's manager as soon as a second player joined.
        /// </summary>
        static object InventarManager()
        {
            Type t = RevivalPlugin.TypeByName("PlayerInventoryManager");
            if (t == null) return null;
            List<object> own = Turret.PlayerInventories();
            if (own.Count == 0) return null;

            string[] namen = new string[] { "current", "Instance", "instance" };
            for (int i = 0; i < namen.Length; i++)
            {
                MethodInfo g = AccessTools.PropertyGetter(t, namen[i]);
                if (g == null || !g.IsStatic) continue;
                object o = g.Invoke(null, null);
                if (o == null) continue;
                for (int j = 0; j < own.Count; j++)
                    if (System.Object.ReferenceEquals(o, own[j])) return o;
            }
            return own[0];
        }

        static int FreeBackpackSlots(object pim)
        {
            if (pim == null) return -1;
            FieldInfo f = AccessTools.Field(pim.GetType(), "_backpackData");
            object data = f == null ? null : f.GetValue(pim);
            if (data == null) return -1;
            FieldInfo idsField = AccessTools.Field(data.GetType(), "ItemID");
            Array ids = idsField == null ? null : idsField.GetValue(data) as Array;
            if (ids == null) return -1;
            int free = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                object value = ids.GetValue(i);
                if (value == null || ReadInt(value) == 0) free++;
            }
            return free;
        }

        static int ReadInt(object value)
        {
            if (value == null) return 0;
            if (value is int) return (int)value;
            Type t = value.GetType();
            MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "op_Implicit"
                    || methods[i].ReturnType != typeof(int)) continue;
                ParameterInfo[] ps = methods[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == t)
                    return (int)methods[i].Invoke(null, new object[] { value });
            }
            return -1;
        }

        static float ReadFloat(object value)
        {
            if (value == null) return 0f;
            if (value is float) return (float)value;
            if (value is double) return (float)(double)value;
            Type t = value.GetType();
            MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "op_Implicit"
                    || methods[i].ReturnType != typeof(float)) continue;
                ParameterInfo[] ps = methods[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == t)
                    return (float)methods[i].Invoke(null, new object[] { value });
            }
            try { return Convert.ToSingle(value); }
            catch { return 0f; }
        }

        static object Field(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo f = AccessTools.Field(instance.GetType(), name);
            return f == null ? null : f.GetValue(instance);
        }

        static object ArrayValue(object instance, string name, int index)
        {
            Array values = Field(instance, name) as Array;
            if (values == null || index < 0 || index >= values.Length) return null;
            return values.GetValue(index);
        }

        static object[] DefaultArgs(MethodInfo method)
        {
            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                if (t == typeof(bool)) args[i] = false;
                else if (t.IsValueType) args[i] = Activator.CreateInstance(t);
                else args[i] = null;
            }
            return args;
        }

        static MethodInfo NamedMethod(object instance, string name, int parameterCount)
        {
            if (instance == null) return null;
            MethodInfo[] all = instance.GetType().GetMethods(BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < all.Length; i++)
                if (all[i].Name == name
                    && all[i].GetParameters().Length == parameterCount) return all[i];
            return null;
        }

        static void RefreshPlayers()
        {
            _players.Clear();
            try
            {
                Type ngsType = RevivalPlugin.TypeByName("NetworkGameServer");
                MethodInfo get = ngsType == null ? null
                    : AccessTools.PropertyGetter(ngsType, "Instance");
                object ngs = get == null ? null : get.Invoke(null, null);
                FieldInfo field = ngsType == null ? null
                    : AccessTools.Field(ngsType, "NetworkPlayers");
                IEnumerable rows = ngs == null || field == null ? null
                    : field.GetValue(ngs) as IEnumerable;
                if (rows == null) return;

                int own = Net.OwnActor();
                foreach (object raw in rows)
                {
                    GameObject go = raw as GameObject;
                    if (go == null) continue;
                    object photonPlayer;
                    int actor = ActorFor(go, out photonPlayer);
                    if (actor <= 0) continue;
                    PlayerRow row = new PlayerRow();
                    row.Actor = actor;
                    row.Mine = actor == own;
                    row.Name = PlayerName(go, photonPlayer, actor);
                    _players.Add(row);
                }
                bool targetExists = false;
                for (int i = 0; i < _players.Count; i++)
                    if (_players[i].Actor == _targetActor) targetExists = true;
                if (!targetExists)
                    _targetActor = own > 0 ? own
                        : (_players.Count == 0 ? -1 : _players[0].Actor);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Admin player list: " + ex.Message);
            }
        }

        static int ActorFor(GameObject go, out object photonPlayer)
        {
            photonPlayer = null;
            Type pncType = RevivalPlugin.TypeByName("PlayerNetworkController");
            if (pncType == null || go == null) return -1;
            Component pnc = go.GetComponentInChildren(pncType, true);
            if (pnc == null) pnc = go.GetComponentInParent(pncType);
            if (pnc == null) return -1;
            MethodInfo get = AccessTools.PropertyGetter(pncType, "GetPhotonPlayer");
            if (get == null) return -1;
            photonPlayer = get.Invoke(pnc, null);
            return PhotonActor(photonPlayer);
        }

        static int PhotonActor(object player)
        {
            if (player == null) return -1;
            string[] names = new string[] { "ID", "ActorNumber", "ActorNr" };
            for (int i = 0; i < names.Length; i++)
            {
                MethodInfo get = AccessTools.PropertyGetter(player.GetType(), names[i]);
                if (get == null) continue;
                try { return Convert.ToInt32(get.Invoke(player, null)); }
                catch { }
            }
            return -1;
        }

        static string PlayerName(GameObject go, object player, int actor)
        {
            if (player != null)
            {
                string[] names = new string[] { "NickName", "name", "Name" };
                for (int i = 0; i < names.Length; i++)
                {
                    MethodInfo get = AccessTools.PropertyGetter(player.GetType(), names[i]);
                    if (get == null) continue;
                    try
                    {
                        object value = get.Invoke(player, null);
                        if (value != null && value.ToString().Length > 0)
                            return value.ToString();
                    }
                    catch { }
                }
            }
            return (go == null ? "player" : go.name) + " #" + actor;
        }

        class GearDef
        {
            public int Slot, Id;
            public float Energy, Regenerate;
            public GearDef(int slot, int id, float energy, float regenerate)
            { Slot = slot; Id = id; Energy = energy; Regenerate = regenerate; }
        }

        class BackpackItem
        {
            public int Id, Bullets, Clip;
            public float Food, Water, Energy, Regenerate, Condition;
        }

        static readonly GearDef[] FullGear = new GearDef[] {
            new GearDef(0, 4128, 0f, 0f), new GearDef(1, 4710, 50f, 0f),
            new GearDef(2, 4323, 0f, 50f), new GearDef(4, 4603, 0f, 0f),
            new GearDef(5, 4205, 50f, 0f), new GearDef(6, 4517, 0f, 8f) };

        static readonly GearDef[] UkbGear = new GearDef[] {
            new GearDef(0, 4017, 45f, 48.5f), new GearDef(1, 4710, 50f, 0f),
            new GearDef(2, 4316, 30f, 48.5f), new GearDef(4, 4603, 0f, 0f),
            new GearDef(5, 4205, 50f, 0f), new GearDef(6, 4509, 25f, 40f) };

        static bool ApplyLoadout(bool ukb, out string message)
        {
            object pim = InventarManager();
            if (pim == null)
            {
                message = Loc.T("инвентарь не найден - сначала зайдите в игру",
                                "inventory not found - enter the world first");
                return false;
            }
            try
            {
                int backpack = ukb ? 6019 : 6028;
                int capacity = ukb ? 22 : 40;
                List<BackpackItem> saved = SnapshotBackpack(pim);
                if (saved.Count > capacity)
                {
                    message = Loc.T("в текущем рюкзаке предметов: " + saved.Count + ", а у "
                        + backpack + " всего слотов: " + capacity + "; ничего не изменено",
                                    "current backpack has " + saved.Count + " items, but "
                        + backpack + " has only " + capacity + " slots; nothing changed");
                    return false;
                }
                if (!EquipBackpack(pim, backpack, saved, out message)) return false;

                GearDef[] gear = ukb ? UkbGear : FullGear;
                for (int i = 0; i < gear.Length; i++) EquipGear(pim, gear[i]);

                EquipWeapon(pim, 0, 1160, 200, 2050);
                EquipWeapon(pim, 1, 1161, 5, 2051);
                EquipWeapon(pim, 2, 1162, 1, 0);

                int[,] supplies = new int[,] {
                    {2050,200},{2050,200},{2051,10},{2051,10},{2051,10},{2051,10},
                    {2053,1},{2053,1},{2053,1},{2053,1},{2053,1},
                    {2052,1},{2052,1},{1163,1},{1163,1},{1163,1},
                    {2054,1},{7001,1},{7001,1},{7001,1} };
                int added = 0;
                for (int i = 0; i < supplies.GetLength(0); i++)
                {
                    string one;
                    if (GibItem(supplies[i, 0], supplies[i, 1], out one)) added++;
                    else break;
                }
                message = (ukb ? Loc.T("полный набор УКБ", "full UKB loadout")
                               : Loc.T("полный набор", "full loadout"))
                    + Loc.T(" выдан; добавлено слотов снабжения: ", " equipped; ") + added
                    + Loc.T("", " supply slots added");
                RevivalPlugin.L.LogInfo("Admin: " + message + ".");
                return true;
            }
            catch (Exception ex)
            {
                message = Loc.T("снаряжение не удалось: ", "loadout failed: ") + ex.Message;
                RevivalPlugin.L.LogError("Admin loadout: " + ex);
                return false;
            }
        }

        static List<BackpackItem> SnapshotBackpack(object pim)
        {
            List<BackpackItem> result = new List<BackpackItem>();
            object data = Field(pim, "_backpackData");
            Array ids = Field(data, "ItemID") as Array;
            if (ids == null) return result;
            for (int i = 0; i < ids.Length; i++)
            {
                int item = ReadInt(ids.GetValue(i));
                if (item <= 0) continue;
                BackpackItem value = new BackpackItem();
                value.Id = item;
                value.Food = ReadFloat(ArrayValue(data, "ItemFood", i));
                value.Water = ReadFloat(ArrayValue(data, "ItemWater", i));
                value.Energy = ReadFloat(ArrayValue(data, "ItemEnergy", i));
                value.Regenerate = ReadFloat(ArrayValue(data, "ItemRegenerate", i));
                value.Condition = ReadFloat(ArrayValue(data, "ItemCondition", i));
                value.Bullets = ReadInt(ArrayValue(data, "ItemBullets", i));
                value.Clip = ReadInt(ArrayValue(data, "ClipItemID", i));
                result.Add(value);
            }
            return result;
        }

        static bool EquipBackpack(object pim, int wanted,
                                  List<BackpackItem> saved, out string message)
        {
            object data = Field(pim, "_backpackData");
            int current = ReadInt(Field(data, "BackpackID"));
            if (current == wanted)
            {
                message = Loc.T("рюкзак уже надет", "backpack already equipped");
                return true;
            }

            if (current > 0) ClearSlot(pim, "ClearGearSlot", 3, current);
            FieldInfo backpackId = data == null ? null
                : AccessTools.Field(data.GetType(), "BackpackID");
            if (backpackId == null)
            {
                message = "BackpackID field not found";
                return false;
            }
            backpackId.SetValue(data, MakeNumber(backpackId.FieldType, 0));

            MethodInfo give = NamedMethod(pim, "GiveItem", 3);
            if (give == null || give.GetParameters().Length != 3)
            {
                message = "GiveItem signature changed";
                return false;
            }
            object[] args = DefaultArgs(give);
            args[0] = wanted;
            Type sourceType = give.GetParameters()[1].ParameterType;
            args[1] = sourceType.IsEnum ? Enum.ToObject(sourceType, 2)
                                        : Convert.ChangeType(2, sourceType);
            args[2] = true;
            give.Invoke(pim, args);

            data = Field(pim, "_backpackData");
            if (ReadInt(Field(data, "BackpackID")) != wanted)
            {
                message = Loc.T("рюкзак " + wanted + " отклонён", "backpack " + wanted + " was refused");
                return false;
            }
            for (int i = 0; i < saved.Count; i++) AddBackpackValues(pim, saved[i]);
            message = Loc.T("рюкзак " + wanted + " надет, восстановлено предметов: " + saved.Count,
                            "backpack " + wanted + " equipped and " + saved.Count
                + " existing item(s) restored");
            return true;
        }

        static void AddBackpackValues(object pim, BackpackItem value)
        {
            MethodInfo add = NamedMethod(pim, "AddBackpackItemFromValues", 9);
            if (add == null)
                throw new MissingMethodException("AddBackpackItemFromValues signature changed");
            object[] args = DefaultArgs(add);
            args[0] = value.Id;
            args[1] = value.Food;
            args[2] = value.Water;
            args[3] = value.Energy;
            args[4] = value.Regenerate;
            args[5] = value.Condition;
            args[6] = value.Bullets;
            args[7] = value.Clip;
            args[args.Length - 1] = true;
            add.Invoke(pim, args);
        }

        static void EquipGear(object pim, GearDef gear)
        {
            object data = Field(pim, "_gearsData");
            int current = ReadInt(ArrayValue(data, "ItemID", gear.Slot));
            if (current > 0 && current != gear.Id)
                ClearSlot(pim, "ClearGearSlot", gear.Slot, current);
            if (current == gear.Id) return;

            MethodInfo add = NamedMethod(pim, "AddGearItemFromValues", 7);
            if (add == null)
                throw new MissingMethodException("AddGearItemFromValues signature changed");
            object[] args = DefaultArgs(add);
            args[0] = gear.Slot;
            args[1] = gear.Id;
            args[2] = gear.Energy;
            args[3] = gear.Regenerate;
            args[4] = 0f;
            args[5] = 0f;
            args[args.Length - 1] = true;
            add.Invoke(pim, args);
            data = Field(pim, "_gearsData");
            if (ReadInt(ArrayValue(data, "ItemID", gear.Slot)) != gear.Id)
                throw new InvalidOperationException("gear " + gear.Id
                    + " was refused in slot " + gear.Slot);
        }

        static void EquipWeapon(object pim, int slot, int item, int bullets, int clip)
        {
            object data = Field(pim, "_weaponsData");
            int current = ReadInt(ArrayValue(data, "ItemID", slot));
            if (current > 0) ClearSlot(pim, "ClearWeaponSlot", slot, current);

            MethodInfo add = NamedMethod(pim, "AddWeaponItemFromValues", 6);
            if (add == null)
                throw new MissingMethodException("AddWeaponItemFromValues signature changed");
            object[] args = DefaultArgs(add);
            args[0] = slot;
            args[1] = item;
            args[2] = bullets;
            args[3] = clip;
            args[4] = 0f;
            args[args.Length - 1] = true;
            add.Invoke(pim, args);
            data = Field(pim, "_weaponsData");
            if (ReadInt(ArrayValue(data, "ItemID", slot)) != item)
                throw new InvalidOperationException("weapon " + item
                    + " was refused in slot " + slot);
        }

        static void ClearSlot(object pim, string methodName, int slot, int item)
        {
            int count = methodName == "ClearGearSlot" ? 5 : 4;
            MethodInfo clear = NamedMethod(pim, methodName, count);
            if (clear == null) throw new MissingMethodException(methodName);
            object[] args = DefaultArgs(clear);
            if (args.Length < 2) throw new MissingMethodException(methodName + " signature");
            args[0] = slot;
            args[1] = item;
            ParameterInfo[] ps = clear.GetParameters();
            for (int i = 2; i < ps.Length; i++)
            {
                if (ps[i].ParameterType != typeof(bool)) continue;
                string name = ps[i].Name == null ? "" : ps[i].Name.ToLowerInvariant();
                args[i] = name.IndexOf("onchange") >= 0;
            }
            clear.Invoke(pim, args);
        }

        static object MakeNumber(Type type, int value)
        {
            if (type == typeof(int)) return value;
            MethodInfo[] all = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Name != "op_Implicit" || all[i].ReturnType != type) continue;
                ParameterInfo[] ps = all[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
                    return all[i].Invoke(null, new object[] { value });
            }
            return Convert.ChangeType(value, type);
        }

        public static class Net
        {
            const int GrantAction = 1;
            const int ItemAction = 2;
            const int LoadoutAction = 3;
            const int TeleportAction = 4;
            const int GodModeAction = 5;

            static bool _hooked, _failed;
            static MethodInfo _raise;
            static Type _optionsType;
            static FieldInfo _onEvent;

            public static void EnsureHooked()
            {
                if (_hooked || _failed || RevivalPlugin.CfgAdminEventCode == null) return;
                try
                {
                    int code = RevivalPlugin.CfgAdminEventCode.Value;
                    int drone = RevivalPlugin.CfgDroneEventCode.Value;
                    if (code < 0 || code > 199
                        || (code >= drone && code <= drone + 4)
                        || code == RevivalPlugin.CfgTurretEventCode.Value
                        || (RevivalPlugin.CfgPatrolCrewDroneEventCode != null
                            && code == RevivalPlugin.CfgPatrolCrewDroneEventCode.Value))
                        throw new Exception("event code " + code + " overlaps another channel");

                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null) throw new Exception("PhotonNetwork missing");
                    _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                    _onEvent = AccessTools.Field(photon, "OnEventCall");
                    _optionsType = RevivalPlugin.TypeByName("RaiseEventOptions");
                    if (_raise == null || _onEvent == null)
                        throw new Exception("Photon event reflection path incomplete");
                    MethodInfo own = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static);
                    Delegate handler = Delegate.CreateDelegate(_onEvent.FieldType, own);
                    Delegate current = _onEvent.GetValue(null) as Delegate;
                    _onEvent.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("Admin network attached: event " + code + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("Admin network not attached: " + ex);
                }
            }

            internal static int OwnActor()
            {
                try
                {
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null) return -1;
                    MethodInfo get = AccessTools.PropertyGetter(photon, "player");
                    if (get == null) get = AccessTools.PropertyGetter(photon, "LocalPlayer");
                    return PhotonActor(get == null ? null : get.Invoke(null, null));
                }
                catch { return -1; }
            }

            static bool Send(float[] data, bool reliable)
            {
                EnsureHooked();
                if (!_hooked) return false;
                object options = _optionsType == null ? null
                    : Activator.CreateInstance(_optionsType);
                _raise.Invoke(null, new object[] {
                    (byte)RevivalPlugin.CfgAdminEventCode.Value, data, reliable, options });
                return true;
            }

            public static void Grant(int target, out string message)
            {
                if (target <= 0) { message = Loc.T("сначала выберите игрока", "select a player first"); return; }
                if (target == OwnActor())
                {
                    _sessionGranted = true;
                    message = Loc.T("админ-доступ уже активен в этой сессии",
                                    "admin access already active for this session");
                    return;
                }
                message = Send(new float[] { GrantAction, target }, true)
                    ? Loc.T("временный админ-доступ отправлен игроку #", "temporary admin access sent to player #") + target
                    : Loc.T("не удалось отправить выдачу админа", "admin grant could not be sent");
            }

            public static void Item(int target, int item, int amount, out string message)
            {
                if (target <= 0) { message = Loc.T("сначала выберите игрока", "select a player first"); return; }
                if (target == OwnActor())
                {
                    GibItem(item, amount, out message);
                    return;
                }
                message = Send(new float[] { ItemAction, target, item, amount }, true)
                    ? Loc.T("предмет ", "item ") + item + Loc.T(" отправлен игроку #", " sent to player #") + target
                    : Loc.T("не удалось отправить команду выдачи", "item command could not be sent");
            }

            public static void Loadout(int target, bool ukb, out string message)
            {
                if (target <= 0) { message = Loc.T("сначала выберите игрока", "select a player first"); return; }
                if (target == OwnActor())
                {
                    ApplyLoadout(ukb, out message);
                    return;
                }
                message = Send(new float[] { LoadoutAction, target, ukb ? 1f : 0f }, true)
                    ? (ukb ? Loc.T("набор УКБ", "UKB loadout") : Loc.T("набор", "loadout"))
                        + Loc.T(" отправлен игроку #", " sent to player #") + target
                    : Loc.T("не удалось отправить команду снаряжения", "loadout command could not be sent");
            }

            public static void Teleport(int target, Vector3 point, out string message)
            {
                if (target <= 0) { message = Loc.T("сначала выберите игрока", "select a player first"); return; }
                if (target == OwnActor())
                {
                    MapTools.TeleportLocal(point, out message);
                    return;
                }
                message = Send(new float[] {
                    TeleportAction, target, point.x, point.y, point.z }, true)
                    ? Loc.T("телепорт отправлен игроку #", "teleport sent to player #") + target
                    : Loc.T("не удалось отправить телепорт", "teleport command could not be sent");
            }

            public static void GodMode(int target, bool enabled, out string message)
            {
                if (target <= 0) { message = Loc.T("сначала выберите игрока", "select a player first"); return; }
                if (target == OwnActor())
                {
                    SetGodMode(enabled, out message);
                    return;
                }
                message = Send(new float[] {
                    GodModeAction, target, enabled ? 1f : 0f }, true)
                    ? Loc.T("бессмертие ", "god mode ") + (enabled ? Loc.T("ВКЛ", "ON") : Loc.T("ВЫКЛ", "OFF"))
                        + Loc.T(" отправлено игроку #", " sent to player #") + target
                    : Loc.T("не удалось отправить команду бессмертия", "god mode command could not be sent");
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                if (RevivalPlugin.CfgAdminEventCode == null
                    || code != (byte)RevivalPlugin.CfgAdminEventCode.Value) return;
                try
                {
                    float[] data = content as float[];
                    if (data == null || data.Length < 2) return;
                    int target = Mathf.RoundToInt(data[1]);
                    if (target != OwnActor()) return;
                    int action = Mathf.RoundToInt(data[0]);
                    string message = "";
                    if (action == GrantAction)
                    {
                        _sessionGranted = true;
                        _zutritt = 1;
                        message = Loc.T("игрок #" + sender + " выдал вам временный админ-доступ на эту сессию",
                                        "player #" + sender + " granted temporary admin access for this session");
                    }
                    else if (action == ItemAction && data.Length >= 4)
                        GibItem(Mathf.RoundToInt(data[2]), Mathf.RoundToInt(data[3]),
                                out message);
                    else if (action == LoadoutAction && data.Length >= 3)
                        ApplyLoadout(data[2] > 0.5f, out message);
                    else if (action == TeleportAction && data.Length >= 5)
                        MapTools.TeleportLocal(new Vector3(data[2], data[3], data[4]),
                                               out message);
                    else if (action == GodModeAction && data.Length >= 3)
                        SetGodMode(data[2] > 0.5f, out message);
                    if (message.Length > 0)
                    {
                        Melde(message);
                        Turret.Hinweis(message, 5f);
                    }
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Admin network receive: " + ex.Message);
                }
            }
        }

        static void SetGodMode(bool enabled, out string message)
        {
            _godMode = enabled;
            message = Loc.T("бессмертие ", "god mode ") + (enabled ? Loc.T("ВКЛ", "ON") : Loc.T("ВЫКЛ", "OFF"))
                + Loc.T(" на эту сессию", " for this session");
            RevivalPlugin.L.LogInfo("Admin: " + message + ".");
        }

        static void Melde(string s)
        {
            _status = s;
            RevivalPlugin.L.LogInfo("Adminmenue: " + s);
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try
            {
                _key = (KeyCode)Enum.Parse(typeof(KeyCode),
                                           RevivalPlugin.CfgAdminKey.Value, true);
            }
            catch
            {
                _key = KeyCode.F8;
                RevivalPlugin.L.LogWarning("Adminmenue: Taste "
                    + RevivalPlugin.CfgAdminKey.Value + " unbekannt, benutze F8.");
            }
            return _key;
        }
    }
}
