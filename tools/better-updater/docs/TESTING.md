# TESTING — 验证

> 来源：源文档 §17
> 本文回答：**验收标准是什么？测试矩阵有哪些？CI 怎么守？故障怎么注入？打包脚本长什么样？**
> 完成定义（DoD）见 `PLAN.md` §5。

---

## 1. 测试矩阵

> **覆盖状态（2026-09-15）**：矩阵共 **141 条**用例；已自动化 **65 条**（25 单元 + 12 e2e + 9 不变量 + 19 能力），`cargo test` 全绿。**F 组（不变量 I1–I10）已全部覆盖**；**E 组的缩回开关与 Tier 2 隔离已覆盖**。仍待补的是 A–D 组中依赖**交互式/并发夹具**的条目（真实强杀、UAC 会话、独占句柄、多实例竞争），以及因**条件不具备**而暂不可达的 `sig_*`（公钥列表为空）与 `authenticode_*`（Phase 7 搁置）。未被自动化覆盖的条目**不视为已通过**，逐条进度见 §6"覆盖进度"。

### A. 事务与崩溃恢复（重点）

| 用例 | 场景 | 预期断言 |
| :--- | :--- | :--- |
| `crash_window_action_done_record_lost` | **在 `ReplaceFileW` 成功之后、`MOVED` 记录落盘之前强杀进程** | L2 看门狗经**备份目录对账**还原该文件；全部文件回到旧版，**无半新半旧** |
| `crash_window_new_done_record_lost` | **在 `MoveFileExW` 成功之后、`ADDED` 记录落盘之前强杀** | 依据 PLANNED 阶段的权威 `NEW` 集合删除该文件，目录无孤儿新文件 |
| `crash_before_stage_applying` | `PLANNED` fsync 之后、`APPLYING` 之前强杀 | 目录零改动；恢复仅清理备份/临时目录与 Journal |
| `crash_midway_L1` | 替换中途注入 IO 错误 | 进程内立即回滚，退出 1；**文件层面 100% 复原**（空目录残留不视为失败） |
| `crash_midway_L2` | 替换中途按 **PID / 进程树**强杀正在施工的进程（注意：分身路径下映像名是 `upd-worker-<GEN>.exe`，**不是** `updater.exe`，`taskkill /im updater.exe` 会打空） | 看门狗接管，秒级完成回滚；无半坏文件 |
| `crash_midway_L3` | 同时杀死 Worker 与看门狗 | 下次任意 updater 启动（或 `--recover`）完成回滚 |
| `crash_after_committed_before_launch` | `COMMITTED` fsync 之后、拉起之前强杀 | 看门狗完成 GC **并拉起主程序**；用户不会看到"应用消失" |
| `crash_before_watchdog_spawn` | `PLANNED` 之后、看门狗派生之前强杀 | 由 L3 完成收敛（PLANNED ⇒ 仅清理） |
| `rollback_retry_on_sharing_violation` | 回滚时旧文件被短暂占用（不共享删除） | 按 `--write-retries` 重试；释放后成功 |
| `rollback_partial_failure` | 回滚中某文件重试耗尽仍失败 | **不中断其余文件**；结束后判"回滚未完成"，退出 3，**保留 Journal 与备份**；再次启动可继续收敛 |
| `rollback_1177` | 回滚中遇到 1177 | 记为本次重试失败，继续重试，不中止其它文件 |
| `rollback_dir_cleanup` | 包内含新子目录 | `DIR` 集合中的空目录被逆序删除 |
| `journal_gen_isolation` | 人为制造"上一事务 COMMITTED 但备份删除失败"的残留代，再启动新事务并触发回滚 | 只还原**本代**备份；陈旧代中的旧文件**绝不**被还原到 target |
| `stale_gen_gc` | 残留 `.updater/backup/<old-gen>` 超过 24 小时 | 任意 updater 启动时被删除，且不影响现存 Journal 引用的代 |
| `journal_fsync_committed` | 在 `COMMITTED` fsync 之后断电 | 恢复判定为"已提交"，仅做收尾 GC |
| `journal_fsync_not_committed` | 在 `COMMITTED` fsync 之前断电 | 恢复判定为"未提交"并执行回滚，而非误判已提交 |
| `journal_target_mismatch` | Journal 的 `TARGET` 与 `--target` 不符 | 拒绝恢复，退出 3，现场保留（防跨应用误伤） |
| `journal_unknown_version` | `JOURNAL:99` | 拒绝恢复，退出 3，现场保留 |
| `backup_delete_fail_keeps_journal` | 备份目录删除失败（句柄占用） | Journal 保留为 COMMITTED **且不删**；下次启动 GC，备份不永久泄漏 |
| `recover_mode` | `updater.exe --recover --target <dir>` | 仅凭 Journal 恢复，无需 `--zip`/`--sig`；成功后按需拉起；退出码符合契约 |
| `journal_target_case_insensitive` | Journal 的 `TARGET` 为 `c:\app`、`--target` 为 `C:\App\` | 规范化后**视为相同**，正常恢复；**不得**因大小写/尾随分隔符差异判为"跨应用"而退出 3 |
| `recover_lock_busy` | 另一 updater 持锁 | 10s 轮询后退出 2，本次不执行任何动作 |
| `recover_with_pid_waits` | 目标程序仍在运行 + `--recover --pid <PID>` | 先等目标退出再回滚，避免假性"回滚未完成" |

### B. 派生、退出码与参数

| 用例 | 场景 | 预期断言 |
| :--- | :--- | :--- |
| `derived_exit_code_default` | 分身路径 | 派生方退出 0 且日志有 `END: DERIVED(worker)`；**用例明确断言"0 不承载真实结果"** |
| `derived_exit_code_wait` | 自身在 target 外 + `--wait-derived` | 派生方等待并回传终态真实退出码 |
| `wait_derived_conflict` | 自身在 target 内 + `--wait-derived` | **强制不等待**并记 WARNING；自更新仍成功 |
| `elevate_cancel` | UAC 被取消 | 退出 2，兜底拉起旧版 |
| `shadow_worker_switch` | updater 位于 target 内运行 | 自身被复制到**运行期目录**，原进程退出，`target/updater.exe` 被更新为新版 |
| `self_update_in_target` | updater 在 target 内 + 包内含新版 `updater.exe` | 本次即完成自更新（与 Go 版的核心差异） |
| `self_protect_fallback` | 人为让 Worker 仍在 target 内运行 | 该路径被无条件保护，绝不覆盖自身 |
| `worker_relative_path` | 全部路径以**相对路径**传入且 updater 在 target 内 | Worker 解析到与原进程**相同**的绝对路径；**路径分隔符已归一化**；更新落在正确目录 |
| `worker_cwd_binding` | 调用方 cwd 与运行期目录不同 | Worker 的 `lpCurrentDirectory` 等于原 cwd |
| `worker_lock_inherit` | 主实例分身 | Worker 不会因句柄继承永久等锁；**在主实例退出后**于 10s 内取锁成功 |
| `lock_busy_main_and_worker` | 已有实例持锁 | 主实例/Worker 10s 后退出 2 |
| `fork_no_lock_gap` | 主实例派生 Worker 的**整个窗口内**，高频并发发起第二次调用 | 第二个实例要么在 10s 内取锁成功（此时第一个 Worker 因取锁失败退出 2，且**零改动**），要么退出 2；**任何情况下不得出现两个并发事务**（原"先关锁再派生"的 50–300ms 窗口已消除） |
| `elevate_still_unwritable_no_recursion` | 提权后 target **仍不可写**（如 ACL 显式拒绝） | **不再提权**：退出 2；日志中 `DERIVED(elevate)` 只出现**一次**；无 UAC 递归 |
| `quote_arg_roundtrip` | 空串/空格/引号/尾随反斜杠/`a\"b`/Tab/代理对 | 重建命令行经 `CommandLineToArgvW` 解析后与原 token 列表完全一致 |
| `args_raw_passthrough` | `--args '--foo="a b" --bar'` | 主程序收到 `--foo=a b` 与 `--bar` **两个**参数 |
| `args_at_tail` | `--args` 位于命令行末尾且含引号 | 原文拼接，不被 `quote_arg` 二次处理 |
| `no_elevate_when_native_admin` | 原生管理员启动且未传 `--elevate` | **不触发**提权，也**不附加** `--elevated-worker` |
| `watchdog_pid_reuse` | Worker 退出后 PID 被复用 | 看门狗经 `--watch-image` 比对后立即判定"已退出"，不白等安全超时 |

### C. 替换、保护与路径安全

| 用例 | 场景 | 预期断言 |
| :--- | :--- | :--- |
| `replace_atomic_open_share` | 目标 DLL 被另一进程以共享读、**不共享删除**打开 | 返回 32，按 `--write-retries` 重试；释放后成功 |
| `replace_error_1177` | 构造 1177 场景 | **不盲目重试**，立即回滚并退出 1 |
| `replace_same_volume` | target 与 `%TEMP%` 不同卷 | 暂存文件落在 target 同卷的 `.updater/tmp/<GEN>`，替换成功 |
| `temp_file_not_hidden` | 更新后检查所有被写入文件的属性 | **不含 `FILE_ATTRIBUTE_HIDDEN`**；`.updater/tmp` / `.updater/backup` **目录**为隐藏 |
| `backup_attribute_merge` | 旧文件带自定义属性，新文件为普通属性 | 覆盖分支结果继承被替换文件的属性（`ReplaceFileW` 语义） |
| `require_empty_file_ok` | `--require` 指向包内 0 字节文件 | 提交前自检通过 |
| `precheck_no_kill` | 损坏 zip / 错误签名 | 主程序**完全不被关闭**，退出 2，目录零改动 |
| `zip_handle_pinned` | 验签通过后、事务前尝试替换 zip 文件 | 因 `FILE_SHARE_READ` 独占读而**替换失败**；或替换成功则本次事务必然报错——两者都不允许"验签对象 ≠ 解压对象" |
| `delete_zip_after_close` | `--delete-zip` | 在关闭 zip 句柄之后删除成功；删除失败仅 WARNING |
| `sig_missing_fail_closed` | 存在公钥常量但 `.sig` 缺失 | 退出 2，绝不继续 |
| `sig_tampered` | zip 被改 1 字节 | 签名校验失败，退出 2 |
| `sig_hex_and_bin` | `.sig` 分别为 64B 二进制与 128 字符 Hex | 两者均正确识别 |
| `keep_rules_parity` | 目录 / glob / 字面量三类规则 | 与 Go 版 `verify.py` 结果逐条一致 |
| `keep_internal_reserved` | 包内含 `.updater/journal` / `.updater/backup/x` / `.updater/tmp/x` | 全部被拦截，绝不落盘 |
| `keep_internal_reserved_bare_name` | 包内含**裸文件条目 `.updater`**（无斜杠） | 被"精确名"规则拦截，绝不落盘；**不得**出现与同名目录争抢导致的 `ERROR_ACCESS_DENIED(5)` + 整包回滚 |
| `join_escape_blocked` | 包内条目经 Junction 指向外部目录 | 物理路径校验识破并拒绝，退出 2 |
| `fs_unsupported_default_continue` | target 位于网络盘/不支持 `GetFinalPathNameByHandleW` 的文件系统，**未传** `--strict-path-check` | **不拒绝**：记 WARNING 后继续（字符串层校验已通过），更新正常完成 |
| `fs_unsupported_strict_reject` | 同上但传 `--strict-path-check` | 拒绝并退出 2，日志明确指出"无法取得规范物理路径" |
| `long_path` | target 深层路径总长 > 260 | 全部 Win32 调用使用 verbatim 前缀，替换成功 |
| `zipbomb_declared` | 中央目录**声明**总量超限 | 预检阶段拒绝，退出 **2**（零改动） |
| `zipbomb_streamed` | 声明值正常但**实际**解压超限（谎报头） | 事务中流式中止 → 回滚 → 退出 **1** |
| `zipbomb_ratio` | 单条目**声明**比值 > 2000:1（阈值已抬到 Deflate 物理上限 1032:1 之上） | 预检层拒绝，退出 **2**。**注意**：不得再用 1000:1 —— 该值低于物理上限，会误杀含大段重复字节的合法包 |
| `zipbomb_legit_high_ratio` | 构造一个**合法**包：含大段重复字节（如全零数据文件）使实际比值接近 1000:1 | **正常安装成功**（绝对值远低于 `--max-uncompressed`）；不得被比值规则拦截 |
| `zip_method_reject` | 包内含 zstd/bzip2/AES 条目 | 拒绝，退出 2 |
| `duplicate_entry_reject` | 包内重复条目（含大小写变体） | 拒绝，退出 2 |
| `manifest_path_normalized` | 清单用 `/` 生成（Linux CI 打包），磁盘路径用 `\` | 归一化后匹配成功；**不得**出现"清单条目在磁盘上找不到 → 误判自检失败 → 全量回滚" |
| `manifest_strip_mapping` | `--strip 1` + 含清单的包 | 哈希复核与保留名拦截均按**同一 strip 规则**映射后的路径进行；映射到 target 之外的条目被拒绝 |
| `manifest_protected_file_not_hashed` | 包内 `.updatekeep` 内容与磁盘不同（该文件受保护、永不被覆盖） | 提交前自检**通过**（受保护/未写入项不进校验集合）；**不得**因此回滚 |

### D. 进程、权限与收尾

| 用例 | 场景 | 预期断言 |
| :--- | :--- | :--- |
| `pid_reuse_not_waited` | PID 被复用给 `svchost.exe` | 句柄等待在真实目标退出时立即返回；无超时假等待 |
| `pid_image_mismatch_logged` | `--launch` 与实际映像不同 | 记 WARNING，**仍按句柄等待**，不跳过 |
| `pid_access_denied` | 目标以管理员运行而 updater 未提权 | 按 87/5 分流，超时退出 2 并兜底拉起 |
| `de_elevate_medium` | 因 `--elevate` 提权完成更新 | 拉起的主程序 Token 为 Medium，可接受桌面拖拽 |
| `de_elevate_env_preserved` | 调用方注入自定义环境变量后提权更新 | 主程序仍能看到该变量 |
| `de_elevate_1314` | 构造 `SeImpersonatePrivilege` 缺失 | WARNING 后降级为常规拉起，**不崩溃** |
| `de_elevate_session0_no_launch` | 在 `SessionId == 0`（服务上下文）下走到降权路径 | 记 ERROR、**不拉起**（不得以 System 在无桌面会话静默启动）；退出码按契约（已提交 → 3；回滚/预检 → 1/2）。用例必须断言**没有**创建任何进程 |
| `de_elevate_skip` | `--keep-elevation` | 主程序保持管理员权限 |
| `current_dir_and_env` | 主程序读取 cwd 与环境变量 | cwd == target，环境变量继承 |
| `log_passthrough` | updater 位于 target 内（触发分身） | 全流程只有**一份**日志文件 |
| `log_concurrent_no_interleave` | Worker 与看门狗同时写日志 | 无行交错（逐行一次性 `WriteFile`） |
| `del_cleanup` | 连续两次更新 | 第二次启动清理上一次的 `upd-*.del`；`MOVEFILE_DELAY_UNTIL_REBOOT` 已登记 |
| `dry_run_zero_touch` | `--dry-run` | 目录零改动、**无 Journal**、无看门狗派生 |
| `exit_codes` | 构造四种终态 | 分别返回 0 / 1 / 2 / 3，且 3 时不拉起主程序 |
| `help_version` | `--help` / `--version` | 均退出 0；`--version` 含公钥指纹或 `unsigned-build` |

### E. 新增能力（每项均含故障注入与"关闭后行为"两条）

> `previous_*`（保留 + `_meta.txt`）与 `rollback_previous_*` 用例**均为必测**（`--rollback-previous` 已随 Phase 6 落地，不再暂缓）；`rm_shutdown_*` 与 `splash_*` 已删除（对应能力被删），改为"参数已删除"的回归用例。

| 用例 | 场景 | 预期断言 |
| :--- | :--- | :--- |
| `manifest_parsed_same_handle` | 校验清单是否从同一句柄读取 | 清单内容与解压内容来自同一文件对象；**替换 zip 后本次事务必然报错**（I8 回归） |
| `manifest_missing_compat` | 包内无 `updater.manifest` | 跳过版本判定 + 逐文件哈希自检，记 WARNING，其余流程照常（缩回开关） |
| `version_compare_table` | 单测：`1.10.0 vs 1.9.0`、预发布后缀 vs 同号正式版、两个预发布后缀之间的标识符比较、`1.2`（缺补段）、`1.2.3+build`、非数字段 | 按 `DESIGN.md` §5.3 的规则表逐条成立；缺段与 `+build` 一律**拒绝**（不宽容解析——版本比较是降级防护的判定依据） |
| `downgrade_rejected` | 包版本 **低于** `.updater/state` 且未传 `--allow-downgrade` | 退出 2，主程序不受打扰，目录零改动 |
| `downgrade_allowed` | 同上但传 `--allow-downgrade` | 正常更新；日志高等级告警 |
| `same_version_warn` | 包版本 == 当前版本 | 允许 + WARNING（同版本修复重装） |
| `min_version_rejected` | 包版本低于 `--min-version` | 退出 2 |
| `min_upgradable_from_rejected` | 清单声明 `MIN_UPGRADABLE_FROM:1.2.0`，而 `.updater/state` 为 `1.0.0` | 拒绝，退出 2（版本跳跃过大）；该字段从"死字段"变为有消费者 |
| `state_missing_first_install` | `.updater/state` 不存在 | 允许任意版本 + WARNING；提交时补写状态 |
| `state_atomic_on_crash` | 写 `.updater/state` 中途强杀 | 状态文件要么是旧内容、要么是新内容，**不出现半截**（Tier 0 原子写） |
| `state_repaired_from_journal` | 在 `COMMITTED` 之后、写状态之前强杀 | 恢复层用 Journal 的 `TOVER` **补写**状态为正确版本 |
| `previous_retained` | 更新成功且 `--previous-ttl-days 7` | `<BACKUP>` 被**重命名**为 `.updater/previous/<GEN>`（非删除）；其中含 `_meta.txt`（**VERSION + TSA + ADDED**），且 `ADDED` 与 Journal 权威 `NEW` 集合逐一对应 |
| `previous_meta_from_authoritative_new` | 事务中强杀使**运行期** `ADDED:` 建议记录丢失，但 Journal 权威 `NEW` 集合完整 | 恢复后生成的 `_meta.txt` **仍包含完整 ADDED**（取自权威集）；**不得**出现"回退后留下新版独有孤儿文件" |
| `previous_ttl_zero` | `--previous-ttl-days 0` | 退回旧行为：提交后删除备份（缩回开关），且不要求写 `_meta.txt` |
| `previous_transfer_fail_no_journal_delete` | 强制让"备份→previous"的改名失败 | **WARNING + 保留 COMMITTED Journal + 不删除 Journal**、**绝不回滚**（I2/I3 回归）；下次启动的 COMMITTED 分支幂等收尾 |
| `rollback_previous_ok` | 新版启动即崩，执行 `--rollback-previous` | 目标文件还原为上一版本；**新版新增的文件被删除**（依据 `_meta.txt` 的 ADDED）；`.updater/state` 版本回退 |
| `rollback_previous_is_transactional` | 回退过程中强杀 | 走同一套三层恢复；回退本身也产生新的 `.updater/previous`，因此**可再回退** |
| `rollback_previous_reuses_restore` | 静态检查 + 单测 | `--rollback-previous` 与回滚 R1 **调用同一个** `restore_from`，且传入 `excludes = {_meta.txt}`（I10 幂等性同源） |
| `rollback_previous_none` | 无保留目录时执行 | 记 INFO "无保留版本可退"，退出 0，目录零改动 |
| `previous_gc_ttl` | 保留目录超过 TTL 且非最新 | 被 GC 删除；**最新 1 个始终保留**（I4 回归） |
| `rm_diagnose_names_holder` | 目标 DLL 被另一进程以不共享删除方式打开 | 重试耗尽后日志出现 `"file <rel> 被 <name>(<pid>) 占用"`；**事务行为与不含诊断时完全一致**（未被诊断改变） |
| `rm_feature_disabled` | 以 `--no-default-features` 构建 | 32/5 走原有重试/回滚，无 RM 日志；其余行为不变（缩回开关） |
| `rm_shutdown_removed` | 传 `--rm-shutdown`；并静态检查源码 | ① 该参数**已不存在** → 按未知参数退出 2（有明确日志）；② 代码中不出现 `RmShutdown` / `RmRestart` / `RmForceShutdown`（本工具**不关闭任何用户进程**） |
| `authenticode_feature_off` | 默认构建 | 无 WinTrust 调用，体积不受影响（缩回开关） |
| `authenticode_unsigned_rejected` | 开启 feature 且传 `--verify-authenticode`，包内 EXE 未签名 | 退出 2，目录零改动 |
| `authenticode_wrong_publisher` | 包内 EXE 签名有效但主体名不匹配 | 退出 2 |
| `progress_failure_isolated` | `--progress-file` 指向不可写路径 | WARNING + **事务结果与退出码完全不受影响**（I5 回归，Tier 2 隔离） |
| `progress_atomic_rewrite` | 应用侧高频轮询进度文件 | 永不读到半行/半个字段（整份原子替换） |
| `splash_param_removed` | 传 `--splash <png>` | 该参数**已不存在** → 按未知参数退出 2；且源码中无 PNG 解码调用、无窗口/消息循环 |
| `watchdog_safety_timeout_downgrade` | 让 Worker 存活超过 `--timeout + 600s`，使看门狗命中安全上限 | 看门狗记 WARNING 并**明确标注降级 L3** 后退出；**不得**把它判为"已有新实例接管" |
| `watchdog_param_passthrough` | 以非默认 `--previous-ttl-days` / `--write-retries` / `--write-delay-ms` / `--timeout` 运行并让看门狗接管收尾 | 看门狗命令行**包含**这些参数；其收尾/回滚行为与 Worker 一致；`--delete-zip` **不**由看门狗执行 |
| `lockfile_cross_session` | 模拟两个会话同时更新同一 target | 第二个实例取得 `ERROR_SHARING_VIOLATION`，10s 轮询后退出 2 |
| `lockfile_no_stale` | 持锁进程被强杀后再次启动 | 锁文件已被内核随句柄关闭而删除，**无陈旧锁**，新实例立即取锁成功 |
| `lockfile_delete_on_close` | 正常结束时检查 | `<target>/.updater/lock` 不存在（`FILE_FLAG_DELETE_ON_CLOSE`） |
| `lockfile_delete_pending_retry` | 在上一个持锁者刚关闭句柄（delete pending）的瞬间发起第二次取锁 | 第二次调用拿到 `ERROR_ACCESS_DENIED(5)` 后**进入轮询**并最终成功；**不得**因"5 像权限问题"直接退出 |
| `single_internal_dir_steadystate` | 完整跑一次成功更新后，枚举 target 根 | 内部增量**只有 `.updater/` 一项**（Hidden + System）；`.updater/` 内稳态只有 `state`（+ 可选的 `previous/`）；不存在 `journal`/`lock`/`backup`/`tmp` 残留 |
| `single_internal_dir_blocked` | 包内含 `.updater/journal`、`.updater/state`、`.updater/previous/x`、`.updater/anything`、**以及裸文件 `.updater`** | **精确名 + 前缀两条规则全部拦截**，绝不落盘（I9；同时验证规则收敛后无漏网） |
| `internal_dir_hidden_attr` | 检查 `.updater/` 属性 | 含 `FILE_ATTRIBUTE_HIDDEN`；其内**文件**保持 `NORMAL`（不继承隐藏） |
| `probe_creates_internal_dir` | 全新 target，首次运行 | 预检即创建 `.updater/` 并写入后删除随机文件；**不产生任何 target 根级临时文件** |
| `probe_skipped_on_dry_run` | `--dry-run` | 不创建 `.updater/`、不写锁、目录零改动 |
| `elevate_no_lock_release_needed` | 不可写 target + `--elevate` | 提权发生在取锁之前，流程中**不存在**"释放锁再派生"这一步；提权实例直接取锁成功 |
| `runtime_dir_location` | 正常触发分身 | Worker/看门狗副本位于 `%LOCALAPPDATA%\<目标目录名>-updater\<hash8>\runtime\`，**`%TEMP%` 下无任何 `upd-*` 文件** |
| `runtime_dir_fallback` | 人为使 `%LOCALAPPDATA%` 不可用/不可写 | 按有序候选切换到 `%TEMP%\updater-runtime\<hash8>\` + WARNING；更新照常成功 |
| `runtime_dir_name_stable` | 同一 target 多次更新 | `<basename>-updater\<hash8>` 稳定不变；`target.txt` 内容为完整原始路径 |
| `runtime_dir_reject_network` | 把 `%LOCALAPPDATA%` 重定向到网络共享（映射盘） | 因 `GetDriveTypeW != DRIVE_FIXED` 被拒，自动切到下一候选；**绝不从网络路径运行副本** |
| `runtime_dir_same_basename` | 两个同名不同路径的安装（`C:\A\MyApp` 与 `D:\B\MyApp`） | `hash8` 不同 → 目录隔离，互不干扰 |
| `runtime_dir_orphan_gc` | 预置超过 24 小时的孤儿 `upd-worker-*.exe` | 下次启动被清理；**正在运行的副本不被删除** |
| `runtime_dir_failure_isolated` | 让运行期目录创建失败 | WARNING + 降级（不分身则退出 2 并兜底拉起；已分身则走 L3），**不影响事务结果判定**（I5，Tier 2） |

### F. 不变量守恒（I1–I10）

| 用例 | 不变量 | 断言 |
| :--- | :--- | :--- |
| `inv_I1_rollback_before_commit` | I1 | 在 COMMITTED fsync 之前的**任意**注入点强杀，回滚均可复原 |
| `inv_I2_no_rollback_after_commit` | I2 | 在 COMMITTED 之后的任意注入点（含 previous 转移失败、状态写入失败）**绝不**触发回滚 |
| `inv_I3_journal_after_backup` | I3 | 人为使 previous 转移失败，断言 Journal **仍存在** |
| `inv_I4_gen_scoped_recovery` | I4 | 预置陈旧代目录，触发回滚后断言陈旧代未被触碰 |
| `inv_I5_tier2_isolated` | I5 | 逐一使每个 Tier 2 功能失效，断言退出码与目录状态与"全部正常"时**完全一致** |
| `inv_I6_no_journal_means_determined` | I6 | 无 Journal 时断言 target 为新版或旧版**之一**，不存在中间态；且 `.updater/previous/` 的存在不影响该判定 |
| `inv_I7_same_volume` | I7 | target 与 `%TEMP%` 不同卷时，备份/临时/保留目录均落在 target 所在卷 |
| `inv_I8_same_handle` | I8 | 验签、清单解析、解压使用同一文件对象 |
| `inv_I9_reserved_names` | I9 | 包内含任一内部保留名（含 `.updater/state`、`.updater/lock`、`.updater/previous/x`、**裸 `.updater`**、`updater.manifest`）均被拦截/不落盘 |
| `inv_I10_rollback_idempotent` | I10 | 对同一 Journal 连续执行三次回滚，结果与执行一次一致 |

---

## 2. CI 守卫

**单一入口（已落地）**：`scripts/verify.ps1` 把下面的检查合并为一条流水线，CI 与本机调用的是**同一个脚本**——避免"本地绿、CI 红"的两套判定漂移。

| 脚本 | 职责 | 失败语义 |
| :--- | :--- | :--- |
| `scripts/verify.ps1` | 全量守卫编排：构建（含缩回开关）→ 测试 → clippy → 分层 → 体积 → Defender 归档 | 任一步失败即**就地停下**并返回非 0 |
| `scripts/check_tiers.ps1` | §2.3 分层守卫（Tier 声明 / 禁用构造 / 依赖数 / 行数 WARN） | 硬失败（行数仅 WARN） |
| `scripts/check_size.ps1` | §2.1 体积守卫 + `docs/artifacts/` 快照归档 | 硬失败 |
| `scripts/gen_manifest.ps1` | §4 的 `updater.manifest` 生成（打包流程，非守卫） | — |

> **CI 激活说明**：Actions **只读取仓库根目录**的 `.github/workflows/`，而本仓库是 monorepo。
> 项目内的 `.github/workflows/ci.yml` 是**可评审的版本化定义**；要真正跑起来，需把它放到
> `<repo>/.github/workflows/better-updater.yml`（`paths` 已限定到 `tools/better-updater/**`，
> 不影响同仓其它项目）。该 workflow 只做一件事：在 `windows-latest` 上执行 `scripts/verify.ps1`。

### 2.1 体积守卫

**已落地**为 `scripts/check_size.ps1`（下列逻辑即该脚本的实现口径）：

```powershell
# 体积守卫：上界告警、下界异常检测（下界命中通常意味着 feature 被误裁或构建未包含功能）
$size = (Get-Item target\release\updater.exe).Length
$kb   = [math]::Round($size / 1KB, 1)
Write-Host "updater.exe = $kb KB"
if ($size -gt 750KB) { throw "体积超上限: $kb KB（检查是否误开 zip 的 deflate/zopfli）" }
if ($size -lt 250KB) { throw "体积低于下限: $kb KB（检查 feature 是否被误裁）" }
```

> **快照口径已修正**：不要用 `cargo tree -e features` 落档（`cargo tree` 的树形字符在
> PowerShell 5.1 + 中文代码页下会被破坏，且 `cargo tree` 的 feature 展开随版本变动）。
> `check_size.ps1` 改为归档 `cargo metadata --no-deps` 的 JSON 到 `docs/artifacts/deps.json`，
> 体积回归时用**机器可解析**的格式定位放大来源。

### 2.2 其他必须执行的检查

1. `cargo build --release` 的 **feature 完整性验证**（防"清单漏 feature"复发）；
2. `cargo build --release --no-default-features`（验证 RM feature 缩回开关可用）；
3. `cargo test --all-targets`：单元（`quote_arg` / `keep` / ZIP / Journal / **版本比较边界表** / 路径规范化 / I8 / GC）+ e2e + 不变量 + 能力四组；
4. **E/F 两组为强制项**，未覆盖即视为构建失败——现由 `tests/invariants.rs` 与 `tests/capabilities.rs` 承担（进度见 §5）；
5. `PLAN.md` §4.1 的**六项**实构建/实测结论已归档到 `REFERENCE.md` §1.2（5/6 通过，1 项随 Phase 7 搁置）；
6. **禁止**引入 UPX 步骤；Defender 扫描结果归档到 `docs/artifacts/defender-scan.txt`（`SCANNED` / `UNAVAILABLE` / `SKIPPED` 三态，见该目录 README）；
7. **分层守卫**（见 §2.3）。

### 2.3 分层守卫脚本

```powershell
# ---- 分层守卫 ----
# 约定：每个 .rs 的**第一行**必须声明 `// Tier: 0|1|2`
$srcFiles = Get-ChildItem src -Recurse -Filter *.rs
$undeclared = $srcFiles | Where-Object {
  -not ((Get-Content $_ -TotalCount 1) -match '//\s*Tier:\s*[012]')
}
if ($undeclared) { throw "存在未声明 Tier 的模块（视为逃避分层）: $($undeclared.Name -join ', ')" }

# Tier 0 产品代码行数（不含 #[cfg(test)] 之后的测试代码）：超限 WARN + 触发架构评审，不硬失败
$prodLines = 0
foreach ($f in $srcFiles) {
  $lines = Get-Content $f
  if ($lines[0] -match '//\s*Tier:\s*0') {
    $cut = ($lines | Select-String -Pattern '^\s*#\[cfg\(test\)\]' | Select-Object -First 1).LineNumber
    if ($cut) { $prodLines += ($cut - 1) } else { $prodLines += $lines.Count }
  }
}
if ($prodLines -gt 2600) { Write-Warning "Tier 0 产品代码 $prodLines 行 > 2600：触发架构评审（不阻断构建）" }

# Tier 0 禁止并发/异步/间接层：**先剥离注释与字符串字面量再匹配**（否则模块顶部
# 那句"禁止 async / 线程"的说明性注释会把自己的模块判死），命中即硬失败
$forbidden = '\bstd::thread\b|\basync\s+fn\b|\btokio\b|\bBox<dyn\b|\bdyn\s+[A-Za-z_]'
foreach ($f in $srcFiles) {
  $lines = Get-Content $f
  if ($lines[0] -match '//\s*Tier:\s*0') {
    $code = $lines | ForEach-Object { ($_ -replace '//.*$', '') -replace '"(?:[^"\\]|\\.)*"', '""' }
    $hit = $code | Select-String -Pattern $forbidden
    if ($hit) { throw "Tier 0 出现禁用构造（$($f.Name)）: $($hit[0])" }
  }
}

# 直接依赖数量预算：**必须用 cargo metadata**。
# 不能再用 `cargo tree --depth 1 | Select-String '^[├└]'`：
#   ① 它会把 windows-sys 也算进去（7 个）而预算文字写的是 6 → 上线当天即红；
#   ② 管道进入 PowerShell 5.1 时按控制台代码页解码，树形字符 '├'/'└' 会被破坏
#      → 匹配恒为 0 → **守卫静默失效**（比失败更危险）。
$meta  = cargo metadata --format-version 1 --no-deps | ConvertFrom-Json
$deps  = @($meta.packages[0].dependencies | Where-Object { $_.kind -ne 'dev' })
if ($deps.Count -gt 7) { throw "直接依赖超预算: $($deps.Count)（上限 7 = windows-sys + 6 个第三方）" }
```

> **为什么行数不再硬失败**：Tier 0 同时被要求 100% 分支覆盖与"每条不变量至少一次故障注入"，测试代码与注释本身就占去可观的行数。硬卡一个脱离实际的数字，只会逼出"压缩排版"或"把核心逻辑硬标成 Tier 1"的变形操作——那是把守卫变成了指标游戏。

---

## 3. 故障注入的钩子形态（Phase 4 开工前必须定稿）

复杂度约束要求"每条不变量至少一次故障注入"、"每条新增能力至少一条故障注入 + 一条关闭后行为"，DoD 还要求**真实进程终止**。但 Tier 0 同时规定"非 Win32 间接层 0 层"、禁止 `trait object` / 泛型抽象。"必须注入"与"不许有抽象层"看似矛盾——本节给出唯一形态。

| 类别 | 形态 | 覆盖的用例 |
| :--- | :--- | :--- |
| **外部注入（首选，零代码改动）** | 用**测试夹具进程**制造真实条件：独占句柄制造 32、只读目录制造 5、按 PID / 进程树强杀制造崩溃、占满磁盘制造写失败、把 `%LOCALAPPDATA%` 指向网络路径制造候选失败、预置陈旧代目录制造 GC 场景、让看门狗等满安全超时制造 3T 分支 | 绝大多数 A / C / D / E 组用例。**这类注入最真实，优先级最高**；DoD 的"真实终止"只能走这条 |
| **`#[cfg(test)]` 薄包装（唯一允许的代码内注入）** | 在 Tier 0 模块内以 `#[cfg(test)]` 提供一个"让第 N 次同类 Win32 调用返回指定错误码"的**计数器 + 错误码覆盖**静态变量；**生产构建下这些符号完全不存在**（`#[cfg(test)]` 编译期剔除） | "替换中途注入 IO 错误"（`crash_midway_L1`）、"1177 场景"（`replace_error_1177`）、`inv_I2_*` 的提交后失败注入点 |
| **禁止的做法** | ① 为可测性在 Tier 0 引入 `trait FsBackend` 之类的抽象层；② 用**环境变量**在生产构建里开启"注入模式"（等于给线上留后门）；③ 用**编译期 feature** 切换实现（会生成两套代码路径，与"单一路径复用"直接冲突） | — |

**约束**：`#[cfg(test)]` 薄包装属于**测试代码**（CI 口径已把 `#[cfg(test)]` 之后的代码排除在产品行数之外，不计入 Tier 0 的 2600 行预算）；但**注入点必须声明在被注入模块内**——不允许搬到独立的 `testkit` crate（那会让生产代码反向依赖测试代码）。

---

## 4. 包清单生成（打包脚本）

`updater.manifest` 由打包侧生成，**必须与 zip 一起构建后再一起签名**（顺序不可颠倒，否则清单与内容不匹配）：

```powershell
# 假设 $stage 为待打包目录，$ver 为版本号，$minFrom 为最低可升级起始版本（可选）
$lines = @("MANIFEST:1", "VERSION:$ver")
if ($minFrom) { $lines += "MIN_UPGRADABLE_FROM:$minFrom" }   # 可选；消费者见 DESIGN.md §5.4.1
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    # 必须把分隔符归一化为 `\`。本脚本可能在 Linux/pwsh 上执行，
    # 此时 GetRelativePath 产出 `/`，与磁盘侧拼接出的 `\` 不一致会导致
    # "清单条目在磁盘上找不到" → 误判自检失败 → 全量回滚
    $rel = ([IO.Path]::GetRelativePath($stage, $_.FullName)) -replace '/', '\'
    if ($rel -eq 'updater.manifest' -or $rel -eq '.updater' -or $rel -like '.updater\*') { return }   # 包元数据自身 + 内部保留名（含裸 .updater），均不入清单
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    $lines += "FILE:$rel|$($_.Length)|$h"
}
Get-ChildItem $stage -Recurse -Directory | ForEach-Object {
    $lines += "DIR:" + [IO.Path]::GetRelativePath($stage, $_.FullName)
}
# 写入（LF 结尾、UTF-8 无 BOM），随后压缩，最后对 zip 签名
$lines -join "`n" | Set-Content -Path (Join-Path $stage 'updater.manifest') -Encoding utf8NoBOM -NoNewline
```

> 该脚本属**打包流程**，不是 updater 的一部分；但它是版本与降级防护生效的前提，应纳入发布检查单。
> **已落地**为 `scripts/gen_manifest.ps1`（可直接调用，见脚本头部用法）。

---

## 5. 覆盖进度（2026-09-15）

自动化用例 **65 条**：`cargo test` 全绿，`cargo clippy --all-targets` 0 警告。

| 测试文件 | 条数 | 职责 |
| :--- | :--- | :--- |
| `src/**` 模块内测试 | 25 | `quote_arg` 往返、版本比较边界、keep 规则、Journal 解析/校验、路径规范化、**I8 同一文件对象**、**GC 三条规则** |
| `tests/e2e.rs` | 12 | 主流程：完整更新、`--dry-run` 零落盘、降级拒绝、L3 恢复、保留名拦截、影子自更新、看门狗接管、`--rollback-previous` |
| `tests/invariants.rs` | 9 | **F 组 I1–I7 / I9 / I10**（I8 在模块内测试） |
| `tests/capabilities.rs` | 19 | **E 组**：缩回开关、Tier 2 隔离、版本防护矩阵、GC、锁、布局与属性、进度、退出码、CLI 契约 |
| `tests/common/mod.rs` | — | 共享夹具（含**跨测试二进制**的 Win32 命名互斥量串行锁） |

### 已覆盖（矩阵条目 → 用例）

| 矩阵条目 | 对应用例 |
| :--- | :--- |
| F 组 `inv_I1…I10` 全部 10 条 | `inv_*` × 9 + `pkg::handle::tests`（I8） |
| `crash_midway_L1` / `journal_fsync_not_committed` | `inv_I1_rollback_before_commit`、`recover_from_applying_journal_rolls_back` |
| `journal_fsync_committed` / `state_repaired_from_journal` | `recover_from_committed_journal_finalizes` |
| `crash_midway_L2` / `crash_after_committed_before_launch` | `watchdog_rolls_back_applying`、`watchdog_finalizes_committed` |
| `journal_unknown_version` / `exit_codes` | `exit_codes_matrix`（0/1/2/3 四态） |
| `rollback_previous_ok` / `rollback_previous_is_transactional`(部分) | `rollback_previous_reverts_to_retained_generation` |
| `rollback_previous_none` | `rollback_previous_none` |
| `previous_retained` / `previous_meta_from_authoritative_new` | `previous_retained_with_meta`、`previous_meta_from_authoritative_new` |
| `previous_ttl_zero`（缩回开关） | `previous_ttl_zero_retracts` |
| `manifest_missing_compat`（缩回开关） | `manifest_missing_compat` |
| `downgrade_rejected` / `downgrade_allowed` / `same_version_warn` / `min_version_rejected` / `state_missing_first_install` | `downgrade_rejected`、`downgrade_allowed`、`same_version_warn`、`min_version_rejected`、`state_missing_first_install` |
| `stale_gen_gc` / `previous_gc_ttl` | `stale_gen_gc` + `gc::tests` ×3（含**被引用代不清理**，仅模块内可观察） |
| `lockfile_no_stale` / `lockfile_delete_on_close` | `lockfile_no_stale_and_delete_on_close` |
| `single_internal_dir_steadystate` / `internal_dir_hidden_attr` / `temp_file_not_hidden` | `single_internal_dir_steadystate_and_hidden_attr`、`temp_file_not_hidden` |
| `runtime_dir_location` | `runtime_dir_location` |
| `progress_failure_isolated` / `progress_atomic_rewrite`（部分） | `inv_I5_tier2_isolated`、`progress_file_written_atomically` |
| `help_version` / `splash_param_removed` / `rm_shutdown_removed` | `help_and_version`、`removed_params_rejected`、`unknown_arg_rejected_exit_2` |
| `require_empty_file_ok` / `dry_run_zero_touch` / `delete_zip_after_close` | `require_empty_file_ok`、`dry_run_zero_touch`、`full_update_flow_with_manifest` |
| `dry_run` 不派生看门狗 / `probe_skipped_on_dry_run` | `dry_run_zero_touch` |
| `keep_internal_reserved*` / `single_internal_dir_blocked` | `inv_I9_reserved_names`、`reserved_names_and_keep_file_protected` |

### 待补（及阻塞因素）

| 条目 | 阻塞因素 |
| :--- | :--- |
| `crash_window_*` / `crash_midway_L3` / `crash_before_watchdog_spawn`（**真实进程终止**，DoD 第 2 条） | 需"在 `ReplaceFileW` 成功后、`MOVED` 落盘前"精确插入强杀的夹具；需按 PID/进程树定位 `upd-worker-<GEN>.exe` |
| `elevate_*` / `de_elevate_*` / `elevate_cancel` / `elevate_still_unwritable_no_recursion` | 需交互式 UAC 会话与第二个管理员上下文，无法在无人值守 CI 中稳定复现 |
| `lock_busy_main_and_worker` / `fork_no_lock_gap` / `lockfile_cross_session` / `recover_lock_busy` | 需可控的多实例竞争夹具（当前串行锁下不可并发发起） |
| `zip_handle_pinned` / `manifest_parsed_same_handle`（外部观察） | 机制已由 `pkg::handle` 模块内测试证明；外部观察需在事务窗口内并发替换 zip，夹具未就位 |
| `rm_diagnose_names_holder` / `rm_feature_disabled` | 需真实独占句柄持有者；无默认 feature 构建需在 CI 单独跑一条 `--no-default-features` |
| `sig_missing_fail_closed` / `sig_tampered` / `sig_hex_and_bin` | **当前构建公钥列表为空**（`unsigned-build`），签名强制分支不可达；填真实公钥后即可自动化 |
| `authenticode_*` | **Phase 7 搁置**（无代码签名证书） |
| `de_elevate_session0_no_launch` | 需在 Session 0 服务上下文启动 |
| `zipbomb_*` / `zip_method_reject` / `duplicate_entry_reject` / `join_escape_blocked` / `long_path` / `fs_unsupported_*` / `keep_rules_parity` | 夹具可做，尚未编写（`keep_rules_parity` 另需 Go 版 `verify.py` 对照） |

