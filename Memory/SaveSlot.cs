using System.Text;

namespace BuWenFanChen.Memory;

/// <summary>
/// 存档识别（F1 不同存档适配）。
///
/// 游戏的存档结构（实测 2026-10-24）：
///   {LocalLow}\Nuverse\WorldApart\StorageV1\{版本号}\{存档 GUID}\
/// 例：StorageV1\1.0\9c033496-d784-4c10-a890-897ebac9fa67
///
/// 我们做的事：
///   ① 启动时扫一遍，按「最近修改」判断当前是哪个存档
///   ② 把 MemoryPaths.Scope 设为那个 GUID
///   ③ 记忆/角色卡跟着隔离到 {Root}\{Scope}\
///
/// ★ 为什么靠「最近修改」：游戏没把「当前存档」暴露给我们，
///   只能看哪个存档目录最近被写过。进游戏读档的头几秒可能判断错，
///   所以控制台里会显示「当前判定为 X」，并允许手动切换。
///
/// ★ 老数据迁移：开了 Scope 以后路径从 {Root}\memory 变成 {Root}\{Scope}\memory，
///   首次检测成功时把老目录整体搬过去（只搬一次），玩家无感。
/// </summary>
public static class SaveSlot
{
    /// <summary>当前判定的存档 GUID（空 = 没检测到，用全局目录）</summary>
    public static string CurrentId { get; private set; } = "";

    /// <summary>当前判定是否来自用户手动指定（手动指定的不会被自动检测覆盖）</summary>
    public static bool ManuallyPicked { get; private set; }

    /// <summary>游戏存档根：{LocalLow}\Nuverse\WorldApart\StorageV1</summary>
    public static string StorageRoot
    {
        get
        {
            try
            {
                var localLow = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow");
                if (!Directory.Exists(localLow)) return null;

                // 公司名可能变（Nuverse / DefaultCompany），扫一层找 WorldApart
                foreach (var company in Directory.GetDirectories(localLow))
                {
                    var wa = Path.Combine(company, "WorldApart", "StorageV1");
                    if (Directory.Exists(wa)) return wa;
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>列出所有存档：[{Id, Version, Path, LastWrite}]，按最近修改降序</summary>
    public static List<(string Id, string Version, string Path, DateTime LastWrite)> List()
    {
        var result = new List<(string Id, string Version, string Path, DateTime LastWrite)>();
        var root = StorageRoot;
        if (root == null || !Directory.Exists(root)) return result;

        try
        {
            // 结构：StorageV1\{版本}\{GUID}
            foreach (var verDir in Directory.GetDirectories(root))
            {
                var version = Path.GetFileName(verDir);
                foreach (var slotDir in Directory.GetDirectories(verDir))
                {
                    var id = Path.GetFileName(slotDir);
                    // GUID 形如 xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
                    if (id.Length < 20 || !id.Contains('-')) continue;
                    DateTime last = DateTime.MinValue;
                    try { last = Directory.GetLastWriteTime(slotDir); } catch { }
                    result.Add((id, version, slotDir, last));
                }
            }
        }
        catch { }

        return result.OrderByDescending(x => x.LastWrite).ToList();
    }

    /// <summary>
    /// 自动检测：按「最近修改」选一个，设为当前 Scope。
    /// 只在还没手动指定时生效。返回是否有变化。
    /// </summary>
    public static bool AutoDetect()
    {
        if (ManuallyPicked) return false;

        var slots = List();
        if (slots.Count == 0) return false;

        var best = slots[0].Id;
        if (best == CurrentId) return false;

        Apply(best, manual: false);
        return true;
    }

    /// <summary>手动指定存档（控制台用）</summary>
    public static bool Pick(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var slots = List();
        if (!slots.Any(s => s.Id == id)) return false;
        Apply(id, manual: true);
        return true;
    }

    /// <summary>取消手动指定，回到自动检测</summary>
    public static void ClearManual()
    {
        ManuallyPicked = false;
        AutoDetect();
    }

    private static void Apply(string id, bool manual)
    {
        var changed = CurrentId != id;
        CurrentId = id;
        ManuallyPicked = manual;
        MemoryPaths.Scope = id;

        if (changed)
        {
            Log($"[存档] 当前判定为 {id}（{(manual ? "手动指定" : "最近修改")}）");
            MigrateLegacyData();
        }
    }

    /// <summary>
    /// 把老路径的全局数据搬到当前存档目录（只搬一次）。
    /// {Root}\memory        → {Root}\{Scope}\memory
    /// {Root}\card          → {Root}\{Scope}\card
    /// </summary>
    private static void MigrateLegacyData()
    {
        if (string.IsNullOrEmpty(CurrentId)) return;
        try
        {
            var root = MemoryPaths.Root;
            var scopeDir = Path.Combine(root, CurrentId);

            // ★ 只迁移 memory：角色卡是全局的（不分存档），不参与迁移
            foreach (var sub in new[] { "memory" })
            {
                var oldPath = Path.Combine(root, sub);
                var newPath = Path.Combine(scopeDir, sub);
                if (!Directory.Exists(oldPath)) continue;
                if (Directory.Exists(newPath)) continue;   // 新目录已有数据，不乱动

                try
                {
                    Directory.CreateDirectory(scopeDir);
                    Directory.Move(oldPath, newPath);
                    Log($"[存档] 老数据已迁移：{sub} → {CurrentId}\\{sub}\\");
                }
                catch (Exception e)
                {
                    Log($"[存档] 迁移 {sub} 失败（不影响使用，只是老数据留原处）：{e.Message}");
                }
            }
        }
        catch { }
    }

    /// <summary>给控制台用的状态描述</summary>
    public static string Describe()
    {
        var slots = List();
        var cur = string.IsNullOrEmpty(CurrentId) ? "（未检测到，用全局目录）" : CurrentId;
        return $"当前存档：{cur}（{(ManuallyPicked ? "手动指定" : "自动判定")}）｜共发现 {slots.Count} 个存档";
    }

    private static void Log(string msg)
    {
        try { MemoryEngine.Log?.Invoke(msg, false); } catch { }
    }
}
