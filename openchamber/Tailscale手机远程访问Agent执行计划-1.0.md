# 计划 1.0｜通过 Tailscale 从 iPhone 安全访问电脑端 Agent Web UI

> 本计划已通过两轮技术审核并放行，可直接复制给编程 Agent 执行。
> 执行顺序为 Step 1 → Step 5，完成后按第三节 A1–A5 逐条验收，全过即上线。
>
> **占位符说明**：`<你的账号@example.com>` 替换为 Tailscale 账号邮箱；`<电脑名>` 替换为 Tailscale 管理台中显示的设备名；`<tailnet>` 替换为你的 tailnet 域名（如 `xxxx.github`）；`<密码>` 替换为强随机密码。

---

## 一、架构概述

- 手机仅作浏览器终端；代码、会话、执行环境、凭据全部留在电脑本机。
- 网络暴露面收敛为**单端口**：Web UI 服务绑定回环地址（127.0.0.1），不直接对任何网络接口开放；唯一入口是 Tailscale Serve 在 443 端口终止 TLS 后反代到本地端口。
- grants（访问控制）最小化为单条规则：仅 `tag:mobile` 设备可访问 `tag:server` 的 `tcp:443`。
- 安全边界三层 + 一道纵深：
  1. Tailscale grants——网络层准入（哪些设备能到达 443）
  2. 回环绑定——消除 LAN/公网直连面
  3. 专用系统用户——进程权限隔离
  4. UI 密码——应用层纵深防御（**不是**唯一边界，不承担网络准入职责）

---

## 二、执行步骤

### Step 1｜网络层：装 Tailscale、开 MagicDNS + HTTPS、定义并分配标签

1. 电脑和 iPhone 安装 Tailscale，登录同一账号。
2. Tailscale 管理台 → DNS：开启 **MagicDNS** 和 **HTTPS Certificates**（Serve 的必要前提，缺了 CLI 会报错并引导开启）。
3. 在 policy file 中定义 tagOwners（完整 policy file 见 Step 2，两步合并为一份提交）：
   - `tag:server` → 归属 `你的账号@example.com`，分配给电脑
   - `tag:mobile` → 归属 `你的账号@example.com`，分配给 iPhone
4. 分配方式二选一：管理台 → Machines 手动打标签；或生成携带对应 tag 的 auth key 注册设备使其自动携带。
5. **前提确认**：iPhone 端 Tailscale App 保持开启，确保 `.ts.net` 域名可解析（Serve 只支持域名访问，不支持 Tailscale IP 直连；iPhone 官方 App 默认满足）。

### Step 2｜Policy file：tagOwners + grants 合并为一份提交

**注意：以下是一个完整文件，不是两个。** policy file 全局只有一份，分次粘贴会互相覆盖。

```json
{
  "tagOwners": {
    "tag:server": ["<你的账号@example.com>"],
    "tag:mobile": ["<你的账号@example.com>"]
  },
  "grants": [
    {
      "src": ["tag:mobile"],
      "dst": ["tag:server"],
      "ip": ["tcp:443"]
    }
  ]
}
```

要点：
- deny-by-default：不显式放行 3000、8000 或 22。Web UI 监听在回环地址，外部无法直连，所有入站流量统一经 Serve 走 443。
- grants 是 Tailscale 官方 2025 年起推荐的新配置语法，与旧 ACL 写法可共存；新 tailnet 直接用 grants。
- **降级备选（非默认）**：若确需保留 HTTP 直连降级通道，在 `ip` 中追加 `"tcp:3000"` / `"tcp:8000"`，并在 Step 3 将服务绑定 Tailscale IP（100.x.x.x）而非回环。默认方案不需要。

### Step 3｜启动 Web UI（绑定回环地址）

OpenChamber（统一用 CLI 参数，不用环境变量——`OPENCHAMBER_PORT` 无官方文档出处，避免静默失败）：

```bash
openchamber --host 127.0.0.1 --port 3000 --ui-password <密码>
```

cptr：

```bash
cptr run --host 127.0.0.1 --port 8000
```

要点：
- 绑定 127.0.0.1 后，只有本机的 Tailscale Serve 能代理到达，局域网和公网均无法直连。
- **cptr 安全警告**：首次启动会打印一次性 setup URL（含 `/?token=<64-hex>`），这是创建管理员账户的唯一凭据，账户创建后立即失效。妥善保存、严禁写入共享日志或截图。
- **异常回退**：若某工具拒绝监听回环以外的预期行为，以实际启动日志为准。万不得已退回 `0.0.0.0` 时，必须同时用主机防火墙封锁这些端口的 LAN/公网网卡，且验收项 A5 变为必测。

### Step 4｜Tailscale Serve（统一入口）

```bash
tailscale serve --bg 3000
# 或（二选一，当前单服务场景）
tailscale serve --bg 8000
```

- 手机访问 `https://<电脑名>.<tailnet>.ts.net`。Serve 在 443 终止 TLS 并反代到 `127.0.0.1:<端口>`；grants 已放行 443，应立即可达。首次请求可能略慢（自动签发证书）。
- **脚注（日后扩展用）**：若需要**同时**通过 Serve 暴露两个服务，第二个必须挂备用 HTTPS 端口，并同步更新 grants：

```bash
tailscale serve --bg 3000                    # 占用 443
tailscale serve --bg --https=8443 8000       # 占用 8443（Serve 公开端口仅限 443 / 8443 / 10000）
```

  grants 的 `ip` 相应改为 `["tcp:443", "tcp:8443"]`，访问地址为 `https://<电脑名>.<tailnet>.ts.net:8443`。

### Step 5｜隔离与纵深防御

1. Agent 以**专用系统用户**运行，仅授予项目目录权限；该用户的环境变量和凭据与主账号隔离。
2. UI 密码定位为应用层第二道防线。若 tailnet 内存在不应访问 Agent 的设备，在 grants 的 `src` 层面排除，不能只靠密码。
3. Tailscale 账号开启 2FA。

---

## 三、验收（5 条硬性放行标准）

| 编号 | 验收项 | 放行标准 | 验证方法 |
|------|--------|----------|----------|
| A1 | Serve 可达 | Tailscale 开启时 iPhone 能加载 `https://<电脑名>.<tailnet>.ts.net`；关闭 Tailscale 后不可达 | 开/关 VPN 对比访问 |
| A2 | Grants 生效 | ① iPhone 无法访问未放行端口（22、3000、8000），预期**超时**（非 RST）；② **关键反例**：tailnet 内一台未打 `tag:mobile` 的设备访问 Serve 地址应被拒绝 | ① iPhone 上 curl 未放行端口；② 用未授权设备访问 443 |
| A3 | UI 认证生效 | 未携带密码/未认证时被拒绝 | 无凭据访问 Serve 地址 |
| A4 | 会话保持 | 关闭 Safari 标签后重新打开，Agent 任务仍在电脑端运行 | 以电脑端进程/日志为准，不以手机页面状态为准 |
| A5 | LAN 反例 | 同一 Wi-Fi 下其他设备访问 `http://<局域网IP>:<端口>` 应被拒绝或超时 | 另一台 LAN 设备 curl/浏览器测试 |

说明：
- A2 ② 是 grants 的鉴别性验证：服务绑定回环后，A2 ① 即使 grants 写错也会通过，必须用未授权设备反测 443 才能证明 grants 真正生效。
- A4 验证 Agent 进程持久性，与网络回连解耦；网络切换后 Web UI 能否重新加载属于 A1 的延伸观察，不单独设门槛。
- A5 在回环绑定方案下理论上自动满足，但执行成本近零，首次部署仍建议实测一次——它同时验证"服务确实绑对了地址"，防止 CLI 参数被静默忽略。若 Step 3 走了 0.0.0.0 回退路径，A5 为必测项。

---

## 四、放行结论

完成 Step 1–5、通过 A1–A5 后放行。

**补充路径（非阻塞）**：若仅需手机查看 Agent 进度，OpenChamber 自带 Cloudflare 快速隧道和 Private Relay（端到端加密、无需开端口），配置成本低于本方案，可作为轻量替代；两案不互斥。
