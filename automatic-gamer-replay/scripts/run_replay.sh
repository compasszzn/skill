#!/bin/bash
# run_replay.sh — 通用回放启动脚本
#
# 用法：
#   bash ./run_replay.sh <game_executable> <sequence.json> [output_dir] [extra_replayer_args...]
#
# 用例（以 OpenAW3D 为例，<SKILL_DIR> 为本 skill 的 automatic-gamer-replay 目录）：
#   cd <SKILL_DIR>/scripts
#   bash ./run_replay.sh /home/unitydev/project/Builds/linux/OpenAW3D \
#     <SKILL_DIR>/examples/example_sequence.json \
#     ./replay_output \
#     -camera-pos 7.5,20,8.5 -camera-rot 90,0,0 -camera-fov 60 \
#     -camera-disable-script StrategyCamera
#
#   执行后自动：启动游戏 → 窗口置顶 → 逐事件回放 → 录制 mp4 → 输出 done.json
#   结果在 ./replay_output/：done.json / replay.mp4 / player.log
#
# 前提：SequenceReplayer.cs 已放入游戏 Assets/ 目录并重新构建
# 依赖：xdotool / x11-utils (xwininfo) / ffmpeg

set -uo pipefail   # no -e: we handle errors explicitly (xdotool returns non-zero when no window found)
export DISPLAY="${DISPLAY:-:0.0}"

GAME="${1:?用法: $0 <game_executable> <sequence.json> [output_dir] [extra_args...]}"
SEQ="${2:?缺少 sequence.json 路径}"
OUT="${3:-$(dirname "$SEQ")/replay_output}"
shift 3 2>/dev/null || shift $#
EXTRA_ARGS=("$@")

# ---- 检查依赖 ----
need_install=()
command -v xdotool  >/dev/null 2>&1 || need_install+=(xdotool)
command -v xwininfo  >/dev/null 2>&1 || need_install+=(x11-utils)
command -v ffmpeg    >/dev/null 2>&1 || need_install+=(ffmpeg)
if [ ${#need_install[@]} -gt 0 ]; then
  echo "缺少依赖: ${need_install[*]}"
  echo "安装: sudo apt install -y ${need_install[*]}"
  exit 1
fi

# ---- 准备输出目录 ----
rm -rf "$OUT"; mkdir -p "$OUT"

# ---- 启动游戏（窗口模式） ----
# -logFile 必须在 EXTRA_ARGS 之前，否则 Unity 可能不解析它后面的参数
"$GAME" \
  -screen-fullscreen 0 -screen-width 800 -screen-height 600 \
  -sequence "$SEQ" \
  -replay-output-dir "$OUT" \
  -replay-quit-on-end true \
  -replay-record true \
  -logFile "$OUT/player.log" \
  "${EXTRA_ARGS[@]}" &
GAMEPID=$!
echo "游戏 PID: $GAMEPID"

# ---- 等待游戏窗口出现并置顶 ----
# 简单策略：等 5s 让游戏初始化，然后按产品名查找窗口
WID=""
sleep 5
for i in $(seq 1 30); do
  # 尝试匹配可执行文件名作为窗口标题
  WID=$(xdotool search --onlyvisible --name "$(basename "$GAME")" 2>/dev/null | head -1 || true)
  [ -n "$WID" ] && break
  # 尝试匹配所有可见窗口中属于该 PID 的
  WID=$(xdotool search --onlyvisible --name "." 2>/dev/null | while read w; do
    pid=$(xdotool getwindowpid "$w" 2>/dev/null || echo 0)
    [ "$pid" = "$GAMEPID" ] && echo "$w" && break
  done || true)
  [ -n "$WID" ] && break
  sleep 1
done

if [ -z "$WID" ]; then
  echo "警告: 未找到游戏窗口，等待游戏自行退出"
else
  echo "游戏窗口: $WID"
  xdotool windowactivate "$WID" 2>/dev/null || true
  xdotool windowraise  "$WID" 2>/dev/null || true
fi

# ---- 持续置顶（防止其他窗口遮挡） ----
( while kill -0 "$GAMEPID" 2>/dev/null; do
    [ -n "$WID" ] && xdotool windowraise "$WID" 2>/dev/null || true
    sleep 3
  done ) &
RAISEPID=$!

# ---- 等待回放完成 ----
DEADLINE=$((SECONDS + 300))
while [ $SECONDS -lt $DEADLINE ]; do
  [ -f "$OUT/done.json" ] && break
  kill -0 "$GAMEPID" 2>/dev/null || { echo "游戏已退出"; break; }
  sleep 2
done

kill "$RAISEPID" 2>/dev/null || true

# ---- 收尾 ----
for i in $(seq 1 15); do kill -0 "$GAMEPID" 2>/dev/null || break; sleep 1; done
kill "$GAMEPID" 2>/dev/null || true
wait "$GAMEPID" 2>/dev/null || true
pkill -f "ffmpeg.*replay.mp4" 2>/dev/null || true

echo "=== done.json ==="
cat "$OUT/done.json" 2>/dev/null || echo "未生成（回放可能未完成）"
echo "=== 输出文件 ==="
ls -la "$OUT"
