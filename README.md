# 不问凡尘 · AI 角色记忆与人格增强（V1.2 插件源码）

《不问凡尘》（WorldApart）的 AI 对话增强 MOD —— **C# + BepInEx 6（IL2CPP）插件**，
1.2 起完全重写，取代 1.1.2 的「Python + Caddy 代理」方案：双击游戏即用，不占端口、无双重注入。

Steam 创意工坊：`3821078180`（物品 ID）

---

## 这个仓库是什么

**只有插件源码**（约 45 个文件、0.9 MB）。不含：

```
✗ 游戏本体 / 游戏 DLL / BepInEx 运行时
✗ BGE 语义模型（bge-small-zh-v1.5，约 23 MB）
✗ 玩家数据（角色卡 / 记忆 / 关系 / 世界书）
✗ 发布包（zip / 诊断工具）
```

## 功能概览

- **长期记忆**：每轮对话即时入库（chunk + BGE 向量），摘要 / 事实 / 骨架四层；BM25 + BGE + RRF 融合检索
- **角色卡**：人设字段（姓名 / 身份 / 性格 / 善恶立场 / 外貌穿着 / 说话习惯）+ 口吻 + 底线 + NSFW
- **角色世界书**：关键词触发；可从游戏原文推断生成
- **完全替换游戏设定**：卡 6 项 + 世界书「角色自身」齐全时，删掉游戏原文那段提示词（临时拼的，不动存档）
- **控制台**：13 页，`http://127.0.0.1:8848`，配置存盘即生效
- **1.1.2 数据迁移**：角色卡 + 记忆一键迁入
- **一键改真名**：按内置官方真名表（`npcId → 真名`）统一卡名 / 世界书 / 资料里的名字

## 目录结构

```
Plugin.cs                    BepInEx 入口（配置加载 / Harmony 装配）
PromptConfig.cs              配置读写（config.json）
CompleteBudgetedPatch.cs     请求侧 hook：prefix / memory / tail 注入、提示词装配
AddNpcChatMessagePatch.cs    响应侧 hook：字数切分、台本同步
ConsoleServer.cs             控制台（HTTP 服务 + 内嵌 HTML/JS）
NsfwConfig.cs                情事用语规范（存盘即生效）
IntimacyJudge.cs             自动好感度（独立 API 调用）
Card/                        角色卡（生成 / 渲染 / 校验 / 迁移）
Memory/                      记忆（抽取 / 检索 / 向量 / 世界书 / 关系 / 改名工具）
Output/                      输出侧容错（JSON 修复 / 切段 / 台本重建）
Assets/npc-id-name.csv       官方 NPC 真名表（1591 条，编译进 DLL）
```

## 构建

需要 .NET 6 SDK 与一份**已装 BepInEx 的游戏目录**（提供 `BepInEx.Core`、`Il2CppInterop`、`Game.dll` 等引用）。

```powershell
# 修改 BuWenFanChen.csproj 里的 GameDir / BgeSource 两个路径
dotnet build -c Release
```

`GameDir` 指向游戏根目录，`BgeSource` 指向 BGE 模型目录（`bge-small-zh-v1.5`）。
构建后会尝试把 DLL + 依赖 + 模型拷到 `plugins\BuWenFanChen\`，拷不动（游戏在跑）不影响产物。

## 说明

- 与游戏官方无关，纯第三方增强插件。
- 不修改游戏存档；所有数据写在 `%LOCALAPPDATA%Low\Nuverse\WorldApart\BuWenFanChen\`。
- 问题反馈 / 交流：QQ 群 `338115203`
