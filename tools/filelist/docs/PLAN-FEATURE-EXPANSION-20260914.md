# FileList 进阶功能扩展与安全架构设计方案

> **版本**: v1.0 | **日期**: 2026-09-14 | **状态**: 规划中 / 待实施

---

## 1. 背景与核心设计哲学

FileList 作为轻量级文件目录索引与搜索 Web 服务，核心定位是：**极轻量单二进制（~7MB）、无外部数据库、超大目录内存增量索引毫秒级秒搜、开箱即用**。

在与常见全功能文件管理器（如 FileBrowser）对比时，用户在日常使用（家庭 NAS、内网运维、文件分发与数据接收）中，最常需要的功能主要集中在：
1. **富媒体查看**：不再局限于图片，扩展到音视频流式播放。
2. **轻量运维编辑**：文本/配置文件直接查看与轻量修改保存。
3. **高频便捷操作**：目录打包 ZIP 一键下载、新建文件夹、一键复制直链与手机扫码。
4. **安全受控管理**：支持重命名与**高度安全防护的删除操作**（针对高风险操作引入**独立删除口令 `deleteToken`** 强校验）。

### 坚守的设计红线：
- **零外部大型依赖**：严禁引入几十兆的复杂富文本/代码编辑器（如 Monaco/CodeMirror），保持纯原生前端与 Go 标准库实现。
- **无临时磁盘开销**：ZIP 压缩必须全程内存流式透传，严禁在服务端磁盘生成巨大临时压缩包打爆磁盘。
- **原子落盘与极度安全**：文本保存采用原子替换机制，删除操作引入独立口令防御，严防误删与文件损坏。

---

## 2. 功能全景矩阵

| 模块 | 功能名称 | 前端实现方式 | 后端实现方式 | 额外依赖 | 复杂度 |
| :--- | :--- | :--- | :--- | :--- | :---: |
| **一** | **音视频在线播放弹窗** | 原生 HTML5 `<video>` / `<audio>` 播放弹窗 | **零改动**（原生复用既有 HTTP 206 Range 拖拽串流） | 无 | 🟢 极低 |
| **二** | **文本预览与轻量在线编辑** | 原生等宽 `<textarea>` + `Ctrl+S` + 脏标记防误关 | `PUT /api/content`，临时文件 + `os.Rename` 原子写盘 | 无 | 🟢 低 |
| **三** | **新建文件夹（mkdir）** | 工具栏按钮 + 弹窗输入 | `POST /api/mkdir`，`sanitizeFilename` 清洗 | 无 | 🟢 极低 |
| **四** | **直链复制与手机扫码** | `navigator.clipboard` + 嵌入式 SVG 二维码生成 | **零改动** | 极小 SVG QR 库 (~8KB) | 🟢 极低 |
| **五** | **目录流式打包 ZIP 下载** | 工具栏/操作列“打包下载”按钮 | Go `archive/zip` 边压缩边流式输出，**0 临时磁盘占用** | 无（标准库） | 🟢 低 |
| **六** | **文件重命名** | 操作列按钮 + 名称输入 Dialog | `POST /api/rename`，原子 `os.Rename` + 目标防覆盖 | 无 | 🟢 极低 |
| **七** | **独立口令保护的安全删除** | 红色危险操作 Dialog，**强制要求输入删除口令** | `POST /api/delete`，`subtle.ConstantTimeCompare` 强校验 | 无 | 🟡 低 |

---

## 3. 各模块详细设计方案

### 模块一：音频与视频在线播放弹窗（Audio & Video Player）

#### 1. 业务流程与 UI 交互
- **格式识别**：
  - 视频扩展名：`.mp4`, `.webm`, `.ogv`, `.m4v`, `.mov`
  - 音频扩展名：`.mp3`, `.wav`, `.ogg`, `.flac`, `.aac`, `.m4a`
- **播放交互**：
  - 列表模式下：点击音视频名称或右侧播放图标，唤起播放弹窗；
  - 网格模式下：音视频卡片呈现专用播放徽章，点击直接唤起播放弹窗；
  - 弹窗顶部栏：显示文件名、大小、新标签页打开与原文件下载按钮；
  - 弹窗主体：
    - 视频：自适应大屏居中 `<video controls autoplay playsinline>`，最大高度 `80vh`；
    - 音频：精美居中声波/黑胶封面卡片 + 宽屏 `<audio controls autoplay>`；
  - 快捷操作：`Esc` 键退出播放，点击背景空白退出，弹窗关闭时自动暂停音视频，释放网络连接。

#### 2. 后端机制
- **零改动**：FileList 的 `/raw/...` 端点底层为 Go `http.ServeFile`，天然支持：
  1. 标准 MIME Type 嗅探与正确标头（`video/mp4`, `audio/mpeg` 等）；
  2. **HTTP 206 Partial Content（Range 请求）**：用户在播放器中任意拖拽进度条 Seek，浏览器自动发送 `Range: bytes=xxx-xxx`，服务端仅传输对应分块，不占满带宽，实现秒播。

---

### 模块二：文本/代码预览与轻量在线编辑保存（Text Viewer & Editor）

#### 1. 前端设计（原生轻快编辑器）
- **适用格式**：`.txt`, `.md`, `.log`, `.yaml`, `.yml`, `.json`, `.xml`, `.sh`, `.bat`, `.ps1`, `.py`, `.go`, `.js`, `.css`, `.html`, `.conf`, `.ini`, `.env` 等纯文本文件。
- **界面与体验**：
  - 弹窗最大化沉浸编辑视口（宽 90vw，高 85vh）；
  - 工具栏：文件名、当前行数/字符数、只读/编辑模式切换开关、「💾 保存 (Ctrl+S)」按钮、右上角关闭；
  - 核心控件：原生高质感 `<textarea>`，配置系统等宽字体栈：
    `ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", monospace`；
  - 快捷交互：
    - 拦截 `Tab` 键：自动插入 2 个或 4 个空格，防止光标焦点跳出输入框；
    - 拦截 `Ctrl+S` / `Cmd+S`：直接触发保存，并在工具栏呈现“保存中...”与绿字“已保存”动画反馈；
    - 脏数据检测（Dirty Checking）：若内容被修改且未保存，用户尝试关闭弹窗或刷新页面时，弹出浏览器原生确认，防止误触导致劳动成果丢失。

#### 2. 后端设计与原子写入
- **接口**：`PUT /api/content?path=/data/config.yaml`
- **请求体**：原始纯文本数据（`Content-Type: text/plain; charset=utf-8`）
- **安全检查**：
  1. 权限检查：检查 `manage.allowEdit == true`；
  2. 敏感规则：受 `index.excludeFiles` 保护，禁止修改越界或黑名单文件；
  3. 目标必须存在且为常规文本文件（大小限制默认 <= 5MB，防止编辑超大日志文件导致浏览器卡死）。
- **原子写盘（Atomic Save）**：
  ```go
  // 1. 写入同一目录下的临时文件，保证在同一物理文件系统
  tmpPath := realPath + ".tmp." + randomString(8)
  err := os.WriteFile(tmpPath, data, 0644)
  // 2. 通过原子重命名替换原有文件
  err = os.Rename(tmpPath, realPath)
  ```
  *收益：即便写入中途断电或网络超时，也不会损坏破坏原始文件（避免文件内容被清空成 0 字节）。*

---

### 模块三：新建文件夹（Make Directory）

#### 1. 业务流程与交互
- 工具栏在“上传文件”按钮右侧提供「📁 新建文件夹」按钮（仅在具体目录下展示，根目录挂载点视图隐藏）。
- 点击弹出输入模态框：输入文件夹名称，回车或点击确认创建，`Esc` 取消。

#### 2. 后端接口
- **接口**：`POST /api/mkdir?path=/data/photos&name=2026`
- **防护与清洗**：
  1. 权限检查：`manage.allowMkdir == true`；
  2. 使用 `sanitizeFilename(name)` 清洗非法路径分隔符与非法字符，规避 Windows 保留设备名；
  3. 路径校验：目标必须在已配置的 Root 物理目录内；
  4. 冲突处理：若目标目录已存在，返回 `409 Conflict` 友好提示；
  5. 成功后执行 `os.Mkdir(targetPath, 0755)`，并通知前端静默刷新当前目录。

---

### 模块四：外链一键复制与手机扫码下载（Copy Link & Mobile QR Code）

#### 1. 业务场景
- 电脑端用户需要将文件直链快速发给他人，或者通过手机相机扫码直接在移动端浏览器下载/播放。

#### 2. 前端设计
- 在每行文件的操作列增加「📋 复制链接」和「📱 扫码」小按钮。
- **复制链接**：
  - 调用原生 `navigator.clipboard.writeText(...)`；
  - 自动将相对路径与当前 `window.location.origin` 及 `basePath` 拼接为完整的公网/局域网绝对 URL；
  - 按钮临时变为绿底对勾“已复制”，2 秒后自动复原。
- **手机扫码二维码**：
  - 点击弹出居中小卡片，展示清晰二维码与文件名；
  - 引入零网络依赖的嵌入式极简 SVG QR 渲染库（仅 8KB，打包进 `web/static/`，无任何外部 CDN 依赖）；
  - 手机扫码后直接调用浏览器系统内核下载。

---

### 模块五：目录一键打包 ZIP 流式下载（Streaming Folder as Zip）

#### 1. 业务痛点与普通做法的缺陷
- 普通做法：先在服务端磁盘遍历目录，生成一个压缩包 `.zip` 临时文件，生成完毕后再发给用户，下载完再删。
- **缺陷**：大目录会吃光服务器磁盘，若用户中途取消则留下无用垃圾大文件；响应延迟极高，用户需要干等数分钟。

#### 2. FileList 真·流式压缩设计（Zero Disk Temp Files）
- **核心机制**：Go 标准库 `archive/zip` 支持直接将压缩写入器包裹在 `http.ResponseWriter` 之上！
  ```go
  func (s *Server) handleZip(w http.ResponseWriter, r *http.Request) {
      // 1. 设置下载标头，开启分块传输 (Chunked)
      w.Header().Set("Content-Type", "application/zip")
      w.Header().Set("Content-Disposition", contentDisposition(dirName+".zip", true))
      
      zipWriter := zip.NewWriter(w)
      defer zipWriter.Close()
      
      // 2. 遍历目录，逐个文件写入 zipWriter
      filepath.Walk(realDir, func(path string, info os.FileInfo, err error) error {
          // 过滤 excludeFiles / excludeDirs
          // zipWriter.CreateHeader(...)
          // io.Copy(zipWriter, file)
          return nil
      })
  }
  ```
- **关键技术特性**：
  1. **零临时磁盘开销**：完全在内存小缓冲区流式中转，服务端磁盘增加 0 字节；
  2. **毫秒级响应**：点击后浏览器立刻弹出下载保存对话框，边压缩边流式下发；
  3. **安全过滤**：压缩过程中严格跳过 `.git`、`node_modules` 及敏感文件规则；软链接逃逸检测全面生效。

---

### 模块六：文件重命名（Rename）

#### 1. 交互设计
- 操作列提供“重命名”操作项，弹窗展示旧文件名，允许输入新文件名。

#### 2. 后端设计
- **接口**：`POST /api/rename?path=/data/sub/old.txt&new_name=new.txt`
- **安全检查**：
  1. 权限检查：`manage.allowRename == true`；
  2. `sanitizeFilename(new_name)` 清洗非法字符；
  3. 目标必须位于同一父目录内，严禁通过重命名跨目录移动文件（防路径穿越）；
  4. 目标文件若已存在，返回 `409 Conflict`，防意外覆盖已有文件；
  5. 执行原子 `os.Rename`。

---

### 模块七：独立口令保护的安全删除（Safe Delete with deleteToken）

#### 1. 为什么需要“独立删除口令”？
删除文件具有**极高风险与不可逆性**：
- 在家庭内网、工作室或私有共享中，往往允许多人甚至访客上传文件或临时编辑配置；
- 普通的二次确认弹窗（“是否确认删除？”）极易被误点击或误触；
- 若误删生产配置、家庭相册或重要备份，后果灾难。
- **解决方案**：在配置文件中设定**独立的删除口令 `deleteToken`**。所有删除请求必须携带并校验该口令，否则服务端物理拒绝！

#### 2. 配置与开关规则
```yaml
manage:
  enabled: true
  allowDelete: true          # 总删除开关
  deleteToken: "admin-pass"  # 独立删除口令（核心防护凭证）
```
- **硬锁死安全保护（Safe by Default）**：
  1. 若 `allowDelete: false`，删除功能全局禁用；
  2. 若 `deleteToken: ""`（未设置或留空），**即便是 `allowDelete: true`，服务端也一律强制拒绝所有删除请求**（返回 403：`delete operation locked: deleteToken is not configured`）；
  3. 只有明确配置了非空 `deleteToken` 且口令匹配时，才允许执行删除！

#### 3. 前端删除确认弹窗（Danger Modal）
- 界面视觉强化警示（红色醒目警告边框与标题）；
- 显示删除目标详情：文件名/目录名、文件大小、完整路径；
- **口令输入框**：
  `<input type="password" placeholder="请输入系统删除口令以确认..." x-model="deletePass">`；
- **可选会话记忆**：
  提供勾选框 `[ ] 本次浏览器会话记住口令 (sessionStorage)`，同一会话批量清理时无需反复输入，但关闭浏览器标签后立即失效，永不持久化到 localStorage 或 Cookie；
- **错误震颤动效**：
  若输入的口令错误，弹窗边框红光闪烁并提示“删除口令错误，操作被拒绝”。

#### 4. 后端接口与防御
- **接口**：`POST /api/delete`
- **参数**：
  - `path`: 待删除虚拟路径；
  - `token`: 用户在弹窗中输入的删除口令（或通过 Header `X-Delete-Token` 传递）；
- **校验逻辑**：
  1. 检查 `manage.allowDelete == true` 且 `manage.deleteToken != ""`；
  2. **时序安全比对（Constant-Time Compare）**：
     使用 `crypto/subtle.ConstantTimeCompare([]byte(token), []byte(configuredToken)) == 1`，彻底阻断基于响应时延探测口令长度的时序攻击（Timing Attack）；
  3. **保护根挂载点**：
     严禁删除根挂载点目录（如 `/data`, `/umedia` 等），无论传入何种口令一律直接拦截（返回 400：`cannot delete root mounts`）；
  4. **软链接越界防护**：
     若目标是指向 Root 之外的软链接，仅允许删除软链接自身，严禁递归删除软链接指向的外部真实目录；
  5. 目录删除防护：删除目录时默认仅允许空目录（`os.Remove`），若为非空目录可要求显式确认递归删除（`os.RemoveAll`），防止整盘秒空。

---

## 4. 配置文件规范（`config.yaml`）

为保持向后兼容，所有新增能力收敛在 `manage:` 配置段下（默认全部取安全默认值）：

```yaml
# FileList 扩展管理能力配置
manage:
  # 是否开启文件管理功能总开关（默认 false，保持只读安全）
  enabled: false

  # 文本在线查看与轻量编辑保存（默认 true，在 manage.enabled 开启时生效）
  allowEdit: true

  # 新建文件夹功能（默认 true，在 manage.enabled 开启时生效）
  allowMkdir: true

  # 文件重命名功能（默认 true，在 manage.enabled 开启时生效）
  allowRename: true

  # 文件删除功能（默认 false）
  allowDelete: false

  # 独立删除口令（核心安全项！）
  # 必须配置非空强口令，删除确认弹窗中必须输入此口令才允许物理删除
  # 若留空，删除功能自动硬锁死
  deleteToken: ""
```

---

## 5. 实施路线图（分阶段推进建议）

为保障主线稳定性与开发节奏，建议分三个阶段逐步实施：

### 阶段一：纯只读体验增强（零风险，立即见效）
- [ ] **音视频在线播放弹窗**（原生 HTML5 视频/音频组件，前端直接复用 Range 流）；
- [ ] **直链一键复制与极简手机扫码二维码**（纯前端实现，极大便利多设备分发）；
- [ ] **目录一键打包 ZIP 流式下载**（Go 标准库 `archive/zip`，0 磁盘开销）。

### 阶段二：文本编辑与目录创建（轻量运维利器）
- [ ] **新建文件夹功能**（`POST /api/mkdir` + 前端新建按钮）；
- [ ] **文本/代码轻量查看与在线编辑**（原生 `<textarea>` + `Ctrl+S` + 原子写盘与防误关）。

### 阶段三：受控文件管理与高危防御（严格安全）
- [ ] **文件重命名**（`POST /api/rename`）；
- [ ] **独立口令保护的安全删除**（`POST /api/delete` + `deleteToken` 常数时间比对 + 红色防误删模态框）。

---

## 6. 总结与后续

本设计方案在**不引入任何重型外部框架、不增加数据库、不破坏 7MB 单二进制**的前提下，补齐了 FileBrowser 在日常运维和内容分发中最常用的核心功能，同时通过**独立的删除口令（`deleteToken`）**从架构层面消除了误删数据的重大隐患。
