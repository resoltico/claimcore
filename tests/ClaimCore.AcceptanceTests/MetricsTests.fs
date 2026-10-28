module ClaimCore.AcceptanceTests.MetricsTests

open System
open Expecto
open ClaimCore.TestSupport

let private expectedKeys =
    function
    | "claimcore.requests" -> [ "endpoint"; "result" ]
    | "claimcore.request.duration" -> [ "endpoint" ]
    | "claimcore.sessions.ended" -> [ "reason" ]
    | "claimcore.sessions.started"
    | "claimcore.ledger.append.duration"
    | "claimcore.ledger.head_wait.duration" -> []
    | name -> failtestf "Unexpected ClaimCore instrument: %s" name

let private allowedValues =
    function
    | "endpoint" -> "unknown" :: Inventory.tokens "endpoints"
    | "result" -> [ "succeeded"; "refused"; "failed"; "host_failure" ]
    | "reason" -> [ "logout"; "expired"; "invalid" ]
    | _ -> failtest "A tag key must be declared"

let private check (observation: MetricObservations.Observation) =
    Expect.isTrue observation.SharedMeter "Every instrument uses the one application meter"

    Expect.isTrue
        (Double.IsFinite observation.Value && observation.Value >= 0.)
        "Measurements are finite and nonnegative"

    let keys = observation.Tags |> List.map fst

    Expect.equal
        (List.sort keys)
        (expectedKeys observation.Instrument |> List.sort)
        "Exactly the declared tag keys occur, without duplicates"

    for key, value in observation.Tags do
        Expect.contains
            (allowedValues key)
            value
            "No identity, operation, case or business value can enter a tag"

    let unitName =
        if observation.Instrument.EndsWith(".duration", StringComparison.Ordinal) then
            "s"
        else
            ""

    Expect.equal observation.UnitName unitName "Duration instruments publish seconds"

[<Tests>]
let tests =
    testCaseAsync
        "native acceptance emits bounded request ledger and session metrics"
        (async {
            let! observations = MetricsAcceptance.run () |> Async.AwaitTask
            let names = observations |> List.map _.Instrument |> Set.ofList

            Expect.equal
                names
                (set
                    [
                        "claimcore.requests"
                        "claimcore.request.duration"
                        "claimcore.ledger.append.duration"
                        "claimcore.ledger.head_wait.duration"
                        "claimcore.sessions.started"
                        "claimcore.sessions.ended"
                    ])
                "Every declared instrument emits during a real PostgreSQL and HTTP acceptance flow"

            observations |> List.iter check

            let values key instrument =
                observations
                |> List.filter (fun observation -> observation.Instrument = instrument)
                |> List.collect _.Tags
                |> List.choose (fun (name, value) -> if name = key then Some value else None)
                |> Set.ofList

            Expect.equal
                (values "reason" "claimcore.sessions.ended")
                (set [ "logout"; "expired"; "invalid" ])
                "All committed session endings are exercised"

            Expect.equal
                (values "result" "claimcore.requests")
                (set [ "succeeded"; "refused"; "failed"; "host_failure" ])
                "Every bounded request result is exercised"
        })
