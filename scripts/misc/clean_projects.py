#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
clean_projects.py
精准识别并清理各类开发工程（.NET, Rust, Flutter, Node.js, Android, CMake, Python, Maven等）
构建产物与临时缓存目录的高性能脚本。

特性：
- 默认 Dry-run 安全模式，绝不误删；
- 自顶向下剪枝扫描，跳过 .git/.svn 等敏感目录；
- 通用剪枝优化：即便未启用特定技术栈清理，也绝不下钻其大型依赖/构件目录（如 node_modules, .gradle, target），秒级扫描；
- 智能跳过 Python 虚拟环境内部依赖深层遍历，避免扫描成千上万琐碎 pycache；
- 二次构件指纹比对，严防同名源码目录误删；
- 多线程快速体积统计与人性化彩色终端报告；
- 跨平台控制台编码与权限容错。
"""

import argparse
import concurrent.futures
import fnmatch
import os
import shutil
import stat
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Set, Tuple

# ==========================================
# 控制台编码与 UTF-8 兼容性保障
# ==========================================
if sys.platform == "win32":
    try:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    try:
        import ctypes
        ctypes.windll.kernel32.SetConsoleOutputCP(65001)
    except Exception:
        pass


# ==========================================
# 终端颜色与样式支持
# ==========================================
class Colors:
    RESET = "\033[0m"
    BOLD = "\033[1m"
    DIM = "\033[2m"
    RED = "\033[31m"
    GREEN = "\033[32m"
    YELLOW = "\033[33m"
    BLUE = "\033[34m"
    MAGENTA = "\033[35m"
    CYAN = "\033[36m"
    WHITE = "\033[37m"

    @classmethod
    def disable_if_needed(cls):
        if not sys.stdout.isatty() and "TERM" not in os.environ:
            for attr in dir(cls):
                if not attr.startswith("_") and isinstance(getattr(cls, attr), str):
                    setattr(cls, attr, "")


# ==========================================
# 数据模型
# ==========================================
@dataclass
class CleanupTarget:
    path: Path
    category: str           # 如 Dotnet, Rust, Flutter, Node.js, Android, Python, CMake, Maven
    rule: str               # 命中规则描述
    size_bytes: int = 0
    file_count: int = 0
    error: Optional[str] = None

    @property
    def formatted_size(self) -> str:
        return format_bytes(self.size_bytes)


def format_bytes(size: int) -> str:
    """人性化格式化字节数"""
    if size < 1024:
        return f"{size} B"
    elif size < 1024 * 1024:
        return f"{size / 1024:.1f} KB"
    elif size < 1024 * 1024 * 1024:
        return f"{size / (1024 * 1024):.1f} MB"
    else:
        return f"{size / (1024 * 1024 * 1024):.2f} GB"


def parse_size_str(size_str: str) -> int:
    """将如 '10MB', '1GB', '500KB' 解析为字节数"""
    size_str = size_str.strip().upper()
    multipliers = {
        "B": 1,
        "KB": 1024,
        "K": 1024,
        "MB": 1024 * 1024,
        "M": 1024 * 1024,
        "GB": 1024 * 1024 * 1024,
        "G": 1024 * 1024 * 1024,
    }
    for unit, mult in sorted(multipliers.items(), key=lambda x: -len(x[0])):
        if size_str.endswith(unit):
            num_part = size_str[:-len(unit)].strip()
            return int(float(num_part) * mult)
    return int(float(size_str))


# ==========================================
# 保护与防误删安全检测
# ==========================================
PROTECTED_DIR_NAMES = {
    ".git", ".svn", ".hg", ".bzr", ".cvs",
    "System Volume Information", "$RECYCLE.BIN",
    "Windows", "Program Files", "Program Files (x86)",
}

VENV_DIR_NAMES = {".venv", "venv", "env", ".env", ".conda"}


def is_root_or_system_path(path: Path) -> bool:
    """防止指定了盘符根目录导致严重灾难"""
    try:
        resolved = path.resolve()
        if resolved.parent == resolved:
            return True
        user_profile = os.environ.get("USERPROFILE")
        if user_profile and resolved == Path(user_profile).resolve():
            return True
    except Exception:
        pass
    return False


# ==========================================
# 辅助扫描与指纹判断
# ==========================================
def quick_list_child_names(path: Path, max_items: int = 50) -> Set[str]:
    """快速列出目录下的直接子项名称（不递归，受限数量，避免性能瓶颈）"""
    names = set()
    try:
        with os.scandir(path) as it:
            for i, entry in enumerate(it):
                if i >= max_items:
                    break
                names.add(entry.name)
    except (PermissionError, FileNotFoundError, OSError):
        pass
    return names


def has_matching_file(names: Set[str], patterns: List[str]) -> bool:
    """检查名称集合中是否存在匹配任一 glob 模式的文件"""
    for name in names:
        for pat in patterns:
            if fnmatch.fnmatch(name, pat):
                return True
    return False


# ==========================================
# 各技术栈细粒度指纹校验器
# ==========================================
class FingerprintChecker:
    @staticmethod
    def is_dotnet_target(dir_name: str, parent_path: Path, dir_path: Path) -> Tuple[bool, str]:
        """
        验证是否为 .NET 的 bin / obj 编译目录。
        必须满足：
        1. 所在工程或祖先目录存在 *.csproj, *.fsproj, *.vbproj 等
        2. bin/obj 内部包含典型编译输出目录或文件（如 Debug, Release, net8.0, project.assets.json, *.dll）
        """
        if dir_name not in ("bin", "obj"):
            return False, ""

        # 检查同级或向上两级是否有工程文件
        has_proj = False
        search_dirs = [parent_path, parent_path.parent]
        for sdir in search_dirs:
            if not sdir.exists():
                continue
            child_names = quick_list_child_names(sdir)
            if has_matching_file(child_names, ["*.csproj", "*.fsproj", "*.vbproj"]):
                has_proj = True
                break

        if not has_proj:
            return False, ""

        inner_names = quick_list_child_names(dir_path)
        dotnet_markers = {"Debug", "Release", "netcoreapp*", "net*", "x64", "x86", "arm64", "netstandard*"}
        has_marker = has_matching_file(inner_names, list(dotnet_markers))
        has_assets = "project.assets.json" in inner_names or any(
            n.endswith(".dll") or n.endswith(".pdb") or n.endswith(".cache") for n in inner_names
        )

        if dir_name == "obj" and (has_marker or has_assets or "project.nuget.cache" in inner_names):
            return True, f".NET 项目中间对象目录 ({dir_name})"
        if dir_name == "bin" and (has_marker or has_assets):
            return True, f".NET 项目编译输出目录 ({dir_name})"

        if not inner_names and has_proj:
            return True, f".NET 空编译/缓存目录 ({dir_name})"

        return False, ""

    @staticmethod
    def is_rust_target(dir_name: str, parent_path: Path, dir_path: Path) -> Tuple[bool, str]:
        """验证是否为 Rust Cargo target 目录"""
        if dir_name != "target":
            return False, ""

        has_cargo = (parent_path / "Cargo.toml").is_file() or (parent_path.parent / "Cargo.toml").is_file()
        if not has_cargo:
            return False, ""

        inner_names = quick_list_child_names(dir_path)
        if (
            "CACHEDIR.TAG" in inner_names
            or "debug" in inner_names
            or "release" in inner_names
            or ".rustc_info.json" in inner_names
            or ".fingerprint" in inner_names
            or not inner_names
        ):
            return True, "Rust Cargo 构建目标目录 (target)"

        return False, ""

    @staticmethod
    def is_flutter_target(dir_name: str, parent_path: Path, dir_path: Path) -> Tuple[bool, str]:
        """验证是否为 Flutter / Dart 构建或缓存目录"""
        has_pubspec = (parent_path / "pubspec.yaml").is_file() or (parent_path.parent / "pubspec.yaml").is_file()

        if dir_name == ".dart_tool" and has_pubspec:
            return True, "Dart/Flutter 包与工具缓存 (.dart_tool)"

        if dir_name == "build" and has_pubspec:
            inner_names = quick_list_child_names(dir_path)
            if (
                "app" in inner_names
                or "flutter_assets" in inner_names
                or "flutter_bootstrap" in inner_names
                or "windows" in inner_names
                or "linux" in inner_names
                or "web" in inner_names
                or not inner_names
            ):
                return True, "Flutter 编译构建目录 (build)"

        if dir_name == "ephemeral":
            path_str = str(dir_path).replace("\\", "/").lower()
            if "/flutter/ephemeral" in path_str:
                return True, "Flutter 平台临时生成构件 (ephemeral)"

        return False, ""

    @staticmethod
    def is_android_gradle_target(dir_name: str, parent_path: Path, dir_path: Path) -> Tuple[bool, str]:
        """验证是否为 Android / Gradle 构建和缓存目录"""
        has_gradle = any(
            (p / f).is_file()
            for p in [parent_path, parent_path.parent, parent_path.parent.parent]
            for f in ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "gradlew"]
        )

        if not has_gradle:
            return False, ""

        if dir_name == ".gradle":
            return True, "Gradle 构建缓存目录 (.gradle)"

        if dir_name == ".cxx":
            return True, "Android NDK C++ 构件缓存 (.cxx)"

        if dir_name == "build":
            inner_names = quick_list_child_names(dir_path)
            gradle_build_markers = {
                "intermediates", "outputs", "tmp", "generated", "kotlin", "reports", "classes"
            }
            if inner_names & gradle_build_markers or not inner_names:
                return True, "Android/Gradle 编译构建目录 (build)"

        return False, ""

    @staticmethod
    def is_node_target(dir_name: str, parent_path: Path, dir_path: Path) -> Tuple[bool, str]:
        """验证是否为 Node.js / 前端生态缓存或构建目录"""
        has_pkg = (parent_path / "package.json").is_file() or (parent_path.parent / "package.json").is_file()
        if not has_pkg:
            return False, ""

        if dir_name == "node_modules":
            return True, "Node.js 依赖模块目录 (node_modules)"

        if dir_name == ".next":
            return True, "Next.js 编译缓存 (.next)"

        if dir_name == ".turbo":
            return True, "Turborepo 构建缓存 (.turbo)"

        if dir_name == ".parcel-cache":
            return True, "Parcel 打包缓存 (.parcel-cache)"

        if dir_name == ".docusaurus":
            return True, "Docusaurus 构建缓存 (.docusaurus)"

        if dir_name == ".cache":
            return True, "前端工具缓存 (.cache)"

        if dir_name in ("dist", "out"):
            inner_names = quick_list_child_names(dir_path)
            has_bundle = any(
                n.endswith(".js") or n.endswith(".css") or n.endswith(".map") or n.endswith(".html")
                for n in inner_names
            )
            has_subpkg = "assets" in inner_names or "static" in inner_names
            if has_bundle or has_subpkg or not inner_names:
                return True, f"前端打包构建目录 ({dir_name})"

        return False, ""

    @staticmethod
    def is_cmake_target(dir_name: str, parent_path: Path, dir_path: Path) -> Tuple[bool, str]:
        """验证是否为 CMake 构建目录"""
        if dir_name.startswith("cmake-build-"):
            return True, f"CLion/CMake 构建目录 ({dir_name})"

        if dir_name == "build":
            if (parent_path / "CMakeLists.txt").is_file():
                inner_names = quick_list_child_names(dir_path)
                if "CMakeCache.txt" in inner_names or "CMakeFiles" in inner_names:
                    return True, "CMake 编译输出目录 (build)"

        return False, ""

    @staticmethod
    def is_python_target(dir_name: str, parent_path: Path, dir_path: Path) -> Tuple[bool, str]:
        """验证是否为 Python 缓存与构建目录"""
        if dir_name == "__pycache__":
            return True, "Python 字节码缓存 (__pycache__)"

        if dir_name in (".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox"):
            return True, f"Python 测试/代码检查缓存 ({dir_name})"

        if dir_name.endswith(".egg-info"):
            return True, f"Python 包元数据目录 ({dir_name})"

        if dir_name in ("build", "dist"):
            if (parent_path / "pyproject.toml").is_file() or (parent_path / "setup.py").is_file():
                inner_names = quick_list_child_names(dir_path)
                if any(n.endswith(".whl") or n.endswith(".tar.gz") for n in inner_names) or not inner_names:
                    return True, f"Python 打包输出目录 ({dir_name})"

        return False, ""

    @staticmethod
    def is_python_venv(dir_name: str, dir_path: Path) -> bool:
        """检查目录是否是 Python 虚拟环境"""
        if dir_name in VENV_DIR_NAMES:
            inner_names = quick_list_child_names(dir_path)
            if "pyvenv.cfg" in inner_names or "Scripts" in inner_names or "bin" in inner_names:
                return True
        return False

    @staticmethod
    def is_maven_target(dir_name: str, parent_path: Path, dir_path: Path) -> Tuple[bool, str]:
        """验证是否为 Maven 构建目录"""
        if dir_name != "target":
            return False, ""

        if (parent_path / "pom.xml").is_file():
            inner_names = quick_list_child_names(dir_path)
            if "classes" in inner_names or "generated-sources" in inner_names or "maven-archiver" in inner_names:
                return True, "Maven 构建输出目录 (target)"

        return False, ""

    @staticmethod
    def is_vs_cache(dir_name: str, parent_path: Path, dir_path: Path) -> Tuple[bool, str]:
        """验证 Visual Studio 解决方案级缓存目录 (.vs)"""
        if dir_name == ".vs":
            for p in [parent_path, parent_path.parent]:
                child_names = quick_list_child_names(p)
                if has_matching_file(child_names, ["*.sln"]):
                    return True, "Visual Studio 解决方案缓存 (.vs)"
        return False, ""


# ==========================================
# 目录大小与文件数快速计算
# ==========================================
def calculate_dir_size_and_count(dir_path: Path) -> Tuple[int, int]:
    """快速遍历计算目录大小（字节）和文件数"""
    total_size = 0
    total_files = 0
    try:
        for root, _, files in os.walk(dir_path, onerror=None):
            for f in files:
                try:
                    fpath = os.path.join(root, f)
                    total_size += os.path.getsize(fpath)
                    total_files += 1
                except (OSError, PermissionError, FileNotFoundError):
                    pass
    except Exception:
        pass
    return total_size, total_files


# ==========================================
# 扫描核心类
# ==========================================
class ProjectCleaner:
    def __init__(
        self,
        root_path: Path,
        enabled_types: Optional[Set[str]] = None,
        skip_node_modules: bool = False,
        include_venv: bool = False,
        min_size_bytes: int = 0,
        max_depth: Optional[int] = None,
    ):
        self.root_path = root_path.resolve()
        self.enabled_types = {t.lower() for t in enabled_types} if enabled_types else None
        self.skip_node_modules = skip_node_modules
        self.include_venv = include_venv
        self.min_size_bytes = min_size_bytes
        self.max_depth = max_depth
        self.targets: List[CleanupTarget] = []
        self.scanned_dirs_count = 0

    def is_type_enabled(self, category_name: str) -> bool:
        if not self.enabled_types:
            return True
        cat_lower = category_name.lower()
        aliases = {
            "node": "node.js",
            "nodejs": "node.js",
            "react": "node.js",
            "dotnet": ".net",
            "csharp": ".net",
            "rust": "rust",
            "cargo": "rust",
            "flutter": "flutter",
            "android": "android",
            "gradle": "android",
            "python": "python",
            "cmake": "cmake",
            "maven": "maven",
        }
        mapped = aliases.get(cat_lower, cat_lower)
        for t in self.enabled_types:
            alias_t = aliases.get(t, t)
            if alias_t == mapped or t == cat_lower:
                return True
        return False

    def scan(self) -> List[CleanupTarget]:
        """
        深度扫描并精准识别待清理目录。
        核心原则：
        1. 自顶向下剪枝策略；
        2. 通用剪枝优化：即便未启用特定技术栈清理，也绝不下钻其大型构件/依赖目录（如 node_modules, target, .gradle, .dart_tool）；
        3. 严禁进入受保护的版本控制与系统目录。
        """
        targets: List[CleanupTarget] = []
        checker = FingerprintChecker()

        root_depth = len(self.root_path.parts)

        for current_root, dirs, _ in os.walk(self.root_path, topdown=True, onerror=None):
            current_path = Path(current_root)
            self.scanned_dirs_count += 1

            if self.max_depth is not None:
                cur_depth = len(current_path.parts) - root_depth
                if cur_depth >= self.max_depth:
                    dirs.clear()
                    continue

            # 1. 过滤受保护目录：从 dirs 列表中直接移除，绝不下钻
            dirs[:] = [d for d in dirs if d not in PROTECTED_DIR_NAMES]

            # 2. 处理 Python 虚拟环境：
            dirs_to_prune = set()
            for d in list(dirs):
                child_path = current_path / d
                if checker.is_python_venv(d, child_path):
                    if self.include_venv and self.is_type_enabled("python"):
                        targets.append(
                            CleanupTarget(
                                path=child_path,
                                category="Python",
                                rule=f"Python 虚拟环境目录 ({d})",
                            )
                        )
                    dirs_to_prune.add(d)

            # 3. 逐个识别各技术栈目标
            for d in list(dirs):
                if d in dirs_to_prune:
                    continue

                child_path = current_path / d
                matched = False
                category = ""
                rule_desc = ""
                should_prune = False

                # --- Rust / Cargo ---
                ok, rule = checker.is_rust_target(d, current_path, child_path)
                if ok:
                    should_prune = True
                    if self.is_type_enabled("rust"):
                        matched, category, rule_desc = True, "Rust", rule

                # --- Flutter ---
                if not matched and not should_prune:
                    ok, rule = checker.is_flutter_target(d, current_path, child_path)
                    if ok:
                        should_prune = True
                        if self.is_type_enabled("flutter"):
                            matched, category, rule_desc = True, "Flutter", rule

                # --- Android / Gradle ---
                if not matched and not should_prune:
                    ok, rule = checker.is_android_gradle_target(d, current_path, child_path)
                    if ok:
                        should_prune = True
                        if self.is_type_enabled("android"):
                            matched, category, rule_desc = True, "Android", rule

                # --- .NET / C# ---
                if not matched and not should_prune:
                    ok, rule = checker.is_dotnet_target(d, current_path, child_path)
                    if ok:
                        should_prune = True
                        if self.is_type_enabled("dotnet"):
                            matched, category, rule_desc = True, ".NET", rule

                # --- Visual Studio 解决方案缓存 ---
                if not matched and not should_prune:
                    ok, rule = checker.is_vs_cache(d, current_path, child_path)
                    if ok:
                        should_prune = True
                        if self.is_type_enabled("dotnet"):
                            matched, category, rule_desc = True, ".NET", rule

                # --- Node.js / React ---
                if not matched and not should_prune:
                    ok, rule = checker.is_node_target(d, current_path, child_path)
                    if ok:
                        should_prune = True
                        if self.is_type_enabled("node"):
                            if d == "node_modules" and self.skip_node_modules:
                                pass
                            else:
                                matched, category, rule_desc = True, "Node.js", rule

                # --- CMake / C++ ---
                if not matched and not should_prune:
                    ok, rule = checker.is_cmake_target(d, current_path, child_path)
                    if ok:
                        should_prune = True
                        if self.is_type_enabled("cmake"):
                            matched, category, rule_desc = True, "CMake", rule

                # --- Python 源码级缓存/产物 ---
                if not matched and not should_prune:
                    ok, rule = checker.is_python_target(d, current_path, child_path)
                    if ok:
                        should_prune = True
                        if self.is_type_enabled("python"):
                            matched, category, rule_desc = True, "Python", rule

                # --- Maven ---
                if not matched and not should_prune:
                    ok, rule = checker.is_maven_target(d, current_path, child_path)
                    if ok:
                        should_prune = True
                        if self.is_type_enabled("maven"):
                            matched, category, rule_desc = True, "Maven", rule

                # 记录命中项目
                if matched:
                    targets.append(
                        CleanupTarget(
                            path=child_path,
                            category=category,
                            rule=rule_desc,
                        )
                    )

                # 通用剪枝：一旦该目录被判定为已知构件/依赖目录，无论用户当前是否要清理它，
                # 都绝不再下钻其内部进行冗余扫描！
                if should_prune:
                    dirs_to_prune.add(d)

            # 4. 执行剪枝
            for pd in dirs_to_prune:
                if pd in dirs:
                    dirs.remove(pd)

        self.targets = targets
        return targets

    def populate_sizes(self, max_workers: int = 8):
        """多线程并发计算所有目标目录的大小和文件数"""
        if not self.targets:
            return

        def task(item: CleanupTarget):
            size, count = calculate_dir_size_and_count(item.path)
            item.size_bytes = size
            item.file_count = count
            return item

        with concurrent.futures.ThreadPoolExecutor(max_workers=max_workers) as executor:
            list(executor.map(task, self.targets))

        if self.min_size_bytes > 0:
            self.targets = [t for t in self.targets if t.size_bytes >= self.min_size_bytes]

        self.targets.sort(key=lambda x: x.size_bytes, reverse=True)


# ==========================================
# 删除执行与 Windows 权限处理
# ==========================================
def remove_readonly(func, path, exc_info):
    """
    处理 Windows 下的只读文件删除权限错误
    """
    try:
        os.chmod(path, stat.S_IWRITE)
        func(path)
    except Exception as e:
        sys.stderr.write(f"删除失败: {path} - {e}\n")


def safe_delete_dir(target_path: Path) -> Tuple[bool, Optional[str]]:
    """安全删除目录"""
    try:
        str_path = str(target_path)
        if sys.platform == "win32" and not str_path.startswith("\\\\?\\") and len(str_path) >= 240:
            str_path = "\\\\?\\" + str_path

        shutil.rmtree(str_path, onerror=remove_readonly)
        return True, None
    except Exception as e:
        return False, str(e)


# ==========================================
# 控制台排版与报表展示
# ==========================================
def print_banner():
    print(f"{Colors.CYAN}{Colors.BOLD}======================================================================{Colors.RESET}")
    print(f"{Colors.CYAN}{Colors.BOLD}   多技术栈构建与缓存精准清理工具 (Clean Projects CLI){Colors.RESET}")
    print(f"{Colors.CYAN}{Colors.BOLD}======================================================================{Colors.RESET}")


def render_report(targets: List[CleanupTarget], root_path: Path, is_dry_run: bool, max_display: int = 50, show_all: bool = False):
    print()
    if is_dry_run:
        print(f"{Colors.YELLOW}{Colors.BOLD}[DRY-RUN 模式] 仅做扫描检测与体积统计，未删除任何文件。{Colors.RESET}")
        print(f"{Colors.DIM}  如需实际删除，请在确认无误后添加 --delete 或 -f 参数执行。{Colors.RESET}\n")
    else:
        print(f"{Colors.RED}{Colors.BOLD}[执行模式] 即将/已执行实际物理删除清理。{Colors.RESET}\n")

    if not targets:
        print(f"{Colors.GREEN}[+] 未发现需要清理的构建或缓存目录，工程目录非常整洁！{Colors.RESET}\n")
        return

    # 按技术栈分类汇总
    by_category: Dict[str, List[CleanupTarget]] = {}
    for t in targets:
        by_category.setdefault(t.category, []).append(t)

    print(f"{Colors.BOLD}{'技术栈':<12} {'目标目录数':<12} {'总文件数':<12} {'占用空间':<14}{Colors.RESET}")
    print(f"{'-'*52}")

    total_bytes = 0
    total_files = 0
    for cat, items in sorted(by_category.items(), key=lambda x: -sum(i.size_bytes for i in x[1])):
        cat_bytes = sum(i.size_bytes for i in items)
        cat_files = sum(i.file_count for i in items)
        total_bytes += cat_bytes
        total_files += cat_files
        print(f"{cat:<12} {len(items):<12} {cat_files:<12} {format_bytes(cat_bytes):<14}")

    print(f"{'-'*52}")
    print(f"{Colors.BOLD}{'总计':<12} {len(targets):<12} {total_files:<12} {format_bytes(total_bytes):<14}{Colors.RESET}\n")

    display_items = targets if show_all else targets[:max_display]

    print(f"{Colors.BOLD}[清单] 发现的待清理目录详情（按体积从大到小排序）：{Colors.RESET}")
    print(f"{'-'*110}")
    print(f"{'序号':<5} {'技术栈':<10} {'占用体积':<12} {'文件数':<8} {'路径':<72}")
    print(f"{'-'*110}")

    for idx, t in enumerate(display_items, 1):
        try:
            rel_path = t.path.relative_to(root_path)
            display_path = str(rel_path)
        except Exception:
            display_path = str(t.path)

        color = Colors.RESET
        if t.size_bytes > 500 * 1024 * 1024:
            color = Colors.RED
        elif t.size_bytes > 50 * 1024 * 1024:
            color = Colors.YELLOW

        print(f"{idx:<5} {t.category:<10} {color}{t.formatted_size:<12}{Colors.RESET} {t.file_count:<8} {display_path}")

    if not show_all and len(targets) > max_display:
        remaining = len(targets) - max_display
        rem_bytes = sum(t.size_bytes for t in targets[max_display:])
        print(f"{'-'*110}")
        print(f"{Colors.DIM}... 其余 {remaining} 个目录 (共 {format_bytes(rem_bytes)}) 未在此展开，已计入上方总计（可使用 --all 参数显示完整列表）{Colors.RESET}")

    print(f"{'-'*110}")
    print(f"[统计] 预估释放空间总量: {Colors.GREEN}{Colors.BOLD}{format_bytes(total_bytes)}{Colors.RESET} （共 {len(targets)} 个目录，{total_files} 个文件）\n")


# ==========================================
# 主流程入口
# ==========================================
def main():
    Colors.disable_if_needed()

    parser = argparse.ArgumentParser(
        description="精准清理各类项目（.NET, Rust, Flutter, Node.js, Android, CMake, Python, Maven等）的构建产物与缓存"
    )
    parser.add_argument(
        "-p", "--path",
        default=".",
        help="待扫描的根路径 (默认为当前目录)"
    )
    parser.add_argument(
        "--dry-run",
        dest="dry_run",
        action="store_true",
        default=True,
        help="模拟扫描模式，不执行删除（默认行为）"
    )
    parser.add_argument(
        "-f", "--delete", "--force",
        dest="delete_mode",
        action="store_true",
        default=False,
        help="真正执行物理删除（默认关闭）"
    )
    parser.add_argument(
        "-y", "--yes",
        dest="assume_yes",
        action="store_true",
        default=False,
        help="在执行删除时跳过交互式二次确认"
    )
    parser.add_argument(
        "-t", "--types",
        help="指定仅清理的技术栈，以逗号分隔 (例如: rust,dotnet,node,flutter,android,cmake,python,maven)"
    )
    parser.add_argument(
        "--skip-node-modules",
        action="store_true",
        default=False,
        help="跳过清理 node_modules（保留依赖，只清理构建产物与缓存）"
    )
    parser.add_argument(
        "--include-venv",
        action="store_true",
        default=False,
        help="将 Python 虚拟环境 (.venv/venv) 整体作为清理目标"
    )
    parser.add_argument(
        "--min-size",
        default=None,
        help="仅显示/清理大于指定体积的目录 (例如: 10MB, 100KB, 1GB)"
    )
    parser.add_argument(
        "--max-depth",
        type=int,
        default=None,
        help="限制最大扫描层级深度"
    )
    parser.add_argument(
        "--all",
        action="store_true",
        default=False,
        help="显示全部命中目录清单，不限制展示数量"
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=50,
        help="清单展示条目限制 (默认: 50)"
    )
    parser.add_argument(
        "-w", "--workers",
        type=int,
        default=8,
        help="并发统计大小的线程数 (默认: 8)"
    )

    args = parser.parse_args()

    root_path = Path(args.path).resolve()
    if not root_path.exists() or not root_path.is_dir():
        print(f"{Colors.RED}错误: 指定的路径不存在或不是目录: {root_path}{Colors.RESET}", file=sys.stderr)
        sys.exit(1)

    if is_root_or_system_path(root_path):
        print(f"{Colors.RED}安全拦截: 禁止直接在根驱动器或系统主目录下执行全局清理: {root_path}{Colors.RESET}", file=sys.stderr)
        sys.exit(1)

    is_dry_run = True
    if args.delete_mode:
        is_dry_run = False

    min_size_bytes = 0
    if args.min_size:
        try:
            min_size_bytes = parse_size_str(args.min_size)
        except Exception as e:
            print(f"{Colors.RED}错误: 无法解析 --min-size 参数 '{args.min_size}': {e}{Colors.RESET}", file=sys.stderr)
            sys.exit(1)

    enabled_types = None
    if args.types:
        enabled_types = {t.strip().lower() for t in args.types.split(",") if t.strip()}

    print_banner()
    print(f"[*] 扫描根路径: {Colors.CYAN}{root_path}{Colors.RESET}")
    if enabled_types:
        print(f"[*] 指定技术栈: {', '.join(sorted(enabled_types))}")
    if args.skip_node_modules:
        print(f"[*] 已开启: 跳过 node_modules 保护")
    if args.include_venv:
        print(f"[*] 已开启: 包含 Python 虚拟环境整体清理")
    if min_size_bytes > 0:
        print(f"[*] 体积阈值: >= {format_bytes(min_size_bytes)}")

    cleaner = ProjectCleaner(
        root_path=root_path,
        enabled_types=enabled_types,
        skip_node_modules=args.skip_node_modules,
        include_venv=args.include_venv,
        min_size_bytes=min_size_bytes,
        max_depth=args.max_depth,
    )

    start_time = time.time()
    print("\n[*] 正在深度扫描工程目录...", end="", flush=True)
    cleaner.scan()
    print(f" 完成（已检索 {cleaner.scanned_dirs_count} 个目录，命中 {len(cleaner.targets)} 处临时目录）")

    print("[*] 正在统计目录体积...", end="", flush=True)
    cleaner.populate_sizes(max_workers=args.workers)
    elapsed = time.time() - start_time
    print(f" 完成（耗时 {elapsed:.2f} 秒）")

    # 渲染报表
    render_report(
        cleaner.targets,
        root_path,
        is_dry_run,
        max_display=args.limit,
        show_all=args.all,
    )

    # 如果是非 dry-run，执行物理删除
    if not is_dry_run and cleaner.targets:
        if not args.assume_yes:
            print(f"{Colors.RED}{Colors.BOLD}[!] 警告: 即将永久删除上述 {len(cleaner.targets)} 个目录，释放空间 {format_bytes(sum(t.size_bytes for t in cleaner.targets))}。{Colors.RESET}")
            resp = input("[?] 请输入 'yes' 确认删除，输入其他任意内容取消: ").strip().lower()
            if resp != "yes":
                print(f"{Colors.YELLOW}操作已取消，未删除任何目录。{Colors.RESET}")
                return

        print(f"\n[*] 开始执行清理...")
        success_count = 0
        fail_count = 0
        freed_bytes = 0

        for t in cleaner.targets:
            try:
                rel_path = t.path.relative_to(root_path)
            except Exception:
                rel_path = t.path

            print(f"[-] 正在删除 [{t.category}] {rel_path} ({t.formatted_size})... ", end="", flush=True)
            ok, err = safe_delete_dir(t.path)
            if ok:
                print(f"{Colors.GREEN}成功{Colors.RESET}")
                success_count += 1
                freed_bytes += t.size_bytes
            else:
                print(f"{Colors.RED}失败: {err}{Colors.RESET}")
                fail_count += 1

        print(f"\n{Colors.GREEN}{Colors.BOLD}[OK] 清理完成！{Colors.RESET}")
        print(f"   成功删除: {success_count} 个目录")
        if fail_count > 0:
            print(f"   {Colors.RED}删除失败: {fail_count} 个目录{Colors.RESET}")
        print(f"   实际释放空间: {Colors.GREEN}{format_bytes(freed_bytes)}{Colors.RESET}\n")


if __name__ == "__main__":
    main()
