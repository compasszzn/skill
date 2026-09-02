# Sequence Replay Skill — 通用键鼠序列回放驱动指南

## 适用场景

你有一个游戏的键鼠操作序列文件（`sequence.json`），需要让一个**原版游戏（无内挂代码）**完全根据这个序列自动运行。不需要修改游戏源码、不需要注入 DLL、不需要 hook 游戏函数——只需要一个独立的外部驱动程序。

## 核心原理

**序列文件记录的是人类可复现的键鼠操作（屏幕坐标 + 按键 + 时序），驱动程序逐条重放这些操作，与人类按照录屏轨迹手动操作完全等价。**

序列不依赖任何游戏内部对象引用、InstanceID、内存地址——只依赖：
- 屏幕坐标 `(sx, sy)`
- 操作类型（鼠标移动/点击/按键）
- 时序（帧索引或时间戳）
- 前置条件（可选：等待某个状态出现再执行下一步）

## 前提条件

1. **游戏窗口必须可见且可交互**（非最小化、非被遮挡）
2. **游戏窗口位置和分辨率必须与录制时一致**（或驱动程序能自动检测并适配）
3. **游戏必须是确定性的**（相同输入 → 相同结果；无 RNG 或 RNG 已固定种子）
4. **序列必须完整**（从游戏启动到结束的全部操作，包括菜单点击、加载等待）

---

## 序列文件格式（通用 schema）

```json
{
  "header": {
    "game": "游戏名称",
    "resolution": "录制时的 Screen.width x Screen.height",
    "created": "ISO 时间戳",
    "fps": 60
  },
  "events": [
    {
      "i": 0,
      "frame": 1,
      "t": 0.0,
      "op": "mousemove",
      "sx": 512,
      "sy": 384
    },
    {
      "i": 1,
      "frame": 3,
      "t": 0.05,
      "op": "mousedown",
      "button": 1,
      "sx": 512,
      "sy": 384
    },
    {
      "i": 2,
      "frame": 4,
      "t": 0.07,
      "op": "mouseup",
      "button": 1,
      "sx": 512,
      "sy": 384
    },
    {
      "i": 3,
      "frame": 120,
      "t": 2.0,
      "op": "keydown",
      "key": "Return"
    },
    {
      "i": 4,
      "frame": 122,
      "t": 2.03,
      "op": "keyup",
      "key": "Return"
    }
  ]
}
```

### 字段说明

| 字段 | 类型 | 必填 | 说明 |
|------|------|:---:|------|
| `i` | int | ✅ | 事件序号 |
| `frame` | int | ✅ | 帧索引（录制时的 `Time.frameCount`，回放时序参考） |
| `t` | float | ✅ | 时间戳（录制时的 `Time.unscaledTime`，秒） |
| `op` | string | ✅ | 操作类型：`mousemove` / `mousedown` / `mouseup` / `keydown` / `keyup` / `key` / `scroll` |
| `sx` | int | mouse* | 屏幕 X 坐标（左下角原点，与 Unity Screen 一致） |
| `sy` | int | mouse* | 屏幕 Y 坐标（左下角原点，Y 向上） |
| `button` | int | mouse* | 鼠标按钮：1=左, 2=中, 3=右 |
| `key` | string | key* | 键键名（X11 keysym 名称，如 `Return` / `Escape` / `a`） |
| `amount` | float | scroll | 滚轮量（正=上，负=下） |
| `wait` | string | ❌ | 前置等待条件 id（可选，见下文） |

### 坐标系约定

序列中的 `sx, sy` 使用 **Unity Screen 坐标系**（左下角原点，Y 向上）。驱动程序在回放时负责将其转换为 OS 屏幕坐标（左上角原点，Y 向下）。

---

## 回放驱动实现

### 方案 A：Python 外部驱动（推荐，零侵入）

Python 脚本在游戏进程外部运行，用 `xdotool`（Linux）或 `pyautogui`（跨平台）注入 OS 级鼠标/键盘事件。游戏不需要任何修改。

#### 核心代码

```python
#!/usr/bin/env python3
"""replay.py — 通用键鼠序列回放驱动

用法：
  python3 replay.py --sequence sequence.json --window "Game Title"
  python3 replay.py --sequence sequence.json --executable ./game --window "Game Title"
"""

import argparse
import json
import os
import subprocess
import sys
import time


def find_window(title):
    """查找游戏窗口 ID"""
    result = subprocess.run(
        ["xdotool", "search", "--onlyvisible", "--name", title],
        capture_output=True, text=True
    )
    wids = result.stdout.strip().split("\n")
    return wids[0] if wids and wids[0] else None


def get_window_geometry(wid):
    """获取窗口在屏幕上的位置和大小"""
    result = subprocess.run(
        ["xdotool", "getwindowgeometry", "--shell", wid],
        capture_output=True, text=True
    )
    geo = {}
    for line in result.stdout.strip().split("\n"):
        if "=" in line:
            k, v = line.split("=", 1)
            geo[k.strip()] = int(v.strip())
    return geo


def force_windowed(wid, width, height):
    """强制窗口模式（解决全屏不接收 X11 事件问题）"""
    subprocess.run(
        ["xdotool", "set_window", "--overrideredirect", "1", wid],
        capture_output=True
    )
    subprocess.run(
        ["xdotool", "windowsize", wid, str(width), str(height)],
        capture_output=True
    )
    time.sleep(0.5)


def unity_to_x11(sx, sy, screen_w, screen_h, win_x, win_y, win_w, win_h):
    """Unity Screen 坐标 → X11 屏幕绝对坐标"""
    scale_x = win_w / screen_w if screen_w > 0 else 1.0
    scale_y = win_h / screen_h if screen_h > 0 else 1.0
    abs_x = win_x + int(sx * scale_x)
    abs_y = win_y + int((screen_h - sy) * scale_y)  # Y 轴翻转
    return abs_x, abs_y


def do_mousemove(abs_x, abs_y):
    subprocess.run(["xdotool", "mousemove", str(abs_x), str(abs_y)], capture_output=True)


def do_mousedown(button):
    xdo_btn = {1: 1, 2: 2, 3: 3}[button]  # Unity: 1=左 2=中 3=右 → xdotool 同
    subprocess.run(["xdotool", "mousedown", str(xdo_btn)], capture_output=True)


def do_mouseup(button):
    xdo_btn = {1: 1, 2: 2, 3: 3}[button]
    subprocess.run(["xdotool", "mouseup", str(xdo_btn)], capture_output=True)


def do_keydown(key):
    subprocess.run(["xdotool", "keydown", key], capture_output=True)


def do_keyup(key):
    subprocess.run(["xdotool", "keyup", key], capture_output=True)


def do_scroll(amount):
    if amount > 0:
        subprocess.run(["xdotool", "click", "4"], capture_output=True)  # wheel up
    else:
        subprocess.run(["xdotool", "click", "5"], capture_output=True)  # wheel down


def replay(sequence_path, window_title, executable=None, force_window_size=None):
    # 加载序列
    with open(sequence_path) as f:
        seq = json.load(f)

    header = seq.get("header", {})
    events = seq["events"]
    rec_screen_w = header.get("screen_width", 800)
    rec_screen_h = header.get("screen_height", 600)

    print(f"序列: {len(events)} 事件, 录制分辨率 {rec_screen_w}x{rec_screen_h}")

    # 启动游戏（如果指定了可执行文件）
    if executable:
        print(f"启动游戏: {executable}")
        subprocess.Popen([executable])
        time.sleep(3)  # 等待窗口出现

    # 查找游戏窗口
    wid = None
    for _ in range(30):  # 最多等 30 秒
        wid = find_window(window_title)
        if wid:
            break
        time.sleep(1)

    if not wid:
        print(f"找不到窗口: {window_title}")
        sys.exit(1)

    print(f"游戏窗口: {wid}")

    # 强制窗口模式（如果需要）
    if force_window_size:
        w, h = force_window_size
        force_windowed(wid, w, h)

    # 获取窗口几何
    geo = get_window_geometry(wid)
    win_x = geo.get("X", 0)
    win_y = geo.get("Y", 0)
    win_w = geo.get("WIDTH", rec_screen_w)
    win_h = geo.get("HEIGHT", rec_screen_h)
    print(f"窗口位置: ({win_x}, {win_y}) 大小: {win_w}x{win_h}")

    # 激活窗口
    subprocess.run(["xdotool", "windowactivate", wid], capture_output=True)
    time.sleep(0.5)

    # 逐事件回放
    print("开始回放...")
    prev_t = events[0]["t"] if events else 0
    for ev in events:
        # 时序控制：按时间戳间隔等待
        dt = ev["t"] - prev_t
        if dt > 0:
            time.sleep(dt)
        prev_t = ev["t"]

        op = ev["op"]

        if op in ("mousemove", "mousedown", "mouseup", "click"):
            sx = ev["sx"]
            sy = ev["sy"]
            abs_x, abs_y = unity_to_x11(
                sx, sy, rec_screen_w, rec_screen_h,
                win_x, win_y, win_w, win_h
            )

            if op == "mousemove":
                do_mousemove(abs_x, abs_y)
            elif op == "mousedown":
                do_mousemove(abs_x, abs_y)
                time.sleep(0.02)
                do_mousedown(ev["button"])
            elif op == "mouseup":
                do_mouseup(ev["button"])
            elif op == "click":
                do_mousemove(abs_x, abs_y)
                time.sleep(0.02)
                btn = ev.get("button", 1)
                do_mousedown(btn)
                time.sleep(0.03)
                do_mouseup(btn)

        elif op == "keydown":
            do_keydown(ev["key"])
        elif op == "keyup":
            do_keyup(ev["key"])
        elif op == "key":
            do_keydown(ev["key"])
            time.sleep(0.05)
            do_keyup(ev["key"])
        elif op == "scroll":
            do_scroll(ev["amount"])

        # 每个操作后短暂让出 CPU
        time.sleep(0.01)

    print("回放完成")


def main():
    ap = argparse.ArgumentParser(description="通用键鼠序列回放驱动")
    ap.add_argument("--sequence", required=True, help="序列文件路径")
    ap.add_argument("--window", required=True, help="游戏窗口标题（用于查找窗口）")
    ap.add_argument("--executable", default=None, help="游戏可执行文件路径（自动启动）")
    ap.add_argument("--force-window-size", default=None, help="强制窗口大小 WxH（如 800x600）")
    args = ap.parse_args()

    force_size = None
    if args.force_window_size:
        w, h = args.force_window_size.split("x")
        force_size = (int(w), int(h))

    replay(args.sequence, args.window, args.executable, force_size)


if __name__ == "__main__":
    main()
```

#### 使用方式

```bash
# 方式 1：游戏已运行，只回放
python3 replay.py --sequence sequence.json --window "Game Title"

# 方式 2：自动启动游戏并回放
python3 replay.py --sequence sequence.json --window "Game Title" --executable ./game

# 方式 3：强制窗口大小（解决全屏问题）
python3 replay.py --sequence sequence.json --window "Game Title" --force-window-size 800x600
```

---

### 方案 B：Unity RuntimeInitializeOnLoadMethod 驱动（需能放入代码）

如果能在游戏工程中放入一个脚本文件（不改游戏原始代码），用 `RuntimeInitializeOnLoadMethod` 在游戏启动时自动加载序列并回放。

#### 核心代码

```csharp
using System.Collections;
using System.Diagnostics;
using System.IO;
using UnityEngine;

public class SequenceReplayer : MonoBehaviour
{
    [System.Serializable]
    class SeqEvent
    {
        public int i, frame;
        public float t;
        public string op;
        public int sx, sy, button;
        public string key;
        public float amount;
    }

    [System.Serializable]
    class SeqFile
    {
        public SeqEvent[] events;
    }

    // 从命令行参数或环境变量读取序列路径
    // -sequence /path/to/sequence.json 或 AUTOGAMER_SEQUENCE=/path/to/sequence.json
    static string GetSequencePath()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-sequence")
                return args[i + 1];
        var env = System.Environment.GetEnvironmentVariable("AUTOGAMER_SEQUENCE");
        return env;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoInit()
    {
        var path = GetSequencePath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var go = new GameObject("SequenceReplayer");
        DontDestroyOnLoad(go);
        go.AddComponent<SequenceReplayer>().StartReplay(path);
    }

    SeqEvent[] _events;
    int _winX, _winY, _winW, _winH;

    public void StartReplay(string path)
    {
        var seq = JsonUtility.FromJson<SeqFile>(File.ReadAllText(path));
        _events = seq.events;
        DetectWindow();
        StartCoroutine(Run());
    }

    void DetectWindow()
    {
        // 用 xdotool 检测窗口位置（Linux）
        var bash = new Process();
        bash.StartInfo = new ProcessStartInfo("/bin/bash",
            "-c 'xdotool getactivewindow'")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        bash.Start();
        bash.WaitForExit(3000);
        var wid = bash.StandardOutput.ReadLine();
        if (!string.IsNullOrEmpty(wid))
        {
            // 强制窗口模式
            var resize = new Process();
            resize.StartInfo = new ProcessStartInfo("/bin/bash",
                $"-c 'xdotool set_window --overrideredirect 1 {wid}; xdotool windowsize {wid} 800 600'")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            resize.Start();
            resize.WaitForExit(3000);

            // 获取窗口几何
            var geo = new Process();
            geo.StartInfo = new ProcessStartInfo("/bin/bash",
                $"-c 'xdotool getwindowgeometry --shell {wid}'")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            geo.Start();
            geo.WaitForExit(3000);
            foreach (var line in geo.StandardOutput.ReadToEnd().Split('\n'))
            {
                var parts = line.Split('=');
                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int v))
                {
                    if (parts[0].Trim() == "X") _winX = v;
                    else if (parts[0].Trim() == "Y") _winY = v;
                    else if (parts[0].Trim() == "WIDTH") _winW = v;
                    else if (parts[0].Trim() == "HEIGHT") _winH = v;
                }
            }
        }
        Debug.Log($"[Replayer] 窗口: ({_winX},{_winY}) {_winW}x{_winH}");
    }

    (int x, int y) ToX11(int sx, int sy)
    {
        float scaleX = _winW > 0 && Screen.width > 0 ? (float)_winW / Screen.width : 1f;
        float scaleY = _winH > 0 && Screen.height > 0 ? (float)_winH / Screen.height : 1f;
        return (_winX + (int)(sx * scaleX), _winY + (int)((Screen.height - sy) * scaleY));
    }

    void Xdo(int sx, int sy, int button, bool down)
    {
        var (absX, absY) = ToX11(sx, sy);
        int xdoBtn = button == 1 ? 1 : (button == 3 ? 3 : 2);
        string action = down ? "mousedown" : "mouseup";
        var p = new Process();
        p.StartInfo = new ProcessStartInfo("/bin/bash",
            $"-c 'xdotool mousemove {absX} {absY} {action} {xdoBtn}'")
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        p.Start();
        p.WaitForExit(500);
    }

    IEnumerator Run()
    {
        float prevT = _events[0].t;
        foreach (var ev in _events)
        {
            float dt = ev.t - prevT;
            if (dt > 0) yield return new WaitForSecondsRealtime(dt);
            prevT = ev.t;

            switch (ev.op)
            {
                case "mousemove":
                    var (mx, my) = ToX11(ev.sx, ev.sy);
                    var p = new Process();
                    p.StartInfo = new ProcessStartInfo("/bin/bash",
                        $"-c 'xdotool mousemove {mx} {my}'")
                    { UseShellExecute = false, CreateNoWindow = true };
                    p.Start(); p.WaitForExit(500);
                    break;
                case "mousedown":
                    Xdo(ev.sx, ev.sy, ev.button, true);
                    break;
                case "mouseup":
                    Xdo(ev.sx, ev.sy, ev.button, false);
                    break;
                case "click":
                    Xdo(ev.sx, ev.sy, ev.button, true);
                    yield return null;
                    Xdo(ev.sx, ev.sy, ev.button, false);
                    break;
                case "keydown":
                    var kd = new Process();
                    kd.StartInfo = new ProcessStartInfo("/bin/bash",
                        $"-c 'xdotool keydown {ev.key}'")
                    { UseShellExecute = false, CreateNoWindow = true };
                    kd.Start(); kd.WaitForExit(500);
                    break;
                case "keyup":
                    var ku = new Process();
                    ku.StartInfo = new ProcessStartInfo("/bin/bash",
                        $"-c 'xdotool keyup {ev.key}'")
                    { UseShellExecute = false, CreateNoWindow = true };
                    ku.Start(); ku.WaitForExit(500);
                    break;
            }
            yield return null;
        }
        Debug.Log("[Replayer] 回放完成");
    }
}
```

#### 使用方式

```bash
# 将 SequenceReplayer.cs 放入游戏工程的 Assets/ 目录（不改任何游戏代码）
# 重新构建游戏
# 运行时指定序列：
./game -sequence /path/to/sequence.json
# 或环境变量：
AUTOGAMER_SEQUENCE=/path/to/sequence.json ./game
```

---

## 两种方案对比

| 维度 | 方案 A：Python 外部驱动 | 方案 B：Unity RuntimeInitializeOnLoadMethod |
|------|----------------------|------------------------------------------|
| 游戏代码修改 | ❌ 完全不需要 | ⚠️ 需放入一个 .cs 文件（不改游戏原始代码） |
| 游戏重新构建 | ❌ 不需要 | ✅ 需要 |
| 时序精度 | ⚠️ 受 OS 调度影响（±10ms） | ✅ 协程帧级精度 |
| 窗口检测 | 外部查找（xdotool search） | 内部 API（更精确） |
| 跨平台 | Linux（xdotool）/ Windows（pyautogui） | 仅 Unity 支持的平台 |
| 适用场景 | 原版游戏不可修改时 | 可放入代码时 |

**推荐**：优先用方案 A（Python 外部驱动），不需要重新构建游戏。方案 B 用于需要更精确时序控制的场景。

---

## 关键实施细节

### 1. 窗口位置适配

录制时和回放时游戏窗口可能在不同位置/大小。驱动程序必须在回放前：
1. 查找游戏窗口（按标题）
2. 获取窗口屏幕位置和大小
3. 计算缩放比（`winW / recScreenW`, `winH / recScreenH`）
4. 每个坐标按缩放比 + 窗口偏移转换

### 2. 全屏问题处理

Unity/Unreal 等引擎的 Player 默认全屏时可能不接收 X11 事件：
- **Linux**：`xdotool set_window --overrideredirect 1 <WID>` + `xdotool windowsize <WID> <W> <H>`
- **Windows**：用 `SetWindowPos` API 或 `--windowed` 命令行参数
- **通用**：在引擎设置中改为窗口模式（`fullscreenMode=0`）

### 3. 时序控制

两种时序策略：

| 策略 | 实现 | 优点 | 缺点 |
|------|------|------|------|
| 时间戳间隔 | `time.sleep(ev.t - prev_t)` | 简单 | 帧率波动时偏差累积 |
| 帧索引对齐 | 按帧号等待（需知道目标帧率） | 精确 | 需要固定帧率 |

**推荐**：时间戳间隔（简单且对大多数游戏足够）。如果游戏有加载画面等不确定时长，使用前置条件等待（见下）。

### 4. 前置条件等待（可选但推荐）

序列中每个事件可以带 `wait` 字段，表示执行前等待某个条件。驱动程序检查条件满足后才执行该事件：

```json
{"i": 5, "wait": "scene_loaded", "op": "click", ...}
```

条件的检测方式取决于驱动类型：
- **Python 外部驱动**：截图 + 像素匹配 / OCR（较慢但通用）
- **Unity 内部驱动**：检查游戏对象/场景状态（精确但需了解游戏内部）

对于不需要精确等待的场景，可以省略 `wait`，直接按时间戳间隔执行。

### 5. 确定性要求

回放要得到与录制时相同的结果，游戏必须确定性运行：
- 无 RNG，或 RNG 用固定种子初始化
- 无网络交互
- 无基于 `DateTime.Now` / `Time.realtimeSinceStartup` 的逻辑
- 物理模拟固定时间步长

如果游戏非确定性，回放可能在某个时刻偏离录制时的轨迹。此时需要前置条件等待来重新同步。

---

## 跨平台替代方案

### Windows（pyautogui）

```python
import pyautogui

# Unity Screen (左下角原点) → Windows 屏幕坐标 (左上角原点)
def unity_to_windows(sx, sy, screen_w, screen_h):
    return sx, screen_h - sy

pyautogui.moveTo(x, y)
pyautogui.mouseDown(button='left')
pyautogui.mouseUp(button='left')
pyautogui.press('Return')
```

### macOS（cliclick 或 pyautogui）

```bash
# cliclick
cliclick c:x,y    # 点击
cliclick dd:x,y   # 按下
cliclick du:x,y   # 释放
cliclick kp:return  # 按键
```

### 通用（pynput，跨平台）

```python
from pynput.mouse import Controller, Button
from pynput.keyboard import Controller as KeyController

mouse = Controller()
mouse.position = (x, y)
mouse.press(Button.left)
mouse.release(Button.left)
```

---

## 验证标准

**等价性验证**：录制一份序列 → 在原版游戏上回放 → 对比结果（通关/分数/状态）是否与录制时一致。

如果不一致：
1. 检查窗口位置/大小是否匹配（坐标偏移导致点到错误位置）
2. 检查游戏是否确定性（RNG/网络/时间依赖）
3. 检查时序是否过快/过慢（游戏动画未完成时下一个操作已执行）
4. 检查全屏问题（事件未到达游戏窗口）

---

## 完整工作流

```
1. 在带内挂的游戏中运行 auto 模式 → 产出 sequence.json + recording.mp4
2. 把 sequence.json 复制到原版游戏机器上
3. 启动原版游戏（无内挂代码）
4. 运行 replay.py --sequence sequence.json --window "游戏标题"
5. 驱动程序自动查找窗口、调整大小、逐条重放操作
6. 原版游戏按序列执行，得到与内挂版相同的结果
```

---

## 录像录制（录制端实现指南）

回放端不需要录像功能，但录制端（有内挂的游戏）需要录制完整对局视频，用于人工审查和 VLM 分析。以下是通用录像录制方案。

### 本游戏的摄像机规格（必须复刻，否则坐标不可复现）

> **关键**：序列中的所有屏幕坐标 `(sx, sy)` 都是基于一个被程序固定到特定位置/角度的主相机计算的。回放端必须确保游戏主相机处于完全相同的位置/角度/投影模式，否则 `WorldToScreenPoint` 算出的坐标不同，xdotool 点到错误位置。

#### 原版游戏的相机（不能直接用于回放）

游戏原版的 `StrategyCamera` 是透视相机，跟随鼠标边缘滚动 + WASD/方向键移动，位置随时变化。**不能用这个相机做序列回放**——同一个世界对象在不同相机位置会投影到不同的屏幕坐标。

#### 录制端使用的固定相机设置

AutoGamer 在 `AGBootstrap.FreezeCamera()` 中做了两件事：
1. 禁用 `StrategyCamera`（停止鼠标/键盘驱动的相机移动）
2. 将主相机（`Main Camera`）固定到俯视位置

```csharp
// AGBootstrap.FreezeCamera() 的完整代码：
var sc = FindObjectOfType<StrategyCamera>();
if (sc != null) sc.enabled = false;        // 禁用相机滚动脚本

var camGo = GameObject.Find("Main Camera");
camGo.transform.position = new Vector3(7.5f, 20f, 8.5f);     // 俯视位置
camGo.transform.rotation = Quaternion.Euler(90, 0, 0);       // 垂直向下看
```

#### 固定相机的完整参数表

| 参数 | 值 | 说明 |
|------|-----|------|
| 相机对象 | `Main Camera`（场景中的主相机） | 不是新建相机，是直接移动原版主相机 |
| 位置 | `(7.5, 20, 8.5)` | X=地图水平中心（0~15 的中点≈7.5），Z=地图垂直中心（0~17 的中点≈8.5），Y=高处 |
| 旋转 | `Euler(90, 0, 0)` | 垂直向下看（X 轴旋转 90°） |
| 投影模式 | **Perspective**（透视） | 原版主相机默认是透视（`fieldOfView=60`），不改为正交 |
| Field of View | 60 | 原版默认值，不改 |
| Screen.width | 800 | 由 `-screen-width 800` 命令行参数控制 |
| Screen.height | 600 | 由 `-screen-height 600` 命令行参数控制 |
| StrategyCamera | **disabled** | 原版相机滚动脚本被禁用 |

#### 为什么不用正交投影？

原版 `Main Camera` 是透视相机（FOV=60）。`WorldToScreenPoint` 在透视和正交下算出的屏幕坐标不同。序列录制时用的是透视投影，回放时也必须用透视投影，否则坐标映射不一致。

#### 回放端如何复刻这个相机

**方案 A（Python 外部驱动）**：无法直接控制游戏内相机。必须在序列回放前让游戏进入相同状态。两种方式：
1. 在序列开头加入"启动时自动设置相机"的事件（需要游戏有相机控制接口）
2. 在 `sequence.json` 的 `header` 中记录相机参数，回放驱动提示用户手动设置

**方案 B（Unity RuntimeInitializeOnLoadMethod）**：在游戏启动时自动设置相机：
```csharp
// 回放驱动在游戏启动时自动固定相机
var sc = FindObjectOfType<StrategyCamera>();
if (sc != null) sc.enabled = false;
Camera.main.transform.position = new Vector3(7.5f, 20f, 8.5f);
Camera.main.transform.rotation = Quaternion.Euler(90, 0, 0);
```

#### 序列文件中应记录的相机元数据

`sequence.json` 的 `header` 中应包含完整的相机参数，让回放端能验证/复刻：

```json
{
  "header": {
    "camera": {
      "type": "perspective",
      "position": [7.5, 20, 8.5],
      "rotation": [90, 0, 0],
      "fieldOfView": 60,
      "orthographic": false
    },
    "screen": { "width": 800, "height": 600 },
    "note": "主相机被固定到俯视位置，StrategyCamera 被禁用。回放时必须确保相机处于相同位置/角度/投影模式"
  }
}
```

#### 坐标计算流程（从世界坐标到 xdotool 点击的完整链路）

以点击红方坦克 `(2, 11)` 为例：

```
1. 碰撞体 bounds.center + up * (size.y/2 + 0.1)
   → 世界坐标 (2.0, 1.13, 10.93)

2. Camera.WorldToScreenPoint(worldPos)
   → 用固定相机 (7.5, 20, 8.5) Euler(90,0,0) FOV=60 透视投影
   → Screen.width=800, Screen.height=600
   → Unity Screen 坐标 (248.5, 366.9)  ← 左下角原点，Y 向上

3. ToScreenCoord(248, 367)
   → 窗口偏移 (0, 0) + 缩放比 (800/800=1.0, 600/600=1.0)
   → absX = 0 + 248 * 1.0 = 248
   → absY = 0 + (600 - 367) * 1.0 = 233  ← Y 轴翻转（Unity 向上 → X11 向下）

4. xdotool mousemove 248 233 mousedown 1 mouseup 1
   → OS 级真实鼠标事件 → Unity 自动处理物理射线 → 命中坦克碰撞体 → OnMouseDown
```

**如果回放端相机的位置/角度/FOV/投影模式/分辨率任何一个不同，步骤 2 算出的屏幕坐标就会不同，步骤 3-4 点击到错误位置。**

### 原理

用 Unity 的专用录制相机渲染到 RenderTexture，逐帧读取 RGBA 像素通过管道写入 ffmpeg 子进程，实时编码为 mp4。**不劫持主相机的 targetTexture**（会导致画面冻结），用独立相机。

### 摄像机角度

**录制相机不使用游戏主相机**——主相机的角度/位置由游戏控制（可能跟随玩家、旋转、缩放），录制时不可控。录制相机应该固定为**俯视全图**角度，确保整个游戏画面都在视野内：

```csharp
var recCam = gameObject.AddComponent<Camera>();
recCam.targetTexture = renderTexture;
recCam.orthographic = true;                          // 正交投影，无透视畸变
recCam.orthographicSize = mapHeight / 2f + 1f;        // 覆盖整个地图高度
recCam.transform.position = new Vector3(
    mapCenterX,                                       // 地图中心 X
    mapCenterY + 20f,                                 // 高处俯视
    mapCenterZ                                        // 地图中心 Z
);
recCam.transform.rotation = Quaternion.Euler(90, 0, 0);  // 垂直向下看
recCam.clearFlags = CameraClearFlags.Skybox;
recCam.cullingMask = ~0;                              // 渲染所有层
recCam.depth = 100;                                   // 不影响主相机渲染顺序
```

#### 不同游戏类型的摄像机角度

| 游戏类型 | 推荐角度 | 说明 |
|---------|---------|------|
| 2D 网格/棋盘（本游戏） | 正交俯视 90° | 整张地图可见，无透视畸变 |
| 2D 横版/俯视动作 | 正交俯视跟随玩家 | `orthographicSize` 覆盖玩家周围区域 |
| 3D 场景 | 透视俯视 60° 或跟随玩家 | `fieldOfView=60`，位置在玩家上方 20 单位 |
| 多房间/滚动地图 | 跟随主相机但固定偏移 | `recCam.transform跟随 mainCamera 但去掉抖动` |

#### 通用原则

1. **能看到全部关键游戏元素**：玩家、敌人、UI、目标位置
2. **固定不抖动**：不要跟随游戏相机的抖动/旋转效果
3. **正交优先**：2D 游戏用正交投影避免透视畸变；3D 游戏用透视
4. **不干扰主相机**：独立 Camera 组件 + 独立 RenderTexture，不修改 `Camera.main.targetTexture`

### 完整录制器实现

```csharp
using System.Diagnostics;
using System.IO;
using UnityEngine;

public class VideoRecorder : MonoBehaviour
{
    Camera _recCam;
    RenderTexture _rt;
    Texture2D _tex;
    byte[] _buffer;
    Process _ffmpeg;
    int _frames;
    string _outPath;
    bool _active;
    float _nextCapture;
    int _fps = 10;
    int _width = 512;
    int _height = 512;

    /// <summary>开始录制</summary>
    public void Begin(string outputPath, int width, int height, int fps)
    {
        _outPath = outputPath;
        _width = width;
        _height = height;
        _fps = fps;

        // 1. 创建 RenderTexture
        _rt = new RenderTexture(width, height, 24);

        // 2. 创建录制相机（独立于主相机）
        _recCam = gameObject.AddComponent<Camera>();
        _recCam.targetTexture = _rt;
        _recCam.orthographic = true;
        _recCam.orthographicSize = 10f;                    // 根据游戏地图调整
        _recCam.transform.position = new Vector3(
            mapCenterX, mapCenterY + 20f, mapCenterZ);     // 俯视位置
        _recCam.transform.rotation = Quaternion.Euler(90, 0, 0);  // 向下看
        _recCam.clearFlags = CameraClearFlags.Skybox;
        _recCam.depth = 100;

        // 3. 创建 Texture2D 用于读取像素
        _tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        _buffer = new byte[width * height * 4];

        // 4. 启动 ffmpeg 子进程
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        _ffmpeg = new Process();
        _ffmpeg.StartInfo = new ProcessStartInfo("ffmpeg",
            $"-y -f rawvideo -vcodec rawvideo -pixel_format rgba " +
            $"-colorspace bt709 -video_size {width}x{height} " +
            $"-framerate {fps} -loglevel warning -i - " +
            $"-c:v libx264 -preset veryfast -pix_fmt yuv420p " +
            $"-crf 23 \"{outputPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        _ffmpeg.Start();
        _active = true;
    }

    void Update()
    {
        if (!_active) return;
        float interval = 1f / Mathf.Max(1, _fps);
        if (Time.unscaledTime < _nextCapture) return;
        _nextCapture = Time.unscaledTime + interval;

        // 5. 渲染到 RenderTexture
        _recCam.Render();

        // 6. 读取像素到 Texture2D
        var prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        _tex.Apply(false);
        RenderTexture.active = prev;

        // 7. 写入 ffmpeg 管道
        var raw = _tex.GetRawTextureData<byte>();
        raw.CopyTo(_buffer);
        _ffmpeg.StandardInput.BaseStream.Write(_buffer, 0, _buffer.Length);
        _ffmpeg.StandardInput.BaseStream.Flush();
        _frames++;
    }

    /// <summary>停止录制</summary>
    public void Stop()
    {
        if (!_active) return;
        _active = false;
        _ffmpeg.StandardInput.Close();
        _ffmpeg.WaitForExit(5000);
        Debug.Log($"视频录制完成: {_outPath} ({_frames} 帧)");
    }

    void OnDestroy()
    {
        Stop();
        if (_rt != null) { _rt.Release(); Destroy(_rt); }
        if (_tex != null) Destroy(_tex);
    }
}
```

### ffmpeg 依赖

- **Linux**：`sudo apt install ffmpeg` 或 `pip install imageio-ffmpeg`
- **macOS**：`brew install ffmpeg`
- **Windows**：下载 ffmpeg.exe 放入 PATH

如果系统 PATH 中无 ffmpeg，可通过环境变量/命令行参数指定路径：
```
AUTOGAMER_FFMPEG_PATH=/path/to/ffmpeg
```

### 录制触发时机

| 时机 | 动作 |
|------|------|
| Auto 模式开启 | `Begin(outputPath, width, height, fps)` |
| 游戏结束（胜利/失败/超时） | `Stop()` |
| 进程退出前 | `OnDestroy` 兜底停止 |

### 录制参数建议

| 参数 | 推荐值 | 说明 |
|------|--------|------|
| 分辨率 | 512x512 或 1024x1024 | 512 够用于 VLM 分析；1024 更清晰 |
| 帧率 | 10 fps | 回合制游戏 10fps 足够；动作游戏用 15-30 |
| 编码 | H.264 CRF 23 | 平衡质量和文件大小 |
| preset | veryfast | 实时编码速度优先 |

### 无头模式注意

- `-batchmode -nographics`：RenderTexture 渲染为黑帧（无 GPU 渲染）
- 需要视频时用 `-batchmode`（不加 `-nographics`），Unity 仍可无窗口渲染到 RenderTexture
- 纯 log 迭代用 `-batchmode -nographics`（不需要视频时）

### 不劫持主相机

**绝对不要**设置 `Camera.main.targetTexture = renderTexture`——这会导致游戏画面冻结（主相机不再渲染到屏幕）。录制必须用独立的 Camera 组件。

### 视频与序列的对应关系

| 产出文件 | 用途 | 谁需要 |
|---------|------|--------|
| `sequence.json` | 键鼠操作序列 → 回放到原版游戏 | 回放驱动 |
| `recording.mp4` | 完整对局视频 → 人工审查 / VLM 分析 | 开发者 |
| `battlelog.json` | 数值快照（每回合单位/建筑/资源状态） | 统计分析 |

三者独立但互补：序列驱动回放，视频供人看，log 供机器分析。
