using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;

namespace UndoTheSpire2;

internal static class UndoCombatTurnRuntime
{
    internal static readonly Type TurnStateType =
        typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.Combat.CombatTurnState", throwOnError: true)!;

    private static readonly FieldInfo TurnStateField = RequiredField("_turnState");
    private static readonly FieldInfo TurnLoopTaskField = RequiredField("_turnLoopTask");
    private static readonly FieldInfo EffectDepthField = RequiredField("_cardOrPotionEffectDepth");
    private static RestoredTurn? _restoredTurn;

    private sealed class RestoredTurn(object state)
    {
        public object State { get; } = state;
        public bool SkipPhaseOne { get; set; }
        public TaskCompletionSource Finished { get; } = new();
    }

    internal static MethodInfo TurnMethod(string name) =>
        AccessTools.DeclaredMethod(typeof(CombatManager), name, [TurnStateType])
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, name);

    public static Task? CancelCurrentTurnLoop()
    {
        CombatManager manager = CombatManager.Instance;
        Task? previousLoop = (Task?)TurnLoopTaskField.GetValue(manager);
        if (TurnStateField.GetValue(manager) is { } state)
            AccessTools.DeclaredMethod(TurnStateType, "Cancel").Invoke(state, null);
        return previousLoop;
    }

    public static async Task PrepareForRestoreAsync(CombatState combatState, Task? previousLoop)
    {
        if (previousLoop != null)
        {
            try
            {
                await previousLoop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (previousLoop.IsCanceled)
            {
                // Cancellation belongs to the discarded turn, not the restored combat.
            }
        }

        object state = Activator.CreateInstance(TurnStateType, [combatState])
            ?? throw new InvalidOperationException("Could not create restored combat turn state.");
        Set(state, "IsInProgress", true);
        Set(state, "IsStarting", false);
        Set(state, "EndTurnSignalSource", Activator.CreateInstance(Property("EndTurnSignalSource").PropertyType));
        Set(state, "BeginEnemyTurnSignalSource", new TaskCompletionSource<Func<Task>?>());
        TurnStateField.SetValue(CombatManager.Instance, state);
        ((Dictionary<Player, int>)EffectDepthField.GetValue(CombatManager.Instance)!).Clear();
        _restoredTurn = new RestoredTurn(state);
    }

    public static void StartRestoredTurnLoop(CombatState combatState)
    {
        RestoredTurn turn = GetRestoredTurn();
        Task loop = RunRestoredTurnLoopAsync(turn, combatState);
        TurnLoopTaskField.SetValue(CombatManager.Instance, loop);
        _ = TaskHelper.RunSafely(loop);
    }

    public static async Task ResumeAfterRetainSelectionAsync(Player player)
    {
        RestoredTurn turn = GetRestoredTurn();
        if (turn.Finished.Task.IsCompleted)
            throw new InvalidOperationException("The restored retain-choice turn has already finished.");

        turn.SkipPhaseOne = true;
        CombatManager.Instance.SetReadyToEndTurn(player, canBackOut: false);
        CombatManager.Instance.SetReadyToBeginEnemyTurn(player);
        await turn.Finished.Task;
    }

    private static async Task RunRestoredTurnLoopAsync(RestoredTurn turn, CombatState combatState)
    {
        object state = turn.State;
        CancellationToken token = (CancellationToken)Property("Ct").GetValue(state)!;
        try
        {
            Func<Task>? actionDuringEnemyTurn = null;
            if (combatState.CurrentSide == CombatSide.Player)
            {
                // The snapshot already contains turn-start effects. Resume at the ready signals,
                // not StartCombatInternal/StartTurn, which would draw cards and run hooks again.
                object endSource = Property("EndTurnSignalSource").GetValue(state)!;
                Task endTask = (Task)endSource.GetType().GetProperty("Task")!.GetValue(endSource)!;
                await endTask.WaitAsync(token);
                object signal = endTask.GetType().GetProperty("Result")!.GetValue(endTask)!;
                if (signal.GetType().GetProperty("RunningAction")!.GetValue(signal) is GameAction runningAction)
                {
                    try
                    {
                        await runningAction.CompletionTask.WaitAsync(token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // A canceled action still releases the turn-end boundary.
                    }
                }
                if (!turn.SkipPhaseOne)
                {
                    MethodInfo phaseOne = AccessTools.DeclaredMethod(
                        typeof(CombatManager), "AfterAllPlayersReadyToEndTurn", [TurnStateType, signal.GetType()])
                        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "AfterAllPlayersReadyToEndTurn");
                    await (Task)phaseOne.Invoke(CombatManager.Instance, [state, signal])!;
                }

                if (!(bool)Property("IsLive").GetValue(state)!)
                {
                    turn.Finished.TrySetResult();
                    return;
                }
                var beginSource = (TaskCompletionSource<Func<Task>?>)Property("BeginEnemyTurnSignalSource").GetValue(state)!;
                actionDuringEnemyTurn = await beginSource.Task.WaitAsync(token);
                await InvokeTurnTask("AfterAllPlayersReadyToBeginEnemyTurn", state);
            }
            turn.Finished.TrySetResult();

            while ((bool)Property("IsLive").GetValue(state)!)
            {
                token.ThrowIfCancellationRequested();
                MethodInfo startTurn = AccessTools.DeclaredMethod(
                    typeof(CombatManager), "StartTurn", [TurnStateType, typeof(Func<Task>)])
                    ?? throw new MissingMethodException(typeof(CombatManager).FullName, "StartTurn");
                await (Task)startTurn.Invoke(CombatManager.Instance, [state, actionDuringEnemyTurn])!;
                actionDuringEnemyTurn = await (Task<Func<Task>?>)InvokeTurnTask("AwaitTurnEndAndSwitchSides", state);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            turn.Finished.TrySetCanceled(token);
            throw;
        }
        catch (Exception ex)
        {
            turn.Finished.TrySetException(ex);
            MainFile.Logger.Error($"Restored combat turn loop failed: {ex}");
            UndoDebugLog.Write($"restored_turn_loop_failed: {ex}");
            throw;
        }
    }

    private static Task InvokeTurnTask(string name, object state) =>
        (Task)TurnMethod(name).Invoke(CombatManager.Instance, [state])!;

    private static RestoredTurn GetRestoredTurn()
    {
        if (_restoredTurn == null || !ReferenceEquals(_restoredTurn.State, TurnStateField.GetValue(CombatManager.Instance)))
            throw new InvalidOperationException("No restored combat turn is active.");
        return _restoredTurn;
    }

    private static FieldInfo RequiredField(string name) =>
        AccessTools.DeclaredField(typeof(CombatManager), name)
        ?? throw new MissingFieldException(typeof(CombatManager).FullName, name);

    private static PropertyInfo Property(string name) =>
        AccessTools.DeclaredProperty(TurnStateType, name)
        ?? throw new MissingMemberException(TurnStateType.FullName, name);

    private static void Set(object state, string name, object? value) => Property(name).SetValue(state, value);
}
