using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// B6/O8 修复配套：取消文案按上游真实终态措辞；产物清单排除 CLI 内部状态文件。
/// 观察记录来源：#124 用户终止被写成"上游失败"；#125 产物混入 .crush\crush.db 等。
/// </summary>
public class GatewayPhraseTests
{
    // ---- B6：StepStatusPhrase ----

    [Fact]
    public void 文案_上游失败_仍写失败()
    {
        Assert.Equal("失败", A2aGateway.StepStatusPhrase("failed"));
    }

    [Fact]
    public void 文案_用户终止的上游_写已取消而非失败()
    {
        // #124 场景：用户点终止 → n1=cancelled → 下游取消原因应为"已取消"
        Assert.Equal("已取消", A2aGateway.StepStatusPhrase("cancelled"));
    }

    [Fact]
    public void 文案_中断的上游_写已中断()
    {
        Assert.Equal("已中断", A2aGateway.StepStatusPhrase("interrupted"));
    }

    [Fact]
    public void 文案_未知状态_兜底未完成()
    {
        Assert.Equal("未完成", A2aGateway.StepStatusPhrase(null));
        Assert.Equal("未完成", A2aGateway.StepStatusPhrase("weird"));
    }

    // ---- O8：IsRealArtifact ----

    [Fact]
    public void 产物_正常交付文件_计入()
    {
        Assert.True(A2aGateway.IsRealArtifact("src/main.py"));
        Assert.True(A2aGateway.IsRealArtifact("review.md"));
        Assert.True(A2aGateway.IsRealArtifact("screen_annotator/README.md"));
    }

    [Fact]
    public void 产物_宿主投放的约束文件_排除()
    {
        Assert.False(A2aGateway.IsRealArtifact("opencode.json"));
        Assert.False(A2aGateway.IsRealArtifact("AGENTS.md"));
        Assert.False(A2aGateway.IsRealArtifact(".crushrc"));
        Assert.False(A2aGateway.IsRealArtifact("CRUSH.md"));
    }

    [Fact]
    public void 产物_CLI状态目录_排除()
    {
        // #125 实测混入的三类 + opencode 配置锚定目录
        Assert.False(A2aGateway.IsRealArtifact(".crush\\crush.db"));
        Assert.False(A2aGateway.IsRealArtifact(".crush\\logs\\crush.log"));
        Assert.False(A2aGateway.IsRealArtifact(".crush\\.gitignore"));
        Assert.False(A2aGateway.IsRealArtifact(".oc-config\\opencode\\opencode.json"));
        Assert.False(A2aGateway.IsRealArtifact(".oc-config\\AGENTS.md"));
    }
}
