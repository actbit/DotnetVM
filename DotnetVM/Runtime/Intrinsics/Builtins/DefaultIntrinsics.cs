using DotnetVM.Metadata;
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
public static partial class DefaultIntrinsics {
    /// <summary>VM 起動時に標準 intrinsic をすべて登録する。</summary>
    public static void RegisterAll(IntrinsicRegistry registry) {
        RegisterObject(registry);
        RegisterString(registry);
        RegisterPrimitiveToString(registry);
        RegisterChar(registry);
        RegisterMath(registry);
        RegisterConsole(registry);
        RegisterConvert(registry);
        RegisterExceptions(registry);
        RegisterType(registry);
        RegisterMethodBase(registry);
        RegisterAssembly(registry);
        AssemblyLoadContextRuntime.RegisterAll(registry);
        RegisterReflectionEmit(registry);
        RegisterInterpolatedStringHandler(registry);
        RegisterDisposable(registry);
        RegisterRuntimeHelpers(registry);
        RegisterDelegates(registry);
        RegisterInterlocked(registry);
        RegisterFile(registry);
        RegisterWebClient(registry);
    }
}
