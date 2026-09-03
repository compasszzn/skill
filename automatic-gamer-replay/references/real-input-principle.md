# 真实输入驱动原则（通用规范）

## 核心原则

**bot 的所有操作必须通过 OS 层或 Unity Input System 虚拟设备注入，与人类玩家按住鼠标键盘产生的输入事件完全等价。禁止任何绕过输入管线的"内挂直调"。**

## 严格禁止的做法

| 禁止方式 | 为什么禁止 |
|---------|-----------|
| `SendMessage("OnMouseDown")` | 绕过了 Unity 的物理射线+碰撞体判定，能点到人类点不到的位置 |
| 反射调用 `OnButtonPress(item)` | 绕过了 OnGUI/uGUI 的事件系统，人类鼠标不会触发这条路径 |
| 直接调用游戏 manager 方法 | 完全脱离输入管线，回放无法复现 |
| `Input.GetMouseButtonDown` 返回值的 hook/覆盖 | 欺骗 Input 状态读取，非真实输入事件 |

## 必须采用的做法

**所有操作 = 鼠标移动到屏幕坐标 → 按下/释放 → Unity 引擎自行处理射线/UI 事件**

| 操作类型 | 正确注入方式 |
|---------|------------|
| 点击 3D 世界对象 | `Mouse.current.WarpCursorPosition(screenPos)` → `leftButton.Press()` → `Release()`；Unity 自动物理射线 → 命中碰撞体 → 触发 `OnMouseDown` |
| 点击 IMGUI 按钮 | 鼠标移到按钮 rect 中心 → 左键按下/抬起；Unity IMGUI 自行处理 `GUI.Button` 点击 |
| 点击 uGUI 按钮 | 鼠标移到按钮位置 → 左键按下/抬起；EventSystem 自行处理 |
| 右键取消 | `rightButton.Press()` / `Release()`；游戏读 `Input.GetMouseButtonDown(1)` 或 InputSystem action 自然触发 |
| 键盘按键 | `Keyboard.current[xKey].Press()` / `Release()` |
| 鼠标拖拽 | Warp 到起点 → Press → 逐帧 Warp 到终点 → Release |
| 滚轮 | `Mouse.current.scroll.ValueCallback(...)` |

## 人机等价性约束

**如果人类玩家在同样分辨率下把鼠标移到同一个屏幕坐标并点击，得到的结果必须与 bot 完全一致。**

这意味着 bot 必须像人类一样处理所有"点不到"的情况：
- 对象被遮挡 → bot 不能点它，必须等遮挡移走或改变相机角度
- UI 按钮在动画播放中不可交互 → bot 必须等动画结束
- 碰撞体被禁用 → bot 点不到，必须通过其他可达路径操作
- 多单位叠加 → 射线只命中最近的，bot 必须接受这个限制并规划替代策略

## 序列录制与回放

- 录制：每个操作记录屏幕坐标 `(sx, sy)` + 按键/按钮 + 帧索引
- 回放：Warp 鼠标到 `(sx, sy)` → 按下/抬起，不依赖任何游戏内部对象引用
- 等价性验证的标准就是：**人类拿录屏里的鼠标轨迹手动操作，能复现完全相同的结果**
