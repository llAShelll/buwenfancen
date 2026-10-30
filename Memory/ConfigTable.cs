using System.Collections;
using System.Text;

namespace BuWenFanChen.Memory;

/// <summary>
/// 游戏配置表读取（2026-10-25）。
///
/// 探针查明配置表入口是 <c>LubanDatas.Tables</c>，它继承 Singleton&lt;Tables&gt;，
/// 静态属性名是 <c>T</c>（不是 Instance）。
///
/// 表结构：
///   Tables.TbNpcBaseCfg  → 角色配置（Name / SectId / RealmId / Age / Gender …）
///   Tables.TbSect        → 宗门（Name …）
///   Tables.TbCultivateRealm → 境界（Name …）
///   Tables.TbItemType    → 物品类型（Name …，用来把「兴趣 ID」转成「文房」）
///
/// 用途：补游戏原文【没发】的信息 —— 初始名、兴趣名、宗门、境界、年龄。
/// 游戏原文的「## 角色属性」只有 身份/性格/外貌/善恶，缺这几项。
/// </summary>
public static class ConfigTable
{
    /// <summary>
    /// ★ 2026-10-25 最关键的一行。
    /// Luban 的字段都是 private 的 backing field（_npcName_k__BackingField），
    /// 默认反射只查 public，**根本看不到**。所有反射调用必须带这个 flags。
    /// </summary>
    private const System.Reflection.BindingFlags F =
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.Static;

    /// <summary>只查实例成员（属性/字段），跟探针用的 flags 完全一致</summary>
    public const System.Reflection.BindingFlags FI =
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Instance;

    /// <summary>只查静态成员</summary>
    public const System.Reflection.BindingFlags FS =
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Static;

    /// <summary>
    /// ★ 2026-10-25 角色名缓存。
    ///   根因：LubanDatas.Tables 在【非主线程】（HTTP 控制台线程）读不到，
    ///   所以「一键改真名」在控制台点的时候 GetBaseName 全返回空。
    ///   解法：进对话时（游戏主线程）把真名缓存下来，持久化到 npcname.json，
    ///   控制台读缓存即可（正好：改名对象都是「聊过的角色」，都有缓存）。
    /// </summary>
    private static readonly Dictionary<string, string> NameCache = new();
    private static string NameCachePath =>
        System.IO.Path.Combine(PromptConfig.Dir, "npcname.json");

    // ==================== 静态表：npcId → 真名（2026-10-29） ====================
    /// <summary>
    /// 官方 NPC 名单（id → 真名）。
    /// ★ 为什么需要它：游戏里换「化身/皮肤」会连 NPC 名字都改（「奈宝·芊儿4K2」），
    ///   配置表 TbNpcBaseCfg.NpcName 拿到的就是这种带皮肤前缀的名字，
    ///   剥前缀只能得到「芊儿4K2」，去不掉尾巴上的版本号，也不保证对。
    ///   真名在语音 MOD 的那份 csv 里（游戏官方名单）：references\npcs\npc_id_name.csv
    ///   来源优先级（后加载的覆盖先加载的）：
    ///     ③ 内置（嵌在 DLL 里，29 KB，热补丁也能用）
    ///     ① config\npc-id-name.csv（玩家可自己换一份）
    ///     ② 游戏目录 A1IndexTTSMod\references\npcs\npc_id_name.csv（装了语音 MOD 就是最新的）
    /// </summary>
    private static readonly Dictionary<string, string> StaticName = new();
    private static bool _staticLoaded;

    public static void LoadStaticNames()
    {
        if (_staticLoaded) return;
        _staticLoaded = true;

        // ③ 内置打底
        try
        {
            var asm = typeof(ConfigTable).Assembly;
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.EndsWith("npc-id-name.csv", StringComparison.OrdinalIgnoreCase)) continue;
                using var st = asm.GetManifestResourceStream(res);
                if (st == null) continue;
                using var rd = new System.IO.StreamReader(st, Encoding.UTF8);
                LoadStaticCsv(rd.ReadToEnd());
            }
        }
        catch { }

        // ① config 自定义
        try
        {
            var p = System.IO.Path.Combine(PromptConfig.Dir, "npc-id-name.csv");
            if (System.IO.File.Exists(p)) LoadStaticCsv(System.IO.File.ReadAllText(p, Encoding.UTF8));
        }
        catch { }

        // ② 语音 MOD 目录（最新）
        try
        {
            var p = FindTtsNameCsv();
            if (p != null) LoadStaticCsv(System.IO.File.ReadAllText(p, Encoding.UTF8));
        }
        catch { }
    }

    private static string FindTtsNameCsv()
    {
        try
        {
            // Application.dataPath = <游戏根>\WorldApart_Data
            var data = UnityEngine.Application.dataPath;
            if (string.IsNullOrEmpty(data)) return null;
            var root = System.IO.Path.GetDirectoryName(data);
            if (string.IsNullOrEmpty(root)) return null;
            var p = System.IO.Path.Combine(root, "A1IndexTTSMod", "references", "npcs", "npc_id_name.csv");
            return System.IO.File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>解析静态表 CSV（带引号/不带引号都认），只要前两列 id,name</summary>
    private static void LoadStaticCsv(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            var cols = SplitCsvLine(line);
            if (cols.Count < 2) continue;
            var id = cols[0].Trim().Trim('"').Trim();
            var nm = cols[1].Trim().Trim('"').Trim();
            if (id.Length == 0 || !char.IsDigit(id[0])) continue;   // 跳过表头
            if (nm.Length == 0) continue;
            lock (StaticName) StaticName[id] = nm;
        }
    }

    private static List<string> SplitCsvLine(string line)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuote)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuote = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuote = true;
                else if (c == ',') { outp.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        outp.Add(sb.ToString());
        return outp;
    }

    /// <summary>静态表里的真名（没有返回空）</summary>
    public static string GetStaticName(string npcCfgId)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return "";
        LoadStaticNames();
        lock (StaticName)
            return StaticName.TryGetValue(npcCfgId, out var v) ? (v ?? "") : "";
    }

    /// <summary>静态表条数（控制台显示 / 自检用）</summary>
    public static int StaticNameCount
    {
        get { LoadStaticNames(); lock (StaticName) return StaticName.Count; }
    }

    /// <summary>启动时读名字缓存（重启后不丢）</summary>
    public static void LoadNameCache()
    {
        try
        {
            if (!System.IO.File.Exists(NameCachePath)) return;
            var node = System.Text.Json.Nodes.JsonNode.Parse(
                System.IO.File.ReadAllText(NameCachePath, Encoding.UTF8));
            if (node is not System.Text.Json.Nodes.JsonObject o) return;
            lock (NameCache)
            {
                foreach (var kv in o)
                {
                    var v = kv.Value?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(v)) NameCache[kv.Key] = v;
                }
            }
        }
        catch { }
    }

    /// <summary>把缓存里的名字写盘（进对话时调用，主线程）</summary>
    public static void SaveNameCache()
    {
        try
        {
            var o = new System.Text.Json.Nodes.JsonObject();
            lock (NameCache)
            {
                foreach (var kv in NameCache) o[kv.Key] = kv.Value;
            }
            System.IO.File.WriteAllText(NameCachePath, o.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// 皮肤名 → cfgId 映射（迁移 1.1.2 数据时反查角色 ID 用）。
    /// 1.1.2 的卡/记忆文件名是「皮肤名」（某某_某角色_某身份_），
    /// 配置表 npcName 也是皮肤名（某某·某角色1），把「_」换成「·」就能精确反查 ID。
    /// </summary>
    private static readonly Dictionary<string, string> SkinCache = new();
    private static string SkinCachePath =>
        System.IO.Path.Combine(PromptConfig.Dir, "npcskin.json");

    public static void LoadSkinCache()
    {
        try
        {
            if (!System.IO.File.Exists(SkinCachePath)) return;
            var node = System.Text.Json.Nodes.JsonNode.Parse(
                System.IO.File.ReadAllText(SkinCachePath, Encoding.UTF8));
            if (node is not System.Text.Json.Nodes.JsonObject o) return;
            lock (SkinCache)
            {
                foreach (var kv in o)
                {
                    var v = kv.Value?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(v)) SkinCache[kv.Key] = v;
                }
            }
        }
        catch { }
    }

    public static void SaveSkinCache()
    {
        try
        {
            var o = new System.Text.Json.Nodes.JsonObject();
            lock (SkinCache)
            {
                foreach (var kv in SkinCache) o[kv.Key] = kv.Value;
            }
            System.IO.File.WriteAllText(SkinCachePath, o.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>缓存「皮肤名 → cfgId」</summary>
    public static void CacheSkin(string skinName, string npcCfgId)
    {
        if (string.IsNullOrWhiteSpace(skinName) || string.IsNullOrEmpty(npcCfgId)) return;
        lock (SkinCache) SkinCache[skinName.Trim()] = npcCfgId;
    }

    /// <summary>按皮肤名反查 cfgId（迁移用），没有返回空</summary>
    public static string GetIdBySkin(string skinName)
    {
        if (string.IsNullOrWhiteSpace(skinName)) return "";
        lock (SkinCache)
            return SkinCache.TryGetValue(skinName.Trim(), out var id) ? id : "";
    }

    /// <summary>返回所有「皮肤名 → cfgId」条目（迁移扫描时遍历用）</summary>
    public static List<(string Skin, string Id)> GetAllSkins()
    {
        lock (SkinCache)
            return SkinCache.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>年龄 + 背景缓存（主线程读到的，供控制台「补齐世界书」在非主线程用）</summary>
    private static readonly Dictionary<string, (int Age, string Bg)> ProfileCache = new();
    private static string ProfileCachePath =>
        System.IO.Path.Combine(PromptConfig.Dir, "npcprofile.json");

    public static void LoadProfileCache()
    {
        try
        {
            if (!System.IO.File.Exists(ProfileCachePath)) return;
            var node = System.Text.Json.Nodes.JsonNode.Parse(
                System.IO.File.ReadAllText(ProfileCachePath, Encoding.UTF8));
            if (node is not System.Text.Json.Nodes.JsonObject o) return;
            lock (ProfileCache)
            {
                foreach (var kv in o)
                {
                    if (kv.Value is not System.Text.Json.Nodes.JsonObject v) continue;
                    var age = -1;
                    var bg = "";
                    try { age = v["Age"]?.GetValue<int>() ?? -1; } catch { }
                    try { bg = v["Bg"]?.GetValue<string>() ?? ""; } catch { }
                    ProfileCache[kv.Key] = (age, bg);
                }
            }
        }
        catch { }
    }

    public static void SaveProfileCache()
    {
        try
        {
            var o = new System.Text.Json.Nodes.JsonObject();
            lock (ProfileCache)
            {
                foreach (var kv in ProfileCache)
                    o[kv.Key] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["Age"] = kv.Value.Age,
                        ["Bg"] = kv.Value.Bg ?? "",
                    };
            }
            System.IO.File.WriteAllText(ProfileCachePath, o.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>主线程缓存一个角色的年龄 + 背景</summary>
    public static void CacheProfile(string npcCfgId, int age, string bg)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return;
        if (age <= 0 && string.IsNullOrWhiteSpace(bg)) return;
        lock (ProfileCache) ProfileCache[npcCfgId] = (age, bg ?? "");
    }

    /// <summary>读缓存的年龄（没有返回 -1）</summary>
    public static int GetCachedAge(string npcCfgId)
    {
        lock (ProfileCache)
            return ProfileCache.TryGetValue(npcCfgId, out var v) ? v.Age : -1;
    }

    /// <summary>读缓存的背景（没有返回空）</summary>
    public static string GetCachedBg(string npcCfgId)
    {
        lock (ProfileCache)
            return ProfileCache.TryGetValue(npcCfgId, out var v) ? (v.Bg ?? "") : "";
    }

    /// <summary>
    /// 剥皮肤前缀，得到真名。
    /// ★ 2026-10-25 探针实测：配置表 TbNpcBaseCfg 里只有 npcName（皮肤名，如「某某·某角色」），
    ///   没有 BaseName/原名字段。真名 = 皮肤名去掉「某某·」这类前缀 + 去掉重名尾号。
    ///   例：某某·某角色 → 某角色；某某·某角色1 → 某角色；某某 → 某某（没前缀，原样）。
    /// </summary>
    public static string StripSkinPrefix(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim();
        var dot = n.LastIndexOf('·');
        if (dot >= 0 && dot + 1 < n.Length)
        {
            n = n.Substring(dot + 1).Trim();
        }
        // 去重名尾号（某角色1、某角色_2）
        n = n.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd('_').Trim();
        return n;
    }

    /// <summary>外部（主线程）主动缓存一个角色的真名</summary>
    public static void CacheName(string npcCfgId, string name)
    {
        if (string.IsNullOrEmpty(npcCfgId) || string.IsNullOrWhiteSpace(name)) return;
        lock (NameCache)
        {
            NameCache[npcCfgId] = name.Trim();
        }
    }

    private static object _tables;
    private static bool _searched;
    private static DateTime _lastTryUtc = DateTime.MinValue;

    /// <summary>拿 LubanDatas.Tables 实例。
    /// ★ 2026-10-29 修：原来是「只试一次，失败就永远 null」。
    ///   玩家侧真实踩坑：控制台走 HTTP 线程先调了一次（非主线程读不到 Il2Cpp 静态属性）→
    ///   失败结果被永久记住 → 之后进对话（主线程）也不再重试 →
    ///   改名缓存 / 迁移永远抓不到角色名（日志「拿不到 TbNpcBaseCfg 表 / 枚举出 0 个」）。
    ///   现在改成：成功才永久缓存；失败只记时间，冷却 3 秒后可再试。</summary>
    public static object GetTables(StringBuilder probe = null, bool forceRetry = false)
    {
        if (_tables != null) return _tables;
        if (!forceRetry && _searched && (DateTime.UtcNow - _lastTryUtc).TotalSeconds < 3) return null;
        _searched = true;
        _lastTryUtc = DateTime.UtcNow;

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        // ① ★ Il2Cpp 侧遍历属性：typeof() 拿到的是代理类，看不到 Il2Cpp 的静态属性
        //    必须用 Il2CppType.Of<T>() 再遍历
        try
        {
            var il2t = Il2CppInterop.Runtime.Il2CppType.Of<LubanDatas.Tables>();
            if (probe != null) probe.AppendLine($"  [配置表] Il2CppType = {il2t.FullName}");
            try
            {
                var props = il2t.GetProperties();
                if (probe != null) probe.AppendLine($"  [配置表] Il2CppType 属性数 = {props.Length}");
                foreach (var p in props)
                {
                    var pn = p.Name ?? "";
                    if (probe != null) probe.AppendLine($"    属性: {pn}");
                    if (pn == "T" || pn == "Instance" || pn == "Current")
                    {
                        try
                        {
                            var v = p.GetValue(null);
                            if (v != null) { _tables = v; return _tables; }
                        }
                        catch (Exception e1)
                        {
                            if (probe != null) probe.AppendLine($"      取值失败: {e1.Message}");
                        }
                    }
                }
            }
            catch (Exception e2)
            {
                if (probe != null) probe.AppendLine($"  [配置表] GetProperties 失败: {e2.Message}");
            }

            // ★ 2026-10-29：有些游戏版本里 T 是静态【字段】而不是属性 —— 只查属性会漏，必须也查字段
            try
            {
                var fields = il2t.GetFields();
                if (probe != null) probe.AppendLine($"  [配置表] Il2CppType 字段数 = {fields.Length}");
                var fi = 0;
                foreach (var f in fields)
                {
                    var fn = f.Name ?? "";
                    // 别把两三百个字段全打进日志：只打前 60 个 + 命中名字，够定位了
                    if (probe != null && fi++ < 60) probe.AppendLine($"    字段: {fn}");
                    if (fn == "T" || fn == "Instance" || fn == "Current" || fn == "Tables" || fn == "Default")
                    {
                        try
                        {
                            var v = f.GetValue(null);
                            if (v != null) { _tables = v; return _tables; }
                        }
                        catch (Exception e2b)
                        {
                            if (probe != null) probe.AppendLine($"      字段取值失败: {e2b.Message}");
                        }
                    }
                }
            }
            catch (Exception e2c)
            {
                if (probe != null) probe.AppendLine($"  [配置表] GetFields 失败: {e2c.Message}");
            }
        }
        catch (Exception e3)
        {
            if (probe != null) probe.AppendLine($"  [配置表] Il2CppType.Of 失败: {e3.Message}");
        }

        // ② .NET 侧：泛型基类的静态属性
        try
        {
            var t = typeof(LubanDatas.Tables);
            var bt = t.BaseType;
            if (probe != null) probe.AppendLine($"  [配置表] .NET BaseType = {bt?.FullName ?? "<null>"}");
            if (bt != null)
            {
                foreach (var nm in new[] { "T", "Instance", "Current" })
                {
                    try
                    {
                        var p = bt.GetProperty(nm, flags);
                        if (p != null)
                        {
                            var v = p.GetValue(null);
                            if (v != null) { _tables = v; return _tables; }
                        }
                    }
                    catch { }
                }
            }
            // 自己身上的静态成员（属性 + 字段都要试，理由同上）
            foreach (var nm in new[] { "T", "Instance", "Current", "Default", "Tables" })
            {
                try
                {
                    var p = t.GetProperty(nm, flags);
                    if (p != null)
                    {
                        var v = p.GetValue(null);
                        if (v != null) { _tables = v; return _tables; }
                    }
                    var f = t.GetField(nm, flags);
                    if (f != null)
                    {
                        var v2 = f.GetValue(null);
                        if (v2 != null) { _tables = v2; return _tables; }
                    }
                }
                catch { }
            }
        }
        catch (Exception e4)
        {
            if (probe != null) probe.AppendLine($"  [配置表] .NET 侧失败: {e4.Message}");
        }

        if (probe != null) probe.AppendLine("  [配置表] ❌ 所有路径都没拿到 Tables");
        return _tables;
    }

    /// <summary>拿某张表的对象（如 "TbNpcBaseCfg"）</summary>
    public static object GetTable(string tableProp, StringBuilder probe = null, bool forceRetry = false)
    {
        var tables = GetTables(probe, forceRetry);
        if (tables == null) return null;

        // ★ 遍历实例属性找名字（GetProperty 对 IL2CPP 代理类不可靠：
        //   242 个属性都在，但 GetProperty(name, F) 可能命中不了）
        try
        {
            foreach (var p in tables.GetType().GetProperties(FI))
            {
                if (p.Name != tableProp) continue;
                object v = null;
                try { v = p.GetValue(tables); }
                catch (Exception e0)
                {
                    if (probe != null) probe.AppendLine($"  [配置表] GetTable({tableProp}) 取值异常: {e0.Message}");
                    continue;
                }
                if (probe != null) probe.AppendLine($"  [配置表] GetTable({tableProp}) → {(v == null ? "null" : v.GetType().Name)}");
                if (v != null) return v;
            }
            if (probe != null)
            {
                probe.AppendLine($"  [配置表] GetTable({tableProp}) 遍历属性没命中，共 {tables.GetType().GetProperties(FI).Length} 个属性");
            }
        }
        catch (Exception e1)
        {
            if (probe != null) probe.AppendLine($"  [配置表] GetTable 异常: {e1.Message}");
        }
        return null;
    }

    /// <summary>按 id 取表里的一行</summary>
    public static object GetRow(string tableProp, int id, StringBuilder probe = null)
    {
        var table = GetTable(tableProp, probe);
        if (table == null || id <= 0) return null;
        try
        {
            var tt = table.GetType();
            foreach (var m in tt.GetMethods())
            {
                if (m.Name != "get_Item" && m.Name != "Get") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                try
                {
                    object arg;
                    var pt = ps[0].ParameterType;
                    if (pt == typeof(int)) arg = id;
                    else
                    {
                        var op = pt.GetMethod("op_Explicit", new[] { typeof(int) });
                        if (op == null) continue;
                        arg = op.Invoke(null, new object[] { id });
                    }
                    var row = m.Invoke(table, new[] { arg });
                    if (row != null)
                    {
                        if (probe != null) probe.AppendLine($"  [配置表] GetRow({tableProp},{id}) 走 {m.Name}({m.GetParameters()[0].ParameterType.Name}) 成功");
                        return row;
                    }
                }
                catch (Exception e3)
                {
                    if (probe != null) probe.AppendLine($"  [配置表] GetRow 试 {m.Name} 失败: {e3.InnerException?.Message ?? e3.Message}");
                }
            }
            if (probe != null) probe.AppendLine($"  [配置表] GetRow({tableProp},{id}) 所有 get_Item/Get 都没成");
        }
        catch (Exception e4)
        {
            if (probe != null) probe.AppendLine($"  [配置表] GetRow 异常: {e4.Message}");
        }
        return null;
    }

    /// <summary>
    /// 按属性名找值。
    ///
    /// ★ 2026-10-25 实测出来的真相（三路都要试，缺一不可）：
    ///   ① <c>GetProperty(name, F)</c> —— 在「行对象」上【能用】（探针验证过）
    ///   ② <c>GetProperties()</c> 遍历 —— 在「Tables」上可用（GetProperty 返回 null）
    ///   ③ 字段遍历 —— 有些成员只暴露成字段
    ///
    ///   属性名是 NpcName，底层字段是 _npcName_k__BackingField，三种名字都要拼。
    /// </summary>
    /// <summary>
    /// 按属性名找值。
    ///
    /// ★★★ 2026-10-25 真根因（PRO 一眼看穿）：**大小写**。
    ///   Luban 属性名是 <c>NpcName</c>，但底层 backing field 是 <c>_npcName_k__BackingField</c>
    ///   （首字母小写）。之前拼的是 <c>_NpcName_k__BackingField</c>（大写 N），永远找不到。
    ///   探针硬编码小写所以能命中。
    ///
    ///   修：候选名里加「首字母小写」版本 + 遍历时忽略大小写。
    /// </summary>
    public static object ReadProp(object row, string prop)
    {
        if (row == null || string.IsNullOrEmpty(prop)) return null;
        var t = row.GetType();
        var lower = char.ToLowerInvariant(prop[0]) + prop.Substring(1);
        var names = new[]
        {
            prop,
            prop + "_k__BackingField",
            lower + "_k__BackingField",                        // ← 关键：npcName（小写）
            "_" + prop + "_k__BackingField",
            "_" + lower + "_k__BackingField",                  // ← 关键：_npcName_k__BackingField
        };

        // ① 先试 GetProperty(name, FI)
        foreach (var n in names)
        {
            try
            {
                var p = t.GetProperty(n, FI);
                if (p != null)
                {
                    var v = p.GetValue(row);
                    if (v != null) return v;
                }
            }
            catch { }
        }

        // ② 遍历属性（忽略大小写）
        try
        {
            foreach (var p in t.GetProperties(FI))
            {
                var hit = false;
                foreach (var n in names)
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                if (!hit) continue;
                try
                {
                    var v = p.GetValue(row);
                    if (v != null) return v;
                }
                catch { }
            }
        }
        catch { }

        // ③ 遍历字段（忽略大小写）
        try
        {
            foreach (var f in t.GetFields(FI))
            {
                var hit = false;
                foreach (var n in names)
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                if (!hit) continue;
                try
                {
                    var v = f.GetValue(row);
                    if (v != null) return v;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 读字符串。
    ///
    /// ★ 2026-10-25 加强：Luban 的行对象上，属性可能是 <c>L10nText</c>（本地化包装），
    ///   而真正的 string 藏在 <c>get_NpcName()</c> 这种方法里。所以顺序：
    ///     ① 先试 <c>get_XXX()</c> 方法（返回 string 就直接用）
    ///     ② 再试属性/字段（L10nText 要深度解析）
    /// </summary>
    public static string ReadString(object row, string prop)
    {
        if (row == null || string.IsNullOrEmpty(prop)) return "";
        var t = row.GetType();

        // ① 先试 get_XXX() 方法（绕过 L10nText）
        try
        {
            var m = t.GetMethod("get_" + prop,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m != null)
            {
                var rt = m.ReturnType;
                if (rt == typeof(string))
                {
                    var v = m.Invoke(row, null);
                    var sv = v?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(sv)) return sv.Trim();
                }
            }
        }
        catch { }

        // ② 属性/字段
        var raw = ReadProp(row, prop);
        if (raw == null) return "";
        return ResolveL10n(raw)?.Trim() ?? "";
    }

    /// <summary>
    /// 把 L10nText 之类的本地化包装拆成文字。
    /// 从 DLL 里看到 L10nText 和 TbStrConfig 成对出现，所以它内部一定有个 key/id；
    /// 先试着读「已经是文字」的成员，读不到就试 ToString，最后兜底返回空。
    /// </summary>
    public static string ResolveL10n(object v)
    {
        if (v == null) return "";
        try
        {
            var t = v.GetType();
            var tName = t.Name ?? "";

            // 不是 L10nText 就直接 ToString
            if (tName.IndexOf("L10nText", StringComparison.OrdinalIgnoreCase) < 0)
                return (v.ToString() ?? "").Trim();

            // ★ 2026-10-25：L10nText 的结构（探针实测）
            //     Languages = Dictionary<string, string>   { "zh": "某某", ... }
            //     Value     = "某某"                        ← 当前语言，直接可用
            //   注意：必须遍历属性，GetProperty 在代理类上不可靠。

            // ① 优先读 Value（先 GetProperty，再遍历 —— 两路都试）
            try
            {
                object pvObj = null;
                try
                {
                    var pp = t.GetProperty("Value", FI);
                    if (pp != null) pvObj = pp.GetValue(v);
                }
                catch { }
                if (pvObj == null)
                {
                    foreach (var p in t.GetProperties(FI))
                    {
                        if (p.Name != "Value") continue;
                        try { pvObj = p.GetValue(v); } catch { }
                        break;
                    }
                }
                var pv = pvObj?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(pv) && !pv.Contains("LubanDatas") && !pv.Contains("Il2CppSystem"))
                    return pv.Trim();
            }
            catch { }

            // ② 从 Languages 字典里取中文
            try
            {
                foreach (var p in t.GetProperties(FI))
                {
                    if (p.Name != "Languages") continue;
                    var dict = p.GetValue(v);
                    var got = PickFromLangDict(dict);
                    if (!string.IsNullOrEmpty(got)) return got;
                    break;
                }
            }
            catch { }

            // ③ 兜底：遍历所有属性/字段找「像文字」的值
            foreach (var p in t.GetProperties(FI))
            {
                try
                {
                    // 同样不调 GetIndexParameters()
                    var pv = p.GetValue(v)?.ToString() ?? "";
                    if (LooksLikeText(pv)) return pv.Trim();
                }
                catch { }
            }
            foreach (var f in t.GetFields(FI))
            {
                try
                {
                    var fv = f.GetValue(v)?.ToString() ?? "";
                    if (LooksLikeText(fv)) return fv.Trim();
                }
                catch { }
            }
        }
        catch { }
        return "";
    }

    /// <summary>从 L10nText.Languages 字典里挑中文（zh / zh-CN / 第一个非空）</summary>
    private static string PickFromLangDict(object dict)
    {
        if (dict == null) return "";
        try
        {
            var keys = new List<string>();
            var items = new Dictionary<string, string>();

            // 当作 IDictionary 处理（Il2Cpp 的 Dictionary 实现了 IEnumerable）
            var en = dict as System.Collections.IEnumerable;
            if (en == null) return "";
            foreach (var kv in en)
            {
                if (kv == null) continue;
                var kt = kv.GetType();
                string k = null, val = null;
                try
                {
                    foreach (var p in kt.GetProperties(FI))
                    {
                        if (p.Name == "Key" || p.Name == "_key") k = p.GetValue(kv)?.ToString();
                        if (p.Name == "Value" || p.Name == "_value") val = p.GetValue(kv)?.ToString();
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(val)) continue;
                items[k] = val;
                keys.Add(k);
            }

            // 优先中文
            foreach (var want in new[] { "zh", "zh-CN", "zh_CN", "CN", "cn", "Chinese" })
                if (items.TryGetValue(want, out var s1) && LooksLikeText(s1)) return s1.Trim();

            // 再退：第一个看起来像文字的
            foreach (var k in keys)
                if (items.TryGetValue(k, out var s2) && LooksLikeText(s2)) return s2.Trim();
        }
        catch { }
        return "";
    }

    /// <summary>判断一个字符串像不像「真正要显示的文本」（有汉字/假名/字母，不只是类型名）</summary>
    private static bool LooksLikeText(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.Length < 1 || s.Length > 200) return false;
        if (s.Contains("LubanDatas") || s.Contains("Il2CppSystem")) return false;   // 类型名不算
        foreach (var c in s)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;   // 汉字
            if (c >= 0x3040 && c <= 0x30FF) return true;   // 假名
            if (char.IsLetter(c) && c < 128) return true;  // 英文字母
        }
        return false;
    }

    /// <summary>读整数（兼容 Nullable&lt;int&gt; / TbXxxId），失败返回 -1</summary>
    public static int ReadInt(object row, string prop)
    {
        if (row == null || string.IsNullOrEmpty(prop)) return -1;
        var t = row.GetType();

        // ① 先试 get_XXX() 方法
        try
        {
            var m = t.GetMethod("get_" + prop,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m != null)
            {
                var v0 = m.Invoke(row, null);
                var n0 = UnwrapInt(v0);
                if (n0 >= 0) return n0;
            }
        }
        catch { }

        // ② 属性/字段
        var v = ReadProp(row, prop);
        return UnwrapInt(v);
    }

    /// <summary>把 int / Nullable&lt;T&gt; / TbXxxId 统一拆成 int，失败返回 -1</summary>
    private static int UnwrapInt(object v)
    {
        if (v == null) return -1;
        try
        {
            var t = v.GetType();
            var tn = t.Name ?? "";

            // Nullable<T>（.NET 标准 or Il2CppSystem.Nullable）—— 都必须遍历，GetProperty 不可靠
            var isNullable = (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
                          || tn.IndexOf("Nullable", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isNullable)
            {
                object has = null, val = null;
                try
                {
                    foreach (var p in t.GetProperties(FI))
                    {
                        var pn = p.Name ?? "";
if (pn.Equals("HasValue", StringComparison.OrdinalIgnoreCase)) has = p.GetValue(v);
else if (pn.Equals("Value", StringComparison.OrdinalIgnoreCase)) val = p.GetValue(v);
                    }
                }
                catch { }
                if (has is bool b && !b) return -1;
                return val == null ? -1 : UnwrapInt(val);
            }

            return ReadIntFromObject(v);
        }
        catch { return -1; }
    }

    /// <summary>TbXxxId 包装类型 → int（试 ToString / op_Explicit / id 字段）</summary>
    private static int ReadIntFromObject(object v)
    {
        if (v == null) return -1;
        try
        {
            if (v is int i) return i;
            var s = v.ToString() ?? "";
            if (int.TryParse(s, out var parsed)) return parsed;
            // op_Explicit → int
            var op = v.GetType().GetMethod("op_Explicit", new[] { v.GetType() });
            if (op != null && op.ReturnType == typeof(int))
                return Convert.ToInt32(op.Invoke(null, new[] { v }));
            // 试读 id 字段
            // 遍历找 id（GetProperty 对 IL2CPP 代理不可靠）
            var vt = v.GetType();
            foreach (var p in vt.GetProperties(FI))
            {
                if (p.Name != "id" && p.Name != "Id" && p.Name != "Value" && p.Name != "_id") continue;
                try
                {
                    var x = p.GetValue(v);
                    if (x != null && int.TryParse(x.ToString(), out var p2)) return p2;
                }
                catch { }
            }
            foreach (var f in vt.GetFields(FI))
            {
                if (f.Name != "id" && f.Name != "Id" && f.Name != "Value" && f.Name != "_id") continue;
                try
                {
                    var x = f.GetValue(v);
                    if (x != null && int.TryParse(x.ToString(), out var p2)) return p2;
                }
                catch { }
            }
        }
        catch { }
        return -1;
    }

    // ==================== 三个卡点的封装 ====================

    /// <summary>初始角色名（配置表 Name）。拿不到返回空串。</summary>
    public static string GetBaseName(string npcCfgId)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return "";

        // ① ★ 静态表（官方真名）—— 优先级最高：
        //   游戏换化身/皮肤会把 NpcName 变成「奈宝·芊儿4K2」，剥前缀也去不干净，
        //   只有静态表能拿到「芊儿」。这一级让卡命名 / 世界书 / 一键改真名全部跟着变对。
        try
        {
            var sn = GetStaticName(npcCfgId);
            if (!string.IsNullOrWhiteSpace(sn)) return sn;
        }
        catch { }

        // ② 缓存命中（非主线程 / 重启后都靠这个）
        lock (NameCache)
        {
            if (NameCache.TryGetValue(npcCfgId, out var cached) && !string.IsNullOrWhiteSpace(cached))
                return cached;
        }

        // ② 读配置表（只有游戏主线程能读；读到了就进缓存）
        if (!int.TryParse(npcCfgId, out var id)) return "";
        var row = GetRow("TbNpcBaseCfg", id);
        // ★ 2026-10-25 探针实测：没有 BaseName 字段，只有 npcName（皮肤名）
        //   真名 = 剥皮肤前缀（「某某·某角色」→「某角色」）
        var n = StripSkinPrefix(ReadString(row, "NpcName"));
        if (!string.IsNullOrWhiteSpace(n))
        {
            lock (NameCache) NameCache[npcCfgId] = n;
        }
        return n;
    }

    /// <summary>背景（TbNpcBaseCfg.PropPlot）</summary>
    public static string GetBackground(string npcCfgId)
    {
        if (!int.TryParse(npcCfgId, out var id)) return "";
        var row = GetRow("TbNpcBaseCfg", id);
        return ReadString(row, "PropPlot").Trim();
    }

    /// <summary>宗门名（SectId → TbSect.Name）</summary>
    public static string GetSectName(string npcCfgId)
    {
        if (!int.TryParse(npcCfgId, out var id)) return "";
        var row = GetRow("TbNpcBaseCfg", id);
        var sectId = ReadInt(row, "PropSect");
        if (sectId <= 0) return "";
        var sectRow = GetRow("TbSect", sectId);
        return ReadString(sectRow, "Name").Trim();
    }

    /// <summary>境界名（RealmId → TbCultivateRealm.Name）</summary>
    public static string GetRealmName(string npcCfgId)
    {
        if (!int.TryParse(npcCfgId, out var id)) return "";
        var row = GetRow("TbNpcBaseCfg", id);
        var realmId = ReadInt(row, "PropLayerId");
        if (realmId <= 0) return "";
        var realmRow = GetRow("TbCultivateLayer", realmId);
        return ReadString(realmRow, "Name").Trim();
    }

    /// <summary>年龄（TbNpcBaseCfg.Age）</summary>
    public static int GetAge(string npcCfgId)
    {
        if (!int.TryParse(npcCfgId, out var id)) return -1;
        return ReadInt(GetRow("TbNpcBaseCfg", id), "Age");
    }

    /// <summary>兴趣名（物品类型 ID 列表 → TbItemType.Name 列表）</summary>
    public static List<string> GetInterestNames(IEnumerable<string> ids)
    {
        var result = new List<string>();
        if (ids == null) return result;
        foreach (var s in ids)
        {
            if (!int.TryParse(s, out var id) || id <= 0) continue;
            var row = GetRow("TbItemType", id);
            var n = ReadString(row, "Name").Trim();
            if (!string.IsNullOrEmpty(n) && !result.Contains(n)) result.Add(n);
        }
        return result;
    }

    /// <summary>一次性拿全（供角色世界书用）：宗门/境界/年龄</summary>
    public static string DescribeProfile(string npcCfgId)
    {
        var parts = new List<string>();
        try
        {
            var sect = GetSectName(npcCfgId);
            if (!string.IsNullOrEmpty(sect)) parts.Add("宗门：" + sect);
            var realm = GetRealmName(npcCfgId);
            if (!string.IsNullOrEmpty(realm)) parts.Add("境界：" + realm);
            var age = GetAge(npcCfgId);
            if (age > 0) parts.Add("年龄：" + age);
        }
        catch { }
        return string.Join("。", parts);
    }
}
