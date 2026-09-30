// Next Day: Survival - Revival Toolkit
//
// UiKitCore - the Unity-free half of the shared in-game UI kit (task W-UI1).
// Everything here is plain C# 3.0 without a UnityEngine type, so the offline
// check (research/ui_kit_check.py) compiles this file UNCHANGED with the .NET
// 3.5 csc into a harness and runs it: the retry/backoff clock behind every
// "connection lost, retrying in N s" line, the toast ring, the keyboard
// navigation state, the resolution scale and the cached number/retry strings.
// The drawing half is Revival.UiKit.cs; the style rules are docs/UI_KIT.md.
//
// Allocation rule: nothing in here allocates after its first use of a value.
// Strings are created once per distinct value (UiNum, UiText) or once per
// change (UiMemo), never per repaint.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression bodies.
// UTF-8 (no BOM), compiled with /codepage:65001 like the rest.

using System;

namespace NextDayRevival
{
    /// <summary>Resolution scale: 1.0 at 1080 lines, times the player's
    /// [UI] Scale, clamped so a 720p window stays readable and a 4K one
    /// does not need a magnifier.</summary>
    public static class UiScale
    {
        public const float Min = 0.75f;
        public const float Max = 2.5f;

        public static float For(int screenHeight, float user)
        {
            if (screenHeight <= 0) screenHeight = 1080;
            if (user < 0.5f) user = 0.5f;
            if (user > 2f) user = 2f;
            float s = screenHeight / 1080f * user;
            if (s < Min) s = Min;
            if (s > Max) s = Max;
            // Quantised to 1/20 so a window dragged between monitors does not
            // rebuild the fonts for every pixel of height.
            return (float)Math.Round(s * 20f) / 20f;
        }
    }

    /// <summary>
    /// The state of anything that waits on a remote answer (master server,
    /// roster, shop). Never an endless "waiting": an attempt that gets no
    /// answer within Timeout counts as failed, a failure schedules the next
    /// attempt with a doubling delay (FirstDelay, 2x, 4x ... MaxDelay), and
    /// SecondsLeft is what the "retrying in N s" line shows.
    /// Usage per tick: if (clock.Step(now)) SendRequest(); the answer calls
    /// Succeed(now) or Fail(now).
    /// </summary>
    public sealed class UiRetryClock
    {
        public const int Idle = 0;
        public const int Connecting = 1;
        public const int Online = 2;
        public const int Lost = 3;

        public float FirstDelay = 2f;
        public float MaxDelay = 30f;
        public float Timeout = 10f;

        int _state;
        int _fails;
        double _since;
        double _next;

        public int State { get { return _state; } }
        public int Fails { get { return _fails; } }
        public double Since { get { return _since; } }

        public void Reset()
        {
            _state = Idle;
            _fails = 0;
            _since = 0.0;
            _next = 0.0;
        }

        /// <summary>An attempt went out now.</summary>
        public void Start(double now)
        {
            _state = Connecting;
            _since = now;
        }

        public void Succeed(double now)
        {
            _state = Online;
            _fails = 0;
            _since = now;
        }

        public void Fail(double now)
        {
            _fails++;
            _state = Lost;
            _since = now;
            _next = now + Delay(_fails);
        }

        /// <summary>Seconds before the next attempt after the n-th failure in a row.</summary>
        public float Delay(int fails)
        {
            float d = FirstDelay;
            for (int i = 1; i < fails && d < MaxDelay; i++) d *= 2f;
            return d > MaxDelay ? MaxDelay : d;
        }

        /// <summary>Times out a hung attempt and returns true exactly once
        /// when the next attempt is due (the clock is Connecting again).</summary>
        public bool Step(double now)
        {
            if (_state == Connecting && now - _since >= Timeout) Fail(now);
            if (_state == Lost && now >= _next)
            {
                _state = Connecting;
                _since = now;
                return true;
            }
            return false;
        }

        /// <summary>Whole seconds until the next attempt (0 unless Lost).</summary>
        public int SecondsLeft(double now)
        {
            if (_state != Lost) return 0;
            double s = _next - now;
            if (s <= 0.0) return 0;
            return (int)Math.Ceiling(s);
        }
    }

    /// <summary>One toast. Kind is a UiTone value.</summary>
    public struct UiToast
    {
        public string Text;
        public int Kind;
        public float Born;
        public float Life;
    }

    /// <summary>Toast tones, shared by status lines and toasts.</summary>
    public static class UiTone
    {
        public const int Info = 0;
        public const int Success = 1;
        public const int Warning = 2;
        public const int Error = 3;
        public const int Loading = 4;
    }

    /// <summary>A fixed ring of the newest toasts; no list growth, no
    /// per-frame work while it is empty. The same text pushed again while
    /// it is still shown refreshes that toast instead of stacking it.</summary>
    public sealed class UiToastRing
    {
        public const int Capacity = 4;
        public const float FadeIn = 0.15f;
        public const float FadeOut = 0.4f;

        readonly UiToast[] _items = new UiToast[Capacity];
        int _count;

        public int Count { get { return _count; } }

        /// <summary>Oldest first.</summary>
        public UiToast At(int i) { return _items[i]; }

        public void Push(string text, int kind, float now, float life)
        {
            if (text == null) return;
            for (int i = 0; i < _count; i++)
            {
                if (_items[i].Kind != kind || !string.Equals(_items[i].Text, text)) continue;
                _items[i].Born = now - FadeIn;
                _items[i].Life = life;
                return;
            }
            if (_count == Capacity)
            {
                for (int i = 1; i < Capacity; i++) _items[i - 1] = _items[i];
                _count--;
            }
            _items[_count].Text = text;
            _items[_count].Kind = kind;
            _items[_count].Born = now;
            _items[_count].Life = life;
            _count++;
        }

        /// <summary>Drops expired toasts; call once per frame while Count > 0.</summary>
        public void Prune(float now)
        {
            int w = 0;
            for (int r = 0; r < _count; r++)
            {
                if (now - _items[r].Born >= _items[r].Life) continue;
                if (w != r) _items[w] = _items[r];
                w++;
            }
            for (int i = w; i < _count; i++) _items[i].Text = null;
            _count = w;
        }

        public void Clear()
        {
            for (int i = 0; i < _count; i++) _items[i].Text = null;
            _count = 0;
        }

        /// <summary>0..1 opacity: a short fade in, a fade out at the end.</summary>
        public float Alpha(int i, float now)
        {
            float age = now - _items[i].Born;
            float left = _items[i].Life - age;
            float a = 1f;
            if (age < FadeIn) a = age / FadeIn;
            if (left < FadeOut) a = Math.Min(a, left / FadeOut);
            if (a < 0f) a = 0f;
            return a;
        }
    }

    /// <summary>
    /// Keyboard navigation for one window. Every focusable control asks
    /// Next() for its index in draw order; Up/Down (and Tab) move the focus,
    /// Enter activates the focused control, Left/Right adjust it (tabs,
    /// sliders, toggles). IMGUI runs a window several times per frame (layout,
    /// input, repaint); the order is the same in each pass, so the index is
    /// stable. The count of the last complete pass bounds the wrap-around.
    /// </summary>
    public sealed class UiNav
    {
        int _focus = -1;
        int _count;
        int _lastCount;
        bool _activate;
        int _adjust;

        public int Focus { get { return _focus; } }
        public int LastCount { get { return _lastCount; } }

        public void BeginPass()
        {
            _count = 0;
        }

        /// <summary>Ends a pass: key presses no control took are dropped.</summary>
        public void EndPass()
        {
            _lastCount = _count;
            if (_focus >= _lastCount) _focus = _lastCount - 1;
            _activate = false;
            _adjust = 0;
        }

        public int Next() { return _count++; }

        public bool IsFocused(int index) { return index == _focus; }

        public void SetFocus(int index) { _focus = index; }

        public void Move(int delta)
        {
            if (_lastCount <= 0) { _focus = -1; return; }
            if (_focus < 0) { _focus = delta > 0 ? 0 : _lastCount - 1; return; }
            _focus = ((_focus + delta) % _lastCount + _lastCount) % _lastCount;
        }

        public void Press() { if (_focus >= 0) _activate = true; }

        public void Adjust(int delta) { if (_focus >= 0) _adjust = delta; }

        public bool TakeActivate(int index)
        {
            if (!_activate || index != _focus) return false;
            _activate = false;
            return true;
        }

        public int TakeAdjust(int index)
        {
            if (_adjust == 0 || index != _focus) return 0;
            int d = _adjust;
            _adjust = 0;
            return d;
        }

        public void Clear()
        {
            _focus = -1;
            _activate = false;
            _adjust = 0;
        }
    }

    /// <summary>Cached decimal strings for 0..999; one allocation per value
    /// for the whole session. Outside that range it builds a new string.</summary>
    public static class UiNum
    {
        const int Cached = 1000;
        static readonly string[] _cache = new string[Cached];

        public static string Of(int v)
        {
            if (v < 0 || v >= Cached) return v.ToString();
            string s = _cache[v];
            if (s == null) { s = v.ToString(); _cache[v] = s; }
            return s;
        }
    }

    /// <summary>A text rebuilt only when its integer key changes:
    /// string t = m.Stale(v) ? m.Set(v, "Volume " + UiNum.Of(v) + " %") : m.Text;</summary>
    public sealed class UiMemo
    {
        int _key = int.MinValue;
        string _text;

        public string Text { get { return _text; } }

        public bool Stale(int key) { return _text == null || key != _key; }

        public string Set(int key, string text)
        {
            _key = key;
            _text = text;
            return text;
        }

        public void Invalidate() { _text = null; }
    }

    /// <summary>The kit's fixed status sentences, cached per value and
    /// language (0 = Russian, 1 = English, as Loc.Lang).</summary>
    public static class UiText
    {
        public const int MaxSeconds = 120;
        static readonly string[][] _retry = { new string[MaxSeconds + 1], new string[MaxSeconds + 1] };

        /// <summary>"Connection lost - retrying in N s".</summary>
        public static string Retry(int seconds, int lang)
        {
            int l = lang == 0 ? 0 : 1;
            if (seconds < 0) seconds = 0;
            if (seconds > MaxSeconds) seconds = MaxSeconds;
            string s = _retry[l][seconds];
            if (s != null) return s;
            if (seconds == 0)
                s = l == 0 ? "Нет связи - повторяем..." : "Connection lost - retrying...";
            else
                s = l == 0 ? "Нет связи - повтор через " + UiNum.Of(seconds) + " с"
                           : "Connection lost - retrying in " + UiNum.Of(seconds) + " s";
            _retry[l][seconds] = s;
            return s;
        }
    }
}
