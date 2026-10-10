module internal ClaimCore.IntegrationTests.FixtureCleanup

open System
open System.IO
open Npgsql

let private failureKind (error: exn) =
    match error with
    | :? OperationCanceledException -> "cancelled"
    | :? TimeoutException -> "timeout"
    | :? PostgresException -> "postgres-refusal"
    | :? NpgsqlException -> "transport-unavailable"
    | :? IOException -> "io-unavailable"
    | _ -> "unknown"

let run cleanup body =
    let mutable bodyError: exn option = None

    try
        try
            body ()
        with error ->
            bodyError <- Some error
            reraise ()
    finally
        try
            cleanup ()
        with error ->
            match bodyError with
            | None -> raise error
            | Some original ->
                try
                    original.Data["FixtureCleanupFailure"] <- failureKind error
                    original.Data["FixtureCleanupSettlement"] <- "unknown"
                with _ ->
                    try
                        Console.Error.WriteLine(
                            "fixture-cleanup-settlement=unknown kind="
                            + failureKind error
                            + " diagnostic-retention=unavailable"
                        )
                    with _ ->
                        ()

let physical stop identifiers directory body =
    let mutable bodyPassed = false

    let cleanup () =
        let mutable failure: exn option = None

        for identifier in identifiers () do
            if identifier <> "" then
                try
                    if stop identifier <> 0 then
                        invalidOp "Owned physical restore cleanup is unsettled."
                with error ->
                    if failure.IsNone then
                        failure <- Some error

        match failure with
        | Some error -> raise error
        | None when bodyPassed -> Directory.Delete(directory, true)
        | None -> ()

    run cleanup (fun () ->
        let result = body ()
        bodyPassed <- true
        result)
