using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace AutoGamer
{
    /// <summary>序列事件（JsonUtility 可序列化的扁平结构）</summary>
    [Serializable]
    public class AGSeqEvent
    {
        public int i;
        public int frame;
        public float t;
        public string op;       // world_click | screen_click | gui_click | right_click | replay_click
        public string meta;     // 人类可读元数据（如 "unit@(2,11)" / "ActionPopup/Capture"）
        public int sx, sy;      // Unity Screen 坐标（左下角原点，Y 向上）— 回放执行依据
        public string inject;   // os_mouse
        public string wait;     // 前置等待谓词 id
    }

    // ====================================================================
    //  通用注入器（不依赖任何游戏类型）
    // ====================================================================

    /// <summary>
    /// 唯一注入口（OS 级真实鼠标版 / 通用版）：用 xdotool 注入 OS 级鼠标事件。
    /// 不依赖任何游戏类型——只接受屏幕坐标和世界坐标。
    /// 
    /// 通用接口：
    ///   ClickScreenPos(sx, sy)           — 点击 Unity Screen 坐标
    ///   ClickWorldPos(worldPos)          — 点击世界坐标（自动转屏幕坐标）
    ///   ClickColliderCenter(collider)    — 点击碰撞体顶部（避免被地面遮挡）
    ///   RightClick()                     — 右键
    ///   ReplayClick(sx, sy, button)      — 回放按屏幕坐标
    /// 
    /// 游戏适配层通过调用上述通用接口实现具体操作。
    /// </summary>
    public static class AGInputInjector
    {
        public static event Action<AGSeqEvent> OnInjected;
        public static int OpCount { get; private set; }

        static bool _verified;
        static int _gvX, _gvY, _gvW, _gvH;

        // ---- 窗口检测 ----

        static bool VerifyXdotool()
        {
            if (_verified) return true;
            try { DetectGameViewPosition(); _verified = true; return true; }
            catch (Exception e) { AGLog.Warn("xdotool 不可用: " + e.Message); return false; }
        }

        static void DetectGameViewPosition()
        {
#if UNITY_EDITOR
            var asm = System.Reflection.Assembly.GetAssembly(typeof(UnityEditor.EditorWindow));
            var gameViewType = asm.GetType("UnityEditor.GameView");
            if (gameViewType != null)
            {
                var gv = UnityEditor.EditorWindow.GetWindow(gameViewType);
                if (gv != null)
                {
                    var pos = gv.position;
                    _gvX = (int)pos.x; _gvY = (int)pos.y;
                    _gvW = (int)pos.width; _gvH = (int)pos.height;
                    AGLog.Info($"xdotool 已就绪, GameView (Editor) ({_gvX},{_gvY}) {_gvW}x{_gvH}");
                    return;
                }
            }
            // fallback: xdotool 查找编辑器窗口
            var winId = RunBash("xdotool search --onlyvisible --name \"Tuanjie\" 2>/dev/null | tail -1");
            if (!string.IsNullOrEmpty(winId?.Trim()))
            {
                var geo = RunBash($"xdotool getwindowgeometry --shell {winId.Trim()}");
                ParseGeometry(geo);
            }
#else
            // Player: 按产品名查找窗口
            var productName = Application.productName;
            var winId = "";
            for (int i = 0; i < 10; i++)
            {
                winId = RunBash($"xdotool search --onlyvisible --name \"{productName}\" 2>/dev/null | head -1")?.Trim();
                if (!string.IsNullOrEmpty(winId)) break;
                System.Threading.Thread.Sleep(500);
            }
            if (string.IsNullOrEmpty(winId))
                winId = RunBash("xdotool getactivewindow 2>/dev/null")?.Trim();

            if (!string.IsNullOrEmpty(winId))
            {
                var geo = RunBash($"xdotool getwindowgeometry --shell {winId}");
                ParseGeometry(geo);

                // 强制窗口模式（Tuanjie/Unity Player 默认全屏不接收 X11 事件）
                // overrideredirect+windowsize → 窗口管理器不再管理窗口
                // 先用 wmctrl 移动窗口到 (0,0)（含标题栏）
                RunBash($"wmctrl -i -r {winId} -e 0,0,0,{Screen.width},{Screen.height}");
                System.Threading.Thread.Sleep(500);
                // 再 overrideredirect+windowsize（取消标题栏 → 客户区从 (0,0) 开始）
                RunBash($"xdotool set_window --overrideredirect 1 {winId}; xdotool windowsize {winId} {Screen.width} {Screen.height}");
                System.Threading.Thread.Sleep(500);

                // 重新检测调整后的窗口几何
                var geo2 = RunBash($"xdotool getwindowgeometry --shell {winId}");
                ParseGeometry(geo2);
            }
#endif
            _gvW = _gvW > 0 ? _gvW : Screen.width;
            _gvH = _gvH > 0 ? _gvH : Screen.height;
            AGLog.Info($"xdotool 已就绪, GameView ({_gvX},{_gvY}) {_gvW}x{_gvH} Screen={Screen.width}x{Screen.height}");
        }

        static void ParseGeometry(string geo)
        {
            if (string.IsNullOrEmpty(geo)) return;
            foreach (var line in geo.Split('\n'))
            {
                var parts = line.Split('=');
                if (parts.Length != 2) continue;
                if (int.TryParse(parts[1].Trim(), out int v))
                {
                    var key = parts[0].Trim();
                    if (key == "X") _gvX = v;
                    else if (key == "Y") _gvY = v;
                    else if (key == "WIDTH") _gvW = v;
                    else if (key == "HEIGHT") _gvH = v;
                }
            }
        }

        // ---- 坐标转换 ----

        /// <summary>Unity Screen 坐标 → X11 屏幕绝对坐标</summary>
        static (int x, int y) ToScreenCoord(int unityX, int unityY)
        {
            float scaleX = _gvW > 0 && Screen.width > 0 ? (float)_gvW / Screen.width : 1f;
            float scaleY = _gvH > 0 && Screen.height > 0 ? (float)_gvH / Screen.height : 1f;
            int absX = _gvX + (int)(unityX * scaleX);
            int absY = _gvY + (int)((Screen.height - unityY) * scaleY);
            return (absX, absY);
        }

        /// <summary>世界坐标 → Unity Screen 坐标</summary>
        public static Vector2 WorldToScreen(Vector3 worldPos)
        {
            var cam = Camera.main;
            if (cam == null) { var go = GameObject.Find("Main Camera"); cam = go != null ? go.GetComponent<Camera>() : null; }
            if (cam == null) return Vector2.zero;
            var sp = cam.WorldToScreenPoint(worldPos);
            return new Vector2(sp.x, sp.y);
        }

        // ---- xdotool 注入 ----

        static void XdoClick(int unityX, int unityY, int button)
        {
            var (absX, absY) = ToScreenCoord(unityX, unityY);
            int xdoBtn = button == 0 ? 1 : (button == 1 ? 3 : 2);
            RunBash($"xdotool mousemove {absX} {absY} mousedown {xdoBtn} mouseup {xdoBtn}", 500);
        }

        static void XdoMove(int unityX, int unityY)
        {
            var (absX, absY) = ToScreenCoord(unityX, unityY);
            RunBash($"xdotool mousemove {absX} {absY}", 500);
        }

        // ---- 核心点击协程 ----

        static IEnumerator ClickAt(Vector2 screenPos, int button, string op, string meta, string waitId)
        {
            if (!VerifyXdotool()) { AGLog.Warn("xdotool 不可用"); yield break; }
            int sx = Mathf.RoundToInt(screenPos.x);
            int sy = Mathf.RoundToInt(screenPos.y);
            var (absX, absY) = ToScreenCoord(sx, sy);
            AGLog.Info($"ClickAt screen=({sx},{sy}) x11=({absX},{absY}) meta={meta}");

            XdoMove(sx, sy);
            yield return null;
            XdoClick(sx, sy, button);
            yield return null;
            yield return null;
            yield return null;
            Emit(op, meta, sx, sy, waitId);
        }

        // ================================================================
        //  通用接口（不依赖游戏类型）
        // ================================================================

        /// <summary>点击 Unity Screen 坐标</summary>
        public static IEnumerator ClickScreenPos(int sx, int sy, string meta = "", string waitId = "")
        {
            yield return ClickAt(new Vector2(sx, sy), 0, "screen_click", meta, waitId);
        }

        /// <summary>点击世界坐标（自动转屏幕坐标）</summary>
        public static IEnumerator ClickWorldPos(Vector3 worldPos, string meta = "", string waitId = "")
        {
            var screen = WorldToScreen(worldPos);
            yield return ClickAt(screen, 0, "world_click", meta, waitId);
        }

        /// <summary>点击碰撞体顶部（避免被地面/地块遮挡）</summary>
        public static IEnumerator ClickColliderTop(Collider col, string meta = "", string waitId = "")
        {
            if (col == null) { AGLog.Warn("ClickColliderTop(null)"); yield break; }
            var worldPos = col.bounds.center + Vector3.up * (col.bounds.size.y * 0.5f + 0.1f);
            yield return ClickWorldPos(worldPos, meta, waitId);
        }

        /// <summary>点击碰撞体中心</summary>
        public static IEnumerator ClickColliderCenter(Collider col, string meta = "", string waitId = "")
        {
            if (col == null) { AGLog.Warn("ClickColliderCenter(null)"); yield break; }
            yield return ClickWorldPos(col.bounds.center, meta, waitId);
        }

        /// <summary>右键点击（当前鼠标位置或屏幕中心）</summary>
        public static IEnumerator RightClick(string waitId = "")
        {
            yield return ClickAt(new Vector2(Screen.width / 2, Screen.height / 2), 1, "right_click", "", waitId);
        }

        /// <summary>键盘按键（按下→释放）</summary>
        public static IEnumerator PressKey(string key, string waitId = "")
        {
            RunBash($"xdotool keydown {key}", 500);
            yield return null;
            RunBash($"xdotool keyup {key}", 500);
            yield return null;
            Emit("key_press", key, 0, 0, waitId);
        }

        /// <summary>鼠标拖拽（从起点到终点）</summary>
        public static IEnumerator Drag(int fromX, int fromY, int toX, int toY, string meta = "", string waitId = "")
        {
            if (!VerifyXdotool()) yield break;
            XdoMove(fromX, fromY);
            yield return null;
            var (ax, ay) = ToScreenCoord(fromX, fromY);
            RunBash($"xdotool mousemove {ax} {ay} mousedown 1", 500);
            yield return null;
            // 逐帧移动到终点
            int steps = 5;
            for (int i = 1; i <= steps; i++)
            {
                int x = fromX + (toX - fromX) * i / steps;
                int y = fromY + (toY - fromY) * i / steps;
                XdoMove(x, y);
                yield return null;
            }
            var (bx, by) = ToScreenCoord(toX, toY);
            RunBash($"xdotool mousemove {bx} {by} mouseup 1", 500);
            yield return null;
            Emit("drag", meta, toX, toY, waitId);
        }

        /// <summary>回放专用：按屏幕坐标重放点击</summary>
        public static IEnumerator ReplayClick(int sx, int sy, int button, string waitId = "")
        {
            yield return ClickAt(new Vector2(sx, sy), button, "replay_click", "", waitId);
        }

        public static void ResetCount() { OpCount = 0; }

        // ---- 内部工具 ----

        static void Emit(string op, string meta, int sx, int sy, string waitId)
        {
            OpCount++;
            var e = new AGSeqEvent
            {
                i = OpCount - 1, frame = Time.frameCount, t = Time.unscaledTime,
                op = op, meta = meta ?? "", sx = sx, sy = sy,
                inject = "os_mouse", wait = waitId ?? ""
            };
            OnInjected?.Invoke(e);
        }

        static string RunBash(string script, int timeoutMs = 3000)
        {
            try
            {
                var p = new Process();
                p.StartInfo = new ProcessStartInfo("/bin/bash", $"-c '{script}'")
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true
                };
                p.Start();
                p.WaitForExit(timeoutMs);
                return p.StandardOutput.ReadToEnd();
            }
            catch (Exception e) { AGLog.Warn("bash 失败: " + e.Message); return null; }
        }
    }

    // ====================================================================
    //  游戏适配层（本坦克游戏的特化调用——其他游戏替换此部分即可）
    // ====================================================================

    /// <summary>
    /// 本坦克游戏的适配层：把游戏对象转换为通用注入器能理解的世界坐标/屏幕坐标。
    /// 其他游戏替换这个类即可，AGInputInjector 不需要改。
    /// </summary>
    public static class AGGameAdapter
    {
        static AGObserver O { get { return AGObserver.Instance; } }

        /// <summary>点击单位（坦克）</summary>
        public static IEnumerator ClickUnit(Unit unit, string waitId)
        {
            if (unit == null) yield break;
            var col = unit.GetComponent<Collider>();
            var meta = $"unit@{unit.TilePosition()}";
            if (col != null)
                yield return AGInputInjector.ClickColliderTop(col, meta, waitId);
            else
                yield return AGInputInjector.ClickWorldPos(unit.transform.position + Vector3.up * 1.2f, meta, waitId);
        }

        /// <summary>点击地块（移动目标/空地点击）</summary>
        public static IEnumerator ClickTile(Point tile, string waitId)
        {
            var meta = $"tile@({tile.x},{tile.y})";
            yield return AGInputInjector.ClickWorldPos(new Vector3(tile.x, 0.5f, tile.y), meta, waitId);
        }

        /// <summary>点击建筑</summary>
        public static IEnumerator ClickBuilding(Building b, string waitId)
        {
            if (b == null) yield break;
            var col = b.GetComponent<Collider>();
            var meta = $"building@{b.TilePosition()}";
            if (col != null && col.enabled)
                yield return AGInputInjector.ClickColliderTop(col, meta, waitId);
            else
                yield return AGInputInjector.ClickWorldPos(b.transform.position + Vector3.up * 3f, meta, waitId);
        }

        /// <summary>点击 IMGUI 菜单按钮（反射读 Rect + Items + ButtonHeight）</summary>
        public static IEnumerator ClickMenuButton(Menu menu, string item, string waitId)
        {
            var menuName = menu != null ? menu.GetType().Name : "?";
            int sx = 0, sy = 0;
            var rect = AGReflection.GetMenuRect(menu);
            if (rect.HasValue)
            {
                int btnIdx = -1, btnHeight = 30;
                try
                {
                    var itemsField = menu.GetType().GetField("Items",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (itemsField != null)
                    {
                        var items = itemsField.GetValue(menu) as System.Collections.IList;
                        if (items != null)
                            for (int i = 0; i < items.Count; i++)
                                if (items[i] != null && items[i].ToString() == item) { btnIdx = i; break; }
                    }
                    var bhField = menu.GetType().GetField("ButtonHeight",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (bhField != null) btnHeight = (int)bhField.GetValue(menu);
                }
                catch (System.Exception) { }

                sx = (int)(rect.Value.x + rect.Value.width / 2);
                if (btnIdx >= 0)
                {
                    int btnCenterY = (int)(rect.Value.y + 4 + btnIdx * btnHeight + btnHeight / 2f);
                    sy = Screen.height - btnCenterY;
                }
                else
                    sy = Screen.height - (int)(rect.Value.y + rect.Value.height / 2);
            }
            AGLog.Info($"GUI 点击 {menuName}/{item} @({sx},{sy})");
            yield return AGInputInjector.ClickScreenPos(sx, sy, $"{menuName}/{item}", waitId);
        }

        /// <summary>点击教程全屏遮罩</summary>
        public static IEnumerator ClickTutorial(string waitId)
        {
            yield return AGInputInjector.ClickScreenPos(Screen.width / 2, Screen.height / 2, "tutorial_dismiss", waitId);
        }

        /// <summary>右键取消</summary>
        public static IEnumerator RightClick(string waitId)
        {
            yield return AGInputInjector.RightClick(waitId);
        }
    }
}
