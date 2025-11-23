using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace WpfVideoPet;

/// <summary>
/// 提供开机自启的注册表配置能力。
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WpfVideoPet";

    /// <summary>
    /// 获取当前进程可执行文件的完整路径，若无法获取则返回空字符串。
    /// </summary>
    private static string GetExecutablePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }

    /// <summary>
    /// 将当前可执行文件写入启动项，使应用在开机后自动运行。
    /// </summary>
    public static void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null)
            {
                AppLogger.Warn("无法创建或打开注册表自启项，开机自启设置未生效。");
                return;
            }

            var exe = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(exe))
            {
                AppLogger.Error("无法获取可执行文件路径，已放弃写入开机自启配置。");
                return;
            }

            var desiredValue = $"\"{exe}\"";
            key.SetValue(AppName, desiredValue);

            var actualValue = key.GetValue(AppName) as string;
            if (string.Equals(actualValue, desiredValue, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Info($"已写入开机自启配置，指向可执行文件: {exe}");
            }
            else
            {
                AppLogger.Warn($"开机自启值写入后验证失败，当前值: {actualValue ?? "<null>"}，预期: {desiredValue}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "设置开机自启时出现异常");
        }
    }

    /// <summary>
    /// 删除开机自启配置，阻止应用随系统启动。
    /// </summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null)
            {
                AppLogger.Warn("未找到自启注册表项，跳过禁用操作。");
                return;
            }

            key.DeleteValue(AppName, false);
            AppLogger.Info("已删除开机自启配置。");
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "禁用开机自启时出现异常");
        }
    }

    /// <summary>
    /// 判断当前用户注册表中是否存在开机自启配置。
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var value = key?.GetValue(AppName) as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                AppLogger.Info($"检测到自启已开启，当前注册表值: {value}");
                return true;
            }

            AppLogger.Info("未检测到自启配置，将视为未启用。");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "读取开机自启状态失败，默认返回未启用");
            return false;
        }
    }
}