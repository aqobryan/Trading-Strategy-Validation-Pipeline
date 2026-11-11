# NinjaTrader Trade Loggers

Two NinjaScript (C#) indicators for NinjaTrader 8 that record every trade on an account to CSV, so automated or manual trading can be analyzed offline in Python, Excel or anything else.

- **TradeLogger** writes one row per closed trade: entry/exit times, size, average prices, gross ticks and gross P&L.
- **RetraceLogger** writes one row per trade with the trade's full price path: how far it went for you (MFE) and against you (MAE), every pullback from a new high, the bot's own stop and target distances, why the trade ended, and what price did for up to 90 minutes after the exit.

Together they answer questions a broker statement can't: *Would a trailing stop have helped? How much do winners pull back before paying? Which trades were ahead before they lost? Which hours, days or news windows perform worst?*

## Features

**TradeLogger**
- Logs every closed trade (flat or reversal) with millisecond timestamps.
- Gross P&L before commissions, in ticks and dollars.
- On-chart panel with recent trades, win rate and running profit factor.

**RetraceLogger**
- Full excursion path per trade (`peak:drop:minute` segments), MFE/MAE, largest and average pullback, giveback.
- Reads the strategy's working orders to record the initial stop (`SL_Ticks`), target (`TP_Ticks`) and last stop level (`LastStopTicks`, showing whether the stop had moved into profit).
- Exit classification: `StopLoss`, `TrailStop`, `Target`, `SignalClose`, `Flatten`, `AccountHalt`, `Unnamed`, `Other`.
- Scale-ins: fills within a configurable entry window (default 60 s) are one entry; later same-direction fills (adds) get their own row, linked to the parent trade (`ParentEntry`), with flags such as `Overlap` and `SharedExit`.
- Post-exit tracking: how much further price ran in the trade's favor or against it after the exit.
- ATR at entry, entry hour and day of week for time-of-day analysis.
- If the CSV layout changes, the old file is archived (`_old_<timestamp>.csv`) instead of mixing formats.

Both indicators only **read** account data. They never place, change or cancel orders.

## Installation

1. Copy `Indicators/TradeLogger.cs` and `Indicators/RetraceLogger.cs` into
   `Documents\NinjaTrader 8\bin\Custom\Indicators\`.
2. In NinjaTrader, open **New > NinjaScript Editor** and press **F5** to compile.
3. Add each indicator to a chart of the instrument you trade, and set **Account name** (default `Sim101`).
4. Add one instance per account. Each account and instrument gets its own CSV.

Output folder (configurable): `Documents\NinjaTrader 8\TradeLogs\`
- `TradeLog_<Account>_<Instrument>.csv`
- `RetraceLog_<Account>_<Instrument>.csv`

Match the two files on `EntryTime`. Times use the PC's local clock, so note the time zone if you run on a VPS.

## CSV columns

**TradeLog:** `TradeNo, Account, Strategy, Contract, Direction, EntryTime, ExitTime, Qty, AvgEntry, AvgExit, GrossTicks, GrossPnL, EntryName, ExitName, StartedBeforeLoad`

**RetraceLog:** `TradeNo, Account, Strategy, Contract, Direction, EntryTime, ExitTime, EntryHour, DayOfWeek, MinutesInTrade, MinutesToMFE, Qty, EntryFills, ExitFills, StartedBeforeLoad, EntryName, ExitName, TickValue, AtrTicksAtEntry, SL_Ticks, TP_Ticks, LastStopTicks, AvgEntry, AvgExit, NetTicks, MFE_Ticks, MAE_Ticks, MaxRetraceToPeak, AvgRetraceToPeak, RetraceCount, GivebackTicks, Path, PostMinutesTracked, PostRunTicks, PostAdverseTicks, PostPath, AvgEntryAll, NetTicksAll, ExitReason, Flags, Fills, ParentEntry`

All tick figures are per contract and measured in the trade's favor (positive = profit). Full definitions are in the comments at the top of each `.cs` file.

## Notes

- P&L is gross. Subtract your own commissions and fees.
- Excursions for a position that was already open when the indicator loaded are measured from load time (`StartedBeforeLoad = 1`).
- Add the indicators while flat, and recompile after updating.
- Tested in live use on NinjaTrader 8 with an automated MNQ futures strategy. Order-attribution logic for overlapping trades was also unit-tested against stand-in NinjaTrader types.

## License

MIT. See `LICENSE`.
