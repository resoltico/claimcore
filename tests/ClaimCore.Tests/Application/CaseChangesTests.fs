module ClaimCore.Tests.CaseChangesTests

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.TestSupport

let private identity = Guid.Parse "90000000-0000-4000-8000-000000000001"

let private fields: CaseFields =
    {
        Work = CaseWork.empty
        CaseReference = CaseFixtures.reference "CORE-HISTORY"
        Status = CaseStatus.Opened
        Registration = Inputs.admittedRegistration Inputs.registration
        Progress = PaymentProgress.Undecided
    }

let private receipt revision disclosure : OperationReceipt =
    {
        OperationId = CaseFixtures.operationId identity
        Position =
            LedgerPosition.create
                Sequence.first
                (EntryHash.ofBytes (Array.zeroCreate 32) |> Option.get)
        Subject =
            ReceiptSubject.Case
                {
                    CaseId = CaseFixtures.caseId identity
                    Reference = fields.CaseReference
                    Revision = CaseFixtures.revision revision
                    Act =
                        if revision = 1L then
                            CaseActKind.Open
                        else
                            CaseActKind.AmendRegistration
                    Snapshot = disclosure
                }
        RecordedAt = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = CaseFixtures.actorId identity
    }

let private live name =
    let registration =
        Registration.values fields.Registration
        |> List.map (fun (key, value) ->
            key,
            if key = BusinessField.ClaimantName then
                Admitted.Name(CaseFixtures.name name)
            else
                value)
        |> Registration.create
        |> Result.defaultWith failtest

    LiveCase.Active
        { fields with
            Registration = registration
        }

[<Tests>]
let tests =
    testList
        "core disclosed history comparisons"
        [
            testCase "compares independently supplied adjacent disclosed facts" (fun () ->
                let before = receipt 1L (SnapshotDisclosure.Disclosed(live "Before"))
                let after = receipt 2L (SnapshotDisclosure.Disclosed(live "After"))

                Expect.equal
                    (CaseChanges.history (Some before) after)
                    (Ok(
                        Some
                            {
                                Lifecycle = None
                                Fields =
                                    [
                                        {
                                            FieldName = BusinessField.ClaimantName
                                            Before = Some "Before"
                                            After = Some "After"
                                        }
                                    ]
                            }
                    ))
                    "Independent semantic values and unchanged-field preservation")
            testCase "disclosure cannot be replaced by a fabricated removal comparison" (fun () ->
                for hidden in [ SnapshotDisclosure.Withheld; SnapshotDisclosure.Erased ] do
                    let visible = SnapshotDisclosure.Disclosed(live "Sensitive synthetic field")

                    Expect.equal
                        (CaseChanges.history (Some(receipt 1L hidden)) (receipt 2L visible))
                        (Ok None)
                        "Unavailable prior values remain unavailable"

                    Expect.equal
                        (CaseChanges.history (Some(receipt 1L visible)) (receipt 2L hidden))
                        (Ok None)
                        "Hidden after state reveals no prior values")
            testCase "a missing or nonadjacent predecessor is an integrity finding" (fun () ->
                let shown = SnapshotDisclosure.Disclosed(live "Shown")

                Expect.isOk
                    (CaseChanges.history None (receipt 1L shown))
                    "Opening positive control"

                Expect.equal
                    (CaseChanges.history None (receipt 2L shown))
                    (Error CoreFault.StoreIntegrityError)
                    "A missing accepted predecessor cannot qualify comparison"

                Expect.equal
                    (CaseChanges.history (Some(receipt 1L shown)) (receipt 3L shown))
                    (Error CoreFault.StoreIntegrityError)
                    "A gap cannot be compared"

                let foreignSubject =
                    match (receipt 1L shown).Subject with
                    | ReceiptSubject.Case value ->
                        ReceiptSubject.Case
                            { value with
                                CaseId =
                                    CaseFixtures.caseId (
                                        Guid.Parse "90000000-0000-4000-8000-000000000099"
                                    )
                            }
                    | _ -> failtest "Case fixture"

                let foreign =
                    { (receipt 1L shown) with
                        Subject = foreignSubject
                    }

                Expect.equal
                    (CaseChanges.history (Some foreign) (receipt 2L shown))
                    (Error CoreFault.StoreIntegrityError)
                    "Foreign case data cannot enter differences")
        ]
