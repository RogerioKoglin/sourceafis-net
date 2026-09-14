// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Linq;
using NUnit.Framework;
using SourceAFIS.Engine.Fusion;

namespace SourceAFIS
{
    public class TemplateFuserTest
    {
        [Test]
        public void IdenticalInputsProduceStableOrdinaryTemplate()
        {
            FingerprintTemplate original = FingerprintTemplateTest.ProbeGray();

            TemplateFusionResult result = TemplateFuser.Fuse(
            [
                new(original),
                new(original),
                new(original)
            ]);
            byte[] serialized = result.Template.ToByteArray();
            var restored = new FingerprintTemplate(serialized);

            Assert.Multiple(() =>
            {
                Assert.That(result.Plan.Reference, Is.EqualTo(0));
                Assert.That(result.Plan.Alignments.All(alignment => alignment.Accepted), Is.True);
                Assert.That(result.OutputMinutiae, Is.EqualTo(original.Minutiae.Length));
                Assert.That(result.MergedReferenceMinutiae, Is.EqualTo(original.Minutiae.Length));
                Assert.That(result.RetainedSupportedNovelMinutiae, Is.Zero);
                Assert.That(result.RetainedUniqueCoverageMinutiae, Is.Zero);
                Assert.That(serialized, Is.EqualTo(original.ToByteArray()));
                Assert.That(restored.ToByteArray(), Is.EqualTo(serialized));
            });
        }

        [Test]
        public void RejectedOutlierCannotContributeMinutiae()
        {
            FingerprintTemplate reference = FingerprintTemplateTest.ProbeGray();
            FingerprintTemplate matching = FingerprintTemplateTest.MatchingGray();
            FingerprintTemplate outlier = FingerprintTemplateTest.NonmatchingGray();

            TemplateFusionResult result = TemplateFuser.Fuse(
            [
                new(reference),
                new(matching),
                new(outlier)
            ]);

            Assert.Multiple(() =>
            {
                Assert.That(result.Plan.Reference, Is.EqualTo(0));
                Assert.That(result.Plan.Alignments.Single(alignment => alignment.Template == 1).Accepted, Is.True);
                Assert.That(result.Plan.Alignments.Single(alignment => alignment.Template == 2).Accepted, Is.False);
                Assert.That(result.RetainedSupportedNovelMinutiae, Is.Zero);
                Assert.That(result.RetainedUniqueCoverageMinutiae, Is.Zero);
                Assert.That(result.OutputMinutiae, Is.EqualTo(result.ReferenceMinutiae));
            });
        }

        [Test]
        public void AnchoredModePreservesReferenceMatcherGeometry()
        {
            FingerprintTemplate reference = FingerprintTemplateTest.ProbeGray();
            FingerprintTemplate matching = FingerprintTemplateTest.MatchingGray();
            FingerprintTemplate outlier = FingerprintTemplateTest.NonmatchingGray();
            double expected = new FingerprintMatcher(reference).Match(reference);

            TemplateFusionResult result = TemplateFuser.Fuse(
            [
                new(reference),
                new(matching),
                new(outlier)
            ],
            new(
                AverageReferenceMinutiae: false,
                RetainSupportedNovelMinutiae: false,
                RetainUniqueCoverageMinutiae: false));
            double actual = new FingerprintMatcher(reference).Match(result.Template);

            Assert.Multiple(() =>
            {
                Assert.That(result.OutputMinutiae, Is.EqualTo(reference.Minutiae.Length));
                Assert.That(actual, Is.EqualTo(expected).Within(1e-9));
            });
        }

        [Test]
        public void RobustAlignmentRejectsGeometricOutlier()
        {
            var expected = new RigidTransform(Math.PI / 8, 14, -9);
            FusionPoint[] moving =
            [
                new(0, 0),
                new(50, 0),
                new(0, 60),
                new(50, 60),
                new(20, 30)
            ];
            var pairs = moving
                .Select(point => (Reference: expected.Apply(point), Moving: point))
                .Append((Reference: new FusionPoint(400, -300), Moving: new FusionPoint(10, 15)))
                .ToArray();

            int[] inliers = TemplateAlignmentRobustifier.SelectInliers(pairs, 5);
            RigidTransform actual = TemplateAligner.Estimate(inliers.Select(index => pairs[index]).ToArray());

            Assert.Multiple(() =>
            {
                Assert.That(inliers, Has.Length.EqualTo(5));
                Assert.That(inliers, Does.Not.Contain(5));
                Assert.That(actual.Rotation, Is.EqualTo(expected.Rotation).Within(1e-9));
                Assert.That(actual.TranslationX, Is.EqualTo(expected.TranslationX).Within(1e-9));
                Assert.That(actual.TranslationY, Is.EqualTo(expected.TranslationY).Within(1e-9));
            });
        }
    }
}
