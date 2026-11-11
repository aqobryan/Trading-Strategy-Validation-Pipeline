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

// TradeLogger
// Watches fills on one account for the chart's instrument. Every time a trade closes
// (position returns to flat, or reverses) it appends one row to a CSV file:
//
//   TradeNo, Account, Strategy, Contract, Direction, EntryTime, ExitTime, Qty,
//   AvgEntry, AvgExit, GrossTicks, GrossPnL, EntryName, ExitName, StartedBeforeLoad
//
// GrossPnL is in dollars BEFORE commissions. Times are your PC's local clock, to the millisecond,
// in the same format RetraceLogger uses, so the two files can be matched on EntryTime.
// A small bottom-right panel shows the latest trades plus win rate and a running (gross) profit factor.
//
// Add one instance per account. Each account gets its own CSV file.

namespace NinjaTrader.NinjaScript.Indicators
{
    public class TradeLogger : Indicator
    {
        private const string TimeFmt = "yyyy-MM-dd HH:mm:ss.fff";

        private Account account;
        private readonly object sync = new object();
        private readonly HashSet<string> seenExec = new HashSet<string>();

        // Open-trade tracking
        private int pos;
        private int tradeDir;
        private double avg;
        private DateTime openTime;
        private double realized;          // gross P&L in currency, before commissions
        private int exitQty;
        private double exitValue;
        private int maxQty;
        private int entryFills;
        private int exitFills;
        private string entryName = "";
        private string exitName = "";
        private bool startedBeforeLoad;
        private int tradeCount;

        // Running stats + panel
        private readonly List<string> lines = new List<string>();
        private double grossProfit, grossLoss;
        private int wins;
        private bool dirty;
        private string filePath;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description         = "Logs every closed trade to CSV: gross P&L (before commissions) and times.";
                Name                = "TradeLogger";
                Calculate           = Calculate.OnPriceChange;
                IsOverlay           = true;
                DisplayInDataBox    = false;
                PaintPriceMarkers   = false;

                AccountName         = "Sim101";
                StrategyLabel       = "";
                MaxLinesShown       = 6;
                OutputFolder        = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "TradeLogs");
            }
            else if (State == State.DataLoaded)
            {
                lock (Account.All)
                    account = Account.All.FirstOrDefault(a => a.Name == AccountName);

                if (account == null)
                {
                    Print("TradeLogger: account '" + AccountName + "' not found.");
                    return;
                }

                try
                {
                    Directory.CreateDirectory(OutputFolder);
                    string safeAcct = new string(AccountName.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
                    filePath = Path.Combine(OutputFolder,
                        "TradeLog_" + safeAcct + "_" + Instrument.MasterInstrument.Name + ".csv");

                    const string header =
                        "TradeNo,Account,Strategy,Contract,Direction,EntryTime,ExitTime,Qty," +
                        "AvgEntry,AvgExit,GrossTicks,GrossPnL,EntryName,ExitName,StartedBeforeLoad";

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
                    Print("TradeLogger: could not prepare log file: " + ex.Message);
                    filePath = null;
                }

                // Pick up a position that is already open when the indicator loads
                // (flagged StartedBeforeLoad; entry time is then the load time).
                lock (account.Positions)
                {
                    foreach (Position p in account.Positions)
                    {
                        if (p.Instrument.MasterInstrument.Name == Instrument.MasterInstrument.Name
                            && p.MarketPosition != MarketPosition.Flat)
                        {
                            tradeDir = p.MarketPosition == MarketPosition.Long ? 1 : -1;
                            pos      = tradeDir * p.Quantity;
                            avg      = p.AveragePrice;
                            openTime = DateTime.Now;
                            maxQty   = p.Quantity;
                            entryFills = 1;
                            startedBeforeLoad = true;
                        }
                    }
                }

                account.ExecutionUpdate += OnExecutionUpdate;
            }
            else if (State == State.Terminated)
            {
                if (account != null)
                    account.ExecutionUpdate -= OnExecutionUpdate;
            }
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace(",", " ").Replace("\r", " ").Replace("\n", " ").Trim();
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
                    return;   // already processed

                OrderAction a = ex.Order.OrderAction;
                int sgn = (a == OrderAction.Buy || a == OrderAction.BuyToCover) ? 1 : -1;
                HandleFill(sgn, ex.Quantity, ex.Price, ex.Time, ex.Order.Name);
            }
        }

        private void HandleFill(int sgn, int qty, double price, DateTime time, string orderName)
        {
            if (qty <= 0)
                return;

            double pv = Instrument.MasterInstrument.PointValue;
            int remaining = qty;

            // Part of the fill that closes (or reduces) the open position
            if (pos != 0 && Math.Sign(pos) != sgn)
            {
                int closing = Math.Min(qty, Math.Abs(pos));

                realized  += (price - avg) * Math.Sign(pos) * closing * pv;
                exitQty   += closing;
                exitValue += price * closing;
                exitFills++;
                exitName   = orderName;
                pos       += sgn * closing;
                remaining -= closing;

                if (pos == 0)
                    CloseTrade(time);
            }

            // Part of the fill that opens a new position or adds to the current one
            if (remaining > 0)
            {
                if (pos == 0)
                {
                    tradeDir   = sgn;
                    avg        = price;
                    pos        = sgn * remaining;
                    openTime   = time;
                    realized   = 0;
                    exitQty    = 0;
                    exitValue  = 0;
                    maxQty     = remaining;
                    entryFills = 1;
                    exitFills  = 0;
                    entryName  = orderName;
                    exitName   = "";
                    startedBeforeLoad = false;
                }
                else
                {
                    avg = (avg * Math.Abs(pos) + price * remaining) / (Math.Abs(pos) + remaining);
                    pos += sgn * remaining;
                    maxQty = Math.Max(maxQty, Math.Abs(pos));
                    entryFills++;
                }
            }
        }

        private void CloseTrade(DateTime exitTime)
        {
            tradeCount++;
            CultureInfo ic = CultureInfo.InvariantCulture;

            double tickSize = Instrument.MasterInstrument.TickSize;
            double avgExit  = exitQty > 0 ? exitValue / exitQty : avg;
            double ticks    = tickSize > 0 ? tradeDir * (avgExit - avg) / tickSize : 0;
            double gross    = realized;

            if (gross > 0) { grossProfit += gross; wins++; }
            else if (gross < 0) grossLoss += -gross;

            string side = tradeDir > 0 ? "Long" : "Short";

            string row = string.Join(",", new string[]
            {
                tradeCount.ToString(ic),
                Clean(AccountName),
                Clean(StrategyLabel),
                Instrument.FullName,
                side,
                openTime.ToString(TimeFmt, ic),
                exitTime.ToString(TimeFmt, ic),
                maxQty.ToString(ic),
                avg.ToString("F2", ic),
                avgExit.ToString("F2", ic),
                ticks.ToString("0.##", ic),
                gross.ToString("F2", ic),
                Clean(entryName),
                Clean(exitName),
                startedBeforeLoad ? "1" : "0"
            });

            if (filePath != null)
            {
                try { File.AppendAllText(filePath, row + Environment.NewLine); }
                catch (Exception ex) { Print("TradeLogger: write failed: " + ex.Message); }
            }

            lines.Add(string.Format("#{0} {1} {2:HH:mm}  {3:+0.00;-0.00}",
                tradeCount, tradeDir > 0 ? "L" : "S", openTime, gross));

            Print("TradeLogger " + row);
            dirty = true;
        }

        protected override void OnBarUpdate()
        {
            if (!dirty)
                return;
            dirty = false;

            var sb = new StringBuilder();
            sb.AppendLine("Trade log (" + AccountName + ")");

            int start = Math.Max(0, lines.Count - MaxLinesShown);
            for (int i = start; i < lines.Count; i++)
                sb.AppendLine(lines[i]);

            string pf = grossLoss > 0 ? (grossProfit / grossLoss).ToString("F2", CultureInfo.InvariantCulture) : "n/a";
            double winPct = tradeCount > 0 ? 100.0 * wins / tradeCount : 0;
            sb.AppendLine(string.Format("Trades {0}  Win {1:F0}%  PF {2} (gross)", tradeCount, winPct, pf));

            Draw.TextFixed(this, "TradeLoggerPanel", sb.ToString(), TextPosition.BottomRight);
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
        [Display(Name = "Output folder", GroupName = "Parameters", Order = 3)]
        public string OutputFolder { get; set; }
        #endregion
    }
}
