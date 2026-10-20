// /********************************************************
// *                                                       *
// *   Copyright (C) Microsoft. All rights reserved.       *
// *                                                       *
// ********************************************************/

namespace SyslogDecode.Model
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net;

    /// <summary>Defines the structure of a Syslog entry. </summary>
    [DebuggerDisplay("{Header}")]
    public class ParsedSyslogMessage
    {
        /// <summary>Detected format; it does not imply RFC validity.</summary>
        public PayloadType PayloadType;
        public ParseStatus ParseStatus { get; internal set; }
        public ValidationStatus ValidationStatus { get; internal set; }
        /// <summary>Whether Facility/Severity came from a successfully decoded PRI.</summary>
        public bool HasPriority { get; internal set; }
        public IReadOnlyList<ParseDiagnostic> Diagnostics => _diagnostics;
        private readonly List<ParseDiagnostic> _diagnostics = new List<ParseDiagnostic>();

        internal void RecordDiagnostic(ParseDiagnostic diagnostic)
        {
            ImportLegacyDiagnostics(_diagnostics.Count);
            _diagnostics.Add(diagnostic);
            ErrorMessages.Add(diagnostic.Position.HasValue
                ? $"{diagnostic.Message} (near {diagnostic.Position.Value})" : diagnostic.Message);
            ValidationStatus = ValidationStatus.ErrorsDetected;
        }

        // Older/custom parsers may still write to the public ErrorMessages list directly.
        internal void ImportLegacyDiagnostics(int start)
        {
            for (int i = start; i < ErrorMessages.Count; i++)
                _diagnostics.Add(new ParseDiagnostic("LegacyParserError", "Parsing", null, ErrorMessages[i]));
            if (ErrorMessages.Count > 0) ValidationStatus = ValidationStatus.ErrorsDetected;
        }
        public Facility Facility;
        public Severity Severity;
        public SyslogHeader Header;
        public string Message;

        /// <summary>The StructuredData element for entries following RFC-5424 spec. It is a list of named elements, 
        /// each having a list of name-value pairs. </summary>
        /// <remarks>Keys inside element can be repeated (see RFC 5424, example with IP parameter), so element value is a list of pairs, not dictionary.</remarks>
        public IDictionary<string, IList<NameValuePair>> StructuredData5424 = new Dictionary<string, IList<NameValuePair>>();

        /// <summary>Data extracted from text message sections using various extraction methods, mostly pattern-matching. Keys/names can be repeated, 
        /// so the data is represented as list of key-value pairs, not dictionary. </summary>
        public IList<NameValuePair> ExtractedTuples = new List<NameValuePair>();

        /// <summary>All data, parsed and extracted, represented as a Dictionary. Values from the <see cref="StructuredData5424"/> and <see cref="ExtractedTuples"/>
        /// are combined in this single dictionary. </summary>
        /// <remarks>Each value in a dictionary is either a single value, or an array of strings, if there is more than one value.
        /// The exception is IPv4 and IPv6 entries, which are always arrays, even if there is just one value. This is done for conveniences 
        /// of querying the data in databases like Kusto. </remarks>
        public IDictionary<string, object> Data = new Dictionary<string, object>();

        /// <summary>Source raw syslog message. </summary>
        public RawSyslogMessage Source; 

        /// <summary>Parsing error messages.</summary>
        public readonly List<string> ErrorMessages = new List<string>();

        public ParsedSyslogMessage(RawSyslogMessage source) {
            Source = source; 
            Header = new SyslogHeader(); 
        }

        public ParsedSyslogMessage(Facility facility, Severity severity, DateTime? timestamp = null, string hostName = null,
                           string appName = null, string procId = null, string msgId = null, string message = null)
        {
            PayloadType = PayloadType.Rfc5424;
            Facility = facility;
            Severity = severity;
            timestamp = timestamp ?? DateTime.UtcNow;
            Header = new SyslogHeader()
            {
                Timestamp = timestamp,
                HostName = hostName,
                AppName = appName,
                ProcId = procId,
                MsgId = msgId //Version should always be 1
            };
            Message = message;
        }
        public override string ToString() => Header?.ToString(); 
    }

    public class SyslogHeader
    {
        public DateTime? Timestamp; 
        public string HostName;
        public string AppName;
        public string ProcId;
        public string MsgId;
        public override string ToString() => $"{Timestamp} host:{HostName} app: {AppName}";
    }

    [DebuggerDisplay("{Name}={Value}")]
    public class NameValuePair
    {
        public string Name;
        public string Value; 
    }

}
