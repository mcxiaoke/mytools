# FileList

轻量级文件目录索引与搜索 Web 服务。类似 caddy filebrowser / nginx autoindex，但内置增量索引和文件名搜索。

## 特性

- **目录浏览** - 网页访问，列出配置的目录，可点击进入子目录
- **文件搜索** - 基于内存索引的文件名/目录名搜索，不实时遍历磁盘
- **真增量索引** - 目录未变化时整棵子树跳过（空闲时近零开销），变化只做差异更新，支持磁盘持久化
- **路径映射** - URL 路径与磁盘路径可不同，如 `/data -> /mnt/data`
- **子目录部署** - 支持 `basePath`，可挂到反代（Caddy/Nginx）的任意子路径下
- **文件下载/预览** - 点击文件在线预览或下载
- **全部只读** - 不支持上传；可选 token 鉴权，默认无鉴权开箱即用
- **跨平台** - 单二进制，支持 Windows 和 Linux
- **零依赖运行** - 编译为单个可执行文件，无运行时依赖

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
| `roots` | — | 路径映射列表 |

> 路径展开：`log.file`、`dataDir`、`index.persist`、`roots[].path` 中的 `~` 会展开为用户主目录。

## 安全（全部可选，默认开箱即用）

以下能力默认都是关闭或采取安全默认值，家庭内网可不配置直接运行；需要时可逐步开启：

| 能力 | 配置 | 说明 |
|------|------|------|
| **token 鉴权** | `server.token: 我的密码` | 开启后浏览器首次访问在地址后加 `?token=密码` 即可，服务端下发 Cookie 保持会话；脚本可用 `Authorization: Bearer 密码`。 |
| **软链接越界拦截** | `security.allowOutsideSymlinks: true` | 默认拦截通过软链接读取 root 之外的文件（如指向 `/etc` 的链接）；内网需要发布软链接内容时再开启。索引过程从不跟随目录软链接。 |
| **html/svg 防内联 XSS** | `security.blockInlineHTML: false` | 默认 `.html`/`.svg` 等一律强制下载，不在站点同源渲染（防止共享的 html 窃取本服务数据）；需要在线预览网页时关闭。 |
| **敏感文件默认排除** | `index.excludeFiles` | 默认排除 `.env`、`.htpasswd`、`*.key`、`id_rsa` 等敏感文件；显式写空列表 `excludeFiles: []` 可取消。搜索和目录浏览同时生效。 |

> 目录浏览（实时读盘）与索引搜索共享同一套排除规则，行为一致；默认的 root 排除项（`.git`、`node_modules` 等）也同时作用于两者。

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
./build/filelist -config config.yaml
```

浏览器访问 `http://localhost:8080`。

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
| 超时 | 大文件下载可能超时，Nginx 加 `proxy_read_timeout 600s;` |
| 请求体 | 只读服务无需调 `client_max_body_size` |
| 编码 | API 返回 JSON 已带 `charset=utf-8`，反代不影响 |

## API

| 路径 | 说明 |
|------|------|
| `GET /` | Web UI 页面 |
| `GET /api/roots` | 配置的根目录列表 |
| `GET /api/list?path=/data` | 列出指定目录内容 |
| `GET /api/search?q=keyword` | 搜索文件名 |
| `GET /api/stats` | 索引统计 |
| `GET /raw/data/file.txt` | 文件预览/下载 |
| `GET /raw/data/file.txt?download=1` | 强制下载 |

## 项目结构

```
filelist/
├── src/                  # 源码目录
│   ├── main.go           # 入口：配置加载、日志初始化、服务启动、优雅关闭
│   ├── config.go         # 配置解析（YAML）与路径映射
│   ├── indexer.go        # 增量索引引擎（内存索引+持久化+后台更新）
│   ├── server.go         # HTTP 路由与处理器
│   ├── log.go            # 日志（级别过滤+文件输出）
│   ├── web/index.html    # 嵌入式 Web UI（单页应用）
│   ├── go.mod
│   └── go.sum
├── build/                # 编译产物输出目录
├── data/                 # 运行时数据目录（索引缓存等，运行时生成）
├── config.sample.yaml    # 示例配置（含全部默认值和注释）
├── config.yaml           # 实际配置（从 sample 复制后编辑）
├── deploy/
│   ├── filelist.service        # systemd 服务（专用用户 + 安全加固）
│   └── filelist-root.service   # systemd 服务（root 直接运行，简化版）
├── docs/
│   └── DESIGN.md         # 实现方案设计文档
├── build.ps1             # PowerShell 构建脚本
└── Makefile              # Make 构建脚本
```
