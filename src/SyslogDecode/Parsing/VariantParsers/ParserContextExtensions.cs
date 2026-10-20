// /********************************************************
// *                                                       *
// *   Copyright (C) Microsoft. All rights reserved.       *
// *                                                       *
// ********************************************************/

namespace SyslogDecode.Parsing
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using SyslogDecode.Model;

    public static class ParserContextExtensions
    {
        public static bool ReadSymbol(this ParserContext ctx, char symbol, bool throwIfMismatch = true)
        {
            if (ctx.Current == SyslogChars.Space)
            {
                ctx.SkipSpaces();
            }
            if (ctx.Current == symbol)
            {
                ctx.Position++;
                return true; 
            }
            if (throwIfMismatch)
                throw new Exception($"Invalid input, expected '{symbol}' ");
            else
                return false; 
        }

        public static bool TryReadUntil(this ParserContext ctx, string symbol, out string text)
        {
            var spos = ctx.Text.IndexOf(symbol, ctx.Position); 
            if (spos > 0)
            {
                text = ctx.Text.Substring(ctx.Position, spos - ctx.Position);
                ctx.Position = spos;
                return true; 
            }
            text = null;
            return false; 
        }

        public static void SkipSpaces(this ParserContext ctx)
        {
            while (!ctx.Eof() && ctx.Current == SyslogChars.Space)
                ctx.Position++;
        }

        public static string ReadWordOrNil(this ParserContext ctx)
        {
            var word = ctx.ReadWord();
            return (word == SyslogChars.Nil) ? null : word;
        }


        public static string ReadWord(this ParserContext ctx)
        {
            if (ctx.Current == SyslogChars.Space)
            {
                ctx.SkipSpaces();
            }

            var separatorPos = ctx.Text.IndexOfAny(SyslogChars.WordSeparators, ctx.Position);
            if (separatorPos < 0)
            {
                separatorPos = ctx.Text.Length;
            }

            var word = ctx.Text.Substring(ctx.Position, separatorPos - ctx.Position);
            ctx.Position = separatorPos;
            return word;
        }

        public static string ReadQuotedString(this ParserContext ctx)
        {
            ctx.ReadSymbol(SyslogChars.DQuote);
            string result = string.Empty;
            int curr = ctx.Position;
            while (true)
            {
                var next = ctx.Text.IndexOfAny(SyslogChars.QuoteOrEscape, curr);
                var segment = ctx.Text.Substring(curr, next - curr);
                result += segment;
                switch (ctx.CharAt(next))
                {
                    case SyslogChars.DQuote:
                        // we are done
                        ctx.Position = next + 1; // after dquote
                        return result;

                    case SyslogChars.Escape:
                        // it is escape symbol, add next char to result, shift to next char and continue loop
                        result += ctx.CharAt(next + 1);
                        curr = next + 2;
                        break;
                }//switch
            } // loop
        }

        public static int ReadNumber(this ParserContext ctx, int maxDigits = 10)
        {
            var digits = ctx.ReadDigits(maxDigits);
            return int.Parse(digits); 

        }

        public static string ReadDigits(this ParserContext ctx, int maxDigits = 10)
        {
            var start = ctx.Position;
            for (int i = 0; i < maxDigits; i++) {
                if (!char.IsDigit(ctx.Current))
                    break;
                ctx.Position++;
                if (ctx.Position >= ctx.Text.Length)
                    break;
            }
            if (ctx.Position == start)
                return null;
            var res = ctx.Text.Substring(start, ctx.Position - start);
            return res; 
        }


        /// <summary>Parser standard (for all levels) prefix &lt;n&gt;. </summary>
        /// <param name="ctx">parser context.</param>
        /// <returns>True if prefix read correctly; otherwise, false.</returns>
        public static bool ReadSyslogPrefix(this ParserContext ctx)
        {
            ctx.Position = 0;
            ctx.Prefix = null;
            var text = ctx.Text;
            if (text.Length < 3 || text[0] != '<')
                return false;
            int pos = 1, priority = 0;
            while (pos < text.Length && pos <= 3 && text[pos] >= '0' && text[pos] <= '9')
                priority = priority * 10 + text[pos++] - '0';
            if (pos == 1 || pos >= text.Length || text[pos] != '>' || priority > 191 ||
                (pos > 2 && text[1] == '0'))
                return false;
            ctx.Position = pos + 1;
            ctx.Prefix = text.Substring(0, ctx.Position);
            return true;
        }

        public static void AssignFacilitySeverity(this ParserContext ctx)
        {
            // Prefix has already been validated. No Replace/temporary numeric strings.
            int priority = 0;
            for (int i = 1; i < ctx.Prefix.Length - 1; i++)
                priority = priority * 10 + ctx.Prefix[i] - '0';
            ctx.ParsedMessage.Facility = (Facility)(priority / 8);
            ctx.ParsedMessage.Severity = (Severity)(priority % 8);
        }

        public static bool Reset(this ParserContext ctx)
        {
            if (ctx.Prefix == null && !ctx.ReadSyslogPrefix())
            {
                return false; 
            }
            ctx.Position = ctx.Prefix.Length;
            return true; 
        }

        public static bool Match(this ParserContext ctx, string token, 
              StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            if (ctx.Position + token.Length > ctx.Text.Length)
                return false; 
            if (string.Compare(ctx.Text, ctx.Position, token, 0, token.Length, comparison) == 0)
            {
                ctx.Position += token.Length;
                return true;
            }
            return false; 
        }

        public static string GetValue(this IList<NameValuePair> prms, string name)
        {
            return prms.FirstOrDefault(p => p.Name == name)?.Value;
        }

        public static string CutOffBOM(this string msg)
        {
            if (msg.StartsWith(SyslogChars.BOM, StringComparison.Ordinal))
                msg = msg.Substring(SyslogChars.BOM.Length);
            return msg; 
        }

    } //class
}
