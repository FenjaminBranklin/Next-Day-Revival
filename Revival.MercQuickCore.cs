// Mouse gestures for merc commands. Pure C# 3.0; tested unchanged offline.
namespace NextDayRevival
{
    internal struct MercQuickGesture
    {
        internal const int Capture = 1, Attack = 2, Rally = 4, Open = 8, Close = 16;
        internal const float HoldSeconds = 0.28f, DoubleSeconds = 0.28f;
        bool _down, _pending, _wheel, _swallow;
        float _pressed, _released;

        internal bool Busy { get { return _down || _pending || _wheel || _swallow; } }

        internal void Cancel()
        {
            _down = false; _pending = false; _wheel = false; _swallow = false;
        }

        internal int Step(float now, bool down, bool held, bool up)
        {
            int action = 0;
            if (_pending && now - _released >= DoubleSeconds)
            { _pending = false; action |= Attack; }
            if (down)
            {
                if (_pending)
                {
                    _pending = false; _down = false; _swallow = true;
                    return Rally;
                }
                _down = true; _pressed = now;
            }
            if (_swallow)
            {
                if (up || !held) _swallow = false;
                return action;
            }
            // Opening on release too handles low-FPS frames which straddle hold.
            if (_down && (held || up) && now - _pressed >= HoldSeconds && !_wheel)
            { _wheel = true; action |= Open; }
            if (_down && up)
            {
                _down = false;
                if (_wheel) { _wheel = false; action |= Close; }
                else { _pending = true; _released = now; action |= Capture; }
            }
            else if (_down && !held)
            { Cancel(); } // focus/input loss must not invent a release
            return action;
        }
    }
}
