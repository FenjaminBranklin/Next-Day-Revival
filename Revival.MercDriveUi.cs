// Z M5a: destination chosen by owner on map or by an explicit crosshair tap.
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercUi
    {
        static bool _placeDrive;
        static void StartDriveMap()
        {
            _placing = true; _placeDrive = true; _placeAttack = false;
            _route.Clear(); _placeSeen = Time.time; _placeKeyDown = -1f; _lastWasTap = false;
            Toast(Loc.T("Поездка: клик по карте - туда и обратно. Выбранные наёмники едут вместе. Esc - отмена.",
                "Vehicle trip: map click = out and back. Selected mercs ride together. Esc = cancel."), false);
        }
        static void DriveMapClick(Vector3 at)
        {
            Vector3 ground;
            if (!RevivalGroundEnemies.TryGround(at, 12f, out ground))
            { Toast(Loc.T("Нет поверхности в выбранной точке.", "No ground at the selected point."), true); return; }
            _placeDrive = _placing = false;
            Mercs.OrderDrive(ground);
        }
        static void DriveInput(bool gameWindow)
        {
            if (Time.time - _placeSeen > 120f || Input.GetKeyDown(KeyCode.Escape))
            { CancelRoute(Loc.T("Поездка отменена.", "Vehicle trip cancelled.")); return; }
            if (gameWindow || !PlaceDown()) return;
            Vector3 point, facing;
            if (!CrosshairPoint(out point, out facing))
            { Toast(Loc.T("Для поездки выберите землю.", "Choose ground for the vehicle trip."), true); return; }
            _placeDrive = _placing = false; Mercs.OrderDrive(point);
        }
        static void DrawDrivePlacing()
        {
            Rect r = new Rect(Screen.width * 0.5f - 280f, Screen.height - 170f, 560f, 48f);
            Box(r, new Color(0f, 0f, 0f, 0.72f));
            Centered(new Rect(r.x, r.y + 4f, r.width, 22f), Loc.T("ПОЕЗДКА ТУДА И ОБРАТНО", "VEHICLE ROUND TRIP"), _label);
            Centered(new Rect(r.x, r.y + 26f, r.width, 18f), Loc.T("Клик по карте или ", "Map click or ") + _wheelKeyText
                + Loc.T(" - земля под прицелом. Esc - отмена.", " tap = ground under crosshair. Esc = cancel."), _small);
        }
        static void DriveButtons(Rect r, Color color, bool selected)
        {
            if (ButtonColored(new Rect(r.x, r.y, 230f, r.height), Loc.T("На машине по карте...", "Drive vehicle on map..."), color, selected))
            { _listOpen = false; RestoreCursor(); StartDriveMap(); }
            if (ButtonColored(new Rect(r.x + 236f, r.y, 190f, r.height), Loc.T("Вернуть машину", "Return vehicle"), color, MercDrive.Active)) MercDrive.Return();
            VanillaUi.Label(new Rect(r.x + 432f, r.y + 4f, r.width - 432f, 22f), Loc.T("Исправная машина в 60 м", "Ready free vehicle within 60 m"), _small);
        }
    }
}
