using System.Net;
using System.Net.Http.Headers;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    // Standard client/message/content state machines terminate at the policy gate.
    // The guest-visible handler has no socket, DNS, redirect or cookie transport.
    private sealed class GatewayHttpHandler(IntrinsicContext ctx) : HttpMessageHandler {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ctx.Shared.ShutdownToken);
            try {
            var headers = request.Headers.Concat(request.Content?.Headers ?? (IEnumerable<KeyValuePair<string, IEnumerable<string>>>)[])
                .Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var maximum = ctx.Network?.MaxHttpRequestBodyBytes ?? 0;
            using var body = new HttpBodyBuffer(ctx, maximum);
            if (request.Content is not null) {
                if (request.Content.Headers.ContentLength is long size && size > maximum)
                    throw new NetworkQuotaExceededException("HTTP request exceeds its byte budget.");
                request.Content.CopyToAsync(body, linked.Token).GetAwaiter().GetResult();
            }
            var result = (ctx.Network ?? throw new OperationNotAllowedException("HTTP gateway is unavailable.")).OpenHttp(
                request.Method.Method, request.RequestUri!, body.ToArray(), headers, linked.Token, request.Version, request.VersionPolicy,
                request.Content is not null);
            var response = new HttpResponseMessage((HttpStatusCode)result.StatusCode) {
                Content = new GatewayResponseContent(ctx, result.Body, linked), RequestMessage = request, Version = result.Version, ReasonPhrase = result.ReasonPhrase,
            };
            foreach (var (name, value) in result.Headers)
                if (!response.Headers.TryAddWithoutValidation(name, value)) response.Content.Headers.TryAddWithoutValidation(name, value);
            result.BodyCompleted = () => { foreach (var (name, value) in result.TrailingHeaders()) response.TrailingHeaders.TryAddWithoutValidation(name, value); };
            return response;
            } catch { linked.Dispose(); throw; }
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = ctx.Heap.Allocate(ctx.Shared.GuestTasks.Create(FindAnyType(ctx, "System.Threading.Tasks.Task")!));
            try {
                ctx.Shared.GuestTasks.Run(worker, [], () => {
                    try { completion.TrySetResult(Send(request, cancellationToken)); }
                    catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                    return default;
                });
            } catch (Exception ex) {
                ctx.Shared.GuestTasks.CompleteHostException(worker, ex);
                completion.TrySetException(ex);
            }
            return completion.Task;
        }
    }

    internal static readonly Type[] HttpBoundaryTypes = [
        typeof(HttpClient), typeof(HttpMessageInvoker), typeof(HttpRequestMessage), typeof(HttpResponseMessage), typeof(HttpMethod),
        typeof(HttpRequestOptions), typeof(IEnumerator<>), typeof(System.Collections.IEnumerator), typeof(IDictionary<,>),
        typeof(HttpContent), typeof(ByteArrayContent), typeof(StringContent), typeof(StreamContent), typeof(FormUrlEncodedContent),
        typeof(MultipartContent), typeof(MultipartFormDataContent), typeof(Uri), typeof(UriBuilder), typeof(Version),
        typeof(HttpHeaders), typeof(HttpRequestHeaders), typeof(HttpResponseHeaders), typeof(HttpContentHeaders),
        typeof(AuthenticationHeaderValue), typeof(MediaTypeHeaderValue), typeof(MediaTypeWithQualityHeaderValue),
        typeof(StringWithQualityHeaderValue), typeof(ProductHeaderValue), typeof(ProductInfoHeaderValue),
        typeof(NameValueHeaderValue), typeof(NameValueWithParametersHeaderValue), typeof(CacheControlHeaderValue),
        typeof(ContentDispositionHeaderValue), typeof(ContentRangeHeaderValue), typeof(RangeHeaderValue), typeof(RangeItemHeaderValue),
        typeof(EntityTagHeaderValue), typeof(RetryConditionHeaderValue), typeof(TransferCodingHeaderValue), typeof(TransferCodingWithQualityHeaderValue),
        typeof(ViaHeaderValue), typeof(WarningHeaderValue), typeof(HttpHeaderValueCollection<>),
    ];

    private static StackSlot BclTask(IntrinsicContext ctx, string resultType, StackSlot[] roots, Func<StackSlot> work, bool run = false) {
        var definition = FindAnyType(ctx, resultType == "System.Void" ? "System.Threading.Tasks.Task" : "System.Threading.Tasks.Task`1")
            ?? throw new InvalidOperationException("Task is unavailable.");
        VmType type = resultType == "System.Void" ? definition : new VmConstructedType { Definition = definition, TypeArguments = [FindAnyType(ctx, resultType)!] };
        var task = ctx.Heap.Allocate(ctx.Shared.GuestTasks.Create(type));
        if (run) ctx.Shared.GuestTasks.Run(task, roots, () => {
            try { return BclCall(() => work()) ?? default; }
            catch (OperationCanceledException) when (!ctx.Shared.ShutdownToken.IsCancellationRequested) { task.SetCanceled(); return default; }
        });
        else {
            try { ctx.Shared.GuestTasks.Complete(task, BclCall(() => work()) ?? default); }
            catch (OperationCanceledException) { task.SetCanceled(); ctx.Shared.GuestTasks.Complete(task); }
            catch (Exception ex) { ctx.Shared.GuestTasks.CompleteHostException(task, ex); }
        }
        return StackSlot.OfObject(task);
    }

    private static void RegisterHttpClient(IntrinsicRegistry r) {
        foreach (var type in HttpBoundaryTypes) RegisterHostBoundary(r, type);
        RegisterHostBoundary(r, typeof(Stream), method => method.Name.Contains("Async", StringComparison.Ordinal) || method.Name is "get_Length" or "get_Position" or "set_Position" or "Seek" or "SetLength" or "ReadByte" or "WriteByte");
        BclConstructor(r, "System.Net.Http.HttpContent", [], static (ctx, a) => { SetBclValue(a[0], new GuestHttpContent(ctx, a[0])); return null; });
        foreach (var handler in new[] { "System.Net.Http.HttpMessageHandler", "System.Net.Http.HttpClientHandler", "System.Net.Http.SocketsHttpHandler", "System.Net.Http.DelegatingHandler" }) {
            BclConstructor(r, handler, [], (ctx, a) => {
                if (a[0].ObjectValue is VmClassInstance guest && guest.ClassType.FullName != handler) {
                    if (!BclStates.TryGetValue(guest, out _)) SetBclValue(a[0], new GuestHttpHandler(ctx, a[0]));
                } else SetBclValue(a[0], new GatewayHttpHandler(ctx));
                return null;
            });
            BclFace(r, handler, "Dispose", true, [], static (_, a) => { BclValue<HttpMessageHandler>(a[0]).Dispose(); return null; });
        }
        BclConstructor(r, "System.Net.Http.DelegatingHandler", ["System.Net.Http.HttpMessageHandler"], static (ctx, a) => { var handler = new GuestHttpHandler(ctx, a[0]) { InnerSlot = a[1], Inner = BclValue<HttpMessageHandler>(a[1]) }; SetBclValue(a[0], handler); KeepBoundaryRoots((VmObject)a[0].ObjectValue!, [a[1]]); return null; });
        BclFace(r, "System.Net.Http.DelegatingHandler", "get_InnerHandler", true, [], static (_, a) => BclValue<GuestHttpHandler>(a[0]).InnerSlot);
        BclFace(r, "System.Net.Http.DelegatingHandler", "set_InnerHandler", true, ["System.Net.Http.HttpMessageHandler"], static (_, a) => { if (a[1].ObjectValue is null) throw new ArgumentNullException("value"); var handler = BclValue<GuestHttpHandler>(a[0]); handler.Inner = BclValue<HttpMessageHandler>(a[1]); handler.InnerSlot = a[1]; KeepBoundaryRoots((VmObject)a[0].ObjectValue!, [a[1]]); return null; });
        BclFace(r, "System.Net.Http.DelegatingHandler", "SendAsync", true, ["System.Net.Http.HttpRequestMessage", "System.Threading.CancellationToken"], static (ctx, a) => HostHttpTask(ctx, BclValue<GuestHttpHandler>(a[0]).Forward(BclValue<HttpRequestMessage>(a[1]), CancellationRuntime.State(a[2])?.Token ?? default), a));
        BclFace(r, "System.Net.Http.DelegatingHandler", "Send", true, ["System.Net.Http.HttpRequestMessage", "System.Threading.CancellationToken"], static (ctx, a) => WrapBcl(ctx, "System.Net.Http.HttpResponseMessage", BclValue<GuestHttpHandler>(a[0]).ForwardSync(BclValue<HttpRequestMessage>(a[1]), CancellationRuntime.State(a[2])?.Token ?? default)));
        foreach (var handler in new[] { "System.Net.Http.HttpMessageHandler", "System.Net.Http.DelegatingHandler" })
            BclFace(r, handler, "Dispose", true, ["System.Boolean"], static (_, a) => { if (a[1].AsInt32 != 0) BclValue<GuestHttpHandler>(a[0]).DisposeInner(); return null; });
        foreach (var parameters in new[] { Array.Empty<string>(), new[] { "System.Net.Http.HttpMessageHandler" }, new[] { "System.Net.Http.HttpMessageHandler", "System.Boolean" } }) {
            BclConstructor(r, "System.Net.Http.HttpClient", parameters, (ctx, a) => {
                var handler = parameters.Length == 0 ? new GatewayHttpHandler(ctx) : BclValue<HttpMessageHandler>(a[1]);
                SetBclValue(a[0], new HttpClient(handler, a.Length < 3 || a[2].AsInt32 != 0));
                ((VmObject)a[0].ObjectValue!).BclReferences = a.Skip(1).Select(s => s.ObjectValue).OfType<VmObject>().ToArray(); return null;
            });
        }
        foreach (var withOwnership in new[] { false, true }) BclConstructor(r, "System.Net.Http.HttpMessageInvoker",
            withOwnership ? ["System.Net.Http.HttpMessageHandler", "System.Boolean"] : ["System.Net.Http.HttpMessageHandler"],
            static (_, a) => { SetBclValue(a[0], new HttpMessageInvoker(BclValue<HttpMessageHandler>(a[1]), a.Length < 3 || a[2].AsInt32 != 0)); KeepBoundaryRoots((VmObject)a[0].ObjectValue!, [a[1]]); return null; });
        const string keyType = "System.Net.Http.HttpRequestOptionsKey`1";
        BclFace(r, keyType, ".ctor", true, ["System.String"], static (ctx, a) => {
            if (a[0].ObjectValue is not VmByRef reference) throw new InvalidOperationException("An option key must be addressed.");
            var definition = FindAnyType(ctx, keyType)!; var arguments = ctx.ClassTypeArguments;
            reference.Write(StackSlot.OfValueType(new VmStructValue(new VmConstructedType { Definition = definition, TypeArguments = arguments }, [a[1]], arguments))); return null;
        });
        BclFace(r, keyType, "get_Key", true, [], static (_, a) => ((VmStructValue)(a[0].ObjectValue is VmByRef reference ? reference.Read().ObjectValue! : a[0].ObjectValue!)).Fields[0]);
        BclFace(r, "System.Net.Http.HttpRequestOptions", "Set", true, [keyType + "<!!0>", "!!0"], static (_, a) => {
            var key = StringValue(((VmStructValue)a[1].ObjectValue!).Fields[0])!;
            BclValue<HttpRequestOptions>(a[0]).Set(new HttpRequestOptionsKey<BoundaryGuestValue>(key), new BoundaryGuestValue(SlotOps.PushCopyOfValue(a[2])));
            KeepBoundaryRoots((VmObject)a[0].ObjectValue!, [a[2]]); return null;
        });
        BclFace(r, "System.Net.Http.HttpRequestOptions", "TryGetValue", true, [keyType + "<!!0>", "!!0&"], static (ctx, a) => {
            var key = StringValue(((VmStructValue)a[1].ObjectValue!).Fields[0])!;
            var found = BclValue<HttpRequestOptions>(a[0]).TryGetValue(new HttpRequestOptionsKey<BoundaryGuestValue>(key), out var value);
            ((VmByRef)a[2].ObjectValue!).Write(found ? SlotOps.PushCopyOfValue(value!.Value) : new ObjectModel().DefaultForType(ctx.MethodTypeArguments[0], ctx.Types));
            return StackSlot.OfInt32(found ? 1 : 0);
        });
    }
}
