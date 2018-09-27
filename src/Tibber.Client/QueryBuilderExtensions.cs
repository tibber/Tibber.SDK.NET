using System;

namespace Tibber.Client
{
    public static class QueryBuilderExtensions
    {
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
                            homeId.ToString())
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
