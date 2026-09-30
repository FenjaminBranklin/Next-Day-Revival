// X radar clarity: localized constant labels; no string building on draw/tick.
namespace NextDayRevival
{
    internal static class RadarClarityText
    {
        internal static string Advice(int advice)
        {
            switch (advice)
            {
                case RadarClarityCore.Friend: return Loc.T("СВОЙ - не стрелять", "FRIEND - hold fire");
                case RadarClarityCore.Identify: return Loc.T("НЕИЗВЕСТНЫЙ - опознать перед огнем", "UNKNOWN - identify before firing");
                case RadarClarityCore.Heavy: return Loc.T("52-K в зоне огня", "52-K in range");
                case RadarClarityCore.Short: return Loc.T("ZU-23 в зоне огня / Stinger", "ZU-23 in range / Stinger");
                case RadarClarityCore.Low: return Loc.T("Низко / малая цель - лучше ZU-23 / Stinger", "Low / small target - prefer ZU-23 / Stinger");
                case RadarClarityCore.Outside: return Loc.T("Вне зоны своих орудий - ждать сближения", "Outside own gun envelope - wait for approach");
                case RadarClarityCore.Observe: return Loc.T("Только наблюдение - консоль не под вашим контролем", "Watch only - console refuses your orders");
                default: return Loc.T("Нет своих боеготовых орудий - занять / укомплектовать", "No own ready guns - capture / crew a gun");
            }
        }

        internal static string Type(int type)
        {
            switch (type)
            {
                case 0: return Loc.T("ВЕРТОЛЕТ Mi-8", "HELI Mi-8");
                case 1: return Loc.T("ТРАНСПОРТ An-2", "TRANSPORT An-2");
                case 2: return Loc.T("БОМБАРДИРОВЩИК Tu-95", "BOMBER Tu-95");
                case 3: return Loc.T("ДРОН FPV", "DRONE FPV");
                case 4: return Loc.T("ДРОН разведчик", "DRONE recon");
                default: return Loc.T("ВОЗДУШНАЯ ЦЕЛЬ", "AIRCRAFT");
            }
        }

        internal static string Picture { get { return Loc.T("ЦЕЛИ - ПО УГРОЗЕ (ETA ДО АЭРОДРОМА)", "CONTACTS - THREAT ORDER (ETA TO AIRFIELD)"); } }
        internal static string Legend { get { return Loc.T("Зеленый: свои   Красный: враг   Желтый: неизвестный", "Green: friendly   Red: hostile   Amber: unknown"); } }
        internal static string Landing { get { return Loc.T("Mi-8: ожидается высадка", "Mi-8 landing inbound"); } }
        internal static string Raid { get { return Loc.T("ВОЗДУШНЫЙ НАЛЕТ", "AIR RAID"); } }
        internal static string Eta { get { return Loc.T("ETA с", "ETA s"); } }
        internal static string Arrived { get { return Loc.T("СЕЙЧАС", "DUE"); } }
    }
}
