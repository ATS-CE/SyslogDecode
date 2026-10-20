// /********************************************************
// *                                                       *
// *   Copyright (C) Microsoft. All rights reserved.       *
// *                                                       *
// ********************************************************/

namespace SyslogDecode.Parsing
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using SyslogDecode.Model;

    public class Rfc5424SyslogParser: ISyslogVariantParser
    {
        public bool TryParse(ParserContext ctx)
        {
            if (!ctx.Reset())
                return false;
            if(!ctx.Match("1 "))
            {
                return false; 
            }

            // It is RFC-5424 entry
            var entry = ctx.ParsedMessage; 
            entry.PayloadType = PayloadType.Rfc5424; 
            try
            {
                entry.Header = this.ParseHeader(ctx);
                this.ParseStructuredData(ctx);
                entry.Message = this.ParseMessage(ctx);
                return true; 
            }
            catch (Exception ex)
            {
                ctx.AddError(ex.Message);
                return false; 
            }
        }

        private  SyslogHeader ParseHeader(ParserContext ctx)
        {
            var header = ctx.ParsedMessage.Header = new SyslogHeader(); 
            header.Timestamp = ctx.ParseStandardTimestamp();
            header.HostName = ctx.ReadWordOrNil();
            header.AppName = ctx.ReadWordOrNil();
            header.ProcId = ctx.ReadWordOrNil();
            header.MsgId = ctx.ReadWordOrNil();
            return header; 
        }

        private void ParseStructuredData(ParserContext ctx)
        {
            ctx.SkipSpaces();

            if (ctx.Current == SyslogChars.NilChar)
            {
                ctx.Position++;
                return; 
            }

            var data = ctx.ParsedMessage.StructuredData5424;
            try
            {
                if (ctx.Current != SyslogChars.Lbr)
                {
                    ctx.AddError("Expected structured data or NILVALUE.");
                    return; 
                }
                // start parsing elements
                while(!ctx.Eof())
                {
                    var elem = ParseElement(ctx);
                    if (elem == null)
                    {
                        return;
                    }
                    if (data.ContainsKey(elem.Item1))
                        ctx.AddError("Duplicate structured data ID.");
                    else
                        data.Add(elem.Item1, elem.Item2);
                }

            } catch (Exception ex)
            {
                ctx.AddError(ex.Message);
            }
        }

        private  Tuple<string, List<NameValuePair>> ParseElement(ParserContext ctx)
        {
            if (ctx.Current != SyslogChars.Lbr)
            {
                return null; 
            }
            ctx.Position++;
            var elemName = ReadName(ctx);
            ctx.SkipSpaces();
            var paramList = new List<NameValuePair>();
            var elem = new Tuple<string, List<NameValuePair>>(elemName, paramList);
            while (ctx.Current != SyslogChars.Rbr)
            {
                var paramName = ReadName(ctx);
                ctx.ReadSymbol('=');
                var paramValue = ReadParameterValue(ctx);
                var prm = new NameValuePair() { Name = paramName, Value = paramValue };
                paramList.Add(prm);
                ctx.SkipSpaces();
            }

            ctx.ReadSymbol(SyslogChars.Rbr);
            return elem; 
        }

        private static string ReadName(ParserContext ctx)
        {
            int start = ctx.Position;
            while (!ctx.Eof())
            {
                char ch = ctx.Current;
                if (ch == ' ' || ch == '=' || ch == ']') break;
                if (ch < 33 || ch > 126 || ch == '"')
                    throw new FormatException("Invalid structured data name.");
                ctx.Position++;
            }
            int length = ctx.Position - start;
            if (length == 0 || length > 32)
                throw new FormatException("Structured data names must contain 1..32 characters.");
            return ctx.Text.Substring(start, length);
        }

        private static string ReadParameterValue(ParserContext ctx)
        {
            ctx.ReadSymbol('"');
            int start = ctx.Position;
            StringBuilder builder = null;
            while (!ctx.Eof())
            {
                char ch = ctx.Current;
                if (ch == '"')
                {
                    string value = builder == null
                        ? ctx.Text.Substring(start, ctx.Position - start)
                        : builder.Append(ctx.Text, start, ctx.Position - start).ToString();
                    ctx.Position++;
                    return value;
                }
                if (ch == ']')
                    throw new FormatException("Unescaped closing bracket in parameter value.");
                if (ch == '\\' && ctx.Position + 1 < ctx.Text.Length)
                {
                    char next = ctx.Text[ctx.Position + 1];
                    if (next == '\\' || next == '"' || next == ']')
                    {
                        if (builder == null) builder = new StringBuilder();
                        builder.Append(ctx.Text, start, ctx.Position - start).Append(next);
                        ctx.Position += 2;
                        start = ctx.Position;
                        continue;
                    }
                    // RFC 5424 section 6.3.3: preserve unknown escape sequences.
                }
                ctx.Position++;
            }
            throw new FormatException("Unterminated structured data parameter value.");
        }

        private string ParseMessage(ParserContext ctx)
        {
            if (ctx.Eof())
            {
                return null;
            }
            if (ctx.Current != SyslogChars.Space)
                ctx.AddError("Expected space before MSG.");
            else
                ctx.Position++; // Exactly one delimiter; remaining spaces belong to MSG.
            var msg = ctx.Text.Substring(ctx.Position);
            // RFC 5424 allows BOM (byte order mark, 3 byte sequence) to precede the actual message. 
            // it will be read into the message OK, now 'msg' can contain this prefix - it is invisible
            // and will bring a lot of trouble when working with the string (ex: string comparisons are broken)
            // So we remove it explicitly.
            return msg.CutOffBOM();
        }


    } //class

}
