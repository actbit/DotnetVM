using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private static void RegisterAdditionalBcl(IntrinsicRegistry r) {
        RegisterRegex(r);
        RegisterCrypto(r);
        RegisterCompression(r);
        RegisterHttpClient(r);
    }

    private static void BclFace(IntrinsicRegistry r, string type, string method, bool instance, string[] parameters, IntrinsicImpl impl) =>
        r.RegisterBinding(instance ? BindingKey.Instance(type, method, parameters) : BindingKey.Static(type, method, parameters),
            (ctx, a) => BclCall(() => impl(ctx, a)), BindingOrigin.Managed);

    private static void BclConstructor(IntrinsicRegistry r, string type, string[] parameters, IntrinsicImpl impl) {
        BclFace(r, type, ".ctor", true, parameters, impl);
        var key = IntrinsicKey.Instance(type, ".ctor", parameters.Length);
        if (!r.TryGet(key, out _)) r.Register(key, (ctx, a) => BclCall(() => impl(ctx, a)));
    }

    private static Regex NewRegex(IntrinsicContext ctx, string pattern, RegexOptions options, TimeSpan? timeout = null) {
        var policy = ctx.MemoryPolicy;
        var maximum = policy?.MaxRegexMatchTimeoutMilliseconds ?? 250;
        if (maximum <= 0 || pattern.Length > (policy?.MaxRegexPatternLength ?? 16384))
            throw new MemoryQuotaExceededException("Regex pattern or timeout policy exceeded.");
        var effective = timeout ?? TimeSpan.FromMilliseconds(maximum);
        if (effective <= TimeSpan.Zero || effective.TotalMilliseconds > maximum)
            throw new ArgumentOutOfRangeException("matchTimeout", "Regex timeout exceeds the VM limit.");
        ctx.Heap.ChargeHostWork(pattern.Length);
        ctx.Heap.ChargeHostBuffer(pattern.Length);
        // Compiled host code is deliberately unnecessary for this VM boundary.
        return new Regex(pattern, options & ~RegexOptions.Compiled, effective);
    }

    private static string RegexInput(IntrinsicContext ctx, StackSlot slot) {
        var input = StringValue(slot) ?? throw new ArgumentNullException("input");
        if (input.Length > (ctx.MemoryPolicy?.MaxRegexInputLength ?? 1024 * 1024))
            throw new MemoryQuotaExceededException("Regex input limit exceeded.");
        ctx.Heap.ChargeHostWork(input.Length);
        return input;
    }

    private static void RegisterRegex(IntrinsicRegistry r) {
        const string T = "System.Text.RegularExpressions.Regex";
        foreach (var parameters in new[] { new[] { "System.String" }, new[] { "System.String", "System.Text.RegularExpressions.RegexOptions" }, new[] { "System.String", "System.Text.RegularExpressions.RegexOptions", "System.TimeSpan" } })
            BclConstructor(r, T, parameters, (ctx, a) => {
                SetBclValue(a[0], NewRegex(ctx, StringValue(a[1]) ?? throw new ArgumentNullException("pattern"),
                    a.Length > 2 ? (RegexOptions)a[2].AsInt32 : RegexOptions.None, a.Length > 3 ? TimeSpan.FromMilliseconds(TimeSpanMilliseconds(a[3])) : null));
                return null;
            });
        foreach (var method in new[] { "IsMatch", "Match", "Replace", "Split" }) {
            StackSlot? Run(IntrinsicContext ctx, StackSlot[] a, Regex regex, int inputAt) {
                var input = RegexInput(ctx, a[inputAt]);
                try {
                    if (method == "IsMatch") return StackSlot.OfInt32(regex.IsMatch(input) ? 1 : 0);
                    if (method == "Match") return WrapBcl(ctx, "System.Text.RegularExpressions.Match", regex.Match(input));
                    if (method == "Replace") {
                        var replacement = StringValue(a[inputAt + 1]) ?? throw new ArgumentNullException("replacement");
                        // Replacement tokens can expand to an entire prefix, suffix or capture.
                        // Reserve each match's upper bound before Match.Result constructs it.
                        var tokens = replacement.Count(static c => c == '$');
                        var bound = (long)replacement.Length + (long)tokens * input.Length;
                        ChargeRegexExpansion(ctx, 2L * input.Length);
                        return StackSlot.OfObject(ctx.MakeString(regex.Replace(input, match => {
                            ChargeRegexExpansion(ctx, 2 * bound);
                            return match.Result(replacement);
                        })));
                    }
                    // Split includes every successful capture, including captures in lookahead.
                    // Bound those strings and the result array before the host materializes them.
                    var captures = regex.GetGroupNumbers().Length - 1;
                    ChargeRegexExpansion(ctx, (input.Length + 1L) * (16 + (long)captures * input.Length));
                    return StackSlot.OfObject(ctx.MakeStringArray(regex.Split(input)));
                } catch (RegexMatchTimeoutException ex) { throw new UnhandledGuestException(ex.GetType().FullName!, ex.Message); }
            }
            var instanceParams = method == "Replace" ? new[] { "System.String", "System.String" } : new[] { "System.String" };
            BclFace(r, T, method, true, instanceParams, (ctx, a) => Run(ctx, a, BclValue<Regex>(a[0]), 1));
            foreach (var withOptions in new[] { false, true }) {
                var parameters = method == "Replace" ? new List<string> { "System.String", "System.String", "System.String" } : new List<string> { "System.String", "System.String" };
                if (withOptions) parameters.Add("System.Text.RegularExpressions.RegexOptions");
                BclFace(r, T, method, false, parameters.ToArray(), (ctx, a) => {
                    var regex = NewRegex(ctx, StringValue(a[1]) ?? throw new ArgumentNullException("pattern"), withOptions ? (RegexOptions)a[^1].AsInt32 : RegexOptions.None);
                    // Static Replace puts pattern between input and replacement.
                    return Run(ctx, method == "Replace" ? [a[0], a[2]] : a, regex, 0);
                });
            }
        }
        foreach (var type in new[] { "System.Text.RegularExpressions.Match", "System.Text.RegularExpressions.Group", "System.Text.RegularExpressions.Capture" }) {
            if (type != "System.Text.RegularExpressions.Capture") BclFace(r, type, "get_Success", true, [], static (_, a) => StackSlot.OfInt32(BclValue<Group>(a[0]).Success ? 1 : 0));
            BclFace(r, type, "get_Value", true, [], static (ctx, a) => StackSlot.OfObject(ctx.MakeString(BclValue<Capture>(a[0]).Value)));
            BclFace(r, type, "get_Index", true, [], static (_, a) => StackSlot.OfInt32(BclValue<Capture>(a[0]).Index));
            BclFace(r, type, "get_Length", true, [], static (_, a) => StackSlot.OfInt32(BclValue<Capture>(a[0]).Length));
        }
        BclFace(r, "System.Text.RegularExpressions.Match", "get_Groups", true, [], static (ctx, a) => WrapBcl(ctx, "System.Text.RegularExpressions.GroupCollection", BclValue<Match>(a[0]).Groups));
        foreach (var param in new[] { "System.String", "System.Int32" })
            BclFace(r, "System.Text.RegularExpressions.GroupCollection", "get_Item", true, [param], (ctx, a) => WrapBcl(ctx, "System.Text.RegularExpressions.Group", param == "System.String" ? BclValue<GroupCollection>(a[0])[StringValue(a[1])!] : BclValue<GroupCollection>(a[0])[a[1].AsInt32]));
    }

    private static void ChargeRegexExpansion(IntrinsicContext ctx, long chars) {
        if (chars > int.MaxValue) throw new MemoryQuotaExceededException("Regex result expansion exceeds the VM limit.");
        ctx.Heap.ChargeHostBuffer((int)chars);
        ctx.Heap.ChargeHostWork(chars);
    }

    private static void RegisterCrypto(IntrinsicRegistry r) {
        RegisterAes(r);
        foreach (var algorithm in new[] { "SHA256", "SHA384", "SHA512" }) {
            byte[] Hash(byte[] data) => algorithm switch { "SHA256" => SHA256.HashData(data), "SHA384" => SHA384.HashData(data), _ => SHA512.HashData(data) };
            var type = "System.Security.Cryptography." + algorithm;
            foreach (var input in new[] { "System.Byte[]", "System.ReadOnlySpan`1<System.Byte>" })
                BclFace(r, type, "HashData", false, [input], (ctx, a) => StackSlot.OfObject(ctx.MakeByteArray(Hash(EncodingBytes(ctx, a[0], input)))));
            BclFace(r, type, "HashData", false, ["System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>"], (ctx, a) => {
                var digest = Hash(EncodingBytes(ctx, a[0], "System.ReadOnlySpan`1<System.Byte>"));
                if (ReadSpanParts(a[1]).Length < digest.Length) throw new ArgumentException("Destination is too small.");
                WriteByteSpan(ReadSpanParts(a[1]).Reference, digest); return StackSlot.OfInt32(digest.Length);
            });
            BclFace(r, type, "TryHashData", false, ["System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>", "System.Int32&"], (ctx, a) => {
                var digest = Hash(EncodingBytes(ctx, a[0], "System.ReadOnlySpan`1<System.Byte>"));
                var dest = ReadSpanParts(a[1]);
                if (dest.Length < digest.Length) { WriteWritten(a[2], 0); return StackSlot.OfInt32(0); }
                WriteByteSpan(dest.Reference, digest); WriteWritten(a[2], digest.Length); return StackSlot.OfInt32(1);
            });
        }
        foreach (var input in new[] { "System.Byte[]", "System.ReadOnlySpan`1<System.Byte>" })
            BclFace(r, "System.Security.Cryptography.HMACSHA256", "HashData", false, [input, input], (ctx, a) => StackSlot.OfObject(ctx.MakeByteArray(HMACSHA256.HashData(EncodingBytes(ctx, a[0], input), EncodingBytes(ctx, a[1], input)))));
        BclFace(r, "System.Security.Cryptography.CryptographicOperations", "FixedTimeEquals", false, ["System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>"], static (ctx, a) => StackSlot.OfInt32(CryptographicOperations.FixedTimeEquals(EncodingBytes(ctx, a[0], "span"), EncodingBytes(ctx, a[1], "span")) ? 1 : 0));
        BclFace(r, "System.Security.Cryptography.CryptographicOperations", "ZeroMemory", false, ["System.Span`1<System.Byte>"], static (ctx, a) => {
            var dest = ReadSpanParts(a[0]); ctx.Heap.ChargeHostWork(dest.Length); ctx.Heap.ChargeHostBuffer(dest.Length);
            WriteByteSpan(dest.Reference, new byte[dest.Length]); return null;
        });
        BclFace(r, "System.Security.Cryptography.RandomNumberGenerator", "GetBytes", false, ["System.Int32"], static (ctx, a) => {
            var count = a[0].AsInt32; if (count < 0) throw new ArgumentOutOfRangeException("count");
            ctx.Heap.ChargeHostBuffer(count); ctx.Heap.ChargeHostWork(count); var bytes = new byte[count]; ctx.Shared.FillRandom(bytes);
            return StackSlot.OfObject(ctx.MakeByteArray(bytes));
        });
        BclFace(r, "System.Security.Cryptography.RandomNumberGenerator", "Fill", false, ["System.Span`1<System.Byte>"], static (ctx, a) => {
            var dest = ReadSpanParts(a[0]); ctx.Heap.ChargeHostBuffer(dest.Length); ctx.Heap.ChargeHostWork(dest.Length);
            var bytes = new byte[dest.Length]; ctx.Shared.FillRandom(bytes); WriteByteSpan(dest.Reference, bytes); return null;
        });
    }
}
