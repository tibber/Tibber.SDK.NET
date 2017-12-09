namespace Tibber.Client
{
    public static class QueryBuilderExtensions
    {
        public static TibberQueryBuilder WithDefaults(this TibberQueryBuilder builder) =>
            builder.WithAllScalarFields()
                .WithViewer(
                    new ViewerQueryBuilder()
                        .WithAllScalarFields()
                        .WithHomes(
                            new HomeQueryBuilder()
                                .WithAllScalarFields()
                                .WithAddress(new AddressQueryBuilder().WithAllFields())
                                .WithCurrentSubscription(
                                    new SubscriptionQueryBuilder()
                                        .WithAllScalarFields()
                                        .WithSubscriber(new LegalEntityQueryBuilder().WithAllFields())
                                )
                                .WithOwner(new LegalEntityQueryBuilder().WithAllFields())
                                .WithMeteringPointData(new MeteringPointDataQueryBuilder().WithAllFields())
                        )
                );
    }
}
