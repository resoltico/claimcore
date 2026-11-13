module ClaimCore.Tests.CaseSubjectTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport

let private accepted value =
    Result.defaultWith (fun error -> failtestf "%A" error) value

let private today = DateOnly(2026, 11, 9)
let private instant = DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)
let private reference = CaseFixtures.reference "SUBJECT-1"

let private stamp number =
    {
        SubjectStamp.Position =
            EffectPosition.create (CaseFixtures.sequence (int64 number)) 0 |> Option.get
        OperationId = OperationIds.numberedAt instant number |> CaseFixtures.operationId
        ActorId = CaseFixtures.actorId (Guid.Parse "10000000-0000-4000-8000-000000000001")
        RecordedAt = instant
    }

let private command number (state: CaseSubjectState option) act =
    Case.admit
        today
        (state |> Option.bind _.Snapshot)
        {
            OperationId = OperationId.value (stamp number).OperationId
            CaseReference = CaseReference.format reference
            ExpectedRevision =
                state |> Option.map (_.Revision >> Revision.value) |> Option.defaultValue 0L
            Act = act
        }
    |> accepted

let private apply number (state: CaseSubjectState option) act =
    let command = command number state act

    CaseSubject.decide today state command
    |> accepted
    |> CaseSubject.apply (stamp number) state
    |> Option.get

let private paid () =
    let opened = apply 1 None (AuthoredCaseAct.Open Inputs.registration)
    let decided = apply 2 (Some opened) (AuthoredCaseAct.Decide Inputs.decision)
    apply 3 (Some decided) (AuthoredCaseAct.RecordPayment Inputs.payment)

[<Tests>]
let tests =
    testList
        "owned case subject"
        [
            testCase "removed rows retain their accepted content and position" (fun () ->
                let before = paid ()
                let payment = (stamp 3).OperationId

                let after =
                    apply
                        4
                        (Some before)
                        (AuthoredCaseAct.RemovePayment(OperationId.value payment))

                Expect.isEmpty
                    (CaseSubject.active after)
                    "Removed entries are absent from active state"

                let rows = CaseSubject.rows after |> Option.get
                Expect.equal rows.Payments.Length 1 "The retained row is not discarded"
                let row = List.exactlyOne rows.Payments

                Expect.equal
                    row.Position
                    (stamp 3).Position
                    "Removal preserves accepting provenance"

                match row.Content with
                | PaymentProjectionContent.Available(entry, true) ->
                    Expect.equal
                        (Amount.value (PaymentEntry.amount entry))
                        750M
                        "Accepted amount remains available until erasure"
                | _ -> failtest "Expected a retained removed payment")
            testCase
                "restore refuses a payment amount that disagrees with the case aggregate"
                (fun () ->
                    let before = paid ()
                    let row = before.Payments |> Map.values |> Seq.exactlyOne

                    let forged =
                        CaseFixtures.paymentEntry
                            (OperationId.value row.PaymentId)
                            (Inputs.paymentWith [ BusinessField.PaidAmount, "2" ] Inputs.payment)

                    let payments =
                        Map.add
                            row.PaymentId
                            { row with
                                Content = PaymentProjectionContent.Available(forged, false)
                            }
                            before.Payments

                    Expect.isError
                        (CaseSubject.restore before.ChangedAt before.Snapshot.Value payments)
                        "The complete owned set must agree with its financial facts")
            testCase
                "erasure retains reference identity and never reopens it as an absent case"
                (fun () ->
                    let erased = apply 4 (Some(paid ())) (AuthoredCaseAct.Bare BareAct.Erase)
                    let rows = CaseSubject.rows erased |> Option.get

                    Expect.equal
                        rows.Snapshot.State
                        CaseState.Erased
                        "The retained row is a tombstone"

                    Expect.equal rows.Snapshot.Reference reference "Reference remains reserved"

                    Expect.equal
                        rows.Snapshot.Revision
                        (CaseFixtures.revision 4L)
                        "Revision remains retained"

                    Expect.isTrue
                        (rows.Payments
                         |> List.forall (fun row ->
                             match row.Content with
                             | PaymentProjectionContent.Cleared _ -> true
                             | _ -> false))
                        "No private payment content survives"

                    let opening =
                        command 5 (Some erased) (AuthoredCaseAct.Open Inputs.registration)

                    Expect.isError
                        (CaseSubject.decide today (Some erased) opening)
                        "Erased and never-created states are distinct")
            testCase
                "erased-content replay rejects a forged removal and projects only after erasure"
                (fun () ->
                    let caseId = CaseId.ofOperationId (stamp 1).OperationId

                    let effect number act removed =
                        {
                            CaseSubjectStructure.Stamp = stamp number
                            CaseId = caseId
                            Reference = if number = 1 then Some reference else None
                            Revision = CaseFixtures.revision (int64 number)
                            Act = act
                            RemovedPayment = removed
                        }

                    let opened =
                        CaseSubject.applyErased None (effect 1 CaseActKind.Open None) |> accepted

                    let decided =
                        CaseSubject.applyErased opened (effect 2 CaseActKind.Decide None)
                        |> accepted

                    let recorded =
                        CaseSubject.applyErased decided (effect 3 CaseActKind.RecordPayment None)
                        |> accepted

                    Expect.isNone
                        (recorded |> Option.bind CaseSubject.rows)
                        "Missing private facts are explicit, not fabricated"

                    Expect.isError
                        (CaseSubject.applyErased
                            recorded
                            (effect 4 CaseActKind.RemovePayment (Some (stamp 9).OperationId)))
                        "An unheld payment cannot be removed"

                    let removed =
                        CaseSubject.applyErased
                            recorded
                            (effect 4 CaseActKind.RemovePayment (Some (stamp 3).OperationId))
                        |> accepted

                    let erased =
                        CaseSubject.applyErased removed (effect 5 CaseActKind.Erase None)
                        |> accepted

                    let rows = erased |> Option.bind CaseSubject.rows |> Option.get

                    Expect.equal
                        rows.Snapshot.State
                        CaseState.Erased
                        "Authorized erasure completes the structural reconstruction"

                    let row = List.exactlyOne rows.Payments

                    Expect.equal
                        row.Content
                        (PaymentProjectionContent.Cleared true)
                        "Removal provenance survives without private values"

                    Expect.equal row.Position (stamp 3).Position "Creation position remains exact")
        ]
