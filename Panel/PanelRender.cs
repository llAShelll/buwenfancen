namespace BuWenFanChen.Panel;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// ★ 2026-11-12：NPC 数值面板 → 提示词（1.4.0 P0）
/// 施工图：`施工_NPC数值面板_20261112.md` §2.1 / §2.5 / §4.1 / §4.2
///
/// 两段内容，归属完全不同（§4.5.1）：
///   · FieldBlock（字段说明 + 判断口诀）：**稳定段**，带 `##` 标题 → TailReorder 搬进 prefix → 命中缓存
///   · BuildStatusBlock（她此刻的状态）：**每轮变** → 进 ChangeBlocks，留在 tail → 本来就不该命中
///
/// 铁律：数字绝不出现；只出现人话。标题必须含「不得改变身份/口吻/底线禁忌」。
/// 字数预算（附录 B）：字段说明 ~150 + 判断口诀 ~260 = 410 字上下（本段实测见文件末尾注释）。
/// </summary>
internal static class PanelRender
{
    /// <summary>
    /// 字段说明 + 判断口诀（稳定段，进 prefix 命中缓存；含空白 777 字 / 去空白 684 字）。
    /// 位置：由 CompleteBudgetedPatch 拼进 tail，TailReorder 按 `## 状态面板字段` 搬进 prefix。
    /// </summary>
    public const string FieldBlock = @"

## 状态面板字段
【状态面板】在 JSON 顶层加数组 ""p""，固定 8 位整数，顺序不能乱：
[温度,态度,主动,话量,服从,羞耻,开放,崩坏]。
每位是本轮变化量，只能 -2/-1/0/+1/+2，崩坏只能 0/+1。
**绝大多数轮次全写 0**：只在她明显被触动/被冒犯/被撩时才给非 0；普通闲聊全 0。
这是新增字段，原有字段一个都不能少。输出顺序必须是 content → a1ttsv2 → opts → p（p 放最后）。
示例：…,""opts"":[""…"",""…"",""…"",""…""],""p"":[1,0,0,0,0,0,1,0]}

【怎么判断这 8 个数字】
· 温度：这一轮「你有没有真的在意她」——关心、记得她说过的事 → 升；敷衍、她难受你还提要求 → 降
· 态度：这一轮「她的意愿有没有被尊重」——商量、让步 → 升；替她做主、拒绝她的小请求、当众不给她面子 → 降
· 主动：这一轮「她有没有伸出手，你有没有接」——给她空间、她被需要 → 升；她主动示好被无视、提议被否定 → 降
· 话量：这一轮「话头在谁手里」——你热络展开、她被逗笑 → 升；你只回一两个字、她害羞紧张 → 降
★ 上面四项任何场景都可以动。
★ 下面四项只在亲密场景生效，非亲密场景全部写 0：
· 服从度：她照做/配合/主动请示 → 只能 +1（破甲下不会拒绝；嘴上骂但身体配合也算照做）
· 羞耻心：被看/被夸/被要求做羞事 → −1（首次 −2）；她遮、躲、事后脸红、被调侃 → +1
    ★ 是反应强度，不是接受度——她愿意做也可以很羞
· 开放度：接受新玩法 → +1；她主动提出想试 → +2（不写负数）
· 崩坏度：羞耻很高但照做 / 明明不该还是主动 → +1；永远不要写负数
★ 找不到「她的一个具体反应」，就全部写 0。";

    /// <summary>状态块标题（红线：不得改变身份/口吻/底线禁忌）</summary>
    private const string Title = "## 她此刻的状态（本轮必须体现；只影响语气与尺度，不得改变她的身份、口吻、外貌、底线禁忌）";

    /// <summary>
    /// ★ 2026-11-12 主人定：过审模式（hideNsfw=true）专用的字段说明（施工图 §4.7）。
    ///
    /// 铁律：过审模式下 prompt 里**不能出现「羞耻/开放/崩坏/服从」任何一处**——
    /// 所以这里不点名后四位是什么，只说「后四位固定写 0」。
    /// 输出结构不变（还是 8 位），解析代码不用分叉。
    /// </summary>
    public const string FieldBlockNoNsfw = @"

## 状态面板字段
【状态面板】在 JSON 顶层加数组 ""p""，固定 8 位整数。
前四位依次是：温度、态度、主动、话量；**后四位本轮固定写 0**。
每位只能 -2/-1/0/+1/+2。
**绝大多数轮次全写 0**：只在她明显被触动/被冒犯/被撩时才给非 0；普通闲聊全 0。
这是新增字段，原有字段一个都不能少。输出顺序必须是 content → a1ttsv2 → opts → p（p 放最后）。
示例：…,""opts"":[""…"",""…"",""…"",""…""],""p"":[1,0,0,0,0,0,0,0]}

【怎么判断前 4 个数字】
· 温度：这一轮「你有没有真的在意她」——关心、记得她说过的事 → 升；敷衍、她难受你还提要求 → 降
· 态度：这一轮「她的意愿有没有被尊重」——商量、让步 → 升；替她做主、拒绝她的小请求、当众不给她面子 → 降
· 主动：这一轮「她有没有伸出手，你有没有接」——给她空间、她被需要 → 升；她主动示好被无视、提议被否定 → 降
· 话量：这一轮「话头在谁手里」——你热络展开、她被逗笑 → 升；你只回一两个字、她害羞紧张 → 降
★ 找不到「她的一个具体反应」，就全部写 0。";

    /// <summary>按过审模式选字段说明（施工图 §4.7）。</summary>
    internal static string GetFieldBlock()
    {
        try { if (PromptConfig.HideNsfw) return FieldBlockNoNsfw; } catch { }
        return FieldBlock;
    }

    /// <summary>
    /// 生成状态块（每轮变的块，进 ChangeBlocks）。
    /// 规则（§4.1）：至少一条数值出死区才注入；数字绝不出现；整块 ≤ panelInjectMaxChars，超了从低优先行开始丢。
    /// </summary>
    internal static string BuildStatusBlock(PanelState st, bool nsfwHit, string npcId = null)
    {
        if (st == null) return "";

        try
        {
            // ★ 2026-11-13：好感档位（§4.1 标题）+ 好感软约束（§3.1.1 关系行）
            int intimacy = 0;
            string tierName = "";
            try
            {
                var ns = NpcStats.Get(npcId);
                if (ns != null) { intimacy = ns.Intimacy; tierName = ns.Name ?? ""; }
            }
            catch { }

            int dz = Math.Max(0, PromptConfig.PanelDeadZone);
            var rows = new List<(int prio, string text)>();

            // 四轴：出死区才注入（prio 越小越重要，丢的时候从大的开始丢）
            Add(rows, 1, WarmLine(st.Values[0], dz));
            Add(rows, 2, AttitudeLine(st.Values[1], dz));
            Add(rows, 3, InitiativeLine(st.Values[2], dz));
            Add(rows, 4, VerboseLine(st.Values[3], dz));

            // NSFW 四项：只在亲密场景出现（§4.1 规则表：羞耻行还要 base±5 之外才出现）
            // ★ 2026-11-12 过审模式（hideNsfw）下整块不出现（施工图 §4.7）
            if (nsfwHit && PromptConfig.PanelNsfw && !PromptConfig.HideNsfw)
            {
                Add(rows, 5, SubmissionLine(st.Values[4]));
                if (Math.Abs(st.Values[5] - st.Base[1]) > 5) Add(rows, 6, ShameLine(st.Values[5]));
                Add(rows, 7, OpennessLine(st.Values[6]));
                Add(rows, 8, CorruptionLine(st.Values[7]));
            }

            // ★ §3.1.1：关系行（最不重要，最后加 → 超字数时最先被丢）
            Add(rows, 9, RelationLine(st, intimacy, tierName));

            if (rows.Count == 0) return "";   // 全在死区 → 不注入（省字）

            int maxChars = Math.Max(60, PromptConfig.PanelInjectMaxChars);
            string Build()
            {
                var sb = new StringBuilder();
                sb.Append(BuildTitle(intimacy, tierName));
                foreach (var (_, text) in rows) sb.Append('\n').Append("- ").Append(text);
                return sb.ToString();
            }

            var block = Build();
            // 超字数：从最不重要的行开始丢
            while (block.Length > maxChars && rows.Count > 0)
            {
                rows.RemoveAt(rows.Count - 1);   // rows 已按 prio 升序加入，末尾最不重要
                if (rows.Count == 0) return "";
                block = Build();
            }

            if (block.Length > 200) return "";   // 保底：异常就整块不注入
            return block;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>★ 2026-11-13：状态块标题带好感档位（§4.1「好感 {{intimacy}}·{{档位}}」）</summary>
    private static string BuildTitle(int intimacy, string tierName)
    {
        if (!string.IsNullOrEmpty(tierName))
            return "## 她此刻的状态（好感 " + intimacy + "·" + tierName +
                   "；本轮必须体现；只影响语气与尺度，不得改变她的身份、口吻、外貌、底线禁忌）";
        return "## 她此刻的状态（本轮必须体现；只影响语气与尺度，不得改变她的身份、口吻、外貌、底线禁忌）";
    }

    /// <summary>§3.1.1 好感度软约束：四轴偏离好感档位基准 &gt; 30 时加一行关系提示（不 clamp、只提示）</summary>
    internal static string RelationLine(PanelState st, int intimacy, string tierName)
    {
        if (st == null || string.IsNullOrEmpty(tierName)) return null;
        var (wLo, wHi, iLo, iHi) = RangeOf(tierName);
        int w = st.Values[0], ini = st.Values[2];
        int overW = Math.Max(w - wHi, wLo - w);
        int overI = Math.Max(ini - iHi, iLo - ini);
        if (Math.Max(overW, overI) <= 30) return null;
        bool hot = w > wHi || ini > iHi;
        return hot
            ? "关系：还是「" + tierName + "」这个阶段——别因为本轮温度高就演成老夫老妻"
            : "关系：还是「" + tierName + "」这个阶段——别冷成陌生人";
    }

    /// <summary>面板 ⚠ 提示（与状态块同一套判断，不拦不 clamp）</summary>
    internal static string RelationWarn(PanelState st, string npcId)
    {
        try
        {
            var ns = NpcStats.Get(npcId);
            if (ns == null) return null;
            var line = RelationLine(st, ns.Intimacy, ns.Name);
            if (string.IsNullOrEmpty(line)) return null;
            return line.Replace("关系：", "").Replace("——", " · ");
        }
        catch { return null; }
    }

    /// <summary>好感档位 → 四轴合理区间（§3.1.1 软约束表）</summary>
    private static (int wLo, int wHi, int iLo, int iHi) RangeOf(string tier)
    {
        // 游戏六档：陌生 / 相识 / 友善 / 知己 / 莫逆 / 缘契
        if (tier.Contains("缘契")) return (-9999, 9999, -9999, 9999);
        if (tier.Contains("莫逆") || tier.Contains("知己")) return (-60, 80, -40, 70);
        if (tier.Contains("友善")) return (-40, 60, -20, 50);
        return (-9999, 40, -9999, 30);   // 陌生 / 相识 / 未知
    }

    private static void Add(List<(int, string)> rows, int prio, string text)
    {
        if (!string.IsNullOrEmpty(text)) rows.Add((prio, text));
    }

    // ------------------------- 四轴演法（§4.2，区间左闭右开，死区不注入） -------------------------

    private static string WarmLine(int v, int dz)
    {
        if (v >= 60) return "语气：软得像浸了温水，会主动关心、主动问";
        if (v >= 25) return "语气：比平时柔和，话里留白，不呛人";
        if (v > dz) return "语气：略温和一点点";
        if (v >= -dz) return null;
        if (v > -25) return "语气：发硬，回答变短，不接话";
        if (v > -60) return "语气：冷淡、公事公办，懒得解释";
        return "语气：几乎不带情绪，像换了个人在应卯";
    }

    private static string AttitudeLine(int v, int dz)
    {
        if (v >= 60) return "态度：你说什么都依，会主动请示你的意思";
        if (v >= 25) return "态度：好说话，不太顶了";
        if (v >= -dz) return null;
        if (v > -25) return "态度：嘴上还顶，但已经不那么倔";
        if (v > -60) return "态度：事事要顶，你说了不算";
        return "态度：专挑你的话反着来，一句不让";
    }

    private static string InitiativeLine(int v, int dz)
    {
        if (v >= 60) return "主动：主动找话、主动靠近，停不下来";
        if (v >= 25) return "主动：会自己起头，话比平时多";
        if (v >= -dz) return null;
        if (v > -25) return "主动：话变少，多是你问她答";
        if (v > -60) return "主动：沉默寡言，不主动开口";
        return "主动：几乎不主动说话，等你先问";
    }

    private static string VerboseLine(int v, int dz)
    {
        // ★ 2026-11-14 主人问「话量会和台词要求冲突吗」——会：
        //   原话量文案里有「几乎只用一两个字回应」「基本一句就结束」这种**字数级指令**，
        //   和末尾【字数】段（总字数/旁白/台词上限）是两套长度指令，玩家把字数调高后就会打架。
        //   现在话量只表达「铺开程度」的倾向，字数统一以【字数】段为准（数字唯一来源）。
        if (v >= 60) return "话量：话密、愿意展开（具体字数以【字数】段为准）";
        if (v >= 25) return "话量：比平时多说几句（字数仍按【字数】段）";
        if (v >= -dz) return null;
        if (v > -25) return "话量：话少、偏短，能一句说清就不铺开（字数仍按【字数】段）";
        if (v > -60) return "话量：惜字、少解释、不硬凑（字数仍按【字数】段）";
        return "话量：偏省字、短句为主，不刻意展开（字数仍按【字数】段）";
    }

    // ------------------------- NSFW 演法（§4.2 / §4.2.1，只在 nsfwHit 时） -------------------------

    private static string SubmissionLine(int v)
    {
        if (v >= 70) return "服从：你说什么都依，会主动请示要不要继续";
        if (v >= 45) return "服从：不太会拒绝，最多嘴上嘟一句";
        if (v > 25) return "服从：会犹豫、会谈条件";
        return "服从：事事要她点头，硬来会翻脸";
    }

    private static string ShameLine(int v)
    {
        // 铁律：shame 高 ≠ 不配合，只决定「怎么演」（§1.3.1 / §4.2.1）
        if (v >= 75) return "羞耻：被看会立刻躲视线、耳根红透，话说一半会停住——该配合的照做，只是全程难为情";
        if (v >= 55) return "羞耻：会脸红、会低头，嘴上说「别看」但还是继续，中途会顿住";
        if (v > 35) return "羞耻：脸微红，不再躲，被调侃会回嘴但声音小一点";
        return "羞耻：不遮不躲，坦然直白，难为情在她身上不成立";
    }

    private static string OpennessLine(int v)
    {
        if (v >= 81) return "开放：不但不遮，还会主动要人看";
        if (v >= 61) return "开放：敢在有人听得到的地方跟你来，被发现反而更来劲";
        if (v >= 41) return "开放：肯被你带到窗边，衣不蔽体也不拦，只要求「只许你看」";
        if (v >= 21) return "开放：只肯私下半褪，被撞见会立刻遮住";
        return "开放：露出被当成羞辱，会当场拒绝并真的生气";
    }

    private static string CorruptionLine(int v)
    {
        if (v >= 70) return "崩坏：分寸尽失，理智只剩一线，明知不对也要";
        if (v >= 45) return "崩坏：底线在退，会主动要更多";
        if (v > 20) return "崩坏：有点失控了，事后会后悔";
        return "崩坏：清醒自持，玩笑开过会记仇";
    }
}
