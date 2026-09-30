// Next Day: Survival - Revival Toolkit
//
// TraderUi - the trader window rebuilt on the UI kit (task W-UI2). The game's
// NGUI window (UIController + HUD_MarketplaceUI + StorageObject) keeps running
// underneath and keeps doing EVERY trade and every safe move; this window only
// replaces how it looks and how it is operated:
//
//   SAFE   the safe as an icon grid per category with its capacity (48 per
//          category, 336 in all), the backpack beside it (slots, weight);
//          click an item or drag it across to store / take it. Uses the
//          game's own MoveItemToStorage / MoveItemFromStorage with the same
//          free-slot rule the game's drag zone applies, so the master server
//          receives exactly the message it always did.
//   TRADE  two columns - the trader's offer | your backpack - with category
//          filter, search, price comparison (trader price vs. sell-back),
//          a quantity picker and the balance always in view. A unit is
//          bought or sold by selecting the game's own list row and calling
//          HUD_MarketplaceUI.BuyItem - one unit per 0.12 s, so each goes to
//          the master server as the game sends it and a failure stops the rest.
//   MERCS  the Mercenaries page (MercPage, W-UI3): hire cards | my mercs.
//
// How the native window is kept but not seen: the NGUI UI camera's culling
// mask is 0 while this window is open (nothing drawn, nothing hit by NGUI's
// raycasts) and restored when it closes. Any exception, a missing game member
// or [UI] NewTraderWindow = false leaves the game's own window on screen.
//
// Master server: BackendManager drops storage and money messages silently
// while its connection is down (IL: MsgStorageListRequest / MessagePlayerStorage
// return when !isConnected). So this window polls IsConnected at 2 Hz, shows
// "Connection lost - retrying in N s" (UiRetryClock), and holds every safe move
// and trade while the link is down - nothing can vanish into a dead socket.
// The safe list is re-requested with backoff until the answer arrives.
//
// Performance: nothing runs while no trader/safe is open (Tick: one bool
// test; Draw: one bool test). Open: game state is read through IL-emitted
// delegates (no reflection Invoke, no boxing) - money/link/mode at 2 Hz, the
// safe grids at 4 Hz; the trader's offer is read once per page open, one
// category per frame, by capturing the game's own list build
// (AddMarketItemTolistUI is skipped while capturing, so no NGUI rows are
// built for it). Every text is built on change (UiMemo, TraderBook.Texts).
// FrameProf slots TraderUi.Tick / TraderUi.Draw.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression bodies.
// UTF-8 (no BOM), compiled with /codepage:65001 like the rest.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static class TraderUi
    {
        // ------------------------------------------------------------ typed access

        // Game members are bound once to IL-emitted delegates: a field read or
        // a call through them costs a delegate call and allocates nothing
        // (ObscuredInt/ObscuredFloat go through their op_Implicit in the IL).
        delegate object RefOf(object o);
        delegate int IntOf(object o);
        delegate float FloatOf(object o);
        delegate int IntAt(object array, int i);
        delegate float FloatAt(object array, int i);
        delegate void V0(object o);
        delegate int I0(object o);
        delegate bool B0(object o);
        delegate bool B1(object o, int a);
        delegate bool BF(object o, float a);
        delegate int I1(object o, int a);
        delegate int S1(int a);
        delegate void V3(object o, int a, bool b, bool c);
        delegate void VOB(object o, object a, bool b);
        delegate void VO(object o, object a);
        delegate object OOB(object o, object a, bool b);
        delegate bool Switch(object ui, int mode, object storage, bool sound, bool ignoreTime, bool force);
        delegate void MoveTo(object inv, object storage, int from, int toCat, int toSlot, bool addToView);
        delegate void MoveFrom(object inv, object storage, int from, int to);

        static bool _bound, _bindFailed;
        static Type _tUi, _tStorage, _tMarket, _tHud, _tRow, _tInv, _tStats, _tBackend, _tLoc;
        static IntOf _uiMode;
        static RefOf _uiStorage, _uiHud, _uiCam;
        static FieldInfo _fBackendInstance, _fLocInstance, _fLocalPlayer;
        static MethodInfo _mBackendInstance, _mNgsInstance;
        static IntOf _stoLoaded, _stoCategory;
        static RefOf _stoData, _stoCounts;
        static RefOf _hudList;
        static RefOf _rowItem;
        static IntOf _itemId, _itemPrice, _itemSlot, _itemCat;
        static RefOf _itemIcon;
        static RefOf _invBag;
        static RefOf _bagIdArr, _bagIconArr, _bagWeightArr, _bagUsingArr;
        static IntOf _bagMax, _bagBusy;
        static FloatOf _bagCur, _bagMaxW;
        static RefOf _conIdArr, _conIconArr, _conWeightArr;
        static IntAt _obscuredIntAt;
        static FloatAt _obscuredFloatAt;
        static Switch _switch;
        static V3 _showMarketCat, _showStorageCat;
        static VOB _selectRow;
        static V0 _buyItem, _cancelBuy;
        static I0 _money, _bagFreeSlot, _freeStorageSlot, _freeViewSlot;
        static B0 _connected, _checkFreeSlots, _bagEquipped;
        static B1 _sameCategory, _categoryHasRoom;
        static BF _weightOk;
        static I1 _sellPrice;
        static S1 _itemCategory;
        static VO _requestStorage, _playSound;
        static OOB _locText;
        static MoveTo _moveToStorage;
        static MoveFrom _moveFromStorage;

        // ------------------------------------------------------------ settings

        static ConfigEntry<bool> _cfgOn;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgOn = cfg.Bind("UI", "NewTraderWindow", true,
                "The redesigned trader/safe window (Safe + Trade pages, UI kit look). "
                + "Off = the game's own window. Trades and safe moves are the game's own either way.");
        }

        // ------------------------------------------------------------ session state

        const int PageSafe = 0, PageTrade = 1, PageMercs = 2;
        const int SideNone = -1, SideSafe = 0, SideBag = 1, SideTrader = 2, SideInv = 3;
        const int SafeSlots = 48, SafeAll = 336, BagCap = 64;

        static readonly UiWindow WinTrader = new UiWindow("Торговец", "Trader", 1000f, 680f);
        static readonly UiWindow WinStash = new UiWindow("Хранилище", "Storage", 1000f, 680f);
        static UiWindow _win;

        static readonly string[] PagesRu1 = { "Сейф" };
        static readonly string[] PagesEn1 = { "Safe" };
        static readonly string[] PagesRu2 = { "Сейф", "Торговля" };
        static readonly string[] PagesEn2 = { "Safe", "Trade" };
        static readonly string[] PagesRu3 = { "Сейф", "Торговля", "Наёмники" };
        static readonly string[] PagesEn3 = { "Safe", "Trade", "Mercenaries" };
        static readonly string[] SafeCatRu = { "Огнестрел", "Обвесы", "Одежда", "Холодное", "Медицина", "Еда", "Прочее" };
        static readonly string[] SafeCatEn = { "Firearms", "Attach.", "Clothes", "Melee", "Medical", "Food", "Other" };

        static bool _nativeOpen;       // the game's trader/safe window is open (SwitchMarketStorgeUI)
        static int _nativeMode = 2;    // 0 safe, 1 market, 2 closed
        static object _ui;             // UIController
        static Component _storage;     // StorageObject of this session
        static Component _market;      // MarketplaceObject on it (null: a plain safe)
        static bool _active;           // this window owns the session
        static bool _standDown;        // leave this session to the game's own window
        static bool _hidden;           // NGUI camera masked
        static Camera _uiCamera;
        static int _savedMask;
        static int _page;
        static int _pendingPage = -1;
        static bool _mercs;
        static object _hud, _inv, _stats, _backend, _loc;
        static bool _warned;
        static int _drawMiss;          // passes in a row the kit could not draw the window

        // Link to the master server (2 Hz poll) and the safe list request.
        static readonly UiRetryClock _link = new UiRetryClock();
        static readonly UiRetryClock _safeClock = new UiRetryClock();
        static bool _linkUp;
        static bool _safeAnswer;
        static double _nextPoll, _nextSafeRead;
        static int _moneyNow = -1;

        // ------------------------------------------------------------ safe page data

        static readonly int[] _safeIds = new int[SafeSlots];
        static readonly Texture[] _safeIcons = new Texture[SafeSlots];
        static readonly string[] _safeNames = new string[SafeSlots];
        static readonly float[] _safeWeights = new float[SafeSlots];
        static readonly int[] _catCounts = new int[TraderCat.Count];
        static int _safeUsed, _safeTotal, _safeCat = -1;
        static bool _safeLoaded, _countsDirty = true;
        static readonly int[] _bagIds = new int[BagCap];
        static readonly Texture[] _bagIcons = new Texture[BagCap];
        static readonly string[] _bagNames = new string[BagCap];
        static readonly float[] _bagWeights = new float[BagCap];
        static readonly bool[] _bagInUse = new bool[BagCap];
        static int _bagSlots, _bagUsed;
        static float _bagWeight, _bagMaxWeight;
        static bool _bagHas;
        static int _pendingStore = -1, _pendingTake = -1, _pendingCat = -1;
        static readonly UiScroll _bagScroll = new UiScroll();

        // Click / drag between the grids.
        static int _press = SideNone, _pressIdx = -1;
        static bool _dragging;
        static Vector2 _pressAt;
        static Rect _safeGridScreen, _bagGridScreen;

        // ------------------------------------------------------------ trade page data

        static readonly TraderBook _book = new TraderBook();
        static int _capMask;           // categories still to read, bit per category 0..7
        static int _capture = -1;      // category being read right now (AddMarketItemTolistUI prefix)
        static bool _offerRead;        // the trader side was read at least once this session
        static int _nativeCat = -1;    // category the game's (hidden) list shows with real rows
        static int _rowBase, _rowBaseFrame = -1;   // rows below _rowBase are doomed this frame (Destroy is deferred)
        static int _filter;            // filter bar index, 0 = All
        static string _search = "";
        static string _searchClean = "";
        static readonly UiScroll _traderScroll = new UiScroll();
        static readonly UiScroll _invScroll = new UiScroll();
        static int _selSide = SideNone, _selId, _selPrice;
        static TraderRow _sel;
        static int _qty = 1;
        static readonly TraderOrder _order = new TraderOrder();
        static bool _orderQueued;
        static bool _orderBuy;
        static int _orderQty;
        static readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        static readonly Dictionary<int, int> _cats = new Dictionary<int, int>();

        // Texts rebuilt on change only.
        static readonly UiMemo _moneyText = new UiMemo();
        static readonly TraderMemo _safeCapText = new TraderMemo();
        static readonly TraderMemo _bagCapText = new TraderMemo();
        static readonly UiMemo _traderHead = new UiMemo();
        static readonly UiMemo _invHead = new UiMemo();
        static readonly TraderMemo _actionText = new TraderMemo();
        static readonly TraderMemo _afterText = new TraderMemo();
        static readonly TraderMemo _unitText = new TraderMemo();
        static readonly TraderMemo _orderText = new TraderMemo();

        /// <summary>True while this window owns the trader session (MercUi then
        /// draws its tab inside it instead of over the game's tab buttons).</summary>
        internal static bool Owns { get { return _active; } }

        /// <summary>W-UI3: the balance read at 2 Hz for the Mercenaries page
        /// (-1 while closed or unknown).</summary>
        internal static int MoneyNow { get { return _active ? _moneyNow : -1; } }

        /// <summary>True while the search box has the keyboard (game keys are held).</summary>
        static bool _typing;

        // ------------------------------------------------------------ install

        internal static void Install(Harmony harmony)
        {
            try
            {
                _tUi = RevivalPlugin.TypeByName("UIController");
                _tStorage = RevivalPlugin.TypeByName("StorageObject");
                _tHud = RevivalPlugin.TypeByName("HUD_MarketplaceUI");
                Type input = RevivalPlugin.TypeByName("MyInputManager");
                MethodInfo sw = _tUi == null ? null : AccessTools.Method(_tUi, "SwitchMarketStorgeUI", null, null);
                MethodInfo add = _tHud == null ? null : AccessTools.Method(_tHud, "AddMarketItemTolistUI", null, null);
                MethodInfo load = _tStorage == null ? null : AccessTools.Method(_tStorage, "SetBackendStorageData", null, null);
                if (sw == null || add == null || load == null)
                {
                    RevivalPlugin.L.LogWarning("TraderUi: trader window members missing - the game's own window stays.");
                    return;
                }
                harmony.Patch(sw, null, new HarmonyMethod(typeof(TraderUi).GetMethod("SwitchPostfix")), null, null, null);
                harmony.Patch(add, new HarmonyMethod(typeof(TraderUi).GetMethod("AddRowPrefix")), null, null, null, null);
                harmony.Patch(load, null, new HarmonyMethod(typeof(TraderUi).GetMethod("LoadedPostfix")), null, null, null);
                int keys = 0;
                if (input != null)
                {
                    foreach (string name in new string[] { "ButtonDown", "Button", "ButtonUp" })
                    {
                        MethodInfo m = AccessTools.Method(input, name, null, null);
                        if (m == null || m.ReturnType != typeof(bool)) continue;
                        harmony.Patch(m, new HarmonyMethod(typeof(TraderUi).GetMethod("KeyPrefix")), null, null, null, null);
                        keys++;
                    }
                }
                RevivalPlugin.L.LogInfo("TraderUi: trader window hooks installed (" + keys + " key guards).");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TraderUi: install failed, the game's own window stays: " + ex.Message); }
        }

        /// <summary>Postfix UIController.SwitchMarketStorgeUI: every open,
        /// page switch and close of the game's trader/safe window passes here.</summary>
        public static void SwitchPostfix(object __instance, object __0, object __1, bool __result)
        {
            if (!__result) return;
            try
            {
                int mode = System.Convert.ToInt32(__0);
                _ui = __instance;
                _nativeMode = mode;
                if (mode == 0 || mode == 1)
                {
                    Component st = __1 as Component;
                    if (st != null && !ReferenceEquals(st, _storage)) { if (_active) Deactivate(); _storage = st; _standDown = false; }
                    _nativeOpen = true;
                    if (_active) NativeModeChanged(mode);
                }
                else
                {
                    _nativeOpen = false;
                    _standDown = false;
                }
            }
            catch (Exception ex) { Fail("switch", ex); }
        }

        /// <summary>Prefix HUD_MarketplaceUI.AddMarketItemTolistUI: while the
        /// window reads a category the item is recorded and no NGUI row is built.</summary>
        public static bool AddRowPrefix(object __0)
        {
            if (_capture < 0) return true;
            try { Capture(__0); }
            catch (Exception ex) { Fail("capture", ex); }
            return false;
        }

        /// <summary>Postfix StorageObject.SetBackendStorageData: the safe list arrived.</summary>
        public static void LoadedPostfix(object __instance)
        {
            if (_active && ReferenceEquals(__instance, _storage)) _safeAnswer = true;
        }

        /// <summary>Prefix MyInputManager.Button*: no game key while the search box types.</summary>
        public static bool KeyPrefix(ref bool __result)
        {
            if (!_typing) return true;
            __result = false;
            return false;
        }

        // ------------------------------------------------------------ binding

        static bool Bind()
        {
            if (_bound) return true;
            if (_bindFailed) return false;
            try
            {
                _tMarket = RevivalPlugin.TypeByName("MarketplaceObject");
                _tRow = RevivalPlugin.TypeByName("MarketItemUI");
                _tInv = RevivalPlugin.TypeByName("PlayerInventoryManager");
                _tStats = RevivalPlugin.TypeByName("PlayerStatisticsManager");
                _tBackend = RevivalPlugin.TypeByName("BackendManager");
                _tLoc = RevivalPlugin.TypeByName("LocalizationManager");
                Type item = RevivalPlugin.TypeByName("MarketItem");
                Type bag = RevivalPlugin.TypeByName("BackpackData");
                Type con = RevivalPlugin.TypeByName("ContainerData");
                Type ngs = RevivalPlugin.TypeByName("NetworkGameServer");
                if (_tMarket == null || _tRow == null || _tInv == null || _tStats == null || _tBackend == null
                    || _tLoc == null || item == null || bag == null || con == null || ngs == null)
                    throw new MissingMemberException("a trader type");

                _uiMode = (IntOf)Reader(_tUi, "currentSwitchMarketStorageUI", typeof(int), typeof(IntOf));
                _uiStorage = (RefOf)Reader(_tUi, "CurrentMarketAndStorageObject", typeof(object), typeof(RefOf));
                _uiHud = (RefOf)Reader(_tUi, "HUD_MarketplaceUI_Script", typeof(object), typeof(RefOf));
                _uiCam = (RefOf)Reader(_tUi, "UICam", typeof(object), typeof(RefOf));
                _stoLoaded = (IntOf)Reader(_tStorage, "IsLoadedStorageData", typeof(int), typeof(IntOf));
                _stoCategory = (IntOf)Reader(_tStorage, "CurrentCategory", typeof(int), typeof(IntOf));
                _stoData = (RefOf)Reader(_tStorage, "_storageData", typeof(object), typeof(RefOf));
                _stoCounts = (RefOf)Reader(_tStorage, "StorageCategoriesItemsCount", typeof(object), typeof(RefOf));
                _hudList = (RefOf)Reader(_tHud, "MarketItemsListUI", typeof(object), typeof(RefOf));
                _rowItem = (RefOf)Reader(_tRow, "Item", typeof(object), typeof(RefOf));
                _itemId = (IntOf)Reader(item, "ItemID", typeof(int), typeof(IntOf));
                _itemPrice = (IntOf)Reader(item, "Price", typeof(int), typeof(IntOf));
                _itemSlot = (IntOf)Reader(item, "BackpackSlotId", typeof(int), typeof(IntOf));
                _itemCat = (IntOf)Reader(item, "Category", typeof(int), typeof(IntOf));
                _itemIcon = (RefOf)Reader(item, "Icon", typeof(object), typeof(RefOf));
                _invBag = (RefOf)Reader(_tInv, "_backpackData", typeof(object), typeof(RefOf));
                _bagIdArr = (RefOf)Reader(bag, "ItemID", typeof(object), typeof(RefOf));
                _bagIconArr = (RefOf)Reader(bag, "ItemIcon", typeof(object), typeof(RefOf));
                _bagWeightArr = (RefOf)Reader(bag, "ItemWeight", typeof(object), typeof(RefOf));
                _bagUsingArr = (RefOf)Reader(bag, "ItemOnUsing", typeof(object), typeof(RefOf));
                _bagMax = (IntOf)Reader(bag, "MaxSlots", typeof(int), typeof(IntOf));
                _bagBusy = (IntOf)Reader(bag, "BusySlots", typeof(int), typeof(IntOf));
                _bagCur = (FloatOf)Reader(bag, "CurrentWeight", typeof(float), typeof(FloatOf));
                _bagMaxW = (FloatOf)Reader(bag, "MaxWeight", typeof(float), typeof(FloatOf));
                _conIdArr = (RefOf)Reader(con, "ItemID", typeof(object), typeof(RefOf));
                _conIconArr = (RefOf)Reader(con, "ItemIcon", typeof(object), typeof(RefOf));
                _conWeightArr = (RefOf)Reader(con, "ItemWeight", typeof(object), typeof(RefOf));
                FieldInfo ids = AccessTools.Field(bag, "ItemID");
                FieldInfo ws = AccessTools.Field(bag, "ItemWeight");
                _obscuredIntAt = (IntAt)Element(ids.FieldType.GetElementType(), typeof(int), typeof(IntAt));
                _obscuredFloatAt = (FloatAt)Element(ws.FieldType.GetElementType(), typeof(float), typeof(FloatAt));

                _switch = (Switch)Call(_tUi, "SwitchMarketStorgeUI", typeof(Switch));
                _showMarketCat = (V3)Call(_tMarket, "ShowItemsMarketCategory", typeof(V3));
                _showStorageCat = (V3)Call(_tStorage, "ShowItemsStorageCategory", typeof(V3));
                _selectRow = (VOB)Call(_tHud, "SelectMarketItemInfo", typeof(VOB));
                _buyItem = (V0)Call(_tHud, "BuyItem", typeof(V0));
                _cancelBuy = (V0)Call(_tHud, "CancelBuyItem", typeof(V0));
                _money = (I0)Call(_tStats, "GetPlayerMoney", typeof(I0));
                _bagFreeSlot = (I0)Call(_tInv, "GetBackpackFreeSlot", typeof(I0));
                _freeStorageSlot = (I0)Call(_tStorage, "GetFreeStorageSlotId", typeof(I0));
                _freeViewSlot = (I0)Call(_tStorage, "GetFreeCategoryStorageSlotId", typeof(I0));
                _connected = (B0)Call(_tBackend, "IsConnected", typeof(B0));
                _checkFreeSlots = (B0)CallExact(_tInv, "CheckFreeSlots", Type.EmptyTypes, typeof(B0));
                _bagEquipped = (B0)Call(bag, "isEquipedBackpack", typeof(B0));
                _sameCategory = (B1)Call(_tStorage, "IsSameStorageCategoryItem", typeof(B1));
                _categoryHasRoom = (B1)Call(_tStorage, "IsCheckFreeSlotStorageCategory", typeof(B1));
                _weightOk = (BF)Call(_tInv, "CheckMaxBackpackWeight", typeof(BF));
                _sellPrice = (I1)Call(_tMarket, "GenerateSellItemPrice", typeof(I1));
                _itemCategory = (S1)Call(_tStorage, "GetItemStorageCategory", typeof(S1));
                _requestStorage = (VO)Call(_tBackend, "MsgStorageListRequest", typeof(VO));
                _playSound = (VO)Call(_tUi, "PlayUISound", typeof(VO));
                _locText = (OOB)Call(_tLoc, "GetLocalizationText", typeof(OOB));
                _moveToStorage = (MoveTo)Call(_tInv, "MoveItemToStorage", typeof(MoveTo));
                _moveFromStorage = (MoveFrom)Call(_tInv, "MoveItemFromStorage", typeof(MoveFrom));

                _mBackendInstance = AccessTools.PropertyGetter(_tBackend, "Instance");
                _fBackendInstance = _mBackendInstance == null ? AccessTools.Field(_tBackend, "<Instance>k__BackingField") : null;
                _fLocInstance = AccessTools.Field(_tLoc, "Instance");
                _mNgsInstance = AccessTools.PropertyGetter(ngs, "Instance");
                _fLocalPlayer = AccessTools.Field(ngs, "localPlayer");
                if ((_fBackendInstance == null && _mBackendInstance == null) || _fLocInstance == null
                    || _mNgsInstance == null || _fLocalPlayer == null)
                    throw new MissingMemberException("an instance accessor");
                _bound = true;
                RevivalPlugin.L.LogInfo("TraderUi: game members bound - the new trader window is active.");
                return true;
            }
            catch (Exception ex)
            {
                _bindFailed = true;
                RevivalPlugin.L.LogWarning("TraderUi: cannot bind the trader members, the game's own window stays: " + ex.Message);
                return false;
            }
        }

        /// <summary>A field read compiled to IL: reference fields as object,
        /// int/enum/bool/ObscuredInt as int, float/ObscuredFloat as float.</summary>
        static Delegate Reader(Type type, string name, Type result, Type signature)
        {
            FieldInfo f = type == null ? null : AccessTools.Field(type, name);
            if (f == null) throw new MissingFieldException(type == null ? "?" : type.Name, name);
            DynamicMethod dm = new DynamicMethod("ndr_trader_" + name, result, new Type[] { typeof(object) }, f.DeclaringType, true);
            ILGenerator il = dm.GetILGenerator();
            if (f.IsStatic) il.Emit(OpCodes.Ldsfld, f);
            else
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, f.DeclaringType);
                il.Emit(OpCodes.Ldfld, f);
            }
            ConvertTo(il, f.FieldType, result, name);
            il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(signature);
        }

        /// <summary>An array element read compiled to IL (ObscuredInt[] -> int ...).</summary>
        static Delegate Element(Type elem, Type result, Type signature)
        {
            DynamicMethod dm = new DynamicMethod("ndr_trader_elem_" + elem.Name, result,
                new Type[] { typeof(object), typeof(int) }, typeof(TraderUi), true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, elem.MakeArrayType());
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldelem, elem);
            ConvertTo(il, elem, result, elem.Name);
            il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(signature);
        }

        static void ConvertTo(ILGenerator il, Type from, Type to, string what)
        {
            if (from == to) return;
            if (to == typeof(object))
            {
                if (from.IsValueType) throw new InvalidOperationException("value field read as object: " + what);
                return;
            }
            if (to == typeof(int) && (from == typeof(bool) || (from.IsEnum && Enum.GetUnderlyingType(from) == typeof(int))))
                return;
            MethodInfo[] ms = from.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != to) continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == from) { il.Emit(OpCodes.Call, ms[i]); return; }
            }
            throw new InvalidOperationException("no conversion " + from.Name + " -> " + to.Name + " for " + what);
        }

        /// <summary>A method call compiled to IL: the instance (if any) comes in
        /// as object, reference parameters as object, enums as int.</summary>
        static Delegate Call(Type type, string name, Type signature)
        {
            MethodInfo inv = signature.GetMethod("Invoke");
            int want = inv.GetParameters().Length;
            MethodInfo[] all = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            for (int i = 0; i < all.Length; i++)
            {
                MethodInfo m = all[i];
                if (m.Name != name) continue;
                if (m.GetParameters().Length + (m.IsStatic ? 0 : 1) != want) continue;
                return Emit(m, signature);
            }
            throw new MissingMethodException(type.Name, name);
        }

        static Delegate CallExact(Type type, string name, Type[] args, Type signature)
        {
            MethodInfo m = AccessTools.Method(type, name, args, null);
            if (m == null) throw new MissingMethodException(type.Name, name);
            return Emit(m, signature);
        }

        static Delegate Emit(MethodInfo m, Type signature)
        {
            MethodInfo inv = signature.GetMethod("Invoke");
            ParameterInfo[] dp = inv.GetParameters();
            ParameterInfo[] mp = m.GetParameters();
            Type[] pt = new Type[dp.Length];
            for (int i = 0; i < dp.Length; i++) pt[i] = dp[i].ParameterType;
            DynamicMethod dm = new DynamicMethod("ndr_trader_" + m.Name, inv.ReturnType, pt, m.DeclaringType, true);
            ILGenerator il = dm.GetILGenerator();
            int off = 0;
            if (!m.IsStatic)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, m.DeclaringType);
                off = 1;
            }
            for (int j = 0; j < mp.Length; j++)
            {
                Type need = mp[j].ParameterType;
                Type have = pt[j + off];
                il.Emit(OpCodes.Ldarg, (short)(j + off));
                if (need == have) continue;
                if (have == typeof(object) && !need.IsValueType) { il.Emit(OpCodes.Castclass, need); continue; }
                if (have == typeof(int) && need.IsEnum && Enum.GetUnderlyingType(need) == typeof(int)) continue;
                throw new InvalidOperationException(m.Name + ": parameter " + j + " is " + need.Name);
            }
            il.Emit(m.IsStatic ? OpCodes.Call : OpCodes.Callvirt, m);
            Type r = m.ReturnType, d = inv.ReturnType;
            if (d == typeof(void)) { if (r != typeof(void)) il.Emit(OpCodes.Pop); }
            else if (r != d && !(d == typeof(int) && r.IsEnum) && !(d == typeof(object) && !r.IsValueType))
                throw new InvalidOperationException(m.Name + ": returns " + r.Name);
            il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(signature);
        }

        // ------------------------------------------------------------ frame

        /// <summary>Update. Idle: one bool test.</summary>
        internal static void Tick()
        {
            if (!_nativeOpen && !_active) return;
            try { TickOpen(); }
            catch (Exception ex) { Fail("tick", ex); }
        }

        static void TickOpen()
        {
            if (!_nativeOpen || _standDown) { if (_active) Deactivate(); return; }
            if (!_active) { Activate(); if (!_active) return; }
            double now = Time.realtimeSinceStartup;
            if (!_win.Open) { UserClosed(); return; }
            if (now >= _nextPoll) { _nextPoll = now + 0.5; if (!Poll(now)) return; }
            if (_link.Step(now)) { if (_linkUp) _link.Succeed(now); else _link.Fail(now); }
            if (_pendingPage >= 0) { int p = _pendingPage; _pendingPage = -1; GoPage(p); }
            if (_page == PageSafe) SafeTick(now);
            else if (_page == PageTrade) TradeTick(now);
            else if (_page == PageMercs) { FrameProf.S(FrameProf.S_MercPageT); MercPage.Tick(now); FrameProf.E(FrameProf.S_MercPageT); }
        }

        static void Activate()
        {
            if (_cfgOn != null && !_cfgOn.Value) { _standDown = true; return; }
            if (!Bind() || _storage == null || _ui == null) { _standDown = true; return; }
            object hud = _uiHud(_ui);
            object ngs = _mNgsInstance.Invoke(null, null);
            GameObject player = ngs == null ? null : _fLocalPlayer.GetValue(ngs) as GameObject;
            if (hud == null || player == null) { _standDown = true; return; }
            _hud = hud;
            _inv = player.GetComponent(_tInv);
            _stats = player.GetComponent(_tStats);
            _backend = _mBackendInstance != null ? _mBackendInstance.Invoke(null, null) : _fBackendInstance.GetValue(null);
            _loc = _fLocInstance.GetValue(null);
            if (_inv == null || _stats == null || _backend == null) { _standDown = true; return; }
            _market = _storage.GetComponent(_tMarket);
            _mercs = _market != null && MercUi.EmbedFor(_storage);
            _win = _market != null ? WinTrader : WinStash;
            UiKit.Open(_win);
            _active = true;
            _drawMiss = 0;
            Hide(true);
            if (!_hidden)
            {
                // No UI camera to mask: two windows on top of each other would
                // take clicks twice - the game's own window it is.
                Log("the game's UI camera was not found - its own trader window stays.");
                _standDown = true;
                Deactivate();
                return;
            }
            _link.Reset();
            _linkUp = _connected(_backend);
            if (_linkUp) _link.Succeed(Time.realtimeSinceStartup); else _link.Fail(Time.realtimeSinceStartup);
            _safeClock.Reset();
            _nextPoll = 0.0;
            _nextSafeRead = 0.0;
            _moneyNow = _money(_stats);
            _offerRead = false;
            _capMask = 0;
            _capture = -1;
            _nativeCat = -1;
            _book.BeginTrader();
            _book.BeginInventory();
            _sel = null;
            _selSide = SideNone;
            _order.Stop();
            _orderQueued = false;
            _press = SideNone;
            _safeCat = -1;
            _countsDirty = true;
            _pendingStore = _pendingTake = _pendingCat = _pendingPage = -1;
            _page = _nativeMode == 1 ? PageTrade : PageSafe;
            NativeModeChanged(_nativeMode);
        }

        /// <summary>The game changed its page (or we asked it to).</summary>
        static void NativeModeChanged(int mode)
        {
            if (mode == 0)
            {
                if (_page != PageMercs) _page = PageSafe;
                ClearSafe();
                _safeAnswer = false;
                _safeClock.Reset();
                _safeClock.Start(Time.realtimeSinceStartup);
                _nextSafeRead = 0.0;
            }
            else if (mode == 1)
            {
                if (_page != PageMercs) _page = PageTrade;
                _nativeCat = 0;          // InteractWithMarket shows category 0 with real rows
                _capMask = 0xFF;         // read all seven categories and the sell list
                _offerRead = false;
            }
        }

        static void Deactivate()
        {
            _active = false;
            _typing = false;
            _capture = -1;
            _capMask = 0;
            _order.Stop();
            _orderQueued = false;
            _press = SideNone;
            _dragging = false;
            Hide(false);
            if (_win != null && _win.Open) UiKit.Close(_win);
            _hud = _inv = _stats = _backend = _loc = null;
            _market = null;
            _sel = null;
        }

        /// <summary>Esc or the close button: close the game's window too.</summary>
        static void UserClosed()
        {
            object ui = _ui;
            bool closed = false;
            try { closed = ui != null && _switch(ui, 2, null, true, true, false); }
            catch (Exception ex) { Fail("close", ex); }
            Deactivate();
            if (!closed && _nativeOpen) _standDown = true;   // the game refused: its own window stays usable
        }

        static void Hide(bool hide)
        {
            try
            {
                if (hide && !_hidden)
                {
                    _uiCamera = _ui == null ? null : _uiCam(_ui) as Camera;
                    if (_uiCamera == null) return;
                    _savedMask = _uiCamera.cullingMask;
                    _uiCamera.cullingMask = 0;
                    _hidden = true;
                }
                else if (!hide && _hidden)
                {
                    if (_uiCamera != null) _uiCamera.cullingMask = _savedMask;
                    _hidden = false;
                    _uiCamera = null;
                }
            }
            catch (Exception ex) { Log("ngui camera: " + ex.Message); }
        }

        /// <summary>2 Hz: is the game's window still open on our safe, money, link.</summary>
        static bool Poll(double now)
        {
            UnityEngine.Object live = _ui as UnityEngine.Object;
            if (_storage == null || live == null) { _nativeOpen = false; Deactivate(); return false; }
            int mode = _uiMode(_ui);
            if (mode != 0 && mode != 1) { _nativeOpen = false; Deactivate(); return false; }
            if (mode != _nativeMode) { _nativeMode = mode; NativeModeChanged(mode); }
            if (_stats != null) _moneyNow = _money(_stats);
            bool up = _backend != null && _connected(_backend);
            if (up != _linkUp)
            {
                _linkUp = up;
                if (up) _link.Succeed(now); else _link.Fail(now);
            }
            return true;
        }

        static void GoPage(int page)
        {
            if (page == _page) return;
            if (page == PageTrade && _market == null) return;
            if (page == PageMercs && !_mercs) return;
            _press = SideNone;
            _dragging = false;
            if (page == PageMercs) { _page = PageMercs; Mercs.RequestRoster(0f); return; }
            int mode = page == PageTrade ? 1 : 0;
            _page = page;
            if (_nativeMode == mode) return;
            if (!_switch(_ui, mode, _storage, true, true, false))
                UiKit.Toast(Loc.T("Торговец занят - попробуйте ещё раз", "The trader is busy - try again"), UiTone.Warning);
        }

        // ------------------------------------------------------------ safe page

        static void ClearSafe()
        {
            for (int i = 0; i < SafeSlots; i++) { _safeIds[i] = 0; _safeIcons[i] = null; _safeNames[i] = null; }
            _safeUsed = 0;
            _safeLoaded = false;
            _countsDirty = true;
        }

        static void SafeTick(double now)
        {
            if (_nativeMode != 0) return;
            if (_safeAnswer) { _safeAnswer = false; _safeClock.Succeed(now); _nextSafeRead = 0.0; _countsDirty = true; }
            if (!_safeLoaded && _safeClock.Step(now))
            {
                if (_linkUp) _requestStorage(_backend, _storage);
                else _safeClock.Fail(now);
            }
            if (_pendingCat >= 0) { int c = _pendingCat; _pendingCat = -1; if (c != _safeCat) { _showStorageCat(_storage, c, true, true); _nextSafeRead = 0.0; } }
            if (_pendingStore >= 0) { int s = _pendingStore; _pendingStore = -1; Store(s); _nextSafeRead = 0.0; }
            if (_pendingTake >= 0) { int s = _pendingTake; _pendingTake = -1; Take(s); _nextSafeRead = 0.0; }
            if (now >= _nextSafeRead) { _nextSafeRead = now + 0.25; ReadSafe(); ReadBag(); }
        }

        /// <summary>4 Hz: the shown category of the safe. No allocation unless a slot changed.</summary>
        static void ReadSafe()
        {
            bool loaded = _stoLoaded(_storage) != 0;
            if (loaded && !_safeLoaded && _safeClock.State != UiRetryClock.Online) _safeClock.Succeed(Time.realtimeSinceStartup);
            _safeLoaded = loaded;
            if (!loaded) return;
            int cat = _stoCategory(_storage);
            if (cat != _safeCat) { _safeCat = cat; _countsDirty = true; }
            object data = _stoData(_storage);
            if (data == null) return;
            object ids = _conIdArr(data);
            Texture[] icons = _conIconArr(data) as Texture[];
            object ws = _conWeightArr(data);
            Array idArr = ids as Array;
            int n = idArr == null ? 0 : Mathf.Min(idArr.Length, SafeSlots);
            int used = 0;
            for (int i = 0; i < SafeSlots; i++)
            {
                int id = i < n ? _obscuredIntAt(ids, i) : 0;
                if (id != _safeIds[i])
                {
                    _safeIds[i] = id;
                    _safeNames[i] = id == 0 ? null : Name(id);
                    _countsDirty = true;
                }
                _safeIcons[i] = id == 0 || icons == null || i >= icons.Length ? null : icons[i];
                _safeWeights[i] = id == 0 || ws == null ? 0f : _obscuredFloatAt(ws, i);
                if (id != 0) used++;
            }
            _safeUsed = used;
            if (_countsDirty) ReadCounts();
        }

        /// <summary>The per-category counts (a Dictionary keyed by the game's
        /// enum). Only after a change of the safe - it boxes.</summary>
        static void ReadCounts()
        {
            _countsDirty = false;
            for (int i = 0; i < TraderCat.Count; i++) _catCounts[i] = 0;
            IDictionary d = _stoCounts(_storage) as IDictionary;
            int total = 0;
            if (d != null)
            {
                IDictionaryEnumerator en = d.GetEnumerator();
                while (en.MoveNext())
                {
                    int k = System.Convert.ToInt32(en.Key);
                    int v = System.Convert.ToInt32(en.Value);
                    if (k >= 0 && k < TraderCat.Count) _catCounts[k] = v;
                    total += v;
                }
            }
            if (_safeCat >= 0 && _safeCat < TraderCat.Count && _catCounts[_safeCat] < _safeUsed) _catCounts[_safeCat] = _safeUsed;
            if (total < _safeUsed) total = _safeUsed;
            _safeTotal = total;
        }

        /// <summary>4 Hz: the backpack slots, weight and use flags.</summary>
        static void ReadBag()
        {
            object bag = _invBag(_inv);
            _bagHas = bag != null;
            if (bag == null) { _bagSlots = 0; return; }
            object ids = _bagIdArr(bag);
            Texture[] icons = _bagIconArr(bag) as Texture[];
            object ws = _bagWeightArr(bag);
            bool[] inUse = _bagUsingArr(bag) as bool[];
            Array idArr = ids as Array;
            int max = _bagMax(bag);
            int n = idArr == null ? 0 : idArr.Length;
            if (max > 0 && max < n) n = max;
            if (n > BagCap) n = BagCap;
            _bagSlots = n;
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                int id = _obscuredIntAt(ids, i);
                if (id != _bagIds[i]) { _bagIds[i] = id; _bagNames[i] = id == 0 ? null : Name(id); }
                _bagIcons[i] = id == 0 || icons == null || i >= icons.Length ? null : icons[i];
                _bagWeights[i] = id == 0 || ws == null ? 0f : _obscuredFloatAt(ws, i);
                _bagInUse[i] = id != 0 && inUse != null && i < inUse.Length && inUse[i];
                if (id != 0) used++;
            }
            _bagUsed = used;
            _bagWeight = _bagCur(bag);
            _bagMaxWeight = _bagMaxW(bag);
        }

        /// <summary>Backpack slot -> safe: the game's drag-zone rule, then its MoveItemToStorage.</summary>
        static void Store(int slot)
        {
            if (slot < 0 || slot >= _bagSlots || _bagIds[slot] == 0) return;
            if (!Ready(true)) return;
            if (_bagInUse[slot]) { Error(Loc.T("Этот предмет сейчас используется.", "That item is in use right now.")); return; }
            int id = _bagIds[slot];
            int free = _freeStorageSlot(_storage);
            int view = _freeViewSlot(_storage);
            bool same = _sameCategory(_storage, id);
            bool room = _categoryHasRoom(_storage, id);
            if (!TraderMath.CanStore(free, view, same, room))
            {
                Error(Loc.T("В сейфе нет места для этой категории (48 на категорию).",
                            "The safe is full for this category (48 per category)."));
                return;
            }
            _moveToStorage(_inv, _storage, slot, view, free, same);
            _playSound(_ui, "Click");
            _countsDirty = true;
            // Stored under another category than the one shown: say where it went.
            if (!same)
                UiKit.Toast(Loc.T("Убрано в категорию: ", "Stored under: ") + TraderCat.Name(CategoryOf(id), Loc.Lang() == 0 ? 0 : 1), UiTone.Info);
        }

        /// <summary>Safe slot -> backpack: the game's drop rule, then its MoveItemFromStorage.</summary>
        static void Take(int index)
        {
            if (index < 0 || index >= SafeSlots || _safeIds[index] == 0) return;
            if (!Ready(true)) return;
            object bag = _invBag(_inv);
            if (bag == null || !_bagEquipped(bag))
            {
                Error(Loc.T("Сначала наденьте рюкзак.", "Put on a backpack first."));
                return;
            }
            if (!_checkFreeSlots(_inv) || !_weightOk(_inv, _safeWeights[index]))
            {
                Error(Loc.T("В рюкзаке нет места или он слишком тяжёлый.", "No room in the backpack, or it would be too heavy."));
                return;
            }
            int to = _bagFreeSlot(_inv);
            if (to < 0) { Error(Loc.T("В рюкзаке нет свободной ячейки.", "No free backpack slot.")); return; }
            _moveFromStorage(_inv, _storage, index, to);
            _playSound(_ui, "Click");
            _countsDirty = true;
        }

        /// <summary>A move or trade needs the safe loaded (safe page) and the master server.</summary>
        static bool Ready(bool safe)
        {
            if (!_linkUp)
            {
                Error(Loc.T("Нет связи с мастер-сервером - ничего не перемещаем, пока она не вернётся.",
                            "No master server connection - nothing moves until it is back."));
                return false;
            }
            if (safe && !_safeLoaded)
            {
                Error(Loc.T("Сейф ещё загружается.", "The safe is still loading."));
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------ trade page

        static void TradeTick(double now)
        {
            if (_nativeMode != 1 || _market == null) return;
            if (_orderQueued && !_order.Active && _capMask == 0)
            {
                _orderQueued = false;
                StartOrder(now);
            }
            if (_capMask != 0 && !_order.Active)
            {
                int cat = 0;
                while (cat < 8 && (_capMask & (1 << cat)) == 0) cat++;
                _capMask &= ~(1 << cat);
                CaptureCategory(cat);
                if (_capMask == 0) FinishCapture();
                return;
            }
            if (_order.Due(now)) OrderUnit(now);
        }

        /// <summary>Reads one category through the game's own list build; the
        /// AddMarketItemTolistUI prefix records the items instead of building rows.</summary>
        static void CaptureCategory(int cat)
        {
            if (cat == 0) _book.BeginTrader();
            if (cat == TraderCat.Sell) _book.BeginInventory();
            _capture = cat;
            try { _showMarketCat(_market, cat, false, true); }
            finally { _capture = -1; }
            _nativeCat = -1;   // the game's list is empty now: rebuilt before a trade
        }

        static void Capture(object item)
        {
            if (item == null) return;
            int id = _itemId(item);
            int price = _itemPrice(item);
            Texture icon = _itemIcon(item) as Texture;
            if (_capture == TraderCat.Sell)
            {
                int slot = _itemSlot(item);
                _book.AddSell(id, price, slot, CategoryOf(id), Name(id), icon);
            }
            else
            {
                int back = _sellPrice(_market, id);
                _book.AddOffer(id, price, _capture, Name(id), icon, back > 0 ? back : -1);
            }
        }

        static void FinishCapture()
        {
            _book.Trader.Sort();
            _book.Inventory.Sort();
            _book.Link();
            _offerRead = true;
            Reselect();
        }

        static void Reselect()
        {
            if (_selSide == SideNone) { _sel = null; return; }
            TraderRow r = _selSide == SideTrader ? _book.Trader.Find(_selId) : _book.Inventory.Find(_selId, _selPrice);
            if (r == null && _selSide == SideInv) r = _book.Inventory.Find(_selId);
            _sel = r;
            if (r == null) _selSide = SideNone;
            else _selPrice = r.Price;
        }

        static void Select(int side, TraderRow r)
        {
            if (r == null) return;
            if (_sel != r) _qty = 1;
            _sel = r;
            _selSide = side;
            _selId = r.ItemId;
            _selPrice = r.Price;
        }

        static void StartOrder(double now)
        {
            if (_sel == null || _orderQty <= 0) return;
            if (!Ready(false)) return;
            _order.Start(_orderBuy, _sel, _orderQty, now);
        }

        /// <summary>One unit through the game's own BuyItem (buy and sell alike).</summary>
        static void OrderUnit(double now)
        {
            if (!_linkUp)
            {
                StopOrder(Loc.T("Связь с мастер-сервером потеряна - торговля остановлена.",
                                "Master server connection lost - the trade stopped."));
                return;
            }
            bool buy = _order.Buy;
            int cat = buy ? _order.Category : TraderCat.Sell;
            if (_nativeCat != cat) Rebuild(cat);
            object row = NativeRow(buy ? _order.ItemId : -1, buy ? -1 : _order.NextSlot);
            if (row == null)
            {
                StopOrder(buy ? Loc.T("Торговец больше не продаёт этот предмет.", "The trader no longer offers this item.")
                              : Loc.T("Этого предмета больше нет в рюкзаке.", "That item is no longer in the backpack."));
                return;
            }
            int before = _money(_stats);
            if (buy && before < _order.Price)
            {
                StopOrder(Loc.T("Не хватает денег.", "Not enough money."));
                return;
            }
            _selectRow(_hud, row, true);
            _buyItem(_hud);
            int after = _money(_stats);
            _moneyNow = after;
            if (after == before)
            {
                _cancelBuy(_hud);   // the game opened its (hidden) message box
                StopOrder(buy ? Loc.T("В рюкзаке нет места или он слишком тяжёлый.", "No room in the backpack, or it would be too heavy.")
                              : Loc.T("Торговец не принял предмет.", "The trader did not take the item."));
                return;
            }
            _order.Unit(now, after - before);
            if (!_order.Active) EndOrder(true, null);
        }

        static void StopOrder(string why)
        {
            _order.Stop();
            EndOrder(false, why);
        }

        static void EndOrder(bool ok, string why)
        {
            if (_order.Done > 0)
            {
                int sum = _order.Earned < 0 ? -_order.Earned : _order.Earned;
                string text = (_order.Buy ? Loc.T("Куплено: ", "Bought ") : Loc.T("Продано: ", "Sold "))
                    + UiNum.Of(_order.Done) + " x " + (_order.Name ?? "?")
                    + Loc.T(" за ", " for ") + TraderMath.Money(sum);
                UiKit.Toast(text, UiTone.Success);
            }
            if (!ok && why != null) Error(why);
            _capMask |= 1 << TraderCat.Sell;   // the backpack changed: read the sell list again
        }

        /// <summary>The game builds the real rows of a category. Its
        /// ClearAllMarketItems only Destroys the old rows - deferred to the end
        /// of the frame - so they stay children until then; the new rows are
        /// appended after them.</summary>
        static void Rebuild(int cat)
        {
            Transform list = _hudList(_hud) as Transform;
            _rowBase = list == null ? 0 : list.childCount;
            _rowBaseFrame = Time.frameCount;
            _showMarketCat(_market, cat, false, true);
            _nativeCat = cat;
        }

        /// <summary>The game's live list row for an item id (buy) or a backpack
        /// slot (sell). Doomed rows of an earlier build are skipped, and the
        /// row's own category must fit the side (sell rows are category 7) -
        /// a wrong row would turn a sale into a purchase or back.</summary>
        static object NativeRow(int itemId, int slot)
        {
            Transform list = _hudList(_hud) as Transform;
            if (list == null) return null;
            int start = _rowBaseFrame == Time.frameCount ? _rowBase : 0;
            for (int i = start; i < list.childCount; i++)
            {
                Component row = list.GetChild(i).GetComponent(_tRow);
                if (row == null) continue;
                object item = _rowItem(row);
                if (item == null) continue;
                bool sellRow = _itemCat(item) == TraderCat.Sell;
                if (itemId >= 0 && !sellRow && _itemId(item) == itemId) return row;
                if (slot >= 0 && sellRow && _itemSlot(item) == slot) return row;
            }
            return null;
        }

        static int CategoryOf(int id)
        {
            int c;
            if (_cats.TryGetValue(id, out c)) return c;
            c = _itemCategory(id);
            if (c < 0 || c >= TraderCat.Count) c = TraderCat.Other;
            _cats[id] = c;
            return c;
        }

        /// <summary>The game's localized item name, once per item id.</summary>
        static string Name(int id)
        {
            string s;
            if (_names.TryGetValue(id, out s)) return s;
            s = null;
            try
            {
                string key = "$" + id + "_Name";
                s = _loc == null ? null : _locText(_loc, key, false) as string;
                if (string.IsNullOrEmpty(s) || s == key) s = null;
            }
            catch { s = null; }
            if (s == null) s = "#" + id;
            _names[id] = s;
            return s;
        }

        static void Error(string text)
        {
            UiKit.Toast(text, UiTone.Error);
        }

        // ------------------------------------------------------------ draw

        /// <summary>OnGUI. Idle: one bool test.</summary>
        internal static void Draw()
        {
            if (!_active) return;
            if (!UiKit.BeginWindow(_win))
            {
                _typing = false;
                // Open but not drawable (the kit failed to build): never leave
                // the player with an invisible trader - hand it back.
                if (_win.Open && ++_drawMiss > 8) KitMissing();
                return;
            }
            _drawMiss = 0;
            try { Content(_win.Content); }
            catch (Exception ex) { Fail("draw", ex); }
            UiKit.EndWindow(_win);
            _typing = _active && GUIUtility.keyboardControl != 0;
        }

        static void Content(Rect c)
        {
            float row = UiKit.S(UiKit.RowH), gap = UiKit.S(UiKit.Gap);
            int lang = Loc.Lang() == 0 ? 0 : 1;
            // Top bar: pages left, balance right.
            float barH = row + UiKit.S(4f);
            string[] pages = _market == null ? (lang == 0 ? PagesRu1 : PagesEn1)
                : _mercs ? (lang == 0 ? PagesRu3 : PagesEn3) : (lang == 0 ? PagesRu2 : PagesEn2);
            float tabsW = Mathf.Min(c.width * 0.55f, UiKit.S(150f) * pages.Length);
            int picked = UiKit.Tabs(new Rect(0f, 0f, tabsW, barH), _page, pages);
            if (picked != _page) _pendingPage = picked;
            DrawBalance(new Rect(c.width - UiKit.S(260f), 0f, UiKit.S(260f), barH), lang);

            float footH = UiKit.S(36f);
            Rect foot = new Rect(0f, c.height - footH, c.width, footH);
            Rect body = new Rect(0f, barH + gap * 2f, c.width, foot.y - gap - (barH + gap * 2f));
            if (_page == PageSafe) SafePage(body, row, gap, lang);
            else if (_page == PageTrade) TradePage(body, row, gap, lang);
            else { FrameProf.S(FrameProf.S_MercPageD); MercPage.Draw(body); FrameProf.E(FrameProf.S_MercPageD); }
            UiKit.LinkStatus(foot, _link, Time.realtimeSinceStartup,
                Loc.T("Мастер-сервер на связи - сейф и деньги сохраняются там",
                      "Master server connected - the safe and your money are kept there"));
        }

        static void DrawBalance(Rect r, int lang)
        {
            UiKit.Fill(r, UiKit.Field, 1);
            int m = _moneyNow;
            string t = _moneyText.Stale(m) ? _moneyText.Set(m, m < 0 ? "?" : TraderMath.Money(m)) : _moneyText.Text;
            UiKit.Label(new Rect(r.x + UiKit.S(12f), r.y, r.width * 0.45f, r.height),
                lang == 0 ? "БАЛАНС" : "BALANCE", UiFont.Small, UiFont.Left, UiKit.TextDim);
            UiKit.Label(new Rect(r.x, r.y, r.width - UiKit.S(12f), r.height), t, UiFont.Heading, UiFont.Right, UiKit.Warn);
            UiKit.Tip(r, Loc.T("Ваши деньги (хранятся на мастер-сервере)", "Your money (kept on the master server)"));
        }

        // ---- safe

        static void SafePage(Rect b, float row, float gap, int lang)
        {
            float half = Mathf.Floor((b.width - gap * 2f) * 0.54f);
            Rect left = new Rect(b.x, b.y, half, b.height);
            Rect right = new Rect(left.xMax + gap * 2f, b.y, b.width - half - gap * 2f, b.height);
            float cell = Mathf.Min(UiKit.S(56f), Mathf.Floor((left.width - UiKit.S(4f) * 7f) / 8f));

            // Left: the safe.
            float y = left.y;
            UiKit.Section(new Rect(left.x, y, left.width, row * 0.75f), lang == 0 ? "СЕЙФ" : "SAFE");
            y += row * 0.75f + gap;
            int shown = _safeCat < 0 ? 0 : _safeCat;
            int pick = UiKit.Tabs(new Rect(left.x, y, left.width, row), shown, lang == 0 ? SafeCatRu : SafeCatEn);
            if (pick != shown && _safeLoaded) _pendingCat = pick;
            y += row + gap;
            string cap = _safeCapText.Stale(_safeUsed, _safeTotal, shown, lang) ? _safeCapText.Set(_safeUsed, _safeTotal, shown, lang,
                TraderCat.Name(shown, lang) + "  " + UiNum.Of(_safeUsed)
                + " / " + UiNum.Of(SafeSlots) + (lang == 0 ? "   -   всего " : "   -   in all ") + UiNum.Of(_safeTotal) + " / " + UiNum.Of(SafeAll))
                : _safeCapText.Text;
            UiKit.Label(new Rect(left.x, y, left.width, row * 0.7f), cap, UiFont.Small, UiFont.Left, UiKit.TextDim);
            y += row * 0.7f;
            UiKit.Progress(new Rect(left.x, y, left.width, UiKit.S(6f)), TraderMath.Fill(_safeUsed, SafeSlots),
                _safeUsed >= SafeSlots ? UiKit.Bad : UiKit.Accent);
            y += UiKit.S(6f) + gap;
            Rect grid = new Rect(left.x, y, left.width, left.yMax - y);
            if (_safeLoaded) SafeGrid(grid, cell);
            else
            {
                Rect st = new Rect(grid.x, grid.y, grid.width, UiKit.S(40f));
                if (_safeClock.State == UiRetryClock.Lost)
                    UiKit.Status(st, UiTone.Error, UiText.Retry(_safeClock.SecondsLeft(Time.realtimeSinceStartup), lang));
                else UiKit.Status(st, UiTone.Loading, lang == 0 ? "Открываем сейф..." : "Opening the safe...");
                UiKit.Paragraph(new Rect(grid.x, st.yMax + gap, grid.width, row * 2f),
                    lang == 0 ? "Содержимое сейфа хранится на мастер-сервере." : "The safe's contents are kept on the master server.",
                    UiKit.TextDim);
            }

            // Right: the backpack.
            y = right.y;
            UiKit.Section(new Rect(right.x, y, right.width, row * 0.75f), lang == 0 ? "РЮКЗАК" : "BACKPACK");
            y += row * 0.75f + gap;
            int wk = Mathf.RoundToInt(_bagWeight * 10f), mk = Mathf.RoundToInt(_bagMaxWeight * 10f);
            string bcap = _bagCapText.Stale(_bagUsed * 1000 + _bagSlots, wk, mk, lang) ? _bagCapText.Set(_bagUsed * 1000 + _bagSlots, wk, mk, lang,
                (lang == 0 ? "Ячейки " : "Slots ")
                + UiNum.Of(_bagUsed) + " / " + UiNum.Of(_bagSlots) + (lang == 0 ? "   -   вес " : "   -   weight ")
                + Tenths(wk) + " / " + Tenths(mk) + (lang == 0 ? " кг" : " kg")) : _bagCapText.Text;
            UiKit.Label(new Rect(right.x, y, right.width, row * 0.7f), bcap, UiFont.Small, UiFont.Left, UiKit.TextDim);
            y += row * 0.7f;
            float wf = TraderMath.Fill(_bagWeight, _bagMaxWeight);
            UiKit.Progress(new Rect(right.x, y, right.width, UiKit.S(6f)), wf, wf >= 1f ? UiKit.Bad : wf > 0.85f ? UiKit.Warn : UiKit.Good);
            y += UiKit.S(6f) + gap;
            float hintH = row * 1.3f;
            Rect bgrid = new Rect(right.x, y, right.width, right.yMax - y - hintH);
            if (!_bagHas || _bagSlots == 0)
                UiKit.Paragraph(bgrid, lang == 0 ? "Рюкзак не надет - брать из сейфа можно только в рюкзак."
                                                 : "No backpack on - items from the safe go into a backpack.", UiKit.TextDim);
            else BagGrid(bgrid, cell);
            UiKit.Paragraph(new Rect(right.x, right.yMax - hintH, right.width, hintH),
                lang == 0 ? "Щелчок или перетаскивание переносит предмет между сейфом и рюкзаком."
                          : "Click an item, or drag it across, to move it between the safe and the backpack.",
                UiKit.TextDim);
            DragInput();
            DragGhost(cell);
        }

        static string Tenths(int v)
        {
            if (v < 0) v = 0;
            return UiNum.Of(v / 10) + "." + UiNum.Of(v % 10);
        }

        static void SafeGrid(Rect g, float cell)
        {
            float step = cell + UiKit.S(4f);
            int cols = Mathf.Max(1, (int)((g.width + UiKit.S(4f)) / step));
            _safeGridScreen = ScreenRect(new Rect(g.x, g.y, g.width, Mathf.Min(g.height, ((SafeSlots + cols - 1) / cols) * step)));
            for (int i = 0; i < SafeSlots; i++)
            {
                Rect r = new Rect(g.x + (i % cols) * step, g.y + (i / cols) * step, cell, cell);
                if (r.yMax > g.yMax + 1f) break;
                Cell(r, _safeIds[i], _safeIcons[i], _safeNames[i], false, SideSafe, i);
            }
        }

        static void BagGrid(Rect g, float cell)
        {
            float step = cell + UiKit.S(4f);
            int cols = Mathf.Max(1, (int)((g.width - UiKit.S(12f) + UiKit.S(4f)) / step));
            int rows = (_bagSlots + cols - 1) / cols;
            _bagGridScreen = ScreenRect(g);
            UiKit.BeginScroll(g, _bagScroll, rows * step);
            int first = Mathf.Max(0, (int)(_bagScroll.Offset / step)) * cols;
            int last = Mathf.Min(_bagSlots, ((int)((_bagScroll.Offset + g.height) / step) + 1) * cols);
            for (int i = first; i < last; i++)
            {
                Rect r = new Rect((i % cols) * step, (i / cols) * step, cell, cell);
                Cell(r, _bagIds[i], _bagIcons[i], _bagNames[i], _bagInUse[i], SideBag, i);
            }
            UiKit.EndScroll();
        }

        static void Cell(Rect r, int id, Texture icon, string name, bool inUse, int side, int index)
        {
            Event e = Event.current;
            bool hot = UiKit.Hover(r);
            if (e.type == EventType.MouseDown && e.button == 0 && hot && id != 0)
            {
                _press = side;
                _pressIdx = index;
                _pressAt = GUIUtility.GUIToScreenPoint(e.mousePosition);
                _dragging = false;
                e.Use();
            }
            if (e.type != EventType.Repaint) return;
            bool pressed = _press == side && _pressIdx == index;
            UiKit.Fill(r, id == 0 ? UiKit.Field : hot ? UiKit.CardHover : UiKit.CardFill, 2);
            if (id == 0) return;
            if (icon != null && !(pressed && _dragging))
            {
                float p = UiKit.S(4f);
                GUI.color = inUse ? UiKit.Fade(Color.white, 0.4f) : Color.white;
                GUI.DrawTexture(new Rect(r.x + p, r.y + p, r.width - 2f * p, r.height - 2f * p), icon, ScaleMode.ScaleToFit, true);
                GUI.color = Color.white;
            }
            else if (icon == null) UiKit.Label(r, "?", UiFont.Body, UiFont.Center, UiKit.TextDim);
            if (hot) UiKit.Outline(r, UiKit.Fade(UiKit.Accent, 0.8f));
            if (name != null) UiKit.Tip(r, name);
        }

        /// <summary>After both grids: a release over the other grid (drag) or a
        /// plain click moves the item; a drag released elsewhere does nothing.</summary>
        static void DragInput()
        {
            if (_press == SideNone) return;
            Event e = Event.current;
            Vector2 at = GUIUtility.GUIToScreenPoint(e.mousePosition);
            if (e.type == EventType.MouseDrag)
            {
                if (!_dragging && (at - _pressAt).sqrMagnitude > UiKit.S(6f) * UiKit.S(6f)) _dragging = true;
                e.Use();
            }
            else if (e.rawType == EventType.MouseUp)
            {
                bool toOther = _press == SideSafe ? _bagGridScreen.Contains(at) : _safeGridScreen.Contains(at);
                if (!_dragging || toOther)
                {
                    if (_press == SideSafe) _pendingTake = _pressIdx;
                    else _pendingStore = _pressIdx;
                }
                _press = SideNone;
                _dragging = false;
                if (e.type == EventType.MouseUp) e.Use();
            }
        }

        static void DragGhost(float cell)
        {
            if (!_dragging || _press == SideNone || Event.current.type != EventType.Repaint) return;
            Texture icon = _press == SideSafe ? (_pressIdx < SafeSlots ? _safeIcons[_pressIdx] : null)
                                              : (_pressIdx < BagCap ? _bagIcons[_pressIdx] : null);
            Vector2 m = Event.current.mousePosition;
            Rect r = new Rect(m.x - cell * 0.5f, m.y - cell * 0.5f, cell, cell);
            UiKit.Fill(r, UiKit.Fade(UiKit.Accent, 0.25f), 2);
            if (icon != null) GUI.DrawTexture(r, icon, ScaleMode.ScaleToFit, true);
        }

        static Rect ScreenRect(Rect r)
        {
            Vector2 a = GUIUtility.GUIToScreenPoint(new Vector2(r.x, r.y));
            return new Rect(a.x, a.y, r.width, r.height);
        }

        // ---- trade

        static void TradePage(Rect b, float row, float gap, int lang)
        {
            if (_book.TextsStale(lang)) _book.Texts(lang);
            // Filter bar + search.
            float searchW = UiKit.S(240f);
            Rect bar = new Rect(b.x, b.y, b.width - searchW - gap, row);
            _filter = UiKit.Tabs(bar, _filter, lang == 0 ? TraderCat.FilterRu : TraderCat.FilterEn);
            Rect sr = new Rect(bar.xMax + gap, b.y, searchW, row);
            string s = UiKit.TextField(sr, _search, 32);
            if (!ReferenceEquals(s, _search)) { _search = s; _searchClean = TraderText.Clean(s); }
            if (string.IsNullOrEmpty(_search) && GUIUtility.keyboardControl == 0)
                UiKit.Label(new Rect(sr.x + UiKit.S(10f), sr.y, sr.width - UiKit.S(20f), sr.height),
                    lang == 0 ? "Поиск..." : "Search...", UiFont.Body, UiFont.Left, UiKit.Fade(UiKit.TextDim, 0.7f));
            int cat = TraderCat.FromFilter(_filter);
            _book.Trader.Refilter(cat, _searchClean);
            _book.Inventory.Refilter(cat, _searchClean);

            float detailH = UiKit.S(72f);
            float top = b.y + row + gap * 2f;
            Rect cols = new Rect(b.x, top, b.width, b.yMax - top - detailH - gap);
            Rect lc = UiKit.Col(cols, 0, 2);
            Rect rc = UiKit.Col(cols, 1, 2);
            lc.width -= gap * 0.5f;
            rc.x += gap * 0.5f; rc.width -= gap * 0.5f;

            int tk = _book.Trader.ViewCount * 10 + lang;
            string th = _traderHead.Stale(tk) ? _traderHead.Set(tk, (lang == 0 ? "ТОРГОВЕЦ ПРОДАЁТ   " : "TRADER SELLS   ")
                + UiNum.Of(_book.Trader.ViewCount)) : _traderHead.Text;
            int ik = _book.Inventory.ViewCount * 10 + lang;
            string ih = _invHead.Stale(ik) ? _invHead.Set(ik, (lang == 0 ? "ВАШ РЮКЗАК - ПРОДАТЬ   " : "YOUR BACKPACK - SELL   ")
                + UiNum.Of(_book.Inventory.ViewCount)) : _invHead.Text;
            Column(lc, th, _book.Trader, _traderScroll, SideTrader, row, gap, lang);
            Column(rc, ih, _book.Inventory, _invScroll, SideInv, row, gap, lang);
            Detail(new Rect(b.x, b.yMax - detailH, b.width, detailH), row, gap, lang);
        }

        static void Column(Rect r, string head, TraderList list, UiScroll scroll, int side, float row, float gap, int lang)
        {
            UiKit.Section(new Rect(r.x, r.y, r.width, row * 0.75f), head);
            Rect view = new Rect(r.x, r.y + row * 0.75f + gap, r.width, r.height - row * 0.75f - gap);
            if (!_offerRead)
            {
                UiKit.Status(new Rect(view.x, view.y, view.width, UiKit.S(40f)), UiTone.Loading,
                    lang == 0 ? "Читаем список торговца..." : "Reading the trader's list...");
                return;
            }
            int n = list.ViewCount;
            if (n == 0)
            {
                UiKit.Paragraph(view, side == SideTrader
                    ? (lang == 0 ? "Здесь нет подходящих предметов. Смените категорию или поиск." : "Nothing matches here. Change the category or the search.")
                    : (lang == 0 ? "В рюкзаке нет предметов, которые покупает торговец." : "Nothing in your backpack this trader buys."),
                    UiKit.TextDim);
                return;
            }
            float rh = UiKit.S(46f), rs = rh + UiKit.S(4f);
            float w = UiKit.BeginScroll(view, scroll, n * rs - UiKit.S(4f)).width;
            int first = Mathf.Max(0, (int)(scroll.Offset / rs));
            int last = Mathf.Min(n, (int)((scroll.Offset + view.height) / rs) + 2);
            for (int i = first; i < last; i++)
                RowItem(new Rect(0f, i * rs, w, rh), list.View(i), side);
            UiKit.EndScroll();
        }

        static void RowItem(Rect r, TraderRow item, int side)
        {
            Event e = Event.current;
            bool hot = UiKit.Hover(r);
            if (e.type == EventType.MouseDown && e.button == 0 && hot)
            {
                Select(side, item);
                if (e.clickCount == 2) Order(side == SideTrader, 1);
                e.Use();
            }
            if (e.type != EventType.Repaint) return;
            bool sel = _sel == item && _selSide == side;
            UiKit.Fill(r, sel ? UiKit.Fade(UiKit.Accent, 0.22f) : hot ? UiKit.CardHover : UiKit.CardFill, 1);
            if (sel) UiKit.Outline(r, UiKit.Accent);
            float isz = r.height - UiKit.S(10f);
            Rect ir = new Rect(r.x + UiKit.S(6f), r.y + UiKit.S(5f), isz, isz);
            UiKit.Fill(ir, UiKit.Field, 2);
            Texture icon = item.Icon as Texture;
            if (icon != null) GUI.DrawTexture(ir, icon, ScaleMode.ScaleToFit, true);
            float tx = ir.xMax + UiKit.S(10f);
            float pw = UiKit.S(92f);
            float tw = r.xMax - tx - pw - UiKit.S(8f);
            UiKit.Label(new Rect(tx, r.y + UiKit.S(4f), tw, r.height * 0.5f), item.Name, UiFont.Body, UiFont.Left, UiKit.Text);
            UiKit.Label(new Rect(tx, r.y + r.height * 0.5f - UiKit.S(1f), tw, r.height * 0.5f - UiKit.S(3f)), item.SubText, UiFont.Small, UiFont.Left, UiKit.TextDim);
            bool buy = side == SideTrader;
            Color pc = buy ? (_moneyNow >= 0 && item.Price > _moneyNow ? UiKit.Bad : UiKit.Text) : UiKit.Good;
            UiKit.Label(new Rect(r.xMax - pw - UiKit.S(10f), r.y + UiKit.S(4f), pw, r.height * 0.5f), item.PriceText, UiFont.Body, UiFont.Right, pc);
            if (item.CountText != null)
                UiKit.Label(new Rect(r.xMax - pw - UiKit.S(10f), r.y + r.height * 0.5f, pw, r.height * 0.5f - UiKit.S(4f)), item.CountText, UiFont.Small, UiFont.Right, UiKit.TextDim);
        }

        static void Detail(Rect r, float row, float gap, int lang)
        {
            UiKit.Fill(r, UiKit.Header, 1);
            UiKit.Outline(r, UiKit.Line);
            float pad = UiKit.S(12f);
            Rect inner = new Rect(r.x + pad, r.y + pad, r.width - 2f * pad, r.height - 2f * pad);
            if (_order.Active || _orderQueued)
            {
                string t = _orderText.Stale(_order.Done, _order.Wanted, _order.Buy ? 1 : 0, lang) ? _orderText.Set(_order.Done, _order.Wanted, _order.Buy ? 1 : 0, lang,
                    (_order.Buy ? (lang == 0 ? "Покупаем " : "Buying ") : (lang == 0 ? "Продаём " : "Selling "))
                    + UiNum.Of(_order.Done) + " / " + UiNum.Of(_order.Wanted) + " ...") : _orderText.Text;
                UiKit.Status(new Rect(inner.x, inner.y + (inner.height - UiKit.S(40f)) * 0.5f, inner.width * 0.6f, UiKit.S(40f)), UiTone.Loading, t);
                if (UiKit.Button(new Rect(inner.xMax - UiKit.S(140f), inner.y + (inner.height - row) * 0.5f, UiKit.S(140f), row),
                        lang == 0 ? "Стоп" : "Stop", UiButton.Secondary, true, null))
                { _orderQueued = false; _order.Stop(); EndOrder(true, null); }
                return;
            }
            if (_capMask != 0)
            {
                UiKit.Status(new Rect(inner.x, inner.y + (inner.height - UiKit.S(40f)) * 0.5f, inner.width * 0.6f, UiKit.S(40f)),
                    UiTone.Loading, lang == 0 ? "Обновляем списки..." : "Updating the lists...");
                return;
            }
            TraderRow s = _sel;
            if (s == null)
            {
                UiKit.Paragraph(inner, lang == 0
                    ? "Выберите предмет слева (купить) или справа (продать). Двойной щелчок - сделка на 1 шт."
                    : "Pick an item on the left to buy or on the right to sell. Double-click trades one.", UiKit.TextDim);
                return;
            }
            bool buy = _selSide == SideTrader;
            int money = _moneyNow < 0 ? 0 : _moneyNow;
            int max = TraderMath.MaxQty(buy, money, s.Price, s.Count);
            _qty = TraderMath.ClampQty(_qty, max);
            int total = TraderMath.Total(s.Price, _qty);

            // Icon + name + unit price comparison.
            float isz = inner.height;
            Rect ir = new Rect(inner.x, inner.y, isz, isz);
            UiKit.Fill(ir, UiKit.Field, 2);
            Texture icon = s.Icon as Texture;
            if (icon != null && Event.current.type == EventType.Repaint) GUI.DrawTexture(ir, icon, ScaleMode.ScaleToFit, true);
            float nx = ir.xMax + UiKit.S(12f);
            float nw = inner.width * 0.34f;
            UiKit.Label(new Rect(nx, inner.y, nw, inner.height * 0.5f), s.Name, UiFont.Heading, UiFont.Left, UiKit.Text);
            string unit = _unitText.Stale(s.Price, s.Other, buy ? 1 : 0, lang) ? _unitText.Set(s.Price, s.Other, buy ? 1 : 0, lang, buy
                ? (lang == 0 ? "Цена " : "Price ") + s.PriceText + (s.Other > 0 ? (lang == 0 ? "   выкуп " : "   sells back ") + TraderMath.Money(s.Other) : "")
                : (lang == 0 ? "Выкуп " : "Sells for ") + s.PriceText + (s.Other > 0 ? (lang == 0 ? "   у торговца " : "   trader asks ") + TraderMath.Money(s.Other) : ""))
                : _unitText.Text;
            UiKit.Label(new Rect(nx, inner.y + inner.height * 0.5f, nw, inner.height * 0.5f), unit, UiFont.Small, UiFont.Left, UiKit.TextDim);

            // Quantity picker.
            float qx = nx + nw + UiKit.S(8f);
            float bw = row;
            Rect minus = new Rect(qx, inner.y + (inner.height - row) * 0.5f, bw, row);
            if (UiKit.IconButton(minus, UiIcon.Minus, lang == 0 ? "Меньше" : "Fewer", true)) _qty = TraderMath.ClampQty(_qty - 1, max);
            Rect qr = new Rect(minus.xMax + UiKit.S(4f), minus.y, UiKit.S(48f), row);
            UiKit.Fill(qr, UiKit.Field, 1);
            UiKit.Label(qr, UiNum.Of(_qty), UiFont.Heading, UiFont.Center, max > 0 ? UiKit.Text : UiKit.TextDim);
            Rect plus = new Rect(qr.xMax + UiKit.S(4f), minus.y, bw, row);
            if (UiKit.IconButton(plus, UiIcon.Plus, lang == 0 ? "Больше" : "More", true)) _qty = TraderMath.ClampQty(_qty + 1, max);
            Rect mx = new Rect(plus.xMax + UiKit.S(4f), minus.y, UiKit.S(56f), row);
            if (UiKit.Button(mx, lang == 0 ? "Макс" : "Max", UiButton.Ghost, max > 1,
                    buy ? (lang == 0 ? "Сколько хватает денег (до 50)" : "As many as the money buys (up to 50)")
                        : (lang == 0 ? "Все такие предметы (до 50)" : "Every such item (up to 50)")))
                _qty = max;

            // Result + action.
            float ax = mx.xMax + UiKit.S(12f);
            float aw = inner.xMax - ax;
            int after = TraderMath.After(money, buy, total);
            string at = _afterText.Stale(after, lang, 0, 0) ? _afterText.Set(after, lang, 0, 0, (lang == 0 ? "После сделки: " : "Balance after: ") + TraderMath.Money(after)) : _afterText.Text;
            UiKit.Label(new Rect(ax, inner.y, aw, inner.height * 0.45f), at, UiFont.Small, UiFont.Right, after < 0 ? UiKit.Bad : UiKit.TextDim);
            string act = _actionText.Stale(total, _qty, buy ? 1 : 0, lang) ? _actionText.Set(total, _qty, buy ? 1 : 0, lang,
                (buy ? (lang == 0 ? "Купить " : "Buy ") : (lang == 0 ? "Продать " : "Sell ")) + UiNum.Of(_qty)
                + (buy ? "  -  " : "  +  ") + TraderMath.Money(total)) : _actionText.Text;
            bool can = max > 0 && _linkUp;
            string tip = !_linkUp ? Loc.T("Нет связи с мастер-сервером", "No master server connection")
                : max == 0 ? (buy ? Loc.T("Не хватает денег", "Not enough money") : Loc.T("Нечего продавать", "Nothing to sell")) : null;
            Rect btn = new Rect(inner.xMax - UiKit.S(220f), inner.y + inner.height * 0.45f, UiKit.S(220f), inner.height * 0.55f);
            if (UiKit.Button(btn, act, UiButton.Primary, can, tip)) Order(buy, _qty);
        }

        /// <summary>A trade request from the UI: runs in Tick (Update), one unit per step.</summary>
        static void Order(bool buy, int qty)
        {
            if (_sel == null || _order.Active || _orderQueued) return;
            if (!_linkUp)
            {
                Error(Loc.T("Нет связи с мастер-сервером - торговля на паузе.", "No master server connection - trading is paused."));
                return;
            }
            if (buy && _moneyNow >= 0 && _moneyNow < _sel.Price)
            {
                Error(Loc.T("Не хватает денег.", "Not enough money."));
                return;
            }
            _orderBuy = buy;
            _orderQty = qty;
            _orderQueued = true;
        }

        // ------------------------------------------------------------ failure

        /// <summary>Any exception: log once, hand this session back to the game's own window.</summary>
        static void Fail(string where, Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                Log(where + " failed, the game's own window takes over: " + ex);
            }
            _capture = -1;
            _standDown = true;
            if (_active) Deactivate();
        }

        static void KitMissing()
        {
            Fail("draw", new InvalidOperationException("the UI kit cannot draw"));
        }

        static void Log(string s)
        {
            if (RevivalPlugin.L != null) RevivalPlugin.L.LogWarning("TraderUi: " + s);
        }
    }
}
