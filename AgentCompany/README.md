# 小公司 · Agent 指挥中心（AgentCompany）

一个 Windows 桌面"小公司"：你当老板，三位数字员工（**claude / opencode / crush**，均为无头 CLI 子进程）在一个受管控的工作区里接活干活。所有命令执行、文件写入、token 花费都在你的**审批与预算**管控之内。

一句话：**把"一句话需求 → 拆解 → 执行 → 评审 → 交付"整条流水线搬进一个可拷贝的 exe。**

当前版本 **v1.1.1**（.NET 8 WPF，205/205 单测全绿）。

---

## 架构

```
┌─────────────────── 小公司 · Agent 指挥中心（WPF 壳 / 终端 CLI 双入口）───────────────────┐
│  组织        任务看板          运行监视           审批中心            预算总览      设置  │
└───────────────┬────────────────────────────────────────────────────────────────────────┘
                │  A2aGateway（调度中枢：CEO 拆解 → DAG 波次执行 → FIFO 并发闸门 → 熔断）
                │              CeoPlanner ── 拆 DAG（人工确认后开工）
   ┌────────────┴───────────┬─────────────────────┬──────────────────────┐
   │ claude（CEO 兼首席）    │ opencode（主力执行） │ crush（测试评审）     │ ← 三员工 CLI
   │ 规划/架构/分派          │ 代码实现/构建        │ 只读评审/中文报告     │   (无头子进程)
   └───────────┬────────────┴──────────┬──────────┴──────────┬───────────┘
               │  AgentCliProcessHost（stdin 下任务 / stdout 收事件 / 看门狗 / Job Object 陪葬）
               │              McpBridge（公司工具 MCP：file_read/write/list + terminal）
               │              ↕ 命名管道 → 审批门（白名单外命令逐条批准/拒绝）
               ▼
       workspace\（每任务一目录：pipeline\task-N ── 产物、review.md、MEMORY.md）
       %APPDATA%\AgentCompany\（state.db 状态库、diag.log、bridge.log、备份）
```

## 项目结构

| 目录/文件 | 说明 |
|-----------|------|
| `AgentCompany.Desktop.csproj` / `App.xaml` / `MainWindow.xaml` / `Pages/` / `Themes/` / `Services/` | WPF 桌面壳：六页面（组织 / 任务看板 / 运行监视 / 审批中心 / 预算总览 / 设置）+ macOS 风格主题（37 键 token 字典、热切换） |
| `AgentCompany.Engine/` | 调度引擎：A2aGateway、CeoPlanner、DagSpec、PromptStore（提示词外置 + 三态回退 + 锚点校验）、TerminalApprovalGate、AgentCliProcessHost、FolderSnapshot、CliKit 等 |
| `AgentCompany.Cli/` | 终端模式（`agentcompany` REPL：多行提交需求、`/folder` `/peek` `/status` `/budget` `/prompts` 等命令），与 GUI 同库共用、双向互斥 |
| `AgentCompany.McpBridge/` | 公司工具 MCP 桥（file_read / file_write / file_list / terminal），员工的内置写工具一律禁用，只能走此桥受管控 |
| `AgentCompany.Core/` | 基础设施：路径沙箱（Sandbox）等 |
| `AgentCompany.Tests/` | 单元测试（205 个） |
| `tools/` | 冒烟脚本（smoke.ps1）等 |
| `使用手册.md` | 详细使用说明（员工角色、审批安全、预算熔断、自定义提示词、终端模式、FAQ） |
| `换机部署清单.md` | 新机器部署步骤（Node.js → 三个 CLI → 鉴权 → 拷贝 → 自检），每步附验证方法 |
| `示例计划.md` | 示例任务计划（可从任务看板 [选择计划] 读入） |
| `DELIVERY-1.1.md` | v1.1.x 交付说明（M0–M6 里程碑、验收记录） |
| `发布.bat` | 一键发布脚本 |
| `acceptance.ps1` / `acceptance2.ps1` | 验收脚本 |

## 核心特性

- **CEO 拆解**：一句话需求 → 1–5 节点 DAG（谁干、依赖谁、只准写哪些文件），人工确认后开工；产出代码的需求最后一个节点固定是 crush 评审节点。
- **四道安全闸（全部默认从严，fail-safe = 拒绝）**：
  1. 命令白名单——白名单外的任何命令走命名管道请求人工审批（120 秒倒计时，超时=拒绝，无"全部批准"）；
  2. 路径沙箱——所有文件读写限制在任务工作区内，`..` 穿越/绝对路径越界直接拒绝；
  3. 文件所有权——DAG 节点只准写自己 `write_targets` 声明的文件；
  4. 员工封锁——三员工内置 Bash/写文件工具一律禁用，只能走公司 MCP 工具；crush 只读（`.crushrc` deny 全部执行/写类工具）。
- **预算与熔断**：每任务三路上限（token / 墙钟 / 重跑次数），到线熔断；等审批的时间不计入墙钟。
- **提示词外置（v1.1）**：`%APPDATA%\AgentCompany\prompts\` 四文件（planner/claude/opencode/crush），三态回退 + 必含锚点校验——改坏不停摆、安全条款改不丢。
- **终端模式（v1.1）**：`agentcompany` 命令直接对话 CEO，与 GUI 同库共用、双向互斥。
- **崩溃恢复**：Job Object 陪葬（宿主死 → 进程树全灭）+ 启动清扫（杀残党、任务标"已中断"、可一键重跑）。
- **调度优化（v1.1）**：同员工节点自动串行、跨员工并行，节点并发上限可调，看门狗按任务粒度。

## 构建与运行

```powershell
# 需要 .NET 8 SDK（Windows x64，WPF 桌面应用）
dotnet build AgentCompany.sln -c Release
dotnet test  -c Release

# 发布（产物在 publish\，免安装可拷贝）
./发布.bat
```

运行前需另装三个员工 CLI 并完成对应鉴权（本仓库不含任何密钥），完整步骤见 `换机部署清单.md`：

```powershell
npm i -g @anthropic-ai/claude-code @opencode/cli @charmland/crush
```

## 说明

- 本项目开发全程**不包含任何真实 API key / 密钥**；需要配置鉴权的位置一律使用占位符，见 `换机部署清单.md`。
- 状态库与日志位于 `%APPDATA%\AgentCompany\`，不在仓库内；`workspace\` 为运行时生成，均已加入 `.gitignore`。

## 许可

仅供学习与研究使用。
