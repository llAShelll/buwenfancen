namespace BuWenFanChen;

using System;
using BepInEx.Logging;

/// <summary>
/// ★ 2026-11-04：聊天输入框桥。
///
/// 探针已确认：
///   路径 UIRoot(Clone)/MainLayer/NpcDialoguePanel(Clone)/DialogueInputPanel/DialogueInputPanelView/Prefab#inputField
///   组件 CommonInputField（游戏自己的类）—— 它【没有】text 属性！
///   但它有 InputFieldMain（可写属性），指向真正的 Unity 输入框组件。
///
/// 所以写入路径是：CommonInputField.InputFieldMain.text = 文字
/// 第一次调用会打印 InputFieldMain 的真实类型，方便确认。
/// </summary>
internal static class InputFieldBridge
{
    private const string InputPath =
        "UIRoot(Clone)/MainLayer/NpcDialoguePanel(Clone)/DialogueInputPanel/DialogueInputPanelView/Prefab#inputField";

    private static readonly ManualLogSource Log = Logger.CreateLogSource("不问凡尘·输入框");
    private static bool _typeLogged;
    private static bool _rectLogged;
    private static bool _canvasLogged;     // ★ 2026-11-10：Canvas 换算诊断只打一次
    private static bool _fallbackLogged;   // ★ 2026-11-10：比例兜底警告只打一次（曾每帧刷出 8.5MB 日志）
    private static bool _noRectLogged;     // ★ 2026-11-10：拿不到 RectTransform 警告只打一次

    /// <summary>聊天输入框可见区域（Image#inputBase）的屏幕矩形。Overlay Canvas 下世界坐标即屏幕坐标。</summary>
    private const string BasePath =
        "UIRoot(Clone)/MainLayer/NpcDialoguePanel(Clone)/DialogueInputPanel/DialogueInputPanelView/Image#inputBase";

    /// <summary>
    /// 取输入框的屏幕矩形（左上角坐标 + 宽高，y 轴从屏幕顶部算）。
    /// 用于让 HUD 与输入框底边/左右对齐。失败返回 false。
    ///
    /// ★ 2026-11-04：第一版直接 GameObject.Find + transform as RectTransform 会失败，
    ///   改成从 TMP_InputField 自己的 m_RectTransform 拿（最可靠，不依赖对象激活状态）。
    /// </summary>
    internal static bool TryGetInputRect(out float left, out float top, out float width, out float height)
    {
        left = top = width = height = 0f;
        try
        {
            UnityEngine.RectTransform rt = null;

            // ★ 优先级 ①：从 CommonInputField 的同级/父级找【可见底图 Image#inputBase】
            //   实测 TMP_InputField.m_RectTransform 只有 570 宽（那是文字区），
            //   真正输入框可见区域约 1726 宽 —— 必须用底图那个 RectTransform。
            try
            {
                var ifc = FindInputComponent();
                var comp = ifc as UnityEngine.Component;
                if (comp != null)
                {
                    var parent = comp.transform.parent;   // DialogueInputPanelView
                    if (parent != null)
                    {
                        var baseT = parent.Find("Image#inputBase");
                        if (baseT != null) rt = baseT.GetComponent<UnityEngine.RectTransform>();
                    }
                }
            }
            catch { }

            // ② 退回：从 CommonInputField.InputFieldMain（TMP_InputField）的 m_RectTransform 拿
            if (rt == null)
            {
                try
                {
                    var ifc = FindInputComponent();
                    if (ifc != null)
                    {
                        var main = ifc.GetType().GetProperty("InputFieldMain")?.GetValue(ifc);
                        if (main != null)
                        {
                            var mt = main.GetType();
                            rt = mt.GetProperty("m_RectTransform")?.GetValue(main) as UnityEngine.RectTransform;
                            if (rt == null)
                            {
                                var tc = mt.GetProperty("textComponent")?.GetValue(main);
                                if (tc != null)
                                    rt = tc.GetType().GetProperty("rectTransform")?.GetValue(tc) as UnityEngine.RectTransform;
                            }
                        }
                    }
                }
                catch { }
            }

            // ③ 退回：GameObject.Find + GetComponent<RectTransform>
            if (rt == null)
            {
                var go = UnityEngine.GameObject.Find(BasePath) ?? UnityEngine.GameObject.Find(InputPath);
                if (go != null)
                {
                    rt = go.GetComponent<UnityEngine.RectTransform>();
                    _cachedInputGo = go;   // ★ 缓存 GameObject 引用（隐藏后仍可用）
                }
            }

            if (rt == null)
            {
                // ★ 2026-11-10：这条警告原来每次调用都打 —— 家园场景下输入框找不到，
                //   OnGUI 每帧调用 → 日志每秒刷几十条（又踩一次"每帧刷日志"的坑）。现在只打一次。
                if (!_noRectLogged)
                {
                    _noRectLogged = true;
                    Log.LogWarning("[输入框] 拿不到 RectTransform（m_RectTransform / Find 都没成功）—— 此警告只打一次");
                }
                return false;
            }

            var pos = rt.position;
            var size = rt.rect.size;
            var pivot = rt.pivot;
            var sw = UnityEngine.Screen.width;
            var sh = UnityEngine.Screen.height;

            // ★ 2026-11-10 修（游戏更新后 UI 坐标不再是「世界==屏幕」）：
            //   实测输入框 RectTransform.position=(-679.3,-940.9)，直接当屏幕坐标会算出屏幕外。
            //   正确做法：按它所在 Canvas 的模式，用 RectTransformUtility.WorldToScreenPoint 换算。
            //   换算结果不合理就退回旧算法，再不合理就用屏幕比例兜底（保证玩家看得见）。
            bool resolved = false;
            try
            {
                var canvas = _cachedCanvas;
                if (canvas == null)
                {
                    canvas = rt.GetComponentInParent<UnityEngine.Canvas>();
                    _cachedCanvas = canvas;   // ★ 缓存，避免每帧 GetComponentInParent
                }
                if (canvas != null)
                {
                    var mode = canvas.renderMode;
                    var cam = mode == UnityEngine.RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    var sp = UnityEngine.RectTransformUtility.WorldToScreenPoint(cam, pos);
                    var sf = canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
                    float w2 = size.x * sf, h2 = size.y * sf;
                    float l2 = sp.x - w2 * pivot.x;
                    float b2 = sp.y - h2 * pivot.y;
                    float t2 = sh - (b2 + h2);
                    // ★ 2026-11-10 修：sane 判据不能卡太死 —— 输入框本来就在屏幕底部，
                    //   top=1968（Screen 2160 的 91%）是正常的，之前用 sh*0.9 当上限把它误判成"不合理"，
                    //   结果 Canvas 换算白算了、又退回旧算法、框贴回屏幕左边。
                    //   现在只排除「明显跑到屏幕外」的情况（上限放到 1.1 倍）。
                    bool sane = l2 >= -sw * 0.2f && l2 <= sw * 1.1f
                                && t2 >= -sh * 0.2f && t2 <= sh * 1.1f
                                && w2 >= 40f && h2 >= 8f && w2 <= sw * 0.95f;
                    if (!_canvasLogged)
                    {
                        _canvasLogged = true;
                        Log.LogInfo($"[输入框] Canvas 换算诊断：mode={mode} scale={sf:F3} cam={(cam != null ? "有" : "无")}"
                            + $" pos=({pos.x:F1},{pos.y:F1}) size=({size.x:F1},{size.y:F1}) → left={l2:F1} top={t2:F1} w={w2:F1} h={h2:F1}"
                            + $" | sane={sane} Screen={sw}x{sh}");
                    }
                    if (sane)
                    {
                        left = l2; top = t2; width = w2; height = h2;
                        resolved = true;
                    }
                }
            }
            catch (Exception ce)
            {
                if (!_canvasLogged) { _canvasLogged = true; Log.LogWarning($"[输入框] Canvas 换算失败：{ce.GetType().Name}: {ce.Message}"); }
            }

            if (!resolved)
            {
                // 旧算法（Overlay 假设：世界坐标就是屏幕坐标）
                left = pos.x - size.x * pivot.x;
                var bottomW = pos.y - size.y * pivot.y;
                top = sh - (bottomW + size.y);
                width = size.x;
                height = size.y;

                // 旧算法也离谱 → 屏幕比例兜底（★ 警告只打一次，别刷日志）
                if (left < -sw * 0.2f || left > sw * 0.9f || top < -sh * 0.2f || top > sh * 0.9f
                    || width < 40f || height < 8f || width > sw * 0.95f)
                {
                    left = sw * 0.06f;
                    width = sw * 0.30f;
                    height = sh * 0.055f;
                    top = sh * 0.55f;
                    if (!_fallbackLogged)
                    {
                        _fallbackLogged = true;
                        Log.LogWarning($"[输入框] 矩形不合理且 Canvas 换算不可用（position=({pos.x:F1},{pos.y:F1})"
                            + $" size=({size.x:F1},{size.y:F1})）→ 用屏幕比例兜底（此警告只打一次）"
                            + $" left={left:F0} top={top:F0} w={width:F0} h={height:F0} Screen={sw}x{sh}");
                    }
                }
            }

            if (!_rectLogged)
            {
                _rectLogged = true;
                Log.LogInfo($"[输入框] 矩形原始：position=({pos.x:F1},{pos.y:F1}) size=({size.x:F1},{size.y:F1}) "
                            + $"pivot=({pivot.x:F2},{pivot.y:F2}) → left={left:F1} top={top:F1} w={width:F1} h={height:F1} "
                            + $"Screen={UnityEngine.Screen.width}x{UnityEngine.Screen.height}");
            }

            // ★ 2026-11-07：把面板根节点缓存下来（此时还在激活状态，能拿到）
            //   玩家点选项那一刻输入框可能失焦/隐藏，GameObject.Find 就找不到了。
            try { if (rt.parent != null) _panelRoot = rt.parent; } catch { }

            // ★★ 2026-11-07 关键修法：直接从 rect 所在对象反查 TMP_InputField 组件并缓存。
            //   绕开 CommonInputField（它在部分玩家环境里扫不到 / 失焦后取不到），
            //   拿到的就是真正持有 text 属性的组件。
            try { CacheMainFrom(rt); } catch { }

            return width > 1f && height > 1f;
        }
        catch (Exception e)
        {
            Log.LogWarning($"[输入框] 取矩形失败：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// ★ 2026-11-07 修（玩家反馈「点四选一文字没进输入框」）：
    ///   真因是 `GameObject.Find` 只找**激活**对象。取显示框坐标时输入框还在，
    ///   玩家点选项那一刻输入框失焦/面板重建 → Find 返回 null → 直接放弃写入。
    ///   现在改为：① 优先用缓存引用；② 失效再从 inputBase 父节点递归扫；③ 都不行才放弃。
    /// </summary>
    private static object _cachedIfc;

    /// <summary>
    /// ★ 2026-11-07：缓存输入面板根节点（DialogueInputPanelView）。
    ///   玩家点 HUD 那一刻输入框常失焦/隐藏，`GameObject.Find` 只找激活对象会扑空；
    ///   但 Transform 引用还在，能枚举未激活子对象 → 从这里扫 CommonInputField。
    /// </summary>
    private static UnityEngine.Transform _panelRoot;

    /// <summary>
    /// ★ 2026-11-07：缓存真正的 TMP 输入组件（持有 text 属性）。
    ///   从「取矩形时那个 RectTransform」反查，不依赖 CommonInputField 是否扫得到。
    /// </summary>
    private static object _cachedMain;

    /// <summary>取矩形时命中的那个 GameObject / RectTransform（缓存引用，隐藏后仍可访问）</summary>
    private static UnityEngine.GameObject _cachedInputGo;
    private static UnityEngine.RectTransform _cachedRt;

    /// <summary>★ 2026-11-10：缓存输入框所在 Canvas（每帧 GetComponentInParent 太贵，拿到就复用）。
    ///   Unity 的 == null 会识别已销毁对象，场景切换后自动重新拿。</summary>
    private static UnityEngine.Canvas _cachedCanvas;

    /// <summary>
    /// ★ 2026-11-08 重度修（玩家反馈「热修复后无限诊断，聊一会儿就卡，10 分钟 44 MB 日志」）：
    ///   本方法原来每帧被 TryGetInputRect 调用（HUD 每帧取矩形），里面还遍历多个对象
    ///   并把每个组件名写进日志 —— 等于每帧刷几十行。
    ///
    ///   现在：**只缓存 RectTransform**（极便宜），找组件交给 FindByTypeAll（按类型全量，一次即可）。
    ///   另外实测发现 `GetComponents<Component>()` 在 IL2CPP 下返回的类型名全是 "Component"，
    ///   拿不到真实类型 —— 这也是「遍历组件找 InputField」永远匹配不到的原因，那条路彻底弃用。
    /// </summary>
    private static void CacheMainFrom(UnityEngine.RectTransform rt)
    {
        try { if (rt != null) _cachedRt = rt; } catch { }
    }

    /// <summary>找到 CommonInputField 组件（按类型名匹配，不依赖程序集引用）。</summary>
    private static object FindInputComponent()
    {
        // ① 缓存（Unity 的 == null 会识别已销毁对象）
        try
        {
            if (_cachedIfc is UnityEngine.Component cached && cached != null) return _cachedIfc;
        }
        catch { }
        _cachedIfc = null;

        // ② 原路径（保持兼容）
        try
        {
            var go = UnityEngine.GameObject.Find(InputPath);
            if (go != null)
            {
                foreach (var c in go.GetComponents<UnityEngine.Component>())
                {
                    if (c == null) continue;
                    if (c.GetType().Name == "CommonInputField") { _cachedIfc = c; return c; }
                }
            }
        }
        catch { }

        // ③ 用【缓存的父节点】扫（关键：能访问已失焦/未激活的对象）
        //   ★ 2026-11-07：玩家点 HUD 那一刻输入面板常被隐藏，GameObject.Find 全局失效，
        //   但缓存下来的 Transform 引用仍能枚举未激活子对象。
        try
        {
            if (_panelRoot != null)
            {
                var hit = ScanForInput(_panelRoot, 0);
                if (hit != null)
                {
                    _cachedIfc = hit;
                    Log.LogInfo("[输入框] 用缓存面板节点扫描找到 CommonInputField（输入框已失焦/隐藏）");
                    return hit;
                }
            }
        }
        catch { }

        // ④ 递归扫 inputBase 的父节点（不依赖对象激活状态，也不依赖子对象名字）
        try
        {
            var baseGo = UnityEngine.GameObject.Find(BasePath);
            if (baseGo != null)
            {
                var parent = baseGo.transform.parent;
                if (parent != null) _panelRoot = parent;
                var hit = ScanForInput(parent, 0);
                if (hit == null) hit = ScanForInput(baseGo.transform, 0);
                if (hit != null)
                {
                    _cachedIfc = hit;
                    Log.LogInfo("[输入框] 缓存未命中，已递归扫描找到 CommonInputField（输入框可能刚失焦）");
                    return hit;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>递归向下找类型名为 CommonInputField 的组件。</summary>
    private static object ScanForInput(UnityEngine.Transform root, int depth)
    {
        if (root == null || depth > 10) return null;
        try
        {
            foreach (var c in root.GetComponents<UnityEngine.Component>())
            {
                if (c == null) continue;
                if (c.GetType().Name == "CommonInputField") return c;
            }
            int n = root.childCount;
            for (int i = 0; i < n; i++)
            {
                var r = ScanForInput(root.GetChild(i), depth + 1);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }

    /// <summary>写完后尽量让 UI 刷新（TMP 有 ForceLabelUpdate 就调；没有就算了）。</summary>
    private static void TryForceRefresh(object comp, System.Type ty)
    {
        try
        {
            var m = ty.GetMethod("ForceLabelUpdate");
            if (m != null) { m.Invoke(comp, null); Log.LogInfo("[输入框] 已调 ForceLabelUpdate 刷新显示"); }
        }
        catch { }
    }

    /// <summary>在类型（含父类型）上按名字找方法，大小写不敏感。</summary>
    private static System.Reflection.MethodInfo FindMethod(Type ty, string name)
    {
        const System.Reflection.BindingFlags F =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        try
        {
            for (var t = ty; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(F))
                {
                    if (m.Name == name) return m;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// ★★ 2026-11-08 根治方案：按【类型】全量查找 CommonInputField。
    ///   `Resources.FindObjectsOfTypeAll` 不受「激活状态」限制，
    ///   而 `GameObject.Find` 只找激活对象 —— 这就是部分玩家「输入框失焦后彻底找不到」的根因。
    /// </summary>
    private static Game.CommonInputField FindByTypeAll()
    {
        try
        {
            var ty = Il2CppInterop.Runtime.Il2CppType.Of<Game.CommonInputField>();
            var objs = UnityEngine.Resources.FindObjectsOfTypeAll(ty);
            if (objs == null) return null;
            int n = 0;
            foreach (var o in objs)
            {
                if (o == null) continue;
                n++;
                try
                {
                    var c = o.TryCast<Game.CommonInputField>();
                    if (c != null)
                    {
                        Log.LogInfo($"[输入框] 按类型全量找到 CommonInputField（扫描 {n} 个对象）");
                        return c;
                    }
                }
                catch { }
            }
            Log.LogInfo($"[输入框] 按类型全量扫描 {n} 个对象，没有 CommonInputField");
        }
        catch (Exception e)
        {
            Log.LogWarning($"[输入框] FindObjectsOfTypeAll 失败：{e.GetType().Name}: {e.Message}");
        }
        return null;
    }

    /// <summary>把文字写进聊天输入框。成功返回 true。</summary>
    internal static bool TryWrite(string text)
    {
        try
        {
            // ★★ 2026-11-08 主路径（根治）：按类型找到游戏自己的组件，调它自己的 SetInputText。
            //   不依赖层级路径、不依赖对象激活状态、不碰 TMP 内部属性。
            var comp = FindByTypeAll();
            if (comp != null)
            {
                var cty = comp.GetType();

                // ① 先试反射 SetInputText（interop 类常常没导出，失败也正常）
                try
                {
                    var m = FindMethod(cty, "SetInputText")
                         ?? FindMethod(cty, "SetInputTextWithoutActivate");
                    if (m != null)
                    {
                        m.Invoke(comp, new object[] { text });
                        Log.LogInfo($"[输入框] 用 {m.Name}（反射调用）写入成功：{text}");
                        return true;
                    }
                }
                catch (Exception e2)
                {
                    Log.LogWarning($"[输入框] SetInputText 调用失败：{e2.GetType().Name}，改用 InputFieldMain");
                }

                // ② ★ 关键：组件已经拿到 → 直接用它的 InputFieldMain（TMP_InputField）.text
                //    这条路以前走不通，是因为**找不到 CommonInputField**；现在按类型找到了就能走。
                try
                {
                    var mainProp2 = cty.GetProperty("InputFieldMain");
                    if (mainProp2 == null)
                    {
                        foreach (var p2 in cty.GetProperties())
                        {
                            if ((p2.Name ?? "").IndexOf("InputField", StringComparison.OrdinalIgnoreCase) >= 0
                                && p2.PropertyType != typeof(string))
                            { mainProp2 = p2; break; }
                        }
                    }
                    var main2 = mainProp2?.GetValue(comp);
                    if (main2 == null)
                    {
                        Log.LogWarning($"[输入框] 找到组件（{cty.Name}）但 InputFieldMain 为空");
                    }
                    else
                    {
                        var mt2 = main2.GetType();
                        var tp2 = mt2.GetProperty("text");
                        if (tp2 == null)
                        {
                            Log.LogWarning($"[输入框] {mt2.Name} 上没有 text 属性");
                        }
                        else
                        {
                            tp2.SetValue(main2, text);
                            var afterMain = tp2.GetValue(main2) as string;
                            var okMain = afterMain == text;
                            Log.LogInfo($"[输入框] 组件({cty.Name}) → {mt2.Name}.text 写入：读回=\"{afterMain}\" → {(okMain ? "✓ 成功" : "✗ 读回不一致")}");
                            if (okMain)
                            {
                                TryForceRefresh(main2, mt2);
                                return true;
                            }
                        }
                    }
                }
                catch (Exception e3)
                {
                    Log.LogWarning($"[输入框] InputFieldMain 写入异常：{e3.GetType().Name}: {e3.Message}");
                }
            }

            // ★★ 2026-11-07 主路径：用取矩形时缓存下来的 TMP 组件直接写。
            //   不依赖 CommonInputField（部分玩家环境里它扫不到 / 输入框失焦后取不到）。
            if (_cachedMain != null)
            {
                try
                {
                    var mty = _cachedMain.GetType();
                    var mtp = mty.GetProperty("text");
                    if (mtp != null)
                    {
                        mtp.SetValue(_cachedMain, text);
                        var afterCached = mtp.GetValue(_cachedMain) as string;
                        var okCached = afterCached == text;
                        Log.LogInfo($"[输入框] 用缓存组件（{mty.Name}）写入：读回=\"{afterCached}\" → {(okCached ? "✓ 成功" : "✗ 读回不一致")}");
                        if (okCached)
                        {
                            TryForceRefresh(_cachedMain, mty);
                            return true;
                        }
                    }
                }
                catch (Exception e1)
                {
                    Log.LogWarning($"[输入框] 缓存组件写入异常：{e1.GetType().Name}: {e1.Message}");
                }
            }

            var ifc = FindInputComponent();
            if (ifc == null) { Log.LogWarning("[输入框] 没找到 CommonInputField"); return false; }

            var ty = ifc.GetType();
            var mainProp = ty.GetProperty("InputFieldMain");
            if (mainProp == null) { Log.LogWarning("[输入框] CommonInputField 上没有 InputFieldMain"); return false; }

            var main = mainProp.GetValue(ifc);
            if (main == null) { Log.LogWarning("[输入框] InputFieldMain 为空（面板未激活？）"); return false; }

            var mt = main.GetType();
            if (!_typeLogged)
            {
                _typeLogged = true;
                Log.LogInfo($"[输入框] InputFieldMain 的类型 = {mt.FullName}");
                var props = mt.GetProperties();
                var names = new System.Text.StringBuilder();
                int n = 0;
                foreach (var p in props) { if (n++ > 20) { names.Append("…"); break; } names.Append(p.Name).Append(' '); }
                Log.LogInfo($"[输入框] 它的属性：{names}");
            }

            var tp = mt.GetProperty("text");
            if (tp == null) { Log.LogWarning($"[输入框] {mt.Name} 上没有 text 属性，无法写入"); return false; }

            var before = tp.GetValue(main) as string;
            tp.SetValue(main, text);
            var after = tp.GetValue(main) as string;

            var ok = after == text;
            Log.LogInfo($"[输入框] 写入「{text}」：原值=\"{before}\" 读回=\"{after}\" → {(ok ? "✓ 成功" : "✗ 读回不一致")}");

            // 写完后顺手触发一次值变化回调，让 UI 刷新（有就触发，没有就算了）
            try
            {
                var cbProp = ty.GetProperty("OnInputValueChanged");
                var cb = cbProp?.GetValue(ifc);
                if (cb != null) Log.LogInfo($"[输入框] 值变化回调类型 = {cb.GetType().Name}（未主动调用）");
            }
            catch { }

            return ok;
        }
        catch (Exception e)
        {
            Log.LogWarning($"[输入框] 写入异常：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }
}
