using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace AgentCompany.Core;

/// <summary>
/// 审批请求（Bridge → 宿主，一行 JSON）。
/// 轮 7：task_title 为来源任务标题（多任务并行时审批中心按它区分；非任务直发为 null）。
/// </summary>
public sealed record ApprovalRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("task_title")] string? TaskTitle = null);

/// <summary>审批响应（宿主 → Bridge，一行 JSON）。</summary>
public sealed record ApprovalResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("note")] string? Note = null);

/// <summary>审批失败原因（Response 为 null 时才有意义）。</summary>
public enum ApprovalFailure
{
    None,
    /// <summary>通道连不上 / 报文损坏 / id 不匹配（宿主 5 秒内无响应即此档，fail-safe = 拒绝）。</summary>
    Unreachable,
    /// <summary>宿主 120 秒未决定（Bridge 侧等待上限）。</summary>
    TimedOut,
}

/// <summary>一次审批的结局：要么拿到响应，要么带失败原因。</summary>
public sealed record ApprovalOutcome(ApprovalResponse? Response, ApprovalFailure Failure)
{
    public bool IsApproved => Response is { Approved: true };
}

/// <summary>管道协议常量与编解码（宿主 / Bridge / 测试共用）。</summary>
public static class ApprovalPipe
{
    /// <summary>固定管道名（宿主也可经 AGENTCOMPANY_PIPE 覆盖）。</summary>
    public const string DefaultPipeName = "AgentCompany.Approval";

    /// <summary>连接上限：5 秒内连不上宿主即判不可达（直接 DENIED，绝不放行）。</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>等待宿主决定的上限：120 秒，超时视为拒绝。</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(120);

    // 中文命令按原样可读（不做 \uXXXX 转义），null 字段不写入
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Encode(ApprovalRequest request) => JsonSerializer.Serialize(request, Json);
    public static string Encode(ApprovalResponse response) => JsonSerializer.Serialize(response, Json);

    public static ApprovalRequest DecodeRequest(string line) =>
        JsonSerializer.Deserialize<ApprovalRequest>(line, Json)
        ?? throw new JsonException("请求反序列化为 null");

    public static ApprovalResponse DecodeResponse(string line) =>
        JsonSerializer.Deserialize<ApprovalResponse>(line, Json)
        ?? throw new JsonException("响应反序列化为 null");
}

/// <summary>
/// Bridge 侧审批客户端：连接 → 写一行请求 → 读一行响应 → 校验 id。
/// 通过 Stream 工厂注入连接方式，宿主端可用内存双工流直接模拟（单元测试）。
/// 任何异常都吞掉并转为失败结局（fail-safe 方向 = 拒绝）。
/// </summary>
public static class ApprovalPipeClient
{
    public static async Task<ApprovalOutcome> AskAsync(
        Func<CancellationToken, Task<Stream>> connectAsync,
        ApprovalRequest request,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken = default)
    {
        Stream? stream = null;
        try
        {
            stream = await connectAsync(cancellationToken);

            // 写请求
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync(ApprovalPipe.Encode(request));

            // 等响应（带超时）
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(responseTimeout);
            var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return new ApprovalOutcome(null, ApprovalFailure.TimedOut);
            }

            if (line is null)
                return new ApprovalOutcome(null, ApprovalFailure.Unreachable); // 对端提前断开

            var response = ApprovalPipe.DecodeResponse(line);
            return response.Id == request.Id
                ? new ApprovalOutcome(response, ApprovalFailure.None)
                : new ApprovalOutcome(null, ApprovalFailure.Unreachable); // id 不匹配：报文可疑，拒绝
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ApprovalOutcome(null, ApprovalFailure.Unreachable);
        }
        catch
        {
            return new ApprovalOutcome(null, ApprovalFailure.Unreachable);
        }
        finally
        {
            try { stream?.Dispose(); } catch { /* 收尾失败不影响结局 */ }
        }
    }

    /// <summary>命名管道便捷入口：管道名 + 5 秒连接上限 + 120 秒响应上限。</summary>
    public static Task<ApprovalOutcome> AskNamedPipeAsync(string pipeName, ApprovalRequest request) =>
        AskAsync(async ct =>
        {
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(ApprovalPipe.ConnectTimeout);
                await client.ConnectAsync(connect.Token);
            }
            return client;
        }, request, ApprovalPipe.ResponseTimeout);
}
