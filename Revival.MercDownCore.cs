// Z K7: merc-only incapacitation and rescue clock. No Unity or world queries.
namespace NextDayRevival
{
    internal sealed class MercDownState
    {
        internal const float BleedSeconds = 150f, Reach = 8.4f;
        internal bool Down, Final, Healing, Pending;
        internal float Until, Started, Seconds;
        internal int Item, Healer, Serial;
        internal bool Enter(float now)
        {
            if (Down || Final) return false;
            Down = true; Healing = false; Pending = false; Item = 0; Healer = 0;
            Until = now + BleedSeconds; return true;
        }
        internal bool Expired(float now) { return Down && now >= Until; }
        internal void Kill() { Final = true; Healing = false; Pending = false; }
        internal bool Begin(float now, int item, int healer)
        {
            if (!Down || Final || Healing || Expired(now) || !MercMedicine.Item(item)) return false;
            Item = item; Healer = healer; Started = now; Seconds = MercMedicine.Seconds(item);
            Healing = true; Pending = false; return true;
        }
        internal bool Ready(float now) { return Down && !Final && Healing && !Expired(now) && now >= Started + Seconds; }
        internal void MedicTimer(float seconds) { if (Healing && Healer != 0) Seconds = seconds; }
        internal void Cancel() { Healing = false; Pending = false; }
        internal bool Revive(float now)
        {
            if (!Ready(now)) return false;
            Down = false; Healing = false; Pending = false; Healer = 0; Item = 0; return true;
        }
    }
}
