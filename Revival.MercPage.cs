// Next Day: Survival - Revival Toolkit
//
// MercPage - the trader window's Mercenaries page rebuilt on the UI kit
// (task W-UI3). TraderUi (Revival.TraderUi.cs) draws it as its third page at
// every civilian, looter and traitor trader:
//
//   LEFT   hire cards: a portrait plate in the tier colour, name, tier chip
//          (MercGrade from the traits and level, the same grade the M3 fight
//          loop plays), price, traits as percent buffs (aim / speed /
//          toughness chips with a tooltip each), upkeep per 24 in-game hours,
//          weapon, armour with its protection bar. Hiring asks twice (the
//          first click arms the card for 4 s); 1-9 over the cards picks a card.
//          A hire on the wire shows a spinner on its card, a refused hire an
//          error line with the reason ("money refunded") and Retry.
//   RIGHT  my mercs: name, profile, tier, one status chip from his own fight
//          loop (in cover, falling back, retreating at low health, patching
//          up ...), health, current order, where he is (metres + compass
//          point from you), upkeep due (or the grace left while unpaid) and
//          the payment state - queued, in flight (spinner, the master server
//          has not answered), failed (why, money refunded). Buttons: Locate
//          (a gold square on the map and a screen marker for 30 s), Order
//          (follow / stay here / vehicle / patrol here / perimeter here /
//          peaceful, for this merc or for all), Pay, Dismiss (asks twice).
//          The notification filter (all / important / deaths only) is the
//          [Mercs] Notifications setting; MercUi.Toast applies it.
//
// Everything that moves money or changes an order is Mercs' own (Hire, Pay,
// PayAllDue, Order*, Dismiss) - the master server stays authoritative for
// money and the roster, exactly as with the B3 hire tab this page replaces.
//
// Performance: runs only while the trader window shows this page (TraderUi
// calls Tick and Draw; FrameProf slots MercPage.Tick / MercPage.Draw). The
// positions are read at 2 Hz, every text is built when its key changes
// (UiMemo, cards per settlement and language), the balance is TraderUi's
// 2 Hz value. No allocation while the page sits open.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression bodies.
// UTF-8 (no BOM), compiled with /codepage:65001 like the rest.

using System;
using System.Collections.Generic;
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
        static float S(float v) { return UiKit.S(v); }

        // ------------------------------------------------------------ cards (hire)

        const float CardH = 236f;
        const float RowH = 136f;
        const float StripH = 96f;

        sealed class Card
        {
            internal Mercs.Profile P;
            internal string Name, Price, Upkeep, Weapon, Armour, Tier, Number, HireText, ConfirmText, Where;
            internal int TierN;
            internal readonly string[] Chips = new string[4];
            internal readonly string[] ChipTips = new string[4];
            internal int ChipCount;
        }

        static readonly List<Card> _cards = new List<Card>();
        static string _cardsFor;
        static List<Mercs.Profile> _cardsFrom;
        static int _cardsLang = -1;
        static readonly UiScroll _cardScroll = new UiScroll();
        static string _armedId;
        static float _armedUntil;

        /// <summary>The cards of this settlement, built when the settlement,
        /// the profile table or the language changes.</summary>
        static void Cards(string settlement, int lang)
        {
            List<Mercs.Profile> all = Mercs.AllProfiles;
            if (settlement == _cardsFor && ReferenceEquals(all, _cardsFrom) && lang == _cardsLang) return;
            _cardsFor = settlement; _cardsFrom = all; _cardsLang = lang;
            _cards.Clear();
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
            return tier >= 3 ? UiKit.Warn : tier == 2 ? UiKit.Good : tier == 1 ? UiKit.Accent : UiKit.TextDim;
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
        static readonly UiScroll _rowScroll = new UiScroll();
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

        /// <summary>From TraderUi while this page shows: positions at 2 Hz.</summary>
        internal static void Tick(double now)
        {
            if (now < _nextRead) return;
            _nextRead = now + 0.5;
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
            if (_armedId != null && Time.time >= _armedUntil) _armedId = null;
            if (_dismissId != NoRow && Time.time >= _dismissUntil) _dismissId = NoRow;
        }

        /// <summary>From TraderUi.Content: the page into the body rect.</summary>
        internal static void Draw(Rect b)
        {
            int lang = Lang();
            float gap = S(UiKit.Gap), row = S(UiKit.RowH);
            float now = Time.time;
            float leftW = Mathf.Floor((b.width - gap * 2f) * 0.56f);
            Rect left = new Rect(b.x, b.y, leftW, b.height);
            Rect right = new Rect(left.xMax + gap * 2f, b.y, b.width - leftW - gap * 2f, b.height);
            HireColumn(left, row, gap, lang, now);
            MercColumn(right, gap, lang, now);
        }

        // ------------------------------------------------------------ left: hire

        static readonly UiMemo _hireHead = new UiMemo();
        static readonly UiMemo _upkeepNote = new UiMemo();
        static readonly UiMemo _fullText = new UiMemo();
        static readonly UiMemo _hireFail = new UiMemo();

        static void HireColumn(Rect c, float row, float gap, int lang, float now)
        {
            string settlement = MercUi.EmbeddedSettlement;
            Cards(settlement, lang);
            float h = S(22f);
            int sk = settlement == "civilian" ? 1 : settlement == "looter" ? 2 : settlement == "traitor" ? 3 : 0;
            int hk = ((sk * 16) + (_faction + 1)) * 2 + lang;
            string head = _hireHead.Stale(hk) ? _hireHead.Set(hk, HireHead(sk)) : _hireHead.Text;
            UiKit.Section(new Rect(c.x, c.y, c.width, h), head);
            int grace = Mercs.GraceHours;
            int uk = grace * 2 + lang;
            string note = _upkeepNote.Stale(uk) ? _upkeepNote.Set(uk,
                Loc.T("Содержание списывается за каждые 24 игровых часа службы. Без оплаты: ",
                      "Upkeep is billed per 24 in-game hours while he is deployed. Unpaid: ")
                + grace + Loc.T(" ч отсрочки, потом он уходит.", " h grace, then he deserts.")) : _upkeepNote.Text;
            UiKit.Label(new Rect(c.x, c.y + h + S(2f), c.width, S(18f)), note, UiFont.Small, UiFont.Left, UiKit.TextDim);

            float top = c.y + h + S(26f);
            Rect foot = new Rect(c.x, c.yMax - row, c.width, row);
            Rect view = new Rect(c.x, top, c.width, foot.y - gap - top);
            float ch = S(CardH);
            int rows = (_cards.Count + 1) / 2;
            float contentH = Mathf.Max(0f, rows * (ch + gap) - gap);
            int money = TraderUi.MoneyNow;
            int support = Mercs.Support, alive = Mercs.AliveCount, cap = Mercs.Cap;
            bool hiring = Mercs.HirePending, busy = Mercs.MoneyBusy;
            if (_cards.Count == 0)
                UiKit.Paragraph(view, Loc.T("Этот торговец не предлагает наёмников.", "This trader offers no mercenaries."), UiKit.TextDim);
            else
            {
                Rect content = UiKit.BeginScroll(view, _cardScroll, contentH);
                float cw = Mathf.Floor((content.width - gap) * 0.5f);
                for (int i = 0; i < _cards.Count; i++)
                {
                    Card k = _cards[i];
                    Rect r = new Rect((i % 2) * (cw + gap), (i / 2) * (ch + gap), cw, ch);
                    if (r.yMax < _cardScroll.Offset || r.y > _cardScroll.Offset + view.height) continue;
                    int block = MercHire.Block(support, alive, cap, hiring, busy, money, k.P.Price);
                    DrawCard(r, k, block, alive, cap, lang, now);
                }
                UiKit.EndScroll();
            }
            LinkLine(foot);

            // 1-9 over the cards picks a card (arms it; again = hire).
            Event e = Event.current;
            if (e.type == EventType.KeyDown && UiKit.Hover(c) && e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha9)
            {
                int n = e.keyCode - KeyCode.Alpha0;
                if (n <= _cards.Count)
                {
                    Card k = _cards[n - 1];
                    Click(k, MercHire.Block(support, alive, cap, hiring, busy, money, k.P.Price), alive, cap, lang);
                    e.Use();
                }
            }
        }

        static string HireHead(int sk)
        {
            string where = sk == 1 ? Loc.T("НАЁМ - МИРНОЕ ПОСЕЛЕНИЕ", "HIRE - CIVILIAN SETTLEMENT")
                : sk == 2 ? Loc.T("НАЁМ - ПОСЕЛЕНИЕ МАРОДЁРОВ", "HIRE - LOOTER SETTLEMENT")
                : sk == 3 ? Loc.T("НАЁМ - ПРЕДАТЕЛИ + ВОЕННЫЙ ГОРОДОК", "HIRE - TRAITOR CAMP + MILITARY TOWN")
                : Loc.T("НАЁМ", "HIRE");
            return where + Loc.T("   ВАША СТОРОНА: ", "   YOUR SIDE: ") + Mercs.FactionLabel(_faction).ToUpperInvariant();
        }

        static void DrawCard(Rect r, Card k, int block, int alive, int cap, int lang, float now)
        {
            Mercs.Profile p = k.P;
            UiKit.Card(r);
            bool armed = _armedId == p.Id && now < _armedUntil;
            if (MercUi.Flashing(p.Id)) UiKit.Outline(r, UiKit.Good);
            else if (armed) UiKit.Outline(r, UiKit.Accent);
            float pad = S(10f);
            float x = r.x + pad, y = r.y + pad, w = r.width - pad * 2f;

            // Portrait plate in the tier colour, the hotkey number under it.
            Color tc = TierColor(k.TierN);
            Rect face = new Rect(x, y, S(52f), S(62f));
            UiKit.Fill(face, UiKit.Fade(tc, 0.16f), 1);
            float icon = face.width - S(16f);
            UiKit.Icon(new Rect(face.x + S(8f), face.y + S(4f), icon, icon), UiIcon.User, tc);
            UiKit.Label(new Rect(face.x, face.yMax - S(16f), face.width, S(14f)), k.Number, UiFont.Small, UiFont.Center, UiKit.TextDim);
            float tx = face.xMax + S(10f), tw = r.xMax - pad - tx;
            UiKit.Label(new Rect(tx, y, tw, S(20f)), k.Name, UiFont.Heading, UiFont.Left, UiKit.Text);
            Rect tier = new Rect(tx, y + S(22f), S(92f), S(18f));
            UiKit.Chip(tier, k.Tier, TierTone(k.TierN));
            UiKit.Tip(tier, Loc.T("Класс по навыкам: чем выше, тем умнее он держит укрытие и отходит",
                "Grade from his traits: higher = sharper cover use, earlier fall-back"));
            if (k.Where != null)
                UiKit.Label(new Rect(tier.xMax + S(6f), tier.y, tw - tier.width - S(6f), tier.height), k.Where, UiFont.Small, UiFont.Left, UiKit.TextDim);
            UiKit.Label(new Rect(tx, y + S(42f), tw, S(20f)), k.Price, UiFont.Heading, UiFont.Left, UiKit.Warn);
            y += S(68f);

            // Percentage buffs and the Flak gunner's calibration strength.
            Rect chips = new Rect(x, y, w, S(18f));
            if (k.ChipCount == 0) UiKit.Chip(UiKit.Col(chips, 0, 3), Loc.T("без навыков", "no traits"), UiTone.Info);
            int columns = k.ChipCount > 3 ? 2 : 3;
            for (int i = 0; i < k.ChipCount; i++)
            {
                Rect cr = UiKit.Col(chips, i % columns, columns);
                cr.y += S(22f) * (i / columns);
                UiKit.Chip(cr, k.Chips[i], UiTone.Success);
                UiKit.Tip(cr, k.ChipTips[i]);
            }
            y += S(k.ChipCount > 3 ? 46f : 24f);
            UiKit.Label(new Rect(x, y, w, S(18f)), k.Upkeep, UiFont.Small, UiFont.Left, UiKit.TextDim);
            y += S(18f);
            UiKit.Label(new Rect(x, y, w, S(18f)), k.Weapon, UiFont.Body, UiFont.Left, UiKit.Text);
            y += S(19f);
            Rect armour = new Rect(x, y, w, S(16f));
            UiKit.Label(armour, k.Armour, UiFont.Small, UiFont.Left, UiKit.TextDim);
            UiKit.Tip(armour, k.Armour);
            y += S(18f);
            UiKit.Progress(new Rect(x, y, w, S(4f)), p.Protection, UiKit.Accent);

            // Bottom: hire in flight / refused / the button.
            Rect btn = new Rect(x, r.yMax - pad - S(30f), w, S(30f));
            if (Mercs.HireProfile == p.Id)
            {
                UiKit.Status(btn, UiTone.Loading, Loc.T("Найм - ждём мастер-сервер...", "Hiring - waiting for the master server..."));
                return;
            }
            float errAt = Mercs.HireErrorAt;
            if (Mercs.HireErrorProfile == p.Id && Mercs.HireError != null && now >= errAt && now - errAt < MercPay.FailShow)
            {
                int rk = MercPay.Reason(Mercs.HireError) * 2 + lang;
                string fail = _hireFail.Stale(rk) ? _hireFail.Set(rk, Loc.T("Найм отклонён: ", "Hire refused: ")
                    + (lang == 0 ? ReasonRu : ReasonEn)[MercPay.Reason(Mercs.HireError)]
                    + Loc.T(" - деньги возвращены", " - money refunded")) : _hireFail.Text;
                Rect st = new Rect(btn.x, btn.y, btn.width - S(78f), btn.height);
                UiKit.Status(st, UiTone.Error, fail);
                UiKit.Tip(st, fail);
                if (UiKit.Button(new Rect(st.xMax + S(6f), btn.y, S(72f), btn.height), Loc.T("Ещё раз", "Retry"),
                    UiButton.Secondary, block == MercHire.Ok, null))
                    Click(k, block, alive, cap, lang);
                return;
            }
            string text = block != MercHire.Ok ? BlockText(block, alive, cap, lang) : armed ? k.ConfirmText : k.HireText;
            if (UiKit.Button(btn, text, armed ? UiButton.Primary : UiButton.Secondary, block == MercHire.Ok,
                block != MercHire.Ok ? null : armed ? Loc.T("Второй щелчок нанимает", "Second click hires")
                : Loc.T("Щелчок - выбрать, второй - нанять (деньги сразу)", "Click to pick, again to hire (paid at once)")))
                Click(k, block, alive, cap, lang);
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

        /// <summary>Hiring costs a fortune: the first click arms the card for
        /// 4 s, the second hires (Mercs.Hire checks everything again).</summary>
        static void Click(Card k, int block, int alive, int cap, int lang)
        {
            MercUi.Reply();
            if (block != MercHire.Ok) { UiKit.Toast(BlockText(block, alive, cap, lang), UiTone.Warning); return; }
            float now = Time.time;
            if (_armedId == k.P.Id && now < _armedUntil)
            {
                _armedId = null;
                Mercs.Hire(k.P);
                return;
            }
            _armedId = k.P.Id; _armedUntil = now + 4f;
        }

        static void LinkLine(Rect r)
        {
            int phase = Mercs.LinkPhase;
            int tone = phase == MercLink.PhaseLive ? UiTone.Success : phase == MercLink.PhaseNoModule ? UiTone.Error
                : phase == MercLink.PhaseSilent ? UiTone.Warning : UiTone.Loading;
            string t = Mercs.LinkText();
            UiKit.Status(r, tone, t);
            UiKit.Tip(r, t);
        }

        // ------------------------------------------------------------ right: my mercs

        static readonly UiMemo _mineHead = new UiMemo();
        static readonly UiMemo _payAll = new UiMemo();

        static void MercColumn(Rect c, float gap, int lang, float now)
        {
            List<Mercs.Record> roster = Mercs.Roster;
            int alive = Mercs.AliveCount, cap = Mercs.Cap;
            float h = S(22f);
            int hk = (alive * 64 + cap) * 2 + lang;
            string head = _mineHead.Stale(hk) ? _mineHead.Set(hk, Loc.T("МОИ НАЁМНИКИ   ", "MY MERCENARIES   ") + alive + " / " + cap) : _mineHead.Text;
            UiKit.Section(new Rect(c.x, c.y, c.width, h), head);

            // Notification filter.
            float y = c.y + h + gap;
            float lw = S(92f);
            UiKit.Label(new Rect(c.x, y, lw, S(28f)), Loc.T("Сообщения", "Messages"), UiFont.Small, UiFont.Left, UiKit.TextDim);
            Rect tabs = new Rect(c.x + lw, y, c.width - lw, S(28f));
            int mode = NoteMode;
            int picked = UiKit.Tabs(tabs, mode, lang == 0 ? NotesRu : NotesEn);
            if (picked != mode && _cfgNotes != null) _cfgNotes.Value = MercPageNote.Name(picked);
            UiKit.Tip(tabs, Loc.T("Какие сообщения наёмников показывать справа вверху. Ответы на ваши действия видны всегда.",
                "Which merc messages show top right. Answers to your own clicks and keys always show."));
            y += S(28f) + gap;

            // All mercs: order strip, pay everything due.
            int due = 0;
            for (int i = 0; i < roster.Count; i++) if (roster[i].Unpaid && !roster[i].PayPending) due++;
            Rect act = new Rect(c.x, y, c.width, S(30f));
            bool any = alive > 0;
            if (UiKit.Button(UiKit.Col(act, 0, 2), _orderAll ? Loc.T("Скрыть приказы", "Hide orders") : Loc.T("Приказ всем...", "Order all..."),
                UiButton.Secondary, any, null))
            { _orderAll = !_orderAll; _orderFor = NoRow; MercUi.Reply(); }
            int pk = due * 2 + lang;
            string payAll = _payAll.Stale(pk) ? _payAll.Set(pk, due == 0 ? Loc.T("Долгов нет", "Nothing due")
                : Loc.T("Оплатить долги (", "Pay all due (") + due + ")") : _payAll.Text;
            if (UiKit.Button(UiKit.Col(act, 1, 2), payAll, due > 0 ? UiButton.Primary : UiButton.Secondary, due > 0, null))
            { MercUi.Reply(); Mercs.PayAllDue(); }
            y = act.yMax + gap;
            if (_orderAll && any)
            {
                OrderStrip(new Rect(c.x, y, c.width, S(StripH)), null, lang);
                y += S(StripH) + gap;
            }

            Rect view = new Rect(c.x, y, c.width, c.yMax - y);
            if (roster.Count == 0)
            {
                int phase = Mercs.LinkPhase;
                Rect st = new Rect(view.x, view.y, view.width, S(UiKit.RowH));
                if (phase == MercLink.PhaseAsking || phase == MercLink.PhaseSilent)
                    UiKit.Status(st, UiTone.Loading, Loc.T("Загружаем ваши контракты...", "Loading your contracts..."));
                else if (phase == MercLink.PhaseNoModule)
                    UiKit.Status(st, UiTone.Error, Mercs.LinkText());
                else
                    UiKit.Paragraph(view, Loc.T("Наёмников пока нет. Наймите слева - он выйдет рядом с этим торговцем и пойдёт за вами.",
                        "No mercenaries yet. Hire one on the left - he steps out beside this trader and follows you."), UiKit.TextDim);
                return;
            }

            float rowH = S(RowH), strip = S(StripH) + gap;
            float contentH = 0f;
            for (int i = 0; i < roster.Count; i++) contentH += rowH + gap + (roster[i].Id == _orderFor && !roster[i].Dead ? strip : 0f);
            Rect content = UiKit.BeginScroll(view, _rowScroll, Mathf.Max(0f, contentH - gap));
            float ry = 0f;
            int number = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                Mercs.Record m = roster[i];
                if (!m.Dead) number++;
                float hh = rowH + (m.Id == _orderFor && !m.Dead ? strip : 0f);
                Rect r = new Rect(0f, ry, content.width, hh);
                ry += hh + gap;
                if (r.yMax < _rowScroll.Offset || r.y > _rowScroll.Offset + view.height) continue;
                if (DrawRow(r, m, lang, now)) break;   // the roster changed (dismissed)
            }
            UiKit.EndScroll();
        }

        /// <summary>One merc. True when the roster changed under the loop.</summary>
        static bool DrawRow(Rect r, Mercs.Record m, int lang, float now)
        {
            Row row = RowOf(m, lang);
            bool dead = m.Dead;
            if (dead) UiKit.Fill(r, UiKit.Fade(UiKit.CardFill, 0.5f), 1);
            else UiKit.Card(r);
            if (MercUi.Located(m.Id)) UiKit.Outline(r, UiKit.Warn);
            float pad = S(10f);
            float x = r.x + pad, y = r.y + pad, w = r.width - pad * 2f;
            Color text = dead ? UiKit.TextDim : UiKit.Text;

            // Line 1: name, profile + tier, status chip.
            int status = StatusOf(m);
            float chipW = S(150f);
            UiKit.Label(new Rect(x, y, w - chipW - S(6f), S(20f)), row.Title, UiFont.Heading, UiFont.Left, text);
            Rect chip = new Rect(r.xMax - pad - chipW, y + S(1f), chipW, S(18f));
            UiKit.Chip(chip, (lang == 0 ? StatusRu : StatusEn)[status], MercStatus.Tone(status));
            y += S(20f);
            UiKit.Label(new Rect(x, y, w, S(16f)), row.Sub, UiFont.Small, UiFont.Left, UiKit.TextDim);
            y += S(20f);

            // Line 2: health, order, where.
            int pct = Mathf.Clamp(Mathf.RoundToInt(m.Hp * 100f), 0, 100);
            UiKit.Progress(new Rect(x, y + S(6f), S(70f), S(6f)), m.Hp, dead ? UiKit.TextDim : pct > 60 ? UiKit.Good : pct > 30 ? UiKit.Warn : UiKit.Bad);
            string hp = row.Hp.Stale(pct) ? row.Hp.Set(pct, UiNum.Of(pct) + " %") : row.Hp.Text;
            UiKit.Label(new Rect(x + S(76f), y, S(44f), S(18f)), hp, UiFont.Small, UiFont.Left, UiKit.TextDim);
            float ox = x + S(122f), whereW = S(104f);
            if (!dead)
            {
                MercOrder o = m.Order;
                int ok = ((((o.Mode * 8 + Mathf.Min(o.Points.Length, 7)) * 128 + Mathf.Clamp((int)o.RadiusM, 0, 127)) * 2
                    + (m.Peaceful ? 1 : 0)) * 2 + lang) * 1200 + AttackKey(m);
                string order = row.Order.Stale(ok) ? row.Order.Set(ok, OrderText(m)) : row.Order.Text;
                UiKit.Label(new Rect(ox, y, r.xMax - pad - whereW - ox, S(18f)), order, UiFont.Body, UiFont.Left, text);
                int wk = row.WhereKey * 2 + lang;
                string where = row.Where.Stale(wk) ? row.Where.Set(wk, WhereText(row.WhereKey, lang)) : row.Where.Text;
                UiKit.Label(new Rect(r.xMax - pad - whereW, y, whereW, S(18f)), where, UiFont.Small, UiFont.Right, UiKit.TextDim);
            }
            y += S(22f);

            // Line 3: upkeep due / payment state.
            Rect line = new Rect(x, y, w, S(20f));
            bool deserting = m.Unit != null && m.Unit.Deserting;
            int pay = MercPay.State(dead, m.Unpaid, m.PayPending, m.PayWanted, m.PayError != null, m.PayErrorAt, now);
            int reason = pay == MercPay.Failed ? MercPay.Reason(m.PayError) : 0;
            int tenths = MercDue.Tenths(m.Deployed, m.PaidUntil, Mercs.GraceHours, m.Unpaid);
            int dk = (((Math.Min(tenths, 20000) * 6 + pay) * MercPay.Reasons + reason) * 4 + (dead ? 1 : deserting ? 2 : 0)) * 2 + lang;
            string dueText = row.Due.Stale(dk) ? row.Due.Set(dk, DueText(m, row, pay, reason, tenths, deserting)) : row.Due.Text;
            if (dead) UiKit.Label(line, dueText, UiFont.Small, UiFont.Left, UiKit.TextDim);
            else if (deserting) UiKit.Status(line, UiTone.Error, dueText);
            else if (pay == MercPay.InFlight) UiKit.Status(line, UiTone.Loading, dueText);
            else if (pay == MercPay.Queued) UiKit.Status(line, UiTone.Warning, dueText);
            else if (pay == MercPay.Failed || pay == MercPay.Due) UiKit.Status(line, UiTone.Error, dueText);
            else UiKit.Label(line, dueText, UiFont.Small, UiFont.Left, UiKit.TextDim);
            UiKit.Tip(line, dueText);
            y += S(26f);
            if (dead) return false;

            // Line 4: Locate, Order, Pay, Dismiss, native medkit gift.
            Rect btns = new Rect(x, y, w, S(26f));
            bool spawned = m.Unit != null && m.Unit.Ai != null;
            if (UiKit.Button(UiKit.Col(btns, 0, 6), Loc.T("Найти", "Locate"), UiButton.Secondary, spawned,
                Loc.T("Отметить на карте и на экране на 30 с", "Mark him on the map and on screen for 30 s")))
                Locate(m, row, lang);
            bool open = _orderFor == m.Id;
            if (UiKit.Button(UiKit.Col(btns, 1, 6), open ? Loc.T("Скрыть", "Close") : Loc.T("Приказ...", "Order..."),
                UiButton.Secondary, !deserting, null))
            { _orderFor = open ? NoRow : m.Id; _orderAll = false; MercUi.Reply(); }
            bool canPay = m.Unpaid && !m.PayPending && !m.PayWanted && !deserting;
            if (UiKit.Button(UiKit.Col(btns, 2, 6), canPay ? row.PayText : Loc.T("Оплачен", "Paid up"),
                canPay ? UiButton.Primary : UiButton.Secondary, canPay, null))
            { MercUi.Reply(); Mercs.Pay(m, true); }
            bool armed = _dismissId == m.Id && now < _dismissUntil;
            if (UiKit.Button(UiKit.Col(btns, 3, 6), armed ? Loc.T("Точно?", "Really?") : Loc.T("Уволить", "Dismiss"),
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
            if (UiKit.Button(UiKit.Col(btns, 4, 6), Mercs.MedkitLabel(m),
                UiButton.Secondary, Mercs.CanGiveMedkit(m),
                m.Down.Down ? Loc.T("Оживить аптечкой из рюкзака (3 м); оставайтесь рядом", "Revive with an inventory medkit within 3 m; stay nearby")
                : Loc.T("Дать аптечку из рюкзака (10 м); оплата пополняет запас",
                    "Give one native medkit within 10 m; upkeep refills supplies"))) Mercs.GiveMedkit(m);
            if (UiKit.Button(UiKit.Col(btns, 5, 6), Mercs.MedicLabel(m), UiButton.Secondary, Mercs.CanMedic(m),
                Loc.T("Дополнительная роль: сражается, спасает раненых, расходует аптечки", "Extra duty: fights, rescues and treats allies using finite medkits")))
                Mercs.SetMedic(m, !m.Medic);
            if (open)
            {
                y += S(26f) + S(UiKit.Gap);
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
            if (UiKit.Button(UiKit.Col(r3, 0, 3), Loc.T("В укрытие", "Take cover"), UiButton.Secondary, true, null)) act = 6;
            if (UiKit.Button(UiKit.Col(r3, 1, 3), Loc.T("К пушкам", "Man guns"), UiButton.Secondary, true, null)) act = 7;
            if (UiKit.Button(UiKit.Col(r3, 2, 3), Loc.T("К радару", "Man radar"), UiButton.Secondary, true, null)) act = 8;
            if (UiKit.Button(UiKit.Col(r1, 0, 3), Loc.T("За мной", "Follow me"), UiButton.Secondary, true, null)) act = 0;
            if (UiKit.Button(UiKit.Col(r1, 1, 3), Loc.T("Стоять здесь", "Stay here"), UiButton.Secondary, true,
                Loc.T("Держать ваше текущее место", "Hold your current spot"))) act = 1;
            if (UiKit.Button(UiKit.Col(r1, 2, 3), Loc.T("За техникой", "Follow vehicle"), UiButton.Secondary, true,
                Loc.T("Сесть в вашу машину (или в следующую)", "Board your vehicle (or the next one you take)"))) act = 2;
            if (UiKit.Button(UiKit.Col(r2, 0, 3), Loc.T("Патруль здесь", "Patrol here"), UiButton.Secondary, true,
                Loc.T("Круг 30 м вокруг вашего места; маршрут по карте - клавиша L", "30 m loop around your spot; a map route: L list"))) act = 3;
            if (UiKit.Button(UiKit.Col(r2, 1, 3), Loc.T("Периметр здесь", "Perimeter here"), UiButton.Secondary, true,
                Loc.T("Охранять круг вокруг вашего места", "Guard a circle around your spot"))) act = 4;
            bool peaceful = target != null ? target.Peaceful : FirstPeaceful();
            if (UiKit.Button(UiKit.Col(r2, 2, 3), peaceful ? Loc.T("Мирный: ВКЛ", "Peaceful: ON") : Loc.T("Мирный: ВЫКЛ", "Peaceful: OFF"),
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
                    case 7: Mercs.OrderRaidGuns(); break;
                    case 8: Mercs.OrderAAPost(true, Mercs.OwnerPosition); break;
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
