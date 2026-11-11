// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Linq;
using NUnit.Framework;
using SourceAFIS.Engine.Fusion;

namespace SourceAFIS
{
    public class TemplateFusionPlannerTest
    {
        const double Tolerance = 1e-9;

        [Test]
        public void RigidAlignmentRecoversKnownTransform()
        {
            var expected = new RigidTransform(Math.PI / 7, 23.5, -11.25);
            FusionPoint[] moving =
            [
                new(-15, -10),
                new(20, -8),
                new(-12, 31),
                new(27, 35)
            ];
            var pairs = moving
                .Select(point => (Reference: expected.Apply(point), Moving: point))
                .ToArray();

            RigidTransform actual = TemplateAligner.Estimate(pairs);

            Assert.Multiple(() =>
            {
                Assert.That(actual.Rotation, Is.EqualTo(expected.Rotation).Within(Tolerance));
                Assert.That(actual.TranslationX, Is.EqualTo(expected.TranslationX).Within(Tolerance));
                Assert.That(actual.TranslationY, Is.EqualTo(expected.TranslationY).Within(Tolerance));
            });
        }

        [Test]
        public void IdenticalTemplateProducesExactAlignment()
        {
            FingerprintTemplate template = FingerprintTemplateTest.ProbeGray();

            TemplateAlignment alignment = TemplateAligner.Align(template, template);

            TestContext.WriteLine(Describe(alignment));
            Assert.Multiple(() =>
            {
                Assert.That(alignment.TransformAvailable, Is.True);
                Assert.That(alignment.Pairs, Has.Count.EqualTo(template.Minutiae.Length));
                Assert.That(alignment.Transform.Rotation, Is.EqualTo(0).Within(Tolerance));
                Assert.That(alignment.Transform.TranslationX, Is.EqualTo(0).Within(Tolerance));
                Assert.That(alignment.Transform.TranslationY, Is.EqualTo(0).Within(Tolerance));
                Assert.That(alignment.RootMeanSquareError, Is.EqualTo(0).Within(Tolerance));
                Assert.That(alignment.MaximumError, Is.EqualTo(0).Within(Tolerance));
            });
        }

        [Test]
        public void PlannerKeepsMatchingPairAwayFromOutlier()
        {
            FingerprintTemplate probe = FingerprintTemplateTest.ProbeGray();
            FingerprintTemplate matching = FingerprintTemplateTest.MatchingGray();
            FingerprintTemplate outlier = FingerprintTemplateTest.NonmatchingGray();

            TemplateFusionPlan plan = TemplateFusionPlanner.Plan([probe, matching, outlier]);

            foreach (TemplateFusionPairScore score in plan.Scores)
                TestContext.WriteLine($"pair={score.First}-{score.Second},forward={score.Forward:R},reverse={score.Reverse:R},conservative={score.Conservative:R}");
            TestContext.WriteLine($"reference={plan.Reference}");
            foreach (TemplateFusionAlignment alignment in plan.Alignments)
                TestContext.WriteLine($"template={alignment.Template},accepted={alignment.Accepted}," + Describe(alignment.Alignment));

            TemplateFusionPairScore genuine = plan.Scores.Single(score => score.First == 0 && score.Second == 1);
            TemplateFusionAlignment matchingAlignment = plan.Alignments.Single(alignment => alignment.Template == 1);
            TemplateFusionAlignment outlierAlignment = plan.Alignments.Single(alignment => alignment.Template == 2);
            Assert.Multiple(() =>
            {
                Assert.That(plan.Reference, Is.EqualTo(0));
                Assert.That(genuine.Conservative, Is.GreaterThan(40));
                Assert.That(plan.Alignments, Has.Count.EqualTo(2));
                Assert.That(matchingAlignment.Accepted, Is.True);
                Assert.That(matchingAlignment.Alignment.Pairs, Has.Count.GreaterThanOrEqualTo(8));
                Assert.That(outlierAlignment.Accepted, Is.False);
                Assert.That(outlierAlignment.Alignment.Pairs, Has.Count.LessThan(8));
            });
        }

        static string Describe(TemplateAlignment alignment) =>
            $"score={alignment.Score:R},pairs={alignment.Pairs.Count},rotation={alignment.Transform.Rotation:R}," +
            $"translation=({alignment.Transform.TranslationX:R},{alignment.Transform.TranslationY:R})," +
            $"rms={alignment.RootMeanSquareError:R},max={alignment.MaximumError:R}";
    }
}
