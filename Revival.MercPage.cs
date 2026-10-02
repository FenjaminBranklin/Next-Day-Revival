// Next Day: Survival - Revival Toolkit
//
// MercPage - trader mercenaries with the native HUD_Marketplace form,
// fonts, textured list rows, buy button and Yes/No confirmation (a-u2).
// MercUi hosts this page beside the untouched native safe/market tabs.
// All hire/pay/order/medkit/medic/dismiss operations remain Mercs calls;
// the master server remains authoritative for money, inventory and roster.
// Tick reads positions, faction and money at 2 Hz only while this page shows.
// Cards, contract text and GUI styles are cached. C# 3.0, UTF-8 without BOM.
//

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercPage
    {
        // ------------------------------------------------------------ settings

        static ConfigEntry<string> _cfgNotes;
        static string _noteText;
        static int _noteMode;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgNotes = cfg.Bind("Mercs", "Notifications", "All",
                "Which merc messages show top right: All, Important (warnings, failed payments, deaths) or "
                + "Deaths. Answers to your own clicks and keys always show. Also set on the trader's "
                + "Mercenaries page.");
        }

        /// <summary>The filter (MercPageNote.ShowAll/ShowImportant/ShowDeaths),
        /// parsed only when the setting's text changes.</summary>
        internal static int NoteMode
        {
            get
            {
                if (_cfgNotes == null) return MercPageNote.ShowAll;
                string v = _cfgNotes.Value;
                if (!ReferenceEquals(v, _noteText)) { _noteText = v; _noteMode = MercPageNote.Parse(v); }
                return _noteMode;
            }
        }

        // ------------------------------------------------------------ texts

        static readonly string[] NotesRu = { "Все", "Важные", "Гибель" };
        static readonly string[] NotesEn = { "All", "Important", "Deaths" };
        static readonly string[] TierRu = { "НОВОБРАНЕЦ", "БОЕЦ", "ВЕТЕРАН", "ЭЛИТА" };
        static readonly string[] TierEn = { "RECRUIT", "REGULAR", "VETERAN", "ELITE" };
        static readonly string[] StatusRu =
        {
            "ГОТОВ", "УБИТ", "ДЕЗЕРТИРУЕТ", "ПРИБЫВАЕТ", "ЕДЕТ", "ОТХОД - МАЛО HP", "ОТСТУПАЕТ", "УКЛОНЯЕТСЯ",
            "ПЕРЕВЯЗКА", "ПЕРЕЗАРЯДКА", "ОБХОД", "В УКРЫТИИ", "ОГОНЬ ИЗ УКРЫТИЯ", "К УКРЫТИЮ", "В БОЮ",
            "У ОРУДИЯ", "САДИТСЯ"
        };
        static readonly string[] StatusEn =
        {
            "READY", "KILLED", "DESERTING", "ARRIVING", "RIDING", "RETREATING - LOW HP", "FALLING BACK", "EVADING",
            "PATCHING UP", "RELOADING", "FLANKING", "IN COVER", "FIRING FROM COVER", "TO COVER", "IN COMBAT",
            "ON THE GUN", "BOARDING"
        };
        static readonly string[] CompassRu = { "С", "СВ", "В", "ЮВ", "Ю", "ЮЗ", "З", "СЗ" };
        static readonly string[] CompassEn = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        static readonly string[] BlockRu = { "", "", "", "Идёт найм...", "Идёт оплата...", "Персонаж не загружен", "Не хватает денег" };
        static readonly string[] BlockEn = { "", "", "", "Hiring...", "Paying upkeep...", "Character not loaded", "Not enough money" };
        static readonly string[] ReasonRu =
        {
            "сервер отказал", "нет ответа сервера", "не хватает денег", "сервер видит другой баланс",
            "сервер не увидел списание", "лимит наёмников", "контракт неизвестен серверу", "ещё не к оплате",
            "нет связи с сервером"
        };
        static readonly string[] ReasonEn =
        {
            "refused by the server", "no answer from the master server", "not enough money",
            "the server saw a different balance", "the server did not see the money leave", "merc limit reached",
            "contract unknown to the server", "not due yet", "no master server link"
        };

        static int Lang() { return Loc.Lang() == 0 ? 0 : 1; }
        static float S(float v) { return v * VanillaSkin.K; }

        // ------------------------------------------------------------ cards (hire)

        const float CardH = 236f;
        const float RowH = 136f;
        const float StripH = 96f;

        sealed class Card
        {
            internal Mercs.Profile P;
            internal string Name, Price, Upkeep, Weapon, Armour, Tier, Number, HireText, ConfirmText, Where;
            internal string Initial, Description, DetailTip, Question;
            internal int TierN;
            internal readonly string[] Chips = new string[4];
            internal readonly string[] ChipTips = new string[4];
            internal int ChipCount;
        }

        static readonly List<Card> _cards = new List<Card>();
        static string _cardsFor;
        static List<Mercs.Profile> _cardsFrom;
        static int _cardsLang = -1;
        static float _hireOffset, _contractOffset;
        static int _pickedHire;
        static int _pickedMerc = NoRow;
        static bool _contracts;
        static Card _pendingHire;

        /// <summary>The cards of this settlement, built when the settlement,
        /// the profile table or the language changes.</summary>
        static void Cards(string settlement, int lang)
        {
            List<Mercs.Profile> all = Mercs.AllProfiles;
            if (settlement == _cardsFor && ReferenceEquals(all, _cardsFrom) && lang == _cardsLang) return;
            _cardsFor = settlement; _cardsFrom = all; _cardsLang = lang;
            _cards.Clear();
            _pickedHire = 0;
            _hireOffset = 0f;
            _pendingHire = null;
            VanillaSkin.CancelAsk();
            if (settlement == null) return;
            List<Mercs.Profile> list = Mercs.ProfilesFor(settlement);
            for (int i = 0; i < list.Count; i++)
            {
                Mercs.Profile p = list[i];
                Card k = new Card();
                k.P = p;
                k.Name = p.Name;
                k.Price = Mercs.Money0(p.Price);
                k.Upkeep = Loc.T("Содержание ", "Upkeep ") + Mercs.Money0(p.Upkeep)
                    + Loc.T(" за 24 игровых часа", " per 24 in-game hours");
                k.Weapon = p.WeaponLabel;
                k.Armour = p.ArmourLabel;
                k.TierN = TierOf(p);
                k.Tier = lang == 0 ? TierRu[k.TierN] : TierEn[k.TierN];
                k.Number = UiNum.Of(i + 1);
                k.HireText = Loc.T("Нанять  (", "Hire  (") + (i + 1) + ")";
                k.ConfirmText = Loc.T("Подтвердить ", "Confirm ") + k.Price;
                k.Where = p.Settlement == "mtown" ? Loc.T("военный городок", "military town") : null;
                k.ChipCount = 0;
                if (p.Precise > 0) AddChip(k, Loc.T("Меткость +", "Aim +") + p.Precise + "%",
                    Loc.T("Разброс оружия меньше на ", "Weapon spread smaller by ") + p.Precise + "%");
                if (p.Fast > 0) AddChip(k, Loc.T("Скорость +", "Speed +") + p.Fast + "%",
                    Loc.T("Бегает быстрее на ", "Moves faster by ") + p.Fast + "%");
                if (p.Tanky > 0) AddChip(k, Loc.T("Живучесть +", "Tough +") + p.Tanky + "%",
                    Loc.T("Здоровье +", "Health +") + p.Tanky + Loc.T("%, броня держит ещё ", "%, armour stops a further ")
                    + (p.Tanky / 2) + "%");
                if (p.AAGunner > 0) AddChip(k, Loc.T("Зенитчик ", "Flak gunner ") + p.AAGunner + "/50",
                    Loc.T("Точнее наводит 52-К, быстрее корректирует огонь и точнее ставит взрыватель. 25 - стандартный специалист. Исправный радар с оператором усиливает всю батарею.",
                        "Steadier 52-K aim, faster bracketing and tighter fuze timing. 25 is the standard specialist. A working, manned radar improves the whole battery."));
                k.Initial = string.IsNullOrEmpty(k.Name) ? "?" : k.Name.Substring(0, 1);
                k.Description = k.Upkeep + "\n" + k.Weapon + "\n" + k.Armour;
                k.DetailTip = k.Description;
                for (int c = 0; c < k.ChipCount; c++)
                {
                    k.Description += (c == 0 ? "\n" : "  ") + k.Chips[c];
                    k.DetailTip += "\n" + k.ChipTips[c];
                }
                k.Question = Loc.T("Нанять ", "Hire ") + k.Name + Loc.T(" за ", " for ") + k.Price + "?\n" + k.Upkeep;
                _cards.Add(k);
            }
        }

        static void AddChip(Card k, string text, string tip)
        {
            if (k.ChipCount >= k.Chips.Length) return;
            k.Chips[k.ChipCount] = text; k.ChipTips[k.ChipCount] = tip; k.ChipCount++;
        }

        static int TierOf(Mercs.Profile p)
        {
            return p == null ? 0 : Mathf.Clamp(MercGrade.Tier(MercGrade.Of(p.Precise, p.Fast, p.Tanky, p.Level)), 0, 3);
        }

        static Color TierColor(int tier)
        {
            return tier >= 3 ? VanillaSkin.Gold : tier >= 1 ? VanillaSkin.White : VanillaSkin.Grey;
        }

        static int TierTone(int tier)
        {
            return tier >= 3 ? UiTone.Warning : tier == 2 ? UiTone.Success : UiTone.Info;
        }

        // ------------------------------------------------------------ rows (my mercs)

        sealed class Row
        {
            internal string Title, Sub, PayText;
            internal int Lang = -1;
            internal int Upkeep;
            internal int Tier;
            internal int WhereKey = -1;
            internal readonly UiMemo Hp = new UiMemo();
            internal readonly UiMemo Order = new UiMemo();
            internal readonly UiMemo Where = new UiMemo();
            internal readonly UiMemo Due = new UiMemo();
        }

        static readonly Dictionary<int, Row> _rows = new Dictionary<int, Row>();
        static readonly bool[] _keep = new bool[64];
        const int NoRow = int.MinValue;
        static int _orderFor = NoRow;       // the row whose order strip is open
        static bool _orderAll;              // the "all mercs" order strip is open
        static int _dismissId = NoRow;
        static float _dismissUntil;

        static Row RowOf(Mercs.Record m, int lang)
        {
            Row row;
            if (!_rows.TryGetValue(m.Id, out row)) { row = new Row(); _rows[m.Id] = row; }
            if (row.Lang != lang)
            {
                Mercs.Profile p = Mercs.ProfileById(m.ProfileId);
                row.Lang = lang;
                row.Tier = TierOf(p);
                row.Upkeep = p == null ? 0 : p.Upkeep;
                row.Title = m.Name;
                row.Sub = (p == null ? m.ProfileId : p.Name) + "  -  " + (lang == 0 ? TierRu[row.Tier] : TierEn[row.Tier])
                    + (m.Session ? "  (test)" : "");
                row.PayText = Loc.T("Оплатить ", "Pay ") + Mercs.Money0(row.Upkeep);
                row.Hp.Invalidate(); row.Order.Invalidate(); row.Where.Invalidate(); row.Due.Invalidate();
            }
            return row;
        }

        // ------------------------------------------------------------ frame

        static double _nextRead;
        static int _faction = -1;

        static Component _moneyStats;
        static MethodInfo _getMoney;
        static int _moneyNow = -1;
        static double _nextStats;
        static readonly UiMemo _moneyText = new UiMemo();
        static readonly UiMemo _countText = new UiMemo();

        internal static void Open()
        {
            _nextRead = 0.0;
            _pickedHire = 0; _pickedMerc = NoRow;
            _hireOffset = _contractOffset = 0f;
            _contracts = false; _orderAll = false; _orderFor = NoRow;
            Close();
        }

        internal static void Close()
        {
            VanillaSkin.CancelAsk();
            _pendingHire = null;
            _dismissId = NoRow;
        }

        /// <summary>From the active MercUi tab: positions, faction and balance at 2 Hz.</summary>
        internal static void Tick(double now)
        {
            if (now < _nextRead) return;
            _nextRead = now + 0.5;
            if (_moneyStats == null && now >= _nextStats)
            {
                _nextStats = now + 2.0;
                _moneyStats = Admin.LocalStats();
                _getMoney = _moneyStats == null ? null : AccessTools.Method(_moneyStats.GetType(), "GetPlayerMoney", Type.EmptyTypes, null);
            }
            _moneyNow = _moneyStats == null || _getMoney == null ? -1 : FastCall.Int(_getMoney, _moneyStats);
            GameObject owner = Mercs.OwnerObject;
            _faction = Mercs.FactionOf(owner);
            Vector3 me = owner == null ? Vector3.zero : owner.transform.position;
            List<Mercs.Record> roster = Mercs.Roster;
            for (int i = 0; i < roster.Count; i++)
            {
                Mercs.Record m = roster[i];
                Row row;
                if (!_rows.TryGetValue(m.Id, out row)) continue;
                MercUnit u = m.Unit;
                if (m.Dead || owner == null || u == null || u.Ai == null) { row.WhereKey = -1; continue; }
                Vector3 at = u.Ai.transform.position;
                row.WhereKey = MercWhere.Key(at.x - me.x, at.z - me.z);
            }
            if (_rows.Count > roster.Count + 16) _rows.Clear();   // ended contracts: rebuilt on sight
            if (_dismissId != NoRow && Time.time >= _dismissUntil) _dismissId = NoRow;
        }

        static readonly UiMemo _fullText = new UiMemo();
        static readonly UiMemo _hireFail = new UiMemo();
        static readonly UiMemo _payAll = new UiMemo();

        /// <summary>The native form rect, never a custom safe/trade replacement.</summary>
        internal static bool Draw(Rect form)
        {
            int lang = Lang();
            float now = Time.time;
            Cards(MercUi.EmbeddedSettlement, lang);
            VanillaSkin.Begin(form);
            MercPageLook.BeginTips();
            bool enabled = GUI.enabled;
            bool asking = VanillaSkin.Asking;
            GUI.enabled = enabled && !asking;
            if (Event.current.type == EventType.Repaint) VanillaSkin.Frame(form);
            bool close = VanillaSkin.Close(VanillaSkin.Px(881f, 9f, 20f, 20f));
            if (VanillaSkin.TextTab(VanillaSkin.Px(447f, 7f, 195f, 28f), Loc.T("Найм", "Hire"), !_contracts, true))
                _contracts = false;
            if (VanillaSkin.TextTab(VanillaSkin.Px(642f, 7f, 195f, 28f), Loc.T("Мои наёмники", "My mercenaries"), _contracts, true))
                _contracts = true;
            if (_contracts) Contracts(lang, now);
            else HireList(lang, now);
            string money = _moneyText.Stale(_moneyNow) ? _moneyText.Set(_moneyNow, _moneyNow < 0 ? "?" : Mercs.Money0(_moneyNow)) : _moneyText.Text;
            VanillaSkin.Text(VanillaSkin.Px(684f, 461f, 152f, 23f), money, VanillaSkin.Regular, 26f, VanillaSkin.Right, VanillaSkin.White);
            int ck = (Mercs.AliveCount * 64 + Mercs.Cap) * 2 + lang;
            string count = _countText.Stale(ck) ? _countText.Set(ck, Loc.T("Наёмники ", "Mercs ") + Mercs.AliveCount + " / " + Mercs.Cap) : _countText.Text;
            VanillaSkin.Text(VanillaSkin.Px(447f, 489f, 184f, 17f), count, VanillaSkin.Regular, 14f, VanillaSkin.Left, VanillaSkin.Grey);
            VanillaSkin.Text(VanillaSkin.Px(632f, 489f, 236f, 17f), Mercs.LinkText(), VanillaSkin.Regular, 12f, VanillaSkin.Right, VanillaSkin.Grey);
            MercPageLook.Tip(VanillaSkin.Px(447f, 489f, 421f, 17f), Mercs.ProfileSource);
            GUI.enabled = enabled;
            int answer = VanillaSkin.DrawDialog();
            if (answer == 1 && _pendingHire != null)
            {
                Card hire = _pendingHire;
                _pendingHire = null;
                MercUi.Reply();
                // Mercs.Hire checks live money/cap/link again, then the master decides.
                Mercs.Hire(hire.P);
            }
            if (!VanillaSkin.Asking) { _pendingHire = null; MercPageLook.DrawTips(); }
            return close;
        }

        static int HireBlock(Card k)
        {
            return MercHire.Block(Mercs.Support, Mercs.AliveCount, Mercs.Cap, Mercs.HirePending, Mercs.MoneyBusy, _moneyNow, k.P.Price);
        }

        static void HireList(int lang, float now)
        {
            Rect view = VanillaSkin.Px(447f, 45f, 421f, 411f);
            _hireOffset = VanillaSkin.BeginList(view, VanillaSkin.Px(886f, 40f, 10f, 416f), _hireOffset, S(_cards.Count * 58f));
            for (int i = 0; i < _cards.Count; i++)
            {
                Rect r = new Rect(0f, S(i * 58f) - _hireOffset, view.width, S(58f));
                if (r.yMax < 0f || r.y > view.height) continue;
                Card k = _cards[i];
                if (VanillaSkin.Row(r, i == _pickedHire)) _pickedHire = i;
                VanillaSkin.Plate(new Rect(r.x + S(8f), r.y + S(5f), S(48f), S(48f)), k.Initial, TierColor(k.TierN), 26f);
                VanillaSkin.Text(new Rect(r.x + S(65f), r.y + S(4f), S(244f), S(24f)), k.Name, VanillaSkin.Regular, 18f, VanillaSkin.Left, VanillaSkin.White);
                VanillaSkin.Text(new Rect(r.x + S(65f), r.y + S(30f), S(240f), S(20f)), k.Tier, VanillaSkin.Regular, 14f, VanillaSkin.Left, VanillaSkin.Grey);
                VanillaSkin.Text(new Rect(r.x + S(310f), r.y + S(17f), S(100f), S(24f)), k.Price, VanillaSkin.Regular, 18f, VanillaSkin.Right, VanillaSkin.White);
            }
            VanillaSkin.EndList();
            if (_cards.Count == 0)
            {
                VanillaSkin.Paragraph(VanillaSkin.Px(40f, 351f, 366f, 84f), Loc.T("Этот торговец не предлагает наёмников.", "This trader offers no mercenaries."), 16f, VanillaSkin.Grey);
                return;
            }
            _pickedHire = Mathf.Clamp(_pickedHire, 0, _cards.Count - 1);
            Card picked = _cards[_pickedHire];
            HireDetail(picked, HireBlock(picked), lang, now);
            Event e = Event.current;
            if (GUI.enabled && e.type == EventType.KeyDown && !VanillaSkin.Asking)
            {
                int n = e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha9 ? e.keyCode - KeyCode.Alpha0 : 0;
                if (n > 0 && n <= _cards.Count)
                {
                    _pickedHire = n - 1;
                    Card k = _cards[_pickedHire];
                    Click(k, HireBlock(k), Mercs.AliveCount, Mercs.Cap, lang);
                    e.Use();
                }
            }
        }

        static void HireDetail(Card k, int block, int lang, float now)
        {
            Mercs.Profile p = k.P;
            VanillaSkin.Text(VanillaSkin.Px(40f, 9f, 348f, 22f), k.Name, VanillaSkin.Regular, 20f, VanillaSkin.Right, VanillaSkin.White);
            VanillaSkin.Plate(VanillaSkin.Px(71f, 29f, 300f, 300f), k.Initial, TierColor(k.TierN), 76f);
            VanillaSkin.Text(VanillaSkin.Px(85f, 39f, 272f, 26f), k.Tier, VanillaSkin.Bebas, 22f, VanillaSkin.Center, VanillaSkin.White);
            VanillaSkin.Bar(VanillaSkin.Px(85f, 282f, 272f, 6f), p.Protection, VanillaSkin.Green);
            VanillaSkin.Paragraph(VanillaSkin.Px(40f, 351f, 366f, 84f), k.Description, 14f, VanillaSkin.Grey);
            MercPageLook.Tip(VanillaSkin.Px(40f, 351f, 366f, 84f), k.DetailTip);
            if (k.Where != null) VanillaSkin.Text(VanillaSkin.Px(85f, 298f, 272f, 20f), k.Where, VanillaSkin.Regular, 14f, VanillaSkin.Center, VanillaSkin.White);
            VanillaSkin.Text(VanillaSkin.Px(39f, 471f, 152f, 26f), k.Price, VanillaSkin.Regular, 26f, VanillaSkin.Right, VanillaSkin.White);
            string status = block == MercHire.Ok ? null : BlockText(block, Mercs.AliveCount, Mercs.Cap, lang);
            if (Mercs.HireProfile == p.Id) status = Loc.T("Найм - ждём мастер-сервер...", "Hiring - waiting for the master server...");
            else if (Mercs.HireErrorProfile == p.Id && Mercs.HireError != null && now >= Mercs.HireErrorAt && now - Mercs.HireErrorAt < MercPay.FailShow)
            {
                int rk = MercPay.Reason(Mercs.HireError) * 2 + lang;
                status = _hireFail.Stale(rk) ? _hireFail.Set(rk, Loc.T("Найм отклонён: ", "Hire refused: ")
                    + (lang == 0 ? ReasonRu : ReasonEn)[MercPay.Reason(Mercs.HireError)]
                    + Loc.T(" - деньги возвращены", " - money refunded")) : _hireFail.Text;
            }
            VanillaSkin.Text(VanillaSkin.Px(40f, 331f, 366f, 18f), status, VanillaSkin.Regular, 13f, VanillaSkin.Left, VanillaSkin.Red);
            MercPageLook.Tip(VanillaSkin.Px(40f, 331f, 366f, 18f), status);
            if (VanillaSkin.BuyButton(VanillaSkin.Px(265f, 463f, 160f, 40f), Loc.T("Нанять", "Hire"), block == MercHire.Ok))
                Click(k, block, Mercs.AliveCount, Mercs.Cap, lang);
        }

        static string BlockText(int block, int alive, int cap, int lang)
        {
            if (block == MercHire.NoServer) return Mercs.ReasonNoServer();
            if (block == MercHire.Full)
            {
                int key = (alive * 64 + cap) * 2 + lang;
                return _fullText.Stale(key) ? _fullText.Set(key, alive + " / " + cap + Loc.T(" наёмников - лимит", " mercs - the limit")) : _fullText.Text;
            }
            return (lang == 0 ? BlockRu : BlockEn)[Mathf.Clamp(block, 0, BlockEn.Length - 1)];
        }

        // The native-style Yes/No dialog replaces the timed two-click arm.
        static void Click(Card k, int block, int alive, int cap, int lang)
        {
            MercUi.Reply();
            if (block != MercHire.Ok) { UiKit.Toast(BlockText(block, alive, cap, lang), UiTone.Warning); return; }
            _pendingHire = k;
            VanillaSkin.Ask(k.Question, 1);
        }

        static void Contracts(int lang, float now)
        {
            string[] notes = lang == 0 ? NotesRu : NotesEn;
            for (int i = 0; i < notes.Length; i++)
                if (VanillaSkin.TextTab(VanillaSkin.Px(447f + i * 140f, 43f, 140f, 27f), notes[i], NoteMode == i, true) && _cfgNotes != null)
                    _cfgNotes.Value = MercPageNote.Name(i);
            List<Mercs.Record> roster = Mercs.Roster;
            Rect view = VanillaSkin.Px(447f, 77f, 421f, 379f);
            _contractOffset = VanillaSkin.BeginList(view, VanillaSkin.Px(886f, 77f, 10f, 379f), _contractOffset, S(roster.Count * 58f));
            Mercs.Record picked = null;
            for (int i = 0; i < roster.Count; i++)
            {
                Mercs.Record m = roster[i];
                if (m.Id == _pickedMerc) picked = m;
                Rect r = new Rect(0f, S(i * 58f) - _contractOffset, view.width, S(58f));
                if (r.yMax < 0f || r.y > view.height) continue;
                if (VanillaSkin.Row(r, m.Id == _pickedMerc)) { _pickedMerc = m.Id; picked = m; _orderAll = false; }
                Row row = RowOf(m, lang);
                VanillaSkin.Text(new Rect(r.x + S(12f), r.y + S(4f), S(285f), S(24f)), row.Title, VanillaSkin.Regular, 18f, VanillaSkin.Left, m.Dead ? VanillaSkin.Grey : VanillaSkin.White);
                VanillaSkin.Text(new Rect(r.x + S(12f), r.y + S(31f), S(285f), S(20f)), (lang == 0 ? StatusRu : StatusEn)[StatusOf(m)], VanillaSkin.Regular, 14f, VanillaSkin.Left, VanillaSkin.Grey);
                VanillaSkin.Bar(new Rect(r.x + S(308f), r.y + S(26f), S(100f), S(6f)), m.Hp, VanillaSkin.Green);
            }
            VanillaSkin.EndList();
            if (picked == null && roster.Count > 0) { picked = roster[0]; _pickedMerc = picked.Id; }
            int due = 0;
            for (int i = 0; i < roster.Count; i++) if (roster[i].Unpaid && !roster[i].PayPending && !roster[i].Dead) due++;
            int pk = due * 2 + lang;
            string payAll = _payAll.Stale(pk) ? _payAll.Set(pk, Loc.T("Оплатить долги (", "Pay all due (") + due + ")") : _payAll.Text;
            if (VanillaSkin.Button(VanillaSkin.Px(447f, 463f, 111f, 32f), Loc.T("Оплатить все", "Pay all"), 16f, due > 0))
            { MercUi.Reply(); Mercs.PayAllDue(); }
            MercPageLook.Tip(VanillaSkin.Px(447f, 463f, 111f, 32f), payAll);
            if (VanillaSkin.Button(VanillaSkin.Px(563f, 463f, 111f, 32f), Loc.T("Приказ всем", "Order all"), 16f, Mercs.AliveCount > 0))
            { _orderAll = !_orderAll; _orderFor = NoRow; MercUi.Reply(); }
            if (VanillaSkin.Button(VanillaSkin.Px(265f, 463f, 160f, 40f), Loc.T("Полный список", "Full roster"), 18f, true))
                MercUi.OpenTraderRoster();
            if (_orderAll)
            {
                VanillaSkin.Text(VanillaSkin.Px(40f, 9f, 348f, 22f), Loc.T("Приказ всем наёмникам", "Orders for all mercenaries"), VanillaSkin.Regular, 20f, VanillaSkin.Right, VanillaSkin.White);
                OrderStrip(VanillaSkin.Px(40f, 150f, 366f, 96f), null, lang);
            }
            else if (picked != null)
            {
                Row row = RowOf(picked, lang);
                VanillaSkin.Text(VanillaSkin.Px(40f, 9f, 348f, 22f), row.Title, VanillaSkin.Regular, 20f, VanillaSkin.Right, VanillaSkin.White);
                VanillaSkin.Text(VanillaSkin.Px(71f, 65f, 300f, 60f), row.Sub, VanillaSkin.Bebas, 24f, VanillaSkin.Center, VanillaSkin.Gold);
                if (DrawRow(VanillaSkin.Px(40f, 151f, 366f, 284f), picked, lang, now)) _pickedMerc = NoRow;
            }
            else VanillaSkin.Paragraph(VanillaSkin.Px(40f, 351f, 366f, 84f),
                Loc.T("Наёмников пока нет. Выберите Найм, чтобы нанять бойца.", "No mercenaries yet. Select Hire to recruit one."), 16f, VanillaSkin.Grey);
        }

        /// <summary>One merc. True when the roster changed under the loop.</summary>
        static bool DrawRow(Rect r, Mercs.Record m, int lang, float now)
        {
            Row row = RowOf(m, lang);
            bool dead = m.Dead;
            float pad = S(10f);
            float x = r.x + pad, y = r.y + pad, w = r.width - pad * 2f;
            Color text = dead ? MercPageLook.TextDim : MercPageLook.Text;

            // Line 1: name, profile + tier, status chip.
            int status = StatusOf(m);
            float chipW = S(150f);
            MercPageLook.Label(new Rect(x, y, w - chipW - S(6f), S(20f)), row.Title, UiFont.Heading, UiFont.Left, text);
            Rect chip = new Rect(r.xMax - pad - chipW, y + S(1f), chipW, S(18f));
            MercPageLook.Chip(chip, (lang == 0 ? StatusRu : StatusEn)[status], MercStatus.Tone(status));
            y += S(20f);
            MercPageLook.Label(new Rect(x, y, w, S(16f)), row.Sub, UiFont.Small, UiFont.Left, MercPageLook.TextDim);
            y += S(20f);

            // Line 2: health, order, where.
            int pct = Mathf.Clamp(Mathf.RoundToInt(m.Hp * 100f), 0, 100);
            MercPageLook.Progress(new Rect(x, y + S(6f), S(70f), S(6f)), m.Hp, dead ? MercPageLook.TextDim : pct > 60 ? MercPageLook.Good : pct > 30 ? MercPageLook.Warn : MercPageLook.Bad);
            string hp = row.Hp.Stale(pct) ? row.Hp.Set(pct, UiNum.Of(pct) + " %") : row.Hp.Text;
            MercPageLook.Label(new Rect(x + S(76f), y, S(44f), S(18f)), hp, UiFont.Small, UiFont.Left, MercPageLook.TextDim);
            float ox = x + S(122f), whereW = S(104f);
            if (!dead)
            {
                MercOrder o = m.Order;
                int ok = ((((o.Mode * 8 + Mathf.Min(o.Points.Length, 7)) * 128 + Mathf.Clamp((int)o.RadiusM, 0, 127)) * 2
                    + (m.Peaceful ? 1 : 0)) * 2 + lang) * 1200 + AttackKey(m);
                string order = row.Order.Stale(ok) ? row.Order.Set(ok, OrderText(m)) : row.Order.Text;
                MercPageLook.Label(new Rect(ox, y, r.xMax - pad - whereW - ox, S(18f)), order, UiFont.Body, UiFont.Left, text);
                int wk = row.WhereKey * 2 + lang;
                string where = row.Where.Stale(wk) ? row.Where.Set(wk, WhereText(row.WhereKey, lang)) : row.Where.Text;
                MercPageLook.Label(new Rect(r.xMax - pad - whereW, y, whereW, S(18f)), where, UiFont.Small, UiFont.Right, MercPageLook.TextDim);
            }
            y += S(22f);

            // Line 3: upkeep due / payment state.
            Rect line = new Rect(x, y, w - S(116f), S(20f));
            MercPageLook.Label(new Rect(r.xMax - pad - S(112f), y, S(112f), S(20f)),
                Mercs.MedkitStockLabel(m), UiFont.Small, UiFont.Right, MercPageLook.TextDim);
            bool deserting = m.Unit != null && m.Unit.Deserting;
            int pay = MercPay.State(dead, m.Unpaid, m.PayPending, m.PayWanted, m.PayError != null, m.PayErrorAt, now);
            int reason = pay == MercPay.Failed ? MercPay.Reason(m.PayError) : 0;
            int tenths = MercDue.Tenths(m.Deployed, m.PaidUntil, Mercs.GraceHours, m.Unpaid);
            int dk = (((Math.Min(tenths, 20000) * 6 + pay) * MercPay.Reasons + reason) * 4 + (dead ? 1 : deserting ? 2 : 0)) * 2 + lang;
            string dueText = row.Due.Stale(dk) ? row.Due.Set(dk, DueText(m, row, pay, reason, tenths, deserting)) : row.Due.Text;
            if (dead) MercPageLook.Label(line, dueText, UiFont.Small, UiFont.Left, MercPageLook.TextDim);
            else if (deserting) MercPageLook.Status(line, UiTone.Error, dueText);
            else if (pay == MercPay.InFlight) MercPageLook.Status(line, UiTone.Loading, dueText);
            else if (pay == MercPay.Queued) MercPageLook.Status(line, UiTone.Warning, dueText);
            else if (pay == MercPay.Failed || pay == MercPay.Due) MercPageLook.Status(line, UiTone.Error, dueText);
            else MercPageLook.Label(line, dueText, UiFont.Small, UiFont.Left, MercPageLook.TextDim);
            MercPageLook.Tip(line, dueText);
            y += S(26f);
            if (dead) return false;

            // Line 4: Locate, Order, Pay, Dismiss, native medkit gift.
            Rect btns = new Rect(x, y, w, S(26f));
            bool spawned = m.Unit != null && m.Unit.Ai != null;
            if (MercPageLook.Button(MercPageLook.Col(btns, 0, 3), Loc.T("Найти", "Locate"), UiButton.Secondary, spawned,
                Loc.T("Отметить на карте и на экране на 30 с", "Mark him on the map and on screen for 30 s")))
                Locate(m, row, lang);
            bool open = _orderFor == m.Id;
            if (MercPageLook.Button(MercPageLook.Col(btns, 1, 3), open ? Loc.T("Скрыть", "Close") : Loc.T("Приказ...", "Order..."),
                UiButton.Secondary, !deserting, null))
            { _orderFor = open ? NoRow : m.Id; _orderAll = false; MercUi.Reply(); }
            bool canPay = m.Unpaid && !m.PayPending && !m.PayWanted && !deserting;
            if (MercPageLook.Button(MercPageLook.Col(btns, 2, 3), canPay ? row.PayText : Loc.T("Оплачен", "Paid up"),
                canPay ? UiButton.Primary : UiButton.Secondary, canPay, null))
            { MercUi.Reply(); Mercs.Pay(m, true); }
            btns.y += S(32f);
            bool armed = _dismissId == m.Id && now < _dismissUntil;
            if (MercPageLook.Button(MercPageLook.Col(btns, 0, 3), armed ? Loc.T("Точно?", "Really?") : Loc.T("Уволить", "Dismiss"),
                UiButton.Danger, true, Loc.T("Без возврата денег; второй щелчок увольняет", "No refund; a second click dismisses")))
            {
                MercUi.Reply();
                if (!armed) { _dismissId = m.Id; _dismissUntil = now + 4f; }
                else
                {
                    _dismissId = NoRow;
                    if (_orderFor == m.Id) _orderFor = NoRow;
                    _rows.Remove(m.Id);
                    Mercs.Dismiss(m);
                    return true;
                }
            }
            if (MercPageLook.Button(MercPageLook.Col(btns, 1, 3), m.Down.Down ? Mercs.MedkitLabel(m)
                : Loc.T("Дать аптечку", "Give medkit"),
                UiButton.Secondary, Mercs.CanGiveMedkit(m),
                m.Down.Down ? Loc.T("Оживить аптечкой из рюкзака (3 м); оставайтесь рядом", "Revive with an inventory medkit within 3 m; stay nearby")
                : Loc.T("Дать аптечку из рюкзака (10 м); оплата пополняет запас",
                    "Give one native medkit within 10 m; upkeep refills supplies"))) Mercs.GiveMedkit(m);
            if (MercPageLook.Button(MercPageLook.Col(btns, 2, 3), Mercs.MedicLabel(m), UiButton.Secondary, Mercs.CanMedic(m),
                Loc.T("Дополнительная роль: сражается, спасает раненых, расходует аптечки", "Extra duty: fights, rescues and treats allies using finite medkits")))
                Mercs.SetMedic(m, !m.Medic);
            if (open)
            {
                y = btns.yMax + S(6f);
                OrderStrip(new Rect(x, y, w, S(StripH)), m, lang);
            }
            return false;
        }

        static int StatusOf(Mercs.Record m)
        {
            MercUnit u = m.Unit;
            bool spawned = u != null && u.Ai != null;
            int seat = MercStatus.SeatNone;
            byte state = MercStatus.BrainOff, mode = MercStatus.ModeNormal;
            if (spawned)
            {
                MercSeat s = u.Ride;
                if (s.Carrier != null) seat = s.Gunner ? MercStatus.SeatGunner : MercStatus.SeatRiding;
                else if (s.Boarding != null) seat = MercStatus.SeatBoarding;
                MercBrain brain = u.Fight.Brain;
                if (brain != null) { state = brain.State; mode = brain.Mode; }
            }
            return MercStatus.Of(m.Dead, u != null && u.Deserting, spawned, seat, m.Combat, state, mode);
        }

        static string OrderText(Mercs.Record m)
        {
            MercOrder o = m.Order;
            if (o.Survive) return Loc.T("В укрытии", "Taking cover");
            if (o.Mode == MercOrder.ManGun) return Loc.T("У пушки", "Manning gun");
            if (o.Mode == MercOrder.ManRadar) return Loc.T("У радара", "Manning radar");
            string t;
            switch (o.Mode)
            {
                case MercOrder.Follow: t = Loc.T("Следует за вами", "Following you"); break;
                case MercOrder.Vehicle: t = Loc.T("За вашей техникой", "Following your vehicle"); break;
                case MercOrder.Patrol:
                    t = o.Points.Length == 0 ? Loc.T("Патруль вокруг точки", "Patrolling a loop")
                        : Loc.T("Патруль, точек: ", "Patrol, points: ") + o.Points.Length;
                    break;
                case MercOrder.Perimeter: t = Loc.T("Периметр ", "Perimeter ") + Mathf.RoundToInt(o.RadiusM) + " m"; break;
                case MercOrder.Attack: t = AttackText(m); break;
                default: t = Loc.T("Держит точку", "Holding a point"); break;
            }
            return m.Peaceful ? t + Loc.T(" - мирный", " - peaceful") : t;
        }

        /// <summary>merc-attack-orders: the attack's phase and the distance to
        /// the objective in 10 m steps (0 for every other order).</summary>
        static int AttackKey(Mercs.Record m)
        {
            if (m.Order.Mode != MercOrder.Attack) return 0;
            MercUnit u = m.Unit;
            int phase = u == null || u.Attack.For != m.Order ? MercAttackRun.Advance : u.Attack.Phase;
            float d = u == null || u.Ai == null ? MercAttackGeo.Length(m.Order) : MercAttackGeo.Flat(m.Order.Centre - u.Ai.transform.position);
            return 1 + phase * 199 + Mathf.Clamp((int)(d / 28f), 0, 198);
        }

        static string AttackText(Mercs.Record m)
        {
            int key = AttackKey(m) - 1;
            int phase = key / 199, tens = key % 199;
            switch (phase)
            {
                case MercAttackRun.Holding: return Loc.T("Атака: держит цель", "Attack: holding the objective");
                case MercAttackRun.Stalled: return Loc.T("Атака: не пройти - держится", "Attack: stuck - holding short");
                case MercAttackRun.Search: return Loc.T("Атака: ищет противника", "Attack: searching for the enemy");
                case MercAttackRun.Overwatch: return Loc.T("Атака: прикрывает, до цели ", "Attack: covering, objective ") + tens * 10 + " m";
                default: return Loc.T("Атака: наступает, до цели ", "Attack: advancing, objective ") + tens * 10 + " m";
            }
        }

        static string WhereText(int key, int lang)
        {
            if (key < 0) return Loc.T("не в мире", "not in the world");
            int metres = MercWhere.Metres(key);
            if (metres == 0) return Loc.T("рядом с вами", "beside you");
            return metres + (lang == 0 ? " м " : " m ") + (lang == 0 ? CompassRu : CompassEn)[MercWhere.Point(key)];
        }

        static string DueText(Mercs.Record m, Row row, int pay, int reason, int tenths, bool deserting)
        {
            if (m.Dead) return Loc.T("Контракт окончен - снаряжение осталось на теле", "Contract ended - his gear stays on the body");
            if (deserting) return Loc.T("Уходит - контракт потерян", "Deserting - the contract is lost");
            string cost = Mercs.Money0(row.Upkeep);
            switch (pay)
            {
                case MercPay.InFlight:
                    return Loc.T("Оплата ", "Paying ") + cost + Loc.T(" - ждём мастер-сервер...", " - waiting for the master server...");
                case MercPay.Queued:
                    return Loc.T("Оплата в очереди - идёт другая оплата", "Payment queued - another payment is on the wire");
                case MercPay.Failed:
                    return Loc.T("Оплата не прошла: ", "Payment failed: ") + (Loc.Ru ? ReasonRu : ReasonEn)[reason]
                        + Loc.T(" - деньги возвращены", " - money refunded")
                        + (m.Unpaid ? Loc.T(", уйдёт через ", ", deserts in ") + MercDue.Text(tenths) + Loc.T(" ч", " h") : "");
                case MercPay.Due:
                    return Loc.T("НЕ ОПЛАЧЕН - уйдёт через ", "UNPAID - deserts in ") + MercDue.Text(tenths)
                        + Loc.T(" игровых ч", " in-game h");
                default:
                    return Loc.T("Содержание ", "Upkeep ") + cost + Loc.T(" через ", " due in ") + MercDue.Text(tenths)
                        + Loc.T(" игровых ч", " in-game h");
            }
        }

        static void Locate(Mercs.Record m, Row row, int lang)
        {
            MercUi.Reply();
            MercUi.Locate(m);
            UiKit.Toast(m.Name + ": " + WhereText(row.WhereKey, lang)
                + Loc.T(" - отмечен на карте и на экране (30 с)", " - marked on the map and on screen (30 s)"), UiTone.Info);
        }

        // ------------------------------------------------------------ orders

        /// <summary>Six order buttons for one merc (target) or for all (null).
        /// Point orders take the player's own spot - the player chose it.</summary>
        static void OrderStrip(Rect r, Mercs.Record target, int lang)
        {
            float bh = S(28f), g = S(6f);
            Rect r1 = new Rect(r.x, r.y, r.width, bh), r2 = new Rect(r.x, r.y + bh + g, r.width, bh);
            int act = -1;
            Rect r3 = new Rect(r.x, r.y + (bh + g) * 2f, r.width, bh);
            if (MercPageLook.Button(MercPageLook.Col(r3, 0, 2), Loc.T("В укрытие", "Take cover"), UiButton.Secondary, true, null)) act = 6;
            if (MercPageLook.Button(MercPageLook.Col(r3, 1, 2), Loc.T("Занять ПВО", "Man air defence"),
                UiButton.Secondary, true, Loc.T("Весь отряд; ещё раз - освободить", "Whole squad; click again to release"))) act = 7;
            if (MercPageLook.Button(MercPageLook.Col(r1, 0, 3), Loc.T("За мной", "Follow me"), UiButton.Secondary, true, null)) act = 0;
            if (MercPageLook.Button(MercPageLook.Col(r1, 1, 3), Loc.T("Стоять здесь", "Stay here"), UiButton.Secondary, true,
                Loc.T("Держать ваше текущее место", "Hold your current spot"))) act = 1;
            if (MercPageLook.Button(MercPageLook.Col(r1, 2, 3), Loc.T("За техникой", "Follow vehicle"), UiButton.Secondary, true,
                Loc.T("Сесть в вашу машину (или в следующую)", "Board your vehicle (or the next one you take)"))) act = 2;
            if (MercPageLook.Button(MercPageLook.Col(r2, 0, 3), Loc.T("Патруль здесь", "Patrol here"), UiButton.Secondary, true,
                Loc.T("Круг 30 м вокруг вашего места; маршрут по карте - клавиша L", "30 m loop around your spot; a map route: L list"))) act = 3;
            if (MercPageLook.Button(MercPageLook.Col(r2, 1, 3), Loc.T("Периметр здесь", "Perimeter here"), UiButton.Secondary, true,
                Loc.T("Охранять круг вокруг вашего места", "Guard a circle around your spot"))) act = 4;
            bool peaceful = target != null ? target.Peaceful : FirstPeaceful();
            if (MercPageLook.Button(MercPageLook.Col(r2, 2, 3), peaceful ? Loc.T("Мирный: ВКЛ", "Peaceful: ON") : Loc.T("Мирный: ВЫКЛ", "Peaceful: OFF"),
                UiButton.Secondary, true, Loc.T("Не начинает бой, отвечает на огонь", "Starts no fight, returns fire"))) act = 5;
            if (act >= 0) Order(target, act);
        }

        static bool FirstPeaceful()
        {
            List<Mercs.Record> roster = Mercs.Roster;
            for (int i = 0; i < roster.Count; i++) if (!roster[i].Dead) return roster[i].Peaceful;
            return false;
        }

        /// <summary>Mercs' own order calls address the selection: select the
        /// target (or all) for the call and put the player's selection back.</summary>
        static void Order(Mercs.Record target, int act)
        {
            MercUi.Reply();
            List<Mercs.Record> roster = Mercs.Roster;
            int n = Math.Min(roster.Count, _keep.Length);
            for (int i = 0; i < n; i++) { _keep[i] = roster[i].Selected; roster[i].Selected = target == null || roster[i] == target; }
            try
            {
                GameObject o = Mercs.OwnerObject;
                switch (act)
                {
                    case 0: Mercs.OrderFollow(); break;
                    case 1: if (o != null) Mercs.OrderStay(o.transform.position, o.transform.forward); break;
                    case 2: Mercs.OrderVehicle(); break;
                    case 3: Mercs.OrderPatrol(new List<Vector3>()); break;
                    case 4: if (o != null) Mercs.OrderPerimeter(o.transform.position, o.transform.forward); break;
                    case 5: Mercs.TogglePeaceful(); break;
                    case 6: Mercs.OrderRaidCover(); break;
                    case 7: Mercs.ToggleAirDefence(); break;
                }
            }
            finally
            {
                for (int i = 0; i < n && i < roster.Count; i++) roster[i].Selected = _keep[i];
            }
            _orderFor = NoRow;
            _orderAll = false;
        }
    }
}
