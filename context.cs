using System;
using System.Collections.Generic;
using System.Linq; // Still needed for Positions.Any()
using cAlgo.API;
using cAlgo.API.Collections;
// using cAlgo.API.Indicators; // No longer needed directly here if IndicatorsHelper is removed
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    /// <summary>
    /// Defines the current market context.
    /// </summary>
    public enum MarketContext
    {
        /// <summary>
        /// Undefined state.
        /// </summary>
        Undefined,

        /// <summary>
        /// Uptrend.
        /// </summary>
        TrendingUp,

        /// <summary>
        /// Downtrend.
        /// </summary>
        TrendingDown,

        /// <summary>
        /// Sideways movement (flat/consolidation).
        /// </summary>
        Ranging
    }

    /// <summary>
    /// Represents a single price bar/candle.
    /// </summary>
    public class PriceBar
    {
        public DateTime Timestamp { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public double Volume { get; set; }

        // These might be useful for other features later, but not for current context logic
        // public double TypicalPrice => (High + Low + Close) / 3;
        // public double TrueRange(PriceBar previousBar)
        // {
        //     if (previousBar == null) return High - Low;
        //     return Math.Max(High - Low, Math.Max(Math.Abs(High - previousBar.Close), Math.Abs(Low - previousBar.Close)));
        // }
    }

    /// <summary>
    /// Market context analyzer based on N-period price change.
    /// </summary>
    public class MarketContextAnalyzer
    {
        private readonly int _lookbackPeriod;
        private readonly double _thresholdInPips;

        public MarketContextAnalyzer(int lookbackPeriod, double thresholdInPips)
        {
            _lookbackPeriod = lookbackPeriod > 0 ? lookbackPeriod : 1; // Ensure lookback is at least 1
            _thresholdInPips = thresholdInPips;
        }

        /// <summary>
        /// Defines the current market context based on N-period price change.
        /// </summary>
        /// <param name="priceBars">List of historical price data (candles/bars).</param>
        /// <param name="pipSize">The pip size for the current symbol.</param>
        /// <returns>Current MarketContext.</returns>
        public MarketContext GetContext(IList<PriceBar> priceBars, double pipSize)
        {
            if (priceBars == null || priceBars.Count < _lookbackPeriod + 1)
            {
                //Console.WriteLine("Not enough data to determine context.");
                return MarketContext.Undefined;
            }
            if (pipSize <= 0)
            {
                //Console.WriteLine("Pip size is invalid, cannot determine context.");
                return MarketContext.Undefined; 
            }

            PriceBar currentBar = priceBars.Last();
            PriceBar lookbackBar = priceBars[priceBars.Count - 1 - _lookbackPeriod];

            double priceChange = currentBar.Close - lookbackBar.Close;
            double priceChangeInPips = priceChange / pipSize;

            if (priceChangeInPips > _thresholdInPips)
            {
                return MarketContext.TrendingUp;
            }
            else if (priceChangeInPips < -_thresholdInPips)
            {
                return MarketContext.TrendingDown;
            }
            else
            {
                return MarketContext.Ranging;
            }
        }
    }


    [Robot(AccessRights = AccessRights.None, AddIndicators = true)] // AddIndicators might not be strictly needed now
    public class context : Robot
    {
        [Parameter("MACD Fast EMA Period", DefaultValue = 12, MinValue = 2, Group = "Signal Indicator")]
        public int MacdFastEmaPeriod { get; set; }

        [Parameter("MACD Slow EMA Period", DefaultValue = 26, MinValue = 5, Group = "Signal Indicator")]
        public int MacdSlowEmaPeriod { get; set; }

        [Parameter("MACD Signal Period", DefaultValue = 9, MinValue = 2, Group = "Signal Indicator")]
        public int MacdSignalPeriod { get; set; }

        [Parameter("Trend EMA Period", DefaultValue = 200, MinValue = 20, Group = "Trend Filter")]
        public int EmaPeriod { get; set; }

        [Parameter("ADX Period", DefaultValue = 14, MinValue = 2, Group = "Trend Filter")]
        public int AdxPeriod { get; set; }

        [Parameter("ADX Threshold", DefaultValue = 20, MinValue = 0, MaxValue = 50, Group = "Trend Filter")]
        public double AdxThreshold { get; set; }

        [Parameter("Risk % Per Trade", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 1.0, Step = 0.1, Group = "Management")]
        public double RiskPercentage { get; set; }

        [Parameter("Stop Loss (Pips)", DefaultValue = 20, MinValue = 1, Group = "Management")]
        public int StopLossInPips { get; set; }

        [Parameter("Take Profit (Pips)", DefaultValue = 40, MinValue = 1, Group = "Management")]
        public int TakeProfitInPips { get; set; }

        [Parameter("Trailing Stop (Pips)", DefaultValue = 0, MinValue = 1, Group = "Management")] // 0 to disable
        public int TrailingStopPips { get; set; }

        [Parameter("Trade Label", DefaultValue = "MarketTrendBot_v2", Group = "Management")]
        public string TradeLabel { get; set; }
        
        private DateTime _lastTradeDate = DateTime.MinValue;
        private readonly TimeSpan _tradeStartTime = new TimeSpan(9, 0, 0); // 09:00
        private readonly TimeSpan _tradeEndTime = new TimeSpan(15, 0, 0);   // 15:00
        private const int UtcOffsetHours = 3; // For UTC+3, assuming Server.Time is UTC
        private MacdHistogram _macd;
        private ExponentialMovingAverage _emaTrend;
        private AverageDirectionalMovementIndexRating _adx;


        protected override void OnStart()
        {
            _macd = Indicators.MacdHistogram(MacdFastEmaPeriod, MacdSlowEmaPeriod, MacdSignalPeriod);
            _emaTrend = Indicators.ExponentialMovingAverage(MarketData.GetBars(TimeFrame).ClosePrices, EmaPeriod);
            _adx = Indicators.AverageDirectionalMovementIndexRating(AdxPeriod);
            Print("MACD Bot with EMA and ADX filters Started.");
        }

        protected override void OnBar()
        {
            var serverTimeUtc = Server.Time;
            var exchangeTime = serverTimeUtc.AddHours(UtcOffsetHours);
            if (exchangeTime.TimeOfDay < _tradeStartTime || exchangeTime.TimeOfDay >= _tradeEndTime)
            {
                Print($"Current exchange time {exchangeTime:HH:mm:ss} (UTC+{UtcOffsetHours}) is outside trading hours ({_tradeStartTime} - {_tradeEndTime}). No new positions.");
                return;
            }

            if (serverTimeUtc.Date == _lastTradeDate.Date)
            {
                Print($"One trade limit for {serverTimeUtc.Date:yyyy-MM-dd} already reached. No new positions.");
                return;
            }

            bool hasOpenPosition = Positions.Any(p => p.Label == TradeLabel && p.SymbolName == Symbol.Name);
            if (hasOpenPosition)
            {
                Print($"An open position with label '{TradeLabel}' already exists. No new entry.");
                return;
            }

            // Check for sufficient data for all indicators
            if (_macd.Signal.Count < 2 || _macd.Histogram.Count < 2 || _emaTrend.Result.Count < 1 || _adx.ADX.Count < 1) // Ensure all parts of MACD needed for calculation have data
            {
                Print("Not enough data for MACD (Signal/Histogram), EMA, or ADX indicators yet.");
                return;
            }

            bool shouldAttemptTrade = false;
            TradeType tradeType = TradeType.Buy; // Default, will be overwritten

            // Trend Filter Logic
            double currentClose = Bars.Last(0).Close;
            double currentEma = _emaTrend.Result.LastValue;
            double currentAdx = _adx.ADX.LastValue; // ADX.LastValue should give the main ADX line

            bool isTrendStrongEnough = currentAdx >= AdxThreshold;
            bool isUptrendGlobal = currentClose > currentEma;
            bool isDowntrendGlobal = currentClose < currentEma;

            if (!isTrendStrongEnough)
            {
                Print($"ADX ({currentAdx:F2}) is below threshold ({AdxThreshold}). No trade due to weak trend.");
            }
            else
            {
                // MACD Crossover Logic (Signal)
                // MACD Line = Histogram + Signal Line
                double currentMacd = _macd.Histogram.Last(0) + _macd.Signal.Last(0);
                double previousMacd = _macd.Histogram.Last(1) + _macd.Signal.Last(1);
                double currentSignal = _macd.Signal.Last(0);
                double previousSignal = _macd.Signal.Last(1);

                // Buy signal: MACD crosses above Signal line, in an uptrend, and trend is strong
                if (isUptrendGlobal && previousMacd < previousSignal && currentMacd > currentSignal)
                {
                    shouldAttemptTrade = true;
                    tradeType = TradeType.Buy;
                    Print($"MACD Buy Signal CONFIRMED by EMA & ADX: Price ({currentClose}) > EMA ({currentEma:F5}), ADX ({currentAdx:F2}) >= {AdxThreshold}. MACD ({currentMacd:F5}) crossed Signal ({currentSignal:F5}).");
                }
                // Sell signal: MACD crosses below Signal line, in a downtrend, and trend is strong
                else if (isDowntrendGlobal && previousMacd > previousSignal && currentMacd < currentSignal)
                {
                    shouldAttemptTrade = true;
                    tradeType = TradeType.Sell;
                    Print($"MACD Sell Signal CONFIRMED by EMA & ADX: Price ({currentClose}) < EMA ({currentEma:F5}), ADX ({currentAdx:F2}) >= {AdxThreshold}. MACD ({currentMacd:F5}) crossed Signal ({currentSignal:F5}).");
                }
                else
                {
                    Print($"No MACD Crossover or trend conditions not met. Price={currentClose}, EMA={currentEma:F5}, ADX={currentAdx:F2}, MACD Line={currentMacd:F5}, Signal Line={currentSignal:F5}, Prev MACD={previousMacd:F5}, Prev Signal={previousSignal:F5}");
                }
            }
            
            if (shouldAttemptTrade)
            {
                if (StopLossInPips <= 0)
                {
                    Print("StopLossInPips must be greater than 0 to calculate volume based on risk. No trade.");
                    return;
                }
                 if (Symbol.PipValue <= 0) 
                {
                    Print($"Symbol.PipValue ({Symbol.PipValue}) is zero or negative. Cannot calculate trade volume. No trade.");
                    return;
                }
                 if (Symbol.PipSize <= 0)
                 {
                    Print($"Symbol.PipSize ({Symbol.PipSize}) is zero or negative. No trade.");
                    return;
                 }

                double riskAmountInAccountCurrency = Account.Balance * (RiskPercentage / 100.0);
                double riskPerPipPerUnit = Symbol.PipValue;
                double totalRiskValueForSlPerUnit = StopLossInPips * riskPerPipPerUnit;

                if (totalRiskValueForSlPerUnit <= 0)
                {
                    Print($"Total risk value for SL per unit ({totalRiskValueForSlPerUnit}) is zero or negative. SLPips: {StopLossInPips}, PipValue: {Symbol.PipValue}. No trade.");
                    return;
                }

                double positionSizeInUnits_raw = riskAmountInAccountCurrency / totalRiskValueForSlPerUnit;
                
                if (Symbol.VolumeInUnitsStep <= 0)
                {
                    Print("Symbol.VolumeInUnitsStep is zero or negative. Cannot normalize volume. No trade.");
                    return;
                }

                double normalizedVolumeInUnits = Math.Floor(positionSizeInUnits_raw / Symbol.VolumeInUnitsStep) * Symbol.VolumeInUnitsStep;

                if (normalizedVolumeInUnits < Symbol.VolumeInUnitsMin)
                {
                    Print($"Calc norm. volume {normalizedVolumeInUnits} units < min {Symbol.VolumeInUnitsMin} units. Using min volume.");
                    normalizedVolumeInUnits = Symbol.VolumeInUnitsMin;
                    
                    double costOfMinVolumeSl = normalizedVolumeInUnits * totalRiskValueForSlPerUnit;
                    if (costOfMinVolumeSl > riskAmountInAccountCurrency && riskAmountInAccountCurrency > 0)
                    {
                         Print($"Cannot afford min vol {Symbol.VolumeInUnitsMin} units with risk {RiskPercentage}% & SL {StopLossInPips} pips. Risk: {riskAmountInAccountCurrency}, Cost: {costOfMinVolumeSl}. No trade.");
                         return;
                    }
                }
                
                if (normalizedVolumeInUnits > Symbol.VolumeInUnitsMax)
                {
                    Print($"Calc norm. volume {normalizedVolumeInUnits} units > max {Symbol.VolumeInUnitsMax} units. Using max volume.");
                    normalizedVolumeInUnits = Symbol.VolumeInUnitsMax;
                }

                if (normalizedVolumeInUnits <= 0)
                {
                    Print($"Final volume in units is zero or negative ({normalizedVolumeInUnits}). Cannot trade.");
                    return;
                }

                double finalVolumeInUnits = normalizedVolumeInUnits;
                double finalVolumeInLots = Symbol.VolumeInUnitsToQuantity(finalVolumeInUnits);
                
                Print($"Attempting to open {tradeType} position (MACD Crossover + EMA/ADX confirmation): Volume={finalVolumeInLots} lots ({finalVolumeInUnits} units), SL={StopLossInPips} pips, TP={TakeProfitInPips} pips, Risk={RiskPercentage}% ({riskAmountInAccountCurrency} {Account.Asset.Name})");
                
                var result = ExecuteMarketOrder(tradeType, Symbol.Name, finalVolumeInUnits, TradeLabel, StopLossInPips, TakeProfitInPips);

                if (result.IsSuccessful)
                {
                    _lastTradeDate = serverTimeUtc.Date; 
                    Print($"Trade executed successfully. Position ID: {result.Position.Id}, Price: {result.Position.EntryPrice}, Volume: {result.Position.VolumeInUnits} units");
                }
                else
                {
                    Print($"Failed to execute trade: {result.Error}");
                }
            }

            ManageTrailingStops();
        }

        protected override void OnTick()
        {
            // ManageTrailingStops(); // Potentially call here if needed, with careful consideration
        }

        protected override void OnStop()
        {
            Print("cBot stopped.");
        }

        private void ManageTrailingStops()
        {
            if (TrailingStopPips <= 0) return; // Trailing stop is disabled

            foreach (var position in Positions)
            {
                if (position.SymbolName == Symbol.Name && position.Label == TradeLabel)
                {
                    if (position.TradeType == TradeType.Buy)
                    {
                        double newStopLossPrice = Math.Round(Symbol.Bid - TrailingStopPips * Symbol.PipSize, Symbol.Digits);
                        if (newStopLossPrice > position.EntryPrice && 
                            (!position.StopLoss.HasValue || newStopLossPrice > position.StopLoss.Value))
                        {
                            var modifyResult = ModifyPosition(position, newStopLossPrice, position.TakeProfit);
                            if (modifyResult.IsSuccessful)
                            {
                                Print($"Trailing Stop for BUY Position #{position.Id} moved to {newStopLossPrice}");
                            }
                            else
                            {
                                Print($"Error modifying BUY Position #{position.Id} for trailing stop: {modifyResult.Error}");
                            }
                        }
                    }
                    else if (position.TradeType == TradeType.Sell)
                    {
                        double newStopLossPrice = Math.Round(Symbol.Ask + TrailingStopPips * Symbol.PipSize, Symbol.Digits);
                        if (newStopLossPrice < position.EntryPrice && 
                            (!position.StopLoss.HasValue || newStopLossPrice < position.StopLoss.Value))
                        {
                            var modifyResult = ModifyPosition(position, newStopLossPrice, position.TakeProfit);
                            if (modifyResult.IsSuccessful)
                            {
                                Print($"Trailing Stop for SELL Position #{position.Id} moved to {newStopLossPrice}");
                            }
                            else
                            {
                                Print($"Error modifying SELL Position #{position.Id} for trailing stop: {modifyResult.Error}");
                            }
                        }
                    }
                }
            }
        }
    }
}


