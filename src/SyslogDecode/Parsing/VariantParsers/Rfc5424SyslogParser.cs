// Copyright (C) Microsoft. All rights reserved.
namespace SyslogDecode.Parsing
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using SyslogDecode.Model;

    /// <summary>RFC 5424 version-1 text grammar. Byte-level encoding validation is separate.</summary>
    public class Rfc5424SyslogParser : ISyslogVariantParser
    {
        internal static bool HasVersionPrefix(ParserContext ctx)
        {
            int p = ctx.Position;
            while (p < ctx.Text.Length && IsDigit(ctx.Text[p])) p++;
            return p > ctx.Position && (p == ctx.Text.Length || ctx.Text[p] == ' ' || ctx.Text[p] == '\t');
        }

        public bool TryParse(ParserContext ctx)
        {
            if (!ctx.Reset() || !HasVersionPrefix(ctx)) return false;
            var entry = ctx.ParsedMessage;
            entry.PayloadType = PayloadType.Rfc5424;
            int start = ctx.Position;
            if (!ReadToken(ctx, "VERSION", 3, out var version)) return false;
            if (version[0] == '0' || !AllDigits(version))
                return Fail(ctx, "InvalidVersion", "VERSION", start, "VERSION must contain 1..3 digits without a leading zero.");
            entry.Header.Version = int.Parse(version, System.Globalization.CultureInfo.InvariantCulture);
            if (version != "1")
                return Fail(ctx, "UnsupportedVersion", "VERSION", start, "Only RFC 5424 version 1 is supported.");

            start = ctx.Position;
            if (!ReadToken(ctx, "TIMESTAMP", 32, out var timestamp)) return false;
            entry.Header.TimestampText = timestamp;
            if (timestamp != "-")
            {
                if (!TryTimestamp(timestamp, out var utc, out var rangeError))
                    Fail(ctx, rangeError ? "TimestampOutOfRange" : "InvalidTimestamp", "TIMESTAMP", start,
                        rangeError ? "Timestamp cannot be represented as a .NET UTC DateTime; original value retained."
                                   : "Invalid RFC 5424 timestamp syntax or calendar value.");
                else entry.Header.Timestamp = utc;
            }
            if (!ReadToken(ctx, "HOSTNAME", 255, out var host)) return false;
            entry.Header.HostName = Nil(host);
            if (!ReadToken(ctx, "APP-NAME", 48, out var app)) return false;
            entry.Header.AppName = Nil(app);
            if (!ReadToken(ctx, "PROCID", 128, out var proc)) return false;
            entry.Header.ProcId = Nil(proc);
            if (!ReadToken(ctx, "MSGID", 32, out var id)) return false;
            entry.Header.MsgId = Nil(id);

            if (!ReadStructuredData(ctx)) return false;
            if (ctx.Eof()) { entry.Message = null; entry.MessageEncoding = MessageEncodingStatus.Absent; return true; }
            if (ctx.Current != ' ')
                return Fail(ctx, "InvalidDelimiter", "MSG", ctx.Position, "Expected one SP before MSG.");
            ctx.Position++;
            Rfc5424Encoding.ReadMessage(ctx);
            return true;
        }

        private static string Nil(string value) => value == "-" ? null : value;
        private static bool IsDigit(char ch) => ch >= '0' && ch <= '9';
        private static bool AllDigits(string text)
        {
            foreach (char ch in text) if (!IsDigit(ch)) return false;
            return true;
        }
        private static bool Fail(ParserContext ctx, string code, string field, int position, string message)
        {
            ctx.ParsedMessage.RecordDiagnostic(new ParseDiagnostic(code, "RFC5424", position, message, field));
            return false;
        }
        private static bool ReadToken(ParserContext ctx, string field, int max, out string value)
        {
            int start = ctx.Position;
            value = null;
            while (!ctx.Eof() && ctx.Current != ' ')
            {
                if (ctx.Current < 33 || ctx.Current > 126)
                    return Fail(ctx, "InvalidHeaderCharacter", field, ctx.Position, "Header fields require printable US-ASCII.");
                if (ctx.Position - start == max)
                    return Fail(ctx, "HeaderFieldTooLong", field, ctx.Position, $"Field exceeds {max} characters.");
                ctx.Position++;
            }
            if (ctx.Position == start)
                return Fail(ctx, "EmptyHeaderField", field, start, "Empty header field; use NILVALUE instead.");
            if (ctx.Eof())
                return Fail(ctx, "TruncatedHeader", field, ctx.Position, "Header requires a following SP and field.");
            value = ctx.Text.Substring(start, ctx.Position - start);
            ctx.Position++; // exactly one delimiter, never SkipSpaces
            return true;
        }

        private static bool ReadStructuredData(ParserContext ctx)
        {
            if (ctx.Current == '-') { ctx.Position++; return true; }
            if (ctx.Current != '[')
                return Fail(ctx, "MissingStructuredData", "STRUCTURED-DATA", ctx.Position, "Expected NILVALUE or SD-ELEMENT.");
            while (ctx.Current == '[')
            {
                ctx.Position++;
                int start = ctx.Position;
                if (!ReadName(ctx, "SD-ID", out var name)) return false;
                if (!ValidEnterpriseSuffix(name))
                    return Fail(ctx, "InvalidEnterpriseId", "SD-ID", start, "Private SD-ID requires one @ followed by a decimal enterprise number.");
                if (ctx.ParsedMessage.StructuredData5424.ContainsKey(name))
                    return Fail(ctx, "DuplicateSdId", "SD-ID", start, "SD-ID must be unique within the message.");
                var parameters = new List<NameValuePair>();
                while (ctx.Current == ' ')
                {
                    ctx.Position++;
                    if (!ReadName(ctx, "PARAM-NAME", out var parameter)) return false;
                    if (ctx.Current != '=')
                        return Fail(ctx, "InvalidDelimiter", "SD-PARAM", ctx.Position, "Expected '=' immediately after PARAM-NAME.");
                    ctx.Position++;
                    if (!ReadValue(ctx, out var value)) return false;
                    parameters.Add(new NameValuePair { Name = parameter, Value = value });
                }
                if (ctx.Current != ']')
                    return Fail(ctx, "InvalidDelimiter", "SD-ELEMENT", ctx.Position, "Expected SP before a parameter or closing ']'.");
                ctx.Position++;
                // Publish only complete elements; a malformed element never leaks partial parameters.
                ctx.ParsedMessage.StructuredData5424.Add(name, parameters);
            }
            return true;
        }
        internal static bool ValidEnterpriseSuffix(string name)
        {
            int at = name.IndexOf('@');
            if (at < 0) return true; // Registry membership is not inferred from a fixed allow-list.
            if (at == 0 || at == name.Length - 1) return false;
            bool digit = false;
            for (int i = at + 1; i < name.Length; i++)
            {
                if (IsDigit(name[i])) { digit = true; continue; }
                if (name[i] != '.' || !digit) return false;
                digit = false;
            }
            if (!digit) return false;
            return true;
        }
        private static bool ReadName(ParserContext ctx, string field, out string name)
        {
            name = null;
            int start = ctx.Position;
            while (!ctx.Eof() && ctx.Current != ' ' && ctx.Current != '=' && ctx.Current != ']')
            {
                if (ctx.Current < 33 || ctx.Current > 126 || ctx.Current == '"')
                    return Fail(ctx, "InvalidSdName", field, ctx.Position, "Invalid SD-NAME character.");
                if (ctx.Position - start == 32)
                    return Fail(ctx, "InvalidSdName", field, ctx.Position, "SD-NAME exceeds 32 characters.");
                ctx.Position++;
            }
            if (ctx.Position == start)
                return Fail(ctx, "InvalidSdName", field, start, "SD-NAME must not be empty.");
            name = ctx.Text.Substring(start, ctx.Position - start);
            return true;
        }
        private static bool ReadValue(ParserContext ctx, out string value)
        {
            value = null;
            if (ctx.Current != '"')
                return Fail(ctx, "InvalidDelimiter", "PARAM-VALUE", ctx.Position, "Expected opening quote immediately after '='.");
            ctx.Position++;
            int start = ctx.Position;
            int valueStart = start;
            StringBuilder builder = null;
            while (!ctx.Eof())
            {
                char ch = ctx.Current;
                if (ch == '"')
                {
                    if (!Rfc5424Encoding.ValidateParameter(ctx, valueStart, ctx.Position)) return false;
                    value = builder == null ? ctx.Text.Substring(start, ctx.Position - start)
                        : builder.Append(ctx.Text, start, ctx.Position - start).ToString();
                    ctx.Position++;
                    return true;
                }
                if (ch == ']') return Fail(ctx, "UnescapedBracket", "PARAM-VALUE", ctx.Position, "']' must be escaped inside PARAM-VALUE.");
                if (ch == '\\' && ctx.Position + 1 < ctx.Text.Length)
                {
                    char next = ctx.Text[ctx.Position + 1];
                    if (next == '\\' || next == '"' || next == ']')
                    {
                        builder ??= new StringBuilder();
                        builder.Append(ctx.Text, start, ctx.Position - start).Append(next);
                        ctx.Position += 2;
                        start = ctx.Position;
                        continue;
                    }
                    // Unknown escapes MUST remain unchanged (RFC 5424 section 6.3.3).
                }
                ctx.Position++;
            }
            return Fail(ctx, "UnterminatedValue", "PARAM-VALUE", ctx.Position, "Missing closing quote.");
        }

        private static int Number(string text, int start, int length)
        {
            int number = 0;
            for (int i = start; i < start + length; i++)
            {
                if (i >= text.Length || !IsDigit(text[i])) return -1;
                number = number * 10 + text[i] - '0';
            }
            return number;
        }
        internal static bool TryTimestamp(string text, out DateTime utc, out bool rangeError)
        {
            utc = default;
            rangeError = false;
            if (text.Length < 20 || text[4] != '-' || text[7] != '-' || text[10] != 'T' ||
                text[13] != ':' || text[16] != ':') return false;
            int year = Number(text,0,4), month = Number(text,5,2), day = Number(text,8,2);
            int hour = Number(text,11,2), minute = Number(text,14,2), second = Number(text,17,2);
            if (year < 0 || month < 1 || month > 12 || day < 1 ||
                day > DateTime.DaysInMonth(year == 0 ? 400 : year, month) ||
                hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59) return false;
            int p = 19, microseconds = 0;
            if (text[p] == '.')
            {
                p++;
                int start = p;
                while (p < text.Length && IsDigit(text[p]))
                {
                    if (p - start == 6) return false;
                    microseconds = microseconds * 10 + text[p++] - '0';
                }
                if (p == start) return false;
                for (int n=p-start; n<6; n++) microseconds *= 10;
            }
            int offsetMinutes = 0;
            if (p < text.Length && text[p] == 'Z') p++;
            else
            {
                if (text.Length - p != 6 || (text[p] != '+' && text[p] != '-') || text[p+3] != ':') return false;
                int oh = Number(text,p+1,2), om = Number(text,p+4,2);
                if (oh < 0 || oh > 23 || om < 0 || om > 59) return false;
                offsetMinutes = (oh * 60 + om) * (text[p] == '+' ? 1 : -1);
                p += 6;
            }
            if (p != text.Length) return false;
            if (year == 0) { rangeError = true; return false; }
            long ticks = new DateTime(year,month,day,hour,minute,second,DateTimeKind.Utc).Ticks
                + microseconds * 10L - offsetMinutes * TimeSpan.TicksPerMinute;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) { rangeError = true; return false; }
            utc = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
    }
}
