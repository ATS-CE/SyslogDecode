// /********************************************************
// *                                                       *
// *   Copyright (C) Microsoft. All rights reserved.       *
// *                                                       *
// ********************************************************/

namespace SyslogDecode.Model
{
    using System;
    using System.Net;
    using System.Text;

    /// <summary>A container for the raw, unparsed syslog message and related information.</summary>
    public class RawSyslogMessage
    {
        /// <summary>Date-time when the message was received. </summary>
        public DateTime ReceivedOn;

        /// <summary>The IP address of the message sender. </summary>
        public IPAddress SourceIpAddress;

        /// <summary>The message content. </summary>
        public string Message;

        /// <summary>Sender UDP port, if known. Null for legacy text inputs.</summary>
        public int? SourcePort;

        /// <summary>Exact datagram bytes, owned by this message; never the reusable receive buffer.
        /// Empty bytes with HasRawData=true represent a received zero-length datagram.</summary>
        public ReadOnlyMemory<byte> RawData { get; private set; }
        public bool HasRawData { get; private set; }

        /// <summary>Copies a datagram before the caller can reuse its buffer. Message is only a
        /// UTF-8 display/legacy parsing view; RawData is authoritative, including invalid UTF-8.</summary>
        public static RawSyslogMessage FromDatagram(ReadOnlySpan<byte> bytes, DateTime receivedOn,
            IPAddress sourceIpAddress = null, int? sourcePort = null)
        {
            var owned = bytes.ToArray();
            return new RawSyslogMessage
            {
                RawData = owned, HasRawData = true, ReceivedOn = receivedOn,
                SourceIpAddress = sourceIpAddress, SourcePort = sourcePort,
                Message = Encoding.UTF8.GetString(owned)
            };
        }
    }

}
