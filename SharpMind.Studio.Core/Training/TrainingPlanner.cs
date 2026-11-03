namespace SharpMind.Studio.Core.Training;

public enum PlannerStart
{
    Nothing,
    BaseModel,
    MyModel,
    Checkpoint,
    Playbook,
    PreviousRun,
}

public enum PlannerGoal
{
    Knowledge,
    Behaviour,
    Improve,
    Smaller,
    FitDevice,
    Capability,
    Architecture,
}

public enum PlannerMoveStatus
{
    Ready,
    Blocked,
}

/// <summary>Why a move cannot run: missing input, a prerequisite, or no engine support yet.</summary>
public enum PlannerBlockKind
{
    None,
    NeedsInput,
    NeedsCheckpoint,
    NotSupported,
}

public enum PlannerAction
{
    Design,
    Formatter,
    Tokenizer,
    Data,
    Pretrain,
    Continue,
    LoRA,
    Sft,
    Distill,
    Qat,
    Quantize,
    Evaluate,
    Compare,
    Promote,
    Package,
    Resume,
    Fields,
}

public enum PlannerDestination
{
    NewModel,
    Improve,
}

public enum PlannerTask
{
    NarrowEdge,
    ChatAssistant,
    DomainExpert,
    ToolUser,
    Classifier,
}

public enum PlannerRuntime
{
    ThisPc,
    PhoneEdge,
    ServerGpu,
    Unknown,
}

public enum PlannerDataShape
{
    None,
    SmallCorpus,
    LargeCorpus,
    InstructionData,
}

public enum PlannerConstraint
{
    Public,
    Internal,
    Regulated,
    Unknown,
}

public enum PlannerEffort
{
    Minutes,
    Hours,
    Overnight,
}

/// <summary>The plain-language intake behind the three plans.</summary>
public sealed record PlannerIntake
{
    public PlannerTask Task { get; init; } = PlannerTask.DomainExpert;
    public PlannerRuntime Runtime { get; init; } = PlannerRuntime.ThisPc;
    public PlannerDataShape Data { get; init; } = PlannerDataShape.SmallCorpus;
    public string? GoodLooksLike { get; init; }
    public PlannerConstraint Constraint { get; init; } = PlannerConstraint.Unknown;
    public PlannerEffort Effort { get; init; } = PlannerEffort.Hours;
}

/// <summary>Everything the build-map rules need to propose a path.</summary>
public sealed record PlannerInput
{
    public PlannerStart Start { get; init; } = PlannerStart.Nothing;
    public IReadOnlySet<PlannerGoal> Goals { get; init; } = new HashSet<PlannerGoal>();
    public PlannerIntake Intake { get; init; } = new();
    public bool HasData { get; init; }
    public bool HasFormatter { get; init; }
    public bool IsQuantizedOnly { get; init; }
    public long? CorpusTokens { get; init; }
    public long? ParameterCount { get; init; }

    /// <summary>Data counts only when the intake has not said there is none.</summary>
    public bool EffectiveHasData => HasData && Intake.Data != PlannerDataShape.None;
}

/// <summary>One step in a proposed path, with the reason it is there.</summary>
public sealed record PlannerMove
{
    public required PlannerAction Action { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Reason { get; init; }
    public PlannerMoveStatus Status { get; init; } = PlannerMoveStatus.Ready;
    public PlannerBlockKind BlockKind { get; init; } = PlannerBlockKind.None;
    public string? BlockedReason { get; init; }
    public string? Alternative { get; init; }
}

/// <summary>One of the three plans: Fast, Balanced or Ambitious.</summary>
public sealed record TrainingPlan
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<PlannerMove> Moves { get; init; }
    public IReadOnlyList<string> Risks { get; init; } = [];
    public string? Estimate { get; init; }
    public string? PlaybookId { get; init; }
    public PlannerDestination Destination { get; init; } = PlannerDestination.NewModel;

    /// <summary>Rule set that generated/annotated this plan.</summary>
    public string? RuleSetId { get; init; }

    public string? RuleSetVersion { get; init; }

    /// <summary>Rules that fired for this plan, in evaluation order.</summary>
    public IReadOnlyList<string> AppliedRuleIds { get; init; } = [];
}

/// <summary>
/// The build-map planner: a deterministic rule set over the start node and the
/// user's goals. It proposes the supported path and marks unsupported moves
/// honestly instead of pretending the engine can run them.
///
/// The rules are plain code for now; the design calls for versioned rule files
/// once the move set stabilises.
/// </summary>
public static class TrainingPlanner
{
    public static IReadOnlyList<TrainingPlan> Plan(PlannerInput input, TrainingRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        bool architectureChange = input.Goals.Contains(PlannerGoal.Architecture);
        bool fromBase = input.Start != PlannerStart.Nothing && !architectureChange;
        bool wantsSmaller = input.Goals.Contains(PlannerGoal.Smaller)
                            || input.Goals.Contains(PlannerGoal.FitDevice);
        bool wantsBehaviour = input.Goals.Contains(PlannerGoal.Behaviour);
        bool wantsKnowledge = input.Goals.Contains(PlannerGoal.Knowledge);
        bool wantsCompare = input.Goals.Contains(PlannerGoal.Improve);

        var fast = BuildFast(input, fromBase, wantsKnowledge, wantsBehaviour, wantsSmaller);
        var balanced = BuildBalanced(input, fromBase, wantsKnowledge, wantsBehaviour, wantsCompare, wantsSmaller, architectureChange);
        var ambitious = BuildAmbitious(input, fromBase, wantsKnowledge, wantsBehaviour, wantsCompare, wantsSmaller, architectureChange);

        TrainingRuleSet set = rules ?? TrainingRuleSet.Load(TrainingRuleSetStore.ReadEmbedded());
        return
        [
            ApplyRules(fast, input, set, fromBase, architectureChange),
            ApplyRules(balanced, input, set, fromBase, architectureChange),
            ApplyRules(ambitious, input, set, fromBase, architectureChange),
        ];
    }

    private static TrainingPlan BuildFast(
        PlannerInput input, bool fromBase, bool wantsKnowledge, bool wantsBehaviour, bool wantsSmaller)
    {
        var moves = new List<PlannerMove>();
        if (fromBase) AddStart(input, moves);
        if (!fromBase) moves.Add(DesignMove(true));

        moves.Add(DataMove(input, fromBase
            ? "A focused LoRA run still needs the new or corrective text it should learn from."
            : "Pretraining starts from the corpus; the tokenizer and batches are built from it."));

        if (fromBase)
        {
            moves.Add(QuantizedMasterMove(input));
            moves.Add(Move(PlannerAction.LoRA, "LoRA",
                wantsKnowledge ? "Rank 8 adapters over q/k/v/o and the FFN projections." : "Rank 4 adapters over q/v.",
                wantsKnowledge
                    ? "New knowledge is best carried by the larger adapter set; the base stays frozen."
                    : "A light adapter shifts style or tone without touching the base.",
                input.EffectiveHasData ? PlannerMoveStatus.Ready : PlannerMoveStatus.Blocked,
                input.EffectiveHasData ? null : "Add the fine-tune corpus first.",
                input.EffectiveHasData ? null : "Open Improve and choose a corpus.",
                PlannerBlockKind.NeedsInput));
        }
        else
        {
            moves.Add(Move(PlannerAction.Pretrain, "Pretrain",
                "A small, narrow model on this corpus.",
                "From-scratch training suits a narrow-domain edge model; it is not a shortcut to general chat.",
                input.EffectiveHasData ? PlannerMoveStatus.Ready : PlannerMoveStatus.Blocked,
                input.EffectiveHasData ? null : "Add a corpus first.",
                input.EffectiveHasData ? null : "Use the smoke playbook to validate the pipeline first.",
                PlannerBlockKind.NeedsInput));
        }

        moves.Add(EvaluateMove());
        moves.Add(PackageMove());

        if (wantsSmaller) moves.Add(DistillMove());

        var risks = new List<string>();
        if (fromBase)
            risks.Add("LoRA adds targeted behaviour or knowledge; it cannot add a new language or large capability cheaply.");
        else
            risks.Add("From-scratch training is data-bound: a small corpus gives a narrow model, not a general chat model.");

        return new TrainingPlan
        {
            Id = "fast",
            Name = "Fast",
            Summary = fromBase
                ? "A focused LoRA pass with the existing base, then evaluate and package."
                : "A small from-scratch model to prove the corpus and get a usable narrow model.",
            Moves = moves,
            Risks = risks,
            Estimate = Estimate(input, fromBase),
            PlaybookId = fromBase
                ? (wantsKnowledge ? "lora-domain" : "lora-light")
                : "domain-small",
            Destination = fromBase ? PlannerDestination.Improve : PlannerDestination.NewModel,
        };
    }

    private static TrainingPlan BuildBalanced(
        PlannerInput input,
        bool fromBase,
        bool wantsKnowledge,
        bool wantsBehaviour,
        bool wantsCompare,
        bool wantsSmaller,
        bool architectureChange)
    {
        var moves = new List<PlannerMove>();
        if (fromBase) AddStart(input, moves);

        if (!fromBase)
        {
            moves.Add(DesignMove(true));
            moves.Add(TokenizerMove());
        }

        moves.Add(DataMove(input, "Both paths need the corpus cleaned and split before a weight-changing move."));

        if (fromBase)
        {
            moves.Add(QuantizedMasterMove(input));
            moves.Add(ContinueMove(input));
            moves.Add(Move(PlannerAction.LoRA, "LoRA",
                "Rank 8 adapters over q/k/v/o and the FFN projections.",
                "LoRA is the supported way to adapt a packaged base without a full retrain.",
                input.EffectiveHasData ? PlannerMoveStatus.Ready : PlannerMoveStatus.Blocked,
                input.EffectiveHasData ? null : "Add the fine-tune corpus first.",
                input.EffectiveHasData ? null : "Open Improve and choose a corpus.",
                PlannerBlockKind.NeedsInput));
        }
        else
        {
            moves.Add(Move(PlannerAction.Pretrain, "Pretrain",
                "A wider, BPE-tokenized model on this corpus.",
                "A larger from-scratch run suits a domain base; it needs enough tokens to be worth the time.",
                input.EffectiveHasData ? PlannerMoveStatus.Ready : PlannerMoveStatus.Blocked,
                input.EffectiveHasData ? null : "Add a corpus first.",
                input.EffectiveHasData ? null : "Start with the smoke playbook.",
                PlannerBlockKind.NeedsInput));
        }

        if (wantsBehaviour) moves.Add(SftMove(input));

        moves.Add(EvaluateMove());
        if (wantsCompare) moves.Add(CompareMove());
        moves.Add(QatMove());
        moves.Add(QuantizeMove());
        moves.Add(PackageMove());
        if (wantsSmaller) moves.Add(DistillMove());

        var risks = new List<string>
        {
            fromBase
                ? "Continuing can forget; add replay data if the new corpus is narrow."
                : "From-scratch training is data-bound; measure the corpus before committing hours.",
            "QAT approximates the packaged precision; verify the held-out exam after export.",
        };

        return new TrainingPlan
        {
            Id = "balanced",
            Name = "Balanced",
            Summary = fromBase
                ? "Adapt the base, compare it with the previous version, train against a QAT target and package."
                : "Design and pretrain a solid base, evaluate it, then train against a QAT target.",
            Moves = moves,
            Risks = risks,
            Estimate = Estimate(input, fromBase),
            PlaybookId = fromBase ? "lora-domain" : "domain-small",
            Destination = fromBase ? PlannerDestination.Improve : PlannerDestination.NewModel,
        };
    }

    private static TrainingPlan BuildAmbitious(
        PlannerInput input,
        bool fromBase,
        bool wantsKnowledge,
        bool wantsBehaviour,
        bool wantsCompare,
        bool wantsSmaller,
        bool architectureChange)
    {
        var moves = new List<PlannerMove>();
        if (fromBase) AddStart(input, moves);

        if (!fromBase)
        {
            moves.Add(DesignMove(true));
            moves.Add(TokenizerMove());
        }

        moves.Add(DataMove(input, "The full path needs the corpus cleaned, split and reviewed."));

        if (fromBase)
        {
            moves.Add(QuantizedMasterMove(input));
            moves.Add(ContinueMove(input));
            moves.Add(Move(PlannerAction.LoRA, "LoRA",
                "Rank 8 adapters over q/k/v/o and the FFN projections.",
                "LoRA is the supported first move; it is cheap enough to compare against the base.",
                input.EffectiveHasData ? PlannerMoveStatus.Ready : PlannerMoveStatus.Blocked,
                input.EffectiveHasData ? null : "Add the fine-tune corpus first.",
                input.EffectiveHasData ? null : "Open Improve and choose a corpus.",
                PlannerBlockKind.NeedsInput));
        }
        else
        {
            moves.Add(Move(PlannerAction.Pretrain, "Pretrain",
                "A LLaMA-style base with a wider vocabulary and context.",
                "An ambitious path needs a stronger base before behaviour tuning.",
                input.EffectiveHasData ? PlannerMoveStatus.Ready : PlannerMoveStatus.Blocked,
                input.EffectiveHasData ? null : "Add a corpus first.",
                input.EffectiveHasData ? null : "Use the chat-base playbook once the corpus is ready.",
                PlannerBlockKind.NeedsInput));
        }

        moves.Add(SftMove(input));
        moves.Add(EvaluateMove());
        if (wantsCompare) moves.Add(CompareMove());
        moves.Add(Move(PlannerAction.Promote, "Promote",
            "Write a promotion report after the held-out exam.",
            "Promotion should be a recorded verdict, not a feeling.",
            PlannerMoveStatus.Ready,
            null, null));
        moves.Add(QatMove());
        moves.Add(QuantizeMove());
        moves.Add(PackageMove());
        if (wantsSmaller) moves.Add(DistillMove());

        var risks = new List<string>
        {
            "SFT and post-training quantisation are not connected in this build; the plan marks them blocked.",
            fromBase
                ? "Long continued runs can drift; compare against the base before promoting."
                : "From-scratch runs at this size cost hours; rehearse before committing.",
        };

        return new TrainingPlan
        {
            Id = "ambitious",
            Name = "Ambitious",
            Summary = fromBase
                ? "Continue and behaviour-tune the base, then compare, promote and package."
                : "Design a stronger base, pretrain, behavior-tune and package.",
            Moves = moves,
            Risks = risks,
            Estimate = Estimate(input, fromBase),
            PlaybookId = fromBase ? "lora-domain" : "chat-base",
            Destination = fromBase ? PlannerDestination.Improve : PlannerDestination.NewModel,
        };
    }

    private static void AddStart(PlannerInput input, List<PlannerMove> moves)
    {
        switch (input.Start)
        {
            case PlannerStart.Checkpoint:
                moves.Add(Move(PlannerAction.Resume, "Resume or branch",
                    "Continue the recorded step and schedule, or branch into a new recipe.",
                    "A checkpoint already carries weights and optimizer state.",
                    PlannerMoveStatus.Ready, null, null));
                break;
            case PlannerStart.Playbook:
                moves.Add(Move(PlannerAction.Fields, "Fill the open fields",
                    "Supply the corpus and device-specific values the playbook leaves open.",
                    "A playbook is a starting point; the data and target still come from this machine.",
                    PlannerMoveStatus.Ready, null, null));
                break;
            case PlannerStart.PreviousRun:
                moves.Add(Move(PlannerAction.Fields, "Clone the previous run",
                    "Reuse its recipe and data, then change one thing.",
                    "A controlled experiment changes one variable at a time.",
                    PlannerMoveStatus.Ready, null, null));
                break;
        }
    }

    private static PlannerMove DataMove(PlannerInput input, string reason)
    {
        bool hasData = input.EffectiveHasData;
        string? blocked = hasData
            ? null
            : input.Intake.Data == PlannerDataShape.None
                ? "The intake says there is no training data yet."
                : "No corpus source has a path yet.";

        return Move(PlannerAction.Data, "Data",
            "Add or review sources, cleaning stages, validation and exam splits.",
            reason,
            hasData ? PlannerMoveStatus.Ready : PlannerMoveStatus.Blocked,
            blocked,
            hasData ? null : "Add a text corpus in the editor below.",
            PlannerBlockKind.NeedsInput);
    }

    private static PlannerMove DesignMove(bool ready)
        => Move(PlannerAction.Design, "Design",
            "Choose the architecture preset, dimensions, context and MoE layout.",
            "Architecture sets the ceiling for later moves; it belongs before pretraining.",
            ready ? PlannerMoveStatus.Ready : PlannerMoveStatus.Blocked,
            ready ? null : "Design is available in the editor below.",
            ready ? null : null);

    private static PlannerMove TokenizerMove()
        => Move(PlannerAction.Tokenizer, "Tokenizer",
            "Train BPE or character vocabularies, or reuse a cached tokenizer.",
            "The vocabulary fixes the embedding width and the token budget.",
            PlannerMoveStatus.Ready, null, null);

    private static PlannerMove ContinueMove(PlannerInput input)
        => input.Start == PlannerStart.Checkpoint
            ? Move(PlannerAction.Continue, "Continue",
                "Reuse the checkpoint weights and continue the schedule.",
                "The checkpoint already records the step and optimizer state.",
                PlannerMoveStatus.Ready, null, null)
            : Move(PlannerAction.Continue, "Continue",
                "Warm-restart from a training checkpoint under a fresh schedule.",
                "Continuing a packaged .smm directly needs a checkpoint; LoRA is the supported adapt move.",
                PlannerMoveStatus.Blocked,
                "No training checkpoint is selected; a packaged .smm has no optimizer state.",
                "Use LoRA on the packaged base, or point Resume/Warm restart at a checkpoint in the editor.",
                PlannerBlockKind.NeedsCheckpoint);

    private static PlannerMove QuantizedMasterMove(PlannerInput input)
        => input.IsQuantizedOnly
            ? Move(PlannerAction.Fields, "Find or rebuild the float master",
                "Quantized-only weights cap the adaptation quality.",
                "LoRA and QAT want float master weights.",
                PlannerMoveStatus.Blocked,
                "This base is quantized-only; the float master is missing.",
                "Find the float .smm, or accept a lower ceiling with LoRA on the dequantized weights.",
                PlannerBlockKind.NeedsInput)
            : Move(PlannerAction.Fields, "Confirm the float master",
                "The packaged base must expose float weights for training.",
                "LoadForTraining dequantizes the base; adapter quality depends on the master.",
                PlannerMoveStatus.Ready, null, null);

    private static PlannerMove SftMove(PlannerInput input)
        => Move(PlannerAction.Sft, "SFT",
            "Instruction tuning with assistant-only loss masks.",
            input.HasFormatter
                ? "The base has a formatter; SFT would refine instruction following."
                : "Behaviour changes usually need instruction data and a formatter with role tokens.",
            PlannerMoveStatus.Blocked,
            "The SFT data path, formatter designer and record batcher are not connected in this build.",
            "Use LoRA on curated chat text and evaluate on the held-out exam until SFT lands.",
            PlannerBlockKind.NotSupported);

    private static PlannerMove EvaluateMove()
        => Move(PlannerAction.Evaluate, "Evaluate",
            "Watch validation curves and keep the best validation step.",
            "A weight-changing move without a held-out signal is guesswork.",
            PlannerMoveStatus.Ready, null, null);

    private static PlannerMove CompareMove()
        => Move(PlannerAction.Compare, "Compare",
            "Put the candidate beside the base or previous run on curves and the exam.",
            "A candidate is only an improvement relative to something.",
            PlannerMoveStatus.Ready, null, null);

    private static PlannerMove QatMove()
        => Move(PlannerAction.Qat, "QAT",
            "Train against a packaged precision while keeping F32 master weights.",
            "Quantization-aware training reduces the surprise when the model is packaged.",
            PlannerMoveStatus.Ready, null, null);

    private static PlannerMove QuantizeMove()
        => Move(PlannerAction.Quantize, "Quantize",
            "Produce a smaller packaged variant.",
            "Quantization comes after training and evaluation.",
            PlannerMoveStatus.Blocked,
            "Post-training quantisation is not in this build; QAT and GGUF conversion are available.",
            "Use a QAT target during training and convert to GGUF on Models when needed.",
            PlannerBlockKind.NotSupported);

    private static PlannerMove DistillMove()
        => Move(PlannerAction.Distill, "Distill",
            "Train a smaller student against a teacher.",
            "Distillation is the honest route to a smaller model that stays capable.",
            PlannerMoveStatus.Blocked,
            "Teacher/student selection and the distillation loop are not connected in this build.",
            "Keep the current model and revisit distillation when the loop lands.",
            PlannerBlockKind.NotSupported);

    private static PlannerMove PackageMove()
        => Move(PlannerAction.Package, "Package",
            "Export the .smm (and convert to GGUF if another runtime needs it).",
            "The packaged model is the unit the rest of Studio loads.",
            PlannerMoveStatus.Ready, null, null);

    private static PlannerMove Move(
        PlannerAction action,
        string title,
        string detail,
        string reason,
        PlannerMoveStatus status,
        string? blockedReason,
        string? alternative,
        PlannerBlockKind blockKind = PlannerBlockKind.None)
        => new()
        {
            Action = action,
            Title = title,
            Detail = detail,
            Reason = reason,
            Status = status,
            BlockKind = status == PlannerMoveStatus.Blocked ? blockKind : PlannerBlockKind.None,
            BlockedReason = status == PlannerMoveStatus.Blocked ? blockedReason : null,
            Alternative = status == PlannerMoveStatus.Blocked ? alternative : null,
        };

    private static TrainingPlan ApplyRules(
        TrainingPlan plan,
        PlannerInput input,
        TrainingRuleSet set,
        bool fromBase,
        bool architectureChange)
    {
        var moves = plan.Moves.ToList();
        var risks = plan.Risks.ToList();
        string? playbook = plan.PlaybookId;
        var applied = new List<string>();

        foreach (TrainingRule rule in set.Rules)
        {
            if (!Matches(rule.When, input, plan.Id, fromBase, architectureChange))
                continue;

            applied.Add(rule.Id);

            if (!string.IsNullOrWhiteSpace(rule.Then.Playbook))
                playbook = rule.Then.Playbook.Trim();

            if (!string.IsNullOrWhiteSpace(rule.Then.AddRisk))
            {
                string risk = Expand(rule.Then.AddRisk, input);
                if (!risks.Any(existing => string.Equals(existing, risk, StringComparison.Ordinal)))
                    risks.Add(risk);
            }

            if (rule.Then.Block is not { Count: > 0 } blocks) continue;
            for (int i = 0; i < moves.Count; i++)
            {
                if (!blocks.Contains(moves[i].Action.ToString(), StringComparer.OrdinalIgnoreCase))
                    continue;

                moves[i] = moves[i] with
                {
                    Status = PlannerMoveStatus.Blocked,
                    BlockKind = moves[i].BlockKind == PlannerBlockKind.None
                        ? PlannerBlockKind.NeedsInput
                        : moves[i].BlockKind,
                    BlockedReason = rule.Then.BlockedReason ?? moves[i].BlockedReason ?? $"Blocked by rule '{rule.Id}'.",
                    Alternative = rule.Then.Alternative ?? moves[i].Alternative,
                };
            }
        }

        return plan with
        {
            Moves = moves,
            Risks = risks,
            PlaybookId = playbook,
            RuleSetId = set.Id,
            RuleSetVersion = set.Version,
            AppliedRuleIds = applied,
        };
    }

    private static bool Matches(
        TrainingRuleCondition when,
        PlannerInput input,
        string planId,
        bool fromBase,
        bool architectureChange)
    {
        if (!MatchesList(when.Plan, planId)) return false;
        if (!MatchesList(when.Start, input.Start.ToString())) return false;
        if (!MatchesList(when.Task, input.Intake.Task.ToString())) return false;
        if (!MatchesList(when.Runtime, input.Intake.Runtime.ToString())) return false;
        if (!MatchesList(when.Data, input.Intake.Data.ToString())) return false;
        if (!MatchesList(when.Constraint, input.Intake.Constraint.ToString())) return false;
        if (!MatchesList(when.Effort, input.Intake.Effort.ToString())) return false;

        if (when.FromBase is { } fromBaseWanted && fromBaseWanted != fromBase) return false;
        if (when.ArchitectureChange is { } architectureWanted && architectureWanted != architectureChange) return false;
        if (when.HasData is { } dataWanted && dataWanted != input.EffectiveHasData) return false;
        if (when.HasGoodLooksLike is { } goodWanted
            && goodWanted != !string.IsNullOrWhiteSpace(input.Intake.GoodLooksLike))
            return false;

        if (when.GoalsAll is { Count: > 0 } all
            && !all.All(goal => input.Goals.Contains(ParseGoal(goal))))
            return false;

        if (when.GoalsAny is { Count: > 0 } any
            && !any.Any(goal => input.Goals.Contains(ParseGoal(goal))))
            return false;

        return true;
    }

    private static bool MatchesList(IReadOnlyList<string>? allowed, string value)
        => allowed is null || allowed.Count == 0
           || allowed.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static PlannerGoal ParseGoal(string value)
        => Enum.TryParse(value, ignoreCase: true, out PlannerGoal goal)
            ? goal
            : throw new InvalidDataException($"Unknown planner goal '{value}' in a rule condition.");

    private static string Expand(string template, PlannerInput input)
        => template.Replace(
            "{goodLooksLike}",
            input.Intake.GoodLooksLike?.Trim() ?? "",
            StringComparison.Ordinal);

    private static string? Estimate(PlannerInput input, bool fromBase)
    {
        if (fromBase)
            return "Base size and step time appear once the base is selected in Improve.";
        if (input.ParameterCount is > 0)
            return $"About {input.ParameterCount.Value / 1_000_000d:0.0}M parameters before training.";
        return "The parameter estimate appears once the model dimensions are valid.";
    }
}
