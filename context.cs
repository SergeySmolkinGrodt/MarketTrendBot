using System;
using System.Collections.Generic;
using System.Linq; // Still needed for Positions.Any()
using cAlgo.API;
using cAlgo.API.Collections;
// using cAlgo.API.Indicators; // No longer needed directly here if IndicatorsHelper is removed
using cAlgo.API.Internals;

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
        [Parameter("Message", DefaultValue = "Hello world!")]
        public string Message { get; set; }

        [Parameter("Context Lookback Period", DefaultValue = 10, MinValue = 1, Group = "Context Analysis")]
        public int ContextLookbackPeriod { get; set; }

        [Parameter("Context Threshold (Pips)", DefaultValue = 20, MinValue = 1, Group = "Context Analysis")]
        public double ContextThresholdInPips { get; set; }

        [Parameter("Risk % Per Trade", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 1.0, Step = 0.1, Group = "Trading")]
        public double RiskPercentage { get; set; }

        [Parameter("Stop Loss (Pips)", DefaultValue = 20, MinValue = 1, Group = "Trading")]
        public int StopLossInPips { get; set; }

        [Parameter("Take Profit (Pips)", DefaultValue = 40, MinValue = 1, Group = "Trading")]
        public int TakeProfitInPips { get; set; }

        [Parameter("Trade Label", DefaultValue = "MarketTrendBot_v2", Group = "Trading")]
        public string TradeLabel { get; set; }

        [Parameter("Fractal Reaction %", DefaultValue = 0.20, MinValue = 0.01, Group = "Fractal Entry")]
        public double FractalReactionPercentage { get; set; }

        [Parameter("Fractal Window Size", DefaultValue = 1, MinValue = 1, MaxValue = 2, Step = 1, Group = "Fractal Entry")] // e.g., 1 means 1 bar on each side (3-bar fractal)
        public int FractalWindowSize { get; set; }

        [Parameter("Fractal Reaction Timeout (Mins)", DefaultValue = 5, MinValue = 1, MaxValue = 30, Group = "Fractal Entry")]
        public int FractalReactionTimeoutMinutes { get; set; }


        private MarketContextAnalyzer _analyzer;
        private List<PriceBar> _historicalBars;
        private const int _maxBars = 200; // Store a maximum of 200 bars for analysis, can be adjusted

        private DateTime _lastTradeDate = DateTime.MinValue;
        private readonly TimeSpan _tradeStartTime = new TimeSpan(9, 0, 0); // 09:00
        private readonly TimeSpan _tradeEndTime = new TimeSpan(15, 0, 0);   // 15:00
        private const int UtcOffsetHours = 3; // For UTC+3, assuming Server.Time is UTC

        // State variables for fractal entry logic
        private double? _activeH1FractalUpLevel;    // Stores the level of the last identified H1 Up-Fractal to be broken
        private double? _activeH1FractalDownLevel;  // Stores the level of the last identified H1 Down-Fractal to be broken
        private bool _waitingForUpReaction;         // Flag: true if H1 Up-Fractal was broken, waiting for price reaction upwards
        private bool _waitingForDownReaction;       // Flag: true if H1 Down-Fractal was broken, waiting for price reaction downwards
        private double _breakoutConfirmationPrice;  // Stores the close price of the bar that broke the fractal level
        private double _targetReactionPrice;        // Stores the calculated target price for reaction confirmation
        private DateTime _reactionWaitStartTime;    // Stores the timestamp when we started waiting for a reaction


        protected override void OnStart()
        {
            Print(Message);
            _analyzer = new MarketContextAnalyzer(ContextLookbackPeriod, ContextThresholdInPips);
            _historicalBars = new List<PriceBar>();

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
                if (_historicalBars.Count > _maxBars) // Ensure _maxBars is sufficient for longest lookback
                {
                    _historicalBars.RemoveAt(0);
                }
            }
            Print($"Loaded {history.Count} historical bars. Using last {_historicalBars.Count} for analysis.");
            Print($"Ensure _maxBars ({_maxBars}) is greater than ContextLookbackPeriod ({ContextLookbackPeriod}).");


            if (_historicalBars.Count > ContextLookbackPeriod) // Need LookbackPeriod + 1 bars for first calculation
            {
                MarketContext initialContext = _analyzer.GetContext(_historicalBars, Symbol.PipSize);
                Print($"Initial Market Context: {initialContext}");
            }
            else
            {
                Print("Not enough historical data on start to determine context.");
            }
            // Initialize fractal states
            _activeH1FractalUpLevel = null;
            _activeH1FractalDownLevel = null;
            _waitingForUpReaction = false;
            _waitingForDownReaction = false;
            _reactionWaitStartTime = DateTime.MinValue; // Initialize
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
                if (_historicalBars.Count > _maxBars)
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

            bool fractalSignalAllowsTrade = false;
            var h1Bars = MarketData.GetBars(TimeFrame.H1, Symbol.Name);

            if (h1Bars.Count < (2 * FractalWindowSize + 1))
            {
                Print($"Not enough H1 bars ({h1Bars.Count}) to look for fractals with window size {FractalWindowSize}. Need at least {(2 * FractalWindowSize + 1)} H1 bars.");
            }
            else
            {
                if (currentContext == MarketContext.TrendingUp)
                {
                    if (_waitingForUpReaction) // Waiting for UP reaction AFTER a H1 Down Fractal was broken DOWN
                    {
                        if ((Server.Time - _reactionWaitStartTime).TotalMinutes > FractalReactionTimeoutMinutes)
                        {
                            Print($"TIMEOUT waiting for UP reaction after H1 Down Fractal break. Waited longer than {FractalReactionTimeoutMinutes} minutes. Resetting.");
                            _waitingForUpReaction = false;
                        }
                        else if (Bars.Last(0).Close > _targetReactionPrice)
                        {
                            Print($"H1 Down Fractal (Contrarian) UP REACTION CONFIRMED. Current Close {Bars.Last(0).Close} > Target Price {_targetReactionPrice}. Trade signal for TrendingUp generated.");
                            fractalSignalAllowsTrade = true;
                            _waitingForUpReaction = false;
                        }
                        else if (Bars.Last(0).Low < _breakoutConfirmationPrice * (1 - (FractalReactionPercentage / 100.0))) // Price continued down significantly, negating up reaction
                        {
                            Print($"Price moved significantly DOWN after H1 Down Fractal break (Low: {Bars.Last(0).Low} vs BreakoutConfirmation: {_breakoutConfirmationPrice}), negating expected UP reaction. Resetting.");
                            _waitingForUpReaction = false;
                        }
                    }
                    else // Not waiting for reaction, so look for H1 Down fractal and its breakout downwards
                    {
                        if (!_activeH1FractalDownLevel.HasValue) // We look for a DOWN fractal in an UPTREND
                        {
                            _activeH1FractalDownLevel = FindLastH1FractalDown(h1Bars, FractalWindowSize);
                            if (_activeH1FractalDownLevel.HasValue)
                                Print($"TrendingUp Context: Identified new H1 Down-Fractal level to watch for break: {_activeH1FractalDownLevel.Value}");
                        }

                        if (_activeH1FractalDownLevel.HasValue && Bars.Last(0).Low < _activeH1FractalDownLevel.Value) // Breakout DOWN of the H1 Down Fractal
                        {
                            Print($"TrendingUp Context: H1 Down-Fractal at {_activeH1FractalDownLevel.Value} BROKEN DOWN by current bar Low {Bars.Last(0).Low}.");
                            _breakoutConfirmationPrice = Bars.Last(0).Close; // Capture close price of the breakout bar
                            _targetReactionPrice = _breakoutConfirmationPrice * (1 + FractalReactionPercentage / 100.0); // Expect UP reaction
                            _waitingForUpReaction = true; // Now waiting for price to react UPWARDS
                            _activeH1FractalDownLevel = null; 
                            _reactionWaitStartTime = Server.Time; // Start timing the wait for reaction
                            Print($"Breakout bar close: {_breakoutConfirmationPrice}. Waiting for UP reaction above {_targetReactionPrice} (TrendingUp continuation) for {FractalReactionTimeoutMinutes} mins.");
                        }
                    }
                    if (_waitingForDownReaction) // If context switched while waiting for other direction
                    {
                        Print("Context changed to TrendingUp while waiting for general DOWN reaction. Resetting down-wait state.");
                        _waitingForDownReaction = false;
                    }
                }
                else if (currentContext == MarketContext.TrendingDown)
                {
                    if (_waitingForDownReaction) // Waiting for DOWN reaction AFTER a H1 Up Fractal was broken UP
                    {
                        if ((Server.Time - _reactionWaitStartTime).TotalMinutes > FractalReactionTimeoutMinutes)
                        {
                            Print($"TIMEOUT waiting for DOWN reaction after H1 Up Fractal break. Waited longer than {FractalReactionTimeoutMinutes} minutes. Resetting.");
                            _waitingForDownReaction = false;
                        }
                        else if (Bars.Last(0).Close < _targetReactionPrice)
                        {
                            Print($"H1 Up Fractal (Contrarian) DOWN REACTION CONFIRMED. Current Close {Bars.Last(0).Close} < Target Price {_targetReactionPrice}. Trade signal for TrendingDown generated.");
                            fractalSignalAllowsTrade = true;
                            _waitingForDownReaction = false;
                        }
                        else if (Bars.Last(0).High > _breakoutConfirmationPrice * (1 + (FractalReactionPercentage / 100.0))) // Price continued up significantly, negating down reaction
                        {
                            Print($"Price moved significantly UP after H1 Up Fractal break (High: {Bars.Last(0).High} vs BreakoutConfirmation: {_breakoutConfirmationPrice}), negating expected DOWN reaction. Resetting.");
                            _waitingForDownReaction = false;
                        }
                    }
                    else // Not waiting for reaction, so look for H1 Up fractal and its breakout upwards
                    {
                        if (!_activeH1FractalUpLevel.HasValue) // We look for an UP fractal in a DOWNTREND
                        {
                            _activeH1FractalUpLevel = FindLastH1FractalUp(h1Bars, FractalWindowSize);
                            if (_activeH1FractalUpLevel.HasValue)
                                Print($"TrendingDown Context: Identified new H1 Up-Fractal level to watch for break: {_activeH1FractalUpLevel.Value}");
                        }

                        if (_activeH1FractalUpLevel.HasValue && Bars.Last(0).High > _activeH1FractalUpLevel.Value) // Breakout UP of the H1 Up Fractal
                        {
                            Print($"TrendingDown Context: H1 Up-Fractal at {_activeH1FractalUpLevel.Value} BROKEN UP by current bar High {Bars.Last(0).High}.");
                            _breakoutConfirmationPrice = Bars.Last(0).Close;
                            _targetReactionPrice = _breakoutConfirmationPrice * (1 - FractalReactionPercentage / 100.0); // Expect DOWN reaction
                            _waitingForDownReaction = true; // Now waiting for price to react DOWNWARDS
                            _activeH1FractalUpLevel = null; 
                            _reactionWaitStartTime = Server.Time; // Start timing the wait for reaction
                            Print($"Breakout bar close: {_breakoutConfirmationPrice}. Waiting for DOWN reaction below {_targetReactionPrice} (TrendingDown continuation) for {FractalReactionTimeoutMinutes} mins.");
                        }
                    }
                     if (_waitingForUpReaction) // If context switched while waiting for other direction
                    {
                        Print("Context changed to TrendingDown while waiting for UP reaction. Resetting up-wait state.");
                        _waitingForUpReaction = false;
                    }
                }
                else // Market is Ranging or Undefined
                {
                    if (_waitingForUpReaction)
                    {
                        Print("Market context changed to Ranging/Undefined. Resetting wait for UP reaction.");
                        _waitingForUpReaction = false;
                    }
                    if (_waitingForDownReaction)
                    {
                        Print("Market context changed to Ranging/Undefined. Resetting wait for DOWN reaction.");
                        _waitingForDownReaction = false;
                    }
                    _activeH1FractalUpLevel = null; // Clear active fractal levels if not trending
                    _activeH1FractalDownLevel = null;
                }
            }

            if (fractalSignalAllowsTrade) // This flag is true only if context was TrendingUp/Down and reaction was confirmed
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
            else if (currentContext == MarketContext.Ranging)
            {
                Print("Market is RANGING. No new positions will be opened.");
            }
        }

        protected override void OnTick()
        {
            // We are using OnBar for context analysis based on closed candles.
        }

        protected override void OnStop()
        {
            Print("cBot stopped.");
        }

        // Helper function to find the last H1 Up-Fractal
        // An Up-Fractal is a bar whose High is higher than the Highs of 'windowSize' bars to its left and 'windowSize' bars to its right.
        private double? FindLastH1FractalUp(Bars h1Bars, int windowSize)
        {
            // Need at least (2 * windowSize + 1) bars to form a fractal.
            // The last possible central bar of a fractal is at index h1Bars.Count - 1 - windowSize.
            // The first possible central bar is at index windowSize.
            if (h1Bars.Count < (2 * windowSize + 1)) return null;

            for (int i = h1Bars.Count - 1 - windowSize; i >= windowSize; i--)
            {
                bool isFractal = true;
                double centralHigh = h1Bars.HighPrices[i];
                for (int j = 1; j <= windowSize; j++)
                {
                    if (h1Bars.HighPrices[i - j] >= centralHigh || h1Bars.HighPrices[i + j] >= centralHigh)
                    {
                        isFractal = false;
                        break;
                    }
                }
                if (isFractal)
                {
                    // Print($"H1 Up-Fractal found: Time={h1Bars.OpenTimes[i]}, High={centralHigh}");
                    return centralHigh;
                }
            }
            return null;
        }

        // Helper function to find the last H1 Down-Fractal
        // A Down-Fractal is a bar whose Low is lower than the Lows of 'windowSize' bars to its left and 'windowSize' bars to its right.
        private double? FindLastH1FractalDown(Bars h1Bars, int windowSize)
        {
            if (h1Bars.Count < (2 * windowSize + 1)) return null;

            for (int i = h1Bars.Count - 1 - windowSize; i >= windowSize; i--)
            {
                bool isFractal = true;
                double centralLow = h1Bars.LowPrices[i];
                for (int j = 1; j <= windowSize; j++)
                {
                    if (h1Bars.LowPrices[i - j] <= centralLow || h1Bars.LowPrices[i + j] <= centralLow)
                    {
                        isFractal = false;
                        break;
                    }
                }
                if (isFractal)
                {
                    // Print($"H1 Down-Fractal found: Time={h1Bars.OpenTimes[i]}, Low={centralLow}");
                    return centralLow;
                }
            }
            return null;
        }
    }
}

