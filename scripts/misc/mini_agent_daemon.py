"""
Mini Desktop Agent Daemon
极简桌面 AI 守护进程：接入 OpenRouter 模型，通过自然语言执行日常系统指令。
支持：
  1. 终端交互模式 (CLI REPL，即敲即用)
  2. 手机局域网 Web 模式 (--serve，支持手机浏览器无感遥控，常驻内存仅 ~30MB)
  3. 通用 Webhook 接口 (/api/webhook，可直接串接飞书/企业微信/Telegram)
"""

import os
import sys
import json
import socket
import argparse
import subprocess
import urllib.request
import urllib.error
from http.server import HTTPServer, BaseHTTPRequestHandler

# 强制标准输入输出使用 UTF-8
if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
KEY_FILE = os.path.join(SCRIPT_DIR, "openrouter.txt")
DEFAULT_MODEL = "deepseek/deepseek-chat"
OPENROUTER_URL = "https://openrouter.ai/api/v1/chat/completions"


def get_api_key():
    """获取 OpenRouter API Key，优先读取 openrouter.txt，其次环境变量"""
    if os.path.exists(KEY_FILE):
        with open(KEY_FILE, "r", encoding="utf-8") as f:
            key = f.read().strip()
            if key:
                return key
    return os.environ.get("OPENROUTER_API_KEY", "").strip()


def get_local_ip():
    """获取本机局域网 IP，供手机访问"""
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"


# 定义供大模型调用的系统执行工具
TOOLS_SCHEMA = [
    {
        "type": "function",
        "function": {
            "name": "run_powershell",
            "description": "在 Windows 宿主机上执行一条 PowerShell 命令并获取其标准输出与错误信息。适用于查询系统状态、进程管理、文件检索、系统设置等日常操作。",
            "parameters": {
                "type": "object",
                "properties": {
                    "command": {
                        "type": "string",
                        "description": "要执行的精确 PowerShell 命令字符串。"
                    }
                },
                "required": ["command"]
            }
        }
    }
]

SYSTEM_PROMPT = """你是一个运行在用户 Windows 10 电脑本地的极简个人桌面管家。
你的职责是协助用户执行日常桌面指令、查询电脑状态、管理文件与进程。
当用户的指令需要与系统交互时（如查看运行时间、查磁盘、管理应用、查网络、查文件等），请优先调用 `run_powershell` 工具执行命令。
规则：
1. 严禁执行具有高危险性破坏指令（如格式化磁盘、无条件清空系统盘关键目录等）。
2. 命令执行完毕后，根据执行结果向用户汇报简洁明了的结论，突出关键信息。
3. 保持回答精炼高效，不要输出冗长的解释。"""


def execute_powershell(command: str) -> str:
    """安全执行本地 PowerShell 指令并捕获输出"""
    # 基础高危拦截
    dangerous_keywords = ["format-volume", "remove-item -recurse c:\\", "rm -r c:\\"]
    cmd_lower = command.lower()
    for kw in dangerous_keywords:
        if kw in cmd_lower:
            return f"[安全拦截] 拒绝执行潜在破坏性指令: {command}"

    # 优先使用 pwsh，若不存在则回退至 powershell
    shell_bin = "pwsh" if subprocess.run("where pwsh", shell=True, capture_output=True).returncode == 0 else "powershell"

    try:
        proc = subprocess.run(
            [shell_bin, "-NoProfile", "-NonInteractive", "-Command", command],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=25
        )
        out = (proc.stdout or "").strip()
        err = (proc.stderr or "").strip()

        result = []
        if out:
            result.append(out)
        if err:
            result.append(f"[STDERR]\n{err}")

        output_str = "\n".join(result) if result else "(命令执行成功，无终端文本输出)"
        # 截断过长输出，防止刷屏
        if len(output_str) > 3000:
            output_str = output_str[:3000] + f"\n... [输出过长，已截断剩余 {len(output_str) - 3000} 字符]"
        return output_str
    except subprocess.TimeoutExpired:
        return "[执行超时] 命令运行超过 25 秒已终止。"
    except Exception as e:
        return f"[执行异常] {str(e)}"


def parse_tool_args(args_raw) -> str:
    """健壮提取 tool_calls 中的 command 字符串"""
    if isinstance(args_raw, str):
        try:
            args_raw = json.loads(args_raw)
        except Exception:
            return args_raw
    if isinstance(args_raw, dict):
        cmd = args_raw.get("command") or args_raw.get("Command") or args_raw.get("cmd") or ""
        if isinstance(cmd, dict):
            return cmd.get("command") or cmd.get("Command") or str(cmd)
        return str(cmd)
    return str(args_raw)


def chat_with_agent(user_message: str, history: list = None, model: str = DEFAULT_MODEL) -> tuple:
    """与 OpenRouter 模型对话并按需自动触发本地工具"""
    api_key = get_api_key()
    if not api_key:
        return "错误：未找到 OpenRouter API Key，请检查 openrouter.txt 文件。", history

    if history is None:
        history = [{"role": "system", "content": SYSTEM_PROMPT}]

    history.append({"role": "user", "content": user_message})

    payload = {
        "model": model,
        "messages": history,
        "tools": TOOLS_SCHEMA,
    }

    headers = {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json",
        "HTTP-Referer": "https://localhost/mini-agent",
        "X-Title": "MiniAgentDaemon"
    }

    try:
        req = urllib.request.Request(
            OPENROUTER_URL,
            data=json.dumps(payload).encode("utf-8"),
            headers=headers,
            method="POST"
        )
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.loads(resp.read().decode("utf-8"))

        choice = data["choices"][0]
        msg = choice["message"]
        tool_calls = msg.get("tool_calls")

        # 如果模型需要调用本地工具
        if tool_calls:
            history.append(msg)
            for tc in tool_calls:
                func_name = tc["function"]["name"]
                if func_name == "run_powershell":
                    cmd_str = parse_tool_args(tc["function"]["arguments"])
                    print(f"  [执行命令] > {cmd_str}")
                    exec_output = execute_powershell(cmd_str)
                    print(f"  [输出摘要] {exec_output[:120].strip()}...")
                    history.append({
                        "role": "tool",
                        "tool_call_id": tc["id"],
                        "name": func_name,
                        "content": exec_output
                    })

            # 把工具执行结果返回给大模型，让大模型生成自然语言汇报
            payload_step2 = {
                "model": model,
                "messages": history
            }
            req2 = urllib.request.Request(
                OPENROUTER_URL,
                data=json.dumps(payload_step2).encode("utf-8"),
                headers=headers,
                method="POST"
            )
            with urllib.request.urlopen(req2, timeout=30) as resp2:
                data2 = json.loads(resp2.read().decode("utf-8"))
            final_content = data2["choices"][0]["message"]["content"]
            history.append({"role": "assistant", "content": final_content})
            return final_content, history
        else:
            final_content = msg.get("content", "")
            history.append({"role": "assistant", "content": final_content})
            return final_content, history

    except urllib.error.HTTPError as e:
        err_body = e.read().decode("utf-8", errors="replace")
        return f"API 请求失败 HTTP {e.code}: {err_body}", history
    except Exception as e:
        return f"发生异常: {str(e)}", history


HTML_PAGE = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no">
<title>桌面助手 · 手机遥控端</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background: #0f172a; color: #f8fafc; height: 100vh; display: flex; flex-direction: column; }
  header { padding: 12px 16px; background: #1e293b; border-bottom: 1px solid #334155; display: flex; justify-content: space-between; align-items: center; }
  header h1 { font-size: 15px; font-weight: 600; color: #38bdf8; }
  header span { font-size: 11px; background: #0284c7; color: white; padding: 2px 8px; border-radius: 12px; }
  #chat { flex: 1; overflow-y: auto; padding: 16px; display: flex; flex-direction: column; gap: 12px; }
  .msg { max-width: 85%; padding: 10px 14px; border-radius: 14px; font-size: 14px; line-height: 1.5; white-space: pre-wrap; word-break: break-word; }
  .user { align-self: flex-end; background: #2563eb; color: white; border-bottom-right-radius: 4px; }
  .bot { align-self: flex-start; background: #1e293b; border: 1px solid #334155; color: #e2e8f0; border-bottom-left-radius: 4px; }
  .bot code { background: #0f172a; padding: 2px 4px; border-radius: 4px; font-family: monospace; font-size: 13px; }
  .loading { font-style: italic; color: #94a3b8; }
  footer { padding: 12px; background: #1e293b; border-top: 1px solid #334155; display: flex; gap: 8px; }
  input { flex: 1; padding: 10px 14px; border-radius: 20px; border: 1px solid #475569; background: #0f172a; color: white; font-size: 14px; outline: none; }
  input:focus { border-color: #38bdf8; }
  button { padding: 10px 18px; border-radius: 20px; border: none; background: #38bdf8; color: #0f172a; font-weight: 600; font-size: 14px; cursor: pointer; }
  button:disabled { opacity: 0.5; }
</style>
</head>
<body>
<header>
  <h1>💻 个人桌面助手</h1>
  <span>在线</span>
</header>
<div id="chat">
  <div class="msg bot">你好！我是常驻在你电脑上的轻量管家。你可以在这里直接发指令，例如：<br>• 查看电脑剩余空间与运行时间<br>• 检查下载目录最新文件<br>• 关停或启动某软件</div>
</div>
<footer>
  <input type="text" id="input" placeholder="输入控制指令..." autocomplete="off">
  <button id="sendBtn" onclick="send()">发送</button>
</footer>
<script>
  const chat = document.getElementById('chat');
  const input = document.getElementById('input');
  const btn = document.getElementById('sendBtn');
  input.addEventListener('keydown', e => { if (e.key === 'Enter') send(); });

  function appendMsg(text, type) {
    const div = document.createElement('div');
    div.className = 'msg ' + type;
    div.textContent = text;
    chat.appendChild(div);
    chat.scrollTop = chat.scrollHeight;
    return div;
  }

  async function send() {
    const text = input.value.trim();
    if (!text) return;
    input.value = '';
    appendMsg(text, 'user');
    btn.disabled = true;
    const loadingDiv = appendMsg('正在思考与执行指令...', 'bot loading');

    try {
      const resp = await fetch('/api/chat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ message: text })
      });
      const data = await resp.json();
      loadingDiv.className = 'msg bot';
      loadingDiv.textContent = data.reply || data.error || '无返回';
    } catch (e) {
      loadingDiv.className = 'msg bot';
      loadingDiv.textContent = '请求失败: ' + e.message;
    } finally {
      btn.disabled = false;
      chat.scrollTop = chat.scrollHeight;
    }
  }
</script>
</body>
</html>"""


class AgentHttpHandler(BaseHTTPRequestHandler):
    chat_history = [{"role": "system", "content": SYSTEM_PROMPT}]

    def do_GET(self):
        if self.path == "/" or self.path.startswith("/index"):
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.end_headers()
            self.wfile.write(HTML_PAGE.encode("utf-8"))
        elif self.path == "/health":
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"status": "ok"}).encode("utf-8"))
        else:
            self.send_response(404)
            self.end_headers()

    def do_POST(self):
        if self.path in ["/api/chat", "/api/webhook"]:
            content_len = int(self.headers.get("Content-Length", 0))
            post_body = self.rfile.read(content_len).decode("utf-8")
            try:
                req_data = json.loads(post_body)
                user_msg = req_data.get("message", "").strip()
                if not user_msg:
                    reply = "请输入有效指令。"
                else:
                    reply, self.chat_history = chat_with_agent(
                        user_msg,
                        self.chat_history,
                        model=self.server.model_name
                    )
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.end_headers()
                self.wfile.write(json.dumps({"reply": reply}, ensure_ascii=False).encode("utf-8"))
            except Exception as e:
                self.send_response(500)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.end_headers()
                self.wfile.write(json.dumps({"error": str(e)}).encode("utf-8"))
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, format, *args):
        # 简化日志，只输出 API 请求
        if "/api/" in args[0]:
            sys.stdout.write(f"[{self.log_date_time_string()}] {format % args}\n")


def run_server(port: int = 8765, model: str = DEFAULT_MODEL):
    local_ip = get_local_ip()
    server_address = ("0.0.0.0", port)
    httpd = HTTPServer(server_address, AgentHttpHandler)
    httpd.model_name = model

    print("=================================================================")
    print(" 🚀 极简桌面 AI 守护进程已启动 (纯标准库，无 GUI，极低内存)")
    print(f" • 选用模型: {model}")
    print(f" • 本机控制页: http://127.0.0.1:{port}")
    print(f" • 手机遥控页: http://{local_ip}:{port} (同一局域网手机浏览器直接打开)")
    print(f" • 通用 Webhook: http://{local_ip}:{port}/api/webhook")
    print(" • 按 Ctrl + C 即可退出服务")
    print("=================================================================")

    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n服务已停止。")


def main():
    parser = argparse.ArgumentParser(description="极简桌面 AI 守护进程")
    parser.add_argument("-q", "--query", type=str, help="直接执行单次指令并返回结果")
    parser.add_argument("-s", "--serve", action="store_true", help="启动手机遥控/Webhook 守护服务")
    parser.add_argument("-p", "--port", type=int, default=8765, help="守护服务监听端口 (默认 8765)")
    parser.add_argument("-m", "--model", type=str, default=DEFAULT_MODEL, help=f"指定模型 (默认 {DEFAULT_MODEL})")
    args = parser.parse_args()

    api_key = get_api_key()
    if not api_key:
        print(f"[错误] 未检测到 API Key，请在 {KEY_FILE} 中写入您的 OpenRouter Key。")
        sys.exit(1)

    if args.serve:
        run_server(port=args.port, model=args.model)
    elif args.query:
        print(f"用户指令: {args.query}")
        reply, _ = chat_with_agent(args.query, model=args.model)
        print(f"\n助手回复:\n{reply}")
    else:
        # 默认进入终端交互 REPL
        print("=================================================================")
        print(" 💻 极简桌面管家 · 终端交互模式 (输入 exit 或 quit 退出)")
        print(f" • 当前模型: {args.model}")
        print(" • 若想启动手机端遥控服务，请运行: python mini_agent_daemon.py --serve")
        print("=================================================================")
        history = None
        while True:
            try:
                user_input = input("\n[你] > ").strip()
                if not user_input:
                    continue
                if user_input.lower() in ["exit", "quit"]:
                    break
                reply, history = chat_with_agent(user_input, history, model=args.model)
                print(f"[管家] > {reply}")
            except (KeyboardInterrupt, EOFError):
                break
        print("\n已退出。")


if __name__ == "__main__":
    main()
