module ClaimCore.TestSupport.MetricObservations

open System
open System.Collections.Concurrent
open System.Diagnostics.Metrics
open ClaimCore.Application

[<NoComparison>]
type Observation =
    {
        Instrument: string
        UnitName: string
        Value: double
        Tags: (string * string) list
        SharedMeter: bool
    }

/// Capture values, not assertions, in callbacks: telemetry must not change application outcomes.
type Capture() =
    let observations = ConcurrentQueue<Observation>()
    let listener = new MeterListener()

    let record
        (instrument: Instrument)
        value
        (tags: ReadOnlySpan<Collections.Generic.KeyValuePair<string, obj | null>>)
        =
        observations.Enqueue
            {
                Instrument = instrument.Name
                UnitName = Option.ofObj instrument.Unit |> Option.defaultValue ""
                Value = value
                Tags =
                    tags.ToArray()
                    |> Array.map (fun tag -> tag.Key, string tag.Value)
                    |> Array.toList
                SharedMeter = obj.ReferenceEquals(instrument.Meter, Service.metrics)
            }

    do
        listener.InstrumentPublished <-
            Action<Instrument, MeterListener>(fun instrument active ->
                if instrument.Meter.Name = "ClaimCore" then
                    active.EnableMeasurementEvents instrument)

        listener.SetMeasurementEventCallback<int64>(
            MeasurementCallback<int64>(fun instrument value tags _ ->
                record instrument (double value) tags)
        )

        listener.SetMeasurementEventCallback<double>(
            MeasurementCallback<double>(fun instrument value tags _ -> record instrument value tags)
        )

        listener.Start()

    member _.Values = observations.ToArray() |> Array.toList

    interface IDisposable with
        member _.Dispose() = listener.Dispose()
