using System;
using System.Collections.Generic; // Added for IEnumerable
using System.Linq; // Added for LINQ operations
using cAlgo.API;
using cAlgo.API.Collections;
using cAlgo.API.Indicators;
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

        public double TypicalPrice => (High + Low + Close) / 3;
        public double TrueRange(PriceBar previousBar)
        {
            if (previousBar == null) return High - Low;
            return Math.Max(High - Low, Math.Max(Math.Abs(High - previousBar.Close), Math.Abs(Low - previousBar.Close)));
        }
    }

    /// <summary>
    /// Contains helper methods for calculating technical indicators.
    /// </summary>
    public static class IndicatorsHelper
    {
        /// <summary>
        /// Calculates Exponential Moving Average (EMA).
        /// </summary>
        public static List<double> CalculateEMA(IList<double> prices, int period)
        {
            if (prices == null || prices.Count < period)
                return new List<double>();

            var emaValues = new List<double>(prices.Count);
            double multiplier = 2.0 / (period + 1);
            double sma = prices.Take(period).Average(); // Initial SMA
            emaValues.AddRange(Enumerable.Repeat(0.0, period - 1)); // Fill initial with 0 or another placeholder
            emaValues.Add(sma);

            for (int i = period; i < prices.Count; i++)
            {
                double ema = (prices[i] - emaValues.Last(v => v != 0.0)) * multiplier + emaValues.Last(v => v != 0.0);
                emaValues.Add(ema);
            }
            return emaValues;
        }

        /// <summary>
        /// Calculates Average True Range (ATR).
        /// </summary>
        public static List<double> CalculateATR(IList<PriceBar> bars, int period)
        {
            if (bars == null || bars.Count < period)
                return new List<double>();

            var atrValues = new List<double>(bars.Count);
            var trValues = new List<double>(bars.Count);

            trValues.Add(bars[0].High - bars[0].Low); // TR for the first bar
            for (int i = 1; i < bars.Count; i++)
            {
                trValues.Add(bars[i].TrueRange(bars[i-1]));
            }

            // Calculate initial ATR (SMA of TRs)
            atrValues.AddRange(Enumerable.Repeat(0.0, period -1)); // Fill initial with 0 or another placeholder
            double initialAtr = trValues.Take(period).Average();
            atrValues.Add(initialAtr);


            // Wilder's smoothing for subsequent ATR values
            for (int i = period; i < trValues.Count; i++)
            {
                double atr = (atrValues.Last(v => v != 0.0) * (period - 1) + trValues[i]) / period;
                atrValues.Add(atr);
            }
            return atrValues;
        }
    }


    /// <summary>
    /// Market context analyzer.
    /// Will use channels to determine the context in the future.
    /// </summary>
    public class MarketContextAnalyzer
    {
        private readonly int _keltnerPeriod;
        private readonly double _keltnerMultiplier;
        private readonly int _atrPeriod;


        public MarketContextAnalyzer(int keltnerPeriod = 20, double keltnerMultiplier = 2.0, int atrPeriod = 10)
        {
            _keltnerPeriod = keltnerPeriod;
            _keltnerMultiplier = keltnerMultiplier;
            _atrPeriod = atrPeriod;
        }

        /// <summary>
        /// Defines the current market context based on historical data.
        /// </summary>
        /// <param name="priceBars">List of historical price data (candles/bars).</param>
        /// <returns>Current MarketContext.</returns>
        public MarketContext GetContext(IList<PriceBar> priceBars)
        {
            if (priceBars == null || priceBars.Count < Math.Max(_keltnerPeriod, _atrPeriod))
            {
                Console.WriteLine("Not enough data to determine context.");
                return MarketContext.Undefined;
            }

            var typicalPrices = priceBars.Select(p => p.TypicalPrice).ToList();
            var emaValues = IndicatorsHelper.CalculateEMA(typicalPrices, _keltnerPeriod);
            var atrValues = IndicatorsHelper.CalculateATR(priceBars, _atrPeriod);

            if (emaValues.Count == 0 || atrValues.Count == 0)
            {
                 Console.WriteLine("Failed to calculate indicators for context analysis.");
                return MarketContext.Undefined;
            }

            // Get the latest values
            double currentEma = emaValues.Last();
            double currentAtr = atrValues.Last();
            PriceBar currentBar = priceBars.Last();

            double upperKeltner = currentEma + (_keltnerMultiplier * currentAtr);
            double lowerKeltner = currentEma - (_keltnerMultiplier * currentAtr);

            // Basic Keltner Channel Logic:
            // Trend determination can be more sophisticated, e.g., by looking at the slope of the EMA
            // or consecutive closes above/below bands.
            
            // For now, a simplified approach:
            // If close is above upper band -> Trending Up
            // If close is below lower band -> Trending Down
            // Otherwise -> Ranging

            // We also need to consider the slope of the EMA for trend direction.
            // Let's get the EMA from a few bars ago to check the slope
            if (emaValues.Count < 3) // Need at least 3 EMA values to check slope simply
            {
                return MarketContext.Ranging; // Not enough data for slope
            }

            double previousEma2 = emaValues[emaValues.Count - 3]; // EMA two bars before current
            double previousEma1 = emaValues[emaValues.Count - 2]; // EMA one bar before current

            bool emaRising = currentEma > previousEma1 && previousEma1 > previousEma2;
            bool emaFalling = currentEma < previousEma1 && previousEma1 < previousEma2;


            if (currentBar.Close > upperKeltner && emaRising)
            {
                return MarketContext.TrendingUp;
            }
            else if (currentBar.Close < lowerKeltner && emaFalling)
            {
                return MarketContext.TrendingDown;
            }
            else
            {
                // More robust ranging detection could involve checking if price is within bands
                // AND the bands are relatively flat or contracting (low ATR or ATR not expanding significantly)
                // Or if EMA slope is neutral
                bool isPriceInsideBands = currentBar.Close < upperKeltner && currentBar.Close > lowerKeltner;
                bool emaFlat = !emaRising && !emaFalling; // Simplified flatness check

                if (isPriceInsideBands && emaFlat)
                {
                    return MarketContext.Ranging;
                }
                // If price is inside bands but EMA is still clearly rising/falling,
                // it might be a pullback within a trend rather than pure ranging.
                // For now, let's be conservative and classify as Undefined if not clearly trending or ranging.
                 if (isPriceInsideBands && emaRising) return MarketContext.TrendingUp; // Or a weaker form of uptrend/pullback
                 if (isPriceInsideBands && emaFalling) return MarketContext.TrendingDown; // Or a weaker form of downtrend/pullback


                return MarketContext.Ranging; // Default to ranging if inside bands and not clearly trending.
            }
        }
    }


    [Robot(AccessRights = AccessRights.None, AddIndicators = true)]
    public class context : Robot
    {
        [Parameter("Message", DefaultValue = "Hello world!")]
        public string Message { get; set; }

        [Parameter("Keltner Period", DefaultValue = 20, MinValue = 2, Group = "Context Analysis")]
        public int KeltnerPeriod { get; set; }

        [Parameter("Keltner Multiplier", DefaultValue = 2.0, MinValue = 0.1, Group = "Context Analysis")]
        public double KeltnerMultiplier { get; set; }

        [Parameter("ATR Period", DefaultValue = 10, MinValue = 1, Group = "Context Analysis")]
        public int AtrPeriod { get; set; }

        [Parameter("Risk % Per Trade", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0, Step = 0.1, Group = "Trading")]
        public double RiskPercentage { get; set; }

        [Parameter("Stop Loss (Pips)", DefaultValue = 20, MinValue = 1, Group = "Trading")]
        public int StopLossInPips { get; set; }

        [Parameter("Take Profit (Pips)", DefaultValue = 40, MinValue = 1, Group = "Trading")]
        public int TakeProfitInPips { get; set; }

        [Parameter("Trade Label", DefaultValue = "MarketTrendBot_v1", Group = "Trading")]
        public string TradeLabel { get; set; }


        private MarketContextAnalyzer _analyzer;
        private List<PriceBar> _historicalBars;
        private const int _maxBars = 200; // Store a maximum of 200 bars for analysis

        private DateTime _lastTradeDate = DateTime.MinValue;
        private readonly TimeSpan _tradeStartTime = new TimeSpan(9, 0, 0); // 09:00
        private readonly TimeSpan _tradeEndTime = new TimeSpan(15, 0, 0);   // 15:00
        private const int UtcOffsetHours = 3; // For UTC+3, assuming Server.Time is UTC


        protected override void OnStart()
        {
            Print(Message);
            _analyzer = new MarketContextAnalyzer(KeltnerPeriod, KeltnerMultiplier, AtrPeriod);
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
                if (_historicalBars.Count > _maxBars)
                {
                    _historicalBars.RemoveAt(0);
                }
            }
            Print($"Loaded {history.Count} historical bars. Using last {_historicalBars.Count} for analysis.");

            if (_historicalBars.Count > Math.Max(KeltnerPeriod, AtrPeriod))
            {
                MarketContext initialContext = _analyzer.GetContext(_historicalBars);
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
                if (_historicalBars.Count > _maxBars)
                {
                    _historicalBars.RemoveAt(0);
                }
            }

            if (_historicalBars.Count < Math.Max(KeltnerPeriod, AtrPeriod))
            {
                Print($"Not enough data to determine context or trade. Bars collected: {_historicalBars.Count}");
                return;
            }

            MarketContext currentContext = _analyzer.GetContext(_historicalBars);
            Print($"Server Time (UTC): {Server.Time:HH:mm:ss}, Bar Time: {newBar.OpenTime:HH:mm:ss}, Context: {currentContext}");

            // Trading Time Check (UTC+3)
            var serverTimeUtc = Server.Time;
            var exchangeTime = serverTimeUtc.AddHours(UtcOffsetHours);
            if (exchangeTime.TimeOfDay < _tradeStartTime || exchangeTime.TimeOfDay >= _tradeEndTime) // Use >= for end time to exclude 15:00:00 itself
            {
                Print($"Current exchange time {exchangeTime:HH:mm:ss} (UTC+{UtcOffsetHours}) is outside trading hours ({_tradeStartTime} - {_tradeEndTime}). No new positions.");
                return;
            }

            // One Trade Per Day Check
            if (serverTimeUtc.Date == _lastTradeDate.Date)
            {
                Print($"One trade limit for {serverTimeUtc.Date:yyyy-MM-dd} already reached. No new positions.");
                return;
            }

            // Check if an open position by this cBot already exists
            bool hasOpenPosition = Positions.Any(p => p.Label == TradeLabel && p.SymbolName == Symbol.Name);
            if (hasOpenPosition)
            {
                Print($"An open position with label '{TradeLabel}' already exists. No new entry.");
                return;
            }

            // Trade Execution Logic
            if (currentContext == MarketContext.TrendingUp || currentContext == MarketContext.TrendingDown)
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

                double riskAmountInAccountCurrency = Account.Balance * (RiskPercentage / 100.0);
                double riskPerPipPerUnit = Symbol.PipValue;
                double totalRiskValueForSlPerUnit = StopLossInPips * riskPerPipPerUnit;

                if (totalRiskValueForSlPerUnit <= 0)
                {
                    Print($"Total risk value for SL per unit ({totalRiskValueForSlPerUnit}) is zero or negative. StopLossInPips: {StopLossInPips}, PipValue: {Symbol.PipValue}. Cannot calculate volume. No trade.");
                    return;
                }

                double positionSizeInUnits_raw = riskAmountInAccountCurrency / totalRiskValueForSlPerUnit;
                double calculatedVolumeInLots = Symbol.VolumeToLots(positionSizeInUnits_raw);
                
                // Normalize volume and check against min/max
                calculatedVolumeInLots = Symbol.NormalizeVolumeInLots(calculatedVolumeInLots, RoundingMode.Down);

                if (calculatedVolumeInLots < Symbol.VolumeInLotsMin)
                {
                    Print($"Calculated volume {calculatedVolumeInLots} lots is less than minimum {Symbol.VolumeInLotsMin} lots. Attempting to use minimum volume.");
                    calculatedVolumeInLots = Symbol.VolumeInLotsMin;
                    // Re-check if we can afford min volume with the risk settings
                    double minVolumeInUnits = Symbol.LotsToVolumeInUnits(calculatedVolumeInLots);
                    double costOfMinVolumeSl = minVolumeInUnits * totalRiskValueForSlPerUnit;
                    if (costOfMinVolumeSl > riskAmountInAccountCurrency && riskAmountInAccountCurrency > 0)
                    {
                         Print($"Account cannot afford minimum volume {Symbol.VolumeInLotsMin} lots ({minVolumeInUnits} units) with current risk ({RiskPercentage}%) and SL ({StopLossInPips} pips). Risk Amount: {riskAmountInAccountCurrency}, Min Vol SL Cost: {costOfMinVolumeSl}. No trade.");
                         return;
                    }
                }
                
                if (calculatedVolumeInLots > Symbol.VolumeInLotsMax)
                {
                    Print($"Calculated volume {calculatedVolumeInLots} lots is greater than maximum {Symbol.VolumeInLotsMax} lots. Adjusting to maximum.");
                    calculatedVolumeInLots = Symbol.VolumeInLotsMax;
                }

                if (calculatedVolumeInLots == 0) // Double check after all adjustments
                {
                    Print($"Final calculated volume is zero lots. Cannot trade.");
                    return;
                }

                double finalVolumeInUnits = Symbol.LotsToVolumeInUnits(calculatedVolumeInLots);
                if (finalVolumeInUnits == 0) {
                    Print($"Final volume in units is zero. Cannot trade with {calculatedVolumeInLots} lots. Check symbol information (LotSize: {Symbol.LotSize}, etc.)");
                    return;
                }

                TradeType tradeType = (currentContext == MarketContext.TrendingUp) ? TradeType.Buy : TradeType.Sell;
                Print($"Attempting to open {tradeType} position: Context={currentContext}, Volume={calculatedVolumeInLots} lots ({finalVolumeInUnits} units), SL={StopLossInPips} pips, TP={TakeProfitInPips} pips, Risk={RiskPercentage}% ({riskAmountInAccountCurrency} {Account.Currency})");
                
                var result = ExecuteMarketOrder(tradeType, Symbol.Name, finalVolumeInUnits, TradeLabel, StopLossInPips, TakeProfitInPips);

                if (result.IsSuccessful)
                {
                    _lastTradeDate = serverTimeUtc.Date; // Update last trade date only on successful execution
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
            // OnTick can be used for faster reactions if needed, but for context, OnBar is usually preferred.
        }

        protected override void OnStop()
        {
            Print("cBot stopped.");
        }
    }
}

