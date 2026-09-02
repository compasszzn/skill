# Real Mouse Injection & Sequence Replay Skill

## 文件清单与用法

| 文件 | 类型 | 用途 |
|------|------|------|
| `AGInputInjector.cs` | C# 代码 | **通用注入器**。唯一注入口，所有操作经 xdotool OS 级鼠标注入。包含通用接口（ClickScreenPos/ClickWorldPos/ClickColliderTop 等）+ 游戏适配层（AGGameAdapter，按游戏替换）。放入游戏工程 `Assets/` 目录。 |
| `SequenceReplayer.cs` | C# 代码 | **独立回放器**。放入任意 Unity 游戏工程 `Assets/` 目录，通过 `-sequence` 参数启动，按序列文件的屏幕坐标用 xdotool 重放。不改游戏源码，不依赖任何游戏类型。内置屏幕录像（WaitForEndOfFrame → ffmpeg）。已实战验证（OpenAW3D 91 事件完整对局，2026-09）。 |
| `RealInputMigration.md` | 文档 | **改造经验**。从 SendMessage 改为 xdotool 真实鼠标注入的完整记录，包含 6 个关键技术决策和踩坑过程。 |
| `RealInputPrinciple.md` | 文档 | **输入规范**。真实输入驱动的设计原则：禁止 SendMessage/反射，必须用 OS 级鼠标事件。 |
| `RealMouseInjectionSkill.md` | 文档 | **Skill 总览**。文件清单 + 快速使用 + 接口说明 + 环境依赖。 |
| `SequenceReplaySkill.md` | 文档 | **回放 Skill 详细文档**。序列格式 schema + Python 外部驱动代码 + Unity 内部回放代码 + 录像录制方案 + 摄像机规格 + 跨平台方案。 |
| `example_sequence.json` | 数据 | **示例序列**。91 个键鼠操作事件（完整一局坦克游戏），包含 click/right_click/key 等操作类型 + 屏幕坐标 + 时间戳。 |
| `example_battlelog.json` | 数据 | **示例战报**。11 个回合快照（单位/建筑/资源状态），供统计分析参考。 |

---

## 快速使用

### 1. 录制（需要内挂在游戏中）

将 `AGInputInjector.cs` + `AGGameAdapter`（按游戏适配）放入游戏工程，构建后运行：

```bash
AUTOGAMER_AUTO=true ./game -screen-width 800 -screen-height 600
# → 产出 sequence.json + recording.mp4 + battlelog.json
```

### 2. 回放（原版游戏，不改源码）

将 `SequenceReplayer.cs` 放入游戏 `Assets/` 目录，重新构建，运行：

```bash
# 推荐窗口模式启动（避免全屏/overrideredirect 几何问题）
AUTOGAMER_AUTO=false \
./game \
  -screen-fullscreen 0 -screen-width 800 -screen-height 600 \
  -sequence /path/to/sequence.json \
  -camera-pos 7.5,20,8.5 \
  -camera-rot 90,0,0 \
  -camera-fov 60 \
  -camera-disable-script StrategyCamera \
  -replay-output-dir /path/to/output \
  -replay-quit-on-end true \
  -replay-record true -replay-record-fps 15
```

> **重要**：优先用 `-screen-fullscreen 0` 窗口模式启动，避免 `-replay-force-window`
> （overrideredirect 会让窗口管理器重新摆放窗口导致几何不稳，见
> `SequenceReplaySkill.md` 踩坑 #3）。

### 3. Python 外部驱动（完全不改游戏）

见 `SequenceReplaySkill.md` 中的 `replay.py` 代码，在游戏外部用 xdotool 重放。

---

## 代码架构

```
AGInputInjector.cs（通用，不依赖游戏类型）
├── ClickScreenPos(sx, sy)           — 屏幕坐标点击
├── ClickWorldPos(worldPos)          — 世界坐标点击
├── ClickColliderTop(collider)       — 碰撞体顶部点击（避免遮挡）
├── ClickColliderCenter(collider)    — 碰撞体中心点击
├── RightClick()                     — 右键
├── PressKey(key)                   — 键盘
├── Drag(from, to)                  — 拖拽
├── ReplayClick(sx, sy, button)     — 回放
├── WorldToScreen(worldPos)         — 坐标转换
└── 窗口检测 + overrideredirect + 坐标映射

AGGameAdapter（游戏适配层 — 按游戏替换）
├── ClickUnit(Unit) → ClickColliderTop(unit.collider)
├── ClickTile(Point) → ClickWorldPos(x, 0.5, y)
├── ClickBuilding(Building) → ClickColliderTop(building.collider)
├── ClickMenuButton(Menu, item) → 反射读 Rect/Items → ClickScreenPos
└── ClickTutorial() → ClickScreenPos(中心)

SequenceReplayer.cs（独立回放器，不依赖任何游戏类型）
├── RuntimeInitializeOnLoadMethod 自动启动
├── 窗口检测 + overrideredirect + windowsize
├── 相机固定（位置/旋转/FOV 与录制时一致）
├── 按时间戳间隔逐事件重放
└── 写结果 → 退出
```

## 环境依赖

| 依赖 | 安装 |
|------|------|
| xdotool | `sudo apt install xdotool` |
| x11-utils | `sudo apt install x11-utils`（xwininfo，客户区坐标检测） |
| wmctrl | `sudo apt install wmctrl` |
| ffmpeg | `sudo apt install ffmpeg` 或 `pip install imageio-ffmpeg` |
