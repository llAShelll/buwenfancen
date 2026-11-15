using BepInEx.Unity.IL2CPP;
using System;
using System.Text;
using UnityEngine;

namespace BuWenFanChen;

/// <summary>
/// ★ 2026-11-04：四选一对话选项的常驻显示框（IMGUI）。
///
/// 位置：**聊天输入框左边的空白区**（右边缘贴输入框左边、底边对齐输入框底边）
/// 样式：深色半透明底（参考游戏对话框）+ 淡绿边框由外向内渐变（低亮度）
/// 字体：优先从游戏已加载的 Font 资源里拿（最像正文），其次系统字体
/// 交互：点某一条 → 写进聊天输入框（不发发送）
/// 布局：选项过长自动换行，框跟着向上长高（底边固定）
/// 开关：PromptConfig.OptionsEnabled
/// </summary>
public class OptionsHud : MonoBehaviour
{
    private static readonly BepInEx.Logging.ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("不问凡尘·HUD");

    private static string[] _opts;
    private static GUIStyle _style;
    private static GUIStyle _tipStyle;
    private static Font _font;
    private static int _styleFontSize;
    private static bool _fontLogged;

    // ★ 2026-11-12：选项框外框皮肤（三段式：顶/中/底，config\panel\options_frame_*.png）
    private static Texture2D _frameTop, _frameMid, _frameBottom;
    private static DateTime _frameMtime;
    private static int _frameCheckFrame = -9999;
    private static bool _frameLogged;
    private static bool _rectLogged;
    private static float _copiedUntil;   // ★ 2026-11-07：剪贴板兜底提示的显示截止时间

    // ★ 2026-11-10：输入框矩形按帧节流缓存（每 10 帧或屏幕变化才重算一次）。
    //   原来每个 IMGUI 事件都调 TryGetInputRect → 每秒几百次查找 → 选项框一显示就卡。
    private static bool _rectOkay;
    private static bool _lastRectOkay;   // ★ 2026-11-10：矩形状态变化探针
    private static float _rectLeft, _rectTop, _rectW, _rectH;
    private static int _rectFrame = -9999;
    private static int _rectSw = -1, _rectSh = -1;

    /// <summary>★ 2026-11-12：选项框最后一次绘制时的屏幕矩形（个性面板要贴着它对位：右对齐、上方对齐）</summary>
    internal static Rect OptionsRect;

    // ★ 2026-11-13 主人要：选项框可拖动（参照性格面板；偏移持久化到 config，重启保留）
    private static float _dragOffsetX = float.MinValue, _dragOffsetY = float.MinValue;
    private static Vector2? _dragStartMouse;
    private static float _dragStartOffX, _dragStartOffY;
    private static bool _dragMoved;
    private static int _pressedIndex = -1;
    /// <summary>选项框矩形最后一次更新的时间（判断是否还在对话中，防止按钮残留）</summary>
    internal static float OptionsRectTime = -999f;
    internal static bool OptionsRectFresh() =>
        OptionsRect.width > 4f && UnityEngine.Time.time - OptionsRectTime < 2f;

    // ★ 2026-11-10 探针：家园对话时选项框不显示 —— 先排除「组件被销毁」
    private static OptionsHud _instance;
    void Awake() { _instance = this; }

    /// <summary>
    /// ★★ 2026-11-10 保险（家园是独立场景，切换后 HUD 组件可能被销毁 → 框永远不显示）：
    ///   每次 Show 前检查实例还在不在，没了就原地重建一个（DontDestroyOnLoad）。
    ///   重建成功只打一次日志，失败也只打一次警告，不会刷屏。
    /// </summary>
    private static bool _rebuildLogged;
    private static void EnsureInstance()
    {
        try
        {
            if (_instance != null) return;
            try { IL2CPPChainloader.AddUnityComponent<OptionsHud>(); } catch { }   // 已注册会抛，忽略
            var go = new UnityEngine.GameObject("BuWenFanChen_OptionsHud");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<OptionsHud>());
            if (!_rebuildLogged)
            {
                _rebuildLogged = true;
                Log.LogWarning("[HUD] 检测到组件已不在 → 已自动重建（家园场景切换后常见）");
            }
        }
        catch (Exception e)
        {
            if (!_rebuildLogged)
            {
                _rebuildLogged = true;
                Log.LogWarning($"[HUD] 自动重建失败：{e.GetType().Name}: {e.Message}");
            }
        }
    }

    public static void Show(string[] opts)
    {
        EnsureInstance();   // ★ 先保证有人画
        _opts = opts;
        _rectFrame = -9999;   // ★ 2026-11-10：换选项时强制重算一次输入框矩形
        // ★ 2026-11-10 体检打点：解析出选项、推到 HUD 这一步有没有走到。
        //   有这行但屏幕没框 → 问题在 HUD 绘制/挂载；没这行 → 问题在解析上游。
        try
        {
            string scene = "?";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
            Log.LogInfo($"[HUD] Show：场景={scene} 组件={(_instance != null ? "存活" : "没了")}"
                        + $" 条数={(opts == null ? 0 : opts.Length)} Screen={Screen.width}x{Screen.height}");
        }
        catch { }
        try
        {
            BuWenFanChen.Diagnostics.HealthProbe.Link("HUD.Show",
                (opts == null ? 0 : opts.Length) + " 条");
        }
        catch { }
    }

    public static void Clear()
    {
        // ★ 2026-11-10 体检打点：只在前一刻确实有选项时记录（正常清空不刷日志）
        try
        {
            if (_opts != null && _opts.Length > 0)
                BuWenFanChen.Diagnostics.HealthProbe.Link("HUD.Clear", "清掉 " + _opts.Length + " 条");
        }
        catch { }
        _opts = null;
        // ★ 2026-11-12：退出对话时把选项框矩形也清掉，否则「性格面板」按钮会一直残留
        OptionsRect = default;
        OptionsRectTime = -999f;
    }

    // ★ 2026-11-10：家园退出不清框的兜底检测
    //   ★★ 判据教训（主人指出）：绝不能只看「对话面板对象在不在」——
    //   ① 11-08 就记过：正常对话中 NpcDialoguePanel.activeInHierarchy 也会是 false；
    //   ② 家园用的可能是别的对话 UI，面板对象可能压根不存在 → 只看它会在对话中误清。
    //   现在的判据：**对话面板 和 输入框 两个信号都找不到**，连续 15 秒，才认为「真的不在对话里」。
    //   对话进行中一定有输入框（家园对话也有「请输入文本」输入框），所以这个组合不会在对话中误清。
    private static float _panelDeadSince = -1f;
    private static bool _panelDiagLogged;
    private static void CheckDialogueAliveForClear()
    {
        try
        {
            bool inDialogue = false;

            // ① 对话面板（世界对话一定有；家园可能没有）
            try
            {
                var panelTy = Il2CppInterop.Runtime.Il2CppType.Of<Game.NpcDialoguePanel>();
                var panels = UnityEngine.Resources.FindObjectsOfTypeAll(panelTy);
                if (panels != null && panels.Count > 0) inDialogue = true;
            }
            catch { }

            // ② 输入框（对话中一定有；家园对话也有）——这是更可靠的「正在对话」信号
            if (!inDialogue)
            {
                try
                {
                    var inputTy = Il2CppInterop.Runtime.Il2CppType.Of<Game.CommonInputField>();
                    var inputs = UnityEngine.Resources.FindObjectsOfTypeAll(inputTy);
                    if (inputs != null && inputs.Count > 0) inDialogue = true;
                }
                catch { }
            }

            if (inDialogue) { _panelDeadSince = -1f; return; }   // 还在对话里 → 绝不清

            if (_panelDeadSince < 0f)
            {
                _panelDeadSince = UnityEngine.Time.time;
                if (!_panelDiagLogged)
                {
                    _panelDiagLogged = true;
                    Log.LogWarning("[HUD] 对话面板和输入框都找不到 → 3 秒后若还没关闭事件就清空（家园退出兜底）");
                }
                return;
            }
            // ★ 2026-11-10：15 秒太长（主人反馈退出家园后框要等很久才消失）→ 缩到 3 秒。
            //   安全性：判据是「面板和输入框两个对象都找不到」，真在对话里不会触发。
            if (UnityEngine.Time.time - _panelDeadSince >= 3f)
            {
                _panelDeadSince = -1f;
                Log.LogWarning("[HUD] 面板/输入框消失 3 秒且没收到关闭事件 → 清空选项框（家园退出兜底）");
                Clear();
            }
        }
        catch { }
    }

    /// <summary>缓存的对话面板（用来判断对话是否还开着）</summary>
    private static Game.NpcDialoguePanel _cachedPanel;
    private static float _lastPanelScan;
    private static bool _aliveNotFoundLogged;   // 2026-11-08: log once when panel not found   // ★ 重扫冷却（避免每帧全量查找）

    /// <summary>
    /// ★★ 2026-11-08 修（玩家反馈「退出对话框框还在」）：
    ///   选项框是 IMGUI 画的，**独立于游戏 UI**。玩家关掉/退出对话后，
    ///   游戏面板没了，但我们的框还挂在屏幕上 —— 而且没有任何东西会来清它。
    ///   现在每帧检查对话面板是否还可见：不可见就不画。
    ///
    /// ★ 2026-11-08 再修（玩家反馈「聊一会儿就卡」）：
    ///   原来缓存失效后**每帧**都会 `Resources.FindObjectsOfTypeAll` 全量扫一遍（很贵）。
    ///   现在：缓存有效时只查 activeInHierarchy（便宜）；失效才重扫，且 2 秒冷却。
    /// </summary>
    private static bool DialoguePanelAlive()
    {
        try
        {
            if (_cachedPanel != null)
            {
                var go = _cachedPanel.gameObject;
                if (go != null) return go.activeInHierarchy;   // 缓存还在 → 只查可见性
            }
        }
        catch { }
        _cachedPanel = null;   // 对象真的没了才清

        if (UnityEngine.Time.time - _lastPanelScan < 2f) return true;   // 冷却期内按「开着」处理（乐观）
        _lastPanelScan = UnityEngine.Time.time;

        // 重新按类型全量找（不受激活状态限制）
        try
        {
            var ty = Il2CppInterop.Runtime.Il2CppType.Of<Game.NpcDialoguePanel>();
            var objs = UnityEngine.Resources.FindObjectsOfTypeAll(ty);
            if (objs != null)
            {
                foreach (var o in objs)
                {
                    if (o == null) continue;
                    try
                    {
                        var p = o.TryCast<Game.NpcDialoguePanel>();
                        if (p == null) continue;
                        var go2 = p.gameObject;
                        if (go2 != null)
                        {
                            _cachedPanel = p;                 // 缓存住，下次只查 activeInHierarchy
                            return go2.activeInHierarchy;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // ★★ 2026-11-08 修（玩家反馈「控制台有选项、游戏里没框」）：
        //   **找不到对话面板时，按「开着」处理**——宁可多画一个框，
        //   也不能因为判断失误把正常对话里的框藏起来（那可是核心功能）。
        //   只有「明确找到面板且它未激活」才返回 false（那才是真的退出了对话）。
        if (!_aliveNotFoundLogged)
        {
            _aliveNotFoundLogged = true;
            Log.LogInfo("[HUD] 没找到 NpcDialoguePanel → 按「对话开着」处理（保证框能显示）");
        }
        return true;
    }


    private void OnGUI()
    {
        try
        {
            if (_opts == null || _opts.Length == 0) return;
            if (!PromptConfig.OptionsEnabled) return;
            // ★ 2026-11-12 主人要求：选项框皮肤效果不好，已恢复原样（不加载三段皮肤）
            // ★ 2026-11-08 再改：**不再每帧猜「对话面板是否可见」**——
            //   实测 NpcDialoguePanel 在正常对话中 activeInHierarchy 也可能是 false（误判 → 框不显示），
            //   这个判断不可靠。改成监听游戏自己的关闭事件（见文件末尾 DialogueClosePatch）：
            //   退出对话那一刻直接 Clear，平时一律正常画。

            float scale = Mathf.Max(0.72f, Screen.height / 1080f);
            int fs = Mathf.RoundToInt(15f * scale);          // 字号调小
            EnsureStyle(fs);

            float pad = 7f * scale;
            float gap = 5f * scale;
            float w = 360f * scale;
            float textW = w - pad * 2f - 22f * scale;

            // ① 先算每条选项换行后的高度
            var labels = new string[_opts.Length];
            var heights = new float[_opts.Length];
            float totalH = pad * 2f;

            for (int i = 0; i < _opts.Length; i++)
            {
                labels[i] = $"{(char)('A' + i)}. {_opts[i]}";
                float h = _style.CalcHeight(new GUIContent(labels[i]), textW);
                heights[i] = Mathf.Max(22f * scale, h + 6f * scale);
                totalH += heights[i];
                if (i < _opts.Length - 1) totalH += gap;
            }

            // ② 定位：★ 2026-11-10 恢复 1.3 的定位方式（主人在 #7669/#7681 确认过"位置对了"）：
            //   「右边缘 = 输入框左边 - 间距，底边对齐输入框底边」。不写死任何本地像素坐标，
            //   输入框矩形由 InputFieldBridge.TryGetInputRect 实时读（里面已改成 Canvas 坐标换算，
            //   兼容游戏更新后的坐标语义）。
            //   性能：矩形按 10 帧节流（图 2 那份分析指出过每个 IMGUI 事件都查输入框会卡）。
            float x, bottom;
            if (UnityEngine.Time.frameCount - _rectFrame >= 10
                || _rectSw != Screen.width || _rectSh != Screen.height)
            {
                _rectFrame = UnityEngine.Time.frameCount;
                _rectSw = Screen.width;
                _rectSh = Screen.height;
                _rectOkay = InputFieldBridge.TryGetInputRect(out _rectLeft, out _rectTop, out _rectW, out _rectH);
            }

            // ★ 2026-11-10 探针：矩形状态变化时打一行（含场景名），定位「家园里框不显示」
            if (_lastRectOkay != _rectOkay)
            {
                _lastRectOkay = _rectOkay;
                try
                {
                    string scene = "?";
                    try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
                    Log.LogWarning($"[HUD] 矩形状态变化：ok={_rectOkay} 场景={scene}"
                        + $" rect=({_rectLeft:F0},{_rectTop:F0},{_rectW:F0},{_rectH:F0})"
                        + $" Screen={Screen.width}x{Screen.height} HUDw={w:F0}");
                }
                catch { }
            }

            if (_rectOkay)
            {
                x = _rectLeft - w - 26f * scale;   // 右边缘 = 输入框左边 - 一点间距
                bottom = _rectTop + _rectH;        // 底边对齐输入框底边
                if (!_rectLogged)
                {
                    _rectLogged = true;
                    Log.LogInfo($"[HUD] 输入框矩形 left={_rectLeft:F0} top={_rectTop:F0} w={_rectW:F0} h={_rectH:F0}；"
                                + $"→ HUD x={x:F0} bottom={bottom:F0} w={w:F0} totalH={totalH:F0}；"
                                + $"Screen={Screen.width}x{Screen.height}；scale={scale:F2}");
                }
            }
            else
            {
                // ★ 2026-11-10：家园场景取不到世界输入框矩形 → 用屏幕比例贴「家园对话框左侧」。
                //   实测家园对话框输入框约在 0.37~0.60 屏宽、底边约 0.96 屏高；
                //   框右边缘贴过去留一点缝：x = 0.36*sw - w，底边 0.94*sh。
                x = Screen.width * 0.36f - w;
                if (x < 8f) x = 8f;
                bottom = Screen.height * 0.94f;
                if (!_rectLogged)
                {
                    _rectLogged = true;
                    Log.LogWarning($"[HUD] 取输入框矩形失败 → 用屏幕比例兜底 x={x:F0} bottom={bottom:F0}；Screen={Screen.width}x{Screen.height}");
                }
            }

            // ★ 2026-11-10：家园对话退出不触发 NpcDialoguePanel.OnHide → 选项框会残留在屏幕上
            //   （主人截图：退回仙居楼后框还在）。这里做兜底检测：每 60 帧查一次对话面板，
            //   连续 5 秒找不到「激活的 NpcDialoguePanel」就强制清空。
            //   节流到每秒一次（11-08 踩过每帧全量扫描 → 卡顿的坑）。
            if (_opts != null && UnityEngine.Time.frameCount % 60 == 0)
                CheckDialogueAliveForClear();
            if (x < 8f) x = 8f;
            float y = bottom - totalH;
            if (y < 8f) y = 8f;

            // ★ 2026-11-13 主人要：选项框可拖动（偏移相对自动定位，拖过之后持久化）
            if (_dragOffsetX == float.MinValue)
            {
                _dragOffsetX = PromptConfig.OptionsOffsetX;
                _dragOffsetY = PromptConfig.OptionsOffsetY;
                if (Mathf.Abs(_dragOffsetX) > Screen.width * 2f) _dragOffsetX = 0f;
                if (Mathf.Abs(_dragOffsetY) > Screen.height * 2f) _dragOffsetY = 0f;
            }
            x += _dragOffsetX;
            y += _dragOffsetY;
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - w));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - totalH));

            // 拖动：框内按下 → 拖动移动；松手时没拖动过才算点击（点击在下面按钮循环里判定）
            var ev = Event.current;
            if (ev != null)
            {
                if (ev.type == EventType.MouseDown && ev.button == 0
                    && new Rect(x, y, w, totalH).Contains(ev.mousePosition))
                {
                    _dragStartMouse = ev.mousePosition;
                    _dragStartOffX = _dragOffsetX;
                    _dragStartOffY = _dragOffsetY;
                    _dragMoved = false;
                }
                else if (ev.type == EventType.MouseDrag && _dragStartMouse.HasValue && ev.button == 0)
                {
                    var dx = ev.mousePosition.x - _dragStartMouse.Value.x;
                    var dy = ev.mousePosition.y - _dragStartMouse.Value.y;
                    if (Mathf.Abs(dx) > 4f || Mathf.Abs(dy) > 4f) _dragMoved = true;
                    if (_dragMoved)
                    {
                        _dragOffsetX = _dragStartOffX + dx;
                        _dragOffsetY = _dragStartOffY + dy;
                    }
                }
                else if (ev.type == EventType.MouseUp && _dragStartMouse.HasValue)
                {
                    if (_dragMoved)
                    {
                        _dragOffsetX = Mathf.Clamp(_dragOffsetX, -Screen.width, Screen.width);
                        _dragOffsetY = Mathf.Clamp(_dragOffsetY, -Screen.height, Screen.height);
                        try { PromptConfig.SetOptionsOffset(_dragOffsetX, _dragOffsetY); } catch { }
                        Log.LogInfo($"[HUD] 选项框拖动 → 偏移 ({_dragOffsetX:F0},{_dragOffsetY:F0})（已保存，重启保留）");
                    }
                    _dragStartMouse = null;
                    _dragMoved = false;
                }
            }

            OptionsRect = new Rect(x, y, w, totalH);   // ★ 2026-11-12：给个性面板对位用
            OptionsRectTime = UnityEngine.Time.time;

            // ③ 外框（细绿线 + 半透明黑；★2026-11-12 恢复原样，不再叠皮肤）
            DrawGlowBox(new Rect(x, y, w, totalH),
                        new Color(0.30f, 0.52f, 0.44f), scale, false);

            // ★ 2026-11-07：剪贴板兜底提示（写入失败时显示 4 秒）
            if (UnityEngine.Time.time < _copiedUntil)
            {
                EnsureTipStyle(fs);
                GUI.Label(new Rect(x, y - 26f * scale, w, 22f * scale),
                          "已复制到剪贴板，按 Ctrl+V 粘贴", _tipStyle);
            }

            var mouse = Event.current != null ? Event.current.mousePosition : new Vector2(-9999f, -9999f);

            // ④ 逐条画（高度按换行后累加）
            float cy = y + pad;
            for (int i = 0; i < _opts.Length; i++)
            {
                var r = new Rect(x + pad, cy, w - pad * 2f, heights[i]);
                bool hover = r.Contains(mouse);

                var edge = hover ? new Color(0.38f, 0.62f, 0.53f) : new Color(0.30f, 0.52f, 0.44f);
                DrawGlowBox(r, edge, scale, hover);

                GUI.Label(new Rect(r.x + 10f * scale, r.y + 2f * scale,
                                   r.width - 20f * scale, r.height - 4f * scale),
                          labels[i], _style);

                if (hover && Event.current != null
                    && Event.current.type == EventType.MouseDown
                    && Event.current.button == 0)
                {
                    _pressedIndex = i;   // ★ 2026-11-13：记下按下的是哪条，松手且没拖动才触发
                }

                if (hover && Event.current != null
                    && Event.current.type == EventType.MouseUp
                    && Event.current.button == 0
                    && _pressedIndex == i && !_dragMoved)
                {
                    // ★ 2026-11-07 主人定：点完**不自动消失**（框保持到下一轮回复时更新）。
                    //   这里只写文字 + 记日志，不动 _opts。
                    //   ★ 同时加【不依赖游戏 UI 的保底】：写入失败就把文字塞进系统剪贴板 +
                    //     屏幕上提示「Ctrl+V 粘贴」。这样哪怕某些玩家环境怎么都写不进去，
                    //     也能用——剪贴板是系统级能力，不依赖游戏内部结构。
                    bool ok = false;
                    try { ok = InputFieldBridge.TryWrite(_opts[i]); } catch { }
                    if (ok)
                    {
                        Log.LogInfo($"[HUD] 点击第 {i + 1} 条 → 写入成功（框保持到下一轮回复）");
                    }
                    else
                    {
                        try
                        {
                            UnityEngine.GUIUtility.systemCopyBuffer = _opts[i];
                            _copiedUntil = UnityEngine.Time.time + 4f;
                            Log.LogInfo($"[HUD] 点击第 {i + 1} 条 → 写入失败，已复制到剪贴板（控件旁会提示 Ctrl+V）");
                        }
                        catch (Exception e)
                        {
                            Log.LogWarning($"[HUD] 点击第 {i + 1} 条 → 写入失败，复制剪贴板也失败：{e.GetType().Name}");
                        }
                    }
                    Event.current.Use();
                }

                cy += heights[i] + gap;
            }

            if (ev != null && ev.type == EventType.MouseUp) _pressedIndex = -1;
        }
        catch { }
    }

    /// <summary>剪贴板兜底提示的样式（小一号、偏金色）。</summary>
    private static void EnsureTipStyle(int fs)
    {
        if (_tipStyle == null)
        {
            _tipStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = Mathf.Max(12, fs - 2),
            };
        }
        if (_font != null) _tipStyle.font = _font;
        _tipStyle.normal.textColor = new Color(0.95f, 0.82f, 0.45f, 0.95f);
    }

    private static void EnsureStyle(int fs)
    {        if (_font == null || _styleFontSize != fs)
        {
            _font = TryLoadFont(fs);
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    wordWrap = true,
                    alignment = TextAnchor.UpperLeft,
                };
            }
            _style.normal.textColor = new Color(0.95f, 0.95f, 0.95f, 0.92f);
            if (_font != null) _style.font = _font;
            _style.fontSize = fs;
            _styleFontSize = fs;

            if (!_fontLogged)
            {
                _fontLogged = true;
                Log.LogInfo(_font != null ? $"[HUD] 字体 = {_font.name}" : "[HUD] 字体仍未拿到，用默认");
            }
        }
    }

    /// <summary>
    /// 字体优先级：
    ///   ① 游戏已加载的 Font 资源（最像正文）
    ///   ② 系统字体（数组重载，让 Unity 自己挑）
    ///   ③ 单个系统字体名
    /// </summary>
    private static Font TryLoadFont(int fs)
    {
        // ① 场景里游戏自己加载过的字体（FindObjectsOfTypeAll 会包含未激活资源）
        try
        {
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            if (fonts != null && fonts.Length > 0)
            {
                var sb = new StringBuilder();
                int n = 0;
                foreach (var f in fonts)
                {
                    if (f == null) continue;
                    if (n++ < 10) sb.Append(f.name).Append(',');
                }
                Log.LogInfo($"[HUD] 游戏字体资源 {fonts.Length} 个：{sb}");

                // ★ 不能直接取第一个 —— 那通常是 LegacyRuntime（Unity 默认 Arial），中文不对味。
                //   按「最像古风正文字体」的优先级挑：方正 → 华文楷体 → 汉仪 → 微软雅黑
                string[] prefer = { "FZ", "STKAI", "HWMZT", "msyh", "MSYH", "SimSun", "宋体" };
                foreach (var key in prefer)
                {
                    foreach (var f in fonts)
                    {
                        if (f == null || string.IsNullOrEmpty(f.name)) continue;
                        if (f.name.IndexOf(key, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Log.LogInfo($"[HUD] 字体选中：{f.name}（匹配 {key}）");
                        return f;
                    }
                }

                // 兜底：跳过 LegacyRuntime / Arial
                foreach (var f in fonts)
                {
                    if (f == null || string.IsNullOrEmpty(f.name)) continue;
                    if (f.name == "LegacyRuntime") continue;
                    if (f.name.StartsWith("Arial", System.StringComparison.OrdinalIgnoreCase)) continue;
                    Log.LogInfo($"[HUD] 字体选中（兜底）：{f.name}");
                    return f;
                }
            }
        }
        catch (System.Exception e) { Log.LogWarning($"[HUD] 读游戏字体失败：{e.GetType().Name}"); }

        // ② 数组重载
        try
        {
            var f = Font.CreateDynamicFontFromOSFont(
                new[] { "SimSun", "宋体", "NSimSun", "KaiTi", "Microsoft YaHei", "Arial" }, fs);
            if (f != null) { Log.LogInfo("[HUD] 字体命中：系统字体数组重载"); return f; }
        }
        catch { }

        // ③ 单个名字
        foreach (var n in new[] { "SimSun", "宋体", "Microsoft YaHei" })
        {
            try
            {
                var f = Font.CreateDynamicFontFromOSFont(n, fs);
                if (f != null) { Log.LogInfo($"[HUD] 字体命中：{n}"); return f; }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// 画框（2026-11-04 19:5x 第六版）：**不做渐变**。
    ///   一条绿线 + 一整块半透明黑。层数叠加会产生"绿-黑-绿"的分层，干脆去掉。
    /// </summary>
    /// <summary>
    /// ★ 2026-11-12：选项框外框皮肤 —— 读 config\panel\options_frame_{top,mid,bottom}.png
    /// （三段式：顶/底角饰固定高度、中段纵向拉伸，所以框拉高不变形）。
    /// </summary>
    private static void EnsureFrameTex()
    {
        try
        {
            if (UnityEngine.Time.frameCount - _frameCheckFrame < 30) return;
            _frameCheckFrame = UnityEngine.Time.frameCount;

            var dir = System.IO.Path.Combine(BuWenFanChen.PromptConfig.Dir, "panel");
            var pTop = System.IO.Path.Combine(dir, "options_frame_top.png");
            var pMid = System.IO.Path.Combine(dir, "options_frame_mid.png");
            var pBot = System.IO.Path.Combine(dir, "options_frame_bottom.png");
            if (!System.IO.File.Exists(pTop) || !System.IO.File.Exists(pMid) || !System.IO.File.Exists(pBot))
            {
                if (_frameTop != null) { _frameTop = null; Log.LogInfo("[HUD] 选项框皮肤已移除 → 用默认样式"); }
                return;
            }

            long mt = Math.Max(
                System.IO.File.GetLastWriteTimeUtc(pTop).Ticks,
                Math.Max(System.IO.File.GetLastWriteTimeUtc(pMid).Ticks,
                         System.IO.File.GetLastWriteTimeUtc(pBot).Ticks));
            var mtDt = new DateTime(mt, DateTimeKind.Utc);
            if (_frameTop != null && mtDt == _frameMtime) return;

            Texture2D Load1(string p)
            {
                var bytes = System.IO.File.ReadAllBytes(p);
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                return UnityEngine.ImageConversion.LoadImage(t, bytes) ? t : null;
            }

            var t1 = Load1(pTop);
            var t2 = Load1(pMid);
            var t3 = Load1(pBot);
            if (t1 != null && t2 != null && t3 != null)
            {
                _frameTop = t1; _frameMid = t2; _frameBottom = t3;
                _frameMtime = mtDt;
                if (!_frameLogged)
                {
                    _frameLogged = true;
                    Log.LogInfo($"[HUD] 选项框皮肤已加载（三段 {t1.width}×{t1.height} / {t2.height} / {t3.height}）");
                }
            }
        }
        catch (Exception e)
        {
            if (!_frameLogged)
            {
                _frameLogged = true;
                Log.LogWarning($"[HUD] 选项框皮肤加载失败：{e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>先画默认深底+绿线（保证字可读），再把三段皮肤叠上去（皮肤是透明底绿线云纹）</summary>
    private static void DrawSkinFrame(Rect r, float scale)
    {
        DrawGlowBox(r, new Color(0.30f, 0.52f, 0.44f), scale, false);
        if (_frameTop == null || _frameMid == null || _frameBottom == null) return;

        float ratio = r.width / (float)_frameTop.width;
        float th = _frameTop.height * ratio;
        float bh = _frameBottom.height * ratio;
        if (r.height < th + bh + 2f) return;   // 框太矮（选项少）→ 不套皮肤，免得挤变形

        var old = GUI.color;
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, th), _frameTop);
        GUI.DrawTexture(new Rect(r.x, r.y + th, r.width, r.height - th - bh), _frameMid);
        GUI.DrawTexture(new Rect(r.x, r.yMax - bh, r.width, bh), _frameBottom);
        GUI.color = old;
    }

    private static void DrawGlowBox(Rect r, Color edge, float scale, bool hover = false)
    {
        var old = GUI.color;

        var line = hover
            ? new Color(0.42f, 0.68f, 0.57f, 0.95f)
            : new Color(0.30f, 0.52f, 0.44f, 0.85f);
        float bw = 1.5f * scale;

        // 一整块半透明黑
        GUI.color = hover
            ? new Color(0.02f, 0.03f, 0.03f, 0.68f)
            : new Color(0.02f, 0.03f, 0.03f, 0.58f);
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, r.height), Texture2D.whiteTexture);

        // 四边绿线
        GUI.color = line;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, bw), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - bw, r.width, bw), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y, bw, r.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - bw, r.y, bw, r.height), Texture2D.whiteTexture);

        GUI.color = old;
    }
}

/// <summary>
/// ★ 2026-11-08：监听「关闭对话」这个游戏事件，退出对话时把选项框清掉。
///   比「每帧猜面板可见性」可靠 —— 那是基于 activeInHierarchy 的猜测，
///   实测在正常对话中也会被判成 false，导致选项框该显示的时候不显示。
/// </summary>
internal static class DialogueCloseCommon
{
    internal static readonly BepInEx.Logging.ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("不问凡尘·HUD");

    private static bool _loggedHide;

    internal static void OnDialogClosed()
    {
        try
        {
            OptionsHud.Clear();
            if (!_loggedHide)
            {
                _loggedHide = true;
                Log.LogInfo("[HUD] 检测到对话关闭事件 → 已清空选项框（此日志只打一次）");
            }
        }
        catch { }
    }
}

/// <summary>
/// ★ 2026-11-08：对话关闭 → 清空选项框。
///   `OnHide()` 是语音 MOD 已验证能 hook 的对话生命周期方法（比协程 PlayCloseAnimThenHide 可靠）；
///   另一个协程方法也挂上，双保险。
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(Game.NpcDialoguePanel), "OnHide")]
internal static class DialogueClosePatchOnHide
{
    private static void Prefix() => DialogueCloseCommon.OnDialogClosed();
}

[HarmonyLib.HarmonyPatch(typeof(Game.NpcDialoguePanel), "PlayCloseAnimThenHide")]
internal static class DialogueClosePatchAnim
{
    private static void Prefix() => DialogueCloseCommon.OnDialogClosed();
}

/// <summary>
/// ★★ 2026-11-10：家园「返回庭院」/ 空间返回 → 清空选项框。
///
/// 为什么需要：家园是独立场景，退出对话既不触发 NpcDialoguePanel.OnHide，
/// 也不会销毁对话面板对象（它常驻）→ 之前的「3 秒兜底」也失效，框会一直残留。
/// 这招 hook `Game.SpaceManager.BackToParentSpace`（返回父空间），
/// 玩家点「返回庭院」/ 离开家园空间时立刻清空。
/// </summary>
[HarmonyLib.HarmonyPatch]
internal static class SpaceBackClearPatch
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        try
        {
            foreach (var m in HarmonyLib.AccessTools.GetDeclaredMethods(typeof(Game.SpaceManager)))
                if (m != null && m.Name == "BackToParentSpace") return m;
        }
        catch { }
        return null;
    }

    [HarmonyLib.HarmonyPostfix]
    internal static void Postfix()
    {
        try
        {
            DialogueCloseCommon.Log.LogInfo("[HUD] 返回父空间（家园退出）→ 清空选项框");
            DialogueCloseCommon.OnDialogClosed();
        }
        catch { }
    }
}

/// <summary>
/// ★★ 2026-11-10：家园对话面板 `NpcBedroomOverlay` 关闭 → 清空选项框。
///
/// 探针拿到的真实结构：家园（卧室）对话 UI = Game.UI.UPFLogic.NpcBedroom.NpcBedroomOverlay，
/// 它的方法列表里有 OnHide() / OnDisable() —— 家园退出对话走的是**它自己的** OnHide，
/// 不是 NpcDialoguePanel.OnHide（所以之前的 hook 全打偏，框一直残留）。
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(Game.UI.UPFLogic.NpcBedroom.NpcBedroomOverlay), "OnHide")]
internal static class BedroomOverlayHideClearPatch
{
    private static void Prefix()
    {
        try
        {
            DialogueCloseCommon.Log.LogInfo("[HUD] 家园卧室面板 OnHide → 清空选项框");
            DialogueCloseCommon.OnDialogClosed();
        }
        catch { }
    }
}

/// <summary>★ 2026-11-10：家园卧室面板禁用（切场景/关界面）时也清一次，双保险。</summary>
[HarmonyLib.HarmonyPatch(typeof(Game.UI.UPFLogic.NpcBedroom.NpcBedroomOverlay), "OnDisable")]
internal static class BedroomOverlayDisableClearPatch
{
    private static void Prefix()
    {
        try { DialogueCloseCommon.OnDialogClosed(); } catch { }
    }
}

/// <summary>
/// ★★ 2026-11-10：任何「对话类面板」销毁 → 清空选项框。
/// 游戏统一 UI 管理：UIManager.OnUIDestroy(PanelId, BaseUI) 在面板销毁时调用。
/// 世界 / 家园两种对话框都走它，所以这是最通用的一道保险。
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(Game.UIManager), "OnUIDestroy")]
internal static class UIDestroyClearPatch
{
    [HarmonyLib.HarmonyPostfix]
    internal static void Postfix(object __1)
    {
        try
        {
            var ui = __1;
            if (ui == null) return;
            string tn = "";
            try { tn = ui.GetType().Name ?? ""; } catch { }
            // ★ 2026-11-10：家园对话用的是 NpcBedroomOverlay（卧室覆盖层），
            //   名字里既没 Dialogue 也没 Chat —— 之前判断漏了它，所以家园退出框不消。
            if (tn.IndexOf("Dialogue", StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("Chat", StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("Bedroom", StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                DialogueCloseCommon.Log.LogInfo($"[HUD] 对话类面板销毁（{tn}）→ 清空选项框");
                DialogueCloseCommon.OnDialogClosed();
            }
        }
        catch { }
    }
}
