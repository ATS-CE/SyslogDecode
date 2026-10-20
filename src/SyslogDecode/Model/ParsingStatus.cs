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

    public sealed class ParseDiagnostic
    {
        public string Code { get; }
        public string Stage { get; }
        public string Field { get; }
        /// <summary>UTF-16 index in the parser's text view, not a raw byte offset. Null if unknown.</summary>
        public int? Position { get; }
        public string Message { get; }

        public ParseDiagnostic(string code, string stage, int? position, string message, string field = null)
        {
            Code = code; Stage = stage; Field = field; Position = position; Message = message;
        }
    }
}
