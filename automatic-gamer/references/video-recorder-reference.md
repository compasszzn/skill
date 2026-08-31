# 视频录制器实现参考

> 步骤3编写 bot 代码时，视频录制器模块的实现参考。使用 Camera + ffmpeg pipe 方式录制完整单局视频。

---

## 实现方式：Camera + ffmpeg pipe

### 核心原理

- 创建专用 Camera（跟随游戏主 Camera 或固定俯视角）
- Camera 渲染到 RenderTexture
- 每帧读取 RenderTexture 原始像素（RGBA）
- 将 RGBA 原始数据 pipe 到 ffmpeg 子进程的 stdin
- ffmpeg 实时编码为 mp4，不做中间 PNG

### ffmpeg 启动命令

```
ffmpeg -y -f rawvideo -vcodec rawvideo -pixel_format rgba
  -colorspace bt709 -video_size {width}x{height}
  -framerate {fps} -loglevel warning -i -
  -c:v libx264 -pix_fmt yuv420p -crf 23 {outputPath}
```

### 关键代码结构

```csharp
public class VideoRecorder : MonoBehaviour
{
    private Camera recordCamera;
    private RenderTexture renderTexture;
    private Texture2D captureTexture;
    private Process ffmpegProcess;
    private bool isRecording;

    // 命令行参数
    private bool recordVideo = true;   // -record-video
    private int videoFps = 10;         // -video-fps
    private int videoWidth = 512;      // -video-resolution width
    private int videoHeight = 512;     // -video-resolution height

    public void StartRecording(string outputPath)
    {
        // 1. 创建 RenderTexture
        renderTexture = new RenderTexture(videoWidth, videoHeight, 24);
        recordCamera.targetTexture = renderTexture;

        // 2. 创建 Texture2D 用于读取像素
        captureTexture = new Texture2D(videoWidth, videoHeight, TextureFormat.RGBA32, false);

        // 3. 启动 ffmpeg 子进程
        ffmpegProcess = new Process();
        ffmpegProcess.StartInfo.FileName = "ffmpeg";
        ffmpegProcess.StartInfo.Arguments = BuildFfmpegArgs(outputPath);
        ffmpegProcess.StartInfo.UseShellExecute = false;
        ffmpegProcess.StartInfo.RedirectStandardInput = true;
        ffmpegProcess.StartInfo.RedirectStandardError = true;
        ffmpegProcess.Start();

        isRecording = true;
    }

    public void CaptureFrame()
    {
        if (!isRecording) return;

        // 1. 渲染 Camera 到 RenderTexture
        recordCamera.Render();

        // 2. 从 RenderTexture 读取像素到 Texture2D
        RenderTexture.active = renderTexture;
        captureTexture.ReadPixels(new Rect(0, 0, videoWidth, videoHeight), 0, 0);
        captureTexture.Apply();
        RenderTexture.active = null;

        // 3. 获取原始 RGBA 数据并写入 ffmpeg stdin
        byte[] rawBytes = captureTexture.GetRawTextureData();
        ffmpegProcess.StandardInput.BaseStream.Write(rawBytes, 0, rawBytes.Length);
    }

    public void StopRecording()
    {
        isRecording = false;

        // 关闭 ffmpeg stdin，等待编码完成
        ffmpegProcess.StandardInput.BaseStream.Close();
        ffmpegProcess.WaitForExit(30000);  // 最多等30秒

        // 清理资源
        Destroy(renderTexture);
        Destroy(captureTexture);
    }

    private string BuildFfmpegArgs(string outputPath)
    {
        return $"-y -f rawvideo -vcodec rawvideo -pixel_format rgba " +
               $"-colorspace bt709 -video_size {videoWidth}x{videoHeight} " +
               $"-framerate {videoFps} -loglevel warning -i - " +
               $"-c:v libx264 -pix_fmt yuv420p -crf 23 \"{outputPath}\"";
    }
}
```

### 录制触发时机

- `StartRecording()`：Auto 模式开启时调用
- `CaptureFrame()`：每帧 Update 中调用（按 fps 间隔控制）
- `StopRecording()`：单局结束时调用（通关/死亡/时间限制）

### 产出文件

| 文件 | 说明 |
|------|------|
| `recording.mp4` | 完整单局视频 |
| `frame_data.json` | 每帧时间戳、bot 位置、关键事件标记 |

### 命令行参数

| 参数 | 说明 | 默认值 |
|------|------|--------|
| `record-video` | 是否录制视频 | true |
| `video-fps` | 帧率 | 10 |
| `video-resolution` | 分辨率（WxH） | 512x512 |

### ffmpeg 依赖

ffmpeg 必须在系统 PATH 中可用。如果不可用：
- macOS: `brew install ffmpeg`
- Linux (Debian/Ubuntu): `sudo apt install ffmpeg`
- Linux (RHEL/CentOS): `sudo yum install ffmpeg`

run_sweep 脚本启动时应检查 ffmpeg 是否可用并自动安装。

### batch mode 适配

Unity `-batchmode` 下 Camera 仍可渲染到 RenderTexture（Unity 允许无窗口渲染）。ffmpeg 子进程独立运行不受 batch mode 影响。

### frame_data.json 结构

```json
{
  "frames": [
    {
      "frame_index": 0,
      "timestamp_ms": 0,
      "bot_position": {"x": 0, "y": 0},
      "key_event": "level_start"
    },
    {
      "frame_index": 100,
      "timestamp_ms": 10000,
      "bot_position": {"x": 12.3, "y": -5.7},
      "key_event": null
    },
    {
      "frame_index": 970,
      "timestamp_ms": 97000,
      "bot_position": {"x": 45, "y": 30},
      "key_event": "level_end"
    }
  ]
}
```

只在关键事件发生时记录 key_event，大多数帧的 key_event 为 null。
