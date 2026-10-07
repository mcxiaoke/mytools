/*
 * nodelete.c -- 目录删除保护（NoDelete）
 *
 * 原理
 * ----
 * Windows 资源管理器（以及绝大多数程序）使用「规范化」路径访问文件：Win32 会
 * 自动剥掉文件名末尾的点和空格。于是名为 ".NO_DELETE." 的文件：
 *
 *   - 用普通路径  "...\.NO_DELETE."  访问 -> 实际命中 "...\.NO_DELETE"（不存在）
 *   - 但用扩展路径 "\\?\...\.NO_DELETE." 访问 -> 精确命中该文件
 *
 * Explorer 能枚举到它、却无法生成一条指向它的合法路径，因此删除父目录时会
 * 因「目录非空」而失败（ERROR_DIR_NOT_EMPTY / 145）。要解除保护，必须用
 * 扩展路径把这个文件删掉。
 *
 * 设计要点
 * --------
 * 1) 核心逻辑与界面分离：全部逻辑在 nodelete_core()，不弹窗、返回错误码，
 *    可被自动化脚本 / 控制台模式调用，也便于单元测试。
 * 2) 正确的扩展路径构造：UNC 路径必须写成 \\?\UNC\server\share，直接给
 *    \\server\share 加前缀会得到非法的 \\?\\server\share。这是常见坑。
 * 3) 幂等：create 已存在时返回成功并说明状态；remove 不存在时也算成功。
 * 4) 路径规范化：统一分隔符、去掉末尾多余的 '\'、处理空路径与 "." ，
 *    避免拼出 "dir\\\\.NO_DELETE." 这类畸形路径。
 * 5) 验证：删除标记后主动确认父目录确实不再受保护，把结果如实反馈给用户。
 *
 * 编译：gcc -O2 -municode -o nodelete.exe nodelete.c -lshell32
 *      GUI 版额外加 -mwindows 并提供 WinMain 入口（见 build.sh）。
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <io.h>
#include <fcntl.h>

/*
 * 控制台输出统一走 UTF-8。
 *
 * 不能直接用 fwprintf 输出宽字符：重定向到管道/文件时得到的是 UTF-16 字节流，
 * 脚本读取即乱码；_O_U8TEXT 又依赖"真实控制台"，在 Git Bash / MSYS 管道下
 * 同样不生效。这里自己把宽字符串转成 UTF-8 再按字节写出，控制台、重定向、
 * 管道三种场景都能得到正确文本。
 */
static void nd_write_utf8(FILE *f, const wchar_t *ws)
{
    if (!ws || !*ws) return;

    int need = WideCharToMultiByte(CP_UTF8, 0, ws, -1, NULL, 0, NULL, NULL);
    if (need <= 1) return;                 /* need 含结尾 '\0' */
    need -= 1;

    char *buf = (char *)malloc((size_t)need);
    if (!buf) return;
    if (WideCharToMultiByte(CP_UTF8, 0, ws, -1, buf, need + 1, NULL, NULL) > 0)
        fwrite(buf, 1, (size_t)need, f);
    free(buf);
}

/* 输出一行（自动补换行） */
static void nd_wprintln(FILE *f, const wchar_t *ws)
{
    nd_write_utf8(f, ws);
    fputc('\n', f);
}

/* 保护标记文件名：前导点无实际作用，末尾点才是关键（阻断规范化路径访问） */
static const wchar_t *const MARKER_NAME = L".NO_DELETE.";

/* 保护标记内写入的说明文本，便于用户手工辨认（用记事本+"所有文件"可见） */
static const wchar_t *const MARKER_BODY =
    L"NoDelete marker\r\n"
    L"This file makes its folder undeletable from Windows Explorer.\r\n"
    L"Remove it with:  nodelete.exe remove \"<folder>\"\r\n";

typedef enum {
    MODE_CREATE = 0,   /* 加保护：创建标记（已存在则保持不变） */
    MODE_REMOVE,       /* 解除：删除标记 */
    MODE_TOGGLE,       /* 切换 */
    MODE_STATUS        /* 只查询，不改动 */
} nd_mode;

typedef enum {
    ND_OK = 0,
    ND_ERR_USAGE,        /* 参数错误 */
    ND_ERR_ACCESS,        /* 权限不足 */
    ND_ERR_NOTDIR,        /* 目标不是目录 */
    ND_ERR_CREATE,        /* 创建标记失败 */
    ND_ERR_DELETE,        /* 删除标记失败 */
    ND_ERR_INTERNAL       /* 内存/路径处理失败 */
} nd_status;

/* 供界面层使用的详细结果 */
typedef struct {
    nd_status status;
    int       changed;        /* 本次是否真的发生了改动 */
    int       marker_existed; /* 操作前标记是否存在 */
    wchar_t   dir[32768];     /* 目标目录（规范化后，普通路径形式） */
    wchar_t   marker[32768];  /* 标记完整扩展路径 */
    DWORD     err;            /* 失败时的 Win32 错误码，0 表示无 */
} nd_result;

/* ------------------------------------------------------------------ */
/* 工具函数                                                            */
/* ------------------------------------------------------------------ */

/* 取路径的父目录部分；已是目录则原样返回。失败返回 0。 */
static int nd_is_dir(const wchar_t *path)
{
    DWORD a = GetFileAttributesW(path);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

/* 统一分隔符为 '\'，并去掉末尾多余的 '\'（保留盘符根 "C:\"）。 */
static void nd_normalize_seps(wchar_t *p)
{
    wchar_t *w;
    for (w = p; *w; ++w)
        if (*w == L'/') *w = L'\\';

    /* 反复去掉末尾 '\\'，但 "C:\" / "\\" / "\\server\share\" 不能被削成 "C:" */
    size_t n = wcslen(p);
    while (n > 0 && p[n - 1] == L'\\') {
        /* 保留 "X:\" 形式的根 */
        if (n == 3 && p[1] == L':') break;
        /* 保留 "\\" （UNC 起始） */
        if (n == 1) break;
        /* 保留 "\\server\share\" 中的 share */
        if (n == 2 && p[0] == L'\\' && p[1] == L'\\') break;
        p[--n] = L'\0';
    }
}

/* 路径是否存在（存在即可，无论是文件还是目录） */
static int nd_path_exists(const wchar_t *path)
{
    return GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;
}

/*
 * 规范化目标目录。
 *
 * 严格区分三种情况，避免"打错字就悄悄保护上级目录"这种危险行为：
 *   - 目标是存在的目录 -> 用它自己
 *   - 目标是存在的文件 -> 用它所在的目录（这样可对单个文件所在目录加保护）
 *   - 目标根本不存在   -> 失败（返回 0），绝不回退到父目录
 *   - 纯文件名且存在   -> 当前目录
 *
 * 返回 1 表示 out 中是有效的目录路径。
 */
static int nd_resolve_dir(const wchar_t *target, wchar_t *out, size_t cap)
{
    if (!target || !*target) return 0;

    /* 已是目录（含扩展路径 \\?\ 前缀） */
    if (nd_is_dir(target)) {
        if (wcslen(target) >= cap) return 0;
        wcscpy(out, target);
        nd_normalize_seps(out);
        return 1;
    }

    /*
     * 到这里说明它不是目录。必须确认它确实是个已存在的文件：
     * 若路径不存在，说明用户多半打错了名字，此时若继续取父目录，
     * 会把保护加到完全无关的上级目录上，且不会有任何提示。
     */
    if (!nd_path_exists(target)) return 0;

    /* 复制一份再切父目录，避免写坏调用方的字符串 */
    if (wcslen(target) >= cap) return 0;
    wcscpy(out, target);
    nd_normalize_seps(out);

    /* 找最后一个分隔符 */
    wchar_t *last = NULL;
    for (wchar_t *w = out; *w; ++w)
        if (*w == L'\\') last = w;

    if (!last) {
        /* 纯文件名（且该文件存在）-> 当前目录 */
        wcscpy(out, L".");
        return 1;
    }

    if (last == out) {
        /* "\foo" -> 根 "\" */
        out[1] = L'\0';
        return 1;
    }

    *last = L'\0';
    /* 去掉 "C:" 这种盘符相对形式带来的尾部残留，例如 "C:file.txt" -> "C:" */
    size_t n = wcslen(out);
    if (n == 2 && out[1] == L':') {
        out[2] = L'\\';
        out[3] = L'\0';
    }
    nd_normalize_seps(out);
    return out[0] != L'\0';
}

/*
 * 把普通路径转成 \\?\ 扩展路径（用于访问末尾点文件）。
 *
 * 关键点：UNC 路径必须转换前缀，否则得到非法的 \\?\\server\share。
 *   "\\?\C:\dir"                  -> 原样保留
 *   "\\server\share\dir"          -> "\\?\UNC\server\share\dir"
 *   "\\?\UNC\server\share\dir"   -> 原样保留
 *   "C:\dir"                      -> "\\?\C:\dir"
 * 成功返回 1。
 */
static int nd_to_extended(const wchar_t *dir, wchar_t *out, size_t cap)
{
    size_t need;
    if (wcslen(dir) >= cap) return 0;

    if (wcsncmp(dir, L"\\\\?\\", 4) == 0) {
        wcscpy(out, dir);
        return 1;
    }

    if (dir[0] == L'\\' && dir[1] == L'\\') {
        /* UNC：把开头的 "\\\\" 换成 "\\?\UNC\"（末尾这个反斜杠不能漏） */
        need = 4 + wcslen(dir);          /* "\\?\UNC" + (dir 去掉前两个字符) */
        if (need >= cap) return 0;
        wcscpy(out, L"\\\\?\\UNC\\");
        wcscat(out, dir + 2);
        return 1;
    }

    /* 普通（可能是相对）路径：先转绝对路径 */
    wchar_t abs[32768];
    DWORD n = GetFullPathNameW(dir, 32768, abs, NULL);
    if (n == 0 || n >= 32768) return 0;

    need = 4 + wcslen(abs);
    if (need >= cap) return 0;
    wcscpy(out, L"\\\\?\\");
    wcscat(out, abs);
    return 1;
}

/* 组合扩展目录 + 标记名，结果为可直接交给 Win32 的完整路径 */
static int nd_build_marker(const wchar_t *ext_dir, wchar_t *out, size_t cap)
{
    size_t need = wcslen(ext_dir) + 1 + wcslen(MARKER_NAME) + 1;
    if (need > cap) return 0;
    wcscpy(out, ext_dir);
    if (out[0] && out[wcslen(out) - 1] != L'\\')
        wcscat(out, L"\\");
    wcscat(out, MARKER_NAME);
    return 1;
}

/* 通过扩展路径判断标记是否存在（普通路径查不到它） */
static int nd_marker_exists(const wchar_t *ext_marker)
{
    return GetFileAttributesW(ext_marker) != INVALID_FILE_ATTRIBUTES;
}

/* ------------------------------------------------------------------ */
/* 核心操作                                                            */
/* ------------------------------------------------------------------ */

static nd_status nd_create(const wchar_t *ext_marker, nd_result *r)
{
    if (nd_marker_exists(ext_marker)) {
        r->changed = 0;          /* 幂等：已存在即视为成功 */
        return ND_OK;
    }

    /* CREATE_NEW 保证原子性：并发下不会互相覆盖 */
    HANDLE h = CreateFileW(ext_marker,
                           GENERIC_WRITE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL,
                           CREATE_NEW,
                           FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM,
                           NULL);
    if (h == INVALID_HANDLE_VALUE) {
        r->err = GetLastError();
        return (r->err == ERROR_ACCESS_DENIED) ? ND_ERR_ACCESS : ND_ERR_CREATE;
    }

    /* 写入说明文字（失败也不影响保护效果，标记本身已存在即生效） */
    const wchar_t *body = MARKER_BODY;
    DWORD remaining = (DWORD)(wcslen(body) * sizeof(wchar_t));
    const wchar_t *p = body;
    while (remaining > 0) {
        DWORD written = 0;
        if (!WriteFile(h, p, remaining, &written, NULL) || written == 0) break;
        p += written / sizeof(wchar_t);
        remaining -= written;
    }
    CloseHandle(h);

    /* 立即验证：标记确实可被扩展路径访问 */
    if (!nd_marker_exists(ext_marker)) {
        r->err = ERROR_WRITE_FAULT;
        return ND_ERR_CREATE;
    }

    r->changed = 1;
    return ND_OK;
}

static nd_status nd_remove(const wchar_t *ext_marker, nd_result *r)
{
    if (!nd_marker_exists(ext_marker)) {
        r->changed = 0;          /* 幂等：本来就没保护 */
        return ND_OK;
    }

    if (!DeleteFileW(ext_marker)) {
        r->err = GetLastError();
        return (r->err == ERROR_ACCESS_DENIED) ? ND_ERR_ACCESS : ND_ERR_DELETE;
    }

    r->changed = 1;
    return ND_OK;
}

/*
 * 入口：执行一次操作并填充结果。
 * target 可以是目录，也可以是目录下的某个文件（取其父目录）。
 *
 * 注意：所有失败路径都必须写回 r->status —— 调用方（命令行 / 图形界面）
 * 依赖它来决定退出码和提示图标；若提前 return 而不记录，会出现"明明出错
 * 却返回 0"的情况，脚本就无法判断成败。
 */
nd_status nodelete_core(nd_mode mode, const wchar_t *target, nd_result *r)
{
    if (!r) return ND_ERR_INTERNAL;
    ZeroMemory(r, sizeof(*r));
    r->status = ND_OK;

    if (!target || !*target) {
        r->status = ND_ERR_USAGE;
        return r->status;
    }

    /*
     * 1) 解析目标目录。
     *    失败意味着：路径为空/过长，或路径根本不存在（打错字）。
     *    无论哪种，都不能退而取父目录——否则会把保护加到无关的目录上。
     *    这里先把原始输入记下来，便于报错时告诉用户到底传了什么。
     */
    if (!nd_resolve_dir(target, r->dir, 32768)) {
        if (wcslen(target) < 32768) {
            wcscpy(r->dir, target);
            nd_normalize_seps(r->dir);
        }
        r->status = ND_ERR_NOTDIR;
        return r->status;
    }

    /* 2) 确认它是目录 */
    if (!nd_is_dir(r->dir)) {
        r->status = ND_ERR_NOTDIR;
        return r->status;
    }

    /* 3) 构造扩展路径 + 标记路径 */
    {
        wchar_t ext_dir[32768];
        if (!nd_to_extended(r->dir, ext_dir, 32768) ||
            !nd_build_marker(ext_dir, r->marker, 32768)) {
            r->status = ND_ERR_INTERNAL;
            return r->status;
        }
    }

    r->marker_existed = nd_marker_exists(r->marker);

    /* 4) 执行 */
    nd_status st;
    switch (mode) {
    case MODE_CREATE: st = nd_create(r->marker, r); break;
    case MODE_REMOVE: st = nd_remove(r->marker, r); break;
    case MODE_TOGGLE:
        st = r->marker_existed ? nd_remove(r->marker, r) : nd_create(r->marker, r);
        break;
    case MODE_STATUS:
    default:
        st = ND_OK;
        break;
    }
    r->status = st;
    return st;
}

/* ------------------------------------------------------------------ */
/* 结果文本与错误说明                                                  */
/* ------------------------------------------------------------------ */

static void nd_format_sys_error(DWORD e, wchar_t *buf, size_t cap)
{
    LPWSTR msg = NULL;
    DWORD n = FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER |
                                 FORMAT_MESSAGE_FROM_SYSTEM |
                                 FORMAT_MESSAGE_IGNORE_INSERTS,
                             NULL, e, 0, (LPWSTR)&msg, 0, NULL);
    if (n && msg) {
        /* FormatMessage 会带上换行，去掉更整洁 */
        wchar_t *nl = wcschr(msg, L'\r');
        if (nl) *nl = L'\0';
        nl = wcschr(msg, L'\n');
        if (nl) *nl = L'\0';
        _snwprintf(buf, cap, L"%s (错误码 %lu)", msg, (unsigned long)e);
        LocalFree(msg);
    } else {
        _snwprintf(buf, cap, L"未知错误 (错误码 %lu)", (unsigned long)e);
    }
    buf[cap - 1] = L'\0';
}

/* 把结果渲染成给用户看的一段文字 */
static void nd_render_message(const nd_result *r, wchar_t *buf, size_t cap)
{
    wchar_t errbuf[256] = L"";

    if (r->status != ND_OK && r->err)
        nd_format_sys_error(r->err, errbuf, 256);

    /* 按错误码给出简短结论 */
    const wchar_t *head;
    switch (r->status) {
    case ND_ERR_USAGE:      head = L"参数错误"; break;
    case ND_ERR_ACCESS:     head = L"权限不足"; break;
    case ND_ERR_NOTDIR:     head = L"路径不存在或不是目录"; break;
    case ND_ERR_CREATE:     head = L"创建保护标记失败"; break;
    case ND_ERR_DELETE:     head = L"删除保护标记失败"; break;
    case ND_ERR_INTERNAL:   head = L"路径处理失败（路径过长或无效）"; break;
    default:                head = L""; break;
    }

    if (r->status != ND_OK) {
        if (r->status == ND_ERR_NOTDIR) {
            _snwprintf(buf, cap,
                       L"%s：\n%s\n\n请检查路径是否拼写正确。为避免误操作，"
                       L"本工具不会把保护加到上级目录。",
                       head, r->dir);
        } else {
            _snwprintf(buf, cap, L"%s：%s\n\n目录：\n%s", head, errbuf, r->dir);
        }
        buf[cap - 1] = L'\0';
        return;
    }

    /* 成功：区分 create/remove/toggle/status 的结果语义 */
    wchar_t state[64];
    int now_protected = nd_marker_exists(r->marker);
    _snwprintf(state, 64, L"%s", now_protected ? L"已保护（禁止删除）" : L"未保护（可正常删除）");

    _snwprintf(buf, cap,
               L"%s\n%s\n\n目录：\n%s\n\n标记文件：\n%s\n\n"
               L"提示：资源管理器无法手工删除该标记，需再次运行本工具解除。",
               state,
               r->changed ? L"（本次操作已生效）" : L"（状态本来就如此，无需改动）",
               r->dir, r->marker);
    buf[cap - 1] = L'\0';
}

static const wchar_t *nd_status_word(const nd_result *r)
{
    if (r->status != ND_OK) return L"ERROR";
    return nd_marker_exists(r->marker) ? L"PROTECTED" : L"UNPROTECTED";
}

/* ------------------------------------------------------------------ */
/* 命令行解析                                                          */
/* ------------------------------------------------------------------ */

static void nd_usage(wchar_t *buf, size_t cap)
{
    _snwprintf(buf, cap,
        L"NoDelete -- 目录删除保护工具\n\n"
        L"用法：\n"
        L"  nodelete.exe     [create|remove|toggle|status] <目录或文件>   控制台版\n"
        L"  NoDeleteGUI.exe  [create|remove|toggle|status] <目录或文件>   图形版(弹窗)\n\n"
        L"说明：\n"
        L"  create  在目标目录创建 .NO_DELETE. 标记，使该目录无法从资源管理器删除（默认）\n"
        L"  remove  删除标记，恢复可删除\n"
        L"  toggle  有标记则删除，无标记则创建\n"
        L"  status  仅查询当前状态，不做修改\n\n"
        L"从“发送到”菜单调用时，直接把文件夹拖到菜单项上，等价于 create。\n"
        L"加 --quiet 可静默执行（不弹窗），适合脚本调用；退出码 0 表示成功。\n\n"
        L"示例：\n"
        L"  nodelete.exe create \"D:\\MyFolder\"\n"
        L"  nodelete.exe remove \"D:\\MyFolder\"\n"
        L"  nodelete.exe status  \"D:\\MyFolder\"\n"
        L"  nodelete.exe --quiet toggle \"D:\\MyFolder\"");
    buf[cap - 1] = L'\0';
}

static int nd_parse_mode(const wchar_t *s, nd_mode *out)
{
    if (_wcsicmp(s, L"create") == 0 || _wcsicmp(s, L"on") == 0 || _wcsicmp(s, L"protect") == 0) {
        *out = MODE_CREATE; return 1;
    }
    if (_wcsicmp(s, L"remove") == 0 || _wcsicmp(s, L"off") == 0 ||
        _wcsicmp(s, L"delete") == 0 || _wcsicmp(s, L"unprotect") == 0) {
        *out = MODE_REMOVE; return 1;
    }
    if (_wcsicmp(s, L"toggle") == 0) { *out = MODE_TOGGLE; return 1; }
    if (_wcsicmp(s, L"status") == 0 || _wcsicmp(s, L"query") == 0) { *out = MODE_STATUS; return 1; }
    return 0;
}

/*
 * 控制台入口 wmain：完整 CLI，支持静默模式和退出码，便于脚本/测试使用。
 */
int wmain(int argc, wchar_t **argv)
{
    nd_mode mode = MODE_CREATE;
    const wchar_t *target = NULL;
    int quiet = 0;
    int i;

    for (i = 1; i < argc; ++i) {
        if (_wcsicmp(argv[i], L"--quiet") == 0 || _wcsicmp(argv[i], L"-q") == 0) {
            quiet = 1;
        } else if (_wcsicmp(argv[i], L"--help") == 0 || _wcsicmp(argv[i], L"-h") == 0 ||
                   _wcsicmp(argv[i], L"/?") == 0) {
            wchar_t u[2048];
            nd_usage(u, 2048);
            nd_wprintln(stdout, u);
            return 0;
        } else if (!target) {
            nd_mode m;
            if (nd_parse_mode(argv[i], &m)) {
                mode = m;
            } else {
                target = argv[i];   /* 省略动词时第一个非选项参数即目标 */
            }
        } else {
            target = argv[i];       /* 多余参数：取最后一个作为目标 */
        }
    }

    if (!target) {
        wchar_t u[2048];
        nd_usage(u, 2048);
        if (!quiet) nd_wprintln(stderr, u);
        return ND_ERR_USAGE;
    }

    nd_result r;
    nodelete_core(mode, target, &r);

    wchar_t msg[4096];
    nd_render_message(&r, msg, 4096);

    if (quiet) {
        /* 机器可读：状态<TAB>目录，便于脚本解析 */
        nd_write_utf8(stdout, nd_status_word(&r));
        fputc('\t', stdout);
        nd_write_utf8(stdout, r.dir);
        fputc('\n', stdout);
    } else {
        nd_wprintln(stdout, msg);
    }
    fflush(stdout);
    return r.status;
}

/* ------------------------------------------------------------------ */
/* 图形界面入口（用于“发送到”菜单）                                    */
/* ------------------------------------------------------------------ */
/*
 * 单独编译（-mwindows）时使用本入口，程序不弹控制台窗口，只显示 MessageBox。
 * 与 wmain 共用上面的全部逻辑。
 */
int WINAPI WinMain(HINSTANCE hInst, HINSTANCE hPrev, LPSTR lpCmd, int nShow)
{
    (void)hInst; (void)hPrev; (void)lpCmd; (void)nShow;

    int argc = 0;
    wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) {
        MessageBoxW(NULL, L"无法解析命令行。", L"NoDelete", MB_OK | MB_ICONERROR);
        return ND_ERR_INTERNAL;
    }

    nd_mode mode = MODE_CREATE;
    const wchar_t *target = NULL;
    int quiet = 0;

    for (int i = 1; i < argc; ++i) {
        if (_wcsicmp(argv[i], L"--quiet") == 0 || _wcsicmp(argv[i], L"-q") == 0) {
            quiet = 1;
        } else if (_wcsicmp(argv[i], L"--help") == 0 || _wcsicmp(argv[i], L"-h") == 0) {
            wchar_t u[2048];
            nd_usage(u, 2048);
            MessageBoxW(NULL, u, L"NoDelete 使用说明", MB_OK | MB_ICONINFORMATION);
            LocalFree(argv);
            return 0;
        } else if (!target) {
            nd_mode m;
            if (nd_parse_mode(argv[i], &m)) mode = m;
            else target = argv[i];
        } else {
            target = argv[i];
        }
    }

    if (!target) {
        wchar_t u[2048];
        nd_usage(u, 2048);
        MessageBoxW(NULL, u, L"NoDelete 使用说明", MB_OK | MB_ICONINFORMATION);
        LocalFree(argv);
        return ND_ERR_USAGE;
    }

    nd_result r;
    nodelete_core(mode, target, &r);

    if (!quiet) {
        wchar_t msg[4096];
        nd_render_message(&r, msg, 4096);
        UINT icon = (r.status == ND_OK) ? MB_ICONINFORMATION : MB_ICONERROR;
        MessageBoxW(NULL, msg, L"NoDelete -- 目录删除保护", MB_OK | icon);
    }

    LocalFree(argv);
    return r.status;
}
