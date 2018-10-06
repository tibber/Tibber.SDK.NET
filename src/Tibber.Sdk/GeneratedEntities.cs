using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace Tibber.Sdk
{
    #region base classes
    public class FieldMetadata
    {
        public string Name { get; set; }
        public bool IsComplex { get; set; }
        public Type QueryBuilderType { get; set; }
    }

    public enum Formatting
    {
        None,
        Indented
    }

    public abstract class GraphQlQueryBuilder
    {
        private const int IndentationSize = 2;

        private static readonly IList<FieldMetadata> EmptyFieldCollection = new List<FieldMetadata>();

        private readonly Dictionary<string, GraphQlFieldCriteria> _fieldCriteria = new Dictionary<string, GraphQlFieldCriteria>();

        protected virtual IList<FieldMetadata> AllFields { get; } = EmptyFieldCollection;

        public void Clear() => _fieldCriteria.Clear();

        public void IncludeAllFields() => IncludeFields(AllFields);

        public string Build(Formatting formatting = Formatting.None) => Build(formatting, 1);

        protected string Build(Formatting formatting, int level)
        {
            var builder = new StringBuilder();
            builder.Append("{");

            if (formatting == Formatting.Indented)
                builder.AppendLine();

            var separator = String.Empty;
            foreach (var criteria in _fieldCriteria.Values)
            {
                var fieldCriteria = criteria.Build(formatting, level);
                if (formatting == Formatting.Indented)
                    builder.AppendLine(fieldCriteria);
                else if (!String.IsNullOrEmpty(fieldCriteria))
                {
                    builder.Append(separator);
                    builder.Append(fieldCriteria);
                }

                separator = ",";
            }

            if (formatting == Formatting.Indented)
                builder.Append(GetIndentation(level - 1));

            builder.Append("}");
            return builder.ToString();
        }

        protected void IncludeScalarField(string fieldName, IDictionary<string, object> args) =>
            _fieldCriteria[fieldName] = new GraphQlScalarFieldCriteria(fieldName, args);

        protected void IncludeObjectField(string fieldName, GraphQlQueryBuilder objectFieldQueryBuilder, IDictionary<string, object> args) =>
            _fieldCriteria[fieldName] = new GraphQlObjectFieldCriteria(fieldName, objectFieldQueryBuilder, args);

        protected void IncludeFields(IEnumerable<FieldMetadata> fields)
        {
            foreach (var field in fields)
            {
                if (field.QueryBuilderType == null)
                    IncludeScalarField(field.Name, null);
                else
                {
                    var queryBuilder = (GraphQlQueryBuilder)Activator.CreateInstance(field.QueryBuilderType);
                    queryBuilder.IncludeAllFields();
                    IncludeObjectField(field.Name, queryBuilder, null);
                }
            }
        }

        private static string GetIndentation(int level) => new String(' ', level * IndentationSize);

        private abstract class GraphQlFieldCriteria
        {
            protected readonly string FieldName;
            private readonly IDictionary<string, object> _args;

            protected GraphQlFieldCriteria(string fieldName, IDictionary<string, object> args)
            {
                FieldName = fieldName;
                _args = args;
            }

            public abstract string Build(Formatting formatting, int level);

            protected string BuildArgumentClause(Formatting formatting)
            {
                var separator = formatting == Formatting.Indented ? " " : null;
                return
                    _args?.Count > 0
                        ? $"({String.Join($",{separator}", _args.Select(kvp => $"{kvp.Key}:{separator}{BuildArgumentValue(kvp.Value)}"))}){separator}"
                        : String.Empty;
            }

            private static string BuildArgumentValue(object value)
            {
                if (value is Enum @enum)
                    return ConvertEnumToString(@enum);

                var argumentValue = Convert.ToString(value, CultureInfo.InvariantCulture);
                return value is String || value is Guid ? $"\"{argumentValue}\"" : argumentValue;
            }

            private static string ConvertEnumToString(Enum @enum)
            {
                var enumMember =
                    @enum.GetType()
                        .GetTypeInfo()
                        .GetMembers()
                        .Single(m => String.Equals(m.Name, @enum.ToString()));

                var enumMemberAttribute = (EnumMemberAttribute)enumMember.GetCustomAttribute(typeof(EnumMemberAttribute));

                return enumMemberAttribute == null
                    ? @enum.ToString()
                    : enumMemberAttribute.Value;
            }
        }

        private class GraphQlScalarFieldCriteria : GraphQlFieldCriteria
        {
            public GraphQlScalarFieldCriteria(string fieldName, IDictionary<string, object> args) : base(fieldName, args)
            {
            }

            public override string Build(Formatting formatting, int level)
            {
                var builder = new StringBuilder();
                if (formatting == Formatting.Indented)
                    builder.Append(GetIndentation(level));

                builder.Append(FieldName);
                builder.Append(BuildArgumentClause(formatting));
                return builder.ToString();
            }
        }

        private class GraphQlObjectFieldCriteria : GraphQlFieldCriteria
        {
            private readonly GraphQlQueryBuilder _objectQueryBuilder;

            public GraphQlObjectFieldCriteria(string fieldName, GraphQlQueryBuilder objectQueryBuilder, IDictionary<string, object> args) : base(fieldName, args)
            {
                _objectQueryBuilder = objectQueryBuilder;
            }

            public override string Build(Formatting formatting, int level)
            {
                if (_objectQueryBuilder._fieldCriteria.Count == 0)
                    return String.Empty;

                var builder = new StringBuilder();
                var fieldName = FieldName;
                if (formatting == Formatting.Indented)
                    fieldName = $"{GetIndentation(level)}{FieldName} ";

                builder.Append(fieldName);
                builder.Append(BuildArgumentClause(formatting));
                builder.Append(_objectQueryBuilder.Build(formatting, level + 1));
                return builder.ToString();
            }
        }
    }

    public abstract class GraphQlQueryBuilder<TQueryBuilder> : GraphQlQueryBuilder where TQueryBuilder : GraphQlQueryBuilder<TQueryBuilder>
    {
        public TQueryBuilder WithAllFields()
        {
            IncludeAllFields();
            return (TQueryBuilder)this;
        }

        public TQueryBuilder WithAllScalarFields()
        {
            IncludeFields(AllFields.Where(f => !f.IsComplex));
            return (TQueryBuilder)this;
        }

        protected TQueryBuilder WithScalarField(string fieldName, IDictionary<string, object> args = null)
        {
            IncludeScalarField(fieldName, args);
            return (TQueryBuilder)this;
        }

        protected TQueryBuilder WithObjectField(string fieldName, GraphQlQueryBuilder queryBuilder, IDictionary<string, object> args = null)
        {
            IncludeObjectField(fieldName, queryBuilder, args);
            return (TQueryBuilder)this;
        }
    }
    #endregion

    #region builder classes
    public enum HomeAvatar
    {
        [EnumMember(Value = "APARTMENT")] Apartment,
        [EnumMember(Value = "ROWHOUSE")] Rowhouse,
        [EnumMember(Value = "FLOORHOUSE1")] Floorhouse1,
        [EnumMember(Value = "FLOORHOUSE2")] Floorhouse2,
        [EnumMember(Value = "FLOORHOUSE3")] Floorhouse3,
        [EnumMember(Value = "COTTAGE")] Cottage,
        [EnumMember(Value = "CASTLE")] Castle
    }

    public enum HomeType
    {
        [EnumMember(Value = "APARTMENT")] Apartment,
        [EnumMember(Value = "ROWHOUSE")] Rowhouse,
        [EnumMember(Value = "HOUSE")] House,
        [EnumMember(Value = "COTTAGE")] Cottage
    }

    public enum HeatingSource
    {
        [EnumMember(Value = "AIR2AIR_HEATPUMP")] Air2AirHeatpump,
        [EnumMember(Value = "ELECTRICITY")] Electricity,
        [EnumMember(Value = "GROUND")] Ground,
        [EnumMember(Value = "DISTRICT_HEATING")] DistrictHeating,
        [EnumMember(Value = "ELECTRIC_BOILER")] ElectricBoiler,
        [EnumMember(Value = "AIR2WATER_HEATPUMP")] Air2WaterHeatpump,
        [EnumMember(Value = "OTHER")] Other
    }

    public enum PriceResolution
    {
        [EnumMember(Value = "HOURLY")] Hourly,
        [EnumMember(Value = "DAILY")] Daily
    }

    public enum ConsumptionResolution
    {
        [EnumMember(Value = "HOURLY")] Hourly,
        [EnumMember(Value = "DAILY")] Daily,
        [EnumMember(Value = "WEEKLY")] Weekly,
        [EnumMember(Value = "MONTHLY")] Monthly,
        [EnumMember(Value = "ANNUAL")] Annual
    }

    public enum AppScreen
    {
        [EnumMember(Value = "HOME")] Home,
        [EnumMember(Value = "REPORTS")] Reports,
        [EnumMember(Value = "CONSUMPTION")] Consumption,
        [EnumMember(Value = "COMPARISON")] Comparison,
        [EnumMember(Value = "DISAGGREGATION")] Disaggregation,
        [EnumMember(Value = "HOME_PROFILE")] HomeProfile,
        [EnumMember(Value = "CUSTOMER_PROFILE")] CustomerProfile,
        [EnumMember(Value = "METER_READING")] MeterReading,
        [EnumMember(Value = "NOTIFICATIONS")] Notifications,
        [EnumMember(Value = "INVOICES")] Invoices
    }

    public class TibberQueryBuilder : GraphQlQueryBuilder<TibberQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "viewer", IsComplex = true, QueryBuilderType = typeof(ViewerQueryBuilder) }
            };

        public TibberQueryBuilder WithViewer(ViewerQueryBuilder viewerQueryBuilder) => WithObjectField("viewer", viewerQueryBuilder);
    }

    public class ViewerQueryBuilder : GraphQlQueryBuilder<ViewerQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "login" },
                new FieldMetadata { Name = "name" },
                new FieldMetadata { Name = "accountType", IsComplex = true },
                new FieldMetadata { Name = "homes", IsComplex = true, QueryBuilderType = typeof(HomeQueryBuilder) },
                new FieldMetadata { Name = "home", IsComplex = true, QueryBuilderType = typeof(HomeQueryBuilder) }
            };

        public ViewerQueryBuilder WithLogin() => WithScalarField("login");

        public ViewerQueryBuilder WithName() => WithScalarField("name");

        public ViewerQueryBuilder WithAccountType() => WithScalarField("accountType");

        public ViewerQueryBuilder WithHomes(HomeQueryBuilder homeQueryBuilder) => WithObjectField("homes", homeQueryBuilder);

        public ViewerQueryBuilder WithHome(HomeQueryBuilder homeQueryBuilder, Guid id)
        {
            var args = new Dictionary<string, object> { { "id", id } };
            return WithObjectField("home", homeQueryBuilder, args);
        }
    }

    public class HomeQueryBuilder : GraphQlQueryBuilder<HomeQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "id" },
                new FieldMetadata { Name = "timeZone" },
                new FieldMetadata { Name = "appNickname" },
                new FieldMetadata { Name = "appAvatar" },
                new FieldMetadata { Name = "size" },
                new FieldMetadata { Name = "type" },
                new FieldMetadata { Name = "numberOfResidents" },
                new FieldMetadata { Name = "primaryHeatingSource" },
                new FieldMetadata { Name = "hasVentilationSystem" },
                new FieldMetadata { Name = "address", IsComplex = true, QueryBuilderType = typeof(AddressQueryBuilder) },
                new FieldMetadata { Name = "owner", IsComplex = true, QueryBuilderType = typeof(LegalEntityQueryBuilder) },
                new FieldMetadata { Name = "meteringPointData", IsComplex = true, QueryBuilderType = typeof(MeteringPointDataQueryBuilder) },
                new FieldMetadata { Name = "currentSubscription", IsComplex = true, QueryBuilderType = typeof(SubscriptionQueryBuilder) },
                new FieldMetadata { Name = "subscriptions", IsComplex = true, QueryBuilderType = typeof(SubscriptionQueryBuilder) },
                new FieldMetadata { Name = "consumption", IsComplex = true, QueryBuilderType = typeof(HomeConsumptionConnectionQueryBuilder) },
                new FieldMetadata { Name = "features", IsComplex = true, QueryBuilderType = typeof(HomeFeaturesQueryBuilder) }
            };

        public HomeQueryBuilder WithId() => WithScalarField("id");

        public HomeQueryBuilder WithTimeZone() => WithScalarField("timeZone");

        public HomeQueryBuilder WithAppNickname() => WithScalarField("appNickname");

        public HomeQueryBuilder WithAppAvatar() => WithScalarField("appAvatar");

        public HomeQueryBuilder WithSize() => WithScalarField("size");

        public HomeQueryBuilder WithType() => WithScalarField("type");

        public HomeQueryBuilder WithNumberOfResidents() => WithScalarField("numberOfResidents");

        public HomeQueryBuilder WithPrimaryHeatingSource() => WithScalarField("primaryHeatingSource");

        public HomeQueryBuilder WithHasVentilationSystem() => WithScalarField("hasVentilationSystem");

        public HomeQueryBuilder WithAddress(AddressQueryBuilder addressQueryBuilder) => WithObjectField("address", addressQueryBuilder);

        public HomeQueryBuilder WithOwner(LegalEntityQueryBuilder legalEntityQueryBuilder) => WithObjectField("owner", legalEntityQueryBuilder);

        public HomeQueryBuilder WithMeteringPointData(MeteringPointDataQueryBuilder meteringPointDataQueryBuilder) => WithObjectField("meteringPointData", meteringPointDataQueryBuilder);

        public HomeQueryBuilder WithCurrentSubscription(SubscriptionQueryBuilder subscriptionQueryBuilder) => WithObjectField("currentSubscription", subscriptionQueryBuilder);

        public HomeQueryBuilder WithSubscriptions(SubscriptionQueryBuilder subscriptionQueryBuilder) => WithObjectField("subscriptions", subscriptionQueryBuilder);

        public HomeQueryBuilder WithConsumption(HomeConsumptionConnectionQueryBuilder homeConsumptionConnectionQueryBuilder, ConsumptionResolution resolution, int? first = null, int? last = null, string before = null, string after = null, bool? filterEmptyNodes = null)
        {
            var args = new Dictionary<string, object> { { "resolution", resolution } };
            if (first != null)
                args.Add("first", first);

            if (last != null)
                args.Add("last", last);

            if (before != null)
                args.Add("before", before);

            if (after != null)
                args.Add("after", after);

            if (filterEmptyNodes != null)
                args.Add("filterEmptyNodes", filterEmptyNodes);

            return WithObjectField("consumption", homeConsumptionConnectionQueryBuilder, args);
        }

        public HomeQueryBuilder WithFeatures(HomeFeaturesQueryBuilder homeFeaturesQueryBuilder) => WithObjectField("features", homeFeaturesQueryBuilder);
    }

    public class AddressQueryBuilder : GraphQlQueryBuilder<AddressQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "address1" },
                new FieldMetadata { Name = "address2" },
                new FieldMetadata { Name = "address3" },
                new FieldMetadata { Name = "city" },
                new FieldMetadata { Name = "postalCode" },
                new FieldMetadata { Name = "country" },
                new FieldMetadata { Name = "latitude" },
                new FieldMetadata { Name = "longitude" }
            };

        public AddressQueryBuilder WithAddress1() => WithScalarField("address1");

        public AddressQueryBuilder WithAddress2() => WithScalarField("address2");

        public AddressQueryBuilder WithAddress3() => WithScalarField("address3");

        public AddressQueryBuilder WithCity() => WithScalarField("city");

        public AddressQueryBuilder WithPostalCode() => WithScalarField("postalCode");

        public AddressQueryBuilder WithCountry() => WithScalarField("country");

        public AddressQueryBuilder WithLatitude() => WithScalarField("latitude");

        public AddressQueryBuilder WithLongitude() => WithScalarField("longitude");
    }

    public class LegalEntityQueryBuilder : GraphQlQueryBuilder<LegalEntityQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "id" },
                new FieldMetadata { Name = "firstName" },
                new FieldMetadata { Name = "isCompany" },
                new FieldMetadata { Name = "name" },
                new FieldMetadata { Name = "middleName" },
                new FieldMetadata { Name = "lastName" },
                new FieldMetadata { Name = "organizationNo" },
                new FieldMetadata { Name = "language" },
                new FieldMetadata { Name = "contactInfo", IsComplex = true, QueryBuilderType = typeof(ContactInfoQueryBuilder) },
                new FieldMetadata { Name = "address", IsComplex = true, QueryBuilderType = typeof(AddressQueryBuilder) }
            };

        public LegalEntityQueryBuilder WithId() => WithScalarField("id");

        public LegalEntityQueryBuilder WithFirstName() => WithScalarField("firstName");

        public LegalEntityQueryBuilder WithIsCompany() => WithScalarField("isCompany");

        public LegalEntityQueryBuilder WithName() => WithScalarField("name");

        public LegalEntityQueryBuilder WithMiddleName() => WithScalarField("middleName");

        public LegalEntityQueryBuilder WithLastName() => WithScalarField("lastName");

        public LegalEntityQueryBuilder WithOrganizationNo() => WithScalarField("organizationNo");

        public LegalEntityQueryBuilder WithLanguage() => WithScalarField("language");

        public LegalEntityQueryBuilder WithContactInfo(ContactInfoQueryBuilder contactInfoQueryBuilder) => WithObjectField("contactInfo", contactInfoQueryBuilder);

        public LegalEntityQueryBuilder WithAddress(AddressQueryBuilder addressQueryBuilder) => WithObjectField("address", addressQueryBuilder);
    }

    public class ContactInfoQueryBuilder : GraphQlQueryBuilder<ContactInfoQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "email" },
                new FieldMetadata { Name = "mobile" }
            };

        public ContactInfoQueryBuilder WithEmail() => WithScalarField("email");

        public ContactInfoQueryBuilder WithMobile() => WithScalarField("mobile");
    }

    public class MeteringPointDataQueryBuilder : GraphQlQueryBuilder<MeteringPointDataQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "consumptionEan" },
                new FieldMetadata { Name = "gridCompany" },
                new FieldMetadata { Name = "productionEan" },
                new FieldMetadata { Name = "energyTaxType" },
                new FieldMetadata { Name = "vatType" },
                new FieldMetadata { Name = "estimatedAnnualConsumption" }
            };

        public MeteringPointDataQueryBuilder WithConsumptionEan() => WithScalarField("consumptionEan");

        public MeteringPointDataQueryBuilder WithGridCompany() => WithScalarField("gridCompany");

        public MeteringPointDataQueryBuilder WithProductionEan() => WithScalarField("productionEan");

        public MeteringPointDataQueryBuilder WithEnergyTaxType() => WithScalarField("energyTaxType");

        public MeteringPointDataQueryBuilder WithVatType() => WithScalarField("vatType");

        public MeteringPointDataQueryBuilder WithEstimatedAnnualConsumption() => WithScalarField("estimatedAnnualConsumption");
    }

    public class SubscriptionQueryBuilder : GraphQlQueryBuilder<SubscriptionQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "id" },
                new FieldMetadata { Name = "subscriber", IsComplex = true, QueryBuilderType = typeof(LegalEntityQueryBuilder) },
                new FieldMetadata { Name = "validFrom" },
                new FieldMetadata { Name = "validTo" },
                new FieldMetadata { Name = "status" },
                new FieldMetadata { Name = "statusReason" },
                new FieldMetadata { Name = "priceInfo", IsComplex = true, QueryBuilderType = typeof(PriceInfoQueryBuilder) }
            };

        public SubscriptionQueryBuilder WithId() => WithScalarField("id");

        public SubscriptionQueryBuilder WithSubscriber(LegalEntityQueryBuilder legalEntityQueryBuilder) => WithObjectField("subscriber", legalEntityQueryBuilder);

        public SubscriptionQueryBuilder WithValidFrom() => WithScalarField("validFrom");

        public SubscriptionQueryBuilder WithValidTo() => WithScalarField("validTo");

        public SubscriptionQueryBuilder WithStatus() => WithScalarField("status");

        public SubscriptionQueryBuilder WithStatusReason() => WithScalarField("statusReason");

        public SubscriptionQueryBuilder WithPriceInfo(PriceInfoQueryBuilder priceInfoQueryBuilder) => WithObjectField("priceInfo", priceInfoQueryBuilder);
    }

    public class PriceInfoQueryBuilder : GraphQlQueryBuilder<PriceInfoQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "current", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) },
                new FieldMetadata { Name = "today", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) },
                new FieldMetadata { Name = "tomorrow", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) },
                new FieldMetadata { Name = "range", IsComplex = true, QueryBuilderType = typeof(SubscriptionPriceConnectionQueryBuilder) }
            };

        public PriceInfoQueryBuilder WithCurrent(PriceQueryBuilder priceQueryBuilder) => WithObjectField("current", priceQueryBuilder);

        public PriceInfoQueryBuilder WithToday(PriceQueryBuilder priceQueryBuilder) => WithObjectField("today", priceQueryBuilder);

        public PriceInfoQueryBuilder WithTomorrow(PriceQueryBuilder priceQueryBuilder) => WithObjectField("tomorrow", priceQueryBuilder);

        public PriceInfoQueryBuilder WithRange(SubscriptionPriceConnectionQueryBuilder subscriptionPriceConnectionQueryBuilder, PriceResolution resolution, int? first = null, int? last = null, string before = null, string after = null)
        {
            var args = new Dictionary<string, object> { { "resolution", resolution } };
            if (first != null)
                args.Add("first", first);

            if (last != null)
                args.Add("last", last);

            if (before != null)
                args.Add("before", before);

            if (after != null)
                args.Add("after", after);

            return WithObjectField("range", subscriptionPriceConnectionQueryBuilder, args);
        }
    }

    public class PriceQueryBuilder : GraphQlQueryBuilder<PriceQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "total" },
                new FieldMetadata { Name = "energy" },
                new FieldMetadata { Name = "tax" },
                new FieldMetadata { Name = "startsAt" },
                new FieldMetadata { Name = "currency" }
            };

        public PriceQueryBuilder WithTotal() => WithScalarField("total");

        public PriceQueryBuilder WithEnergy() => WithScalarField("energy");

        public PriceQueryBuilder WithTax() => WithScalarField("tax");

        public PriceQueryBuilder WithStartsAt() => WithScalarField("startsAt");

        public PriceQueryBuilder WithCurrency() => WithScalarField("currency");
    }

    public class SubscriptionPriceConnectionQueryBuilder : GraphQlQueryBuilder<SubscriptionPriceConnectionQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "pageInfo", IsComplex = true, QueryBuilderType = typeof(SubscriptionPriceConnectionPageInfoQueryBuilder) },
                new FieldMetadata { Name = "edges", IsComplex = true, QueryBuilderType = typeof(SubscriptionPriceEdgeQueryBuilder) },
                new FieldMetadata { Name = "nodes", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) }
            };

        public SubscriptionPriceConnectionQueryBuilder WithPageInfo(SubscriptionPriceConnectionPageInfoQueryBuilder subscriptionPriceConnectionPageInfoQueryBuilder) => WithObjectField("pageInfo", subscriptionPriceConnectionPageInfoQueryBuilder);

        public SubscriptionPriceConnectionQueryBuilder WithEdges(SubscriptionPriceEdgeQueryBuilder subscriptionPriceEdgeQueryBuilder) => WithObjectField("edges", subscriptionPriceEdgeQueryBuilder);

        public SubscriptionPriceConnectionQueryBuilder WithNodes(PriceQueryBuilder priceQueryBuilder) => WithObjectField("nodes", priceQueryBuilder);
    }

    public class SubscriptionPriceConnectionPageInfoQueryBuilder : GraphQlQueryBuilder<SubscriptionPriceConnectionPageInfoQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "endCursor" },
                new FieldMetadata { Name = "hasNextPage" },
                new FieldMetadata { Name = "hasPreviousPage" },
                new FieldMetadata { Name = "startCursor" },
                new FieldMetadata { Name = "resolution" },
                new FieldMetadata { Name = "currency" },
                new FieldMetadata { Name = "count" },
                new FieldMetadata { Name = "precision" },
                new FieldMetadata { Name = "minEnergy" },
                new FieldMetadata { Name = "minTotal" },
                new FieldMetadata { Name = "maxEnergy" },
                new FieldMetadata { Name = "maxTotal" }
            };

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithEndCursor() => WithScalarField("endCursor");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithHasNextPage() => WithScalarField("hasNextPage");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithHasPreviousPage() => WithScalarField("hasPreviousPage");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithStartCursor() => WithScalarField("startCursor");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithResolution() => WithScalarField("resolution");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithCurrency() => WithScalarField("currency");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithCount() => WithScalarField("count");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithPrecision() => WithScalarField("precision");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithMinEnergy() => WithScalarField("minEnergy");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithMinTotal() => WithScalarField("minTotal");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithMaxEnergy() => WithScalarField("maxEnergy");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithMaxTotal() => WithScalarField("maxTotal");
    }

    public class SubscriptionPriceEdgeQueryBuilder : GraphQlQueryBuilder<SubscriptionPriceEdgeQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "cursor" },
                new FieldMetadata { Name = "node", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) }
            };

        public SubscriptionPriceEdgeQueryBuilder WithCursor() => WithScalarField("cursor");

        public SubscriptionPriceEdgeQueryBuilder WithNode(PriceQueryBuilder priceQueryBuilder) => WithObjectField("node", priceQueryBuilder);
    }

    public class HomeConsumptionConnectionQueryBuilder : GraphQlQueryBuilder<HomeConsumptionConnectionQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "pageInfo", IsComplex = true, QueryBuilderType = typeof(HomeConsumptionPageInfoQueryBuilder) },
                new FieldMetadata { Name = "nodes", IsComplex = true, QueryBuilderType = typeof(ConsumptionQueryBuilder) },
                new FieldMetadata { Name = "edges", IsComplex = true, QueryBuilderType = typeof(HomeConsumptionEdgeQueryBuilder) }
            };

        public HomeConsumptionConnectionQueryBuilder WithPageInfo(HomeConsumptionPageInfoQueryBuilder homeConsumptionPageInfoQueryBuilder) => WithObjectField("pageInfo", homeConsumptionPageInfoQueryBuilder);

        public HomeConsumptionConnectionQueryBuilder WithNodes(ConsumptionQueryBuilder consumptionQueryBuilder) => WithObjectField("nodes", consumptionQueryBuilder);

        public HomeConsumptionConnectionQueryBuilder WithEdges(HomeConsumptionEdgeQueryBuilder homeConsumptionEdgeQueryBuilder) => WithObjectField("edges", homeConsumptionEdgeQueryBuilder);
    }

    public class HomeConsumptionPageInfoQueryBuilder : GraphQlQueryBuilder<HomeConsumptionPageInfoQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "endCursor" },
                new FieldMetadata { Name = "hasNextPage" },
                new FieldMetadata { Name = "hasPreviousPage" },
                new FieldMetadata { Name = "startCursor" },
                new FieldMetadata { Name = "count" },
                new FieldMetadata { Name = "currency" },
                new FieldMetadata { Name = "totalCost" },
                new FieldMetadata { Name = "energyCost" },
                new FieldMetadata { Name = "filtered" }
            };

        public HomeConsumptionPageInfoQueryBuilder WithEndCursor() => WithScalarField("endCursor");

        public HomeConsumptionPageInfoQueryBuilder WithHasNextPage() => WithScalarField("hasNextPage");

        public HomeConsumptionPageInfoQueryBuilder WithHasPreviousPage() => WithScalarField("hasPreviousPage");

        public HomeConsumptionPageInfoQueryBuilder WithStartCursor() => WithScalarField("startCursor");

        public HomeConsumptionPageInfoQueryBuilder WithCount() => WithScalarField("count");

        public HomeConsumptionPageInfoQueryBuilder WithCurrency() => WithScalarField("currency");

        public HomeConsumptionPageInfoQueryBuilder WithTotalCost() => WithScalarField("totalCost");

        public HomeConsumptionPageInfoQueryBuilder WithEnergyCost() => WithScalarField("energyCost");

        public HomeConsumptionPageInfoQueryBuilder WithFiltered() => WithScalarField("filtered");
    }

    public class ConsumptionQueryBuilder : GraphQlQueryBuilder<ConsumptionQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "from" },
                new FieldMetadata { Name = "to" },
                new FieldMetadata { Name = "unitPrice" },
                new FieldMetadata { Name = "unitPriceVAT" },
                new FieldMetadata { Name = "consumption" },
                new FieldMetadata { Name = "consumptionUnit" },
                new FieldMetadata { Name = "totalCost" },
                new FieldMetadata { Name = "unitCost" },
                new FieldMetadata { Name = "currency" }
            };

        public ConsumptionQueryBuilder WithFrom() => WithScalarField("from");

        public ConsumptionQueryBuilder WithTo() => WithScalarField("to");

        public ConsumptionQueryBuilder WithUnitPrice() => WithScalarField("unitPrice");

        public ConsumptionQueryBuilder WithUnitPriceVAT() => WithScalarField("unitPriceVAT");

        public ConsumptionQueryBuilder WithConsumption() => WithScalarField("consumption");

        public ConsumptionQueryBuilder WithConsumptionUnit() => WithScalarField("consumptionUnit");

        public ConsumptionQueryBuilder WithTotalCost() => WithScalarField("totalCost");

        public ConsumptionQueryBuilder WithUnitCost() => WithScalarField("unitCost");

        public ConsumptionQueryBuilder WithCurrency() => WithScalarField("currency");
    }

    public class HomeConsumptionEdgeQueryBuilder : GraphQlQueryBuilder<HomeConsumptionEdgeQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "cursor" },
                new FieldMetadata { Name = "node", IsComplex = true, QueryBuilderType = typeof(ConsumptionQueryBuilder) }
            };

        public HomeConsumptionEdgeQueryBuilder WithCursor() => WithScalarField("cursor");

        public HomeConsumptionEdgeQueryBuilder WithNode(ConsumptionQueryBuilder consumptionQueryBuilder) => WithObjectField("node", consumptionQueryBuilder);
    }

    public class HomeFeaturesQueryBuilder : GraphQlQueryBuilder<HomeFeaturesQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "realTimeConsumptionEnabled" }
            };

        public HomeFeaturesQueryBuilder WithRealTimeConsumptionEnabled() => WithScalarField("realTimeConsumptionEnabled");
    }

    public class RootMutationQueryBuilder : GraphQlQueryBuilder<RootMutationQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "sendMeterReading", IsComplex = true, QueryBuilderType = typeof(MeterReadingResponseQueryBuilder) },
                new FieldMetadata { Name = "updateHome", IsComplex = true, QueryBuilderType = typeof(HomeQueryBuilder) },
                new FieldMetadata { Name = "sendPushNotification", IsComplex = true, QueryBuilderType = typeof(PushNotificationResponseQueryBuilder) }
            };

        public RootMutationQueryBuilder WithSendMeterReading(MeterReadingResponseQueryBuilder meterReadingResponseQueryBuilder, MeterReadingInput input)
        {
            var args = new Dictionary<string, object> { { "input", input } };
            return WithObjectField("sendMeterReading", meterReadingResponseQueryBuilder, args);
        }

        public RootMutationQueryBuilder WithUpdateHome(HomeQueryBuilder homeQueryBuilder, UpdateHomeInput input)
        {
            var args = new Dictionary<string, object> { { "input", input } };
            return WithObjectField("updateHome", homeQueryBuilder, args);
        }

        public RootMutationQueryBuilder WithSendPushNotification(PushNotificationResponseQueryBuilder pushNotificationResponseQueryBuilder, PushNotificationInput input)
        {
            var args = new Dictionary<string, object> { { "input", input } };
            return WithObjectField("sendPushNotification", pushNotificationResponseQueryBuilder, args);
        }
    }

    public class MeterReadingResponseQueryBuilder : GraphQlQueryBuilder<MeterReadingResponseQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "homeId" },
                new FieldMetadata { Name = "time" },
                new FieldMetadata { Name = "reading" }
            };

        public MeterReadingResponseQueryBuilder WithHomeId() => WithScalarField("homeId");

        public MeterReadingResponseQueryBuilder WithTime() => WithScalarField("time");

        public MeterReadingResponseQueryBuilder WithReading() => WithScalarField("reading");
    }

    public class PushNotificationResponseQueryBuilder : GraphQlQueryBuilder<PushNotificationResponseQueryBuilder>
    {
        protected override IList<FieldMetadata> AllFields { get; } =
            new[]
            {
                new FieldMetadata { Name = "successful" },
                new FieldMetadata { Name = "pushedToNumberOfDevices" }
            };

        public PushNotificationResponseQueryBuilder WithSuccessful() => WithScalarField("successful");

        public PushNotificationResponseQueryBuilder WithPushedToNumberOfDevices() => WithScalarField("pushedToNumberOfDevices");
    }
    #endregion

    #region data classes
    public class Query
    {
        /// <summary>
        /// This contains data about the logged-in user
        /// </summary>
        public Viewer Viewer { get; set; }
    }

    public class Viewer
    {
        public string Login { get; set; }
        public string Name { get; set; }
        /// <summary>
        /// The type of account for the logged-in user.    
        /// </summary>
        public ICollection<string> AccountType { get; set; }
        /// <summary>
        /// All homes visible to the logged-in user
        /// </summary>
        public ICollection<Home> Homes { get; set; }
        /// <summary>
        /// Singular home
        /// </summary>
        public Home Home { get; set; }
    }

    public class Home
    {
        public Guid? Id { get; set; }
        /// <summary>
        /// The time zone the home resides in
        /// </summary>
        public string TimeZone { get; set; }
        /// <summary>
        /// The nickname given to the home by the user
        /// </summary>
        public string AppNickname { get; set; }
        /// <summary>
        /// The chosen avatar for the home
        /// </summary>
        public HomeAvatar? AppAvatar { get; set; }
        /// <summary>
        /// The size of the home in square meters
        /// </summary>
        public int? Size { get; set; }
        /// <summary>
        /// The type of home.
        /// </summary>
        public HomeType? Type { get; set; }
        /// <summary>
        /// The number of people living in the home
        /// </summary>
        public int? NumberOfResidents { get; set; }
        /// <summary>
        /// The primary form of heating in the household
        /// </summary>
        public HeatingSource? PrimaryHeatingSource { get; set; }
        /// <summary>
        /// Whether the home has a ventilation system
        /// </summary>
        public bool? HasVentilationSystem { get; set; }
        public Address Address { get; set; }
        /// <summary>
        /// The registered owner of the house
        /// </summary>
        public LegalEntity Owner { get; set; }
        public MeteringPointData MeteringPointData { get; set; }
        /// <summary>
        /// The current/latest subscription related to the home
        /// </summary>
        public Subscription CurrentSubscription { get; set; }
        /// <summary>
        /// All historic subscriptions related to the home
        /// </summary>
        public ICollection<Subscription> Subscriptions { get; set; }
        /// <summary>
        /// Consumption connection
        /// </summary>
        public HomeConsumptionConnection Consumption { get; set; }
        public HomeFeatures Features { get; set; }
    }

    public class Address
    {
        public string Address1 { get; set; }
        public string Address2 { get; set; }
        public string Address3 { get; set; }
        public string City { get; set; }
        public string PostalCode { get; set; }
        public string Country { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
    }

    public class LegalEntity
    {
        public Guid? Id { get; set; }
        /// <summary>
        /// First/Christian name of the entity
        /// </summary>
        public string FirstName { get; set; }
        /// <summary>
        /// Equal to 'true' if the entity is a company
        /// </summary>
        public bool? IsCompany { get; set; }
        /// <summary>
        /// Full name of the entity
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Middle name of the entity
        /// </summary>
        public string MiddleName { get; set; }
        /// <summary>
        /// Last name of the entity
        /// </summary>
        public string LastName { get; set; }
        /// <summary>
        /// Organization number - only populated if entity is a company (isCompany=true)
        /// </summary>
        public string OrganizationNo { get; set; }
        /// <summary>
        /// The primary language of the entity
        /// </summary>
        public string Language { get; set; }
        /// <summary>
        /// Contact information of the entity
        /// </summary>
        public ContactInfo ContactInfo { get; set; }
        /// <summary>
        /// Address information for the entity
        /// </summary>
        public Address Address { get; set; }
    }

    public class ContactInfo
    {
        /// <summary>
        /// The email of the corresponding entity
        /// </summary>
        public string Email { get; set; }
        /// <summary>
        /// The mobile phone no of the corresponding entity
        /// </summary>
        public string Mobile { get; set; }
    }

    public class MeteringPointData
    {
        /// <summary>
        /// The metering point ID of the home
        /// </summary>
        public string ConsumptionEan { get; set; }
        /// <summary>
        /// The grid provider of the home
        /// </summary>
        public string GridCompany { get; set; }
        /// <summary>
        /// The metering point ID of the production
        /// </summary>
        public string ProductionEan { get; set; }
        /// <summary>
        /// The eltax type of the home (only relevant for Swedish homes)
        /// </summary>
        public string EnergyTaxType { get; set; }
        /// <summary>
        /// The VAT type of the home (only relevant for Norwegian homes)
        /// </summary>
        public string VatType { get; set; }
        /// <summary>
        /// The estimated annual consumption as reported by grid company
        /// </summary>
        public int? EstimatedAnnualConsumption { get; set; }
    }

    public class Subscription
    {
        public Guid? Id { get; set; }
        /// <summary>
        /// The owner of the subscription
        /// </summary>
        public LegalEntity Subscriber { get; set; }
        /// <summary>
        /// The time the subscription started
        /// </summary>
        public DateTimeOffset? ValidFrom { get; set; }
        /// <summary>
        /// The time the subscription ended
        /// </summary>
        public DateTimeOffset? ValidTo { get; set; }
        /// <summary>
        /// The current status of the subscription
        /// </summary>
        public string Status { get; set; }
        /// <summary>
        /// Price information related to the subscription
        /// </summary>
        public PriceInfo PriceInfo { get; set; }
    }

    public class PriceInfo
    {
        /// <summary>
        /// The energy price right now
        /// </summary>
        public Price Current { get; set; }
        /// <summary>
        /// The hourly prices of the current day
        /// </summary>
        public ICollection<Price> Today { get; set; }
        /// <summary>
        /// The hourly prices of the upcoming day
        /// </summary>
        public ICollection<Price> Tomorrow { get; set; }
        /// <summary>
        /// Range of prices relative to before/after arguments
        /// </summary>
        public SubscriptionPriceConnection Range { get; set; }
    }

    public class Price
    {
        /// <summary>
        /// The total price (energy+tax)
        /// </summary>
        public decimal? Total { get; set; }
        /// <summary>
        /// The energy part of the price
        /// </summary>
        public decimal? Energy { get; set; }
        /// <summary>
        /// The tax part of the price (elcertificate, eltax (Sweden only) and VAT)
        /// </summary>
        public decimal? Tax { get; set; }
        /// <summary>
        /// The start time of the price
        /// </summary>
        public string StartsAt { get; set; }
        /// <summary>
        /// The price currency
        /// </summary>
        public string Currency { get; set; }
    }

    public class SubscriptionPriceConnection
    {
        public SubscriptionPriceConnectionPageInfo PageInfo { get; set; }
        public ICollection<SubscriptionPriceEdge> Edges { get; set; }
        public ICollection<Price> Nodes { get; set; }
    }

    public class SubscriptionPriceConnectionPageInfo
    {
        public string EndCursor { get; set; }
        public bool? HasNextPage { get; set; }
        public bool? HasPreviousPage { get; set; }
        public string StartCursor { get; set; }
        public string Resolution { get; set; }
        public string Currency { get; set; }
        public int? Count { get; set; }
        public string Precision { get; set; }
        public decimal? MinEnergy { get; set; }
        public decimal? MinTotal { get; set; }
        public decimal? MaxEnergy { get; set; }
        public decimal? MaxTotal { get; set; }
    }

    public class SubscriptionPriceEdge
    {
        /// <summary>
        /// The global ID of the element
        /// </summary>
        public string Cursor { get; set; }
        /// <summary>
        /// A single price node
        /// </summary>
        public Price Node { get; set; }
    }

    public class HomeConsumptionConnection
    {
        public HomeConsumptionPageInfo PageInfo { get; set; }
        public ICollection<ConsumptionEntry> Nodes { get; set; }
        public ICollection<HomeConsumptionEdge> Edges { get; set; }
    }

    public class HomeConsumptionPageInfo
    {
        /// <summary>
        /// The global ID of the last element in the list
        /// </summary>
        public string EndCursor { get; set; }
        /// <summary>
        /// True if further pages are available
        /// </summary>
        public bool? HasNextPage { get; set; }
        /// <summary>
        /// True if previous pages are available
        /// </summary>
        public bool? HasPreviousPage { get; set; }
        /// <summary>
        /// The global ID of the first element in the list
        /// </summary>
        public string StartCursor { get; set; }
        /// <summary>
        /// The number of elements in the list
        /// </summary>
        public int? Count { get; set; }
        /// <summary>
        /// The currency of the page
        /// </summary>
        public string Currency { get; set; }
        /// <summary>
        /// Page total cost
        /// </summary>
        public decimal? TotalCost { get; set; }
        /// <summary>
        /// Page energy cost
        /// </summary>
        public decimal? EnergyCost { get; set; }
        /// <summary>
        /// Number of entries that have been filtered from result set due to empty nodes
        /// </summary>
        public int? Filtered { get; set; }
    }

    public class ConsumptionEntry
    {
        public DateTimeOffset? From { get; set; }
        public DateTimeOffset? To { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? UnitPriceVAT { get; set; }
        /// <summary>
        /// kWh consumed
        /// </summary>
        public decimal? Consumption { get; set; }
        public string ConsumptionUnit { get; set; }
        /// <summary>
        /// Total cost of the consumption
        /// </summary>
        public decimal? TotalCost { get; set; }
        public decimal? UnitCost { get; set; }
        /// <summary>
        /// The cost currency
        /// </summary>
        public string Currency { get; set; }
    }

    public class HomeConsumptionEdge
    {
        public string Cursor { get; set; }
        public ConsumptionEntry Node { get; set; }
    }

    public class HomeFeatures
    {
        /// <summary>
        /// Tibber pulse is paired.
        /// </summary>
        public bool? RealTimeConsumptionEnabled { get; set; }
    }

    public class RootMutation
    {
        /// <summary>
        /// Send meter reading for home (only available for Norwegian users)
        /// </summary>
        public MeterReadingResponse SendMeterReading { get; set; }
        /// <summary>
        /// Update home information
        /// </summary>
        public Home UpdateHome { get; set; }
        /// <summary>
        /// Send notification to Tibber app on registered devices
        /// </summary>
        public PushNotificationResponse SendPushNotification { get; set; }
    }

    public class MeterReadingResponse
    {
        public Guid? HomeId { get; set; }
        public string Time { get; set; }
        public int? Reading { get; set; }
    }

    public class PushNotificationResponse
    {
        public bool? Successful { get; set; }
        public int? PushedToNumberOfDevices { get; set; }
    }
    #endregion

    #region input classes
    public class MeterReadingInput
    {
        public Guid? HomeId { get; set; }
        public string Time { get; set; }
        public int? Reading { get; set; }
    }

    public class UpdateHomeInput
    {
        public Guid? HomeId { get; set; }
        public string AppNickname { get; set; }
        /// <summary>
        /// The chosen avatar for the home
        /// </summary>
        public HomeAvatar? AppAvatar { get; set; }
        /// <summary>
        /// The size of the home in square meters
        /// </summary>
        public int? Size { get; set; }
        /// <summary>
        /// The type of home.
        /// </summary>
        public HomeType? Type { get; set; }
        /// <summary>
        /// The number of people living in the home
        /// </summary>
        public int? NumberOfResidents { get; set; }
        /// <summary>
        /// The primary form of heating in the household
        /// </summary>
        public HeatingSource? PrimaryHeatingSource { get; set; }
        /// <summary>
        /// Whether the home has a ventilation system
        /// </summary>
        public bool? HasVentilationSystem { get; set; }
    }

    public class PushNotificationInput
    {
        public string Title { get; set; }
        public string Message { get; set; }
        public AppScreen? ScreenToOpen { get; set; }
    }
    #endregion
}
