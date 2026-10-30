using System.Text;
using System.Text.Json;

namespace BuWenFanChen.Memory;

/// <summary>
/// BGE 用的 BERT WordPiece 分词器（纯托管，读 tokenizer.json）。
/// 中文按单字切、英文按词+标点切，再做 WordPiece 贪心最长匹配。
/// </summary>
public sealed class BgeTokenizer
{
    private const int MaxInputCharsPerWord = 100;

    private readonly Dictionary<string, int> _vocab;
    private readonly int _unkId;
    private readonly int _clsId;
    private readonly int _sepId;

    public int VocabSize => _vocab.Count;

    public BgeTokenizer(string tokenizerJsonPath)
    {
        using var stream = File.OpenRead(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        _vocab = new Dictionary<string, int>(21128);
        if (root.TryGetProperty("model", out var model) &&
            model.TryGetProperty("vocab", out var vocab))
        {
            foreach (var kv in vocab.EnumerateObject())
            {
                if (kv.Value.ValueKind == JsonValueKind.Number)
                    _vocab[kv.Name] = kv.Value.GetInt32();
            }
        }
        _unkId = GetId("[UNK]");
        _clsId = GetId("[CLS]");
        _sepId = GetId("[SEP]");
    }

    /// <summary>编码成 BGE 输入（input_ids / attention_mask / token_type_ids）。</summary>
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(string text, int maxLen = 512)
    {
        var tokens = new List<string> { "[CLS]" };
        if (!string.IsNullOrEmpty(text))
            tokens.AddRange(Tokenize(text));
        tokens.Add("[SEP]");

        if (tokens.Count > maxLen)
        {
            tokens = tokens.Take(maxLen - 1).ToList();
            tokens.Add("[SEP]");
        }

        var ids = new long[tokens.Count];
        var mask = new long[tokens.Count];
        var types = new long[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            ids[i] = GetId(tokens[i]);
            mask[i] = 1;
            types[i] = 0;
        }
        return (ids, mask, types);
    }

    /// <summary>仅用于调试：返回 token 列表。</summary>
    public List<string> Tokenize(string text)
    {
        var result = new List<string>();
        foreach (var word in BasicTokenize(text))
        {
            if (word.Length > MaxInputCharsPerWord)
            {
                result.Add("[UNK]");
                continue;
            }
            result.AddRange(WordPiece(word));
        }
        return result;
    }

    private int GetId(string token) => _vocab.TryGetValue(token, out var id) ? id : _unkId;

    /// <summary>BertPreTokenizer 等价实现：中文按字、标点单切、英文按空白切。</summary>
    private static IEnumerable<string> BasicTokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (IsCjk(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return c.ToString();
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else if (IsPunctuation(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return c.ToString();
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private IEnumerable<string> WordPiece(string word)
    {
        if (word.Length == 1)
        {
            yield return _vocab.ContainsKey(word) ? word : "[UNK]";
            yield break;
        }

        var start = 0;
        var output = new List<string>();
        var bad = false;
        while (start < word.Length)
        {
            var end = word.Length;
            string cur = null;
            while (start < end)
            {
                var sub = word.Substring(start, end - start);
                if (start > 0) sub = "##" + sub;
                if (_vocab.ContainsKey(sub)) { cur = sub; break; }
                end--;
            }
            if (cur == null) { bad = true; break; }
            output.Add(cur);
            start = end;
        }

        if (bad)
        {
            yield return "[UNK]";
        }
        else
        {
            foreach (var t in output) yield return t;
        }
    }

    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;

    private static bool IsPunctuation(char c)
    {
        if (char.IsPunctuation(c) || char.IsSymbol(c)) return true;
        return (c >= 0x3000 && c <= 0x303F) || (c >= 0xFF00 && c <= 0xFF65);
    }
}
