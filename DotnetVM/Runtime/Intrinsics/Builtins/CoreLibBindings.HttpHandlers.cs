using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;
using System.Net;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private sealed class GatewayResponseContent(IntrinsicContext ctx, Stream body, CancellationTokenSource lifetime) : HttpContent {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(body);
        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return body; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, default);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) {
            ctx.Heap.ChargeHostBuffer(8192); var bytes = new byte[8192];
            while (true) {
                var read = await body.ReadAsync(bytes, token).ConfigureAwait(false); if (read == 0) break;
                ctx.Heap.ChargeHostWork(read); ctx.Heap.ChargeHostBuffer(read);
                await stream.WriteAsync(bytes.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) { body.Dispose(); lifetime.Dispose(); } base.Dispose(disposing); }
    }
    private sealed class HttpBodyBuffer(IntrinsicContext ctx, int maximum) : Stream {
        private readonly MemoryStream _buffer = new();
        public byte[] ToArray() { ctx.Heap.ChargeHostBuffer(checked((int)Length)); return _buffer.ToArray(); }
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> bytes) {
            if (bytes.Length > maximum - Length) throw new NetworkQuotaExceededException("HTTP request exceeds its byte budget.");
            ctx.Heap.ChargeHostWork(bytes.Length); ctx.Heap.ChargeHostBuffer(bytes.Length);
            _buffer.Write(bytes);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) {
            token.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) {
            token.ThrowIfCancellationRequested(); Write(bytes.Span); return ValueTask.CompletedTask;
        }
    }

    private static StackSlot HostHttpTask(IntrinsicContext ctx, Task<HttpResponseMessage> hostTask, StackSlot[] roots) {
        var type = new VmConstructedType { Definition = FindAnyType(ctx, "System.Threading.Tasks.Task`1")!, TypeArguments = [FindAnyType(ctx, "System.Net.Http.HttpResponseMessage")!] };
        var guest = ctx.Heap.Allocate(ctx.Shared.GuestTasks.Create(type));
        ctx.Shared.GuestTasks.KeepTaskRoots(guest, roots);
        void Finish() {
            try { ctx.Shared.GuestTasks.Complete(guest, BclCall(() => WrapBcl(ctx, "System.Net.Http.HttpResponseMessage", hostTask.GetAwaiter().GetResult())) ?? default); }
            catch (OperationCanceledException) { guest.SetCanceled(); ctx.Shared.GuestTasks.Complete(guest); }
            catch (Exception ex) { ctx.Shared.GuestTasks.CompleteHostException(guest, ex); }
        }
        if (hostTask.IsCompleted) Finish(); else hostTask.GetAwaiter().OnCompleted(Finish);
        return StackSlot.OfObject(guest);
    }

    private sealed class GuestHttpHandler(IntrinsicContext ctx, StackSlot receiver) : HttpMessageHandler {
        private bool _disposed;
        private bool _started;
        private HttpMessageHandler? _inner;
        public StackSlot InnerSlot { get; set; } = StackSlot.Null;
        public HttpMessageHandler? Inner { get => _inner; set { ObjectDisposedException.ThrowIf(_disposed, this); if (_started) throw new InvalidOperationException("The handler has already started a request."); _inner = value; } }
        public Task<HttpResponseMessage> Forward(HttpRequestMessage request, CancellationToken token) {
            ObjectDisposedException.ThrowIf(_disposed, this); _started = true;
            using var invoker = new HttpMessageInvoker(Inner ?? throw new InvalidOperationException("InnerHandler is unavailable."), false);
            return invoker.SendAsync(request, token);
        }
        public HttpResponseMessage ForwardSync(HttpRequestMessage request, CancellationToken token) {
            ObjectDisposedException.ThrowIf(_disposed, this); _started = true;
            using var invoker = new HttpMessageInvoker(Inner ?? throw new InvalidOperationException("InnerHandler is unavailable."), false);
            return invoker.Send(request, token);
        }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token) {
            ObjectDisposedException.ThrowIf(_disposed, this); _started = true;
            var state = ctx.Heap.Allocate(new VmCancellationState(FindAnyType(ctx, "System.Threading.CancellationTokenSource")!, token));
            var arguments = new[] { WrapBcl(ctx, "System.Net.Http.HttpRequestMessage", request), CancellationRuntime.Token(ctx, state) };
            using var roots = ctx.RegisterTransientRoots?.Invoke([receiver, arguments[0], arguments[1]]);
            return BclValue<HttpResponseMessage>(ctx.InvokeGuestInstanceMethod!(receiver, "Send", arguments)!.Value);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            ObjectDisposedException.ThrowIf(_disposed, this); _started = true;
            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var state = ctx.Heap.Allocate(new VmCancellationState(FindAnyType(ctx, "System.Threading.CancellationTokenSource")!, token));
            var requestSlot = WrapBcl(ctx, "System.Net.Http.HttpRequestMessage", request);
            try {
                var value = ctx.InvokeGuestInstanceMethod!(receiver, "SendAsync", [requestSlot, CancellationRuntime.Token(ctx, state)]);
                var task = value?.ObjectValue as VmTaskObject ?? throw new InvalidOperationException("A handler must return Task<HttpResponseMessage>.");
                ctx.Shared.GuestTasks.KeepTaskRoots(task, [receiver, requestSlot, StackSlot.OfObject(state)]);
                void Finish() {
                    try {
                        if (task.IsCanceled) { completion.TrySetCanceled(token); return; }
                        var snapshot = task.Snapshot();
                        if (snapshot.HostException is { } host) { completion.TrySetException(host); return; }
                        if (snapshot.GuestException.ObjectValue is { } exception) {
                            var name = exception is VmObject vmException ? vmException.Type.FullName : "System.Exception";
                            completion.TrySetException(new UnhandledGuestException(name, null)); return;
                        }
                        completion.TrySetResult(BclValue<HttpResponseMessage>(snapshot.Result));
                    } catch (Exception ex) { completion.TrySetException(ex); }
                }
                if (task.IsCompleted) Finish(); else task.RegisterCompletion(Finish);
            } catch (Exception ex) { completion.TrySetException(ex); }
            return completion.Task;
        }
        public void DisposeInner() { if (_disposed) return; _disposed = true; Inner?.Dispose(); }
        protected override void Dispose(bool disposing) {
            if (disposing && !_disposed) {
                if (HasGuestOverride("Dispose", 1)) ctx.InvokeGuestInstanceMethod!(receiver, "Dispose", [StackSlot.OfInt32(1)]);
                DisposeInner();
            }
            base.Dispose(disposing);
        }
        private bool HasGuestOverride(string name, int parameters) {
            for (var type = (receiver.ObjectValue as VmClassInstance)?.RuntimeType; type is not null; type = type.BaseType)
                if (type.Methods.Any(m => m.Name == name && m.Signature.ParamTypes.Length == parameters && m.Body is not null)) return true;
            return false;
        }
    }

    private sealed class GuestHttpContent(IntrinsicContext ctx, StackSlot receiver) : HttpContent {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) {
            var destination = WrapBcl(ctx, "System.IO.Stream", stream);
            var task = ctx.InvokeGuestInstanceMethod!(receiver, "SerializeToStreamAsync", [destination, StackSlot.Null])!.Value.ObjectValue as VmTaskObject
                ?? throw new InvalidOperationException("Content serialization must return a Task.");
            ctx.Shared.GuestTasks.KeepTaskRoots(task, [receiver, destination]);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Finish() {
                if (task.IsCanceled) { completion.TrySetCanceled(); return; }
                var snapshot = task.Snapshot();
                if (snapshot.HostException is { } host) completion.TrySetException(host);
                else if (snapshot.GuestException.ObjectValue is VmObject exception) completion.TrySetException(new UnhandledGuestException(exception.Type.FullName, null));
                else completion.TrySetResult();
            }
            if (task.IsCompleted) Finish(); else task.RegisterCompletion(Finish);
            return completion.Task;
        }
        protected override bool TryComputeLength(out long length) {
            var slots = new[] { StackSlot.OfInt64(0) };
            var result = ctx.InvokeGuestInstanceMethod!(receiver, "TryComputeLength", [StackSlot.OfByRef(new VmByRef(slots, 0))]);
            length = slots[0].Int64Value; return result!.Value.AsInt32 != 0;
        }
    }
}
