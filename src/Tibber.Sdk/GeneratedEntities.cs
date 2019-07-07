using System;
using System.Collections;
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

    internal static class GraphQlQueryHelper
    {
        public static string GetIndentation(int level, byte indentationSize) => new String(' ', level * indentationSize);

        public static string BuildArgumentValue(object value, Formatting formatting, int level, byte indentationSize)
        {
            if (value is Enum @enum)
                return ConvertEnumToString(@enum);

            if (value is bool @bool)
                return @bool ? "true" : "false";

            if (value is DateTime dateTime)
                return $"\"{dateTime:O}\"";

            if (value is DateTimeOffset dateTimeOffset)
                return $"\"{dateTimeOffset:O}\"";

            if (value is IGraphQlInputObject inputObject)
                return BuildInputObject(inputObject, formatting, level + 2, indentationSize);

            if (value is String || value is Guid)
                return $"\"{value}\"";

            if (value is IEnumerable enumerable)
            {
                var builder = new StringBuilder();
                builder.Append("[");
                var delimiter = String.Empty;
                foreach (var item in enumerable)
                {
                    builder.Append(delimiter);

                    if (formatting == Formatting.Indented)
                    {
                        builder.AppendLine();
                        builder.Append(GetIndentation(level + 1, indentationSize));
                    }

                    builder.Append(BuildArgumentValue(item, formatting, level, indentationSize));
                    delimiter = ",";
                }

                builder.Append("]");
                return builder.ToString();
            }

            if (value is short || value is ushort || value is byte || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal)
                return Convert.ToString(value, CultureInfo.InvariantCulture);

            var argumentValue = Convert.ToString(value, CultureInfo.InvariantCulture);
            return $"\"{argumentValue}\"";
        }

        public static string BuildInputObject(IGraphQlInputObject inputObject, Formatting formatting, int level, byte indentationSize)
        {
            var builder = new StringBuilder();
            builder.Append("{");

            var isIndentedFormatting = formatting == Formatting.Indented;
            string valueSeparator;
            if (isIndentedFormatting)
            {
                builder.AppendLine();
                valueSeparator = ": ";
            }
            else
                valueSeparator = ":";

            var separator = String.Empty;
            foreach (var propertyValue in inputObject.GetPropertyValues().Where(p => p.Value != null))
            {
                var value = BuildArgumentValue(propertyValue.Value, formatting, level, indentationSize);
                builder.Append(isIndentedFormatting ? GetIndentation(level, indentationSize) : separator);
                builder.Append(propertyValue.Name);
                builder.Append(valueSeparator);
                builder.Append(value);

                separator = ",";

                if (isIndentedFormatting)
                    builder.AppendLine();
            }

            if (isIndentedFormatting)
                builder.Append(GetIndentation(level - 1, indentationSize));

            builder.Append("}");

            return builder.ToString();
        }

        private static string ConvertEnumToString(Enum @enum)
        {
            var enumMember = @enum.GetType().GetTypeInfo().GetField(@enum.ToString());
            if (enumMember == null)
                throw new InvalidOperationException("enum member resolution failed");

            var enumMemberAttribute = (EnumMemberAttribute)enumMember.GetCustomAttribute(typeof(EnumMemberAttribute));

            return enumMemberAttribute == null
                ? @enum.ToString()
                : enumMemberAttribute.Value;
        }
    }

    public struct InputPropertyInfo
    {
        public string Name { get; set; }
        public object Value { get; set; }
    }

    internal interface IGraphQlInputObject
    {
        IEnumerable<InputPropertyInfo> GetPropertyValues();
    }

    public abstract class GraphQlQueryBuilder
    {
        private readonly Dictionary<string, GraphQlFieldCriteria> _fieldCriteria = new Dictionary<string, GraphQlFieldCriteria>();

        protected virtual string Prefix => null;

        protected abstract IList<FieldMetadata> AllFields { get; }

        public void Clear() => _fieldCriteria.Clear();

        public void IncludeAllFields() => IncludeFields(AllFields);

        public string Build(Formatting formatting = Formatting.None, byte indentationSize = 2) => Build(formatting, 1, indentationSize);

        protected string Build(Formatting formatting, int level, byte indentationSize)
        {
            var isIndentedFormatting = formatting == Formatting.Indented;

            var builder = new StringBuilder();

            if (!String.IsNullOrEmpty(Prefix))
            {
                builder.Append(Prefix);

                if (isIndentedFormatting)
                    builder.Append(" ");
            }

            builder.Append("{");

            if (isIndentedFormatting)
                builder.AppendLine();

            var separator = String.Empty;
            foreach (var criteria in _fieldCriteria.Values)
            {
                var fieldCriteria = criteria.Build(formatting, level, indentationSize);
                if (isIndentedFormatting)
                    builder.AppendLine(fieldCriteria);
                else if (!String.IsNullOrEmpty(fieldCriteria))
                {
                    builder.Append(separator);
                    builder.Append(fieldCriteria);
                }

                separator = ",";
            }

            if (isIndentedFormatting)
                builder.Append(GraphQlQueryHelper.GetIndentation(level - 1, indentationSize));

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

        private abstract class GraphQlFieldCriteria
        {
            protected readonly string FieldName;
            private readonly IDictionary<string, object> _args;

            protected GraphQlFieldCriteria(string fieldName, IDictionary<string, object> args)
            {
                FieldName = fieldName;
                _args = args;
            }

            public abstract string Build(Formatting formatting, int level, byte indentationSize);

            protected string BuildArgumentClause(Formatting formatting, int level, byte indentationSize)
            {
                var separator = formatting == Formatting.Indented ? " " : null;
                return
                    _args?.Count > 0
                        ? $"({String.Join($",{separator}", _args.Select(kvp => $"{kvp.Key}:{separator}{GraphQlQueryHelper.BuildArgumentValue(kvp.Value, formatting, level, indentationSize)}"))}){separator}"
                        : String.Empty;
            }
        }

        private class GraphQlScalarFieldCriteria : GraphQlFieldCriteria
        {
            public GraphQlScalarFieldCriteria(string fieldName, IDictionary<string, object> args) : base(fieldName, args)
            {
            }

            public override string Build(Formatting formatting, int level, byte indentationSize)
            {
                var builder = new StringBuilder();
                if (formatting == Formatting.Indented)
                    builder.Append(GraphQlQueryHelper.GetIndentation(level, indentationSize));

                builder.Append(FieldName);
                builder.Append(BuildArgumentClause(formatting, level, indentationSize));
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

            public override string Build(Formatting formatting, int level, byte indentationSize)
            {
                if (_objectQueryBuilder._fieldCriteria.Count == 0)
                    return String.Empty;

                var builder = new StringBuilder();
                var fieldName = FieldName;
                if (formatting == Formatting.Indented)
                    fieldName = $"{GraphQlQueryHelper.GetIndentation(level, indentationSize)}{FieldName} ";

                builder.Append(fieldName);
                builder.Append(BuildArgumentClause(formatting, level, indentationSize));
                builder.Append(_objectQueryBuilder.Build(formatting, level + 1, indentationSize));
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

    #region shared types
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

    /// <summary>
    /// Price level based on trailing price average (3 days for hourly values and 30 days for daily values)
    /// </summary>
    public enum PriceLevel
    {
        /// <summary>
        /// The price is greater than 90 % and smaller than 115 % compared to average price.
        /// </summary>
        [EnumMember(Value = "NORMAL")] Normal,

        /// <summary>
        /// The price is greater than 60 % and smaller or equal to 90 % compared to average price.
        /// </summary>
        [EnumMember(Value = "CHEAP")] Cheap,

        /// <summary>
        /// The price is smaller or equal to 60 % compared to average price.
        /// </summary>
        [EnumMember(Value = "VERY_CHEAP")] VeryCheap,

        /// <summary>
        /// The price is greater or equal to 115 % and smaller than 140 % compared to average price.
        /// </summary>
        [EnumMember(Value = "EXPENSIVE")] Expensive,

        /// <summary>
        /// The price is greater or equal to 140 % compared to average price.
        /// </summary>
        [EnumMember(Value = "VERY_EXPENSIVE")] VeryExpensive
    }

    public enum PriceResolution
    {
        [EnumMember(Value = "HOURLY")] Hourly,
        [EnumMember(Value = "DAILY")] Daily
    }

    public enum EnergyResolution
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
    #endregion

    #region builder classes
    public class TibberQueryBuilder : GraphQlQueryBuilder<TibberQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "viewer", IsComplex = true, QueryBuilderType = typeof(ViewerQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public TibberQueryBuilder WithViewer(ViewerQueryBuilder viewerQueryBuilder) => WithObjectField("viewer", viewerQueryBuilder);
    }

    public class ViewerQueryBuilder : GraphQlQueryBuilder<ViewerQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "login" },
            new FieldMetadata { Name = "name" },
            new FieldMetadata { Name = "accountType", IsComplex = true },
            new FieldMetadata { Name = "homes", IsComplex = true, QueryBuilderType = typeof(HomeQueryBuilder) },
            new FieldMetadata { Name = "home", IsComplex = true, QueryBuilderType = typeof(HomeQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

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
        private static readonly FieldMetadata[] AllFieldMetadata =
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
            new FieldMetadata { Name = "mainFuseSize" },
            new FieldMetadata { Name = "address", IsComplex = true, QueryBuilderType = typeof(AddressQueryBuilder) },
            new FieldMetadata { Name = "owner", IsComplex = true, QueryBuilderType = typeof(LegalEntityQueryBuilder) },
            new FieldMetadata { Name = "meteringPointData", IsComplex = true, QueryBuilderType = typeof(MeteringPointDataQueryBuilder) },
            new FieldMetadata { Name = "currentSubscription", IsComplex = true, QueryBuilderType = typeof(SubscriptionQueryBuilder) },
            new FieldMetadata { Name = "subscriptions", IsComplex = true, QueryBuilderType = typeof(SubscriptionQueryBuilder) },
            new FieldMetadata { Name = "consumption", IsComplex = true, QueryBuilderType = typeof(HomeConsumptionConnectionQueryBuilder) },
            new FieldMetadata { Name = "production", IsComplex = true, QueryBuilderType = typeof(HomeProductionConnectionQueryBuilder) },
            new FieldMetadata { Name = "features", IsComplex = true, QueryBuilderType = typeof(HomeFeaturesQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeQueryBuilder WithId() => WithScalarField("id");

        public HomeQueryBuilder WithTimeZone() => WithScalarField("timeZone");

        public HomeQueryBuilder WithAppNickname() => WithScalarField("appNickname");

        public HomeQueryBuilder WithAppAvatar() => WithScalarField("appAvatar");

        public HomeQueryBuilder WithSize() => WithScalarField("size");

        public HomeQueryBuilder WithType() => WithScalarField("type");

        public HomeQueryBuilder WithNumberOfResidents() => WithScalarField("numberOfResidents");

        public HomeQueryBuilder WithPrimaryHeatingSource() => WithScalarField("primaryHeatingSource");

        public HomeQueryBuilder WithHasVentilationSystem() => WithScalarField("hasVentilationSystem");

        public HomeQueryBuilder WithMainFuseSize() => WithScalarField("mainFuseSize");

        public HomeQueryBuilder WithAddress(AddressQueryBuilder addressQueryBuilder) => WithObjectField("address", addressQueryBuilder);

        public HomeQueryBuilder WithOwner(LegalEntityQueryBuilder legalEntityQueryBuilder) => WithObjectField("owner", legalEntityQueryBuilder);

        public HomeQueryBuilder WithMeteringPointData(MeteringPointDataQueryBuilder meteringPointDataQueryBuilder) => WithObjectField("meteringPointData", meteringPointDataQueryBuilder);

        public HomeQueryBuilder WithCurrentSubscription(SubscriptionQueryBuilder subscriptionQueryBuilder) => WithObjectField("currentSubscription", subscriptionQueryBuilder);

        public HomeQueryBuilder WithSubscriptions(SubscriptionQueryBuilder subscriptionQueryBuilder) => WithObjectField("subscriptions", subscriptionQueryBuilder);

        public HomeQueryBuilder WithConsumption(HomeConsumptionConnectionQueryBuilder homeConsumptionConnectionQueryBuilder, EnergyResolution resolution, int? first = null, int? last = null, string before = null,
            string after = null, bool? filterEmptyNodes = null)
        {
            var args = new Dictionary<string, object>();
            args.Add("resolution", resolution);
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

        public HomeQueryBuilder WithProduction(HomeProductionConnectionQueryBuilder homeProductionConnectionQueryBuilder, EnergyResolution resolution, int? first = null, int? last = null, string before = null,
            string after = null, bool? filterEmptyNodes = null)
        {
            var args = new Dictionary<string, object>();
            args.Add("resolution", resolution);
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

            return WithObjectField("production", homeProductionConnectionQueryBuilder, args);
        }

        public HomeQueryBuilder WithFeatures(HomeFeaturesQueryBuilder homeFeaturesQueryBuilder) => WithObjectField("features", homeFeaturesQueryBuilder);
    }

    public class AddressQueryBuilder : GraphQlQueryBuilder<AddressQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
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

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

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
        private static readonly FieldMetadata[] AllFieldMetadata =
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

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

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
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "email" },
            new FieldMetadata { Name = "mobile" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public ContactInfoQueryBuilder WithEmail() => WithScalarField("email");

        public ContactInfoQueryBuilder WithMobile() => WithScalarField("mobile");
    }

    public class MeteringPointDataQueryBuilder : GraphQlQueryBuilder<MeteringPointDataQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "consumptionEan" },
            new FieldMetadata { Name = "gridCompany" },
            new FieldMetadata { Name = "gridAreaCode" },
            new FieldMetadata { Name = "priceAreaCode" },
            new FieldMetadata { Name = "productionEan" },
            new FieldMetadata { Name = "energyTaxType" },
            new FieldMetadata { Name = "vatType" },
            new FieldMetadata { Name = "estimatedAnnualConsumption" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public MeteringPointDataQueryBuilder WithConsumptionEan() => WithScalarField("consumptionEan");

        public MeteringPointDataQueryBuilder WithGridCompany() => WithScalarField("gridCompany");

        public MeteringPointDataQueryBuilder WithGridAreaCode() => WithScalarField("gridAreaCode");

        public MeteringPointDataQueryBuilder WithPriceAreaCode() => WithScalarField("priceAreaCode");

        public MeteringPointDataQueryBuilder WithProductionEan() => WithScalarField("productionEan");

        public MeteringPointDataQueryBuilder WithEnergyTaxType() => WithScalarField("energyTaxType");

        public MeteringPointDataQueryBuilder WithVatType() => WithScalarField("vatType");

        public MeteringPointDataQueryBuilder WithEstimatedAnnualConsumption() => WithScalarField("estimatedAnnualConsumption");
    }

    public class SubscriptionQueryBuilder : GraphQlQueryBuilder<SubscriptionQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "id" },
            new FieldMetadata { Name = "subscriber", IsComplex = true, QueryBuilderType = typeof(LegalEntityQueryBuilder) },
            new FieldMetadata { Name = "validFrom" },
            new FieldMetadata { Name = "validTo" },
            new FieldMetadata { Name = "status" },
            new FieldMetadata { Name = "statusReason" },
            new FieldMetadata { Name = "priceInfo", IsComplex = true, QueryBuilderType = typeof(PriceInfoQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

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
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "current", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) },
            new FieldMetadata { Name = "today", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) },
            new FieldMetadata { Name = "tomorrow", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) },
            new FieldMetadata { Name = "range", IsComplex = true, QueryBuilderType = typeof(SubscriptionPriceConnectionQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

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
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "total" },
            new FieldMetadata { Name = "energy" },
            new FieldMetadata { Name = "tax" },
            new FieldMetadata { Name = "startsAt" },
            new FieldMetadata { Name = "currency" },
            new FieldMetadata { Name = "level" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public PriceQueryBuilder WithTotal() => WithScalarField("total");

        public PriceQueryBuilder WithEnergy() => WithScalarField("energy");

        public PriceQueryBuilder WithTax() => WithScalarField("tax");

        public PriceQueryBuilder WithStartsAt() => WithScalarField("startsAt");

        public PriceQueryBuilder WithCurrency() => WithScalarField("currency");

        public PriceQueryBuilder WithLevel() => WithScalarField("level");
    }

    public class SubscriptionPriceConnectionQueryBuilder : GraphQlQueryBuilder<SubscriptionPriceConnectionQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "pageInfo", IsComplex = true, QueryBuilderType = typeof(SubscriptionPriceConnectionPageInfoQueryBuilder) },
            new FieldMetadata { Name = "edges", IsComplex = true, QueryBuilderType = typeof(SubscriptionPriceEdgeQueryBuilder) },
            new FieldMetadata { Name = "nodes", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public SubscriptionPriceConnectionQueryBuilder WithPageInfo(SubscriptionPriceConnectionPageInfoQueryBuilder subscriptionPriceConnectionPageInfoQueryBuilder) => WithObjectField("pageInfo", subscriptionPriceConnectionPageInfoQueryBuilder);

        public SubscriptionPriceConnectionQueryBuilder WithEdges(SubscriptionPriceEdgeQueryBuilder subscriptionPriceEdgeQueryBuilder) => WithObjectField("edges", subscriptionPriceEdgeQueryBuilder);

        public SubscriptionPriceConnectionQueryBuilder WithNodes(PriceQueryBuilder priceQueryBuilder) => WithObjectField("nodes", priceQueryBuilder);
    }

    public class SubscriptionPriceConnectionPageInfoQueryBuilder : GraphQlQueryBuilder<SubscriptionPriceConnectionPageInfoQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
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

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

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

    public class PageInfoQueryBuilder : GraphQlQueryBuilder<PageInfoQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "endCursor" },
            new FieldMetadata { Name = "hasNextPage" },
            new FieldMetadata { Name = "hasPreviousPage" },
            new FieldMetadata { Name = "startCursor" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public PageInfoQueryBuilder WithEndCursor() => WithScalarField("endCursor");

        public PageInfoQueryBuilder WithHasNextPage() => WithScalarField("hasNextPage");

        public PageInfoQueryBuilder WithHasPreviousPage() => WithScalarField("hasPreviousPage");

        public PageInfoQueryBuilder WithStartCursor() => WithScalarField("startCursor");
    }

    public class SubscriptionPriceEdgeQueryBuilder : GraphQlQueryBuilder<SubscriptionPriceEdgeQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "cursor" },
            new FieldMetadata { Name = "node", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public SubscriptionPriceEdgeQueryBuilder WithCursor() => WithScalarField("cursor");

        public SubscriptionPriceEdgeQueryBuilder WithNode(PriceQueryBuilder priceQueryBuilder) => WithObjectField("node", priceQueryBuilder);
    }

    public class HomeConsumptionConnectionQueryBuilder : GraphQlQueryBuilder<HomeConsumptionConnectionQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "pageInfo", IsComplex = true, QueryBuilderType = typeof(HomeConsumptionPageInfoQueryBuilder) },
            new FieldMetadata { Name = "nodes", IsComplex = true, QueryBuilderType = typeof(ConsumptionQueryBuilder) },
            new FieldMetadata { Name = "edges", IsComplex = true, QueryBuilderType = typeof(HomeConsumptionEdgeQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeConsumptionConnectionQueryBuilder WithPageInfo(HomeConsumptionPageInfoQueryBuilder homeConsumptionPageInfoQueryBuilder) => WithObjectField("pageInfo", homeConsumptionPageInfoQueryBuilder);

        public HomeConsumptionConnectionQueryBuilder WithNodes(ConsumptionQueryBuilder consumptionQueryBuilder) => WithObjectField("nodes", consumptionQueryBuilder);

        public HomeConsumptionConnectionQueryBuilder WithEdges(HomeConsumptionEdgeQueryBuilder homeConsumptionEdgeQueryBuilder) => WithObjectField("edges", homeConsumptionEdgeQueryBuilder);
    }

    public class HomeConsumptionPageInfoQueryBuilder : GraphQlQueryBuilder<HomeConsumptionPageInfoQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "endCursor" },
            new FieldMetadata { Name = "hasNextPage" },
            new FieldMetadata { Name = "hasPreviousPage" },
            new FieldMetadata { Name = "startCursor" },
            new FieldMetadata { Name = "count" },
            new FieldMetadata { Name = "currency" },
            new FieldMetadata { Name = "totalCost" },
            new FieldMetadata { Name = "energyCost" },
            new FieldMetadata { Name = "totalConsumption" },
            new FieldMetadata { Name = "filtered" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeConsumptionPageInfoQueryBuilder WithEndCursor() => WithScalarField("endCursor");

        public HomeConsumptionPageInfoQueryBuilder WithHasNextPage() => WithScalarField("hasNextPage");

        public HomeConsumptionPageInfoQueryBuilder WithHasPreviousPage() => WithScalarField("hasPreviousPage");

        public HomeConsumptionPageInfoQueryBuilder WithStartCursor() => WithScalarField("startCursor");

        public HomeConsumptionPageInfoQueryBuilder WithCount() => WithScalarField("count");

        public HomeConsumptionPageInfoQueryBuilder WithCurrency() => WithScalarField("currency");

        public HomeConsumptionPageInfoQueryBuilder WithTotalCost() => WithScalarField("totalCost");

        public HomeConsumptionPageInfoQueryBuilder WithEnergyCost() => WithScalarField("energyCost");

        public HomeConsumptionPageInfoQueryBuilder WithTotalConsumption() => WithScalarField("totalConsumption");

        public HomeConsumptionPageInfoQueryBuilder WithFiltered() => WithScalarField("filtered");
    }

    public class ConsumptionQueryBuilder : GraphQlQueryBuilder<ConsumptionQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "from" },
            new FieldMetadata { Name = "to" },
            new FieldMetadata { Name = "unitPrice" },
            new FieldMetadata { Name = "unitPriceVAT" },
            new FieldMetadata { Name = "consumption" },
            new FieldMetadata { Name = "consumptionUnit" },
            new FieldMetadata { Name = "totalCost" },
            new FieldMetadata { Name = "unitCost" },
            new FieldMetadata { Name = "cost" },
            new FieldMetadata { Name = "currency" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public ConsumptionQueryBuilder WithFrom() => WithScalarField("from");

        public ConsumptionQueryBuilder WithTo() => WithScalarField("to");

        public ConsumptionQueryBuilder WithUnitPrice() => WithScalarField("unitPrice");

        public ConsumptionQueryBuilder WithUnitPriceVat() => WithScalarField("unitPriceVAT");

        public ConsumptionQueryBuilder WithConsumption() => WithScalarField("consumption");

        public ConsumptionQueryBuilder WithConsumptionUnit() => WithScalarField("consumptionUnit");

        public ConsumptionQueryBuilder WithTotalCost() => WithScalarField("totalCost");

        public ConsumptionQueryBuilder WithUnitCost() => WithScalarField("unitCost");

        public ConsumptionQueryBuilder WithCost() => WithScalarField("cost");

        public ConsumptionQueryBuilder WithCurrency() => WithScalarField("currency");
    }

    public class HomeConsumptionEdgeQueryBuilder : GraphQlQueryBuilder<HomeConsumptionEdgeQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "cursor" },
            new FieldMetadata { Name = "node", IsComplex = true, QueryBuilderType = typeof(ConsumptionQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeConsumptionEdgeQueryBuilder WithCursor() => WithScalarField("cursor");

        public HomeConsumptionEdgeQueryBuilder WithNode(ConsumptionQueryBuilder consumptionQueryBuilder) => WithObjectField("node", consumptionQueryBuilder);
    }

    public class HomeProductionConnectionQueryBuilder : GraphQlQueryBuilder<HomeProductionConnectionQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "pageInfo", IsComplex = true, QueryBuilderType = typeof(HomeProductionPageInfoQueryBuilder) },
            new FieldMetadata { Name = "nodes", IsComplex = true, QueryBuilderType = typeof(ProductionQueryBuilder) },
            new FieldMetadata { Name = "edges", IsComplex = true, QueryBuilderType = typeof(HomeProductionEdgeQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeProductionConnectionQueryBuilder WithPageInfo(HomeProductionPageInfoQueryBuilder homeProductionPageInfoQueryBuilder) => WithObjectField("pageInfo", homeProductionPageInfoQueryBuilder);

        public HomeProductionConnectionQueryBuilder WithNodes(ProductionQueryBuilder productionQueryBuilder) => WithObjectField("nodes", productionQueryBuilder);

        public HomeProductionConnectionQueryBuilder WithEdges(HomeProductionEdgeQueryBuilder homeProductionEdgeQueryBuilder) => WithObjectField("edges", homeProductionEdgeQueryBuilder);
    }

    public class HomeProductionPageInfoQueryBuilder : GraphQlQueryBuilder<HomeProductionPageInfoQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "endCursor" },
            new FieldMetadata { Name = "hasNextPage" },
            new FieldMetadata { Name = "hasPreviousPage" },
            new FieldMetadata { Name = "startCursor" },
            new FieldMetadata { Name = "count" },
            new FieldMetadata { Name = "currency" },
            new FieldMetadata { Name = "totalProfit" },
            new FieldMetadata { Name = "totalProduction" },
            new FieldMetadata { Name = "filtered" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeProductionPageInfoQueryBuilder WithEndCursor() => WithScalarField("endCursor");

        public HomeProductionPageInfoQueryBuilder WithHasNextPage() => WithScalarField("hasNextPage");

        public HomeProductionPageInfoQueryBuilder WithHasPreviousPage() => WithScalarField("hasPreviousPage");

        public HomeProductionPageInfoQueryBuilder WithStartCursor() => WithScalarField("startCursor");

        public HomeProductionPageInfoQueryBuilder WithCount() => WithScalarField("count");

        public HomeProductionPageInfoQueryBuilder WithCurrency() => WithScalarField("currency");

        public HomeProductionPageInfoQueryBuilder WithTotalProfit() => WithScalarField("totalProfit");

        public HomeProductionPageInfoQueryBuilder WithTotalProduction() => WithScalarField("totalProduction");

        public HomeProductionPageInfoQueryBuilder WithFiltered() => WithScalarField("filtered");
    }

    public class ProductionQueryBuilder : GraphQlQueryBuilder<ProductionQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "from" },
            new FieldMetadata { Name = "to" },
            new FieldMetadata { Name = "unitPrice" },
            new FieldMetadata { Name = "unitPriceVAT" },
            new FieldMetadata { Name = "production" },
            new FieldMetadata { Name = "productionUnit" },
            new FieldMetadata { Name = "profit" },
            new FieldMetadata { Name = "currency" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public ProductionQueryBuilder WithFrom() => WithScalarField("from");

        public ProductionQueryBuilder WithTo() => WithScalarField("to");

        public ProductionQueryBuilder WithUnitPrice() => WithScalarField("unitPrice");

        public ProductionQueryBuilder WithUnitPriceVat() => WithScalarField("unitPriceVAT");

        public ProductionQueryBuilder WithProduction() => WithScalarField("production");

        public ProductionQueryBuilder WithProductionUnit() => WithScalarField("productionUnit");

        public ProductionQueryBuilder WithProfit() => WithScalarField("profit");

        public ProductionQueryBuilder WithCurrency() => WithScalarField("currency");
    }

    public class HomeProductionEdgeQueryBuilder : GraphQlQueryBuilder<HomeProductionEdgeQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "cursor" },
            new FieldMetadata { Name = "node", IsComplex = true, QueryBuilderType = typeof(ProductionQueryBuilder) }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeProductionEdgeQueryBuilder WithCursor() => WithScalarField("cursor");

        public HomeProductionEdgeQueryBuilder WithNode(ProductionQueryBuilder productionQueryBuilder) => WithObjectField("node", productionQueryBuilder);
    }

    public class HomeFeaturesQueryBuilder : GraphQlQueryBuilder<HomeFeaturesQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "realTimeConsumptionEnabled" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeFeaturesQueryBuilder WithRealTimeConsumptionEnabled() => WithScalarField("realTimeConsumptionEnabled");
    }

    public class RootMutationQueryBuilder : GraphQlQueryBuilder<RootMutationQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "sendMeterReading", IsComplex = true, QueryBuilderType = typeof(MeterReadingResponseQueryBuilder) },
            new FieldMetadata { Name = "updateHome", IsComplex = true, QueryBuilderType = typeof(HomeQueryBuilder) },
            new FieldMetadata { Name = "sendPushNotification", IsComplex = true, QueryBuilderType = typeof(PushNotificationResponseQueryBuilder) }
        };

        protected override string Prefix { get; } = "mutation";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

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
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "homeId" },
            new FieldMetadata { Name = "time" },
            new FieldMetadata { Name = "reading" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public MeterReadingResponseQueryBuilder WithHomeId() => WithScalarField("homeId");

        public MeterReadingResponseQueryBuilder WithTime() => WithScalarField("time");

        public MeterReadingResponseQueryBuilder WithReading() => WithScalarField("reading");
    }

    public class PushNotificationResponseQueryBuilder : GraphQlQueryBuilder<PushNotificationResponseQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "successful" },
            new FieldMetadata { Name = "pushedToNumberOfDevices" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public PushNotificationResponseQueryBuilder WithSuccessful() => WithScalarField("successful");

        public PushNotificationResponseQueryBuilder WithPushedToNumberOfDevices() => WithScalarField("pushedToNumberOfDevices");
    }

    public class RootSubscriptionQueryBuilder : GraphQlQueryBuilder<RootSubscriptionQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "liveMeasurement", IsComplex = true, QueryBuilderType = typeof(LiveMeasurementQueryBuilder) }
        };

        protected override string Prefix { get; } = "subscription";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public RootSubscriptionQueryBuilder WithLiveMeasurement(LiveMeasurementQueryBuilder liveMeasurementQueryBuilder, Guid homeId)
        {
            var args = new Dictionary<string, object>();
            args.Add("homeId", homeId);
            return WithObjectField("liveMeasurement", liveMeasurementQueryBuilder, args);
        }
    }

    public class LiveMeasurementQueryBuilder : GraphQlQueryBuilder<LiveMeasurementQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "timestamp" },
            new FieldMetadata { Name = "power" },
            new FieldMetadata { Name = "lastMeterConsumption" },
            new FieldMetadata { Name = "accumulatedConsumption" },
            new FieldMetadata { Name = "accumulatedProduction" },
            new FieldMetadata { Name = "accumulatedCost" },
            new FieldMetadata { Name = "accumulatedReward" },
            new FieldMetadata { Name = "currency" },
            new FieldMetadata { Name = "minPower" },
            new FieldMetadata { Name = "averagePower" },
            new FieldMetadata { Name = "maxPower" },
            new FieldMetadata { Name = "powerProduction" },
            new FieldMetadata { Name = "minPowerProduction" },
            new FieldMetadata { Name = "maxPowerProduction" },
            new FieldMetadata { Name = "lastMeterProduction" },
            new FieldMetadata { Name = "powerFactor" },
            new FieldMetadata { Name = "voltagePhase1" },
            new FieldMetadata { Name = "voltagePhase2" },
            new FieldMetadata { Name = "voltagePhase3" },
            new FieldMetadata { Name = "currentPhase1" },
            new FieldMetadata { Name = "currentPhase2" },
            new FieldMetadata { Name = "currentPhase3" }
        };

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public LiveMeasurementQueryBuilder WithTimestamp() => WithScalarField("timestamp");

        public LiveMeasurementQueryBuilder WithPower() => WithScalarField("power");

        public LiveMeasurementQueryBuilder WithLastMeterConsumption() => WithScalarField("lastMeterConsumption");

        public LiveMeasurementQueryBuilder WithAccumulatedConsumption() => WithScalarField("accumulatedConsumption");

        public LiveMeasurementQueryBuilder WithAccumulatedProduction() => WithScalarField("accumulatedProduction");

        public LiveMeasurementQueryBuilder WithAccumulatedCost() => WithScalarField("accumulatedCost");

        public LiveMeasurementQueryBuilder WithAccumulatedReward() => WithScalarField("accumulatedReward");

        public LiveMeasurementQueryBuilder WithCurrency() => WithScalarField("currency");

        public LiveMeasurementQueryBuilder WithMinPower() => WithScalarField("minPower");

        public LiveMeasurementQueryBuilder WithAveragePower() => WithScalarField("averagePower");

        public LiveMeasurementQueryBuilder WithMaxPower() => WithScalarField("maxPower");

        public LiveMeasurementQueryBuilder WithPowerProduction() => WithScalarField("powerProduction");

        public LiveMeasurementQueryBuilder WithMinPowerProduction() => WithScalarField("minPowerProduction");

        public LiveMeasurementQueryBuilder WithMaxPowerProduction() => WithScalarField("maxPowerProduction");

        public LiveMeasurementQueryBuilder WithLastMeterProduction() => WithScalarField("lastMeterProduction");

        public LiveMeasurementQueryBuilder WithPowerFactor() => WithScalarField("powerFactor");

        public LiveMeasurementQueryBuilder WithVoltagePhase1() => WithScalarField("voltagePhase1");

        public LiveMeasurementQueryBuilder WithVoltagePhase2() => WithScalarField("voltagePhase2");

        public LiveMeasurementQueryBuilder WithVoltagePhase3() => WithScalarField("voltagePhase3");

        public LiveMeasurementQueryBuilder WithCurrentPhase1() => WithScalarField("currentPhase1");

        public LiveMeasurementQueryBuilder WithCurrentPhase2() => WithScalarField("currentPhase2");

        public LiveMeasurementQueryBuilder WithCurrentPhase3() => WithScalarField("currentPhase3");
    }

    #endregion

    #region input classes

    public class MeterReadingInput : IGraphQlInputObject
    {
        public Guid? HomeId { get; set; }
        public string Time { get; set; }
        public int? Reading { get; set; }

        IEnumerable<InputPropertyInfo> IGraphQlInputObject.GetPropertyValues()
        {
            yield return new InputPropertyInfo { Name = "homeId", Value = HomeId };
            yield return new InputPropertyInfo { Name = "time", Value = Time };
            yield return new InputPropertyInfo { Name = "reading", Value = Reading };
        }
    }

    public class UpdateHomeInput : IGraphQlInputObject
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

        /// <summary>
        /// The main fuse size
        /// </summary>
        public int? MainFuseSize { get; set; }

        IEnumerable<InputPropertyInfo> IGraphQlInputObject.GetPropertyValues()
        {
            yield return new InputPropertyInfo { Name = "homeId", Value = HomeId };
            yield return new InputPropertyInfo { Name = "appNickname", Value = AppNickname };
            yield return new InputPropertyInfo { Name = "appAvatar", Value = AppAvatar };
            yield return new InputPropertyInfo { Name = "size", Value = Size };
            yield return new InputPropertyInfo { Name = "type", Value = Type };
            yield return new InputPropertyInfo { Name = "numberOfResidents", Value = NumberOfResidents };
            yield return new InputPropertyInfo { Name = "primaryHeatingSource", Value = PrimaryHeatingSource };
            yield return new InputPropertyInfo { Name = "hasVentilationSystem", Value = HasVentilationSystem };
            yield return new InputPropertyInfo { Name = "mainFuseSize", Value = MainFuseSize };
        }
    }

    public class PushNotificationInput : IGraphQlInputObject
    {
        public string Title { get; set; }
        public string Message { get; set; }
        public AppScreen? ScreenToOpen { get; set; }

        IEnumerable<InputPropertyInfo> IGraphQlInputObject.GetPropertyValues()
        {
            yield return new InputPropertyInfo { Name = "title", Value = Title };
            yield return new InputPropertyInfo { Name = "message", Value = Message };
            yield return new InputPropertyInfo { Name = "screenToOpen", Value = ScreenToOpen };
        }
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
        /// Single home by its ID
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

        /// <summary>
        /// The main fuse size
        /// </summary>
        public int? MainFuseSize { get; set; }

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

        public HomeProductionConnection Production { get; set; }
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
        /// 'true' if the entity is a company
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
        /// The grid area the home/metering point belongs to
        /// </summary>
        public string GridAreaCode { get; set; }

        /// <summary>
        /// The price area the home/metering point belongs to
        /// </summary>
        public string PriceAreaCode { get; set; }

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
        /// The total price (energy + taxes)
        /// </summary>
        public decimal? Total { get; set; }

        /// <summary>
        /// The energy part of the price
        /// </summary>
        public decimal? Energy { get; set; }

        /// <summary>
        /// The tax part of the price (guarantee of origin certificate, energy tax (Sweden only) and VAT)
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

        /// <summary>
        /// The price level compared to recent price values
        /// </summary>
        public PriceLevel? Level { get; set; }
    }

    public class SubscriptionPriceConnection
    {
        public SubscriptionPriceConnectionPageInfo PageInfo { get; set; }
        public ICollection<SubscriptionPriceEdge> Edges { get; set; }
        public ICollection<Price> Nodes { get; set; }
    }

    public class SubscriptionPriceConnectionPageInfo : IPageInfo
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

    public interface IPageInfo
    {
        string EndCursor { get; set; }
        bool? HasNextPage { get; set; }
        bool? HasPreviousPage { get; set; }
        string StartCursor { get; set; }
    }

    public class PageInfo : IPageInfo
    {
        public string EndCursor { get; set; }
        public bool? HasNextPage { get; set; }
        public bool? HasPreviousPage { get; set; }
        public string StartCursor { get; set; }
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

    public class HomeConsumptionPageInfo : IPageInfo
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
        /// Total consumption for page
        /// </summary>
        public decimal? TotalConsumption { get; set; }

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
        public decimal? UnitPriceVat { get; set; }

        /// <summary>
        /// kWh consumed
        /// </summary>
        public decimal? Consumption { get; set; }

        public string ConsumptionUnit { get; set; }
        public decimal? Cost { get; set; }

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

    public class HomeProductionConnection
    {
        public HomeProductionPageInfo PageInfo { get; set; }
        public ICollection<ProductionEntry> Nodes { get; set; }
        public ICollection<HomeProductionEdge> Edges { get; set; }
    }

    public class HomeProductionPageInfo : IPageInfo
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
        /// Page total profit
        /// </summary>
        public decimal? TotalProfit { get; set; }

        /// <summary>
        /// Page total production
        /// </summary>
        public decimal? TotalProduction { get; set; }

        /// <summary>
        /// Number of entries that have been filtered from result set due to empty nodes
        /// </summary>
        public int? Filtered { get; set; }
    }

    public class ProductionEntry
    {
        public DateTimeOffset? From { get; set; }
        public DateTimeOffset? To { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? UnitPriceVat { get; set; }

        /// <summary>
        /// kWh consumed
        /// </summary>
        public decimal? Production { get; set; }

        public string ProductionUnit { get; set; }

        /// <summary>
        /// Total profit of the production
        /// </summary>
        public decimal? Profit { get; set; }

        /// <summary>
        /// The cost currency
        /// </summary>
        public string Currency { get; set; }
    }

    public class HomeProductionEdge
    {
        public string Cursor { get; set; }
        public ProductionEntry Node { get; set; }
    }

    public class HomeFeatures
    {
        /// <summary>
        /// 'true' if Tibber Pulse or Watty device is paired at home
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
}
