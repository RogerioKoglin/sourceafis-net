// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Collections.Generic;
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
    public class CrossDeviceFusionLaboratoryTest
    {
        const string RootVariable = "SOURCEAFIS_CROSS_DEVICE_LAB_ROOT";
        const string PrefixVariable = "SOURCEAFIS_CROSS_DEVICE_BATCH_PREFIX";
        const string OutputVariable = "SOURCEAFIS_CROSS_DEVICE_OUTPUT";
        const double Threshold = 40;
        const double TurnstileDpi = 500;

        sealed class SessionSnapshot
        {
            public string SubjectAlias { get; set; }
            public string FingerCode { get; set; }
            public string SourceAfisVersion { get; set; }
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
            public double? ControlIdQuality { get; set; }
            public double? Nfiq2Score { get; set; }
            public int FinalMinutiae { get; set; }
        }

        sealed class TurnstileSnapshot
        {
            public string SubjectAlias { get; set; }
            public List<TurnstileCaptureSnapshot> Captures { get; set; }
        }

        sealed class TurnstileCaptureSnapshot
        {
            public int Index { get; set; }
            public string RawFile { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int ImageBytes { get; set; }
            public string RawSha256 { get; set; }
            public double? Variance { get; set; }
        }

        sealed record UsbItem(
            string Id,
            int Capture,
            FingerprintTemplate Template,
            BooleanMatrix Coverage,
            bool SerializationBytesStable,
            bool RawReextractionBytesStable,
            double? ControlIdQuality,
            double? Nfiq2Score,
            int FinalMinutiae);

        sealed record TurnstileProbe(
            string Id,
            string Identity,
            FingerprintTemplate Template,
            double? Variance);

        sealed record IdentityData(
            string Batch,
            string Identity,
            string SubjectAlias,
            string FingerCode,
            string SourceAfisVersion,
            IReadOnlyList<UsbItem> Usb,
            IReadOnlyList<TurnstileProbe> Turnstile);

        sealed record TrialRow(
            string TargetIdentity,
            int AcceptedContributors,
            bool SerializationBytesStable);

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

        sealed record FusionVariant(
            string Name,
            TemplateFuserOptions Options);

        sealed record AblationScoreRow(
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

        sealed record IdentitySummary(
            string Batch,
            string Identity,
            string SubjectAlias,
            string FingerCode,
            int UsbCaptures,
            int TurnstileCaptures,
            double? MeanControlIdQuality,
            double? MeanNfiq2Score,
            double? MeanFinalMinutiae,
            double? MeanTurnstileVariance,
            int Trials,
            int FullThreeCapturePlans,
            int OneContributorPlans,
            int ReferenceOnlyPlans,
            int GenuineComparisons,
            int BaselineFalseNonMatches,
            int FusedFalseNonMatches,
            int RescuedGenuineComparisons,
            int RegressedGenuineComparisons,
            Distribution BaselineGenuineScores,
            Distribution FusedGenuineScores,
            Distribution GenuineScoreDeltas,
            int ImpostorComparisons,
            int BaselineFalseMatches,
            int FusedFalseMatches,
            int NewFalseMatches,
            int RemovedFalseMatches);

        sealed record CrossDeviceFusionReport(
            int SchemaVersion,
            DateTimeOffset GeneratedUtc,
            string DatasetSha256,
            string BatchPrefix,
            string SourceAfisVersion,
            string DahomeyCborVersion,
            string ImageSharpVersion,
            double DecisionThreshold,
            double TurnstileDpi,
            int IdentityCount,
            int UsbCaptureCount,
            int TurnstileCaptureCount,
            int UsbTemplatesRead,
            int UsbByteStableReserializations,
            int UsbRawByteStableReextractions,
            int TurnstileHashesValidated,
            int TrialCount,
            int StableFusedSerializations,
            int FullThreeCapturePlans,
            int OneContributorPlans,
            int ReferenceOnlyPlans,
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
            IReadOnlyList<IdentitySummary> Identities);

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        [Test, Explicit("Compares three-capture USB enrollment fusion against turnstile RAW probes.")]
        public void ComparesUsbEnrollmentFusionAgainstTurnstileProbes()
        {
            string root = RequiredDirectory(RootVariable);
            string output = RequiredDirectory(OutputVariable, create: true);
            string prefix = Environment.GetEnvironmentVariable(PrefixVariable);
            Assert.That(prefix, Is.Not.Null.And.Not.Empty,
                $"Set {PrefixVariable} to the batch prefix, for example 20261002-.");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            List<IdentityData> identities = Load(root, prefix);
            Assert.That(identities, Is.Not.Empty);
            Assert.That(identities.All(identity => identity.Usb.Count >= 3), Is.True);
            Assert.That(identities.All(identity => identity.Turnstile.Count > 0), Is.True);
            Assert.That(identities.Select(identity => identity.Identity).Distinct(StringComparer.Ordinal).Count(),
                Is.EqualTo(identities.Count), "Every selected single-session batch must have a unique identity.");

            TurnstileProbe[] probes = identities.SelectMany(identity => identity.Turnstile).ToArray();
            Dictionary<string, FingerprintMatcher> probeMatchers = probes.ToDictionary(
                probe => probe.Id,
                probe => new FingerprintMatcher(probe.Template),
                StringComparer.Ordinal);
            var planning = new TemplateFusionOptions(Threshold, 8);
            var options = new TemplateFuserOptions(planning);
            var trials = new List<TrialRow>();
            var scores = new List<ScoreRow>();

            foreach (IdentityData identity in identities)
            {
                for (int first = 0; first < identity.Usb.Count - 2; ++first)
                {
                    for (int second = first + 1; second < identity.Usb.Count - 1; ++second)
                    {
                        for (int third = second + 1; third < identity.Usb.Count; ++third)
                        {
                            UsbItem[] enrollment = [identity.Usb[first], identity.Usb[second], identity.Usb[third]];
                            string trialId = $"{identity.Identity}:e{enrollment[0].Capture:D2}-{enrollment[1].Capture:D2}-{enrollment[2].Capture:D2}";
                            TemplateFusionResult fusion = TemplateFuser.Fuse(
                                enrollment.Select(item => new TemplateFusionInput(item.Template, item.Coverage)).ToArray(),
                                options);
                            UsbItem reference = enrollment[fusion.Plan.Reference];
                            byte[] serialized = fusion.Template.ToByteArray();
                            var restored = new FingerprintTemplate(serialized);
                            bool stable = restored.ToByteArray().SequenceEqual(serialized);
                            trials.Add(new(
                                identity.Identity,
                                fusion.Plan.Alignments.Count(alignment => alignment.Accepted),
                                stable));

                            foreach (TurnstileProbe probe in probes)
                            {
                                FingerprintMatcher matcher = probeMatchers[probe.Id];
                                double baseline = matcher.Match(reference.Template);
                                double fused = matcher.Match(restored);
                                bool isGenuine = string.Equals(probe.Identity, identity.Identity, StringComparison.Ordinal);
                                scores.Add(new(
                                    trialId,
                                    identity.Identity,
                                    reference.Capture,
                                    probe.Id,
                                    probe.Identity,
                                    isGenuine,
                                    baseline,
                                    fused,
                                    fused - baseline,
                                    baseline >= Threshold,
                                    fused >= Threshold));
                            }
                        }
                    }
                }
            }

            stopwatch.Stop();
            const double tieTolerance = 1e-9;
            ScoreRow[] genuine = scores.Where(score => score.Genuine).ToArray();
            ScoreRow[] impostor = scores.Where(score => !score.Genuine).ToArray();
            IReadOnlyList<IdentitySummary> summaries = identities.Select(identity =>
                Summarize(identity, trials, scores)).ToArray();
            var report = new CrossDeviceFusionReport(
                1,
                DateTimeOffset.UtcNow,
                DatasetHash(identities),
                prefix,
                FingerprintCompatibility.Version,
                VersionOf(typeof(Dahomey.Cbor.CborOptions).Assembly),
                VersionOf(typeof(SixLabors.ImageSharp.Image).Assembly),
                Threshold,
                TurnstileDpi,
                identities.Count,
                identities.Sum(identity => identity.Usb.Count),
                probes.Length,
                identities.Sum(identity => identity.Usb.Count),
                identities.Sum(identity => identity.Usb.Count(item => item.SerializationBytesStable)),
                identities.Sum(identity => identity.Usb.Count(item => item.RawReextractionBytesStable)),
                probes.Length,
                trials.Count,
                trials.Count(trial => trial.SerializationBytesStable),
                trials.Count(trial => trial.AcceptedContributors == 2),
                trials.Count(trial => trial.AcceptedContributors == 1),
                trials.Count(trial => trial.AcceptedContributors == 0),
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
                summaries);

            string reportPath = Path.Combine(output, "cross-device-fusion-report.json");
            string scoresPath = Path.Combine(output, "cross-device-fusion-scores.csv");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
            File.WriteAllText(scoresPath, Csv(scores), new UTF8Encoding(false));

            int expectedTrials = identities.Sum(identity => CombinationsOfThree(identity.Usb.Count));
            int expectedGenuine = identities.Sum(identity =>
                CombinationsOfThree(identity.Usb.Count) * identity.Turnstile.Count);
            int expectedImpostor = identities.Sum(identity =>
                CombinationsOfThree(identity.Usb.Count) * (probes.Length - identity.Turnstile.Count));
            Assert.Multiple(() =>
            {
                Assert.That(report.TrialCount, Is.EqualTo(expectedTrials));
                Assert.That(report.GenuineComparisons, Is.EqualTo(expectedGenuine));
                Assert.That(report.ImpostorComparisons, Is.EqualTo(expectedImpostor));
                Assert.That(report.UsbByteStableReserializations, Is.EqualTo(report.UsbCaptureCount));
                Assert.That(report.UsbRawByteStableReextractions, Is.EqualTo(report.UsbCaptureCount));
                Assert.That(report.StableFusedSerializations, Is.EqualTo(report.TrialCount));
                Assert.That(scores.All(score => double.IsFinite(score.BaselineScore)
                    && double.IsFinite(score.FusedScore)
                    && double.IsFinite(score.Delta)), Is.True);
                Assert.That(File.Exists(reportPath), Is.True);
                Assert.That(File.Exists(scoresPath), Is.True);
            });

            TestContext.WriteLine($"dataset={report.DatasetSha256}");
            TestContext.WriteLine($"identities={report.IdentityCount},usb={report.UsbCaptureCount},turnstile={report.TurnstileCaptureCount},trials={report.TrialCount}");
            TestContext.WriteLine($"plans=full:{report.FullThreeCapturePlans},one:{report.OneContributorPlans},referenceOnly:{report.ReferenceOnlyPlans}");
            TestContext.WriteLine($"genuine={report.GenuineComparisons},baselineFn:{report.BaselineFalseNonMatches},fusedFn:{report.FusedFalseNonMatches},rescued:{report.RescuedGenuineComparisons},regressed:{report.RegressedGenuineComparisons}");
            TestContext.WriteLine($"genuineP05=baseline:{report.BaselineGenuineScores.P05:R},fused:{report.FusedGenuineScores.P05:R},deltaMedian:{report.GenuineScoreDeltas.Median:R}");
            TestContext.WriteLine($"impostor={report.ImpostorComparisons},baselineFm:{report.BaselineFalseMatches},fusedFm:{report.FusedFalseMatches},p99=baseline:{report.BaselineImpostorScores.P99:R},fused:{report.FusedImpostorScores.P99:R},maxFused:{report.FusedImpostorScores.Maximum:R}");
            TestContext.WriteLine($"report={reportPath}");
            TestContext.WriteLine($"scores={scoresPath}");
        }

        [Test, Explicit("Compares average, anchored, and strict three-capture fusion against turnstile RAW probes.")]
        public void ComparesCrossDeviceFusionVariants()
        {
            string root = RequiredDirectory(RootVariable);
            string output = RequiredDirectory(OutputVariable, create: true);
            string prefix = Environment.GetEnvironmentVariable(PrefixVariable);
            Assert.That(prefix, Is.Not.Null.And.Not.Empty,
                $"Set {PrefixVariable} to the batch prefix, for example 20261002-.");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            List<IdentityData> identities = Load(root, prefix);
            Assert.That(identities, Is.Not.Empty);
            Assert.That(identities.All(identity => identity.Usb.Count >= 3), Is.True);
            Assert.That(identities.All(identity => identity.Turnstile.Count > 0), Is.True);
            Assert.That(identities.Select(identity => identity.Identity).Distinct(StringComparer.Ordinal).Count(),
                Is.EqualTo(identities.Count), "Every selected single-session batch must have a unique identity.");

            TurnstileProbe[] probes = identities.SelectMany(identity => identity.Turnstile).ToArray();
            Dictionary<string, FingerprintMatcher> probeMatchers = probes.ToDictionary(
                probe => probe.Id,
                probe => new FingerprintMatcher(probe.Template),
                StringComparer.Ordinal);
            var planning = new TemplateFusionOptions(Threshold, 8);
            FusionVariant[] variants =
            [
                new("full-average", new(
                    planning,
                    AverageReferenceMinutiae: true,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: true)),
                new("full-anchored", new(
                    planning,
                    AverageReferenceMinutiae: false,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: true)),
                new("full-anchored-blend25", new(
                    planning,
                    AverageReferenceMinutiae: false,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: true,
                    AnchoredReferenceBlend: .25)),
                new("full-anchored-blend50", new(
                    planning,
                    AverageReferenceMinutiae: false,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: true,
                    AnchoredReferenceBlend: .5)),
                new("full-anchored-blend75", new(
                    planning,
                    AverageReferenceMinutiae: false,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: true,
                    AnchoredReferenceBlend: .75)),
                new("strict-2of3-anchored", new(
                    planning,
                    AverageReferenceMinutiae: false,
                    RetainSupportedNovelMinutiae: true,
                    RetainUniqueCoverageMinutiae: false,
                    MinimumReferenceSupport: 2))
            ];
            var scores = new List<AblationScoreRow>();
            var stableSerializations = new int[variants.Length];
            var outputMinutiae = new long[variants.Length];
            int trials = 0;
            int fullPlans = 0;
            int oneContributorPlans = 0;
            int referenceOnlyPlans = 0;

            foreach (IdentityData identity in identities)
            {
                for (int first = 0; first < identity.Usb.Count - 2; ++first)
                {
                    for (int second = first + 1; second < identity.Usb.Count - 1; ++second)
                    {
                        for (int third = second + 1; third < identity.Usb.Count; ++third)
                        {
                            UsbItem[] enrollment = [identity.Usb[first], identity.Usb[second], identity.Usb[third]];
                            TemplateFusionInput[] inputs = enrollment
                                .Select(item => new TemplateFusionInput(item.Template, item.Coverage))
                                .ToArray();
                            string trialId = $"{identity.Identity}:e{enrollment[0].Capture:D2}-{enrollment[1].Capture:D2}-{enrollment[2].Capture:D2}";
                            var fused = new FingerprintTemplate[variants.Length];
                            int referenceIndex = -1;
                            for (int variant = 0; variant < variants.Length; ++variant)
                            {
                                TemplateFusionResult fusion = TemplateFuser.Fuse(inputs, variants[variant].Options);
                                if (referenceIndex < 0)
                                {
                                    referenceIndex = fusion.Plan.Reference;
                                    int contributors = fusion.Plan.Alignments.Count(alignment => alignment.Accepted);
                                    if (contributors == 2)
                                        ++fullPlans;
                                    else if (contributors == 1)
                                        ++oneContributorPlans;
                                    else
                                        ++referenceOnlyPlans;
                                }
                                else
                                    Assert.That(fusion.Plan.Reference, Is.EqualTo(referenceIndex));
                                byte[] serialized = fusion.Template.ToByteArray();
                                fused[variant] = new FingerprintTemplate(serialized);
                                if (fused[variant].ToByteArray().SequenceEqual(serialized))
                                    ++stableSerializations[variant];
                                outputMinutiae[variant] += fusion.OutputMinutiae;
                            }
                            ++trials;

                            UsbItem reference = enrollment[referenceIndex];
                            foreach (TurnstileProbe probe in probes)
                            {
                                FingerprintMatcher matcher = probeMatchers[probe.Id];
                                double baseline = matcher.Match(reference.Template);
                                double[] variantScores = fused.Select(matcher.Match).ToArray();
                                scores.Add(new(
                                    trialId,
                                    identity.Identity,
                                    reference.Capture,
                                    probe.Id,
                                    probe.Identity,
                                    string.Equals(probe.Identity, identity.Identity, StringComparison.Ordinal),
                                    baseline,
                                    variantScores));
                            }
                        }
                    }
                }
            }

            stopwatch.Stop();
            double[] thresholds = [40, 42, 45, 50, 60];
            var results = variants.Select((variant, index) =>
            {
                AblationScoreRow[] genuine = scores.Where(score => score.Genuine).ToArray();
                AblationScoreRow[] impostor = scores.Where(score => !score.Genuine).ToArray();
                double[] genuineScores = genuine.Select(score => score.VariantScores[index]).ToArray();
                double[] genuineDeltas = genuine.Select(score => score.VariantScores[index] - score.BaselineScore).ToArray();
                double[] impostorScores = impostor.Select(score => score.VariantScores[index]).ToArray();
                double[] impostorDeltas = impostor.Select(score => score.VariantScores[index] - score.BaselineScore).ToArray();
                return new
                {
                    variant.Name,
                    variant.Options.AverageReferenceMinutiae,
                    variant.Options.RetainSupportedNovelMinutiae,
                    variant.Options.RetainUniqueCoverageMinutiae,
                    variant.Options.MinimumReferenceSupport,
                    variant.Options.AnchoredReferenceBlend,
                    TrialCount = trials,
                    StableSerializations = stableSerializations[index],
                    MeanOutputMinutiae = outputMinutiae[index] / (double)trials,
                    GenuineComparisons = genuine.Length,
                    BaselineFalseNonMatches = genuine.Count(score => score.BaselineScore < Threshold),
                    VariantFalseNonMatches = genuineScores.Count(score => score < Threshold),
                    RescuedGenuineComparisons = genuine.Count(score => score.BaselineScore < Threshold && score.VariantScores[index] >= Threshold),
                    RegressedGenuineComparisons = genuine.Count(score => score.BaselineScore >= Threshold && score.VariantScores[index] < Threshold),
                    GenuineScores = Describe(genuineScores),
                    GenuineDeltas = Describe(genuineDeltas),
                    ImpostorComparisons = impostor.Length,
                    BaselineFalseMatches = impostor.Count(score => score.BaselineScore >= Threshold),
                    VariantFalseMatches = impostorScores.Count(score => score >= Threshold),
                    NewFalseMatches = impostor.Count(score => score.BaselineScore < Threshold && score.VariantScores[index] >= Threshold),
                    RemovedFalseMatches = impostor.Count(score => score.BaselineScore >= Threshold && score.VariantScores[index] < Threshold),
                    ImpostorScores = Describe(impostorScores),
                    ImpostorDeltas = Describe(impostorDeltas),
                    ThresholdSweep = thresholds.Select(threshold => new
                    {
                        Threshold = threshold,
                        BaselineFalseNonMatches = genuine.Count(score => score.BaselineScore < threshold),
                        VariantFalseNonMatches = genuineScores.Count(score => score < threshold),
                        BaselineFalseMatches = impostor.Count(score => score.BaselineScore >= threshold),
                        VariantFalseMatches = impostorScores.Count(score => score >= threshold)
                    }).ToArray()
                };
            }).ToArray();
            var report = new
            {
                SchemaVersion = 1,
                GeneratedUtc = DateTimeOffset.UtcNow,
                DatasetSha256 = DatasetHash(identities),
                BatchPrefix = prefix,
                SourceAfisVersion = FingerprintCompatibility.Version,
                DahomeyCborVersion = VersionOf(typeof(Dahomey.Cbor.CborOptions).Assembly),
                ImageSharpVersion = VersionOf(typeof(SixLabors.ImageSharp.Image).Assembly),
                DecisionThreshold = Threshold,
                TurnstileDpi,
                IdentityCount = identities.Count,
                UsbCaptureCount = identities.Sum(identity => identity.Usb.Count),
                TurnstileCaptureCount = probes.Length,
                TrialCount = trials,
                FullThreeCapturePlans = fullPlans,
                OneContributorPlans = oneContributorPlans,
                ReferenceOnlyPlans = referenceOnlyPlans,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                Variants = results
            };

            string reportPath = Path.Combine(output, "cross-device-fusion-ablation-report.json");
            string scoresPath = Path.Combine(output, "cross-device-fusion-ablation-scores.csv");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
            File.WriteAllText(scoresPath, AblationCsv(scores, variants), new UTF8Encoding(false));

            Assert.Multiple(() =>
            {
                Assert.That(stableSerializations.All(count => count == trials), Is.True);
                Assert.That(scores.All(score => double.IsFinite(score.BaselineScore)
                    && score.VariantScores.All(double.IsFinite)), Is.True);
                Assert.That(File.Exists(reportPath), Is.True);
                Assert.That(File.Exists(scoresPath), Is.True);
            });
            foreach (var result in results)
                TestContext.WriteLine($"variant={result.Name},fn={result.VariantFalseNonMatches},rescued={result.RescuedGenuineComparisons},regressed={result.RegressedGenuineComparisons},genuineP05={result.GenuineScores.P05:R},deltaMedian={result.GenuineDeltas.Median:R},fm={result.VariantFalseMatches},impostorP99={result.ImpostorScores.P99:R},impostorMax={result.ImpostorScores.Maximum:R},meanMinutiae={result.MeanOutputMinutiae:R}");
            TestContext.WriteLine($"report={reportPath}");
            TestContext.WriteLine($"scores={scoresPath}");
        }

        static List<IdentityData> Load(string root, string prefix)
        {
            var identities = new List<IdentityData>();
            foreach (string batch in Directory.GetDirectories(root, $"{prefix}*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                string[] reports = Directory.GetFiles(batch, "report.json", SearchOption.AllDirectories);
                string turnstileManifest = Path.Combine(batch, "turnstile", "manifest.json");
                if (reports.Length != 1 || !File.Exists(turnstileManifest))
                    continue;

                SessionSnapshot session = JsonSerializer.Deserialize<SessionSnapshot>(
                    File.ReadAllText(reports[0]), JsonOptions)
                    ?? throw new InvalidDataException($"Cannot deserialize {reports[0]}.");
                TurnstileSnapshot turnstile = JsonSerializer.Deserialize<TurnstileSnapshot>(
                    File.ReadAllText(turnstileManifest), JsonOptions)
                    ?? throw new InvalidDataException($"Cannot deserialize {turnstileManifest}.");
                if (!string.Equals(session.SubjectAlias, turnstile.SubjectAlias, StringComparison.Ordinal))
                    throw new InvalidDataException($"Subject mismatch in {batch}.");

                string identity = $"{session.SubjectAlias}:{session.FingerCode}";
                string sessionRoot = Path.GetDirectoryName(reports[0]);
                List<UsbItem> usb = session.Captures
                    .OrderBy(capture => capture.Index)
                    .Select(capture => LoadUsb(sessionRoot, identity, capture))
                    .ToList();
                string turnstileRoot = Path.GetDirectoryName(turnstileManifest);
                List<TurnstileProbe> probes = turnstile.Captures
                    .OrderBy(capture => capture.Index)
                    .Select(capture => LoadTurnstile(turnstileRoot, identity, capture))
                    .ToList();
                identities.Add(new(
                    Path.GetFileName(batch),
                    identity,
                    session.SubjectAlias,
                    session.FingerCode,
                    session.SourceAfisVersion,
                    usb,
                    probes));
            }
            return identities;
        }

        static UsbItem LoadUsb(string root, string identity, CaptureSnapshot capture)
        {
            string templatePath = SafePath(root, capture.SourceAfisTemplateFile);
            string rawPath = SafePath(root, capture.RawFile);
            byte[] serialized = File.ReadAllBytes(templatePath);
            byte[] raw = File.ReadAllBytes(rawPath);
            ValidateHash(serialized, capture.SourceAfisTemplateSha256, templatePath);
            ValidateHash(raw, capture.RawSha256, rawPath);
            if (raw.Length != checked(capture.Width * capture.Height))
                throw new InvalidDataException($"USB RAW length mismatch: {rawPath}");
            var template = new FingerprintTemplate(serialized);
            var image = new FingerprintImage(
                capture.Width,
                capture.Height,
                raw,
                new FingerprintImageOptions { Dpi = capture.Dpi });
            var reextracted = new FingerprintTemplate(image);
            return new(
                $"{identity}:u{capture.Index:D2}",
                capture.Index,
                template,
                ExtractInnerMask(image),
                template.ToByteArray().SequenceEqual(serialized),
                reextracted.ToByteArray().SequenceEqual(serialized),
                capture.ControlIdQuality,
                capture.Nfiq2Score,
                capture.FinalMinutiae);
        }

        static TurnstileProbe LoadTurnstile(
            string root,
            string identity,
            TurnstileCaptureSnapshot capture)
        {
            string rawPath = SafePath(root, capture.RawFile);
            byte[] raw = File.ReadAllBytes(rawPath);
            ValidateHash(raw, capture.RawSha256, rawPath);
            int expected = checked(capture.Width * capture.Height);
            if (raw.Length != expected || capture.ImageBytes != expected)
                throw new InvalidDataException($"Turnstile RAW length mismatch: {rawPath}");
            var image = new FingerprintImage(
                capture.Width,
                capture.Height,
                raw,
                new FingerprintImageOptions { Dpi = TurnstileDpi });
            return new(
                $"{identity}:t{capture.Index:D2}",
                identity,
                new FingerprintTemplate(image),
                capture.Variance);
        }

        static IdentitySummary Summarize(
            IdentityData identity,
            IReadOnlyList<TrialRow> trials,
            IReadOnlyList<ScoreRow> scores)
        {
            TrialRow[] identityTrials = trials
                .Where(trial => string.Equals(trial.TargetIdentity, identity.Identity, StringComparison.Ordinal))
                .ToArray();
            ScoreRow[] genuine = scores
                .Where(score => string.Equals(score.TargetIdentity, identity.Identity, StringComparison.Ordinal)
                    && score.Genuine)
                .ToArray();
            ScoreRow[] impostor = scores
                .Where(score => string.Equals(score.TargetIdentity, identity.Identity, StringComparison.Ordinal)
                    && !score.Genuine)
                .ToArray();
            return new(
                identity.Batch,
                identity.Identity,
                identity.SubjectAlias,
                identity.FingerCode,
                identity.Usb.Count,
                identity.Turnstile.Count,
                Mean(identity.Usb.Select(item => item.ControlIdQuality)),
                Mean(identity.Usb.Select(item => item.Nfiq2Score)),
                identity.Usb.Average(item => (double)item.FinalMinutiae),
                Mean(identity.Turnstile.Select(item => item.Variance)),
                identityTrials.Length,
                identityTrials.Count(trial => trial.AcceptedContributors == 2),
                identityTrials.Count(trial => trial.AcceptedContributors == 1),
                identityTrials.Count(trial => trial.AcceptedContributors == 0),
                genuine.Length,
                genuine.Count(score => !score.BaselineAccepted),
                genuine.Count(score => !score.FusedAccepted),
                genuine.Count(score => !score.BaselineAccepted && score.FusedAccepted),
                genuine.Count(score => score.BaselineAccepted && !score.FusedAccepted),
                Describe(genuine.Select(score => score.BaselineScore)),
                Describe(genuine.Select(score => score.FusedScore)),
                Describe(genuine.Select(score => score.Delta)),
                impostor.Length,
                impostor.Count(score => score.BaselineAccepted),
                impostor.Count(score => score.FusedAccepted),
                impostor.Count(score => !score.BaselineAccepted && score.FusedAccepted),
                impostor.Count(score => score.BaselineAccepted && !score.FusedAccepted));
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

        static double? Mean(IEnumerable<double?> source)
        {
            double[] values = source.Where(value => value.HasValue).Select(value => value.Value).ToArray();
            return values.Length > 0 ? values.Average() : null;
        }

        static string DatasetHash(IEnumerable<IdentityData> identities)
        {
            var lines = new List<string>();
            foreach (IdentityData identity in identities.OrderBy(identity => identity.Identity, StringComparer.Ordinal))
            {
                foreach (UsbItem item in identity.Usb)
                    lines.Add($"{item.Id}|{Sha256(item.Template.ToByteArray())}");
                foreach (TurnstileProbe probe in identity.Turnstile)
                    lines.Add($"{probe.Id}|{Sha256(probe.Template.ToByteArray())}");
            }
            return Sha256(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
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

        static string AblationCsv(
            IEnumerable<AblationScoreRow> scores,
            IReadOnlyList<FusionVariant> variants)
        {
            var csv = new StringBuilder("trialId,targetIdentity,referenceCapture,probeId,probeIdentity,isGenuine,baselineScore");
            foreach (FusionVariant variant in variants)
                csv.Append(',').Append(variant.Name);
            csv.Append("\r\n");
            foreach (AblationScoreRow score in scores)
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

        static string SafePath(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Path escapes its session: {path}");
            return path;
        }

        static void ValidateHash(byte[] bytes, string expected, string path)
        {
            if (!string.Equals(Sha256(bytes), expected, StringComparison.Ordinal))
                throw new InvalidDataException($"SHA-256 mismatch: {path}");
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
