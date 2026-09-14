// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Collections.Generic;
using System.Linq;
using SourceAFIS.Engine.Primitives;

namespace SourceAFIS.Engine.Fusion
{
    readonly record struct FusionPoint(double X, double Y);

    readonly record struct RigidTransform(
        double Rotation,
        double TranslationX,
        double TranslationY)
    {
        public FusionPoint Apply(FusionPoint point)
        {
            double cosine = Math.Cos(Rotation);
            double sine = Math.Sin(Rotation);
            return new(
                cosine * point.X - sine * point.Y + TranslationX,
                sine * point.X + cosine * point.Y + TranslationY);
        }
    }

    sealed record TemplateAlignmentPair(
        int Reference,
        int Moving,
        double PositionError);

    sealed record TemplateAlignment(
        double Score,
        bool TransformAvailable,
        RigidTransform Transform,
        double RootMeanSquareError,
        double MaximumError,
        IReadOnlyList<TemplateAlignmentPair> Pairs);

    static class TemplateAligner
    {
#pragma warning disable CS0649 // Fields are populated by the CBOR deserializer.
        sealed class MinutiaPairSnapshot
        {
            public int Probe;
            public int Candidate;
        }

        sealed class EdgePairSnapshot
        {
            public int ProbeFrom;
            public int ProbeTo;
            public int CandidateFrom;
            public int CandidateTo;
        }

        sealed class PairingSnapshot
        {
            public MinutiaPairSnapshot Root;
            public List<EdgePairSnapshot> Tree;
        }
#pragma warning restore CS0649

        sealed class PairingCapture : FingerprintTransparency
        {
            byte[] pairing;

            public override bool Accepts(string key) => key == "best-pairing";

            public override void Take(string key, string mime, byte[] data)
            {
                if (key == "best-pairing" && mime == "application/cbor")
                    pairing = data.ToArray();
            }

            public IReadOnlyList<(int Reference, int Moving)> Pairs()
            {
                if (pairing == null)
                    return Array.Empty<(int, int)>();

                var snapshot = SerializationUtils.Deserialize<PairingSnapshot>(pairing);
                var pairs = new List<(int Reference, int Moving)>();
                var seen = new HashSet<(int Reference, int Moving)>();

                void Add(int reference, int moving)
                {
                    if (seen.Add((reference, moving)))
                        pairs.Add((reference, moving));
                }

                Add(snapshot.Root.Probe, snapshot.Root.Candidate);
                foreach (var edge in snapshot.Tree)
                    Add(edge.ProbeTo, edge.CandidateTo);
                return pairs;
            }
        }

        public static TemplateAlignment Align(
            FingerprintTemplate reference,
            FingerprintTemplate moving)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentNullException.ThrowIfNull(moving);

            double score;
            IReadOnlyList<(int Reference, int Moving)> matched;
            using (var capture = new PairingCapture())
            {
                score = new FingerprintMatcher(reference).Match(moving);
                matched = capture.Pairs();
            }

            var points = matched.Select(pair => (
                Reference: new FusionPoint(
                    reference.Minutiae[pair.Reference].Position.X,
                    reference.Minutiae[pair.Reference].Position.Y),
                Moving: new FusionPoint(
                    moving.Minutiae[pair.Moving].Position.X,
                    moving.Minutiae[pair.Moving].Position.Y))).ToArray();

            if (points.Length < 2)
                return new(
                    score,
                    false,
                    new RigidTransform(0, 0, 0),
                    double.PositiveInfinity,
                    double.PositiveInfinity,
                    Array.Empty<TemplateAlignmentPair>());

            RigidTransform transform = Estimate(points);
            var aligned = new TemplateAlignmentPair[points.Length];
            double squaredError = 0;
            double maximumError = 0;
            for (int i = 0; i < points.Length; ++i)
            {
                FusionPoint transformed = transform.Apply(points[i].Moving);
                double dx = transformed.X - points[i].Reference.X;
                double dy = transformed.Y - points[i].Reference.Y;
                double error = Math.Sqrt(dx * dx + dy * dy);
                squaredError += error * error;
                maximumError = Math.Max(maximumError, error);
                aligned[i] = new(matched[i].Reference, matched[i].Moving, error);
            }

            return new(
                score,
                true,
                transform,
                Math.Sqrt(squaredError / points.Length),
                maximumError,
                aligned);
        }

        internal static RigidTransform Estimate(
            IReadOnlyList<(FusionPoint Reference, FusionPoint Moving)> pairs)
        {
            ArgumentNullException.ThrowIfNull(pairs);
            if (pairs.Count < 2)
                throw new ArgumentException("At least two point pairs are required.", nameof(pairs));

            double referenceX = pairs.Average(pair => pair.Reference.X);
            double referenceY = pairs.Average(pair => pair.Reference.Y);
            double movingX = pairs.Average(pair => pair.Moving.X);
            double movingY = pairs.Average(pair => pair.Moving.Y);
            double dot = 0;
            double cross = 0;

            foreach (var pair in pairs)
            {
                double mx = pair.Moving.X - movingX;
                double my = pair.Moving.Y - movingY;
                double rx = pair.Reference.X - referenceX;
                double ry = pair.Reference.Y - referenceY;
                dot += mx * rx + my * ry;
                cross += mx * ry - my * rx;
            }

            double rotation = Math.Atan2(cross, dot);
            double cosine = Math.Cos(rotation);
            double sine = Math.Sin(rotation);
            double translationX = referenceX - (cosine * movingX - sine * movingY);
            double translationY = referenceY - (sine * movingX + cosine * movingY);
            return new(rotation, translationX, translationY);
        }
    }
}
