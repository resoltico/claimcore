module ClaimCore.Tests.OperationIntentTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private valid result =
    Result.defaultWith (fun error -> failtestf "Intent fixture: %A" error) result

let private guid number =
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)

let private operation = OperationId.create (guid 2) |> Option.get
let private parent = CaseId.create (guid 1) |> Option.get
let private reference text = CaseReference.parse text |> valid

let private command amount =
    let decision =
        DecisionInput.create
            [
                BusinessField.PaymentDecisionDate, "2026-01-02"
                BusinessField.PayableAmount, amount
                BusinessField.PayableCurrency, "EUR"
            ]
        |> valid

    Case.parse
        {
            OperationId = guid 2
            CaseReference = "CASE-ONE"
            ExpectedRevision = 1L
            Act = CaseAct.Decide decision
        }
    |> valid

let private scope reference act =
    {
        CaseCommandSkeleton.CaseId = parent
        Reference = reference
        ExpectedRevision = Revision.first
        Act = act
        SelectedPayment = None
    }

[<Tests>]
let tests =
    testList
        "operation input identity"
        [
            testCase
                "case identity uses normalized input and resolved target rather than lookup spelling"
                (fun () ->
                    let first =
                        OperationIntent.case
                            operation
                            (scope (reference "Case-One") CaseActKind.Decide)
                            (command "10.0000")

                    let same =
                        OperationIntent.case
                            operation
                            (scope (reference "CASE-ONE") CaseActKind.Decide)
                            (command "10")

                    Expect.equal
                        first
                        same
                        "Equivalent decimals and reference spelling preserve input identity"

                    Expect.notEqual
                        first
                        (OperationIntent.case
                            operation
                            (scope (reference "CASE-ONE") CaseActKind.Decide)
                            (command "11"))
                        "Changed business input conflicts"

                    Expect.notEqual
                        first
                        (OperationIntent.case
                            operation
                            { (scope (reference "CASE-ONE") CaseActKind.Decide) with
                                CaseId = CaseId.create (guid 3) |> Option.get
                            }
                            (command "10"))
                        "Another resolved case cannot borrow identity")
            testCase "erasing a result does not erase a fully public close input" (fun () ->
                let registration =
                    RegistrationInput.create
                        [
                            BusinessField.IncidentDate, "2026-01-01"
                            BusinessField.IncidentNotificationDate, "2026-01-02"
                            BusinessField.IncidentCountry, "LV"
                            BusinessField.ClaimantName, "Synthetic claimant"
                            BusinessField.InsurerName, "Synthetic insurer"
                            BusinessField.ClaimedAmount, "100"
                            BusinessField.ClaimedCurrency, "EUR"
                        ]
                    |> valid

                let opening =
                    Case.parse
                        {
                            OperationId = guid 1
                            CaseReference = "CASE-ONE"
                            ExpectedRevision = 0L
                            Act = CaseAct.Open registration
                        }
                    |> valid

                let before =
                    Case.decideSnapshot
                        (DateOnly(2026, 11, 9))
                        CasePaymentContext.NoneRequired
                        opening
                        None
                    |> valid
                    |> _.After

                let close =
                    Case.parse
                        {
                            OperationId = guid 2
                            CaseReference = "CASE-ONE"
                            ExpectedRevision = 1L
                            Act = CaseAct.Bare BareAct.Close
                        }
                    |> valid

                let after =
                    Case.decideSnapshot
                        (DateOnly(2026, 11, 9))
                        CasePaymentContext.NoneRequired
                        close
                        (Some before)
                    |> valid

                let header =
                    {
                        OperationHeader.InstallationId = guid 9
                        Sequence = Sequence.create 2L |> Option.get
                        PreviousHash = EntryHash.zero
                        OperationId = operation
                        ActorId = ActorId.create (guid 9) |> Option.get
                        RecordedAt = DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)
                        BusinessDate = Some(DateOnly(2026, 11, 9))
                    }

                let publicInput = scope before.Reference CaseActKind.Close

                let value =
                    RetainedOperation.create
                        header
                        (CommandSkeleton.Case publicInput)
                        (OperationValues.Case { Command = close; Decision = after })
                        (fun () -> [| 0uy .. 31uy |])
                    |> valid

                let expected = Some(OperationIntent.case operation publicInput close)

                Expect.equal
                    (OperationIntent.accepted value)
                    expected
                    "Complete input compares without recording metadata"

                let erased =
                    OperationRoot.restore
                        header
                        (OperationRoot.commandBytes value.Root)
                        (OperationRoot.effectBytes value.Root)
                        (value.Root.Structure.Compartments |> List.map Compartment.withoutContent)
                        value.Root.Hash
                    |> valid
                    |> RetainedOperation.decode
                    |> valid

                Expect.equal
                    (OperationIntent.accepted erased)
                    expected
                    "Private result loss cannot turn a known close into unseen input"

                let otherHeader =
                    { header with
                        RecordedAt = header.RecordedAt.AddSeconds 1.
                        Sequence = Sequence.create 3L |> Option.get
                        PreviousHash = value.Root.Hash
                    }

                let recordedAgain =
                    RetainedOperation.create
                        otherHeader
                        (CommandSkeleton.Case publicInput)
                        (OperationValues.Case { Command = close; Decision = after })
                        (fun () -> [| 1uy .. 32uy |])
                    |> valid

                Expect.equal
                    (OperationIntent.accepted recordedAgain)
                    expected
                    "Time, sequence, previous hash and salt do not enter business identity"

                Expect.notEqual
                    recordedAgain.Root.Hash
                    value.Root.Hash
                    "Acceptance proof still binds recording metadata")
        ]
