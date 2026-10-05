using System;
using System.Runtime.InteropServices;

namespace MemReclaim
{
    /// <summary>完整性级别。</summary>
    internal enum IntegrityLevel
    {
        Unknown = 0,
        Untrusted,   // 0x0000
        Low,         // 0x1000
        Medium,      // 0x2000
        MediumPlus,  // 0x2100
        High,        // 0x3000
        System,      // 0x4000
        Protected    // 0x5000
    }

    /// <summary>
    /// 检测本进程运行的完整性级别。
    ///
    /// 【为什么必须有这个检测】
    /// 若程序文件带有 Low 完整性标签（沙箱目录、受管目录常见），Windows 会强制
    /// 该进程以 Low IL 运行，令牌中所有敏感特权被剥离。此时四项清理会全部失败，
    /// 而系统只返回笼统的 STATUS_PRIVILEGE_NOT_HELD，用户完全无从下手。
    ///
    /// 【与“移动 / 复制”的关系——这是最容易踩的坑】
    /// NTFS 规则：
    ///   · 同卷“移动”＝重命名，文件 ACL（含完整性标签）原样保留
    ///     → 移出沙箱目录的副本仍然带 Low 标签，照样失败
    ///   · “复制”＝创建新文件，继承目标目录属性
    ///     → 标签消失，可以正常工作
    /// 因此提示语必须明确要求用户“复制”，只说“换个目录”是不够的。
    /// </summary>
    internal static class IntegrityCheck
    {
        private const int TokenIntegrityLevel = 25;

        // SID 中完整性级别的 RID
        private const int SECURITY_MANDATORY_UNTRUSTED_RID = 0x0000;
        private const int SECURITY_MANDATORY_LOW_RID = 0x1000;
        private const int SECURITY_MANDATORY_MEDIUM_RID = 0x2000;
        private const int SECURITY_MANDATORY_MEDIUM_PLUS_RID = 0x2100;
        private const int SECURITY_MANDATORY_HIGH_RID = 0x3000;
        private const int SECURITY_MANDATORY_SYSTEM_RID = 0x4000;
        private const int SECURITY_MANDATORY_PROTECTED_RID = 0x5000;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass,
            IntPtr info, int infoLength, out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>读取当前进程令牌的完整性级别。失败返回 Unknown。</summary>
        public static IntegrityLevel GetCurrentLevel()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out token))
                return IntegrityLevel.Unknown;

            try
            {
                int length;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out length);
                if (length <= 0) return IntegrityLevel.Unknown;

                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out length))
                        return IntegrityLevel.Unknown;

                    // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; }
                    // SID_AND_ATTRIBUTES 首字段即 PSID
                    IntPtr sid = Marshal.ReadIntPtr(buffer);
                    if (sid == IntPtr.Zero) return IntegrityLevel.Unknown;

                    return RidToLevel(ReadLastRid(sid));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch
            {
                return IntegrityLevel.Unknown;
            }
            finally { CloseHandle(token); }
        }

        /// <summary>取 SID 的最后一个子授权值（完整性级别 SID 的 RID）。</summary>
        private static int ReadLastRid(IntPtr sid)
        {
            IntPtr countPtr = GetSidSubAuthorityCount(sid);
            if (countPtr == IntPtr.Zero) return -1;
            int count = Marshal.ReadByte(countPtr);
            if (count <= 0) return -1;

            IntPtr ridPtr = GetSidSubAuthority(sid, count - 1);
            if (ridPtr == IntPtr.Zero) return -1;
            return Marshal.ReadInt32(ridPtr);
        }

        private static IntegrityLevel RidToLevel(int rid)
        {
            switch (rid)
            {
                case SECURITY_MANDATORY_UNTRUSTED_RID: return IntegrityLevel.Untrusted;
                case SECURITY_MANDATORY_LOW_RID: return IntegrityLevel.Low;
                case SECURITY_MANDATORY_MEDIUM_RID: return IntegrityLevel.Medium;
                case SECURITY_MANDATORY_MEDIUM_PLUS_RID: return IntegrityLevel.MediumPlus;
                case SECURITY_MANDATORY_HIGH_RID: return IntegrityLevel.High;
                case SECURITY_MANDATORY_SYSTEM_RID: return IntegrityLevel.System;
                case SECURITY_MANDATORY_PROTECTED_RID: return IntegrityLevel.Protected;
                default: return IntegrityLevel.Unknown;
            }
        }

        public static string LevelName(IntegrityLevel level)
        {
            switch (level)
            {
                case IntegrityLevel.Untrusted: return "Untrusted（不受信任）";
                case IntegrityLevel.Low: return "Low（低）";
                case IntegrityLevel.Medium: return "Medium（中）";
                case IntegrityLevel.MediumPlus: return "Medium Plus（中加）";
                case IntegrityLevel.High: return "High（高）";
                case IntegrityLevel.System: return "System（系统）";
                case IntegrityLevel.Protected: return "Protected（受保护）";
                default: return "未知";
            }
        }

        /// <summary>该完整性级别是否足以启用内存清理所需特权。</summary>
        public static bool IsSufficient(IntegrityLevel level)
        {
            return level == IntegrityLevel.Medium
                || level == IntegrityLevel.MediumPlus
                || level == IntegrityLevel.High
                || level == IntegrityLevel.System;
        }

        /// <summary>
        /// 生成针对当前环境的处理建议。
        /// 只在确实受限时给出“复制而非移动”的指引，避免正常环境下误导用户。
        /// </summary>
        public static string BuildAdvice(IntegrityLevel level, bool isElevated, string exePath)
        {
            if (IsSufficient(level))
            {
                if (isElevated) return null;
                return "当前完整性级别正常，但未以管理员身份运行。\r\n" +
                       "请右键本程序 →「以管理员身份运行」。";
            }

            // 受限：明确区分“复制”与“移动”，这是关键
            return "本程序正以 " + LevelName(level) + " 完整性级别运行，令牌中的特权已被系统剥离，\r\n" +
                   "因此所有清理操作都会失败。\r\n" +
                   "\r\n" +
                   "原因：程序文件带有“低完整性标签”，通常来自沙箱目录或受管目录。\r\n" +
                   "当前位置：" + exePath + "\r\n" +
                   "\r\n" +
                   "解决办法（注意：必须用【复制】，不能用【移动】）：\r\n" +
                   "  · 复制：创建新文件，继承目标目录属性，标签消失 → 可用\r\n" +
                   "  · 移动：同卷移动等于重命名，标签会跟着文件走 → 仍然失败\r\n" +
                   "\r\n" +
                   "请右键本程序 →「复制」，粘贴到普通目录（如 C:\\MemReclaim\\），\r\n" +
                   "再以管理员身份运行。";
        }
    }
}
