// Z H0b: bounded, Unity-independent scenario input and measurement contracts.
// C# 3.0, ASCII. Compiled unchanged by scenario_check.py.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace NextDayRevival
{
    internal sealed class ScenarioActorSpec
    {
        internal string Id, Kind, Profile, Faction, Vehicle;
        internal double At, Health, Yaw;
        internal double[] Position, Target;
        internal int[] Weapons;
        internal bool Patrol;
    }

    internal sealed class ScenarioOrderSpec
    {
        internal string Actor, Mode;
        internal double At;
        internal double[][] Points;
        internal int Post = -1;
        internal string Goal;
        internal double Deadline;
        internal string Team;
        internal int Slot, TeamSize = 1;
    }

    internal sealed class ScenarioAirSpec
    {
        internal string Id, Mode, Faction;
        internal double At, Heading, Length, Width;
        internal double[] Target, Drop;
        internal int Bombers, Transports, Load;
    }

    internal sealed class ScenarioCameraSpec
    {
        internal string Id;
        internal double At, Fov;
        internal int Width, Height;
        internal double[] Position, Target;
    }

    internal sealed class ScenarioSpec
    {
        internal string Name;
        internal double Duration, Timeout;
        internal ScenarioActorSpec[] Actors;
        internal ScenarioOrderSpec[] Orders;
        internal ScenarioCameraSpec[] Captures;
        internal ScenarioAirSpec[] Air;
        internal string Frame;
        internal double[] Observer;

        internal static ScenarioSpec Parse(string json)
        {
            Dictionary<string, object> root = Obj(ScenarioJson.Parse(json));
            Keys(root, "schema,name,duration,timeout,actors,orders,captures,air,frame,observer");
            if (Num(root, "schema", 0) != 1) throw new ArgumentException("Scenario schema must be 1.");
            ScenarioSpec s = new ScenarioSpec();
            s.Name = Slug(Str(root, "name", ""));
            s.Duration = Range(Num(root, "duration", 30), 1, 600);
            s.Timeout = Range(Num(root, "timeout", s.Duration + 180), s.Duration + 30, 1800);
            s.Frame = Str(root, "frame", "world"); Choice(s.Frame, "world,tower");
            s.Observer = root.ContainsKey("observer") ? Vec(root, "observer") : null;
            List<object> actors = Arr(root, "actors", 64), orders = Arr(root, "orders", 128), captures = Arr(root, "captures", 64);
            s.Actors = new ScenarioActorSpec[actors.Count];
            s.Orders = new ScenarioOrderSpec[orders.Count];
            s.Captures = new ScenarioCameraSpec[captures.Count];
            Dictionary<string, ScenarioActorSpec> ids = new Dictionary<string, ScenarioActorSpec>();
            int mercs = 0;
            for (int i = 0; i < actors.Count; i++)
            {
                Dictionary<string, object> o = Obj(actors[i]);
                Keys(o, "id,kind,at,position,target,profile,faction,vehicle,weapons,health,yaw,patrol");
                ScenarioActorSpec a = new ScenarioActorSpec();
                a.Id = Slug(Str(o, "id", "")); a.Kind = Str(o, "kind", "npc");
                Choice(a.Kind, "npc,merc,raid,vehicle");
                if (ids.ContainsKey(a.Id)) throw new ArgumentException("Duplicate actor " + a.Id);
                ids.Add(a.Id, a);
                a.At = Range(Num(o, "at", 0), 0, s.Duration - 0.1);
                a.Position = Vec(o, "position");
                a.Target = o.ContainsKey("target") ? Vec(o, "target") : null;
                if (a.Kind == "raid" && a.Target == null) throw new ArgumentException("Raid needs target.");
                a.Profile = Str(o, "profile", "civ-rifle");
                a.Faction = Str(o, "faction", "traitor");
                Choice(a.Faction, "civilian,looter,traitor,mtown");
                a.Vehicle = Str(o, "vehicle", "btr");
                Choice(a.Vehicle, "btr,tank,ural,technical,arty,gepard,katyusha,mi8");
                a.Health = Range(Num(o, "health", 150), 1, 10000);
                a.Yaw = Range(Num(o, "yaw", 0), -360, 360);
                if (o.ContainsKey("patrol")) { if (!(o["patrol"] is bool)) throw new ArgumentException("Patrol needs boolean."); a.Patrol = (bool)o["patrol"]; }
                List<object> weapons = Arr(o, "weapons", 4);
                a.Weapons = new int[weapons.Count == 0 ? 1 : weapons.Count];
                if (weapons.Count == 0) a.Weapons[0] = 1006;
                for (int k = 0; k < weapons.Count; k++) a.Weapons[k] = Integer(weapons[k], 1, 100000);
                if (a.Kind == "merc" && ++mercs > 10) throw new ArgumentException("Maximum ten scenario mercs.");
                s.Actors[i] = a;
            }
            for (int i = 0; i < orders.Count; i++)
            {
                Dictionary<string, object> o = Obj(orders[i]); Keys(o, "actor,mode,at,points,post,goal,deadline,slot,team");
                ScenarioOrderSpec a = new ScenarioOrderSpec();
                a.Actor = Str(o, "actor", "");
                ScenarioActorSpec actor;
                if (!ids.TryGetValue(a.Actor, out actor) || actor.Kind != "merc") throw new ArgumentException("Order needs scenario merc.");
                a.Mode = Str(o, "mode", "stay"); Choice(a.Mode, "follow,stay,perimeter,attack,patrol,gun,radar");
                a.Post = o.ContainsKey("post") ? Integer(o["post"], 0, 13) : -1;
                if (a.Mode == "gun" || a.Mode == "radar")
                {
                    if (a.Post < 0 || a.Post == 11 || (a.Mode == "radar" ? a.Post != 4 : a.Post == 4)) throw new ArgumentException("Invalid station post.");
                }
                else if (a.Post != -1) throw new ArgumentException("Post needs station order.");
                a.Goal = Str(o, "goal", "response"); Choice(a.Goal, "response,roof");
                a.Slot = Integer(Num(o, "slot", 0), 0, 9); a.Team = Str(o, "team", "");
                if (a.Team.Length > 0) { Slug(a.Team); if (a.Mode != "attack") throw new ArgumentException("Team needs attack order."); }
                a.Deadline = Range(Num(o, "deadline", Math.Min(30, s.Duration)), 0.1, s.Duration);
                if (a.Goal == "roof" && (a.Mode != "stay" || s.Frame != "tower")) throw new ArgumentException("Roof goal needs tower-frame stay order.");
                a.At = Range(Num(o, "at", 0), actor.At, s.Duration - 0.1);
                List<object> points = Arr(o, "points", 6);
                a.Points = new double[points.Count][];
                for (int k = 0; k < points.Count; k++) a.Points[k] = Vector(points[k]);
                if (a.Mode != "follow" && a.Mode != "gun" && a.Mode != "radar" && points.Count < (a.Mode == "patrol" ? 2 : 1)) throw new ArgumentException("Order needs points.");
                s.Orders[i] = a;
            }
            for (int i = 0; i < s.Orders.Length; i++)
            {
                ScenarioOrderSpec a = s.Orders[i]; if (a.Team.Length == 0) continue;
                int count = 0;
                for (int j = 0; j < s.Orders.Length; j++)
                {
                    ScenarioOrderSpec b = s.Orders[j]; if (b.Team != a.Team) continue; count++;
                    if (a.At != b.At || a.Points[0][0] != b.Points[0][0] || a.Points[0][1] != b.Points[0][1] || a.Points[0][2] != b.Points[0][2]
                        || (i != j && (a.Slot == b.Slot || a.Actor == b.Actor))) throw new ArgumentException("Inconsistent attack team.");
                }
                if (a.Slot >= count) throw new ArgumentException("Team slots must be contiguous."); a.TeamSize = count;
            }
            Dictionary<string, bool> cameras = new Dictionary<string, bool>();
            for (int i = 0; i < captures.Count; i++)
            {
                Dictionary<string, object> o = Obj(captures[i]); Keys(o, "id,at,position,target,width,height,fov");
                ScenarioCameraSpec c = new ScenarioCameraSpec();
                c.Id = Slug(Str(o, "id", ""));
                if (cameras.ContainsKey(c.Id)) throw new ArgumentException("Duplicate capture id.");
                cameras.Add(c.Id, true);
                c.At = Range(Num(o, "at", 0), 0, s.Duration);
                c.Position = Vec(o, "position"); c.Target = Vec(o, "target");
                double distance = 0; for (int k = 0; k < 3; k++) distance += Math.Abs(c.Position[k] - c.Target[k]);
                if (distance < 0.01) throw new ArgumentException("Camera needs a distinct target.");
                c.Width = Integer(Num(o, "width", 960), 64, 1920);
                c.Height = Integer(Num(o, "height", 540), 64, 1080);
                c.Fov = Range(Num(o, "fov", 60), 10, 120); s.Captures[i] = c;
            }
            List<object> air = Arr(root, "air", 8); s.Air = new ScenarioAirSpec[air.Count];
            Dictionary<string, bool> airIds = new Dictionary<string, bool>();
            for (int i = 0; i < air.Count; i++)
            {
                Dictionary<string, object> o = Obj(air[i]);
                Keys(o, "id,mode,at,target,drop,heading,length,width,bombers,transports,load,faction");
                ScenarioAirSpec a = new ScenarioAirSpec(); a.Id = Slug(Str(o, "id", ""));
                if (airIds.ContainsKey(a.Id)) throw new ArgumentException("Duplicate air id."); airIds.Add(a.Id, true);
                a.Mode = Str(o, "mode", "raid"); Choice(a.Mode, "raid,an2-bomb");
                if (a.Mode == "an2-bomb" && (o.ContainsKey("drop") || o.ContainsKey("heading") || o.ContainsKey("length")
                    || o.ContainsKey("width") || o.ContainsKey("bombers") || o.ContainsKey("transports") || o.ContainsKey("load")))
                    throw new ArgumentException("An-2 support uses fixed 150 m, six FAB-50; only target/time/faction are selectable.");
                a.At = Range(Num(o, "at", 0), 0, s.Duration - 0.1);
                a.Target = Vec(o, "target"); a.Drop = o.ContainsKey("drop") ? Vec(o, "drop") : a.Target;
                a.Heading = Range(Num(o, "heading", 0), 0, 360);
                a.Length = Range(Num(o, "length", 150), 28, 2520); a.Width = Range(Num(o, "width", 100), 0, 840);
                a.Bombers = Integer(Num(o, "bombers", 1), 0, 2); a.Transports = Integer(Num(o, "transports", 1), 0, 2);
                a.Load = Integer(Num(o, "load", 6), 1, 8);
                a.Faction = Str(o, "faction", "traitor"); Choice(a.Faction, "civilian,looter,traitor,mtown");
                if (a.Mode == "raid" && a.Bombers + a.Transports == 0) throw new ArgumentException("Empty air raid.");
                s.Air[i] = a;
            }
            int airMen = 0;
            for (int i = 0; i < s.Air.Length; i++) if (s.Air[i].Mode == "raid")
                for (int k = 0; k < s.Air[i].Transports * s.Air[i].Load; k++)
                {
                    airMen++;
                    if (ids.ContainsKey("air-" + s.Air[i].Id + "-para-" + (k + 1))) throw new ArgumentException("Actor id collides with native air slot.");
                }
            if (s.Actors.Length + airMen > 64) throw new ArgumentException("Maximum 64 authored and air actors combined.");
            return s;
        }

        internal static string Slug(string value)
        {
            if (value.Length < 1 || value.Length > 64) throw new ArgumentException("Invalid scenario identifier.");
            foreach (char c in value) if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z')
                && !(c >= '0' && c <= '9') && c != '-' && c != '_') throw new ArgumentException("Identifiers use ASCII letters, digits, '-' or '_'.");
            return value;
        }
        internal static double Range(double v, double min, double max)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v < min || v > max) throw new ArgumentException("Scenario number out of range.");
            return v;
        }
        static void Choice(string v, string choices) { if (Array.IndexOf(choices.Split(','), v) < 0) throw new ArgumentException("Unsupported scenario value " + v); }
        static Dictionary<string, object> Obj(object v) { Dictionary<string, object> o = v as Dictionary<string, object>; if (o == null) throw new ArgumentException("Expected JSON object."); return o; }
        static void Keys(Dictionary<string, object> o, string keys) { string[] allowed = keys.Split(','); foreach (string k in o.Keys) if (Array.IndexOf(allowed, k) < 0) throw new ArgumentException("Unknown scenario field " + k); }
        static string Str(Dictionary<string, object> o, string k, string fallback) { object v; if (!o.TryGetValue(k, out v)) return fallback; if (!(v is string)) throw new ArgumentException("Expected string " + k); return (string)v; }
        static double Num(Dictionary<string, object> o, string k, double fallback) { object v; if (!o.TryGetValue(k, out v)) return fallback; if (!(v is double)) throw new ArgumentException("Expected number " + k); return (double)v; }
        static int Integer(object v, int min, int max) { if (!(v is double)) throw new ArgumentException("Expected integer."); double n = Range((double)v, min, max); if (n != Math.Floor(n)) throw new ArgumentException("Expected integer."); return (int)n; }
        static List<object> Arr(Dictionary<string, object> o, string k, int max) { object v; if (!o.TryGetValue(k, out v)) return new List<object>(); List<object> a = v as List<object>; if (a == null || a.Count > max) throw new ArgumentException("Invalid array " + k); return a; }
        static double[] Vec(Dictionary<string, object> o, string k) { object v; if (!o.TryGetValue(k, out v)) throw new ArgumentException("Missing vector " + k); return Vector(v); }
        static double[] Vector(object v) { List<object> a = v as List<object>; if (a == null || a.Count != 3) throw new ArgumentException("Expected three world units."); double[] p = new double[3]; for (int i = 0; i < 3; i++) { if (!(a[i] is double)) throw new ArgumentException("Invalid coordinate."); p[i] = Range((double)a[i], -100000, 100000); } return p; }
    }

    // Completion is distinct from H0b's first movement/fire response latency.
    internal sealed class ScenarioArrivalClock
    {
        internal double At = -1;
        internal void Observe(double now, bool current, bool alive, bool arrived)
        { if (At < 0 && current && alive && arrived) At = now; }
        internal bool Within(double issued, double deadline) { return At >= issued && At - issued <= deadline; }
    }

    // No framework/Unity JSON dependency; strict grammar, duplicate-key and depth guards.
    internal sealed class ScenarioJson
    {
        readonly string _s; int _p;
        ScenarioJson(string s) { _s = s; }
        internal static object Parse(string s) { if (s == null || s.Length > 131072) throw new ArgumentException("Scenario exceeds 128 KiB."); ScenarioJson p = new ScenarioJson(s); object v = p.Value(0); p.White(); if (p._p != s.Length) throw new ArgumentException("Trailing JSON."); return v; }
        void White() { while (_p < _s.Length && (_s[_p] == ' ' || _s[_p] == '\n' || _s[_p] == '\r' || _s[_p] == '\t')) _p++; }
        bool Take(char c) { White(); if (_p < _s.Length && _s[_p] == c) { _p++; return true; } return false; }
        void Need(char c) { if (!Take(c)) throw new ArgumentException("Malformed JSON."); }
        object Value(int depth)
        {
            if (depth > 12) throw new ArgumentException("JSON nesting limit.");
            White(); if (_p >= _s.Length) throw new ArgumentException("Incomplete JSON.");
            if (_s[_p] == '"') return String();
            if (Take('{')) { Dictionary<string, object> d = new Dictionary<string, object>(); if (Take('}')) return d; do { string k = String(); Need(':'); if (d.ContainsKey(k)) throw new ArgumentException("Duplicate JSON key."); d.Add(k, Value(depth + 1)); } while (Take(',')); Need('}'); return d; }
            if (Take('[')) { List<object> a = new List<object>(); if (Take(']')) return a; do { if (a.Count >= 1024) throw new ArgumentException("JSON array limit."); a.Add(Value(depth + 1)); } while (Take(',')); Need(']'); return a; }
            if (_s[_p] == 't' && Literal("true")) return true;
            if (_s[_p] == 'f' && Literal("false")) return false;
            if (_s[_p] == 'n' && Literal("null")) return null;
            int start = _p;
            if (_s[_p] == '-') _p++;
            if (_p >= _s.Length || _s[_p] < '0' || _s[_p] > '9') throw new ArgumentException("Invalid JSON value.");
            if (_s[_p] == '0') _p++; else Digits();
            if (_p < _s.Length && _s[_p] == '.') { _p++; Digits(); }
            if (_p < _s.Length && (_s[_p] == 'e' || _s[_p] == 'E')) { _p++; if (_p < _s.Length && (_s[_p] == '+' || _s[_p] == '-')) _p++; Digits(); }
            double number;
            if (!double.TryParse(_s.Substring(start, _p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) throw new ArgumentException("JSON number overflow.");
            return ScenarioSpec.Range(number, -double.MaxValue, double.MaxValue);
        }
        void Digits() { int start = _p; while (_p < _s.Length && _s[_p] >= '0' && _s[_p] <= '9') _p++; if (_p == start) throw new ArgumentException("Invalid JSON number."); }
        bool Literal(string s) { if (_s.Length - _p < s.Length || string.CompareOrdinal(_s, _p, s, 0, s.Length) != 0) throw new ArgumentException("Invalid JSON literal."); _p += s.Length; return true; }
        string String()
        {
            Need('"'); StringBuilder b = new StringBuilder();
            while (_p < _s.Length)
            {
                char c = _s[_p++]; if (c == '"') return b.ToString(); if (c < 32) throw new ArgumentException("JSON control character.");
                if (c == '\\') { if (_p >= _s.Length) break; c = _s[_p++]; switch (c) { case '"': case '\\': case '/': break; case 'b': c = '\b'; break; case 'f': c = '\f'; break; case 'n': c = '\n'; break; case 'r': c = '\r'; break; case 't': c = '\t'; break; case 'u': if (_p + 4 > _s.Length) throw new ArgumentException("Invalid JSON escape."); c = (char)int.Parse(_s.Substring(_p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture); _p += 4; break; default: throw new ArgumentException("Invalid JSON escape."); } }
                b.Append(c);
            }
            throw new ArgumentException("Unterminated JSON string.");
        }
        internal static string Quote(string s)
        {
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in s) { if (c == '"' || c == '\\') b.Append('\\').Append(c); else if (c < 32 || c > 126) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); }
            return b.Append('"').ToString();
        }
        internal static string Number(double n) { return n.ToString("0.######", CultureInfo.InvariantCulture); }
    }

    internal sealed class ScenarioFrames
    {
        readonly double[] _samples = new double[1000000];
        internal int Count; internal double Total, Max;
        internal void Add(double ms) { ScenarioSpec.Range(ms, 0, double.MaxValue); if (Count == _samples.Length) throw new InvalidOperationException("Frame sample capacity exceeded."); _samples[Count++] = ms; Total += ms; if (ms > Max) Max = ms; }
        internal double Average { get { return Count == 0 ? 0 : Total / Count; } }
        internal double P99() { if (Count == 0) return 0; Array.Sort(_samples, 0, Count); return _samples[(int)Math.Ceiling(Count * 0.99) - 1]; }
    }

    internal sealed class ScenarioActorMetrics
    {
        internal bool Dead, Downed, Exposed, Stuck;
        internal int Deaths, DownedEvents, StuckEvents;
        internal double SecondsExposed, LastAt;
        internal void Sample(double now, bool alive, bool downed, bool exposed, bool stuck)
        {
            // Left-interval integration at 5 Hz; never integrates past a sampled death.
            if (!Dead && Exposed) SecondsExposed += Math.Max(0, now - LastAt);
            if (!alive && !Dead) Deaths++;
            bool down = alive && downed;
            if (down && !Downed) DownedEvents++;
            if (stuck && !Stuck && alive) StuckEvents++;
            Dead = !alive; Downed = down; Exposed = alive && exposed; Stuck = stuck; LastAt = now;
        }
    }

    internal sealed class ScenarioOrderClock
    {
        internal bool Issued, Executed;
        internal double IssuedAt, Latency;
        internal void Issue(double now) { Issued = true; IssuedAt = now; }
        internal void Observe(double now, bool current, bool alive, bool settled, double movedUnits, bool fired)
        {
            if (!Issued || Executed || !current || !alive || now < IssuedAt) return;
            if (settled || movedUnits >= 2.8 || fired) { Executed = true; Latency = now - IssuedAt; }
        }
    }

    internal sealed class ScenarioGate : IDisposable
    {
        Mutex _mutex; bool _owned;
        internal ScenarioGate(string name)
        {
            _mutex = new Mutex(false, name);
            try { try { _owned = _mutex.WaitOne(0, false); } catch (AbandonedMutexException) { _owned = true; } if (!_owned) throw new InvalidOperationException("Another offline run owns the global lock."); }
            catch { _mutex.Close(); _mutex = null; throw; }
        }
        public void Dispose() { if (_mutex == null) return; if (_owned) _mutex.ReleaseMutex(); _mutex.Close(); _mutex = null; _owned = false; }
    }

    // Independent wall-clock stop even if Unity's main thread hangs in a render
    // or native spawn. No Unity API, dialog, network or external file access.
    internal sealed class ScenarioWatchdog
    {
        readonly Timer _timer;
        readonly string _output;
        internal ScenarioWatchdog(double seconds, string output)
        {
            ScenarioSpec.Range(seconds, 0.1, 1805); _output = output;
            _timer = new Timer(Expired, null, (int)Math.Ceiling(seconds * 1000), System.Threading.Timeout.Infinite);
        }
        void Expired(object state)
        {
            try { File.WriteAllText(Path.Combine(_output, "watchdog.json"), "{\"success\":false,\"error\":\"Scenario process exceeded independent wall timeout\"}"); }
            catch { }
            finally { Environment.Exit(124); }
        }
    }
}
