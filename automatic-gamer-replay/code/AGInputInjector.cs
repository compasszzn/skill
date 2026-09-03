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
        static string _windowWid;

        // ---- 窗口检测 ----

        static bool VerifyXdotool()
        {
            if (_verified) return true;
            try { DetectGameViewPosition(); _verified = true; return true; }
            catch (Exception e) { AGLog.Warn("xdotool 不可用: " + e.Message); return false; }
        }

        /// <summary>每次点击前刷新窗口几何（防止窗口被 WM 移动导致点击错位）。</summary>
        static void RefreshGeometry()
        {
            // 仅在 Player 模式下需要刷新（Editor 模式 GameView 位置由 Unity 管理）
#if !UNITY_EDITOR
            DetectWindowClientPosition();
#endif
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
                _windowWid = winId.Trim();
                DetectWindowClientPosition();
            }
#else
            // Player: 按产品名查找窗口
            DetectWindowClientPosition();
#endif
            _gvW = _gvW > 0 ? _gvW : Screen.width;
            _gvH = _gvH > 0 ? _gvH : Screen.height;
            AGLog.Info($"xdotool 已就绪, GameView ({_gvX},{_gvY}) {_gvW}x{_gvH} Screen={Screen.width}x{Screen.height}");
        }

        /// <summary>
        /// 获取窗口客户区（client area）的绝对坐标和大小。
        /// 
        /// 关键：xdotool getwindowgeometry 在带 WM 装饰的窗口上返回外框(frame)坐标，
        /// 包含标题栏和边框。点击坐标必须相对客户区，否则会整体偏移
        /// （实测 xfwm4：外框 (10,85) vs 客户区 (5,56)，_NET_FRAME_EXTENTS=5,5,29,5）。
        /// 
        /// 优先 xwininfo（客户区绝对坐标）；后备 xdotool + _NET_FRAME_EXTENTS 修正；
        /// 再后备 xdotool 裸值（仅对无装饰/override-redirect 窗口准确）。
        /// </summary>
        static void DetectWindowClientPosition()
        {
            // 查找窗口 ID（首次或窗口丢失时）
            if (string.IsNullOrEmpty(_windowWid))
            {
                var productName = Application.productName;
                for (int i = 0; i < 10; i++)
                {
                    _windowWid = RunBash($"xdotool search --onlyvisible --name \"{productName}\" 2>/dev/null | head -1")?.Trim();
                    if (!string.IsNullOrEmpty(_windowWid)) break;
                    System.Threading.Thread.Sleep(500);
                }
                if (string.IsNullOrEmpty(_windowWid))
                    _windowWid = RunBash("xdotool getactivewindow 2>/dev/null")?.Trim();
            }

            if (string.IsNullOrEmpty(_windowWid))
            {
                AGLog.Warn("无法获取窗口 ID");
                return;
            }

            int gx = -1, gy = -1, gw = -1, gh = -1;

            // 方法 1：xwininfo（客户区绝对坐标，最可靠）
            var xwi = RunBash($"xwininfo -id {_windowWid} 2>/dev/null");
            if (!string.IsNullOrEmpty(xwi))
            {
                foreach (var line in xwi.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("Absolute upper-left X:")) int.TryParse(t.Substring(t.IndexOf(':') + 1).Trim(), out gx);
                    else if (t.StartsWith("Absolute upper-left Y:")) int.TryParse(t.Substring(t.IndexOf(':') + 1).Trim(), out gy);
                    else if (t.StartsWith("Width:")) int.TryParse(t.Substring(t.IndexOf(':') + 1).Trim(), out gw);
                    else if (t.StartsWith("Height:")) int.TryParse(t.Substring(t.IndexOf(':') + 1).Trim(), out gh);
                }
            }

            // 方法 2：xdotool + _NET_FRAME_EXTENTS 修正
            if (gx < 0 || gy < 0 || gw <= 0 || gh <= 0)
            {
                var geo = RunBash($"xdotool getwindowgeometry --shell {_windowWid} 2>/dev/null");
                if (!string.IsNullOrEmpty(geo))
                {
                    foreach (var line in geo.Split('\n'))
                    {
                        var parts = line.Split('=');
                        if (parts.Length != 2) continue;
                        if (int.TryParse(parts[1].Trim(), out int v))
                        {
                            var key = parts[0].Trim();
                            if (key == "X") gx = v;
                            else if (key == "Y") gy = v;
                            else if (key == "WIDTH") gw = v;
                            else if (key == "HEIGHT") gh = v;
                        }
                    }
                    // _NET_FRAME_EXTENTS = left, right, top, bottom → 修正回客户区坐标
                    var ext = RunBash($"xprop -id {_windowWid} _NET_FRAME_EXTENTS 2>/dev/null");
                    if (!string.IsNullOrEmpty(ext) && ext.Contains("="))
                    {
                        var nums = ext.Substring(ext.IndexOf('=') + 1).Split(',');
                        int fl, ft;
                        if (nums.Length == 4 && int.TryParse(nums[0].Trim(), out fl) && int.TryParse(nums[2].Trim(), out ft))
                        {
                            gx -= fl; gy -= ft;
                        }
                    }
                }
            }

            if (gx < 0 || gy < 0 || gw <= 0 || gh <= 0)
            {
                _windowWid = null;  // 窗口可能已销毁，下次重新查找
                return;
            }

            // 几何稳定时少刷日志
            if (gx != _gvX || gy != _gvY || gw != _gvW || gh != _gvH)
                AGLog.Info($"窗口客户区: ({gx},{gy}) {gw}x{gh}");

            _gvX = gx; _gvY = gy; _gvW = gw; _gvH = gh;
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
            RefreshGeometry();  // 每次点击前刷新窗口几何（防止窗口被 WM 移动导致点击错位）
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
