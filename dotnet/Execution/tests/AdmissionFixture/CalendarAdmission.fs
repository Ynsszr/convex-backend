namespace RuntimeFixture

open System
open System.Globalization
open System.Text.Json
open Convex.FSharp

/// Each getter is independent so a refused calendar operation cannot prevent
/// a separate, deterministic registration from being selected.
type NativeCalendarFixtures =
    static member parseDate = Convex.query(fun _ (_:unit) -> async { return DateTime.Parse("12:34",CultureInfo.InvariantCulture).Day })
    static member parseExactDate = Convex.query(fun _ (_:unit) -> async { return DateTime.ParseExact("12:34","HH:mm",CultureInfo.InvariantCulture).Day })
    static member tryParseDate = Convex.query(fun _ (_:unit) -> async { return DateTime.TryParse("12:34",CultureInfo.InvariantCulture,DateTimeStyles.None) |> fst })
    static member parseOffset = Convex.query(fun _ (_:unit) -> async { return DateTimeOffset.Parse("12:34",CultureInfo.InvariantCulture).Day })
    static member parseExactOffset = Convex.query(fun _ (_:unit) -> async { return DateTimeOffset.ParseExact("12:34","HH:mm",CultureInfo.InvariantCulture).Day })
    static member tryParseOffset = Convex.query(fun _ (_:unit) -> async { return DateTimeOffset.TryParse("12:34",CultureInfo.InvariantCulture,DateTimeStyles.None) |> fst })
    static member localDate = Convex.query(fun _ (_:unit) -> async { return DateTime.UnixEpoch.ToLocalTime().Hour })
    static member universalDate = Convex.query(fun _ (_:unit) -> async { return DateTime(2024,1,2).ToUniversalTime().Hour })
    static member binaryDate = Convex.query(fun _ (_:unit) -> async { return DateTime(2024,1,2,0,0,0,DateTimeKind.Local).ToBinary() })
    static member fromBinaryDate = Convex.query(fun _ (_:unit) -> async { return DateTime.FromBinary(0L).Day })
    static member fileDate = Convex.query(fun _ (_:unit) -> async { return DateTime.FromFileTime(0L).Hour })
    static member toFileDate = Convex.query(fun _ (_:unit) -> async { return DateTime(2024,1,2).ToFileTime() })
    static member toUtcFileDate = Convex.query(fun _ (_:unit) -> async { return DateTime(2024,1,2,0,0,0,DateTimeKind.Local).ToFileTimeUtc() })
    static member inferredOffset = Convex.query(fun _ (_:unit) -> async { return DateTimeOffset(DateTime(2024,1,2)).Hour })
    static member validatedLocalOffset = Convex.query(fun _ (_:unit) -> async { return DateTimeOffset(DateTime(2024,1,2,0,0,0,DateTimeKind.Local),TimeSpan.Zero).Hour })
    static member localOffset = Convex.query(fun _ (_:unit) -> async { return DateTimeOffset.UnixEpoch.ToLocalTime().Hour })
    static member offsetLocalDate = Convex.query(fun _ (_:unit) -> async { return DateTimeOffset.UnixEpoch.LocalDateTime.Hour })
    static member offsetFileDate = Convex.query(fun _ (_:unit) -> async { return DateTimeOffset.FromFileTime(0L).Hour })
    static member formatDate = Convex.query(fun _ (_:unit) -> async { return DateTime(2024,1,2,0,0,0,DateTimeKind.Local).ToString("O",CultureInfo.InvariantCulture) })
    static member convertDate = Convex.query(fun _ (_:unit) -> async { return Convert.ToDateTime("12:34",CultureInfo.InvariantCulture).Day })
    static member changeTypeDate = Convex.query(fun _ (_:unit) -> async { return (Convert.ChangeType("12:34",typeof<DateTime>,CultureInfo.InvariantCulture) :?> DateTime).Day })
    static member jsonOffset = Convex.query(fun _ (_:unit) -> async {
        use document=JsonDocument.Parse("\"2024-01-02T12:34:00\"")
        return document.RootElement.GetDateTimeOffset().Hour
    })
    static member jsonDate = Convex.query(fun _ (_:unit) -> async {
        use document=JsonDocument.Parse("\"2024-01-02T12:34:00+03:00\"")
        return document.RootElement.GetDateTime().Hour
    })
    static member jsonFormatDate = Convex.query(fun _ (_:unit) -> async {
        use output=new System.IO.MemoryStream()
        use writer=new Utf8JsonWriter(output)
        writer.WriteStringValue(DateTime(2024,1,2,0,0,0,DateTimeKind.Local))
        writer.Flush()
        return output.Length
    })
    static member currentDateFormat = Convex.query(fun _ (_:unit) -> async { return DateTimeFormatInfo.CurrentInfo.ShortDatePattern })
    static member currentNumberFormat = Convex.query(fun _ (_:unit) -> async { return NumberFormatInfo.CurrentInfo.NumberDecimalSeparator })
    static member mutationParse = Convex.mutation(fun _ (_:unit) -> async { return DateTime.Parse("12:34",CultureInfo.InvariantCulture).Day })
    static member importParse =
        let date=DateTime.Parse("12:34",CultureInfo.InvariantCulture)
        Convex.query(fun _ (_:unit) -> async { return date.Day })

    static member calendarArithmetic = Convex.query(fun _ (_:unit) -> async {
        let date=DateTime(2024,2,28,23,0,0,DateTimeKind.Utc).AddDays(1.).AddHours(1.)
        return date.Month*100+date.Day
    })
    static member offsetArithmetic = Convex.query(fun _ (_:unit) -> async {
        let date=DateTimeOffset(2024,2,28,23,0,0,TimeSpan.FromHours(3.)).AddDays(1.).AddHours(1.)
        let utc=date.ToOffset(TimeSpan.Zero)
        return utc.Month*100+utc.Day
    })
    static member explicitTickOffset = Convex.query(fun _ (_:unit) -> async {
        let date=DateTimeOffset(DateTime(2024,1,2).Ticks,TimeSpan.FromHours(3.))
        return date.UtcDateTime.Day
    })
    static member unixArithmetic = Convex.query(fun _ (_:unit) -> async {
        return DateTimeOffset.FromUnixTimeSeconds(0L).AddDays(2.).ToUnixTimeSeconds() |> int
    })
    static member actionParsing = Convex.action(fun _ (_:unit) -> async {
        return DateTime.Parse("12:34",CultureInfo.InvariantCulture).Hour+
               DateTimeOffset.Parse("12:34",CultureInfo.InvariantCulture).Hour
    })
