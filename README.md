Tibber SDK.NET (beta) [![NuGet Badge](https://buildstats.info/nuget/Tibber.Sdk?includePreReleases=true)](https://www.nuget.org/packages/Tibber.Sdk)
=======================

Package for accessing Tibber API.

----------
Installation
-------------
Using nuget package manager:
```
Install-Package Tibber.Sdk -Version 0.3.0-beta
```

Authorization
-------------
You must have Tibber account to access our API. Access token can be generated at https://developer.tibber.com.

Usage
-------------
```csharp
using Tibber.Sdk;
```

```csharp
var client = new TibberApiClient(accessToken);

var basicData = await client.GetBasicData();
var homeId = basicData.Data.Viewer.Homes.First().Id.Value;
var consumption = await client.GetHomeConsumption(homeId, EnergyResolution.Monthly);

var customQueryBuilder =
    new TibberQueryBuilder()
        .WithAllScalarFields()
        .WithViewer(
            new ViewerQueryBuilder()
                .WithAllScalarFields()
                .WithAccountType()
                .WithHome(
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
                        .WithMeteringPointData(new MeteringPointDataQueryBuilder().WithAllFields()),
                    homeId
                )
        );

var customQuery = customQueryBuilder.Build(); // produces plain GraphQL query text
var result = await client.Query(customQuery);
```

Extension methods
-------------
It's good practice to define custom queries as extension methods, either of root `TibberQueryBuilder` or any child subquery builder. It helps to reduce code redundancy.
Example:
```csharp
public static class QueryBuilderExtensions
{
    /// <summary>
    /// Builds a query for home consumption.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="homeId"></param>
    /// <param name="resolution"></param>
    /// <param name="lastEntries">how many last entries to fetch</param>
    /// <returns></returns>
    public static TibberQueryBuilder WithHomeConsumption(this TibberQueryBuilder builder, Guid homeId, EnergyResolution resolution, int lastEntries) =>
        builder.WithAllScalarFields()
            .WithViewer(
                new ViewerQueryBuilder()
                    .WithHome(
                        new HomeQueryBuilder().WithConsumption(resolution, lastEntries),
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
            new HomeConsumptionConnectionQueryBuilder().WithNodes(new ConsumptionQueryBuilder().WithAllFields()),
            resolution,
            last: lastEntries);
}
```
Usage:
```csharp
var query = new TibberQueryBuilder().WithHomeConsumption(homeId, EnergyResolution.Monthly, 12).Build();
await client.Query(query);
```

Real-time measurement usage
-------------
You must have active Tibber Pulse or Watty device at your home to access real-time measurements. `basicData.Data.Viewer.Home.Features.RealTimeConsumptionEnabled` must return `true`.

Sample observer implementation:
```csharp
public class RealTimeMeasurementObserver : IObserver<RealTimeMeasurement>
{
    public void OnCompleted() => Console.WriteLine("Real time measurement stream has been terminated. ");
    public void OnError(Exception error) => Console.WriteLine($"An error occured: {error}");
    public void OnNext(RealTimeMeasurement value) =>
        Console.WriteLine($"{value.Timestamp} - power: {value.Power:N0} W (average: {value.AveragePower:N0} W); consumption since last midnight: {value.AccumulatedConsumption:N3} kWh; cost since last midnight: {value.AccumulatedCost:N2} {value.Currency}");
}
```

Listener initialization:
```csharp
var client = new TibberApiClient(accessToken);
var homeId = Guid.Parse("c70dcbe5-4485-4821-933d-a8a86452737b");
var listener = await client.StartRealTimeMeasurementListener(homeId);
listener.Subscribe(new RealTimeMeasurementObserver());
```

Sample output:
```
2018-09-28 16:53:20 +02:00 - power: 3 200 W (average: 1 678 W); consumption since last midnight: 28,338 kWh; cost since last midnight: 13,92 NOK
2018-09-28 16:53:22 +02:00 - power: 3 195 W (average: 1 678 W); consumption since last midnight: 28,340 kWh; cost since last midnight: 13,92 NOK
2018-09-28 16:53:24 +02:00 - power: 3 197 W (average: 1 678 W); consumption since last midnight: 28,342 kWh; cost since last midnight: 13,93 NOK
```
