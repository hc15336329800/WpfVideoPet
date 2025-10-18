using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WpfVideoPet.xunfei
{
    /// <summary>
    /// 封装讯飞 AIKIT 文本转语音流程的服务，实现初始化、会话驱动与结果文件生成。
    /// </summary>
    public sealed class AikitSayService : IDisposable
    {
        // 默认讯飞播报配置。
        private static readonly AikitListenSettings DefaultSettings = new()
        {
            HostUrl = string.Empty,
            ApiKey = "0fb097671abc68e6383f049571ac7eb2",
            ApiSecret = "MjdjYzk3OGE1ZWQ3NTAxYTliZmUzNmYz",
            AppId = "50334b7e",
            AbilityId = DefaultAbilityId,
            VoiceName = DefaultVoiceName,
            OutputDirectory = DefaultOutputDirectoryName
        };

        // 默认鉴权方式。
        private const int DefaultAuthType = 0;
        // 默认能力编号，对应 Program.cs 示例。
        private const string DefaultAbilityId = "e2e44feff";
        // 默认发音人。
        private const string DefaultVoiceName = "xiaoyan";
        // 默认输出目录名称。
        private const string DefaultOutputDirectoryName = "tts_output";
        // 默认语言编码。
        private const int DefaultLanguage = 1;
        // 默认文本编码格式。
        private const string DefaultTextEncoding = "UTF-8";

        // 输出回调委托引用。
        private static readonly NativeMethods.AIKIT_OnOutput OutputCallback = OnOutput;
        // 事件回调委托引用。
        private static readonly NativeMethods.AIKIT_OnEvent EventCallback = OnEvent;
        // 错误回调委托引用。
        private static readonly NativeMethods.AIKIT_OnError ErrorCallback = OnError;
        // 全局会话表用于在回调中定位上下文。
        private static readonly ConcurrentDictionary<IntPtr, SynthesisSession> Sessions = new();

        // 服务内部使用的讯飞配置。
        private readonly AikitListenSettings _settings;
        // 初始化过程的同步锁。
        private readonly object _initLock = new();
        // 控制串行执行的信号量。
        private readonly SemaphoreSlim _synthesisLock = new(1, 1);

        // 是否已释放资源标记。
        private bool _disposed;
        // 是否已完成 SDK 初始化标记。
        private bool _initialized;

        /// <summary>
        /// 使用外部提供的讯飞配置初始化 TTS 服务实例。
        /// </summary>
        /// <param name="settings">包含 ApiKey、ApiSecret、AppId 的讯飞配置。</param>
        /// <exception cref="ArgumentNullException">当配置为空时抛出。</exception>
        public AikitSayService(AikitListenSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 使用默认的讯飞配置初始化 TTS 服务实例，便于快速调试。
        /// </summary>
        public AikitSayService()
            : this(DefaultSettings)
        {
            AppLogger.Info("AikitSayService 已使用默认讯飞鉴权信息初始化。");
        }

        /// <summary>
        /// 执行一次文本转语音流程，完成后返回生成的 WAV 文件路径。
        /// </summary>
        /// <param name="text">需要合成的文本内容。</param>
        /// <param name="voiceName">可选的发音人名称，默认使用小燕。</param>
        /// <param name="cancellationToken">外部传入的取消令牌。</param>
        /// <returns>合成成功时的 WAV 文件绝对路径，失败或取消时返回 null。</returns>
        public async Task<string?> SynthesizeAsync(string text, string? voiceName = null, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("待合成文本不能为空。", nameof(text));
            }

            AppLogger.Info("收到新的文本转语音请求，正在等待内部锁以串行执行。");
            await _synthesisLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await Task.Run(() => SynthesizeInternal(text, voiceName, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _synthesisLock.Release();
            }
        }

        /// <summary>
        /// 释放讯飞 SDK 资源，防止进程退出时遗留句柄。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_initialized)
            {
                var result = NativeMethods.AIKIT_UnInit();
                AppLogger.Info($"AikitSayService 已完成 SDK 逆初始化，返回码：{result}。");
            }

            _synthesisLock.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 内部执行文本转语音的同步逻辑，负责完成一次完整的会话。
        /// </summary>
        /// <param name="text">待合成文本内容。</param>
        /// <param name="voiceName">需要使用的发音人。</param>
        /// <param name="cancellationToken">取消标记。</param>
        /// <returns>合成成功的 WAV 文件路径或 null。</returns>
        private string? SynthesizeInternal(string text, string? voiceName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();
            // 应用程序根目录。
            string baseDirectory = AppContext.BaseDirectory;
            // TTS 输出目录。
            string outputDirectory = ResolveOutputDirectory(baseDirectory);
            AppLogger.Info($"TTS 音频输出目录: {outputDirectory}");
            Directory.CreateDirectory(outputDirectory);

            // 输出文件名称前缀。
            string fileStem = $"tts_{DateTime.Now:yyyyMMddHHmmssfff}";
            // PCM 文件路径。
            string pcmPath = Path.Combine(outputDirectory, fileStem + ".pcm");
            // WAV 文件路径。
            string wavPath = Path.Combine(outputDirectory, fileStem + ".wav");

            // 实际发音人。
            string voiceToUse = ResolveVoiceName(voiceName);
            AppLogger.Info($"准备使用发音人: {voiceToUse}");

            // 实际能力编号。
            string abilityId = ResolveAbilityId();
            AppLogger.Info($"准备使用 TTS 能力编号: {abilityId}");

            using var session = new SynthesisSession(pcmPath, wavPath);
            session.CleanupOutputs();

            IntPtr paramBuilder = IntPtr.Zero;
            IntPtr dataBuilder = IntPtr.Zero;
            IntPtr textPtr = IntPtr.Zero;
            IntPtr handlePtr = IntPtr.Zero;
            string? resultPath = null;

            try
            {
                paramBuilder = NativeMethods.AIKITBuilder_Create(NativeMethods.BuilderType.Param);
                if (paramBuilder == IntPtr.Zero)
                {
                    AppLogger.Error("创建讯飞参数构造器失败。");
                    return null;
                }

                dataBuilder = NativeMethods.AIKITBuilder_Create(NativeMethods.BuilderType.Data);
                if (dataBuilder == IntPtr.Zero)
                {
                    AppLogger.Error("创建讯飞数据构造器失败。");
                    return null;
                }

                AddStringParam(paramBuilder, "vcn", voiceToUse);
                AddStringParam(paramBuilder, "vcnModel", voiceToUse);
                // 语言参数写入结果。
                int languageResult = NativeMethods.AIKITBuilder_AddInt(paramBuilder, "language", DefaultLanguage);
                AppLogger.Info($"写入语言参数返回码：{languageResult}。");
                AddStringParam(paramBuilder, "textEncoding", DefaultTextEncoding);

                byte[] textBytes = Encoding.UTF8.GetBytes(text);
                textPtr = Marshal.AllocHGlobal(textBytes.Length);
                Marshal.Copy(textBytes, 0, textPtr, textBytes.Length);

                var builderData = new NativeMethods.BuilderData
                {
                    type = (int)NativeMethods.BuilderDataType.Text,
                    name = "text",
                    data = textPtr,
                    len = textBytes.Length,
                    status = (int)NativeMethods.AIKIT_DataStatus.Once
                };

                int addBufResult = NativeMethods.AIKITBuilder_AddBuf(dataBuilder, ref builderData);
                AppLogger.Info($"文本数据已写入讯飞数据构造器，返回码：{addBufResult}。");
                if (addBufResult != 0)
                {
                    AppLogger.Error($"写入文本数据失败，返回码：{addBufResult}。");
                    return null;
                }

                IntPtr paramPtr = NativeMethods.AIKITBuilder_BuildParam(paramBuilder);
                IntPtr dataPtr = NativeMethods.AIKITBuilder_BuildData(dataBuilder);

                AppLogger.Info("准备启动讯飞 TTS 会话。");
                int startResult = NativeMethods.AIKIT_Start(abilityId, paramPtr, IntPtr.Zero, ref handlePtr);
                AppLogger.Info($"AIKIT_Start 返回码：{startResult}，AbilityId={abilityId}。");
                if (startResult != 0)
                {
                    AppLogger.Error($"讯飞 TTS 会话启动失败，错误码：{startResult}，请核对 AppId、AbilityId 与鉴权信息是否匹配。");
                    return null;
                }

                if (!Sessions.TryAdd(handlePtr, session))
                {
                    AppLogger.Error("无法登记 TTS 会话上下文，终止流程。");
                    return null;
                }

                try
                {
                    int writeResult = NativeMethods.AIKIT_Write(handlePtr, dataPtr);
                    AppLogger.Info($"AIKIT_Write 返回码：{writeResult}。");
                    if (writeResult != 0)
                    {
                        return null;
                    }

                    AppLogger.Info("等待讯飞 TTS 完成事件。");
                    session.WaitForCompletion(cancellationToken);
                    AppLogger.Info("讯飞 TTS 会话已结束等待。");

                    if (!string.IsNullOrEmpty(session.ErrorMessage))
                    {
                        AppLogger.Error($"讯飞 TTS 会话报错：{session.ErrorMessage}");
                        return null;
                    }

                    if (!session.HasAudioData)
                    {
                        AppLogger.Warn("讯飞 TTS 未返回任何音频数据。");
                        return null;
                    }

                    ConvertPcmToWav(session.PcmPath, session.WavPath, session.SampleRate, session.Channels, session.BitsPerSample);
                    resultPath = session.WavPath;
                }
                finally
                {
                    AppLogger.Info("准备结束讯飞 TTS 会话。");
                    int endResult = NativeMethods.AIKIT_End(handlePtr);
                    AppLogger.Info($"AIKIT_End 返回码：{endResult}。");
                    Sessions.TryRemove(handlePtr, out _);
                }
            }
            finally
            {
                if (textPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(textPtr);
                }

                if (dataBuilder != IntPtr.Zero)
                {
                    NativeMethods.AIKITBuilder_Destroy(dataBuilder);
                }

                if (paramBuilder != IntPtr.Zero)
                {
                    NativeMethods.AIKITBuilder_Destroy(paramBuilder);
                }
            }

            return resultPath;
        }

        /// <summary>
        /// 解析最终使用的发音人，优先采用外部参数，其次读取配置，最后回退到默认值。
        /// </summary>
        /// <param name="voiceName">调用方指定的发音人。</param>
        /// <returns>最终用于 TTS 请求的发音人名称。</returns>
        private string ResolveVoiceName(string? voiceName)
        {
            string configuredVoice = string.IsNullOrWhiteSpace(_settings.VoiceName) ? DefaultVoiceName : _settings.VoiceName;
            return string.IsNullOrWhiteSpace(voiceName) ? configuredVoice : voiceName;
        }

        /// <summary>
        /// 解析最终使用的能力编号，允许通过配置覆盖默认值。
        /// </summary>
        /// <returns>可用于 AIKIT_Start 的能力编号。</returns>
        private string ResolveAbilityId()
        {
            return string.IsNullOrWhiteSpace(_settings.AbilityId) ? DefaultAbilityId : _settings.AbilityId;
        }

        /// <summary>
        /// 解析最终使用的输出目录，支持绝对路径与相对路径。
        /// </summary>
        /// <param name="baseDirectory">应用根目录，用于拼接相对路径。</param>
        /// <returns>确保有效的输出目录。</returns>
        private string ResolveOutputDirectory(string baseDirectory)
        {
            if (!string.IsNullOrWhiteSpace(_settings.OutputDirectory))
            {
                string configured = _settings.OutputDirectory;
                if (Path.IsPathRooted(configured))
                {
                    return configured;
                }

                return Path.GetFullPath(Path.Combine(baseDirectory, configured));
            }

            return Path.Combine(baseDirectory, DefaultOutputDirectoryName);

        }
        /// <summary>
        /// 确保讯飞 AIKIT SDK 已完成初始化并注册回调。
        /// </summary>
        private void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            lock (_initLock)
            {
                if (_initialized)
                {
                    return;
                }

                // SDK 工作目录，与 Program.cs 示例保持一致。
                string workDir = Path.GetFullPath("./");
                // 资源目录，直接使用示例中的 resource 文件夹。
                string resourceDir = Path.GetFullPath("./resource");
                // 日志文件路径。
                string logPath = Path.Combine(workDir, "aikit_tts.log");

                if (!Directory.Exists(resourceDir))
                {
                    AppLogger.Warn($"未找到资源目录：{resourceDir}，可能导致 SDK 初始化失败。");
                }

                var initParam = new NativeMethods.AIKIT_InitParam
                {
                    authType = DefaultAuthType,
                    appID = _settings.AppId,
                    apiKey = _settings.ApiKey,
                    apiSecret = _settings.ApiSecret,
                    workDir = workDir,
                    resDir = resourceDir,
                    licenseFile = null,
                    batchID = null,
                    UDID = null,
                    cfgFile = null
                };

                // 日志配置结果码。
                int logResult = NativeMethods.AIKIT_SetLogInfo(0, 2, logPath);
                AppLogger.Info($"设置讯飞日志信息结果：{logResult}，路径：{logPath}");

                var callbacks = new NativeMethods.AIKIT_Callbacks
                {
                    outputCB = OutputCallback,
                    eventCB = EventCallback,
                    errorCB = ErrorCallback
                };

                int registerResult = NativeMethods.AIKIT_RegisterCallback(callbacks);
                AppLogger.Info($"讯飞 TTS 回调注册结果：{registerResult}。");

                string abilityId = ResolveAbilityId(); // 初始化时记录实际能力编号。
                AppLogger.Info($"即将初始化讯飞 SDK，AppId={_settings.AppId}，AbilityId={abilityId}。");
                int initResult = NativeMethods.AIKIT_Init(ref initParam);
                if (initResult != 0)
                {
                    AppLogger.Error($"讯飞 TTS SDK 初始化失败，错误码：{initResult}。");
                    throw new InvalidOperationException($"AIKIT 初始化失败，错误码：{initResult}");
                }

                AppLogger.Info("讯飞 TTS SDK 初始化成功。");
                _initialized = true;
            }
        }



        /// <summary>
        /// 向参数构造器写入字符串键值对，统一处理日志输出。
        /// </summary>
        /// <param name="builder">AIKIT 参数构造器句柄。</param>
        /// <param name="key">参数名称。</param>
        /// <param name="value">参数值。</param>
        private static void AddStringParam(IntPtr builder, string key, string value)
        {
            int length = Encoding.UTF8.GetByteCount(value);
            int result = NativeMethods.AIKITBuilder_AddString(builder, key, value, length);
            AppLogger.Info($"写入参数 {key}，返回码：{result}。");
        }

        /// <summary>
        /// 将 PCM 文件转换成 WAV，方便外部播放或调试。
        /// </summary>
        /// <param name="pcmPath">PCM 文件路径。</param>
        /// <param name="wavPath">目标 WAV 文件路径。</param>
        /// <param name="sampleRate">采样率。</param>
        /// <param name="channels">通道数。</param>
        /// <param name="bitsPerSample">采样位宽。</param>
        private static void ConvertPcmToWav(string pcmPath, string wavPath, int sampleRate, short channels, short bitsPerSample)
        {
            if (!File.Exists(pcmPath))
            {
                AppLogger.Warn("未找到 PCM 源文件，无法转换 WAV。");
                return;
            }

            byte[] pcmData = File.ReadAllBytes(pcmPath);
            if (pcmData.Length == 0)
            {
                AppLogger.Warn("PCM 数据长度为 0，跳过 WAV 转换。");
                return;
            }

            AppLogger.Info($"开始执行 PCM->WAV 转换，PCM 长度：{pcmData.Length}。");
            using var wavFile = new FileStream(wavPath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(wavFile, Encoding.UTF8, leaveOpen: false);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcmData.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            writer.Write(byteRate);
            short blockAlign = (short)(channels * bitsPerSample / 8);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcmData.Length);
            writer.Write(pcmData);

            AppLogger.Info("PCM->WAV 转换完成。");
        }

        /// <summary>
        /// 确保服务实例处于未释放状态，防止访问已销毁对象。
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AikitSayService));
            }
        }

        /// <summary>
        /// 文本转语音会话上下文，负责记录输出文件与状态。
        /// </summary>
        private sealed class SynthesisSession : IDisposable
        {
            // PCM 文件路径。
            public string PcmPath { get; }
            // WAV 文件路径。
            public string WavPath { get; }
            // PCM 采样率。
            public int SampleRate { get; } = 16000;
            // 音频通道数。
            public short Channels { get; } = 1;
            // 采样位宽。
            public short BitsPerSample { get; } = 16;
            // 用于等待回调结束的事件。
            private ManualResetEventSlim CompletionEvent { get; } = new(false);
            // 最终错误信息。
            public string? ErrorMessage { get; set; }

            /// <summary>
            /// 构造会话上下文。
            /// </summary>
            /// <param name="pcmPath">PCM 文件输出路径。</param>
            /// <param name="wavPath">WAV 文件输出路径。</param>
            public SynthesisSession(string pcmPath, string wavPath)
            {
                PcmPath = pcmPath;
                WavPath = wavPath;
            }

            /// <summary>
            /// 指示当前是否已经写入音频数据。
            /// </summary>
            public bool HasAudioData => File.Exists(PcmPath) && new FileInfo(PcmPath).Length > 0;

            /// <summary>
            /// 删除旧的输出文件，确保本次合成结果不被历史数据干扰。
            /// </summary>
            public void CleanupOutputs()
            {
                if (File.Exists(PcmPath))
                {
                    File.Delete(PcmPath);
                    AppLogger.Info($"已清理旧的 PCM 文件：{PcmPath}");
                }

                if (File.Exists(WavPath))
                {
                    File.Delete(WavPath);
                    AppLogger.Info($"已清理旧的 WAV 文件：{WavPath}");
                }
            }

            /// <summary>
            /// 追加一段音频数据到 PCM 文件。
            /// </summary>
            /// <param name="buffer">音频数据缓冲区。</param>
            public void AppendAudio(byte[] buffer)
            {
                using var stream = new FileStream(PcmPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(buffer, 0, buffer.Length);
                stream.Flush();
                AppLogger.Info($"已写入 PCM 数据片段，长度：{buffer.Length}");
            }

            /// <summary>
            /// 等待合成完成事件，支持取消。
            /// </summary>
            /// <param name="cancellationToken">取消令牌。</param>
            public void WaitForCompletion(CancellationToken cancellationToken)
            {
                CompletionEvent.Wait(cancellationToken);
            }

            /// <summary>
            /// 通知会话已经结束。
            /// </summary>
            public void MarkCompleted()
            {
                CompletionEvent.Set();
            }

            /// <summary>
            /// 释放事件资源。
            /// </summary>
            public void Dispose()
            {
                CompletionEvent.Dispose();
            }
        }

        /// <summary>
        /// 处理讯飞输出回调，将音频数据写入当前会话。
        /// </summary>
        /// <param name="handle">会话句柄。</param>
        /// <param name="outputPtr">输出数据指针。</param>
        private static void OnOutput(IntPtr handle, IntPtr outputPtr)
        {
            if (!Sessions.TryGetValue(handle, out var session))
            {
                AppLogger.Warn("无法在输出回调中找到对应的会话上下文。");
                return;
            }

            if (outputPtr == IntPtr.Zero)
            {
                AppLogger.Warn("输出回调未返回有效数据指针。");
                return;
            }

            var output = Marshal.PtrToStructure<NativeMethods.AIKIT_OutputData>(outputPtr);
            IntPtr nodePtr = output.node;

            while (nodePtr != IntPtr.Zero)
            {
                var node = Marshal.PtrToStructure<NativeMethods.AIKIT_BaseData>(nodePtr);
                string key = Marshal.PtrToStringAnsi(node.key) ?? string.Empty;
                AppLogger.Info($"收到 TTS 输出节点，key={key}, len={node.len}, status={node.status}");

                if (node.value != IntPtr.Zero && node.len > 0)
                {
                    byte[] buffer = new byte[node.len];
                    Marshal.Copy(node.value, buffer, 0, node.len);
                    session.AppendAudio(buffer);
                }

                nodePtr = node.next;
            }
        }

        /// <summary>
        /// 处理讯飞事件回调，主要用于捕获结束事件。
        /// </summary>
        /// <param name="handle">会话句柄。</param>
        /// <param name="eventType">事件类型。</param>
        /// <param name="eventValue">事件附带的值。</param>
        private static void OnEvent(IntPtr handle, NativeMethods.AIKIT_EVENT eventType, IntPtr eventValue)
        {
            AppLogger.Info($"收到 TTS 事件：{eventType}");

            if (!Sessions.TryGetValue(handle, out var session))
            {
                AppLogger.Warn("事件回调未找到对应会话。");
                return;
            }

            if (eventType == NativeMethods.AIKIT_EVENT.End)
            {
                session.MarkCompleted();
            }
        }

        /// <summary>
        /// 处理讯飞错误回调，记录错误并唤醒等待线程。
        /// </summary>
        /// <param name="handle">会话句柄。</param>
        /// <param name="errorCode">错误码。</param>
        /// <param name="descPtr">错误描述指针。</param>
        private static void OnError(IntPtr handle, int errorCode, IntPtr descPtr)
        {
            string description = descPtr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(descPtr) ?? string.Empty;
            AppLogger.Error($"收到 TTS 错误回调，code={errorCode}, desc={description}");

            if (Sessions.TryGetValue(handle, out var session))
            {
                session.ErrorMessage = $"{errorCode}:{description}";
                session.MarkCompleted();
            }
        }

        /// <summary>
        /// 讯飞 SDK 所需的 P/Invoke 声明与结构定义。
        /// </summary>
        private static class NativeMethods
        {
            private const string DllName = "AEE_lib.dll";

            internal enum BuilderType
            {
                Param = 0,
                Data = 1
            }

            internal enum BuilderDataType
            {
                Text = 0,
                Audio = 1,
                Image = 2,
                Video = 3
            }

            internal enum AIKIT_DataStatus
            {
                Begin = 0,
                Continue = 1,
                End = 2,
                Once = 3
            }

            internal enum AIKIT_EVENT
            {
                Unknown = 0,
                Start = 1,
                End = 2,
                Timeout = 3,
                Progress = 4
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct BuilderData
            {
                public int type;
                [MarshalAs(UnmanagedType.LPStr)]
                public string name;
                public IntPtr data;
                public int len;
                public int status;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct AIKIT_InitParam
            {
                public int authType;
                [MarshalAs(UnmanagedType.LPStr)]
                public string? appID;
                [MarshalAs(UnmanagedType.LPStr)]
                public string? apiKey;
                [MarshalAs(UnmanagedType.LPStr)]
                public string? apiSecret;
                [MarshalAs(UnmanagedType.LPStr)]
                public string? workDir;
                [MarshalAs(UnmanagedType.LPStr)]
                public string? resDir;
                [MarshalAs(UnmanagedType.LPStr)]
                public string? licenseFile;
                [MarshalAs(UnmanagedType.LPStr)]
                public string? batchID;
                [MarshalAs(UnmanagedType.LPStr)]
                public string? UDID;
                [MarshalAs(UnmanagedType.LPStr)]
                public string? cfgFile;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct AIKIT_Callbacks
            {
                public AIKIT_OnOutput? outputCB;
                public AIKIT_OnEvent? eventCB;
                public AIKIT_OnError? errorCB;
            }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void AIKIT_OnOutput(IntPtr handle, IntPtr output);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void AIKIT_OnEvent(IntPtr handle, AIKIT_EVENT eventType, IntPtr eventValue);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void AIKIT_OnError(IntPtr handle, int err, IntPtr desc);

            [StructLayout(LayoutKind.Sequential)]
            internal struct AIKIT_OutputData
            {
                public IntPtr node;
                public int count;
                public int totalLen;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct AIKIT_BaseData
            {
                public IntPtr next;
                public IntPtr desc;
                public IntPtr key;
                public IntPtr value;
                public IntPtr reserved;
                public int len;
                public int type;
                public int status;
                public int from;
            }

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern IntPtr AIKITBuilder_Create(BuilderType type);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int AIKITBuilder_AddInt(IntPtr handle, string key, int value);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int AIKITBuilder_AddString(IntPtr handle, string key, string value, int length);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int AIKITBuilder_AddBuf(IntPtr handle, ref BuilderData data);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr AIKITBuilder_BuildParam(IntPtr handle);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr AIKITBuilder_BuildData(IntPtr handle);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void AIKITBuilder_Destroy(IntPtr handle);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int AIKIT_SetLogInfo(int level, int mode, string? path);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int AIKIT_RegisterCallback(AIKIT_Callbacks callbacks);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int AIKIT_Init(ref AIKIT_InitParam param);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int AIKIT_UnInit();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int AIKIT_Start(string ability, IntPtr param, IntPtr userContext, ref IntPtr handle);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int AIKIT_Write(IntPtr handle, IntPtr inputData);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int AIKIT_End(IntPtr handle);
        }
    }
}