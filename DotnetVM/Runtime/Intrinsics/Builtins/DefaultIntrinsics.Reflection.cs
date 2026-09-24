using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
    // ---- System.Type / System.Reflection.MethodBase (typeof / GetType / GetCurrentMethod 面) ----

    /// <summary>レシーバの実行時型を得る (Object.GetType() 用)。</summary>
    internal static VmType RuntimeTypeOf(IntrinsicContext ctx, in StackSlot slot) =>
        slot.ObjectValue switch {
            VmClassInstance ci => ci.TypeArguments.Length > 0
                ? new VmConstructedType { Definition = ci.ClassType, TypeArguments = ci.TypeArguments }
                : (VmType)ci.ClassType,
            VmBoxedValue bv => bv.Type,
            VmArray arr => arr.ArrayType,
            // C5.5 Wave 4: Object.ToString の IL 化で "str".GetType() がこの面を辿る。
            // VmString の実行時型は System.String (旧 ③ intrinsic の「VmString は自分自身」
            // 特殊化に相当する型同一性)
            VmString => ctx.Types.FindIntrinsicType("System.String")
                ?? throw new InvalidOperationException("ファサード型 System.String が未登録です。"),
            VmExceptionObject e => e.Type,
            VmAssemblyObject assembly => assembly.Type,
            VmAssemblyLoadContext loadContext => loadContext.Type,
            VmAssemblyNameObject assemblyName => assemblyName.Type,
            VmMemoryStreamObject stream => stream.Type,
            VmRuntimeObject rt => rt.Target,
            null => throw new UnhandledGuestException("System.NullReferenceException", null),
            _ => ctx.Types.FindIntrinsicType("System.Object")
                ?? throw new InvalidOperationException("ファサード型 System.Object が未登録です。"),
        };

    /// <summary>System.Type ファサードの実体を生成する (typeof(X) / GetType() の戻り値)。
    /// 実 CLR の RuntimeType と同じく型ごとに単一実体 (VM 単位でインターンする)。
    /// CoreLib IL が bne.un 等の参照同一性で型分岐するため、都度 new では誤分岐する。</summary>
    internal static StackSlot MakeRuntimeObject(IntrinsicContext ctx, VmType type) {
        lock (ctx.Shared.TypeFacadeGate) {
        if (!ctx.Shared.TypeFacades.TryGetValue(type, out var facade)) {
            facade = ctx.Heap.Allocate(new VmRuntimeObject { Target = type });
            ctx.Shared.TypeFacades[type] = facade;
        }
        return StackSlot.OfObject(facade);
        }
    }

    /// <summary>オブジェクトの実行時型ファサードをスロットで返す (Object.GetType() /
    /// RuntimeHelpers::GetMethodTable 等の共通面)。</summary>
    internal static StackSlot TypeFacadeOf(IntrinsicContext ctx, in StackSlot slot) =>
        MakeRuntimeObject(ctx, RuntimeTypeOf(ctx, slot));

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
                    VmRuntimeField runtimeField => runtimeField.Target.Name,
                    VmRuntimeProperty runtimeProperty => runtimeProperty.Name,
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

        // Reflection の戻り値は CLR の RuntimeMethodInfo/RuntimeFieldInfo ではなく、
        // VM のメタデータオブジェクトを返す。式木と DynamicMethod の引数にもそのまま渡せる。
        r.Register(IntrinsicKey.Instance(T, "GetMethod", 1), static (ctx, a) =>
            MakeMethodObject(ctx, ((VmRuntimeObject)a[0].ObjectValue!).Target,
                (a[1].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null), null));
        r.Register(IntrinsicKey.Instance(T, "GetMethod", 2), static (ctx, a) =>
            MakeMethodObject(ctx, ((VmRuntimeObject)a[0].ObjectValue!).Target,
                (a[1].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null), a[2]));
        r.Register(IntrinsicKey.Instance(T, "GetConstructor", 1), static (ctx, a) =>
            MakeMethodObject(ctx, ((VmRuntimeObject)a[0].ObjectValue!).Target, ".ctor", a[1]));
        r.Register(IntrinsicKey.Instance(T, "GetMethods", 0), static (ctx, a) =>
            MakeMethodArray(ctx, ((VmRuntimeObject)a[0].ObjectValue!).Target));
        r.Register(IntrinsicKey.Instance(T, "GetField", 1), static (ctx, a) =>
            MakeFieldObject(ctx, ((VmRuntimeObject)a[0].ObjectValue!).Target,
                (a[1].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null)));
        r.Register(IntrinsicKey.Instance(T, "GetProperty", 1), static (ctx, a) =>
            MakePropertyObject(ctx, ((VmRuntimeObject)a[0].ObjectValue!).Target,
                (a[1].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null)));
        r.Register(IntrinsicKey.Instance(T, "GetFields", 0), static (ctx, a) =>
            MakeFieldArray(ctx, ((VmRuntimeObject)a[0].ObjectValue!).Target));
    }

    internal static StackSlot MakeMethodObject(IntrinsicContext ctx, VmType type, string name, StackSlot? parameterTypes) {
        var expectedCount = parameterTypes is { ObjectValue: VmArray array } ? array.Length : -1;
        for (VmType? current = type; current is not null; current = current.BaseType) {
            var method = current.Methods.FirstOrDefault(candidate =>
                candidate.Name == name && (expectedCount < 0 || candidate.Signature.ParamTypes.Length == expectedCount));
            if (method is not null)
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeMethod { Target = method }));
        }
        return StackSlot.Null;
    }

    internal static StackSlot MakeMethodArray(IntrinsicContext ctx, VmType type) {
        var values = new List<StackSlot>();
        for (VmType? current = type; current is not null; current = current.BaseType)
            foreach (var method in current.Methods)
                values.Add(StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeMethod { Target = method })));
        return StackSlot.OfObject(ctx.MakeObjectArray(values));
    }

    internal static StackSlot MakeFieldObject(IntrinsicContext ctx, VmType type, string name) {
        for (VmType? current = type; current is not null; current = current.BaseType)
            if (current.Fields.FirstOrDefault(field => field.Name == name) is { } field)
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeField { Target = field }));
        return StackSlot.Null;
    }

    internal static StackSlot MakeFieldArray(IntrinsicContext ctx, VmType type) {
        var values = new List<StackSlot>();
        for (VmType? current = type; current is not null; current = current.BaseType)
            foreach (var field in current.Fields)
                values.Add(StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeField { Target = field })));
        return StackSlot.OfObject(ctx.MakeObjectArray(values));
    }

    internal static StackSlot MakePropertyObject(IntrinsicContext ctx, VmType type, string name) {
        for (VmType? current = type; current is not null; current = current.BaseType)
            if (current.Methods.FirstOrDefault(method => method.Name == "get_" + name && method.Signature.ParamTypes.Length == 0) is { } getter)
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmRuntimeProperty { Name = name, Getter = getter }));
        return StackSlot.Null;
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

    /// <summary>Assembly は CLR の Assembly オブジェクトではなく、VM 内の TypeLoader を包んで扱う。</summary>
    internal static StackSlot LoadAssembly(IntrinsicContext ctx, StackSlot[] args) {
        if (args[0].ObjectValue is VmAssemblyNameObject assemblyName || args[0].ObjectValue is VmString) {
            var simpleName = args[0].ObjectValue is VmAssemblyNameObject named
                ? named.Name
                : SimpleAssemblyName(((VmString)args[0].ObjectValue!).Value);
            var namedLoader = ctx.Types.Context?.FindBySimpleName(simpleName)
                ?? throw new UnhandledGuestException("System.IO.FileNotFoundException",
                    $"アセンブリ '{simpleName}' が見つかりません。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmAssemblyObject { Loader = namedLoader }));
        }
        if (args[0].ObjectValue is not VmArray bytes)
            throw new UnhandledGuestException("System.ArgumentNullException", null);
        if (ctx.MemoryPolicy is { } policy && bytes.Length > policy.MaxAssemblyBytes)
            throw new OperationNotAllowedException(
                $"Assembly.Load の入力が上限を超えています (上限 {policy.MaxAssemblyBytes:N0} バイト)。");
        ctx.Heap.ChargeHostBuffer(bytes.Length);
        var imageBytes = ctx.ReadByteArray(args[0]);
        var loader = ctx.LoadAssemblyFromBytes?.Invoke(imageBytes)
            ?? throw new OperationNotAllowedException("Assembly.Load は VM の動的ローダーから利用できません。");
        return StackSlot.OfObject(ctx.Heap.Allocate(new VmAssemblyObject { Loader = loader }));
    }

    private static string SimpleAssemblyName(string fullName) {
        var comma = fullName.IndexOf(',');
        return (comma < 0 ? fullName : fullName[..comma]).Trim();
    }

    private static void RegisterAssembly(IntrinsicRegistry r) {
        const string T = "System.Reflection.Assembly";
        r.Register(IntrinsicKey.Static(T, "Load", 1), static (ctx, a) => LoadAssembly(ctx, a));
        r.Register(IntrinsicKey.Static(T, "Load", 2), static (ctx, a) => LoadAssembly(ctx, a));
        r.Register(IntrinsicKey.Instance(T, "get_FullName", 0), static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(((VmAssemblyObject)a[0].ObjectValue!).Loader.Image.Identity.ToString())));
        r.Register(IntrinsicKey.Instance(T, "get_Location", 0), static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(((VmAssemblyObject)a[0].ObjectValue!).Loader.Image.SourcePath ?? "")));
        r.Register(IntrinsicKey.Instance(T, "GetType", 1), static (ctx, a) => {
            var assembly = (VmAssemblyObject)a[0].ObjectValue!;
            var name = (a[1].ObjectValue as VmString)?.Value
                ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
            return assembly.Loader.FindTypeByFullName(name) is { } type
                ? MakeRuntimeObject(ctx, type)
                : StackSlot.Null;
        });
        r.Register(IntrinsicKey.Instance(T, "GetType", 2), static (ctx, a) => {
            var assembly = (VmAssemblyObject)a[0].ObjectValue!;
            var name = (a[1].ObjectValue as VmString)?.Value
                ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
            if (assembly.Loader.FindTypeByFullName(name) is { } type)
                return MakeRuntimeObject(ctx, type);
            if (a[2].AsInt32 != 0)
                throw new UnhandledGuestException("System.TypeLoadException", $"型 '{name}' が見つかりません。");
            return StackSlot.Null;
        });
        r.Register(IntrinsicKey.Instance(T, "GetTypes", 0), static (ctx, a) => {
            var loader = ((VmAssemblyObject)a[0].ObjectValue!).Loader;
            var types = new List<VmType>();
            for (var rid = 1; rid <= loader.Image.Tables.GetRowCount(TableKind.TypeDef); rid++) {
                var type = loader.GetTypeDef(rid);
                if (type.FullName != "<Module>")
                    types.Add(type);
            }
            var elementType = (VmType?)ctx.Types.FindTypeByFullName("System.Type")
                ?? ctx.Types.FindIntrinsicType("System.Type")
                ?? throw new InvalidOperationException("System.Type が解決できません。");
            var values = types.Select(type => MakeRuntimeObject(ctx, type)).ToArray();
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmArray(
                new VmArrayType { ElementType = elementType }, values)));
        });
    }

    private static void RegisterExpressionTrees(IntrinsicRegistry r) {
        const string T = "System.Linq.Expressions.Expression";
        static VmExpressionObject Node(StackSlot slot) => slot.ObjectValue as VmExpressionObject
            ?? throw new UnhandledGuestException("System.ArgumentException", "引数は VM 式木ノードである必要があります。");
        static VmType Type(IntrinsicContext ctx, StackSlot slot) => slot.ObjectValue is VmRuntimeObject runtimeType
            ? runtimeType.Target
            : throw new UnhandledGuestException("System.ArgumentException", "Type 引数は VM の型情報である必要があります。");
        static StackSlot ConstantValue(StackSlot slot) => slot.ObjectValue is VmBoxedValue boxed && boxed.Fields.Length == 1
            ? boxed.Fields[0] : slot;

        r.Register(IntrinsicKey.Static(T, "Constant", 1), static (ctx, a) => {
            var value = ConstantValue(a[0]);
            var type = value.ObjectValue is null
                ? ctx.Types.FindIntrinsicType("System.Object")!
                : RuntimeTypeOf(ctx, value);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Constant, Constant = value, ResultType = type,
            }));
        });
        r.Register(IntrinsicKey.Static(T, "Constant", 2), static (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Constant, Constant = ConstantValue(a[0]), ResultType = Type(ctx, a[1]),
            })));
        r.Register(IntrinsicKey.Static(T, "Parameter", 1), static (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = Type(ctx, a[0]),
            })));
        r.Register(IntrinsicKey.Static(T, "Parameter", 2), static (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = Type(ctx, a[0]),
            })));

        void Binary(string name, VmExpressionKind kind) =>
            r.Register(IntrinsicKey.Static(T, name, 2), (ctx, a) => {
                var left = Node(a[0]);
                var right = Node(a[1]);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = left, Right = right, ResultType = left.ResultType,
                }));
            });
        Binary("Add", VmExpressionKind.Add);
        Binary("Subtract", VmExpressionKind.Subtract);
        Binary("Multiply", VmExpressionKind.Multiply);
        Binary("Divide", VmExpressionKind.Divide);

        foreach (var (name, kind) in new[] {
            ("AddChecked", VmExpressionKind.Add), ("SubtractChecked", VmExpressionKind.Subtract),
            ("MultiplyChecked", VmExpressionKind.Multiply), ("Modulo", VmExpressionKind.Modulo),
            ("And", VmExpressionKind.And), ("Or", VmExpressionKind.Or),
            ("ExclusiveOr", VmExpressionKind.ExclusiveOr), ("AndAlso", VmExpressionKind.AndAlso),
            ("OrElse", VmExpressionKind.OrElse), ("Equal", VmExpressionKind.Equal),
            ("NotEqual", VmExpressionKind.NotEqual), ("GreaterThan", VmExpressionKind.GreaterThan),
            ("GreaterThanOrEqual", VmExpressionKind.GreaterThanOrEqual), ("LessThan", VmExpressionKind.LessThan),
            ("LessThanOrEqual", VmExpressionKind.LessThanOrEqual),
        })
            r.RegisterBinding(BindingKey.StaticAnyParams(T, name), (ctx, a) => {
                if (a.Length < 2)
                    throw new UnhandledGuestException("System.ArgumentException", $"Expression.{name} の引数が不足しています。");
                var left = Node(a[0]);
                var right = Node(a[1]);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = left, Right = right, ResultType = left.ResultType,
                }));
            }, BindingOrigin.Managed);

        foreach (var (name, kind) in new[] {
            ("Negate", VmExpressionKind.Negate), ("NegateChecked", VmExpressionKind.Negate),
            ("UnaryPlus", VmExpressionKind.Convert), ("Not", VmExpressionKind.Not),
        })
            r.RegisterBinding(BindingKey.StaticAnyParams(T, name), (ctx, a) => {
                var operand = Node(a[0]);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = operand, ResultType = operand.ResultType,
                }));
            }, BindingOrigin.Managed);

        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Convert"), (ctx, a) => {
            var operand = Node(a[0]);
            var target = a.Length > 1 ? Type(ctx, a[1]) : operand.ResultType;
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Convert, Left = operand, ResultType = target,
            }));
        }, BindingOrigin.Managed);
        foreach (var (name, kind) in new[] { ("TypeIs", VmExpressionKind.TypeIs), ("TypeAs", VmExpressionKind.TypeAs) })
            r.RegisterBinding(BindingKey.StaticAnyParams(T, name), (ctx, a) => {
                var operand = Node(a[0]);
                var target = Type(ctx, a[1]);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = operand, NewType = target,
                    ResultType = kind == VmExpressionKind.TypeIs ? ctx.Types.FindIntrinsicType("System.Boolean") : target,
                }));
            }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "ArrayIndex"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.ArrayIndex, Left = Node(a[0]), Right = Node(a[1]),
                ResultType = Node(a[0]).ResultType,
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "ArrayLength"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.ArrayLength, Left = Node(a[0]),
                ResultType = ctx.Types.FindIntrinsicType("System.Int32"),
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Condition"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Conditional, Left = Node(a[0]),
                IfTrue = Node(a[1]), IfFalse = Node(a[2]), ResultType = Node(a[1]).ResultType,
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Assign"), (ctx, a) => {
            var target = Node(a[0]);
            var value = Node(a[1]);
            if (target.Kind != VmExpressionKind.Parameter)
                throw new UnhandledGuestException("System.ArgumentException", "Expression.Assign の左辺は ParameterExpression である必要があります。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Assign, Left = target, Right = value, ResultType = value.ResultType,
            }));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Variable"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = Type(ctx, a[0]), IsVariable = true,
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Default"), (ctx, a) => {
            var type = Type(ctx, a[0]);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Default, ResultType = type, NewType = type,
            }));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Quote"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Quote, Object = Node(a[0]), ResultType = Node(a[0]).ResultType,
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "ArrayAccess"), (ctx, a) => {
            var indexes = Nodes(a[^1]);
            if (indexes.Length != 1)
                throw new UnhandledGuestException("System.NotSupportedException", "式木の多次元配列アクセスは未対応です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.ArrayIndex, Left = Node(a[0]), Right = indexes[0],
                ResultType = Node(a[0]).ResultType,
            }));
        }, BindingOrigin.Managed);

        static VmExpressionObject[] Nodes(StackSlot slot) => slot.ObjectValue switch {
            VmArray array => array.Elements.Select(Node).ToArray(),
            null => [],
            _ => throw new UnhandledGuestException("System.ArgumentException", "式木引数配列が不正です。"),
        };
        r.Register(IntrinsicKey.Static(T, "Assign", 2), static (ctx, a) => {
            var target = Node(a[0]);
            var value = Node(a[1]);
            if (target.Kind != VmExpressionKind.Parameter)
                throw new UnhandledGuestException("System.ArgumentException", "Expression.Assign の左辺は ParameterExpression である必要があります。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Assign, Left = target, Right = value, ResultType = value.ResultType,
            }));
        });
        r.Register(IntrinsicKey.Static(T, "Variable", 1), static (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = Type(ctx, a[0]), IsVariable = true,
            })));
        r.Register(IntrinsicKey.Static(T, "Variable", 2), static (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = Type(ctx, a[0]), IsVariable = true,
            })));
        r.Register(IntrinsicKey.Static(T, "Default", 1), static (ctx, a) => {
            var type = Type(ctx, a[0]);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Default, ResultType = type, NewType = type,
            }));
        });
        r.Register(IntrinsicKey.Static(T, "Quote", 1), static (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Quote, Object = Node(a[0]), ResultType = Node(a[0]).ResultType,
            })));
        r.Register(IntrinsicKey.Static(T, "ArrayAccess", 2), static (ctx, a) => {
            var indexes = Nodes(a[1]);
            if (indexes.Length != 1)
                throw new UnhandledGuestException("System.NotSupportedException", "式木の多次元配列アクセスは未対応です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.ArrayIndex, Left = Node(a[0]), Right = indexes[0],
                ResultType = Node(a[0]).ResultType,
            }));
        });
        r.Register(IntrinsicKey.Static(T, "Block", 1), static (ctx, a) => {
            var expressions = Nodes(a[0]);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Block, Expressions = expressions,
                ResultType = expressions.Length == 0 ? ctx.Types.FindIntrinsicType("System.Void") : expressions[^1].ResultType,
            }));
        });
        r.Register(IntrinsicKey.Static(T, "Block", 2), static (ctx, a) => {
            var expressions = Nodes(a[1]);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Block, Expressions = expressions,
                ResultType = expressions.Length == 0 ? ctx.Types.FindIntrinsicType("System.Void") : expressions[^1].ResultType,
            }));
        });
        foreach (var name in new[] { "NewArrayInit", "NewArrayBounds" })
            r.Register(IntrinsicKey.Static(T, name, 2), (ctx, a) => {
                var elementType = Type(ctx, a[0]);
                var values = Nodes(a[1]);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = VmExpressionKind.NewArray, NewType = elementType, Arguments = values,
                    NewArrayBounds = name == "NewArrayBounds", ResultType = new VmArrayType { ElementType = elementType },
                }));
            });
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Block"), (ctx, a) => {
            var expressions = a.Length == 0 ? [] : Nodes(a[^1]);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Block, Expressions = expressions,
                ResultType = expressions.Length == 0 ? ctx.Types.FindIntrinsicType("System.Void") : expressions[^1].ResultType,
            }));
        }, BindingOrigin.Managed);
        foreach (var name in new[] { "NewArrayInit", "NewArrayBounds" })
            r.RegisterBinding(BindingKey.StaticAnyParams(T, name), (ctx, a) => {
                var elementType = Type(ctx, a[0]);
                var values = a.Length > 1 ? Nodes(a[^1]) : [];
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = VmExpressionKind.NewArray, NewType = elementType, Arguments = values,
                    NewArrayBounds = name == "NewArrayBounds",
                    ResultType = new VmArrayType { ElementType = elementType },
                }));
            }, BindingOrigin.Managed);
        static VmMethod Method(StackSlot slot) => slot.ObjectValue is VmRuntimeMethod runtimeMethod
            ? runtimeMethod.Target
            : throw new UnhandledGuestException("System.ArgumentException", "MethodInfo が必要です。");
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Call"), (ctx, a) => {
            VmExpressionObject? receiver = null;
            VmMethod method;
            VmExpressionObject[] arguments;
            if (a[0].ObjectValue is VmRuntimeMethod directMethod) {
                method = directMethod.Target;
                arguments = a.Length > 1 ? Nodes(a[^1]) : [];
            } else {
                receiver = Node(a[0]);
                method = Method(a[1]);
                arguments = a.Length > 2 ? Nodes(a[^1]) : [];
            }
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Call, Object = receiver, Method = method, Arguments = arguments,
                ResultType = ctx.Types.FindIntrinsicType("System.Object"),
            }));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "New"), (ctx, a) => {
            var ctor = a[0].ObjectValue is VmRuntimeMethod runtimeMethod
                ? runtimeMethod.Target
                : throw new UnhandledGuestException("System.ArgumentException", "ConstructorInfo が必要です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.New, Method = ctor, NewType = ctor.DeclaringType,
                Arguments = a.Length > 1 ? Nodes(a[^1]) : [], ResultType = ctor.DeclaringType,
            }));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Invoke"), (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Invoke, Object = Node(a[0]),
                Arguments = a.Length > 1 ? Nodes(a[^1]) : [], ResultType = ctx.Types.FindIntrinsicType("System.Object"),
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Field"), (ctx, a) => {
            var receiver = a.Length > 1 && a[0].ObjectValue is VmExpressionObject ? Node(a[0]) : null;
            var ownerType = receiver?.ResultType ?? (a.Length > 1 && a[0].ObjectValue is VmRuntimeObject type ? type.Target : null);
            var field = a[^1].ObjectValue is VmRuntimeField runtimeField
                ? runtimeField.Target
                : ownerType is { } owner && a[^1].ObjectValue is VmString fieldName
                    ? (MakeFieldObject(ctx, owner, fieldName.Value).ObjectValue as VmRuntimeField)?.Target
                    : null;
            if (field is null)
                throw new UnhandledGuestException("System.ArgumentException", "FieldInfo が必要です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.MemberAccess, Object = receiver, Field = field,
                ResultType = field.FieldType,
            }));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Property"), (ctx, a) => {
            var receiver = a.Length > 1 && a[0].ObjectValue is VmExpressionObject ? Node(a[0]) : null;
            var ownerType = receiver?.ResultType ?? (a.Length > 1 && a[0].ObjectValue is VmRuntimeObject type ? type.Target : null);
            var property = a[^1].ObjectValue as VmRuntimeProperty;
            if (property is null && a[^1].ObjectValue is VmString propertyName && ownerType is { } owner)
                property = MakePropertyObject(ctx, owner, propertyName.Value).ObjectValue as VmRuntimeProperty;
            if (property is null)
                throw new UnhandledGuestException("System.ArgumentException", "PropertyInfo が必要です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.MemberAccess, Object = receiver, Property = property,
                ResultType = ctx.Types.FindIntrinsicType("System.Object"),
            }));
        }, BindingOrigin.Managed);

        r.Register(IntrinsicKey.Static(T, "Lambda", 2), static (ctx, a) => {
            if (ctx.MethodTypeArguments.Length != 1)
                throw new UnhandledGuestException("System.ArgumentException", "Lambda<TDelegate> は delegate 型引数が必要です。");
            var body = Node(a[0]);
            var parameters = a[1].ObjectValue switch {
                VmArray array => array.Elements.Select(Node).ToArray(),
                null => [],
                _ => throw new UnhandledGuestException("System.ArgumentException", "parameters は ParameterExpression[] である必要があります。"),
            };
            if (parameters.Any(p => p.Kind != VmExpressionKind.Parameter))
                throw new UnhandledGuestException("System.ArgumentException", "lambda の引数は ParameterExpression である必要があります。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Lambda,
                Body = body,
                Parameters = parameters,
                DelegateType = ctx.MethodTypeArguments[0],
                ResultType = ctx.MethodTypeArguments[0],
            }));
        });
        r.Register(IntrinsicKey.Static(T, "Lambda", 3), static (ctx, a) => {
            var delegateType = Type(ctx, a[0]);
            var body = Node(a[1]);
            var parameters = Nodes(a[2]);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Lambda, Body = body, Parameters = parameters,
                DelegateType = delegateType, ResultType = delegateType,
            }));
        });
        r.RegisterBinding(BindingKey.StaticAnyParams(T, "Lambda"), static (ctx, a) => {
            var genericDelegate = ctx.MethodTypeArguments.Length == 1 ? ctx.MethodTypeArguments[0] : null;
            var explicitDelegate = genericDelegate is null && a.Length > 0 && a[0].ObjectValue is VmRuntimeObject
                ? Type(ctx, a[0]) : null;
            var delegateType = genericDelegate ?? explicitDelegate
                ?? throw new UnhandledGuestException("System.ArgumentException", "Lambda には delegate 型が必要です。");
            var bodyIndex = explicitDelegate is null ? 0 : 1;
            var body = Node(a[bodyIndex]);
            var parameters = a.Length > bodyIndex + 1 ? Nodes(a[^1]) : [];
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Lambda, Body = body, Parameters = parameters,
                DelegateType = delegateType, ResultType = delegateType,
            }));
        }, BindingOrigin.Managed);

        r.Register(IntrinsicKey.Instance("System.Linq.Expressions.LambdaExpression", "Compile", 0), static (ctx, a) => {
            var lambda = Node(a[0]);
            if (lambda.Kind != VmExpressionKind.Lambda || lambda.Body is null || lambda.DelegateType is null)
                throw new UnhandledGuestException("System.InvalidOperationException", "式木 Lambda が不正です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmDelegate {
                DeclaredType = lambda.DelegateType,
                ExpressionLambda = lambda,
            }));
        });
        r.Register(IntrinsicKey.Instance("System.Linq.Expressions.LambdaExpression", "Compile", 1), static (ctx, a) => {
            var lambda = Node(a[0]);
            if (lambda.Kind != VmExpressionKind.Lambda || lambda.Body is null || lambda.DelegateType is null)
                throw new UnhandledGuestException("System.InvalidOperationException", "式木 Lambda が不正です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmDelegate {
                DeclaredType = lambda.DelegateType,
                ExpressionLambda = lambda,
            }));
        });
    }

    private static void RegisterReflectionEmit(IntrinsicRegistry r) {
        ReflectionEmitRuntime.RegisterOpCodeFields(r);
        const string dynamicMethod = "System.Reflection.Emit.DynamicMethod";
        const string generator = "System.Reflection.Emit.ILGenerator";
        foreach (var parameterCount in new[] { 3, 4, 5, 7 })
            r.Register(IntrinsicKey.Instance(dynamicMethod, ".ctor", parameterCount),
                (ctx, args) => ReflectionEmitRuntime.ConstructDynamicMethod(ctx, args));
        r.Register(IntrinsicKey.Instance(dynamicMethod, "GetILGenerator", 0),
            (ctx, args) => ReflectionEmitRuntime.GetILGenerator(ctx, args));
        r.Register(IntrinsicKey.Instance(dynamicMethod, "GetILGenerator", 1),
            (ctx, args) => ReflectionEmitRuntime.GetILGenerator(ctx, args));
        r.Register(IntrinsicKey.Instance(dynamicMethod, "CreateDelegate", 1),
            (ctx, args) => ReflectionEmitRuntime.CreateDelegate(ctx, args));
        r.Register(IntrinsicKey.Instance(dynamicMethod, "CreateDelegate", 2),
            (ctx, args) => ReflectionEmitRuntime.CreateDelegate(ctx, args));
        r.Register(IntrinsicKey.Instance(generator, "DefineLabel", 0),
            (ctx, args) => ReflectionEmitRuntime.DefineLabel(ctx, args));
        r.Register(IntrinsicKey.Instance(generator, "MarkLabel", 1),
            (ctx, args) => ReflectionEmitRuntime.MarkLabel(ctx, args));
        r.Register(IntrinsicKey.Instance(generator, "DeclareLocal", 1),
            (ctx, args) => ReflectionEmitRuntime.DeclareLocal(ctx, args));
        r.Register(IntrinsicKey.Instance(generator, "DeclareLocal", 2),
            (ctx, args) => ReflectionEmitRuntime.DeclareLocal(ctx, args));
        r.Register(IntrinsicKey.Instance(generator, "Emit", 1), (ctx, args) => ReflectionEmitRuntime.Emit(ctx, args));
        r.Register(IntrinsicKey.Instance(generator, "Emit", 2), (ctx, args) => ReflectionEmitRuntime.Emit(ctx, args));
        r.Register(IntrinsicKey.Instance(generator, "EmitCall", 3), (ctx, args) => ReflectionEmitRuntime.EmitCall(ctx, args));
    }
}
