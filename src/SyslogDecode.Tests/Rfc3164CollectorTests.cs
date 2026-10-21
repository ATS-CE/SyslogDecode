namespace SyslogDecode.Tests
{
    using System;
    using System.Linq;
    using System.Text;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using SyslogDecode.Model;
    using SyslogDecode.Parsing;

    [TestClass]
    public class Rfc3164CollectorTests
    {
        private static readonly DateTime Received = new DateTime(2026,1,1,0,0,1,DateTimeKind.Utc);
        private static ParsedSyslogMessage Parse(string text, TimeZoneInfo zone = null, DateTime? received = null, bool raw = false)
        {
            var parser = SyslogMessageParser.CreateDefault();
            parser.VariantParsers.OfType<Rfc3164SyslogParser>().Single().SenderTimeZoneResolver = _ => zone;
            return parser.Parse(raw ? RawSyslogMessage.FromDatagram(Encoding.UTF8.GetBytes(text), received ?? Received)
                : new RawSyslogMessage { Message = text, ReceivedOn = received ?? Received });
        }
        [TestMethod]
        public void UnknownZoneDoesNotInventUtc()
        {
            var r = Parse("<34>Dec 31 23:59:59 h app: body");
            Assert.AreEqual(ParseStatus.Parsed,r.ParseStatus);
            Assert.IsNull(r.Header.Timestamp);
            Assert.AreEqual(new DateTime(2025,12,31,23,59,59),r.Rfc3164.LocalTimestamp);
            Assert.AreEqual(DateTimeKind.Unspecified,r.Rfc3164.LocalTimestamp.Value.Kind);
            Assert.AreEqual(Rfc3164TimeStatus.ZoneUnknown,r.Rfc3164.TimeStatus);
            Assert.IsTrue(r.Rfc3164.YearInferred);
        }
        [TestMethod]
        public void ConfiguredSenderZoneUsesLocalReceiveYear()
        {
            var zone = TimeZoneInfo.CreateCustomTimeZone("Test+2",TimeSpan.FromHours(2),"Test+2","Test+2");
            var r = Parse("<34>Jan  1 01:30:00 h app: body",zone,new DateTime(2025,12,31,23,31,0,DateTimeKind.Utc));
            Assert.AreEqual(new DateTime(2025,12,31,23,30,0,DateTimeKind.Utc),r.Header.Timestamp);
            Assert.AreEqual(2026,r.Rfc3164.LocalTimestamp.Value.Year);
            Assert.AreEqual(Rfc3164TimeStatus.EstimatedUtc,r.Rfc3164.TimeStatus);
            Assert.AreEqual(zone.Id,r.Rfc3164.TimeZoneId);
        }
        [TestMethod]
        public void NearestYearCanBeNextYear()
        {
            var r = Parse("<34>Jan  1 00:00:01 h app: body",TimeZoneInfo.Utc,new DateTime(2025,12,31,23,59,59,DateTimeKind.Utc));
            Assert.AreEqual(2026,r.Header.Timestamp.Value.Year);
        }
        [TestMethod]
        public void DstGapAndOverlapAreNotGuessed()
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
            var gap = Parse("<34>Mar 29 02:30:00 h app: body",zone,new DateTime(2026,3,29,2,0,0,DateTimeKind.Utc));
            var overlap = Parse("<34>Oct 25 02:30:00 h app: body",zone,new DateTime(2026,10,25,2,0,0,DateTimeKind.Utc));
            Assert.IsNull(gap.Header.Timestamp); Assert.IsNull(overlap.Header.Timestamp);
            Assert.AreEqual(Rfc3164TimeStatus.InvalidLocalTime,gap.Rfc3164.TimeStatus);
            Assert.AreEqual(Rfc3164TimeStatus.AmbiguousLocalTime,overlap.Rfc3164.TimeStatus);
            Assert.AreEqual(ParseStatus.ParsedWithErrors,gap.ParseStatus);
            Assert.AreEqual(ParseStatus.ParsedWithErrors,overlap.ParseStatus);
        }
        [TestMethod]
        public void TagContentPreservesEveryDelimiter()
        {
            foreach (string content in new[] { "[123]:  body  ", ":  body  ", " body", "#subagent", "", "-suffix" })
            {
                var r = Parse("<34>Dec 31 23:59:59 h app"+content);
                Assert.AreEqual("app",r.Rfc3164.Tag);
                Assert.AreEqual("app",r.Header.AppName);
                Assert.AreEqual(content,r.Rfc3164.Content); Assert.AreEqual(content,r.Message);
                Assert.AreEqual(content.StartsWith("[123]") ? "123" : null,r.Header.ProcId);
            }
        }
        [TestMethod]
        public void TagBoundariesAndEmptyContent()
        {
            var r = Parse("<34>Dec 31 23:59:59 h "+new string('a',32)+": text");
            Assert.AreEqual(ParseStatus.Parsed,r.ParseStatus);
            r = Parse("<34>Dec 31 23:59:59 h "+new string('a',33)+": text");
            Assert.IsTrue(r.Diagnostics.Any(d=>d.Code=="TagTooLong"));
            Assert.AreEqual(33,r.Rfc3164.Tag.Length);
            r = Parse("<34>Dec 31 23:59:59 h ");
            Assert.AreEqual("",r.Rfc3164.Content);
            r = Parse("<34>Dec 31 23:59:59 h  body");
            Assert.AreEqual("",r.Rfc3164.Tag); Assert.AreEqual(" body",r.Rfc3164.Content);
        }
        [TestMethod]
        public void DatagramSizeUsesOctetsAndNeverTruncates()
        {
            const string prefix = "<34>Dec 31 23:59:59 h app: ";
            foreach(int length in new[] {1024,1025})
            {
                string text = prefix+new string('x',length-prefix.Length);
                var r = Parse(text,raw:true);
                Assert.AreEqual(length,r.Source.RawData.Length);
                Assert.AreEqual(length==1025,r.Diagnostics.Any(d=>d.Code=="MessageTooLong"));
                Assert.AreEqual(text.Substring(text.IndexOf("app",StringComparison.Ordinal)),Encoding.UTF8.GetString(r.MessageBytes.Span));
            }
            var unicode = Parse(prefix+new string('ä',510),raw:true);
            Assert.IsTrue(unicode.Diagnostics.Any(d=>d.Code=="MessageTooLong"));
            var textOnly = Parse(prefix+new string('x',2000));
            Assert.IsFalse(textOnly.Diagnostics.Any(d=>d.Code=="MessageTooLong"));
        }
        [TestMethod]
        public void MalformedTimeIsDiagnosedWithoutFallback()
        {
            foreach(string time in new[] { "Feb 30 12:00:00", "Jan 01 00:00:00", "Jan  0 00:00:00", "Jan  1 24:00:00", "Jan  1 00:00:60", "Jan  1" })
            {
                var r = Parse("<34>"+time+" h app: body");
                Assert.AreEqual(PayloadType.Rfc3164,r.PayloadType);
                Assert.AreEqual(ParseStatus.Failed,r.ParseStatus);
                Assert.AreEqual("InvalidTimestamp",r.Diagnostics[0].Code);
            }
        }
        [TestMethod]
        public void HostAndControlsAreDiagnosedButDataRetained()
        {
            foreach(string host in new[] {"h", "192.0.2.1", "2001:db8::1"})
                Assert.AreEqual(ParseStatus.Parsed,Parse("<34>Dec 31 23:59:59 "+host+" app: body").ParseStatus);
            Assert.IsTrue(Parse("<34>Dec 31 23:59:59 host.example app: body").Diagnostics.Any(d=>d.Code=="InvalidHostname"));
            var r = Parse("<34>Dec 31 23:59:59 h app: a\0b");
            Assert.AreEqual(": a\0b",r.Message);
            Assert.IsTrue(r.Diagnostics.Any(d=>d.Code=="ControlInMessage"));
        }
        [TestMethod]
        public void MissingReceiveTimeAndFreeMessagesRemainAvailable()
        {
            var r = Parse("<34>Dec 31 23:59:59 h app: body",received:default(DateTime));
            Assert.IsNull(r.Header.Timestamp); Assert.IsNull(r.Rfc3164.LocalTimestamp);
            Assert.IsTrue(r.Diagnostics.Any(d=>d.Code=="MissingReceiveTime"));
            r = Parse("free text without PRI",raw:true);
            Assert.AreEqual(ParseStatus.Unrecognized,r.ParseStatus);
            Assert.AreEqual("free text without PRI",r.Message); Assert.IsTrue(r.Source.HasRawData);
        }
        [TestMethod]
        public void ReceiveYearBoundsAndLeapDayDoNotThrow()
        {
            foreach(var received in new[] {new DateTime(1,1,2),new DateTime(9999,12,30),new DateTime(2024,2,29)})
            {
                var r = Parse("<34>Feb 29 12:00:00 h app: body",TimeZoneInfo.Utc,received);
                Assert.IsFalse(r.Diagnostics.Any(d=>d.Code=="ParserException"));
            }
            var leap = Parse("<34>Feb 29 12:00:00 h app: body",TimeZoneInfo.Utc,new DateTime(2024,2,29));
            Assert.AreEqual(2024,leap.Header.Timestamp.Value.Year);
        }
    }
}
