using System;

namespace Tibber.Sdk
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
        /// Builds a query for homes and features.
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static TibberQueryBuilder WithHomes(this TibberQueryBuilder builder) =>
            builder.WithAllScalarFields()
                .WithViewer(
                    new ViewerQueryBuilder()
                        .WithAllScalarFields()
                        .WithHomes(
                            new HomeQueryBuilder()
                                .WithAllScalarFields()
                                .WithFeatures(new HomeFeaturesQueryBuilder()
                                    .WithAllFields())
                        )
                );

        /// <summary>
        /// Builds a query for home and features.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="homeId"></param>
        /// <returns></returns>
        public static TibberQueryBuilder WithHomeById(this TibberQueryBuilder builder, Guid homeId) =>
            builder.WithAllScalarFields()
                .WithViewer(
                    new ViewerQueryBuilder()
                        .WithAllScalarFields()
                        .WithHome(
                            new HomeQueryBuilder()
                                .WithAllScalarFields()
                                .WithFeatures(new HomeFeaturesQueryBuilder()
                                    .WithAllFields()),
                            homeId
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
        public static TibberQueryBuilder WithHomeConsumption(this TibberQueryBuilder builder, Guid homeId, EnergyResolution resolution, int? lastEntries) =>
            builder.WithViewer(
                    new ViewerQueryBuilder()
                        .WithHome(
                            new HomeQueryBuilder().WithConsumption(resolution, lastEntries ?? LastConsumptionEntries(resolution)),
                            homeId
                        )
                );

        /// <summary>
        /// Builds a query for home production.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="homeId"></param>
        /// <param name="resolution"></param>
        /// <param name="lastEntries">how many last entries to fetch; if no value provider a default will be used - hourly: 24; daily: 30; weekly: 4; monthly: 12; annually: 1</param>
        /// <returns></returns>
        public static TibberQueryBuilder WithHomeProduction(this TibberQueryBuilder builder, Guid homeId, EnergyResolution resolution, int? lastEntries) =>
            builder.WithViewer(
                new ViewerQueryBuilder()
                    .WithHome(
                        new HomeQueryBuilder().WithHomeProduction(resolution, lastEntries ?? LastConsumptionEntries(resolution)),
                        homeId
                    )
            );

        /// <summary>
        /// Builds a query for home consumption.
        /// </summary>
        /// <param name="homeQueryBuilder"></param>
        /// <param name="resolution"></param>
        /// <param name="lastEntries">how many last entries to fetch</param>
        /// <returns></returns>
        public static HomeQueryBuilder WithConsumption(this HomeQueryBuilder homeQueryBuilder, EnergyResolution resolution, int lastEntries) =>
            homeQueryBuilder.WithConsumption(
                new HomeConsumptionConnectionQueryBuilder().WithNodes(new ConsumptionEntryQueryBuilder().WithAllFields()),
                resolution,
                last: lastEntries);

        /// <summary>
        /// Builds a query for home production.
        /// </summary>
        /// <param name="homeQueryBuilder"></param>
        /// <param name="resolution"></param>
        /// <param name="lastEntries">how many last entries to fetch</param>
        /// <returns></returns>
        public static HomeQueryBuilder WithHomeProduction(this HomeQueryBuilder homeQueryBuilder, EnergyResolution resolution, int lastEntries) =>
            homeQueryBuilder.WithProduction(
                new HomeProductionConnectionQueryBuilder().WithNodes(new ProductionEntryQueryBuilder().WithAllFields()),
                resolution,
                last: lastEntries);

        private static int LastConsumptionEntries(EnergyResolution resolution) =>
            resolution switch
            {
                EnergyResolution.Annual => 1,
                EnergyResolution.Daily => 30,
                EnergyResolution.Hourly => 24,
                EnergyResolution.Monthly => 12,
                EnergyResolution.Weekly => 4,
                _ => throw new NotSupportedException($"{resolution} resolution not supported")
            };
    }
}
