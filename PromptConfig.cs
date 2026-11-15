using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuWenFanChen;

/// <summary>
/// 提示词配置化（阶段 4 核心）。
///
/// 目录： plugins\BuWenFanChen\config\
///   ├─ inject.txt          ← 档位 1 的注入内容（首次自动写出内置默认）
///   ├─ config.json         ← { "tier": 1 }
///   ├─ tier2-template.md   ← 给玩家拿去问本地模型的模板
///   └─ tier2-inject.txt    ← 玩家生成后放这里（tier=2 时优先用）
///
/// 规则：
///   · 首次启动自动写出上面 3 个文件（玩家拿到就能改）
///   · tier=1 → inject.txt；tier=2 且有 tier2-inject.txt → 用它
///   · 任何文件缺失/损坏/为空 → 回退内置默认，游戏照常
///   · 改了文件要重启游戏生效（暂不做热重载）
/// </summary>
public static class PromptConfig
{
    private const string Src = "不问凡尘·配置";

    /// <summary>配置目录</summary>
    public static string Dir
    {
        get
        {
            var pluginDir = System.IO.Path.GetDirectoryName(typeof(PromptConfig).Assembly.Location) ?? ".";
            return System.IO.Path.Combine(pluginDir, "config");
        }
    }

    public static string InjectPath => System.IO.Path.Combine(Dir, "inject.txt");
    public static string ConfigPath => System.IO.Path.Combine(Dir, "config.json");
    public static string Tier2Path => System.IO.Path.Combine(Dir, "tier2-inject.txt");
    public static string TemplatePath => System.IO.Path.Combine(Dir, "tier2-template.md");

    /// <summary>当前生效的档位（1 或 2）</summary>
    public static int Tier { get; private set; } = 1;

    /// <summary>当前生效的注入内容</summary>
    public static string Inject { get; private set; } = "";

    /// <summary>是否已加载（避免重复写文件）</summary>
    public static bool Loaded { get; private set; }

    /// <summary>
    /// 是否写 dump 调试文件（默认 false）。
    /// 2026-10-24 改：原来每轮都写、永不删除，
    /// prompt_*.txt 里是完整提示词 + 玩家这一轮说的话（明文），
    /// 长期玩家目录会堆几千个文件且有隐私风险。
    /// 默认关闭；排查问题时在 config.json 里改 true。
    /// </summary>
    public static bool DumpEnabled { get; private set; } = false;

    /// <summary>dump 开启时保留多少个文件（超出删最旧），默认 200。</summary>
    public static int DumpKeep { get; private set; } = 200;

    /// <summary>
    /// JSON 死命令的【玩家覆盖值】。
    ///   null  = 没设置 → 自动（装了语音 MOD 就开，没装就关）
    ///   true  = 强制开
    ///   false = 强制关（做对照实验用）
    /// 真正的生效判断在 JsonIntensifyActive，它把自动和覆盖合起来算一次。
    /// </summary>
    public static bool? JsonIntensifyOverride { get; private set; }

    /// <summary>
    /// 死命令最终是否生效。自动模式下取决于有没有语音 MOD。
    /// </summary>
    public static bool JsonIntensifyActive =>
        JsonIntensifyOverride ?? AudioModPresent();

    /// <summary>语音 MOD 是否存在（缓存）。检测失败保守认为存在。</summary>
    private static bool? _audioModCache;
    private static bool AudioModPresent()
    {
        if (_audioModCache.HasValue) return _audioModCache.Value;
        try { _audioModCache = AddNpcChatMessagePatch.TtsPresent(); }
        catch { _audioModCache = true; }
        return _audioModCache.Value;
    }

    /// <summary>死命令当前状态的可读描述（日志/控制台用）。</summary>
    public static string JsonIntensifyDesc =>
        JsonIntensifyOverride == null
            ? (JsonIntensifyActive ? "自动→开（检测到语音 MOD）" : "自动→关（未检测到语音 MOD）")
            : (JsonIntensifyOverride.Value ? "强制开" : "强制关");

    /// <summary>
    /// 死命令示例开关（默认开）：在【系统死命令】后面追加一条**完整可抄**的 a1ttsv2 示例。
    /// 依据：2026-11-02 离线 AB（各 12 次）——a1ttsv2 合规 6/12（50%）→ 9/12（75%）。
    /// 关掉 = 只留现在的结构描述（老行为），做对照实验用。
    /// </summary>
    public static bool DeadCommandExample { get; private set; } = true;

    /// <summary>
    /// 四选一对话选项开关（2026-11-03 实验，默认开）。
    /// 开：要求模型在 JSON 顶层多给 "opts": [4 条玩家可说的话]，
    ///     输出侧由 OptionsProbe 摘掉（不进 content / a1ttsv2 / 历史），只记日志 + 落盘。
    /// 关：完全回到老行为（prompt 里不出现选项要求）。
    /// 用途：先验证「模型能不能稳定给 4 条好选项」，稳了再做弹窗面板。
    /// </summary>
    public static bool OptionsEnabled { get; private set; } = true;

    /// <summary>
    /// ★ 2026-11-04：自动好感度走「主请求」还是「独立请求」。
    /// true（默认）= 模型在主回复 JSON 顶层给 "intimacy":{"delta":N,"reason":"…"}，
    ///              我们从原始输出解析，省掉每轮那次 1200 输入 token 的判断请求。
    /// false      = 回到原来的独立请求（IntimacyJudge.Judge）。
    /// 两个开关配合：autoIntimacyEnabled 管「要不要加减」，本开关管「怎么判断」。
    /// </summary>
    public static bool IntimacyInMainRequest { get; private set; } = true;

    /// <summary>
    /// 是否启用「从正文补造 a1ttsv2」的兜底（默认 true）。
    /// 关掉之后：模型没吐 a1ttsv2 时不做任何事，交给 TTS 自己的 recovery。
    /// 用途：验证「模型自己给的台本能否独立工作」，判断补写是帮忙还是干扰。
    /// </summary>
    // ★ 2026-10-24 18:40：默认改成 false。
    //   原因（本鲸实测 + Havoc 指出）：我们在 AddNpcChatMessage 看到的 raw 里
    //   a1ttsv2 一定已经被游戏清洗掉，所以「本捕获点 absent」永远成立 →
    //   补写保护（Valid 时不补）永远不触发 → 每次补写都在往游戏回复里塞一份
    //   「合法但劣质」的台本，把语音 MOD 自己抓到的模型原版（带模型写的 tag）顶掉。
    //   只要模型吐出率 > 50%，补写的期望收益就是负的。
    /// <summary>
    /// 回复字数上限（玩家可在 config.json 里改）。
    /// 三个数字都会被自己生成的【字数】规则段和输出侧裁段逻辑使用。
    /// </summary>
    /// <summary>
    /// ★ 2026-11-12 主人定：AI 回复语言（zh-CN 简中 / zh-TW 繁中 / en 英文）。
    /// 只改「模型输出的文字语言」：游戏 UI、输入框、语音 MOD 一概不动。
    /// 实现：tail 最末尾追加一段语言锁定（见 CompleteBudgetedPatch.BuildOutputLanguageBlock）。
    /// 实测（2026-11-12，真实请求 req_0007）：加在 tail 末尾，4 轮中文输入也拉不回去；
    /// 缓存只多 ~163 token 未命中。默认 zh-CN = 完全不注入，行为零变化。
    /// </summary>
    public static string OutputLanguage { get; private set; } = "zh-CN";

    /// <summary>NSFW 板块注入模式：off / auto（默认，关键词触发）/ always</summary>
    public static string NsfwMode { get; private set; } = "auto";

    // ===== ★ 2026-11-12：NPC 数值面板（1.4.0）配置（施工图 §8） =====

    /// <summary>面板总开关（默认 false = 完全不影响行为，tail 与 1.3.5 一致）</summary>
    public static bool PanelEnabled { get; private set; } = true;   // ★ 2026-11-14 主人要求：性格面板默认开启

    /// <summary>true = 判定搭主请求 "p" 字段（零额外调用）；false = 独立调用（预留）</summary>
    public static bool PanelInMainRequest { get; private set; } = true;

    /// <summary>NSFW 四项是否启用</summary>
    public static bool PanelNsfw { get; private set; } = true;

    /// <summary>四轴单轮上限（1~2）</summary>
    public static int PanelStepMax { get; private set; } = 1;

    /// <summary>强烈事件上限</summary>
    public static int PanelStrongStep { get; private set; } = 2;

    /// <summary>NSFW 单轮上限</summary>
    public static int PanelNsfwStepMax { get; private set; } = 2;

    /// <summary>死区半径（范围内不注入状态句）</summary>
    public static int PanelDeadZone { get; private set; } = 7;

    /// <summary>同一轴连续变化多少轮触发冷却</summary>
    public static int PanelCooldownStreak { get; private set; } = 3;

    /// <summary>冷却停多少轮</summary>
    public static int PanelCooldownLatch { get; private set; } = 2;

    /// <summary>四轴衰减周期（按总轮数），0 = 不衰减</summary>
    public static int PanelDecayEvery { get; private set; } = 8;

    /// <summary>NSFW 四项按「空闲轮数」衰减（§3.2.1 铁规一）；false = NSFW 永不衰减</summary>
    public static bool PanelNsfwDecayIdle { get; private set; } = true;

    /// <summary>点「按角色卡重新推断」前二次确认（预留 P1）</summary>
    public static bool PanelInitConfirm { get; private set; } = true;

    /// <summary>growth 养成模式（面板 &gt; NSFW 模块）/ allopen 我全都要（NSFW 模块 &gt; 面板）</summary>
    public static string PanelNsfwMode { get; private set; } = "growth";

    /// <summary>控制台「手动改数值（作弊）」折叠区默认展开（预留 P1）</summary>
    public static bool PanelCheatEdit { get; private set; } = false;

    /// <summary>状态块字数硬上限</summary>
    public static int PanelInjectMaxChars { get; private set; } = 120;

    /// <summary>玩法授权按开放度档位渐进（默认关 = 保持一刀切全开；P2 才用）</summary>
    public static bool NsfwPanelGating { get; private set; } = false;

    /// <summary>面板初始位置（屏幕比例，P1 用）</summary>
    public static double PanelBtnX { get; private set; } = 0.66;
    public static double PanelBtnY { get; private set; } = 0.80;

    /// <summary>★ 2026-11-13：性格面板缩放（0.5~1.5，100% = 原始大小；玩家拖动后持久化到 config.json）</summary>
    public static float PanelScale { get; private set; } = 1.0f;

    /// <summary>★ 2026-11-13 主人要：选项框拖动偏移（相对自动定位位置；拖动后持久化，重启保留）</summary>
    public static float OptionsOffsetX { get; private set; } = 0f;
    public static float OptionsOffsetY { get; private set; } = 0f;

    /// <summary>★ 2026-11-13：允许男性角色也使用性格面板（默认关；关时男性 NPC 不显示面板）</summary>
    public static bool PanelForMale { get; private set; } = false;

    /// <summary>命中后持续注入的轮数</summary>
    public static int NsfwLatch { get; private set; } = 5;

    /// <summary>回复总字数下限（提示词建议，非硬约束；0 = 不限）</summary>
    public static int ReplyLenLo { get; private set; } = 0;

    /// <summary>
    /// 后台抽取间隔（每 N 轮抽一次记忆）。
    /// 2026-10-25 主人定：48 → 30。
    /// 依据：玩家一小时大约和同一个 NPC 聊 40 轮，30 轮 ≈ 一小时抽一次，合理。
    /// </summary>
    public static int ExtractInterval { get; private set; } = 30;

    /// <summary>
    /// ★ 2026-11-07 主人定：canon 保留轮数（档位）。
    ///
    /// 影响：保留越多 → prompt 越长 → 模型越容易忘格式（实测长度与失败率强正相关）、
    ///       命中率/成本也越差；保留越少 → 原文窗口窄，但记忆本体（facts/摘要/骨架）不受影响。
    /// 实际行为：归档触发点 = CanonKeepRounds + 10（ArchiveEvery），裁到 CanonKeepRounds。
    ///   10 → 波动 10~20 轮（平均 15）
    ///   15 → 波动 15~25 轮（平均 20）
    ///   20 → 波动 20~30 轮（平均 25，旧默认）
    /// 默认 10：配合 prompt 缩短，优先降失败率和成本。
    /// </summary>
    public static int CanonKeepRounds { get; private set; } = 10;

    /// <summary>
    /// ★ 2026-11-07 主人定：归档/合并轮次（canon 涨到 Keep+ArchiveEvery 时触发一次，
    /// 一次裁掉 ArchiveEvery 轮，同时把裁掉的轮次异步合并进长期记忆）。
    ///
    /// 与 CanonKeepRounds 一起构成四档（上限 = 保留 + 合并 - 1，始终大于保留轮数，不会退化成滑动窗口）：
    ///   MAX  = 8/4  → 波动 8~11 轮，体验最好、合并最勤、缓存断点最多
    ///   High = 10/6 → 波动 10~15 轮（默认）
    ///   Low  = 15/8 → 波动 15~22 轮
    ///   Off  = 20/10→ 波动 20~29 轮，最省、格式失败率最高
    ///
    /// 实测依据（576 次重放）：格式失败率由 prompt 长度主导，与档位无独立关系。
    /// 约束：必须 ≥ 2 —— 再小就退化成「每轮都裁」的滑动窗口，缓存会崩。
    /// </summary>
    public static int CanonArchiveEvery { get; private set; } = 6;

    /// <summary>
    /// ★ 2026-11-07 主人定：**记忆合并节拍**（每 N 轮把积压的归档内容一次性整合进长期记忆）。
    /// 与「裁剪节拍（CanonArchiveEvery）」解耦：
    ///   · 裁剪可以勤 → canon 窗口始终短 → 遵循率高、峰值低（但每次裁剪会断一次缓存，从 history 开头）
    ///   · 合并可以稀 → LLM 调用少 + 记忆区（s0 里的长期记忆段）断点少（记忆更新断在 s0，比裁剪更靠前、更贵）
    /// 四档：MAX 8 / High 24 / Low 36 / Off 48 —— 每 1~5 次裁剪才合并一次，
    /// 合并时把 archive_pending.jsonl 里积压的全部内容压成多代总述（每 4~6 轮一代）。
    /// MAX 档的定位是「记忆最新鲜」（8 轮就合并一次），不是「失败率最低」——
    /// 实测加了 14 字格式指令后，High/Low 的掉 JSON 率（2.1%/3.1%）并不比 MAX（3.1%）差。
    /// </summary>
    public static int CanonMergeEvery { get; private set; } = 24;

    // ============ ★ 2026-11-07：玩家主角设定（覆盖/新增游戏原文里的「## 当前场景 → 对话对象」）============
    // 空字符串 = 不覆盖，用游戏原文。填了就替换对应项。
    // 「对话对象」在 context（messages[1]）里，注入走 CompleteBudgetedPatch.InjectPlayerProfile。
    public static string PlayerName { get; private set; } = "";
    public static string PlayerGender { get; private set; } = "";
    public static string PlayerRealm { get; private set; } = "";
    public static string PlayerSect { get; private set; } = "";

    /// <summary>
    /// 自定义条目（原文没有的键）。一行一条，格式 `键：值`。
    /// 例：`灵根：火单` / `出身：山下药铺的儿子`。同名键会覆盖原文，新键追加到列表末尾。
    /// </summary>
    public static string PlayerExtra { get; private set; } = "";

    /// <summary>代演在场角色（阶段 5 功能，字段先留）</summary>
    public static bool AllowNpcActing { get; private set; } = false;

    /// <summary>角色卡总开关（阶段 5 功能，字段先留）</summary>
    public static bool CardEnabled { get; private set; } = true;

    /// <summary>
    /// ★ 2026-10-25 主人定：用角色卡取代游戏官方角色设定。
    ///   开启后：① 注入角色卡「身份/性格/善恶立场/外貌穿着」块
    ///           ② REPLACE 规则删掉游戏的「## 角色属性 / ## 角色设定 / ## 角色背景」段
    ///   省约 2300 字 / 轮。
    /// ★ 2026-10-26 主人定：**默认常开**（新装/缺字段的玩家默认就是开）。
    ///   保护：卡里「姓名 + 身份」没填时，这一轮仍保留游戏原文（见 ApplyReplace），
    ///   所以新角色还没建卡也不会把角色搞崩。
    /// </summary>
    public static bool ReplaceOfficialProfile { get; private set; } = true;

    /// <summary>自动好感度总开关（2026-10-27 测试功能，默认关）。</summary>
    public static bool AutoIntimacyEnabled { get; private set; } = false;

    /// <summary>正面最多加多少（玩家可改，默认 5）。</summary>
    public static int AutoIntimacyMaxUp { get; private set; } = 5;

    /// <summary>负面最多扣多少（玩家可改，默认 3，按正数存）。</summary>
    public static int AutoIntimacyMaxDown { get; private set; } = 3;

    /// <summary>加分行为（玩家可改词块，默认写好的那串）。</summary>
    public static string AutoIntimacyPositive { get; private set; } = "真诚关心、体贴、说到她心里、共同经历、送她喜欢的东西";

    /// <summary>扣分行为（玩家可改词块）。</summary>
    public static string AutoIntimacyNegative { get; private set; } = "冒犯、轻慢、欺骗、触到她的底线、无视她的感受";

    /// <summary>
    /// 专门给好感判断用的 API 地址（留空 = 沿用主对话 API）。
    /// 填 Ollama 就能本地跑：http://localhost:11434/v1/chat/completions
    /// </summary>
    public static string AutoIntimacyApiUrl { get; private set; } = "";

    /// <summary>好感判断用的模型名（留空 = qwen2.5:0.5b）。</summary>
    public static string AutoIntimacyModel { get; private set; } = "";

    /// <summary>好感判断用的 API 密钥（本地模型留空即可；填云 API 时要真 key）。</summary>
    public static string AutoIntimacyApiKey { get; private set; } = "";

    /// <summary>生效方式：async（迟一轮）/ sync（当前轮，退出前最后一轮不生效）。</summary>
    public static string AutoIntimacyMode { get; private set; } = "async";

    /// <summary>
    /// 生成发给判定模型的好感规则（2026-10-27 主人要求：玩家只改数字，不给整段文本框）。
    /// 数字来自 AutoIntimacyMaxUp / AutoIntimacyMaxDown。
    /// </summary>
    public static string BuildAutoIntimacyPrompt()
    {
        var up = Math.Clamp(AutoIntimacyMaxUp, 1, 20);
        var down = Math.Clamp(AutoIntimacyMaxDown, 1, 20);
        var pos = string.IsNullOrWhiteSpace(AutoIntimacyPositive) ? "真诚关心、体贴" : AutoIntimacyPositive.Trim();
        var neg = string.IsNullOrWhiteSpace(AutoIntimacyNegative) ? "冒犯、轻慢、欺骗" : AutoIntimacyNegative.Trim();
        return "你是角色扮演里的好感判定器。根据下面这一轮对话，判断玩家这轮的言行对角色好感的影响，只输出一个整数（-" + down + " 到 +" + up + "）：\n"
            + "+1 ~ +" + up + "：" + pos + "\n"
            + "   0   ：普通日常、闲聊、无关痛痒\n"
            + "-1 ~ -" + down + "：" + neg + "\n"
            + "只输出数字，不要解释、不要别的字。";
    }

    /// <summary>长期记忆注入总开关（关 = 不注入记忆，但仍照常抽取写入）</summary>
    public static bool MemoryEnabled { get; private set; } = true;

    /// <summary>全局世界书注入总开关（关 = 不注入世界书条目）</summary>
    public static bool WorldbookEnabled { get; private set; } = true;

    /// <summary>角色世界书注入总开关（关 = 不注入角色世界书条目）</summary>
    public static bool LorebookEnabled { get; private set; } = true;

    /// <summary>NSFW 板块注入总开关（关 = 不注入 NSFW 板块，即使命中关键词）</summary>
    public static bool NsfwEnabled { get; private set; } = true;

    /// <summary>
    /// ★ 2026-11-09：**过审模式**——隐藏所有 NSFW 内容（控制台 NSFW 页、角色卡 NSFW 区、提示词 NSFW 注入）。
    ///   教学视频 / 直播录屏用，默认 false，正常玩家完全不受影响。config: hideNsfw
    /// </summary>
    public static bool HideNsfw { get; private set; } = false;

    /// <summary>
    /// 角色卡专用模型（空 = 跟游戏对话用同一个模型）。
    /// 1.1.2 有独立配置；想用更强的模型生成角色卡时填，例如 deepseek-v4-pro。
    /// </summary>
    public static string CardModel { get; private set; } = "";

    /// <summary>
    /// MOD 自己用的 API 地址（空 = 用游戏 settings.json 里的 ainpc 配置）。
    /// ★ 2026-10-25 主人要：MOD 的所有 API 调用（记忆抽取/重评/角色卡生成）单独走，
    ///   不要和游戏对话挤在同一个 key / 同一个模型上。
    /// </summary>
    /// <summary>
    /// 每轮记忆注入字数上限（玩家可调，默认 3000）。
    /// ★ 2026-10-25 主人定：不要「档位」这个 1.1.2 的老概念了，
    ///   换成一个直接填数字的「最大注入」选项。
    ///   这和 RAG 检索条数是两件事：检索按相关性走，不受这个数影响。
    /// </summary>
    public static int MemoryInjectLimit { get; private set; } = 3000;

    /// <summary>摘要字数上限（内部固定值，不暴露给玩家）</summary>
    public static int SummaryCharLimit => 1200;

    public static string ModApiUrl { get; private set; } = "";

    /// <summary>MOD 自己用的 API Key（空 = 用游戏的）</summary>
    public static string ModApiKey { get; private set; } = "";

    /// <summary>MOD 自己用的模型名（空 = 用游戏的）</summary>
    public static string ModModel { get; private set; } = "";

    /// <summary>启动时是否自动打开控制台网页</summary>
    public static bool ConsoleAutoOpen { get; private set; } = true;

    /// <summary>
    /// 用浏览器「应用模式」打开控制台（--app，无地址栏无标签页，看起来像独立窗口）。
    /// 关掉游戏时插件会自动关闭这个窗口。找不到 Chrome/Edge 时自动退回普通浏览器。
    /// </summary>
    public static bool ConsoleAppMode { get; private set; } = true;

    /// <summary>控制台窗口宽度（应用模式 --window-size 用，前端拖完窗口会写回）</summary>
    public static int ConsoleWinW { get; private set; } = 1400;

    /// <summary>控制台窗口高度</summary>
    public static int ConsoleWinH { get; private set; } = 900;

    public static int ReplyLenTotal { get; private set; } = 200;

    /// <summary>旁白合计上限（（）内的字）</summary>
    public static int ReplyLenNarration { get; private set; } = 120;

    /// <summary>台词合计上限（""内的字，语音 MOD 要读的部分）</summary>
    public static int ReplyLenDialogue { get; private set; } = 80;   // 120 + 80 = 200，和总上限对得上

    /// <summary>裁剪硬阈值倍数（超上限 × 这个倍数才真删，平时只警告）</summary>
    public static double ReplyLenHardRatio { get; private set; } = 2.0;

    // ★ 2026-11-07 主人要求：补写语音台本整套功能已删除
    //   （FallbackEnabled / FallbackAlways / ScriptRepairEnabled / FallbackDesc 全部移除）

    /// <summary>加载摘要（给启动日志用）</summary>
    public static string Summary { get; private set; } = "";

    private static void Log(string msg, bool warn = false)
    {
        try
        {
            var l = BepInEx.Logging.Logger.CreateLogSource(Src);
            if (warn) l.LogWarning(msg); else l.LogInfo(msg);
        }
        catch { }
    }

    /// <summary>
    /// 热重载：重新读 config.json（玩家在控制台改完立即生效，不用重启）。
    /// 只重读 PromptConfig 的字段；inject.txt / NSFW 词表各自有各自的加载时机。
    /// </summary>
    public static void Reload()
    {
        try
        {
            Loaded = false;
            Load(_lastBuiltin);
            Log("♻ 配置已热重载（存盘即生效）");
        }
        catch (Exception e)
        {
            Log($"配置热重载失败：{e.GetType().Name}: {e.Message}", true);
        }
    }

    private static string _lastBuiltin = "";

    /// <summary>
    /// 加载配置。builtinDefault = 代码里的 INJECT_V12（兜底用）。
    /// </summary>
    public static void Load(string builtinDefault)
    {
        if (Loaded) return;
        Loaded = true;
        if (!string.IsNullOrEmpty(builtinDefault)) _lastBuiltin = builtinDefault;

        try
        {
            System.IO.Directory.CreateDirectory(Dir);

            // ① config.json 不存在 → 写默认（同时写 inject.txt 和模板）
            if (!System.IO.File.Exists(ConfigPath))
            {
                TryWrite(ConfigPath,
                    "{\n" +
                    "  \"tier\": 1,\n" +
                    "  \"dumpEnabled\": false,\n" +
                    "  \"dumpKeep\": 200,\n" +
                    "  \"_说明\": \"tier=1 用 inject.txt；tier=2 用 tier2-inject.txt。dumpEnabled=true 会把每轮提示词写到 dump 目录（含对话内容，仅排查问题用），dumpKeep 控制保留多少个文件。改完重启游戏生效。\"\n" +
                    "}\n");
                TryWrite(InjectPath, builtinDefault);
                TryWrite(TemplatePath, Tier2Template);
                Log($"✓ 首次运行，已写出默认配置到 {Dir}");
            }
            else
            {
                // 目录在但 inject.txt 不在 → 补写
                if (!System.IO.File.Exists(InjectPath))
                {
                    TryWrite(InjectPath, builtinDefault);
                    Log("✓ inject.txt 不存在，已补写内置默认");
                }
                if (!System.IO.File.Exists(TemplatePath))
                    TryWrite(TemplatePath, Tier2Template);
            }

            // ② 读 tier + dump 配置
            var tier = 1;
            try
            {
                var raw = System.IO.File.ReadAllText(ConfigPath, Encoding.UTF8);
                var node = JsonNode.Parse(raw);
                if (node is JsonObject o)
                {
                    if (o.TryGetPropertyValue("tier", out var t) && t != null)
                        tier = t.GetValue<int>();

                    if (o.TryGetPropertyValue("dumpEnabled", out var de) && de != null)
                    {
                        try { DumpEnabled = de.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("jsonIntensify", out var ji) && ji != null)
                    {
                        try { JsonIntensifyOverride = ji.GetValue<bool>(); } catch { }
                    }
                    // ★ 2026-11-02：死命令示例开关（默认开；config 里 deadCommandExample=false 可关）
                    if (o.TryGetPropertyValue("deadCommandExample", out var dce) && dce != null)
                    {
                        try { DeadCommandExample = dce.GetValue<bool>(); } catch { }
                    }
                    // ★ 2026-11-03：四选一对话选项（实验，默认开；optionsEnabled=false 可关）
                    if (o.TryGetPropertyValue("optionsEnabled", out var oen) && oen != null)
                    {
                        try { OptionsEnabled = oen.GetValue<bool>(); } catch { }
                    }
                    // ★ 2026-11-04：自动好感度走主请求还是独立请求（默认主请求）
                    if (o.TryGetPropertyValue("intimacyInMainRequest", out var imr) && imr != null)
                    {
                        try { IntimacyInMainRequest = imr.GetValue<bool>(); } catch { }
                    }
                    // ★ 字数上限（玩家可改）
                    if (o.TryGetPropertyValue("replyLenTotal", out var rlt) && rlt != null)
                    {
                        try { var v = rlt.GetValue<int>(); if (v > 0) ReplyLenTotal = v; } catch { }
                    }
                    if (o.TryGetPropertyValue("replyLenNarration", out var rln) && rln != null)
                    {
                        try { var v = rln.GetValue<int>(); if (v > 0) ReplyLenNarration = v; } catch { }
                    }
                    if (o.TryGetPropertyValue("replyLenDialogue", out var rld) && rld != null)
                    {
                        try { var v = rld.GetValue<int>(); if (v > 0) ReplyLenDialogue = v; } catch { }
                    }
                    if (o.TryGetPropertyValue("replyLenHardRatio", out var rlh) && rlh != null)
                    {
                        try { var v = rlh.GetValue<double>(); if (v >= 1.0) ReplyLenHardRatio = v; } catch { }
                    }
                    // ★ NSFW 板块
                    if (o.TryGetPropertyValue("nsfwMode", out var nm) && nm != null)
                    {
                        try { var v = nm.GetValue<string>(); if (!string.IsNullOrWhiteSpace(v)) NsfwMode = v; } catch { }
                    }
                    if (o.TryGetPropertyValue("nsfwLatch", out var nl) && nl != null)
                    {
                        try { var v = nl.GetValue<int>(); if (v > 0) NsfwLatch = v; } catch { }
                    }
                    // ★ 2026-11-12 主人定：AI 回复语言（zh-CN / zh-TW / en）
                    if (o.TryGetPropertyValue("outputLanguage", out var ol) && ol != null)
                    {
                        try
                        {
                            var v = (ol.GetValue<string>() ?? "").Trim();
                            if (v == "zh-CN" || v == "zh-TW" || v == "en") OutputLanguage = v;
                            else Log($"[配置] outputLanguage={v} 不认识（只支持 zh-CN / zh-TW / en），用默认 {OutputLanguage}");
                        }
                        catch { }
                    }

                    // ★ 2026-11-12：NPC 数值面板（1.4.0，施工图 §8）——全部容错读取，坏值忽略
                    if (o.TryGetPropertyValue("panelEnabled", out var pen) && pen != null)
                    { try { PanelEnabled = pen.GetValue<bool>(); } catch { } }
                    if (o.TryGetPropertyValue("panelInMainRequest", out var pimr) && pimr != null)
                    { try { PanelInMainRequest = pimr.GetValue<bool>(); } catch { } }
                    if (o.TryGetPropertyValue("panelNsfw", out var pnsw) && pnsw != null)
                    { try { PanelNsfw = pnsw.GetValue<bool>(); } catch { } }
                    if (o.TryGetPropertyValue("panelStepMax", out var psm) && psm != null)
                    { try { var v = psm.GetValue<int>(); if (v >= 1 && v <= 2) PanelStepMax = v; } catch { } }
                    if (o.TryGetPropertyValue("panelStrongStep", out var pss) && pss != null)
                    { try { var v = pss.GetValue<int>(); if (v >= 1 && v <= 2) PanelStrongStep = v; } catch { } }
                    if (o.TryGetPropertyValue("panelNsfwStepMax", out var pnsm) && pnsm != null)
                    { try { var v = pnsm.GetValue<int>(); if (v >= 1 && v <= 2) PanelNsfwStepMax = v; } catch { } }
                    if (o.TryGetPropertyValue("panelDeadZone", out var pdz) && pdz != null)
                    { try { var v = pdz.GetValue<int>(); if (v >= 0 && v <= 30) PanelDeadZone = v; } catch { } }
                    if (o.TryGetPropertyValue("panelCooldownStreak", out var pcs) && pcs != null)
                    { try { var v = pcs.GetValue<int>(); if (v >= 2 && v <= 10) PanelCooldownStreak = v; } catch { } }
                    if (o.TryGetPropertyValue("panelCooldownLatch", out var pcl) && pcl != null)
                    { try { var v = pcl.GetValue<int>(); if (v >= 0 && v <= 10) PanelCooldownLatch = v; } catch { } }
                    if (o.TryGetPropertyValue("panelDecayEvery", out var pde) && pde != null)
                    { try { var v = pde.GetValue<int>(); if (v >= 0 && v <= 100) PanelDecayEvery = v; } catch { } }
                    if (o.TryGetPropertyValue("panelNsfwDecayIdle", out var pndi) && pndi != null)
                    { try { PanelNsfwDecayIdle = pndi.GetValue<bool>(); } catch { } }
                    if (o.TryGetPropertyValue("panelInitConfirm", out var pic) && pic != null)
                    { try { PanelInitConfirm = pic.GetValue<bool>(); } catch { } }
                    if (o.TryGetPropertyValue("panelNsfwMode", out var pnm) && pnm != null)
                    {
                        try
                        {
                            var v = (pnm.GetValue<string>() ?? "").Trim();
                            if (v == "growth" || v == "allopen") PanelNsfwMode = v;
                        }
                        catch { }
                    }
                    if (o.TryGetPropertyValue("panelCheatEdit", out var pce) && pce != null)
                    { try { PanelCheatEdit = pce.GetValue<bool>(); } catch { } }
                    if (o.TryGetPropertyValue("panelInjectMaxChars", out var pimc) && pimc != null)
                    { try { var v = pimc.GetValue<int>(); if (v >= 60 && v <= 400) PanelInjectMaxChars = v; } catch { } }
                    if (o.TryGetPropertyValue("nsfwPanelGating", out var npg) && npg != null)
                    { try { NsfwPanelGating = npg.GetValue<bool>(); } catch { } }
                    if (o.TryGetPropertyValue("panelBtnX", out var pbx) && pbx != null)
                    { try { PanelBtnX = pbx.GetValue<double>(); } catch { } }
                    if (o.TryGetPropertyValue("panelBtnY", out var pby) && pby != null)
                    { try { PanelBtnY = pby.GetValue<double>(); } catch { } }
                    if (o.TryGetPropertyValue("panelScale", out var psc) && psc != null)
                    {
                        try
                        {
                            var v = psc.GetValue<float>();
                            if (v >= 0.5f && v <= 1.5f) PanelScale = v;
                        }
                        catch { }
                    }
                    if (o.TryGetPropertyValue("panelForMale", out var pfm) && pfm != null)
                    { try { PanelForMale = pfm.GetValue<bool>(); } catch { } }
                    if (o.TryGetPropertyValue("optionsOffX", out var oox) && oox != null)
                    { try { OptionsOffsetX = (float)oox.GetValue<double>(); } catch { } }
                    if (o.TryGetPropertyValue("optionsOffY", out var ooy) && ooy != null)
                    { try { OptionsOffsetY = (float)ooy.GetValue<double>(); } catch { } }
                    // ★ 阶段 4-A：剩余硬编码搬进 config.json
                    if (o.TryGetPropertyValue("replyLenLo", out var rllo) && rllo != null)
                    {
                        try { var v = rllo.GetValue<int>(); if (v >= 0) ReplyLenLo = v; } catch { }
                    }
                    if (o.TryGetPropertyValue("extractInterval", out var ei) && ei != null)
                    {
                        try { var v = ei.GetValue<int>(); if (v > 0) ExtractInterval = v; } catch { }
                    }
                    // ★ 2026-11-07 主人定：canon 保留轮数档位（实际触发点 = 该值 + ArchiveEvery(10)）
                    if (o.TryGetPropertyValue("canonKeepRounds", out var ckr) && ckr != null)
                    {
                        try
                        {
                            var v = ckr.GetValue<int>();
                            if (v >= 4 && v <= 60) CanonKeepRounds = v;
                            else Log($"[配置] canonKeepRounds={v} 超出 4~60，忽略，用默认 {CanonKeepRounds}");
                        }
                        catch { }
                    }
                    // ★ 2026-11-07 主人定：canon 归档/合并轮次（上限 = 保留 + 该值 - 1）
                    if (o.TryGetPropertyValue("canonArchiveEvery", out var cae) && cae != null)
                    {
                        try
                        {
                            var v = cae.GetValue<int>();
                            if (v >= 2 && v <= 30) CanonArchiveEvery = v;
                            else Log($"[配置] canonArchiveEvery={v} 超出 2~30，忽略，用默认 {CanonArchiveEvery}");
                        }
                        catch { }
                    }
                    // 安全闸：合并轮次至少 2 —— 等于 1 就是「每轮都裁」的滑动窗口，缓存会崩
                    if (CanonArchiveEvery < 2)
                    {
                        Log($"[配置] canonArchiveEvery={CanonArchiveEvery} 太小（<2），恢复到 2");
                        CanonArchiveEvery = 2;
                    }
                    // ★ 2026-11-07 主人定：记忆合并节拍（跟裁剪解耦，默认 24 轮）
                    if (o.TryGetPropertyValue("canonMergeEvery", out var cme) && cme != null)
                    {
                        try
                        {
                            var v = cme.GetValue<int>();
                            if (v >= 2 && v <= 200) CanonMergeEvery = v;
                            else Log($"[配置] canonMergeEvery={v} 超出 2~200，忽略，用默认 {CanonMergeEvery}");
                        }
                        catch { }
                    }
                    // 安全闸：合并节拍至少 2 —— 等于 1 就是每轮合并，等于把两个旋钮又绑回一起
                    if (CanonMergeEvery < 2)
                    {
                        Log($"[配置] canonMergeEvery={CanonMergeEvery} 太小（<2），恢复到 2");
                        CanonMergeEvery = 2;
                    }
                    // ★ 2026-11-07：玩家主角设定（空 = 不覆盖，用游戏原文）
                    if (o.TryGetPropertyValue("playerName", out var pn) && pn != null)
                    { try { PlayerName = pn.GetValue<string>() ?? ""; } catch { } }
                    if (o.TryGetPropertyValue("playerGender", out var pg) && pg != null)
                    { try { PlayerGender = pg.GetValue<string>() ?? ""; } catch { } }
                    if (o.TryGetPropertyValue("playerRealm", out var pr) && pr != null)
                    { try { PlayerRealm = pr.GetValue<string>() ?? ""; } catch { } }
                    if (o.TryGetPropertyValue("playerSect", out var ps) && ps != null)
                    { try { PlayerSect = ps.GetValue<string>() ?? ""; } catch { } }
                    if (o.TryGetPropertyValue("playerExtra", out var pe) && pe != null)
                    { try { PlayerExtra = pe.GetValue<string>() ?? ""; } catch { } }
                    if (o.TryGetPropertyValue("allowNpcActing", out var ana) && ana != null)
                    {
                        try { AllowNpcActing = ana.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("cardEnabled", out var ce) && ce != null)
                    {
                        try { CardEnabled = ce.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("replaceOfficialProfile", out var rop) && rop != null)
                    {
                        try { ReplaceOfficialProfile = rop.GetValue<bool>(); } catch { }
                    }
                    // ★ 2026-10-27 自动好感度（玩家只改数字：正面上限 / 负面上限）
                    if (o.TryGetPropertyValue("autoIntimacyEnabled", out var aie) && aie != null)
                    {
                        try { AutoIntimacyEnabled = aie.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("autoIntimacyMaxUp", out var aimu) && aimu != null)
                    {
                        try { var v = aimu.GetValue<int>(); if (v >= 1) AutoIntimacyMaxUp = Math.Clamp(v, 1, 20); } catch { }
                    }
                    if (o.TryGetPropertyValue("autoIntimacyMaxDown", out var aimd) && aimd != null)
                    {
                        try { var v = aimd.GetValue<int>(); if (v >= 1) AutoIntimacyMaxDown = Math.Clamp(v, 1, 20); } catch { }
                    }
                    if (o.TryGetPropertyValue("autoIntimacyPositive", out var aipos) && aipos != null)
                    {
                        var v = aipos.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(v)) AutoIntimacyPositive = v;
                    }
                    if (o.TryGetPropertyValue("autoIntimacyNegative", out var aineg) && aineg != null)
                    {
                        var v = aineg.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(v)) AutoIntimacyNegative = v;
                    }
                    if (o.TryGetPropertyValue("autoIntimacyApiUrl", out var aiau) && aiau != null)
                    {
                        try { AutoIntimacyApiUrl = aiau.GetValue<string>() ?? ""; } catch { }
                    }
                    if (o.TryGetPropertyValue("autoIntimacyModel", out var aiam) && aiam != null)
                    {
                        try { AutoIntimacyModel = aiam.GetValue<string>() ?? ""; } catch { }
                    }
                    if (o.TryGetPropertyValue("autoIntimacyApiKey", out var aiak) && aiak != null)
                    {
                        try { AutoIntimacyApiKey = aiak.GetValue<string>() ?? ""; } catch { }
                    }
                    if (o.TryGetPropertyValue("autoIntimacyMode", out var aim) && aim != null)
                    {
                        var vm = aim.GetValue<string>();
                        if (vm == "sync" || vm == "async") AutoIntimacyMode = vm;
                    }
                    if (o.TryGetPropertyValue("memoryEnabled", out var me) && me != null)
                    {
                        try { MemoryEnabled = me.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("worldbookEnabled", out var wbe) && wbe != null)
                    {
                        try { WorldbookEnabled = wbe.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("lorebookEnabled", out var lbe) && lbe != null)
                    {
                        try { LorebookEnabled = lbe.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("nsfwEnabled", out var ne) && ne != null)
                    {
                        try { NsfwEnabled = ne.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("hideNsfw", out var hn) && hn != null)
                    {
                        try { HideNsfw = hn.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("cardModel", out var cm) && cm != null)
                    {
                        try { CardModel = cm.GetValue<string>() ?? ""; } catch { }
                    }
                    if (o.TryGetPropertyValue("memoryInjectLimit", out var mil) && mil != null)
                    {
                        try
                        {
                            var v = mil.GetValue<int>();
                            if (v >= 200) MemoryInjectLimit = v;   // 太小的值会让记忆完全没用
                        }
                        catch { }
                    }
                    if (o.TryGetPropertyValue("modApiUrl", out var mau) && mau != null)
                    {
                        try { ModApiUrl = mau.GetValue<string>() ?? ""; } catch { }
                    }
                    if (o.TryGetPropertyValue("modApiKey", out var mak) && mak != null)
                    {
                        try { ModApiKey = mak.GetValue<string>() ?? ""; } catch { }
                    }
                    if (o.TryGetPropertyValue("modModel", out var mm) && mm != null)
                    {
                        try { ModModel = mm.GetValue<string>() ?? ""; } catch { }
                    }
                    if (o.TryGetPropertyValue("consoleAutoOpen", out var cao) && cao != null)
                    {
                        try { ConsoleAutoOpen = cao.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("consoleAppMode", out var cam) && cam != null)
                    {
                        try { ConsoleAppMode = cam.GetValue<bool>(); } catch { }
                    }
                    if (o.TryGetPropertyValue("consoleWinW", out var cww) && cww != null)
                    {
                        try { var v = cww.GetValue<int>(); if (v >= 400) ConsoleWinW = v; } catch { }
                    }
                    if (o.TryGetPropertyValue("consoleWinH", out var cwh) && cwh != null)
                    {
                        try { var v = cwh.GetValue<int>(); if (v >= 300) ConsoleWinH = v; } catch { }
                    }
                    if (o.TryGetPropertyValue("dumpKeep", out var dk) && dk != null)
                    {
                        try
                        {
                            var kv = dk.GetValue<int>();
                            DumpKeep = kv < 10 ? 10 : (kv > 5000 ? 5000 : kv);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e)
            {
                Log($"⚠ config.json 解析失败（用 tier=1）：{e.GetType().Name}: {e.Message}", true);
            }
            if (tier != 1 && tier != 2) tier = 1;
            Tier = tier;

            // ★ 补齐新字段：老版本 config.json 没有 dumpEnabled / dumpKeep，
            //   补进去让玩家看得见（保留所有已有字段与原值）
            try { EnsureConfigFields(); } catch { }

            // ③ 选注入内容
            string source;
            if (tier == 2)
            {
                if (System.IO.File.Exists(Tier2Path))
                {
                    var t2 = SafeRead(Tier2Path);
                    if (!string.IsNullOrWhiteSpace(t2))
                    {
                        Inject = t2.Trim();
                        source = "tier2-inject.txt";
                    }
                    else
                    {
                        Inject = builtinDefault;
                        source = "内置默认（tier2-inject.txt 为空）";
                        Log("⚠ tier=2 但 tier2-inject.txt 为空，回退内置默认", true);
                    }
                }
                else
                {
                    Inject = builtinDefault;
                    source = "内置默认（tier=2 但 tier2-inject.txt 不存在）";
                    Log("⚠ tier=2 但 tier2-inject.txt 不存在，回退内置默认", true);
                }
            }
            else
            {
                var t1 = SafeRead(InjectPath);
                if (!string.IsNullOrWhiteSpace(t1))
                {
                    Inject = t1.Trim();
                    source = "inject.txt";
                }
                else
                {
                    Inject = builtinDefault;
                    source = "内置默认（inject.txt 为空）";
                    Log("⚠ inject.txt 为空，回退内置默认", true);
                }
            }

            Summary = $"档位 {Tier} / 来源 {source} / {Inject.Length} 字 / dump={(DumpEnabled ? $"开(留{DumpKeep})" : "关")} / 死命令={JsonIntensifyDesc}" + (HideNsfw ? " / ★过审模式=开" : "");
            Log($"✓ 提示词配置已加载：{Summary}");
        }
        catch (Exception e)
        {
            Inject = builtinDefault;
            Tier = 1;
            Summary = $"内置默认（加载异常：{e.GetType().Name}）";
            Log($"⚠ 配置加载异常，回退内置默认：{e.GetType().Name}: {e.Message}", true);
        }

            // ★ NSFW 板块初始化（建目录 / 写默认文件 / 读内容）
            try { NsfwConfig.Load(NsfwMode, NsfwLatch); } catch { }

            // ★ 阶段 4-A 包容性校验（2026-10-24 20:10）：
            //   玩家可能填出矛盾值（如总上限 80 却把旁白设 200、台词设 100）。
            //   自动收敛到合理范围，避免「提示词说 200、代码按 80 裁」的错位。
            try
            {
                var sum = ReplyLenNarration + ReplyLenDialogue;
                if (ReplyLenTotal < sum)
                {
                    Log($"[配置] 总上限 {ReplyLenTotal} 小于 旁白+台词 {sum}，自动收敛为 {sum}");
                    ReplyLenTotal = sum;
                }
                if (ReplyLenLo > ReplyLenTotal)
                {
                    Log($"[配置] 下限 {ReplyLenLo} 大于 总上限 {ReplyLenTotal}，自动收敛为 {ReplyLenTotal}");
                    ReplyLenLo = ReplyLenTotal;
                }
            }
            catch { }
    }

    /// <summary>
    /// 确保 config.json 里存在新版字段，缺了就补默认值（不覆盖已有值）。
    /// </summary>
    private static void EnsureConfigFields()
    {
        if (!System.IO.File.Exists(ConfigPath)) return;
        var raw = System.IO.File.ReadAllText(ConfigPath, Encoding.UTF8);
        if (JsonNode.Parse(raw) is not JsonObject o) return;

        var defaults = new (string Key, JsonNode Value)[]
        {
            ("dumpEnabled", JsonValue.Create(false)),
            ("dumpKeep", JsonValue.Create(200)),
            ("deadCommandExample", JsonValue.Create(true)), // 2026-11-02：死命令后追加完整 a1ttsv2 示例（实测 50%→75%）
            ("optionsEnabled", JsonValue.Create(true)),     // 2026-11-03：四选一对话选项（顶层 opts，输出侧摘掉）
            ("outputLanguage", JsonValue.Create("zh-CN")),  // 2026-11-12：AI 回复语言（zh-CN / zh-TW / en）
            // ★ 2026-11-12：NPC 数值面板（1.4.0，施工图 §8）——默认关，不影响老玩家
            ("panelEnabled", JsonValue.Create(true)),
            ("panelInMainRequest", JsonValue.Create(true)),
            ("panelNsfw", JsonValue.Create(true)),
            ("panelStepMax", JsonValue.Create(1)),
            ("panelStrongStep", JsonValue.Create(2)),
            ("panelNsfwStepMax", JsonValue.Create(2)),
            ("panelDeadZone", JsonValue.Create(7)),
            ("panelCooldownStreak", JsonValue.Create(3)),
            ("panelCooldownLatch", JsonValue.Create(2)),
            ("panelDecayEvery", JsonValue.Create(8)),
            ("panelNsfwDecayIdle", JsonValue.Create(true)),
            ("panelInitConfirm", JsonValue.Create(true)),
            ("panelNsfwMode", JsonValue.Create("growth")),
            ("panelCheatEdit", JsonValue.Create(false)),
            ("panelInjectMaxChars", JsonValue.Create(120)),
            ("nsfwPanelGating", JsonValue.Create(false)),
            ("panelBtnX", JsonValue.Create(0.66)),
            ("panelBtnY", JsonValue.Create(0.80)),
            ("panelScale", JsonValue.Create(1.0)),
            ("panelForMale", JsonValue.Create(false)),
            ("optionsOffX", JsonValue.Create(0.0)),
            ("optionsOffY", JsonValue.Create(0.0)),
            ("intimacyInMainRequest", JsonValue.Create(true)), // 2026-11-04：好感度走主请求（省一次调用）
            ("cardEnabled", JsonValue.Create(true)),
            ("replaceOfficialProfile", JsonValue.Create(true)),   // 2026-10-26 主人定：默认常开（卡不齐时自动保留原文）
            ("autoIntimacyEnabled", JsonValue.Create(false)),     // 2026-10-27 自动好感度（测试功能，默认关）
            ("autoIntimacyMaxUp", JsonValue.Create(5)),
            ("autoIntimacyMaxDown", JsonValue.Create(3)),
            ("autoIntimacyPositive", JsonValue.Create("真诚关心、体贴、说到她心里、共同经历、送她喜欢的东西")),
            ("autoIntimacyNegative", JsonValue.Create("冒犯、轻慢、欺骗、触到她的底线、无视她的感受")),
            ("autoIntimacyApiUrl", JsonValue.Create("")),
            ("autoIntimacyApiKey", JsonValue.Create("")),
            ("autoIntimacyModel", JsonValue.Create("")),
            ("autoIntimacyMode", JsonValue.Create("async")),
            ("memoryEnabled", JsonValue.Create(true)),
            ("worldbookEnabled", JsonValue.Create(true)),
            ("lorebookEnabled", JsonValue.Create(true)),
            ("nsfwEnabled", JsonValue.Create(true)),
        ("hideNsfw", JsonValue.Create(false)),   // 2026-11-09 过审模式（隐藏所有 NSFW）
            ("memoryInjectLimit", JsonValue.Create(3000)),
            // ★ 2026-11-07 主人定：canon 保留轮数档位（触发点 = 保留 + 合并轮次）
            ("canonKeepRounds", JsonValue.Create(10)),
            // ★ 2026-11-07：归档/合并轮次（High 档默认 6 → 波动 10~15 轮）
            ("canonArchiveEvery", JsonValue.Create(6)),
            // ★ 2026-11-07：记忆合并节拍（跟裁剪解耦，High 档默认 24 轮合并一次）
            ("canonMergeEvery", JsonValue.Create(24)),
            // ★ 2026-11-07：玩家主角设定（空 = 用游戏原文）
            ("playerName", JsonValue.Create("")),
            ("playerGender", JsonValue.Create("")),
            ("playerRealm", JsonValue.Create("")),
            ("playerSect", JsonValue.Create("")),
            ("playerExtra", JsonValue.Create("")),
            ("memoryTier", null),   // null = 删除旧字段
        };

        var changed = false;
        foreach (var (key, val) in defaults)
        {
            if (val == null)
            {
                // null = 删除这个旧字段
                if (o.ContainsKey(key)) { o.Remove(key); changed = true; }
                continue;
            }
            if (!o.ContainsKey(key))
            {
                o[key] = val;
                changed = true;
            }
        }
        if (!changed) return;

        var text = o.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        System.IO.File.WriteAllText(ConfigPath, text + "\n", new UTF8Encoding(false));
        Log("✓ config.json 已补齐缺失字段（各功能总开关等）");
    }

    private static string SafeRead(string path)
    {
        try { return System.IO.File.ReadAllText(path, Encoding.UTF8); }
        catch { return null; }
    }

    private static void TryWrite(string path, string content)
    {
        try
        {
            if (System.IO.File.Exists(path)) return;
            System.IO.File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Log($"⚠ 写文件失败 {System.IO.Path.GetFileName(path)}：{e.GetType().Name}", true);
        }
    }

    /// <summary>★ 2026-11-13：保存面板缩放（玩家拖过滑块后重启游戏 / 换新对话都保留）</summary>
    public static void SetPanelScale(float v)
    {
        v = System.Math.Clamp(v, 0.5f, 1.5f);
        PanelScale = v;
        try
        {
            if (!System.IO.File.Exists(ConfigPath)) return;
            var raw = System.IO.File.ReadAllText(ConfigPath, Encoding.UTF8);
            if (System.Text.Json.Nodes.JsonNode.Parse(raw) is not System.Text.Json.Nodes.JsonObject o) return;
            o["panelScale"] = System.Text.Json.Nodes.JsonValue.Create((double)System.Math.Round(v, 3));
            var text = o.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            System.IO.File.WriteAllText(ConfigPath, text + "\n", new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Log($"⚠ 保存面板缩放失败：{e.GetType().Name}", true);
        }
    }

    /// <summary>★ 2026-11-13 主人要：保存选项框拖动偏移（相对自动定位；重启/换对话都保留）</summary>
    public static void SetOptionsOffset(float x, float y)
    {
        x = (float)System.Math.Round(x, 1);
        y = (float)System.Math.Round(y, 1);
        if (System.Math.Abs(x - OptionsOffsetX) < 0.5f && System.Math.Abs(y - OptionsOffsetY) < 0.5f) return;
        OptionsOffsetX = x;
        OptionsOffsetY = y;
        try
        {
            if (!System.IO.File.Exists(ConfigPath)) return;
            var raw = System.IO.File.ReadAllText(ConfigPath, Encoding.UTF8);
            if (System.Text.Json.Nodes.JsonNode.Parse(raw) is not System.Text.Json.Nodes.JsonObject o) return;
            o["optionsOffX"] = System.Text.Json.Nodes.JsonValue.Create((double)x);
            o["optionsOffY"] = System.Text.Json.Nodes.JsonValue.Create((double)y);
            var text = o.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            System.IO.File.WriteAllText(ConfigPath, text + "\n", new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Log($"⚠ 保存选项框位置失败：{e.GetType().Name}", true);
        }
    }

    /// <summary>★ 2026-11-13：恢复选项框自动定位（偏移清零并写盘）</summary>
    public static void ResetOptionsOffset()
    {
        OptionsOffsetX = 0f;
        OptionsOffsetY = 0f;
        SetOptionsOffset(0f, 0f);
    }

    /// <summary>档位 2 的模板（给玩家拿去问本地模型）。</summary>
    private const string Tier2Template = """
# 提示词优化模板（档位 2 · 本地模型 / 中转模型适配）

## 怎么用

1. 打开你用的本地模型 / 中转模型（LM Studio、Ollama、任意聊天界面都行）
2. 把下面「发给模型的提问」整段复制过去
3. 它会生成一份适合它自己的格式限制文档
4. 把生成的内容粘贴到 `config\tier2-inject.txt`
5. 把 `config\config.json` 里的 `"tier"` 改成 `2`
6. 重启游戏

---

## 发给模型的提问（复制下面这段）

```
我要用你来驱动一个角色扮演游戏。游戏的引擎要求你的每次回复必须是一个 JSON 对象，
里面至少包含这几个字段：

  content   —— 展示给玩家的文本。旁白/动作/神态/场景/心理描写统一写在（中文全角括号）里，
               角色说的话一律用“全角双引号”括起，并且和旁白分开成行。
  emotion   —— 情绪标签，一个英文小写单词（如 normal / angry / sad / happy / shy）
  control   —— 关系控制，通常是 "none"；只在玩家明确要建立关系且你愿意时才用别的值

另外还有一些硬性要求：
  · 不要输出 JSON 之外任何内容（不要解释、不要 markdown 代码块围栏、不要前后缀）
  · 不要出现「作为AI」「我只是语言模型」这类声明
  · 整体长度约 120~200 字；旁白不超过 120 字，台词不超过 80 字
  · 台词要精简，能一句说完就不要两句
  · 不要每句都用问句收尾

请你做两件事：
  1. 写一段「系统提示词」，确保你每次都能严格输出上面这个 JSON 结构
  2. 告诉我：你希望我用什么格式/顺序组织这段提示词，你才最容易遵守

输出格式：
【系统提示词】
（这里写你希望的提示词全文）

【使用说明】
（这里写你对格式组织方式的建议）
```

---

## 把结果放哪

```
把【系统提示词】那部分的内容，整段粘贴到：
  plugins\BuWenFanChen\config\tier2-inject.txt

然后把 config\config.json 改成：
  { "tier": 2 }

重启游戏。
```

## 注意

```
· tier2-inject.txt 会【完全替换】内置注入，不是追加
  → 所以里面必须包含破甲、格式、剧情前提这些要求
  → 模板里已经让模型自己写了，模型应该会带上
· 如果发现效果变差，把 config.json 的 tier 改回 1 即可
· 本 MOD 的 REPLACE 规则（改写游戏原文的那 14 条）不受此配置影响，始终生效
```
""";
}
