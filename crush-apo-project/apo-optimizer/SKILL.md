---
name: apo-optimizer
description: 仅当用户明确说出“跑一下 APO”“优化提示词”“自动优化提示词”时激活。
---

# APO 自动提示词优化器

## 触发条件
仅当用户明确要求“跑 APO / 优化提示词 / 自动优化提示词”时激活。日常写代码时绝对不要触发。

## 执行步骤
1. 向用户展示一句话：“正在为您启动 APO 后台进化任务...”。
2. 调用 `python $HOME/.config/crush/skills/apo-optimizer/scripts/run_apo.py`。
3. 该脚本会瞬间返回结果，告诉你任务已在后台运行。
4. 立即结束当前操作，告诉用户：“优化任务已在后台启动，请等待 1-3 分钟后，自行查看 `apo/best_prompt.md` 中的结果。现在我们可以继续聊别的。”
