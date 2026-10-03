using System.Security.Cryptography;
using DotnetVM.Runtime.Execution;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static void RegisterAes(IntrinsicRegistry r) {
        const string A = "System.Security.Cryptography.Aes";
        BclFace(r, A, "Create", false, [], static (ctx, _) => {
            var aes = Aes.Create(); var key = new byte[32]; var iv = new byte[16];
            ctx.Heap.ChargeHostBuffer(48); ctx.Shared.FillRandom(key); ctx.Shared.FillRandom(iv);
            aes.Key = key; aes.IV = iv; CryptographicOperations.ZeroMemory(key);
            return WrapBcl(ctx, A, aes);
        });
        foreach (var type in new[] { A, "System.Security.Cryptography.SymmetricAlgorithm" }) {
            BclFace(r, type, "Dispose", true, [], static (_, a) => { BclValue<Aes>(a[0]).Dispose(); return null; });
            foreach (var property in new[] { "Key", "IV" }) {
                BclFace(r, type, "get_" + property, true, [], (ctx, a) => StackSlot.OfObject(ctx.MakeByteArray(property == "Key" ? BclValue<Aes>(a[0]).Key : BclValue<Aes>(a[0]).IV)));
                BclFace(r, type, "set_" + property, true, ["System.Byte[]"], (ctx, a) => { var bytes = EncodingBytes(ctx, a[1], "System.Byte[]"); if (property == "Key") BclValue<Aes>(a[0]).Key = bytes; else BclValue<Aes>(a[0]).IV = bytes; return null; });
            }
            foreach (var method in new[] { "EncryptCbc", "DecryptCbc", "EncryptEcb", "DecryptEcb" })
                foreach (var input in new[] { "System.Byte[]", "System.ReadOnlySpan`1<System.Byte>" }) {
                    var cbc = method.EndsWith("Cbc", StringComparison.Ordinal); var encrypt = method.StartsWith("Encrypt", StringComparison.Ordinal);
                    var parameters = cbc ? new[] { input, input, "System.Security.Cryptography.PaddingMode" } : new[] { input, "System.Security.Cryptography.PaddingMode" };
                    BclFace(r, type, method, true, parameters, (ctx, a) => {
                        var aes = BclValue<Aes>(a[0]); var bytes = EncodingBytes(ctx, a[1], input); var padding = (PaddingMode)a[^1].AsInt32;
                        ctx.Heap.ChargeHostBuffer(checked(bytes.Length + 16));
                        var iv = cbc ? EncodingBytes(ctx, a[2], input) : Array.Empty<byte>();
                        var result = (encrypt, cbc) switch {
                            (true, true) => aes.EncryptCbc(bytes, iv, padding), (false, true) => aes.DecryptCbc(bytes, iv, padding),
                            (true, false) => aes.EncryptEcb(bytes, padding), _ => aes.DecryptEcb(bytes, padding),
                        };
                        return StackSlot.OfObject(ctx.MakeByteArray(result));
                    });
                }
        }
    }
}
