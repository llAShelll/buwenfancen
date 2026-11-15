using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using BuWenFanChen.Memory;
using BuWenFanChen.Diagnostics;

namespace BuWenFanChen;

[BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        Log.LogInfo($"{PluginInfo.NAME} v{PluginInfo.VERSION}（构建 {PluginInfo.BUILD_TAG}）加载中…");
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

            // ★ 2026-11-07：canon 三旋钮（保留轮数 / 裁剪节拍 / 记忆合并节拍）。裁剪与合并已解耦。
            //   注意：改 config 后要重启游戏才生效（和 replyLenTotal 等一致）。
            try
            {
                BuWenFanChen.History.CanonStore.KeepRounds = PromptConfig.CanonKeepRounds;
                BuWenFanChen.History.CanonStore.ArchiveEvery = PromptConfig.CanonArchiveEvery;
                var k = BuWenFanChen.History.CanonStore.KeepRounds;
                var e = BuWenFanChen.History.CanonStore.ArchiveEvery;
                Log.LogInfo($"[历史] canon 档位：保留 {k} 轮 / 每 {e} 轮裁剪一次（窗口 {k}~{k + e - 1} 轮）"
                          + $" / 每 {PromptConfig.CanonMergeEvery} 轮合并一次长期记忆");
            }
            catch (Exception ke) { Log.LogWarning($"[历史] 同步 canon 档位失败：{ke.Message}"); }

            // ★ 2026-11-05：history 接管——启动时探测能不能反射构造 LlmMessage（只写日志，不改任何东西）
            //   能构造 → canon.jsonl 才能在游戏重启后恢复；不能 → 只做内存 canon
            try { BuWenFanChen.History.CanonStore.ProbeCtorOnce(); } catch { }

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

            // ★★ 2026-11-10：改用**实例** Harmony，只为了能事后查「我到底挂上了哪些 patch」。
            //   静态 CreateAndPatchAll 拿不到实例；而本插件是显式注册模式，patch 挂不上时
            //   Harmony 静默跳过、日志啥也不显示 —— 游戏更新后第一件事就是查这个。
            var harmony = new Harmony(PluginInfo.GUID + ".main");

            harmony.PatchAll(typeof(CompleteBudgetedPatch));
            Log.LogInfo("✓ CompleteBudgeted hook 完成（观察模式）");
            harmony.PatchAll(typeof(AddNpcChatMessagePatch));
            Log.LogInfo("✓ 输出侧 hook 完成（字数切分 + 观察）");

            // ★★ 2026-11-08：对话关闭 → 清空选项框。
            //   本插件是**显式注册**模式（harmony.PatchAll(typeof(X))），
            //   光给类加 [HarmonyPatch] 特性不会自动生效 —— 必须在这里注册，
            //   否则「退出对话框框还在」永远修不好（这就是之前 patch 一次都没触发的原因）。
            try
            {
                harmony.PatchAll(typeof(DialogueClosePatchOnHide));
                harmony.PatchAll(typeof(DialogueClosePatchAnim));
                Log.LogInfo("✓ 对话关闭 hook 完成（OnHide / PlayCloseAnimThenHide → 清空选项框）");
            }
            catch (Exception ce)
            {
                Log.LogWarning($"⚠ 对话关闭 hook 失败：{ce.Message}");
            }

            // ★★ 2026-11-10：家园退出（独立场景，不走 OnHide、面板对象也常驻）→ 清空选项框。
            //   ① SpaceManager.BackToParentSpace（返回庭院/父空间）② UIManager.OnUIDestroy（对话面板销毁）
            try
            {
                harmony.PatchAll(typeof(SpaceBackClearPatch));
                Log.LogInfo("✓ 家园返回父空间 hook 完成（BackToParentSpace → 清空选项框）");
            }
            catch (Exception se) { Log.LogWarning($"⚠ 家园返回 hook 失败：{se.Message}"); }
            try
            {
                harmony.PatchAll(typeof(UIDestroyClearPatch));
                Log.LogInfo("✓ 面板销毁 hook 完成（OnUIDestroy → 对话面板销毁时清空选项框）");
            }
            catch (Exception ue) { Log.LogWarning($"⚠ 面板销毁 hook 失败：{ue.Message}"); }

            // ★★★ 2026-11-10：家园（卧室）对话面板 NpcBedroomOverlay 的 OnHide / OnDisable。
            //   探针证实：家园对话走的是它自己的 OnHide，不是 NpcDialoguePanel.OnHide
            //   —— 这是「家园退出后选项框残留」的最终解法。
            try
            {
                harmony.PatchAll(typeof(BedroomOverlayHideClearPatch));
                harmony.PatchAll(typeof(BedroomOverlayDisableClearPatch));
                Log.LogInfo("✓ 家园卧室面板 hook 完成（NpcBedroomOverlay.OnHide/OnDisable → 清空选项框）");
            }
            catch (Exception be) { Log.LogWarning($"⚠ 家园卧室面板 hook 失败：{be.Message}"); }

            // ★ 开场白过滤（2026-10-24 01:05 定案）
            //   时机：NpcModel.StartFreeChatAsync 的 Prefix（能拿到 NpcModel，且 OnShow 还没渲染）
            //   判据：NpcModel.IsConfiguredOpening(text) —— 游戏自己的判断函数
            harmony.PatchAll(typeof(CurrentNpcTracker));
            try
            {
                harmony.PatchAll(typeof(GreetingFilter));
                harmony.PatchAll(typeof(GreetingFilterEarly));

                Log.LogInfo("✓ 开场白过滤 hook 完成（IsConfiguredOpening 判定，有历史时移除）");
            }
            catch (Exception ge)
            {
                Log.LogWarning($"⚠ 开场白过滤 hook 失败：{ge.Message}");
            }

            // ★ 2026-11-04：截下模型原始输出。语音 MOD 会在 AddNpcChatMessage 之前
            //   把 a1ttsv2 / opts 洗掉（502 字 → 264 字），必须在 LogResponse 点留一份。
            try
            {
                harmony.PatchAll(typeof(BuWenFanChen.Output.RawReplyStore));
                Log.LogInfo("✓ 原始输出截取 hook 完成");
            }
            catch (Exception rr) { Log.LogWarning($"原始输出截取 hook 失败：{rr.Message}"); }

            // ★ 2026-11-12：请求体落盘 —— 玩家再报空响应时，把实际发给模型的 messages 存下来比对。
            //   只读、全程 try/catch、失败自动关闭，不碰任何参数（见 Diagnostics\RequestBodyDump 注释）。
            try
            {
                harmony.PatchAll(typeof(BuWenFanChen.Diagnostics.RequestBodyDump));
                Log.LogInfo("✓ 请求体落盘 hook 完成（_diag\\requests\\req_*.json，保留最近 60 份）");
            }
            catch (Exception rb) { Log.LogWarning($"请求体落盘 hook 失败：{rb.Message}"); }

            // ★ 2026-11-12：闸门探针 —— patch JNGame.Ainpc.Llm.BudgetedLlmCall.Prepare/ExecuteAsync。
            //   这是玩家真正撞到的「预算预检」，在 LogRequest 之后、HTTP 之前。
            //   前三次修复都在闸门上游，这道门从没被观测过（PRO 审核指出）。
            //   纯读 + try/catch，不改任何行为。
            try
            {
                harmony.PatchAll(typeof(BuWenFanChen.Diagnostics.BudgetGateProbe));
                Log.LogInfo("✓ 闸门探针 hook 完成（BudgetedLlmCall.Prepare/ExecuteAsync）");
            }
            catch (Exception bg) { Log.LogWarning($"闸门探针 hook 失败：{bg.Message}"); }

            // ★ 2026-11-05：只读预算探针 —— 看游戏给历史留了多少预算、保留最近几轮。
            //   目的：搞清「历史为什么只带 8 轮」，判断能不能调大。
            try
            {
                harmony.PatchAll(typeof(ChatBudgetProbe));
                Log.LogInfo("✓ 预算探针 hook 完成");
            }
            catch (Exception cb) { Log.LogWarning($"预算探针 hook 失败：{cb.Message}"); }

            // ★ 2026-11-04：四选一选项的常驻显示框（IMGUI，屏幕左下、输入框上方）
            try
            {
                var hudGo = new UnityEngine.GameObject("BuWenFanChen_OptionsHud");
                UnityEngine.Object.DontDestroyOnLoad(hudGo);
                // BepInEx 6：先把自定义 MonoBehaviour 注册给 IL2CPP，再按 Il2Cpp 类型挂上去
                IL2CPPChainloader.AddUnityComponent<OptionsHud>();
                hudGo.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<OptionsHud>());
                Log.LogInfo("✓ 选项显示框已挂载");
            }
            catch (Exception he) { Log.LogWarning($"选项显示框挂载失败：{he.Message}"); }

            // ★ 2026-11-12：NPC 数值面板（1.4.0 P1）—— 游戏内 IMGUI 面板 + 切场景自动关
            try
            {
                var panelGo = new UnityEngine.GameObject("BuWenFanChen_PanelHud");
                UnityEngine.Object.DontDestroyOnLoad(panelGo);
                IL2CPPChainloader.AddUnityComponent<BuWenFanChen.Panel.PanelHud>();
                panelGo.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<BuWenFanChen.Panel.PanelHud>());
                Log.LogInfo("✓ 数值面板 HUD 已挂载（右下角「面板」按钮）");
            }
            catch (Exception phe) { Log.LogWarning($"数值面板 HUD 挂载失败：{phe.Message}"); }
            try
            {
                harmony.PatchAll(typeof(BuWenFanChen.Panel.PanelHideSpacePatch));
                harmony.PatchAll(typeof(BuWenFanChen.Panel.PanelHideBedroomHidePatch));
                harmony.PatchAll(typeof(BuWenFanChen.Panel.PanelHideBedroomDisablePatch));
                // ★ 2026-11-12 21:4x 补：退出世界对话 / 面板销毁也要关（主人报「退出对话后面板没消失」）
                harmony.PatchAll(typeof(BuWenFanChen.Panel.PanelHideDialoguePatch));
                harmony.PatchAll(typeof(BuWenFanChen.Panel.PanelHideDialogueAnimPatch));
                harmony.PatchAll(typeof(BuWenFanChen.Panel.PanelHideUIPatch));
                Log.LogInfo("✓ 数值面板场景关闭 hook 完成（对话 / 家园 / 面板销毁 6 处）");
            }
            catch (Exception ppe) { Log.LogWarning($"数值面板场景 hook 失败：{ppe.Message}"); }
            // ★ 2026-11-14：面板看门狗——游戏按 F5 / 重建 UI 会把 PanelHud 的宿主对象销毁，
            //   之后没 OnGUI 面板永久消失；看门狗每秒检查、自动重建
            try
            {
                BuWenFanChen.Panel.PanelWatchdog.EnsureInstalled();
                Log.LogInfo("✓ 面板看门狗已挂载（F5/UI 重建后自动恢复面板）");
            }
            catch (Exception we) { Log.LogWarning($"面板看门狗挂载失败：{we.Message}"); }

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
            // try { harmony.PatchAll(typeof(BuildMessagesProbe2)); } catch { }

            // ★ 2026-10-28：方案 B 前置验证 —— 最小探针
            //   结果：hook 点命中了，但游戏在 Complete 调用链里崩（il2cpp_value_box AccessViolation）。
            //   崩溃链：CompleteBudgeted → Complete → il2cpp_runtime_invoke → il2cpp_value_box → 崩溃
            //   结论：LLM 调用层（Complete / PostJsonAsync）在 Il2CppInterop 下 hook 就是会崩，
            //         连「只打日志不碰内容」的 Prefix 都救不了 —— 方案 B 放弃。
            // try { harmony.PatchAll(typeof(LlmProbeMin)); } catch { }

            // ★★ 2026-10-24 19:35 禁用 LlmCompleteProbe ★★
            //   实测崩溃：Exception Processing Message 0xc0000005（Access Violation）
            //   原因推测：在 Prefix 里用 .NET 的 IEnumerable 遍历 Il2Cpp 的 IReadOnlyList<LlmMessage>，
            //   再反射读 Il2Cpp 对象的属性 → 访问冲突。
            //   这和之前 PostJsonAsync 的 0xc0000005 是同一类问题：Il2Cpp 对象不能这样碰。
            //   结论：LLM 调用层（Complete / PostJsonAsync）在当前 hook 方式下都不可用。
            // harmony.PatchAll(typeof(LlmCompleteProbe));

            // ★ RequestDumpPatch 禁用：PostJsonAsync 这个 hook 点彻底不能碰（ref/只读都 0xc0000005）
            // harmony.PatchAll(typeof(RequestDumpPatch));

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

            // ★★ 2026-11-10：启动体检（只跑一次，报告同时落盘到 存档\BuWenFanChen\_diag\health_*.txt）。
            //   排查「游戏更新后选项 / 自动好感度同时失效」：先看这份报告里
            //   AddNpcChatMessage / CompleteBudgeted 有没有挂上、目标方法签名有没有变。
            try { HealthProbe.Run(harmony); }
            catch (Exception hp) { Log.LogWarning($"[体检] 运行失败：{hp.Message}"); }
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

    /// <summary>★ 2026-11-11 晚 = **1.3.3**。
    ///   内容：修 canon 历史接管的「对齐」逻辑 —— 它在「游戏给的历史条数 &gt; canon 保留条数」时
    ///   必然重叠=0（每轮全量重建 + 每轮归档 44 条 + 每轮触发一次后台合并 LLM 调用），
    ///   与主请求抢同一个 API key → 偶发空响应 → 游戏显示「上下文超出模型上限」。
    ///   另加指纹归一化（剥 a1ttsv2/JSON 外壳 + 去空白）。</summary>
    public const string VERSION = "1.4.0";

    /// <summary>★ 发版标记——每次出包改这个，启动日志会打出来，
    ///   「玩家到底装的哪一版」一眼就能确认（不用再靠 DLL 大小/时间猜）。</summary>
    public const string BUILD_TAG = "20261114-2020";
}
