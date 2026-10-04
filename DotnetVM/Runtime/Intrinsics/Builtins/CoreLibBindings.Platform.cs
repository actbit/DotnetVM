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
    /// Kernel32.GetEnvironmentVariableW の実 DllImport は host の Environment.GetEnvironmentVariable を
    /// 直接呼ばず、この VM ごとの仮想環境のみを参照する (host 環境の読み替えを遮断し、
    /// trusted CoreLib domain 呼出でのみ到達する特権面に)。
    /// 既定は空 (GlobalizationMode::get_Invariant を true 固定にする呼出経路が
    /// DOTNET_SYSTEM_GLOBALIZATION_INVARIANT の実環境読み取りに依存しない)。</summary>

    /// <summary>本家 CultureInfo::.cctor → CultureData.get_Invariant → GlobalizationMode の
    /// IL は AppContextConfigHelper → Environment.GetEnvironmentVariableCore を辿り、その
    /// Kernel32.GetEnvironmentVariable の managed wrapper を実行し、生成された
    /// GetEnvironmentVariableW DllImport の末端だけをバインドする。
    /// タスク 2 hardening: この面は trusted CoreLib (TrustedCoreLib domain) からの呼出のみ
    /// 到達する特権面 (BindingDomain.TrustedCoreLib) とし、VM ごとの仮想環境変数ストアを
    /// 読む (host Environment.GetEnvironmentVariable への直接委譲を廃止)。
    /// バッファへは Win32 規約 (戻り = 終端 null 除くコピー文字数 / 不足時は終端含む
    /// 必要文字数を返すのみ、未定義なら 0 + lastError = 203) で書き込む。
    /// Windows の Marshal.GetLastSystemError / SetLastSystemError も元の IL を実行する。
    /// バインドは Kernel32.GetLastError / SetLastError の実 DllImport と、本体のない
    /// PInvokeError InternalCall に限定する。状態は ctx.Shared の VM インスタンス状態。</summary>
    private static void RegisterEnvironmentAndMarshal(IntrinsicRegistry r) {
        r.RegisterBinding(BindingKey.TrustedStatic("System.Environment", "GetProcessorCount"), static (_, _) => StackSlot.OfInt32(1), BindingOrigin.InternalCall);
        r.RegisterBinding(BindingKey.Static("System.Environment", "get_CurrentManagedThreadId"),
            static (ctx, _) => StackSlot.OfInt32(ctx.Shared.GuestThreads.CurrentManagedThreadId),
            BindingOrigin.InternalCall);
        // Guest debugging has no native debugger attached. Task's diagnostic
        // probe must not enter the host's P/Invoke implementation.
        r.RegisterBinding(BindingKey.TrustedStatic("System.Diagnostics.Debugger", "IsManagedDebuggerAttached"),
            static (_, _) => StackSlot.OfInt32(0), BindingOrigin.InternalCall);
        const string MarshalType = "System.Runtime.InteropServices.Marshal";
        if (OperatingSystem.IsWindows()) {
            // These are actual native imports reached by Marshal's original
            // static initializer. The VM has no COM or process ANSI code page.
            r.RegisterBinding(BindingKey.TrustedStatic(MarshalType, "<IsBuiltInComSupportedInternal>g____PInvoke|30_0"),
                static (_, _) => StackSlot.OfInt32(0), BindingOrigin.PInvokeReplacement);
            r.RegisterBinding(BindingKey.TrustedStatic("Interop+Kernel32", "GetCPInfo", "System.UInt32", "Interop+Kernel32+CPINFO*"),
                static (_, _) => StackSlot.OfInt32(0), BindingOrigin.PInvokeReplacement);
        }
        var systemErrorType = OperatingSystem.IsWindows() ? "Interop+Kernel32" : MarshalType;
        var systemErrorOrigin = OperatingSystem.IsWindows() ? BindingOrigin.PInvokeReplacement : BindingOrigin.InternalCall;
        r.RegisterBinding(BindingKey.TrustedStatic(systemErrorType, OperatingSystem.IsWindows() ? "SetLastError" : "SetLastSystemError", "System.Int32"),
            static (ctx, a) => {
                ctx.Shared.LastSystemError = a[0].AsInt32;
                return null;
            },
            systemErrorOrigin);
        r.RegisterBinding(BindingKey.TrustedStatic(systemErrorType, OperatingSystem.IsWindows() ? "GetLastError" : "GetLastSystemError"),
            static (ctx, _) => StackSlot.OfInt32(ctx.Shared.LastSystemError),
            systemErrorOrigin);
        // PInvokeError is a bodyless runtime InternalCall. Keep the existing
        // VM error slot; no managed Marshal wrapper is substituted on Windows.
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
        if (OperatingSystem.IsWindows()) r.RegisterBinding(BindingKey.TrustedStatic("Interop+Kernel32", "<GetEnvironmentVariable>g____PInvoke|296_0",
                "System.UInt16*", "System.Char*", "System.UInt32"),
            static (ctx, a) => GetEnvironmentVariableImpl(ctx, a),
            BindingOrigin.PInvokeReplacement);
        // Interop+BCrypt.BCryptGenRandom (Random / Marvin ハッシュ種等の乱数源 P/Invoke)。
        // 任意の native import は実行せず、ホスト暗号乱数 API に限定して委譲する。
        // P/Invoke 呼出元は TrustedCoreLib domain に限定し、ゲストからの直接呼出は拒否する。
        if (OperatingSystem.IsWindows()) r.RegisterBinding(BindingKey.TrustedStatic("Interop+BCrypt", "BCryptGenRandom",
                "System.IntPtr", "System.Byte*", "System.Int32", "System.Int32"),
            static (ctx, a) => {
                var count = a[2].AsInt32;
                return FillRandomBytes(ctx, a[1], count, "BCryptGenRandom");
            },
            BindingOrigin.PInvokeReplacement);
        // Unix CoreLib は同じ乱数源を Interop+Sys.GetNonCryptographicallySecureRandomBytes
        // (SystemNative) 経由で呼ぶ。ネイティブ import は実行せず、Windows 側の
        // BCrypt 代替と同じく VM のホスト RNG に委譲する。
        if (!OperatingSystem.IsWindows()) r.RegisterBinding(BindingKey.TrustedStatic("Interop+Sys", "GetNonCryptographicallySecureRandomBytes",
                "System.Byte*", "System.Int32"),
            static (ctx, a) => {
                var count = a[1].AsInt32;
                if (count < 0)
                    throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
                if (count == 0)
                    return null;

                var (native, slotRef) = ResolvePointerBase(a[0],
                    "Interop+Sys.GetNonCryptographicallySecureRandomBytes");
                if (native is not null) {
                    if (native.ByteOffset < 0 || (long)native.ByteOffset + count > native.Bytes.Length)
                        throw new InvalidOperationException(
                            $"GetNonCryptographicallySecureRandomBytes がブロック外を参照します (offset={native.ByteOffset}, {count} バイト)。");
                    ctx.Shared.FillRandom(native.Bytes.AsSpan(native.ByteOffset, count));
                    return null;
                }

                // Marvin/HashCode はローカルの ulong/uint を渡す構成がある。
                // 共通実装で各 managed スロット幅 (Int32/Int64) を保持して書き戻す。
                if (slotRef is not null) {
                    return FillRandomBytes(ctx, a[0], count,
                        "Interop+Sys.GetNonCryptographicallySecureRandomBytes");
                }

                throw new InvalidOperationException(
                    $"GetNonCryptographicallySecureRandomBytes のバッファがバイト実体ではありません ({a[0].Kind})。");
            },
            BindingOrigin.PInvokeReplacement);
        // GlobalizationMode+Settings::get_Invariant を true 固定にする (VM 規約: culture は
        // 不変カルチャ固定)。本家の DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 起動と同一意味論
        // で、本家 IL の分岐は CultureInfo::GetUserDefaultLocaleName 等の OS locale 取得
        // (Kernel32 P/Invoke) を通らない managed 経路に落ちる。OS locale 面自体は
        // culture 機構スコープ外のため代替実装を持たない (fail-closed を維持)
        r.RegisterBinding(BindingKey.TrustedStatic("System.Globalization.GlobalizationMode+Settings", "get_Invariant"),
            static (_, _) => StackSlot.OfInt32(1), BindingOrigin.InternalCall);
    }

    private static StackSlot FillRandomBytes(IntrinsicContext ctx, StackSlot buffer, int count,
        string operation) {
        if (count < 0)
            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
        if (count == 0)
            return StackSlot.OfInt32(0);
        if (buffer.ObjectValue is VmNativePointer native) {
            if (native.ByteOffset < 0 || (long)native.ByteOffset + count > native.Bytes.Length)
                throw new InvalidOperationException(
                    $"{operation} がブロック外を参照します (offset={native.ByteOffset}, {count} バイト)。");
            ctx.Shared.FillRandom(native.Bytes.AsSpan(native.ByteOffset, count));
            return StackSlot.OfInt32(0);
        }
        // Marvin uses ulong and HashCode uses uint locals. Respect each slot's
        // width rather than interpreting every managed random buffer as ulong.
        if (buffer.Kind == StackKind.ByRef && buffer.ObjectValue is VmByRef byRef) {
            if (byRef.Container.Length == 0)
                throw new UnhandledGuestException("System.NullReferenceException", null);
            byRef.EnsureWritable();
            long capacity = 0;
            for (int i = byRef.Index; capacity < count && i < byRef.Container.Length; i++) {
                capacity += byRef.Container[i].Kind switch { StackKind.Int32 => 4, StackKind.Int64 => 8, _ => throw new InvalidOperationException("Random buffer is not an integer slot.") };
            }
            if (byRef.Index < 0 || count > capacity)
                throw new InvalidOperationException(
                    $"{operation} がスロット列の範囲外を参照します (index={byRef.Index}, {count} バイト)。");
            var remaining = count;
            var index = byRef.Index;
            Span<byte> bytes = stackalloc byte[8];
            while (remaining > 0) {
                var slot = byRef.Container[index];
                var width = slot.Kind == StackKind.Int32 ? 4 : 8;
                BinaryPrimitives.WriteInt64LittleEndian(bytes, slot.Int64Value);
                var writeCount = Math.Min(width, remaining);
                ctx.Shared.FillRandom(bytes[..writeCount]);
                byRef.Container[index] = width == 4 ? StackSlot.OfInt32(BinaryPrimitives.ReadInt32LittleEndian(bytes)) : StackSlot.OfInt64(BinaryPrimitives.ReadInt64LittleEndian(bytes));
                remaining -= writeCount;
                index++;
            }
            return StackSlot.OfInt32(0);
        }
        throw new InvalidOperationException(
            $"{operation} のバッファがバイト実体ではありません ({buffer.Kind})。");
    }


    private static StackSlot GetEnvironmentVariableImpl(IntrinsicContext ctx, StackSlot[] a) {
        var (namePointer, nameReference) = ResolvePointerBase(a[0], "GetEnvironmentVariableW name");
        namePointer?.EnsureBounds(0);
        var available = namePointer is not null ? (namePointer.Bytes.Length - namePointer.ByteOffset) / 2
            : nameReference is not null ? nameReference.Container.Length - nameReference.Index : 0;
        var length = 0;
        while (length < available) {
            ctx.Heap.ChargeHostWork(1);
            var character = namePointer is not null
                ? BinaryPrimitives.ReadUInt16LittleEndian(namePointer.Bytes.AsSpan(checked(namePointer.ByteOffset + length * 2), 2))
                : nameReference!.Container[nameReference.Index + length].AsInt32;
            if (character == 0) break;
            length++;
        }
        if (length == available)
            throw new UnhandledGuestException("System.ArgumentException", "Environment variable name is not terminated.");
        ctx.Heap.ChargeHostBuffer(checked(length * 2));
        var name = string.Create(length, (namePointer, nameReference), static (chars, state) => {
            for (var i = 0; i < chars.Length; i++) chars[i] = state.namePointer is not null
                ? (char)BinaryPrimitives.ReadUInt16LittleEndian(state.namePointer.Bytes.AsSpan(state.namePointer.ByteOffset + i * 2, 2))
                : (char)state.nameReference!.Container[state.nameReference.Index + i].AsInt32;
        });
        var (native, slotRef) = ResolvePointerBase(a[1], "Interop+Kernel32.GetEnvironmentVariable");
        if (native is null && slotRef is null)
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
            native.EnsureWritable();
            var offset = native.ByteOffset;
            if (offset < 0 || (long)offset + value.Length * 2 + 2 > native.Bytes.Length)
                throw new InvalidOperationException(
                    $"Kernel32.GetEnvironmentVariable のバッファがブロック外を参照します (offset={offset}, 必要 {value.Length + 1} 文字, ブロック {native.Bytes.Length} バイト)。");
            var bytes = native.Bytes;
            for (var i = 0; i < value.Length; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + i * 2, 2), (short)value[i]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + value.Length * 2, 2), (short)'\0');
        } else {
            slotRef!.EnsureWritable();
            if (slotRef.Index < 0 || (long)slotRef.Index + value.Length + 1 > slotRef.Container.Length)
                throw new UnhandledGuestException("System.IndexOutOfRangeException", null);
            for (var i = 0; i < value.Length; i++)
                slotRef!.Container[slotRef.Index + i] = StackSlot.OfInt32(value[i]);
            slotRef!.Container[slotRef.Index + value.Length] = StackSlot.OfInt32('\0');
        }
        return StackSlot.OfInt32(value.Length);
    }
}
