// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System.Collections.Generic;
using SourceAFIS.Engine.Features;

namespace SourceAFIS.Engine.Transparency
{
    class ConsistentHashEntry
    {
        public readonly int Key;
        public readonly List<IndexedEdge> Edges;

        public ConsistentHashEntry(int key, List<IndexedEdge> edges)
        {
            Key = key;
            Edges = edges;
        }
    }
}
