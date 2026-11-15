namespace BuWenFanChen;

using System;
using System.Text;
using BepInEx.Logging;

/// <summary>
/// ★ 2026-11-04：游戏内 UI 只读探针（弹窗前置调研）。
///
/// 目的：搞清楚「聊天输入框」到底叫什么、是什么组件类型、挂在哪个 Canvas 下。
/// 不创建、不修改、不点击任何东西 —— 只遍历一遍场景，把关键字命中的对象打出来。
///
/// 触发：AddNpcChatMessage 回调里（那个点一定是主线程，Unity API 才安全），只跑一次。
/// 失败只会吞掉并打一行警告，不影响游戏。
/// </summary>
internal static class UiProbe
{
    private static bool _done;

    /// <summary>只跑一次；任何时候调用都安全。</summary>
    internal static void RunOnce()
    {
        if (_done) return;
        _done = true;
        var log = Logger.CreateLogSource("不问凡尘·UI探针");
        try { Run(log); }
        catch (Exception e) { log.LogWarning($"[UI探针] 异常：{e.GetType().Name}: {e.Message}"); }
    }

    private static readonly string[] Keywords =
    {
        "输入", "聊天", "对话", "发送", "语音",
        "chat", "Chat", "input", "Input", "send", "Send", "Canvas",
    };

    private const int MaxDepth = 9;
    private const int MaxObjects = 8000;
    private const int MaxHits = 60;

    private static void Run(ManualLogSource log)
    {
        int visited = 0, hits = 0;

        // ★ 2026-11-04 12:30 修正：第一次只遍历 GetActiveScene()，Main 场景只有 7 个对象、命中 0。
        //   UI 显然在别的 additive 场景（或 DontDestroyOnLoad）里 —— 改成遍历所有已加载场景。
        try
        {
            var count = UnityEngine.SceneManagement.SceneManager.sceneCount;
            log.LogInfo($"[UI探针] 已加载场景 {count} 个，开始遍历（只读）");
            for (int i = 0; i < count; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                log.LogInfo($"[UI探针]   场景[{i}] name={sc.name} loaded={sc.isLoaded} rootCount={sc.rootCount}");
                if (!sc.isLoaded) continue;
                foreach (var root in sc.GetRootGameObjects())
                {
                    if (root == null) continue;
                    Walk(root.transform, log, 0, "", ref visited, ref hits);
                }
            }
        }
        catch (Exception e)
        {
            log.LogWarning($"[UI探针] 场景遍历异常：{e.GetType().Name}: {e.Message}");
        }

        // ★ 再兜一层：DontDestroyOnLoad 里的对象不在 sceneCount 里。
        //   Canvas 类型在 UIModule，本项目没引用 → 改用 Transform 全局扫描（CoreModule 里有）。
        //   只打印关键字命中的 + 根节点清单，不做递归，避免量太大。
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<UnityEngine.Transform>();
            log.LogInfo($"[UI探针] 全场景 Transform 共 {all.Length} 个");

            int hits2 = 0;
            foreach (var t in all)
            {
                if (t == null) continue;
                var n = t.gameObject.name ?? "";
                bool hit = false;
                foreach (var k in Keywords)
                {
                    if (n.Contains(k, StringComparison.Ordinal)) { hit = true; break; }
                }
                if (!hit) continue;
                if (++hits2 > MaxHits) break;
                log.LogInfo($"[UI探针] T#{hits2} {FullPath(t)}\n        组件: {DescribeComponents(t.gameObject)}");
            }

            // 根节点清单（帮助理解层级）
            int roots2 = 0;
            foreach (var t in all)
            {
                if (t == null || t.parent != null) continue;
                if (++roots2 > 80) break;
                log.LogInfo($"[UI探针] 根: {t.gameObject.name} (子 {t.childCount})");
            }
        }
        catch (Exception e)
        {
            log.LogWarning($"[UI探针] Transform 扫描异常：{e.GetType().Name}: {e.Message}");
        }

        log.LogInfo($"[UI探针] 遍历完：对象 {visited}，命中 {hits}。结束");
    }

    private static string FullPath(UnityEngine.Transform t)
    {
        try
        {
            var sb = new StringBuilder();
            while (t != null)
            {
                if (sb.Length > 0) sb.Insert(0, "/");
                sb.Insert(0, t.gameObject.name);
                t = t.parent;
            }
            return sb.ToString();
        }
        catch { return "?"; }
    }

    private static void Walk(UnityEngine.Transform t, ManualLogSource log, int depth, string path,
                             ref int visited, ref int hits)
    {
        if (depth > MaxDepth || visited >= MaxObjects || hits >= MaxHits) return;
        visited++;
        try
        {
            var go = t.gameObject;
            var name = go.name ?? "";
            var full = path + name;

            bool hit = false;
            foreach (var k in Keywords)
            {
                if (name.Contains(k, StringComparison.Ordinal)) { hit = true; break; }
            }
            if (hit)
            {
                hits++;
                log.LogInfo($"[UI探针] #{hits} {full}\n        组件: {DescribeComponents(go)}");
            }

            var count = t.childCount;
            for (int i = 0; i < count; i++)
                Walk(t.GetChild(i), log, depth + 1, full + "/", ref visited, ref hits);
        }
        catch { }
    }

    /// <summary>列出组件类型名；遇到输入框顺手读一次 text（只读）。</summary>
    private static string DescribeComponents(UnityEngine.GameObject go)
    {
        try
        {
            var arr = go.GetComponents<UnityEngine.Component>();
            var sb = new StringBuilder();
            int n = 0;
            foreach (var c in arr)
            {
                if (c == null) continue;
                if (n++ > 12) { sb.Append("…"); break; }
                var tn = c.GetType().Name;
                sb.Append(tn).Append(' ');

                if (tn.Contains("InputField", StringComparison.OrdinalIgnoreCase)
                    || tn.Contains("TMP_Input", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var p = c.GetType().GetProperty("text");
                        var v = p?.GetValue(c) as string;
                        sb.Append($"[text=\"{v}\"] ");
                    }
                    catch { sb.Append("[text 读不到] "); }
                }

                // ★ 2026-11-04：对输入框/面板控制器做一次只读成员反射，
                //   回答「text 能不能写」「有没有现成的 SetText 接口」。
                if (tn == "CommonInputField" || tn == "DialogueInputPanel")
                {
                    try
                    {
                        var ty = c.GetType();
                        sb.Append("\n        [成员] ");
                        sb.Append("props=");
                        int pn = 0;
                        foreach (var pr in ty.GetProperties())
                        {
                            if (pn++ > 25) { sb.Append("…"); break; }
                            sb.Append(pr.Name);
                            if (pr.CanWrite) sb.Append("(可写)");
                            sb.Append(' ');
                        }
                        sb.Append("\n        [成员] methods=");
                        int mn = 0;
                        foreach (var md in ty.GetMethods())
                        {
                            if (mn++ > 30) { sb.Append("…"); break; }
                            sb.Append(md.Name).Append(' ');
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.Append("[反射失败:" + ex.GetType().Name + "] ");
                    }
                }
            }
            return sb.ToString().Trim();
        }
        catch (Exception e)
        {
            return "（组件读取失败：" + e.GetType().Name + "）";
        }
    }
}
