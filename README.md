Tibber C# SDK
=======================

Package for accessing Tibber API.

----------
Installation
-------------
Using nuget package manager:
```
Install-Package Tibber.SDK
```

Usage
-------------
```
var client = new TibberApiClient(accessToken);

var basicData = await client.GetBasicData();
var homeId = basicData.Data.Viewer.Homes.First().Id.Value;
var consumption = await client.GetHomeConsumption(homeId, ConsumptionResolution.Monthly);

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

Real-time measurement usage
-------------
You must have active Tibber Pulse device at your home to access real-time measurements. `basicData.Data.Viewer.Home.Features.RealTimeConsumptionEnabled` must return `true`.

Sample observer implementation:
```
public class LiveMeasurementObserver : IObserver<LiveMeasurement>
{
  public void OnCompleted() => Console.WriteLine("Live measurement stream has been terminated. ");
  public void OnError(Exception error) => Console.WriteLine($"An error occured: {error}");
  public void OnNext(LiveMeasurement value) =>
    Console.WriteLine($"{value.Timestamp} - power: {value.Power:N0} W (average: {value.AveragePower:N0} W); consumption since last midnight: {value.AccumulatedConsumption:N3} kWh; cost since last midnight: {value.AccumulatedCost:N2} {value.Currency}");
}
```

Listener initialization:
```
var client = new TibberApiClient(accessToken);
var homeId = Guid.Parse("c70dcbe5-4485-4821-933d-a8a86452737b");
var listener = await client.StartLiveMeasurementListener(homeId);
listener.Subscribe(new LiveMeasurementObserver());
```

Sample output:
```
2018-09-28 16:53:20 +02:00 - power: 3 200 W (average: 1 678 W); consumption since last midnight: 28,338 kWh; cost since last midnight: 13,92 NOK
2018-09-28 16:53:22 +02:00 - power: 3 195 W (average: 1 678 W); consumption since last midnight: 28,340 kWh; cost since last midnight: 13,92 NOK
2018-09-28 16:53:24 +02:00 - power: 3 197 W (average: 1 678 W); consumption since last midnight: 28,342 kWh; cost since last midnight: 13,93 NOK
```