
using System;

/// <summary>
/// 简单的控制台日志记录工具，提供彩色输出便于诊断。
/// </summary>
public static class Logger
{
    /// <summary>输出调试日志，通常在排查流程或数据问题时使用。</summary>
    public static void LogDebug(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[DBG] {message}");
        Console.ForegroundColor = originalColor;
    }

    /// <summary>输出普通提示日志，用于记录关键节点。</summary>
    public static void LogInfo(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[INF] {message}");
        Console.ForegroundColor = originalColor;
    }

    /// <summary>输出警告日志，强调可能需要注意的异常情况。</summary>
    public static void LogWarning(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WRN] {message}");
        Console.ForegroundColor = originalColor;
    }

    /// <summary>输出错误日志，强调严重问题。</summary>
    public static void LogError(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERR] {message}");
        Console.ForegroundColor = originalColor;
    }
}