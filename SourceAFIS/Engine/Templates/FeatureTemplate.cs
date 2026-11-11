// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System.Collections.Generic;
using SourceAFIS.Engine.Features;
using SourceAFIS.Engine.Primitives;

namespace SourceAFIS.Engine.Templates
{
    class FeatureTemplate
    {
        public readonly ShortPoint Size;
        public readonly List<Minutia> Minutiae;

        public FeatureTemplate(ShortPoint size, List<Minutia> minutiae)
        {
            Size = size;
            Minutiae = minutiae;
        }
    }
}
