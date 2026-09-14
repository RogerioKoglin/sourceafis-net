// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System.Collections.Generic;
using System.Linq;
using SourceAFIS.Engine.Matcher;

namespace SourceAFIS.Engine.Transparency
{
    class ConsistentPairingGraph
    {
        public readonly ConsistentMinutiaPair Root;
        public readonly List<ConsistentEdgePair> Tree;
        public readonly List<ConsistentEdgePair> Support;

        public ConsistentPairingGraph(int count, MinutiaPair[] pairs, List<MinutiaPair> support)
        {
            Root = new ConsistentMinutiaPair(pairs[0].Probe, pairs[0].Candidate);
            Tree = (from p in pairs select new ConsistentEdgePair(p)).Take(count).ToList();
            Support = (from p in support select new ConsistentEdgePair(p)).ToList();
        }
    }
}
