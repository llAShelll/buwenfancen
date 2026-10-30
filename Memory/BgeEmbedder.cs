using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BuWenFanChen.Memory;

/// <summary>
/// BGE-small-zh-v1.5 嵌入器（ONNX Runtime）。
/// CLS pooling + L2 normalize，输出 512 维向量。
/// 懒加载：第一次用到才初始化，模型缺失时返回 null（退回 BM25）。
/// </summary>
public sealed class BgeEmbedder : IDisposable
{
    public const int Dim = 512;

    private readonly InferenceSession _session;
    private readonly BgeTokenizer _tokenizer;
    private readonly string _inputIdsName;
    private readonly string _attentionMaskName;
    private readonly string _tokenTypeIdsName;
    private readonly string _outputName;

    public bool IsReady => _session != null;

    public BgeEmbedder(string modelPath, string tokenizerPath)
    {
        _tokenizer = new BgeTokenizer(tokenizerPath);

        var options = new SessionOptions();
        options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, options);

        // 自适应输入输出名（不同导出版本可能不同）
        _inputIdsName = FindName("input_ids");
        _attentionMaskName = FindName("attention_mask");
        _tokenTypeIdsName = FindName("token_type_ids");
        _outputName = _session.OutputMetadata.Keys.First();
    }

    private string FindName(string preferred)
    {
        foreach (var k in _session.InputMetadata.Keys)
            if (k.Equals(preferred, StringComparison.OrdinalIgnoreCase)) return k;
        return preferred;
    }

    /// <summary>把文本编码成归一化后的 512 维向量（余弦相似度直接用点积）。</summary>
    public float[] Embed(string text)
    {
        var (ids, mask, types) = _tokenizer.Encode(text);
        var seqLen = ids.Length;

        var inputIds = new DenseTensor<long>(new[] { 1, seqLen });
        var attentionMask = new DenseTensor<long>(new[] { 1, seqLen });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, seqLen });
        for (var i = 0; i < seqLen; i++)
        {
            inputIds[0, i] = ids[i];
            attentionMask[0, i] = mask[i];
            tokenTypeIds[0, i] = types[i];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, attentionMask),
        };
        // 有些导出没有 token_type_ids
        if (_session.InputMetadata.ContainsKey(_tokenTypeIdsName))
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIdsName, tokenTypeIds));

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();   // [1, seqLen, 512]

        // CLS pooling：取第 0 个 token
        var vec = new float[Dim];
        for (var d = 0; d < Dim; d++) vec[d] = output[0, 0, d];

        // L2 normalize
        double norm = 0;
        for (var d = 0; d < Dim; d++) norm += vec[d] * (double)vec[d];
        norm = Math.Sqrt(norm);
        if (norm > 1e-8)
            for (var d = 0; d < Dim; d++) vec[d] = (float)(vec[d] / norm);

        return vec;
    }

    public void Dispose() => _session?.Dispose();
}

/// <summary>
/// BGE 嵌入器的懒加载单例。模型文件缺失或加载失败时返回 null（检索退回纯 BM25）。
/// </summary>
public static class EmbedderProvider
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    private static BgeEmbedder _instance;
    private static bool _tried;
    private static readonly object _lock = new();

    public static BgeEmbedder Get()
    {
        lock (_lock)
        {
            if (_tried) return _instance;
            _tried = true;
            try
            {
                if (File.Exists(MemoryPaths.BgeModelPath) && File.Exists(MemoryPaths.BgeTokenizerPath))
                {
                    // ★ 显式绑定插件目录里的 onnxruntime.dll：
                    //   游戏里可能同时有别的 MOD 也带 onnxruntime（语音 MOD 的 ASR 就是），
                    //   不指定路径时会被系统 PATH / 已加载的同名库抢走，版本不匹配就会崩。
                    //   这里用「临时改 DLL 搜索目录 + 预加载绝对路径」把我们的那份先占上。
                    try
                    {
                        var pluginDir = System.IO.Path.GetDirectoryName(typeof(MemoryPaths).Assembly.Location) ?? ".";
                        var nativeDll = System.IO.Path.Combine(pluginDir, "onnxruntime.dll");
                        if (File.Exists(nativeDll))
                        {
                            // 路径隔离：临时改 DLL 搜索目录 + 绝对路径预加载
                            // （官方 NativeLibraryConfig 在 1.20.1 / 1.29.0 都不存在，只能用这个办法）
                            try { SetDllDirectory(pluginDir); } catch { }                            try { System.Runtime.InteropServices.NativeLibrary.Load(nativeDll); } catch { }
                            try { SetDllDirectory(null); } catch { }

                            // 如果进程里已经加载了「别人」的 onnxruntime（比如语音 MOD 的 ASR），
                            // 同名模块会被 Windows 复用 → 版本不匹配 → AccessViolation 崩溃。
                            // 这时主动降级：不用 BGE（退回 BM25），不冒险。
                            var foreign = FindForeignOnnxLoad(pluginDir);
                            if (foreign != null)
                            {
                                try
                                {
                                    BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                                        .LogWarning($"[语义] 检测到别的 onnxruntime 已加载：{foreign}"
                                                  + " → 为避免版本冲突，本次不用 BGE（退回关键词检索）");
                                }
                                catch { }
                                return null;
                            }
                        }
                    }
                    catch { }

                    _instance = new BgeEmbedder(MemoryPaths.BgeModelPath, MemoryPaths.BgeTokenizerPath);
                }
            }
            catch
            {
                _instance = null;
            }
            return _instance;
        }
    }

    public static bool IsReady => Get() != null;

    /// <summary>进程里是否已经加载了「不是我们插件目录」的 onnxruntime.dll（返回那个路径）。</summary>
    private static string FindForeignOnnxLoad(string ourDir)
    {
        try
        {
            var ourPath = System.IO.Path.Combine(ourDir, "onnxruntime.dll");
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                if (!m.ModuleName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(m.FileName, ourPath, StringComparison.OrdinalIgnoreCase)) continue;
                return m.FileName;
            }
        }
        catch { }
        return null;
    }

    /// <summary>文本 → 归一化向量；模型不可用时返回 null。</summary>
    public static float[] Embed(string text)
    {
        var e = Get();
        if (e == null) return null;
        try { return e.Embed(text); }
        catch { return null; }
    }
}
