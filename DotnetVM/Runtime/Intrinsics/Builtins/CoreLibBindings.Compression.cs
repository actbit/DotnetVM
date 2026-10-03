using System.IO.Compression;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Heap;
using DotnetVM.Runtime.Objects;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

internal static partial class CoreLibBindings {
    private sealed class GuestMemoryStream(VmMemoryStreamObject memory, VmHeap heap) : Stream {
        private bool _disposed;
        public override bool CanRead => !_disposed && !memory.IsDisposed;
        public override bool CanWrite => !_disposed && !memory.IsDisposed;
        public override bool CanSeek => !_disposed && !memory.IsDisposed;
        public override long Length { get { Check(); return memory.Bytes.Length; } }
        public override long Position { get => memory.Position; set { Check(); if (value < 0 || value > int.MaxValue) throw new ArgumentOutOfRangeException(); memory.Position = (int)value; } }
        private void Check() => ObjectDisposedException.ThrowIf(_disposed || memory.IsDisposed, this);
        public override void Flush() => Check();
        public override int Read(byte[] buffer, int offset, int count) { Check(); CheckSlice(buffer.Length, offset, count); return Read(buffer.AsSpan(offset, count)); }
        public override int Read(Span<byte> buffer) {
            Check(); var count = Math.Min(buffer.Length, Math.Max(0, memory.Bytes.Length - memory.Position));
            heap.ChargeHostWork(count); memory.Bytes.AsSpan(Math.Min(memory.Position, memory.Bytes.Length), count).CopyTo(buffer); memory.Position += count; return count;
        }
        public override void Write(byte[] buffer, int offset, int count) { CheckSlice(buffer.Length, offset, count); Write(buffer.AsSpan(offset, count)); }
        public override void Write(ReadOnlySpan<byte> buffer) {
            Check(); heap.ChargeHostWork(buffer.Length);
            var length = checked(memory.Position + buffer.Length);
            if (length > memory.Bytes.Length) {
                heap.ChargeHostBuffer(length); var expanded = new byte[length]; memory.Bytes.CopyTo(expanded, 0); memory.ReplaceBytes(expanded);
            }
            buffer.CopyTo(memory.Bytes.AsSpan(memory.Position)); memory.Position = length;
        }
        public override long Seek(long offset, SeekOrigin origin) { Position = checked(offset + (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length)); return Position; }
        public override void SetLength(long length) {
            Check(); if (length < 0 || length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length));
            heap.ChargeHostBuffer((int)length); var bytes = new byte[(int)length]; memory.Bytes.AsSpan(0, Math.Min(memory.Bytes.Length, bytes.Length)).CopyTo(bytes); memory.ReplaceBytes(bytes);
        }
        protected override void Dispose(bool disposing) { _disposed = true; memory.IsDisposed = true; base.Dispose(disposing); }
    }

    private static Stream GuestStream(IntrinsicContext ctx, StackSlot slot) => slot.ObjectValue is VmMemoryStreamObject stream
        ? new GuestMemoryStream(stream, ctx.Heap) : BclValue<Stream>(slot);

    private static void RegisterCompression(IntrinsicRegistry r) {
        foreach (var name in new[] { "GZipStream", "DeflateStream", "BrotliStream", "ZLibStream" }) {
            var type = "System.IO.Compression." + name;
            foreach (var selector in new[] { "CompressionMode", "CompressionLevel" })
                foreach (var leaveOpen in new[] { false, true }) {
                    var parameters = new List<string> { "System.IO.Stream", "System.IO.Compression." + selector };
                    if (leaveOpen) parameters.Add("System.Boolean");
                    BclConstructor(r, type, parameters.ToArray(), (ctx, a) => {
                        var stream = GuestStream(ctx, a[1]);
                        var keep = a.Length > 3 && a[3].AsInt32 != 0;
                        var mode = (CompressionMode)a[2].AsInt32; var level = (CompressionLevel)a[2].AsInt32;
                        Stream compression = (name, selector) switch {
                            ("GZipStream", "CompressionMode") => new GZipStream(stream, mode, keep),
                            ("GZipStream", _) => new GZipStream(stream, level, keep),
                            ("DeflateStream", "CompressionMode") => new DeflateStream(stream, mode, keep),
                            ("DeflateStream", _) => new DeflateStream(stream, level, keep),
                            ("BrotliStream", "CompressionMode") => new BrotliStream(stream, mode, keep),
                            ("BrotliStream", _) => new BrotliStream(stream, level, keep),
                            (_, "CompressionMode") => new ZLibStream(stream, mode, keep),
                            _ => new ZLibStream(stream, level, keep),
                        };
                        SetBclValue(a[0], compression);
                        if (a[0].ObjectValue is VmObject receiver && a[1].ObjectValue is VmObject backing) receiver.BclReferences = [backing];
                        return null;
                    });
                }
        }
        foreach (var type in new[] { "System.IO.Stream", "System.IO.MemoryStream", "System.IO.Compression.GZipStream", "System.IO.Compression.DeflateStream", "System.IO.Compression.BrotliStream", "System.IO.Compression.ZLibStream" }) {
            foreach (var property in new[] { "CanRead", "CanWrite", "CanSeek" })
                BclFace(r, type, "get_" + property, true, [], (ctx, a) => { var stream = GuestStream(ctx, a[0]); return StackSlot.OfInt32((property == "CanRead" ? stream.CanRead : property == "CanWrite" ? stream.CanWrite : stream.CanSeek) ? 1 : 0); });
            BclFace(r, type, "Write", true, ["System.Byte[]", "System.Int32", "System.Int32"], static (ctx, a) => {
                var bytes = EncodingBytes(ctx, a[1], "System.Byte[]"); CheckSlice(bytes.Length, a[2].AsInt32, a[3].AsInt32);
                GuestStream(ctx, a[0]).Write(bytes, a[2].AsInt32, a[3].AsInt32); return null;
            });
            BclFace(r, type, "Write", true, ["System.ReadOnlySpan`1<System.Byte>"], static (ctx, a) => { GuestStream(ctx, a[0]).Write(EncodingBytes(ctx, a[1], "span")); return null; });
            // MemoryStream Read already has its normalized binding.
            BclFace(r, type, "Dispose", true, [], static (ctx, a) => { GuestStream(ctx, a[0]).Dispose(); return null; });
            BclFace(r, type, "Close", true, [], static (ctx, a) => { GuestStream(ctx, a[0]).Dispose(); return null; });
            if (type != "System.IO.MemoryStream") {
                BclFace(r, type, "Read", true, ["System.Byte[]", "System.Int32", "System.Int32"], static (ctx, a) => {
                    var dest = RequireEncodingArray(a[1], "buffer"); var offset = a[2].AsInt32; var count = a[3].AsInt32; CheckSlice(dest.Length, offset, count);
                    ctx.Heap.ChargeHostBuffer(count); ctx.Heap.ChargeHostWork(count); var bytes = new byte[count];
                    var read = GuestStream(ctx, a[0]).Read(bytes, 0, count);
                    for (var i = 0; i < read; i++) dest.Elements[offset + i] = StackSlot.OfInt32(bytes[i]); return StackSlot.OfInt32(read);
                });
            }
            BclFace(r, type, "Flush", true, [], static (ctx, a) => { GuestStream(ctx, a[0]).Flush(); return null; });
            foreach (var withSize in new[] { false, true })
                BclFace(r, type, "CopyTo", true, withSize ? ["System.IO.Stream", "System.Int32"] : ["System.IO.Stream"], (ctx, a) => {
                    var count = withSize ? a[2].AsInt32 : 8192; if (count <= 0) throw new ArgumentOutOfRangeException("bufferSize");
                    count = Math.Min(count, 8192); ctx.Heap.ChargeHostBuffer(count); var bytes = new byte[count];
                    var source = GuestStream(ctx, a[0]); var dest = GuestStream(ctx, a[1]);
                    while (true) { ctx.Shared.ShutdownToken.ThrowIfCancellationRequested(); var read = source.Read(bytes, 0, count); if (read == 0) break; ctx.Heap.ChargeHostWork(read); dest.Write(bytes, 0, read); }
                    return null;
                });
        }
    }
}
