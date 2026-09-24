using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // ---- System.Runtime.InteropServices.Marshal / Interop+Kernel32 (環境取得起動面) ----

    /// <summary>VM 代替の last system error (Marshal.SetLastSystemError / GetLastSystemError 面)。
    /// 実 CLR の per-thread TLS スロットの代わりに VM ごとの共有状態 (VmSharedState) で模倣する。
    /// CultureInfo.InvariantCulture 初期化 (GlobalizationMode → GetEnvironmentVariableCore)
    /// が GetLastSystemError() &lt; ERROR_ENVVAR_NOT_FOUND(203) で成功判定に使う。
    /// (タスク 2 hardening: これらの面は TrustedCoreLib domain に属し、trusted CoreLib IL
    /// からの呼出のみ照合される。呼出元は caller loader 基準で判定する。)</summary>

    /// <summary>VM ごとの仮想環境変数ストアは VmSharedState.VirtualEnvironment を使う
    /// (static 共有にしない。VM ごとに分離する)。
    /// Kernel32.GetEnvironmentVariable 面は host の Environment.GetEnvironmentVariable を
    /// 直接呼ばず、この VM ごとの仮想環境のみを参照する (host 環境の読み替えを遮断し、
    /// trusted CoreLib domain 呼出でのみ到達する特権面に)。
    /// 既定は空 (GlobalizationMode::get_Invariant を true 固定にする呼出経路が
    /// DOTNET_SYSTEM_GLOBALIZATION_INVARIANT の実環境読み取りに依存しない)。</summary>

    /// <summary>本家 CultureInfo::.cctor → CultureData.get_Invariant → GlobalizationMode の
    /// IL は AppContextConfigHelper → Environment.GetEnvironmentVariableCore を辿り、その
    /// Kernel32.GetEnvironmentVariable(name, buffer, size) が P/Invoke 面。
    /// タスク 2 hardening: この面は trusted CoreLib (TrustedCoreLib domain) からの呼出のみ
    /// 到達する特権面 (BindingDomain.TrustedCoreLib) とし、VM ごとの仮想環境変数ストアを
    /// 読む (host Environment.GetEnvironmentVariable への直接委譲を廃止)。
    /// バッファへは Win32 規約 (戻り = 終端 null 除くコピー文字数 / 不足時は終端含む
    /// 必要文字数を返すのみ、未定義なら 0 + lastError = 203) で書き込む。
    /// Marshal の 4 面は IL 実体が下請け P/Invoke shim 呼びのみのため
    /// internal-call リーフで VM lastError に代替する (trusted CoreLib 限定。
    /// 状態は ctx.Shared の VM インスタンス状態)。</summary>
    private static void RegisterEnvironmentAndMarshal(IntrinsicRegistry r) {
        const string MarshalType = "System.Runtime.InteropServices.Marshal";
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "SetLastSystemError", "System.Int32"),
            static (ctx, a) => {
                ctx.Shared.LastSystemError = a[0].AsInt32;
                return null;
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "GetLastSystemError"),
            static (ctx, _) => StackSlot.OfInt32(ctx.Shared.LastSystemError),
            BindingOrigin.InternalCall);
        // SystemError/PInvokeError は実 CLR でも同一 TLS スロットの alias 面
        // (SetLastSystemError/GetLastSystemError の IL 実体が呼ぶ下請け)
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "SetLastPInvokeError", "System.Int32"),
            static (ctx, a) => {
                ctx.Shared.LastSystemError = a[0].AsInt32;
                return null;
            },
            BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "GetLastPInvokeError"),
            static (ctx, _) => StackSlot.OfInt32(ctx.Shared.LastSystemError),
            BindingOrigin.InternalCall);
        // Kernel32.GetEnvironmentVariable は trusted CoreLib (GlobalizationMode 経路) からの
        // 起動面としてのみ有効な特権面。BindingDomain.TrustedCoreLib で鍵化し、
        // 呼出元 loader 基準 (CallEngine.CallerDomainOf) で trusted CoreLib IL からの
        // 呼出のみ照合する
        r.RegisterBinding(BindingKey.TrustedStatic("Interop+Kernel32", "GetEnvironmentVariable",
                "System.String", "System.Char&", "System.UInt32"),
            static (ctx, a) => GetEnvironmentVariableImpl(ctx, a),
            BindingOrigin.PInvokeReplacement);
        // Interop+BCrypt.BCryptGenRandom (Random / Marvin ハッシュ種等の乱数源 P/Invoke)。
        // 任意の native import は実行せず、ホスト暗号乱数 API に限定して委譲する。
        // P/Invoke 呼出元は TrustedCoreLib domain に限定し、ゲストからの直接呼出は拒否する。
        r.RegisterBinding(BindingKey.TrustedStatic("Interop+BCrypt", "BCryptGenRandom",
                "System.IntPtr", "System.Byte*", "System.Int32", "System.Int32"),
            static (ctx, a) => {
                var count = a[2].AsInt32;
                if (count < 0)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                if (count == 0)
                    return StackSlot.OfInt32(0);
                if (a[1].ObjectValue is VmNativePointer native) {
                    if (native.ByteOffset < 0 || (long)native.ByteOffset + count > native.Bytes.Length)
                        throw new InvalidOperationException(
                            $"BCryptGenRandom がブロック外を参照します (offset={native.ByteOffset}, {count} バイト)。");
                    ctx.Shared.FillRandom(native.Bytes.AsSpan(native.ByteOffset, count));
                    return StackSlot.OfInt32(0);
                }
                // マネージポインタ (Marvin seed の stackalloc ulong)。既知の Int64 スロット
                // のみ扱い、他の managed バッファ表現は誤書込みを避けて fail-closed にする。
                if (a[1].Kind == StackKind.ByRef && a[1].ObjectValue is VmByRef byRef) {
                    if (byRef.Container.Length == 0)
                        throw new UnhandledGuestException("System.NullReferenceException", null);
                    var slots = (int)(((long)count + 7) / 8);
                    if (byRef.Index < 0 || slots > byRef.Container.Length - byRef.Index)
                        throw new InvalidOperationException(
                            $"BCryptGenRandom がスロット列の範囲外を参照します (index={byRef.Index}, {count} バイト)。");
                    var remaining = count;
                    var index = byRef.Index;
                    Span<byte> bytes = stackalloc byte[8];
                    while (remaining > 0) {
                        var slot = byRef.Container[index];
                        if (slot.Kind != StackKind.Int64)
                            throw new InvalidOperationException(
                                $"BCryptGenRandom のマネージバッファ要素が Int64 スロットではありません ({slot.Kind})。");
                        BinaryPrimitives.WriteInt64LittleEndian(bytes, slot.Int64Value);
                        var writeCount = Math.Min(8, remaining);
                        ctx.Shared.FillRandom(bytes[..writeCount]);
                        byRef.Container[index] = StackSlot.OfInt64(BinaryPrimitives.ReadInt64LittleEndian(bytes));
                        remaining -= writeCount;
                        index++;
                    }
                    return StackSlot.OfInt32(0);
                }
                throw new InvalidOperationException(
                    $"BCryptGenRandom のバッファがバイト実体ではありません ({a[1].Kind})。");
            },
            BindingOrigin.PInvokeReplacement);
        // GlobalizationMode+Settings::get_Invariant を true 固定にする (VM 規約: culture は
        // 不変カルチャ固定)。本家の DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 起動と同一意味論
        // で、本家 IL の分岐は CultureInfo::GetUserDefaultLocaleName 等の OS locale 取得
        // (Kernel32 P/Invoke) を通らない managed 経路に落ちる。OS locale 面自体は
        // culture 機構スコープ外のため代替実装を持たない (fail-closed を維持)
        r.RegisterBinding(BindingKey.TrustedStatic("System.Globalization.GlobalizationMode+Settings", "get_Invariant"),
            static (_, _) => StackSlot.OfInt32(1), BindingOrigin.InternalCall);
        // Type::GetTypeFromHandle: 本家 IL 本体は RuntimeType.GetTypeFromHandle (runtime
        // intrinsic = IL なし) の呼び出しを含むため ② IL 実行に落とせない (ldfld
        // RuntimeTypeHandle::m_type が VM 表現境界外)。実 CLR もこの面は IL を実行しない
        // (監査表 (c) runtime-representation)。VmTypeHandle → VmRuntimeObject ファサード変換
        // として同等面を提供する
        r.RegisterBinding(BindingKey.Static("System.Type", "GetTypeFromHandle", "System.RuntimeTypeHandle"),
            static (ctx, a) => a[0].ObjectValue is VmTypeHandle handle
                ? DefaultIntrinsics.MakeRuntimeObject(ctx, handle.Target)
                : throw new InvalidOperationException("GetTypeFromHandle の引数が RuntimeTypeHandle ではありません。"),
            BindingOrigin.InternalCall);
    }


    private static StackSlot GetEnvironmentVariableImpl(IntrinsicContext ctx, StackSlot[] a) {
        var name = (a[0].ObjectValue as VmString)?.Value;
        var (native, slotRef) = ResolvePointerBase(a[1], "Interop+Kernel32.GetEnvironmentVariable");
        if (name is null || (native is null && slotRef is null))
            throw new UnhandledGuestException("System.NullReferenceException", null);
        // host Environment への直接委譲を廃止: VM ごとの仮想環境変数ストア (ctx.Shared) を読む
        var value = ctx.Shared.VirtualEnvironment.TryGetValue(name, out var found) ? found : null;
        ctx.Shared.LastSystemError = value is null ? 203 /* ERROR_ENVVAR_NOT_FOUND */ : 0;
        if (string.IsNullOrEmpty(value))
            return StackSlot.OfInt32(0);
        // バッファ不足 (nSize <= 文字数): 書き込みは行わず終端含む必要文字数を返す
        // (lpBuffer 内容は Win32 規約上不定 — 呼び出し側の GetEnvironmentVariableCore は
        // この戻りで容量を確保して再試行する)
        if (value.Length + 1 > a[2].AsInt32)
            return StackSlot.OfInt32(value.Length + 1);
        if (native is not null) {
            var offset = native.ByteOffset;
            if (offset < 0 || (long)offset + value.Length * 2 + 2 > native.Bytes.Length)
                throw new InvalidOperationException(
                    $"Kernel32.GetEnvironmentVariable のバッファがブロック外を参照します (offset={offset}, 必要 {value.Length + 1} 文字, ブロック {native.Bytes.Length} バイト)。");
            var bytes = native.Bytes;
            for (var i = 0; i < value.Length; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + i * 2, 2), (short)value[i]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + value.Length * 2, 2), (short)'\0');
        } else {
            for (var i = 0; i < value.Length; i++)
                slotRef!.Container[slotRef.Index + i] = StackSlot.OfInt32(value[i]);
            slotRef!.Container[slotRef.Index + value.Length] = StackSlot.OfInt32('\0');
        }
        return StackSlot.OfInt32(value.Length);
    }
}
