using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Compares the actual compiled member signatures with each installed Revit API.
// Reflection/string-based calls and native execution are outside this check.
internal sealed class Signatures : ISignatureTypeProvider<string, object>
{
    internal static string Name(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeReference)
        {
            var type = reader.GetTypeReference((TypeReferenceHandle)handle);
            var name = reader.GetString(type.Name);
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? Name(reader, type.ResolutionScope) + "+" + name
                : reader.GetString(type.Namespace) + "." + name;
        }
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
            return type.GetDeclaringType().IsNil
                ? reader.GetString(type.Namespace) + "." + reader.GetString(type.Name)
                : Name(reader, type.GetDeclaringType()) + "+" + reader.GetString(type.Name);
        }
        return handle.Kind == HandleKind.TypeSpecification
            ? reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(new Signatures(), null) : "?";
    }
    public string GetArrayType(string type, ArrayShape shape) => type + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
    public string GetByReferenceType(string type) => type + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fn:" + Key(signature);
    public string GetGenericInstantiation(string type, ImmutableArray<string> args) => type + "<" + string.Join(",", args) + ">";
    public string GetGenericMethodParameter(object context, int index) => "!!" + index;
    public string GetGenericTypeParameter(object context, int index) => "!" + index;
    public string GetModifiedType(string modifier, string type, bool required) => type;
    public string GetPinnedType(string type) => type;
    public string GetPointerType(string type) => type + "*";
    public string GetPrimitiveType(PrimitiveTypeCode code) => "System." + code;
    public string GetSZArrayType(string type) => type + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Name(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Name(reader, handle);
    public string GetTypeFromSpecification(MetadataReader reader, object context, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    internal static string Key(MethodSignature<string> signature) => signature.ReturnType + "(" + string.Join(",", signature.ParameterTypes) + ")`" + signature.GenericParameterCount;
}

internal sealed class Api : IDisposable
{
    private readonly FileStream _stream;
    private readonly PEReader _pe;
    internal readonly MetadataReader Reader;
    internal readonly Dictionary<string, TypeDefinitionHandle> Types;
    internal Api(string path)
    {
        _stream = File.OpenRead(path); _pe = new PEReader(_stream); Reader = _pe.GetMetadataReader();
        Types = Reader.TypeDefinitions.ToDictionary(h => Signatures.Name(Reader, h));
    }
    internal bool HasMember(string type, string name, string signature, bool field, int depth = 0)
    {
        if (depth > 30 || !Types.TryGetValue(type, out var handle)) return false;
        var definition = Reader.GetTypeDefinition(handle);
        if (field)
        {
            if (definition.GetFields().Any(h => { var f = Reader.GetFieldDefinition(h); return Reader.GetString(f.Name) == name && f.DecodeSignature(new Signatures(), null) == signature; })) return true;
        }
        else if (definition.GetMethods().Any(h => { var m = Reader.GetMethodDefinition(h); return Reader.GetString(m.Name) == name && Signatures.Key(m.DecodeSignature(new Signatures(), null)) == signature; })) return true;
        if (name == ".ctor" || definition.BaseType.IsNil) return false;
        return HasMember(Signatures.Name(Reader, definition.BaseType), name, signature, field, depth + 1);
    }
    public void Dispose() { _pe.Dispose(); _stream.Dispose(); }
}

internal static class Program
{
    private static string AssemblyOf(MetadataReader reader, TypeReferenceHandle handle)
    {
        var scope = reader.GetTypeReference(handle).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference) scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        return scope.Kind == HandleKind.AssemblyReference ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) : "";
    }
    private static int Main(string[] args)
    {
        using var stream = File.OpenRead(args[0]); using var pe = new PEReader(stream); var reader = pe.GetMetadataReader();
        var apiNames = new HashSet<string> { "RevitAPI", "RevitAPIUI", "AdWindows", "UIFramework" };
        var apis = apiNames.Where(n => File.Exists(Path.Combine(args[1], n + ".dll"))).ToDictionary(n => n, n => new Api(Path.Combine(args[1], n + ".dll")));
        var missing = new Dictionary<int, object>(); int checkedMembers = 0;
        var missingTypes = reader.TypeReferences.Where(h => apis.ContainsKey(AssemblyOf(reader, h)))
            .Where(h => !apis[AssemblyOf(reader, h)].Types.ContainsKey(Signatures.Name(reader, h)))
            .Select(h => new { assembly = AssemblyOf(reader, h), type = Signatures.Name(reader, h) }).ToArray();
        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var typeHandle = (TypeReferenceHandle)member.Parent; var assembly = AssemblyOf(reader, typeHandle);
            if (!apis.TryGetValue(assembly, out var api)) continue;
            checkedMembers++;
            var type = Signatures.Name(reader, typeHandle); var name = reader.GetString(member.Name);
            bool field = member.GetKind() == MemberReferenceKind.Field;
            string signature = field ? member.DecodeFieldSignature(new Signatures(), null) : Signatures.Key(member.DecodeMethodSignature(new Signatures(), null));
            if (!api.HasMember(type, name, signature, field))
                missing[MetadataTokens.GetToken(handle)] = new { assembly, type, member = name, signature };
        }
        var callers = new List<object>();
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)).ToDictionary(op => unchecked((ushort)op.Value));
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle); if (method.RelativeVirtualAddress == 0) continue;
            var bytes = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
            for (int i = 0; i < bytes.Length;)
            {
                ushort code = bytes[i++]; if (code == 0xfe) code = (ushort)(0xfe00 | bytes[i++]); var op = opcodes[code];
                if (op.OperandType == OperandType.InlineMethod || op.OperandType == OperandType.InlineField || op.OperandType == OperandType.InlineTok)
                {
                    int token = BitConverter.ToInt32(bytes, i);
                    if (missing.TryGetValue(token, out var failure)) callers.Add(new { caller = Signatures.Name(reader, method.GetDeclaringType()) + "::" + reader.GetString(method.Name), failure });
                }
                i += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i),
                    _ => 4
                };
            }
        }
        var db = apis["RevitAPI"];
        bool ShapeEditor(string type) => db.HasMember(type, "GetSlabShapeEditor", "Autodesk.Revit.DB.SlabShapeEditor()`0", false)
            || db.HasMember(type, "get_SlabShapeEditor", "Autodesk.Revit.DB.SlabShapeEditor()`0", false);
        bool Point(string name) => db.HasMember("Autodesk.Revit.DB.SlabShapeEditor", name, "Autodesk.Revit.DB.SlabShapeVertex(Autodesk.Revit.DB.XYZ)`0", false);
        bool Split(string name) => db.HasMember("Autodesk.Revit.DB.SlabShapeEditor", name,
            "Autodesk.Revit.DB.SlabShapeCreaseArray(Autodesk.Revit.DB.SlabShapeVertex,Autodesk.Revit.DB.SlabShapeVertex)`0", false)
            || db.HasMember("Autodesk.Revit.DB.SlabShapeEditor", name,
                "System.Collections.Generic.IList`1<Autodesk.Revit.DB.SlabShapeCrease>(Autodesk.Revit.DB.SlabShapeVertex,Autodesk.Revit.DB.SlabShapeVertex)`0", false);
        var adapters = new Dictionary<string, bool>
        {
            ["Floor shape editor"] = ShapeEditor("Autodesk.Revit.DB.Floor"),
            ["Roof shape editor"] = ShapeEditor("Autodesk.Revit.DB.RoofBase"),
            ["Add/Draw shape point"] = Point("AddPoint") || Point("DrawPoint"),
            ["Add/Draw split line"] = Split("AddSplitLine") || Split("DrawSplitLine"),
            ["ElementId numeric constructor"] = db.HasMember("Autodesk.Revit.DB.ElementId", ".ctor", "System.Void(System.Int64)`0", false)
                || db.HasMember("Autodesk.Revit.DB.ElementId", ".ctor", "System.Void(System.Int32)`0", false)
        };
        if (adapters.Any(a => !a.Value))
            foreach (var methodHandle in db.Reader.GetTypeDefinition(db.Types["Autodesk.Revit.DB.SlabShapeEditor"]).GetMethods())
            {
                var method = db.Reader.GetMethodDefinition(methodHandle);
                string name = db.Reader.GetString(method.Name);
                if (name.Contains("Point") || name.Contains("SplitLine")) Console.WriteLine(name + ": " + Signatures.Key(method.DecodeSignature(new Signatures(), null)));
            }
        var report = new { plugin = Path.GetFullPath(args[0]), host = args[1], checkedMembers, missingTypes, missingMembers = missing.Values, callers, runtimeAdapters = adapters };
        File.WriteAllText(args[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{checkedMembers} API members checked; {missingTypes.Length} missing types; {missing.Count} unresolved member signatures; {adapters.Count(a => !a.Value)} failed runtime adapters. Report: {args[2]}");
        foreach (var api in apis.Values) api.Dispose();
        return missing.Count == 0 && missingTypes.Length == 0 && adapters.All(a => a.Value) ? 0 : 1;
    }
}
