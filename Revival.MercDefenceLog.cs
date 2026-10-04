// H M3: man air defence diagnostics, kept in the release. Every line starts
// with "MercAD:" in the BepInEx log:
// - the order: where the owner stands, every post with its state (free,
//   destroyed, held by whom, reserved), then one line per merc - his post
//   (name + position) and the path from where he stands (tower stairs, then
//   NavMesh: complete/partial/invalid), or why he got none;
// - every 5 s per merc whose post is not reached: distance, step state, the
//   order he really runs, climb, agent, what overrides the post;
// - on arrival: seat taken, or the lease rule that refuses it (every 5 s).
// Strings are built only when a line is written (the order, 5 s, arrival),
// never per frame. No new frame hook: AirDefenceTick (F6 MercAirfieldT).
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal static partial class Mercs
    {
        static readonly StringBuilder DefenceLine = new StringBuilder(320);
        static NavMeshPath _defencePath;

        static void DefenceLog(string line)
        {
            RevivalPlugin.L.LogInfo("MercAD: " + line);
        }

        static string DefenceV(Vector3 v)
        {
            return "(" + v.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " + v.y.ToString("0.0", CultureInfo.InvariantCulture)
                + ", " + v.z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }

        static string DefenceF(float f) { return f.ToString("0.0", CultureInfo.InvariantCulture); }

        static float DefenceFlat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static string DefenceLabel(int post)
        {
            if (post == MercAA.Radar) return "radar#4";
            Flak.Gun g = Flak.ByIndex(post);
            if (g == null) return "post#" + post;
            int pit = FlakPositionsCore.Position(post);
            return (g.ShortRange ? "ZU-23#" : "52-K#") + post + (pit >= 0 ? " " + FlakPositionsCore.Name[pit] : "");
        }

        // Why a post is or is not open for this order (the plan's rules).
        static string DefencePostState(int post, Component probe)
        {
            bool radar = post == MercAA.Radar;
            Flak.Gun g = radar ? null : Flak.ByIndex(post);
            if (!radar && g == null) return "no gun";
            if (!radar && g.Town) return "town gun (not airfield)";
            if (!DefenceStanding(post)) return radar ? "radar not working" : "destroyed";
            Vector3 at; Quaternion rot;
            if (!MercAA.Pose(post, out at, out rot)) return "no seat pose (gun/console not built)";
            if (DefencePriority[post] < 0) return "not planned (second ZU-23)";
            string where = " at " + DefenceV(at);
            DefenceMember crew = DefenceCrew(post);
            if (crew == null) for (int i = 0; i < Defence.Count; i++) if (Defence[i].Post == post) crew = Defence[i];
            if (crew != null) return "-> #" + crew.Record.Id + where;
            if (probe != null && MercAA.HostileCrew(post, probe) != null) return "enemy crew (cleared first)" + where;
            if (!MercAA.AvailableForOrder(post))
            {
                if (radar) return (TowerRadar.OperatorActor >= 0 ? "player actor " + TowerRadar.OperatorActor + " at the console"
                    : RadarOperator.Alive ? "own-side NPC operator" : "console busy") + where;
                if (g == Flak._manned) return "you sit at this gun" + where;
                if (Time.time < g.ClaimedUntil) return "player actor " + g.ClaimActor + " at the gun" + where;
                if (Flak.Up(g.Gunner)) return "own-side native gunner on the seat" + where;
                return "not available" + where;
            }
            MercAAPost held = MercAA.Held(post);
            if (held != null && DefenceOf(held.Ai) == null) return "held by merc of actor " + held.Actor + where;
            for (int i = 0; i < _roster.Count; i++)
            {
                Record other = _roster[i];
                if (other.Dead || other.Deserted || other.Down.Down || DefenceFor(other) != null) continue;
                if (MercStations.Matches(other.Order, DefenceSeats[post])) return "reserved by the order of #" + other.Id + where;
            }
            return "free, no merc left" + where;
        }

        // From where he stands: the tower's stairs first, then the NavMesh.
        static string DefencePath(Record r, Vector3 at)
        {
            if (r.Unit == null || r.Unit.Ai == null) return "not spawned";
            Vector3 from = r.Unit.Ai.transform.position;
            string lead = "";
            if (TowerRoof.ManUp(from))
            {
                lead = "on the tower: stairs down to the foot " + DefenceV(TowerRoof.FootNav) + ", then ";
                from = TowerRoof.FootNav;
            }
            NavMeshHit a, b;
            if (!NavMesh.SamplePosition(from, out a, 6f, NavMesh.AllAreas)) return lead + "NavMesh FAILED: none within 6 u of " + DefenceV(from);
            if (!NavMesh.SamplePosition(at, out b, 8f, NavMesh.AllAreas)) return lead + "NavMesh FAILED: none within 8 u of the seat";
            if (_defencePath == null) _defencePath = new NavMeshPath();
            bool found = NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, _defencePath);
            Vector3[] corners = _defencePath.corners;
            float length = 0f;
            for (int i = 1; i < corners.Length; i++) length += Vector3.Distance(corners[i - 1], corners[i]);
            float gap = corners.Length > 0 ? DefenceFlat(corners[corners.Length - 1], at) : -1f;
            string status = !found ? "FAILED" : _defencePath.status == NavMeshPathStatus.PathComplete ? "ok"
                : _defencePath.status == NavMeshPathStatus.PathPartial ? "PARTIAL" : "INVALID";
            return lead + "NavMesh " + status + ", " + corners.Length + " corners, " + DefenceF(length) + " u, ends "
                + DefenceF(gap) + " u from the seat (seat sample " + DefenceF(DefenceFlat(b.position, at)) + " u off)";
        }

        static string DefenceWhyNone(DefenceMember m, int open)
        {
            Record r = m.Record;
            if (r.Unpaid) return "unpaid";
            if (r.Down.Down || r.Down.Final) return "downed";
            if (r.Unit != null && r.Unit.Deserting) return "deserting";
            return "reserve: " + open + " open post(s) for " + Defence.Count + " mercs; keeps " + MercUi.OrderText(r);
        }

        static void DefenceLogRefused(bool owner, bool built, float sq)
        {
            if (!owner) { DefenceLog("order refused: no owner body"); return; }
            if (!built) { DefenceLog("order refused: tower/radar not built in this region"); return; }
            if (sq < 0f) { DefenceLog("order refused: no paid, living, picked mercs"); return; }
            DefenceLog("order refused: owner " + DefenceF(Mathf.Sqrt(sq)) + " u from the tower (limit 1680 u)");
        }

        static void DefenceLogOrder()
        {
            Component probe = null;
            for (int i = 0; i < Defence.Count && probe == null; i++) if (Defence[i].Record.Unit != null) probe = Defence[i].Record.Unit.Ai;
            Vector3 owner = OwnerPosition;
            DefenceLog("order given: owner at " + DefenceV(owner) + (TowerRoof.ManUp(owner) ? " (on the tower)" : "")
                + ", " + Defence.Count + " mercs, master " + MercAA.Authority + ", local actor " + MercAA.LocalActor());
            int open = 0;
            for (int post = 0; post < DefenceSeats.Length; post++)
            {
                string state = DefencePostState(post, probe);
                if (state.Length > 1 && state[0] == '-' && state[1] == '>') open++;
                DefenceLog("  post " + DefenceLabel(post) + ": " + state);
            }
            for (int i = 0; i < Defence.Count; i++)
            {
                DefenceMember m = Defence[i];
                Record r = m.Record;
                DefenceLine.Length = 0;
                DefenceLine.Append("  #").Append(r.Id).Append(' ').Append(r.Name).Append(": ");
                if (m.Post >= 0)
                {
                    Vector3 at = DefenceSeats[m.Post].At;
                    DefenceLine.Append(DefenceLabel(m.Post)).Append(" seat ").Append(DefenceV(at));
                    if (r.Unit != null && r.Unit.Ai != null)
                        DefenceLine.Append(", ").Append(DefenceF(DefenceFlat(r.Unit.Ai.transform.position, at))).Append(" u flat from ")
                            .Append(DefenceV(r.Unit.Ai.transform.position));
                    DefenceLine.Append("; path: ").Append(DefencePath(r, at));
                    if (r.Unit != null) r.Unit.DefTrack.NextLog = Time.time + MercDefenceCore.LogEvery;
                }
                else DefenceLine.Append("no post - ").Append(DefenceWhyNone(m, open));
                DefenceLog(DefenceLine.ToString());
            }
        }

        // AirDefenceTick (2 Hz): arrival lines at once, progress every 5 s.
        static void DefenceLogTick(float now, bool moved)
        {
            if (moved)
                for (int i = 0; i < Defence.Count; i++)
                {
                    DefenceMember m = Defence[i];
                    if (!m.Moved) continue;
                    DefenceLog("#" + m.Record.Id + " re-planned: " + (m.Post < 0 ? "no post - " + MercUi.OrderText(m.Record)
                        : DefenceLabel(m.Post) + " seat " + DefenceV(DefenceSeats[m.Post].At) + "; path: " + DefencePath(m.Record, DefenceSeats[m.Post].At)));
                }
            for (int i = 0; i < Defence.Count; i++)
            {
                DefenceMember m = Defence[i];
                MercUnit u = m.Record.Unit;
                if (m.Post < 0 || u == null || u.Ai == null) continue;
                MercPostTrack t = u.DefTrack;
                bool seated = MercAA.PostOf(u) == m.Post && MercAA.Held(m.Post) != null && MercAA.Held(m.Post).Ai == u.Ai;
                if (seated)
                {
                    if (!t.SaidSeat)
                    {
                        t.SaidSeat = true;
                        DefenceLog("#" + m.Record.Id + " at " + DefenceLabel(m.Post) + ": seat TAKEN after "
                            + DefenceF(now - t.Since) + " s" + (t.Rescues > 0 ? " (" + t.Rescues + " rescue(s))" : "") + "; gun crewed by the merc");
                    }
                    continue;
                }
                if (t.SaidSeat) { t.SaidSeat = false; DefenceLog("#" + m.Record.Id + " left the seat of " + DefenceLabel(m.Post) + ": " + MercAA.LeaseWhy(u, m.Post)); }
                if (!MercDefenceCore.LogDue(t, now)) continue;
                Vector3 me = u.Ai.transform.position, at = DefenceSeats[m.Post].At;
                bool climbing = TowerRoof.Climbing(u.Ai.transform);
                DefenceLine.Length = 0;
                DefenceLine.Append('#').Append(m.Record.Id).Append(' ').Append(DefenceLabel(m.Post));
                if (t.Arrived && t.Post == m.Post && t.For == u.Order)
                    DefenceLine.Append(": at the post, seat NOT taken - ").Append(MercAA.LeaseWhy(u, m.Post)).Append(';');
                else DefenceLine.Append(": post not reached;");
                DefenceLine.Append(" flat ").Append(DefenceF(DefenceFlat(me, at))).Append(" u, rise ").Append(DefenceF(me.y - at.y))
                    .Append(" u, best ").Append(DefenceF(t.Best == float.MaxValue ? -1f : t.Best)).Append(" u ")
                    .Append(DefenceF(now - t.ProgressAt)).Append(" s ago, pit ").Append(DefenceF(MercAA.PitFlat(m.Post, me)))
                    .Append(" u; state ").Append(climbing ? "TOWER STAIRS (climb)" : MercDefenceCore.StateName(t.State))
                    .Append("; order ").Append(MercUi.OrderText(m.Record));
                if (!MercAA.IsOrder(u.Order) || MercAA.PostOf(u) != m.Post) DefenceLine.Append(" (OVERRIDES the post)");
                DefenceLine.Append("; on tower ").Append(TowerRoof.ManUp(me) ? "yes" : "no");
                if (u.Fight.Brain != null) DefenceLine.Append("; fight mode ").Append(u.Fight.Brain.Mode);
                if (u.AARetreat) DefenceLine.Append("; AA retreat (health)");
                if (MercCrewPhases.Ground(u)) DefenceLine.Append("; GROUND duty");
                if (u.Ride.Carrier != null) DefenceLine.Append("; in a vehicle");
                if (t.Rescues > 0) DefenceLine.Append("; rescues ").Append(t.Rescues);
                NavMeshAgent agent = GepardCrew.Agent(u.Ai);
                if (agent == null) DefenceLine.Append("; no agent");
                else if (!agent.isActiveAndEnabled) DefenceLine.Append("; agent OFF");
                else
                {
                    DefenceLine.Append("; agent on NavMesh ").Append(agent.isOnNavMesh ? "yes" : "NO");
                    if (agent.isOnNavMesh)
                        DefenceLine.Append(", path ").Append(agent.pathPending ? "pending" : agent.hasPath ? agent.pathStatus.ToString() : "none")
                            .Append(", left ").Append(DefenceF(agent.hasPath ? agent.remainingDistance : -1f))
                            .Append(" u, dest ").Append(DefenceV(agent.destination))
                            .Append(", speed ").Append(DefenceF(agent.velocity.magnitude))
                            .Append(agent.isStopped ? ", STOPPED" : "")
                            .Append(agent.updatePosition ? "" : ", position frozen");
                }
                DefenceLog(DefenceLine.ToString());
            }
        }
    }
}
