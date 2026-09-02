using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 通用键鼠序列回放器 — 方案B（Unity RuntimeInitializeOnLoadMethod 驱动）
/// 放入任意 Unity 游戏工程 Assets 即可使用（不改游戏原始代码），不依赖任何游戏类型。
/// 已实战验证：OpenAW3D 完整对局 91 事件回放 + mp4 录像（2026-09）。
///
/// 工作流程：
///   1. Application.runInBackground = true
///   2. 启动 2s 后初始化窗口几何（客户区坐标，见 DetectWindowGeometry 注释）
///   3. 游戏主场景（header.map）加载后固定相机到录制时位置（每次加载都会重新固定）
///   4. 逐事件按时间戳间隔 + 屏幕坐标用 xdotool 重放；
///      场景切换后先等新场景 + 相机就绪再继续（场景就绪屏障）
///   5. 每次点击前刷新窗口几何（自纠错：窗口被 WM 移动也能跟上）
///   6. 回放完成后写结果 → 停止录制 → 退出
///
/// 视频录制（可选，默认开）：WaitForEndOfFrame 读取屏幕像素——能同时捕捉 3D 场景与
/// OnGUI 的 IMGUI 界面（独立录制相机抓不到 IMGUI），RGBA 原始帧通过管道写入 ffmpeg
/// 子进程实时编码为 mp4。
///
/// 启动方式（命令行参数或环境变量，两者等价）：
///   -sequence /path/to/sequence.json        [REPLAY_SEQUENCE]
///   -camera-pos x,y,z                       [REPLAY_CAMERA_POS]      相机位置，与录制时一致
///   -camera-rot x,y,z                       [REPLAY_CAMERA_ROT]      相机旋转，与录制时一致
///   -camera-fov 60                          [REPLAY_CAMERA_FOV]      相机 FOV，与录制时一致
///   -camera-disable-script StrategyCamera   [REPLAY_CAMERA_DISABLE_SCRIPT]  要禁用的相机控制脚本（可选）
///   -replay-window-title "My Game"          [REPLAY_WINDOW_TITLE]    窗口标题（可选，默认 productName）
///   -replay-force-window 800x600            [REPLAY_FORCE_WINDOW]    强制窗口大小（可选，仅游戏全屏启动时用）
///   -replay-output-dir /path/to/output      [REPLAY_OUTPUT_DIR]      输出目录（可选）
///   -replay-quit-on-end true               [REPLAY_QUIT_ON_END]     回放结束退出进程（默认 true）
///   -replay-record true                     [REPLAY_RECORD]          录制视频（默认 true，需 ffmpeg）
///   -replay-record-fps 15                   [REPLAY_RECORD_FPS]       录制帧率（默认 15）
///   -replay-record-tail 3                   [REPLAY_REC_TAIL]        回放结束后继续录制秒数（默认 3）
///
/// 推荐配合启动参数（窗口模式 + 录制分辨率，避免全屏问题）：
///   ./game -screen-fullscreen 0 -screen-width 800 -screen-height 600 -sequence seq.json ...
///
/// 环境依赖：xdotool（必需）；ffmpeg（录像时需要）；x11-utils 的 xwininfo/xprop（推荐，
/// 用于客户区坐标；缺失时回退 xdotool 裸值——仅对无装饰窗口准确）。
///
/// 序列文件格式（JSON，JsonUtility 兼容）：
/// {
///   "header": { "game": "…", "map": "Scene01" },   // map = 游戏主场景名，场景就绪屏障用
///   "events": [
///     {"i":0, "frame":1, "t":2.8, "op":"click", "sx":400, "sy":323, "button":1,
///      "meta":"MainMenu/Play", "wait":"main_menu_visible"}
///   ]
/// }
/// sx, sy 为 Unity Screen 坐标（左下角原点，Y 向上）；t 为录制时 Time.unscaledTime 秒。
/// </summary>
public class SequenceReplayer : MonoBehaviour
{
    [System.Serializable]
    public class SeqEvent
    {
        public int i, frame;
        public float t;
        public string op;
        public int sx, sy;
        public int button;
        public string key;
        public string wait;
        public string meta;
    }

    [System.Serializable]
    public class SeqHeader
    {
        public string game;
        public string map;
    }

    [System.Serializable]
    public class SeqFile
    {
        public SeqHeader header;
        public SeqEvent[] events;
    }

    // 配置
    string _sequencePath;
    Vector3 _cameraPos = new Vector3(7.5f, 20f, 8.5f);
    Vector3 _cameraRot = new Vector3(90, 0, 0);
    float _cameraFov = 60;
    string _cameraDisableScript = "";
    string _windowTitle = "";
    int _forceW = 800, _forceH = 600;
    string _outputDir = "";
    bool _quitOnEnd = true;

    // 视频录制配置
    bool _recEnabled = true;
    int _recFps = 15;
    float _recTail = 3f;
    string _ffmpegPath = "ffmpeg";

    // 运行时
    SeqFile _seq;
    int _gvX, _gvY, _gvW, _gvH;
    bool _windowReady;
    string _markerPath;
    float _bootTime;
    string _windowWid;
    bool _forceWindowSet;
    bool _replayStarted;

    // 游戏主场景（header.map；无 header 时回退 AG 旧约定的 "Scene01" 探测）
    string _gameSceneName = "";
    int _lastSetupSceneHandle = -1;

    // 场景就绪屏障（场景切换后等新场景/相机就绪再继续，防止点击落在旧场景的按钮上）
    bool _settled;
    bool _cameraFrozen;

    // 录制运行时
    Texture2D _recTex;
    byte[] _recBuf;
    System.Diagnostics.Process _ffmpegProc;
    string _videoPath;
    int _recW, _recH;
    long _recFrames;
    float _recNext;
    bool _recording;
    bool _recSizeWarned;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoInit()
    {
        var path = GetArg("-sequence") ?? GetEnv("REPLAY_SEQUENCE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var go = new GameObject("SequenceReplayer");
        DontDestroyOnLoad(go);
        var r = go.AddComponent<SequenceReplayer>();
        r.LoadConfig();
    }

    static string GetArg(string name)
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    static string GetEnv(string name)
    {
        var v = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    static Vector3 ParseVec3(string s, Vector3 def)
    {
        if (string.IsNullOrEmpty(s)) return def;
        var parts = s.Split(',');
        if (parts.Length != 3) return def;
        float x, y, z;
        if (float.TryParse(parts[0], out x) && float.TryParse(parts[1], out y) && float.TryParse(parts[2], out z))
            return new Vector3(x, y, z);
        return def;
    }

    void LoadConfig()
    {
        _bootTime = Time.unscaledTime;
        _sequencePath = GetArg("-sequence") ?? GetEnv("REPLAY_SEQUENCE");
        _cameraPos = ParseVec3(GetArg("-camera-pos") ?? GetEnv("REPLAY_CAMERA_POS"), new Vector3(7.5f, 20f, 8.5f));
        _cameraRot = ParseVec3(GetArg("-camera-rot") ?? GetEnv("REPLAY_CAMERA_ROT"), new Vector3(90, 0, 0));
        float.TryParse(GetArg("-camera-fov") ?? GetEnv("REPLAY_CAMERA_FOV") ?? "60", out _cameraFov);
        _cameraDisableScript = GetArg("-camera-disable-script") ?? GetEnv("REPLAY_CAMERA_DISABLE_SCRIPT") ?? "";
        _windowTitle = GetArg("-replay-window-title") ?? GetEnv("REPLAY_WINDOW_TITLE") ?? "";
        _outputDir = GetArg("-replay-output-dir") ?? GetEnv("REPLAY_OUTPUT_DIR") ?? "";
        var quitStr = GetArg("-replay-quit-on-end") ?? GetEnv("REPLAY_QUIT_ON_END") ?? "true";
        _quitOnEnd = quitStr.ToLower() != "false";

        // 录制配置
        var recStr = GetArg("-replay-record") ?? GetEnv("REPLAY_RECORD") ?? "true";
        _recEnabled = recStr.ToLower() != "false";
        int recFps;
        if (int.TryParse(GetArg("-replay-record-fps") ?? GetEnv("REPLAY_RECORD_FPS") ?? "15", out recFps))
            _recFps = Mathf.Clamp(recFps, 1, 60);
        float recTail;
        if (float.TryParse(GetArg("-replay-record-tail") ?? GetEnv("REPLAY_REC_TAIL") ?? "3", out recTail))
            _recTail = recTail;
        _ffmpegPath = GetArg("-replay-ffmpeg") ?? GetEnv("REPLAY_FFMPEG_PATH") ?? "ffmpeg";

        var fw = GetArg("-replay-force-window") ?? GetEnv("REPLAY_FORCE_WINDOW");
        if (!string.IsNullOrEmpty(fw))
        {
            var parts = fw.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out _forceW) && int.TryParse(parts[1], out _forceH)) _forceWindowSet = true;
        }

        if (string.IsNullOrEmpty(_windowTitle)) _windowTitle = Application.productName;

        // 加载序列
        var json = File.ReadAllText(_sequencePath);
        _seq = JsonUtility.FromJson<SeqFile>(json);

        // 游戏主场景名（header.map），用于场景就绪屏障与相机固定
        _gameSceneName = (_seq != null && _seq.header != null && !string.IsNullOrEmpty(_seq.header.map)) ? _seq.header.map : "";
        _settled = false; // 初始场景也需要一次就绪确认

        // 输出目录
        if (string.IsNullOrEmpty(_outputDir))
            _outputDir = Path.Combine(Application.dataPath, "..", "AutoGamerOutput", "replay-" + System.DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_outputDir);
        _markerPath = Path.Combine(_outputDir, "done.json");
        _videoPath = Path.Combine(_outputDir, "replay.mp4");

        Log($"配置: {_sequencePath} ({(_seq?.events?.Length ?? 0)} 事件)");
        Log($"  相机: pos={_cameraPos} rot={_cameraRot} fov={_cameraFov}");
        if (!string.IsNullOrEmpty(_cameraDisableScript)) Log($"  禁用脚本: {_cameraDisableScript}");
        if (!string.IsNullOrEmpty(_gameSceneName)) Log($"  游戏主场景: {_gameSceneName}");
        Log($"  录制: enabled={_recEnabled} fps={_recFps} tail={_recTail}s ffmpeg={_ffmpegPath}");

        Application.runInBackground = true;

        SceneManager.sceneLoaded += OnSceneLoaded;
        HandleScene(SceneManager.GetActiveScene());   // 初始场景（sceneLoaded 事件此时已错过）
        StartCoroutine(BootReplay());                 // 唯一回放驱动，不依赖场景结构
    }

    void OnSceneLoaded(Scene s, LoadSceneMode mode)
    {
        HandleScene(s);
        _settled = false;      // 场景切换后重新等待就绪
        _cameraFrozen = false;
    }

    bool IsGameScene(string sceneName)
    {
        if (!string.IsNullOrEmpty(_gameSceneName)) return sceneName == _gameSceneName;
        // 无 header.map 时的回退约定：AG 录制序列默认主场景为 Scene01
        if (sceneName == "Scene01" || sceneName.Contains("Scene01") || sceneName.Contains("scene01"))
        {
            _gameSceneName = sceneName;
            return true;
        }
        return false;
    }

    void HandleScene(Scene s)
    {
        // 游戏主场景：每次加载（含重开局）都固定相机；用 handle 去重防止同一实例重复 setup
        if (IsGameScene(s.name) && s.handle != _lastSetupSceneHandle)
        {
            _lastSetupSceneHandle = s.handle;
            StartCoroutine(SetupGameScene());
        }
    }

    IEnumerator BootReplay()
    {
        // 等待启动场景（主菜单/游戏场景）渲染稳定
        yield return new WaitForSecondsRealtime(2f);

        // 窗口初始化（仅在显式要求时才做 overrideredirect 强制——
        // 窗口模式启动无需强制，且 overrideredirect 会让 WM 重新摆放窗口导致几何不稳）
        if (!_windowReady)
        {
            if (_forceWindowSet)
            {
                ForceWindowMode();
                yield return new WaitForSecondsRealtime(1f);
            }
            DetectWindowGeometry();
            _windowReady = true;
            yield return new WaitForSecondsRealtime(0.5f);
        }

        yield return RunReplay();
    }

    IEnumerator SetupGameScene()
    {
        // 等待场景初始化（几帧让场景完全加载）
        float t0 = Time.unscaledTime;
        while (Time.unscaledTime - t0 <= 1f) yield return null;

        SetupCamera();
        Log("游戏场景就绪，相机已固定: " + _gameSceneName);

        yield return new WaitForSecondsRealtime(0.5f);
    }

    void SetupCamera()
    {
        if (!string.IsNullOrEmpty(_cameraDisableScript))
        {
            var type = System.Type.GetType(_cameraDisableScript);
            if (type == null)
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                { type = asm.GetType(_cameraDisableScript); if (type != null) break; }
            if (type != null)
            {
                var comp = FindObjectOfType(type) as MonoBehaviour;
                if (comp != null) { comp.enabled = false; Log($"已禁用: {_cameraDisableScript}"); }
            }
        }

        var cam = Camera.main;
        if (cam == null) { var go = GameObject.Find("Main Camera"); cam = go != null ? go.GetComponent<Camera>() : null; }
        if (cam != null)
        {
            cam.transform.position = _cameraPos;
            cam.transform.rotation = Quaternion.Euler(_cameraRot);
            cam.fieldOfView = _cameraFov;
            _cameraFrozen = true;
            Log($"相机已固定: pos={_cameraPos} rot={_cameraRot} fov={_cameraFov}");
        }
        else
        {
            Log("警告: 找不到主相机，无法固定（点击世界坐标将不可复现）");
        }
    }

    IEnumerator RunReplay()
    {
        if (_seq == null || _seq.events == null || _seq.events.Length == 0) yield break;
        if (_replayStarted) yield break;
        _replayStarted = true;

        // 窗口已就绪且分辨率稳定，开始视频录制
        if (_recEnabled) StartCoroutine(RecordVideo());

        Log($"开始回放: {_seq.events.Length} 事件");
        float prevT = _seq.events[0].t;
        int executed = 0;

        foreach (var ev in _seq.events)
        {
            float dt = ev.t - prevT;
            if (dt > 0) yield return new WaitForSecondsRealtime(dt);
            prevT = ev.t;

            // 场景切换后等待新场景就绪（防止点击落在旧场景的按钮或未固定的相机上）
            if (!_settled) yield return WaitSceneSettled();

            Log($"事件 {ev.i}/{_seq.events.Length - 1}: {ev.op}{(string.IsNullOrEmpty(ev.meta) ? "" : " " + ev.meta)} ({ev.sx},{ev.sy})");

            switch (ev.op)
            {
                case "click":
                case "world_click":
                case "gui_click":
                case "tutorial_click":
                case "screen_click":
                case "replay_click":
                    yield return ClickAt(ev.sx, ev.sy, ev.button > 0 ? ev.button - 1 : 0);
                    break;
                case "right_click":
                    yield return ClickAt(ev.sx, ev.sy, 1);
                    break;
                case "mousemove":
                    XdoMove(ev.sx, ev.sy);
                    yield return null;
                    break;
                case "mousedown":
                    XdoMove(ev.sx, ev.sy);
                    yield return null;
                    XdoMouseButton(ev.button > 0 ? ev.button : 1, true);
                    break;
                case "mouseup":
                    XdoMouseButton(ev.button > 0 ? ev.button : 1, false);
                    break;
                case "key":
                case "key_press":
                    XdoKey(ev.key, true);
                    yield return null;
                    XdoKey(ev.key, false);
                    break;
                default:
                    // 未知 op 类型也尝试点击
                    yield return ClickAt(ev.sx, ev.sy, 0);
                    break;
            }
            executed++;
            yield return null;
        }

        Log($"回放完成: {executed}/{_seq.events.Length} 事件");

        // 录制结尾（捕捉结算画面）
        if (_recording) yield return new WaitForSecondsRealtime(_recTail);
        StopRecording();

        // 写结果
        try
        {
            var result = $"{{\"status\":\"complete\",\"executed\":{executed},\"total\":{_seq.events.Length},\"duration_s\":{Time.unscaledTime - _bootTime:F1},\"video\":\"{_videoPath.Replace("\\", "\\\\")}\",\"frames\":{_recFrames}}}";
            File.WriteAllText(_markerPath, result);
            Log($"结果已写入: {_markerPath}");
        }
        catch (System.Exception e) { Log("写入结果失败: " + e.Message); }

        if (_quitOnEnd)
        {
            yield return new WaitForSecondsRealtime(2f);
            Log("退出运行");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }

    // ---- 场景就绪屏障 ----

    IEnumerator WaitSceneSettled()
    {
        float t0 = Time.unscaledTime;

        // 游戏主场景：等待相机固定（SetupGameScene 在场景加载后执行），再留缓冲
        bool isGameScene = !string.IsNullOrEmpty(_gameSceneName) && SceneManager.GetActiveScene().name == _gameSceneName;
        if (isGameScene)
        {
            while (!_cameraFrozen && Time.unscaledTime - t0 < 15f) yield return null;
            yield return new WaitForSecondsRealtime(0.3f);
            Log("场景就绪: " + SceneManager.GetActiveScene().name + (_cameraFrozen ? " (相机已固定)" : " (相机固定超时)"));
        }
        else
        {
            // 非游戏场景（主菜单等）：短缓冲即可
            yield return new WaitForSecondsRealtime(0.3f);
        }
        _settled = true;
    }

    // ---- 视频录制（屏幕捕获 → ffmpeg 管道） ----

    IEnumerator RecordVideo()
    {
        _recording = true;
        _recNext = Time.unscaledTime;

        while (_recording)
        {
            yield return new WaitForEndOfFrame();

            if (!_recording) break;
            if (Time.unscaledTime < _recNext) continue;
            _recNext = Time.unscaledTime + 1f / _recFps;

            if (_recTex == null)
            {
                // 首帧确定分辨率（此后保持不变，ffmpeg rawvideo 需要固定尺寸）
                _recW = Screen.width;
                _recH = Screen.height;
                _recTex = new Texture2D(_recW, _recH, TextureFormat.RGBA32, false);
                _recBuf = new byte[_recW * _recH * 4];
                if (!StartFfmpeg()) { _recording = false; yield break; }
            }

            if (_recTex.width != Screen.width || _recTex.height != Screen.height)
            {
                if (!_recSizeWarned) { Log($"警告: 屏幕尺寸变化 {_recW}x{_recH} → {Screen.width}x{Screen.height}，跳帧"); _recSizeWarned = true; }
                continue;
            }

            try
            {
                _recTex.ReadPixels(new Rect(0, 0, _recW, _recH), 0, 0, false);
                var raw = _recTex.GetRawTextureData<byte>();
                raw.CopyTo(_recBuf);
                _ffmpegProc.StandardInput.BaseStream.Write(_recBuf, 0, _recBuf.Length);
                _ffmpegProc.StandardInput.BaseStream.Flush();
                _recFrames++;
            }
            catch (System.Exception e)
            {
                Log("ffmpeg 写入失败，停止录制: " + e.Message);
                StopRecording();
                yield break;
            }
        }
    }

    bool StartFfmpeg()
    {
        try
        {
            // ReadPixels 读到的像素自下而上（纹理原点在左下），需要 vflip 翻转为视频行序
            var psi = new System.Diagnostics.ProcessStartInfo(_ffmpegPath,
                $"-y -f rawvideo -vcodec rawvideo -pixel_format rgba " +
                $"-video_size {_recW}x{_recH} -framerate {_recFps} -loglevel error -i - " +
                $"-vf vflip -c:v libx264 -preset veryfast -pix_fmt yuv420p " +
                $"-crf 23 \"{_videoPath}\"")
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            _ffmpegProc = new System.Diagnostics.Process { StartInfo = psi };
            _ffmpegProc.OutputDataReceived += (s, e) => { };
            _ffmpegProc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.LogWarning("[Replayer][ffmpeg] " + e.Data); };
            _ffmpegProc.Start();
            _ffmpegProc.BeginOutputReadLine();
            _ffmpegProc.BeginErrorReadLine();
            Log($"录制开始: {_videoPath} ({_recW}x{_recH}@{_recFps})");
            return true;
        }
        catch (System.Exception e)
        {
            Log("ffmpeg 启动失败（禁用录像继续回放）: " + e.Message);
            return false;
        }
    }

    void StopRecording()
    {
        if (!_recording) return;
        _recording = false;

        try
        {
            if (_ffmpegProc != null && !_ffmpegProc.HasExited)
            {
                _ffmpegProc.StandardInput.Close();
                if (!_ffmpegProc.WaitForExit(10000)) _ffmpegProc.Kill();
            }
            Log($"录制完成: {_videoPath} ({_recFrames} 帧)");
        }
        catch (System.Exception e) { Log("停止录制失败: " + e.Message); }
    }

    void OnDestroy()
    {
        StopRecording();
        if (_recTex != null) Destroy(_recTex);
    }

    void OnApplicationQuit()
    {
        StopRecording();
    }

    // ---- 点击协程 ----

    IEnumerator ClickAt(int unityX, int unityY, int button)
    {
        if (!_windowReady)
        {
            if (_forceWindowSet)
            {
                ForceWindowMode();
                yield return new WaitForSecondsRealtime(1f);
            }
            DetectWindowGeometry();
            _windowReady = true;
        }

        // 每次点击前刷新窗口几何（防止窗口被 WM 移动导致点击错位）
        DetectWindowGeometry();

        // 1. 移动光标
        XdoMove(unityX, unityY);
        yield return null;

        // 2. 点击
        XdoClick(unityX, unityY, button);
        yield return null;
        yield return null;
        yield return null;
    }

    // ---- 窗口管理 ----

    void ForceWindowMode()
    {
        var wid = FindGameWindow();
        if (string.IsNullOrEmpty(wid)) { Log("警告: 无法获取窗口 ID"); return; }

        // overrideredirect + windowsize + 移到 (0,0)（取消 WM 装饰；仅全屏兜底用，窗口模式勿用）
        RunBash($"xdotool set_window --overrideredirect 1 {wid}; xdotool windowsize {wid} {_forceW} {_forceH}; xdotool windowmove {wid} 0 0");
        System.Threading.Thread.Sleep(1000);

        Log($"强制窗口模式: {wid} → {_forceW}x{_forceH}");
    }

    void DetectWindowGeometry()
    {
        if (string.IsNullOrEmpty(_windowWid)) _windowWid = FindGameWindow();
        if (string.IsNullOrEmpty(_windowWid)) { Log("警告: 无法获取窗口几何"); return; }

        // 关键：xdotool getwindowgeometry 在带 WM 装饰的窗口上返回外框(frame)坐标，
        // 而点击坐标必须相对客户区(client)——差一个标题栏高度会让所有点击整体下移，
        // 误点到别的按钮（实测 xfwm4：外框 (10,85) vs 客户区 (5,56)，_NET_FRAME_EXTENTS=5,5,29,5）。
        // 优先 xwininfo（客户区绝对坐标）；后备 xdotool + _NET_FRAME_EXTENTS 修正；
        // 再后备 xdotool 裸值（仅对无装饰/override-redirect 窗口准确）。

        int gx = -1, gy = -1, gw = -1, gh = -1;

        var xwi = RunBash($"xwininfo -id {_windowWid} 2>/dev/null");
        if (!string.IsNullOrEmpty(xwi))
        {
            foreach (var line in xwi.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("Absolute upper-left X:")) int.TryParse(t.Substring(t.IndexOf(':') + 1).Trim(), out gx);
                else if (t.StartsWith("Absolute upper-left Y:")) int.TryParse(t.Substring(t.IndexOf(':') + 1).Trim(), out gy);
                else if (t.StartsWith("Width:")) int.TryParse(t.Substring(t.IndexOf(':') + 1).Trim(), out gw);
                else if (t.StartsWith("Height:")) int.TryParse(t.Substring(t.IndexOf(':') + 1).Trim(), out gh);
            }
        }

        if (gx < 0 || gy < 0 || gw <= 0 || gh <= 0)
        {
            var geo = RunBash($"xdotool getwindowgeometry --shell {_windowWid} 2>/dev/null");
            if (!string.IsNullOrEmpty(geo))
            {
                foreach (var line in geo.Split('\n'))
                {
                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    if (int.TryParse(parts[1].Trim(), out int v))
                    {
                        var key = parts[0].Trim();
                        if (key == "X") gx = v;
                        else if (key == "Y") gy = v;
                        else if (key == "WIDTH") gw = v;
                        else if (key == "HEIGHT") gh = v;
                    }
                }
                // _NET_FRAME_EXTENTS = left, right, top, bottom → 修正回客户区坐标
                var ext = RunBash($"xprop -id {_windowWid} _NET_FRAME_EXTENTS 2>/dev/null");
                if (!string.IsNullOrEmpty(ext) && ext.Contains("="))
                {
                    var nums = ext.Substring(ext.IndexOf('=') + 1).Split(',');
                    int fl, ft;
                    if (nums.Length == 4 && int.TryParse(nums[0].Trim(), out fl) && int.TryParse(nums[2].Trim(), out ft))
                    {
                        gx -= fl; gy -= ft;
                    }
                }
            }
        }

        if (gx < 0 || gy < 0 || gw <= 0 || gh <= 0) { _windowWid = null; return; }

        // 几何稳定时少刷日志，仅变化时打印
        if (gx != _gvX || gy != _gvY || gw != _gvW || gh != _gvH)
        {
            _gvX = gx; _gvY = gy; _gvW = gw; _gvH = gh;
            Log($"窗口几何(客户区): ({_gvX},{_gvY}) {_gvW}x{_gvH} Screen={Screen.width}x{Screen.height}");
        }
        else
        {
            _gvX = gx; _gvY = gy; _gvW = gw; _gvH = gh;
        }
    }

    string FindGameWindow()
    {
        string title = !string.IsNullOrEmpty(_windowTitle) ? _windowTitle : Application.productName;
        if (string.IsNullOrEmpty(title)) title = "Unity";
        title = title.Replace("'", "");   // bash 单引号安全
        for (int i = 0; i < 10; i++)
        {
            var result = RunBash($"xdotool search --onlyvisible --name \"{title}\" 2>/dev/null | head -1");
            if (!string.IsNullOrEmpty(result?.Trim()))
            {
                Log($"找到窗口: {result.Trim()} (标题={title}, 尝试={i+1})");
                return result.Trim();
            }
            System.Threading.Thread.Sleep(500);
        }
        Log($"警告: 找不到标题为 '{title}' 的窗口");
        return null;
    }

    // ---- 坐标转换（Unity Screen 左下原点 → X11 左上原点，相对客户区） ----

    (int x, int y) ToX11(int unityX, int unityY)
    {
        float scaleX = _gvW > 0 && Screen.width > 0 ? (float)_gvW / Screen.width : 1f;
        float scaleY = _gvH > 0 && Screen.height > 0 ? (float)_gvH / Screen.height : 1f;
        int absX = _gvX + (int)(unityX * scaleX);
        int absY = _gvY + (int)((Screen.height - unityY) * scaleY);
        return (absX, absY);
    }

    // ---- xdotool 注入 ----

    void XdoClick(int unityX, int unityY, int button)
    {
        var (absX, absY) = ToX11(unityX, unityY);
        int xdoBtn = button == 0 ? 1 : (button == 1 ? 3 : 2);
        RunBash($"xdotool mousemove {absX} {absY} mousedown {xdoBtn} mouseup {xdoBtn}", 500);
    }

    void XdoMove(int unityX, int unityY)
    {
        var (absX, absY) = ToX11(unityX, unityY);
        RunBash($"xdotool mousemove {absX} {absY}", 500);
    }

    void XdoMouseButton(int button, bool down)
    {
        int xdoBtn = button == 1 ? 1 : (button == 3 ? 3 : 2);
        string action = down ? "mousedown" : "mouseup";
        RunBash($"xdotool {action} {xdoBtn}", 500);
    }

    void XdoKey(string key, bool down)
    {
        string action = down ? "keydown" : "keyup";
        RunBash($"xdotool {action} {key}", 500);
    }

    // ---- 工具 ----

    static string RunBash(string script, int timeoutMs = 3000)
    {
        try
        {
            var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo("/bin/bash", $"-c '{script}'")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            p.Start();
            p.WaitForExit(timeoutMs);
            return p.StandardOutput.ReadToEnd();
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogWarning("[Replayer] bash 失败: " + e.Message);
            return null;
        }
    }

    static void Log(string msg)
    {
        UnityEngine.Debug.Log("[Replayer] " + msg);
    }
}
