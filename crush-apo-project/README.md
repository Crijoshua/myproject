# Crush CLI 的 APO 提示词优化插件

本项目是一套**给 Crush CLI 做的 APO（Automatic Prompt Optimization，自动提示词优化）插件**，
由两个部分组成：一个安装在 Crush 里的 Skill 插件（入口），加一个 APO 优化引擎工作区（核心）。

它的作用是：**自动反复优化一个"系统提示词"，直到产出的提示词在评测任务上拿到更高的分数**，
优化结果写入 `apo/best_prompt.md`，可以直接拿来用在别的编程助手上。

---

## 两个组成部分

| 目录 | 身份 | 说明 |
|---|---|---|
| `apo-optimizer/` | **Crush 插件（Skill）** | 安装在 `~/.config/crush/skills/` 后，用户对 Crush 说"跑一下 APO / 优化提示词"即可触发，后台启动优化任务，不阻塞对话 |
| `apo/` | **APO 核心引擎工作区** | 单文件 Python 脚本 `apo_loop.py` 实现完整的"生成 → 打分 → 改写提示词"闭环 |

## 一图看懂整体流程

```
用户在 Crush 里说："跑一下 APO"
        │
        ▼
┌─────────────────────────────┐
│  apo-optimizer（Crush Skill） │
│  SKILL.md 识别触发词          │
│  scripts/run_apo.py          │
│   · 找到 ~/apo/apo_loop.py   │
│   · subprocess.Popen 后台启动 │
│   · 立即返回"已启动"          │
└──────────────┬──────────────┘
               │ 后台运行
               ▼
┌─────────────────────────────┐
│  apo（APO 引擎工作区）        │
│  apo_loop.py 闭环循环：       │
│   1. 用候选提示词生成代码      │
│   2. 硬校验（危险操作一票否决） │
│   3. LLM 评分员打分           │
│   4. 按失败原因改写提示词      │
│   5. 更优则写入 best_prompt.md│
└─────────────────────────────┘
        │
        ▼
1-3 分钟后查看 apo/best_prompt.md 获得优化后的提示词
```

## 快速开始

```powershell
# 1. 安装依赖
pip install -r requirements.txt

# 2. 设置智谱 API Key（不设则脚本直接退出）
$env:ZHIPU_API_KEY = "你的key"

# 3. 手动运行引擎（可在任意目录下执行）
python apo/apo_loop.py

# 或者：把 apo-optimizer 装进 Crush（引擎需放在 ~/apo），直接对 Crush 说"跑一下 APO"
```

## 文档目录

| 文档 | 内容 |
|---|---|
| [docs/01-架构与工作原理.md](docs/01-架构与工作原理.md) | 插件触发机制、APO 循环的四个角色、评分与控制流细节 |
| [docs/02-安装与运行.md](docs/02-安装与运行.md) | 如何把插件装进 Crush、如何手动运行引擎、依赖与环境变量 |
| [docs/03-配置与注意事项.md](docs/03-配置与注意事项.md) | 各配置文件说明、已知陷阱（务必先读） |

## 当前优化目标

引擎当前优化的对象是一个 **Python 小工具开发助手的系统提示词**（交付优先、
必须给完整可运行代码、兼容 Windows + Termux、防危险操作默认 dry-run），
评测任务见 `apo/test_tasks.yaml`（批量重命名工具、文件统计工具、简易记事本等）。

换优化目标只需改 `test_tasks.yaml` 的任务集和 `best_prompt.md` 的初始提示词，详见配置文档。
