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

    /// <summary>ボックス化された値の CLR 互換文字列化 (Object.ToString / String.Concat(object,...) 共通)。</summary>
    private static string FormatBoxed(VmBoxedValue boxed) {
        var slot = boxed.Fields[0];
        return boxed.Type.FullName switch {
            "System.Int32" => ((int)slot.Int64Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "System.UInt32" => ((uint)slot.Int64Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "System.Int64" => slot.Int64Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "System.UInt64" => ((ulong)slot.Int64Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "System.Int16" => ((short)slot.Int64Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "System.UInt16" => ((ushort)slot.Int64Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "System.SByte" => ((sbyte)slot.Int64Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "System.Byte" => ((byte)slot.Int64Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "System.Boolean" => slot.Int64Value != 0 ? "True" : "False",
            "System.Char" => ((char)slot.Int64Value).ToString(),
            "System.Single" => ((float)slot.DoubleValue).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "System.Double" => slot.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => boxed.Type.FullName,
        };
    }

    /// <summary>スタックスロットの CLR 互換文字列化 (String.Concat(object,...) の引数用)。</summary>
    private static string FormatSlot(in StackSlot slot) => slot.Kind switch {
        StackKind.Object => slot.ObjectValue switch {
            null => "", // String.Concat(object) は null を空文字列にする
            VmString s => s.Value,
            VmBoxedValue b => FormatBoxed(b),
            VmClassInstance ci => ci.ClassType.FullName,
            VmExceptionObject e => FormatExceptionText(e.Type.FullName, e.Message),
            VmArray arr => arr.ArrayType.FullName,
            var other => other.ToString() ?? "",
        },
        StackKind.Int32 or StackKind.NativeInt => slot.Int64Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StackKind.Int64 => slot.Int64Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StackKind.Float => slot.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StackKind.ValueType => slot.ObjectValue?.ToString() ?? "",
        _ => "",
    };

    /// <summary>VM 起動時に標準 intrinsic をすべて登録する。</summary>
    public static void RegisterAll(IntrinsicRegistry registry) {
        RegisterObject(registry);
        RegisterString(registry);
        RegisterPrimitiveToString(registry);
        RegisterMath(registry);
        RegisterConsole(registry);
        RegisterConvert(registry);
        RegisterExceptions(registry);
        RegisterDisposable(registry);
        RegisterRuntimeHelpers(registry);
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

    /// <summary>プリミティブ型の instance ToString() (int.ToString() 等の直接呼出用)。</summary>
    private static void RegisterPrimitiveToString(IntrinsicRegistry registry) {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        void S(string type, Func<StackSlot, string> format) =>
            registry.Register(IntrinsicKey.Instance(type, "ToString", 0),
                (ctx, a) => StackSlot.OfObject(ctx.MakeString(format(a[0]))));

        S("System.Int32", s => ((int)s.Int64Value).ToString(inv));
        S("System.UInt32", s => ((uint)s.Int64Value).ToString(inv));
        S("System.Int64", s => s.Int64Value.ToString(inv));
        S("System.UInt64", s => ((ulong)s.Int64Value).ToString(inv));
        S("System.Int16", s => ((short)s.Int64Value).ToString(inv));
        S("System.UInt16", s => ((ushort)s.Int64Value).ToString(inv));
        S("System.SByte", s => ((sbyte)s.Int64Value).ToString(inv));
        S("System.Byte", s => ((byte)s.Int64Value).ToString(inv));
        S("System.Boolean", s => s.Int64Value != 0 ? "True" : "False");
        S("System.Char", s => ((char)s.Int64Value).ToString());
        S("System.Single", s => ((float)s.DoubleValue).ToString(inv));
        S("System.Double", s => s.DoubleValue.ToString(inv));
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
            return StackSlot.OfInt32(string.CompareOrdinal(s.String(0).Value, s.String(1).Value));
        });
        Instance("Contains", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value.Contains(s.String(1).Value) ? 1 : 0);
        });
        Instance("StartsWith", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value.StartsWith(s.String(1).Value, StringComparison.Ordinal) ? 1 : 0);
        });
        Instance("EndsWith", 1, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfInt32(s.String(0).Value.EndsWith(s.String(1).Value, StringComparison.Ordinal) ? 1 : 0);
        });
        Instance("IndexOf", 1, static (_, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            return s[1].ObjectValue is VmString needle
                ? StackSlot.OfInt32(value.IndexOf(needle.Value, StringComparison.Ordinal))
                : StackSlot.OfInt32(value.IndexOf(s.Char(1)));
        });
        Instance("LastIndexOf", 1, static (_, a) => {
            var s = new Args(a);
            var value = s.String(0).Value;
            return s[1].ObjectValue is VmString needle
                ? StackSlot.OfInt32(value.LastIndexOf(needle.Value, StringComparison.Ordinal))
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
            StackSlot.OfObject(ctx.MakeString(FormatSlot(a[0]) + FormatSlot(a[1]))));
        Static("Concat", 3, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(FormatSlot(a[0]) + FormatSlot(a[1]) + FormatSlot(a[2]))));
        Static("Concat", 4, static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(FormatSlot(a[0]) + FormatSlot(a[1]) + FormatSlot(a[2]) + FormatSlot(a[3]))));
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

        // 出力はすべて仮想コンソールデバイスへ。ホスト物理 I/O には直接触れない。
        S("Write", 1, static (ctx, a) => {
            ctx.Console.Write(false, Format(a[0]));
            return null;
        });
        S("WriteLine", 0, static (ctx, _) => {
            ctx.Console.Write(false, "\n");
            return null;
        });
        S("WriteLine", 1, static (ctx, a) => {
            ctx.Console.Write(false, Format(a[0]) + "\n");
            return null;
        });
        S("ReadLine", 0, static (ctx, _) => {
            var line = ctx.Console.ReadLine();
            return line is null ? StackSlot.Null : StackSlot.OfObject(ctx.MakeString(line));
        });
    }

    /// <summary>Console 出力用の値の文字列化 (VM オブジェクトモデル内で完結)。</summary>
    private static string Format(in StackSlot slot) => slot.Kind switch {
        StackKind.Int32 => slot.Int64Value.ToString(),
        StackKind.Int64 or StackKind.NativeInt => slot.Int64Value.ToString(),
        StackKind.Float => slot.DoubleValue.ToString(),
        StackKind.Object => slot.ObjectValue switch {
            null => "",
            VmString s => s.Value,
            var other => throw new NotSupportedException(
                $"Console 出力のオブジェクト {other.GetType().Name} の文字列化はオブジェクトモデル (M3) 以降で対応します。"),
        },
        _ => throw new InvalidOperationException($"Console 出力できないスタック型です: {slot.Kind}"),
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
    }
}
