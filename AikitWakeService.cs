using Microsoft.VisualBasic;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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

        private static readonly string[] KnownSdkDirectories =
        {  @"D:\C#_code\WpfVideoPet\DLL"        };

        private readonly string? _externalSdkDirectory;
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
        /// 初始化服务，可传入包含完整 SDK 的目录。
        /// </summary>
        /// <param name="sdkDirectory">包含原生 DLL、keyword.txt 以及 resource 目录的 SDK 根路径。</param>
        public AikitWakeService(string? sdkDirectory = null)
        {
            if (!string.IsNullOrWhiteSpace(sdkDirectory))
            {
                try
                {
                    _externalSdkDirectory = Path.GetFullPath(sdkDirectory!);
                }
                catch
                {
                    _externalSdkDirectory = sdkDirectory;
                }
            }
        }


        /// <summary>
        /// 唤醒后触发。
        /// </summary>
        public event EventHandler? WakeTriggered;

        /// <summary>
        /// 识别到具体唤醒词时触发，便于界面层展示提示或执行差异化业务逻辑。
        /// </summary>
        public event EventHandler<WakeKeywordEventArgs>? WakeKeywordRecognized;

        /// <summary>
        /// 当业务需要启动语音识别播报时触发。
        /// </summary>
        public event EventHandler? SpeechRecognitionRequested;

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
                    AppLogger.Info("开始启动 Aikit 唤醒服务……");
                    InitializeEngine();
                    StartMicrophone();
                    _initialized = true;
                    AppLogger.Info("Aikit 唤醒服务启动成功。");
                    return true;
                }
                catch (DllNotFoundException ex)
                {
                    AppLogger.Error(ex, "Aikit 唤醒服务启动失败：未能找到原生依赖库，请确认 AikitDll 相关文件已复制到程序目录。");
                    StopInternal();
                    return false;
                }
                catch (BadImageFormatException ex)
                {
                    AppLogger.Error(ex, "Aikit 唤醒服务启动失败：检测到架构不匹配，请确保应用以 x64 模式运行并提供 64 位原生库。");
                    StopInternal();
                    return false;
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
                AppLogger.Info("正在停止 Aikit 唤醒服务。");
                StopInternal();
                _initialized = false;
            }
        }
      
        
        /// <summary>
        /// 完成原生唤醒引擎的初始化，包括依赖复制、回调注册与会话启动。
        /// </summary>
        private void InitializeEngine()
        {
            var baseDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = Directory.GetCurrentDirectory();
            }
            
            AppLogger.Info($"Aikit 唤醒服务原生目录: {baseDirectory}");

            var keywordFullPath = EnsureNativeDependencies(baseDirectory);

            _originalWorkingDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(baseDirectory);

            _workDir = ".";
            _resDir = Path.Combine(".", "resource");

             _keywordPath = IsPureAscii(keywordFullPath)
                ? keywordFullPath
                : Path.Combine(".", "keyword.txt");

            AppLogger.Info($"初始化 Aikit 唤醒服务，工作目录: {Path.GetFullPath(_workDir)}, 资源目录: {Path.GetFullPath(_resDir)}, 唤醒词文件: {Path.GetFullPath(_keywordPath)}");
          
            var initResult = AD_Init(AppId, ApiKey, ApiSecret, _workDir, _resDir);
            AppLogger.Info($"AD_Init 返回代码: {initResult}");
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
            int registerResult = AD_RegisterWakeCallbacks(_outputCallback, _eventCallback, _errorCallback);
            AppLogger.Info($"AD_RegisterWakeCallbacks 返回代码: {registerResult}");
            if (registerResult != 0)
            {
                throw new InvalidOperationException($"注册唤醒回调失败：{registerResult} ({TranslateError(registerResult)})");
            }

            var loadResult = AD_LoadKeywordFile(AbilityId, KeywordKey, _keywordPath);
            AppLogger.Info($"AD_LoadKeywordFile 返回代码: {loadResult}");
            if (loadResult != 0)
            {
                throw new InvalidOperationException($"加载唤醒词失败：{loadResult} ({TranslateError(loadResult)})");
            }

            var specifyResult = AD_SpecifyKeywordSet(AbilityId, KeywordKey, 0);
            AppLogger.Info($"AD_SpecifyKeywordSet 返回代码: {specifyResult}");
            if (specifyResult != 0)
            {
                throw new InvalidOperationException($"激活唤醒词集合失败：{specifyResult} ({TranslateError(specifyResult)})");
            }

            var startResult = AD_StartWakeSession(AbilityId, null);
            AppLogger.Info($"AD_StartWakeSession 返回代码: {startResult}");
            if (startResult != 0)
            {
                throw new InvalidOperationException($"开启唤醒会话失败：{startResult} ({TranslateError(startResult)})");
            }

            _sessionStarted = true;
            AppLogger.Info("Aikit 唤醒引擎初始化完成，等待麦克风数据。");
        }

        /// <summary>
        /// 初始化麦克风采集并订阅录音事件，将音频流喂入唤醒引擎。
        /// </summary>
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
            AppLogger.Info("Aikit 唤醒服务已开始监听麦克风 (16kHz/16bit/Mono, 20ms 缓冲)。");
        }


        /// <summary>
        /// 处理唤醒引擎输出，根据关键字触发唤醒事件或记录日志。
        /// </summary>
        /// <param name="abilityPtr">能力标识指针。</param>
        /// <param name="keyPtr">输出键名指针。</param>
        /// <param name="valuePtr">输出值指针。</param>
        private void OnWakeOutput(IntPtr abilityPtr, IntPtr keyPtr, IntPtr valuePtr)
        {
            var key = PtrToUtf8(keyPtr);
            var value = PtrToUtf8(valuePtr);

            if (key == "func_wake_up" || key == "rlt")
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    AppLogger.Info($"唤醒回调原始数据: {value}");
                }

                // 标记是否识别到唤醒词以及是否需要触发视频通话，避免误触发。
                var keywordRecognized = false;
                var shouldTriggerVideoCall = false; // 标记是否需要弹出视频窗口，仅当识别到“打开视频”时才允许触发。

                if (TryGetKeyword(value, out var keyword))
                {
                    keywordRecognized = true;
                    var recognizedKeyword = keyword!; // 唤醒回调解析出的标准化唤醒词。
                    AppLogger.Info($"解析到唤醒词：{recognizedKeyword}");

                    string? notificationMessage = null; // 用于传递给界面层的业务提示文案。

                    switch (recognizedKeyword)
                    {
                        case "小白小白":
                            AppLogger.Info("触发“小白小白”业务逻辑：例如启动语音助手或开启常用功能。");
                            notificationMessage = "已唤醒“小白小白”，正在为您准备常用功能。";
                            break;
                        case "小黄小黄":
                            AppLogger.Info("触发“小黄小黄”业务逻辑：例如播报天气或执行定制命令。");
                            notificationMessage = "已唤醒“小黄小黄”，即将为您播报定制内容。";

                            // 播放默认提示音反馈用户操作，再延迟一秒启动语音转写。
                            AppLogger.Info("播放默认唤醒提示音，告知用户已成功唤醒。");
                            PlayDing();

                            AppLogger.Info("已播放提示音，将在 1 秒后触发语音识别流程。");
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    // 设置延迟 0.5 秒启动语音转写
                                    await Task.Delay(TimeSpan.FromSeconds(0.5)).ConfigureAwait(false);
                                    AppLogger.Info("提示音播放延迟结束，开始通知主线程执行语音识别。");
                                    SpeechRecognitionRequested?.Invoke(this, EventArgs.Empty);
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.Error(ex, $"调度语音识别时发生异常: {ex.Message}");
                                }
                            });

                            AppLogger.Info("“小黄小黄”指令仅执行语音识别流程，将显式阻断视频通话逻辑。");
                            WakeKeywordRecognized?.Invoke(this, new WakeKeywordEventArgs(recognizedKeyword, notificationMessage));
                            AppLogger.Info("“小黄小黄”业务处理完毕，本次唤醒流程结束。");
                            return;
                        case "打开视频":
                            AppLogger.Info("触发“打开视频”业务逻辑：准备唤起视频通话窗口。");
                            notificationMessage = "已识别指令“打开视频”，正在为您建立视频通话。";
                            shouldTriggerVideoCall = true;
                            break;
                        default:
                            AppLogger.Info($"唤醒词 {recognizedKeyword} 未绑定特定逻辑，执行默认处理。");
                            break;
                    }

                    WakeKeywordRecognized?.Invoke(this, new WakeKeywordEventArgs(recognizedKeyword, notificationMessage));
                }
                else
                {
                    AppLogger.Warn($"唤醒回调未能解析 keyword 字段：{value}");
                    AppLogger.Info("请检查回调 JSON 是否包含 keyword 字段，确认 keyword.txt 为 UTF-8 且每行以分号结尾，并确保 AD_LoadKeywordFile/AD_SpecifyKeywordSet 参数一致。");
                }

                if (keywordRecognized)
                {
                    if (shouldTriggerVideoCall)
                    {
                        AppLogger.Info("当前唤醒指令需要打开视频通话，将播放提示音并弹出视频窗口。");
                        PlayDing();
                        WakeTriggered?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        AppLogger.Info("当前唤醒词不匹配“打开视频”指令，本次唤醒将不会弹出视频窗口。");
                    }
                }
                else
                {
                    AppLogger.Warn("未识别出唤醒词，本次唤醒将不会触发视频窗口。");
                }
            }
            else
            {
                AppLogger.Info($"[Aikit 输出] {key}: {value}");
            }
        }

        /// <summary>
        /// 从唤醒回调 JSON 串中提取 keyword 字段，返回是否解析成功。
        /// </summary>
        /// <param name="json">回调给定的 JSON 文本。</param>
        /// <param name="keyword">成功时输出的唤醒词。</param>
        private bool TryGetKeyword(string? json, out string? keyword)
        {
            keyword = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            if (TryExtractKeyword(json, out keyword, out var warning, out var parseError))
            {
                keyword = NormalizeKeyword(keyword);
                return !string.IsNullOrWhiteSpace(keyword);
            }

            var fixedJson = FixKeywordInJson(json);
            if (!string.Equals(fixedJson, json, StringComparison.Ordinal))
            {
                AppLogger.Info("检测到唤醒回调疑似 UTF-8 乱码，尝试自动纠正 keyword 字段后重新解析。");
                if (TryExtractKeyword(fixedJson, out keyword, out var fixedWarning, out var fixedParseError))
                {
                    keyword = NormalizeKeyword(keyword);
                    return !string.IsNullOrWhiteSpace(keyword);
                }

                if (!string.IsNullOrEmpty(fixedParseError))
                {
                    AppLogger.Warn($"{fixedParseError}原始数据：{fixedJson}");
                }
                else if (!string.IsNullOrEmpty(fixedWarning))
                {
                    AppLogger.Warn($"{fixedWarning}原始数据：{fixedJson}");
                }
            }

            if (!string.IsNullOrEmpty(parseError))
            {
                AppLogger.Warn($"{parseError}原始数据：{json}");
            }
            else if (!string.IsNullOrEmpty(warning))
            {
                AppLogger.Warn($"{warning}原始数据：{json}");
            }

            AppLogger.Info("若多次解析失败，可检查 keyword.txt 格式、回调内容编码以及是否被其他模块修改。");
            return false;
        }

        private static bool TryExtractKeyword(string json, out string? keyword, out string? warning, out string? parseError)
        {
            keyword = null;
            warning = null;
            parseError = null;

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("keyword", out var keywordElement) &&
                        keywordElement.ValueKind == JsonValueKind.String)
                    {
                        keyword = keywordElement.GetString();
                        return true;
                    }

                    if (document.RootElement.TryGetProperty("rlt", out var resultElement))
                    {
                        if (resultElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in resultElement.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.Object &&
                                    item.TryGetProperty("keyword", out var itemKeyword) &&
                                    itemKeyword.ValueKind == JsonValueKind.String)
                                {
                                    keyword = itemKeyword.GetString();
                                    return true;
                                }
                            }

                            warning = "唤醒回调 JSON 的 rlt 数组中未找到 keyword 字段。";
                            return false;
                        }

                        warning = "唤醒回调 JSON 的 rlt 字段类型异常，期望数组。";
                        return false;
                    }
                }

                warning = "唤醒回调 JSON 中未找到 keyword 字段。";
                return false;
            }
            catch (JsonException ex)
            {
                parseError = $"唤醒回调 JSON 解析失败：{ex.Message}。";
                return false;
            }
        }
        private static string? NormalizeKeyword(string? keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return null;
            }

            var trimmed = keyword.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            var fixedKeyword = FixUtf8Mojibake(trimmed);
            if (!string.Equals(trimmed, fixedKeyword, StringComparison.Ordinal))
            {
                AppLogger.Info($"唤醒词编码已自动纠正：{trimmed} -> {fixedKeyword}");
            }

            return string.IsNullOrWhiteSpace(fixedKeyword) ? null : fixedKeyword;
        }

        private static string FixUtf8Mojibake(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            try
            {
                if (Encoding.Default.CodePage == Encoding.UTF8.CodePage)
                {
                    return value;
                }

                var bytes = Encoding.Default.GetBytes(value);
                var decoded = Encoding.UTF8.GetString(bytes);
                return decoded.Contains('\uFFFD') ? value : decoded;
            }
            catch
            {
                return value;
            }
        }

        private static string FixKeywordInJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return json;
            }

            return Regex.Replace(
                json,
                "(\\\"keyword\\\"\\s*:\\s*\\\")([^\\\"]*)(\\\")",
                match =>
                {
                    var original = match.Groups[2].Value;
                    var fixedKeyword = FixUtf8Mojibake(original);
                    return match.Groups[1].Value + fixedKeyword + match.Groups[3].Value;
                });
        }

         

        /// <summary>
        /// 播放提示音提示唤醒成功，若提示音文件不存在则尝试使用蜂鸣器。
        /// </summary>
        private void PlayDing()
        {
            try
            {
                // 默认先尝试绝对路径，可在部署时替换。
                const string ringPathAbs = @"C:\\chime.wav";
                string ringPath = File.Exists(ringPathAbs)
                    ? ringPathAbs
                    : Path.Combine(Directory.GetCurrentDirectory(), "chime.wav");

                AppLogger.Info($"唤醒提示音路径: {ringPath}");

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

        /// <summary>
        /// 麦克风缓冲区回调，将 PCM 数据写入唤醒引擎。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="args">录音缓冲区数据。</param>
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

        /// <summary>
        /// 麦克风录音结束回调，记录异常终止信息。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="args">停止事件参数。</param>
        private void OnMicRecordingStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception != null)
            {
                AppLogger.Error(args.Exception, "麦克风录音意外停止");
            }
        }

        /// <summary>
        /// 将指向 UTF-8 文本的非托管指针转换为托管字符串。
        /// </summary>
        /// <param name="ptr">UTF-8 文本指针。</param>
        /// <returns>转换后的托管字符串。</returns>
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

        /// <summary>
        /// 判断字符串是否仅包含 ASCII 字符。
        /// </summary>
        /// <param name="value">待检测字符串。</param>
        /// <returns>全部为 ASCII 时返回 true。</returns>
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

        /// <summary>
        /// 将原生错误码转换为易读的中文描述。
        /// </summary>
        /// <param name="code">原生错误码。</param>
        /// <returns>对应的中文描述。</returns>
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

        /// <summary>
        /// 停止麦克风、结束会话并释放原生资源。
        /// </summary>
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
                    AppLogger.Info("已结束 Aikit 唤醒会话。");
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
                AppLogger.Info("Aikit 唤醒引擎资源已释放。");
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
        /// <summary>
        /// 确保原生依赖完整存在，必要时尝试从候选目录复制。
        /// </summary>
        /// <param name="baseDirectory">应用根目录。</param>
        /// <returns>唤醒词文件的绝对路径。</returns>
        private string EnsureNativeDependencies(string baseDirectory)
        {
            if (TryVerifyNativeDependencies(baseDirectory, out _, out var baseMissing))
            {
                return VerifyNativeDependencies(baseDirectory);
            }

            AppLogger.Warn($"检测到程序目录缺少 Aikit 依赖: {string.Join("; ", baseMissing)}，尝试从外部目录复制……");

            foreach (var candidate in EnumerateCandidateSdkDirectories(baseDirectory))
            {
                if (string.Equals(candidate, baseDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AppLogger.Info($"尝试从候选目录复制 Aikit 依赖: {candidate}");

                if (!Directory.Exists(candidate))
                {
                    AppLogger.Info($"候选 Aikit SDK 目录不存在: {candidate}");
                    continue;
                }

                if (!TryVerifyNativeDependencies(candidate, out _, out var missing))
                {
                    AppLogger.Warn($"候选目录缺少必要文件: {candidate}; 缺失: {string.Join("; ", missing)}");
                    continue;
                }

                try
                {
                    CopySdkPayload(candidate, baseDirectory);
                    AppLogger.Info($"已从 {candidate} 复制 Aikit 依赖到 {baseDirectory}");
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"复制 Aikit 依赖失败：{candidate}");
                }
            }

            return VerifyNativeDependencies(baseDirectory);
        }

        /// <summary>
        /// 检查指定目录下的原生依赖是否齐全。
        /// </summary>
        /// <param name="baseDirectory">待检测目录。</param>
        /// <param name="keywordFullPath">检测到的唤醒词文件路径。</param>
        /// <param name="missing">缺失项集合。</param>
        /// <returns>依赖完整时返回 true。</returns>
        private static bool TryVerifyNativeDependencies(string baseDirectory, out string keywordFullPath, out List<string> missing)
        {
            missing = new List<string>();

            foreach (var (name, path) in GetNativeDependencyMap(baseDirectory))
            {
                if (!File.Exists(path))
                {
                    missing.Add($"{name} -> {path}");
                }
            }

            var resourceDirectory = Path.Combine(baseDirectory, "resource");
            if (!Directory.Exists(resourceDirectory))
            {
                missing.Add($"resource 目录 -> {resourceDirectory}");
            }

            keywordFullPath = Path.Combine(baseDirectory, "keyword.txt");
            if (!File.Exists(keywordFullPath))
            {
                missing.Add($"keyword.txt -> {keywordFullPath}");
            }

            return missing.Count == 0;
        }

        /// <summary>
        /// 强制校验原生依赖，缺失时抛出异常。
        /// </summary>
        /// <param name="baseDirectory">依赖所在目录。</param>
        /// <returns>唤醒词文件的绝对路径。</returns>
        private static string VerifyNativeDependencies(string baseDirectory)
        {
            if (!TryVerifyNativeDependencies(baseDirectory, out var keywordFullPath, out var missing))
            {
                throw new FileNotFoundException("Aikit 原生依赖缺失: " + string.Join("; ", missing));
            }

            foreach (var (name, path) in GetNativeDependencyMap(baseDirectory))
            {
                AppLogger.Info($"检测到依赖文件: {name} ({path})");
            }

            var resourceDirectory = Path.Combine(baseDirectory, "resource");
            AppLogger.Info($"检测到资源目录: {resourceDirectory}");
            AppLogger.Info($"检测到唤醒词文件: {keywordFullPath}");

            return keywordFullPath;
        }

        /// <summary>
        /// 获取唤醒引擎运行所需的关键文件映射。
        /// </summary>
        /// <param name="baseDirectory">依赖所在目录。</param>
        /// <returns>文件名称与完整路径的集合。</returns>
        private static IEnumerable<(string Name, string Path)> GetNativeDependencyMap(string baseDirectory)
        {
            yield return ("AikitDll.dll", Path.Combine(baseDirectory, "AikitDll.dll"));
            yield return ("AEE_lib.dll", Path.Combine(baseDirectory, "AEE_lib.dll"));
            yield return ("ef7d69542_v1014_aee.dll", Path.Combine(baseDirectory, "ef7d69542_v1014_aee.dll"));
        }

        /// <summary>
        /// 遍历可能的 SDK 目录，去重后返回可用路径。
        /// </summary>
        /// <param name="baseDirectory">应用根目录。</param>
        /// <returns>候选 SDK 目录集合。</returns>
        private IEnumerable<string> EnumerateCandidateSdkDirectories(string baseDirectory)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in GetCandidateSdkDirectories(baseDirectory))
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string normalized;
                try
                {
                    normalized = Path.GetFullPath(raw);
                }
                catch
                {
                    normalized = raw;
                }

                if (seen.Add(normalized))
                {
                    yield return normalized;
                }
            }
        }

        /// <summary>
        /// 枚举候选 SDK 目录，包含外部传参、环境变量与默认路径。
        /// </summary>
        /// <param name="baseDirectory">应用根目录。</param>
        /// <returns>候选目录序列。</returns>
        private IEnumerable<string?> GetCandidateSdkDirectories(string baseDirectory)
        {
            if (!string.IsNullOrWhiteSpace(_externalSdkDirectory))
            {
                yield return _externalSdkDirectory;
            }

            var envPath = Environment.GetEnvironmentVariable("AIKIT_SDK_DIR");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                yield return envPath;
            }

            yield return Path.Combine(baseDirectory, "AikitNet", "DLL");
            foreach (var parentDllDirectory in EnumerateParentDllDirectories(baseDirectory))
            {
                yield return parentDllDirectory;
            }

            foreach (var known in KnownSdkDirectories)
            {
                yield return known;
            }
        }


        /// <summary>
        /// 枚举以应用目录为起点逐级向上的 DLL 目录候选。
        /// </summary>
        /// <param name="baseDirectory">当前应用根目录。</param>
        /// <returns>沿父目录向上可能存在 DLL 子目录的集合。</returns>
        private static IEnumerable<string?> EnumerateParentDllDirectories(string baseDirectory)
        {
            DirectoryInfo? current = null;

            try
            {
                current = new DirectoryInfo(baseDirectory);
            }
            catch
            {
                yield break;
            }

            while (current != null)
            {
                string? candidate = null;

                try
                {
                    candidate = Path.Combine(current.FullName, "DLL");
                }
                catch
                {
                    candidate = null;
                }

                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    yield return candidate;
                }

                current = current.Parent;
            }
        }
        /// <summary>
        /// 将 SDK 目录下的 DLL 与资源复制到目标目录。
        /// </summary>
        /// <param name="sourceDirectory">源目录。</param>
        /// <param name="targetDirectory">目标目录。</param>
        private static void CopySdkPayload(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);

            foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(file);
                var destination = Path.Combine(targetDirectory, fileName);
                File.Copy(file, destination, true);
                AppLogger.Info($"复制 Aikit 依赖文件: {fileName} -> {destination}");
            }

            var sourceResourceDir = Path.Combine(sourceDirectory, "resource");
            if (Directory.Exists(sourceResourceDir))
            {
                var targetResourceDir = Path.Combine(targetDirectory, "resource");
                CopyDirectoryRecursively(sourceResourceDir, targetResourceDir);
                AppLogger.Info($"复制 resource 目录: {sourceResourceDir} -> {targetResourceDir}");
            }
        }
        /// <summary>
        /// 递归复制目录及其文件，用于迁移 resource 资源。
        /// </summary>
        /// <param name="sourceDir">源目录。</param>
        /// <param name="targetDir">目标目录。</param>
        private static void CopyDirectoryRecursively(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var destination = Path.Combine(targetDir, relative);
                var destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(file, destination, true);
            }
        }
        /// <summary>
        /// 递归复制目录及其文件，用于迁移 resource 资源。
        /// </summary>
        /// <param name="sourceDir">源目录。</param>
        /// <param name="targetDir">目标目录。</param>
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
    /// <summary>
    /// 唤醒词识别事件参数，包含关键词与可选的提示消息。
    /// </summary>
    public sealed class WakeKeywordEventArgs : EventArgs
    {
        /// <summary>
        /// 使用给定的唤醒词与提示消息构造事件参数。
        /// </summary>
        /// <param name="keyword">识别到的唤醒词。</param>
        /// <param name="notificationMessage">可选的提示消息，可用于界面层展示。</param>
        public WakeKeywordEventArgs(string keyword, string? notificationMessage)
        {
            Keyword = keyword;
            NotificationMessage = notificationMessage;
        }

        /// <summary>
        /// 识别到的唤醒词内容。
        /// </summary>
        public string Keyword { get; }

        /// <summary>
        /// 供界面展示的提示消息，可能为空。
        /// </summary>
        public string? NotificationMessage { get; }
    }
}