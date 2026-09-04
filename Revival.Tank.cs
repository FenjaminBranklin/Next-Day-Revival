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

    /// <summary>
    /// A procedural spatial report for both vehicle guns. There is no donor
    /// AudioClip in the runtime-built weapon, so the same deterministic clip
    /// is generated on every client and only the tiny shot event is sent.
    /// </summary>
    public static class VehicleShotSound
    {
        static AudioClip _tank;
        static AudioClip _btr;

        public static void Play(Vector3 point, bool tank)
        {
            if (RevivalPlugin.CfgTurretSound == null
                || !RevivalPlugin.CfgTurretSound.Value) return;
            try
            {
                GameObject go = new GameObject(tank
                    ? "NDR Tank Shot Sound" : "NDR BTR Shot Sound");
                go.transform.position = point;
                AudioSource source = go.AddComponent<AudioSource>();
                source.clip = Clip(tank);
                source.loop = false;
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.dopplerLevel = 0f;
                source.minDistance = tank ? 30f : 10f;
                source.maxDistance = RevivalPlugin.CfgTurretSoundRange == null
                    ? 650f : Mathf.Max(50f,
                        RevivalPlugin.CfgTurretSoundRange.Value);
                source.volume = RevivalPlugin.CfgTurretSoundVolume == null
                    ? 1f : Mathf.Clamp01(
                        RevivalPlugin.CfgTurretSoundVolume.Value);
                source.Play();
                UnityEngine.Object.Destroy(go, source.clip.length + 1f);
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Vehicle shot sound: " + ex.Message);
            }
        }

        static AudioClip Clip(bool tank)
        {
            if (tank && _tank != null) return _tank;
            if (!tank && _btr != null) return _btr;

            const int rate = 44100;
            float seconds = tank ? 3.4f : 0.48f;
            float[] data = new float[Mathf.RoundToInt(rate * seconds)];
            int seed = tank ? 125 : 30;
            float filteredNoise = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float t = (float)i / rate;
                seed = seed * 1103515245 + 12345;
                float noise = (((seed >> 16) & 0x7fff) / 16383.5f) - 1f;
                filteredNoise = filteredNoise * (tank ? 0.965f : 0.90f)
                    + noise * (tank ? 0.035f : 0.10f);
                if (tank)
                {
                    float body = (Mathf.Sin(2f * Mathf.PI * 54f * t) * 0.90f
                        + Mathf.Sin(2f * Mathf.PI * 82f * t) * 0.48f
                        + Mathf.Sin(2f * Mathf.PI * 117f * t) * 0.24f)
                        * Mathf.Exp(-t * 1.75f);
                    float blast = filteredNoise * 3.2f * Mathf.Exp(-t * 2.4f);
                    float crack = noise * 1.7f * Mathf.Exp(-t * 75f);
                    float echo1 = t > 0.24f
                        ? (Mathf.Sin(2f * Mathf.PI * 47f * (t - 0.24f)) * 0.62f
                           + filteredNoise * 0.75f)
                          * Mathf.Exp(-(t - 0.24f) * 2.3f) : 0f;
                    float echo2 = t > 0.66f
                        ? (Mathf.Sin(2f * Mathf.PI * 39f * (t - 0.66f)) * 0.42f
                           + filteredNoise * 0.45f)
                          * Mathf.Exp(-(t - 0.66f) * 1.8f) : 0f;
                    data[i] = Mathf.Clamp(body * 1.15f + blast + crack
                        + echo1 + echo2, -1f, 1f);
                }
                else
                {
                    float envelope = Mathf.Exp(-t * 10f);
                    float body = Mathf.Sin(2f * Mathf.PI * 82f * t) * 0.60f;
                    float crack = Mathf.Exp(-t * 45f) * noise;
                    float echo = t > 0.09f
                        ? Mathf.Sin(2f * Mathf.PI * 57f * t)
                          * Mathf.Exp(-(t - 0.09f) * 4f) : 0f;
                    data[i] = Mathf.Clamp((body + noise * 0.24f) * envelope
                        + crack * 0.85f + echo * 0.18f, -1f, 1f);
                }
            }
            AudioClip clip = AudioClip.Create(tank ? "NDR_TankShot"
                : "NDR_BTRShot", data.Length, 1, rate, false);
            clip.SetData(data, 0);
            if (tank) _tank = clip; else _btr = clip;
            return clip;
        }
    }

    /// <summary>
    /// Erzeugt ein Fahrzeug vor dem Spieler. Ohne das steht ein BTR nur dort,
    /// wo die Szene einen VehicleSpawnPoint hat - zum Pruefen des Turmgeschuetzes
    /// zu wenig.
    ///
    /// Nachgebaut aus VehicleSpawnPoint::InstantiateCar (IL, 2026-08-28):
    ///
    ///     PhotonNetwork.InstantiateSceneObject("VehicleSpawn\" + name, pos, rot, 0, data)
    ///     VehicleGameSystem.Fuel, .Durability
    ///     VehicleInventoryManager.SetPartSpawn(10002, 10003, 10004)
    ///
    /// Zwei bewusste Abweichungen:
    ///
    /// 1. The data block keeps element zero null. VehicleGameSystem reads that
    ///    element as a spawn point id and returns immediately when it is null.
    ///    Element one carries only our T-72 marker, so the vehicle stays at the
    ///    requested position while every client can build the same appearance.
    /// 2. Die drei Teile setzt LocalSetVehicleComponent direkt statt ueber
    ///    SetPartSpawn - das wuerfelt bei Modus 3 mit 50 Prozent, und ein
    ///    Fahrzeug ohne Zuendkerze faehrt nicht.
    /// </summary>
    // ------------------------------------------------------------ Kampfpanzer

    /// <summary>
    /// Macht aus einem frisch gespawnten BTR-80A einen T-72.
    ///
    /// Der Panzer ist kein eigenes Fahrzeug, sondern ein umgebautes. Dasselbe
    /// Prefab, dieselben Kollider, dieselbe Fahrphysik, dasselbe PhotonView.
    /// Ausgetauscht werden drei Dinge, und nur diese drei:
    ///
    ///     das sichtbare Mesh   hull -> t72_hull, turret -> t72_turret
    ///     die Zahl der Sitze   6 + Geschuetz  ->  3 + Geschuetz
    ///     das Werteprofil      Turret rechnet mit den Tank-Werten
    ///
    /// Der Panzer faehrt sich damit wie ein BTR. Das ist kein Versehen,
    /// sondern der Preis, zu dem es ueberhaupt geht: ein Fahrzeug ist nicht
    /// ein Modell, sondern Modell plus Radkollider, Fahrphysik, Netzabgleich,
    /// Sitzpunkte, Schadensmodell, Treibstoff, Bauteile und Spawnsystem.
    ///
    /// BELEGT (REVERSE_ENGINEERING.md 18, am 2026-08-29 ohne Spielstart):
    ///
    ///   Das Prefab hat sieben BoxCollider und acht Radkollider, aber KEINEN
    ///   MeshCollider. Ein Meshtausch kann die Fahrphysik nicht treffen - das
    ///   war die riskanteste Annahme des ganzen Vorhabens.
    ///
    ///   VehicleGameSystem::InitCar setzt
    ///       Passengers = new GameObject[SeatPoints.childCount]
    ///   Die Sitzzahl steht also NUR an SeatPoints. InitCar ist gelaufen, wenn
    ///   dieser Umbau greift - deshalb wird das Array hier neu gesetzt. Zu
    ///   diesem Zeitpunkt sitzt noch niemand darin.
    ///
    ///   Der Knoten `Meshes` dreht -90 Grad um X. Im Meshraum ist damit +Z
    ///   oben und -Y vorn, der Massstab betraegt 3 Einheiten je Meter.
    ///   `t72_mesh.py` baut genau so - deshalb wird hier nichts skaliert und
    ///   nichts gedreht.
    ///
    /// NETWORK APPEARANCE (CONFIRMED IL, 2026-08-31): Photon transmits the
    /// prefab path, not a locally changed mesh. CarSpawn therefore writes a
    /// marker into InstantiateSceneObject's object[] data. NetworkingPeer::
    /// DoInstantiate receives that data on the creator, other players and late
    /// joiners; TankNetwork's postfix applies this same conversion there.
    ///
    /// UNGEPRUEFT: alles, was Augen braucht. Steht in TASKS.md unter
    /// "Abnahme im Spiel".
    /// </summary>
    public static class Tank
    {
        /// <summary>
        /// Kennzeichnung im Namen der Instanz. Sie MUSS weiter mit "BTR-80A"
        /// beginnen: `Turret.IsBtr` prueft diesen Praefix, und der Panzer soll
        /// vom vorhandenen Geschuetzcode ohne jede Aenderung erkannt werden.
        /// </summary>
        public const string Marke = "_T72";
        const string InstanzName = "BTR-80A_Spawn" + Marke;

        /// <summary>
        /// Turmring des T-72 in der Wanne, in Spieleinheiten. Der BTR hat den
        /// Turm hoeher und weiter vorn; bliebe die Transform stehen, schwebte
        /// der Panzerturm.
        ///
        /// Der Wert ist seit 0.5.3 nicht mehr gesetzt, sondern GEMESSEN:
        /// `t72_import.py` liest die Baugruppe `t-72_wrecked_LOD0` aus dem
        /// Spiel und gibt am Ende die Stellung aus, die die Turmtransform
        /// bekommen muss. Wer den Import erneut laufen laesst, vergleicht die
        /// letzte Zeile seiner Ausgabe mit dieser Zeile hier.
        /// </summary>
        static readonly Vector3 Turmring = new Vector3(0f, -0.922f, 4.144f);

        /// <summary>
        /// Wo die Mitfahrer im Panzer sitzen, in Fahrzeugeinheiten relativ zu
        /// SeatPoints. Fahrer vorn links, dahinter zwei Plaetze im Kampfraum -
        /// alle drei tief genug, dass kein Kopf durch das Dach stoesst.
        /// </summary>
        static readonly Vector3[] Mitfahrerplaetze = new Vector3[] {
            new Vector3(-1.10f, -1.0f, 3.60f),
            new Vector3( 1.35f, -1.0f, 0.20f),
            new Vector3(-1.35f, -1.0f, -2.60f),
        };

        /// <summary>
        /// The BTR gunner seat is at y 0.95. In the lower T-72 body that puts
        /// the seated player's head through the roof. The sight camera follows
        /// the turret independently, so only the physical body moves down.
        /// </summary>
        static readonly Vector3 Gunnerplatz = new Vector3(0f, -1.15f, 1.10f);

        static Material _mat;

        /// <summary>
        /// The first F9 spawn used to load two meshes and three 2K textures in
        /// one Update call. Unity could not service Photon during that work,
        /// and the combined pause exceeded its 15 second disconnect timeout.
        /// Fill the existing Assets caches near startup instead, yielding one
        /// frame between every expensive file/decode step. The actual tank
        /// construction remains synchronous but has no disk work left.
        /// </summary>
        public static IEnumerator Prewarm()
        {
            if (RevivalPlugin.CfgTank == null || !RevivalPlugin.CfgTank.Value
                || RevivalPlugin.CfgTankSwapMesh == null
                || !RevivalPlugin.CfgTankSwapMesh.Value)
                yield break;

            yield return null;
            Assets.Load("t72_hull.ndmesh");
            yield return null;
            Assets.Load("t72_turret.ndmesh");
            yield return null;
            Assets.Texture("t72_diffuse.png", false, true);
            yield return null;
            Assets.Texture("t72_metal.png", true, true);
            yield return null;
            Assets.Texture("t72_normal.png", true, true);
            yield return null;
            // Running gear: the two scrolling tracks, their link texture, and
            // the road/idler/drive wheels named in the manifest. Warmed here so
            // the F9 spawn does no disk work (see summary above).
            if (RevivalPlugin.CfgTankAnimate == null || RevivalPlugin.CfgTankAnimate.Value)
            {
                Assets.Texture("t72_track.png", false, true);
                yield return null;
                Assets.Load("t72_track_left.ndmesh");
                yield return null;
                Assets.Load("t72_track_right.ndmesh");
                yield return null;
                string[] files = RunningGear.ManifestMeshFiles();
                for (int i = 0; i < files.Length; i++)
                {
                    Assets.Load(files[i]);
                    yield return null;
                }
            }
            RevivalPlugin.L.LogInfo("Panzer: resources prewarmed for F9.");
        }

        public static bool IstPanzer(Transform root)
        {
            if (root == null) return false;
            return root.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Der ganze Umbau. Aufgerufen direkt nach dem Spawn.</summary>
        public static void Umbauen(GameObject car)
        {
            if (car == null) return;
            car.name = InstanzName;
            try { Sitze(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Panzer, Sitze: " + ex); }
            if (!RevivalPlugin.CfgTankSwapMesh.Value)
            {
                RevivalPlugin.L.LogInfo("Panzer: Meshtausch ist abgeschaltet, "
                    + "es bleibt ein BTR mit Panzerwerten stehen.");
                return;
            }
            try { Meshes(car); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Panzer, Mesh: " + ex); }
        }

        // ------------------------------------------------------------- Sitze

        static void Sitze(GameObject car)
        {
            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (vgsType == null) return;
            Component vgs = car.GetComponent(vgsType);
            if (vgs == null) return;

            FieldInfo fSeats = AccessTools.Field(vgsType, "SeatPoints");
            Transform seats = fSeats == null ? null : fSeats.GetValue(vgs) as Transform;
            if (seats == null)
            {
                RevivalPlugin.L.LogWarning("Panzer: SeatPoints fehlt, Sitzzahl bleibt.");
                return;
            }

            // Der Geschuetzsitz haengt schon dran (Turret.InitCarPrefix lief
            // beim Erzeugen) und bleibt in jedem Fall. Weggenommen werden nur
            // Mitfahrerplaetze, und zwar von hinten.
            int behalten = Mathf.Max(1, RevivalPlugin.CfgTankSeats.Value);
            List<Transform> mitfahrer = new List<Transform>();
            Transform gunner = null;
            for (int i = 0; i < seats.childCount; i++)
            {
                Transform c = seats.GetChild(i);
                if (c.name == Turret.SeatName) { gunner = c; continue; }
                mitfahrer.Add(c);
            }
            for (int i = mitfahrer.Count - 1; i >= behalten; i--)
            {
                // SetParent wirkt sofort, Destroy erst am Frameende - und bis
                // dahin zaehlte childCount den Sitz noch mit.
                mitfahrer[i].SetParent(null, false);
                UnityEngine.Object.Destroy(mitfahrer[i].gameObject);
            }

            // Die verbliebenen Plaetze IN die Wanne legen.
            //
            // Die Sitzpunkte des BTR stehen fuer eine Wanne, die hoeher ist als
            // die des Panzers: Driver bei z 5.47 und Passenger1 bei z 5.77
            // liegen unter der abfallenden Bugplatte, und dort ragten am
            // 2026-08-29 im Spiel zwei Koepfe aus dem Panzerdach heraus.
            //
            // VehicleGameSystem::SitToPassengerPlace setzt Position und Drehung
            // des Mitfahrers EINMAL aus SeatPoints.GetChild(i) (im IL gelesen,
            // 2026-08-29) - wer die Punkte vor dem Aufsitzen verschiebt,
            // verschiebt damit den Mitfahrer, und sonst nichts. The gunner is
            // moved too: its BTR height is above the T-72 roof.
            //
            // Hoehe -1.0 statt 0.15: das Wannendach liegt bei 4.5 Einheiten,
            // ein sitzender Koerper ist rund 3.9 hoch. Damit bleibt gut eine
            // Einheit Luft, und der Platz liegt trotzdem nicht so tief, dass
            // die Kamera durch den Boden faellt.
            for (int i = 0; i < mitfahrer.Count && i < Mitfahrerplaetze.Length; i++)
            {
                if (mitfahrer[i] == null) continue;
                mitfahrer[i].localPosition = Mitfahrerplaetze[i];
            }
            if (gunner != null)
            {
                gunner.localPosition = Gunnerplatz;
                gunner.localRotation = Quaternion.identity;
            }

            FieldInfo fPass = AccessTools.Field(vgsType, "Passengers");
            if (fPass != null) fPass.SetValue(vgs, new GameObject[seats.childCount]);

            RevivalPlugin.L.LogInfo("Panzer: " + seats.childCount + " Sitze ("
                + behalten + " Mitfahrer plus Geschuetz), Passengers neu gesetzt, "
                + "Mitfahrer und Richtschuetze in die Wanne gesetzt.");
        }

        // ------------------------------------------------------------- Modell

        static void Meshes(GameObject car)
        {
            Mesh wanne = Assets.Load("t72_hull.ndmesh");
            Mesh turm = Assets.Load("t72_turret.ndmesh");
            if (wanne == null || turm == null)
            {
                RevivalPlugin.L.LogWarning("Panzer: t72_hull.ndmesh oder "
                    + "t72_turret.ndmesh fehlt neben der DLL - es bleibt ein BTR.");
                return;
            }

            int wannen = 0, tuerme = 0, glas = 0;
            Transform[] all = car.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                // Vier LOD-Stufen tragen dieselben Namen. Alle vier werden
                // umgehaengt: die LODGroup blendet beim Wegfahren um, und ein
                // Panzer, der ab dreissig Metern zum BTR wird, ist schlimmer
                // als gar keiner.
                if (t.name == "hull") { if (Setzen(t, wanne)) wannen++; }
                else if (t.name == "turret")
                {
                    if (Setzen(t, turm)) { t.localPosition = Turmring; tuerme++; }
                }
                else if (t.name == "glass")
                {
                    Renderer r = t.GetComponent<Renderer>();
                    if (r != null) { r.enabled = false; glas++; }
                }
            }

            // Die acht Raeder des BTR liegen innerhalb der Ketten und waeren
            // von aussen kaum zu sehen - aber sie drehen sich beim Fahren, und
            // ein drehendes LKW-Rad in einer Panzerkette faellt dann doch auf.
            // Nur die Anzeige geht aus; die Radkollider bleiben, sonst faehrt
            // nichts mehr.
            int raeder = 0;
            Transform naben = car.transform.Find("Wheel Transforms");
            if (naben != null)
            {
                Renderer[] rs = naben.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++) { rs[i].enabled = false; raeder++; }
            }

            RevivalPlugin.L.LogInfo("Panzer: " + wannen + " Wannen, " + tuerme
                + " Tuerme getauscht, " + glas + " Scheiben und " + raeder
                + " Raeder ausgeblendet.");

            // The running gear (road wheels, idler, drive sprocket, and the two
            // tracks) is NOT baked into the hull mesh any more - it is built and
            // animated as separate children so it turns and scrolls with the
            // vehicle. Attach to the first hull transform found; its local frame
            // IS the mesh frame the wheel positions were measured in.
            if (RevivalPlugin.CfgTankAnimate == null || RevivalPlugin.CfgTankAnimate.Value)
            {
                Transform hull = null;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].name == "hull") { hull = all[i]; break; }
                if (hull != null)
                {
                    try { RunningGear.Build(car, hull, _mat != null ? _mat : Panzermaterial(null), Trackmaterial()); }
                    catch (Exception ex) { RevivalPlugin.L.LogError("Panzer, Laufwerk: " + ex); }
                }
                else RevivalPlugin.L.LogWarning("Panzer: kein hull-Transform fuer das Laufwerk gefunden.");
            }
        }

        /// <summary>
        /// Material for the two scrolling tracks: the hull shader with the
        /// tileable link texture (t72_track.png), set to REPEAT so a moving UV
        /// offset makes the links travel. A FRESH instance PER tank, because the
        /// plugin slides its texture offset every frame - one shared material
        /// would make every tank's tracks scroll at the last tank's speed, and
        /// it must never touch the hull material.
        /// </summary>
        static Material Trackmaterial()
        {
            Material vorlage = _mat != null ? _mat : Panzermaterial(null);
            Shader shader = vorlage != null && vorlage.shader != null
                ? vorlage.shader : Shader.Find("Standard");
            Material m = new Material(shader);
            m.name = "T72_Track_Material";
            Texture2D tex = Assets.Texture("t72_track.png", false, true);
            if (tex != null) tex.wrapMode = TextureWrapMode.Repeat;
            m.mainTexture = tex;
            // The mesh UV runs 0..1 round the loop; tile the link texture
            // T72RunningGear.TrackRepeats times across it (must match
            // t72_import.TRACK_REPEATS).
            Vector2 tile = new Vector2(T72RunningGear.TrackRepeats, 1f);
            m.mainTextureScale = tile;
            if (m.HasProperty("_MainTex"))
            {
                m.SetTexture("_MainTex", tex);
                m.SetTextureScale("_MainTex", tile);
            }
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.25f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.35f);
            // No metallic/normal map on the track - keep it a plain lit surface.
            if (m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", null);
            m.DisableKeyword("_METALLICGLOSSMAP");
            if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", null);
            m.DisableKeyword("_NORMALMAP");
            if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 0f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", 1f);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", 0f);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            m.DisableKeyword("_EMISSION");
            return m;
        }

        static bool Setzen(Transform t, Mesh mesh)
        {
            MeshFilter mf = t.GetComponent<MeshFilter>();
            if (mf == null) return false;
            // .mesh und nicht .sharedMesh: sonst traegt jedes BTR im Spiel ab
            // sofort das Panzermodell.
            mf.mesh = mesh;
            Renderer r = t.GetComponent<Renderer>();
            if (r != null) r.material = Panzermaterial(r.sharedMaterial);
            return true;
        }

        /// <summary>
        /// Material aus dem Shader des Fahrzeugs und den eigenen Texturen.
        ///
        /// Dieselbe Kette wie `ItemFactory.MakeMaterial` - dort steht
        /// ausfuehrlich, warum jede einzelne Zeile noetig ist. Kurz: Shader vom
        /// Original uebernehmen, Metallic-Map abraeumen, Blend-Modus hart auf
        /// Opaque, und die Normal Map ohne das Keyword `_NORMALMAP` wird zwar
        /// gesetzt, aber nicht ausgewertet.
        ///
        /// Einmal gebaut und gemerkt: alle Panzer teilen sich ein Material.
        /// </summary>
        static Material Panzermaterial(Material vorlage)
        {
            if (_mat != null) return _mat;

            Shader shader = vorlage == null ? null : vorlage.shader;
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new Exception("kein brauchbarer Shader gefunden");

            Texture2D diffuse = Assets.Texture("t72_diffuse.png", false, true);
            Material m = new Material(shader);
            m.name = "T72_Material";
            m.mainTexture = diffuse;
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", diffuse);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);

            // OBERFLAECHE WIE AM MTW - UND ZWAR AUS DESSEN EIGENEM MATERIAL
            //
            // Bis 0.5.1 stand hier Metallic 0.04 und Glossiness 0.10, mit der
            // Begruendung "Panzerlack ist matt". Das war eine Annahme, und im
            // Spiel sah der Panzer damit aus wie bemaltes Papier, waehrend der
            // BTR direkt daneben metallisch glaenzte. Der Auftrag lautet
            // ausdruecklich: dieselbe Oberflaeche wie der MTW.
            //
            // `research/dump_material.py btr-80a_alb` (2026-08-29) sagt, was
            // der MTW wirklich benutzt: Standardshader mit den Keywords
            // _METALLICGLOSSMAP und _NORMALMAP, dazu `_GlossMapScale` 0.4. Die
            // Skalarwerte `_Metallic` 0.30 und `_Glossiness` 0.5 sind dabei
            // WIRKUNGSLOS - liegt eine Map an, liest der Shader Metallic aus
            // deren rotem Kanal und Smoothness aus deren Alpha. Gemessen an
            // btr-80a_met: Rot im Mittel 0.15, Alpha ueberall 1.0 (DXT1 hat
            // keinen Alphakanal), wirksame Smoothness also 0.40.
            //
            // t72_metal.png traegt genau diese Werte auf Wanne und Turm und
            // hoehere am Laufwerk. `_GlossMapScale` bleibt deshalb auf 1.0 -
            // die 0.4 stecken schon im Alphakanal. Die Skalarwerte werden
            // trotzdem gesetzt, damit der Panzer auch dann vernuenftig
            // aussieht, wenn die Map einmal fehlt.
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.40f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.15f);
            Texture2D metal = Assets.Texture("t72_metal.png", true, true);
            if (metal != null && m.HasProperty("_MetallicGlossMap"))
            {
                m.SetTexture("_MetallicGlossMap", metal);
                if (m.HasProperty("_GlossMapScale")) m.SetFloat("_GlossMapScale", 1.0f);
                // 0 = Smoothness aus dem Alphakanal der Metallic-Map. Genau so
                // steht es am MTW.
                if (m.HasProperty("_SmoothnessTextureChannel"))
                    m.SetFloat("_SmoothnessTextureChannel", 0f);
                m.EnableKeyword("_METALLICGLOSSMAP");
            }
            else
            {
                if (m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", null);
                m.DisableKeyword("_METALLICGLOSSMAP");
            }
            // Ohne diese beiden bleibt das Metall stumpf: der Standardshader
            // rechnet Glanzlicht und Spiegelung nur, wenn beide anstehen.
            if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 1f);
            if (m.HasProperty("_GlossyReflections")) m.SetFloat("_GlossyReflections", 1f);
            m.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
            m.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
            if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 0f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", 1f);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", 0f);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            m.DisableKeyword("_EMISSION");

            Texture2D nrm = Assets.Texture("t72_normal.png", true, true);
            if (nrm != null && m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", nrm);
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 1.0f);
                m.EnableKeyword("_NORMALMAP");
            }

            RevivalPlugin.L.LogInfo("Panzer: Material auf Shader " + shader.name
                + ", Normal Map " + (nrm != null)
                + ", Metallic Map " + (metal != null) + ".");
            _mat = m;
            return m;
        }
    }

    /// <summary>
    /// Builds the T-72 running gear (road wheels, idler, drive sprocket, the two
    /// tracks) as separate children of the hull and hangs the animator on the
    /// vehicle. The wheels and their axle positions come from t72_wheels.txt,
    /// written by t72_import.py; the two track bands carry lengthwise UVs.
    /// </summary>
    public static class RunningGear
    {
        class Entry
        {
            public string Name;
            public string Mesh;
            public float X, Y, Z, Radius;
        }

        static List<Entry> _manifest;

        static void ParseManifest()
        {
            if (_manifest != null) return;
            _manifest = new List<Entry>();
            string path = Path.Combine(RevivalPlugin.AssetDir, "t72_wheels.txt");
            if (!File.Exists(path))
            {
                RevivalPlugin.L.LogWarning("Laufwerk: t72_wheels.txt fehlt neben der DLL.");
                return;
            }
            string[] lines = File.ReadAllLines(path);
            System.Globalization.CultureInfo inv =
                System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0 || s[0] == '#') continue;
                string[] p = s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 6) continue;
                try
                {
                    Entry e = new Entry();
                    e.Name = p[0]; e.Mesh = p[1];
                    e.X = float.Parse(p[2], inv);
                    e.Y = float.Parse(p[3], inv);
                    e.Z = float.Parse(p[4], inv);
                    e.Radius = float.Parse(p[5], inv);
                    _manifest.Add(e);
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Laufwerk: Zeile unlesbar: " + s + " (" + ex.Message + ")");
                }
            }
        }

        /// <summary>Distinct wheel mesh files, for prewarming.</summary>
        public static string[] ManifestMeshFiles()
        {
            ParseManifest();
            List<string> files = new List<string>();
            for (int i = 0; i < _manifest.Count; i++)
                if (!files.Contains(_manifest[i].Mesh)) files.Add(_manifest[i].Mesh);
            return files.ToArray();
        }

        public static void Build(GameObject car, Transform hull, Material wheelMat, Material trackMat)
        {
            ParseManifest();
            if (_manifest.Count == 0)
            {
                RevivalPlugin.L.LogWarning("Laufwerk: kein Manifest, Raeder bleiben aus.");
                return;
            }

            List<Transform> wheels = new List<Transform>();
            List<float> radien = new List<float>();
            int gebaut = 0;
            for (int i = 0; i < _manifest.Count; i++)
            {
                Entry e = _manifest[i];
                Mesh mesh = Assets.Load(e.Mesh);
                if (mesh == null) continue;
                GameObject go = new GameObject("ndr_wheel_" + e.Name);
                go.transform.SetParent(hull, false);
                go.transform.localPosition = new Vector3(e.X, e.Y, e.Z);
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                MeshFilter mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = wheelMat;
                wheels.Add(go.transform);
                radien.Add(e.Radius);
                gebaut++;
            }

            int gleise = 0;
            string[] seiten = new string[] { "left", "right" };
            for (int s = 0; s < seiten.Length; s++)
            {
                Mesh mesh = Assets.Load("t72_track_" + seiten[s] + ".ndmesh");
                if (mesh == null) continue;
                GameObject go = new GameObject("ndr_track_" + seiten[s]);
                go.transform.SetParent(hull, false);
                go.transform.localPosition = Vector3.zero;   // verts already in hull coords
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                MeshFilter mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = trackMat;
                gleise++;
            }

            T72RunningGear anim = car.GetComponent<T72RunningGear>();
            if (anim == null) anim = car.AddComponent<T72RunningGear>();
            anim.Init(hull, wheels.ToArray(), radien.ToArray(), trackMat);

            RevivalPlugin.L.LogInfo("Panzer: Laufwerk gebaut - " + gebaut
                + " Raeder, " + gleise + " Ketten, animiert.");
        }
    }

    /// <summary>
    /// Turns the road wheels and scrolls the tracks from the vehicle's own
    /// motion, so the running gear moves with the tank the way the APC's wheels
    /// turn. Purely visual: it reads the transform's world velocity, not the
    /// physics, so it works the same on the driver's client and on remote
    /// (Photon-synced) tanks.
    /// </summary>
    public class T72RunningGear : MonoBehaviour
    {
        // The track loop perimeter in mesh units (t72_import.py prints ~20-21).
        const float TrackPerimeterUnits = 20.5f;
        // Link-texture tiles round the loop. MUST match t72_import.TRACK_REPEATS;
        // the track material scales its UV by this, so one link spans
        // perimeter/TrackRepeats metres and the offset must advance that fast.
        public const float TrackRepeats = 46f;

        Transform _hull;
        Transform[] _wheels;
        float[] _radius;
        float[] _angle;
        Material _track;
        float _trackOffset;
        Vector3 _lastPos;
        bool _have;

        public void Init(Transform hull, Transform[] wheels, float[] radius, Material track)
        {
            _hull = hull;
            _wheels = wheels;
            _radius = radius;
            _track = track;
            _angle = new float[wheels != null ? wheels.Length : 0];
            _lastPos = transform.position;
            _have = _hull != null && _wheels != null;
        }

        void LateUpdate()
        {
            if (!_have) return;
            float dt = Time.deltaTime;
            if (dt <= 1e-5f) return;

            Vector3 pos = transform.position;
            Vector3 vel = (pos - _lastPos) / dt;
            _lastPos = pos;
            if (_hull == null) return;

            // World velocity into the hull's own axes. The mesh frame has +z up,
            // -y forward, x across - so forward speed is -y.
            Vector3 vl = _hull.InverseTransformDirection(vel);
            float fwd = -vl.y;

            float scale = Mathf.Abs(_hull.lossyScale.x);
            if (scale < 1e-4f) scale = 1f;
            float dir = (RevivalPlugin.CfgTankSpinInvert != null
                         && RevivalPlugin.CfgTankSpinInvert.Value) ? -1f : 1f;

            for (int i = 0; i < _wheels.Length; i++)
            {
                if (_wheels[i] == null) continue;
                float rW = Mathf.Max(0.01f, _radius[i]) * scale;      // world metres
                float dDeg = (fwd / rW) * Mathf.Rad2Deg * dt * dir;
                _angle[i] += dDeg;
                _wheels[i].localRotation = Quaternion.AngleAxis(_angle[i], Vector3.right);
            }

            if (_track != null)
            {
                float scroll = RevivalPlugin.CfgTankTrackScroll != null
                    ? RevivalPlugin.CfgTankTrackScroll.Value : 1f;
                float perim = TrackPerimeterUnits * scale;             // world metres
                if (perim < 1e-4f) perim = 1f;
                // One link spans perim/TrackRepeats metres; advance the tiled
                // offset so the links travel at ground speed.
                _trackOffset += (fwd * dt * TrackRepeats / perim) * scroll * dir;
                Vector2 o = _track.mainTextureOffset;
                o.x = _trackOffset;
                _track.mainTextureOffset = o;
            }
        }
    }

    /// <summary>
    /// Carries the T-72 decision in Photon's cached scene-instantiation event.
    /// Element zero deliberately remains null because VehicleGameSystem treats
    /// it as an Int32 spawn point id. The string in element one is independent
    /// mod data and is safe for Photon to cache and replay to late joiners.
    /// </summary>
    public static class TankNetwork
    {
        const string Marker = "NDR_T72_V1";

        public static object[] SpawnData(bool tank)
        {
            if (!tank) return null;
            return new object[] { null, Marker };
        }

        static bool IsTankData(object value)
        {
            object[] data = value as object[];
            return data != null && data.Length > 1 && data[0] == null
                && string.Equals(data[1] as string, Marker, StringComparison.Ordinal);
        }

        public static void Install(Harmony harmony)
        {
            try
            {
                Type peer = RevivalPlugin.TypeByName("NetworkingPeer");
                if (peer == null)
                {
                    RevivalPlugin.L.LogWarning("Panzer-Netzwerk: NetworkingPeer fehlt.");
                    return;
                }

                MethodInfo target = null;
                MethodInfo[] methods = peer.GetMethods(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo candidate = methods[i];
                    if (candidate.Name == "DoInstantiate"
                        && candidate.ReturnType == typeof(GameObject)
                        && candidate.GetParameters().Length == 3)
                    {
                        target = candidate;
                        break;
                    }
                }
                if (target == null)
                {
                    RevivalPlugin.L.LogWarning("Panzer-Netzwerk: DoInstantiate fehlt.");
                    return;
                }

                harmony.Patch(target, null,
                    new HarmonyMethod(typeof(TankNetwork).GetMethod("Postfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("Panzer-Netzwerk: Spawnmarker aktiv.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Panzer-Netzwerk konnte nicht aktiviert werden: " + ex);
            }
        }

        public static void Postfix(object __0, GameObject __result)
        {
            try
            {
                if (__result == null || Tank.IstPanzer(__result.transform)) return;
                IDictionary eventData = __0 as IDictionary;
                if (eventData == null || !eventData.Contains((byte)5)) return;
                if (!IsTankData(eventData[(byte)5])) return;

                Tank.Umbauen(__result);
                RevivalPlugin.L.LogInfo("Panzer-Netzwerk: T-72 auf diesem Client aufgebaut: "
                    + __result.name + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Panzer-Netzwerk, Spawnmarker: " + ex);
            }
        }
    }

    public static class CarSpawn
    {
        // Aus VehicleSpawnPoint::InstantiateCar: Batterie, Schluessel, Kerze.
        static readonly int[] Parts = new int[] { 10002, 10003, 10004 };

        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static KeyCode _tankKey = KeyCode.None;
        static bool _tankKeyParsed;
        static bool _tankTimeoutRaised;

        public static void Tick()
        {
            try
            {
                // Der Panzer haengt NICHT an Research/SpawnCar: der Fahrzeug-
                // spawn ist ein Werkzeug und standardmaessig aus, der Panzer
                // ist Spielinhalt und standardmaessig an.
                if (RevivalPlugin.CfgTank.Value && Input.GetKeyDown(TankKey()))
                {
                    GuardTankTimeout();
                    Spawn(true);
                    return;
                }
                if (!RevivalPlugin.CfgSpawnCar.Value) return;
                if (!Input.GetKeyDown(Key())) return;
                Spawn(false);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Fahrzeugspawn: " + ex);
            }
        }

        /// <summary>
        /// Put a vehicle down at a given place. Same path as the F7/F9 keys -
        /// same prefab, same three parts, same tank and durability - only the
        /// position comes from the caller instead of from the camera. The
        /// patrol driver spawns through here so there is exactly one place
        /// that knows how a vehicle is built.
        /// </summary>
        internal static GameObject SpawnAt(Vector3 pos, Quaternion rot, bool panzer)
        {
            if (!IsMasterClient())
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: dieser Client ist nicht "
                    + "Masterclient. InstantiateSceneObject wird von Photon abgewiesen.");
                return null;
            }

            string name = RevivalPlugin.CfgSpawnCarName.Value;
            GameObject car = InstantiateSceneObject("VehicleSpawn\\" + name, pos, rot,
                panzer);
            if (car == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: Photon lieferte null fuer \""
                    + name + "\". Ist der Prefabname richtig?");
                return null;
            }

            Prepare(car);
            if (panzer && !Tank.IstPanzer(car.transform)) Tank.Umbauen(car);
            return car;
        }

        /// <summary>
        /// Spawn an ARBITRARY vehicle prefab (not the configured BTR donor) and
        /// rebuild it on every client. This is the generic seam the vehicle
        /// registry uses for the 15-seat Ural: it Photon-instantiates
        /// "VehicleSpawn\\&lt;prefabName&gt;" with a caller-supplied network marker
        /// (so late joiners and remote clients run the same <paramref name="rebuild"/>
        /// through the DoInstantiate postfix), applies the same fuel/parts
        /// Prepare the BTR/tank use, and then runs the rebuild locally. Master
        /// client only, exactly like SpawnAt - Photon rejects scene-object
        /// instantiation otherwise. Returns null on refusal so the caller
        /// (patrol/convoy/admin) can react without a half-built vehicle.
        /// </summary>
        internal static GameObject SpawnPrefab(string prefabName, Vector3 pos,
                                               Quaternion rot, object[] netData,
                                               Action<GameObject> rebuild)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            if (!IsMasterClient())
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: dieser Client ist nicht "
                    + "Masterclient. InstantiateSceneObject wird von Photon abgewiesen.");
                return null;
            }

            GameObject car = InstantiateSceneObjectData("VehicleSpawn\\" + prefabName,
                                                        pos, rot, netData);
            if (car == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: Photon lieferte null fuer \""
                    + prefabName + "\". Ist der Prefabname richtig?");
                return null;
            }

            Prepare(car);
            if (rebuild != null)
            {
                try { rebuild(car); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError("Fahrzeugspawn: Umbau von \""
                        + prefabName + "\" fehlgeschlagen: " + ex);
                }
            }
            return car;
        }

        static void Spawn(bool panzer)
        {
            float started = Time.realtimeSinceStartup;
            Camera cam = Camera.main;
            if (cam == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: keine Kamera gefunden.");
                return;
            }

            Vector3 ahead = cam.transform.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.000001f) ahead = Vector3.forward;
            ahead.Normalize();

            float distance = Mathf.Max(4f, RevivalPlugin.CfgSpawnCarDistance.Value);
            Vector3 above = cam.transform.position + ahead * distance + Vector3.up * 30f;

            Vector3 ground;
            GameObject under = Turret.RaycastObject(above, Vector3.down, 200f, out ground);
            if (under == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: unter " + above
                    + " ist kein Boden - naeher an festen Grund stellen.");
                return;
            }

            if (!IsMasterClient())
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: dieser Client ist nicht "
                    + "Masterclient. InstantiateSceneObject wird von Photon abgewiesen.");
                return;
            }

            string name = RevivalPlugin.CfgSpawnCarName.Value;
            string path = "VehicleSpawn\\" + name;
            Vector3 pos = ground + Vector3.up * 1.6f;
            Quaternion rot = Quaternion.LookRotation(ahead, Vector3.up);

            GameObject car = InstantiateSceneObject(path, pos, rot, panzer);
            if (car == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: Photon lieferte null fuer \""
                    + path + "\". Ist der Prefabname richtig?");
                return;
            }

            Prepare(car);
            if (panzer)
            {
                if (!Tank.IstPanzer(car.transform)) Tank.Umbauen(car);
            }
            Munitionsbeigabe(car, panzer);

            RevivalPlugin.L.LogInfo((panzer ? "Panzer aus \"" : "Fahrzeug \"")
                + name + "\" erzeugt bei " + pos
                + ", Boden \"" + under.name + "\" in "
                + (Time.realtimeSinceStartup - started).ToString("0.00") + " s.");
        }

        /// <summary>
        /// Photon 15 seconds is too aggressive for the synchronous first tank
        /// construction on this Unity version. Raise the existing peer value
        /// immediately before F9 and keep it for the session. This does not
        /// affect normal traffic; it only delays declaring a genuinely dead
        /// connection while the main thread is occupied.
        /// </summary>
        static void GuardTankTimeout()
        {
            if (_tankTimeoutRaised) return;
            try
            {
                Type network = RevivalPlugin.TypeByName("PhotonNetwork");
                if (network == null) throw new Exception("PhotonNetwork missing");

                object peer = null;
                FieldInfo peerField = AccessTools.Field(network, "networkingPeer");
                if (peerField != null) peer = peerField.GetValue(null);
                if (peer == null)
                {
                    PropertyInfo peerProperty = AccessTools.Property(network,
                                                                    "networkingPeer");
                    if (peerProperty != null)
                        peer = peerProperty.GetValue(null, null);
                }
                if (peer == null) throw new Exception("networkingPeer missing");

                const int wanted = 60000;
                int previous = -1;
                PropertyInfo property = AccessTools.Property(peer.GetType(),
                                                              "DisconnectTimeout");
                if (property != null && property.CanWrite)
                {
                    object old = property.GetValue(peer, null);
                    if (old != null) previous = Convert.ToInt32(old);
                    property.SetValue(peer, wanted, null);
                }
                else
                {
                    FieldInfo field = AccessTools.Field(peer.GetType(),
                                                        "DisconnectTimeout");
                    if (field == null) throw new Exception("DisconnectTimeout missing");
                    object old = field.GetValue(peer);
                    if (old != null) previous = Convert.ToInt32(old);
                    field.SetValue(peer, wanted);
                }

                _tankTimeoutRaised = true;
                RevivalPlugin.L.LogInfo("Panzer: Photon timeout " + previous
                    + " -> " + wanted + " ms for F9 construction.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Panzer: Photon timeout guard failed - "
                    + ex.Message);
            }
        }

        /// <summary>
        /// Granaten zum Panzer dazulegen.
        ///
        /// Am 2026-08-29 stand im Spiel ein fertiger Panzer, dessen Geschuetz
        /// auf jeden Mausklick schwieg - im Rucksack lag keine einzige 2053.
        /// Die Begruendung stand nur im Log. Wer den Panzer per Taste
        /// hinstellt, bekommt seine erste Ladung jetzt mit dazu; alles Weitere
        /// kommt aus Loot oder dem Adminmenue.
        /// </summary>
        static void Munitionsbeigabe(GameObject car, bool panzer)
        {
            int menge = panzer ? RevivalPlugin.CfgTankSpawnAmmo.Value
                               : RevivalPlugin.CfgTurretSpawnAmmo.Value;
            if (menge <= 0) return;
            int id = panzer ? RevivalPlugin.CfgTankAmmoId.Value
                            : RevivalPlugin.CfgTurretAmmoId.Value;

            if (!panzer)
            {
                int boxes = 1;
                ItemDef def = RevivalPlugin.FindItem(id);
                if (def != null && def.Bullets > 0)
                    boxes = Mathf.Max(1, Mathf.CeilToInt((float)menge / def.Bullets));
                int added = AddAmmoToTrunk(car, id, boxes);
                if (added > 0)
                {
                    int rounds = def == null ? menge : added * def.Bullets;
                    RevivalPlugin.L.LogInfo("MTW: Munitionsbeigabe " + added
                        + " Behaelter Item " + id + " im Kofferraum ("
                        + rounds + " Schuss).");
                    Turret.Hinweis(rounds + Loc.T(" патронов в багажнике БТР",
                                                  " rounds in the APC trunk"), 4f);
                    return;
                }
                RevivalPlugin.L.LogWarning("MTW: Kofferraum nahm Item " + id
                    + " nicht an - versuche den lokalen Rucksack.");
            }

            string meldung;
            bool ok = Admin.GibItem(id, menge, out meldung);
            RevivalPlugin.L.LogInfo((panzer ? "Panzer" : "MTW")
                + ": Munitionsbeigabe " + menge + "x " + id + " - " + meldung);
            if (ok) Turret.Hinweis(menge
                + (panzer ? Loc.T(" снарядов в рюкзаке", " shells in the backpack")
                          : Loc.T(" патронов в рюкзаке", " rounds in the backpack")), 4f);
            else Turret.Hinweis(Loc.T("боеприпасы не выданы: ", "ammunition failed: ") + meldung, 6f);
        }

        static int AddAmmoToTrunk(GameObject car, int id, int boxes)
        {
            if (car == null || boxes <= 0) return 0;
            try
            {
                Type type = RevivalPlugin.TypeByName("ItemsContainer");
                if (type == null) return 0;
                Component[] containers = car.GetComponentsInChildren(type, true);
                if (containers.Length == 0) return 0;
                object trunk = containers[0];
                MethodInfo free = AccessTools.Method(type, "GetContainerFreeSlot",
                    Type.EmptyTypes, null);
                MethodInfo add = AccessTools.Method(type,
                    "AddNewContainerItemFromResources",
                    new Type[] { typeof(int) }, null);
                if (free == null || add == null) return 0;

                int added = 0;
                for (int i = 0; i < boxes; i++)
                {
                    int before = Convert.ToInt32(free.Invoke(trunk, null));
                    if (before < 0) break;
                    add.Invoke(trunk, new object[] { id });
                    int after = Convert.ToInt32(free.Invoke(trunk, null));
                    if (after == before) break;
                    added++;
                }
                return added;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("MTW: Munition in Kofferraum: " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Tank, Zustand und die drei Teile, sonst springt der Motor nicht an.
        /// </summary>
        static void Prepare(GameObject car)
        {
            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (vgsType == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: VehicleGameSystem nicht gefunden.");
                return;
            }

            Component vgs = car.GetComponent(vgsType);
            if (vgs == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: kein VehicleGameSystem am Fahrzeug.");
                return;
            }

            SetFloatField(vgs, "Fuel", 4000f);
            SetFloatField(vgs, "Durability", 2000f);

            MethodInfo set = AccessTools.Method(vgsType, "LocalSetVehicleComponent", null, null);
            if (set == null)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: LocalSetVehicleComponent fehlt - "
                    + "das Fahrzeug steht ohne Batterie, Kerze und Schluessel da.");
                return;
            }

            ParameterInfo[] ps = set.GetParameters();
            bool secondIsBool = ps.Length > 1 && ps[1].ParameterType == typeof(bool);
            for (int i = 0; i < Parts.Length; i++)
            {
                object second = secondIsBool ? (object)true : (object)1;
                try { set.Invoke(vgs, new object[] { Parts[i], second }); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Fahrzeugspawn: Teil " + Parts[i]
                        + " liess sich nicht setzen: " + ex.Message);
                }
            }
        }

        static void SetFloatField(object instance, string name, float value)
        {
            try
            {
                FieldInfo fi = AccessTools.Field(instance.GetType(), name);
                if (fi == null) return;
                if (fi.FieldType == typeof(float)) fi.SetValue(instance, value);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: Feld " + name
                    + " nicht gesetzt: " + ex.Message);
            }
        }

        static bool IsMasterClient()
        {
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (photon == null) return false;
            MethodInfo getter = AccessTools.PropertyGetter(photon, "isMasterClient");
            if (getter == null) getter = AccessTools.PropertyGetter(photon, "IsMasterClient");
            // Findet sich die Eigenschaft nicht, wird nicht geraten: dann laesst
            // Photon den Aufruf entweder zu oder meldet es selbst.
            if (getter == null) return true;
            return (bool)getter.Invoke(null, null);
        }

        static GameObject InstantiateSceneObject(string path, Vector3 position,
                                                 Quaternion rotation, bool panzer)
        {
            return InstantiateSceneObjectData(path, position, rotation,
                                              TankNetwork.SpawnData(panzer));
        }

        /// <summary>
        /// The shared PhotonNetwork.InstantiateSceneObject call. The data block
        /// travels in Photon's cached scene-instantiation event (event key 5) to
        /// every client and late joiner, where a DoInstantiate postfix reads its
        /// marker and rebuilds the vehicle - the same mechanism the T-72 uses.
        /// </summary>
        static GameObject InstantiateSceneObjectData(string path, Vector3 position,
                                                     Quaternion rotation, object[] data)
        {
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (photon == null) throw new MissingMemberException("PhotonNetwork nicht gefunden.");

            MethodInfo chosen = null;
            MethodInfo[] methods = photon.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "InstantiateSceneObject") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 5 && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType == typeof(Vector3)
                    && ps[2].ParameterType == typeof(Quaternion)
                    && ps[3].ParameterType == typeof(byte))
                {
                    chosen = m;
                    break;
                }
            }
            if (chosen == null)
                throw new MissingMethodException(
                    "PhotonNetwork.InstantiateSceneObject(string,Vector3,Quaternion,byte,object[])");

            return chosen.Invoke(null, new object[] {
                path, position, rotation, (byte)0, data })
                as GameObject;
        }

        static KeyCode TankKey()
        {
            if (_tankKeyParsed) return _tankKey;
            _tankKeyParsed = true;
            try
            {
                _tankKey = (KeyCode)Enum.Parse(typeof(KeyCode),
                                               RevivalPlugin.CfgTankKey.Value, true);
            }
            catch
            {
                _tankKey = KeyCode.F9;
                RevivalPlugin.L.LogWarning("Panzer: Key "
                    + RevivalPlugin.CfgTankKey.Value + " unbekannt, benutze F9.");
            }
            return _tankKey;
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try
            {
                _key = (KeyCode)Enum.Parse(typeof(KeyCode),
                                           RevivalPlugin.CfgSpawnCarKey.Value, true);
            }
            catch
            {
                _key = KeyCode.F7;
                RevivalPlugin.L.LogWarning("Fahrzeugspawn: SpawnCarKey "
                    + RevivalPlugin.CfgSpawnCarKey.Value + " unbekannt, benutze F7.");
            }
            return _key;
        }
    }
}
