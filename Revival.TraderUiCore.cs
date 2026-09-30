// Next Day: Survival - Revival Toolkit
//
// TraderUiCore - the Unity-free half of the trader window (task W-UI2).
// The window itself (Revival.TraderUi.cs) reads the game's own lists - the
// trader's offer per category, the sell list of the backpack, the safe - and
// hands plain numbers and strings to the classes here: row pools, grouping of
// identical backpack items, the category/search filter, the price comparison
// between both columns, the quantity picker and the money texts. Plain C# 3.0
// without a UnityEngine type, so research/trader_ui_check.py compiles this
// file UNCHANGED with the .NET 3.5 csc into a harness and runs it.
//
// Nothing here decides a price or moves an item: buying and selling stay the
// game's HUD_MarketplaceUI.BuyItem, the safe stays the game's
// MoveItemToStorage/MoveItemFromStorage, money stays the master server's.
//
// Allocation rule: rows are pooled and reused; texts are built when the data
// or the language changes, never per repaint; the filter rebuilds its index
// only when the category, the search text or the data version changes.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression bodies.
// UTF-8 (no BOM), compiled with /codepage:65001 like the rest.

using System;

namespace NextDayRevival
{
    /// <summary>The game's StorageObject.StorageCategory numbers; Sell (7)
    /// is the trader's "your items" list, not a real category.</summary>
    public static class TraderCat
    {
        public const int Firearm = 0;
        public const int Attachments = 1;
        public const int Clothes = 2;
        public const int Melee = 3;
        public const int Meds = 4;
        public const int Food = 5;
        public const int Other = 6;
        public const int Sell = 7;
        public const int Count = 7;
        /// <summary>Filter value for "every category".</summary>
        public const int All = -1;

        static readonly string[] NameRu = { "Огнестрел", "Обвесы", "Одежда", "Холодное", "Медицина", "Еда", "Прочее" };
        static readonly string[] NameEn = { "Firearms", "Attachments", "Clothes", "Melee", "Medical", "Food", "Other" };

        /// <summary>Filter bar labels: "All" then the seven categories.</summary>
        public static readonly string[] FilterRu = { "Все", "Огнестрел", "Обвесы", "Одежда", "Холодное", "Медицина", "Еда", "Прочее" };
        public static readonly string[] FilterEn = { "All", "Firearms", "Attach.", "Clothes", "Melee", "Medical", "Food", "Other" };

        public static string Name(int cat, int lang)
        {
            if (cat < 0 || cat >= Count) cat = Other;
            return lang == 0 ? NameRu[cat] : NameEn[cat];
        }

        /// <summary>Filter bar index (0 = All) to a category (-1 = All).</summary>
        public static int FromFilter(int index)
        {
            return index <= 0 || index > Count ? All : index - 1;
        }
    }

    /// <summary>
    /// One line of a column. On the trader side one offer (Count 1); on the
    /// inventory side a group of identical backpack items at the same sell
    /// price, with the backpack slots that hold them.
    /// </summary>
    public sealed class TraderRow
    {
        public int ItemId;
        /// <summary>Trader side: the buy price. Inventory side: the sell price of one item.</summary>
        public int Price;
        public int Category;
        public int Count;
        public int[] Slots = new int[4];
        public string Name;
        /// <summary>The item icon (a Texture; object so the core stays Unity-free).</summary>
        public object Icon;
        /// <summary>Trader side: what one sells back for (-1 unknown).
        /// Inventory side: what the trader charges for it (-1 not on offer).</summary>
        public int Other;
        /// <summary>Trader side: how many the player carries.</summary>
        public int Owned;
        /// <summary>Display texts, built by TraderBook.Texts on data or language change.</summary>
        public string PriceText;
        public string SubText;
        public string CountText;

        public void Reset()
        {
            ItemId = 0; Price = 0; Category = TraderCat.Other; Count = 0;
            Name = null; Icon = null; Other = -1; Owned = 0;
            PriceText = null; SubText = null; CountText = null;
        }

        public void AddSlot(int slot)
        {
            if (Count == Slots.Length)
            {
                int[] grown = new int[Slots.Length * 2];
                Array.Copy(Slots, grown, Slots.Length);
                Slots = grown;
            }
            Slots[Count++] = slot;
        }
    }

    /// <summary>A pooled list of rows plus a filtered, index-only view.</summary>
    public sealed class TraderList
    {
        TraderRow[] _rows = new TraderRow[32];
        int _count;
        int[] _view = new int[32];
        int _viewCount;
        int _viewCat = int.MinValue;
        string _viewSearch;
        int _viewVersion = -1;
        int _version;

        public int Count { get { return _count; } }
        public int ViewCount { get { return _viewCount; } }
        /// <summary>Bumps on every change of the rows (Clear/Add/Sort).</summary>
        public int Version { get { return _version; } }

        public TraderRow At(int i) { return _rows[i]; }
        public TraderRow View(int i) { return _rows[_view[i]]; }

        public void Clear()
        {
            _count = 0;
            _version++;
        }

        /// <summary>A reset row from the pool; allocates only when the pool grows.</summary>
        public TraderRow Add()
        {
            if (_count == _rows.Length)
            {
                TraderRow[] grown = new TraderRow[_rows.Length * 2];
                Array.Copy(_rows, grown, _rows.Length);
                _rows = grown;
            }
            TraderRow r = _rows[_count];
            if (r == null) { r = new TraderRow(); _rows[_count] = r; }
            r.Reset();
            _count++;
            _version++;
            return r;
        }

        public TraderRow Find(int itemId)
        {
            for (int i = 0; i < _count; i++) if (_rows[i].ItemId == itemId) return _rows[i];
            return null;
        }

        public TraderRow Find(int itemId, int price)
        {
            for (int i = 0; i < _count; i++)
                if (_rows[i].ItemId == itemId && _rows[i].Price == price) return _rows[i];
            return null;
        }

        /// <summary>Category, then price, then name - an insertion sort on the
        /// pool (no comparer object, no allocation; lists are a few hundred rows
        /// and sorted once per data change).</summary>
        public void Sort()
        {
            for (int i = 1; i < _count; i++)
            {
                TraderRow r = _rows[i];
                int j = i - 1;
                while (j >= 0 && Before(r, _rows[j])) { _rows[j + 1] = _rows[j]; j--; }
                _rows[j + 1] = r;
            }
            _version++;
        }

        static bool Before(TraderRow a, TraderRow b)
        {
            if (a.Category != b.Category) return a.Category < b.Category;
            if (a.Price != b.Price) return a.Price < b.Price;
            return string.CompareOrdinal(a.Name ?? "", b.Name ?? "") < 0;
        }

        /// <summary>Rebuilds the view when the category, the search text or the
        /// rows changed; otherwise returns false at once. cat -1 = every category.</summary>
        public bool Refilter(int cat, string search)
        {
            if (cat == _viewCat && string.Equals(search, _viewSearch) && _version == _viewVersion) return false;
            _viewCat = cat;
            _viewSearch = search;
            _viewVersion = _version;
            if (_view.Length < _count)
            {
                int n = _view.Length;
                while (n < _count) n *= 2;
                _view = new int[n];
            }
            _viewCount = 0;
            for (int i = 0; i < _count; i++)
            {
                TraderRow r = _rows[i];
                if (cat >= 0 && r.Category != cat) continue;
                if (!TraderText.Matches(r.Name, search)) continue;
                _view[_viewCount++] = i;
            }
            return true;
        }

        /// <summary>Forces the next Refilter to rebuild.</summary>
        public void Invalidate() { _viewVersion = -1; }
    }

    /// <summary>
    /// Both columns of the trade page and the price comparison between them.
    /// Fill: BeginTrader/AddOffer per offer, BeginInventory/AddSell per
    /// backpack entry of the game's sell list, then Link and Texts.
    /// </summary>
    public sealed class TraderBook
    {
        public readonly TraderList Trader = new TraderList();
        public readonly TraderList Inventory = new TraderList();
        int _textLang = -1;
        int _textVersion = -1;

        public void BeginTrader() { Trader.Clear(); }
        public void BeginInventory() { Inventory.Clear(); }

        public TraderRow AddOffer(int itemId, int price, int category, string name, object icon, int sellBack)
        {
            TraderRow r = Trader.Add();
            r.ItemId = itemId;
            r.Price = price;
            r.Category = category;
            r.Count = 1;
            r.Name = name;
            r.Icon = icon;
            r.Other = sellBack;
            return r;
        }

        /// <summary>One backpack entry of the sell list. Identical items at the
        /// same price share a row (ammo boxes with different fill sell for
        /// different prices and so stay separate lines).</summary>
        public TraderRow AddSell(int itemId, int price, int slot, int category, string name, object icon)
        {
            TraderRow r = Inventory.Find(itemId, price);
            if (r == null)
            {
                r = Inventory.Add();
                r.ItemId = itemId;
                r.Price = price;
                r.Category = category;
                r.Name = name;
                r.Icon = icon;
            }
            r.AddSlot(slot);
            return r;
        }

        /// <summary>The comparison: each offer learns how many the player
        /// carries, each carried item what the trader charges for it.</summary>
        public void Link()
        {
            for (int i = 0; i < Trader.Count; i++) Trader.At(i).Owned = 0;
            for (int i = 0; i < Inventory.Count; i++)
            {
                TraderRow inv = Inventory.At(i);
                TraderRow offer = Trader.Find(inv.ItemId);
                inv.Other = offer == null ? -1 : offer.Price;
                if (offer != null)
                {
                    offer.Owned += inv.Count;
                    // No base sell price read: the real sell list knows one.
                    if (offer.Other < 0) offer.Other = inv.Price;
                }
            }
            _textVersion = -1;
        }

        /// <summary>True when the display texts must be rebuilt (data or language changed).</summary>
        public bool TextsStale(int lang)
        {
            return lang != _textLang || _textVersion != Trader.Version + Inventory.Version * 7919;
        }

        /// <summary>Builds every row's price/sub/count text. Allocates once per
        /// data or language change, never per frame.</summary>
        public void Texts(int lang)
        {
            _textLang = lang;
            _textVersion = Trader.Version + Inventory.Version * 7919;
            for (int i = 0; i < Trader.Count; i++)
            {
                TraderRow r = Trader.At(i);
                r.PriceText = TraderMath.Money(r.Price);
                string sub = TraderCat.Name(r.Category, lang);
                if (r.Other > 0)
                    sub += (lang == 0 ? "  -  выкуп " : "  -  sells back ") + TraderMath.Money(r.Other);
                if (r.Owned > 0)
                    sub += (lang == 0 ? "  -  у вас " : "  -  you have ") + UiNum.Of(r.Owned);
                r.SubText = sub;
                r.CountText = null;
            }
            for (int i = 0; i < Inventory.Count; i++)
            {
                TraderRow r = Inventory.At(i);
                r.PriceText = TraderMath.Money(r.Price);
                string sub = TraderCat.Name(r.Category, lang);
                if (r.Other > 0)
                    sub += (lang == 0 ? "  -  у торговца " : "  -  trader sells at ") + TraderMath.Money(r.Other);
                else
                    sub += lang == 0 ? "  -  торговец не продаёт" : "  -  not on offer here";
                r.SubText = sub;
                r.CountText = r.Count > 1 ? "x" + UiNum.Of(r.Count) : null;
            }
        }
    }

    /// <summary>Money and quantity arithmetic of the trade page.</summary>
    public static class TraderMath
    {
        /// <summary>Most units one click may trade (the order runs one per tick).</summary>
        public const int MaxOrder = 50;

        /// <summary>price x n, clamped instead of overflowing.</summary>
        public static int Total(int price, int n)
        {
            if (price <= 0 || n <= 0) return 0;
            long t = (long)price * n;
            return t > int.MaxValue ? int.MaxValue : (int)t;
        }

        /// <summary>How many of 'want' the money pays for.</summary>
        public static int Affordable(int money, int price, int want)
        {
            if (want <= 0 || money <= 0) return 0;
            if (price <= 0) return want;
            int can = money / price;
            return can < want ? can : want;
        }

        /// <summary>The largest quantity the picker allows: buying is bounded by
        /// money and MaxOrder, selling by the items in the group and MaxOrder.</summary>
        public static int MaxQty(bool buy, int money, int price, int count)
        {
            int m = buy ? Affordable(money, price, MaxOrder) : count;
            if (m > MaxOrder) m = MaxOrder;
            return m < 0 ? 0 : m;
        }

        /// <summary>A picked quantity kept in 1..max (0 when nothing is possible).</summary>
        public static int ClampQty(int q, int max)
        {
            if (max <= 0) return 0;
            if (q < 1) return 1;
            return q > max ? max : q;
        }

        /// <summary>The balance after the trade (clamped at int range).</summary>
        public static int After(int money, bool buy, int total)
        {
            long a = buy ? (long)money - total : (long)money + total;
            if (a > int.MaxValue) return int.MaxValue;
            if (a < int.MinValue) return int.MinValue;
            return (int)a;
        }

        /// <summary>"12 345" - thousands grouped with a space, as the game's
        /// own price labels. Allocates; callers cache the result.</summary>
        public static string Money(int v)
        {
            if (v >= 0 && v < 1000) return UiNum.Of(v);
            bool neg = v < 0;
            long a = neg ? -(long)v : v;
            char[] buf = new char[16];
            int p = buf.Length;
            int digits = 0;
            do
            {
                if (digits > 0 && digits % 3 == 0) buf[--p] = ' ';
                buf[--p] = (char)('0' + (int)(a % 10));
                a /= 10;
                digits++;
            } while (a > 0);
            if (neg) buf[--p] = '-';
            return new string(buf, p, buf.Length - p);
        }

        /// <summary>The native store rule from ItemSlotUI.SlotDetecting: a free
        /// global slot, a free slot in the shown category when the item
        /// belongs to it, and room in the item's own category (48).</summary>
        public static bool CanStore(int freeGlobal, int freeInView, bool sameCategory, bool categoryHasRoom)
        {
            if (freeGlobal < 0) return false;
            if (freeInView < 0 && sameCategory) return false;
            return categoryHasRoom;
        }

        /// <summary>0..1 fill of a capacity bar.</summary>
        public static float Fill(float part, float whole)
        {
            if (whole <= 0f) return 0f;
            float f = part / whole;
            return f < 0f ? 0f : f > 1f ? 1f : f;
        }
    }

    /// <summary>A text rebuilt only when one of up to four integer keys
    /// changes (UiMemo with one key would need packing, and a packed key can
    /// collide - a price text must never show a stale number).</summary>
    public sealed class TraderMemo
    {
        int _a, _b, _c, _d;
        string _text;

        public string Text { get { return _text; } }

        public bool Stale(int a, int b, int c, int d)
        {
            return _text == null || a != _a || b != _b || c != _c || d != _d;
        }

        public string Set(int a, int b, int c, int d, string text)
        {
            _a = a; _b = b; _c = c; _d = d;
            _text = text;
            return text;
        }

        public void Invalidate() { _text = null; }
    }

    /// <summary>The search box match.</summary>
    public static class TraderText
    {
        /// <summary>Empty search matches all; otherwise a case-insensitive
        /// substring (ordinal: Latin and Cyrillic, no culture tables, no allocation).</summary>
        public static bool Matches(string name, string search)
        {
            if (string.IsNullOrEmpty(search)) return true;
            if (name == null) return false;
            return name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>A search string trimmed of spaces; the same object when
        /// nothing had to go (so an unchanged box never refilters).</summary>
        public static string Clean(string s)
        {
            if (s == null) return null;
            int a = 0, b = s.Length;
            while (a < b && s[a] == ' ') a++;
            while (b > a && s[b - 1] == ' ') b--;
            if (a == 0 && b == s.Length) return s;
            return s.Substring(a, b - a);
        }
    }

    /// <summary>
    /// A trade of several units, carried out one unit per step so that each
    /// goes through the game's own BuyItem (and its money update to the
    /// master server) separately and a failure stops the rest.
    /// </summary>
    public sealed class TraderOrder
    {
        public bool Active;
        public bool Buy;
        public int ItemId;
        public int Price;
        public int Category;
        public int Wanted;
        public int Done;
        public int Earned;
        public string Name;
        public int[] Slots = new int[TraderMath.MaxOrder];
        public double NextAt;
        /// <summary>Seconds between two units.</summary>
        public const double Step = 0.12;

        public void Start(bool buy, TraderRow row, int qty, double now)
        {
            Active = qty > 0;
            Buy = buy;
            ItemId = row.ItemId;
            Price = row.Price;
            Category = row.Category;
            Name = row.Name;
            Wanted = qty;
            Done = 0;
            Earned = 0;
            NextAt = now;
            if (!buy)
            {
                int n = qty < row.Count ? qty : row.Count;
                if (n > Slots.Length) n = Slots.Length;
                for (int i = 0; i < n; i++) Slots[i] = row.Slots[i];
                Wanted = n;
                Active = n > 0;
            }
        }

        /// <summary>True when the next unit is due now.</summary>
        public bool Due(double now)
        {
            return Active && Done < Wanted && now >= NextAt;
        }

        /// <summary>The backpack slot of the next unit to sell.</summary>
        public int NextSlot { get { return Buy || Done >= Wanted ? -1 : Slots[Done]; } }

        public void Unit(double now, int moneyDelta)
        {
            Done++;
            Earned += moneyDelta;
            NextAt = now + Step;
            if (Done >= Wanted) Active = false;
        }

        public void Stop() { Active = false; }
    }
}
