using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// [选择计划] 导入 .md 后的任务标题派生（A2aGateway.TitleFromDemand）：
/// 计划首行常是 Markdown 标题——剥 # 前缀取干净标题；一句话需求行为兼容（首 24 字截断）。
/// </summary>
public class TitleFromDemandTests
{
    [Fact]
    public void 标题_一句话短需求_原样返回()
    {
        Assert.Equal("写个官网首页", A2aGateway.TitleFromDemand("写个官网首页"));
    }

    [Fact]
    public void 标题_一句话超24字_截断加省略号()
    {
        var longDemand = new string('做', 30);
        Assert.Equal(new string('做', 24) + "…", A2aGateway.TitleFromDemand(longDemand));
    }

    [Fact]
    public void 标题_md计划_首行标题剥掉井号前缀()
    {
        var plan = "# 新版官网 v2 计划\n\n## 目标\n- 首页\n- 关于我们";
        Assert.Equal("新版官网 v2 计划", A2aGateway.TitleFromDemand(plan));
    }

    [Fact]
    public void 标题_md计划_首行是正文_取首行()
    {
        var plan = "做一个多步骤复杂官网\n\n第二步：关于页";
        Assert.Equal("做一个多步骤复杂官网", A2aGateway.TitleFromDemand(plan));
    }

    [Fact]
    public void 标题_空行之后才有标题_取首个非空行()
    {
        var plan = "\n\n# 深藏的标题\n正文";
        Assert.Equal("深藏的标题", A2aGateway.TitleFromDemand(plan));
    }

    [Fact]
    public void 标题_标题行内空白_折成单空格()
    {
        var plan = "# 官网\t改版\t计划";
        Assert.Equal("官网 改版 计划", A2aGateway.TitleFromDemand(plan));
    }
}
