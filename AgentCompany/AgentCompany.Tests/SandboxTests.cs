using AgentCompany.Core;

namespace AgentCompany.Tests;

/// <summary>沙箱路径判定：越界 / 相对 / .. 穿越 / 大小写 / 伪前缀 / 非法字符。</summary>
public class SandboxTests : IDisposable
{
    private readonly string _root;
    private readonly string _rootAltCase;

    public SandboxTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ac-sandbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _rootAltCase = _root.ToUpperInvariant();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录尽力清理 */ }
    }

    [Fact]
    public void 工作区内相对路径_允许并解析为根下绝对路径()
    {
        var full = Sandbox.ResolveInside(_root, Path.Combine("sub", "hello.txt"));
        Assert.NotNull(full);
        Assert.Equal(Path.Combine(_root, "sub", "hello.txt"), full);
        Assert.True(full!.StartsWith(_root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 双点穿越出根_拒绝()
    {
        Assert.Null(Sandbox.ResolveInside(_root, @"..\outside.txt"));
        Assert.Null(Sandbox.ResolveInside(_root, Path.Combine("sub", "..", "..", "steal.txt")));
    }

    [Fact]
    public void 绝对路径指向区外_拒绝()
    {
        Assert.Null(Sandbox.ResolveInside(_root, @"C:\Windows\System32\drivers\etc\hosts"));
        Assert.Null(Sandbox.ResolveInside(_root, @"\\server\share\file.txt"));
    }

    [Fact]
    public void 大小写变体_工作区内仍允许()
    {
        // 根目录大小写不同（Windows 卷大小写不敏感场景）
        var full = Sandbox.ResolveInside(_rootAltCase, "hello.TXT");
        Assert.NotNull(full);
        Assert.Equal(Path.Combine(_root, "hello.TXT"), full, ignoreCase: true);
    }

    [Fact]
    public void 伪前缀目录_工作区外的近似名_拒绝()
    {
        // C:\...\ws 与 C:\...\ws2：只差后缀，必须拒绝，防止字符串前缀绕过
        var sibling = _root + "2";
        Assert.Null(Sandbox.ResolveInside(_root, Path.Combine(sibling, "x.txt")));
    }

    [Theory]
    [InlineData("a<b.txt")]
    [InlineData("a|b.txt")]
    [InlineData("::bad")]
    [InlineData("con.txt\0tail")]
    public void 非法路径_拒绝(string path)
    {
        Assert.Null(Sandbox.ResolveInside(_root, path));
    }

    [Fact]
    public void 根自身_允许()
    {
        Assert.NotNull(Sandbox.ResolveInside(_root, "."));
        Assert.NotNull(Sandbox.ResolveInside(_root, ""));
    }

    [Fact]
    public void Terminal参数扫描_区外绝对路径与穿越_命中()
    {
        Assert.Equal(@"C:\Windows\System32", Sandbox.FindEscapingPathArg(_root, @"dir C:\Windows\System32"));
        Assert.NotNull(Sandbox.FindEscapingPathArg(_root, @"type ..\..\secret.txt"));
        // git 范围语法 HEAD..HEAD~1 不是路径，不得误伤
        Assert.Null(Sandbox.FindEscapingPathArg(_root, "git diff HEAD..HEAD~1"));
        // 工作区内相对/白名单命令不命中
        Assert.Null(Sandbox.FindEscapingPathArg(_root, "npm install lodash"));
        Assert.Null(Sandbox.FindEscapingPathArg(_root, "dir src"));
    }

    [Fact]
    public void 拒绝文案_带前缀且含原路径()
    {
        var text = Sandbox.Denied(@"C:\Windows\System32");
        Assert.StartsWith(Sandbox.DeniedPrefix, text);
        Assert.Contains(@"C:\Windows\System32", text);
    }
}
