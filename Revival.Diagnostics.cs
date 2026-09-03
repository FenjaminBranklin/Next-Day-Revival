using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{

    public static class Diag
    {
        static int _reload, _backpack;

        public static void ReloadPrefix(object __instance)
        {
            if (_reload >= 6) return;
            _reload++;
            ManualLogSource L = RevivalPlugin.L;
            try
            {
                object wd = F(__instance, "_weaponFirearmData");
                object inv = F(__instance, "_plrInventoryManager");
                L.LogInfo("=== ReloadWeapon (#" + _reload + ")");
                if (wd != null)
                    L.LogInfo("    ItemID=" + F(wd, "ItemID") + " MaxBullets=" + F(wd, "MaxBullets")
                              + " Clips=" + WeaponData.Arr(F(F(wd, "WeaponAmmo"), "Clips"), 8));
                if (inv != null)
                {
                    object wpn = F(inv, "_weaponsData");
                    object bp = F(inv, "_backpackData");
                    L.LogInfo("    Slot=" + F(wpn, "CurrentSlotID")
                              + " WaffenIDs=" + WeaponData.Arr(F(wpn, "ItemID"), 8)
                              + " Bullets=" + WeaponData.Arr(F(wpn, "Bullets"), 8));
                    L.LogInfo("    Rucksack=" + WeaponData.Arr(F(bp, "ItemID"), 20));
                }
            }
            catch (Exception ex) { L.LogError("Reload-Diagnose: " + ex.Message); }
        }

        // __args faengt ALLE Parameter ab, unabhaengig von Position und Namen.
        public static void BackpackPrefix(object[] __args)
        {
            ManualLogSource L = RevivalPlugin.L;

            if (_backpack >= 12) return;
            try
            {
                int itemId = -1;
                if (__args.Length > 0 && __args[0] is int) itemId = (int)__args[0];
                ItemDef mine = null;
                List<ItemDef> items = RevivalPlugin.Items;
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Id == itemId) { mine = items[i]; break; }
                if (mine == null && !RevivalPlugin.CfgVerbose.Value) return;

                _backpack++;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < __args.Length; i++)
                    sb.Append(i > 0 ? ", " : "").Append(__args[i] == null ? "null" : __args[i].ToString());
                L.LogInfo("=== AddBackpackItemFromValues(" + sb + ")");
                if (mine == null) return;

                Type dm = RevivalPlugin.TypeByName("ItemSpawnCategoriesDB");
                MethodInfo cur = dm == null ? null : AccessTools.PropertyGetter(dm, "current");
                object db = cur == null ? null : cur.Invoke(null, null);
                if (db == null) { L.LogWarning("  DB NULL"); return; }

                MethodInfo get = AccessTools.Method(db.GetType(), "GetItemSpawnedScriptByID", null, null);
                object res = get == null ? null : get.Invoke(db, new object[] { itemId });
                if (res == null) { L.LogWarning("  GetItemSpawnedScriptByID liefert NULL"); return; }

                Component comp = res as Component;
                L.LogInfo("  liefert " + res.GetType().Name + " auf '"
                          + (comp == null ? "?" : comp.gameObject.name) + "', ist es meins? "
                          + (ReferenceEquals(res, mine.Factory.MySpawned) ? "JA" : "NEIN"));
                if (comp != null)
                {
                    MeshFilter mf = comp.GetComponentInChildren<MeshFilter>(true);
                    L.LogInfo("  erstes Mesh = " + (mf == null || mf.sharedMesh == null
                                                    ? "keins" : mf.sharedMesh.name));
                }
            }
            catch (Exception ex) { L.LogError("Rucksack-Diagnose: " + ex.Message); }
        }

        static object F(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            while (t != null && t != typeof(object))
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) { try { return f.GetValue(o); } catch { return null; } }
                t = t.BaseType;
            }
            return null;
        }
    }
    // ------------------------------------------------------------- Netzwacht

    /// <summary>
    /// The one measurement E-043 and E-044 were missing.
    ///
    /// Both read `DisconnectByClientTimeout` as "the main thread was busy",
    /// and both ruled out the other side by checking the LOCAL master server.
    /// That check proves nothing about this message. The local master only
    /// serves login, profile, static data and the server list. The room the
    /// timeout belongs to is a PHOTON CLOUD room - own app id, region 0, see
    /// MASTERSERVER.md section 2 - so the peer that timed out is a UDP link
    /// over the internet, not a socket to 127.0.0.1. A healthy master log is
    /// consistent with every one of the causes below.
    ///
    /// Three of them produce the same line and need different fixes:
    ///
    ///   the main thread stalled   Update stops for longer than the peer
    ///                             tolerates. Shows up here as a frame gap.
    ///   the link degraded         packets stop arriving. Shows up as a
    ///                             rising round trip time and resent reliable
    ///                             commands while the frame gaps stay small.
    ///   the room is flooded       more traffic than the plan carries. Shows
    ///                             up as a growing outgoing queue.
    ///
    /// The symptoms reported on 2026-08-30 - after F9, in the loading screen,
    /// and at no particular moment - cannot all be the tank construction. The
    /// 2026-09-01 two-client failure finally measured the degraded-link case:
    /// 5052 reliable resends with a small outgoing queue and no long frame
    /// before Photon's 15 second default disconnected both players. The guard
    /// below raises every current or replacement peer to the configured 60
    /// seconds. The optional reports remain read-only apart from enabling
    /// Photon's own traffic counters.
    /// </summary>
    public static class NetWatch
    {
        static bool _first = true;
        static float _lastFrame;
        static float _nextReport;
        static float _worstSinceReport;
        static float _worstEver;
        static string _lastState = "";

        static object _peer;
        static float _nextPeerLook;
        static Type _network;
        static PropertyInfo _stateDetailed;
        static bool _looked;

        static double _lastIn, _lastOut, _lastMs;
        static bool _statsOn;
        static float _nextTimeoutCheck;

        // Hard code floor for SentCountAllowance, applied on top of the cfg
        // value. 2026-09-03 live-join acceptance of the 6.5.0 guard FAILED: the
        // guard applied (log "resend limit 7 -> 30"), yet during a real map load
        // `resent` still climbed 983 -> 5357 -> 13468 across a ~35 s run of
        // repeated multi-second stalls (worstframe 4.73 s) and Photon
        // self-disconnected (peer recreated -> back to lobby). 30 is too low to
        // ride out a full map load. The floor exists so an INSTALLED cfg that
        // still carries the old 30 (BepInEx never rewrites an existing key) gets
        // the higher tolerance too - without it the fix would reach only fresh
        // installs, not the players who already have the bug. DisconnectTimeout
        // (60 s of true silence) stays the real death detector; a stalled but
        // alive link, which is what a load is, must not self-disconnect on the
        // resend COUNT before it can catch up.
        const int ResendFloor = 300;

        public static void Tick()
        {
            GuardTimeout();
            if (RevivalPlugin.CfgNetWatch == null || !RevivalPlugin.CfgNetWatch.Value) return;

            float now = Time.realtimeSinceStartup;
            if (_first)
            {
                _first = false;
                _lastFrame = now;
                _nextReport = now;
                RevivalPlugin.L.LogInfo("Netzwacht " + Uhr()
                    + " active - frame gaps above "
                    + RevivalPlugin.CfgNetWatchHitch.Value.ToString("0.00")
                    + " s and a Photon report every "
                    + RevivalPlugin.CfgNetWatchEvery.Value.ToString("0") + " s.");
                return;
            }

            float gap = now - _lastFrame;
            _lastFrame = now;
            if (gap > _worstSinceReport) _worstSinceReport = gap;

            // The gap is measured on the frame AFTER the pause, so a
            // synchronous scene load or a first-time asset load lands here
            // whole. Time.realtimeSinceStartup keeps running while Unity does
            // not, which is exactly why it and not Time.deltaTime is used.
            float grenze = Mathf.Max(0.05f, RevivalPlugin.CfgNetWatchHitch.Value);
            if (gap > grenze)
            {
                bool schlimmster = gap > _worstEver;
                if (schlimmster) _worstEver = gap;
                RevivalPlugin.L.LogWarning("Netzwacht " + Uhr() + " frame gap "
                    + gap.ToString("0.00") + " s"
                    + (schlimmster ? " (worst so far)" : "")
                    + " - " + PeerText() + ".");
                // A gap this long has already moved the report clock past due.
                // Report right after it as well, so the counters either show
                // the damage or show that the link was never the problem.
                _nextReport = now;
            }

            if (now < _nextReport) return;
            _nextReport = now + Mathf.Max(1f, RevivalPlugin.CfgNetWatchEvery.Value);

            string state = StateText();
            bool gewechselt = state != _lastState;
            _lastState = state;
            RevivalPlugin.L.LogInfo("Netzwacht " + Uhr() + " state=" + state
                + (gewechselt ? " (changed)" : "")
                + " " + PeerText() + " " + VerkehrText()
                + " worstframe=" + _worstSinceReport.ToString("0.00") + " s.");
            _worstSinceReport = 0f;
        }

        /// <summary>
        /// Photon creates or resets its networking peer on every reconnect.
        /// The old tank guard changed only the peer that happened to exist when
        /// F9 was pressed, so ordinary play and every later peer kept the game's
        /// 15 second default. Check once a second and raise any current peer
        /// whose value is lower than the configured tolerance. A higher value
        /// is preserved.
        /// </summary>
        static void GuardTimeout()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextTimeoutCheck) return;
            _nextTimeoutCheck = now + 1f;

            object peer = Peer();
            if (peer == null) return;

            // The silence timer: how long without any Photon response before the
            // link is declared dead. Raised so a short shared UDP interruption
            // does not drop both clients.
            if (RevivalPlugin.CfgPhotonTimeout != null)
                RaisePeerInt(peer, "DisconnectTimeout",
                    Mathf.Clamp(RevivalPlugin.CfgPhotonTimeout.Value, 15000, 120000),
                    "timeout", " ms");

            // The resend count: a multi-second loading stall starves the Photon
            // service loop, so incoming ACKs go unread and every reliable command
            // is resent. The 2026-09-03 kill (resent past 5000 with a small
            // outgoing queue, only ~19 s after Joined - well inside the raised
            // 60 s DisconnectTimeout) was the resend COUNT tripping, not the
            // silence timer. So the timeout guard alone never covered this case.
            // SentCountAllowance lets the resends ride out the stall;
            // QuickResendAttempts recovers faster once the frame returns.
            // The value is floored to ResendFloor (see the field): the first
            // client-side fix at 30 was measured too low against a real map load,
            // and the floor makes the higher tolerance reach installs whose cfg
            // still holds the old 30. 0 in the cfg still disables the guard.
            if (RevivalPlugin.CfgPhotonResendLimit != null
                && RevivalPlugin.CfgPhotonResendLimit.Value > 0)
                RaisePeerInt(peer, "SentCountAllowance",
                    Mathf.Max(RevivalPlugin.CfgPhotonResendLimit.Value, ResendFloor),
                    "resend limit", "");
            if (RevivalPlugin.CfgPhotonQuickResend != null
                && RevivalPlugin.CfgPhotonQuickResend.Value >= 0)
                RaisePeerInt(peer, "QuickResendAttempts",
                    RevivalPlugin.CfgPhotonQuickResend.Value, "quick resend", "");
        }

        /// <summary>Raise one integer knob on the current Photon peer to at least
        /// `wanted`, once. Photon replaces the peer on every reconnect, so a lower
        /// value on a fresh peer is raised again while a higher value is left
        /// alone. Property or field, climbing the base types - the same lookup
        /// the DisconnectTimeout guard first needed.</summary>
        static void RaisePeerInt(object peer, string name, int wanted,
                                 string label, string unit)
        {
            try
            {
                Type type = peer.GetType();
                PropertyInfo property = FindProperty(type, name);
                FieldInfo field = property == null ? FindField(type, name) : null;
                object old = property != null ? property.GetValue(peer, null)
                    : field != null ? field.GetValue(peer) : null;
                if (old == null) return;
                int previous = Convert.ToInt32(old);
                if (previous >= wanted) return;

                if (property != null && property.CanWrite)
                    property.SetValue(peer, wanted, null);
                else if (field != null)
                    field.SetValue(peer, wanted);
                else
                    return;

                RevivalPlugin.L.LogInfo("Photon guard: " + label + " " + previous
                    + unit + " -> " + wanted + unit + " on the current room peer.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Photon guard: " + label
                    + " could not be set - " + ex.Message);
            }
        }

        static PropertyInfo FindProperty(Type type, string name)
        {
            while (type != null)
            {
                PropertyInfo property = type.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null) return property;
                type = type.BaseType;
            }
            return null;
        }

        static FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        static string Uhr()
        {
            return DateTime.Now.ToString("HH:mm:ss.fff");
        }

        /// <summary>The peer's own counters. `RoundTripTime` and
        /// `ResentReliableCommands` separate a dead link from a busy client;
        /// `QueuedOutgoingCommands` separates both from us sending more than
        /// the room carries. `DisconnectTimeout` is here because the F9 guard
        /// raises it and nothing has ever confirmed that it held.</summary>
        static string PeerText()
        {
            object peer = Peer();
            if (peer == null) return "no peer";
            return "rtt=" + Zahl(peer, "RoundTripTime")
                + "/" + Zahl(peer, "RoundTripTimeVariance") + " ms"
                + " out=" + Zahl(peer, "QueuedOutgoingCommands")
                + " in=" + Zahl(peer, "QueuedIncomingCommands")
                + " resent=" + Zahl(peer, "ResentReliableCommands")
                + " losscrc=" + Zahl(peer, "PacketLossByCrc")
                + " timeout=" + Zahl(peer, "DisconnectTimeout");
        }

        static string StateText()
        {
            Look();
            if (_stateDetailed == null) return "?";
            try
            {
                object v = _stateDetailed.GetValue(null, null);
                return v == null ? "?" : v.ToString();
            }
            catch { return "?"; }
        }

        /// <summary>The peer is replaced on every reconnect, so it is looked
        /// up again every second instead of cached for the session.</summary>
        static object Peer()
        {
            float now = Time.realtimeSinceStartup;
            if (_peer != null && now < _nextPeerLook) return _peer;
            _nextPeerLook = now + 1f;
            _peer = null;
            try
            {
                Look();
                if (_network == null) return null;
                FieldInfo f = AccessTools.Field(_network, "networkingPeer");
                if (f != null) _peer = f.GetValue(null);
                if (_peer == null)
                {
                    PropertyInfo p = AccessTools.Property(_network, "networkingPeer");
                    if (p != null) _peer = p.GetValue(null, null);
                }
            }
            catch { _peer = null; }
            return _peer;
        }

        static void Look()
        {
            if (_looked) return;
            _looked = true;
            _network = RevivalPlugin.TypeByName("PhotonNetwork");
            if (_network == null)
            {
                RevivalPlugin.L.LogWarning("Netzwacht: PhotonNetwork not found - "
                    + "only the frame gaps are measured.");
                return;
            }
            _stateDetailed = AccessTools.Property(_network, "connectionStateDetailed");
        }

        /// <summary>
        /// Bytes per second in each direction, from the peer's own traffic
        /// counters. This is the only reading that separates "we send more
        /// than the room carries" from the other two causes: a queue length is
        /// drained every frame and is therefore almost always zero, even while
        /// the client is well over the plan's message rate.
        ///
        /// Switching the counters on is the one write this class makes. It is
        /// a diagnostic flag on the peer, it changes no traffic, and Photon
        /// ships it for exactly this purpose.
        /// </summary>
        static string VerkehrText()
        {
            object peer = Peer();
            if (peer == null) return "";
            try
            {
                if (!_statsOn)
                {
                    PropertyInfo on = AccessTools.Property(peer.GetType(),
                                                           "TrafficStatsEnabled");
                    if (on == null || !on.CanWrite) return "";
                    on.SetValue(peer, true, null);
                    _statsOn = true;
                    _lastMs = 0d;
                    return "traffic=on";
                }

                double ms = Wert(peer, "TrafficStatsElapsedMs");
                double ein = Bytes(peer, "TrafficStatsIncoming");
                double aus = Bytes(peer, "TrafficStatsOutgoing");
                double spanne = ms - _lastMs;
                string text = "traffic=?";
                if (_lastMs > 0d && spanne > 0d)
                    text = "in=" + ((ein - _lastIn) * 1000d / spanne).ToString("0")
                         + " out=" + ((aus - _lastOut) * 1000d / spanne).ToString("0")
                         + " B/s";
                _lastMs = ms;
                _lastIn = ein;
                _lastOut = aus;
                return text;
            }
            catch { return ""; }
        }

        static double Bytes(object peer, string name)
        {
            PropertyInfo p = AccessTools.Property(peer.GetType(), name);
            object stats = p == null ? null : p.GetValue(peer, null);
            if (stats == null)
            {
                FieldInfo f = AccessTools.Field(peer.GetType(), name);
                if (f != null) stats = f.GetValue(peer);
            }
            return stats == null ? 0d : Wert(stats, "TotalPacketBytes");
        }

        static double Wert(object o, string name)
        {
            if (o == null) return 0d;
            try
            {
                object v = null;
                PropertyInfo p = AccessTools.Property(o.GetType(), name);
                if (p != null) v = p.GetValue(o, null);
                else
                {
                    FieldInfo f = AccessTools.Field(o.GetType(), name);
                    if (f != null) v = f.GetValue(o);
                }
                return v == null ? 0d : Convert.ToDouble(v);
            }
            catch { return 0d; }
        }

        static string Zahl(object o, string name)
        {
            if (o == null) return "?";
            try
            {
                object v = null;
                PropertyInfo p = AccessTools.Property(o.GetType(), name);
                if (p != null) v = p.GetValue(o, null);
                else
                {
                    FieldInfo f = AccessTools.Field(o.GetType(), name);
                    if (f != null) v = f.GetValue(o);
                }
                if (v == null) return "?";
                return Convert.ToDouble(v).ToString("0");
            }
            catch { return "?"; }
        }
    }

    // ------------------------------------------------------------ Research

    /// <summary>
    /// Erkundungswerkzeug fuer die Frage, was mit Regionen und Szenen geht.
    ///
    /// WAS IM CLIENT STEHT
    /// -------------------
    /// GameRegionsManager laedt ScriptableObjects/GameRegions, ein
    /// GameRegionsData mit einem Array GameRegionData[] RegionsList. Jeder
    /// Eintrag hat region, startScene und List&lt;int&gt; scenes - alles reine
    /// Buildindizes aus den BuildSettings.
    ///
    /// Der Release-Datensatz enthaelt genau EINE Region:
    ///     region 0 Severoufimsk, startScene 5, scenes [5, 6, 9, 7, 13, 14]
    ///
    /// Die BuildSettings kennen aber 19 Szenen. Nicht benutzt und trotzdem
    /// vollstaendig im Build: 3 Bunker_A65, 4 GW_Scene_2, 18 Underground_Lab
    /// sowie die Chunks 0, 2, 3, 4, 7, 8, 9.
    ///
    /// Gereist wird ueber GameLocationChangeManager::ChangeGameLocation, das
    /// einen LocationChangeTrigger erwartet. In GenerateServerOptions steht die
    /// entscheidende Zeile: bei LocationaChangeType == 2 wird _gameScene direkt
    /// aus trigger.SubLocation gesetzt. SubLocation IST also der Buildindex.
    ///
    /// Beides zusammen heisst: eine Szene laesst sich anspringen, sobald sie in
    /// der Szenenliste ihrer Region steht - denn GetRegionDataAtScene liefert
    /// sonst null, und JoinRoom laeuft in eine NullReference.
    ///
    /// Standardmaessig ist hier alles aus. Es ist ein Werkzeug zum Nachsehen,
    /// keine Spielfunktion.
    /// </summary>
    public static class Research
    {
        static bool _armed;
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;

        public static void ReportRegions()
        {
            try
            {
                object data = GetRegionsData();
                if (data == null) { RevivalPlugin.L.LogInfo("Regionen: kein GameRegionsData."); return; }
                FieldInfo fl = AccessTools.Field(data.GetType(), "RegionsList");
                IEnumerable list = fl == null ? null : fl.GetValue(data) as IEnumerable;
                if (list == null) { RevivalPlugin.L.LogInfo("Regionen: keine RegionsList."); return; }

                RevivalPlugin.L.LogInfo("--- Regionen ---");
                foreach (object r in list)
                {
                    if (r == null) continue;
                    Type rt = r.GetType();
                    object region = Val(rt, r, "region");
                    object start = Val(rt, r, "startScene");
                    IEnumerable scenes = Val(rt, r, "scenes") as IEnumerable;
                    RevivalPlugin.L.LogInfo("  region=" + Regions.RegionName(region)
                                            + " startScene=" + Regions.SceneName(start)
                                            + " scenes=" + Regions.SceneNames(scenes));
                }
                RevivalPlugin.L.LogInfo("--- Regionen Ende ---");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Regionen: " + ex.Message); }
        }

        public static void Tick()
        {
            if (RevivalPlugin.CfgSceneJump == null || !RevivalPlugin.CfgSceneJump.Value) return;
            if (!_keyParsed)
            {
                _keyParsed = true;
                try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), RevivalPlugin.CfgJumpKey.Value, true); }
                catch { _key = KeyCode.None; }
                if (_key == KeyCode.None)
                    RevivalPlugin.L.LogWarning("JumpKey ist kein KeyCode: "
                                               + RevivalPlugin.CfgJumpKey.Value);
                else if (!_armed)
                {
                    _armed = true;
                    RevivalPlugin.L.LogInfo("Szenenwechsel scharf: " + _key + " -> Szene "
                                            + RevivalPlugin.CfgJumpScene.Value);
                }
            }
            if (_key == KeyCode.None) return;
            if (!Input.GetKeyDown(_key)) return;
            int region = RevivalPlugin.CfgJumpRegion.Value;
            if (region >= 0) JumpToRegion(region);
            else Jump(RevivalPlugin.CfgJumpScene.Value);
        }

        /// <summary>
        /// Assembles a LocationChangeTrigger and hands it to
        /// ChangeGameLocation. Type 2 means sub-location, and then SubLocation
        /// is the GameScene VALUE of the target scene - not a build index.
        ///
        /// Evidence from GenerateServerOptions: for type 2 it writes
        /// trigger.SubLocation and for type 1
        /// GetGameRegionData(trigger.Region).startScene into the same field
        /// _gameScene. Both numbers therefore have the same type, and that
        /// type is the GameScene enum. Cross-check against the scene files:
        /// the door trigger in Bunker_A65 carries SubLocation 6, which is
        /// GW_Scene_2, and Bunker_A65 itself reports CurrentScene 9.
        /// </summary>
        public static void Jump(int gameScene)
        {
            try
            {
                Type tTrig = RevivalPlugin.TypeByName("LocationChangeTrigger");
                Type tMgr = RevivalPlugin.TypeByName("GameLocationChangeManager");
                if (tTrig == null || tMgr == null)
                {
                    RevivalPlugin.L.LogWarning("Szenenwechsel: Typen nicht gefunden.");
                    return;
                }

                UnityEngine.Object mgrObj = UnityEngine.Object.FindObjectOfType(tMgr);
                if (mgrObj == null)
                {
                    RevivalPlugin.L.LogWarning("Szenenwechsel: kein GameLocationChangeManager in der Szene.");
                    return;
                }

                GameObject go = new GameObject("NextDayRevival_Jump");
                Component trig = go.AddComponent(tTrig);
                if (trig == null)
                {
                    UnityEngine.Object.Destroy(go);
                    RevivalPlugin.L.LogWarning("Szenenwechsel: Trigger nicht anlegbar.");
                    return;
                }
                SetField(tTrig, trig, "Id", 9000 + gameScene);
                SetField(tTrig, trig, "LocationaChangeType", 2);
                SetField(tTrig, trig, "SubLocation", gameScene);
                SetField(tTrig, trig, "ShowOnMapUI", false);

                MethodInfo m = AccessTools.Method(tMgr, "ChangeGameLocation", null, null);
                if (m == null)
                {
                    UnityEngine.Object.Destroy(go);
                    RevivalPlugin.L.LogWarning("Szenenwechsel: ChangeGameLocation nicht gefunden.");
                    return;
                }
                RevivalPlugin.L.LogInfo("Szenenwechsel nach "
                                        + Regions.SceneName(gameScene) + " ...");
                m.Invoke(mgrObj, new object[] { trig });
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Szenenwechsel: " + ex); }
        }

        /// <summary>
        /// Changes into the start scene of a whole region. That is change
        /// type 1: GenerateServerOptions then fetches
        /// GetGameRegionData(Region).startScene by itself. It is exactly how
        /// the three bunker doors in the game send the player back up to the
        /// surface - type 1, Region 0, SubLocation 0.
        /// </summary>
        public static void JumpToRegion(int region)
        {
            try
            {
                Type tTrig = RevivalPlugin.TypeByName("LocationChangeTrigger");
                Type tMgr = RevivalPlugin.TypeByName("GameLocationChangeManager");
                if (tTrig == null || tMgr == null)
                {
                    RevivalPlugin.L.LogWarning("Region change: types not found.");
                    return;
                }
                UnityEngine.Object mgrObj = UnityEngine.Object.FindObjectOfType(tMgr);
                if (mgrObj == null)
                {
                    RevivalPlugin.L.LogWarning("Region change: no "
                        + "GameLocationChangeManager in the scene.");
                    return;
                }

                GameObject go = new GameObject("NextDayRevival_RegionJump");
                Component trig = go.AddComponent(tTrig);
                if (trig == null)
                {
                    UnityEngine.Object.Destroy(go);
                    RevivalPlugin.L.LogWarning("Region change: cannot add the trigger.");
                    return;
                }
                SetField(tTrig, trig, "Id", 9500 + region);
                SetField(tTrig, trig, "LocationaChangeType", 1);
                SetField(tTrig, trig, "Region", region);
                SetField(tTrig, trig, "SubLocation", 0);
                SetField(tTrig, trig, "ShowOnMapUI", false);

                MethodInfo m = AccessTools.Method(tMgr, "ChangeGameLocation", null, null);
                if (m == null)
                {
                    UnityEngine.Object.Destroy(go);
                    RevivalPlugin.L.LogWarning("Region change: ChangeGameLocation "
                        + "not found.");
                    return;
                }
                RevivalPlugin.L.LogInfo("Region change to "
                                        + Regions.RegionName(region) + " ...");
                m.Invoke(mgrObj, new object[] { trig });
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Region change: " + ex); }
        }

        static void SetField(Type t, object o, string name, object value)
        {
            FieldInfo f = AccessTools.Field(t, name);
            if (f == null) return;
            try
            {
                if (f.FieldType.IsEnum) f.SetValue(o, Enum.ToObject(f.FieldType, value));
                else f.SetValue(o, Convert.ChangeType(value, f.FieldType));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("  Trigger." + name + ": " + ex.Message);
            }
        }

        static object Val(Type t, object o, string name)
        {
            FieldInfo f = AccessTools.Field(t, name);
            if (f == null) return null;
            try { return f.GetValue(o); } catch { return null; }
        }

        /// <summary>
        /// Gets hold of the GameRegionsData.
        ///
        /// FindObjectOfType CANNOT find this manager, and that is not a
        /// timing problem - it never could. Measured 2026-08-29 from the IL:
        ///
        ///     GameRegionsManager::.cctor
        ///         newobj GameRegionsManager::.ctor
        ///         stsfld Instance
        ///
        /// The class derives from MonoBehaviour but its instance is built by
        /// the **static constructor** with `new`, and it is never attached to
        /// a GameObject. There is no such component in any scene, so
        /// FindObjectOfType returns null for the whole session. That is why
        /// ReportRegions had been printing "kein GameRegionsData" since it
        /// was written, and why the second region was never registered on the
        /// first try in the game.
        ///
        /// The static field `Instance` is the way in - it is what the game
        /// itself uses (`ldsfld Instance` in UseTransition). Reading it also
        /// runs the static constructor, so the manager exists from the first
        /// attempt on.
        ///
        /// `mgr` is deliberately typed `object`: an instance created with
        /// `new` has no native peer, and UnityEngine.Object's overloaded
        /// `==` would report it as null. With the static type `object` the
        /// comparison is an ordinary reference check.
        /// </summary>
        internal static object GetRegionsData()
        {
            Type t = RevivalPlugin.TypeByName("GameRegionsManager");
            if (t == null) return null;

            object mgr = null;
            FieldInfo inst = AccessTools.Field(t, "Instance");
            if (inst != null && inst.IsStatic)
            {
                try { mgr = inst.GetValue(null); }
                catch { }
            }
            if (mgr == null) mgr = UnityEngine.Object.FindObjectOfType(t);
            if (mgr == null) return null;

            FieldInfo f = AccessTools.Field(t, "_gameRegionsData");
            object data = f == null ? null : f.GetValue(mgr);
            if (data == null)
            {
                MethodInfo setup = AccessTools.Method(t, "SetupGameRegionsData", null, null);
                if (setup != null)
                {
                    ResourceHook.Reentry = true;
                    try { setup.Invoke(mgr, null); }
                    catch { }
                    finally { ResourceHook.Reentry = false; }
                    data = f == null ? null : f.GetValue(mgr);
                }
            }
            return data;
        }
    }

    // -------------------------------------------------------------- Regions

    /// <summary>
    /// Adds a second region to the game's region list.
    ///
    /// WHY THIS WORKS AT ALL
    /// ---------------------
    /// Until 2026-08-29 REVERSE_ENGINEERING.md said that a region's
    /// `startScene` and `scenes` were build indices - so a new region would
    /// need a new scene in the build, and that would need a rebuild of the
    /// game. That was a misreading. They are values of the `GameScene` enum.
    ///
    /// EVIDENCE - `GameLocationChangeManager::GenerateServerOptions` writes
    /// into the one field `_gameScene` either
    ///     GetGameRegionData(trigger.Region).startScene   (change type 1)
    ///     trigger.SubLocation                            (change type 2)
    /// so both numbers have the same type. Cross-check against the scene
    /// files, measured 2026-08-29:
    ///     level3  Bunker_A65       SceneGamePlayDataObjects.CurrentScene  9
    ///     level4  GW_Scene_2                                              6
    ///     level6  Catacombs                                              13
    ///     level18 Underground_Lab                                        14
    /// Those are the enum values, not the build indices 3, 4, 6 and 18.
    ///
    /// WHAT THIS CLASS DOES
    /// --------------------
    /// `GameRegionsManager.gameRegionsList` is a plain array of
    /// `GameRegionData` with the three fields `region`, `startScene`,
    /// `scenes`. This class appends one element to that array - the same kind
    /// of work `Registry` does for the item databases.
    /// `GameModeOptionsUI::UpdateGameRegionsPopUp` walks the same array to
    /// fill the region drop-down of the room settings, so the new region
    /// shows up there by itself.
    ///
    /// WHY THE SCENES HAVE TO LEAVE REGION 0
    /// -------------------------------------
    /// `GetRegionDataAtScene` walks the list from index 0 and returns the
    /// FIRST region whose `scenes` contains the value. Region 0 comes first.
    /// If Bunker_A65 and Underground_Lab stayed in its list, region 0 would
    /// win every lookup and the new region would be a label and nothing else.
    /// That is what `TakeFromRegion0` is for. Region 0 keeps GW_Scene_1,
    /// GW_Scene_2, GW_Scene_3 and Catacombs - the whole surface world.
    ///
    /// NOT A SERVER MATTER. `Stage64Server.cs` knows neither game scenes nor
    /// game regions; both travel between clients in the Photon room options.
    /// Both players do need the same plugin, or one of them has a region the
    /// other cannot resolve.
    ///
    /// NOT YET ACCEPTED IN THE GAME - state 2026-08-29. The plan and the open
    /// points are in docs/ai/tasks/new-regions.md.
    /// </summary>
    public static class Regions
    {
        static float _next;
        static bool _loggedFail;

        /// <summary>Name of a GameScene value, from the game's own enum.</summary>
        public static string SceneName(object value)
        {
            return EnumName("GameScene", value);
        }

        /// <summary>Name of a GameRegion value, from the game's own enum.</summary>
        public static string RegionName(object value)
        {
            return EnumName("GameRegion", value);
        }

        /// <summary>A scene list as "[9 Bunker_A65, 14 Underground_Lab]".</summary>
        public static string SceneNames(IEnumerable scenes)
        {
            if (scenes == null) return "[]";
            System.Text.StringBuilder sb = new System.Text.StringBuilder("[");
            bool first = true;
            foreach (object o in scenes)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(SceneName(o));
            }
            return sb.Append("]").ToString();
        }

        static string EnumName(string typeName, object value)
        {
            if (value == null) return "null";
            int n;
            try { n = Convert.ToInt32(value); }
            catch { return value.ToString(); }
            try
            {
                Type t = RevivalPlugin.TypeByName(typeName);
                if (t != null && t.IsEnum)
                {
                    string name = Enum.GetName(t, Enum.ToObject(t, n));
                    if (!string.IsNullOrEmpty(name)) return n + " " + name;
                }
            }
            catch { }
            return n + " ?";
        }

        /// <summary>
        /// Called every frame, does work at most every two seconds. The
        /// GameRegionsManager only exists once the menu is up, and its data
        /// can be reloaded on a scene change - so this does not register once
        /// and forget, it checks whether the region is still in the list.
        ///
        /// While the list is out of reach the interval grows to ten seconds.
        /// `GetRegionsData` calls `SetupGameRegionsData`, and that reads a
        /// ScriptableObject out of Resources - not something to do every two
        /// seconds for a whole session if the data never turns up.
        /// </summary>
        public static void Tick()
        {
            if (RevivalPlugin.CfgNewRegion == null || !RevivalPlugin.CfgNewRegion.Value) return;
            if (Time.realtimeSinceStartup < _next) return;
            bool reached = false;
            try { reached = Apply(false); }
            catch (Exception ex)
            {
                if (!_loggedFail)
                {
                    _loggedFail = true;
                    RevivalPlugin.L.LogWarning("Region: " + ex.Message);
                }
            }
            _next = Time.realtimeSinceStartup + (reached ? 2f : 10f);
        }

        /// <summary>
        /// Registers the region if it is missing. `loud` also logs when there
        /// was nothing to do. Returns whether the region list could be
        /// reached at all - not whether anything was changed.
        /// </summary>
        public static bool Apply(bool loud)
        {
            object data = Research.GetRegionsData();
            if (data == null)
            {
                if (loud) RevivalPlugin.L.LogInfo("Region: no GameRegionsData yet.");
                return false;
            }
            FieldInfo fList = AccessTools.Field(data.GetType(), "RegionsList");
            Array arr = fList == null ? null : fList.GetValue(data) as Array;
            if (arr == null)
            {
                if (loud) RevivalPlugin.L.LogWarning("Region: no RegionsList.");
                return false;
            }

            int id = RevivalPlugin.CfgNewRegionId.Value;
            for (int i = 0; i < arr.Length; i++)
            {
                object row = arr.GetValue(i);
                if (row == null) continue;
                if (IntField(row, "region") == id)
                {
                    if (loud)
                        RevivalPlugin.L.LogInfo("Region " + RegionName(id)
                            + " is already in the list.");
                    return true;
                }
            }

            int start = RevivalPlugin.CfgNewRegionStart.Value;
            List<int> scenes = Parse(RevivalPlugin.CfgNewRegionScenes.Value);
            if (!scenes.Contains(start))
            {
                // Otherwise GetRegionDataAtScene would not find our own start
                // scene in our own region, and the room switch would end up in
                // the wrong region or in none at all.
                scenes.Insert(0, start);
                RevivalPlugin.L.LogInfo("Region: startScene " + SceneName(start)
                    + " was missing from the scene list and has been prepended.");
            }

            Type tRow = arr.GetType().GetElementType();
            object fresh = NewRow(tRow, id, start, scenes);
            if (fresh == null) return true;   // reached, but unusable - do not hammer it

            if (RevivalPlugin.CfgNewRegionExclusive.Value) TakeFromOthers(arr, scenes);

            Array longer = Array.CreateInstance(tRow, arr.Length + 1);
            Array.Copy(arr, longer, arr.Length);
            longer.SetValue(fresh, arr.Length);
            fList.SetValue(data, longer);

            RevivalPlugin.L.LogInfo("Region " + RegionName(id) + " registered: startScene "
                + SceneName(start) + ", scenes " + SceneNames(scenes)
                + (RevivalPlugin.CfgNewRegionExclusive.Value
                   ? ", taken out of the other regions." : ", other regions unchanged."));
            Research.ReportRegions();
            return true;
        }

        /// <summary>Fills a fresh GameRegionData with the three fields.</summary>
        static object NewRow(Type tRow, int region, int startScene, List<int> scenes)
        {
            if (tRow == null)
            {
                RevivalPlugin.L.LogWarning("Region: no GameRegionData type.");
                return null;
            }
            object row;
            try { row = Activator.CreateInstance(tRow); }
            catch
            {
                // No public default constructor: allocate uninitialised. All
                // three fields are written right below anyway.
                row = System.Runtime.Serialization.FormatterServices
                          .GetUninitializedObject(tRow);
            }
            if (row == null) return null;

            FieldInfo fRegion = AccessTools.Field(tRow, "region");
            FieldInfo fStart = AccessTools.Field(tRow, "startScene");
            FieldInfo fScenes = AccessTools.Field(tRow, "scenes");
            if (fRegion == null || fStart == null || fScenes == null)
            {
                RevivalPlugin.L.LogWarning("Region: GameRegionData does not have the "
                    + "three expected fields.");
                return null;
            }

            fRegion.SetValue(row, Enum.ToObject(fRegion.FieldType, region));
            fStart.SetValue(row, Enum.ToObject(fStart.FieldType, startScene));

            IList list = Activator.CreateInstance(fScenes.FieldType) as IList;
            if (list == null)
            {
                RevivalPlugin.L.LogWarning("Region: scenes is not a list.");
                return null;
            }
            Type element = ElementType(fScenes.FieldType);
            for (int i = 0; i < scenes.Count; i++)
                list.Add(element != null && element.IsEnum
                         ? Enum.ToObject(element, scenes[i]) : (object)scenes[i]);
            fScenes.SetValue(row, list);
            return row;
        }

        static Type ElementType(Type listType)
        {
            try
            {
                Type[] args = listType.GetGenericArguments();
                if (args != null && args.Length == 1) return args[0];
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Takes the given scenes away from every region that already exists.
        ///
        /// Not just region 0, even though only region 0 is in question today:
        /// the new row is APPENDED, and GetRegionDataAtScene returns the first
        /// region that contains the scene. Any row in front of ours would win,
        /// whatever number it carries.
        /// </summary>
        static void TakeFromOthers(Array arr, List<int> scenes)
        {
            for (int r = 0; r < arr.Length; r++)
            {
                object row = arr.GetValue(r);
                if (row == null) continue;
                FieldInfo f = AccessTools.Field(row.GetType(), "scenes");
                IList list = f == null ? null : f.GetValue(row) as IList;
                if (list == null) continue;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    int value;
                    try { value = Convert.ToInt32(list[i]); }
                    catch { continue; }
                    if (!scenes.Contains(value)) continue;
                    list.RemoveAt(i);
                    RevivalPlugin.L.LogInfo("  " + SceneName(value) + " removed from region "
                        + RegionName(IntField(row, "region")) + ".");
                }
            }
        }

        static int IntField(object o, string name)
        {
            FieldInfo f = AccessTools.Field(o.GetType(), name);
            if (f == null) return int.MinValue;
            try { return Convert.ToInt32(f.GetValue(o)); }
            catch { return int.MinValue; }
        }

        static List<int> Parse(string csv)
        {
            List<int> values = new List<int>();
            if (string.IsNullOrEmpty(csv)) return values;
            string[] parts = csv.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i].Trim();
                if (t.Length == 0) continue;
                try { values.Add(Convert.ToInt32(t)); }
                catch { RevivalPlugin.L.LogWarning("Region: not a number: " + t); }
            }
            return values;
        }

        /// <summary>
        /// Label for the drop-down - only when the config asks for a name of
        /// its own.
        ///
        /// CORRECTION TO new-regions.md section 3: the key is NOT
        /// "$GameRegion_2". `UpdateGameRegionsPopUp` puts the `region` field
        /// into `String.Concat` with `box GameRegion`, so the call ends up in
        /// `Enum.ToString()` and the key carries the NAME. For region 2 that
        /// is "$GameRegion_Uralsk" - and that entry is already in the game's
        /// Localization_DB, translated into five languages. The new region
        /// names itself; the plugin has nothing to do. Only a value with no
        /// name in the enum falls back to the number, which is why both forms
        /// are answered here.
        /// </summary>
        public static bool Label(string key, ref string result)
        {
            if (RevivalPlugin.CfgNewRegion == null || !RevivalPlugin.CfgNewRegion.Value)
                return false;
            string own = RevivalPlugin.CfgNewRegionName.Value;
            if (string.IsNullOrEmpty(own)) return false;

            int id = RevivalPlugin.CfgNewRegionId.Value;
            if (key == "$GameRegion_" + id) { result = own; return true; }

            Type t = RevivalPlugin.TypeByName("GameRegion");
            if (t != null && t.IsEnum)
            {
                string name = Enum.GetName(t, Enum.ToObject(t, id));
                if (!string.IsNullOrEmpty(name) && key == "$GameRegion_" + name)
                {
                    result = own;
                    return true;
                }
            }
            return false;
        }
    }
}
