// Next Day: Survival - marker-bound airfield ambience. C# 3.0.
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class AirfieldAmbience
    {
        const string Nature = "sounds/ambient/nature/ambience_sounds/";
        static ConfigEntry<bool> _enabled;
        static readonly List<Emitter> _emitters = new List<Emitter>();
        static readonly List<Transform> _rooms = new List<Transform>();
        static readonly HashSet<int> _markers = new HashSet<int>();
        static readonly HashSet<string> _missing = new HashSet<string>();
        static AudioSource _donor;
        static AudioListener _listener;
        static AudioReverbZone _reverb;
        static float _scanAt, _retryAt, _listenerAt, _donorAt;
        // X perf-fix: F6 showed a 14-15 ms AirfieldAmbience.Tick peak every 5 s -
        // Scan's FindObjectOfType walks every object of every loaded scene. The
        // donor is looked up only while missing (every DonorEvery s), markers are
        // rescanned every RescanEvery s once emitters exist, the AudioListener is
        // searched at most once a second, and one missing clip loads per scan.
        const float RescanEvery = 30f, DonorEvery = 10f, ListenerEvery = 1f;
        static Type _managerType;
        static FieldInfo _ambientField;
        static PropertyInfo _managerInstance;   // static NetworkGameModesManager.Instance
        static int _donorScans;                 // scene-wide fallback searches left out after 3
        static Transform _reverbRoom;
        static AudioReverbPreset _reverbPreset;

        sealed class Emitter
        {
            internal Transform marker;
            internal AudioSource source;
            internal string path;
            internal Vector3 offset;
            internal float gain, radius, next;
            internal bool loop, area;
        }

        internal static void BindConfig(ConfigFile cfg)
        {
            _enabled = cfg.Bind("World", "EastAirfieldAmbience", true,
                "Ambient sounds and interior reverb at east airfield markers; requires EastTile.");
        }

        internal static void Tick()
        {
            if (!EastWorld.On || _enabled == null || !_enabled.Value)
            { Clear(); return; }
            float now = Time.realtimeSinceStartup;
            if (now < _retryAt) return;
            try
            {
                if (now >= _scanAt)
                {
                    _scanAt = now + (_emitters.Count > 0 ? RescanEvery : 5f);
                    Scan();
                }
                if (_emitters.Count == 0) { Reverb(false, Vector3.zero); return; }
                if ((_listener == null || !_listener.isActiveAndEnabled) && now >= _listenerAt)
                {
                    _listener = null;
                    _listenerAt = now + ListenerEvery;
                    foreach (AudioListener candidate in UnityEngine.Object.FindObjectsOfType(typeof(AudioListener)))
                        if (candidate.isActiveAndEnabled) { _listener = candidate; break; }
                }
                GameObject player = MapTools.LocalPlayer();
                bool active = player != null && _listener != null;
                Vector3 ear = active ? _listener.transform.position : Vector3.zero;
                float volume = active && _donor != null && !_donor.mute
                    ? Mathf.Clamp01(_donor.volume) : 0f;
                for (int i = _emitters.Count - 1; i >= 0; i--)
                {
                    Emitter e = _emitters[i];
                    if (e.marker == null)
                    {
                        if (e.source != null) UnityEngine.Object.Destroy(e.source.gameObject);
                        _emitters.RemoveAt(i);
                        continue;
                    }
                    if (e.source == null) continue;
                    Vector3 local = e.offset;
                    if (e.area)
                    {
                        // Nearest point of the apron, with full wind across its footprint.
                        local = Quaternion.Inverse(e.marker.rotation) * (ear - e.marker.position);
                        Vector3 half = e.marker.lossyScale * 0.5f;
                        local = new Vector3(Mathf.Clamp(local.x, -half.x, half.x),
                            Mathf.Clamp(local.y, -half.y, half.y), Mathf.Clamp(local.z, -half.z, half.z));
                    }
                    e.source.transform.position = e.marker.position + e.marker.rotation * local;
                    if (_donor != null && e.source.outputAudioMixerGroup != _donor.outputAudioMixerGroup)
                        e.source.outputAudioMixerGroup = _donor.outputAudioMixerGroup;
                    bool near = active && Vector3.Distance(ear, e.source.transform.position) < e.radius;
                    float target = near ? e.gain * volume : 0f;
                    // Immediate mute for settings changes; smooth distance entrances/exits.
                    e.source.volume = volume <= 0f ? 0f : Mathf.MoveTowards(e.source.volume, target, Time.deltaTime * 0.15f);
                    if (!near || volume <= 0f)
                    {
                        if (e.source.volume <= 0f) e.source.Stop();
                    }
                    else if (!e.source.isPlaying && (e.loop || now >= e.next))
                    {
                        e.source.Play();
                        e.next = now + e.source.clip.length + UnityEngine.Random.Range(12f, 35f);
                    }
                }
                Reverb(active, ear);
            }
            catch (Exception ex)
            {
                Clear();
                _retryAt = now + 10f;
                RevivalPlugin.L.LogWarning("AirfieldAmbience: retry in 10s: " + ex.Message);
            }
        }

        static void Scan()
        {
            if (_managerType == null)
            {
                _managerType = AccessTools.TypeByName("NetworkGameModesManager");
                if (_managerType != null)
                {
                    _ambientField = AccessTools.Field(_managerType, "_ambientSource");
                    _managerInstance = AccessTools.Property(_managerType, "Instance");
                    MethodInfo get = _managerInstance == null ? null : _managerInstance.GetGetMethod(true);
                    if (get == null || !get.IsStatic) _managerInstance = null;
                }
            }
            float now = Time.realtimeSinceStartup;
            bool foundScene = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || scene.name != "EastAirfield") continue;
                foundScene = true;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    Transform markers = root.transform.Find("Markers");
                    if (markers == null) continue;
                    foreach (Transform marker in markers)
                    {
                        if (!_markers.Add(marker.GetInstanceID())) continue;
                        string id = marker.name.Split('|')[0];
                        Vector3 half = marker.lossyScale * 0.5f;
                        if (id == "A1")
                        {
                            Add(marker, Nature + "wind_heavy", Vector3.zero, 0.55f, 110f, true, true);
                            Add(marker, "sounds/gw_scene_3_sounds/flag", new Vector3(half.x, 5f, half.z), 0.7f, 70f, true, false);
                        }
                        if (id == "H1")
                        {
                            Add(marker, Nature + "wind_metal", new Vector3(0f, 0f, -half.z + 4f), 0.65f, 150f, false, false);
                            Add(marker, Nature + "billboard", new Vector3(half.x - 4f, 0f, 0f), 0.8f, 130f, false, false);
                        }
                        if (id == "D1")
                            Add(marker, Nature + "current_2", new Vector3(-half.x + 12f, -half.y + 6f, 0f), 0.65f, 85f, true, false);
                        if (id == "R2")
                        {
                            for (int j = -1; j <= 1; j++)
                                Add(marker, Nature + "forest_1", new Vector3(half.x + 60f, 0f, j * half.z * 0.7f), 0.6f, 230f, true, false);
                            Add(marker, Nature + "crow", new Vector3(half.x + 110f, 15f, half.z * 0.7f), 0.7f, 600f, false, false);
                        }
                        if (id == "H1" || id == "D2a" || id == "D2b") _rooms.Add(marker);
                    }
                }
            }
            if (!foundScene) { Clear(); _scanAt = Time.realtimeSinceStartup + 5f; return; }
            if (_ambientField != null && _donor == null && now >= _donorAt)
            {
                _donorAt = now + DonorEvery;
                object manager = _managerInstance == null ? null : _managerInstance.GetValue(null, null);
                // The scene-wide search is the 14 ms of the old peak: a fallback,
                // three times at most, only while the singleton is not there.
                if (manager == null && _donorScans < 3)
                {
                    _donorScans++;
                    manager = UnityEngine.Object.FindObjectOfType(_managerType);
                }
                _donor = manager != null ? _ambientField.GetValue(manager) as AudioSource : null;
            }
            // Clips can become available after the additive scene loads. Retry
            // missing clips - one per scan, a disk load is not a frame's work.
            for (int i = 0; i < _emitters.Count; i++)
            {
                Emitter e = _emitters[i];
                if (e.source != null || e.marker == null) continue;
                Load(e);
                if (e.source == null) continue;                 // still missing: a cheap lookup
                _scanAt = Mathf.Min(_scanAt, now + 0.5f);       // loaded one: the next one soon
                break;
            }
        }

        static void Add(Transform marker, string path, Vector3 offset, float gain,
            float radius, bool loop, bool area)
        {
            Emitter e = new Emitter();
            e.marker = marker; e.path = path; e.offset = offset; e.gain = gain;
            e.radius = radius; e.loop = loop; e.area = area;
            e.next = Time.realtimeSinceStartup + UnityEngine.Random.Range(2f, 10f);
            _emitters.Add(e);
        }

        static void Load(Emitter e)
        {
            AudioClip clip = Resources.Load(e.path, typeof(AudioClip)) as AudioClip;
            if (clip == null)
            {
                if (_missing.Add(e.path)) RevivalPlugin.L.LogWarning("AirfieldAmbience: missing clip, will retry: " + e.path);
                return;
            }
            GameObject go = new GameObject("NDR Airfield " + e.marker.name + " " + clip.name);
            SceneManager.MoveGameObjectToScene(go, e.marker.gameObject.scene);
            e.source = go.AddComponent<AudioSource>();
            e.source.playOnAwake = false;
            e.source.clip = clip;
            e.source.loop = e.loop;
            e.source.volume = 0f;
            e.source.spatialBlend = 1f;
            e.source.dopplerLevel = 0f;
            e.source.rolloffMode = AudioRolloffMode.Linear;
            e.source.minDistance = e.area ? 15f : 8f;
            e.source.maxDistance = e.radius;
            RevivalPlugin.L.LogInfo("AirfieldAmbience: " + e.marker.name + " <- " + e.path);
        }

        static void Reverb(bool active, Vector3 ear)
        {
            Transform room = null;
            for (int i = _rooms.Count - 1; i >= 0; i--)
            {
                Transform t = _rooms[i];
                if (t == null) { _rooms.RemoveAt(i); continue; }
                Vector3 d = Quaternion.Inverse(t.rotation) * (ear - t.position);
                // Piece markers include 2m padding. Inset it so exterior walls stay dry.
                Vector3 h = t.lossyScale * 0.5f - new Vector3(2.8f, 0f, 2.8f);
                if (active && Mathf.Abs(d.x) < h.x && Mathf.Abs(d.y) < h.y && Mathf.Abs(d.z) < h.z)
                { room = t; break; }
            }
            if (room == null)
            {
                if (_reverb != null && _reverb.enabled) _reverb.enabled = false;
                _reverbRoom = null;
                return;
            }
            if (_reverb == null)
            {
                GameObject go = new GameObject("NDR Airfield interior reverb");
                SceneManager.MoveGameObjectToScene(go, room.gameObject.scene);
                _reverb = go.AddComponent<AudioReverbZone>();
                _reverb.minDistance = 1f;
                _reverb.maxDistance = 2f;
            }
            // Unity reverb zones are spheres: put a small sphere at the listener only
            // while inside the marker box. Never alter vanilla zones or listener effects.
            _reverb.transform.position = ear;
            if (room != _reverbRoom)
            {
                // The name is read once per room entered, not every frame inside.
                _reverbRoom = room;
                _reverbPreset = room.name.StartsWith("H1|") ? AudioReverbPreset.Hangar : AudioReverbPreset.Stoneroom;
            }
            if (_reverb.reverbPreset != _reverbPreset) _reverb.reverbPreset = _reverbPreset;
            _reverb.enabled = true;
        }

        static void Clear()
        {
            foreach (Emitter e in _emitters)
                if (e.source != null) { e.source.Stop(); UnityEngine.Object.Destroy(e.source.gameObject); }
            if (_reverb != null) { _reverb.enabled = false; UnityEngine.Object.Destroy(_reverb.gameObject); }
            _reverb = null; _donor = null; _listener = null; _reverbRoom = null; _donorScans = 0;
            _emitters.Clear(); _rooms.Clear(); _markers.Clear();
            _scanAt = 0f;
        }
    }
}
