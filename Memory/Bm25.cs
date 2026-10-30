namespace BuWenFanChen.Memory;

/// <summary>
/// BM25 检索（纯 C#，无依赖）。
/// 中文按【单字 + bigram】切分，英文/数字按整词切分。
/// 参数用 BM25 标准值：k1=1.5, b=0.75。
/// </summary>
public sealed class Bm25
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    private readonly Dictionary<string, List<(int DocIdx, int Tf)>> _postings = new();
    private readonly List<int> _docLens = new();
    private double _avgDl = 1;

    public int DocCount => _docLens.Count;

    /// <summary>构建索引。docs 的每个元素对应一个可检索单元（chunk / fact）。</summary>
    public static Bm25 Build(IReadOnlyList<string> docs)
    {
        var bm = new Bm25();
        for (var i = 0; i < docs.Count; i++)
        {
            var terms = Tokenize(docs[i]);
            var tf = new Dictionary<string, int>();
            foreach (var t in terms)
                tf[t] = tf.TryGetValue(t, out var n) ? n + 1 : 1;
            foreach (var kv in tf)
            {
                if (!bm._postings.TryGetValue(kv.Key, out var list))
                    bm._postings[kv.Key] = list = new List<(int, int)>();
                list.Add((i, kv.Value));
            }
            bm._docLens.Add(terms.Count);
        }
        if (bm._docLens.Count > 0)
        {
            double sum = 0;
            foreach (var l in bm._docLens) sum += l;
            bm._avgDl = sum / bm._docLens.Count;
        }
        return bm;
    }

    /// <summary>检索，返回 (docIdx, score) 按分数降序。</summary>
    public List<(int DocIdx, double Score)> Search(string query, int topK)
    {
        var result = new List<(int, double)>();
        if (_docLens.Count == 0 || string.IsNullOrWhiteSpace(query)) return result;

        var qTerms = Tokenize(query);
        if (qTerms.Count == 0) return result;

        var scores = new Dictionary<int, double>();
        var n = _docLens.Count;
        var seen = new HashSet<string>();

        foreach (var term in qTerms)
        {
            if (!seen.Add(term)) continue;   // 每个 term 只算一次
            if (!_postings.TryGetValue(term, out var posting)) continue;

            // ★ 单字降权：单字多义（「气」在"天气/语气/脾气"里都有），bigram 才是真信号
            var weight = term.Length == 1 ? 0.25 : 1.0;

            var df = posting.Count;
            var idf = Math.Log((n - df + 0.5) / (df + 0.5) + 1);
            foreach (var (docIdx, tf) in posting)
            {
                var dl = _docLens[docIdx];
                var score = idf * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * dl / _avgDl)) * weight;
                scores[docIdx] = scores.TryGetValue(docIdx, out var s) ? s + score : score;
            }
        }

        foreach (var kv in scores)
            result.Add((kv.Key, kv.Value));

        result.Sort((a, b) => b.Item2.CompareTo(a.Item2));

        // ★ 低分噪声过滤：低于最高分 30% 的丢掉
        //   （实测「今天天气如何」时，伞那条只有最高分的 1.3%，纯噪声）
        if (result.Count > 1)
        {
            var top = result[0].Item2;
            var threshold = top * 0.3;
            result.RemoveAll(r => r.Item2 < threshold);
        }

        if (result.Count > topK) result.RemoveRange(topK, result.Count - topK);
        return result;
    }

    /// <summary>中文 bigram + 单字 + 英文/数字整词（过滤停用词）</summary>
    public static List<string> Tokenize(string text, bool filterStopWords = true)
    {
        var terms = new List<string>();
        if (string.IsNullOrEmpty(text)) return terms;

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c < 128 && char.IsLetterOrDigit(c))
            {
                var start = i;
                while (i < text.Length && text[i] < 128 && char.IsLetterOrDigit(text[i])) i++;
                terms.Add(text.Substring(start, i - start).ToLowerInvariant());
            }
            else if (IsCjk(c))
            {
                terms.Add(c.ToString());
                if (i + 1 < text.Length && IsCjk(text[i + 1]))
                    terms.Add(text.Substring(i, 2));
                i++;
            }
            else
            {
                i++;
            }
        }

        if (filterStopWords)
            terms.RemoveAll(t => StopWords.Contains(t));
        return terms;
    }

    /// <summary>
    /// 停用词表：高频虚词/代词/无区分度动词。
    /// 语料小的时候 IDF 压不住它们（这次实测「说」「来」误命中），所以显式剔除。
    /// </summary>
    private static readonly HashSet<string> StopWords = new()
    {
        // 单字：代词 / 助词 / 语气 / 介词 / 连词
        "我", "你", "他", "她", "它", "们", "的", "了", "着", "过",
        "呢", "吧", "啊", "哦", "嗯", "呀", "嘛", "么", "啦", "诶",
        "是", "在", "和", "与", "也", "就", "都", "而", "及", "或",
        "但", "却", "被", "把", "给", "让", "对", "从", "到", "于",
        "之", "其", "为", "以", "所", "者", "有", "没", "不", "无",
        "会", "能", "要", "很", "太", "更", "最", "个", "些", "点",
        "一", "又", "再", "还", "只", "才", "已", "将", "并", "且",
        "如", "若", "因", "这", "那", "上", "下", "里", "中", "来", "去",
        // 双字虚词
        "我们", "你们", "他们", "什么", "怎么", "这样", "那样",
        "这个", "那个", "这些", "那些", "一个", "一些", "一点",
        "可以", "应该", "已经", "还是", "或者", "但是", "因为",
        "所以", "如果", "虽然", "然而", "然后", "而且", "并且",
        "就是", "不是", "没有", "不要", "自己", "现在", "时候",
        // 无区分度动词
        "说", "做", "觉得", "知道", "看看", "一下",
        // 低区分度形容词/量词/方位（bigram 里会保留信息，如「天气」「地方」「说话」）
        "好", "多", "少", "大", "小", "高", "低", "长", "短",
        "天", "地", "人", "事", "话", "问", "答",
        "上", "下", "前", "后", "左", "右", "里", "外", "中", "间",
        // 双字虚词（表推测/程度）
        "好像", "应该", "可能", "大概", "也许", "似乎", "确实", "真的",
        "非常", "特别", "十分", "有点", "一直", "总是", "不过", "其实",
    };

    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;
}
