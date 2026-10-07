// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SourceAFIS.Engine.Configuration;
using SourceAFIS.Engine.Extractor;
using SourceAFIS.Engine.Fusion;
using SourceAFIS.Engine.Primitives;

namespace SourceAFIS
{
    public class PublicFusionValidationLaboratoryTest
    {
        const string DatasetVariable = "SOURCEAFIS_PUBLIC_FUSION_DATASET";
        const string OutputVariable = "SOURCEAFIS_PUBLIC_FUSION_OUTPUT";
        const double Dpi = 500;
        const double Threshold = 40;
        static readonly Regex FilePattern = new(
            @"^(?<person>\d+)_(?<finger>\d+)_(?<capture>\d+)\.tif$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        sealed record Item(
            string Id,
            string Identity,
            int Capture,
            FingerprintTemplate Template,
            BooleanMatrix Coverage,
            string ImageSha256);

        sealed record Variant(string Name, TemplateFuserOptions Options);

        sealed record ScoreRow(
            string TrialId,
            string TargetIdentity,
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

        sealed record VariantResult(
            string Name,
            double AnchoredReferenceBlend,
            int TrialCount,
            int StableSerializations,
            double MeanOutputMinutiae,
            int GenuineComparisons,
            int BaselineFalseNonMatches,
            int VariantFalseNonMatches,
            int RescuedGenuineComparisons,
            int RegressedGenuineComparisons,
            Distribution GenuineScores,
            Distribution GenuineDeltas,
            int ImpostorComparisons,
            int BaselineFalseMatches,
            int VariantFalseMatches,
            int NewFalseMatches,
            int RemovedFalseMatches,
            Distribution ImpostorScores,
            Distribution ImpostorDeltas);

        sealed record ValidationReport(
            int SchemaVersion,
            DateTimeOffset GeneratedUtc,
            string DatasetName,
            string DatasetSha256,
            string SourceAfisVersion,
            double Dpi,
            double DecisionThreshold,
            int IdentityCount,
            int CaptureCount,
            IReadOnlyList<int> EnrollmentCaptures,
            IReadOnlyList<int> GenuineProbeCaptures,
            int ImpostorProbeCapture,
            int TrialCount,
            int FullThreeCapturePlans,
            int OneContributorPlans,
            int ReferenceOnlyPlans,
            long ElapsedMilliseconds,
            IReadOnlyList<VariantResult> Variants);

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        [Test, Explicit("Validates the frozen anchored fusion candidate on an external public dataset.")]
        public void ValidatesFrozenCandidateOnCrossMatchDataset()
        {
            string dataset = RequiredDirectory(DatasetVariable);
            string output = RequiredDirectory(OutputVariable, create: true);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            List<Item> items = Load(dataset);
            Item[][] identities = items
                .GroupBy(item => item.Identity, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.OrderBy(item => item.Capture).ToArray())
                .ToArray();
            Assert.That(identities, Has.Length.EqualTo(51));
            Assert.That(identities.All(identity => identity.Length == 8), Is.True);
            Assert.That(identities.All(identity => identity.Select(item => item.Capture)
                .SequenceEqual(Enumerable.Range(1, 8))), Is.True);

            int[] enrollmentCaptures = [1, 2, 3, 4, 5, 6];
            int[] genuineProbeCaptures = [7, 8];
            const int impostorProbeCapture = 8;
            var planning = new TemplateFusionOptions(Threshold, 8);
            Variant[] variants =
            [
                new("full-anchored", new(
                    planning,
                    AverageReferenceMinutiae: false,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: true,
                    AnchoredReferenceBlend: 0)),
                new("full-anchored-blend25", new(
                    planning,
                    AverageReferenceMinutiae: false,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: true,
                    AnchoredReferenceBlend: .25))
            ];
            Dictionary<string, FingerprintMatcher> matchers = items
                .Where(item => genuineProbeCaptures.Contains(item.Capture))
                .ToDictionary(item => item.Id, item => new FingerprintMatcher(item.Template), StringComparer.Ordinal);
            var stable = new int[variants.Length];
            var outputMinutiae = new long[variants.Length];
            var scores = new List<ScoreRow>();
            int trials = 0;
            int fullPlans = 0;
            int oneContributorPlans = 0;
            int referenceOnlyPlans = 0;

            foreach (Item[] identity in identities)
            {
                Item[] enrollmentPool = identity
                    .Where(item => enrollmentCaptures.Contains(item.Capture))
                    .ToArray();
                for (int first = 0; first < enrollmentPool.Length - 2; ++first)
                {
                    for (int second = first + 1; second < enrollmentPool.Length - 1; ++second)
                    {
                        for (int third = second + 1; third < enrollmentPool.Length; ++third)
                        {
                            Item[] enrollment = [enrollmentPool[first], enrollmentPool[second], enrollmentPool[third]];
                            TemplateFusionInput[] inputs = enrollment
                                .Select(item => new TemplateFusionInput(item.Template, item.Coverage))
                                .ToArray();
                            string trialId = $"{identity[0].Identity}:e{enrollment[0].Capture}-{enrollment[1].Capture}-{enrollment[2].Capture}";
                            var fused = new FingerprintTemplate[variants.Length];
                            int referenceIndex = -1;
                            for (int variant = 0; variant < variants.Length; ++variant)
                            {
                                TemplateFusionResult result = TemplateFuser.Fuse(inputs, variants[variant].Options);
                                if (referenceIndex < 0)
                                {
                                    referenceIndex = result.Plan.Reference;
                                    int contributors = result.Plan.Alignments.Count(alignment => alignment.Accepted);
                                    if (contributors == 2)
                                        ++fullPlans;
                                    else if (contributors == 1)
                                        ++oneContributorPlans;
                                    else
                                        ++referenceOnlyPlans;
                                }
                                else
                                    Assert.That(result.Plan.Reference, Is.EqualTo(referenceIndex));
                                byte[] serialized = result.Template.ToByteArray();
                                fused[variant] = new FingerprintTemplate(serialized);
                                if (fused[variant].ToByteArray().SequenceEqual(serialized))
                                    ++stable[variant];
                                outputMinutiae[variant] += result.OutputMinutiae;
                            }
                            ++trials;
                            Item reference = enrollment[referenceIndex];
                            IEnumerable<Item> probes = identity
                                .Where(item => genuineProbeCaptures.Contains(item.Capture))
                                .Concat(identities
                                    .Where(other => !string.Equals(other[0].Identity, identity[0].Identity, StringComparison.Ordinal))
                                    .Select(other => other.Single(item => item.Capture == impostorProbeCapture)));
                            foreach (Item probe in probes)
                            {
                                FingerprintMatcher matcher = matchers[probe.Id];
                                double baseline = matcher.Match(reference.Template);
                                scores.Add(new(
                                    trialId,
                                    identity[0].Identity,
                                    probe.Id,
                                    probe.Identity,
                                    string.Equals(probe.Identity, identity[0].Identity, StringComparison.Ordinal),
                                    baseline,
                                    fused.Select(matcher.Match).ToArray()));
                            }
                        }
                    }
                }
            }

            stopwatch.Stop();
            VariantResult[] results = variants.Select((variant, index) =>
                Summarize(variant, index, trials, stable[index], outputMinutiae[index], scores)).ToArray();
            var report = new ValidationReport(
                1,
                DateTimeOffset.UtcNow,
                "Neurotechnology CrossMatch Sample DB",
                DatasetHash(items),
                FingerprintCompatibility.Version,
                Dpi,
                Threshold,
                identities.Length,
                items.Count,
                enrollmentCaptures,
                genuineProbeCaptures,
                impostorProbeCapture,
                trials,
                fullPlans,
                oneContributorPlans,
                referenceOnlyPlans,
                stopwatch.ElapsedMilliseconds,
                results);
            string reportPath = Path.Combine(output, "public-fusion-validation-report.json");
            string scoresPath = Path.Combine(output, "public-fusion-validation-scores.csv");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
            File.WriteAllText(scoresPath, Csv(scores, variants), new UTF8Encoding(false));

            int expectedTrials = identities.Length * CombinationsOfThree(enrollmentCaptures.Length);
            Assert.Multiple(() =>
            {
                Assert.That(trials, Is.EqualTo(expectedTrials));
                Assert.That(stable.All(count => count == trials), Is.True);
                Assert.That(scores.All(score => double.IsFinite(score.BaselineScore)
                    && score.VariantScores.All(double.IsFinite)), Is.True);
                Assert.That(File.Exists(reportPath), Is.True);
                Assert.That(File.Exists(scoresPath), Is.True);
            });
            foreach (VariantResult result in results)
                TestContext.WriteLine($"variant={result.Name},fn={result.VariantFalseNonMatches},rescued={result.RescuedGenuineComparisons},regressed={result.RegressedGenuineComparisons},genuineP05={result.GenuineScores.P05:R},deltaMedian={result.GenuineDeltas.Median:R},fm={result.VariantFalseMatches},impostorP99={result.ImpostorScores.P99:R},impostorMax={result.ImpostorScores.Maximum:R},meanMinutiae={result.MeanOutputMinutiae:R}");
            TestContext.WriteLine($"report={reportPath}");
            TestContext.WriteLine($"scores={scoresPath}");
        }

        static VariantResult Summarize(
            Variant variant,
            int index,
            int trials,
            int stable,
            long outputMinutiae,
            IReadOnlyList<ScoreRow> scores)
        {
            ScoreRow[] genuine = scores.Where(score => score.Genuine).ToArray();
            ScoreRow[] impostor = scores.Where(score => !score.Genuine).ToArray();
            double[] genuineScores = genuine.Select(score => score.VariantScores[index]).ToArray();
            double[] genuineDeltas = genuine.Select(score => score.VariantScores[index] - score.BaselineScore).ToArray();
            double[] impostorScores = impostor.Select(score => score.VariantScores[index]).ToArray();
            double[] impostorDeltas = impostor.Select(score => score.VariantScores[index] - score.BaselineScore).ToArray();
            return new(
                variant.Name,
                variant.Options.AnchoredReferenceBlend,
                trials,
                stable,
                outputMinutiae / (double)trials,
                genuine.Length,
                genuine.Count(score => score.BaselineScore < Threshold),
                genuineScores.Count(score => score < Threshold),
                genuine.Count(score => score.BaselineScore < Threshold && score.VariantScores[index] >= Threshold),
                genuine.Count(score => score.BaselineScore >= Threshold && score.VariantScores[index] < Threshold),
                Describe(genuineScores),
                Describe(genuineDeltas),
                impostor.Length,
                impostor.Count(score => score.BaselineScore >= Threshold),
                impostorScores.Count(score => score >= Threshold),
                impostor.Count(score => score.BaselineScore < Threshold && score.VariantScores[index] >= Threshold),
                impostor.Count(score => score.BaselineScore >= Threshold && score.VariantScores[index] < Threshold),
                Describe(impostorScores),
                Describe(impostorDeltas));
        }

        static List<Item> Load(string dataset)
        {
            var items = new List<Item>();
            foreach (string path in Directory.GetFiles(dataset, "*.tif", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                Match match = FilePattern.Match(Path.GetFileName(path));
                if (!match.Success)
                    throw new InvalidDataException($"Unexpected CrossMatch filename: {path}");
                string identity = $"{match.Groups["person"].Value}_{match.Groups["finger"].Value}";
                int capture = int.Parse(match.Groups["capture"].Value, CultureInfo.InvariantCulture);
                byte[] encoded = File.ReadAllBytes(path);
                var image = new FingerprintImage(encoded, new FingerprintImageOptions { Dpi = Dpi });
                items.Add(new(
                    $"{identity}:{capture}",
                    identity,
                    capture,
                    new FingerprintTemplate(image),
                    ExtractInnerMask(image),
                    Sha256(encoded)));
            }
            return items;
        }

        static BooleanMatrix ExtractInnerMask(FingerprintImage image)
        {
            DoubleMatrix normalized = ImageResizer.Resize(image.Matrix, image.Dpi);
            var blocks = new BlockMap(normalized.Width, normalized.Height, Parameters.BlockSize);
            HistogramCube histogram = LocalHistograms.Create(blocks, normalized);
            BooleanMatrix blockMask = SegmentationMask.Compute(blocks, histogram);
            return SegmentationMask.Inner(SegmentationMask.Pixelwise(blockMask, blocks));
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

        static string DatasetHash(IEnumerable<Item> items) => Sha256(Encoding.UTF8.GetBytes(string.Join("\n",
            items.OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => $"{item.Id}|{item.ImageSha256}"))));

        static string Csv(IEnumerable<ScoreRow> scores, IReadOnlyList<Variant> variants)
        {
            var csv = new StringBuilder("trialId,targetIdentity,probeId,probeIdentity,isGenuine,baselineScore");
            foreach (Variant variant in variants)
                csv.Append(',').Append(variant.Name);
            csv.Append("\r\n");
            foreach (ScoreRow score in scores)
            {
                csv.Append(score.TrialId).Append(',')
                    .Append(score.TargetIdentity).Append(',')
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

        static string RequiredDirectory(string variable, bool create = false)
        {
            string value = Environment.GetEnvironmentVariable(variable);
            Assert.That(value, Is.Not.Null.And.Not.Empty, $"Set {variable}.");
            string path = Path.GetFullPath(value);
            if (create)
                Directory.CreateDirectory(path);
            Assert.That(Directory.Exists(path), Is.True, $"Directory does not exist: {path}");
            return path;
        }

        static int CombinationsOfThree(int count) => count * (count - 1) * (count - 2) / 6;

        static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
