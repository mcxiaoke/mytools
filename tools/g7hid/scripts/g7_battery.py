# -*- coding: utf-8 -*-
"""
机械师 G7 2.4G 鼠标电量读取工具（本地 CLI，无需浏览器/联网）

协议来自官方网页驱动 (machenike.com/G7) 的逆向分析:
  设备: VID 0x320F / PID 0x226F, 厂商集合 usagePage 0xFF1C usage 0x92
  发送: reportId=0x04, 63 字节载荷 [00 00 CMD SUB 00*27 NESTED] (2.4G: NESTED=2)
  接收: inputreport (reportId=0x04), 响应第 8 字节(index 7)为电量百分比
  电量: 先发 CMD=0x19 SUB=0x18, 再发 CMD=0x1A SUB=0x06, 取响应 [reportId] 后第 7 字节

仅依赖 Windows 自带 hid.dll / setupapi.dll / kernel32.dll (ctypes)。
用法: python g7_battery.py
"""
import ctypes
import ctypes.wintypes as wt
import re
import sys
import time

VID = 0x320F
PID = 0x226F
REPORT_ID = 0x04
REPORT_LEN = 63          # 载荷长度（不含 reportId）
NESTED = 2               # 2.4G 模式
TIMEOUT_MS = 2000

# ---------------------------------------------------------------- Win32 常量
GENERIC_READ, GENERIC_WRITE = 0x80000000, 0x40000000
FILE_SHARE_READ, FILE_SHARE_WRITE = 1, 2
OPEN_EXISTING = 3
FILE_FLAG_OVERLAPPED = 0x40000000
INVALID_HANDLE_VALUE = -1
WAIT_OBJECT_0, WAIT_TIMEOUT = 0, 0x102
ERROR_IO_PENDING = 997
DIGCF_PRESENT, DIGCF_DEVICEINTERFACE = 0x00000002, 0x00000010


class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_ulong), ("Data2", ctypes.c_ushort),
                ("Data3", ctypes.c_ushort), ("Data4", ctypes.c_ubyte * 8)]


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_ulong), ("InterfaceClassGuid", GUID),
                ("Flags", ctypes.c_ulong), ("Reserved", ctypes.POINTER(ctypes.c_ulong))]


class SP_DEVICE_INTERFACE_DETAIL_DATA_W(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_ulong), ("DevicePath", ctypes.c_wchar * 1)]


# cbSize 必须是 sizeof(结构本身)：x64=8, x86=6（写死 6 在 x64 上会被拒绝！）
DETAIL_CB_SIZE = ctypes.sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)


class HIDD_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("Size", ctypes.c_ulong), ("VendorID", ctypes.c_ushort),
                ("ProductID", ctypes.c_ushort), ("VersionNumber", ctypes.c_ushort)]


class OVERLAPPED(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_void_p),      # ULONG_PTR
                ("InternalHigh", ctypes.c_void_p),  # ULONG_PTR
                ("Offset", ctypes.c_ulong),         # DWORD
                ("OffsetHigh", ctypes.c_ulong),     # DWORD
                ("hEvent", wt.HANDLE)]


class HIDP_CAPS(ctypes.Structure):
    # 注意: 必须与 hidpi.h 的 _HIDP_CAPS 完全一致(38 个 USHORT, 76 字节)。
    # 少定义字段会导致 HidP_GetCaps 写越界 → 堆损坏 → 随机段错误!
    _fields_ = [("Usage", ctypes.c_ushort), ("UsagePage", ctypes.c_ushort),
                ("InputReportByteLength", ctypes.c_ushort),
                ("OutputReportByteLength", ctypes.c_ushort),
                ("FeatureReportByteLength", ctypes.c_ushort),
                ("Reserved", ctypes.c_ushort * 17),
                ("NumberLinkCollectionNodes", ctypes.c_ushort),
                ("NumberInputButtonCaps", ctypes.c_ushort),
                ("NumberInputValueCaps", ctypes.c_ushort),
                ("NumberInputDataIndices", ctypes.c_ushort),
                ("NumberOutputButtonCaps", ctypes.c_ushort),
                ("NumberOutputValueCaps", ctypes.c_ushort),
                ("NumberOutputDataIndices", ctypes.c_ushort),
                ("NumberFeatureButtonCaps", ctypes.c_ushort),
                ("NumberFeatureValueCaps", ctypes.c_ushort),
                ("NumberFeatureDataIndices", ctypes.c_ushort)]


setupapi = ctypes.WinDLL("setupapi")
hid = ctypes.WinDLL("hid")
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

# ---- setupapi ----
setupapi.SetupDiGetClassDevsW.restype = wt.HANDLE
setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.POINTER(GUID), wt.LPCWSTR, wt.HWND, ctypes.c_ulong]
setupapi.SetupDiEnumDeviceInterfaces.restype = wt.BOOL
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(GUID), ctypes.c_ulong, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA)]
setupapi.SetupDiGetDeviceInterfaceDetailW.restype = wt.BOOL
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [wt.HANDLE, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA), ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong), ctypes.c_void_p]
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wt.HANDLE]
# ---- hid ----
hid.HidD_GetAttributes.restype = wt.BOOL
hid.HidD_GetAttributes.argtypes = [wt.HANDLE, ctypes.POINTER(HIDD_ATTRIBUTES)]
hid.HidD_GetPreparsedData.restype = wt.BOOL
hid.HidD_GetPreparsedData.argtypes = [wt.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
hid.HidP_GetCaps.restype = ctypes.c_long  # NTSTATUS
hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]
hid.HidD_FreePreparsedData.restype = wt.BOOL
hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
hid.HidD_SetOutputReport.restype = wt.BOOL
hid.HidD_SetOutputReport.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_ulong]
hid.HidD_SetFeature.restype = wt.BOOL
hid.HidD_SetFeature.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_ulong]
hid.HidD_GetFeature.restype = wt.BOOL
hid.HidD_GetFeature.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_ulong]
# ---- kernel32 ----
kernel32.CreateFileW.restype = wt.HANDLE
kernel32.CreateFileW.argtypes = [wt.LPCWSTR, ctypes.c_ulong, ctypes.c_ulong, wt.LPVOID, ctypes.c_ulong, ctypes.c_ulong, wt.HANDLE]
kernel32.CloseHandle.restype = wt.BOOL
kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.ReadFile.restype = wt.BOOL
kernel32.ReadFile.argtypes = [wt.HANDLE, wt.LPVOID, ctypes.c_ulong, wt.LPDWORD, wt.LPVOID]
kernel32.WriteFile.restype = wt.BOOL
kernel32.WriteFile.argtypes = [wt.HANDLE, wt.LPVOID, ctypes.c_ulong, wt.LPDWORD, wt.LPVOID]
kernel32.CreateEventW.restype = wt.HANDLE
kernel32.CreateEventW.argtypes = [wt.LPVOID, wt.BOOL, wt.BOOL, wt.LPCWSTR]
kernel32.WaitForSingleObject.restype = ctypes.c_ulong
kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, ctypes.c_ulong]
kernel32.CancelIoEx.restype = wt.BOOL
kernel32.CancelIoEx.argtypes = [wt.HANDLE, wt.LPVOID]


def log(msg):
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


# ---------------------------------------------------------------- 枚举
def bad_handle(h):
    return h is None or h in (-1, 0xFFFFFFFFFFFFFFFF, 0)


def open_device(path):
    return kernel32.CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                                FILE_SHARE_READ | FILE_SHARE_WRITE, None,
                                OPEN_EXISTING, FILE_FLAG_OVERLAPPED, None)


def enum_all_paths():
    """枚举全部 HID 接口路径（不解析 VID/PID）"""
    guid = GUID.from_buffer_copy(bytes.fromhex("b2551e4d6ff1cf1188cb001111000030"))
    hdev = setupapi.SetupDiGetClassDevsW(ctypes.byref(guid), None, None, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    if bad_handle(hdev):
        raise OSError("SetupDiGetClassDevsW 失败")
    paths = []
    idx = 0
    try:
        while True:
            ifd = SP_DEVICE_INTERFACE_DATA()
            ifd.cbSize = ctypes.sizeof(SP_DEVICE_INTERFACE_DATA)
            if not setupapi.SetupDiEnumDeviceInterfaces(hdev, None, ctypes.byref(guid), idx, ctypes.byref(ifd)):
                break
            idx += 1
            need = ctypes.c_ulong(0)
            setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(ifd), None, 0, ctypes.byref(need), None)
            if not need.value:
                continue
            buf = ctypes.create_string_buffer(need.value)
            detail = ctypes.cast(buf, ctypes.POINTER(SP_DEVICE_INTERFACE_DETAIL_DATA_W))
            detail.contents.cbSize = DETAIL_CB_SIZE
            if not setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(ifd), buf, need.value, ctypes.byref(need), None):
                continue
            paths.append(ctypes.wstring_at(ctypes.addressof(buf) + 4))
    finally:
        setupapi.SetupDiDestroyDeviceInfoList(hdev)
    return paths


def get_attrs(handle):
    attrs = HIDD_ATTRIBUTES()
    attrs.Size = ctypes.sizeof(HIDD_ATTRIBUTES)
    if hid.HidD_GetAttributes(handle, ctypes.byref(attrs)):
        return attrs.VendorID, attrs.ProductID
    return None


def enum_interfaces():
    """返回 [(path, vid, pid)]；优先从路径解析，失败的回退到打开读属性"""
    out = []
    fallback = []
    for path in enum_all_paths():
        m = re.search(r"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", path, re.I)
        if m:
            out.append((path, int(m.group(1), 16), int(m.group(2), 16)))
        else:
            fallback.append(path)
    for path in fallback:
        h = kernel32.CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, None, OPEN_EXISTING, 0, None)
        if bad_handle(h):
            continue
        try:
            a = get_attrs(h)
            if a:
                out.append((path, a[0], a[1]))
        finally:
            kernel32.CloseHandle(h)
    return out


def get_caps(handle):
    pre = ctypes.c_void_p()
    if not hid.HidD_GetPreparsedData(handle, ctypes.byref(pre)):
        return None
    caps = HIDP_CAPS()
    status = hid.HidP_GetCaps(pre, ctypes.byref(caps))
    hid.HidD_FreePreparsedData(pre)
    return caps if status >= 0 else None


# ---------------------------------------------------------------- 协议
def build_cmd(cmd, sub, payload_len=REPORT_LEN, report_id=REPORT_ID, nested=NESTED):
    """Windows 缓冲区 = [reportId] + payload(63B)
    payload = [00][00][CMD][SUB][00*27][NESTED]  (CMD 在 payload[2], NESTED 在 payload[31])
    与网页驱动 sendReport(0x04, data) 的 data 完全一致"""
    buf = (ctypes.c_ubyte * (payload_len + 1))()
    buf[0] = report_id
    buf[3], buf[4] = cmd, sub
    if payload_len >= 32:
        buf[32] = nested          # payload[31]
    elif payload_len >= 8:
        buf[payload_len] = nested  # payload[-1]
    return buf


def send_report(handle, buf, payload_len):
    """依次尝试 WriteFile(重叠) / SetOutputReport / SetFeature，返回使用的方式名"""
    ev = kernel32.CreateEventW(None, True, False, None)
    if not ev:
        return None
    ov = OVERLAPPED()
    ov.hEvent = ev
    written = ctypes.c_ulong(0)
    ok = kernel32.WriteFile(handle, buf, payload_len + 1, ctypes.byref(written), ctypes.byref(ov))
    err = ctypes.get_last_error()
    if not ok and err == ERROR_IO_PENDING:
        ok = kernel32.WaitForSingleObject(ev, TIMEOUT_MS) == WAIT_OBJECT_0
        err = ctypes.get_last_error()
    if ok:
        kernel32.CloseHandle(ev)
        return "WriteFile(output)"
    log(f"    WriteFile 失败 err={err}")
    kernel32.CancelIoEx(handle, ctypes.byref(ov))
    kernel32.CloseHandle(ev)
    if hid.HidD_SetOutputReport(handle, buf, payload_len + 1):
        return "HidD_SetOutputReport"
    if hid.HidD_SetFeature(handle, buf, payload_len + 1):
        return "HidD_SetFeature"
    return None


def recv_input_report(handle, timeout_ms):
    """重叠读一条输入报告，返回 bytes（含 reportId 首字节）或 None"""
    ev = kernel32.CreateEventW(None, True, False, None)
    if not ev:
        return None
    try:
        ov = OVERLAPPED()
        ov.hEvent = ev
        buf = (ctypes.c_ubyte * 256)()
        n = ctypes.c_ulong(0)
        if not kernel32.ReadFile(handle, buf, 256, ctypes.byref(n), ctypes.byref(ov)):
            if ctypes.get_last_error() != ERROR_IO_PENDING:
                return None
            if kernel32.WaitForSingleObject(ev, timeout_ms) != WAIT_OBJECT_0:
                kernel32.CancelIoEx(handle, ctypes.byref(ov))
                return None
        got = ov.InternalHigh or n.value
        return bytes(buf[:got])
    finally:
        kernel32.CloseHandle(ev)


def read_battery(path, watch=False, watch_secs=60):
    """对单个接口执行完整电量流程。成功返回电量值，失败返回 None"""
    handle = open_device(path)
    if bad_handle(handle):
        log("  打开失败（可能被浏览器/驱动占用，请先关闭网页驱动页面）")
        return None
    try:
        caps = get_caps(handle)
        if caps:
            log(f"  caps: usagePage={caps.UsagePage:#06x} usage={caps.Usage:#06x} "
                f"IN={caps.InputReportByteLength}B OUT={caps.OutputReportByteLength}B FEAT={caps.FeatureReportByteLength}B")
        # 先按驱动的 63 字节载荷试；若设备按描述符只收 7 字节，再回退
        for payload_len in (REPORT_LEN, 7):
            log(f"  —— 尝试载荷长度 {payload_len} 字节 ——")
            # 驱动连接流程: 2.4G 先发 0xAA 配对检查（响应 byte7/byte8 为 ff 表示该槽位未配对）
            probe_session(handle, payload_len)
            ok = True
            for cmd, sub, tag in [(0x19, 0x18, "0x19"), (0x1A, 0x06, "0x1A")]:
                how = send_report(handle, build_cmd(cmd, sub, payload_len), payload_len)
                if how is None:
                    log(f"  发送 {tag} 失败（WriteFile/SetOutputReport/SetFeature 均被拒绝）")
                    ok = False
                    break
                log(f"  → {tag} via {how}")
                rep = recv_input_report(handle, TIMEOUT_MS)
                if rep is None:
                    fbuf = (ctypes.c_ubyte * (payload_len + 1))()
                    fbuf[0] = REPORT_ID
                    if hid.HidD_GetFeature(handle, fbuf, payload_len + 1) and any(fbuf[1:9]):
                        rep = bytes(fbuf)
                        log(f"  ← {tag} 响应(GetFeature) {rep.hex(' ')}")
                    else:
                        log(f"  {tag} 无响应（{TIMEOUT_MS}ms 超时）")
                        ok = False
                        break
                else:
                    log(f"  ← {tag} 响应 {rep.hex(' ')}")
                if cmd == 0x1A and rep:
                    if len(rep) >= 9 and rep[0] == REPORT_ID:
                        log(f"  电量候选: payload[7]=rep[8]={rep[8]}  (驱动语义)")
                        val = rep[8]
                    elif len(rep) >= 8:
                        log(f"  电量候选: rep[7]={rep[7]}")
                        val = rep[7]
                    else:
                        ok = False
                        break
                    if val > 0 or not watch:
                        return val
                    # 电量为 0: 2.4G 鼠标可能休眠，进入守望模式
                    log("  读到 0% —— 2.4G 鼠标可能在休眠。进入守望模式，请移动/点击鼠标唤醒它…")
                    return watch_loop(handle, payload_len, watch_secs)
            if ok:
                return None
        return None
    finally:
        kernel32.CloseHandle(handle)


def watch_loop(handle, payload_len, watch_secs):
    """持续轮询 0x19→0x1A，等待鼠标被唤醒后返回真实电量"""
    deadline = time.time() + watch_secs
    last = None
    while time.time() < deadline:
        got = True
        for cmd, sub, tag in [(0x19, 0x18, "0x19"), (0x1A, 0x06, "0x1A")]:
            how = send_report(handle, build_cmd(cmd, sub, payload_len), payload_len)
            if how is None:
                got = False
                break
            rep = recv_input_report(handle, TIMEOUT_MS)
            if rep is None:
                got = False
                break
            if cmd == 0x1A and rep:
                log(f"  [watch] 0x1A 原始响应 {rep.hex(' ')}")
                if len(rep) >= 9 and rep[0] == REPORT_ID:
                    val = rep[8]
                elif len(rep) >= 8:
                    val = rep[7]
                else:
                    val = None
                if val != last:
                    log(f"  [watch] 电量 = {val}%")
                    last = val
                if val:
                    return val
        if not got:
            return None
        time.sleep(2)
    log(f"  [watch] {watch_secs} 秒内未读到非零电量（鼠标是否开机/已配对？）")
    return None


def probe_session(handle, payload_len):
    """驱动完整握手流程: 0xAA 配对检查 → 0x30 读 cid → 0x19 → 0x1A，全部转储用于对齐分析"""
    log("  === 完整握手探测（与驱动连接流程一致） ===")
    for cmd, sub, param, tag in [(0xAA, 0x00, 0, "0xAA配对"),
                                 (0x30, 0x10, 0, "0x30cid?"),
                                 (0x30, 0x10, 1, "0x30槽1"),
                                 (0x30, 0x10, 2, "0x30槽2"),
                                 (0x19, 0x18, 2, "0x19"),
                                 (0x1A, 0x06, 2, "0x1A电量")]:
        how = send_report(handle, build_cmd(cmd, sub, payload_len, nested=param), payload_len)
        if how is None:
            log(f"  {tag}: 发送失败")
            continue
        rep = recv_input_report(handle, TIMEOUT_MS)
        if rep is None:
            log(f"  {tag}: 无响应")
            continue
        idx = " ".join(f"{i}:{b:02x}" for i, b in enumerate(rep))
        log(f"  {tag} ({len(rep)}B): {idx}")


def main():
    log("枚举 HID 设备…")
    devs = enum_interfaces()
    log(f"共 {len(devs)} 个 HID 接口")
    targets = [d for d in devs if d[1] == VID and d[2] == PID]
    if not targets:
        log(f"未找到 VID={VID:#06x} PID={PID:#06x} 的设备（接收器是否已插入 USB？）")
        return 1
    log(f"匹配 G7 接收器（{VID:#06x}:{PID:#06x}）的接口 {len(targets)} 个：")
    for p, _, _ in targets:
        log(f"  - {p}")

    for path, _, _ in targets:
        log(f"尝试接口: …{path[-60:]}")
        val = read_battery(path, watch=True)
        if val is not None:
            log(f"★ 电量 = {val}%")
            return 0
    log("所有接口均未读到电量。")
    log("提示：1) 先完全关闭浏览器里的 machenike 驱动页面再运行；2) 可改用 g7_battery.html（WebHID 方式）交叉验证。")
    return 2


if __name__ == "__main__":
    sys.exit(main())
