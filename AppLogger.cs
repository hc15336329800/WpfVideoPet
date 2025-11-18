
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;

namespace WpfVideoPet
{

    /// <summary>
    /// 日志类
    /// </summary>
    internal static class AppLogger
    {
        private static readonly object SyncRoot = new();
        private static string? _logDirectory;
        private static string? _logFilePath;
        private const long MaxLogFileSizeBytes = 10 * 1024 * 1024; // 单个日志文件最大 10 MB
        private const int MaxLogRetentionCount = 100; // 仅保留最新 100 个日志文件，避免磁盘被填满

        public static void Initialize(string logDirectory)
        {
            if (string.IsNullOrWhiteSpace(logDirectory))
            {
                return;
            }

            lock (SyncRoot)
            {
                _logDirectory = logDirectory;
                Directory.CreateDirectory(_logDirectory);
                _logFilePath = BuildLogFilePath();
                CleanupOldLogs();
                WriteHeader();
            }
        }

        public static void Info(string message) => WriteLine("INFO", message);

        public static void Warn(string message) => WriteLine("WARN", message);

        public static void Error(string message) => WriteLine("ERROR", message);

        public static void Error(Exception exception, string message) => WriteLine("ERROR", $"{message} | {exception}");

        private static void WriteLine(string level, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"[{timestamp}] [{level}] {message}";

            Debug.WriteLine(line);

            lock (SyncRoot)
            {
                if (string.IsNullOrEmpty(_logDirectory))
                {
                    return;
                }

                EnsureWritableLogFile();


                if (_logFilePath == null)
                {
                    return;
                }

                try
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // Ignore file IO issues to avoid crashing the app.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>
        /// 在写日志前确保当前文件仍在允许的大小范围内，超出后切换到新的日志文件。
        /// 同时触发一次旧文件清理，避免日志持续累加导致磁盘膨胀。
        /// </summary>
        private static void EnsureWritableLogFile()
        {
            if (string.IsNullOrEmpty(_logDirectory))
            {
                return;
            }

            var needRotate = string.IsNullOrEmpty(_logFilePath);
            if (!needRotate && File.Exists(_logFilePath!))
            {
                try
                {
                    var length = new FileInfo(_logFilePath!).Length;
                    needRotate = length >= MaxLogFileSizeBytes;
                }
                catch (IOException)
                {
                    needRotate = true; // IO 异常时强制切换文件，避免写入失败反复触发。
                }
                catch (UnauthorizedAccessException)
                {
                    needRotate = true;
                }
            }

            if (!needRotate)
            {
                return;
            }

            _logFilePath = BuildLogFilePath();
            CleanupOldLogs();
            WriteHeader();
        }


        /// <summary>
        /// 控制日志总数量，超过上限时按时间顺序清理旧文件，避免长时间运行导致磁盘被塞满。
        /// </summary>
        private static void CleanupOldLogs()
        {
            if (string.IsNullOrEmpty(_logDirectory) || !Directory.Exists(_logDirectory))
            {
                return;
            }

            try
            {
                var logFiles = Directory
                    .EnumerateFiles(_logDirectory!, "log_*.txt", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .Where(info => info.Exists)
                    .OrderByDescending(info => info.CreationTimeUtc)
                    .ToList();

                if (logFiles.Count <= MaxLogRetentionCount)
                {
                    return;
                }

                foreach (var file in logFiles.Skip(MaxLogRetentionCount))
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (IOException)
                    {
                        // 忽略清理异常，确保不会阻断后续写日志流程。
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string? BuildLogFilePath()
        {
            if (string.IsNullOrEmpty(_logDirectory))
            {
                return null;
            }

            var fileName = $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            return Path.Combine(_logDirectory!, fileName);
        }

        private static void WriteHeader()
        {
            if (string.IsNullOrEmpty(_logFilePath))
            {
                return;
            }

            var header = new StringBuilder();
            header.AppendLine(new string('=', 80));
            header.AppendLine($"日志启动时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            header.AppendLine($"应用版本: {typeof(AppLogger).Assembly.GetName().Version}");
            header.AppendLine(new string('=', 80));

            try
            {
                File.AppendAllText(_logFilePath!, header.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}