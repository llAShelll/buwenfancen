namespace BuWenFanChen.Memory;

/// <summary>
/// 记忆路径的统一入口（★ 接口约定：全项目只有这里算路径，禁止别处硬编码 "memory\\" / "card\\"）。
/// 存档适配：改 Scope 的值即可，不动业务代码。
/// </summary>
public static class MemoryPaths
{
    /// <summary>存档作用域占位（默认空 = 全局；以后填存档 GUID 实现按存档隔离）</summary>
    public static string Scope { get; set; } = "";

    private static string _root;

    /// <summary>
    /// 数据根目录。默认相对插件目录的 "data"；
    /// 由 Plugin 启动时调用 Init 设为游戏存档区（Application.persistentDataPath）。
    /// 不直接依赖 UnityEngine，方便本地单测。
    /// </summary>
    public static string Root
    {
        get
        {
            if (_root != null) return _root;
            _root = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(MemoryPaths).Assembly.Location) ?? ".",
                "data");
            return _root;
        }
    }

    /// <summary>由 Plugin 启动时设置数据根目录。</summary>
    public static void Init(string root)
    {
        _root = root;
    }

    /// <summary>角色记忆目录：{Root}\{Scope}\memory\{NpcCfgId}\</summary>
    public static string MemDir(string npcCfgId)
    {
        var p = Root;
        if (!string.IsNullOrEmpty(Scope))
            p = System.IO.Path.Combine(p, Scope);
        return System.IO.Path.Combine(p, "memory", Sanitize(npcCfgId));
    }

    /// <summary>
    /// 角色卡路径：{Root}\card\{NpcCfgId}.json
    ///
    /// ★ 角色卡【不分存档】（2026-10-24 主人定）：
    ///   角色卡描述的是「这个角色是什么样」，不管玩家开几局游戏都一样，
    ///   所以全局共享 —— A 存档配好的卡，B 存档直接能用。
    ///   只有记忆需要按存档隔离（见 MemDir）。
    /// </summary>
    public static string CardPath(string npcCfgId)
    {
        return System.IO.Path.Combine(Root, "card", Sanitize(npcCfgId) + ".json");
    }

    /// <summary>BGE 模型目录（插件目录\model\bge）</summary>
    public static string BgeModelDir
    {
        get
        {
            var pluginDir = System.IO.Path.GetDirectoryName(typeof(MemoryPaths).Assembly.Location) ?? ".";
            return System.IO.Path.Combine(pluginDir, "model", "bge");
        }
    }

    public static string BgeModelPath => System.IO.Path.Combine(BgeModelDir, "model_int8.onnx");
    public static string BgeTokenizerPath => System.IO.Path.Combine(BgeModelDir, "tokenizer.json");

    /// <summary>chunk 向量索引文件：{MemDir}\chunks.vec</summary>
    public static string VectorPath(string npcCfgId) =>
        System.IO.Path.Combine(MemDir(npcCfgId), "chunks.vec");

    /// <summary>清理目录名里的非法字符（防 NpcCfgId 含路径分隔符）</summary>
    private static string Sanitize(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrEmpty(name) ? "_" : name;
    }
}
