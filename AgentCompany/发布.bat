@echo off
rem ================================================================
rem  AgentCompany - release publish script (round 8 + M4 CLI)
rem  Output:
rem    publish\AgentCompany.exe                        (desktop, self-contained single file)
rem    publish\McpBridge\AgentCompany.McpBridge.exe    (MCP server, sandbox + approval)
rem    publish\cli\agentcompany.exe                    (M4: CEO terminal REPL)
rem    publish\agentcompany.cmd                        (M4: shim -> cli\agentcompany.exe)
rem    publish\使用手册.md / 换机部署清单.md            (manuals ship with the app)
rem ================================================================
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 set "PATH=%PATH%;C:\Program Files\dotnet"

echo [1/4] dotnet publish AgentCompany.Desktop ...
dotnet publish AgentCompany.Desktop.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish
if errorlevel 1 (
    echo [FAIL] desktop publish failed.
    exit /b 1
)

echo [2/4] dotnet publish AgentCompany.McpBridge ...
dotnet publish AgentCompany.McpBridge\AgentCompany.McpBridge.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish\McpBridge
if errorlevel 1 (
    echo [FAIL] mcp bridge publish failed.
    exit /b 1
)

echo [3/4] dotnet publish AgentCompany.Cli ...
dotnet publish AgentCompany.Cli\AgentCompany.Cli.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish\cli
if errorlevel 1 (
    echo [FAIL] cli publish failed.
    exit /b 1
)
rem CLI 员工宿主按 exe 旁 McpBridge\ 定位（与主程序同布局），复制第 2 步产物
if exist publish\McpBridge xcopy /e /i /y publish\McpBridge publish\cli\McpBridge >nul
rem 终端命令 shim：publish 根 agentcompany.cmd -> cli\agentcompany.exe
>publish\agentcompany.cmd (
    echo @echo off
    echo "%%~dp0cli\agentcompany.exe" %%*
)

echo [4/4] copy manuals into publish\ ...
set COPIED=0
if exist 使用手册.md (
    copy /y 使用手册.md publish\ >nul
    set /a COPIED+=1
) else (
    echo [WARN] 使用手册.md not found at repo root.
)
if exist 换机部署清单.md (
    copy /y 换机部署清单.md publish\ >nul
    set /a COPIED+=1
) else (
    echo [WARN] 换机部署清单.md not found at repo root.
)

rem ---- 摘要：版本取自 csproj 的 <Version>（与 AppInfo.Version 四同源约束）----
set VER=?
for /f "tokens=3 delims=<>" %%v in ('findstr /r "<Version>" AgentCompany.Desktop.csproj') do set VER=%%v

set FILECOUNT=0
for /f %%c in ('dir /s /b /a-d publish ^| find /c /v ""') do set FILECOUNT=%%c

set TOTALBYTES=0
for /f %%s in ('powershell -NoProfile -Command "$s=[long]0; foreach($f in [IO.Directory]::GetFiles('publish','*','AllDirectories')){$s+=(Get-Item -LiteralPath $f).Length}; $s"') do set TOTALBYTES=%%s
set /a TOTALMB=TOTALBYTES/1048576

set BRIDGE=缺位
if exist publish\McpBridge\AgentCompany.McpBridge.exe set BRIDGE=就位

set CLI=缺位
if exist publish\cli\agentcompany.exe if exist publish\agentcompany.cmd set CLI=就位

echo ================================================================
echo  发布摘要
echo    版本        : v%VER%
echo    文件数      : %FILECOUNT% （手册 %COPIED%/2 份）
echo    总大小      : %TOTALBYTES% 字节（约 %TOTALMB% MB）
echo    McpBridge   : %BRIDGE%
echo    终端命令    : %CLI% （cli\agentcompany.exe + agentcompany.cmd）
echo    主程序      : publish\AgentCompany.exe
echo    终端入口    : publish\agentcompany.cmd（设置页[安装终端命令]写入 PATH）
echo ================================================================
endlocal
