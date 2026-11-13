评审完成（只读核对 5 份产物 + R1 对照页），报告如下：
R2 迭代『手机可视化操作 CMD 命令行（Web 终端 ttyd）』评审报告
总体评价：5 个产物实到 4 个。start-cmd.ps1、stop-cmd.ps1、credentials.md、R2-测试与验收.md 质量较高、相互一致；但核心产物 portal-index-r2.html 在工作区内完全缺失（全目录无任何 .html 文件），该文件对应的全部核对项无法核验，R2 交付不完整。
【优点】
1. start-cmd.ps1 全项达标：以 `-i 127.0.0.1 -p 9801 -c` 启动（第 94 行）；凭据从同目录 credentials.md 解析（第 19、58-65 行）；pwsh 启动参数内置 `[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; chcp 65001`（第 90 行）；独立维护 cmd.pid / cmd.out.log / cmd.err.log（第 20-22 行）并带“已在运行则跳过”逻辑（第 26-43 行）；路径全部基于 $PSScriptRoot，ttyd.exe 取 `..\tools\ttyd\ttyd.exe`（第 18-23 行）。
2. start-cmd.ps1 防重复启动细节到位：能识别 PID 被复用（进程名 `-ieq 'ttyd'` 校验，第 32 行）并清理残留 pid 文件；启动后休眠 800ms 复查进程未立即退出（第 110-118 行）。
3. stop-cmd.ps1 行为正确：按 pid 强制停止（第 29 行）、轮询确认退出（第 31-37 行）、无论何种路径均清理 pid 文件（第 54 行）、pid 文件不存在 / 内容无效 / 进程已不存在时均提示后正常 exit 0（第 16-19、46-50、55 行）。
4. credentials.md 完全符合要求：含「终端 Basic Auth 用户名/密码」章节（第 8 行）；头部明确「仅本机保存，权限仅当前用户，与 1.0 UI 密码独立」（第 3 行）；密码 `<TTYD_PASS>$mX2@pQ5zT8!e` 为 20 位强密码（大小写+数字+特殊字符，无冒号/空格/双引号，满足 16 位以上）；顶格 `ttyd_user:` / `ttyd_pass:` 格式与 start-cmd.ps1 正则 `^ttyd_(user|pass)\s*:\s*(\S+)\s*$` 精确匹配，还给出了换密码流程与 icacls 收权建议。
5. R2-测试与验收.md 覆盖完整：阶段一含 401（1-1）、whoami/dir/ipconfig 中文无乱码（1-2）、8443 门户卡片跳转 /ttyd/ 可达（1-3）；阶段二为 iPhone 实机同等验证（2-1、2-2）并含锁屏 30 秒会话存活（2-3）；末尾有 A1 命令执行、A2 中文输出、A3 认证 401、A4 基线无损的验收对照表，背景信息与两个脚本实际行为一致。
6. 存在的 4 份文件均为中文文案，只读查看未检出 BOM 字符（U+FEFF），与声明的“UTF-8 无 BOM”相符。
【问题】（按严重程度排列）
1.（严重）portal-index-r2.html 缺失：工作区根目录及子目录中不存在任何 HTML 文件。R2 要求的门户页改造——「命令行终端」卡片由 disabled div 改为可用链接、href 由 JS 拼接为 `'https://' + location.hostname + ':8443/ttyd/'`、描述改为「在手机上直接操作电脑的 PowerShell 命令行」、页脚改为「迭代 2.0 · R1 门户 + R2 Web CMD」——全部无法核对。连锁影响：验收手册步骤 1-3 与 A4 表中门户相关项实际无法执行，R2 门户验收不具备条件。
2.（中）stop-cmd.ps1 第 26-29 行停止进程前不校验进程名：若 cmd.pid 中的 PID 已被其他程序复用，会误杀无关进程。start-cmd.ps1 第 32 行有 `-ieq 'ttyd'` 校验，两个脚本行为不一致。
3.（中）start-cmd.ps1 第 94 行将 `用户名:密码` 明文拼入 ttyd 进程命令行：Windows 下任意本机进程可通过 Win32_Process 等读到 Basic Auth 密码。受 ttyd `-c` 参数形式限制属固有风险，但 credentials.md 的注意事项中未提及该暴露面。
4.（低）stop-cmd.ps1 第 29-43 行：Stop-Process 失败或 2 秒内未退出时仍以 exit 0 结束，且 pid 文件随即被删除（第 54 行），调用方无法感知“停止失败”，后续也无法凭 pid 重试。
5.（低）credentials.md 第 16 行将手机访问地址描述为 `http://127.0.0.1:9801/`：手机实际入口是 `https://<tailnet域名>:8443/ttyd/`，127.0.0.1 仅为本机监听地址，文案易误导使用者。
6.（低）R2-测试与验收.md 步骤 2-3 预期“无需重新认证、无需刷新页面”偏乐观：iOS 锁屏会挂起 Safari 页面并断开 WebSocket，解锁后往往需 ttyd 前端自动重连，建议措辞放宽为“页面恢复/重连后会话仍可用”。
7.（低）start-cmd.ps1 第 101-102 行日志每次启动覆盖写（Start-Process 重定向不追加），异常排障的历史日志会丢失。
【建议】
1. 优先补齐 portal-index-r2.html：以 R1 版（交付-v2/portal/index.html）为底稿，仅将「命令行终端」卡片改为 `<a class="card" id="web-cmd">`，script 中追加 `document.getElementById('web-cmd').href = 'https://' + location.hostname + ':8443/ttyd/';`，描述与页脚按要求修改，保留深色卡片风格、Agent 工作台卡片与 R3 预留卡不动；补齐后再复核核对项 1。
2. stop-cmd.ps1 参照 start-cmd.ps1 增加 `ProcessName -ieq 'ttyd'` 校验，避免误杀；停止失败时改返回非零退出码并保留 pid 文件。
3. credentials.md 注意事项补充“密码会出现在 ttyd 进程命令行中，本机其他进程可读”的风险说明。
4. 修正 credentials.md 中手机访问入口的表述；放宽手册步骤 2-3 的预期措辞；如需保留排障历史，可改为带时间戳的日志文件名。
5. 编码与语言项：现有 4 份文件目测无 BOM、全中文，合规；portal-index-r2.html 补齐后需同样保证 UTF-8 无 BOM。
