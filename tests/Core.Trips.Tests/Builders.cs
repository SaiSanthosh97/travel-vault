using TravelVault.Core.Common;
using TravelVault.Core.Documents;

namespace TravelVault.Core.Trips.Tests;

/// <summary>Small helpers to build test documents readably.</summary>
internal static class Build
{
    public static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private const string Zone = "Asia/Kolkata";

    public static Place City(string code, string city) => new(city + " airport", code, city, "IN", Zone);

    public static TravelDocument Flight(string from, string to, DateTime departs, string pnr = "X7KQ2P", string flight = "6E 2345", params string[] travelers)
    {
        var doc = TravelDocument.Create("fam", DocumentType.Flight, "me", Now);
        doc.Pnr = pnr;
        doc.Segments.Add(new Segment
        {
            Id = Ids.New(),
            Mode = TravelMode.Flight,
            From = City(from, Cities[from]),
            To = City(to, Cities[to]),
            Departs = new LocalDateTimeZ(departs, Zone),
            Arrives = new LocalDateTimeZ(departs.AddHours(2.5), Zone),
            ServiceNumber = flight,
        });
        doc.SetPassengers((travelers.Length == 0 ? ["rahul"] : travelers).Select(t => new DocumentPassenger(t, null, Confidence.High)), "rahul");
        return doc;
    }

    public static TravelDocument Hotel(string city, DateTime checkIn, DateTime checkOut, params string[] travelers)
    {
        var doc = TravelDocument.Create("fam", DocumentType.Hotel, "me", Now);
        doc.Segments.Add(new Segment
        {
            Id = Ids.New(),
            Mode = TravelMode.Stay,
            From = new Place("Grand Hotel", null, city, "IN", Zone),
            Departs = new LocalDateTimeZ(checkIn, Zone),
            Arrives = new LocalDateTimeZ(checkOut, Zone),
        });
        doc.SetPassengers((travelers.Length == 0 ? ["rahul"] : travelers).Select(t => new DocumentPassenger(t, null, Confidence.High)), "rahul");
        return doc;
    }

    public static TravelDocument BoardingPass(string from, string to, DateTime departs, string pnr, string flight, string traveler)
    {
        var doc = Flight(from, to, departs, pnr, flight, traveler);
        doc.Type = DocumentType.BoardingPass;
        return doc;
    }

    private static readonly Dictionary<string, string> Cities = new()
    {
        ["BLR"] = "Bangalore", ["DEL"] = "New Delhi", ["BOM"] = "Mumbai", ["GOI"] = "Goa", ["MAA"] = "Chennai",
    };
}
