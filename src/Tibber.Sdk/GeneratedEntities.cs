using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
    public class QueryBuilderParameterConverter<T> : JsonConverter
    {
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) =>
            reader.TokenType switch
            {
                JsonToken.Null => null,
                _ => (QueryBuilderParameter<T>)(T)serializer.Deserialize(reader, typeof(T))
            };

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value == null)
                writer.WriteNull();
            else
                serializer.Serialize(writer, ((QueryBuilderParameter<T>)value).Value, typeof(T));
        }

        public override bool CanConvert(Type objectType) => objectType.IsSubclassOf(typeof(QueryBuilderParameter));
    }
#endif

    internal static class GraphQlQueryHelper
    {
        private static readonly Regex RegexWhiteSpace = new Regex(@"\s", RegexOptions.Compiled);
        private static readonly Regex RegexGraphQlIdentifier = new Regex(@"^[_A-Za-z][_0-9A-Za-z]*$", RegexOptions.Compiled);

        public static string GetIndentation(int level, byte indentationSize)
        {
            return new String(' ', level * indentationSize);
        }

        public static string BuildArgumentValue(object value, string formatMask, Formatting formatting, int level, byte indentationSize)
        {
            if (value is null)
                return "null";

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
            if (value is JValue jValue)
            {
                switch (jValue.Type)
                {
                    case JTokenType.Null: return "null";
                    case JTokenType.Integer:
                    case JTokenType.Float:
                    case JTokenType.Boolean:
                        return BuildArgumentValue(jValue.Value, null, formatting, level, indentationSize);
                    default:
                        return "\"" + jValue.Value + "\"";
                }
            }

            if (value is JProperty jProperty)
            {
                if (RegexWhiteSpace.IsMatch(jProperty.Name))
                    throw new ArgumentException($"JSON object keys used as GraphQL arguments must not contain whitespace; key: {jProperty.Name}");

                return $"{jProperty.Name}:{(formatting == Formatting.Indented ? " " : null)}{BuildArgumentValue(jProperty.Value, null, formatting, level, indentationSize)}";
            }

            if (value is JObject jObject)
                return BuildEnumerableArgument(jObject, null, formatting, level + 1, indentationSize, '{', '}');
#endif

            var enumerable = value as IEnumerable;
            if (!String.IsNullOrEmpty(formatMask) && enumerable == null)
                return
                    value is IFormattable formattable
                        ? "\"" + formattable.ToString(formatMask, CultureInfo.InvariantCulture) + "\""
                        : throw new ArgumentException($"Value must implement {nameof(IFormattable)} interface to use a format mask. ", nameof(value));

            if (value is Enum @enum)
                return ConvertEnumToString(@enum);

            if (value is bool @bool)
                return @bool ? "true" : "false";

            if (value is DateTime dateTime)
                return "\"" + dateTime.ToString("O") + "\"";

            if (value is DateTimeOffset dateTimeOffset)
                return "\"" + dateTimeOffset.ToString("O") + "\"";

            if (value is IGraphQlInputObject inputObject)
                return BuildInputObject(inputObject, formatting, level + 2, indentationSize);

            if (value is String || value is Guid)
                return "\"" + value + "\"";

            if (enumerable != null)
                return BuildEnumerableArgument(enumerable, formatMask, formatting, level, indentationSize, '[', ']');

            if (value is short || value is ushort || value is byte || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal)
                return Convert.ToString(value, CultureInfo.InvariantCulture);

            var argumentValue = Convert.ToString(value, CultureInfo.InvariantCulture);
            return "\"" + argumentValue + "\"";
        }

        private static string BuildEnumerableArgument(IEnumerable enumerable, string formatMask, Formatting formatting, int level, byte indentationSize, char openingSymbol, char closingSymbol)
        {
            var builder = new StringBuilder();
            builder.Append(openingSymbol);
            var delimiter = String.Empty;
            foreach (var item in enumerable)
            {
                builder.Append(delimiter);

                if (formatting == Formatting.Indented)
                {
                    builder.AppendLine();
                    builder.Append(GetIndentation(level + 1, indentationSize));
                }

                builder.Append(BuildArgumentValue(item, formatMask, formatting, level, indentationSize));
                delimiter = ",";
            }

            builder.Append(closingSymbol);
            return builder.ToString();
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
            foreach (var propertyValue in inputObject.GetPropertyValues())
            {
                var queryBuilderParameter = propertyValue.Value as QueryBuilderParameter;
                var value =
                    queryBuilderParameter?.Name != null
                        ? "$" + queryBuilderParameter.Name
                        : BuildArgumentValue(queryBuilderParameter?.Value ?? propertyValue.Value, propertyValue.FormatMask, formatting, level, indentationSize);

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

        public static string BuildDirective(GraphQlDirective directive, Formatting formatting, int level, byte indentationSize)
        {
            if (directive == null)
                return String.Empty;

            var isIndentedFormatting = formatting == Formatting.Indented;
            var indentationSpace = isIndentedFormatting ? " " : String.Empty;
            var builder = new StringBuilder();
            builder.Append(indentationSpace);
            builder.Append("@");
            builder.Append(directive.Name);
            builder.Append("(");

            string separator = null;
            foreach (var kvp in directive.Arguments)
            {
                var argumentName = kvp.Key;
                var argument = kvp.Value;

                builder.Append(separator);
                builder.Append(argumentName);
                builder.Append(":");
                builder.Append(indentationSpace);

                if (argument.Name == null)
                    builder.Append(BuildArgumentValue(argument.Value, null, formatting, level, indentationSize));
                else
                {
                    builder.Append("$");
                    builder.Append(argument.Name);
                }

                separator = isIndentedFormatting ? ", " : ",";
            }

            builder.Append(")");
            return builder.ToString();
        }

        public static void ValidateGraphQlIdentifier(string name, string identifier)
        {
            if (identifier != null && !RegexGraphQlIdentifier.IsMatch(identifier))
                throw new ArgumentException("Value must match [_A-Za-z][_0-9A-Za-z]*. ", name);
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

    internal struct InputPropertyInfo
    {
        public string Name { get; set; }
        public object Value { get; set; }
        public string FormatMask { get; set; }
    }

    internal interface IGraphQlInputObject
    {
        IEnumerable<InputPropertyInfo> GetPropertyValues();
    }

    public interface IGraphQlQueryBuilder
    {
        void Clear();
        void IncludeAllFields();
        string Build(Formatting formatting = Formatting.None, byte indentationSize = 2);
    }

    public struct QueryBuilderArgumentInfo
    {
        public string ArgumentName { get; set; }
        public QueryBuilderParameter ArgumentValue { get; set; }
        public string FormatMask { get; set; }
    }

    public abstract class QueryBuilderParameter
    {
        private string _name;

        internal string GraphQlTypeName { get; }
        internal object Value { get; set; }

        public string Name
        {
            get => _name;
            set
            {
                GraphQlQueryHelper.ValidateGraphQlIdentifier(nameof(Name), value);
                _name = value;
            }
        }

        protected QueryBuilderParameter(string name, string graphQlTypeName, object value)
        {
            Name = name?.Trim();
            GraphQlTypeName = graphQlTypeName?.Replace(" ", null).Replace("\t", null).Replace("\n", null).Replace("\r", null);
            Value = value;
        }

        protected QueryBuilderParameter(object value) => Value = value;
    }

    public class QueryBuilderParameter<T> : QueryBuilderParameter
    {
        public new T Value
        {
            get => (T)base.Value;
            set => base.Value = value;
        }

        protected QueryBuilderParameter(string name, string graphQlTypeName, T value) : base(name, graphQlTypeName, value)
        {
        }

        private QueryBuilderParameter(T value) : base(value)
        {
        }

        public static implicit operator QueryBuilderParameter<T>(T value) => new QueryBuilderParameter<T>(value);

        public static implicit operator T(QueryBuilderParameter<T> parameter) => parameter.Value;
    }

    public class GraphQlQueryParameter<T> : QueryBuilderParameter<T>
    {
        private string _formatMask;

        public string FormatMask
        {
            get => _formatMask;
            set => _formatMask =
                typeof(IFormattable).GetTypeInfo().IsAssignableFrom(typeof(T))
                    ? value
                    : throw new InvalidOperationException($"Value must be of {nameof(IFormattable)} type. ");
        }

        public GraphQlQueryParameter(string name, string graphQlTypeName, T value) : base(name, graphQlTypeName, value)
        {
        }
    }

    public abstract class GraphQlDirective
    {
        private readonly Dictionary<string, QueryBuilderParameter> _arguments = new Dictionary<string, QueryBuilderParameter>();

        internal IEnumerable<KeyValuePair<string, QueryBuilderParameter>> Arguments => _arguments;

        public string Name { get; }

        protected GraphQlDirective(string name)
        {
            GraphQlQueryHelper.ValidateGraphQlIdentifier(nameof(name), name);
            Name = name;
        }

        protected void AddArgument(string name, QueryBuilderParameter value)
        {
            if (value != null)
                _arguments[name] = value;
        }
    }

    public abstract class GraphQlQueryBuilder : IGraphQlQueryBuilder
    {
        private readonly Dictionary<string, GraphQlFieldCriteria> _fieldCriteria = new Dictionary<string, GraphQlFieldCriteria>();

        private readonly GraphQlDirective[] _directives;

        private Dictionary<string, GraphQlFragmentCriteria> _fragments;
        private List<QueryBuilderArgumentInfo> _queryParameters;

        protected virtual string Prefix => null;

        protected abstract string TypeName { get; }

        protected abstract IList<FieldMetadata> AllFields { get; }

        public string Alias { get; }

        protected GraphQlQueryBuilder(string alias, params GraphQlDirective[] directives)
        {
            GraphQlQueryHelper.ValidateGraphQlIdentifier(nameof(alias), alias);
            Alias = alias;
            _directives = directives;
        }

        public virtual void Clear()
        {
            _fieldCriteria.Clear();
            _fragments?.Clear();
            _queryParameters?.Clear();
        }

        void IGraphQlQueryBuilder.IncludeAllFields()
        {
            IncludeAllFields();
        }

        public string Build(Formatting formatting = Formatting.None, byte indentationSize = 2)
        {
            return Build(formatting, 1, indentationSize);
        }

        protected void IncludeAllFields()
        {
            IncludeFields(AllFields);
        }

        protected virtual string Build(Formatting formatting, int level, byte indentationSize)
        {
            var isIndentedFormatting = formatting == Formatting.Indented;
            var separator = String.Empty;
            var indentationSpace = isIndentedFormatting ? " " : String.Empty;
            var builder = new StringBuilder();

            if (!String.IsNullOrEmpty(Prefix))
            {
                builder.Append(Prefix);

                if (!String.IsNullOrEmpty(Alias))
                {
                    builder.Append(" ");
                    builder.Append(Alias);
                }

                if (_queryParameters?.Count > 0)
                {
                    builder.Append(indentationSpace);
                    builder.Append("(");

                    foreach (var queryParameterInfo in _queryParameters)
                    {
                        if (isIndentedFormatting)
                        {
                            builder.AppendLine(separator);
                            builder.Append(GraphQlQueryHelper.GetIndentation(level, indentationSize));
                        }
                        else
                            builder.Append(separator);

                        builder.Append("$");
                        builder.Append(queryParameterInfo.ArgumentValue.Name);
                        builder.Append(":");
                        builder.Append(indentationSpace);

                        builder.Append(queryParameterInfo.ArgumentValue.GraphQlTypeName);

                        if (!queryParameterInfo.ArgumentValue.GraphQlTypeName.EndsWith("!"))
                        {
                            builder.Append(indentationSpace);
                            builder.Append("=");
                            builder.Append(indentationSpace);
                            builder.Append(GraphQlQueryHelper.BuildArgumentValue(queryParameterInfo.ArgumentValue.Value, queryParameterInfo.FormatMask, formatting, 0, indentationSize));
                        }

                        separator = ",";
                    }

                    builder.Append(")");
                }
            }

            if (_directives != null)
                foreach (var directive in _directives.Where(d => d != null))
                    builder.Append(GraphQlQueryHelper.BuildDirective(directive, formatting, level, indentationSize));

            builder.Append(indentationSpace);
            builder.Append("{");

            if (isIndentedFormatting)
                builder.AppendLine();

            separator = String.Empty;

            foreach (var criteria in _fieldCriteria.Values.Concat(_fragments?.Values ?? Enumerable.Empty<GraphQlFragmentCriteria>()))
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

        protected void IncludeScalarField(string fieldName, string alias, IList<QueryBuilderArgumentInfo> args, GraphQlDirective[] directives)
        {
            GraphQlQueryHelper.ValidateGraphQlIdentifier(nameof(alias), alias);
            _fieldCriteria[alias ?? fieldName] = new GraphQlScalarFieldCriteria(fieldName, alias, args, directives);
        }

        protected void IncludeObjectField(string fieldName, GraphQlQueryBuilder objectFieldQueryBuilder, IList<QueryBuilderArgumentInfo> args)
        {
            _fieldCriteria[objectFieldQueryBuilder.Alias ?? fieldName] = new GraphQlObjectFieldCriteria(fieldName, objectFieldQueryBuilder, args);
        }

        protected void IncludeFragment(GraphQlQueryBuilder objectFieldQueryBuilder)
        {
            _fragments ??= new Dictionary<string, GraphQlFragmentCriteria>();
            _fragments[objectFieldQueryBuilder.TypeName] = new GraphQlFragmentCriteria(objectFieldQueryBuilder);
        }

        protected void ExcludeField(string fieldName)
        {
            if (fieldName == null)
                throw new ArgumentNullException(nameof(fieldName));

            _fieldCriteria.Remove(fieldName);
        }

        protected void IncludeFields(IEnumerable<FieldMetadata> fields)
        {
            IncludeFields(fields, null);
        }

        private void IncludeFields(IEnumerable<FieldMetadata> fields, List<Type> parentTypes)
        {
            foreach (var field in fields)
            {
                if (field.QueryBuilderType == null)
                    IncludeScalarField(field.Name, null, null, null);
                else
                {
                    var builderType = GetType();

                    if (parentTypes != null && parentTypes.Any(t => t.IsAssignableFrom(field.QueryBuilderType)))
                        continue;

                    parentTypes?.Add(builderType);

                    var queryBuilder = InitializeChildBuilder(builderType, field.QueryBuilderType, parentTypes);

                    var includeFragmentMethods = field.QueryBuilderType.GetMethods().Where(IsIncludeFragmentMethod);

                    foreach (var includeFragmentMethod in includeFragmentMethods)
                        includeFragmentMethod.Invoke(queryBuilder, new object[] { InitializeChildBuilder(builderType, includeFragmentMethod.GetParameters()[0].ParameterType, parentTypes) });

                    IncludeObjectField(field.Name, queryBuilder, null);
                }
            }
        }

        private static GraphQlQueryBuilder InitializeChildBuilder(Type parentQueryBuilderType, Type queryBuilderType, List<Type> parentTypes)
        {
            var constructorInfo = queryBuilderType.GetConstructors().SingleOrDefault(IsCompatibleConstructor);
            if (constructorInfo == null)
                throw new InvalidOperationException($"{queryBuilderType.FullName} constructor not found");

            var queryBuilder = (GraphQlQueryBuilder)constructorInfo.Invoke(new object[constructorInfo.GetParameters().Length]);
            queryBuilder.IncludeFields(queryBuilder.AllFields, parentTypes ?? new List<Type> { parentQueryBuilderType });
            return queryBuilder;
        }

        private static bool IsIncludeFragmentMethod(MethodInfo methodInfo)
        {
            if (!methodInfo.Name.StartsWith("With") || !methodInfo.Name.EndsWith("Fragment"))
                return false;

            var parameters = methodInfo.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType.IsSubclassOf(typeof(GraphQlQueryBuilder));
        }

        private static bool IsCompatibleConstructor(ConstructorInfo constructorInfo)
        {
            var parameters = constructorInfo.GetParameters();
            if (parameters.Length == 0 || parameters[0].ParameterType != typeof(String))
                return false;

            return parameters.Skip(1).All(p => p.ParameterType.IsSubclassOf(typeof(GraphQlDirective)));
        }

        protected void AddParameter<T>(GraphQlQueryParameter<T> parameter)
        {
            _queryParameters ??= new List<QueryBuilderArgumentInfo>();
            _queryParameters.Add(new QueryBuilderArgumentInfo { ArgumentValue = parameter, FormatMask = parameter.FormatMask });
        }

        private abstract class GraphQlFieldCriteria
        {
            private readonly IList<QueryBuilderArgumentInfo> _args;

            protected readonly string FieldName;

            protected static string GetIndentation(Formatting formatting, int level, byte indentationSize) =>
                formatting == Formatting.Indented ? GraphQlQueryHelper.GetIndentation(level, indentationSize) : null;

            protected GraphQlFieldCriteria(string fieldName, IList<QueryBuilderArgumentInfo> args)
            {
                FieldName = fieldName;
                _args = args;
            }

            public abstract string Build(Formatting formatting, int level, byte indentationSize);

            protected string BuildArgumentClause(Formatting formatting, int level, byte indentationSize)
            {
                var separator = formatting == Formatting.Indented ? " " : null;
                var argumentCount = _args?.Count ?? 0;
                if (argumentCount == 0)
                    return String.Empty;

                var arguments =
                    _args.Select(
                        a => $"{a.ArgumentName}:{separator}{(a.ArgumentValue.Name == null ? GraphQlQueryHelper.BuildArgumentValue(a.ArgumentValue.Value, a.FormatMask, formatting, level, indentationSize) : "$" + a.ArgumentValue.Name)}");

                return $"({String.Join($",{separator}", arguments)})";
            }

            protected static string BuildAliasPrefix(string alias, Formatting formatting)
            {
                var separator = formatting == Formatting.Indented ? " " : String.Empty;
                return String.IsNullOrWhiteSpace(alias) ? null : alias + ':' + separator;
            }
        }

        private class GraphQlScalarFieldCriteria : GraphQlFieldCriteria
        {
            private readonly string _alias;
            private readonly GraphQlDirective[] _directives;

            public GraphQlScalarFieldCriteria(string fieldName, string alias, IList<QueryBuilderArgumentInfo> args, GraphQlDirective[] directives) : base(fieldName, args)
            {
                _alias = alias;
                _directives = directives;
            }

            public override string Build(Formatting formatting, int level, byte indentationSize) =>
                GetIndentation(formatting, level, indentationSize) + BuildAliasPrefix(_alias, formatting) + FieldName + BuildArgumentClause(formatting, level, indentationSize) +
                (_directives == null ? null : String.Concat(_directives.Select(d => d == null ? null : GraphQlQueryHelper.BuildDirective(d, formatting, level, indentationSize))));
        }

        private class GraphQlObjectFieldCriteria : GraphQlFieldCriteria
        {
            private readonly GraphQlQueryBuilder _objectQueryBuilder;

            public GraphQlObjectFieldCriteria(string fieldName, GraphQlQueryBuilder objectQueryBuilder, IList<QueryBuilderArgumentInfo> args) : base(fieldName, args)
            {
                _objectQueryBuilder = objectQueryBuilder;
            }

            public override string Build(Formatting formatting, int level, byte indentationSize) =>
                _objectQueryBuilder._fieldCriteria.Count > 0 || _objectQueryBuilder._fragments?.Count > 0
                    ? GetIndentation(formatting, level, indentationSize) + BuildAliasPrefix(_objectQueryBuilder.Alias, formatting) + FieldName +
                      BuildArgumentClause(formatting, level, indentationSize) + _objectQueryBuilder.Build(formatting, level + 1, indentationSize)
                    : null;
        }

        private class GraphQlFragmentCriteria : GraphQlFieldCriteria
        {
            private readonly GraphQlQueryBuilder _objectQueryBuilder;

            public GraphQlFragmentCriteria(GraphQlQueryBuilder objectQueryBuilder) : base(objectQueryBuilder.TypeName, null)
            {
                _objectQueryBuilder = objectQueryBuilder;
            }

            public override string Build(Formatting formatting, int level, byte indentationSize) =>
                _objectQueryBuilder._fieldCriteria.Count == 0
                    ? null
                    : GetIndentation(formatting, level, indentationSize) + "..." + (formatting == Formatting.Indented ? " " : null) + "on " +
                      FieldName + BuildArgumentClause(formatting, level, indentationSize) + _objectQueryBuilder.Build(formatting, level + 1, indentationSize);
        }
    }

    public abstract class GraphQlQueryBuilder<TQueryBuilder> : GraphQlQueryBuilder where TQueryBuilder : GraphQlQueryBuilder<TQueryBuilder>
    {
        protected GraphQlQueryBuilder(string alias, GraphQlDirective[] directives)
            : base(alias, directives)
        {
        }

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

        public TQueryBuilder ExceptField(string fieldName)
        {
            ExcludeField(fieldName);
            return (TQueryBuilder)this;
        }

        public TQueryBuilder WithTypeName(string alias = null, params GraphQlDirective[] directives)
        {
            IncludeScalarField("__typename", alias, null, directives);
            return (TQueryBuilder)this;
        }

        protected TQueryBuilder WithScalarField(string fieldName, string alias, GraphQlDirective[] directives, IList<QueryBuilderArgumentInfo> args = null)
        {
            IncludeScalarField(fieldName, alias, args, directives);
            return (TQueryBuilder)this;
        }

        protected TQueryBuilder WithObjectField(string fieldName, GraphQlQueryBuilder queryBuilder, IList<QueryBuilderArgumentInfo> args = null)
        {
            IncludeObjectField(fieldName, queryBuilder, args);
            return (TQueryBuilder)this;
        }

        protected TQueryBuilder WithFragment(GraphQlQueryBuilder queryBuilder)
        {
            IncludeFragment(queryBuilder);
            return (TQueryBuilder)this;
        }

        protected TQueryBuilder WithParameterInternal<T>(GraphQlQueryParameter<T> parameter)
        {
            AddParameter(parameter);
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

    #region directives
    /// <summary>
    /// Directs the executor to skip this field or fragment when the `if` argument is true.
    /// </summary>
    public class SkipDirective : GraphQlDirective
    {
        public SkipDirective(QueryBuilderParameter<bool> @if) : base("skip")
        {
            AddArgument("if", @if);
        }
    }

    /// <summary>
    /// Directs the executor to include this field or fragment only when the `if` argument is true.
    /// </summary>
    public class IncludeDirective : GraphQlDirective
    {
        public IncludeDirective(QueryBuilderParameter<bool> @if) : base("include")
        {
            AddArgument("if", @if);
        }
    }
    #endregion

    #region builder classes
    public class TibberQueryBuilder : GraphQlQueryBuilder<TibberQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "viewer", IsComplex = true, QueryBuilderType = typeof(ViewerQueryBuilder) }
        };

        protected override string Prefix { get; } = "query";

        protected override string TypeName { get; } = "Query";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public TibberQueryBuilder(string alias = null) : base(alias, null)
        {
        }

        public TibberQueryBuilder WithParameter<T>(GraphQlQueryParameter<T> parameter) => WithParameterInternal(parameter);

        public TibberQueryBuilder WithViewer(ViewerQueryBuilder viewerQueryBuilder) => WithObjectField("viewer", viewerQueryBuilder);

        public TibberQueryBuilder ExceptViewer() => ExceptField("viewer");
    }

    public class ViewerQueryBuilder : GraphQlQueryBuilder<ViewerQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "login" },
            new FieldMetadata { Name = "userId" },
            new FieldMetadata { Name = "name" },
            new FieldMetadata { Name = "accountType", IsComplex = true },
            new FieldMetadata { Name = "homes", IsComplex = true, QueryBuilderType = typeof(HomeQueryBuilder) },
            new FieldMetadata { Name = "home", IsComplex = true, QueryBuilderType = typeof(HomeQueryBuilder) }
        };

        protected override string TypeName { get; } = "Viewer";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public ViewerQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public ViewerQueryBuilder WithLogin(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("login", alias, new GraphQlDirective[] { skip, include });

        public ViewerQueryBuilder ExceptLogin() => ExceptField("login");

        public ViewerQueryBuilder WithUserId(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("userId", alias, new GraphQlDirective[] { skip, include });

        public ViewerQueryBuilder ExceptUserId() => ExceptField("userId");

        public ViewerQueryBuilder WithName(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("name", alias, new GraphQlDirective[] { skip, include });

        public ViewerQueryBuilder ExceptName() => ExceptField("name");

        public ViewerQueryBuilder WithAccountType(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("accountType", alias, new GraphQlDirective[] { skip, include });

        public ViewerQueryBuilder ExceptAccountType() => ExceptField("accountType");

        public ViewerQueryBuilder WithHomes(HomeQueryBuilder homeQueryBuilder) => WithObjectField("homes", homeQueryBuilder);

        public ViewerQueryBuilder ExceptHomes() => ExceptField("homes");

        public ViewerQueryBuilder WithHome(HomeQueryBuilder homeQueryBuilder, QueryBuilderParameter<Guid> id)
        {
            var args = new List<QueryBuilderArgumentInfo>();
            args.Add(new QueryBuilderArgumentInfo { ArgumentName = "id", ArgumentValue = id });
            return WithObjectField("home", homeQueryBuilder, args);
        }

        public ViewerQueryBuilder ExceptHome()
        {
            return ExceptField("home");
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

        protected override string TypeName { get; } = "Home";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public HomeQueryBuilder WithId(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("id", alias, new GraphQlDirective[] { skip, include });

        public HomeQueryBuilder ExceptId() => ExceptField("id");

        public HomeQueryBuilder WithTimeZone(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("timeZone", alias, new GraphQlDirective[] { skip, include });

        public HomeQueryBuilder ExceptTimeZone() => ExceptField("timeZone");

        public HomeQueryBuilder WithAppNickname(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("appNickname", alias, new GraphQlDirective[] { skip, include });

        public HomeQueryBuilder ExceptAppNickname() => ExceptField("appNickname");

        public HomeQueryBuilder WithAppAvatar(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("appAvatar", alias, new GraphQlDirective[] { skip, include });

        public HomeQueryBuilder ExceptAppAvatar() => ExceptField("appAvatar");

        public HomeQueryBuilder WithSize(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("size", alias, new GraphQlDirective[] { skip, include });

        public HomeQueryBuilder ExceptSize() => ExceptField("size");

        public HomeQueryBuilder WithType(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("type", alias, new GraphQlDirective[] { skip, include });

        public HomeQueryBuilder ExceptType() => ExceptField("type");

        public HomeQueryBuilder WithNumberOfResidents(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("numberOfResidents", alias, new GraphQlDirective[] { skip, include });

        public HomeQueryBuilder ExceptNumberOfResidents() => ExceptField("numberOfResidents");

        public HomeQueryBuilder WithPrimaryHeatingSource(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("primaryHeatingSource", alias, new GraphQlDirective[] { skip, include });

        public HomeQueryBuilder ExceptPrimaryHeatingSource() => ExceptField("primaryHeatingSource");

        public HomeQueryBuilder WithHasVentilationSystem(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("hasVentilationSystem", alias, new GraphQlDirective[] { skip, include });

        public HomeQueryBuilder ExceptHasVentilationSystem() => ExceptField("hasVentilationSystem");

        public HomeQueryBuilder WithMainFuseSize(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("mainFuseSize", alias, new GraphQlDirective[] { skip, include });

        public HomeQueryBuilder ExceptMainFuseSize() => ExceptField("mainFuseSize");

        public HomeQueryBuilder WithAddress(AddressQueryBuilder addressQueryBuilder) => WithObjectField("address", addressQueryBuilder);

        public HomeQueryBuilder ExceptAddress() => ExceptField("address");

        public HomeQueryBuilder WithOwner(LegalEntityQueryBuilder legalEntityQueryBuilder) => WithObjectField("owner", legalEntityQueryBuilder);

        public HomeQueryBuilder ExceptOwner() => ExceptField("owner");

        public HomeQueryBuilder WithMeteringPointData(MeteringPointDataQueryBuilder meteringPointDataQueryBuilder) => WithObjectField("meteringPointData", meteringPointDataQueryBuilder);

        public HomeQueryBuilder ExceptMeteringPointData() => ExceptField("meteringPointData");

        public HomeQueryBuilder WithCurrentSubscription(SubscriptionQueryBuilder subscriptionQueryBuilder) => WithObjectField("currentSubscription", subscriptionQueryBuilder);

        public HomeQueryBuilder ExceptCurrentSubscription() => ExceptField("currentSubscription");

        public HomeQueryBuilder WithSubscriptions(SubscriptionQueryBuilder subscriptionQueryBuilder) => WithObjectField("subscriptions", subscriptionQueryBuilder);

        public HomeQueryBuilder ExceptSubscriptions() => ExceptField("subscriptions");

        public HomeQueryBuilder WithConsumption(HomeConsumptionConnectionQueryBuilder homeConsumptionConnectionQueryBuilder, QueryBuilderParameter<EnergyResolution> resolution, QueryBuilderParameter<int?> first = null, QueryBuilderParameter<int?> last = null, QueryBuilderParameter<DateTimeOffset?> before = null, QueryBuilderParameter<DateTimeOffset?> after = null, QueryBuilderParameter<bool?> filterEmptyNodes = null)
        {
            var args = new List<QueryBuilderArgumentInfo>();
            args.Add(new QueryBuilderArgumentInfo { ArgumentName = "resolution", ArgumentValue = resolution });
            if (first != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "first", ArgumentValue = first });

            if (last != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "last", ArgumentValue = last });

            if (before != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "before", ArgumentValue = before });

            if (after != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "after", ArgumentValue = after });

            if (filterEmptyNodes != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "filterEmptyNodes", ArgumentValue = filterEmptyNodes });

            return WithObjectField("consumption", homeConsumptionConnectionQueryBuilder, args);
        }

        public HomeQueryBuilder ExceptConsumption()
        {
            return ExceptField("consumption");
        }

        public HomeQueryBuilder WithProduction(HomeProductionConnectionQueryBuilder homeProductionConnectionQueryBuilder, QueryBuilderParameter<EnergyResolution> resolution, QueryBuilderParameter<int?> first = null, QueryBuilderParameter<int?> last = null, QueryBuilderParameter<DateTimeOffset?> before = null, QueryBuilderParameter<DateTimeOffset?> after = null, QueryBuilderParameter<bool?> filterEmptyNodes = null)
        {
            var args = new List<QueryBuilderArgumentInfo>();
            args.Add(new QueryBuilderArgumentInfo { ArgumentName = "resolution", ArgumentValue = resolution });
            if (first != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "first", ArgumentValue = first });

            if (last != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "last", ArgumentValue = last });

            if (before != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "before", ArgumentValue = before });

            if (after != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "after", ArgumentValue = after });

            if (filterEmptyNodes != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "filterEmptyNodes", ArgumentValue = filterEmptyNodes });

            return WithObjectField("production", homeProductionConnectionQueryBuilder, args);
        }

        public HomeQueryBuilder ExceptProduction()
        {
            return ExceptField("production");
        }

        public HomeQueryBuilder WithFeatures(HomeFeaturesQueryBuilder homeFeaturesQueryBuilder) => WithObjectField("features", homeFeaturesQueryBuilder);

        public HomeQueryBuilder ExceptFeatures() => ExceptField("features");
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

        protected override string TypeName { get; } = "Address";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public AddressQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public AddressQueryBuilder WithAddress1(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("address1", alias, new GraphQlDirective[] { skip, include });

        public AddressQueryBuilder ExceptAddress1() => ExceptField("address1");

        public AddressQueryBuilder WithAddress2(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("address2", alias, new GraphQlDirective[] { skip, include });

        public AddressQueryBuilder ExceptAddress2() => ExceptField("address2");

        public AddressQueryBuilder WithAddress3(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("address3", alias, new GraphQlDirective[] { skip, include });

        public AddressQueryBuilder ExceptAddress3() => ExceptField("address3");

        public AddressQueryBuilder WithCity(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("city", alias, new GraphQlDirective[] { skip, include });

        public AddressQueryBuilder ExceptCity() => ExceptField("city");

        public AddressQueryBuilder WithPostalCode(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("postalCode", alias, new GraphQlDirective[] { skip, include });

        public AddressQueryBuilder ExceptPostalCode() => ExceptField("postalCode");

        public AddressQueryBuilder WithCountry(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("country", alias, new GraphQlDirective[] { skip, include });

        public AddressQueryBuilder ExceptCountry() => ExceptField("country");

        public AddressQueryBuilder WithLatitude(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("latitude", alias, new GraphQlDirective[] { skip, include });

        public AddressQueryBuilder ExceptLatitude() => ExceptField("latitude");

        public AddressQueryBuilder WithLongitude(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("longitude", alias, new GraphQlDirective[] { skip, include });

        public AddressQueryBuilder ExceptLongitude() => ExceptField("longitude");
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

        protected override string TypeName { get; } = "LegalEntity";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public LegalEntityQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public LegalEntityQueryBuilder WithId(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("id", alias, new GraphQlDirective[] { skip, include });

        public LegalEntityQueryBuilder ExceptId() => ExceptField("id");

        public LegalEntityQueryBuilder WithFirstName(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("firstName", alias, new GraphQlDirective[] { skip, include });

        public LegalEntityQueryBuilder ExceptFirstName() => ExceptField("firstName");

        public LegalEntityQueryBuilder WithIsCompany(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("isCompany", alias, new GraphQlDirective[] { skip, include });

        public LegalEntityQueryBuilder ExceptIsCompany() => ExceptField("isCompany");

        public LegalEntityQueryBuilder WithName(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("name", alias, new GraphQlDirective[] { skip, include });

        public LegalEntityQueryBuilder ExceptName() => ExceptField("name");

        public LegalEntityQueryBuilder WithMiddleName(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("middleName", alias, new GraphQlDirective[] { skip, include });

        public LegalEntityQueryBuilder ExceptMiddleName() => ExceptField("middleName");

        public LegalEntityQueryBuilder WithLastName(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("lastName", alias, new GraphQlDirective[] { skip, include });

        public LegalEntityQueryBuilder ExceptLastName() => ExceptField("lastName");

        public LegalEntityQueryBuilder WithOrganizationNo(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("organizationNo", alias, new GraphQlDirective[] { skip, include });

        public LegalEntityQueryBuilder ExceptOrganizationNo() => ExceptField("organizationNo");

        public LegalEntityQueryBuilder WithLanguage(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("language", alias, new GraphQlDirective[] { skip, include });

        public LegalEntityQueryBuilder ExceptLanguage() => ExceptField("language");

        public LegalEntityQueryBuilder WithContactInfo(ContactInfoQueryBuilder contactInfoQueryBuilder) => WithObjectField("contactInfo", contactInfoQueryBuilder);

        public LegalEntityQueryBuilder ExceptContactInfo() => ExceptField("contactInfo");

        public LegalEntityQueryBuilder WithAddress(AddressQueryBuilder addressQueryBuilder) => WithObjectField("address", addressQueryBuilder);

        public LegalEntityQueryBuilder ExceptAddress() => ExceptField("address");
    }

    public class ContactInfoQueryBuilder : GraphQlQueryBuilder<ContactInfoQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "email" },
            new FieldMetadata { Name = "mobile" }
        };

        protected override string TypeName { get; } = "ContactInfo";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public ContactInfoQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public ContactInfoQueryBuilder WithEmail(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("email", alias, new GraphQlDirective[] { skip, include });

        public ContactInfoQueryBuilder ExceptEmail() => ExceptField("email");

        public ContactInfoQueryBuilder WithMobile(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("mobile", alias, new GraphQlDirective[] { skip, include });

        public ContactInfoQueryBuilder ExceptMobile() => ExceptField("mobile");
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

        protected override string TypeName { get; } = "MeteringPointData";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public MeteringPointDataQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public MeteringPointDataQueryBuilder WithConsumptionEan(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("consumptionEan", alias, new GraphQlDirective[] { skip, include });

        public MeteringPointDataQueryBuilder ExceptConsumptionEan() => ExceptField("consumptionEan");

        public MeteringPointDataQueryBuilder WithGridCompany(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("gridCompany", alias, new GraphQlDirective[] { skip, include });

        public MeteringPointDataQueryBuilder ExceptGridCompany() => ExceptField("gridCompany");

        public MeteringPointDataQueryBuilder WithGridAreaCode(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("gridAreaCode", alias, new GraphQlDirective[] { skip, include });

        public MeteringPointDataQueryBuilder ExceptGridAreaCode() => ExceptField("gridAreaCode");

        public MeteringPointDataQueryBuilder WithPriceAreaCode(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("priceAreaCode", alias, new GraphQlDirective[] { skip, include });

        public MeteringPointDataQueryBuilder ExceptPriceAreaCode() => ExceptField("priceAreaCode");

        public MeteringPointDataQueryBuilder WithProductionEan(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("productionEan", alias, new GraphQlDirective[] { skip, include });

        public MeteringPointDataQueryBuilder ExceptProductionEan() => ExceptField("productionEan");

        public MeteringPointDataQueryBuilder WithEnergyTaxType(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("energyTaxType", alias, new GraphQlDirective[] { skip, include });

        public MeteringPointDataQueryBuilder ExceptEnergyTaxType() => ExceptField("energyTaxType");

        public MeteringPointDataQueryBuilder WithVatType(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("vatType", alias, new GraphQlDirective[] { skip, include });

        public MeteringPointDataQueryBuilder ExceptVatType() => ExceptField("vatType");

        public MeteringPointDataQueryBuilder WithEstimatedAnnualConsumption(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("estimatedAnnualConsumption", alias, new GraphQlDirective[] { skip, include });

        public MeteringPointDataQueryBuilder ExceptEstimatedAnnualConsumption() => ExceptField("estimatedAnnualConsumption");
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
            new FieldMetadata { Name = "priceInfo", IsComplex = true, QueryBuilderType = typeof(PriceInfoQueryBuilder) }
        };

        protected override string TypeName { get; } = "Subscription";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public SubscriptionQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public SubscriptionQueryBuilder WithId(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("id", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionQueryBuilder ExceptId() => ExceptField("id");

        public SubscriptionQueryBuilder WithSubscriber(LegalEntityQueryBuilder legalEntityQueryBuilder) => WithObjectField("subscriber", legalEntityQueryBuilder);

        public SubscriptionQueryBuilder ExceptSubscriber() => ExceptField("subscriber");

        public SubscriptionQueryBuilder WithValidFrom(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("validFrom", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionQueryBuilder ExceptValidFrom() => ExceptField("validFrom");

        public SubscriptionQueryBuilder WithValidTo(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("validTo", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionQueryBuilder ExceptValidTo() => ExceptField("validTo");

        public SubscriptionQueryBuilder WithStatus(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("status", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionQueryBuilder ExceptStatus() => ExceptField("status");

        public SubscriptionQueryBuilder WithPriceInfo(PriceInfoQueryBuilder priceInfoQueryBuilder) => WithObjectField("priceInfo", priceInfoQueryBuilder);

        public SubscriptionQueryBuilder ExceptPriceInfo() => ExceptField("priceInfo");
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

        protected override string TypeName { get; } = "PriceInfo";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public PriceInfoQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public PriceInfoQueryBuilder WithCurrent(PriceQueryBuilder priceQueryBuilder) => WithObjectField("current", priceQueryBuilder);

        public PriceInfoQueryBuilder ExceptCurrent() => ExceptField("current");

        public PriceInfoQueryBuilder WithToday(PriceQueryBuilder priceQueryBuilder) => WithObjectField("today", priceQueryBuilder);

        public PriceInfoQueryBuilder ExceptToday() => ExceptField("today");

        public PriceInfoQueryBuilder WithTomorrow(PriceQueryBuilder priceQueryBuilder) => WithObjectField("tomorrow", priceQueryBuilder);

        public PriceInfoQueryBuilder ExceptTomorrow() => ExceptField("tomorrow");

        public PriceInfoQueryBuilder WithRange(SubscriptionPriceConnectionQueryBuilder subscriptionPriceConnectionQueryBuilder, QueryBuilderParameter<PriceResolution> resolution, QueryBuilderParameter<int?> first = null, QueryBuilderParameter<int?> last = null, QueryBuilderParameter<DateTimeOffset?> before = null, QueryBuilderParameter<DateTimeOffset?> after = null)
        {
            var args = new List<QueryBuilderArgumentInfo>();
            args.Add(new QueryBuilderArgumentInfo { ArgumentName = "resolution", ArgumentValue = resolution });
            if (first != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "first", ArgumentValue = first });

            if (last != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "last", ArgumentValue = last });

            if (before != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "before", ArgumentValue = before });

            if (after != null)
                args.Add(new QueryBuilderArgumentInfo { ArgumentName = "after", ArgumentValue = after });

            return WithObjectField("range", subscriptionPriceConnectionQueryBuilder, args);
        }

        public PriceInfoQueryBuilder ExceptRange()
        {
            return ExceptField("range");
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

        protected override string TypeName { get; } = "Price";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public PriceQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public PriceQueryBuilder WithTotal(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("total", alias, new GraphQlDirective[] { skip, include });

        public PriceQueryBuilder ExceptTotal() => ExceptField("total");

        public PriceQueryBuilder WithEnergy(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("energy", alias, new GraphQlDirective[] { skip, include });

        public PriceQueryBuilder ExceptEnergy() => ExceptField("energy");

        public PriceQueryBuilder WithTax(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("tax", alias, new GraphQlDirective[] { skip, include });

        public PriceQueryBuilder ExceptTax() => ExceptField("tax");

        public PriceQueryBuilder WithStartsAt(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("startsAt", alias, new GraphQlDirective[] { skip, include });

        public PriceQueryBuilder ExceptStartsAt() => ExceptField("startsAt");

        public PriceQueryBuilder WithCurrency(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("currency", alias, new GraphQlDirective[] { skip, include });

        public PriceQueryBuilder ExceptCurrency() => ExceptField("currency");

        public PriceQueryBuilder WithLevel(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("level", alias, new GraphQlDirective[] { skip, include });

        public PriceQueryBuilder ExceptLevel() => ExceptField("level");
    }

    public class SubscriptionPriceConnectionQueryBuilder : GraphQlQueryBuilder<SubscriptionPriceConnectionQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
            {
            new FieldMetadata { Name = "pageInfo", IsComplex = true, QueryBuilderType = typeof(SubscriptionPriceConnectionPageInfoQueryBuilder) },
            new FieldMetadata { Name = "edges", IsComplex = true, QueryBuilderType = typeof(SubscriptionPriceEdgeQueryBuilder) },
            new FieldMetadata { Name = "nodes", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) }
        };

        protected override string TypeName { get; } = "SubscriptionPriceConnection";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public SubscriptionPriceConnectionQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public SubscriptionPriceConnectionQueryBuilder WithPageInfo(SubscriptionPriceConnectionPageInfoQueryBuilder subscriptionPriceConnectionPageInfoQueryBuilder) => WithObjectField("pageInfo", subscriptionPriceConnectionPageInfoQueryBuilder);

        public SubscriptionPriceConnectionQueryBuilder ExceptPageInfo() => ExceptField("pageInfo");

        public SubscriptionPriceConnectionQueryBuilder WithEdges(SubscriptionPriceEdgeQueryBuilder subscriptionPriceEdgeQueryBuilder) => WithObjectField("edges", subscriptionPriceEdgeQueryBuilder);

        public SubscriptionPriceConnectionQueryBuilder ExceptEdges() => ExceptField("edges");

        public SubscriptionPriceConnectionQueryBuilder WithNodes(PriceQueryBuilder priceQueryBuilder) => WithObjectField("nodes", priceQueryBuilder);

        public SubscriptionPriceConnectionQueryBuilder ExceptNodes() => ExceptField("nodes");
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

        protected override string TypeName { get; } = "SubscriptionPriceConnectionPageInfo";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public SubscriptionPriceConnectionPageInfoQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithEndCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("endCursor", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptEndCursor() => ExceptField("endCursor");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithHasNextPage(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("hasNextPage", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptHasNextPage() => ExceptField("hasNextPage");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithHasPreviousPage(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("hasPreviousPage", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptHasPreviousPage() => ExceptField("hasPreviousPage");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithStartCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("startCursor", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptStartCursor() => ExceptField("startCursor");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithResolution(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("resolution", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptResolution() => ExceptField("resolution");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithCurrency(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("currency", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptCurrency() => ExceptField("currency");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithCount(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("count", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptCount() => ExceptField("count");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithPrecision(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("precision", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptPrecision() => ExceptField("precision");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithMinEnergy(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("minEnergy", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptMinEnergy() => ExceptField("minEnergy");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithMinTotal(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("minTotal", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptMinTotal() => ExceptField("minTotal");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithMaxEnergy(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("maxEnergy", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptMaxEnergy() => ExceptField("maxEnergy");

        public SubscriptionPriceConnectionPageInfoQueryBuilder WithMaxTotal(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("maxTotal", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceConnectionPageInfoQueryBuilder ExceptMaxTotal() => ExceptField("maxTotal");
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

        protected override string TypeName { get; } = "PageInfo";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public PageInfoQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public PageInfoQueryBuilder WithEndCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("endCursor", alias, new GraphQlDirective[] { skip, include });

        public PageInfoQueryBuilder ExceptEndCursor() => ExceptField("endCursor");

        public PageInfoQueryBuilder WithHasNextPage(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("hasNextPage", alias, new GraphQlDirective[] { skip, include });

        public PageInfoQueryBuilder ExceptHasNextPage() => ExceptField("hasNextPage");

        public PageInfoQueryBuilder WithHasPreviousPage(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("hasPreviousPage", alias, new GraphQlDirective[] { skip, include });

        public PageInfoQueryBuilder ExceptHasPreviousPage() => ExceptField("hasPreviousPage");

        public PageInfoQueryBuilder WithStartCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("startCursor", alias, new GraphQlDirective[] { skip, include });

        public PageInfoQueryBuilder ExceptStartCursor() => ExceptField("startCursor");

        public PageInfoQueryBuilder WithSubscriptionPriceConnectionPageInfoFragment(SubscriptionPriceConnectionPageInfoQueryBuilder subscriptionPriceConnectionPageInfoQueryBuilder) => WithFragment(subscriptionPriceConnectionPageInfoQueryBuilder);

        public PageInfoQueryBuilder WithHomeConsumptionPageInfoFragment(HomeConsumptionPageInfoQueryBuilder homeConsumptionPageInfoQueryBuilder) => WithFragment(homeConsumptionPageInfoQueryBuilder);

        public PageInfoQueryBuilder WithHomeProductionPageInfoFragment(HomeProductionPageInfoQueryBuilder homeProductionPageInfoQueryBuilder) => WithFragment(homeProductionPageInfoQueryBuilder);
    }

    public class SubscriptionPriceEdgeQueryBuilder : GraphQlQueryBuilder<SubscriptionPriceEdgeQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "cursor" },
            new FieldMetadata { Name = "node", IsComplex = true, QueryBuilderType = typeof(PriceQueryBuilder) }
        };

        protected override string TypeName { get; } = "SubscriptionPriceEdge";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;


        public SubscriptionPriceEdgeQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public SubscriptionPriceEdgeQueryBuilder WithCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("cursor", alias, new GraphQlDirective[] { skip, include });

        public SubscriptionPriceEdgeQueryBuilder ExceptCursor() => ExceptField("cursor");

        public SubscriptionPriceEdgeQueryBuilder WithNode(PriceQueryBuilder priceQueryBuilder) => WithObjectField("node", priceQueryBuilder);

        public SubscriptionPriceEdgeQueryBuilder ExceptNode() => ExceptField("node");
    }

    public class HomeConsumptionConnectionQueryBuilder : GraphQlQueryBuilder<HomeConsumptionConnectionQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "pageInfo", IsComplex = true, QueryBuilderType = typeof(HomeConsumptionPageInfoQueryBuilder) },
            new FieldMetadata { Name = "nodes", IsComplex = true, QueryBuilderType = typeof(ConsumptionEntryQueryBuilder) },
            new FieldMetadata { Name = "edges", IsComplex = true, QueryBuilderType = typeof(HomeConsumptionEdgeQueryBuilder) }
        };

        protected override string TypeName { get; } = "HomeConsumptionConnection";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeConsumptionConnectionQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public HomeConsumptionConnectionQueryBuilder WithPageInfo(HomeConsumptionPageInfoQueryBuilder homeConsumptionPageInfoQueryBuilder) => WithObjectField("pageInfo", homeConsumptionPageInfoQueryBuilder);

        public HomeConsumptionConnectionQueryBuilder ExceptPageInfo() => ExceptField("pageInfo");

        public HomeConsumptionConnectionQueryBuilder WithNodes(ConsumptionEntryQueryBuilder consumptionEntryQueryBuilder) => WithObjectField("nodes", consumptionEntryQueryBuilder);

        public HomeConsumptionConnectionQueryBuilder ExceptNodes() => ExceptField("nodes");

        public HomeConsumptionConnectionQueryBuilder WithEdges(HomeConsumptionEdgeQueryBuilder homeConsumptionEdgeQueryBuilder) => WithObjectField("edges", homeConsumptionEdgeQueryBuilder);

        public HomeConsumptionConnectionQueryBuilder ExceptEdges() => ExceptField("edges");
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
            new FieldMetadata { Name = "totalConsumption" },
            new FieldMetadata { Name = "filtered" }
        };

        protected override string TypeName { get; } = "HomeConsumptionPageInfo";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeConsumptionPageInfoQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public HomeConsumptionPageInfoQueryBuilder WithEndCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("endCursor", alias, new GraphQlDirective[] { skip, include });

        public HomeConsumptionPageInfoQueryBuilder ExceptEndCursor() => ExceptField("endCursor");

        public HomeConsumptionPageInfoQueryBuilder WithHasNextPage(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("hasNextPage", alias, new GraphQlDirective[] { skip, include });

        public HomeConsumptionPageInfoQueryBuilder ExceptHasNextPage() => ExceptField("hasNextPage");

        public HomeConsumptionPageInfoQueryBuilder WithHasPreviousPage(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("hasPreviousPage", alias, new GraphQlDirective[] { skip, include });

        public HomeConsumptionPageInfoQueryBuilder ExceptHasPreviousPage() => ExceptField("hasPreviousPage");

        public HomeConsumptionPageInfoQueryBuilder WithStartCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("startCursor", alias, new GraphQlDirective[] { skip, include });

        public HomeConsumptionPageInfoQueryBuilder ExceptStartCursor() => ExceptField("startCursor");

        public HomeConsumptionPageInfoQueryBuilder WithCount(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("count", alias, new GraphQlDirective[] { skip, include });

        public HomeConsumptionPageInfoQueryBuilder ExceptCount() => ExceptField("count");

        public HomeConsumptionPageInfoQueryBuilder WithCurrency(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("currency", alias, new GraphQlDirective[] { skip, include });

        public HomeConsumptionPageInfoQueryBuilder ExceptCurrency() => ExceptField("currency");

        public HomeConsumptionPageInfoQueryBuilder WithTotalCost(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("totalCost", alias, new GraphQlDirective[] { skip, include });

        public HomeConsumptionPageInfoQueryBuilder ExceptTotalCost() => ExceptField("totalCost");

        public HomeConsumptionPageInfoQueryBuilder WithTotalConsumption(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("totalConsumption", alias, new GraphQlDirective[] { skip, include });

        public HomeConsumptionPageInfoQueryBuilder ExceptTotalConsumption() => ExceptField("totalConsumption");

        public HomeConsumptionPageInfoQueryBuilder WithFiltered(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("filtered", alias, new GraphQlDirective[] { skip, include });

        public HomeConsumptionPageInfoQueryBuilder ExceptFiltered() => ExceptField("filtered");
    }

    public class ConsumptionEntryQueryBuilder : GraphQlQueryBuilder<ConsumptionEntryQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "from" },
            new FieldMetadata { Name = "to" },
            new FieldMetadata { Name = "unitPrice" },
            new FieldMetadata { Name = "unitPriceVAT" },
            new FieldMetadata { Name = "consumption" },
            new FieldMetadata { Name = "consumptionUnit" },
            new FieldMetadata { Name = "cost" },
            new FieldMetadata { Name = "currency" }
        };

        protected override string TypeName { get; } = "Consumption";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public ConsumptionEntryQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public ConsumptionEntryQueryBuilder WithFrom(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("from", alias, new GraphQlDirective[] { skip, include });

        public ConsumptionEntryQueryBuilder ExceptFrom() => ExceptField("from");

        public ConsumptionEntryQueryBuilder WithTo(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("to", alias, new GraphQlDirective[] { skip, include });

        public ConsumptionEntryQueryBuilder ExceptTo() => ExceptField("to");

        public ConsumptionEntryQueryBuilder WithUnitPrice(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("unitPrice", alias, new GraphQlDirective[] { skip, include });

        public ConsumptionEntryQueryBuilder ExceptUnitPrice() => ExceptField("unitPrice");

        public ConsumptionEntryQueryBuilder WithUnitPriceVat(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("unitPriceVAT", alias, new GraphQlDirective[] { skip, include });

        public ConsumptionEntryQueryBuilder ExceptUnitPriceVat() => ExceptField("unitPriceVAT");

        public ConsumptionEntryQueryBuilder WithConsumption(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("consumption", alias, new GraphQlDirective[] { skip, include });

        public ConsumptionEntryQueryBuilder ExceptConsumption() => ExceptField("consumption");

        public ConsumptionEntryQueryBuilder WithConsumptionUnit(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("consumptionUnit", alias, new GraphQlDirective[] { skip, include });

        public ConsumptionEntryQueryBuilder ExceptConsumptionUnit() => ExceptField("consumptionUnit");

        public ConsumptionEntryQueryBuilder WithCost(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("cost", alias, new GraphQlDirective[] { skip, include });

        public ConsumptionEntryQueryBuilder ExceptCost() => ExceptField("cost");

        public ConsumptionEntryQueryBuilder WithCurrency(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("currency", alias, new GraphQlDirective[] { skip, include });

        public ConsumptionEntryQueryBuilder ExceptCurrency() => ExceptField("currency");
    }

    public class HomeConsumptionEdgeQueryBuilder : GraphQlQueryBuilder<HomeConsumptionEdgeQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "cursor" },
            new FieldMetadata { Name = "node", IsComplex = true, QueryBuilderType = typeof(ConsumptionEntryQueryBuilder) }
        };

        protected override string TypeName { get; } = "HomeConsumptionEdge";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeConsumptionEdgeQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public HomeConsumptionEdgeQueryBuilder WithCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("cursor", alias, new GraphQlDirective[] { skip, include });

        public HomeConsumptionEdgeQueryBuilder ExceptCursor() => ExceptField("cursor");

        public HomeConsumptionEdgeQueryBuilder WithNode(ConsumptionEntryQueryBuilder consumptionEntryQueryBuilder) => WithObjectField("node", consumptionEntryQueryBuilder);

        public HomeConsumptionEdgeQueryBuilder ExceptNode() => ExceptField("node");
    }

    public class HomeProductionConnectionQueryBuilder : GraphQlQueryBuilder<HomeProductionConnectionQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "pageInfo", IsComplex = true, QueryBuilderType = typeof(HomeProductionPageInfoQueryBuilder) },
            new FieldMetadata { Name = "nodes", IsComplex = true, QueryBuilderType = typeof(ProductionEntryQueryBuilder) },
            new FieldMetadata { Name = "edges", IsComplex = true, QueryBuilderType = typeof(HomeProductionEdgeQueryBuilder) }
        };

        protected override string TypeName { get; } = "HomeProductionConnection";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeProductionConnectionQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public HomeProductionConnectionQueryBuilder WithPageInfo(HomeProductionPageInfoQueryBuilder homeProductionPageInfoQueryBuilder) => WithObjectField("pageInfo", homeProductionPageInfoQueryBuilder);

        public HomeProductionConnectionQueryBuilder ExceptPageInfo() => ExceptField("pageInfo");

        public HomeProductionConnectionQueryBuilder WithNodes(ProductionEntryQueryBuilder productionEntryQueryBuilder) => WithObjectField("nodes", productionEntryQueryBuilder);

        public HomeProductionConnectionQueryBuilder ExceptNodes() => ExceptField("nodes");

        public HomeProductionConnectionQueryBuilder WithEdges(HomeProductionEdgeQueryBuilder homeProductionEdgeQueryBuilder) => WithObjectField("edges", homeProductionEdgeQueryBuilder);

        public HomeProductionConnectionQueryBuilder ExceptEdges() => ExceptField("edges");
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

        protected override string TypeName { get; } = "HomeProductionPageInfo";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeProductionPageInfoQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public HomeProductionPageInfoQueryBuilder WithEndCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("endCursor", alias, new GraphQlDirective[] { skip, include });

        public HomeProductionPageInfoQueryBuilder ExceptEndCursor() => ExceptField("endCursor");

        public HomeProductionPageInfoQueryBuilder WithHasNextPage(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("hasNextPage", alias, new GraphQlDirective[] { skip, include });

        public HomeProductionPageInfoQueryBuilder ExceptHasNextPage() => ExceptField("hasNextPage");

        public HomeProductionPageInfoQueryBuilder WithHasPreviousPage(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("hasPreviousPage", alias, new GraphQlDirective[] { skip, include });

        public HomeProductionPageInfoQueryBuilder ExceptHasPreviousPage() => ExceptField("hasPreviousPage");

        public HomeProductionPageInfoQueryBuilder WithStartCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("startCursor", alias, new GraphQlDirective[] { skip, include });

        public HomeProductionPageInfoQueryBuilder ExceptStartCursor() => ExceptField("startCursor");

        public HomeProductionPageInfoQueryBuilder WithCount(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("count", alias, new GraphQlDirective[] { skip, include });

        public HomeProductionPageInfoQueryBuilder ExceptCount() => ExceptField("count");

        public HomeProductionPageInfoQueryBuilder WithCurrency(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("currency", alias, new GraphQlDirective[] { skip, include });

        public HomeProductionPageInfoQueryBuilder ExceptCurrency() => ExceptField("currency");

        public HomeProductionPageInfoQueryBuilder WithTotalProfit(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("totalProfit", alias, new GraphQlDirective[] { skip, include });

        public HomeProductionPageInfoQueryBuilder ExceptTotalProfit() => ExceptField("totalProfit");

        public HomeProductionPageInfoQueryBuilder WithTotalProduction(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("totalProduction", alias, new GraphQlDirective[] { skip, include });

        public HomeProductionPageInfoQueryBuilder ExceptTotalProduction() => ExceptField("totalProduction");

        public HomeProductionPageInfoQueryBuilder WithFiltered(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("filtered", alias, new GraphQlDirective[] { skip, include });

        public HomeProductionPageInfoQueryBuilder ExceptFiltered() => ExceptField("filtered");
    }

    public class ProductionEntryQueryBuilder : GraphQlQueryBuilder<ProductionEntryQueryBuilder>
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

        protected override string TypeName { get; } = "Production";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public ProductionEntryQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public ProductionEntryQueryBuilder WithFrom(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("from", alias, new GraphQlDirective[] { skip, include });

        public ProductionEntryQueryBuilder ExceptFrom() => ExceptField("from");

        public ProductionEntryQueryBuilder WithTo(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("to", alias, new GraphQlDirective[] { skip, include });

        public ProductionEntryQueryBuilder ExceptTo() => ExceptField("to");

        public ProductionEntryQueryBuilder WithUnitPrice(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("unitPrice", alias, new GraphQlDirective[] { skip, include });

        public ProductionEntryQueryBuilder ExceptUnitPrice() => ExceptField("unitPrice");

        public ProductionEntryQueryBuilder WithUnitPriceVat(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("unitPriceVAT", alias, new GraphQlDirective[] { skip, include });

        public ProductionEntryQueryBuilder ExceptUnitPriceVat() => ExceptField("unitPriceVAT");

        public ProductionEntryQueryBuilder WithProduction(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("production", alias, new GraphQlDirective[] { skip, include });

        public ProductionEntryQueryBuilder ExceptProduction() => ExceptField("production");

        public ProductionEntryQueryBuilder WithProductionUnit(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("productionUnit", alias, new GraphQlDirective[] { skip, include });

        public ProductionEntryQueryBuilder ExceptProductionUnit() => ExceptField("productionUnit");

        public ProductionEntryQueryBuilder WithProfit(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("profit", alias, new GraphQlDirective[] { skip, include });

        public ProductionEntryQueryBuilder ExceptProfit() => ExceptField("profit");

        public ProductionEntryQueryBuilder WithCurrency(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("currency", alias, new GraphQlDirective[] { skip, include });

        public ProductionEntryQueryBuilder ExceptCurrency() => ExceptField("currency");
    }

    public class HomeProductionEdgeQueryBuilder : GraphQlQueryBuilder<HomeProductionEdgeQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "cursor" },
            new FieldMetadata { Name = "node", IsComplex = true, QueryBuilderType = typeof(ProductionEntryQueryBuilder) }
        };

        protected override string TypeName { get; } = "HomeProductionEdge";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeProductionEdgeQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public HomeProductionEdgeQueryBuilder WithCursor(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("cursor", alias, new GraphQlDirective[] { skip, include });

        public HomeProductionEdgeQueryBuilder ExceptCursor() => ExceptField("cursor");

        public HomeProductionEdgeQueryBuilder WithNode(ProductionEntryQueryBuilder productionEntryQueryBuilder) => WithObjectField("node", productionEntryQueryBuilder);

        public HomeProductionEdgeQueryBuilder ExceptNode() => ExceptField("node");
    }

    public class HomeFeaturesQueryBuilder : GraphQlQueryBuilder<HomeFeaturesQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "realTimeConsumptionEnabled" }
        };

        protected override string TypeName { get; } = "HomeFeatures";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public HomeFeaturesQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public HomeFeaturesQueryBuilder WithRealTimeConsumptionEnabled(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("realTimeConsumptionEnabled", alias, new GraphQlDirective[] { skip, include });

        public HomeFeaturesQueryBuilder ExceptRealTimeConsumptionEnabled() => ExceptField("realTimeConsumptionEnabled");
    }

    public class TibberMutationQueryBuilder : GraphQlQueryBuilder<TibberMutationQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "sendMeterReading", IsComplex = true, QueryBuilderType = typeof(MeterReadingResponseQueryBuilder) },
            new FieldMetadata { Name = "updateHome", IsComplex = true, QueryBuilderType = typeof(HomeQueryBuilder) },
            new FieldMetadata { Name = "sendPushNotification", IsComplex = true, QueryBuilderType = typeof(PushNotificationResponseQueryBuilder) }
        };

        protected override string Prefix { get; } = "mutation";

        protected override string TypeName { get; } = "RootMutation";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public TibberMutationQueryBuilder(string alias = null) : base(alias, null)
        {
        }

        public TibberMutationQueryBuilder WithParameter<T>(GraphQlQueryParameter<T> parameter) => WithParameterInternal(parameter);

        public TibberMutationQueryBuilder WithSendMeterReading(MeterReadingResponseQueryBuilder meterReadingResponseQueryBuilder, QueryBuilderParameter<MeterReadingInput> input)
        {
            var args = new List<QueryBuilderArgumentInfo>();
            args.Add(new QueryBuilderArgumentInfo { ArgumentName = "input", ArgumentValue = input });
            return WithObjectField("sendMeterReading", meterReadingResponseQueryBuilder, args);
        }

        public TibberMutationQueryBuilder ExceptSendMeterReading()
        {
            return ExceptField("sendMeterReading");
        }

        public TibberMutationQueryBuilder WithUpdateHome(HomeQueryBuilder homeQueryBuilder, QueryBuilderParameter<UpdateHomeInput> input)
        {
            var args = new List<QueryBuilderArgumentInfo>();
            args.Add(new QueryBuilderArgumentInfo { ArgumentName = "input", ArgumentValue = input });
            return WithObjectField("updateHome", homeQueryBuilder, args);
        }

        public TibberMutationQueryBuilder ExceptUpdateHome()
        {
            return ExceptField("updateHome");
        }

        public TibberMutationQueryBuilder WithSendPushNotification(PushNotificationResponseQueryBuilder pushNotificationResponseQueryBuilder, QueryBuilderParameter<PushNotificationInput> input)
        {
            var args = new List<QueryBuilderArgumentInfo>();
            args.Add(new QueryBuilderArgumentInfo { ArgumentName = "input", ArgumentValue = input });
            return WithObjectField("sendPushNotification", pushNotificationResponseQueryBuilder, args);
        }

        public TibberMutationQueryBuilder ExceptSendPushNotification()
        {
            return ExceptField("sendPushNotification");
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

        protected override string TypeName { get; } = "MeterReadingResponse";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public MeterReadingResponseQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public MeterReadingResponseQueryBuilder WithHomeId(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("homeId", alias, new GraphQlDirective[] { skip, include });

        public MeterReadingResponseQueryBuilder ExceptHomeId() => ExceptField("homeId");

        public MeterReadingResponseQueryBuilder WithTime(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("time", alias, new GraphQlDirective[] { skip, include });

        public MeterReadingResponseQueryBuilder ExceptTime() => ExceptField("time");

        public MeterReadingResponseQueryBuilder WithReading(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("reading", alias, new GraphQlDirective[] { skip, include });

        public MeterReadingResponseQueryBuilder ExceptReading() => ExceptField("reading");
    }

    public class PushNotificationResponseQueryBuilder : GraphQlQueryBuilder<PushNotificationResponseQueryBuilder>
    {
        private static readonly FieldMetadata[] AllFieldMetadata =
        {
            new FieldMetadata { Name = "successful" },
            new FieldMetadata { Name = "pushedToNumberOfDevices" }
        };

        protected override string TypeName { get; } = "PushNotificationResponse";

        protected override IList<FieldMetadata> AllFields { get; } = AllFieldMetadata;

        public PushNotificationResponseQueryBuilder(string alias = null, SkipDirective skip = null, IncludeDirective include = null) : base(alias, new GraphQlDirective[] { skip, include })
        {
        }

        public PushNotificationResponseQueryBuilder WithSuccessful(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("successful", alias, new GraphQlDirective[] { skip, include });

        public PushNotificationResponseQueryBuilder ExceptSuccessful() => ExceptField("successful");

        public PushNotificationResponseQueryBuilder WithPushedToNumberOfDevices(string alias = null, SkipDirective skip = null, IncludeDirective include = null) => WithScalarField("pushedToNumberOfDevices", alias, new GraphQlDirective[] { skip, include });

        public PushNotificationResponseQueryBuilder ExceptPushedToNumberOfDevices() => ExceptField("pushedToNumberOfDevices");
    }
    #endregion

    #region input classes
    public class MeterReadingInput : IGraphQlInputObject
    {
        private InputPropertyInfo _homeId;
        private InputPropertyInfo _time;
        private InputPropertyInfo _reading;

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<Guid?>))]
#endif
        public QueryBuilderParameter<Guid?> HomeId
        {
            get => (QueryBuilderParameter<Guid?>)_homeId.Value;
            set => _homeId = new InputPropertyInfo { Name = "homeId", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<string>))]
#endif
        public QueryBuilderParameter<string> Time
        {
            get => (QueryBuilderParameter<string>)_time.Value;
            set => _time = new InputPropertyInfo { Name = "time", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<int?>))]
#endif
        public QueryBuilderParameter<int?> Reading
        {
            get => (QueryBuilderParameter<int?>)_reading.Value;
            set => _reading = new InputPropertyInfo { Name = "reading", Value = value };
        }

        IEnumerable<InputPropertyInfo> IGraphQlInputObject.GetPropertyValues()
        {
            if (_homeId.Name != null) yield return _homeId;
            if (_time.Name != null) yield return _time;
            if (_reading.Name != null) yield return _reading;
        }
    }

    public class UpdateHomeInput : IGraphQlInputObject
    {
        private InputPropertyInfo _homeId;
        private InputPropertyInfo _appNickname;
        private InputPropertyInfo _appAvatar;
        private InputPropertyInfo _size;
        private InputPropertyInfo _type;
        private InputPropertyInfo _numberOfResidents;
        private InputPropertyInfo _primaryHeatingSource;
        private InputPropertyInfo _hasVentilationSystem;
        private InputPropertyInfo _mainFuseSize;

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<Guid?>))]
#endif
        public QueryBuilderParameter<Guid?> HomeId
        {
            get => (QueryBuilderParameter<Guid?>)_homeId.Value;
            set => _homeId = new InputPropertyInfo { Name = "homeId", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<string>))]
#endif
        public QueryBuilderParameter<string> AppNickname
        {
            get => (QueryBuilderParameter<string>)_appNickname.Value;
            set => _appNickname = new InputPropertyInfo { Name = "appNickname", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<HomeAvatar?>))]
#endif
        public QueryBuilderParameter<HomeAvatar?> AppAvatar
        {
            get => (QueryBuilderParameter<HomeAvatar?>)_appAvatar.Value;
            set => _appAvatar = new InputPropertyInfo { Name = "appAvatar", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<int?>))]
#endif
        public QueryBuilderParameter<int?> Size
        {
            get => (QueryBuilderParameter<int?>)_size.Value;
            set => _size = new InputPropertyInfo { Name = "size", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<HomeType?>))]
#endif
        public QueryBuilderParameter<HomeType?> Type
        {
            get => (QueryBuilderParameter<HomeType?>)_type.Value;
            set => _type = new InputPropertyInfo { Name = "type", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<int?>))]
#endif
        public QueryBuilderParameter<int?> NumberOfResidents
        {
            get => (QueryBuilderParameter<int?>)_numberOfResidents.Value;
            set => _numberOfResidents = new InputPropertyInfo { Name = "numberOfResidents", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<HeatingSource?>))]
#endif
        public QueryBuilderParameter<HeatingSource?> PrimaryHeatingSource
        {
            get => (QueryBuilderParameter<HeatingSource?>)_primaryHeatingSource.Value;
            set => _primaryHeatingSource = new InputPropertyInfo { Name = "primaryHeatingSource", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<bool?>))]
#endif
        public QueryBuilderParameter<bool?> HasVentilationSystem
        {
            get => (QueryBuilderParameter<bool?>)_hasVentilationSystem.Value;
            set => _hasVentilationSystem = new InputPropertyInfo { Name = "hasVentilationSystem", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<int?>))]
#endif
        public QueryBuilderParameter<int?> MainFuseSize
        {
            get => (QueryBuilderParameter<int?>)_mainFuseSize.Value;
            set => _mainFuseSize = new InputPropertyInfo { Name = "mainFuseSize", Value = value };
        }

        IEnumerable<InputPropertyInfo> IGraphQlInputObject.GetPropertyValues()
        {
            if (_homeId.Name != null) yield return _homeId;
            if (_appNickname.Name != null) yield return _appNickname;
            if (_appAvatar.Name != null) yield return _appAvatar;
            if (_size.Name != null) yield return _size;
            if (_type.Name != null) yield return _type;
            if (_numberOfResidents.Name != null) yield return _numberOfResidents;
            if (_primaryHeatingSource.Name != null) yield return _primaryHeatingSource;
            if (_hasVentilationSystem.Name != null) yield return _hasVentilationSystem;
            if (_mainFuseSize.Name != null) yield return _mainFuseSize;
        }
    }

    public class PushNotificationInput : IGraphQlInputObject
    {
        private InputPropertyInfo _title;
        private InputPropertyInfo _message;
        private InputPropertyInfo _screenToOpen;

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<string>))]
#endif
        public QueryBuilderParameter<string> Title
        {
            get => (QueryBuilderParameter<string>)_title.Value;
            set => _title = new InputPropertyInfo { Name = "title", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<string>))]
#endif
        public QueryBuilderParameter<string> Message
        {
            get => (QueryBuilderParameter<string>)_message.Value;
            set => _message = new InputPropertyInfo { Name = "message", Value = value };
        }

#if !GRAPHQL_GENERATOR_DISABLE_NEWTONSOFT_JSON
        [JsonConverter(typeof(QueryBuilderParameterConverter<AppScreen?>))]
#endif
        public QueryBuilderParameter<AppScreen?> ScreenToOpen
        {
            get => (QueryBuilderParameter<AppScreen?>)_screenToOpen.Value;
            set => _screenToOpen = new InputPropertyInfo { Name = "screenToOpen", Value = value };
        }

        IEnumerable<InputPropertyInfo> IGraphQlInputObject.GetPropertyValues()
        {
            if (_title.Name != null) yield return _title;
            if (_message.Name != null) yield return _message;
            if (_screenToOpen.Name != null) yield return _screenToOpen;
        }
    }
    #endregion

    #region data classes
    public class Tibber
    {
        /// <summary>
        /// This contains data about the logged-in user
        /// </summary>
        public Viewer Viewer { get; set; }
    }

    public class Viewer
    {
        public string Login { get; set; }
        /// <summary>
        /// Unique user identifier
        /// </summary>
        public string UserId { get; set; }
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

    /// <summary>
    /// Address information
    /// </summary>
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
        /// Nordpool spot price
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

    public class TibberMutation
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
