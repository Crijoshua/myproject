三份目标文档不在指定目录，先扩大搜索范围确认是否存放在其他位置。三份文档实际位于当前工作目录，开始通读。# 评审报告：admin-console-manual.md / serve-config.md / ACCEPTANCE-REPORT.md
> 评审范围说明：指定目录 `Temp\opencode\plan` 下仅有 plan.md，三份待评审文档实际位于评审工作区根目录（task-133），已按实际位置完成通读，并与需求规格逐条比对。
## 一、优点
1. **与基准逐条对齐度高**：admin-console-manual.md 三节（DNS 启用 HTTPS Certificates / Access Controls 整文件替换 / Machines 打标签）齐全，且每节均附"如何确认本步成功"的核验方法，满足"每步附核验方法"要求。
2. **定稿 JSON 一字不差**：admin-console-manual.md:58 与 ACCEPTANCE-REPORT.md:18 中的 policy JSON 与基准完全一致（逐字符相同），并明确了"一字不差、保持单行、全文件替换、JSON 合法性、校验错误必须先修正"等警告。
3. **顺序依赖与前置条件交代清晰**：标签须先在 tagOwners 定义再分配、HTTPS 证书先启用再 serve、设备 check-in 延迟（admin-console-manual.md:100-102）均有说明；serve-config.md 前置条件三条齐全，并含"HTTPS 证书未开则 serve 报错"的处理（serve-config.md:33-35）。
4. **执行边界明确**：serve-config.md:13-15 明确命令由监督者执行、员工不执行，与基准一致。
5. **命令与 URL 准确且三份一致**：`tailscale serve --bg 3000` / `serve status` / `serve off`、最终 URL `https://<PC_NAME>.<TAILNET>.ts.net`、标签 laptop=tag:server / iphone-12-mini=tag:mobile、端口 3000/443、域名 <TAILNET>.ts.net 在"统一口径"（ACCEPTANCE-REPORT.md:7-12）中固定，三份文档一致；命令/JSON/URL/标签均为 ASCII。
6. **验收映射忠实**：A1–A5 逐条对应基准——A1 needs-iPhone（Safari + 关 VPN 复测，含缓存排查）；A2-1 三个端口超时判据（非 RST）并如实说明 iPhone 无 curl/nc 的局限；A2-2 如实记录"两台设备均打标签、缺反例设备"的局限，并给出 policy 审查 + serve status + 临时摘标签反测三层替代；A3 正确处理"静态外壳 200"细微差别（改测 API/session 端点，401/403/3xx PASS、200 FAIL）；A4 以 tasklist 进程为准；A5 区分"绑定层面已验证"与"关 VPN 重测"。
7. **状态标记体系（needs-iPhone / needs-human / 机器可验证 / 部分已验证）** 定义清晰并附含义说明，便于人工接手。
## 二、问题
1. **【次要】admin-console-manual.md:108（第 3 节"如何确认本步成功"的可选核验）**
   问题：将"iPhone App 设备列表中能看到 laptop"归因为"因为 policy 的 grants 已放行 tag:mobile → tag:server 的 tcp:443"，技术归因不成立。设备列表可见性由 tailnet 的设备可见机制决定，与 grants 是否放行无关；grants 只影响能否建立连接。该核验不能作为 grants 生效的证据。
   修改建议：改写为"（可选核验）在 iPhone App 设备列表中确认 laptop 在线、域名可解析；设备列表可见性与 grants 无关，不能据此判定 grants 生效，grants 生效以验收报告 A2 系列为准"。
2. **【次要·须核实】ACCEPTANCE-REPORT.md:25（引用的既定事实——冒烟测试）**
   问题：冒烟测试写作 `GET http://127.0.0.1:3100`，与三份文档统一口径的本地服务端口 3000 不一致（基准红线要求端口口径完全一致）。本次评审基准中亦未提供"3100"这一既定事实。
   修改建议：核实冒烟测试实际端口——若为笔误，必须改为 3000 后方可放行；若监督者冒烟测试确在临时端口 3100 上进行，请在句中补一句说明（如"冒烟测试使用临时端口 3100，正式服务端口为 3000"），避免读者误判口径不统一。
3. **【次要·须核实】ACCEPTANCE-REPORT.md:25（超出基准事实清单的技术细节）**
   问题：`npm 包 @openchamber/web`、`Node v24.22.2` 两处细节超出本次评审基准明示的既定事实（基准仅确认 openchamber 2.7.0 与 C:\Tools\node24），评审无法独立证实；A4 中"openchamber 及其拉起的 opencode CLI"同理。
   修改建议：逐项对照会话既定事实确认来源；确认无误可保留，无法溯源的细节应删除或降格为"以实际环境为准"。
## 三、建议（可选改进）
1. admin-console-manual.md 第 1 节可补充 DNS 页直达链接 `https://login.tailscale.com/admin/dns`，与第 2、3 节已给出的 `/admin/acls`、`/admin/machines` 直达链接风格统一。
2. A2-1 可补一句提示：iOS Safari 对非常规端口的失败提示文案不统一（可能显示"无法打开页面"而非"超时"），判读以"长时间无响应（30–60 秒以上）"这一行为特征为准。
3. A4 可附具体检索示例（如 `tasklist` 输出中查找 openchamber / node 相关进程名），降低执行者判读成本。
4. A5 待测部分可注明获取 `<局域网IP>` 的方法（电脑端 `ipconfig` 查看局域网网卡 IPv4）。
5. 可在 ACCEPTANCE-REPORT.md 的"验收项总览"表中为 A2-2 补充一句"建议优先执行步骤 3 反测以获得最强证据"，明确三个替代手段的优先级。
## 四、总体结论
**修改后放行。** 三份文档与需求规格的符合度高：JSON 逐字符一致、命令/URL/标签/端口口径统一、执行人边界与局限记录诚实，无阻断或重要问题。放行前需完成三处次要修改/核实：修正或加注 admin-console-manual.md:108 的 grants 归因表述；核实并处理 ACCEPTANCE-REPORT.md:25 的 3100 端口口径；确认 npm 包名、Node 版本等细节均有既定事实来源。上述修改完成后即可放行。
