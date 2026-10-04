// Compact merc HUD/list. Native GroupPlayerPrefab: groupPlayerWhite 260x35,
// #545454 row, Bebas heading and Roboto body. No management window on L.
// Existing Mercs.Tick/Draw F6 slots include this adapter; no scene discovery,
// string formatting, roster copies or reference allocation in its hot paths.
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercUi
    {
        const int CompactWidth = 260, CompactRow = 36, CompactMaxRows = 6;
        static int _compactOffset;
        static bool _compactCursor;
        static readonly GUIStyle _compactName = new GUIStyle();
        static readonly GUIStyle _compactDuty = new GUIStyle();
        static readonly GUIStyle _compactTitle = new GUIStyle();
        static readonly GUIStyle _compactHit = new GUIStyle();
        static readonly Color CompactInk = new Color(0.329f, 0.329f, 0.329f, 1f);
        static readonly Color CompactHover = new Color(0.43f, 0.43f, 0.43f, 1f);

        static bool MercListBlocked()
        {
            // h-u1: only the radar view and the full map hide the list. The
            // 6.70 gate also asked CanCommand, CameraOwner and every custom
            // window; one stuck or false native flag hid it for the session.
            if (RadarScope.InView || GameplayCursor.CommandUiState == 8) return true;
            // Keep the trader's explicit Full roster entry, including whitelist.
            if (_listOpen && _listTab >= 0) return !_tabVisible;
            return false;
        }

        static void CompactListInput()
        {
            // The detailed roster belongs to its trader; do not leave the
            // large management form over gameplay after the trader closes.
            if (_listOpen && _listTab >= 0 && !_tabVisible)
            { _listOpen = false; RestoreCursor(); }
            if (_compactCursor && (!_listOpen || _listTab >= 0 || MercListBlocked()))
            {
                _compactCursor = false;
                RestoreCursor();
            }
        }

        static void CompactStyles(float k)
        {
            // Set properties on existing styles, also accepting late native fonts.
            _compactName.font = _compactDuty.font = VanillaUi.Font(false);
            _compactTitle.font = VanillaUi.Font(true);
            _compactName.fontSize = Mathf.RoundToInt(16f * k);
            _compactDuty.fontSize = Mathf.RoundToInt(12f * k);
            _compactTitle.fontSize = Mathf.RoundToInt(22f * k);
            _compactName.normal.textColor = _compactTitle.normal.textColor = VanillaUi.White;
            _compactDuty.normal.textColor = VanillaUi.Grey;
            _compactName.alignment = _compactDuty.alignment = TextAnchor.MiddleLeft;
            _compactTitle.alignment = TextAnchor.MiddleLeft;
            _compactName.clipping = _compactDuty.clipping = TextClipping.Clip;
            _compactName.wordWrap = _compactDuty.wordWrap = false;
        }

        static Rect CompactFrame(int count, out float k, out int rows)
        {
            // Never exceed the native 260px group row, even on a large monitor.
            k = Mathf.Min(Mathf.Clamp(Screen.height / 1080f, 0.75f, 1f),
                Mathf.Max(0.1f, (Screen.width - 24f) / CompactWidth));
            rows = Mathf.Min(CompactMaxRows, Mathf.Max(1,
                Mathf.FloorToInt((Screen.height - 174f * k - 24f) / (CompactRow * k))));
            rows = Mathf.Min(rows, Mathf.Max(1, count));
            float height = (28f + rows * CompactRow) * k;
            return new Rect(12f, Mathf.Max(12f, Screen.height - 150f * k - height), CompactWidth * k, height);
        }

        static string CompactDuty(Mercs.Record m)
        {
            if (m.Dead) return Loc.T("МЕРТВ", "DEAD");
            if (m.Down.Down) return Loc.T("ПОМОЩЬ", "SOS");
            if (m.Unpaid) return Loc.T("ОПЛАТА", "UNPAID");
            if (m.Order.MoveNear || m.Order.Survive) return Loc.T("УКРЫТИЕ", "COVER");
            if (m.Unit != null && m.Unit.Rally) return Loc.T("СБОР", "RALLY");
            if (m.Medic) return Loc.T("МЕДИК", "MEDIC");
            switch (m.Order.Mode)
            {
                case MercOrder.Follow: return Loc.T("ЗА МНОЙ", "FOLLOW");
                case MercOrder.Vehicle: return Loc.T("ТЕХНИКА", "VEHICLE");
                case MercOrder.Drive: return Loc.T("ПОЕЗДКА", "DRIVE");
                case MercOrder.ManGun: return Loc.T("ПУШКА", "GUN");
                case MercOrder.ManRadar: return Loc.T("РАДАР", "RADAR");
                case MercOrder.Patrol: return Loc.T("ПАТРУЛЬ", "PATROL");
                case MercOrder.Perimeter: return Loc.T("ПЕРИМЕТР", "PERIM");
                case MercOrder.Attack: return Loc.T("АТАКА", "ATTACK");
                default: return Loc.T("СТОИТ", "STAY");
            }
        }

        static bool _compactWarned;
        static Texture2D _compactWhite;

        // Cold: a slow resolver retry while art is missing, one log line.
        static void CompactMissing(Texture2D row, Texture2D on, Texture2D off, Texture2D bar)
        {
            VanillaUi.Want();
            if (_compactWarned) return;
            _compactWarned = true;
            RevivalPlugin.L.LogWarning("MercList: native art missing (row " + (row != null) + ", checks "
                + (on != null && off != null) + ", bar " + (bar != null) + ", font " + (VanillaUi.Font(false) != null)
                + ") - plain rows until it resolves.");
        }

        static void CompactTexture(Rect r, Texture2D texture, Color tint)
        {
            Color old = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(r, texture);
            GUI.color = old;
        }

        static void DrawCompactList(bool interactive)
        {
            if (MercListBlocked()) return;
            int count = Mercs.Roster.Count;
            if (count == 0 && !interactive) return;
            // Native assets are resolved by VanillaUi.Begin, never by this list.
            // h-u1: a missing texture or font used to hide the list for the
            // whole session. Draw the same #545454 rows on the built-in white
            // texture and the default font until the native art resolves.
            Texture2D rowArt = VanillaUi.Asset("groupPlayerWhite");
            Texture2D checkOn = VanillaUi.Asset("galka_enable"), checkOff = VanillaUi.Asset("galka_disable");
            Texture2D health = VanillaUi.Asset("progressbar");
            if (rowArt == null || checkOn == null || checkOff == null || health == null
                || VanillaUi.Font(false) == null) CompactMissing(rowArt, checkOn, checkOff, health);
            if (_compactWhite == null) _compactWhite = Texture2D.whiteTexture;
            Texture2D white = _compactWhite;
            if (rowArt == null) rowArt = white;
            if (health == null) health = white;
            float k; int rows;
            Rect frame = CompactFrame(count, out k, out rows);
            CompactStyles(k);
            _compactOffset = Mathf.Clamp(_compactOffset, 0, Mathf.Max(0, count - rows));
            if (interactive)
            {
                _compactCursor = true;
                CursorTracker.Restoring = true;
                try { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
                finally { CursorTracker.Restoring = false; }
                Event e = Event.current;
                if (e.type == EventType.ScrollWheel && frame.Contains(e.mousePosition))
                {
                    _compactOffset = Mathf.Clamp(_compactOffset + (e.delta.y > 0f ? 1 : -1), 0, Mathf.Max(0, count - rows));
                    e.Use();
                }
            }
            bool repaint = Event.current.type == EventType.Repaint;
            if (repaint)
            {
                CompactTexture(new Rect(frame.x, frame.y, frame.width, 26f * k), rowArt, CompactInk);
                VanillaUi.Label(new Rect(frame.x + 8f * k, frame.y, frame.width - 48f * k, 26f * k),
                    Loc.T("НАЁМНИКИ", "MERCS"), _compactTitle);
            }
            if (interactive && GUI.Button(new Rect(frame.xMax - 24f * k, frame.y, 24f * k, 24f * k), GUIContent.none, _compactHit))
            { _listOpen = false; CompactListInput(); }
            if (repaint && interactive)
                VanillaUi.Label(new Rect(frame.xMax - 20f * k, frame.y, 20f * k, 24f * k), "X", _compactDuty);
            if (count == 0)
            {
                if (repaint)
                {
                    Rect empty = new Rect(frame.x, frame.y + 28f * k, frame.width, 35f * k);
                    CompactTexture(empty, rowArt, CompactInk);
                    VanillaUi.Label(new Rect(empty.x + 8f * k, empty.y, empty.width - 16f * k, empty.height),
                        Loc.T("Нанять: у торговца", "Hire at a settlement trader"), _compactName);
                }
                return;
            }
            for (int i = 0; i < rows; i++)
            {
                Mercs.Record m = Mercs.Roster[_compactOffset + i];
                Rect r = new Rect(frame.x, frame.y + (28f + i * CompactRow) * k, frame.width, 35f * k);
                if (interactive)
                {
                    bool selected = GUI.Toggle(r, m.Selected, GUIContent.none, _compactHit);
                    if (selected != m.Selected) { m.Selected = selected; Reply(); }
                }
                if (!repaint) continue;
                bool hover = interactive && r.Contains(Event.current.mousePosition);
                CompactTexture(r, rowArt, hover ? CompactHover : CompactInk);
                Texture2D check = m.Selected ? checkOn : checkOff;
                Rect box = new Rect(r.x + 6f * k, r.y + 9f * k, 14f * k, 14f * k);
                if (check != null) CompactTexture(box, check, Color.white);
                else CompactTexture(box, white, m.Selected ? VanillaUi.White : VanillaSkin.BarBack);
                VanillaUi.Label(new Rect(r.x + 26f * k, r.y + 2f * k, 140f * k, 22f * k), m.Name, _compactName);
                Rect bar = new Rect(r.x + 26f * k, r.y + 26f * k, 64f * k, 4f * k);
                CompactTexture(bar, health, VanillaSkin.BarBack);
                float hp = Mathf.Clamp01(m.Hp);
                if (!m.Dead && hp > 0f)
                {
                    // Clip the full native texture instead of stretching its art.
                    Color old = GUI.color; GUI.color = HpColor(hp);
                    Rect fill = new Rect(bar.x, bar.y, bar.width * hp, bar.height);
                    GUI.DrawTextureWithTexCoords(fill, health, new Rect(0f, 0f, hp, 1f));
                    GUI.color = old;
                }
                VanillaUi.Label(new Rect(r.x + 174f * k, r.y + 5f * k, 80f * k, 24f * k), CompactDuty(m), _compactDuty);
            }
        }
    }
}
