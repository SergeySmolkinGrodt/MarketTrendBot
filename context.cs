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

        [Parameter("Context Lookback Period", DefaultValue = 10, MinValue = 1, Group = "Context Analysis")]
        public int ContextLookbackPeriod { get; set; }

        [Parameter("Context Threshold (Pips)", DefaultValue = 20, MinValue = 1, Group = "Context Analysis")]
        public double ContextThresholdInPips { get; set; }

        [Parameter("RSI Buy Threshold", DefaultValue = 55, MinValue = 30, MaxValue = 70, Group = "Context Analysis")]
        public double RsiBuyThreshold { get; set; }

        [Parameter("RSI Sell Threshold", DefaultValue = 45, MinValue = 30, MaxValue = 50, Group = "Context Analysis")]
        public double RsiSellThreshold { get; set; }

        [Parameter("RSI Period", DefaultValue = 14, MinValue = 2, Group = "Context Analysis")]
        public int RsiPeriod { get; set; }

        [Parameter("Max Historical Bars", DefaultValue = 500, MinValue = 50, MaxValue = 1000, Group = "Context Analysis")]
        public int MaxHistoricalBars { get; set; }

        [Parameter("Risk % Per Trade", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 1.0, Step = 0.1, Group = "Management")]
        public double RiskPercentage { get; set; }

        [Parameter("Stop Loss (Pips)", DefaultValue = 20, MinValue = 1, Group = "Management")]
        public int StopLossInPips { get; set; }

        [Parameter("Take Profit (Pips)", DefaultValue = 40, MinValue = 1, Group = "Management")]
        public int TakeProfitInPips { get; set; }

        [Parameter("Trailing Stop (Pips)", DefaultValue = 0, MinValue = 0, Group = "Management")] // 0 to disable
        public int TrailingStopPips { get; set; }

        [Parameter("Trade Label", DefaultValue = "MarketTrendBot_v2", Group = "Management")]
        public string TradeLabel { get; set; }

        
        private MarketContextAnalyzer _analyzer;
        private List<PriceBar> _historicalBars;
        private DateTime _lastTradeDate = DateTime.MinValue;
        private readonly TimeSpan _tradeStartTime = new TimeSpan(9, 0, 0); // 09:00
        private readonly TimeSpan _tradeEndTime = new TimeSpan(15, 0, 0);   // 15:00
        private const int UtcOffsetHours = 3; // For UTC+3, assuming Server.Time is UTC
        private RelativeStrengthIndex _rsi;


        protected override void OnStart()
        {
        
            _analyzer = new MarketContextAnalyzer(ContextLookbackPeriod, ContextThresholdInPips);
            _historicalBars = new List<PriceBar>();
            _rsi = Indicators.RelativeStrengthIndex(MarketData.GetBars(TimeFrame).ClosePrices, RsiPeriod);

            var history = MarketData.GetBars(TimeFrame, Symbol.Name);
            foreach (var bar in history)
            {
                 var priceBar = new PriceBar
                {
                    Timestamp = bar.OpenTime,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.TickVolume
                };
                _historicalBars.Add(priceBar);
                if (_historicalBars.Count > MaxHistoricalBars) // Ensure _maxBars is sufficient for longest lookback
                {
                    _historicalBars.RemoveAt(0);
                }
            }
            Print($"Loaded {history.Count} historical bars. Using last {_historicalBars.Count} for analysis.");
            Print($"Ensure MaxHistoricalBars ({MaxHistoricalBars}) is greater than ContextLookbackPeriod ({ContextLookbackPeriod}).");


            if (_historicalBars.Count > ContextLookbackPeriod) // Need LookbackPeriod + 1 bars for first calculation
            {
                MarketContext initialContext = _analyzer.GetContext(_historicalBars, Symbol.PipSize);
                Print($"Initial Market Context: {initialContext}");
            }
            else
            {
                Print("Not enough historical data on start to determine context.");
            }
        }

        protected override void OnBar()
        {
            var newBar = Bars.Last(1);
            var priceBar = new PriceBar
            {
                Timestamp = newBar.OpenTime,
                Open = newBar.Open,
                High = newBar.High,
                Low = newBar.Low,
                Close = newBar.Close,
                Volume = newBar.TickVolume
            };

            if (_historicalBars.Count == 0 || _historicalBars.Last().Timestamp != priceBar.Timestamp)
            {
                _historicalBars.Add(priceBar);
                if (_historicalBars.Count > MaxHistoricalBars)
                {
                    _historicalBars.RemoveAt(0);
                }
            }

            if (_historicalBars.Count <= ContextLookbackPeriod) // Check if enough data for lookback (needs N+1 bars)
            {
                Print($"Not enough data to determine context. Bars collected: {_historicalBars.Count}, Need: {ContextLookbackPeriod + 1}");
                return;
            }

            MarketContext currentContext = _analyzer.GetContext(_historicalBars, Symbol.PipSize);
            Print($"Server Time (UTC): {Server.Time:HH:mm:ss}, Bar Time: {newBar.OpenTime:HH:mm:ss}, Context: {currentContext}, PipChangeThreshold: {ContextThresholdInPips} over {ContextLookbackPeriod} bars");

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

            // Determine if a trade should be attempted based on market context
            bool shouldAttemptTrade = false;
            if (currentContext == MarketContext.TrendingUp || currentContext == MarketContext.TrendingDown)
            {
                // RSI Filter Logic
                double currentRsi = _rsi.Result.LastValue;
                if (currentContext == MarketContext.TrendingUp)
                {
                    if (currentRsi > RsiBuyThreshold)
                    {
                        shouldAttemptTrade = true;
                        Print($"RSI ({currentRsi:F2}) > Buy Threshold ({RsiBuyThreshold}). Allowing BUY.");
                    }
                    else
                    {
                        Print($"RSI ({currentRsi:F2}) <= Buy Threshold ({RsiBuyThreshold}). Filtering BUY signal.");
                    }
                }
                else // TrendingDown
                {
                    if (currentRsi < RsiSellThreshold)
                    {
                        shouldAttemptTrade = true;
                        Print($"RSI ({currentRsi:F2}) < Sell Threshold ({RsiSellThreshold}). Allowing SELL.");
                    }
                    else
                    {
                        Print($"RSI ({currentRsi:F2}) >= Sell Threshold ({RsiSellThreshold}). Filtering SELL signal.");
                    }
                }
            }
            else if (currentContext == MarketContext.Ranging)
            {
                 Print("Market is RANGING. No new positions will be opened.");
            }
             else // Undefined
            {
                 Print("Market context is UNDEFINED. No new positions will be opened.");
            }


            if (shouldAttemptTrade) // This flag is true if context was TrendingUp/Down
            {
                if (StopLossInPips <= 0)
                {
                    Print("StopLossInPips must be greater than 0 to calculate volume based on risk. No trade.");
                    return;
                }
                 if (Symbol.PipValue <= 0) // PipValue needed for risk calc, PipSize for context calc
                {
                    Print($"Symbol.PipValue ({Symbol.PipValue}) is zero or negative. Cannot calculate trade volume. No trade.");
                    return;
                }
                 if (Symbol.PipSize <= 0)
                 {
                    Print($"Symbol.PipSize ({Symbol.PipSize}) is zero or negative. Cannot calculate pips for context. No trade.");
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

                TradeType tradeType = (currentContext == MarketContext.TrendingUp) ? TradeType.Buy : TradeType.Sell;
                Print($"Attempting to open {tradeType} position: Context={currentContext}, Volume={finalVolumeInLots} lots ({finalVolumeInUnits} units), SL={StopLossInPips} pips, TP={TakeProfitInPips} pips, Risk={RiskPercentage}% ({riskAmountInAccountCurrency} {Account.Asset.Name})");
                
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

            ManageTrailingStops(); // Call method to manage trailing stops
        }

        protected override void OnTick()
        {
            // We are using OnBar for context analysis based on closed candles.
            // If more responsive trailing stop is needed, part of ManageTrailingStops logic could be moved here,
            // but be mindful of an increased number of modification requests.
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
                        // Ensure new SL is higher than entry price to start trailing
                        // And also ensure new SL is higher than current SL (if any)
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
                        // Ensure new SL is lower than entry price to start trailing
                        // And also ensure new SL is lower than current SL (if any)
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

