namespace ClaimCore.RecordFormat

open System

/// PostgreSQL and the permanent ledger represent instants at whole-microsecond precision.
module internal RecordedInstant =
    let hasMicrosecondPrecision (value: DateTimeOffset) =
        value.UtcTicks % TimeSpan.TicksPerMicrosecond = 0L
