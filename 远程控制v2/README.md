# 远程控制 v2（基于 Tailscale VPN 的手机远程 Agent 操作）

通过 Tailscale 私有网络，用手机（iPhone Safari）安全操作家里电脑：中文门户导航、网页命令行终端。1.0 基线（Tailscale 手机远程访问 Agent）+ 2.0 迭代（手机中文化与可视化远程操作）。

> 本仓库为脱敏交付版：真实凭据、tailnet 域名、账号、IP 均以 `<占位符>` 表示，运行前请按各文档替换。

## 目录结构

```
├── Tailscale手机远程访问Agent执行计划-1.0.md   # 1.0 执行计划（基线架构）
├── 手机端中文与远程操作迭代计划-2.0.md         # 2.0 迭代计划（R1/R2/R3）
├── 交付\            # 1.0 交付：Agent Web UI、启停脚本、policy、验收报告
└── 交付-v2\         # 2.0 交付（纯增量，不动 1.0）
    ├── portal\              # R1 中文门户页（node 静态服务，127.0.0.1:9800）
    ├── tools\ttyd\ttyd.exe  # R2 Web 终端（ttyd 1.7.7 win32）
    ├── ws-gateway.js        # R2 网关：iOS Safari WebSocket 认证兼容
    ├── start/stop-portal.ps1 · start/stop-cmd.ps1
    ├── R2-交付说明.md · R2-测试与验收.md · R2-评审-crush.md
    └── 迭代状态.md          # R1✅ R2✅ R3⏸ 状态看板与续接备忘
```

## 端口与链路

| 入口（tailnet TLS） | 本地服务 | 用途 |
|---|---|---|
| `https://<PC_NAME>.<TAILNET>.ts.net`（443） | 127.0.0.1:3000 | 1.0 Agent Web UI |
| `https://…:8443/` | 127.0.0.1:9800 | v2 中文门户页 |
| `https://…:8443/ttyd` | 127.0.0.1:9802 网关 → 9801 ttyd | R2 网页命令行 |

所有服务仅绑定回环地址；唯一入口是 tailscale serve（TLS）+ policy grants（`tag:mobile → tag:server`，默认拒绝）。

## 部署要点

1. **1.0 基线**：按 `交付\` 内计划文档部署（node UI + serve 443 + policy）。
2. **v2 增量**：
   - 门户：`交付-v2\start-portal.ps1`；serve：`tailscale serve --bg --https=8443 9800`
   - 终端：新建 `交付-v2\credentials.md`（格式见 `R2-交付说明.md`，键：`ttyd_user:` / `ttyd_pass:`），运行 `start-cmd.ps1`；serve：`tailscale serve --bg --https=8443 --set-path=/ttyd 9802`
   - policy grants 追加：`{ "src": ["tag:mobile"], "dst": ["tag:server"], "ip": ["tcp:8443", "tcp:10000"] }`
3. **iOS Safari 兼容**：Safari 不会在 WebSocket 升级请求上携带 Basic Auth 凭据，`ws-gateway.js` 以「Basic 认证 → 会话 Cookie → WS 注入凭据」解决，务必经网关（9802）而非直连 ttyd。

## 安全设计

- 纵深防御：tailnet 私网 + serve TLS + policy grants（最小授权）+ 应用层 Basic Auth（独立于 1.0 UI 密码）
- 所有本地服务仅 127.0.0.1；VNC/RDP 类端口不对局域网/公网开放
- 凭据仅存本机 `credentials.md`（不随仓库分发），脚本启动时自动读取
