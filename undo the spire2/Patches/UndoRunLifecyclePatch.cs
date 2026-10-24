// 文件说明：在战斗开始和结束时初始化或清理 undo 状态。
using HarmonyLib;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

[HarmonyPatch]
public static class UndoRunLifecyclePatch
{
    [HarmonyPatch]
    private static class EndCombatPatch
    {
        private static MethodBase TargetMethod()
        {
            return UndoCombatTurnRuntime.TurnMethod("EndCombatInternal");
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            UndoDelayedCombatRewardService.FlushPendingRewards("combat_ended");
            MainFile.Controller.ClearHistory("combat ended");
        }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    [HarmonyPostfix]
    public static void RunCleanupPostfix()
    {
        MainFile.Controller.ClearHistory("run cleaned up");
    }
}
