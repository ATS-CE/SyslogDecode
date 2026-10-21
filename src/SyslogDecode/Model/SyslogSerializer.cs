namespace SyslogDecode.Model
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using SyslogDecode.Parsing;

    /// <summary>Validated RFC 5424 version-1 output. Original datagrams remain in Source.RawData.</summary>
    public static class SyslogSerializer
    {
        // Formatting is fixed to invariant RFC syntax; no mutable process-wide format override.
        public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>Serializes Message as Unicode with a BOM. Null means absent; empty means present.
        /// Does not silently reuse previously parsed MessageBytes after edits.</summary>
        public static string Serialize(ParsedSyslogMessage entry)
        {
            var writer = WriteEnvelope(entry);
            if (entry.Message != null)
            {
                ValidateUnicode(entry.Message, "MSG");
                writer.Append(' ').Append('\uFEFF').Append(entry.Message);
            }
            return writer.ToString();
        }
        public static byte[] SerializeBytes(ParsedSyslogMessage entry) => StrictUtf8.GetBytes(Serialize(entry));

        /// <summary>Explicit byte payload, including any BOM. Empty span means present empty MSG.
        /// Without BOM any bytes are permitted. With BOM strict UTF-8 is required.</summary>
        public static byte[] SerializeBytes(ParsedSyslogMessage entry, ReadOnlySpan<byte> messageBytes)
        {
            var envelope = WriteEnvelope(entry).ToString();
            if (messageBytes.Length >= 3 && messageBytes[0] == 0xef && messageBytes[1] == 0xbb && messageBytes[2] == 0xbf)
            {
                try { StrictUtf8.GetCharCount(messageBytes.Slice(3)); }
                catch (DecoderFallbackException ex) { throw new ArgumentException("BOM-marked MSG must be valid shortest-form UTF-8.", nameof(messageBytes), ex); }
            }
            int length = StrictUtf8.GetByteCount(envelope);
            var bytes = new byte[checked(length + 1 + messageBytes.Length)];
            StrictUtf8.GetBytes(envelope.AsSpan(), bytes.AsSpan());
            bytes[length] = 32;
            messageBytes.CopyTo(bytes.AsSpan(length + 1));
            return bytes;
        }
        private static StringBuilder WriteEnvelope(ParsedSyslogMessage entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.PayloadType != PayloadType.Rfc5424)
                throw new ArgumentException("Only RFC 5424 entries can be serialized; map other formats explicitly.", nameof(entry));
            if (entry.ParseStatus == ParseStatus.Failed || entry.ParseStatus == ParseStatus.Unrecognized || entry.ParseStatus == ParseStatus.ParsedWithErrors)
                throw new ArgumentException("Cannot serialize a failed or diagnosed parse result; construct a corrected entry explicitly.", nameof(entry));
            var h = entry.Header ?? throw new ArgumentException("Header is required.", nameof(entry));
            if (h.Version.HasValue && h.Version != 1) throw new ArgumentException("Only VERSION 1 is supported.", nameof(entry));
            int facility = (int)entry.Facility, severity = (int)entry.Severity;
            if (facility < 0 || facility > 23 || severity < 0 || severity > 7)
                throw new ArgumentException("Facility must be 0..23 and severity 0..7.", nameof(entry));
            var writer = new StringBuilder();
            writer.Append('<').Append((facility*8+severity).ToString(CultureInfo.InvariantCulture)).Append(">1 ");
            writer.Append(Timestamp(h)).Append(' ');
            HeaderField(writer,h.HostName,255,"HOSTNAME");
            HeaderField(writer,h.AppName,48,"APP-NAME");
            HeaderField(writer,h.ProcId,128,"PROCID");
            HeaderField(writer,h.MsgId,32,"MSGID");
            StructuredData(writer,entry.StructuredData5424);
            return writer;
        }
        private static string Timestamp(SyslogHeader header)
        {
            if (!header.Timestamp.HasValue) return "-";
            DateTime time = header.Timestamp.Value;
            if (time.Kind == DateTimeKind.Unspecified)
                throw new ArgumentException("Timestamp needs explicit UTC or Local kind; an unspecified clock has no UTC offset.", "TIMESTAMP");
            if (time.Kind == DateTimeKind.Local)
            {
                var clock = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
                if (TimeZoneInfo.Local.IsInvalidTime(clock) || TimeZoneInfo.Local.IsAmbiguousTime(clock))
                    throw new ArgumentException("Local timestamp is ambiguous or falls in a DST gap; supply UTC explicitly.", "TIMESTAMP");
                long ticks = time.Ticks - TimeZoneInfo.Local.GetUtcOffset(time).Ticks;
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                    throw new ArgumentException("UTC timestamp is outside DateTime range.", "TIMESTAMP");
                time = time.ToUniversalTime();
            }
            // Preserve an unchanged valid source token, including the unknown-offset marker -00:00.
            if (header.TimestampText != null && Rfc5424SyslogParser.TryTimestamp(header.TimestampText, out var original, out _) && original == time)
                return header.TimestampText;
            // RFC 5424 supports microseconds; discard only the final 100ns digit, never round into another second.
            time = new DateTime(time.Ticks - time.Ticks % 10, DateTimeKind.Utc);
            string result = time.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            return result + "Z";
        }
        private static void HeaderField(StringBuilder writer, string value, int maximum, string field)
        {
            if (value == null) { writer.Append("- "); return; }
            if (value.Length == 0 || value.Length > maximum) throw new ArgumentException("Invalid field length.",field);
            foreach (char c in value) if (c < 33 || c > 126) throw new ArgumentException("Expected printable US-ASCII without spaces.",field);
            writer.Append(value).Append(' ');
        }
        private static void Name(string value, string field)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 32) throw new ArgumentException("Name must contain 1..32 characters.",field);
            foreach (char c in value)
                if (c < 33 || c > 126 || c == '=' || c == ']' || c == '"') throw new ArgumentException("Invalid SD-NAME character.",field);
        }
        private static void ValidateUnicode(string value, string field)
        {
            try { StrictUtf8.GetByteCount(value); }
            catch (EncoderFallbackException ex) { throw new ArgumentException("Unpaired UTF-16 surrogate.",field,ex); }
        }
        private static void StructuredData(StringBuilder writer, IDictionary<string,IList<NameValuePair>> data)
        {
            if (data == null || data.Count == 0) { writer.Append('-'); return; }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in data)
            {
                Name(element.Key,"SD-ID");
                if (!names.Add(element.Key)) throw new ArgumentException("Duplicate SD-ID.","SD-ID");
                if (!Rfc5424SyslogParser.ValidEnterpriseSuffix(element.Key)) throw new ArgumentException("Invalid private enterprise suffix.","SD-ID");
                if (element.Value == null) throw new ArgumentException("Parameter list cannot be null.","SD-PARAM");
                writer.Append('[').Append(element.Key);
                foreach (var parameter in element.Value)
                {
                    if (parameter == null) throw new ArgumentException("Parameter cannot be null.","SD-PARAM");
                    Name(parameter.Name,"PARAM-NAME");
                    if (parameter.Value == null) throw new ArgumentException("Use an empty string for an empty parameter value.","PARAM-VALUE");
                    ValidateUnicode(parameter.Value,"PARAM-VALUE");
                    writer.Append(' ').Append(parameter.Name).Append("=\"");
                    foreach (char c in parameter.Value)
                    {
                        if (c == '\\' || c == '"' || c == ']') writer.Append('\\');
                        writer.Append(c);
                    }
                    writer.Append('"');
                }
                writer.Append(']');
            }
        }
    }
}
