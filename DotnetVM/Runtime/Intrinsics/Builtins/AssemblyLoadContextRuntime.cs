using DotnetVM.Policy;
using DotnetVM.Metadata;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Intrinsics;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

/// <summary>
/// System.Runtime.Loader.AssemblyLoadContext と AssemblyName の VM 面。
/// CLR のローダーや CLR Assembly はゲストへ公開せず、VmAssemblyContext にだけ登録する。
/// </summary>
internal static class AssemblyLoadContextRuntime {
    private const string LoadContextType = "System.Runtime.Loader.AssemblyLoadContext";
    private const string AssemblyNameType = "System.Reflection.AssemblyName";
    private const string MemoryStreamType = "System.IO.MemoryStream";

    public static void RegisterAll(IntrinsicRegistry registry) {
        RegisterAssemblyLoadContext(registry);
        RegisterAssemblyName(registry);
        RegisterMemoryStream(registry);
    }

    /// <summary>実 CoreLib 画像の MemberRef/MethodDef から到達する同じ面。</summary>
    internal static void RegisterBindings(IntrinsicRegistry r) {
        const string alc = LoadContextType;
        const string name = AssemblyNameType;
        r.RegisterBinding(BindingKey.Instance(alc, ".ctor", "System.String"), static (ctx, a) => {
            InitializeBase(ctx, a, collectible: false);
            return null;
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(alc, ".ctor", "System.String", "System.Boolean"), static (ctx, a) => {
            InitializeBase(ctx, a, collectible: a[2].AsInt32 != 0);
            return null;
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Static(alc, "get_Default"), static (ctx, _) =>
            StackSlot.OfObject(ctx.Heap.Allocate(RequireDefault(ctx))), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(alc, "get_Name"), static (ctx, a) => {
            var value = Require(a[0]).Name;
            return value is null ? StackSlot.Null : StackSlot.OfObject(ctx.MakeString(value));
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(alc, "get_IsCollectible"), static (_, a) =>
            StackSlot.OfInt32(Require(a[0]).IsCollectible ? 1 : 0), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(alc, "get_Assemblies"), static (ctx, a) =>
            Assemblies(ctx, RequireActive(a[0])), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(alc, "LoadFromAssemblyPath", "System.String"), static (ctx, a) => {
            var loadContext = RequireActive(a[0]);
            var loader = ctx.LoadAssemblyFromPath?.Invoke(loadContext, StringArg(a[1]))
                ?? throw new OperationNotAllowedException("AssemblyLoadContext のパスロードは VM ホストから利用できません。");
            return Assembly(ctx, loader);
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(alc, "LoadFromAssemblyName", name), static (ctx, a) =>
            LoadByName(ctx, RequireActive(a[0]), a[1]), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(alc, "LoadFromStream", "System.IO.Stream"), static (ctx, a) =>
            LoadFromStream(ctx, RequireActive(a[0]), a[1]), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(alc, "LoadFromStream", "System.IO.Stream", "System.IO.Stream"), static (ctx, a) =>
            LoadFromStream(ctx, RequireActive(a[0]), a[1]), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(alc, "Load", name), static (ctx, a) =>
            LoadByName(ctx, RequireActive(a[0]), a[1]), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(alc, "Unload"), static (_, a) => {
            var loadContext = Require(a[0]);
            try {
                loadContext.Unload();
            } catch (InvalidOperationException ex) {
                throw new UnhandledGuestException("System.InvalidOperationException", ex.Message);
            }
            return null;
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(name, "get_Name"), static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(RequireName(a[0]).Name)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(name, "get_FullName"), static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(RequireName(a[0]).FullName)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(name, "ToString"), static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(RequireName(a[0]).FullName)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(MemoryStreamType, "get_Position"), static (_, a) =>
            StackSlot.OfInt64(RequireStream(a[0]).Position), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(MemoryStreamType, "set_Position", "System.Int64"), static (_, a) => {
            var stream = RequireStream(a[0]);
            var position = a[1].Int64Value;
            if (position < 0 || position > stream.Bytes.Length)
                throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
            stream.Position = (int)position;
            return null;
        }, BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(MemoryStreamType, "get_Length"), static (_, a) =>
            StackSlot.OfInt64(RequireStream(a[0]).Bytes.Length), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(MemoryStreamType, "ToArray"), static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeByteArray(RequireStream(a[0]).Bytes)), BindingOrigin.Managed);
        r.RegisterBinding(BindingKey.Instance(MemoryStreamType, "Read", "System.Byte[]", "System.Int32", "System.Int32"),
            static (ctx, a) => Read(ctx, a), BindingOrigin.Managed);
    }

    private static void RegisterAssemblyLoadContext(IntrinsicRegistry r) {
        const string t = LoadContextType;
        r.Register(IntrinsicKey.Instance(t, ".ctor", 1), static (ctx, a) => {
            InitializeBase(ctx, a, collectible: false);
            return null;
        });
        r.Register(IntrinsicKey.Instance(t, ".ctor", 2), static (ctx, a) => {
            InitializeBase(ctx, a, collectible: a[2].AsInt32 != 0);
            return null;
        });
        r.Register(IntrinsicKey.Static(t, "get_Default", 0), static (ctx, _) =>
            StackSlot.OfObject(ctx.Heap.Allocate(RequireDefault(ctx))));
        r.Register(IntrinsicKey.Instance(t, "get_Name", 0), static (ctx, a) => {
            var loadContext = Require(a[0]);
            return loadContext.Name is null ? StackSlot.Null : StackSlot.OfObject(ctx.MakeString(loadContext.Name));
        });
        r.Register(IntrinsicKey.Instance(t, "get_IsCollectible", 0), static (_, a) =>
            StackSlot.OfInt32(Require(a[0]).IsCollectible ? 1 : 0));
        r.Register(IntrinsicKey.Instance(t, "get_Assemblies", 0), static (ctx, a) => {
            var loadContext = RequireActive(a[0]);
            return Assemblies(ctx, loadContext);
        });
        r.Register(IntrinsicKey.Instance(t, "LoadFromAssemblyPath", 1), static (ctx, a) => {
            var loadContext = RequireActive(a[0]);
            var path = StringArg(a[1]);
            var loader = ctx.LoadAssemblyFromPath?.Invoke(loadContext, path)
                ?? throw new OperationNotAllowedException("AssemblyLoadContext のパスロードは VM ホストから利用できません。");
            return Assembly(ctx, loader);
        });
        r.Register(IntrinsicKey.Instance(t, "LoadFromAssemblyName", 1), static (ctx, a) => {
            var loadContext = RequireActive(a[0]);
            return LoadByName(ctx, loadContext, a[1]);
        });
        r.Register(IntrinsicKey.Instance(t, "LoadFromStream", 1), static (ctx, a) =>
            LoadFromStream(ctx, RequireActive(a[0]), a[1]));
        r.Register(IntrinsicKey.Instance(t, "LoadFromStream", 2), static (ctx, a) =>
            LoadFromStream(ctx, RequireActive(a[0]), a[1]));
        // VM では Stream の標準面をまだ持たないため、byte[] を受け取る便利な面だけは
        // LoadFromAssemblyBytes として明示的に提供する。実際の CLR シグネチャとは衝突しない。
        r.Register(IntrinsicKey.Instance(t, "LoadFromAssemblyBytes", 1), static (ctx, a) => {
            var loadContext = RequireActive(a[0]);
            var length = CheckAssemblyByteArray(ctx, a[1]);
            ctx.Heap.ChargeHostBuffer(length);
            var bytes = ctx.ReadByteArray(a[1]);
            var loader = ctx.LoadAssemblyInContext?.Invoke(loadContext, bytes)
                ?? throw new OperationNotAllowedException("AssemblyLoadContext の動的ローダーは VM ホストから利用できません。");
            return Assembly(ctx, loader);
        });
        r.Register(IntrinsicKey.Instance(t, "Load", 1), static (ctx, a) => {
            var loadContext = RequireActive(a[0]);
            return LoadByName(ctx, loadContext, a[1]);
        });
        r.Register(IntrinsicKey.Instance(t, "Unload", 0), static (_, a) => {
            var loadContext = Require(a[0]);
            try {
                loadContext.Unload();
            } catch (InvalidOperationException ex) {
                throw new UnhandledGuestException("System.InvalidOperationException", ex.Message);
            }
            return null;
        });
    }

    private static void RegisterAssemblyName(IntrinsicRegistry r) {
        const string t = AssemblyNameType;
        r.Register(IntrinsicKey.Instance(t, ".ctor", 0), static (ctx, a) => {
            SetAssemblyName(a, "");
            return null;
        });
        r.Register(IntrinsicKey.Instance(t, ".ctor", 1), static (_, a) => {
            SetAssemblyName(a, StringArg(a[1]));
            return null;
        });
        r.Register(IntrinsicKey.Instance(t, "get_Name", 0), static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(RequireName(a[0]).Name)));
        r.Register(IntrinsicKey.Instance(t, "get_FullName", 0), static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(RequireName(a[0]).FullName)));
        r.Register(IntrinsicKey.Instance(t, "ToString", 0), static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeString(RequireName(a[0]).FullName)));
    }

    private static void RegisterMemoryStream(IntrinsicRegistry r) {
        const string t = MemoryStreamType;
        r.Register(IntrinsicKey.Instance(t, ".ctor", 0), static (_, a) => {
            SetStreamBytes(a, []);
            return null;
        });
        r.Register(IntrinsicKey.Instance(t, ".ctor", 1), static (ctx, a) => {
            ctx.Heap.ChargeHostBuffer(ctx.GetByteArrayLength(a[1]));
            SetStreamBytes(a, ctx.ReadByteArray(a[1]));
            return null;
        });
        r.Register(IntrinsicKey.Instance(t, "get_Position", 0), static (_, a) =>
            StackSlot.OfInt64(RequireStream(a[0]).Position));
        r.Register(IntrinsicKey.Instance(t, "set_Position", 1), static (_, a) => {
            var stream = RequireStream(a[0]);
            var position = a[1].Int64Value;
            if (position < 0 || position > stream.Bytes.Length)
                throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
            stream.Position = (int)position;
            return null;
        });
        r.Register(IntrinsicKey.Instance(t, "get_Length", 0), static (_, a) =>
            StackSlot.OfInt64(RequireStream(a[0]).Bytes.Length));
        r.Register(IntrinsicKey.Instance(t, "ToArray", 0), static (ctx, a) =>
            StackSlot.OfObject(ctx.MakeByteArray(RequireStream(a[0]).Bytes)));
        r.Register(IntrinsicKey.Instance(t, "Read", 3), static (ctx, a) => Read(ctx, a));
    }

    /// <summary>ObjectEngine の特殊構築経路。AssemblyLoadContext/AssemblyName は専用 VM オブジェクトを持つ。</summary>
    internal static StackSlot Construct(IntrinsicContext ctx, string typeName, StackSlot[] args) {
        if (typeName == LoadContextType) {
            var name = args.Length > 1 && args[1].ObjectValue is VmString text ? text.Value : null;
            var collectible = args.Length > 2 && args[2].AsInt32 != 0;
            var loadContext = ctx.CreateAssemblyLoadContext?.Invoke(name, collectible)
                ?? throw new OperationNotAllowedException("AssemblyLoadContext は VM ホストから利用できません。");
            return StackSlot.OfObject(ctx.Heap.Allocate(loadContext));
        }
        if (typeName == AssemblyNameType) {
            var fullName = args.Length > 1 && args[1].ObjectValue is VmString text ? text.Value : "";
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmAssemblyNameObject { FullName = fullName }));
        }
        if (typeName == MemoryStreamType) {
            var bytes = Array.Empty<byte>();
            if (args.Length > 1 && args[1].ObjectValue is VmArray) {
                ctx.Heap.ChargeHostBuffer(ctx.GetByteArrayLength(args[1]));
                bytes = ctx.ReadByteArray(args[1]);
            }
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmMemoryStreamObject { Bytes = bytes }));
        }
        throw new NotSupportedException($"特殊 intrinsic 構築型 {typeName} は未対応です。");
    }

    private static VmAssemblyLoadContext RequireDefault(IntrinsicContext ctx) =>
        ctx.DefaultAssemblyLoadContext
        ?? throw new OperationNotAllowedException("AssemblyLoadContext.Default は VM ホストから利用できません。");

    private static VmAssemblyLoadContext Require(StackSlot slot) =>
        slot.ObjectValue switch {
            VmAssemblyLoadContext loadContext => loadContext,
            VmClassInstance { AssemblyLoadContextHandle: { } loadContext } => loadContext,
            _ => throw new UnhandledGuestException("System.NullReferenceException", null),
        };

    private static VmAssemblyLoadContext RequireActive(StackSlot slot) {
        VmLifetime.EnsureLiveForGuest(slot);
        var loadContext = Require(slot);
        if (loadContext.IsUnloaded)
            throw new UnhandledGuestException("System.ObjectDisposedException", "AssemblyLoadContext はアンロード済みです。");
        return loadContext;
    }

    private static VmAssemblyNameObject RequireName(StackSlot slot) =>
        slot.ObjectValue as VmAssemblyNameObject
        ?? throw new UnhandledGuestException("System.NullReferenceException", null);

    private static string StringArg(StackSlot slot) =>
        slot.ObjectValue is VmString text
            ? text.Value
            : throw new UnhandledGuestException("System.ArgumentNullException", null);

    private static string SimpleName(string fullName) {
        var comma = fullName.IndexOf(',');
        return (comma < 0 ? fullName : fullName[..comma]).Trim();
    }

    private static void SetAssemblyName(StackSlot[] args, string fullName) {
        // AssemblyName の .ctor は ObjectEngine が専用オブジェクトを用意してから呼ぶ。
        if (args[0].ObjectValue is VmAssemblyNameObject current)
            current.FullName = fullName;
        else
            throw new InvalidOperationException("AssemblyName の構築状態が不正です。");
    }

    private static StackSlot Assembly(IntrinsicContext ctx, TypeLoader loader) =>
        StackSlot.OfObject(ctx.Heap.Allocate(new VmAssemblyObject { Loader = loader }));

    private static StackSlot LoadFromStream(IntrinsicContext ctx, VmAssemblyLoadContext loadContext, StackSlot streamSlot) {
        var stream = streamSlot.ObjectValue as VmMemoryStreamObject
            ?? throw new UnhandledGuestException("System.NotSupportedException", "VM では MemoryStream のみ AssemblyLoadContext に渡せます。");
        var remaining = stream.Bytes.Length - stream.Position;
        CheckAssemblySize(ctx, remaining);
        // PEImage retains its input memory. Copy only the unread slice so a
        // tiny assembly loaded from the tail of a large MemoryStream does not
        // keep the entire guest-provided backing array alive.
        var bytes = stream.Bytes.AsSpan(stream.Position, remaining).ToArray();
        var loader = ctx.LoadAssemblyInContext?.Invoke(loadContext, bytes)
            ?? throw new OperationNotAllowedException("AssemblyLoadContext の動的ローダーは VM ホストから利用できません。");
        stream.Position = stream.Bytes.Length;
        return Assembly(ctx, loader);
    }

    private static VmMemoryStreamObject RequireStream(StackSlot slot) =>
        slot.ObjectValue as VmMemoryStreamObject
        ?? throw new UnhandledGuestException("System.NullReferenceException", null);

    private static void SetStreamBytes(StackSlot[] args, byte[] bytes) {
        if (args[0].ObjectValue is not VmMemoryStreamObject stream)
            throw new InvalidOperationException("MemoryStream の構築状態が不正です。");
        stream.ReplaceBytes(bytes);
    }

    private static StackSlot Read(IntrinsicContext ctx, StackSlot[] args) {
        var stream = RequireStream(args[0]);
        var destination = args[1].ObjectValue as VmArray
            ?? throw new UnhandledGuestException("System.ArgumentNullException", null);
        var offset = args[2].AsInt32;
        var count = args[3].AsInt32;
        if (offset < 0 || count < 0 || offset > destination.Length - count)
            throw new UnhandledGuestException("System.ArgumentOutOfRangeException", null);
        var copied = Math.Min(count, stream.Bytes.Length - stream.Position);
        for (var i = 0; i < copied; i++)
            destination.Elements[offset + i] = StackSlot.OfInt32(stream.Bytes[stream.Position + i]);
        stream.Position += copied;
        return StackSlot.OfInt32(copied);
    }

    private static void InitializeBase(IntrinsicContext ctx, StackSlot[] args, bool collectible) {
        var name = args.Length > 1 && args[1].ObjectValue is VmString text ? text.Value : null;
        var loadContext = ctx.CreateAssemblyLoadContext?.Invoke(name, collectible)
            ?? throw new OperationNotAllowedException("AssemblyLoadContext は VM ホストから利用できません。");
        switch (args[0].ObjectValue) {
            case VmClassInstance instance:
                instance.AssemblyLoadContextHandle = loadContext;
                break;
            case VmAssemblyLoadContext:
                // ObjectEngine の専用構築経路では既に VM ハンドルを生成済み。
                break;
            default:
                throw new UnhandledGuestException("System.InvalidOperationException", "AssemblyLoadContext の this が不正です。");
        }
    }

    private static StackSlot Assemblies(IntrinsicContext ctx, VmAssemblyLoadContext loadContext) {
        lock (loadContext.LifetimeGate) {
            VmLifetime.EnsureLiveForGuest(loadContext);
            var assemblyType = (VmType?)ctx.Types.FindTypeByFullName("System.Reflection.Assembly")
                ?? VmAssemblyObject.AssemblyFacade;
            var elements = loadContext.Context.Loaders
                .Select(loader => StackSlot.OfObject(ctx.Heap.Allocate(new VmAssemblyObject { Loader = loader })))
                .ToArray();
            return StackSlot.OfObject(ctx.Heap.Allocate(new VmArray(
                new VmArrayType { ElementType = assemblyType }, elements)));
        }
    }

    private static StackSlot LoadByName(IntrinsicContext ctx, VmAssemblyLoadContext loadContext, StackSlot slot) {
        lock (loadContext.LifetimeGate) {
            VmLifetime.EnsureLiveForGuest(loadContext);
            var fullName = slot.ObjectValue switch {
                VmAssemblyNameObject assemblyName => assemblyName.FullName,
                VmString text => text.Value,
                _ => throw new UnhandledGuestException("System.ArgumentNullException", null),
            };
            TypeLoader? loader;
            if (fullName.Contains(',')) {
                AssemblyIdentity identity;
                try {
                    identity = AssemblyIdentity.ParseFullName(fullName);
                } catch (Exception ex) when (ex is ArgumentException or FileLoadException) {
                    throw new UnhandledGuestException("System.ArgumentException", ex.Message);
                }
                loader = loadContext.Context.FindByIdentity(identity);
            } else {
                loader = loadContext.Context.FindBySimpleName(SimpleName(fullName));
            }
            if (loader is null)
                throw new UnhandledGuestException("System.IO.FileNotFoundException",
                    $"アセンブリ '{fullName}' が見つかりません。");
            return Assembly(ctx, loader);
        }
    }

    private static int CheckAssemblyByteArray(IntrinsicContext ctx, in StackSlot slot) {
        var length = ctx.GetByteArrayLength(slot);
        CheckAssemblySize(ctx, length);
        return length;
    }

    private static void CheckAssemblySize(IntrinsicContext ctx, long length) {
        if (ctx.MemoryPolicy is { } policy && length > policy.MaxAssemblyBytes)
            throw new OperationNotAllowedException(
                $"AssemblyLoadContext の入力が上限を超えています (上限 {policy.MaxAssemblyBytes:N0} バイト)。");
    }
}
