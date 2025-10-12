
using NAudio.Wave; // 若未安装，可先注释掉，用下文 WAV 文件版
using System;
using System.IO;
using System.Media; // 新增
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.Versioning;


class Program
{
    // ====== 与 AikitDll.h 对齐的导入 ======
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void AD_OnOutput(IntPtr abilityId, IntPtr key, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void AD_OnEvent(IntPtr abilityId, int eventType, IntPtr payload);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void AD_OnError(IntPtr abilityId, int errCode, IntPtr desc);


    [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] 
    static extern int AD_Init(string appId, string apiKey, string apiSecret, string workDir, string resDir);

    [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl)] static extern int AD_UnInit();
    [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl)] static extern int AD_RegisterWakeCallbacks(AD_OnOutput o, AD_OnEvent e, AD_OnError er);
    [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] 
    static extern int AD_LoadKeywordFile(string abilityId, string key, string keywordTxtPath);
    [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] 
    static extern int AD_SpecifyKeywordSet(string abilityId, string key, int index);
    [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] 
    static extern int AD_StartWakeSession(string abilityId, string thresholdParam);
    [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl)] static extern int AD_WritePcm16(short[] pcm, int samples, int lastFlag);
    [DllImport("AikitDll.dll", CallingConvention = CallingConvention.Cdecl)] static extern int AD_EndWakeSession();

    static AD_OnOutput _o; static AD_OnEvent _e; static AD_OnError _er; // 防止被 GC
    static WaveInEvent mic;


    [SupportedOSPlatform("windows")]
    static void Main()
    {

        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        string baseDir = AppContext.BaseDirectory;

        // SDK 对路径的编码要求为 UTF-8，若直接把包含中文的绝对路径传给 native DLL，
        // .NET 默认会按系统 ANSI 编码（GBK 等）做一次转换，SDK 解析后会出现乱码。
        // 通过把当前工作目录切换到程序目录，再传入纯 ASCII 的相对路径，可避免该问题。
        Environment.CurrentDirectory = baseDir;

        string workDir = ".";                                           // 授权/日志缓存目录
        string resDir = System.IO.Path.Combine(".", "resource");      // SDK 资源目录
        string kwTxtFullPath = System.IO.Path.Combine(Environment.CurrentDirectory, "keyword.txt");
        string kwTxt = IsPureAscii(kwTxtFullPath)
            ? kwTxtFullPath
            : System.IO.Path.Combine(".", "keyword.txt");             // 自定义唤醒词（与可执行文件同级）

        Console.WriteLine("当前工作目录: " + Environment.CurrentDirectory);
        Console.WriteLine("授权缓存目录(workDir): " + System.IO.Path.GetFullPath(workDir));
        Console.WriteLine("资源目录(resDir): " + System.IO.Path.GetFullPath(resDir));
        Console.WriteLine("唤醒词文件: " + kwTxtFullPath);
        PrintWakeOnlyChecklist();



        // 1) 初始化（首跑需联网激活一次）  设置  APPID 、APIKey 、APISecret 证信息！！
        int r = AD_Init("50334b7e", "0fb097671abc68e6383f049571ac7eb2", "MjdjYzk3OGE1ZWQ3NTAxYTliZmUzNmYz", workDir, resDir);
        if (r != 0)
        {
            Console.WriteLine("AD_Init = " + r + " (" + TranslateError(r) + ")");
            Console.WriteLine(GetInitTroubleShooting(r, workDir));
            return;
        }
        Console.WriteLine("AD_Init = 0 (初始化成功)");
        // 2) 注册回调（打印结果）——VS2010请用拼接/Format
        _o = (abPtr, keyPtr, valPtr) =>
        {
            var ab = PtrToUtf8(abPtr);
            var key = PtrToUtf8(keyPtr);
            var val = PtrToUtf8(valPtr);

            if (key == "func_wake_up" || key == "rlt")
            {
                Console.WriteLine("[Out] func_wake_up:" + val);

                // 1) 优先用绝对路径（你可把文件放到 C:\ 或程序目录）
                var ringPathAbs = @"C:\chime.wav";

                // 2) 若不存在，则回退到程序目录下的 chime.wav
                var ringPath = File.Exists(ringPathAbs)
                    ? ringPathAbs
                    : Path.Combine(Environment.CurrentDirectory, "chime.wav");

                // 可选：再显式打印一次你选择的路径
                Console.WriteLine("[Ring] 选择: " + ringPath);

                PlayDing(ringPath);
            }
            else Console.WriteLine("[Out] " + key + ":" + val);
        };
        _e = (abPtr, evt, pPtr) => Console.WriteLine("[Evt] " + evt + " " + PtrToUtf8(pPtr));
        _er = (abPtr, err, dPtr) => Console.WriteLine("[Err] " + err + " " + PtrToUtf8(dPtr));

        Console.WriteLine("AD_RegisterWakeCallbacks = " + AD_RegisterWakeCallbacks(_o, _e, _er));

        // 3) 加载与指定唤醒词
        int loadRet = AD_LoadKeywordFile("e867a88f2", "key_word", kwTxt);
        Console.WriteLine("AD_LoadKeywordFile = " + loadRet);
        if (loadRet != 0)
        {
            Console.WriteLine("加载唤醒词失败: " + TranslateError(loadRet));
            Console.WriteLine(GetWakeTroubleShooting(loadRet, workDir, resDir, kwTxt));
            AD_UnInit();
            return;
        }

        int specifyRet = AD_SpecifyKeywordSet("e867a88f2", "key_word", 0);
        Console.WriteLine("AD_SpecifyKeywordSet = " + specifyRet);
        if (specifyRet != 0)
        {
            Console.WriteLine("激活唤醒词集合失败: " + TranslateError(specifyRet));
            Console.WriteLine(GetWakeTroubleShooting(specifyRet, workDir, resDir, kwTxt));
            AD_UnInit();
            return;
        }

        // 4) 开会话（阈值越大越严格，先 0:1500）
        //int startRet = AD_StartWakeSession("e867a88f2", "0:1500");

        int startRet = AD_StartWakeSession("e867a88f2", null);
        // 若成功，再试 "0 0:1500"，最后再试 "0:1500"

        Console.WriteLine("AD_StartWakeSession = " + startRet);
        if (startRet != 0)
        {
            Console.WriteLine("开启唤醒会话失败: " + TranslateError(startRet));
            Console.WriteLine(GetWakeTroubleShooting(startRet, workDir, resDir, kwTxt));
            AD_UnInit();
            return;
        }

        // 5) 麦克风：16k/16bit/Mono
        mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 20 };
        mic.DataAvailable += (s,a)=>{
            short[] pcm = new short[a.BytesRecorded/2];
            Buffer.BlockCopy(a.Buffer, 0, pcm, 0, a.BytesRecorded);
            AD_WritePcm16(pcm, pcm.Length, 0);
        };
        mic.StartRecording();

        Console.WriteLine("监听中… 说出 keyword.txt 里的中文唤醒词（回车停止）");
        Console.ReadLine();

        mic.StopRecording(); mic.Dispose();
        AD_EndWakeSession();
        AD_UnInit();
    }

    // 将因“把 UTF-8 当成本地 ANSI 解码”导致的中文乱码，纠回 UTF-8
    static string FixUtf8Mojibake(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        try
        {
            // 把“错误解码后的 .NET 字符串”按本地 ANSI 编回字节，再按 UTF-8 正确解码
            var bytes = Encoding.Default.GetBytes(s);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return s; }
    }

    // 只修 JSON 里的 "keyword": "..." 这一个字段，其他内容不改
    static string FixKeywordInJson(string json)
    {
        return Regex.Replace(
            json,
            "(\"keyword\"\\s*:\\s*\")([^\"]*)(\")",
            m => m.Groups[1].Value + FixUtf8Mojibake(m.Groups[2].Value) + m.Groups[3].Value
        );
    }
    static bool IsPureAscii(string value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        foreach (char c in value)
        {
            if (c > 0x7F) return false;
        }
        return true;
    }

    static string TranslateError(int code)
    {
        switch (code)
        {
            case 0: return "成功";
            case 18000: return "本地 license 文件不存在";
            case 18005: return "授权已过期";
            case 18007: return "授权信息与 APPID/APIKey 不匹配";
            case 18105: return "资源加载失败，workDir 内未找到对应资源";
            case 18400: return "工作目录无写权限";
            case 18705: return "云端应用 apiKey 或 apiSecret 校验失败";
            case 18707: return "云端授权已过期";
            case 18708: return "云端授权数量已满，请在控制台释放设备或联系讯飞扩容";
            case 100007: return "唤醒引擎启动失败";
            default:
                return "未知错误，详见 AikitDll/include/aikit_err.h";
        }
    }

    static string GetInitTroubleShooting(int code, string workDir)
    {
        switch (code)
        {
            case 18708:
                return "初始化失败：云端授权数量已满。请登录控制台→我的应用→终端管理确认是否有遗留设备占用；如数量正常，但依旧报错，请保留 " + workDir + " 下的 aikit_device*.dat 截图并联系讯飞技术支持核查。";
            case 18705:
                return "初始化失败：云端校验失败。请再次核对 AppID/APIKey/APISecret 是否与当前能力（语音唤醒 Websocket）一致，注意不要填成 REST API 的密钥。";
            case 18105:
                return "初始化失败：资源目录不完整。请确认 " + workDir + " 及其 resource 子目录中存在唤醒所需的模型文件。";
            default:
                return "初始化失败，请根据上方错误信息检查授权、资源配置或网络环境。";
        }
    }

    static string GetWakeTroubleShooting(int code, string workDir, string resDir, string keywordPath)
    {
        switch (code)
        {
            case 18105:
                return "请确认资源目录 " + System.IO.Path.GetFullPath(resDir) + " 内存在 ivw70/IVW_* 模型文件，且程序具有读权限。";
            case 18508:
                return "请确认 AD_SpecifyKeywordSet 传入的 key 与 keyword.txt 中配置一致（通常为 \"key_word\"）。";
            case 100007:
                return "引擎返回 100007：通常是唤醒词未正确加载。请确认 keyword.txt 为 UTF-8、每行以分号结尾，并确保 AD_LoadKeywordFile 返回 0；必要时删除 "
                    + System.IO.Path.Combine(System.IO.Path.GetFullPath(workDir), "aikit_device*.dat") + " 后重新初始化。";
            default:
                return "唤醒流程失败，请根据日志与返回码排查唤醒词格式（" + System.IO.Path.GetFullPath(keywordPath)
                    + "）、资源完整性或重新激活后再试。";
        }
    }

    static string PtrToUtf8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return string.Empty;

        // 计算以 0 结尾的 C 字符串长度
        int len = 0;
        while (Marshal.ReadByte(ptr, len) != 0) len++;

        // 复制字节并按 UTF-8 解码
        var bytes = new byte[len];
        Marshal.Copy(ptr, bytes, 0, len);
        return Encoding.UTF8.GetString(bytes);
    }

    // 唤醒时候的提示音
    static void PlayDing(string path)
    {
        try
        {
            Console.WriteLine("[Ring] 使用铃声: " + path + "  exists=" + File.Exists(path));
            if (!File.Exists(path))
            {
                Console.WriteLine("[Warn] 铃声文件不存在: " + path);
                return;
            }

            // 在线程池里同步播完，避免对象过早被释放
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var reader = new NAudio.Wave.AudioFileReader(path))   // 支持 WAV/MP3
                    using (var wo = new NAudio.Wave.WaveOutEvent())
                    {
                        wo.Init(reader);
                        wo.Play();
                        while (wo.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                            Thread.Sleep(20);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Warn] NAudio 播放失败：" + ex.Message);
                    try { Console.Beep(880, 180); Console.Beep(1320, 220); } catch { }
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Warn] 铃声播放失败：" + ex.Message);
        }
    }



    static void PrintWakeOnlyChecklist()
    {
        Console.WriteLine();
        Console.WriteLine("=== 语音唤醒能力配置自查 ===");
        Console.WriteLine("1. 控制台仅领取“语音唤醒(离线)”的装机量，其他能力（如离线听写/合成）不要同时激活。");
        Console.WriteLine("2. 若使用 Android Demo，请打开 demo 的 ability 配置文件，仅保留 AbilityConstant.IVW_ID（语音唤醒能力），其余能力常量全部注释。");
        Console.WriteLine("3. resource\\ability_config.json 中若存在多项 engineIds，请改成只包含语音唤醒；重新打包资源后再覆盖到设备。");
        Console.WriteLine("4. 再次执行初始化前，可删除 workDir 下旧的 aikit_device*.dat，避免历史激活信息干扰。");
        Console.WriteLine("================================");
        Console.WriteLine();
    }
}
