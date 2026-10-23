using System;
using System.Collections.Generic;
using System.Text;

namespace ConventionMapper
{
    /// <summary>Thrown when the mapping configuration is invalid (e.g. by AssertConfigurationIsValid).</summary>
    public class MapperConfigurationException : Exception
    {
        public MapperConfigurationException(string message) : base(message) { Errors = new[] { message }; }

        public MapperConfigurationException(string message, Exception innerException) : base(message, innerException) { Errors = new[] { message }; }

        public MapperConfigurationException(IReadOnlyList<string> errors) : base(BuildMessage(errors)) { Errors = errors; }

        /// <summary>Individual problems found.</summary>
        public IReadOnlyList<string> Errors { get; }

        private static string BuildMessage(IReadOnlyList<string> errors)
        {
            var sb = new StringBuilder();
            sb.Append("The mapping configuration is invalid. ").Append(errors.Count).AppendLine(" problem(s) found:");
            foreach (var error in errors)
            {
                sb.AppendLine();
                sb.AppendLine(error);
            }
            return sb.ToString();
        }
    }

    /// <summary>Thrown when a mapping fails at runtime.</summary>
    public class MappingException : Exception
    {
        public MappingException(string message) : base(message) { }

        public MappingException(string message, Exception innerException) : base(message, innerException) { }

        public MappingException(string message, Exception? innerException, Type? sourceType, Type? destinationType, string? memberName)
            : base(message, innerException)
        {
            SourceType = sourceType;
            DestinationType = destinationType;
            MemberName = memberName;
        }

        public Type? SourceType { get; }
        public Type? DestinationType { get; }
        public string? MemberName { get; }

        /// <summary>Full path to the failing member, e.g. "OrderDto.Lines[3].Product".</summary>
        public string? MemberPath { get; private set; }

        public override string Message =>
            MemberPath == null ? base.Message : base.Message + Environment.NewLine + Environment.NewLine + "Path:" + Environment.NewLine + MemberPath;

        /// <summary>Records the path at the innermost failure point; outer frames leave it unchanged.</summary>
        internal MappingException CapturePath(ResolutionContext context)
        {
            if (MemberPath == null)
            {
                var path = context.FormatPath();
                if (path.Length > 0) MemberPath = path;
            }
            return this;
        }

        private static string Types(Type source, Type destination) =>
            "Mapping types:" + Environment.NewLine +
            source.Name + " -> " + destination.Name + Environment.NewLine +
            source.FullName + " -> " + destination.FullName;

        internal static MappingException MissingMap(Type source, Type destination) => new MappingException(
            "Missing type map configuration or unsupported mapping." + Environment.NewLine + Environment.NewLine + Types(source, destination),
            null, source, destination, null);

        internal static MappingException ForTypes(Type source, Type destination, Exception inner) => new MappingException(
            "Error mapping types." + Environment.NewLine + Environment.NewLine + Types(source, destination),
            inner, source, destination, null);

        internal static MappingException ForMember(TypeMap typeMap, string member, Exception inner) => new MappingException(
            "Error mapping types." + Environment.NewLine + Environment.NewLine + Types(typeMap.SourceType, typeMap.DestinationType) +
            Environment.NewLine + Environment.NewLine + "Destination member:" + Environment.NewLine + member,
            inner, typeMap.SourceType, typeMap.DestinationType, member);
    }
}
