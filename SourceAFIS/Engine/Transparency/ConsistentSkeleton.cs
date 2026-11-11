// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System.Collections.Generic;
using System.Linq;
using SourceAFIS.Engine.Features;
using SourceAFIS.Engine.Primitives;

namespace SourceAFIS.Engine.Transparency
{
    class ConsistentSkeleton
    {
        public readonly int Width;
        public readonly int Height;
        public readonly List<IntPoint> Minutiae;
        public readonly List<ConsistentSkeletonRidge> Ridges;

        public ConsistentSkeleton(int width, int height, List<IntPoint> minutiae, List<ConsistentSkeletonRidge> ridges)
        {
            Width = width;
            Height = height;
            Minutiae = minutiae;
            Ridges = ridges;
        }

        public static ConsistentSkeleton Of(Skeleton skeleton)
        {
            var offsets = new Dictionary<SkeletonMinutia, int>();
            for (int i = 0; i < skeleton.Minutiae.Count; ++i)
                offsets[skeleton.Minutiae[i]] = i;
            return new(
                skeleton.Size.X,
                skeleton.Size.Y,
                (from m in skeleton.Minutiae select m.Position).ToList(),
                (from m in skeleton.Minutiae
                 from r in m.Ridges
                 where r.Points is CircularList<IntPoint>
                 select new ConsistentSkeletonRidge(offsets[r.Start], offsets[r.End], r.Points)).ToList());
        }
    }
}
