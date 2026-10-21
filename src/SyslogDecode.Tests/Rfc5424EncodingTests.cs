namespace SyslogDecode.Tests
{
    using System;
    using System.Linq;
    using System.Text;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using SyslogDecode.Model;
    using SyslogDecode.Parsing;

    [TestClass]
    public class Rfc5424EncodingTests
    {
        private const string Header = "<34>1 - h a p m ";
        private static readonly byte[] Bom = { 0xef, 0xbb, 0xbf };
        private static ParsedSyslogMessage Parse(byte[] bytes) => SyslogMessageParser.CreateDefault()
            .Parse(RawSyslogMessage.FromDatagram(bytes, DateTime.UtcNow));
        private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
        private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
        private static readonly byte[][] Invalid = {
            new byte[] {0xff}, new byte[] {0x80}, new byte[] {0xc0,0xaf},
            new byte[] {0xe0,0x80,0xaf}, new byte[] {0xed,0xa0,0x80},
            new byte[] {0xf4,0x90,0x80,0x80}, new byte[] {0xf0,0x80,0x80,0xaf},
            new byte[] {0xc2}, new byte[] {0xe2,0x82}, new byte[] {0xf0,0x9f,0x98}
        };
        [TestMethod]
        public void BomMessageAcceptsUnicodeScalarsAndRetainsBytes()
        {
            const string text = "ä日本😀\0\r\n\uFEFF";
            var payload = Join(Bom, Bytes(text));
            var r = Parse(Join(Bytes(Header + "- "), payload));
            Assert.AreEqual(ParseStatus.Parsed, r.ParseStatus);
            Assert.AreEqual(MessageEncodingStatus.Utf8, r.MessageEncoding);
            Assert.AreEqual(text, r.Message);
            CollectionAssert.AreEqual(payload, r.MessageBytes.ToArray());
        }
        [TestMethod]
        public void InvalidBomMessagesAreNotInterpreted()
        {
            foreach (var invalid in Invalid)
            {
                var bytes = Join(Bytes(Header + "- "), Bom, Bytes("ok"), invalid);
                var r = Parse(bytes);
                Assert.AreEqual(ParseStatus.ParsedWithErrors, r.ParseStatus);
                Assert.AreEqual(MessageEncodingStatus.InvalidUtf8, r.MessageEncoding);
                Assert.IsNull(r.Message);
                Assert.AreEqual(Bytes(Header + "- ").Length + 5, r.Diagnostics[0].BytePosition);
                Assert.IsNull(r.Diagnostics[0].Position);
                CollectionAssert.AreEqual(bytes, r.Source.RawData.ToArray());
            }
        }
        [TestMethod]
        public void ArbitraryOctetsWithoutBomRemainAllowed()
        {
            foreach (var invalid in Invalid)
            {
                var r = Parse(Join(Bytes(Header + "- "), invalid));
                Assert.AreEqual(ParseStatus.Parsed, r.ParseStatus);
                Assert.AreEqual(MessageEncodingStatus.Unspecified, r.MessageEncoding);
                CollectionAssert.AreEqual(invalid, r.MessageBytes.ToArray());
            }
            Assert.AreEqual(MessageEncodingStatus.Unspecified, Parse(Bytes(Header + "- ä")).MessageEncoding);
        }
        [TestMethod]
        public void BomMustBeAtMessageStart()
        {
            var r = Parse(Join(Bytes(Header + "- x"), Bom, new byte[] {0xff}));
            Assert.AreEqual(MessageEncodingStatus.Unspecified, r.MessageEncoding);
            Assert.AreEqual(0, r.Diagnostics.Count);
            r = Parse(Join(Bom, Bytes(Header + "- text")));
            Assert.AreEqual(ParseStatus.Unrecognized, r.ParseStatus);
            Assert.AreEqual("InvalidPriority", r.Diagnostics[0].Code);
        }
        [TestMethod]
        public void EmptyAbsentAndRepeatedBomAreDistinct()
        {
            var r = Parse(Bytes(Header + "-"));
            Assert.AreEqual(MessageEncodingStatus.Absent, r.MessageEncoding); Assert.IsFalse(r.HasMessageBytes);
            r = Parse(Bytes(Header + "- "));
            Assert.AreEqual(MessageEncodingStatus.Unspecified, r.MessageEncoding); Assert.IsTrue(r.HasMessageBytes);
            Assert.AreEqual(0, r.MessageBytes.Length); Assert.AreEqual("", r.Message);
            r = Parse(Join(Bytes(Header + "- "), Bom));
            Assert.AreEqual(MessageEncodingStatus.Utf8, r.MessageEncoding); Assert.AreEqual("", r.Message);
            r = Parse(Join(Bytes(Header + "- "), Bom, Bom));
            Assert.AreEqual("\uFEFF", r.Message);
        }
        [TestMethod]
        public void InvalidParametersDoNotLeakReplacementText()
        {
            foreach (var invalid in Invalid)
            {
                var prefix = Bytes(Header + "[good p=\"ä😀\"][bad p=\"");
                var r = Parse(Join(prefix, invalid, Bytes("\"]")));
                Assert.AreEqual(ParseStatus.Failed, r.ParseStatus);
                Assert.AreEqual(1, r.StructuredData5424.Count);
                Assert.AreEqual("ä😀", r.StructuredData5424["good"][0].Value);
                Assert.AreEqual("PARAM-VALUE", r.Diagnostics[0].Field);
                Assert.AreEqual(prefix.Length, r.Diagnostics[0].BytePosition);
            }
        }
        [TestMethod]
        public void ValidParametersPreserveEscapesControlsAndReplacementScalar()
        {
            const string sd = "[x p=\"ä😀\\\"\\\\\\]\0\uFEFF\uFFFD\" q=\"日本\"] ";
            var r = Parse(Join(Bytes(Header + sd), Bom, Bytes("ok")));
            Assert.AreEqual(ParseStatus.Parsed, r.ParseStatus, string.Join(";", r.ErrorMessages));
            Assert.AreEqual("ä😀\"\\]\0\uFEFF\uFFFD", r.StructuredData5424["x"][0].Value);
            Assert.AreEqual("日本", r.StructuredData5424["x"][1].Value);
            Assert.AreEqual("ok", r.Message);
            CollectionAssert.AreEqual(Join(Bom, Bytes("ok")), r.MessageBytes.ToArray());
        }
        [TestMethod]
        public void StringInputsNeverClaimByteValidation()
        {
            var parser = SyslogMessageParser.CreateDefault();
            var r = parser.Parse(new RawSyslogMessage { Message = Header + "- \uFEFF😀" });
            Assert.AreEqual(MessageEncodingStatus.TextOnly, r.MessageEncoding);
            Assert.IsFalse(r.HasMessageBytes); Assert.AreEqual("😀", r.Message);
            foreach (var text in new[] { Header + "- \ud800", Header + "[x p=\"\udfff\"]" })
            {
                r = parser.Parse(new RawSyslogMessage { Message = text });
                Assert.AreEqual("InvalidUnicode", r.Diagnostics[0].Code);
                Assert.IsNull(r.Diagnostics[0].BytePosition);
            }
        }
        [TestMethod]
        public void DatagramParsingUsesOriginalBytesDespiteMutableDisplayText()
        {
            var raw = RawSyslogMessage.FromDatagram(Join(Bytes(Header + "- "), Bom, Bytes("original")), DateTime.UtcNow);
            raw.Message = "";
            var r = SyslogMessageParser.CreateDefault().Parse(raw);
            Assert.AreEqual(ParseStatus.Parsed, r.ParseStatus);
            Assert.AreEqual("original", r.Message);
        }
    }
}
