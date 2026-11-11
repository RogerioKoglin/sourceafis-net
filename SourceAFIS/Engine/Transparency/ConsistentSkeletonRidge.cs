// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System.Collections.Generic;
using SourceAFIS.Engine.Primitives;

namespace SourceAFIS.Engine.Transparency
{
    class ConsistentSkeletonRidge
    {
        public readonly int Start;
        public readonly int End;
        public readonly IList<IntPoint> Points;

        public ConsistentSkeletonRidge(int start, int end, IList<IntPoint> points)
        {
            Start = start;
            End = end;
            Points = points;
        }
    }
}
