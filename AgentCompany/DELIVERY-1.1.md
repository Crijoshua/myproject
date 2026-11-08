# 交付说明 · v1.1.0（M0–M6 全程）

日期：2026-11-07/08 · 交付物：源码 + `publish\`（v1.1.0，31 文件 / 约 412 MB，含双向互斥修复重发布）+ 两份手册更新版 + `M5b-报告.md` + `tools\reviews\m234-crush-review.md`
基线：v1.0.0（轮 8 收官）· 迭代计划：《AgentCompany-v1.1-迭代计划.md》(rev2，2026-11-01)
四大特性：**A 提示词外置与读取优化（M2a/M2b）· B 任务分配优化（M3）· C GUI macOS 风格（M5a/M5b）· D CEO 终端入口（M4）**

> 里程碑快照：`AgentCompany-snapshots\`（m0-baseline / m1-engine-sink / m2a-prompt-store / m2b-settings-ui / m3-scheduling / m4-ceo-cli / m5b-gui-macos-skin / m6-acceptance-release）。仓库已迁移 `Desktop\AgentCompany` → `Desktop\github\AgentCompany`（快照随仓库走；无 git，快照即标签）。

---

## 一、范围回顾（M0–M6）

| 里程碑 | 内容 | 状态 |
|--------|------|------|
| M0 基线固化 | v1.0 基线留档（141/141） | ✅ 11-01 |
| M1 引擎下沉 | 引擎类迁入 `AgentCompany.Engine`（纯移动零行为）+ crush 评审两轮 | ✅ 11-01/02 |
| M2a PromptStore | 提示词外置四文件 + 三态回退 + 锚点校验 + 记忆滚动窗口 | ✅ 11-01 |
| M2b 设置页提示词 UI | 提示词编辑区（四键：来源/编辑/保存/恢复） | ✅ 11-02 |
| M3 调度优化 | 同员工串行 + 员工多样性优先 + 节点并发上限可调 + 看门狗按任务粒度 | ✅ 11-02 |
| M4 CEO 终端入口 | `AgentCompany.Cli` REPL + FolderSnapshot + TerminalApprovalGate + 互斥 + shim/PATH 安装 | ✅ 11-03（r2）+ 本轮联审修复 |
| M5a/M5b macOS 换肤 | 37 键 token 字典 + 接线 + 控件重皮 + 六页换肤 + 主题热切 | ✅ 11-07（M5b 曾于 11-03 因配额中断，本轮恢复完成） |
| M2-M4 联审 | crush 风险聚焦评审（6 风险点，24 次工具调用） | ✅ 11-07，**有条件通过 → 两处中级问题当轮修复，四项低危记 v1.2** |
| M6 验收发布 | 版本四同源 1.1.0 + 发布 + S1–S7 + 边界验收 + 文档 | ✅ 11-07/08（本文） |

## 二、M5b 与联审修复要点（本轮新增改动）

1. **M5b 换肤**：详见《M5b-报告.md》（UIA 冒烟 17/17：41 AutomationId + 文案锚点 + 主题三态像素验证 + 重启持久化）。
2. **联审修复 #1【中】**（`AgentCompany.Engine\CliKit.cs`）：`/peek` 越界判定补分隔符边界——旧 `StartsWith(rootFull)` 在 root=`C:\repo` 时会放行 `../repo2/x.txt`（解析为 `C:\repo2\x.txt` 前缀误命中）；改"相等或 root+分隔符前缀"；新增兄弟目录形态单测 `Peek_兄弟目录同前缀不越界_联审m234`。
3. **联审修复 #2【中】**（`AgentCompany.Engine\TerminalApprovalGate.cs`）：① 终端审批占台互斥（`SemaphoreSlim(1,1)`）——并发请求 FIFO 逐条交互，杜绝两个 Decide 循环交错读写控制台（y/n 归属错乱）；等待方留 `_pending`（墙钟暂停语义不变），拿到台子首查剩余时间，过线即 timeout 拒绝。② 重定向下行读窗口化——单枚挂起的阻塞 `ReadLine` 任务只等当前 1s 窗口，窗口无输入返回 null 交还时钟判定，杜绝"ReadLine 永久阻塞 → 请求滞留 → 墙钟无限暂停"。修复在 S6 实跑中实战验证（无人值守倒计时逐秒推进 → 超时拒绝 → 员工改道）。
4. **反向互斥补齐（6.3#13）**（`App.xaml.cs`）：实测发现 GUI 在 CLI 运行期间**不会拒绝启动**——`NamedPipeServerStream(MaxAllowedServerInstances)` 允许双宿主同时监听同名管道，原设计只有 CLI 侧做了 `PipeProbe` 探测。修复：GUI `OnStartup` 在单实例检查后同样探测 `ApprovalPipe.DefaultPipeName`（500ms），被占即弹「终端版（agentcompany）正在运行，请先关闭后重试。」并退出。实测：CLI 保活 → 双击 GUI → 仅现「启动冲突」弹窗、**主窗口不出现**，双向互斥闭环。
5. **S6 首轮实跑（失败路径，fail-safe 全链路实证）**：任务 #128（hello.py）CEO 拆解正常、y 确认正常、claude 节点无视"不执行命令"约束尝试 `python hello.py` → 终端审批 115 秒倒计时逐秒推进（**redirected-stdin 窗口化修复实战生效**）→ 超时=拒绝落库 → 节点失败止损 → 下游取消 → 任务带因失败 → REPL 干净收尾退出。审批等待期任务墙钟暂停（未被看门狗误杀）。该轮完整走通失败语义链；completed 路径由第二轮补验（见 S6）。
6. **v1.2 待办（联审低危 4 项）**：安装按钮校验 `cli\agentcompany.exe` 而非仅 shim、REG_EXPAND_SZ 展开固化风险；FolderSnapshot 盘相对路径（`C:foo`）口径；REPL 提交前按三员工完整自检提示；DagSpec"多样性优先"注释与实现口径对齐。
7. **前置修复**：仓库迁移后 `AgentCompany-snapshots\` 落入根 glob（6795 编译错）——csproj 追加 `Compile Remove` + `Page Remove` 排除。

## 三、版本四同源

| 源 | 值 |
|----|----|
| `AgentCompany.Desktop.csproj` `<Version>`（发布摘要读此处） | 1.1.0 |
| `AppInfo.Version`（Engine const；CLI 横幅 / 设置页"关于"） | 1.1.0 |
| `MainWindow.xaml` 窗口标题 | v1.1.0 |
| `Engine/Cli/McpBridge` csproj | 1.1.0（新增，此前仅 Desktop 有） |

## 四、验收结果（S1–S7 + 边界）

| # | 断言 | 结果 | 证据 |
|---|------|------|------|
| S1 | `dotnet build -c Release` | **PASS** | 0 错误、0 警告（基线 2 条历史告警亦未复现） |
| S2 | `dotnet test -c Release` | **PASS** | **204/204 全绿**（= M4 后基数 203 + 联审修复新增 1；存量源文件零改动口径保持） |
| S3 | GUI 启动 | **PASS** | 可移植副本窗口标题「小公司 · Agent 指挥中心 v1.1.0」；设置页关于 1.1.0；macOS 皮肤在位（底色 #FAFAFB≈Base）；六项自检通过（无弹窗） |
| S4 | `发布.bat` | **PASS** | 摘要 v1.1.0 / 30 文件 / 412.3 MB / McpBridge 就位 / agentcompany.cmd+cli 就位 / 手册 2/2 |
| S5 | 可移植 + 旧库直通 | **PASS** | publish 整夹拷 `%TEMP%\opencode\ACv11-portable` 双击可用；真实旧 state.db（含 127 条任务历史）零迁移零报错；旧设置可读（max_parallel_tasks=2）；工作区根=默认（跟随 exe） |
| S6 | CLI 终端闭环 | **PASS**（两轮：失败语义链 + 完成路径） | **轮1（#128）**：拆解→y→claude 越约束跑 `python hello.py`→终端审批 115s 倒计时（redirected-stdin 修复实战）→timeout=拒绝→节点止损→下游取消→任务带因失败→干净退出；审批等待期墙钟暂停未误杀。**轮2（#129）**：纯文档任务 2 节点（opencode 写 10 笔记+ready.txt ∥→ crush 评审），2 分钟完成，12 产物 + **review.md**（crush 中文评审「通过验收，可交付」），MEMORY.md +1 行，/quit 退出码 0。审批跨宿主同库实证：approval_records 有 `claude \| python hello.py \| timeout \| 22:20:06→22:22:01`（CLI 写入、GUI 审批中心同表可读） |
| S7 | PATH 安装后新开终端 `agentcompany` | **PASS** | 设置页 UIA 点击[安装终端命令]→状态行「终端命令：已安装」+ 提示「已写入用户 PATH…」；用户 PATH 注册表实测含 publish 根且**仅 1 条**（幂等）；PATH 重建会话 `agentcompany --version` →「小公司 · Agent 指挥中心 · 终端版 v1.1.0」exit 0 |
| — | GUI/CLI 双向互斥（6.3#13） | **PASS** | GUI 在跑 → CLI exit 2「桌面版正在运行」；CLI 在跑 → GUI 仅弹「启动冲突」、主窗口不出现（**本轮补齐的修复**，见二.4） |

**边界条件覆盖（6.3 十七项）**：① 提示词三态×四文件 → PromptStoreTests 单测 + CLI `/prompts` 实查（四键=内置，外置目录全局共享 GUI/CLI）；② 锚点安全底线 → PromptStoreTests；③ MEMORY 窗口 → MemoryWindowTests；④⑤⑥ 并发/串行/黄金对照 → SchedulerV11Tests；⑦ 旧库直通 → S5 实测；⑧ UIA 红线 → M5b 冒烟 41 AutomationId + 文案锚点全命中（accept_round8 的 UIA 断言超集）；⑨ 主题三态 → M5b 冒烟像素验证 + 重启保持；⑩ 看门狗口径 → WatchdogPerTaskTests + S6 实跑（审批等待期倒计时/暂停实证）；⑪ 崩溃恢复 → v1.0 语义未动（StartupSweepTests/GracefulExitTests），CLI Ctrl+C=优雅退出设计同源；⑫ 可移植+prompts 目录 → S5 + `/prompts` 实查；⑬ GUI/CLI 双向互斥 → 双向实测（CLI→exit 2；GUI→「启动冲突」无主窗，见二.4）；⑭ --folder 边界 → CliTests（不存在/相对路径/条目 500/深度 3）+ `--folder` 实跑树摘要；⑮ 终端审批等价 → S6 实跑：白名单外命令 115s 倒计时→超时拒绝→落库（与 GUI 同库同口径）；⑯ CLI 中断恢复 → 设计同 GUI（Ctrl+C 优雅退出留档，未做破坏性实杀）；⑰ 终端编码 → 管道全程 UTF-8 中文无乱码（Windows Terminal 口径，见五.6 工具侧注记）；conhost chcp 提示为横幅逻辑，留档。

## 五、已知问题与遗留

1. v1.2 待办 4 项（联审低危，见二.6）；
2. publish 体积约 412 MB（CLI 第二套自包含运行时；可后续改框架依赖共享）；
3. system 主题不监听 Windows 深浅实时切换（Apply 时刻解析，重启/重选生效）；
4. 状态库同机共享设计延续（多副本共用 %APPDATA%，轮 8 已知问题 1）；
5. 发布.bat 摘要"总大小"行显示 0 字节（Measure-Object 取值链路小毛病，不影响产物；文件数/版本/就位项均正确）；
6. **验收工具侧编码注记（非产品缺陷）**：S6 两轮需求经 Windows PowerShell 5.1 管道下发，`$OutputEncoding` 默认 ASCII，中文在进 CLI 前被替换为 `?`（任务标题与 MEMORY 行可见 `?` 串）；产品自身 stdin/任务文本链路为 UTF-8（GUI 路径、CLI 输出方向、员工工作区文件中文均验证无损——#129 十篇笔记正文中文正常即为输出侧实证）。后续无人值守驱动请先 `$OutputEncoding = [Text.Encoding]::UTF8`；
7. S6 轮1 中 claude 节点在审批被拒后会话异常终止（"错误\|glm-5.3"）——止损语义正确（失败带因、下游取消），但"被拒后员工改道重试"的适应性在该轮未呈现（v1.0 观察期 opencode 曾多次改道成功）；属员工行为差异，留观。


## 六、跑批史（本轮验收实录）

- `tools\reviews\m234-crush-review.md`（crush 联审，2026-11-07）
- M5b UIA 冒烟 17/17（`%TEMP%\opencode\m5b-smoke.ps1`，双跑一致）
- M6 S3/S5 脚本（`%TEMP%\opencode\m6-s3s5.ps1`，6/6）
- S6 两轮实跑日志（`%TEMP%\opencode\m6-s6-run.log` / `m6-s6-run2.log`）+ 产物 `publish\cli\workspace\pipeline\task-129\`
- S7 / 反向互斥脚本（`%TEMP%\opencode\m6-s7.ps1` / `m6-mutex-reverse.ps1`）
- v1.1.1 冒烟 7/7（`%TEMP%\opencode\v111-smoke.ps1`：标题/关于/安装双态/PATH 幂等与类型）

## 七、v1.1.1 加固修订（2026-11-08）

联审低危 4 项 + 发布脚本缺陷，全部闭环（版本四同源升 1.1.1；205/205 全绿；发布实测摘要 v1.1.1 / 53 文件 / 432,511,028 字节）：

| # | 改动 | 文件 | 验证 |
|---|------|------|------|
| 1 | 安装终端命令三项加固：① 双件校验（shim + `cli\agentcompany.exe`，开发目录 bin 天然被拒）② 注册表原始读写（`DoNotExpandEnvironmentNames`，不再展开固化 `%…%`，保持原值类型）③ 补 WM_SETTINGCHANGE 广播（`NativeWin.BroadcastEnvironmentChange`，保"重开终端即可用"） | `Pages\SettingsPage.xaml.cs`、`AgentCompany.Engine\JobObject.cs` | 冒烟：publish 放行幂等；bin 缺件拒绝；PATH 仅 1 条、类型保持（String） |
| 2 | FolderSnapshot 拒盘相对路径：`C:foo` 的 `IsPathRooted`=true 但按盘当前目录解析，改 `IsFullyRooted`（盘符+目录或 UNC）；新增单测 | `AgentCompany.Engine\FolderSnapshot.cs`、`AgentCompany.Tests\CliTests.cs` | `FolderSnapshot_盘相对路径拒绝_联审v12` 通过（205/205） |
| 3 | REPL 提交前三员工提示：claude 缺席仍硬禁；opencode/crush 缺席告警（可提交，节点期闸门拒绝） | `AgentCompany.Cli\Program.cs` | 代码路径审查（破坏路径覆盖属破坏性环境改动，不实跑） |
| 4 | DagSpec.SelectReady 注释与实现对齐："按 seq 扫描 + 跳过忙碌员工"（效果等价多样性优先，非排序实现） | `AgentCompany.Engine\DagSpec.cs` | 纯注释 |
| 5 | 发布.bat 总大小行修复：`^|` 在双引号内对 cmd 是字面量 → PowerShell 收到裸 `^` 语法错误 → 恒 0（v1.0 起遗留）。改无管道表达式（`foreach` 累加），GBK+CRLF 字节级替换保持 | `发布.bat` | 实测摘要 432,511,028 字节（原恒 0） |

版本四同源 1.1.1 复核：csproj×4 / AppInfo / 窗口标题 / 设置页关于 / CLI `--version` / McpBridge 头 / 两份手册标题，全部一致。
