# PROPOSAL — 内置打包能力（`packer`）

> 状态：**已决策并已实施（2026-09-16）**。采纳形态 **(b) 同 crate 第二 bin + feature gate**。
> 实施结果见 §9；决策见 §7；计划见 §8。
> 起因：第三方易用性反馈第 4 条（详见 `CHANGES-20260916.md`）。
> 结论摘要：动机成立，但"把 pack 放进 `updater.exe`"不是最优形态——见 §3 的三条实测约束与 §4 的推荐形态。

---

## 1. 反馈原文的诉求

> better-updater 非常用心地设计了 `gen_manifest.ps1` 和 `release_pack.ps1`（启动闭包连续排在 zip 末尾 + 回读校验）。
> 但许多大型开源应用（Flutter、Electron、Qt）的 CI/CD 或打包脚本是用 Python、Bash 或 Node.js 写的，
> 在跨平台构建容器或特定机器上调用 `.ps1` 比较别扭。
> 建议：让 `updater.exe` 自身直接内置 `updater.exe pack --stage <dir> --version <ver> --out <zip>`。

诉求可以拆成两条，**它们的解法不同**，混在一起会选错形态：

| # | 诉求 | 真正的障碍 |
| :-: | :--- | :--- |
| A | 不要为打包写第二套逻辑（清单 + 闭包顺序 + 校验） | 现在逻辑在 PowerShell 里，调用方换语言就得重写一遍 |
| B | 在**非 Windows** 构建容器（Linux CI、Docker、GitHub Actions ubuntu-latest）里也能打包 | `.ps1` 需要 PowerShell；**而 `updater.exe` 是 Windows PE，在 Linux 上同样跑不了** |

**关键判断**：把 pack 塞进 `updater.exe` 只解决 A，**不解决 B** —— 在 Linux 构建容器里你依然无法执行它。
而 B 往往才是"跨平台 CI 别扭"的真实痛点（Flutter / Electron / Qt 的产物经常在 Linux runner 上组装）。

---

## 2. 现状盘点（要搬的东西到底是什么）

| 现有物 | 职责 | 规模 |
| :--- | :--- | :--- |
| `scripts/gen_manifest.ps1` | 遍历 stage → 逐文件 SHA-256 → 写 `updater.manifest`（LF/UTF-8 无 BOM） | 55 行 |
| `scripts/release_pack.ps1` | 清单 → 闭包判定 → 压缩（条目顺序）→ 产物自检（清单一致性 + 闭包连续收尾）→ sha256 | 232 行 |
| `stage/.bootclosure` | 覆盖"启动闭包"的默认附加模式（`data/*.so`、`data/*.dat`） | 约定 |

两条**不可丢**的正确性约束：

1. **顺序**：先写清单 → 再压缩。清单与 zip 内容不一致 ⇒ 更新器提交前哈希自检失败 ⇒ 全量回滚。
2. **闭包连续收尾**：启动闭包（根目录直系文件 + `data/*.so` / `data/*.dat`）必须**连续排在 zip 末尾**，
   把"中断落在半新半旧且起不来"的概率从事务跨度压到闭包跨度（实测 94/94 → 10/95）。

---

## 3. 三条实测约束（本次已在本机跑出数）

### 3.1 依赖：**不需要新增**（已实证）

用项目现有的 `zip` + `flate2` feature 组合（`deflate-flate2` + `flate2` + `rust_backend`）直接写 Deflate 条目、
写空目录条目，均成功（338 B 的探测包，读到回）。⇒ **直接依赖数保持 7/7，不会触发 `check_tiers.ps1` 的硬失败。**

### 3.2 体积：写侧要被**引用它的那个二进制**付约 **54 KB**（已实测）

在项目自身的 `[profile.release]`（`opt-level="z"` + `lto` + `codegen-units=1` + `strip`）下，
同 crate 的"只读"与"读+写"两个二进制对比：

| 二进制 | 体积 |
| :--- | ---: |
| 只用 zip 读侧（≈ 今天 `updater.exe` 的依赖面） | 218112 B |
| 再加 zip **写侧**（`ZipWriter` + Deflate 编码器 + 目录条目） | 271872 B |
| **边际增量** | **+53760 B ≈ +53.8 KB（+11.5%）** |

**这 54 KB 由"谁引用了写侧"承担**——这一点决定了形态选择：

- 形态 **(a) `updater.exe pack`**：写侧被更新器自己的 CLI 分派引用 ⇒ **每一个最终用户**都要付这 54 KB，
  而收益只落在构建期（发布流水线）。把构建期工具的成本摊进运行期产物，方向上是拧的。
- 形态 **(b) 独立 `packer` bin**：写侧只被 `packer.exe` 引用 ⇒ `updater.exe` **一分钱不涨**
  （2026-09-16 实测：带 `--features pack` 与默认 feature 构建出的 `updater.exe` **大小完全相同**，479744 B；
  详见 §9.5）。

预算上限 750 KB 对两种形态都不是问题；这一节的结论是"**别把构建期能力的成本转嫁给最终用户**"。

### 3.3 复杂度：Tier 0 已在超线状态

`check_tiers.ps1` 当前报 `Tier 0 产品代码 2879 行 > 2600（触发架构评审）`，
而 pack 的 CLI 接线（`cli.rs` 解析 + `main.rs` 分派）恰好都落在 Tier 0/Tier 1 的公共路径上。
另：`SCOPE.md` §4.3 **明令禁止第二套实现** —— 做了内置 pack，`scripts/*.ps1` 就必须**退役或改为薄壳**，
否则同一套规则躺在两个地方，正是该条禁止的情形。

---

## 4. 三个候选形态

| 形态 | 做法 | 依赖 | 产物体积 | 跨平台打包（诉求 B） | 单一实现 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **(a) 内置子命令**（反馈原提案） | `updater.exe pack …` 进主产物 | 7/7 不变 | **+53.8 KB** | ❌ 仍是 Windows PE | ✅ 需脚本退役 |
| **(b) 同 crate 的第二 bin**（**推荐**） | `src/bin/packer.rs` + `required-features = ["pack"]`，`cargo run --features pack --bin packer -- --stage …` | 7/7 不变（写侧只在 feature 开启时参与） | **主产物 0**（feature gate） | ✅ 可 `--target x86_64-unknown-linux-gnu` / `aarch64-apple-darwin` 交叉构建 | ✅ 需脚本退役 |
| (c) 维持现状 | 脚本保留，调用方自己适配 | 7/7 | 0 | ❌（但要跨平台就得自己写实现） | ❌ 调用方各写一套 |

### 为什么推荐 (b)

1. **直面诉求 B**：packer 是纯 std + zip + sha2 的**可移植**代码（不碰 Win32），
   同一份源码可为本机 / Linux / macOS 构建 —— 这才是"Python / Bash / Node 的 CI 都能调一条命令"的正解。
   `updater.exe pack` 做不到这件事。
2. **不向运行期产物收税**：主产物保持 466.5 KB；写侧那 54 KB 只出现在开发期二进制里。
3. **单一实现落地得很自然**：`release_pack.ps1` 与 `gen_manifest.ps1` **删除**，
   `verify.ps1`/CI 里改为 `cargo run --release --features pack --bin packer -- …`（一行）。
   规则（清单格式、闭包判定、顺序、机械守卫）只存在于 `src/packer/`。
4. **不触碰更新器主链**：`updater.exe` 的 CLI 面、退出码契约、`--help`、
   甚至"集成方可能误把 pack 当更新模式"的风险，全都不存在。

### (b) 的代价与需要接受的新东西

| 代价 | 说明 | 缓解 |
| :--- | :--- | :--- |
| 多一个构建目标 | 仓库从"1 个产物"变成"1 个发布产物 + 1 个开发期工具" | `required-features` 保证默认 `cargo build --release` 不构建它；`check_size.ps1` 只盯 `updater.exe` |
| 需要防"发布产物误带写侧" | 若哪天有人给 `default` 加上 `pack`，主产物就白胖 54 KB | 在 `check_tiers.ps1` 或 `check_size.ps1` 增设一条机械守卫：**默认 feature 构建不得包含 `pack` 模块**（符号/体积二选一，体积更简单：给 `updater.exe` 设一个 500 KB 的"默认 feature 上限"） |
| 交叉编译链 | Linux/macOS 目标需要 `rustup target add` + 链接器 | 只在需要跨平台打包的 CI 里做；不阻塞本机开发（本机直接 `cargo run --features pack`） |
| 行数归属 | 新增 Tier 1 模块（预估 300–450 行：遍历 + 清单 + 闭包 + zip 写 + 自检 + CLI） | 落在 Tier 1，**Tier 0 只增加 `main.rs` 里的一次模式分派（几行）**；`PLAN.md` 的 Tier 0 预算不受影响 |

---

## 5. 实现大纲（形态 (b) 的详细规格，配合 §8 使用）

> 本节是 §8 阶段 1–2 的**逐条规格**；"必须成立"的不变量比目录结构更重要。

### 5.1 目录与命令

```
src/packer/
├── mod.rs         # 入口：run(args) -> i32；编排「清单 → 顺序 → 压缩 → 自检」
├── walk.rs        # [T1] 遍历 stage：排除 .updater\、.bootclosure、updater.manifest 自身（与现有脚本同一套排除规则）
├── manifest.rs    # [T1] 生成 updater.manifest（复用 pkg::manifest 的格式常量，避免两份格式定义）
├── closure.rs     # [T1] 启动闭包判定（默认 data/*.so、data/*.dat + .bootclosure 覆盖）
└── zip_write.rs   # [T1] 按顺序写 zip（Store/Deflate）+ 空目录条目
src/bin/packer.rs  # [T1] CLI：--stage --version [--min-upgradable-from] [--app-version] [--out] [--force] [--sign?]
```

命令形态（保持与脚本语义一致，便于迁移）：

```
packer --stage <dir> --version <ver> --out <zip> [--min-upgradable-from <ver>] [--app-version <ver>] [--force]
```

### 5.2 必须保持的不变量（与现有脚本逐条对齐）

| # | 不变量 | 落地方式 |
| :-: | :--- | :--- |
| P1 | **先清单后压缩** | 单函数内固定顺序 + 注释说明为什么不可颠倒 |
| P2 | 闭包**连续收尾** | 顺序由算法确定性产生；**写后再回读 zip 中央目录自检**（把 PowerShell 里的机械守卫原样搬过来） |
| P3 | 相对路径分隔符归一化为 `\` | 清单内一律 `\`；zip 条目一律 `/`（跨平台跑时最容易错的一处） |
| P4 | 清单格式：`MANIFEST:1` / `VERSION:` / `MIN_UPGRADABLE_FROM:` / `FILE:rel\|size\|sha256` / `DIR:rel`，LF + UTF-8 无 BOM | 与 `pkg::manifest::parse` 用同一份常量 |
| P5 | `--version` 与 `--app-version` 不一致 ⇒ 拒绝 | 与脚本一致（三处版本必须一致，否则降级防护误判） |
| P6 | 输出已存在且无 `--force` ⇒ 拒绝 | 与脚本一致 |
| P7 | 签名未启用 ⇒ 显式报错而非静默跳过 | 与 `release_pack.ps1` 的 `-Sign` 行为一致 |
| P8 | 产物摘要打印 `--sha256 <hash>`，供调用方传给更新器 | 与脚本一致 |

### 5.3 验收（写进 `TESTING.md`）

1. **等价性**：同一 stage 目录分别用旧脚本与新 packer 打包 → 逐条目名/顺序/清单内容**完全一致**（sha256 可不同：时间戳/压缩实现细节不同是允许的，需在测试里明确边界）。
2. **闭包守卫**：变异测试 —— 把顺序改回纯字母序 ⇒ 打包必须失败（与脚本已有的变异测试同型）。
3. **跨平台**：在 Linux 目标上构建 packer 并对同一 stage 打包成功（`cargo build --target x86_64-unknown-linux-gnu --features pack`）。
4. **产物守卫**：`cargo build --release`（默认 feature）产物**不含** pack 写侧（体积上限 500 KB 硬断言）。
5. **端到端**：packer 产物直接交给 `updater.exe` 跑一次真实更新成功（复用 `check_av.ps1` 的夹具路径）。

### 5.4 与现有脚本/文档的处置（按 D2：**暂不删，先并存**）

| 对象 | 现状处置 | 退役条件（真实项目跑通后） |
| :--- | :--- | :--- |
| `scripts/gen_manifest.ps1` | **保留**（参照物 + 兜底） | 删除，规则已在 `src/packer/manifest.rs` |
| `scripts/release_pack.ps1` | **保留**（参照物 + 兜底） | 删除；`MANUAL-VERIFICATION.md` 的 `New-Pkg` 改用 packer |
| `SCOPE.md` §2.3 第 7 条 | 补充"顺序规则有两个实现（packer 为目标实现，脚本为过渡参照），由等价性测试守住" | 改为只指向 packer |
| `USAGE.md` §7 / §13.3 | 增加 packer 命令与"两者产出等价"的说明 | 只留 packer |
| `.github/workflows/ci.yml` / `verify.ps1` | 增加"构建 packer + 跑 packer 测试（含等价性）"步骤 | 不变 |
| `check_size.ps1` | 新增"默认 feature 产物不得含写侧字面量"的形态守卫 | 不变 |

---

## 6. 备选：先做"最小可用"的顺序

若不想一次性投入 300–450 行，可按下面两步走（每步都能独立交付）：

1. **第一步（约 120 行，收益最大）**：只做 `packer` 的**闭包顺序 + 压缩 + 回读自检**，
   清单仍由 `gen_manifest.ps1` 生成（或直接调用现有 `pkg::manifest` 的生成函数）。
   这一步解决"CI 必须用 PowerShell 才能排出合规顺序"的主痛点，且**不动清单格式这件契约物**。
2. **第二步**：把清单生成并入，删除两个 `.ps1`，补齐等价性/变异/跨平台验收。

---

## 7. 决策结论（2026-09-16，项目方拍板）

| # | 问题 | 决策 | 落地含义 |
| :-: | :--- | :--- | :--- |
| D1 | 形态 | **(b) 同 crate 第二 bin + `required-features`** | `src/bin/packer.rs` + `src/packer/*`（Tier 1）；主产物 0 KB 增量 |
| D2 | 脚本处置 | **暂不删** —— 等 packer 在**真实项目**（jigsawpuzzle 迁移）上验证通过后再删 | 短期内两套并存 ⇒ **必须加机械守卫防漂移**（§8 阶段 3）：等价性测试必须绿，否则视为回归 |
| D3 | 跨平台目标 | **代码保持可移植（不用 Win32）；当前只保证 Windows 可用** | packer 不引入任何 `win32::` 依赖；Linux 目标"能构建"但不进验收；**macOS 不做**（其 app 更新机制是整目录替换，与本工具无关） |
| D4 | 节奏 | **一次性做完整**（实施时允许分阶段） | 按 §8 的 5 个阶段推进，每阶段独立验证 |

**D2 的补充说明（重要）**：`SCOPE.md` §4.3 禁止"第二套实现"。D2 使它变成**限期双轨**：
packer 是**目标实现**，两个 `.ps1` 是**过渡期参照物**，退役条件是"真实项目跑通"。
为避免这段窗口里两边悄悄漂移，等价性由**测试**守住（同 stage → 入口顺序与清单逐行一致），
而不是靠人记得同步改两处。

---

## 8. 实施计划（D1–D4 已批准）

### 阶段 0：结构前提 —— crate 拆成 lib + 两个 bin

第二 bin 要复用 `pkg::manifest` / `pkg::zip_read` / `keep` 等既有实现（这正是"单一实现"的关键），
必须先把模块声明从 `main.rs` 提到 `src/lib.rs`：

```
src/lib.rs          // [T0] 模块声明（原 main.rs 的 mod 列表）+ packer 的 feature gate
src/main.rs         // [T0] 只剩 updater 的启动序列，模块改走 `use updater::…`
src/bin/packer.rs   // [T1] 打包 CLI（required-features = ["pack"]）
src/packer/*.rs     // [T1] 打包实现（#[cfg(feature = "pack")]）
```

约束：`windows_subsystem = "windows"` 只能留在 updater 的 bin 里（packer 要能在终端输出）；
`updater` 的 lib 名与 bin 名同名是 Rust 合法且标准的形态。

### 阶段 1：packer 实现（Tier 1）

| 模块 | 职责 | 可复用 |
| :--- | :--- | :--- |
| `packer/walk.rs` | 遍历 stage，排除 `.updater\*`、`.bootclosure`（配置不入包） | — |
| `packer/manifest.rs` | 生成 `updater.manifest`（`MANIFEST:1` / `VERSION` / `MIN_UPGRADABLE_FROM` / `FILE:rel\|size\|sha256` / `DIR`，LF + UTF-8 无 BOM） | `verify::sha256::hash_file`（**已是流式**）、`pkg::manifest` 的格式口径 |
| `packer/closure.rs` | 启动闭包判定：根目录直系文件 + `data/*.so`/`*.dat`，可由 `.bootclosure` 覆盖 | — |
| `packer/zip_write.rs` | 写 zip：非闭包（字母序）→ 空目录条目 → **闭包连续收尾** | `zip` 写侧（已实测可用，无需新依赖） |
| `packer/mod.rs` | 编排：清单 → 顺序 → 压缩 → **回读自检** → 摘要 | **`pkg::zip_read::scan`（泛型，无 Win32）** ⇒ 复用更新器自己的"包合法性"判定：压缩方法约束、加密拒绝、Zip Slip、**大小写不敏感重复条目** |

> 复用 `zip_read::scan` 是本次设计的关键收益：自检不再只是"清单对得上"，
> 而是**直接用更新器接受包的那套规则**去验一遍 —— 比现有 PS 脚本更强，且没有第二套实现。
> 额外加一项脚本没有的检查：保留名（`keep::is_reserved`）条目 **WARNING**（不改变产物，保持与脚本等价）。

### 阶段 2：CLI

```
packer --stage <DIR> --version <VER> --out <ZIP>
       [--min-upgradable-from <VER>] [--app-version <VER>] [--force] [--sign]
```

- 与脚本逐条对齐的行为：`--version`/`--app-version` 不一致 ⇒ 拒绝；输出已存在且无 `--force` ⇒ 拒绝；
  `--sign` ⇒ **显式报错**（本期未启用签名，不静默跳过）；末尾打印 `--sha256 <hash>` 供调用方传给更新器。
- 退出码：`0` 成功；`1` 任一失败（就地停下）。

### 阶段 3：机械守卫（D2 的防漂移 + D1 的形态守卫）

| 守卫 | 内容 | 落点 |
| :--- | :--- | :--- |
| **等价性** | 同一 stage 分别用 `release_pack.ps1` 与 packer 打包 ⇒ **入口名序列完全一致 + `updater.manifest` 逐行一致** | `tests/packer.rs`（PowerShell 不可用时跳过，Linux 上不红） |
| **闭包变异** | 故意破坏顺序 ⇒ 打包必须失败并列出"闭包成员 vs 实际尾部"（与脚本已有变异测试同型） | `tests/packer.rs` |
| **形态** | `cargo build --release`（默认 feature）**不得**包含写侧：断言 `updater.exe` 里**不存在** packer 独有字面量 `--min-upgradable-from` | `scripts/check_size.ps1` |
| **端到端** | packer 的产物直接交给 `updater.exe` 跑一次真实更新成功 | 复用 `check_av.ps1` 夹具路径（改为可传入现成包） |
| **分层** | packer 模块 Tier 1、无 Win32 依赖；`Tier 0` 只增 `main.rs` 的分派（本次实际为 0，因为不分派） | `check_tiers.ps1`（现有机制覆盖） |

### 阶段 4：文档与留档

`SCOPE.md`（packer 属**开发期工具**、不进发布产物、脚本退役条件）、`PLAN.md`（模块与预算）、
`TESTING.md`（计数 + 新守卫）、`USAGE.md` §7/§13.3（打包命令 + 双轨说明）、`docs/README.md` 索引、`CHANGES-20260916.md`。

### 不做（明确划界）

- 不做 macOS 打包适配（D3）；
- 不做 Linux 目标的 CI 验收（代码可移植即可，避免为一个非目标引入交叉编译链）；
- 不在 `updater.exe` 里暴露 `pack` 子命令（D1）；
- 不改清单格式、zip 布局、闭包判定规则（这些是契约物，改它们就不是"搬实现"而是"改契约"）。

---

## 9. 实施结果（2026-09-16）

### 9.1 落了什么

| 产物 | 内容 |
| :--- | :--- |
| `src/lib.rs`（45 行，T0） | 库根：18 个模块声明 + `packer` 的 feature 门控 |
| `src/main.rs` | 只剩入口与编排（模块声明搬到 lib，改走 `use updater::…`） |
| `src/packer/{mod,walk,manifest,closure,zip_write}.rs` | Tier 1，**约 700 行产品代码**（含注释；比 §5 预估的 300–450 行高，主要是本项目的注释密度） |
| `src/bin/packer.rs`（42 行） | 控制台 CLI：解析 → 调 lib → 映射退出码 |
| `tests/packer.rs`（364 行） | 5 条集成用例（含等价性守卫） |
| `src/packer/*` 内的单测 | 11 条（闭包判定 4 / 排序与尾部 3 / 遍历与排除 2 / 清单往返 2 / 流式写 zip 1 / 参数校验 2，含 mod 内 2 条） |

### 9.2 实现中做的技术判断（都记录在代码注释里）

| # | 判断 | 理由 |
| :-: | :--- | :--- |
| 1 | **自检复用 `pkg::zip_read::scan`** | 它是泛型的、不碰 Win32 ⇒ packer 可直接用**更新器接受包的那套规则**验自己的产物（压缩方法约束、加密拒绝、Zip Slip、**大小写不敏感重复条目**）。比旧脚本的自检更强，且**没有第二套实现** |
| 2 | 清单列**全部**目录，zip 只写**空**目录 | 与旧脚本一致：`DIR:` 行是计划期建目录的依据；非空目录由文件动作顺带创建，写进 zip 只是冗余 |
| 3 | `.bootclosure` **两边都排除**（清单 + 包） | 它是打包配置、不入包（旧脚本的 `release_pack.ps1` 也是这么做的）；落在清单里会让清单不再是"包内容"的忠实描述。**旧脚本的 `gen_manifest.ps1` 恰恰漏了这条** ⇒ 见 9.3 |
| 4 | 排序用**字节序** | 跨平台确定；旧脚本的 `Sort-Object` 是区域敏感的。判等口径随之改为"集合 + 顺序无关的清单内容"，而不是"字节相同" |
| 5 | 隐藏文件**照常打包**并提示 | PowerShell 的 `Get-ChildItem` 默认跳过隐藏项，是它的无意行为、不是契约；静默丢文件比多打几个文件更危险。差异以 `[WARN]` 显式列出 |
| 6 | 写 zip **流式**（`io::copy`） | 与自检的流式哈希同一原则：整包可达 GB 级，不把文件读进内存 |
| 7 | `--sign` **显式报错** | 与旧脚本一致：宁可让 CI 报错，也不让 CI 以为包已签名 |

### 9.3 副产物：发现旧流水线的一个真实缺陷

`gen_manifest.ps1` 的排除表只有 `updater.manifest` 与 `.updater\*`，**没有排除 `.bootclosure`**，
于是它会把打包配置写进清单；而 `release_pack.ps1` 又把 `.bootclosure` 排除在 zip 之外
⇒ 旧脚本**自己的产物自检**报"清单声明了 .bootclosure，但包内不存在"并**拒绝出包**。

也就是说：**只要 stage 里放了 `.bootclosure`，旧流水线就打不出包**（而 `.bootclosure` 恰恰是
启动闭包规则唯一的覆盖入口）。packer 两边都排除，因此不受影响。
该缺陷已记录在 `USAGE.md` §2.1、`TESTING.md` §4 与 `CHANGES-20260916.md`；**脚本按 D2 暂不修改**，
等价性用例的夹具因此不含 `.bootclosure`（并在注释里写明原因）。

### 9.4 验证（全部实测）

| 项 | 结果 |
| :--- | :--- |
| `cargo test --all-targets` | **91 passed / 0 failed**（与实施前一致，未回归） |
| `cargo test --all-targets --features pack` | **107 passed / 0 failed**（+11 单测 +5 集成） |
| **端到端** | packer 产出的包交给 `updater.exe` → **真实更新成功**：文件就位、空目录被创建、状态写入、包元数据与 `.bootclosure` 均不落盘 |
| **与旧脚本等价** | 同一 stage 两边打包：条目集合（文件/目录）完全一致、清单内容逐行一致、闭包在两边都是连续尾部 |
| **形态守卫** | `check_size.ps1`：发布产物内**不存在** packer 独有字面量（写侧没被链进来） |
| clippy | 两轮（默认 / `--features pack`）`-D warnings` 均 0 |
| Tier 0 产品代码 | 2879 → **2910 行**（+31：`lib.rs` 的模块声明与文档；仍为"超 2600 → 架构评审 WARN"这一既有状态） |
| 发布体积 | 477696 → **479744 B（468.5 KB）**，+2 KB（来自 lib 拆分，不是写侧——写侧的 +54 KB 被 feature gate 挡在开发期产物里） |
| `packer.exe` | 366.5 KB（开发期工具，不计入发布预算） |
| 依赖 | 仍 **7/7**（写侧复用既有 `zip`+`flate2`） |
| 分层 | 41 个模块 Tier 声明齐全；`packer` 全部 Tier 1、**零 `win32::` 依赖**（可移植） |

### 9.5 一次构建两个产物（2026-09-16）

问题：能不能一条命令同时出 `updater.exe` 与 `packer.exe`？

**能，而且一条就够**：`cargo build --release --features pack` 构建本 package 的全部 bin，两个都出来。
`scripts/build.ps1` 因此只做一件事——把输出**收集**到 `target\dist\` 并打印大小与 sha256：

```powershell
pwsh -File scripts/build.ps1
#   updater.exe   468.5 KB
#   packer.exe    366.5 KB
```

**两条实测**（第二条推翻了本方案 §3.2 的一个隐含担忧，也推翻了本节初版的"分两次构建"做法）：

| # | 实测 | 结论 |
| :-: | :--- | :--- |
| 1 | 带 `--features pack` 构建出的 `updater.exe` 与默认 feature **大小完全相同**（479744 B） | §3.2 的 "+54 KB" 是 `packer.exe` 自己的体积（写侧只被它引用，未引用代码由 LTO 丢弃），**不是**给最终用户的税 |
| 2 | 本项目 release 构建**不可复现**：内容与 feature 都不变、强制重编，sha256 仍然不同（大小恒定） | "字节相同"从来不能作为判据；**唯一稳定的量是大小**，"分两次构建让 updater 由默认 feature 落盘"属于无效仪式，已删除 |

**那么"写侧不得进入 updater.exe"靠什么兜住？** 靠 `scripts/check_size.ps1` 的**形态守卫**
（断言产物内不存在 packer 独有字面量把 `--min-upgradable-from` 等），**不是**构建顺序。
`scripts/verify.ps1` 内部复用 `build.ps1`，构建只有一处定义。

