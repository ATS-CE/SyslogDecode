namespace SyslogDecode.Tests
{
    using System;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using SyslogDecode.Model;
    using SyslogDecode.Parsing;

    [TestClass]
    public class Rfc5424GrammarTests
    {
        private static ParsedSyslogMessage Parse(string text) => SyslogMessageParser.CreateDefault()
            .Parse(new RawSyslogMessage { Message = text });
        private static ParsedSyslogMessage Sd(string text) => Parse("<34>1 - h a p m " + text);
        private static ParsedSyslogMessage Time(string text) => Parse("<34>1 " + text + " h a p m - body");
        private static void Good(ParsedSyslogMessage result)
        {
            Assert.AreEqual(ParseStatus.Parsed, result.ParseStatus, string.Join(";", result.ErrorMessages));
            Assert.AreEqual(PayloadType.Rfc5424, result.PayloadType);
            Assert.AreEqual(0, result.Diagnostics.Count);
        }
        private static void Bad(ParsedSyslogMessage result, string field)
        {
            Assert.IsTrue(result.ParseStatus == ParseStatus.Failed || result.ParseStatus == ParseStatus.ParsedWithErrors);
            Assert.AreEqual(PayloadType.Rfc5424, result.PayloadType);
            Assert.IsTrue(result.Diagnostics.Any(d => d.Field == field), result.Source.Message);
            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "ParserException"));
        }
        [TestMethod]
        public void NilHeaderAndOptionalMessage()
        {
            var r = Parse("<34>1 - - - - - -"); Good(r);
            Assert.AreEqual(1, r.Header.Version); Assert.AreEqual("-", r.Header.TimestampText);
            Assert.IsNull(r.Header.Timestamp); Assert.IsNull(r.Header.HostName);
            Assert.IsNull(r.Header.AppName); Assert.IsNull(r.Header.ProcId); Assert.IsNull(r.Header.MsgId);
            Assert.IsNull(r.Message);
            Good(Sd("- ")); Assert.AreEqual("", Sd("- ").Message);
            Assert.AreEqual("  text\r\n", Sd("-   text\r\n").Message);
        }
        [TestMethod]
        public void HeaderLengthBoundaries()
        {
            var names = new[] { "HOSTNAME", "APP-NAME", "PROCID", "MSGID" };
            var limits = new[] { 255, 48, 128, 32 };
            for (int i = 0; i < 4; i++)
            {
                var fields = new[] { "h", "a", "p", "m" };
                fields[i] = new string('x', limits[i]);
                Good(Parse("<34>1 - " + string.Join(" ", fields) + " -"));
                fields[i] += "x";
                Bad(Parse("<34>1 - " + string.Join(" ", fields) + " -"), names[i]);
            }
        }
        [TestMethod]
        public void HeaderRequiresAsciiAndExactSpaces()
        {
            foreach (string host in new[] { "", "h\t", "ä", "h\0", "h\u007f" })
                Bad(Parse("<34>1 - " + host + " a p m -"), "HOSTNAME");
            Good(Parse("<34>1 - !~ !~ !~ !~ -"));
            Bad(Parse("<34>1\t- h a p m -"), "VERSION");
        }
        [TestMethod]
        public void NumericVersionsNeverFallBack()
        {
            foreach (string version in new[] { "0", "01", "1000", "2", "999" })
                Bad(Parse("<34>" + version + " - h a p m -"), "VERSION");
            Assert.AreEqual("UnsupportedVersion", Parse("<34>2 - h a p m -").Diagnostics[0].Code);
        }
        [TestMethod]
        public void TruncationNeverThrowsOrReclassifies()
        {
            const string text = "<34>1 - h a p m [example@1 p=\"value\"]";
            for (int n = 5; n < text.Length; n++)
            {
                var result = Parse(text.Substring(0, n));
                Assert.AreEqual(ParseStatus.Failed, result.ParseStatus, n.ToString());
                Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "ParserException"), n.ToString());
            }
        }
        [TestMethod]
        public void TimestampFractionsAndOffsets()
        {
            for (int n = 1; n <= 6; n++) Good(Time("2024-02-29T23:59:59." + new string('1', n) + "Z"));
            var r = Time("2024-01-02T03:04:05.123456+02:30"); Good(r);
            Assert.AreEqual(new DateTime(2024,1,2,0,34,5,DateTimeKind.Utc).AddTicks(1234560), r.Header.Timestamp);
            Good(Time("2024-01-02T03:04:05+23:59"));
            Good(Time("2024-01-02T03:04:05-23:59"));
            r = Time("2024-01-02T03:04:05-00:00"); Good(r);
            Assert.AreEqual("2024-01-02T03:04:05-00:00", r.Header.TimestampText);
        }
        [TestMethod]
        public void InvalidTimestampsRetainHeaderAndBody()
        {
            foreach (var time in new[] { "2023-02-29T00:00:00Z", "2024-13-01T00:00:00Z",
                "2024-01-00T00:00:00Z", "2024-01-01t00:00:00Z", "2024-01-01T00:00:00z",
                "2024-01-01T24:00:00Z", "2024-01-01T00:60:00Z", "2024-01-01T00:00:60Z",
                "2024-01-01T00:00:00.Z", "2024-01-01T00:00:00.1234567Z", "2024-01-01T00:00:00",
                "2024-01-01T00:00:00+24:00", "2024-01-01T00:00:00+00:60" })
            {
                var r = Time(time); Bad(r, "TIMESTAMP");
                Assert.AreEqual(ParseStatus.ParsedWithErrors, r.ParseStatus);
                Assert.AreEqual(time, r.Header.TimestampText); Assert.IsNull(r.Header.Timestamp);
                Assert.AreEqual("h", r.Header.HostName); Assert.AreEqual("body", r.Message);
            }
        }
        [TestMethod]
        public void TimestampRepresentationLimitsAreExplicit()
        {
            foreach (var time in new[] { "0000-01-01T00:00:00Z", "0001-01-01T00:00:00+00:01", "9999-12-31T23:59:59-00:01" })
            {
                var r = Time(time); Bad(r, "TIMESTAMP");
                Assert.AreEqual("TimestampOutOfRange", r.Diagnostics[0].Code);
                Assert.AreEqual(time, r.Header.TimestampText);
            }
            Good(Time("0001-01-01T00:00:00Z")); Good(Time("9999-12-31T23:59:59.999999Z"));
        }
        [TestMethod]
        public void StructuredDataNameBoundaries()
        {
            Good(Sd("[" + new string('x',32) + " " + new string('p',32) + "=\"\"]"));
            Bad(Sd("[" + new string('x',33) + "]"), "SD-ID");
            Bad(Sd("[x " + new string('p',33) + "=\"\"]"), "PARAM-NAME");
            foreach (var id in new[] { "", "x\t", "ä", "x\"" }) Bad(Sd("[" + id + "]"), "SD-ID");
            Good(Sd("[[x]")); // '[' is permitted by SD-NAME's ABNF.
        }
        [TestMethod]
        public void EnterpriseSuffixSyntax()
        {
            Good(Sd("[example@32473]")); Good(Sd("[example@32473.1.2]"));
            foreach (var id in new[] { "@1", "x@", "x@a", "x@@1", "x@1.", "x@.1", "x@1..2" })
                Bad(Sd("[" + id + "]"), "SD-ID");
        }
        [TestMethod]
        public void ParameterEscapingAndControlCharacters()
        {
            var r = Sd("[x p=\"a\\\"b\\\\c\\]d\\qe\" control=\"\t\r\n\0\"]"); Good(r);
            Assert.AreEqual("a\"b\\c]d\\qe", r.StructuredData5424["x"][0].Value);
            Assert.AreEqual("\t\r\n\0", r.StructuredData5424["x"][1].Value);
            Bad(Sd("[x p=\"a]b\"]"), "PARAM-VALUE");
            Bad(Sd("[x p=\"unfinished"), "PARAM-VALUE");
        }
        [TestMethod]
        public void DuplicateElementsFailButRepeatedParametersRemain()
        {
            var r = Sd("[x p=\"first\"][x p=\"second\"]"); Bad(r, "SD-ID");
            Assert.AreEqual(1, r.StructuredData5424.Count);
            Assert.AreEqual("first", r.StructuredData5424["x"][0].Value);
            r = Sd("[x p=\"first\" p=\"second\"][X]"); Good(r);
            Assert.AreEqual(2, r.StructuredData5424["x"].Count);
            CollectionAssert.AreEqual(new[] { "first", "second" }, (string[])r.Data["p"]);
        }
        [TestMethod]
        public void MalformedElementsAreAtomic()
        {
            foreach (var broken in new[] { "[bad p=\"ok\" q=\"unfinished", "[bad p =\"x\"]", "[bad p= \"x\"]", "[bad  p=\"x\"]", "[bad ]", "[bad p=\"x\"q=\"y\"]" })
            {
                var r = Sd("[good p=\"saved\"]" + broken);
                Assert.AreEqual(ParseStatus.Failed, r.ParseStatus);
                Assert.AreEqual(1, r.StructuredData5424.Count);
                Assert.AreEqual("saved", r.StructuredData5424["good"][0].Value);
                Assert.AreEqual("<34>1 - h a p m [good p=\"saved\"]" + broken, r.Source.Message);
            }
        }
        [TestMethod]
        public void StructuredDataAndMessageDelimiter()
        {
            Good(Sd("[a][b]"));
            var r = Sd("[a] [b]"); Good(r);
            Assert.AreEqual(1, r.StructuredData5424.Count); Assert.AreEqual("[b]", r.Message);
            Bad(Sd("[a]text"), "MSG"); Bad(Sd("-text"), "MSG");
            Bad(Sd(""), "STRUCTURED-DATA");
        }
        [TestMethod]
        public void DiagnosticsIdentifyFieldAndTextPosition()
        {
            var r = Parse("<34>1 - h\t a p m -");
            Bad(r, "HOSTNAME"); Assert.AreEqual(9, r.Diagnostics[0].Position);
            Assert.AreEqual("RFC5424", r.Diagnostics[0].Stage);
            Assert.AreEqual(ValidationStatus.ErrorsDetected, r.ValidationStatus);
        }
    }
}
