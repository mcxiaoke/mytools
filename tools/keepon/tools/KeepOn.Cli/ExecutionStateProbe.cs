using System;
using System.Runtime.InteropServices;

namespace KeepOn.Cli
{
    /// <summary>
    /// 通过 CallNtPowerInformation(SystemExecutionState=16) 读取当前线程的电源请求位，
    /// 这是 SetThreadExecutionState 的对应读取接口，无需管理员权限。
    /// 用于验证"保持唤醒请求确实已提交给系统"。
    /// </summary>
    internal static class ExecutionStateProbe
    {
        private const int SystemExecutionState = 16;

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern int CallNtPowerInformation(
            int informationLevel,
            IntPtr inputBuffer,
            int inputBufferSize,
            out uint outputBuffer,
            int outputBufferSize);

        /// <summary>读取当前进程的电源请求位；失败返回 null</summary>
        public static uint? Read()
        {
            try
            {
                int status = CallNtPowerInformation(
                    SystemExecutionState,
                    IntPtr.Zero,
                    0,
                    out uint state,
                    sizeof(uint));

                return status == 0 ? state : (uint?)null;
            }
            catch
            {
                return null;
            }
        }

        public static string Describe(uint state)
        {
            const uint systemRequired = 0x00000001;
            const uint displayRequired = 0x00000002;
            const uint continuous = 0x80000000;

            var parts = new System.Collections.Generic.List<string>();
            if ((state & continuous) != 0) parts.Add("ES_CONTINUOUS");
            if ((state & systemRequired) != 0) parts.Add("ES_SYSTEM_REQUIRED");
            if ((state & displayRequired) != 0) parts.Add("ES_DISPLAY_REQUIRED");

            return parts.Count == 0
                ? "无（0x" + state.ToString("X8") + "）"
                : string.Join(" | ", parts) + "  (0x" + state.ToString("X8") + ")";
        }
    }
}
