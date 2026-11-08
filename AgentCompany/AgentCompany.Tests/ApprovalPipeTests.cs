using System.Text;
using AgentCompany.Core;

namespace AgentCompany.Tests;

/// <summary>
/// 审批管道协议往返：内存双工流模拟宿主端（读一行请求 → 写一行响应）。
/// </summary>
public class ApprovalPipeTests
{
    private static ApprovalRequest Sample() =>
        new(Guid.NewGuid().ToString("N"), "claude", "terminal", "npm install lodash");

    [Fact]
    public async Task 宿主批准_客户端拿到approved响应()
    {
        var (clientSide, hostSide) = DuplexMemoryStream.CreatePair();
        var request = Sample();

        var host = Task.Run(async () =>
        {
            using var hostStream = hostSide;
            var reader = new StreamReader(hostStream, Encoding.UTF8);
            var writer = new StreamWriter(hostStream, new UTF8Encoding(false)) { AutoFlush = true };
            var line = await reader.ReadLineAsync();
            Assert.NotNull(line);
            var received = ApprovalPipe.DecodeRequest(line!);
            Assert.Equal(request.Id, received.Id);       // 同一请求
            Assert.Equal("claude", received.Agent);
            Assert.Equal("npm install lodash", received.Command);
            await writer.WriteLineAsync(ApprovalPipe.Encode(new ApprovalResponse(request.Id, Approved: true)));
        });

        var outcome = await ApprovalPipeClient.AskAsync(
            _ => Task.FromResult<Stream>(clientSide), request, TimeSpan.FromSeconds(5));

        await host;
        Assert.Equal(ApprovalFailure.None, outcome.Failure);
        Assert.True(outcome.IsApproved);
        Assert.Equal(request.Id, outcome.Response!.Id);
    }

    [Fact]
    public async Task 宿主拒绝_客户端拿到denied响应()
    {
        var (clientSide, hostSide) = DuplexMemoryStream.CreatePair();
        var request = Sample();

        var host = Task.Run(async () =>
        {
            using var hostStream = hostSide;
            var reader = new StreamReader(hostStream, Encoding.UTF8);
            var writer = new StreamWriter(hostStream, new UTF8Encoding(false)) { AutoFlush = true };
            await reader.ReadLineAsync();
            await writer.WriteLineAsync(ApprovalPipe.Encode(new ApprovalResponse(request.Id, Approved: false)));
        });

        var outcome = await ApprovalPipeClient.AskAsync(
            _ => Task.FromResult<Stream>(clientSide), request, TimeSpan.FromSeconds(5));

        await host;
        Assert.Equal(ApprovalFailure.None, outcome.Failure);
        Assert.False(outcome.IsApproved);
    }

    [Fact]
    public async Task 宿主沉默_超时判为TimedOut()
    {
        var (clientSide, hostSide) = DuplexMemoryStream.CreatePair();
        var request = Sample();

        var outcome = await ApprovalPipeClient.AskAsync(
            _ => Task.FromResult<Stream>(clientSide), request, TimeSpan.FromMilliseconds(300));

        Assert.Null(outcome.Response);
        Assert.Equal(ApprovalFailure.TimedOut, outcome.Failure);
        hostSide.Dispose();
        clientSide.Dispose();
    }

    [Fact]
    public async Task 连不上宿主_判为Unreachable()
    {
        var request = Sample();
        var outcome = await ApprovalPipeClient.AskAsync(
            _ => throw new IOException("管道不存在"), request, TimeSpan.FromSeconds(5));
        Assert.Null(outcome.Response);
        Assert.Equal(ApprovalFailure.Unreachable, outcome.Failure);
    }

    [Fact]
    public async Task 响应id不匹配_判为Unreachable不采信()
    {
        var (clientSide, hostSide) = DuplexMemoryStream.CreatePair();
        var request = Sample();

        var host = Task.Run(async () =>
        {
            using var hostStream = hostSide;
            var reader = new StreamReader(hostStream, Encoding.UTF8);
            var writer = new StreamWriter(hostStream, new UTF8Encoding(false)) { AutoFlush = true };
            await reader.ReadLineAsync();
            // 伪造：批准的是另一个 id
            await writer.WriteLineAsync(ApprovalPipe.Encode(new ApprovalResponse("not-the-id", Approved: true)));
        });

        var outcome = await ApprovalPipeClient.AskAsync(
            _ => Task.FromResult<Stream>(clientSide), request, TimeSpan.FromSeconds(5));

        await host;
        Assert.False(outcome.IsApproved);
        Assert.Equal(ApprovalFailure.Unreachable, outcome.Failure);
    }

    [Fact]
    public void 编解码_中文字段不转义不丢失()
    {
        var req = new ApprovalRequest("id-1", "claude", "terminal", "echo 你好公司");
        var encoded = ApprovalPipe.Encode(req);
        Assert.DoesNotContain("\\u", encoded); // 原样可读
        Assert.Contains("你好公司", encoded);
        var decoded = ApprovalPipe.DecodeRequest(encoded);
        Assert.Equal(req, decoded);

        var resp = new ApprovalResponse("id-1", false);
        Assert.Equal("{\"id\":\"id-1\",\"approved\":false}", ApprovalPipe.Encode(resp)); // null note 不写
    }
}

/// <summary>
/// 内存双工流对：一端 Write 的字节对端可 Read，用于在测试里模拟宿主 ↔ Bridge 的管道。
/// </summary>
internal sealed class DuplexMemoryStream : Stream
{
    private readonly Queue<byte> _inbox = new();
    private readonly SemaphoreSlim _available = new(0);
    private DuplexMemoryStream? _peer;
    private bool _peerGone;

    private DuplexMemoryStream() { }

    public static (DuplexMemoryStream A, DuplexMemoryStream B) CreatePair()
    {
        var a = new DuplexMemoryStream();
        var b = new DuplexMemoryStream { _peer = a };
        a._peer = b;
        return (a, b);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        _peer!.Deliver(buffer.AsSpan(offset, count));

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _peer!.Deliver(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            lock (_inbox)
            {
                var n = 0;
                while (n < count && _inbox.Count > 0)
                    buffer[offset + n++] = _inbox.Dequeue();
                if (n > 0)
                    return n;
                if (_peerGone)
                    return 0;
            }
            _available.Wait(50);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (true)
        {
            lock (_inbox)
            {
                var n = 0;
                var span = buffer.Span;
                while (n < span.Length && _inbox.Count > 0)
                    span[n++] = _inbox.Dequeue();
                if (n > 0)
                    return n;
                if (_peerGone)
                    return 0;
            }
            // 异步等数据，且必须可被取消：StreamReader.ReadLineAsync(ct) 靠它传播超时
            await _available.WaitAsync(ct);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        _peer!._peerGone = true;
        _peer!._available.Release();
    }

    private void Deliver(ReadOnlySpan<byte> data)
    {
        lock (_inbox)
        {
            foreach (var b in data)
                _inbox.Enqueue(b);
        }
        _available.Release();
    }
}
