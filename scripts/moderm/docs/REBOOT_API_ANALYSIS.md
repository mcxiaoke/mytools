# 中国移动光猫 (华为 HN8546X6N-20) 接口分析与重启方案

## 1. 设备与环境概况

- **设备型号**：华为 EchoLife HN8546X6N-20（中国移动 10G-GPON 定制智能网关）
- **管理地址**：`http://192.168.3.1/`
- **账号权限**：普通用户 (`user`)
- **密码传递**：通过环境变量 `MODERM_PASS` 或命令行参数 `--password` 传入
- **后台架构**：ASP/CGI 混合架构，基于 Token 和 Session Cookie 防重放及 CSRF

---

## 2. 核心接口与通信时序

整个重启过程分为三个关键阶段：**获取登录Token -> 登录建连 -> 获取页面onttoken -> 发送重启指令**。

### 阶段一：获取登录随机 Token
- **请求方式**：`POST http://192.168.3.1/asp/GetRandCount.asp`
- **请求体**：空
- **响应内容**：返回一串随机十六进制哈希字符串（例如 `863e3286f675878...`，注意开头可能带 UTF-8 BOM `\ufeff`）。

### 阶段二：表单登录建立 Session
- **请求方式**：`POST http://192.168.3.1/login.cgi`
- **请求头**：
  - `Cookie: Cookie=body:Language:chinese:id=-1;path=/`
  - `Referer: http://192.168.3.1/`
  - `Content-Type: application/x-www-form-urlencoded`
- **请求体（表单数据）**：
  ```
  UserName=user
  PassWord=<Base64编码后的密码>
  x.X_HW_Token=<阶段一获取的随机Token>
  ```
- **服务端响应**：
  - 成功时返回 200，并下发 Session Cookie：
    `Set-Cookie: CookieHttp=sid=<session_id>:Language:chinese:id=1; path=/; HttpOnly`

### 阶段三：获取重启页面专属 onttoken
- **请求方式**：`GET http://192.168.3.1/html/ssmp/devmanage/cmccdevicereset.asp`
- **请求头**：携带上一步获得的 `Cookie` 和 `CookieHttp`
- **页面内容提取**：
  HTML 中包含一个隐藏的表单项：
  ```html
  <input type="hidden" name="onttoken" id="hwonttoken" value="8a1d89ffb117c0afc2912270bfe9f09860037ff643b62a5153f312390205103d">
  ```
  该 `onttoken` 是执行重启等敏感操作时必须携带的一次性校验值。

### 阶段四：触发设备重启
- **请求方式**：`POST http://192.168.3.1/html/ssmp/devmanage/set.cgi?x=InternetGatewayDevice.X_HW_DEBUG.SMP.DM.ResetBoard&RequestFile=html/ssmp/devmanage/cmccdevicereset.asp`
- **请求头**：
  - `Referer: http://192.168.3.1/html/ssmp/devmanage/cmccdevicereset.asp`
  - `Content-Type: application/x-www-form-urlencoded`
- **请求体**：
  ```
  x.X_HW_Token=<阶段三提取的 onttoken>
  ```
- **执行效果**：网关接收到此命令后会立即断开连接并进入硬件重启流程，耗时约 1~2 分钟后恢复。

---

## 3. 脚本工具与使用方法

本项目提供两种脚本方案，均支持从环境变量 `MODERM_PASS` 读取密码：

### 方案 A：轻量无依赖脚本 `reboot_modem.py`（推荐用于定时任务）
基于 Python 标准库（`urllib` + `http.cookiejar`），**不需要安装任何第三方包（0.2秒完成）**：

```bash
# 方式 1：设置环境变量
export MODERM_PASS="your_password"
python reboot_modem.py --check
python reboot_modem.py --reboot

# 方式 2：参数直接传入
python reboot_modem.py --password "your_password" --check
python reboot_modem.py --password "your_password" --reboot

# 方式 3：后台循环定时重启（例如每天凌晨 04:30）
python reboot_modem.py --daily-at 04:30
```

### 方案 B：Playwright 浏览器自动化脚本 `reboot_modem_playwright.py`
使用 Edge/Chromium 浏览器模拟真实点击与对话框确认：
```bash
python reboot_modem_playwright.py --dry-run
python reboot_modem_playwright.py --reboot
```

---

## 4. 远程部署与定时任务说明

脚本已部署至目标主机：
- 脚本位置：`/etc/scripts/reboot-modem.py`
- 定时配置：`0 4 * * * /usr/bin/python3 /etc/scripts/reboot-modem.py --reboot >> /var/log/reboot-modem.log 2>&1`
- 日志归档：`/etc/logrotate.d/reboot-modem`
