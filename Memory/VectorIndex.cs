using System.Text;

namespace BuWenFanChen.Memory;

/// <summary>
/// chunk 向量索引（BGE 512 维），落盘为追加式二进制文件。
/// 格式：magic(8) + dim(int32) + count(int32) + [chunkId(int32) + dim × float32] × count
/// </summary>
public sealed class VectorIndex
{
    private const string Magic = "BWFCVEC1";
    private readonly Dictionary<int, float[]> _map = new();
    public int Count => _map.Count;

    public void Add(int chunkId, float[] vec)
    {
        if (vec == null || vec.Length == 0) return;
        _map[chunkId] = vec;
    }

    public bool Contains(int chunkId) => _map.ContainsKey(chunkId);

    public void Remove(int chunkId) => _map.Remove(chunkId);

    /// <summary>余弦相似度检索（向量已归一化，点积即余弦）。</summary>
    public List<(int ChunkId, float Sim)> Search(float[] queryVec, int topK, float minSim = 0.40f)
    {
        var result = new List<(int, float)>();
        if (queryVec == null || _map.Count == 0) return result;

        foreach (var kv in _map)
        {
            float dot = 0;
            var a = kv.Value;
            var n = Math.Min(a.Length, queryVec.Length);
            for (var i = 0; i < n; i++) dot += a[i] * queryVec[i];
            if (dot >= minSim) result.Add((kv.Key, dot));
        }

        result.Sort((x, y) => y.Item2.CompareTo(x.Item2));
        if (result.Count > topK) result.RemoveRange(topK, result.Count - topK);
        return result;
    }

    // ================= 落盘 =================

    public static VectorIndex Load(string path, int dim = BgeEmbedder.Dim)
    {
        var idx = new VectorIndex();
        if (!File.Exists(path)) return idx;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs, Encoding.ASCII);
            var magic = Encoding.ASCII.GetString(br.ReadBytes(8));
            if (magic != Magic) return idx;
            var fileDim = br.ReadInt32();
            var count = br.ReadInt32();
            if (fileDim != dim)
            {
                // 维度变了（换模型），旧索引作废
                return idx;
            }
            for (var i = 0; i < count; i++)
            {
                var chunkId = br.ReadInt32();
                var vec = new float[fileDim];
                for (var d = 0; d < fileDim; d++) vec[d] = br.ReadSingle();
                idx._map[chunkId] = vec;
            }
        }
        catch
        {
            // 损坏就当空索引
            return new VectorIndex();
        }
        return idx;
    }

    /// <summary>全量保存（先写 .tmp 再 replace）。</summary>
    public void Save(string path, int dim = BgeEmbedder.Dim)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var bw = new BinaryWriter(fs, Encoding.ASCII))
        {
            bw.Write(Encoding.ASCII.GetBytes(Magic));
            bw.Write(dim);
            bw.Write(_map.Count);
            foreach (var kv in _map)
            {
                bw.Write(kv.Key);
                var v = kv.Value;
                for (var d = 0; d < dim; d++) bw.Write(d < v.Length ? v[d] : 0f);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    public void Clear() => _map.Clear();
}
