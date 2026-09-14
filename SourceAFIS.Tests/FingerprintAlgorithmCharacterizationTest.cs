// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SourceAFIS.Engine.Features;
using SourceAFIS.Engine.Matcher;
using SourceAFIS.Engine.Primitives;

namespace SourceAFIS
{
    /// <summary>
    /// Freezes observable behavior of the unmodified algorithm before experimental
    /// matcher and template-fusion work starts. Update the baseline only after an
    /// intentional algorithm change has been reviewed with its full snapshot diff.
    /// </summary>
    public class FingerprintAlgorithmCharacterizationTest
    {
        const double ScoreTolerance = 1e-9;
        const string SourceAfis314RawProbeTemplateBase64 =
            "p2d2ZXJzaW9uajMuMTQuMC1uZXRld2lkdGgZAUxmaGVpZ2h0GQIVanBvc2l0aW9uc1iYNxgoGJQYfBh0GHEYfRiGGIcYshjWGOMY/hjqGPAYdxj7GDIYfRhLGI4YvBh2GMkYgRiqGPIY5BkBDRhhGH8YYRicGJAYyBiMGIsYlBi+GNkYohi2GP4YWRiDGJEY8BjvGEQYjxiTGPoY8hjQGOoZAQdqcG9zaXRpb25zWZg3GQEkGCwZAQwZAXQZAcIZAZoZAZgZAa4Y2BgoGD4YOBi4GMwY2RkBGRkBThjQGQHMGN4YIhkBphhIGQHAGQFGGDYZASIZAVsYhhgqGQGuGKAZAQAYUBkBkBkBshkBmBkBBBiGGQHUGQGsGQGcGFEYpRjxGDsY5RkBDhj4GQHYGHoYshkBrhkBohkBmGpkaXJlY3Rpb25zmDf6P8kP2/pAx2EY+j+2AQf6QJIIL/pAUpdE+kCCecD6QIqeyPpAT3DT+j9e84f6QGQf5fpAZB/l+j7tYzj6P3iC0fpAhrSZ+kC84r/6P5Lvx/o/tgEH+kAQ/oX6QCdD4vpAJQJ7+kBy2Bj6QHQ+z/pAW7cV+kBVsfz6QJB60/pAWL2W+j+2AQf6P7xN6fpALf/R+kCCx+v6QGk+cPpATEJf+j+d97L6PpU50/pAk5to+kC+SdD6QJ6iwfo/o8Fm+kBt8zD6QMXfXvpAqBcC+kCgH4H6PUyhLfkAAPo/dccs+j8ikR36P423Dfo/z3Tk+j/0fcv6QEXdVvo/T/qr+kB5f9X6QKtjdPpAoZHu+kClohdldHlwZXN4N0VCQkVFQkVFRUJFRUJCRUVCRUJCQkJFRUJCQkVFQkVFQkJCRUVFRUVCRUVFRUJFRUVCRUVCRUI=";

#pragma warning disable CS0649 // Fields are populated by the CBOR deserializer.
        class PointSnapshot
        {
            public short X;
            public short Y;
        }

        class MinutiaSnapshot
        {
            public PointSnapshot Position;
            public float Direction;
            public MinutiaType Type;
        }

        class FeatureTemplateSnapshot
        {
            public PointSnapshot Size;
            public List<MinutiaSnapshot> Minutiae;
        }

        class EdgeShapeSnapshot
        {
            public float ReferenceAngle;
            public float NeighborAngle;
            public short Length;
        }

        class NeighborEdgeSnapshot
        {
            public EdgeShapeSnapshot Shape;
            public byte Neighbor;
        }

        class IndexedEdgeSnapshot
        {
            public EdgeShapeSnapshot Shape;
            public byte Reference;
            public byte Neighbor;
        }

        class HashEntrySnapshot
        {
            public int Key;
            public List<IndexedEdgeSnapshot> Edges;
        }

        class SkeletonRidgeSnapshot
        {
            public int Start;
            public int End;
            public List<PointSnapshot> Points;
        }

        class SkeletonSnapshot
        {
            public int Width;
            public int Height;
            public List<PointSnapshot> Minutiae;
            public List<SkeletonRidgeSnapshot> Ridges;
        }

        class MinutiaPairSnapshot
        {
            public int Probe;
            public int Candidate;
        }

        class EdgePairSnapshot
        {
            public int ProbeFrom;
            public int ProbeTo;
            public int CandidateFrom;
            public int CandidateTo;
        }

        class PairingSnapshot
        {
            public MinutiaPairSnapshot Root;
            public List<EdgePairSnapshot> Tree;
            public List<EdgePairSnapshot> Support;
        }

        class ScoreSnapshot
        {
            public int MinutiaCount;
            public double MinutiaScore;
            public double MinutiaFractionInProbe;
            public double MinutiaFractionInCandidate;
            public double MinutiaFraction;
            public double MinutiaFractionScore;
            public int SupportingEdgeSum;
            public int EdgeCount;
            public double EdgeScore;
            public int SupportedMinutiaCount;
            public double SupportedMinutiaScore;
            public int MinutiaTypeHits;
            public double MinutiaTypeScore;
            public int DistanceErrorSum;
            public int DistanceAccuracySum;
            public double DistanceAccuracyScore;
            public float AngleErrorSum;
            public float AngleAccuracySum;
            public double AngleAccuracyScore;
            public double TotalScore;
            public double ShapedScore;
        }
#pragma warning restore CS0649

        sealed class SelectedTransparency : FingerprintTransparency
        {
            readonly HashSet<string> accepted;
            readonly Dictionary<string, List<(string Mime, byte[] Data)>> captured = new();

            public SelectedTransparency(params string[] accepted)
            {
                this.accepted = new HashSet<string>(accepted);
            }

            public override bool Accepts(string key) => accepted.Contains(key);

            public override void Take(string key, string mime, byte[] data)
            {
                if (!captured.TryGetValue(key, out var values))
                    captured[key] = values = new();
                values.Add((mime, data.ToArray()));
            }

            public T SingleCbor<T>(string key)
            {
                var value = AssertSingle(key);
                Assert.That(value.Mime, Is.EqualTo("application/cbor"), $"Unexpected MIME type for '{key}'.");
                Assert.That(value.Data, Is.Not.EqualTo(new byte[] { 0xa0 }),
                    $"Transparency key '{key}' contains an empty CBOR map. Check record/field serialization.");
                return SerializationUtils.Deserialize<T>(value.Data);
            }

            public string SingleText(string key)
            {
                var value = AssertSingle(key);
                Assert.That(value.Mime, Is.EqualTo("text/plain"), $"Unexpected MIME type for '{key}'.");
                return Encoding.UTF8.GetString(value.Data);
            }

            (string Mime, byte[] Data) AssertSingle(string key)
            {
                Assert.That(captured.ContainsKey(key), Is.True, $"Transparency key '{key}' was not emitted.");
                Assert.That(captured[key], Has.Count.EqualTo(1), $"Transparency key '{key}' must be emitted exactly once.");
                return captured[key][0];
            }
        }

        record ExtractionSnapshot(
            int Skeleton,
            int Inner,
            int CloudFiltered,
            int Top,
            int Final,
            int EdgeStars,
            int Edges,
            string TemplateHash);

        record MatchSnapshot(
            double Score,
            int BestRoot,
            int TriedRoots,
            int HashBuckets,
            int HashedEdges,
            PairingSnapshot Pairing,
            ScoreSnapshot Components,
            string PairingHash);

        static FeatureTemplateSnapshot Feature(SelectedTransparency transparency, string key) =>
            transparency.SingleCbor<FeatureTemplateSnapshot>(key);

        static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        static string PairingHash(PairingSnapshot pairing)
        {
            var canonical = new StringBuilder();
            canonical.Append(pairing.Root.Probe).Append(':').Append(pairing.Root.Candidate).Append('|');
            foreach (var pair in pairing.Tree)
                canonical.Append(pair.ProbeFrom).Append(':').Append(pair.ProbeTo).Append(':')
                    .Append(pair.CandidateFrom).Append(':').Append(pair.CandidateTo).Append(';');
            canonical.Append('|');
            foreach (var pair in pairing.Support.OrderBy(p => p.ProbeFrom).ThenBy(p => p.ProbeTo)
                .ThenBy(p => p.CandidateFrom).ThenBy(p => p.CandidateTo))
                canonical.Append(pair.ProbeFrom).Append(':').Append(pair.ProbeTo).Append(':')
                    .Append(pair.CandidateFrom).Append(':').Append(pair.CandidateTo).Append(';');
            return Sha256(Encoding.UTF8.GetBytes(canonical.ToString()));
        }

        static void AssertTemplateEquivalent(FingerprintTemplate expected, FingerprintTemplate actual)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actual.Size, Is.EqualTo(expected.Size));
                Assert.That(actual.Minutiae, Has.Length.EqualTo(expected.Minutiae.Length));
                Assert.That(actual.Edges, Has.Length.EqualTo(expected.Edges.Length));
            });
            for (int i = 0; i < expected.Minutiae.Length; ++i)
                Assert.Multiple(() =>
                {
                    Assert.That(actual.Minutiae[i].Position, Is.EqualTo(expected.Minutiae[i].Position), $"Position of minutia {i} changed.");
                    Assert.That(actual.Minutiae[i].Direction, Is.EqualTo(expected.Minutiae[i].Direction), $"Direction of minutia {i} changed.");
                    Assert.That(actual.Minutiae[i].Type, Is.EqualTo(expected.Minutiae[i].Type), $"Type of minutia {i} changed.");
                });
            for (int reference = 0; reference < expected.Edges.Length; ++reference)
            {
                Assert.That(actual.Edges[reference], Has.Length.EqualTo(expected.Edges[reference].Length), $"Edge-star size at minutia {reference} changed.");
                for (int edge = 0; edge < expected.Edges[reference].Length; ++edge)
                    Assert.Multiple(() =>
                    {
                        Assert.That(actual.Edges[reference][edge].Neighbor, Is.EqualTo(expected.Edges[reference][edge].Neighbor));
                        Assert.That(actual.Edges[reference][edge].Shape.Length, Is.EqualTo(expected.Edges[reference][edge].Shape.Length));
                        Assert.That(actual.Edges[reference][edge].Shape.ReferenceAngle, Is.EqualTo(expected.Edges[reference][edge].Shape.ReferenceAngle));
                        Assert.That(actual.Edges[reference][edge].Shape.NeighborAngle, Is.EqualTo(expected.Edges[reference][edge].Shape.NeighborAngle));
                    });
            }
        }

        static void AssertTopology(FingerprintTemplate template)
        {
            Assert.That(template.Edges, Has.Length.EqualTo(template.Minutiae.Length));
            for (int reference = 0; reference < template.Edges.Length; ++reference)
            {
                var star = template.Edges[reference];
                Assert.That(star.Length, Is.LessThanOrEqualTo(9));
                for (int i = 0; i < star.Length; ++i)
                {
                    Assert.That(star[i].Neighbor, Is.InRange(0, template.Minutiae.Length - 1));
                    Assert.That(star[i].Neighbor, Is.Not.EqualTo(reference));
                    if (i > 0)
                    {
                        var previous = star[i - 1];
                        bool ordered = previous.Shape.Length < star[i].Shape.Length
                            || previous.Shape.Length == star[i].Shape.Length && previous.Neighbor < star[i].Neighbor;
                        Assert.That(ordered, Is.True, $"Edge star {reference} is not strictly ordered at position {i}.");
                    }
                }
            }
        }

        static ExtractionSnapshot Extract(FingerprintImage image)
        {
            using var transparency = new SelectedTransparency(
                "skeleton-minutiae",
                "inner-minutiae",
                "removed-minutia-clouds",
                "top-minutiae",
                "shuffled-minutiae",
                "edge-table");
            var template = new FingerprintTemplate(image);

            var skeleton = Feature(transparency, "skeleton-minutiae");
            var inner = Feature(transparency, "inner-minutiae");
            var cloud = Feature(transparency, "removed-minutia-clouds");
            var top = Feature(transparency, "top-minutiae");
            var shuffled = Feature(transparency, "shuffled-minutiae");
            var edgeTable = transparency.SingleCbor<NeighborEdgeSnapshot[][]>("edge-table");

            Assert.Multiple(() =>
            {
                Assert.That(inner.Minutiae.Count, Is.LessThanOrEqualTo(skeleton.Minutiae.Count));
                Assert.That(cloud.Minutiae.Count, Is.LessThanOrEqualTo(inner.Minutiae.Count));
                Assert.That(top.Minutiae.Count, Is.LessThanOrEqualTo(cloud.Minutiae.Count));
                Assert.That(shuffled.Minutiae, Has.Count.EqualTo(top.Minutiae.Count));
                Assert.That(template.Minutiae, Has.Length.EqualTo(shuffled.Minutiae.Count));
                Assert.That(edgeTable, Has.Length.EqualTo(template.Minutiae.Length));
            });
            AssertTopology(template);

            return new(
                skeleton.Minutiae.Count,
                inner.Minutiae.Count,
                cloud.Minutiae.Count,
                top.Minutiae.Count,
                template.Minutiae.Length,
                template.Edges.Length,
                template.Edges.Sum(star => star.Length),
                Sha256(template.ToByteArray()));
        }

        static MatchSnapshot Match(FingerprintTemplate probe, FingerprintTemplate candidate)
        {
            using var transparency = new SelectedTransparency("root-pairs", "roots", "best-pairing", "best-score", "best-match");
            var matcher = new FingerprintMatcher(probe);
            double score = matcher.Match(candidate);
            var roots = transparency.SingleCbor<List<MinutiaPairSnapshot>>("roots");
            var pairing = transparency.SingleCbor<PairingSnapshot>("best-pairing");
            var components = transparency.SingleCbor<ScoreSnapshot>("best-score");
            int bestRoot = int.Parse(transparency.SingleText("best-match"), CultureInfo.InvariantCulture);

            Assert.Multiple(() =>
            {
                Assert.That(components.ShapedScore, Is.EqualTo(score).Within(ScoreTolerance));
                Assert.That(components.MinutiaCount, Is.EqualTo(pairing.Tree.Count));
                Assert.That(components.EdgeCount, Is.EqualTo(components.MinutiaCount + components.SupportingEdgeSum));
                Assert.That(components.SupportingEdgeSum, Is.EqualTo(2 * pairing.Support.Count));
                Assert.That(components.TotalScore, Is.EqualTo(
                    components.MinutiaScore
                    + components.MinutiaFractionScore
                    + components.SupportedMinutiaScore
                    + components.EdgeScore
                    + components.MinutiaTypeScore
                    + components.DistanceAccuracyScore
                    + components.AngleAccuracyScore).Within(ScoreTolerance));
                Assert.That(pairing.Tree.Select(p => p.ProbeTo), Is.Unique);
                Assert.That(pairing.Tree.Select(p => p.CandidateTo), Is.Unique);
                Assert.That(pairing.Tree.All(p => p.ProbeFrom >= 0 && p.ProbeFrom < probe.Minutiae.Length
                    && p.ProbeTo >= 0 && p.ProbeTo < probe.Minutiae.Length), Is.True);
                Assert.That(pairing.Tree.All(p => p.CandidateFrom >= 0 && p.CandidateFrom < candidate.Minutiae.Length
                    && p.CandidateTo >= 0 && p.CandidateTo < candidate.Minutiae.Length), Is.True);
                Assert.That(bestRoot, Is.InRange(0, roots.Count - 1));
            });

            return new(
                score,
                bestRoot,
                roots.Count,
                matcher.Hash.Count,
                matcher.Hash.Values.Sum(bucket => bucket.Count),
                pairing,
                components,
                PairingHash(pairing));
        }

        static string ExtractionText(ExtractionSnapshot snapshot) => string.Format(
            CultureInfo.InvariantCulture,
            "skeleton={0},inner={1},cloud={2},top={3},final={4},stars={5},edges={6},template={7}",
            snapshot.Skeleton,
            snapshot.Inner,
            snapshot.CloudFiltered,
            snapshot.Top,
            snapshot.Final,
            snapshot.EdgeStars,
            snapshot.Edges,
            snapshot.TemplateHash);

        static string MatchText(MatchSnapshot snapshot) => string.Format(
            CultureInfo.InvariantCulture,
            "score={0:R},root={1}/{2},hashBuckets={3},hashedEdges={4},pairs={5},support={6},pairing={7},raw={8:R},minutiaScore={9:R},fractionProbe={10:R},fractionCandidate={11:R},fractionScore={12:R},edgeCount={13},edgeScore={14:R},typeHits={15},typeScore={16:R},supported={17},supportedScore={18:R},distanceError={19},distanceScore={20:R},angleError={21:R},angleScore={22:R}",
            snapshot.Score,
            snapshot.BestRoot,
            snapshot.TriedRoots,
            snapshot.HashBuckets,
            snapshot.HashedEdges,
            snapshot.Pairing.Tree.Count,
            snapshot.Pairing.Support.Count,
            snapshot.PairingHash,
            snapshot.Components.TotalScore,
            snapshot.Components.MinutiaScore,
            snapshot.Components.MinutiaFractionInProbe,
            snapshot.Components.MinutiaFractionInCandidate,
            snapshot.Components.MinutiaFractionScore,
            snapshot.Components.EdgeCount,
            snapshot.Components.EdgeScore,
            snapshot.Components.MinutiaTypeHits,
            snapshot.Components.MinutiaTypeScore,
            snapshot.Components.SupportedMinutiaCount,
            snapshot.Components.SupportedMinutiaScore,
            snapshot.Components.DistanceErrorSum,
            snapshot.Components.DistanceAccuracyScore,
            snapshot.Components.AngleErrorSum,
            snapshot.Components.AngleAccuracyScore);

        [Test]
        public void OriginalAlgorithmSnapshot()
        {
            var rawProbe = FingerprintTemplateTest.ProbeGray();
            var rawMatching = FingerprintTemplateTest.MatchingGray();
            var rawNonmatching = FingerprintTemplateTest.NonmatchingGray();
            var matching = Match(rawProbe, rawMatching);
            var reverse = Match(rawMatching, rawProbe);
            var nonmatching = new FingerprintMatcher(rawProbe).Match(rawNonmatching);

            string actual = string.Join(Environment.NewLine, new[]
            {
                "raw-probe: " + ExtractionText(Extract(FingerprintImageTest.ProbeGray())),
                "raw-matching: " + ExtractionText(Extract(FingerprintImageTest.MatchingGray())),
                "png-probe: " + ExtractionText(Extract(FingerprintImageTest.Probe())),
                "match-forward: " + MatchText(matching),
                "match-reverse: " + MatchText(reverse),
                "nonmatch-score: " + nonmatching.ToString("R", CultureInfo.InvariantCulture)
            });

            string expected = string.Join(Environment.NewLine, new[]
            {
                "raw-probe: skeleton=135,inner=55,cloud=55,top=55,final=55,stars=55,edges=495,template=82747efcb1ded8e96c78259b3475dd89d5aeac71ca786ea9f2bdf833f770c471",
                "raw-matching: skeleton=112,inner=49,cloud=47,top=47,final=47,stars=47,edges=423,template=00475504771fe496288c8f2503ca407629acba6e67b5eabdc6e7e8266084af38",
                "png-probe: skeleton=115,inner=46,cloud=46,top=46,final=46,stars=46,edges=414,template=5f2c0e2443fa4cbcb6b82e934a3d9bb752f0b347303467f9cdcc6d97cb5287f1",
                "match-forward: score=106.51580124192927,root=7/64,hashBuckets=26960,hashedEdges=81894,pairs=25,support=54,pairing=ef2ba28216acb0e34e6680480d10507473e4bd2684ffbf1643853c682ea8f52d,raw=57.98842997087348,minutiaScore=0.8,fractionProbe=0.45454545454545453,fractionCandidate=0.5319148936170213,fractionScore=4.429206963249516,edgeCount=133,edgeScore=35.245000000000005,typeHits=14,typeScore=8.806000000000001,supported=24,supportedScore=4.632,distanceError=225,distanceScore=2.7605769230769233,angleError=4.4270678,angleScore=1.3156460845470428",
                "match-reverse: score=106.51580124192927,root=4/65,hashBuckets=17979,hashedEdges=60522,pairs=25,support=54,pairing=f673071869335d847dccf5c5366e6dd77c1d9ae6e10d8cab554baf05fa2cf57f,raw=57.98842997087348,minutiaScore=0.8,fractionProbe=0.5319148936170213,fractionCandidate=0.45454545454545453,fractionScore=4.429206963249516,edgeCount=133,edgeScore=35.245000000000005,typeHits=14,typeScore=8.806000000000001,supported=24,supportedScore=4.632,distanceError=225,distanceScore=2.7605769230769233,angleError=4.4270678,angleScore=1.3156460845470428",
                "nonmatch-score: 2.340766003178002"
            });
            TestContext.WriteLine(actual);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void SerializationRoundTripPreservesTemplateTopologyAndScores()
        {
            var original = FingerprintTemplateTest.ProbeGray();
            byte[] serialized = original.ToByteArray();
            var restored = new FingerprintTemplate(serialized);
            byte[] reserialized = restored.ToByteArray();

            AssertTemplateEquivalent(original, restored);
            Assert.That(reserialized, Is.EqualTo(serialized));
            Assert.That(Sha256(serialized), Is.EqualTo("82747efcb1ded8e96c78259b3475dd89d5aeac71ca786ea9f2bdf833f770c471"));

            string base64 = Convert.ToBase64String(serialized);
            Assert.That(Convert.FromBase64String(base64), Is.EqualTo(serialized));

            var candidate = FingerprintTemplateTest.MatchingGray();
            double originalScore = new FingerprintMatcher(original).Match(candidate);
            double restoredScore = new FingerprintMatcher(restored).Match(candidate);
            Assert.That(restoredScore, Is.EqualTo(originalScore).Within(ScoreTolerance));
        }

        [Test]
        public void SourceAfis314SerializedTemplateRemainsCompatible()
        {
            byte[] legacyBytes = Convert.FromBase64String(SourceAfis314RawProbeTemplateBase64);
            Assert.That(Sha256(legacyBytes), Is.EqualTo("82747efcb1ded8e96c78259b3475dd89d5aeac71ca786ea9f2bdf833f770c471"));

            var legacy = new FingerprintTemplate(legacyBytes);
            var current = FingerprintTemplateTest.ProbeGray();
            AssertTemplateEquivalent(current, legacy);
            Assert.That(legacy.ToByteArray(), Is.EqualTo(legacyBytes));

            double score = new FingerprintMatcher(legacy).Match(FingerprintTemplateTest.MatchingGray());
            Assert.That(score, Is.EqualTo(106.51580124192927).Within(ScoreTolerance));
        }

        [Test]
        public void TransparencyPayloadsContainStructuredAlgorithmData()
        {
            FingerprintTemplate probe;
            using (var extraction = new SelectedTransparency("ridges-traced-skeleton", "shuffled-minutiae", "edge-table"))
            {
                probe = FingerprintTemplateTest.ProbeGray();
                var skeleton = extraction.SingleCbor<SkeletonSnapshot>("ridges-traced-skeleton");
                var features = extraction.SingleCbor<FeatureTemplateSnapshot>("shuffled-minutiae");
                var edges = extraction.SingleCbor<NeighborEdgeSnapshot[][]>("edge-table");
                Assert.Multiple(() =>
                {
                    Assert.That(skeleton.Width, Is.GreaterThan(0));
                    Assert.That(skeleton.Height, Is.GreaterThan(0));
                    Assert.That(skeleton.Minutiae, Is.Not.Empty);
                    Assert.That(skeleton.Ridges, Is.Not.Empty);
                    Assert.That(features.Minutiae, Has.Count.EqualTo(probe.Minutiae.Length));
                    Assert.That(edges, Has.Length.EqualTo(probe.Edges.Length));
                });
            }

            using (var matching = new SelectedTransparency("edge-hash", "root-pairs", "roots", "best-pairing", "best-score", "best-match"))
            {
                var matcher = new FingerprintMatcher(probe);
                double score = matcher.Match(FingerprintTemplateTest.MatchingGray());
                var hash = matching.SingleCbor<List<HashEntrySnapshot>>("edge-hash");
                var roots = matching.SingleCbor<List<MinutiaPairSnapshot>>("roots");
                var pairing = matching.SingleCbor<PairingSnapshot>("best-pairing");
                var components = matching.SingleCbor<ScoreSnapshot>("best-score");
                Assert.Multiple(() =>
                {
                    Assert.That(hash, Is.Not.Empty);
                    Assert.That(hash.Sum(entry => entry.Edges.Count), Is.EqualTo(matcher.Hash.Values.Sum(bucket => bucket.Count)));
                    Assert.That(roots, Is.Not.Empty);
                    Assert.That(pairing.Tree, Is.Not.Empty);
                    Assert.That(components.ShapedScore, Is.EqualTo(score).Within(ScoreTolerance));
                });
            }
        }

        [Test]
        public void IdentifyOneToManyRanksGenuineCandidateFirst()
        {
            var probe = FingerprintTemplateTest.ProbeGray();
            var candidates = new[]
            {
                FingerprintTemplateTest.NonmatchingGray(),
                FingerprintTemplateTest.MatchingGray(),
                FingerprintTemplate.Empty
            };
            var matcher = new FingerprintMatcher(probe);
            var scores = candidates.Select(candidate => matcher.Match(candidate)).ToArray();
            var ranked = scores
                .Select((score, index) => (Index: index, Score: score))
                .OrderByDescending(result => result.Score)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(scores[0], Is.EqualTo(2.340766003178002).Within(ScoreTolerance));
                Assert.That(scores[1], Is.EqualTo(106.51580124192927).Within(ScoreTolerance));
                Assert.That(scores[2], Is.EqualTo(0).Within(ScoreTolerance));
                Assert.That(ranked[0].Index, Is.EqualTo(1));
                Assert.That(ranked[0].Score, Is.GreaterThan(40));
                Assert.That(ranked[0].Score, Is.GreaterThan(ranked[1].Score));
                Assert.That(ranked[2].Score, Is.EqualTo(0));
            });
        }

        [Test]
        public void RepeatedExtractionAndMatchingAreDeterministic()
        {
            var firstProbe = FingerprintTemplateTest.ProbeGray();
            var firstCandidate = FingerprintTemplateTest.MatchingGray();
            var secondProbe = FingerprintTemplateTest.ProbeGray();
            var secondCandidate = FingerprintTemplateTest.MatchingGray();

            Assert.That(secondProbe.ToByteArray(), Is.EqualTo(firstProbe.ToByteArray()));
            Assert.That(secondCandidate.ToByteArray(), Is.EqualTo(firstCandidate.ToByteArray()));
            double firstScore = new FingerprintMatcher(firstProbe).Match(firstCandidate);
            double secondScore = new FingerprintMatcher(secondProbe).Match(secondCandidate);
            Assert.That(secondScore, Is.EqualTo(firstScore).Within(ScoreTolerance));
        }
    }
}
