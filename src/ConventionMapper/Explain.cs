using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ConventionMapper.Internal;

namespace ConventionMapper
{
    /// <summary>Builds the text returned by IConfigurationProvider.Explain.</summary>
    internal static class MapExplainer
    {
        public static string Explain(TypeMap typeMap, MapperConfiguration configuration)
        {
            var sb = new StringBuilder();
            sb.Append(Name(typeMap.SourceType)).Append(" -> ").Append(Name(typeMap.DestinationType))
              .Append("   (profile '").Append(typeMap.ProfileName).AppendLine("')");

            if (typeMap.TypeConverter != null)
            {
                sb.AppendLine("  Converted as a whole by a type converter (ConvertUsing / ConvertUsingEnumMapping).");
                foreach (var error in typeMap.Config.ValidationErrors) sb.Append("  ! ").AppendLine(error);
                return sb.ToString();
            }

            sb.Append("  Construction: ").AppendLine(DescribeConstruction(typeMap));
            if (typeMap.ConstructorMap != null)
            {
                foreach (var p in typeMap.ConstructorMap.Parameters)
                    sb.Append("    ").Append(p.Parameter.Name).Append(" <- ").AppendLine(p.Source == null ? "(default value)" : p.Source.Describe());
            }

            var rows = new List<(string Name, string Description)>();
            foreach (var map in typeMap.PropertyMaps)
            {
                string description;
                if (map.Ignored) description = "(ignored)";
                else if (map.Source == null) description = map.DestinationMember.CanWrite ? "(UNMAPPED)" : "(read-only, skipped)";
                else description = "<- " + map.Source.Describe() + Notes(map, configuration);
                rows.Add((map.DestinationName, description));
            }
            foreach (var path in typeMap.PathMaps)
            {
                var name = string.Join(".", path.Path.Select(m => m.Name));
                rows.Add((name, "<- " + path.Source.Describe() + (path.Condition != null ? "   [condition]" : string.Empty)));
            }

            if (rows.Count > 0)
            {
                sb.AppendLine("  Members:");
                var width = rows.Max(r => r.Name.Length);
                foreach (var row in rows) sb.Append("    ").Append(row.Name.PadRight(width)).Append("  ").AppendLine(row.Description);
            }

            var extras = new List<string>();
            if (typeMap.BeforeMaps.Length > 0) extras.Add(typeMap.BeforeMaps.Length + " before-map action(s)");
            if (typeMap.AfterMaps.Length > 0) extras.Add(typeMap.AfterMaps.Length + " after-map action(s)");
            if (typeMap.PreserveReferences) extras.Add("preserves references (cycle-safe)");
            if (typeMap.MaxDepth > 0) extras.Add("max depth " + typeMap.MaxDepth);
            if (typeMap.EqualityComparison != null) extras.Add("items are matched by EqualityComparison when mapping into existing collections");
            if (typeMap.IgnoreNullSourceValues) extras.Add("null source values are ignored");
            if (typeMap.DerivedMaps.Count > 0)
                extras.Add("derived maps: " + string.Join(", ", typeMap.DerivedMaps.Select(m => Name(m.SourceType) + " -> " + Name(m.DestinationType))));
            foreach (var extra in extras) sb.Append("  * ").AppendLine(extra);

            return sb.ToString();
        }

        private static string DescribeConstruction(TypeMap typeMap)
        {
            if (typeMap.CustomCtor != null) return "ConstructUsing";
            if (typeMap.ConstructorMap != null)
                return "constructor (" + string.Join(", ", typeMap.ConstructorMap.Parameters.Select(p => Name(p.ParameterType) + " " + p.Parameter.Name)) + ")";
            if (typeMap.Factory != null) return "parameterless constructor";
            return typeMap.DerivedMaps.Count > 0 ? "abstract; a derived map is chosen at runtime" : "none available";
        }

        private static string Notes(PropertyMap map, MapperConfiguration configuration)
        {
            var notes = new List<string>();
            var source = map.SourceValueType;
            var destination = map.DestinationType;
            if (source != destination)
            {
                var text = Name(source) + " -> " + Name(destination);
                if (configuration.ResolveTypeMap(source, source, destination) != null)
                {
                    text += " via map";
                }
                else if (ReflectionHelper.IsCollectionType(source) && ReflectionHelper.IsCollectionType(destination))
                {
                    var sourceElement = ReflectionHelper.GetElementType(source);
                    var destinationElement = ReflectionHelper.GetElementType(destination);
                    if (configuration.ResolveTypeMap(sourceElement, sourceElement, destinationElement) != null)
                        text += ", items via map " + Name(sourceElement) + " -> " + Name(destinationElement);
                }
                notes.Add(text);
            }
            if (map.PreCondition != null) notes.Add("pre-condition");
            if (map.Condition != null) notes.Add("condition");
            if (map.HasNullSubstitute) notes.Add("null substitute: " + (map.NullSubstitute ?? "null"));
            if (map.UseDestinationValue) notes.Add("uses destination value");
            if (map.MappingOrder.HasValue) notes.Add("order " + map.MappingOrder.Value);
            if (map.ExplicitExpansion) notes.Add("ProjectTo: only when expanded");
            if (map.Transformers.Length > 0) notes.Add(map.Transformers.Length + " value transform(s)");
            return notes.Count == 0 ? string.Empty : "   [" + string.Join("; ", notes) + "]";
        }

        private static string Name(Type type) => ReflectionHelper.FriendlyName(type);
    }
}
