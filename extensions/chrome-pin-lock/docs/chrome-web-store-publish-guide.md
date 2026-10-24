# Chrome Web Store 发布合规审查与提审指南 (PIN Site Lock)

> 整理日期：2026-10-22  
> 项目目录：`C:\Home\Projects\mytools\extensions\chrome-pin-lock`  
> 适用标准：Chrome Web Store 开发者政策、Manifest V3 审核规范、欧盟 DSA 合规要求

---

## 1. 总体审查与发布就绪度评估

### 1.1 技术架构（已合规）
* **Manifest V3**：已完全基于 MV3 开发，Service Worker 规范注册（`"type": "module"`）。
* **代码安全性**：纯本地离线运行，使用 Web Crypto API 进行 PBKDF2 哈希加密，无 `eval`、无动态执行代码、无外部远程脚本引入，符合 Google 严格的 CSP 安全要求。
* **图标资源**：`icons/` 目录下已具备 16×16、48×48、128×128 像素的标准 PNG 图标。

### 1.2 发布阻断项（直接提交会被拒审）
1. **交互缺陷（必拒）**：`manifest.json` 声明了 `"action"` 但未指定 `default_popup`，同时 `background.js` 中没有监听 `chrome.action.onClicked`。审核员点击浏览器扩展图标无任何响应，会以 **“功能不完整 / 按钮无效（Broken Functionality）”** 拒审。
2. **缺失隐私政策（强制项）**：由于声明了 `<all_urls>` 和 `tabs` 敏感权限，商店后台强制要求提供公开的 **Privacy Policy URL**，未提供则无法提交审核。
3. **缺少运行截图（强制项）**：后台提交必须包含至少 1 张合规尺寸（**1280×800** 或 **640×400**）的真实界面截图。

---

## 2. 需调整的代码与配置

### 2.1 修复工具栏图标点击事件
在 `background.js` 中增加 `chrome.action.onClicked` 监听，点击扩展图标直接打开设置页：

```javascript
// 点击扩展工具栏图标打开设置页
chrome.action.onClicked.addListener(() => {
  chrome.runtime.openOptionsPage();
});
```

---

## 3. 商店提审权限说明（Permissions Justification）

Chrome Web Store 后台对敏感权限要求逐项填写英文说明，请直接复制以下经过合规校对的文案：

| 权限字段 | 权限类型 | 提交后台的英文理由（Justification） |
| :--- | :--- | :--- |
| **`<all_urls>`** | `host_permissions` | The extension allows users to define custom protected domains, IP addresses, and URL prefixes to be locked behind a PIN code. To detect navigations to these arbitrary, user-specified websites via webNavigation and redirect them to the local lock verification page, broad host permissions are required. No web content is injected, modified, read, or transmitted off-device. |
| **`webNavigation`** | `permissions` | Required to intercept top-level frame navigation events before sensitive pages render, ensuring protected sites are redirected to the PIN authentication screen immediately. |
| **`tabs`** | `permissions` | Required to query opened tabs and update their URLs to the lock screen when the idle timer expires or when the system is locked. |
| **`storage`** | `permissions` | Used to persist user-configured protected site lists, PBKDF2-hashed PIN credentials locally, and maintain temporary session unlock states. |
| **`idle`** | `permissions` | Used to detect when the user is away from the device or locks the operating system, automatically re-locking previously unlocked protected tabs. |

---

## 4. 商店基本信息与文案草稿

### 4.1 基础信息
* **Extension Name（名称）**：`PIN Site Lock`（与 `manifest.json` 保持一致）
* **Category（分类）**：`Productivity`（生产力工具）或 `Developer Tools`
* **Single Purpose（单一用途 - 审核员必看）**：
  > Protects specified domains and IP addresses by requiring a PIN code before access.

### 4.2 简短描述（Short Description，≤ 132 字符）
```text
访问指定域名或 IP 前需输入 PIN 码，保护敏感页面，支持空闲自动锁定。
```

### 4.3 详细描述（Detailed Description，用于商店展示，禁止出现底层实现代码术语）
```text
PIN Site Lock 是一款轻量、安全的浏览器本地门禁工具。当你借用电脑给他人或在公共场所离开电脑时，可防止他人直接查看或访问敏感管理后台与内网站点。

主要功能：
• 访问拦截：自定义受保护的域名、IP 地址（支持端口）或特定 URL 前缀，访问前需输入 PIN 码解锁。
• 会话保持：在当前浏览器会话中解锁后无缝浏览，关闭浏览器后自动恢复锁定状态。
• 空闲自动锁：支持设置静置超时时间（1~240 分钟），系统锁屏或超时未操作时自动锁定所有受保护页面。
• 防暴力破解：连续输错 5 次 PIN 码后自动静默锁定 30 秒。

隐私与安全承诺：
• 纯本地运行：所有配置和加密后的密码凭据仅保存在您的本地浏览器中，绝不上传到任何远程服务器。
• 零数据收集：本扩展不包含任何统计分析、不追踪任何浏览历史、不展示任何广告。

使用方法：
1. 安装后点击工具栏图标进入设置页；
2. 设置您的管理 PIN 码；
3. 添加需要锁定的域名或 IP 地址；
4. 之后访问对应网站时，将会自动跳转到 PIN 输入界面。
```

---

## 5. 隐私政策模板（Privacy Policy）

因声明了 `<all_urls>`，必须在公网托管一份隐私政策并向商店提供 URL（可发布到 GitHub Pages、GitHub Gist、Notion 或个人博客）：

```markdown
# Privacy Policy for PIN Site Lock

Last updated: 2026-10-22

PIN Site Lock ("we", "our", or "the extension") is committed to protecting your privacy. This Privacy Policy explains how our Chrome extension handles user information.

## 1. No Data Collection or Transmission
PIN Site Lock does NOT collect, store, transmit, or share any personal data, sensitive information, or browsing activity to any external servers or third-party entities.

## 2. Local Storage Usage
All information configured within the extension—including user-defined protected domain lists, session unlock statuses, idle timeout settings, and PBKDF2 cryptographic hashes of your PIN—is stored exclusively on your local device using standard browser storage (`chrome.storage.local` and `chrome.storage.session`). We do not have access to your PIN or stored configuration.

## 3. Host Permissions and Website Access
The extension requests `<all_urls>` and `webNavigation` permissions solely to detect navigations to user-specified protected websites and redirect them to the local PIN unlock interface. The extension does not read, log, alter, or monetize the contents of any websites you visit.

## 4. Third-Party Services
The extension does not use any third-party tracking, analytics, advertising networks, or external cloud services.

## 5. Changes to This Policy
We may update this Privacy Policy from time to time. Any changes will be reflected with a revised date at the top of this document.

## 6. Contact
If you have any questions or feedback regarding this policy, please open an issue on our project repository.
```

---

## 6. 数据披露表单填写指引（Data Usage Disclosure）

在 Chrome Web Store 开发者后台的 **“Privacy practices / 隐私实践”** 页面：
1. **Single Purpose**：填入上述单一用途说明。
2. **Permission Justification**：填入上述各权限的理由。
3. **Data Usage Checkboxes（数据使用勾选）**：
   * “Does your extension collect user data?” 👉 勾选 **No（否）**。
4. **Certification（合规承诺）**：勾选以下三项：
   * 不得出售用户数据给第三方；
   * 不得将数据用于信贷、借贷或信用评估；
   * 不得将数据用于个性化广告投放。

---

## 7. 纯净发布包打包（ZIP Packaging）

**严禁直接全量压缩项目目录**，否则会将 `temp/`（包含多个备份文件）和 `docs/`（包含设计文档）打入安装包，增加人工审查被拒风险。

### 7.1 打包文件白名单
```
chrome-pin-lock.zip
├── manifest.json
├── background.js
├── lib.js
├── lock.html
├── lock.js
├── options.html
├── options.js
└── icons/
    ├── icon16.png
    ├── icon48.png
    └── icon128.png
```

### 7.2 PowerShell 打包脚本示例
在项目根目录下执行：
```powershell
Compress-Archive -Path manifest.json, background.js, lib.js, lock.html, lock.js, options.html, options.js, icons -DestinationPath temp/chrome-pin-lock-v1.0.0.zip -Force
```
生成的 `temp/chrome-pin-lock-v1.0.0.zip` 即为最终上传给 Chrome 开发者中心的发布包。
