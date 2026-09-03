# 键鼠序列提取与回放实现参考（OS 级真实输入版）

> 步骤3编写 bot 代码时，AGGameAdapter（游戏适配层）、AGInputInjector（唯一注入口）、序列录制器、内挂自检回放器（AGSequenceReplayer）四个模块的实现参考；也是步骤6"序列提取与回放验证"的技术规格。目标：内挂 auto 模式的全部操作被编码为键鼠序列 `sequence.json`，脱离决策引擎在（同一游戏/原版/复刻）游戏上回放重新验证。

---

## 核心思想：序列是内挂行为的无损编码

- 内挂（闭环）：读游戏状态 → 决策 → AGGameAdapter 翻译坐标 → AGInputInjector 注入 **OS 级真实鼠标事件**
- 序列回放（开环）：不管状态，按时间表把屏幕坐标逐条重放

**人机等价原则是序列可作验证依据的合法性根基**：所有操作与人类按住鼠标键盘完全等价（禁止 SendMessage/反射直调/直调 manager，见 `real-input-principle.md`）。因此"序列能复现同样结果"同时是：注入层重构后的回归测试、操作审计、纯序列驱动的复现手段、复刻游戏的一致性测试用例。

**前提约束（违反则等价性必然断裂）**：

1. 内挂全程只允许"键鼠可表达"的操作——每条策略规则必须映射到步骤1词汇表原语（见 SKILL.md 步骤2"键鼠原语"列）
2. 阻塞 UI 一律真实点击，禁止逻辑层直调（见 SKILL.md 3.2b）
3. 所有输入事件走 AGInputInjector 唯一注入口，禁止散装输入调用
4. 录制局相机固定 + 确定性运行（seed/fresh run/speed=1，见下文确定性清单）

**推论**：两局只要在任何一帧产生分歧（RNG、帧率、时序），闭环 bot 会自适应改变后续输入，开环回放不会——输入流从此分歧。所以"一一对应是否成立"本质上是游戏确定性的试金石。

---

## 序列文件格式（sequence.json）

**单行紧凑 JSON**（避免日志收集器摊平转义，见步骤5 multiverse 坑）。`header` 记录制环境，`events` 为键鼠事件数组：

```json
{"header":{"game":"Tanks-Hotseat","map":"Scene01","mode":"auto","created":"2026-09-01T12:30:04Z","seed":0,"speed":1.0,"gameVersion":"2022.3.62t8","resolution":"800x600",
  "camera":{"disable_script":"StrategyCamera","position":[7.5,20,8.5],"rotation":[90,0,0],"fieldOfView":60}},
 "events":[
  {"i":0,"frame":5,"t":3.809,"op":"screen_click","meta":"MainMenu/Play (Hotseat Multiplayer)","sx":400,"sy":323,"inject":"os_mouse","wait":"main_menu_visible"},
  {"i":2,"frame":17,"t":4.033,"op":"world_click","meta":"unit@(2,11)","sx":249,"sy":367,"inject":"os_mouse","wait":"game_settled"},
  {"i":4,"frame":64,"t":4.854,"op":"screen_click","meta":"ActionPopupMenu/Wait","sx":304,"sy":259,"inject":"os_mouse","wait":"action_popup_visible"}
 ]}
```

### 字段说明（`code/AGInputInjector.cs` 的 `AGSeqEvent`）

| 字段 | 类型 | 必填 | 说明 |
|------|------|:---:|------|
| `i` | int | ✅ | 事件序号（0 起） |
| `frame` | int | ✅ | `Time.frameCount`——自检回放帧对齐的参考 |
| `t` | float | ✅ | `Time.unscaledTime` 秒——独立回放器按时间戳间隔重放 |
| `op` | string | ✅ | `screen_click` / `world_click` / `gui_click` / `tutorial_click` / `right_click` / `replay_click` / `key_press` / `drag` / `mousemove` / `mousedown` / `mouseup` / `scroll` |
| `meta` | string | ✅ | 人类可读语义目标（如 `"MainMenu/Play"`、`"unit@(2,11)"`、`"tile@(4,7)"`）——回放失败时直接定位"本想点什么" |
| `sx, sy` | int | ✅ | Unity Screen 坐标（左下角原点，Y 向上）——回放执行的唯一依据 |
| `inject` | string | ✅ | 注入方式标记，恒为 `os_mouse` |
| `wait` | string | ❌ | 前置等待条件 id（场景就绪屏障，见下文） |
| `button` / `key` / `amount` | - | 按需 | 鼠标键 / 键名（X11 keysym，如 `Return`）/ 滚轮量 |

### 坐标系约定

`sx, sy` 使用 **Unity Screen 坐标系**（左下角原点，Y 向上）。回放端负责转换为 OS 屏幕坐标：X11 左上角原点、Y 向下、加窗口客户区偏移、按窗口/渲染分辨率缩放比换算。序列**不含任何游戏对象引用**——只有坐标、按键、时序，所以同一份轨迹可以拿到任何一份构建（原版或复刻版）上重放。

---

## 录制实现（auto 模式 = 序列提取）

### 调用链

```
决策引擎
  → yield return AGGameAdapter.ClickUnit(unit, waitId)      ← 游戏适配层（按游戏编写）
  → yield return AGInputInjector.ClickColliderTop(col, meta, waitId)  ← 通用注入器（code/ 直接复用）
  → xdotool mousemove <absX> <absY> mousedown 1 mouseup 1   ← OS 级真实事件
  → Unity 接收 X11 事件生成 Event.current → 物理射线/IMGUI/uGUI 自然响应
  → AGInputInjector.Emit(op, meta, sx, sy, waitId) → OnInjected?.Invoke(e)
  → InputRecorder 订阅 OnInjected，逐事件收集，单局结束写 sequence.json
```

### AGInputInjector 通用接口（不依赖游戏类型，直接复用）

| 接口 | 说明 |
|------|------|
| `ClickScreenPos(sx, sy, meta, waitId)` | 屏幕坐标点击 |
| `ClickWorldPos(worldPos, meta, waitId)` | 世界坐标点击（自动 WorldToScreenPoint） |
| `ClickColliderTop(col, meta, waitId)` | 碰撞体顶部点击（`bounds.center + up*(size.y/2+0.1)`，避免被地面/地块遮挡） |
| `ClickColliderCenter(col, meta, waitId)` | 碰撞体中心点击 |
| `RightClick(waitId)` | 右键（取消） |
| `PressKey(key, waitId)` | 键盘按下→释放 |
| `Drag(fromX, fromY, toX, toY, meta, waitId)` | 拖拽（逐帧移动） |
| `ReplayClick(sx, sy, button, waitId)` | 回放专用：按屏幕坐标重放（方案0 的执行接口） |
| `WorldToScreen(worldPos)` | 坐标转换工具 |

所有接口都是 `IEnumerator`（xdotool 调用需要多帧：移动→点击→等待引擎处理），调用方必须 `yield return`。

### AGGameAdapter 游戏适配层（按游戏替换）

把游戏对象翻译为通用注入器能理解的坐标，并附语义 meta。示例（坦克游戏，完整代码在 `code/AGInputInjector.cs` 下半部分）：

```csharp
public static class AGGameAdapter
{
    public static IEnumerator ClickUnit(Unit unit, string waitId)
    {
        var col = unit.GetComponent<Collider>();
        var meta = $"unit@{unit.TilePosition()}";
        if (col != null) yield return AGInputInjector.ClickColliderTop(col, meta, waitId);
        else yield return AGInputInjector.ClickWorldPos(unit.transform.position + Vector3.up * 1.2f, meta, waitId);
    }

    public static IEnumerator ClickTile(Point tile, string waitId)
        => AGInputInjector.ClickWorldPos(new Vector3(tile.x, 0.5f, tile.y), $"tile@({tile.x},{tile.y})", waitId);

    // IMGUI 菜单按钮：反射只读 Rect/Items/ButtonHeight 计算单按钮中心（点整框中心会命中错误按钮）
    public static IEnumerator ClickMenuButton(Menu menu, string item, string waitId) { ... }
}
```

### 录制规则（保证可回放）

- **唯一注入口**：所有输入事件必须经 AGInputInjector——绕过注入口的操作既让序列缺失对应项，也违反人机等价原则
- **记录"实际写进输入系统的东西"**：屏幕坐标 `(sx, sy)`，不是内部决策值（如"移动意图向量"）——序列必须能脱离 bot 单独驱动游戏
- **相机固定（位姿由游戏情况决定，确定后必须记录）**：录制局主相机处于确定位姿（禁用会移动相机的脚本）。**位姿选择由游戏情况决定**——相机本身静止的游戏直接用默认位姿；相机跟随/移动的游戏选一个能覆盖全部关键交互元素、固定不抖动的位姿（2D 棋盘/回合制 → 俯视全图；3D 场景 → 透视俯视 60°，参考 `sequence-replay-guide.md` 的游戏类型→相机角度表）。**最终采用的相机配置（disable_script / position / rotation / fieldOfView / 分辨率）固化后写入序列 header**——回放端从 header 读取这些值生成 `-camera-pos/-camera-rot/-camera-fov/-camera-disable-script` 命令行，不靠人工回忆；否则 world→screen 映射不同，点击全偏
- **wait 屏障标注**：场景切换后的事件标注 `wait`（如 `main_menu_visible` / `game_settled` / `action_popup_visible`）——回放端在场景切换处等就绪再执行
- **时序双记录**：`t`（unscaledTime，独立回放器用）+ `frame`（frameCount，自检回放帧对齐用）
- **单行紧凑 JSON + 原子写入**：先写临时文件再 rename（与 sweep 结果文件一致）；文件过大时分段写入
- **header 完整**：game / map（游戏主场景名，回放器场景就绪屏障用）/ seed / speed / 分辨率 / 相机参数

---

## 回放实现（三种方案）

### 方案0：内挂自检回放（AGSequenceReplayer，bot 工程内实现）

**用途**：Run A 录完 sequence 后，先在同一构建内自检"录 → 放"一致，排除轨迹本身的问题；之后再在目标游戏上回放，偏差才能归因于目标游戏。自检局同时会**重录 `sequence_B`**（ReplayClick 经同一注入口，每条也 Emit），供输入流层对比。

```bash
# 同一内挂构建切到 replay 模式（决策引擎完全禁用）
AUTOGAMER_RUN_MODE=replay AUTOGAMER_SEQUENCE=<output>/sequence.json \
./game -screen-fullscreen 0 -screen-width 800 -screen-height 600
```

实现要点：

```csharp
// AGSequenceReplayer（bot 工程内，随步骤3一起编写）
// - run-mode=replay 时激活；决策引擎全部禁用（任何"顺手帮忙"的兜底决策都会污染对比）
// - fresh run / 自动开局 / 终局退出等工程框架照常工作（开局动作不是序列的一部分）
// - 逐事件：按 t 时间戳间隔（或 frame 对齐）→ AGInputInjector.ReplayClick(sx, sy, button, waitId)
//   ——按屏幕坐标重放，不解析任何游戏对象
// - 某帧事件多于一个时按序全部注入；回放帧号越过下一事件 frame 超阈值（>30 帧）时记录警告
// - 序列结束/游戏结束即退出进程，与 auto 模式一致
```

### 方案B：独立回放器（推荐）—— `code/SequenceReplayer.cs` + `scripts/run_replay.sh`

**用途**：在（复刻/原版）目标游戏上回放，验证序列脱离内挂可复现。单文件放入目标游戏 `Assets/`（**不改游戏原始代码**），`RuntimeInitializeOnLoadMethod` 自动启动，不依赖任何游戏类型。已实战验证（OpenAW3D 91 事件完整对局 + mp4 录像，2026-09）。

内置能力：窗口客户区检测（xwininfo 优先 + `_NET_FRAME_EXTENTS` 后备）、每次点击前刷新几何（自纠错）、场景就绪屏障（`sceneLoaded` → 相机固定 → 0.3s 缓冲）、相机按参数固定、屏幕捕获录像（`WaitForEndOfFrame` + ReadPixels → ffmpeg，**同时捕捉 3D 与 IMGUI**）、结果写 `done.json`。

**命令行参数速查**（均有等价环境变量 `REPLAY_*`）：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `-sequence <path>` | — | 序列文件路径（必填，缺失则回放器不激活） |
| `-camera-pos x,y,z` | `7.5,20,8.5` | 相机位置，**必须与录制时一致** |
| `-camera-rot x,y,z` | `90,0,0` | 相机旋转，**必须与录制时一致** |
| `-camera-fov N` | `60` | 相机 FOV，**必须与录制时一致** |
| `-camera-disable-script <Type>` | — | 禁用会移动相机的脚本（如 `StrategyCamera`） |
| `-replay-window-title` | productName | 窗口标题（查找窗口用） |
| `-replay-force-window WxH` | — | 强制窗口大小（**仅游戏全屏启动时兜底**，窗口模式勿用） |
| `-replay-output-dir <dir>` | 序列同目录 | 产物输出目录 |
| `-replay-quit-on-end true` | true | 回放结束自动退出进程 |
| `-replay-record true` | true | 录像开关（需 ffmpeg） |
| `-replay-record-fps N` | 15 | 录像帧率 |
| `-replay-record-tail N` | 3 | 回放结束后续录秒数（捕捉结算画面） |

**一键脚本用法**：

```bash
# 一键部署（检查/安装依赖 + 复制 SequenceReplayer.cs 进目标游戏工程 + 引导构建）：
bash scripts/setup_and_run.sh <game_project_dir> <sequence.json> [replayer_args...]

# 一键回放（启动游戏→置顶窗口→等待完成→打印产物）：
bash scripts/run_replay.sh <game_exe> <sequence.json> <output_dir> \
  -camera-pos 7.5,20,8.5 -camera-rot 90,0,0 -camera-fov 60 \
  -camera-disable-script StrategyCamera
# 产物：<output_dir>/done.json + replay.mp4 + player.log
```

`run_replay.sh` 行为：检查 xdotool/xwininfo/ffmpeg → 以**窗口模式**（`-screen-fullscreen 0 -screen-width 800 -screen-height 600`）启动游戏并传 `-sequence`/`-replay-*` 参数 → 按可执行文件名或 PID 查找窗口并**每 3s 持续置顶**（防其他窗口遮挡导致点击落空）→ 轮询 `done.json`（300s 超时，游戏退出即止）→ 收尾 kill 残留进程并打印产物清单。

`done.json` 结果格式：

```json
{"status":"complete","executed":91,"total":91,"duration_s":124.5,"video":"/path/to/replay.mp4","frames":1868}
```

### 方案A：Python 外部驱动（目标游戏完全不可修改时）

游戏进程外用 xdotool 重放，零代码修改、无需重新构建。**完整 `replay.py` 源码、窗口几何检测、坐标转换见 `sequence-replay-guide.md`**。要点：时序精度受 OS 调度影响（±10ms）、无法固定相机、无场景就绪屏障（依赖序列 `wait` 字段 + 截图匹配）、需额外录屏工具。

---

## 验证流程（步骤6 的"序列提取与回放验证"）

```
Run A: -run-mode auto  -seed s → sequence_A + battlelog_A + recording_A.mp4
Run B: -run-mode replay -sequence sequence_A -seed s（内挂自检）→ battlelog_B + sequence_B（重录）
Run C: 目标游戏 + SequenceReplayer（scripts/run_replay.sh）→ done.json + replay.mp4
```

对比层级（完整指标体系与判定标准见 `replay-evaluation.md`）：

1. **输入流层**（Run A vs Run B）：对比 sequence_B 与 sequence_A 的"实际重放的坐标/按键流"（`sx/sy/button/key/t` 序列）——录制/回放走同一注入口时构造上必然一致；**忽略 `op`/`inject` 等来源标注字段**（`screen_click` vs `replay_click` 是同一操作的不同来源标记）。不一致 → 注入路径混用或存在非键鼠可表达操作
2. **执行层**（Run C）：`done.json` 的 executed/total——事件全部执行、无崩溃、未超时
3. **终局层**：battlelog summary（胜负/用时/回合数）一致
4. **状态层**：battlelog 逐回合快照 diff → 报告**首个分歧回合**（首个分歧点之前两边完全一致，排查范围缩到该回合附近活跃的系统）
5. **视觉层**：recording.mp4 vs replay.mp4，帧相似度 / VLM 问答

### diff 脚本模板

```python
import json, sys

def sig(e, fields):
    return json.dumps({k: e.get(k) for k in fields}, sort_keys=True)

a = json.load(open(sys.argv[1]))["events"]
b = json.load(open(sys.argv[2]))["events"]

# 输入流层：只比较玩家操作字段（忽略 op/inject 等来源标注）
fields = ["sx", "sy", "button", "key", "t"]
if len(a) != len(b):
    print(f"length mismatch: A={len(a)} B={len(b)}")
for i, (ea, eb) in enumerate(zip(a, b)):
    if sig(ea, fields) != sig(eb, fields):
        print(f"first input divergence at index {i}:")
        print("  A:", sig(ea, fields)); print("  B:", sig(eb, fields)); break
else:
    print("input-stream match:", len(a) == len(b))
```

battlelog diff：按事件的 `(回合/时间, 类型)` 索引对齐，报告首个不一致条目及两侧上下文（前后各 3 条），供"联合诊断"定位是哪个系统先偏离。

---

## 确定性清单（回放可对比的前提）

| # | 检查项 | 说明 |
|---|--------|------|
| 1 | 固定随机种子 | run 启动时 `UnityEngine.Random.InitState(seed)` + 所有 `System.Random(new FixedSeed)`；seed 走命令行参数并写入序列 header 与 battlelog |
| 2 | fresh run | 清存档续玩进度（关卡时间/经验/能力），两局初始状态一致（见 SKILL.md 3.2c） |
| 3 | 同 speed / timeScale | 等价性验证统一用 speed=1；注意 3.2d 的 timeScale 复位陷阱 |
| 4 | 逻辑在 FixedUpdate | 逻辑走固定步长的游戏可严格复现；逻辑在 `Update`（可变帧率）的游戏只能近似对应——帧率波动造成轨迹微小偏差，属预期内的"统计等价" |
| 5 | unscaledTime 依赖 | 真实时间冷却/倒计时天然破坏对应（两局墙钟节奏不同）；独立回放器按 `t` 时间戳间隔重放，此类逻辑存在时只在结果层要求一致 |
| 6 | 光标状态 | `Cursor.lockState` 锁定模式下鼠标移动无效，注入前检查并记录 |
| 7 | 运行方式 | **窗口模式（X11 可见窗口）**——注入与回放都依赖可见窗口，`-batchmode -nographics` 不可用；无人值守用 Xvfb 虚拟显示；需要真实视频时窗口模式本身满足 |
| 8 | 协程/动画 | `WaitForSeconds` 基于 scaled time，与 timeScale 一致即可复现；`WaitForSecondsRealtime` 属于第 5 条 |
| 9 | 并发 | 等价性验证单实例跑（concurrency=1）；本地多实例时窗口不可互相遮挡（xdotool 点击落在光标下的窗口），或每实例独立 DISPLAY |

> 判定口径：**输入流层必须严格一致；执行/终局/状态层在满足清单 1-9 后应一致，`Update` 逻辑游戏允许帧级微小漂移，但胜负与关键事件序列必须一致。**

---

## 常见坑

**注入与坐标类**（实战验证，详见 `sequence-replay-guide.md` 与 `real-input-migration.md`）：

- **客户区 vs 外框坐标（最致命）**：`xdotool getwindowgeometry` 返回含标题栏的**外框**坐标，点击必须相对**客户区**——差一个标题栏高度（实测 xfwm4：外框 (10,85) vs 客户区 (5,56)，`_NET_FRAME_EXTENTS=5,5,29,5`）会让所有点击整体下移 29px，点 "Play" 实际点到 "Quit"。→ 优先 `xwininfo`（直接返回客户区绝对坐标），后备 `xdotool` + `xprop _NET_FRAME_EXTENTS` 修正
- **场景切换后时序过快**：录制时（Editor 热启动）场景 0.1s 加载完，回放时（standalone 冷启动）需 1-2s，下一点击落在旧场景。→ 方案B 的场景就绪屏障（`sceneLoaded` + 相机固定 + 0.3s 缓冲）；方案A 靠 `wait` 字段标注切换点
- **overrideredirect 几何不稳**：设置 overrideredirect 后 WM 会重摆窗口，坐标短时间跳变。→ 窗口模式启动优先，overrideredirect 仅全屏兜底且设置后等 1s
- **全屏 Player 不接收 X11 事件**：xdotool 点击无效、`Screen.SetResolution` 不改变窗口物理大小。→ 一律 `-screen-fullscreen 0` 启动
- **xdotool 阻塞主线程**：`WaitForExit` 同步阻塞会让协程死锁。→ `WaitForExit(500)` 短超时 + `ClickAt` 协程多个 `yield return null`
- **IMGUI 按钮中心**：`Menu.Rect` 是整框，点框中心命中错误按钮。→ 反射只读 `Items` 索引 + `ButtonHeight` 计算单按钮中心（`AGGameAdapter.ClickMenuButton`）

**录制与回放类**：

- **录制时记录了决策值而不是玩家操作**：序列必须记录"实际写进输入系统的屏幕坐标"，不是内部意图
- **相机不一致**：录制端相机被移动过/回放端未固定 → `WorldToScreenPoint` 结果不同，点击全偏。相机参数写入 header，回放端逐参数复刻
- **replay 局里决策引擎没禁干净**：某个兜底逻辑"帮忙"做了选择，序列里却没有对应事件 → 输入流对比抓不出来（重录序列与回放一致），必须靠状态层 diff 发现
- **开局差异**：自动开局的耗时/帧数两局不同 → 序列第 0 帧应对齐"对局开始"，开局前事件（进主菜单、选关卡）也须入序列并带 `wait` 屏障
- **速度感知错位**：speed>1 时帧号与 unscaled 时间的对应关系变化——等价性验证统一 speed=1
- **窗口被遮挡**：xdotool 点击落在光标下的窗口——run_replay.sh 持续置顶；本地并发实例窗口错开摆放
- **multiverse 模式回放**：序列随 battle log 一样可能被日志收集器摊平转义——抓取时兼容三种形态（见步骤5）；replay 局的 allocation 需传入序列文件（挂载或打包进镜像），且容器需 X11 虚拟显示
