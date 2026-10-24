using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.TestSupport;
using UndoTheSpire2;

if (args.Length != 1)
    throw new ArgumentException("Pass the installed game's data directory.");

string gameData = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(gameData, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
await CompatibilityChecks.RunAsync();

internal static class CompatibilityChecks
{
    private static readonly Assembly Mod = typeof(UndoController).Assembly;
    private static readonly Harmony Harmony = new("UndoTheSpire2.CompatibilityTests");
    private static int _phaseOneCalls;
    private static int _phaseTwoCalls;
    private static int _enemyTurns;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task RunAsync()
    {
        TestMode.IsOn = true;
        // Only engine-environment probes are stubbed; game/mod methods are the installed binaries.
        Harmony.Patch(AccessTools.Method(typeof(OS), nameof(OS.GetCmdlineArgs)),
            prefix: new HarmonyMethod(typeof(CompatibilityChecks), nameof(NoCommandLine)));
        Harmony.Patch(AccessTools.Method(typeof(OS), nameof(OS.HasFeature)),
            prefix: new HarmonyMethod(typeof(CompatibilityChecks), nameof(NoFeature)));

        try
        {
            RuntimeHelpers.RunClassConstructor(Type("UndoRuntimeStateCodecRegistry").TypeHandle);
            Console.WriteLine("PASS snapshot codec registry initializes");

            int patched = 0;
            foreach (Type type in Mod.GetTypes().Where(t => t.IsDefined(typeof(HarmonyPatch), false)))
            {
                Harmony.CreateClassProcessor(type).Patch();
                patched++;
            }
            Console.WriteLine($"PASS all {patched} Harmony patch classes install");

            await RestoredTurnStateIsIsolatedAsync();
            Console.WriteLine("PASS restored turn state replaces canceled signals and read-only manager flags");
            await RestoredTurnAdvancesOnceAsync(retain: false);
            await RestoredTurnAdvancesOnceAsync(retain: true);
            Console.WriteLine("PASS normal and retain-choice restores advance the enemy turn exactly once");
        }
        finally
        {
            Harmony.UnpatchAll(Harmony.Id);
        }
    }

    private static async Task RestoredTurnAdvancesOnceAsync(bool retain)
    {
        Type runtime = Type("UndoCombatTurnRuntime");
        Type turnType = (Type)runtime.GetField("TurnStateType", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Type signalType = typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.Combat.EndTurnSignal", true)!;
        var combat = (CombatState)RuntimeHelpers.GetUninitializedObject(typeof(CombatState));
        combat.CurrentSide = CombatSide.Player;
        var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
        _phaseOneCalls = _phaseTwoCalls = _enemyTurns = 0;

        MethodInfo[] originals =
        [
            AccessTools.Method(typeof(CombatManager), "AfterAllPlayersReadyToEndTurn", [turnType, signalType]),
            AccessTools.Method(typeof(CombatManager), "AfterAllPlayersReadyToBeginEnemyTurn", [turnType]),
            AccessTools.Method(typeof(CombatManager), "StartTurn", [turnType, typeof(Func<Task>)]),
            AccessTools.Method(typeof(CombatManager), nameof(CombatManager.SetReadyToEndTurn)),
            AccessTools.Method(typeof(CombatManager), nameof(CombatManager.SetReadyToBeginEnemyTurn))
        ];
        string[] prefixes = [nameof(PhaseOne), nameof(PhaseTwo), nameof(EnemyTurn), nameof(EndTurn), nameof(BeginEnemyTurn)];
        for (int i = 0; i < originals.Length; i++)
            Harmony.Patch(originals[i], prefix: new HarmonyMethod(typeof(CompatibilityChecks), prefixes[i]));

        try
        {
            await (Task)Call(runtime, "PrepareForRestoreAsync", combat, null)!;
            Call(runtime, "StartRestoredTurnLoop", combat);
            Check(_phaseOneCalls == 0 && _enemyTurns == 0, "restore must not replay turn-start effects");
            if (retain)
                await ((Task)Call(runtime, "ResumeAfterRetainSelectionAsync", player)!).WaitAsync(TimeSpan.FromSeconds(5));
            else
                CombatManager.Instance.SetReadyToEndTurn(player, false);

            Task loop = (Task)AccessTools.Field(typeof(CombatManager), "_turnLoopTask").GetValue(CombatManager.Instance)!;
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            Check(_phaseOneCalls == (retain ? 0 : 1), "retain must skip already-applied phase-one effects");
            Check(_phaseTwoCalls == 1 && _enemyTurns == 1, "restoration must advance phase two and enemy turn once");
            Call(runtime, "CancelCurrentTurnLoop");
        }
        finally
        {
            foreach (MethodInfo original in originals)
                Harmony.Unpatch(original, HarmonyPatchType.Prefix, Harmony.Id);
        }
    }

    private static object CurrentTurn() =>
        AccessTools.Field(typeof(CombatManager), "_turnState").GetValue(CombatManager.Instance)!;

    private static bool EndTurn(Player player)
    {
        object state = CurrentTurn();
        object source = state.GetType().GetProperty("EndTurnSignalSource")!.GetValue(state)!;
        Type signalType = source.GetType().GenericTypeArguments[0];
        object signal = Activator.CreateInstance(signalType, [null, 1, player, null])!;
        source.GetType().GetMethod("TrySetResult")!.Invoke(source, [signal]);
        return false;
    }

    private static bool BeginEnemyTurn()
    {
        object state = CurrentTurn();
        var source = (TaskCompletionSource<Func<Task>?>)state.GetType().GetProperty("BeginEnemyTurnSignalSource")!.GetValue(state)!;
        source.TrySetResult(null);
        return false;
    }

    private static bool PhaseOne(ref Task __result)
    {
        _phaseOneCalls++;
        BeginEnemyTurn();
        __result = Task.CompletedTask;
        return false;
    }

    private static bool PhaseTwo(ref Task __result)
    {
        _phaseTwoCalls++;
        CombatManager.Instance.DebugOnlyGetState()!.CurrentSide = CombatSide.Enemy;
        __result = Task.CompletedTask;
        return false;
    }

    private static bool EnemyTurn(ref Task __result)
    {
        Check(CombatManager.Instance.DebugOnlyGetState()!.CurrentSide == CombatSide.Enemy, "do not restart the restored player turn");
        _enemyTurns++;
        object state = CurrentTurn();
        state.GetType().GetProperty("IsInProgress")!.SetValue(state, false);
        __result = Task.CompletedTask;
        return false;
    }

    private static async Task RestoredTurnStateIsIsolatedAsync()
    {
        Type runtime = Type("UndoCombatTurnRuntime");
        Type turnType = (Type)runtime.GetField("TurnStateType", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var combat = (CombatState)RuntimeHelpers.GetUninitializedObject(typeof(CombatState));
        FieldInfo field = AccessTools.Field(typeof(CombatManager), "_turnState");

        await (Task)Call(runtime, "PrepareForRestoreAsync", combat, null)!;
        object original = field.GetValue(CombatManager.Instance)!;
        object oldSignal = turnType.GetProperty("EndTurnSignalSource")!.GetValue(original)!;
        turnType.GetProperty("EndingPlayerTurnPhaseOne")!.SetValue(original, true);
        Check(CombatManager.Instance.EndingPlayerTurnPhaseOne, "manager must expose turn-state flags");

        Call(runtime, "CancelCurrentTurnLoop");
        Task oldSignalTask = (Task)oldSignal.GetType().GetProperty("Task")!.GetValue(oldSignal)!;
        Check(oldSignalTask.IsCanceled, "discarded turn signal must be canceled");
        await (Task)Call(runtime, "PrepareForRestoreAsync", combat, null)!;
        object restored = field.GetValue(CombatManager.Instance)!;
        Check(!ReferenceEquals(original, restored), "restoration must own a fresh turn state");
        Check(CombatManager.Instance.IsInProgress, "restored combat must stay in progress");
        Check(!CombatManager.Instance.EndingPlayerTurnPhaseOne, "old phase-one flag must not leak");
        Check(!CombatManager.Instance.EndingPlayerTurnPhaseTwo, "old phase-two flag must not leak");
        Check(ReferenceEquals(combat, CombatManager.Instance.DebugOnlyGetState()), "combat model must remain in place");

        object newSignal = turnType.GetProperty("EndTurnSignalSource")!.GetValue(restored)!;
        Task newSignalTask = (Task)newSignal.GetType().GetProperty("Task")!.GetValue(newSignal)!;
        Check(!newSignalTask.IsCompleted, "restored end-turn signal must accept a new end turn");
        Call(runtime, "CancelCurrentTurnLoop");
    }

    private static Type Type(string name) => Mod.GetType("UndoTheSpire2." + name, throwOnError: true)!;

    private static object? Call(Type type, string name, params object?[] arguments) =>
        (AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name)).Invoke(null, arguments);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static bool NoCommandLine(ref string[] __result)
    {
        __result = [];
        return false;
    }

    private static bool NoFeature(ref bool __result)
    {
        __result = false;
        return false;
    }
}
