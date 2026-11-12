# Tailscale 管理控制台操作手册（admin-console-manual.md）

## 读者与适用范围

- 读者：在 https://login.tailscale.com 上操作本 tailnet 的管理员（本文为逐击/点击级操作指引）。
- Tailnet 域名：<TAILNET>.ts.net
- 控制台内设备名：本电脑 = laptop（Windows）；iPhone = iphone-12-mini
- 控制台现状（操作前应核对一致）：
  - MagicDNS：已开启
  - HTTPS Certificates：未开启
  - 标签：无任何标签
  - serve 配置：无

## 操作顺序（重要）

请严格按顺序完成以下三节：

1. 第 1 节：DNS 页——启用 HTTPS Certificates
2. 第 2 节：Access Controls——用定稿 JSON 整文件替换 policy
3. 第 3 节：Machines 页——给 laptop 打 tag:server、给 iphone-12-mini 打 tag:mobile

顺序原因：第 3 节要分配的标签必须先在第 2 节的 policy（tagOwners）中定义，否则 Machines 页选不到这些标签；后续 serve 配置（见 serve-config.md）依赖第 1 节启用的 HTTPS 证书。

---

## 第 1 节：DNS 页——启用 HTTPS Certificates

### 点击路径

1. 浏览器打开 https://login.tailscale.com，用管理员账号登录。
2. 点击左侧导航栏的 DNS，进入 DNS 页。
3. 在页面中找到 HTTPS Certificates（HTTPS 证书）区块。
4. 点击该区块中的 Enable（启用）按钮。
5. 等待页面确认启用完成。除第 4 步外，请勿改动本页任何其他设置。

### 警告：不要动 MagicDNS

MagicDNS 当前已开启，是本方案的依赖（设备域名 <PC_NAME>.<TAILNET>.ts.net 的解析与 HTTPS 证书签发都依赖它）。在 DNS 页操作时，请勿关闭或修改 MagicDNS 及其 Nameservers 相关设置，以免破坏解析与证书签发。

### 如何确认本步成功

- DNS 页的 HTTPS Certificates 区块显示为 Enabled（已启用）状态；
- 页面无报错、无设置回退；
- 后续核验：监督者执行 tailscale serve --bg 3000（见 serve-config.md）时，不再出现“需先启用 HTTPS 证书”的报错。

---

## 第 2 节：Access Controls——用定稿 JSON 整文件替换 policy

### 点击路径

1. 点击左侧导航栏的 Access Controls（或直接打开 https://login.tailscale.com/admin/acls）。
2. 在策略编辑器文本框内点击，按 Ctrl+A 全选现有全部内容。
3. 按 Delete 删除全部现有内容。
4. 将下面这段定稿 JSON 原样粘贴进编辑器（一字不差、保持单行，含全部引号与方括号）：

```json
{"tagOwners":{"tag:server":["<OWNER_ACCOUNT>"],"tag:mobile":["<OWNER_ACCOUNT>"]},"grants":[{"src":["tag:mobile"],"dst":["tag:server"],"ip":["tcp:443"]}]}
```

5. 点击 Save 保存。

### 醒目警告：这是全文件替换

- 本操作会用上述 JSON **替换整个 policy 文件**——控制台里现有的全部访问控制规则都会被覆盖。请确认无误后再执行。
- 粘贴后必须保持 JSON 合法：不得增删引号、逗号、方括号，不得混入注释或多余字符。
- 点击 Save 时若控制台报校验错误（如 JSON 解析失败、语法错误），**必须先修正再保存**；严禁忽略错误强行保存。

### 如何确认本步成功

- 保存后编辑器上方没有红色校验错误提示；
- 重新打开 Access Controls 页，粘贴的 JSON 仍完整存在（内容已持久化）；
- 内容核对要点：tagOwners 定义了 tag:server 与 tag:mobile（属主均为 <OWNER_ACCOUNT>）；grants 有且仅有一条规则——src 为 ["tag:mobile"]、dst 为 ["tag:server"]、ip 为 ["tcp:443"]。

---

## 第 3 节：Machines 页——给设备打标签

### 前置依赖

第 2 节必须已完成并保存成功（policy 中已定义 tag:server 与 tag:mobile），否则 Machines 页无法选择这两个标签。

### 给 laptop 分配 tag:server（点击路径）

1. 点击左侧导航栏的 Machines（或直接打开 https://login.tailscale.com/admin/machines）。
2. 在设备列表中找到 laptop（Windows，本电脑）。
3. 点击该设备行，进入设备详情页。
4. 找到 Tags（标签）设置项，点击编辑/添加标签。
5. 在可选标签中选择 tag:server。
6. 确认保存。

### 给 iphone-12-mini 分配 tag:mobile（点击路径）

1. 在 Machines 页设备列表中找到 iphone-12-mini。
2. 点击进入设备详情页。
3. 找到 Tags（标签）设置项，点击编辑/添加标签。
4. 选择 tag:mobile。
5. 确认保存。

### 生效延迟说明

标签变更需要等待设备向控制台 check-in 后才完全生效，可能需要片刻。iPhone 端可打开 Tailscale App（必要时断开再重连一次）以加快 check-in。请预留几分钟等待，不要因为页面已显示标签就立即认定网络侧已生效。

### 如何确认本步成功

- Machines 页设备列表中，laptop 行显示标签 tag:server，iphone-12-mini 行显示标签 tag:mobile；
- 等待数分钟（设备 check-in）后刷新页面，两个标签仍然存在；
- （可选核验）在 iphone-12-mini 的 Tailscale App 设备列表中刷新后能看到 laptop 带有 tag:server 标签（设备列表可见性来自 tailnet 成员关系，与 grants 无关；grants 只决定 tag:mobile → tag:server 的 tcp:443 连通性，后者由验收项 A1 实测确认——此句为评审修订，原文把列表可见性误归因于 grants）。

---

## 三节全部完成之后

控制台侧准备就绪。下一步：由监督者按 serve-config.md 执行 tailscale serve 命令（员工不执行）。验收项与判读标准见 ACCEPTANCE-REPORT.md。
