namespace SyslogDecode.Model
{
    /// <summary>Extraction outcome, not an RFC conformity certificate.</summary>
    public enum ParseStatus
    {
        NotParsed, Parsed, ParsedWithErrors, Unrecognized, Failed
    }

    /// <summary>Full RFC validation is not implemented yet. Never infer conformance from Parsed.</summary>
    public enum ValidationStatus
    {
        NotValidated, ErrorsDetected
    }

    /// <summary>Encoding classification of RFC 5424 MSG, independent of overall RFC validity.</summary>
    public enum MessageEncodingStatus { NotExamined, Absent, TextOnly, Unspecified, Utf8, InvalidUtf8 }

    public sealed class ParseDiagnostic
    {
        public string Code { get; }
        public string Stage { get; }
        public string Field { get; }
        public int? BytePosition { get; }
        /// <summary>UTF-16 index in the parser's text view, not a raw byte offset. Null if unknown.</summary>
        public int? Position { get; }
        public string Message { get; }

        public ParseDiagnostic(string code, string stage, int? position, string message, string field = null, int? bytePosition = null)
        {
            BytePosition = bytePosition; Code = code; Stage = stage; Field = field; Position = position; Message = message;
        }
    }
}
