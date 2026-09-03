# 轨迹回放评估——等价性验证与复刻质量评估工作流

> 步骤6"序列提取与回放验证"的评估参考。核心用途：产出一份可在不同游戏构建之间交叉复现的键鼠轨迹（trajectory），回放到目标游戏（同一游戏/原版/复刻版）上，通过逐层对比结果来量化"内挂执行与序列回放是否一一对应"或"游戏复刻得好不好"。
>
> 一句话评估逻辑：把同一份 `sequence.json` 轨迹喂给目标游戏——如果复刻得足够好（或等价性成立），轨迹应该能**完整走完全部事件**，并得到与录制端**完全一致的终局与逐回合状态**；任何偏差（点击落空 / 菜单错位 / 逻辑分歧）都会让回放中断或结果偏离，即直接暴露缺陷的位置。
>
> 实战验证基线：OpenAW3D（Advance Wars 3D 克隆）91 事件完整对局回放 + mp4 录像（2026-09）。

---

## 评估闭环

```
① 录制端（带内挂的游戏构建）        ② 轨迹产物               ③ 回放端（重新验证）
┌──────────────────────────┐    ┌────────────────┐    ┌─────────────────────────┐
│ 带内挂的游戏自动跑完一局     │    │ sequence.json  │    │ 目标游戏（复刻/原版）上重放 │
│ AGGameAdapter（按游戏适配） │ →  │ （键鼠事件序列）  │ →  │ SequenceReplayer.cs 放入  │
│ AGInputInjector（唯一注入口）│    │ recording.mp4  │    │ Assets/（不改游戏代码）     │
│ xdotool OS 级真实鼠标注入    │    │ battlelog.json │    │ 相机固定到录制时参数         │
│ 固定相机 + 固定 RNG 种子     │    │ （逐回合状态真值）│    │ 场景就绪屏障+客户区坐标自纠错 │
└──────────────────────────┘    └────────────────┘    └──────────┬──────────────┘
                                                              ↓
                                             ④ 逐层对比 → 等价性结论 / 复刻质量结论
                                                done.json / battlelog diff / mp4 对比
```

**为什么轨迹能跨游戏版本**：序列只记录屏幕坐标 + 按键 + 时序，不含任何 InstanceID / 内存地址 / 对象引用。同一份轨迹可以拿到任何一份构建（原版或复刻版）上重放，是天然的"复刻一致性测试用例"。

---

## 轨迹三件套

| 文件 | 内容 | 评估角色 |
|------|------|---------|
| `sequence.json` | 键鼠操作序列：屏幕坐标 `(sx,sy)` + 操作类型 + 时间戳 + 前置等待条件 + meta 语义标注 | 驱动回放的"测试脚本"（示例：`examples/example_sequence.json`，91 事件完整对局） |
| `battlelog.json` | `summary`（result / winner / days / seed 等）+ `turns[]` 逐回合快照（单位/建筑/资源状态） | **逻辑对比真值**（示例：`examples/example_battlelog.json`，11 回合） |
| `recording.mp4` | 录制端完整对局录像（所见即所得） | **视觉对比真值**（人工 / VLM 审查） |

**评估利器 meta 字段**：每个点击都标注了语义目标（如 `"MainMenu/Play"`、`"tile@(4,7)"`、`"BuyMenu/Tank"`）。回放失败时，对照 `done.json` 的中断位置 + meta，一步定位目标游戏在哪个 UI / 单位 / 逻辑上出了问题。

---

## 四层对比指标体系

由浅入深：先看"能不能走完"，再看"结果是否一致"，最后看"画面是否一致"。

| 层级 | 对比对象 | 一致性指标 | 一致 ⇒ 结论 |
|------|---------|-----------|------------|
| ① 执行层 | `done.json`（executed / total） | 全部事件执行、无崩溃、未超时 | 轨迹走通 ⇒ 目标游戏基础可玩、输入管线正确 |
| ② 终局层 | battlelog `summary` | result / winner / 用时 / 回合数与录制端一致 | 核心规则（胜负 / 回合）正确 |
| ③ 状态层 | battlelog `turns[]` 逐回合 diff | 每回合单位位置 / HP / 建筑归属 / 资源逐项相等 | 移动 / 战斗 / 经济等逻辑逐条正确 |
| ④ 视觉层 | `recording.mp4` vs `replay.mp4` | 帧相似度 / VLM 问答：界面布局、单位位置、动画表现 | 表现层（UI 布局 / 美术 / 镜头）也对齐 |

**判"好"的证据链**（以示例轨迹为例）：
- 91/91 事件全部执行（done.json）
- 终局 `red_wins` / 6 天一致（summary）
- 11 回合状态 diff = 0（turns）
- 两段视频逐帧 / VLM 评估一致
- ⇒ 功能、逻辑、表现三层全对齐，等价性成立 / 复刻质量高

**偏差即暴露缺陷**：
- 轨迹中断 / 崩溃 → 基础可玩性缺陷
- 某按钮点不到 → 该 UI 布局 / 交互偏差（meta 字段直接指出是哪个按钮）
- 过程相似但终局不同 → 数值 / 规则差异
- 状态一致但画面不同 → 表现层差异

---

## 完整操作流程

### Step 1 · 录制轨迹（带内挂的游戏构建）

`AGInputInjector.cs` + `AGGameAdapter`（按游戏适配）放入游戏工程后构建，运行：

```bash
AUTOGAMER_AUTO=true ./game -screen-fullscreen 0 -screen-width 800 -screen-height 600
# → 产出 sequence.json + recording.mp4 + battlelog.json
```

### Step 1.5 · 内挂自检 replay（推荐：先确认 sequence 本身没问题）

同一个内挂构建切到 replay 模式，把刚录出的轨迹先重放一遍：

```bash
AUTOGAMER_RUN_MODE=replay AUTOGAMER_SEQUENCE=<output>/sequence.json \
./game -screen-fullscreen 0 -screen-width 800 -screen-height 600
# 由内挂侧 AGSequenceReplayer 逐事件调 AGInputInjector.ReplayClick(sx,sy) 按屏幕坐标重放
```

**自检的意义**：同一构建内"录 → 放"一致 ⇒ 排除录制/坐标/时序问题。之后再在目标游戏上回放，出现的偏差就更能归因于**目标游戏本身**而非轨迹质量问题。

### Step 2 · 在目标游戏上回放（推荐方案B：放入 SequenceReplayer.cs 重新构建）

```bash
./game -screen-fullscreen 0 -screen-width 800 -screen-height 600 \
  -sequence /path/to/sequence.json \
  -camera-pos 7.5,20,8.5 -camera-rot 90,0,0 -camera-fov 60 \
  -camera-disable-script StrategyCamera \
  -replay-output-dir /path/to/output \
  -replay-quit-on-end true \
  -replay-record true -replay-record-fps 15
# → 产出 replay.mp4 + done.json + player.log
```

**务必窗口模式启动**（`-screen-fullscreen 0`）：全屏 Player 不接收 X11 事件；也不要对窗口模式使用 `-replay-force-window`（overrideredirect 会让窗口管理器重摆窗口，几何不稳，见 `sequence-replay-guide.md` 踩坑 #3）。

### Step 3 · 一键脚本（推荐入口）

| 脚本 | 作用 | 用法 |
|------|------|------|
| `scripts/setup_and_run.sh` | 检查并安装 xdotool / x11-utils / ffmpeg；把 SequenceReplayer.cs 复制进目标游戏工程 Assets/Scripts/；引导构建与回放 | `bash scripts/setup_and_run.sh <game_project_dir> <sequence.json> [args...]` |
| `scripts/run_replay.sh` | 启动游戏 → 查找并持续置顶窗口 → 等待回放完成（300s 超时）→ 打印 done.json 与产物清单 | `bash scripts/run_replay.sh <game_exe> <sequence.json> <output_dir> [args...]` |

### Step 4 · 评估对比

1. 读 `done.json`：executed / total 事件数、视频路径、帧数 → 执行完整度
2. 对比 `battlelog.json`：summary 终局 + turns 逐回合 diff → 逻辑一致性（diff 脚本模板见 `sequence-record-replay.md`）
3. 对比两段 mp4：帧 diff / VLM 评估 → 视觉一致性
4. 不一致 → 按下文诊断表区分"环境问题"还是"真缺陷"

---

## 回放方案对比

| 维度 | 方案0：内挂自检 | 方案B：Unity SequenceReplayer.cs | 方案A：Python 外部驱动（replay.py） |
|------|---------------|--------------------------------|----------------------------------|
| 位置 | 内挂构建内（AGSequenceReplayer.cs，bot 工程文件） | 目标游戏 `Assets/` 放入一个 .cs（不改游戏原始代码） | 游戏进程外 |
| 游戏重新构建 | ❌ | ✅ | ❌ |
| 时序精度 | ✅ 帧级 | ✅ 协程帧级 | ⚠️ OS 调度（±10ms） |
| 相机固定 | ✅（同构建） | ✅ 自动按录制参数固定 | ❌ 无法直接控制 |
| 内置录像 | 录制端独立相机 | ✅ ReadPixels → ffmpeg（含 IMGUI） | ❌ 需额外录屏工具 |
| 场景就绪屏障 | ✅（了解游戏内部，最精确） | ✅ sceneLoaded + 相机就绪检测 | ⚠️ 靠 wait 字段 / 截图匹配 |
| 重录序列（输入流对比） | ✅ ReplayClick 经同一注入口 Emit | ❌ | ❌ |
| 适用场景 | 录完先自检轨迹可复现性 | **默认推荐**：可放入代码时 | 目标游戏完全不可修改时 |

方案A 的完整 Python 源码与用法见 `sequence-replay-guide.md`。

---

## 回放不一致诊断表——区分"环境问题"与"真缺陷"

**回放失败 ≠ 一定有问题。** 先按此表排除环境因素，剩下的偏差就是等价性断裂 / 复刻缺陷的直接证据。

| 症状 | 先排查（环境因素） | 若排除环境后仍复现 ⇒ 缺陷定位 |
|------|------------------|------------------------------|
| 所有点击整体偏移约一个标题栏高度（如 29px） | 客户区 vs 外框坐标（踩坑 #1）；窗口几何未刷新 | 通常为环境问题 |
| 场景切换后的第一个点击不响应 / 点错 | 加载时序（踩坑 #2）；方案A 缺 wait 标注 | 通常为环境问题；若屏障后仍失败 → 该场景加载逻辑有偏差 |
| 点击坐标全对不上（整体偏移） | 相机参数 / 分辨率与录制端不一致（`-camera-pos/-rot/-fov`、`-screen-width/height`） | 镜头系统 / UI 适配有偏差 |
| 特定按钮点不到（其余正常） | 按钮是否处于动画/禁用状态（人类也点不到的属正常） | 该 UI 布局 / 交互逻辑有偏差——meta 字段直接指出是哪个按钮 |
| 轨迹走通，但单位不移动 / 行为异常 | 确定性（RNG seed / 时间依赖 / 网络；见 `sequence-record-replay.md` 确定性清单） | 移动 / 战斗 / 规则逻辑有缺陷，用 battlelog 逐回合 diff 定位首个分歧回合 |
| 过程一致，终局不同 | seed 是否一致、speed 是否相同 | 数值公式（伤害 / 资源 / 胜负判定）有缺陷 |
| 状态层全一致，视频画面不同 | 录像帧率 / 编码参数差异 | 表现层（美术 / 动画 / UI 样式）差异，不影响玩法正确性 |

**归因顺序建议**：自检（Run B）先过 → 再跑 Run C → Run C 的偏差先按环境排查 → 环境排除后按四层定位缺陷层（执行/UI/逻辑/表现）→ 用 meta + 首个分歧回合收窄范围。

---

## 坐标链路（从"想点什么"到"OS 点击"的完整传递）

以点击红方坦克 `unit@(2,11)` 为例（示例序列事件 i=2）：

```
① 碰撞体顶部 bounds.center + up×(size.y/2+0.1)，避免被地面/地块遮挡
   → 世界坐标 (2.0, 1.13, 10.93)
② WorldToScreenPoint（固定相机 (7.5,20,8.5) · Euler(90,0,0) · FOV 60 · 800×600）
   → Unity Screen 坐标（左下角原点，Y↑）(248.5, 366.9)
③ 转 X11 绝对坐标：客户区偏移 (5,56) + 缩放比 + Y 轴翻转（Unity Y↑ → X11 Y↓）
   → (253, 289)
④ xdotool 注入 mousemove → mousedown → mouseup，OS 级真实事件
   → Unity 自动物理射线命中 OnMouseDown ✓
```

链路中任何一环不同（相机位置/角度/FOV/分辨率/客户区偏移），②③④ 步全部点错位置——**回放端相机必须逐参数复刻录制端**，这就是 `-camera-*` 参数存在的意义。

### 相机配置记录（由游戏情况确定，随轨迹存档供回放取用）

合适的相机位姿**由游戏情况决定**：相机本身静止的游戏直接用默认位姿；相机跟随/移动的游戏选一个能覆盖全部关键交互元素、固定不抖动的位姿（游戏类型→相机角度参考表见 `sequence-replay-guide.md`）。**录制端确定后把最终采用的配置填入下表随轨迹一起存档**，并写入 `sequence.json` 的 header——回放命令的 `-camera-*` 参数直接按这份记录生成，不依赖人工回忆：

| 参数 | 值（示例轨迹 OpenAW3D） | 回放参数 |
|------|-----|------|
| 相机对象 | `Main Camera`（直接固定原版主相机，不新建） | — |
| 位置 | `(7.5, 20, 8.5)` | `-camera-pos` |
| 旋转 | `Euler(90, 0, 0)`（俯视） | `-camera-rot` |
| FOV | 60 | `-camera-fov` |
| 分辨率 | 800 × 600 | `-screen-width` / `-screen-height` |
| 相机控制脚本 | disabled（`StrategyCamera`） | `-camera-disable-script` |

### 回放端五重自纠错机制（`code/SequenceReplayer.cs` 内置）

1. **客户区坐标修正**：优先 `xwininfo` 取客户区绝对坐标；后备 `xdotool` + `_NET_FRAME_EXTENTS` 修正外框差值
2. **点击前刷新几何**：每次点击前重新检测窗口位置/大小，窗口被 WM 移动也能跟上
3. **场景就绪屏障**：`sceneLoaded` 后等新场景 + 相机固定完成 + 0.3s 缓冲再执行下一事件，杜绝"点击落在旧场景"
4. **窗口模式优先**：`-screen-fullscreen 0` 启动，避免 overrideredirect 引发的几何抖动
5. **窗口持续置顶**：`scripts/run_replay.sh` 每 3s `windowraise` 一次，防止其他窗口遮挡导致点击落空

外加**录像含 IMGUI**：`WaitForEndOfFrame` 读帧缓冲 → ffmpeg（RGBA + vflip），3D 与 OnGUI 界面同时入画，供视觉评估。

---

## 确定性前提（回放可对比的硬条件）

| 条件 | 要求 |
|------|------|
| RNG | 固定种子（序列 header 中 `seed`） |
| 网络 | 无网络交互逻辑 |
| 时间 | 无 `DateTime.Now` / `realtimeSinceStartup` 依赖（unscaledTime 倒计时类逻辑只在结果层要求一致） |
| 物理 | 固定时间步长 |

任一条件不满足，目标游戏与录制端会自然偏离——此时偏离不一定是缺陷，需先排除非确定性因素（完整清单见 `sequence-record-replay.md`）。

## 环境依赖（Linux）

| 依赖 | 安装 | 用途 |
|------|------|------|
| xdotool | `sudo apt install xdotool` | OS 级鼠标/键盘注入（必需） |
| x11-utils | `sudo apt install x11-utils` | xwininfo 客户区坐标检测（强烈推荐） |
| ffmpeg | `sudo apt install ffmpeg` | 回放录像 mp4 编码（视觉评估用） |
| wmctrl | `sudo apt install wmctrl` | 窗口管理（可选） |

跨平台替代：Windows 用 pyautogui（客户区坐标免修正）；macOS 用 cliclick；通用可用 pynput（见 `sequence-replay-guide.md`）。
