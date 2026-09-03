---
name: automatic-gamer-replay
description: 基于 Unity 游戏代码构建自动化游戏助手（内挂 bot）——内挂的全部操作经 OS 级真实鼠标注入（xdotool）自动游玩关卡、收集战斗数据、验证关卡可通过性与难度曲线，并从唯一注入口提取键鼠操作序列（sequence.json）；随后用 SequenceReplayer 在同一游戏或复刻游戏上回放序列进行重新验证，通过四层对比（执行/终局/状态/视觉）验证内挂执行与序列回放的等价性、或量化游戏复刻质量。当用户提到自动化游戏、自动游玩、游戏测试bot、关卡平衡测试、游戏AI助手、auto play、game automation、Unity游戏bot、关卡自动通关、run sweep、关卡难度曲线、战斗log分析、VLM视频分析、内挂、序列提取、sequence extract、键鼠操作序列、序列录制与回放、sequence replay、轨迹回放、真实鼠标注入、xdotool、AGGameAdapter、SequenceReplayer、等价性验证、复刻游戏质量评估、游戏复刻验证时使用此skill。即使只是想"让游戏自己跑起来看看"或"验证复刻游戏复刻得好不好"，也应触发此skill。
---

# 自动化游戏助手构建 + 键鼠轨迹提取与回放验证

## Overview

在 Unity 中构建自动化游戏助手。核心价值：用代码规则模拟玩家决策，反复自动游玩关卡，收集战斗数据，帮助开发者验证关卡是否可通过、难度曲线是否合理——这些是人工测试难以高效覆盖的。

在此基础上，本 skill 把 bot 的全部操作改为 **OS 级真实键鼠注入**，并内建**轨迹提取与回放验证**闭环：

- **真实输入**：bot 的每个操作经 `AGInputInjector`（唯一注入口）用 xdotool 注入 OS 级真实鼠标/键盘事件——与人类按住鼠标键盘产生的输入完全等价。禁止 SendMessage / 反射直调 / 直调 manager 等绕过输入管线的"内挂直调"（人机等价原则，见 `references/real-input-principle.md`）
- **序列提取（extract）**：内挂运行时，决策引擎 → `AGGameAdapter`（游戏适配层，把单位/地块/建筑/菜单翻译为坐标）→ `AGInputInjector` 注入并逐事件 `Emit` `AGSeqEvent`，录制为 `sequence.json`——只含屏幕坐标 + 时序 + 语义标注，不含任何游戏对象引用/InstanceID，天然跨构建可复现
- **回放验证（re-validate）**：提取出的序列用 `SequenceReplayer`（独立回放器，`code/SequenceReplayer.cs`，单文件放入目标游戏不改源码）回放重新验证，`scripts/run_replay.sh` 一键启动。序列复现同样结果 ⇒ 内挂行为被无损编码（等价性成立）；在复刻游戏上回放则量化复刻质量，任何偏差直接暴露复刻缺陷的位置

> **注意**：构建过程中任何涉及到写文件的操作，当文件过大时，可以分段写入文件

## 前置要求

- 限定 Unity 游戏
- 构建自动化游戏助手需要有游戏本身的全部代码，用来理解游戏逻辑和关卡
- **Linux + X11 图形环境**（真实鼠标注入与序列回放都依赖可见窗口）：
  - 系统依赖：xdotool / x11-utils（xwininfo、xprop）/ ffmpeg（录像）/ wmctrl（可选）
  - 安装：`sudo apt install xdotool x11-utils ffmpeg wmctrl`（一键检查安装见 `scripts/setup_and_run.sh`）
  - **窗口模式启动是硬要求**：`-screen-fullscreen 0 -screen-width 800 -screen-height 600`——全屏 Player 不接收 X11 事件（实测结论，见 `references/real-input-migration.md`）
  - 无显示器的服务器用 Xvfb 虚拟显示：`Xvfb :1 -screen 0 1280x1024x24 & export DISPLAY=:1`
- 回放验证的目标游戏（复刻版/原版）**不需要提供源码**——能把 `code/SequenceReplayer.cs` 放进其 `Assets/` 重新构建即可；完全不可修改时用 Python 外部驱动（方案A，见 `references/sequence-replay-guide.md`）

## skill 自带资产

| 路径 | 用途 |
|------|------|
| `code/AGInputInjector.cs` | **通用注入器（唯一注入口）**：xdotool OS 级注入 + 坐标转换 + 窗口客户区检测。`OnInjected` 事件逐条 Emit `AGSeqEvent` = sequence.json 的录制来源；`ReplayClick` = 内挂自检回放（方案0）的执行接口。文件下半部分含坦克游戏 `AGGameAdapter` 示例（按游戏替换） |
| `code/AGGameAdapter_Example.cs` | 游戏适配层示例说明：把 Unit/Tile/Building/MenuButton 翻译为通用注入器能理解的世界/屏幕坐标 |
| `code/SequenceReplayer.cs` | **独立序列回放器（方案B）**：单文件放入任意 Unity 工程 `Assets/`，不改游戏源码；相机固定 + 场景就绪屏障 + 客户区坐标自纠错 + 内置 ffmpeg 录像 + done.json。已实战验证（OpenAW3D 91 事件完整对局，2026-09） |
| `scripts/setup_and_run.sh` | 一键部署：检查/安装依赖 → 复制 SequenceReplayer.cs 进目标游戏工程 → 引导构建与回放 |
| `scripts/run_replay.sh` | 一键回放：启动游戏（窗口模式）→ 查找并持续置顶窗口 → 等待 done.json（300s 超时）→ 打印产物清单 |
| `examples/example_sequence.json` | 示例轨迹：91 事件完整对局（屏幕坐标 + 时序 + meta 语义标注 + wait 屏障，seed=0） |
| `examples/example_battlelog.json` | 示例战报真值：summary 终局 + 逐回合状态快照（回放对比的基准） |
| `references/real-input-principle.md` | 真实输入规范：禁止内挂直调、必须 OS 级事件、人机等价性约束（序列可作为验证依据的合法性根基） |
| `references/real-input-migration.md` | 改造实录：SendMessage → xdotool 的 6 大关键技术决策与已验证操作清单 |
| `references/real-input-overview.md` | 注入 skill 总览：文件清单 + 快速使用 + 接口说明 |
| `references/sequence-replay-guide.md` | 回放深版：序列 schema、Python 外部驱动源码（方案A）、录像方案、相机规格、跨平台方案 |
| `references/sequence-record-replay.md` | 录制与回放技术规格：唯一注入口、录制规则、三种回放方案、确定性清单、常见坑 |
| `references/replay-evaluation.md` | 回放评估工作流：轨迹三件套、四层对比指标、诊断表（环境问题 vs 复刻缺陷） |

## 核心工作流：内挂提取 → 回放验证

```
① 录制端（带内挂的游戏构建）           ② 轨迹产物             ③ 回放端（重新验证）
┌────────────────────────────┐   ┌────────────────┐   ┌──────────────────────────┐
│ 决策引擎 → AGGameAdapter     │   │ sequence.json  │   │ SequenceReplayer.cs 放入  │
│  （对象 → 世界/屏幕坐标）     │ → │ （键鼠事件序列） │ → │ 目标游戏 Assets/（不改源码）│
│ → AGInputInjector（唯一注入口）│   │ recording.mp4  │   │ 相机固定到录制时参数        │
│  xdotool OS 级真实鼠标注入    │   │ battlelog.json │   │ 场景就绪屏障 + 客户区自纠错 │
│  OnInjected 逐事件 Emit      │   │ （逻辑真值）    │   └──────────┬───────────────┘
└────────────────────────────┘                          ↓
                                            ④ 四层对比评估（references/replay-evaluation.md）
                                               执行层 │ 终局层 │ 状态层 │ 视觉层
```

回放验证的三种模式（由近及远，逐级排除归因）：

| 模式 | 驱动方式 | 验证什么 | 启动方式 |
|------|---------|---------|---------|
| **方案0 内挂自检** | 同一内挂构建切 replay 模式，经 `AGInputInjector.ReplayClick(sx,sy)` 按屏幕坐标重放 | 排除轨迹本身的问题（坐标/时序/录制缺陷）——自检一致后，后续偏差才能归因于目标游戏 | `AUTOGAMER_RUN_MODE=replay AUTOGAMER_SEQUENCE=<sequence.json> ./game -screen-fullscreen 0 -screen-width 800 -screen-height 600` |
| **方案B 独立回放器（推荐）** | `SequenceReplayer.cs` 放入目标游戏（复刻/原版）工程重新构建 | 序列在"无决策引擎"游戏上可复现 → 等价性成立 / 复刻质量 | `bash scripts/run_replay.sh <game_exe> <sequence.json> <output_dir> -camera-pos ... -camera-rot ... -camera-fov ... -camera-disable-script ...` |
| **方案A Python 外部驱动** | 游戏进程外用 xdotool 重放（`references/sequence-replay-guide.md` 的 replay.py） | 目标游戏完全不可修改、不能放入代码时 | `python3 replay.py --sequence sequence.json --window "Game Title" --executable "./game -screen-fullscreen 0 ..."` |

**为什么轨迹能跨构建复现**：序列只记录屏幕坐标 + 按键 + 时序，不含任何 InstanceID / 内存地址 / 对象引用。同一份轨迹可以拿到任何一份构建（原版或复刻版）上重放，是天然的"一致性测试用例"。

---

## 步骤总览

本 skill 包含 8 个主步骤（步骤0-6，其中0.5为最小可行bot验证）和 6 个验证评估子步骤（步骤1拆为3个模块各有独立评估，步骤2/3/6各1个评估）。每个验证评估子步骤使用 Agent 工具启动一个评估子 agent（subagent_type: general-purpose）审查产出质量，确保分析不准、策略不适、代码有误等问题在早期被发现，而不是在后面跑完 sweep 才暴露。

验证评估子步骤的使用规则：
- 评估子 agent 必须拿到完整的产出内容和游戏代码目录路径，才能交叉验证
- 发现关键错误（遗漏重要敌人、攻击方式与代码不一致、引用不存在类、策略完全误判）时，必须返回主流程修正后再继续后续步骤
- 次要建议（措辞改进、数值精度不够）可以记录但不阻塞主流程

步骤0是 git init，确保所有后续改动都有版本记录。步骤6是全自动迭代测试循环——包含两阶段（Phase 1 策略验证、Phase 2 稳定性验证），使用 VLM 视频分析 + 战斗 log 联合诊断。每轮内含评估环节，发现分析不准或优化方向不可执行时修正后再继续优化。循环持续到用户设定的目标达成。

此外，bot 的全部操作经 **AGInputInjector 唯一注入口**以 OS 级真实鼠标注入（与人类键鼠操作完全等价）。auto 模式运行时逐事件录制键鼠操作序列 `sequence.json`；序列可通过三种方式回放重新验证——内挂自检（replay 模式禁用决策引擎、按屏幕坐标重放）、独立 SequenceReplayer（放入原版/复刻游戏，`scripts/run_replay.sh` 驱动）、Python 外部驱动——用于对比"内挂执行"与"序列回放执行"是否一一对应（等价性验证），或在复刻游戏上量化复刻质量（见步骤6）。

---

## 步骤 0：初始化 git

如果游戏工程没有接入 git，将游戏工程初始化为 git repository，并加入经典的 Unity 工程 gitignore 文件。

git 的作用：从第一个分析报告开始，所有改动都有 commit 记录。方便追溯策略演变历程，也方便回滚到之前的版本。步骤6迭代测试循环中每轮优化后都会 commit，如果没有 git，这些改动无法追溯和回滚。

---

## 步骤 0.5：最小可行 Bot（工程链路验证）

在深入分析游戏系统之前，先构建一个**最小可行 bot**验证工程链路是否通畅。目标是尽早暴露工程问题（画面冻结、进程不退出、自动开局失败、timeScale 卡死等），避免在完整分析和编码后才发现链路不通。

**最小可行 bot 只需实现：**
1. 自动启动游戏（从主菜单进入对局）
2. 简单移动（固定方向或随机方向，不做决策）
3. 游戏结束后自动退出进程
4. `Application.runInBackground = true`
5. timeScale 强制恢复（无对话框时 timeScale=0 → 恢复）

**验证清单（全部通过才继续步骤1）：**
- [ ] 窗口模式启动正常（`-screen-fullscreen 0`），失焦/置后不暂停
- [ ] 双击 exe 能自动进入游戏并移动
- [ ] 主画面正常渲染（不冻结、UI 不叠加）
- [ ] 游戏结束后进程在 5 秒内退出
- [ ] `-auto false` 能禁用 bot 恢复手动
- [ ] AUTO/Speed UI 按钮可见可点击
- [ ] 并发跑 2 个实例不互相干扰（窗口错开摆放，不互相遮挡——xdotool 点击落在光标下的窗口）

> 工程问题（画面冻结、进程不退出、自动开局失败）往往比策略问题更耗时间。先跑通工程链路再深入分析，能避免大量返工。此步骤产出的工程框架（Bootstrap、BotConfig、AutoUIHandler、VideoRecorder 的工程部分）可直接复用到完整 bot 中。

---

## 步骤 1：分析游戏系统，生成分析报告

分析当前游戏的系统，按模块顺序生成游戏分析报告。

分析报告采用分层结构：

- **通用基础层**（所有游戏类型必须包含）→ 读取 `references/analysis-template-common.md`，定义报告的章节骨架（游戏类型、关卡与胜利条件、核心机制、辅助系统、物理系统），但不预设具体游戏概念
- **类型深度层**（按游戏类型按需追加）→ 按模块顺序读取对应的类型深度层模板，每个模块产出独立文档、独立评估
  - **动作/割草类**（Vampire Survivors、Musou 等）→ 按顺序读取：
    - `references/analysis-template-action-A-scan.md`（实体扫描）
    - `references/analysis-template-action-B-deepdive.md`（实体深挖）
    - `references/analysis-template-action-C-interaction.md`（交互与系统）
  - **三消/方块消除类**（Candy Crush、Sweet Sugar、宝石迷阵等）→ 按顺序读取：
    - `references/analysis-template-match3-A-scan.md`（棋子/特殊糖/棋盘块/目标/限制扫描）
    - `references/analysis-template-match3-B-deepdive.md`（坐标系/块清除触发/目标剩余量深挖）
    - `references/analysis-template-match3-C-interaction.md`（交换-匹配-下落-目标推进交互）
    - 编码前**务必先读** `references/match3-bot-playbook.md`（加权评分求解器架构 + 高频致命坑清单 + 机制根因诊断法）
  - 其他类型后续按需新增，每个类型的模板放在 `references/analysis-template-<type>-A/B/C-*.md` 中

步骤1中"游戏类型与核心玩法"的识别结果，决定了需要加载哪个类型深度层模板。类型深度层的每个字段都直接对应 bot 的决策输入——分析是为了决策，不是为了文档好看。

> **注意**：步骤1仅产出客观事实（实体、属性、交互），不包含策略决策。策略设计在步骤2中进行。

### 1.1 实体扫描 → 产出扫描清单文档

读取类型深度层的 Module A 模板（如动作类读取 `analysis-template-action-A-scan.md`），遍历代码扫描所有实体类型，产出扫描清单文档。

清单中每个实体获得唯一 ID（如 `enemy_001`、`projectile_003`、`terrain_002`），后续模块通过 ID 引用。

#### 1.1 评估 — 完整性检查

扫描清单完成后，使用 Agent 工具启动一个评估子 agent（subagent_type: general-purpose），对扫描清单进行完整性验证。

评估子 agent 收到的指令应包含：
- 完整的扫描清单内容
- 游戏代码目录路径
- Module A 模板中的评估重点说明

评估重点：**完整性**——是否遗漏了代码中的任何实体类型？

关键遗漏（缺少重要敌人类型、弹幕类型等）必须修正清单后再进入 Module B；次要遗漏不阻塞。

### 1.2 实体深挖 → 产出属性文档

读取类型深度层的 Module B 模板（如动作类读取 `analysis-template-action-B-deepdive.md`），对 Module A 清单中的每个实体精读代码，提取详细属性。用 Module A 分配的 ID 索引。

#### 1.2 评估 — 准确性检查

属性文档完成后，使用 Agent 工具启动一个评估子 agent（subagent_type: general-purpose），对属性文档进行准确性验证。

评估子 agent 收到的指令应包含：
- 完整的属性文档内容
- Module A 的扫描清单（作为索引参考）
- 游戏代码目录路径
- Module B 模板中的评估重点说明

评估重点：**准确性**——每个实体的数值是否与代码/配置一致？行为描述是否与实现逻辑匹配？

关键数值错误（伤害值与代码不一致、行为描述与实现逻辑矛盾）必须修正属性文档后再进入 Module C；次要精度问题不阻塞。

### 1.3 交互与系统 → 产出交互文档

读取类型深度层的 Module C 模板（如动作类读取 `analysis-template-action-C-interaction.md`），横跨多个实体代码做交叉推理，推断碰撞关系、生成规则、预警信号、视野边界等系统演化规律。引用 Module A 的实体 ID 和 Module B 的属性数据。

#### 1.3 评估 — 逻辑一致性检查

交互文档完成后，使用 Agent 工具启动一个评估子 agent（subagent_type: general-purpose），对交互文档进行逻辑一致性验证。

评估子 agent 收到的指令应包含：
- 完整的交互文档内容
- Module A 的扫描清单 + Module B 的属性文档（作为交叉验证参考）
- 游戏代码目录路径
- Module C 模板中的评估重点说明

评估重点：**逻辑一致性**——碰撞矩阵是否覆盖了所有实体组合？生成规则是否与配置表一致？预警信号是否与攻击代码对应？

关键逻辑不一致（碰撞矩阵遗漏重要组合、生成规则与代码矛盾）必须修正交互文档后再进入 Step 2；次要不一致不阻塞。

### 1.4 输入动作词汇表 → 产出词汇表文档

扫描游戏的输入与交互管线，产出**输入动作词汇表**——bot 的所有操作最终必须落在这份词汇表的原语上，这是后续"键鼠序列录制/回放/等价性验证"的基础。

本 skill 的注入方式是 **OS 级真实键鼠事件（xdotool）**，因此词汇表原语必须是"人类在屏幕上能做出的操作"。需要识别：

- **交互路径**：每个可操作对象的点击如何到达游戏逻辑——3D 碰撞体（物理射线 → `OnMouseDown`）、IMGUI（`OnGUI` 的 `GUI.Button`，只响应 `Event.current`）、uGUI（EventSystem 射线）。这决定 bot 能否点到它、点击坐标如何计算
- **玩家可用的全部输入原语**，逐个列出：
  - **世界点击**：点 3D 对象（单位/地块/建筑）——需给出"对象 → 屏幕坐标"的换算依据（碰撞体 bounds、 RectTransform）
  - **屏幕/GUI 点击**：点 UI 按钮/菜单——需给出按钮位置的计算依据（IMGUI 反射读 `Rect`/`Items`/`ButtonHeight`；uGUI 读 RectTransform）
  - 右键取消、鼠标拖拽、滚轮、键盘按键（含长按/组合语义）
- **相机可确定性**：主相机位姿决定"世界坐标 → 屏幕坐标"的映射。词汇表必须回答：主相机能否被固定（禁用 StrategyCamera 之类会移动相机的脚本）？合适的固定位姿是什么？——**位姿选择由游戏情况决定**：相机本身静止的游戏直接用默认位姿；相机跟随/移动的游戏选一个能覆盖全部关键交互元素、固定不抖动的位姿（2D 棋盘/回合制 → 俯视全图；3D 场景 → 透视俯视，参考 `references/sequence-replay-guide.md` 的游戏类型→相机角度表）。**相机不固定则序列坐标不可复现，回放验证必然失败**——最终采用的相机配置（freezable/disable_script/pos/rot/fov/分辨率）作为"相机配置记录"写入词汇表交付物，录制端按它固定主相机、回放端按它生成 `-camera-pos/-camera-rot/-camera-fov/-camera-disable-script` 参数
- **只读反射入口清单**：计算 UI 布局坐标所需的字段（如 `Menu.Rect`、`Menu.Items`、`Menu.ButtonHeight`）——**只读反射允许，反射调用游戏方法禁止**（见 `references/real-input-principle.md`）

词汇表结构示例：

```json
{"primitives": [
  {"id": "world_click", "type": "mouse_click", "target": "单位/地块/建筑（物理射线 OnMouseDown）",
   "coord": "碰撞体 bounds.center + up*(size.y/2+0.1) → WorldToScreenPoint"},
  {"id": "gui_click",   "type": "mouse_click", "target": "IMGUI 菜单按钮",
   "coord": "反射读 Rect/Items/ButtonHeight 计算单按钮中心"},
  {"id": "ui_click",    "type": "mouse_click", "target": "uGUI 按钮",
   "coord": "RectTransform 世界角点 → WorldToScreenPoint"},
  {"id": "right_click",  "type": "mouse_click", "button": "right"},
  {"id": "drag",         "type": "mouse_drag"},
  {"id": "confirm",      "type": "key", "binding": "Return"},
  {"id": "cancel",       "type": "key", "binding": "Escape"}
],
 "camera": {"freezable": true, "disable_script": "StrategyCamera",
            "pos": [7.5, 20, 8.5], "rot": [90, 0, 0], "fov": 60, "resolution": [800, 600]}}
```

> **关键约束**：步骤2设计的每条策略规则都必须能翻译到这份词汇表的原语。由于注入是 OS 级真实事件，**bot 与人类共享同一套可达性限制**——对象被遮挡点不到、按钮动画中点不到、碰撞体禁用点不到，bot 必须像人类一样等待或规划替代路径（人机等价性约束，见 `references/real-input-principle.md`）。只能靠直调游戏方法实现、键鼠表达不了的操作在本 skill 中**不允许出现**——序列回放无法复现它们，等价性会断裂，设计期就应暴露而不是回放对比时才发现。

#### 1.4 评估 — 键鼠可表达性检查

词汇表完成后，使用 Agent 工具启动一个评估子 agent（subagent_type: general-purpose），验证词汇表完整性——是否遗漏玩家输入通道（手柄、触屏、组合键、长按/连发等）？每个原语的交互路径与坐标换算依据是否在游戏代码中真实存在？相机可固定性是否已确认？

### 交付物

- Module A: 实体扫描清单文档
- Module B: 属性文档
- Module C: 交互文档
- Module D: 输入动作词汇表文档（含**相机配置记录**：freezable / disable_script / pos / rot / fov / 分辨率——由当前游戏情况确定后固化下来，录制端固定主相机、序列 header、回放端 `-camera-*` 参数三处共用同一份记录）

这些文档作为步骤2头脑风暴的核心输入。

---

## 步骤 2：搜索参考 → LLM头脑风暴 → 策略设计

将步骤1的客观事实通过创造性LLM头脑风暴转化为可执行的bot策略。

### 2.1 搜索参考方案

从步骤1识别出的核心决策挑战出发（挑战驱动），搜索同类游戏的社区验证过的自动化策略。

搜索策略：
- 先列出步骤1分析报告中的核心决策挑战（如"闪避时机管理"、"威胁优先级排序"、"升级选择策略"）
- 围绕每个挑战，组合关键词搜索：
  - 中文：`[具体挑战] 游戏自动化`、`[具体挑战] bot 决策`
  - 英文：`[具体 challenge] game AI strategy`、`[具体 challenge] rule-based agent`、`[具体 challenge] automation approach`
  - 同时也搜架构层面的参考：`[选择的架构] game bot implementation`、`[架构] Unity 自动化`
- 重点关注：
  - 每个挑战是否有成熟的解法
  - 该类型游戏自动化的常见陷阱和边界情况

输出：**参考素材包**——社区验证过的方案、适用策略、需调整策略、不适用策略、常见陷阱。该素材包作为头脑风暴素材，不是最终策略映射。

参考素材包结构：

```markdown
# [游戏名称] 自动化参考方案

## 搜索来源
（列出搜索到的关键文章/讨论/项目链接）

## 挑战→策略映射
（每个核心决策挑战对应哪些参考策略，标注来源）
- 挑战1：[核心决策挑战描述] → 策略：[描述] —— 来源：[链接]
- 挑战2：[核心决策挑战描述] → 策略：[描述] —— 来源：[链接]

## 需调整的策略
（思路好但需要根据当前游戏特点调整的策略）
- 策略：[原思路] → [调整方向] —— 原因：[当前游戏的特殊性]

## 不适用的策略
（看起来相关但不适合当前游戏的策略，简述原因——知道什么不该做同样重要）

## 常见陷阱
（搜索中发现的该类型游戏自动化的常见问题）
```

### 2.2 LLM 头脑风暴策略设计（核心）

LLM 收到三个输入：
1. 步骤1 A/B/C 分析文档（游戏客观事实）
2. 步骤2.1 参考素材包（社区经验）
3. 游戏代码目录（可随时回头查代码验证想法）

头脑风暴可以多轮：
- 第一轮：基于分析文档和参考素材产出初步策略方案
- 回头查代码：验证策略中引用的游戏类/方法/字段是否确实存在
- 第二轮：修正不可行的部分，产出最终策略设计文档

**核心原则：架构由策略决定，而非反过来选择架构再填策略。** 如果策略需要持续评估优先级，自然适配效用系统；如果策略有清晰的阶段转换，自然适配行为树。不要先选架构再填策略。

输出：**策略设计文档**，参考 `references/strategy-design-template.md`。

策略设计文档结构：

```markdown
# [游戏名称] Bot 策略设计

## 核心决策挑战
（从步骤1分析中提取，列出 bot 需要解决的 2-3 个最关键决策问题）

## 策略方案
（头脑风暴产出的完整策略。每个核心挑战对应具体策略规则。每条规则必须是具体可编码的判断条件，不能有"根据情况判断"这类模糊表述）

### 挑战 1: [核心决策挑战描述]
- 策略规则 1: [具体可编码的判断条件 → 对应动作]
- 策略规则 2: [...]

### 挑战 2: [核心决策挑战描述]
- 策略规则 1: [...]

## 架构选择
（由策略需求自然推导出的架构选择。说明：策略需要什么能力 → 哪种架构天然提供这种能力 → 因此选择该架构）

## 策略 → 代码映射
（每条策略规则对应什么代码结构、需要读什么游戏状态、输出什么操作、落到哪个键鼠原语）

| 策略规则 | 代码结构 | 游戏状态输入 | 操作输出 | 键鼠原语 |
|----------|---------|-------------|---------|----------|
| [策略规则1] | [哪个模块的哪个方法] | [观察器读什么字段] | [适配层+注入器执行什么操作] | [步骤1词汇表中的原语，如 world_click(碰撞体顶部) / gui_click(某菜单按钮) / key(Return)] |

> **键鼠可表达性约束**：每条策略规则的"键鼠原语"列必须非空——bot 的所有操作最终都要经 AGInputInjector 变成 OS 级真实输入事件，这样 auto 模式录制的 `sequence.json` 才能完整编码 bot 行为，回放才能复现。无法映射到原语的操作（如"直接调 manager 应用选择"）在本 skill 中不允许出现。

## 参考来源标注
（每条策略思路的来源——头脑风暴原创、借鉴社区经验、还是结合两者）
```

### 2.3 验证评估 — 策略设计审查

策略设计文档完成后，使用 Agent 工具启动一个评估子 agent（subagent_type: general-purpose），进行审查验证。

评估内容：

| 检查项 | 说明 |
|--------|------|
| 数据支撑 | 每条策略规则是否有步骤1数据支撑（Module A/B/C 的实体、属性、交互信息） |
| 可编码性 | 规则是否具体可编码（不是"需要更好的闪避"这种模糊表述） |
| 架构推导 | 架构选择是否由策略需求自然推导，而非随意选择 |
| 参考相关性 | 参考素材是否真正相关，而非仅因类型相同就默认适用 |
| 键鼠可表达性 | 每条策略规则是否映射到步骤1输入动作词汇表的原语；是否存在键鼠表达不了的操作 |

评估子 agent 输出一份验证报告。发现策略无数据支撑、规则不可编码、架构误选时，修正后再继续；次要建议不阻塞。

---

## 步骤 3：编写自动化游戏助手代码

根据步骤1的游戏分析报告和步骤2的策略设计文档，编写自动化游戏助手的代码。

### 3.1 架构对齐与代码结构

决策代码必须使用步骤2策略设计文档中确定的架构来组织。步骤2参考素材包中找到的策略模式应融入决策逻辑。

代码整体原则——按游戏是否使用 asmdef 选隔离策略：
- **游戏代码有自己的 asmdef**：助手代码用单独的 assembly + asmdef，助手 asmdef 依赖游戏 asmdef。隔离最干净。
- **游戏代码无 asmdef（全部在预定义的 `Assembly-CSharp`）**：**不要**给助手建 asmdef——asmdef 程序集**无法引用预定义程序集**，会编译不过；给整个游戏补 asmdef 又是高侵入、易破坏构建。此时改用"独立可删除文件夹 + 复用默认程序集 + 命名空间隔离"（如 `Assets/AutoGamer/`，命名空间 `XXX.AutoGamer`）。删除该文件夹即可移除助手。
- 除了必要的 field visibility 修改，不修改游戏原始代码。最小化对游戏本身的侵入

代码文件按职责组织（每个职责一个文件/目录）：

| 模块 | 职责 | 数据来源 |
|------|------|----------|
| 决策引擎 | 按步骤2策略设计实现核心决策逻辑；**全部注入调用是协程，用 `yield return`** | 步骤2策略设计文档 |
| 游戏状态观察器 | 从游戏对象读取 bot 需要的实时状态（敌人位置/HP/冷却/弹幕等） | 步骤1分析报告 |
| **AGGameAdapter（游戏适配层）** | 把游戏对象（单位/地块/建筑/菜单按钮）翻译为通用注入器能理解的世界/屏幕坐标，附语义 meta（如 `unit@(2,11)`）——**按游戏编写**，参考 `code/AGGameAdapter_Example.cs` 与 `code/AGInputInjector.cs` 下半部分的坦克示例 | 步骤1词汇表 + 分析报告 |
| **AGInputInjector（通用注入器·唯一注入口）** | xdotool OS 级真实鼠标/键盘注入 + 坐标转换 + 窗口客户区检测；`OnInjected` 事件逐条 `Emit` `AGSeqEvent`；提供 `ReplayClick` 回放接口——**直接复用 `code/AGInputInjector.cs`，不用改** | `code/AGInputInjector.cs` |
| **键鼠序列录制器** | 订阅 `AGInputInjector.OnInjected`，逐事件写 sequence.json | 注入口事件流 |
| **内挂自检回放器（AGSequenceReplayer）** | replay 模式：禁用决策引擎，逐事件调 `AGInputInjector.ReplayClick(sx, sy)` 按屏幕坐标重放（不解析游戏对象），设计见 `references/sequence-record-replay.md` | sequence.json |
| 战斗 log 记录器 | 记录每局关键数据到 JSON（简化版，只记录视频无法展示的信息） | 步骤1 + 简化版 battlelog 模板 |
| **视频录制器** | 录制完整单局视频，产出 mp4（录制端独立相机方案） | `references/video-recorder-reference.md` |
| UI 管理器 | Auto 按钮、速度按钮 | — |
| 启动参数处理器 | 解析命令行参数 + 环境变量 | — |

> `code/AGInputInjector.cs` 依赖 bot 工程中的三个小工具类：`AGLog`（日志转发 Debug.Log）、`AGReflection`（只读反射，如 `GetMenuRect`）、`AGObserver`（状态观察器单例）。随 bot 代码在步骤3一起实现（通常各 10-30 行），**只读反射允许，反射调用游戏方法禁止**。

### 视频录制器设计

**实现方式**：Camera + ffmpeg pipe
- 创建专用 Camera（跟随游戏主 Camera 或固定俯视角）
- Camera 渲染到 RenderTexture
- 每帧读取 RenderTexture 原始像素（RGBA），pipe 到 ffmpeg 子进程 stdin
- ffmpeg 实时编码为 mp4，不做中间 PNG
- ffmpeg 启动命令：`-y -f rawvideo -vcodec rawvideo -pixel_format rgba -colorspace bt709 -video_size WxH -framerate N -loglevel warning -i - -c:v libx264 -pix_fmt yuv420p -crf 23 output.mp4`
- 无中间 PNG 文件——直接输出 mp4

**录制触发时机**：
- `StartRecording()`：Auto 模式开启时调用
- `StopRecording()`：单局结束时调用（通关/死亡/时间限制）
- 每局产出独立的 mp4 文件

**产出文件**：
- `recording.mp4`（完整单局视频）
- `frame_data.json`（每帧时间戳、bot 位置、关键事件标记）

**新增命令行参数**：
- `record-video`（bool，默认 true）
- `video-fps`（帧率，默认 10）
- `video-resolution`（分辨率 WxH，默认匹配游戏分辨率或 512x512）

**ffmpeg 依赖**：必需。如果系统 PATH 中不可用，自动安装（macOS: `brew install ffmpeg`，Linux Debian/Ubuntu: `sudo apt install ffmpeg`）。无降级回退方案。

> 录制端（内挂局）用独立录制相机；回放端（SequenceReplayer）用屏幕捕获（`WaitForEndOfFrame` + ReadPixels，能同时捕捉 3D 与 IMGUI）——两套方案用途不同，见 `references/sequence-replay-guide.md`。

### 战斗 log — 简化版

由于视频已覆盖视觉叙事，战斗 log 只记录**视频无法展示的内容**：

**保留**：
- 单局总结：胜/负、用时、最终 HP、关键指标
- 关键决策追踪：仅在决策转折点记录（非每 tick），包含 bot 的推理和效用评分
- 关键事件：仅在重要时刻记录（首次遭遇新敌人类型、HP 低于阈值、升级选择）
- 数值快照：HP 变化、伤害来源分布（视频无法展示的具体数值）

**移除**：
- 每 tick 完整事件流（视频覆盖视觉叙事）
- 每 tick 位置记录（视频展示移动轨迹）
- 每 tick 伤害详细记录（视频展示"被击中"，log 记录每个敌人类型的总伤害）

log 文件从数百 KB 缩减到几 KB。视频 + log 互补而非重叠。

**对回放验证的重要补充**：battlelog 是回放对比的"逻辑真值"——**终局 summary + 逐回合状态快照**（单位位置/HP/资源等，见 `examples/example_battlelog.json`）。评估复刻质量或等价性时，靠它做终局层/状态层 diff，因此关键状态必须可量化、可逐回合快照。

### 键鼠序列录制与回放（轨迹提取与验证的基础）

bot 支持两种运行模式，由 `-run-mode`（或环境变量 `AUTOGAMER_RUN_MODE`）切换：

- **auto 模式（默认）**：决策引擎驱动。所有操作经 **AGInputInjector 唯一注入口**注入 OS 级真实鼠标事件：决策引擎 → `yield return AGGameAdapter.ClickUnit(unit, waitId)` → `AGInputInjector.ClickColliderTop(col)` → `xdotool mousemove/mousedown/mouseup` → Unity 接收 X11 事件自动生成 `Event.current` → 物理射线/IMGUI/uGUI 自然响应。注入器同时把每个事件 Emit 给录制器，产出 `sequence.json`——bot 行为的键鼠编码，只含屏幕坐标+时序+语义标注
- **replay 模式（内挂自检）**：完全禁用决策引擎，AGSequenceReplayer 读取 `sequence.json`，逐事件调 `AGInputInjector.ReplayClick(sx, sy)` 按屏幕坐标重放——在同一构建内先验证"录 → 放"一致，排除轨迹本身的问题

序列事件格式（`code/AGInputInjector.cs` 的 `AGSeqEvent`；完整 schema、录制规则、回放规格见 `references/sequence-record-replay.md`）：

```json
{"i": 2, "frame": 17, "t": 4.03, "op": "world_click", "meta": "unit@(2,11)",
 "sx": 249, "sy": 367, "inject": "os_mouse", "wait": "game_settled"}
```

**录制规则（保证可回放）**：
- **唯一注入口**：所有输入事件必须经 AGInputInjector——任何绕过注入口的散装输入既让序列缺失对应项，也违反人机等价原则
- **相机固定**：录制局内主相机必须固定在步骤1词汇表"相机配置记录"确定的位姿（禁用会移动相机的脚本）——相机决定 world→screen 映射，**最终采用的相机配置写入序列 header**（见 `references/sequence-record-replay.md`），回放端按 header 逐参数复刻（`-camera-pos/-camera-rot/-camera-fov/-camera-disable-script`）
- **时序记录**：`t` 为 `Time.unscaledTime` 秒、`frame` 为 `Time.frameCount`。独立回放器按 `t` 时间戳间隔重放（跨工程通用）；内挂自检可按帧索引对齐
- **wait 屏障标注**：场景切换后的事件用 `wait` 字段标注（如 `main_menu_visible` / `game_settled` / `action_popup_visible`），回放端在场景切换处等就绪再执行，防止点击落在旧场景的按钮上
- **meta 语义标注**：每个点击记录人类可读目标（如 `"MainMenu/Play"`、`"tile@(4,7)"`）——回放失败时对照 done.json 中断位置 + meta，一步定位"本想点什么"
- **单行紧凑 JSON + 原子写入**（先写临时文件再 rename），避免与日志收集器冲突

**回放验证**：三种模式（内挂自检 / 独立 SequenceReplayer + `scripts/run_replay.sh` / Python 外部驱动）与四层对比流程见步骤6"序列提取与回放验证"。

### 3.2 核心实现要求

两个关键实现问题——如果这两点没搞清楚，bot 代码写出来要么读不到数据、要么控制不了游戏。

**游戏状态读取：**

bot 需要实时读取游戏状态才能做决策。实现方式：
- 在游戏状态观察器（AGObserver）中，通过 asmdef 依赖直接读取游戏对象的 public/internal 字段
- 观察器收集的数据应对应步骤1分析报告中识别的关键决策输入
- 如果游戏有多个需要观察的系统（敌人、弹幕、地形、升级），每个系统单独写观察方法

**输入控制（OS 级真实鼠标注入）：**

bot 需要将决策结果转化为游戏操作。本 skill **不走 Unity InputSystem 虚拟设备、不模拟按键状态、不 hook Input API**，而是用 xdotool 注入 OS 级真实鼠标/键盘事件：

```
决策引擎 → yield return AGGameAdapter.ClickUnit(unit, waitId)
        → AGInputInjector.ClickColliderTop(col)
        → xdotool mousemove <absX> <absY> mousedown 1 mouseup 1
        → Unity 接收 X11 事件自动生成 Event.current
        → 物理射线 / IMGUI / uGUI 自然响应 —— 与人类点击完全等价
```

**为什么必须 OS 级事件而非 Unity InputSystem**（实测结论，详见 `references/real-input-migration.md`）：

| 方案 | 结论 |
|------|------|
| InputSystem StateEvent（WriteValueIntoEvent） | 不生成 IMGUI 的 `Event.current`，`GUI.Button` 不响应 ✕ |
| `Mouse.current.WarpCursorPosition` | 只移动光标位置，不生成点击事件 ✕ |
| `activeInputHandler=Both` 桥接 | 只桥接 `Input.GetMouseButtonDown` 等旧 API，不桥接 IMGUI Event ✕ |
| xdotool（XSendEvent，OS 级） | 引擎自动生成 `Event.current`，IMGUI / uGUI / 物理射线全部接收 ✓ |

**实现要点**（`code/AGInputInjector.cs` 已内置，理解后按需调用）：
- **窗口客户区检测**：`xdotool getwindowgeometry` 返回的是含标题栏的**外框(frame)**坐标，点击必须相对**客户区(client)**——优先 `xwininfo`，后备 `xdotool` + `_NET_FRAME_EXTENTS` 修正，否则所有点击整体偏移一个标题栏高度（如 xfwm4 实测外框 (10,85) vs 客户区 (5,56)，点 "Play" 实际点到 "Quit"）
- **每次点击前刷新窗口几何**：窗口可能被 WM 移动，点击前重取几何自纠错
- **协程化注入**：xdotool 的 `WaitForExit` 在主线程同步阻塞会导致协程死锁——`WaitForExit(500)` 短超时 + `ClickAt` 协程多个 `yield return null`；所有注入方法签名是 `IEnumerator`，调用方必须 `yield return`
- **坐标链路**：世界坐标 → `Camera.WorldToScreenPoint`（固定相机）→ Unity Screen 坐标（左下原点，Y↑）→ 客户区偏移 + 缩放比 + Y 轴翻转 → X11 绝对坐标（左上原点，Y↓）。链路中任何一环不同（相机/分辨率/客户区偏移），点击全部错位
- **碰撞体遮挡**：点击 3D 对象用碰撞体顶部（`bounds.center + up*(size.y/2+0.1)`，`ClickColliderTop`），避免被地面/地块碰撞体挡住射线
- **IMGUI 按钮中心**：`Menu.Rect` 是整个菜单框，点框中心可能命中错误按钮——反射**读** `Items` 索引 + `ButtonHeight` 计算单按钮中心（`AGGameAdapter.ClickMenuButton` 有现成实现）
- **Editor 模式未验证**：Editor 中 GameView 嵌在编辑器窗口内，坐标映射复杂——注入/录制/回放验证一律用 Player 构建

### 3.2b 阻塞式 UI 与终局处理（极易卡死，必须全覆盖）

很多游戏用 `Time.timeScale = 0` 暂停并弹出需要点击的 UI。bot 若不逐个处理，会**永久卡死**。这类阻塞点远不止"升级选择"，需在步骤1就把它们都识别出来，逐一实现自动处理：
- **开局前**：主菜单/关卡选择（如 `AGGameAdapter.ClickMenuButton(menu, "Play")`）
- **对局中**：升级/技能选卡、武器选择、宝箱/奖励确认（常有"跳过动画"+"领取"两步按钮）、复活弹窗
- **对局后**：通关结算屏、失败结算屏

**处理方式一律是真实点击**（移动鼠标到按钮中心 → mousedown/mouseup），与人类一致。人机等价模式下没有"逻辑层直调"这条捷径——直调会在序列里留下无对应物的操作（录制端有、回放端没有），等价性直接断裂（见 `references/real-input-principle.md` 的禁止清单）。

实现要点：
- 处理逻辑要在 `Update` 里**先于"暂停就 return"的早退执行**——否则结算屏/升级屏自己把 timeScale 置 0，bot 永远走不到检测分支（典型 bug：通关检测不到，胜利被误判为超时）
- 点击**真正可交互的那个**按钮（按名字/`Selectable` 状态匹配；动画中禁用的按钮要等动画结束再点——**人类也点不到的东西 bot 同样点不到，这是等价性约束而非缺陷**，被遮挡/未激活的按钮必须等或换路径）
- 点击后**等面板真正关闭再做下一步**（很多关闭是带动画的协程，重复点击会打断关闭、卡在半开状态）
- **强制关闭看门狗（只记录不绕过）**：面板开启超过 N 秒（unscaled）仍未关闭，记录异常并标记本局等价性验证失败——**不要**强制置 `IsOpen=false`/恢复 timeScale/触发 `onClosed` 这类绕过输入管线的兜底（会污染序列）；无人值守的卡死风险由 run_sweep 的墙钟超时 kill 兜底

### 3.2c 时间、窗口与显示环境

- **计时用 `Time.unscaledTime`**：speed 通过 `Time.timeScale` 缩放时，`Time.time` 会随之失真。战斗 log 的存活时长、序列时间戳 `t`、录制时间戳都应基于 unscaled 时间
- **窗口模式是硬要求**：xdotool 注入/回放都需要可见的 X11 窗口：
  - 启动加 `-screen-fullscreen 0 -screen-width 800 -screen-height 600`（全屏 Player 不接收 X11 事件，`SequenceReplayer` 与注入器都依赖窗口可见可交互）
  - **不要用 `-batchmode -nographics`**：无窗口 = 无法注入。无人值守的图形环境用 Xvfb 虚拟显示（`Xvfb :1 -screen 0 1280x1024x24 & export DISPLAY=:1`）或带显示器的 Linux 机器
  - `Application.runInBackground = true`（+ ProjectSettings `runInBackground: 1`）防失焦暂停
  - `overrideredirect` 强制窗口仅作全屏兜底，窗口模式勿用——overrideredirect 会让窗口管理器重摆窗口导致几何不稳（见 `references/sequence-replay-guide.md` 踩坑 #3）
- **本地多实例并发**：xdotool 点击落在光标处的窗口——并发实例的窗口**不可互相遮挡**（错开摆放，或每实例独立 DISPLAY）
- **fresh run**：无人值守每次应开全新一局，主动清掉存档里的续玩进度（关卡时间/经验/已有能力等），否则会"继续上一局"

### 3.2d 无人值守运行时陷阱清单（集中排查）

下列坑都属于"进程能启动、单跑也像在动，但无人值守批量跑时会静默失效/浪费/卡死"，极难从启动日志发现，务必逐项实现并验证：

- **后台暂停（`Application.runInBackground=0`）**：窗口失焦（或并发跑、最小化）时游戏自动暂停，整个 sweep 停滞，墙钟空耗。→ 运行时无条件设 `Application.runInBackground=true`，并在 ProjectSettings 里也设 `runInBackground: 1`
- **`Time.timeScale` 被 UI 面板重置**：很多游戏在每次关闭暂停 UI（升级/结算/宝箱…）时把 `Time.timeScale` 复位为 1，导致 speed 加速被悄悄取消——bot 越频繁开面板（如频繁升级），越接近全程 1×，高速 sweep 形同虚设。→ 在每个运行帧**重新断言** `Time.timeScale = 目标speed`；必要时同步放宽 `Time.maximumDeltaTime`、解除 `targetFrameRate` 限制，让加速真正生效
- **纯生存型 bot 的无限长局**：见步骤5"永生不赢"陷阱——设 per-run **游戏内时间上限**，别只靠墙钟 timeout
- **本地并发的存档互相覆盖**：local 模式 concurrency>1 时多个实例默认共享同一 `persistentDataPath`，存档数据竞争/损坏（详见步骤5）。→ 给每个实例隔离 `persistentDataPath`
- **并发窗口互相遮挡**：xdotool 点击落在光标下的窗口——多实例窗口必须错开摆放或各自独立 DISPLAY（见 3.2c）
- **构建锁残留**：见步骤4"构建卫生"——每次构建前杀残留 Unity 进程 + 删 `Temp/UnityLockfile`

> 通用验证手法：无人值守跑通后，**核对 log 里的"墙钟时长 vs 游戏内时长"是否符合 speed 倍率**（如 speed=4 时一局墙钟应约为游戏内时长的 1/4）。若两者接近 1:1，多半是 `timeScale` 被复位或后台暂停在作怪。

### 实验设计原则

迭代优化必须遵循科学的实验设计，避免"改了多个变量但不知道哪个有效"：

**核心原则：每轮只改一个关键变量**
- 每轮优化只修改一个策略参数或一个模块的逻辑，保持其他不变
- 如果必须同时改多个，必须在报告中明确标注每个改动，并说明预期效果和实际效果的归因困难
- 例外：工程 bug 修复（如画面冻结）可与策略优化同时进行，因为它们不影响策略变量

**从单局迭代切换到批量统计的时机：**
- 当连续 3 轮单局结果的波动范围 < 20%（如存活 200-240s）时，单局结果不再有区分力
- 此时立即切换到 100 局批量 sweep，用统计方法（均值、中位数、标准差）评估改动效果
- 批量 sweep 的样本量建议：Phase 1 至少 50 局，Phase 2 至少 100 局

**A/B 对比测试（当不确定哪个参数更好时）：**
- 同时跑两组配置（如 gem-weight=60% vs 80%），各 50 局
- 对比均值和分布，用统计显著性判断（如 t-test p<0.05）而非"看着更大"
- 记录到知识库中作为已验证的结论

### 瓶颈自动诊断

每轮 sweep 完成后，运行自动化瓶颈诊断脚本，避免纯靠人工分析遗漏关键信息。**完整诊断维度、瓶颈识别规则和分析脚本模板见 `references/bottleneck-diagnosis.md`。**

核心要点：
- 分析 10 个维度：存活分布、升级vs性能、首选技能、升级类型、技能组合、伤害来源、Boss局、进度梯度、相关性矩阵、拐点检测
- 根据 7 条规则自动识别瓶颈类型（DPS不足/生存不足/XP不足/特定威胁/Boss战/输出效率/随机性）
- 输出诊断结论指导下一步优化方向

### 游戏知识积累（知识库）

每轮 sweep 后更新一个结构化的知识库文件 `findings.json`，避免跨轮遗忘已发现的结论：

- **validated_findings**：已验证的结论（含置信度和证据）
- **disproven_hypotheses**：已推翻的假设（避免重复验证）
- **best_config**：当前最优配置参数
- **open_questions**：未解决的问题

下一轮策略优化前先读取知识库，避免重复发现已知结论。

### 3.2e Unity 版本兼容性防护（必做）

不同 Unity 版本的 API 差异很大，bot 代码必须做版本安全适配。**详细清单和安全写法见 `references/unity-version-compat.md`。**

关键要点：
- `FindObjectsOfType`（不用 `FindObjectsByType`，后者仅 2022.2+）
- 字体获取用 fallback 链（Arial → LegacyRuntime → 跳过文字）
- `FindObjectsOfType` 每帧调用会严重拖慢性能——必须缓存 + 定时刷新（0.3-0.5s）
- VideoRecorder 绝不能劫持 `Camera.main.targetTexture`（会导致画面冻结）——必须用独立录制相机
- UI 创建代码用 try-catch 包裹

### 3.3 功能与基础设施

非决策核心的功能需求，按类别分组：

**UI 功能：**
- 右上角 Auto 按钮：点击开启自动决策，关闭恢复手动操控
- Auto 按钮旁速度按钮：1倍/2倍/3倍/4倍切换，加速自动模式下多局游玩
- 升级系统：如果游戏有升级，自动选择最有利的一项

**记录功能：**
- 战斗 log：每局结束记录关键数据，按简化版 battlelog 模板写入 JSON 文件（通用基础层读 `references/battlelog-template-common.md`）
- 视频录制：录制完整单局视频为 mp4（见视频录制器设计）
- 键鼠序列录制：auto 模式下逐事件记录 sequence.json（见"键鼠序列录制与回放"）

**命令行参数（无人值守启动，同时支持环境变量传入，方便 Multiverse 执行）：**
- speed（1-4）：游戏运行速度
- level：指定游玩关卡
- auto（true/false，环境变量 `AUTOGAMER_AUTO`）：是否默认开启自动化
- record-video（true/false）：是否录制视频
- video-fps（数字）：视频录制帧率
- video-resolution（WxH）：视频录制分辨率
- run-mode（auto/replay，环境变量 `AUTOGAMER_RUN_MODE`，默认 auto）：auto=内挂决策驱动；replay=内挂自检回放（禁用决策引擎，按屏幕坐标重放）
- sequence（路径，环境变量 `AUTOGAMER_SEQUENCE`）：auto 模式为序列输出路径；replay 模式为序列输入路径
- record-sequence（true/false，默认 true）：auto 模式下是否录制键鼠操作序列
- seed（数字）：固定随机种子（UnityEngine.Random 与 System.Random 同时初始化），写入战斗 log 与序列 header；等价性/回放验证必填

### 3.4 验证评估 — 代码审查

代码编写完成后，使用 Agent 工具启动一个评估子 agent（subagent_type: general-purpose），对自动化助手代码进行审查验证。

评估子 agent 收到的指令应包含：
- 自动化助手的代码文件列表和路径
- 步骤1分析报告中的关键游戏机制（敌人类型、攻击方式、交互路径等）
- 步骤2的策略设计文档

评估内容：

| 检查项 | 说明 |
|--------|------|
| 架构对齐 | 代码是否使用了步骤2策略设计文档中确定的决策架构，而非随意组织 |
| 交互路径匹配 | 点击坐标计算是否与步骤1识别的交互路径一致（物理射线/IMGUI/uGUI 各自的坐标依据） |
| 游戏类引用正确性 | 引用的游戏类/方法/字段是否确实存在于游戏代码中 |
| 决策逻辑覆盖度 | bot 决策是否覆盖步骤1报告中所有关键威胁场景 |
| 游戏状态观察完整度 | 观察器是否收集了步骤1报告中所有关键决策输入数据 |
| 视频录制器正确性 | 是否正确使用了 Camera + ffmpeg pipe 方式 |
| 录制触发正确性 | 录制是否正确在 Auto 启动时开始、单局结束时停止 |
| ffmpeg 依赖处理 | ffmpeg 是否为必需依赖且有 install-if-missing 行为 |
| 战斗 log 简化 | 战斗 log 是否只记录视频无法展示的内容 |
| 战斗 log 可对比性 | summary 终局 + 逐回合状态快照是否可量化，能作为回放对比真值 |
| 伤害来源追踪 | 战斗 log 的 damage_taken 是否包含所有敌人类型 |
| 隔离方式正确性 | 有 asmdef 走 asmdef 依赖；无 asmdef 走独立文件夹+命名空间（不强套 asmdef） |
| 最小侵入检查 | 无不必要的游戏代码修改 |
| 启动参数正确性 | 命令行参数实现是否正确，且支持环境变量传入 |
| **人机等价性** | 是否存在 SendMessage / 反射调用游戏方法 / 直调 manager / hook Input API 等绕过输入管线的操作（一律禁止） |
| **注入唯一性** | 所有输入事件是否都经 AGInputInjector 注入，无绕过注入口的散装输入调用 |
| **注入协程化** | 所有注入调用是否 `yield return`；xdotool 超时是否 ≤500ms 防主线程死锁 |
| **窗口与坐标** | 是否窗口模式启动；客户区坐标是否 xwininfo 优先 + `_NET_FRAME_EXTENTS` 后备；每次点击前是否刷新窗口几何 |
| **相机确定性** | 录制局主相机是否被固定（禁用移动脚本）；参数是否写入序列 header 供回放端复刻 |
| 序列录制正确性 | sequence.json 是否含 i/frame/t/op/meta/sx/sy/inject/wait 全字段、单行紧凑、原子写入 |
| 自检回放完整性 | replay 模式是否完全禁用决策引擎、仅按屏幕坐标经 ReplayClick 重放 |
| 阻塞式 UI 全覆盖 | 开局/升级/武器/宝箱/复活/通关结算/失败结算等暂停 UI 是否都自动处理，不会卡死 |
| 阻塞 UI 真实点击 | UI 处理是否一律真实点击（无逻辑层直调）；看门狗是否只记录不绕过 |
| 终局检测时机 | 胜/负检测是否先于"暂停就 return"的早退（结算屏会置 timeScale=0） |
| 计时基准 | 存活时长/序列时间戳是否用 unscaledTime（不受 speed 缩放影响） |
| 后台不暂停 | 是否设 `Application.runInBackground=true`（+ ProjectSettings），失焦/并发不暂停 |
| speed 真生效 | 是否每帧重新断言 `Time.timeScale=speed`，防 UI 关闭复位导致加速失效 |
| 插桩可信 | 伤害来源/击杀/升级/终局等关键指标确实被记录，可作为迭代依据 |
| Unity 版本兼容 | 关键 API（FindObjectsOfType、字体、Camera.main）是否做了版本安全适配和 null 检查；FindObjectsOfType 是否缓存而非每帧调用 |
| 最小可行 bot 验证 | 工程链路（自动开局→移动→渲染→退出）是否在完整编码前已验证通过 |

评估子 agent 输出一份验证报告。发现编译级错误（引用不存在类、注入器调用方式错误、架构与步骤2不一致）或关键逻辑遗漏时，必须修正代码后再继续；次要代码风格建议不阻塞。

---

## 步骤 3：给游戏添加UOS Multiverse的支持

1. 给游戏添加Multiverse的SDK接入
2. 给游戏添加打包为Linux Dedicated Server的功能，方便Multiverse打包

> **真实输入与 X11 的注意**：xdotool 注入/回放需要 X11 图形环境。multiverse allocation 跑 auto/replay 局前，先确认容器具备虚拟显示（Xvfb + `DISPLAY`）；不具备时，注入/录制/回放相关的局改用 local 模式跑，multiverse 模式只承担纯 log 类局（不依赖鼠标注入的）。

## 步骤 4：打包游戏

将游戏打包为目标平台为当前操作系统的可执行文件。打包完成后，记录可执行文件的路径——步骤5的 run_sweep 脚本需要此路径来启动游戏。

打包注意：
- 用 Editor 脚本 + `-batchmode -quit -executeMethod` 命令行打包，便于自动化；打包是长操作，用后台进程 + 日志轮询。
- **构建期注入 scripting define 要用 `BuildPlayerOptions.extraScriptingDefines`**，不要用 `PlayerSettings.SetScriptingDefineSymbols`——后者在同一个 batchmode `-executeMethod` 会话里**不会触发重新编译**，gated 代码会被静默编译掉（典型坑：Multiverse SDK 生命周期代码没进包，运行时毫无反应）。
- Linux Dedicated Server 打包需先装对应构建模块，并设 Server subtarget。
- **回放验证的目标构建**：把 `code/SequenceReplayer.cs` 放入目标游戏（复刻/原版）工程的 `Assets/` 重新构建（`scripts/setup_and_run.sh` 可自动复制并引导）。内挂构建与回放构建可为同一构建——不传 `-sequence` 参数时 SequenceReplayer 不激活、bot 由 `-auto` 控制；严格的"原版游戏验证"则用不含内挂的构建（只含 SequenceReplayer.cs）。

### 构建卫生（每次 batchmode 构建前必做，否则高频崩）

Unity 同一时刻只允许一个进程打开同一工程。自动化反复构建时，上一次的 Unity 进程（或 Editor、或上一次 crash 残留）会持有工程锁，导致新构建直接报 **"another Unity instance is running" / "Multiple Unity instances cannot open the same project"** 并失败。

**每次构建前的清理步骤**（务必先于启动 Unity 执行）：
1. 杀掉残留的 Unity 进程：
   - Windows：`Get-Process Unity -ErrorAction SilentlyContinue | Stop-Process -Force`
   - macOS/Linux：`pkill -f Unity` 或精确匹配该工程的进程
2. 删除工程锁文件：`<projectPath>/Temp/UnityLockfile`（不存在时忽略）。
3. 再启动 `Unity -batchmode -quit -projectPath ... -executeMethod ...`。

构建后从日志轮询关键标志（如自定义的 `build SUCCEEDED` / `build FAILED` / `error CS`）判断结果，不要只看进程退出码。

---

## 步骤 5：编写 run_sweep 脚本

前置依赖：步骤4打包产出的可执行文件路径。

run_sweep 脚本的作用是：自动化批量执行游戏关卡，收集足够多的战斗数据来做统计分析，这是人工测试无法高效完成的。

- 该脚本会使用命令行反复启动游戏可执行文件进行游玩
- 该脚本支持 mode参数，来支持两种运行游戏的模式: local和multiverse，默认使用local模式运行
    * local模式下，该脚本会使用本地游戏可执行文件来执行游戏关卡
    * multiverse模式下，该脚本会使用UOS Multiverse来执行游戏关卡，每一局游戏，脚本会按照下面的步骤来运行游戏
        1. 创建一个multiverse的allocation，以环境变量形式传入游戏可执行文件的必要参数（如speed，level，auto等）
        2. 等待allocation启动并运行
        3. 等待allocation运行结束
        4. 获取allocation的log，保存到本地
        5. 抓取allocation log中的battle log，以BEGIN_COMBAT_LOG_JSON开始，END_COMBAT_LOG_JSON结束，是一个JSON string，将该log替代原来本地文件形式的battle log
    * multiverse模式下，该脚本会记录下调用Multiverse API的所有request和response到log中，方便日后查看debug
- 如果游戏有关卡，该脚本支持 levels 参数，运行指定游戏关卡。支持多个关卡的输入，如 1,3,5 代表执行关卡1，3，5；支持范围形式的关卡输入，如 1-5 代表执行关卡1，2，3，4，5
- 该脚本支持 runs-per-level 参数，决定反复执行的次数
- 该脚本支持 speed 参数，决定游戏运行的速度（透传给游戏本身）
- 该脚本支持 concurrency 参数，决定启动游戏的并发度。并发度大于1时，脚本会启动多个游戏进程来进行游玩
- 该脚本会分析和记录每次执行游戏的结果，成功与否和用时，最终生成汇总报告写入文件

**注入环境的硬约束（脚本必须实现）：**
- **启动游戏一律加窗口模式参数**：`-screen-fullscreen 0 -screen-width 800 -screen-height 600`（全屏不接收 X11 事件）
- **启动前检查并安装依赖**：xdotool / x11-utils（xwininfo）/ ffmpeg——不可用时自动 `sudo apt install`（或提示后安装）
- **并发实例的窗口不可互相遮挡**：错开摆放（如按实例索引平移窗口位置）或每实例独立 DISPLAY（`Xvfb :N`）——xdotool 点击落在光标下的窗口，互相遮挡会点到别人的窗口
- **回放验证局的编排**：等价性/复刻评估局不是普通 sweep 局，按步骤6的 Run A → Run B → Run C 流程编排，收集三类产物：内挂局（sequence.json + battlelog + recording.mp4）、自检局（battlelog）、独立回放局（`scripts/run_replay.sh` 产出 done.json + replay.mp4）

**纯生存型 bot 的"永生不赢"陷阱（必须设游戏内时间上限）：** 走位/生存型 bot 常出现"永远活着但永远打不死 Boss"的局——它能无限躲避却推不动进度，于是一直跑到墙钟 timeout（如 1800s）才被杀，墙钟严重浪费，且这些局对统计无价值。**只靠墙钟 timeout 不够**：应额外提供一个 **per-run 游戏内时间上限**参数（如 `max-run-seconds`，透传给游戏，到点判负并退出），它不受 speed 缩放与卡死影响，能稳定截断这类无意义长局。墙钟 timeout 仅作最后兜底。

**本地并发的存档隔离（重要）：** local 模式 concurrency>1 时，多个实例默认共享同一 `Application.persistentDataPath` 存档，会互相覆盖导致数据竞争/损坏。必须给每个实例隔离存档（如各自独立的 persistentDataPath），否则只能 concurrency=1。multiverse 模式每个容器独立存档，天然无此问题。

**multiverse 模式抓 battle log 的坑（务必参考 multiverse skill）：**
- allocation `create` 失败时进程退出码可能仍是 0，错误在响应 `code` 字段——必须解析 body 检查，否则会对不存在的 allocation 空等到超时。
- `--allocation-ttl` 不能超过 game 的 allocationTTL（默认 10m）。
- 对局结束后日志走 **COS 文件**（logFileStatus: generating→finished），实时 `log` 字段会变空；要轮询等 finished 再下载 `.gz`、解压。
- COS 日志是 **k8s 收集器包裹格式**，且可能把长 JSON 摊平进记录顶层——抓 `BEGIN_COMBAT_LOG_JSON...END_COMBAT_LOG_JSON` 时要兼容"已转义/未转义/被摊平"三种形态（建议按标志性字段定位 + 括号配平提取，而非假设是干净一行）。
- 这些细节详见 multiverse skill 的 `references/allocation.md` 与 `references/cli.md`。建议把 battle log 设计成**单行紧凑 JSON**，避免与收集器字段冲突。

**Phase 逻辑：**

**Phase 1（策略验证——bot 还不能通关时）：**
- 每轮每关只跑 1 局
- 快速迭代：跑 → 分析 → 优化 → 重复
- 每轮结束后由步骤6决定是否切换到 Phase 2

**Phase 2（稳定性验证——bot 能通关后）：**
- 每关跑多局（runs-per-level 参数）
- 收集统计数据：通关率、平均用时等
- 模型自行决定分析范围

**视频与轨迹文件收集：**
- 收集每局的 mp4 + frame_data.json + sequence.json（键鼠操作序列，若开启录制）
- 按关卡和轮次组织：`output/level-1/round-1/recording.mp4`
- 回放验证局另存：`output/level-1/round-1/replay/done.json`、`replay.mp4`
- 在汇总报告中记录视频与序列文件路径

**ffmpeg 依赖检查：**
- 脚本启动时检查 PATH 中是否有 ffmpeg（以及 xdotool / xwininfo）
- 如果不可用，自动安装（apt/brew）

**Sweep 脚本工程健壮性（必须实现）：**

- **进程锁**：sweep 启动前检查是否有其他 sweep 脚本在运行（检查 PID 文件或进程列表），避免并发 sweep 互相干扰
- **断点续跑**：每局完成后立即写入结果文件，脚本中断后可从已完成的位置继续（跳过已完成的 round 目录）
- **墙钟超时 kill**：每局游戏进程设置独立的墙钟超时（timeout 参数），超时后强制 kill 进程，避免卡死的局占用并发槽
- **game over 后进程不退出的兜底**：游戏进程应在 game over 后 5 秒内退出。如果未退出，sweep 脚本应在墙钟超时后强制 kill
- **实时进度输出**：每完成一局输出进度（已完成/总数、当前局结果、预计剩余时间），方便监控
- **并发槽位回收**：如果某局因崩溃未产出 battle_log，sweep 脚本应检测到并释放并发槽位，不要永久等待
- **结果文件原子写入**：每局结果先写临时文件再 rename，避免并发写入损坏

---

## 步骤 6：迭代测试循环（含视频分析）

全自动迭代测试循环。每轮 run_sweep → 收集视频 + log → VLM 视频分析 → 联合分析 → 报告 → 自动优化 → commit → 判断目标 → 继续/结束。用户只在开始时设定目标指标，结束时收到最终报告。

> **VLM 不可用时（缺 `DASHSCOPE_API_KEY` / 无网络 / 用户暂缓）**：不要中断迭代，改用**纯 log 诊断法**作为一等替代——读 `references/log-only-diagnosis.md`。它用"密集插桩 + 周期状态快照 + 症状→根因层对照表"补偿看不到画面的信息缺失，在数值与决策推理上甚至比 VLM 更准。拿到 key 后再切回 VLM + log 联合分析。

> **三消/方块消除类**：视频截图常因 batch / shader 问题为黑帧而不可用，**优先采用纯 log 诊断法**，并按 `references/match3-bot-playbook.md` 第四节的"机制根因诊断流程"逐类攻破卡死关卡（读目标身份 → 读源码确认清除机制 → 对照 bot 评分 → 修正后多次取样）。每关至少 3 次取样，单次"通关"不代表修复成功。

> **游戏速度会影响"能不能赢"，不只是吞吐**：高 `Time.timeScale`（speed 3–4x）下敌人每个 bot 决策帧之间多走 N 步，反应式 bot 会反应不及而暴毙。**调优/验证用 speed=1；高速 sweep 只用于已能稳定通关的关卡做吞吐统计。** 若 bot 在高速下早死但低速能活，先怀疑速度而非策略。

### 开始前

用户设定：
- 目标指标（如通关率 100%、存活率 90% 等）
- run_sweep 参数（关卡、每关次数、速度）

**先验插桩可信，再开始调优（必做）：** 第一轮正式迭代前，先单跑一局确认战斗 log 的关键指标**真实可信**——胜负判定正确（注意终局检测要先于暂停早退，见 3.2b）、伤害来源/升级次数/存活时长确实在记录、**进度梯度指标 `summary.progress` 与吞吐（击杀数）确实在递增**（见 battlelog 模板）。若插桩没接好（如伤害恒为 0、升级数/击杀恒为 0），后续都是基于错误数据盲调，浪费迭代轮次。**重要：很多"策略问题"其实是控制或感知问题**——例如吞吐≈0 往往是"移动禁用攻击导致从没开火"（见 Module C 控制↔战斗耦合），应先查控制/感知层再调走位（症状→根因层对照表见 `references/log-only-diagnosis.md`）。环境前置项（真实 Python、xdotool/xwininfo/ffmpeg、DASHSCOPE_API_KEY、可执行文件）也在此一并确认。

### 序列提取与回放验证（需要"内挂与序列一一对应"或"复刻质量评估"时必做）

回答两个问题之一：
1. **等价性**：内挂 auto 模式提取的键鼠操作序列，脱离决策引擎回放时，能否复现同样的游戏行为——序列是 bot 行为的无损编码吗
2. **复刻质量**：在（复刻/原版）目标游戏上回放同一份序列，能否得到与录制端一致的结果——任何偏差（点击落空/菜单错位/逻辑分歧）都会让回放中断或结果偏离，直接暴露复刻缺陷的位置

**流程**（注入层/输入相关代码改动后重跑；评估复刻质量时对每份轨迹跑一次）：

1. **Run A（内挂提取）**：`-run-mode auto -level N -seed <s> -speed 1`（固定 seed、固定相机、固定分辨率 800x600、fresh run）→ 产出 `sequence_A.json` + `battlelog_A` + `recording_A.mp4`
2. **Run B（内挂自检 replay）**：同一构建，`-run-mode replay -sequence sequence_A.json -seed <s> -speed 1`（或环境变量 `AUTOGAMER_RUN_MODE=replay AUTOGAMER_SEQUENCE=...`）→ 产出 `battlelog_B` 与重放的序列。**自检的意义**：同一构建内"录 → 放"一致 ⇒ 排除轨迹本身的问题（坐标/时序/录制缺陷）；之后再在目标游戏上回放，出现的偏差才能归因于目标游戏而非轨迹质量
3. **Run C（独立回放验证）**：目标游戏（复刻/原版）放入 `code/SequenceReplayer.cs` 构建后，用 `scripts/run_replay.sh` 一键启动：
   ```bash
   bash scripts/run_replay.sh <game_exe> sequence_A.json <output_dir> \
     -camera-pos <录制时位置,如 7.5,20,8.5> \
     -camera-rot <录制时旋转,如 90,0,0> \
     -camera-fov <录制时FOV,如 60> \
     -camera-disable-script <录制时禁用的相机脚本,如 StrategyCamera>
   ```
   脚本自动：启动游戏（窗口模式）→ 查找并持续置顶窗口 → 等待回放完成（300s 超时）→ 打印产物。`-camera-*` 参数按 `sequence_A.json` header 中的**相机配置记录**填写（录制端确定相机时已固化，不靠人工回忆），产出 `replay.mp4` + `done.json`（executed/total/视频路径/帧数）+ `player.log`
4. **四层对比**（指标体系与判定标准详见 `references/replay-evaluation.md`）：
   - **执行层**：`done.json` 的 executed/total——事件全部执行、无崩溃、未超时 ⇒ 轨迹走通
   - **终局层**：battlelog summary（胜负/用时/回合数）与录制端一致
   - **状态层**：battlelog 逐回合状态快照 diff → 报告**首个分歧回合**（把排查范围缩到该回合附近活跃的系统）
   - **视觉层**：`recording_A.mp4` vs `replay.mp4`，帧相似度 / VLM 问答评估（界面布局、单位位置、动画表现）
5. **结论判定与诊断**：
   - 四层全一致 ⇒ 等价性成立 / 复刻质量高（功能、逻辑、表现三层全对齐）
   - 不一致时**先按诊断表排除环境因素**（客户区偏移 / 场景切换时序 / 相机参数不一致 / 窗口被遮挡 / 确定性破坏——见 `references/replay-evaluation.md` 诊断表），**排除后仍复现的偏差即是等价性断裂或复刻缺陷的直接证据**——用 `meta` 字段精确定位"哪一步、哪个元素"出错
   - Run B 自检就不一致 ⇒ 问题在轨迹本身或注入层（回查步骤2"键鼠原语"列、3.2 的坐标链路），不要急着归因目标游戏

> 详细规格（序列 schema、录制规则、三种回放方案的技术实现、确定性清单、diff 脚本、常见坑）见 `references/sequence-record-replay.md`；评估指标体系与诊断表见 `references/replay-evaluation.md`；`SequenceReplayer` 的完整参数表、Python 外部驱动源码、相机规格、跨平台方案见 `references/sequence-replay-guide.md`。

### Phase 1 — 策略验证（bot 还不能通关时）

每轮只跑 1 局（遵循"实验设计原则"——每轮只改一个变量）：

1. 执行 run_sweep（每关 1 局）
2. 收集视频 mp4 + 简化版战斗 log
3. **VLM 视频分析**：将完整 mp4 发送给 qwen3.6-plus（通过 dashscope API）进行视频分析
4. **联合分析**：VLM 视觉洞察 + 战斗 log 数值数据 → 综合诊断报告
5. 评估子 agent 审查报告
6. 根据报告自动优化 bot 代码
7. Git commit
8. **更新知识库** `findings.json`（记录本轮发现/验证/推翻的假设）
9. 决策：能通关 → 切换到 Phase 2；不能通关 → 下一轮

> **何时从 Phase 1 切换到批量 sweep**：当连续 3 轮单局结果波动 < 20% 时，单局不再有区分力。立即切换到 100 局批量 sweep，用统计方法评估改动效果。批量 sweep 后运行"瓶颈自动诊断"脚本，识别瓶颈类型并指导下一步优化方向。

### Phase 2 — 稳定性验证（bot 能通关后）

每轮跑多局：

1. 执行 run_sweep（每关多局）
2. 收集所有局视频 + 战斗 log
3. **VLM 视频分析**：模型自行决定分析范围——只分析失败局、抽样分析成功局、或对比通关/失败局
4. 联合分析 → 综合诊断报告（聚焦稳定性和边界情况）
5. **运行瓶颈自动诊断脚本**（存活分布、升级vs性能、技能组合、伤害来源等）
6. 评估子 agent 审查报告
7. 自动优化 bot 代码
8. Git commit
9. **更新知识库** `findings.json`
10. 决策：通关率达到目标 → 结束迭代；连续3轮无改善 → 报告停滞；否则继续

### VLM 视频分析实现

使用 **qwen3.6-plus** 通过 dashscope OpenAI 兼容接口进行视频分析：

```python
from openai import OpenAI
import base64, os

client = OpenAI(
    api_key=os.environ["DASHSCOPE_API_KEY"],
    base_url="https://dashscope.aliyuncs.com/compatible-mode/v1"
)

# 视频文件读取
with open(video_path, "rb") as f:
    video_b64 = base64.b64encode(f.read()).decode("utf-8")

# 发送视频给 VLM 分析
response = client.chat.completions.create(
    model="qwen3.6-plus",
    messages=[{
        "role": "user",
        "content": [
            {"type": "video_url", "video_url": {"url": f"data:video/mp4;base64,{video_b64}", "fps": 2}},
            {"type": "text", "text": "<LLM自行撰写的分析prompt>"}
        ]
    }]
)
```

**fps 参数**：控制 VLM 抽帧密度。建议：
- 短视频（<30秒）：fps=2（保留完整节奏）
- 中等视频（30-120秒）：fps=1（平衡细节和 token）
- 长视频（>120秒）：fps=0.5（减少 token 消耗，仍保留完整叙事）

### VLM 分析 prompt — 模型自行决定原则

skill **不提供固定 prompt 模板**。执行 skill 的 LLM 拥有完整上下文（步骤1分析文档、步骤2策略设计、当前 bot 代码、本轮已知问题），基于这些信息自行撰写 VLM 分析 prompt。

prompt 撰写方向（按需选择，不必全部包含）：
1. **策略执行流分析**：从开始到结束，每阶段 bot 在做什么、为什么做
2. **策略节奏感**：哪些时段 bot 在推进、哪些在犹豫、哪些在被动应对
3. **失败/成功的完整叙事**：追溯整个过程怎么走到这个结局的
4. **被浪费的机会**：哪些时刻 bot 本可以做出更好的反应但策略没覆盖
5. **画面外状态推断**：根据 bot 的行为推断可能的内部状态（HP、冷却等）
6. **通关局 vs 失败局对比**（Phase 2 时）：关键差异在哪里，失败局从什么时刻开始偏离
7. **特定问题追踪**：针对本轮已知问题聚焦观察
8. **回放对比**（做过 Run C 时）：录制端与回放端两段视频逐时段对比——行为轨迹、点击目标、终局画面是否一致

prompt 撰写建议：
- 聚焦**策略执行诊断**，不是视觉描述（"画面上有个红色圆点"没有诊断价值）
- 针对**当前诊断目标**（如果本轮已知问题是闪避决策，prompt 应聚焦闪避相关的观察）
- 要求 VLM 按**时间段**组织分析（"第0-5秒做什么、第5-15秒做什么"），不是碎片化描述
- Phase 1 的 prompt 倾向"完整策略评估"
- Phase 2 的 prompt 倾向"对比分析"或"特定问题追踪"

> 详细原则参考：`references/vlm-analysis-principle.md`

### VLM 分析结果与 battle log 的合并

VLM 分析结果提供**视觉洞察**（策略在画面上的表现），battle log 提供**精确数据**（具体数值和决策推理）。两者合并为综合诊断：

- VLM 说"bot 在第30秒被两个远程敌人夹击在狭窄通道中无法闪避"
- log 说"第30秒 dodge_decision: chosen_action=继续攻击, reason=近战距离更近(效用0.7) > 远程距离较远(效用0.3)"
- 合并诊断："效用评分只考虑了距离而忽略了通道限制，需要在狭窄地形中对远程威胁加权"

这种合并才能给出精准可执行的代码改动方向。

### 验证评估 — 报告审查

报告生成后，使用 Agent 工具启动一个评估子 agent（subagent_type: general-purpose），审查报告质量。

评估子 agent 收到的指令应包含：
- 战斗 log 原始数据（JSON 文件）
- VLM 视频分析结果
- 总结报告内容
- 步骤1分析报告中的敌人威胁等级和关键机制

评估内容：

| 检查项 | 说明 |
|--------|------|
| 数据支撑度 | 每个结论是否有战斗 log 数据 **或** VLM 视觉证据支撑，而非凭印象推断 |
| VLM 洞察相关性 | VLM 分析结果是否与策略相关（不是单纯的视觉描述） |
| 胜负归因合理性 | 胜利/失败的原因是否追溯到具体决策失误（通过视频 + log 交叉验证） |
| 优化建议可执行性 | 优化建议是否是具体可执行的代码改动方向 |
| 优化建议针对性 | 优化建议是否针对 VLM 分析和决策追踪暴露的具体问题 |
| 视-log 结合质量 | 视觉洞察和 log 数据是否有效结合得出诊断结论 |
| 回放对比归因严谨性 | （做过 Run C 时）偏差是否先排除了环境因素再归因于复刻缺陷/等价性断裂 |

### 每轮报告格式

每轮生成的报告必须包含以下结构：

```markdown
# Round N 测试报告

## 当前阶段
Phase 1（策略验证）/ Phase 2（稳定性验证）

## 本轮改动
- 改动1: [具体改动描述，对比上一轮代码]

## 本轮结果
- Phase 1: 通关/失败，X秒
- Phase 2: 通关率 X%（目标: Y%），平均用时 X秒

## VLM 视频分析洞察
- 洞察1: [从视频中观察到的策略执行问题]
- 洞察2: [...]

## 战斗 log 数据支撑
- 关键指标: [HP变化、伤害来源分布等]

## 联合诊断
- 问题: [结合视频洞察 + log 数据的精确定位]
  原因: [...]
  建议: [具体可执行的代码改动]

## 回放验证（本轮做过时）
- 自检 replay: 一致/不一致（不一致时首个分歧点）
- 独立回放: done.json executed/total、四层对比结论、按诊断表的归因

## 优化方向
- 优化1: [具体代码改动建议]
```

### 迭代终止条件

- 目标指标达到用户设定值 → 迭代结束，输出最终报告
- Phase 1 能通关 → 切换到 Phase 2
- Phase 2 通关率达到目标 → 迭代结束
- 连续3轮无显著改善 → 向用户报告停滞状态，用户决定是否继续
- 每轮开始前检查：如果上一轮评估发现关键问题（如 bot 代码编译失败），暂停迭代等待修正

### 最终报告

迭代结束时生成最终报告，汇总所有轮次的演变趋势，并增加 VLM 分析演变趋势：

```markdown
# 最终测试报告

## 目标
[用户设定的目标指标]

## 达成情况
- 最终目标指标: X% (目标: Y%)
- 总迭代轮数: N
- Phase 1 轮数: X, Phase 2 轮数: Y

## 演变趋势
- Round 1: 通关率 30% → 主要问题: ...
- Round 2: 通关率 55% → 主要改进: ...
- ...
- Round N: 通关率 X% → 达成目标

## VLM 分析演变趋势
[VLM 视频分析中观察到的策略执行演变趋势]

## 回放验证结论
[等价性是否成立 / 复刻质量评估结论；若做了多轮 Run C，记录偏差修复的演变]

## 关键转折
[哪些改动带来了最大提升]

## 遗留问题
[如果目标未 100% 达成，记录剩余问题]
```
