// /********************************************************
// *                                                       *
// *   Copyright (C) Microsoft. All rights reserved.       *
// *                                                       *
// ********************************************************/

namespace SyslogDecode.Parsing
{
    using System;
    using System.Globalization;
    using System.Linq;
    using SyslogDecode.Model;

    public static class TimestampParseHelper
    {
        static readonly string[] _months = "Jan,Feb,Mar,Apr,May,Jun,Jul,Aug,Sep,Oct,Nov,Dec".ToUpperInvariant().Split(',');
        static readonly string[] _timezones = "UTC,GMT,PDT,PST,IST".Split(',');

        public static bool TryParseTimestamp(ParserContext ctx)
        {
            try
            {
                return TryParseBsdTimestamp(ctx) || TryParseTimestampImpl(ctx);
            }
            catch (Exception)
            {
                // ctx.ErrorMessages.Add(ex.ToString());
                return false;
            }
        }

        private static bool TryParseBsdTimestamp(ParserContext ctx)
        {
            // Fast path for the fixed-width RFC 3164 timestamp. Keep vendor fallback below.
            int p = ctx.Position;
            var text = ctx.Text;
            if (text.Length - p < 16 || text[p + 3] != ' ' || text[p + 6] != ' ' ||
                text[p + 9] != ':' || text[p + 12] != ':' || text[p + 15] != ' ')
                return false;
            const string months = "JanFebMarAprMayJunJulAugSepOctNovDec";
            int month = 0;
            for (int i = 0; i < 12; i++)
                if (string.CompareOrdinal(text, p, months, i * 3, 3) == 0) { month = i + 1; break; }
            int day = text[p + 4] == ' ' ? Digit(text[p + 5]) : Pair(text, p + 4);
            int hour = Pair(text, p + 7), minute = Pair(text, p + 10), second = Pair(text, p + 13);
            var received = ctx.ParsedMessage.Source.ReceivedOn;
            if (received == default(DateTime)) received = DateTime.UtcNow;
            int year = received.Year;
            if (received.Month == 1 && month == 12) year--;
            if (year < 1 || month == 0 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
                hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59)
                return false;
            // Compatibility: original parser assumes UTC if no zone is supplied.
            ctx.ParsedMessage.Header.Timestamp = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            ctx.Position = p + 16;
            return true;
        }

        private static int Digit(char ch) => ch >= '0' && ch <= '9' ? ch - '0' : -1;
        private static int Pair(string text, int p)
        {
            int a = Digit(text[p]), b = Digit(text[p + 1]);
            return a < 0 || b < 0 ? -1 : a * 10 + b;
        }

        private static bool TryParseTimestampImpl(ParserContext ctx)
        {
            // sometimes there's starting space
            if (ctx.Current == SyslogChars.Space)
                ctx.SkipSpaces();

            // some messages start with some numeric Id: 
            // <139>36177473: Mar  4 17:21:18 UTC: 
            if (char.IsDigit(ctx.Current))
            {
                // make sure it's not a year and at least 5 chars
                // bug fix - make sure you have at least 5 chars in the string
                var fiveDigits = ctx.Text.Length > ctx.Position + 5 && ctx.Text.Substring(ctx.Position, 5).All(ch => char.IsDigit(ch));
                if (fiveDigits)
                {
                    var savePos = ctx.Position;
                    var digits = ctx.ReadDigits(20);
                    if (ctx.Match(": "))
                    {
                        // we swallowed this numeric prefix and ': ' after that, nothing to do
                    }
                    else
                    {
                        ctx.Position = savePos; // rollback, timestamp evaluation will go from string start
                    }
                }
            }
            var result = TryParseIfStartsWithYear(ctx) || TryParseTimestampWithMonthName(ctx) ||
                TryParseIfStartsWithColonThenYear(ctx) || TryParseIfStartsWithSpace(ctx);
            return result;

        }

        public static DateTime? ParseStandardTimestamp(this ParserContext ctx)
        {
            var ts = ctx.ReadWord();
            if (ts == null || ts == "-")
            {
                return null;
            }

            if (DateTime.TryParse(ts, out var dt))
            {
                // by default TryParse produces local time
                return dt.ToUniversalTime();
            }
            ctx.AddError($"Invalid timestamp '{ts}'.");
            return DateTime.MinValue;
        }



        // match entry like  
        //  <141>2020-03-04T17:20:54.412705+00:00 MWH....
        private static bool TryParseIfStartsWithYear(ParserContext ctx)
        {
            var year = DateTime.UtcNow.Year;
            var savePos = ctx.Position;
            if (ctx.Match($"{year}-") || ctx.Match($"{year - 1}-")) //(also check prior year)
            {
                ctx.Position = savePos;
                var spPos = ctx.Text.IndexOf(" ", ctx.Position);// 
                if (spPos > 0)
                {
                    var dtStr = ctx.Text.Substring(ctx.Position, spPos - ctx.Position);
                    ctx.Position = spPos;
                    if (DateTime.TryParse(dtStr, out var dt))
                    {
                        ctx.ParsedMessage.Header.Timestamp = dt.ToUniversalTime();
                        return true;
                    }
                }
            }
            ctx.Position = savePos;
            return false;
        }

        private static bool TryParseTimestampWithMonthName(ParserContext ctx)
        {
            if (ctx.Text.Length < ctx.Position + 21)
                return false;
            var tsStr = ctx.Text.Substring(ctx.Position, 20);
            if (tsStr.StartsWith(": "))
                tsStr = tsStr.Substring(2);
            if (tsStr.Contains("/"))
                return false;
            var words = tsStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 4)
                return false;
            string year = null;
            string month = null;
            string day = null;
            string time = null;
            string tz = null;
            var y = DateTime.UtcNow.Year;
            var thisYear = y.ToString();
            var prevYear = (y - 1).ToString();
            string lastWord = null;
            // Take first 5 words and try to understand what it is
            for (int i = 0; i < 5; i++)
            {
                if (i >= words.Length)
                    break;
                var word = words[i];
                if (word == thisYear || word == prevYear)
                    year = word;
                else if (word.Length == 3 && _months.Contains(word.ToUpperInvariant()))
                    month = word;
                else if (word.Length < 3 && int.TryParse(word, out var iDay))
                {
                    day = word;
                }
                else if (word.Contains(":") && word.Split(':').Length == 3)
                {
                    time = word;
                    lastWord = word;
                }
                else if (word.Length == 4 && word.EndsWith(":"))
                {
                    var w3 = word.Substring(0, 3).ToUpperInvariant();
                    if (_timezones.Contains(w3))
                    {
                        tz = w3;
                        lastWord = word;
                        break; //for loop
                    }
                }
            }
            // check if we have enough - at least month, day and time
            if (month == null || day == null || time == null)
                return false;
            if (year == null)
                year = GuessYear(month).ToString();
            ctx.ParsedMessage.Header.Timestamp = ConstructDateTime(year, month, day, time, tz);
            // advance position
            ctx.Position = ctx.Text.IndexOf(lastWord, ctx.Position) + lastWord.Length;
            return true;
        }

        private static DateTime ConstructDateTime(string year, string month, string day, string time, string tz)
        {
            var y = int.Parse(year);
            var m = Array.IndexOf(_months, month.ToUpperInvariant()) + 1;
            var d = int.Parse(day);
            TimeSpan.TryParse(time, out var t);
            var dt = new DateTime(y, m, d, t.Hours, t.Minutes, t.Seconds, DateTimeKind.Utc);
            if (tz != null)
            {
                var offs = GetTimezoneOffset(tz);
                dt = dt.Subtract(offs);
            }
            return dt;
        }

        private static TimeSpan GetTimezoneOffset(string value)
        {
            TimeSpan ts;
            switch (value)
            {
                case "PDT":
                    ts = TimeSpan.FromHours(-7);
                    break;
                case "PST":
                    ts = TimeSpan.FromHours(-8);
                    break;
                default:
                    ts = TimeSpan.Zero;
                    break;
            }
            return ts;
        }


        // guess year for a month, most likely current year, but handle new year 
        private static int GuessYear(string month)
        {
            var now = DateTime.UtcNow;
            var year = now.Year;
            // If it is Jan, but entry's month is Dec, then it was previous year
            if (string.Equals(month, "Dec", StringComparison.OrdinalIgnoreCase) && now.Month == 1)
            {
                year--;
            }
            return year;
        }

        // match entry like  
        //  <186>: 2020 Mar  4 17:20:54.183 UTC:
        private static bool TryParseIfStartsWithColonThenYear(ParserContext ctx)
        {
            var year = DateTime.UtcNow.Year;
            var savePos = ctx.Position;
            if (ctx.Match(": ") && (ctx.Match($"{year}") || ctx.Match($"{year - 1}"))) //check prior year
            {
                // we already matched beyond year; move back: 
                ctx.Position = savePos + ": ".Length;
                var UTC = "PST:"; // "UTC:";
                var utcPos = ctx.Text.IndexOf(UTC, ctx.Position);
                if (utcPos > 0)
                {
                    var dtStr = ctx.Text.Substring(ctx.Position, utcPos - ctx.Position);
                    ctx.Position = utcPos + UTC.Length;
                    if (DateTime.TryParse(dtStr, out var dt))
                    {
                        ctx.ParsedMessage.Header.Timestamp = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                        return true;
                    }
                }
            }
            ctx.Position = savePos;
            return false;
        }

        // match entry like  
        //  <134> 03/04/2020:17:20:58 GMT ams07....
        private static bool TryParseIfStartsWithSpace(ParserContext ctx)
        {
            ctx.Reset();
            if (ctx.Current != SyslogChars.Space)
                return false;
            var prefix = ctx.Text.Substring(ctx.Position, 25);
            var year = DateTime.UtcNow.Year;
            if (prefix.Contains($"/{year}:") || prefix.Contains($"/{year - 1}:")) //(also check prior year)
            {
                ctx.Position++;
                var spPos = ctx.Text.IndexOf(" ", ctx.Position);
                if (spPos > 0)
                {
                    var dtStr = ctx.Text.Substring(ctx.Position, spPos - ctx.Position);
                    ctx.Position = spPos + 1;
                    ctx.Match("GMT"); //skip also GMT
                    if (DateTime.TryParse(dtStr, out var dt) ||
                        DateTime.TryParseExact(dtStr, "MM/dd/yyyy:HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt))
                    {
                        ctx.ParsedMessage.Header.Timestamp = dt.ToUniversalTime();
                    }
                }
                return true;
            }
            return false;
        }

    }
}
