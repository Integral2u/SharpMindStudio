using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// Run history is what makes the Compare view survive a restart: each record
/// must round-trip its curve and best-validation figures, ignore corrupt files,
/// and delete cleanly.
/// </summary>
public sealed class TrainingRunStoreTests
{
    [Fact]
    public void SaveListAndDelete_RoundTripCurveAndBestVal()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sharpmind-runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var older = new TrainingRunRecord
            {
                Name = "older",
                Success = true,
                TotalSteps = 10,
                FinalStep = 10,
                ValidationPercent = 20,
                BestValLoss = 2.5,
                BestValStep = 8,
                FinalLoss = 2.7,
                ElapsedSeconds = 3.5,
                FinishedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                Curve =
                [
                    new TrainingCurvePoint(5, 3.0, 1e-3, 3.1),
                    new TrainingCurvePoint(10, 2.7, 5e-4, 2.5),
                ],
            };
            var newer = new TrainingRunRecord
            {
                Name = "newer",
                Success = true,
                FineTune = true,
                BaseModelPath = @"C:\models\base.smm",
                TotalSteps = 4,
                FinalStep = 4,
                FinishedUtc = new DateTime(2026, 1, 3, 3, 4, 5, DateTimeKind.Utc),
                Curve =
                [
                    new TrainingCurvePoint(1, 4.0, 1e-3, null),
                    new TrainingCurvePoint(4, 3.6, 5e-4, null),
                ],
            };

            TrainingRunStore.Save(older, folder);
            TrainingRunStore.Save(newer, folder);

            // A broken file must not hide the valid ones.
            File.WriteAllText(Path.Combine(folder, "broken.json"), "{ not json");

            IReadOnlyList<TrainingRunSummary> runs = TrainingRunStore.List(folder);

            Assert.Equal(2, runs.Count);
            Assert.Equal("newer", runs[0].Record.Name);
            Assert.Equal("older", runs[1].Record.Name);

            TrainingRunRecord loadedOlder = runs[1].Record;
            Assert.Equal(2, loadedOlder.Curve.Count);
            Assert.Equal(2.5, loadedOlder.BestValLoss);
            Assert.Equal(8, loadedOlder.BestValStep);
            Assert.Equal(2.5, loadedOlder.Curve[^1].ValLoss);
            Assert.Equal(3.5, loadedOlder.ElapsedSeconds);

            Assert.True(runs[0].Record.FineTune);
            Assert.Equal(@"C:\models\base.smm", runs[0].Record.BaseModelPath);
            Assert.All(runs[0].Record.Curve, point => Assert.Null(point.ValLoss));

            TrainingRunStore.Delete(runs[0].Path);
            Assert.Single(TrainingRunStore.List(folder));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Update_WritesAnExamResultBackIntoTheExistingRecord()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sharpmind-runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var record = new TrainingRunRecord
            {
                Name = "examined",
                Success = true,
                FinishedUtc = DateTime.UtcNow,
                ExportPath = @"C:\models\examined.smm",
            };
            string path = TrainingRunStore.Save(record, folder);

            record.ExamLoss = 2.31;
            record.ExamPerplexity = 10.1;
            record.ExamBatches = 4;
            record.ExamTokens = 1_024;
            record.ExamEvaluatedUtc = DateTime.UtcNow;
            TrainingRunStore.Update(path, record);

            TrainingRunRecord reloaded = Assert.Single(TrainingRunStore.List(folder)).Record;
            Assert.Equal(2.31, reloaded.ExamLoss);
            Assert.Equal(10.1, reloaded.ExamPerplexity);
            Assert.Equal(4, reloaded.ExamBatches);
            Assert.Equal(1_024, reloaded.ExamTokens);
            Assert.NotNull(reloaded.ExamEvaluatedUtc);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public void From_MapsTheLiveRunIntoAHistoryRecord()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sharpmind-runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string export = Path.Combine(dir, "model.smm");
            string adapter = Path.Combine(dir, "model.smlora");
            File.WriteAllBytes(export, new byte[128]);
            File.WriteAllBytes(adapter, new byte[64]);

            var job = new TrainingJobSettings
            {
                Name = "tuned",
                BaseModelPath = @"C:\models\base.smm",
                TotalSteps = 12,
                ValidationPercent = 15,
                LearningRate = 2e-4f,
                HiddenDim = 64,
                BatchSize = 2,
                SeqLen = 32,
            };
            var result = new TrainingRunResult
            {
                Success = true,
                ExportPath = export,
                AdapterPath = adapter,
                FinalStep = 12,
                BestValLoss = 1.75,
                BestValStep = 9,
            };
            var curve = new List<TrainingCurvePoint>
            {
                new(6, 2.4, 2e-4, 2.2),
                new(12, 1.9, 1e-4, 1.75),
            };

            TrainingRunRecord record = TrainingRunRecord.From(
                job, result, curve, TimeSpan.FromSeconds(7.25), export);

            Assert.True(record.FineTune);
            Assert.Equal(@"C:\models\base.smm", record.BaseModelPath);
            Assert.Equal(12, record.FinalStep);
            Assert.Equal(15, record.ValidationPercent);
            Assert.Equal(1.75, record.BestValLoss);
            Assert.Equal(9, record.BestValStep);
            Assert.Equal(1.9, record.FinalLoss);
            Assert.Equal(7.25, record.ElapsedSeconds);
            Assert.Equal(128, record.ExportBytes);
            Assert.Equal(adapter, record.AdapterPath);
            Assert.Equal(2, record.Curve.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
