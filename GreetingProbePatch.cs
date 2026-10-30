using System.Reflection;
using HarmonyLib;

namespace BuWenFanChen;

/// <summary>
/// 记录对话面板当前 NPC（供开场白过滤判断用）。
/// SetDataInternal(TbNpcBaseCfgId npcId, bool forceInteractRefresh) 是进入对话的必经点。
/// </summary>
[HarmonyPatch(typeof(Game.NpcDialoguePanel), "SetDataInternal")]
internal static class CurrentNpcTracker
{
    public static string CurrentCfgId = "";

    [HarmonyPrefix]
    internal static void Prefix(object npcId)
    {
        try
        {
            CurrentCfgId = ReadCfgId(npcId);
            BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                .LogInfo($"[开场白] 进入对话 → CurrentCfgId={CurrentCfgId}");
        }
        catch (Exception e)
        {
            BepInEx.Logging.Logger.CreateLogSource("不问凡尘")
                .LogWarning($"[开场白] SetDataInternal 异常: {e.GetType().Name}: {e.Message}");
        }
    }

    private static string ReadCfgId(object cfgId)
    {
        if (cfgId == null) return "";
        try
        {
            var v = cfgId.GetType().GetProperty("Value",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cfgId);
            if (v != null) return v.ToString();
        }
        catch { }
        return cfgId.ToString() ?? "";
    }
}
