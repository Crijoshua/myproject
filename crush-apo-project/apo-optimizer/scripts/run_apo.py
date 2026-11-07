import subprocess
import sys
import os
import datetime

# Windows 控制台默认 GBK，打印 emoji/中文会触发 UnicodeEncodeError，统一重配为 UTF-8
if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

def main():
    home_dir = os.path.expanduser("~")
    apo_script_path = os.path.join(home_dir, "apo", "apo_loop.py")

    if not os.path.exists(apo_script_path):
        print(f"错误: 找不到核心脚本 {apo_script_path}")
        sys.exit(1)

    # 准备日志文件
    log_dir = os.path.join(home_dir, "apo", "logs")
    os.makedirs(log_dir, exist_ok=True)
    timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    log_file_path = os.path.join(log_dir, f"crush_bg_{timestamp}.log")

    # 将输出写入日志文件，并且不阻塞界面
    with open(log_file_path, "w", encoding="utf-8") as f:
        subprocess.Popen(
            [sys.executable, apo_script_path],
            stdout=f,
            stderr=subprocess.STDOUT,
            start_new_session=True,  # POSIX 下脱离当前会话，避免随父进程退出被杀；Windows 下忽略
        )

    # 立刻打印这句话给 Crush
    print("✅ APO 优化任务已成功在后台启动！")
    print(f"📄 运行日志将保存到: {log_file_path}")
    print("⏳ 由于大模型调用耗时较长（约需 1-3 分钟），请在几分钟后去查看 `apo/best_prompt.md` 获取最新结果。")

if __name__ == "__main__":
    main()
