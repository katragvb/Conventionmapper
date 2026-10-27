using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ConventionMapper.Roslyn
{
    /// <summary>
    /// CM0032: [Sensitive] data leaving through the paths mappers don't guard, which is how most leaks happen: an object with
    /// [Sensitive] members passed to a logger, put into an exception message, or printed by ToString() (a hand-written override
    /// that reads a [Sensitive] member, or a record's compiler-generated one). Redacted values ([MapRedact] methods) and log
    /// strings ([MapLogString]) are fine. Shared by the generator's and the runtime package's analyzers; the runtime copy
    /// stays quiet when the generator is also active, so nothing is reported twice.
    /// </summary>
    internal static class SensitiveFlow
    {
        public static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            "CM0032", "[Sensitive] data written to a log, exception or ToString",
            "{0}",
            "ConventionMapper", DiagnosticSeverity.Warning, true,
            helpLinkUri: "https://github.com/katragvb/Conventionmapper/blob/main/docs/diagnostics.md#cm0032");

        private const string SensitiveName = "ConventionMapper.SensitiveAttribute";

        /// <summary>Registers the checks; the caller configures generated-code analysis and concurrency.</summary>
        public static void Register(AnalysisContext context, bool deferToGenerator)
        {
            context.RegisterCompilationStartAction(start =>
            {
                // The generator adds its attributes to this assembly; a referenced project's generated attributes don't count.
                if (deferToGenerator && start.Compilation.Assembly.GetTypeByMetadataName("ConventionMapper.GeneratedMapperAttribute") != null) return;
                var exception = start.Compilation.GetTypeByMetadataName("System.Exception");
                var cache = new ConcurrentDictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);

                start.RegisterOperationAction(c => AnalyzeInvocation(c, cache), OperationKind.Invocation);
                start.RegisterOperationAction(c => AnalyzeException(c, cache, exception), OperationKind.ObjectCreation);
                start.RegisterOperationAction(AnalyzeToString, OperationKind.PropertyReference, OperationKind.FieldReference);
                start.RegisterSymbolAction(AnalyzeRecord, SymbolKind.NamedType);
            });
        }

        // ------------------------------------------------------------------ loggers

        private static void AnalyzeInvocation(OperationAnalysisContext context, ConcurrentDictionary<ITypeSymbol, string> cache)
        {
            var invocation = (IInvocationOperation)context.Operation;
            if (!IsLogCall(invocation.TargetMethod)) return;
            foreach (var argument in invocation.Arguments)
                foreach (var value in Values(argument.Value))
                {
                    var path = SensitivePath(value.Type, cache);
                    if (path == null || IsRedacted(value)) continue;
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, value.Syntax.GetLocation(),
                        "'" + value.Syntax + "' (" + Display(value.Type!) + ") has the [Sensitive] member " + path + " and is written to the log by " +
                        invocation.TargetMethod.Name + "(); log a [MapLogString] or [MapRedact] result instead"));
                }
        }

        /// <summary>Microsoft.Extensions.Logging and Serilog calls, and [LoggerMessage] methods.</summary>
        private static bool IsLogCall(IMethodSymbol method)
        {
            if (method.GetAttributes().Any(a => a.AttributeClass?.Name == "LoggerMessageAttribute")) return true;
            var ns = method.ContainingType?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (!ns.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal) && !ns.StartsWith("Serilog", StringComparison.Ordinal)) return false;
            switch (method.Name)
            {
                case "Log": case "LogTrace": case "LogDebug": case "LogInformation": case "LogWarning": case "LogError": case "LogCritical":
                case "BeginScope": case "Verbose": case "Debug": case "Information": case "Warning": case "Error": case "Fatal": case "Write":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The values an argument passes: params array elements, interpolation holes and concatenated parts, without conversions.</summary>
        private static IEnumerable<IOperation> Values(IOperation operation)
        {
            var value = Unwrap(operation);
            switch (value)
            {
                case IArrayCreationOperation array when array.Initializer != null:
                    foreach (var element in array.Initializer.ElementValues)
                        foreach (var inner in Values(element)) yield return inner;
                    break;
                case IInterpolatedStringOperation text:
                    foreach (var part in text.Parts.OfType<IInterpolationOperation>())
                        foreach (var inner in Values(part.Expression)) yield return inner;
                    break;
                case IBinaryOperation binary when binary.OperatorKind == BinaryOperatorKind.Add && binary.Type?.SpecialType == SpecialType.System_String:
                    foreach (var inner in Values(binary.LeftOperand)) yield return inner;
                    foreach (var inner in Values(binary.RightOperand)) yield return inner;
                    break;
                case IInvocationOperation call when call.TargetMethod.Name == "ToString" && call.Instance != null && call.Arguments.Length == 0:
                    yield return Unwrap(call.Instance);
                    break;
                default:
                    if (value.Type != null && value.Type.SpecialType != SpecialType.System_String) yield return value;
                    break;
            }
        }

        private static IOperation Unwrap(IOperation operation)
        {
            while (operation is IConversionOperation conversion) operation = conversion.Operand;
            return operation;
        }

        /// <summary>A call to a [MapRedact] method: its result has the [Sensitive] members masked.</summary>
        private static bool IsRedacted(IOperation value) =>
            value is IInvocationOperation call && call.TargetMethod.GetAttributes().Any(a => a.AttributeClass?.Name == "MapRedactAttribute");

        // ------------------------------------------------------------------ exception messages

        private static void AnalyzeException(OperationAnalysisContext context, ConcurrentDictionary<ITypeSymbol, string> cache, INamedTypeSymbol? exception)
        {
            var creation = (IObjectCreationOperation)context.Operation;
            if (exception == null || !InheritsFrom(creation.Type, exception)) return;
            foreach (var argument in creation.Arguments)
            {
                if (argument.Parameter?.Type.SpecialType != SpecialType.System_String) continue;
                foreach (var value in Values(argument.Value))
                {
                    var path = SensitivePath(value.Type, cache);
                    if (path == null || IsRedacted(value)) continue;
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, value.Syntax.GetLocation(),
                        "'" + value.Syntax + "' (" + Display(value.Type!) + ") has the [Sensitive] member " + path + " and goes into the message of " +
                        creation.Type!.Name + ", which logs and error pages show; use a [MapLogString] result or leave it out"));
                }
            }
        }

        private static bool InheritsFrom(ITypeSymbol? type, INamedTypeSymbol baseType)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (SymbolEqualityComparer.Default.Equals(t, baseType)) return true;
            return false;
        }

        // ------------------------------------------------------------------ ToString

        /// <summary>A hand-written ToString() override that reads a [Sensitive] member.</summary>
        private static void AnalyzeToString(OperationAnalysisContext context)
        {
            if (!(context.ContainingSymbol is IMethodSymbol method) || method.Name != "ToString" || !method.IsOverride || method.Parameters.Length != 0) return;
            var member = context.Operation is IPropertyReferenceOperation property ? (ISymbol)property.Property : ((IFieldReferenceOperation)context.Operation).Field;
            if (!IsSensitive(member)) return;
            context.ReportDiagnostic(Diagnostic.Create(Descriptor, context.Operation.Syntax.GetLocation(),
                "ToString() of '" + method.ContainingType.Name + "' writes the [Sensitive] member '" + member.ContainingType?.Name + "." + member.Name +
                "', so it shows up wherever the object is logged or printed; leave it out or mask it"));
        }

        /// <summary>A record's compiler-generated ToString() prints every public property, [Sensitive] ones included.</summary>
        private static void AnalyzeRecord(SymbolAnalysisContext context)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (!type.IsRecord) return;
            var sensitive = type.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(p => p.DeclaredAccessibility == Accessibility.Public && IsSensitive(p));
            if (sensitive == null) return;
            var custom = type.GetMembers().OfType<IMethodSymbol>().Any(m => !m.IsImplicitlyDeclared &&
                (m.Name == "ToString" && m.Parameters.Length == 0 || m.Name == "PrintMembers"));
            if (custom) return;
            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            if (location == null) return;
            context.ReportDiagnostic(Diagnostic.Create(Descriptor, location,
                "Record '" + type.Name + "' prints all its public properties in ToString(), including the [Sensitive] '" + sensitive.Name +
                "'; override PrintMembers (or ToString) to leave it out"));
        }

        // ------------------------------------------------------------------ which types carry sensitive data

        private static bool IsSensitive(ISymbol member) => member.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == SensitiveName);

        /// <summary>"User.PasswordHash" for the first [Sensitive] member reachable from the type (nested objects and collections), or null.</summary>
        private static string? SensitivePath(ITypeSymbol? type, ConcurrentDictionary<ITypeSymbol, string> cache)
        {
            if (type == null) return null;
            var path = cache.GetOrAdd(type, t => Find(t, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0) ?? string.Empty);
            return path.Length == 0 ? null : path;
        }

        private static string? Find(ITypeSymbol type, HashSet<ITypeSymbol> visited, int depth)
        {
            if (depth > 3 || type.SpecialType != SpecialType.None || !visited.Add(type)) return null;
            if (type is IArrayTypeSymbol array) return Find(array.ElementType, visited, depth);
            if (!(type is INamedTypeSymbol named) || named.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true && !named.IsGenericType)
                return null;
            if (named.IsGenericType && named.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true)
            {
                foreach (var argument in named.TypeArguments)
                    if (Find(argument, visited, depth) is string inner) return inner;
                return null;
            }
            for (var t = named; t != null && t.SpecialType == SpecialType.None; t = t.BaseType)
                foreach (var member in t.GetMembers())
                {
                    if (member.IsStatic || !(member is IPropertySymbol || member is IFieldSymbol) || member.IsImplicitlyDeclared) continue;
                    if (IsSensitive(member)) return named.Name + "." + member.Name;
                    var memberType = member is IPropertySymbol p ? p.Type : ((IFieldSymbol)member).Type;
                    if (Find(memberType, visited, depth + 1) is string nested) return nested;
                }
            return null;
        }

        private static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }
}
