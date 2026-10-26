using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ConventionMapper.Testing
{
    /// <summary>Finds every method referenced from the IL of an assembly (calls, method groups and expression trees).</summary>
    internal static class IlScanner
    {
        private static readonly Dictionary<short, OpCode> OneByte = new Dictionary<short, OpCode>();
        private static readonly Dictionary<short, OpCode> TwoByte = new Dictionary<short, OpCode>();

        static IlScanner()
        {
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!(field.GetValue(null) is OpCode code)) continue;
                if (code.Size == 1) OneByte[code.Value] = code;
                else TwoByte[(short)(code.Value & 0xFF)] = code;
            }
        }

        /// <summary>Keys of the methods referenced by code in <paramref name="assembly"/>, except code inside <paramref name="excluded"/> (and its nested types).</summary>
        public static HashSet<string> CalledMethods(Assembly assembly, Type? excluded = null)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in Methods(assembly, excluded))
                foreach (var reference in References(method))
                    result.Add(MapperAssert.Key(reference.Target));
            return result;
        }

        /// <summary>
        /// Keys of the methods that a MapperAssert.WritesExactly / UpdatesExactly check runs: method groups passed to it and
        /// methods called by lambdas created in a method that calls it.
        /// </summary>
        public static HashSet<string> VerifiedMethods(Assembly assembly, Type? excluded = null)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in Methods(assembly, excluded))
            {
                var references = References(method);
                if (!references.Any(r => r.Target.DeclaringType == typeof(MapperAssert) &&
                                         (r.Target.Name == nameof(MapperAssert.WritesExactly) || r.Target.Name == nameof(MapperAssert.UpdatesExactly))))
                    continue;
                foreach (var delegateTarget in references.Where(r => r.IsMethodPointer).Select(r => r.Target))
                {
                    result.Add(MapperAssert.Key(delegateTarget)); // a method group: WritesExactly(Mappings.ToDto, ...)
                    foreach (var call in References(delegateTarget)) result.Add(MapperAssert.Key(call.Target)); // a lambda's body
                }
            }
            return result;
        }

        private static IEnumerable<MethodBase> Methods(Assembly assembly, Type? excluded)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var type in LoadableTypes(assembly))
            {
                if (excluded != null && IsSelfOrNested(type, excluded)) continue;
                foreach (var method in type.GetMethods(all)) yield return method;
                foreach (var ctor in type.GetConstructors(all)) yield return ctor;
            }
        }

        internal readonly struct Reference
        {
            public Reference(MethodBase target, bool isMethodPointer)
            {
                Target = target;
                IsMethodPointer = isMethodPointer;
            }

            public MethodBase Target { get; }

            /// <summary>ldftn / ldvirtftn: the method becomes a delegate (a lambda or a method group).</summary>
            public bool IsMethodPointer { get; }
        }

        private static bool IsSelfOrNested(Type type, Type container)
        {
            for (var t = type; t != null; t = t.DeclaringType)
                if (t == container || (container.IsGenericTypeDefinition && t.IsGenericType && t.GetGenericTypeDefinition() == container)) return true;
            return false;
        }

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null)!;
            }
        }

        private static List<Reference> References(MethodBase method)
        {
            var result = new List<Reference>();
            byte[]? il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch (Exception)
            {
                return result;
            }
            if (il == null) return result;

            var module = method.Module;
            var typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            var i = 0;
            while (i < il.Length)
            {
                OpCode code;
                var b = il[i++];
                if (b == 0xFE)
                {
                    if (i >= il.Length || !TwoByte.TryGetValue(il[i++], out code)) return result;
                }
                else if (!OneByte.TryGetValue(b, out code))
                {
                    return result; // unknown opcode: stop rather than misread the rest
                }

                switch (code.OperandType)
                {
                    case OperandType.InlineMethod:
                    case OperandType.InlineTok:
                    {
                        var token = BitConverter.ToInt32(il, i);
                        try
                        {
                            if (module.ResolveMember(token, typeArgs, methodArgs) is MethodBase target)
                                result.Add(new Reference(target, code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn));
                        }
                        catch (Exception)
                        {
                            // tokens of types and fields, or members that cannot be resolved here
                        }
                        i += 4;
                        break;
                    }
                    case OperandType.InlineSwitch:
                        i += 4 + 4 * BitConverter.ToInt32(il, i);
                        break;
                    default:
                        i += OperandSize(code.OperandType);
                        break;
                }
            }
            return result;
        }

        private static int OperandSize(OperandType type)
        {
            switch (type)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                default:
                    return 4;
            }
        }
    }
}
