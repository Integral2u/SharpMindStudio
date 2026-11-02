using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// Rules are data: they must validate, block/add risk/choose a playbook, expand
/// the intake text, and stamp the version onto the plan they produced.
/// </summary>
public sealed class TrainingRuleSetTests
{
    [Fact]
    public void DefaultRuleSet_LoadsValidatesAndCarriesAVersion()
    {
        TrainingRuleSet set = TrainingRuleSet.Load(TrainingRuleSetStore.ReadEmbedded());

        Assert.Equal("studio-training", set.Id);
        Assert.False(string.IsNullOrWhiteSpace(set.Version));
        Assert.NotEmpty(set.Rules);
        Assert.Equal(set.Rules.Count, set.Rules.Select(rule => rule.Id).Distinct().Count());
    }

    [Fact]
    public void Rules_BlockAddRiskChoosePlaybookAndExpandTheIntake()
    {
        var set = new TrainingRuleSet
        {
            Id = "test-rules",
            Version = "9.1",
            Rules =
            [
                new TrainingRule
                {
                    Id = "no-data",
                    When = new TrainingRuleCondition { FromBase = false, Data = ["None"] },
                    Then = new TrainingRuleAction
                    {
                        Block = ["Pretrain"],
                        BlockedReason = "Nothing to train on.",
                        Alternative = "Add a corpus first.",
                    },
                },
                new TrainingRule
                {
                    Id = "regulated",
                    When = new TrainingRuleCondition { Constraint = ["Regulated"] },
                    Then = new TrainingRuleAction { AddRisk = "Keep it local." },
                },
                new TrainingRule
                {
                    Id = "minutes-smoke",
                    When = new TrainingRuleCondition
                    {
                        Plan = ["fast"],
                        FromBase = false,
                        Effort = ["Minutes"],
                    },
                    Then = new TrainingRuleAction { Playbook = "smoke" },
                },
                new TrainingRule
                {
                    Id = "good-looks",
                    When = new TrainingRuleCondition { HasGoodLooksLike = true },
                    Then = new TrainingRuleAction
                    {
                        AddRisk = "Turn '{goodLooksLike}' into eval prompts.",
                    },
                },
            ],
        };

        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.Nothing,
            HasData = false,
            Intake = new PlannerIntake
            {
                Data = PlannerDataShape.None,
                Constraint = PlannerConstraint.Regulated,
                Effort = PlannerEffort.Minutes,
                GoodLooksLike = "answer support tickets",
            },
        }, set);

        TrainingPlan fast = plans[0];
        Assert.Equal("smoke", fast.PlaybookId);
        Assert.Equal("test-rules", fast.RuleSetId);
        Assert.Equal("9.1", fast.RuleSetVersion);
        Assert.Contains("minutes-smoke", fast.AppliedRuleIds);
        Assert.Contains(fast.Risks, risk => risk == "Keep it local.");
        Assert.Contains(fast.Risks, risk => risk == "Turn 'answer support tickets' into eval prompts.");

        PlannerMove pretrain = Assert.Single(fast.Moves.Where(move => move.Action == PlannerAction.Pretrain));
        Assert.Equal(PlannerMoveStatus.Blocked, pretrain.Status);
        Assert.Equal("Nothing to train on.", pretrain.BlockedReason);
        Assert.Equal("Add a corpus first.", pretrain.Alternative);
    }

    [Fact]
    public void Validate_RejectsDuplicateIdsAndUnknownValues()
    {
        var duplicate = new TrainingRuleSet
        {
            Rules =
            [
                new TrainingRule { Id = "same", Then = new TrainingRuleAction { AddRisk = "a" } },
                new TrainingRule { Id = "same", Then = new TrainingRuleAction { AddRisk = "b" } },
            ],
        };
        Assert.Throws<InvalidDataException>(duplicate.Validate);

        var unknown = new TrainingRuleSet
        {
            Rules =
            [
                new TrainingRule
                {
                    Id = "bad",
                    When = new TrainingRuleCondition { Task = ["NotATask"] },
                    Then = new TrainingRuleAction { AddRisk = "x" },
                },
            ],
        };
        Assert.Throws<InvalidDataException>(unknown.Validate);

        var noAction = new TrainingRuleSet
        {
            Rules = [new TrainingRule { Id = "empty" }],
        };
        Assert.Throws<InvalidDataException>(noAction.Validate);
    }

    [Fact]
    public void DefaultRuleSet_ProducesTheIntakeRisksAndPlaybooks()
    {
        IReadOnlyList<TrainingPlan> plans = TrainingPlanner.Plan(new PlannerInput
        {
            Start = PlannerStart.Nothing,
            HasData = true,
            Intake = new PlannerIntake
            {
                Data = PlannerDataShape.InstructionData,
                Runtime = PlannerRuntime.PhoneEdge,
                Task = PlannerTask.ToolUser,
                GoodLooksLike = "summarise the handbook",
            },
        });

        Assert.All(plans, plan =>
        {
            Assert.False(string.IsNullOrWhiteSpace(plan.RuleSetVersion));
            Assert.Contains(plan.Risks, risk => risk.Contains("SFT", StringComparison.Ordinal));
            Assert.Contains(plan.Risks, risk => risk.Contains("Phone/edge", StringComparison.Ordinal));
            Assert.Contains(plan.Risks, risk => risk.Contains("tool probe", StringComparison.Ordinal));
            Assert.Contains(plan.Risks, risk => risk.Contains("summarise the handbook", StringComparison.Ordinal));
        });
    }
}
