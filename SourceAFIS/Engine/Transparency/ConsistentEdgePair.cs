// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using SourceAFIS.Engine.Matcher;

namespace SourceAFIS.Engine.Transparency
{
    class ConsistentEdgePair
    {
        public readonly int ProbeFrom;
        public readonly int ProbeTo;
        public readonly int CandidateFrom;
        public readonly int CandidateTo;

        public ConsistentEdgePair(int probeFrom, int probeTo, int candidateFrom, int candidateTo)
        {
            ProbeFrom = probeFrom;
            ProbeTo = probeTo;
            CandidateFrom = candidateFrom;
            CandidateTo = candidateTo;
        }

        public ConsistentEdgePair(MinutiaPair pair) : this(pair.ProbeRef, pair.Probe, pair.CandidateRef, pair.Candidate) { }
    }
}
