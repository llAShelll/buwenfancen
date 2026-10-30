using System.IO.Compression;

namespace BuWenFanChen.Memory;

/// <summary>
/// 记忆导出 / 导入（阶段 3.5-B ③）。
/// · 导出：把某个角色的记忆目录打成 zip
/// · 导入：从 zip 恢复到指定角色（解压覆盖 + 作废缓存）
/// 控制台 UI 阶段 4 再接；现在提供两个静态入口。
/// </summary>
public static class MemoryTransfer
{
    /// <summary>
    /// 导出某角色记忆为 zip。成功返回 zip 路径，失败返回 null（目录不存在）。
    /// zip 放在 Root 下，文件名：记忆_{npcCfgId}_{时间戳}.zip
    /// </summary>
    public static string ExportZip(string npcCfgId)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return null;
        var dir = MemoryPaths.MemDir(npcCfgId);
        if (!System.IO.Directory.Exists(dir)) return null;

        try
        {
            var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var outPath = System.IO.Path.Combine(MemoryPaths.Root, $"记忆_{npcCfgId}_{stamp}.zip");
            if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath);
            ZipFile.CreateFromDirectory(dir, outPath);
            return outPath;
        }
        catch { return null; }
    }

    /// <summary>
    /// 从 zip 恢复某角色记忆。成功返回 null，失败返回错误信息。
    /// · 解压覆盖到目标角色目录（防止目录穿越）
    /// · 作废内存缓存和检索缓存，下次 Get 会重新读盘
    /// </summary>
    public static string ImportZip(string zipPath, string targetNpcId)
    {
        if (string.IsNullOrEmpty(targetNpcId)) return "缺少目标角色 ID";
        if (string.IsNullOrEmpty(zipPath) || !System.IO.File.Exists(zipPath)) return "zip 文件不存在";

        try
        {
            var dir = System.IO.Path.GetFullPath(MemoryPaths.MemDir(targetNpcId));
            System.IO.Directory.CreateDirectory(dir);

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;   // 目录条目

                var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, entry.FullName));
                // 防目录穿越：目标必须仍在 dir 内
                if (!dest.StartsWith(dir + System.IO.Path.DirectorySeparatorChar)) continue;

                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        catch (Exception e)
        {
            return $"解压失败：{e.Message}";
        }

        // 作废缓存，强制重新读盘
        MemoryEngine.InvalidateCache(targetNpcId);
        MemoryRetriever.Invalidate(targetNpcId);
        return null;
    }

    // ==================== 跨存档继承（F1 扩展）====================

    /// <summary>
    /// 列出「哪些存档里存着这个角色的记忆」。
    /// 扫描 {Root}\{任意目录}\memory\{npcCfgId} 这种结构。
    /// 返回 [(存档ID, 记忆目录, 最后修改)]，按最近修改降序。
    /// </summary>
    public static List<(string Scope, string Path, DateTime LastWrite)> ListScopesWith(string npcCfgId)
    {
        var result = new List<(string, string, DateTime)>();
        if (string.IsNullOrEmpty(npcCfgId)) return result;
        try
        {
            var root = MemoryPaths.Root;
            if (!System.IO.Directory.Exists(root)) return result;

            foreach (var scopeDir in System.IO.Directory.GetDirectories(root))
            {
                var scopeId = System.IO.Path.GetFileName(scopeDir);
                var memDir = System.IO.Path.Combine(scopeDir, "memory", npcCfgId);
                if (!System.IO.Directory.Exists(memDir)) continue;
                DateTime last = DateTime.MinValue;
                try { last = System.IO.Directory.GetLastWriteTime(memDir); } catch { }
                result.Add((scopeId, memDir, last));
            }
        }
        catch { }
        return result.OrderByDescending(x => x.Item3).ToList();
    }

    /// <summary>
    /// 把某个存档里这个角色的记忆，复制到【当前存档】。
    /// 会先备份当前存档里的旧记忆（如果存在），再整体替换。
    /// 成功返回 null，失败返回错误信息。
    /// </summary>
    public static string ImportFromScope(string npcCfgId, string fromScope)
    {
        if (string.IsNullOrEmpty(npcCfgId)) return "缺少角色 ID";
        if (string.IsNullOrEmpty(fromScope)) return "缺少来源存档";
        if (fromScope == MemoryPaths.Scope) return "来源就是当前存档，不用导";

        try
        {
            var root = MemoryPaths.Root;
            var src = System.IO.Path.Combine(root, fromScope, "memory", npcCfgId);
            if (!System.IO.Directory.Exists(src)) return "来源存档里没有这个角色的记忆";

            var dst = MemoryPaths.MemDir(npcCfgId);

            // ① 备份当前存档的旧记忆
            if (System.IO.Directory.Exists(dst))
            {
                var backup = dst + ".导入前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                try { CopyDir(dst, backup); } catch { }
                try { System.IO.Directory.Delete(dst, recursive: true); } catch { }
            }

            // ② 复制过来
            CopyDir(src, dst);

            // ③ 作废缓存
            MemoryEngine.InvalidateCache(npcCfgId);
            MemoryRetriever.Invalidate(npcCfgId);

            return null;
        }
        catch (Exception e) { return "导入失败：" + e.Message; }
    }

    /// <summary>递归复制目录</summary>
    private static void CopyDir(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var file in System.IO.Directory.GetFiles(src))
        {
            var target = System.IO.Path.Combine(dst, System.IO.Path.GetFileName(file));
            System.IO.File.Copy(file, target, overwrite: true);
        }
        foreach (var dir in System.IO.Directory.GetDirectories(src))
        {
            CopyDir(dir, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(dir)));
        }
    }

    /// <summary>从 zip 文件名里猜 npcCfgId（格式 记忆_{id}_{时间戳}.zip），猜不到返回 null。</summary>
    public static string TryGuessNpcId(string zipPath)
    {
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(zipPath ?? "");
            // 记忆_{id}_{stamp}
            var parts = name.Split('_');
            return parts.Length >= 2 ? parts[1] : null;
        }
        catch { return null; }
    }
}
