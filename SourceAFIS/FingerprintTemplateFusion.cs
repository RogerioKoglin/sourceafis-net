// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Collections.Generic;
using System.Linq;
using SourceAFIS.Engine.Configuration;
using SourceAFIS.Engine.Extractor;
using SourceAFIS.Engine.Fusion;
using SourceAFIS.Engine.Primitives;

namespace SourceAFIS
{
    /// <summary>One image/template pair participating in three-capture template fusion.</summary>
    public sealed class FingerprintTemplateFusionInput
    {
        internal BooleanMatrix Coverage { get; }

        /// <summary>Template extracted from <see cref="Image" />.</summary>
        public FingerprintTemplate Template { get; }

        /// <summary>Image from which <see cref="Template" /> was extracted.</summary>
        public FingerprintImage Image { get; }

        /// <summary>Extracts a template and prepares its coverage map for fusion.</summary>
        public FingerprintTemplateFusionInput(FingerprintImage image)
        {
            ArgumentNullException.ThrowIfNull(image);
            Image = image;
            Template = new FingerprintTemplate(image);
            Coverage = ExtractCoverage(image);
        }

        /// <summary>Reuses an already extracted template and prepares the corresponding image coverage map.</summary>
        /// <remarks>The template must have been extracted from the supplied image with the same SourceAFIS version.</remarks>
        public FingerprintTemplateFusionInput(FingerprintTemplate template, FingerprintImage image)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(image);
            Template = template;
            Image = image;
            Coverage = ExtractCoverage(image);
        }

        static BooleanMatrix ExtractCoverage(FingerprintImage image)
        {
            DoubleMatrix normalized = ImageResizer.Resize(image.Matrix, image.Dpi);
            var blocks = new BlockMap(normalized.Width, normalized.Height, Parameters.BlockSize);
            HistogramCube histogram = LocalHistograms.Create(blocks, normalized);
            BooleanMatrix blockMask = SegmentationMask.Compute(blocks, histogram);
            return SegmentationMask.Inner(SegmentationMask.Pixelwise(blockMask, blocks));
        }
    }

    /// <summary>Configuration of three-capture template fusion.</summary>
    public sealed class FingerprintTemplateFusionOptions
    {
        /// <summary>Minimum conservative SourceAFIS score required to accept a contributing capture.</summary>
        public double MinimumAlignmentScore { get; init; } = 40;

        /// <summary>Minimum matched minutiae required to accept a contributing capture.</summary>
        public int MinimumMatchedMinutiae { get; init; } = 8;

        /// <summary>
        /// Fraction of the displacement from the medoid minutia toward the aligned consensus.
        /// Zero preserves medoid geometry and one applies the complete consensus displacement.
        /// </summary>
        public double ReferenceGeometryBlend { get; init; } = .25;

        /// <summary>Whether to retain novel minutiae confirmed by both non-reference captures.</summary>
        public bool RetainSupportedNovelMinutiae { get; init; } = true;

        /// <summary>Whether to retain a contributor minutia when it adds image coverage absent from other captures.</summary>
        public bool RetainUniqueCoverageMinutiae { get; init; } = true;
    }

    /// <summary>Diagnostics for one non-reference capture considered during fusion.</summary>
    public sealed record FingerprintTemplateFusionContributor(
        int InputIndex,
        double ConservativeScore,
        int MatchedMinutiae,
        bool Accepted,
        double RmsPositionError,
        double MaximumPositionError);

    /// <summary>Result and diagnostics produced by three-capture template fusion.</summary>
    public sealed class FingerprintTemplateFusionResult
    {
        /// <summary>Ordinary SourceAFIS template containing the fused minutiae and rebuilt edge table.</summary>
        public FingerprintTemplate Template { get; }

        /// <summary>Index of the medoid input whose coordinate system anchors the result.</summary>
        public int ReferenceIndex { get; }

        /// <summary>Diagnostics for the two non-reference inputs.</summary>
        public IReadOnlyList<FingerprintTemplateFusionContributor> Contributors { get; }

        /// <summary>Minutiae in the medoid template.</summary>
        public int ReferenceMinutiae { get; }

        /// <summary>Minutiae in the fused template.</summary>
        public int OutputMinutiae { get; }

        /// <summary>True when both non-reference captures passed the alignment gate.</summary>
        public bool UsedAllThreeCaptures => Contributors.All(contributor => contributor.Accepted);

        internal FingerprintTemplateFusionResult(TemplateFusionResult result)
        {
            Template = result.Template;
            ReferenceIndex = result.Plan.Reference;
            ReferenceMinutiae = result.ReferenceMinutiae;
            OutputMinutiae = result.OutputMinutiae;
            Contributors = result.Plan.Alignments
                .OrderBy(alignment => alignment.Template)
                .Select(alignment => new FingerprintTemplateFusionContributor(
                    alignment.Template,
                    alignment.ConservativeScore,
                    alignment.Alignment.Pairs.Count,
                    alignment.Accepted,
                    alignment.Alignment.RootMeanSquareError,
                    alignment.Alignment.MaximumError))
                .ToArray();
        }
    }

    /// <summary>Fuses exactly three impressions of the same finger into one ordinary SourceAFIS template.</summary>
    public static class FingerprintTemplateFusion
    {
        /// <summary>Fuses three image/template pairs using a medoid-anchored coordinate system.</summary>
        public static FingerprintTemplateFusionResult Fuse(
            IReadOnlyList<FingerprintTemplateFusionInput> inputs,
            FingerprintTemplateFusionOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            if (inputs.Count != 3)
                throw new ArgumentException("Fingerprint template fusion requires exactly three inputs.", nameof(inputs));
            if (inputs.Any(input => input == null))
                throw new ArgumentException("Fingerprint template fusion does not accept null inputs.", nameof(inputs));
            options ??= new();
            if (!double.IsFinite(options.MinimumAlignmentScore) || options.MinimumAlignmentScore < 0)
                throw new ArgumentOutOfRangeException(nameof(options));
            if (options.MinimumMatchedMinutiae < 2)
                throw new ArgumentOutOfRangeException(nameof(options));
            if (!double.IsFinite(options.ReferenceGeometryBlend)
                || options.ReferenceGeometryBlend < 0
                || options.ReferenceGeometryBlend > 1)
                throw new ArgumentOutOfRangeException(nameof(options));

            var planning = new TemplateFusionOptions(
                options.MinimumAlignmentScore,
                options.MinimumMatchedMinutiae);
            var fusion = new TemplateFuserOptions(
                planning,
                AverageReferenceMinutiae: false,
                RetainSupportedNovelMinutiae: options.RetainSupportedNovelMinutiae,
                RetainUniqueCoverageMinutiae: options.RetainUniqueCoverageMinutiae,
                AnchoredReferenceBlend: options.ReferenceGeometryBlend);
            TemplateFusionResult result = TemplateFuser.Fuse(
                inputs.Select(input => new TemplateFusionInput(input.Template, input.Coverage)).ToArray(),
                fusion);
            return new(result);
        }
    }
}
