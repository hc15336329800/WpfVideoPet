
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace WpfVideoPet
{
    internal static class AppLogger
    {
        private static readonly object SyncRoot = new();
        private static string? _logDirectory;
        private static string? _logFilePath;
        private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB

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

                if (string.IsNullOrEmpty(_logFilePath) || (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length > MaxLogFileSizeBytes))
                {
                    _logFilePath = BuildLogFilePath();
                }

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