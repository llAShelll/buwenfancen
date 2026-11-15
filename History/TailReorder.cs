using System.Text;

namespace BuWenFanChen.History;

/// <summary>
/// ★ 2026-11-06：三区重排（方案 v3 §3.4）
///
/// 目的：把 tail 里「稳定的、不该占尾巴的」搬到首区（prefix，缓存最稳），
///      把「每轮变的」压到 tail 中后段，只让它们断自己后面的内容。
///
/// 实测依据（2026-11-05 三轮真实 tail，见 _capture\重排收益测算.md）：
///   原 tail 7,995 字、公共前缀 3,000 → 整体命中 88.8%
///   重排后 tail 5,823 字、公共前缀 3,381 → 整体命中 94.5%
///
/// 搬迁规则（照 §3.4）：
///   首区 ← 长期记忆 / 角色卡（口吻·外貌·自定义·亲密·代演·自称）/ 时间与推进（时间场景·推进动作·不重复·字数）
///   尾区变化块 ← 相关回忆 / 随身之物（压到死命令之前）
///   尾区最末不动 ← 死命令 / 输出示例 / 四个选项 / 好感变化（红线：必须贴输入）
/// </summary>
public static class TailReorder
{
    /// <summary>总开关</summary>
    public static bool Enabled = true;

    /// <summary>
    /// 搬到首区（prefix）的段。
    ///
    /// ★ 2026-11-06 收紧（主人提醒后修正）：只搬【绝对静态】的段。
    ///   prefix 是缓存最稳的地方，但它有个铁律：**变一个字就全断**。
    ///   所以下面这些绝对不能进：
    ///     ## 角色亲密设定      —— CardRenderer.BuildBlock(card, nsfwHit) 条件注入，
    ///                             亲密轮多 578 字 → prefix 变长 → canon 全废
    ///     ## 允许代演在场角色  —— 内容随在场角色浮动（38~322 字）
    ///     ## 长期记忆          —— 骨架会变（实测 11/17，不是 17/17）
    ///     【时间与场景】       —— 含当前时间地点，会变
    ///   它们留在 tail，靠"稳定在前、变化在后"的排序减少伤害。
    /// </summary>
    private static readonly string[] ToPrefix =
    {
        "## 长期记忆",          // ★ 归档节拍更新（每 10 轮一次，和 canon 裁剪同轮），可以进
        "## 角色属性",          // ★ 2026-11-06 补：角色卡开头就是它，12 轮 hash 完全一致
        "## 角色口吻",          // 卡字段，卡不变就固定
        "## 角色外貌穿着",      // 同上
        "## 自定义设定",        // 同上
        "## 自称与名字",        // 同上
        "【推进与动作】",       // 固定文本
        "【不重复】",           // 固定文本
        // ★ 2026-11-12：面板字段说明 + 判断口诀（稳定段，带 ## 标题）。
        //   施工图 §4.5.1：字段说明要进 prefix 命中缓存（状态块相反，见 ChangeBlocks）。
        "## 状态面板字段",
        // ★ 2026-11-06：【字数】不搬 —— `## 输出格式` 里写着「字数要求见本节末尾【字数】段」，
        //   搬走会让引用断裂（模型在 tail 里找不到）。133 字换引用完整，值。
    };

    /// <summary>每轮变、要压到最后的段</summary>
    private static readonly string[] ChangeBlocks =
    {
        "## 相关回忆",
        "**随身之物**",
        // ★ 2026-11-06 补：这些是「条件出现」的块，增删会把后面的内容全部推走
        "**待售货物**",
        "## 过往要事",
        "**本轮回馈方式**",
        "**心意赠予**",
        "## 允许代演在场角色",
        "## 关于你自己的过往与关系",
        // ★ 2026-11-12：NPC 数值面板状态块（每轮变，施工图 §4.5 红字：绝不放 ToPrefix）
        "## 她此刻的状态",
    };

    /// <summary>
    /// 破甲组：必须贴玩家输入（离生成点越近越有效）。
    /// ★ 2026-11-06：从「稳定核心」里拎出来，集中到死命令之前。
    ///   原设计（2026-10-27 主人定）就是「NSFW 离生成点最近，压过角色卡的绝不说」，
    ///   但这些轮次里被散落在各处，最高指令一度距输入 6,875 字，威力全废。
    ///   代价：这组约 1,483 字本来稳定、现在排在变化块之后不命中 —— 用命中换效果，值得。
    /// </summary>
    private static readonly string[] ArmorBlocks =
    {
        "【最高指令】",
        "【三条总闸】",
        "【全场景】",
        // ★ 2026-11-06：去套路规则是「表达约束」，必须贴输入才压得住，
        //   跟破甲组一起排（否则会掉进稳定核心，离生成点 6,000+ 字，形同虚设）。
        "【去套路】",
        // ★ 2026-11-06 主人补：角色卡的 NSFW 部分也要跟 NSFW 模块待在一起
        //   （原设计 2026-10-27：NSFW 规范离生成点最近，压过角色卡的「口吻/绝不说」）
        "## 角色亲密设定",
        "【高级破甲】",
        "【重口味授权】",
        "【细节要求】",
        "【玩法授权】",
        "【描写强度】",
        "【用语规范】",
        "【必须使用的直白词】",
        "【优先级】",
        "【严禁使用的说法】",
        "【描写义务】",
        "【台词要求】",
        "【角色辨识】",
    };

    /// <summary>必须在最末、贴玩家输入的段（红线，顺序也不动）</summary>
    private static readonly string[] EndBlocks =
    {
        "【系统死命令】",
        "【输出示例",
        "【四个对话选项】",
        "【好感变化】",
    };

    /// <summary>
    /// 重排 tail。
    /// </summary>
    /// <param name="tail">已拼好的 tail</param>
    /// <param name="prefixExtra">输出：要拼到 prefix 末尾的内容（可能为空串）</param>
    /// <param name="note">输出：日志说明</param>
    /// <returns>重排后的 tail；出错时原样返回</returns>
    public static string Run(string tail, out string prefixExtra, out string note)
    {
        prefixExtra = "";
        note = null;
        if (!Enabled) { note = "重排开关关着"; return tail; }
        if (string.IsNullOrEmpty(tail)) { note = "tail 为空"; return tail; }

        try
        {
            var blocks = SplitBlocks(tail);
            if (blocks.Count <= 1) { note = "切不出块（单段）"; return tail; }

            var toPrefix = new StringBuilder();
            var stableHead = new StringBuilder();
            var change = new StringBuilder();
            var armor = new StringBuilder();
            var end = new StringBuilder();

            bool reachedEnd = false;
            int nPrefix = 0, nChange = 0, nArmor = 0, nEnd = 0;

            foreach (var b in blocks)
            {
                var head = HeadOf(b);

                if (StartsWithAny(head, ToPrefix)) { toPrefix.Append(b); nPrefix++; continue; }
                if (StartsWithAny(head, ChangeBlocks)) { change.Append(b); nChange++; continue; }
                if (StartsWithAny(head, ArmorBlocks)) { armor.Append(b); nArmor++; continue; }

                if (StartsWithAny(head, EndBlocks)) reachedEnd = true;
                if (reachedEnd) { end.Append(b); nEnd++; }
                else stableHead.Append(b);
            }

            prefixExtra = toPrefix.ToString();
            // 顺序：稳定核心 → 条件/变化块 → 破甲（贴输入）→ 死命令（红线）
            var newTail = stableHead.ToString() + change.ToString() + armor.ToString() + end.ToString();

            note = $"搬首区 {nPrefix} 段/{prefixExtra.Length} 字 | 变化块 {nChange} 段/{change.Length} 字 | "
                 + $"破甲组 {nArmor} 段/{armor.Length} 字 | 尾部固定 {nEnd} 段 | "
                 + $"tail {tail.Length} → {newTail.Length} 字";
            return newTail;
        }
        catch (Exception e)
        {
            prefixExtra = "";
            note = "重排异常 " + e.GetType().Name + ": " + e.Message;
            return tail;
        }
    }

    // ───────── 切块 ─────────

    /// <summary>
    /// 按「行首是 ## / ** / 【」切段。行首判定兼容 \r\n（前一个字符是 \n）。
    /// </summary>
    private static List<string> SplitBlocks(string text)
    {
        var list = new List<string>();
        int start = 0;
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i - 1] != '\n') continue;
            if (!IsBlockStart(text, i)) continue;
            list.Add(text.Substring(start, i - start));
            start = i;
        }
        if (start < text.Length) list.Add(text.Substring(start));
        return list;
    }

    private static bool IsBlockStart(string text, int i)
    {
        if (i >= text.Length) return false;
        char c = text[i];
        if (c == '【') return true;
        if (c == '#' && i + 1 < text.Length && text[i + 1] == '#') return true;
        if (c == '*' && i + 1 < text.Length && text[i + 1] == '*') return true;
        return false;
    }

    /// <summary>取块的第一行（用来判类型）</summary>
    private static string HeadOf(string block)
    {
        if (string.IsNullOrEmpty(block)) return "";
        int nl = block.IndexOf('\n');
        return nl < 0 ? block : block.Substring(0, nl);
    }

    private static bool StartsWithAny(string s, string[] prefixes)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var p in prefixes)
            if (s.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }
}
