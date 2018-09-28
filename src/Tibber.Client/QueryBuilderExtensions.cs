using System;

namespace Tibber.Client
{
    public static class QueryBuilderExtensions
    {
        /// <summary>
        /// Builds a query for customer, homes and their active subscription data. 
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static TibberQueryBuilder WithHomesAndSubscriptions(this TibberQueryBuilder builder) =>
            builder.WithAllScalarFields()
                .WithViewer(
                    new ViewerQueryBuilder()
                        .WithAllScalarFields()
                        .WithAccountType()
                        .WithHomes(
                            new HomeQueryBuilder()
                                .WithAllScalarFields()
                                .WithAddress(new AddressQueryBuilder().WithAllFields())
                                .WithCurrentSubscription(
                                    new SubscriptionQueryBuilder()
                                        .WithAllScalarFields()
                                        .WithSubscriber(new LegalEntityQueryBuilder().WithAllFields())
                                        .WithPriceInfo(new PriceInfoQueryBuilder().WithCurrent(new PriceQueryBuilder().WithAllFields()))
                                )
                                .WithOwner(new LegalEntityQueryBuilder().WithAllFields())
                                .WithFeatures(new HomeFeaturesQueryBuilder().WithAllFields())
                                .WithMeteringPointData(new MeteringPointDataQueryBuilder().WithAllFields())
                        )
                );

        /// <summary>
        /// Builds a query for home consumption.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="homeId"></param>
        /// <param name="resolution"></param>
        /// <param name="lastEntries">how many last entries to fetch; if no value provider a default will be used - hourly: 24; daily: 30; weekly: 4; monthly: 12; annually: 1</param>
        /// <returns></returns>
        public static TibberQueryBuilder WithHomeConsumption(this TibberQueryBuilder builder, Guid homeId, ConsumptionResolution resolution, int? lastEntries) =>
            builder.WithAllScalarFields()
                .WithViewer(
                    new ViewerQueryBuilder()
                        .WithHome(
                            new HomeQueryBuilder()
                                .WithConsumption(
                                    new HomeConsumptionConnectionQueryBuilder()
                                        .WithNodes(
                                            new ConsumptionQueryBuilder().WithAllFields()),
                                            resolution,
                                            last: lastEntries ?? LastConsumptionEntries(resolution)),
                            homeId)
                );

        private static int LastConsumptionEntries(ConsumptionResolution resolution)
        {
            switch (resolution)
            {
                case ConsumptionResolution.Annual: return 1;
                case ConsumptionResolution.Daily: return 30;
                case ConsumptionResolution.Hourly: return 24;
                case ConsumptionResolution.Monthly: return 12;
                case ConsumptionResolution.Weekly: return 4;
                default: throw new NotSupportedException($"{resolution} resolution not supported");
            }
        }
    }
}
