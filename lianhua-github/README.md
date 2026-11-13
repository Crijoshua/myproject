# 最终练化 · LianHua

> 纯前端、零依赖、离线可用的健身训练记录 Web 应用（PWA）
> A dependency-free, offline-capable workout tracking web app (PWA).

深色 / 衬线 / 细线的 FF15 风格界面，专为手机在健身房单手操作设计，也可在电脑浏览器中使用。所有数据保存在本机浏览器的 `localStorage` 中，不上传任何服务器。

Dark, serif, thin-line UI inspired by Final Fantasy XV, designed for one-handed phone use in the gym. All data stays in your browser's `localStorage` — nothing is ever uploaded.

## 功能 Features

- **动作库**：内置 564 个动作（热身 / 胸 / 背 / 腿 / 肩 / 臂 / 核心 / 全身 / 奥举 / 有氧），支持搜索 + 部位 / 阶段 / 类型三维筛选，全部附带中英双语名称
- **训练计划**：多套计划管理（新建 / 切换 / 重命名 / 删除），动作可配置组数、次数或秒数、重量、组间歇，可排序
- **引导式训练**：状态机流程（准备 → 进行中 → 组间休息 → … → 总结），休息倒计时归零提示音（Web Audio 合成，无音频文件）、屏幕常亮（Wake Lock）、计时全部基于时间戳，锁屏 / 刷新后可精确恢复
- **训练历史**：最多 200 条记录，逐组回看完成情况
- **数据管理**：导出 / 导入 JSON 备份（换机恢复），二次确认的清空
- **中英双语界面**：所有界面文案均为「中文 + 小号英文」双语显示

## 使用 How to Use

- **在线**：直接访问 GitHub Pages 部署地址（见下方部署步骤），手机浏览器打开后可「添加到主屏幕」当 App 使用
- **本地**：双击打开 `lianhua.html` 即可，无需安装任何东西

## 部署到 GitHub Pages Deploy

1. 登录 GitHub → 右上角 **+** → **New repository**，名称随意（如 `lianhua`），选 Public，**不要**勾选 "Add a README"，点击 **Create repository**
2. 在新仓库页面点击 **"uploading an existing file"** 链接
3. 把本目录内的**全部文件**拖入上传区 → **Commit changes**
4. 仓库 **Settings → Pages** → Branch 选 `main`、目录选 `/ (root)` → **Save**
5. 等待 1~2 分钟，访问 `https://你的用户名.github.io/仓库名/` 即可

> `index.html` 会自动跳转到主页面 `lianhua.html`，无需额外配置。
> 提示音、屏幕常亮等特性需要 HTTPS 环境，GitHub Pages 自带 HTTPS，天然满足。

## 文件结构 Structure

```
├── index.html           # GitHub Pages 入口（跳转到 lianhua.html）
├── lianhua.html         # 应用主页面
├── style.css            # 样式（FF15 风格深色主题）
├── app.js               # 全部业务逻辑（M0~M6 模块）
├── exercises.js         # 内置动作库（564 条，含自检）
├── translations.js      # 动作英文名对照表（仅用于界面显示）
├── manifest.webmanifest # PWA 清单
└── apple-touch-icon.png # 应用图标
```

## 数据说明 Data

- 计划与历史保存在浏览器 `localStorage`（key 前缀 `lianhua.`），**清除浏览器数据会一起清掉**，建议每周用「数据管理 → 导出备份」保存一份 JSON
- 备份文件可导入本机或换机恢复

## License

[MIT](./LICENSE)
