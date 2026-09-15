# TRANSACTION — 事务与崩溃恢复

> 来源：源文档 §5、§6
> 本文回答：**怎么保证"全量成功或全量回退"？进程被强杀或掉电后怎么收敛？**
> 这是全文的**正确性骨架**。此处的任何一项都不可延后首发——它们直接决定"失败是否可回滚"。

---

## 1. 崩溃恢复：三层闭环

### 1.1 三层职责与不变量

| 层 | 机制 | 覆盖场景 | 时效 |
| :--- | :--- | :--- | :--- |
| **L1** | 进程内错误处理：任一步失败 → 立即回滚（§3.5） | 可捕获的 IO 失败、替换失败、自检不通过 | 立即 |
| **L2** | **恢复看门狗**：detached 独立进程，Worker 被强杀/崩溃后接管 | `taskkill /f`、panic abort、杀软终止、进程树被清 | 秒级 |
| **L3** | **冷启动自愈**：任意 updater 启动（含 `--recover`）优先介入。**其可靠性依赖调用方遵守 §2.3 的硬性集成契约**——L3 的触发条件由调用方决定，L2 看门狗已交付但**不能替代**该契约（L2 自身也可能被杀） | 看门狗亦被杀、系统断电、长期遗留 | 下次启动 |

三层共用同一段恢复代码（`journal::recover`），并以 **Journal + 磁盘实际状态**为唯一事实来源，因此天然幂等：任意层可在任意时刻重复执行而结果收敛。

**不变量守恒清单（I1–I10）** —— 这是正确性骨架，**任何新增能力都必须逐条对照，确认不破坏，并为每条补一条回归测试**：

| ID | 不变量 | 容易被谁触碰 |
| :--- | :--- | :--- |
| **I1** | 在 `STAGE:COMMITTED` fsync **之前**，回滚总能恢复旧版 | 保留上一版本需证明它只发生在 COMMITTED 之后 |
| **I2** | 在 `STAGE:COMMITTED` fsync **之后**，**绝不**回滚 | "转移失败"分支必须遵守：只能 WARNING，不得触发回滚 |
| **I3** | 备份**成功转移（或删除）之前不得删除 Journal** | 把"删除备份"改为"转移备份"时必须保持同等顺序保证 |
| **I4** | 恢复只作用于**本 GEN** | GC 规则必须只清理 TTL 到期且未被引用的代 |
| **I5** | 任何 Tier 2 功能失败不改变退出码与事务结果 | 进度文件、RM 诊断、日志、GC、运行期目录全部需附"失效不影响判定"测试 |
| **I6** | 无 Journal 时 target 状态**确定**（提交已完成或从未开始） | 保留目录**不是** Journal，不参与该判定；`--rollback-previous` 必须写**新事务** |
| **I7** | 替换只在**同卷**内进行 | 保留目录必须与 target 同卷（放在 target 内即满足） |
| **I8** | 验签与解压作用于**同一文件对象** | 清单读取必须复用同一句柄（不得按路径重新打开） |
| **I9** | **内部保留名永不被写入** | 新增保留名必须同步加入两层拦截点 |
| **I10** | 回滚是**幂等**的 | `--rollback-previous` 必须复用同一段恢复代码，不得另写一份 |

### 1.2 Journal 规范（`.updater/journal`）

位置：`<target>/.updater/journal`。行式文本、UTF-8、LF 结尾。

```
JOURNAL:3
GEN:20260914T161230-18422
TARGET:C:\App
BACKUP:C:\App\.updater\backup\20260914T161230-18422
TMPDIR:C:\App\.updater\tmp\20260914T161230-18422
TOVER:1.5.0
STAGE:PLANNED
EXIST:app.exe
EXIST:data\icudtl.dat
NEW:newfeature.dll
DIR:plugins\newdir
STAGE:APPLYING
MOVED:app.exe
ADDED:newfeature.dll
STAGE:COMMITTED
```

> `JOURNAL` 版本号随格式变更递增。版本未知一律**拒绝恢复**并退出 3，绝不"尽力猜测"——恢复路径不接受任何形式的宽容解析。

#### 1.2.1 两类记录：权威集与建议集（崩溃窗口得以封闭的核心）

| 类别 | 记录 | 落盘时机 | 承载正确性 |
| :--- | :--- | :--- | :--- |
| **权威** | `JOURNAL` / `GEN` / `TARGET` / `BACKUP` / `TMPDIR` / `TOVER` / `EXIST` / `NEW` / `DIR` | **PLANNED 阶段一次性写入并 fsync**，此后不再修改 | **是**——恢复的唯一权威依据 |
| **权威** | `STAGE:*` | 每次阶段迁移**单独追加并 fsync** | **是** |
| **建议** | `MOVED` / `ADDED` / `DIRDONE` | 追加写，**不逐行 fsync**，仅在阶段迁移与收尾时统一 flush | **否**——仅供诊断与进度显示 |

`TOVER`（目标版本）在任何文件动作之前即已 durable，因此即使在"COMMITTED 之后、`.updater/state` 写入之前"崩溃，恢复层也能把已安装版本补写成正确值，不会出现"目录是新版、状态文件记着旧版"的不一致。

**为什么建议集不承载正确性**：若文件动作已完成而记录尚未落盘（断电/强杀窗口），依赖记录的回滚会漏掉该文件并留下"半新半旧"。解法是让回滚**不依赖**这些记录：

- **覆盖类（`EXIST` 中的路径）**：回滚通过**备份目录对账**判定——备份目录里有什么就还原什么。备份是 `ReplaceFileW` 在同一原子操作中产出的，**内容天然与磁盘状态一致**，不存在"动作成功但备份没生成"的窗口。
- **新增类（`NEW` 中的路径）**：删除判定所需的信息已由 `PLANNED` 阶段的**权威集**持久化，且发生在任何文件动作之前。

因此"任意时刻断电均可确定性收敛"这一承诺**在实现层面成立**。

#### 1.2.2 目录隔离：GEN 代号

`BACKUP` 与 `TMPDIR` 均带 `GEN` 子目录：

```
<target>/.updater/backup/<GEN>/...
<target>/.updater/tmp/<GEN>/...
```

理由：若不加隔离，一旦"上一事务提交成功但备份删除失败"留下陈旧备份，下一事务在"无条件还原备份目录全部内容"策略下会把文件**回退到更早版本**。带 `GEN` 后：

- 恢复只在本事务的 `<BACKUP>` 内对账，绝不触碰其它代；
- 陈旧代由 GC 规则清理（在任意 updater 启动时执行，失败仅 WARNING）：
  - `.updater/backup/*` 与 `.updater/tmp/*`：**不被任何现存 Journal 引用**且 **mtime 早于 24 小时** → 删除；
  - `.updater/previous/*`：**保留最新 1 个**；其余在 **mtime 超过 `--previous-ttl-days`（默认 7 天）** 后删除；
  - GC 只按上述三条规则动作，**不解析、不推断、不"尽力恢复"**；`--previous-ttl-days 0` 时该目录不再产生。

#### 1.2.3 耐久性与鲁棒性

- Journal 句柄在事务期间保持打开；`PLANNED`、`APPLYING`、`COMMITTED` 三次迁移均执行 `sync_data()`（等价 `FlushFileBuffers`），**成功后才执行对应的文件动作**。
- 允许尾行截断：解析时忽略无法识别行；`STAGE` 取最后一条**成功解析**的记录。
- 拒绝恢复（退出 3 并保留现场）：`JOURNAL` 版本未知、`TARGET` 与 `--target` 不符、`BACKUP`/`TMPDIR` 不在 `target` 内。
  - **比对前必须规范化**：去 `\\?\` 前缀、分隔符统一为 `\`、**大小写不敏感**、去掉尾随分隔符。否则 `c:\app` 与 `C:\App\` 这类**等价路径**会被判为"跨应用误伤"而拒绝恢复并退出 3——那是把一次正常的自愈变成灾难态。用例 `journal_target_case_insensitive`。
  - `BACKUP`/`TMPDIR` 的"在 target 内"判定同样在规范化之后进行（同时防止把外部目录当成本事务的备份树来还原）。
- **不使用 JSON**：崩溃路径不应引入解析器依赖（serde 全套约 +100–200 KB，手写 JSON 解析徒增攻击面）。

### 1.3 三层恢复的收敛路径（总览）

| Journal 状态 | 含义 | 恢复动作 |
| :--- | :--- | :--- |
| 不存在 | 无未完结事务 | 无事可做 |
| `STAGE:PLANNED` | 计划已落盘但未开始改动 | 目录零改动；仅清理备份/临时目录与 Journal |
| `STAGE:APPLYING` | 改动中 | **执行回滚**（§3.5）；成功后**拉起主程序**（旧版完整可用） |
| `STAGE:COMMITTED` | 已提交，绝不回滚（I2） | **完成收尾**（§3.6）；成功后**拉起主程序** |

`COMMITTED` + 自检通过意味着目录状态**完全确定**，此时拉起是安全的，且是唯一能补救"目录已是健康新版、用户却看不到应用启动"这一窗口的动作。

### 1.4 恢复看门狗（L2）

**派生时机**：`PLANNED` 已 fsync 之后、`STAGE:APPLYING` 之前。这样看门狗在**任何文件动作之前**就已存在。

**派生方式**：把自身复制为 `<运行期目录>\upd-watchdog-<GEN>.exe`，然后：

```text
upd-watchdog-<GEN>.exe --watchdog --target <abs> --watch-pid <Worker PID>
                        --watch-image <Worker 映像绝对路径>
                        [--launch <abs>] [--args <原始片段>] [--log <abs>]
                        [--keep-elevation] [--elevated-worker]
                        --previous-ttl-days <N>
                        --write-retries <N> --write-delay-ms <N>
                        --timeout <N>
```

**参数透传集合**：看门狗会**独立执行**收尾（§3.6）与回滚（§3.5），因此凡影响这两条路径的参数**必须透传**——否则看门狗用默认值、与 Worker 的取值分叉（例如 Worker 传 `--previous-ttl-days 0`，看门狗按默认 7 天保留，行为不一致且难以复现）。

| 必须透传 | 不需要透传 | 说明 |
| :--- | :--- | :--- |
| `--previous-ttl-days`、`--write-retries`、`--write-delay-ms`、`--timeout`、`--launch`、`--args`、`--log`、`--keep-elevation`、`--elevated-worker`、`--pid` | `--zip`、`--sig`、`--require`、`--keep`、`--strip`、`--sha256`、`--min-version`、`--allow-downgrade`、`--dry-run`、`--elevate` | 右侧这些只服务于"发起一次新更新"，而收尾与回滚只依赖 Journal 里已有的权威记录（`TOVER` / `EXIST` / `NEW` / `DIR`） |

**`--delete-zip` 在看门狗分支的语义为"不执行"**：看门狗**不清理 zip**——它是 Tier 2 友好性动作，且 zip 可能已被 Worker 删除；允许看门狗也去删会出现"两个删除者"，风险高于收益。残留交给调用方或下次运行（`--delete-zip` 失败本来就只记 WARNING）。

创建参数：`DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP`，`bInheritHandles = FALSE`，`lpCurrentDirectory = target`。

**看门狗循环**

```text
1. 安全超时上限 = --timeout + 600s（防孤儿永久驻留）
2. OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, watch_pid)
     拿不到句柄 → 视为 Worker 已退出，进入 3
     拿到句柄   → QueryFullProcessImageNameW 比对 --watch-image：
                   不匹配 ⇒ PID 已被复用，视为 Worker 已退出，进入 3（不白等超时）
                   匹配   ⇒ WaitForSingleObject(handle, 安全超时)
                             · WAIT_OBJECT_0 → Worker 已退出，进入 3
                             · WAIT_TIMEOUT  → 命中安全上限、**Worker 仍存活** → 进入 3T
3T.（安全超时路径）
     记 WARNING "看门狗安全超时（--timeout+600s）已到而 Worker 仍存活"，**放弃守护**，
     写日志终止行、自身改名 .del 后退出；日志中**明确标注"降级为 L3"**。
     ※ 绝不能在此处去取锁、再据其成败推断"已有新实例接管"：Worker 仍存活时它自己
       就持着锁，取锁失败**恰恰证明 Worker 没死**。按错误写法会静默丢掉 L2 覆盖，
       且文档没有说明已降级——这是必须修掉的一处语义错误。
3. 尝试获取锁（10s 轮询，32/5 均可重试）
     失败 ⇒ Worker 已退出却仍取不到锁 → 判为"已有新 updater 接管"，自身改名 .del 后退出
     成功 ⇒ 进入 4
4. 读 Journal，按 STAGE 分支：
     · 不存在             → 无事可做
     · STAGE:COMMITTED    → **完成收尾**（按 §3.6 的顺序，幂等）：
                              ① 用 TOVER 补写/校正 .updater/state
                              ② 写 <BACKUP>/_meta.txt（ADDED 取自 Journal 的权威 NEW 集合）
                              ③ 备份 → .updater/previous/<GEN>（或按 TTL 删除）
                              ④ 删除 Journal ← **仅当 ② 与 ③ 都成功**
                            **全部成功后执行拉起主程序**
     · PLANNED / APPLYING → 执行回滚（§3.5）
                            **回滚成功后执行拉起主程序**（旧版完整可用）
     拉起失败 → ERROR 日志（不影响看门狗自身退出）
5. 释放锁，写日志终止行，自身改名 .del，退出
```

**为什么看门狗可以拉起**：若 Worker 在 `COMMITTED` 之后、拉起之前被杀，结果是"目录已是健康新版、用户却永远看不到应用启动"，且 Journal 被 GC 后再无自愈入口。此时拉起是安全的，且是唯一能补救该窗口的动作。

**为何不会重复拉起**：Worker 正常收尾顺序为"`COMMITTED` → 写 `_meta.txt` → 转移备份 → 删 Journal → 清理 zip → 拉起 → 自改名 → 退出"，即 Worker 退出时 Journal 已不存在。看门狗只在 Worker **退出之后**（`WAIT_OBJECT_0` 分支）动作，故不会重复拉起。

派生失败（磁盘满/被杀软拦截）→ 记录 WARNING 并继续，系统降级到 L3，不阻断更新。

---

## 2. 恢复模式 `--recover`（应用侧入口）

```text
updater.exe --recover --target <DIR> [--pid <PID>] [--launch <EXE>] [--args <S>] [--timeout N] [--log PATH]
```

### 2.1 流程与退出码

- 仅需 `--target`；**不读 `--zip`、不要求 `--sig`**，一切依据 Journal 自证。
- 流程：绝对化 → **确保 `.updater/` 存在** → 取锁（失败 → 退出 2）→ **若给出 `--pid` 先等待目标进程退出** → §1.3 的判定（含**成功后拉起**）→ 退出。
- **退出码**：恢复动作成功执行完毕（含"无 Journal 可恢复"）→ `0`；恢复本身失败（IO 错误、Journal 与 `--target` 不符）→ `3` 且保留现场。取锁超时除外（`2`）。**不产生 `1`**。
- **与运行中目标进程的关系**：未给 `--pid` 时，若目标程序仍在运行，回滚会撞 `ERROR_SHARING_VIOLATION(32)`；重试耗尽后按"**稍后重试**"处理（保留 Journal、退出 3），**不代表现场损坏**——待应用退出后再次执行即可收敛。

### 2.2 用途

1. 与 L2 共同覆盖"看门狗也被杀"的场景；
2. 运维手工修复；
3. **应用启动自检（见 §2.3，硬性集成契约）**。

### 2.3 硬性集成契约（不可选）

> 主程序每次启动时，若检测到 `<target>/.updater/journal` 存在，**必须**先同步执行
> `updater.exe --recover --target <dir>` 并等待其退出码，再继续自身启动。

**为什么是硬性契约**：本设计承诺"任意时刻被强杀/终止/掉电均可确定性收敛"，而 L3 的触发条件是"**任意 updater 启动**"——这由调用方决定。L2 看门狗虽已交付（`SCOPE.md` §2.1），但它**同样可能被杀**（`crash_midway_L3` 场景），无法独自承担承诺。因此"调用方不集成启动自检"会让承诺不成立：Worker 在 `APPLYING` 阶段被杀、看门狗也未能接管时，目录停在半更新状态，而下次被拉起的是**半新半旧的程序**。

**结论**：本契约**不可选**（无论在 L2 是否可用的情况下）。它是 L3 的唯一入口，因此作为交付物（`SCOPE.md` §2.3）与 DoD 项强制要求。

---

## 3. 事务替换

### 3.1 为什么用 `ReplaceFileW`

放弃"先移走旧文件 → 再写新文件"，因为那会产生"旧版已走、新版未落"的**应用不存在窗口**。`ReplaceFileW` 在内核层一步完成"替换 + 旧文件挪至备份"，消灭该窗口，并**原子地产出**回滚所需的备份——这一点是 §1.2.1 备份对账策略成立的前提。

### 3.2 单文件替换流程

对每个待写文件 `rel`（非目录、未命中保护）：

```text
1. dst = <target>/<rel>
2. 确保 **dst 的父目录**存在（MkdirAll；新建目录在计划阶段已登记 DIR:<rel>，
   创建成功时追加建议性记录 DIRDONE:<rel>）
2b. bak = <BACKUP>/<rel> → **确保 bak 的父目录存在**（MkdirAll）
   ※ ReplaceFileW **不会**创建 lpBackupFileName 的父目录：目录缺失时直接失败
     （ERROR_PATH_NOT_FOUND(3) / ACCESS_DENIED(5)）并触发整包回滚。
   ※ bak 侧目录属事务私有结构，**不登记**进 Journal 的 DIR 集合——
     DIR 只用于"回滚时逆序删空目录"，且仅作用于 target 侧（§3.5 R1.D）。
   ※ 同理 tmp 侧也要建父目录：CreateFileW(CREATE_NEW) 不会创建目录。
       ⇒ 共**三处 MkdirAll**（dst / backup / tmp 的父目录），缺任一处即整包回滚。
3. tmp = <TMPDIR>/<rel>            ← 与 dst 同卷；文件属性 NORMAL
4. CreateFileW(tmp, GENERIC_WRITE, 0, NULL, CREATE_NEW,
               FILE_ATTRIBUTE_NORMAL, NULL)
   流式写入解压数据（同步累计实际字节数 → 超 --max-uncompressed 立即失败）
   FlushFileBuffers(tmp)  →  CloseHandle(tmp)
5. 在**此刻**判定 dst 是否存在（不用计划期的结论）：
   ├─ 存在且为普通文件
   │     ReplaceFileW(dst, tmp, <BACKUP>/<rel>,
   │                  REPLACEFILE_IGNORE_MERGE_ERRORS, NULL, NULL)
   │     成功 → 追加 MOVED:<rel>
   ├─ 存在但为目录 → 立即失败（触发回滚）
   └─ 不存在
         若 rel ∈ NEW   → MoveFileExW(tmp, dst, MOVEFILE_REPLACE_EXISTING)
                          成功 → 追加 ADDED:<rel>
         若 rel ∈ EXIST → 该路径在等待期间被外部删除：
                          先 MoveFileExW(tmp, dst, MOVEFILE_REPLACE_EXISTING)
                          成功 → 追加 ADDED:<rel>（回滚按 §3.5 残余说明处理）
6. 建议性记录按阶段迁移或收尾时 flush（不逐行 fsync，见 §1.2.1）
```

**暂存位置的唯一硬约束是"同卷"，不是"同目录"**。因此把所有暂存文件集中到 `<target>/.updater/tmp/<GEN>/`：

- 满足 `ReplaceFileW` 的同卷要求；
- **不再对文件设置 `FILE_ATTRIBUTE_HIDDEN`**：隐藏属性只加在 `.updater/tmp` 与 `.updater/backup` **目录**上（`SetFileAttributesW` 作用于目录，不传播到文件）；
- 目标树在事务期间保持干净，崩溃残留集中一处，便于 GC。

**隐藏属性的机制澄清**：`ReplaceFileW` 会"合并**被替换文件**的信息（属性与 ACL）到替换文件"，因此覆盖分支一般不会把 `tmp` 的隐藏属性带过去；真正会传播的是 `MoveFileExW` 分支（新增文件）。用"隐藏目录 + 文件属性 NORMAL"同时消除两条路径的风险，无需在改名前额外 `SetFileAttributesW`。

**执行顺序**：按 zip 条目顺序**串行逐文件**（提取 → 替换 → 记录），不做"先全部提取再全部替换"。峰值额外占用约为单文件大小。

**备份目录**：`<BACKUP>` = `<target>/.updater/backup/<GEN>`，结构镜像 `rel`；目录属性 Hidden + System。

### 3.3 `ReplaceFileW` 的真实语义与错误码

**澄清**：`ReplaceFileW` 并不比普通替换"更容忍文件被占用"。实现上它以 `GENERIC_READ | DELETE | SYNCHRONIZE` 打开被替换文件并要求 `FILE_SHARE_DELETE`；只要另有进程以**不共享删除**的方式打开（杀软、备份工具、编辑器、同步盘），立即失败 `ERROR_SHARING_VIOLATION (32)`。其真实优势只有两点：**原子性**与**无空窗期**。

**硬约束**：替换文件、被替换文件、备份文件**必须同卷**。

| 错误码 | 含义 | 处置 |
| :--- | :--- | :--- |
| `0` | 成功 | 追加 `MOVED` / `ADDED` |
| `32` `ERROR_SHARING_VIOLATION` | 被其它进程占用 | **可重试**（`--write-retries` / `--write-delay-ms`） |
| `5` `ERROR_ACCESS_DENIED` | 权限不足 | **可重试**（次数同 `--write-retries`）；仍失败 → 该文件失败。**注意：锁文件路径下的 5 还可能表示 `DELETE_PENDING` 瞬态**，在锁那一侧必须按可重试处理 |
| `1175` `ERROR_UNABLE_TO_REMOVE_REPLACED` | 被替换文件无法删除 | **可重试**；两文件均保持原名 |
| `1176` `ERROR_UNABLE_TO_MOVE_REPLACEMENT` | 替换文件无法改名 | 重试；重试前确认 dst 与 tmp 均仍存在 |
| `1177` `ERROR_UNABLE_TO_MOVE_REPLACEMENT_2` | 替换文件无法移走，但已继承原文件流 | **不盲目重试**：状态已半迁移，立即失败并触发回滚（旧文件已在备份名下，可被 §3.5 对账还原） |
| 其它 | — | 立即失败并触发回滚 |

**重试预算**：默认 `--write-retries 20` × `--write-delay-ms 500` ≈ **单文件 10 秒**。

- 理由：真实占用者多是**杀软实时扫描、同步盘（OneDrive 等）、索引服务、编辑器自动保存**，其持锁时间常在数秒到数十秒量级；而"重试耗尽"的代价是**整包回滚 + 用户可见的更新失败**，用 3 秒去赌对方尽快放手并不划算。
- **间隔固定**：Tier 0 不允许随机化或复杂退避——不确定的等待会让崩溃窗口测试无法复现。
- **回滚使用同一组参数**，避免出现"回滚比安装更急"的错位。
- 仍未成功时，应优先依据 RM 诊断（`RUNTIME.md` §2.3）定位占用者，而不是继续加大重试。

### 3.4 提交前自检

在把 Journal 切到 `COMMITTED` 之前必须全部通过。**这是"全量回滚"承诺的最后一道闸门**，因此采取"宁可回滚，不可带病提交"的立场。

| 序 | 检查 | 依据 | 失败后果 |
| :--- | :--- | :--- | :--- |
| 1 | **逐文件哈希复核**（默认开启） | 包清单 `files[].sha256`，**校验集合见下** | 回滚 |
| 2 | **逐文件大小复核** | 包清单 `files[].size` | 回滚 |
| 3 | **`--require` 全量复核** | 调用方声明 | 回滚（存在即可，**允许 0 字节**） |
| 4 | **`--launch` 入口校验** | PE 结构 | 回滚（存在、可读、`MZ` + `e_lfanew` 处 `PE\0\0`） |
| 5 | **解压总量一致** | 规划预期 | 回滚（防截断与静默短写） |
| 6 | **包内 EXE/DLL Authenticode**（可选，Phase 7 搁置） | `--verify-authenticode` | 回滚 |

**被校验集合的定义（第 1/2 项）：校验集合 = "计划内实际写入的文件"，不是"清单里的全部条目"。** 必须排除以下三类，否则会出现"自己把自己的包判成坏包"：

1. **受保护而被跳过**的条目——最典型的是 `<target>/.updatekeep`：它**随应用打包**，但按保护语义"命中即既不覆盖也不删除"，磁盘上留的是**旧内容**。若把它纳入复核，任何"新版改了 `.updatekeep`"的合法包都会自检失败 → 整包回滚（且每次重试都失败）。同类还有 `--keep` 命中的任意文件、以及内部保留名。
2. **`--strip` 剥离后与清单路径不同名**的条目——比对前按 §5.4.2 的"清单路径基准"施加**同一 strip 规则**映射，映射不到的一律不进集合。
3. 清单中存在、但**不在本次计划内**（例如同路径大小写变体已被去重折叠）的条目。

> 换言之：复核的作用是"证明我写下去的东西没被改坏"，不是"证明包与磁盘完全一致"——后者是打包侧的职责。

**为什么值得逐文件重算哈希**：只校验 `MZ` 幻数时，一个**被截断或静默短写的 DLL** 完全可能通过。有了包清单后，可以在提交前逐文件重算哈希，把"磁盘写坏 / 杀软改写 / 静默短写"这类问题从"应用下次启动时崩溃"变成"本次更新即回滚"。

- 代价：提交前需要重读一遍已写入的文件（分页缓存通常命中，代价可接受）；
- 逃生开关：`--skip-hash-verify`（用于超大包或诊断场景）——**但它必须记 WARNING，且报告与 `END` 行中标注 `unverified`**，避免"静默降级"。

任一项失败 → 触发回滚，退出 1（回滚失败则 3）。

### 3.5 回滚算法（备份对账 + 权威计划集）

```text
输入：Journal（未提交）、GEN、TARGET、BACKUP、TMPDIR、EXIST / NEW / DIR 集合

Phase R1 — 对账（覆盖阶段，不依赖建议性记录）
  A. 备份目录对账：枚举 <BACKUP> 子树中的全部文件（相对路径 P），逐个还原：
       <TARGET>/P 存在 → ReplaceFileW(<TARGET>/P, <BACKUP>/P, NULL, ...)
       不存在           → MoveFileExW(<BACKUP>/P, <TARGET>/P, 0)
     ※ 本段即 **restore.rs 的唯一还原例程 restore_from()**：
       §4 的 --rollback-previous 调用的是**同一个函数**，不得另写一份。
     ※ **函数签名固定为 `restore_from(source_dir, excludes)`**：
       `excludes` 由调用方给出——§3.5 R1 传空集，§4 传 `{_meta.txt}`
       （`previous/<GEN>/` 内除旧版本文件外还有 `_meta.txt`，它**不是**待还原内容）。
       **不得**在函数内部按调用方身份分支，否则就出现了"第二套实现"（I10 的幂等同源会被打破）。
     每个操作独立执行 --write-retries 次重试（--write-delay-ms 间隔）。
     ※ 备份目录中只有事务前的旧版本，无条件还原本就正确；
       即使动作已完成而记录丢失（崩溃窗口），还原同样正确：覆盖新版、回到旧版。
  B. 新增集合清理：对每个 NEW:<rel>
       若 <BACKUP>/<rel> **不存在**（未被 A 处理，说明它是纯新增）
         且 <TARGET>/<rel> 存在 → 删除
       （若 <BACKUP>/<rel> 存在，说明 apply 期已降级为覆盖类，由 A 处理，此处必须跳过）
  C. 临时目录清理：删除 <TMPDIR> 全树
  D. 目录清理：对 DIR 集合**逆序**删除空目录（非空则跳过）

Phase R2 — 收敛判定
  全部成功 → 删除 <BACKUP>（应已空）→ 删除 <TARGET>/.updater/journal → 返回"回滚成功"
  否则     → **保留 Journal 与 <BACKUP>** → 返回"回滚未完成"
             （后续 L2/L3 可继续尝试；Journal 存在是继续收敛的唯一入口）

重试与容错策略
  · 每个文件操作独立重试，**单个文件失败不中断其余文件**——尽力最大化恢复面积；
  · 全部尝试完毕后若仍有失败项 → 判为"回滚未完成"（退出 3），保留现场；
  · 1177 在回滚中出现 → 记为本次重试失败并继续重试；重试耗尽后标记该文件失败，
    不中止其它文件；
  · R1.A 的失败不影响 B/C/D 的执行（B 依赖"备份中不存在"这一存在性判定，而非计数）。

幂等性：任意一步重复执行结果收敛。A 中已还原的条目（备份侧文件已消失）在下次运行时自然跳过；
B 中已删除的文件再次删除是无操作。

退出码映射：回滚成功 → 1（并兜底拉起旧版）；回滚未完成 → 3（保留现场、**不拉起**）。
L2/L3 触发的回滚不直接产生进程退出码，由所在进程按 §1.4 / §2.1 规则退出。
```

**已知残余（如实记录，不宣称绝对完美）**

1. **"计划为 `EXIST`，但 apply 时已被外部删除"** 的路径：会被记为 `ADDED`，但备份目录中无对应条目，`EXIST` 集合又不参与删除判定 → 回滚后该新文件留在原位（应用自己删了旧文件、我们补了新文件）。判定为可接受：存在性变更由应用自身造成，且留在原位的是包内同版本的正确文件，不构成"半新半旧"。
2. **空目录残留**：`DIR` 集合之外的目录（例如包内纯目录条目父链上的中间目录）可能残留空壳，不影响运行，由下次更新覆写或人工清理。
3. **掉电语义的边界**："任意时刻收敛"实际建立在"内核随进程终止关闭句柄" + "NTFS 元数据日志保证目录项操作的原子性/顺序"之上。Windows **不提供应用层可用的目录 fsync**，因此"目录项持久化顺序"不被应用层担保。把承诺表述为"进程被强杀/终止"是准确的；把它读作"任意掉电时序下目录项顺序均可证明"则超出本设计的担保范围。
4. **回滚期间的"假性失败"**：目标程序仍在运行时，回滚会因 `32` 重试耗尽而判"回滚未完成"（保留 Journal、退出 3）。**此时现场是健康的**，只是文件被占用；用 `--recover --pid <PID>` 等待应用退出后重试即可收敛。因此**调用方不应把退出码 3 直接等同于"目录已损坏"**——它同时覆盖"拉不起来"与"暂时回滚不动"两类情形。

### 3.6 提交收尾：保留上一版本与 Journal 删除顺序

```text
1. STAGE:COMMITTED 追加并 fsync          ← 提交点，此后绝不回滚（I2）
2. 原子写入 <target>/.updater/state（内容含 TOVER 与 GEN）
     失败 → 重试 3 次 × 200ms；仍失败 → WARNING + END 行标注
     （可安全容忍：Journal 的 TOVER 仍 durable，下次启动由恢复层补写）
2b. 若 --previous-ttl-days > 0：写入 <BACKUP>/_meta.txt（**必须在删除 Journal 之前**）
     VERSION:<TOVER>
     TSA:<提交时间>
     ADDED:<rel>    ← 逐条来自 Journal 的**权威 NEW 集合**，且仅取
                      "备份目录中不存在同名文件"的条目
                      （与 §3.5 R1.B 的存在性判定同源；理由见下方铁律）
     fsync 后关闭。写入失败 → WARNING，**并且不得删除 Journal**
3. 处置 <BACKUP>
   ├─ --previous-ttl-days == 0 → 删除（旧行为）
   └─ 否则 → MoveFileExW 重命名为 <target>/.updater/previous/<GEN>
        成功 → 4
        失败（句柄延迟释放）→ 重试 5 次 × 200ms
            成功 → 4
            仍失败 → WARNING，**保留 COMMITTED Journal**，然后**跳到 5**
4. 删除 Journal —— **仅当步骤 2b 与步骤 3 都成功**时执行；
   任一步失败都必须**跳过本步**，交由下次启动的 COMMITTED 分支幂等收尾
5. 继续后续步骤（清理 zip、拉起主程序）—— 已提交绝不回滚
```

**铁律（I3）**：`<BACKUP>` **成功转移（或删除）之前不得删除 Journal**。否则一旦失败，下次启动无 Journal 可依，备份目录将**永久泄漏**且无任何线索。

**铁律：`_meta.txt` 也必须在删除 Journal 之前落盘，且其 `ADDED` 只能取自权威集。** `ADDED` 决定"回退时该删哪些"新版独有文件。运行期的 `ADDED:` / `MOVED:` 行属**建议集**（不逐行 fsync，崩溃即丢），若据其生成 `_meta.txt`，一旦该记录在崩溃中丢失，回退完成后就会留下**新版独有的孤儿文件**——这正是 I1/I2 要防的"半新半旧"。因此：

- `ADDED` 必须来自 PLANNED 阶段已 durable 的**权威 `NEW` 集合**；
- 并须与"备份目录中是否已有同名文件"做**存在性判定**（与 §3.5 R1.B 同一逻辑），避免把"apply 期已降级为覆盖类"的条目误记为新增；
- `_meta.txt` 本身位于 `<BACKUP>` 内，会被 §3.5 R1.A 的"枚举全部文件"扫到，因此 §4 调用 `restore_from` 时必须传入 `excludes = {_meta.txt}`。

**为什么"转移"与"删除"风险相同却更好**：`MoveFileExW` 同卷重命名是元数据操作，与删除同样轻量，但它**保留了旧版本**——把"新版启动即崩"从**事故**降级为**可自愈事件**。这正是 Squirrel "保留当前 + 上一版用于回滚"的做法。

**与 GEN 的配合**：若第 3 步反复失败，残留的是"带 `GEN` 的代目录 + COMMITTED Journal"，下次启动由 §1.2.2 的 GC 规则或 §1.4 的 COMMITTED 分支清理，二者都只作用于被引用的代。

---

## 4. 版本回退 `--rollback-previous`（已实现）

**目的**：当新版本本身有缺陷（启动即崩、功能不可用）时，能在**不重新下载包**的前提下退回上一版本。

> **状态：本能力暂缓实现（首发不交付）。** 首发仍**保留** `previous/` 备份与 `_meta.txt`（§3.6 步骤 2b / 3）——它只是同卷 rename，代价极低，且对"新版启动即崩"有实际的灾难备查价值。坏版本的兜底路径改用 `CONTRACT.md` §5.2 的"**重下旧包 + `--allow-downgrade`**"。本节规格完整保留，作为延后实现的依据。
> 判定依据：延后原则是"**其缺失不会导致目录损坏**"——`previous/` 缺失时最坏情况是"回退需要重新下载"，不是"目录损坏"。

### 4.1 关键设计：复用事务引擎，不写第二套回滚

```text
updater.exe --rollback-previous --target <DIR> [--launch <EXE>] [--args <S>] [--log PATH]
```

```text
1. 取锁（同一个 lockfile::acquire）
2. 定位来源：<target>/.updater/previous/ 下取 _meta.txt 中 TSA 最新的一个目录，读取其 _meta.txt
     VERSION:<回退后应显示的版本>
     ADDED:<rel>            ← 本次（即"上一版本→当前版本"那次更新）新增的文件，
                               回退时需删除，否则会留下新版独有的文件
     TSA:<提交时间>
   若不存在保留目录 → 日志 INFO "无保留版本可退"，退出 0
3. 构建 plan：
     · 来源目录中的每个文件（除 _meta.txt）→ 覆盖写 target（走 §3.2 的同一套 ReplaceFileW 流程）
     · _meta.txt 中的每个 ADDED:<rel>       → 删除 target/<rel>
4. 执行：写 Journal(PLANNED/APPLYING) → 应用 plan → §3.4 自检 → COMMITTED
     ※ 会产生新的 <BACKUP>，提交后按 §3.6 转入 .updater/previous/<新GEN>
       ⇒ 因此回退本身也是**可再回退**的（可来回切换），且当前版本被自动保留
5. 恢复 .updater/state 为 _meta.txt 的 VERSION
6. 清理来源目录（已还原，不再需要）
7. 按 `RUNTIME.md` §5 拉起主程序
```

**为什么必须写 Journal**：回退本身也是一次多文件替换，同样可能被强杀/断电。若绕过事务引擎，我们就有了**第二条需要独立证明正确性的恢复路径**——这正是"禁止第二套实现"明令禁止的。复用后，`--rollback-previous` 自动获得三层恢复、备份对账、GEN 隔离的全部保障。

### 4.2 已知边界

- 只能回退**一次**（到最近一次提交前）。更早的版本已按 TTL 清理；
- 若上一版本是"首次安装"（即没有更早的版本），则不存在保留目录，回退报告"无保留版本可退"；
- 回退不还原用户数据（用户数据从未被更新触碰）；
- **代选择**：取 `previous/` 下 `_meta.txt` 中 `TSA` **最新**的代；`GEN` 字典序仅作为 `_meta.txt` 缺失时的回退手段（系统时钟回拨会让字典序失真，选错代意味着回退到更早的版本）。选中的 GEN 必须写进日志。
- **触发路径（契约）**：updater **不会自行回退**（I6 明确 `previous/` 不参与恢复判定），因此**必须由调用方提供触发点**。推荐：应用连续 N 次启动失败（或启动后 N 秒内崩退）即调用 `updater.exe --rollback-previous --target <dir>`。若调用方不提供触发点，"启动即崩"的现场将没有任何可自动执行的救援动作。
- **防重装（契约）**：回退后 `.updater/state` 的版本变低，而**同一个坏包仍满足 `pkg_ver > cur_ver`** → 下一次更新会把它**再装一遍**（形成"回退 → 被覆盖 → 再回退"的循环）。因此回退必须与"否决该版本"配对：调用方用 `--min-version` 钉住被否决的版本，或由服务端撤回该版本（kill switch 属服务端职责）。
- **回退也可被强杀**：回退本身走同一套事务（写 Journal、有 `<BACKUP>`、有自检），因此 L1/L2/L3 三层恢复同样适用；回退事务提交后也会产生自己的 `previous/<新GEN>` 与 `_meta.txt`，这使"回退可再回退"成立——两代的 `_meta.txt` 都按 §3.6 步骤 2b 的规则生成（`ADDED` 取自该次事务的权威 `NEW` 集合）。
