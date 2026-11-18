using System.Globalization;

namespace TravelVault.Core.Documents;

/// <summary>Generates readable titles: "Flight Bengaluru → Delhi", "Hotel · Taj Palace", "Visa · Schengen".</summary>
public static class DocumentTitles
{
    public static string For(TravelDocument d)
    {
        var first = d.Segments.FirstOrDefault();
        var last = d.Segments.LastOrDefault();
        var route = first is null
            ? null
            : first.To is null
                ? first.From.Display
                : $"{first.From.City ?? first.From.Name} → {(last?.To ?? first.To).City ?? (last?.To ?? first.To).Name}";

        return d.Type switch
        {
            DocumentType.Flight => Join("Flight", route ?? d.Provider),
            DocumentType.Train => Join("Train", route ?? d.Provider),
            DocumentType.Bus => Join("Bus", route ?? d.Provider),
            DocumentType.Hotel => Join("Hotel", d.Provider ?? route),
            DocumentType.Event => Join("Event", d.Provider ?? route),
            DocumentType.Visa => Join("Visa", d.Identity?.VisaCountry ?? d.Identity?.IssuingCountry),
            DocumentType.Passport => Join("Passport", d.Identity?.IssuingCountry),
            DocumentType.BoardingPass => Join("Boarding pass", route),
            _ => Join("Document", d.Provider ?? route),
        };

        static string Join(string kind, string? detail) => string.IsNullOrWhiteSpace(detail) ? kind : $"{kind} · {detail}";
    }

    /// <summary>Applies the generated title unless the user edited it.</summary>
    public static void Refresh(TravelDocument d)
    {
        if (!d.IsUserEdited(TravelDocument.Fields.Title))
        {
            d.Title = For(d);
        }
    }
}

/// <summary>Normalises booking codes so "x7kq-2p", " X7KQ2P " and "X7KQ2P" compare equal.</summary>
public static class BookingCodes
{
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var cleaned = new string(code.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        return cleaned.Length is >= 5 and <= 20 ? cleaned : null;
    }

    public static bool Same(string? a, string? b) => Normalize(a) is { } x && x == Normalize(b);
}

/// <summary>
/// Merges a duplicate (source) into the document the user keeps (target):
/// files become versions (newest import is primary), empty fields are filled, more confident values win,
/// user edits on the target are kept, and the source becomes a tombstone pointing at the target.
/// </summary>
public static class DocumentMerger
{
    public static void Merge(TravelDocument target, TravelDocument source, DateTimeOffset now)
    {
        if (target.Id == source.Id || target.FamilyId != source.FamilyId)
        {
            throw new InvalidOperationException("Can only merge two different documents of the same family.");
        }

        var newest = source.Files.OrderByDescending(f => f.AddedAt).FirstOrDefault();
        var sourceIsNewer = newest is not null && target.Files.All(f => f.AddedAt <= newest.AddedAt);
        foreach (var file in source.Files.OrderBy(f => f.AddedAt))
        {
            var copy = new FileVersion
            {
                Id = file.Id,
                Sha256 = file.Sha256,
                Size = file.Size,
                MediaType = file.MediaType,
                OriginalName = file.OriginalName,
                AddedAt = file.AddedAt,
                AddedBy = file.AddedBy,
            };
            target.AddFile(copy, makePrimary: sourceIsNewer && file == newest);
        }

        // An updated e-ticket (newer) has the current times/seats: take its segments unless the user edited them.
        Take(TravelDocument.Fields.Segments, () => source.Segments.Count > 0 && (sourceIsNewer || target.Segments.Count == 0), () => target.Segments = source.Segments);
        Take(TravelDocument.Fields.Provider, () => Better(source.Provider, target.Provider, TravelDocument.Fields.Provider), () => target.Provider = source.Provider);
        Take(TravelDocument.Fields.Pnr, () => Better(source.Pnr, target.Pnr, TravelDocument.Fields.Pnr), () => target.Pnr = source.Pnr);
        Take(TravelDocument.Fields.BookingReference, () => Better(source.BookingReference, target.BookingReference, TravelDocument.Fields.BookingReference), () => target.BookingReference = source.BookingReference);
        Take(TravelDocument.Fields.Identity, () => target.Identity is null && source.Identity is not null, () => target.Identity = source.Identity);

        if (!target.IsUserEdited(TravelDocument.Fields.Passengers))
        {
            target.Passengers = target.Passengers.Concat(source.Passengers).DistinctBy(p => p.TravelerId).ToList();
        }

        // Printed extras: the newer ticket wins per label (a waitlisted ticket that got confirmed); the rest are kept.
        var newer = sourceIsNewer ? source.Details : target.Details;
        var older = sourceIsNewer ? target.Details : source.Details;
        target.Details = newer.Concat(older.Where(o => newer.All(n => n.Label != o.Label))).ToList();
        target.Notes = string.Join("\n", new[] { target.Notes, source.Notes }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
        target.TripId ??= source.TripId;
        DocumentTitles.Refresh(target);

        source.Delete(now);
        source.MergedInto = target.Id;

        void Take(string field, Func<bool> condition, Action apply)
        {
            if (!target.IsUserEdited(field) && condition())
            {
                apply();
                if (source.FieldConfidence.TryGetValue(field, out var c))
                {
                    target.FieldConfidence[field] = c;
                }
            }
        }

        bool Better(string? incoming, string? existing, string field) =>
            !string.IsNullOrWhiteSpace(incoming)
            && (string.IsNullOrWhiteSpace(existing) || source.ConfidenceOf(field) > target.ConfidenceOf(field));
    }
}

/// <summary>
/// Settles a sync conflict between the local copy and an incoming copy of the same document.
/// Whole-record last-writer-wins by version, with one protection: a field the user edited locally is not
/// replaced by an automatically-produced value from the other phone (e.g. a merge that ran there).
/// </summary>
public static class DocumentConflicts
{
    public static TravelDocument Resolve(TravelDocument local, TravelDocument incoming)
    {
        if (incoming.Version <= local.Version)
        {
            return local;
        }

        foreach (var (field, confidence) in local.FieldConfidence)
        {
            if (confidence != Confidence.UserEdited || incoming.IsUserEdited(field))
            {
                continue;
            }

            switch (field)
            {
                case TravelDocument.Fields.Title: incoming.Title = local.Title; break;
                case TravelDocument.Fields.Provider: incoming.Provider = local.Provider; break;
                case TravelDocument.Fields.Pnr: incoming.Pnr = local.Pnr; break;
                case TravelDocument.Fields.BookingReference: incoming.BookingReference = local.BookingReference; break;
                case TravelDocument.Fields.Passengers: incoming.Passengers = local.Passengers; break;
                case TravelDocument.Fields.Segments: incoming.Segments = local.Segments; break;
                case TravelDocument.Fields.Identity: incoming.Identity = local.Identity; break;
                case TravelDocument.Fields.Type: incoming.Type = local.Type; break;
                default: continue;
            }

            incoming.FieldConfidence[field] = Confidence.UserEdited;
        }

        return incoming;
    }
}

public static class DateFormats
{
    public static string Short(LocalDateTimeZ? t) =>
        t is { } v ? v.Local.ToString(v.HasTime ? "d MMM yyyy, HH:mm" : "d MMM yyyy", CultureInfo.InvariantCulture) : "";
}
