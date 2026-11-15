using System;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;

namespace BuWenFanChen.Diagnostics;

/// <summary>
/// ★ 2026-11-10：游戏更新（11-10 16:34：WorldApart.exe / GameAssembly.dll / UnityPlayer.dll 全换）
/// 之后「四选一选项 + 自动好感度」同时失效的排查设施。
///
/// 为什么这两项会一起哑：它们的唯一调用点都在
/// AddNpcChatMessagePatch.Prefix / Postfix 里（OptionsProbe.TakeAndStrip、
/// MainIntimacyProbe.ParseAndSubmit）。这个 patch 一断，两项一起失效 —— 而
/// 本插件是显式注册模式，patch 挂不上时 Harmony 只是静默跳过，日志里啥也看不见。
///
/// 本文件分两块：
///   ① 启动体检（只跑一次）：游戏文件基线 / 配置快照 / 本插件实际挂上的 patch 清单 /
///      关键目标方法签名（有没有变）
///   ② 运行时链路打点（每轮最多几行）：AddNpcChatMessage 进入 / tail 注入 / 选项解析 / HUD.Show
///
/// 纪律：**绝不每帧打日志**。2026-11-08 踩过「诊断写在每帧路径里 → 聊十分钟日志 44MB、
/// 越玩越卡」的坑。Link() 带总量上限，超了自动静默。
/// </summary>
internal static class HealthProbe
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("不问凡尘·体检");

    // ==================== 运行时打点 ====================

    private static int _linkCount;
    private const int LinkCap = 400;

    /// <summary>运行时链路打点（每轮最多几行）。超过上限自动静默，防止刷日志。</summary>
    internal static void Link(string stage, string detail)
    {
        try
        {
            if (Interlocked.Increment(ref _linkCount) > LinkCap)
            {
                if (_linkCount == LinkCap + 1)
                    Log.LogInfo("[链路] 打点已达 " + LinkCap + " 条上限 → 后续静默（重新进游戏恢复）");
                return;
            }
            Log.LogInfo("[链路] " + stage + "：" + detail);
        }
        catch { }
    }

    /// <summary>只打一次的打点（启动/首次路径用）。</summary>
    internal static void Once(ref bool flag, string text)
    {
        try
        {
            if (flag) return;
            flag = true;
            Log.LogInfo(text);
        }
        catch { }
    }

    // ==================== ① 启动体检 ====================

    /// <summary>启动体检总入口。所有异常都被吞掉——体检绝不能拖垮插件加载。</summary>
    internal static void Run(Harmony harmony)
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== 不问凡尘 · 启动体检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====");
        sb.AppendLine("插件 " + PluginInfo.NAME + " v" + PluginInfo.VERSION + "（BUILD_TAG " + PluginInfo.BUILD_TAG + "）");

        try { AppendGameBaseline(sb); } catch (Exception e) { sb.AppendLine("[体检] 游戏基线失败：" + e.Message); }
        try { AppendConfig(sb); } catch (Exception e) { sb.AppendLine("[体检] 配置快照失败：" + e.Message); }
        try { AppendPatchHealth(sb, harmony); } catch (Exception e) { sb.AppendLine("[体检] patch 状态失败：" + e.Message); }
        try { AppendTargets(sb); } catch (Exception e) { sb.AppendLine("[体检] 目标方法失败：" + e.Message); }
        try { AppendExitHookCandidates(sb); } catch (Exception e) { sb.AppendLine("[体检] 退出 hook 扫描失败：" + e.Message); }
        try { AppendBedroomOverlayMethods(sb); } catch (Exception e) { sb.AppendLine("[体检] NpcBedroomOverlay 扫描失败：" + e.Message); }

        var text = sb.ToString();
        foreach (var line in text.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) Log.LogInfo(line.TrimEnd());
        DumpToFile(text);
    }

    private static void AppendGameBaseline(StringBuilder sb)
    {
        // ★ 已知基线（2026-11-09 之前，游戏更新前）：
        //   WorldApart.exe 679,688 / GameAssembly.dll 165,978,888 / UnityPlayer.dll 30,747,848
        //   11-10 16:34 全部被更新（大小/时间都会变）——这行日志就是为了让「到底哪版游戏」一眼可见。
        sb.AppendLine("[体检] 游戏文件（基线=11-09 前：WorldApart.exe 679,688 / GameAssembly 165,978,888 / UnityPlayer 30,747,848）");
        var dataDir = UnityEngine.Application.dataPath;                 // ...\A1\WorldApart_Data
        var gameDir = string.IsNullOrEmpty(dataDir) ? null : Path.GetDirectoryName(dataDir);
        foreach (var name in new[] { "WorldApart.exe", "GameAssembly.dll", "UnityPlayer.dll" })
        {
            try
            {
                if (gameDir == null) { sb.AppendLine("[体检]   " + name + "：拿不到 gameDir"); continue; }
                var p = Path.Combine(gameDir, name);
                if (!File.Exists(p)) { sb.AppendLine("[体检]   " + name + "：不存在（" + p + "）"); continue; }
                var fi = new FileInfo(p);
                sb.AppendLine("[体检]   " + name + "：" + fi.Length.ToString("N0") + " 字节 / "
                              + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch (Exception e) { sb.AppendLine("[体检]   " + name + "：读取失败 " + e.Message); }
        }
    }

    private static void AppendConfig(StringBuilder sb)
    {
        sb.AppendLine("[体检] 关键开关："
            + "optionsEnabled=" + PromptConfig.OptionsEnabled
            + " / autoIntimacyEnabled=" + PromptConfig.AutoIntimacyEnabled
            + " / intimacyInMainRequest=" + PromptConfig.IntimacyInMainRequest
            + " / hideNsfw=" + PromptConfig.HideNsfw
            + " / replyLenTotal=" + PromptConfig.ReplyLenTotal);
    }

    private static void AppendPatchHealth(StringBuilder sb, Harmony harmony)
    {
        sb.AppendLine("[体检] 本插件实际挂上的 patch（重点看有没有 AddNpcChatMessage / CompleteBudgeted）：");
        int n = 0;
        try
        {
            foreach (var m in harmony.GetPatchedMethods())
            {
                n++;
                string desc;
                try { desc = (m.DeclaringType == null ? "?" : m.DeclaringType.FullName) + "." + m.Name; }
                catch { desc = "（方法名读取失败）"; }
                sb.AppendLine("[体检]   ✓ " + desc);
            }
        }
        catch (Exception e)
        {
            sb.AppendLine("[体检]   遍历 patch 失败（" + e.GetType().Name + "：" + e.Message + "）");
        }
        sb.AppendLine("[体检]   合计 " + n + " 个方法被本插件 patch");
    }

    private static void AppendTargets(StringBuilder sb)
    {
        sb.AppendLine("[体检] 关键目标方法解析（参数签名变了就是游戏更新导致的）：");
        AppendTarget(sb, typeof(Game.Model.NpcModel), "AddNpcChatMessage");
        AppendTarget(sb, typeof(Game.Model.AinpcRuntime), "CompleteBudgeted");
        AppendTarget(sb, typeof(Game.NpcDialoguePanel), "OnHide");
        AppendTarget(sb, typeof(Game.NpcDialoguePanel), "PlayCloseAnimThenHide");
    }

    /// <summary>★ 2026-11-10：扫描 Game 程序集，找出「退出/面板关闭」相关方法属于哪个类。
    ///   目的：家园退出对话时选项框会残留（不走 NpcDialoguePanel.OnHide），
    ///   想 hook 真正的退出点（如 BackToParentSpace / OnUIDestroy / ClosePanelInstance）。
    ///   metadata 里方法名和类名分开存，所以必须运行时遍历类型才能拿到「类.方法」。</summary>
    /// <summary>★ 2026-11-10：打印 NpcBedroomOverlay（家园卧室对话覆盖层）的方法列表。
    ///   UI 探针证实家园对话走的是它（不是 NpcDialoguePanel），
    ///   需要知道它的「关闭/隐藏」方法名才能真正 hook 到家园退出点。</summary>
    private static void AppendBedroomOverlayMethods(StringBuilder sb)
    {
        try
        {
            var t = typeof(Game.UI.UPFLogic.NpcBedroom.NpcBedroomOverlay);
            sb.AppendLine("[体检] NpcBedroomOverlay 方法列表（找关闭/隐藏方法）：");
            int n = 0;
            foreach (var m in AccessTools.GetDeclaredMethods(t))
            {
                if (m == null) continue;
                var ps = new StringBuilder();
                try
                {
                    foreach (var p in m.GetParameters())
                        ps.Append(p.ParameterType.Name).Append(' ');
                }
                catch { ps.Append('?'); }
                sb.AppendLine("[体检]   " + m.Name + "(" + ps.ToString().TrimEnd() + ")");
                n++;
            }
            sb.AppendLine("[体检]   （共 " + n + " 个方法）");
        }
        catch (Exception e)
        {
            sb.AppendLine("[体检] NpcBedroomOverlay 不可用/扫描失败：" + e.GetType().Name + " " + e.Message);
        }
    }

    private static void AppendExitHookCandidates(StringBuilder sb)
    {
        var asm = typeof(Game.Model.NpcModel).Assembly;
        var hits = new StringBuilder();
        int n = 0;
        try
        {
            foreach (var t in AccessTools.GetTypesFromAssembly(asm))
            {
                if (t == null) continue;
                string tn;
                try { tn = t.FullName ?? ""; } catch { continue; }
                if (tn.IndexOf("UI", StringComparison.Ordinal) < 0
                    && tn.IndexOf("Panel", StringComparison.Ordinal) < 0
                    && tn.IndexOf("Space", StringComparison.Ordinal) < 0) continue;
                foreach (var m in AccessTools.GetDeclaredMethods(t))
                {
                    if (m == null) continue;
                    var mn = m.Name;
                    if (mn != "BackToParentSpace" && mn != "OnUIDestroy"
                        && mn != "ClosePanelInstance" && mn != "OnPanelClosed"
                        && mn != "OnPanelOpened") continue;
                    var ps = new StringBuilder();
                    try
                    {
                        foreach (var p in m.GetParameters())
                            ps.Append(p.ParameterType.Name).Append(' ');
                    }
                    catch { ps.Append('?'); }
                    hits.Append("\n[体检]   ").Append(tn).Append('.').Append(mn)
                        .Append('(').Append(ps.ToString().TrimEnd()).Append(')');
                    n++;
                    if (n >= 30) break;
                }
                if (n >= 30) break;
            }
        }
        catch (Exception e)
        {
            sb.AppendLine("[体检] 退出 hook 扫描中途失败：" + e.GetType().Name + " " + e.Message);
        }
        sb.AppendLine("[体检] 退出/面板关闭候选 hook 点（共 " + n + " 个）：");
        sb.Append(hits);
    }

    private static void AppendTarget(StringBuilder sb, Type t, string method)
    {
        try
        {
            var m = AccessTools.Method(t, method);
            if (m == null)
            {
                sb.AppendLine("[体检]   ✗ " + t.Name + "." + method + " —— 找不到！");
                return;
            }
            var ps = new StringBuilder();
            try
            {
                foreach (var p in m.GetParameters())
                    ps.Append(p.ParameterType.Name).Append(' ').Append(p.Name).Append(", ");
            }
            catch { ps.Append("(取参数失败)"); }
            sb.AppendLine("[体检]   ✓ " + t.Name + "." + method + "(" + ps.ToString().TrimEnd(' ', ',') + ")");
        }
        catch (Exception e)
        {
            sb.AppendLine("[体检]   ✗ " + t.Name + "." + method + " —— 解析异常 " + e.GetType().Name + "：" + e.Message);
        }
    }

    private static void DumpToFile(string text)
    {
        try
        {
            var dir = Path.Combine(BuWenFanChen.Memory.MemoryPaths.Root, "_diag");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "health_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            Log.LogInfo("[体检] 报告已落盘：" + path);
        }
        catch (Exception e)
        {
            Log.LogWarning("[体检] 报告落盘失败：" + e.Message);
        }
    }
}
