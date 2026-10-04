using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DotnetVM.Host;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DotnetVM.Tests;

public sealed class BclCompatibilityTests {
    private const string Source = """
        using System;
        using System.Globalization;
        using System.Text;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using System.Runtime.Intrinsics;
        using System.Threading.Tasks;
        using System.IO;
        public static class BclChecks {
            public sealed class JsonModel {
                public string Name { get; set; } = "";
                [JsonPropertyName("count")] public int Count { get; set; }
                [JsonIgnore] public int Hidden { get; set; }
                public List<int> Items { get; set; } = new();
            }
            public sealed class JsonImmutable {
                public int Number { get; }
                public string Text { get; init; }
                [JsonConstructor] public JsonImmutable(int number, string text) { Number = number; Text = text; }
            }
            public sealed class PlusOneConverter : JsonConverter<int> {
                public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetInt32() - 1;
                public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value + 1);
            }
            public static string JsonNullable() => JsonSerializer.Serialize<int?[]>(new int?[] { null, 42 }) + "|" + JsonSerializer.Deserialize<int?[]>("[null,42]")[1].Value;
            public static string JsonConstructor() { var value = JsonSerializer.Deserialize<JsonImmutable>("{\"Number\":42,\"Text\":\"日本\"}"); return JsonSerializer.Serialize(value); }
            public static string JsonConverter() { var options = new JsonSerializerOptions(); options.Converters.Add(new PlusOneConverter()); return JsonSerializer.Serialize(42, options) + "|" + JsonSerializer.Deserialize<int>("43", options); }
            public static string JsonUtf8() { var bytes = JsonSerializer.SerializeToUtf8Bytes(new [] {1,2}); return JsonSerializer.Deserialize<int[]>(bytes)[1] + "|" + Encoding.UTF8.GetString(bytes); }
            public static string StreamingUtf8Arrays() {
                var encoding = new UTF8Encoding(false, true); var encoder = encoding.GetEncoder();
                char[] chars = new char[] { '日', '本' }; var bytes = new byte[6];
                encoder.Convert(chars, 0, 2, bytes, 0, 3, true, out int consumed, out int written, out bool done);
                encoder.GetBytes(chars, consumed, 2 - consumed, bytes, written, true);
                var decoder = encoding.GetDecoder(); var result = new char[2];
                decoder.Convert(bytes, 0, 6, result, 0, 1, true, out int read, out int used, out bool complete);
                decoder.GetChars(bytes, read, 6 - read, result, used, true);
                return consumed + ":" + written + ":" + done + "|" + read + ":" + used + ":" + complete + "|" + new string(result);
            }
            public static string StreamingUtf8(bool strict) {
                var encoding = new UTF8Encoding(false, strict);
                var encoder = encoding.GetEncoder(); Span<byte> output = stackalloc byte[8];
                encoder.Convert("\uD83D".AsSpan(), output, false, out int chars1, out int bytes1, out bool complete1);
                encoder.Convert("\uDE00".AsSpan(), output, true, out int chars2, out int bytes2, out bool complete2);
                var decoder = encoding.GetDecoder(); Span<char> text = stackalloc char[4];
                decoder.Convert(output.Slice(0, 2), text, false, out int consumed1, out int written1, out bool done1);
                decoder.Convert(output.Slice(2, 2), text, true, out int consumed2, out int written2, out bool done2);
                string decoded = new string(text.Slice(0, written2)); decoder.Reset(); encoder.Reset();
                return chars1 + ":" + bytes1 + ":" + complete1 + "|" + chars2 + ":" + bytes2 + ":" + complete2 + "|" +
                    consumed1 + ":" + written1 + ":" + done1 + "|" + consumed2 + ":" + written2 + ":" + done2 + "|" + decoded;
            }
            public static bool StreamingUtf8StrictFlush() {
                var decoder = new UTF8Encoding(false, true).GetDecoder(); Span<char> chars = stackalloc char[4];
                decoder.GetChars(new byte[] { 0xE3 }.AsSpan(), chars, false);
                try { decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, true); return false; }
                catch (DecoderFallbackException) { decoder.Reset(); return decoder.GetChars(new byte[] { 65 }.AsSpan(), chars, true) == 1; }
            }
            public static string Culture(string name) {
                var c = new CultureInfo(name, false);
                return c.Name + "|" + c.Parent.Name + "|" + c.LCID + "|" + c.IsNeutralCulture + "|" +
                    c.NumberFormat.NumberDecimalSeparator + "|" + CultureInfo.GetCultureInfo(name).IsReadOnly;
            }
            public static string Provider() {
                var c = CultureInfo.GetCultureInfo("de-DE");
                return 1234.5.ToString("N2", c) + "|" + 1234.ToString(c) + "|" + 12.5m.ToString("F2", c) + "|" +
                    double.Parse("1.234,5", c).ToString("F1", CultureInfo.InvariantCulture) + "|" +
                    int.Parse("1.234", NumberStyles.Number, c) + "|" + Convert.ToInt32("1234", c) + "|" +
                    string.Format(c, "{0:N1}:{1:N0}", 1234.5, 1234);
            }
            public static string CustomProvider() {
                var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                c.NumberFormat.NegativeSign = "~";
                c.NumberFormat.NumberDecimalSeparator = ":";
                return (-42).ToString(c) + "|" + 1.5.ToString("F1", c.NumberFormat) + "|" + int.Parse("~42", c);
            }
            public static string ExplicitCase() {
                var c = new CultureInfo("tr-TR");
                return "iI".ToUpper(c) + "|" + "iI".ToLower(c) + "|" + char.ToUpper('i', c) + "|" + c.TextInfo.ToLower("I");
            }
            public static string ChangeCulture() {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");
                return CultureInfo.CurrentCulture.Name + "|" + CultureInfo.CurrentUICulture.Name + "|" +
                    12.5.ToString("F1") + "|" + Task.Run(() => 12.5.ToString("F1")).GetAwaiter().GetResult();
            }
            public static string InvalidCulture() {
                try { new CultureInfo((string)null); return "miss"; }
                catch (ArgumentNullException) { return "null"; }
            }
            public static string EncodingRoundtrip(string value, bool bom, bool strict) {
                Encoding e = new UTF8Encoding(bom, strict);
                byte[] bytes = e.GetBytes(value);
                return e.WebName + "|" + e.CodePage + "|" + e.GetPreamble().Length + "|" +
                    e.GetByteCount(value) + "|" + e.GetCharCount(bytes) + "|" + e.GetString(bytes);
            }
            public static string DefaultEncoding() => Encoding.UTF8.GetPreamble().Length + "|" +
                new UTF8Encoding().GetPreamble().Length + "|" + Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("日本😀"));
            public static string InvalidUtf8(bool strict, int kind) {
                var e = new UTF8Encoding(false, strict);
                try {
                    if (kind == 0) return e.GetString(new byte[] { 0xC0, 0xAF });
                    if (kind == 1) return e.GetString(new byte[] { 0xED, 0xA0, 0x80 });
                    if (kind == 2) return e.GetString(new byte[] { 0xF0, 0x9F, 0x98 });
                    return e.GetString(e.GetBytes("\uD800x\uDC00"));
                } catch (DecoderFallbackException) { return "decoder"; }
                  catch (EncoderFallbackException) { return "encoder"; }
            }
            public static string EncodingSlices() {
                var e = new UTF8Encoding(false, true);
                byte[] buffer = new byte[30];
                int count = e.GetBytes("x日本😀y", 1, 4, buffer, 3);
                char[] chars = new char[12];
                int written = e.GetChars(buffer, 3, count, chars, 2);
                var allChars = e.GetChars(e.GetBytes(new char[] { 'x', '日', '本', 'y' }, 1, 2));
                return count + "|" + written + "|" + new string(chars, 2, written) + "|" + e.GetString(buffer, 3, count) + "|" + new string(allChars);
            }
            public static string EncodingSpan() {
                var e = new UTF8Encoding(true, true);
                Span<byte> bytes = stackalloc byte[20];
                int n = e.GetBytes("日本😀".AsSpan(), bytes);
                Span<char> chars = stackalloc char[10];
                int m = e.GetChars(bytes.Slice(0, n), chars);
                return n + "|" + m + "|" + e.GetString(bytes.Slice(0, n));
            }
            public static string SpanOps() {
                int[] a = { 1, 2, 3, 4, 5 };
                var s = a.AsSpan();
                s.Slice(0, 4).CopyTo(s.Slice(1));
                MemoryMarshal.CreateSpan(ref a[2], 2).Fill(9);
                ref int first = ref MemoryMarshal.GetReference(s);
                ref int last = ref Unsafe.AddByteOffset(ref first, (IntPtr)16);
                return a[0] + ":" + a[1] + ":" + a[2] + ":" + a[3] + ":" + last + "|" +
                    (long)Unsafe.ByteOffset(ref first, ref last) + "|" + Unsafe.AreSame(ref Unsafe.Subtract(ref last, 4), ref first);
            }
            public static int BitCastIdentity() => Unsafe.BitCast<int, uint>(-1) == uint.MaxValue ? 1 : 0;
            public static int BitCastSizeMismatch() {
                try { return (int)Unsafe.BitCast<int, long>(1); }
                catch (NotSupportedException) { return -1; }
            }
            private struct Triple { public int A, B, C; }
            private struct Nested { public byte Prefix; public Triple Values; public short Suffix; }
            public static string RawStructs() {
                var item = new Nested { Prefix = 3, Values = new Triple { A = 1, B = 2, C = 7 }, Suffix = -9 };
                byte[] buffer = new byte[64]; MemoryMarshal.Write(buffer.AsSpan(1), in item);
                var copy = MemoryMarshal.Read<Nested>(buffer.AsSpan(1));
                var a = new Triple[] { item.Values, item.Values }; var b = new Triple[2]; a.AsSpan().CopyTo(b); a[0].C = 99;
                long bits = Unsafe.BitCast<double, long>(2.5); object boxed = (IntPtr)42;
                return Unsafe.SizeOf<Nested>() + "|" + copy.Prefix + "|" + copy.Values.C + "|" + copy.Suffix + "|" + b[0].C + "|" + Unsafe.BitCast<long, double>(bits).ToString(CultureInfo.InvariantCulture) + "|" + ((IntPtr)boxed).ToInt32();
            }
            public static string ProviderSpan() {
                Span<char> chars = stackalloc char[30];
                bool ok = 1234.5.TryFormat(chars, out int written, "N1", CultureInfo.GetCultureInfo("de-DE"));
                return ok + "|" + new string(chars.Slice(0, written));
            }
            private struct WithReference { public int Value; public object Tag; }
            public static string SpanClear() {
                var ints = new int[] { 1, 2, 3 }; ints.AsSpan(1).Clear();
                var values = new WithReference[] { new WithReference { Value = 3, Tag = new object() }, new WithReference { Value = 4, Tag = new object() } };
                values.AsSpan().Clear();
                var raw = new Triple[] { new Triple { A = 1, C = 9 } }; raw.AsSpan().Clear();
                return ints[0] + "|" + ints[1] + "|" + values[0].Value + "|" + (values[1].Tag == null) + "|" + raw[0].C;
            }
            public static int VectorOps() {
                var a = Vector128.Create(3);
                var b = Vector128.Create(7);
                var sum = a + b;
                return Vector128.GetElement(sum, 0) + Vector128.GetElement(sum, 3) + Vector128<int>.Count;
            }
            public static string VectorShapes() {
                var bytes = Vector64.Create((byte)250) + Vector64.Create((byte)10);
                var doubles = Vector256.Create(1.25) * Vector256.Create(2.0);
                var wide = Vector512.WithElement(Vector512.Create((short)2), 31, (short)-9);
                var floats = Vector128.Create(3.5f) - Vector128.Create(0.5f);
                bool rejected = false; try { _ = Vector128<char>.Count; } catch (NotSupportedException) { rejected = true; }
                return Vector64.GetElement(bytes, 7) + "|" + Vector256.GetElement(doubles, 3).ToString(CultureInfo.InvariantCulture) + "|" +
                    Vector512.GetElement(wide, 31) + "|" + Vector128.GetElement(floats, 0).ToString(CultureInfo.InvariantCulture) + "|" + rejected;
            }
            public static string JsonRead() {
                using var doc = JsonDocument.Parse("{\"name\":\"日本😀\",\"n\":12.5,\"ok\":true,\"items\":[1,2,3]}");
                var root = doc.RootElement;
                return root.GetProperty("name").GetString() + "|" + root.GetProperty("n").GetDouble().ToString(CultureInfo.InvariantCulture) +
                    "|" + root.GetProperty("ok").GetBoolean() + "|" + root.GetProperty("items").GetArrayLength();
            }
            public static string JsonWrite() {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream)) {
                    writer.WriteStartObject(); writer.WriteString("name", "日本😀");
                    writer.WriteBoolean("ok", true); writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            public static string JsonSerializeInt() => JsonSerializer.Serialize(42);
            public static int JsonDeserializeInt() => JsonSerializer.Deserialize<int>("42");
            public static bool TupleEquality() {
                var left = (typeof(int), typeof(string), true);
                var right = (typeof(int), typeof(string), true);
                return System.Collections.Generic.EqualityComparer<(Type,Type,bool)>.Default.Equals(left, right)
                    && !System.Collections.Generic.EqualityComparer<(Type,Type,bool)>.Default.Equals(left, (typeof(int), typeof(string), false));
            }
            public static string JsonCollections() {
                var value = new Dictionary<string, List<int>> { ["items"] = new List<int> { 1, 2, 3 } };
                var json = JsonSerializer.Serialize(value);
                return json + "|" + JsonSerializer.Deserialize<Dictionary<string, List<int>>>(json)["items"][2];
            }
            public static string JsonObjects() {
                var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
                var value = new JsonModel { Name = "日本😀", Count = 42, Hidden = 9, Items = new List<int> { 4, 5 } };
                var json = JsonSerializer.Serialize(value, options);
                var restored = JsonSerializer.Deserialize<JsonModel>(json, options);
                return json + "|" + restored.Name + "|" + restored.Count + "|" + restored.Items.Count + "|" + restored.Hidden;
            }
            public static bool JsonInvalid() {
                try { JsonSerializer.Deserialize<int>("{}"); return false; } catch (JsonException) { return true; }
            }
        }
        """;
    private static readonly Lazy<(byte[] Bytes, Assembly Clr)> Guest = new(() => {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "BclCompatibility",
            extraReferences: [MetadataReference.CreateFromFile(typeof(JsonSerializer).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Memory.dll"))], allowUnsafe: true);
        return (bytes, Assembly.Load(bytes));
    });

    private static VirtualMachine CreateVm(bool coreLib = true) {
        var vm = new VirtualMachine(new VmHostOptions {
            LoadHostCoreLib = coreLib, EnableJit = true, JitPromotionThreshold = 2,
            Memory = new MemoryPolicy { InstructionQuota = 100_000_000, MaxRecursionDepth = 64 },
        });
        vm.LoadAssembly(new MemoryStream(Guest.Value.Bytes));
        return vm;
    }

    private static object? Clr(string method, object?[] args) {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            return Guest.Value.Clr.GetType("BclChecks")!.GetMethod(method)!.Invoke(null, args);
        } finally {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    [Theory]
    [InlineData("Culture", "de-DE")]
    [InlineData("Culture", "tr-TR")]
    [InlineData("Culture", "ja-JP")]
    [InlineData("Culture", "")]
    [InlineData("Provider")]
    [InlineData("CustomProvider")]
    [InlineData("ExplicitCase")]
    [InlineData("InvalidCulture")]
    [InlineData("DefaultEncoding")]
    [InlineData("EncodingSlices")]
    [InlineData("EncodingSpan")]
    [InlineData("SpanOps")]
    [InlineData("BitCastIdentity")]
    [InlineData("BitCastSizeMismatch")]
    [InlineData("VectorOps")]
    [InlineData("VectorShapes")]
    [InlineData("RawStructs")]
    [InlineData("ProviderSpan")]
    [InlineData("SpanClear")]
    [InlineData("StreamingUtf8StrictFlush")]
    [InlineData("StreamingUtf8Arrays")]
    public void GuestBclMatchesClr(string method, params object?[] args) {
        var expected = Clr(method, args);
        using var vm = CreateVm();
        for (var i = 0; i < 3; i++) Assert.Equal(expected, vm.Invoke("BclChecks", method, args));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Utf8FlagsAndInvalidSequencesMatchClr(bool bom, bool strict) {
        using var vm = CreateVm();
        foreach (var value in new[] { "", "ASCII", "日本😀\0" })
            Assert.Equal(Clr("EncodingRoundtrip", [value, bom, strict]), vm.Invoke("BclChecks", "EncodingRoundtrip", value, bom, strict));
        for (var kind = 0; kind < 4; kind++)
            Assert.Equal(Clr("InvalidUtf8", [strict, kind]), vm.Invoke("BclChecks", "InvalidUtf8", strict, kind));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf8StreamingPreservesPartialSequences(bool strict) {
        using var vm = CreateVm();
        Assert.Equal(Clr("StreamingUtf8", [strict]), vm.Invoke("BclChecks", "StreamingUtf8", strict));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CultureAndEncodingWorkWithoutHostCoreLib(bool coreLib) {
        using var vm = CreateVm(coreLib);
        foreach (var method in new[] { "Culture", "ExplicitCase", "DefaultEncoding" }) {
            object?[] args = method == "Culture" ? ["de-DE"] : [];
            Assert.Equal(Clr(method, args), vm.Invoke("BclChecks", method, args));
        }
    }

    [Fact]
    public void GuestCultureFlowsToTasksAndRestoresHost() {
        using var vm = CreateVm();
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        Assert.Equal(Clr("ChangeCulture", []), vm.Invoke("BclChecks", "ChangeCulture"));
        Assert.Same(previous, CultureInfo.CurrentCulture);
        Assert.Same(previousUi, CultureInfo.CurrentUICulture);
    }

    [Theory]
    [InlineData("JsonRead")]
    [InlineData("JsonWrite")]
    [InlineData("JsonSerializeInt")]
    [InlineData("JsonDeserializeInt")]
    [InlineData("JsonCollections")]
    [InlineData("JsonObjects")]
    [InlineData("JsonInvalid")]
    [InlineData("JsonNullable")]
    [InlineData("JsonConstructor")]
    [InlineData("JsonConverter")]
    [InlineData("JsonUtf8")]
    public void SystemTextJsonMatchesClr(string method) {
        using var vm = CreateVm();
        vm.LoadAssembly(typeof(JsonSerializer).Assembly.Location);
        vm.Tracer.Start();
        try { Assert.Equal(Clr(method, []), vm.Invoke("BclChecks", method)); }
        catch (Exception ex) { throw new InvalidOperationException(string.Join("\n", vm.Tracer.Frames.TakeLast(90)), ex); }
        Assert.Contains(vm.Tracer.Frames, frame => frame.AssemblyName == "System.Text.Json");
    }

    [Fact]
    public void ConstructedInterfacesChooseTheTypedEqualsOverload() {
        using var vm = CreateVm();
        Assert.Equal(true, vm.Invoke("BclChecks", "TupleEquality"));
    }

    [Fact]
    public void TaskFailureDuringShutdownPreservesHostDisposedException() {
        using var stop = new CancellationTokenSource();
        using var budget = new GuestWorkerBudget(1);
        using var runtime = new GuestTaskRuntime(1, 1, 4, budget, stop.Token, 1000);
        var task = runtime.Create(new VmIntrinsicType { Namespace = "System.Threading.Tasks", Name = "Task", IsValue = false });
        stop.Cancel();
        runtime.CompleteHostException(task, new DotnetVM.Policy.UnhandledGuestException("System.InvalidProgramException", "shutdown race"));
        Assert.IsType<ObjectDisposedException>(task.Snapshot().HostException);
        Assert.Empty(runtime.EnumerateRoots());
    }
}
