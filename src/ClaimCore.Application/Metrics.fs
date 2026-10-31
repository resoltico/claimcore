namespace ClaimCore.Application


open System
open System.Diagnostics.Metrics

/// The application meter is shared with hosting instruments; no identity or business data is tagged.
module internal ApplicationMetrics =
    let meter = new Meter("ClaimCore")

    let private append =
        meter.CreateHistogram<double>("claimcore.ledger.append.duration", "s")

    let private headWait =
        meter.CreateHistogram<double>("claimcore.ledger.head_wait.duration", "s")

    let private record (instrument: Histogram<double>) (elapsed: TimeSpan) =
        // An in-process diagnostic listener must not change transaction or delivery outcomes.
        try
            instrument.Record elapsed.TotalSeconds
        with _ ->
            ()

    let appended elapsed = record append elapsed
    let waited elapsed = record headWait elapsed
