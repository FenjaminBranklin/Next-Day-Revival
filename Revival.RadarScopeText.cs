// Z R2a: localized map scope labels; keep the existing radar source ASCII.
namespace NextDayRevival
{
    internal static class RadarScopeText
    {
        internal static string Title { get { return Loc.T("ВОЗДУШНАЯ ОБСТАНОВКА - ВСЯ КАРТА", "AIR PICTURE - WHOLE MAP"); } }
        internal static string TerrainHint { get { return Loc.T("Низкие вертолеты за рельефом скрыты. Выберите цель на карте или в списке.", "Low helicopters behind terrain are hidden. Select a target on the map or in the list."); } }
        internal static string NoAntenna { get { return Loc.T("НЕТ СИГНАЛА - АНТЕННА РАЗРУШЕНА", "NO SIGNAL - ANTENNA DESTROYED"); } }
        internal static string NoConsole { get { return Loc.T("НЕТ СИГНАЛА - ПУЛЬТ ПОВРЕЖДЕН", "NO SIGNAL - CONSOLE DAMAGED"); } }
        internal static string NoArtwork { get { return Loc.T("Карта недоступна - координаты и цели сохранены", "Map artwork unavailable - coordinates and contacts retained"); } }
    }
}
