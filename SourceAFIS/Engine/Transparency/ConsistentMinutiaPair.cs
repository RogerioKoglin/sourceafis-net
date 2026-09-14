// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net

namespace SourceAFIS.Engine.Transparency
{
    class ConsistentMinutiaPair
    {
        public readonly int Probe;
        public readonly int Candidate;

        public ConsistentMinutiaPair(int probe, int candidate)
        {
            Probe = probe;
            Candidate = candidate;
        }
    }
}
