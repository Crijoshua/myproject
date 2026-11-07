import sys
import os

# stdout/stderr 统一重配为 UTF-8：前者保证控制台中文提示可打印，
# 后者保证 LLM 调用失败的 traceback（可能含服务端返回的中文错误）写入日志时不混入 GBK 字节
for _stream in (sys.stdout, sys.stderr):
    if _stream.encoding and _stream.encoding.lower() != "utf-8":
        try:
            _stream.reconfigure(encoding="utf-8")
        except Exception:
            pass

import yaml
import json
import datetime
import pathlib
import shutil
from openai import OpenAI

# 引擎自身所在目录：所有相对路径都以它为基准，在任意工作目录下运行都能正确定位文件
SCRIPT_DIR = pathlib.Path(__file__).resolve().parent

API_KEY = os.environ.get("ZHIPU_API_KEY")
if not API_KEY:
    print("错误：未找到 ZHIPU_API_KEY 环境变量！")
    sys.exit(1)

client = OpenAI(api_key=API_KEY, base_url="https://open.bigmodel.cn/api/paas/v4/")
MODEL_NAME = "GLM-5.3-Flash"

call_counter = {"n": 0}
class BudgetExceeded(Exception): pass

def load_yaml(path):
    # utf-8-sig：同时兼容带与不带 BOM 的 YAML 文件
    with open(path, "r", encoding="utf-8-sig") as f: return yaml.safe_load(f)

def check_budget(config):
    if call_counter["n"] >= config["max_llm_calls"]: raise BudgetExceeded("上限")
    call_counter["n"] += 1

def strip_code_fence(text):
    """剥掉 LLM 误输出的包装性 Markdown 代码围栏，避免围栏混入提示词文件。
    仅处理首行是围栏的情况：恰好 2 个围栏行时第二个必为闭合包装（连同剥离）；
    3 个及以上时只剥首尾，保护提示词内部合法的代码示例围栏。"""
    t = text.strip()
    if not t.startswith("```"): return t
    lines = t.splitlines()
    fence_idx = [i for i, ln in enumerate(lines) if ln.strip().startswith("```")]
    if len(fence_idx) == 2:
        # 首行开围栏 + 唯一的闭合围栏：两个都删，保留其余全部内容
        keep = [ln for i, ln in enumerate(lines) if i not in fence_idx]
        return "\n".join(keep).strip()
    # 嵌套围栏情形：只剥首行，末行若恰为围栏也剥掉
    if lines and lines[-1].strip().startswith("```"):
        lines = lines[:-1]
    return "\n".join(lines[1:]).strip()

def run_agent_with_prompt(prompt, task, config):
    check_budget(config)
    print(f"  [正在生成代码] 任务: {task['id']} ...")
    response = client.chat.completions.create(
        model=MODEL_NAME,
        messages=[{"role": "system", "content": prompt}, {"role": "user", "content": task['requirement']}],
        temperature=0.7
    )
    return response.choices[0].message.content

def hard_check(output):
    if "删除系统目录" in output or "格式化磁盘" in output or "rm -rf /" in output: return {"fatal": True, "details": "危险"}
    return {"fatal": False, "details": "OK"}

def llm_judge(output, task, acceptance_criteria, config):
    check_budget(config)
    print(f"  [正在打分] 任务: {task['id']} ...")
    # 权重文案从 config.yaml 动态生成，保证与实际加权逻辑一致
    w = config["weights"]
    weights_text = f"启动{w['startup']:.0%}，功能{w['requirement_match']:.0%}，交付{w['delivery_completeness']:.0%}"
    judge_prompt = f"""你是 Python 程序评分员。根据以下信息给候选程序打分。
【任务需求】{task['requirement']}
【程序输出】{output}
【评分项与权重】{weights_text}。出现危险操作一票否决。
只输出严格的 JSON，不要 Markdown 标记：
{{"startup": 0-10, "requirement_match": 0-10, "delivery_completeness": 0-10, "safety_veto": false, "reason": "简短理由"}}"""
    response = client.chat.completions.create(
        model=MODEL_NAME,
        messages=[{"role": "user", "content": judge_prompt}],
        temperature=0.1,
        response_format={"type": "json_object"}
    )
    try:
        data = json.loads(response.choices[0].message.content)
        # 字段兜底 + 统一转 float，防止模型返回缺字段或字符串数字导致崩溃
        return {
            "startup": float(data.get("startup", 0)),
            "requirement_match": float(data.get("requirement_match", 0)),
            "delivery_completeness": float(data.get("delivery_completeness", 0)),
            "safety_veto": bool(data.get("safety_veto", False)),
            "reason": str(data.get("reason", "")),
        }
    except Exception: return {"startup": 0, "requirement_match": 0, "delivery_completeness": 0, "safety_veto": True, "reason": "解析失败"}

def compute_weighted_score(hard, soft, weights):
    if hard["fatal"] or soft.get("safety_veto"): return 0
    return (soft["startup"] * weights["startup"] + soft["requirement_match"] * weights["requirement_match"] + soft["delivery_completeness"] * weights["delivery_completeness"]) / 10 * 100

def optimize_prompt(best_prompt, failures, config):
    check_budget(config)
    print(f"  [正在优化提示词] ...")
    opt_prompt = f"""你是提示词优化器。根据当前提示词和失败摘要，改写提示词。
【当前提示词】{best_prompt}
【失败摘要】{failures}
【要求】1.只改提示词。2.针对失败原因修正。3.输出完整新版，不要解释。"""
    response = client.chat.completions.create(model=MODEL_NAME, messages=[{"role": "user", "content": opt_prompt}], temperature=0.7)
    return strip_code_fence(response.choices[0].message.content)

def evaluate_prompt(prompt, tasks, config, acceptance_criteria):
    results = []
    for task in tasks:
        output = run_agent_with_prompt(prompt, task, config)
        hard = hard_check(output)
        if hard["fatal"]:
            score = 0; soft = {"safety_veto": True, "reason": hard["details"]}
        else:
            soft = llm_judge(output, task, acceptance_criteria, config)
            score = compute_weighted_score(hard, soft, config["weights"])
        results.append({"task": task, "score": score, "output": output, "hard": hard, "soft": soft})
    return results

def backup_before_round(round_no, config):
    timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    backup_dir = pathlib.Path(SCRIPT_DIR / config["paths"]["backups_dir"]) / f"round_{round_no:02d}_{timestamp}"
    backup_dir.mkdir(parents=True, exist_ok=True)
    best = pathlib.Path(SCRIPT_DIR / config["paths"]["best_prompt"])
    if best.exists(): shutil.copy2(best, backup_dir / "best_prompt.md")
    shutil.copy2(SCRIPT_DIR / "config.yaml", backup_dir / "config.yaml")

def save_best(prompt, score, config):
    pathlib.Path(SCRIPT_DIR / config["paths"]["best_prompt"]).write_text(prompt, encoding="utf-8")

def log_round(round_no, prompt, avg, results, config):
    logs_dir = pathlib.Path(SCRIPT_DIR / config["paths"]["logs_dir"])
    logs_dir.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    log_file = logs_dir / f"round_{round_no}_{timestamp}.log"
    with open(log_file, "w", encoding="utf-8") as f:
        f.write(f"Round: {round_no}\nTime: {timestamp}\n")
        if avg is not None: f.write(f"Average score: {avg:.2f}\n")
        f.write(f"Prompt:\n{prompt}\n\nResults:\n")
        for r in results: f.write(f"  Task: {r['task']['id']}, Score: {r['score']:.2f}\n")

def summarize_failures(results):
    fails = [f"任务 {r['task']['id']} 分数低：{r['soft'].get('reason')}" for r in results if r["score"] < 70]
    return "\n".join(fails) if fails else "无明显失败"

def main():
    config = load_yaml(SCRIPT_DIR / "config.yaml")
    tasks_cfg = load_yaml(SCRIPT_DIR / "test_tasks.yaml")
    acceptance_criteria = load_yaml(SCRIPT_DIR / "acceptance_criteria.yaml")
    best_path = pathlib.Path(SCRIPT_DIR / config["paths"]["best_prompt"])
    if not best_path.exists(): best_path.write_text("你是一个 Python 助手。", encoding="utf-8")
    best_prompt = strip_code_fence(best_path.read_text(encoding="utf-8-sig"))
    candidate_prompt = best_prompt
    best_score = -1
    try:
        for round_no in range(config["max_rounds"]):
            print(f"\n--- 第 {round_no + 1} 轮开始 ---")
            backup_before_round(round_no, config)
            results = evaluate_prompt(candidate_prompt, tasks_cfg["dev_tasks"], config, acceptance_criteria)
            avg = sum(r["score"] for r in results) / len(results)
            failures = summarize_failures(results)
            log_round(round_no, candidate_prompt, avg, results, config)
            print(f"本轮平均分: {avg:.2f}")
            if avg > best_score:
                best_score = avg
                best_prompt = candidate_prompt
                save_best(best_prompt, best_score, config)
                print("-> 已更新最佳提示词")
            else: print("-> 未超过当前最佳，保留原版")
            if round_no < config["max_rounds"] - 1: candidate_prompt = optimize_prompt(best_prompt, failures, config)
        print("\n--- 留出集验收 ---")
        holdout_results = evaluate_prompt(best_prompt, tasks_cfg["holdout_tasks"], config, acceptance_criteria)
        print("=" * 50 + "\nAPO 真实运行完成\n" + "=" * 50)
        print(f"最佳开发集分数: {best_score:.2f}")
        for r in holdout_results: print(f"  {r['task']['id']}: {r['score']:.2f} 分, 评语: {r['soft'].get('reason')}")
    except BudgetExceeded as exc: print(f"\n已停止：{exc}")

if __name__ == "__main__": main()
