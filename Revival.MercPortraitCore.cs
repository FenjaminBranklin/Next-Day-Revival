// Equipment identity shared with mercportraits.py and editor/mercs.js.
using System;
using System.Globalization;
using System.IO;

namespace NextDayRevival
{
    internal static class MercPortraitKey
    {
        internal const int Size = 384;
        // Charge event work against future frames, reserving 0.025 ms of the
        // 0.1 ms budget for guards/drawing. The capture itself is a one-shot burst.
        internal static int DelayFrames(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0)
                throw new IOException("Portrait capture timing");
            return (int)Math.Min(1000000.0, Math.Max(1.0, Math.Ceiling(milliseconds / 0.075)));
        }
        internal static string Of(int[] items)
        {
            if (items == null || items.Length != 7) throw new IOException("Portrait equipment count");
            string key = "merc-v1";
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] < (i == 0 ? 1 : 0) || items[i] > 99999)
                    throw new IOException("Portrait equipment ID");
                key += "-" + items[i].ToString(CultureInfo.InvariantCulture);
            }
            return key;
        }

        internal static int[] Parse(string row, out string key, out string ticket)
        {
            string[] cols = row.Split('\t');
            if (cols.Length != 9) throw new IOException("Portrait job columns");
            int[] items = new int[7];
            for (int i = 0; i < items.Length; i++)
                if (!int.TryParse(cols[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out items[i]))
                    throw new IOException("Portrait job number");
            key = Of(items);
            if (key != cols[0]) throw new IOException("Portrait job identity");
            ticket = cols[8];
            if (ticket.Length != 64) throw new IOException("Portrait job ticket");
            for (int i = 0; i < ticket.Length; i++)
                if (!(ticket[i] >= '0' && ticket[i] <= '9') && !(ticket[i] >= 'a' && ticket[i] <= 'f'))
                    throw new IOException("Portrait job ticket alphabet");
            return items;
        }
    }
}
