namespace BuWenFanChen.Panel;

using BepInEx.Unity.IL2CPP;
using UnityEngine;

/// <summary>
/// ★ 2026-11-14：面板看门狗。
/// 游戏按 F5 / 重建 UI 时，PanelHud 的 OnGUI 宿主对象可能被销毁，之后没人再调 OnGUI，
/// 面板就永久消失（日志里连「绘制中」都不打）。这里每秒检查一次，_instance 没了就重建。
/// （_visible / _rect 等状态都是静态字段，重建后面板会自己恢复显示）
/// </summary>
public class PanelWatchdog : MonoBehaviour
{
    private static PanelWatchdog _instance;
    private static float _next;

    private void Awake() { _instance = this; }

    private void Update()
    {
        try
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            PanelHud.EnsureAlive();
        }
        catch { }
    }

    public static void EnsureInstalled()
    {
        try
        {
            if (_instance != null) return;
            // ★ 2026-11-14：挂到 BepInEx 常驻组件容器上（游戏 F5 重建 UI 不会清掉 Chainloader 的根对象）
            IL2CPPChainloader.AddUnityComponent<PanelWatchdog>();
        }
        catch { }
    }
}
