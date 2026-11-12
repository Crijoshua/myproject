# OpenChamber 远程 Agent 访问方案

通过 Tailscale 从手机安全访问电脑端 Agent Web UI 的完整交付方案，含中文门户页迭代。

## 项目结构

```
├── Tailscale手机远程访问Agent执行计划-1.0.md   # 1.0 主计划（架构 + 5 步执行 + 5 条验收）
├── 手机端中文与远程操作迭代计划-2.0.md         # 2.0 迭代计划（中文门户 / Web CMD / 远程桌面）
├── 交付\                                       # 1.0 交付物（策略、脚本、验收报告、操作手册）
└── 交付-v2\                                    # 2.0-R1 交付物（中文门户页 + 独立启停脚本）
```

## 架构要点

- 手机仅作浏览器终端；代码、会话、凭据全部留在电脑本机
- Web UI 仅绑定 `127.0.0.1`，唯一入口是 Tailscale Serve（TLS）反代
- grants 最小化：`tag:mobile → tag:server`，deny-by-default
- 纵深防御：grants 网络准入 + 回环绑定 + 专用系统用户 + UI 密码

## 2.0 迭代进度

- [x] **R1 中文门户页**：全中文移动端导航页，经 Serve 8443 端口暴露，本地测试通过
- [ ] R2 Web CMD（ttyd 手机命令行）
- [ ] R3 远程桌面（Guacamole 操作资源管理器 / Edge）

## 运行（R1 门户页）

```powershell
# 启动（仅回环 127.0.0.1:9800，需 Node.js）
.\交付-v2\start-portal.ps1

# 停止
.\交付-v2\stop-portal.ps1
```

手机访问：`https://<你的电脑名>.<你的tailnet>.ts.net:8443`（需先执行 `tailscale serve --bg --https=8443 9800` 并在 policy 放行 8443）。

## 安全说明

- `credentials.md`、日志、pid 文件**不随仓库分发**，请在本机 `交付\credentials.md` 中维护凭据
- 仓库内所有文档已脱敏：tailnet 域名、账号、IP 均以占位符表示
