module ClaimCore.Tests.SignedRecoveryImportTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.SignedRecoveryTestSupport

let private preservesOriginalPreparer () =
    let request =
        {
            OperationId = Guid.Parse("20000000-0000-4000-8000-000000000951")
            CaseReference = "SIGNED-IMPORT-001"
            ExpectedVersion = 0L
            Command = Command.Open registration
        }

    let source, digest = SignedRecoveryTestSupport.source request
    let recovery = new CoreRecoveryStore.Store()
    let store = recovery :> IRecoveryStore

    let retain () =
        RecoveryImports.retainEnvelope store authority importer source digest CancellationToken.None
        |> fun pending -> pending.Result

    match retain () with
    | RecoveryImportRetainOutcome.RetainedPreparation _ -> ()
    | _ -> failtest "A current signed artifact must retain once"

    let retained =
        match store.Get(request.OperationId, CancellationToken.None).Result with
        | Ok(Some(RecoveryStoredOperation.Retained(value, _))) -> value
        | _ -> failtest "Signed retained preparation is required"

    let verified =
        verify source
        |> Result.defaultWith (fun _ -> failtest "Signed artifact must verify")

    Expect.equal retained.CaseId verified.CaseId "Signed reserved case ID is retained"
    Expect.equal retained.PreparerActorId verified.PreparerActorId "Original preparer is preserved"

    Expect.equal
        retained.PreparerGrantRevision
        verified.PreparerGrantRevision
        "Original grant revision is preserved"

    Expect.equal retained.ImporterActorId (Some importer.ActorId) "Current importer is separate"
    Expect.notEqual retained.PreparerActorId importer.ActorId "Importer does not become preparer"

    match retain () with
    | RecoveryImportRetainOutcome.ExistingPreparation _ -> ()
    | _ -> failtest "Exact signed retry must observe existing retention"

[<Tests>]
let tests =
    testList
        "signed recovery import attribution"
        [
            testCase
                "[CC-REC-001] signed import preserves A and records importer B"
                preservesOriginalPreparer
        ]
