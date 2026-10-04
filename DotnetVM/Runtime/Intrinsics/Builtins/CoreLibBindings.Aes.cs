using System.Security.Cryptography;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    internal static readonly Type[] CryptoBoundaryTypes = [typeof(SymmetricAlgorithm), typeof(ICryptoTransform), typeof(CryptoStream), typeof(KeySizes), typeof(AesGcm)];
    internal static bool IncludeCryptoBoundary(System.Reflection.MethodBase method) => method.DeclaringType == typeof(AesGcm)
        ? method.Name is not ("Encrypt" or "Decrypt")
        : method.DeclaringType != typeof(SymmetricAlgorithm) || method.Name is not ("Create" or "GenerateKey" or "GenerateIV" or "Dispose" or "get_Key" or "set_Key" or "get_IV" or "set_IV" or "EncryptCbc" or "DecryptCbc" or "EncryptEcb" or "DecryptEcb");
    private static void RegisterAes(IntrinsicRegistry r) {
        foreach (var type in CryptoBoundaryTypes) RegisterHostBoundary(r, type, IncludeCryptoBoundary);
        BclFace(r, "System.Security.Cryptography.ICryptoTransform", "Dispose", true, [], static (_, a) => { BclValue<ICryptoTransform>(a[0]).Dispose(); return null; });
        const string A = "System.Security.Cryptography.Aes";
        BclFace(r, A, "Create", false, [], static (ctx, _) => {
            var aes = Aes.Create(); var key = new byte[32]; var iv = new byte[16];
            ctx.Heap.ChargeHostBuffer(48); ctx.Shared.FillRandom(key); ctx.Shared.FillRandom(iv);
            aes.Key = key; aes.IV = iv; CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(iv);
            return WrapBcl(ctx, A, aes);
        });
        foreach (var type in new[] { A, "System.Security.Cryptography.SymmetricAlgorithm" }) {
            foreach (var property in new[] { "Key", "IV" }) BclFace(r, type, "Generate" + property, true, [], (ctx, a) => {
                var aes = BclValue<Aes>(a[0]); var bytes = new byte[property == "Key" ? aes.KeySize / 8 : aes.BlockSize / 8];
                ctx.Heap.ChargeHostBuffer(bytes.Length); ctx.Shared.FillRandom(bytes);
                try { if (property == "Key") aes.Key = bytes; else aes.IV = bytes; }
                finally { CryptographicOperations.ZeroMemory(bytes); } return null;
            });
            BclFace(r, type, "Dispose", true, [], static (_, a) => { BclValue<Aes>(a[0]).Dispose(); return null; });
            foreach (var property in new[] { "Key", "IV" }) {
                BclFace(r, type, "get_" + property, true, [], (ctx, a) => StackSlot.OfObject(ctx.MakeByteArray(property == "Key" ? BclValue<Aes>(a[0]).Key : BclValue<Aes>(a[0]).IV)));
                BclFace(r, type, "set_" + property, true, ["System.Byte[]"], (ctx, a) => { var bytes = EncodingBytes(ctx, a[1], "System.Byte[]"); try { if (property == "Key") BclValue<Aes>(a[0]).Key = bytes; else BclValue<Aes>(a[0]).IV = bytes; } finally { CryptographicOperations.ZeroMemory(bytes); } return null; });
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
            RegisterAesSpanDestinations(r, type);
        }
        RegisterAesGcm(r);
    }
    private static void RegisterAesGcm(IntrinsicRegistry r) {
        const string type = "System.Security.Cryptography.AesGcm";
        const string read = "System.ReadOnlySpan`1<System.Byte>", write = "System.Span`1<System.Byte>";
        foreach (var sized in new[] { false, true }) BclConstructor(r, type, sized ? [read, "System.Int32"] : [read], (ctx, a) => {
            var key = EncodingBytes(ctx, a[1], "span");
            try {
#pragma warning disable SYSLIB0053
                SetBclValue(a[0], sized ? new AesGcm(key, a[2].AsInt32) : new AesGcm(key));
#pragma warning restore SYSLIB0053
            } finally { CryptographicOperations.ZeroMemory(key); } return null;
        });
        foreach (var method in new[] { "Encrypt", "Decrypt" }) BclFace(r, type, method, true,
            [read, read, method == "Encrypt" ? write : read, write, read], (ctx, a) => {
                var buffers = a.Skip(1).Select(v => EncodingBytes(ctx, v, "span")).ToArray();
                try {
                    ctx.Heap.ChargeHostWork(buffers[1].Length);
                    if (method == "Encrypt") BclValue<AesGcm>(a[0]).Encrypt(buffers[0], buffers[1], buffers[2], buffers[3], buffers[4]);
                    else BclValue<AesGcm>(a[0]).Decrypt(buffers[0], buffers[1], buffers[2], buffers[3], buffers[4]);
                } finally {
                    foreach (var index in method == "Encrypt" ? new[] { 2, 3 } : new[] { 3 }) WriteBoundaryBytes(a[index + 1], buffers[index]);
                    foreach (var bytes in buffers) CryptographicOperations.ZeroMemory(bytes);
                } return null;
            });
        foreach (var method in new[] { "Encrypt", "Decrypt" }) BclFace(r, type, method, true,
            ["System.Byte[]", "System.Byte[]", "System.Byte[]", "System.Byte[]", "System.Byte[]"], (ctx, a) => {
                var buffers = a.Skip(1).Select(v => v.ObjectValue is null ? null : EncodingBytes(ctx, v, "System.Byte[]")).ToArray();
                try {
                    ctx.Heap.ChargeHostWork(buffers[1]!.Length);
                    if (method == "Encrypt") BclValue<AesGcm>(a[0]).Encrypt(buffers[0]!, buffers[1]!, buffers[2]!, buffers[3]!, buffers[4]);
                    else BclValue<AesGcm>(a[0]).Decrypt(buffers[0]!, buffers[1]!, buffers[2]!, buffers[3]!, buffers[4]);
                } finally {
                    foreach (var index in method == "Encrypt" ? new[] { 2, 3 } : new[] { 3 })
                        if (a[index + 1].ObjectValue is VmArray array && buffers[index] is { } bytes)
                            for (int i = 0; i < array.Length; i++) array.Elements[i] = StackSlot.OfInt32(bytes[i]);
                }
                return null;
            });
    }

    private static void RegisterAesSpanDestinations(IntrinsicRegistry r, string type) {
        const string read = "System.ReadOnlySpan`1<System.Byte>", write = "System.Span`1<System.Byte>", paddingType = "System.Security.Cryptography.PaddingMode";
        foreach (var mode in new[] { "Ecb", "Cbc", "Cfb" }) foreach (var encrypt in new[] { false, true }) foreach (var attempt in new[] { false, true }) {
            var method = (attempt ? "Try" : "") + (encrypt ? "Encrypt" : "Decrypt") + mode;
            var parameters = new List<string> { read }; if (mode != "Ecb") parameters.Add(read); parameters.Add(write);
            if (attempt && mode != "Ecb") parameters.Add("System.Int32&"); parameters.Add(paddingType);
            if (attempt && mode == "Ecb") parameters.Add("System.Int32&"); if (mode == "Cfb") parameters.Add("System.Int32");
            BclFace(r, type, method, true, parameters.ToArray(), (ctx, a) => {
                var aes = BclValue<Aes>(a[0]); var input = EncodingBytes(ctx, a[1], "span");
                var iv = mode == "Ecb" ? [] : EncodingBytes(ctx, a[2], "span");
                var destinationIndex = mode == "Ecb" ? 2 : 3;
                var output = EncodingBytes(ctx, a[destinationIndex], "span");
                var padding = (PaddingMode)a[Array.IndexOf(parameters.ToArray(), paddingType) + 1].AsInt32;
                var feedback = mode == "Cfb" ? a[^1].AsInt32 : 8;
                int written = 0;
                try {
                    ctx.Heap.ChargeHostWork(input.Length);
                    bool success = (mode, encrypt) switch {
                        ("Ecb", true) => aes.TryEncryptEcb(input, output, padding, out written),
                        ("Ecb", false) => aes.TryDecryptEcb(input, output, padding, out written),
                        ("Cbc", true) => aes.TryEncryptCbc(input, iv, output, out written, padding),
                        ("Cbc", false) => aes.TryDecryptCbc(input, iv, output, out written, padding),
                        ("Cfb", true) => aes.TryEncryptCfb(input, iv, output, out written, padding, feedback),
                        _ => aes.TryDecryptCfb(input, iv, output, out written, padding, feedback),
                    };
                    if (!success && !attempt) throw new ArgumentException("Destination is too short.");
                    if (success) WriteBoundaryBytes(a[destinationIndex], output.AsSpan(0, written));
                    if (attempt) { ((VmByRef)a[Array.IndexOf(parameters.ToArray(), "System.Int32&") + 1].ObjectValue!).Write(StackSlot.OfInt32(written)); return StackSlot.OfInt32(success ? 1 : 0); }
                    return StackSlot.OfInt32(written);
                } finally { CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(iv); CryptographicOperations.ZeroMemory(output); }
            });
        }
    }
}
