using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

/// <summary>
/// VM が標準で面を提供する intrinsic 群 (System.Object / String / Math / Console / Convert)。
/// 値の受け渡しはすべて StackSlot (VM オブジェクトモデル) に正規化され、
/// I/O は仮想コンソールデバイス経由のみ。登録は VM 起動時の RegisterDefaults のみで行う。
/// </summary>
public static class DefaultIntrinsics {
    /// <summary>引数列の型付き読み取りヘルパ。</summary>
    private readonly struct Args(StackSlot[] slots) {
        public StackSlot this[int i] => slots[i];
        public int Count => slots.Length;

        public int Int32(int i) => slots[i].Kind switch {
            StackKind.Int32 or StackKind.NativeInt => (int)slots[i].Int64Value,
            StackKind.Int64 => CheckedInt32(slots[i].Int64Value),
            _ => BadArg<int>(i, "int32"),
        };
        public long Int64(int i) => slots[i].Kind switch {
            StackKind.Int32 => slots[i].Int64Value,
            StackKind.Int64 or StackKind.NativeInt => slots[i].Int64Value,
            _ => BadArg<long>(i, "int64"),
        };
        public double Float(int i) => slots[i].Kind switch {
            StackKind.Float => slots[i].DoubleValue,
            StackKind.Int32 => slots[i].Int64Value, // F スタックでは i4 からの暗変換は本来ないが許容
            _ => BadArg<double>(i, "float"),
        };
        public bool Bool(int i) => slots[i].Kind == StackKind.Int32 && slots[i].Int64Value != 0;
        public char Char(int i) => (char)Int32(i);

        public VmString String(int i) {
            if (slots[i].Kind == StackKind.Object && slots[i].ObjectValue is VmString s)
                return s;
            if (slots[i].Kind == StackKind.Object && slots[i].ObjectValue is null)
                throw new UnhandledGuestException("System.NullReferenceException", null);
            return BadArg<VmString>(i, "string");
        }

        private static int CheckedInt32(long value) {
            if (value is < int.MinValue or > int.MaxValue)
                throw new UnhandledGuestException("System.OverflowException", null);
            return (int)value;
        }

        private T BadArg<T>(int i, string want) =>
            throw new InvalidOperationException($"intrinsic 引数 {i} の型が不正です (期待: {want}, 実際: {slots[i].Kind})。");
    }

    /// <summary>指定の intrinsic が未実装であることを示すエラー (未実装面の明示)。</summary>
    private static StackSlot? NotImplemented(string name) =>
        throw new NotSupportedException($"intrinsic '{name}' は実装されていません (未実装面の明示)。");

    /// <summary>ボックス化された値の CLR 互換文字列化 (Object.ToString / String.Concat(object,...) 共通)。
    /// CLR の int.ToString() 等は CurrentCulture で書式化するため InvariantCulture は使わない。</summary>
    private static string FormatBoxed(VmBoxedValue boxed) {
        var slot = boxed.Fields[0];
        return boxed.Type.FullName switch {
            "System.Int32" => ((int)slot.Int64Value).ToString(),
            "System.UInt32" => ((uint)slot.Int64Value).ToString(),
            "System.Int64" => slot.Int64Value.ToString(),
            "System.UInt64" => ((ulong)slot.Int64Value).ToString(),
            "System.Int16" => ((short)slot.Int64Value).ToString(),
            "System.UInt16" => ((ushort)slot.Int64Value).ToString(),
            "System.SByte" => ((sbyte)slot.Int64Value).ToString(),
            "System.Byte" => ((byte)slot.Int64Value).ToString(),
            "System.Boolean" => slot.Int64Value != 0 ? "True" : "False",
            "System.Char" => ((char)slot.Int64Value).ToString(),
            "System.Single" => ((float)slot.DoubleValue).ToString(),
            "System.Double" => slot.DoubleValue.ToString(),
            _ => boxed.Type.FullName,
        };
    }

    /// <summary>スタックスロットの CLR 互換文字列化 (String.Concat(object,...) の引数用)。</summary>
    private static string FormatSlot(IntrinsicContext ctx, in StackSlot slot) => FormatValue(ctx, slot, "");

    /// <summary>
    /// スロットの CLR 互換文字列化。declaredType は宣言上のパラメータ型名
    /// (i4 スロットに統合される char / bool のオーバーロード判別に使用。未知なら空文字列)。
    /// ゲストクラスのインスタンスは ToString の仮想ディスパッチを試す (CLR の暗黙 ToString と同じ)。
    /// </summary>
    private static string FormatValue(IntrinsicContext ctx, in StackSlot slot, string declaredType) {
        switch (declaredType) {
            case "System.Char" when slot.Kind is StackKind.Int32 or StackKind.NativeInt:
                return ((char)slot.Int64Value).ToString();
            case "System.Boolean" when slot.Kind is StackKind.Int32:
                return slot.Int64Value != 0 ? "True" : "False";
        }
        return slot.Kind switch {
            StackKind.Int32 or StackKind.NativeInt => slot.Int64Value.ToString(),
            StackKind.Int64 => slot.Int64Value.ToString(),
            StackKind.Float => slot.DoubleValue.ToString(),
            StackKind.Object => slot.ObjectValue switch {
                null => "", // CLR も null は空文字列化する
                VmString s => s.Value,
                VmBoxedValue b => FormatBoxed(b),
                VmRuntimeObject t => t.Target.FullName,
                VmRuntimeMethod m => m.Target.ToString() ?? "",
                VmExceptionObject e => FormatExceptionText(e.Type.FullName, e.Message),
                VmClassInstance ci => ctx.InvokeToString(slot)?.Value ?? ci.ClassType.FullName,
                VmArray arr => arr.ArrayType.FullName,
                var other => other.ToString() ?? "",
            },
            StackKind.ValueType => ctx.InvokeToString(slot)?.Value ?? slot.ObjectValue?.ToString() ?? "",
            _ => "",
        };
    }

    // ---- 複合書式エンジン (String.Format / Console.Write 共通) ----

    /// <summary>複合書式 ({index[,alignment][:formatSpec]}) の展開。CLR の string.Format と同じ
    /// 「{{」/「}}」エスケープ、正の alignment は右寄せ、負は左寄せ。</summary>
    private static string FormatValues(IntrinsicContext ctx, string format, StackSlot[] values) {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < format.Length; i++) {
            var c = format[i];
            if (c == '{') {
                if (i + 1 < format.Length && format[i + 1] == '{') {
                    sb.Append('{');
                    i++;
                    continue;
                }
                var close = format.IndexOf('}', i);
                if (close < 0)
                    throw new UnhandledGuestException("System.FormatException", null);
                sb.Append(FormatPlaceholder(ctx, format[(i + 1)..close], values));
                i = close;
            } else if (c == '}' && i + 1 < format.Length && format[i + 1] == '}') {
                sb.Append('}');
                i++;
            } else {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string FormatPlaceholder(IntrinsicContext ctx, string spec, StackSlot[] values) {
        var colon = spec.IndexOf(':');
        var formatSpec = colon >= 0 ? spec[(colon + 1)..] : "";
        var head = colon >= 0 ? spec[..colon] : spec;
        var comma = head.IndexOf(',');
        if (!int.TryParse((comma >= 0 ? head[..comma] : head).Trim(), out var index))
            throw new UnhandledGuestException("System.FormatException", null);
        if (index < 0 || index >= values.Length)
            throw new UnhandledGuestException("System.FormatException", null);
        var text = FormatValue(ctx, values[index], "");
        if (comma >= 0) {
            if (!int.TryParse(head[(comma + 1)..].Trim(), out var alignment))
                throw new UnhandledGuestException("System.FormatException", null);
            text = alignment > 0 ? text.PadLeft(alignment) : text.PadRight(-alignment);
        }
        return ApplyFormatSpecifier(text, values[index], formatSpec);
    }

    /// <summary>共通の書式指定子 (X/x: 16進、D: 10進桁数、F/f: 固定小数、N: 桁区切り、G: 一般、P: パーセント)。
    /// 対応外の指定子は既定書式にフォールバックする。</summary>
    private static string ApplyFormatSpecifier(string text, in StackSlot slot, string spec) {
        if (spec.Length == 0)
            return text;
        var kind = char.ToUpperInvariant(spec[0]);
        int? digits = spec.Length > 1 && int.TryParse(spec[1..], out var n) ? n : null;
        try {
            return kind switch {
                'X' or 'x' => FormatHex(slot, kind == 'x', digits),
                'D' => digits is null ? text : IntegerValue(slot)?.ToString(new string('0', digits.Value)),
                'F' => FormatFloat(slot)?.ToString(digits is null ? "F2" : $"F{digits.Value}"),
                'N' => FormatFloat(slot)?.ToString(digits is null ? "N2" : $"N{digits.Value}"),
                'G' => FormatFloat(slot)?.ToString(digits is null ? "G" : $"G{digits.Value}"),
                'P' => FormatFloat(slot)?.ToString("P" + (digits?.ToString() ?? "")),
                'C' => FormatFloat(slot)?.ToString("C" + (digits?.ToString() ?? "")),
                _ => text,
            } ?? text;
        } catch (FormatException) {
            return text;
        }
    }

    /// <summary>16 進書式 ({0:X})。ソースのビット幅 (i4/i8) を尊重する (CLR と同じ)。</summary>
    private static string FormatHex(in StackSlot slot, bool lower, int? digits) {
        var inner = slot.Kind == StackKind.Object && slot.ObjectValue is VmBoxedValue b ? b.Fields[0] : slot;
        var fmt = (lower ? "x" : "X") + (digits?.ToString() ?? "");
        return inner.Kind == StackKind.Int64
            ? ((ulong)inner.Int64Value).ToString(fmt)
            : ((uint)(int)inner.Int64Value).ToString(fmt);
    }

    private static long? IntegerValue(in StackSlot slot) {
        var inner = slot.Kind == StackKind.Object && slot.ObjectValue is VmBoxedValue b ? b.Fields[0] : slot;
        return inner.Kind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt ? inner.Int64Value : null;
    }

    private static double? FormatFloat(in StackSlot slot) {
        var inner = slot.Kind == StackKind.Object && slot.ObjectValue is VmBoxedValue b ? b.Fields[0] : slot;
        return inner.Kind == StackKind.Float ? inner.DoubleValue : null;
    }

    /// <summary>VM 起動時に標準 intrinsic をすべて登録する。</summary>
    public static void RegisterAll(IntrinsicRegistry registry) {
        RegisterObject(registry);
        RegisterString(registry);
        RegisterPrimitiveToString(registry);
        RegisterMath(registry);
        RegisterConsole(registry);
        RegisterConvert(registry);
        RegisterExceptions(registry);
        RegisterType(registry);
        RegisterMethodBase(registry);
        RegisterInterpolatedStringHandler(registry);
        RegisterDisposable(registry);
        RegisterRuntimeHelpers(registry);
        RegisterFile(registry);
        RegisterWebClient(registry);
    }

    // ---- System.Type / System.Reflection.MethodBase (typeof / GetType / GetCurrentMethod 面) ----

    /// <summary>レシーバの実行時型を得る (Object.GetType() 用)。</summary>
    private static VmType RuntimeTypeOf(IntrinsicContext ctx, in StackSlot slot) =>
        slot.ObjectValue switch {
            VmClassInstance ci => ci.TypeArguments.Length > 0
                ? new VmConstructedType { Definition = ci.ClassType, TypeArguments = ci.TypeArguments }
                : (VmType)ci.ClassType,
            VmBoxedValue bv => bv.Type,
            VmArray arr => arr.ArrayType,
            VmExceptionObject e => e.Type,
            VmRuntimeObject rt => rt.Target,
            null => throw new UnhandledGuestException("System.NullReferenceException", null),
            _ => ctx.Types.FindIntrinsicType("System.Object")
                ?? throw new InvalidOperationException("ファサード型 System.Object が未登録です。"),
        };

    /// <summary>
    /// 文字列補間 ($"...") がコンパイルされる DefaultInterpolatedStringHandler の面。
    /// Roslyn はローカルのハンドラ構造体を ldloca + call で操作するため、全 instance メソッドの
    /// this はローカルスロットへの ByRef。状態はスロットに置いた VmIntrinsicCarrier
    /// (Payload = StringBuilder) で保持する。AppendFormatted&lt;T&gt; は MethodSpec 経由で来るため
    /// インスタンス化後の宣言型名 (IntrinsicContext.ParameterTypeNames) で判別する。
    /// </summary>
    private static void RegisterInterpolatedStringHandler(IntrinsicRegistry r) {
        const string T = "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler";

        // this (ByRef) → 状態バッファ
        static System.Text.StringBuilder State(StackSlot[] a) =>
            a[0].ObjectValue is VmByRef byRef && byRef.Slot.ObjectValue is VmIntrinsicCarrier carrier
                ? (System.Text.StringBuilder)carrier.Payload!
                : throw new InvalidOperationException("DefaultInterpolatedStringHandler の this が初期化されていません。");
        // this (ByRef) → 状態バッファを書き戻す
        static void SetState(StackSlot[] a, object? payload) {
            if (a[0].ObjectValue is VmByRef byRef)
                byRef.Slot = StackSlot.OfObject(new VmIntrinsicCarrier { Payload = payload });
        }

        void I(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Instance(T, name, ps), impl);

        I(".ctor", 2, static (_, a) => { SetState(a, new System.Text.StringBuilder()); return null; });
        I(".ctor", 3, static (_, a) => { SetState(a, new System.Text.StringBuilder()); return null; }); // +IFormatProvider (カルチャは CurrentCulture)
        I("AppendLiteral", 1, static (_, a) => {
            State(a).Append(new Args(a).String(1).Value);
            return null;
        });
        I("AppendFormatted", 1, static (ctx, a) => {
            State(a).Append(FormatValue(ctx, a[1], ctx.ParamAt(0)));
            return null;
        });
        I("AppendFormatted", 2, static (ctx, a) => {
            // (T, string format) と (T, int alignment) の 2 overload を宣言型で判別
            var s = State(a);
            var text = FormatValue(ctx, a[1], ctx.ParamAt(0));
            if (ctx.ParamAt(1) == "System.String")
                s.Append(ApplyFormatSpecifier(text, a[1], new Args(a).String(2).Value ?? ""));
            else if (ctx.ParamAt(1) == "System.Int32")
                s.Append(ApplyAlignment(text, new Args(a).Int32(2)));
            else
                throw new InvalidOperationException($"AppendFormatted の第2引数型 {ctx.ParamAt(1)} は未対応です。");
            return null;
        });
        I("AppendFormatted", 3, static (ctx, a) => {
            // (T, int alignment, string format)
            var s = State(a);
            var text = FormatValue(ctx, a[1], ctx.ParamAt(0));
            text = ApplyAlignment(text, new Args(a).Int32(2));
            s.Append(ApplyFormatSpecifier(text, a[1], new Args(a).String(3).Value ?? ""));
            return null;
        });
        I("ToStringAndClear", 0, static (ctx, a) => {
            var text = State(a).ToString();
            SetState(a, null); // CLR と同じくハンドラを使い切る (呼出後の再使用は破壊扱い)
            return StackSlot.OfObject(ctx.MakeString(text));
        });
        I("ToString", 0, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(State(a).ToString())));
    }

    /// <summary>補間の alignment 指定 ({value,-5} 等)。正は右寄せ、負は左寄せ。</summary>
    private static string ApplyAlignment(string text, int alignment) =>
        alignment > 0 ? text.PadLeft(alignment) : alignment < 0 ? text.PadRight(-alignment) : text;

    /// <summary>System.Type ファサードの実体を生成する (typeof(X) / GetType() の戻り値)。</summary>
    private static StackSlot MakeRuntimeObject(IntrinsicContext ctx, VmType type) =>
        StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeObject { Target = type }));

    private static void RegisterType(IntrinsicRegistry r) {
        const string T = "System.Type";
        r.Register(IntrinsicKey.Static(T, "GetTypeFromHandle", 1), static (ctx, a) =>
            a[0].ObjectValue is VmTypeHandle handle
                ? MakeRuntimeObject(ctx, handle.Target)
                : throw new InvalidOperationException("GetTypeFromHandle の引数が RuntimeTypeHandle ではありません。"));
        // typeof(x) == typeof(y) はコンパイラが op_Equality / op_Inequality に出す (型同一性比較)
        r.Register(IntrinsicKey.Static(T, "op_Equality", 2), static (_, a) =>
            StackSlot.OfInt32(SameType(a) ? 1 : 0));
        r.Register(IntrinsicKey.Static(T, "op_Inequality", 2), static (_, a) =>
            StackSlot.OfInt32(SameType(a) ? 0 : 1));

        // Name / FullName 等は MemberInfo 宣言のメンバ。Roslyn は宣言型を MemberRef 親に
        // 出すため、同一面を System.Reflection.MemberInfo にも登録しておく。
        foreach (var typeName in new[] { T, "System.Reflection.MemberInfo" }) {
            var type = typeName;
            void Instance(string name, int ps, IntrinsicImpl impl) =>
                r.Register(IntrinsicKey.Instance(type, name, ps), impl);
            Instance("get_Name", 0, static (ctx, a) => {
                // MemberInfo::Name は Type と MethodBase の両方の宣言元なので双方向に対応
                var name = a[0].ObjectValue switch {
                    VmRuntimeObject runtimeType => runtimeType.Target.Name,
                    VmRuntimeMethod runtimeMethod => runtimeMethod.Target.Name,
                    _ => throw new InvalidOperationException("MemberInfo::get_Name の this が Type/MethodBase 面ではありません。"),
                };
                return StackSlot.OfObject(ctx.MakeString(name));
            });
            Instance("get_FullName", 0, static (ctx, a) =>
                StackSlot.OfObject(ctx.MakeString(((VmRuntimeObject)a[0].ObjectValue!).Target.FullName)));
            Instance("get_UnderlyingSystemType", 0, static (ctx, a) =>
                MakeRuntimeObject(ctx, ((VmRuntimeObject)a[0].ObjectValue!).Target));
            Instance("ToString", 0, static (ctx, a) =>
                StackSlot.OfObject(ctx.MakeString(((VmRuntimeObject)a[0].ObjectValue!).Target.FullName)));
            Instance("Equals", 1, static (_, a) =>
                StackSlot.OfInt32(a[0].ObjectValue is VmRuntimeObject && a[1].ObjectValue is VmRuntimeObject && SameType(a) ? 1 : 0));
        }
    }

    private static bool SameType(StackSlot[] a) =>
        a[0].ObjectValue is VmRuntimeObject left && a[1].ObjectValue is VmRuntimeObject right &&
        left.Target.FullName == right.Target.FullName;

    private static void RegisterMethodBase(IntrinsicRegistry r) {
        const string T = "System.Reflection.MethodBase";
        r.Register(IntrinsicKey.Static(T, "GetMethodFromHandle", 1), static (ctx, a) =>
            a[0].ObjectValue is VmMethodHandle handle
                ? StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeMethod { Target = handle.Target }))
                : throw new InvalidOperationException("GetMethodFromHandle の引数が RuntimeMethodHandle ではありません。"));
        r.Register(IntrinsicKey.Static(T, "GetCurrentMethod", 0), static (ctx, _) =>
            ctx.CurrentMethodHook is { } hook && hook() is { } method
                ? StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeMethod { Target = method }))
                : StackSlot.Null);

        void Instance(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Instance(T, name, ps), impl);
        Instance("get_Name", 0, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(((VmRuntimeMethod)a[0].ObjectValue!).Target.Name)));
        Instance("get_DeclaringType", 0, static (ctx, a) =>
            MakeRuntimeObject(ctx, ((VmRuntimeMethod)a[0].ObjectValue!).Target.DeclaringType));
        Instance("ToString", 0, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(((VmRuntimeMethod)a[0].ObjectValue!).Target.ToString() ?? "")));
    }

    // ---- System.IO.File (ストレージゲートウェイ経由のみ) ----

    /// <summary>ストレージ面の取得 (未設定なら即拒否。ブリッジ未設定はゲートウェイが拒否する)。</summary>
    private static StorageGateway Storage(IntrinsicContext ctx) =>
        ctx.Storage ?? throw new OperationNotAllowedException(
            "ストレージ面は無効化されています (VmHostOptions.StorageBridge 未設定)。");

    /// <summary>ネットワーク面の取得 (未設定なら即拒否。ブリッジ未設定はゲートウェイが拒否する)。</summary>
    private static NetworkGateway Network(IntrinsicContext ctx) =>
        ctx.Network ?? throw new OperationNotAllowedException(
            "ネットワーク面は無効化されています (VmHostOptions.NetworkBridge 未設定)。");

    private static void RegisterFile(IntrinsicRegistry r) {
        const string T = "System.IO.File";
        void S(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Static(T, name, ps), impl);

        S("Exists", 1, static (ctx, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(Storage(ctx).Exists(s.String(0).Value) ? 1 : 0);
        });
        S("ReadAllBytes", 1, static (ctx, a) => {
            var s = new Args(a);
            return StackSlot.OfObject(ctx.MakeByteArray(Storage(ctx).Read(s.String(0).Value)));
        });
        S("WriteAllBytes", 2, static (ctx, a) => {
            var s = new Args(a);
            Storage(ctx).Write(s.String(0).Value, ctx.ReadByteArray(a[1]));
            return null;
        });
        S("ReadAllText", 1, static (ctx, a) => {
            var s = new Args(a);
            var bytes = Storage(ctx).Read(s.String(0).Value);
            return StackSlot.OfObject(ctx.MakeString(System.Text.Encoding.UTF8.GetString(bytes)));
        });
        S("WriteAllText", 2, static (ctx, a) => {
            var s = new Args(a);
            Storage(ctx).Write(s.String(0).Value, System.Text.Encoding.UTF8.GetBytes(s.String(1).Value));
            return null;
        });
        S("Delete", 1, static (ctx, a) => {
            Storage(ctx).Delete(new Args(a).String(0).Value);
            return null;
        });
    }

    // ---- System.Net.WebClient (ネットワークゲートウェイ経由のみ) ----

    private static void RegisterWebClient(IntrinsicRegistry r) {
        const string T = "System.Net.WebClient";
        void I(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Instance(T, name, ps), impl);

        I(".ctor", 0, static (_, _) => null);
        // instance メソッドは args[0] が this のため、引数は Args のインデックス 1 以降で読む
        I("DownloadData", 1, static (ctx, a) => {
            var url = new Args(a).String(1).Value;
            return StackSlot.OfObject(ctx.MakeByteArray(Network(ctx).Transfer(url, ReadOnlyMemory<byte>.Empty)));
        });
        I("DownloadString", 1, static (ctx, a) => {
            var url = new Args(a).String(1).Value;
            var bytes = Network(ctx).Transfer(url, ReadOnlyMemory<byte>.Empty);
            return StackSlot.OfObject(ctx.MakeString(System.Text.Encoding.UTF8.GetString(bytes)));
        });
        I("UploadData", 2, static (ctx, a) => {
            var s = new Args(a);
            var data = ctx.ReadByteArray(a[2]);
            var bytes = Network(ctx).Transfer(s.String(1).Value, data);
            return StackSlot.OfObject(ctx.MakeByteArray(bytes));
        });
    }

    /// <summary>
    /// IDisposable::Dispose の面。using/foreach の leave 時に呼ばれる。
    /// ゲスト実装があれば Call の仮想ディスパッチが優先されるため、この intrinsic は
    /// 「レシーバにゲスト実装が無かった場合の面」(リソースを持たない 既定 = no-op) となる。
    /// </summary>
    private static void RegisterDisposable(IntrinsicRegistry registry) {
        registry.Register(IntrinsicKey.Instance("System.IDisposable", "Dispose", 0),
            (ctx, a) => null);
    }

    // ---- System.Runtime.CompilerServices.RuntimeHelpers ----

    /// <summary>配列初期化子 (ldtoken Field + InitializeArray) の面。FieldRVA データを要素に展開する。</summary>
    private static void RegisterRuntimeHelpers(IntrinsicRegistry registry) {
        registry.Register(IntrinsicKey.Static(
                "System.Runtime.CompilerServices.RuntimeHelpers", "InitializeArray", 2),
            static (_, a) => {
                if (a[0].ObjectValue is not VmArray array)
                    throw new InvalidOperationException("InitializeArray の第1引数が配列ではありません。");
                if (a[1].ObjectValue is not VmFieldRvaData handle)
                    throw new InvalidOperationException("InitializeArray の第2引数がフィールドハンドルではありません。");
                CopyInitializerData(array, handle.Data.Span);
                return null;
            });
    }

    /// <summary>FieldRVA 初期データを配列要素へリトルエンディアンで展開する。</summary>
    private static void CopyInitializerData(VmArray array, ReadOnlySpan<byte> data) {
        var offset = 0;
        for (var i = 0; i < array.Length; i++) {
            var size = 0;
            switch (array.ArrayType.ElementType.FullName) {
                case "System.Byte":
                    array.Elements[i] = StackSlot.OfInt32(data[offset]);
                    size = 1;
                    break;
                case "System.SByte":
                    array.Elements[i] = StackSlot.OfInt32((sbyte)data[offset]);
                    size = 1;
                    break;
                case "System.Boolean":
                    array.Elements[i] = StackSlot.OfInt32(data[offset] != 0 ? 1 : 0);
                    size = 1;
                    break;
                case "System.Char":
                    array.Elements[i] = StackSlot.OfInt32(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]));
                    size = 2;
                    break;
                case "System.Int16":
                    array.Elements[i] = StackSlot.OfInt32(BinaryPrimitives.ReadInt16LittleEndian(data[offset..]));
                    size = 2;
                    break;
                case "System.UInt16":
                    array.Elements[i] = StackSlot.OfInt32(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]));
                    size = 2;
                    break;
                case "System.Int32":
                    array.Elements[i] = StackSlot.OfInt32(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]));
                    size = 4;
                    break;
                case "System.UInt32":
                    array.Elements[i] = StackSlot.OfInt32(unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..])));
                    size = 4;
                    break;
                case "System.Int64":
                    array.Elements[i] = StackSlot.OfInt64(BinaryPrimitives.ReadInt64LittleEndian(data[offset..]));
                    size = 8;
                    break;
                case "System.UInt64":
                    array.Elements[i] = StackSlot.OfInt64(unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(data[offset..])));
                    size = 8;
                    break;
                case "System.Single":
                    array.Elements[i] = StackSlot.OfFloat(BinaryPrimitives.ReadSingleLittleEndian(data[offset..]));
                    size = 4;
                    break;
                case "System.Double":
                    array.Elements[i] = StackSlot.OfFloat(BinaryPrimitives.ReadDoubleLittleEndian(data[offset..]));
                    size = 8;
                    break;
                default:
                    throw new NotSupportedException(
                        $"InitializeArray はプリミティブ要素配列のみ対応しています ({array.ArrayType.ElementType.FullName})。");
            }
            offset += size;
        }
        if (offset > data.Length)
            throw new BadImageFormatException(
                $"FieldRVA 初期データが不足しています (必要 {offset} バイト, 実際 {data.Length} バイト)。");
    }

    /// <summary>プリミティブ型の instance ToString() (int.ToString() 等の直接呼出用)。
    /// CLR と同じく CurrentCulture で書式化する (InvariantCulture は互換性上の差異になるため使わない)。</summary>
    private static void RegisterPrimitiveToString(IntrinsicRegistry registry) {
        void S(string type, Func<StackSlot, string> format) =>
            registry.Register(IntrinsicKey.Instance(type, "ToString", 0),
                (ctx, a) => StackSlot.OfObject(ctx.MakeString(format(a[0]))));

        S("System.Int32", s => ((int)s.Int64Value).ToString());
        S("System.UInt32", s => ((uint)s.Int64Value).ToString());
        S("System.Int64", s => s.Int64Value.ToString());
        S("System.UInt64", s => ((ulong)s.Int64Value).ToString());
        S("System.Int16", s => ((short)s.Int64Value).ToString());
        S("System.UInt16", s => ((ushort)s.Int64Value).ToString());
        S("System.SByte", s => ((sbyte)s.Int64Value).ToString());
        S("System.Byte", s => ((byte)s.Int64Value).ToString());
        S("System.Boolean", s => s.Int64Value != 0 ? "True" : "False");
        S("System.Char", s => ((char)s.Int64Value).ToString());
        S("System.Single", s => ((float)s.DoubleValue).ToString());
        S("System.Double", s => s.DoubleValue.ToString());
    }

    // ---- System.Object ----

    private static void RegisterObject(IntrinsicRegistry r) {
        const string T = "System.Object";
        r.Register(IntrinsicKey.Instance(T, ".ctor", 0), static (_, _) => null);
        r.Register(IntrinsicKey.Instance(T, "ToString", 0), static (ctx, a) => {
            var s = new Args(a);
            return s[0].ObjectValue switch {
                VmString str => StackSlot.OfObject(str),
                VmBoxedValue boxed => StackSlot.OfObject(ctx.MakeString(FormatBoxed(boxed))),
                // CLR の既定 ToString は型の完全名を返す
                VmClassInstance ci => StackSlot.OfObject(ctx.MakeString(DefaultToString(ctx, ci))),
                VmExceptionObject e => StackSlot.OfObject(ctx.MakeString(FormatExceptionText(e.Type.FullName, e.Message))),
                VmArray arr => StackSlot.OfObject(ctx.MakeString(arr.ArrayType.FullName)),
                _ => NotImplemented("System.Object::ToString"),
            };
        });
        r.Register(IntrinsicKey.Instance(T, "Equals", 1), static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(ReferenceEquals(s[0].ObjectValue, s[1].ObjectValue) ? 1 : 0);
        });
        r.Register(IntrinsicKey.Static(T, "Equals", 2), static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(ReferenceEquals(s[0].ObjectValue, s[1].ObjectValue) ? 1 : 0);
        });
        r.Register(IntrinsicKey.Instance(T, "GetHashCode", 0), static (ctx, a) =>
            StackSlot.OfInt32(ctx.IdentityHash(a[0].ObjectValue)));
        // Object.GetType(): レシーバの実行時型 (構築型なら型引数込み) の System.Type ファサードを返す
        r.Register(IntrinsicKey.Instance(T, "GetType", 0), static (ctx, a) =>
            MakeRuntimeObject(ctx, RuntimeTypeOf(ctx, a[0])));
    }

    // ---- System.Exception (ファサード階層の共通面) ----

    /// <summary>CLR 互換の既定 ToString。Exception 派生クラスは「型名: メッセージ」形式。</summary>
    private static string DefaultToString(IntrinsicContext ctx, VmClassInstance ci) {
        if (!DerivesFromException(ci.ClassType))
            return ci.ClassType.FullName;
        var message = IntrinsicContext.GetExceptionMessage(ci);
        return FormatExceptionText(ci.ClassType.FullName, message);
    }

    private static string FormatExceptionText(string typeName, VmString? message) {
        var text = message?.Value;
        return string.IsNullOrEmpty(text) ? typeName : $"{typeName}: {text}";
    }

    /// <summary>クラスが Exception ファサードを基底に持つか (ゲスト例外クラスの判定)。</summary>
    private static bool DerivesFromException(VmType type) {
        for (var t = (VmType?)type; t is not null; t = t.BaseType)
            if (t.FullName == "System.Exception")
                return true;
        return false;
    }

    /// <summary>例外メッセージの取得 (VmExceptionObject / ゲスト Exception 派生クラスの両方)。</summary>
    private static VmString? GetMessageOf(in StackSlot slot) => slot.ObjectValue switch {
        VmExceptionObject e => e.Message,
        VmClassInstance ci when DerivesFromException(ci.ClassType) => IntrinsicContext.GetExceptionMessage(ci),
        _ => null,
    };

    private static Exception BadReceiverException(Args s) =>
        new InvalidOperationException($"例外 intrinsic のレシーバが不正です: {DescribeReceiver(s[0])}");

    private static string DescribeReceiver(in StackSlot slot) => slot.ObjectValue switch {
        null => "null",
        var o => o.GetType().Name,
    };

    private static void RegisterExceptions(IntrinsicRegistry r) {
        const string T = "System.Exception";
        void Instance(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Instance(T, name, ps), impl);

        // .ctor: メッセージの記録。VmExceptionObject には直接、ゲスト派生クラスには例外メッセージ表へ
        Instance(".ctor", 0, static (_, a) => {
            var s = new Args(a);
            SetMessage(s[0], null);
            return null;
        });
        Instance(".ctor", 1, static (_, a) => {
            var s = new Args(a);
            SetMessage(s[0], s[1].ObjectValue as VmString);
            return null;
        });
        Instance("get_Message", 0, static (ctx, a) => {
            var s = new Args(a);
            if (s[0].ObjectValue is not (VmExceptionObject or VmClassInstance))
                throw BadReceiverException(s);
            var message = s[0].ObjectValue switch {
                VmExceptionObject e => e.Message,
                VmClassInstance ci when DerivesFromException(ci.ClassType) =>
                    IntrinsicContext.GetExceptionMessage(ci),
                _ => null,
            };
            // CLR 互換: メッセージ未設定なら「Exception of type 'X' was thrown.」
            var typeName = s[0].ObjectValue switch {
                VmExceptionObject e => e.Type.FullName,
                VmClassInstance ci => ci.ClassType.FullName,
                _ => throw new InvalidOperationException("unreachable"),
            };
            var text = message is not null && message.Value.Length > 0
                ? message.Value
                : $"Exception of type '{typeName}' was thrown.";
            return StackSlot.OfObject(ctx.MakeString(text));
        });
        Instance("ToString", 0, static (ctx, a) => {
            var s = new Args(a);
            return s[0].ObjectValue switch {
                VmExceptionObject e => StackSlot.OfObject(ctx.MakeString(FormatExceptionText(e.Type.FullName, e.Message))),
                VmClassInstance ci => StackSlot.OfObject(ctx.MakeString(DefaultToString(ctx, ci))),
                _ => throw BadReceiverException(s),
            };
        });
    }

    /// <summary>Exception::.ctor のメッセージ記録 (レシーバの種類で保存先を選ぶ)。</summary>
    private static void SetMessage(in StackSlot receiver, VmString? message) {
        switch (receiver.ObjectValue) {
            case VmExceptionObject e:
                e.Message = message;
                break;
            case VmClassInstance ci:
                IntrinsicContext.SetExceptionMessage(ci, message);
                break;
            default:
                throw new InvalidOperationException($"例外 .ctor のレシーバが不正です: {DescribeReceiver(receiver)}");
        }
    }

    // ---- System.String ----

    private static void RegisterString(IntrinsicRegistry r) {
        const string T = "System.String";
        var instanceTemplate = IntrinsicKey.Instance(T, "", 0);
        var staticTemplate = IntrinsicKey.Static(T, "", 0);

        // インスタンスメソッド (this + 引数) / 静的メソッド
        void Instance(string name, int ps, IntrinsicImpl impl) =>
            r.Register(instanceTemplate with { MethodName = name, Arity = ps + 1 }, impl);
        void Static(string name, int ps, IntrinsicImpl impl) =>
            r.Register(staticTemplate with { MethodName = name, Arity = ps }, impl);

        // 静的フィールド (ldsfld の TypeRef 親で参照される)。空文字列はプールから実体化
        r.RegisterStaticField(T, "Empty", static ctx => StackSlot.OfObject(ctx.MakeString("")));

        Instance(".ctor", 0, static (_, _) => null); // フォールバック (通常は newobj 経由で呼ばれない)
        Instance("get_Length", 0, static (_, a) => StackSlot.OfInt32(new Args(a).String(0).Value.Length));
        Instance("get_Chars", 1, static (_, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            var i = s.Int32(1);
            if ((uint)i >= (uint)value.Length)
                throw new UnhandledGuestException("System.IndexOutOfRangeException", null);
            return StackSlot.OfInt32(value[i]);
        });
        Instance("Substring", 1, static (ctx, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            var start = s.Int32(1);
            if ((uint)start > (uint)value.Length)
                throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
            return StackSlot.OfObject(ctx.MakeString(value[start..]));
        });
        Instance("Substring", 2, static (ctx, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            var start = s.Int32(1);
            var length = s.Int32(2);
            if ((uint)start > (uint)value.Length || length < 0 || start + length > value.Length)
                throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
            return StackSlot.OfObject(ctx.MakeString(value.Substring(start, length)));
        });
        Instance("Equals", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s[1].ObjectValue is VmString other && s.String(0).Value == other.Value ? 1 : 0);
        });
        Instance("CompareTo", 1, static (_, a) => {
            var s = new Args(a);
            // CLR の String.CompareTo は CurrentCulture の比較 (Ordinal ではない)
            return StackSlot.OfInt32(string.Compare(s.String(0).Value, s.String(1).Value, StringComparison.CurrentCulture));
        });
        Instance("Contains", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value.Contains(s.String(1).Value) ? 1 : 0);
        });
        Instance("StartsWith", 1, static (_, a) => {
            var s = new Args(a);
            // CLR の 1 引数 StartsWith/EndsWith は CurrentCulture 比較
            return StackSlot.OfInt32(s.String(0).Value.StartsWith(s.String(1).Value, StringComparison.CurrentCulture) ? 1 : 0);
        });
        Instance("EndsWith", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value.EndsWith(s.String(1).Value, StringComparison.CurrentCulture) ? 1 : 0);
        });
        Instance("IndexOf", 1, static (_, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            return s[1].ObjectValue is VmString needle
                ? StackSlot.OfInt32(value.IndexOf(needle.Value, StringComparison.CurrentCulture)) // CLR の 1 引数 IndexOf は CurrentCulture
                : StackSlot.OfInt32(value.IndexOf(s.Char(1)));
        });
        Instance("LastIndexOf", 1, static (_, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            return s[1].ObjectValue is VmString needle
                ? StackSlot.OfInt32(value.LastIndexOf(needle.Value, StringComparison.CurrentCulture))
                : StackSlot.OfInt32(value.LastIndexOf(s.Char(1)));
        });
        Instance("Replace", 2, static (ctx, a) => {
            var s = new Args(a);
            return StackSlot.OfObject(ctx.MakeString(s.String(0).Value.Replace(s.String(1).Value, s.String(2).Value)));
        });
        Instance("ToUpper", 0, static (ctx, a) => StackSlot.OfObject(ctx.MakeString(new Args(a).String(0).Value.ToUpperInvariant())));
        Instance("ToLower", 0, static (ctx, a) => StackSlot.OfObject(ctx.MakeString(new Args(a).String(0).Value.ToLowerInvariant())));
        Instance("Trim", 0, static (ctx, a) => StackSlot.OfObject(ctx.MakeString(new Args(a).String(0).Value.Trim())));
        Instance("ToString", 0, static (_, a) => StackSlot.OfObject(new Args(a).String(0)));

        // String.Concat は string / object の各オーバーロードが同一キーに集約されるため、
        // 引数を実行時にフォーマットして両方を処理する (ボックス化値は ToString 相当)
        Static("Concat", 2, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(FormatSlot(ctx, a[0]) + FormatSlot(ctx, a[1]))));
        Static("Concat", 3, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(FormatSlot(ctx, a[0]) + FormatSlot(ctx, a[1]) + FormatSlot(ctx, a[2]))));
        Static("Concat", 4, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(FormatSlot(ctx, a[0]) + FormatSlot(ctx, a[1]) + FormatSlot(ctx, a[2]) + FormatSlot(ctx, a[3]))));
        // String.Format (書式 + 値 1〜3 + params object[])。末尾 object[] は共通処理で展開
        Static("Format", 2, FormatImpl);
        Static("Format", 3, FormatImpl);
        Static("Format", 4, FormatImpl);
        Static("Format", 5, FormatImpl);
        // String.Split (char / char[] / string[] セパレータ。カウント / StringSplitOptions 付きも統合)
        Instance("Split", 1, SplitImpl);
        Instance("Split", 2, SplitImpl);
        // String.Join (区切り string/char、要素 string[]/object[])
        Static("Join", 2, JoinImpl);
        Static("Equals", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value == s.String(1).Value ? 1 : 0);
        });
        Static("op_Equality", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value == s.String(1).Value ? 1 : 0);
        });
        Static("op_Inequality", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value != s.String(1).Value ? 1 : 0);
        });
        Static("IsNullOrEmpty", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s[0].ObjectValue is not VmString str || str.Value.Length == 0 ? 1 : 0);
        });
        Static("IsNullOrWhiteSpace", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s[0].ObjectValue is not VmString str || string.IsNullOrWhiteSpace(str.Value) ? 1 : 0);
        });
        Static("Intern", 1, static (ctx, a) => StackSlot.OfObject(new Args(a).String(0))); // プール済み VmString を返す
    }

    /// <summary>String.Format 統合面 (第1引数が書式、残りが値。末尾 params object[] は展開)。</summary>
    private static StackSlot? FormatImpl(IntrinsicContext ctx, StackSlot[] a) =>
        StackSlot.OfObject(ctx.MakeString(FormatValues(ctx, new Args(a).String(0).Value, FormatArgs(ctx, a))));

    /// <summary>書式値列の取り出し (末尾パラメータが object[] なら展開して連結する)。</summary>
    private static StackSlot[] FormatArgs(IntrinsicContext ctx, StackSlot[] a) {
        if (ctx.ParamAt(a.Length - 1) == "System.Object[]") {
            var head = a[1..^1];
            var tail = ReadObjectArray(a[^1]);
            var merged = new StackSlot[head.Length + tail.Length];
            head.CopyTo(merged, 0);
            tail.CopyTo(merged, head.Length);
            return merged;
        }
        return a[1..];
    }

    private static StackSlot[] ReadObjectArray(in StackSlot slot) {
        if (slot.Kind != StackKind.Object || slot.ObjectValue is not VmArray arr)
            throw new InvalidOperationException("params object[] の引数が配列ではありません。");
        return arr.Elements;
    }

    private static char[] ReadCharArray(in StackSlot slot) {
        if (slot.Kind != StackKind.Object || slot.ObjectValue is not VmArray arr)
            throw new InvalidOperationException("char[] の引数が配列ではありません。");
        return arr.Elements.Select(e => (char)e.Int64Value).ToArray();
    }

    private static string[] ReadStringArray(IntrinsicContext ctx, in StackSlot slot) {
        if (slot.Kind != StackKind.Object || slot.ObjectValue is not VmArray arr)
            throw new InvalidOperationException("string[] の引数が配列ではありません。");
        return arr.Elements.Select(e => e.ObjectValue is VmString s ? s.Value
            : ctx.InvokeToString(e)?.Value ?? "").ToArray();
    }

    /// <summary>String.Split 統合面。宣言パラメータ型で char / char[] / string[] を判別する。
    /// instance メソッドのため args[0] は this (= レシーバ文字列)、ParamAt(0) がセパレータの宣言型。</summary>
    private static StackSlot? SplitImpl(IntrinsicContext ctx, StackSlot[] a) {
        var s = new Args(a);
        var value = s.String(0).Value;
        var t1 = ctx.ParamAt(0);
        string[] parts;
        if (a.Length == 2) {
            // this + セパレータのみ
            parts = t1 switch {
                "System.Char" => value.Split(s.Char(1)),
                "System.Char[]" => value.Split(ReadCharArray(a[1])),
                "System.String[]" => value.Split(ReadStringArray(ctx, a[1]), StringSplitOptions.None),
                _ => throw new InvalidOperationException($"String.Split 未対応のセパレータ型: {t1}"),
            };
        } else if (a.Length == 3) {
            // this + セパレータ + (count | StringSplitOptions)
            var t2 = ctx.ParamAt(1);
            parts = (t1, t2) switch {
                ("System.Char", "System.StringSplitOptions") => value.Split(s.Char(1), (StringSplitOptions)s.Int32(2)),
                ("System.Char", "System.Int32") => value.Split(s.Char(1), s.Int32(2)),
                ("System.Char[]", "System.StringSplitOptions") => value.Split(ReadCharArray(a[1]), (StringSplitOptions)s.Int32(2)),
                ("System.Char[]", "System.Int32") => value.Split(ReadCharArray(a[1]), s.Int32(2)),
                ("System.String[]", "System.StringSplitOptions") => value.Split(ReadStringArray(ctx, a[1]), (StringSplitOptions)s.Int32(2)),
                ("System.String[]", "System.Int32") => value.Split(ReadStringArray(ctx, a[1]), s.Int32(2), StringSplitOptions.None),
                _ => throw new InvalidOperationException($"String.Split 未対応の引数型: ({t1}, {t2})"),
            };
        } else {
            throw new InvalidOperationException($"String.Split の引数 {a.Length - 1} 個は未対応です。");
        }
        return StackSlot.OfObject(ctx.MakeStringArray(parts));
    }

    /// <summary>String.Join 統合面 (区切り string/char、要素 string[]/object[])。</summary>
    private static StackSlot? JoinImpl(IntrinsicContext ctx, StackSlot[] a) {
        var s = new Args(a);
        var separator = ctx.ParamAt(0) == "System.Char" ? s.Char(0).ToString() : s.String(0).Value;
        var parts = ReadObjectArray(a[1]).Select(v => v.ObjectValue switch {
            null => "",
            VmString str => str.Value,
            _ => ctx.InvokeToString(v)?.Value ?? FormatSlot(ctx, v),
        });
        return StackSlot.OfObject(ctx.MakeString(string.Join(separator, parts)));
    }

    // ---- System.Math ----

    private static void RegisterMath(IntrinsicRegistry r) {
        const string T = "System.Math";
        void S(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Static(T, name, ps), impl);

        S("Abs", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(Math.Abs(s.Int32(0))),
                StackKind.Int64 => StackSlot.OfInt64(Math.Abs(s.Int64(0))),
                _ => StackSlot.OfFloat(Math.Abs(s.Float(0))),
            };
        });
        S("Max", 2, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(Math.Max(s.Int32(0), s.Int32(1))),
                StackKind.Int64 => StackSlot.OfInt64(Math.Max(s.Int64(0), s.Int64(1))),
                _ => StackSlot.OfFloat(Math.Max(s.Float(0), s.Float(1))),
            };
        });
        S("Min", 2, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(Math.Min(s.Int32(0), s.Int32(1))),
                StackKind.Int64 => StackSlot.OfInt64(Math.Min(s.Int64(0), s.Int64(1))),
                _ => StackSlot.OfFloat(Math.Min(s.Float(0), s.Float(1))),
            };
        });
        S("Sqrt", 1, static (_, a) => StackSlot.OfFloat(Math.Sqrt(new Args(a).Float(0))));
        S("Pow", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfFloat(Math.Pow(s.Float(0), s.Float(1)));
        });
        S("Floor", 1, static (_, a) => StackSlot.OfFloat(Math.Floor(new Args(a).Float(0))));
        S("Ceiling", 1, static (_, a) => StackSlot.OfFloat(Math.Ceiling(new Args(a).Float(0))));
        S("Round", 1, static (_, a) => StackSlot.OfFloat(Math.Round(new Args(a).Float(0), MidpointRounding.ToEven)));
        S("Truncate", 1, static (_, a) => StackSlot.OfFloat(Math.Truncate(new Args(a).Float(0))));
        S("Sign", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(Math.Sign(s.Int32(0))),
                StackKind.Int64 => StackSlot.OfInt32(Math.Sign(s.Int64(0))),
                _ => StackSlot.OfInt32(Math.Sign(s.Float(0))),
            };
        });
        S("Sin", 1, static (_, a) => StackSlot.OfFloat(Math.Sin(new Args(a).Float(0))));
        S("Cos", 1, static (_, a) => StackSlot.OfFloat(Math.Cos(new Args(a).Float(0))));
        S("Tan", 1, static (_, a) => StackSlot.OfFloat(Math.Tan(new Args(a).Float(0))));
        S("Asin", 1, static (_, a) => StackSlot.OfFloat(Math.Asin(new Args(a).Float(0))));
        S("Acos", 1, static (_, a) => StackSlot.OfFloat(Math.Acos(new Args(a).Float(0))));
        S("Atan", 1, static (_, a) => StackSlot.OfFloat(Math.Atan(new Args(a).Float(0))));
        S("Atan2", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfFloat(Math.Atan2(s.Float(0), s.Float(1)));
        });
        S("Exp", 1, static (_, a) => StackSlot.OfFloat(Math.Exp(new Args(a).Float(0))));
        S("Log", 1, static (_, a) => StackSlot.OfFloat(Math.Log(new Args(a).Float(0))));
        S("Log", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfFloat(Math.Log(s.Float(0), s.Float(1)));
        });
        S("Log10", 1, static (_, a) => StackSlot.OfFloat(Math.Log10(new Args(a).Float(0))));
    }

    // ---- System.Console (仮想コンソールデバイス経由のみ) ----

    private static void RegisterConsole(IntrinsicRegistry r) {
        const string T = "System.Console";
        void S(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Static(T, name, ps), impl);

        // Write/WriteLine の全オーバーロードを同一キーに統合し、宣言パラメータ型
        // (ctx.ParameterTypeNames) で char / bool / object / params object[] 等を判別する
        foreach (var arity in new[] { 1, 2, 3, 4, 5 }) {
            S("Write", arity, WriteImpl(newline: false));
            S("WriteLine", arity, WriteImpl(newline: true));
        }
        S("WriteLine", 0, static (ctx, _) => {
            ctx.Console.Write(false, "\n");
            return null;
        });
        S("ReadLine", 0, static (ctx, _) => {
            var line = ctx.Console.ReadLine();
            return line is null ? StackSlot.Null : StackSlot.OfObject(ctx.MakeString(line));
        });
    }

    /// <summary>Console.Write/WriteLine 統合面。出力はすべて仮想コンソールデバイスへ。
    /// ホスト物理 I/O には直接触れない。</summary>
    private static IntrinsicImpl WriteImpl(bool newline) => (ctx, a) => {
        var text = a.Length switch {
            1 when ctx.ParamAt(0) == "System.Object[]" =>
                string.Concat(ReadObjectArray(a[0]).Select(v => FormatValue(ctx, v, "System.Object"))),
            1 => FormatValue(ctx, a[0], ctx.ParamAt(0)), // (char)/(bool)/(int)/(double)/(object)/(string)
            _ => FormatValues(ctx, new Args(a).String(0).Value, FormatArgs(ctx, a)),
        };
        ctx.Console.Write(false, newline ? text + "\n" : text);
        return null;
    };

    // ---- System.Convert ----

    private static void RegisterConvert(IntrinsicRegistry r) {
        const string T = "System.Convert";
        void S(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Static(T, name, ps), impl);

        S("ToBoolean", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(s.Int32(0) != 0 ? 1 : 0),
                StackKind.Int64 => StackSlot.OfInt32(s.Int64(0) != 0 ? 1 : 0),
                StackKind.Float => StackSlot.OfInt32(s.Float(0) != 0 ? 1 : 0),
                _ => StackSlot.OfInt32(bool.Parse(s.String(0).Value) ? 1 : 0),
            };
        });
        S("ToInt32", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(s.Int32(0)),
                StackKind.Int64 => StackSlot.OfInt32(Checked(s.Int64(0))),
                StackKind.Float => StackSlot.OfInt32((int)Math.Round(s.Float(0), MidpointRounding.ToEven)),
                StackKind.Object when s[0].ObjectValue is VmString str => StackSlot.OfInt32(int.Parse(str.Value)),
                _ => throw new InvalidOperationException($"Convert.ToInt32 未対応の入力型: {s[0].Kind}"),
            };
            static int Checked(long v) {
                if (v is < int.MinValue or > int.MaxValue)
                    throw new UnhandledGuestException("System.OverflowException", null);
                return (int)v;
            }
        });
        S("ToInt64", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt64(s.Int32(0)),
                StackKind.Int64 => StackSlot.OfInt64(s.Int64(0)),
                StackKind.Float => StackSlot.OfInt64((long)Math.Round(s.Float(0), MidpointRounding.ToEven)),
                StackKind.Object when s[0].ObjectValue is VmString str => StackSlot.OfInt64(long.Parse(str.Value)),
                _ => throw new InvalidOperationException($"Convert.ToInt64 未対応の入力型: {s[0].Kind}"),
            };
        });
        S("ToDouble", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfFloat(s.Int32(0)),
                StackKind.Int64 => StackSlot.OfFloat(s.Int64(0)),
                StackKind.Float => StackSlot.OfFloat(s.Float(0)),
                StackKind.Object when s[0].ObjectValue is VmString str => StackSlot.OfFloat(double.Parse(str.Value)),
                _ => throw new InvalidOperationException($"Convert.ToDouble 未対応の入力型: {s[0].Kind}"),
            };
        });
        S("ToString", 1, static (ctx, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfObject(ctx.MakeString(s.Int32(0).ToString())),
                StackKind.Int64 => StackSlot.OfObject(ctx.MakeString(s.Int64(0).ToString())),
                StackKind.Float => StackSlot.OfObject(ctx.MakeString(s.Float(0).ToString())),
                StackKind.Object when s[0].ObjectValue is VmString str => StackSlot.OfObject(str),
                _ => throw new InvalidOperationException($"Convert.ToString 未対応の入力型: {s[0].Kind}"),
            };
        });
        S("ToChar", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s[0].Kind switch {
                StackKind.Int32 => checked((char)s.Int32(0)),
                StackKind.Int64 => checked((char)s.Int64(0)),
                _ => throw new InvalidOperationException($"Convert.ToChar 未対応の入力型: {s[0].Kind}"),
            });
        });
        S("ToByte", 1, static (_, a) => ToIntegral("ToByte", new Args(a), byte.MinValue, byte.MaxValue));
        S("ToSByte", 1, static (_, a) => ToIntegral("ToSByte", new Args(a), sbyte.MinValue, sbyte.MaxValue));
        S("ToInt16", 1, static (_, a) => ToIntegral("ToInt16", new Args(a), short.MinValue, short.MaxValue));
        S("ToUInt16", 1, static (_, a) => ToIntegral("ToUInt16", new Args(a), ushort.MinValue, ushort.MaxValue));
        S("ToUInt32", 1, static (_, a) => ToIntegral("ToUInt32", new Args(a), uint.MinValue, uint.MaxValue));
        S("ToUInt64", 1, static (_, a) => ToUnsigned64("ToUInt64", new Args(a)));
        S("ToSingle", 1, static (_, a) => {
            var s = new Args(a);
            var value = s[0].Kind switch {
                StackKind.Int32 => (float)s.Int32(0),
                StackKind.Int64 => (float)s.Int64(0),
                StackKind.Float => (float)s.Float(0),
                StackKind.Object when s[0].ObjectValue is VmString str => float.Parse(str.Value),
                _ => throw new InvalidOperationException($"Convert.ToSingle 未対応の入力型: {s[0].Kind}"),
            };
            return StackSlot.OfFloat(value);
        });
    }

    /// <summary>Convert.ToXxx の共通核。入力 (i4/i8/f/10 進文字列) を丸めて 64bit 整数にし、
    /// 範囲外ならゲスト例外 System.OverflowException として投げる (CLR の Convert と同じ)。</summary>
    private static StackSlot ToIntegral(string target, Args s, long min, long max) {
        var value = s[0].Kind switch {
            StackKind.Int32 => s.Int32(0),
            StackKind.Int64 => s.Int64(0),
            StackKind.Float => (long)Math.Round(s.Float(0), MidpointRounding.ToEven),
            StackKind.Object when s[0].ObjectValue is VmString str => long.Parse(str.Value),
            _ => throw new InvalidOperationException($"Convert.{target} 未対応の入力型: {s[0].Kind}"),
        };
        if (value < min || value > max)
            throw new UnhandledGuestException("System.OverflowException", null);
        // i4 スロットには bit パターンを格納する (u4 値は (int) でラップ。読み手が u4 として解釈)
        return StackSlot.OfInt32((int)value);
    }

    /// <summary>Convert.ToUInt64 の共通核 (戻り値のみ i8 スロット)。入力が負整数なら CLR 同様 OverflowException。</summary>
    private static StackSlot ToUnsigned64(string target, Args s) {
        if (s[0].Kind is StackKind.Int32 or StackKind.Int64 or StackKind.NativeInt &&
            (s[0].Kind == StackKind.Int64 ? s.Int64(0) : s.Int32(0)) < 0)
            throw new UnhandledGuestException("System.OverflowException", null);
        var value = s[0].Kind switch {
            StackKind.Int32 => (ulong)(long)s.Int32(0),
            StackKind.Int64 or StackKind.NativeInt => (ulong)s.Int64(0),
            StackKind.Float => (ulong)Math.Round(s.Float(0), MidpointRounding.ToEven), // 負/範囲外の浮動小数は変換時点で例外
            StackKind.Object when s[0].ObjectValue is VmString str => ulong.Parse(str.Value),
            _ => throw new InvalidOperationException($"Convert.{target} 未対応の入力型: {s[0].Kind}"),
        };
        return StackSlot.OfInt64((long)value);
    }
}
