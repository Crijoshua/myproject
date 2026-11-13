# 验收报告（ACCEPTANCE-REPORT.md）

## 报告信息

- 方案：通过 Tailscale 从 iPhone 安全访问电脑端 Agent Web UI（验收项定义见同目录 plan.md 第三节 A1–A5）
- 关联文档：admin-console-manual.md（控制台操作手册）、serve-config.md（serve 命令，由监督者执行）
- 统一口径（三份文档一致）：
  - 最终访问 URL：https://<PC_NAME>.<TAILNET>.ts.net
  - 标签：laptop = tag:server；iphone-12-mini = tag:mobile
  - 本地服务端口：3000（仅回环监听）；tailscale serve 对外端口：443
  - Tailnet 域名：<TAILNET>.ts.net

## 引用的既定事实（原样引用，不得改写）

- 定稿 policy（policy-file.json，整文件）：

```json
{"tagOwners":{"tag:server":["<OWNER_ACCOUNT>"],"tag:mobile":["<OWNER_ACCOUNT>"]},"grants":[{"src":["tag:mobile"],"dst":["tag:server"],"ip":["tcp:443"]}]}
```

- 定稿脚本（已获监督者批准）：
  - start-agent-ui.ps1：从 credentials.md 读取 UI_PASSWORD=，启动 C:\Tools\node24\openchamber.cmd --port 3000 --ui-password，detached 方式，并校验仅回环监听。
  - stop-agent-ui.ps1：按 pid 文件杀进程树 + openchamber 进程清扫 + 端口释放。
  - verify-local.ps1：check1 仅回环监听；check2 serve URL 可达，serve 未配置时跳过；check3 未认证 GET 必须为 401/403/3xx，返回 200 即 FAIL。
- 运行环境与冒烟测试：openchamber 2.7.0（npm 包 @openchamber/web）安装在独立 Node v24.22.2（C:\Tools\node24），nvm/node20 环境未动（员工 CLI 在那边）。默认绑定 localhost；--lan 参数被禁止。openchamber 拉起现有 opencode CLI。监督者冒烟测试（冒烟专用端口 3100，与正式服务端口 3000 不同属有意为之，避免占用正式端口）：GET http://127.0.0.1:3100 返回 200 静态外壳（密码门槛位于 API/session 层）；LAN IP 访问被阻断；已确认仅回环监听。

## 验收项总览

| 验收项 | 状态标记 |
| --- | --- |
| A1 | needs-iPhone |
| A2-1 | needs-iPhone |
| A2-2 | needs-human（含如实记录的局限） |
| A3 | 机器可验证 |
| A4 | 机器可验证（原则上） |
| A5 | 部分已验证（绑定层面）+ needs-iPhone（关 VPN 重测） |

---

## A1：iPhone 经 Tailscale 可达，关 VPN 后不可达

- **状态标记**：needs-iPhone
- **操作步骤**：
  1. iPhone 打开 Tailscale App，确认已连入 tailnet（设备 iphone-12-mini，已带 tag:mobile）。
  2. 打开 Safari，访问 https://<PC_NAME>.<TAILNET>.ts.net。
  3. 观察并记录页面是否加载。
  4. 关闭 Tailscale（断开 VPN）。
  5. 再次用 Safari 访问同一 URL。
- **预期结果**：Tailscale 开启时页面能加载；关闭 Tailscale 后不可达。
- **判读方法**：两个观测点同时成立才判 PASS；若关闭 Tailscale 后仍可加载，判 FAIL，排查（缓存、其他路径）后重测。

## A2-1：未授权端口必须超时

- **状态标记**：needs-iPhone
- **操作步骤**：Tailscale 保持开启，用 iPhone Safari 依次访问以下地址，每个至少观察 30–60 秒：
  - https://<PC_NAME>.<TAILNET>.ts.net:22
  - https://<PC_NAME>.<TAILNET>.ts.net:3000
  - https://<PC_NAME>.<TAILNET>.ts.net:8000
- **预期结果**：三个端口均为长时间无响应（TIMEOUT），而不是立即被拒绝。
- **判读方法**：长时间无响应、最终 Safari 报“无法连接/超时”类提示 → 记 TIMEOUT，PASS；若秒级就收到“连接被拒绝”类明确失败（不是超时语义）→ 判 FAIL 并复查后重测（按定稿 policy 仅放行 tcp:443，其余端口应为丢弃/超时行为）。
- **局限说明（如实记录）**：iPhone 上没有 curl、nc 等命令行工具，Safari 无法给出精确状态码与计时，只能以“长时间无响应”近似判断超时。本项在 iPhone 端只能做到此程度。

## A2-2：未授权设备反例验证

- **状态标记**：needs-human（并如实记录局限）
- **局限（如实说明）**：本 tailnet 仅 2 台设备（laptop、iphone-12-mini），且都会被打上标签（tag:server / tag:mobile），缺少一台未打标签的“未授权反例设备”，无法直接端到端演示“未授权设备被拒绝”。
- **操作步骤（务实替代）**：
  1. 策略文件审查：核对控制台生效 policy 即上述定稿 JSON；确认 grants 有且只有一条——src=["tag:mobile"]、dst=["tag:server"]、ip=["tcp:443"]，无任何其他放行项。未打标签设备不匹配任何 src，按默认拒绝处理。
  2. 暴露面核对：由监督者执行 tailscale serve status，确认只有 https://<PC_NAME>.<TAILNET>.ts.net（443 → 127.0.0.1:3000）一个服务，未暴露其他端口。
  3. （可选，需管理员人工执行并负责恢复）反测：临时移除 iphone-12-mini 的 tag:mobile 标签，等设备 check-in 后在 iPhone 上访问 https://<PC_NAME>.<TAILNET>.ts.net，应不可达；测完立即恢复 tag:mobile 并再次等待 check-in。注意：此期间 iPhone 将失去访问能力，务必及时恢复。
- **预期结果**：步骤 1、2 核对无误；如执行步骤 3，移除标签后不可达、恢复后可达。
- **判读方法**：以核对结果为准——grants 唯一且仅放行 tag:mobile → tag:server 的 tcp:443，即策略面 PASS。
- **可证明 / 无法证明（诚实声明）**：
  - 可证明：policy 规则集合唯一（仅 tag:mobile → tag:server 的 tcp:443）；serve 只暴露 443；如执行反测，移除标签后不可达、恢复后可达。
  - 无法证明：缺少第三台未授权设备时，无法真实演示“陌生/未授权设备被拒”，只能依据 policy 语义与暴露面推断。

## A3：未认证访问必须被拒绝

- **状态标记**：机器可验证
- **细微差别（必须注明）**：监督者冒烟测试中 GET http://127.0.0.1:3100 返回 200 静态外壳——密码门槛位于 API/session 层，而非静态外壳层。因此“静态外壳返回 200”不等于认证失效。
- **操作步骤**：
  1. 电脑端运行 verify-local.ps1，查看 check3 结果。
  2. check3 判定标准：未认证 GET 返回 401/403/3xx 记 PASS；返回 200 记 FAIL。
  3. 若对静态外壳测得 200（即上述细微差别），应改测某个 API/session 端点（受密码门槛保护的那一层）后再判。
- **预期结果**：未认证请求得到 401/403/3xx；没有任何 API/session 层端点对未认证请求返回 200。
- **判读方法**：API/session 端点：401/403/3xx → PASS，200 → FAIL；静态外壳可能返回 200（已知细微差别），不据此判 FAIL，改测 API/session 端点定论。

## A4：Agent 进程独立于浏览器持续运行

- **状态标记**：机器可验证（原则上）
- **操作步骤**：
  1. 正常通过浏览器使用 Agent Web UI（https://<PC_NAME>.<TAILNET>.ts.net）。
  2. 完全关闭浏览器。
  3. 电脑端运行 tasklist，确认 agent 相关进程（openchamber 及其拉起的 opencode CLI）仍在运行。
- **预期结果**：关闭浏览器后，agent 相关进程仍存在。
- **判读方法**：以电脑端进程为准（tasklist 输出），不以浏览器端表现为准；进程仍在 → PASS，进程消失 → FAIL。

## A5：LAN 直连被阻断

- **状态标记**：部分已验证
- **已验证部分（引用监督者冒烟测试）**：已在**绑定层面**确认 LAN 被阻断——openchamber 默认仅绑定 localhost（回环）、--lan 参数被禁止、LAN IP 访问被阻断、已确认仅回环监听。此部分无需重复验证。
- **待验证部分（needs-iPhone，关 VPN 重测指引）**：
  1. iPhone 关闭 Tailscale（断开 VPN），保持与电脑同一 Wi-Fi；或改用同一 Wi-Fi 下其他设备。
  2. 访问 http://<局域网IP>:3000（<局域网IP> 为 laptop 的局域网地址）。
  3. 预期：被拒绝或超时（不可达）。
  4. 判读：无法建立连接/长时间无响应 → PASS；能打开页面 → FAIL。

## 附：状态标记含义

- needs-iPhone：需持有 iPhone 的人工执行者现场测试。
- needs-human：需人工（管理员/监督者）执行或决策。
- 机器可验证：可在电脑端以脚本/命令客观判定。
- 部分已验证：部分内容已由监督者冒烟测试证实，其余待补测。
