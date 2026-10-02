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
            Camera cam = CameraOwner.MainCamera();
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
            // One projection for every overlay (Revival.MapProject.cs): the
            // east world's rectangle, and the point in the texture's own
            // units - its scale is not 1 in the east map window.
            return MapProject.ToGui(point, texture, cam, world, map, out gui);
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

                Vector2 centre = EastWorld.MapCentre;   // (0, 0) unless the east world is on
                Vector3 above = new Vector3((nx - 0.5f) * world.x + centre.x, 1000f,
                                            (ny - 0.5f) * world.y + centre.y);
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

        static MethodInfo _localServerGetter;
        static FieldInfo _localPlayerField;
        static int _localPlayerFrame = -1;
        static GameObject _framePlayer;
        static GameObject _scanPlayer;
        static float _scanPlayerUntil;

        public static GameObject LocalPlayer()
        {
            // Share read-only discovery across the many frame consumers. Do not
            // keep a player across frames: respawn and reconnect must be seen.
            if (_localPlayerFrame == Time.frameCount && _framePlayer != null)
                return _framePlayer;
            _framePlayer = FindLocalPlayer();
            _localPlayerFrame = Time.frameCount;
            return _framePlayer;
        }

        static GameObject FindLocalPlayer()
        {
            try
            {
                Type ngsType = RevivalPlugin.TypeByName("NetworkGameServer");
                if (ngsType != null)
                {
                    if (_localServerGetter == null)
                        _localServerGetter = AccessTools.PropertyGetter(ngsType, "Instance");
                    if (_localPlayerField == null)
                        _localPlayerField = AccessTools.Field(ngsType, "localPlayer");
                    MethodInfo get = _localServerGetter;
                    object ngs = get == null ? null : get.Invoke(null, null);
                    FieldInfo local = _localPlayerField;
                    GameObject go = ngs == null || local == null ? null
                        : local.GetValue(ngs) as GameObject;
                    if (go != null) return go;
                }

                // n01 perf: the scene-wide fallback walks every MonoBehaviour.
                // A miss (dead, loading, no NetworkGameServer answer) is not
                // retried by every caller every frame, and a hit is reused
                // for a second (Unity's null check drops a destroyed one).
                if (_scanPlayer != null && Time.unscaledTime < _scanPlayerUntil) return _scanPlayer;
                if (Time.unscaledTime < _scanPlayerUntil) return null;
                _scanPlayer = null;
                _scanPlayerUntil = Time.unscaledTime + 1f;
                Type movement = RevivalPlugin.TypeByName("PlayerMovementController");
                if (movement == null) return null;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(movement);
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i] as MonoBehaviour;
                    if (mb != null && IsMine(mb)) { _scanPlayer = mb.gameObject; return _scanPlayer; }
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
            if (!Enabled) return;
            if (!_pending)
            {
                // The result of the last click stays readable after the popup
                // closed (the flyover's ETA; a teleport's failure).
                if (!string.IsNullOrEmpty(_status) && Time.time < _statusUntil)
                    VanillaUi.Label(new Rect(Screen.width * 0.5f - 210f, Screen.height - 90f, 420f, 44f), _status);
                return;
            }
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
            VanillaUi.Label(new Rect(mx - 5f, my - 12f, 16f, 20f), "x");

            // Button just off the cursor, flipped back onto the screen if it
            // would run off an edge.
            float x = mx + 8f;
            float y = my + 8f;
            if (x + w > Screen.width) x = mx - w - 8f;
            if (y + h > Screen.height) y = my - h - 8f;
            if (x < 0f) x = 0f;
            if (y < 0f) y = 0f;

            if (VanillaUi.Button(new Rect(x, y, w, h), Loc.T("Телепорт", "Teleport")))
            {
                string message;
                MapTools.TeleportLocal(_target, out message);
                _pending = false;
                _status = message;
                _statusUntil = Time.time + 4f;
                RevivalPlugin.L.LogInfo("Map teleport: " + message);
            }

            // N3: the admin's test flyover over this point (Revival.NpcAircraft.cs).
            if (VanillaUi.Button(new Rect(x, y + h + 2f, w, h), Loc.T("Пролёт", "Flyover")))
            {
                string message = Flyover.Ask(_target);
                _pending = false;
                _status = message;
                _statusUntil = Time.time + 6f;
                RevivalPlugin.L.LogInfo("Map flyover: " + message);
            }

            // N11: an air strike (the panel's event) with its target on this point.
            if (VanillaUi.Button(new Rect(x, y + 2f * (h + 2f), w, h), Loc.T("Авиаудар", "Air strike")))
            {
                string message = AirEvents.Ask(_target);
                _pending = false;
                _status = message;
                _statusUntil = Time.time + 8f;
                RevivalPlugin.L.LogInfo("Map air strike: " + message);
            }

            if (!string.IsNullOrEmpty(_status) && Time.time < _statusUntil)
                VanillaUi.Label(new Rect(x, y + 3f * (h + 2f), 420f, 44f), _status);
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
    /// <summary>W-UI4: the row cursor of the admin panel's tabs - and of
    /// the Options() blocks Flyover and AirEvents add to them. Content
    /// coordinates of the current scroll area, one kit row per call.</summary>
    internal static class AdminLayout
    {
        internal static float Y;
        internal static float Width;

        internal static void Begin(float width) { Y = 0f; Width = width; }

        /// <summary>The next full-width row (UiKit.RowH high).</summary>
        internal static Rect Row()
        {
            float h = UiKit.S(UiKit.RowH);
            Rect r = new Rect(0f, Y, Width, h);
            Y += h + UiKit.S(UiKit.Gap);
            return r;
        }

        /// <summary>A section title (small, upper case, hairline) with the
        /// section gap above it.</summary>
        internal static void Section(string title)
        {
            if (Y > 0f) Y += UiKit.S(UiKit.Gap);
            float h = UiKit.S(24f);
            UiKit.Section(new Rect(0f, Y, Width, h), title);
            Y += h + UiKit.S(UiKit.Gap);
        }

        /// <summary>A dim caption under the last row; nothing for null/empty.</summary>
        internal static void Note(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            float h = UiKit.S(18f);
            UiKit.Label(new Rect(UiKit.S(4f), Y - UiKit.S(4f), Width - UiKit.S(8f), h), text,
                        UiFont.Small, UiFont.Left, UiKit.TextDim);
            Y += h;
        }

        /// <summary>One line of body text in r.</summary>
        internal static void Text(Rect r, string text, Color c)
        {
            UiKit.Label(r, text, UiFont.Body, UiFont.Left, c);
        }

        /// <summary>The part of a row from fraction <paramref name="from"/>,
        /// <paramref name="width"/> wide; a kit gap is left before the next part.</summary>
        internal static Rect Part(Rect row, float from, float width)
        {
            float g = from + width < 0.999f ? UiKit.S(UiKit.Gap) : 0f;
            return new Rect(row.x + row.width * from, row.y, row.width * width - g, row.height);
        }
    }

    public static class Admin
    {
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static string _menge = "";
        static string _moneyAmount = "100000";
        static string _moneyStatus = "";
        static string _status;               // the last message; null = "Ready."
        static bool _sessionGranted;
        static bool _godMode;
        static bool _teleportArmed;
        static int _targetActor = -1;
        static float _nextPlayers;
        static int _mercPick;

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
            public string Label, LabelName;  // the button text and the name it was built from
            public bool LabelMine;
        }

        static readonly List<PlayerRow> _players = new List<PlayerRow>();
        static readonly List<PlayerRow> _rowPool = new List<PlayerRow>();

        public static bool IsOpen { get { return Win.Open; } }

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

        /// <summary>Is the admin god mode on for this client? Read by the
        /// surveillance drone, which god mode also keeps out of every fight.</summary>
        internal static bool GodModeActive { get { return _godMode; } }

        public static void Install(Harmony harmony)
        {
            AdminFaction.Install(harmony);
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
            if (Helipads.ClearAreaMapClick()) return;
            if (MercUi.MapClick()) return;           // B3b: a merc patrol route being set
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
        /// <summary>This client's Steam id, or null (B3 mercenaries: the
        /// roster owner and the whitelist key).</summary>
        internal static string LocalSteamId() { return SteamId(); }

        /// <summary>The local PlayerStatisticsManager (money, faction), or null
        /// before the character has loaded. Not per frame: it walks the
        /// player inventories.</summary>
        internal static Component LocalStats()
        {
            Component inventory = InventarManager() as Component;
            Type type = RevivalPlugin.TypeByName("PlayerStatisticsManager");
            return inventory == null || type == null ? null : inventory.GetComponent(type);
        }

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
            // The roster serves the menu and an armed map teleport only.
            if ((Win.Open || _teleportArmed) && Time.time >= _nextPlayers)
            {
                _nextPlayers = Time.time + 1f;
                RefreshPlayers();
            }
            if (!RevivalPlugin.CfgAdmin.Value || !Zutritt())
            {
                if (Win.Open) UiKit.Close(Win);
                return;
            }
            try
            {
                if (Win.Open && Time.realtimeSinceStartup >= _nextLive) RefreshLive();
                if (!Input.GetKeyDown(Key())) return;
                // A focused text field in the panel owns the keyboard.
                if (Win.Open && GUIUtility.keyboardControl != 0) return;
                UiKit.Toggle(Win);
                if (Win.Open)
                {
                    RefreshPlayers();
                    _nextPlayers = Time.time + 1f;
                    RefreshLabels();
                    _nextLive = 0f;
                }
                else if (PerfBisect.OffCount > 0 && !PerfBisect.AutoRunning)
                    UiKit.Toast("Perf tab: " + PerfBisect.OffCount + " feature(s) still switched off", UiTone.Warning);
                RevivalPlugin.L.LogInfo("Adminmenue " + (Win.Open ? "auf" : "zu") + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Adminmenue: " + ex);
            }
        }

        // ================================================= W-UI4: the kit window
        //
        // The admin panel in the UI kit (docs/UI_KIT.md): one window, seven
        // tabs, every row a kit control on the AdminLayout cursor. The kit
        // frees and restores the cursor and closes on Esc. Nothing here
        // builds a string per frame: labels that carry a key or a count are
        // built on open (RefreshLabels), the other modules' status lines at
        // 1 Hz for the visible tab only (RefreshLive).

        static readonly UiWindow Win = new UiWindow("Revival - Админ", "Revival - Admin", 680f, 720f);
        const int TabPlayers = 0, TabVehicles = 1, TabWorld = 2, TabMercs = 3, TabItems = 4, TabTools = 5, TabPerf = 6, TabCount = 7;
        static readonly string[] TabsRu = { "Игроки", "Техника", "Мир", "Наёмники", "Предметы", "Сервис", "Произв." };
        static readonly string[] TabsEn = { "Players", "Vehicles", "World", "Mercs", "Items", "Tools", "Perf" };
        static int _tab;
        static readonly UiScroll[] _scroll = { new UiScroll(), new UiScroll(), new UiScroll(),
                                               new UiScroll(), new UiScroll(), new UiScroll(), new UiScroll() };
        static readonly float[] _tabH = new float[TabCount];
        static float _viewH;
        static float _nextLive;

        // Built on open (keys, capacities, config switches).
        static string _lblKatyRockets, _lblKatyNote, _lblBombs, _lblBombsNote, _lblHeliNote, _lblAn2Note;
        static string _lblTurret, _lblArena, _lblSpawnCar, _lblTank, _lblJump, _lblAuto;
        static int _labelsLang = -1;
        // Refreshed at 1 Hz while the tab shows them.
        static string _liveNpc, _liveForest, _liveBench, _liveMercs, _liveCover, _liveFights, _liveNotify, _liveMerc, _liveMines;
        static readonly List<string> _liveFlak = new List<string>();
        // The item list's row labels, rebuilt when the list changes.
        static string[] _itemLabels = new string[0];
        static int _itemCount = -1;
        static string[] _extraLabels;

        public static void Draw()
        {
            if (!UiKit.BeginWindow(Win)) return;
            try { Content(Win.Content); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Adminmenue draw: " + ex.Message); }
            UiKit.EndWindow(Win);
        }

        static void Content(Rect c)
        {
            float row = UiKit.S(UiKit.RowH), gap = UiKit.S(UiKit.Gap);
            if (_labelsLang != Loc.Lang()) RefreshLabels();
            int tab = UiKit.Tabs(new Rect(0f, 0f, c.width, row + UiKit.S(4f)), _tab, Loc.Lang() == 0 ? TabsRu : TabsEn);
            if (tab != _tab) { _tab = tab; _nextLive = 0f; }
            float top = row + UiKit.S(4f) + UiKit.S(UiKit.Pad);
            float foot = row + gap;
            Rect view = new Rect(0f, top, c.width, c.height - top - foot);
            _viewH = view.height;
            Rect inner = UiKit.BeginScroll(view, _scroll[_tab], _tabH[_tab]);
            AdminLayout.Begin(inner.width);
            switch (_tab)
            {
                case TabPlayers: TabPlayerTools(); break;
                case TabVehicles: TabVehicleTools(); break;
                case TabWorld: TabWorldTools(); break;
                case TabMercs: TabMercTools(); break;
                case TabItems: TabItemList(); break;
                case TabPerf: TabPerfTools(); break;
                default: TabDevTools(); break;
            }
            _tabH[_tab] = AdminLayout.Y;
            UiKit.EndScroll();
            Rect status = new Rect(0f, c.height - row, c.width, row);
            UiKit.Status(status, UiTone.Info, _status ?? Loc.T("Готово.", "Ready."));
            UiKit.Tip(status, Loc.T("Всё это также попадает в лог BepInEx. Разбор: python playlog.py",
                                    "Everything here also goes to the BepInEx log. Read it with: python playlog.py"));
        }

        static bool Btn(Rect r, string text) { return UiKit.Button(r, text, UiButton.Secondary, true, null); }

        static bool Btn(Rect r, string text, string tip) { return UiKit.Button(r, text, UiButton.Secondary, true, tip); }

        // ------------------------------------------------------------ Players

        static void TabPlayerTools()
        {
            AdminFaction.Draw();
            AdminLayout.Section(Loc.T("ДЕНЬГИ (ТОЛЬКО СЕБЕ)", "MONEY (YOURSELF ONLY)"));
            Rect r = AdminLayout.Row();
            if (Btn(UiKit.Col(r, 0, 3), "+100k")) GiveMoney("100000");
            if (Btn(UiKit.Col(r, 1, 3), "+1M")) GiveMoney("1000000");
            if (Btn(UiKit.Col(r, 2, 3), "+10M")) GiveMoney("10000000");
            r = AdminLayout.Row();
            AdminLayout.Text(AdminLayout.Part(r, 0f, 0.2f), Loc.T("Сумма:", "Amount:"), UiKit.TextDim);
            _moneyAmount = UiKit.TextField(AdminLayout.Part(r, 0.2f, 0.4f), _moneyAmount, 32);
            if (UiKit.Button(AdminLayout.Part(r, 0.6f, 0.4f), Loc.T("выдать себе", "give to myself"), UiButton.Primary, true, null))
                GiveMoney(_moneyAmount);
            AdminLayout.Note(_moneyStatus);

            AdminLayout.Section(Loc.T("ИГРОК-ЦЕЛЬ", "TARGET PLAYER"));
            if (_players.Count == 0)
            {
                AdminLayout.Text(AdminLayout.Row(), Loc.T("В мире пока нет игроков.", "No player in the world yet."), UiKit.TextDim);
            }
            for (int i = 0; i < _players.Count; i += 3)
            {
                r = AdminLayout.Row();
                for (int k = 0; k < 3 && i + k < _players.Count; k++)
                {
                    PlayerRow p = _players[i + k];
                    bool on = _targetActor == p.Actor;
                    if (UiKit.Button(UiKit.Col(r, k, 3), p.Label, on ? UiButton.Primary : UiButton.Secondary, true, null))
                        _targetActor = p.Actor;
                }
            }
            r = AdminLayout.Row();
            if (Btn(UiKit.Col(r, 0, 3), Loc.T("выдать админа", "grant admin"),
                    Loc.T("админ-меню для выбранного игрока на эту сессию", "the admin panel for the selected player, this session")))
            {
                string message;
                Net.Grant(_targetActor, out message);
                Melde(message);
            }
            if (Btn(UiKit.Col(r, 1, 3), Loc.T("телепорт по карте", "teleport on map")))
            {
                if (_targetActor < 0) Melde(Loc.T("сначала выберите игрока", "select a player first"));
                else
                {
                    _teleportArmed = true;
                    UiKit.Close(Win);
                    Melde(Loc.T("откройте карту и щёлкните по месту назначения",
                                "open the map and click the destination"));
                }
            }
            if (_teleportArmed && Btn(UiKit.Col(r, 2, 3), Loc.T("отменить телепорт", "cancel teleport")))
            {
                _teleportArmed = false;
                Melde(Loc.T("телепорт по карте отменён", "map teleport cancelled"));
            }
            r = AdminLayout.Row();
            if (Btn(UiKit.Col(r, 0, 3), Loc.T("бессмертие ВКЛ", "god mode ON")))
            {
                string message;
                Net.GodMode(_targetActor, true, out message);
                Melde(message);
            }
            if (Btn(UiKit.Col(r, 1, 3), Loc.T("бессмертие ВЫКЛ", "god mode OFF")))
            {
                string message;
                Net.GodMode(_targetActor, false, out message);
                Melde(message);
            }
            bool self = _targetActor == Net.OwnActor();
            UiKit.Chip(UiKit.Col(r, 2, 3), self
                ? (_godMode ? Loc.T("локально: защищён", "local: protected") : Loc.T("локально: уязвим", "local: vulnerable"))
                : Loc.T("для выбранного игрока", "applies to selected player"),
                self && _godMode ? UiTone.Success : UiTone.Info);

            AdminLayout.Section(Loc.T("ПОЛНОЕ СНАРЯЖЕНИЕ", "COMPLETE LOADOUT"));
            r = AdminLayout.Row();
            if (Btn(UiKit.Col(r, 0, 2), Loc.T("полный набор", "full loadout")))
            {
                string message;
                Net.Loadout(_targetActor, false, out message);
                Melde(message);
            }
            if (Btn(UiKit.Col(r, 1, 2), Loc.T("полный набор + броня УКБ", "full loadout UKB armor")))
            {
                string message;
                Net.Loadout(_targetActor, true, out message);
                Melde(message);
            }
        }

        // ----------------------------------------------------------- Vehicles

        static void TabVehicleTools()
        {
            AdminLayout.Section(Loc.T("СПАВН ПЕРЕД ВАМИ", "SPAWN IN FRONT OF YOU"));
            // N8: the vanilla condition. With the switch on, every spawn button
            // below puts its vehicle down the way the world's own spawn points
            // do (battery, spark plugs, key each 50 percent, a low tank);
            // off, it comes ready as before. Convoys and F7/F9 never change.
            VehicleCondition.SpawnFound = UiKit.Toggle(AdminLayout.Row(), VehicleCondition.SpawnFound,
                Loc.T("спавн как найденный (ванильное состояние)", "spawn as found (vanilla condition)"),
                Loc.T("АКБ/свечи/ключ 50%, мало топлива", "battery/plugs/key 50%, low fuel"));
            Rect r = AdminLayout.Row();
            if (Btn(UiKit.Col(r, 0, 3), "Spawn Ural"))
                Melde(VehicleCondition.AsAdmin(() => VehicleCondition.SpawnKindInFront("ural")));
            if (Btn(UiKit.Col(r, 1, 3), "Spawn T-72"))
                Melde(VehicleCondition.AsAdmin(() => VehicleCondition.SpawnKindInFront("tank")));
            if (Btn(UiKit.Col(r, 2, 3), "Spawn MTW (BTR)"))
                Melde(VehicleCondition.AsAdmin(() => VehicleCondition.SpawnKindInFront("btr")));
            r = AdminLayout.Row();
            if (Btn(UiKit.Col(r, 0, 3), "Spawn technical", "In front of you (MG gun truck)"))
                Melde(VehicleCondition.AsAdmin(() => Technical.SpawnInFront()));
            // The drivable howitzer has no key of its own: F4..F12 are all
            // taken. This button is its spawn, exactly as ArtyVehicle/Key says.
            if (Btn(UiKit.Col(r, 1, 3), "Spawn howitzer", "In front of you (drivable 122 mm howitzer)"))
                Melde(VehicleCondition.AsAdmin(() => ArtyVehicle.SpawnInFront()));
            // Same both-ways press as the [PlayerHeli] spawn key: with an empty
            // machine of yours in reach it takes that one away again.
            if (Btn(UiKit.Col(r, 2, 3), "Spawn helicopter", _lblHeliNote))
                Melde(VehicleCondition.AsAdmin(() => PlayerHeli.SpawnInFront()));

            AdminLayout.Section(Loc.T("ПВО И АРТИЛЛЕРИЯ", "AIR DEFENCE AND ARTILLERY"));
            r = AdminLayout.Row();
            // Same as the howitzer: no free F-key, Gepard/Key is None by default.
            if (Btn(UiKit.Col(r, 0, 2), "Spawn Gepard", "In front of you (35 mm anti-aircraft gun with radar)"))
                Melde(VehicleCondition.AsAdmin(() => Gepard.SpawnInFront()));
            // Four belts of Gepard/AmmoItemId: each one reloads RoundsPerBelt.
            if (Btn(UiKit.Col(r, 1, 2), "Gepard ammo x4", "Ammunition belts for the Gepard into your inventory"))
            {
                string one;
                GibItem(Gepard.CfgAmmoId.Value, 4, out one);
                Melde(one);
            }
            AdminLayout.Note(Gepard.OffNote());
            r = AdminLayout.Row();
            // The Katyusha comes with full rails, so its salvo can be tried at
            // once; the rockets button tests the reload.
            if (Btn(UiKit.Col(r, 0, 2), "Spawn Katyusha"))
                Melde(Katyusha.SpawnInFront());
            if (Btn(UiKit.Col(r, 1, 2), _lblKatyRockets))
            {
                string one;
                GibItem(Katyusha.ItemId, Katyusha.Capacity, out one);
                Melde(one);
            }
            AdminLayout.Note(_lblKatyNote);

            AdminLayout.Section(Loc.T("АН-2", "AN-2"));
            r = AdminLayout.Row();
            // The flyable An-2 ready to go: full tanks, every repair part,
            // full bomb racks. The host builds it, anyone else asks the host
            // over the An-2's own spawn request - the helicopter's pattern.
            if (Btn(UiKit.Col(r, 0, 2), "Spawn An-2 (ready)", _lblAn2Note))
                Melde(PlayerAn2.SpawnReadyInFront());
            // Bombs for the An-2's racks (the load's count), as the Gepard's belts above.
            if (Btn(UiKit.Col(r, 1, 2), _lblBombs, _lblBombsNote))
            {
                string one;
                GibItem(An2Bombs.ItemId, An2Bombs.Capacity, out one);
                Melde(one);
            }
            AdminLayout.Note(PlayerAn2.OffNote() ?? An2Bombs.OffNote());

            AdminLayout.Section(Loc.T("БЛИЖАЙШАЯ ТЕХНИКА", "NEAREST VEHICLE"));
            // The vehicle within 15 m (a Mi-8 within 20 m first): what it has,
            // and the two conditions put on it by hand for a test.
            r = AdminLayout.Row();
            if (Btn(UiKit.Col(r, 0, 3), Loc.T("состояние", "condition")))
                Melde(NearestCondition(0));
            if (Btn(UiKit.Col(r, 1, 3), Loc.T("как найденная", "as found")))
                Melde(NearestCondition(1));
            if (Btn(UiKit.Col(r, 2, 3), Loc.T("готова", "ready")))
                Melde(NearestCondition(2));
            r = AdminLayout.Row();
            if (Btn(UiKit.Col(r, 0, 3), "Vehicle find status")) Melde(VehicleFinds.Report());
        }

        // -------------------------------------------------------------- World

        static void TabWorldTools()
        {
            AdminLayout.Section(Loc.T("СОБЫТИЯ", "EVENTS"));
            Rect r = AdminLayout.Row();
            if (Btn(UiKit.Col(r, 0, 2), Loc.T("отправить конвой сейчас", "spawn convoy now"),
                    Loc.T("тест: нужен маршрут с меткой \"конвой\" (F4)", "test: needs a route marked \"convoy\" (F4)")))
                Melde(RevivalConvoy.SpawnNow());
            if (Btn(UiKit.Col(r, 1, 2), Loc.T("вертолёт с десантом сейчас", "troop helicopter now"),
                    Loc.T("случайная точка из редактора (Troop landings)", "random landing from the editor (Troop landings)")))
                Melde(RevivalTroopInsertion.SpawnNow());

            AdminLayout.Section(Loc.T("ВОЗДУХ", "AIR"));
            r = AdminLayout.Row();
            // N3: an NPC An-2 edge to edge over you, a target for every AA
            // system (Revival.NpcAircraft.cs). A map point: right-click the map.
            if (Btn(AdminLayout.Part(r, 0f, 0.7f), "Test flyover",
                    "NPC An-2 from the map edge straight over you (hostile to all AA; map right-click: Flyover here)"))
                Melde(Flyover.OverMe());
            if (Btn(AdminLayout.Part(r, 0.7f, 0.3f), "Clear"))
                Melde(Flyover.Clear());
            Flyover.Options();
            r = AdminLayout.Row();
            // N11: an editor air event (Tu-95 carpet, An-2 paradrop) with its
            // target moved onto you (Revival.AirEvents.cs). A map point: right-click the map.
            if (Btn(AdminLayout.Part(r, 0f, 0.7f), "Trigger air event now",
                    "the chosen event at your position: siren + radar, then bombers / paratroopers (map right-click: Air strike here)"))
                Melde(AirEvents.NowAtMe());
            if (Btn(AdminLayout.Part(r, 0.7f, 0.3f), "Call off"))
                Melde(AirEvents.Clear());
            AirEvents.Options();
            // N6: what the 52-Ks make of it - fire direction, range, each
            // gun's state (read-only; the flyover above is the target).
            if (Flak.On)
            {
                AdminLayout.Section(Loc.T("ЗЕНИТКИ 52-К", "52-K FLAK"));
                for (int i = 0; i < _liveFlak.Count; i++)
                    AdminLayout.Text(AdminLayout.Row(), _liveFlak[i], i == 0 ? UiKit.TextDim : UiKit.Text);
            }

            AdminLayout.Section(Loc.T("ВОСТОК", "EAST"));
            r = AdminLayout.Row();
            // N9a: are the airfield and town loot points up, and what is the
            // nearest one (docs/ai/tasks/n09a-east-loot-spots.md).
            if (Btn(UiKit.Col(r, 0, 2), Loc.T("лут востока: статус", "east loot: status"),
                    Loc.T("точки лута аэродрома и городка, ближайшая к вам",
                          "Airfield and military town loot points, the nearest to you")))
                Melde(Airfield.LootReport());
            // Every PMN-2 this client knows, off the map on every client.
            if (UiKit.Button(UiKit.Col(r, 1, 2), "Clear AP mines", UiButton.Danger, true, _liveMines))
                Melde(ApMine.ClearAll());
        }

        // -------------------------------------------------------------- Mercs

        static void TabMercTools()
        {
            // B3: mercenaries for testing (Revival.Mercs.cs). With the server
            // roster the grant is a real saved contract; without it a
            // session-only test merc. The status line says which.
            AdminLayout.Section(Loc.T("НАЁМНИКИ", "MERCENARIES"));
            AdminLayout.Text(AdminLayout.Row(), _liveMercs, UiKit.Text);
            // M2: what their fight loops do (Revival.MercFight.cs); the cover
            // overlay shows each merc's state over his head.
            AdminLayout.Note(_liveFights);
            AdminLayout.Note(_liveNotify);
            List<Mercs.Profile> mercProfiles = Mercs.AllProfiles;
            Rect r;
            if (mercProfiles.Count > 0)
            {
                r = AdminLayout.Row();
                float arrow = r.height;
                Rect prev = new Rect(r.x, r.y, arrow, r.height);
                Rect next = new Rect(r.x + r.width * 0.62f - arrow, r.y, arrow, r.height);
                if (Btn(prev, "<"))
                {
                    _mercPick = (_mercPick + mercProfiles.Count - 1) % mercProfiles.Count;
                    _nextLive = 0f;
                }
                AdminLayout.Text(new Rect(prev.xMax + UiKit.S(8f), r.y, next.x - prev.xMax - UiKit.S(16f), r.height), _liveMerc, UiKit.Text);
                if (Btn(next, ">"))
                {
                    _mercPick = (_mercPick + 1) % mercProfiles.Count;
                    _nextLive = 0f;
                }
                _mercPick = Mathf.Clamp(_mercPick, 0, mercProfiles.Count - 1);
                if (UiKit.Button(AdminLayout.Part(r, 0.62f, 0.38f), "Give me this merc", UiButton.Primary, true, null))
                    Melde(Mercs.AdminGive(mercProfiles[_mercPick].Id));
            }
            // W: every merc of mine beside me on FOLLOW; unspawned ones now.
            if (Btn(AdminLayout.Row(), "Bring my mercs to me")) Melde(Mercs.AdminBring());
            r = AdminLayout.Row();
            if (Btn(UiKit.Col(r, 0, 3), "Bill upkeep now")) Melde(Mercs.AdminBillNow());
            if (Btn(UiKit.Col(r, 1, 3), "+24 in-game h")) Melde(Mercs.AdminAddHours(24.0));
            if (Btn(UiKit.Col(r, 2, 3), "Roster to log")) Melde(Mercs.AdminDump());
            r = AdminLayout.Row();
            if (UiKit.Button(UiKit.Col(r, 0, 2), "Kill selected merc", UiButton.Danger, true, null)) Melde(Mercs.AdminKillSelected());
            if (UiKit.Button(UiKit.Col(r, 1, 2), "Clear my roster", UiButton.Danger, true, null)) Melde(Mercs.AdminClear());

            // M1: the merc cover field (Revival.MercCover.cs) and its overlay.
            AdminLayout.Section(Loc.T("УКРЫТИЯ", "COVER"));
            MercCoverService.Show = UiKit.Toggle(AdminLayout.Row(), MercCoverService.Show,
                Loc.T("показать укрытия наёмников", "show merc cover"), null);
            AdminLayout.Note(_liveCover);
        }

        // -------------------------------------------------------------- Items

        static void TabItemList()
        {
            Rect r = AdminLayout.Row();
            AdminLayout.Text(AdminLayout.Part(r, 0f, 0.55f), Loc.T("Кол-во (пусто = станд.):", "Amount (empty = default):"), UiKit.TextDim);
            _menge = UiKit.TextField(AdminLayout.Part(r, 0.55f, 0.2f), _menge, 6);
            AdminLayout.Section(Loc.T("ВЫДАТЬ ПРЕДМЕТЫ В РЮКЗАК", "PUT ITEMS IN THE BACKPACK"));
            ItemLabels();
            List<ItemDef> items = RevivalPlugin.Items;
            float rh = UiKit.S(UiKit.RowH), step = rh + UiKit.S(4f);
            float from = _scroll[TabItems].Offset - step, to = _scroll[TabItems].Offset + _viewH;
            for (int i = 0; i < items.Count + ExtraItemIds.Length; i++)
            {
                bool extra = i >= items.Count;
                int e = i - items.Count;
                if (extra && RevivalPlugin.FindItem(ExtraItemIds[e]) != null) continue;
                float y = AdminLayout.Y;
                AdminLayout.Y += step;
                if (y < from || y > to)
                {
                    // Off screen: take the button's control id and focus slot
                    // anyway, so ids stay the same whatever the scroll offset.
                    GUIUtility.GetControlID(FocusType.Passive);
                    Win.Nav.Next();
                    continue;
                }
                Rect row = new Rect(0f, y, AdminLayout.Width, rh);
                UiKit.Card(row);
                AdminLayout.Text(new Rect(row.x + UiKit.S(10f), y, row.width * 0.75f, rh),
                    extra ? _extraLabels[e] : (i < _itemLabels.Length ? _itemLabels[i] : null), UiKit.Text);
                if (Btn(AdminLayout.Part(row, 0.78f, 0.22f), Loc.T("выдать", "give")))
                {
                    if (extra) GebenExtra(ExtraItemIds[e]);
                    else Geben(items[i]);
                }
            }
        }

        static void ItemLabels()
        {
            List<ItemDef> items = RevivalPlugin.Items;
            if (_itemCount == items.Count && _extraLabels != null) return;
            _itemCount = items.Count;
            _itemLabels = new string[items.Count];
            for (int i = 0; i < items.Count; i++) _itemLabels[i] = items[i].Id + "  " + items[i].Name;
            _extraLabels = new string[ExtraItemIds.Length];
            for (int i = 0; i < ExtraItemIds.Length; i++) _extraLabels[i] = ExtraItemIds[i] + "  " + ExtraItemNames[i];
        }

        // -------------------------------------------------------------- Tools

        static void TabDevTools()
        {
            AdminLayout.Section(Loc.T("ИНСТРУМЕНТЫ", "TOOLS"));
            RevivalPlugin.CfgTurret.Value = UiKit.Toggle(AdminLayout.Row(), RevivalPlugin.CfgTurret.Value, _lblTurret, null);
            RevivalPlugin.CfgArena.Value = UiKit.Toggle(AdminLayout.Row(), RevivalPlugin.CfgArena.Value, _lblArena, null);
            RevivalPlugin.CfgSpawnCar.Value = UiKit.Toggle(AdminLayout.Row(), RevivalPlugin.CfgSpawnCar.Value, _lblSpawnCar, null);
            RevivalPlugin.CfgTank.Value = UiKit.Toggle(AdminLayout.Row(), RevivalPlugin.CfgTank.Value, _lblTank, null);
            RevivalPlugin.CfgSceneJump.Value = UiKit.Toggle(AdminLayout.Row(), RevivalPlugin.CfgSceneJump.Value, _lblJump, null);

            AdminLayout.Section(Loc.T("ЗАМЕРЫ", "BENCHMARKS"));
            Rect r = AdminLayout.Row();
            // N2: frame cost of the NPC distance tiers at this spot (~28 s,
            // hold still); the line is the live tier count until a result.
            if (Btn(AdminLayout.Part(r, 0f, 0.34f), "NPC tier bench"))
                Melde(NpcDistance.Bench());
            AdminLayout.Text(AdminLayout.Part(r, 0.34f, 0.66f), _liveNpc, UiKit.TextDim);
            r = AdminLayout.Row();
            // P3: frame cost of the far forest at this spot (~14 s, on then
            // off); the line is the live state until a result.
            if (Btn(AdminLayout.Part(r, 0f, 0.34f), "Far forest bench"))
                Melde(FarForest.Bench());
            AdminLayout.Text(AdminLayout.Part(r, 0.34f, 0.66f), _liveForest, UiKit.TextDim);
            r = AdminLayout.Row();
            // P3: before/after screenshots from this spot into <game>/NDR_Shots.
            if (Btn(AdminLayout.Part(r, 0f, 0.34f), "Far forest shots"))
                Melde(FarForest.Shots());
            r = AdminLayout.Row();
            // P2: frame time and draw load at the ten render viewpoints
            // (vanilla towns, airfield, military town, tile; ~150 s, teleports
            // and returns you); pressed again, aborts. "Count here": the same
            // numbers at this spot, now.
            if (Btn(AdminLayout.Part(r, 0f, 0.34f), FrameBench.Running ? "Render bench (abort)" : "Render bench"))
                Melde(FrameBench.StartRender());
            if (Btn(AdminLayout.Part(r, 0.34f, 0.2f), "Count here"))
                Melde(FrameBench.CountHere());
            AdminLayout.Text(AdminLayout.Part(r, 0.54f, 0.46f), _liveBench, UiKit.TextDim);

            AdminLayout.Section(Loc.T("ИНТЕРФЕЙС", "INTERFACE"));
            r = AdminLayout.Row();
            // W-UI1: the shared UI kit's demo window (Revival.UiKit.cs). The
            // admin panel closes so the kit window is not under this one.
            if (Btn(AdminLayout.Part(r, 0f, 0.34f), Loc.T("Демо UI-кита", "UI kit demo")))
            {
                UiKit.Close(Win);
                UiDemo.Toggle();
            }
        }

        // --------------------------------------------------------------- Perf

        /// <summary>X perf-bisect (Revival.PerfBisect.cs): every heavy feature
        /// with a local on/off switch, the frame time, each one's own F6 ms,
        /// the auto test and its table. Strings come from RefreshLive (1 Hz)
        /// and the auto test's result; nothing is built here.</summary>
        static void TabPerfTools()
        {
            AdminLayout.Section(Loc.T("ПРОИЗВОДИТЕЛЬНОСТЬ (ЛОКАЛЬНО, ДО ПЕРЕЗАПУСКА)", "PERFORMANCE (LOCAL, UNTIL RESTART)"));
            AdminLayout.Text(AdminLayout.Row(), PerfBisect.LiveHead, UiKit.Text);
            Rect r = AdminLayout.Row();
            bool running = PerfBisect.AutoRunning;
            if (UiKit.Button(AdminLayout.Part(r, 0f, 0.4f), running ? "Stop auto test" : _lblAuto,
                    running ? UiButton.Danger : UiButton.Primary, true,
                    "each feature 5 s off in turn (4 s on before it); hold still, frame ms with / without to this tab and the log"))
                Melde(PerfBisect.AutoToggle());
            if (Btn(AdminLayout.Part(r, 0.4f, 0.2f), "All on") && !running)
            {
                PerfBisect.AllOn();
                _nextLive = 0f;
            }
            AdminLayout.Text(AdminLayout.Part(r, 0.6f, 0.4f), PerfBisect.LiveProgress, UiKit.TextDim);
            AdminLayout.Note(Loc.T("Выключение: только у вас; как хост - патрули/авиасобытия/конвой стоят у всех.",
                                   "Off = this client only; as host, patrols / air events / convoy pause for everyone."));

            AdminLayout.Section(Loc.T("ФУНКЦИИ", "FEATURES"));
            for (int f = 0; f < PerfBisect.Count; f++)
            {
                r = AdminLayout.Row();
                bool on = !PerfBisect.IsOff(f);
                bool want = UiKit.Toggle(AdminLayout.Part(r, 0f, 0.7f), on, PerfBisect.Names[f], PerfBisect.Tips[f]);
                AdminLayout.Text(AdminLayout.Part(r, 0.7f, 0.3f), PerfBisect.LiveOwn(f), UiKit.TextDim);
                if (want != on && !running)
                {
                    PerfBisect.SetOff(f, !want);
                    _nextLive = 0f;
                }
            }

            if (!PerfBisect.HaveResult) return;
            AdminLayout.Section(Loc.T("АВТОТЕСТ: МС КАДРА", "AUTO TEST: FRAME MS"));
            AdminLayout.Note(PerfBisect.ResultTitle);
            r = AdminLayout.Row();
            AdminLayout.Text(AdminLayout.Part(r, 0f, 0.55f), "feature", UiKit.TextDim);
            AdminLayout.Text(AdminLayout.Part(r, 0.55f, 0.15f), "on", UiKit.TextDim);
            AdminLayout.Text(AdminLayout.Part(r, 0.7f, 0.15f), "off", UiKit.TextDim);
            AdminLayout.Text(AdminLayout.Part(r, 0.85f, 0.15f), "saved", UiKit.TextDim);
            for (int f = 0; f < PerfBisect.Count; f++)
            {
                r = AdminLayout.Row();
                int tone = PerfBisect.ResultTone(f);
                Color c = tone == 2 ? UiKit.Warn : tone == 1 ? UiKit.Text : UiKit.TextDim;
                AdminLayout.Text(AdminLayout.Part(r, 0f, 0.55f), PerfBisect.Names[f], c);
                AdminLayout.Text(AdminLayout.Part(r, 0.55f, 0.15f), PerfBisect.ResultOn(f), c);
                AdminLayout.Text(AdminLayout.Part(r, 0.7f, 0.15f), PerfBisect.ResultOff(f), c);
                AdminLayout.Text(AdminLayout.Part(r, 0.85f, 0.15f), PerfBisect.ResultDelta(f), c);
            }
        }

        // ------------------------------------------------------------ texts

        /// <summary>On open and on a language change: the labels that carry a
        /// key, a capacity or a config switch.</summary>
        static void RefreshLabels()
        {
            _labelsLang = Loc.Lang();
            _lblKatyRockets = "Katyusha rockets x" + Katyusha.Capacity;
            _lblKatyNote = Katyusha.Enabled
                ? "BM-13 rocket launcher, rails loaded - " + Katyusha.CfgFireKey.Value + " in the cab: map fire control; M-13 rockets: "
                  + Katyusha.CfgLoadKey.Value + " at a standing Katyusha loads them, one by one"
                : "[Katyusha] Enabled = false in the config";
            _lblBombs = "An-2 bombs x" + An2Bombs.Capacity;
            _lblBombsNote = "Aerial bombs into your inventory (" + An2Bombs.CfgLoadKey.Value + " at a parked An-2 loads them as "
                + An2BombLoad.Name(An2Bombs.LoadKg) + ", [An2Bombs] BombLoad / F2)"
                + (An2Bombs.Enabled ? "" : " - loading needs [PlayerAn2] Enabled and [Gameplay] An2Bombs; the ready An-2 switches both on");
            _lblHeliNote = "In front of you (Mi-8 you can fly - " + PlayerHeli.CfgBoardKey.Value + " to get in)";
            _lblAn2Note = "In front of you (repaired, fuelled, bombs aboard - " + PlayerAn2.CfgBoardKey.Value + " to get in)";
            _lblTurret = Loc.T("Пушка (клавиша ", "Gun (key ") + RevivalPlugin.CfgTurretKey.Value + ")";
            _lblArena = Loc.T("Полигон (клавиша ", "Test area (key ") + RevivalPlugin.CfgArenaKey.Value + ")";
            _lblSpawnCar = Loc.T("Спавн техники (клавиша ", "Vehicle spawn (key ") + RevivalPlugin.CfgSpawnCarKey.Value + ")";
            _lblTank = Loc.T("Танк Т-72 (клавиша ", "T-72 tank (key ") + RevivalPlugin.CfgTankKey.Value + ")";
            _lblJump = Loc.T("Переход сцены (клавиша ", "Scene jump (key ") + RevivalPlugin.CfgJumpKey.Value + ")";
            _lblAuto = "Auto test (~" + Mathf.CeilToInt(PerfBisect.AutoSeconds) + " s)";
            for (int i = 0; i < _players.Count; i++) _players[i].Label = null;
            LabelPlayers();
        }

        /// <summary>1 Hz while open: the status lines of the visible tab (the
        /// modules build them; nothing is asked for a hidden tab).</summary>
        static void RefreshLive()
        {
            _nextLive = Time.realtimeSinceStartup + 1f;
            switch (_tab)
            {
                case TabWorld:
                    _liveMines = "PMN-2 anti-personnel mines laid: " + ApMine.Count;
                    _liveFlak.Clear();
                    if (Flak.On)
                    {
                        List<FlakGunInfo> guns = Flak.Guns();
                        _liveFlak.Add((TowerRadar.On ? TowerRadar.TierName(TowerRadar.Tier) : "no radar")
                            + ", range " + Flak.RangeMetres(TowerRadar.On && TowerRadar.Tier == 2).ToString("0") + " m"
                            + (guns.Count == 0 ? ", no gun built yet" : ""));
                        for (int i = 0; i < guns.Count; i++)
                            _liveFlak.Add(guns[i].Id + "   " + guns[i].State + "   crew " + guns[i].CrewAlive + "/2   "
                                + guns[i].Rounds + " rds");
                    }
                    break;
                case TabMercs:
                    _liveMercs = Mercs.Status();
                    _liveCover = "Merc cover: " + MercCoverService.Status();
                    _liveFights = "Merc fights: " + MercFightStats.Status();
                    _liveNotify = MercNotify.Status();
                    List<Mercs.Profile> mercProfiles = Mercs.AllProfiles;
                    if (mercProfiles.Count > 0)
                    {
                        _mercPick = Mathf.Clamp(_mercPick, 0, mercProfiles.Count - 1);
                        Mercs.Profile mp = mercProfiles[_mercPick];
                        _liveMerc = mp.Name + " (" + mp.Settlement + ", " + mp.Price + ")";
                    }
                    break;
                case TabPerf:
                    PerfBisect.RefreshLive();
                    break;
                case TabTools:
                    _liveNpc = NpcDistance.Status();
                    _liveForest = FarForest.Status();
                    _liveBench = FrameBench.Status();
                    break;
            }
        }

        /// <summary>The target buttons' texts, built when a row's name changes.</summary>
        static void LabelPlayers()
        {
            for (int i = 0; i < _players.Count; i++)
            {
                PlayerRow p = _players[i];
                if (p.Label != null && p.LabelName == p.Name && p.LabelMine == p.Mine) continue;
                p.LabelName = p.Name;
                p.LabelMine = p.Mine;
                p.Label = (p.Mine ? Loc.T("я: ", "me: ") : "") + p.Name;
            }
        }

        /// <summary>N8 admin tools on the nearest vehicle: 0 report, 1 as
        /// found, 2 ready. A Mi-8 within 20 m is taken first, else the nearest
        /// ground vehicle within 15 m.</summary>
        static string NearestCondition(int what)
        {
            GameObject heli = PlayerHeli.NearestMachine(20f);
            GameObject car = heli == null ? VehicleCondition.Nearest(15f) : null;
            if (heli == null && car == null)
                return Loc.T("рядом нет техники", "no vehicle nearby");
            if (what != 0 && !RevivalTroopInsertion.MasterClient())
                return Loc.T("только хост", "host only");
            if (heli != null)
            {
                if (what == 1) HeliCondition.MakeFound(heli);
                else if (what == 2) HeliCondition.MakeReady(heli);
                return HeliCondition.Describe(heli);
            }
            if (what == 1) VehicleCondition.Found(car);
            else if (what == 2) VehicleCondition.Ready(car);
            // Fuel and parts arrive by RPC; the report shows this frame's values.
            return VehicleCondition.Describe(car);
        }
        // Keep the native save and HUD path; never assign the obscured Money field.
        static void GiveMoney(string text)
        {
            if (!RevivalPlugin.CfgAdmin.Value || !Zutritt()) return;
            int amount;
            if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out amount) || amount <= 0)
            {
                MoneyMessage(Loc.T("Введите целое число от 1 до 2147483647 (без разделителей).",
                    "Enter a whole number from 1 to 2147483647 (digits only)."));
                return;
            }
            try
            {
                Component inventory = InventarManager() as Component;
                Type type = RevivalPlugin.TypeByName("PlayerStatisticsManager");
                Component stats = inventory == null || type == null ? null : inventory.GetComponent(type);
                MethodInfo viewGetter = stats == null ? null
                    : AccessTools.PropertyGetter(stats.GetType(), "photonView");
                object view = viewGetter == null ? null : viewGetter.Invoke(stats, null);
                MethodInfo mine = view == null ? null : AccessTools.PropertyGetter(view.GetType(), "isMine");
                if (stats == null || mine == null || !(bool)mine.Invoke(view, null)
                    || Field(stats, "_generalStatistic") == null || Field(stats, "_backendManager") == null)
                {
                    MoneyMessage(Loc.T("Сначала войдите в мир и дождитесь загрузки персонажа.",
                        "Enter the world and wait for your character to load first."));
                    return;
                }
                MethodInfo get = AccessTools.Method(type, "GetPlayerMoney", Type.EmptyTypes, null);
                MethodInfo add = AccessTools.Method(type, "AddPlayerMoney",
                    new Type[] { typeof(int), typeof(bool), typeof(bool) }, null);
                if (get == null || get.ReturnType != typeof(int) || add == null)
                {
                    MoneyMessage(Loc.T("API денег недоступен в этой версии игры.",
                        "Money API unavailable in this game version."));
                    return;
                }
                int before = (int)get.Invoke(stats, null);
                if (before < 0 || (long)before + amount > int.MaxValue)
                {
                    MoneyMessage(Loc.T("Сумма превышает предел баланса; деньги не выданы.",
                        "Amount exceeds the balance limit; no money given."));
                    return;
                }
                // IL confirmed: update backend = true, show native reward message = true.
                add.Invoke(stats, new object[] { amount, true, true });
                int after = (int)get.Invoke(stats, null);
                MoneyMessage(Loc.T("Выдано себе: +", "Given to yourself: +") + amount
                    + Loc.T(". Баланс: ", ". Balance: ") + after
                    + Loc.T(". Сохранение запрошено.", ". Save requested."));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Admin money: " + ex);
                MoneyMessage(Loc.T("Ошибка API денег. Проверьте баланс перед повтором; подробности в логе.",
                    "Money API error. Check your balance before retrying; see log for details."));
            }
        }

        static void MoneyMessage(string message)
        {
            _moneyStatus = message;
            Melde(message);
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
            FieldInfo f = FastField.Find(instance.GetType(), name);
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
                    PlayerRow row = _players.Count < _rowPool.Count ? _rowPool[_players.Count] : null;
                    if (row == null) { row = new PlayerRow(); _rowPool.Add(row); }
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
                LabelPlayers();
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
                    // Missing version-dependent aliases are normal, not a
                    // Harmony warning to write once per player per refresh.
                    PropertyInfo property = null;
                    for (Type t = player.GetType(); t != null && property == null; t = t.BaseType)
                        property = t.GetProperty(names[i], BindingFlags.Instance
                            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (property == null) continue;
                    try
                    {
                        object value = property.GetValue(player, null);
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
            // With the panel shut (map teleport, map right-click) the answer
            // would go unseen in the status strip: a toast shows it.
            if (!Win.Open && !string.IsNullOrEmpty(s)) UiKit.Toast(s, UiTone.Info);
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
