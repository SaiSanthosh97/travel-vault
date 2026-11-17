using TravelVault.Core.Documents;

namespace TravelVault.Core.Parsing;

/// <summary>
/// Decides what kind of document a text is. Strong structural signals win outright (a boarding-pass barcode,
/// a passport/visa MRZ); otherwise each type scores weighted keywords and the highest score wins.
/// Keyword scoring is transparent and easy to tune with new samples, unlike a trained model.
/// </summary>
public static class DocumentClassifier
{
    private static readonly (DocumentType Type, string Pattern, int Weight)[] Signals =
    [
        (DocumentType.BoardingPass, @"boarding\s*pass", 6),
        (DocumentType.BoardingPass, @"\bgate\b", 2),
        (DocumentType.BoardingPass, @"boarding\s*time", 3),
        (DocumentType.BoardingPass, @"\bzone\b|\bgroup\s*\d", 1),

        (DocumentType.Flight, @"\bflight\b", 3),
        (DocumentType.Flight, @"\bairlines?\b|\bairways\b", 3),
        (DocumentType.Flight, @"e-?ticket|electronic ticket|itinerary receipt", 2),
        (DocumentType.Flight, @"\bterminal\b", 2),
        (DocumentType.Flight, @"\bbaggage\b|check-?in baggage|cabin bag", 2),
        (DocumentType.Flight, @"\bairport\b", 2),
        (DocumentType.Flight, @"\bpnr\b", 1),

        (DocumentType.Train, @"\btrain\b", 4),
        (DocumentType.Train, @"\bcoach\b|\bberth\b|\bcarriage\b", 2),
        (DocumentType.Train, @"\bplatform\b", 2),
        (DocumentType.Train, @"\brailways?\b|\brail\b", 3),
        (DocumentType.Train, @"\bstation\b", 1),
        (DocumentType.Train, @"\b(?:sleeper|3a|2a|1a|cc|2s|sl)\b\s*(?:class)?", 1),

        (DocumentType.Bus, @"\bbus\b", 4),
        (DocumentType.Bus, @"boarding\s*point|dropping\s*point|pick-?up\s*point", 4),
        (DocumentType.Bus, @"\boperator\b|\btravels\b", 2),
        (DocumentType.Bus, @"\bseater\b|\bsleeper\s*bus\b|a/c\s*(?:sleeper|seater)", 2),

        (DocumentType.Hotel, @"check[\s-]?in", 3),
        (DocumentType.Hotel, @"check[\s-]?out", 3),
        (DocumentType.Hotel, @"\bhotel\b|\bresort\b|\bhostel\b|\bsuites?\b|\binn\b", 3),
        (DocumentType.Hotel, @"\broom\b|\bnights?\b|\bguests?\b", 2),
        (DocumentType.Hotel, @"\bstay\b|\baccommodation\b|\bproperty\b", 2),

        (DocumentType.Event, @"\bvenue\b", 4),
        (DocumentType.Event, @"\bevent\b|\bconcert\b|\bshow\b|\bmatch\b|\bfestival\b|\bmovie\b|\bscreen\b", 3),
        (DocumentType.Event, @"\badmit\b|\bentry\b|\bgates?\s*open", 2),
        (DocumentType.Event, @"\brow\b|\bsection\b|\bblock\b|\bstand\b", 1),

        (DocumentType.Visa, @"\bvisa\b", 6),
        (DocumentType.Visa, @"\bentries\b|\bentry\s*type\b|duration of stay|\bmultiple\b", 2),

        (DocumentType.Passport, @"\bpassport\b", 4),
        (DocumentType.Passport, @"nationality|date of birth|place of birth", 2),
    ];

    public static (DocumentType Type, Confidence Confidence) Classify(TextFacts facts, IReadOnlyList<Barcode> barcodes, MrzData? mrz)
    {
        if (barcodes.Any(b => BcbpDecoder.LooksLikeBcbp(b.Value)))
        {
            return (DocumentType.BoardingPass, Confidence.High);
        }

        if (mrz is not null)
        {
            return (mrz.Kind == MrzKind.Passport ? DocumentType.Passport : DocumentType.Visa, Confidence.High);
        }

        var scores = Signals
            .GroupBy(s => s.Type)
            .Select(g => (Type: g.Key, Score: g.Sum(s => facts.Has(s.Pattern) ? s.Weight : 0)))
            .OrderByDescending(x => x.Score)
            .ToList();

        // A booking brand is a strong hint (redBus → bus, Booking.com → hotel).
        if (KnownProviders.Find(facts.Text) is { Hint: not DocumentType.Other } brand)
        {
            var index = scores.FindIndex(s => s.Type == brand.Hint);
            scores[index] = (brand.Hint, scores[index].Score + 4);
            scores = [.. scores.OrderByDescending(x => x.Score)];
        }

        var best = scores[0];
        var second = scores.Count > 1 ? scores[1].Score : 0;
        if (best.Score < 3)
        {
            return (DocumentType.Other, Confidence.Low);
        }

        var confidence = best.Score >= 8 && best.Score >= second * 2 ? Confidence.High
            : best.Score > second ? Confidence.Medium
            : Confidence.Low;
        return (best.Type, confidence);
    }
}
