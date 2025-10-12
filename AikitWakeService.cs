using Microsoft.VisualBasic;
using NAudio.Wave;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace WpfVideoPet
{
    /// <summary>
    /// 讯飞语音唤醒
    /// 将 AikitNet 控制台程序的唤醒逻辑嵌入到 WPF 应用中，常驻监听麦克风。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class AikitWakeService : IDisposable
    {
        private const string AbilityId = "e867a88f2";
        private const string KeywordKey = "key_word";
        private const string AppId = "50334b7e";
        private const string ApiKey = "0fb097671abc68e6383f049571ac7eb2";
        private const string ApiSecret = "MjdjYzk3OGE1ZWQ3NTAxYTliZmUzNmYz";

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void AD_OnOutput(IntPtr abilityId, IntPtr key, IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void AD_OnEvent(IntPtr abilityId, int eventType, IntPtr payload);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void AD_OnError(IntPtr abilityId, int errCode, IntPtr desc);

        [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int AD_Init(string appId, string apiKey, string apiSecret, string workDir, string resDir);

        [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AD_UnInit();

        [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AD_RegisterWakeCallbacks(AD_OnOutput output, AD_OnEvent evt, AD_OnError error);

        [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int AD_LoadKeywordFile(string abilityId, string key, string keywordTxtPath);

        [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int AD_SpecifyKeywordSet(string abilityId, string key, int index);

        [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int AD_StartWakeSession(string abilityId, string thresholdParam);

        [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AD_WritePcm16(short[] pcm, int samples, int lastFlag);

        [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AD_EndWakeSession();

        private readonly object _syncRoot = new();
        private WaveInEvent? _mic;
        private bool _disposed;
        private bool _initialized;
        private bool _sessionStarted;
        private string? _originalWorkingDirectory;
        private string? _workDir;
        private string? _resDir;
        private string? _keywordPath;

        private AD_OnOutput? _outputCallback;
        private AD_OnEvent? _eventCallback;
        private AD_OnError? _errorCallback;
        private EventHandler<WaveInEventArgs>? _dataAvailableHandler;
        private EventHandler<StoppedEventArgs>? _recordingStoppedHandler;

        /// <summary>
        /// 唤醒后触发。
        /// </summary>
        public event EventHandler? WakeTriggered;

        /// <summary>
        /// 将控制台程序中的初始化逻辑迁移到 WPF 常驻服务。
        /// </summary>
        /// <returns>启动成功返回 true。</returns>
        public bool Start()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                if (_initialized)
                {
                    return true;
                }

                try
                {
                    InitializeEngine();
                    StartMicrophone();
                    _initialized = true;
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"Aikit 唤醒服务启动失败：{ex.Message}");
                    StopInternal();
                    return false;
                }
            }
        }

        /// <summary>
        /// 停止唤醒引擎并释放资源。
        /// </summary>
        public void Stop()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                StopInternal();
                _initialized = false;
            }
        }

        private void InitializeEngine()
        {
            var baseDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = Directory.GetCurrentDirectory();
            }

            _originalWorkingDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(baseDirectory);

            _workDir = ".";
            _resDir = Path.Combine(".", "resource");

            var keywordFullPath = Path.Combine(Directory.GetCurrentDirectory(), "keyword.txt");
            _keywordPath = IsPureAscii(keywordFullPath)
                ? keywordFullPath
                : Path.Combine(".", "keyword.txt");

            AppLogger.Info($"初始化 Aikit 唤醒服务，工作目录: {Path.GetFullPath(_workDir)}, 资源目录: {Path.GetFullPath(_resDir)}");

            var initResult = AD_Init(AppId, ApiKey, ApiSecret, _workDir, _resDir);
            if (initResult != 0)
            {
                var message = TranslateError(initResult);
                throw new InvalidOperationException($"AD_Init 失败：{initResult} ({message})");
            }

            _outputCallback = OnWakeOutput;
            _eventCallback = (abPtr, evt, payload) =>
            {
                var payloadText = PtrToUtf8(payload);
                AppLogger.Info($"[Aikit 事件] {evt} {payloadText}");
            };
            _errorCallback = (abPtr, err, desc) =>
            {
                var descText = PtrToUtf8(desc);
                AppLogger.Error($"[Aikit 错误] {err} {descText}");
            };

            var registerResult = AD_RegisterWakeCallbacks(_outputCallback, _eventCallback, _errorCallback);
            if (registerResult != 0)
            {
                throw new InvalidOperationException($"注册唤醒回调失败：{registerResult} ({TranslateError(registerResult)})");
            }

            var loadResult = AD_LoadKeywordFile(AbilityId, KeywordKey, _keywordPath);
            if (loadResult != 0)
            {
                throw new InvalidOperationException($"加载唤醒词失败：{loadResult} ({TranslateError(loadResult)})");
            }

            var specifyResult = AD_SpecifyKeywordSet(AbilityId, KeywordKey, 0);
            if (specifyResult != 0)
            {
                throw new InvalidOperationException($"激活唤醒词集合失败：{specifyResult} ({TranslateError(specifyResult)})");
            }

            var startResult = AD_StartWakeSession(AbilityId, null);
            if (startResult != 0)
            {
                throw new InvalidOperationException($"开启唤醒会话失败：{startResult} ({TranslateError(startResult)})");
            }

            _sessionStarted = true;
        }

        private void StartMicrophone()
        {
            _mic = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 20
            };

            _dataAvailableHandler = OnMicDataAvailable;
            _recordingStoppedHandler = OnMicRecordingStopped;

            _mic.DataAvailable += _dataAvailableHandler;
            _mic.RecordingStopped += _recordingStoppedHandler;

            _mic.StartRecording();
            AppLogger.Info("Aikit 唤醒服务已开始监听麦克风。");
        }

        private void OnWakeOutput(IntPtr abilityPtr, IntPtr keyPtr, IntPtr valuePtr)
        {
            var key = PtrToUtf8(keyPtr);
            var value = PtrToUtf8(valuePtr);

            if (key == "func_wake_up" || key == "rlt")
            {
                AppLogger.Info($"检测到唤醒词：{value}");
                PlayDing();
                WakeTriggered?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                AppLogger.Info($"[Aikit 输出] {key}: {value}");
            }
        }

        private void PlayDing()
        {
            try
            {
                // 默认先尝试绝对路径，可在部署时替换。
                const string ringPathAbs = @"C:\\chime.wav";
                string ringPath = File.Exists(ringPathAbs)
                    ? ringPathAbs
                    : Path.Combine(Directory.GetCurrentDirectory(), "chime.wav");

                if (!File.Exists(ringPath))
                {
                    AppLogger.Warn($"铃声文件不存在：{ringPath}");
                    return;
                }

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        using var reader = new AudioFileReader(ringPath);
                        using var wo = new WaveOutEvent();
                        wo.Init(reader);
                        wo.Play();
                        while (wo.PlaybackState == PlaybackState.Playing)
                        {
                            Thread.Sleep(20);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"NAudio 播放铃声失败：{ex.Message}");
                        try
                        {
                            Console.Beep(880, 180);
                            Console.Beep(1320, 220);
                        }
                        catch
                        {
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"播放唤醒提示音失败：{ex.Message}");
            }
        }

        private void OnMicDataAvailable(object? sender, WaveInEventArgs args)
        {
            try
            {
                var samples = args.BytesRecorded / 2;
                if (samples <= 0)
                {
                    return;
                }

                var pcm = new short[samples];
                Buffer.BlockCopy(args.Buffer, 0, pcm, 0, args.BytesRecorded);
                AD_WritePcm16(pcm, pcm.Length, 0);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"写入唤醒引擎 PCM 数据失败：{ex.Message}");
            }
        }

        private void OnMicRecordingStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception != null)
            {
                AppLogger.Error(args.Exception, "麦克风录音意外停止");
            }
        }

        private static string PtrToUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
            {
                return string.Empty;
            }

            var length = 0;
            while (Marshal.ReadByte(ptr, length) != 0)
            {
                length++;
            }

            var bytes = new byte[length];
            Marshal.Copy(ptr, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static bool IsPureAscii(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            foreach (var c in value)
            {
                if (c > 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        private static string TranslateError(int code)
        {
            return code switch
            {
                0 => "成功",
                18000 => "本地 license 文件不存在",
                18005 => "授权已过期",
                18007 => "授权信息与 APPID/APIKey 不匹配",
                18105 => "资源加载失败，workDir 内未找到对应资源",
                18400 => "工作目录无写权限",
                18705 => "云端应用 apiKey 或 apiSecret 校验失败",
                18707 => "云端授权已过期",
                18708 => "云端授权数量已满，请在控制台释放设备或联系讯飞扩容",
                100007 => "唤醒引擎启动失败",
                _ => "未知错误，详见 AikitDll/include/aikit_err.h",
            };
        }

        private void StopInternal()
        {
            try
            {
                if (_mic != null)
                {
                    if (_dataAvailableHandler != null)
                    {
                        _mic.DataAvailable -= _dataAvailableHandler;
                        _dataAvailableHandler = null;
                    }

                    if (_recordingStoppedHandler != null)
                    {
                        _mic.RecordingStopped -= _recordingStoppedHandler;
                        _recordingStoppedHandler = null;
                    }

                    _mic.StopRecording();
                    _mic.Dispose();
                    _mic = null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"停止麦克风失败：{ex.Message}");
            }

            try
            {
                if (_sessionStarted)
                {
                    AD_EndWakeSession();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"结束唤醒会话失败：{ex.Message}");
            }
            finally
            {
                _sessionStarted = false;
            }

            try
            {
                AD_UnInit();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"释放 Aikit 引擎失败：{ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(_originalWorkingDirectory))
            {
                try
                {
                    Directory.SetCurrentDirectory(_originalWorkingDirectory);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"恢复工作目录失败：{ex.Message}");
                }
                finally
                {
                    _originalWorkingDirectory = null;
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AikitWakeService));
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}