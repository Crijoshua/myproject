# Tailscale serve 配置说明（serve-config.md）

## 前置条件（必须先完成）

必须先完成 admin-console-manual.md 中的全部控制台步骤：

1. DNS 页：HTTPS Certificates 已启用（MagicDNS 保持开启、未做改动）；
2. Access Controls：policy 已用定稿 JSON 整文件替换，grants 仅放行 tag:mobile → tag:server 的 tcp:443；
3. Machines 页：laptop 已打 tag:server、iphone-12-mini 已打 tag:mobile，并已等待设备 check-in 生效。

若任一步未完成，请先回到 admin-console-manual.md 补齐，再执行本文命令。

## 执行人声明（重要）

本文所有 tailscale 命令均由**监督者**在电脑端（laptop，Windows）执行；**员工不执行**这些命令。

## 背景：为什么是 443 与 3000

电脑端 Agent Web UI 由 start-agent-ui.ps1 启动：C:\Tools\node24\openchamber.cmd --port 3000 --ui-password，仅回环监听（localhost）。tailscale serve 在 tailnet 侧的 443 端口终止 TLS，并把流量反代到本机回环的 3000 端口；外部设备只能经 Tailscale（受 policy 的 tcp:443 放行约束）访问，无法绕过。

## 命令

### 1. 启动（后台方式，单服务自动占用 443）

```text
tailscale serve --bg 3000
```

- --bg 表示后台运行；
- 单服务场景下 tailscale 自动占用 443 端口，无需手动指定；
- 预期：命令成功返回、无报错。

#### 若报错：未启用 HTTPS 证书

若控制台未先启用 HTTPS Certificates（admin-console-manual.md 第 1 节），tailscale serve 会报错并提示需要先开启 HTTPS 证书。此时请先完成 admin-console-manual.md 第 1 节，再重新执行本命令。

### 2. 验证

```text
tailscale serve status
```

预期输出要点：

- 输出中存在一个处于运行/启用状态的 serve 服务（而非空结果/无服务）；
- 该服务将 https://<PC_NAME>.<TAILNET>.ts.net（443 端口）的流量代理到本机 127.0.0.1:3000；
- 无错误状态。

### 3. 最终访问 URL

https://<PC_NAME>.<TAILNET>.ts.net

iPhone 连入 Tailscale（iphone-12-mini，tag:mobile）后，用 Safari 访问上述 URL 即可到达电脑端 Agent Web UI；policy 仅放行 tag:mobile → tag:server 的 tcp:443，未放行任何其他端口。

### 4. 卸载（停用 serve）

```text
tailscale serve off
```

- 停止 serve 并释放 443 端口；
- 卸载后如需恢复，重新执行第 1 步命令即可。

## 关联文档

- 控制台准备步骤（必须先行完成）：admin-console-manual.md
- 验收项与判读标准：ACCEPTANCE-REPORT.md
