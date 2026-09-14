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
using SourceAFIS.Engine.Fusion;

namespace SourceAFIS
{
    public class FingerprintFusionLaboratoryTest
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
            public string SourceAfisTemplateFile { get; set; }
            public string SourceAfisTemplateSha256 { get; set; }
        }

        sealed record Item(
            string Id,
            string Identity,
            int Capture,
            string TemplateHash,
            bool ReserializedBytesEqual,
            FingerprintTemplate Template);

        sealed record AlignmentRow(
            int MovingCapture,
            bool Accepted,
            double ConservativeScore,
            double MatcherScore,
            int Pairs,
            double? Rotation,
            double? TranslationX,
            double? TranslationY,
            double? RootMeanSquareError,
            double? MaximumError);

        sealed record TrialRow(
            string TrialId,
            string Identity,
            int[] EnrollmentCaptures,
            int ReferenceCapture,
            IReadOnlyList<AlignmentRow> Alignments);

        sealed record ScoreRow(
            string TrialId,
            string TargetIdentity,
            int ReferenceCapture,
            string ProbeId,
            string ProbeIdentity,
            bool Genuine,
            double Score,
            bool Accepted);

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

        sealed record IdentityAlignmentSummary(
            string Identity,
            int Trials,
            int FullThreeCapturePlans,
            int OneContributorPlans,
            int ReferenceOnlyPlans,
            int AcceptedAlignments,
            int RejectedAlignments);

        sealed record LaboratoryReport(
            int SchemaVersion,
            DateTimeOffset GeneratedUtc,
            string DatasetSha256,
            string SourceAfisVersion,
            string DahomeyCborVersion,
            string ImageSharpVersion,
            double AlignmentScoreThreshold,
            int MinimumMatchedMinutiae,
            int IdentityCount,
            int CaptureCount,
            int LegacyTemplatesRead,
            int ByteStableReserializations,
            int TrialCount,
            int FullThreeCapturePlans,
            int OneContributorPlans,
            int ReferenceOnlyPlans,
            int AcceptedAlignments,
            int RejectedAlignments,
            int GenuineComparisons,
            int ImpostorComparisons,
            int FalseNonMatches,
            int FalseMatches,
            Distribution GenuineScores,
            Distribution ImpostorScores,
            long ElapsedMilliseconds,
            IReadOnlyList<IdentityAlignmentSummary> Identities,
            IReadOnlyList<TrialRow> Trials);

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        [Test, Explicit("Reads a local biometric laboratory batch selected through an environment variable.")]
        public void GeneratesImmutableMedoidBaselineForEveryEnrollmentTriplet()
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
            var options = new TemplateFusionOptions(threshold, 8);
            Dictionary<(string Probe, string Candidate), double> scoreMatrix = BuildScoreMatrix(items);
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
                            TemplateFusionPlan plan = TemplateFusionPlanner.Plan(
                                enrollment.Select(item => item.Template).ToArray(),
                                options);
                            Item reference = enrollment[plan.Reference];

                            AlignmentRow[] alignments = plan.Alignments
                                .Select(alignment => new AlignmentRow(
                                    enrollment[alignment.Template].Capture,
                                    alignment.Accepted,
                                    alignment.ConservativeScore,
                                    alignment.Alignment.Score,
                                    alignment.Alignment.Pairs.Count,
                                    alignment.Alignment.TransformAvailable ? alignment.Alignment.Transform.Rotation : null,
                                    alignment.Alignment.TransformAvailable ? alignment.Alignment.Transform.TranslationX : null,
                                    alignment.Alignment.TransformAvailable ? alignment.Alignment.Transform.TranslationY : null,
                                    alignment.Alignment.TransformAvailable ? alignment.Alignment.RootMeanSquareError : null,
                                    alignment.Alignment.TransformAvailable ? alignment.Alignment.MaximumError : null))
                                .ToArray();
                            trials.Add(new(
                                trialId,
                                identity[0].Identity,
                                enrollment.Select(item => item.Capture).ToArray(),
                                reference.Capture,
                                alignments));

                            var enrollmentIds = enrollment.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                            IEnumerable<Item> probes = identity
                                .Where(item => !enrollmentIds.Contains(item.Id))
                                .Concat(items.Where(item => !string.Equals(
                                    item.Identity,
                                    identity[0].Identity,
                                    StringComparison.Ordinal)));
                            foreach (Item probe in probes)
                            {
                                double score = scoreMatrix[(probe.Id, reference.Id)];
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
                                    score,
                                    score >= threshold));
                            }
                        }
                    }
                }
            }

            stopwatch.Stop();
            ScoreRow[] genuine = scores.Where(score => score.Genuine).ToArray();
            ScoreRow[] impostor = scores.Where(score => !score.Genuine).ToArray();
            IdentityAlignmentSummary[] identities = trials
                .GroupBy(trial => trial.Identity, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => Summarize(group.Key, group.ToArray()))
                .ToArray();
            var report = new LaboratoryReport(
                1,
                DateTimeOffset.UtcNow,
                DatasetHash(items),
                FingerprintCompatibility.Version,
                VersionOf(typeof(Dahomey.Cbor.CborOptions).Assembly),
                VersionOf(typeof(SixLabors.ImageSharp.Image).Assembly),
                options.MinimumAlignmentScore,
                options.MinimumMatchedMinutiae,
                groups.Length,
                items.Count,
                items.Count,
                items.Count(item => item.ReserializedBytesEqual),
                trials.Count,
                trials.Count(trial => trial.Alignments.Count(alignment => alignment.Accepted) == 2),
                trials.Count(trial => trial.Alignments.Count(alignment => alignment.Accepted) == 1),
                trials.Count(trial => trial.Alignments.All(alignment => !alignment.Accepted)),
                trials.Sum(trial => trial.Alignments.Count(alignment => alignment.Accepted)),
                trials.Sum(trial => trial.Alignments.Count(alignment => !alignment.Accepted)),
                genuine.Length,
                impostor.Length,
                genuine.Count(score => !score.Accepted),
                impostor.Count(score => score.Accepted),
                Describe(genuine.Select(score => score.Score)),
                Describe(impostor.Select(score => score.Score)),
                stopwatch.ElapsedMilliseconds,
                identities,
                trials);

            string reportPath = Path.Combine(batch, "fusion-baseline-report.json");
            string scoresPath = Path.Combine(batch, "fusion-baseline-scores.csv");
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
                Assert.That(report.AcceptedAlignments + report.RejectedAlignments, Is.EqualTo(2 * expectedTrials));
                Assert.That(scores.All(score => double.IsFinite(score.Score)), Is.True);
                Assert.That(report.DatasetSha256, Does.Match("^[0-9a-f]{64}$"));
                Assert.That(File.Exists(reportPath), Is.True);
                Assert.That(File.Exists(scoresPath), Is.True);
            });

            TestContext.WriteLine($"dataset={report.DatasetSha256}");
            TestContext.WriteLine($"captures={report.CaptureCount},trials={report.TrialCount},full={report.FullThreeCapturePlans},one={report.OneContributorPlans},referenceOnly={report.ReferenceOnlyPlans}");
            TestContext.WriteLine($"alignmentsAccepted={report.AcceptedAlignments},alignmentsRejected={report.RejectedAlignments}");
            TestContext.WriteLine($"genuine={report.GenuineComparisons},fn={report.FalseNonMatches},p05={report.GenuineScores.P05:R},median={report.GenuineScores.Median:R}");
            TestContext.WriteLine($"impostor={report.ImpostorComparisons},fm={report.FalseMatches},p99={report.ImpostorScores.P99:R},max={report.ImpostorScores.Maximum:R}");
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
                    string path = Path.GetFullPath(Path.Combine(root, capture.SourceAfisTemplateFile));
                    if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Template path escapes its session: {path}");
                    byte[] serialized = File.ReadAllBytes(path);
                    string hash = Sha256(serialized);
                    if (!string.Equals(hash, capture.SourceAfisTemplateSha256, StringComparison.Ordinal))
                        throw new InvalidDataException($"Template hash mismatch: {path}");
                    var template = new FingerprintTemplate(serialized);
                    result.Add(new(
                        $"{identity}:c{capture.Index:D2}",
                        identity,
                        capture.Index,
                        hash,
                        template.ToByteArray().SequenceEqual(serialized),
                        template));
                }
            }
            return result;
        }

        static IdentityAlignmentSummary Summarize(string identity, TrialRow[] trials)
        {
            int accepted = trials.Sum(trial => trial.Alignments.Count(alignment => alignment.Accepted));
            return new(
                identity,
                trials.Length,
                trials.Count(trial => trial.Alignments.Count(alignment => alignment.Accepted) == 2),
                trials.Count(trial => trial.Alignments.Count(alignment => alignment.Accepted) == 1),
                trials.Count(trial => trial.Alignments.All(alignment => !alignment.Accepted)),
                accepted,
                2 * trials.Length - accepted);
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
            var csv = new StringBuilder("trialId,targetIdentity,referenceCapture,probeId,probeIdentity,isGenuine,score,accepted\r\n");
            foreach (ScoreRow score in scores)
                csv.Append(score.TrialId).Append(',')
                    .Append(score.TargetIdentity).Append(',')
                    .Append(score.ReferenceCapture).Append(',')
                    .Append(score.ProbeId).Append(',')
                    .Append(score.ProbeIdentity).Append(',')
                    .Append(score.Genuine ? "true" : "false").Append(',')
                    .Append(score.Score.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(score.Accepted ? "true" : "false").Append("\r\n");
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
