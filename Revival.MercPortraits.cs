// Portraits are event work. Tick only wakes a persistent worker at 10 s intervals.
// Native cloning, PNG readback and encoding run once per loadout, on GUI repaint.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercPortraits
    {
        internal sealed class Entry
        {
            internal string Key;
            internal int[] Items;
            internal Texture2D Texture;
            internal float Retry;
            internal bool Published;
        }
        sealed class Job { internal string Key, Ticket; internal int[] Items; }
        static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        static readonly List<Entry> Pending = new List<Entry>();
        static readonly AutoResetEvent PollWake = new AutoResetEvent(false);
        static string _directory;
        static volatile bool _busy, _ready;
        static Job[] _jobs;
        static string _failure, _lastFailure;
        static float _nextPoll, _nextCapture;
        static int _cursor;
        static int _captureAfterFrame;
        static MercPortraitHost _host;
        static Thread _worker;

        internal static void Install()
        {
            _directory = Path.Combine(Paths.CachePath, "ndr-merc-portraits");
            if (_host != null) return;
            GameObject host = new GameObject("NDR merc portrait service");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _host = host.AddComponent<MercPortraitHost>();
            if (_worker == null)
            {
                _worker = new Thread(new ThreadStart(PollLoop));
                _worker.IsBackground = true;
                _worker.Name = "NDR merc portrait bridge";
                _worker.Start();
            }
        }

        internal static void ProfilesChanged(List<Mercs.Profile> profiles)
        {
            Install();
            foreach (Entry old in Entries.Values) old.Published = false;
            // Reuse identical equipment even when price, skills or names change.
            for (int i = 0; i < profiles.Count; i++)
            {
                Mercs.Profile p = profiles[i];
                int[] items = new int[] { p.Weapon, p.Headwear, p.Mask, p.Body, p.Hands, p.Legs, p.Backpack };
                p.PortraitKey = MercPortraitKey.Of(items);
                Add(p.PortraitKey, items).Published = true;
            }
        }

        static Entry Add(string key, int[] items)
        {
            Entry entry;
            if (Entries.TryGetValue(key, out entry)) return entry;
            // Keep published portraits pinned; evict only older editor drafts.
            if (Entries.Count >= 160)
            {
                int drop = -1;
                for (int i = 0; i < Pending.Count; i++)
                    if (!Pending[i].Published) { drop = i; break; }
                if (drop < 0) throw new IOException("Portrait session cache full");
                Entry old = Pending[drop];
                if (old.Texture != null) UnityEngine.Object.Destroy(old.Texture);
                Entries.Remove(old.Key); Pending.RemoveAt(drop); _cursor = 0;
            }
            entry = new Entry(); entry.Key = key; entry.Items = items;
            Entries.Add(key, entry); Pending.Add(entry);
            return entry;
        }

        internal static Entry For(Mercs.Profile p)
        {
            Entry entry;
            return p.PortraitKey != null && Entries.TryGetValue(p.PortraitKey, out entry) ? entry : null;
        }

        internal static void Tick()
        {
            FrameProf.S(FrameProf.S_MercPortraitT);
            try
            {
                if (_host == null || OfflineStart.Active || _busy || _ready || Time.realtimeSinceStartup < _nextPoll) return;
                _nextPoll = Time.realtimeSinceStartup + 10f;
                _busy = true;
                PollWake.Set();
            }
            finally { FrameProf.E(FrameProf.S_MercPortraitT); }
        }

        static void PollLoop()
        {
            for (;;)
            {
                PollWake.WaitOne();
                Poll();
            }
        }

        static void Poll()
        {
            try
            {
                string pin, address = LiveRoutes.PortraitEndpoint(out pin);
                if (address.Length == 0) return;
                string[] rows = LiveRoutes.Fetch(address, pin, "").Split('\n');
                if (rows.Length > 66 || rows[0].TrimEnd('\r') != "NDR-MERC-PORTRAITS-1")
                    throw new IOException("Portrait job snapshot");
                List<Job> jobs = new List<Job>();
                bool uploaded = false;
                _failure = null;
                for (int i = 1; i < rows.Length; i++)
                {
                    string row = rows[i].TrimEnd('\r');
                    if (row.Length == 0) continue;
                    Job job = new Job();
                    job.Items = MercPortraitKey.Parse(row, out job.Key, out job.Ticket);
                    jobs.Add(job);
                    string file = Path.Combine(_directory, job.Key + ".png");
                    if (!uploaded && File.Exists(file))
                    {
                        uploaded = true;
                        try
                        {
                            byte[] png = File.ReadAllBytes(file);
                            LiveRoutes.UploadPortrait(address + "/" + job.Key, pin, job.Ticket, png);
                        }
                        catch (Exception ex) { _failure = ex.Message; }
                    }
                }
                _jobs = jobs.ToArray();
            }
            catch (Exception ex) { _jobs = null; _failure = ex.Message; }
            finally { _ready = true; _busy = false; }
        }

        internal static void Repaint()
        {
            if (_ready)
            {
                if (_jobs != null)
                    for (int i = 0; i < _jobs.Length; i++) Add(_jobs[i].Key, _jobs[i].Items);
                if (_failure != _lastFailure)
                {
                    _lastFailure = _failure;
                    if (_failure != null) RevivalPlugin.L.LogWarning("MercPortrait: editor bridge " + _failure);
                }
                _jobs = null; _ready = false;
            }
            float now = Time.realtimeSinceStartup;
            if (Pending.Count == 0 || now < _nextCapture || Time.frameCount - _captureAfterFrame < 0) return;
            // One portrait every five seconds; no scans, physics or AI per frame.
            if (_cursor >= Pending.Count) _cursor = 0;
            Entry entry = Pending[_cursor++];
            if (entry.Texture != null || now < entry.Retry) return;
            _nextCapture = now + 5f;
            FrameProf.S(FrameProf.S_MercPortraitCapture);
            long began = Stopwatch.GetTimestamp();
            Texture2D texture = null;
            try
            {
                string file = Path.Combine(_directory, entry.Key + ".png");
                if (File.Exists(file))
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    if (!texture.LoadImage(File.ReadAllBytes(file)) || texture.width != MercPortraitKey.Size
                        || texture.height != MercPortraitKey.Size)
                    { UnityEngine.Object.Destroy(texture); texture = null; }
                }
                if (texture == null)
                {
                    texture = MercPortraitRender.Capture(entry.Items);
                    Directory.CreateDirectory(_directory);
                    string temporary = file + ".tmp";
                    File.WriteAllBytes(temporary, texture.EncodeToPNG());
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(temporary, file);
                }
                texture.name = entry.Key; texture.filterMode = FilterMode.Bilinear;
                entry.Texture = texture;
                RevivalPlugin.L.LogInfo("MercPortrait: ready " + entry.Key);
            }
            catch (Exception ex)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                entry.Retry = now + 60f;
                RevivalPlugin.L.LogWarning("MercPortrait: capture " + entry.Key + " - " + ex.Message);
            }
            finally
            {
                double milliseconds = (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency;
                _captureAfterFrame = Time.frameCount + MercPortraitKey.DelayFrames(milliseconds);
                FrameProf.E(FrameProf.S_MercPortraitCapture);
            }
        }
    }

    public sealed class MercPortraitHost : MonoBehaviour
    {
        void LateUpdate()
        {
            // Batchmode has no GUI repaint; the isolated arena still captures.
            if (OfflineStart.Active) MercPortraits.Repaint();
        }
        void OnGUI()
        {
            if (!OfflineStart.Active && Event.current.type == EventType.Repaint) MercPortraits.Repaint();
        }
    }
}
