// Mouse gestures for merc commands. Pure C# 3.0; tested unchanged offline.
namespace NextDayRevival
{
    internal struct MercQuickGesture
    {
        internal const int Capture = 1, Attack = 2, Move = 4, Open = 8, Close = 16;
        internal const float HoldSeconds = 0.28f, DoubleSeconds = 0.30f;
        bool _down, _pending, _wheel, _second;
        float _pressed, _released;

        internal bool Busy { get { return _down || _pending || _wheel; } }

        internal void Cancel()
        {
            _down = false; _pending = false; _wheel = false; _second = false;
        }

        internal int Step(float now, bool down, bool held, bool up)
        {
            int action = 0;
            if (_pending && now - _released >= DoubleSeconds) _pending = false;
            if (down)
            {
                _second = _pending;
                _pending = false;
                _down = true; _pressed = now;
            }
            // Opening on release too handles low-FPS frames which straddle hold.
            if (_down && (held || up) && now - _pressed >= HoldSeconds && !_wheel)
            { _wheel = true; action |= Open; }
            if (_down && up)
            {
                _down = false;
                if (_wheel) { _wheel = false; action |= Close; }
                else
                {
                    // The first tap is immediate. A completed second tap
                    // supersedes it using the aim at its own release.
                    _pending = !_second; _released = now;
                    action |= Capture | (_second ? Move : Attack);
                }
                _second = false;
            }
            else if (_down && !held)
            { Cancel(); } // focus/input loss must not invent a release
            return action;
        }
    }
}
