# lite 基线冻结（lite-baseline-20260916）

> 冻结时间：**2026-09-16 09:38:40 +0800**
> 来源：`C:\Home\Projects\mytools\tools\updater\`（用户机本题路径，已不再维护）
> 冻结动作：**只读复制**，源目录未做任何修改。

---

## 为什么冻结

兄弟项目 `updater/`（lite 版）**已停止维护**，即将被归档删除，以结束"维护两套"的局面。
删除之后，本项目里所有"lite 是什么行为"的判断都将失去依据，因此先把参照物落到本目录。

> 原本还计划用一个 `--lite` 档位去吸收 lite 的行为，该档位**已于同日实现后撤销**
> （理由见 [`../../../CHANGES-20260916.md`](../../../CHANGES-20260916.md) §5，
> 被否决的实现留档在 [`../lite-profile-20260916-superseded/`](../lite-profile-20260916-superseded/)）。
> 这**不影响本归档的价值**：它仍是"lite 到底做了什么"的唯一权威遗留。

删除 `updater/` 会让以下引用**悬空或失去依据**，因此必须在删除前把参照物落到本目录：

| 悬空对象 | 位置 |
| :--- | :--- |
| lite 设计文档链接 | `docs/SCOPE.md` §5 → `../../updater/docs/rust-updater-architecture-design-lite.md` |
| lite 兼容性复核依据 | `docs/updater-vs-better-updater-reliability-recheck.md`（逐文件通读 lite 源码的产物，引用行号） |
| `--lite` 实施期的语义对照源 | 本归档的 `rust-src/` |
| 行为等价性复核所需的参照二进制 | 本项目**不入库**该文件（仓库根 `.gitignore` 忽略 `*.exe`）；从 `rust-src/` 重建后与下方记录的 sha256 比对 |

---

## 目录内容

| 路径 | 内容 | 来源 |
| :--- | :--- | :--- |
| `rust-src/src/` | lite 全部 9 个源文件（1821 行） | `updater/rust/src/` |
| `rust-src/examples/mock_host.rs` | 调用方示例 | `updater/rust/examples/` |
| `rust-src/Cargo.toml` / `Cargo.lock` | 构建定义与锁定依赖 | `updater/rust/` |
| `rust-src/README.md` | lite 使用指南（含各语言调用示例） | `updater/rust/` |
| `docs/` | lite 设计文档、兼容性对比、变更时间线 | `updater/docs/` |
| `MANIFEST.sha256` | 全部 **17 个**被归档文件的 SHA256（不含本 README 与 `.gitattributes` 自身） | 本目录 |

**未归档**：

- `bin/updater-lite-20260916.exe`（参照二进制）——仓库根 `.gitignore` 忽略 `*.exe` 与 `**/bin/`，
  这是整个 monorepo 对构建产物的既有约定，本归档**不与之对抗**；
  该二进制可由 `rust-src/` 用 `cargo build --release` 在数秒内重建（见下方"参照二进制"）。
- `updater/go/`（历史 Go 版，非本次平替对象）、`updater/docs/temp/`（临时产物）。

---

## 完整性校验

在本目录执行：

```bash
sha256sum -c MANIFEST.sha256
```

期望输出 **17 行 `OK`**，无 `FAILED`，且**无格式警告**。任一项不符即表示基线被污染，**先查清再继续**。

> `MANIFEST.sha256` 刻意只覆盖**会被提交的文件**，且**排除自身与两个说明文件**
> （本 `README.md`、`.gitattributes`）——清单不自我引用，避免"改了说明就跑不过校验"。
> 它也不含任何需现场生成的产物：一份在干净 clone 上注定失败（或注定报警告）的清单，比没有清单更糟。
> 另：不写任何注释行（`sha256sum -c` 对非哈希行报 `improperly formatted`），说明文字全部留在本 README。

关键锚点：

| 文件 | SHA256 |
| :--- | :--- |
| `rust-src/src/transaction.rs` | `f2ec862e28c31dc693b568d9dfa1668186a9b54b5bddb32d920c3993faacf056` |
| `rust-src/src/keep.rs` | `67ae9df5de4f2373244cf5c552a65c0a4ac393c2d54019551dc97f9eb9d4dee3` |
| `rust-src/src/args.rs` | `60766d5e89e64286687c76d4c66e4ba122a72505e037ce8084439afcd0ad12f1` |

### 参照二进制（未入库，可重建）

需要一份**可运行的** lite 参照时，用归档源码重建：

```bash
cd rust-src && cargo build --release
sha256sum target/release/updater.exe
```

冻结时（2026-09-14 19:04 的构建产物）的期望值：

| 项 | 值 |
| :--- | :--- |
| 大小 | `374784` bytes |
| SHA256 | `d0c27bbf84bca75db1bdf44ac703d49cff9ba3795e7487c01a0a21f984ef200f` |

> ⚠️ 重建结果**不保证与上表逐字节相同**（编译器版本、路径、时间戳都会影响产物）。
> 上表是"当时那个二进制长什么样"的记录，用于判断手里那份是不是同一件东西；
> 它**不是**可复现构建的承诺。要判断语义是否等价，请以 `rust-src/src/` 的源码为准。

> ⚠️ 本归档**不用于发布**。它是参照物，不是交付物。

---

## 使用约束

1. **只读**——本目录是冻结基线，**禁止任何修改**。改它等于销毁参照物，
   "lite 到底做了什么"就再也无从查证。
2. **禁止当作实现来源**——不要从这里复制代码进 `src/`。任何"吸收 lite 行为"的实现都必须复用
   `better-updater` 现有的单一实现（事务引擎 `apply_file` / `rollback` / `restore_from` 一行不改），
   这是 `SCOPE.md` §4.3「禁止第二套实现」的硬要求。本归档只用于**对照语义**。
3. **`docs/` 内的 4 个文档是历史快照**，其中的链接指向已不存在的 `updater/` 路径，属预期。

---

## lite 行为速查（迁移对照用）

从 lite 迁到 `better-updater` 时需要的 lite 行为要点（以归档源码为准，逐条可回溯）：

| 关注点 | lite 行为 | 源码位置 |
| :--- | :--- | :--- |
| 进程模型 | 单进程同步，不派生任何子进程，退出码 = 真实结果 | `main.rs` |
| 回滚机制 | 进程内 `rollback_stack`（`Vec<RollbackAction>`），进程被杀即失效 | `transaction.rs` |
| 工作目录 | `.updater_tmp/` `.updater_bak/` `.updater.lock`，成功后全部清除 | `transaction.rs` / `keep.rs` |
| 退出码 | `0` 成功 / `1` 一切失败（含等待超时）/ `2` 用法错误 | `main.rs:17-19` |
| 崩溃自愈 | **无**。无 Journal、无 `--recover` 入口 | — |
| 版本防护 | 无（无 state，无降级校验） | — |
| 提交前自检 | 无逐文件哈希复核 | `preflight.rs` |
| keep 规则 | `--keep-file` 保护**任意深度**同名文件；glob 匹配**任一中间路径段** | `keep.rs:57-63` |
| 内部保留名 | `.updater` `.updater_tmp` `.updater_bak` `.updater.lock` | `keep.rs:48-55` |
| 日志 | 恒写 `%TEMP%\updater-<unix秒>.log`，格式 `[unix秒] LEVEL: msg` | `main.rs:45-52` |
| 自保护 | updater 在 target 内 → 无条件跳过自身 | `keep.rs:64-68` |

> ⚠️ 两边**退出码语义不同**：`better-updater` 是 `0/1/2/3`（`2`=中止、`1`=已回滚、`3`=已提交但未拉起），
> lite 只有 `0/1/2`。迁移时解析退出码的代码必须改写——具体清单见
> `docs/updater-vs-better-updater-reliability-recheck.md` §2（9 处行为差异）。
