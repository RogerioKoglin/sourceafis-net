// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Collections.Generic;
using System.Linq;

namespace SourceAFIS.Engine.Fusion
{
    sealed record TemplateFusionPairScore(
        int First,
        int Second,
        double Forward,
        double Reverse,
        double Conservative);

    sealed record TemplateFusionOptions(
        double MinimumAlignmentScore = 40,
        int MinimumMatchedMinutiae = 8);

    sealed record TemplateFusionAlignment(
        int Template,
        double ConservativeScore,
        bool Accepted,
        TemplateAlignment Alignment);

    sealed record TemplateFusionPlan(
        int Reference,
        IReadOnlyList<TemplateFusionPairScore> Scores,
        IReadOnlyList<TemplateFusionAlignment> Alignments);

    static class TemplateFusionPlanner
    {
        public static TemplateFusionPlan Plan(
            IReadOnlyList<FingerprintTemplate> templates,
            TemplateFusionOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(templates);
            options ??= new();
            if (templates.Count != 3)
                throw new ArgumentException("Template fusion currently requires exactly three templates.", nameof(templates));
            if (templates.Any(template => template == null))
                throw new ArgumentException("Template fusion does not accept null templates.", nameof(templates));
            if (!double.IsFinite(options.MinimumAlignmentScore) || options.MinimumAlignmentScore < 0)
                throw new ArgumentOutOfRangeException(nameof(options), "Minimum alignment score must be finite and non-negative.");
            if (options.MinimumMatchedMinutiae < 2)
                throw new ArgumentOutOfRangeException(nameof(options), "At least two matched minutiae are required.");

            double[,] symmetric = new double[3, 3];
            var scores = new List<TemplateFusionPairScore>(3);
            for (int first = 0; first < 2; ++first)
            {
                for (int second = first + 1; second < 3; ++second)
                {
                    double forward = new FingerprintMatcher(templates[first]).Match(templates[second]);
                    double reverse = new FingerprintMatcher(templates[second]).Match(templates[first]);
                    double conservative = Math.Min(forward, reverse);
                    symmetric[first, second] = conservative;
                    symmetric[second, first] = conservative;
                    scores.Add(new(first, second, forward, reverse, conservative));
                }
            }

            int reference = Enumerable.Range(0, 3)
                .Select(index => new
                {
                    Index = index,
                    Minimum = Enumerable.Range(0, 3)
                        .Where(other => other != index)
                        .Min(other => symmetric[index, other]),
                    Average = Enumerable.Range(0, 3)
                        .Where(other => other != index)
                        .Average(other => symmetric[index, other])
                })
                .OrderByDescending(candidate => candidate.Minimum)
                .ThenByDescending(candidate => candidate.Average)
                .ThenBy(candidate => candidate.Index)
                .First()
                .Index;

            var alignments = Enumerable.Range(0, 3)
                .Where(index => index != reference)
                .Select(index =>
                {
                    TemplateAlignment alignment = TemplateAligner.Align(templates[reference], templates[index]);
                    TemplateFusionPairScore pair = scores.Single(score =>
                        score.First == Math.Min(reference, index)
                        && score.Second == Math.Max(reference, index));
                    bool accepted = alignment.TransformAvailable
                        && pair.Conservative >= options.MinimumAlignmentScore
                        && alignment.Pairs.Count >= options.MinimumMatchedMinutiae;
                    return new TemplateFusionAlignment(index, pair.Conservative, accepted, alignment);
                })
                .ToArray();
            return new(reference, scores, alignments);
        }
    }
}
