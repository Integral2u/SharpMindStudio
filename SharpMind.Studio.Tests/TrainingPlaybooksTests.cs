using SharpMind.Data.Sources;
using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// Playbooks are editor starting points, so they must be complete and valid:
/// unique, with either a pretrain job that passes the same validation as the
/// form, or a fine-tune recipe with usable LoRA controls.
/// </summary>
public sealed class TrainingPlaybooksTests
{
    [Fact]
    public void Playbooks_AreUniqueAndComplete()
    {
        Assert.NotEmpty(TrainingPlaybooks.All);
        Assert.Equal(
            TrainingPlaybooks.All.Count,
            TrainingPlaybooks.All.Select(playbook => playbook.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (TrainingPlaybook playbook in TrainingPlaybooks.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(playbook.Name));
            Assert.False(string.IsNullOrWhiteSpace(playbook.Eyebrow));
            Assert.False(string.IsNullOrWhiteSpace(playbook.Description));
            Assert.False(string.IsNullOrWhiteSpace(playbook.Outcome));

            if (playbook.Kind == TrainingPlaybookKind.Pretrain)
            {
                Assert.NotNull(playbook.CreateJob);
                Assert.Null(playbook.FineTune);
            }
            else
            {
                Assert.Null(playbook.CreateJob);
                Assert.NotNull(playbook.FineTune);

                TrainingFineTuneRecipe recipe = playbook.FineTune!;
                Assert.True(recipe.Rank > 0);
                Assert.True(recipe.Alpha > 0);
                Assert.NotEmpty(recipe.Targets);
                Assert.True(recipe.Steps > 0);
                Assert.True(recipe.LearningRate > 0);
                Assert.Contains(recipe.Output, new[] { "merged", "adapter", "both" });
            }
        }
    }

    [Fact]
    public void PretrainPlaybooks_PassTheEditorValidation()
    {
        foreach (TrainingPlaybook playbook in TrainingPlaybooks.All
                     .Where(playbook => playbook.Kind == TrainingPlaybookKind.Pretrain))
        {
            TrainingJobSettings job = playbook.CreateJob!();
            job.Sources =
            [
                new JobSource
                {
                    Component = new JobComponent
                    {
                        DisplayName = "Text File",
                        TypeName = typeof(TextFileSource).FullName!,
                        Args = new Dictionary<string, string>
                        {
                            ["path"] = @"C:\tmp\corpus.txt",
                            ["mode"] = "FilePerDoc",
                        },
                    },
                },
            ];

            IReadOnlyList<string> warnings = TrainingJobValidation.Warnings(job);
            Assert.True(warnings.Count == 0, $"{playbook.Id}: {string.Join("; ", warnings)}");

            // The resolved config must be constructible for the exported model.
            var config = TrainingModelOptions.ResolveModelConfig(job, job.TokenizerVocabSize);
            Assert.True(config.HiddenDim > 0);
            Assert.True(config.NumLayers > 0);
            Assert.True(config.MaxSeqLen >= job.SeqLen);
        }
    }
}
