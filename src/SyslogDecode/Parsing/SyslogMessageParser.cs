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

    /// <summary>Configurable syslog message parser for multiple message formats (RFCs). </summary>
    /// <remarks>
    ///    Holds a configurable list of parsers for handling specific version/formats, and a 
    ///    list of value extractors for pattern-based extraction.   
    /// </remarks>
    public class SyslogMessageParser
    {
        public readonly List<ISyslogVariantParser> VariantParsers = new List<ISyslogVariantParser>();
        public readonly List<IValuesExtractor> ValueExtractors = new List<IValuesExtractor>();

        /// <summary> Creates and configures a default parser, with support for all major syslog versions 
        ///     and IP addresses extractor. 
        /// </summary>
        /// <returns>Syslog parser instance.</returns>
        /// <remarks>The default parser contains a number of parsers for specific syslog formats: RFC-5424, RFC-3164, 
        /// key-value pairs parser, and plain text parser. It also adds IP addresses extractor that extracts IPv4 and IPv6
        /// addresses from the messages and places them into ExtractedData dictionary field of the output parsed message.
        /// </remarks>
        public static SyslogMessageParser CreateDefault()
        {
            var parser = new SyslogMessageParser();
            parser.AddVariantParsers(new Rfc5424SyslogParser(), new KeyValueListParser(), 
                                     new Rfc3164SyslogParser(), new PlainTextParser());
            parser.AddValueExtractors(new IpAddressesExtractor());
            return parser;
        }

        /// <summary>Adds a list of format-specific parsers. </summary>
        /// <param name="parsers">A list of parsers.</param>
        public void AddVariantParsers(params ISyslogVariantParser[] parsers)
        {
            VariantParsers.AddRange(parsers);
        }

        /// <summary>Adds a list of pattern-based value extractors. </summary>
        /// <param name="extractors">A list of extractors.</param>
        public void AddValueExtractors(params IValuesExtractor[] extractors)
        {
            ValueExtractors.AddRange(extractors); 
        }

        /// <summary>Parses a raw syslog message and returns a parsed message object. </summary>
        /// <param name="rawMessage">Raw syslog message.</param>
        /// <returns></returns>
        public ParsedSyslogMessage Parse(RawSyslogMessage rawMessage)
        {
            var ctx = new ParserContext(rawMessage);
            Parse(ctx);
            return ctx.ParsedMessage; 
        }


        internal void Parse(ParserContext context)
        {
            var entry = context.ParsedMessage;
            if (string.IsNullOrEmpty(context.Text))
            {
                entry.Message = entry.Source.Message;
                context.AddError("Missing or empty input.", "EmptyInput", "Input");
                entry.ParseStatus = ParseStatus.Failed;
                return;
            }
            if (!context.ReadSyslogPrefix())
            {
                entry.Message = entry.Source.Message;
                context.AddError("Missing or invalid PRI; original input retained.", "InvalidPriority", "Priority");
                entry.ParseStatus = ParseStatus.Unrecognized;
                return;
            }
            context.AssignFacilitySeverity();
            entry.HasPriority = true;
            // A recognizable numeric version envelope must not fall through to another format on failure.
            bool versioned = Rfc5424SyslogParser.HasVersionPrefix(context);
            if (versioned) entry.PayloadType = PayloadType.Rfc5424;

            foreach (var parser in VariantParsers)
            {
                if (versioned && !(parser is Rfc5424SyslogParser)) continue;
                context.Reset();
                int errorsBefore = entry.ErrorMessages.Count;
                bool matched;
                try
                {
                    matched = parser.TryParse(context);
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is AccessViolationException))
                {
                    context.AddError(ex.Message, "ParserException", "Parsing");
                    entry.ParseStatus = ParseStatus.Failed;
                    return;
                }
                // Synchronize legacy diagnostics only when the extension bypassed AddError.
                entry.ImportLegacyDiagnostics(entry.Diagnostics.Count);
                if (matched)
                {
                    try
                    {
                        ExtractDataFromMessage(context);
                        BuildAllDataDictionary(entry);
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is AccessViolationException))
                    {
                        context.AddError(ex.Message, "ExtractionException", "Extraction");
                        entry.ParseStatus = ParseStatus.Failed;
                        return;
                    }
                    entry.ImportLegacyDiagnostics(entry.Diagnostics.Count);
                    entry.ParseStatus = entry.ErrorMessages.Count > 0 ? ParseStatus.ParsedWithErrors
                        : entry.PayloadType == PayloadType.PlainText ? ParseStatus.Unrecognized : ParseStatus.Parsed;
                    return;
                }
                if (versioned || entry.ErrorMessages.Count > errorsBefore || entry.PayloadType != PayloadType.NotSyslog)
                {
                    if (entry.ErrorMessages.Count == errorsBefore)
                        context.AddError("Recognized format could not be parsed.", "IncompleteMessage", "Parsing");
                    entry.ParseStatus = ParseStatus.Failed;
                    return;
                }
                // A non-matching parser is speculative: discard any tentative fields.
                entry.Header ??= new SyslogHeader();
                entry.Rfc3164 = null;
                entry.Header.Timestamp = null;
                entry.Header.TimestampText = null;
                entry.Header.Version = null;
                entry.Header.HostName = null;
                entry.Header.AppName = null;
                entry.Header.ProcId = null;
                entry.Header.MsgId = null;
                entry.Message = null;
                entry.StructuredData5424.Clear();
                entry.ExtractedTuples.Clear();
                entry.Data.Clear();
            }
            entry.Message = entry.Source.Message;
            context.AddError("No configured parser accepted the message.", "NoMatchingParser", "Detection");
            entry.ParseStatus = ParseStatus.Unrecognized;
        }

        private void ExtractDataFromMessage(ParserContext ctx)
        {
            var entry = ctx.ParsedMessage;
            var data = entry.ExtractedTuples;
            // For RFC-5424 and KeyValue payload types, everything is already structured and extracted
            // But we want to run IP values detector against all of them
            switch (entry.PayloadType)
            {
                case PayloadType.Rfc5424:
                    var allParams = entry.StructuredData5424.SelectMany(e => e.Value).ToList();
                    var Ips = IpAddressesExtractor.ExtractIpAddresses(allParams);
                    data.AddRange(Ips);
                    return; 

                case PayloadType.KeyValuePairs:
                    var Ips2 = IpAddressesExtractor.ExtractIpAddresses(entry.ExtractedTuples);
                    data.AddRange(Ips2); 
                    return; 
            }

            // otherwise run extractors from plain message
            if (entry == null || string.IsNullOrWhiteSpace(entry.Message) || entry.Message.Length < 10)
                return;

            foreach(var extr in ValueExtractors)
            {
                var values = extr.ExtractValues(ctx); 
                if (values != null && values.Count > 0)
                {
                    data.AddRange(values); 
                }
            }
        }

        /// <summary>Merges all parsed and extracted data into a single Data dictionary. </summary>
        /// <param name="message"></param>
        private void BuildAllDataDictionary(ParsedSyslogMessage message)
        {
            // Group directly: avoid flattened lists, LINQ GroupBy and a second dictionary of arrays.
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var pair in message.ExtractedTuples)
                AddValue(groups, pair);
            if (message.StructuredData5424 != null)
                foreach (var element in message.StructuredData5424)
                    foreach (var pair in element.Value)
                        AddValue(groups, pair);
            message.Data.Clear();
            foreach (var group in groups)
                message.Data[group.Key] = group.Value.Count == 1 && group.Key != "IPv4" && group.Key != "IPv6"
                    ? (object)group.Value[0] : group.Value.ToArray();
        }

        private static void AddValue(Dictionary<string, List<string>> groups, NameValuePair pair)
        {
            if (!groups.TryGetValue(pair.Name, out var values))
                groups.Add(pair.Name, values = new List<string>(1));
            values.Add(pair.Value);
        }

    }
}
