using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Xtts10SampleNet8;

/// <summary>
/// 程序入口，用于演示通过 iFLYTEK AIKIT SDK 调用流式语音合成能力。
/// </summary>
internal static class Program
{
    // 回调保持引用防止被 GC 回收
    private static readonly NativeMethods.AIKIT_OnOutput OutputCallback = OnOutput;
    private static readonly NativeMethods.AIKIT_OnEvent EventCallback = OnEvent;
    private static readonly NativeMethods.AIKIT_OnError ErrorCallback = OnError;

    // 等待语音合成结束的事件
    private static readonly ManualResetEventSlim TtsFinishedEvent = new(false);

    // PCM 输出路径
    private const string PcmOutputPath = "OutPut.pcm";

    // WAV 输出路径
    private const string WavOutputPath = "OutPut.wav";

    /// <summary>
    /// 主函数：初始化 SDK、注册回调并执行一次文本转语音流程。
    /// </summary>
    private static void Main()
    {
        // 设置控制台编码
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("[INFO] 程序启动，准备初始化 AIKIT SDK。");

        // 构造初始化参数
        var initParam = new NativeMethods.AIKIT_InitParam
        {
            authType = 0, // 授权方式：0 表示设备级授权
            appID = "50334b7e",
            apiKey = "0fb097671abc68e6383f049571ac7eb2",
            apiSecret = "MjdjYzk3OGE1ZWQ3NTAxYTliZmUzNmYz",
            workDir = Path.GetFullPath("./"),
            resDir = Path.GetFullPath("./resource"),
            licenseFile = null,
            batchID = null,
            UDID = null,
            cfgFile = null
        };

        // 设置日志输出
        // 日志文件路径
        var logPath = Path.GetFullPath("./log.txt");
        var logResult = NativeMethods.AIKIT_SetLogInfo(0, 2, logPath);
        Console.WriteLine($"[INFO] 设置日志信息结果：{logResult}");

        // 注册全局回调
        var callbacks = new NativeMethods.AIKIT_Callbacks
        {
            outputCB = OutputCallback,
            eventCB = EventCallback,
            errorCB = ErrorCallback
        };
        // 回调注册结果
        var registerResult = NativeMethods.AIKIT_RegisterCallback(callbacks);
        Console.WriteLine($"[INFO] 注册回调结果：{registerResult}");

        // 初始化 SDK
        // 初始化结果
        var initResult = NativeMethods.AIKIT_Init(ref initParam);
        Console.WriteLine($"[INFO] AIKIT_Init 结果：{initResult}");
        if (initResult != 0)
        {
            Console.WriteLine("[ERROR] SDK 初始化失败，程序退出。");
            return;
        }

        try
        {
            RunTtsFlow();
        }
        finally
        {
            // 逆初始化
            var uninitResult = NativeMethods.AIKIT_UnInit();
            Console.WriteLine($"[INFO] SDK 逆初始化完成，结果：{uninitResult}");
        }

        Console.WriteLine("[INFO] 程序执行完毕，按任意键退出。");
    }

    /// <summary>
    /// 执行文本转语音流程：构建参数、提交文本、等待回调并转换为 WAV 文件。
    /// </summary>
    private static void RunTtsFlow()
    {
        Console.WriteLine("[INFO] 开始执行 TTS 逻辑。");
        CleanupPreviousOutputs();

        // 创建参数构造器
        // 参数构造器句柄
        var paramBuilder = NativeMethods.AIKITBuilder_Create(NativeMethods.BuilderType.Param);
        if (paramBuilder == IntPtr.Zero)
        {
            Console.WriteLine("[ERROR] 创建参数构造器失败。");
            return;
        }

        // 创建数据构造器
        // 数据构造器句柄
        var dataBuilder = NativeMethods.AIKITBuilder_Create(NativeMethods.BuilderType.Data);
        if (dataBuilder == IntPtr.Zero)
        {
            Console.WriteLine("[ERROR] 创建数据构造器失败。");
            NativeMethods.AIKITBuilder_Destroy(paramBuilder);
            return;
        }

        var abilityId = "e2e44feff"; // 能力编号
        var voiceName = "xiaoyan"; // 发音人
        var text = "技术支持跑的快,全靠大佬们带,受着老板们和研发大佬熏陶,技术支持也不会掉队的."; // 合成文本

        IntPtr textPtr = IntPtr.Zero; // 文本指针

        try
        {
            // 设置参数
            AddStringParam(paramBuilder, "vcn", voiceName);
            AddStringParam(paramBuilder, "vcnModel", voiceName);
            NativeMethods.AIKITBuilder_AddInt(paramBuilder, "language", 1);
            AddStringParam(paramBuilder, "textEncoding", "UTF-8");

            // 准备文本数据
            // 文本内容字节
            var textBytes = Encoding.UTF8.GetBytes(text);
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

            // 添加文本结果
            var addBufResult = NativeMethods.AIKITBuilder_AddBuf(dataBuilder, ref builderData);
            Console.WriteLine($"[INFO] 添加文本数据结果：{addBufResult}");
            if (addBufResult != 0)
            {
                return;
            }

            // 构建指针
            var paramPtr = NativeMethods.AIKITBuilder_BuildParam(paramBuilder); // 参数指针
            var dataPtr = NativeMethods.AIKITBuilder_BuildData(dataBuilder); // 数据指针

            Console.WriteLine("[INFO] 启动流式会话。");
            TtsFinishedEvent.Reset();
            var handlePtr = IntPtr.Zero; // 会话句柄
            var startResult = NativeMethods.AIKIT_Start(abilityId, paramPtr, IntPtr.Zero, ref handlePtr);
            Console.WriteLine($"[INFO] AIKIT_Start 结果：{startResult}");
            if (startResult != 0)
            {
                return;
            }

            try
            {
                var writeResult = NativeMethods.AIKIT_Write(handlePtr, dataPtr); // 写入结果
                Console.WriteLine($"[INFO] AIKIT_Write 结果：{writeResult}");
                if (writeResult != 0)
                {
                    return;
                }

                Console.WriteLine("[INFO] 等待语音合成完成事件。");
                TtsFinishedEvent.Wait();
                Console.WriteLine("[INFO] 接收到合成完成事件，准备转换 PCM 为 WAV。");
                ConvertPcmToWav(PcmOutputPath, WavOutputPath, 16000, 1, 16);
            }
            finally
            {
                var endResult = NativeMethods.AIKIT_End(handlePtr); // 结束结果
                Console.WriteLine($"[INFO] AIKIT_End 结果：{endResult}");
            }
        }
        finally
        {
            if (textPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(textPtr);
            }
            NativeMethods.AIKITBuilder_Destroy(dataBuilder);
            NativeMethods.AIKITBuilder_Destroy(paramBuilder);
        }
    }

    /// <summary>
    /// 添加字符串参数到构造器，包含基本的错误日志输出。
    /// </summary>
    /// <param name="builder">构造器句柄。</param>
    /// <param name="key">参数名称。</param>
    /// <param name="value">参数值。</param>
    private static void AddStringParam(IntPtr builder, string key, string value)
    {
        var result = NativeMethods.AIKITBuilder_AddString(builder, key, value, Encoding.UTF8.GetByteCount(value)); // 返回码
        Console.WriteLine($"[INFO] 添加参数 {key} 结果：{result}");
    }

    /// <summary>
    /// 回调：处理 SDK 输出的音频数据，持续写入 PCM 文件。
    /// </summary>
    private static void OnOutput(IntPtr handle, IntPtr outputPtr)
    {
        if (outputPtr == IntPtr.Zero)
        {
            Console.WriteLine("[WARN] 输出回调收到空数据。");
            return;
        }

        var output = Marshal.PtrToStructure<NativeMethods.AIKIT_OutputData>(outputPtr); // 输出结构
        var nodePtr = output.node; // 当前节点
        while (nodePtr != IntPtr.Zero)
        {
            var node = Marshal.PtrToStructure<NativeMethods.AIKIT_BaseData>(nodePtr); // 节点内容
            var key = Marshal.PtrToStringAnsi(node.key) ?? string.Empty; // 数据标识
            Console.WriteLine($"[DEBUG] OnOutput key={key}, len={node.len}, status={node.status}");
            if (node.value != IntPtr.Zero && node.len > 0)
            {
                var buffer = new byte[node.len]; // 音频片段
                Marshal.Copy(node.value, buffer, 0, node.len);
                AppendBytesToFile(PcmOutputPath, buffer);
            }

            nodePtr = node.next;
        }
    }

    /// <summary>
    /// 将字节数据追加到指定文件，内部使用 FileStream 避免未定义的 File.AppendAllBytes 调用。
    /// </summary>
    /// <param name="filePath">目标文件路径。</param>
    /// <param name="data">需要写入的字节数据。</param>
    private static void AppendBytesToFile(string filePath, byte[] data)
    {
        Console.WriteLine($"[DEBUG] 准备写入 PCM 片段，长度：{data.Length}");
        // 追加模式写入文件
        using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    /// <summary>
    /// 回调：处理事件通知，遇到结束事件时解除等待。
    /// </summary>
    private static void OnEvent(IntPtr handle, NativeMethods.AIKIT_EVENT eventType, IntPtr eventValue)
    {
        Console.WriteLine($"[INFO] OnEvent 收到事件：{eventType}");
        if (eventType == NativeMethods.AIKIT_EVENT.End)
        {
            TtsFinishedEvent.Set();
        }
    }

    /// <summary>
    /// 回调：输出错误码和描述信息，方便排查问题。
    /// </summary>
    private static void OnError(IntPtr handle, int errorCode, IntPtr descPtr)
    {
        var desc = descPtr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(descPtr);
        Console.WriteLine($"[ERROR] OnError code={errorCode}, desc={desc}");
    }

    /// <summary>
    /// 删除历史生成的 PCM/WAV 文件，避免旧数据影响测试。
    /// </summary>
    private static void CleanupPreviousOutputs()
    {
        if (File.Exists(PcmOutputPath))
        {
            File.Delete(PcmOutputPath);
            Console.WriteLine("[INFO] 已删除旧的 PCM 文件。");
        }

        if (File.Exists(WavOutputPath))
        {
            File.Delete(WavOutputPath);
            Console.WriteLine("[INFO] 已删除旧的 WAV 文件。");
        }
    }

    /// <summary>
    /// 将 PCM 裸数据转换为 WAV 文件，便于播放与调试。
    /// </summary>
    /// <param name="pcmPath">PCM 文件路径。</param>
    /// <param name="wavPath">目标 WAV 文件路径。</param>
    /// <param name="sampleRate">采样率。</param>
    /// <param name="channels">通道数。</param>
    /// <param name="bitsPerSample">量化位数。</param>
    private static void ConvertPcmToWav(string pcmPath, string wavPath, int sampleRate, short channels, short bitsPerSample)
    {
        if (!File.Exists(pcmPath))
        {
            Console.WriteLine("[WARN] 未找到 PCM 文件，跳过转换。");
            return;
        }

        var pcmData = File.ReadAllBytes(pcmPath);
        if (pcmData.Length == 0)
        {
            Console.WriteLine("[WARN] PCM 数据为空，跳过转换。");
            return;
        }

        Console.WriteLine($"[INFO] 开始转换 WAV，数据长度：{pcmData.Length}");
        using var wavFile = new FileStream(wavPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(wavFile, Encoding.UTF8, leaveOpen: false);

        // 写入 WAV 头信息
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmData.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        writer.Write(byteRate);
        var blockAlign = (short)(channels * bitsPerSample / 8);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmData.Length);
        writer.Write(pcmData);

        Console.WriteLine("[INFO] WAV 文件转换完成。");
    }

    /// <summary>
    /// 内部封装的 P/Invoke 声明以及相关结构、枚举定义。
    /// </summary>
    private static class NativeMethods
    {
        private const string DllName = "AEE_lib.dll"; // 库文件名称

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
            public int type; // 数据类型
            [MarshalAs(UnmanagedType.LPStr)]
            public string name; // 数据段名称
            public IntPtr data; // 数据地址
            public int len; // 数据长度
            public int status; // 数据状态
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AIKIT_InitParam
        {
            public int authType; // 授权类型
            [MarshalAs(UnmanagedType.LPStr)]
            public string? appID; // 应用 ID
            [MarshalAs(UnmanagedType.LPStr)]
            public string? apiKey; // 应用 Key
            [MarshalAs(UnmanagedType.LPStr)]
            public string? apiSecret; // 应用 Secret
            [MarshalAs(UnmanagedType.LPStr)]
            public string? workDir; // 工作目录
            [MarshalAs(UnmanagedType.LPStr)]
            public string? resDir; // 资源目录
            [MarshalAs(UnmanagedType.LPStr)]
            public string? licenseFile; // 授权文件
            [MarshalAs(UnmanagedType.LPStr)]
            public string? batchID; // 批次号
            [MarshalAs(UnmanagedType.LPStr)]
            public string? UDID; // 自定义设备标识
            [MarshalAs(UnmanagedType.LPStr)]
            public string? cfgFile; // 配置文件
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AIKIT_Callbacks
        {
            public AIKIT_OnOutput? outputCB; // 输出回调
            public AIKIT_OnEvent? eventCB; // 事件回调
            public AIKIT_OnError? errorCB; // 错误回调
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
            public IntPtr node; // 数据链表头
            public int count; // 节点数量
            public int totalLen; // 总长度
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AIKIT_BaseData
        {
            public IntPtr next; // 下一个节点
            public IntPtr desc; // 描述指针
            public IntPtr key; // 键名
            public IntPtr value; // 数据指针
            public IntPtr reserved; // 预留字段
            public int len; // 数据长度
            public int type; // 数据类型
            public int status; // 数据状态
            public int from; // 数据来源
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