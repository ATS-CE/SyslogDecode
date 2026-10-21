using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SyslogDecode.Model;
using SyslogDecode.Parsing;

class Program
{
    static void Main()
    {
#if BASELINE
        const string variant = "baseline";
#else
        const string variant = "current";
#endif
        Console.WriteLine("ENV " + JsonSerializer.Serialize(new {variant, runtime=RuntimeInformation.FrameworkDescription,
            os=RuntimeInformation.OSDescription, architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            cpus=Environment.ProcessorCount, serverGc=System.Runtime.GCSettings.IsServerGC}));
        string[] cases = { "3164", "5424-msg", "5424-sd", "5424-escapes" };
        string[] inputs = {
            "<34>Oct 20 12:34:56 host app: event 192.0.2.1",
            "<34>1 2003-10-11T22:14:15.003Z host app - id - \uFEFFtext",
            "<34>1 2003-10-11T22:14:15.003Z host app - id [a@1 p=\"x\" q=\"y\"] \uFEFFtext",
            "<34>1 2003-10-11T22:14:15.003Z host app - id [a@1 p=\"a\\\\b\\\"c\\]d\"] \uFEFFtext"
        };
        var received = new DateTime(2026,10,20,12,35,0,DateTimeKind.Utc);
        var parser = SyslogMessageParser.CreateDefault();
        for (int c=0; c<inputs.Length; c++)
        {
            var text = inputs[c];
            var bytes = Encoding.UTF8.GetBytes(text);
            var raw = new RawSyslogMessage {Message=text,ReceivedOn=received};
            foreach (string mode in new[]{"text-parse","receive-and-parse"})
            {
                ParsedSyslogMessage Run() => parser.Parse(mode == "text-parse" ? raw : Receive(bytes,received));
                var check = Run();
                if (check.ErrorMessages.Count != 0 || check.Header.HostName != "host" || (int)check.Severity != 2 || check.Message == null)
                    throw new Exception("Benchmark input did not parse as expected: " + cases[c]);
                for(int i=0;i<50000;i++) Run();
                for(int round=0;round<5;round++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var watch = new Stopwatch();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    int checksum=0;
                    const int count=200000;
                    watch.Start();
                    for(int i=0;i<count;i++)
                    {
                        var result=Run();
                        checksum += (int)result.Severity + result.Message.Length + result.Data.Count;
                    }
                    watch.Stop();
                    long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
                    Console.WriteLine("BENCH " + JsonSerializer.Serialize(new{variant,scenario=cases[c],mode,round,count,
                        seconds=watch.Elapsed.TotalSeconds,messagesPerSecond=count/watch.Elapsed.TotalSeconds,
                        bytesPerMessage=(double)allocated/count,checksum}));
                }
            }
        }
    }
    static RawSyslogMessage Receive(byte[] bytes,DateTime received)
    {
#if BASELINE
        // Simulate the original UDP receive ownership (new byte[]), then its UTF-8 text conversion.
        var owned=(byte[])bytes.Clone();
        return new RawSyslogMessage {Message=Encoding.UTF8.GetString(owned),ReceivedOn=received};
#else
        return RawSyslogMessage.FromDatagram(bytes,received);
#endif
    }
}
