using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WpfVideoPet;

namespace WpfVideoPet.xunfei
{

    //  文字转语音
    //  统一子目录名为 SayDLL：将原生 DLL、resource 资源与输出/工作目录切到该目录（最小修改，集中管理）禁止加入全局DLL路径防止污染
    public sealed class AikitSayService
    {

        //  预加载 SayDLL 目录中的所有原生库，避免污染全局搜索路径
        private static class WinNative
        {
            [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr LoadLibrary(string lpFileName);
        }
        //  预加载原生库，确保独立运行

        /// <summary>
        /// 预加载 SayDLL 子目录中的原生库，确保无需修改全局 DLL 搜索路径也能解析依赖。
        /// </summary>
        /// <param name="sayDllDir">SayDLL 目录的绝对路径。</param>
        /// <param name="preloadAll">是否预加载目录下的全部 DLL。</param>
        private static void EnsureNativeLibrariesLoaded(string sayDllDir, bool preloadAll = true)
        {
            if (!Directory.Exists(sayDllDir)) throw new DirectoryNotFoundException($"未找到 SayDLL 目录：{sayDllDir}");

            if (!preloadAll)
            {
                return;
            }

            foreach (var dll in Directory.GetFiles(sayDllDir, "*.dll"))
            {
                var handle = WinNative.LoadLibrary(dll);             // 预加载，避免二级依赖找不到；记录加载句柄
                if (handle == IntPtr.Zero)
                {
                    var errorCode = Marshal.GetLastPInvokeError();
                    AppLogger.Warn($"加载 SayDLL 目录下的原生库失败: {dll}，错误码: {errorCode}");
                }
                else
                {
                    AppLogger.Info($"已加载 SayDLL 原生库: {dll}");
                }
            }
        }


        // 【复刻：回调保持引用，防止 GC 回收】
        private static readonly NativeMethods.AIKIT_OnOutput OutputCallback = OnOutput;
        private static readonly NativeMethods.AIKIT_OnEvent EventCallback = OnEvent;
        private static readonly NativeMethods.AIKIT_OnError ErrorCallback = OnError;

        // 【复刻：结束事件】
        private static readonly ManualResetEventSlim TtsFinishedEvent = new(false);

        // 【复刻：输出文件名保持一致】
        private const string PcmOutputPath = "OutPut.pcm";
        private const string WavOutputPath = "OutPut.wav";

        // 【修改点-11：新增绝对路径属性；目的：把输出文件放到 SayDLL 文件夹中】
        private static string PcmOutputPathAbs => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SayDLL", PcmOutputPath);
        private static string WavOutputPathAbs => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SayDLL", WavOutputPath);

        // 【新增：防重复初始化标记】
        private static int _inited = 0;

        /// <summary>
        ///  新增 Init，目的：提供给外部显式初始化，严格复刻初始化参数】
        /// </summary>
        public void Init()
        {
            if (Interlocked.CompareExchange(ref _inited, 1, 0) == 1) return;

            //  新增并优先设置 DLL 搜索目录；目的：确保 AEE_lib.dll 及依赖从 SayDLL 解析】
            var sayDllDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SayDLL");
            AppLogger.Info($"准备加载 SayDLL 目录原生库: {sayDllDir}");
            EnsureNativeLibrariesLoaded(sayDllDir); // 必须在任何 AIKIT 调用之前执行

            // 日志路径切到 SayDLL；目的：日志集中在同一目录便于排查】
            var logPath = Path.Combine(sayDllDir, "log.txt");
            var logResult = NativeMethods.AIKIT_SetLogInfo(0, 2, logPath);

            // 注册回调（保留你原有代码）
            var callbacks = new NativeMethods.AIKIT_Callbacks { outputCB = OutputCallback, eventCB = EventCallback, errorCB = ErrorCallback };
            var registerResult = NativeMethods.AIKIT_RegisterCallback(callbacks);

            // 可选日志：Console.WriteLine($"[INFO] RegisterCB:{registerResult}");

            var baseDir = sayDllDir;         // Debug\net8.0-windows\SayDLL 目录下
            var initParam = new NativeMethods.AIKIT_InitParam
            {
                authType = 0,
                appID = "50334b7e",
                apiKey = "0fb097671abc68e6383f049571ac7eb2",
                apiSecret = "MjdjYzk3OGE1ZWQ3NTAxYTliZmUzNmYz",
                workDir = baseDir,                                   // → SayDLL 目录下
                resDir = Path.Combine(baseDir, "resource"),          // → SayDLL\resource  目录下
                licenseFile = null,
                batchID = null,
                UDID = null,
                cfgFile = null
            };

            var initResult = NativeMethods.AIKIT_Init(ref initParam);
            if (initResult != 0)
            {
                _inited = 0;
                throw new InvalidOperationException($"AIKIT_Init 失败：{initResult}");
            }
        }

        /// <summary>
        /// 【修改点-5：新增可复用入口，目的：严格复刻 RunTtsFlow；供外部调用执行一次 TTS】
        /// </summary>
        public void Speak(string text,
                          string voiceName = "xiaoyan",
                          string abilityId = "e2e44feff",
                          int sampleRate = 16000, short channels = 1, short bitsPerSample = 16)
        {
            if (_inited == 0) throw new InvalidOperationException("尚未初始化，请先调用 Init().");

            CleanupPreviousOutputs(); // 【复刻：清理旧文件】

            // 创建参数/数据构造器
            var paramBuilder = NativeMethods.AIKITBuilder_Create(NativeMethods.BuilderType.Param);
            if (paramBuilder == IntPtr.Zero) throw new InvalidOperationException("创建参数构造器失败。");

            var dataBuilder = NativeMethods.AIKITBuilder_Create(NativeMethods.BuilderType.Data);
            if (dataBuilder == IntPtr.Zero)
            {
                NativeMethods.AIKITBuilder_Destroy(paramBuilder);
                throw new InvalidOperationException("创建数据构造器失败。");
            }

            IntPtr textPtr = IntPtr.Zero;
            try
            {
                // 【复刻：参数设置】
                AddStringParam(paramBuilder, "vcn", voiceName);
                AddStringParam(paramBuilder, "vcnModel", voiceName);
                NativeMethods.AIKITBuilder_AddInt(paramBuilder, "language", 1);
                AddStringParam(paramBuilder, "textEncoding", "UTF-8");

                // 【复刻：准备文本数据】
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
                var addBufResult = NativeMethods.AIKITBuilder_AddBuf(dataBuilder, ref builderData);
                if (addBufResult != 0) throw new InvalidOperationException($"添加文本失败：{addBufResult}");

                // 【复刻：构建与启动会话】
                var paramPtr = NativeMethods.AIKITBuilder_BuildParam(paramBuilder);
                var dataPtr = NativeMethods.AIKITBuilder_BuildData(dataBuilder);

                TtsFinishedEvent.Reset();
                var handlePtr = IntPtr.Zero;
                var startResult = NativeMethods.AIKIT_Start(abilityId, paramPtr, IntPtr.Zero, ref handlePtr);
                if (startResult != 0) throw new InvalidOperationException($"AIKIT_Start 失败：{startResult}");

                try
                {
                    var writeResult = NativeMethods.AIKIT_Write(handlePtr, dataPtr);
                    if (writeResult != 0) throw new InvalidOperationException($"AIKIT_Write 失败：{writeResult}");

                    // 【复刻：等待结束事件】
                    TtsFinishedEvent.Wait();

                    // 【复刻：PCM 转 WAV】（保持原方法签名）
                    // 【修改点-11B：将转换输入/输出路径替换为 SayDLL 绝对路径】
                    ConvertPcmToWav(PcmOutputPathAbs, WavOutputPathAbs, sampleRate, channels, bitsPerSample);
                }
                finally
                {
                    NativeMethods.AIKIT_End(handlePtr); // 复刻：End
                }
            }
            finally
            {
                if (textPtr != IntPtr.Zero) Marshal.FreeHGlobal(textPtr);
                NativeMethods.AIKITBuilder_Destroy(dataBuilder);
                NativeMethods.AIKITBuilder_Destroy(paramBuilder);
            }
        }


        public void Uninit()
        {
            if (Interlocked.Exchange(ref _inited, 0) == 1)
            {
                NativeMethods.AIKIT_UnInit();
            }
        }

        // ================== 以下为复刻的私有辅助逻辑与回调 ==================

        private static void AddStringParam(IntPtr builder, string key, string value)
        {
            NativeMethods.AIKITBuilder_AddString(builder, key, value, Encoding.UTF8.GetByteCount(value));
        }

        private static void OnOutput(IntPtr handle, IntPtr outputPtr)
        {
            if (outputPtr == IntPtr.Zero) return;

            var output = Marshal.PtrToStructure<NativeMethods.AIKIT_OutputData>(outputPtr);
            var nodePtr = output.node;
            while (nodePtr != IntPtr.Zero)
            {
                var node = Marshal.PtrToStructure<NativeMethods.AIKIT_BaseData>(nodePtr);
                if (node.value != IntPtr.Zero && node.len > 0)
                {
                    var buffer = new byte[node.len];
                    Marshal.Copy(node.value, buffer, 0, node.len);
                    // 【修改点-11A：将写入路径替换为 SayDLL 绝对路径】
                    AppendBytesToFile(PcmOutputPathAbs, buffer);
                }
                nodePtr = node.next;
            }
        }

        private static void AppendBytesToFile(string filePath, byte[] data)
        {
            using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private static void OnEvent(IntPtr handle, NativeMethods.AIKIT_EVENT eventType, IntPtr eventValue)
        {
            if (eventType == NativeMethods.AIKIT_EVENT.End) TtsFinishedEvent.Set();
        }

        private static void OnError(IntPtr handle, int errorCode, IntPtr descPtr)
        {
            // 可选：外部可根据需要接管日志
            // var desc = descPtr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(descPtr);
            // Console.WriteLine($"[ERROR] {errorCode}: {desc}");
        }

        private static void CleanupPreviousOutputs()
        {
            // 【修改点-11C：删除 SayDLL 目录下的旧输出文件】
            if (File.Exists(PcmOutputPathAbs)) File.Delete(PcmOutputPathAbs);
            if (File.Exists(WavOutputPathAbs)) File.Delete(WavOutputPathAbs);
        }

        private static void ConvertPcmToWav(string pcmPath, string wavPath, int sampleRate, short channels, short bitsPerSample)
        {
            if (!File.Exists(pcmPath)) return;
            var pcmData = File.ReadAllBytes(pcmPath);
            if (pcmData.Length == 0) return;

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
            var byteRate = sampleRate * channels * bitsPerSample / 8;
            writer.Write(byteRate);
            var blockAlign = (short)(channels * bitsPerSample / 8);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcmData.Length);
            writer.Write(pcmData);
        }

        /// <summary>
        /// 【复刻：P/Invoke 与结构体定义；DLL 默认放 Debug 目录】
        /// </summary>
        private static class NativeMethods
        {
            private const string DllName = "AEE_lib.dll"; // 资源/DLL 均放 Debug 下

            internal enum BuilderType { Param = 0, Data = 1 }
            internal enum BuilderDataType { Text = 0, Audio = 1, Image = 2, Video = 3 }
            internal enum AIKIT_DataStatus { Begin = 0, Continue = 1, End = 2, Once = 3 }
            internal enum AIKIT_EVENT { Unknown = 0, Start = 1, End = 2, Timeout = 3, Progress = 4 }

            [StructLayout(LayoutKind.Sequential)]
            internal struct BuilderData
            {
                public int type;
                [MarshalAs(UnmanagedType.LPStr)] public string name;
                public IntPtr data;
                public int len;
                public int status;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct AIKIT_InitParam
            {
                public int authType;
                [MarshalAs(UnmanagedType.LPStr)] public string? appID;
                [MarshalAs(UnmanagedType.LPStr)] public string? apiKey;
                [MarshalAs(UnmanagedType.LPStr)] public string? apiSecret;
                [MarshalAs(UnmanagedType.LPStr)] public string? workDir;
                [MarshalAs(UnmanagedType.LPStr)] public string? resDir;
                [MarshalAs(UnmanagedType.LPStr)] public string? licenseFile;
                [MarshalAs(UnmanagedType.LPStr)] public string? batchID;
                [MarshalAs(UnmanagedType.LPStr)] public string? UDID;
                [MarshalAs(UnmanagedType.LPStr)] public string? cfgFile;
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
