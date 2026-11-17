using System.Text.RegularExpressions;
using TravelVault.Core.Common;
using TravelVault.Core.Documents;
using F = TravelVault.Core.Documents.TravelDocument.Fields;

namespace TravelVault.Core.Parsing;

/// <summary>
/// Turns extracted text + barcodes into a <see cref="ParsedDocument"/>. Order of trust:
/// 1) standard machine formats (boarding-pass barcode, MRZ) — exact; 2) labelled values ("PNR: …") — high;
/// 3) patterns without labels (airport-code pairs, dates near keywords) — medium; 4) fallbacks — low, flagged for review.
/// </summary>
public static partial class TravelDocumentParser
{
    private static readonly HashSet<string> NotAirports = new(StringComparer.Ordinal)
    {
        "PNR", "THE", "AND", "FOR", "YOU", "ALL", "NOT", "ARE", "FROM", "VIA", "AIR", "PAX", "ADT", "CHD", "INF", "SSR", "GST",
        "TAX", "FEE", "INR", "USD", "EUR", "GBP", "AED", "SGD", "QTY", "REF", "NON", "YES", "MRS", "DOB", "ETA", "ETD", "STD",
        "STA", "UTC", "GMT", "IST", "PDF", "WEB", "APP", "OTP", "SMS", "SEC", "MIN", "HRS", "DAY", "MON", "TUE", "WED", "THU",
        "FRI", "SAT", "SUN", "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC", "BAG", "KGS",
    };

    public static ParsedDocument Parse(ExtractedContent content, DateOnly reference)
    {
        var facts = TextFacts.Analyze(content.Text, reference);
        var mrz = MrzParser.Find(content.Text, reference);
        var (type, typeConfidence) = DocumentClassifier.Classify(facts, content.Barcodes, mrz);

        var result = new ParsedDocument { Type = type };
        result.Set(F.Type, typeConfidence);

        switch (type)
        {
            case DocumentType.BoardingPass:
                ParseBoardingPass(result, facts, content.Barcodes, reference);
                break;
            case DocumentType.Flight:
                ParseFlight(result, facts);
                break;
            case DocumentType.Train:
            case DocumentType.Bus:
                ParseGroundTravel(result, facts, type == DocumentType.Train ? TravelMode.Train : TravelMode.Bus);
                break;
            case DocumentType.Hotel:
                ParseHotel(result, facts);
                break;
            case DocumentType.Event:
                ParseEvent(result, facts);
                break;
            case DocumentType.Visa:
            case DocumentType.Passport:
                ParseIdentity(result, facts, mrz);
                break;
            default:
                ParseReferences(result, facts);
                AddNames(result, facts);
                if (FindAirportRoutes(facts).Count > 0)
                {
                    ParseFlight(result, facts); // unclassified but has an airport route: treat as flight, low confidence
                    result.Type = DocumentType.Flight;
                    result.Set(F.Type, Confidence.Low);
                }

                break;
        }

        if (facts.DateTimes.Any(d => d.Ambiguous))
        {
            result.Warnings.Add("Some dates could be day/month or month/day. Day/month was assumed — please check.");
        }

        return result;
    }

    // ---------------- Boarding pass ----------------

    private static void ParseBoardingPass(ParsedDocument result, TextFacts facts, IReadOnlyList<Barcode> barcodes, DateOnly reference)
    {
        var data = barcodes.Select(b => BcbpDecoder.Decode(b.Value, reference)).FirstOrDefault(d => d is not null);
        if (data is null)
        {
            // "Boarding pass" printed but no readable barcode: read it like a flight ticket.
            ParseFlight(result, facts);
            result.Type = DocumentType.BoardingPass;
            return;
        }

        result.BoardingPass = data;
        result.PassengerNames.Add(data.PassengerName);
        result.Set(F.Passengers, Confidence.High);
        result.Pnr = data.Legs[0].Pnr;
        result.Set(F.Pnr, Confidence.High);
        result.Provider = Airlines.Name(data.Legs[0].Carrier) ?? data.Legs[0].Carrier;
        result.Set(F.Provider, Airlines.IsKnown(data.Legs[0].Carrier) ? Confidence.High : Confidence.Medium);

        // The barcode has the date but not the time; take a departure/boarding time from the text when printed.
        var departureTime = TimeAfterLabel(facts, @"depart(?:ure|s)?|dep\.?|flight\s*time|std");
        foreach (var leg in data.Legs)
        {
            var from = Place(leg.From);
            result.Segments.Add(new Segment
            {
                Id = Ids.New(),
                Mode = TravelMode.Flight,
                From = from,
                To = Place(leg.To),
                Departs = leg.Date is { } d ? new LocalDateTimeZ(d.ToDateTime(leg == data.Legs[0] && departureTime is { } t ? t : TimeOnly.MinValue), from.TimeZoneId) : null,
                CarrierCode = leg.Carrier,
                ServiceNumber = $"{leg.Carrier} {leg.FlightNumber}",
                Seat = leg.Seat.Length > 0 ? leg.Seat : null,
                TravelClass = leg.Compartment.ToString(),
            });
        }

        result.Set(F.Segments, Confidence.High);
    }

    private static TimeOnly? TimeAfterLabel(TextFacts facts, string label)
    {
        var value = facts.LabelValue(label);
        if (value is null)
        {
            return null;
        }

        var m = TimeInText().Match(value);
        return m.Success && TimeOnly.TryParse(m.Value.Replace('.', ':'), System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    // ---------------- Flight ----------------

    private static void ParseFlight(ParsedDocument result, TextFacts facts)
    {
        ParseReferences(result, facts, flight: true);
        var routes = FindAirportRoutes(facts);
        var flights = FlightNumbers(facts);
        var used = new HashSet<DateTimeMention>();

        for (var i = 0; i < routes.Count; i++)
        {
            var (from, to, line) = routes[i];
            var departs = facts.DateTimes.FirstOrDefault(d => !used.Contains(d) && d.Line >= line - 2)
                ?? facts.DateTimes.FirstOrDefault(d => !used.Contains(d));
            DateTimeMention? arrives = null;
            if (departs is not null)
            {
                used.Add(departs);
                arrives = facts.DateTimes.FirstOrDefault(d => !used.Contains(d) && d.Line <= departs.Line + 6 && d.Time is not null
                    && d.ToDateTime() > departs.ToDateTime() && d.ToDateTime() - departs.ToDateTime() < TimeSpan.FromHours(30));
                if (arrives is not null)
                {
                    used.Add(arrives);
                }
            }

            var flight = i < flights.Count ? flights[i] : flights.LastOrDefault();
            result.Segments.Add(new Segment
            {
                Id = Ids.New(),
                Mode = TravelMode.Flight,
                From = from,
                To = to,
                Departs = departs is null ? null : new LocalDateTimeZ(departs.ToDateTime(), from.TimeZoneId),
                Arrives = arrives is null ? null : new LocalDateTimeZ(arrives.ToDateTime(), to.TimeZoneId),
                CarrierCode = flight.Code,
                ServiceNumber = flight.Code is null ? null : $"{flight.Code} {flight.Number}",
                Seat = Seat(facts),
                TravelClass = TravelClass(facts),
            });
        }

        var segmentsOk = result.Segments.Count > 0 && result.Segments.All(s => s.Departs is not null);
        result.Set(F.Segments, segmentsOk ? (result.Segments.Any(s => s.Departs!.Value.HasTime) ? Confidence.High : Confidence.Medium) : Confidence.Low);

        var airline = flights.Select(f => f.Code).FirstOrDefault(c => c is not null);
        if (airline is not null)
        {
            result.Provider = Airlines.Name(airline);
            result.Set(F.Provider, Confidence.High);
        }
        else if (Airlines.FindByName(facts.Text) is { } byName)
        {
            result.Provider = byName.Name;
            result.Set(F.Provider, Confidence.Medium);
        }
        else
        {
            ProviderFromBrands(result, facts);
        }

        AddNames(result, facts);
    }

    /// <summary>Airport routes in reading order: "BLR → DEL", "Bengaluru (BLR) … Delhi (DEL)", or "Bengaluru to Delhi".</summary>
    internal static List<(Place From, Place To, int Line)> FindAirportRoutes(TextFacts facts)
    {
        var routes = new List<(Place, Place, int)>();
        var directory = AirportDirectory.Instance;

        // 1) Explicit code pairs on one line.
        for (var i = 0; i < facts.Lines.Count; i++)
        {
            foreach (Match m in CodePair().Matches(facts.Lines[i]))
            {
                if (Valid(m.Groups[1].Value) is { } a && Valid(m.Groups[2].Value) is { } b && a.Code != b.Code)
                {
                    AddRoute(a.ToPlace(), b.ToPlace(), i);
                }
            }
        }

        // 2) Codes in brackets, in order, paired: "(BLR) … (DEL)".
        if (routes.Count == 0)
        {
            var codes = new List<(Airport Airport, int Line)>();
            for (var i = 0; i < facts.Lines.Count; i++)
            {
                foreach (Match m in BracketCode().Matches(facts.Lines[i]))
                {
                    if (Valid(m.Groups[1].Value) is { } a)
                    {
                        codes.Add((a, i));
                    }
                }
            }

            // Remove immediate repeats (the same code printed twice in a row, e.g. header + detail).
            codes = codes.Where((c, idx) => idx == 0 || c.Airport.Code != codes[idx - 1].Airport.Code).ToList();
            for (var i = 0; i + 1 < codes.Count; i += 2)
            {
                AddRoute(codes[i].Airport.ToPlace(), codes[i + 1].Airport.ToPlace(), codes[i].Line);
            }
        }

        // 3) Itinerary tables: two airport codes on one row together with a time or flight number
        //    ("EK 569   BLR Bengaluru T2   DXB Dubai T3   15Jan2027 04:25").
        if (routes.Count == 0)
        {
            for (var i = 0; i < facts.Lines.Count; i++)
            {
                var line = facts.Lines[i];
                var codes = StandaloneCode().Matches(line)
                    .Select(m => Valid(m.Value))
                    .Where(a => a is not null)
                    .Select(a => a!)
                    .DistinctBy(a => a.Code)
                    .ToList();
                if (codes.Count >= 2 && (TimeInText().IsMatch(line) || FlightNumber().IsMatch(line)))
                {
                    AddRoute(codes[0].ToPlace(), codes[1].ToPlace(), i);
                }
            }
        }

        // 4) Lines starting with a bare code followed by a time: "BLR 06:10" / "DEL 08:55".
        if (routes.Count == 0)
        {
            var codes = new List<(Airport Airport, int Line)>();
            for (var i = 0; i < facts.Lines.Count; i++)
            {
                var m = LeadingCode().Match(facts.Lines[i]);
                if (m.Success && Valid(m.Groups[1].Value) is { } a)
                {
                    codes.Add((a, i));
                }
            }

            for (var i = 0; i + 1 < codes.Count; i += 2)
            {
                AddRoute(codes[i].Airport.ToPlace(), codes[i + 1].Airport.ToPlace(), codes[i].Line);
            }
        }

        // 5) City names: "Bengaluru to Delhi".
        if (routes.Count == 0)
        {
            for (var i = 0; i < facts.Lines.Count; i++)
            {
                var m = CityPair().Match(facts.Lines[i]);
                if (m.Success
                    && directory.FindByCity(m.Groups["a"].Value.Trim()).FirstOrDefault() is { } a
                    && directory.FindByCity(m.Groups["b"].Value.Trim()).FirstOrDefault() is { } b
                    && a.Code != b.Code)
                {
                    AddRoute(a.ToPlace() with { City = m.Groups["a"].Value.Trim() }, b.ToPlace() with { City = m.Groups["b"].Value.Trim() }, i);
                }
            }
        }

        return routes;

        Airport? Valid(string code) => NotAirports.Contains(code) ? null : directory.Find(code);

        void AddRoute(Place from, Place to, int line)
        {
            if (routes.Count == 0 || routes[^1].Item1.Code != from.Code || routes[^1].Item2.Code != to.Code)
            {
                routes.Add((from, to, line));
            }
        }
    }

    /// <summary>Flight numbers with a known airline designator: "6E 2345", "AI-101", "EK512".</summary>
    internal static List<(string? Code, string Number)> FlightNumbers(TextFacts facts)
    {
        var list = new List<(string?, string)>();
        foreach (Match m in FlightNumber().Matches(facts.Text))
        {
            var code = m.Groups[1].Value.ToUpperInvariant();
            if (Airlines.IsKnown(code) && code.Any(char.IsLetter))
            {
                var number = m.Groups[2].Value.TrimStart('0');
                if (number.Length > 0 && !list.Contains((code, number)))
                {
                    list.Add((code, number));
                }
            }
        }

        return list;
    }

    // ---------------- Train / bus ----------------

    private static void ParseGroundTravel(ParsedDocument result, TextFacts facts, TravelMode mode)
    {
        ParseReferences(result, facts);
        var (from, to) = GroundRoute(facts);
        var departs = facts.DateTimeNear(@"depart(?:ure|s)?|dep\b|start(?:s|ing)?\s*(?:date|time)?|boarding\s*(?:time|date)|journey\s*date|date\s*of\s*journey|travel\s*date|reporting\s*time")
            ?? facts.DateTimes.FirstOrDefault(d => d.Time is not null) ?? facts.DateTimes.FirstOrDefault();
        var arrives = facts.DateTimeNear(@"arriv(?:al|es)|arr\b|drop(?:ping)?\s*time|reach(?:es)?");
        if (arrives is not null && departs is not null && arrives.ToDateTime() <= departs.ToDateTime())
        {
            arrives = null;
        }

        if (from is not null)
        {
            result.Segments.Add(new Segment
            {
                Id = Ids.New(),
                Mode = mode,
                From = from,
                To = to,
                Departs = departs is null ? null : new LocalDateTimeZ(departs.ToDateTime(), null),
                Arrives = arrives is null ? null : new LocalDateTimeZ(arrives.ToDateTime(), null),
                ServiceNumber = mode == TravelMode.Train ? TrainNumber(facts) : facts.LabelValue(@"bus\s*(?:no|number)|vehicle\s*(?:no|number)") is { } bus ? First(bus, 20) : null,
                Seat = mode == TravelMode.Train ? Berth(facts) : Seat(facts),
                TravelClass = facts.LabelValue(@"class|coach\s*type|bus\s*type") is { } cls ? First(cls, 30) : null,
            });
        }

        result.Set(F.Segments, from is null ? Confidence.Low : departs is null ? Confidence.Low : departs.Time is null ? Confidence.Medium : Confidence.High);
        if (mode == TravelMode.Bus && facts.LabelValue(@"operator|travels|bus\s*operator") is { } op)
        {
            result.Provider = First(op, 40);
            result.Set(F.Provider, Confidence.Medium);
        }

        if (result.Provider is null)
        {
            ProviderFromBrands(result, facts);
        }

        AddNames(result, facts);
    }

    private static (Place? From, Place? To) GroundRoute(TextFacts facts)
    {
        // "From: Bengaluru (SBC)  To: Chennai Central (MAS)" on one line
        foreach (var line in facts.Lines)
        {
            var m = FromToInline().Match(line);
            if (m.Success)
            {
                return (StationPlace(m.Groups["a"].Value), StationPlace(m.Groups["b"].Value));
            }
        }

        var from = facts.LabelValue(@"^from", @"boarding\s*(?:point|station|at|from)", @"origin", @"source", @"departure\s*(?:station|point|from)", @"pick-?up\s*(?:point|location)");
        var to = facts.LabelValue(@"^to", @"dropping\s*point", @"destination", @"arrival\s*(?:station|point)", @"drop(?:-?off)?\s*(?:point|location|at)", @"reservation\s*upto");
        if (from is not null)
        {
            return (StationPlace(from), to is null ? null : StationPlace(to));
        }

        // "Bengaluru → Chennai"
        foreach (var line in facts.Lines)
        {
            var m = ArrowRoute().Match(line);
            if (m.Success)
            {
                return (StationPlace(m.Groups["a"].Value), StationPlace(m.Groups["b"].Value));
            }
        }

        return (null, null);
    }

    private static Place StationPlace(string raw)
    {
        var text = Regex.Split(raw.Trim(), @"\s{2,}|\s+(?:to|date|dep|arr|time)\b|\t", RegexOptions.IgnoreCase)[0].Trim(' ', ':', '-', ',');
        var code = BracketAnyCode().Match(text);
        var name = code.Success ? text[..code.Index].Trim() : text;
        name = name.Length > 60 ? name[..60] : name;
        var city = name.Split([',', '-'], 2)[0].Trim();
        var airportCity = AirportDirectory.Instance.FindByCity(city).FirstOrDefault();
        return new Place(name.Length > 0 ? name : text, code.Success ? code.Groups[1].Value : null, city, airportCity?.CountryCode, airportCity?.TimeZoneId);
    }

    private static string? TrainNumber(TextFacts facts)
    {
        var m = TrainNo().Match(facts.Text);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? Berth(TextFacts facts)
    {
        var coach = facts.LabelValue(@"coach") is { } c ? TextFacts.FirstCode(c) ?? FirstToken(c) : null;
        var berth = facts.LabelValue(@"berth|seat") is { } b ? FirstToken(b) : null;
        return (coach, berth) switch
        {
            (null, null) => null,
            (null, _) => berth,
            (_, null) => coach,
            _ => $"{coach}/{berth}",
        };
    }

    // ---------------- Hotel ----------------

    private static void ParseHotel(ParsedDocument result, TextFacts facts)
    {
        ParseReferences(result, facts);
        var name = facts.LabelValue(@"hotel\s*name", @"property(?:\s*name)?", @"accommodation", @"hotel") is { } labelled && labelled.Length > 2
            ? First(labelled, 80)
            : facts.Lines.FirstOrDefault(l => HotelWords().IsMatch(l) && l.Length < 80 && !l.Contains(':'));
        var checkIn = facts.DateTimeNear(@"check[\s-]?in|arrival") ?? facts.DateTimes.FirstOrDefault();
        var checkOut = facts.DateTimeNear(@"check[\s-]?out|departure");
        if (checkOut is not null && checkIn is not null && checkOut.ToDateTime() <= checkIn.ToDateTime())
        {
            checkOut = facts.DateTimes.FirstOrDefault(d => d.ToDateTime() > checkIn.ToDateTime());
        }

        var city = FindCity(facts);
        var place = new Place(name ?? "Hotel", null, city?.City, city?.CountryCode, city?.TimeZoneId);
        result.Segments.Add(new Segment
        {
            Id = Ids.New(),
            Mode = TravelMode.Stay,
            From = place,
            Departs = checkIn is null ? null : new LocalDateTimeZ(checkIn.ToDateTime(), place.TimeZoneId),
            Arrives = checkOut is null ? null : new LocalDateTimeZ(checkOut.ToDateTime(), place.TimeZoneId),
        });
        result.Set(F.Segments, checkIn is null ? Confidence.Low : name is null ? Confidence.Medium : Confidence.High);
        result.Provider = name;
        result.Set(F.Provider, name is null ? Confidence.Low : Confidence.Medium);
        if (KnownProviders.Find(facts.Text) is { } brand && name is null)
        {
            result.Provider = brand.Name;
        }

        AddNames(result, facts);
    }

    // ---------------- Event ----------------

    private static void ParseEvent(ParsedDocument result, TextFacts facts)
    {
        ParseReferences(result, facts);
        var venue = facts.LabelValue(@"venue", @"location", @"where", @"stadium", @"theat(?:re|er)", @"address");
        var title = facts.LabelValue(@"event(?:\s*name)?", @"show", @"movie", @"match", @"concert", @"performance")
            ?? facts.Lines.FirstOrDefault(l => l.Length is > 3 and < 80 && !l.Contains(':') && KnownProviders.Find(l) is null && !Regex.IsMatch(l, @"ticket|booking|confirm", RegexOptions.IgnoreCase));
        var when = facts.DateTimeNear(@"date|when|show\s*time|starts?|event\s*time|doors") ?? facts.DateTimes.FirstOrDefault();
        var city = FindCity(facts);
        var place = new Place(venue is null ? title ?? "Event" : First(venue, 80), null, city?.City, city?.CountryCode, city?.TimeZoneId);
        result.Segments.Add(new Segment
        {
            Id = Ids.New(),
            Mode = TravelMode.Event,
            From = place,
            Departs = when is null ? null : new LocalDateTimeZ(when.ToDateTime(), place.TimeZoneId),
            Seat = facts.LabelValue(@"seats?", @"row", @"section", @"block") is { } seat ? First(seat, 30) : null,
        });
        result.Set(F.Segments, when is null ? Confidence.Low : venue is null ? Confidence.Medium : Confidence.High);
        result.Provider = title is null ? null : First(title, 80);
        result.Set(F.Provider, title is null ? Confidence.Low : Confidence.Medium);
        AddNames(result, facts);
    }

    // ---------------- Visa / passport ----------------

    private static void ParseIdentity(ParsedDocument result, TextFacts facts, MrzData? mrz)
    {
        if (mrz is not null)
        {
            result.Identity = new IdentityInfo(mrz.DocumentNumber, mrz.IssuingCountry, null, mrz.ExpiryDate,
                mrz.Kind == MrzKind.Visa ? mrz.IssuingCountry : null);
            result.PassengerNames.Add(mrz.FullName);
            var confidence = mrz.ChecksPassed ? Confidence.High : Confidence.Medium;
            result.Set(F.Identity, confidence);
            result.Set(F.Passengers, confidence);
            if (result.Type == DocumentType.Visa)
            {
                var from = facts.DateTimeNear(@"valid\s*from|date\s*of\s*issue|issue\s*date|issued\s*on");
                result.Identity = result.Identity with { ValidFrom = from?.Date };
            }

            return;
        }

        var number = result.Type == DocumentType.Visa
            ? facts.LabelCode(@"visa\s*(?:no|number)", @"control\s*(?:no|number)", @"visa\s*grant\s*number", @"application\s*(?:id|number)")
            : facts.LabelCode(@"passport\s*(?:no|number)", @"document\s*(?:no|number)");
        var expiry = facts.DateTimeNear(@"expir(?:y|es|ation)|valid\s*(?:until|till|to|thru)|date\s*of\s*expiry|until");
        var validFrom = facts.DateTimeNear(@"valid\s*from|date\s*of\s*issue|issue\s*date|issued\s*on");
        if (expiry is null && facts.DateTimes.Count > 0)
        {
            expiry = facts.DateTimes.MaxBy(d => d.Date); // the latest date on a visa is usually its expiry
        }

        var country = facts.Lines.Select(l => VisaCountry().Match(l)).FirstOrDefault(m => m.Success)?.Groups["c"].Value.Trim();
        result.Identity = new IdentityInfo(number, null, validFrom?.Date, expiry?.Date, string.IsNullOrWhiteSpace(country) ? null : TitleCase(country));
        result.Set(F.Identity, number is null || expiry is null ? Confidence.Low : Confidence.Medium);
        AddNames(result, facts);
    }

    // ---------------- Shared ----------------

    private static void ParseReferences(ParsedDocument result, TextFacts facts, bool flight = false)
    {
        result.Pnr = facts.LabelCode(@"airline\s*pnr", @"pnr", @"record\s*locator", @"reservation\s*code", @"airline\s*(?:booking\s*)?ref(?:erence)?");
        result.BookingReference = facts.LabelCode(
            @"booking\s*(?:id|ref(?:erence)?|code|number|no)",
            @"confirmation\s*(?:code|number|no|#)?",
            @"reservation\s*(?:id|number|no)",
            @"itinerary\s*(?:no|number|id)",
            @"trip\s*id",
            @"order\s*(?:id|number|no)",
            @"ticket\s*(?:no|number|id)");
        if (result.BookingReference == result.Pnr)
        {
            result.BookingReference = null;
        }

        if (flight && result.Pnr is null && result.BookingReference is { Length: 6 } six)
        {
            result.Pnr = six; // a 6-character booking reference on a flight ticket is the airline PNR
            result.BookingReference = null;
        }

        if (result.Pnr is not null)
        {
            result.Set(F.Pnr, Confidence.High);
        }

        if (result.BookingReference is not null)
        {
            result.Set(F.BookingReference, Confidence.High);
        }
    }

    private static void AddNames(ParsedDocument result, TextFacts facts)
    {
        foreach (var name in facts.PersonNames())
        {
            if (!result.PassengerNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.PassengerNames.Add(name);
            }
        }

        if (!result.Confidence.ContainsKey(F.Passengers))
        {
            result.Set(F.Passengers, result.PassengerNames.Count == 0 ? Confidence.Low : Confidence.Medium);
        }
    }

    private static void ProviderFromBrands(ParsedDocument result, TextFacts facts)
    {
        if (KnownProviders.Find(facts.Text) is { } brand)
        {
            result.Provider = brand.Name;
            result.Set(F.Provider, Confidence.Medium);
        }
    }

    private static Airport? FindCity(TextFacts facts)
    {
        var directory = AirportDirectory.Instance;
        foreach (var line in facts.Lines)
        {
            // Address parts without postcodes: "12 MG Road, New Delhi 110001, India" → "MG Road", "New Delhi", "India".
            foreach (var part in Regex.Split(line, @"[,\-|•:]").Select(p => Regex.Replace(p, @"\d+", " ").Trim()))
            {
                if (part.Length is >= 4 and <= 30 && directory.FindByCity(part).FirstOrDefault() is { } airport)
                {
                    return airport;
                }
            }
        }

        return null;
    }

    private static string? Seat(TextFacts facts)
    {
        var m = SeatPattern().Match(facts.Text);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string? TravelClass(TextFacts facts)
    {
        var m = ClassPattern().Match(facts.Text);
        return m.Success ? TitleCase(m.Value) : null;
    }

    private static Place Place(string code) =>
        AirportDirectory.Instance.Find(code)?.ToPlace() ?? new Place(code, code);

    private static string First(string value, int max)
    {
        var s = Regex.Split(value.Trim(), @"\s{2,}|\t")[0].Trim();
        return s.Length > max ? s[..max] : s;
    }

    private static string FirstToken(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim(',', '.') ?? value;

    private static string TitleCase(string s) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    [GeneratedRegex(@"(?<![A-Za-z])([A-Z]{3})\s*(?:-|–|—|→|->|>|to|✈|\u2708)\s*([A-Z]{3})(?![A-Za-z])")]
    private static partial Regex CodePair();

    [GeneratedRegex(@"\(([A-Z]{3})\)")]
    private static partial Regex BracketCode();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{3}(?![A-Za-z0-9])")]
    private static partial Regex StandaloneCode();

    [GeneratedRegex(@"\(([A-Z]{2,5})\)")]
    private static partial Regex BracketAnyCode();

    [GeneratedRegex(@"^([A-Z]{3})(?:\s+\d{1,2}[:.]\d{2}|\s*$)")]
    private static partial Regex LeadingCode();

    [GeneratedRegex(@"^(?<a>[A-Za-z][A-Za-z .]{2,30}?)\s+(?:to|→|->)\s+(?<b>[A-Za-z][A-Za-z .]{2,30}?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CityPair();

    [GeneratedRegex(@"\bfrom\s*[:\-]\s*(?<a>.+?)\s+to\s*[:\-]\s*(?<b>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FromToInline();

    [GeneratedRegex(@"^(?<a>[A-Za-z][A-Za-z .()]{2,40}?)\s*(?:→|->|⇒)\s*(?<b>[A-Za-z][A-Za-z .()]{2,40})$")]
    private static partial Regex ArrowRoute();

    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Z0-9]{2})\s?-?\s?(\d{1,4})(?![\d:/])")]
    private static partial Regex FlightNumber();

    [GeneratedRegex(@"(?:train\s*(?:no\.?|number|#)?(?:\s*/\s*name)?|trn)\s*[:\-/]?\s*(\d{3,6})", RegexOptions.IgnoreCase)]
    private static partial Regex TrainNo();

    [GeneratedRegex(@"seat(?:\s*(?:no\.?|number))?\s*[:\-]?\s*(\d{1,2}[A-K])\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeatPattern();

    [GeneratedRegex(@"\b(?:premium\s+economy|economy|business|first)(?:\s+class)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClassPattern();

    [GeneratedRegex(@"\b(?:hotel|resort|inn|suites?|residency|lodge|hostel|villa|apartments?|palace)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HotelWords();

    [GeneratedRegex(@"(?<c>[A-Za-z][A-Za-z ]{2,30}?)\s+(?:e-?)?visa\b", RegexOptions.IgnoreCase)]
    private static partial Regex VisaCountry();

    [GeneratedRegex(@"\b([01]?\d|2[0-3])[:.][0-5]\d\b")]
    private static partial Regex TimeInText();
}
