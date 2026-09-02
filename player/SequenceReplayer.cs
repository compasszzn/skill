using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 通用键鼠序列回放器 — 放入任意 Unity 游戏工程即可使用（不改游戏原始代码）
///
/// 完全复制 AGSequenceReplayer + AGBootstrap 的 setup 流程：
///   1. Application.runInBackground = true
///   2. Screen.SetResolution + 窗口强制（overrideredirect+windowsize）
///   3. 等待 MainMenu 场景 → AutoClickPlay（xdotool 点击 Play 按钮）
///   4. 等待 Scene01 加载 → FreezeCamera（固定相机到录制时位置）+ 视频录制
///   5. 逐事件按时间戳间隔 + 屏幕坐标用 xdotool 重放
///   6. 回放完成后写结果 → 退出
///
/// 启动方式（命令行或环境变量）：
///   -sequence /path/to/sequence.json       序列文件路径
///   -screen-width 800 -screen-height 600    窗口分辨率（必须与录制时一致）
///   -camera-pos 7.5,20,8.5                  相机位置（必须与录制时一致）
///   -camera-rot 90,0,0                      相机旋转（必须与录制时一致）
///   -camera-fov 60                          相机 FOV（必须与录制时一致）
///   -camera-disable-script StrategyCamera   要禁用的相机控制脚本名（可选）
///   -replay-force-window 800x600            强制窗口模式大小（解决全屏问题）
///   -replay-output-dir /path/to/output      输出目录（可选）
///   -replay-quit-on-end true               回放结束后退出进程（默认 true）
///
/// 或环境变量：
///   REPLAY_SEQUENCE=/path/to/sequence.json
///   REPLAY_CAMERA_POS=7.5,20,8.5
///   REPLAY_CAMERA_ROT=90,0,0
///   REPLAY_CAMERA_FOV=60
///   REPLAY_CAMERA_DISABLE_SCRIPT=StrategyCamera
///   REPLAY_FORCE_WINDOW=800x600
///   REPLAY_OUTPUT_DIR=/path/to/output
///   REPLAY_QUIT_ON_END=true
///
/// 序列文件格式（JSON，JsonUtility 兼容）：
/// {
///   "events": [
///     {"i":0, "frame":1, "t":2.8, "op":"click", "sx":400, "sy":323, "button":1, "wait":""}
///   ]
/// }
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
    }

    [System.Serializable]
    public class SeqFile
    {
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

    // 运行时
    SeqFile _seq;
    int _gvX, _gvY, _gvW, _gvH;
    bool _windowReady;
    bool _scene01Seen;
    string _markerPath;
    float _bootTime;

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

        var fw = GetArg("-replay-force-window") ?? GetEnv("REPLAY_FORCE_WINDOW");
        if (!string.IsNullOrEmpty(fw))
        {
            var parts = fw.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out _forceW) && int.TryParse(parts[1], out _forceH)) { }
        }

        if (string.IsNullOrEmpty(_windowTitle)) _windowTitle = Application.productName;

        // 加载序列
        var json = File.ReadAllText(_sequencePath);
        _seq = JsonUtility.FromJson<SeqFile>(json);

        // 输出目录
        if (string.IsNullOrEmpty(_outputDir))
            _outputDir = Path.Combine(Application.dataPath, "..", "AutoGamerOutput", "replay-" + System.DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_outputDir);
        _markerPath = Path.Combine(_outputDir, "done.json");

        Log($"配置: {_sequencePath} ({(_seq?.events?.Length ?? 0)} 事件)");
        Log($"  相机: pos={_cameraPos} rot={_cameraRot} fov={_cameraFov}");
        if (!string.IsNullOrEmpty(_cameraDisableScript)) Log($"  禁用脚本: {_cameraDisableScript}");

        Application.runInBackground = true;

        SceneManager.sceneLoaded += OnSceneLoaded;
        HandleScene(SceneManager.GetActiveScene().name);
    }

    void OnSceneLoaded(Scene s, LoadSceneMode mode) { HandleScene(s.name); }

    void HandleScene(string sceneName)
    {
        // 匹配 AGBootstrap：MainMenu → AutoClickPlay；Scene01 → SetupScene01
        if (sceneName == "Scene01" || sceneName.Contains("Scene01") || sceneName.Contains("scene01"))
        {
            if (_scene01Seen) { Log("Scene01 再次加载：忽略"); return; }
            _scene01Seen = true;
            StartCoroutine(SetupScene01());
        }
        else
        {
            // 任何非 Scene01 的场景 → 尝试点击 Play
            StartCoroutine(AutoClickPlay());
        }
    }

    IEnumerator AutoClickPlay()
    {
        // 等待 MainMenu 的 OnGUI 渲染（至少 2 帧）
        yield return new WaitForSecondsRealtime(2f);

        // 窗口强制（第一次调用时初始化 xdotool + overrideredirect）
        if (!_windowReady)
        {
            ForceWindowMode();
            yield return new WaitForSecondsRealtime(1f);
            DetectWindowGeometry();
            _windowReady = true;
            yield return new WaitForSecondsRealtime(1f);
        }

        // 直接开始回放（序列第一个事件就是 Play 按钮点击）
        // 不自己点击 Play → 由 RunReplay 按序列坐标点击
        Log("MainMenu 就绪，等待序列回放");
        yield return RunReplay();
    }

    IEnumerator SetupScene01()
    {
        // 等待场景初始化
        float t0 = Time.unscaledTime;
        while (true)
        {
            if (Time.unscaledTime - t0 > 30f) { Log("Scene01 初始化超时"); yield break; }
            yield return null;
            // 等待几帧让场景完全加载
            if (Time.unscaledTime - t0 > 1f) break;
        }

        // 固定相机
        SetupCamera();
        Log("Scene01 就绪，相机已固定");

        // 相机已固定，等待回放继续（RunReplay 已在 AutoClickPlay 中启动）
        yield return new WaitForSecondsRealtime(1f);
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
            Log($"相机已固定: pos={_cameraPos} rot={_cameraRot} fov={_cameraFov}");
        }
    }

    IEnumerator RunReplay()
    {
        if (_seq == null || _seq.events == null || _seq.events.Length == 0) yield break;

        Log($"开始回放: {_seq.events.Length} 事件");
        float prevT = _seq.events[0].t;
        int executed = 0;

        foreach (var ev in _seq.events)
        {
            float dt = ev.t - prevT;
            if (dt > 0) yield return new WaitForSecondsRealtime(dt);
            prevT = ev.t;

            switch (ev.op)
            {
                case "click":
                case "world_click":
                case "gui_click":
                case "tutorial_click":
                case "screen_click":
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
                case "replay_click":
                    yield return ClickAt(ev.sx, ev.sy, ev.button > 0 ? ev.button - 1 : 0);
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

        // 写结果
        try
        {
            var result = $"{{\"status\":\"complete\",\"executed\":{executed},\"total\":{_seq.events.Length},\"duration_s\":{Time.unscaledTime - _bootTime:F1}}}";
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

    // ---- 点击协程（完全复制 AGInputInjector.ClickAt） ----

    IEnumerator ClickAt(int unityX, int unityY, int button)
    {
        if (!_windowReady)
        {
            ForceWindowMode();
            yield return new WaitForSecondsRealtime(1f);
            DetectWindowGeometry();
            _windowReady = true;
        }

        // 1. 移动光标
        XdoMove(unityX, unityY);
        yield return null;

        // 2. 点击
        XdoClick(unityX, unityY, button);
        yield return null;
        yield return null;
        yield return null;
    }

    // ---- 窗口管理（完全复制 AGInputInjector.DetectGameViewPosition） ----

    void ForceWindowMode()
    {
        var wid = FindGameWindow();
        if (string.IsNullOrEmpty(wid)) { Log("警告: 无法获取窗口 ID"); return; }

        // overrideredirect + windowsize（取消窗口管理器装饰 + 调整大小）
        RunBash($"xdotool set_window --overrideredirect 1 {wid}; xdotool windowsize {wid} {_forceW} {_forceH}");
        System.Threading.Thread.Sleep(1000);

        Log($"强制窗口模式: {wid} → {_forceW}x{_forceH}");
    }

    void DetectWindowGeometry()
    {
        var wid = FindGameWindow();
        if (string.IsNullOrEmpty(wid)) { Log("警告: 无法获取窗口几何"); return; }

        var geo = RunBash($"xdotool getwindowgeometry --shell {wid}");
        if (string.IsNullOrEmpty(geo)) return;

        foreach (var line in geo.Split('\n'))
        {
            var parts = line.Split('=');
            if (parts.Length != 2) continue;
            if (int.TryParse(parts[1].Trim(), out int v))
            {
                var key = parts[0].Trim();
                if (key == "X") _gvX = v;
                else if (key == "Y") _gvY = v;
                else if (key == "WIDTH") _gvW = v;
                else if (key == "HEIGHT") _gvH = v;
            }
        }
        _gvW = _gvW > 0 ? _gvW : Screen.width;
        _gvH = _gvH > 0 ? _gvH : Screen.height;
        Log($"窗口几何: ({_gvX},{_gvY}) {_gvW}x{_gvH} Screen={Screen.width}x{Screen.height}");
    }

    string FindGameWindow()
    {
        string title = !string.IsNullOrEmpty(_windowTitle) ? _windowTitle : Application.productName;
        if (string.IsNullOrEmpty(title)) title = "Unity";
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

    // ---- 坐标转换（完全复制 AGInputInjector.ToScreenCoord） ----

    (int x, int y) ToX11(int unityX, int unityY)
    {
        float scaleX = _gvW > 0 && Screen.width > 0 ? (float)_gvW / Screen.width : 1f;
        float scaleY = _gvH > 0 && Screen.height > 0 ? (float)_gvH / Screen.height : 1f;
        int absX = _gvX + (int)(unityX * scaleX);
        int absY = _gvY + (int)((Screen.height - unityY) * scaleY);
        return (absX, absY);
    }

    // ---- xdotool 注入（完全复制 AGInputInjector.XdoClick/XdoMove） ----

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
