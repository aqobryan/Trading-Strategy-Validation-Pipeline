#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// RetraceLogger
// Records, for every trade on one account (chart's instrument), how price moved while the trade
// was open, so trailing stop settings (activation level + distance) can be replayed offline,
// by time of day. Everything is measured in TICKS in the trade's favor.
//
// Per trade (one CSV row):
//   Identity / time   TradeNo, Account, Strategy, Contract, Direction, EntryTime, ExitTime,
//                     EntryHour, DayOfWeek, MinutesInTrade, MinutesToMFE
//   Trade shape       Qty, EntryFills, ExitFills, StartedBeforeLoad, EntryName, ExitName
//   Conversion        TickValue (dollars per tick per contract)
//   Volatility        AtrTicksAtEntry
//   Strategy exits    SL_Ticks / TP_Ticks (initial stop and target distance from entry, read from the
//                     working orders; blank if not seen), LastStopTicks (last stop level seen,
//                     relative to entry: + = profit locked, - = still risking)
//   Result            AvgEntry, AvgExit, NetTicks (before commissions)
//   Excursions        MFE_Ticks, MAE_Ticks
//   Retrace stats     MaxRetraceToPeak, AvgRetraceToPeak, RetraceCount, GivebackTicks
//   Path              "peak:drop:minute|peak:drop:minute|..." up to the exit
//   After the exit    PostMinutesTracked, PostRunTicks, PostAdverseTicks, PostPath
//   Added in v2       AvgEntryAll, NetTicksAll, ExitReason, Flags, Fills, ParentEntry (see below)
//
// v2 entry rule (time based, not price based): every entry fill that happens within the first
// EntryWindowSeconds (default 60) of a trade's first fill counts as ONE entry, and the entry price
// (AvgEntry, the baseline for every tick figure in the row) is the average price of all contracts filled
// in that window. Any same-direction fill AFTER the window (the next signal, an add) is its own
// trade: its own row, its own entry price, its own path measured from that price. Several trades can be
// open at once. Columns for this: Flags "Overlap" (another trade was open at the same time), ParentEntry
// (EntryName of the oldest trade that was still open when this one opened, blank if none).
// A closing fill is given to the trade that owns the stop / target order that was filled; if that is not
// known, to the oldest open trade first. Flag "SharedExit" = one fill closed several trades at one price.
// Each stop / target order is assigned to the newest open trade that has none of that kind yet; flag
// "NoOwnOrders" = the trade never saw a stop / target of its own and follows the position's (SL_Ticks,
// TP_Ticks and LastStopTicks are then measured from its own entry against the position's orders).
// AvgEntryAll / NetTicksAll are kept so the column layout stays the same; with one trade per entry window they
// equal AvgEntry / NetTicks.
//
// ExitReason (new): StopLoss, TrailStop (stop that had moved into profit), Target, SignalClose
// ("Close position"), Flatten (exit within FlattenToleranceSeconds of the flatten time, default 4:00 PM,
// that was not a stop or target), AccountHalt (order name contains "Halt"), Unnamed, Other.
// Flags (new): HaltEntry (entry order name contains "Halt", e.g. ExitAcctHalt1: a flatten order that
// opened a position by accident), Overlap, SharedExit, NoOwnOrders.
// Fills (new): every fill as seconds-after-first-fill:signedQty@price, e.g. 0.0:+3@29797.75|905.2:-3@29805.25
//
// Path segments: "peak" = favorable high-water mark (ticks from entry) when a pullback began,
// "drop" = deepest pullback (ticks) from that peak before price made a new high,
// "minute" = minutes after entry when that peak was made. The first segment starts at entry
// (peak 0), so its drop is the adverse move before the trade first went into profit.
//
// After the exit the indicator keeps following price for PostExitMinutes (or until a new trade
// opens) so you can see what a trailing stop would have done instead of the strategy's own exit.
//   PostRunTicks      how far price went further in the trade's favor beyond the exit price
//   PostAdverseTicks  how far price went against the trade beyond the exit price
//   PostPath          continuation of the path from the segment that was in progress at the exit
//
// Add one instance per account. Each account gets its own CSV file.

namespace NinjaTrader.NinjaScript.Indicators
{
    public class RetraceLogger : Indicator
    {
        private const string TimeFmt = "yyyy-MM-dd HH:mm:ss.fff";

        private class Trk
        {
            public int No, Dir, Pos, MaxQty, EntryFills, ExitFills, ExitQty;
            public double Entry, ExitValue, AtrAtEntry;
            public DateTime Open, LastMfe, ExitTime, PostEnd;
            public string EntryName = "", ExitName = "";
            public bool Partial, Closed;

            // v2: entry window and fill record
            public double AllValue, WinValue, AvgEntryAll, NetTicksAll;
            public int AllQty, WinQty;
            public List<string> Fills = new List<string>();
            public string ExitReason = "", Flags = "";

            // v2b: every entry outside the first trade's window is its own trade row
            public string ParentEntry = "";            // EntryName of the oldest trade still open when this one opened
            public bool Overlap, SharedExit, OwnSeen, UseShared;

            // Live excursion tracking (ticks, favorable = positive)
            public double Peak, PeakMin, SegDrop, Mfe, Mae;
            public List<double[]> Segs = new List<double[]>();   // {peak, drop, minuteOfPeak}
            public int PreCount;

            // Snapshot taken at the exit
            public double AvgExit, NetTicks, MfeAtExit, MaeAtExit, MinToMfeAtExit;
            public double MaxToPeak, AvgToPeak, Giveback;
            public int RetraceCount;
            public string PrePath = "";

            // Post-exit tracking (ticks from entry, favorable = positive)
            public double PostMaxF, PostMinF;

            // The strategy's own exit orders, seen as working orders (ticks from entry)
            public double SlTicks, TpTicks, LastStopTicks;   // LastStopTicks: + = profit locked, - = still risk
            public bool HaveSl, HaveTp, HaveStop;
        }

        private Account account;
        private readonly object sync = new object();
        private readonly HashSet<string> seenExec = new HashSet<string>();

        private readonly List<Trk> open = new List<Trk>();   // trades currently open, oldest first
        private readonly List<Trk> fins = new List<Trk>();   // closed trades still being followed through the post-exit window
        private readonly HashSet<Order> working = new HashSet<Order>();   // orders currently working on the account
        private readonly Dictionary<Order, Trk> owner = new Dictionary<Order, Trk>();   // which open trade each stop / target order belongs to
        private const double OwnOrderGraceSeconds = 5;   // a trade that has seen no stop / target of its own after this long shares the position's
        private int tradeCount;
        private double tickSize;
        private double tickValue;

        private ATR atr;
        private double lastAtrTicks;

        // Panel
        private readonly List<string> lines = new List<string>();
        private int longN, shortN;
        private double longRetraceSum, shortRetraceSum;
        private double longMaxSum, shortMaxSum;
        private bool dirty;
        private string filePath;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description         = "Logs per-trade retracement path data for trailing stop analysis.";
                Name                = "RetraceLogger";
                Calculate           = Calculate.OnPriceChange;
                IsOverlay           = true;
                DisplayInDataBox    = false;
                PaintPriceMarkers   = false;

                AccountName         = "Sim101";
                StrategyLabel       = "";
                MaxLinesShown       = 5;
                AtrPeriod           = 14;
                PostExitMinutes     = 90;
                EntryWindowSeconds  = 60;
                FlattenTagEnabled   = true;
                FlattenHour         = 16;
                FlattenMinute       = 0;
                FlattenToleranceSeconds = 120;
                OutputFolder        = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "TradeLogs");
            }
            else if (State == State.DataLoaded)
            {
                tickSize  = Instrument.MasterInstrument.TickSize;
                tickValue = Instrument.MasterInstrument.PointValue * tickSize;
                atr = ATR(AtrPeriod);

                lock (Account.All)
                    account = Account.All.FirstOrDefault(a => a.Name == AccountName);

                if (account == null)
                {
                    Print("RetraceLogger: account '" + AccountName + "' not found.");
                    return;
                }

                try
                {
                    Directory.CreateDirectory(OutputFolder);
                    string safeAcct = new string(AccountName.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
                    filePath = Path.Combine(OutputFolder,
                        "RetraceLog_" + safeAcct + "_" + Instrument.MasterInstrument.Name + ".csv");

                    const string header =
                        "TradeNo,Account,Strategy,Contract,Direction,EntryTime,ExitTime,EntryHour,DayOfWeek," +
                        "MinutesInTrade,MinutesToMFE,Qty,EntryFills,ExitFills,StartedBeforeLoad,EntryName,ExitName," +
                        "TickValue,AtrTicksAtEntry,SL_Ticks,TP_Ticks,LastStopTicks,AvgEntry,AvgExit,NetTicks,MFE_Ticks,MAE_Ticks," +
                        "MaxRetraceToPeak,AvgRetraceToPeak,RetraceCount,GivebackTicks,Path," +
                        "PostMinutesTracked,PostRunTicks,PostAdverseTicks,PostPath," +
                        "AvgEntryAll,NetTicksAll,ExitReason,Flags,Fills,ParentEntry";

                    // An older-format file is archived, so rows with different columns never get mixed
                    if (File.Exists(filePath))
                    {
                        string first = File.ReadLines(filePath).FirstOrDefault();
                        if (first != header)
                        {
                            string archived = Path.Combine(OutputFolder,
                                Path.GetFileNameWithoutExtension(filePath) + "_old_" +
                                DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                            File.Move(filePath, archived);
                        }
                    }

                    if (!File.Exists(filePath))
                        File.AppendAllText(filePath, header + Environment.NewLine);
                    else
                        tradeCount = Math.Max(0, File.ReadAllLines(filePath).Length - 1);
                }
                catch (Exception ex)
                {
                    Print("RetraceLogger: could not prepare log file: " + ex.Message);
                    filePath = null;
                }

                // Remember stop / target orders that are already working
                lock (account.Orders)
                {
                    foreach (Order o in account.Orders)
                    {
                        if (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted
                            || o.OrderState == OrderState.PartFilled)
                            working.Add(o);
                    }
                }

                // Pick up a position that is already open when the indicator loads
                // (flagged StartedBeforeLoad; excursions are then only measured from load time).
                lock (account.Positions)
                {
                    foreach (Position p in account.Positions)
                    {
                        if (p.Instrument.MasterInstrument.Name == Instrument.MasterInstrument.Name
                            && p.MarketPosition != MarketPosition.Flat)
                        {
                            int dir = p.MarketPosition == MarketPosition.Long ? 1 : -1;
                            Trk t = NewTrade(dir, p.Quantity, p.AveragePrice, DateTime.Now, "");
                            t.Partial = true;
                            open.Add(t);
                            AssignAll();
                            ApplyOrders(t);
                        }
                    }
                }

                account.ExecutionUpdate += OnExecutionUpdate;
                account.OrderUpdate     += OnOrderUpdate;
            }
            else if (State == State.Terminated)
            {
                if (account != null)
                {
                    account.ExecutionUpdate -= OnExecutionUpdate;
                    account.OrderUpdate     -= OnOrderUpdate;
                }

                lock (sync)
                {
                    FinalizeAll(DateTime.Now);
                }
            }
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace(",", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private Trk NewTrade(int dir, int qty, double price, DateTime time, string orderName)
        {
            var t = new Trk();
            t.Dir        = dir;
            t.Pos        = dir * qty;
            t.MaxQty     = qty;
            t.Entry      = price;
            t.Open       = time;
            t.LastMfe    = time;
            t.EntryFills = 1;
            t.EntryName  = orderName;
            t.AtrAtEntry = lastAtrTicks;
            t.AllQty     = qty;
            t.AllValue   = price * qty;
            t.WinQty     = qty;
            t.WinValue   = price * qty;
            t.Fills.Add(FillText(t, time, qty, price));
            return t;
        }

        // One fill as "seconds after the trade's first fill : signed quantity @ price" (+ = adds to the position, - = reduces it)
        private static string FillText(Trk s, DateTime time, int signedQty, double price)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            double secs = Math.Max(0, (time - s.Open).TotalSeconds);
            return secs.ToString("0.0", ic) + ":" + (signedQty > 0 ? "+" : "") + signedQty.ToString(ic) + "@" + price.ToString("F2", ic);
        }

        // The entry price moved (a fill inside the entry window changed the average): re-measure everything
        // recorded so far from the new entry price. shift = ticks the entry price moved in the trade's favor.
        private void Rebase(Trk s, double shift)
        {
            if (Math.Abs(shift) < 1e-12)
                return;
            s.Peak += shift;
            s.Mfe   = Math.Max(0, s.Mfe + shift);
            s.Mae   = Math.Max(0, s.Mae - shift);
            foreach (double[] seg in s.Segs)
                seg[0] += shift;
        }

        // Every last-traded price: feeds the open trade, and the closed trade in its post-exit window.
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last)
                return;

            DateTime now = DateTime.Now;
            lock (sync)
            {
                foreach (Trk t in open)
                {
                    Track(t, e.Price, now);

                    // A trade that opened while another was open normally gets its own stop / target.
                    // If none showed up, it follows the stop / target already working on the position.
                    if (!t.OwnSeen && !t.UseShared && (now - t.Open).TotalSeconds > OwnOrderGraceSeconds)
                    {
                        t.UseShared = true;
                        ApplyOrders(t);
                    }
                }

                for (int i = fins.Count - 1; i >= 0; i--)
                {
                    Track(fins[i], e.Price, now);
                    if (now >= fins[i].ExitTime.AddMinutes(PostExitMinutes))
                        FinalizeFin(fins[i], now);
                }
            }
        }

        // Caller must hold the lock.
        private void Track(Trk s, double price, DateTime now)
        {
            if (tickSize <= 0)
                return;

            double f = s.Dir * (price - s.Entry) / tickSize;   // favorable ticks (negative = against us)

            if (f > s.Mfe)
            {
                s.Mfe = f;
                s.LastMfe = now;
            }
            if (-f > s.Mae)
                s.Mae = -f;

            if (s.Closed)
            {
                if (f > s.PostMaxF) s.PostMaxF = f;
                if (f < s.PostMinF) s.PostMinF = f;
            }

            if (f > s.Peak)
            {
                if (s.SegDrop > 0)
                    s.Segs.Add(new double[] { s.Peak, s.SegDrop, s.PeakMin });
                s.Peak    = f;
                s.SegDrop = 0;
                s.PeakMin = Math.Max(0, (now - s.Open).TotalMinutes);
            }
            else
            {
                double drop = s.Peak - f;
                if (drop > s.SegDrop) s.SegDrop = drop;
            }
        }

        // Fires on the account's thread.
        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            Execution ex = e.Execution;
            if (ex == null || ex.Order == null)
                return;
            if (ex.Instrument.MasterInstrument.Name != Instrument.MasterInstrument.Name)
                return;

            lock (sync)
            {
                if (!seenExec.Add(ex.ExecutionId))
                    return;

                OrderAction a = ex.Order.OrderAction;
                int sgn = (a == OrderAction.Buy || a == OrderAction.BuyToCover) ? 1 : -1;
                HandleFill(sgn, ex.Quantity, ex.Price, ex.Time, ex.Order.Name, ex.Order);
            }
        }

        // One fill from the account. 'ord' is the order that was filled (used to find which trade it belongs to).
        private void HandleFill(int sgn, int qty, double price, DateTime time, string orderName, Order ord)
        {
            if (qty <= 0)
                return;

            int remaining = qty;

            // Part of the fill that closes (or reduces) open trades. The trade that owns the filled stop / target
            // order is closed first; anything left over closes the oldest open trades first.
            var touched = new List<Trk>();
            while (remaining > 0)
            {
                Trk t = null;
                Trk own;
                if (ord != null && owner.TryGetValue(ord, out own) && open.Contains(own) && own.Pos != 0 && Math.Sign(own.Pos) != sgn)
                    t = own;
                if (t == null)
                    t = open.FirstOrDefault(x => x.Pos != 0 && Math.Sign(x.Pos) != sgn);
                if (t == null)
                    break;

                int closing = Math.Min(remaining, Math.Abs(t.Pos));

                Track(t, price, time);   // make sure the exit price is part of the path
                t.Fills.Add(FillText(t, time, -closing, price));
                t.ExitQty   += closing;
                t.ExitValue += price * closing;
                t.ExitFills++;
                t.ExitName   = orderName;
                t.Pos       += sgn * closing;
                remaining   -= closing;
                if (!touched.Contains(t))
                    touched.Add(t);
            }
            if (touched.Count > 1)
                foreach (Trk t in touched)
                    t.SharedExit = true;   // one fill closed several trades, so they share an exit price
            foreach (Trk t in touched)
                if (t.Pos == 0)
                    CloseTrade(t, time);

            // Part of the fill that opens a new position or adds to an open one
            if (remaining > 0)
            {
                // The newest open trade in this direction takes the fill if it is still inside its entry window
                Trk join = null;
                for (int i = open.Count - 1; i >= 0; i--)
                {
                    if (open[i].Dir != sgn)
                        continue;
                    if ((time - open[i].Open).TotalSeconds <= EntryWindowSeconds)
                        join = open[i];
                    break;
                }

                if (join != null)
                {
                    // Inside the entry window: these contracts are part of the entry, so the entry price is the
                    // average of every contract filled in the window, and what was recorded so far is re-measured from it.
                    join.Fills.Add(FillText(join, time, remaining, price));
                    join.AllQty   += remaining;
                    join.AllValue += price * remaining;
                    join.Pos      += sgn * remaining;
                    join.MaxQty    = Math.Max(join.MaxQty, Math.Abs(join.Pos));
                    join.EntryFills++;

                    double oldEntry = join.Entry;
                    join.WinQty   += remaining;
                    join.WinValue += price * remaining;
                    join.Entry     = join.WinValue / join.WinQty;
                    if (tickSize > 0)
                        Rebase(join, join.Dir * (oldEntry - join.Entry) / tickSize);
                }
                else
                {
                    // Outside every entry window (the next signal, an add, a re-entry): its own trade row
                    FinalizeAll(time);   // a new trade ends the post-exit windows of closed trades
                    if (open.Count == 0)
                        owner.Clear();

                    Trk nt = NewTrade(sgn, remaining, price, time, orderName);
                    if (open.Count > 0)
                    {
                        nt.ParentEntry = open[0].EntryName;
                        nt.Overlap = true;
                        foreach (Trk o in open)
                            o.Overlap = true;
                    }
                    open.Add(nt);
                    AssignAll();
                    foreach (Trk o in open)
                        ApplyOrders(o);
                }
            }
        }

        // Stop and target orders that are working but not yet assigned go to a trade (see AssignOwner).
        private void AssignAll()
        {
            foreach (Order o in working)
                AssignOwner(o);
        }

        private static bool IsStopType(Order o)  { return o.OrderType == OrderType.StopMarket || o.OrderType == OrderType.StopLimit; }
        private static bool IsLimitType(Order o) { return o.OrderType == OrderType.Limit; }

        private bool HasLiveOrder(Trk t, bool stopKind)
        {
            foreach (KeyValuePair<Order, Trk> kv in owner)
            {
                if (kv.Value != t || !working.Contains(kv.Key))
                    continue;
                if (stopKind ? IsStopType(kv.Key) : IsLimitType(kv.Key))
                    return true;
            }
            return false;
        }

        // A new stop / target order goes to the newest open trade (in the order's direction) that does not have a
        // working order of that kind yet; if every trade has one, to the newest trade. Caller holds the lock.
        private void AssignOwner(Order o)
        {
            if (o == null || owner.ContainsKey(o) || open.Count == 0)
                return;
            bool stopKind = IsStopType(o);
            if (!stopKind && !IsLimitType(o))
                return;

            Trk pick = null;
            for (int i = open.Count - 1; i >= 0; i--)
            {
                Trk t = open[i];
                OrderAction exitAction = t.Dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
                if (o.OrderAction != exitAction)
                    continue;
                if (pick == null)
                    pick = t;
                if (!HasLiveOrder(t, stopKind))
                {
                    pick = t;
                    break;
                }
            }
            if (pick != null)
            {
                owner[o] = pick;
                pick.OwnSeen = true;
            }
        }

        // Caller must hold the lock.
        private void CloseTrade(Trk s, DateTime exitTime)
        {
            open.Remove(s);

            tradeCount++;
            s.No       = tradeCount;
            s.ExitTime = exitTime;
            s.AvgExit  = s.ExitQty > 0 ? s.ExitValue / s.ExitQty : s.Entry;
            s.NetTicks = tickSize > 0 ? s.Dir * (s.AvgExit - s.Entry) / tickSize : 0;
            s.AvgEntryAll = s.AllQty > 0 ? s.AllValue / s.AllQty : s.Entry;
            s.NetTicksAll = tickSize > 0 ? s.Dir * (s.AvgExit - s.AvgEntryAll) / tickSize : 0;
            s.ExitReason  = ClassifyExit(s, exitTime);
            s.Flags       = BuildFlags(s);

            s.MfeAtExit      = s.Mfe;
            s.MaeAtExit      = s.Mae;
            s.MinToMfeAtExit = Math.Max(0, (s.LastMfe - s.Open).TotalMinutes);

            // Path up to the exit: completed segments plus the one in progress
            var pre = new List<double[]>(s.Segs);
            if (s.SegDrop > 0)
                pre.Add(new double[] { s.Peak, s.SegDrop, s.PeakMin });
            s.PreCount = s.Segs.Count;
            s.PrePath  = PathString(pre);

            // Pullbacks endured on the way to the trade's best point: every segment whose peak is
            // below the final high-water mark (the last segment starts at the best point itself).
            var toPeak = pre.Where(x => x[0] < s.MfeAtExit - 1e-9).Select(x => x[1]).ToList();
            s.MaxToPeak    = toPeak.Count > 0 ? toPeak.Max() : 0;
            s.AvgToPeak    = toPeak.Count > 0 ? toPeak.Average() : 0;
            s.RetraceCount = toPeak.Count;
            s.Giveback     = s.MfeAtExit - s.NetTicks;

            // Begin the post-exit window
            s.Closed  = true;
            s.PostMaxF = s.NetTicks;
            s.PostMinF = s.NetTicks;
            fins.Add(s);

            // Panel stats (pre-exit information)
            if (s.Dir > 0) { longN++;  longRetraceSum  += s.AvgToPeak; longMaxSum  += s.MaxToPeak; }
            else           { shortN++; shortRetraceSum += s.AvgToPeak; shortMaxSum += s.MaxToPeak; }

            lines.Add(string.Format("#{0} {1} net {2:0.#}t  MFE {3:0.#}  maxRetr {4:0.#}  avgRetr {5:0.#}",
                s.No, s.Dir > 0 ? "L" : "S", s.NetTicks, s.MfeAtExit, s.MaxToPeak, s.AvgToPeak));
            dirty = true;

            if (PostExitMinutes <= 0)
                FinalizeFin(s, exitTime);
        }

        // Why the trade ended. Uses NinjaTrader's standard managed-order names ("Stop loss", "Profit target", "Close position").
        private string ClassifyExit(Trk s, DateTime exitTime)
        {
            string nl = (s.ExitName ?? "").Trim().ToLowerInvariant();
            if (nl.Contains("halt"))
                return "AccountHalt";

            bool isStop   = nl == "stop loss";
            bool isTarget = nl == "profit target";

            // A time-based flatten (default 4:00 PM on this PC's clock) that was not the strategy's own stop or target
            if (FlattenTagEnabled && !isStop && !isTarget)
            {
                DateTime flat = exitTime.Date.AddHours(FlattenHour).AddMinutes(FlattenMinute);
                if (Math.Abs((exitTime - flat).TotalSeconds) <= FlattenToleranceSeconds)
                    return "Flatten";
            }

            if (isStop)
                return (s.HaveStop && s.LastStopTicks > 0) ? "TrailStop" : "StopLoss";
            if (isTarget)
                return "Target";
            if (nl == "close position")
                return "SignalClose";
            if (nl.Length == 0)
                return "Unnamed";
            return "Other";
        }

        private string BuildFlags(Trk s)
        {
            var fl = new List<string>();
            string en = (s.EntryName ?? "").ToLowerInvariant();
            if (en.Contains("halt"))
                fl.Add("HaltEntry");
            if (s.Overlap)
                fl.Add("Overlap");        // another trade was open at the same time (an add or a second signal)
            if (s.SharedExit)
                fl.Add("SharedExit");     // one exit fill closed this trade together with another
            if (s.Overlap && !s.OwnSeen)
                fl.Add("NoOwnOrders");    // no stop / target of its own was seen; it follows the position's
            return string.Join(";", fl);
        }

        // Caller must hold the lock. Ends the post-exit windows of every closed trade.
        private void FinalizeAll(DateTime end)
        {
            for (int i = fins.Count - 1; i >= 0; i--)
                FinalizeFin(fins[i], end);
        }

        // Caller must hold the lock. Writes the row for a closed trade at the end of its post-exit window.
        private void FinalizeFin(Trk s, DateTime end)
        {
            if (s == null || !fins.Remove(s))
                return;

            s.PostEnd = end;

            // Continuation of the path from the segment that was in progress at the exit
            var post = new List<double[]>();
            for (int i = s.PreCount; i < s.Segs.Count; i++)
                post.Add(s.Segs[i]);
            if (s.SegDrop > 0)
                post.Add(new double[] { s.Peak, s.SegDrop, s.PeakMin });

            CultureInfo ic = CultureInfo.InvariantCulture;
            string side = s.Dir > 0 ? "Long" : "Short";

            string row = string.Join(",", new string[]
            {
                s.No.ToString(ic),
                Clean(AccountName),
                Clean(StrategyLabel),
                Instrument.FullName,
                side,
                s.Open.ToString(TimeFmt, ic),
                s.ExitTime.ToString(TimeFmt, ic),
                s.Open.Hour.ToString(ic),
                s.Open.DayOfWeek.ToString(),
                Math.Max(0, (s.ExitTime - s.Open).TotalMinutes).ToString("0.0", ic),
                s.MinToMfeAtExit.ToString("0.0", ic),
                s.MaxQty.ToString(ic),
                s.EntryFills.ToString(ic),
                s.ExitFills.ToString(ic),
                s.Partial ? "1" : "0",
                Clean(s.EntryName),
                Clean(s.ExitName),
                tickValue.ToString("0.####", ic),
                s.AtrAtEntry.ToString("0.0", ic),
                s.HaveSl   ? s.SlTicks.ToString("0.#", ic)       : "",
                s.HaveTp   ? s.TpTicks.ToString("0.#", ic)       : "",
                s.HaveStop ? s.LastStopTicks.ToString("0.#", ic) : "",
                s.Entry.ToString("F2", ic),
                s.AvgExit.ToString("F2", ic),
                s.NetTicks.ToString("0.##", ic),
                s.MfeAtExit.ToString("0.##", ic),
                s.MaeAtExit.ToString("0.##", ic),
                s.MaxToPeak.ToString("0.##", ic),
                s.AvgToPeak.ToString("0.##", ic),
                s.RetraceCount.ToString(ic),
                s.Giveback.ToString("0.##", ic),
                s.PrePath,
                Math.Max(0, (s.PostEnd - s.ExitTime).TotalMinutes).ToString("0.0", ic),
                (s.PostMaxF - s.NetTicks).ToString("0.##", ic),
                (s.NetTicks - s.PostMinF).ToString("0.##", ic),
                PathString(post),
                s.AvgEntryAll.ToString("F2", ic),
                s.NetTicksAll.ToString("0.##", ic),
                s.ExitReason,
                s.Flags,
                string.Join("|", s.Fills),
                Clean(s.ParentEntry)
            });

            if (filePath != null)
            {
                try { File.AppendAllText(filePath, row + Environment.NewLine); }
                catch (Exception ex) { Print("RetraceLogger: write failed: " + ex.Message); }
            }

            Print("RetraceLogger " + row);
        }

        // Fires on the account's thread: keeps track of which orders are currently working
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            Order o = e.Order;
            if (o == null || o.Instrument == null)
                return;
            if (o.Instrument.MasterInstrument.Name != Instrument.MasterInstrument.Name)
                return;

            lock (sync)
            {
                if (o.OrderState == OrderState.Filled || o.OrderState == OrderState.Cancelled
                    || o.OrderState == OrderState.Rejected)
                    working.Remove(o);   // its owner entry stays until the position is flat, so a late fill event can still find it
                else
                {
                    working.Add(o);
                    AssignOwner(o);
                }

                foreach (Trk t in open)
                    ApplyOrders(t);
            }
        }

        // Reads the strategy's exit orders for one open trade: the first stop seen = initial stop loss,
        // the first limit on the profit side = profit target, and the latest stop level as it trails.
        // Only the trade's own orders are read (a trade that found none after a few seconds follows the position's).
        // Caller must hold the lock (or be on the load path, before events are subscribed).
        private void ApplyOrders(Trk s)
        {
            if (tickSize <= 0)
                return;

            OrderAction exitAction = s.Dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;

            foreach (Order o in working)
            {
                if (o.OrderAction != exitAction)
                    continue;
                Trk mine;
                if (!(owner.TryGetValue(o, out mine) && mine == s) && !s.UseShared)
                    continue;

                if (o.OrderType == OrderType.StopMarket || o.OrderType == OrderType.StopLimit)
                {
                    double lvl = s.Dir * (o.StopPrice - s.Entry) / tickSize;   // negative = risk, positive = profit locked
                    if (!s.HaveSl && lvl < 0)
                    {
                        s.SlTicks = -lvl;
                        s.HaveSl = true;
                    }
                    s.LastStopTicks = lvl;
                    s.HaveStop = true;
                }
                else if (o.OrderType == OrderType.Limit)
                {
                    double d = s.Dir * (o.LimitPrice - s.Entry) / tickSize;
                    if (!s.HaveTp && d > 0)
                    {
                        s.TpTicks = d;
                        s.HaveTp = true;
                    }
                }
            }
        }

        private static string PathString(List<double[]> segs)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return string.Join("|", segs.Select(x =>
                x[0].ToString("0.##", ic) + ":" + x[1].ToString("0.##", ic) + ":" + x[2].ToString("0.#", ic)));
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar >= AtrPeriod && tickSize > 0)
                lastAtrTicks = atr[0] / tickSize;

            // Close out a post-exit window even if no ticks arrive
            lock (sync)
            {
                DateTime now = DateTime.Now;
                for (int i = fins.Count - 1; i >= 0; i--)
                    if (now >= fins[i].ExitTime.AddMinutes(PostExitMinutes))
                        FinalizeFin(fins[i], now);
            }

            if (!dirty)
                return;
            dirty = false;

            var sb = new StringBuilder();
            sb.AppendLine("Retrace log (" + AccountName + ")");

            int start = Math.Max(0, lines.Count - MaxLinesShown);
            for (int i = start; i < lines.Count; i++)
                sb.AppendLine(lines[i]);

            if (longN > 0)
                sb.AppendLine(string.Format("LONG  n={0}  avg retrace {1:0.0}t  avg max {2:0.0}t",
                    longN, longRetraceSum / longN, longMaxSum / longN));
            if (shortN > 0)
                sb.AppendLine(string.Format("SHORT n={0}  avg retrace {1:0.0}t  avg max {2:0.0}t",
                    shortN, shortRetraceSum / shortN, shortMaxSum / shortN));

            Draw.TextFixed(this, "RetraceLoggerPanel", sb.ToString(), TextPosition.TopRight);
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Account name", GroupName = "Parameters", Order = 0)]
        public string AccountName { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Strategy label (optional)", Description = "Written to every row, e.g. Strat1", GroupName = "Parameters", Order = 1)]
        public string StrategyLabel { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "Trades shown on chart", GroupName = "Parameters", Order = 2)]
        public int MaxLinesShown { get; set; }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "ATR period (logged at entry)", GroupName = "Parameters", Order = 3)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0, 240)]
        [Display(Name = "Minutes to keep tracking after exit", Description = "0 = off", GroupName = "Parameters", Order = 4)]
        public int PostExitMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Output folder", GroupName = "Parameters", Order = 5)]
        public string OutputFolder { get; set; }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "Entry window (seconds)", Description = "Entry fills within this many seconds of the first fill are one entry; the entry price is their average", GroupName = "Entry and exit tags", Order = 0)]
        public int EntryWindowSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tag time-based flatten exits", Description = "Label exits at the flatten time below as Flatten", GroupName = "Entry and exit tags", Order = 1)]
        public bool FlattenTagEnabled { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Flatten hour (PC clock)", GroupName = "Entry and exit tags", Order = 2)]
        public int FlattenHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Flatten minute", GroupName = "Entry and exit tags", Order = 3)]
        public int FlattenMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 900)]
        [Display(Name = "Flatten tolerance (seconds)", Description = "An exit this close to the flatten time (and not a stop or target) is tagged Flatten", GroupName = "Entry and exit tags", Order = 4)]
        public int FlattenToleranceSeconds { get; set; }
        #endregion
    }
}
