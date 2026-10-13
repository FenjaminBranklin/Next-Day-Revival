// One ASCII line per manual refresh, without strings/boxing in the think path.
// Open only on console entry; drain on repaint/leave using a reusable byte array.
using System;
using System.IO;

namespace NextDayRevival
{
    internal static class RadarManualLog
    {
        static readonly RadarManualStats[] Pending = new RadarManualStats[64];
        static readonly byte[] Buffer = new byte[512];
        static FileStream _file;
        static int _count, _length;

        internal static void Open(string path)
        {
            if (_file != null) return;
            try { _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("RadarManual log open: " + ex.Message); }
        }

        internal static void Enqueue(RadarManualStats stats)
        {
            if (_file == null) return;
            if (_count == Pending.Length) Flush(); // Preserve every refresh if repaint is delayed.
            if (_file != null) Pending[_count++] = stats;
        }

        static void Label(string value)
        {
            for (int i = 0; i < value.Length; i++) Buffer[_length++] = (byte)value[i];
        }

        static void Number(int value)
        {
            if (value < 0) { Buffer[_length++] = (byte)'-'; value = -value; }
            int start = _length;
            do { Buffer[_length++] = (byte)('0' + value % 10); value /= 10; } while (value != 0);
            for (int a = start, b = _length - 1; a < b; a++, b--)
            { byte digit = Buffer[a]; Buffer[a] = Buffer[b]; Buffer[b] = digit; }
        }

        static void Field(string label, int value) { Label(label); Number(value); }

        internal static void Flush()
        {
            if (_file == null || _count == 0) return;
            try
            {
                for (int i = 0; i < _count; i++)
                {
                    RadarManualStats s = Pending[i];
                    _length = 0;
                    Field("RadarManual frame=", s.Frame);
                    Field(" candidates=", s.Candidates); Field(" listed=", s.Listed);
                    Field(" reject_invalid=", s.Invalid); Field(" reject_offline=", s.Offline);
                    Field(" reject_range=", s.Range);
                    Field(" defer_pool=", s.PendingPool);
                    Field(" old_shadow=", s.OldShadow); Field(" old_scope=", s.OldRange);
                    Field(" old_height=", s.OldHeight);
                    Field(" old_sweep=", s.OldSweep);
                    Field(" updated_near=", s.Near); Field(" updated_far=", s.Far);
                    Field(" deferred=", s.Deferred);
                    Buffer[_length++] = 10;
                    _file.Write(Buffer, 0, _length);
                }
                _file.Flush();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("RadarManual log write: " + ex.Message);
                _file.Close(); _file = null;
            }
            _count = 0;
        }

        internal static void Close()
        {
            Flush();
            if (_file != null) { _file.Close(); _file = null; }
        }
    }
}
