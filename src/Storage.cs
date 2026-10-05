using System;
using System.IO;
using System.Text;

namespace MemReclaim
{
    /// <summary>
    /// 统一的文件位置管理。
    ///
    /// 为什么需要它：免 UAC 模式下程序可能装在 Program Files 这类只有管理员可写的目录，
    /// 而托盘进程是普通权限，往 exe 目录写配置会直接失败。因此所有可变文件
    /// （配置、日志、结果）一律放到 %APPDATA%\MemReclaim\。
    ///
    /// 兼容旧版本：旧版把配置放在 exe 目录，首次运行时自动迁移过来。
    /// </summary>
    internal static class Storage
    {
        private const string AppFolderName = "MemReclaim";

        private static string _dataDir;
        private static string _fallbackReason;

        /// <summary>可写数据目录 %APPDATA%\MemReclaim\，不存在时自动创建。</summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir == null)
                {
                    string appData = Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData);

                    // 极端情况下 ApplicationData 为空（服务账户、受限环境），
                    // 退回 exe 目录，至少保证程序能跑起来
                    if (string.IsNullOrEmpty(appData))
                    {
                        _dataDir = AppDomain.CurrentDomain.BaseDirectory;
                        _fallbackReason = "无法解析 %APPDATA% 路径";
                    }
                    else
                    {
                        string target = Path.Combine(appData, AppFolderName);
                        try
                        {
                            Directory.CreateDirectory(target);
                            _dataDir = target;
                        }
                        catch (Exception ex)
                        {
                            // 回退时记录原因：这种情况会带来实际后果
                            // （配置写到了 exe 目录，在 Program Files 下会失败），
                            // 静默回退会让问题变得难以排查。
                            _dataDir = AppDomain.CurrentDomain.BaseDirectory;
                            _fallbackReason = "无法创建 " + target + "：" + ex.Message;
                        }
                    }
                }
                return _dataDir;
            }
        }

        /// <summary>
        /// 数据目录是否发生了回退（即未能使用 %APPDATA%）。
        ///
        /// 回退本身不致命，但会削弱「程序装在只读目录也能用」这一保证：
        /// 配置与日志会落到 exe 目录，在 Program Files 下将无法写入。
        /// 因此界面与自检应当如实报告，而不是静默降级。
        /// </summary>
        public static bool IsFallback
        {
            get { string ignored = DataDir; return _fallbackReason != null; }
        }

        /// <summary>回退原因；未回退时为 null。</summary>
        public static string FallbackReason
        {
            get { string ignored = DataDir; return _fallbackReason; }
        }

        /// <summary>exe 所在目录。只读用途（例如迁移时查找旧配置）。</summary>
        public static string ExeDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(DataDir, "MemReclaim.config.xml"); }
        }

        public static string LogPath
        {
            get { return Path.Combine(DataDir, "MemReclaim.log"); }
        }

        public static string SelfTestPath
        {
            get { return Path.Combine(DataDir, "MemReclaim.selftest.txt"); }
        }

        /// <summary>委派执行的结果文件（由计划任务写入，托盘进程读取）。</summary>
        public static string DelegateResultPath
        {
            get { return Path.Combine(DataDir, "last-result.txt"); }
        }

        /// <summary>委派执行的请求文件（托盘写入请求号，任务读取后原样回写）。</summary>
        public static string DelegateRequestPath
        {
            get { return Path.Combine(DataDir, "last-request.txt"); }
        }

        /// <summary>
        /// 把旧版留在 exe 目录的配置迁移到 %APPDATA%。
        /// 仅在目标不存在且源存在时执行，返回是否发生了迁移。
        /// </summary>
        public static bool MigrateLegacyConfig()
        {
            try
            {
                string oldPath = Path.Combine(ExeDir, "MemReclaim.config.xml");
                string newPath = ConfigPath;

                if (File.Exists(newPath)) return false;   // 已有新配置，不覆盖
                if (!File.Exists(oldPath)) return false;  // 没有旧配置
                if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return false;

                File.Copy(oldPath, newPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>追加一行日志，失败时静默（日志不应影响主流程）。</summary>
        public static void AppendLog(string text)
        {
            try
            {
                File.AppendAllText(LogPath, text, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>安全写文件：先写临时文件再替换，避免读取方读到半截内容。</summary>
        public static bool WriteAtomic(string path, string content)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
