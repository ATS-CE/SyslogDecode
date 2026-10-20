// /********************************************************
// *                                                       *
// *   Copyright (C) Microsoft. All rights reserved.       *
// *                                                       *
// ********************************************************/

namespace SyslogDecode.Parsing
{
    using SyslogDecode.Common;
    using SyslogDecode.Model;
    using System.Collections.Generic;
    using System.Diagnostics;

    [DebuggerDisplay("P:{Position}, ch: {Current}")]
    public class ParserContext
    {
        public string Text;
        public int Position;
        public string Prefix; // standard <n> prefix identifying 'syslog' message
        public readonly ParsedSyslogMessage ParsedMessage;
        public List<string> ErrorMessages => ParsedMessage.ErrorMessages;

        public char Current => Position < Text.Length ? Text[Position] : '\0';
        public char CharAt(int position) => this.Text[position];
        public bool Eof() => this.Position >= this.Text.Length;

        public ParserContext(string message) : this(new RawSyslogMessage() { Message = message, ReceivedOn = AppTime.UtcNow }) { }

        public ParserContext(RawSyslogMessage msg)
        {
            if (msg == null) throw new System.ArgumentNullException(nameof(msg));
            Text = (msg.Message ?? string.Empty).CutOffBOM();
            ParsedMessage = new ParsedSyslogMessage(msg);
        }

        public void AddError(string message, string code = "ParserError", string stage = "Parsing")
        {
            ParsedMessage.RecordDiagnostic(new ParseDiagnostic(code, stage, Position, message));
        }


    }
}
