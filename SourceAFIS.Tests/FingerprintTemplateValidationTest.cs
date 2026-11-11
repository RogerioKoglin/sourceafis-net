// Part of SourceAFIS for .NET: https://sourceafis.machinezoo.com/net
using System;
using NUnit.Framework;
using SourceAFIS.Engine.Primitives;
using SourceAFIS.Engine.Templates;

namespace SourceAFIS
{
    public class FingerprintTemplateValidationTest
    {
        static PersistentTemplate Valid() => SerializationUtils.Deserialize<PersistentTemplate>(
            FingerprintTemplateTest.ProbeGray().ToByteArray());

        static void Reject(PersistentTemplate persistent) =>
            Assert.That(() => new FingerprintTemplate(SerializationUtils.Serialize(persistent)), Throws.Exception);

        [Test]
        public void RejectsMalformedCbor() =>
            Assert.That(() => new FingerprintTemplate(new byte[] { 0xff, 0x00, 0x01 }), Throws.Exception);

        [Test]
        public void RejectsNullMinutiaArray()
        {
            var persistent = Valid();
            persistent.PositionsX = null;
            Reject(persistent);
        }

        [Test]
        public void RejectsInconsistentMinutiaArrayLengths()
        {
            var persistent = Valid();
            persistent.PositionsY = new short[persistent.PositionsY.Length - 1];
            Reject(persistent);
        }

        [Test]
        public void RejectsOutOfRangePosition()
        {
            var persistent = Valid();
            persistent.PositionsX[0] = 10_001;
            Reject(persistent);
        }

        [Test]
        public void RejectsDenormalizedDirection()
        {
            var persistent = Valid();
            persistent.Directions[0] = -1;
            Reject(persistent);
        }

        [Test]
        public void RejectsUnknownMinutiaType()
        {
            var persistent = Valid();
            char[] types = persistent.Types.ToCharArray();
            types[0] = 'X';
            persistent.Types = new string(types);
            Reject(persistent);
        }
    }
}
