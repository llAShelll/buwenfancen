namespace BuWenFanChen.Panel;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

/// <summary>
/// ★ 2026-11-12：性格面板 · 游戏内 IMGUI 面板（1.4.0 P1）
/// 施工图：`施工_NPC数值面板_20261112.md` §6
///
/// 铁律：**纯 GUI.* + 手动坐标**——GUILayout / GUILayoutUtility 被游戏 IL2CPP 裁剪，调了会抛
/// `NotSupportedException: Method unstripping failed`（OnGUI 中断 → 面板空白）。
/// 不用 GUI.Popup / GUI.Window / GUI.Button / Event.Used / UnityEngine.Input（都不可用）。
///
/// 2026-11-12 第五轮反馈：
///   · 面板整体再小一号：字号 12 × scale，窗口 360×420；
///   · 数值条去掉末端光点；
///   · 「性格面板」按钮：文字=性格面板、字号与选项框一致（15 × scale）、位置在**选项框下方**；
///   · 拖动区域从窗口顶部整行（含标题文字区）都能拖，不再只有 24px 一条；
///   · 背景换成**程序生成的绿色古风纹理**（回纹边角 + 斜向暗纹，颜色可调；失败自动退回手绘框）。
///
/// 过审模式（hideNsfw）：NSFW 四项整块不画（§4.7）。
/// </summary>
public class PanelHud : MonoBehaviour
{
    private static readonly BepInEx.Logging.ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("不问凡尘·面板HUD");

    private static PanelHud _instance;
    private static bool _visible;
    private static Rect _rect = new Rect(-1, -1, 0, 0);
    private static PanelState _st;
    private static string _npcId = "";
    private static float _nextRefresh;
    private static bool _dragging;
    private static string _msg = "";
    private static float _msgUntil;
    private static bool _busy;
    private static int _protoIdx = -1;   // ★ 2026-11-14：-1 = 未设置（0 是「傲娇」的合法索引，不能再用 0 当哨兵）
    private static bool _dropdownOpen;
    // ★ 2026-11-14：记住「原型下拉是给哪个 NPC 选的 + 什么时候选的」——防止 id 抖动把选择清掉
    private static string _protoNpcId = "";
    private static float _protoSetAt = -100f;
    private static Rect _templateBtnRect;
    private static bool _closeClicked;

    // 纹理（程序生成的绿色古风底纹；尺寸变化时重建）
    private static Texture2D _panelTex;
    private static int _texW, _texH;

    // ★ 2026-11-12：外部纹理（玩家把喜欢的古风图放进 config\panel\panel_bg.png 即用，优先于程序纹理）
    private static Texture2D _extTex;
    private static DateTime _extMtime;
    private static bool _extLogged;

    // ★ 2026-11-12：数值条纹理（config\panel\bar_texture.png；白色/浅色横条素材，自动去白底后叠在彩色条上）
    private static Texture2D _barTex;
    // ★ 2026-11-14：渐变纹理（左暗右亮 / 左亮右暗）——数值条填充不再用 12 段色块
    private static Texture2D _gradTexL, _gradTexR;
    // ★ 2026-11-14：背景三段（顶/中/底）——用普通 DrawTexture 画九宫格
    //   （GUI.DrawTextureWithTexCoords 在 IL2CPP 下会抛 ObjectCollectedException，面板直接消失）
    private static Texture2D _bgTop, _bgMid, _bgBot;
    // ★ 2026-11-14 主人定：折叠/展开各一张背景图（panel_bg.png / panel_bg_open.png 可选）
    private static Texture2D _bgTexOpen;
    private static DateTime _bgOpenMtime = DateTime.MinValue;
    private static DateTime _barMtime;
    private static bool _barLogged;

    // ★ 2026-11-12：浅色皮肤（外部图中央偏亮时，文字自动转深色，保证观感）
    private static bool _lightSkin;

    /// <summary>★ 2026-11-13：解锁列表 / 原型匹配区折叠状态</summary>
    private static bool _unlockCollapsed;
    private static int _styleKey;

    // 输入框矩形缓存（按钮吸附 / 面板定位用；10 帧节流）
    private static float _btnL, _btnT, _btnW, _btnH;
    private static bool _btnOk;
    private static int _btnFrame = -9999, _btnSw = -1, _btnSh = -1;

    private static Font _font;
    private static GUIStyle _label, _labelDim, _btn, _btnSmall, _btnPlain, _btnDark, _title, _toggle;
    // ★ 2026-11-14：顶部行（模板/套用/锁定）字号加大一档
    private static GUIStyle _btnTop;
    private static GUIStyle _arrUp, _arrDown, _arrFlat;
    private static int _fs = 12;

    private void Awake() { _instance = this; }

    private static void EnsureInstance()
    {
        try
        {
            if (_instance != null) return;
            // ★ 2026-11-14 主人反馈「F5/切页面面板消失」：以前用 new GameObject + DontDestroyOnLoad，
            //   但游戏 F5 重建 UI 时会把这种自建根对象一起清掉（连 DontDestroyOnLoad 也保不住）。
            //   改成挂到 BepInEx 自己的常驻组件容器上（Chainloader 管理，游戏清不掉）。
            try { IL2CPPChainloader.AddUnityComponent<PanelHud>(); } catch { }
            Log.LogInfo("[面板HUD] 组件已重建");
        }
        catch (Exception e) { Log.LogWarning($"[面板HUD] 重建失败：{e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>★ 2026-11-14：F5/UI 重建后 OnGUI 宿主被销毁时，由看门狗周期调用——自动重建（静态状态保留，面板自动恢复）</summary>
    public static void EnsureAlive()
    {
        try { if (_instance == null) EnsureInstance(); } catch { }
    }

    public static void Toggle()
    {
        EnsureInstance();
        _visible = !_visible;
        Log.LogInfo("[面板HUD] " + (_visible ? "打开" : "关闭") + " 面板");
        if (_visible)
        {
            _lastDialogueAt = Time.unscaledTime;   // 防止刚打开就被自动隐藏兜底关掉
            EnsureExternalTexture();               // ★ 打开前先加载皮肤，避免首帧黑底
            _styleKey = 0;                         // 强制按新皮肤重建样式
            Refresh(true);
        }
    }
    private static float _lastDialogueAt;
    // ★ 2026-11-14 诊断：面板跳绘/绘制中的限流日志时间戳
    private static float _lastPanelLog, _lastPanelLog2;

    public static void HideFromScene()
    {
        if (_visible || _dropdownOpen)
        {
            _visible = false;
            _dragging = false;
            _dropdownOpen = false;
            Log.LogInfo("[面板HUD] 对话/场景结束 → 自动关闭");
        }
    }

    public static void Hide()
    {
        if (_visible) Log.LogInfo("[面板HUD] 面板被 Hide() 关闭");
        _visible = false;
        _dragging = false;
        _dropdownOpen = false;
    }

    // ==================== 数据 ====================

    private static void Refresh(bool force = false)
    {
        if (!force && Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 0.5f;

        // ★ 2026-11-14 主人要求：进新角色对话时面板跟着切过去（不再停在老角色）
        //   优先用「当前对话 NPC」（进对话就更新），回退 LastNpcId（模型回复时更新）
        var id = !string.IsNullOrEmpty(PanelStore.CurrentNpc) ? PanelStore.CurrentNpc : PanelStore.LastNpcId;
        if (string.IsNullOrEmpty(id)) { _st = null; _npcId = ""; return; }
        if (id != _npcId)
        {
            // ★ 2026-11-14 主人反馈「选完傲娇过 1 秒切回原来」：
            //   id 可能在 CurrentNpc / LastNpcId 之间抖动，抖一次就把玩家刚选的下拉清回 Best 推断。
            //   现在只有「真的换到另一个 NPC」才重置；选择后 2 秒内一律不重置（防止抖动误伤）。
            if (id != _protoNpcId && Time.unscaledTime - _protoSetAt > 2f)
            {
                _protoIdx = -1;
                _dropdownOpen = false;
                Log.LogInfo("[面板HUD] 换角色 → 原型下拉重置（npc=" + id + "）");
            }
        }
        _npcId = id;
        _st = PanelStore.Peek(id);
        try
        {
            if (_st != null && string.IsNullOrEmpty(_st.NpcName))
                _st.NpcName = BuWenFanChen.Memory.MemoryEngine.ResolveName(id) ?? "";
        }
        catch { }

        // ★ 2026-11-14：-1 = 还没选过下拉 → 按当前数值推断最像的原型；一旦手动选了（哪怕选的是索引 0 的「傲娇」）就保持不动
        if (_st != null && _protoIdx < 0)
        {
            var best = PanelArchetype.Best(_st.Values);
            for (int i = 0; i < PanelArchetype.All.Length; i++)
                if (PanelArchetype.All[i].Name == best.Name) { _protoIdx = i; break; }
            if (_protoIdx < 0) _protoIdx = 0;
        }
    }

    private static void Msg(string m, float secs = 4f)
    {
        _msg = m;
        _msgUntil = Time.unscaledTime + secs;
    }

    // ==================== 输入框矩形（10 帧节流） ====================

    private static void EnsureInputRect()
    {
        try
        {
            if (Time.frameCount - _btnFrame >= 10 || _btnSw != Screen.width || _btnSh != Screen.height)
            {
                _btnFrame = Time.frameCount;
                _btnSw = Screen.width;
                _btnSh = Screen.height;
                _btnOk = BuWenFanChen.InputFieldBridge.TryGetInputRect(out _btnL, out _btnT, out _btnW, out _btnH);
            }
        }
        catch { }
    }

    // ==================== 绘制入口 ====================

    private void OnGUI()
    {
        try
        {
            if (!PromptConfig.PanelEnabled)
            {
                // ★ 2026-11-14 诊断：面板被「总开关关闭」跳过时留痕（限流）
                if (Time.unscaledTime - _lastPanelLog > 30f) { _lastPanelLog = Time.unscaledTime; Log.LogInfo("[面板HUD] panelEnabled=false → 不绘制"); }
                return;
            }
            // ★ 2026-11-14 主人反馈「男角色面板打开后会自动关闭」：面板 UI 层不再按性别隐藏——
            //   男角色也能看面板数值（数值是通用的）；「男角色也能用面板」开关只决定
            //   要不要把面板写进 prompt（女性向文案不套给男角色，见 PanelStore.ShouldHidePanel 的注入侧拦截）
            if (_uiScale < 0f) _uiScale = PromptConfig.PanelScale;   // ★ 从 config 读玩家上次调的大小
            EnsureExternalTexture();   // ★ 先加载外部图（否则首帧还是旧配色 → 黑底白字）
            EnsureStyle();
            EnsureInputRect();
            if (_instance == null) EnsureInstance();

            if (!_visible) { DrawToggleButton(); return; }
            // ★ 2026-11-14 诊断：确认面板真的在画（每 30 秒一次，降噪）
            if (Time.unscaledTime - _lastPanelLog2 > 30f)
            {
                _lastPanelLog2 = Time.unscaledTime;
                Log.LogInfo("[面板HUD] 绘制中 _visible=true rect=" + _rect + " npc=" + _npcId + " 数据=" + (_st != null));
            }

            // ★ 2026-11-14 主人反馈「一开 F5 刷新就关了」：
            //   按 F5 / 切到浏览器控制台时游戏窗口失焦 → 输入框矩形读不到 → 原来 1.5 秒兜底会误关面板。
            //   现在去掉这个「输入框失效就自动关」的兜底：手动打开的面板只由场景/对话 hook 关闭
            //   （退出对话、切场景、回父空间等 6 处 hook 已覆盖）。
            bool inDialogue = (_btnOk && _btnW > 40f) || BuWenFanChen.OptionsHud.OptionsRectFresh();
            if (inDialogue) _lastDialogueAt = Time.unscaledTime;

            Refresh();
            float s = ScaleOf();
            bool firstOpen = _rect.x < 0;
            // ★ 2026-11-14 主人定：折叠/展开各配一张背景图，比例不动——面板尺寸直接按当前那张图的比例
            //   （内容区按图内框比例 + 一字间距排版，内容刚好填满红框；空面板（无数据）也用折叠态）
            bool collapsedState = _st == null || _unlockCollapsed;
            var bgNow = collapsedState ? _extTex : (_bgTexOpen ?? _extTex);
            float targetW = 548f * s;   // ★ 2026-11-14 主人要求：宽度不动（保持 548）
            float targetH = (bgNow != null && bgNow.width > 0)
                ? targetW * bgNow.height / bgNow.width
                : (collapsedState ? 521f : 711f) * s;
            bool needResize = firstOpen
                || Mathf.Abs(_rect.height - targetH) > 0.5f
                || Mathf.Abs(_rect.width - targetW) > 0.5f;
            if (needResize)
            {
                // ★ 2026-11-14 主人反馈：调尺寸时面板被强制拉回左边（每次缩放都重新贴选项框）。
                //   现在：只有第一次打开才按默认锚点定位；之后尺寸变化保持面板中心不动。
                float oldCx = _rect.x + _rect.width * 0.5f;
                float oldCy = _rect.y + _rect.height * 0.5f;

                _rect.height = targetH;
                _rect.width = targetW;

                if (firstOpen)
                {
                    var or = BuWenFanChen.OptionsHud.OptionsRect;
                    float right, optionsTop;
                    if (BuWenFanChen.OptionsHud.OptionsRectFresh()) { right = or.xMax; optionsTop = or.y; }
                    else if (_btnOk && _btnW > 40f)
                    {
                        right = Mathf.Max(8f, _btnL - 26f * s);
                        optionsTop = Mathf.Max(8f, _btnT + _btnH - 130f * s);
                    }
                    else { right = Screen.width - 16f; optionsTop = Screen.height - 16f - 130f * s; }

                    _rect.x = right - _rect.width;
                    _rect.y = optionsTop - _rect.height;
                }
                else
                {
                    // 尺寸变化：保持中心（玩家拖到哪就留在哪，不再被拉回左边）
                    _rect.x = oldCx - _rect.width * 0.5f;
                    _rect.y = oldCy - _rect.height * 0.5f;
                }

                _rect.x = Mathf.Clamp(_rect.x, 8f, Mathf.Max(8f, Screen.width - _rect.width - 8f));
                if (_rect.y < 8f) _rect.y = 8f;
                if (_rect.y + _rect.height > Screen.height - 8f) _rect.y = Screen.height - _rect.height - 8f;
            }

            EnsureBarTexture();
            EnsurePanelTexture();
            // ★ 2026-11-14 主人反馈「傲娇选项切换不了」：下拉列表盖住的按钮会抢走点击，这里先处理下拉点击
            if (_dropdownOpen) HandleProtoDropdownClick(s);
            DrawContent();
            HandleDrag();
        }
        catch (Exception e)
        {
            if ((DateTime.Now - _lastErr).TotalSeconds > 30)
            {
                _lastErr = DateTime.Now;
                Log.LogWarning($"[面板HUD] 绘制异常：{e.GetType().Name}: {e.Message}");
            }
        }
    }
    private static DateTime _lastErr = DateTime.MinValue;

    /// <summary>★ 2026-11-13：滑块 100% = 缩小 2/3 之前的原始大小；默认 66.7% 保持现在视觉</summary>
    private static float ScaleOf() => BaseScale() * Mathf.Clamp(_uiScale, 0.5f, 1.5f);

    /// <summary>用户自定义缩放（50%~150%，100% = 原始大小；-1 = 尚未从 config 读取）</summary>
    private static float _uiScale = -1f;
    private static bool _scaleDrag;

    /// <summary>屏幕基准缩放（按钮字号 / 选项框对齐用，不缩 2/3）</summary>
    private static float BaseScale() => Mathf.Max(0.72f, Screen.height / 1080f);

    /// <summary>「性格面板」按钮：对话框时显示，位置在**选项框下方**（无选项框时输入框左下方）</summary>
    private static void DrawToggleButton()
    {
        try
        {
            var r = ToggleRect(out bool inDialogue);
            if (!inDialogue) return;
            DrawToggleBox(r);   // ★ 统一浅底样式（不随皮肤切换，避免黑一下白一下）
            if (Click(r)) Toggle();
            float labelH = _toggle.fontSize * 1.6f;
            GUI.Label(new Rect(r.x + 2f, r.y + (r.height - labelH) * 0.5f, r.width - 4f, labelH),
                      "性格面板", _toggle);
        }
        catch { }
    }

    /// <summary>「性格面板」按钮：★ 2026-11-13 跟选项框同款设计（半透明深底 + #60B28C 绿边 + 浅色字）</summary>
    private static void DrawToggleBox(Rect r)
    {
        var old = GUI.color;
        var e = Event.current;
        bool hover = e != null && r.Contains(e.mousePosition);
        GUI.color = hover ? new Color(0.10f, 0.16f, 0.14f, 0.94f) : new Color(0.03f, 0.06f, 0.055f, 0.86f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = hover ? new Color(0.42f, 0.66f, 0.56f, 0.98f) : new Color(0.30f, 0.52f, 0.44f, 0.92f);
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1.5f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - 1.5f, r.width, 1.5f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y, 1.5f, r.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - 1.5f, r.y, 1.5f, r.height), Texture2D.whiteTexture);
        GUI.color = old;
    }

    private static Rect ToggleRect(out bool inDialogue)
    {
        inDialogue = false;
        float s = BaseScale();   // ★ 按钮尺寸不跟面板一起缩（主人要求）
        EnsureInputRect();

        float w = 120f * s, h = 32f * s;
        // ★ 2026-11-13 主人要求：按钮放**选项框（对话框）下方**，避免玩家点输入框时误点
        if (OptionsHud.OptionsRectFresh())
        {
            var or = BuWenFanChen.OptionsHud.OptionsRect;
            float x = Mathf.Clamp(or.x, 4f, Mathf.Max(4f, Screen.width - w - 4f));
            float y = or.yMax + 5f * s;
            if (y + h > Screen.height - 4f) y = Mathf.Max(4f, or.y - h - 5f * s);
            inDialogue = true;
            return new Rect(x, y, w, h);
        }
        if (_btnOk && _btnW > 40f)
        {
            // ★ 2026-11-13：没有选项框时也放在「选项框区域」下方（输入框左边那带），不要跑到输入框正下方
            float x = Mathf.Clamp(_btnL - 26f * s - w, 4f, Mathf.Max(4f, Screen.width - w - 4f));
            float y = _btnT + _btnH + 4f * s;
            if (y + h > Screen.height - 4f) y = Mathf.Max(4f, _btnT - h - 4f);
            inDialogue = true;
            return new Rect(x, y, w, h);
        }
        return new Rect(Screen.width - 140f * s, Screen.height - 48f * s, w, h);
    }

    // ==================== 面板内容 ====================

    private static void DrawContent()
    {
        float s = ScaleOf();
        float lineH = 19f * s;   // ★ 2026-11-14：标题/模板行高压缩（21→19），适配内容区高度

        // 背景：整图绘制（★ 2026-11-14：折叠/展开各一张图，比例不动、不拉伸）
        var bg = (_st == null || _unlockCollapsed) ? _extTex : (_bgTexOpen ?? _extTex);
        if (bg != null)
        {
            var oldC = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(_rect, bg);
            GUI.color = oldC;
        }
        else if (_panelTex != null)
        {
            var oldC = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(_rect, _panelTex);
            GUI.color = oldC;
        }
        else if (_panelTex != null)
        {
            var oldC = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(_rect, _panelTex);
            GUI.color = oldC;
        }
        else
        {
            var oldC = GUI.color;
            GUI.color = new Color(0.02f, 0.035f, 0.035f, 0.88f);
            GUI.DrawTexture(_rect, Texture2D.whiteTexture);
            GUI.color = oldC;
        }
        if (_extTex == null) DrawFrame(_rect, s);   // 外部图自带边框，不再叠画

        // ×
        var close = new Rect(_rect.xMax - 26f * s, _rect.y + 5f * s, 21f * s, 19f * s);
        DrawBox(close, false);
        if (Click(close)) { Hide(); return; }
        GUI.Label(close, "×", _btnSmall);

        // ★ 2026-11-14 主人定：内容区用【固定像素】定义（图片像素，随图长度自动变动，不按占比推）
        //   上 = 图片顶端往下 390 图px；下 = 图片底端往上 310 图px；左右 = 图片左右各向中间 100 图px
        var bgForLayout = (_st == null || _unlockCollapsed) ? _extTex : (_bgTexOpen ?? _extTex);
        float k = (bgForLayout != null && bgForLayout.width > 0) ? _rect.width / bgForLayout.width : 1f;
        const float TopPx = 390f, BottomPx = 310f, SidePx = 100f;
        var inner = new Rect(_rect.x + SidePx * k, _rect.y + TopPx * k,
                             Mathf.Max(60f, _rect.width - 2f * SidePx * k),
                             Mathf.Max(60f, _rect.height - (TopPx + BottomPx) * k));
        float y = inner.y;
        float labelW = 36f * s;

        var st = _st;
        if (st == null)
        {
            // ★ 2026-11-14 主人要求：空面板提示字号加大 + 加粗 + 纯黑
            LabelBold(new Rect(inner.x, y, inner.width, lineH * 2f), "先和任意角色聊一句，再打开面板。", _title);
            return;
        }

        string npcName = string.IsNullOrEmpty(st.NpcName) ? st.NpcId : st.NpcName;
        LabelBold(new Rect(inner.x, y, inner.width, lineH), "性格面板 · " + npcName, _title);
        y += lineH + 2f * s;

        // ===== 模板行：下拉 + 套用 + 锁定 =====
        if (_protoIdx < 0) _protoIdx = 0;   // 兜底（正常 Refresh 已推断好）
        LabelBold(new Rect(inner.x, y + 3f * s, labelW, lineH), "模板", _label);
        var tpl = new Rect(inner.x + labelW, y, 96f * s, 21f * s);
        _templateBtnRect = tpl;
        DrawBox(tpl, false);
        if (Click(tpl)) _dropdownOpen = !_dropdownOpen;
        LabelBold(tpl, PanelArchetype.All[Mathf.Clamp(_protoIdx, 0, PanelArchetype.All.Length - 1)].Name + " ▾", _btnTop);

        var apply = new Rect(tpl.xMax + 5f * s, y, 46f * s, 21f * s);
        DrawBox(apply, false);
        if (Click(apply))
        {
            if (PanelStore.ApplyArchetype(_npcId, PanelArchetype.All[Mathf.Clamp(_protoIdx, 0, PanelArchetype.All.Length - 1)].Name))
            {
                Msg("已套用模板：" + PanelArchetype.All[Mathf.Clamp(_protoIdx, 0, PanelArchetype.All.Length - 1)].Name);
                Refresh(true);
            }
        }
        LabelBold(apply, "套用", _btnTop);

        var lockR = new Rect(apply.xMax + 5f * s, y, 56f * s, 21f * s);
        DrawBox(lockR, false);
        if (Click(lockR)) { PanelStore.SetLocked(_npcId, !st.Locked); Refresh(true); }
        LabelBold(lockR, (st.Locked ? "☑ 锁定" : "☐ 锁定"), _btnTop);
        y += lineH + 2f * s;

        // ★ 2026-11-14 主人要求：加备注——建议和角色卡一同使用（本地规则判断不如 API 读角色卡准）
        LabelBold(new Rect(inner.x, y, inner.width * 0.62f, 16f * s), "初值来源：" + SourceText(st.InitSource), _label);
        GUI.Label(new Rect(inner.x + inner.width * 0.62f, y, inner.width * 0.38f, 16f * s), "（建议配合角色卡）", _labelDim);
        y += 16f * s;

        // ===== 情绪四维 =====
        y = DrawSeparatorLine(inner, y, s);
        GUI.Label(new Rect(inner.x, y, inner.width, 16f * s), "情绪四维", _labelDim);
        y += 16f * s;
        y = DrawValueRow(inner, y, st, 0, "温度", -100, 100, new Color(1f, 0.55f, 0.30f), s);
        y = DrawValueRow(inner, y, st, 1, "态度", -100, 100, new Color(0.72f, 0.58f, 1f), s);
        y = DrawValueRow(inner, y, st, 2, "主动", -100, 100, new Color(0.38f, 0.88f, 0.72f), s);
        y = DrawValueRow(inner, y, st, 3, "话量", -100, 100, new Color(0.95f, 0.82f, 0.38f), s);

        // ===== 亲密状态（过审模式整块不画） =====
        if (!PromptConfig.HideNsfw)
        {
            y = DrawSeparatorLine(inner, y, s);
            GUI.Label(new Rect(inner.x, y, inner.width, 16f * s), "亲密状态", _labelDim);
            y += 16f * s;
            string[] names = { "服从", "羞耻", "开放", "崩坏" };
            Color[] colors =
            {
                new Color(0.42f, 0.66f, 1f), new Color(1f, 0.58f, 0.74f),
                new Color(0.88f, 0.55f, 0.95f), new Color(0.85f, 0.32f, 0.32f),
            };
            for (int i = 0; i < 4; i++)
                y = DrawValueRow(inner, y, st, 4 + i, names[i], 0, 100, colors[i], s);
        }

        // ===== NSFW 玩法解锁 + 原型匹配（★ 2026-11-13：可折叠，点标题行收起/展开） =====
        y = DrawSeparatorLine(inner, y, s);
        var bestPre = PanelArchetype.Best(st.Values);
        var foldRect = new Rect(inner.x, y, inner.width, 18f * s);
        string foldTitle = (_unlockCollapsed ? "▶ " : "▼ ") + "玩法解锁 / 原型匹配"
            + (bestPre.Name != "未定型" ? "（" + bestPre.Name + "）" : "");
        if (Click(foldRect)) _unlockCollapsed = !_unlockCollapsed;
        GUI.Label(foldRect, foldTitle, _labelDim);
        y += 18f * s;
        if (!_unlockCollapsed)
        {
            foreach (var line in GatingLines(st).Take(10))
            {
                LabelBold(new Rect(inner.x, y, inner.width, 16f * s), line, _labelDim);
                y += 16f * s;
            }

            // ===== 原型匹配 =====
            var best = bestPre;
            LabelBold(new Rect(inner.x, y, inner.width, 18f * s),
                "当前：" + (best.Name == "未定型" ? "未定型（多聊几句再调）" : best.Name + "（匹配 " + best.Match + "%）"), _label);
            y += 18f * s;
        }

        // ★ 2026-11-13 §3.1.1：好感软约束 ⚠（和状态块同一套判断，不拦不 clamp）
        var warn = PanelRender.RelationWarn(st, _npcId);
        if (!string.IsNullOrEmpty(warn))
        {
            GUI.Label(new Rect(inner.x, y, inner.width, 18f * s), "⚠ " + warn, _labelDim);
            y += 16f * s;
        }

        // ===== 两个推断按钮 =====
        if (_busy)
        {
            GUI.Label(new Rect(inner.x, y, inner.width, 20f * s), "AI 推断中…（花 1 次 API，约 500 token）", _labelDim);
        }
        else
        {
            float btnW = (inner.width - 7f * s) * 0.62f;
            var b1 = new Rect(inner.x, y, btnW, 26f * s);
            DrawBox(b1, true);
            if (Click(b1)) { _busy = true; Msg("正在调用 API 推断…", 10f); _ = RunLlmInit(); }
            LabelBold(b1, "按角色卡重新推断 · 花1次API", _btnSmall);

            var b2 = new Rect(b1.xMax + 7f * s, y, inner.xMax - (b1.xMax + 7f * s), 26f * s);
            DrawBox(b2, true);
            if (Click(b2)) { PanelStore.ResetByKeywords(_npcId); Msg("已按角色卡本地规则重置（免费）"); Refresh(true); }
            LabelBold(b2, "重置本角色 · 免费", _btnSmall);
        }
        y += 28f * s;

        if (!string.IsNullOrEmpty(_msg) && Time.unscaledTime < _msgUntil)
            GUI.Label(new Rect(inner.x, y, inner.width, 18f * s), _msg, _labelDim);
        y += 16f * s;

        // ★ 2026-11-13：自定义缩放滑块（面板底部，0.6~1.6 倍）
        DrawScaleRow(inner, y, s);

        // 模板下拉列表（自绘，盖在最上层）
        if (_dropdownOpen) DrawProtoDropdown(s);
    }

    /// <summary>★ 2026-11-13：自定义缩放滑块（0.6~1.6 倍，拖动即时生效）</summary>
    private static void DrawScaleRow(Rect inner, float y, float s)
    {
        LabelBold(new Rect(inner.x, y + 3f * s, 40f * s, 16f * s), "缩放", _labelDim);

        var track = new Rect(inner.x + 42f * s, y + 7f * s, Mathf.Max(40f, inner.width - 42f * s - 48f * s), 7f * s);
        var old = GUI.color;
        GUI.color = _lightSkin ? new Color(0.88f, 0.78f, 0.74f, 0.90f) : new Color(0.16f, 0.22f, 0.20f, 0.90f);
        GUI.DrawTexture(track, Texture2D.whiteTexture);

        float t = Mathf.InverseLerp(0.5f, 1.5f, Mathf.Clamp(_uiScale, 0.5f, 1.5f));
        float knobW = 9f * s;
        float knobX = track.x + (track.width - knobW) * t;
        GUI.color = _lightSkin ? new Color(0.62f, 0.28f, 0.20f, 0.95f) : new Color(0.30f, 0.52f, 0.44f, 0.95f);
        GUI.DrawTexture(new Rect(knobX, track.y - 3f * s, knobW, 13f * s), Texture2D.whiteTexture);
        GUI.color = old;

        LabelBold(new Rect(inner.xMax - 44f * s, y + 3f * s, 44f * s, 16f * s),
                  Mathf.RoundToInt(_uiScale * 100f) + "%", _labelDim);

        // 拖动 / 点击
        var e = Event.current;
        if (e == null) return;
        if (e.type == EventType.MouseDown && e.button == 0 && track.Contains(e.mousePosition))
        {
            _scaleDrag = true;
            e.Use();
        }
        if (_scaleDrag && e.type == EventType.MouseDrag)
        {
            float p = Mathf.Clamp01((e.mousePosition.x - track.x) / Mathf.Max(1f, track.width));
            _uiScale = Mathf.Lerp(0.5f, 1.5f, p);
            e.Use();
        }
        if (e.type == EventType.MouseUp && _scaleDrag)
        {
            _scaleDrag = false;
            BuWenFanChen.PromptConfig.SetPanelScale(_uiScale);   // ★ 松手即写进 config.json
        }
        else if (e.type == EventType.MouseUp) _scaleDrag = false;
    }

    /// <summary>一行：轴名 + 渐变彩色条（只读）+ 数值</summary>
    private static float DrawValueRow(Rect inner, float y, PanelState st, int axis, string name,
                                      int min, int max, Color color, float s)
    {
        // ★ 2026-11-14 分档变色：颜色随数值变化（覆盖调用处传的固定色）
        color = AxisColor(axis, st.Values[axis]);
        float lineH = 22f * s;   // ★ 2026-11-14：数值行高压缩（24→22）
        GUI.Label(new Rect(inner.x, y + 2f * s, 36f * s, lineH), name, _label);

        float x = inner.x + 36f * s;
        float valW = 30f * s;
        var bar = new Rect(x, y + 3f * s, Mathf.Max(40f, inner.width - 36f * s - valW - 18f * s), 20f * s);
        DrawBar(bar, st.Values[axis], min, max, color, axis);

        int v = st.Values[axis];
        var val = new Rect(bar.xMax + 4f * s, y + 1f * s, valW, lineH);
        GUI.Label(val, axis < 4 && v > 0 ? "+" + v : v.ToString(), _label);

        // ★ 2026-11-13：本轮 delta 箭头（↑ / ↓ / ─），让玩家看到数值在动
        int d = 0;
        var ld = PanelStore.LastDelta;
        if (ld != null && ld.Length > axis && PanelStore.LastDeltaNpc == _npcId) d = ld[axis];
        var arr = new Rect(val.xMax, y + 1f * s, 16f * s, lineH);
        LabelBold(arr, d > 0 ? "↑" : (d < 0 ? "↓" : "─"), d > 0 ? _arrUp : (d < 0 ? _arrDown : _arrFlat));

        return y + lineH + 1f * s;
    }

    /// <summary>
    /// 渐变数值条（只读）：★ 2026-11-12 主人要求 —— 用素材里的**胶囊形**当底框（不再用方块浅灰底），
    /// 彩色数值填充叠在胶囊上面（内缩避开圆角与金边）；没有素材时才退回原来的方块样式。
    /// </summary>
    private static void DrawBar(Rect r, int value, int min, int max, Color color, int axis)
    {
        var old = GUI.color;
        // ★ 2026-11-14 主人要求：数值条继续用素材，但把素材的白色纸底抠成透明（只留墨边/云纹）
        bool hasSkin = _barTex != null;
        if (hasSkin)
        {
            GUI.color = Color.white;
            GUI.DrawTexture(r, _barTex);
        }
        else
        {
            // 兜底（没素材时）：极淡纸底 + 细墨线
            GUI.color = _lightSkin ? new Color(1f, 1f, 1f, 0.10f) : new Color(1f, 1f, 1f, 0.06f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = _lightSkin ? new Color(0.42f, 0.28f, 0.18f, 0.45f) : new Color(0.62f, 0.72f, 0.66f, 0.40f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 1f, r.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, 1f, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax - 1f, r.y, 1f, r.height), Texture2D.whiteTexture);
        }

        float t = Mathf.Clamp01((value - min) / (float)(max - min));
        // 填充内缩：避开素材两端的深墨收边（按素材比例 ~3.5% 宽）与上下边
        float padX = hasSkin ? Mathf.Max(3f, r.width * 0.035f) : 1f;
        float padY = hasSkin ? Mathf.Max(1f, r.height * 0.18f) : 1f;
        float innerW = r.width - padX * 2f;
        float innerH = r.height - padY * 2f;
        Rect fill = default;

        if (axis < 4)
        {
            float half = innerW * 0.5f;
            float cx = r.x + r.width * 0.5f;
            float w = half * t;
            if (w > 0.5f)
            {
                fill = value >= 0 ? new Rect(cx, r.y + padY, w, innerH)
                                  : new Rect(cx - w, r.y + padY, w, innerH);
                DrawGradient(fill, color, value >= 0);
            }
            if (!hasSkin)
            {
                GUI.color = new Color(0.40f, 0.62f, 0.52f, 0.6f);
                GUI.DrawTexture(new Rect(cx, r.y, 1f, r.height), Texture2D.whiteTexture);
            }
        }
        else
        {
            float w = innerW * t;
            if (w > 0.5f)
            {
                fill = new Rect(r.x + padX, r.y + padY, w, innerH);
                DrawGradient(fill, color, true);
            }
        }

        // ★ 2026-11-14：底框已手画（淡纸底+墨线），这里不再画旧的四角倒角/绿边
        GUI.color = old;
    }

    /// <summary>★ 2026-11-14：生成渐变纹理（1×64 灰度，Clamp）——平滑无块，比 12 段色块细腻。</summary>
    private static void EnsureGradTex()
    {
        if (_gradTexL != null) return;
        try
        {
            const int N = 64;
            _gradTexL = new Texture2D(N, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            _gradTexR = new Texture2D(N, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pxL = new Color32[N * 2];
            var pxR = new Color32[N * 2];
            for (int i = 0; i < N; i++)
            {
                float t = i / (float)(N - 1);
                float kL = 0.58f + 0.42f * t;      // 左暗 → 右亮
                float kR = 1.0f - 0.42f * t;       // 左亮 → 右暗
                var cL = new Color(kL, kL, kL, 1f);
                var cR = new Color(kR, kR, kR, 1f);
                for (int y = 0; y < 2; y++)
                {
                    pxL[i + y * N] = cL;
                    pxR[i + y * N] = cR;
                }
            }
            _gradTexL.SetPixels32(pxL); _gradTexL.Apply();
            _gradTexR.SetPixels32(pxR); _gradTexR.Apply();
            _gradTexL.hideFlags = HideFlags.HideAndDontSave;
            _gradTexR.hideFlags = HideFlags.HideAndDontSave;
        }
        catch
        {
            _gradTexL = null;
            _gradTexR = null;
        }
    }

    /// <summary>★ 2026-11-14：数值颜色——四轴按原设计区分色相（温度橙/态度紫/主动青绿/话量金黄），数值只调明暗；NSFW 各自递进。</summary>
    private static Color AxisColor(int axis, int value)
    {
        if (axis < 4)
        {
            Color baseColor = axis switch
            {
                0 => new Color(0.98f, 0.56f, 0.30f),   // 温度：暖橙
                1 => new Color(0.72f, 0.58f, 0.98f),   // 态度：紫
                2 => new Color(0.42f, 0.86f, 0.70f),   // 主动：青绿
                _ => new Color(0.95f, 0.80f, 0.40f),   // 话量：金黄
            };
            float t = Mathf.Clamp01((value + 100f) / 200f);
            float sat = 0.55f + 0.45f * t;             // 数值低 → 偏灰，数值高 → 鲜艳
            return Color.Lerp(new Color(0.62f, 0.62f, 0.62f), baseColor, sat);
        }
        float v = Mathf.Clamp01(value / 100f);
        switch (axis)
        {
            case 4: return Color.Lerp(new Color(0.42f, 0.66f, 1f), new Color(0.26f, 0.40f, 0.95f), v);
            case 5: return Color.Lerp(new Color(0.95f, 0.62f, 0.68f), new Color(1f, 0.42f, 0.55f), v);
            case 6: return Color.Lerp(new Color(0.80f, 0.55f, 0.95f), new Color(0.72f, 0.30f, 0.92f), v);
            default: return Color.Lerp(new Color(0.85f, 0.32f, 0.32f), new Color(0.90f, 0.12f, 0.12f), v);
        }
    }

    /// <summary>渐变填充（★ 2026-11-14：优先用渐变纹理，平滑无块 + 半透明透出底框纹理）</summary>
    private static void DrawGradient(Rect r, Color c, bool brightRight)
    {
        EnsureGradTex();
        if (_gradTexL != null)
        {
            var old = GUI.color;
            GUI.color = new Color(c.r, c.g, c.b, 0.84f);   // V2：半透明，底框水墨纹透出
            GUI.DrawTexture(r, brightRight ? _gradTexL : _gradTexR);
            GUI.color = old;
            return;
        }
        const int segs = 12;
        for (int i = 0; i < segs; i++)
        {
            float f0 = i / (float)segs;
            float f1 = (i + 1f) / segs;
            float k = brightRight ? 0.55f + 0.45f * f1 : 1.0f - 0.45f * f1;
            float lift = 0.10f * (1f - k);
            GUI.color = new Color(c.r * k + lift, c.g * k + lift, c.b * k + lift, c.a);
            GUI.DrawTexture(new Rect(r.x + r.width * f0, r.y, r.width * (f1 - f0) + 0.6f, r.height),
                            Texture2D.whiteTexture);
        }
    }

    private static float DrawSeparatorLine(Rect inner, float y, float s)
    {
        var old = GUI.color;
        // ★ 2026-11-14：粉色主题（浅色皮肤）用棕金分隔线，深色皮肤保留绿色
        GUI.color = _lightSkin ? new Color(0.55f, 0.30f, 0.22f, 0.55f) : new Color(0.30f, 0.52f, 0.44f, 0.38f);
        GUI.DrawTexture(new Rect(inner.x, y + 3f * s, inner.width, 1f), Texture2D.whiteTexture);
        GUI.color = _lightSkin ? new Color(0.62f, 0.34f, 0.18f, 0.85f) : new Color(0.48f, 0.72f, 0.60f, 0.6f);
        GUI.DrawTexture(new Rect(inner.x, y + 1.5f * s, 3f * s, 4f * s), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(inner.xMax - 3f * s, y + 1.5f * s, 3f * s, 4f * s), Texture2D.whiteTexture);
        GUI.color = old;
        return y + 6f * s;
    }

    private static void DrawFrame(Rect r, float s)
    {
        var old = GUI.color;
        // ★ 2026-11-14：面板边框按皮肤配色（粉色主题用棕金，深色皮肤保留绿色）
        GUI.color = _lightSkin ? new Color(0.62f, 0.38f, 0.18f, 0.92f) : new Color(0.36f, 0.60f, 0.50f, 0.92f);
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1.5f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - 1.5f, r.width, 1.5f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y, 1.5f, r.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - 1.5f, r.y, 1.5f, r.height), Texture2D.whiteTexture);
        GUI.color = old;
    }

    private static void DrawBox(Rect r, bool accent)
    {
        var old = GUI.color;
        var e = Event.current;
        bool hover = e != null && r.Contains(e.mousePosition);
        if (_lightSkin)
        {
            // 浅色皮肤：半透明白底 + 棕金边（★ 2026-11-14 粉色主题配色）
            GUI.color = hover ? new Color(1f, 1f, 1f, 0.86f) : new Color(1f, 1f, 1f, 0.60f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = hover ? new Color(0.50f, 0.26f, 0.18f, 0.95f) : new Color(0.62f, 0.38f, 0.26f, 0.85f);
        }
        else
        {
            GUI.color = hover ? new Color(0.11f, 0.18f, 0.16f, 0.96f) : new Color(0.07f, 0.11f, 0.10f, 0.92f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = hover ? new Color(0.50f, 0.74f, 0.62f, 0.95f)
                              : (accent ? new Color(0.42f, 0.66f, 0.56f, 0.95f) : new Color(0.30f, 0.52f, 0.44f, 0.80f));
        }
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - 1f, r.width, 1f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y, 1f, r.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - 1f, r.y, 1f, r.height), Texture2D.whiteTexture);
        GUI.color = old;
    }

    /// <summary>★ 2026-11-14：双绘加粗（描两遍、第二遍右移 0.7px）——自定义字体不支持 fontStyle.Bold 时也能加粗。</summary>
    private static void LabelBold(Rect r, string text, GUIStyle st)
    {
        GUI.Label(r, text, st);
        try { GUI.Label(new Rect(r.x + 0.7f, r.y, r.width, r.height), text, st); } catch { }
    }

    private static bool Click(Rect r)
    {
        var e = Event.current;
        if (e == null) return false;
        if (e.type != EventType.MouseDown || e.button != 0) return false;
        if (!r.Contains(e.mousePosition)) return false;
        e.Use();
        return true;
    }

    /// <summary>★ 2026-11-14：下拉列表矩形（绘制与点击判定共用，保证坐标一致）</summary>
    private static Rect ProtoListRect(float s)
    {
        float itemH = 22f * s;
        int n = PanelArchetype.All.Length;
        var list = new Rect(_templateBtnRect.x, _templateBtnRect.yMax + 2f * s, 120f * s, itemH * n);
        if (list.yMax > Screen.height - 4f) list.y = _templateBtnRect.y - list.height - 2f * s;
        if (list.y < 4f) list.y = 4f;
        return list;
    }

    /// <summary>
    /// ★ 2026-11-14 主人反馈「傲娇选项切换不了」：
    /// 下拉列表展开后会盖住下面的「套用/锁定」等按钮，而那些按钮的点击判定更早
    /// （同一个 MouseDown 被按钮先 Use 掉）→ 点列表条目实际点到了下面的按钮。
    /// 这里在绘制内容之前先把下拉的点击处理掉（优先级最高）。
    /// </summary>
    private static void HandleProtoDropdownClick(float s)
    {
        try
        {
            var e = Event.current;
            if (e == null) return;
            if (e.type != EventType.MouseDown || e.button != 0) return;
            var list = ProtoListRect(s);
            if (!list.Contains(e.mousePosition))
            {
                _dropdownOpen = false;   // 点下拉以外 → 关闭，不抢事件（让下面的按钮正常响应）
                return;
            }
            float itemH = 22f * s;
            int n = PanelArchetype.All.Length;
            for (int i = 0; i < n; i++)
            {
                var ir = new Rect(list.x + 2f, list.y + 2f + i * itemH, list.width - 4f, itemH - 2f);
                if (!ir.Contains(e.mousePosition)) continue;
                _protoIdx = i;
                _protoNpcId = _npcId;
                _protoSetAt = Time.unscaledTime;
                _dropdownOpen = false;
                Msg("模板：" + PanelArchetype.All[i].Name, 2f);
                Log.LogInfo("[面板HUD] 选了原型模板：" + PanelArchetype.All[i].Name + "（npc=" + _npcId + "）");
                e.Use();
                return;
            }
            e.Use();   // 点在列表空白处也吃掉，避免误触下面的按钮
        }
        catch { }
    }

    private static void DrawProtoDropdown(float s)
    {
        try
        {
            float itemH = 22f * s;
            int n = PanelArchetype.All.Length;
            var list = ProtoListRect(s);

            var old = GUI.color;
            // ★ 2026-11-13：模板下拉改成浅底深字（之前黑底在浅绿面板上很突兀、字也发灰看不清）
            GUI.color = new Color(0.97f, 0.985f, 0.96f, 0.98f);
            GUI.DrawTexture(list, Texture2D.whiteTexture);
            GUI.color = new Color(0.26f, 0.48f, 0.36f, 0.95f);
            GUI.DrawTexture(new Rect(list.x, list.y, list.width, 1.5f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(list.x, list.yMax - 1.5f, list.width, 1.5f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(list.x, list.y, 1.5f, list.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(list.xMax - 1.5f, list.y, 1.5f, list.height), Texture2D.whiteTexture);
            GUI.color = old;

            for (int i = 0; i < n; i++)
            {
                var ir = new Rect(list.x + 2f, list.y + 2f + i * itemH, list.width - 4f, itemH - 2f);
                bool hover = Event.current != null && ir.Contains(Event.current.mousePosition);
                if (hover)
                {
                    var c = GUI.color;
                    GUI.color = new Color(0.78f, 0.91f, 0.83f, 0.98f);
                    GUI.DrawTexture(ir, Texture2D.whiteTexture);
                    GUI.color = c;
                }
                GUI.Label(new Rect(ir.x + 6f, ir.y + 1f, ir.width - 12f, ir.height - 2f), PanelArchetype.All[i].Name, _btnDark);
                // 点击已在 HandleProtoDropdownClick 里提前处理（避免被下方按钮抢事件）
            }
        }
        catch { }
    }

    /// <summary>★ 拖动区域：窗口顶部整条（含「性格面板 · xxx」标题行 + 右侧空白），不再只有 24px</summary>
    private static void HandleDrag()
    {
        try
        {
            var e = Event.current;
            if (e == null) return;
            float s = ScaleOf();
            // 顶部整行都可拖（右侧 30px 留给 × 按钮）
            var bar = new Rect(_rect.x, _rect.y, _rect.width - 30f * s, 30f * s);
            if (e.type == EventType.MouseDown && e.button == 0 && bar.Contains(e.mousePosition))
            {
                _dragging = true;
                e.Use();
            }
            else if (_dragging && e.type == EventType.MouseDrag)
            {
                _rect.position += e.delta;
                _rect.x = Mathf.Clamp(_rect.x, 0f, Screen.width - _rect.width);
                _rect.y = Mathf.Clamp(_rect.y, 0f, Screen.height - _rect.height);
                e.Use();
            }
            else if (e.type == EventType.MouseUp) _dragging = false;
        }
        catch { }
    }

    private static async System.Threading.Tasks.Task RunLlmInit()
    {
        try
        {
            var (ok, msg) = await PanelStore.ReInitByLlmAsync(_npcId);
            _busy = false;
            Msg(msg ?? "完成", 8f);
            Refresh(true);
        }
        catch (Exception e)
        {
            _busy = false;
            Msg("AI 推断异常：" + e.GetType().Name, 8f);
        }
    }

    private static string SourceText(string src) => src switch
    {
        "keyword" => "角色卡本地规则推断",
        "llm" => "AI 读角色卡推断",
        "template" => "模板套用",
        "manual" => "手动调整",
        "default" => "默认值（还没角色卡）",
        _ => src ?? "默认值",
    };

    private static IEnumerable<string> GatingLines(PanelState st)
    {
        var lines = new List<string>();
        if (PromptConfig.HideNsfw || !PromptConfig.PanelNsfw) return lines;

        int open = st.Values[6], sub = st.Values[4], cor = st.Values[7];
        lines.Add("✓ 私下常规　默认可用");
        void Add(string name, bool ok, string need)
            => lines.Add((ok ? "✓ " : "· ") + name + (ok ? "　已解锁" : "　需要 " + need));
        Add("半褪 / 蒙眼 / 轻微束缚", open >= 21, "开放 21");
        Add("露出（窗边 · 半掩）", open >= 41, "开放 41");
        Add("半公开（野外 · 隔墙有人）", open >= 61, "开放 61");
        Add("公开（多人 · 故意展示）", open >= 81, "开放 81");
        Add("SM / 轻度调教", open >= 21 && sub >= 40, "开放 21 + 服从 40");
        Add("强制 / 命令式", open >= 41 && sub >= 60, "开放 41 + 服从 60");
        Add("痛感 / 惩罚向", open >= 61 && sub >= 70, "开放 61 + 服从 70");
        Add("触手 / 催眠（崩坏类）", open >= 41 && cor >= 30, "开放 41 + 崩坏 30");
        Add("肉便器化 / 精神崩坏", open >= 81 && cor >= 60, "开放 81 + 崩坏 60");
        return lines;
    }

    // ==================== 程序生成的绿色古风底纹 ====================

    /// <summary>
    /// ★ 2026-11-12：外部纹理支持 —— 玩家把喜欢的古风图改名 panel_bg.png 放进
    /// `config\panel\`，面板直接用这张图当背景（优先于程序纹理；建议 6:7 比例，如 720×840）。
    /// </summary>
    private static void EnsureExternalTexture()
    {
        try
        {
            var dir = System.IO.Path.Combine(PromptConfig.Dir, "panel");
            if (!System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
                try
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "说明.txt"),
                        "性格面板背景图：\r\n" +
                        "把喜欢的图片改名成 panel_bg.png 放在本目录，重启游戏即生效。\r\n" +
                        "建议尺寸 720×840（或比例接近 6:7），面板会拉伸铺满，比例差太多会变形。\r\n" +
                        "删掉这张图就恢复默认的绿色回纹底纹。\r\n",
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            var p = System.IO.Path.Combine(dir, "panel_bg.png");
            var po = System.IO.Path.Combine(dir, "panel_bg_open.png");
            if (!System.IO.File.Exists(p))
            {
                if (_extTex != null || _lightSkin)
                {
                    _extTex = null;
                    _bgTexOpen = null;
                    _lightSkin = false;
                    _styleKey = 0;
                    _rect.x = -1;
                }
                return;
            }
            var mtime = System.IO.File.GetLastWriteTimeUtc(p);
            if (_extTex != null && mtime == _extMtime) { }
            else
            {
                var bytes = System.IO.File.ReadAllBytes(p);
                if (bytes == null || bytes.Length < 16) { _extTex = null; return; }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (UnityEngine.ImageConversion.LoadImage(tex, bytes))
                {
                    tex.hideFlags = HideFlags.HideAndDontSave;   // ★ 防 GC 回收
                    var old = _extTex;
                    _extTex = tex;
                    _extMtime = mtime;
                    if (old != null) { try { UnityEngine.Object.Destroy(old); } catch { } }

                    // ★ 浅色皮肤判定（取中央区域平均亮度）
                    _lightSkin = IsLightTexture(tex, out int lum);
                    _rect.x = -1;      // 触发下一帧重新定位/定尺寸
                    _styleKey = 0;     // 强制重建样式（文字颜色跟着换）
                    Log.LogInfo($"[面板HUD] 已加载背景图（折叠）：{p}（{tex.width}x{tex.height}，亮度={lum}，{(lum >= 170 ? "浅色皮肤→深色字" : "深色皮肤→浅色字")}）");
                }
                else
                {
                    _extTex = null;
                    if (!_extLogged) { _extLogged = true; Log.LogWarning($"[面板HUD] 外部背景图加载失败（不是有效图片）：{p}"); }
                }
            }

            // ★ 2026-11-14 主人定：展开态单独一张背景图（panel_bg_open.png，没有就沿用折叠图）
            try
            {
                if (System.IO.File.Exists(po))
                {
                    var mo = System.IO.File.GetLastWriteTimeUtc(po);
                    if (_bgTexOpen == null || mo != _bgOpenMtime)
                    {
                        var b2 = System.IO.File.ReadAllBytes(po);
                        if (b2 != null && b2.Length > 16)
                        {
                            var t2 = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                            if (UnityEngine.ImageConversion.LoadImage(t2, b2))
                            {
                                t2.hideFlags = HideFlags.HideAndDontSave;
                                var old2 = _bgTexOpen;
                                _bgTexOpen = t2;
                                _bgOpenMtime = mo;
                                if (old2 != null) { try { UnityEngine.Object.Destroy(old2); } catch { } }
                                Log.LogInfo($"[面板HUD] 已加载背景图（展开）：{po}（{t2.width}x{t2.height}）");
                            }
                        }
                    }
                }
            }
            catch { }
        }
        catch (Exception e)
        {
            _extTex = null;
            if ((DateTime.Now - _lastTexErr).TotalSeconds > 60)
            {
                _lastTexErr = DateTime.Now;
                Log.LogWarning($"[面板HUD] 外部背景图读取失败：{e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// ★ 2026-11-12：数值条纹理 —— 玩家把横条素材（白色/浅色）改名 bar_texture.png 放进
    /// `config\panel\`，程序再叠在彩色渐变条上（保留每根条的颜色区分）。
    /// ★ 2026-11-13 主人定：**底框不抠白**（保留素材原本的白色/云纹底，不再转透明）。
    /// </summary>
    private static void EnsureBarTexture()
    {
        try
        {
            var dir = System.IO.Path.Combine(PromptConfig.Dir, "panel");
            var p = System.IO.Path.Combine(dir, "bar_texture.png");
            if (!System.IO.File.Exists(p)) { _barTex = null; return; }
            var mtime = System.IO.File.GetLastWriteTimeUtc(p);
            if (_barTex != null && mtime == _barMtime) return;

            var bytes = System.IO.File.ReadAllBytes(p);
            if (bytes == null || bytes.Length < 16) { _barTex = null; return; }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (UnityEngine.ImageConversion.LoadImage(tex, bytes))
            {
                tex.hideFlags = HideFlags.HideAndDontSave;   // ★ 防 GC 回收
                // ★ 2026-11-14 主人要求：把素材的白色/浅色纸底抠成透明（只留深墨收边与云纹线条）
                MakeBarBgTransparent(tex);
                var old = _barTex;
                _barTex = tex;
                _barMtime = mtime;
                if (old != null) { try { UnityEngine.Object.Destroy(old); } catch { } }
                Log.LogInfo($"[面板HUD] 已加载数值条纹理：{p}（{tex.width}x{tex.height}）");
            }
            else
            {
                _barTex = null;
                if (!_barLogged) { _barLogged = true; Log.LogWarning($"[面板HUD] 数值条纹理加载失败：{p}"); }
            }
        }
        catch (Exception e)
        {
            _barTex = null;
            if ((DateTime.Now - _lastTexErr).TotalSeconds > 60)
            {
                _lastTexErr = DateTime.Now;
                Log.LogWarning($"[面板HUD] 数值条纹理读取失败：{e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>★ 2026-11-14 主人要求：数值条素材的白色/浅色纸底抠成透明，只保留深墨收边与云纹线条。</summary>
    private static void MakeBarBgTransparent(Texture2D tex)
    {
        try
        {
            var px = tex.GetPixels32();
            if (px.Length == 0) return;
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                int lum = (c.r + c.g + c.b) / 3;
                if (lum >= 226)
                {
                    px[i] = new Color32(c.r, c.g, c.b, 0);          // 纸底 → 全透明
                }
                else if (lum >= 205)
                {
                    int a = (int)((226 - lum) * 255f / 21f);         // 云纹过渡 → 半透明
                    if (a < 40) a = 40;
                    if (a > 255) a = 255;
                    px[i] = new Color32(c.r, c.g, c.b, (byte)a);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
        }
        catch { }
    }

    /// <summary>取中央 50% 区域平均亮度（0~255），判断是不是浅色皮肤</summary>
    private static bool IsLightTexture(Texture2D tex, out int lum)
    {
        lum = 128;
        try
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            int x0 = w / 4, x1 = w - x0, y0 = h / 4, y1 = h - y0;
            long sum = 0; int cnt = 0;
            for (int y = y0; y < y1; y += 2)
                for (int x = x0; x < x1; x += 2)
                {
                    var c = px[y * w + x];
                    sum += (c.r + c.g + c.b) / 3;
                    cnt++;
                }
            if (cnt > 0) lum = (int)(sum / cnt);
        }
        catch { }
        return lum >= 170;
    }

    /// <summary>★ 2026-11-14：从纹理里裁一块（Unity 纹理 y 原点在左下）——用于背景九宫格三段。</summary>
    private static Texture2D CropTexture(Texture2D src, int x, int y, int w, int h)
    {
        try
        {
            w = Mathf.Clamp(w, 1, src.width);
            h = Mathf.Clamp(h, 1, src.height);
            x = Mathf.Clamp(x, 0, src.width - w);
            y = Mathf.Clamp(y, 0, src.height - h);
            var px = src.GetPixels(x, y, w, h);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
        catch { return null; }
    }

    /// <summary>白底素材自动转透明（只抠纯白，胶囊内部浅色保留）；已带透明像素的图不再二次处理</summary>
    private static void MakeLightTransparent(Texture2D tex)
    {
        try
        {
            var px = tex.GetPixels32();
            if (px.Length == 0) return;

            // ★ 已经抠过的图（含透明像素）直接跳过，避免把胶囊内部也抠掉
            for (int i = 0; i < px.Length; i += 37)
                if (px[i].a < 200) return;

            long sum = 0;
            for (int i = 0; i < px.Length; i++) sum += (px[i].r + px[i].g + px[i].b) / 3;
            if (sum / px.Length < 200) return;   // 不是白底图 → 原样

            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                int lum = (c.r + c.g + c.b) / 3;
                if (lum > 250) { px[i] = new Color32(255, 255, 255, 0); continue; }
                if (lum > 236) { px[i] = new Color32(c.r, c.g, c.b, (byte)(255 * (250 - lum) / 14)); continue; }
                px[i] = new Color32(c.r, c.g, c.b, 255);
            }
            tex.SetPixels32(px);
            tex.Apply();
        }
        catch { }
    }

    /// <summary>按当前面板尺寸生成一张「回纹边角 + 斜向暗纹」的绿色底纹（缓存；失败则退回纯色）</summary>
    private static void EnsurePanelTexture()
    {
        try
        {
            // 有外部背景图就不用程序纹理（省内存）
            if (_extTex != null)
            {
                if (_panelTex != null) { try { UnityEngine.Object.Destroy(_panelTex); } catch { } _panelTex = null; }
                return;
            }
            int w = Mathf.RoundToInt(_rect.width);
            int h = Mathf.RoundToInt(_rect.height);
            if (_panelTex != null && w == _texW && h == _texH) return;
            if (w < 8 || h < 8) return;

            var old = _panelTex;
            _panelTex = BuildPanelTexture(w, h);
            _texW = w; _texH = h;
            if (old != null) { try { UnityEngine.Object.Destroy(old); } catch { } }
        }
        catch (Exception e)
        {
            _panelTex = null;
            if ((DateTime.Now - _lastTexErr).TotalSeconds > 60)
            {
                _lastTexErr = DateTime.Now;
                Log.LogWarning($"[面板HUD] 纹理生成失败（退回纯色）：{e.GetType().Name}: {e.Message}");
            }
        }
    }
    private static DateTime _lastTexErr = DateTime.MinValue;

    private static Texture2D BuildPanelTexture(int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color32[w * h];

        var baseC = new Color32(7, 16, 14, 232);      // 深青黑底
        var stripe = new Color32(9, 21, 18, 232);     // 斜向暗纹（更亮一点点）
        var line = new Color32(96, 178, 140, 255);    // 亮绿线
        var dim = new Color32(44, 92, 74, 255);       // 暗绿线

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[y * w + x] = ((x + y) % 8 == 0) ? stripe : baseC;
            }
        }

        // 外框 2px 亮线
        for (int x = 0; x < w; x++)
        {
            px[x] = line; px[w + x] = line;
            px[(h - 1) * w + x] = line; px[(h - 2) * w + x] = line;
        }
        for (int y = 0; y < h; y++)
        {
            px[y * w] = line; px[y * w + 1] = line;
            px[y * w + w - 1] = line; px[y * w + w - 2] = line;
        }

        // 内框 1px 暗线（缩进 4）
        int i = 4;
        for (int x = i; x < w - i; x++)
        {
            px[i * w + x] = dim; px[(h - i - 1) * w + x] = dim;
        }
        for (int y = i; y < h - i; y++)
        {
            px[y * w + i] = dim; px[y * w + w - i - 1] = dim;
        }

        // 四角回纹（每角 12×12 的「回」字框 + 内小点）
        for (int c = 0; c < 4; c++)
        {
            bool right = (c & 1) != 0;
            bool bottom = (c & 2) != 0;
            int bx = right ? w - 14 : 3;
            int by = bottom ? h - 14 : 3;
            // 回字外框
            for (int k = 0; k < 10; k++)
            {
                px[(by + 0) * w + bx + k] = line;
                px[(by + 9) * w + bx + k] = line;
                px[(by + k) * w + bx] = line;
                px[(by + k) * w + bx + 9] = line;
            }
            // 内框
            for (int k = 2; k < 8; k++)
            {
                px[(by + 2) * w + bx + k] = dim;
                px[(by + 7) * w + bx + k] = dim;
                px[(by + k) * w + bx + 2] = dim;
                px[(by + k) * w + bx + 7] = dim;
            }
            // 中心点
            px[(by + 4) * w + bx + 4] = line;
            px[(by + 5) * w + bx + 5] = line;
        }

        // 顶部标题区：一条淡绿横带（高 22）
        int band = Math.Min(h, 22);
        for (int y = 0; y < band; y++)
            for (int x = 0; x < w; x++)
            {
                float a = (1f - y / (float)band) * 0.5f;
                var c0 = px[y * w + x];
                px[y * w + x] = new Color32(
                    (byte)(c0.r * (1 - a) + 30 * a),
                    (byte)(c0.g * (1 - a) + 60 * a),
                    (byte)(c0.b * (1 - a) + 48 * a),
                    232);
            }

        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    // ==================== 字体 / 样式 ====================

    private static void EnsureStyle()
    {
        // ★ 面板内文字再小一号：12 × scale；按钮用选项框字号 15 × scale（主人要求对齐）
        int fs = Mathf.RoundToInt(12f * ScaleOf());
        int fsToggle = Mathf.RoundToInt(15f * BaseScale());   // 按钮字号跟选项框一致（不缩 2/3）
        int key = fs * 10 + (_lightSkin ? 1 : 0);
        if (_label != null && key == _styleKey && _font != null) return;
        _styleKey = key;
        _fs = fs;
        // ★ 2026-11-14：浅色（粉纱）背景用【粗体字体】，薄纱/花枝区域文字也能看清
        {
            var f = _lightSkin ? TryLoadFontBold(fs) : TryLoadFont(fs);
            if (f != null) _font = f;
        }

        _label = new GUIStyle(GUI.skin.label) { fontSize = fs };
        _labelDim = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(9, fs - 1) };
        _btn = new GUIStyle(GUI.skin.label) { fontSize = fs, alignment = TextAnchor.MiddleCenter };
        _btnSmall = new GUIStyle(GUI.skin.label) { fontSize = fs, alignment = TextAnchor.MiddleCenter };
        _btnPlain = new GUIStyle(GUI.skin.label) { fontSize = fs, alignment = TextAnchor.MiddleCenter };
        _btnDark = new GUIStyle(GUI.skin.label) { fontSize = fs, alignment = TextAnchor.MiddleCenter };
        // ★ 2026-11-13：数值旁 delta 箭头（↑↓─）
        // ★ 2026-11-14：变动箭头字号加大一档（配合双绘加粗，更醒目）
        _arrUp = new GUIStyle(GUI.skin.label) { fontSize = fs + 1, alignment = TextAnchor.MiddleCenter };
        _arrDown = new GUIStyle(GUI.skin.label) { fontSize = fs + 1, alignment = TextAnchor.MiddleCenter };
        _arrFlat = new GUIStyle(GUI.skin.label) { fontSize = fs + 1, alignment = TextAnchor.MiddleCenter };
        _toggle = new GUIStyle(GUI.skin.label) { fontSize = fsToggle, alignment = TextAnchor.MiddleCenter };
        _title = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(12, fs + 4) };
        // ★ 2026-11-14：顶部行字号加大一档（模板 / 套用 / 锁定）
        _btnTop = new GUIStyle(GUI.skin.label) { fontSize = fs + 1, alignment = TextAnchor.MiddleCenter };

        Color main, dim, gold, btnText;
        if (_lightSkin)
        {
            // ★ 2026-11-14：粉色纱背景 → 纯黑文字（主人明确：顶上/底下薄纱处的字要纯黑才能看清）
            main = new Color(0.00f, 0.00f, 0.00f, 1.0f);
            dim = new Color(0.03f, 0.03f, 0.03f, 0.98f);
            gold = new Color(0.05f, 0.03f, 0.01f, 1.0f);
            btnText = new Color(0.00f, 0.00f, 0.00f, 1.0f);
        }
        else
        {
            main = new Color(0.95f, 0.95f, 0.95f, 0.95f);
            dim = new Color(0.74f, 0.76f, 0.74f, 0.92f);
            gold = new Color(0.94f, 0.84f, 0.56f, 0.97f);
            btnText = new Color(0.86f, 0.96f, 0.92f, 0.96f);
        }

        _label.normal.textColor = main;
        _title.normal.textColor = gold;
        _labelDim.normal.textColor = dim;
        _btn.normal.textColor = main;
        _btnSmall.normal.textColor = main;
        _btnDark.normal.textColor = new Color(0.00f, 0.00f, 0.00f, 1.0f);   // 浅底列表用纯黑字
        _btnPlain.normal.textColor = btnText;
        _toggle.normal.textColor = new Color(0.90f, 0.95f, 0.92f, 0.95f);   // 按钮跟选项框一致：深底浅字
        _btnTop.normal.textColor = main;

        if (_font != null)
        {
            _label.font = _font; _labelDim.font = _font; _btn.font = _font; _btnDark.font = _font;
            _btnSmall.font = _font; _btnPlain.font = _font; _title.font = _font; _toggle.font = _font;
            _btnTop.font = _font;
            _arrUp.font = _font; _arrDown.font = _font; _arrFlat.font = _font;
        }

        // ★ 2026-11-13：本轮 delta 箭头配色（浅底深色 / 深底亮色）
        if (_lightSkin)
        {
            _arrUp.normal.textColor = new Color(0.08f, 0.52f, 0.24f, 1f);
            _arrDown.normal.textColor = new Color(0.74f, 0.18f, 0.16f, 1f);
            _arrFlat.normal.textColor = new Color(0.45f, 0.50f, 0.47f, 0.90f);
        }
        else
        {
            _arrUp.normal.textColor = new Color(0.45f, 0.95f, 0.55f, 1f);
            _arrDown.normal.textColor = new Color(1f, 0.45f, 0.40f, 1f);
            _arrFlat.normal.textColor = new Color(0.65f, 0.68f, 0.66f, 0.90f);
        }
    }

    /// <summary>★ 2026-11-14：浅色皮肤（粉纱背景）用粗体字体——薄纱/花枝区域文字也清楚。</summary>
    private static Font TryLoadFontBold(int fs)
    {
        try
        {
            var f = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei Bold", "Microsoft YaHei UI Bold", "SimHei", "黑体", "Arial Bold", "Microsoft YaHei" }, fs);
            if (f != null) return f;
        }
        catch { }
        return TryLoadFont(fs);
    }

    private static Font TryLoadFont(int fs)
    {
        try
        {
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            if (fonts != null && fonts.Length > 0)
            {
                string[] prefer = { "FZ", "STKAI", "HWMZT", "msyh", "MSYH", "SimSun", "宋体" };
                foreach (var key in prefer)
                    foreach (var f in fonts)
                    {
                        if (f == null || string.IsNullOrEmpty(f.name)) continue;
                        if (f.name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return f;
                    }
                foreach (var f in fonts)
                {
                    if (f == null || string.IsNullOrEmpty(f.name)) continue;
                    if (f.name == "LegacyRuntime") continue;
                    if (f.name.StartsWith("Arial", StringComparison.OrdinalIgnoreCase)) continue;
                    return f;
                }
            }
        }
        catch { }
        try
        {
            var f = Font.CreateDynamicFontFromOSFont(
                new[] { "SimSun", "宋体", "NSimSun", "KaiTi", "Microsoft YaHei", "Arial" }, fs);
            if (f != null) return f;
        }
        catch { }
        return null;
    }
}

// ==================== 场景 / 对话关闭 → 隐藏面板 ====================

[HarmonyLib.HarmonyPatch(typeof(Game.SpaceManager), "BackToParentSpace")]
internal static class PanelHideSpacePatch
{
    [HarmonyLib.HarmonyPostfix]
    internal static void Postfix() { try { PanelHud.HideFromScene(); } catch { } }
}

[HarmonyLib.HarmonyPatch(typeof(Game.UI.UPFLogic.NpcBedroom.NpcBedroomOverlay), "OnHide")]
internal static class PanelHideBedroomHidePatch
{
    [HarmonyLib.HarmonyPrefix]
    internal static void Prefix() { try { PanelHud.HideFromScene(); } catch { } }
}

[HarmonyLib.HarmonyPatch(typeof(Game.UI.UPFLogic.NpcBedroom.NpcBedroomOverlay), "OnDisable")]
internal static class PanelHideBedroomDisablePatch
{
    [HarmonyLib.HarmonyPrefix]
    internal static void Prefix() { try { PanelHud.HideFromScene(); } catch { } }
}

[HarmonyLib.HarmonyPatch(typeof(Game.NpcDialoguePanel), "OnHide")]
internal static class PanelHideDialoguePatch
{
    [HarmonyLib.HarmonyPrefix]
    internal static void Prefix() { try { PanelHud.HideFromScene(); } catch { } }
}

[HarmonyLib.HarmonyPatch(typeof(Game.NpcDialoguePanel), "PlayCloseAnimThenHide")]
internal static class PanelHideDialogueAnimPatch
{
    [HarmonyLib.HarmonyPrefix]
    internal static void Prefix() { try { PanelHud.HideFromScene(); } catch { } }
}

/// <summary>兜底：任何对话类面板被销毁时也隐藏</summary>
[HarmonyLib.HarmonyPatch(typeof(Game.UIManager), "OnUIDestroy")]
internal static class PanelHideUIPatch
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
            if (tn.IndexOf("Dialogue", StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("Chat", StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("Bedroom", StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0)
                PanelHud.HideFromScene();
        }
        catch { }
    }
}
