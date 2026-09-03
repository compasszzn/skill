# skill

Codely CLI 技能集合,聚焦 Unity 游戏自动化。

## 目录

| 目录 | 说明 |
|------|------|
| [automatic-gamer/](automatic-gamer/SKILL.md) | 基于 Codely 构建 Unity 游戏自动化助手(Auto Bot)的 skill:分析游戏代码、生成 bot、自动游玩关卡、收集战斗数据,验证关卡可通过性与难度曲线。包含 VLM 视频分析 + 战斗 log 联合诊断的全自动迭代循环。 |
| [player/](player/README.md) | 真实鼠标注入与键鼠序列回放 skill:xdotool OS 级鼠标注入、操作序列录制/回放、内挂与序列回放的等价性验证。 |
| [automatic-gamer-replay/](automatic-gamer-replay/SKILL.md) | automatic-gamer 与 player 的合并版:内挂 bot 经 AGGameAdapter + AGInputInjector(xdotool OS 级真实鼠标)自动游玩并从唯一注入口提取键鼠操作序列(sequence extract),再用 SequenceReplayer 回放序列重新验证(run_replay.sh 一键启动)——内挂与序列回放的等价性验证、游戏复刻质量评估。 |
