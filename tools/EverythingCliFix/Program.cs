using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EverythingCliFix {
    internal class Program {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetCommandLine();
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
        [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(
            string lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        static string FindEsExe() {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string localEs = Path.Combine(baseDir, "escli.exe");
            if (File.Exists(localEs))
                return localEs;
            string fixedPath = @"C:\Home\Tools\bin\escli.exe";
            if (File.Exists(fixedPath))
                return fixedPath;
            return "escli.exe";
        }

        static string GetArgsRemainder(string cmdLine) {
            if (string.IsNullOrEmpty(cmdLine))
                return "";
            cmdLine = cmdLine.Trim();
            if (cmdLine.Length == 0)
                return "";
            if (cmdLine[0] == '"') {
                int end = cmdLine.IndexOf('"', 1);
                if (end != -1)
                    return cmdLine.Substring(end + 1).Trim();
            }
            int sp = cmdLine.IndexOf(' ');
            return sp != -1 ? cmdLine.Substring(sp + 1).Trim() : "";
        }

        static int Main(string[] args) {
            string esPath = FindEsExe();
            string rawCmd = Marshal.PtrToStringUni(GetCommandLine());
            string remainder = GetArgsRemainder(rawCmd);

            string targetCmd = "\"" + esPath + "\"";
            if (!string.IsNullOrEmpty(remainder)) {
                targetCmd += " " + remainder;
            }

            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = @"WinSta0\Default"; // 关键：指定桌面
            si.dwFlags = 0x00000100;           // STARTF_USESTDHANDLES
            si.hStdInput = GetStdHandle(-10);
            si.hStdOutput = GetStdHandle(-11);
            si.hStdError = GetStdHandle(-12);

            PROCESS_INFORMATION pi;
            if (!CreateProcess(null, targetCmd, IntPtr.Zero, IntPtr.Zero, true, 0, IntPtr.Zero, null, ref si, out pi)) {
                Console.Error.WriteLine("es2: CreateProcess failed (" + Marshal.GetLastWin32Error() + ")");
                return 1;
            }

            WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
            uint exitCode;
            GetExitCodeProcess(pi.hProcess, out exitCode);
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return (int)exitCode;
        }
    }
}
