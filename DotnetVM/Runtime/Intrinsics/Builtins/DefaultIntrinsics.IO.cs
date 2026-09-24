using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
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
}
