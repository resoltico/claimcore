namespace ClaimCore.Postgres

open System
open System.Globalization
open System.IO

/// Date parameters cross the driver as finite integer day offsets, not DateOnly extrema.
/// This avoids Npgsql's process-wide infinity conversion without changing global switches.
module internal ScalarEncoding =
    let private epoch = DateOnly(2000, 1, 1).DayNumber
    let private epochSql = "DATE '2000-01-01'"
    let private invalidDays = Int32.MinValue

    let dateDays (value: string) =
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber
        - epoch

    let dateText (days: int) =
        let day = int64 epoch + int64 days

        if days = invalidDays || day < 0L || day > int64 DateOnly.MaxValue.DayNumber then
            raise (InvalidDataException("A stored date is outside the finite supported range."))

        DateOnly.FromDayNumber(int day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    /// Identifier arguments are fixed repository-owned SQL names, never request data.
    let dateColumn (name: string) =
        "CASE WHEN NOT isfinite(c."
        + name
        + ") THEN CAST(-2147483648 AS integer) ELSE c."
        + name
        + " - "
        + epochSql
        + " END AS "
        + name

    let dateParameter (name: string) = "(" + epochSql + " + @" + name + ")"
