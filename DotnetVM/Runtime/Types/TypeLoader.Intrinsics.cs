namespace DotnetVM.Runtime.Types;

public sealed partial class TypeLoader {
    // ---- ファサード型 ----

    private void InitializeIntrinsicTypes() {
        var @object = new VmIntrinsicType { Namespace = "System", Name = "Object", IsValue = false };
        var valueType = new VmIntrinsicType { Namespace = "System", Name = "ValueType", IsValue = false, Parent = @object };
        var @enum = new VmIntrinsicType { Namespace = "System", Name = "Enum", IsValue = false, Parent = valueType };

        void Add(VmIntrinsicType type, uint[]? genericParamFlags = null) {
            if (genericParamFlags is not null)
                type.SetGenericParamFlags(genericParamFlags);
            _intrinsicTypes[type.FullName] = type;
        }
        Add(@object);
        Add(valueType);
        Add(@enum);
        Add(new VmIntrinsicType { Namespace = "System", Name = "Void", IsValue = true });
        Add(new VmIntrinsicType { Namespace = "System", Name = "String", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Exception", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System", Name = "TimeSpan", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Console", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Math", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Convert", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Array", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Type", IsValue = false, Parent = @object });
        var memberInfo = new VmIntrinsicType { Namespace = "System.Reflection", Name = "MemberInfo", IsValue = false, Parent = @object };
        Add(memberInfo);
        var methodBase = new VmIntrinsicType { Namespace = "System.Reflection", Name = "MethodBase", IsValue = false, Parent = memberInfo };
        Add(methodBase);
        Add(new VmIntrinsicType { Namespace = "System.Reflection", Name = "MethodInfo", IsValue = false, Parent = methodBase });
        Add(new VmIntrinsicType { Namespace = "System.Reflection", Name = "ConstructorInfo", IsValue = false, Parent = methodBase });
        Add(new VmIntrinsicType { Namespace = "System.Reflection", Name = "FieldInfo", IsValue = false, Parent = memberInfo });
        Add(new VmIntrinsicType { Namespace = "System.Reflection", Name = "PropertyInfo", IsValue = false, Parent = memberInfo });
        Add(new VmIntrinsicType { Namespace = "System.Reflection", Name = "Assembly", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System.Reflection", Name = "AssemblyName", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System.Runtime.Loader", Name = "AssemblyLoadContext", IsValue = false, Parent = @object });
        var stream = new VmIntrinsicType { Namespace = "System.IO", Name = "Stream", IsValue = false, Parent = @object };
        Add(stream);
        Add(new VmIntrinsicType { Namespace = "System.IO", Name = "MemoryStream", IsValue = false, Parent = stream });
        var expression = new VmIntrinsicType { Namespace = "System.Linq.Expressions", Name = "Expression", IsValue = false, Parent = @object };
        Add(expression);
        Add(new VmIntrinsicType { Namespace = "System.Linq.Expressions", Name = "Expression`1", IsValue = false, Parent = expression }, [0u]);
        Add(new VmIntrinsicType { Namespace = "System.Linq.Expressions", Name = "LambdaExpression", IsValue = false, Parent = expression });
        Add(new VmIntrinsicType { Namespace = "System.Linq.Expressions", Name = "ParameterExpression", IsValue = false, Parent = expression });
        Add(new VmIntrinsicType { Namespace = "System.Linq.Expressions", Name = "BinaryExpression", IsValue = false, Parent = expression });
        Add(new VmIntrinsicType { Namespace = "System.Linq.Expressions", Name = "ConstantExpression", IsValue = false, Parent = expression });
        Add(new VmIntrinsicType { Namespace = "System.Reflection.Emit", Name = "DynamicMethod", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System.Reflection.Emit", Name = "ILGenerator", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System.Reflection.Emit", Name = "Label", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System.Reflection.Emit", Name = "LocalBuilder", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System.Reflection.Emit", Name = "OpCodes", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System.Reflection.Emit", Name = "OpCode", IsValue = true, Parent = valueType });
        // 例外階層のファサード (ECMA-335 / CLR の SystemException 配下)。VM 内部例外もここから実体化する
        var systemException = new VmIntrinsicType { Namespace = "System", Name = "SystemException", IsValue = false, Parent = _intrinsicTypes["System.Exception"] };
        Add(systemException);
        var ioException = new VmIntrinsicType { Namespace = "System.IO", Name = "IOException", IsValue = false, Parent = systemException };
        Add(ioException);
        Add(new VmIntrinsicType { Namespace = "System.IO", Name = "FileNotFoundException", IsValue = false, Parent = ioException });
        Add(new VmIntrinsicType { Namespace = "System", Name = "InvalidOperationException", IsValue = false, Parent = systemException });
        var argumentException = new VmIntrinsicType { Namespace = "System", Name = "ArgumentException", IsValue = false, Parent = systemException };
        Add(argumentException);
        Add(new VmIntrinsicType { Namespace = "System", Name = "ArgumentOutOfRangeException", IsValue = false, Parent = argumentException });
        Add(new VmIntrinsicType { Namespace = "System", Name = "ArgumentNullException", IsValue = false, Parent = argumentException });
        Add(new VmIntrinsicType { Namespace = "System", Name = "ApplicationException", IsValue = false, Parent = _intrinsicTypes["System.Exception"] });
        foreach (var name in new[] {
            "NullReferenceException", "IndexOutOfRangeException", "DivideByZeroException",
            "OverflowException", "InvalidCastException", "ArrayTypeMismatchException",
            "FormatException", "StackOverflowException", "OutOfMemoryException",
            "NotSupportedException", "OperationCanceledException", "TimeoutException", "TypeLoadException",
            "NotImplementedException", "RankException", "InvalidProgramException",
        })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = false, Parent = systemException });
        Add(new VmIntrinsicType { Namespace = "System", Name = "AggregateException", IsValue = false, Parent = systemException });
        // Activator.CreateInstance の失敗分類 (CLR の継承鎖どおり MemberAccess ← MissingMember ← MissingMethod)
        var memberAccess = new VmIntrinsicType { Namespace = "System", Name = "MemberAccessException", IsValue = false, Parent = systemException };
        Add(memberAccess);
        var missingMember = new VmIntrinsicType { Namespace = "System", Name = "MissingMemberException", IsValue = false, Parent = memberAccess };
        Add(missingMember);
        Add(new VmIntrinsicType { Namespace = "System", Name = "MissingMethodException", IsValue = false, Parent = missingMember });
        Add(new VmIntrinsicType { Namespace = "System", Name = "ObjectDisposedException", IsValue = false, Parent = _intrinsicTypes["System.InvalidOperationException"] });

        // プリミティブはすべて ValueType の派生
        foreach (var (name, isValue) in new[] {
            ("Boolean", true), ("Char", true), ("SByte", true), ("Byte", true),
            ("Int16", true), ("UInt16", true), ("Int32", true), ("UInt32", true),
            ("Int64", true), ("UInt64", true), ("Single", true), ("Double", true),
        })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = isValue, Parent = valueType });

        // ゲストが実装/参照する頻出外部インターフェースのファサード
        foreach (var name in new[] { "IDisposable", "IComparable", "ICloneable", "IFormatProvider" })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "SynchronizationContext", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "SendOrPostCallback", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "CancellationToken", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "CancellationTokenSource", IsValue = false, Parent = @object });
        // 文字列補間 ($"...") がコンパイルされる DefaultInterpolatedStringHandler (ref struct) の
        // ファサード。本体は intrinsic 面 (AppendLiteral / AppendFormatted / ToStringAndClear) が担う。
        Add(new VmIntrinsicType {
            Namespace = "System.Runtime.CompilerServices",
            Name = "DefaultInterpolatedStringHandler", IsValue = true, Parent = valueType,
        });
        // 頻出 BCL 列挙型のファサード (署名上の TypeRef 解決に必要。値は i4 スロットとして扱う)
        foreach (var name in new[] { "StringSplitOptions", "StringComparison" })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = true, Parent = valueType });

        // デリゲート機構のファサード。Delegate/MulticastDelegate は継承判定の根で、
        // Action/Func/Predicate 等はそれらの派生として合成する (newobj デリゲート生成と
        // callvirt Invoke のデリゲート呼出は Interpreter 側でこの継承関係を判定に使う)
        var @delegate = new VmIntrinsicType { Namespace = "System", Name = "Delegate", IsValue = false, Parent = @object };
        Add(@delegate);
        var multicastDelegate = new VmIntrinsicType { Namespace = "System", Name = "MulticastDelegate", IsValue = false, Parent = @delegate };
        Add(multicastDelegate);
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "ThreadStart", IsValue = false, Parent = multicastDelegate });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "ParameterizedThreadStart", IsValue = false, Parent = multicastDelegate });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "Thread", IsValue = false, Parent = @object });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "Monitor", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "ThreadState", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "ThreadPriority", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "SynchronizationLockException", IsValue = false, Parent = systemException });
        Add(new VmIntrinsicType { Namespace = "System.Threading", Name = "Interlocked", IsValue = false });
        var task = new VmIntrinsicType { Namespace = "System.Threading.Tasks", Name = "Task", IsValue = false, Parent = @object };
        Add(task);
        Add(new VmIntrinsicType { Namespace = "System.Threading.Tasks", Name = "Task`1", IsValue = false, Parent = task }, [0u]);
        // Task-backed ValueTask は同じ VM task object を値型ラッパー越しに公開する。
        // zero-initialized ValueTask は binding 側の completed-state 表現で扱う。
        // async ValueTask<T> のコンパイラ生成 state machine が参照する面もここで
        // 明示的に合成し、同名の guest 型へ binding が誤適用されないようにする。
        var valueTask = new VmIntrinsicType { Namespace = "System.Threading.Tasks", Name = "ValueTask", IsValue = true, Parent = valueType };
        Add(valueTask);
        Add(new VmIntrinsicType { Namespace = "System.Threading.Tasks", Name = "ValueTask`1", IsValue = true, Parent = valueTask }, [0u]);
        Add(new VmIntrinsicType { Namespace = "System.Threading.Tasks.Sources", Name = "IValueTaskSource", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System.Threading.Tasks.Sources", Name = "IValueTaskSource`1", IsValue = false }, [0u]);
        Add(new VmIntrinsicType { Namespace = "System.Threading.Tasks.Sources", Name = "ValueTaskSourceStatus", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System.Threading.Tasks.Sources", Name = "ValueTaskSourceOnCompletedFlags", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System", Name = "Span`1", IsValue = true, Parent = valueType }, [0u]);
        Add(new VmIntrinsicType { Namespace = "System", Name = "ReadOnlySpan`1", IsValue = true, Parent = valueType }, [0u]);
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "TaskAwaiter", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "TaskAwaiter`1", IsValue = true, Parent = valueType }, [0u]);
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "ValueTaskAwaiter", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "ValueTaskAwaiter`1", IsValue = true, Parent = valueType }, [0u]);
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "AsyncTaskMethodBuilder", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "AsyncTaskMethodBuilder`1", IsValue = true, Parent = valueType }, [0u]);
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "AsyncValueTaskMethodBuilder", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "AsyncValueTaskMethodBuilder`1", IsValue = true, Parent = valueType }, [0u]);
        foreach (var name in new[] {
            "ConfiguredTaskAwaitable", "ConfiguredTaskAwaitable`1",
            "ConfiguredValueTaskAwaitable", "ConfiguredValueTaskAwaitable`1",
        })
            Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = name, IsValue = true, Parent = valueType },
                name.EndsWith("`1", StringComparison.Ordinal) ? [0u] : null);
        foreach (var name in new[] {
            "ConfiguredTaskAwaitable+ConfiguredTaskAwaiter",
            "ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter",
            "ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter",
            "ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter",
        })
            Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = name, IsValue = true, Parent = valueType },
                name.Contains("`1+", StringComparison.Ordinal) ? [0u] : null);
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "IAsyncStateMachine", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "INotifyCompletion", IsValue = false });
        Add(new VmIntrinsicType { Namespace = "System.Runtime.CompilerServices", Name = "ICriticalNotifyCompletion", IsValue = false });
        foreach (var arity in Enumerable.Range(0, 17))
            Add(new VmIntrinsicType { Namespace = "System", Name = arity == 0 ? "Action" : $"Action`{arity}", IsValue = false, Parent = multicastDelegate },
                Enumerable.Repeat(0u, arity).ToArray());
        foreach (var arity in Enumerable.Range(1, 16))
            Add(new VmIntrinsicType { Namespace = "System", Name = $"Func`{arity}", IsValue = false, Parent = multicastDelegate },
                Enumerable.Repeat(0u, arity).ToArray());
        foreach (var (name, ns, arity) in new[] {
            ("Predicate`1", "System", 1),
            ("Comparison`1", "System", 1),
            ("EventHandler", "System", 0),
            ("EventHandler`1", "System", 1),
            ("Converter`2", "System", 2),
            ("ResolveEventHandler", "System.Reflection", 0),
        })
            Add(new VmIntrinsicType { Namespace = ns, Name = name, IsValue = false, Parent = multicastDelegate },
                Enumerable.Repeat(0u, arity).ToArray());

        // TypedReference / varargs 周辺の特殊値型 (__makeref / arglist の署名解決に必要)
        Add(new VmIntrinsicType { Namespace = "System", Name = "TypedReference", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System", Name = "ArgIterator", IsValue = true, Parent = valueType });
        Add(new VmIntrinsicType { Namespace = "System", Name = "RuntimeArgumentHandle", IsValue = true, Parent = valueType });
        // native int のファサード (ldftn の戻り型 / sizeof 等)。既存登録があれば尊重する
        _intrinsicTypes.TryAdd("System.IntPtr", new VmIntrinsicType { Namespace = "System", Name = "IntPtr", IsValue = true, Parent = valueType });
        _intrinsicTypes.TryAdd("System.UIntPtr", new VmIntrinsicType { Namespace = "System", Name = "UIntPtr", IsValue = true, Parent = valueType });

        foreach (var name in new[] { "IEnumerable", "IEnumerator", "ICollection", "IList" })
            Add(new VmIntrinsicType { Namespace = "System.Collections", Name = name, IsValue = false });
        // ジェネリックインターフェースは BCL 既知の変性を登録する (castclass/isinst の変性判定に使う)
        foreach (var name in new[] { "IEquatable`1", "IComparable`1" })
            Add(new VmIntrinsicType { Namespace = "System", Name = name, IsValue = false }, [Contravariant]);
        foreach (var name in new[] { "IComparer`1", "IEqualityComparer`1" })
            Add(new VmIntrinsicType { Namespace = "System.Collections.Generic", Name = name, IsValue = false }, [Contravariant]);
        foreach (var name in new[] {
            "IEnumerable`1", "IEnumerator`1", "IReadOnlyList`1", "IReadOnlyCollection`1",
            "IReadOnlySet`1", "IAsyncEnumerable`1", "IAsyncEnumerator`1",
        })
            Add(new VmIntrinsicType { Namespace = "System.Collections.Generic", Name = name, IsValue = false }, [Covariant]);
        foreach (var name in new[] { "ICollection`1", "IList`1", "ISet`1", "IDictionary`2" })
            Add(new VmIntrinsicType { Namespace = "System.Collections.Generic", Name = name, IsValue = false });

        // 注意: ブリッジ経由の I/O 面 (System.IO.File / System.Net.WebClient) はここでは合成しない。
        // 界面の再現有無はブリッジ設定で制御する (VirtualMachine.LoadAssembly が条件付きで登録)。
        // ブリッジ未設定ならゲストにその面自体が存在しない = fail-closed
    }

    /// <summary>ブリッジ設定がある場合のみ呼ばれる I/O ファサード型の登録 (VirtualMachine から)。</summary>
    internal void AddIoFacade(string name) {
        VmIntrinsicType type = name switch {
            "File" => new VmIntrinsicType { Namespace = "System.IO", Name = "File", IsValue = false },
            "WebClient" => new VmIntrinsicType { Namespace = "System.Net", Name = "WebClient", IsValue = false },
            _ => throw new ArgumentException($"未知の I/O ファサード型: {name}"),
        };
        if (!_intrinsicTypes.TryAdd(type.FullName, type))
            throw new InvalidOperationException($"intrinsic 型 {type.FullName} は既に登録されています。");
    }

    /// <summary>ファサード型を名前で取得 (無ければ null)。</summary>
    public VmIntrinsicType? FindIntrinsicType(string fullName) =>
        _intrinsicTypes.GetValueOrDefault(fullName);

    /// <summary>intrinsic ファサード型を登録する (VM 起動時のみ。実行中の呼び出しは不可)。</summary>
    public void RegisterIntrinsicType(VmIntrinsicType type) {
        if (!_intrinsicTypes.TryAdd(type.FullName, type))
            throw new InvalidOperationException($"intrinsic 型 {type.FullName} は既に登録されています。");
    }
}
