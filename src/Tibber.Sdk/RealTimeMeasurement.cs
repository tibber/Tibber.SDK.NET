using System;
using Newtonsoft.Json;

namespace Tibber.Sdk
{
    public class RealTimeMeasurement
    {
        /// <summary>
        /// Timestamp when usage occurred
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }
        /// <summary>
        /// Consumption at the moment (W)
        /// </summary>
        public decimal Power { get; set; }
        /// <summary>
        /// Last meter active import register state (kWh)
        /// </summary>
        public decimal? LastMeterConsumption { get; set; }
        /// <summary>
        /// Energy consumed since the last midnight (kWh)
        /// </summary>
        public decimal AccumulatedConsumption { get; set; }
        /// <summary>
        /// Energy consumed since the beginning of the hour (kWh)
        /// </summary>
        public decimal AccumulatedConsumptionLastHour { get; set; }
        /// <summary>
        /// Net energy produced and returned to grid since midnight (kWh)
        /// </summary>
        public decimal AccumulatedProduction { get; set; }
        /// <summary>
        /// Net energy produced since the beginning of the hour (kWh)
        /// </summary>
        public decimal AccumulatedProductionLastHour { get; set; }
        /// <summary>
        /// Accumulated cost since midnight; requires active Tibber power deal
        /// </summary>
        public decimal? AccumulatedCost { get; set; }
        /// <summary>
        /// Accumulated reward since midnight; requires active Tibber power deal
        /// </summary>
        public decimal? AccumulatedReward { get; set; }
        /// <summary>
        /// Currency of displayed cost; requires active Tibber power deal
        /// </summary>
        public string Currency { get; set; }
        /// <summary>
        /// Min consumption since midnight (W)
        /// </summary>
        public decimal MinPower { get; set; }
        /// <summary>
        /// Average consumption since midnight (W)
        /// </summary>
        public decimal AveragePower { get; set; }
        /// <summary>
        /// Peak consumption since midnight (W)
        /// </summary>
        public decimal MaxPower { get; set; }
        /// <summary>
        /// Net production (A-) at the moment (Watt)
        /// </summary>
        public decimal? PowerProduction { get; set; }
        /// <summary>
        /// Reactive consumption (Q+) at the moment (kVAr)
        /// </summary>
        public decimal? PowerReactive { get; set; }
        /// <summary>
        /// Net reactive production (Q-) at the moment (kVAr)
        /// </summary>
        public decimal? PowerProductionReactive { get; set; }
        /// <summary>
        /// Minimum net production since midnight (W)
        /// </summary>
        public decimal? MinPowerProduction { get; set; }
        /// <summary>
        /// Maximum net production since midnight (W)
        /// </summary>
        public decimal? MaxPowerProduction { get; set; }
        /// <summary>
        /// Last meter active export register state (kWh)
        /// </summary>
        public decimal? LastMeterProduction { get; set; }
        /// <summary>
        /// Voltage (V) on phase 1; on Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50. There can be other deviations based on concrete meter firmware.
        /// </summary>
        public decimal? VoltagePhase1 { get; set; }
        /// <summary>
        /// Voltage (V) on phase 2; on Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50. There can be other deviations based on concrete meter firmware. Value is always null for single phase meters.
        /// </summary>
        public decimal? VoltagePhase2 { get; set; }
        /// <summary>
        /// Voltage (V) on phase 3; on Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50. There can be other deviations based on concrete meter firmware. Value is always null for single phase meters.
        /// </summary>
        public decimal? VoltagePhase3 { get; set; }
        /// <summary>
        /// Current (A) on phase 1; on Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50. There can be other deviations based on concrete meter firmware.
        /// </summary>
        [JsonProperty("CurrentL1")]
        public decimal? CurrentPhase1 { get; set; }
        /// <summary>
        /// Current (A) on phase 2; on Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50. There can be other deviations based on concrete meter firmware. Value is always null for single phase meters.
        /// </summary>
        [JsonProperty("CurrentL2")]
        public decimal? CurrentPhase2 { get; set; }
        /// <summary>
        /// Current (A) on phase 3; on Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50. There can be other deviations based on concrete meter firmware. Value is always null for single phase meters.
        /// </summary>
        [JsonProperty("CurrentL3")]
        public decimal? CurrentPhase3 { get; set; }
        /// <summary>
        /// Power factor (active power / apparent power)
        /// </summary>
        public decimal? PowerFactor { get; set; }
        /// <summary>
        /// Device signal strength (Pulse - dB; Watty - percent)
        /// </summary>
        public int? SignalStrength { get; set; }
    }
}
