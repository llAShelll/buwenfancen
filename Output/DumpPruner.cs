using System.IO;

namespace BuWenFanChen;

/// <summary>
/// dump 目录清理器（2026-10-24）。
///
/// 背景：原来每轮对话写 2 个文件、永不删除。
/// prompt_*.txt 平均 22 KB，且内容是完整提示词 + 玩家这一轮说的话（明文）。
/// 长期玩家目录会堆到几千个文件、几百 MB，还有隐私风险。
///
/// 策略：每 20 次写入清一次（避免每次都扫目录），保留最近 N 个文件，其余删最旧。
/// </summary>
internal static class DumpPruner
{
    private static int _writes;
    private const int PRUNE_EVERY = 20;

    internal static void Prune(string dir, int keep)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || keep <= 0) return;
            if (++_writes % PRUNE_EVERY != 0) return;
            if (!Directory.Exists(dir)) return;

            var files = Directory.GetFiles(dir, "*.txt");
            if (files.Length <= keep) return;

            var ordered = new FileInfo[files.Length];
            for (var i = 0; i < files.Length; i++) ordered[i] = new FileInfo(files[i]);
            Array.Sort(ordered, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            var removed = 0;
            for (var i = keep; i < ordered.Length; i++)
            {
                try
                {
                    ordered[i].Delete();
                    removed++;
                }
                catch { }
            }

            if (removed > 0)
            {
                try
                {
                    BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                        .LogInfo($"[输出] dump 清理：保留最近 {keep} 个，删除 {removed} 个旧文件");
                }
                catch { }
            }
        }
        catch { }
    }
}
