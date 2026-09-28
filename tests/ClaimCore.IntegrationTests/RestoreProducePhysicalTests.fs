module ClaimCore.IntegrationTests.RestoreProducePhysicalTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestorePhysicalCopyFixture
open ClaimCore.IntegrationTests.RestorePhysicalProcess
open ClaimCore.IntegrationTests.RestoreProducePhysicalChecks

let private missingWalRefused scratch =
    let wal = Path.Combine(scratch, "primary", "pg_wal")

    let segments =
        Directory.EnumerateFiles(wal)
        |> Seq.filter (fun path ->
            Path.GetFileName(path)
            |> Option.ofObj
            |> Option.exists (fun name -> Regex.IsMatch(name, "^[0-9A-F]{24}$")))
        |> Seq.toList

    match segments with
    | [] -> failtest "Physical base backup retained no testable WAL segment"
    | first :: _ ->
        let displaced = Path.Combine(scratch, "removed-wal-segment")
        File.Move(first, displaced)

        try
            let program =
                if File.Exists("/opt/homebrew/opt/libpq/bin/pg_verifybackup") then
                    "/opt/homebrew/opt/libpq/bin/pg_verifybackup"
                else
                    "pg_verifybackup"

            let code, _, _ = run program [ Path.Combine(scratch, "primary") ]
            Expect.notEqual code 0 "Missing required WAL refuses pg_verifybackup"
        finally
            File.Move(displaced, first)

let private physicalPair owner app writer witness =
    withCapturedPrimary
        (fun capture ->
            physicalCopyProof run capture.ScratchRoot owner capture.Facts
            missingWalRefused capture.ScratchRoot
            newerTipRefusesOldPair app witness capture.Facts)
        owner
        app
        writer
        witness

let tests =
    testList
        "physical restored pair"
        [
            testCase
                "[CC-BACKUP-001] two physical PostgreSQL restores pass full audit and missing WAL refuses"
                (fun _ -> withAuthorityRuntimeDatabase physicalPair)
        ]
