# 真实输入改造：做法、经验与教训

## 目标

将 AutoGamer 的注入方式从"内挂直调"（SendMessage/反射 OnButtonPress）改为**真实 OS 级鼠标输入**，使 bot 的所有操作与人类玩家按住鼠标键盘完全等价。

规范文档：`AutoGamerOutput/RealInputPrinciple.md`

## 架构变化

### 之前（SendMessage 方式）

```
决策引擎 → AGInputInjector.WorldClickUnit(unit)
  → unit.gameObject.SendMessage("OnMouseDown")     ← 绕过物理射线
  → AGInputInjector.GuiClickMenu(menu, "Capture")
  → 反射调用 menu.OnButtonPress("Capture")          ← 绕过 IMGUI 事件系统
```

### 现在（OS 级鼠标方式）

```
决策引擎 → yield return AGInputInjector.WorldClickUnit(unit)
  → Camera.WorldToScreenPoint(unit.transform.position)  ← 算屏幕坐标
  → xdotool mousemove <absX> <absY> click 1              ← OS 级真实鼠标事件
  → Unity 引擎接收 X11 事件 → 自动生成 Event.current
  → IMGUI GUI.Button 检测 click → 触发回调               ← 与人类点击等价
  → Physics.Raycast → OnMouseDown                        ← 与人类点击等价
```

## 关键技术决策

### 1. 为什么用 xdotool 而非 Unity InputSystem

| 方案 | 问题 |
|------|------|
| InputSystem StateEvent (WriteValueIntoEvent) | 不生成 IMGUI 的 `Event.current`，`GUI.Button` 不响应 |
| InputSystem WarpCursorPosition | 只移动光标位置，不生成点击事件 |
| `activeInputHandler=Both` 桥接 | 只桥接 `Input.GetMouseButtonDown` 等旧 API，不桥接 IMGUI Event |
| xdotool (XSendEvent) | OS 级事件，Unity 自动生成 `Event.current`，IMGUI/uGUI/物理射线全部接收 ✅ |

**核心认知**：Unity IMGUI (OnGUI) 的 `GUI.Button` 只响应 `Event.current`，而 `Event.current` 由引擎从 OS 窗口事件生成。InputSystem 的虚拟设备状态写入不会生成 IMGUI Event。必须通过 OS 级真实鼠标事件驱动。

### 2. Tuanjie Player 全屏模式不接收 X11 事件

Tuanjie/Unity Player 在 Linux 上默认全屏（`fullscreenMode=1`），全屏模式下：
- xdotool 的 `click` 不触发任何 Unity 事件
- xte (XTest extension) 同样无效
- `xdotool windowsize` 无法改变窗口大小
- `Screen.SetResolution` / `Screen.fullScreen=false` 不改变窗口物理大小
- `PlayerSettings.fullscreenMode=0` 在构建后不生效

**解决方案**：用 `xdotool set_window --overrideredirect 1 <WID>` 强制取消窗口管理器装饰，然后 `xdotool windowsize <WID> 800 600` 强制调整窗口大小。这是唯一在 Tuanjie Player 上生效的方法。

### 3. 坐标系映射

Unity Screen 坐标和 X11 屏幕坐标有三重差异：

| 维度 | Unity Screen | X11 屏幕 |
|------|-------------|---------|
| 原点 | 左下角 | 左上角 |
| Y 轴 | 向上 | 向下 |
| 分辨率 | `Screen.width/height`（渲染分辨率） | 窗口物理大小（可能不同） |

转换公式：
```
scaleX = windowWidth / Screen.width
scaleY = windowHeight / Screen.height
absX = windowX + unityX * scaleX
absY = windowY + (Screen.height - unityY) * scaleY
```

### 4. IMGUI 按钮中心计算

`Menu.Rect` 是整个菜单框的位置，不是单个按钮的位置。点击菜单框中心可能命中错误的按钮（如点到 Quit 而非 Play）。

**解决方案**：反射读取 `Menu.Items` 列表找到按钮索引 + `Menu.ButtonHeight`，计算单按钮中心：
```
buttonCenterY_IMGUI = rect.y + 4 + buttonIndex * buttonHeight + buttonHeight / 2
buttonCenterY_Screen = Screen.height - buttonCenterY_IMGUI
```

### 5. xdotool 阻塞 Unity 主线程

xdotool 的 `WaitForExit` 在 Unity 主线程上同步阻塞，导致协程无法推进帧 → 死锁。

**解决方案**：
- `WaitForExit(500)` 短超时（xdotool 通常 <100ms 完成）
- ClickAt 协程中多个 `yield return null` 确保引擎有足够帧处理 IMGUI 事件

### 6. 所有注入方法改为协程

由于 xdotool 调用需要多帧（移动→点击→等待处理），所有注入器方法从 `void` 改为 `IEnumerator`，调用方用 `yield return`：
```csharp
// 之前
AGInputInjector.WorldClickUnit(unit, waitId);

// 现在
yield return AGInputInjector.WorldClickUnit(unit, waitId);
```

## 已验证可工作的操作

| 操作 | 状态 | 说明 |
|------|------|------|
| MainMenu Play 按钮 | ✅ | xdotool 点击 IMGUI GUI.Button 中心 → Application.LoadLevel |
| 教程全屏按钮 | ✅ | xdotool 点击屏幕中心 → HideTutorial + ShowDayNo |
| 坦克选中（OnMouseDown） | ✅ | xdotool 点击坦克世界坐标 → 物理射线 → OnMouseDown |
| 坦克移动（Tile.OnMouseDown） | ✅ | xdotool 点击地块 → 物理射线 → MoveToTile |
| 行动菜单按钮（Wait/Capture/Fire） | ✅ | xdotool 点击 IMGUI 按钮中心 → OnButtonPress |
| 购买建筑（Building.OnMouseDown） | ⚠️ | 建筑 Collider 被坦克占据时禁用 → xdotool 点不到（与人类一致） |

## 待解决

1. **购买建筑坐标精度**：坦克移走后点击基地格，但坐标可能有偏差（相机角度导致 WorldToScreenPoint 不精确）。需要验证点击位置是否在建筑碰撞体范围内。

2. **回放模式**：replay 模式当前按序列记录的屏幕坐标 `(sx, sy)` 重放。由于窗口位置/大小可能跨运行不同，回放需要在运行时重新检测窗口几何并应用相同缩放。

3. **Editor Play Mode**：Editor 中 Game View 嵌在编辑器窗口中，xdotool 需要点击编辑器窗口的 Game View 区域。坐标映射更复杂（需要知道 Game View 在编辑器窗口中的偏移）。当前方案在 Player 中工作，Editor 中未验证。

4. **右键取消**：已实现 `RightClick` 方法（xdotool click button 3），但未在策略中使用（策略设计为零取消依赖）。如需使用可直接 `yield return AGInputInjector.RightClick(waitId)`。

## 代码文件变更

| 文件 | 变更 |
|------|------|
| `AGInputInjector.cs` | 完全重写：移除 SendMessage/反射调用，改为 xdotool OS 级鼠标注入 + 协程式 |
| `AGReflection.cs` | 移除 `InvokeOnButtonPress`/`InvokeTutorialDismiss`，保留 `GetMenuRect`/`GetTutorialVisible`（只读状态） |
| `AGTurnEngine.cs` | 所有注入器调用改为 `yield return` |
| `AGSequenceReplayer.cs` | 改为按屏幕坐标 `ReplayClick(sx, sy)` 重放，不解析游戏对象 |
| `AGBootstrap.cs` | AutoClickPlay 改为 `yield return` |

## 环境依赖

- `xdotool`：OS 级鼠标注入（`sudo apt install xdotool`）
- `xautomation` (xte)：备选方案（XTest extension），当前未使用
- `ffmpeg`：视频录制（`imageio-ffmpeg` 静态二进制）
- `com.unity.inputsystem` 1.14.3：已安装但当前未用于注入（仅 `activeInputHandler=Both` 桥接旧 API）
- Player 窗口模式：`xdotool set_window --overrideredirect 1` + `xdotool windowsize 800 600` 强制

## 运行方式

```bash
# Player（窗口模式 + xdotool 真实鼠标）
AUTOGAMER_AUTO=true ./Builds/TanksAutoGamer -screen-width 800 -screen-height 600

# 回放
AUTOGAMER_RUN_MODE=replay \
AUTOGAMER_SEQUENCE=<output>/sequence.json \
./Builds/TanksAutoGamer -screen-width 800 -screen-height 600
```

Player 启动后 AutoGamer 会自动用 xdotool 强制窗口为 800x600，然后所有操作通过 xdotool 真实鼠标点击驱动。
