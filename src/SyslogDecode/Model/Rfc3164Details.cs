namespace SyslogDecode.Model
{
    using System;

    public enum Rfc3164TimeStatus
    {
        Unresolved, ZoneUnknown, EstimatedUtc, AmbiguousLocalTime, InvalidLocalTime, VendorTimestamp
    }

    /// <summary>RFC 3164 has neither a year nor a time zone on the wire.</summary>
    public sealed class Rfc3164Details
    {
        public string TimestampText { get; internal set; }
        public DateTime? LocalTimestamp { get; internal set; }
        public bool YearInferred { get; internal set; }
        public string TimeZoneId { get; internal set; }
        public Rfc3164TimeStatus TimeStatus { get; internal set; }
        public string Tag { get; internal set; }
        /// <summary>Everything after TAG, including its terminating character and all whitespace.</summary>
        public string Content { get; internal set; }
    }
}
