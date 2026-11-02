using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// The build-map planner must propose a supported path, keep unsupported moves
/// visible with an honest reason and alternative, and point at a real playbook.
/// </summary>
public sealed class TrainingPlannerTests
{
    private static readonly HashSet<string> PlaybookIds =
        [.. TrainingPlaybooks.All.Select(playbook => playbook.Id)];

    [Fact]
    public void FromScratch_PlansPretrainAndBlocksUnsupportedMoves()
    {
        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.Nothing,
            HasData = true,
            Goals = new HashSet<PlannerGoal> { PlannerGoal.Knowledge, PlannerGoal.Behaviour },
            ParameterCount = 1_000_000,
        });

        Assert.Equal(["fast", "balanced", "ambitious"], plans.Select(plan => plan.Id));
        Assert.All(plans, plan => Assert.Equal(PlannerDestination.NewModel, plan.Destination));
        Assert.All(plans, plan => Assert.Contains(plan.PlaybookId!, PlaybookIds));

        TrainingPlan fast = plans[0];
        Assert.Contains(fast.Moves, move => move.Action == PlannerAction.Pretrain);
        Assert.DoesNotContain(fast.Moves, move => move.Action == PlannerAction.LoRA);
        Assert.Contains("1.0M", fast.Estimate);

        TrainingPlan balanced = plans[1];
        Assert.Contains(balanced.Moves, move => move.Action == PlannerAction.Design);
        Assert.Contains(balanced.Moves, move => move.Action == PlannerAction.Tokenizer);
        Assert.Contains(balanced.Moves, move => move.Action == PlannerAction.Qat);
        Assert.Contains(balanced.Moves, move => move.Action == PlannerAction.Quantize
                                              && move.Status == PlannerMoveStatus.Blocked);

        TrainingPlan ambitious = plans[2];
        PlannerMove sft = Assert.Single(ambitious.Moves.Where(move => move.Action == PlannerAction.Sft));
        Assert.Equal(PlannerMoveStatus.Blocked, sft.Status);
        Assert.False(string.IsNullOrWhiteSpace(sft.BlockedReason));
        Assert.False(string.IsNullOrWhiteSpace(sft.Alternative));

        AssertAllBlockedMovesAreExplained(plans);
    }

    [Fact]
    public void BaseModel_PlansAdaptInImproveAndKeepTheBase()
    {
        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.BaseModel,
            HasData = true,
            Goals = new HashSet<PlannerGoal> { PlannerGoal.Knowledge, PlannerGoal.Improve },
        });

        Assert.All(plans, plan => Assert.Equal(PlannerDestination.Improve, plan.Destination));
        Assert.All(plans, plan => Assert.Contains(plan.PlaybookId!, PlaybookIds));

        TrainingPlan fast = plans[0];
        Assert.Contains(fast.Moves, move => move.Action == PlannerAction.LoRA);
        Assert.DoesNotContain(fast.Moves, move => move.Action == PlannerAction.Pretrain);
        Assert.Equal("lora-domain", fast.PlaybookId);

        TrainingPlan balanced = plans[1];
        Assert.Contains(balanced.Moves, move => move.Action == PlannerAction.Compare);
        Assert.Contains(balanced.Moves, move => move.Action == PlannerAction.Qat);
        Assert.Contains(balanced.Moves, move => move.Action == PlannerAction.Quantize
                                               && move.Status == PlannerMoveStatus.Blocked);

        AssertAllBlockedMovesAreExplained(plans);
    }

    [Fact]
    public void NoData_BlocksDataAndTheMoveThatNeedsIt()
    {
        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.Nothing,
            HasData = false,
        });

        PlannerMove data = Assert.Single(plans[0].Moves.Where(move => move.Action == PlannerAction.Data));
        Assert.Equal(PlannerMoveStatus.Blocked, data.Status);
        Assert.Contains("corpus", data.BlockedReason, StringComparison.OrdinalIgnoreCase);

        PlannerMove pretrain = Assert.Single(plans[0].Moves.Where(move => move.Action == PlannerAction.Pretrain));
        Assert.Equal(PlannerMoveStatus.Blocked, pretrain.Status);
        Assert.Contains("corpus", pretrain.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Checkpoint_StartsWithResumeAndCanContinue()
    {
        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.Checkpoint,
            HasData = true,
            Goals = new HashSet<PlannerGoal> { PlannerGoal.Improve },
        });

        Assert.Equal(PlannerAction.Resume, plans[0].Moves[0].Action);

        PlannerMove resume = plans[0].Moves[0];
        Assert.Equal(PlannerMoveStatus.Ready, resume.Status);

        PlannerMove balancedContinue = Assert.Single(
            plans[1].Moves.Where(move => move.Action == PlannerAction.Continue));
        Assert.Equal(PlannerMoveStatus.Ready, balancedContinue.Status);
    }

    [Fact]
    public void Smaller_AlwaysLeavesDistillationVisibleButBlocked()
    {
        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.MyModel,
            HasData = true,
            Goals = new HashSet<PlannerGoal> { PlannerGoal.Smaller },
        });

        Assert.All(plans, plan =>
        {
            PlannerMove distill = Assert.Single(plan.Moves.Where(move => move.Action == PlannerAction.Distill));
            Assert.Equal(PlannerMoveStatus.Blocked, distill.Status);
            Assert.False(string.IsNullOrWhiteSpace(distill.BlockedReason));
            Assert.False(string.IsNullOrWhiteSpace(distill.Alternative));
        });
    }

    [Fact]
    public void ArchitectureChange_StartsFromDesignEvenWithABase()
    {
        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.MyModel,
            HasData = true,
            Goals = new HashSet<PlannerGoal> { PlannerGoal.Architecture },
        });

        Assert.All(plans, plan => Assert.Equal(PlannerDestination.NewModel, plan.Destination));
        Assert.Contains(plans[0].Moves, move => move.Action == PlannerAction.Design);
        Assert.Contains(plans[0].Moves, move => move.Action == PlannerAction.Pretrain);
        Assert.DoesNotContain(plans[0].Moves, move => move.Action == PlannerAction.LoRA);
    }

    [Fact]
    public void EveryPlan_IsWellFormedForEveryStart()
    {
        foreach (PlannerStart start in Enum.GetValues<PlannerStart>())
        {
            IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
            {
                Start = start,
                HasData = true,
                Goals = new HashSet<PlannerGoal> { PlannerGoal.Behaviour, PlannerGoal.FitDevice },
            });

            Assert.Equal(3, plans.Count);
            Assert.All(plans, plan =>
            {
                Assert.NotEmpty(plan.Moves);
                Assert.Contains(plan.PlaybookId!, PlaybookIds);
                Assert.False(string.IsNullOrWhiteSpace(plan.Summary));
            });

            AssertAllBlockedMovesAreExplained(plans);
        }
    }

    [Fact]
    public void Intake_DataNone_BlocksDataEvenWhenAPathExists()
    {
        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.Nothing,
            HasData = true,
            Intake = new PlannerIntake { Data = PlannerDataShape.None },
        });

        PlannerMove data = Assert.Single(plans[0].Moves.Where(move => move.Action == PlannerAction.Data));
        Assert.Equal(PlannerMoveStatus.Blocked, data.Status);
        Assert.Contains("intake", data.BlockedReason, StringComparison.OrdinalIgnoreCase);

        PlannerMove pretrain = Assert.Single(plans[0].Moves.Where(move => move.Action == PlannerAction.Pretrain));
        Assert.Equal(PlannerMoveStatus.Blocked, pretrain.Status);
    }

    [Fact]
    public void Intake_Minutes_FromScratchUsesTheSmokePlaybook()
    {
        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.Nothing,
            HasData = true,
            Intake = new PlannerIntake { Effort = PlannerEffort.Minutes },
        });

        Assert.Equal("smoke", plans[0].PlaybookId);
        Assert.Contains(plans[0].Risks, risk => risk.Contains("Minutes", StringComparison.Ordinal));
    }

    [Fact]
    public void Intake_CarriesPrivacyInstructionAndEvalHintsIntoEveryPlan()
    {
        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.BaseModel,
            HasData = true,
            Intake = new PlannerIntake
            {
                Constraint = PlannerConstraint.Regulated,
                Data = PlannerDataShape.InstructionData,
                Runtime = PlannerRuntime.PhoneEdge,
                Task = PlannerTask.ToolUser,
                GoodLooksLike = "answer support tickets",
            },
        });

        Assert.All(plans, plan =>
        {
            Assert.Contains(plan.Risks, risk => risk.Contains("this machine", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(plan.Risks, risk => risk.Contains("SFT", StringComparison.Ordinal));
            Assert.Contains(plan.Risks, risk => risk.Contains("Phone/edge", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(plan.Risks, risk => risk.Contains("tool probe", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(plan.Risks, risk => risk.Contains("eval prompts", StringComparison.OrdinalIgnoreCase));
        });
    }

    private static void AssertAllBlockedMovesAreExplained(IEnumerable<TrainingPlan> plans)
    {
        foreach (PlannerMove move in plans.SelectMany(plan => plan.Moves)
                                      .Where(move => move.Status == PlannerMoveStatus.Blocked))
        {
            Assert.False(string.IsNullOrWhiteSpace(move.BlockedReason), $"{move.Title} needs a reason");
            Assert.False(string.IsNullOrWhiteSpace(move.Alternative), $"{move.Title} needs an alternative");
        }
    }
}
