using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
    // ---- System.Console (仮想コンソールデバイス経由のみ) ----

    private static void RegisterConsole(IntrinsicRegistry r) {
        const string T = "System.Console";
        // legacy intrinsic (名前 + 引数個数) とランタイムバインド (Origin = Device) の両方に登録する。
        // 解決はランタイムバインド (優先順位 ①) が先に当たり、監査面にデバイス由来が現れる。
        // 仮想コンソールデバイス (VmConsole) がシンクであり続ける点は不変。
        // バインド側はメソッド名ごとに 1 件の全引数一致面とし、オーバーロード判別は
        // 実行時の宣言パラメータ型名 (ctx.ParameterTypeNames) で行う
        void DeviceBinding(string name, IntrinsicImpl impl) =>
            r.RegisterBinding(BindingKey.StaticAnyParams(T, name), impl, BindingOrigin.Device);

        // Write/WriteLine の全オーバーロードを同一キーに統合し、宣言パラメータ型
        // (ctx.ParameterTypeNames) で char / bool / object / params object[] 等を判別する
        foreach (var arity in new[] { 1, 2, 3, 4, 5 }) {
            r.Register(IntrinsicKey.Static(T, "Write", arity), WriteImpl(newline: false));
            r.Register(IntrinsicKey.Static(T, "WriteLine", arity), WriteImpl(newline: true));
        }
        r.Register(IntrinsicKey.Static(T, "WriteLine", 0), static (ctx, _) => {
            ctx.Console.Write(false, "\n");
            return null;
        });
        r.Register(IntrinsicKey.Static(T, "ReadLine", 0), static (ctx, _) => {
            var line = ctx.Console.ReadLine();
            return line is null ? StackSlot.Null : StackSlot.OfObject(ctx.MakeString(line));
        });

        DeviceBinding("Write", WriteImpl(newline: false));
        DeviceBinding("WriteLine", static (ctx, a) => {
            if (a.Length == 0) {
                ctx.Console.Write(false, "\n");
                return null;
            }
            return WriteImpl(newline: true)(ctx, a);
        });
        DeviceBinding("ReadLine", static (ctx, _) => {
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
}
