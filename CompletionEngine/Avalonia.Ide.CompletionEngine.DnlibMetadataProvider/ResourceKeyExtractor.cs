using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Avalonia.Ide.CompletionEngine.DnlibMetadataProvider;

/// <summary>
/// Extracts resource keys defined by XAML resource dictionaries from compiled assemblies.
/// Avalonia compiles <c>&lt;SolidColorBrush x:Key="MyBrush"/&gt;</c> entries into
/// <c>ldstr "MyBrush"; ldftn &lt;deferred build&gt;; ...; ResourceDictionary::AddDeferred</c>
/// calls inside the populate/build methods. Walking the IL recovers the keys (and, when
/// determinable, the type of each resource) without needing the original XAML source.
/// </summary>
internal static class ResourceKeyExtractor
{
    public static List<(string Key, string? TypeFullName)> Extract(AssemblyDef assembly)
    {
        var result = new List<(string, string?)>();
        foreach (var module in assembly.Modules)
        {
            foreach (var type in module.Types)
                WalkType(type, result, assembly);
        }
        return result;
    }

    private static void WalkType(TypeDef type, List<(string, string?)> result, AssemblyDef assembly)
    {
        foreach (var method in type.Methods)
        {
            // Top level x:Class styles populate through !XamlIlPopulate; deferred resources
            // (ControlThemes and other resources) are built lazily in XamlClosure_N::Build_N methods;
            // AvaloniaResource-compiled files are built through CompiledAvaloniaXaml Build:/Populate:.
            if (method.HasBody
                && (method.Name == "!XamlIlPopulate"
                    || method.Name.StartsWith("Build_", StringComparison.Ordinal)
                    || method.Name.StartsWith("Build:", StringComparison.Ordinal)
                    || method.Name.StartsWith("Populate:", StringComparison.Ordinal)))
            {
                ExtractFromMethod(method, result, assembly);
            }
        }

        foreach (var nested in type.NestedTypes)
            WalkType(nested, result, assembly);
    }

    private static void ExtractFromMethod(MethodDef method, List<(string, string?)> result, AssemblyDef assembly)
    {
        var instructions = method.Body.Instructions;

        for (var i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];

            if (instr.OpCode != OpCodes.Call && instr.OpCode != OpCodes.Callvirt)
                continue;

            if (instr.Operand is not IMethod methodRef)
                continue;

            var declaringType = methodRef.DeclaringType?.FullName;
            // Compiled resource dictionaries are populated through ResourceDictionary::Add/AddDeferred,
            // and also through the generic IDictionary<object,object>::Add surfaced by get_Resources().
            var isDictionaryAdd = declaringType == "Avalonia.Controls.ResourceDictionary"
                || declaringType == "Avalonia.ResourceDictionary"
                || declaringType == "System.Collections.Generic.IDictionary`2<System.Object,System.Object>";
            if (!isDictionaryAdd)
                continue;

            var name = methodRef.Name.ToString();
            if (name is not ("Add" or "AddDeferred" or "set_Item"))
                continue;

            var keyIndex = FindKeyIndex(instructions, i, name == "AddDeferred");
            if (keyIndex == null || instructions[keyIndex.Value].Operand is not string key)
                continue;
            if (!IsPlausibleKey(key))
                continue;

            var typeName = name == "AddDeferred"
                ? GetDeferredValueType(instructions, keyIndex.Value, assembly)
                : GetInlineValueType(instructions, keyIndex.Value, i);

            result.Add((key, typeName));
        }
    }

    /// <summary>Filters out strings that are clearly not resource keys (uri/path style values).</summary>
    private static bool IsPlausibleKey(string key) =>
        key.Length > 0 && key[0] != '{' && !key.Contains("://") && !key.StartsWith("/");

    /// <summary>Finds the index of the key string feeding the dictionary add call at
    /// <paramref name="addIndex"/>. For <c>AddDeferred</c> the compiled pattern is
    /// <c>ldstr key; ldftn deferred; ...; AddDeferred</c>; for inline <c>Add</c>/<c>set_Item</c>
    /// it is <c>ldstr key; &lt;value build&gt;; Add</c> (the closest ldstr before the call).</summary>
    private static int? FindKeyIndex(IList<Instruction> instructions, int addIndex, bool deferred)
    {
        for (var j = addIndex - 1; j >= Math.Max(0, addIndex - 32); --j)
        {
            var p = instructions[j];
            if (p.OpCode == OpCodes.Ldstr && p.Operand is string)
            {
                // The key of a deferred resource is the ldstr immediately preceding the ldftn
                // of its build method (e.g. ldstr "key"; ldftn Build_1; ...; AddDeferred).
                if (j + 1 < instructions.Count && instructions[j + 1].OpCode == OpCodes.Ldftn)
                    return j;
                // Inline entry: the closest ldstr before the add call is the key.
                if (!deferred)
                    return j;
            }
            if (p.OpCode == OpCodes.Ldftn)
            {
                // The key of a deferred resource is the ldstr preceding the ldftn of its build
                // method, allowing a few neutral pushes (e.g. ldnull for the closure target) in
                // between (compiled pattern: ldstr key; ldnull; ldftn Build; ...; AddDeferred).
                for (var k = j - 1; k >= Math.Max(0, j - 4); --k)
                {
                    var q = instructions[k];
                    if (q.OpCode == OpCodes.Ldstr && q.Operand is string)
                        return k;
                    if (q.OpCode != OpCodes.Ldnull && q.OpCode != OpCodes.Nop)
                        break;
                }
                return null;
            }
            if (p.OpCode == OpCodes.Call || p.OpCode == OpCodes.Callvirt)
            {
                // Previous resource boundary reached.
                if (p.Operand is IMethod m && IsResourceDictionaryAdd(m))
                    break;
            }
        }
        return null;
    }

    private static bool IsResourceDictionaryAdd(IMethod m) =>
        m.Name.ToString() is ("Add" or "AddDeferred" or "set_Item")
        && (m.DeclaringType?.FullName == "Avalonia.Controls.ResourceDictionary"
            || m.DeclaringType?.FullName == "Avalonia.ResourceDictionary"
            || m.DeclaringType?.FullName == "System.Collections.Generic.IDictionary`2<System.Object,System.Object>");

    /// <summary>Determines the resource value type for a deferred entry by inspecting the
    /// <c>ldftn</c> build method that follows the key (allowing neutral pushes such as the
    /// <c>ldnull</c> closure target between the key and the <c>ldftn</c>).</summary>
    private static string? GetDeferredValueType(IList<Instruction> instructions, int keyIndex, AssemblyDef assembly)
    {
        for (var k = keyIndex + 1; k < Math.Min(instructions.Count, keyIndex + 5); k++)
        {
            var p = instructions[k];
            if (p.OpCode == OpCodes.Ldftn && p.Operand is IMethod buildRef)
            {
                var buildMethod = FindMethod(assembly, buildRef.DeclaringType?.FullName, buildRef.Name.ToString());
                if (buildMethod?.HasBody != true)
                    return null;
                return GetValueTypeFromBuildBody(buildMethod.Body.Instructions);
            }
            if (p.OpCode != OpCodes.Ldnull && p.OpCode != OpCodes.Nop)
                return null;
        }
        return null;
    }

    private static string? GetInlineValueType(IList<Instruction> instructions, int keyIndex, int addIndex) =>
        GetValueProducerType(instructions, keyIndex + 1, addIndex, stopAtRet: false);

    /// <summary>Scans the instructions between <paramref name="start"/> and <paramref name="end"/>
    /// for the resource value producer and returns the type of the outermost value: a reference
    /// type built with <c>newobj</c>, or a value type boxed to object with <c>box</c>. The last
    /// producer wins (deeper objects such as nested markup extensions are constructed first, and
    /// property values such as <c>Color</c> are consumed by property setters rather than reaching
    /// the dictionary add call).</summary>
    private static string? GetValueProducerType(IList<Instruction> instructions, int start, int end, bool stopAtRet)
    {
        string? last = null;
        for (var i = start; i < end && i < instructions.Count; i++)
        {
            var p = instructions[i];
            if (stopAtRet && p.OpCode == OpCodes.Ret)
                break;

            if (p.OpCode == OpCodes.Newobj && p.Operand is IMethod nm
                && IsResourceType(nm.DeclaringType?.FullName))
            {
                last = nm.DeclaringType?.FullName;
            }
            else if (p.OpCode == OpCodes.Box && p.Operand is ITypeDefOrRef bt)
            {
                last = bt.FullName;
            }
        }
        return last;
    }

    /// <summary>Extracts the resource value type from a deferred build method body. Falls back to
    /// <c>System.String</c> when the value is produced directly from a string literal.</summary>
    private static string? GetValueTypeFromBuildBody(IList<Instruction> instructions)
    {
        var typeName = GetValueProducerType(instructions, 0, instructions.Count, stopAtRet: true);
        if (typeName != null)
            return typeName;

        for (var i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode == OpCodes.Ret)
                break;
            if (instructions[i].OpCode == OpCodes.Ldstr)
                return "System.String";
        }
        return null;
    }

    private static bool IsResourceType(string? typeFullName) =>
        typeFullName != null
        && !typeFullName.Contains("XamlIl")
        && !typeFullName.EndsWith("Context", StringComparison.Ordinal)
        && !typeFullName.StartsWith("System.Func", StringComparison.Ordinal)
        && typeFullName != "System.Object"
        && !typeFullName.StartsWith("Avalonia.Markup.Xaml.MarkupExtensions", StringComparison.Ordinal);

    private static MethodDef? FindMethod(AssemblyDef assembly, string? typeFullName, string methodName)
    {
        if (typeFullName == null)
            return null;
        foreach (var module in assembly.Modules)
        {
            foreach (var type in module.GetTypes())
            {
                if (type.FullName != typeFullName)
                    continue;
                foreach (var method in type.Methods)
                    if (method.Name == methodName)
                        return method;
            }
        }
        return null;
    }
}
