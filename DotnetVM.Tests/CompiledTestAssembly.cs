using System.Reflection;
using DotnetVM.Host;

namespace DotnetVM.Tests;

/// <summary>
/// コンパイル済みテストアセンブリを CLR と VM の両方で実行するための共通ハーネス。
/// テスト本体からアセンブリのロード手順を隠し、CLR/VM の突合コードを一貫させる。
/// </summary>
internal sealed class CompiledTestAssembly {
    private readonly byte[] _peImage;

    public CompiledTestAssembly(string source, string assemblyName = "TestAsm", bool allowUnsafe = false) {
        (_peImage, ClrAssembly) = TestAssemblyCompiler.Compile(source, assemblyName, allowUnsafe);
    }

    public Assembly ClrAssembly { get; }

    public object? InvokeClr(string typeName, string methodName, params object?[]? args) =>
        ClrAssembly.GetType(typeName)!.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args);

    public VirtualMachine CreateVm(bool loadHostCoreLib = true) {
        var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = loadHostCoreLib });
        using var stream = new MemoryStream(_peImage);
        vm.LoadAssembly(stream);
        return vm;
    }
}
