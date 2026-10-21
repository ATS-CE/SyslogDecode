namespace SyslogDecode.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using SyslogDecode.Model;
    using SyslogDecode.Parsing;
    using SyslogDecode.Udp;

    [TestClass]
    public class RawDataAndStatusTests
    {
        private static ParsedSyslogMessage Parse(string message) => SyslogMessageParser.CreateDefault()
            .Parse(new RawSyslogMessage { Message = message, ReceivedOn = DateTime.UtcNow });

        [TestMethod]
        public void DatagramOwnsExactBytesAndMetadata()
        {
            byte[] bytes = { 0xef, 0xbb, 0xbf, 0xff, 0, 32, 32, 13, 10 };
            var expected = bytes.ToArray();
            var received = DateTime.UtcNow;
            var raw = RawSyslogMessage.FromDatagram(bytes, received, IPAddress.Loopback, 12345);
            Array.Fill(bytes, (byte)42);
            CollectionAssert.AreEqual(expected, raw.RawData.ToArray());
            Assert.IsTrue(raw.HasRawData);
            Assert.AreEqual(received, raw.ReceivedOn);
            Assert.AreEqual(IPAddress.Loopback, raw.SourceIpAddress);
            Assert.AreEqual(12345, raw.SourcePort);
            var result = SyslogMessageParser.CreateDefault().Parse(raw);
            Assert.AreSame(raw, result.Source);
            CollectionAssert.AreEqual(expected, result.Source.RawData.ToArray());
        }

        [TestMethod]
        public void TextInputDoesNotPretendToHaveOriginalBytes()
        {
            var result = Parse("<34>1 - h a - id - body");
            Assert.IsFalse(result.Source.HasRawData);
            Assert.AreEqual(PayloadType.Rfc5424, result.PayloadType);
            Assert.AreEqual(ParseStatus.Parsed, result.ParseStatus);
            Assert.AreEqual(ValidationStatus.NotValidated, result.ValidationStatus);
            Assert.IsTrue(result.HasPriority);
            Assert.AreEqual(0, result.Diagnostics.Count);
        }

        [TestMethod]
        public void MissingInvalidAndEmptyInputsRemainAccessible()
        {
            foreach (var text in new[] { "free text  ", "<999>1 - h a - - - body", "<01>message" })
            {
                var result = Parse(text);
                Assert.AreEqual(text, result.Message);
                Assert.AreEqual(text, result.Source.Message);
                Assert.AreEqual(ParseStatus.Unrecognized, result.ParseStatus);
                Assert.IsFalse(result.HasPriority);
                Assert.AreEqual("InvalidPriority", result.Diagnostics[0].Code);
                Assert.AreEqual(ValidationStatus.ErrorsDetected, result.ValidationStatus);
            }
            foreach (var text in new string[] { null, "" })
            {
                var result = Parse(text);
                Assert.AreEqual(ParseStatus.Failed, result.ParseStatus);
                Assert.AreEqual("EmptyInput", result.Diagnostics[0].Code);
                Assert.AreEqual(text, result.Source.Message);
            }
            var empty = RawSyslogMessage.FromDatagram(ReadOnlySpan<byte>.Empty, DateTime.UtcNow);
            Assert.IsTrue(empty.HasRawData);
            Assert.AreEqual(0, empty.RawData.Length);
            Assert.AreEqual(ParseStatus.Failed, SyslogMessageParser.CreateDefault().Parse(empty).ParseStatus);
        }

        [TestMethod]
        public void MalformedVersionOneDoesNotFallBackToPlainText()
        {
            const string text = "<34>1 - h a - id [a@1 p=\"unterminated";
            var result = Parse(text);
            Assert.AreEqual(PayloadType.Rfc5424, result.PayloadType);
            Assert.AreEqual(ParseStatus.Failed, result.ParseStatus);
            Assert.AreEqual(ValidationStatus.ErrorsDetected, result.ValidationStatus);
            Assert.IsTrue(result.Diagnostics.Count > 0);
            Assert.AreEqual("h", result.Header.HostName);
            Assert.AreEqual(text, result.Source.Message);
            Assert.IsTrue(result.Diagnostics[0].Position.HasValue);
        }

        [TestMethod]
        public void RecognizedFailureIsNotReclassified()
        {
            var parser = new SyslogMessageParser();
            parser.AddVariantParsers(new RecognizingFailure(), new PlainTextParser());
            var result = parser.Parse(new RawSyslogMessage { Message = "<34>vendor" });
            Assert.AreEqual(PayloadType.KeyValuePairs, result.PayloadType);
            Assert.AreEqual(ParseStatus.Failed, result.ParseStatus);
            Assert.AreEqual("IncompleteMessage", result.Diagnostics[0].Code);
        }

        [TestMethod]
        public void NonMatchingParserCannotLeakTentativeFields()
        {
            var parser = new SyslogMessageParser();
            parser.AddVariantParsers(new SpeculativeParser(), new PlainTextParser());
            var result = parser.Parse(new RawSyslogMessage { Message = "<34>plain" });
            Assert.IsNull(result.Header.HostName);
            Assert.AreEqual(0, result.StructuredData5424.Count);
            Assert.AreEqual(0, result.ExtractedTuples.Count);
            Assert.AreEqual(ParseStatus.Unrecognized, result.ParseStatus);
            Assert.AreEqual(ValidationStatus.NotValidated, result.ValidationStatus);
        }

        [TestMethod]
        public void LegacyErrorListGetsStructuredDiagnostic()
        {
            var parser = new SyslogMessageParser();
            parser.AddVariantParsers(new LegacyErrorParser());
            var result = parser.Parse(new RawSyslogMessage { Message = "<34>legacy" });
            Assert.AreEqual(ParseStatus.ParsedWithErrors, result.ParseStatus);
            Assert.AreEqual("LegacyParserError", result.Diagnostics[0].Code);
            Assert.AreEqual(ValidationStatus.ErrorsDetected, result.ValidationStatus);
        }

        [TestMethod]
        public void ParserExceptionStillProducesStreamOutputAndNextMessage()
        {
            var parser = new SyslogMessageParser();
            parser.AddVariantParsers(new ThrowOnceParser(), new PlainTextParser());
            var stream = new DirectStream(parser);
            var output = new List<ParsedSyslogMessage>();
            stream.ItemProcessed += (sender, args) => output.Add(args.Item);
            stream.Feed(new RawSyslogMessage { Message = "<34>first" });
            stream.Feed(new RawSyslogMessage { Message = "<34>second" });
            Assert.AreEqual(2, output.Count);
            Assert.AreEqual(ParseStatus.Failed, output[0].ParseStatus);
            Assert.AreEqual("ParserException", output[0].Diagnostics[0].Code);
            Assert.AreEqual("<34>first", output[0].Source.Message);
            Assert.AreEqual("second", output[1].Message);
        }

        [TestMethod]
        public void ExtractionExceptionPreservesParsedFieldsAndSource()
        {
            var parser = new SyslogMessageParser();
            parser.AddVariantParsers(new PlainTextParser());
            parser.AddValueExtractors(new ThrowingExtractor());
            var result = parser.Parse(new RawSyslogMessage { Message = "<34>long enough message" });
            Assert.AreEqual(ParseStatus.Failed, result.ParseStatus);
            Assert.AreEqual("ExtractionException", result.Diagnostics[0].Code);
            Assert.AreEqual("long enough message", result.Message);
            Assert.AreEqual("<34>long enough message", result.Source.Message);
        }

        [TestMethod]
        public void UdpRetainsEarlierPacketsIncludingBinaryAndEmptyPayloads()
        {
            using var listener = new SyslogUdpListener(IPAddress.Loopback, 0, 1024 * 1024);
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var arrived = new AutoResetEvent(false);
            var output = new List<RawSyslogMessage>();
            listener.Received += (s,e) => { output.Add(e.Item); arrived.Set(); };
            var target = (IPEndPoint)listener.PortListener.Client.LocalEndPoint;
            var source = (IPEndPoint)sender.Client.LocalEndPoint;
            byte[][] packets = { Enumerable.Repeat((byte)65,8192).ToArray(),
                new byte[] { 0xff, 0, 13, 10, 32 }, Array.Empty<byte>(), Encoding.UTF8.GetBytes("<34>1 - h a - - - ok") };
            var before = DateTime.UtcNow;
            listener.Start();
            try
            {
                foreach (var bytes in packets)
                {
                    sender.Send(bytes, bytes.Length, target);
                    Assert.IsTrue(arrived.WaitOne(5000), "UDP receive timeout");
                }
                Assert.AreEqual(packets.Length, output.Count);
                for (int i=0; i<packets.Length; i++)
                {
                    CollectionAssert.AreEqual(packets[i], output[i].RawData.ToArray());
                    Assert.IsTrue(output[i].HasRawData);
                    Assert.AreEqual(source.Port, output[i].SourcePort);
                    Assert.AreEqual(IPAddress.Loopback, output[i].SourceIpAddress);
                    Assert.IsTrue(output[i].ReceivedOn >= before && output[i].ReceivedOn <= DateTime.UtcNow);
                }
            }
            finally { listener.Stop(); }
        }

        private sealed class RecognizingFailure : ISyslogVariantParser
        {
            public bool TryParse(ParserContext ctx) { ctx.ParsedMessage.PayloadType = PayloadType.KeyValuePairs; return false; }
        }
        private sealed class SpeculativeParser : ISyslogVariantParser
        {
            public bool TryParse(ParserContext ctx)
            {
                ctx.ParsedMessage.Header.HostName = "must not leak";
                ctx.ParsedMessage.StructuredData5424["tentative"] = new List<NameValuePair>();
                ctx.ParsedMessage.ExtractedTuples.Add(new NameValuePair { Name="x", Value="y" });
                return false;
            }
        }
        private sealed class LegacyErrorParser : ISyslogVariantParser
        {
            public bool TryParse(ParserContext ctx) { ctx.ErrorMessages.Add("legacy error"); return true; }
        }
        private sealed class ThrowOnceParser : ISyslogVariantParser
        {
            private bool thrown;
            public bool TryParse(ParserContext ctx) { if (!thrown) { thrown=true; throw new FormatException("bad message"); } return false; }
        }
        private sealed class ThrowingExtractor : IValuesExtractor
        {
            public IList<NameValuePair> ExtractValues(ParserContext ctx) => throw new FormatException("bad extraction");
        }
        private sealed class DirectStream : SyslogStreamParser
        {
            public DirectStream(SyslogMessageParser parser) : base(parser) { }
            public void Feed(RawSyslogMessage raw) => ProcessItem(raw);
        }
    }
}
