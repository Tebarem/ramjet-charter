using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Charter.Import;

/// <summary>A read-only view that refuses to deliver more than a limit of bytes (the guard against a compressed file that expands without bound).
/// It does not own the inner stream: disposing it leaves the inner stream open.</summary>
internal sealed class LimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private readonly string _what;
    private long _total;

    public LimitedStream(Stream inner, long limit, string what)
    {
        _inner = inner;
        _limit = limit;
        _what = what;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _total;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => Account(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Account(_inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Account(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Account(int read)
    {
        _total += read;
        if (_total > _limit) throw new InvalidDataException($"{_what}: more than {_limit} bytes after decompression; refused");
        return read;
    }
}
