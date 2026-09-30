// Paratroop mission decisions; no Unity scene queries or per-tick allocation.
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class ParaObjective
    {
        internal const byte Regroup = 0, Advance = 1, Hold = 2;
        internal const float Every = 0.5f, Step = 42f, Arrive = 12f;
        internal const float RegroupLimit = 90f;
        internal const float MovingRadius = 160f, HoldRadius = 70f;
        public readonly Vector3[] Slots;
        public readonly Vector3 Objective;
        public Vector3 Anchor;
        public byte Phase;
        public int Version;
        public float Next, HoldUntil;
        readonly float _holdSeconds;
        float _regroupUntil;
        bool _started;

        internal ParaObjective(int count, Vector3 rally, Vector3 objective, float holdSeconds)
        {
            Slots = new Vector3[count];
            Anchor = rally; Objective = objective; _holdSeconds = holdSeconds;
            Layout();
        }

        static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        void Layout()
        {
            // Spread slots stay stable through turns and casualty losses.
            for (int i = 0; i < Slots.Length; i++)
            {
                float a = i * 2.39996f, r = 14f * Mathf.Sqrt(i);
                Slots[i] = Anchor + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            }
            Version++;
        }

        internal bool Due(float now) { return now >= Next; }

        internal void Tick(GroundBrain brain, float now)
        {
            if (!Due(now)) return;
            Next = now + Every;
            if (!_started) { _started = true; _regroupUntil = now + RegroupLimit; }
            // Combat/search orders take priority; preserve the mission to resume.
            if (brain.Phase != GroundPhase.Calm)
            {
                if (Phase == Regroup) _regroupUntil = now + RegroupLimit;
                return;
            }
            int alive = 0, near = 0;
            for (int i = 0; i < brain.Count; i++)
            {
                if (!brain.Men[i].Alive) continue;
                alive++;
                if (Flat(brain.Men[i].Pos - Slots[i]) <= Arrive) near++;
            }
            if (alive == 0 || Phase == Hold) return;
            // Three quarters keep the body together without one blocked man
            // halting everyone forever. The laggards keep their catch-up slots.
            int quorum = (alive * 3 + 3) / 4;
            if (near < quorum && (Phase != Regroup || now < _regroupUntil)) return;
            if (Phase == Regroup) Phase = Advance;
            Vector3 delta = Objective - Anchor; delta.y = 0f;
            float distance = delta.magnitude;
            if (distance <= 0.1f)
            {
                Phase = Hold;
                HoldUntil = now + _holdSeconds;
                return;
            }
            Anchor = distance <= Step ? Objective : Anchor + delta * (Step / distance);
            Layout();
        }
    }
}
