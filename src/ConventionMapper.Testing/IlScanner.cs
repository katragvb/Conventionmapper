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
            foreach (var type in LoadableTypes(assembly))
            {
                if (excluded != null && IsSelfOrNested(type, excluded)) continue;
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                IEnumerable<MethodBase> methods = type.GetMethods(all);
                methods = methods.Concat(type.GetConstructors(all));
                foreach (var method in methods) Scan(method, result);
            }
            return result;
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

        private static void Scan(MethodBase method, HashSet<string> result)
        {
            byte[]? il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch (Exception)
            {
                return;
            }
            if (il == null) return;

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
                    if (i >= il.Length || !TwoByte.TryGetValue(il[i++], out code)) return;
                }
                else if (!OneByte.TryGetValue(b, out code))
                {
                    return; // unknown opcode: stop rather than misread the rest
                }

                switch (code.OperandType)
                {
                    case OperandType.InlineMethod:
                    case OperandType.InlineTok:
                    {
                        var token = BitConverter.ToInt32(il, i);
                        try
                        {
                            if (module.ResolveMember(token, typeArgs, methodArgs) is MethodBase target) result.Add(MapperAssert.Key(target));
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
