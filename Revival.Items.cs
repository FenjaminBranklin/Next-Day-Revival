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

    // ------------------------------------------------------------ Hooks

    public static class LocalizationHook
    {
        public static void Postfix(string __0, ref string __result)
        {
            if (__0 == null || __0.Length < 3 || __0[0] != '$') return;
            if (Regions.Label(__0, ref __result)) return;
            List<ItemDef> items = RevivalPlugin.Items;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDef d = items[i];
                if (__0 == "$" + d.Id + "_Name") { __result = d.Name; return; }
                if (__0 == "$" + d.Id + "_Descr") { __result = d.Descr; return; }
            }
        }
    }

    /// <summary>
    /// Faengt Resources.Load ab und liefert fuer die neuen IDs das selbst
    /// gebaute Prefab statt null.
    ///
    /// Das Spiel laedt fuer eine Waffe ZWEI verschiedene Prefabs:
    ///   PlayerDataPrefabs/Weapons/&lt;id&gt;_Weapon   Modell in Hand und am Ruecken
    ///   &lt;ItemKategorie.PrefabPatch&gt;&lt;id&gt;_Spawn   Inventareintrag
    /// Der Praefix der zweiten Form haengt an der Kategorie, die fuer eine neue
    /// ID nicht stimmen muss - deshalb wird auf die Endung gematcht.
    ///
    /// Dazu kommt die Zielfernrohr-Textur: xmlItemsDataManager liest das
    /// Attribut Scope aus weapons_db.xml und ruft damit Resources.Load(string)
    /// mit anschliessendem isinst Texture2D auf. Der Pfad muss also nur hier
    /// bedient werden, damit die TAC-50 ein eigenes Fadenkreuz bekommt.
    /// </summary>
    public static class ResourceHook
    {
        const string WEAPON_PREFIX = "PlayerDataPrefabs/Weapons/";
        internal static bool Reentry;
        static int _served;

        public static bool Prefix(string path, ref UnityEngine.Object __result)
        {
            if (Reentry || path == null) return true;
            if (RevivalPlugin.CfgCustomItems == null || !RevivalPlugin.CfgCustomItems.Value)
                return true;

            if (path.Equals(RevivalPlugin.ScopePath, StringComparison.OrdinalIgnoreCase))
            {
                Texture2D scope = Assets.Texture("scope50.png", false, false);
                if (scope == null) return true;
                Log(path, "Zielfernrohr");
                __result = scope;
                return false;
            }

            List<ItemDef> items = RevivalPlugin.Items;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDef d = items[i];

                if (d.IsWeapon
                    && path.Equals(WEAPON_PREFIX + d.Id + "_Weapon", StringComparison.OrdinalIgnoreCase))
                {
                    GameObject go = d.Factory.GetModelPrefab();
                    if (go == null) return true;
                    Log(path, "Modell");
                    __result = go;
                    return false;
                }

                // Auf die Endung matchen, weil der Praefix aus der Kategorie kommt
                // und fuer eine neue ID nicht stimmen muss. Das Zeichen davor muss
                // aber ein Trennzeichen sein - sonst wuerde "2050_Spawn" auch auf
                // "12050_Spawn" passen.
                string tail = d.Id + "_Spawn";
                if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
                    && (path.Length == tail.Length || path[path.Length - tail.Length - 1] == '/'))
                {
                    GameObject go = d.Factory.GetSpawnPrefab(
                        path.Substring(0, path.Length - tail.Length));
                    if (go == null) return true;
                    Log(path, "Inventareintrag");
                    __result = go;
                    return false;
                }
            }
            return true;
        }

        static void Log(string path, string what)
        {
            if (_served >= 24) return;
            _served++;
            RevivalPlugin.L.LogInfo("Ausgeliefert #" + _served + " " + what + ": " + path);
        }
    }

    // -------------------------------------------------------------- Assets

    /// <summary>Laedt Mesh und Texturen aus dem assets-Verzeichnis, mit Cache.</summary>
    public static class Assets
    {
        static Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();
        static Dictionary<string, Mesh> _mesh = new Dictionary<string, Mesh>();

        public static Texture2D Texture(string fileName, bool linear, bool mipmaps)
        {
            if (fileName == null) return null;
            if (_tex.ContainsKey(fileName)) return _tex[fileName];
            Texture2D t = null;
            try
            {
                string path = Path.Combine(RevivalPlugin.AssetDir, fileName);
                if (!File.Exists(path))
                {
                    RevivalPlugin.L.LogWarning("Textur fehlt: " + path);
                }
                else
                {
                    t = new Texture2D(4, 4, TextureFormat.RGBA32, mipmaps, linear);
                    t.LoadImage(File.ReadAllBytes(path));
                    t.name = fileName;
                    t.wrapMode = TextureWrapMode.Clamp;
                    t.Apply(mipmaps);
                    RevivalPlugin.L.LogInfo("Textur geladen: " + fileName + " "
                                            + t.width + "x" + t.height);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning(fileName + ": " + ex.Message); }
            _tex[fileName] = t;
            return t;
        }

        /// <summary>
        /// Wie <see cref="Texture"/>, aber still, wenn die Datei nicht da ist.
        /// Fuer Karten, die nur MANCHE Stuecke haben - die Metallic/Gloss-Map
        /// liegt neben MG42 und LAW, nicht neben Gurt, Munition und TAC-50.
        /// `Texture` wuerde dort bei jedem Bau eine Warnung ins Log schreiben,
        /// die nichts meldet ausser "ist so gedacht".
        /// </summary>
        public static Texture2D TextureIfPresent(string fileName)
        {
            if (fileName == null) return null;
            if (_tex.ContainsKey(fileName)) return _tex[fileName];
            if (!File.Exists(Path.Combine(RevivalPlugin.AssetDir, fileName))) return null;
            return Texture(fileName, true, true);
        }

        /// <summary>Liest eine .ndmesh. Prueft die Normalen, bevor sie ins Spiel geht.</summary>
        public static Mesh Load(string fileName)
        {
            if (fileName == null) return null;
            if (_mesh.ContainsKey(fileName)) return _mesh[fileName];
            Mesh mesh = null;
            try
            {
                string path = Path.Combine(RevivalPlugin.AssetDir, fileName);
                if (!File.Exists(path)) throw new FileNotFoundException("fehlt: " + path);
                mesh = Read(path, fileName);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Mesh " + fileName + ": " + ex); }
            _mesh[fileName] = mesh;
            return mesh;
        }

        static Mesh Read(string path, string name)
        {
            FileStream fs = File.OpenRead(path);
            BinaryReader r = new BinaryReader(fs);
            try
            {
                byte[] magic = r.ReadBytes(4);
                if (magic[0] != (byte)'N' || magic[1] != (byte)'D'
                    || magic[2] != (byte)'M' || magic[3] != (byte)'S')
                    throw new Exception("falsche Magic in " + path);
                r.ReadInt32();                                  // Version
                int n = r.ReadInt32();

                Vector3[] verts = new Vector3[n];
                for (int i = 0; i < n; i++)
                    verts[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Vector3[] norms = new Vector3[n];
                int bad = 0;
                for (int i = 0; i < n; i++)
                {
                    Vector3 v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    // Eine Normale der Laenge null wird im Shader zu normalize(0)
                    // und damit zu NaN - im Bild ein Pixel ohne Obergrenze, mit
                    // Bloom ein weisser Fleck. Lieber nach oben zeigen lassen.
                    if (v.sqrMagnitude < 1e-8f) { v = Vector3.up; bad++; }
                    norms[i] = v.normalized;
                }
                Vector2[] uvs = new Vector2[n];
                for (int i = 0; i < n; i++)
                    uvs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());

                int m = r.ReadInt32();
                int[] tris = new int[m];
                for (int i = 0; i < m; i++) tris[i] = r.ReadInt32();

                Mesh mesh = new Mesh();
                mesh.name = name;
                mesh.vertices = verts;
                mesh.normals = norms;
                mesh.uv = uvs;
                mesh.triangles = tris;
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                RevivalPlugin.L.LogInfo("Mesh geladen: " + name + "  " + n + " Vertices, "
                                        + (m / 3) + " Dreiecke, bounds=" + mesh.bounds.size
                                        + (bad > 0 ? ("  ACHTUNG " + bad + " Null-Normalen ersetzt") : ""));
                if (bad > 0)
                    RevivalPlugin.L.LogWarning("Null-Normalen in " + name
                        + " - das Mesh-Skript neu laufen lassen, ndmesh.validate faengt das ab.");
                return mesh;
            }
            finally { r.Close(); fs.Close(); }
        }
    }

    // ------------------------------------------------------------- Factory

    /// <summary>Baut Modell- und Inventarprefab fuer genau ein ItemDef.</summary>
    public class ItemFactory
    {
        readonly ItemDef _def;

        GameObject _model;
        GameObject _spawn;
        Component _mySpawned;
        Mesh _mesh;
        Material _mat;
        bool _modelFailed, _spawnFailed;

        public ItemFactory(ItemDef def) { _def = def; }

        public Component MySpawned { get { return _mySpawned; } }

        /// <summary>
        /// Is the built inventory template still there? Uses Unity's own null
        /// operator on purpose: a destroyed object is not `null` in the CLR
        /// sense, it only compares equal to null - and exactly that state is
        /// what turns GetItemSpawnedScriptByID into a liar.
        /// </summary>
        public bool SpawnAlive
        {
            get { return _spawn != null && _mySpawned != null; }
        }

        /// <summary>Drops the cache so the next call builds a fresh template.</summary>
        public void ForgetSpawn()
        {
            _spawn = null;
            _mySpawned = null;
            _spawnFailed = false;
        }

        // Unterordner von LootSpawn laut ResourceManager. Die Spende kann in
        // jedem davon liegen, und die vom Spiel angefragte Kategorie muss nicht
        // stimmen - alle Schusswaffen liegen unter ASR, Munition unter
        // Ammunation, gefragt wird aber nach Rifles.
        static readonly string[] SPAWN_FOLDERS = {
            "LootSpawn/Ammunation/",
            "LootSpawn/Weapons/ASR/", "LootSpawn/Weapons/Rifles/",
            "LootSpawn/Weapons/Handguns/", "LootSpawn/Weapons/Usable/",
            "LootSpawn/Weapons/OneHandMelees/", "LootSpawn/Weapons/TwoHandMelees/",
            // Clothing and backpacks. A clone of a clothing/backpack donor
            // (helmet 4017, body armour 4316, trousers 4509, backpack 6019, ...)
            // has its inventory prefab under these folders, not under
            // Weapons/Ammunation - so without them GetSpawnPrefab(null) could not
            // find the donor at prewarm or at RepairIfDead time, the inventory
            // template failed to build ("Spende-Inventarprefab in keinem
            // Kandidatenpfad gefunden"), and the given item fell back to the bare
            // donor look. Folders confirmed from research/resource_paths.tsv; the
            // full clothing subfolder set is listed so any clothing clone
            // resolves. Resources.Load is case-insensitive (weapons load via
            // the PascalCase paths above though the asset index is lowercase).
            "LootSpawn/Backpacks/",
            "LootSpawn/Clothes/Head/Special/", "LootSpawn/Clothes/Head/Hats/",
            "LootSpawn/Clothes/Head/Masks/",
            "LootSpawn/Clothes/Body/Jackets/", "LootSpawn/Clothes/Body/Special/",
            "LootSpawn/Clothes/Body/Hands/",
            "LootSpawn/Clothes/Legs/Pants/",
        };

        // ------------------------------------------------------------ Modell

        public GameObject GetModelPrefab()
        {
            if (_model != null) return _model;
            if (_modelFailed || !_def.IsWeapon) return null;
            try
            {
                _model = BuildModel();
                RevivalPlugin.L.LogInfo(_def.Id + ": Modell-Prefab gebaut.");
            }
            catch (Exception ex)
            {
                _modelFailed = true;
                RevivalPlugin.L.LogError(_def.Id + ": Modellbau fehlgeschlagen: " + ex);
            }
            return _model;
        }

        GameObject LoadDonorWeapon()
        {
            try
            {
                ResourceHook.Reentry = true;
                return Resources.Load("PlayerDataPrefabs/Weapons/" + _def.DonorId + "_Weapon")
                       as GameObject;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Spende-Waffe " + _def.DonorId + ": " + ex.Message);
                return null;
            }
            finally { ResourceHook.Reentry = false; }
        }

        GameObject BuildModel()
        {
            GameObject donor = LoadDonorWeapon();
            _mesh = Assets.Load(_def.Mesh);
            if (_mesh == null) throw new Exception("kein Mesh");
            _mat = MakeMaterial(donor);

            // Aufbau exakt wie beim RPD: Wurzel, darunter das Mesh-Objekt, daran
            // haengen die leeren Anker-Transforms.
            GameObject root = new GameObject(_def.Id + "_Weapon");

            GameObject body = new GameObject(_def.Name.Replace(" ", "_") + "_LOD0_W");
            body.transform.SetParent(root.transform, false);
            MeshFilter mf = body.AddComponent<MeshFilter>();
            mf.sharedMesh = _mesh;
            MeshRenderer mr = body.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;

            // Die Anker liegen an der Geometrie des eigenen Meshes. Y-Minimum ist
            // die Muendung, weil alle Meshes des Toolkits entlang -Y nach vorn
            // zeigen - so wie das RPD-Mesh, an dem sie ausgerichtet sind.
            Bounds b = _mesh.bounds;
            float muzzleY = b.min.y;
            MakeAnchor(body.transform, "Muzzle", new Vector3(0f, muzzleY, 0.096f));
            MakeAnchor(body.transform, "MuzzleShoot", new Vector3(0f, muzzleY + 0.074f, 0.097f));
            MakeAnchor(body.transform, "CapsuleSpawner", new Vector3(0.05f, 0.118f, 0.143f));
            MakeAnchor(body.transform, "LHandIKTarget", new Vector3(0f, muzzleY * 0.45f, 0.0f));
            MakeAnchor(body.transform, "LHandIKTargetAiming", new Vector3(0f, muzzleY * 0.45f, 0.0f));

            // ERST jetzt die Komponenten uebernehmen. Vorher gaebe es weder das
            // Mesh-Objekt noch die Anker, und die Verdrahtung legte fuer jeden
            // Verweis einen Platzhalter im Ursprung an statt die echten Anker zu
            // finden - die Waffe zeigte dann auf leere Objekte an falscher Stelle.
            CopyDonorComponents(donor, root);
            ApplyScale(root);

            // Die Originale aus Resources haben m_IsActive = true. Wird die
            // Vorlage selbst deaktiviert, ist auch jede Instantiate-Kopie
            // deaktiviert - die Waffe existiert dann, wird aber nie eingeschaltet.
            // Also bleibt die Wurzel aktiv und haengt unter einem deaktivierten
            // Halter: activeSelf true, activeInHierarchy false.
            GameObject holder = new GameObject("NextDayRevival_Holder_" + _def.Id);
            holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(holder);
            root.transform.SetParent(holder.transform, false);
            root.SetActive(true);

            RevivalPlugin.L.LogInfo(_def.Id + ": activeSelf=" + root.activeSelf
                                    + " activeInHierarchy=" + root.activeInHierarchy);
            return root;
        }

        static void MakeAnchor(Transform parent, string name, Vector3 pos)
        {
            GameObject a = new GameObject(name);
            a.transform.SetParent(parent, false);
            a.transform.localPosition = pos;
            a.transform.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// Nimmt den Shader einer vorhandenen Waffe und setzt genau die
        /// Eigenschaften, die die Spielmaterialien auch setzen.
        ///
        /// Frueher standen hier _Metallic 0.55 plus eine eigene
        /// Metallic/Gloss-Map samt Keyword. Von 1488 Materialien mit _Metallic
        /// in resources.assets hat kein einziges eine solche Map, und 78 Prozent
        /// stehen auf Metallic 0.0 (osnova am RPD: 0.0 / Glossiness 0.6). Ein
        /// fast spiegelndes Metall nimmt seine Farbe vollstaendig aus der
        /// Umgebungsspiegelung - im Menue also aus der Skybox.
        /// </summary>
        Material MakeMaterial(GameObject donor)
        {
            Shader spende = null;
            try
            {
                if (donor != null)
                {
                    MeshRenderer[] rs = donor.GetComponentsInChildren<MeshRenderer>(true);
                    for (int i = 0; i < rs.Length && spende == null; i++)
                        if (rs[i] != null && rs[i].sharedMaterial != null)
                            spende = rs[i].sharedMaterial.shader;
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Spende-Shader: " + ex.Message); }

            // DER SHADER DES PANZERS, NICHT DER DER SPENDE-WAFFE (2026-08-30)
            //
            // Bis hierher wurde der Shader der Spende uebernommen, und genau
            // das war der Grund, warum die Waffen matt blieben, obwohl neben
            // ihnen eine Metallic-Map lag. `research/dump_material.py` ueber
            // alle 1708 Materialien in resources.assets, nach Texturslots
            // sortiert:
            //
            //   Shader 56  Standard                   nur _MetallicGlossMap
            //   Shader 55  Standard (Specular setup)  nur _SpecGlossMap
            //   Shader 57  Standard (Roughness setup) BEIDE
            //
            // Der T-72 erbt vom MTW und landet auf 56. Dort ist die Smoothness
            // der Alphakanal der Metallic-Map - der Panzer sieht deshalb
            // metallisch aus. Die Waffen erben von ihrer Spende und landen auf
            // 57, und dort kommt die Smoothness NICHT aus dem Alpha, sondern
            // als Roughness aus `_SpecGlossMap`. Der Slot war leer, sein
            // Vorgabewert ist "white" - Roughness 1.0, also Smoothness 0 und
            // kein einziges Glanzlicht auf der ganzen Waffe. Das Log sagte die
            // ganze Zeit "Metall-Map=mg42_metal.png", und sie wurde auch
            // benutzt: nur die Haelfte davon, die das Metallic traegt.
            //
            // Also derselbe Shader wie beim Panzer. Die Spende bleibt der
            // Rueckfall, und darunter steht `_SpecGlossMap`, damit auch dieser
            // Rueckfall nicht wieder matt ist.
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = spende;
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new Exception("kein brauchbarer Shader gefunden");
            RevivalPlugin.L.LogInfo(_def.Id + ": Shader " + shader.name
                + " (Spende-Waffe: "
                + (spende == null ? "unbekannt" : spende.name) + ")");

            Texture2D diffuse = Assets.Texture(_def.Diffuse, false, true);
            Material mat = new Material(shader);
            mat.name = _def.Name + "_Material";
            mat.mainTexture = diffuse;
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", diffuse);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_Glossiness"))
                mat.SetFloat("_Glossiness", RevivalPlugin.CfgGlossiness.Value);
            if (mat.HasProperty("_Metallic"))
                mat.SetFloat("_Metallic", RevivalPlugin.CfgMetallic.Value);

            // Metallic/Gloss-Map, wenn neben der Diffuse eine liegt:
            // <stamm>_metal.png. Der Standardshader liest dann Metallic aus
            // deren ROTEM Kanal und Smoothness aus dem ALPHAKANAL; die beiden
            // Skalarwerte darueber sind damit wirkungslos. So haengt die Karte
            // am MTW des Spiels und seit 0.5.2 am T-72 - und erst sie hat den
            // Panzer aufhoeren lassen, neben dem MTW wie bemaltes Papier
            // auszusehen. Bis 0.5.4 stand hier, es gebe keine solche Karte;
            // das war vor der Messung an btr-80a_alb richtig und ist es nicht
            // mehr. Waffen ohne eigene Karte - TAC-50, Gurt, Munition - gehen
            // weiter durch den unteren Zweig und bleiben, wie sie waren.
            string stamm = null;
            if (_def.Diffuse != null && _def.Diffuse.EndsWith("_diffuse.png"))
                stamm = _def.Diffuse.Substring(
                    0, _def.Diffuse.Length - "_diffuse.png".Length);
            string metalName = stamm == null ? null : stamm + "_metal.png";
            string roughName = stamm == null ? null : stamm + "_rough.png";
            Texture2D metal = Assets.TextureIfPresent(metalName);
            if (metal != null && mat.HasProperty("_MetallicGlossMap"))
            {
                mat.SetTexture("_MetallicGlossMap", metal);
                // 1.0, weil die Daempfung schon im Alphakanal der Karte steckt.
                if (mat.HasProperty("_GlossMapScale")) mat.SetFloat("_GlossMapScale", 1.0f);
                // 0 = Smoothness aus dem Alphakanal der Karte, so wie am MTW.
                if (mat.HasProperty("_SmoothnessTextureChannel"))
                    mat.SetFloat("_SmoothnessTextureChannel", 0f);
                mat.EnableKeyword("_METALLICGLOSSMAP");
                // Ohne diese beiden bleibt das Metall stumpf: Glanzlicht und
                // Spiegelung rechnet der Standardshader nur, wenn beide
                // anstehen. Am Panzer stehen sie aus demselben Grund.
                if (mat.HasProperty("_SpecularHighlights")) mat.SetFloat("_SpecularHighlights", 1f);
                if (mat.HasProperty("_GlossyReflections")) mat.SetFloat("_GlossyReflections", 1f);
                mat.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
                mat.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
            }
            else
            {
                // Keine Karte. Falls der geerbte Zustand eine hat oder das
                // Keyword traegt, hier abraeumen.
                if (mat.HasProperty("_MetallicGlossMap")) mat.SetTexture("_MetallicGlossMap", null);
                mat.DisableKeyword("_METALLICGLOSSMAP");
            }

            // `Standard (Roughness setup)` liest die Smoothness NICHT aus dem
            // Alphakanal oben, sondern als Roughness aus diesem Slot - siehe
            // den Block ueber der Shaderwahl. Hat das Material den Slot, ist es
            // dieser Shader, und dann entscheidet allein diese Datei, ob die
            // Waffe glaenzt. `<stamm>_rough.png` ist der umgekehrte Alphakanal
            // derselben Karte, geschrieben von texlib.save_rough_atlas.
            Texture2D rauh = Assets.TextureIfPresent(roughName);
            if (mat.HasProperty("_SpecGlossMap"))
            {
                if (rauh != null)
                {
                    mat.SetTexture("_SpecGlossMap", rauh);
                    mat.EnableKeyword("_SPECGLOSSMAP");
                }
                else
                {
                    // Ohne eigene Karte waere der Vorgabewert "white", also
                    // Roughness 1 und ein vollstaendig mattes Stueck. Den Slot
                    // leeren heisst hier: der Skalarwert entscheidet wieder.
                    mat.SetTexture("_SpecGlossMap", null);
                    mat.DisableKeyword("_SPECGLOSSMAP");
                }
            }

            // Blend-Modus hart auf Opaque: liefert das Spende-Material einen
            // Transparenzmodus mit, wird der sonst mitgeerbt und die Waffe wird
            // stellenweise durchsichtig.
            if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", 1f);   // One
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", 0f);   // Zero
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.black);
            mat.DisableKeyword("_EMISSION");

            int q = 2000 + RevivalPlugin.CfgRenderQueue.Value;   // 2000 = Geometry
            mat.renderQueue = q;

            Texture2D nrm = Assets.Texture(_def.Normal, true, true);
            if (nrm != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", nrm);
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1.0f);
                // Ohne das Keyword wird die Textur zwar gesetzt, aber nicht
                // ausgewertet - die haeufigste Stolperfalle beim Standard-Shader.
                mat.EnableKeyword("_NORMALMAP");
            }

            RevivalPlugin.L.LogInfo(_def.Id + ": Material Metallic="
                + RevivalPlugin.CfgMetallic.Value + " Glossiness="
                + RevivalPlugin.CfgGlossiness.Value + " renderQueue=" + q
                + " Normal=" + (nrm != null)
                + " Metall-Map=" + (metal != null ? metalName : "keine")
                + " Rauheits-Map=" + (rauh != null ? roughName : "keine")
                + " SpecGlossSlot=" + mat.HasProperty("_SpecGlossMap"));
            return mat;
        }

        /// <summary>
        /// PlayerMenuCustomizationManager.WeaponSpineInstanceManager macht am Ende
        /// Instantiate(prefab).GetComponent&lt;ItemTransformManager&gt;().ApplyLocalTransformData().
        /// Ohne diese Komponente ist GetComponent null und der Aufruf wirft eine
        /// NullReferenceException, die den Charakterbildschirm haengen laesst.
        ///
        /// Kopiert werden nur Wertefelder. UnityEngine.Object-Verweise bleiben
        /// aussen vor, sonst zeigt die neue Waffe auf Kindobjekte der Spende.
        /// </summary>
        void CopyDonorComponents(GameObject donor, GameObject target)
        {
            if (donor == null)
            {
                RevivalPlugin.L.LogWarning(_def.Id + ": keine Spende-Waffe - Komponenten fehlen.");
                return;
            }
            foreach (Component src in donor.GetComponents<Component>())
            {
                if (src == null) continue;
                Type t = src.GetType();
                if (t == typeof(Transform)) continue;
                if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;

                Component dst = target.GetComponent(t);
                if (dst == null) dst = target.AddComponent(t);
                if (dst == null)
                {
                    RevivalPlugin.L.LogWarning("AddComponent fehlgeschlagen: " + t.Name);
                    continue;
                }

                int n = 0;
                Type walk = t;
                while (walk != null && walk != typeof(MonoBehaviour) && walk != typeof(object))
                {
                    FieldInfo[] fs = walk.GetFields(BindingFlags.Instance | BindingFlags.Public
                                                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    foreach (FieldInfo f in fs)
                    {
                        Type ft = f.FieldType;
                        if (typeof(UnityEngine.Object).IsAssignableFrom(ft)) continue;
                        if (!ft.IsValueType && ft != typeof(string)) continue;
                        try { f.SetValue(dst, f.GetValue(src)); n++; }
                        catch { }
                    }
                    walk = walk.BaseType;
                }
                RevivalPlugin.L.LogInfo(_def.Id + ": Komponente uebernommen: " + t.Name
                                        + " (" + n + " Wertefelder)");
                WireReferences(src, dst, t, target, 0);
            }
        }

        /// <summary>
        /// Schreibt die gewuenschte Skalierung in die kopierten
        /// Transform-Komponenten.
        ///
        /// ChangeWeaponHelper ruft WeaponTranformManager::ApplyLocalTransformData,
        /// und das setzt localPosition, localEulerAngles UND localScale der
        /// Wurzel aus den Feldern der Komponente. Was am Prefab steht, ist danach
        /// egal. Deshalb muss der Wert dorthin, nicht an transform.localScale.
        /// </summary>
        void ApplyScale(GameObject root)
        {
            float s = RevivalPlugin.CfgScale.Value;
            if (s <= 0f) s = 0.01f;
            root.transform.localScale = new Vector3(s, s, s);

            string[] names = { "WeaponTranformManager", "ItemTransformManager" };
            for (int i = 0; i < names.Length; i++)
            {
                Type t = RevivalPlugin.TypeByName(names[i]);
                if (t == null) continue;
                Component c = root.GetComponent(t);
                if (c == null) continue;
                FieldInfo f = AccessTools.Field(t, "localScale");
                if (f == null || f.FieldType != typeof(Vector3)) continue;
                try
                {
                    f.SetValue(c, new Vector3(s, s, s));
                    RevivalPlugin.L.LogInfo(_def.Id + ": " + names[i] + ".localScale -> " + s);
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning(_def.Id + ": localScale nicht setzbar: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Zieht Objektverweise vom Spende-Prefab auf die eigenen Kindobjekte um:
        /// der Verweis der Spende zeigt auf ein Kindobjekt mit einem bestimmten
        /// Namen, im eigenen Prefab wird ein Kind gleichen Namens gesucht. Fehlt
        /// es - etwa magaz_l oder Zatvor_l - wird ein leeres Objekt angelegt,
        /// damit der Verweis nicht null bleibt.
        /// </summary>
        void WireReferences(object src, object dst, Type t, GameObject root, int depth)
        {
            if (src == null || dst == null || depth > 2) return;
            Type walk = t;
            while (walk != null && walk != typeof(MonoBehaviour) && walk != typeof(object))
            {
                FieldInfo[] fs = walk.GetFields(BindingFlags.Instance | BindingFlags.Public
                                                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (FieldInfo f in fs)
                {
                    Type ft = f.FieldType;
                    object sv;
                    try { sv = f.GetValue(src); } catch { continue; }

                    if (typeof(Transform).IsAssignableFrom(ft) || typeof(GameObject).IsAssignableFrom(ft))
                    {
                        string wanted = null;
                        Transform st = sv as Transform;
                        GameObject sg = sv as GameObject;
                        if (st != null) wanted = st.name;
                        else if (sg != null) wanted = sg.name;
                        if (wanted == null) continue;

                        Transform mine = FindOrCreate(root, wanted);
                        try
                        {
                            if (typeof(Transform).IsAssignableFrom(ft)) f.SetValue(dst, mine);
                            else f.SetValue(dst, mine.gameObject);
                            if (RevivalPlugin.CfgVerbose.Value)
                                RevivalPlugin.L.LogInfo("      " + f.Name + " -> " + wanted);
                        }
                        catch (Exception ex)
                        {
                            RevivalPlugin.L.LogWarning("      " + f.Name + " nicht setzbar: " + ex.Message);
                        }
                    }
                    else if (sv != null && !ft.IsPrimitive && !ft.IsEnum && ft != typeof(string)
                             && !typeof(UnityEngine.Object).IsAssignableFrom(ft)
                             && (ft.Namespace == null || (!ft.Namespace.StartsWith("System")
                                                          && !ft.Namespace.StartsWith("UnityEngine"))))
                    {
                        // Verschachtelte serialisierbare Klasse, etwa WeaponTransforms
                        object dv;
                        try { dv = f.GetValue(dst); } catch { continue; }
                        if (dv == null)
                        {
                            try { dv = Activator.CreateInstance(ft); f.SetValue(dst, dv); }
                            catch { continue; }
                        }
                        WireReferences(sv, dv, ft, root, depth + 1);
                    }
                }
                walk = walk.BaseType;
            }
        }

        Transform FindOrCreate(GameObject root, string name)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform tr in all)
                if (tr.name == name) return tr;
            if (root.name == name) return root.transform;

            Transform parent = root.transform;
            foreach (Transform tr in all)
                if (tr.name.EndsWith("_LOD0_W")) { parent = tr; break; }

            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            if (RevivalPlugin.CfgVerbose.Value)
                RevivalPlugin.L.LogInfo("      (Platzhalter angelegt: " + name + ")");
            return go.transform;
        }

        // -------------------------------------------------------- Inventar

        /// <summary>
        /// Der Inventareintrag. Geklont vom Spende-Prefab, dann wird in jeder
        /// Komponente das Feld ItemID auf die neue ID gezogen - sonst legt
        /// AddWeaponItemFromValues das Item unter der Spende-ID ab.
        /// </summary>
        public GameObject GetSpawnPrefab(string requestedPrefix)
        {
            if (_spawn != null) return _spawn;
            if (_spawnFailed) return null;
            try
            {
                GameObject donor = LoadDonorSpawn(requestedPrefix);
                if (donor == null)
                {
                    _spawnFailed = true;
                    RevivalPlugin.L.LogError(_def.Id
                        + ": Spende-Inventarprefab in keinem Kandidatenpfad gefunden.");
                    return null;
                }

                // A Resources prefab is dormant. Our runtime clone used to be
                // active for one frame before it was parented below the hidden
                // holder. Its PhotonView.Awake registered that TEMPLATE as a
                // scene object. When Photon later cloned it for a real drop,
                // both objects carried the same view id; PUN destroyed the
                // template and cached the dead object. The next item then did
                // not drop, and a death drop threw before the respawn UI.
                //
                // Clone while the donor is inactive, then make activeSelf true
                // only below an inactive parent. The template never becomes
                // activeInHierarchy; the real world clone is the first object
                // whose PhotonView wakes up.
                bool donorActive = donor.activeSelf;
                if (donorActive) donor.SetActive(false);
                GameObject clone = null;
                try { clone = UnityEngine.Object.Instantiate(donor); }
                finally { if (donorActive) donor.SetActive(true); }
                if (clone == null) throw new Exception("Spende konnte nicht geklont werden");
                clone.name = _def.Id + "_Spawn";

                Texture2D icon = Assets.Texture(_def.Icon, false, false);
                Texture2D wicon = Assets.Texture(_def.WeaponIcon, false, false);

                int patched = 0;
                Component[] all = clone.GetComponentsInChildren<Component>(true);
                foreach (Component c in all)
                {
                    if (c == null) continue;
                    Type t = c.GetType();
                    while (t != null && t != typeof(object))
                    {
                        FieldInfo fid = t.GetField("ItemID", BindingFlags.Instance
                                                   | BindingFlags.Public | BindingFlags.NonPublic
                                                   | BindingFlags.DeclaredOnly);
                        if (fid == null) { t = t.BaseType; continue; }

                        if (RevivalPlugin.CfgVerbose.Value) DumpFieldNames(t);

                        try
                        {
                            if (fid.FieldType == typeof(int)) fid.SetValue(c, _def.Id);
                            else fid.SetValue(c, Convert.ChangeType(_def.Id, fid.FieldType));
                            patched++;
                        }
                        catch (Exception ex)
                        {
                            RevivalPlugin.L.LogWarning("  ItemID nicht setzbar: " + ex.Message);
                        }

                        SetIcon(c, t, "ItemIcon", icon);
                        SetIcon(c, t, "WeaponIcon", wicon != null ? wicon : icon);
                        if (_mySpawned == null && t.Name == "ItemSpawned") _mySpawned = c;
                        SetInt(c, t, "Bullets", _def.Bullets);
                        if (_def.ClipItemId > 0) SetInt(c, t, "ClipItemID", _def.ClipItemId);
                        SetFloat(c, t, "ItemWeight", _def.Weight);
                        break;
                    }
                }
                if (patched == 0)
                    RevivalPlugin.L.LogWarning(_def.Id
                        + ": kein ItemID-Feld im Inventarprefab gefunden.");

                SwapGeometry(clone);

                GameObject holder = new GameObject("NextDayRevival_SpawnHolder_" + _def.Id);
                holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(holder);
                clone.transform.SetParent(holder.transform, false);
                clone.SetActive(true);

                _spawn = clone;
                SpawnWatch.Remember(_def.Id, clone, holder);
                RevivalPlugin.L.LogInfo(_def.Id + ": Inventarprefab gebaut ("
                    + all.Length + " Komponenten, " + patched + "x ItemID gesetzt).");
                return _spawn;
            }
            catch (Exception ex)
            {
                _spawnFailed = true;
                RevivalPlugin.L.LogError(_def.Id + ": Inventarprefab fehlgeschlagen: " + ex);
                return null;
            }
        }

        GameObject LoadDonorSpawn(string requestedPrefix)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(requestedPrefix))
                candidates.Add(requestedPrefix + _def.DonorId + "_Spawn");
            for (int i = 0; i < SPAWN_FOLDERS.Length; i++)
                candidates.Add(SPAWN_FOLDERS[i] + _def.DonorId + "_Spawn");

            try
            {
                ResourceHook.Reentry = true;
                for (int i = 0; i < candidates.Count; i++)
                {
                    GameObject go = Resources.Load(candidates[i]) as GameObject;
                    if (go != null)
                    {
                        RevivalPlugin.L.LogInfo(_def.Id + ": Spende-Inventarprefab "
                                                + candidates[i]);
                        return go;
                    }
                    if (RevivalPlugin.CfgVerbose.Value)
                        RevivalPlugin.L.LogInfo("  Kandidat leer: " + candidates[i]);
                }
            }
            finally { ResourceHook.Reentry = false; }
            return null;
        }

        /// <summary>
        /// Der Klon traegt bis hier die Geometrie der Spende. Ohne Austausch liegt
        /// im Rucksack sichtbar das Spende-Item. Also alle MeshFilter des Klons
        /// auf das eigene Mesh umhaengen - Hierarchie, Skalierung und Ausrichtung
        /// des Original-Prefabs bleiben dabei erhalten. Genau deshalb sind die
        /// Meshes der Munitionsitems in ammo_mesh.py auf die Ausdehnung UND den
        /// Mittelpunkt von magaz_l gezogen: der Collider des Originals bleibt.
        /// </summary>
        void SwapGeometry(GameObject clone)
        {
            Mesh mesh = _mesh;
            if (mesh == null) mesh = Assets.Load(_def.Mesh);
            if (mesh == null) return;

            Material mat = _mat;
            if (mat == null)
            {
                mat = MakeMaterial(clone);
                _mat = mat;
            }

            int swapped = 0;
            int hidden = 0;
            MeshFilter[] mfs = clone.GetComponentsInChildren<MeshFilter>(true);
            MeshFilter primary = null;
            foreach (MeshFilter mf in mfs)
            {
                if (mf == null) continue;
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (primary != null)
                {
                    mf.sharedMesh = null;
                    if (mr != null) mr.enabled = false;
                    hidden++;
                    continue;
                }
                primary = mf;
                mf.sharedMesh = mesh;
                if (mr != null && mat != null)
                {
                    Material[] mats = new Material[Math.Max(1, mr.sharedMaterials.Length)];
                    for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                    mr.sharedMaterials = mats;
                    mr.enabled = true;
                }
                swapped++;
            }
            SkinnedMeshRenderer[] sks = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer sk in sks)
                if (sk != null) { sk.sharedMesh = null; sk.enabled = false; hidden++; }

            // A donor with three LOD renderers was the visible "three copies"
            // report. Keep one complete custom mesh and prevent LODGroup from
            // re-enabling the now empty donor renderers at distance.
            LODGroup[] lods = clone.GetComponentsInChildren<LODGroup>(true);
            for (int i = 0; i < lods.Length; i++)
                if (lods[i] != null) lods[i].enabled = false;

            RevivalPlugin.L.LogInfo(_def.Id + ": Inventar-Geometrie ersetzt ("
                + swapped + " sichtbarer MeshFilter, " + hidden
                + " Spender-Renderer stillgelegt, Skalierung "
                + clone.transform.localScale + ")");
        }

        static void DumpFieldNames(Type t)
        {
            RevivalPlugin.L.LogInfo("Inventarkomponente " + t.Name + ":");
            FieldInfo[] every = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (FieldInfo any in every)
                RevivalPlugin.L.LogInfo("    " + any.FieldType.Name + " " + any.Name);
        }

        static void SetInt(Component c, Type t, string name, int value)
        {
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f == null) return;
                if (f.FieldType == typeof(int)) f.SetValue(c, value);
                else f.SetValue(c, Convert.ChangeType(value, f.FieldType));
                RevivalPlugin.L.LogInfo("  " + name + " -> " + value);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("  " + name + ": " + ex.Message); }
        }

        static void SetFloat(Component c, Type t, string name, float value)
        {
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f == null) return;
                if (f.FieldType == typeof(float)) f.SetValue(c, value);
                else f.SetValue(c, Convert.ChangeType(value, f.FieldType));
                RevivalPlugin.L.LogInfo("  " + name + " -> " + value);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("  " + name + ": " + ex.Message); }
        }

        /// <summary>
        /// Das Feld heisst ItemIcon oder WeaponIcon; ob es ein Sprite oder eine
        /// Texture haelt, entscheidet sich erst zur Laufzeit. Beides wird bedient.
        /// </summary>
        static void SetIcon(Component c, Type t, string name, Texture2D tex)
        {
            if (tex == null) return;
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f == null) return;

                if (typeof(Sprite).IsAssignableFrom(f.FieldType))
                {
                    Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                              new Vector2(0.5f, 0.5f), 100f);
                    sp.name = tex.name;
                    f.SetValue(c, sp);
                    RevivalPlugin.L.LogInfo("  " + name + " -> Sprite " + tex.name);
                }
                else if (typeof(Texture).IsAssignableFrom(f.FieldType))
                {
                    f.SetValue(c, tex);
                    RevivalPlugin.L.LogInfo("  " + name + " -> Texture " + tex.name);
                }
                else
                {
                    RevivalPlugin.L.LogWarning("  " + name + " hat unerwarteten Typ "
                                               + f.FieldType.FullName);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("  " + name + ": " + ex.Message); }
        }
    }

    // ------------------------------------------------------------ Registry

    /// <summary>
    /// Traegt die neuen IDs in die Item-Datenbank ein.
    ///
    /// Es gibt MEHRERE Nachschlagewerke, nicht nur eines:
    ///   GetItemByID              -> DeserealizedSpawnDictionary
    ///   GetItemSpawnedScriptByID -> ItemSpawnedDictionary
    ///   GetRandomItemByCategory  -> SpawnCategoriesDictionary
    /// Der Waffenslot benutzt das erste, der Rucksack das zweite, der Loot das
    /// dritte. Wer nur eines befuellt, bekommt "ItemSpawned is null!" beim
    /// Umlegen und das Item verschwindet.
    /// </summary>
    /// <summary>
    /// Says who destroys one of our inventory templates.
    ///
    /// The entry the game reads out of ItemSpawnedDictionary is a COMPONENT on
    /// an object this plugin built. Destroy that object and the entry becomes
    /// Unity's "fake null": the id still has a key, the value still looks like
    /// an object, and every lookup answers null. The item is then unavailable
    /// for the rest of the session - "ItemSpawned is null!".
    ///
    /// A prefix on Object.Destroy is the only place where the CALLER is still
    /// on the stack; in OnDestroy it is long gone, because Unity destroys at
    /// the end of the frame.
    /// </summary>
    public static class SpawnWatch
    {
        class Vorlage
        {
            public int Id;
            public GameObject Go;
            public GameObject Holder;
        }

        static readonly List<Vorlage> _vorlagen = new List<Vorlage>();

        public static void Remember(int id, GameObject go, GameObject holder)
        {
            for (int i = 0; i < _vorlagen.Count; i++)
                if (_vorlagen[i].Id == id) { _vorlagen.RemoveAt(i); break; }
            Vorlage v = new Vorlage();
            v.Id = id; v.Go = go; v.Holder = holder;
            _vorlagen.Add(v);
        }

        public static void DestroyPrefix(UnityEngine.Object __0)
        {
            if (_vorlagen.Count == 0 || __0 == null) return;
            try
            {
                GameObject go = __0 as GameObject;
                if (go == null)
                {
                    Component c = __0 as Component;
                    if (c == null) return;
                    go = c.gameObject;
                }
                for (int i = 0; i < _vorlagen.Count; i++)
                {
                    Vorlage v = _vorlagen[i];
                    if (!Betrifft(go, v)) continue;
                    RevivalPlugin.L.LogWarning("VORLAGE " + v.Id + " wird zerstoert ("
                        + __0.GetType().Name + " \"" + __0.name + "\"). Ohne sie ist das "
                        + "Item bis zum Neustart weg. Aufrufer:");
                    RevivalPlugin.L.LogWarning(Environment.StackTrace);
                    return;
                }
            }
            catch { }
        }

        /// <summary>Is `go` the template itself, its holder, or a child of it?</summary>
        static bool Betrifft(GameObject go, Vorlage v)
        {
            Transform t = go.transform;
            while (t != null)
            {
                if (ReferenceEquals(t.gameObject, v.Go)
                    || ReferenceEquals(t.gameObject, v.Holder)) return true;
                t = t.parent;
            }
            return false;
        }
    }

    public static class Registry
    {
        // MarketplaceObject.RecalculateMarketItemsCategory APPENDS every rank
        // list (A-D) the current trader sells (its MarketItemRanksSelling) into
        // ONE un-deduplicated result list, sorts by ItemID and shows it (RE 30).
        // A custom item that sits in several of A-D therefore appears once PER
        // rank the trader sells - the doubled/tripled entries the player saw. A
        // vanilla item lives in exactly one list; a custom item must too. It is
        // placed only in rank 0 / list A, the base tier a trader with an empty
        // rank list is normalized to, so it shows up once and stays as widely
        // available as a common vanilla good. The full A-D name set is still
        // used to STRIP an id from every list when cleaning stale registrations.
        static readonly string[] MarketRankFields = new string[] {
            "MarketItemsA", "MarketItemsB", "MarketItemsC", "MarketItemsD" };

        // Kept separate from ItemDef on purpose. The antenna rework owns the
        // definitions for 2055-2057; once those definitions are integrated,
        // this table registers them without either side recreating the other.
        static readonly int[] ShopItemIds = new int[] {
            1160, 1161, 1162, 1163, 2050, 2051, 2053, 2054, 2055, 2056, 2057 };
        static readonly int[] ShopBuyPrices = new int[] {
            18000, 30000, 25000, 10000, 4000, 4500,
            5000, 100000, 50000, 2500, 25000 };

        public static void RegisterAll()
        {
            try
            {
                object db = GetDb();
                if (db == null) return;
                Type t = db.GetType();

                FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                                 | BindingFlags.NonPublic);
                int filled = 0, total = 0;
                foreach (FieldInfo f in fields)
                {
                    IDictionary dic = GetDic(db, f);
                    if (dic == null) continue;
                    if (f.Name == "SpawnCategoriesDictionary") continue;   // andere Schluessel
                    total++;

                    List<ItemDef> items = RevivalPlugin.Items;
                    int done = 0;
                    for (int i = 0; i < items.Count; i++)
                    {
                        ItemDef d = items[i];
                        if (dic.Contains(d.Id)) { done++; continue; }
                        if (!dic.Contains(d.DonorId)) continue;

                        object value = dic[d.DonorId];
                        string via = "Spende-Verweis";
                        Component mine = d.Factory.MySpawned;
                        if (mine != null)
                        {
                            Type want = value == null ? null : value.GetType();
                            if (want == null || want.IsInstanceOfType(mine))
                            {
                                value = mine;
                                via = "eigenes ItemSpawned";
                            }
                        }
                        try
                        {
                            dic[d.Id] = value;
                            done++;
                            RevivalPlugin.L.LogInfo("Item-DB " + f.Name + ": " + d.Id
                                                    + " angelegt (" + via + ").");
                        }
                        catch (Exception ex)
                        {
                            RevivalPlugin.L.LogWarning("Item-DB " + f.Name + ": " + d.Id
                                                       + " nicht eintragbar: " + ex.Message);
                        }
                    }
                    if (done == items.Count) filled++;
                    RevivalPlugin.L.LogInfo("Item-DB " + f.Name + ": " + dic.Count
                                            + " Eintraege, " + done + "/" + items.Count
                                            + " eigene vorhanden.");
                }
                RevivalPlugin.L.LogInfo("Item-DB: " + filled + " von " + total
                                        + " Woerterbuechern vollstaendig.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Item-DB-Registrierung: " + ex); }
        }

        /// <summary>
        /// Extends the game's MarketplaceDB rather than introducing another
        /// shop path. MarketItem.Price is what buying reads; the parallel price
        /// dictionary is what GenerateSellItemPrice reads before multiplying by
        /// this trader's BuyPlayerItemsPercent and converting the positive float
        /// to int (therefore truncating/flooring it).
        /// </summary>
        public static void RegisterMarketplace()
        {
            try
            {
                Type marketType = RevivalPlugin.TypeByName("MarketplaceDB");
                MethodInfo current = marketType == null ? null
                    : AccessTools.PropertyGetter(marketType, "current");
                object market = current == null ? null : current.Invoke(null, null);
                if (market == null)
                {
                    RevivalPlugin.L.LogWarning("Marketplace: MarketplaceDB.current fehlt.");
                    return;
                }

                FieldInfo categoryField = AccessTools.Field(market.GetType(),
                    "MarketItemsCategoriesDictionary");
                FieldInfo priceField = AccessTools.Field(market.GetType(),
                    "MarketItemsPriceDictionary");
                IDictionary categories = categoryField == null
                    ? null : GetDic(market, categoryField);
                IDictionary prices = priceField == null
                    ? null : GetDic(market, priceField);
                if (categories == null || prices == null)
                {
                    RevivalPlugin.L.LogWarning("Marketplace: Kategorien oder Preistabelle fehlen.");
                    return;
                }

                // 2052 is a display/unused round. Remove stale registrations as
                // well as declining to add it, so it cannot be bought or sold.
                ItemDef display = FindUniqueDefinition(2052);
                if (display != null)
                    SetSpawnMarketData(display.Factory.MySpawned, false, 0);
                int excluded = RemoveMarketItem(categories, 2052, null);
                if (prices.Contains(2052))
                {
                    prices.Remove(2052);
                    excluded++;
                }
                if (excluded > 0)
                    RevivalPlugin.L.LogWarning("Marketplace: " + excluded
                        + " alte Registrierung(en) fuer ausgeschlossene 2052 entfernt.");

                int registered = 0;
                int pending = 0;
                for (int i = 0; i < ShopItemIds.Length; i++)
                {
                    int id = ShopItemIds[i];
                    int buyPrice = ShopBuyPrices[i];
                    ItemDef def = FindUniqueDefinition(id);
                    if (def == null)
                    {
                        pending++;
                        RevivalPlugin.L.LogWarning("Marketplace: ItemDef " + id
                            + " fehlt; Shop-Metadaten warten auf die Definition.");
                        continue;
                    }

                    object categoryKey;
                    object template;
                    bool existing = FindMarketItem(categories, id,
                        out categoryKey, out template);
                    if (!existing && !FindMarketItem(categories, def.DonorId,
                                                     out categoryKey, out template))
                    {
                        RevivalPlugin.L.LogWarning("Marketplace: weder " + id
                            + " noch Spende " + def.DonorId
                            + " in einer Haendlerkategorie gefunden.");
                        continue;
                    }

                    Texture2D icon = Assets.Texture(def.Icon, false, false);
                    bool placed = PutInMarket(categories, categoryKey,
                        template, id, buyPrice, icon);
                    if (!placed)
                    {
                        RevivalPlugin.L.LogWarning("Marketplace: " + id
                            + " konnte nicht in Rang A eingetragen werden.");
                        continue;
                    }

                    prices[id] = buyPrice;
                    SetSpawnMarketData(def.Factory.MySpawned, true, buyPrice);
                    registered++;
                    RevivalPlugin.L.LogInfo("Marketplace: " + id + " fuer "
                        + buyPrice + " in Rang A (Rang 0) registriert"
                        + (existing ? " (Mehrfacheintraege bereinigt)."
                                    : " (Kategorie der Spende " + def.DonorId + ")."));
                }

                RevivalPlugin.L.LogInfo("Marketplace: " + registered + "/"
                    + ShopItemIds.Length + " Shop-Items registriert, " + pending
                    + " Definition(en) noch nicht integriert; 2052 ausgeschlossen.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Marketplace-Registrierung: " + ex);
            }
        }

        static ItemDef FindUniqueDefinition(int id)
        {
            ItemDef found = null;
            int count = 0;
            List<ItemDef> items = RevivalPlugin.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Id != id) continue;
                if (found == null) found = items[i];
                count++;
            }
            if (count > 1)
            {
                RevivalPlugin.L.LogError("Marketplace: doppelte ItemDef fuer " + id
                    + " (" + count + "); nicht registriert.");
                return null;
            }
            return found;
        }

        static bool FindMarketItem(IDictionary categories, int id,
                                   out object categoryKey, out object entry)
        {
            categoryKey = null;
            entry = null;
            foreach (DictionaryEntry category in categories)
            {
                object holder = category.Value;
                if (holder == null) continue;
                for (int rank = 0; rank < MarketRankFields.Length; rank++)
                {
                    IList list = MarketRankList(holder, MarketRankFields[rank]);
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (EntryId(list[i]) != id) continue;
                        categoryKey = category.Key;
                        entry = list[i];
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Registers `id` in rank 0 (list A) of its canonical category and
        /// NOWHERE else. First strips the id from every category and every A-D
        /// rank list - so any stale multi-list registration from an earlier run
        /// that made the item show up two- or threefold at multi-rank traders is
        /// cleaned up - keeping at most the single entry already in list A, then
        /// ensures exactly that one entry exists and is filled in. Returns true
        /// when the one entry carries the id and price.
        /// </summary>
        static bool PutInMarket(IDictionary categories, object categoryKey,
                                object template, int id, int price,
                                Texture2D icon)
        {
            object keep = null;
            object canonHolder = null;
            foreach (DictionaryEntry category in categories)
            {
                object holder = category.Value;
                if (holder == null) continue;
                bool canonical = object.Equals(category.Key, categoryKey);
                if (canonical) canonHolder = holder;
                for (int rank = 0; rank < MarketRankFields.Length; rank++)
                {
                    IList list = MarketRankList(holder, MarketRankFields[rank]);
                    if (list == null) continue;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (EntryId(list[i]) != id) continue;
                        // Keep exactly one: the entry already in list A of the
                        // canonical category. Drop every other occurrence.
                        if (canonical && rank == 0 && keep == null) keep = list[i];
                        else list.RemoveAt(i);
                    }
                }
            }

            IList target = canonHolder == null
                ? null : MarketRankList(canonHolder, MarketRankFields[0]);
            if (target == null) return false;
            if (keep == null)
            {
                keep = CloneEntry(template, id);
                if (keep == null) return false;
                target.Add(keep);
            }
            bool idSet = SetMarketField(keep, "ItemID", id);
            bool priceSet = SetMarketField(keep, "Price", price);
            SetMarketField(keep, "Rank", 0);
            SetMarketField(keep, "Category", categoryKey);
            if (icon != null) SetMarketField(keep, "Icon", icon);
            return idSet && priceSet;
        }

        /// <summary>
        /// Removes an item from every market rank except, optionally, one
        /// category. Used both to consolidate duplicate mod registrations and
        /// to keep 2052 out of the marketplace completely.
        /// </summary>
        static int RemoveMarketItem(IDictionary categories, int id, object keepCategory)
        {
            int removed = 0;
            foreach (DictionaryEntry category in categories)
            {
                if (keepCategory != null && object.Equals(category.Key, keepCategory))
                    continue;
                object holder = category.Value;
                if (holder == null) continue;
                for (int rank = 0; rank < MarketRankFields.Length; rank++)
                {
                    IList list = MarketRankList(holder, MarketRankFields[rank]);
                    if (list == null) continue;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (EntryId(list[i]) != id) continue;
                        list.RemoveAt(i);
                        removed++;
                    }
                }
            }
            return removed;
        }

        static IList MarketRankList(object holder, string fieldName)
        {
            FieldInfo field = FindField(holder.GetType(), fieldName);
            if (field == null) return null;
            try { return field.GetValue(holder) as IList; }
            catch { return null; }
        }

        static void SetSpawnMarketData(Component spawned, bool visible, int price)
        {
            if (spawned == null) return;
            SetMarketField(spawned, "ShowInMarket", visible);
            SetMarketField(spawned, "Price", price);
        }

        /// <summary>
        /// MarketItem.Price and ItemSpawned.ShowInMarket use CodeStage's
        /// ObscuredInt/ObscuredBool, while IDs and dictionary prices are plain
        /// integers. Their public implicit operators are the one safe common
        /// conversion path.
        /// </summary>
        static bool SetMarketField(object target, string fieldName, object value)
        {
            if (target == null) return false;
            try
            {
                FieldInfo field = FindField(target.GetType(), fieldName);
                if (field == null) return false;
                object converted = value;
                if (value != null && !field.FieldType.IsInstanceOfType(value))
                {
                    if (field.FieldType.IsEnum)
                    {
                        converted = Enum.ToObject(field.FieldType, value);
                    }
                    else
                    {
                        MethodInfo op = field.FieldType.GetMethod("op_Implicit",
                            BindingFlags.Public | BindingFlags.Static, null,
                            new Type[] { value.GetType() }, null);
                        converted = op == null
                            ? Convert.ChangeType(value, field.FieldType)
                            : op.Invoke(null, new object[] { value });
                    }
                }
                field.SetValue(target, converted);
                return true;
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Marketplace-Feld " + fieldName + ": "
                        + ex.Message);
                return false;
            }
        }

        static FieldInfo FindField(Type type, string fieldName)
        {
            while (type != null && type != typeof(object))
            {
                FieldInfo field = type.GetField(fieldName, BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        /// <summary>
        /// Haengt die neuen IDs in die Loot-Kategorien, in denen auch das
        /// Spende-Item steht. GetRandomItemByCategory zieht aus
        /// SpawnCategoriesDictionary[kategorie].SpawnPrefabs - liegt die neue ID
        /// dort nicht drin, wird sie in der Welt nie gespawnt.
        /// </summary>
        public static void AddToLootTables()
        {
            try
            {
                object db = GetDb();
                if (db == null) return;
                FieldInfo f = AccessTools.Field(db.GetType(), "SpawnCategoriesDictionary");
                IDictionary dic = f == null ? null : GetDic(db, f);
                if (dic == null) { RevivalPlugin.L.LogWarning("Loot: keine Kategorietabelle."); return; }

                List<ItemDef> items = RevivalPlugin.Items;
                int added = 0;
                foreach (object key in dic.Keys)
                {
                    object table = dic[key];
                    if (table == null) continue;
                    FieldInfo fp = AccessTools.Field(table.GetType(), "SpawnPrefabs");
                    IList list = fp == null ? null : fp.GetValue(table) as IList;
                    if (list == null) continue;

                    for (int i = 0; i < items.Count; i++)
                    {
                        ItemDef d = items[i];
                        object donorEntry = null;
                        bool already = false;
                        for (int k = 0; k < list.Count; k++)
                        {
                            int id = EntryId(list[k]);
                            if (id == d.Id) { already = true; break; }
                            if (id == d.DonorId) donorEntry = list[k];
                        }
                        if (already || donorEntry == null) continue;

                        object copy = CloneEntry(donorEntry, d.Id);
                        if (copy == null) continue;
                        try { list.Add(copy); added++; }
                        catch (Exception ex)
                        {
                            RevivalPlugin.L.LogWarning("Loot: " + d.Id + " nicht eintragbar: "
                                                       + ex.Message);
                        }
                    }
                }
                RevivalPlugin.L.LogInfo("Loot-Kategorien: " + added + " Eintraege ergaenzt ("
                                        + dic.Count + " Kategorien geprueft).");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Loot-Kategorien: " + ex); }
        }

        static int EntryId(object entry)
        {
            if (entry == null) return -1;
            FieldInfo f = AccessTools.Field(entry.GetType(), "ItemID");
            if (f == null) return -1;
            try { return Convert.ToInt32(f.GetValue(entry)); }
            catch { return -1; }
        }

        static object CloneEntry(object src, int newId)
        {
            try
            {
                Type t = src.GetType();
                object copy = Activator.CreateInstance(t);
                FieldInfo[] fs = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                                             | BindingFlags.NonPublic);
                foreach (FieldInfo f in fs)
                {
                    try { f.SetValue(copy, f.GetValue(src)); }
                    catch { }
                }
                FieldInfo fid = AccessTools.Field(t, "ItemID");
                if (fid != null)
                {
                    if (fid.FieldType == typeof(int)) fid.SetValue(copy, newId);
                    else fid.SetValue(copy, Convert.ChangeType(newId, fid.FieldType));
                }
                // Der Pfad zeigt weiter auf das Spende-Prefab. Unser
                // Resources.Load-Prefix matcht aber auf die Endung <id>_Spawn,
                // also muss die ID auch im Pfad stehen. FindField statt
                // AccessTools.Field: MarketItem-Klone haben kein Path-Feld, und
                // AccessTools.Field wuerde das pro Eintrag als Warnung loggen.
                FieldInfo fp = FindField(t, "Path");
                if (fp != null && fp.FieldType == typeof(string))
                {
                    string p = fp.GetValue(src) as string;
                    if (!string.IsNullOrEmpty(p))
                        fp.SetValue(copy, p.Replace(EntryIdString(src), newId.ToString()));
                }
                return copy;
            }
            catch { return null; }
        }

        static string EntryIdString(object entry)
        {
            int id = EntryId(entry);
            return id < 0 ? "" : id.ToString();
        }

        static IDictionary GetDic(object db, FieldInfo f)
        {
            object holder;
            try { holder = f.GetValue(db); } catch { return null; }
            if (holder == null) return null;
            PropertyInfo pDic = AccessTools.Property(holder.GetType(), "Dic");
            if (pDic == null) return null;
            try { return pDic.GetValue(holder, null) as IDictionary; }
            catch { return null; }
        }

        static readonly Dictionary<int, int> _repairs = new Dictionary<int, int>();

        /// <summary>
        /// Puts an entry back that has died under us.
        ///
        /// Called right before the game looks an id up. If the template behind
        /// that id is gone, it is rebuilt and written into the dictionaries
        /// again - the game then gets a living object instead of the fake null
        /// that made it say "ItemSpawned is null!" (E-029).
        ///
        /// Three attempts per id, then it gives up: a template that cannot be
        /// built will not become buildable by trying harder, and a repair loop
        /// inside a lookup would be worse than the missing item.
        /// </summary>
        public static void RepairIfDead(int id)
        {
            ItemDef d = null;
            List<ItemDef> items = RevivalPlugin.Items;
            for (int i = 0; i < items.Count; i++)
                if (items[i].Id == id) { d = items[i]; break; }
            if (d == null || d.Factory.SpawnAlive) return;

            int mal = 0;
            if (_repairs.TryGetValue(id, out mal) && mal >= 3) return;
            _repairs[id] = mal + 1;

            RevivalPlugin.L.LogWarning("Item " + id + ": Inventarvorlage ist weg - "
                + "wird neu gebaut (Versuch " + (mal + 1) + " von 3).");
            d.Factory.ForgetSpawn();
            if (d.Factory.GetSpawnPrefab(null) == null)
            {
                RevivalPlugin.L.LogError("Item " + id + ": Vorlage laesst sich nicht "
                    + "neu bauen.");
                return;
            }

            object db = GetDb();
            if (db == null) return;
            Component mine = d.Factory.MySpawned;
            int n = 0;
            FieldInfo[] fields = db.GetType().GetFields(BindingFlags.Instance
                                     | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo f in fields)
            {
                if (f.Name == "SpawnCategoriesDictionary") continue;
                IDictionary dic = GetDic(db, f);
                if (dic == null) continue;
                object value = dic.Contains(d.DonorId) ? dic[d.DonorId] : null;
                if (mine != null && (value == null || value.GetType().IsInstanceOfType(mine)))
                    value = mine;
                if (value == null) continue;
                try { dic[d.Id] = value; n++; }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("  " + f.Name + ": " + ex.Message);
                }
            }
            RevivalPlugin.L.LogInfo("Item " + id + ": Vorlage steht wieder, "
                + n + " Woerterbuecher nachgetragen.");
        }

        /// <summary>
        /// Prefix on ItemSpawnCategoriesDB::GetItemSpawnedScriptByID - the one
        /// place every path goes through: giving, picking up, spawning loot.
        /// </summary>
        public static void LookupPrefix(int __0)
        {
            try { RepairIfDead(__0); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Vorlagenpruefung: " + ex.Message);
            }
        }

        static object GetDb()
        {
            Type t = RevivalPlugin.TypeByName("ItemSpawnCategoriesDB");
            if (t == null) { RevivalPlugin.L.LogWarning("ItemSpawnCategoriesDB nicht gefunden."); return null; }
            MethodInfo cur = AccessTools.PropertyGetter(t, "current");
            if (cur == null) { RevivalPlugin.L.LogWarning("ItemSpawnCategoriesDB.current fehlt."); return null; }
            ResourceHook.Reentry = true;
            try { return cur.Invoke(null, null); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("current: " + ex.Message); return null; }
            finally { ResourceHook.Reentry = false; }
        }
    }

    // -------------------------------------------------------- Waffendaten

    /// <summary>Liest die aus weapons_db.xml geparsten Werte zurueck.</summary>
    public static class WeaponData
    {
        public static bool Available(int itemId)
        {
            try { return Get(itemId) != null; }
            catch { return false; }
        }

        public static object Get(int itemId)
        {
            Type t = RevivalPlugin.TypeByName("xmlItemsDataManager");
            if (t == null) return null;
            MethodInfo inst = AccessTools.PropertyGetter(t, "Instance");
            object mgr = inst == null ? null : inst.Invoke(null, null);
            if (mgr == null) return null;

            MethodInfo get = null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public
                                                  | BindingFlags.NonPublic))
            {
                if (m.Name != "GetFirearmWeaponData") continue;
                if (m.GetParameters().Length == 1) { get = m; break; }
            }
            if (get == null) return null;

            ParameterInfo pi = get.GetParameters()[0];
            object[] args = new object[1];
            args[0] = pi.ParameterType == typeof(int)
                    ? (object)itemId
                    : Convert.ChangeType(itemId, pi.ParameterType);
            return get.Invoke(mgr, args);
        }

        /// <summary>Die Handvoll Werte, auf die es beim Einbau ankommt.</summary>
        public static void Summary(int itemId)
        {
            try
            {
                object data = Get(itemId);
                if (data == null)
                {
                    RevivalPlugin.L.LogWarning("WAFFENDATEN " + itemId
                        + ": NULL - Eintrag fehlt in weapons_db.xml!");
                    return;
                }
                RevivalPlugin.L.LogInfo("WAFFENDATEN " + itemId
                    + "  MaxBullets=" + F(data, "MaxBullets")
                    + "  Damage=" + F(data, "Damage")
                    + "  rateOfFire=" + F(data, "rateOfFire")
                    + "  ReloadTime=" + F(data, "ReloadTime")
                    + "  Modes=" + F(data, "_shootModes")
                    + "  Cal=" + F(data, "Cal")
                    + "  Scope=" + F(data, "Scope")
                    + "  ScopeFOV=" + F(data, "ScopeFOV")
                    + "  Clips=" + Arr(Field(Field(data, "WeaponAmmo"), "Clips"), 8));
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Waffendaten " + itemId + ": " + ex.Message); }
        }

        static object Field(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            while (t != null && t != typeof(object))
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) { try { return f.GetValue(o); } catch { return null; } }
                t = t.BaseType;
            }
            return null;
        }

        static string F(object o, string name)
        {
            object v = Field(o, name);
            return v == null ? "null" : v.ToString();
        }

        internal static string Arr(object arr, int max)
        {
            IEnumerable e = arr as IEnumerable;
            if (e == null) return arr == null ? "null" : arr.ToString();
            System.Text.StringBuilder sb = new System.Text.StringBuilder("[");
            int n = 0;
            foreach (object x in e)
            {
                if (n > 0) sb.Append(", ");
                sb.Append(x == null ? "null" : x.ToString());
                if (++n >= max) { sb.Append(", ..."); break; }
            }
            return sb.Append("]").ToString();
        }
    }

    // ---------------------------------------------------------- Diagnose

    /// <summary>
    /// Diagnose statt Vermutung. ReloadWeapon prueft der Reihe nach mehrere
    /// Bedingungen und kehrt still zurueck, sobald eine zutrifft; der Rucksack
    /// gibt beim Einlegen zurueck, was die Datenbank fuer eine ID hergibt.
    /// Beide Stellen protokollieren hier, was sie sehen.
    /// </summary>
    /// <summary>
    /// Keeps a missing back model from taking the whole character screen with
    /// it.
    ///
    /// `PlayerMenuCustomizationManager::WeaponSpineInstanceManager` hangs the
    /// weapon on the character's back, and its IL reads:
    ///
    ///     Instantiate(Resources.Load("PlayerDataPrefabs/Weapons/" + id + "_Weapon"))
    ///
    /// The game DOES check that result against null - two instructions too
    /// late, after the Instantiate ("NetworkShowWeaponSpine: not loaded!").
    /// For an id without such a prefab - every item of ours that is not a
    /// weapon, the drone 1163 in a weapon slot for instance -
    /// Instantiate(null) throws ArgumentException,
    /// CharacterOptionsUI.ShowCharacterData never returns, and the loading
    /// screen stands at "Spielcharaktere laden" until the process is killed.
    /// That cost several game starts on 2026-08-29; the stack trace is in
    /// E-028.
    ///
    /// The finalizer swallows exactly that. By the time it runs, the method
    /// has already cleared the old model off the back; what is missing
    /// afterwards is the new one - which is right, the item is not a weapon.
    /// </summary>
    public static class SpineGuard
    {
        static bool _gemeldet;

        public static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!_gemeldet)
            {
                _gemeldet = true;
                RevivalPlugin.L.LogWarning("Rueckenmodell: "
                    + __exception.GetType().Name + " geschluckt - ein Item im "
                    + "Waffenslot hat kein _Weapon-Prefab. Ohne das haengt der "
                    + "Charakterbildschirm beim Laden des Profils (E-028).");
            }
            return null;
        }
    }
}
