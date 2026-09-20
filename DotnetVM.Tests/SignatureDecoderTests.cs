using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// 独自署名デコーダを System.Reflection.Metadata の ISignatureTypeProvider (オラクル) と照合する。
/// </summary>
public class SignatureDecoderTests {
    private const string Source = """
        using System;
        using System.Collections.Generic;
        namespace Sig {
            public class Holder<T> where T : class {
                public T? Value;
                public int Count;
                public static T Echo(T value) => value;
                public Dictionary<int, List<string>> Build() => new();
                public ref int ByRef(out long target) { target = 0; return ref Count; }
                public int[,] MultiDim(int[,] grid) => grid;
                public U Convert<T2, U>(T2 input) where U : class => (U)(object)input!;
            }
            public enum Flavor { Sour, Sweet }
            public struct Point { public int X, Y; }
        }
        """;

    private static readonly byte[] Pe = TestAssemblyCompiler.CompileToBytes(Source);

    // SRM の型名プロバイダ (オラクル)
    private sealed class TypeNameProvider : ISignatureTypeProvider<string, object?> {
        public string GetPrimitiveType(PrimitiveTypeCode code) => $"p:{code}";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) {
            var td = reader.GetTypeDefinition(handle);
            return $"def:{reader.GetString(td.Name)}";
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) {
            var tr = reader.GetTypeReference(handle);
            return $"ref:{reader.GetString(tr.Name)}";
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{shape.Rank}]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(",", typeArguments)}>";
        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetPinnedType(string elementType) => elementType;
    }

    private static (MetadataReader Reader, PEReader PeReader) Open() {
        var peReader = new PEReader(new MemoryStream(Pe));
        return (peReader.GetMetadataReader(), peReader);
    }

    [Fact]
    public void MethodSignatures_MatchOracle() {
        var image = AssemblyImage.Parse(Pe);
        var (reader, peReader) = Open();
        var provider = new TypeNameProvider();

        var methodCount = image.Tables.GetRowCount(TableKind.MethodDef);
        for (var rid = 1; rid <= methodCount; rid++) {
            var vmSig = SignatureDecoder.DecodeMethodSignature(image.GetMethodSignature(rid).ToArray());

            var mh = MetadataTokens.MethodDefinitionHandle(rid);
            var md = reader.GetMethodDefinition(mh);
            var oracleSig = md.DecodeSignature(provider, null);

            // パラメータ数
            Assert.Equal(oracleSig.ParameterTypes.Length, vmSig.ParamTypes.Length);
            // ジェネリックパラメータ数
            Assert.Equal(oracleSig.GenericParameterCount, vmSig.GenericParamCount);
            // 戻り値型
            Assert.Equal(oracleSig.ReturnType, ToOracleString(vmSig.ReturnType));
            // パラメータ型
            for (var i = 0; i < vmSig.ParamTypes.Length; i++)
                Assert.Equal(oracleSig.ParameterTypes[i], ToOracleString(vmSig.ParamTypes[i]));
        }
    }

    [Fact]
    public void FieldSignatures_MatchOracle() {
        var image = AssemblyImage.Parse(Pe);
        var (reader, peReader) = Open();
        var provider = new TypeNameProvider();

        var fieldCount = image.Tables.GetRowCount(TableKind.Field);
        for (var rid = 1; rid <= fieldCount; rid++) {
            var fieldBlob = image.GetBlob(image.Tables.GetRowIndex(TableKind.Field, rid, 2));
            var vmSig = SignatureDecoder.DecodeFieldSignature(fieldBlob.ToArray());

            var fh = MetadataTokens.FieldDefinitionHandle(rid);
            var fd = reader.GetFieldDefinition(fh);
            var oracleType = fd.DecodeSignature(provider, null);

            Assert.Equal(oracleType, ToOracleString(vmSig.FieldType));
        }
    }

    [Fact]
    public void TypeSpecs_MatchOracle() {
        var image = AssemblyImage.Parse(Pe);
        var (reader, peReader) = Open();
        var provider = new TypeNameProvider();

        var specCount = image.Tables.GetRowCount(TableKind.TypeSpec);
        Assert.True(specCount > 0, "テストアセンブリに TypeSpec が必要です。");
        for (var rid = 1; rid <= specCount; rid++) {
            var specBlob = image.GetBlob(image.Tables.GetRowIndex(TableKind.TypeSpec, rid, 0));
            var vmSig = SignatureDecoder.DecodeTypeSpecSignature(specBlob.ToArray()).Type;

            var sh = MetadataTokens.TypeSpecificationHandle(rid);
            var oracleType = reader.GetTypeSpecification(sh).DecodeSignature(provider, null);

            Assert.Equal(oracleType, ToOracleString(vmSig));
        }
    }

    /// <summary>独自 SigType をオラクルの文字列表記に変換する。</summary>
    private static string ToOracleString(SigType type) => type.Kind switch {
        SigKind.Boolean => "p:Boolean",
        SigKind.Char => "p:Char",
        SigKind.I1 => "p:SByte",
        SigKind.U1 => "p:Byte",
        SigKind.I2 => "p:Int16",
        SigKind.U2 => "p:UInt16",
        SigKind.I4 => "p:Int32",
        SigKind.U4 => "p:UInt32",
        SigKind.I8 => "p:Int64",
        SigKind.U8 => "p:UInt64",
        SigKind.R4 => "p:Single",
        SigKind.R8 => "p:Double",
        SigKind.String => "p:String",
        SigKind.Object => "p:Object",
        SigKind.Void => "p:Void",
        SigKind.I => "p:IntPtr",
        SigKind.U => "p:UIntPtr",
        SigKind.TypedByRef => "p:TypedReference",
        SigKind.TypeToken when (type.Token >> 24) == 0x01 =>
            "ref:" + TokenName(type.Token),
        SigKind.TypeToken when (type.Token >> 24) == 0x02 =>
            "def:" + TokenName(type.Token),
        SigKind.TypeToken => $"token(0x{type.Token:X8})",
        SigKind.GenericVar => $"!{type.VarNumber}",
        SigKind.GenericMethodVar => $"!!{type.VarNumber}",
        SigKind.ByRef => ToOracleString(type.Inner!) + "&",
        SigKind.Pointer => ToOracleString(type.Inner!) + "*",
        SigKind.SzArray => ToOracleString(type.Inner!) + "[]",
        SigKind.Array => $"{ToOracleString(type.Inner!)}[{type.Rank}]",
        SigKind.GenericInst when type.Args is not null =>
            $"{ToOracleString(new SigType(SigKind.TypeToken, type.Token))}<{string.Join(",", type.Args.Select(ToOracleString))}>",
        _ => throw new InvalidOperationException($"未対応の SigKind: {type.Kind}"),
    };

    private static string TokenName(uint token) {
        var (reader, _) = Open();
        var table = (int)(token >> 24);
        var rid = (int)(token & 0xFFFFFF);
        return table switch {
            0x01 => reader.GetString(reader.GetTypeReference(MetadataTokens.TypeReferenceHandle(rid)).Name),
            0x02 => reader.GetString(reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(rid)).Name),
            _ => throw new InvalidOperationException(),
        };
    }
}
