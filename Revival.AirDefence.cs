using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    // Pure rules also compiled by research/aa_ownership_check.py.
    internal static class AirDefencePolicy
    {
        internal static bool Follows(int owner, int side, bool player, int playerSide)
        {
            return owner >= 0 && owner == side && (!player || playerSide == side);
        }

        internal static bool Hostile(string owner, string pilot)
        {
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(pilot) || owner == pilot) return false;
            return pilot == "Neutral" || pilot == "Marauder" || pilot == "Peace"
                || pilot == "Hermit" || pilot == "Wildman" || pilot == "Military"
                || pilot == "Traitor" || pilot == "MilitaryNeutral";
        }

        internal static bool Radar(bool town, int owner, int operatorSide, int tier)
        {
            return !town && tier == 2 && owner >= 0 && owner == operatorSide;
        }

        internal static bool TownArea(float x, float z, float cx, float cz, float radius)
        {
            float dx = x - cx, dz = z - cz;
            return dx * dx + dz * dz <= radius * radius;
        }
    }

    // No Update: pilot identities arrive on the existing boarding heartbeat.
    internal sealed class AirPilot : MonoBehaviour
    {
        internal int Actor = -1;
        internal float Until;
        internal string NpcFaction;

        internal static void Board(GameObject go, int actor, float[] data)
        {
            if (go == null || data == null || data.Length < 3) return;
            AirPilot p = go.GetComponent<AirPilot>();
            if (data[1] <= 0.5f)
            {
                if (p != null && p.Actor == actor) { p.Actor = -1; p.Until = 0f; }
                return;
            }
            if (data[2] <= 0.5f) return; // Passengers never change pilot IFF.
            if (p == null) p = go.AddComponent<AirPilot>();
            p.Actor = actor;
            p.Until = Time.time + 5f;
        }

        internal static void Npc(GameObject go, string side)
        {
            if (go == null || string.IsNullOrEmpty(side)) return;
            AirPilot p = go.GetComponent<AirPilot>();
            if (p == null) p = go.AddComponent<AirPilot>();
            p.NpcFaction = Fraktion.Eigene(side);
        }

        internal static string Faction(GepardGun.Contact c)
        {
            if (c == null || c.Go == null) return null;
            if (c.Kind == 2 || c.Kind == 3)
                return PlayerFaction(c.Actor);
            // NPC raids/test intruders are explicitly hostile raiders.
            if (NpcAircraft.Hostile(c.Go)) return "Wildman";
            AirPilot p = c.Go.GetComponent<AirPilot>();
            if (p == null) return null;
            if (p.Actor >= 0 && Time.time <= p.Until) return PlayerFaction(p.Actor);
            return p.NpcFaction;
        }

        static string PlayerFaction(int actor)
        {
            GameObject player = Crocodile.PlayerByActor(actor);
            return player == null || !Crocodile.PlayerUp(player) ? null : Fraktion.Spielerseite(player);
        }
    }

    // Room properties survive disconnects/master migration; only the master writes.
    // Reflection and serialization run only on initialization, migration or capture.
    internal static class AirfieldOwnership
    {
        const string Key = "ndr.af.holder";
        static object _room;
        static object _masterPlayer;
        static MethodInfo _masterGetter;
        static int _masterActor = -1;
        static bool _initialized;
        internal static bool Captured;
        internal static int Holder = -1;
        internal static string FactionName;
        internal static string CrewSide;

        internal static void Ensure(bool migration)
        {
            if (_initialized && !migration) return;
            _initialized = true;
            if (Holder < 0) Apply(TowerRadar.SideId(Fraktion.Eigene(Airfield.Faction())), false);
            try
            {
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                _room = photon == null ? null : AccessTools.PropertyGetter(photon, "room").Invoke(null, null);
                if (_room == null) return;
                IDictionary props = AccessTools.PropertyGetter(_room.GetType(), "CustomProperties").Invoke(_room, null) as IDictionary;
                int[] state = props == null ? null : props[Key] as int[];
                if (state != null && state.Length == 2) Apply(state[0], state[1] != 0);
            }
            catch (Exception ex) { TowerRadar.Log("holder restore: " + ex.Message); }
        }

        internal static void Apply(int side, bool captured)
        {
            if (side < 0) return;
            if (side == Holder && captured == Captured) return;
            Holder = side;
            Captured = captured;
            Type fraction = RevivalPlugin.TypeByName("Fraction");
            FactionName = side == TowerRadar.UnreadSide || fraction == null ? null : Enum.GetName(fraction, side);
            CrewSide = FactionName == "Peace" ? "civilian" : FactionName == "Marauder" ? "looter"
                : FactionName == "Traitor" ? "traitor" : FactionName == "Neutral" ? "neutral" : null;
        }

        /// <summary>W Tower 3 merged: the console claim no longer captures. The
        /// airfield is taken on the ground (AirfieldHold); the guns and radar
        /// follow its holder through <see cref="Follow"/>.</summary>
        internal static bool Take(int actor) { return false; }

        /// <summary>Master, from AirfieldHold's 1 Hz step: the holder it decided
        /// (captured by players, or the garrison back) becomes the guns' and
        /// radar's owner, saved in the room for a master handoff.</summary>
        internal static void Follow(int holder, bool byPlayers)
        {
            if (holder < 0 || !Crocodile.IsMaster()) return;
            if (holder == Holder && byPlayers == Captured) return;
            Apply(holder, byPlayers);
            try
            {
                if (_room != null)
                {
                    MethodInfo set = AccessTools.Method(_room.GetType(), "SetCustomProperties", null, null);
                    IDictionary props = (IDictionary)Activator.CreateInstance(set.GetParameters()[0].ParameterType);
                    props[Key] = new int[] { Holder, Captured ? 1 : 0 };
                    set.Invoke(_room, new object[] { props, null, false });
                }
            }
            catch (Exception ex) { TowerRadar.Log("holder save: " + ex.Message); }
            Flak.ClearAirfieldOrders();
            TowerRadar.Log("the guns and radar now answer to " + TowerRadar.SideLabel(holder) + ".");
        }

        internal static string ZoneSide() { return CrewSide ?? "players"; }
        internal static bool Shown() { return Flak.On; }
        internal static bool Armed() { return Flak.AirfieldManned(); }

        internal static bool MasterSender(int actor)
        {
            try
            {
                if (_masterGetter == null)
                {
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null) return false;
                    _masterGetter = AccessTools.PropertyGetter(photon, "masterClient");
                }
                object master = _masterGetter == null ? null : _masterGetter.Invoke(null, null);
                if (master == null) return false;
                if (!object.ReferenceEquals(master, _masterPlayer))
                {
                    _masterPlayer = master;
                    _masterActor = Convert.ToInt32(AccessTools.PropertyGetter(master.GetType(), "ID").Invoke(master, null));
                }
                return actor == _masterActor;
            }
            catch { return false; }
        }

        internal static void Reset()
        {
            _initialized = Captured = false;
            _room = null;
            Holder = -1;
            FactionName = CrewSide = null;
        }
    }
}
