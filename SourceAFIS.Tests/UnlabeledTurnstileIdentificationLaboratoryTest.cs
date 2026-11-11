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
    public class UnlabeledTurnstileIdentificationLaboratoryTest
    {
        const string BatchVariable = "SOURCEAFIS_MULTIFINGER_LAB_BATCH";
        const string OutputVariable = "SOURCEAFIS_MULTIFINGER_OUTPUT";
        const double TurnstileDpi = 500;

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

        sealed class TurnstileSnapshot
        {
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
        }

        sealed record UsbCapture(
            int Index,
            FingerprintTemplate Template,
            BooleanMatrix Coverage,
            string TemplateHash);

        sealed record IdentityData(
            string Identity,
            IReadOnlyList<UsbCapture> Captures);

        sealed record ProbeData(
            string Id,
            FingerprintTemplate Template,
            string RawHash);

        sealed record ScoreRow(
            string ProbeId,
            string EnrollmentCaptures,
            string Mode,
            string CandidateIdentity,
            double Score,
            int Rank);

        sealed record ModeSummary(
            string Mode,
            string ModalTopIdentity,
            int ModalTopCount,
            double ModalTopRate,
            int DistinctTopIdentities,
            double MinimumTopScore,
            double MedianTopScore,
            double MinimumGap,
            double MedianGap);

        sealed record ProbeSummary(
            string ProbeId,
            ModeSummary Baseline,
            ModeSummary Fused);

        sealed record IdentificationReport(
            int SchemaVersion,
            DateTimeOffset GeneratedUtc,
            string DatasetSha256,
            string SourceAfisVersion,
            string DahomeyCborVersion,
            string ImageSharpVersion,
            double TurnstileDpi,
            int IdentityCount,
            int UsbCaptureCount,
            int TurnstileCaptureCount,
            int GalleryCount,
            int StableFusedSerializations,
            IReadOnlyList<ProbeSummary> Probes);

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        [Test, Explicit("Ranks unlabeled turnstile probes against 120 ten-finger USB galleries.")]
        public void IdentifiesTurnstileProbesAgainstTenFingerUsbGalleries()
        {
            string batch = RequiredDirectory(BatchVariable);
            string output = RequiredDirectory(OutputVariable, create: true);
            List<IdentityData> identities = LoadUsbIdentities(batch);
            List<ProbeData> probes = LoadTurnstileProbes(batch);
            Assert.That(identities.Count, Is.EqualTo(10));
            Assert.That(identities.All(identity => identity.Captures.Count == 10), Is.True);

            var planning = new TemplateFusionOptions(40, 8);
            var options = new TemplateFuserOptions(planning);
            var rows = new List<ScoreRow>();
            int stableFusions = 0;
            int galleries = 0;

            for (int first = 0; first < 8; ++first)
            {
                for (int second = first + 1; second < 9; ++second)
                {
                    for (int third = second + 1; third < 10; ++third)
                    {
                        string enrollmentId = $"{first + 1:D2}-{second + 1:D2}-{third + 1:D2}";
                        var baseline = new Dictionary<string, FingerprintTemplate>(StringComparer.Ordinal);
                        var fused = new Dictionary<string, FingerprintTemplate>(StringComparer.Ordinal);
                        foreach (IdentityData identity in identities)
                        {
                            UsbCapture[] enrollment =
                            [
                                identity.Captures[first],
                                identity.Captures[second],
                                identity.Captures[third]
                            ];
                            TemplateFusionResult fusion = TemplateFuser.Fuse(
                                enrollment.Select(capture => new TemplateFusionInput(
                                    capture.Template,
                                    capture.Coverage)).ToArray(),
                                options);
                            baseline[identity.Identity] = enrollment[fusion.Plan.Reference].Template;
                            byte[] serialized = fusion.Template.ToByteArray();
                            var restored = new FingerprintTemplate(serialized);
                            if (restored.ToByteArray().SequenceEqual(serialized))
                                ++stableFusions;
                            fused[identity.Identity] = restored;
                        }

                        foreach (ProbeData probe in probes)
                        {
                            AddRanking(rows, probe, enrollmentId, "baseline", baseline);
                            AddRanking(rows, probe, enrollmentId, "fused", fused);
                        }
                        ++galleries;
                    }
                }
            }

            IReadOnlyList<ProbeSummary> summaries = probes
                .Select(probe => new ProbeSummary(
                    probe.Id,
                    Summarize(probe.Id, "baseline", rows, galleries),
                    Summarize(probe.Id, "fused", rows, galleries)))
                .ToArray();
            var report = new IdentificationReport(
                1,
                DateTimeOffset.UtcNow,
                DatasetHash(identities, probes),
                FingerprintCompatibility.Version,
                VersionOf(typeof(Dahomey.Cbor.CborOptions).Assembly),
                VersionOf(typeof(SixLabors.ImageSharp.Image).Assembly),
                TurnstileDpi,
                identities.Count,
                identities.Sum(identity => identity.Captures.Count),
                probes.Count,
                galleries,
                stableFusions,
                summaries);

            string reportPath = Path.Combine(output, "turnstile-identification-report.json");
            string scoresPath = Path.Combine(output, "turnstile-identification-scores.csv");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
            File.WriteAllText(scoresPath, Csv(rows), new UTF8Encoding(false));

            Assert.Multiple(() =>
            {
                Assert.That(galleries, Is.EqualTo(120));
                Assert.That(stableFusions, Is.EqualTo(galleries * identities.Count));
                Assert.That(rows.Count, Is.EqualTo(galleries * probes.Count * identities.Count * 2));
                Assert.That(rows.All(row => double.IsFinite(row.Score)), Is.True);
                Assert.That(File.Exists(reportPath), Is.True);
                Assert.That(File.Exists(scoresPath), Is.True);
            });

            foreach (ProbeSummary summary in summaries)
                TestContext.WriteLine(
                    $"{summary.ProbeId}: baseline={summary.Baseline.ModalTopIdentity} "
                    + $"{summary.Baseline.ModalTopCount}/{galleries}, "
                    + $"fused={summary.Fused.ModalTopIdentity} {summary.Fused.ModalTopCount}/{galleries}");
            TestContext.WriteLine($"report={reportPath}");
            TestContext.WriteLine($"scores={scoresPath}");
        }

        static void AddRanking(
            List<ScoreRow> rows,
            ProbeData probe,
            string enrollment,
            string mode,
            IReadOnlyDictionary<string, FingerprintTemplate> gallery)
        {
            var matcher = new FingerprintMatcher(probe.Template);
            var ranked = gallery
                .Select(candidate => (
                    Identity: candidate.Key,
                    Score: matcher.Match(candidate.Value)))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Identity, StringComparer.Ordinal)
                .ToArray();
            for (int rank = 0; rank < ranked.Length; ++rank)
                rows.Add(new(
                    probe.Id,
                    enrollment,
                    mode,
                    ranked[rank].Identity,
                    ranked[rank].Score,
                    rank + 1));
        }

        static ModeSummary Summarize(
            string probeId,
            string mode,
            IReadOnlyList<ScoreRow> rows,
            int galleries)
        {
            ScoreRow[] top = rows
                .Where(row => string.Equals(row.ProbeId, probeId, StringComparison.Ordinal)
                    && string.Equals(row.Mode, mode, StringComparison.Ordinal)
                    && row.Rank == 1)
                .ToArray();
            var modal = top
                .GroupBy(row => row.CandidateIdentity, StringComparer.Ordinal)
                .Select(group => new { Identity = group.Key, Count = group.Count() })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Identity, StringComparer.Ordinal)
                .First();
            double[] topScores = top.Select(row => row.Score).OrderBy(value => value).ToArray();
            double[] gaps = rows
                .Where(row => string.Equals(row.ProbeId, probeId, StringComparison.Ordinal)
                    && string.Equals(row.Mode, mode, StringComparison.Ordinal)
                    && row.Rank <= 2)
                .GroupBy(row => row.EnrollmentCaptures, StringComparer.Ordinal)
                .Select(group => group.OrderBy(row => row.Rank).First().Score
                    - group.OrderBy(row => row.Rank).Skip(1).First().Score)
                .OrderBy(value => value)
                .ToArray();
            return new(
                mode,
                modal.Identity,
                modal.Count,
                modal.Count / (double)galleries,
                top.Select(row => row.CandidateIdentity).Distinct(StringComparer.Ordinal).Count(),
                topScores[0],
                Percentile(topScores, .5),
                gaps[0],
                Percentile(gaps, .5));
        }

        static List<IdentityData> LoadUsbIdentities(string batch)
        {
            var result = new List<IdentityData>();
            foreach (string reportPath in Directory.GetFiles(batch, "report.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                SessionSnapshot session = JsonSerializer.Deserialize<SessionSnapshot>(
                    File.ReadAllText(reportPath), JsonOptions)
                    ?? throw new InvalidDataException($"Cannot deserialize {reportPath}.");
                string root = Path.GetDirectoryName(reportPath);
                string identity = $"{session.SubjectAlias}:{session.FingerCode}";
                List<UsbCapture> captures = session.Captures
                    .OrderBy(capture => capture.Index)
                    .Select(capture => LoadUsb(root, capture))
                    .ToList();
                result.Add(new(identity, captures));
            }
            return result.OrderBy(identity => identity.Identity, StringComparer.Ordinal).ToList();
        }

        static UsbCapture LoadUsb(string root, CaptureSnapshot capture)
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
            if (!template.ToByteArray().SequenceEqual(serialized))
                throw new InvalidDataException($"USB template reserialization changed: {templatePath}");
            if (!new FingerprintTemplate(image).ToByteArray().SequenceEqual(serialized))
                throw new InvalidDataException($"USB template reextraction changed: {templatePath}");
            return new(capture.Index, template, ExtractInnerMask(image), Sha256(serialized));
        }

        static List<ProbeData> LoadTurnstileProbes(string batch)
        {
            string manifestPath = Path.Combine(batch, "turnstile", "manifest.json");
            TurnstileSnapshot manifest = JsonSerializer.Deserialize<TurnstileSnapshot>(
                File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidDataException($"Cannot deserialize {manifestPath}.");
            string root = Path.GetDirectoryName(manifestPath);
            return manifest.Captures.OrderBy(capture => capture.Index).Select(capture =>
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
                return new ProbeData(
                    $"turnstile-{capture.Index:D2}",
                    new FingerprintTemplate(image),
                    capture.RawSha256);
            }).ToList();
        }

        static BooleanMatrix ExtractInnerMask(FingerprintImage image)
        {
            DoubleMatrix normalized = ImageResizer.Resize(image.Matrix, image.Dpi);
            var blocks = new BlockMap(normalized.Width, normalized.Height, Parameters.BlockSize);
            HistogramCube histogram = LocalHistograms.Create(blocks, normalized);
            BooleanMatrix blockMask = SegmentationMask.Compute(blocks, histogram);
            return SegmentationMask.Inner(SegmentationMask.Pixelwise(blockMask, blocks));
        }

        static string Csv(IEnumerable<ScoreRow> rows)
        {
            var csv = new StringBuilder("probeId,enrollmentCaptures,mode,candidateIdentity,score,rank\r\n");
            foreach (ScoreRow row in rows)
                csv.Append(row.ProbeId).Append(',')
                    .Append(row.EnrollmentCaptures).Append(',')
                    .Append(row.Mode).Append(',')
                    .Append(row.CandidateIdentity).Append(',')
                    .Append(row.Score.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.Rank).Append("\r\n");
            return csv.ToString();
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

        static string DatasetHash(
            IEnumerable<IdentityData> identities,
            IEnumerable<ProbeData> probes)
        {
            var lines = identities
                .SelectMany(identity => identity.Captures.Select(capture =>
                    $"{identity.Identity}:u{capture.Index:D2}|{capture.TemplateHash}"))
                .Concat(probes.Select(probe => $"{probe.Id}|{probe.RawHash}"))
                .OrderBy(line => line, StringComparer.Ordinal);
            return Sha256(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
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
