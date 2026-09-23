using System.Buffers.Binary;
using System.Reflection;
using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    /// <summary>Assembly.Load(byte[]) は CLR にロードせず、必ず同じ VM の PE loader に通す。</summary>
    private static void RegisterAssembly(IntrinsicRegistry r) {
        const string T = "System.Reflection.Assembly";
        r.RegisterBinding(BindingKey.Static(T, "Load", "System.Byte[]"),
            static (ctx, a) => DefaultIntrinsics.LoadAssembly(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Load", "System.Byte[]", "System.Byte[]"),
            static (ctx, a) => DefaultIntrinsics.LoadAssembly(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Load", "System.Reflection.AssemblyName"),
            static (ctx, a) => DefaultIntrinsics.LoadAssembly(ctx, a), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "get_FullName"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(
                ((VmAssemblyObject)a[0].ObjectValue!).Loader.Image.Identity.ToString())), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "get_Location"),
            static (ctx, a) => StackSlot.OfObject(ctx.MakeString(
                ((VmAssemblyObject)a[0].ObjectValue!).Loader.Image.SourcePath ?? "")), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "GetType", "System.String"),
            static (ctx, a) => {
                var assembly = (VmAssemblyObject)a[0].ObjectValue!;
                var name = (a[1].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
                return assembly.Loader.FindTypeByFullName(name) is { } type
                    ? DefaultIntrinsics.MakeRuntimeObject(ctx, type) : StackSlot.Null;
            }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "GetType", "System.String", "System.Boolean"),
            static (ctx, a) => {
                var assembly = (VmAssemblyObject)a[0].ObjectValue!;
                var name = (a[1].ObjectValue as VmString)?.Value
                    ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
                if (assembly.Loader.FindTypeByFullName(name) is { } type)
                    return DefaultIntrinsics.MakeRuntimeObject(ctx, type);
                if (a[2].AsInt32 != 0)
                    throw new UnhandledGuestException("System.TypeLoadException", $"型 '{name}' が見つかりません。");
                return StackSlot.Null;
            }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(T, "GetTypes"),
            static (ctx, a) => {
                var loader = ((VmAssemblyObject)a[0].ObjectValue!).Loader;
                var values = new List<StackSlot>();
                for (var rid = 1; rid <= loader.Image.Tables.GetRowCount(TableKind.TypeDef); rid++) {
                    var type = loader.GetTypeDef(rid);
                    if (type.FullName != "<Module>")
                        values.Add(DefaultIntrinsics.MakeRuntimeObject(ctx, type));
                }
                var elementType = (VmType?)ctx.Types.FindTypeByFullName("System.Type")
                    ?? ctx.Types.FindIntrinsicType("System.Type")
                    ?? throw new InvalidOperationException("System.Type が解決できません。");
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmArray(
                    new VmArrayType { ElementType = elementType }, values.ToArray())));
            }, BindingOrigin.Managed);
    }

    /// <summary>Expression.Compile は小さな式ノードを VM 側で評価する delegate として返す。</summary>
    private static void RegisterExpressionTrees(IntrinsicRegistry r) {
        const string T = "System.Linq.Expressions.Expression";
        static VmExpressionObject Node(StackSlot slot) => slot.ObjectValue as VmExpressionObject
            ?? throw new UnhandledGuestException("System.ArgumentException", "引数は VM 式木ノードである必要があります。");
        static VmType TargetType(StackSlot slot) => slot.ObjectValue is VmRuntimeObject runtimeType
            ? runtimeType.Target
            : throw new UnhandledGuestException("System.ArgumentException", "Type 引数は VM の型情報である必要があります。");
        static StackSlot ConstantValue(StackSlot slot) => slot.ObjectValue is VmBoxedValue boxed && boxed.Fields.Length == 1
            ? boxed.Fields[0] : slot;
        void BindByArity(string name, int[] arities, IntrinsicImpl impl) {
            foreach (var arity in arities)
                r.RegisterBinding(BindingKey.StaticByArity(T, name, arity), impl, BindingOrigin.Managed);
        }

        r.RegisterBinding(BindingKey.Static(T, "Constant", "System.Object"),
            static (ctx, a) => {
                var value = ConstantValue(a[0]);
                var type = value.ObjectValue is null
                    ? ctx.Types.FindIntrinsicType("System.Object")!
                    : DefaultIntrinsics.RuntimeTypeOf(ctx, value);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = VmExpressionKind.Constant, Constant = value, ResultType = type,
                }));
            }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Constant", "System.Object", "System.Type"),
            static (ctx, a) => StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Constant, Constant = ConstantValue(a[0]), ResultType = TargetType(a[1]),
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Parameter", "System.Type"),
            static (ctx, a) => StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = TargetType(a[0]),
            })), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(T, "Parameter", "System.Type", "System.String"),
            static (ctx, a) => StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = TargetType(a[0]),
            })), BindingOrigin.Managed);

        void Binary(string name, VmExpressionKind kind) =>
            r.RegisterBinding(BindingKey.Static(T, name, "System.Linq.Expressions.Expression", "System.Linq.Expressions.Expression"),
                (ctx, a) => StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = Node(a[0]), Right = Node(a[1]), ResultType = Node(a[0]).ResultType,
                })), BindingOrigin.Managed);
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
            BindByArity(name, [2], (ctx, a) =>
                StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = Node(a[0]), Right = Node(a[1]), ResultType = Node(a[0]).ResultType,
                })));
        foreach (var (name, kind) in new[] {
            ("Negate", VmExpressionKind.Negate), ("NegateChecked", VmExpressionKind.Negate),
            ("Not", VmExpressionKind.Not),
        })
            BindByArity(name, [1], (ctx, a) =>
                StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = Node(a[0]), ResultType = Node(a[0]).ResultType,
                })));
        BindByArity("Convert", [1, 2], (ctx, a) => {
            var node = Node(a[0]);
            var target = a.Length > 1 && a[1].ObjectValue is VmRuntimeObject type ? type.Target : node.ResultType;
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Convert, Left = node, ResultType = target,
            }));
        });
        foreach (var (name, kind) in new[] { ("TypeIs", VmExpressionKind.TypeIs), ("TypeAs", VmExpressionKind.TypeAs) })
            BindByArity(name, [1, 2], (ctx, a) => {
                var operand = Node(a[0]);
                var target = TargetType(a[1]);
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = kind, Left = operand, NewType = target,
                    ResultType = kind == VmExpressionKind.TypeIs ? ctx.Types.FindIntrinsicType("System.Boolean") : target,
                }));
            });
        BindByArity("ArrayIndex", [1, 2], (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.ArrayIndex, Left = Node(a[0]), Right = Node(a[1]), ResultType = Node(a[0]).ResultType,
            })));
        BindByArity("ArrayLength", [1], (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.ArrayLength, Left = Node(a[0]), ResultType = ctx.Types.FindIntrinsicType("System.Int32"),
            })));
        BindByArity("Condition", [1, 3], (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Conditional, Left = Node(a[0]), IfTrue = Node(a[1]), IfFalse = Node(a[2]), ResultType = Node(a[1]).ResultType,
            })));
        BindByArity("Assign", [1, 2], (ctx, a) => {
            var target = Node(a[0]);
            var value = Node(a[1]);
            if (target.Kind != VmExpressionKind.Parameter)
                throw new UnhandledGuestException("System.ArgumentException", "Expression.Assign の左辺は ParameterExpression である必要があります。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Assign, Left = target, Right = value, ResultType = value.ResultType,
            }));
        });
        BindByArity("Variable", [1, 2], (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Parameter, ResultType = TargetType(a[0]), IsVariable = true,
            })));
        BindByArity("Default", [1], (ctx, a) => {
            var type = TargetType(a[0]);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Default, ResultType = type, NewType = type,
            }));
        });
        BindByArity("Quote", [1], (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Quote, Object = Node(a[0]), ResultType = Node(a[0]).ResultType,
            })));
        BindByArity("ArrayAccess", [1, 2], (ctx, a) => {
            var indexes = Nodes(a[^1]);
            if (indexes.Length != 1)
                throw new UnhandledGuestException("System.NotSupportedException", "式木の多次元配列アクセスは未対応です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.ArrayIndex, Left = Node(a[0]), Right = indexes[0],
                ResultType = Node(a[0]).ResultType,
            }));
        });
        static VmExpressionObject[] Nodes(StackSlot slot) => slot.ObjectValue switch {
            VmArray array => array.Elements.Select(Node).ToArray(),
            null => [],
            _ => throw new UnhandledGuestException("System.ArgumentException", "式木引数配列が不正です。"),
        };
        BindByArity("Block", [1, 2], (ctx, a) => {
            var expressions = a.Length == 0 ? [] : Nodes(a[^1]);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Block, Expressions = expressions,
                ResultType = expressions.Length == 0 ? ctx.Types.FindIntrinsicType("System.Void") : expressions[^1].ResultType,
            }));
        });
        foreach (var name in new[] { "NewArrayInit", "NewArrayBounds" })
            BindByArity(name, [1, 2], (ctx, a) => {
                var elementType = TargetType(a[0]);
                var values = a.Length > 1 ? Nodes(a[^1]) : [];
                return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                    Kind = VmExpressionKind.NewArray, NewType = elementType, Arguments = values,
                    NewArrayBounds = name == "NewArrayBounds",
                    ResultType = new VmArrayType { ElementType = elementType },
                }));
            });
        BindByArity("Call", [1, 2, 3], (ctx, a) => {
            VmExpressionObject? receiver = null;
            if (a[0].ObjectValue is not VmRuntimeMethod) receiver = Node(a[0]);
            var methodSlot = receiver is null ? a[0] : a[1];
            var method = methodSlot.ObjectValue is VmRuntimeMethod runtimeMethod
                ? runtimeMethod.Target : throw new UnhandledGuestException("System.ArgumentException", "MethodInfo が必要です。");
            var firstArgs = receiver is null ? (a.Length > 1 ? Nodes(a[^1]) : []) : (a.Length > 2 ? Nodes(a[^1]) : []);
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Call, Object = receiver, Method = method, Arguments = firstArgs,
                ResultType = ctx.Types.FindIntrinsicType("System.Object"),
            }));
        });
        BindByArity("New", [1, 2], (ctx, a) => {
            var ctor = a[0].ObjectValue is VmRuntimeMethod runtimeMethod
                ? runtimeMethod.Target
                : throw new UnhandledGuestException("System.ArgumentException", "ConstructorInfo が必要です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.New, Method = ctor, NewType = ctor.DeclaringType,
                Arguments = a.Length > 1 ? Nodes(a[^1]) : [], ResultType = ctor.DeclaringType,
            }));
        });
        BindByArity("Invoke", [1, 2], (ctx, a) =>
            StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Invoke, Object = Node(a[0]), Arguments = a.Length > 1 ? Nodes(a[^1]) : [],
                ResultType = ctx.Types.FindIntrinsicType("System.Object"),
            })));
        BindByArity("Field", [1, 2], (ctx, a) => {
            var receiver = a.Length > 1 && a[0].ObjectValue is VmExpressionObject ? Node(a[0]) : null;
            var ownerType = receiver?.ResultType ?? (a.Length > 1 && a[0].ObjectValue is VmRuntimeObject type ? type.Target : null);
            var field = a[^1].ObjectValue is VmRuntimeField runtimeField
                ? runtimeField.Target
                : ownerType is { } owner && a[^1].ObjectValue is VmString fieldName
                    ? (DefaultIntrinsics.MakeFieldObject(ctx, owner, fieldName.Value).ObjectValue as VmRuntimeField)?.Target
                    : null;
            if (field is null)
                throw new UnhandledGuestException("System.ArgumentException", "FieldInfo が必要です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.MemberAccess, Object = receiver, Field = field, ResultType = field.FieldType,
            }));
        });
        BindByArity("Property", [1, 2], (ctx, a) => {
            var receiver = a.Length > 1 && a[0].ObjectValue is VmExpressionObject ? Node(a[0]) : null;
            var ownerType = receiver?.ResultType ?? (a.Length > 1 && a[0].ObjectValue is VmRuntimeObject type ? type.Target : null);
            var property = a[^1].ObjectValue as VmRuntimeProperty;
            if (property is null && a[^1].ObjectValue is VmString propertyName && ownerType is { } owner)
                property = DefaultIntrinsics.MakePropertyObject(ctx, owner, propertyName.Value).ObjectValue as VmRuntimeProperty;
            if (property is null)
                throw new UnhandledGuestException("System.ArgumentException", "PropertyInfo が必要です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.MemberAccess, Object = receiver, Property = property,
                ResultType = ctx.Types.FindIntrinsicType("System.Object"),
            }));
        });

        r.RegisterBinding(BindingKey.Static(T, "Lambda", "System.Linq.Expressions.Expression", "System.Linq.Expressions.ParameterExpression[]"),
            static (ctx, a) => {
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
                    Kind = VmExpressionKind.Lambda, Body = body, Parameters = parameters,
                    DelegateType = ctx.MethodTypeArguments[0], ResultType = ctx.MethodTypeArguments[0],
                }));
            }, BindingOrigin.Managed);
        BindByArity("Lambda", [2, 3], (ctx, a) => {
            var genericDelegate = ctx.MethodTypeArguments.Length == 1 ? ctx.MethodTypeArguments[0] : null;
            var explicitDelegate = genericDelegate is null && a.Length > 0 && a[0].ObjectValue is VmRuntimeObject
                ? TargetType(a[0]) : null;
            var delegateType = genericDelegate ?? explicitDelegate
                ?? throw new UnhandledGuestException("System.ArgumentException", "Lambda には delegate 型が必要です。");
            var bodyIndex = explicitDelegate is null ? 0 : 1;
            var body = Node(a[bodyIndex]);
            var parameters = a.Length > bodyIndex + 1 ? Nodes(a[^1]) : [];
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmExpressionObject {
                Kind = VmExpressionKind.Lambda, Body = body, Parameters = parameters,
                DelegateType = delegateType, ResultType = delegateType,
            }));
        });

        StackSlot Compile(IntrinsicContext ctx, StackSlot[] args) {
            var lambda = Node(args[0]);
            if (lambda.Kind != VmExpressionKind.Lambda || lambda.Body is null || lambda.DelegateType is null)
                throw new UnhandledGuestException("System.InvalidOperationException", "式木 Lambda が不正です。");
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmDelegate {
                DeclaredType = lambda.DelegateType, ExpressionLambda = lambda,
            }));
        }
        foreach (var type in new[] { "System.Linq.Expressions.LambdaExpression", "System.Linq.Expressions.Expression`1" }) {
            r.RegisterBinding(BindingKey.Instance(type, "Compile"), (ctx, args) => Compile(ctx, args), BindingOrigin.Managed);
            r.RegisterBinding(BindingKey.Instance(type, "Compile", "System.Boolean"), (ctx, args) => Compile(ctx, args), BindingOrigin.Managed);
        }
    }

    private static void RegisterReflectionEmit(IntrinsicRegistry r) {
        const string dynamicMethod = "System.Reflection.Emit.DynamicMethod";
        const string generator = "System.Reflection.Emit.ILGenerator";
        r.RegisterBinding(BindingKey.Instance(dynamicMethod, "GetILGenerator"),
            (ctx, args) => ReflectionEmitRuntime.GetILGenerator(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(dynamicMethod, "GetILGenerator", "System.Int32"),
            (ctx, args) => ReflectionEmitRuntime.GetILGenerator(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(dynamicMethod, "CreateDelegate", "System.Type"),
            (ctx, args) => ReflectionEmitRuntime.CreateDelegate(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(dynamicMethod, "CreateDelegate", "System.Type", "System.Object"),
            (ctx, args) => ReflectionEmitRuntime.CreateDelegate(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "DefineLabel"),
            (ctx, args) => ReflectionEmitRuntime.DefineLabel(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "MarkLabel", "System.Reflection.Emit.Label"),
            (ctx, args) => ReflectionEmitRuntime.MarkLabel(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "DeclareLocal", "System.Type"),
            (ctx, args) => ReflectionEmitRuntime.DeclareLocal(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "DeclareLocal", "System.Type", "System.Boolean"),
            (ctx, args) => ReflectionEmitRuntime.DeclareLocal(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "Emit", "System.Reflection.Emit.OpCode"),
            (ctx, args) => ReflectionEmitRuntime.Emit(ctx, args), BindingOrigin.Managed);
        foreach (var operand in new[] { "System.Byte", "System.SByte", "System.Int16", "System.Int32", "System.Int64", "System.Single", "System.Double" })
            r.RegisterBinding(BindingKey.Instance(generator, "Emit", "System.Reflection.Emit.OpCode", operand),
                (ctx, args) => ReflectionEmitRuntime.Emit(ctx, args), BindingOrigin.Managed);
        foreach (var operand in new[] { "System.Reflection.Emit.Label", "System.Reflection.Emit.Label[]", "System.Reflection.Emit.LocalBuilder", "System.Reflection.MethodInfo", "System.Reflection.ConstructorInfo", "System.Reflection.FieldInfo", "System.Type", "System.String" })
            r.RegisterBinding(BindingKey.Instance(generator, "Emit", "System.Reflection.Emit.OpCode", operand),
                (ctx, args) => ReflectionEmitRuntime.Emit(ctx, args), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(generator, "EmitCall", "System.Reflection.Emit.OpCode", "System.Reflection.MethodInfo", "System.Type[]"),
            (ctx, args) => ReflectionEmitRuntime.EmitCall(ctx, args), BindingOrigin.Managed);
    }


    // ---- System.Activator (インスタンス生成面) ----    /// <summary>Activator.CreateInstance(Type) の同等意味論。
    /// 本家 IL は RuntimeType.CreateInstanceDefaultCtor → ActivatorCache (ランタイム内部の
    /// メソッドテーブルキャッシュ) を辿るため VM 表現境界。値型は既定値の box、参照型は
    /// 公開無引数 .ctor を NewInstanceHook (ObjectEngine 実体。確保＋初期化＋IL 実行) で
    /// 実行する。抽象・インターフェース・.ctor 無しは CLR と同一分類のゲスト例外。</summary>
    private static void RegisterActivator(IntrinsicRegistry r) {
        r.RegisterBinding(BindingKey.Static("System.Activator", "CreateInstance", "System.Type"),
            static (ctx, a) => {
                if (a[0].ObjectValue is not VmRuntimeObject runtime)
                    throw new UnhandledGuestException("System.ArgumentNullException", null);
                var target = runtime.Target;
                if (target.IsValueType) {
                    var def = new ObjectModel().DefaultForType(target, ctx.Types);
                    if (def.Kind == StackKind.ValueType && def.ObjectValue is VmStructValue sv)
                        return StackSlot.OfObject(ctx.Heap.Allocate(
                            new VmBoxedValue(sv.StructType is VmClassType cls ? cls : target, sv.Fields)));
                    return StackSlot.OfObject(ctx.Heap.Allocate(new VmBoxedValue(target, [def])));
                }
                if (target.IsInterface)
                    throw new UnhandledGuestException("System.MissingMethodException", null);
                VmClassType def2;
                GenericContext? classContext = null;
                if (target is VmConstructedType constructed) {
                    if (constructed.Definition is not VmClassType constructedDef)
                        throw new UnhandledGuestException("System.ArgumentException", null);
                    def2 = constructedDef;
                    classContext = new GenericContext { ClassArgs = constructed.TypeArguments };
                } else if (target is VmClassType simple) {
                    def2 = simple;
                } else {
                    // 配列は MissingMethod (CLR 規約: 要素数なしでは構築不可)、
                    // ポインタ/参照/ジェネリックパラメータは ArgumentException
                    throw new UnhandledGuestException(
                        target is VmArrayType or VmMultiDimArrayType
                            ? "System.MissingMethodException" : "System.ArgumentException", null);
                }
                if ((def2.Flags & 0x80) != 0)
                    throw new UnhandledGuestException("System.MemberAccessException", null);
                var ctor = def2.Methods.FirstOrDefault(m =>
                    m.Name == ".ctor" && !m.IsStatic && m.Signature.ParamTypes.Length == 0 &&
                    m.IsPublic && m.Body is not null);
                if (ctor is null)
                    throw new UnhandledGuestException("System.MissingMethodException", null);
                var hook = ctx.NewInstanceHook
                    ?? throw new InvalidOperationException("Activator のインスタンス生成フックが設定されていません。");
                return StackSlot.OfObject(hook(target, ctor, [], classContext));
            },
            BindingOrigin.Managed);
        // RuntimeTypeHandle.CreateInstanceForAnotherGenericParameter(Type, RuntimeType):
        // ArraySortHelper<T>.CreateArraySortHelper 等が比較子 (GenericComparer<T> 等) の
        // 未初期化実体を得る面。本家はランタイム内部だが、VM では構築型の確保 (ctor 不実行 =
        // GetUninitializedObject と同一) が同一意味論。stateless な比較子型が使う。
        r.RegisterBinding(BindingKey.Static("System.RuntimeTypeHandle", "CreateInstanceForAnotherGenericParameter",
                "System.RuntimeType", "System.RuntimeType"),
            static (ctx, a) => {
                if (a[0].ObjectValue is not VmRuntimeObject genericType)
                    throw new InvalidOperationException(
                        "CreateInstanceForAnotherGenericParameter の第 1 引数が Type ファサードではありません。");
                if (genericType.Target is not VmConstructedType constructed ||
                    constructed.Definition is not VmClassType def)
                    throw new InvalidOperationException(
                        "CreateInstanceForAnotherGenericParameter は構築ジェネリック型にのみ対応しています。");
                var storage = new ObjectModel().CreateInstanceStorage(def, ctx.Types,
                    new GenericContext { ClassArgs = constructed.TypeArguments });
                return StackSlot.OfObject(ctx.Heap.Allocate(
                    new VmClassInstance(def, storage, constructed.TypeArguments)));
            },
            BindingOrigin.InternalCall);
    }

    /// <summary>Type.GetType(string) の型名解決 (アセンブリ修飾・ジェネリック・配列/参照/ポインタ)。
    /// アセンブリ指定ありはその画像内のみ、単純名は trusted CoreLib を先に (fake BCL 防止)・
    /// 次に全ロード画像で探す。解釈不能・未解決は null (CLR の null 返却規約)。</summary>
    private static VmType? ResolveTypeName(IntrinsicContext ctx, string name) {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        SplitTopLevel(name, ',', out var typePart, out var assemblyPart);
        typePart = typePart.Trim();
        TypeLoader? ambient = null;
        if (!string.IsNullOrEmpty(assemblyPart)) {
            var comma = assemblyPart.IndexOf(',');
            var simpleName = (comma < 0 ? assemblyPart : assemblyPart[..comma]).Trim();
            ambient = ctx.Types.Context?.FindBySimpleName(simpleName);
            if (ambient is null)
                return null;
        }
        return ResolveTypePart(ctx, typePart, ambient);
    }

    private static VmType? ResolveTypePart(IntrinsicContext ctx, string typePart, TypeLoader? ambient) {
        // 末尾の [] / & / * を剥がす (多重対応)
        var suffixes = new List<char>();
        var core = typePart.TrimEnd();
        while (core.EndsWith("[]", StringComparison.Ordinal) || core.EndsWith("&", StringComparison.Ordinal) ||
               (core.EndsWith("*", StringComparison.Ordinal) && !core.EndsWith("**", StringComparison.Ordinal))) {
            if (core.EndsWith("[]", StringComparison.Ordinal)) {
                suffixes.Add('a');
                core = core[..^2].TrimEnd();
            } else if (core.EndsWith("&", StringComparison.Ordinal)) {
                suffixes.Add('&');
                core = core[..^1].TrimEnd();
            } else {
                suffixes.Add('*');
                core = core[..^1].TrimEnd();
            }
        }
        VmType? resolved;
        var bracket = core.IndexOf('[');
        if (bracket < 0) {
            resolved = FindNamedType(ctx, core, ambient);
        } else {
            // ジェネリック実体化: Def`N[[arg],[arg]]
            var defName = core[..bracket].Trim();
            var argsSection = core[bracket..].Trim();
            if (!argsSection.StartsWith("[", StringComparison.Ordinal) || !argsSection.EndsWith("]", StringComparison.Ordinal))
                return null;
            var inner = argsSection[1..^1];
            var argTexts = SplitTopLevelAll(inner, ',');
            var def = FindNamedType(ctx, defName, ambient);
            if (def is not VmClassType defClass || defClass.GenericParamCount != argTexts.Count)
                return null;
            var typeArgs = new VmType[argTexts.Count];
            for (var i = 0; i < argTexts.Count; i++) {
                var argText = argTexts[i].Trim();
                // 引数は [修飾名] 形で包まれる (包みが無ければそのまま)
                if (argText.StartsWith("[", StringComparison.Ordinal) && argText.EndsWith("]", StringComparison.Ordinal))
                    argText = argText[1..^1];
                var argType = ResolveTypeName(ctx, argText);
                if (argType is null)
                    return null;
                typeArgs[i] = argType;
            }
            resolved = new VmConstructedType { Definition = defClass, TypeArguments = typeArgs };
        }
        if (resolved is null)
            return null;
        foreach (var suffix in suffixes) {
            resolved = suffix switch {
                'a' => ArrayWithKnownBase(ctx, resolved),
                '&' => new VmByRefType { ElementType = resolved },
                // ポインタは ByRef と同様に扱う (署名解決の既存規約)
                _ => new VmByRefType { ElementType = resolved },
            };
        }
        return resolved;
    }

    private static VmType ArrayWithKnownBase(IntrinsicContext ctx, VmType element) {
        var array = new VmArrayType { ElementType = element };
        try {
            array.SetBaseType(ctx.Types.ResolveWellKnownType("System.Array"));
        } catch {
            // System.Array 未解決時は基底なし (稀。キャスト判定のみ弱くなる)
        }
        return array;
    }

    private static VmType? FindNamedType(IntrinsicContext ctx, string fullName, TypeLoader? ambient) {
        if (ambient is not null) {
            try {
                return ambient.FindTypeByFullName(fullName);
            } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException or InvalidOperationException or KeyNotFoundException) {
                return null;
            }
        }
        // 単純名: trusted 実型を先に (同名 fake 型の混入防止)、次に全ロード画像
        try {
            if (ctx.Types.TryResolveTrustedUnifiedType(fullName) is { } trusted)
                return trusted;
        } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException or InvalidOperationException or KeyNotFoundException) {
        }
        var context = ctx.Types.Context;
        if (context is null) {
            try {
                return ctx.Types.FindTypeByFullName(fullName);
            } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException or InvalidOperationException or KeyNotFoundException) {
                return null;
            }
        }
        foreach (var loader in context.Loaders) {
            try {
                if (loader.FindTypeByFullName(fullName) is { } found)
                    return found;
            } catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException or InvalidOperationException or KeyNotFoundException) {
                continue;
            }
        }
        return null;
    }

    /// <summary>トップレベルの区切り ([]) ネストを無視して最初に分割する。</summary>
    private static void SplitTopLevel(string text, char separator, out string left, out string? right) {
        var depth = 0;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == separator && depth == 0) {
                left = text[..i];
                right = text[(i + 1)..];
                return;
            }
        }
        left = text;
        right = null;
    }

    private static List<string> SplitTopLevelAll(string text, char separator) {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == separator && depth == 0) {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }
}
