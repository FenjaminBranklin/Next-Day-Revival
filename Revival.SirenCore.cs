// Shared client-side siren admission. No Unity dependency or frame allocations.
namespace NextDayRevival
{
    internal sealed class SirenGate
    {
        int _active;
        bool _latched;
        double _until;

        // A continuous alarm from any tower/event belongs to the current raid.
        // A short all-clear gap cannot replay a clip that has not finished yet.
        internal bool Request(ref bool requested, bool on, double now, double duration)
        {
            if (_active == 0 && now >= _until) _latched = false;
            if (requested != on)
            {
                _active += on ? 1 : -1;
                requested = on;
            }
            if (!on || _latched) return false;
            _latched = true;
            _until = now + duration;
            return true;
        }

        internal void Release(ref bool requested)
        {
            if (!requested) return;
            _active--;
            requested = false;
            // Keep the cooldown even if the sounding source was destroyed.
        }
    }
}
