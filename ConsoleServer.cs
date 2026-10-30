using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Text.Json.Nodes;

namespace BuWenFanChen;

/// <summary>
/// 控制台（本地网页）HTTP 服务。
/// 阶段 4-B。
///
/// · 只监听 127.0.0.1（本地），端口固定 8848，被占用自动 +1
/// · 页面是内嵌 HTML，API 走同源 POST
/// · 后台线程跑监听，游戏停止时 Stop()
///
/// 安全约定：
///   · 不监听 0.0.0.0，只 localhost
///   · 写配置走 POST + JSON，只改 config.json，不碰游戏内存里的热值（改完提示重启）
/// </summary>
public static class ConsoleServer
{
    private static HttpListener _listener;
    private static Thread _thread;
    private static volatile bool _running;
    private static System.Diagnostics.Process _browser;

    public static int Port { get; private set; } = -1;
    public static bool IsRunning => _running;

    public static void Start()
    {
        if (_running) return;
        for (var port = 8848; port < 8870; port++)
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                l.Start();
                _listener = l;
                Port = port;
                break;
            }
            catch { }
        }
        if (_listener == null)
        {
            Log($"控制台启动失败：8848~8869 端口都被占用");
            return;
        }

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "BWFC-Console" };
        _thread.Start();

        // ★ 进程退出钩子（2026-10-24 20:30）：
        //   Unity 的 OnDestroy 在游戏退出时常常来不及执行（进程被直接杀），
        //   所以额外挂 AppDomain 的 ProcessExit / DomainUnload 双保险。
        try
        {
            AppDomain.CurrentDomain.ProcessExit += (_, __) => StopQuiet();
            AppDomain.CurrentDomain.DomainUnload += (_, __) => StopQuiet();
        }
        catch { }
        Log($"控制台已启动：http://127.0.0.1:{Port}/");

        // 自动打开浏览器（可关，见 config.json 的 consoleAutoOpen）
        try { if (PromptConfig.ConsoleAutoOpen) OpenBrowser(Port); }
        catch (Exception e) { Log("自动打开浏览器失败：" + e.Message + "（可手动访问上面的地址）"); }
    }

    public static void Stop()
    {
        _running = false;
        try { _listener?.Stop(); _listener?.Close(); } catch { }

        // 关掉应用模式开的浏览器窗口（游戏退出时自动关）
        try
        {
            if (_browser != null)
            {
                if (!_browser.HasExited)
                {
                    _browser.Kill(entireProcessTree: true);
                    Log("已关闭控制台浏览器窗口");
                }
                else
                {
                    Log("浏览器进程已自行退出（窗口可能还在，手动关掉即可）");
                }
            }
        }
        catch (Exception e) { Log("关闭浏览器窗口失败：" + e.Message + "（手动关掉即可）"); }
    }

    /// <summary>退出时的静默关闭（不写日志，避免退出阶段访问已销毁的日志系统）</summary>
    private static void StopQuiet()
    {
        _running = false;
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        try
        {
            if (_browser != null && !_browser.HasExited)
                _browser.Kill(entireProcessTree: true);
        }
        catch { }
    }

    /// <summary>重新打开控制台窗口（玩家关了窗口后，用快捷键再叫出来）</summary>
    public static void OpenInBrowser()
    {
        try
        {
            if (!_running || Port <= 0) { Log("控制台没在跑，先重启游戏"); return; }
            OpenBrowser(Port);
            Log($"已重新打开控制台：http://127.0.0.1:{Port}/");
        }
        catch (Exception e) { Log("重新打开失败：" + e.Message); }
    }

    /// <summary>用应用模式或默认浏览器打开控制台</summary>
    private static void OpenBrowser(int port)
    {
        var url = $"http://127.0.0.1:{port}/";

        // 不用应用模式 → 直接丢给系统默认浏览器
        if (!PromptConfig.ConsoleAppMode)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return;
        }

        var exe = FindChromiumBrowser();
        if (exe == null)
        {
            Log("没找到 Chrome/Edge，退回默认浏览器");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return;
        }

        try
        {
            // ★ 关键：给控制台一个独立 profile（--user-data-dir）
            //   否则 Chrome 是单实例：新起的 chrome.exe 会把参数转给已有主进程后立刻退出，
            //   我们拿到的 Process 早已 HasExited，游戏退出时 Kill 不掉（实测踩过）。
            //   独立 profile 会启动一个真正独立的浏览器实例，句柄才有效。
            var profileDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bwfc-console-profile");
            try { System.IO.Directory.CreateDirectory(profileDir); } catch { }

            var w = Math.Max(400, PromptConfig.ConsoleWinW);
            var h = Math.Max(300, PromptConfig.ConsoleWinH);

            _browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                // ★ --app 模式本身不记住窗口大小，所以要自己存尺寸：
                //   前端窗口 resize 时把尺寸写回 config，下次启动用这个尺寸。
                Arguments = $"--app={url} --window-size={w},{h} --user-data-dir=\"{profileDir}\"",
                UseShellExecute = false,   // 需要拿到 Process 句柄，退出时才能关
            });
            Log($"已用应用模式打开：{System.IO.Path.GetFileName(exe)}（独立 profile）");
        }
        catch (Exception e)
        {
            Log("应用模式启动失败：" + e.Message + "，退回默认浏览器");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
    }

    /// <summary>找 Chrome 或 Edge 的可执行文件，找不到返回 null</summary>
    private static string FindChromiumBrowser()
    {
        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // ★ Edge 优先（2026-10-24 主人定：Windows 自带，最不容易缺）
            var candidates = new[]
            {
                System.IO.Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                System.IO.Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
                System.IO.Path.Combine(local, @"Microsoft\Edge\Application\msedge.exe"),
                System.IO.Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                System.IO.Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                System.IO.Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            };
            foreach (var c in candidates)
                if (System.IO.File.Exists(c)) return c;
        }
        catch { }
        return null;
    }

    private static void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx = null;
            try { ctx = _listener.GetContext(); }
            catch { break; }

            try { Handle(ctx); }
            catch (Exception e) { TryWrite(ctx, 500, "text/plain", "服务器内部错误：" + e.Message); }
        }
    }

    private static void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
        if (path == "") path = "/";

        switch (path)
        {
            case "/":
                // 不缓存首页：改了 CSS 刷新就能看到，不会被浏览器旧副本挡住
                ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                ctx.Response.Headers["Pragma"] = "no-cache";
                ctx.Response.Headers["Expires"] = "0";
                TryWrite(ctx, 200, "text/html; charset=utf-8", IndexHtml());
                break;
            case "/api/status":
                TryWrite(ctx, 200, "application/json; charset=utf-8", StatusJson());
                break;
            case "/api/config":
                HandleConfig(ctx);
                break;
            case "/api/npcs":
                TryWrite(ctx, 200, "application/json; charset=utf-8", NpcsJson());
                break;
            case "/api/memory":
                HandleMemory(ctx);
                break;
            case "/api/memory/facts":
                HandleSaveFacts(ctx);
                break;
            case "/api/memory/skeleton":
                HandleSaveSkeleton(ctx);
                break;
            case "/api/memory/summary":
                HandleSummary(ctx);
                break;
            case "/api/memory/reclassify":
                HandleMemoryTool(ctx, true);
                break;
            case "/api/memory/compress":
                HandleMemoryTool(ctx, false);
                break;
            case "/api/memory/extract":
                HandleMemoryExtract(ctx);
                break;
            case "/api/memory/delete":
                HandleDeleteMemory(ctx);
                break;
            case "/api/openfolder":
                HandleOpenFolder(ctx);
                break;
            case "/api/cleanbackup":
                HandleCleanBackup(ctx);
                break;
            case "/api/log":
                HandleLog(ctx);
                break;
            case "/api/ask":
                HandleAsk(ctx);
                break;
            case "/api/card":
                HandleCard(ctx);
                break;
            case "/api/card/generate":
                HandleCardGenerate(ctx);
                break;
            case "/api/card/source":
                HandleCardSource(ctx);
                break;
            case "/api/card/list":
                HandleCardList(ctx);
                break;
            case "/api/card/pick":
                HandleCardPick(ctx);
                break;
            case "/api/card/saveas":
                HandleCardSaveAs(ctx);
                break;
            case "/api/card/rename":
                HandleCardRename(ctx);
                break;
            case "/api/card/cleanoldnames":
                HandleCardCleanOldNames(ctx);
                break;
            case "/api/migrate/scan":
                HandleMigrateScan(ctx);
                break;
            case "/api/migrate/apply":
                HandleMigrateApply(ctx);
                break;
            case "/api/rename":
                HandleRename(ctx);
                break;
            case "/api/card/lorebook":
                HandleCardLorebook(ctx);
                break;
            case "/api/card/check":
                HandleCardCheck(ctx);
                break;
            case "/api/card/check/apply":
                HandleCardCheckApply(ctx);
                break;
            case "/api/card/preview":
                HandleCardPreview(ctx);
                break;
            case "/api/card/rewrite":
                HandleCardRewrite(ctx);
                break;
            case "/api/card/rewrite/apply":
                HandleCardRewriteApply(ctx);
                break;
            case "/api/card/source-fields":
                HandleCardSourceFields(ctx);
                break;
            case "/api/card/delete":
                HandleCardDelete(ctx);
                break;
            case "/api/card/lorebook/delete":
                HandleLorebookDelete(ctx);
                break;
            case "/api/intimacy/history":
                HandleIntimacyHistory(ctx);
                break;
            case "/api/nsfw":
                HandleNsfw(ctx);
                break;
            case "/api/tokens":
                HandleTokens(ctx);
                break;
            case "/api/card/draft":
                HandleCardDraft(ctx);
                break;
            case "/api/saves":
                HandleSaves(ctx);
                break;
            case "/api/worldbook/list":
                HandleWorldbookList(ctx);
                break;
            case "/api/worldbook":
                HandleWorldbook(ctx);
                break;
            case "/api/worldbook/infer":
                HandleWorldbookInfer(ctx);
                break;
            case "/api/memory/scopes":
                HandleMemoryScopes(ctx);
                break;
            case "/api/memory/import":
                HandleMemoryImport(ctx);
                break;
            case "/api/windowsize":
                HandleWindowSize(ctx);
                break;
            case "/console-bg.png":
                ServeBgImage(ctx);
                break;
            default:
                TryWrite(ctx, 404, "text/plain", "Not Found");
                break;
        }
    }

    // ==================== API ====================

    private static void HandleConfig(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod == "POST")
        {
            string body;
            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();

            var err = SaveConfig(body);
            if (err == null)
            {
                // ★ 2026-10-26 热重载：保存后立即重新读 config，不用重启游戏
                try { BuWenFanChen.PromptConfig.Reload(); } catch { }
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true}");
            }
            else
                TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"" + JsonEscape(err) + "\"}");
        }
        else
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8", CurrentConfigJson());
        }
    }

    private static string SaveConfig(string body)
    {
        try
        {
            // 只允许合法的 JSON 对象
            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            if (node is not System.Text.Json.Nodes.JsonObject obj) return "必须是一个 JSON 对象";

            // 白名单字段（只允许这些，防玩家塞奇怪字段）
            var allowed = new[] {
                "tier", "dumpEnabled", "dumpKeep", "fallbackEnabled", "fallbackAlways",
                "replyLenTotal", "replyLenNarration", "replyLenDialogue", "replyLenHardRatio", "replyLenLo",
                "nsfwMode", "nsfwLatch", "extractInterval", "allowNpcActing", "cardEnabled",
                "cardModel", "consoleAutoOpen", "consoleAppMode", "consoleWinW", "consoleWinH",
                "modApiUrl", "modApiKey", "modModel", "memoryInjectLimit",
                "memoryEnabled", "worldbookEnabled", "lorebookEnabled", "nsfwEnabled", "cardEnabled",
                "replaceOfficialProfile",
                "autoIntimacyEnabled", "autoIntimacyMaxUp", "autoIntimacyMaxDown",
                "autoIntimacyPositive", "autoIntimacyNegative",
                "autoIntimacyApiUrl", "autoIntimacyApiKey", "autoIntimacyModel", "autoIntimacyMode"
            };

            // 读现有配置，只覆盖白名单里出现的
            var path = PromptConfig.ConfigPath;
            var current = System.IO.File.Exists(path)
                ? System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path, Encoding.UTF8)) as System.Text.Json.Nodes.JsonObject
                : new System.Text.Json.Nodes.JsonObject();
            if (current == null) current = new System.Text.Json.Nodes.JsonObject();

            foreach (var kv in obj)
            {
                var key = kv.Key;
                if (Array.IndexOf(allowed, key) < 0) continue;   // 白名单外忽略
                // JsonNode 没有 DeepClone，用「序列化再解析」做独立副本
                current[key] = kv.Value == null
                    ? null
                    : System.Text.Json.Nodes.JsonNode.Parse(kv.Value.ToJsonString());
            }

            // 备份后写回
            var backup = path + ".bak_" + DateTime.Now.ToString("HHmmss");
            try { System.IO.File.Copy(path, backup, true); } catch { }

            System.IO.File.WriteAllText(path,
                current.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            return null;
        }
        catch (Exception e)
        {
            return "保存失败：" + e.Message;
        }
    }

    private static string CurrentConfigJson()
    {
        try
        {
            var path = PromptConfig.ConfigPath;
            if (!System.IO.File.Exists(path)) return "{}";
            return System.IO.File.ReadAllText(path, Encoding.UTF8);
        }
        catch { return "{}"; }
    }

    /// <summary>列出有记忆目录的 NPC（返回 [{id, name}]）</summary>
    private static string NpcsJson()
    {
        try
        {
            // ★ 2026-10-25：原来只列「有 memory 目录」的角色，
            //   导致只生成了角色卡/世界书、还没记忆的角色在控制台里看不到。
            //   现在把 memory / card / relation 三处的角色都收集起来。
            var ids = new System.Collections.Generic.SortedSet<string>();

            void CollectDirs(string dir)
            {
                try
                {
                    if (!System.IO.Directory.Exists(dir)) return;
                    foreach (var d in System.IO.Directory.GetDirectories(dir))
                    {
                        var n = System.IO.Path.GetFileName(d);
                        if (n.Length > 0 && char.IsDigit(n[0])) ids.Add(n);
                    }
                }
                catch { }
            }

            var root = BuWenFanChen.Memory.MemoryPaths.Root;
            var scope = BuWenFanChen.Memory.MemoryPaths.Scope ?? "";
            var prefix = string.IsNullOrEmpty(scope) ? root : System.IO.Path.Combine(root, scope);

            CollectDirs(System.IO.Path.Combine(prefix, "memory"));
            CollectDirs(System.IO.Path.Combine(prefix, "relation"));
            // card 是全局的（不分存档）
            // ★ 2026-10-29：顺便记下「哪些角色有主卡（.json 本体）」，
            //   供前端「只看有卡的」和「（仅记忆）」标注用。
            var hasMainCard = new System.Collections.Generic.HashSet<string>();
            try
            {
                var cardDir = System.IO.Path.Combine(root, "card");
                if (System.IO.Directory.Exists(cardDir))
                {
                    foreach (var f in System.IO.Directory.GetFiles(cardDir))
                    {
                        var fn = System.IO.Path.GetFileName(f);
                        // ★ 2026-10-29 修：备份文件不算角色。
                        //   删卡会留 `xxx-193401.json.删除备份_时间戳`，改真名会留 `.改名备份_`，
                        //   以前这些也会被扫进下拉框 → 玩家「明明删了还在」。
                        if (fn.Contains(".bak") || fn.Contains("备份")) continue;
                        // 形如 某某-190601.lorebook.json / 某某-190601.source.md / 某某-190601.json
                        var mm = System.Text.RegularExpressions.Regex.Match(fn, @"-(\d+)(?:\.|$)");
                        if (mm.Success) ids.Add(mm.Groups[1].Value);
                        // 主卡（不是世界书、不是资料）
                        var mm2 = System.Text.RegularExpressions.Regex.Match(fn, @"-(\d+)\.json$");
                        if (mm2.Success) hasMainCard.Add(mm2.Groups[1].Value);
                    }
                }
            }
            catch { }

            var sb = new StringBuilder();
            sb.Append('[');
            var first = true;
            foreach (var id in ids)
            {
                // 名字优先级：① 玩家手动改过的卡名 ② 配置表真名 ③ meta.json
                var name = "";
                try { name = BuWenFanChen.Card.CardStore.GetDisplayName(id); } catch { }
                if (string.IsNullOrWhiteSpace(name))
                {
                    try { name = BuWenFanChen.Memory.ConfigTable.GetBaseName(id); } catch { }
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        var metaPath = System.IO.Path.Combine(BuWenFanChen.Memory.MemoryPaths.MemDir(id), "meta.json");
                        if (System.IO.File.Exists(metaPath))
                        {
                            var meta = System.Text.Json.Nodes.JsonNode.Parse(
                                System.IO.File.ReadAllText(metaPath, Encoding.UTF8));
                            name = meta?["NpcName"]?.GetValue<string>() ?? "";
                        }
                    }
                    catch { }
                }
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":\"").Append(JsonEscape(id)).Append("\",\"name\":\"").Append(JsonEscape(name))
                  .Append("\",\"hasCard\":").Append(hasMainCard.Contains(id) ? "true" : "false").Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }
        catch { return "[]"; }
    }

    private static void HandleMemory(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        if (string.IsNullOrEmpty(npc))
        {
            TryWrite(ctx, 400, "application/json", "{\"error\":\"缺少 npc 参数\"}");
            return;
        }
        TryWrite(ctx, 200, "application/json; charset=utf-8", MemoryJson(npc));
    }

    /// <summary>前端窗口 resize 后 POST 尺寸过来，存进 config，下次启动用这个尺寸</summary>
    private static void HandleWindowSize(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod == "POST")
        {
            try
            {
                string body;
                using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();
                var node = System.Text.Json.Nodes.JsonNode.Parse(body);
                var w = node?["w"]?.GetValue<int>() ?? 0;
                var h = node?["h"]?.GetValue<int>() ?? 0;
                if (w >= 400 && h >= 300)
                {
                    // 直接更新内存 + 写 config（防抖后偶发调用，不频繁）
                    SaveWindowSize(w, h);
                }
                TryWrite(ctx, 200, "application/json", "{\"ok\":true}");
            }
            catch { TryWrite(ctx, 200, "application/json", "{\"ok\":false}"); }
        }
        else
        {
            TryWrite(ctx, 200, "application/json", "{\"w\":" + PromptConfig.ConsoleWinW + ",\"h\":" + PromptConfig.ConsoleWinH + "}");
        }
    }

    private static void SaveWindowSize(int w, int h)
    {
        try
        {
            var path = PromptConfig.ConfigPath;
            System.Text.Json.Nodes.JsonObject obj;
            if (System.IO.File.Exists(path))
            {
                var n = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path, Encoding.UTF8));
                obj = n as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
            }
            else obj = new System.Text.Json.Nodes.JsonObject();
            obj["consoleWinW"] = w;
            obj["consoleWinH"] = h;
            System.IO.File.WriteAllText(path,
                obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>提供背景图（config\console-bg.png，玩家可自行替换；没有就 404 回退纯色）</summary>
    private static void ServeBgImage(HttpListenerContext ctx)
    {
        try
        {
            var path = System.IO.Path.Combine(PromptConfig.Dir, "console-bg.png");
            if (!System.IO.File.Exists(path)) { TryWrite(ctx, 404, "text/plain", "no-bg"); return; }
            var bytes = System.IO.File.ReadAllBytes(path);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "image/png";
            // 不缓存：玩家换了背景图，刷新就生效（否则浏览器会用旧图）
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers["Pragma"] = "no-cache";
            ctx.Response.Headers["Expires"] = "0";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch { }
    }

    /// <summary>用资源管理器打开配置目录（方便玩家直接改文件）</summary>
    private static void OpenConfigFolder()
    {
        try
        {
            var dir = PromptConfig.Dir;
            if (System.IO.Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception e) { Log("打开文件夹失败：" + e.Message); }
    }

    /// <summary>一键迁移：重评重要度 / 压缩摘要（会调模型，几秒到十几秒）</summary>
    private static void HandleMemoryTool(HttpListenerContext ctx, bool reclassify)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        if (string.IsNullOrEmpty(npc))
        {
            TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少 npc 参数\"}");
            return;
        }
        try
        {
            var task = reclassify
                ? BuWenFanChen.Memory.MemoryTools.ReclassifyAsync(npc)
                : BuWenFanChen.Memory.MemoryTools.CompressAsync(npc);
            var (ok, msg) = task.GetAwaiter().GetResult();
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":" + (ok ? "true" : "false") + ",\"msg\":\"" + JsonEscape(msg) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("执行失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>手动完整抽取（2026-10-27 主人要：「立即整理」按钮）——摘要 + facts + 骨架。</summary>
    private static void HandleMemoryExtract(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        if (string.IsNullOrEmpty(npc))
        {
            TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少 npc 参数\"}");
            return;
        }
        try
        {
            var (ok, msg) = BuWenFanChen.Memory.MemoryTools.Extract(npc);
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":" + (ok ? "true" : "false") + ",\"msg\":\"" + JsonEscape(msg) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("整理失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>删除某类备份文件（.bak_ / .删除备份_ / .关系变更备份_ 等）。★ 2026-10-28 主人要。</summary>
    private static void HandleCleanBackup(HttpListenerContext ctx)
    {
        try
        {
            var type = (ctx.Request.QueryString["type"] ?? "all").Trim().ToLowerInvariant();
            var id = (ctx.Request.QueryString["id"] ?? "").Trim();
            var dirs = new System.Collections.Generic.List<string>();
            var patterns = new System.Collections.Generic.List<string>();

            void AddDir(string d)
            {
                try
                {
                    if (!string.IsNullOrEmpty(d) && System.IO.Directory.Exists(d) && !dirs.Contains(d))
                        dirs.Add(d);
                }
                catch { }
            }

            if (type == "all")
            {
                // ★ 一键清所有角色的备份：card 目录 + 所有存档的 worldbook/memory
                var root = BuWenFanChen.Memory.MemoryPaths.Root;
                AddDir(System.IO.Path.GetDirectoryName(BuWenFanChen.Memory.MemoryPaths.CardPath("0")) ?? "");
                AddDir(System.IO.Path.Combine(root, "worldbook"));
                var memRoot = System.IO.Path.Combine(root, "memory");
                try
                {
                    if (System.IO.Directory.Exists(memRoot))
                        foreach (var m in System.IO.Directory.GetDirectories(memRoot)) AddDir(m);
                    foreach (var scopeDir in System.IO.Directory.GetDirectories(root))
                    {
                        AddDir(System.IO.Path.Combine(scopeDir, "worldbook"));
                        var mem = System.IO.Path.Combine(scopeDir, "memory");
                        if (System.IO.Directory.Exists(mem))
                            foreach (var m in System.IO.Directory.GetDirectories(mem)) AddDir(m);
                    }
                }
                catch { }
                patterns.Add("*.bak_*");
                patterns.Add("*.删除备份_*");
                patterns.Add("*.关系变更备份_*");
                patterns.Add("*备份_*");
            }
            else if (type == "card")
            {
                AddDir(System.IO.Path.GetDirectoryName(BuWenFanChen.Memory.MemoryPaths.CardPath("0")) ?? "");
                patterns.Add("*.bak_*");
                patterns.Add("*.删除备份_*");
                patterns.Add("*.关系变更备份_*");
                patterns.Add("*备份_*");
            }
            else if (type == "worldbook")
            {
                AddDir(BuWenFanChen.Memory.Worldbook.Dir);
                patterns.Add("*.bak_*");
                patterns.Add("*.删除备份_*");
            }
            else if (type == "lorebook")
            {
                AddDir(System.IO.Path.GetDirectoryName(BuWenFanChen.Memory.MemoryPaths.CardPath("0")) ?? "");
                patterns.Add("*.lorebook.json.删除备份_*");
                patterns.Add("*.lorebook.json.bak_*");
                patterns.Add("*.关系变更备份_*");
            }
            else if (type == "memory")
            {
                if (string.IsNullOrEmpty(id))
                {
                    TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少 npc 参数\"}");
                    return;
                }
                var root = BuWenFanChen.Memory.MemoryPaths.Root;
                AddDir(System.IO.Path.Combine(root, "memory", id));
                try
                {
                    foreach (var scopeDir in System.IO.Directory.GetDirectories(root))
                        AddDir(System.IO.Path.Combine(scopeDir, "memory", id));
                }
                catch { }
                patterns.Add("*备份_*");
                patterns.Add("*.bak_*");
            }
            else
            {
                TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"未知类型\"}");
                return;
            }

            if (dirs.Count == 0)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true,\"msg\":\"没有可清理的目录\"}");
                return;
            }

            var removed = 0;
            var failed = 0;
            foreach (var dir in dirs)
            {
                foreach (var pat in patterns)
                {
                    try
                    {
                        foreach (var f in System.IO.Directory.GetFiles(dir, pat))
                        {
                            try { System.IO.File.Delete(f); removed++; }
                            catch { failed++; }
                        }
                    }
                    catch { }
                }
            }
            var msg = failed > 0 ? $"已删 {removed} 个备份，{failed} 个删不掉" : $"已删 {removed} 个备份";
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"msg\":\"" + JsonEscape(msg) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("删除备份失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>打开文件夹（config / 角色卡 / 世界书），方便玩家分享。★ 2026-10-28 主人要。</summary>
    private static void HandleOpenFolder(HttpListenerContext ctx)
    {
        try
        {
            var type = (ctx.Request.QueryString["type"] ?? "config").Trim().ToLowerInvariant();
            if (type == "config" || type == "nsfw")
            {
                OpenConfigFolder();
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true,\"msg\":\"已打开配置文件夹\"}");
                return;
            }
            string path;
            if (type == "worldbook")
            {
                path = BuWenFanChen.Memory.Worldbook.Dir;
            }
            else
            {
                var cardFile = BuWenFanChen.Memory.MemoryPaths.CardPath("0");
                path = System.IO.Path.GetDirectoryName(cardFile) ?? "";
            }
            if (string.IsNullOrEmpty(path))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"找不到目录\"}");
                return;
            }
            if (!System.IO.Directory.Exists(path)) System.IO.Directory.CreateDirectory(path);
            // ★ 2026-10-28：直接让 Windows 打开这个目录（UseShellExecute + FileName=目录），
            //   比 explorer.exe + 引号参数稳（后者在部分环境会被当成「文档」）。
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"msg\":\"" + JsonEscape("已打开：" + path) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("打开失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>
    /// 删除某个角色的全部记忆（F6）。
    /// ★ 先导出 zip 备份，再删目录；删完作废缓存。
    /// </summary>
    private static void HandleDeleteMemory(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        var npc = ctx.Request.QueryString["npc"] ?? "";
        if (string.IsNullOrEmpty(npc))
        {
            TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少 npc 参数\"}");
            return;
        }
        try
        {
            // ① 备份
            var zip = BuWenFanChen.Memory.MemoryTransfer.ExportZip(npc);
            if (zip == null)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"备份失败，为了数据安全没有删除\"}");
                return;
            }

            // ② 删目录
            var dir = BuWenFanChen.Memory.MemoryPaths.MemDir(npc);
            if (System.IO.Directory.Exists(dir))
                System.IO.Directory.Delete(dir, recursive: true);

            // ③ 作废缓存
            BuWenFanChen.Memory.MemoryEngine.InvalidateCache(npc);
            BuWenFanChen.Memory.MemoryRetriever.Invalidate(npc);

            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"msg\":\"已删除，备份在 " + JsonEscape(System.IO.Path.GetFileName(zip)) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("删除失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>读游戏日志的最后 N 行（默认 200，最多 2000）</summary>
    private static void HandleLog(HttpListenerContext ctx)
    {
        var n = 200;
        var q = ctx.Request.QueryString["lines"];
        if (!string.IsNullOrEmpty(q) && int.TryParse(q, out var parsed)) n = Math.Clamp(parsed, 50, 2000);

        try
        {
            // BepInEx 的日志在游戏根目录的 BepInEx\LogOutput.log
            var root = Environment.CurrentDirectory;
            var path = System.IO.Path.Combine(root, "BepInEx", "LogOutput.log");
            if (!System.IO.File.Exists(path))
            {
                // 兜底：从数据根往上两级找
                var alt = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    BuWenFanChen.Memory.MemoryPaths.Root, "..", "..", "BepInEx", "LogOutput.log"));
                if (System.IO.File.Exists(alt)) path = alt;
            }
            if (!System.IO.File.Exists(path))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"找不到日志文件：BepInEx/LogOutput.log\"}");
                return;
            }

            // 只读最后 N 行：文件可能很大，全读太浪费
            var lines = TailLines(path, n);
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"lines\":[").Append(string.Join(",", lines.Select(l => "\"" + JsonEscape(l) + "\""))).Append("]}");
            TryWrite(ctx, 200, "application/json; charset=utf-8", sb.ToString());
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("读日志失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>读文件最后 n 行（从文件尾部往前读块，避免整文件载入）</summary>
    private static List<string> TailLines(string path, int n)
    {
        var result = new List<string>();
        try
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            var length = fs.Length;
            if (length == 0) return result;

            var chunk = 64 * 1024;
            var pos = length;
            var text = "";
            while (pos > 0 && text.Split('\n').Length <= n + 1 && pos > length - 4 * 1024 * 1024)
            {
                var readSize = (int)Math.Min(chunk, pos);
                pos -= readSize;
                fs.Seek(pos, System.IO.SeekOrigin.Begin);
                var buf = new byte[readSize];
                fs.Read(buf, 0, readSize);
                text = Encoding.UTF8.GetString(buf) + text;
            }
            var all = text.Split('\n');
            var start = Math.Max(0, all.Length - n);
            for (var i = start; i < all.Length; i++)
            {
                var line = all[i].TrimEnd('\r');
                if (!string.IsNullOrEmpty(line)) result.Add(line);
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// AI 答疑：读最近日志 + 问模型，用大白话回答玩家的问题。
    /// 会消耗玩家自己的 API token，前端要提示。
    /// </summary>
    private static void HandleAsk(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        try
        {
            string body;
            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();
            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            var question = node?["question"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(question))
            {
                TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"请先描述你的问题\"}");
                return;
            }

            var cfg = BuWenFanChen.Memory.MemoryEngine.GetLlmConfig(null);
            if (!cfg.IsValid)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"读不到 API 配置（settings.json 的 ainpc 段），先配好再问\"}");
                return;
            }

            // 收集诊断信息
            var diag = new StringBuilder();
            diag.AppendLine("【当前配置】");
            try
            {
                diag.AppendLine($"回复上限={PromptConfig.ReplyLenTotal}（旁白{PromptConfig.ReplyLenNarration}+台词{PromptConfig.ReplyLenDialogue}）");
                diag.AppendLine($"情事模式={BuWenFanChen.NsfwConfig.Mode} 锁存={BuWenFanChen.NsfwConfig.LatchRounds}轮");
                diag.AppendLine($"抽取间隔={PromptConfig.ExtractInterval} 兜底={PromptConfig.FallbackEnabled} 角色卡开关={PromptConfig.CardEnabled}");
            }
            catch { }
            diag.AppendLine();
            diag.AppendLine("【最近 80 行日志】");
            try
            {
                // ★ 2026-10-26：CurrentDirectory 不一定是游戏根目录（启动器/其它 MOD 可能改过），
                //   找不到就按「插件程序集位置」往上推三级（plugins\BuWenFanChen → 游戏根）再试。
                var path = System.IO.Path.Combine(Environment.CurrentDirectory, "BepInEx", "LogOutput.log");
                if (!System.IO.File.Exists(path))
                {
                    var p = System.IO.Path.GetDirectoryName(typeof(ConsoleServer).Assembly.Location) ?? "";
                    for (var i = 0; i < 3 && !string.IsNullOrEmpty(p); i++) p = System.IO.Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(p))
                    {
                        var alt = System.IO.Path.Combine(p, "BepInEx", "LogOutput.log");
                        if (System.IO.File.Exists(alt)) path = alt;
                    }
                }
                var lines = System.IO.File.Exists(path) ? TailLines(path, 80) : new List<string>();
                if (lines.Count == 0) diag.AppendLine("（没找到 LogOutput.log，或日志为空）");
                else diag.AppendLine(string.Join("\n", lines));
            }
            catch { diag.AppendLine("（读日志失败）"); }

            var system = @"你是「不问凡尘」这个游戏 MOD 的技术支持助手。
玩家会用大白话描述遇到的问题，你需要结合他的配置和运行日志判断原因，给出**具体可执行的解决步骤**。

回答要求：
- 用大白话，不要用专业术语；必须提到具体在哪里改、改成什么
- 先给结论（是什么问题），再给步骤
- 如果日志里看不出问题，就明说「日志里没有相关报错」，然后给常见的排查方向
- 不要编造日志里没有的错误
- 控制在 300 字以内";

            var user = $"玩家的问题：{question}\n\n{diag}";
            var (content, err) = BuWenFanChen.Memory.LlmClient
                .CompleteAsync(cfg, system, user, 1500)
                .GetAwaiter().GetResult();

            if (content == null)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"" + JsonEscape("模型调用失败：" + err) + "\"}");
                return;
            }

            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"answer\":\"" + JsonEscape(content.Trim()) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("答疑失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>角色卡读写：GET 取、POST 存（阶段 5-A）</summary>
    private static void HandleCard(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        if (string.IsNullOrEmpty(npc))
        {
            TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少 npc 参数\"}");
            return;
        }

        if (ctx.Request.HttpMethod == "POST")
        {
            try
            {
                string body;
                using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();
                if (string.IsNullOrWhiteSpace(body))
                {
                    TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"内容为空\"}");
                    return;
                }
                var node = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject;
                if (node == null)
                {
                    TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"不是合法的 JSON 对象\"}");
                    return;
                }
                // 备份旧卡
                try
                {
                    var p0 = BuWenFanChen.Card.CardStore.PathOf(npc);
                    if (System.IO.File.Exists(p0))
                        System.IO.File.Copy(p0, p0 + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), overwrite: true);
                }
                catch { }

                // ★ 保留原卡的顶层元数据（前端表单只提交字段表内容，不含这些）
                try
                {
                    var old = BuWenFanChen.Card.CardStore.Load(npc);
                    if (old != null)
                    {
                        foreach (var mkey in new[] { "启用", "_来源", "_自动生成基准", "_显示名" })
                        {
                            if (node[mkey] != null || old[mkey] == null) continue;
                            node[mkey] = System.Text.Json.Nodes.JsonNode.Parse(old[mkey]!.ToJsonString());
                        }
                    }
                    if (node["启用"] == null) node["启用"] = true;   // 保存过的卡一律视为启用
                }
                catch { }

                var ok = BuWenFanChen.Card.CardStore.Save(npc, node);
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    ok ? "{\"ok\":true,\"msg\":\"已保存（下次进对话生效）\"}"
                       : "{\"ok\":false,\"msg\":\"保存失败\"}");
            }
            catch (Exception e)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"" + JsonEscape("保存失败：" + e.Message) + "\"}");
            }
            return;
        }

        // GET
        try
        {
            // ★ blank=1：还没有卡时，返回一份空白表单让玩家直接填
            if (ctx.Request.QueryString["blank"] == "1")
            {
                var blankCard = BuWenFanChen.Card.CardFields.NewCard();
                var blankFields = BuWenFanChen.Card.CardFields.ToJson().ToJsonString(
                    new System.Text.Json.JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    });
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":true,\"card\":" + blankCard.ToJsonString() +
                    ",\"fields\":" + blankFields + ",\"enabled\":false}");
                return;
            }

            var card = BuWenFanChen.Card.CardStore.Load(npc);
            if (card == null)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"这个角色还没有角色卡\"}");
                return;
            }
            // 2026-10-25: 返回结构化数据 + 字段表，前端按字段表渲染表单（对齐 1.1.2）
            var cardJson = card.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            var fieldsJson = BuWenFanChen.Card.CardFields.ToJson().ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"card\":" + cardJson +
                ",\"fields\":" + fieldsJson +
                ",\"enabled\":" + (BuWenFanChen.Card.CardStore.IsEnabled(card) ? "true" : "false") + "}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("读取失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>角色卡自动生成：玩家粘贴资料 → 调模型产出卡 JSON</summary>
    private static void HandleCardGenerate(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        var npc = ctx.Request.QueryString["npc"] ?? "";
        try
        {
            string body;
            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();
            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            var material = node?["material"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(material))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"请先粘贴官方角色资料\"}");
                return;
            }
            var (ok, data) = BuWenFanChen.Card.CardGenerator
                .GenerateAsync(npc, material)
                .GetAwaiter().GetResult();
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":" + (ok ? "true" : "false") +
                ",\"msg\":\"" + JsonEscape(ok ? "已生成，确认后点保存" : data) + "\"" +
                (ok ? ",\"json\":\"" + JsonEscape(data) + "\"" : "") + "}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("生成失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>读自动抓取的角色资料（card\{npc}.source.md）</summary>
    /// <summary>
    /// 从抓下来的游戏资料里解析出角色卡字段（规则解析，不花 API）。
    /// 资料格式固定：- **身份**：… / - **性格**：… / - **外貌**：… / - **善恶**：…
    /// 姓名从「## 角色设定」的「你是 XXX」里取；「## 角色背景」整段作为补充背景返回。
    /// </summary>
    private static void HandleCardSourceFields(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        if (string.IsNullOrEmpty(npc))
        {
            TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少 npc 参数\"}");
            return;
        }
        try
        {
            var material = BuWenFanChen.Card.CardSourceStore.Load(npc);
            if (string.IsNullOrWhiteSpace(material))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"还没抓到资料：进游戏和这个角色聊一句，插件会自动抓取\"}");
                return;
            }

            var fields = new System.Text.Json.Nodes.JsonObject();
            var background = "";
            var inBg = false;
            var inSpeak = false;
            var speakText = "";
            foreach (var rawLine in material.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("## "))
                {
                    inBg = line.Contains("角色背景");
                    // ★ 2026-10-27 主人定：「说话习惯」第 1 段（句子节奏/自称/称呼）填进人设「说话习惯」
                    inSpeak = line.Contains("说话习惯");
                    continue;
                }
                if (inBg)
                {
                    if (line.Length > 0) background += (background.Length > 0 ? "\n" : "") + line;
                    continue;
                }
                if (inSpeak)
                {
                    // ★ 2026-10-27 主人定：卡里放「句子节奏：」之后、到「接话须回应…」之前的内容
                    //   （每个角色这段不一样，可能含年龄/身份细节）；接话/行为规则留在游戏原文。
                    //   「接话须」或「本轮没有」都算停止点。
                    if (line.Length == 0) continue;
                    if (line.IndexOf("接话须") >= 0 || line.IndexOf("本轮没有") >= 0)
                    {
                        inSpeak = false;
                        continue;
                    }
                    speakText += (speakText.Length > 0 ? "\n" : "") + line;
                    continue;
                }
                var m = System.Text.RegularExpressions.Regex.Match(line,
                    @"^-\s*\*\*(姓名|身份|性格|外貌|善恶)\*\*\s*[：:]\s*(.+)$");
                if (!m.Success) continue;
                var key = m.Groups[1].Value;
                var val = m.Groups[2].Value.Trim();
                switch (key)
                {
                    case "姓名": fields["人设|姓名"] = val; break;
                    case "身份": fields["人设|身份"] = val; break;
                    case "性格": fields["人设|性格"] = val; break;
                    case "外貌": fields["人设|外貌穿着"] = val; break;
                    case "善恶": fields["人设|善恶立场"] = val; break;
                }
            }

            // ★ 2026-10-27：「说话习惯」第 1 段 → 人设「说话习惯」字段
            if (!string.IsNullOrWhiteSpace(speakText))
                fields["人设|说话习惯"] = speakText.Trim();

            // ★ 2026-10-29：姓名一律取静态表（官方名单）里的真名。
            //   原文「你是 奈宝·芊儿4K2」是化身/皮肤名，填进卡里就是脏名字。
            var staticName = BuWenFanChen.Memory.ConfigTable.GetStaticName(npc);
            if (!string.IsNullOrWhiteSpace(staticName))
                fields["人设|姓名"] = staticName;

            // 姓名兜底：资料里常见「你是 XXX」（静态表没有这个 id 时才用）
            if (!fields.ContainsKey("人设|姓名"))
            {
                var mm = System.Text.RegularExpressions.Regex.Match(material, @"你是\s*([^\s，,。、；;]+)");
                if (mm.Success) fields["人设|姓名"] = mm.Groups[1].Value.Trim();
            }

            // ★ 2026-10-27 主人定（B 方案）：背景不再填卡字段，改成写世界书「角色自身」（背景+年龄）。
            //   卡的「角色背景」字段已删；世界书「角色自身」才是背景的唯一来源。
            if (!string.IsNullOrWhiteSpace(background))
            {
                try
                {
                    var nmForBg = BuWenFanChen.Memory.MemoryEngine.ResolveName(npc);
                    var ageForBg = BuWenFanChen.Memory.ConfigTable.GetCachedAge(npc);
                    BuWenFanChen.Memory.RelationStore.UpdateSelfEntry(npc, nmForBg, ageForBg, background.Trim());
                }
                catch { }
            }

            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"fields\":" + fields.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }) +
                ",\"background\":\"" + JsonEscape(background) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("解析失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>删除角色卡（backup=true 默认：改名成 .删除备份_时间 而不是真删）。</summary>
    private static void HandleCardDelete(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        var backup = ctx.Request.QueryString["backup"] != "false";
        // ★ 2026-10-29：withMemory=true → 卡/记忆/关系/世界书/资料 一起清（全部挪走，不真删）
        var withMemory = ctx.Request.QueryString["withMemory"] == "true";
        if (string.IsNullOrEmpty(npc))
        {
            TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少 npc 参数\"}");
            return;
        }
        try
        {
            var rootDir = BuWenFanChen.Memory.MemoryPaths.Root;

            if (withMemory)
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var binDir = System.IO.Path.Combine(rootDir, "_已删除_" + stamp, npc);
                System.IO.Directory.CreateDirectory(binDir);
                var movedFiles = 0;

                // ① card 目录里属于这个角色的全部文件（卡本体 / 世界书 / 资料 / 备份）
                try
                {
                    var cardDir = System.IO.Path.Combine(rootDir, "card");
                    if (System.IO.Directory.Exists(cardDir))
                    {
                        foreach (var f in System.IO.Directory.GetFiles(cardDir))
                        {
                            var fn = System.IO.Path.GetFileName(f);
                            if (!System.Text.RegularExpressions.Regex.IsMatch(fn, @"-" + npc + @"(\.|$)")) continue;
                            System.IO.File.Move(f, System.IO.Path.Combine(binDir, fn), overwrite: true);
                            movedFiles++;
                        }
                    }
                }
                catch { }

                // ② 记忆目录
                try
                {
                    var memDir = BuWenFanChen.Memory.MemoryPaths.MemDir(npc);
                    if (System.IO.Directory.Exists(memDir))
                        System.IO.Directory.Move(memDir, System.IO.Path.Combine(binDir, "memory"));
                }
                catch { }

                // ③ 关系数据
                try
                {
                    var memDir = BuWenFanChen.Memory.MemoryPaths.MemDir(npc);
                    var prefixDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(memDir));
                    if (!string.IsNullOrEmpty(prefixDir))
                    {
                        var relPath = System.IO.Path.Combine(prefixDir, "relation", npc + ".json");
                        if (System.IO.File.Exists(relPath))
                            System.IO.File.Move(relPath, System.IO.Path.Combine(binDir, "relation.json"), overwrite: true);
                    }
                }
                catch { }

                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":true,\"msg\":\"" + JsonEscape($"已删除（{movedFiles} 个文件 + 记忆/关系 都挪到 data\\_已删除_{stamp}\\{npc}）") + "\"}");
                return;
            }

            var path = BuWenFanChen.Card.CardStore.ResolvePath(npc);
            if (path == null || !System.IO.File.Exists(path))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"这个角色没有角色卡\"}");
                return;
            }
            if (backup)
                System.IO.File.Move(path, path + ".删除备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), overwrite: true);
            else
                System.IO.File.Delete(path);
            TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true,\"msg\":\"已删除\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("删除失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>删除角色世界书（backup=true 默认：改名成 .删除备份_时间）。</summary>
    private static void HandleLorebookDelete(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        var backup = ctx.Request.QueryString["backup"] != "false";
        if (string.IsNullOrEmpty(npc))
        {
            TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少 npc 参数\"}");
            return;
        }
        try
        {
            var dir = System.IO.Path.GetDirectoryName(BuWenFanChen.Memory.MemoryPaths.CardPath(npc)) ?? ".";
            var files = System.IO.Directory.GetFiles(dir, "*-" + npc + ".lorebook.json");
            var path = files.Length > 0 ? files[0] : null;
            if (path == null)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"这个角色没有角色世界书\"}");
                return;
            }
            if (backup)
                System.IO.File.Move(path, path + ".删除备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), overwrite: true);
            else
                System.IO.File.Delete(path);
            TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true,\"msg\":\"已删除\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("删除失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>自动好感度统计：当前（或指定）NPC 的每轮变化 + 累计。</summary>
    private static void HandleIntimacyHistory(HttpListenerContext ctx)
    {
        try
        {
            var npc = ctx.Request.QueryString["npc"];
            if (string.IsNullOrEmpty(npc)) npc = BuWenFanChen.IntimacyJudge.LastNpc;
            var list = BuWenFanChen.IntimacyJudge.GetHistory(npc);
            var rows = new System.Text.StringBuilder();
            rows.Append("[");
            int total = 0;
            for (int i = 0; i < list.Count; i++)
            {
                total += list[i];
                if (i > 0) rows.Append(",");
                rows.Append("{\"turn\":").Append(i + 1)
                    .Append(",\"delta\":").Append(list[i])
                    .Append(",\"total\":").Append(total).Append("}");
            }
            rows.Append("]");
            var name = string.IsNullOrEmpty(npc) ? "" : BuWenFanChen.Memory.MemoryEngine.ResolveName(npc);
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"npc\":\"" + JsonEscape(npc ?? "") + "\",\"npcName\":\"" + JsonEscape(name ?? "")
                + "\",\"total\":" + total + ",\"rows\":" + rows + "}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape(e.Message) + "\"}");
        }
    }

    private static void HandleCardSource(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        if (string.IsNullOrEmpty(npc))
        {
            TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少 npc 参数\"}");
            return;
        }
        try
        {
            var material = BuWenFanChen.Card.CardSourceStore.Load(npc);
            if (string.IsNullOrWhiteSpace(material))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"还没抓到资料：进游戏和这个角色聊一句，插件会自动抓取\"}");
                return;
            }
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"material\":\"" + JsonEscape(material) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("读取失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>角色卡检查（规则粗筛 + 模型判定）</summary>
    private static void HandleCardCheck(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        var mode = ctx.Request.QueryString["mode"] ?? "1";
        var useModel = mode != "0";
        try
        {
            var (ok, items, msg) = BuWenFanChen.Card.CardChecker.CheckAsync(npc, useModel).GetAwaiter().GetResult();
            if (!ok)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"" + JsonEscape(msg) + "\"}");
                return;
            }
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"msg\":\"").Append(JsonEscape(msg)).Append("\",\"items\":[");
            var first = true;
            foreach (var it in items)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"section\":\"").Append(JsonEscape(it.Section))
                  .Append("\",\"field\":\"").Append(JsonEscape(it.Field))
                  .Append("\",\"value\":\"").Append(JsonEscape(it.Value))
                  .Append("\",\"verdict\":\"").Append(it.Verdict)
                  .Append("\",\"reason\":\"").Append(JsonEscape(it.Reason))
                  .Append("\",\"suggestion\":\"").Append(JsonEscape(it.Suggestion))
                  .Append("\"}");
            }
            sb.Append("]}");
            TryWrite(ctx, 200, "application/json; charset=utf-8", sb.ToString());
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("检查失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>应用玩家勾选的检查建议</summary>
    private static void HandleCardCheckApply(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        try
        {
            string body;
            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();
            var node = JsonNode.Parse(body) as JsonObject;
            var npc = node?["npc"]?.GetValue<string>() ?? "";
            var adopted = node?["采纳"] as JsonArray;
            if (string.IsNullOrEmpty(npc) || adopted == null)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"参数不完整\"}");
                return;
            }
            var doBackup = true;
            try { doBackup = node?["backup"]?.GetValue<bool>() ?? true; } catch { }
            var (ok, msg) = BuWenFanChen.Card.CardChecker.ApplySuggestions(npc, adopted, doBackup);
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":" + (ok ? "true" : "false") + ",\"msg\":\"" + JsonEscape(msg) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("应用失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>动态试演：用当前卡当场演一段（不进游戏验收）</summary>
    private static void HandleCardPreview(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json; charset=utf-8", "{\"成功\":false,\"文本\":\"只接受 POST\"}");
            return;
        }
        try
        {
            string body;
            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();
            var node = JsonNode.Parse(body) as JsonObject;
            var npc = node?["npc"]?.GetValue<string>() ?? "";
            var sceneIdx = 0;
            try { sceneIdx = node?["场景"]?.GetValue<int>() ?? 0; } catch { }
            var model = node?["模型"]?.GetValue<string>() ?? "";
            var nsfw = false;
            try { nsfw = node?["NSFW"]?.GetValue<bool>() ?? false; } catch { }
            var extra = node?["台词"]?.GetValue<string>() ?? "";

            if (string.IsNullOrEmpty(npc))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"成功\":false,\"文本\":\"缺少 npc 参数\"}");
                return;
            }

            var (ok, text, emotion, msg) = BuWenFanChen.Card.CardGenerator
                .PreviewAsync(npc, sceneIdx, model, nsfw, extra).GetAwaiter().GetResult();

            var result = new JsonObject
            {
                ["成功"] = ok,
                ["文本"] = text,
                ["情绪"] = emotion,
                ["说明"] = msg,
            };
            TryWrite(ctx, 200, "application/json; charset=utf-8", result.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"成功\":false,\"文本\":\"" + JsonEscape("试演失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>按「AI生卡」重写，返回逐项差异（不直接覆盖）</summary>
    private static void HandleCardRewrite(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        try
        {
            var (ok, data) = BuWenFanChen.Card.CardGenerator
                .RewritePreviewAsync(npc).GetAwaiter().GetResult();
            if (ok && data != null)
            {
                var json = data.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                TryWrite(ctx, 200, "application/json; charset=utf-8", json);
            }
            else
            {
                var msg = data?["消息"]?.GetValue<string>() ?? "重写失败";
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"成功\":false,\"消息\":\"" + JsonEscape(msg) + "\"}");
            }
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"成功\":false,\"消息\":\"" + JsonEscape("重写失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>应用玩家勾选的替换项（POST body: { npc, 新卡, 采纳:["区块·字段",...] }）</summary>
    private static void HandleCardRewriteApply(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        try
        {
            string body;
            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();
            var node = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject;
            var npc = node?["npc"]?.GetValue<string>() ?? "";
            var newCard = node?["新卡"] as System.Text.Json.Nodes.JsonObject;
            var adopted = node?["采纳"] as System.Text.Json.Nodes.JsonArray;
            if (string.IsNullOrEmpty(npc) || newCard == null || adopted == null)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"参数不完整\"}");
                return;
            }
            var (ok, msg) = BuWenFanChen.Card.CardGenerator.ApplyRewrite(npc, newCard, adopted);
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":" + (ok ? "true" : "false") + ",\"msg\":\"" + JsonEscape(msg) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("应用失败：" + e.Message) + "\"}");
        }
    }

    // ==================== NSFW 模块（4 个文件结构化读写）====================

    private static string NsfwDir =>
        System.IO.Path.Combine(PromptConfig.Dir, "nsfw");

    /// <summary>把文件切成「注释头（# 开头的行 + 空行）+ 正文」</summary>
    private static (string Header, string Body) SplitHeaderBody(string text)
    {
        var lines = (text ?? "").Split('\n');
        var i = 0;
        var header = new StringBuilder();
        while (i < lines.Length)
        {
            var l = lines[i].Trim();
            if (l.StartsWith("#") || l.Length == 0) { header.AppendLine(lines[i]); i++; }
            else break;
        }
        var body = string.Join("\n", lines.Skip(i)).Trim('\n');
        return (header.ToString(), body);
    }

    private static (List<string> A, List<string> B) ReadKeywordsFile()
    {
        var A = new List<string>();
        var B = new List<string>();
        try
        {
            var path = System.IO.Path.Combine(NsfwDir, "情事关键词.txt");
            if (System.IO.File.Exists(path))
            {
                var lines = System.IO.File.ReadAllText(path, Encoding.UTF8).Split('\n');
                var cur = "";
                foreach (var raw in lines)
                {
                    var l = raw.Trim();
                    if (l.StartsWith("#") || l.Length == 0) continue;
                    if (l.Contains("A 级") || l.Contains("A级")) { cur = "A"; continue; }
                    if (l.Contains("B 级") || l.Contains("B级")) { cur = "B"; continue; }
                    foreach (var w in l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (cur == "A" && !A.Contains(w)) A.Add(w);
                        else if (cur == "B" && !B.Contains(w)) B.Add(w);
                    }
                }
            }
        }
        catch { }
        return (A, B);
    }

    private static string ReadNsfwBody(string file)
    {
        try
        {
            var path = System.IO.Path.Combine(NsfwDir, file);
            if (!System.IO.File.Exists(path)) return "";
            return SplitHeaderBody(System.IO.File.ReadAllText(path, Encoding.UTF8)).Body;
        }
        catch { return ""; }
    }

    private static void SaveNsfwBody(string file, string body)
    {
        try
        {
            var path = System.IO.Path.Combine(NsfwDir, file);
            var header = "";
            if (System.IO.File.Exists(path))
                header = SplitHeaderBody(System.IO.File.ReadAllText(path, Encoding.UTF8)).Header;
            var text = header + (body ?? "").Trim() + "\n";
            System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch { }
    }

    private static void HandleNsfw(HttpListenerContext ctx)
    {
        try
        {
            System.IO.Directory.CreateDirectory(NsfwDir);

            if (ctx.Request.HttpMethod == "POST")
            {
                string body;
                using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();
                var node = JsonNode.Parse(body) as JsonObject;
                if (node == null)
                {
                    TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"参数不合法\"}");
                    return;
                }

                // ① 关键词
                var A = node["keywordsA"] as JsonArray ?? new JsonArray();
                var B = node["keywordsB"] as JsonArray ?? new JsonArray();
                var kwPath = System.IO.Path.Combine(NsfwDir, "情事关键词.txt");
                var kwHeader = "";
                if (System.IO.File.Exists(kwPath))
                    kwHeader = SplitHeaderBody(System.IO.File.ReadAllText(kwPath, Encoding.UTF8)).Header;
                var sb = new StringBuilder();
                sb.Append(kwHeader);
                sb.AppendLine("【A 级】命中任意一个即触发");
                sb.AppendLine(string.Join(" ", A.Select(x => x?.GetValue<string>() ?? "").Where(w => w.Length > 0)));
                sb.AppendLine();
                sb.AppendLine("【B 级】命中两个及以上才触发");
                sb.AppendLine(string.Join(" ", B.Select(x => x?.GetValue<string>() ?? "").Where(w => w.Length > 0)));
                System.IO.File.WriteAllText(kwPath, sb.ToString(), new UTF8Encoding(false));

                // ②③④ 正文
                SaveNsfwBody("高级破甲.txt", node["armor"]?.GetValue<string>() ?? "");
                SaveNsfwBody("情事用语规范.txt", node["guifan"]?.GetValue<string>() ?? "");
                SaveNsfwBody("情事用语规范_自定义.txt", node["custom"]?.GetValue<string>() ?? "");

                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true,\"msg\":\"已保存（存盘即生效）\"}");
                return;
            }

            // 读
            var kw = ReadKeywordsFile();
            var result = new JsonObject
            {
                ["keywordsA"] = new JsonArray(kw.A.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                ["keywordsB"] = new JsonArray(kw.B.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                ["armor"] = ReadNsfwBody("高级破甲.txt"),
                ["guifan"] = ReadNsfwBody("情事用语规范.txt"),
                ["custom"] = ReadNsfwBody("情事用语规范_自定义.txt"),
            };
            TryWrite(ctx, 200, "application/json; charset=utf-8", result.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("操作失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>
    /// Token 花费预览（2026-10-25 主人要）：列出每块的估算注入字数，让玩家知道开什么消耗多少。
    /// 数据是「估算」，来自实测（prefix 4059 / tail 4956 / input 214 那次拆分），不是精确统计。
    /// 中文 ≈ 0.6 token/字 做保守估算。
    /// </summary>
    private static void HandleTokens(HttpListenerContext ctx)
    {
        try
        {
            var items = new JsonArray();

            void Add(string name, int chars, bool enabled, bool always, string note)
            {
                items.Add(new JsonObject
                {
                    ["name"] = name,
                    ["chars"] = chars,
                    ["enabled"] = enabled,
                    ["always"] = always,
                    ["note"] = note,
                });
            }

            // 游戏原文（固定，改不了）
            Add("角色属性 + 设定 + 背景", 2300, true, true, "游戏自带");
            Add("游戏契约（输出格式等）", 2800, true, true, "游戏自带");
            Add("历史对话（约 2 条）", 300, true, true, "游戏自带");

            // MOD 注入
            Add("最高指令（破甲）", 556, true, true, "常驻，不可关");
            Add("字数规则", 100, true, true, "常驻");
            Add("世界书", 700, PromptConfig.WorldbookEnabled, false, "命中才注入，最多 700 字");
            Add("角色卡", 1446, PromptConfig.CardEnabled, false, "每个角色不同");
            Add("角色世界书", 600, PromptConfig.LorebookEnabled, false, "命中才注入");
            Add("NSFW 板块", 827, PromptConfig.NsfwEnabled, false, "命中才注入");
            Add("死命令（语音台本）", 495, PromptConfig.JsonIntensifyActive, false, "装语音 MOD 才注入");
            Add("长期记忆", PromptConfig.MemoryInjectLimit, PromptConfig.MemoryEnabled, false, "按档位，最多这么多");

            var totalAll = 0;
            var activeChars = 0;
            foreach (var it in items)
            {
                var c = it["chars"]!.GetValue<int>();
                totalAll += c;
                if (it["enabled"]!.GetValue<bool>()) activeChars += c;
            }

            // 费用估算（DeepSeek 官方空闲时段价，元/百万 token；输入按未命中缓存算）
            var outChars = PromptConfig.ReplyLenTotal > 0 ? PromptConfig.ReplyLenTotal : 200;
            var outTokens = (int)Math.Round(outChars * 0.6);
            var inTokens = (int)Math.Round(totalAll * 0.6);
            var isPro = (PromptConfig.ModModel ?? "").ToLowerInvariant().Contains("pro");
            var priceIn = isPro ? 4.5 : 1.0;
            var priceOut = isPro ? 13.5 : 4.0;
            var costIn = Math.Round(inTokens / 1000000.0 * priceIn, 4);
            var costOut = Math.Round(outTokens / 1000000.0 * priceOut, 4);

            // ★ 2026-10-27：自动好感度 = 每轮额外一次 API 调用（不占主对话 context）
            //   配了本地判断模型（autoIntimacyApiUrl）就不算钱。
            var extraLocal = !string.IsNullOrWhiteSpace(PromptConfig.AutoIntimacyApiUrl);
            var extraInTokens = PromptConfig.AutoIntimacyEnabled ? 1200 : 0;
            var extraOutTokens = PromptConfig.AutoIntimacyEnabled ? 16 : 0;
            var costExtra = (PromptConfig.AutoIntimacyEnabled && !extraLocal)
                ? Math.Round(extraInTokens / 1000000.0 * priceIn + extraOutTokens / 1000000.0 * priceOut, 4)
                : 0;

            var result = new JsonObject
            {
                ["items"] = items,
                ["totalAll"] = totalAll,
                ["estTokens"] = inTokens,
                ["outTokens"] = outTokens,
                ["costIn"] = costIn,
                ["costOut"] = costOut,
                ["costTotal"] = Math.Round(costIn + costOut + costExtra, 4),
                ["extraCall"] = new JsonObject
                {
                    ["enabled"] = PromptConfig.AutoIntimacyEnabled,
                    ["local"] = extraLocal,
                    ["inTokens"] = extraInTokens,
                    ["outTokens"] = extraOutTokens,
                    ["cost"] = costExtra,
                    ["note"] = "自动好感度：每轮对话额外调一次判定模型",
                },
                ["priceModel"] = isPro ? "deepseek-v4-pro" : "deepseek-flash",
                ["note"] = "各项上限相加的预估，实际通常更少",
                ["memoryLimit"] = PromptConfig.MemoryInjectLimit,
            };
            TryWrite(ctx, 200, "application/json; charset=utf-8", result.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("计算失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>草稿读写：GET 取 / POST 带 action=apply|discard</summary>
    private static void HandleCardDraft(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        var action = ctx.Request.QueryString["action"] ?? "";

        if (ctx.Request.HttpMethod == "POST" && action == "apply")
        {
            var ok = BuWenFanChen.Card.CardStore.ApplyDraft(npc, out var msg);
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":" + (ok ? "true" : "false") + ",\"msg\":\"" + JsonEscape(msg) + "\"}");
            return;
        }
        if (ctx.Request.HttpMethod == "POST" && action == "discard")
        {
            BuWenFanChen.Card.CardStore.DiscardDraft(npc);
            TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true,\"msg\":\"已放弃优化\"}");
            return;
        }

        // GET：读草稿
        try
        {
            var draft = BuWenFanChen.Card.CardStore.LoadDraft(npc);
            if (draft == null)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"没有待应用的优化\"}");
                return;
            }
            var json = draft.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"json\":\"" + JsonEscape(json) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("读取失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>存档列表 / 当前存档 / 切换（F1）</summary>
    private static void HandleSaves(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod == "POST")
            {
                var id = ctx.Request.QueryString["id"] ?? "";
                if (string.IsNullOrEmpty(id))
                {
                    // 恢复自动判定
                    BuWenFanChen.Memory.SaveSlot.ClearManual();
                    TryWrite(ctx, 200, "application/json; charset=utf-8",
                        "{\"ok\":true,\"msg\":\"已恢复自动判定\",\"current\":\"" +
                        JsonEscape(BuWenFanChen.Memory.SaveSlot.CurrentId) + "\"}");
                    return;
                }
                var ok = BuWenFanChen.Memory.SaveSlot.Pick(id);
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":" + (ok ? "true" : "false") +
                    ",\"msg\":\"" + JsonEscape(ok ? "已切换，重启游戏后生效" : "找不到这个存档") +
                    "\",\"current\":\"" + JsonEscape(BuWenFanChen.Memory.SaveSlot.CurrentId) + "\"}");
                return;
            }

            var slots = BuWenFanChen.Memory.SaveSlot.List();
            var sb = new StringBuilder();
            sb.Append("{\"current\":\"").Append(JsonEscape(BuWenFanChen.Memory.SaveSlot.CurrentId))
              .Append("\",\"manual\":").Append(BuWenFanChen.Memory.SaveSlot.ManuallyPicked ? "true" : "false")
              .Append(",\"slots\":[");
            var first = true;
            foreach (var s in slots)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":\"").Append(JsonEscape(s.Id))
                  .Append("\",\"version\":\"").Append(JsonEscape(s.Version))
                  .Append("\",\"lastWrite\":\"").Append(s.LastWrite.ToString("yyyy-MM-dd HH:mm"))
                  .Append("\"}");
            }
            sb.Append("]}");
            TryWrite(ctx, 200, "application/json; charset=utf-8", sb.ToString());
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("读取失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>列出「哪些存档里存着这个角色的记忆」</summary>
    private static void HandleMemoryScopes(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        try
        {
            var list = BuWenFanChen.Memory.MemoryTransfer.ListScopesWith(npc);
            var sb = new StringBuilder();
            sb.Append("{\"current\":\"").Append(JsonEscape(BuWenFanChen.Memory.MemoryPaths.Scope)).Append("\",\"scopes\":[");
            var first = true;
            foreach (var it in list)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"scope\":\"").Append(JsonEscape(it.Scope))
                  .Append("\",\"lastWrite\":\"").Append(it.LastWrite.ToString("yyyy-MM-dd HH:mm"))
                  .Append("\"}");
            }
            sb.Append("]}");
            TryWrite(ctx, 200, "application/json; charset=utf-8", sb.ToString());
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("读取失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>把别的存档里这个角色的记忆导入到当前存档</summary>
    private static void HandleMemoryImport(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        var from = ctx.Request.QueryString["from"] ?? "";
        try
        {
            var err = BuWenFanChen.Memory.MemoryTransfer.ImportFromScope(npc, from);
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                err == null
                    ? "{\"ok\":true,\"msg\":\"已从该存档导入记忆（原记忆已备份）\"}"
                    : "{\"ok\":false,\"msg\":\"" + JsonEscape(err) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("导入失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>列出这个角色的所有卡 + 当前选中的是哪张</summary>
    private static void HandleCardList(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        try
        {
            var cards = BuWenFanChen.Card.CardStore.ListCards(npc);
            var pick = BuWenFanChen.Card.CardStore.GetPick(npc);
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"pick\":\"").Append(JsonEscape(pick)).Append("\",\"cards\":[");
            var first = true;
            foreach (var c in cards)
            {
                if (!first) sb.Append(',');
                first = false;
                var label = string.IsNullOrEmpty(c.Name) ? "默认卡" : c.Name;
                sb.Append("{\"name\":\"").Append(JsonEscape(c.Name))
                  .Append("\",\"label\":\"").Append(JsonEscape(label))
                  .Append("\",\"file\":\"").Append(JsonEscape(System.IO.Path.GetFileName(c.Path)))
                  .Append("\"}");
            }
            sb.Append("]}");
            TryWrite(ctx, 200, "application/json; charset=utf-8", sb.ToString());
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("读取失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>设置当前存档里这个角色用哪张卡（name 为空 = 默认卡）</summary>
    private static void HandleCardPick(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        var npc = ctx.Request.QueryString["npc"] ?? "";
        var name = ctx.Request.QueryString["name"] ?? "";
        try
        {
            var ok = BuWenFanChen.Card.CardStore.SetPick(npc, name);
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                ok ? "{\"ok\":true,\"msg\":\"已切换（下次进对话生效）\"}"
                   : "{\"ok\":false,\"msg\":\"保存失败\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>把当前卡另存为一张新卡（复制文件，不覆盖）</summary>
    private static void HandleCardSaveAs(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        var npc = ctx.Request.QueryString["npc"] ?? "";
        var name = ctx.Request.QueryString["name"] ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"卡名不能为空\"}");
            return;
        }
        try
        {
            var src = BuWenFanChen.Card.CardStore.ResolvePath(npc);
            if (src == null)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"当前没有可复制的卡\"}");
                return;
            }

            // 新文件名：{角色名}-{代码}-{卡名}.json
            var srcName = System.IO.Path.GetFileNameWithoutExtension(src);
            var safe = BuWenFanChen.Card.CardStore.SanitizeFileName(name);
            if (string.IsNullOrEmpty(safe))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"卡名不合法\"}");
                return;
            }
            var dir = System.IO.Path.GetDirectoryName(src) ?? ".";
            // 去掉可能已有的卡名后缀，再拼新卡名
            var baseName = srcName;
            var dash = srcName.IndexOf("-" + npc + "-", StringComparison.Ordinal);
            if (dash >= 0) baseName = srcName.Substring(0, dash + npc.Length + 1);
            else if (srcName.EndsWith("-" + npc)) baseName = srcName.Substring(0, srcName.Length - npc.Length - 1);

            var newPath = System.IO.Path.Combine(dir, (baseName.Length > 0 ? baseName : npc) + "-" + safe + ".json");
            if (System.IO.File.Exists(newPath))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"已经有一张叫「" + JsonEscape(safe) + "」的卡了\"}");
                return;
            }
            System.IO.File.Copy(src, newPath);
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"msg\":\"已另存为「" + JsonEscape(safe) + "」，可在上面的下拉里切换\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("另存失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>世界书文件列表</summary>
    private static void HandleWorldbookList(HttpListenerContext ctx)
    {
        try
        {
            var list = BuWenFanChen.Memory.Worldbook.ListBooks();
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"dir\":\"").Append(JsonEscape(BuWenFanChen.Memory.Worldbook.Dir))
              .Append("\",\"books\":[").Append(string.Join(",", list.Select(b => "\"" + JsonEscape(b) + "\""))).Append("]}");
            TryWrite(ctx, 200, "application/json; charset=utf-8", sb.ToString());
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape(e.Message) + "\"}");
        }
    }

    /// <summary>世界书读 / 写 / 删</summary>
    private static void HandleWorldbook(HttpListenerContext ctx)
    {
        var name = ctx.Request.QueryString["name"] ?? "";
        var action = ctx.Request.QueryString["action"] ?? "";

        // 本地解析设定文本 → 条目（不保存）
        if (ctx.Request.HttpMethod == "POST" && action == "parse")
        {
            try
            {
                string body;
                using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();
                var entries = BuWenFanChen.Memory.Worldbook.ParseFromRaw(body);
                TryWrite(ctx, 200, "application/json; charset=utf-8", entries.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }));
            }
            catch (Exception e)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"" + JsonEscape("解析失败：" + e.Message) + "\"}");
            }
            return;
        }

        // 删
        if (ctx.Request.HttpMethod == "POST" && action == "delete")
        {
            var ok = BuWenFanChen.Memory.Worldbook.DeleteBook(name);
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":" + (ok ? "true" : "false") + ",\"msg\":\"" + JsonEscape(ok ? "已删除（原件已备份）" : "删除失败") + "\"}");
            return;
        }

        // 写
        if (ctx.Request.HttpMethod == "POST")
        {
            try
            {
                string body;
                using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();
                if (string.IsNullOrWhiteSpace(name)) name = "未命名";
                var node = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject;
                if (node == null)
                {
                    TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"不是合法的 JSON 对象\"}");
                    return;
                }
                if (!node.ContainsKey("name")) node["name"] = name;
                var ok = BuWenFanChen.Memory.Worldbook.SaveBook(name, node);
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    ok ? "{\"ok\":true,\"msg\":\"已保存（下一轮生效）\"}" : "{\"ok\":false,\"msg\":\"保存失败\"}");
            }
            catch (Exception e)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"" + JsonEscape("保存失败：" + e.Message) + "\"}");
            }
            return;
        }

        // 读
        try
        {
            var book = BuWenFanChen.Memory.Worldbook.LoadBook(name);
            if (book == null)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"没有这本世界书\"}");
                return;
            }
            var json = book.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"json\":\"" + JsonEscape(json) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape(e.Message) + "\"}");
        }
    }

    /// <summary>从已有角色资料推断世界书（调模型，结果只是草稿）</summary>
    private static void HandleWorldbookInfer(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        try
        {
            var (ok, data, matChars) = BuWenFanChen.Memory.WorldbookInfer
                .InferAsync()
                .GetAwaiter().GetResult();
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":" + (ok ? "true" : "false") +
                ",\"msg\":\"" + JsonEscape(ok ? $"已推断（用了 {matChars} 字资料），确认后点保存" : data) + "\"" +
                (ok ? ",\"json\":\"" + JsonEscape(data) + "\"" : "") + "}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("推断失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>角色世界书：GET 读 / POST 写 / action=gen 从关系网重新生成</summary>
    private static void HandleCardLorebook(HttpListenerContext ctx)
    {
        var npc = ctx.Request.QueryString["npc"] ?? "";
        var action = ctx.Request.QueryString["action"] ?? "";
        if (string.IsNullOrEmpty(npc))
        {
            TryWrite(ctx, 400, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少 npc 参数\"}");
            return;
        }

        // 角色名（生成文件名要用）★ 优先配置表初始名
        string name = "";
        try { name = BuWenFanChen.Memory.ConfigTable.GetBaseName(npc); } catch { }
        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = BuWenFanChen.Memory.MemoryStore.Load(npc).Meta.NpcName ?? ""; } catch { }
        }
        var path = BuWenFanChen.Memory.RelationStore.LorebookPathOf(npc, name);

        // 从关系网重新生成
        if (ctx.Request.HttpMethod == "POST" && action == "gen")
        {
            try
            {
                var ok = BuWenFanChen.Memory.RelationStore.WriteLorebookIfAbsent(npc, name, overwrite: true,
                    selfAge: BuWenFanChen.Memory.ConfigTable.GetCachedAge(npc),
                    selfBackground: BuWenFanChen.Memory.ConfigTable.GetCachedBg(npc));
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    ok ? "{\"ok\":true,\"msg\":\"已从游戏关系网重新生成\"}"
                       : "{\"ok\":false,\"msg\":\"没有可用的关系数据（先进游戏和这个角色聊一句，插件会自动抓）\"}");
            }
            catch (Exception e)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"" + JsonEscape("生成失败：" + e.Message) + "\"}");
            }
            return;
        }

        // 写
        if (ctx.Request.HttpMethod == "POST")
        {
            try
            {
                string body;
                using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();
                var node = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject;
                if (node == null)
                {
                    TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"不是合法的 JSON 对象\"}");
                    return;
                }
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                if (System.IO.File.Exists(path))
                {
                    try { System.IO.File.Copy(path, path + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true); } catch { }
                }
                System.IO.File.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), new UTF8Encoding(false));
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":true,\"msg\":\"已保存（下一轮生效）\"}");
            }
            catch (Exception e)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"" + JsonEscape("保存失败：" + e.Message) + "\"}");
            }
            return;
        }

        // 读
        try
        {
            if (!System.IO.File.Exists(path))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"还没有角色世界书：进游戏和这个角色聊一句会自动抓关系，或者点「从关系网重新生成」\"}");
                return;
            }
            var text = System.IO.File.ReadAllText(path, Encoding.UTF8);
            var node2 = System.Text.Json.Nodes.JsonNode.Parse(text);
            var pretty = node2?.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) ?? text;
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"json\":\"" + JsonEscape(pretty) + "\",\"file\":\"" +
                JsonEscape(System.IO.Path.GetFileName(path)) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("读取失败：" + e.Message) + "\"}");
        }
    }


    /// <summary>一键改名：把所有角色名（记忆/角色卡/关系）换成配置表真名</summary>
    /// <summary>
    /// 迁移执行：把 1.1.2 的角色卡 + 记忆迁到 V1.2。
    /// body: { dir, items: [{file,id,name,补充:{姓名,身份,善恶立场}}] }
    /// </summary>
    private static void HandleMigrateApply(HttpListenerContext ctx)
    {
        try
        {
            var body = "";
            try
            {
                using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();
            }
            catch { }

            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            var dir = node?["dir"]?.GetValue<string>() ?? "";
            var items = node?["items"] as System.Text.Json.Nodes.JsonArray;

            var doneCards = new System.Text.Json.Nodes.JsonArray();
            var doneIds = new System.Text.Json.Nodes.JsonArray();
            var failedCards = new System.Text.Json.Nodes.JsonArray();
            var doneMems = new System.Text.Json.Nodes.JsonArray();
            var failedMems = new System.Text.Json.Nodes.JsonArray();
            var emptyCards = new System.Text.Json.Nodes.JsonArray();

            if (items != null && !string.IsNullOrWhiteSpace(dir))
            {
                foreach (var it in items)
                {
                    if (it is not System.Text.Json.Nodes.JsonObject o) continue;
                    var file = o["file"]?.GetValue<string>() ?? "";
                    var id = o["id"]?.GetValue<string>() ?? "";
                    var name = o["name"]?.GetValue<string>() ?? "";
                    var extra = o["补充"] as System.Text.Json.Nodes.JsonObject;

                    // ★ 2026-10-29：前端表格里可以改姓名 —— 卡和记忆统一用改后的名字，避免两边不一致
                    try
                    {
                        var picked = extra?["姓名"]?.GetValue<string>()?.Trim();
                        if (!string.IsNullOrWhiteSpace(picked)) name = picked;
                    }
                    catch { }

                    try
                    {
                        var hasContent = MigrateOneCard(dir, file, id, name, extra);
                        doneCards.Add(name);
                        doneIds.Add(id);
                        if (!hasContent) emptyCards.Add(name);
                    }
                    catch (Exception e)
                    {
                        BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                            .LogWarning($"[迁移] 角色卡 {name}（{id}）失败：{e.GetType().Name} {e.Message}\n{e.StackTrace}");
                        failedCards.Add(name + ": " + (e.Message ?? "失败"));
                    }

                    // 没聊过就没有记忆文件，跳过不算失败
                    try
                    {
                        var memFile = System.IO.Path.Combine(dir, "memory", file);
                        if (System.IO.File.Exists(memFile)) { MigrateOneMemory(dir, file, id, name); doneMems.Add(name); }
                    }
                    catch (Exception e)
                    {
                        BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                            .LogWarning($"[迁移] 记忆 {name}（{id}）失败：{e.GetType().Name} {e.Message}\n{e.StackTrace}");
                        failedMems.Add(name + ": " + (e.Message ?? "失败"));
                    }
                }
            }

            var allFailed = new System.Text.Json.Nodes.JsonArray();
            foreach (var f in failedCards) allFailed.Add(System.Text.Json.Nodes.JsonNode.Parse(f.ToJsonString()));
            foreach (var f in failedMems) allFailed.Add(System.Text.Json.Nodes.JsonNode.Parse(f.ToJsonString()));

            TryWrite(ctx, 200, "application/json", new System.Text.Json.Nodes.JsonObject
            {
                ["ok"] = true,
                ["cards"] = new System.Text.Json.Nodes.JsonObject { ["ok"] = doneCards.Count, ["fail"] = failedCards.Count },
                ["memories"] = new System.Text.Json.Nodes.JsonObject { ["ok"] = doneMems.Count, ["fail"] = failedMems.Count },
                ["failedDetail"] = allFailed,
                ["emptyCards"] = emptyCards,
                ["doneIds"] = doneIds,
                ["done"] = doneCards,
                // ★ 2026-10-29：同一个 JsonNode 不能挂两个父节点。
                //   这里原来是 ["failed"] = allFailed —— allFailed 已经挂在 failedDetail 上，
                //   第二次挂就抛 "The node already has a parent."，被外层 catch 吞成 {"ok":false}，
                //   于是「迁移其实已经写进文件了，但页面报红字」，玩家以为没迁成功。必须克隆。
                ["failed"] = System.Text.Json.Nodes.JsonNode.Parse(allFailed.ToJsonString()),
            }.ToJsonString());
        }
        catch (Exception e)
        {
            BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                .LogWarning($"[迁移] 整批失败：{e.GetType().Name} {e.Message}\n{e.StackTrace}");
            TryWrite(ctx, 200, "application/json", "{\"ok\":false,\"msg\":\"" + (e.Message ?? "失败").Replace("\"", "") + "\"}");
        }
    }

    /// <summary>迁移单个 1.1.2 角色卡 → V1.2 卡（字段映射 + 合并补充 + 备份）</summary>
    private static bool MigrateOneCard(string dir, string file, string id, string name, System.Text.Json.Nodes.JsonObject extra)
    {
        var cardDir = System.IO.Path.Combine(dir, "角色卡");
        var srcPath = System.IO.Path.Combine(cardDir, file);
        if (!System.IO.File.Exists(srcPath)) throw new Exception("找不到原卡文件");

        var src = System.Text.Json.Nodes.JsonNode.Parse(
            System.IO.File.ReadAllText(srcPath, Encoding.UTF8)) as System.Text.Json.Nodes.JsonObject;
        if (src == null) throw new Exception("原卡解析失败");

        var dst = MapCard112to12(src, extra);
        // 前端没填姓名时，用扫描出来的真名兜底写进 _显示名
        try
        {
            if (dst["_显示名"] == null && !string.IsNullOrWhiteSpace(name)) dst["_显示名"] = name;
        }
        catch { }

        // 文件名尽量带真名：{真名}-{ID}.json；真名认不出来才退回 {ID}.json
        var dstPath = BuWenFanChen.Card.CardStore.PathOf(id, name);
        // 备份
        if (System.IO.File.Exists(dstPath))
        {
            try
            {
                System.IO.File.Copy(dstPath, dstPath + ".迁移前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true);
            }
            catch { }
        }
        System.IO.File.WriteAllText(dstPath, dst.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));

        // ★ 校验：迁出来是不是空卡（源卡结构不认识时会静默生成空卡）
        var hasContent = false;
        try
        {
            hasContent = !IsNodeEmpty(dst["口吻"])
                      || !IsNodeEmptyExcept(dst["人设"], "姓名", "身份", "善恶立场")
                      || !IsNodeEmpty(dst["自定义设定"])
                      || !IsNodeEmpty(dst["NSFW"]);
            if (!hasContent)
                BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                    .LogWarning($"[迁移] 角色卡 {name}（{id}）迁出来是空卡 —— 源卡结构可能不认识（顶层应有人设/口吻）");
        }
        catch { }

        BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
            .LogInfo($"[迁移] 角色卡 {name}（{id}）已迁入");
        return hasContent;
    }

    /// <summary>迁移单个 1.1.2 记忆（单文件 json）→ V1.2 记忆（meta + chunks.jsonl + facts）</summary>
    private static void MigrateOneMemory(string dir, string file, string id, string name)
    {
        var memDir = System.IO.Path.Combine(dir, "memory");
        var srcPath = System.IO.Path.Combine(memDir, file);
        if (!System.IO.File.Exists(srcPath)) return;   // 该角色没记忆，跳过

        var src = System.Text.Json.Nodes.JsonNode.Parse(
            System.IO.File.ReadAllText(srcPath, Encoding.UTF8)) as System.Text.Json.Nodes.JsonObject;
        if (src == null) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chunks = new List<BuWenFanChen.Memory.Chunk>();
        var chunkId = 1;

        // ① long_summary → summary chunk
        var longSummary = src["long_summary"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrWhiteSpace(longSummary))
            chunks.Add(new BuWenFanChen.Memory.Chunk { Id = chunkId++, Kind = "summary", Npc = name, Text = longSummary.Trim(), CreatedAt = now, LastSeen = now });

        // ② early_gist → summary chunk
        var earlyGist = src["early_gist"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrWhiteSpace(earlyGist))
            chunks.Add(new BuWenFanChen.Memory.Chunk { Id = chunkId++, Kind = "summary", Npc = name, Text = earlyGist.Trim(), CreatedAt = now, LastSeen = now });

        // ③ recent_log → dialog chunks（每对 user+assistant 合成一个）
        if (src["recent_log"] is System.Text.Json.Nodes.JsonArray log)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var node in log)
            {
                if (node is not System.Text.Json.Nodes.JsonObject o) continue;
                var role = o["role"]?.GetValue<string>() ?? "";
                var content = o["content"]?.GetValue<string>() ?? "";
                if (role == "user")
                {
                    if (sb.Length > 0)
                    {
                        chunks.Add(new BuWenFanChen.Memory.Chunk { Id = chunkId++, Kind = "dialog", Npc = name, Text = sb.ToString().Trim(), CreatedAt = now, LastSeen = now });
                        sb.Clear();
                    }
                    sb.Append("玩家：").Append(content.Trim());
                }
                else if (role == "assistant")
                {
                    sb.Append("\n角色：").Append(content.Trim());
                    chunks.Add(new BuWenFanChen.Memory.Chunk { Id = chunkId++, Kind = "dialog", Npc = name, Text = sb.ToString().Trim(), CreatedAt = now, LastSeen = now });
                    sb.Clear();
                }
            }
            if (sb.Length > 0)
                chunks.Add(new BuWenFanChen.Memory.Chunk { Id = chunkId++, Kind = "dialog", Npc = name, Text = sb.ToString().Trim(), CreatedAt = now, LastSeen = now });
        }
        else if (src["recent_log"] is System.Text.Json.Nodes.JsonValue logVal)
        {
            // 旧格式：recent_log 是一整段文本，不是数组 → 整体存一块，别丢
            try
            {
                var logStr = logVal.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(logStr))
                    chunks.Add(new BuWenFanChen.Memory.Chunk { Id = chunkId++, Kind = "dialog", Npc = name, Text = logStr.Trim(), CreatedAt = now, LastSeen = now });
            }
            catch { }
        }

        // ④ facts → List<Fact>（4 类映射）
        var facts = new List<BuWenFanChen.Memory.Fact>();
        var catMap = new Dictionary<string, BuWenFanChen.Memory.FactCategory>
        {
            ["赠物"] = BuWenFanChen.Memory.FactCategory.Gift,
            ["承诺"] = BuWenFanChen.Memory.FactCategory.Promise,
            ["关系里程碑"] = BuWenFanChen.Memory.FactCategory.RelationMilestone,
            ["重要事件"] = BuWenFanChen.Memory.FactCategory.ImportantEvent,
        };
        var unknownCatCount = 0;
        if (src["facts"] is System.Text.Json.Nodes.JsonObject factObj)
        {
            foreach (var kv in factObj)
            {
                // 4 类之外（玩家自定义类别 / 旧格式）不丢，统一归到「重要事件」
                var cat = catMap.TryGetValue(kv.Key, out var c0)
                    ? c0
                    : BuWenFanChen.Memory.FactCategory.ImportantEvent;
                if (!catMap.ContainsKey(kv.Key)) unknownCatCount++;
                if (kv.Value is not System.Text.Json.Nodes.JsonArray arr) continue;
                foreach (var node in arr)
                {
                    if (node is not System.Text.Json.Nodes.JsonObject fo) continue;
                    var text = fo["text"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var imp = 1;
                    try { imp = fo["importance"]?.GetValue<int>() ?? 1; } catch { }
                    var age = 0;
                    try { age = fo["age"]?.GetValue<int>() ?? 0; } catch { }
                    facts.Add(new BuWenFanChen.Memory.Fact
                    {
                        Category = cat,
                        Text = text,
                        Importance = imp,
                        Age = age,
                        Npc = name,
                        Source = BuWenFanChen.Memory.FactSource.Api,
                    });
                }
            }
        }

        // ⑤ 写文件（time_location → 骨架「处境」，别丢）
        var timeLoc = src["time_location"]?.GetValue<string>() ?? "";
        var mem = new BuWenFanChen.Memory.NpcMemory
        {
            Meta = new BuWenFanChen.Memory.MemoryMeta { NpcCfgId = id, NpcName = name },
            Facts = facts,
            Chunks = chunks,
            NextChunkId = chunkId,
            Skeleton = new BuWenFanChen.Memory.Skeleton
            {
                Relations = new List<string>(),
                Situation = timeLoc.Trim(),
            },
        };
        // ★ 迁移前先备份目标端已有记忆（防止覆盖玩家已聊出来的数据）
        try
        {
            var destDir0 = BuWenFanChen.Memory.MemoryPaths.MemDir(id);
            if (System.IO.Directory.Exists(destDir0))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                foreach (var f0 in new[] { "meta.json", "chunks.jsonl", "facts.json", "skeleton.txt" })
                {
                    var fp = System.IO.Path.Combine(destDir0, f0);
                    if (System.IO.File.Exists(fp))
                    {
                        try { System.IO.File.Copy(fp, fp + ".迁移前备份_" + stamp, true); } catch { }
                    }
                }
            }
        }
        catch { }

        try { BuWenFanChen.Memory.MemoryStore.Save(mem); } catch { }

        // 手动写 chunks.jsonl（不经过 AppendChunk，避免迁移时算向量）
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var destDir = BuWenFanChen.Memory.MemoryPaths.MemDir(id);
        System.IO.Directory.CreateDirectory(destDir);
        var csb = new System.Text.StringBuilder();
        foreach (var c in chunks)
            csb.AppendLine(System.Text.Json.JsonSerializer.Serialize(c, opts));
        System.IO.File.WriteAllText(System.IO.Path.Combine(destDir, "chunks.jsonl"), csb.ToString(), new UTF8Encoding(false));

        BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
            .LogInfo($"[迁移] 记忆 {name}（{id}）已迁入：{chunks.Count} chunks / {facts.Count} facts"
                + (unknownCatCount > 0 ? $"（{unknownCatCount} 类自定义事实已归入「重要事件」）" : ""));
    }

    /// <summary>1.1.2 卡 → V1.2 卡（字段映射）</summary>
    private static System.Text.Json.Nodes.JsonObject MapCard112to12(
        System.Text.Json.Nodes.JsonObject src, System.Text.Json.Nodes.JsonObject extra)
    {
        var dst = new System.Text.Json.Nodes.JsonObject();

        // ===== 人设区 =====
        var ren = new System.Text.Json.Nodes.JsonObject();
        // 1.1.2「人设原稿」→ V1.2「AI生卡」
        ren["AI生卡"] = CloneNode(src["人设"]?["人设原稿"]) ?? System.Text.Json.Nodes.JsonValue.Create("");
        // 底线禁忌直接迁
        ren["底线禁忌"] = CloneNode(src["人设"]?["底线禁忌"])
            ?? CloneNode(src["NSFW"]?["底线禁忌"])
            ?? new System.Text.Json.Nodes.JsonArray();

        // ===== 口吻区：遍历，特殊处理 3 个字段 =====
        var kw = new System.Text.Json.Nodes.JsonObject();
        var kwSrc = src["口吻"] as System.Text.Json.Nodes.JsonObject;
        string sense = "", emotion = "", behavior = "";
        if (kwSrc != null)
        {
            foreach (var kv in kwSrc)
            {
                if (kv.Key == "感官标识") { sense = Flat112(kv.Value); continue; }
                if (kv.Key == "情绪外露度") { emotion = Flat112(kv.Value); continue; }
                if (kv.Key == "行为逻辑设定") { behavior = Flat112(kv.Value); continue; }
                kw[kv.Key] = CloneNode(kv.Value);
            }
        }

        // 感官标识 → 外貌穿着
        ren["外貌穿着"] = sense;
        // 情绪外露度 → 性格（行为逻辑是小动作，单独放口吻区，不合并）
        ren["性格"] = emotion;
        // 行为逻辑设定 → 口吻区「行为逻辑」
        if (!string.IsNullOrWhiteSpace(behavior)) kw["行为逻辑"] = behavior;
        // 🎭 三项不强制填；姓名默认带自动识别出的真名
        ren["姓名"] = extra?["姓名"]?.GetValue<string>() ?? "";
        ren["身份"] = extra?["身份"]?.GetValue<string>() ?? "";
        ren["善恶立场"] = extra?["善恶立场"]?.GetValue<string>() ?? "";

        dst["人设"] = ren;
        dst["口吻"] = kw;

        // ===== 自定义设定 + NSFW 直接迁 =====
        dst["自定义设定"] = CloneNode(src["自定义设定"]) ?? new System.Text.Json.Nodes.JsonObject();
        dst["NSFW"] = CloneNode(src["NSFW"]) ?? new System.Text.Json.Nodes.JsonObject();

        // ===== 顶层元数据：启用 / 来源底稿 / 版本（不迁会导致卡不生效）=====
        var enabled = true;
        try { if (src["启用"] != null) enabled = src["启用"]!.GetValue<bool>(); } catch { }
        dst["启用"] = enabled;
        if (src["_来源"] is System.Text.Json.Nodes.JsonObject srcMeta)
            dst["_来源"] = CloneNode(srcMeta);
        dst["_版本"] = 3;

        // ★ 2026-10-29：把真名写进 _显示名 —— 控制台列表优先用它，
        //   这样即使配置表读不到（缓存是空的），卡名也不会变成一串数字
        try
        {
            var showName = extra?["姓名"]?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(showName)) dst["_显示名"] = showName;
        }
        catch { }

        return dst;
    }

    /// <summary>JSON 节点深拷贝：JsonNode 不能同时挂两个父节点，跨对象赋值必须先拷贝。</summary>
    private static System.Text.Json.Nodes.JsonNode CloneNode(System.Text.Json.Nodes.JsonNode v)
        => v == null ? null : System.Text.Json.Nodes.JsonNode.Parse(v.ToJsonString());

    /// <summary>
    /// JSON 节点是否「空」：null / 空白串 / 空数组 / 空对象。
    /// ★ 不能用 ToString() 判断——空数组的 ToString() 是 "[]"，会把空卡误判成有内容。
    /// </summary>
    private static bool IsNodeEmpty(System.Text.Json.Nodes.JsonNode v)
        => IsNodeEmptyExcept(v, null);

    private static bool IsNodeEmptyExcept(System.Text.Json.Nodes.JsonNode v, params string[] skipKeys)
    {
        if (v == null) return true;
        if (v is System.Text.Json.Nodes.JsonArray arr)
        {
            foreach (var x in arr) if (!IsNodeEmpty(x)) return false;
            return true;
        }
        if (v is System.Text.Json.Nodes.JsonObject o)
        {
            foreach (var kv in o)
            {
                if (skipKeys != null && Array.IndexOf(skipKeys, kv.Key) >= 0) continue;
                if (!IsNodeEmpty(kv.Value)) return false;
            }
            return true;
        }
        try { return string.IsNullOrWhiteSpace(v.GetValue<string>()); }
        catch { return string.IsNullOrWhiteSpace(v.ToString()); }
    }

    private static string Flat112(System.Text.Json.Nodes.JsonNode v)
    {
        if (v == null) return "";
        if (v is System.Text.Json.Nodes.JsonArray arr)
            return string.Join("／", arr.Select(x => Flat112(x)));
        try { return v.GetValue<string>() ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// 迁移扫描：扫 1.1.2 目录（角色卡/ + memory/），匹配「皮肤名 → 角色 ID」。
    /// </summary>
    /// <summary>手动改卡名：改文件名前缀 + 卡内 _显示名（不影响 ID 匹配）。</summary>
    private static void HandleCardRename(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        try
        {
            string body;
            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();
            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            var npc = node?["npc"]?.GetValue<string>() ?? "";
            var name = node?["name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(npc))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少角色代码\"}");
                return;
            }
            var r = BuWenFanChen.Card.CardStore.RenameCardFiles(npc, name);
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":" + (r.Ok ? "true" : "false") + ",\"msg\":\"" + JsonEscape(r.Msg) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("改卡名失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>
    /// 清理 card 目录里的「旧名残留」（2026-10-29）。
    /// 背景：换化身/皮肤时旧版本把文件名写成了「奈宝·芊儿4K2-193401.lorebook.json」，
    /// 改真名后变成「芊儿-193401.lorebook.json」，但老文件还在 → 同一角色两份。
    /// 规则：同一 id 同一后缀有多份、且「真名那份」存在时，把其余的挪到 card\_旧名残留_{时间}\。
    /// 不删数据（挪走，随时能拿回来）。
    /// </summary>
    private static void HandleCardCleanOldNames(HttpListenerContext ctx)
    {
        try
        {
            var cardDir = System.IO.Path.Combine(BuWenFanChen.Memory.MemoryPaths.Root, "card");
            var moved = new System.Text.Json.Nodes.JsonArray();
            var kept = 0;
            var backupDir = "";

            if (System.IO.Directory.Exists(cardDir))
            {
                // 按 (id, 后缀) 分组
                var groups = new Dictionary<string, List<string>>();
                foreach (var f in System.IO.Directory.GetFiles(cardDir))
                {
                    var fn = System.IO.Path.GetFileName(f);
                    if (fn.Contains(".bak") || fn.Contains("备份")) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(fn, @"^(.*?)-(\d+)(\.[^.]*.*)$");
                    if (!m.Success) continue;
                    var key = m.Groups[2].Value + "|" + m.Groups[3].Value;
                    if (!groups.TryGetValue(key, out var list)) { list = new List<string>(); groups[key] = list; }
                    list.Add(fn);
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                backupDir = System.IO.Path.Combine(cardDir, "_旧名残留_" + stamp);

                foreach (var kv in groups)
                {
                    var sep = kv.Key.IndexOf('|');
                    var id = kv.Key.Substring(0, sep);
                    var suffix = kv.Key.Substring(sep + 1);
                    var real = BuWenFanChen.Memory.ConfigTable.GetBaseName(id);
                    if (string.IsNullOrWhiteSpace(real)) { kept += kv.Value.Count; continue; }

                    var want = BuWenFanChen.Card.CardStore.SanitizeFileName(real) + "-" + id + suffix;
                    // 没有「真名那份」就不动（避免把唯一的文件搬走）
                    if (!kv.Value.Contains(want)) { kept += kv.Value.Count; continue; }

                    foreach (var fn in kv.Value)
                    {
                        if (fn == want) continue;
                        System.IO.Directory.CreateDirectory(backupDir);
                        var src = System.IO.Path.Combine(cardDir, fn);
                        var dst = System.IO.Path.Combine(backupDir, fn);
                        try { if (System.IO.File.Exists(dst)) System.IO.File.Delete(dst); } catch { }
                        System.IO.File.Move(src, dst);
                        moved.Add(fn);
                    }
                }
            }

            var result = new System.Text.Json.Nodes.JsonObject
            {
                ["ok"] = true,
                ["moved"] = moved.Count,
                ["list"] = moved,
                ["kept"] = kept,
                ["dir"] = backupDir,
                ["msg"] = moved.Count > 0
                    ? $"已挪走 {moved.Count} 个旧名残留 → card\\{System.IO.Path.GetFileName(backupDir)}\\（想拿回来直接拷回去）"
                    : (kept > 0
                        ? $"没有可清理的（{kept} 个文件的名字本来就是对的，或缺少『真名那份』）"
                        : "没有发现旧名残留"),
            };
            TryWrite(ctx, 200, "application/json", result.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("清理失败：" + e.Message) + "\"}");
        }
    }

    private static void HandleMigrateScan(HttpListenerContext ctx)    {
        try
        {
            var body = "";
            try
            {
                using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();
            }
            catch { }
            var dir = "";
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(body);
                dir = node?["dir"]?.GetValue<string>() ?? "";
            }
            catch { }

            var cards = new System.Text.Json.Nodes.JsonArray();
            var memories = new System.Text.Json.Nodes.JsonArray();

            if (!string.IsNullOrWhiteSpace(dir))
            {
                // 角色卡目录
                var cardDir = System.IO.Path.Combine(dir, "角色卡");
                if (System.IO.Directory.Exists(cardDir))
                {
                    foreach (var f in System.IO.Directory.GetFiles(cardDir, "*.json"))
                    {
                        if (System.IO.Path.GetFileName(f).Contains("备份") ||
                            System.IO.Path.GetFileName(f).Contains("优化中")) continue;
                        cards.Add(ScanOne(cardDir, System.IO.Path.GetFileName(f)));
                    }
                }
                // 记忆目录
                var memDir = System.IO.Path.Combine(dir, "memory");
                if (System.IO.Directory.Exists(memDir))
                {
                    foreach (var f in System.IO.Directory.GetFiles(memDir, "*.json"))
                    {
                        if (System.IO.Path.GetFileName(f).Contains("备份")) continue;
                        memories.Add(ScanOne(null, System.IO.Path.GetFileName(f)));
                    }
                }
            }

            var result = new System.Text.Json.Nodes.JsonObject
            {
                ["ok"] = true,
                ["dir"] = dir,
                ["cards"] = cards,
                ["memories"] = memories,
                // ★ 2026-10-29：把「皮肤名 → ID」缓存条数带给前端。
                //   0 = 一次都没抓到 → 迁移必然全部匹配失败，先在 UI 上把原因说清楚。
                ["skins"] = BuWenFanChen.Memory.ConfigTable.GetAllSkins().Count,
            };
            TryWrite(ctx, 200, "application/json", result.ToJsonString());
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json", "{\"ok\":false,\"msg\":\"" + (e.Message ?? "失败").Replace("\"", "") + "\"}");
        }
    }

    /// <summary>单个 1.1.2 文件名 → 匹配皮肤名/ID</summary>
    private static System.Text.Json.Nodes.JsonObject ScanOne(string cardDir, string fileName)
    {
        var base0 = fileName;
        base0 = System.Text.RegularExpressions.Regex.Replace(base0, @"\.json.*$", "");
        base0 = base0.TrimEnd('_').Trim();

        var skin = "";
        var id = "";

        // 候选 1：全 _ → ·
        var cand1 = base0.Replace("_", "·");
        id = BuWenFanChen.Memory.ConfigTable.GetIdBySkin(cand1);
        if (!string.IsNullOrEmpty(id)) { skin = cand1; }
        else
        {
            // 候选 2：去最后一段（称号）
            var parts = base0.Split('_');
            if (parts.Length >= 2)
            {
                var cand2 = string.Join("·", parts.Take(parts.Length - 1));
                var id2 = BuWenFanChen.Memory.ConfigTable.GetIdBySkin(cand2);
                if (!string.IsNullOrEmpty(id2)) { skin = cand2; id = id2; }
            }
        }

        // 候选 3：遍历所有皮肤名，找包含关系
        if (string.IsNullOrEmpty(id))
        {
            foreach (var (sk, sid) in BuWenFanChen.Memory.ConfigTable.GetAllSkins())
            {
                if (cand1.Contains(sk) || sk.Contains(cand1))
                {
                    skin = sk; id = sid; break;
                }
            }
        }

        var realName = "";
        if (!string.IsNullOrEmpty(id))
            realName = BuWenFanChen.Memory.ConfigTable.GetBaseName(id);

        // 读 1.1.2 卡，把「情绪外露度 + 行为逻辑设定」合并成性格，给玩家看/改
        var merged = "";
        if (!string.IsNullOrWhiteSpace(cardDir))
        {
            try
            {
                var path = System.IO.Path.Combine(cardDir, fileName);
                if (System.IO.File.Exists(path))
                {
                    var src = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path, Encoding.UTF8))
                        as System.Text.Json.Nodes.JsonObject;
                    if (src != null)
                    {
                        var kw = src["口吻"] as System.Text.Json.Nodes.JsonObject;
                        merged = MergePersonality(Flat112(kw?["情绪外露度"]), Flat112(kw?["行为逻辑设定"]));
                    }
                }
            }
            catch { }
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["file"] = fileName,
            ["skin"] = skin,
            ["id"] = id,
            ["name"] = realName,
            ["merged"] = merged,
            ["matched"] = !string.IsNullOrEmpty(id),
        };
    }

    /// <summary>情绪外露度 + 行为逻辑设定 → 性格（合并规则，扫描与迁移共用）</summary>
    private static string MergePersonality(string emotion, string behavior)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(emotion)) sb.Append("情绪外露：").Append(emotion.Trim());
        if (!string.IsNullOrWhiteSpace(behavior))
        {
            if (sb.Length > 0) sb.Append("\n\n行为逻辑：\n");
            sb.Append(behavior.Trim());
        }
        return sb.ToString();
    }

    private static void HandleRename(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        try
        {
            var r = BuWenFanChen.Memory.RenameTool.RunAll();
            if (r.Error != null)
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8",
                    "{\"ok\":false,\"msg\":\"" + JsonEscape("改名失败：" + r.Error) + "\"}");
                return;
            }
            var detail = string.Join("；", r.Detail.Take(8));
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"msg\":\"" + JsonEscape(detail) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("改名异常：" + e.Message) + "\"}");
        }
    }

    /// <summary>保存玩家编辑后的事实列表（只改 facts.json，不动 chunks）</summary>
    private static void HandleSaveFacts(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        try
        {
            var npc = ctx.Request.QueryString["npc"] ?? "";
            if (string.IsNullOrWhiteSpace(npc))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少角色代码\"}");
                return;
            }
            string body;
            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();

            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            var arr = node?["facts"] as System.Text.Json.Nodes.JsonArray;

            var list = new List<BuWenFanChen.Memory.Fact>();
            if (arr != null)
            {
                foreach (var it in arr)
                {
                    if (it is not System.Text.Json.Nodes.JsonObject o) continue;
                    var text = (o["text"]?.GetValue<string>() ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var imp = 3;
                    try { imp = o["importance"]?.GetValue<int>() ?? 3; } catch { }
                    if (imp < 1) imp = 1;
                    if (imp > 5) imp = 5;

                    var catStr = o["category"]?.GetValue<string>() ?? "ImportantEvent";
                    var cat = Enum.TryParse<BuWenFanChen.Memory.FactCategory>(catStr, out var c)
                        ? c : BuWenFanChen.Memory.FactCategory.ImportantEvent;

                    var age = 0;
                    try { age = o["age"]?.GetValue<int>() ?? 0; } catch { }

                    list.Add(new BuWenFanChen.Memory.Fact
                    {
                        Text = text,
                        Importance = imp,
                        Category = cat,
                        Age = age,
                    });
                }
            }

            var mem = BuWenFanChen.Memory.MemoryStore.Load(npc);
            mem.Facts = list;
            BuWenFanChen.Memory.MemoryStore.Save(mem);

            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"msg\":\"已保存 " + list.Count + " 条事实\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("保存失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>保存骨架（关系条目 + 处境）；只写 skeleton.txt</summary>
    private static void HandleSaveSkeleton(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        try
        {
            var npc = ctx.Request.QueryString["npc"] ?? "";
            if (string.IsNullOrWhiteSpace(npc))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少角色代码\"}");
                return;
            }
            string body;
            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();

            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            var rels = new List<string>();
            if (node?["relations"] is System.Text.Json.Nodes.JsonArray arr)
            {
                foreach (var it in arr)
                {
                    var s = (it?.GetValue<string>() ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(s)) rels.Add(s);
                }
            }
            var situation = (node?["situation"]?.GetValue<string>() ?? "").Trim();

            var mem = BuWenFanChen.Memory.MemoryStore.Load(npc);
            mem.Skeleton.Relations = rels;
            mem.Skeleton.Situation = situation;
            BuWenFanChen.Memory.MemoryStore.Save(mem);

            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":true,\"msg\":\"骨架已保存（关系 " + rels.Count + " 条）\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("保存失败：" + e.Message) + "\"}");
        }
    }

    /// <summary>摘要编辑：改文字（重算向量）或删除（记遗忘日志）。</summary>
    private static void HandleSummary(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            TryWrite(ctx, 405, "application/json", "{\"ok\":false,\"msg\":\"只接受 POST\"}");
            return;
        }
        try
        {
            var npc = ctx.Request.QueryString["npc"] ?? "";
            if (string.IsNullOrWhiteSpace(npc))
            {
                TryWrite(ctx, 200, "application/json; charset=utf-8", "{\"ok\":false,\"msg\":\"缺少角色代码\"}");
                return;
            }
            string body;
            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();

            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            var id = 0;
            try { id = node?["id"]?.GetValue<int>() ?? 0; } catch { }
            var action = node?["action"]?.GetValue<string>() ?? "save";
            var text = node?["text"]?.GetValue<string>() ?? "";

            var mem = BuWenFanChen.Memory.MemoryStore.Load(npc);
            bool ok;
            string msg;
            if (action == "delete")
            {
                ok = BuWenFanChen.Memory.MemoryStore.RemoveChunk(mem, id, "手动删除摘要");
                msg = ok ? "已删除" : "没找到这条摘要";
            }
            else
            {
                ok = BuWenFanChen.Memory.MemoryStore.UpdateChunkText(mem, id, text);
                msg = ok ? "已保存（向量已重算）" : "没找到这条摘要";
            }

            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":" + (ok ? "true" : "false") + ",\"msg\":\"" + JsonEscape(msg) + "\"}");
        }
        catch (Exception e)
        {
            TryWrite(ctx, 200, "application/json; charset=utf-8",
                "{\"ok\":false,\"msg\":\"" + JsonEscape("操作失败：" + e.Message) + "\"}");
        }
    }

    private static string MemoryJson(string npc)
    {
        try
        {
            var mem = BuWenFanChen.Memory.MemoryStore.Load(npc);
            var sb = new StringBuilder();
            sb.Append("{\"npc\":\"").Append(JsonEscape(npc)).Append("\",");
            sb.Append("\"npcName\":\"").Append(JsonEscape(BuWenFanChen.Memory.MemoryEngine.ResolveName(npc))).Append("\",");

            // ★ 2026-10-26：NPC 状态（只读缓存）——好感度
            try
            {
                var st = NpcStats.Get(npc);
                if (st != null)
                {
                    sb.Append("\"intimacy\":").Append(st.Intimacy).Append(',');
                    sb.Append("\"maxIntimacy\":").Append(st.MaxIntimacy).Append(',');
                    sb.Append("\"intimacyName\":\"").Append(JsonEscape(st.Name ?? "")).Append("\",");
                }
            }
            catch { }

            // 骨架：关系 + 处境
            sb.Append("\"relations\":[");
            sb.Append(string.Join(",", mem.Skeleton.Relations.Select(r => "\"" + JsonEscape(r) + "\"")));
            sb.Append("],");
            sb.Append("\"situation\":\"").Append(JsonEscape(mem.Skeleton.Situation ?? "")).Append("\",");

            // facts
            sb.Append("\"facts\":[");
            sb.Append(string.Join(",", mem.Facts.OrderByDescending(f => f.Importance)
                .Select(f => "{\"text\":\"" + JsonEscape(f.Text) + "\",\"importance\":" + f.Importance
                    + ",\"category\":\"" + f.Category + "\",\"age\":" + f.Age + "}")));
            sb.Append("],");

            // chunks：摘要和对话原文【分开取】。
            //   原来混在一起 OrderByDescending(Turn).Take(30)：一次抽取会生成几十条摘要句，
            //   它们的 Turn 都比旧对话大 → 摘要把 30 条名额占满，对话原文一条都显示不出来。
            //   玩家反馈「摘要一直在更新、对话原文不更新」就是这个原因（数据其实在写，只是没显示）。
            var sumPicked = mem.Chunks.Where(c => c.Kind == BuWenFanChen.Memory.ChunkKind.Summary)
                .OrderByDescending(c => c.Turn).ThenByDescending(c => c.Id).Take(60).ToList();
            var dlgPicked = mem.Chunks.Where(c => c.Kind != BuWenFanChen.Memory.ChunkKind.Summary)
                .OrderByDescending(c => c.Turn).ThenByDescending(c => c.Id).Take(30).ToList();
            sb.Append("\"chunks\":[");
            sb.Append(string.Join(",", sumPicked.Concat(dlgPicked)
                .Select(c => "{\"kind\":\"" + c.Kind + "\",\"id\":" + c.Id + ",\"turn\":" + c.Turn + ",\"text\":\"" + JsonEscape(c.Text) + "\"}")));
            sb.Append("]");
            sb.Append('}');
            return sb.ToString();
        }
        catch (Exception e)
        {
            return "{\"error\":\"" + JsonEscape(e.Message) + "\"}";
        }
    }

    private static string StatusJson()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"port\":").Append(Port).Append(',');
            sb.Append("\"running\":").Append(_running ? "true" : "false").Append(',');
            sb.Append("\"describe\":\"").Append(JsonEscape(Describe())).Append('"');

            // 接口状态：没配 / 还指着旧本地代理 / 正常
            try
            {
                var llm = BuWenFanChen.Memory.MemoryEngine.GetLlmConfig(null);
                var apiUrl = llm?.Url ?? "";
                // ★ 2026-10-26：只把 1.1.2 的代理端口（8788/8790）当「旧本地代理」。
                //   127.0.0.1 的其他端口是本地模型（Ollama 11434 / LM Studio 1234 等），
                //   不能报「旧代理」，否则本地模型玩家会被红色警告误导。
                var legacy = apiUrl.Contains(":8788") || apiUrl.Contains(":8790");
                var localModel = !legacy && (apiUrl.Contains("127.0.0.1") || apiUrl.Contains("localhost"));
                sb.Append(",\"api\":{");
                sb.Append("\"url\":\"").Append(JsonEscape(apiUrl)).Append("\",");
                sb.Append("\"ok\":").Append(llm != null && llm.IsValid ? "true" : "false").Append(',');
                sb.Append("\"legacy\":").Append(legacy ? "true" : "false").Append(',');
                sb.Append("\"local\":").Append(localModel ? "true" : "false");
                sb.Append('}');
            }
            catch { }

            sb.Append('}');
            return sb.ToString();
        }
        catch (Exception e)
        {
            return "{\"port\":" + Port + ",\"describe\":\"状态读取失败：" + JsonEscape(e.Message) + "\"}";
        }
    }

    private static string Describe()
    {
        try
        {
            return "运行中 · 127.0.0.1:" + Port;
        }
        catch { return "运行中"; }
    }

    // ==================== HTML ====================

                                                                                                                    private static string IndexHtml()
    {
        return @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>不问凡尘 V1.2.2 · 控制台</title>
<style>
  :root{--bg:#0b1218;--panel:rgba(16,19,25,.72);--panel2:rgba(24,28,37,.78);--line:rgba(88,96,112,.55);
        --txt:#e8eaf0;--dim:#9aa2b1;--jade:#4fc3a1;--gold:#d9a441;--red:#e06c6c}
  *{box-sizing:border-box}
  body{margin:0;color:var(--txt);font-size:14px;line-height:1.7;
       font-family:""Microsoft YaHei"",""PingFang SC"",system-ui,sans-serif;
       background:#0b1218 url(""/console-bg.png"") center center / cover no-repeat fixed}
  body.nobg{background:var(--bg)}
  header,nav,main{backdrop-filter:blur(10px) saturate(115%);-webkit-backdrop-filter:blur(10px) saturate(115%)}
  .wrap{max-width:1180px;margin:0 auto;padding:18px}
  header{display:flex;align-items:center;gap:12px;background:var(--panel);
         border:1px solid var(--line);border-radius:12px 12px 0 0;padding:14px 18px}
  header h1{margin:0;font-size:16px;font-weight:600}
  header{position:relative}
  .hinfo{margin-left:auto;text-align:right;font-size:12px;color:var(--dim);line-height:1.65}
  .hinfo i{display:inline-block;width:7px;height:7px;border-radius:50%;background:currentColor;margin-right:3px;vertical-align:middle}
  .stat b{color:var(--gold)}
  .warn{display:inline-block;padding:1px 7px;border-radius:4px;font-size:11.5px;
        background:rgba(217,164,65,.14);border:1px solid rgba(217,164,65,.45);
        color:var(--gold);margin-left:6px;vertical-align:middle}
  .ok{display:inline-block;padding:1px 7px;border-radius:4px;font-size:11.5px;
      background:rgba(79,195,161,.14);border:1px solid rgba(79,195,161,.45);
      color:var(--jade);margin-left:6px;vertical-align:middle}
  .dot{width:9px;height:9px;border-radius:50%;display:inline-block;margin-right:6px;background:var(--red)}
  .dot.on{background:var(--jade);box-shadow:0 0 8px var(--jade)}
  .badge{display:inline-flex;align-items:center;background:var(--panel2);border:1px solid var(--line);
         border-radius:20px;padding:4px 14px 4px 12px;font-size:13px;color:var(--dim)}
  .hint{color:var(--dim);font-size:12.5px;line-height:1.75;margin:0 0 16px}
  nav{display:flex;flex-wrap:wrap;background:var(--panel);border:1px solid var(--line);
      border-top:none;padding:0 10px}
  nav button{background:none;border:none;color:var(--dim);padding:12px 14px;cursor:pointer;
             font-family:inherit;font-size:14px;border-bottom:2px solid transparent}
  nav button::before{content:"""";width:7px;height:7px;border-radius:50%;display:inline-block;
                     margin-right:7px;vertical-align:middle;opacity:.6;transition:opacity .15s}
  nav button:hover::before{opacity:1}
  nav button[data-g=run]::before{background:#4fc3a1}
  nav button[data-g=help]::before{background:#5aa9e6}
  nav button[data-g=cfg]::before{background:#d9a441}
  nav button[data-g=tool]::before{background:#a78bfa}
  nav button.on{color:var(--jade);border-bottom-color:var(--jade)}
  nav button.on[data-g=run]{color:#4fc3a1;border-bottom-color:#4fc3a1}
  nav button.on[data-g=help]{color:#5aa9e6;border-bottom-color:#5aa9e6}
  nav button.on[data-g=cfg]{color:#d9a441;border-bottom-color:#d9a441}
  nav button.on[data-g=tool]{color:#a78bfa;border-bottom-color:#a78bfa}
  main{background:var(--panel);border:1px solid var(--line);border-top:none;
       backdrop-filter:blur(14px) saturate(120%);-webkit-backdrop-filter:blur(14px) saturate(120%);
       border-radius:0 0 12px 12px;padding:20px;min-height:60vh}
  .page{display:none}.page.on{display:block}
  h2{font-size:16px;color:var(--jade);margin:0 0 10px;padding-bottom:9px;text-shadow:0 1px 3px rgba(0,0,0,.55);
     border-bottom:1px solid var(--line)}
  #p11 h2,#p13 h2,#p9 h2,#p2 h2{color:#5aa9e6;border-bottom-color:#3a6c93}
  #p3 h2,#p4 h2,#p5 h2,#p12 h2,#p10 h2{color:#d9a441;border-bottom-color:#7a5f27}
  #p6 h2,#p7 h2,#p8 h2{color:#a78bfa;border-bottom-color:#5d4a8a}
  h3{font-size:13px;color:var(--gold);margin:20px 0 10px;padding-bottom:6px;
     border-bottom:1px solid var(--line);font-weight:600}
  .row{display:flex;gap:12px;align-items:flex-start;margin-bottom:12px}
  .row label{width:210px;flex:none;padding-top:7px;color:var(--dim);font-size:13px;white-space:nowrap}
  input[type=text],input[type=password],input[type=number],textarea,select{
    background:var(--bg);border:1px solid var(--line);color:var(--txt);border-radius:7px;
    padding:8px 10px;font-family:inherit;font-size:13px;width:100%}
  textarea{resize:vertical;min-height:56px;line-height:1.6}
  input:focus,textarea:focus,select:focus{outline:none;border-color:var(--jade)}
  .grid2{display:grid;grid-template-columns:1fr 1fr;gap:12px}
  button{cursor:pointer;font-family:inherit;border-radius:8px;font-size:13px}
  .save{background:var(--jade);color:#0d1a16;border:none;padding:9px 20px;font-weight:600;font-size:14px}
  .ghost{background:none;border:1px solid var(--line);color:var(--txt);padding:8px 14px}
  .ghost:hover{border-color:var(--jade);color:var(--jade)}
  .danger{border:1px solid #5a2c2c;color:var(--red);background:none;padding:8px 14px}
  .tagbox{border:1px solid var(--line);border-radius:8px;padding:8px;background:var(--bg);flex:1}
  .tag{display:flex;gap:8px;margin-bottom:6px}
  .tag input{flex:1}
  .x{background:none;border:1px solid var(--line);color:var(--dim);width:30px;height:30px;
     border-radius:6px;flex:none;font-size:15px}
  .x:hover{border-color:var(--red);color:var(--red)}
  .add{background:none;border:1px dashed var(--line);color:var(--dim);padding:6px 12px;font-size:12px}
  .split{display:grid;grid-template-columns:210px 1fr;gap:18px}
  .list{border-right:1px solid var(--line);padding-right:14px}
  .item{padding:7px 10px;border-radius:7px;cursor:pointer;font-size:13px;color:var(--dim)}
  .item:hover{background:var(--panel2);color:var(--txt)}
  .item.on{background:#1b2f29;color:var(--jade)}
  .sechead{margin:20px 0 10px;padding-bottom:6px;border-bottom:1px solid var(--line);
           color:var(--gold);font-size:13px;font-weight:600}
  pre{background:#0f1115;border:1px solid var(--line);border-radius:8px;padding:14px;
      white-space:pre-wrap;word-break:break-all;font-size:12.5px;line-height:1.7;
      max-height:420px;overflow:auto;margin:0}
  .stat{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin-bottom:16px}
  .stat div{background:var(--panel2);border:1px solid var(--line);border-radius:9px;padding:13px}
  .stat b{display:block;font-size:21px;color:var(--gold)}
  .stat span{font-size:12px;color:var(--dim)}
  table{width:100%;border-collapse:collapse;font-size:13px}
  th,td{text-align:left;padding:8px 10px;border-bottom:1px solid var(--line)}
  th{color:var(--dim);font-weight:500;font-size:12px}
  .ok{color:var(--jade)}.warn{color:var(--gold)}.bad{color:var(--red)}
  .chips{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:14px}
  .chip{border:1px solid var(--line);border-radius:20px;padding:5px 13px;font-size:12.5px;color:var(--dim)}
  .chip.on{background:#1b2f29;border-color:var(--jade);color:var(--jade)}
  .msg{margin-top:12px;font-size:13px;color:var(--gold)}
  .hint2{color:var(--dim);font-size:11.5px;margin-top:4px;line-height:1.6;white-space:pre-wrap}
  .entrybox{border:1px solid var(--line);border-radius:8px;padding:12px 14px;margin-bottom:12px;background:var(--panel2)}
  .entrybox .ehead{display:flex;align-items:center;gap:8px;margin-bottom:8px}
  .masterswitch{display:flex;align-items:center;gap:12px;background:var(--panel2);border:1px solid var(--line);border-radius:8px;padding:11px 14px;margin:0 0 14px}
  .masterswitch b{font-size:13.5px;color:var(--gold)}
  .masterswitch .dim{font-size:12px;color:var(--dim);margin-top:3px;line-height:1.6}
  .switch{position:relative;display:inline-block;width:46px;height:24px;flex:none}
  .switch input{opacity:0;width:0;height:0}
  .switch span{position:absolute;cursor:pointer;top:0;left:0;right:0;bottom:0;background:#333a47;border-radius:24px;transition:.2s}
  .switch span:before{content:"""";position:absolute;left:3px;bottom:3px;width:18px;height:18px;background:#fff;border-radius:50%;transition:.2s}
  .switch input:checked + span{background:var(--jade)}
  .switch input:checked + span:before{transform:translateX(22px)}
  .refresh-tip{background:rgba(217,164,65,.13);border:1px solid rgba(217,164,65,.5);
               border-radius:6px;padding:8px 12px;margin:0 0 14px;
               font-size:12.5px;color:#e8c46a;line-height:1.65}
  footer{text-align:center;color:#5d6472;font-size:12px;padding:16px 0}
  @media(max-width:820px){.split{grid-template-columns:1fr}.list{border:none}
    .grid2,.stat{grid-template-columns:1fr}.row{flex-direction:column;gap:5px}.row label{padding-top:0}}
</style>
</head>
<body>
<div class=""wrap"">
  <header>
    <span class=""dot on""></span>
    <h1>不问凡尘 V1.2.2 · 控制台</h1>
    <span class=""badge"" id=""statusBadge"">读取中…</span>
    <span class=""badge"" id=""apiBadge"" style=""display:none"">接口：—</span>
    <div class=""hinfo"">
      <div>只在本地可见</div>
      <div>关掉窗口不影响：地址栏输 <b>127.0.0.1:8848</b></div>
      <div>标签颜色：<i style=""color:#d9a441""></i>配置 <i style=""color:#a78bfa""></i>工具 <i style=""color:#5aa9e6""></i>帮助</div>
      <div>问题/反馈/交流可加QQ群：338115203</div>
    </div>
  </header>

  <nav id=""nav"">
    <button class=""on"" data-p=""p3"" data-g=""cfg"">全局设置</button>
    <button data-p=""p5"" data-g=""cfg"">长期记忆</button>
    <button data-p=""p10"" data-g=""cfg"">NSFW 模块</button>
    <button data-p=""p14"" data-g=""cfg"">世界书</button>
    <button data-p=""p4"" data-g=""cfg"">角色卡编辑</button>
    <button data-p=""p7"" data-g=""cfg"">角色卡优化</button>
    <button data-p=""p16"" data-g=""cfg"">自动好感度</button>
    <button data-p=""p8"" data-g=""tool"">Token 预览</button>
    <button data-p=""p15"" data-g=""tool"">迁移</button>
    <button data-p=""p2"" data-g=""help"">日志</button>
    <button data-p=""p11"" data-g=""help"">教程</button>
    <button data-p=""p13"" data-g=""help"">AI 答疑</button>
    <button data-p=""p9"" data-g=""help"">更新日志</button>
  </nav>

  <main>

    <!-- ========== p3 全局设置 ========== -->
    <section class=""page on"" id=""p3"">
      <h2>全局设置</h2>
      <div class=""stat"">
        <div><b id=""statTotal"">200</b><span>总上限（字）</span></div>
        <div><b id=""statNsfw"">auto</b><span>情事模式</span></div>
        <div><b id=""statFallback"">关</b><span>兜底补写</span></div>
        <div><b id=""statExtract"">30</b><span>抽取间隔（轮）</span></div>
      </div>

      <div class=""sechead"">AI 行为</div>
      <label style=""display:flex;align-items:center;gap:9px;font-size:13px;color:var(--txt);cursor:pointer;margin-bottom:4px"">
        <input type=""checkbox"" id=""allowNpcActing"" style=""width:16px;height:16px;margin:0;flex:none"">
        代演在场角色（允许 AI 用【名字】标签让其他在场角色说一两句）<span class=""ok"">存盘即生效</span>
      </label>
      <p class=""hint"" style=""margin-top:-2px"">
        开启后：AI 可以让「当前场景里在场的其他人」也说一句，写法是用【名字】开头，例如【某某】""这笔账已核过。""<br>
        默认关。关着时只演当前角色，别人的话只用旁白转述。<b>不依赖角色卡：勾上、重启游戏后就生效（没卡的角色也生效）。</b><br>
        每次最多让一个人说一句，主体仍是当前角色，不会变成大段群戏。
      </p>

      <p class=""hint"">
        所有设置保存后<b class=""ok"">立即生效</b>，不用重启游戏。<br>
        <b>情事用语</b>页的那几个文件例外——存盘即生效，不用重启。
      </p>
      <div class=""refresh-tip"">
        ⚠ 进游戏换过存档 / 聊过新角色之后，要按 <b>F5</b> 刷新本页，上面的「当前存档」才会更新。
      </div>

      <div class=""sechead"">存档</div>
      <p class=""hint"">
        记忆和角色卡会按存档隔离：换个存档玩，不会串到别的档里。<br>
        当前判定靠「哪个存档目录最近被写过」，进游戏读档的头几秒可能判错，判错了在这里手动切。
      </p>
      <div class=""row""><label>当前存档</label>
        <div style=""flex:1"">
          <div id=""saveInfo"" style=""font-size:13px;margin-bottom:8px"">读取中…</div>
          <select id=""saveSelect"" onchange=""switchSave()""></select>
          <div style=""margin-top:8px"">
            <button class=""ghost"" onclick=""resetSave()"">恢复自动判定</button>
            <span class=""msg"" id=""saveMsg""></span>
          </div>
        </div>
      </div>

      <div class=""sechead"">回复长度</div>
      <div class=""row""><label>旁白上限（字）<span class=""ok"">存盘即生效</span></label><input id=""replyLenNarration"" type=""number""></div>
      <p class=""hint"" style=""margin-top:-6px"">每轮回复里「旁白 / 动作描写」部分最多多少字。超了只裁旁白，不动台词。</p>
      <div class=""row""><label>台词上限（字）<span class=""ok"">存盘即生效</span></label><input id=""replyLenDialogue"" type=""number"" oninput=""calcSeconds()""></div>
      <p class=""hint"" style=""margin-top:-6px"">AI 说出口的话的字数上限，直接影响语音时长。默认 80 字约 9 秒语音。</p>
      <div class=""row""><label>总上限（字）<span class=""ok"">存盘即生效</span></label><input id=""replyLenTotal"" type=""number""></div>
      <p class=""hint"" style=""margin-top:-6px"">旁白 + 台词合计的字数上限。超了按比例删，最后还有一道硬删兜底。</p>
      <div class=""row""><label>总字数下限（0=不限）<span class=""ok"">存盘即生效</span></label><input id=""replyLenLo"" type=""number""></div>
      <p class=""hint"" style=""margin-top:-6px;line-height:1.8"">
        低于这个字数会明确要求模型写够（例：150）。0 = 不限制。<br>
        <b class=""warn"">设之前先知道这几个坑：</b><br>
        · 语音（TTS）用户：下限越高，模型越可能多写台词，朗读会变长（约 80 字 ≈ 9 秒）<br>
        · 不能超过总上限：填超了会自动收敛成总上限<br>
        · 总上限低于 130 时下限不生效（短档位优先「点到即止」）<br>
        · 低于下限只会写日志，不会自动补写（补写会破坏语音台本）<br>
        · 模型可能为了凑字数灌水——觉得回复变啰嗦就调小或改回 0
      </p>
      <p class=""hint"" style=""margin-top:-6px"">低于这个字数时，提示词会催 AI 写满。填 0 = 不管。</p>
      <div class=""row""><label>硬删倍数<span class=""ok"">存盘即生效</span></label><input id=""replyLenHardRatio"" type=""number"" step=""0.1""></div>
      <p class=""hint"" style=""margin-top:-6px"">字数超过「上限 × 这个倍数」才整段硬删，防止误删正常台词。默认 2.0（台词上限 80 → 160 才删）。</p>

      <h3>格式范本（参考）</h3>
      <table>
        <thead><tr><th>档位</th><th>旁白</th><th>台词</th><th>总</th><th>台词硬删(×2)</th><th>约语音时长</th></tr></thead>
        <tbody>
          <tr><td>很短</td><td>40</td><td>30</td><td>70</td><td>60</td><td>约 3.5 秒</td></tr>
          <tr><td>短</td><td>80</td><td>50</td><td>130</td><td>100</td><td>约 5.7 秒</td></tr>
          <tr><td style=""color:var(--jade)"">标准（默认）</td><td>120</td><td>80</td><td>200</td><td>160</td><td>约 9.2 秒</td></tr>
          <tr><td>长</td><td>200</td><td>100</td><td>300</td><td>200</td><td>约 11.5 秒</td></tr>
        </tbody>
      </table>
      <p class=""hint"">你当前填的台词上限：<b id=""dialVal"">80</b> 字 → 约 <b id=""secVal"">9.2</b> 秒语音（按 8.7 字/秒估算）</p>

      <div class=""sechead"">情事板块</div>
      <div class=""row""><label>注入模式<span class=""ok"">存盘即生效</span></label>
        <select id=""nsfwMode"">
          <option value=""auto"">auto（关键词自动触发，推荐）</option>
          <option value=""always"">always（每轮都注入）</option>
          <option value=""off"">off（从不注入）</option>
        </select>
      </div>
      <p class=""hint"" style=""margin-top:-6px"">auto = 对话里出现关键词才注入，省 token；always = 每轮都注入；off = 从不注入。</p>
      <div class=""row""><label>锁存轮数<span class=""ok"">存盘即生效</span></label><input id=""nsfwLatch"" type=""number""></div>
      <p class=""hint"" style=""margin-top:-6px"">触发一次后，接下来这么多轮继续注入，避免氛围突然断掉。默认 5 轮。</p>

      <div class=""sechead"">记忆与其它</div>
      <div class=""row""><label>抽取间隔（轮）<span class=""ok"">存盘即生效</span></label><input id=""extractInterval"" type=""number""></div>
      <p class=""hint"" style=""margin-top:-6px"">每聊这么多轮，自动把最近的对话总结进长期记忆。太小费 token，太大容易记不住细节。</p>
      <div class=""row""><label>角色卡总开关<span class=""ok"">存盘即生效</span></label>
        <select id=""cardEnabled""><option value=""true"">开</option><option value=""false"">关</option></select>
      </div>
      <p class=""hint"" style=""margin-top:-6px"">关掉后完全不注入角色卡，所有角色回到游戏原生设定。默认开。</p>
      <div class=""row""><label>角色卡专用模型<span class=""ok"">存盘即生效</span></label>
        <input id=""cardModel"" type=""text"" placeholder=""留空 = 跟游戏对话用同一个模型；可填 deepseek-v4-pro 等"">
      </div>
      <p class=""hint"" style=""margin-top:-6px"">生成 / 改写角色卡时用哪个模型。留空 = 跟游戏对话用同一个。</p>

      <div class=""sechead"">MOD 独立 API</div>
      <p class=""hint"">
        MOD 自己的 API 调用（记忆抽取 / 重评重要度 / 角色卡生成）默认跟游戏对话用同一套。
        <br>想分开就填这里：<b class=""ok"">留空 = 用游戏 settings.json 里的 ainpc 配置</b>。
      </p>
      <div class=""row""><label>API 地址<span class=""ok"">存盘即生效</span></label>
        <input id=""modApiUrl"" type=""text"" placeholder=""留空 = 用游戏的；例 https://api.deepseek.com/v1/chat/completions"">
      </div>
      <p class=""hint"" style=""margin-top:-6px"">填完整的 chat/completions 地址。留空 = 用游戏 settings.json 里的。</p>
      <div class=""row""><label>API 密钥<span class=""ok"">存盘即生效</span></label>
        <input id=""modApiKey"" type=""password"" placeholder=""留空 = 用游戏的"">
      </div>
      <p class=""hint"" style=""margin-top:-6px"">留空 = 沿用游戏里那把 key；填了优先用这个。</p>
      <div class=""row""><label>模型名<span class=""ok"">存盘即生效</span></label>
        <input id=""modModel"" type=""text"" placeholder=""留空 = 用游戏的；例 deepseek-flash"">
      </div>
      <p class=""hint"" style=""margin-top:-6px"">MOD 后台调用（记忆抽取 / 生成角色卡等）用哪个模型。留空 = 跟游戏一致。</p>
      <div class=""row""><label>dump 诊断<span class=""ok"">存盘即生效</span></label>
        <select id=""dumpEnabled""><option value=""false"">关（默认）</option><option value=""true"">开</option></select>
      </div>
      <p class=""hint"" style=""margin-top:-6px"">开启后把每轮发给 AI 的完整提示词写进 dump 目录，排查注入问题用。平时关着省磁盘。</p>
      <div class=""row""><label>启动时打开控制台<span class=""warn"">下次启动生效</span></label>
        <select id=""consoleAutoOpen""><option value=""true"">开</option><option value=""false"">关</option></select>
      </div>
      <p class=""hint"" style=""margin-top:-6px"">游戏启动后自动弹出本页面。关掉后手动访问 127.0.0.1:8848。</p>

      <button class=""save"" onclick=""saveSettings()"">保存</button>
      <span class=""msg"" id=""settingsMsg""></span>
    </section>

    <!-- ========== p5 长期记忆 ========== -->
    <section class=""page"" id=""p5"">
      <h2>长期记忆</h2>
<div class=""masterswitch""><div style=""flex:1""><b>长期记忆注入</b><div class=""dim"">关掉后本轮对话不注入记忆（仍会在后台抽取写入，不丢数据）。存盘即生效。</div></div><label class=""switch""><input type=""checkbox"" id=""sw-memoryEnabled"" onchange=""saveMasterSwitch('memoryEnabled', this.checked)""><span></span></label><span class=""msg"" id=""swmsg-memoryEnabled""></span></div>
      <p class=""hint"">这一页除了「记忆注入上限」，其余只做展示——记忆每轮自动读写，不需要重启。</p>

      <div style=""background:rgba(217,164,65,.13);border-left:3px solid #d9a441;padding:11px 15px;border-radius:6px;margin:10px 0 14px"">
        <b style=""color:#f2cd7e;font-size:13.5px"">记忆注入上限</b>
        <div style=""font-size:12px;color:#b9c0cc;margin-top:8px;line-height:1.7"">
          · 每轮最多往提示词里塞多少字记忆。太大占 context，太小会漏细节。<b>默认 3000</b>。<br>
          · 这和 RAG 检索条数是两件事：检索按相关性走，不受这个数影响。<br>
          · 老化规则：<b>1~3 级</b>连续 <b>24 轮</b>没被提到 → 降一级；降到最低再 <b>48 轮</b>没提 → 删除。<b>4~5 级永不降级、永不删除</b>。
        </div>
        <div style=""margin-top:10px;display:flex;align-items:center;gap:10px;flex-wrap:wrap"">
          <input id=""memLimitInput"" type=""number"" min=""200"" max=""12000"" step=""100"" style=""width:120px"">
          <span style=""font-size:12px;color:var(--dim)"">字 / 轮</span>
          <button class=""ghost"" onclick=""saveMemLimit()"">保存（存盘即生效）</button>
          <span class=""msg"" id=""memLimitMsg""></span>
        </div>
      </div>
      <div class=""refresh-tip"">
        ⚠ 本页<b>编辑的内容保存后会自动刷新</b>，不用按 F5；只有「在游戏里新聊的对话」要按 <b>F5</b> 才会显示出来。
      </div>
      <div class=""split"">
        <div class=""list"" id=""npcList""></div>
        <div>
          <div id=""memHead"" class=""hint"">← 选一个角色</div>
          <div id=""memBody"" style=""display:none"">
            <h3>骨架</h3>
            <p class=""hint"">关系可以逐条改、删（删掉就是她不再记得）、新增；处境是当前状态。改完点「保存骨架」。</p>
            <div id=""memSkeleton"" style=""font-size:13px;line-height:1.8""></div>
            <div style=""margin-top:8px""><button class=""ghost"" onclick=""saveSkeleton()"">保存骨架</button><span class=""msg"" id=""skelMsg""></span></div>
            <h3>关键事实</h3>
            <p class=""hint"">按类别分组、组内按重要度 5→1 排列。可以直接改文字、改等级、删掉或新增，改完点「保存事实」。</p>
            <div id=""memFacts"" style=""font-size:13px;line-height:1.8""></div>
            <div style=""margin-top:8px""><button class=""ghost"" onclick=""saveFacts()"">保存事实</button><span class=""msg"" id=""factsMsg""></span></div>
            <h3>摘要与最近对话</h3>
            <p class=""hint"">摘要段可直接改文字（保存后自动重算向量）、删除；对话原文只读。</p>
            <div id=""memChunks""></div>
            <h3>一键迁移</h3>
            <p class=""hint"">这两个操作会调用模型，需要几秒到十几秒；执行前会自动备份原文件。</p>
            <button class=""ghost"" onclick=""doTool('extract')"">立即整理</button>
        <button class=""ghost"" onclick=""doTool('reclassify')"">重评重要度</button>
            <button class=""ghost"" onclick=""doTool('compress')"">压缩旧对话</button>
        <button class=""ghost"" onclick=""cleanBackup('all','','全部')"">清理全部备份</button>
            <span class=""msg"" id=""toolMsg""></span>
            <h3>从其他存档继承记忆</h3>
            <p class=""hint"">
              角色卡是全局的（所有存档共用），只有记忆按存档分开。<br>
              想把某个存档里这个角色的记忆搬过来，选一下来源存档再点导入（当前记忆会先备份）。
            </p>
            <div style=""display:flex;gap:8px;flex-wrap:wrap;align-items:center"">
              <select id=""scopeSelect"" style=""max-width:420px""></select>
              <button class=""ghost"" onclick=""doImportScope()"">导入</button>
              <span class=""msg"" id=""scopeMsg""></span>
            </div>

            <h3>删除这个角色的记忆</h3>
            <p class=""hint"">删除前会自动导出一份 zip 备份（放在 config 目录），但<b class=""bad"">不可撤销</b>，想清楚再点。</p>
            <button class=""danger"" onclick=""doDelete()"">删除全部记忆</button>
          </div>
        </div>
      </div>
    </section>

    <!-- ========== p10 情事用语 ========== -->
    <section class=""page"" id=""p10"">
      <h2>NSFW 模块</h2>
      <p class=""hint"">这一页的四个文件<b class=""ok"">存盘即生效</b>，不用重启游戏。直接在这里改，不用碰文件。</p>

      <div class=""sechead"">情事关键词（A / B 两级触发）</div>
      <div class=""hint2"">A 级 —— 命中任意一个就触发；B 级 —— 命中两个及以上才触发（避免「吻一下」误触发）。</div>
      <div class=""entrybox"">
        <b style=""font-size:13px"">A 级（命中任意一个即触发）</b>
        <div class=""tagbox"" id=""nsfwA""></div>
      </div>
      <div class=""entrybox"">
        <b style=""font-size:13px"">B 级（命中两个及以上才触发）</b>
        <div class=""tagbox"" id=""nsfwB""></div>
      </div>

      <div class=""sechead"">高级破甲</div>
      <div class=""hint2"">只在 NSFW 板块触发后追加，日常对话不注入。极端玩法与细节要求。</div>
      <textarea id=""nsfwArmor"" rows=""8"" style=""width:100%;box-sizing:border-box""></textarea>

      <div class=""sechead"">情事用语规范（主文件）</div>
      <div class=""hint2"">仅亲密场景生效。会被版本更新替换。</div>
      <textarea id=""nsfwGuifan"" rows=""14"" style=""width:100%;box-sizing:border-box""></textarea>

      <div class=""sechead"">情事用语规范 · 自定义补充</div>
      <div class=""hint2"">你自己的偏好，<b class=""ok"">永不覆盖</b>，会追加在主文件后面。</div>
      <textarea id=""nsfwCustom"" rows=""6"" style=""width:100%;box-sizing:border-box""></textarea>

      <div style=""margin-top:14px"">
        <button class=""save"" onclick=""saveNsfw()"">保存全部</button>
        <span class=""msg"" id=""nsfwMsg""></span>
      </div>
      <p class=""hint"" style=""margin-top:10px"">位置：插件目录\config\nsfw\ ｜ <button class=""ghost"" onclick=""fetch('/api/openfolder')"">打开配置文件夹</button></p>
    </section>

    <!-- ========== p14 世界书 ========== -->
    <section class=""page"" id=""p14"">
      <h2>世界书</h2>
<div class=""masterswitch""><div style=""flex:1""><b>世界书注入</b><div class=""dim"">关掉后世界书条目不注入提示词。存盘即生效。</div></div><label class=""switch""><input type=""checkbox"" id=""sw-worldbookEnabled"" onchange=""saveMasterSwitch('worldbookEnabled', this.checked)""><span></span></label><span class=""msg"" id=""swmsg-worldbookEnabled""></span></div>
      <p class=""hint"">
        玩家自己写的世界设定：地名、势力、规矩、禁忌、境界……<b>多个角色共享同一套</b>。<br>
        按<b>关键词触发</b>：对话里出现你写的词，对应条目才会被注入（不是每轮都塞）。<br>
        <span class=""warn"">按存档分</span>：这里编辑的是<b>当前存档</b>的世界书，换存档就是另一套。
      </p>
      <div class=""refresh-tip"">
        ⚠ 本页编辑保存后会自动刷新，不用 F5；只有用文件管理器等<b>别处</b>改过文件，才要按 <b>F5</b>。
      </div>

      <div style=""display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin-bottom:12px"">
        <select id=""wbSelect"" onchange=""loadWb()"" style=""max-width:340px""></select>
        <button class=""ghost"" onclick=""newWb()"">新建</button>
        <button class=""ghost"" onclick=""inferWb()"">从游戏推断</button>
        <button class=""ghost"" onclick=""openFolder('worldbook')"">打开世界书文件夹</button>
        <button class=""ghost"" onclick=""cleanBackup('all','','全部')"">清理全部备份</button>
        <button class=""save"" onclick=""saveWb()"">保存</button>
        <button class=""danger"" onclick=""deleteWb()"">删除</button>
        <span class=""msg"" id=""wbMsg""></span>
      </div>

      <div class=""hint"" style=""margin-bottom:8px"">
        不用写 JSON，直接写设定，一行一条：<code>关键词 / 关键词：内容</code><br>
        例：<code>玄一门 / 宗门 / 外门：玄一门是正道大宗，门规森严。</code><br>
        冒号左边是触发词（多个用「/」或「、」隔开），右边是内容。写好后点「解析成条目」。
      </div>
      <textarea id=""wbRaw"" style=""min-height:150px;font-family:inherit;font-size:13px;line-height:1.7""
        placeholder=""玄一门 / 宗门 / 外门：玄一门是正道大宗，门规森严。&#10;妖兽 / 妖丹：低阶妖兽怕火，高阶会化形。""></textarea>
      <div style=""margin:8px 0""><button class=""ghost"" onclick=""parseWb()"">解析成条目</button></div>
      <div id=""wbForm"" style=""min-height:200px""></div>
      <div id=""wbDir"" class=""hint"" style=""margin-top:8px""></div>
    </section>

    <!-- ========== p4 角色卡编辑 ========== -->
    <section class=""page"" id=""p4"">
      <h2>角色卡编辑</h2>
<div class=""masterswitch""><div style=""flex:1""><b>用角色卡取代游戏角色设定</b><div class=""dim"">开启后，游戏自带的「身份/性格/外貌/善恶/背景」那段（约 2300 字）不再注入，改用角色卡「人设」区的身份/性格/善恶立场/外貌穿着。本质是「用角色卡替代游戏原文，让你可以改游戏设定」。存盘即生效。<br>兜底规则：角色卡的人设项（姓名/身份/性格/善恶/外貌）还没抓取、或还没自动生成之前，不会删原文——新角色、还没建卡的角色都不受影响。<br>「## 角色背景」也一起删，由角色世界书「角色自身」（背景+年龄）顶上。<br>前提：卡的 姓名/身份/性格/善恶/外貌 都要有；另外<b>看游戏原文里有没有那一段</b>——原文有「## 说话习惯」才要求卡里填，原文有「## 角色背景」才要求世界书「角色自身」。原文本身没写的东西，不会因为空着就不替换。</div><div class=""dim"" style=""margin-top:4px"">不用担心「原文被删」：删的只是每次发给模型的那段提示词（临时拼的），游戏本体的角色数据 / 存档 / 对话记录都不会动；关掉这个开关，下一轮就恢复原文。</div></div><label class=""switch""><input type=""checkbox"" id=""sw-replaceOfficialProfile"" onchange=""saveMasterSwitch('replaceOfficialProfile', this.checked)""><span></span></label><span class=""msg"" id=""swmsg-replaceOfficialProfile""></span></div>
<div class=""masterswitch""><div style=""flex:1""><b>角色卡注入</b><div class=""dim"">关掉后角色卡不注入提示词（卡还在，不会丢）。存盘即生效。</div></div><label class=""switch""><input type=""checkbox"" id=""sw-cardEnabled"" onchange=""saveMasterSwitch('cardEnabled', this.checked)""><span></span></label><span class=""msg"" id=""swmsg-cardEnabled""></span></div>
<div class=""masterswitch""><div style=""flex:1""><b>角色世界书注入</b><div class=""dim"">关掉后角色世界书不注入提示词（条目还在）。存盘即生效。</div></div><label class=""switch""><input type=""checkbox"" id=""sw-lorebookEnabled"" onchange=""saveMasterSwitch('lorebookEnabled', this.checked)""><span></span></label><span class=""msg"" id=""swmsg-lorebookEnabled""></span></div>
      <p class=""hint"">
        角色卡是<b>补充层</b>：它讲的是游戏设定没写的东西（口吻、绝不说、感官标识、底线禁忌、人设原稿）。<br>
        保存后下次进对话生效。没有卡的角色不会注入任何内容。
      </p>
      <div class=""refresh-tip"">
        ⚠ <b>刚聊过的角色才抓得到资料</b>：先在游戏里和他/她对话一次，再按 <b>F5</b> 刷新本页，角色才会出现在下拉框里。<br>
        下拉框里已经有角色、但看不到卡或世界书时，也按 <b>F5</b> 再试一次。
      </div>
      <div class=""row""><label>搜索角色</label>
        <input id=""cardNpcSearch"" type=""text"" placeholder=""输入名字或角色代码，边打边筛""
               oninput=""filterCardNpcs()"">
        <label style=""display:flex;align-items:center;gap:6px;margin-left:12px;font-size:13px;color:var(--dim);cursor:pointer;white-space:nowrap""><input type=""checkbox"" id=""cardOnlyHasCard"" onchange=""filterCardNpcs()"" style=""width:auto;margin:0""> 只看有卡的</label>
      </div>
      <div class=""row""><label>选择角色</label>
        <select id=""cardNpcSelect"" onchange=""onCardNpcChange()""></select>
      </div>
      <div class=""row""><label>这张存档用哪张卡</label>
        <div style=""flex:1"">
          <select id=""cardSlotSelect"" onchange=""pickCard()""></select>
          <div class=""hint"" style=""margin-top:6px"">
            一个角色可以有多张卡（默认卡 + 若干备用卡），<b>每个存档可以选不同的那张</b>。<br>
            备用卡的文件名格式：角色名-角色代码-卡名.json（例如 某某-150101-日常.json）
          </div>
        </div>
      </div>
      <div class=""row""><label>改卡名</label>
        <div style=""flex:1"">
          <input id=""cardRenameInput"" type=""text"" placeholder=""输入新的卡名（只改文件名前缀，列表里会显示它）"">
          <div style=""margin-top:8px"">
            <button class=""ghost"" onclick=""renameCardFile()"">改文件名</button>
            <span class=""msg"" id=""cardRenameMsg""></span>
          </div>
          <div class=""hint"" style=""margin-top:6px"">改的是文件名前缀（<b>-角色代码</b> 不动，照样能对上角色），同时会把卡里的「姓名」一起改成新名字。以后保存也不会被覆盖回去。</div>
        </div>
      </div>
      <div style=""margin:10px 0;display:flex;gap:8px;flex-wrap:wrap"">
        <button class=""ghost"" onclick=""loadCard()"">重新读取</button>
        <button class=""save"" onclick=""saveCard()"">保存</button>
        <button class=""ghost"" onclick=""saveAsNewCard()"">另存为新卡</button>
        <button class=""ghost"" onclick=""fillFromSource()"">从游戏资料填入</button>
        <button class=""ghost"" onclick=""openFolder('card')"">打开角色卡文件夹</button>
        <button class=""ghost"" onclick=""cleanBackup('all','','全部')"">清理全部备份</button>
        <button class=""ghost"" onclick=""doRename()"">一键改成真名</button>
        <button class=""ghost"" onclick=""cleanOldNames()"">清理旧名残留</button>
        <button class=""danger"" onclick=""deleteCard()"">删除这张卡</button>
        
        <span class=""msg"" id=""cardMsg""></span>
      </div>
      <p class=""hint"">下面是卡片的原始 JSON。字段含义见教程页；不认识的字段原样保留即可。</p>
      <details open style=""margin:10px 0"">
        <summary style=""cursor:pointer;color:var(--gold);font-size:13px"">角色资料（写卡参考，只读）</summary>
        <div class=""hint2"" style=""margin:6px 0"">这是从游戏提示词里抓到的原始资料（身份/性格/外貌/善恶/背景），写角色卡时可以对照看。</div>
        <pre id=""cardSourceText"" style=""max-height:280px;overflow:auto;font-size:12px;line-height:1.7;white-space:pre-wrap;background:var(--panel2);border:1px solid var(--line);border-radius:6px;padding:10px""></pre>
      </details>

      <div id=""cardForm"" style=""min-height:200px""><p style=""color:var(--dim)"">先选一个角色</p></div>

      <h3>角色世界书（这个角色的背景细节）</h3>
      <p class=""hint"">
        角色卡讲「她<b>现在是什么样</b>」，角色世界书讲「她<b>以前发生过什么</b>」——关系、过往、习惯、秘密。<br>
        <b>关键词触发</b>：对话里出现条目写的词，那条才注入（不是每轮都塞）。<br>
        <span class=""ok"">自动抓取</span>：进游戏和这个角色聊一句，插件会从游戏关系网自动抓「关系 + 角色信息」生成这份书。
      </p>
      <div style=""display:flex;gap:8px;flex-wrap:wrap;margin-bottom:10px"">
        <button class=""ghost"" onclick=""loadLore()"">重新读取</button>
        <button class=""ghost"" onclick=""genLore()"">从关系网重新生成</button>
        <button class=""save"" onclick=""saveLore()"">保存世界书</button>
        <button class=""danger"" onclick=""deleteLore()"">删除世界书</button>
        <button class=""ghost"" onclick=""cleanBackup('all','','全部')"">清理全部备份</button>
        <span class=""msg"" id=""loreMsg""></span>
      </div>
      <div class=""hint"" style=""margin-bottom:8px"">
        每条一个框：关键词（对话提到才触发）+ 内容。<b>单次最多 4 条 / 600 字</b>。
      </div>
      <div id=""loreForm"" style=""min-height:200px""></div>
      <div id=""loreFile"" class=""hint"" style=""margin-top:6px""></div>
    </section>

    <!-- ========== p7 角色卡优化 ========== -->
    <section class=""page"" id=""p7"">
      <h2>角色卡优化</h2>
      <p class=""hint"">检查你写的角色卡<b>模型能不能读懂</b>：含糊的词、只有形容词没有具体行为的句子会被挑出来，并给改写建议。</p>
      <p class=""hint""><b class=""ok"">能读懂的字段一个字都不动</b>，只有挑出来的那几项才给方案。流程：检查 → 逐项勾选采纳 → 确认覆盖原卡。</p>

      <div class=""row""><label>选择角色</label>
        <select id=""genNpcSelect""></select>
      </div>
      <div class=""row""><label>检查强度</label>
        <select id=""optCheckMode"">
          <option value=""1"">规则 + 模型（更准，会调一次 API）</option>
          <option value=""0"">只跑规则（免费、快）</option>
        </select>
      </div>
      <div class=""row""><label>优化前备份</label>
        <label style=""padding-top:7px;color:var(--dim);font-size:13px""><input type=""checkbox"" id=""optBackup"" checked> 应用修改时先备份原卡（确认应用成功后自动删掉备份）</label>
      </div>

      <h3>优化与检查</h3>
      <p class=""hint"">对<b>已经有的角色卡</b>做体检和重写（先选角色 → 再点按钮）。</p>
      <div style=""display:flex;gap:8px;flex-wrap:wrap;margin-bottom:10px"">
        <button class=""ghost"" onclick=""doCheck()"">开始检查</button>
        <button class=""ghost"" onclick=""loadDraft()"">读取未完成优化</button>
        <button class=""ghost"" onclick=""applyDraft()"">确认应用</button>
        <button class=""danger"" onclick=""discardDraft()"">放弃</button>
      </div>
      <span class=""msg"" id=""optMsg""></span>
      <div id=""checkResult"" style=""margin-top:12px""></div>

      <h3 style=""margin-top:26px"">动态试演</h3>
      <p class=""hint"">
        角色卡改完不知道效果好不好？选一个场景，让它<b>当场演一段</b>给你看。<br>
        不需要进游戏，新卡、刚改完的卡都能试 —— 不用进游戏就能验收。
      </p>
      <div class=""chips"" id=""pvScenes""></div>
      <div class=""grid2"">
        <div class=""row""><label>用的模型</label>
          <input id=""pvModel"" type=""text"" placeholder=""留空 = 用 MOD 默认模型"">
        </div>
        <div class=""row""><label>强制注入 NSFW</label>
          <label style=""padding-top:7px;color:var(--dim);font-size:13px""><input type=""checkbox"" id=""pvNsfw""> 是</label>
        </div>
      </div>
      <div class=""row""><label>自定义台词</label>
        <input id=""pvExtra"" type=""text"" placeholder=""（可留空，用上面选的场景）"">
      </div>
      <button class=""save"" onclick=""doPreview()"">开始试演</button>
      <span class=""msg"" id=""pvMsg""></span>
      <pre id=""pvOut"" style=""display:none;margin-top:14px;white-space:pre-wrap;line-height:1.8""></pre>
    </section>

    <!-- ========== p8 用量统计 ========== -->
    <section class=""page"" id=""p8"">
      <h2>Token 花费预览</h2>
      <p class=""hint"">
        下面是<b>估算</b>（不是精确统计）：你开什么块、关什么块，大概会消耗多少 token。<br>
        数值来自实测（一次完整对话 ≈ 10151 字 ≈ 约 6000 token 输入），中文按 0.6 token/字保守估算。<br>
        关掉某项后，那项就不占额度（<span style=""color:var(--red)"">红色</span> = 当前关着）。
      </p>
      <div class=""stat"">
        <div><b id=""tk-token"">—</b><span>预估最多 token</span></div>
        <div><b id=""tk-all"">—</b><span>各项上限相加（字）</span></div>
        <div><b id=""tk-mem"">—</b><span>记忆注入上限（字）</span></div>
        <div><b id=""tk-cost"">—</b><span>预估费用（元/轮）</span></div>
      </div>
      <table id=""tk-table"">
        <thead><tr><th>内容块</th><th>估算字数</th><th>状态</th><th>说明</th></tr></thead>
        <tbody></tbody>
      </table>
      <p class=""hint"" style=""margin-top:12px"">
        费用按 DeepSeek 官方【空闲时段】价估算：deepseek-flash 输入 1 元、输出 4 元（每百万 token，输入按未命中缓存算）；
        deepseek-v4-pro 约 4.5 / 13.5。<br>
        高峰时段（周一至周五 9:00-12:00、14:00-18:00）官方价翻倍，这里不自动区分；实际金额以官方账单为准。<br>
        各功能的总开关在对应页面的顶部。改完要<b>重启游戏</b>才生效（配置启动时读一次）。
      </p>
    </section>

    <!-- ========== p2 日志 ========== -->
    <section class=""page"" id=""p2"">
      <h2>日志</h2>
      <p class=""hint"">显示游戏日志的最后 200 行（BepInEx/LogOutput.log）。排查问题先看这里。</p>
      <div>
        <button class=""ghost"" onclick=""loadLog()"">刷新</button>
        <button class=""ghost"" onclick=""toggleAuto()""><span id=""autoLabel"">自动刷新：关</span></button>
        <span class=""msg"" id=""logMsg""></span>
      </div>
      <pre id=""logBox"" style=""margin-top:12px;max-height:64vh;font-size:12px;line-height:1.55""></pre>
    </section>

    <!-- ========== p11 教程 ========== -->
    <section class=""page"" id=""p11"">
      <h2>教程</h2>
      <h3>第一次使用</h3>
      <p class=""hint"" style=""line-height:1.9"">
        1. <b>解压压缩包 → 双击「安装.bat」→ 填游戏根目录（可拖入）→ 回车</b>，显示「[完成]」即可。<br>
        　　· 没装 BepInEx 会<b>自动装</b>（压缩包里带好了）；已有就跳过、不覆盖<br>
        　　· 重复运行 = 覆盖更新，不动 config / 存档 / 角色卡 / 记忆<br>
        　　· config 和词表不用准备，插件第一次启动自动生成<br>
        　　· 首次启动游戏会慢几分钟（BepInEx 在生成适配文件）<br>
        2. 在游戏里打开「设置 → 自定义 AI 模型」，填：<br>
        　　接口地址 <b>https://api.deepseek.com/v1/chat/completions</b>（<b>直连 DeepSeek</b>，
        别填 127.0.0.1 / 8788 那种旧代理）<br>
        　　API 密钥（sk- 开头）+ 模型名 <b>deepseek-flash</b><br>
        3. 进游戏和任意 NPC 对话，插件会自动接管提示词注入与记忆读写。<br>
        4. 本控制台随游戏启动自动打开；关掉了也不要紧，浏览器地址栏输入 <b>127.0.0.1:8848</b> 就能回来。
      </p>
      <h3>全局设置</h3>
      <p class=""hint"" style=""line-height:1.9"">
        · <b>回复长度</b>：旁白上限、台词上限、总上限、硬删倍数。改完<b class=""warn"">需要重启游戏</b>才生效。<br>
        · <b>情事板块</b>：auto = 关键词自动触发（推荐）；always = 每轮都注入；off = 关闭。<br>
        · <b>抽取间隔</b>：每多少轮对话自动抽取一次记忆，默认 30 轮。
      </p>
      <h3>情事用语</h3>
      <p class=""hint"" style=""line-height:1.9"">
        四个文件在插件目录的 <b>config\nsfw\</b> 下，<b class=""ok"">存盘即生效</b>，不用重启。<br>
        · 情事关键词.txt —— 触发词表（哪些词会让板块自动开启）<br>
        · 高级破甲.txt —— 极端细节授权<br>
        · 情事用语规范.txt —— 官方主文件，更新会替换<br>
        · 情事用语规范_自定义.txt —— 你自己的补充，<b>永不被覆盖</b>
      </p>
      <h3>提示词模板（inject.txt / tier2-inject.txt）</h3>
      <p class=""hint"" style=""line-height:1.9"">
        位置：插件目录 <b>config\inject.txt</b>（档位 1，默认）、<b>config\tier2-inject.txt</b>（档位 2）。<br>
        · 想改「内容尺度 / 越界界定 / 剧情前提 / 格式」这些硬要求，改这两个文件。<br>
        · <b class=""warn"">直接改文件不会自动生效</b>：改完要<b>重启游戏</b>，或者去控制台任意一页点一次「保存」触发热重载。<br>
        · 想切档位 2：改 <b>config.json</b> 里的 <b>""tier"": 2</b>（没有界面开关），再重启或控制台点保存触发。<br>
        · 档位 2 时如果 <b>tier2-inject.txt</b> 是空的或不存在，会<b>静默用内置默认</b>（不是你写的），注意检查。
      </p>
      <h3>长期记忆</h3>
      <p class=""hint"" style=""line-height:1.9"">
        · 左边选角色，右边看他记住的事（骨架 / 关键事实 / 最近 30 轮）<br>
        · <b>重评重要度</b>：按新标准重新给事实打分（旧版本记忆等级偏松，建议跑一次）<br>
        · <b>压缩旧对话</b>：把最早的对话合成摘要，省 token<br>
        · <b>删除记忆</b>：会先自动导出 zip 备份，但删除本身不可撤销
      </p>
      <h3>常见问题</h3>
      <p class=""hint"" style=""line-height:1.9"">
        · <b>装了没反应？</b> 先看页头的接口徽章：红「未配置 / 旧本地代理」就去游戏「设置 → 自定义 AI 模型」填
          https://api.deepseek.com/v1/chat/completions + key；再搜日志里「[接口]」那一行。<br>
        · <b>角色说话不像她？</b> 去角色卡里补「口吻」和「绝不说」，这两项对像不像影响最大。<br>
        · <b>回复太短？</b> 调大台词上限，或者把总上限提到 250 以上。<br>
        · <b>开场白又冒出来了？</b> 老角色才会自动隐藏；新角色第一次见面保留是正常的。
      </p>

      <h3>自动好感度（测试功能）</h3>
      <p class=""hint"" style=""line-height:1.9"">
        在「自动好感度」页开启后，每轮对话会额外调一次 API，判断玩家这轮的表现对 NPC 好感的影响，
        然后调用游戏接口真实加减好感。判断结果能在页面底部表格里逐轮查看。<br>
        <b>想省钱 / 想离线：</b>用本地小模型（Ollama）：<br>
        ① 安装 Ollama（ollama.com 下载，装完运行一次）<br>
        ② 命令行执行 <code>ollama run qwen2.5:0.5b</code>（约 300MB，CPU 就能跑）<br>
        ③ 回到「自动好感度」页：API 地址填 <code>http://localhost:11434/v1/chat/completions</code>，
        模型名填 <code>qwen2.5:0.5b</code>，点保存<br>
        之后好感判断全在本地跑，不花 API 钱。
      </p>
    </section>

    <!-- ========== p13 AI 答疑 ========== -->
    <section class=""page"" id=""p13"">
      <h2>AI 答疑</h2>
      <p class=""hint"">
        描述你遇到的问题，我会读最近的运行日志帮你判断原因。<br>
        <b class=""warn"">会消耗你自己的 API 额度</b>（一次几百 token）。
      </p>
      <textarea id=""askInput"" style=""min-height:90px"" placeholder=""例：角色说话不像她 / 回复太短 / 开场白又冒出来了 / 情事板块没生效""></textarea>
      <div style=""margin-top:10px"">
        <button class=""save"" onclick=""doAsk()"">提问</button>
        <span class=""msg"" id=""askMsg""></span>
      </div>
      <pre id=""askAnswer"" style=""margin-top:14px;display:none""></pre>
    </section>

    <!-- ========== p9 更新日志 ========== -->
    <section class=""page"" id=""p9"">
      <h2>更新日志</h2>
      <h3>V1.2.2（当前版本）</h3>
      <p class=""hint"" style=""line-height:1.9"">
        · <b>修（严重）：没装语音 MOD 的玩家，破甲/角色卡/NSFW/世界书全都不注入</b> —— 原来整个 tail 被「有没有语音 MOD」的开关包住，已拆开（tail 无条件注入，只有死命令随语音 MOD 条件加）<br>
        · <b>修：好感度判断 HTTP 404</b> —— 玩家 settings.json 的 url 只填域名时自动补 /v1/chat/completions<br>
        · <b>修：情事场景用语变保守（「那里/那个地方」回避）</b> —— NSFW 用语规范被角色卡压过，已挪到角色卡之后、离输出最近<br>
        · <b>修：旁白/场景细节偏少</b> —— 补回「旁白可以细腻，多写场景、环境、动作、心理，不必刻意压缩」<br>
        · <b>修：字数改了不生效</b> —— 改为动态读取配置（控制台改多少显示多少）<br>
        · <b>优化：提示词瘦身</b> —— 删掉和死命令重复的【JSON 结构】段、没装语音 MOD 不要求 a1ttsv2、精简格式示例与问句段<br>
        · <b>优化：记忆块挪到 prompt 末尾</b> —— tail 的 5300+ 字从「每轮重新算」变成「命中缓存」，每轮省约 3000 token<br>
        · <b>优化：记忆去重更积极</b>（0.72 → 0.60 + 共同 bigram ≥ 6）<br>
        · <b>改进：人设页新增「说话习惯」字段</b> —— 自动从游戏原文抓取，可自己改<br>
        · <b>改进：「完全替换游戏设定」改 6 项全齐才删原文</b>（含说话习惯），缺一项保留原文<br>
        · <b>改进：角色世界书「角色自身」= 背景 + 年龄的唯一来源</b> —— 背景不再三重重复<br>
        · <b>新增：长期记忆「立即整理」按钮</b> —— 手动把当前对话整理成摘要+事实+骨架<br>
        · <b>改进：记忆抽取间隔默认 48 → 30 轮</b>
      </p>
      <h3>V1.2.1</h3>
      <p class=""hint"" style=""line-height:1.9"">
        · <b>配置热重载</b>：控制台改完点保存即生效，不用重启游戏<br>
        · <b>「完全替换游戏设定」默认开</b>：6 项全齐才删原文（属性/设定/背景全删），由卡人设 5 项 + 世界书「角色自身」顶上<br>
        · <b>自动好感度（测试功能）</b>：独立选项卡，异步/同步可选，规则只改数字和词块，判断模型可选本地 Ollama 或别的云 API<br>
        · <b>删除键</b>：角色卡 / 角色世界书都能删（先自动备份）<br>
        · <b>修：字数规则没落地</b>：游戏原文的字数句现在真能被替换，【字数】段也能正常追加（原来被误判成已存在）<br>
        · <b>修：记忆保存没效果</b>：骨架/事实保存时拿不到角色 ID，请求一直没发出去<br>
        · <b>修：关系重复</b>：骨架【关系】和 4~5 级事实不再重复注入
      </p>
      <h3>V1.2</h3>
      <p class=""hint"" style=""line-height:1.9"">
        ★ 本次是重写：从「Python + Caddy 代理」改成「C# + BepInEx 插件」，
        不再需要启动器、不再占本地端口；老数据可在「迁移」页一键迁过来。
      </p>

      <h3>新增</h3>
      <p class=""hint"" style=""line-height:1.9"">
        · <b>控制台</b>：本地网页（127.0.0.1:8848），随游戏自动打开，12 个页面<br>
        · <b>角色卡</b>：八段式渲染 + 自动生成 + 字段表编辑 + 优化建议 + AI 生卡 + 动态试演<br>
        · <b>角色世界书</b>：角色自身 / 关系条目 / 手写条目，命中词触发注入<br>
        · <b>代演在场角色</b>：让场景里本来就在场的其他人说一句（默认关，不依赖角色卡）<br>
        · <b>长期记忆重写</b>：BM25 + BGE 语义 + RRF 融合；事实分级；老化与遗忘纪录<br>
        · <b>记忆注入上限</b>：数字输入（默认 3000 字/轮），不再分档位<br>
        · <b>迁移页</b>：1.1.2 角色卡 + 记忆一键迁入，分开统计成功 / 失败<br>
        · <b>一键改名</b>：皮肤名 → 真名，批量改 meta / chunks / 卡文件名<br>
        · <b>存档隔离</b>：记忆和角色卡按存档分开，不串档<br>
        · <b>Token 预览</b>：预估最多 token + 费用（元/轮，按官方空闲价）<br>
        · <b>诊断.exe</b>：12 项安装检查，玩家自己排查<br>
        · <b>记忆维护</b>：一键重评重要度 / 压缩摘要
      </p>

      <h3>调整</h3>
      <p class=""hint"" style=""line-height:1.9"">
        · <b>情事板块独立</b>：关键词 A / B 两级触发 + 锁存轮数；词表外置，存盘即生效<br>
        · <b>回复长度</b>：旁白 / 台词 / 总上限 / 硬删倍数四项，带包容性校验；台词超限不主动裁<br>
        · <b>人设区 7 项</b>：姓名 / 身份 / 性格 / 善恶立场 / 外貌穿着 标 🎭，只有开「取代游戏角色设定」才注入<br>
        · <b>口吻区精简</b>：情绪外露度 → 性格；行为逻辑设定独立成「行为逻辑」；感官标识 → 外貌穿着<br>
        · <b>Token 预览</b>：从「当前生效」改为「预估最多」<br>
        · <b>后台调用</b>：默认沿用游戏里的 API key，也可单独配置
      </p>

      <h3>修复</h3>
      <p class=""hint"" style=""line-height:1.9"">
        · 开场白：老角色历史开场白不进上下文，新角色保留并朗读<br>
        · 补写破坏语音台本：默认关闭补写，交给语音 MOD 自己处理<br>
        · 游戏更新后 REPLACE 失配：启动日志打印命中条数，一眼看出异常<br>
        · 名字带数字后缀：提示词明确那不是名字的一部分<br>
        · 记忆越玩越长：老化 + 注入上限双重控制
      </p>
      <h3>V1.1.2</h3>
      <p class=""hint"" style=""line-height:1.9"">
        代理层版本：通过 caddy + core_proxy 拦截请求并改写 messages。<br>
        · 首次引入「长期记忆 + 角色卡 + 末尾锚点」三段式注入<br>
        · 情事板块、记忆重评、摘要压缩<br>
        · 12 页控制台
      </p>
    </section>

  <section class=""page"" id=""p16"">
      <h2>自动好感度 <span class=""warn"">测试功能</span></h2>
      <p class=""hint"" style=""line-height:1.8"">
        每轮对话后，单独调一次 API 判断「这一轮玩家的言行对角色好感的影响」，再调游戏接口真实加减好感。<br>
        <b class=""warn"">多花多少：</b>每轮多一次 API 调用，约 <b>1000~1300 token 输入 + 十几 token 输出</b>，
        按 deepseek-flash 空闲价约 <b>每轮 ¥0.01 左右</b>（高峰期翻倍）。
      </p>

      <div class=""masterswitch""><div style=""flex:1""><b>启用自动好感度</b><div class=""dim"">关掉后完全不调用、不加减。存盘即生效。</div></div><label class=""switch""><input type=""checkbox"" id=""sw-autoIntimacyEnabled"" onchange=""saveMasterSwitch('autoIntimacyEnabled', this.checked)""><span></span></label><span class=""msg"" id=""swmsg-autoIntimacyEnabled""></span></div>

      <div class=""row"" style=""margin-top:12px""><label>生效方式</label>
        <select id=""autoIntimacyMode"">
          <option value=""async"">异步（迟一轮生效，不影响回复速度）</option>
          <option value=""sync"">同步（当前轮立即生效，退出对话前最后一轮不生效）</option>
        </select>
      </div>
      <p class=""hint"" style=""margin-top:-4px"">异步：聊下一句时才把上一轮的好感加上，不影响回复速度；<b class=""warn"">同步：每轮回复整体会迟约 2 秒</b>。</p>

      <div style=""margin-top:14px""><b style=""color:var(--gold);font-size:13px"">好感度规则（改数字就行）</b>
        <div class=""row"" style=""margin-top:8px""><label>正面最多加</label><input id=""autoIntimacyMaxUp"" type=""number"" min=""1"" max=""20"" step=""1"" style=""width:90px""><span style=""color:var(--dim)"">点（+1 ~ +N）</span></div>
        <div class=""row""><label>负面最多扣</label><input id=""autoIntimacyMaxDown"" type=""number"" min=""1"" max=""20"" step=""1"" style=""width:90px""><span style=""color:var(--dim)"">点（-1 ~ -N）</span></div>
        <div class=""row"" style=""margin-top:6px""><label>加分行为</label><input id=""autoIntimacyPositive"" style=""flex:1""></div>
        <div class=""row""><label>扣分行为</label><input id=""autoIntimacyNegative"" style=""flex:1""></div>
        <p class=""hint"">「普通日常 / 闲聊 / 无关痛痒 = 0」是固定的，不用改。</p>
      </div>

      <div style=""margin-top:14px""><b style=""color:var(--gold);font-size:13px"">判断模型（可选）</b>
        <div class=""row"" style=""margin-top:6px""><label>API 地址</label><input id=""autoIntimacyApiUrl"" style=""flex:1"" placeholder=""留空 = 用主对话 API；本地填 http://localhost:11434/v1/chat/completions""></div>
        <div class=""row""><label>API 密钥</label><input id=""autoIntimacyApiKey"" style=""flex:1"" placeholder=""本地模型留空即可；填别家云 API 时填 sk-…""></div>
        <div class=""row""><label>模型名</label><input id=""autoIntimacyModel"" style=""flex:1"" placeholder=""留空 = qwen2.5:0.5b""></div>
        <p class=""hint"">两种都行：<b>本地 Ollama</b>（0 元，CPU 能跑）或<b>别的云 API</b>（填地址 + 密钥 + 模型名）。都留空就沿用主对话那套。详见「教程」页。</p>
      </div>
      <div style=""margin:10px 0"">
        <button class=""save"" onclick=""saveAutoIntimacy()"">保存</button>
        <span class=""msg"" id=""autoIntimacyMsg""></span>
      </div>
      <div class=""refresh-tip"" style=""margin-top:16px"">
        ⚠ 测试功能：每轮对话会额外调用一次 API 判断好感（约 1000~1300 token，¥0.01 左右/轮）。
        异步模式不影响回复速度；<b>同步模式每轮回复整体会迟约 2 秒</b>，且退出对话前最后一轮不生效。
      </div>

      <h3 style=""margin-top:22px"">本次统计 <span id=""aiStatNpc"" class=""hint""></span></h3>
      <p class=""hint"">记录当前这段对话里，每轮判断出来的好感变化（只在本次运行内存里，重启游戏清空）。</p>
      <table id=""aiStatTable"" style=""width:100%;border-collapse:collapse;font-size:13px"">
        <thead><tr style=""color:var(--dim);text-align:left"">
          <th style=""padding:6px 8px;border-bottom:1px solid rgba(255,255,255,.1);width:90px"">轮数</th>
          <th style=""padding:6px 8px;border-bottom:1px solid rgba(255,255,255,.1);width:140px"">好感度变化</th>
          <th style=""padding:6px 8px;border-bottom:1px solid rgba(255,255,255,.1)"">总计变化</th>
        </tr></thead>
        <tbody></tbody>
      </table>
  </section>

  <section class=""page"" id=""p15"">
      <h2>从 1.1.2 迁移数据</h2>
      <p class=""hint"">把 1.1.2 的「角色卡/」和「memory/」目录里的数据迁到 1.2（角色卡加 ID、记忆转碎片化）。迁移前会备份，不动原文件。</p>
      <div class=""refresh-tip"">⚠ 迁移要靠「皮肤名 → 角色 ID」对照表。请先<b>重启游戏 → 随便跟一个角色聊一句</b>，再回来点扫描（扫描时会显示已缓存多少个角色名）。</div>

      <div class=""row"" style=""align-items:center"">
        <label>1.1.2 数据目录</label>
        <input id=""migDir"" style=""flex:1"" placeholder=""例如：E:\下载\不问凡尘 · AI 角色记忆与人格增强"">
      </div>
      <div style=""margin:10px 0"">
        <button class=""save"" onclick=""doMigrateScan()"">扫描</button>
        <span class=""msg"" id=""migMsg""></span>
      </div>

      <div id=""migResult""></div>
    </section>

</main>
  <footer>
    控制台只绑定 127.0.0.1，局域网访问不到 ｜ 关闭游戏即停止控制台<br>
    作者 AShe ｜ 问题/反馈/交流可加 QQ 群：338115203
  </footer>
</div>

<script>
// ===== Token 花费预览 =====
async function doMigrateScan() {
  const dir = (document.getElementById('migDir') || {}).value.trim();
  const m = document.getElementById('migMsg');
  const box = document.getElementById('migResult');
  if (!dir) { if (m) { m.className='msg bad'; m.textContent='先填 1.1.2 数据目录'; } return; }
  if (m) { m.className='msg warn'; m.textContent='扫描中…'; }
  box.innerHTML = '';
  try {
    const r = await fetch('/api/migrate/scan', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({dir: dir}) });
    const o = await r.json();
    if (!o.ok) { if (m) { m.className='msg bad'; m.textContent=o.msg || '失败'; } return; }
    const cardsN = (o.cards||[]).length;
    const matchedN = (o.cards||[]).filter(function(x){return x.matched;}).length;
    const skins = o.skins || 0;
    if (m) {
      m.className = (skins > 0 && matchedN > 0) ? 'msg ok' : 'msg warn';
      m.textContent = '扫描完成：角色卡 ' + cardsN + ' 个（匹配 ' + matchedN + ' 个），记忆 ' + (o.memories||[]).length + ' 个｜角色名缓存 ' + skins + ' 个';
    }
    renderMigrate(box, o.cards || [], o.memories || [], skins);
  } catch (e) { if (m) { m.className='msg bad'; m.textContent='请求失败：' + e.message; } }
}

function renderMigrate(box, cards, memories, skins) {
  window._migCards = cards || [];
  window._migMemories = memories || [];
  skins = skins || 0;
  var warn = '';
  if (skins === 0) {
    warn = '<div class=""msg bad"" style=""margin:8px 0"">角色名缓存是空的 —— 插件还没抓到「皮肤名 → 角色 ID」对照表，所以一个都匹配不上。<br>'
      + '→ <b>重启游戏 → 进游戏和任意角色聊一句（等它回完）→ 再回这里扫描</b>。<br>'
      + '还不行的话，把 BepInEx\\LogOutput.log 里 [改名缓存] 开头那几行发给作者。</div>';
  } else if ((cards||[]).length && !(cards||[]).some(function(c){ return c.matched; })) {
    warn = '<div class=""msg warn"" style=""margin:8px 0"">已缓存 ' + skins + ' 个角色名，但这些文件一个都没对上 —— 可能 1.1.2 里的角色名被改过，或这个角色在当前存档没出现过。</div>';
  }
  const row = (list, isCard) => {
    if (!list || !list.length) return '<p style=""color:var(--dim)"">（无）</p>';
    const all = list || [];
    const matchedList = all.filter(function(y){ return y.matched; });
    const th = 'style=""text-align:left;padding:4px 8px;border-bottom:1px solid var(--line)""';
    let r = '<table style=""width:100%;border-collapse:collapse;font-size:12.5px""><thead><tr>'
      + '<th ' + th + '>1.1.2 文件</th>'
      + '<th ' + th + '>皮肤名</th>'
      + '<th ' + th + '>ID</th>'
      + '<th ' + th + '>真名</th>'
      + '<th ' + th + '>匹配</th>';
    if (isCard) r += '<th ' + th + '>姓名（可改）</th><th ' + th + '>身份</th><th ' + th + '>善恶立场</th>';
    r += '</tr></thead><tbody>';
    all.forEach((x) => {
      r += '<tr><td style=""padding:3px 8px"">' + esc(x.file) + '</td>'
        + '<td style=""padding:3px 8px"">' + esc(x.skin || '') + '</td>'
        + '<td style=""padding:3px 8px"">' + esc(x.id || '') + '</td>'
        + '<td style=""padding:3px 8px"">' + esc(x.name || '') + '</td>'
        + '<td style=""padding:3px 8px"">' + (x.matched ? '<span style=""color:var(--jade)"">✓</span>' : '<span style=""color:#e07a7a"">✗</span>') + '</td>';
      if (isCard && x.matched) {
        const mi = matchedList.indexOf(x);
        r += '<td style=""padding:3px 4px""><input data-mig=""姓名"" data-idx=""' + mi + '"" value=""' + attrEsc(x.name || '') + '"" style=""width:96px;font-size:12px""></td>'
          + '<td style=""padding:3px 4px""><input data-mig=""身份"" data-idx=""' + mi + '"" style=""width:88px;font-size:12px""></td>'
          + '<td style=""padding:3px 4px""><input data-mig=""善恶立场"" data-idx=""' + mi + '"" style=""width:80px;font-size:12px""></td>';
      } else if (isCard) {
        r += '<td></td><td></td><td></td>';
      }
      r += '</tr>';
    });
    r += '</tbody></table>';
    return r;
  };
  box.innerHTML = warn + '<h3>角色卡（' + (cards.length||0) + '）</h3>'
    + '<p class=""hint2"">迁移会自动把「感官标识」→ 外貌穿着、「情绪外露度」→ 性格、「行为逻辑设定」→ 口吻·行为逻辑，迁完直接能用，不用再编辑。<br>'
    + '姓名 / 身份 / 善恶立场（🎭）只有开了「用角色卡取代游戏角色设定」才生效，默认不用填。</p>'
    + '<p class=""hint2"" style=""color:#e8cf9a"">★ 自动识别的角色名不对？迁移后去「角色卡编辑 → 改卡名」，填上真名点「改文件名」即可 —— 只改文件名前缀，不影响角色匹配。</p>'
    + row(cards, true)
    + '<h3>记忆（' + (memories.length||0) + '）</h3>' + row(memories, false)
    + '<div style=""margin-top:12px""><label style=""font-size:13px;color:var(--dim);cursor:pointer""><input type=""checkbox"" id=""migAutoReclassify"" checked> 迁移后自动重评重要度（1.1.2 的等级标准和 1.2 不同，建议勾上）</label></div>'
    + '<div style=""margin-top:10px""><button class=""save"" onclick=""doMigrateApply()"">开始迁移</button><span class=""msg"" id=""migApplyMsg""></span></div>';
}

async function doMigrateApply() {
  const dir = (document.getElementById('migDir') || {}).value.trim();
  const m = document.getElementById('migApplyMsg');
  const cards = window._migCards || [];
  const matched = cards.filter(x => x.matched);
  if (!matched.length) { if (m) { m.className='msg bad'; m.textContent='没有匹配成功的角色卡'; } return; }
  if (!confirm('迁移会：\n· 感官标识 → 外貌穿着\n· 情绪外露度 + 行为逻辑设定 → 性格（合并）\n· 角色卡+记忆一起迁入（记忆：摘要→chunk、recent_log→对话、facts→事实），原 V1.2 数据会备份\n\n确认开始迁移 ' + matched.length + ' 个角色吗？')) return;
  if (m) { m.className='msg warn'; m.textContent='迁移中…'; }
  try {
    const items = matched.map(x => {
      const extra = {};
      document.querySelectorAll('[data-mig]').forEach(inp => {
        const k = inp.getAttribute('data-mig');
        if (parseInt(inp.getAttribute('data-idx')) === matched.indexOf(x)) extra[k] = inp.value.trim();
      });
      if (!extra['姓名']) extra['姓名'] = x.name || '';   // 自动带识别出的真名，不用手填
      return { file: x.file, id: x.id, name: x.name, 补充: extra };
    });
    const r = await fetch('/api/migrate/apply', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({dir: dir, items: items}) });
    const o = await r.json();
    if (o.ok) {
      const c = o.cards || {ok:0,fail:0}, mm = o.memories || {ok:0,fail:0};
      const bad = (o.failedDetail || []).length > 0;
      const empties = o.emptyCards || [];
      if (m) {
        m.className = 'msg ' + (bad || empties.length ? 'warn' : 'ok');
        m.textContent = '角色卡：成功 ' + c.ok + ' / 失败 ' + c.fail
          + '；记忆：成功 ' + mm.ok + ' / 失败 ' + mm.fail
          + (bad ? ' ｜ 失败详情：' + o.failedDetail.join('；') : '')
          + (empties.length ? ' ｜ ⚠ ' + empties.length + ' 个角色迁出来是空的（源卡结构可能不认识）：' + empties.join('、') : '');
      }
      // 迁移后自动重评重要度（1.1.2 与 1.2 标准不同）
      const auto = document.getElementById('migAutoReclassify');
      const ids = o.doneIds || [];
      if (auto && auto.checked && ids.length) {
        let okN = 0, failN = 0;
        for (let i = 0; i < ids.length; i++) {
          if (m) { m.className = 'msg warn'; m.textContent = '迁移完成；正在重评重要度 ' + (i + 1) + '/' + ids.length + '…（每个角色要几秒）'; }
          try {
            const rr = await fetch('/api/memory/reclassify?npc=' + encodeURIComponent(ids[i]), { method:'POST' });
            const ro = await rr.json();
            if (ro.ok) okN++; else failN++;
          } catch (e) { failN++; }
        }
        if (m) {
          m.className = 'msg ' + (failN ? 'warn' : 'ok');
          m.textContent = '迁移完成；重要度重评完成：成功 ' + okN + ' 个' + (failN ? '，失败 ' + failN + ' 个（可去长期记忆页手动重试）' : '');
        }
      }
    } else {
      if (m) {
        m.className = 'msg bad';
        var msg = o.msg || '失败';
        if (msg.indexOf('node already has a parent') >= 0) {
          msg = '迁移其实已经执行（卡和记忆已经写进文件了），只是返回结果时程序出错。'
              + '去「角色卡」页刷新看看有没有新卡；如果还是没有，把这条消息发给作者。';
        }
        m.textContent = msg;
      }
    }
  } catch (e) { if (m) { m.className='msg bad'; m.textContent='请求失败：' + e.message; } }
}

async function loadTokens() {
  try {
    const r = await fetch('/api/tokens');
    const o = await r.json();
    if (!o.items) return;
    document.getElementById('tk-token').textContent = '约 ' + o.estTokens;
    document.getElementById('tk-all').textContent = o.totalAll;
    document.getElementById('tk-mem').textContent = o.memoryLimit || 3000;
    document.getElementById('tk-cost').textContent =
      (o.costTotal == null ? '—' : '约 ¥' + Number(o.costTotal).toFixed(4));
    const tb = document.querySelector('#tk-table tbody');
    let h = '';
    o.items.forEach(it => {
      const st = it.enabled
        ? (it.always ? '<span style=""color:var(--dim)"">常驻</span>' : '<span style=""color:var(--jade)"">开</span>')
        : '<span style=""color:var(--red)"">关</span>';
      h += '<tr><td>' + esc(it.name) + '</td><td>' + it.chars + '</td><td>' + st + '</td><td style=""color:var(--dim)"">' + esc(it.note) + '</td></tr>';
    });
    if (o.extraCall && o.extraCall.enabled) {
      const ecost = o.extraCall.local ? '本地模型（0 元）' : ('约 ¥' + Number(o.extraCall.cost).toFixed(4));
      h += '<tr><td>自动好感度（额外调用）</td><td>+' + o.extraCall.inTokens + ' tok</td>'
        + '<td><span style=""color:var(--jade)"">开</span></td>'
        + '<td style=""color:var(--dim)"">每轮额外调用，' + ecost + '</td></tr>';
    }
    tb.innerHTML = h;
  } catch (e) {}
}

// ===== 动态试演 =====
let PV_SCENE = 0;
const PV_SCENE_NAMES = ['初见（陌生）','日常闲聊','被调笑','被质疑本事','被保护/护短','被赖账/被冒犯','收到礼物','为难的要求','好感高·亲昵','玩家说伤人的话','关系里程碑','亲密场景'];
function renderPvScenes() {
  const el = document.getElementById('pvScenes');
  if (!el) return;
  let h = '';
  PV_SCENE_NAMES.forEach((n, i) => {
    h += '<span class=""chip' + (i === PV_SCENE ? ' on' : '') + '"" onclick=""pickPvScene(' + i + ')"">' + esc(n) + '</span>';
  });
  el.innerHTML = h;
}
function pickPvScene(i) {
  PV_SCENE = i;
  document.querySelectorAll('#pvScenes .chip').forEach((c, idx) => c.classList.toggle('on', idx === i));
}
async function doPreview() {
  const id = optNpc(); if (!id) { alert('先在左边选一个角色'); return; }
  const m = document.getElementById('pvMsg');
  const out = document.getElementById('pvOut');
  m.className = 'msg warn'; m.textContent = '演一段中…（十几秒）';
  out.style.display = 'none';
  try {
    const r = await fetch('/api/card/preview', { method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({
        npc: id,
        场景: PV_SCENE,
        模型: (document.getElementById('pvModel') || {}).value || '',
        NSFW: !!(document.getElementById('pvNsfw') || {}).checked,
        台词: (document.getElementById('pvExtra') || {}).value || '',
      }) });
    const o = await r.json();
    if (o.成功) {
      m.className = 'msg ok';
      m.textContent = '演完了' + (o.情绪 ? '（emotion=' + o.情绪 + '）' : '') + (o.说明 ? ' ' + o.说明 : '');
      out.style.display = 'block';
      out.textContent = o.文本 || '';
    } else {
      m.className = 'msg bad'; m.textContent = o.文本 || '试演失败';
    }
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}

// ===== NSFW 模块 =====
async function loadNsfw() {
  try {
    const r = await fetch('/api/nsfw');
    const o = await r.json();
    renderTags('nsfwA', o.keywordsA || []);
    renderTags('nsfwB', o.keywordsB || []);
    document.getElementById('nsfwArmor').value = o.armor || '';
    document.getElementById('nsfwGuifan').value = o.guifan || '';
    document.getElementById('nsfwCustom').value = o.custom || '';
  } catch (e) {}
}
function renderTags(cid, arr) {
  const box = document.getElementById(cid); if (!box) return;
  let h = '';
  (arr || []).forEach(x => {
    h += '<div class=""tag""><input type=""text"" value=""' + esc(String(x)) + '""><button class=""x"" onclick=""delTag(this)"">×</button></div>';
  });
  h += '<button class=""add"" onclick=""addTag(this)"">+ 添加</button>';
  box.innerHTML = h;
}
function collectTags(cid) {
  const box = document.getElementById(cid);
  const arr = [];
  if (box) box.querySelectorAll('.tag input').forEach(inp => { const v = (inp.value || '').trim(); if (v) arr.push(v); });
  return arr;
}
async function saveNsfw() {
  const m = document.getElementById('nsfwMsg');
  const payload = {
    keywordsA: collectTags('nsfwA'),
    keywordsB: collectTags('nsfwB'),
    armor: document.getElementById('nsfwArmor').value,
    guifan: document.getElementById('nsfwGuifan').value,
    custom: document.getElementById('nsfwCustom').value,
  };
  try {
    const r = await fetch('/api/nsfw', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(payload) });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || (o.ok ? '已保存' : '失败');
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}

// ===== 功能总开关 =====
async function loadMasterSwitches() {
  try {
    const r = await fetch('/api/config');
    const c = await r.json();
    ['memoryEnabled','worldbookEnabled','lorebookEnabled','nsfwEnabled','cardEnabled','replaceOfficialProfile'].forEach(k => {
      const el = document.getElementById('sw-' + k);
      if (el) el.checked = c[k] !== false;
    });
  } catch (e) {}
}
async function saveMasterSwitch(key, on) {
  const m = document.getElementById('swmsg-' + key);
  try {
    const r0 = await fetch('/api/config');
    const c = await r0.json();
    c[key] = on;
    const r = await fetch('/api/config', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(c) });
    const o = await r.json();
    if (m) { m.className = 'msg ' + (o.ok ? 'ok' : 'bad'); m.textContent = o.ok ? '已保存（重启生效）' : (o.msg || '失败'); }
  } catch (e) { if (m) { m.className='msg bad'; m.textContent='请求失败：' + e.message; } }
}

// ===== 导航 =====
document.querySelectorAll('#nav button').forEach(b => {
  b.onclick = () => {
    document.querySelectorAll('#nav button').forEach(x => x.classList.remove('on'));
    b.classList.add('on');
    document.querySelectorAll('.page').forEach(p => p.classList.remove('on'));
    const t = document.getElementById(b.dataset.p);
    if (t) t.classList.add('on');
    if (b.dataset.p === 'p2') loadLog();
    if (b.dataset.p === 'p14') loadWbList();
    if (b.dataset.p === 'p10') loadNsfw();
    if (b.dataset.p === 'p8') loadTokens();
    if (b.dataset.p === 'p5') loadMemLimit();
    if (b.dataset.p === 'p7') renderPvScenes();
    if (b.dataset.p === 'p16') loadAutoIntimacy();
  };
});
// 延迟到所有定义都完成后执行（避免 const 的 TDZ 报错让整个 script 挂掉）
window.addEventListener('load', function () {
  try { loadMasterSwitches(); } catch (e) { console.error('loadMasterSwitches', e); }
  try { renderPvScenes(); } catch (e) { console.error('renderPvScenes', e); }
});

// ===== 配置读写 =====
async function loadConfig() {
  const r = await fetch('/api/config');
  const c = await r.json();
  const set = (id, v) => { const e = document.getElementById(id); if (e && v !== undefined) e.value = v; };
  set('replyLenNarration', c.replyLenNarration ?? 120);
  set('replyLenDialogue', c.replyLenDialogue ?? 80);
  set('replyLenTotal', c.replyLenTotal ?? 200);
  set('replyLenLo', c.replyLenLo ?? 0);
  set('replyLenHardRatio', c.replyLenHardRatio ?? 2.0);
  set('nsfwMode', c.nsfwMode ?? 'auto');
  set('nsfwLatch', c.nsfwLatch ?? 5);
  set('extractInterval', c.extractInterval ?? 30);
  const cbAct = document.getElementById('allowNpcActing');
  if (cbAct) cbAct.checked = !!c.allowNpcActing;
  set('cardEnabled', String(c.cardEnabled ?? true));
  set('cardModel', c.cardModel ?? '');
  set('modApiUrl', c.modApiUrl ?? '');
  set('modApiKey', c.modApiKey ?? '');
  set('modModel', c.modModel ?? '');
  set('dumpEnabled', String(c.dumpEnabled ?? false));
  set('consoleAutoOpen', String(c.consoleAutoOpen ?? true));
  document.getElementById('statTotal').textContent = c.replyLenTotal ?? 200;
  document.getElementById('statNsfw').textContent = c.nsfwMode ?? 'auto';
  document.getElementById('statFallback').textContent = (c.fallbackEnabled ? '开' : '关');
  document.getElementById('statExtract').textContent = c.extractInterval ?? 30;
  calcSeconds();
  loadStatus();
}
function calcSeconds() {
  const dial = +document.getElementById('replyLenDialogue').value || 0;
  document.getElementById('dialVal').textContent = dial;
  document.getElementById('secVal').textContent = (dial / 8.7).toFixed(1);
}
async function loadStatus() {
  try {
    const r = await fetch('/api/status');
    const s = await r.json();
    document.getElementById('statusBadge').textContent = s.describe || ('端口 ' + s.port);
    const ab = document.getElementById('apiBadge');
    if (ab) {
      const a = s.api;
      if (!a) { ab.style.display = 'none'; }
      else if (!a.ok && a.legacy) {
        ab.style.display = ''; ab.className = 'badge bad';
        ab.textContent = '接口：旧本地代理';
        ab.title = a.url + '\n1.2 要直连 DeepSeek（https://api.deepseek.com/v1/chat/completions），去游戏「设置 → 自定义 AI 模型」改';
      } else if (!a.ok) {
        ab.style.display = ''; ab.className = 'badge bad';
        ab.textContent = '接口：未配置';
        ab.title = '游戏里还没填 AI 接口地址：去「设置 → 自定义 AI 模型」填地址 + key + 模型名';
      } else if (a.local) {
        ab.style.display = ''; ab.className = 'badge';
        ab.textContent = '接口：本地模型';
        ab.title = a.url + '\n本地/中转模型能用，但破甲和格式遵守率会打折（见「模型适配说明.txt」）';
      } else {
        ab.style.display = ''; ab.className = 'badge ok';
        ab.textContent = '接口：正常';
        ab.title = a.url;
      }
    }
  } catch (e) {}
}
async function saveSettings() {
  const g = id => document.getElementById(id).value;
  const body = {
    replyLenNarration: +g('replyLenNarration'),
    replyLenDialogue: +g('replyLenDialogue'),
    replyLenTotal: +g('replyLenTotal'),
    replyLenLo: +g('replyLenLo'),
    replyLenHardRatio: +g('replyLenHardRatio'),
    nsfwMode: g('nsfwMode'),
    nsfwLatch: +g('nsfwLatch'),
    extractInterval: +g('extractInterval'),
    allowNpcActing: !!(document.getElementById('allowNpcActing') || {}).checked,
    cardEnabled: g('cardEnabled') === 'true',
    cardModel: g('cardModel') || '',
    modApiUrl: g('modApiUrl') || '',
    modApiKey: g('modApiKey') || '',
    modModel: g('modModel') || '',
    dumpEnabled: g('dumpEnabled') === 'true',
    consoleAutoOpen: g('consoleAutoOpen') === 'true',
  };
  const r = await fetch('/api/config', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
  const o = await r.json();
  const m = document.getElementById('settingsMsg');
  if (o.ok) { m.className = 'msg ok'; m.textContent = '已保存，重启游戏后生效'; }
  else { m.className = 'msg bad'; m.textContent = '保存失败：' + (o.error || '未知'); }
}

// ===== 自动好感度 =====
async function loadAutoIntimacy() {
  try {
    const r = await fetch('/api/config');
    const c = await r.json();
    const sw = document.getElementById('sw-autoIntimacyEnabled');
    if (sw) sw.checked = c.autoIntimacyEnabled === true;
    const mode = document.getElementById('autoIntimacyMode');
    if (mode) mode.value = (c.autoIntimacyMode === 'sync') ? 'sync' : 'async';
    const up = document.getElementById('autoIntimacyMaxUp');
    if (up) up.value = (c.autoIntimacyMaxUp != null ? c.autoIntimacyMaxUp : 5);
    const down = document.getElementById('autoIntimacyMaxDown');
    if (down) down.value = (c.autoIntimacyMaxDown != null ? c.autoIntimacyMaxDown : 3);
    const pos = document.getElementById('autoIntimacyPositive');
    if (pos) pos.value = c.autoIntimacyPositive || '真诚关心、体贴、说到她心里、共同经历、送她喜欢的东西';
    const neg = document.getElementById('autoIntimacyNegative');
    if (neg) neg.value = c.autoIntimacyNegative || '冒犯、轻慢、欺骗、触到她的底线、无视她的感受';
    const apiUrl = document.getElementById('autoIntimacyApiUrl');
    if (apiUrl) apiUrl.value = c.autoIntimacyApiUrl || '';
    const apiKey = document.getElementById('autoIntimacyApiKey');
    if (apiKey) apiKey.value = c.autoIntimacyApiKey || '';
    const mdl = document.getElementById('autoIntimacyModel');
    if (mdl) mdl.value = c.autoIntimacyModel || '';
    loadIntimacyStats();
  } catch (e) { console.error('loadAutoIntimacy', e); }
}
async function loadIntimacyStats() {
  try {
    const r = await fetch('/api/intimacy/history');
    const o = await r.json();
    const tb = document.querySelector('#aiStatTable tbody');
    if (!tb) return;
    const npcEl = document.getElementById('aiStatNpc');
    if (npcEl) npcEl.textContent = o.npcName ? ('（' + o.npcName + '）') : '';
    let h = '';
    (o.rows || []).forEach(rw => {
      const d = rw.delta;
      const color = d > 0 ? 'var(--jade)' : (d < 0 ? 'var(--red)' : 'var(--dim)');
      h += '<tr><td style=""padding:5px 8px;border-bottom:1px solid rgba(255,255,255,.05)"">' + rw.turn + '</td>'
        + '<td style=""padding:5px 8px;border-bottom:1px solid rgba(255,255,255,.05);color:' + color + '"">' + (d > 0 ? '+' : '') + d + '</td>'
        + '<td style=""padding:5px 8px;border-bottom:1px solid rgba(255,255,255,.05)"">' + (rw.total > 0 ? '+' : '') + rw.total + '</td></tr>';
    });
    tb.innerHTML = h || '<tr><td colspan=""3"" style=""padding:8px;color:var(--dim)"">还没有判断记录（聊几轮后刷出来）</td></tr>';
  } catch (e) { console.error('loadIntimacyStats', e); }
}
async function saveAutoIntimacy() {
  const m = document.getElementById('autoIntimacyMsg');
  const body = {
    autoIntimacyMode: document.getElementById('autoIntimacyMode').value,
    autoIntimacyMaxUp: parseInt(document.getElementById('autoIntimacyMaxUp').value, 10) || 5,
    autoIntimacyMaxDown: parseInt(document.getElementById('autoIntimacyMaxDown').value, 10) || 3,
    autoIntimacyPositive: document.getElementById('autoIntimacyPositive').value,
    autoIntimacyNegative: document.getElementById('autoIntimacyNegative').value,
    autoIntimacyApiUrl: (document.getElementById('autoIntimacyApiUrl').value || '').trim(),
    autoIntimacyApiKey: (document.getElementById('autoIntimacyApiKey').value || '').trim(),
    autoIntimacyModel: (document.getElementById('autoIntimacyModel').value || '').trim(),
  };
  try {
    const r = await fetch('/api/config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const o = await r.json();
    if (m) { m.className = 'msg ' + (o.ok ? 'ok' : 'bad'); m.textContent = o.ok ? '已保存（存盘即生效）' : '保存失败'; }
  } catch (e) { if (m) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; } }
}

// ===== 长期记忆 =====
let npcLoaded = false;
let CUR_NPC = '';   // ★ 2026-10-27：当前选中的记忆角色 id（saveSkeleton/saveFacts 等用）
async function loadMemLimit() {
  try {
    const r = await fetch('/api/config');
    const c = await r.json();
    const el = document.getElementById('memLimitInput');
    if (el) el.value = c.memoryInjectLimit || 3000;
  } catch (e) {}
}
async function saveMemLimit() {
  const el = document.getElementById('memLimitInput');
  const m = document.getElementById('memLimitMsg');
  let v = parseInt((el || {}).value || '3000');
  if (isNaN(v) || v < 200) { if (m) { m.className='msg bad'; m.textContent='至少 200 字'; } return; }
  if (v > 12000) v = 12000;
  try {
    const r0 = await fetch('/api/config');
    const c = await r0.json();
    c.memoryInjectLimit = v;
    const r = await fetch('/api/config', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(c) });
    const o = await r.json();
    if (m) { m.className = 'msg ' + (o.ok ? 'ok' : 'bad'); m.textContent = o.ok ? '已保存，重启生效' : (o.msg || '失败'); }
  } catch (e) { if (m) { m.className='msg bad'; m.textContent='请求失败：' + e.message; } }
}

async function loadNpcs() {
  const r = await fetch('/api/npcs');
  const list = await r.json();
  const box = document.getElementById('npcList');
  box.innerHTML = '';
  if (!list.length) { box.innerHTML = '<div class=""hint"">还没有任何角色记忆</div>'; return; }
  for (const n of list) {
    const d = document.createElement('div');
    d.className = 'item';
    d.textContent = n.name ? (n.name + ' (' + n.id + ')') : n.id;
    d.onclick = () => {
      box.querySelectorAll('.item').forEach(x => x.classList.remove('on'));
      d.classList.add('on');
      loadMemory(n.id);
    };
    box.appendChild(d);
  }
  npcLoaded = true;
}
async function loadMemory(id) {
  CUR_NPC = id;
  const r = await fetch('/api/memory?npc=' + encodeURIComponent(id));
  const m = await r.json();
  document.getElementById('memHead').style.display = 'none';
  document.getElementById('memBody').style.display = '';
  document.getElementById('memSkeleton').innerHTML = renderSkeleton(m);
  document.getElementById('memFacts').innerHTML = renderFacts(m) || '（空）';
  document.getElementById('memChunks').innerHTML = renderChunks(m);
  loadScopes();
}

function renderSkeleton(m) {
  const rels = m.relations || [];
  let h = '<div style=""margin-bottom:10px""><b style=""color:var(--gold);font-size:13px"">关系（' + rels.length + '）</b>'
    + '<div class=""tagbox"" id=""sk-rels"">';
  for (const r of rels) {
    h += '<div class=""tag""><input type=""text"" value=""' + esc(r) + '"" style=""flex:1"">'
      + '<button class=""x"" onclick=""delRel(this)"">×</button></div>';
  }
  h += '<button class=""add"" onclick=""addRel()"">+ 新增关系</button></div></div>';
  h += '<div><b style=""color:var(--gold);font-size:13px"">处境</b>'
    + '<textarea id=""sk-situation"" style=""min-height:72px;margin-top:6px"">' + esc(m.situation || '') + '</textarea></div>';
  return h;
}
function addRel() {
  const box = document.getElementById('sk-rels');
  if (!box) return;
  const d = document.createElement('div');
  d.className = 'tag';
  d.innerHTML = '<input type=""text"" style=""flex:1"" placeholder=""例：玩家是她的徒弟"">'
    + '<button class=""x"" onclick=""delRel(this)"">×</button>';
  box.insertBefore(d, box.querySelector('.add'));
}
function delRel(btn) {
  if (!confirm('删掉这条关系后，她将不再记得你们之间有这条关系。确定？')) return;
  const t = btn.closest('.tag');
  if (t) t.remove();
}
async function saveSkeleton() {
  const id = CUR_NPC;
  if (!id) return;
  const relations = [];
  const box = document.getElementById('sk-rels');
  if (box) box.querySelectorAll('.tag input').forEach(inp => {
    const v = (inp.value || '').trim();
    if (v) relations.push(v);
  });
  const situation = ((document.getElementById('sk-situation') || {}).value || '').trim();
  const msg = document.getElementById('skelMsg');
  try {
    const r = await fetch('/api/memory/skeleton?npc=' + encodeURIComponent(id), {
      method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ relations, situation }) });
    const o = await r.json();
    if (msg) { msg.className = 'msg ' + (o.ok ? 'ok' : 'bad'); msg.textContent = o.msg || (o.ok ? '已保存' : '失败'); }
    if (o.ok) refreshMemoryPart('skeleton');
  } catch (e) { if (msg) { msg.className = 'msg bad'; msg.textContent = '请求失败：' + e.message; } }
}

function renderChunks(m) {
  const chunks = m.chunks || [];
  const summaries = chunks.filter(x => x.kind === 'Summary' || x.kind === 'summary');
  const dialogs = chunks.filter(x => x.kind !== 'Summary' && x.kind !== 'summary');
  let h = '';
  h += '<b style=""color:var(--gold);font-size:13px"">摘要段（' + summaries.length + '）</b>';
  h += '<div id=""sumBox"">';
  for (const x of summaries) {
    h += '<div class=""tag"" data-sid=""' + x.id + '""><textarea style=""min-height:60px;flex:1"">' + esc(x.text || '') + '</textarea>'
      + '<button class=""x"" onclick=""delSummary(' + x.id + ')"">×</button></div>';
  }
  h += '</div>';
  h += '<div style=""margin:8px 0""><button class=""ghost"" onclick=""saveSummaries()"">保存摘要</button><span class=""msg"" id=""sumMsg""></span></div>';
  if (dialogs.length) {
    h += '<b style=""color:var(--gold);font-size:13px"">对话原文（' + dialogs.length + '）</b>';
    const d = dialogs.map(x => '[第' + x.turn + '轮] ' + x.text).join('\n\n');
    h += '<pre>' + esc(d) + '</pre>';
  }
  return h || '（空）';
}
function delSummary(id) {
  if (!confirm('删掉这段摘要，她将不再记得这段概括。确定？')) return;
  const npc = CUR_NPC;
  if (!npc) return;
  const m = document.getElementById('sumMsg');
  fetch('/api/memory/summary?npc=' + encodeURIComponent(npc), { method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({ id: id, action: 'delete' }) })
    .then(r => r.json()).then(o => {
      if (m) { m.className = 'msg ' + (o.ok ? 'ok' : 'bad'); m.textContent = o.msg || ''; }
      if (o.ok) refreshMemoryPart('chunks');
    })
    .catch(e => { if (m) { m.className = 'msg bad'; m.textContent = '请求失败'; } });
}
async function saveSummaries() {
  const npc = CUR_NPC;
  if (!npc) return;
  const box = document.getElementById('sumBox');
  if (!box) return;
  const m = document.getElementById('sumMsg');
  let ok = 0, fail = 0;
  const items = box.querySelectorAll('.tag');
  for (const t of items) {
    const id = parseInt(t.getAttribute('data-sid'));
    const text = (t.querySelector('textarea').value || '').trim();
    if (!text) continue;
    try {
      const r = await fetch('/api/memory/summary?npc=' + encodeURIComponent(npc), { method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({ id: id, text: text, action: 'save' }) });
      const o = await r.json();
      if (o.ok) ok++; else fail++;
    } catch (e) { fail++; }
  }
  if (m) { m.className = 'msg ' + (fail ? 'warn' : 'ok'); m.textContent = '保存 ' + ok + ' 条' + (fail ? '，失败 ' + fail + ' 条' : ''); }
  refreshMemoryPart('chunks');
}

async function refreshMemoryPart(part) {
  const id = CUR_NPC;
  if (!id) return;
  try {
    const r = await fetch('/api/memory?npc=' + encodeURIComponent(id));
    const m = await r.json();
    if (part === 'skeleton') document.getElementById('memSkeleton').innerHTML = renderSkeleton(m);
    if (part === 'facts') document.getElementById('memFacts').innerHTML = renderFacts(m);
    if (part === 'chunks') document.getElementById('memChunks').innerHTML = renderChunks(m);
  } catch (e) {}
}

const FACT_CATS = [['Gift','赠物'],['Promise','承诺'],['RelationMilestone','关系里程碑'],['ImportantEvent','重要事件']];
function renderFacts(m) {
  const facts = m.facts || [];
  let h = '';
  for (const [cat, label] of FACT_CATS) {
    const list = facts.filter(x => (x.category || 'ImportantEvent') === cat)
      .sort((a, b) => (b.importance || 0) - (a.importance || 0));
    h += '<div style=""margin-bottom:12px""><b style=""color:var(--gold);font-size:13px"">' + label + '（' + list.length + '）</b>'
      + '<div class=""tagbox"" id=""fb-' + cat + '"">';
    for (const x of list) {
      h += '<div class=""tag""><input type=""text"" value=""' + esc(x.text || '') + '"" style=""flex:1"">'
        + '<input type=""number"" value=""' + (x.importance || 3) + '"" min=""1"" max=""5"" title=""重要度 1~5"" style=""width:66px;flex:none"">'
        + '<button class=""x"" onclick=""delTag(this)"">×</button></div>';
    }
    h += '<button class=""add"" onclick=""addFact(\'' + cat + '\')"">+ 添加一条</button></div></div>';
  }
  return h;
}
function addFact(cat) {
  const box = document.getElementById('fb-' + cat);
  if (!box) return;
  const d = document.createElement('div');
  d.className = 'tag';
  d.innerHTML = '<input type=""text"" style=""flex:1"" placeholder=""写一条事实，例：某某答应过某件事"">'
    + '<input type=""number"" value=""3"" min=""1"" max=""5"" title=""重要度 1~5"" style=""width:66px;flex:none"">'
    + '<button class=""x"" onclick=""delTag(this)"">×</button>';
  box.insertBefore(d, box.querySelector('.add'));
}
async function saveFacts() {
  const id = CUR_NPC;
  if (!id) return;
  const facts = [];
  for (const [cat] of FACT_CATS) {
    const box = document.getElementById('fb-' + cat);
    if (!box) continue;
    box.querySelectorAll('.tag').forEach(t => {
      const ins = t.querySelectorAll('input');
      const text = (ins[0].value || '').trim();
      if (!text) return;
      let imp = parseInt(ins[1].value) || 3;
      imp = Math.max(1, Math.min(5, imp));
      facts.push({ text: text, importance: imp, category: cat });
    });
  }
  const msg = document.getElementById('factsMsg');
  try {
    const r = await fetch('/api/memory/facts?npc=' + encodeURIComponent(id), {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ facts }) });
    const o = await r.json();
    if (msg) { msg.className = 'msg ' + (o.ok ? 'ok' : 'bad'); msg.textContent = o.msg || (o.ok ? '已保存' : '失败'); }
    if (o.ok) refreshMemoryPart('facts');
  } catch (e) { if (msg) { msg.className = 'msg bad'; msg.textContent = '请求失败：' + e.message; } }
}

// ===== 一键迁移 =====
async function doTool(kind) {
  const id = CUR_NPC;
  const m = document.getElementById('toolMsg');
  if (!id) { m.className = 'msg bad'; m.textContent = '先选一个角色'; return; }
  m.className = 'msg warn'; m.textContent = (kind === 'reclassify' ? '重评中' : (kind === 'compress' ? '压缩中' : '整理中')) + '…（可能要十几秒）';
  try {
    const r = await fetch('/api/memory/' + kind + '?npc=' + encodeURIComponent(id), { method:'POST' });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || (o.ok ? '完成' : '失败');
    if (o.ok) setTimeout(() => loadMemory(id), 800);
  } catch (e) {
    m.className = 'msg bad'; m.textContent = '请求失败：' + e.message;
  }
}

// ===== 跨存档继承记忆 =====
async function loadScopes() {
  const id = CUR_NPC;
  const sel = document.getElementById('scopeSelect');
  if (!id || !sel) return;
  try {
    const r = await fetch('/api/memory/scopes?npc=' + encodeURIComponent(id));
    const o = await r.json();
    sel.innerHTML = '<option value="""""""">— 选来源存档 —</option>';
    for (const s of (o.scopes || [])) {
      if (s.scope === o.current) continue;   // 跳过当前存档
      const opt = document.createElement('option');
      opt.value = s.scope;
      opt.textContent = s.scope + '　（' + s.lastWrite + '）';
      sel.appendChild(opt);
    }
    if (sel.options.length <= 1) {
      const opt = document.createElement('option');
      opt.value = '';
      opt.textContent = '（其他存档里没有这个角色的记忆）';
      sel.appendChild(opt);
    }
  } catch (e) {}
}
async function doImportScope() {
  const id = CUR_NPC;
  const from = document.getElementById('scopeSelect').value;
  const m = document.getElementById('scopeMsg');
  if (!id) { m.className = 'msg bad'; m.textContent = '先选角色'; return; }
  if (!from) { m.className = 'msg bad'; m.textContent = '先选来源存档'; return; }
  if (!confirm('从存档 ' + from + ' 导入这个角色的记忆吗？\n\n当前记忆会先备份。')) return;
  m.className = 'msg warn'; m.textContent = '导入中…';
  try {
    const r = await fetch('/api/memory/import?npc=' + encodeURIComponent(id) + '&from=' + encodeURIComponent(from), { method:'POST' });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || (o.ok ? '已导入' : '失败');
    if (o.ok) setTimeout(() => { loadMemory(id); loadScopes(); }, 600);
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}

// ===== 删除记忆（二次确认）=====
async function doDelete() {
  const id = CUR_NPC;
  const m = document.getElementById('toolMsg');
  if (!id) { m.className = 'msg bad'; m.textContent = '先选一个角色'; return; }
  if (!confirm('确定要删除这个角色的全部记忆吗？\n\n会先自动导出一份 zip 备份，但删除本身不可撤销。')) return;
  m.className = 'msg warn'; m.textContent = '删除中…';
  try {
    const r = await fetch('/api/memory/delete?npc=' + encodeURIComponent(id), { method:'POST' });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || (o.ok ? '已删除' : '失败');
    if (o.ok) setTimeout(() => { loadNpcs(); document.getElementById('memBody').style.display = 'none'; document.getElementById('memHead').style.display = ''; }, 800);
  } catch (e) {
    m.className = 'msg bad'; m.textContent = '请求失败：' + e.message;
  }
}

// ===== 日志 =====
let autoTimer = null;
async function loadLog() {
  try {
    const r = await fetch('/api/log?lines=200');
    const o = await r.json();
    const box = document.getElementById('logBox');
    const msg = document.getElementById('logMsg');
    if (o.ok) {
      box.textContent = (o.lines || []).join('\n');
      box.scrollTop = box.scrollHeight;
      msg.textContent = '';
    } else {
      msg.className = 'msg bad';
      msg.textContent = o.msg || '读取失败';
    }
  } catch (e) {
    document.getElementById('logMsg').className = 'msg bad';
    document.getElementById('logMsg').textContent = '请求失败：' + e.message;
  }
}
function toggleAuto() {
  const label = document.getElementById('autoLabel');
  if (autoTimer) { clearInterval(autoTimer); autoTimer = null; label.textContent = '自动刷新：关'; }
  else { autoTimer = setInterval(loadLog, 3000); label.textContent = '自动刷新：开（3秒）'; loadLog(); }
}

// ===== AI 答疑 =====
async function doAsk() {
  const q = document.getElementById('askInput').value.trim();
  const m = document.getElementById('askMsg');
  const box = document.getElementById('askAnswer');
  if (!q) { m.className = 'msg bad'; m.textContent = '先写点问题'; return; }
  m.className = 'msg warn'; m.textContent = '正在分析日志…（十几秒）';
  box.style.display = 'none';
  try {
    const r = await fetch('/api/ask', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({question:q}) });
    const o = await r.json();
    if (o.ok) {
      m.className = 'msg ok'; m.textContent = '回答：';
      box.textContent = o.answer;
      box.style.display = '';
    } else {
      m.className = 'msg bad'; m.textContent = o.msg || '失败';
    }
  } catch (e) {
    m.className = 'msg bad'; m.textContent = '请求失败：' + e.message;
  }
}

// ===== 角色卡 =====
let _allNpcs = [];
async function loadCardNpcs() {
  try {
    const r = await fetch('/api/npcs');
    _allNpcs = await r.json();
  } catch (e) { _allNpcs = []; }
  filterCardNpcs();
}
function renderCardNpcs(kw, onlyCard) {
  const sel = document.getElementById('cardNpcSelect');
  if (!sel) return;
  const prev = sel.value;
  kw = (kw || '').trim().toLowerCase();
  // 按名字拼音排序（zh 排序规则）
  const sorted = _allNpcs.slice().sort((a, b) =>
    String(a.name || a.id).localeCompare(String(b.name || b.id), 'zh-Hans-CN'));
  sel.innerHTML = '<option value="""">— 选择 —</option>';
  const filtered = sorted.filter(n => {
    if (onlyCard && !n.hasCard) return false;
    if (!kw) return true;
    return String(n.name || '').toLowerCase().includes(kw)
        || String(n.id).toLowerCase().includes(kw);
  });
  for (const n of filtered) {
    const o = document.createElement('option');
    o.value = n.id;
    o.textContent = (n.name || n.id) + (n.hasCard ? '' : '（无卡）');
    o.title = '角色代码：' + n.id + (n.hasCard ? '' : ' ｜ 没有角色卡（只有记忆/关系/世界书）');
    sel.appendChild(o);
  }
  if (prev) sel.value = prev;
}
function filterCardNpcs() {
  const v = document.getElementById('cardNpcSearch');
  const only = document.getElementById('cardOnlyHasCard');
  renderCardNpcs(v ? v.value : '', !!(only && only.checked));
}
async function renameCardFile() {
  const id = (document.getElementById('cardNpcSelect') || {}).value;
  const name = ((document.getElementById('cardRenameInput') || {}).value || '').trim();
  const m = document.getElementById('cardRenameMsg');
  if (!id) { if (m) { m.className = 'msg bad'; m.textContent = '先选一个角色'; } return; }
  if (!name) { if (m) { m.className = 'msg bad'; m.textContent = '名字不能为空'; } return; }
  if (m) { m.className = 'msg warn'; m.textContent = '改名中…'; }
  try {
    const r = await fetch('/api/card/rename', { method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({ npc: id, name: name }) });
    const o = await r.json();
    if (m) { m.className = 'msg ' + (o.ok ? 'ok' : 'bad'); m.textContent = o.msg || (o.ok ? '已改名' : '失败'); }
    if (o.ok) loadCardNpcs();
  } catch (e) { if (m) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; } }
}
async function onCardNpcChange() {
  await loadCardSlots();
  await loadCard();
  await loadLore();
}
async function loadCardSlots() {
  const id = document.getElementById('cardNpcSelect').value;
  const sel = document.getElementById('cardSlotSelect');
  if (!sel) return;
  if (!id) { sel.innerHTML = ''; return; }
  try {
    const r = await fetch('/api/card/list?npc=' + encodeURIComponent(id));
    const o = await r.json();
    sel.innerHTML = '';
    for (const c of (o.cards || [])) {
      const opt = document.createElement('option');
      opt.value = c.name;
      opt.textContent = c.label + '　（' + c.file + '）';
      if ((o.pick || '') === c.name) opt.selected = true;
      sel.appendChild(opt);
    }
    if (!sel.options.length) {
      const opt = document.createElement('option');
      opt.value = '';
      opt.textContent = '（还没有卡）';
      sel.appendChild(opt);
    }
  } catch (e) {}
}
async function pickCard() {
  const id = document.getElementById('cardNpcSelect').value;
  const name = document.getElementById('cardSlotSelect').value;
  const m = document.getElementById('cardMsg');
  if (!id) return;
  try {
    const r = await fetch('/api/card/pick?npc=' + encodeURIComponent(id) + '&name=' + encodeURIComponent(name), { method:'POST' });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || '';
    if (o.ok) await loadCard();
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}
async function loadCardSourceRef(id) {
  const box = document.getElementById('cardSourceText');
  if (!box) return;
  if (!id) { box.textContent = ''; return; }
  try {
    const r = await fetch('/api/card/source?npc=' + encodeURIComponent(id));
    const o = await r.json();
    box.textContent = o.ok ? (o.material || '（资料为空）') : (o.msg || '还没抓到资料（进游戏和这个角色聊一句就会抓）');
  } catch (e) { box.textContent = '读取失败：' + e.message; }
}

async function loadCard() {
  const id = document.getElementById('cardNpcSelect').value;
  const box = document.getElementById('cardForm');
  const m = document.getElementById('cardMsg');
  if (!box) return;
  if (!id) { box.innerHTML = '<p style=""color:var(--dim)"">先选一个角色</p>'; return; }
  const r = await fetch('/api/card?npc=' + encodeURIComponent(id));
  const o = await r.json();
  loadCardSourceRef(id);
  if (o.ok && o.fields) {
    window._cardFields = o.fields;
    renderCardForm(o.card || {}, o.fields);
    m.className = 'msg ok';
    m.textContent = o.enabled ? '已启用' : '未启用（「启用」字段为 false）';
    loadCardSlots();
  } else if (o.ok === false && o.msg) {
    // 还没有卡 → 给一份空白表单，玩家直接填
    const fr = await fetch('/api/card?npc=' + encodeURIComponent(id) + '&blank=1');
    const fo = await fr.json();
    if (fo.ok && fo.fields) {
      renderCardForm(fo.card || {}, fo.fields);
      m.className = 'msg warn';
      m.textContent = '还没有角色卡，下面是空白表单，填完点保存';
    } else {
      box.innerHTML = '<p class=""bad"">' + esc(o.msg) + '</p>';
      m.className = 'msg bad'; m.textContent = o.msg;
    }
  } else {
    box.innerHTML = '';
    m.className = 'msg bad'; m.textContent = o.msg || '读取失败';
  }
}

// ===== 按字段表渲染角色卡表单（对齐 1.1.2）=====
// ===== 通用「条目编辑器」（世界书 / 角色世界书共用）=====
function renderEntries(cid, book) {
  const c = document.getElementById(cid); if (!c) return;
  const entries = (book && book.entries) || [];
  // ★ 2026-10-28 主人要：按钮行放到【条目列表上方】，新加的条目在下方顺延
  let h = '<div style=""display:flex;gap:6px;flex-wrap:wrap;margin-bottom:10px"">'
    + '<button class=""add"" onclick=""addEntryBox(&#39;' + cid + '&#39;)"">+ 加一条</button>'
    + '<button class=""add"" onclick=""addPreset(&#39;' + cid + '&#39;, &#39;宗门&#39;)"">+ 宗门</button>'
    + '<button class=""add"" onclick=""addPreset(&#39;' + cid + '&#39;, &#39;境界&#39;)"">+ 境界</button>'
    + '<button class=""add"" onclick=""addPreset(&#39;' + cid + '&#39;, &#39;喜好&#39;)"">+ 喜好</button>'
    + '</div>';
  entries.forEach((e, i) => {
    const src = e.来源 || e.source || 'manual';
    const typ = e.类型 || '';
    const typTag = typ === '角色自身' ? '（角色自身）'
                 : (typ === '关系' ? '（与角色有关系的人）' : '');
    const srcTag = src === 'auto'
      ? '<span style=""color:var(--jade);font-size:11px"">' + typTag + '（自动抓取）</span>'
      : '<span style=""color:var(--gold);font-size:11px"">（手写，永不覆盖）</span>';
    const alwaysCb = (typ === '角色自身')
      ? ' <label style=""font-size:12px;color:var(--dim);cursor:pointer""><input type=""checkbox"" data-role=""always"" ' + (e.始终注入 ? 'checked' : '') + '> 始终注入</label>'
      : '';
    h += '<div class=""entrybox"" data-src=""' + esc(src) + '"">'
      + '<div class=""ehead""><b>条目 ' + (i + 1) + '</b> ' + srcTag + alwaysCb + '<span style=""flex:1""></span>'
      + '<button class=""x"" onclick=""this.closest(&#39;.entrybox&#39;).remove()"">× 删除</button></div>'
      + '<div class=""hint2"">命中词（对话里出现任意一个就注入；人名/地名/门派都加上，提到才会触发）：</div>'
      + '<div class=""tagbox"" data-role=""keys"">';
    (e.keys || []).forEach(k => {
      h += '<div class=""tag""><input type=""text"" value=""' + esc(String(k)) + '"">'
         + '<button class=""x"" onclick=""delTag(this)"">×</button></div>';
    });
    h += '<button class=""add"" onclick=""addTag(this)"">+ 命中词</button></div>'
      + '<div class=""hint2"">内容（命中后注入这段）：</div>'
      + '<textarea data-role=""text"" rows=""3"">' + esc(String(e.text || '')) + '</textarea>'
      + '<div class=""hint2"">优先级（数字越大越先注入）：<input type=""number"" data-role=""priority"" value=""' + (e.priority || 5) + '"" min=""1"" max=""9"" style=""width:60px""></div>'
      + '</div>';
  });
  c.innerHTML = h;
}
function addEntryBox(cid) {
  const c = document.getElementById(cid); if (!c) return;
  const n = c.querySelectorAll('.entrybox').length + 1;   // ★ 编号顺延（条目 4、5…）
  const d = document.createElement('div'); d.className = 'entrybox'; d.setAttribute('data-src', 'manual');
  d.innerHTML = '<div class=""ehead""><b>条目 ' + n + '</b> <span style=""color:var(--gold);font-size:11px"">（手写）</span><span style=""flex:1""></span>'
    + '<button class=""x"" onclick=""this.closest(&#39;.entrybox&#39;).remove()"">× 删除</button></div>'
    + '<div class=""hint2"">触发关键词：</div>'
    + '<div class=""tagbox"" data-role=""keys""><button class=""add"" onclick=""addTag(this)"">+ 关键词</button></div>'
    + '<div class=""hint2"">内容：</div><textarea data-role=""text"" rows=""3""></textarea>'
    + '<div class=""hint2"">重要度：<input type=""number"" data-role=""priority"" value=""5"" min=""1"" max=""9"" style=""width:60px""></div>';
  const addBtn = Array.from(c.children).find(x => x.classList.contains('add'));
  if (addBtn) c.insertBefore(d, addBtn); else c.appendChild(d);
}
function addPreset(cid, name) {
  const c = document.getElementById(cid); if (!c) return;
  const n = c.querySelectorAll('.entrybox').length + 1;   // ★ 编号顺延
  const d = document.createElement('div'); d.className = 'entrybox'; d.setAttribute('data-src', 'manual');
  d.innerHTML = '<div class=""ehead""><b>条目 ' + n + ' · ' + esc(name) + '</b> <span style=""color:var(--gold);font-size:11px"">（手写）</span><span style=""flex:1""></span>'
    + '<button class=""x"" onclick=""this.closest(&#39;.entrybox&#39;).remove()"">× 删除</button></div>'
    + '<div class=""hint2"">命中词（对话里出现任意一个就注入）：</div>'
    + '<div class=""tagbox"" data-role=""keys""><div class=""tag""><input type=""text"" value=""' + esc(name) + '""><button class=""x"" onclick=""delTag(this)"">×</button></div><button class=""add"" onclick=""addTag(this)"">+ 命中词</button></div>'
    + '<div class=""hint2"">内容：</div><textarea data-role=""text"" rows=""3""></textarea>'
    + '<div class=""hint2"">优先级（数字越大越先注入）：<input type=""number"" data-role=""priority"" value=""5"" min=""1"" max=""9"" style=""width:60px""></div>';
  const addBtn = Array.from(c.children).find(x => x.classList.contains('add'));
  if (addBtn) c.insertBefore(d, addBtn); else c.appendChild(d);
}

function collectEntries(cid) {
  const c = document.getElementById(cid);
  const entries = [];
  if (c) c.querySelectorAll('.entrybox').forEach(box => {
    const keys = [];
    box.querySelectorAll('.tagbox[data-role=""keys""] .tag input').forEach(inp => {
      const v = (inp.value || '').trim(); if (v) keys.push(v);
    });
    const text = (box.querySelector('textarea[data-role=""text""]') || {}).value || '';
    const p = parseInt((box.querySelector('input[data-role=""priority""]') || {}).value || '5');
    const aw = box.querySelector('input[data-role=""always""]');
    const ent = { keys, text, priority: isNaN(p) ? 5 : p, 来源: box.dataset.src || 'manual' };
    if (aw) { ent.始终注入 = !!aw.checked; ent.启用 = true; ent.类型 = '角色自身'; }
    if (text.trim() || keys.length) entries.push(ent);
  });
  return { entries };
}

function renderCardForm(card, fields) {
  let h = '';
  for (const sec of Object.keys(fields)) {
    const items = fields[sec] || [];
    h += '<div class=""sechead"">' + esc(sec) + '（' + items.length + ' 项）</div>';
    items.forEach(([k, t, hint, mark], i) => {
      const sv = card[sec] || {};
      const v = sv[k];
      h += '<div class=""row""><label>' + esc(k) + (mark ? ' 🎭' : '') + '</label>';
      if (t === 'list') {
        const arr = Array.isArray(v) ? v : [];
        h += '<div style=""flex:1""><div class=""tagbox"" data-f=""' + esc(sec + '|' + k) + '"">';
        arr.forEach(x => {
          h += '<div class=""tag""><input type=""text"" value=""' + esc(String(x)) + '"">'
             + '<button class=""x"" onclick=""delTag(this)"">×</button></div>';
        });
        h += '<button class=""add"" onclick=""addTag(this)"">+ 添加一项</button></div>';
        h += '<div class=""hint2"">' + esc(hint) + '</div></div>';
      } else {
        h += '<div style=""flex:1""><textarea data-f=""' + esc(sec + '|' + k) + '"">'
           + esc(String(v == null ? '' : v)) + '</textarea>';
        h += '<div class=""hint2"">' + esc(hint) + '</div>';
        if (k === 'AI生卡') {
          h += '<button class=""ghost"" style=""margin-top:8px"" onclick=""doAiGenCard()"">用这段生成整卡</button>';
          h += '<div class=""hint2"" style=""margin-top:4px"">★ 只会重写【口吻】和【NSFW】两块；'
             + '姓名 / 身份 / 性格 / 善恶 / 外貌这些人设字段不会被改（那些用「从游戏资料填入」或手填）。</div>';
          h += '<span class=""msg"" id=""rw-msg""></span><div id=""rw-out""></div>';
        }
        h += '</div>';
      }
      h += '</div>';
    });
  }
  document.getElementById('cardForm').innerHTML = h;
}

// ===== AI生卡：按这段重写整卡 + 逐项勾选替换 =====
let RW_ITEMS = [], RW_ADOPT = {}, RW_NEWCARD = null;
async function doAiGenCard() {
  const id = document.getElementById('cardNpcSelect').value;
  const m = document.getElementById('rw-msg');
  if (!id) return;
  const draftEl = document.querySelector('#cardForm textarea[data-f=""人设|AI生卡""]');
  const draft = (draftEl && draftEl.value || '').trim();
  if (!draft) { if (m) m.textContent = '先在「AI生卡」里写一段，描述你设想中的她'; return; }

  // 先把当前表单存进卡文件（让后端读到的 AI生卡 是最新的）
  try {
    const card = collectCardForm();
    await fetch('/api/card?npc=' + encodeURIComponent(id), { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(card) });
  } catch (e) {}

  if (m) { m.className = 'msg warn'; m.textContent = '生成中（10~30 秒）…'; }
  try {
    const r = await fetch('/api/card/rewrite?npc=' + encodeURIComponent(id), { method:'POST' });
    const o = await r.json();
    if (o.成功) {
      RW_ITEMS = o.项 || []; RW_NEWCARD = o.新卡 || {}; RW_ADOPT = {};
      RW_ITEMS.forEach(it => { RW_ADOPT[it.区块 + '·' + it.字段] = !it.手改; });
      if (m) { m.className = 'msg ok'; m.textContent = '生成完成：可更新 ' + o.可更新数 + ' 项，其中手改 ' + o.手改数 + ' 项（默认不换）'; }
      renderRewrite();
    } else {
      if (m) { m.className = 'msg bad'; m.textContent = o.消息 || '生成失败'; }
    }
  } catch (e) { if (m) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; } }
}
function renderRewrite() {
  const box = document.getElementById('rw-out'); if (!box) return;
  if (!RW_ITEMS.length) { box.innerHTML = '<p class=""ok"">没有需要改的地方。</p>'; return; }
  const esc2 = t => String(t == null ? '' : t).replace(/&/g,'&amp;').replace(/</g,'&lt;');
  let h = '<div style=""border-top:1px solid var(--line);padding:12px 0;margin-top:12px""><b>重写结果（勾选的才会替换）</b></div>';
  RW_ITEMS.forEach(it => {
    const k = it.区块 + '·' + it.字段;
    h += '<div style=""border-top:1px solid var(--line);padding:12px 0"">'
      + '<div style=""display:flex;gap:10px"">'
      + '<label style=""flex:none""><input type=""checkbox"" ' + (RW_ADOPT[k] ? 'checked' : '') + ' onchange=""RW_ADOPT[\'' + k + '\']=this.checked""></label>'
      + '<div style=""flex:1""><b>' + esc2(k) + '</b>'
      + (it.手改 ? ' <span style=""color:#e8cf9a;font-size:12px"">（你手改过，默认不换）</span>' : '')
      + '<div class=""hint2"">现在：' + esc2(it.现在) + '</div>'
      + '<div class=""hint2"">改成：</div>'
      + '<textarea data-rwedit=""' + esc2(k) + '"" rows=""2"" style=""width:100%;box-sizing:border-box;background:#1b1f27;color:var(--acc);border:1px solid #2e333f;border-radius:6px;padding:8px 10px;font:inherit;font-size:13px"">' + esc2(it.新值) + '</textarea>'
      + '</div></div></div>';
  });
  h += '<div style=""margin-top:12px""><button class=""save"" onclick=""rewriteApply()"">确认应用（只替换勾选的）</button>'
    + '<button class=""ghost"" onclick=""rewriteDiscard()"">放弃</button></div>'
    + '<p class=""hint2"">应用前自动备份。AI生卡和底线禁忌永远不会被改。</p>';
  box.innerHTML = h;
}
async function rewriteApply() {
  const id = document.getElementById('cardNpcSelect').value;
  const m = document.getElementById('rw-msg');
  const adopt = Object.keys(RW_ADOPT).filter(k => RW_ADOPT[k]);
  if (!adopt.length) { if (m) m.textContent = '没勾选任何项'; return; }
  // 把微调后的值写回新卡（区分 list / text）
  const fields = window._cardFields || {};
  document.querySelectorAll('textarea[data-rwedit]').forEach(t => {
    const k = t.dataset.rwedit;
    if (adopt.indexOf(k) < 0) return;
    const idx = k.indexOf('·');
    const sec = k.substring(0, idx), fld = k.substring(idx + 1);
    if (!RW_NEWCARD[sec]) RW_NEWCARD[sec] = {};
    const fdef = ((fields[sec] || []).find(f => f[0] === fld));
    const isList = fdef && fdef[1] === 'list';
    if (isList) {
      RW_NEWCARD[sec][fld] = t.value.split('\n').map(x => x.trim()).filter(Boolean);
    } else {
      RW_NEWCARD[sec][fld] = t.value;
    }
  });
  try {
    const r = await fetch('/api/card/rewrite/apply', { method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({ npc: id, 新卡: RW_NEWCARD, 采纳: adopt }) });
    const o = await r.json();
    if (m) { m.className = 'msg ' + (o.ok ? 'ok' : 'bad'); m.textContent = o.msg || (o.ok ? '已应用' : '失败'); }
    if (o.ok) { rewriteDiscard(); await loadCard(); }
  } catch (e) { if (m) { m.className='msg bad'; m.textContent='请求失败：' + e.message; } }
}
function rewriteDiscard() {
  RW_ITEMS = []; RW_ADOPT = {}; RW_NEWCARD = null;
  const box = document.getElementById('rw-out'); if (box) box.innerHTML = '';
  const m = document.getElementById('rw-msg'); if (m) m.textContent = '';
}

function addTag(btn) {
  const box = btn.parentElement;
  const d = document.createElement('div');
  d.className = 'tag';
  d.innerHTML = '<input type=""text""><button class=""x"" onclick=""delTag(this)"">×</button>';
  box.insertBefore(d, btn);
}
function delTag(btn) { btn.parentElement.remove(); }

// ===== 从表单收集回卡片对象 =====
function collectCardForm() {
  const card = { ""_版本"": 3 };
  document.querySelectorAll('#cardForm textarea[data-f]').forEach(el => {
    const idx = el.dataset.f.indexOf('|');
    const sec = el.dataset.f.substring(0, idx);
    const k = el.dataset.f.substring(idx + 1);
    if (!card[sec]) card[sec] = {};
    card[sec][k] = el.value;
  });
  document.querySelectorAll('#cardForm .tagbox[data-f]').forEach(box => {
    const idx = box.dataset.f.indexOf('|');
    const sec = box.dataset.f.substring(0, idx);
    const k = box.dataset.f.substring(idx + 1);
    if (!card[sec]) card[sec] = {};
    const arr = [];
    box.querySelectorAll('.tag input').forEach(inp => {
      const v = (inp.value || '').trim();
      if (v) arr.push(v);
    });
    card[sec][k] = arr;
  });
  return card;
}

// 用「服务端返回的 JSON 字符串」渲染表单（生成/草稿用，不重新拉取）
function renderCardFromJson(jsonText) {
  try {
    const card = (typeof jsonText === 'string') ? JSON.parse(jsonText) : jsonText;
    if (window._cardFields) {
      renderCardForm(card, window._cardFields);
    } else {
      // 没缓存字段表就先拉一次
      const id = document.getElementById('cardNpcSelect').value;
      if (id) {
        fetch('/api/card?npc=' + encodeURIComponent(id)).then(r => r.json()).then(o => {
          if (o.ok && o.fields) { window._cardFields = o.fields; renderCardForm(card, o.fields); }
        });
      }
    }
  } catch (e) {}
}
// ===== 角色世界书 =====
async function loadLore() {
  const id = document.getElementById('cardNpcSelect').value;
  const m = document.getElementById('loreMsg');
  const f = document.getElementById('loreFile');
  if (!id) { renderEntries('loreForm', {entries:[]}); if (f) f.textContent = ''; return; }
  try {
    const r = await fetch('/api/card/lorebook?npc=' + encodeURIComponent(id));
    const o = await r.json();
    if (o.ok) {
      try { renderEntries('loreForm', JSON.parse(o.json)); }
      catch (e) { renderEntries('loreForm', {entries:[]}); }
      m.className = 'msg ok'; m.textContent = '已载入';
      if (f) f.textContent = '文件：' + (o.file || '');
    } else {
      renderEntries('loreForm', {entries:[]});
      m.className = 'msg'; m.textContent = o.msg || '';
      if (f) f.textContent = '';
    }
  } catch (e) { m.className = 'msg bad'; m.textContent = '读取失败：' + e.message; }
}
async function genLore() {
  const id = document.getElementById('cardNpcSelect').value;
  const m = document.getElementById('loreMsg');
  if (!id) { m.className = 'msg bad'; m.textContent = '先选一个角色'; return; }
  if (!confirm('从游戏关系网重新生成角色世界书吗？\n\n会覆盖当前这份（旧文件自动备份）。')) return;
  m.className = 'msg warn'; m.textContent = '生成中…';
  try {
    const r = await fetch('/api/card/lorebook?npc=' + encodeURIComponent(id) + '&action=gen', { method:'POST' });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || '';
    if (o.ok) await loadLore();
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}
async function saveLore() {
  const id = document.getElementById('cardNpcSelect').value;
  const m = document.getElementById('loreMsg');
  if (!id) { m.className = 'msg bad'; m.textContent = '先选一个角色'; return; }
  const book = collectEntries('loreForm');
  try {
    const r = await fetch('/api/card/lorebook?npc=' + encodeURIComponent(id), { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(book) });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || '';
    if (o.ok) loadLore();
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}
async function cleanOldNames() {
  const m = document.getElementById('cardMsg');
  if (!confirm('清理「旧名残留」文件吗？\n\n· 只挪走「同一个角色、同一份文件、但文件名还是旧化身名」的那几份\n· 会放进 card\\_旧名残留_时间戳\\ 文件夹，不删数据\n· 真名那份不受影响')) return;
  if (m) { m.className = 'msg warn'; m.textContent = '清理中…'; }
  try {
    const r = await fetch('/api/card/cleanoldnames', { method:'POST' });
    const o = await r.json();
    if (m) { m.className = 'msg ' + (o.ok ? 'ok' : 'bad'); m.textContent = o.msg || (o.ok ? '完成' : '失败'); }
    if (o.ok && o.moved) { setTimeout(function(){ if (typeof loadCardNpcs === 'function') loadCardNpcs(); }, 600); }
  } catch (e) { if (m) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; } }
}
async function doRename() {
  const m = document.getElementById('cardMsg');
  if (!confirm('把所有角色名改成游戏里的真实名字吗？\n\n会改：记忆文件、角色卡文件名、关系数据。\n改前自动备份，只改能查到真名的角色。')) return;
  m.className = 'msg warn'; m.textContent = '改名中…';
  try {
    const r = await fetch('/api/rename', { method:'POST' });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || (o.ok ? '已完成' : '失败');
    if (o.ok) setTimeout(() => { loadCardNpcs(); loadNpcs(); loadCardSlots(); }, 800);
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}
async function saveAsNewCard() {
  const id = document.getElementById('cardNpcSelect').value;
  const m = document.getElementById('cardMsg');
  if (!id) { m.className = 'msg bad'; m.textContent = '先选一个角色'; return; }
  const name = prompt('给新卡起个名字（例：日常 / 亲密 / 正式）\n\n会复制当前这张卡的内容：');
  if (!name) return;
  try {
    const r = await fetch('/api/card/saveas?npc=' + encodeURIComponent(id) + '&name=' + encodeURIComponent(name), { method:'POST' });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || '';
    if (o.ok) await loadCardSlots();
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}
async function deleteCard() {
  const id = document.getElementById('cardNpcSelect').value;
  const m = document.getElementById('cardMsg');
  if (!id) { if (m) { m.className = 'msg bad'; m.textContent = '先选一个角色'; } return; }
  if (!confirm('删除这张角色卡？\n\n· 会先备份（card 目录留 .删除备份_ 文件）\n· 默认不删角色记忆、关系、世界书\n· 删了之后角色退回游戏默认表现')) return;
  const withMem = confirm('要不要把「记忆 / 关系 / 世界书 / 角色资料」也一起清掉？\n\n· 确定 = 一起清，这个角色会从下拉列表消失\n· 取消 = 只删卡，记忆留着（下拉里会标「无卡」）\n\n两种都会先挪到 data\\_已删除_时间戳\\ ，不真删，能拿回来');
  if (m) { m.className = 'msg warn'; m.textContent = '删除中…'; }
  try {
    const r = await fetch('/api/card/delete?npc=' + encodeURIComponent(id) + '&backup=true&withMemory=' + (withMem ? 'true' : 'false'), { method:'POST' });
    const o = await r.json();
    if (o.ok) { if (m) { m.className = 'msg ok'; m.textContent = o.msg || '已删除（已备份）'; } loadCardNpcs(); loadCardSlots(); }
    else { if (m) { m.className = 'msg bad'; m.textContent = o.msg || '删除失败'; } }
  } catch (e) { if (m) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; } }
}
async function deleteLore() {
  const id = document.getElementById('cardNpcSelect').value;
  const m = document.getElementById('loreMsg');
  if (!id) { if (m) { m.className = 'msg bad'; m.textContent = '先选一个角色'; } return; }
  if (!confirm('删除这个角色的世界书？\n\n· 会先备份（card 目录留 .删除备份_ 文件）\n· 删了之后角色就不记得自己的过往/习惯/秘密')) return;
  if (m) { m.className = 'msg warn'; m.textContent = '删除中…'; }
  try {
    const r = await fetch('/api/card/lorebook/delete?npc=' + encodeURIComponent(id) + '&backup=true', { method:'POST' });
    const o = await r.json();
    if (o.ok) { if (m) { m.className = 'msg ok'; m.textContent = '已删除（已备份）'; } loadLore(); }
    else { if (m) { m.className = 'msg bad'; m.textContent = o.msg || '删除失败'; } }
  } catch (e) { if (m) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; } }
}
async function fillFromSource() {
  const id = document.getElementById('cardNpcSelect').value;
  const m = document.getElementById('cardMsg');
  if (!id) { if (m) { m.className = 'msg bad'; m.textContent = '先选一个角色'; } return; }
  if (m) { m.className = 'msg warn'; m.textContent = '正在读游戏资料…'; }
  try {
    const r = await fetch('/api/card/source-fields?npc=' + encodeURIComponent(id));
    const o = await r.json();
    if (!o.ok) { if (m) { m.className = 'msg bad'; m.textContent = o.msg || '读不到资料'; } return; }
    const f = o.fields || {};
    let filled = 0;
    for (const key of Object.keys(f)) {
      const el = document.querySelector('#cardForm textarea[data-f=""' + key + '""]')
              || document.querySelector('#cardForm input[data-f=""' + key + '""]');
      if (!el) continue;
      if ((el.value || '').trim()) continue;   // 已经有内容的不覆盖，避免冲掉手写
      el.value = f[key];
      filled++;
    }
    if (m) {
      m.className = filled ? 'msg ok' : 'msg warn';
      m.textContent = filled
        ? ('已从游戏资料填入 ' + filled + ' 个空字段（已有内容不覆盖），检查后点「保存」')
        : ('这些字段本来就有内容，没覆盖。' + (o.background ? '背景已写进角色世界书「角色自身」。' : ''));
    }
  } catch (e) { if (m) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; } }
}
async function saveCard() {
  const id = document.getElementById('cardNpcSelect').value;
  const m = document.getElementById('cardMsg');
  if (!id) { m.className='msg bad'; m.textContent='先选一个角色'; return; }
  const card = collectCardForm();
  m.className = 'msg warn'; m.textContent = '保存中…';
  try {
    const r = await fetch('/api/card?npc=' + encodeURIComponent(id), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(card),
    });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || (o.ok ? '已保存' : '保存失败');
  } catch (e) { m.className='msg bad'; m.textContent='请求失败：' + e.message; }
}

// ===== 角色卡生成 =====
async function loadGenNpcs() {
  const r = await fetch('/api/npcs');
  const list = await r.json();
  const sel = document.getElementById('genNpcSelect');
  if (!sel) return;
  sel.innerHTML = '<option value="""""""">— 选择 —</option>';
  for (const n of list) {
    const o = document.createElement('option');
    o.value = n.id;
    o.textContent = n.name || n.id;
    o.title = '角色代码：' + n.id;
    sel.appendChild(o);
  }
}
async function loadCardSource() {
  const id = document.getElementById('genNpcSelect').value;
  const box = document.getElementById('genMaterial');
  const m = document.getElementById('genMsg');
  if (!id) { box.value = ''; return; }
  try {
    const r = await fetch('/api/card/source?npc=' + encodeURIComponent(id));
    const o = await r.json();
    if (o.ok) { box.value = o.material; m.className = 'msg ok'; m.textContent = '已自动抓取（' + o.material.length + ' 字）'; }
    else { m.className = 'msg'; m.textContent = o.msg || '还没抓到资料'; }
  } catch (e) { m.className = 'msg bad'; m.textContent = '读取失败：' + e.message; }
}
async function doGenerate() {
  const npc = document.getElementById('genNpcSelect').value;
  const material = document.getElementById('genMaterial').value.trim();
  const m = document.getElementById('genMsg');
  if (!material) { m.className = 'msg bad'; m.textContent = '先粘贴角色资料'; return; }
  m.className = 'msg warn'; m.textContent = '生成中…（十几秒）';
  try {
    const r = await fetch('/api/card/generate?npc=' + encodeURIComponent(npc), {
      method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({material:material})
    });
    const o = await r.json();
    if (o.ok) {
      m.className = 'msg ok'; m.textContent = '已生成，已填到「角色卡编辑」页，确认后点保存';
      // 切到编辑页并填入
      document.querySelectorAll('#nav button').forEach(x => x.classList.remove('on'));
      const btn = document.querySelector('#nav button[data-p=""p4""]');
      if (btn) btn.classList.add('on');
      document.querySelectorAll('.page').forEach(p => p.classList.remove('on'));
      document.getElementById('p4').classList.add('on');
      if (npc) { document.getElementById('cardNpcSelect').value = npc; }
      renderCardFromJson(o.json);
    } else {
      m.className = 'msg bad'; m.textContent = o.msg || '生成失败';
    }
  } catch (e) {
    m.className = 'msg bad'; m.textContent = '请求失败：' + e.message;
  }
}

// ===== 世界书（F2）=====
let wbLoaded = false;
async function loadWbList() {
  try {
    const r = await fetch('/api/worldbook/list');
    const o = await r.json();
    const sel = document.getElementById('wbSelect');
    if (!sel) return;
    const keep = sel.value;   // ★ 2026-10-28：记住当前选中，刷新后选回来（保存后不再跳成「选择」）
    sel.innerHTML = '<option value="""""""">— 选择 —</option>';
    for (const b of (o.books || [])) {
      const opt = document.createElement('option');
      opt.value = b;
      opt.textContent = b;
      sel.appendChild(opt);
    }
    if (keep && Array.from(sel.options).some(x => x.value === keep)) sel.value = keep;
    const d = document.getElementById('wbDir');
    if (d) d.textContent = '目录：' + (o.dir || '');
    wbLoaded = true;
  } catch (e) {}
}
async function loadWb() {
  const name = document.getElementById('wbSelect').value;
  const m = document.getElementById('wbMsg');
  if (!name) { renderEntries('wbForm', {entries:[]}); return; }
  try {
    const r = await fetch('/api/worldbook?name=' + encodeURIComponent(name));
    const o = await r.json();
    if (o.ok) { try { renderEntries('wbForm', JSON.parse(o.json)); } catch(e){ renderEntries('wbForm', {entries:[]}); } m.className = 'msg ok'; m.textContent = '已载入'; }
    else { renderEntries('wbForm', {entries:[]}); m.className = 'msg'; m.textContent = o.msg || ''; }
  } catch (e) { m.className = 'msg bad'; m.textContent = '读取失败：' + e.message; }
}
function newWb() {
  const name = prompt('世界书名字（例：某宗门 / 某地区 / 修炼体系）：');
  if (!name) return;
  renderEntries('wbForm', { entries: [{ keys: [name], text: '（在这里写 ' + name + ' 的设定）', priority: 5 }] });
  const sel = document.getElementById('wbSelect');
  const opt = document.createElement('option');
  opt.value = name; opt.textContent = name; opt.selected = true;
  sel.appendChild(opt);
  document.getElementById('wbMsg').className = 'msg warn';
  document.getElementById('wbMsg').textContent = '新世界书模板已填入，改完点保存';
}
async function inferWb() {
  const m = document.getElementById('wbMsg');
  if (!confirm('从现有角色资料推断世界书吗？\n\n会消耗 API 额度，结果只是草稿，要自己过一遍再保存。')) return;
  m.className = 'msg warn'; m.textContent = '推断中…（十几秒）';
  try {
    const r = await fetch('/api/worldbook/infer', { method:'POST' });
    const o = await r.json();
    if (o.ok) {
      m.className = 'msg ok'; m.textContent = o.msg || '已推断';
      // 填到一个新名字里
      const name = prompt('推断完成。给这份世界书起个名字（保存后才生效）：', '世界书');
      if (name) {
        const sel = document.getElementById('wbSelect');
        let opt = Array.from(sel.options).find(x => x.value === name);
        if (!opt) { opt = document.createElement('option'); opt.value = name; opt.textContent = name; sel.appendChild(opt); }
        sel.value = name;
        try { renderEntries('wbForm', JSON.parse(o.json)); } catch(e) { renderEntries('wbForm', {entries:[]}); }
        m.className = 'msg warn'; m.textContent = '已填入草稿，确认后点「保存」';
      }
    } else {
      m.className = 'msg bad'; m.textContent = o.msg || '推断失败';
    }
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}
async function saveWb() {
  const name = document.getElementById('wbSelect').value;
  const m = document.getElementById('wbMsg');
  if (!name) { m.className = 'msg bad'; m.textContent = '先选或新建一本世界书'; return; }
  const book = collectEntries('wbForm');
  book.name = name;
  try {
    const r = await fetch('/api/worldbook?name=' + encodeURIComponent(name), { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(book) });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || '';
    if (o.ok) loadWbList();
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}
async function openFolder(type) {
  try {
    const r = await fetch('/api/openfolder?type=' + encodeURIComponent(type), { method:'POST' });
    const o = await r.json();
    const m = document.getElementById(type === 'worldbook' ? 'wbMsg' : 'cardMsg');
    if (m) { m.className = 'msg ' + (o.ok ? 'ok' : 'bad'); m.textContent = o.msg || ''; }
  } catch (e) {
    const m = document.getElementById(type === 'worldbook' ? 'wbMsg' : 'cardMsg');
    if (m) { m.className = 'msg bad'; m.textContent = '打开失败：' + e.message; }
  }
}
async function cleanBackup(type, id, label) {
  if (!confirm('删除「' + label + '」的所有备份文件吗？\n\n只删 .bak_ / .删除备份_ / .关系变更备份_ 这类备份，正式文件不动。')) return;
  try {
    const r = await fetch('/api/cleanbackup?type=' + encodeURIComponent(type) + '&id=' + encodeURIComponent(id || ''), { method:'POST' });
    const o = await r.json();
    alert(o.msg || (o.ok ? '已删除' : '删除失败'));
  } catch (e) { alert('请求失败：' + e.message); }
}
async function parseWb() {
  const raw = (document.getElementById('wbRaw') || {}).value || '';
  const m = document.getElementById('wbMsg');
  if (!raw.trim()) { if (m) m.textContent = '先在上面写设定'; return; }
  try {
    const r = await fetch('/api/worldbook?action=parse', { method:'POST', body: raw });
    const o = await r.json();
    renderEntries('wbForm', o);
    if (m) { m.className = 'msg warn'; m.textContent = '已解析成 ' + (o.entries || []).length + ' 条，检查后点保存'; }
  } catch (e) { if (m) { m.className='msg bad'; m.textContent='解析失败：' + e.message; } }
}

async function deleteWb() {
  const name = document.getElementById('wbSelect').value;
  const m = document.getElementById('wbMsg');
  if (!name) { m.className = 'msg bad'; m.textContent = '先选一本'; return; }
  if (!confirm('删除世界书「' + name + '」吗？\n\n原件会自动备份。')) return;
  try {
    const r = await fetch('/api/worldbook?name=' + encodeURIComponent(name) + '&action=delete', { method:'POST' });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || '';
    if (o.ok) { renderEntries('wbForm', {entries:[]}); loadWbList(); }
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}

// ===== 存档（F1）=====
async function loadSaves() {
  try {
    const r = await fetch('/api/saves');
    const o = await r.json();
    const info = document.getElementById('saveInfo');
    const sel = document.getElementById('saveSelect');
    info.innerHTML = '当前：<b>' + (o.current || '（未检测到，用全局目录）') + '</b>　' +
                     (o.manual ? '<span class=""warn"">手动指定</span>' : '<span class=""ok"">自动判定</span>') +
                     '　共 ' + (o.slots || []).length + ' 个存档';
    sel.innerHTML = '<option value="""""""">— 不切换 —</option>';
    for (const s of (o.slots || [])) {
      const opt = document.createElement('option');
      opt.value = s.id;
      opt.textContent = s.id + '　（' + s.lastWrite + '）';
      if (s.id === o.current) opt.selected = true;
      sel.appendChild(opt);
    }
  } catch (e) {
    const info = document.getElementById('saveInfo');
    if (info) info.textContent = '读取失败：' + e.message;
  }
}
async function switchSave() {
  const id = document.getElementById('saveSelect').value;
  const m = document.getElementById('saveMsg');
  if (!id) return;
  if (!confirm('切换到这个存档吗？\n\n记忆和角色卡会切到该存档的目录，重启游戏后生效。')) return;
  const r = await fetch('/api/saves?id=' + encodeURIComponent(id), { method:'POST' });
  const o = await r.json();
  m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
  m.textContent = o.msg || (o.ok ? '已切换' : '失败');
  if (o.ok) loadSaves();
loadWbList();
}
async function resetSave() {
  const m = document.getElementById('saveMsg');
  const r = await fetch('/api/saves', { method:'POST' });
  const o = await r.json();
  m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
  m.textContent = o.msg || '';
  loadSaves();
}

// ===== 角色卡检查 / 重写 / 草稿 =====
function optNpc() {
  const id = document.getElementById('genNpcSelect').value;
  const m = document.getElementById('optMsg');
  if (!id) { m.className = 'msg bad'; m.textContent = '先去上面选一个角色'; return null; }
  return id;
}
let CHECK_NPC = null;
async function doCheck() {
  const id = optNpc(); if (!id) return;
  CHECK_NPC = id;
  const m = document.getElementById('optMsg');
  const box = document.getElementById('checkResult');
  m.className = 'msg warn'; m.textContent = '检查中…（可能要十几秒）';
  box.innerHTML = '';
  try {
    const r = await fetch('/api/card/check?npc=' + encodeURIComponent(id) + '&mode=' + ((document.getElementById('optCheckMode') || {}).value || '1'), { method:'POST' });
    const o = await r.json();
    if (!o.ok) { m.className = 'msg bad'; m.textContent = o.msg || '检查失败'; return; }
    m.className = 'msg ok'; m.textContent = o.msg;
    const bad = (o.items || []).filter(x => x.verdict !== 'ok');
    if (!bad.length) { box.innerHTML = '<p class=""hint"">全部字段都可执行，没问题。</p>'; return; }
    let h = '<div style=""margin-top:12px"">';
    for (const it of bad) {
      const v = it.verdict === 'empty' ? '<span class=""bad"">没填</span>' : '<span class=""warn"">含糊</span>';
      h += '<div class=""entrybox"">'
        + '<div style=""display:flex;gap:10px"">'
        + '<label style=""flex:none""><input type=""checkbox"" checked data-chk=""' + esc(it.section + '|' + it.field) + '""></label>'
        + '<div style=""flex:1""><b>' + esc(it.section) + '·' + esc(it.field) + '</b> ' + v
        + '<div class=""hint2"">原因：' + esc(it.reason) + '</div>'
        + '<div class=""hint2"">改成（可编辑）：</div>'
        + '<textarea data-chkval=""' + esc(it.section + '|' + it.field) + '"" rows=""2"" style=""width:100%;box-sizing:border-box;background:#1b1f27;color:var(--acc);border:1px solid #2e333f;border-radius:6px;padding:8px 10px;font:inherit;font-size:13px"">' + esc(it.suggestion || '') + '</textarea>'
        + '</div></div></div>';
    }
    h += '<button class=""save"" onclick=""applyCheck()"">采纳勾选项</button></div>';
    box.innerHTML = h;
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}
async function applyCheck() {
  const id = CHECK_NPC; if (!id) return;
  const m = document.getElementById('optMsg');
  const adopted = [];
  document.querySelectorAll('input[data-chk]').forEach(cb => {
    if (!cb.checked) return;
    const k = cb.dataset.chk;
    const ta = document.querySelector('textarea[data-chkval=""' + k + '""]');
    const idx = k.indexOf('|');
    adopted.push({ 区块: k.substring(0, idx), 字段: k.substring(idx + 1), 新值: ta ? ta.value : '' });
  });
  if (!adopted.length) { m.textContent = '没勾选任何项'; return; }
  try {
    const r = await fetch('/api/card/check/apply', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ npc: id, 采纳: adopted, backup: !!(document.getElementById('optBackup') || {}).checked }) });
    const o = await r.json();
    m.className = 'msg ' + (o.ok ? 'ok' : 'bad');
    m.textContent = o.msg || '';
    if (o.ok) { document.getElementById('checkResult').innerHTML = '<p class=""hint"">已应用，可重新检查或去编辑页确认。</p>'; }
  } catch (e) { m.className = 'msg bad'; m.textContent = '请求失败：' + e.message; }
}
async function loadDraft() {
  const id = optNpc(); if (!id) return;
  const m = document.getElementById('optMsg');
  const r = await fetch('/api/card/draft?npc=' + encodeURIComponent(id));
  const o = await r.json();
  if (o.ok) {
    m.className = 'msg ok'; m.textContent = '已载入草稿到「角色卡编辑」页，确认后点「确认应用」';
    document.getElementById('cardNpcSelect').value = id;
    renderCardFromJson(o.json);
  } else { m.className = 'msg'; m.textContent = o.msg || '没有待应用的优化'; }
}
async function applyDraft() {
  const id = optNpc(); if (!id) return;
  const m = document.getElementById('optMsg');
  if (!confirm('确认应用优化吗？当前正式卡会自动备份。')) return;
  const r = await fetch('/api/card/draft?npc=' + encodeURIComponent(id) + '&action=apply', { method:'POST' });
  const o = await r.json();
  m.className = 'msg ' + (o.ok ? 'ok' : 'bad'); m.textContent = o.msg;
}
async function discardDraft() {
  const id = optNpc(); if (!id) return;
  const m = document.getElementById('optMsg');
  if (!confirm('确定放弃这次优化吗？草稿会被删掉。')) return;
  const r = await fetch('/api/card/draft?npc=' + encodeURIComponent(id) + '&action=discard', { method:'POST' });
  const o = await r.json();
  m.className = 'msg ' + (o.ok ? 'ok' : 'bad'); m.textContent = o.msg;
}
function esc(s) {
  return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
// 放进 HTML 属性时还要转义双引号，否则名字里带引号会把属性截断
function attrEsc(s) {
  return esc(s).replace(/""/g, '&quot;');
}

// ===== 窗口尺寸记忆（--app 模式不会自动记，自己存）=====
let resizeTimer;
function reportSize() {
  const w = window.outerWidth, h = window.outerHeight;
  if (w >= 400 && h >= 300) {
    try { fetch('/api/windowsize', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({w:w,h:h}) }); } catch(e){}
  }
}
window.addEventListener('resize', function(){ clearTimeout(resizeTimer); resizeTimer = setTimeout(reportSize, 800); });
window.addEventListener('beforeunload', reportSize);

// ===== 初始化 =====
loadConfig();
loadNpcs();
loadSaves();
loadCardNpcs();
loadGenNpcs();
loadMemLimit();
</script>
</body>
</html>";
    }

    // ==================== 工具 ====================

    private static void TryWrite(HttpListenerContext ctx, int code, string contentType, string body)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = contentType;
            // ★ 2026-10-25：所有响应统一不缓存。
            //   不然玩家在游戏里聊完，控制台刷新还是旧数据（名字、世界书都不更新）。
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers["Pragma"] = "no-cache";
            ctx.Response.Headers["Expires"] = "0";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch { }
    }

    private static string JsonEscape(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    internal static void Log(string msg)
    {
        try { BepInEx.Logging.Logger.CreateLogSource("不问凡尘").LogInfo("[控制台] " + msg); } catch { }
    }
}
