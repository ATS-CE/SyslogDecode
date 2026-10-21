namespace SyslogDecode.Parsing
{
    using System;
    using System.Buffers;
    using System.Text;
    using SyslogDecode.Model;

    internal static class Rfc5424Encoding
    {
        private static int InvalidUtf8(ReadOnlySpan<byte> bytes)
        {
            int p = 0;
            while (p < bytes.Length)
            {
                if (Rune.DecodeFromUtf8(bytes.Slice(p), out _, out int consumed) != OperationStatus.Done) return p;
                p += consumed;
            }
            return -1;
        }
        private static int InvalidUtf16(ReadOnlySpan<char> text)
        {
            int p = 0;
            while (p < text.Length)
            {
                if (Rune.DecodeFromUtf16(text.Slice(p), out _, out int consumed) != OperationStatus.Done) return p;
                p += consumed;
            }
            return -1;
        }
        internal static bool ValidateParameter(ParserContext ctx, int start, int end)
        {
            if (ctx.ParsedMessage.Source.HasRawData)
            {
                int byteStart = ctx.RawByteOffset(start);
                // Quote is ASCII: scan the original bytes, respecting the three recognized escapes.
                var bytes = ctx.ParsedMessage.Source.RawData.Span;
                int stop = byteStart;
                while (stop < bytes.Length && bytes[stop] != 34)
                {
                    if (bytes[stop] == 92 && stop + 1 < bytes.Length &&
                        (bytes[stop+1] == 92 || bytes[stop+1] == 34 || bytes[stop+1] == 93)) stop++;
                    stop++;
                }
                int invalid = InvalidUtf8(bytes.Slice(byteStart, stop - byteStart));
                if (invalid < 0) return true;
                ctx.ParsedMessage.RecordDiagnostic(new ParseDiagnostic("InvalidUtf8", "Encoding", null,
                    "PARAM-VALUE contains invalid UTF-8; element not interpreted.", "PARAM-VALUE", byteStart + invalid));
            }
            else
            {
                int invalid = InvalidUtf16(ctx.Text.AsSpan(start, end - start));
                if (invalid < 0) return true;
                ctx.ParsedMessage.RecordDiagnostic(new ParseDiagnostic("InvalidUnicode", "Encoding", start + invalid,
                    "PARAM-VALUE contains an unpaired UTF-16 surrogate.", "PARAM-VALUE"));
            }
            return false;
        }
        internal static void ReadMessage(ParserContext ctx)
        {
            var entry = ctx.ParsedMessage;
            if (!entry.Source.HasRawData)
            {
                entry.MessageEncoding = MessageEncodingStatus.TextOnly;
                string text = ctx.Text.Substring(ctx.Position);
                // Legacy strings have no original encoding; check scalar validity without claiming byte validation.
                int invalid = InvalidUtf16(text.AsSpan());
                if (invalid >= 0)
                {
                    entry.RecordDiagnostic(new ParseDiagnostic("InvalidUnicode", "Encoding", ctx.Position + invalid,
                        "MSG contains an unpaired UTF-16 surrogate.", "MSG"));
                    return;
                }
                entry.Message = text.CutOffBOM();
                return;
            }
            int start = ctx.RawByteOffset(ctx.Position);
            entry.MessageBytes = entry.Source.RawData.Slice(start);
            entry.HasMessageBytes = true;
            var bytes = entry.MessageBytes.Span;
            bool bom = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
            if (!bom)
            {
                // MSG-ANY is arbitrary octets. This remains a compatibility display view, not an encoding assertion.
                entry.MessageEncoding = MessageEncodingStatus.Unspecified;
                entry.Message = ctx.Text.Substring(ctx.Position);
                return;
            }
            int error = InvalidUtf8(bytes.Slice(3));
            if (error >= 0)
            {
                entry.MessageEncoding = MessageEncodingStatus.InvalidUtf8;
                entry.RecordDiagnostic(new ParseDiagnostic("InvalidUtf8", "Encoding", null,
                    "BOM-marked MSG contains invalid UTF-8; raw bytes retained without interpreting MSG.", "MSG", start + 3 + error));
                return;
            }
            entry.MessageEncoding = MessageEncodingStatus.Utf8;
            // Remove precisely the encoding marker, retaining any subsequent U+FEFF character.
            entry.Message = ctx.Text.Substring(ctx.Position + 1);
        }
    }
}
