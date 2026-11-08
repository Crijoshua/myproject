using AgentCompany.Core;

namespace AgentCompany.Tests;

/// <summary>命令白名单判定：整条命中 / 前缀命中 / 大小写 / 元字符防绕过。</summary>
public class CommandWhitelistTests
{
    [Theory]
    [InlineData("dir")]
    [InlineData("ls")]
    [InlineData("node -v")]
    [InlineData("git status")]
    [InlineData("git diff")]
    [InlineData("git add")]
    [InlineData("git commit")]
    [InlineData("npm test")]
    [InlineData("npm run build")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test")]
    public void 白名单条目整条命中_免审批(string command) => Assert.True(CommandWhitelist.IsWhitelisted(command));

    [Theory]
    [InlineData("git status -s")]
    [InlineData("git diff HEAD")]
    [InlineData("git add .")]
    [InlineData("git commit -m \"提交信息\"")]
    [InlineData("dir /b")]
    [InlineData("  DIR  ")]          // 前导空白 + 大小写
    [InlineData("DOTNET BUILD -c Release")]
    public void 条目加参数前缀命中_免审批(string command) => Assert.True(CommandWhitelist.IsWhitelisted(command));

    [Theory]
    [InlineData("npm install lodash")]          // 名单外 → 审批
    [InlineData("npm --version")]
    [InlineData("git push origin main")]
    [InlineData("del hello.txt")]
    [InlineData("Remove-Item -Recurse .")]
    [InlineData("")]
    [InlineData("   ")]
    public void 白名单外命令_走审批(string command) => Assert.False(CommandWhitelist.IsWhitelisted(command));

    [Theory]
    [InlineData("dir & del C:\\important.txt")]   // & 拼接危险命令
    [InlineData("dir | cmd")]                     // 管道
    [InlineData("dir > C:\\outside.txt")]         // 重定向出区
    [InlineData("git status %EVIL%")]             // 变量展开
    [InlineData("git commit -m `whoami`")]        // 反引号
    public void 含元字符的白名单前缀命令_不直通_转审批(string command) => Assert.False(CommandWhitelist.IsWhitelisted(command));

    [Fact]
    public void 相似但不完整的条目_不命中()
    {
        // "git" 或 "dirx" 不是任何条目的整条/前缀命中
        Assert.False(CommandWhitelist.IsWhitelisted("git"));
        Assert.False(CommandWhitelist.IsWhitelisted("dirx"));
        Assert.False(CommandWhitelist.IsWhitelisted("directory"));
    }
}
