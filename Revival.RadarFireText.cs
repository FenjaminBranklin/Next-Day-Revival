// Z R2b / G R1: localized console texts. Keep TowerRadar.cs ASCII.
namespace NextDayRevival
{
    internal static class RadarFireText
    {
        internal static string Fire { get { return Loc.T("ОГОНЬ", "FIRE"); } }
        internal static string Focus { get { return Loc.T("ФОКУС", "FOCUS"); } }
        internal static string Auto { get { return Loc.T("АВТОМАТ", "FULL AUTO"); } }
        internal static string Cease { get { return Loc.T("ПРЕКРАТИТЬ ОГОНЬ", "CEASE FIRE"); } }
        internal static string Gun { get { return Loc.T("ОРУДИЕ", "GUN"); } }
        internal static string Crew { get { return Loc.T("РАСЧЕТ", "CREW"); } }
        internal static string Rounds { get { return Loc.T("СНАРЯДЫ", "ROUNDS"); } }
        internal static string Empty { get { return Loc.T("ПУСТО", "EMPTY"); } }
        internal static string Low { get { return Loc.T("МАЛО", "LOW"); } }
        internal static string Destroyed { get { return Loc.T("РАЗБИТО", "DESTROYED"); } }
        internal static string NoGuns { get { return Loc.T("Нет орудий в сети ПВО.", "No guns on the air defence net."); } }
        internal static string North { get { return Loc.T("С", "N"); } }
        internal static string SelectFirst { get { return Loc.T("Сначала выберите отметку на экране.", "Click a blip on the scope first."); } }
        internal static string Friendly { get { return Loc.T("Это СВОЙ - орудия не стреляют по своим.", "That track is FRIEND - the guns never fire on their own side."); } }
        internal static string Refused { get { return Loc.T("Пультом владеет оператор штаба - приказы не принимаются.", "The HQ operator holds this console - your orders are refused."); } }
        internal static string NoFollow { get { return Loc.T("Ни одно орудие вам не подчиняется.", "No gun follows you - every gun is manned by another side."); } }
        internal static string Prompt { get { return Loc.T("Пульт РЛС", "Radar console"); } }
    }
}
