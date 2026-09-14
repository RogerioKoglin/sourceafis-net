// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using SourceAFIS.Engine.Configuration;
using SourceAFIS.Engine.Extractor;
using SourceAFIS.Engine.Fusion;
using SourceAFIS.Engine.Primitives;

namespace SourceAFIS
{
    public class FusionAblationLaboratoryTest
    {
        const string BatchVariable = "SOURCEAFIS_FUSION_LAB_BATCH";

        sealed class SessionSnapshot
        {
            public string SubjectAlias { get; set; }
            public string FingerCode { get; set; }
            public List<CaptureSnapshot> Captures { get; set; }
        }

        sealed class CaptureSnapshot
        {
            public int Index { get; set; }
            public string RawFile { get; set; }
            public string SourceAfisTemplateFile { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public double Dpi { get; set; }
            public string RawSha256 { get; set; }
            public string SourceAfisTemplateSha256 { get; set; }
        }

        sealed record Item(
            string Id,
            string Identity,
            int Capture,
            string TemplateHash,
            bool LegacyRoundTripBytesEqual,
            bool RawReextractionBytesEqual,
            FingerprintTemplate Template,
            BooleanMatrix Coverage);

        sealed record Variant(
            string Name,
            TemplateFuserOptions Options);

        sealed class TrialTotals
        {
            public int Trials;
            public int StableSerializations;
            public int DifferentFromReference;
            public int FullPlans;
            public int OneContributorPlans;
            public int ReferenceOnlyPlans;
            public int ReferenceMinutiae;
            public int OutputMinutiae;
            public int MergedReferenceMinutiae;
            public int SupportedNovelMinutiae;
            public int UniqueCoverageMinutiae;
            public int DroppedUnsupportedMinutiae;
            public int DroppedByLimit;
        }

        sealed record ScoreRow(
            string TrialId,
            string TargetIdentity,
            int ReferenceCapture,
            string ProbeId,
            string ProbeIdentity,
            bool Genuine,
            double BaselineScore,
            double[] VariantScores);

        sealed record Distribution(
            int Count,
            double? Minimum,
            double? P01,
            double? P05,
            double? Median,
            double? Mean,
            double? P95,
            double? P99,
            double? Maximum);

        sealed record ThresholdResult(
            double Threshold,
            int BaselineFalseNonMatches,
            int VariantFalseNonMatches,
            int BaselineFingerImpostorMatches,
            int VariantFingerImpostorMatches);

        sealed record VariantResult(
            string Name,
            bool AverageReferenceMinutiae,
            bool RetainSupportedNovelMinutiae,
            bool RetainUniqueCoverageMinutiae,
            int TrialCount,
            int StableSerializations,
            int DifferentFromReference,
            int FullThreeCapturePlans,
            int OneContributorPlans,
            int ReferenceOnlyPlans,
            double MeanReferenceMinutiae,
            double MeanOutputMinutiae,
            int TotalMergedReferenceMinutiae,
            int TotalSupportedNovelMinutiae,
            int TotalUniqueCoverageMinutiae,
            int TotalDroppedUnsupportedMinutiae,
            int TotalDroppedByLimit,
            int GenuineComparisons,
            int BaselineFalseNonMatches,
            int VariantFalseNonMatches,
            int RescuedGenuineComparisons,
            int RegressedGenuineComparisons,
            int GenuineWins,
            int GenuineTies,
            int GenuineLosses,
            Distribution GenuineScores,
            Distribution GenuineDeltas,
            int FingerImpostorComparisons,
            int BaselineFingerImpostorMatches,
            int VariantFingerImpostorMatches,
            Distribution FingerImpostorScores,
            Distribution FingerImpostorDeltas,
            IReadOnlyList<ThresholdResult> ThresholdSweep);

        sealed record AblationReport(
            int SchemaVersion,
            DateTimeOffset GeneratedUtc,
            string DatasetSha256,
            string SourceAfisVersion,
            int SubjectCount,
            int FingerIdentityCount,
            int CaptureCount,
            int LegacyByteStableReserializations,
            int RawByteStableReextractions,
            double AlignmentScoreThreshold,
            int MinimumMatchedMinutiae,
            double MaximumPairPositionError,
            double DuplicatePositionRadius,
            double DuplicateDirectionToleranceDegrees,
            int MinimumNovelSupport,
            int MaximumMinutiae,
            long ElapsedMilliseconds,
            IReadOnlyList<VariantResult> Variants);

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        [Test, Explicit("Runs controlled ablations of the experimental template fusion.")]
        public void SeparatesAveragingSupportedMinutiaeAndUniqueCoverageEffects()
        {
            string batch = Environment.GetEnvironmentVariable(BatchVariable);
            Assert.That(batch, Is.Not.Null.And.Not.Empty,
                $"Set {BatchVariable} to the existing laboratory batch directory.");
            batch = Path.GetFullPath(batch);
            Assert.That(Directory.Exists(batch), Is.True, $"Batch directory does not exist: {batch}");

            var stopwatch = Stopwatch.StartNew();
            List<Item> items = Load(batch);
            Item[][] groups = items
                .GroupBy(item => item.Identity, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.OrderBy(item => item.Capture).ToArray())
                .ToArray();
            const double threshold = 40;
            var planning = new TemplateFusionOptions(threshold, 8);
            Variant[] variants =
            [
                new("matched-average", new(
                    planning,
                    AverageReferenceMinutiae: true,
                    RetainSupportedNovelMinutiae: false,
                    RetainUniqueCoverageMinutiae: false)),
                new("supported-average", new(
                    planning,
                    AverageReferenceMinutiae: true,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: false)),
                new("full-average", new(
                    planning,
                    AverageReferenceMinutiae: true,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: true)),
                new("supported-anchored", new(
                    planning,
                    AverageReferenceMinutiae: false,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: false)),
                new("full-anchored", new(
                    planning,
                    AverageReferenceMinutiae: false,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: true))
            ];
            var totals = variants.Select(_ => new TrialTotals()).ToArray();
            Dictionary<(string Probe, string Candidate), double> baseline = BuildScoreMatrix(items);
            Dictionary<string, FingerprintMatcher> matchers = items.ToDictionary(
                item => item.Id,
                item => new FingerprintMatcher(item.Template),
                StringComparer.Ordinal);
            var scores = new List<ScoreRow>();

            foreach (Item[] identity in groups)
            {
                for (int first = 0; first < identity.Length - 2; ++first)
                {
                    for (int second = first + 1; second < identity.Length - 1; ++second)
                    {
                        for (int third = second + 1; third < identity.Length; ++third)
                        {
                            Item[] enrollment = [identity[first], identity[second], identity[third]];
                            TemplateFusionInput[] inputs = enrollment
                                .Select(item => new TemplateFusionInput(item.Template, item.Coverage))
                                .ToArray();
                            string trialId = $"{identity[0].Identity}:e{enrollment[0].Capture:D2}-{enrollment[1].Capture:D2}-{enrollment[2].Capture:D2}";
                            var fused = new FingerprintTemplate[variants.Length];
                            int referenceIndex = -1;
                            for (int variant = 0; variant < variants.Length; ++variant)
                            {
                                TemplateFusionResult result = TemplateFuser.Fuse(inputs, variants[variant].Options);
                                if (referenceIndex < 0)
                                    referenceIndex = result.Plan.Reference;
                                else
                                    Assert.That(result.Plan.Reference, Is.EqualTo(referenceIndex));
                                byte[] serialized = result.Template.ToByteArray();
                                fused[variant] = new FingerprintTemplate(serialized);
                                Accumulate(
                                    totals[variant],
                                    result,
                                    serialized,
                                    enrollment[referenceIndex].Template.ToByteArray(),
                                    fused[variant]);
                            }

                            Item reference = enrollment[referenceIndex];
                            var enrollmentIds = enrollment.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                            IEnumerable<Item> probes = identity
                                .Where(item => !enrollmentIds.Contains(item.Id))
                                .Concat(items.Where(item => !string.Equals(
                                    item.Identity,
                                    identity[0].Identity,
                                    StringComparison.Ordinal)));
                            foreach (Item probe in probes)
                            {
                                double[] variantScores = fused
                                    .Select(template => matchers[probe.Id].Match(template))
                                    .ToArray();
                                scores.Add(new(
                                    trialId,
                                    identity[0].Identity,
                                    reference.Capture,
                                    probe.Id,
                                    probe.Identity,
                                    string.Equals(probe.Identity, identity[0].Identity, StringComparison.Ordinal),
                                    baseline[(probe.Id, reference.Id)],
                                    variantScores));
                            }
                        }
                    }
                }
            }

            stopwatch.Stop();
            VariantResult[] results = variants
                .Select((variant, index) => Summarize(variant, index, totals[index], scores, threshold))
                .ToArray();
            var report = new AblationReport(
                1,
                DateTimeOffset.UtcNow,
                DatasetHash(items),
                FingerprintCompatibility.Version,
                items.Select(item => item.Identity.Split(':')[0]).Distinct(StringComparer.Ordinal).Count(),
                groups.Length,
                items.Count,
                items.Count(item => item.LegacyRoundTripBytesEqual),
                items.Count(item => item.RawReextractionBytesEqual),
                planning.MinimumAlignmentScore,
                planning.MinimumMatchedMinutiae,
                variants[0].Options.MaximumPairPositionError,
                variants[0].Options.DuplicatePositionRadius,
                variants[0].Options.DuplicateDirectionTolerance * 180 / Math.PI,
                variants[0].Options.MinimumNovelSupport,
                variants[0].Options.MaximumMinutiae,
                stopwatch.ElapsedMilliseconds,
                results);

            string reportPath = Path.Combine(batch, "fusion-ablation-report.json");
            string scoresPath = Path.Combine(batch, "fusion-ablation-scores.csv");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
            File.WriteAllText(scoresPath, Csv(scores, variants), new UTF8Encoding(false));

            int expectedTrials = groups.Sum(group => CombinationsOfThree(group.Length));
            int expectedScores = groups.Sum(group =>
                CombinationsOfThree(group.Length) * (items.Count - 3));
            Assert.Multiple(() =>
            {
                Assert.That(items.All(item => item.LegacyRoundTripBytesEqual), Is.True);
                Assert.That(items.All(item => item.RawReextractionBytesEqual), Is.True);
                Assert.That(totals.All(total => total.Trials == expectedTrials), Is.True);
                Assert.That(totals.All(total => total.StableSerializations == expectedTrials), Is.True);
                Assert.That(scores, Has.Count.EqualTo(expectedScores));
                Assert.That(scores.All(score => score.VariantScores.All(double.IsFinite)), Is.True);
                Assert.That(File.Exists(reportPath), Is.True);
                Assert.That(File.Exists(scoresPath), Is.True);
            });

            TestContext.WriteLine($"dataset={report.DatasetSha256},subjects={report.SubjectCount},fingers={report.FingerIdentityCount},captures={report.CaptureCount}");
            foreach (VariantResult result in results)
                TestContext.WriteLine($"variant={result.Name},fn={result.VariantFalseNonMatches},rescued={result.RescuedGenuineComparisons},regressed={result.RegressedGenuineComparisons},genuineP05={result.GenuineScores.P05:R},deltaMedian={result.GenuineDeltas.Median:R},fingerImpostorMatches={result.VariantFingerImpostorMatches},impostorP99={result.FingerImpostorScores.P99:R},impostorMax={result.FingerImpostorScores.Maximum:R},meanMinutiae={result.MeanOutputMinutiae:R}");
            TestContext.WriteLine($"report={reportPath}");
            TestContext.WriteLine($"scores={scoresPath}");
        }

        static void Accumulate(
            TrialTotals totals,
            TemplateFusionResult result,
            byte[] serialized,
            byte[] reference,
            FingerprintTemplate restored)
        {
            ++totals.Trials;
            if (restored.ToByteArray().SequenceEqual(serialized))
                ++totals.StableSerializations;
            if (!serialized.SequenceEqual(reference))
                ++totals.DifferentFromReference;
            int contributors = result.Plan.Alignments.Count(alignment => alignment.Accepted);
            if (contributors == 2)
                ++totals.FullPlans;
            else if (contributors == 1)
                ++totals.OneContributorPlans;
            else
                ++totals.ReferenceOnlyPlans;
            totals.ReferenceMinutiae += result.ReferenceMinutiae;
            totals.OutputMinutiae += result.OutputMinutiae;
            totals.MergedReferenceMinutiae += result.MergedReferenceMinutiae;
            totals.SupportedNovelMinutiae += result.RetainedSupportedNovelMinutiae;
            totals.UniqueCoverageMinutiae += result.RetainedUniqueCoverageMinutiae;
            totals.DroppedUnsupportedMinutiae += result.DroppedUnsupportedMinutiae;
            totals.DroppedByLimit += result.DroppedByLimit;
        }

        static VariantResult Summarize(
            Variant variant,
            int index,
            TrialTotals totals,
            IReadOnlyList<ScoreRow> scores,
            double threshold)
        {
            const double tieTolerance = 1e-9;
            ScoreRow[] genuine = scores.Where(score => score.Genuine).ToArray();
            ScoreRow[] impostor = scores.Where(score => !score.Genuine).ToArray();
            double[] genuineScores = genuine.Select(score => score.VariantScores[index]).ToArray();
            double[] genuineDeltas = genuine.Select(score => score.VariantScores[index] - score.BaselineScore).ToArray();
            double[] impostorScores = impostor.Select(score => score.VariantScores[index]).ToArray();
            double[] impostorDeltas = impostor.Select(score => score.VariantScores[index] - score.BaselineScore).ToArray();
            return new(
                variant.Name,
                variant.Options.AverageReferenceMinutiae,
                variant.Options.RetainSupportedNovelMinutiae,
                variant.Options.RetainUniqueCoverageMinutiae,
                totals.Trials,
                totals.StableSerializations,
                totals.DifferentFromReference,
                totals.FullPlans,
                totals.OneContributorPlans,
                totals.ReferenceOnlyPlans,
                totals.ReferenceMinutiae / (double)totals.Trials,
                totals.OutputMinutiae / (double)totals.Trials,
                totals.MergedReferenceMinutiae,
                totals.SupportedNovelMinutiae,
                totals.UniqueCoverageMinutiae,
                totals.DroppedUnsupportedMinutiae,
                totals.DroppedByLimit,
                genuine.Length,
                genuine.Count(score => score.BaselineScore < threshold),
                genuine.Count(score => score.VariantScores[index] < threshold),
                genuine.Count(score => score.BaselineScore < threshold && score.VariantScores[index] >= threshold),
                genuine.Count(score => score.BaselineScore >= threshold && score.VariantScores[index] < threshold),
                genuineDeltas.Count(delta => delta > tieTolerance),
                genuineDeltas.Count(delta => Math.Abs(delta) <= tieTolerance),
                genuineDeltas.Count(delta => delta < -tieTolerance),
                Describe(genuineScores),
                Describe(genuineDeltas),
                impostor.Length,
                impostor.Count(score => score.BaselineScore >= threshold),
                impostorScores.Count(score => score >= threshold),
                Describe(impostorScores),
                Describe(impostorDeltas),
                new[] { 40d, 42d, 45d, 50d, 60d }
                    .Select(candidate => new ThresholdResult(
                        candidate,
                        genuine.Count(score => score.BaselineScore < candidate),
                        genuineScores.Count(score => score < candidate),
                        impostor.Count(score => score.BaselineScore >= candidate),
                        impostorScores.Count(score => score >= candidate)))
                    .ToArray());
        }

        static List<Item> Load(string batch)
        {
            var result = new List<Item>();
            foreach (string reportPath in Directory.GetFiles(batch, "report.json", SearchOption.AllDirectories))
            {
                SessionSnapshot session = JsonSerializer.Deserialize<SessionSnapshot>(
                    File.ReadAllText(reportPath),
                    JsonOptions) ?? throw new InvalidDataException($"Cannot deserialize {reportPath}.");
                string root = Path.GetDirectoryName(reportPath);
                string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string identity = $"{session.SubjectAlias}:{session.FingerCode}";
                foreach (CaptureSnapshot capture in session.Captures)
                {
                    string templatePath = SafePath(root, prefix, capture.SourceAfisTemplateFile);
                    string rawPath = SafePath(root, prefix, capture.RawFile);
                    byte[] serialized = File.ReadAllBytes(templatePath);
                    byte[] raw = File.ReadAllBytes(rawPath);
                    string templateHash = Sha256(serialized);
                    if (!string.Equals(templateHash, capture.SourceAfisTemplateSha256, StringComparison.Ordinal))
                        throw new InvalidDataException($"Template hash mismatch: {templatePath}");
                    if (!string.Equals(Sha256(raw), capture.RawSha256, StringComparison.Ordinal))
                        throw new InvalidDataException($"RAW hash mismatch: {rawPath}");
                    var template = new FingerprintTemplate(serialized);
                    var image = new FingerprintImage(
                        capture.Width,
                        capture.Height,
                        raw,
                        new FingerprintImageOptions { Dpi = capture.Dpi });
                    BooleanMatrix coverage = ExtractInnerMask(image);
                    var reextracted = new FingerprintTemplate(image);
                    result.Add(new(
                        $"{identity}:c{capture.Index:D2}",
                        identity,
                        capture.Index,
                        templateHash,
                        template.ToByteArray().SequenceEqual(serialized),
                        reextracted.ToByteArray().SequenceEqual(serialized),
                        template,
                        coverage));
                }
            }
            return result;
        }

        static string SafePath(string root, string prefix, string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Path escapes its session: {path}");
            return path;
        }

        static BooleanMatrix ExtractInnerMask(FingerprintImage image)
        {
            DoubleMatrix normalized = ImageResizer.Resize(image.Matrix, image.Dpi);
            var blocks = new BlockMap(normalized.Width, normalized.Height, Parameters.BlockSize);
            HistogramCube histogram = LocalHistograms.Create(blocks, normalized);
            BooleanMatrix blockMask = SegmentationMask.Compute(blocks, histogram);
            return SegmentationMask.Inner(SegmentationMask.Pixelwise(blockMask, blocks));
        }

        static Dictionary<(string Probe, string Candidate), double> BuildScoreMatrix(
            IReadOnlyList<Item> items)
        {
            var matrix = new Dictionary<(string Probe, string Candidate), double>();
            foreach (Item probe in items)
            {
                var matcher = new FingerprintMatcher(probe.Template);
                foreach (Item candidate in items)
                {
                    if (!string.Equals(probe.Id, candidate.Id, StringComparison.Ordinal))
                        matrix[(probe.Id, candidate.Id)] = matcher.Match(candidate.Template);
                }
            }
            return matrix;
        }

        static Distribution Describe(IEnumerable<double> source)
        {
            double[] values = source.OrderBy(value => value).ToArray();
            if (values.Length == 0)
                return new(0, null, null, null, null, null, null, null, null);
            return new(
                values.Length,
                values[0],
                Percentile(values, .01),
                Percentile(values, .05),
                Percentile(values, .5),
                values.Average(),
                Percentile(values, .95),
                Percentile(values, .99),
                values[^1]);
        }

        static double Percentile(double[] sorted, double percentile)
        {
            double position = (sorted.Length - 1) * percentile;
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            return lower == upper
                ? sorted[lower]
                : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
        }

        static string Csv(IEnumerable<ScoreRow> scores, IReadOnlyList<Variant> variants)
        {
            var csv = new StringBuilder("trialId,targetIdentity,referenceCapture,probeId,probeIdentity,isGenuine,baselineScore");
            foreach (Variant variant in variants)
                csv.Append(',').Append(variant.Name);
            csv.Append("\r\n");
            foreach (ScoreRow score in scores)
            {
                csv.Append(score.TrialId).Append(',')
                    .Append(score.TargetIdentity).Append(',')
                    .Append(score.ReferenceCapture).Append(',')
                    .Append(score.ProbeId).Append(',')
                    .Append(score.ProbeIdentity).Append(',')
                    .Append(score.Genuine ? "true" : "false").Append(',')
                    .Append(score.BaselineScore.ToString("R", CultureInfo.InvariantCulture));
                foreach (double variantScore in score.VariantScores)
                    csv.Append(',').Append(variantScore.ToString("R", CultureInfo.InvariantCulture));
                csv.Append("\r\n");
            }
            return csv.ToString();
        }

        static string DatasetHash(IEnumerable<Item> items)
        {
            string canonical = string.Join("\n", items
                .OrderBy(item => item.Identity, StringComparer.Ordinal)
                .ThenBy(item => item.Capture)
                .Select(item => $"{item.Id}|{item.TemplateHash}"));
            return Sha256(Encoding.UTF8.GetBytes(canonical));
        }

        static int CombinationsOfThree(int count) => count * (count - 1) * (count - 2) / 6;

        static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
