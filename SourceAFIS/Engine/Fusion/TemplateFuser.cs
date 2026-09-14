// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using System.Collections.Generic;
using System.Linq;
using SourceAFIS.Engine.Features;
using SourceAFIS.Engine.Primitives;
using SourceAFIS.Engine.Templates;

namespace SourceAFIS.Engine.Fusion
{
    sealed record TemplateFusionInput(
        FingerprintTemplate Template,
        BooleanMatrix Coverage = null);

    sealed record TemplateFuserOptions(
        TemplateFusionOptions Planning = null,
        double MaximumPairPositionError = 13,
        double DuplicatePositionRadius = 13,
        double DuplicateDirectionTolerance = Math.PI / 9,
        int MinimumNovelSupport = 2,
        int MaximumMinutiae = 100,
        bool AverageReferenceMinutiae = true,
        bool RetainSupportedNovelMinutiae = true,
        bool RetainUniqueCoverageMinutiae = true);

    sealed record TemplateFusionResult(
        FingerprintTemplate Template,
        TemplateFusionPlan Plan,
        int ReferenceMinutiae,
        int OutputMinutiae,
        int MergedReferenceMinutiae,
        int RetainedSupportedNovelMinutiae,
        int RetainedUniqueCoverageMinutiae,
        int DroppedUnsupportedMinutiae,
        int DroppedByLimit);

    static class TemplateAlignmentRobustifier
    {
        public static TemplateAlignment Refine(
            FingerprintTemplate reference,
            FingerprintTemplate moving,
            TemplateAlignment alignment,
            double maximumPositionError)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentNullException.ThrowIfNull(moving);
            ArgumentNullException.ThrowIfNull(alignment);
            if (!double.IsFinite(maximumPositionError) || maximumPositionError <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumPositionError));
            if (!alignment.TransformAvailable || alignment.Pairs.Count < 3)
                return alignment;

            var pairs = alignment.Pairs.Select(pair => (
                Reference: new FusionPoint(
                    reference.Minutiae[pair.Reference].Position.X,
                    reference.Minutiae[pair.Reference].Position.Y),
                Moving: new FusionPoint(
                    moving.Minutiae[pair.Moving].Position.X,
                    moving.Minutiae[pair.Moving].Position.Y))).ToArray();
            int[] inliers = SelectInliers(pairs, maximumPositionError);
            if (inliers.Length < 2)
                return alignment;

            var selected = inliers.Select(index => pairs[index]).ToArray();
            RigidTransform transform = TemplateAligner.Estimate(selected);
            var refined = new TemplateAlignmentPair[inliers.Length];
            double squaredError = 0;
            double maximumError = 0;
            for (int i = 0; i < inliers.Length; ++i)
            {
                int index = inliers[i];
                FusionPoint transformed = transform.Apply(pairs[index].Moving);
                double error = Distance(transformed, pairs[index].Reference);
                squaredError += error * error;
                maximumError = Math.Max(maximumError, error);
                TemplateAlignmentPair original = alignment.Pairs[index];
                refined[i] = new(original.Reference, original.Moving, error);
            }
            return new(
                alignment.Score,
                true,
                transform,
                Math.Sqrt(squaredError / refined.Length),
                maximumError,
                refined);
        }

        internal static int[] SelectInliers(
            IReadOnlyList<(FusionPoint Reference, FusionPoint Moving)> pairs,
            double maximumPositionError)
        {
            ArgumentNullException.ThrowIfNull(pairs);
            if (pairs.Count < 2)
                return Array.Empty<int>();

            int[] best = Array.Empty<int>();
            double bestSquaredError = double.PositiveInfinity;
            for (int first = 0; first < pairs.Count - 1; ++first)
            {
                for (int second = first + 1; second < pairs.Count; ++second)
                {
                    if (Distance(pairs[first].Moving, pairs[second].Moving) < 1
                        || Distance(pairs[first].Reference, pairs[second].Reference) < 1)
                        continue;
                    RigidTransform candidate = TemplateAligner.Estimate([pairs[first], pairs[second]]);
                    (int[] Inliers, double SquaredError) evaluated = Evaluate(
                        pairs,
                        candidate,
                        maximumPositionError);
                    if (evaluated.Inliers.Length > best.Length
                        || evaluated.Inliers.Length == best.Length
                            && evaluated.SquaredError < bestSquaredError)
                    {
                        best = evaluated.Inliers;
                        bestSquaredError = evaluated.SquaredError;
                    }
                }
            }

            if (best.Length < 2)
                return Enumerable.Range(0, pairs.Count).ToArray();
            for (int iteration = 0; iteration < 3; ++iteration)
            {
                RigidTransform refined = TemplateAligner.Estimate(best.Select(index => pairs[index]).ToArray());
                int[] next = Evaluate(pairs, refined, maximumPositionError).Inliers;
                if (next.Length < 2 || next.SequenceEqual(best))
                    break;
                best = next;
            }
            return best;
        }

        static (int[] Inliers, double SquaredError) Evaluate(
            IReadOnlyList<(FusionPoint Reference, FusionPoint Moving)> pairs,
            RigidTransform transform,
            double maximumPositionError)
        {
            var inliers = new List<int>();
            double squaredError = 0;
            for (int i = 0; i < pairs.Count; ++i)
            {
                double error = Distance(transform.Apply(pairs[i].Moving), pairs[i].Reference);
                if (error <= maximumPositionError)
                {
                    inliers.Add(i);
                    squaredError += error * error;
                }
            }
            return (inliers.ToArray(), squaredError);
        }

        static double Distance(FusionPoint first, FusionPoint second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    static class TemplateFuser
    {
        sealed record Observation(
            int Template,
            int Minutia,
            double X,
            double Y,
            float Direction,
            MinutiaType Type);

        sealed class Group
        {
            public readonly List<Observation> Observations = new();
            public bool HasReference;

            public bool Contains(int template) =>
                Observations.Any(observation => observation.Template == template);

            public int Support => Observations
                .Select(observation => observation.Template)
                .Distinct()
                .Count();
        }

        sealed record SelectedGroup(Group Group, FusionPoint Point, bool UniqueCoverage);

        public static TemplateFusionResult Fuse(
            IReadOnlyList<TemplateFusionInput> inputs,
            TemplateFuserOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            options ??= new();
            TemplateFusionOptions planning = options.Planning ?? new();
            Validate(inputs, options);

            TemplateFusionPlan plan = TemplateFusionPlanner.Plan(
                inputs.Select(input => input.Template).ToArray(),
                planning);
            TemplateFusionAlignment[] refinedAlignments = plan.Alignments
                .Select(candidate =>
                {
                    TemplateAlignment refined = TemplateAlignmentRobustifier.Refine(
                        inputs[plan.Reference].Template,
                        inputs[candidate.Template].Template,
                        candidate.Alignment,
                        options.MaximumPairPositionError);
                    bool accepted = refined.TransformAvailable
                        && candidate.ConservativeScore >= planning.MinimumAlignmentScore
                        && refined.Pairs.Count >= planning.MinimumMatchedMinutiae;
                    return new TemplateFusionAlignment(
                        candidate.Template,
                        candidate.ConservativeScore,
                        accepted,
                        refined);
                })
                .ToArray();
            plan = plan with { Alignments = refinedAlignments };

            FingerprintTemplate reference = inputs[plan.Reference].Template;
            var groups = new List<Group>(reference.Minutiae.Length);
            var referenceGroups = new Group[reference.Minutiae.Length];
            for (int i = 0; i < reference.Minutiae.Length; ++i)
            {
                var group = new Group { HasReference = true };
                group.Observations.Add(Observe(plan.Reference, i, reference.Minutiae[i], null));
                groups.Add(group);
                referenceGroups[i] = group;
            }

            foreach (TemplateFusionAlignment contributor in plan.Alignments
                .Where(alignment => alignment.Accepted)
                .OrderBy(alignment => alignment.Template))
            {
                FingerprintTemplate moving = inputs[contributor.Template].Template;
                RigidTransform transform = contributor.Alignment.Transform;
                Dictionary<int, int> paired = contributor.Alignment.Pairs
                    .GroupBy(pair => pair.Moving)
                    .ToDictionary(group => group.Key, group => group.First().Reference);
                for (int i = 0; i < moving.Minutiae.Length; ++i)
                {
                    Observation observation = Observe(
                        contributor.Template,
                        i,
                        moving.Minutiae[i],
                        transform);
                    Group target;
                    if (paired.TryGetValue(i, out int referenceIndex))
                        target = referenceGroups[referenceIndex];
                    else
                        target = FindCompatible(groups, observation, options);
                    if (target == null)
                    {
                        target = new Group();
                        groups.Add(target);
                    }
                    target.Observations.Add(observation);
                }
            }

            int mergedReference = groups.Count(group => group.HasReference && group.Support > 1);
            int retainedSupportedNovel = 0;
            int retainedUniqueCoverage = 0;
            int droppedUnsupported = 0;
            var selected = new List<SelectedGroup>();
            foreach (Group group in groups)
            {
                FusionPoint point = OutputPosition(group, plan.Reference, options);
                if (group.HasReference)
                    selected.Add(new(group, point, false));
                else if (options.RetainSupportedNovelMinutiae
                    && group.Support >= options.MinimumNovelSupport)
                {
                    ++retainedSupportedNovel;
                    selected.Add(new(group, point, false));
                }
                else if (options.RetainUniqueCoverageMinutiae
                    && HasUniqueCoverage(group, point, inputs, plan))
                {
                    ++retainedUniqueCoverage;
                    selected.Add(new(group, point, true));
                }
                else
                    ++droppedUnsupported;
            }

            SelectedGroup[] ordered = selected
                .OrderByDescending(item => item.Group.HasReference)
                .ThenByDescending(item => item.Group.Support)
                .ThenBy(item => item.UniqueCoverage)
                .ThenBy(item => item.Point.Y)
                .ThenBy(item => item.Point.X)
                .ToArray();
            int droppedByLimit = Math.Max(0, ordered.Length - options.MaximumMinutiae);
            ordered = ordered.Take(options.MaximumMinutiae).ToArray();

            (double ShiftX, double ShiftY, short Width, short Height) canvas = Canvas(inputs, plan, ordered);
            var minutiae = ordered.Select(item => new Minutia(
                new ShortPoint(
                    Doubles.RoundToInt(item.Point.X + canvas.ShiftX),
                    Doubles.RoundToInt(item.Point.Y + canvas.ShiftY)),
                OutputDirection(item.Group, plan.Reference, options),
                OutputType(item.Group, plan.Reference, options))).ToList();
            var features = new FeatureTemplate(new(canvas.Width, canvas.Height), minutiae);
            byte[] serialized = SerializationUtils.Serialize(new PersistentTemplate(features));
            var fused = new FingerprintTemplate(serialized);
            return new(
                fused,
                plan,
                reference.Minutiae.Length,
                fused.Minutiae.Length,
                mergedReference,
                retainedSupportedNovel,
                retainedUniqueCoverage,
                droppedUnsupported,
                droppedByLimit);
        }

        static void Validate(
            IReadOnlyList<TemplateFusionInput> inputs,
            TemplateFuserOptions options)
        {
            if (inputs.Count != 3)
                throw new ArgumentException("Template fusion currently requires exactly three inputs.", nameof(inputs));
            if (inputs.Any(input => input == null || input.Template == null))
                throw new ArgumentException("Template fusion does not accept null inputs or templates.", nameof(inputs));
            foreach (TemplateFusionInput input in inputs)
            {
                if (input.Coverage != null
                    && (input.Coverage.Width != input.Template.Size.X
                        || input.Coverage.Height != input.Template.Size.Y))
                    throw new ArgumentException("Coverage dimensions must match template dimensions.", nameof(inputs));
            }
            if (!double.IsFinite(options.DuplicatePositionRadius) || options.DuplicatePositionRadius <= 0)
                throw new ArgumentOutOfRangeException(nameof(options));
            if (!double.IsFinite(options.MaximumPairPositionError) || options.MaximumPairPositionError <= 0)
                throw new ArgumentOutOfRangeException(nameof(options));
            if (!double.IsFinite(options.DuplicateDirectionTolerance)
                || options.DuplicateDirectionTolerance <= 0
                || options.DuplicateDirectionTolerance > Math.PI)
                throw new ArgumentOutOfRangeException(nameof(options));
            if (options.MinimumNovelSupport < 2 || options.MinimumNovelSupport > 3)
                throw new ArgumentOutOfRangeException(nameof(options));
            if (options.MaximumMinutiae <= 0 || options.MaximumMinutiae > short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        static Observation Observe(
            int template,
            int index,
            Minutia minutia,
            RigidTransform? transform)
        {
            FusionPoint point = new(minutia.Position.X, minutia.Position.Y);
            double rotation = 0;
            if (transform.HasValue)
            {
                point = transform.Value.Apply(point);
                rotation = transform.Value.Rotation;
            }
            return new(
                template,
                index,
                point.X,
                point.Y,
                (float)Normalize(minutia.Direction + rotation),
                minutia.Type);
        }

        static Group FindCompatible(
            IReadOnlyList<Group> groups,
            Observation observation,
            TemplateFuserOptions options)
        {
            Group best = null;
            double bestCost = double.PositiveInfinity;
            foreach (Group group in groups)
            {
                if (group.Contains(observation.Template) || Type(group, -1) != observation.Type)
                    continue;
                FusionPoint point = Position(group);
                double dx = point.X - observation.X;
                double dy = point.Y - observation.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > options.DuplicatePositionRadius)
                    continue;
                double direction = FloatAngle.Distance(Direction(group), observation.Direction);
                if (direction > options.DuplicateDirectionTolerance)
                    continue;
                double cost = distance / options.DuplicatePositionRadius
                    + direction / options.DuplicateDirectionTolerance;
                if (cost < bestCost)
                {
                    best = group;
                    bestCost = cost;
                }
            }
            return best;
        }

        static FusionPoint Position(Group group) => new(
            group.Observations.Average(observation => observation.X),
            group.Observations.Average(observation => observation.Y));

        static FusionPoint OutputPosition(
            Group group,
            int reference,
            TemplateFuserOptions options)
        {
            Observation anchor = group.Observations.FirstOrDefault(observation =>
                observation.Template == reference);
            return anchor != null && !options.AverageReferenceMinutiae
                ? new(anchor.X, anchor.Y)
                : Position(group);
        }

        static float Direction(Group group)
        {
            double x = group.Observations.Sum(observation => Math.Cos(observation.Direction));
            double y = group.Observations.Sum(observation => Math.Sin(observation.Direction));
            if (x * x + y * y < 1e-9)
                return group.Observations[0].Direction;
            return (float)Normalize(Math.Atan2(y, x));
        }

        static float OutputDirection(
            Group group,
            int reference,
            TemplateFuserOptions options)
        {
            Observation anchor = group.Observations.FirstOrDefault(observation =>
                observation.Template == reference);
            return anchor != null && !options.AverageReferenceMinutiae
                ? anchor.Direction
                : Direction(group);
        }

        static MinutiaType Type(Group group, int reference)
        {
            int endings = group.Observations.Count(observation => observation.Type == MinutiaType.Ending);
            int bifurcations = group.Observations.Count - endings;
            if (endings != bifurcations)
                return endings > bifurcations ? MinutiaType.Ending : MinutiaType.Bifurcation;
            Observation anchor = group.Observations.FirstOrDefault(observation => observation.Template == reference)
                ?? group.Observations[0];
            return anchor.Type;
        }

        static MinutiaType OutputType(
            Group group,
            int reference,
            TemplateFuserOptions options)
        {
            Observation anchor = group.Observations.FirstOrDefault(observation =>
                observation.Template == reference);
            return anchor != null && !options.AverageReferenceMinutiae
                ? anchor.Type
                : Type(group, reference);
        }

        static bool HasUniqueCoverage(
            Group group,
            FusionPoint point,
            IReadOnlyList<TemplateFusionInput> inputs,
            TemplateFusionPlan plan)
        {
            int[] active = plan.Alignments
                .Where(alignment => alignment.Accepted)
                .Select(alignment => alignment.Template)
                .Append(plan.Reference)
                .ToArray();
            if (active.Any(index => inputs[index].Coverage == null))
                return false;
            foreach (int index in active)
            {
                if (group.Contains(index))
                    continue;
                FusionPoint source = index == plan.Reference
                    ? point
                    : Inverse(
                        plan.Alignments.Single(alignment => alignment.Template == index).Alignment.Transform,
                        point);
                if (inputs[index].Coverage.Get(
                    Doubles.RoundToInt(source.X),
                    Doubles.RoundToInt(source.Y),
                    false))
                    return false;
            }
            return true;
        }

        static FusionPoint Inverse(RigidTransform transform, FusionPoint point)
        {
            double x = point.X - transform.TranslationX;
            double y = point.Y - transform.TranslationY;
            double cosine = Math.Cos(transform.Rotation);
            double sine = Math.Sin(transform.Rotation);
            return new(
                cosine * x + sine * y,
                -sine * x + cosine * y);
        }

        static (double ShiftX, double ShiftY, short Width, short Height) Canvas(
            IReadOnlyList<TemplateFusionInput> inputs,
            TemplateFusionPlan plan,
            IReadOnlyList<SelectedGroup> selected)
        {
            var corners = new List<FusionPoint>();
            AddCorners(corners, inputs[plan.Reference].Template, null);
            foreach (TemplateFusionAlignment alignment in plan.Alignments.Where(item => item.Accepted))
                AddCorners(corners, inputs[alignment.Template].Template, alignment.Alignment.Transform);
            corners.AddRange(selected.Select(item => item.Point));
            double minimumX = corners.Min(point => point.X);
            double minimumY = corners.Min(point => point.Y);
            double maximumX = corners.Max(point => point.X);
            double maximumY = corners.Max(point => point.Y);
            double shiftX = minimumX < 0 ? Math.Ceiling(-minimumX) : 0;
            double shiftY = minimumY < 0 ? Math.Ceiling(-minimumY) : 0;
            int width = (int)Math.Ceiling(maximumX + shiftX) + 1;
            int height = (int)Math.Ceiling(maximumY + shiftY) + 1;
            if (width > short.MaxValue || height > short.MaxValue)
                throw new InvalidOperationException("Fused template canvas exceeds supported dimensions.");
            return (shiftX, shiftY, (short)width, (short)height);
        }

        static void AddCorners(
            ICollection<FusionPoint> output,
            FingerprintTemplate template,
            RigidTransform? transform)
        {
            FusionPoint[] corners =
            [
                new(0, 0),
                new(template.Size.X - 1, 0),
                new(0, template.Size.Y - 1),
                new(template.Size.X - 1, template.Size.Y - 1)
            ];
            foreach (FusionPoint corner in corners)
                output.Add(transform.HasValue ? transform.Value.Apply(corner) : corner);
        }

        static double Normalize(double angle)
        {
            angle %= DoubleAngle.Pi2;
            return angle >= 0 ? angle : angle + DoubleAngle.Pi2;
        }
    }
}
