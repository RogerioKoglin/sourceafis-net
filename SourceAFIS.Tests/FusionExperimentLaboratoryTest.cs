// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
    public class FusionExperimentLaboratoryTest
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

        sealed record TrialRow(
            string TrialId,
            string Identity,
            int[] EnrollmentCaptures,
            int ReferenceCapture,
            int AcceptedContributors,
            int ReferenceMinutiae,
            int OutputMinutiae,
            int MergedReferenceMinutiae,
            int RetainedSupportedNovelMinutiae,
            int RetainedUniqueCoverageMinutiae,
            int DroppedUnsupportedMinutiae,
            int DroppedByLimit,
            bool SerializationBytesStable,
            bool DifferentFromReference,
            string FusedTemplateSha256);

        sealed record ScoreRow(
            string TrialId,
            string TargetIdentity,
            int ReferenceCapture,
            string ProbeId,
            string ProbeIdentity,
            bool Genuine,
            double BaselineScore,
            double FusedScore,
            double Delta,
            bool BaselineAccepted,
            bool FusedAccepted);

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

        sealed record FusionExperimentReport(
            int SchemaVersion,
            DateTimeOffset GeneratedUtc,
            string DatasetSha256,
            string SourceAfisVersion,
            string DahomeyCborVersion,
            string ImageSharpVersion,
            double DecisionThreshold,
            double MinimumAlignmentScore,
            int MinimumMatchedMinutiae,
            double MaximumPairPositionError,
            double DuplicatePositionRadius,
            double DuplicateDirectionToleranceDegrees,
            int MinimumNovelSupport,
            int MaximumMinutiae,
            int IdentityCount,
            int CaptureCount,
            int LegacyTemplatesRead,
            int LegacyByteStableReserializations,
            int RawByteStableReextractions,
            int TrialCount,
            int StableFusedSerializations,
            int FusedTemplatesDifferentFromReference,
            int FullThreeCapturePlans,
            int OneContributorPlans,
            int ReferenceOnlyPlans,
            int TotalReferenceMinutiae,
            int TotalOutputMinutiae,
            int TotalMergedReferenceMinutiae,
            int TotalRetainedSupportedNovelMinutiae,
            int TotalRetainedUniqueCoverageMinutiae,
            int TotalDroppedUnsupportedMinutiae,
            int TotalDroppedByLimit,
            int GenuineComparisons,
            int BaselineFalseNonMatches,
            int FusedFalseNonMatches,
            int RescuedGenuineComparisons,
            int RegressedGenuineComparisons,
            int GenuineWins,
            int GenuineTies,
            int GenuineLosses,
            Distribution BaselineGenuineScores,
            Distribution FusedGenuineScores,
            Distribution GenuineScoreDeltas,
            int ImpostorComparisons,
            int BaselineFalseMatches,
            int FusedFalseMatches,
            int NewFalseMatches,
            int RemovedFalseMatches,
            Distribution BaselineImpostorScores,
            Distribution FusedImpostorScores,
            Distribution ImpostorScoreDeltas,
            long ElapsedMilliseconds,
            IReadOnlyList<TrialRow> Trials);

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        [Test, Explicit("Runs the first fused-template experiment on a local biometric laboratory batch.")]
        public void ComparesConservativeFusionAgainstImmutableMedoidBaseline()
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
            Assert.That(groups, Is.Not.Empty);
            Assert.That(groups.All(group => group.Length >= 4), Is.True,
                "Every identity needs at least three enrollment captures and one held-out probe.");

            const double threshold = 40;
            var planning = new TemplateFusionOptions(threshold, 8);
            var options = new TemplateFuserOptions(planning);
            Dictionary<(string Probe, string Candidate), double> baseline = BuildScoreMatrix(items);
            Dictionary<string, FingerprintMatcher> probeMatchers = items.ToDictionary(
                item => item.Id,
                item => new FingerprintMatcher(item.Template),
                StringComparer.Ordinal);
            var trials = new List<TrialRow>();
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
                            string trialId = $"{identity[0].Identity}:e{enrollment[0].Capture:D2}-{enrollment[1].Capture:D2}-{enrollment[2].Capture:D2}";
                            TemplateFusionResult fusion = TemplateFuser.Fuse(
                                enrollment.Select(item => new TemplateFusionInput(
                                    item.Template,
                                    item.Coverage)).ToArray(),
                                options);
                            Item reference = enrollment[fusion.Plan.Reference];
                            byte[] serialized = fusion.Template.ToByteArray();
                            var restored = new FingerprintTemplate(serialized);
                            bool stable = restored.ToByteArray().SequenceEqual(serialized);
                            bool different = !serialized.SequenceEqual(reference.Template.ToByteArray());
                            trials.Add(new(
                                trialId,
                                identity[0].Identity,
                                enrollment.Select(item => item.Capture).ToArray(),
                                reference.Capture,
                                fusion.Plan.Alignments.Count(alignment => alignment.Accepted),
                                fusion.ReferenceMinutiae,
                                fusion.OutputMinutiae,
                                fusion.MergedReferenceMinutiae,
                                fusion.RetainedSupportedNovelMinutiae,
                                fusion.RetainedUniqueCoverageMinutiae,
                                fusion.DroppedUnsupportedMinutiae,
                                fusion.DroppedByLimit,
                                stable,
                                different,
                                Sha256(serialized)));

                            var enrollmentIds = enrollment.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                            IEnumerable<Item> probes = identity
                                .Where(item => !enrollmentIds.Contains(item.Id))
                                .Concat(items.Where(item => !string.Equals(
                                    item.Identity,
                                    identity[0].Identity,
                                    StringComparison.Ordinal)));
                            foreach (Item probe in probes)
                            {
                                double baselineScore = baseline[(probe.Id, reference.Id)];
                                double fusedScore = probeMatchers[probe.Id].Match(restored);
                                bool isGenuine = string.Equals(
                                    probe.Identity,
                                    identity[0].Identity,
                                    StringComparison.Ordinal);
                                scores.Add(new(
                                    trialId,
                                    identity[0].Identity,
                                    reference.Capture,
                                    probe.Id,
                                    probe.Identity,
                                    isGenuine,
                                    baselineScore,
                                    fusedScore,
                                    fusedScore - baselineScore,
                                    baselineScore >= threshold,
                                    fusedScore >= threshold));
                            }
                        }
                    }
                }
            }

            stopwatch.Stop();
            const double tieTolerance = 1e-9;
            ScoreRow[] genuine = scores.Where(score => score.Genuine).ToArray();
            ScoreRow[] impostor = scores.Where(score => !score.Genuine).ToArray();
            var report = new FusionExperimentReport(
                1,
                DateTimeOffset.UtcNow,
                DatasetHash(items),
                FingerprintCompatibility.Version,
                VersionOf(typeof(Dahomey.Cbor.CborOptions).Assembly),
                VersionOf(typeof(SixLabors.ImageSharp.Image).Assembly),
                threshold,
                planning.MinimumAlignmentScore,
                planning.MinimumMatchedMinutiae,
                options.MaximumPairPositionError,
                options.DuplicatePositionRadius,
                options.DuplicateDirectionTolerance * 180 / Math.PI,
                options.MinimumNovelSupport,
                options.MaximumMinutiae,
                groups.Length,
                items.Count,
                items.Count,
                items.Count(item => item.LegacyRoundTripBytesEqual),
                items.Count(item => item.RawReextractionBytesEqual),
                trials.Count,
                trials.Count(trial => trial.SerializationBytesStable),
                trials.Count(trial => trial.DifferentFromReference),
                trials.Count(trial => trial.AcceptedContributors == 2),
                trials.Count(trial => trial.AcceptedContributors == 1),
                trials.Count(trial => trial.AcceptedContributors == 0),
                trials.Sum(trial => trial.ReferenceMinutiae),
                trials.Sum(trial => trial.OutputMinutiae),
                trials.Sum(trial => trial.MergedReferenceMinutiae),
                trials.Sum(trial => trial.RetainedSupportedNovelMinutiae),
                trials.Sum(trial => trial.RetainedUniqueCoverageMinutiae),
                trials.Sum(trial => trial.DroppedUnsupportedMinutiae),
                trials.Sum(trial => trial.DroppedByLimit),
                genuine.Length,
                genuine.Count(score => !score.BaselineAccepted),
                genuine.Count(score => !score.FusedAccepted),
                genuine.Count(score => !score.BaselineAccepted && score.FusedAccepted),
                genuine.Count(score => score.BaselineAccepted && !score.FusedAccepted),
                genuine.Count(score => score.Delta > tieTolerance),
                genuine.Count(score => Math.Abs(score.Delta) <= tieTolerance),
                genuine.Count(score => score.Delta < -tieTolerance),
                Describe(genuine.Select(score => score.BaselineScore)),
                Describe(genuine.Select(score => score.FusedScore)),
                Describe(genuine.Select(score => score.Delta)),
                impostor.Length,
                impostor.Count(score => score.BaselineAccepted),
                impostor.Count(score => score.FusedAccepted),
                impostor.Count(score => !score.BaselineAccepted && score.FusedAccepted),
                impostor.Count(score => score.BaselineAccepted && !score.FusedAccepted),
                Describe(impostor.Select(score => score.BaselineScore)),
                Describe(impostor.Select(score => score.FusedScore)),
                Describe(impostor.Select(score => score.Delta)),
                stopwatch.ElapsedMilliseconds,
                trials);

            string reportPath = Path.Combine(batch, "fusion-v1-report.json");
            string scoresPath = Path.Combine(batch, "fusion-v1-scores.csv");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
            File.WriteAllText(scoresPath, Csv(scores), new UTF8Encoding(false));

            int expectedTrials = groups.Sum(group => CombinationsOfThree(group.Length));
            int expectedGenuine = groups.Sum(group => CombinationsOfThree(group.Length) * (group.Length - 3));
            int expectedImpostor = groups.Sum(group =>
                CombinationsOfThree(group.Length) * (items.Count - group.Length));
            Assert.Multiple(() =>
            {
                Assert.That(report.TrialCount, Is.EqualTo(expectedTrials));
                Assert.That(report.GenuineComparisons, Is.EqualTo(expectedGenuine));
                Assert.That(report.ImpostorComparisons, Is.EqualTo(expectedImpostor));
                Assert.That(report.LegacyByteStableReserializations, Is.EqualTo(items.Count));
                Assert.That(report.RawByteStableReextractions, Is.EqualTo(items.Count));
                Assert.That(report.StableFusedSerializations, Is.EqualTo(report.TrialCount));
                Assert.That(scores.All(score =>
                    double.IsFinite(score.BaselineScore)
                    && double.IsFinite(score.FusedScore)
                    && double.IsFinite(score.Delta)), Is.True);
                Assert.That(File.Exists(reportPath), Is.True);
                Assert.That(File.Exists(scoresPath), Is.True);
            });

            TestContext.WriteLine($"dataset={report.DatasetSha256}");
            TestContext.WriteLine($"captures={report.CaptureCount},rawReextracted={report.RawByteStableReextractions},trials={report.TrialCount}");
            TestContext.WriteLine($"plans=full:{report.FullThreeCapturePlans},one:{report.OneContributorPlans},referenceOnly:{report.ReferenceOnlyPlans}");
            TestContext.WriteLine($"minutiae=reference:{report.TotalReferenceMinutiae},output:{report.TotalOutputMinutiae},merged:{report.TotalMergedReferenceMinutiae},supportedNovel:{report.TotalRetainedSupportedNovelMinutiae},uniqueCoverage:{report.TotalRetainedUniqueCoverageMinutiae},dropped:{report.TotalDroppedUnsupportedMinutiae}");
            TestContext.WriteLine($"genuine={report.GenuineComparisons},baselineFn:{report.BaselineFalseNonMatches},fusedFn:{report.FusedFalseNonMatches},rescued:{report.RescuedGenuineComparisons},regressed:{report.RegressedGenuineComparisons}");
            TestContext.WriteLine($"genuineP05=baseline:{report.BaselineGenuineScores.P05:R},fused:{report.FusedGenuineScores.P05:R},deltaMedian:{report.GenuineScoreDeltas.Median:R}");
            TestContext.WriteLine($"impostor={report.ImpostorComparisons},baselineFm:{report.BaselineFalseMatches},fusedFm:{report.FusedFalseMatches},p99=baseline:{report.BaselineImpostorScores.P99:R},fused:{report.FusedImpostorScores.P99:R},maxFused:{report.FusedImpostorScores.Maximum:R}");
            TestContext.WriteLine($"report={reportPath}");
            TestContext.WriteLine($"scores={scoresPath}");
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
                string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string identity = $"{session.SubjectAlias}:{session.FingerCode}";
                foreach (CaptureSnapshot capture in session.Captures)
                {
                    string templatePath = SafePath(root, rootPrefix, capture.SourceAfisTemplateFile);
                    string rawPath = SafePath(root, rootPrefix, capture.RawFile);
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

        static string SafePath(string root, string rootPrefix, string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
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
            var matrix = new Dictionary<(string Probe, string Candidate), double>(
                items.Count * (items.Count - 1));
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

        static string DatasetHash(IEnumerable<Item> items)
        {
            string canonical = string.Join("\n", items
                .OrderBy(item => item.Identity, StringComparer.Ordinal)
                .ThenBy(item => item.Capture)
                .Select(item => $"{item.Id}|{item.TemplateHash}"));
            return Sha256(Encoding.UTF8.GetBytes(canonical));
        }

        static string Csv(IEnumerable<ScoreRow> scores)
        {
            var csv = new StringBuilder("trialId,targetIdentity,referenceCapture,probeId,probeIdentity,isGenuine,baselineScore,fusedScore,delta,baselineAccepted,fusedAccepted\r\n");
            foreach (ScoreRow score in scores)
                csv.Append(score.TrialId).Append(',')
                    .Append(score.TargetIdentity).Append(',')
                    .Append(score.ReferenceCapture).Append(',')
                    .Append(score.ProbeId).Append(',')
                    .Append(score.ProbeIdentity).Append(',')
                    .Append(score.Genuine ? "true" : "false").Append(',')
                    .Append(score.BaselineScore.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(score.FusedScore.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(score.Delta.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(score.BaselineAccepted ? "true" : "false").Append(',')
                    .Append(score.FusedAccepted ? "true" : "false").Append("\r\n");
            return csv.ToString();
        }

        static int CombinationsOfThree(int count) => count * (count - 1) * (count - 2) / 6;

        static string VersionOf(Assembly assembly)
        {
            string informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+')[0];
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
