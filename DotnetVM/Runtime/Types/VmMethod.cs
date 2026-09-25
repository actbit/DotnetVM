using DotnetVM.IL;
using DotnetVM.Metadata;
using DotnetVM.Metadata.Signatures;

namespace DotnetVM.Runtime.Types;

/// <summary>VM 内のメソッド (MethodDef ベース)。</summary>
public sealed class VmMethod {
    public required VmType DeclaringType { get; init; }
    public required int MethodDefRid { get; init; }
    public required string Name { get; init; }
    public required MethodSignature Signature { get; init; }
    public required uint Flags { get; init; }
    public required uint ImplFlags { get; init; }
    /// <summary>メソッド本体 (abstract / pinvoke は null)。</summary>
    public required MethodBodyBlock? Body { get; init; }
    /// <summary>このメソッドを定義したアセンブリのローダ (多アセンブリ実行で token 解決先を決める)。</summary>
    public TypeLoader? Loader { get; internal set; }
    internal IReadOnlyDictionary<uint, string>? DynamicStrings { get; init; }
    internal IReadOnlyDictionary<uint, object>? DynamicTokens { get; init; }

    private string? _slotKey;
    private DecodedInstruction[]? _decodedIl;

    /// <summary>ディスパッチ用スロットキー (名前 + パラメータ型の正規化形、成功時のみキャッシュ)。
    /// 署名解決が失敗した場合は都度 null を返す (呼出側はパラメータ数照合にフォールバックする)。</summary>
    internal string? SlotKey {
        get {
            if (_slotKey is null)
                _slotKey = Loader?.TryResolveSlotParams(Signature.ParamTypes) is { } parameters
                    ? VmSlotKeys.Of(Name, parameters) : null;
            return _slotKey;
        }
    }

    public uint Token => global::DotnetVM.Metadata.Token.From(TableKind.MethodDef, MethodDefRid).Value;

    public bool IsStatic => (Flags & 0x0010) != 0;
    public bool IsVirtual => (Flags & 0x0040) != 0;
    public bool IsAbstract => (Flags & 0x0400) != 0;
    public bool IsPublic => (Flags & 0x0006) == 0x0006;
    public bool IsConstructor => Name == ".ctor" || Name == ".cctor";

    /// <summary>IL を事前デコードする (Body が無い場合は例外)。</summary>
    public DecodedInstruction[] DecodeIl() {
        // Method bodies are immutable after loading.  Decoding once is important
        // for both the interpreter and JIT: a hot method otherwise paid for a
        // complete IL decode on every invocation (and the frame rebuilt its
        // offset map as well).
        if (Volatile.Read(ref _decodedIl) is { } cached)
            return cached;

        var body = Body ?? throw new InvalidOperationException(
            $"メソッド {DeclaringType.FullName}::{Name} には本体がありません。");
        var decoded = IlDecoder.Decode(body.IlCode);
        return Interlocked.CompareExchange(ref _decodedIl, decoded, null) ?? decoded;
    }

    public override string ToString() => $"{DeclaringType.FullName}::{Name}";
}

/// <summary>VM 内のフィールド (FieldDef ベース)。</summary>
public sealed class VmField {
    public required VmType DeclaringType { get; init; }
    public required int FieldRid { get; init; }
    public required string Name { get; init; }
    public required FieldSignature Signature { get; init; }
    public required uint Flags { get; init; }

    public bool IsStatic => (Flags & 0x0010) != 0;
    public bool IsPublic => (Flags & 0x0006) == 0x0006;
    public bool IsLiteral => (Flags & 0x0040) != 0;   // enum 定数等 (Constant テーブルで値を持つ)
    public bool IsInitOnly => (Flags & 0x0020) != 0;  // readonly

    /// <summary>フィールドの型。TypeLoader.ResolveFieldType で遅延設定される。</summary>
    public VmType? FieldType { get; internal set; }

    public override string ToString() => $"{DeclaringType.FullName}::{Name}";
}
