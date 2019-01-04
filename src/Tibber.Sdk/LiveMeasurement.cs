using System;

namespace Tibber.Sdk
{
    public class LiveMeasurement
    {
        /// <summary>
        /// When usage occured
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }
        /// <summary>
        /// Wattage consumed
        /// </summary>
        public decimal Power { get; set; }
        /// <summary>
        /// kWh consumed since midnight
        /// </summary>
        public decimal AccumulatedConsumption { get; set; }
        /// <summary>
        /// Accumulated cost since midnight
        /// </summary>
        /// <remarks>requires active Tibber power deal</remarks>
        public decimal? AccumulatedCost { get; set; }
        /// <summary>
        /// Currency of displayed cost
        /// </summary>
        /// <remarks>requires active Tibber power deal</remarks>
        public string Currency { get; set; }
        /// <summary>
        /// Min power since midnight
        /// </summary>
        public decimal MinPower { get; set; }
        /// <summary>
        /// Average power since midnight
        /// </summary>
        public decimal AveragePower { get; set; }
        /// <summary>
        /// Max power since midnight
        /// </summary>
        public decimal MaxPower { get; set; }

        /// <summary>
        /// Voltage on phase 1
        /// </summary>
        /// <remarks>on Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50.</remarks>
        public decimal? VoltagePhase1 { get; set; }
        /// <summary>
        /// Voltage on phase 2
        /// </summary>
        /// <remarks>On Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50. Value is always null for single phase meters.</remarks>
        public decimal? VoltagePhase2 { get; set; }
        /// <summary>
        /// Voltage on phase 2
        /// </summary>
        /// <remarks>On Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50. Value is always null for single phase meters.</remarks>
        public decimal? VoltagePhase3 { get; set; }
        /// <summary>
        /// Current on phase 1
        /// </summary>
        /// <remarks>On Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50.</remarks>
        public decimal? CurrentPhase1 { get; set; }
        /// <summary>
        /// Current on phase 2
        /// </summary>
        /// <remarks>On Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50. Value is always null for single phase meters.</remarks>
        public decimal? CurrentPhase2 { get; set; }
        /// <summary>
        /// Current on phase 3
        /// </summary>
        /// <remarks>On Kaifa and Aidon meters the value is not part of every HAN data frame therefore the value is null at timestamps with second value other than 0, 10, 20, 30, 40, 50. Value is always null for single phase meters.</remarks>
        public decimal? CurrentPhase3 { get; set; }
    }
}