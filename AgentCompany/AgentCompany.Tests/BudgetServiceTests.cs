using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>预算三路判定纯函数（轮 6 任务书要求 ≥3 例）：token / 墙钟 / 重跑。</summary>
public class BudgetServiceTests
{
    [Fact]
    public void 判定_token用量达到上限_熔断()
    {
        var d = BudgetService.Evaluate(usedTokens: 100_000, tokenCap: 100_000, usedWallSeconds: 10, wallCapSeconds: 1800);
        Assert.True(d.Breached);
        Assert.Contains("token", d.Reason);
        Assert.Contains("100000", d.Reason);
    }

    [Fact]
    public void 判定_token低于上限_放行()
    {
        var d = BudgetService.Evaluate(usedTokens: 99_999, tokenCap: 100_000, usedWallSeconds: 0, wallCapSeconds: 1800);
        Assert.False(d.Breached);
        Assert.Null(d.Reason);
    }

    [Fact]
    public void 判定_墙钟达到上限_熔断()
    {
        var d = BudgetService.Evaluate(usedTokens: 0, tokenCap: 100_000, usedWallSeconds: 1800, wallCapSeconds: 1800);
        Assert.True(d.Breached);
        Assert.Contains("墙钟", d.Reason);
    }

    [Fact]
    public void 判定_重跑超过上限_熔断_恰好用完不熔断()
    {
        var at = BudgetService.Evaluate(0, 100_000, 0, 0, rerunsUsed: 3, rerunCap: 3);
        Assert.False(at.Breached); // 用满 3 次仍可，第 4 次拒绝
        var over = BudgetService.Evaluate(0, 100_000, 0, 0, rerunsUsed: 4, rerunCap: 3);
        Assert.True(over.Breached);
        Assert.Contains("重跑", over.Reason);
    }

    [Fact]
    public void 判定_重跑未给上限_用默认3()
    {
        var d = BudgetService.Evaluate(0, 100_000, 0, 0, rerunsUsed: BudgetService.DefaultRerunCap + 1);
        Assert.True(d.Breached);
        Assert.Contains("重跑", d.Reason);
    }

    [Fact]
    public void 判定_零上限视为未设_用默认口径不误熔断()
    {
        // cap <= 0 的两路按"未设置"跳过判定（Register 侧归一到默认，这里纯函数防御）
        var d = BudgetService.Evaluate(usedTokens: 5, tokenCap: 0, usedWallSeconds: 5, wallCapSeconds: 0);
        Assert.False(d.Breached);
    }

    // ---- 台账结转（验收 ⑤ 实锤的坑：CEO 拆解期 token 在"注销→再登记"间凭空消失） ----

    private sealed class NullGate : IApprovalGate
    {
        public int PendingCount => 0;
        public string? WaitingOnCommand => null;
        public int WaitingRemainingSeconds => 0;
    }

    [Fact]
    public void 结转_再登记后CEO已烧token不丢_节点补足即熔断()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ac-budget-{Guid.NewGuid():N}.db");
        var db = new Database(path);
        db.EnsureCreated();
        var svc = new BudgetService(db, new EventRecorder(db, new FakeBus()), new NullGate());
        try
        {
            svc.Register(1, 3000, null, null);
            svc.AddTokens(1, "claude", "ceo", 2556);   // 拆解期：未超
            svc.Unregister(1);                          // 等人确认：注销
            svc.Register(1, 3000, null, null);          // 确认开工：再登记（要结转 2556）
            var tripped = 0;
            svc.FuseTripped += (_, _) => tripped++;
            svc.AddTokens(1, "opencode", null, 444);    // 2556+444=3000 → 熔断
            Assert.Equal(1, tripped);
        }
        finally
        {
            svc.Unregister(1);
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void 结转_无台账记录_从零起算()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ac-budget-{Guid.NewGuid():N}.db");
        var db = new Database(path);
        db.EnsureCreated();
        var svc = new BudgetService(db, new EventRecorder(db, new FakeBus()), new NullGate());
        try
        {
            svc.Register(2, 3000, null, null);
            svc.AddTokens(2, "opencode", null, 2999);  // 新任务台账为空，不误熔断
            var tripped = 0;
            svc.FuseTripped += (_, _) => tripped++;
            svc.AddTokens(2, "opencode", null, 1);      // 3000 → 熔断
            Assert.Equal(1, tripped);
        }
        finally
        {
            svc.Unregister(2);
            try { File.Delete(path); } catch { }
        }
    }
}
