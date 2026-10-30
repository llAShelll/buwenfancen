using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using BuWenFanChen.Memory;

namespace BuWenFanChen;

[BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        Log.LogInfo($"{PluginInfo.NAME} v{PluginInfo.VERSION} 加载中…");
        try
        {
            // ★ 记忆引擎日志接 BepInEx
            MemoryEngine.Log = (msg, warn) =>
            {
                if (warn) Log.LogWarning(msg); else Log.LogInfo(msg);
            };

            // ★ 记忆数据根目录：游戏存档区下的 BuWenFanChen（跟存档走，不污染 MOD 目录）
            var root = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "BuWenFanChen");
            MemoryPaths.Init(root);

            // ★ F1 不同存档适配：检测当前存档，把 Scope 设好
            //   必须在 MemoryPaths.Init 之后、任何记忆读写之前
            try
            {
                BuWenFanChen.Memory.SaveSlot.AutoDetect();
                Log.LogInfo("[存档] " + BuWenFanChen.Memory.SaveSlot.Describe());
            }
            catch (Exception se) { Log.LogWarning($"[存档] 检测失败：{se.Message}"); }
            Log.LogInfo($"✓ 记忆数据根目录：{root}");

            // ★ 阶段 4：加载提示词配置（首次运行会写出默认文件到 config\）
            PromptConfig.Load(CompleteBudgetedPatch.INJECT_V12);

            // ★ ⑧/③ 加载角色名缓存（供控制台「一键改真名」在非主线程读真名）
            try { BuWenFanChen.Memory.ConfigTable.LoadNameCache(); } catch { }
            try { BuWenFanChen.Memory.ConfigTable.LoadSkinCache(); } catch { }
            try { BuWenFanChen.Memory.ConfigTable.LoadProfileCache(); } catch { }

            // ★ 2026-10-29：静态表（npcId → 官方真名）。
            //   换化身/皮肤会把游戏里的 NPC 名字改成「奈宝·芊儿4K2」，
            //   真名只在这张表里（来源：语音 MOD 的 references\npcs\npc_id_name.csv，已内置 1591 条）。
            try
            {
                BuWenFanChen.Memory.ConfigTable.LoadStaticNames();
                Log.LogInfo($"[真名表] 已加载 {BuWenFanChen.Memory.ConfigTable.StaticNameCount} 个角色的官方真名");
            }
            catch (Exception se) { Log.LogWarning($"[真名表] 加载失败：{se.Message}"); }

            // ★ 阶段 3②：检查 BGE 语义检索是否就绪
            if (EmbedderProvider.IsReady)
                Log.LogInfo($"✓ BGE 语义检索就绪（{BgeEmbedder.Dim} 维）");
            else
                Log.LogWarning($"⚠ BGE 模型未加载（{MemoryPaths.BgeModelPath}），检索退回纯 BM25");

            Harmony.CreateAndPatchAll(typeof(CompleteBudgetedPatch));
            Log.LogInfo("✓ CompleteBudgeted hook 完成（观察模式）");
            Harmony.CreateAndPatchAll(typeof(AddNpcChatMessagePatch));
            Log.LogInfo("✓ 输出侧 hook 完成（字数切分 + 观察）");

            // ★ 开场白过滤（2026-10-24 01:05 定案）
            //   时机：NpcModel.StartFreeChatAsync 的 Prefix（能拿到 NpcModel，且 OnShow 还没渲染）
            //   判据：NpcModel.IsConfiguredOpening(text) —— 游戏自己的判断函数
            Harmony.CreateAndPatchAll(typeof(CurrentNpcTracker));
            try
            {
                Harmony.CreateAndPatchAll(typeof(GreetingFilter));
                Harmony.CreateAndPatchAll(typeof(GreetingFilterEarly));

                Log.LogInfo("✓ 开场白过滤 hook 完成（IsConfiguredOpening 判定，有历史时移除）");
            }
            catch (Exception ge)
            {
                Log.LogWarning($"⚠ 开场白过滤 hook 失败：{ge.Message}");
            }

            // ★ 2026-10-24 19:15：探针和扫描放到外面，各自独立 try
            //   之前放在开场白那个 try 里，PatchAll 一抛异常后面的全跳过，导致一直看不到日志
            try
            {
                var scanText = LlmLayerScan.Run();
                foreach (var line in scanText.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Log.LogInfo(line.TrimEnd());
            }
            catch (Exception se) { Log.LogWarning($"[扫描] 失败：{se.GetType().Name} {se.Message}"); }

            // ★ 2026-10-28：方案 B 路 1 验证 —— 结果：BuildMessages 方法不存在
            //   探针 C 的 TargetMethod 全程序集扫描没找到 BuildMessages(LlmMessage...)，Harmony 整类跳过。
            //   结论：游戏里没有叫 BuildMessages 的方法（之前 RAG 里那条是预期输出，不是实测）。
            //   方案 B 彻底放弃：LLM 调用层（Complete/PostJsonAsync）hook 崩 + BuildMessages 不存在。
            // try { Harmony.CreateAndPatchAll(typeof(BuildMessagesProbe2)); } catch { }

            // ★ 2026-10-28：方案 B 前置验证 —— 最小探针
            //   结果：hook 点命中了，但游戏在 Complete 调用链里崩（il2cpp_value_box AccessViolation）。
            //   崩溃链：CompleteBudgeted → Complete → il2cpp_runtime_invoke → il2cpp_value_box → 崩溃
            //   结论：LLM 调用层（Complete / PostJsonAsync）在 Il2CppInterop 下 hook 就是会崩，
            //         连「只打日志不碰内容」的 Prefix 都救不了 —— 方案 B 放弃。
            // try { Harmony.CreateAndPatchAll(typeof(LlmProbeMin)); } catch { }

            // ★★ 2026-10-24 19:35 禁用 LlmCompleteProbe ★★
            //   实测崩溃：Exception Processing Message 0xc0000005（Access Violation）
            //   原因推测：在 Prefix 里用 .NET 的 IEnumerable 遍历 Il2Cpp 的 IReadOnlyList<LlmMessage>，
            //   再反射读 Il2Cpp 对象的属性 → 访问冲突。
            //   这和之前 PostJsonAsync 的 0xc0000005 是同一类问题：Il2Cpp 对象不能这样碰。
            //   结论：LLM 调用层（Complete / PostJsonAsync）在当前 hook 方式下都不可用。
            // Harmony.CreateAndPatchAll(typeof(LlmCompleteProbe));

            // ★ RequestDumpPatch 禁用：PostJsonAsync 这个 hook 点彻底不能碰（ref/只读都 0xc0000005）
            // Harmony.CreateAndPatchAll(typeof(RequestDumpPatch));

            // ★ 接口地址检查（2026-10-26）：没配 / 还指着旧代理时给明确提示，别让玩家以为是 MOD 坏了
            try
            {
                var llm = BuWenFanChen.Memory.MemoryEngine.GetLlmConfig(null);
                var apiUrl = llm?.Url ?? "";
                if (string.IsNullOrWhiteSpace(apiUrl) || !llm.IsValid)
                    Log.LogWarning("[接口] 游戏里还没配置 AI 接口地址（设置 → 自定义 AI 模型）——"
                        + "MOD 的后台功能（建卡 / 记忆抽取 / 卡优化）会用不了；主对话也请先把地址填好。");
                else if (apiUrl.Contains(":8788") || apiUrl.Contains(":8790"))
                    Log.LogWarning($"[接口] 地址还指着 1.1.2 的本地代理：{apiUrl} —— 1.2 要直连 DeepSeek"
                        + "（https://api.deepseek.com/v1/chat/completions），去游戏设置里改。");
                else if (apiUrl.Contains("127.0.0.1") || apiUrl.Contains("localhost"))
                    Log.LogInfo($"[接口] {apiUrl}（本地模型，model={llm.Model}）—— 能用，但破甲/格式遵守率会比 DeepSeek 打折");
                else
                    Log.LogInfo($"[接口] {apiUrl}（model={llm.Model}）");
            }
            catch { }

            // ★ 阶段 4-B：控制台（本地网页）
            try { ConsoleServer.Start(); }
            catch (Exception ce) { Log.LogWarning($"控制台启动失败：{ce.Message}"); }
        }
        catch (Exception e)
        {
            Log.LogError($"✗ hook 失败（游戏仍可正常运行，只是本 MOD 不生效）：{e}");
        }
    }

    /// <summary>游戏退出时关掉控制台（包括应用模式开的浏览器窗口）</summary>
    void OnDestroy()
    {
        try { ConsoleServer.Stop(); } catch { }
    }
}

internal static class PluginInfo
{
    public const string GUID = "com.buwenfan.chen";
    public const string NAME = "不问凡尘";
    public const string VERSION = "1.2.2";

}
