// Recorder-only work: bound formatting on the main thread; file IO has no Unity access.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace NextDayRevival
{
    internal interface ITextSaveSource
    {
        void Reset();
        bool Next(out string line);
    }

    internal sealed class SlicedTextSave
    {
        internal const int LinesPerTick = 128;
        internal const double BudgetMs = 0.2;
        static readonly WaitCallback Writer = Write;
        readonly ITextSaveSource _source;
        List<string> _lines;
        volatile List<string> _spare;
        string _path, _buildingPath;
        bool _pending;
        volatile bool _writing;
        volatile Exception _error;

        internal SlicedTextSave(ITextSaveSource source) { _source = source; }
        internal bool Formatting { get { return !_writing && (_pending || _lines != null); } }
        internal bool Busy { get { return _pending || _lines != null || _writing; } }
        internal void Request(string path) { _path = path; _pending = true; }
        internal Exception TakeError() { Exception e = _error; _error = null; return e; }

        // Plugin shutdown is not a frame tick. Complete queued edits before
        // losing the main-thread source, including an immediate quit after F6.
        internal void Finish()
        {
            while (Busy)
            {
                if (_writing) Thread.Sleep(1);
                else Tick();
            }
        }

        internal void Tick()
        {
            if (_writing || (!_pending && _lines == null)) return;
            long start = Stopwatch.GetTimestamp();
            if (_lines == null)
            {
                _pending = false;
                _buildingPath = _path;
                _source.Reset();
                _lines = _spare;
                _spare = null;
                if (_lines == null) _lines = new List<string>();
            }
            for (int i = 0; i < LinesPerTick; i++)
            {
                if ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency >= BudgetMs) return;
                string line;
                if (!_source.Next(out line))
                {
                    // Hand over immutable text only. The next request waits for
                    // this write, so an older snapshot cannot overwrite a newer one.
                    Job job = new Job(this, _buildingPath, _lines);
                    _writing = true;
                    _lines = null;
                    if (!ThreadPool.QueueUserWorkItem(Writer, job))
                    {
                        _error = new IOException("Could not queue route save.");
                        _writing = false;
                    }
                    return;
                }
                if (line != null) _lines.Add(line);
            }
        }

        sealed class Job
        {
            internal readonly SlicedTextSave Owner;
            internal readonly string Path;
            internal readonly List<string> Lines;
            internal Job(SlicedTextSave owner, string path, List<string> lines)
            { Owner = owner; Path = path; Lines = lines; }
        }

        static void Write(object state)
        {
            Job job = (Job)state;
            try
            {
                string staging = job.Path + ".ndr-saving";
                using (StreamWriter writer = new StreamWriter(staging))
                    for (int i = 0; i < job.Lines.Count; i++) writer.WriteLine(job.Lines[i]);
                // A scanner/reader can briefly deny delete sharing. Retry on
                // the IO worker, with the previous complete file still intact.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        // Inherited staging ACLs need no metadata merge privileges.
                        if (File.Exists(job.Path)) File.Replace(staging, job.Path, null, true);
                        else File.Move(staging, job.Path);
                        break;
                    }
                    catch (IOException)
                    {
                        if (attempt >= 19) throw;
                        Thread.Sleep(10);
                    }
                }
            }
            catch (Exception ex) { job.Owner._error = ex; }
            finally
            {
                job.Lines.Clear();
                job.Owner._spare = job.Lines;
                job.Owner._writing = false;
            }
        }
    }
}
