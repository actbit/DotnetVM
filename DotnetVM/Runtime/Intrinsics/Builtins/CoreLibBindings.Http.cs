using System.Text;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private sealed class GuestHttpClient { public bool Disposed; public CancellationTokenSource Stop { get; } = new(); }
    private sealed record GuestHttpContent(byte[] Bytes);

    private static StackSlot BclTask(IntrinsicContext ctx, string resultType, StackSlot[] roots, Func<StackSlot> work, bool run = false) {
        var definition = FindAnyType(ctx, "System.Threading.Tasks.Task`1") ?? throw new InvalidOperationException("Task is unavailable.");
        var type = new VmConstructedType { Definition = definition, TypeArguments = [FindAnyType(ctx, resultType)!] };
        var task = ctx.Heap.Allocate(ctx.Shared.GuestTasks.Create(type));
        if (run) ctx.Shared.GuestTasks.Run(task, roots, () => {
            try { return work(); }
            catch (OperationCanceledException) when (!ctx.Shared.ShutdownToken.IsCancellationRequested) { task.SetCanceled(); return default; }
        });
        else {
            try { ctx.Shared.GuestTasks.Complete(task, work()); }
            catch (Exception ex) { ctx.Shared.GuestTasks.CompleteHostException(task, ex); }
        }
        return StackSlot.OfObject(task);
    }

    private static void RegisterHttpClient(IntrinsicRegistry r) {
        const string T = "System.Net.Http.HttpClient";
        BclConstructor(r, T, [], static (_, a) => { SetBclValue(a[0], new GuestHttpClient()); return null; });
        foreach (var type in new[] { T, "System.Net.Http.HttpMessageInvoker" })
            BclFace(r, type, "Dispose", true, [], static (_, a) => { var client = BclValue<GuestHttpClient>(a[0]); if (!client.Disposed) { client.Disposed = true; client.Stop.Cancel(); } return null; });
        foreach (var method in new[] { "GetStringAsync", "GetByteArrayAsync", "GetAsync", "PostAsync" })
        foreach (var withToken in new[] { false, true }) {
            var post = method == "PostAsync";
            var parameters = post ? new[] { "System.String", "System.Net.Http.HttpContent" } : new[] { "System.String" };
            if (withToken) parameters = [..parameters, "System.Threading.CancellationToken"];
            BclFace(r, T, method, true, parameters, (ctx, a) => {
                var client = BclValue<GuestHttpClient>(a[0]);
                ObjectDisposedException.ThrowIf(client.Disposed, "HttpClient");
                var cancellation = withToken ? CancellationRuntime.State(a[^1])?.Token ?? default : default;
                var url = StringValue(a[1]) ?? throw new ArgumentNullException("requestUri");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new ArgumentException("An absolute HTTP URL is required.");
                var body = post && a[2].ObjectValue is not null ? BclValue<GuestHttpContent>(a[2]).Bytes : Array.Empty<byte>();
                var resultType = method == "GetStringAsync" ? "System.String" : method == "GetByteArrayAsync" ? "System.Byte[]" : "System.Net.Http.HttpResponseMessage";
                return BclTask(ctx, resultType, a, () => {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.Shared.ShutdownToken, client.Stop.Token, cancellation);
                    var response = (ctx.Network ?? throw new OperationNotAllowedException("HTTP gateway is unavailable.")).TransferHttp(post ? "POST" : "GET", uri, body, cancellationToken: linked.Token);
                    ctx.Heap.ChargeHostBuffer(response.Body.Length); ctx.Heap.ChargeHostWork(response.Body.Length);
                    if (method == "GetStringAsync" || method == "GetByteArrayAsync") {
                        if (response.StatusCode < 200 || response.StatusCode >= 300)
                            throw new UnhandledGuestException("System.Net.Http.HttpRequestException", "HTTP status " + response.StatusCode);
                        return method == "GetStringAsync" ? StackSlot.OfObject(ctx.MakeString(Encoding.UTF8.GetString(response.Body))) : StackSlot.OfObject(ctx.MakeByteArray(response.Body));
                    }
                    return WrapBcl(ctx, "System.Net.Http.HttpResponseMessage", response);
                }, run: true);
            });
        }
        BclConstructor(r, "System.Net.Http.StringContent", ["System.String"], static (ctx, a) => {
            var text = StringValue(a[1]) ?? throw new ArgumentNullException("content"); ctx.Heap.ChargeHostWork(text.Length); ctx.Heap.ChargeHostBuffer(Encoding.UTF8.GetByteCount(text));
            SetBclValue(a[0], new GuestHttpContent(Encoding.UTF8.GetBytes(text))); return null;
        });
        BclConstructor(r, "System.Net.Http.ByteArrayContent", ["System.Byte[]"], static (ctx, a) => { SetBclValue(a[0], new GuestHttpContent(EncodingBytes(ctx, a[1], "System.Byte[]"))); return null; });
        const string Response = "System.Net.Http.HttpResponseMessage";
        BclFace(r, Response, "get_StatusCode", true, [], static (_, a) => StackSlot.OfInt32(BclValue<HttpNetworkResponse>(a[0]).StatusCode));
        BclFace(r, Response, "get_IsSuccessStatusCode", true, [], static (_, a) => StackSlot.OfInt32(BclValue<HttpNetworkResponse>(a[0]).StatusCode is >= 200 and < 300 ? 1 : 0));
        BclFace(r, Response, "get_Content", true, [], static (ctx, a) => WrapBcl(ctx, "System.Net.Http.HttpContent", new GuestHttpContent(BclValue<HttpNetworkResponse>(a[0]).Body)));
        BclFace(r, Response, "EnsureSuccessStatusCode", true, [], static (_, a) => {
            if (BclValue<HttpNetworkResponse>(a[0]).StatusCode is < 200 or >= 300) throw new UnhandledGuestException("System.Net.Http.HttpRequestException", "HTTP request failed.");
            return a[0];
        });
        BclFace(r, Response, "Dispose", true, [], static (_, _) => null);
        foreach (var type in new[] { "System.Net.Http.HttpContent", "System.Net.Http.StringContent", "System.Net.Http.ByteArrayContent" }) {
            BclFace(r, type, "Dispose", true, [], static (_, _) => null);
            BclFace(r, type, "ReadAsStringAsync", true, [], static (ctx, a) => BclTask(ctx, "System.String", a, () => {
                var bytes = BclValue<GuestHttpContent>(a[0]).Bytes; ctx.Heap.ChargeHostBuffer(bytes.Length); ctx.Heap.ChargeHostWork(bytes.Length);
                return StackSlot.OfObject(ctx.MakeString(Encoding.UTF8.GetString(bytes)));
            }));
            BclFace(r, type, "ReadAsByteArrayAsync", true, [], static (ctx, a) => BclTask(ctx, "System.Byte[]", a, () => StackSlot.OfObject(ctx.MakeByteArray(BclValue<GuestHttpContent>(a[0]).Bytes))));
        }
    }
}
