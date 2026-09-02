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
    "map": "游戏主场景名",
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
| `op` | string | ✅ | 操作类型：`mousemove` / `mousedown` / `mouseup` / `keydown` / `keyup` / `key` / `scroll` / `click` / `world_click` / `screen_click` / `gui_click` |
| `sx` | int | mouse* | 屏幕 X 坐标（左下角原点，与 Unity Screen 一致） |
| `sy` | int | mouse* | 屏幕 Y 坐标（左下角原点，Y 向上） |
| `button` | int | mouse* | 鼠标按钮：1=左, 2=中, 3=右 |
| `key` | string | key* | 键键名（X11 keysym 名称，如 `Return` / `Escape` / `a`） |
| `amount` | float | scroll | 滚轮量（正=上，负=下） |
| `wait` | string | ❌ | 前置等待条件 id（可选，见下文） |
| `meta` | string | ❌ | 人类可读元数据（如 `"unit@(2,11)"` / `"MainMenu/Play"`） |

### 坐标系约定

序列中的 `sx, sy` 使用 **Unity Screen 坐标系**（左下角原点，Y 向上）。驱动程序在回放时负责将其转换为 OS 屏幕坐标（左上角原点，Y 向下）。

---

## 回放驱动实现

### 方案 A：Python 外部驱动（推荐，零侵入）

Python 脚本在游戏进程外部运行，用 `xdotool`（Linux）注入 OS 级鼠标/键盘事件。游戏不需要任何修改。

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
    """获取窗口客户区（client area）在屏幕上的位置和大小。

    关键：xdotool getwindowgeometry 返回的是窗口外框（frame）坐标，
    包含标题栏和边框。点击坐标必须相对客户区，否则会整体偏移
    （实测 xfwm4：外框 (10,85) vs 客户区 (5,56)，_NET_FRAME_EXTENTS=5,5,29,5）。

    优先用 xwininfo（直接返回客户区绝对坐标）；
    后备用 xdotool + _NET_FRAME_EXTENTS 修正；
    再后备用 xdotool 裸值（仅对无装饰/override-redirect 窗口准确）。
    """
    # 方法 1：xwininfo（返回客户区绝对坐标，最可靠）
    try:
        result = subprocess.run(
            ["xwininfo", "-id", wid],
            capture_output=True, text=True, timeout=3
        )
        if result.returncode == 0 and result.stdout:
            geo = {}
            for line in result.stdout.split("\n"):
                line = line.strip()
                if line.startswith("Absolute upper-left X:"):
                    geo["X"] = int(line.split(":")[1].strip())
                elif line.startswith("Absolute upper-left Y:"):
                    geo["Y"] = int(line.split(":")[1].strip())
                elif line.startswith("Width:"):
                    geo["WIDTH"] = int(line.split(":")[1].strip())
                elif line.startswith("Height:"):
                    geo["HEIGHT"] = int(line.split(":")[1].strip())
            if "X" in geo and "Y" in geo:
                return geo
    except (FileNotFoundError, subprocess.TimeoutExpired):
        pass  # xwininfo 不可用，回退

    # 方法 2：xdotool + _NET_FRAME_EXTENTS 修正
    result = subprocess.run(
        ["xdotool", "getwindowgeometry", "--shell", wid],
        capture_output=True, text=True
    )
    geo = {}
    for line in result.stdout.strip().split("\n"):
        if "=" in line:
            k, v = line.split("=", 1)
            geo[k.strip()] = int(v.strip())

    # 用 _NET_FRAME_EXTENTS 修正回客户区
    try:
        ext_result = subprocess.run(
            ["xprop", "-id", wid, "_NET_FRAME_EXTENTS"],
            capture_output=True, text=True, timeout=3
        )
        if ext_result.returncode == 0 and "=" in ext_result.stdout:
            nums = ext_result.stdout.split("=")[1].strip().split(",")
            if len(nums) == 4:
                left = int(nums[0].strip())
                top = int(nums[2].strip())
                geo["X"] = geo.get("X", 0) - left
                geo["Y"] = geo.get("Y", 0) - top
    except (FileNotFoundError, subprocess.TimeoutExpired):
        pass  # xprop 不可用，用 xdotool 裸值（仅对无装饰窗口准确）

    return geo


def force_windowed(wid, width, height):
    """强制窗口模式（仅游戏全屏启动时需要；窗口模式启动勿用——overrideredirect
    会让 WM 重新摆放窗口导致几何不稳）"""
    subprocess.run(
        ["xdotool", "set_window", "--overrideredirect", "1", wid],
        capture_output=True
    )
    subprocess.run(
        ["xdotool", "windowsize", wid, str(width), str(height)],
        capture_output=True
    )
    subprocess.run(
        ["xdotool", "windowmove", wid, "0", "0"],
        capture_output=True
    )
    time.sleep(1)


def unity_to_x11(sx, sy, screen_w, screen_h, win_x, win_y, win_w, win_h):
    """Unity Screen 坐标 → X11 屏幕绝对坐标（相对客户区）"""
    scale_x = win_w / screen_w if screen_w > 0 else 1.0
    scale_y = win_h / screen_h if screen_h > 0 else 1.0
    abs_x = win_x + int(sx * scale_x)
    abs_y = win_y + int((screen_h - sy) * scale_y)  # Y 轴翻转
    return abs_x, abs_y


def do_mousemove(abs_x, abs_y):
    subprocess.run(["xdotool", "mousemove", str(abs_x), str(abs_y)], capture_output=True)


def do_mousedown(button):
    xdo_btn = {1: 1, 2: 2, 3: 3}[button]
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
        subprocess.run(["xdotool", "click", "4"], capture_output=True)
    else:
        subprocess.run(["xdotool", "click", "5"], capture_output=True)


def replay(sequence_path, window_title, executable=None, force_window_size=None,
           screen_width=800, screen_height=600):
    # 加载序列
    with open(sequence_path) as f:
        seq = json.load(f)

    header = seq.get("header", {})
    events = seq["events"]
    rec_screen_w = header.get("screen_width", screen_width)
    rec_screen_h = header.get("screen_height", screen_height)

    print(f"序列: {len(events)} 事件, 录制分辨率 {rec_screen_w}x{rec_screen_h}")

    # 启动游戏（如果指定了可执行文件）
    if executable:
        print(f"启动游戏: {executable}")
        subprocess.Popen([executable])
        time.sleep(3)

    # 查找游戏窗口
    wid = None
    for _ in range(30):
        wid = find_window(window_title)
        if wid:
            break
        time.sleep(1)

    if not wid:
        print(f"找不到窗口: {window_title}")
        sys.exit(1)

    print(f"游戏窗口: {wid}")

    # 强制窗口模式（仅全屏启动时需要）
    if force_window_size:
        w, h = force_window_size
        force_windowed(wid, w, h)

    # 获取窗口几何（客户区坐标）
    geo = get_window_geometry(wid)
    win_x = geo.get("X", 0)
    win_y = geo.get("Y", 0)
    win_w = geo.get("WIDTH", rec_screen_w)
    win_h = geo.get("HEIGHT", rec_screen_h)
    print(f"窗口客户区: ({win_x}, {win_y}) 大小: {win_w}x{win_h}")

    # 激活窗口
    subprocess.run(["xdotool", "windowactivate", wid], capture_output=True)
    time.sleep(0.5)

    # 逐事件回放
    print("开始回放...")
    prev_t = events[0]["t"] if events else 0
    prev_scene = None
    for ev in events:
        # 时序控制：按时间戳间隔等待
        dt = ev["t"] - prev_t
        if dt > 0:
            time.sleep(dt)
        prev_t = ev["t"]

        # 场景切换检测：如果事件间隔大且前一个事件可能是场景切换，
        # 重新获取窗口几何（防止场景切换时窗口位置变化）
        wait_id = ev.get("wait", "")
        if wait_id and wait_id != prev_scene:
            geo = get_window_geometry(wid)
            win_x = geo.get("X", win_x)
            win_y = geo.get("Y", win_y)
            win_w = geo.get("WIDTH", win_w)
            win_h = geo.get("HEIGHT", win_h)
            prev_scene = wait_id

        op = ev["op"]

        if op in ("mousemove", "mousedown", "mouseup", "click",
                   "world_click", "screen_click", "gui_click",
                   "tutorial_click", "replay_click"):
            sx = ev["sx"]
            sy = ev["sy"]
            # 每次点击前刷新窗口几何（防止窗口被 WM 移动）
            geo = get_window_geometry(wid)
            cx = geo.get("X", win_x)
            cy = geo.get("Y", win_y)
            cw = geo.get("WIDTH", win_w)
            ch = geo.get("HEIGHT", win_h)
            abs_x, abs_y = unity_to_x11(
                sx, sy, rec_screen_w, rec_screen_h,
                cx, cy, cw, ch
            )

            if op == "mousemove":
                do_mousemove(abs_x, abs_y)
            elif op == "mousedown":
                do_mousemove(abs_x, abs_y)
                time.sleep(0.02)
                btn = ev.get("button", 1)
                do_mousedown(btn)
            elif op == "mouseup":
                btn = ev.get("button", 1)
                do_mouseup(btn)
            else:  # click / world_click / screen_click / ...
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

        time.sleep(0.01)

    print("回放完成")


def main():
    ap = argparse.ArgumentParser(description="通用键鼠序列回放驱动")
    ap.add_argument("--sequence", required=True, help="序列文件路径")
    ap.add_argument("--window", required=True, help="游戏窗口标题")
    ap.add_argument("--executable", default=None, help="游戏可执行文件路径")
    ap.add_argument("--force-window-size", default=None, help="强制窗口大小 WxH")
    ap.add_argument("--screen-width", type=int, default=800, help="录制分辨率宽")
    ap.add_argument("--screen-height", type=int, default=600, help="录制分辨率高")
    args = ap.parse_args()

    force_size = None
    if args.force_window_size:
        w, h = args.force_window_size.split("x")
        force_size = (int(w), int(h))

    replay(args.sequence, args.window, args.executable, force_size,
           args.screen_width, args.screen_height)


if __name__ == "__main__":
    main()
```

#### 使用方式

```bash
# 方式 1：游戏已运行，只回放
python3 replay.py --sequence sequence.json --window "Game Title"

# 方式 2：自动启动游戏并回放（推荐窗口模式启动）
python3 replay.py --sequence sequence.json --window "Game Title" \
  --executable "./game -screen-fullscreen 0 -screen-width 800 -screen-height 600"

# 方式 3：强制窗口大小（仅游戏全屏启动时需要）
python3 replay.py --sequence sequence.json --window "Game Title" --force-window-size 800x600
```

> **重要**：优先用 `-screen-fullscreen 0 -screen-width W -screen-height H` 窗口模式启动游戏，
> 避免 `--force-window-size`（overrideredirect 会让窗口管理器重新摆放窗口，导致几何不稳定）。

---

### 方案 B：Unity RuntimeInitializeOnLoadMethod 驱动（需能放入代码）

如果能在游戏工程中放入一个脚本文件（不改游戏原始代码），用 `RuntimeInitializeOnLoadMethod` 在游戏启动时自动加载序列并回放。

#### 完整实现

见同目录的 **`SequenceReplayer.cs`** — 这是经过实战验证的完整实现，包含以下关键设计：

1. **客户区坐标修正**：`DetectWindowGeometry()` 优先用 `xwininfo` 获取客户区绝对坐标，后备 `xdotool` + `_NET_FRAME_EXTENTS` 修正（详见下文踩坑 #1）
2. **每次点击前刷新窗口几何**：防止窗口管理器在回放过程中移动窗口
3. **场景就绪屏障**：场景切换后等待新场景加载 + 相机固定完成再继续（详见下文踩坑 #2）
4. **窗口模式优先**：不默认做 `overrideredirect`，避免 WM 重新摆放窗口（详见下文踩坑 #3）
5. **屏幕录像**：`WaitForEndOfFrame` + `ReadPixels` 捕获完整画面（3D + IMGUI），RGBA 管道写入 ffmpeg
6. **通用性**：不依赖任何游戏类型、场景名硬编码（通过 `header.map` 配置），单文件放入 `Assets/` 即可

#### 使用方式

```bash
# 将 SequenceReplayer.cs 放入游戏工程的 Assets/ 目录（不改任何游戏代码）
# 重新构建游戏
# 运行时指定序列（推荐窗口模式启动）：
./game -screen-fullscreen 0 -screen-width 800 -screen-height 600 \
  -sequence /path/to/sequence.json \
  -camera-pos 7.5,20,8.5 -camera-rot 90,0,0 -camera-fov 60 \
  -camera-disable-script StrategyCamera \
  -replay-output-dir /path/to/output \
  -replay-quit-on-end true \
  -replay-record true -replay-record-fps 15

# 或用环境变量：
REPLAY_SEQUENCE=/path/to/sequence.json \
REPLAY_CAMERA_POS=7.5,20,8.5 \
REPLAY_CAMERA_ROT=90,0,0 \
REPLAY_CAMERA_FOV=60 \
REPLAY_CAMERA_DISABLE_SCRIPT=StrategyCamera \
./game -screen-fullscreen 0 -screen-width 800 -screen-height 600
```

---

## 两种方案对比

| 维度 | 方案 A：Python 外部驱动 | 方案 B：Unity RuntimeInitializeOnLoadMethod |
|------|----------------------|------------------------------------------|
| 游戏代码修改 | ❌ 完全不需要 | ⚠️ 需放入一个 .cs 文件（不改游戏原始代码） |
| 游戏重新构建 | ❌ 不需要 | ✅ 需要 |
| 时序精度 | ⚠️ 受 OS 调度影响（±10ms） | ✅ 协程帧级精度 |
| 窗口检测 | 外部查找（xdotool search） | 内部 API（更精确） |
| 相机固定 | ❌ 无法直接控制 | ✅ 自动固定 |
| 视频录制 | ❌ 需额外录屏工具 | ✅ 内置（屏幕捕获 → ffmpeg） |
| 场景就绪屏障 | ❌ 难以精确检测 | ✅ 可检测场景/相机状态 |
| 跨平台 | Linux（xdotool）/ Windows（pyautogui） | 仅 Unity 支持的平台 |
| 适用场景 | 原版游戏不可修改时 | 可放入代码时 |

**推荐**：优先用方案 B（Unity 内部驱动）—— 时序精确、可固定相机、可内置录像、可检测场景就绪。方案 A 用于完全不可修改的原版游戏。

---

## 实战踩坑（2026-09 验证记录）

以下三个问题在 OpenAW3D（Advance Wars 3D 克隆）完整对局回放中发现并修复，均为通用问题：

### 踩坑 #1：客户区 vs 外框坐标（最致命）

**现象**：点击坐标计算正确（Unity Screen → X11），但所有点击整体下移约 29px，导致点击 "Play" 按钮实际点到 "Quit" 按钮，游戏直接退出。

**根因**：`xdotool getwindowgeometry` 在带窗口管理器装饰的窗口上返回的是**外框（frame）坐标**，包含标题栏和边框。而 xdotool 的 `mousemove` 使用的是**屏幕绝对坐标**，点击必须相对客户区（client area）。

实测数据（xfwm4 窗口管理器）：
```
xdotool getwindowgeometry → X=10, Y=85     ← 外框（含标题栏 29px + 左边框 5px）
xwininfo -id               → X=5,  Y=56     ← 客户区（实际渲染区域）
_NET_FRAME_EXTENTS          = 5, 5, 29, 5   ← left, right, top, bottom
```

差值正好是标题栏高度（29px），导致所有点击整体下移 29px。

**修复**：优先用 `xwininfo -id <WID>` 获取客户区绝对坐标（直接返回客户区位置）；后备用 `xdotool` + `xprop _NET_FRAME_EXTENTS` 修正外框坐标回客户区。两者都不可用时回退 `xdotool` 裸值（仅对无装饰 / override-redirect 窗口准确）。

```python
# Python（方案 A）
def get_window_geometry(wid):
    # 方法 1：xwininfo（客户区绝对坐标，最可靠）
    result = subprocess.run(["xwininfo", "-id", wid], ...)
    # 解析 "Absolute upper-left X/Y", "Width", "Height"

    # 方法 2：xdotool + _NET_FRAME_EXTENTS 修正
    geo = xdotool_getwindowgeometry(wid)
    ext = xprop(wid, "_NET_FRAME_EXTENTS")  # left, right, top, bottom
    geo["X"] -= ext["left"]
    geo["Y"] -= ext["top"]
```

```csharp
// C#（方案 B，SequenceReplayer.DetectWindowGeometry）
var xwi = RunBash($"xwininfo -id {_windowWid} 2>/dev/null");
// 解析 "Absolute upper-left X/Y", "Width", "Height"
// 后备：xdotool + xprop _NET_FRAME_EXTENTS 修正
```

**影响**：此问题对**所有使用窗口管理器装饰的 Linux 桌面**（xfwm4、Mutter、KWin 等）均存在，不是特定游戏的问题。

### 踩坑 #2：场景切换后时序过快导致点击落在旧场景

**现象**：序列中"点击 Play 按钮"（事件 0）和"点击关闭教程"（事件 1）之间只有 0.1s 间隔。录制时（Editor 内热启动）Scene01 在 0.1s 内加载完成，但回放时（standalone 冷启动）Scene01 需要 1-2s 加载。结果事件 1 的点击在 MainMenu 仍然可见时执行，点到了 "Quit" 按钮。

**根因**：纯时间戳间隔回放在场景切换时不安全——加载时间在不同环境下差异很大（Editor 热启动 vs standalone 冷启动 vs 不同硬件）。

**修复（方案 B）**：加入**场景就绪屏障**——`SceneManager.sceneLoaded` 事件触发后设置 `_settled = false`，回放循环在每个事件执行前检查 `_settled`，如果未就绪则等待：
- 游戏主场景（`header.map`）：等待相机固定完成（`_cameraFrozen == true`），再留 0.3s 缓冲
- 其他场景（主菜单等）：0.3s 短缓冲即可

```csharp
IEnumerator WaitSceneSettled()
{
    if (isGameScene)
        while (!_cameraFrozen && ... < 15f) yield return null;  // 等相机固定
    yield return new WaitForSecondsRealtime(0.3f);
    _settled = true;
}
```

**修复（方案 A）**：Python 无法检测游戏内部状态，需要：
1. 在序列 `events` 中使用 `wait` 字段标注场景切换点
2. 驱动程序在 `wait` 条件变化时重新检测窗口几何并增加额外等待
3. 或用截图 + 像素匹配检测场景是否加载完成

### 踩坑 #3：overrideredirect 导致窗口几何不稳定

**现象**：使用 `xdotool set_window --overrideredirect 1` 取消窗口装饰后，窗口管理器（xfwm4）会重新摆放窗口，导致 `getwindowgeometry` 返回的坐标在短时间内变化，点击落点不可预测。

**根因**：`overrideredirect` 剥夺了窗口管理器对窗口的控制权，但 WM 可能在意识到这一点之前已经移动了窗口。实测：设置 overrideredirect 后窗口从 (2,55) 跳到 (10,85)，然后又跳回，导致读取的几何与实际位置不符。

**修复**：
1. **优先窗口模式启动**：`-screen-fullscreen 0 -screen-width W -screen-height H`，不需要 overrideredirect
2. **仅在游戏全屏启动时才用 overrideredirect**（作为兜底），并设置后等待 1s 让窗口稳定
3. **每次点击前重新检测窗口几何**（自纠错），即使窗口被移动也能跟上

---

## 关键实施细节

### 1. 窗口位置适配（客户区坐标 — 必须正确）

录制时和回放时游戏窗口可能在不同位置/大小。驱动程序必须在回放前：
1. 查找游戏窗口（按标题）
2. **获取窗口客户区位置和大小**（不是外框！见踩坑 #1）
3. 计算缩放比（`winW / recScreenW`, `winH / recScreenH`）
4. 每个坐标按缩放比 + 客户区偏移转换
5. **每次点击前重新获取几何**（防止窗口被 WM 移动）

### 2. 全屏问题处理

Unity/Unreal 等引擎的 Player 默认全屏时可能不接收 X11 事件：
- **首选**：用 `-screen-fullscreen 0 -screen-width W -screen-height H` 命令行参数启动窗口模式
- **Linux 兜底**（仅全屏启动时）：`xdotool set_window --overrideredirect 1 <WID>` + `xdotool windowsize <WID> <W> <H>` + `xdotool windowmove <WID> 0 0`，然后等待 1s
- **Windows**：用 `SetWindowPos` API 或 `--windowed` 命令行参数
- **通用**：在引擎设置中改为窗口模式（`fullscreenMode=0`）

> **注意**：`overrideredirect` 会让窗口管理器重新摆放窗口，导致几何在短时间内不稳定（见踩坑 #3）。仅在游戏全屏启动时作为兜底使用，窗口模式启动时不要用。

### 3. 场景就绪屏障（方案 B 专用，关键）

纯时间戳间隔在场景切换时不安全——加载时间在不同环境下差异很大。方案 B 的 `SequenceReplayer` 实现了场景就绪屏障：
- `SceneManager.sceneLoaded` 事件触发 → `_settled = false`
- 回放循环在每个事件执行前检查 `_settled`
- 游戏主场景：等待相机固定完成 + 0.3s 缓冲
- 其他场景：0.3s 短缓冲

详见踩坑 #2。

### 4. 时序控制

| 策略 | 实现 | 优点 | 缺点 |
|------|------|------|------|
| 时间戳间隔 | `time.sleep(ev.t - prev_t)` / `yield return new WaitForSecondsRealtime(dt)` | 简单 | 帧率波动时偏差累积；场景切换时不可靠 |
| 帧索引对齐 | 按帧号等待（需知道目标帧率） | 精确 | 需要固定帧率 |

**推荐**：时间戳间隔 + 场景就绪屏障（方案 B）。如果游戏有加载画面等不确定时长，用 `wait` 字段标注场景切换点。

### 5. 前置条件等待（可选但推荐）

序列中每个事件可以带 `wait` 字段，表示执行前等待某个条件：

```json
{"i": 5, "wait": "scene_loaded", "op": "click", ...}
```

条件的检测方式取决于驱动类型：
- **Python 外部驱动**：截图 + 像素匹配 / OCR（较慢但通用）
- **Unity 内部驱动**：检查游戏对象/场景状态（精确但需了解游戏内部）

对于不需要精确等待的场景，可以省略 `wait`，直接按时间戳间隔执行。但场景切换处必须有屏障（见踩坑 #2）。

### 6. 确定性要求

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

> Windows 上 `pyautogui.position()` 返回的就是客户区坐标，不需要 frame 修正。

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
1. **检查窗口客户区坐标**（最常见问题！见踩坑 #1）—— `xdotool getwindowgeometry` 返回外框坐标，差一个标题栏高度
2. 检查场景切换后时序是否过快（见踩坑 #2）—— 场景加载需要时间，下一个事件可能在旧场景执行
3. 检查窗口是否被 WM 移动（见踩坑 #3）—— 每次点击前重新获取几何
4. 检查游戏是否确定性（RNG/网络/时间依赖）
5. 检查全屏问题（事件未到达游戏窗口）

---

## 完整工作流

```
1. 在带内挂的游戏中运行 auto 模式 → 产出 sequence.json + recording.mp4
2. 把 sequence.json 复制到原版游戏机器上
3. 启动原版游戏（无内挂代码），窗口模式：
   ./game -screen-fullscreen 0 -screen-width 800 -screen-height 600
4a. 方案 A：运行 replay.py --sequence sequence.json --window "游戏标题"
4b. 方案 B：将 SequenceReplayer.cs 放入 Assets/，构建后运行
    ./game -sequence sequence.json -camera-pos ... -camera-rot ... ...
5. 驱动程序自动查找窗口、获取客户区几何、逐条重放操作
6. 原版游戏按序列执行，得到与内挂版相同的结果
```

---

## 录像录制

### 方案 B 回放端录像（屏幕捕获，推荐）

`SequenceReplayer.cs` 内置了屏幕录像功能：在 `WaitForEndOfFrame` 时刻读取屏幕像素，通过管道写入 ffmpeg 子进程实时编码为 mp4。

**优势**（相比独立录制相机）：
- **同时捕捉 3D 场景与 OnGUI 的 IMGUI 界面**——独立录制相机无法渲染 IMGUI
- **所见即所得**——录到的画面就是玩家看到的画面（含鼠标光标位置）
- **不需要额外相机对象**——直接读取帧缓冲

```csharp
// SequenceReplayer.cs 中的录像核心逻辑
IEnumerator RecordVideo()
{
    while (_recording)
    {
        yield return new WaitForEndOfFrame();
        _recTex.ReadPixels(new Rect(0, 0, _recW, _recH), 0, 0, false);
        var raw = _recTex.GetRawTextureData<byte>();
        raw.CopyTo(_recBuf);
        _ffmpegProc.StandardInput.BaseStream.Write(_recBuf, 0, _recBuf.Length);
        _recFrames++;
    }
}
```

ffmpeg 启动参数（ReadPixels 读到的像素自下而上，需要 `vflip`）：
```
ffmpeg -y -f rawvideo -vcodec rawvideo -pixel_format rgba
  -video_size 800x600 -framerate 15 -i -
  -vf vflip -c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 23 replay.mp4
```

### 录制端录像（独立录制相机，录制时用）

录制端（有内挂的游戏）需要录制完整对局视频，用于人工审查和 VLM 分析。用 Unity 的专用录制相机渲染到 RenderTexture，逐帧读取 RGBA 像素通过管道写入 ffmpeg。

### 本游戏的摄像机规格（必须复刻，否则坐标不可复现）

> **关键**：序列中的所有屏幕坐标 `(sx, sy)` 都是基于一个被程序固定到特定位置/角度的主相机计算的。回放端必须确保游戏主相机处于完全相同的位置/角度/投影模式，否则 `WorldToScreenPoint` 算出的坐标不同，xdotool 点到错误位置。

#### 固定相机参数表

| 参数 | 值 | 说明 |
|------|-----|------|
| 相机对象 | `Main Camera`（场景中的主相机） | 不是新建相机，是直接移动原版主相机 |
| 位置 | `(7.5, 20, 8.5)` | 由 `-camera-pos` 命令行参数配置 |
| 旋转 | `Euler(90, 0, 0)` | 由 `-camera-rot` 命令行参数配置 |
| Field of View | 60 | 由 `-camera-fov` 命令行参数配置 |
| Screen.width | 800 | 由 `-screen-width 800` 命令行参数控制 |
| Screen.height | 600 | 由 `-screen-height 600` 命令行参数控制 |
| 相机控制脚本 | **disabled** | 由 `-camera-disable-script` 配置（如 `StrategyCamera`） |

#### 回放端如何复刻相机

**方案 A（Python 外部驱动）**：无法直接控制游戏内相机。必须在序列回放前让游戏进入相同状态。两种方式：
1. 在序列开头加入"启动时自动设置相机"的事件（需要游戏有相机控制接口）
2. 在 `sequence.json` 的 `header` 中记录相机参数，回放驱动提示用户手动设置

**方案 B（Unity RuntimeInitializeOnLoadMethod）**：在游戏主场景加载后自动设置相机（`SequenceReplayer.SetupCamera()`）：
```csharp
// 禁用相机控制脚本
var comp = FindObjectOfType(Type.GetType(_cameraDisableScript)) as MonoBehaviour;
if (comp != null) comp.enabled = false;
// 固定相机
cam.transform.position = _cameraPos;       // 命令行参数
cam.transform.rotation = Quaternion.Euler(_cameraRot);
cam.fieldOfView = _cameraFov;
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
    "note": "主相机被固定到俯视位置，相机控制脚本被禁用。回放时必须确保相机处于相同位置/角度/投影模式"
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

3. ToX11(248, 367)
   → 客户区偏移 (5, 56) + 缩放比 (800/800=1.0, 600/600=1.0)
   → absX = 5 + 248 * 1.0 = 253
   → absY = 56 + (600 - 367) * 1.0 = 289  ← Y 轴翻转（Unity 向上 → X11 向下）

4. xdotool mousemove 253 289 mousedown 1 mouseup 1
   → OS 级真实鼠标事件 → Unity 自动处理物理射线 → 命中坦克碰撞体 → OnMouseDown
```

**如果回放端相机的位置/角度/FOV/投影模式/分辨率任何一个不同，或者客户区偏移错误，步骤 2-4 都会点击到错误位置。**

### 录制端独立录制相机（录制时用，非回放端）

录制端用独立的 Camera 组件渲染到 RenderTexture，**不劫持主相机的 targetTexture**（会导致画面冻结）。

```csharp
var recCam = gameObject.AddComponent<Camera>();
recCam.targetTexture = renderTexture;
recCam.orthographic = true;                          // 正交投影，无透视畸变
recCam.orthographicSize = mapHeight / 2f + 1f;        // 覆盖整个地图高度
recCam.transform.position = new Vector3(
    mapCenterX, mapCenterY + 20f, mapCenterZ);        // 俯视位置
recCam.transform.rotation = Quaternion.Euler(90, 0, 0);
recCam.clearFlags = CameraClearFlags.Skybox;
recCam.cullingMask = ~0;                              // 渲染所有层
recCam.depth = 100;                                   // 不影响主相机渲染顺序
```

#### 不同游戏类型的摄像机角度

| 游戏类型 | 推荐角度 | 说明 |
|---------|---------|------|
| 2D 网格/棋盘 | 正交俯视 90° | 整张地图可见，无透视畸变 |
| 2D 横版/俯视动作 | 正交俯视跟随玩家 | `orthographicSize` 覆盖玩家周围区域 |
| 3D 场景 | 透视俯视 60° 或跟随玩家 | `fieldOfView=60`，位置在玩家上方 20 单位 |
| 多房间/滚动地图 | 跟随主相机但固定偏移 | `recCam.transform` 跟随 mainCamera 但去掉抖动 |

#### 通用原则

1. **能看到全部关键游戏元素**：玩家、敌人、UI、目标位置
2. **固定不抖动**：不要跟随游戏相机的抖动/旋转效果
3. **正交优先**：2D 游戏用正交投影避免透视畸变；3D 游戏用透视
4. **不干扰主相机**：独立 Camera 组件 + 独立 RenderTexture，不修改 `Camera.main.targetTexture`

### ffmpeg 依赖

- **Linux**：`sudo apt install ffmpeg` 或 `pip install imageio-ffmpeg`
- **macOS**：`brew install ffmpeg`
- **Windows**：下载 ffmpeg.exe 放入 PATH

如果系统 PATH 中无 ffmpeg，可通过环境变量/命令行参数指定路径：
```
REPLAY_FFMPEG_PATH=/path/to/ffmpeg
```

### 录制参数建议

| 参数 | 推荐值 | 说明 |
|--------|--------|------|
| 分辨率 | 与游戏窗口一致（如 800x600） | 方案 B 屏幕捕获；录制端独立相机可用 512x512 或 1024x1024 |
| 帧率 | 10-15 fps | 回合制游戏 10fps 足够；动作游戏用 15-30 |
| 编码 | H.264 CRF 23 | 平衡质量和文件大小 |
| preset | veryfast | 实时编码速度优先 |

### 无头模式注意

- `-batchmode -nographics`：RenderTexture 渲染为黑帧（无 GPU 渲染）
- 需要视频时用 `-batchmode`（不加 `-nographics`），Unity 仍可无窗口渲染到 RenderTexture
- 纯 log 迭代用 `-batchmode -nographics`（不需要视频时）
- 方案 B 的屏幕捕获录像**不支持无头模式**（`ReadPixels` 需要实际帧缓冲）

### 不劫持主相机

**绝对不要**设置 `Camera.main.targetTexture = renderTexture`——这会导致游戏画面冻结（主相机不再渲染到屏幕）。录制端必须用独立的 Camera 组件。回放端的屏幕捕获（`ReadPixels`）不涉及此问题——它读取的是帧缓冲而非相机的 targetTexture。

### 视频与序列的对应关系

| 产出文件 | 用途 | 谁需要 |
|---------|------|--------|
| `sequence.json` | 键鼠操作序列 → 回放到原版游戏 | 回放驱动 |
| `replay.mp4` | 完整对局视频 → 人工审查 / VLM 分析 | 开发者 |
| `done.json` | 回放结果（执行事件数/总事件数/视频路径/帧数） | 自动化验证 |

三者独立但互补：序列驱动回放，视频供人看，结果供机器验证。

---

## 环境依赖

| 依赖 | 安装 | 用途 |
|------|------|------|
| xdotool | `sudo apt install xdotool` | OS 级鼠标/键盘注入（必需） |
| x11-utils | `sudo apt install x11-utils` | xwininfo 客户区坐标检测（推荐） |
| ffmpeg | `sudo apt install ffmpeg` | 视频编码（录像时需要） |
| wmctrl | `sudo apt install wmctrl` | 窗口管理（可选） |
