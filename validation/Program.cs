using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SyslogDecode.Model;
using SyslogDecode.Parsing;
using SyslogDecode.Udp;

class Program
{
    static ParsedSyslogMessage Parse(string text) => SyslogMessageParser.CreateDefault().Parse(
        new RawSyslogMessage { Message = text, ReceivedOn = new DateTime(2026, 1, 2) });
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
    static void Main(string[] args)
    {
        if (args.Contains("--benchmark")) { Benchmark(); return; }
        foreach (var pri in new[] { "<0>", "<34>", "<191>" })
            Check(new ParserContext(pri + "x").ReadSyslogPrefix(), "PRI valid " + pri);
        foreach (var pri in new[] { "<>", "<192>", "<999>", "<01>", "<1234>", "< 1>", "<1 >", "<١>", "x" })
            Check(!new ParserContext(pri).ReadSyslogPrefix(), "PRI invalid " + pri);
        const string h = "<34>1 - host app - id ";
        var m = Parse(h + "-   leading");
        Check(m.Header.Timestamp == null && m.ErrorMessages.Count == 0, "NIL timestamp");
        Check(m.Message == "  leading", "MSG spaces preserved");
        Check(Parse(h + "-").Message == null, "absent MSG");
        Check(Parse(h + "- ").Message == "", "empty MSG");
        Check(Parse(h + "- \uFEFFtext").Message == "text", "BOM compatibility");
        m = Parse(h + "[a@1 p=\"a\\qb\\]c\\\\d\\\"e\"]");
        Check(m.StructuredData5424["a@1"][0].Value == "a\\qb]c\\d\"e", "SD escaping");
        m = Parse(h + "[a@1 p=\"1\" p=\"2\"][b@1]");
        Check(m.StructuredData5424.Count == 2 && m.StructuredData5424["a@1"].Count == 2, "multiple SD / repeated parameter");
        Check(((string[])m.Data["p"]).SequenceEqual(new[] { "1", "2" }), "merged repeated values");
        foreach (var sd in new[] { "[a@1 p=\"unterminated", "[a@1 p=\"x]y\"]", "[a@1][a@1]", "[" + new string('a',33) + "]", "" })
            Check(Parse(h + sd).ErrorMessages.Count > 0, "SD malformed " + sd);
        m = Parse(h + "- text");
        Check(SyslogSerializer.Serialize(m) == h + "- \uFEFFtext", "NIL SD serializer delimiter");
        Check(SyslogSerializer.Serialize(Parse(h + "-")) == h + "-", "NIL SD no MSG delimiter");
        m = Parse("<34>Dec 31 23:59:59 h app: text");
        Check(m.Rfc3164.LocalTimestamp.Value.Year == 2025 && m.Header.Timestamp == null && m.Header.HostName == "h", "BSD short host / year rollover");
        UdpTest();
    }
    static void UdpTest()
    {
        using (var listener = new SyslogUdpListener(IPAddress.Loopback, 0, 1024 * 1024))
        using (var sender = new UdpClient())
        using (var arrived = new AutoResetEvent(false))
        {
            string actual = null;
            listener.Received += (s,e) => { actual = e.Item.Message; arrived.Set(); };
            var endpoint = (IPEndPoint)listener.PortListener.Client.LocalEndPoint;
            listener.Start();
            try
            {
                foreach (var expected in new[] { new string('a', 8192), "<34>1 - h a - - - ä", "x" })
                {
                    var bytes = Encoding.UTF8.GetBytes(expected);
                    sender.Send(bytes, bytes.Length, endpoint);
                    Check(arrived.WaitOne(5000) && actual == expected, "UDP buffer length " + bytes.Length);
                }
            }
            finally { listener.Stop(); }
        }
    }
    static void Benchmark()
    {
        string[] inputs = {
            "<34>Oct 20 12:34:56 host app: event 192.0.2.1",
            "<34>1 2003-10-11T22:14:15.003Z host app - id - text",
            "<34>1 2003-10-11T22:14:15.003Z host app - id [a@1 p=\"x\" q=\"y\"] text",
            "<34>1 2003-10-11T22:14:15.003Z host app - id [a@1 p=\"a\\\\b\\\"c\\]d\"] text" };
        var parser = SyslogMessageParser.CreateDefault();
        Console.WriteLine(Environment.Version + " / " + Environment.OSVersion + " / CPUs " + Environment.ProcessorCount);
        foreach (var input in inputs)
        {
            var raw = new RawSyslogMessage { Message = input, ReceivedOn = DateTime.UtcNow };
            for (int i=0; i<20000; i++) parser.Parse(raw);
            for (int round=0; round<5; round++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                int checksum = 0;
                const int count = 200000;
                for (int i=0; i<count; i++) checksum += (int)parser.Parse(raw).Severity;
                watch.Stop();
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Console.WriteLine($"{Array.IndexOf(inputs,input)} round={round} msg/s={count/watch.Elapsed.TotalSeconds:F0} bytes/msg={(double)bytes/count:F1} checksum={checksum}");
            }
        }
    }
}
