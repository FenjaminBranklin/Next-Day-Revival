// X merc quick orders: event-only aim queries, existing orders and combat AI.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercQuick
    {
        static ConfigEntry<string> _commandCfg, _cameraCfg;
        static string _commandText, _cameraText;
        internal static KeyCode CommandKey = KeyCode.Mouse2;
        static KeyCode _cameraKey = KeyCode.BackQuote, _cameraConfigured = KeyCode.BackQuote;
        static int _deployedFrame = -1;
        static bool _deployed, _owned, _cameraPatched;

        internal static void BindConfig(ConfigFile cfg)
        {
            _commandCfg = cfg.Bind("Mercs", "QuickOrderKey", "Mouse2",
                "With your owned merc roster: tap = immediate attack at release, double tap = move to cover near the mark, "
                + "hold = order categories, then choose an order. None disables this binding; OrderKey remains available.");
            _cameraCfg = cfg.Bind("Mercs", "CameraAimKey", "BackQuote",
                "Camera aim alignment toggle moved from middle mouse while your owned merc roster uses Mouse2. "
                + "Without owned mercs, middle mouse keeps its native camera function. Use a different key from QuickOrderKey.");
        }

        static KeyCode Parse(string text, KeyCode fallback)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), text.Trim(), true); }
            catch { return fallback; }
        }

        internal static void Keys()
        {
            string c = _commandCfg == null ? "Mouse2" : _commandCfg.Value;
            string a = _cameraCfg == null ? "BackQuote" : _cameraCfg.Value;
            if (c != _commandText)
            {
                _commandText = c; CommandKey = Parse(c, KeyCode.Mouse2);
                MercUi.CancelQuick();
            }
            if (a != _cameraText) { _cameraText = a; _cameraConfigured = Parse(a, KeyCode.BackQuote); }
            _cameraKey = _cameraConfigured;
            // Never issue an order and change the camera with the same press.
            if (_cameraKey == CommandKey) _cameraKey = KeyCode.None;
        }

        internal static bool Deployed
        {
            get
            {
                if (_deployedFrame == Time.frameCount) return _deployed;
                _deployedFrame = Time.frameCount; _deployed = false; _owned = false;
                List<Mercs.Record> roster = Mercs.Roster;
                for (int i = 0; i < roster.Count; i++)
                {
                    Mercs.Record r = roster[i];
                    if (r.Dead || r.Deserted) continue;
                    _owned = true;
                    if (r.Unit != null && r.Unit.Ai != null && !r.Unit.Deserting) _deployed = true;
                }
                return _deployed;
            }
        }

        internal static bool HasRoster
        {
            get { if (Deployed) return true; return _owned; }
        }

        internal static bool Available
        {
            get { return CommandKey != KeyCode.None && HasRoster
                && GameplayCursor.CanCommand && !MercUi.ListOpen; }
        }

        internal static void Install(Harmony harmony)
        {
            try
            {
                MethodInfo native = AccessTools.Method(RevivalPlugin.TypeByName("CameraSwitch"), "CameraZPOSManager", null, null);
                if (native == null) throw new MissingMethodException("CameraSwitch.CameraZPOSManager");
                harmony.Patch(native, null, null, new HarmonyMethod(typeof(MercQuick).GetMethod("CameraTranspile")), null, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Merc quick orders camera binding: " + ex.Message); }
            // Optional camera methods must never disable the command poller.
            HookLook(harmony, "RootMotion.CameraController", "UpdateInput");
            HookLook(harmony, "PlayerMovementController", "PlayerRotationControl");
            HookLook(harmony, "CameraFPSController", "Input_Rotate");
            HookLook(harmony, "MouseOrbitController", "LateUpdate");
            RevivalPlugin.L.LogInfo("Merc quick orders: command input enabled; native Mouse2 remap " + _cameraPatched + ".");
        }

        static void HookLook(Harmony harmony, string type, string method)
        {
            try
            {
                MethodInfo target = AccessTools.Method(RevivalPlugin.TypeByName(type), method, null, null);
                if (target != null) harmony.Patch(target, null, null,
                    new HarmonyMethod(typeof(MercQuick).GetMethod("CameraTranspile")), null, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Merc command camera " + type + "." + method + ": " + ex.Message); }
        }

        // Replace only literal button 2 at these two camera call sites. Scroll,
        // zoom, aim state and every other native input instruction remain intact.
        public static IEnumerable<CodeInstruction> CameraTranspile(IEnumerable<CodeInstruction> input)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(input);
            MethodInfo down = AccessTools.Method(typeof(Input), "GetMouseButtonDown", new Type[] { typeof(int) }, null);
            MethodInfo held = AccessTools.Method(typeof(Input), "GetMouseButton", new Type[] { typeof(int) }, null);
            MethodInfo axis = AccessTools.Method(typeof(Input), "GetAxis", new Type[] { typeof(string) }, null);
            for (int i = 1; i < code.Count; i++)
            {
                if (code[i - 1].opcode == OpCodes.Ldstr && code[i].opcode == OpCodes.Call
                    && Equals(code[i].operand, axis)
                    && ((string)code[i - 1].operand == "Mouse X" || (string)code[i - 1].operand == "Mouse Y"))
                    code[i].operand = typeof(MercQuick).GetMethod("CameraAxis");
                if (code[i - 1].opcode != OpCodes.Ldc_I4_2 || code[i].opcode != OpCodes.Call) continue;
                if (Equals(code[i].operand, down))
                {
                    code[i].operand = typeof(MercQuick).GetMethod("CameraDown");
                    _cameraPatched = true;
                }
                else if (Equals(code[i].operand, held)) code[i].operand = typeof(MercQuick).GetMethod("CameraHeld");
            }
            return code;
        }

        public static float CameraAxis(string name)
        { return MercUi.QuickLookBlocked ? 0f : Input.GetAxis(name); }

        public static bool CameraDown(int button)
        {
            Keys();
            if (button == 2 && CommandKey == KeyCode.Mouse2 && HasRoster)
                return _cameraKey != KeyCode.None && GameplayCursor.CanCommand && Input.GetKeyDown(_cameraKey);
            return Input.GetMouseButtonDown(button);
        }

        public static bool CameraHeld(int button)
        {
            Keys();
            if (button == 2 && CommandKey == KeyCode.Mouse2 && HasRoster)
                return _cameraKey != KeyCode.None && GameplayCursor.CanCommand && Input.GetKey(_cameraKey);
            return Input.GetMouseButton(button);
        }
    }

    internal static partial class MercUi
    {
        static MercQuickGesture _quickGesture;
        static bool _quickWheel, _quickNeedsUp;
        static bool _wheelScrollPick;
        static Vector3 _quickPoint;
        static Component _quickNpc;
        static GameObject _quickPlayer;
        static bool _quickHit, _quickActor;
        static Vector3 _commandPing;
        static float _commandPingUntil;
        static bool _commandPingMove;
        static Camera _pingCamera;
        static float _pingCameraAt;
        static int _inputFrame = -1;
        static float _inputWarnAt;

        internal static void PollCommands()
        {
            if (_inputFrame == Time.frameCount) return;
            _inputFrame = Time.frameCount;
            FrameProf.S(FrameProf.S_MercQuickT);
            try { TickInput(); }
            catch (Exception ex)
            {
                CancelQuick();
                if (Time.unscaledTime >= _inputWarnAt)
                {
                    _inputWarnAt = Time.unscaledTime + 5f;
                    RevivalPlugin.L.LogWarning("Merc command input: " + ex.Message);
                }
            }
            finally { FrameProf.E(FrameProf.S_MercQuickT); }
        }

        internal static bool QuickLookBlocked
        { get { return _wheelOpen && !MercRide.OwnerInVehicle && MercQuick.HasRoster
            && GameplayCursor.CanCommand && !ListOpen; } }

        static bool PlaceDown()
        { return (_wheelKey != KeyCode.None && Input.GetKeyDown(_wheelKey))
            || (MercQuick.Available && Input.GetKeyDown(MercQuick.CommandKey)); }

        static bool PlaceHeld()
        { return (_wheelKey != KeyCode.None && Input.GetKey(_wheelKey))
            || (MercQuick.Available && Input.GetKey(MercQuick.CommandKey)); }

        static bool PlaceUp()
        { return (_wheelKey != KeyCode.None && Input.GetKeyUp(_wheelKey))
            || (MercQuick.Available && Input.GetKeyUp(MercQuick.CommandKey)); }

        // G O2: choose a category, then release on an order. Root release cancels.
        static int _wheelGroup = -1, _wheelSlot = -1;
        static int _wheelRootSlot = -1;
        static float _wheelRootAt;
        static int _wheelSession;

        static void RadialBegin()
        {
            _wheelGroup = -1; _wheelSlot = -1; _wheelPick = -1;
            _wheelRootSlot = -1; _wheelRootAt = 0f;
            _wheelVec = Vector2.zero; _wheelScrollPick = false; _wheelSession++;
        }

        static void RadialGroup(int group)
        {
            _wheelGroup = group; _wheelSlot = -1; _wheelPick = -1;
            _wheelRootSlot = -1; _wheelRootAt = 0f;
            _wheelVec = Vector2.zero; _wheelScrollPick = false;
        }

        static bool PickWheel()
        {
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) return false;
            if (Input.GetKeyDown(KeyCode.Backspace)) { RadialGroup(-1); return true; }
            int count = MercRadialPlan.Count(_wheelGroup);
            if (!MercRide.OwnerInVehicle)
            {
                float x = Input.GetAxis("Mouse X"), y = Input.GetAxis("Mouse Y");
                if (x != 0f || y != 0f) _wheelScrollPick = false;
                _wheelVec += new Vector2(x, y);
                if (_wheelVec.magnitude > 6f) _wheelVec = _wheelVec.normalized * 6f;
                if (!_wheelScrollPick) _wheelSlot = _wheelVec.magnitude > 1.2f ? Sector(_wheelVec) : -1;
            }
            // Scroll also works in a seat; it never captures the native seat aim.
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (scroll != 0f)
            {
                _wheelScrollPick = true;
                _wheelSlot = (_wheelSlot < 0 ? (scroll > 0f ? 0 : count - 1)
                    : (_wheelSlot + (scroll > 0f ? 1 : -1) + count) % count);
            }
            _wheelPick = MercRadialPlan.Order(_wheelGroup, _wheelSlot);
            for (int n = 1; n <= count; n++)
                if (Input.GetKeyDown(KeyCode.Alpha0 + n))
                {
                    if (_wheelGroup < 0) { RadialGroup(n - 1); return true; }
                    Issue(MercRadialPlan.Order(_wheelGroup, n - 1)); return false;
                }
            if (_wheelGroup < 0)
            {
                if (_wheelRootSlot != _wheelSlot)
                { _wheelRootSlot = _wheelSlot; _wheelRootAt = Time.unscaledTime + 0.18f; }
                // Deliberate dwell opens the category; fresh motion then picks
                // its order. Left click remains native weapon input.
                if (_wheelSlot >= 0 && (Input.GetKeyDown(KeyCode.Return)
                    || Time.unscaledTime >= _wheelRootAt)) RadialGroup(_wheelSlot);
            }
            return true;
        }

        internal static void CancelQuick()
        {
            _quickGesture.Cancel();
            if (_quickWheel) { _wheelOpen = false; _wheelArmed = false; }
            _quickWheel = false; _quickNeedsUp = true;
            _quickNpc = null; _quickPlayer = null; _quickHit = false; _quickActor = false;
        }

        // PollCommands measures both quick input and the legacy K/list keys.
        static bool QuickInput()
        {
            return QuickStep();
        }

        static bool QuickStep()
        {
            MercQuick.Keys();
            KeyCode key = MercQuick.CommandKey;
            bool held = key != KeyCode.None && Input.GetKey(key);
            if (!MercQuick.Available || _placing || (_wheelArmed && !_quickWheel))
            { CancelQuick(); return false; }
            if (_quickNeedsUp)
            {
                if (held) return true;
                _quickNeedsUp = false;
            }
            bool busy = _quickGesture.Busy;
            int action = _quickGesture.Step(Time.unscaledTime, Input.GetKeyDown(key), held, Input.GetKeyUp(key));
            if ((action & MercQuickGesture.Capture) != 0) CaptureQuickAim();
            if ((action & MercQuickGesture.Attack) != 0) QuickAttack();
            if ((action & MercQuickGesture.Move) != 0) QuickMove();
            if ((action & MercQuickGesture.Open) != 0)
            {
                _quickWheel = true; _wheelOpen = true; _wheelArmed = false;
                RadialBegin();
            }
            if (_quickWheel)
            {
                if (!PickWheel()) { CancelQuick(); return true; }
                if ((action & MercQuickGesture.Close) != 0)
                {
                    int pick = _wheelPick;
                    _quickWheel = false; _wheelOpen = false;
                    if (pick >= 0) Issue(pick);
                }
            }
            return busy || _quickGesture.Busy || action != 0;
        }

        // Save aim at release before executing in the same frame. One ray per tap;
        // holding the menu needs only its existing throttled 5 Hz point preview.
        static void CaptureQuickAim()
        {
            _quickHit = false; _quickActor = false; _quickNpc = null; _quickPlayer = null;
            Camera cam = CameraOwner.MainCamera();
            if (cam == null) return;
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            RaycastHit hit;
            if (!Physics.Raycast(ray.origin + ray.direction * 3f, ray.direction, out hit, 840f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return;
            _quickPoint = hit.point; _quickHit = true;
            NpcWar.MercQuickHit(hit.collider, out _quickNpc, out _quickPlayer);
            _quickActor = _quickNpc != null || _quickPlayer != null;
            if (_quickNpc != null) _quickPoint = _quickNpc.transform.position;
            else if (_quickPlayer != null) _quickPoint = _quickPlayer.transform.position;
        }

        static void QuickAttack()
        {
            Reply();
            if (!_quickHit)
            {
                Toast(Loc.T("Нет цели под прицелом - наведите на врага или землю.",
                    "No target under the crosshair - aim at an enemy or ground."), true);
                return;
            }
            // G O1: the same mercs OrderAttack addresses (no gun/radar crew
            // without a pick).
            List<Mercs.Record> selected = Mercs.SquadSelection();
            // Keep selection and old orders across the existing command's refusals.
            MercOrder[] before = new MercOrder[selected.Count];
            bool enemy = false;
            for (int i = 0; i < selected.Count; i++)
            {
                before[i] = selected[i].Order;
                if (NpcWar.MercQuickEnemy(selected[i].Unit, _quickNpc, _quickPlayer)) enemy = true;
            }
            if (_quickActor && !enemy)
            {
                Toast(Loc.T("Это не допустимый враг: свои, мирные и белый список защищены.",
                    "That is not an eligible enemy: friends, peaceful mercs and the whitelist are protected."), true);
                return;
            }
            Vector3 from = Vector3.zero; int deployed = 0;
            for (int i = 0; i < selected.Count; i++)
                if (selected[i].Unit != null && selected[i].Unit.Ai != null)
                { from += selected[i].Unit.Ai.transform.position; deployed++; }
            from = deployed == 0 ? Mercs.OwnerPosition : from / deployed;
            Vector3 delta = _quickPoint - from; delta.y = 0f;
            // A close enemy needs focus, not an invalid short advance. Keep the
            // current post/leash, cover and low-health retreat in this case.
            bool close = enemy && delta.magnitude < MercOrder.AttackMinUnits;
            if (!close) Mercs.OrderAttack(_quickPoint, MercOrder.AtPoint);
            int focused = 0; bool given = false;
            List<Mercs.Record> got = close ? new List<Mercs.Record>(selected.Count) : null;
            for (int i = 0; i < selected.Count; i++)
            {
                Mercs.Record r = selected[i];
                bool changed = r.Order != before[i];
                given |= changed;
                if ((!changed && !close) || r.Unpaid || r.Dead ||
                    !NpcWar.MercQuickEnemy(r.Unit, _quickNpc, _quickPlayer)) continue;
                r.Unit.QuickNpc = _quickNpc; r.Unit.QuickPlayer = _quickPlayer;
                r.Unit.QuickSteam = _quickPlayer == null ? null : Mercs.SteamOf(_quickPlayer);
                r.Unit.QuickFor = r.Order; r.Unit.QuickUntil = Time.time + 120f;
                if (close) { Mercs.OrderFocused(r, _quickPoint); got.Add(r); }
                else NpcWar.MercOrderWake(r.Unit);
                focused++;
            }
            if (close) Mercs.Announce(Loc.T("АТАКА ЦЕЛИ", "ATTACK TARGET"), got);
            if (given || focused > 0) CommandPing(_quickPoint);
            _quickNpc = null; _quickPlayer = null;
        }

        static void QuickMove()
        {
            Reply();
            if (!_quickHit)
            {
                Toast(Loc.T("Наведите прицел на место для движения.", "Aim at a point to move there."), true);
                return;
            }
            Vector3 direction = _quickPoint - Mercs.OwnerPosition; direction.y = 0f;
            if (Mercs.OrderMove(_quickPoint, direction.normalized))
            { CommandPing(_quickPoint); _commandPingMove = true; }
            _quickNpc = null; _quickPlayer = null;
        }

        static void CommandPing(Vector3 point)
        { _commandPing = point; _commandPingUntil = Time.time + 3f; _commandPingMove = false; }

        static void DrawCommandPing()
        {
            if (Time.time >= _commandPingUntil || Event.current.type != EventType.Repaint) return;
            if (GameUi.WindowOpen || Admin.IsOpen) return;
            FrameProf.S(FrameProf.S_MercQuickD);
            if (_pingCamera == null || Time.time >= _pingCameraAt)
            { _pingCamera = CameraOwner.MainCamera(); _pingCameraAt = Time.time + 0.5f; }
            if (_pingCamera != null)
            {
                Vector3 sp = _pingCamera.WorldToScreenPoint(_commandPing + Vector3.up * 2f);
                if (sp.z > 0f)
                {
                    float x = sp.x, y = Screen.height - sp.y;
                    Color tint = _commandPingMove ? MoveBlue : AttackRed;
                    tint.a = Mathf.Min(1f, _commandPingUntil - Time.time);
                    if (_commandPingMove)
                    {
                        // Destination cross, in the native waypoint HUD style.
                        Box(new Rect(x - 4f, y - 1f, 8f, 2f), tint);
                        Box(new Rect(x - 1f, y - 4f, 2f, 8f), tint);
                    }
                    Box(new Rect(x - 10f, y - 10f, 20f, 2f), tint);
                    Box(new Rect(x - 10f, y + 8f, 20f, 2f), tint);
                    Box(new Rect(x - 10f, y - 8f, 2f, 16f), tint);
                    Box(new Rect(x + 8f, y - 8f, 2f, 16f), tint);
                }
            }
            FrameProf.E(FrameProf.S_MercQuickD);
        }
    }

    public static partial class NpcWar
    {
        // On tap only; walk collider parents, never FindObjectsOfType/Overlap.
        internal static void MercQuickHit(Collider collider, out Component npc, out GameObject player)
        {
            npc = null; player = null;
            if (collider == null) return;
            for (Transform t = collider.transform; t != null; t = t.parent)
            {
                if (_npcType != null) npc = t.GetComponent(_npcType);
                if (npc != null) return;
            }
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i];
                if (go != null && collider.transform.IsChildOf(go.transform)) { player = go; return; }
            }
        }

        internal static bool MercQuickEnemy(MercUnit u, Component npc, GameObject player)
        {
            if (u == null || u.Ai == null || u.Deserting || u.Peaceful) return false;
            Fighter f = FighterOf(u.Ai);
            if (f == null) return false;
            if (player != null)
            {
                if (player == u.OwnerGo || Mercs.ActorOf(player) == Mercs.LocalActor || Mercs.PlayerDead(player)) return false;
                string steam = u.QuickPlayer == player && u.QuickFor == u.Order ? u.QuickSteam : Mercs.SteamOf(player);
                if (steam != null && Mercs.Whitelisted(steam)) return false;
                int side = Mercs.FactionOf(player);
                bool attacker = Mercs.ActorOf(player) == u.LastAttacker && Time.time < u.AttackerUntil;
                return attacker || (side >= 0 && HatedValue(f.Hated, side));
            }
            if (npc == null || npc == u.Ai || !Alive(npc) || MercRide.HiddenRider(npc)) return false;
            Fighter other = FighterOf(npc);
            return (other == null || other.Squad != f.Squad)
                && ((other != null && other.Squad != null) || Targetable(npc))
                && Hostile(f.Hated, FactionOf(npc));
        }

        // Runs at the existing target scan cadence, before normal candidates.
        // Priority is temporary, revalidated, and only taken with native LOS.
        static bool MercQuickFocus(Fighter f, float range, float now)
        {
            MercUnit u = f.Squad.Merc;
            if (u.QuickFor == null) return false;
            if (u.QuickFor != u.Order || now >= u.QuickUntil || !MercQuickEnemy(u, u.QuickNpc, u.QuickPlayer))
            {
                u.QuickFor = null; u.QuickUntil = 0f;
                u.QuickNpc = null; u.QuickPlayer = null; u.QuickSteam = null;
                return false;
            }
            if (!MercMayEngage(u, now)) return false;
            Transform target = u.QuickPlayer != null ? u.QuickPlayer.transform : u.QuickNpc.transform;
            if ((target.position - f.Tr.position).sqrMagnitude > range * range) return false;
            float height;
            if (!AimPoint(f, target, out height)) return false;
            f.Target = target; f.TargetIsPlayer = u.QuickPlayer != null;
            f.Sees = true; f.AimHeight = height; f.LastSeen = now;
            f.NextLos = now + 0.3f;
            u.Sense.Note(target, u.QuickPlayer != null, now);
            SetMercKillTarget(f, u, u.QuickPlayer);
            return true;
        }
    }
}
