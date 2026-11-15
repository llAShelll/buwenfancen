# 不问凡尘 · AI 角色记忆与人格增强（V1.4.0 插件源码）

《不问凡尘》（WorldApart）的 AI 对话增强 MOD —— **C# + BepInEx 6（IL2CPP）插件**。
1.2 起完全重写，取代 1.1.2 的「Python + Caddy 代理」方案：双击游戏即用，不占端口、无双重注入。

Steam 创意工坊：`3821078180`（物品 ID）

---

## 这个仓库是什么

**只有插件源码**。不含：

```
✗ 游戏本体 / 游戏 DLL / BepInEx 运行时
✗ BGE 语义模型（bge-small-zh-v1.5，约 23 MB）
✗ 玩家数据（角色卡 / 记忆 / 关系 / 世界书）
✗ 发布包（zip / 诊断工具）
```

## 功能概览（V1.4.0）

- **性格面板（1.4 新增）**：游戏内 IMGUI 面板，8 数值（温度/态度/主动/话量 + 服从/羞耻/开放/崩坏）随对话缓慢变化；
  判定走主请求内嵌 `"p"` 数组（常态零额外调用）；16 原型模板 / 锁定 / 本地或 API 重新推断 / 缩放 / 解锁列表；
  养成模式下 NSFW 玩法按数值组装；好感软约束；默认开启。
- **长期记忆**：三层设计 —— ①原文层（chunk 库）②摘要层（每 30~48 轮压摘要）③骨架层（每 30 轮一代 ≤30 字，保留 10 代，每轮必注入）；
  另有 fact 库（事实抽取，1~5 级重要度）。**RAG 检索**：BGE-small-zh 向量（ONNX 本地）+ BM25 关键词混合排序，BGE 缺失自动降级 BM25。
- **角色卡**：人设字段 + 口吻 + 底线 + NSFW + 自定义设定（补充背景）；生成 / 优化 / 编辑 / 导入导出。
- **角色世界书**：关键词触发；可从游戏原文推断生成；年龄 = 配置表初始年龄 + 游戏已过年数。
- **游戏世界书**：宗门 / 地点 / 势力补充。
- **去 AI 味**：去套路 7 条（结论说完就停 / 别打分 / 别像答辩 / 别留白腔 / 长度别匀 / 台词口语化）+ AI 腔密度探针（只统计不改正文）。
- **自动好感度 / 四选一预选项**：都走主请求，不额外发请求。
- **选项框可拖动**（位置持久化）；**男角色面板开关**；**过审模式**（NSFW 全隐藏）。
- **控制台**：13+ 页，`http://127.0.0.1:8848`，配置存盘即生效；长期记忆页支持搜索。

## 目录结构

```
Plugin.cs                    BepInEx 入口（配置加载 / Harmony 装配）
PromptConfig.cs              配置读写（config.json）
CompleteBudgetedPatch.cs     请求侧 hook：prefix / memory / tail 注入、提示词装配
AddNpcChatMessagePatch.cs    响应侧 hook：字数切分、台本同步、面板 p 解析
ConsoleServer.cs             控制台（HTTP 服务 + 内嵌 HTML/JS）
NsfwConfig.cs                情事用语规范（存盘即生效）
IntimacyJudge.cs             自动好感度
InputFieldBridge.cs          输入框矩形探测（按钮定位用）
OptionsHud.cs                选项框增强（可拖动）
UiProbe.cs                   UI 探针
Panel/                       性格面板（存储 / 渲染 / 原型 / 初值 / IMGUI HUD / 看门狗）
Card/                        角色卡（生成 / 渲染 / 校验 / 迁移）
Memory/                      记忆（抽取 / 检索 / 向量 / 世界书 / 关系 / 改名工具）
History/                     历史接管（canon 对齐 / tail 重排缓存）
Diagnostics/                 诊断探针（体检 / 请求体落盘 / 请求估算 / 预算闸门）
Output/                      输出侧容错（JSON 修复 / 切段 / 台本重建 / AI 腔探针）
Assets/npc-id-name.csv       官方 NPC 真名表（1685 条，编译进 DLL）
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
