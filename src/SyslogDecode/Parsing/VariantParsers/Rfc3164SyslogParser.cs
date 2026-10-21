namespace SyslogDecode.Parsing
{
    using System;
    using System.Net;
    using SyslogDecode.Model;

    /// <summary>Collector parser: preserve nonconforming input and diagnose it without relay rewriting.</summary>
    public class Rfc3164SyslogParser : ISyslogVariantParser
    {
        /// <summary>Optional sender-specific zone. Null means unknown, never the collector's local zone.</summary>
        public Func<RawSyslogMessage, TimeZoneInfo> SenderTimeZoneResolver { get; set; }
        public bool AllowVendorTimestamps { get; set; } = true;
        private const string Months = "JanFebMarAprMayJunJulAugSepOctNovDec";

        private static bool AlphaNumeric(char c) => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9';
        private static int Digit(char c) => c >= '0' && c <= '9' ? c - '0' : -1;
        private static int Pair(string t, int p) => Digit(t[p]) < 0 || Digit(t[p+1]) < 0 ? -1 : Digit(t[p])*10 + Digit(t[p+1]);
        private static bool Error(ParserContext ctx, string code, string field, int p, string message)
        {
            ctx.ParsedMessage.RecordDiagnostic(new ParseDiagnostic(code, "RFC3164", p, message, field));
            return false;
        }
        public bool TryParse(ParserContext ctx)
        {
            if (!ctx.Reset()) return false;
            int start = ctx.Position, month = 0;
            for (int i = 0; i < 12; i++)
                if (ctx.Text.Length - start >= 3 && string.CompareOrdinal(ctx.Text, start, Months, i*3, 3) == 0)
                    month = i+1;
            var entry = ctx.ParsedMessage;
            if (month == 0)
            {
                if (!AllowVendorTimestamps || !TimestampParseHelper.TryParseTimestamp(ctx)) return false;
                entry.PayloadType = PayloadType.Rfc3164;
                entry.Rfc3164 = new Rfc3164Details { TimestampText = ctx.Text.Substring(start, ctx.Position-start), TimeStatus = Rfc3164TimeStatus.VendorTimestamp };
                // Legacy heuristics do not establish a trustworthy UTC instant.
                entry.Header.Timestamp = null;
                Error(ctx, "VendorTimestamp", "TIMESTAMP", start, "Nonstandard timestamp retained; UTC interpretation is unresolved.");
                ctx.SkipSpaces();
            }
            else
            {
                entry.PayloadType = PayloadType.Rfc3164;
                entry.Rfc3164 = new Rfc3164Details();
                if (!ReadTime(ctx, month)) return false;
            }
            if (entry.Source.HasRawData && entry.Source.RawData.Length > 1024)
                Error(ctx, "MessageTooLong", "MESSAGE", 0, "RFC 3164 message exceeds 1024 octets; all bytes retained.");
            int hostStart = ctx.Position;
            while (!ctx.Eof() && ctx.Current != ' ')
            {
                if (ctx.Current < 33 || ctx.Current > 126)
                    return Error(ctx, "InvalidHostname", "HOSTNAME", ctx.Position, "HOSTNAME requires printable ASCII without spaces.");
                ctx.Position++;
            }
            if (ctx.Position == hostStart)
                return Error(ctx, "MissingHostname", "HOSTNAME", hostStart, "HOSTNAME is empty.");
            entry.Header.HostName = ctx.Text.Substring(hostStart, ctx.Position-hostStart);
            if (!ValidHost(entry.Header.HostName))
                Error(ctx, "InvalidHostname", "HOSTNAME", hostStart, "Expected an unqualified hostname or IP address.");
            if (ctx.Eof()) return Error(ctx, "MissingDelimiter", "HOSTNAME", ctx.Position, "Expected SP after HOSTNAME.");
            ctx.Position++; // consume exactly the header delimiter
            int msgStart = ctx.Position;
            int tagStart = ctx.Position;
            while (!ctx.Eof() && AlphaNumeric(ctx.Current)) ctx.Position++;
            int length = ctx.Position-tagStart;
            entry.Rfc3164.Tag = ctx.Text.Substring(tagStart, length);
            entry.Rfc3164.Content = ctx.Text.Substring(ctx.Position);
            entry.Header.AppName = entry.Rfc3164.Tag;
            entry.Message = entry.Rfc3164.Content;
            if (length > 32) Error(ctx, "TagTooLong", "TAG", tagStart, "TAG exceeds 32 ASCII alphanumeric characters; retained without truncation.");
            // ProcId is only a convenience extraction; brackets remain part of CONTENT.
            var content = entry.Rfc3164.Content;
            if (content.Length > 2 && content[0] == '[')
            {
                int p = 1;
                while (p < content.Length && Digit(content[p]) >= 0) p++;
                if (p > 1 && p < content.Length && content[p] == ']') entry.Header.ProcId = content.Substring(1,p-1);
            }
            if (entry.Source.HasRawData)
            {
                int byteStart = ctx.RawByteOffset(msgStart);
                entry.MessageBytes = entry.Source.RawData.Slice(byteStart);
                entry.HasMessageBytes = true;
                entry.MessageEncoding = MessageEncodingStatus.Unspecified;
            }
            else entry.MessageEncoding = MessageEncodingStatus.TextOnly;
            for (int p = msgStart; p < ctx.Text.Length; p++)
                if (ctx.Text[p] < 32 || ctx.Text[p] == 127)
                { Error(ctx, "ControlInMessage", "MSG", p, "Control character retained in RFC 3164 MSG."); break; }
            return true;
        }
        private static bool ValidHost(string host)
        {
            if (IPAddress.TryParse(host, out _)) return true;
            if (host.Length > 63 || !AlphaNumeric(host[0]) || !AlphaNumeric(host[host.Length-1])) return false;
            foreach (char c in host) if (!AlphaNumeric(c) && c != '-') return false;
            return true;
        }
        private bool ReadTime(ParserContext ctx, int month)
        {
            int p = ctx.Position;
            string t = ctx.Text;
            var entry = ctx.ParsedMessage;
            var details = entry.Rfc3164;
            details.TimestampText = t.Substring(p, Math.Min(15,t.Length-p));
            entry.Header.TimestampText = details.TimestampText;
            if (t.Length-p < 16 || t[p+3] != ' ' || t[p+6] != ' ' || t[p+9] != ':' || t[p+12] != ':' || t[p+15] != ' ')
                return Error(ctx, "InvalidTimestamp", "TIMESTAMP", p, "Expected Mmm dd hh:mm:ss followed by SP.");
            int day = t[p+4] == ' ' ? Digit(t[p+5]) : Pair(t,p+4);
            int hour = Pair(t,p+7), minute = Pair(t,p+10), second = Pair(t,p+13);
            if (day < 1 || day > 31 || (day < 10 && t[p+4] != ' ') || hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59)
                return Error(ctx, "InvalidTimestamp", "TIMESTAMP", p, "Invalid day padding or time components.");
            var received = entry.Source.ReceivedOn;
            if (received == default)
            {
                Error(ctx, "MissingReceiveTime", "TIMESTAMP", p, "Cannot infer year without receive time.");
                ctx.Position += 16;
                return true;
            }
            var zone = SenderTimeZoneResolver?.Invoke(entry.Source);
            details.TimeZoneId = zone?.Id;
            if (received.Kind == DateTimeKind.Local) received = received.ToUniversalTime();
            // ReceivedOn is an instant; unspecified values are treated as UTC by this API contract.
            received = DateTime.SpecifyKind(received, DateTimeKind.Utc);
            var reference = zone == null ? received : TimeZoneInfo.ConvertTimeFromUtc(received, zone);
            DateTime? closest = null;
            long distance = long.MaxValue;
            for (int year = Math.Max(1,reference.Year-1); year <= Math.Min(9999,reference.Year+1); year++)
            {
                if (day > DateTime.DaysInMonth(year,month)) continue;
                var candidate = new DateTime(year,month,day,hour,minute,second,DateTimeKind.Unspecified);
                long delta = Math.Abs(candidate.Ticks-reference.Ticks);
                if (delta < distance) { closest = candidate; distance = delta; }
            }
            if (!closest.HasValue) return Error(ctx, "InvalidTimestamp", "TIMESTAMP", p, "Date is invalid in the receive year and adjacent years.");
            details.YearInferred = true;
            details.LocalTimestamp = closest;
            // Do not put a local clock reading into the UTC-facing Timestamp field.
            if (zone == null) details.TimeStatus = Rfc3164TimeStatus.ZoneUnknown;
            else if (zone.IsInvalidTime(closest.Value))
            {
                details.TimeStatus = Rfc3164TimeStatus.InvalidLocalTime;
                Error(ctx, "InvalidLocalTime", "TIMESTAMP", p, "Sender clock time falls in a DST gap; UTC left unset.");
            }
            else if (zone.IsAmbiguousTime(closest.Value))
            {
                details.TimeStatus = Rfc3164TimeStatus.AmbiguousLocalTime;
                Error(ctx, "AmbiguousLocalTime", "TIMESTAMP", p, "Sender clock time occurs twice at DST transition; UTC left unset.");
            }
            else
            {
                // Avoid ConvertTimeToUtc silently clamping at DateTime boundaries.
                long ticks = closest.Value.Ticks-zone.GetUtcOffset(closest.Value).Ticks;
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                    Error(ctx, "TimestampOutOfRange", "TIMESTAMP", p, "UTC value is outside DateTime range.");
                else
                {
                    entry.Header.Timestamp = new DateTime(ticks,DateTimeKind.Utc);
                    details.TimeStatus = Rfc3164TimeStatus.EstimatedUtc;
                }
            }
            ctx.Position += 16;
            return true;
        }
    }
}
