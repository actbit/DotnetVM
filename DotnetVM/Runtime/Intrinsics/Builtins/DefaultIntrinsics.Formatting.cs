using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
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

    /// <summary>プリミティブ スロットの CLR 互換 ToString (instance ToString 面 / ボックス化
    /// プリミティブへの仮想呼出のバインド用)。callvirt のレシーバは box 済み VmBoxedValue
    /// (Object スロット)、constrained. 経由では正規化後の生スロットのどちらかで来るため、
    /// 箱なら中身と型名を取り出す。primitiveTypeName で i4 スロットに統合される char / bool /
    /// 符号なしの書式を判別する (CLR の int.ToString() 等は CurrentCulture)。</summary>
    internal static string FormatPrimitiveToString(in StackSlot slot, string primitiveTypeName) {
        var value = slot;
        if (slot.Kind == StackKind.Object && slot.ObjectValue is VmBoxedValue boxed) {
            primitiveTypeName = boxed.Type.FullName;
            value = boxed.Fields[0];
        }
        return primitiveTypeName switch {
            "System.Int32" => ((int)value.Int64Value).ToString(),
            "System.UInt32" => ((uint)value.Int64Value).ToString(),
            "System.Int64" => value.Int64Value.ToString(),
            "System.UInt64" => ((ulong)value.Int64Value).ToString(),
            "System.Int16" => ((short)value.Int64Value).ToString(),
            "System.UInt16" => ((ushort)value.Int64Value).ToString(),
            "System.SByte" => ((sbyte)value.Int64Value).ToString(),
            "System.Byte" => ((byte)value.Int64Value).ToString(),
            "System.Boolean" => value.Int64Value != 0 ? "True" : "False",
            "System.Char" => ((char)value.Int64Value).ToString(),
            "System.Single" => ((float)value.DoubleValue).ToString(),
            "System.Double" => value.DoubleValue.ToString(),
            _ => value.Int64Value.ToString(),
        };
    }

    /// <summary>String.Concat の配列面 (CoreLibBindings と共有) の要素フォーマット。
    /// CLR 規約どおり null は空文字列化する。</summary>
    internal static string ConcatFormat(IntrinsicContext ctx, in StackSlot slot) => FormatSlot(ctx, slot);

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
                VmBoxedValue b => b.Type is VmClassType { IsEnum: true }
                    ? CoreLibBindings.FormatEnumForConcat(b)
                    : FormatBoxed(b),
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
            a[0].ObjectValue is VmByRef byRef && byRef.Read().ObjectValue is VmIntrinsicCarrier carrier
                ? (System.Text.StringBuilder)carrier.Payload!
                : throw new InvalidOperationException("DefaultInterpolatedStringHandler の this が初期化されていません。");
        // this (ByRef) → 状態バッファを書き戻す
        static void SetState(StackSlot[] a, object? payload) {
            if (a[0].ObjectValue is VmByRef byRef)
                byRef.Write(StackSlot.OfObject(new VmIntrinsicCarrier { Payload = payload }));
        }

        void I(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Instance(T, name, ps), impl);

        I(".ctor", 2, static (_, a) => { SetState(a, new System.Text.StringBuilder()); return null; });
        I(".ctor", 3, static (_, a) => { SetState(a, new System.Text.StringBuilder()); return null; }); // +IFormatProvider (カルチャは CurrentCulture)
        I("AppendLiteral", 1, static (ctx, a) => {
            var text = new Args(a).String(1).Value;
            ctx.Heap.ChargeHostBuffer(text.Length); // ホスト側 StringBuilder もメモリ会計の対象
            State(a).Append(text);
            return null;
        });
        I("AppendFormatted", 1, static (ctx, a) => {
            var text = FormatValue(ctx, a[1], ctx.ParamAt(0));
            ctx.Heap.ChargeHostBuffer(text.Length);
            State(a).Append(text);
            return null;
        });
        I("AppendFormatted", 2, static (ctx, a) => {
            // (T, string format) と (T, int alignment) の 2 overload を宣言型で判別
            var s = State(a);
            var text = FormatValue(ctx, a[1], ctx.ParamAt(0));
            if (ctx.ParamAt(1) == "System.String")
                text = ApplyFormatSpecifier(text, a[1], new Args(a).String(2).Value ?? "");
            else if (ctx.ParamAt(1) == "System.Int32")
                text = ApplyAlignment(text, new Args(a).Int32(2));
            else
                throw new InvalidOperationException($"AppendFormatted の第2引数型 {ctx.ParamAt(1)} は未対応です。");
            ctx.Heap.ChargeHostBuffer(text.Length);
            s.Append(text);
            return null;
        });
        I("AppendFormatted", 3, static (ctx, a) => {
            // (T, int alignment, string format)
            var s = State(a);
            var text = FormatValue(ctx, a[1], ctx.ParamAt(0));
            text = ApplyAlignment(text, new Args(a).Int32(2));
            text = ApplyFormatSpecifier(text, a[1], new Args(a).String(3).Value ?? "");
            ctx.Heap.ChargeHostBuffer(text.Length);
            s.Append(text);
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

    /// <summary>String.Format 統合面 (第1引数が書式、残りが値。末尾 params object[] は展開)。
    /// C5 からは CoreLibBindings 側のランタイムバインド (culture 依存面) からも再利用する。</summary>
    internal static StackSlot? FormatImpl(IntrinsicContext ctx, StackSlot[] a) =>
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
}
