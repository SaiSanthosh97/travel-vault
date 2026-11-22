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
        // Read the page as a form first (labels and the values beside or below them), then as text.
        var layout = content.Layout ?? TextLayout.FromGrid(content.Text);
        var reading = FormReader.Read(layout);
        var facts = TextFacts.Analyze(content.Text, reference, reading.Fields);
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
                ParseFlight(result, facts, layout);
                break;
            case DocumentType.Train:
            case DocumentType.Bus:
                ParseGroundTravel(result, facts, layout, type == DocumentType.Train ? TravelMode.Train : TravelMode.Bus);
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
                    ParseFlight(result, facts, layout); // unclassified but has an airport route: treat as flight, low confidence
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

        return TextFacts.FirstTime(value);
    }

    // ---------------- Flight ----------------

    private static void ParseFlight(ParsedDocument result, TextFacts facts, TextLayout? layout = null)
    {
        ParseReferences(result, facts, flight: true);
        var routes = FindAirportRoutes(facts);
        if (routes.Count == 0 && LabelledAirportRoute(facts) is { } labelled)
        {
            routes.Add((labelled.From, labelled.To, -1)); // "From: Dibrugarh  To: Kolkata" (boarding passes, itineraries)
        }

        var flights = FlightNumbers(facts);
        var used = new HashSet<DateTimeMention>();

        // Legs printed as cards (departure left, arrival right): read by position, one segment per card.
        var cards = layout is null ? [] : FlightCards(layout, facts.Reference);
        if (cards.Count > 0 && cards.Count >= routes.Count && cards.All(c => c.Departs is not null))
        {
            routes.Clear();
            foreach (var card in cards)
            {
                result.Segments.Add(new Segment
                {
                    Id = Ids.New(),
                    Mode = TravelMode.Flight,
                    From = card.From,
                    To = card.To,
                    Departs = new LocalDateTimeZ(card.Departs!.ToDateTime(), card.From.TimeZoneId),
                    Arrives = card.Arrives is null ? null : new LocalDateTimeZ(card.Arrives.ToDateTime(), card.To.TimeZoneId),
                    CarrierCode = card.CarrierCode,
                    ServiceNumber = card.CarrierCode is null ? null : $"{card.CarrierCode} {card.Number}",
                    Seat = cards.Count == 1 ? Seat(facts) : null,
                    TravelClass = TravelClass(facts),
                });
            }
        }

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

        // One flight whose date and time are printed under separate labels ("Date 08Apr17 … Departure Time 14:40").
        // The labelled departure time wins over a time that only shares the date's line (often the boarding time).
        if (routes.Count == 1 && result.Segments.Count == 1 && result.Segments[0] is var only
            && DateAndTime(facts, @"depart(?:ure|s)?|dep\b|std", DateLabels, @"departure\s*time|^departs?$|^dep$|^std$|^time$") is { Time: not null } labelledTime
            && (only.Departs is not { } current || !current.HasTime || DateOnly.FromDateTime(current.Local) == labelledTime.Date))
        {
            only.Departs = new LocalDateTimeZ(labelledTime.ToDateTime(), only.From.TimeZoneId);
            if (only.Arrives is null && DateAndTime(facts, ArriveLabels, null, @"arrival\s*time|^arrives?$|^arr$|^sta$", labelledTime) is { Time: not null } arrival
                && arrival.ToDateTime() > labelledTime.ToDateTime())
            {
                only.Arrives = new LocalDateTimeZ(arrival.ToDateTime(), only.To?.TimeZoneId);
            }
        }

        FlightDetails(result, facts);
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

    /// <summary>A route from "From"/"To" labels whose values are airport codes or city names.</summary>
    private static (Place From, Place To)? LabelledAirportRoute(TextFacts facts)
    {
        var from = FirstField(facts, [@"^from$", @"^origin$", @"departure\s*(?:city|airport)"]);
        var to = FirstField(facts, [@"^to$", @"^destination$", @"arrival\s*(?:city|airport)"]);
        return from is not null && to is not null && AirportFrom(from) is { } a && AirportFrom(to) is { } b && a.Code != b.Code
            ? (a.ToPlace(), b.ToPlace())
            : null;

        static Airport? AirportFrom(string value)
        {
            var directory = AirportDirectory.Instance;
            var code = BracketCode().Match(value);
            if (code.Success && directory.Find(code.Groups[1].Value) is { } byCode)
            {
                return byCode;
            }

            var text = First(value, 40).Split([',', '('], 2)[0].Trim();
            return BareCode().IsMatch(text) && text.Length == 3 && !NotAirports.Contains(text) && directory.Find(text) is { } bare
                ? bare
                : directory.FindByCity(text).FirstOrDefault();
        }
    }

    private static void FlightDetails(ParsedDocument result, TextFacts facts)
    {
        result.AddDetail("Status", facts.FieldValue(@"^status$|booking\s*status") is { } status && status.Length <= 20 ? status : null);
        result.AddDetail("Terminal", facts.FieldValue("terminal") is { } terminal ? FirstToken(terminal) : null);
        result.AddDetail("Gate", facts.FieldValue("gate") is { } gate ? FirstToken(gate) : null);
        result.AddDetail("Boarding time", facts.LabelValue(@"boarding\s*time") is { } boarding && TextFacts.FirstTime(boarding) is { } t ? t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : null);
        result.AddDetail("E-ticket", facts.FieldValue(@"e-?ticket\s*(?:no\.?|number)") is { } eticket ? TextFacts.FirstCode(eticket) : null);

        // Booked through a travel site (MakeMyTrip, Goibibo…): who to contact for changes, and when it was booked.
        if (KnownProviders.Find(facts.Text) is { Hint: DocumentType.Other } site)
        {
            result.AddDetail("Booked via", site.Name);
        }

        if (BookedOn().Match(facts.Text) is { Success: true } booked && TextFacts.Analyze(booked.Groups[1].Value, facts.Reference).DateTimes.FirstOrDefault() is { } on)
        {
            result.AddDetail("Booked on", on.Date.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [GeneratedRegex(@"\bbooked\s+on\s*:?\s*([^\n)]{6,30})", RegexOptions.IgnoreCase)]
    private static partial Regex BookedOn();

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

    private const string DepartLabels = @"depart(?:ure|s)?|dep\b|start(?:s|ing)?\s*(?:date|time)?|boarding\s*(?:time|date)|journey\s*date|date\s*of\s*(?:journey|boarding)|travel\s*date|reporting\s*time";
    private const string DateLabels = @"date\s*of\s*(?:journey|boarding|travel)|journey\s*date|travel\s*date|start\s*date|flight\s*date|^date$";
    private const string ArriveLabels = @"arriv(?:al|es)|arr\b|drop(?:ping)?\s*time|reach(?:es)?";

    private static void ParseGroundTravel(ParsedDocument result, TextFacts facts, TextLayout layout, TravelMode mode)
    {
        ParseReferences(result, facts);
        var indianRail = mode == TravelMode.Train && IndianRail().IsMatch(facts.Text);
        var (from, to) = GroundRoute(facts, mode, indianRail);

        // No labelled stations: an app ticket card (stations in two columns, codes underneath, times above).
        var card = from is null && mode == TravelMode.Train ? RailAppCard(layout, facts.Reference) : null;
        if (card is not null)
        {
            (from, to, indianRail) = (card.From, card.To, true);
        }

        var departs = card is not null ? card.Departs : DateAndTime(facts, DepartLabels, DateLabels, @"departure\s*time|boarding\s*time|^departs?$|^dep$");
        var arrives = card is not null ? card.Arrives : DateAndTime(facts, ArriveLabels, null, @"arrival\s*time|drop(?:ping)?\s*time|^arrives?$", departs);
        if (arrives is not null && departs is not null && arrives.ToDateTime() <= departs.ToDateTime())
        {
            arrives = null;
        }

        var passengers = mode == TravelMode.Train ? RailPassengers(facts) : [];
        if (mode == TravelMode.Train && passengers.Count == 0)
        {
            passengers = LayoutRailPassengers(layout);
        }

        var (trainNumber, trainName) = mode == TravelMode.Train ? Train(facts) : (null, null);
        if (card?.TrainNumber is not null && (trainNumber is null || trainNumber == card.TrainNumber))
        {
            (trainNumber, trainName) = (card.TrainNumber, trainName ?? card.TrainName);
        }

        var (railClass, railQuota) = mode == TravelMode.Train ? RailSummary(facts) : (null, null);
        if (from is not null)
        {
            result.Segments.Add(new Segment
            {
                Id = Ids.New(),
                Mode = mode,
                From = from,
                To = to,
                Departs = departs is null ? null : new LocalDateTimeZ(departs.ToDateTime(), from.TimeZoneId),
                Arrives = arrives is null ? null : new LocalDateTimeZ(arrives.ToDateTime(), to?.TimeZoneId ?? from.TimeZoneId),
                ServiceNumber = mode == TravelMode.Train ? trainNumber : facts.LabelValue(@"bus\s*(?:no|number)|vehicle\s*(?:no|number)") is { } bus ? First(bus, 20) : null,
                ServiceName = trainName,
                Seat = mode == TravelMode.Train ? Berth(facts) ?? passengers.Select(p => p.Seat).FirstOrDefault(s => s is not null) : BusSeat(facts),
                TravelClass = facts.LabelValue(@"class|coach\s*type|bus\s*type") is { } cls ? First(cls, 30) : railClass is null ? null : DescribeRailClass(railClass),
            });
        }

        result.Set(F.Segments, from is null ? Confidence.Low : departs is null ? Confidence.Low : departs.Time is null ? Confidence.Medium : Confidence.High);
        if (mode == TravelMode.Bus && facts.LabelValue(@"bus\s*operator|operator|travels") is { } op)
        {
            result.Provider = First(op, 40);
            result.Set(F.Provider, Confidence.Medium);
        }

        if (result.Provider is null)
        {
            ProviderFromBrands(result, facts);
            if (result.Provider is null && indianRail)
            {
                result.Provider = "Indian Railways";
                result.Set(F.Provider, Confidence.Medium);
            }
        }

        foreach (var p in passengers)
        {
            if (!result.PassengerNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            {
                result.PassengerNames.Add(p.Name);
            }
        }

        AddNames(result, facts);
        if (mode == TravelMode.Train)
        {
            RailDetails(result, facts, passengers, from, railQuota);
        }
        else
        {
            BusDetails(result, facts);
        }
    }

    /// <summary>
    /// A date-time from labels: a labelled date-time, or a date and a time printed under separate labels
    /// ("Date of Journey: 18/01/2027" + "Departure Time: 22:00"), or (for departures) the first date with a time.
    /// <paramref name="sameDayAs"/> is the departure when reading an arrival that may print only a time.
    /// </summary>
    private static DateTimeMention? DateAndTime(TextFacts facts, string labels, string? dateLabels, string timeLabels, DateTimeMention? sameDayAs = null)
    {
        // A form field holding both ("Scheduled Departure: 03-Apr-2027 18:00") is exact. A time that merely shares a
        // line with the date can be the boarding time, so an explicitly labelled time ("Departure Time 14:40") beats it.
        var labelled = facts.DateTimeNear(labels);
        if (labelled is { Time: not null, Line: -1 })
        {
            return labelled;
        }

        var time = TimeAfterLabel(facts, timeLabels);
        if (time is null && labelled?.Time is not null)
        {
            return labelled;
        }

        var date = labelled?.Date ?? (dateLabels is null ? null : facts.DateTimeNear(dateLabels)?.Date) ?? sameDayAs?.Date;
        if (date is { } d && time is { } t)
        {
            if (sameDayAs is not null && labelled is null && d.ToDateTime(t) <= sameDayAs.ToDateTime())
            {
                d = d.AddDays(1); // an arrival time earlier than the departure is the next morning
            }

            return new DateTimeMention(-1, -1, d, t, false);
        }

        if (labelled is not null || sameDayAs is not null)
        {
            return labelled;
        }

        return (dateLabels is null ? null : facts.DateTimeNear(dateLabels))
            ?? facts.DateTimes.FirstOrDefault(m => m.Time is not null)
            ?? facts.DateTimes.FirstOrDefault();
    }

    private static (Place? From, Place? To) GroundRoute(TextFacts facts, TravelMode mode, bool indianRail)
    {
        string[] fromLabels = mode == TravelMode.Train
            ? [@"boarding\s*at", @"boarding\s*(?:station|from)", @"booked\s*from", @"^from", @"origin", @"source", @"departure\s*(?:station|from)"]
            : [@"^from", @"boarding\s*(?:point|station|at|from)", @"origin", @"source", @"departure\s*(?:station|point|from)", @"pick-?up\s*(?:point|location)"];
        string[] toLabels = mode == TravelMode.Train
            ? [@"resv\.?\s*up\s*to|reservation\s*up\s*to", @"^to", @"destination", @"arrival\s*station"]
            : [@"^to", @"dropping\s*point", @"destination", @"arrival\s*(?:station|point)", @"drop(?:-?off)?\s*(?:point|location|at)"];

        var from = FirstField(facts, fromLabels);
        var to = FirstField(facts, toLabels);
        if (from is not null)
        {
            return (StationPlace(from, indianRail), to is null ? null : StationPlace(to, indianRail));
        }

        // "From: Bengaluru (SBC)  To: Chennai Central (MAS)" on one line
        foreach (var line in facts.Lines)
        {
            var m = FromToInline().Match(line);
            if (m.Success)
            {
                return (StationPlace(m.Groups["a"].Value, indianRail), StationPlace(m.Groups["b"].Value, indianRail));
            }
        }

        // "Bengaluru → Chennai": a route heading is clearer than a label whose value couldn't be placed
        foreach (var line in facts.Lines)
        {
            var m = ArrowRoute().Match(line);
            if (m.Success)
            {
                return (StationPlace(m.Groups["a"].Value, indianRail), StationPlace(m.Groups["b"].Value, indianRail));
            }
        }

        var fromText = facts.LabelValue(fromLabels);
        var toText = facts.LabelValue(toLabels);
        if (fromText is not null)
        {
            return (StationPlace(fromText, indianRail), toText is null ? null : StationPlace(toText, indianRail));
        }

        // Station codes on their own, as rail apps show them: "NDLS → PNBE"
        foreach (var line in facts.Lines)
        {
            var m = StationCodePair().Match(line);
            if (m.Success && m.Groups[1].Value != m.Groups[2].Value)
            {
                return (StationPlace(m.Groups[1].Value, indianRail), StationPlace(m.Groups[2].Value, indianRail));
            }
        }

        return (null, null);
    }

    private static string? FirstField(TextFacts facts, string[] labels) => labels.Select(facts.FieldValue).FirstOrDefault(v => v is not null);

    private static Place StationPlace(string raw, bool indianRail = false)
    {
        var text = Regex.Split(raw.Trim(), @"\s{2,}|\s+(?:to|date|dep|arr|time)\b|\t", RegexOptions.IgnoreCase)[0].Trim(' ', ':', '-', ',');
        var code = BracketAnyCode().Match(text);
        var name = code.Success ? text[..code.Index].Trim() : text;
        name = name.Length > 60 ? name[..60] : name;
        var stationCode = code.Success ? code.Groups[1].Value : null;
        if (!code.Success && indianRail && BareCode().IsMatch(name))
        {
            stationCode = name; // a bare station code ("NDLS")
        }

        if (indianRail)
        {
            return new Place(name.Length > 0 ? name : text, stationCode, RailCity(name, isCode: !code.Success && stationCode == name), "IN", "Asia/Kolkata");
        }

        var city = name.Split([',', '-'], 2)[0].Trim();
        var airportCity = AirportDirectory.Instance.FindByCity(city).FirstOrDefault();
        return new Place(name.Length > 0 ? name : text, stationCode, city, airportCity?.CountryCode, airportCity?.TimeZoneId);
    }

    /// <summary>"KSR BENGALURU" → "Bengaluru", "PATNA JN" → "Patna", "NEW DELHI" → "New Delhi".</summary>
    private static string? RailCity(string station, bool isCode)
    {
        if (station.Length == 0 || isCode)
        {
            return null;
        }

        var words = station.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries).Where(w => !RailNoise().IsMatch(w)).ToList();
        if (words.Count == 0)
        {
            return TitleCase(station);
        }

        var directory = AirportDirectory.Instance;
        for (var length = words.Count; length >= 1; length--)
        {
            for (var start = 0; start + length <= words.Count; start++)
            {
                var candidate = string.Join(' ', words.Skip(start).Take(length));
                if (candidate.Length >= 4 && directory.FindByCity(candidate).Any())
                {
                    return TitleCase(candidate);
                }
            }
        }

        return TitleCase(string.Join(' ', words));
    }

    /// <summary>Train number and name: "20824/AII PURI SF EXP", "14004 / MLDT EXP", "12627".</summary>
    private static (string? Number, string? Name) Train(TextFacts facts)
    {
        var value = facts.LabelValue(@"train\s*no\.?\s*(?:&|and|/)\s*name", @"train\s*(?:no\.?|number)", @"train\s*name");
        if (value is not null && TrainValue().Match(value) is { Success: true } m)
        {
            var name = First(m.Groups["name"].Value, 40).Trim(' ', '/', '-', ':');
            return (m.Groups["no"].Value, name.Length >= 3 && name.Any(char.IsLetter) ? name : null);
        }

        var any = TrainNo().Match(facts.Text);
        return (any.Success ? any.Groups[1].Value : null, null);
    }

    private static string? Berth(TextFacts facts)
    {
        var coach = facts.LabelValue(@"coach(?:\s*(?:no\.?|number))?") is { } c && !c.Contains("type", StringComparison.OrdinalIgnoreCase) ? TextFacts.FirstCode(c) ?? FirstToken(c) : null;
        var berth = facts.LabelValue(@"berth(?:\s*(?:no\.?|number))?") is { } b ? FirstToken(b) : null;
        return (coach, berth) switch
        {
            (null, null) => null,
            (null, _) => berth,
            (_, null) => coach,
            _ => $"{coach}/{berth}",
        };
    }

    private static string? BusSeat(TextFacts facts) =>
        facts.LabelValue(@"seat\s*(?:no\.?|numbers?)", @"seats?") is { } s ? First(s, 20).Trim(',', ' ') : Seat(facts);

    /// <summary>A passenger row of an Indian Railways ticket: "1  NAME SURNAME  34  M  …  CNF/S4/56/SU".</summary>
    internal sealed record RailPassenger(string Name, string? CurrentStatus, string? Seat, string StatusText);

    internal static List<RailPassenger> RailPassengers(TextFacts facts)
    {
        var list = new List<RailPassenger>();
        foreach (var line in facts.Lines)
        {
            var m = RailPassengerRow().Match(line);
            if (!m.Success || TextFacts.CleanName(m.Groups["name"].Value) is not { } name)
            {
                continue;
            }

            var statuses = RailStatus().Matches(m.Groups["rest"].Value).Select(s => Regex.Replace(s.Value, @"\s+", "")).ToList();
            var current = statuses.LastOrDefault();
            var (seat, text) = DescribeRailStatus(current);
            list.Add(new RailPassenger(name, current, seat, text));
        }

        return list.DistinctBy(p => p.Name.ToUpperInvariant()).ToList();
    }

    /// <summary>"CNF/S4/56/SU" → ("S4/56", "Confirmed · Coach S4 · Berth 56 · Side upper"); "WL/12" → (null, "Waitlist 12").</summary>
    internal static (string? Seat, string Text) DescribeRailStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return (null, "");
        }

        var parts = status.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kind = parts[0].ToUpperInvariant();
        var label = kind switch
        {
            "CNF" => "Confirmed",
            "RAC" => "RAC",
            "CAN" => "Cancelled",
            "NOSB" => "No berth (child)",
            _ when kind.EndsWith("WL", StringComparison.Ordinal) => "Waitlist",
            _ => kind,
        };

        var coach = parts.Skip(1).FirstOrDefault(p => CoachCode().IsMatch(p));
        var berth = parts.Skip(1).FirstOrDefault(p => p.All(char.IsAsciiDigit) && p.Length <= 3);
        var type = parts.Skip(1).Select(BerthType).FirstOrDefault(t => t is not null);
        if (coach is null && berth is not null && label is "Waitlist" or "RAC")
        {
            return (null, $"{label} {berth}"); // "WL/12": the number is the position on the waitlist
        }

        var bits = new List<string> { label };
        if (coach is not null)
        {
            bits.Add("Coach " + coach);
        }

        if (berth is not null)
        {
            bits.Add((coach is not null && coach[0] is 'D' or 'C' or 'E' ? "Seat " : "Berth ") + berth);
        }

        if (type is not null)
        {
            bits.Add(type);
        }

        return (coach is not null && berth is not null ? $"{coach}/{berth}" : coach, string.Join(" · ", bits));

        static string? BerthType(string code) => code.ToUpperInvariant() switch
        {
            "LB" or "LOWER" => "Lower",
            "MB" or "MIDDLE" => "Middle",
            "UB" or "UPPER" => "Upper",
            "SL" => "Side lower",
            "SU" => "Side upper",
            "WS" or "WINDOW" => "Window",
            "MS" => "Middle seat",
            "AS" or "AISLE" => "Aisle",
            _ => null,
        };
    }

    private static void RailDetails(ParsedDocument result, TextFacts facts, List<RailPassenger> passengers, Place? from, string? quotaFromSummary = null)
    {
        if (passengers.FirstOrDefault(p => p.CurrentStatus is not null) is { } first)
        {
            result.AddDetail("Status", first.StatusText.Split(" · ")[0]);
        }

        foreach (var p in passengers.Where(p => p.StatusText.Length > 0))
        {
            result.AddDetail(p.Name, p.StatusText);
        }

        result.AddDetail("Quota", facts.FieldValue("quota") is { } quota ? First(quota, 30) : quotaFromSummary);
        result.AddDetail("Distance", facts.FieldValue("distance") is { } distance ? First(distance, 20) : null);
        if (facts.FieldValue(@"booked\s*from") is { } bookedFrom && from is not null && StationPlace(bookedFrom).Code is { } code && code != from.Code)
        {
            result.AddDetail("Booked from", First(bookedFrom, 40));
        }

        result.AddDetail("Transaction ID", facts.FieldValue(@"transaction\s*id") is { } txn ? FirstToken(txn) : null);
        result.AddDetail("Booked on", facts.FieldValue(@"date\s*(?:&|and)\s*time\s*of\s*booking|booking\s*date|date\s*of\s*booking|ticket\s*printing\s*time|booked\s*on") is { } booked ? First(booked, 30) : null);
    }

    private static void BusDetails(ParsedDocument result, TextFacts facts)
    {
        result.AddDetail("Bus type", facts.LabelValue(@"bus\s*type") is { } type ? First(type, 40) : null);
        result.AddDetail("Boarding point", (facts.FieldValue(@"boarding\s*point") ?? facts.FieldValue(@"^location$")) is { } bp ? First(bp, 60) : null);
        result.AddDetail("Landmark", facts.FieldValue("landmark") is { } lm ? First(lm, 60) : null);
        result.AddDetail("Reporting time", facts.LabelValue(@"reporting\s*time") is { } rt ? First(rt, 20) : null);
        result.AddDetail("Dropping point", facts.LabelValue(@"dropping\s*point") is { } dp ? First(dp, 60) : null);
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

        result.AddDetail("Booked via", KnownProviders.Find(facts.Text) is { } via && via.Name != result.Provider ? via.Name : null);
        result.AddDetail("PIN", facts.FieldValue(@"pin(?:\s*code)?") is { } pin && PinValue().Match(pin) is { Success: true } p ? p.Value : null);
        result.AddDetail("Room", facts.FieldValue(@"room\s*type") is { } room ? First(room, 40) : null);
        if (RoomsAndNights().Match(facts.Text) is { Success: true } rn)
        {
            result.AddDetail("Rooms", rn.Groups["rooms"].Value);
            result.AddDetail("Nights", rn.Groups["nights"].Value);
        }
        else
        {
            result.AddDetail("Rooms", facts.FieldValue(@"^rooms?$") is { } rooms ? FirstToken(rooms) : null);
            result.AddDetail("Nights", facts.FieldValue(@"^nights?$") is { } nights ? FirstToken(nights) : null);
        }

        AddNames(result, facts);
    }

    [GeneratedRegex(@"\b\d{4,6}\b")]
    private static partial Regex PinValue();

    [GeneratedRegex(@"(?<rooms>\d{1,2})\s*rooms?\s*[,·|/]?\s*(?<nights>\d{1,2})\s*nights?", RegexOptions.IgnoreCase)]
    private static partial Regex RoomsAndNights();

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
        result.BookingReference = LongBookingId(facts) ?? facts.LabelCode(
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

    /// <summary>
    /// Travel-site booking IDs are longer than airline or rail codes ("NN2ANCVR28576564002" on MakeMyTrip): letters and
    /// digits, 15–24 characters, right after "Booking ID".
    /// </summary>
    private static string? LongBookingId(TextFacts facts) =>
        facts.LabelValue(@"booking\s*id") is { } value
        && value.Split([' ', ',', '(', ';', '|'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim('.', ':', '#') is { Length: >= 15 and <= 24 } token
        && token.All(char.IsAsciiLetterOrDigit) && token.Any(char.IsAsciiDigit) && token.Any(char.IsAsciiLetter)
            ? token.ToUpperInvariant()
            : null;

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

    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Z0-9]{2})\s{0,2}-?\s?(\d{1,4})(?![\d:/])")]
    private static partial Regex FlightNumber();

    [GeneratedRegex(@"(?:train\s*(?:no\.?|number|#)?(?:\s*(?:/|&|and)\s*name)?|trn)\s*[:\-/]?\s*(\d{4,5})(?!\d)", RegexOptions.IgnoreCase)]
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

    [GeneratedRegex(@"\bIRCTC\b|indian\s*railways?|rail\s*one|\bERS\b|electronic\s*reservation\s*slip|resv\.?\s*up\s*to|\bquota\b|\b(?:CNF|RAC|[A-Z]{0,2}WL)/", RegexOptions.IgnoreCase)]
    private static partial Regex IndianRail();

    [GeneratedRegex(@"^\s*([A-Z]{2,5})\s*(?:→|->|⇒|—|–|-|to)\s*([A-Z]{2,5})\s*$")]
    private static partial Regex StationCodePair();

    [GeneratedRegex(@"^[A-Z]{2,5}$")]
    private static partial Regex BareCode();

    /// <summary>Words in Indian station names that aren't part of the city: "PATNA JN", "KSR BENGALURU", "PUNE CANTT".</summary>
    [GeneratedRegex(@"^(?:JN|JN\.|JNC|JCT|JUNCTION|CANTT?|CENTRAL|CNTL|TERMINUS|TERMINAL|TERM|RLY|STN|STATION|KSR|MGR|CSMT|CST|LTT|HALT|CITY|ROAD|RD|SF|EXP)$", RegexOptions.IgnoreCase)]
    private static partial Regex RailNoise();

    [GeneratedRegex(@"^\s*(?<no>\d{4,5})\b\s*[/\-:]?\s*(?<name>.*)$")]
    private static partial Regex TrainValue();

    [GeneratedRegex(@"^\s*(?:#?\d{1,2}[.)]?\s+)?(?<name>[A-Za-z][A-Za-z .']{2,50}?)\s+(?<age>\d{1,3})\s*(?:yrs?|years?)?\s+(?<g>M|F|T|Male|Female|Transgender)\b(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex RailPassengerRow();

    [GeneratedRegex(@"(?<![A-Za-z])(?:CNF|RAC|CAN|NOSB|[A-Z]{0,4}WL)(?:\s*/\s*[A-Z0-9]+)*(?![A-Za-z])")]
    private static partial Regex RailStatus();

    [GeneratedRegex(@"^[A-Z]{1,2}\d{1,2}$")]
    private static partial Regex CoachCode();
}
