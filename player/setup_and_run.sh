#!/bin/bash
# setup_and_run.sh — 一键安装依赖 + 放入 SequenceReplayer.cs 到游戏工程
#
# 用法：
#   bash ./setup_and_run.sh <game_project_dir> <sequence.json> [extra_replayer_args...]
#
# 用例（以 OpenAW3D 为例）：
#   cd /home/unitydev/skill/player
#   bash ./setup_and_run.sh /home/unitydev/project /home/unitydev/skill/player/example_sequence.json \
#     -camera-pos 7.5,20,8.5 -camera-rot 90,0,0 -camera-fov 60 \
#     -camera-disable-script StrategyCamera
#
#   执行后会自动：
#     1. 安装 xdotool / x11-utils / ffmpeg
#     2. 复制 SequenceReplayer.cs → /home/unitydev/project/Assets/Scripts/
#   然后用 Unity Editor 重新构建游戏，再执行 run_replay.sh 启动回放
#
# 前提：Unity Editor（或 Tuanjie Editor）已安装

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

GAME_PROJECT="${1:?用法: $0 <game_project_dir> <sequence.json> [extra_replayer_args...]}"
SEQ="${2:?缺少 sequence.json 路径}"
shift 2 2>/dev/null || shift $#
EXTRA_ARGS=("$@")

# ============================================================
#  步骤 1：安装系统依赖（一次性）
# ============================================================
echo "=== 步骤 1: 检查/安装系统依赖 ==="

need_install=()
command -v xdotool  >/dev/null 2>&1 || need_install+=(xdotool)
command -v xwininfo  >/dev/null 2>&1 || need_install+=(x11-utils)
command -v ffmpeg    >/dev/null 2>&1 || need_install+=(ffmpeg)

if [ ${#need_install[@]} -gt 0 ]; then
  echo "  缺少: ${need_install[*]}，正在安装..."
  sudo apt-get update -qq
  sudo apt-get install -y -qq "${need_install[@]}"
  echo "  安装完成"
else
  echo "  依赖已就绪"
fi

# ============================================================
#  步骤 2：放入 SequenceReplayer.cs 到游戏工程
# ============================================================
echo "=== 步骤 2: 放入 SequenceReplayer.cs ==="

# 找到 Assets/Scripts 目录（没有就创建）
ASSETS_SCRIPTS="$GAME_PROJECT/Assets/Scripts"
if [ ! -d "$GAME_PROJECT/Assets" ]; then
  echo "  错误: $GAME_PROJECT/Assets 不存在，不是 Unity 工程"
  exit 1
fi
mkdir -p "$ASSETS_SCRIPTS"

REPLAYER_SRC="$SCRIPT_DIR/SequenceReplayer.cs"
if [ ! -f "$REPLAYER_SRC" ]; then
  echo "  错误: 找不到 $REPLAYER_SRC"
  exit 1
fi

cp "$REPLAYER_SRC" "$ASSETS_SCRIPTS/SequenceReplayer.cs"
echo "  已复制 → $ASSETS_SCRIPTS/SequenceReplayer.cs"

# ============================================================
#  步骤 3：调用 run_replay.sh（它会提示用户构建游戏）
# ============================================================
echo ""
echo "=== 步骤 3: 启动回放 ==="
echo "  请确保已用 Unity Editor 重新构建游戏（File → Build And Run 或 Build）"
echo "  然后指定构建产物路径，例如："
echo ""
echo "  $SCRIPT_DIR/run_replay.sh ./GameBuild \"$SEQ\" ./output ${EXTRA_ARGS[*]}"
echo ""

# 如果用户提供了游戏可执行文件作为第 3 个参数，直接跑
GAME_EXE="${EXTRA_ARGS[0]:-}"
if [ -n "$GAME_EXE" ] && [ -x "$GAME_EXE" ]; then
  # 第一个 extra arg 是可执行文件路径，去掉它，剩下的才是 replayer 参数
  REMAINING_ARGS=("${EXTRA_ARGS[@]:1}")
  exec "$SCRIPT_DIR/run_replay.sh" "$GAME_EXE" "$SEQ" "./replay_output" "${REMAINING_ARGS[@]}"
else
  echo "  （未检测到游戏可执行文件路径，请手动执行 run_replay.sh）"
  echo "  用法: $SCRIPT_DIR/run_replay.sh <game_executable> <sequence.json> [output_dir] [replayer_args...]"
fi
