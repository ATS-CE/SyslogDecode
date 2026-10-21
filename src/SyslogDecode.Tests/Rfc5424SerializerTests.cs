namespace SyslogDecode.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using SyslogDecode.Model;
    using SyslogDecode.Parsing;
    using SyslogDecode.Udp;

    [TestClass]
    public class Rfc5424SerializerTests
    {
        private static ParsedSyslogMessage Entry(string message = "text") => new ParsedSyslogMessage(
            (Facility)4,(Severity)2,new DateTime(2026,10,21,12,34,56,DateTimeKind.Utc),"h","a","p","m",message);
        private static ParsedSyslogMessage Parse(byte[] bytes) => SyslogMessageParser.CreateDefault().Parse(RawSyslogMessage.FromDatagram(bytes,DateTime.UtcNow));
        private static void Reject(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            Assert.Fail("Expected invalid output to be rejected.");
        }
        [TestMethod]
        public void UnicodeMessageRoundTripsWithBom()
        {
            var entry = Entry("  ä😀\0\r\n\uFEFF");
            var bytes = SyslogSerializer.SerializeBytes(entry);
            var result = Parse(bytes);
            Assert.AreEqual(ParseStatus.Parsed,result.ParseStatus);
            Assert.AreEqual(entry.Message,result.Message);
            Assert.AreEqual(MessageEncodingStatus.Utf8,result.MessageEncoding);
            Assert.AreEqual(0xef,result.MessageBytes.Span[0]);
            Assert.AreEqual(entry.Header.Timestamp,result.Header.Timestamp);
        }
        [TestMethod]
        public void AbsentEmptyAndLeadingBomStayDistinct()
        {
            var absent = Parse(SyslogSerializer.SerializeBytes(Entry(null)));
            var empty = Parse(SyslogSerializer.SerializeBytes(Entry("")));
            var bom = Parse(SyslogSerializer.SerializeBytes(Entry("\uFEFFx")));
            Assert.IsNull(absent.Message); Assert.IsFalse(absent.HasMessageBytes);
            Assert.AreEqual("",empty.Message); Assert.IsTrue(empty.HasMessageBytes);
            Assert.AreEqual("\uFEFFx",bom.Message);
            Assert.AreEqual(3,empty.MessageBytes.Length);
        }
        [TestMethod]
        public void ExplicitRawMessagePreservesArbitraryBytes()
        {
            var payload = new byte[] {0xff,0,0x80,32};
            var entry = Entry("ignored by explicit byte overload");
            var r = Parse(SyslogSerializer.SerializeBytes(entry,payload));
            CollectionAssert.AreEqual(payload,r.MessageBytes.ToArray());
            Assert.AreEqual(MessageEncodingStatus.Unspecified,r.MessageEncoding);
            r = Parse(SyslogSerializer.SerializeBytes(entry,ReadOnlySpan<byte>.Empty));
            Assert.AreEqual(0,r.MessageBytes.Length); Assert.IsTrue(r.HasMessageBytes);
            Reject(()=>SyslogSerializer.SerializeBytes(entry,new byte[]{0xef,0xbb,0xbf,0xc0,0xaf}));
        }
        [TestMethod]
        public void EditedMessageNeverReusesStaleRawBytes()
        {
            var r = Parse(SyslogSerializer.SerializeBytes(Entry("old")));
            r.Message = "new";
            Assert.AreEqual("new",Parse(SyslogSerializer.SerializeBytes(r)).Message);
        }
        [TestMethod]
        public void TimeUsesInvariantUtcAndMicroseconds()
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                var entry = Entry(); entry.Header.Timestamp = entry.Header.Timestamp.Value.AddTicks(1234567);
                var r = Parse(SyslogSerializer.SerializeBytes(entry));
                Assert.AreEqual(entry.Header.Timestamp.Value.Ticks-7,r.Header.Timestamp.Value.Ticks);
                Assert.AreEqual("2026-10-21T12:34:56.123456Z",r.Header.TimestampText);
                entry.Header.Timestamp = DateTime.SpecifyKind(new DateTime(2026,7,15,12,0,0),DateTimeKind.Local);
                Assert.AreEqual(entry.Header.Timestamp.Value.ToUniversalTime(),Parse(SyslogSerializer.SerializeBytes(entry)).Header.Timestamp);
            }
            finally { CultureInfo.CurrentCulture = old; }
        }
        [TestMethod]
        public void UnknownKindAndUnsupportedVersionAreRejected()
        {
            var e = Entry(); e.Header.Timestamp = new DateTime(2026,1,1);
            Reject(()=>SyslogSerializer.Serialize(e));
            e.Header.Timestamp = null; Assert.IsNull(Parse(SyslogSerializer.SerializeBytes(e)).Header.Timestamp);
            e.Header.Version = 2; Reject(()=>SyslogSerializer.Serialize(e));
        }
        [TestMethod]
        public void UnchangedOffsetTokenSurvivesButEditedTimeWins()
        {
            var r = Parse(Encoding.UTF8.GetBytes("<34>1 2026-10-21T12:34:56.123456-00:00 h a p m -"));
            Assert.AreEqual(r.Header.TimestampText,Parse(SyslogSerializer.SerializeBytes(r)).Header.TimestampText);
            r.Header.Timestamp = r.Header.Timestamp.Value.AddSeconds(1);
            Assert.AreEqual("2026-10-21T12:34:57.123456Z",Parse(SyslogSerializer.SerializeBytes(r)).Header.TimestampText);
        }
        [TestMethod]
        public void HeaderAndPriorityValidation()
        {
            foreach(var host in new[]{"", "a b", "ä", "x\n", new string('h',256)})
            { var e=Entry(); e.Header.HostName=host; Reject(()=>SyslogSerializer.Serialize(e)); }
            var good=Entry(); good.Header.HostName=new string('h',255); good.Header.AppName=new string('a',48);
            good.Header.ProcId=new string('p',128); good.Header.MsgId=new string('m',32);
            Assert.AreEqual(ParseStatus.Parsed,Parse(SyslogSerializer.SerializeBytes(good)).ParseStatus);
            good.Header.AppName += "a"; Reject(()=>SyslogSerializer.Serialize(good));
            foreach(int facility in new[]{-1,24}) { var e=Entry(); e.Facility=(Facility)facility; Reject(()=>SyslogSerializer.Serialize(e)); }
            foreach(int severity in new[]{-1,8}) { var e=Entry(); e.Severity=(Severity)severity; Reject(()=>SyslogSerializer.Serialize(e)); }
        }
        [TestMethod]
        public void StructuredDataEscapesAndRepeatedValuesRoundTrip()
        {
            var e=Entry();
            e.StructuredData5424.Add("x@32473",new List<NameValuePair>{
                new NameValuePair{Name="p",Value="ä😀\\\"]\0"}, new NameValuePair{Name="p",Value=""}});
            var r=Parse(SyslogSerializer.SerializeBytes(e));
            Assert.AreEqual(ParseStatus.Parsed,r.ParseStatus);
            Assert.AreEqual(e.StructuredData5424["x@32473"][0].Value,r.StructuredData5424["x@32473"][0].Value);
            Assert.AreEqual(2,r.StructuredData5424["x@32473"].Count);
        }
        [TestMethod]
        public void InvalidStructuredDataAndSurrogatesAreRejected()
        {
            foreach(var name in new[]{"", "x y", "x]", "x=", "x\"", "ä", "x@bad", new string('x',33)})
            { var e=Entry(); e.StructuredData5424.Add(name,new List<NameValuePair>()); Reject(()=>SyslogSerializer.Serialize(e)); }
            var value=Entry(); value.StructuredData5424.Add("x",new List<NameValuePair>{new NameValuePair{Name="p",Value="\ud800"}});
            Reject(()=>SyslogSerializer.Serialize(value));
            value.StructuredData5424["x"][0].Value=null; Reject(()=>SyslogSerializer.Serialize(value));
            Reject(()=>SyslogSerializer.Serialize(Entry("\udfff")));
        }
        [TestMethod]
        public void FailedAndOtherFormatsAreNotSilentlyReencoded()
        {
            Reject(()=>SyslogSerializer.Serialize(null));
            var e=Entry(); e.PayloadType=PayloadType.Rfc3164; Reject(()=>SyslogSerializer.Serialize(e));
            var bad=Parse(Encoding.UTF8.GetBytes("<34>1 - h a p m [bad")); Reject(()=>SyslogSerializer.Serialize(bad));
        }
        [TestMethod]
        public void UdpSenderUsesValidatedByteSerializer()
        {
            using var receiver=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            receiver.Client.ReceiveTimeout=5000;
            using var sender=new SyslogUdpSender((IPEndPoint)receiver.Client.LocalEndPoint);
            var entry=Entry("ä😀"); sender.Send(entry);
            IPEndPoint remote=null;
            var bytes=receiver.Receive(ref remote);
            CollectionAssert.AreEqual(SyslogSerializer.SerializeBytes(entry),bytes);
            Assert.AreEqual(MessageEncodingStatus.Utf8,Parse(bytes).MessageEncoding);
        }
    }
}
