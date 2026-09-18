# FileList

轻量级文件目录索引与搜索 Web 服务。类似 caddy filebrowser / nginx autoindex，但内置增量索引和文件名搜索。

## 特性

- **目录浏览** - 网页访问，列出配置的目录，可点击进入子目录
- **文件搜索** - 基于内存索引的文件名/目录名搜索，不实时遍历磁盘
- **真增量索引** - 目录未变化时整棵子树跳过（空闲时近零开销），变化只做差异更新，支持磁盘持久化
- **路径映射** - URL 路径与磁盘路径可不同，如 `/data -> /mnt/data`
- **子目录部署** - 支持 `basePath`，可挂到反代（Caddy/Nginx）的任意子路径下
- **现代前端体验** - 采用 Pico.css v2 语义化样式与 Alpine.js 声明式响应式框架，零外部 CDN 依赖，支持暗黑模式无缝自适应
- **图片网格与大图预览** - 支持经典列表与图片网格（Grid）双视图无缝切换与持久化；图片原生懒加载；全屏沉浸式 Lightbox 预览（支持键盘 Esc / 左右方向键循环切图）
- **音视频内联播放** - 网页模态框在线流式播放音视频（支持 HTTP 206 Range 分段传输），音频精致黑胶旋转动效
- **文本/代码在线编辑** - 支持 Markdown、代码与纯文本在线查看与保存，脏状态追踪、Tab 缩进、Ctrl+S 快捷键与临时文件原子落盘
- **文件管理与 ZIP 打包** - 支持新建文件夹、文件/目录重命名、目录流式打包 ZIP 下载（服务端零临时磁盘占用）
- **直链复制与手机扫码** - 一键复制下载直链，内置轻量离线 SVG 二维码生成，手机扫码立即可用
- **安全删除口令保护** - 删除危险操作专属 `deleteToken` 二次确认与防时序攻击校验，禁止删除根挂载点
- **移动端与字号自适应** - 移动端视口（400x840 等小屏）响应式重排，无横向滚动溢出；内置无级字号调节（100% ~ 150%）
- **构建元数据与页脚** - 页面页脚与 `/api/stats` 接口直观显示版本号、Git Commit Hash 与构建时间，服务启动日志自动输出元数据，支持 `-version` 命令行参数
- **文件下载/预览** - 点击文件在线预览或下载
- **文件上传（可选）** - 支持网页按钮与拖拽流式上传，大文件低内存占用，同名冲突自动后缀编号；默认只读
- **运维面板（可选）** - 浏览器内执行服务器命令：一次性命令（干净输出+真实退出码）与交互式终端（真 PTY，仅 Unix）；cwd 自动跟随浏览目录；默认关闭，命令白名单与 WebSocket Origin 校验双重防护
- **访问控制** - 可选 token 鉴权，默认无鉴权开箱即用
- **跨平台与单二进制** - 单可执行文件分发，支持 Windows 和 Linux，无运行时依赖

## 快速开始

### 编译

**PowerShell（推荐）：**

```powershell
./build.ps1                    # 编译当前平台
./build.ps1 -Target all        # 交叉编译 Linux + Windows
./build.ps1 -Target linux -Clean   # 清理后仅编译 Linux
```

**Make（Linux/macOS）：**

```bash
make build          # 编译当前平台
make build-all      # 交叉编译 linux + windows
```

产物输出到 `build/` 目录。

### 配置

复制示例配置并编辑：

```bash
cp config.sample.yaml config.yaml
```

配置说明见 `config.sample.yaml` 内注释。主要配置项：

| 字段 | 默认值 | 说明 |
|------|--------|------|
| `server.host` | `0.0.0.0` | 监听地址 |
| `server.port` | `8080` | HTTP 端口 |
| `server.basePath` | `""` | 反代子目录前缀（如 `/files`），根路径部署留空 |
| `server.token` | `""` | 可选访问令牌，空 = 无鉴权 |
| `log.level` | `info` | 日志级别：`debug` / `info` / `warn` / `error` |
| `log.file` | `""` (stdout) | 日志文件路径，空则输出到终端 |
| `dataDir` | `./data` | 运行时数据目录（索引缓存等），自动创建 |
| `index.interval` | `5m` | 索引更新间隔 |
| `index.persist` | `""` (auto) | 索引缓存文件，空则自动用 `dataDir/filelist.idx` |
| `index.maxDepth` | `0` | 目录遍历深度限制（0 = 无限，1 = 仅根目录下第一层） |
| `index.incremental` | `true` | 增量索引：目录 mtime/大小未变则整棵跳过；`false` = 每次全量重建 |
| `index.excludeDirs` | 内置 5 项 | 跳过的目录名（大小写不敏感，支持 `*?`） |
| `index.excludeFiles` | 内置敏感文件 | 跳过的文件名（glob，如 `.env`、`*.key`） |
| `security.allowOutsideSymlinks` | `false` | 是否允许下载指向 root 之外的软链接目标 |
| `security.blockInlineHTML` | `true` | html/svg 文件是否强制下载而非内联渲染 |
| `upload.enabled` | `false` | 是否允许上传文件（需进入具体目录，同名冲突自动后缀编号） |
| `manage.enabled` | `false` | 是否开启文件管理功能（编辑、新建文件夹、重命名、删除、打包等） |
| `manage.deleteToken` | `""` | 独立删除保护口令（删除时需二次确认输入此口令） |
| `manage.maxTextSize` | `2MB` | 文本在线编辑/预览的最大字节限制 |
| `ops.enabled` | `false` | 是否启用运维面板（**必须同时设置 `ops.token`**） |
| `ops.token` | `""` | 面板专用访问口令，与 `server.token` 相互独立 |
| `ops.blacklist` | 内置 26 项 | 追加的关键词黑名单，命令以任一关键词开头即被拒绝（前缀匹配，大小写不敏感） |
| `ops.terminalToken` | `""` | 交互式终端的独立第二因子，空则禁用终端模式 |
| `ops.cwdRoots` | `[]` | 允许的工作目录根（空 = 复用 `roots[].path`） |
| `ops.originPatterns` | `[]` | WebSocket Origin 白名单（空 = 从请求 Host 推导；反代场景必须显式配置） |
| `roots` | — | 路径映射列表 |

> 路径展开：`log.file`、`dataDir`、`index.persist`、`roots[].path` 中的 `~` 会展开为用户主目录。

## 安全（全部可选，默认开箱即用）

以下能力默认都是关闭或采取安全默认值，家庭内网可不配置直接运行；需要时可逐步开启：

| 能力 | 配置 | 说明 |
|------|------|------|
| **token 鉴权** | `server.token: 我的密码` | 开启后浏览器首次访问在地址后加 `?token=密码` 即可，服务端下发 Cookie 保持会话；脚本可用 `Authorization: Bearer 密码`。 |
| **独立删除口令防护** | `manage.deleteToken: 删除密码` | 针对删除危险操作，提供与全局访问 token 解耦的独立保护口令；前端弹窗必须输入口令才能确认；服务端采用恒定时间比对防时序攻击；严格禁止删除根挂载点。 |
| **软链接越界拦截** | `security.allowOutsideSymlinks: true` | 默认拦截通过软链接读取 root 之外的文件（如指向 `/etc` 的链接）；内网需要发布软链接内容时再开启。索引过程从不跟随目录软链接。 |
| **html/svg 防内联 XSS** | `security.blockInlineHTML: false` | 默认 `.html`/`.svg` 等一律强制下载，不在站点同源渲染（防止共享的 html 窃取本服务数据）；需要在线预览网页时关闭。 |
| **敏感文件默认排除** | `index.excludeFiles` | 默认排除 `.env`、`.htpasswd`、`*.key`、`id_rsa` 等敏感文件；显式写空列表 `excludeFiles: []` 可取消。搜索和目录浏览同时生效。 |
| **文件上传安全防护** | `upload.enabled: false`（默认） | 默认只读；开启后强制限制非根目录上传；自动清洗非法字符与控制字符、规避 Windows 保留设备名、UTF-8 字符边界截断、同名自动追加 `(1)` 编号防覆盖、敏感文件拦截、流式落盘防 OOM。 |

> 目录浏览（实时读盘）与索引搜索共享同一套排除规则，行为一致；默认的 root 排除项（`.git`、`node_modules` 等）也同时作用于两者。

## 文件上传（可选能力）

默认处于关闭状态（只读模式）。如需开启上传功能，在配置文件中启用：

```yaml
upload:
  enabled: true
```

- **使用方式**：进入具体子目录后，点击工具栏中的“上传”按钮或直接将文件拖拽至网页内。
- **实时进度**：支持大文件上传进度百分比展示，上传完成后自动刷新文件列表。
- **流式写入**：服务端使用流式传输直接落盘，有效降低传输大文件时的内存开销。
- **同名防覆盖**：当目录中已存在同名文件时，自动按数字编号生成新文件名（例如 `file (1).txt`、`file (2).txt`），保障原文件不被意外覆盖。
- **跨平台文件名安全**：自动过滤或转换不同操作系统下的非法字符（如 `<>:"/\|?*` 及 ASCII 控制字符替换为 `_`），规避 Windows 保留设备名称（`CON`、`PRN`、`AUX`、`NUL`、`COM1-9` 等自动前缀 `_`），并清理尾部非法空格和点。
- **超长文件名截断**：适配主流文件系统的 255 字节单文件名限制，超长文件名会在合法 UTF-8 字符边界对主干进行截断，保留完整后缀扩展名并预留数字冲突编号空间。
- **目录限制**：根路径 `/` 仅展示各个挂载点（Roots），无法直接上传；必须进入具体的目录后才能上传。
- **敏感文件防护**：与目录索引一致，命中的敏感文件（如 `.env` 等）会被禁止上传。

## 文件管理（可选能力）

默认处于关闭状态（只读模式）。如需开启文件编辑、新建文件夹、重命名、打包下载及删除等管理功能，在配置文件中启用：

```yaml
manage:
  enabled: true             # 是否启用管理功能
  deleteToken: "delete123"  # 独立删除保护口令（强烈推荐设置，删除时需二次确认）
  allowEdit: true           # 允许在线编辑文本/Markdown/代码
  allowMkdir: true          # 允许新建文件夹
  allowRename: true         # 允许重命名文件或文件夹
  allowDelete: true         # 允许删除文件或文件夹
  allowZip: true            # 允许目录流式打包 ZIP 下载
  maxTextSize: "2MB"        # 文本编辑最大文件大小
```

- **音视频在线播放**：点击音视频文件（`.mp4`, `.webm`, `.mp3`, `.ogg`, `.flac`, `.wav` 等）唤起播放模态窗，支持 HTTP 206 Range 分段流式传输即开即播，音频支持黑胶旋转动效与元数据展示。
- **文本/代码在线编辑**：支持 Markdown、文本与代码文件在线查看与编辑，等宽排版、Tab 缩进插入 2 个空格、`Ctrl+S` 快捷保存、变动脏状态防丢提醒；服务端采用临时文件落盘并原子替换（Atomic Rename）。
- **新建文件夹 & 重命名**：模态窗交互，支持同名防重校验与合法性过滤，操作后自动更新内存索引。
- **安全删除（deleteToken）**：危险操作二次确认；配置 `deleteToken` 后必须输入口令才可执行删除；服务端使用恒定时间比较防时序攻击；严格保护根挂载点不允许删除。
- **目录流式 ZIP 下载**：工具栏与目录操作提供「📦 打包下载」，服务端边压缩边流式写入 HTTP 响应，零临时磁盘占用。
- **直链复制与手机二维码**：单文件一键复制下载直链；内置纯前端 SVG 二维码生成，手机扫码立即可用。

## 运维面板（可选能力）

> ⚠️ **安全提示**：这是本项目风险等级最高的功能。在 root 部署下，交互式终端等价于一个**可通过浏览器访问的完整 root shell**。默认关闭，请仔细阅读本节后再启用。

**使用场景**：在 filelist 中在线编辑完配置文件后，直接执行 `systemctl restart xxx.service` 使其生效，无需切回 SSH。

### 启用

```yaml
ops:
  enabled: true              # 主开关
  token: "面板专用口令"       # 必需：面板自己的访问口令
  terminalToken: "终端口令"   # 交互式终端的独立第二因子（可选）
  blacklist:                 # 可选：追加黑名单关键词（前缀匹配）
    - mv
    - systemctl stop
```

**两个条件必须同时满足**，面板才会被注册：`ops.enabled: true` **且** `ops.token` 非空。若只开 `enabled` 而未设 token，面板保持禁用并在启动日志中告警——这是刻意的 fail-closed 设计，因为握手没有凭据可校验时，面板等于一个无锁的命令执行器。

**`ops.token` 与 `server.token` 是分开的**，这是刻意的设计：

- 浏览文件应当保持简单，共用一个口令会诱使人把 `server.token` 留空；
- 面板是本项目风险最高的功能，它需要一个可以独立轮换/吊销的凭据，而不影响正常浏览。

面板首次打开时会在抽屉内提示输入 `ops.token`，口令只保存在当前标签页的 `sessionStorage`，关闭标签页即失效。

### 两种模式

| 模式 | 说明 | 输出特性 | 平台 |
|------|------|----------|------|
| **命令**（默认） | 一次性执行，管道收集输出 | 无回显噪声、无 ANSI 转义、**真实退出码** | 全平台 |
| **终端** | 交互式 PTY，真实 shell | 支持 `vim`/`top`/`journalctl -f` 等 | 仅 Unix |

默认落在「命令」模式：主场景（`systemctl restart`）在管道模式下体验更好——输出干净、有退出码、不会被 `less` 分页器卡住。

### cwd 自动跟随

面板的工作目录会**自动跟随你在 filelist 中浏览的目录**。例如浏览到 `/data/nginx` 时，面板的 cwd 即为该目录对应的磁盘路径，可直接执行 `nginx -t`。可点击 🔒 锁定以停止跟随。

### 内置关键词黑名单

命令默认**放行**，只有以黑名单关键词**开头**的命令会被拒绝。内置关键词是这些（大小写不敏感的前缀匹配）：

`rm` `dd` `mkfs` `shred` `truncate` `chmod` `chown` `chattr` `mount` `umount` `shutdown` `reboot` `halt` `poweroff` `kill` `pkill` `useradd` `userdel` `passwd` `crontab` `iptables` `ufw` `apt` `yum` `dnf` `pacman`

因为是前缀匹配，`rm` 同时覆盖 `rmdir`；只有命令**开头**参与比较，所以 `grep rm /var/log/syslog`、`ls /data/rmtest` 这类正常读取不会被误伤。

`ops.blacklist` 中的关键词**追加**到上述集合之上：

```yaml
ops:
  blacklist:
    - mv              # 前缀匹配会同时拦下 mv 开头的命令
    - docker
    - systemctl stop
```

> `mv` 故意不在内置集合中——作为前缀它会连 `mvn` 一起拒绝，而移动文件是常规操作。需要更严格时自行加入。
>
> 分页器与交互式工具（`less`、`more`、`top`、`vi`）不在黑名单内：它们只是会阻塞等待输入，属于「终端」模式的场景。

### 安全模型

| 层级 | 措施 |
|------|------|
| **双重前置** | `enabled` + 非空 `ops.token`，缺一不可（fail-closed） |
| **独立口令** | `ops.token` 与 `server.token` 解耦，可单独轮换/吊销 |
| **独立第二因子** | `ops.terminalToken` 仅用于交互式终端；与 `ops.token` 解耦 |
| **Origin 校验** | WebSocket 握手强制校验 `Origin`，**这是防跨站 WebSocket 劫持（CSWSH）的唯一防线**——因为会话 Cookie 是 `SameSite=Lax`，而 Lax **不能**阻止跨站 WS 握手。未校验时，你访问的任何恶意网页都能静默连上内网面板拿到 root shell。 |
| **显式 token** | 握手必须在 URL 或 `Authorization: Bearer` 中显式携带 token，**不接受仅靠 Cookie 隐式通过** |
| **拼接符扫描** | 元字符（`;` `&&` `\|` `` ` `` `$(` `>` 等）扫描**先于**黑名单匹配执行，含拼接符的命令直接拒绝——否则 `ls; rm -rf /` 会凭第一个词蒙混过关 |
| **提权前缀剥离** | `sudo` / `doas` / `pkexec` 等前缀在匹配前剥离，防止 `sudo rm -rf /` 绕过前缀规则 |
| **cwd 沙箱** | 工作目录限制在 `roots` 内（注意：**这仅约束工作目录，不是安全边界**，`cat /etc/passwd` 依然可读） |
| **环境变量白名单** | 子进程仅继承 `PATH`/`HOME`/`LANG`/`TERM` 等，**绝不继承服务进程完整 env**——否则一条 `env` 就能读走所有 token |
| **进程组回收** | 超时后 SIGTERM → SIGKILL **整个进程组**，防止管道子进程变孤儿 |
| **资源限制** | 单命令超时、输出上限（防 `yes` 洪泛）、并发上限、空闲回收 |
| **审计日志** | 每次执行（含被拒绝的）记录客户端 IP、cwd、命令、退出码、耗时 |

### 反向代理配置

反代场景下 `Origin` 与 `Host` 可能不一致，需显式配置 `ops.originPatterns`：

```yaml
ops:
  originPatterns:
    - "files.example.com"
```

Nginx 需关闭缓冲并放宽超时：

```nginx
location /files/api/ops/ws {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_buffering off;          # 否则输出被缓冲，实时性丧失
    proxy_read_timeout 3600s;     # 否则 60s 空闲即被切断
    proxy_send_timeout 3600s;
}
```

### 能力边界（务必了解）

- **cwd 沙箱不是安全边界**：它只约束工作目录，不阻止读取沙箱外的文件。真正的边界是关键词黑名单与拼接符扫描。
- **前端二次确认不是安全边界**：仅为防手滑，安全由服务端强制。
- **黑名单是「防手滑」而非「防攻击」**：默认放行任意命令，因此拿到 `ops.token` 的人等价于拿到一个受限 shell。不要把面板暴露在不可信网络；需要更强隔离时配合 `ops.cwdRoots` 与独立部署用户。
- **本功能不替代 SSH**：缺少 SSH 的会话复用、端口转发、SFTP 等能力。它是「改完即生效」的快捷通道。
- **Windows 下无交互终端**：`creack/pty` 是 Unix 专用库，Windows 平台仅提供一次性命令模式（前端会自动隐藏终端入口）。

### 文件位置说明

| 文件 | 默认位置 | 由哪个配置控制 |
|------|----------|----------------|
| 索引缓存 | `<dataDir>/filelist.idx` | `dataDir` + `index.persist` |
| 日志文件 | stdout（终端） | `log.file` |
| 运行时数据 | `./data/` | `dataDir` |

**本地开发**（默认配置，文件落在项目目录下）：

```
filelist/
├── config.yaml          # 配置文件
├── data/                # dataDir 默认值
│   └── filelist.idx     # 索引缓存（自动生成）
└── build/
    └── filelist         # 二进制
```

**Linux 部署**（systemd 推荐配置）：

```yaml
# /etc/filelist/config.yaml
log:
  level: info
  file: /var/lib/filelist/filelist.log
dataDir: /var/lib/filelist
```

文件分布：

```
/usr/local/bin/filelist              # 二进制
/etc/filelist/config.yaml            # 配置
/var/lib/filelist/
├── filelist.idx                     # 索引缓存
└── filelist.log                     # 日志
```

### 运行

```bash
# 启动服务（默认读取 config.yaml）
./build/filelist -config config.yaml

# 查看版本与构建元数据
./build/filelist -version
```

浏览器访问 `http://localhost:8080`。

> 提示：在开发或生产运维中，可直接使用 `.\deploy.ps1` 一键编译 Linux 二进制、通过 SCP 传输并重启远程 systemd 服务。

## Linux 开机启动

提供两套 systemd 服务文件，按需选择：

| 文件 | 运行用户 | 安全加固 | 适用场景 |
|------|----------|----------|----------|
| `deploy/filelist.service` | 专用 `filelist` 用户 | 有（ProtectSystem 等） | 对公网或有安全要求 |
| `deploy/filelist-root.service` | root | 无 | 局域网内网，快速部署 |

两套方案都预配置 `WorkingDirectory=/var/lib/filelist`，需在 `config.yaml` 中将 `dataDir` 和 `log.file` 指向该目录。

### 方案一：root 直接运行（内网快速部署）

```bash
# 1. 安装二进制和配置（Windows 上交叉编译出的 Linux 文件默认无执行权限，务必 chmod +x）
sudo cp build/filelist-linux-amd64 /usr/local/bin/filelist
sudo chmod +x /usr/local/bin/filelist
sudo mkdir -p /etc/filelist
sudo cp config.yaml /etc/filelist/

# 2. 安装 service 文件
sudo cp deploy/filelist-root.service /etc/systemd/system/

# 3. 编辑配置：确保 dataDir 和 log.file 指向 /var/lib/filelist，
#    并把 roots 改为真实要共享的绝对路径（配置放在 /etc/filelist 时，
#    沿用默认 "path: ./" 会把 /etc 本身发布到网上！）
#    dataDir: /var/lib/filelist
#    log:
#      level: info
#      file: /var/lib/filelist/filelist.log
#    roots:
#      - url: /data
#        path: /mnt/data

# 4. 启用并启动（/var/lib/filelist 由 ExecStartPre 自动创建）
sudo systemctl daemon-reload
sudo systemctl enable --now filelist

# 查看状态
sudo systemctl status filelist

# 查看日志
tail -f /var/lib/filelist/filelist.log
```

### 方案二：专用用户 + 安全加固

```bash
# 1. 安装二进制和配置（Windows 上交叉编译出的 Linux 文件默认无执行权限，务必 chmod +x）
sudo cp build/filelist-linux-amd64 /usr/local/bin/filelist
sudo chmod +x /usr/local/bin/filelist
sudo mkdir -p /etc/filelist /var/lib/filelist
sudo cp config.yaml /etc/filelist/

# 2. 安装 service 文件
sudo cp deploy/filelist.service /etc/systemd/system/

# 3. 编辑配置：确保 dataDir 和 log.file 指向 /var/lib/filelist，
#    并把 roots 改为真实要共享的绝对路径（配置放在 /etc/filelist 时，
#    沿用默认 "path: ./" 会把 /etc 本身发布到网上！）
#    dataDir: /var/lib/filelist
#    log:
#      level: info
#      file: /var/lib/filelist/filelist.log
#    roots:
#      - url: /data
#        path: /mnt/data

# 4. 创建运行用户
sudo useradd -r -s /usr/sbin/nologin -d /var/lib/filelist filelist

# 5. 确保运行用户可以读取索引目录
sudo chown -R filelist:filelist /var/lib/filelist

# 6. 确保运行用户可以读取要索引的目录
#    sudo chown -R filelist:filelist /mnt/data /mnt/usb  # 按需

# 7. 启用并启动
sudo systemctl daemon-reload
sudo systemctl enable --now filelist

# 查看状态
sudo systemctl status filelist

# 查看日志
sudo journalctl -u filelist
# 或直接查看日志文件
tail -f /var/lib/filelist/filelist.log
```

## 反向代理

FileList 可通过 Caddy 或 Nginx 反代，支持根路径与子目录两种挂载方式。

### 方式一：根路径反代（独立域名/端口）

后端 `config.yaml` 不设 `basePath`：

```caddy
filelist.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

```nginx
server {
    listen 80;
    server_name filelist.example.com;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

### 方式二：子目录反代（推荐，配置 `basePath`）

后端 `config.yaml` 设置 `basePath: /files`，服务自身认识 `/files` 前缀，**反代无需任何 rewrite**，原样转发即可：

```yaml
server:
  basePath: /files   # 页面、API、下载链接都会自动带上前缀
```

**Caddy**（与同域其他站点并存）：

```caddy
example.com {
    handle /files/* {
        reverse_proxy 127.0.0.1:8080
    }
    handle {
        # 其他站点
        reverse_proxy 127.0.0.1:3000
    }
}
```

**Nginx**（`proxy_pass` 末尾不要加 `/`，保持前缀转发）：

```nginx
server {
    listen 80;
    server_name example.com;

    location /files/ {
        proxy_pass http://127.0.0.1:8080;   # 无末尾斜杠：保留 /files 前缀
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    location / {
        proxy_pass http://127.0.0.1:3000;   # 其他站点
    }
}
```

> 反向兼容：若反代层已自行剥掉前缀（如 `rewrite` 或 `proxy_pass` 末尾 `/`），后端保持 `basePath` 为空即可，两种方案不要混用。

### 注意事项

| 项目 | 说明 |
|------|------|
| 文件下载 | FileList 用 `Content-Disposition` 和 `/raw/` 路径处理下载，反代不影响 |
| WebSocket | 不需要，FileList 是纯 HTTP |
| 超时 | 大文件下载/上传可能耗时较长，Nginx 建议配置 `proxy_read_timeout 600s; proxy_send_timeout 600s;` |
| 请求体 / 上传 | 默认只读模式无需调整；若开启文件上传且需传输大文件，Nginx 建议配置 `client_max_body_size 0;`（或按需设为如 `10G`） |
| 编码 | API 返回 JSON 已带 `charset=utf-8`，反代不影响 |

## 测试

### 单元测试与接口测试（Go）

```bash
cd src
go test -v ./...
```

### 端到端测试（E2E 浏览器自动化）

项目采用 Playwright 驱动本机 Microsoft Edge (Chromium) 浏览器运行全流程自动化测试。自动分配测试端口并启动 `filelist.exe`、准备临时目录与配置夹具、执行真实浏览器交互断言并在测试完成后自动清理。

```powershell
# 一键运行全部 E2E 测试（无头模式）
.\tests\run-e2e.ps1

# 调试模式（带浏览器界面交互）
.\tests\run-e2e.ps1 --headed

# 交互式 Playwright UI 运行器
.\tests\run-e2e.ps1 --ui

# 运行单个测试用例集（如上传测试）
.\tests\run-e2e.ps1 specs/upload.spec.js
```

## API

| 路径 | 说明 |
|------|------|
| `GET /` | Web UI 页面 |
| `GET /api/roots` | 配置的根目录列表 |
| `GET /api/list?path=/data` | 列出指定目录内容 |
| `GET /api/search?q=keyword` | 搜索文件名 |
| `GET /api/stats` | 索引统计及版本构建元数据（包含 `version`, `gitCommit`, `buildTime`） |
| `GET /raw/data/file.txt` | 文件预览/下载 |
| `GET /raw/data/file.txt?download=1` | 强制下载 |
| `POST /api/upload?path=/data` | 上传文件到指定目录（multipart/form-data，需开启 `upload.enabled`） |

## 项目结构

```
filelist/
├── src/                  # 源码目录
│   ├── main.go           # 入口：配置加载、日志初始化、版本信息注入、服务启动、优雅关闭
│   ├── config.go         # 配置解析（YAML）与路径映射
│   ├── indexer.go        # 增量索引引擎（内存索引+持久化+后台更新）
│   ├── server.go         # HTTP 路由与处理器（含流式文件上传与动态配置注入）
│   ├── utils.go          # 纯函数与通用工具（文件名安全清洗、UTF-8边界截断、同名编号、越界检测等）
│   ├── log.go            # 日志（级别过滤+文件输出）
│   ├── web/              # 嵌入式 Web 前端资源（//go:embed web/*）
│   │   ├── index.html    # SPA 语义模板（列表/网格双视图、全屏 Lightbox、字号控制器、页脚）
│   │   └── static/       # 前端静态资产（Pico.css, Alpine.js, app.css, app.js）
│   ├── *_test.go         # 单元测试与接口集成测试
│   ├── go.mod
│   └── go.sum
├── tests/                # 测试套件
│   ├── run-e2e.ps1       # 一键运行 E2E 浏览器自动化测试脚本
│   └── e2e/              # Playwright E2E 测试工程（配置、夹具、全量 27 项测试用例）
│       └── specs/        # auth, download, grid, navigation, search, sorting, upload
├── build/                # 编译产物输出目录
├── data/                 # 运行时数据目录（索引缓存等，运行时生成）
├── config.sample.yaml    # 示例配置（含全部默认值和注释）
├── config.yaml           # 实际配置（从 sample 复制后编辑）
├── deploy/
│   ├── filelist.service        # systemd 服务（专用用户 + 安全加固）
│   └── filelist-root.service   # systemd 服务（root 直接运行，简化版）
├── docs/
│   ├── DESIGN.md         # 实现方案设计文档
│   └── CHANGES-*.md      # 重点变更记录
├── build.ps1             # PowerShell 构建脚本（注入 Git Commit 与构建时间）
├── deploy.ps1            # PowerShell 一键交叉编译并热更新部署到远程 Linux 服务
└── Makefile              # Make 构建脚本
```
