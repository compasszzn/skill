# Real Mouse Injection & Sequence Replay Skill

## 概述

一套通用的 Unity 游戏 OS 级真实鼠标注入 + 键鼠序列回放方案。所有操作通过 xdotool 注入 OS 级鼠标事件，与人类操作完全等价。不依赖游戏类型，不修改游戏源码。

## 文件清单

| 文件 | 作用 | 通用？ |
|------|------|:---:|
| `AGInputInjector.cs` | 唯一注入口：xdotool OS 级鼠标注入 + 坐标转换 + 窗口检测 | ✅ 通用 |
| `AGGameAdapter.cs` | 游戏适配层：把游戏对象转为世界坐标/屏幕坐标 | ❌ 按游戏替换 |
| `SequenceReplayer.cs` | 独立序列回放器：放入任意 Unity 工程即可用 | ✅ 通用 |
| `RealInputMigration.md` | 改造经验文档 | ✅ 通用 |
| `replay.py` | Python 外部回放驱动（不需要 Unity 工程修改） | ✅ 通用 |

## 快速使用

### 录制（需要内挂在游戏中）

```bash
AUTOGAMER_AUTO=true ./game -screen-width 800 -screen-height 600
# → 产出 sequence.json + recording.mp4 + battlelog.json
```

### 回放方式 1：Unity 内部回放（需放入 AGSequenceReplayer 代码）

```bash
AUTOGAMER_AUTO=false \
./game -run-mode replay -sequence sequence.json -screen-width 800 -screen-height 600
```

### 回放方式 2：独立脚本回放（不需修改游戏工程）

```bash
# 把 SequenceReplayer.cs 放入游戏 Assets/ 目录，重新构建
./game -sequence sequence.json -screen-width 800 -screen-height 600 \
  -camera-pos 7.5,20,8.5 -camera-rot 90,0,0 -camera-fov 60 \
  -camera-disable-script StrategyCamera -replay-force-window 800x600
```

### 回放方式 3：Python 外部驱动（完全不改游戏）

```bash
python3 replay.py --sequence sequence.json --window "Game Title" --force-window-size 800x600
```

---

## AGInputInjector.cs（通用注入器）

```csharp
// 完整代码见 AutoGamerOutput/player-final2/ 下同名文件
// 核心接口（不依赖游戏类型）：
//   ClickScreenPos(sx, sy)           — 屏幕坐标点击
//   ClickWorldPos(worldPos)          — 世界坐标点击
//   ClickColliderTop(collider)       — 碰撞体顶部点击
//   ClickColliderCenter(collider)    — 碰撞体中心点击
//   RightClick()                     — 右键
//   PressKey(key)                   — 键盘
//   Drag(from, to)                  — 拖拽
//   ReplayClick(sx, sy, button)     — 回放
//   WorldToScreen(worldPos)         — 坐标转换工具
```

## AGGameAdapter.cs（游戏适配层 — 按游戏替换）

```csharp
// 每个游戏写一个适配层，把游戏对象转为通用注入器能理解的世界坐标
// 示例（坦克游戏）：
//   ClickUnit(Unit unit)            → ClickColliderTop(unit.collider)
//   ClickTile(Point tile)           → ClickWorldPos(tile.x, 0.5, tile.y)
//   ClickBuilding(Building b)       → ClickColliderTop(building.collider)
//   ClickMenuButton(Menu, item)     → 反射读 Rect/Items/ButtonHeight → ClickScreenPos
```

## SequenceReplayer.cs（独立回放器）

放入任意 Unity 游戏的 `Assets/` 目录，通过 `RuntimeInitializeOnLoadMethod` 自动启动。支持命令行参数和环境变量配置。

## 改造经验（RealInputMigration.md 摘要）

1. **为什么用 xdotool 而非 InputSystem**：IMGUI 的 GUI.Button 只响应 Event.current，InputSystem StateEvent 不生成 Event.current
2. **全屏问题**：Tuanjie Player 全屏不接收 X11 事件 → overrideredirect+windowsize 强制窗口模式
3. **坐标映射**：Unity Screen（左下角，Y上）→ X11（左上角，Y下）→ 翻转 Y + 窗口偏移 + 缩放比
4. **IMGUI 按钮中心**：反射读 Items 列表 + ButtonHeight 计算单按钮中心，不能点菜单框中心
5. **碰撞体遮挡**：用 bounds.center + 顶部偏移避免被地面/地块碰撞体遮挡
6. **主线程阻塞**：WaitForExit(500) 短超时 + 协程多帧 yield
7. **窗口位置**：必须确保窗口在屏幕范围内 → wmctrl 移动到 (0,0) + overrideredirect 去标题栏

## 环境依赖

- `xdotool`：`sudo apt install xdotool`
- `wmctrl`：`sudo apt install wmctrl`
- `ffmpeg`：`sudo apt install ffmpeg` 或 `pip install imageio-ffmpeg`
- Unity InputSystem 包（可选，仅 `activeInputHandler=Both` 桥接用）
