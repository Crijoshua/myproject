using System.Globalization;
using System.Text;

namespace AgentCompany.Core;

/// <summary>
/// 子进程输出的混合解码（轮 3 实测结论）：
///  - cmd.exe 自身消息在管道重定向下按 OEM 码页写（zh-CN=GBK，chcp 65001 也改不了它）；
///  - node / dotnet / python 等子进程重定向时默认写 UTF-8。
/// 策略：先按严格 UTF-8 解，失败回退 OEM 码页。纯 ASCII 两者等价。
/// </summary>
public static class OutputDecoder
{
    private static readonly object Gate = new();
    private static Encoding? _oem;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>当前系统 OEM 码页（须先注册 CodePagesEncodingProvider）。</summary>
    public static Encoding Oem
    {
        get
        {
            lock (Gate)
            {
                if (_oem is null)
                {
                    Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                    _oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                }
                return _oem;
            }
        }
    }

    /// <summary>严格 UTF-8 优先，OEM 回退。</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try { return Oem.GetString(bytes); }
            catch { return Encoding.Latin1.GetString(bytes); } // 极端环境：有损但不抛
        }
    }
}
